using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.GlobalProducts;

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

    /// <summary>2026-10-29: единица для строк, где ячейка «Единица» пуста (или столбца нет). null — столбец единицы есть, а ячейка пуста →
    /// ошибка строки «укажите единицу»; столбца нет вовсе → «шт» (как всегда).</summary>
    public string? DefaultUnit { get; set; }

    /// <summary>2026-10-29: строка без цены продажи и без наценки в файле получает цену по настройке автонаценки приёмки
    /// (<see cref="ReceivingMarkupSettingsStore"/>: наценка категории → общая) — та же функция, что в ручной приёмке. Выключено — «укажите цену».</summary>
    public bool UseReceivingMarkup { get; set; } = true;
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

    /// <summary>2026-10-29: чем строка была бы без ошибок проверки (приход / новый). Строки с ошибкой проверки (нет цены, количества, единицы) сохраняют
    /// <see cref="Line"/>, и окно позволяет поправить их прямо в таблице (<see cref="BulkImportPlan.Recompute"/>); ошибки разбора числа — только в файле.</summary>
    internal BulkImportRowKind BaseKind { get; set; }

    /// <summary>2026-10-29: единица в файле пуста или не узнана — пока человек не выбрал единицу, строка с ошибкой «укажите единицу».</summary>
    internal string? UnitProblem { get; set; }

    /// <summary>2026-10-29: у существующего товара весовой признак (дробное количество разрешено).</summary>
    internal bool Weighed { get; set; }

    /// <summary>Единица строки (для таблицы).</summary>
    public string Unit => Line?.Unit ?? "";

    /// <summary>Поставить единицу строке (вручную или «Установить единицу» над выбранными): нормализуется по справочнику, своя — как введена.
    /// У существующего товара единица карточки приходом не меняется — только у новых.</summary>
    public bool SetUnit(string? unit)
    {
        if (Line is null || BaseKind != BulkImportRowKind.New)
            return false;
        Line.Unit = UnitCatalog.NormalizeOrKeep(unit);
        UnitProblem = null;
        return true;
    }

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
        var cols = new Cols(cQty, cName, cPurchase, cSale, cUnit);

        var done = 0;
        var fromDirectoryCount = 0;
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
            var weighed = false;
            string? unitProblem = null;
            if (index.Find(barcode) is { } tile)
            {
                line = PurchaseReceivingService.FromTile(tile, quantity ?? 0);
                row.Kind = BulkImportRowKind.Receipt;
                weighed = tile.MustWeigh;
                if (purchase is > 0)
                    line.PurchasePrice = purchase.Value;
                if (sale is > 0)
                    line.SalePrice = sale.Value;
                else if (markup is { } mk && line.PurchasePrice > 0)
                    line.SalePrice = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, mk);
                else if (line.SalePrice <= 0 && line.PurchasePrice > 0 && settings.DefaultMarkupPercent is { } dm)
                    line.SalePrice = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, dm);
                // 2026-10-29: у товара склада цены продажи нет, в файле её тоже нет — считаем по автонаценке приёмки (та же функция, что в ручной приёмке).
                else if (line.SalePrice <= 0 && line.PurchasePrice > 0 && settings.UseReceivingMarkup
                         && ReceivingPricing.AutoSalePrice(line, ReceivingMarkupSettingsStore.Current) is { } autoSale)
                    line.SalePrice = autoSale;

                // 2026-10-29: единица в файле отличается от единицы товара склада — приходом единица не меняется, предупреждаем.
                if (cUnit >= 0 && UnitCatalog.Find(src[cUnit]) is { } fileUnit
                    && !string.Equals(UnitCatalog.NormalizeOrKeep(line.Unit), fileUnit.Storage, StringComparison.Ordinal))
                    row.Warning = AppendWarning(row.Warning, Tr.T($"единица в файле «{fileUnit.Display}», у товара «{UnitCatalog.Display(line.Unit)}» — остаётся «{UnitCatalog.Display(line.Unit)}»",
                        $"файлдагы бирдик «{fileUnit.Display}», товарда «{UnitCatalog.Display(line.Unit)}» — «{UnitCatalog.Display(line.Unit)}» калат",
                        $"unit in the file is “{fileUnit.Display}”, the product has “{UnitCatalog.Display(line.Unit)}” — it stays “{UnitCatalog.Display(line.Unit)}”",
                        $"dosyadaki birim «{fileUnit.Display}», üründe «{UnitCatalog.Display(line.Unit)}» — «{UnitCatalog.Display(line.Unit)}» kalır",
                        $"fayldagi birlik «{fileUnit.Display}», mahsulotda «{UnitCatalog.Display(line.Unit)}» — «{UnitCatalog.Display(line.Unit)}» qoladi"));
            }
            else
            {
                var salePrice = sale is > 0 ? sale.Value
                    : purchase is > 0 && markup is { } mk ? ReceivingPricing.SaleFromMarkup(purchase.Value, mk)
                    : purchase is > 0 && settings.DefaultMarkupPercent is { } dm ? ReceivingPricing.SaleFromMarkup(purchase.Value, dm)
                    : 0;
                // 2026-10-27: в файле есть штрихкод, а названия нет — берём из встроенного справочника готовых наименований NurCRM
                // (только локальная копия: на тысячи строк сервер не опрашиваем). Название из файла всегда главнее.
                if (name.Length == 0 && GlobalProductCatalog.Instance.Find(barcode) is { Name.Length: > 0 and <= 255 } fromDirectory)
                {
                    name = fromDirectory.Name;
                    row.Name = name;
                    fromDirectoryCount++;
                }

                // 2026-10-29: единица — по справочнику UnitCatalog (шт, кг, г, л, мл, м, уп…; ru/ky/uz/en/tr). Столбец есть, а ячейка пуста или
                // единица не узнана — ошибка «укажите единицу» (раньше молча «шт»), если не задана единица для пустых (BulkImportSettings.DefaultUnit).
                var unit = UnitCatalog.DefaultUnit;
                var unitRaw = cUnit >= 0 ? src[cUnit].Trim() : "";
                if (unitRaw.Length > 0)
                {
                    if (UnitCatalog.Normalize(unitRaw) is { } known)
                        unit = known;
                    else if (settings.DefaultUnit is { Length: > 0 } fallbackUnit)
                    {
                        unit = UnitCatalog.NormalizeOrKeep(fallbackUnit);
                        row.Warning = AppendWarning(row.Warning, UnknownUnitText(unitRaw) + Tr.T($" — поставлено «{UnitCatalog.Display(unit)}»", $" — «{UnitCatalog.Display(unit)}» коюлду",
                            $" — set to “{UnitCatalog.Display(unit)}”", $" — «{UnitCatalog.Display(unit)}» yapıldı", $" — «{UnitCatalog.Display(unit)}» qo'yildi"));
                    }
                    else
                        unitProblem = UnknownUnitText(unitRaw);
                }
                else if (settings.DefaultUnit is { Length: > 0 } defaultUnit)
                    unit = UnitCatalog.NormalizeOrKeep(defaultUnit);
                else if (cUnit >= 0)
                    unitProblem = MissingUnitText();

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
                // 2026-10-29: цены продажи нет ни в файле, ни как наценки — автонаценка приёмки (наценка категории → общая): та же функция, что в ручной приёмке.
                if (line.SalePrice <= 0 && line.PurchasePrice > 0 && settings.UseReceivingMarkup
                    && ReceivingPricing.AutoSalePrice(line, ReceivingMarkupSettingsStore.Current) is { } autoNew)
                    line.SalePrice = autoNew;
                row.Kind = BulkImportRowKind.New;
            }

            // ---- проверка теми же правилами, что в приёмке (+ единица и дробное количество)
            row.BaseKind = row.Kind;
            row.Weighed = weighed;
            row.UnitProblem = unitProblem;
            row.Line = line;
            seen[canon] = row; // 2026-10-29: и строка с ошибкой проверки: повтор её штрихкода складывается в неё, а не заводит второй товар
            if (Check(row, cols))
            {
                lines.Add(line);
                fileRows.Add(row.FileRow);
            }
        }

        progress?.Report(done);
        if (fromDirectoryCount > 0)
            PosLogger.Log($"Массовое добавление: названий взято из справочника NurCRM (в файле их не было): {fromDirectoryCount}.", "GLOBAL_PRODUCTS");
        var plan = new BulkImportPlan
        {
            Rows = rows,
            Headers = headers,
            Lines = lines,
            FileRows = fileRows,
            _cols = cols,
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

    /// <summary>Единица из файла → единица справочника (<see cref="UnitCatalog"/>: шт, кг, г, л, мл, м, уп, пара…). Пусто — «шт»; не узнали — «шт» с замечанием.
    /// 2026-10-29: словарь единиц теперь один (UnitCatalog), здесь осталась оболочка для накладных (InvoicePlan).</summary>
    internal static string NormalizeUnit(string raw, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(raw))
            return UnitCatalog.DefaultUnit;
        if (UnitCatalog.Normalize(raw) is { } unit)
            return unit;
        warning = Tr.T($"единица «{raw}» не распознана — поставлено «шт»", $"бирдик «{raw}» таанылган жок — «шт» коюлду", $"unit “{raw}” not recognized — set to “шт”",
            $"birim «{raw}» tanınmadı — «шт» yapıldı", $"birlik «{raw}» tanilmadi — «шт» qo'yildi");
        return UnitCatalog.DefaultUnit;
    }

    private static string AppendWarning(string? current, string add) => string.IsNullOrEmpty(current) ? add : current + "; " + add;

    private static string MissingUnitText() => Tr.T("укажите единицу (шт, кг, г, л, мл, м…)", "өлчөм бирдигин көрсөтүңүз (шт, кг, г, л, мл, м…)",
        "enter the unit (pcs, kg, g, l, ml, m…)", "birimi girin (adet, kg, g, l, ml, m…)", "birlikni kiriting (dona, kg, g, l, ml, m…)");

    private static string UnknownUnitText(string raw) => Tr.T($"единица «{raw}» не распознана — укажите шт, кг, г, л, мл, м…",
        $"бирдик «{raw}» таанылган жок — шт, кг, г, л, мл, м… көрсөтүңүз",
        $"unit “{raw}” is not recognized — use pcs, kg, g, l, ml, m…",
        $"birim «{raw}» tanınmadı — adet, kg, g, l, ml, m… yazın",
        $"birlik «{raw}» tanilmadi — dona, kg, g, l, ml, m… yozing");

    // ------------------------------------------------------------------ проверка строки и пересчёт после правок в таблице

    /// <summary>Столбцы файла, нужные проверке (для подсветки ячейки с ошибкой).</summary>
    private readonly record struct Cols(int Quantity, int Name, int Purchase, int Sale, int Unit);

    private Cols _cols;

    /// <summary>Проверка строки теми же правилами, что в приёмке (<see cref="PurchaseReceivingService.Validate"/>), плюс «единица» и «дробное
    /// количество у штучной единицы». Тексты — «укажите цену/количество/единицу». true — строка годна; иначе Kind = Error, Error заполнен, а
    /// <see cref="BulkImportRow.Line"/> остаётся — человек может поправить число в таблице (<see cref="Recompute"/>).</summary>
    private static bool Check(BulkImportRow row, Cols cols)
    {
        var line = row.Line!;
        var isNew = row.BaseKind == BulkImportRowKind.New;
        var problems = new List<string>();
        ProductImportField? field = null;
        var column = -1;
        void Add(string text, ProductImportField f, int c)
        {
            problems.Add(text);
            if (field is null)
            {
                field = f;
                column = c;
            }
        }

        if (isNew && row.UnitProblem is { Length: > 0 } unitProblem)
            Add(unitProblem, ProductImportField.Unit, cols.Unit);
        if (line.Quantity <= 0)
            Add(Tr.T("укажите количество", "санын көрсөтүңүз", "enter the quantity", "miktarı girin", "miqdorni kiriting"), ProductImportField.Quantity, cols.Quantity);
        else if (UnitCatalog.QuantityProblem(line.Quantity, line.Unit, line.IsWeight || row.Weighed) is { } fractional)
            Add(fractional, ProductImportField.Quantity, cols.Quantity);
        if (isNew && string.IsNullOrWhiteSpace(line.ProductName))
            Add(Tr.T("укажите название", "атын көрсөтүңүз", "enter the name", "adı girin", "nomini kiriting"), ProductImportField.Name, cols.Name);
        if (line.PurchasePrice <= 0)
            Add(Tr.T("укажите закупку", "сатып алуу баасын көрсөтүңүз", "enter the purchase price", "alış fiyatını girin", "xarid narxini kiriting"), ProductImportField.Purchase, cols.Purchase);
        if (line.SalePrice <= 0)
            Add(Tr.T("укажите цену продажи (или наценку)", "сатуу баасын (же үстөктү) көрсөтүңүз", "enter the sale price (or markup)",
                "satış fiyatını (veya kâr oranını) girin", "sotuv narxini (yoki ustamani) kiriting"), ProductImportField.Sale, cols.Sale);

        // Остальные правила приёмки (размеры, дубль нового штрихкода…) — без изменений.
        if (problems.Count == 0)
        {
            var saved = line.Source;
            if (isNew)
                line.Source = ReceivingSource.GlobalBase; // O(N)-поиск штрихкода в каталоге на 15 000 строк — минуты; словарь уже сказал, что кода нет
            var others = PurchaseReceivingService.Validate([line]);
            if (isNew)
                line.Source = ReceivingSource.Unknown;
            else
                line.Source = saved;
            if (others.Count > 0)
                Add(string.Join("; ", others), ProductImportField.Quantity, cols.Quantity);
        }
        else if (isNew)
            line.Source = ReceivingSource.Unknown;

        row.Error = problems.Count == 0 ? null : string.Join("; ", problems);
        row.ErrorField = field;
        row.ErrorColumn = column;
        row.Kind = problems.Count == 0 ? row.BaseKind : BulkImportRowKind.Error;
        return problems.Count == 0;
    }

    /// <summary>Пересчёт после правок строк в таблице (количество, закупка, продажа, наценка, единица): заново проверяет строки, у которых есть
    /// строка прихода, собирает <see cref="Lines"/>/<see cref="FileRows"/> и счётчики. Ошибки разбора числа (строки без прихода) не трогает.</summary>
    public void Recompute()
    {
        var lines = new List<ReceivingLineVm>();
        var fileRows = new List<int>();
        int receipt = 0, created = 0, errors = 0, merged = 0;
        foreach (var row in Rows)
        {
            if (row.Line is not null && row.Kind != BulkImportRowKind.Merged)
            {
                if (Check(row, _cols))
                {
                    lines.Add(row.Line);
                    fileRows.Add(row.FileRow);
                }
            }
            switch (row.Kind)
            {
                case BulkImportRowKind.Receipt: receipt++; break;
                case BulkImportRowKind.New: created++; break;
                case BulkImportRowKind.Merged: merged++; break;
                default: errors++; break;
            }
        }
        Lines = lines;
        FileRows = fileRows;
        ReceiptCount = receipt;
        NewCount = created;
        MergedCount = merged;
        ErrorCount = errors;
    }

    /// <summary>Строк, которые уйдут в приход (приход к товарам склада + новые).</summary>
    public int ReadyCount => ReceiptCount + NewCount;

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
