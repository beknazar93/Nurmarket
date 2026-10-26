using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (М3): план импорта — строки файла → строки прихода ReceivingLineVm с ошибками по номерам строк
// файла. Своей логики денег нет: цены и остатки считают ReceivingLineVm / ReceivingPricing, проверку делает PurchaseReceivingService.Validate,
// проводит приход очередь (BulkReceivingFeeder, М1). Сверка с каталогом идёт по словарю «штрихкод → товар», построенному один раз.
// Построение — не в потоке окна: BuildAsync, ход через IProgress.

/// <summary>Что делать с повторами штрихкода в файле (В-7: по умолчанию складывать количество).</summary>
public enum BulkImportDuplicateMode
{
    SumQuantity,
    FirstRow,
}

public sealed class BulkImportSettings
{
    /// <summary>Наценка для строк без цены продажи, % (В-5: по умолчанию пусто).</summary>
    public double? DefaultMarkupPercent { get; set; }

    public BulkImportDuplicateMode Duplicates { get; set; } = BulkImportDuplicateMode.SumQuantity;
}

public enum BulkImportRowKind
{
    /// <summary>Товар уже есть на складе — приход к нему.</summary>
    Receipt,

    /// <summary>Товара нет — будет создан.</summary>
    New,

    /// <summary>Строку загрузить нельзя (см. <see cref="BulkImportRow.Error"/>).</summary>
    Error,

    /// <summary>Повтор штрихкода: количество сложено со строкой <see cref="BulkImportRow.MergedInto"/>.</summary>
    Merged,
}

public sealed class BulkImportRow
{
    public int FileRow { get; init; }
    public BulkImportRowKind Kind { get; internal set; }
    public string Barcode { get; internal set; } = "";
    public string Name { get; internal set; } = "";

    /// <summary>Строка прихода (для Receipt и New).</summary>
    public ReceivingLineVm? Line { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>Поле, чья ячейка виновата (для подсветки ячейки в таблице), и её столбец в файле.</summary>
    public ProductImportField? ErrorField { get; internal set; }
    public int ErrorColumn { get; internal set; } = -1;

    public string? Warning { get; internal set; }
    public int? MergedInto { get; internal set; }
    internal List<int>? MergedFrom { get; set; }

    /// <summary>Строка файла как есть (для «Скачать строки с ошибками»).</summary>
    public string[] SourceCells { get; internal set; } = [];

    /// <summary>«Что будет»: «приход: остаток 40 → 100», «новый товар».</summary>
    public string Note
    {
        get
        {
            var text = Kind switch
            {
                BulkImportRowKind.Receipt when Line is { } l => Tr.T($"приход: остаток {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"кирешe: калдык {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"receipt: stock {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"giriş: stok {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"kirim: qoldiq {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}"),
                BulkImportRowKind.New => Tr.T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot"),
                BulkImportRowKind.Merged => Tr.T($"сложено со строкой {MergedInto}", $"{MergedInto}-сап менен кошулду", $"added to row {MergedInto}",
                    $"{MergedInto}. satırla birleştirildi", $"{MergedInto}-qator bilan qo'shildi"),
                _ => "",
            };
            if (MergedFrom is { Count: > 0 } from)
                text += Tr.T($" · сложено со строками {string.Join(", ", from)}", $" · {string.Join(", ", from)}-саптар кошулду",
                    $" · rows added: {string.Join(", ", from)}", $" · birleşen satırlar: {string.Join(", ", from)}", $" · qo'shilgan qatorlar: {string.Join(", ", from)}");
            return text;
        }
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>План: все строки файла с разметкой «приход / новый / ошибка» и готовые строки прихода для подающего.</summary>
public sealed class BulkImportPlan
{
    public IReadOnlyList<BulkImportRow> Rows { get; private init; } = [];

    /// <summary>Заголовки файла (для CSV с ошибками).</summary>
    public IReadOnlyList<string> Headers { get; private init; } = [];

    /// <summary>Если не сопоставлен обязательный столбец — строк нет, а здесь сказано, какой.</summary>
    public string? Problem { get; private init; }

    public int ReceiptCount { get; private set; }
    public int NewCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int MergedCount { get; private set; }

    /// <summary>Строки прихода в порядке файла (Receipt и New) — их можно отдавать BulkReceivingFeeder.Start.</summary>
    public IReadOnlyList<ReceivingLineVm> Lines { get; private set; } = [];

    /// <summary>Номера строк файла для <see cref="Lines"/> (по одному на строку).</summary>
    public IReadOnlyList<int> FileRows { get; private set; } = [];

    public TimeSpan Elapsed { get; private set; }

    /// <summary>У скольких товаров склада изменится цена продажи (для подтверждения «Цена продажи изменится у K товаров»).</summary>
    public int SalePriceChangeCount => ReceivingPricing.SalePriceChanges(Lines).Count;

    /// <summary>Каталог старше 10 минут (или не загружался) — перед сверкой его стоит обновить с сервера (CatalogCacheService.RefreshFromApiAsync).</summary>
    public static bool CatalogNeedsRefresh =>
        CatalogCacheService.LastSyncTime is not { } last || DateTime.Now - last > TimeSpan.FromMinutes(10);

    // ------------------------------------------------------------------ построение

    public static Task<BulkImportPlan> BuildAsync(ProductFileTable table, ProductImportMapping mapping, BulkImportSettings settings,
        IReadOnlyList<CatalogProductTileVm>? catalog = null, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Build(table, mapping, settings, catalog, progress, ct), ct);

    public static BulkImportPlan Build(ProductFileTable table, ProductImportMapping mapping, BulkImportSettings settings,
        IReadOnlyList<CatalogProductTileVm>? catalog = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var headers = table.Headers;

        if (!mapping.IsComplete)
        {
            var missing = string.Join(", ", mapping.MissingRequired.Select(ProductImportMapping.FieldLabel));
            return new BulkImportPlan
            {
                Headers = headers,
                Problem = Tr.T($"Не сопоставлен обязательный столбец: {missing}.", $"Милдеттүү мамыче шайкеш келген жок: {missing}.",
                    $"A required column is not mapped: {missing}.", $"Zorunlu sütun eşleştirilmedi: {missing}.", $"Majburiy ustun moslashtirilmadi: {missing}."),
            };
        }

        var index = new BarcodeIndex(catalog ?? SnapshotCatalog());
        var rows = new List<BulkImportRow>(table.DataRowCount);
        var seen = new Dictionary<string, BulkImportRow>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<ReceivingLineVm>();
        var fileRows = new List<int>();
        var cBarcode = mapping.ColumnOf(ProductImportField.Barcode);
        var cName = mapping.ColumnOf(ProductImportField.Name);
        var cQty = mapping.ColumnOf(ProductImportField.Quantity);
        var cPurchase = mapping.ColumnOf(ProductImportField.Purchase);
        var cSale = mapping.ColumnOf(ProductImportField.Sale);
        var cMarkup = mapping.ColumnOf(ProductImportField.Markup);
        var cCategory = mapping.ColumnOf(ProductImportField.Category);
        var cUnit = mapping.ColumnOf(ProductImportField.Unit);
        var cWeight = mapping.ColumnOf(ProductImportField.Weight);

        var done = 0;
        foreach (var src in table.DataRows)
        {
            if ((done & 255) == 0)
                ct.ThrowIfCancellationRequested();
            if ((done % 500) == 0)
                progress?.Report(done);
            done++;

            var row = new BulkImportRow { FileRow = src.FileRow, SourceCells = src.Cells, Name = src[cName].Trim() };
            rows.Add(row);

            // ---- штрихкод
            var rawBarcode = src[cBarcode];
            if (rawBarcode.Length == 0)
            {
                Fail(row, Tr.T("нет штрихкода", "штрих-код жок", "no barcode", "barkod yok", "shtrix-kod yo'q"), ProductImportField.Barcode, cBarcode);
                continue;
            }
            if (ExponentBarcode.IsMatch(rawBarcode))
            {
                Fail(row, Tr.T($"штрихкод «{rawBarcode}» испорчен Excel — сохраните столбец штрихкода как текст и загрузите заново",
                    $"штрих-код «{rawBarcode}» Excel тарабынан бузулган — штрих-код мамычасын текст катары сактап, кайра жүктөңүз",
                    $"barcode “{rawBarcode}” was mangled by Excel — save the barcode column as text and load again",
                    $"barkod «{rawBarcode}» Excel tarafından bozulmuş — barkod sütununu metin olarak kaydedip yeniden yükleyin",
                    $"shtrix-kod «{rawBarcode}» Excel tomonidan buzilgan — shtrix-kod ustunini matn sifatida saqlab, qayta yuklang"), ProductImportField.Barcode, cBarcode);
                continue;
            }
            if (TrailingZeroDecimals.Match(rawBarcode) is { Success: true } trimmed)
                rawBarcode = trimmed.Groups[1].Value;
            var barcode = ProductCodeLookup.Normalize(rawBarcode);
            row.Barcode = barcode;
            if (barcode.Length == 0)
            {
                Fail(row, Tr.T("нет штрихкода", "штрих-код жок", "no barcode", "barkod yok", "shtrix-kod yo'q"), ProductImportField.Barcode, cBarcode);
                continue;
            }
            if (barcode.Length > 64)
            {
                Fail(row, Tr.T("штрихкод длиннее 64 символов", "штрих-код 64 белгиден узун", "barcode is longer than 64 characters",
                    "barkod 64 karakterden uzun", "shtrix-kod 64 belgidan uzun"), ProductImportField.Barcode, cBarcode);
                continue;
            }
            if (src.IsNumeric(cBarcode) && barcode.Length >= 16 && barcode.All(char.IsDigit))
                row.Warning = Tr.T("Excel мог обрезать цифры штрихкода — сохраните столбец как текст",
                    "Excel штрих-коддун сандарын кыскартып салышы мүмкүн — мамычаны текст катары сактаңыз",
                    "Excel may have cut the barcode digits — save the column as text",
                    "Excel barkod rakamlarını kesmiş olabilir — sütunu metin olarak kaydedin",
                    "Excel shtrix-kod raqamlarini qisqartirgan bo'lishi mumkin — ustunni matn sifatida saqlang");

            // ---- числа
            if (!TryNumber(row, ProductImportField.Quantity, cQty, src[cQty], 1e9, out var quantity)
                || !TryNumber(row, ProductImportField.Purchase, cPurchase, cPurchase < 0 ? "" : src[cPurchase], 1e8, out var purchase)
                || !TryNumber(row, ProductImportField.Sale, cSale, cSale < 0 ? "" : src[cSale], 1e8, out var sale)
                || !TryNumber(row, ProductImportField.Markup, cMarkup, cMarkup < 0 ? "" : src[cMarkup], 1e6, out var markup))
                continue;

            var name = row.Name;
            if (name.Length > 255)
            {
                Fail(row, Tr.T($"название длиннее 255 символов ({name.Length})", $"аталышы 255 белгиден узун ({name.Length})", $"name is longer than 255 characters ({name.Length})",
                    $"ad 255 karakterden uzun ({name.Length})", $"nomi 255 belgidan uzun ({name.Length})"), ProductImportField.Name, cName);
                continue;
            }

            // ---- повтор штрихкода в файле
            var canon = Canon(barcode);
            if (seen.TryGetValue(canon, out var first))
            {
                if (settings.Duplicates == BulkImportDuplicateMode.SumQuantity && first.Line is { } target)
                {
                    target.Quantity += quantity ?? 0;
                    (first.MergedFrom ??= []).Add(row.FileRow);
                    row.Kind = BulkImportRowKind.Merged;
                    row.MergedInto = first.FileRow;
                    if ((purchase is > 0 && Math.Abs(purchase.Value - target.PurchasePrice) > 0.004) || (sale is > 0 && Math.Abs(sale.Value - target.SalePrice) > 0.004))
                        row.Warning = Tr.T($"цена отличается — взята из строки {first.FileRow}", $"баасы айырмаланат — {first.FileRow}-саптан алынды",
                            $"price differs — taken from row {first.FileRow}", $"fiyat farklı — {first.FileRow}. satırdan alındı", $"narx farq qiladi — {first.FileRow}-qatordan olindi");
                    continue;
                }

                Fail(row, Tr.T($"штрихкод уже есть в строке {first.FileRow} — пропускается", $"штрих-код {first.FileRow}-сапта мурунтан бар — өткөрүлүп жиберилет",
                    $"barcode is already in row {first.FileRow} — skipped", $"barkod zaten {first.FileRow}. satırda var — atlandı",
                    $"shtrix-kod {first.FileRow}-qatorda allaqachon bor — o'tkazib yuboriladi"), ProductImportField.Barcode, cBarcode);
                continue;
            }

            // ---- существующий товар или новый
            ReceivingLineVm line;
            if (index.Find(barcode) is { } tile)
            {
                line = PurchaseReceivingService.FromTile(tile, quantity ?? 0);
                row.Kind = BulkImportRowKind.Receipt;
                if (purchase is > 0)
                    line.PurchasePrice = purchase.Value;
                if (sale is > 0)
                    line.SalePrice = sale.Value;
                else if (markup is { } mk && line.PurchasePrice > 0)
                    line.SalePrice = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, mk);
                else if (line.SalePrice <= 0 && line.PurchasePrice > 0 && settings.DefaultMarkupPercent is { } dm)
                    line.SalePrice = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, dm);
            }
            else
            {
                var salePrice = sale is > 0 ? sale.Value
                    : purchase is > 0 && markup is { } mk ? ReceivingPricing.SaleFromMarkup(purchase.Value, mk)
                    : purchase is > 0 && settings.DefaultMarkupPercent is { } dm ? ReceivingPricing.SaleFromMarkup(purchase.Value, dm)
                    : 0;
                string? unitWarning = null;
                var unit = cUnit >= 0 ? NormalizeUnit(src[cUnit], out unitWarning) : "шт";
                if (unitWarning is not null)
                    row.Warning = string.IsNullOrEmpty(row.Warning) ? unitWarning : row.Warning + "; " + unitWarning;
                line = new ReceivingLineVm
                {
                    Barcode = barcode,
                    ProductName = name,
                    Unit = unit,
                    // На время проверки — «из базы CRM»: Validate для Unknown-строки ищет штрихкод в каталоге перебором (O(N) на строку,
                    // на 15 000 строк это минуты), а здесь словарь уже сказал, что такого штрихкода в каталоге нет.
                    Source = ReceivingSource.GlobalBase,
                    Quantity = quantity ?? 0,
                    PurchasePrice = purchase ?? 0,
                    SalePrice = salePrice,
                    CategoryName = cCategory >= 0 && src[cCategory].Length > 0 ? src[cCategory] : null,
                    IsWeight = cWeight >= 0 && ParseBool(src[cWeight]),
                };
                row.Kind = BulkImportRowKind.New;
            }

            // ---- проверка теми же правилами, что в приёмке
            var problems = PurchaseReceivingService.Validate([line]);
            if (row.Kind == BulkImportRowKind.New)
                line.Source = ReceivingSource.Unknown;
            if (problems.Count > 0)
            {
                var (field, column) = line.Quantity <= 0 ? (ProductImportField.Quantity, cQty)
                    : string.IsNullOrWhiteSpace(line.ProductName) ? (ProductImportField.Name, cName)
                    : line.PurchasePrice <= 0 ? (ProductImportField.Purchase, cPurchase)
                    : (ProductImportField.Sale, cSale);
                row.Line = null;
                Fail(row, string.Join("; ", problems), field, column);
                continue;
            }

            row.Line = line;
            seen[canon] = row;
            lines.Add(line);
            fileRows.Add(row.FileRow);
        }

        progress?.Report(done);
        var plan = new BulkImportPlan
        {
            Rows = rows,
            Headers = headers,
            Lines = lines,
            FileRows = fileRows,
        };
        foreach (var r in rows)
        {
            switch (r.Kind)
            {
                case BulkImportRowKind.Receipt: plan.ReceiptCount++; break;
                case BulkImportRowKind.New: plan.NewCount++; break;
                case BulkImportRowKind.Merged: plan.MergedCount++; break;
                default: plan.ErrorCount++; break;
            }
        }
        watch.Stop();
        plan.Elapsed = watch.Elapsed;
        return plan;

    }

    private static void Fail(BulkImportRow row, string error, ProductImportField? field, int column)
    {
        row.Kind = BulkImportRowKind.Error;
        row.Error = error;
        row.ErrorField = field;
        row.ErrorColumn = column;
    }

    /// <summary>Число из ячейки. Пустая ячейка или нет столбца — null (не ошибка). Ошибка формата — строка помечается и false.</summary>
    private static bool TryNumber(BulkImportRow row, ProductImportField field, int column, string cell, double limit, out double? value)
    {
        value = null;
        if (column < 0)
            return true;
        cell = cell.Trim();
        if (cell.EndsWith('%'))
            cell = cell[..^1].Trim();
        if (cell.EndsWith("сом", StringComparison.OrdinalIgnoreCase))
            cell = cell[..^3].Trim();
        if (cell.Length == 0)
            return true;

        var label = ProductImportMapping.FieldLabel(field).ToLowerInvariant();
        if (!ProductCsvImporter.TryParseNumber(cell, out var parsed))
        {
            Fail(row, Tr.T($"{label}: не число «{cell}»", $"{label}: сан эмес «{cell}»", $"{label}: not a number “{cell}”", $"{label}: sayı değil «{cell}»", $"{label}: son emas «{cell}»"), field, column);
            return false;
        }
        if (parsed < 0 && field != ProductImportField.Markup)
        {
            Fail(row, Tr.T($"{label}: отрицательное число", $"{label}: терс сан", $"{label}: negative number", $"{label}: negatif sayı", $"{label}: manfiy son"), field, column);
            return false;
        }
        if (Math.Abs(parsed) >= limit)
        {
            Fail(row, Tr.T($"{label}: слишком большое число", $"{label}: өтө чоң сан", $"{label}: number is too large", $"{label}: sayı çok büyük", $"{label}: son juda katta"), field, column);
            return false;
        }

        value = parsed;
        return true;
    }

    // ------------------------------------------------------------------ вспомогательное

    private static readonly Regex ExponentBarcode = new(@"^\d+([.,]\d+)?[eE][+-]?\d+$", RegexOptions.Compiled);
    private static readonly Regex TrailingZeroDecimals = new(@"^(\d{4,})[.,]0+$", RegexOptions.Compiled);

    private static CatalogProductTileVm[] SnapshotCatalog()
    {
        try
        {
            return CatalogCacheService.Products.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Одна запись кода для сравнения: UPC-A из 12 цифр = EAN-13 с нулём, GTIN-14 с нулём впереди = EAN-13.</summary>
    internal static string Canon(string code)
    {
        var key = code.Trim();
        if (key.Length > 0 && key.All(char.IsDigit))
        {
            if (key.Length == 12)
                return "0" + key;
            if (key.Length == 14 && key[0] == '0')
                return key[1..];
        }
        return key;
    }

    private static bool ParseBool(string s)
    {
        var v = s.Trim().ToLowerInvariant();
        return v is "да" or "true" or "1" or "yes" or "y" or "+" or "ооба" or "evet" or "ha" or "истина";
    }

    private static readonly Dictionary<string, string> UnitAliases = BuildUnitAliases();

    private static Dictionary<string, string> BuildUnitAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string unit, params string[] names)
        {
            foreach (var n in names)
                map[n] = unit;
        }
        Add("шт", "шт", "штука", "штуки", "штук", "pcs", "pc", "piece", "pieces", "adet", "дана", "dona", "ea");
        Add("кг", "кг", "килограмм", "килограмм", "килограмма", "kg", "kilogram", "kilo", "килограм");
        Add("л", "л", "литр", "литра", "литров", "l", "lt", "ltr", "litre", "liter", "литр");
        Add("м", "м", "метр", "метра", "метров", "m", "mt", "meter", "metre", "metr");
        Add("уп", "уп", "упак", "упаковка", "упаковки", "pack", "paket", "qadoq", "пачка", "pk");
        return map;
    }

    /// <summary>Единица из файла → одна из пяти, которые знает форма товара (шт, кг, л, м, уп). Пусто — «шт»; не узнали — «шт» с замечанием.</summary>
    internal static string NormalizeUnit(string raw, out string? warning)
    {
        warning = null;
        var key = ProductImportMapping.Key(raw);
        if (key.Length == 0)
            return "шт";
        if (UnitAliases.TryGetValue(key, out var unit))
            return unit;
        warning = Tr.T($"единица «{raw}» не распознана — поставлено «шт»", $"бирдик «{raw}» таанылган жок — «шт» коюлду", $"unit “{raw}” not recognized — set to “шт”",
            $"birim «{raw}» tanınmadı — «шт» yapıldı", $"birlik «{raw}» tanilmadi — «шт» qo'yildi");
        return "шт";
    }

    // ------------------------------------------------------------------ словарь каталога

    /// <summary>Штрихкод → товар каталога: основной и дополнительные штрихкоды, обе записи кода (12/13 цифр). Строится один раз.</summary>
    private sealed class BarcodeIndex
    {
        private readonly Dictionary<string, CatalogProductTileVm> _map;

        public BarcodeIndex(IReadOnlyList<CatalogProductTileVm> catalog)
        {
            _map = new Dictionary<string, CatalogProductTileVm>(catalog.Count * 2, StringComparer.OrdinalIgnoreCase);
            foreach (var tile in catalog)
            {
                Add(tile.Barcode, tile);
                if (tile.AlternateBarcodeVariants is { } variants)
                {
                    foreach (var v in variants)
                        Add(v.Barcode, tile);
                }
                if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
                {
                    foreach (var part in tile.AlternateBarcodesRaw.Split(['\n', '\r', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        Add(part, tile);
                }
            }
        }

        private void Add(string? code, CatalogProductTileVm tile)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;
            var key = Canon(code);
            // Дубль штрихкода в каталоге — как в ProductCodeLookup: товар с остатком важнее.
            if (!_map.TryGetValue(key, out var existing) || (existing.Quantity <= 0 && tile.Quantity > 0))
                _map[key] = tile;
        }

        public CatalogProductTileVm? Find(string code) => _map.TryGetValue(Canon(code), out var tile) ? tile : null;
    }

    // ------------------------------------------------------------------ строки с ошибками

    /// <summary>CSV («;», с кавычками) со строками-ошибками и столбцом «Ошибка»: исправить и загрузить снова. Длинные цифровые коды
    /// пишутся как ="…", чтобы Excel не превратил штрихкод в 4,6E+12. В файл сохранять с BOM (<see cref="ProductImportTemplate.CsvBytes"/>).</summary>
    public string BuildErrorsCsv()
    {
        var sb = new StringBuilder();
        var width = Math.Max(Headers.Count, Rows.Where(r => r.Kind == BulkImportRowKind.Error).Select(r => r.SourceCells.Length).DefaultIfEmpty(0).Max());
        var barcodeColumn = -1;
        for (var i = 0; i < Headers.Count; i++)
        {
            if (ProductImportMapping.AutoDetect([Headers[i]]).ColumnOf(ProductImportField.Barcode) == 0)
            {
                barcodeColumn = i;
                break;
            }
        }

        for (var i = 0; i < width; i++)
            sb.Append(CsvCell(i < Headers.Count ? Headers[i] : "")).Append(';');
        sb.Append(CsvCell(Tr.T("Строка в файле", "Файлдагы сап", "Row in file", "Dosyadaki satır", "Fayldagi qator"))).Append(';');
        sb.Append(CsvCell(Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"))).Append("\r\n");

        foreach (var row in Rows.Where(r => r.Kind == BulkImportRowKind.Error))
        {
            for (var i = 0; i < width; i++)
            {
                var cell = i < row.SourceCells.Length ? row.SourceCells[i] : "";
                if (i == barcodeColumn && cell.Length >= 10 && cell.All(char.IsDigit))
                    sb.Append("=\"").Append(cell).Append('"');
                else
                    sb.Append(CsvCell(cell));
                sb.Append(';');
            }
            sb.Append(row.FileRow.ToString(CultureInfo.InvariantCulture)).Append(';');
            sb.Append(CsvCell(row.Error ?? "")).Append("\r\n");
        }

        return sb.ToString();
    }

    internal static string CsvCell(string value) =>
        value.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
