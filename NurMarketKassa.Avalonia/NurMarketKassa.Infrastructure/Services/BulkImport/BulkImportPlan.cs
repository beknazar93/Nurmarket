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

    /// <summary>2026-10-30: «Только карточки и цены (без прихода)» — остаток не меняется. Включается сам, если в файле нет столбца количества.
    /// Обязательны только штрихкод (и название для новых товаров; для новых ещё цена продажи).</summary>
    public bool CardsOnly { get; set; }

    /// <summary>2026-10-30: строки с одинаковым названием и разными штрихкодами — один товар, лишние штрихкоды становятся дополнительными.
    /// По умолчанию выключено (разные штрихкоды при одном названии бывают у разных товаров).</summary>
    public bool MergeSameName { get; set; }
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

    /// <summary>2026-10-30: в эту строку сложены/объединены другие строки.</summary>
    public bool HasMerged => MergedFrom is { Count: > 0 };

    /// <summary>Строка файла как есть (для «Скачать строки с ошибками»).</summary>
    public string[] SourceCells { get; internal set; } = [];

    /// <summary>2026-10-30: доп. штрихкоды из файла (проверенные, без основного и без повторов).</summary>
    public IReadOnlyList<string> AltBarcodes => _alts;
    internal readonly List<string> _alts = [];

    /// <summary>2026-10-30: товар склада, к которому относится строка (null — новый товар).</summary>
    internal CatalogProductTileVm? Tile { get; set; }

    /// <summary>2026-10-30: полный список доп. штрихкодов для записи на сервер: уже имеющиеся у товара + новые из файла (без затирания и дублей).</summary>
    public IReadOnlyList<AlternateBarcodeVariant> AltFull { get; internal set; } = [];

    /// <summary>Сколько доп. штрихкодов из файла добавится товару (новых для него).</summary>
    public int AltAdded { get; internal set; }

    /// <summary>2026-10-30: строка объединена по названию как доп. штрихкод товара другой строки.</summary>
    public bool NameGroup { get; internal set; }

    /// <summary>2026-10-30: режим «только карточки»: у товара склада в файле нет ничего нового — строка пропускается.</summary>
    public bool Unchanged { get; internal set; }

    /// <summary>2026-10-30: причина, по которой строку не удалось загрузить на сервер (заполняет загрузчик); строка остаётся ошибкой и попадает в CSV.</summary>
    public string? LoadError { get; internal set; }

    /// <summary>2026-10-30: «Создан / обновлён / без изменений» после загрузки (для итога).</summary>
    public string? LoadOutcome { get; internal set; }

    /// <summary>2026-10-30: окно помечает строку, не принятую очередью приходов (причина — в CSV с ошибками).</summary>
    public void SetLoadError(string? error) => LoadError = string.IsNullOrWhiteSpace(error) ? "не загружено" : error;

    /// <summary>2026-10-30: ключ группы ошибки для группировки («15 412: укажите закупку»): без чисел и названий в кавычках.</summary>
    public string? ErrorKey => Error is null ? null : ErrorKeyRegex.Replace(Error, "…");

    private static readonly Regex ErrorKeyRegex = new(@"«[^»]*»|“[^”]*”|\d[\d.,]*", RegexOptions.Compiled);

    /// <summary>«Что будет»: «приход: остаток 40 → 100», «новый товар».</summary>
    public string Note
    {
        get
        {
            var text = Kind switch
            {
                BulkImportRowKind.Receipt or BulkImportRowKind.New when LoadOutcome == "created" => Tr.T("создан", "түзүлдү", "created", "oluşturuldu", "yaratildi"),
                BulkImportRowKind.Receipt or BulkImportRowKind.New when LoadOutcome == "updated" => Tr.T("обновлён", "жаңыртылды", "updated", "güncellendi", "yangilandi"),
                // 2026-10-30: режим «только карточки и цены» — остаток не меняется, пишем, что именно обновится.
                BulkImportRowKind.Receipt when CardsOnly => Unchanged
                    ? Tr.T("без изменений", "өзгөрүүсүз", "no changes", "değişiklik yok", "o'zgarishsiz")
                    : Tr.T("обновить: " + ChangeList(0), "жаңыртуу: " + ChangeList(1), "update: " + ChangeList(2), "güncelle: " + ChangeList(3), "yangilash: " + ChangeList(4)),
                BulkImportRowKind.New when CardsOnly => Tr.T("новый товар (без прихода)", "жаңы товар (кирешесиз)", "new product (no receipt)", "yeni ürün (giriş yok)", "yangi mahsulot (kirimsiz)"),
                BulkImportRowKind.Receipt when Line is { } l => Tr.T($"приход: остаток {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"кирешe: калдык {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"receipt: stock {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"giriş: stok {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}",
                    $"kirim: qoldiq {Num(l.StockBefore)} → {Num(l.StockBefore + l.Quantity)}"),
                BulkImportRowKind.New => Tr.T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot"),
                BulkImportRowKind.Merged when NameGroup => Tr.T($"объединено со строкой {MergedInto}: доп. штрихкод товара", $"{MergedInto}-сап менен бириктирилди: товардын кошумча штрих-коду",
                    $"merged with row {MergedInto}: extra barcode of that product", $"{MergedInto}. satırla birleştirildi: ürünün ek barkodu", $"{MergedInto}-qator bilan birlashtirildi: mahsulotning qo'shimcha shtrix-kodi"),
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

    internal bool CardsOnly { get; set; }
    internal bool ChPurchase { get; set; }
    internal bool ChSale { get; set; }

    private string ChangeList(int lang)
    {
        var parts = new List<string>();
        if (ChPurchase)
            parts.Add(lang switch { 0 => "закупка", 1 => "сатып алуу баасы", 2 => "purchase", 3 => "alış", _ => "xarid" });
        if (ChSale)
            parts.Add(lang switch { 0 => "цена продажи", 1 => "сатуу баасы", 2 => "sale price", 3 => "satış fiyatı", _ => "sotuv narxi" });
        if (AltAdded > 0)
            parts.Add(lang switch { 0 => $"доп. штрихкоды +{AltAdded}", 1 => $"кошумча штрих-коддор +{AltAdded}", 2 => $"extra barcodes +{AltAdded}", 3 => $"ek barkod +{AltAdded}", _ => $"qo'shimcha shtrix-kod +{AltAdded}" });
        return string.Join(", ", parts);
    }
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

    /// <summary>2026-10-30: режим «только карточки и цены» (без прихода).</summary>
    public bool CardsOnly { get; private set; }

    /// <summary>2026-10-30: в режиме карточек — товары склада, у которых в файле нет ничего нового (пропускаются).</summary>
    public int UnchangedCount { get; private set; }

    /// <summary>2026-10-30: строк с предупреждением.</summary>
    public int WarningCount => Rows.Count(r => !string.IsNullOrEmpty(r.Warning));

    /// <summary>2026-10-30: группы ошибок по типу: «15 412 · укажите закупку».</summary>
    public IReadOnlyList<(string Key, string Text, int Count)> ErrorGroups() =>
        Rows.Where(r => r.Kind == BulkImportRowKind.Error && r.ErrorKey is not null)
            .GroupBy(r => r.ErrorKey!)
            .Select(g => (g.Key, g.First().Error!, g.Count()))
            .OrderByDescending(g => g.Item3)
            .ToList();

    public TimeSpan Elapsed { get; private set; }

    /// <summary>У скольких товаров склада изменится цена продажи (для подтверждения «Цена продажи изменится у K товаров»).</summary>
    public int SalePriceChangeCount => ReceivingPricing.SalePriceChanges(Lines).Count;

    /// <summary>Каталог старше 10 минут (или не загружался) — перед сверкой его стоит обновить с сервера (CatalogCacheService.RefreshFromApiAsync).</summary>
    public static bool CatalogNeedsRefresh =>
        // 2026-10-30 (15 тыс. товаров): LastSyncTime — UTC; сравнение с местным DateTime.Now (UTC+6) давало «каталог старше 10 минут» ВСЕГДА,
        // и каждое «Проверить» качало весь каталог (155 страниц).
        CatalogCacheService.LastSyncTime is not { } last || DateTime.UtcNow - last > TimeSpan.FromMinutes(10);

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
        var cAlt = mapping.ColumnOf(ProductImportField.AltBarcodes);
        // 2026-10-30: файл без количества — законная загрузка «только карточки и цены»: остаток не меняется.
        var cardsOnly = settings.CardsOnly || cQty < 0;
        var cols = new Cols(cQty, cName, cPurchase, cSale, cUnit, cardsOnly);
        var byName = new Dictionary<string, BulkImportRow>(StringComparer.Ordinal);

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
            if (!TryNumber(row, ProductImportField.Quantity, cardsOnly ? -1 : cQty, cardsOnly ? "" : src[cQty], 1e9, out var quantity)
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

            // ---- 2026-10-30: доп. штрихкоды (несколько значений через ; , пробел | или перенос) — только цифры 4–14, без чужих
            var rowAlts = new List<string>();
            if (cAlt >= 0 && src[cAlt].Trim().Length > 0 && !TryParseAlts(row, src[cAlt], cAlt, barcode, rowAlts))
                continue;
            row.CardsOnly = cardsOnly;

            // ---- повтор штрихкода в файле
            var canon = Canon(barcode);
            if (seen.TryGetValue(canon, out var first))
            {
                if (settings.Duplicates == BulkImportDuplicateMode.SumQuantity && first.Line is { } target)
                {
                    if (!cardsOnly)
                        target.Quantity += quantity ?? 0;
                    // доп. штрихкоды повторной строки уходят к первой (без дублей и без чужих)
                    foreach (var extra in rowAlts)
                    {
                        if (!first._alts.Any(a => Canon(a) == Canon(extra)) && Canon(extra) != Canon(first.Barcode) && !seen.ContainsKey(Canon(extra)) && index.Find(extra) is null)
                        {
                            first._alts.Add(extra);
                            seen[Canon(extra)] = first;
                        }
                    }
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

            // ---- 2026-10-30: доп. штрихкод уже основной/доп. у ДРУГОГО товара (в каталоге или выше в файле) — ошибка строки
            var ownTile = index.Find(barcode);
            var altConflict = false;
            foreach (var alt in rowAlts)
            {
                if (index.Find(alt) is { } holder && (ownTile is null || !string.Equals(holder.Id, ownTile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Fail(row, Tr.T($"штрихкод {alt} уже у товара «{holder.Title}»", $"{alt} штрих-коду «{holder.Title}» товарында мурунтан бар",
                        $"barcode {alt} already belongs to “{holder.Title}”", $"barkod {alt} zaten «{holder.Title}» ürününde var", $"{alt} shtrix-kodi «{holder.Title}» mahsulotida allaqachon bor"),
                        ProductImportField.AltBarcodes, cAlt);
                    altConflict = true;
                    break;
                }
                if (seen.TryGetValue(Canon(alt), out var inFile))
                {
                    Fail(row, Tr.T($"доп. штрихкод {alt} уже в строке {inFile.FileRow} файла", $"кошумча штрих-код {alt} файлдын {inFile.FileRow}-сабында мурунтан бар",
                        $"extra barcode {alt} is already in row {inFile.FileRow} of the file", $"ek barkod {alt} dosyanın {inFile.FileRow}. satırında zaten var",
                        $"qo'shimcha shtrix-kod {alt} faylning {inFile.FileRow}-qatorida allaqachon bor"), ProductImportField.AltBarcodes, cAlt);
                    altConflict = true;
                    break;
                }
            }
            if (altConflict)
                continue;

            // ---- 2026-10-30: одинаковое название, другой штрихкод (штрихкода нет в каталоге) — один товар, штрихкод становится дополнительным
            var nameKey = settings.MergeSameName ? NameKey(row.Name) : "";
            if (nameKey.Length > 0 && ownTile is null && byName.TryGetValue(nameKey, out var head) && head.Line is { } headLine)
            {
                if (!cardsOnly)
                    headLine.Quantity += quantity ?? 0;
                if (!head._alts.Any(a => Canon(a) == canon))
                    head._alts.Add(barcode);
                seen[canon] = head;
                foreach (var extra in rowAlts)
                {
                    if (!head._alts.Any(a => Canon(a) == Canon(extra)))
                        head._alts.Add(extra);
                    seen[Canon(extra)] = head;
                }
                (head.MergedFrom ??= []).Add(row.FileRow);
                row.Kind = BulkImportRowKind.Merged;
                row.MergedInto = head.FileRow;
                row.NameGroup = true;
                continue;
            }

            // ---- существующий товар или новый
            ReceivingLineVm line;
            var weighed = false;
            string? unitProblem = null;
            if (ownTile is { } tile)
            {
                row.Tile = tile;
                // 2026-10-30: штрихкод файла — доп. штрихкод существующего товара: строка относится к ЭТОМУ товару (обновление/приход), а не создаёт новый.
                if (!string.IsNullOrWhiteSpace(tile.Barcode) && Canon(tile.Barcode) != canon)
                    row.Warning = Tr.T($"штрихкод файла — доп. штрихкод товара «{tile.Title}» (основной {tile.Barcode})", $"файлдагы штрих-код — «{tile.Title}» товарынын кошумча штрих-коду (негизги {tile.Barcode})",
                        $"the file barcode is an extra barcode of “{tile.Title}” (main {tile.Barcode})", $"dosyadaki barkod «{tile.Title}» ürününün ek barkodu (ana {tile.Barcode})",
                        $"fayldagi shtrix-kod «{tile.Title}» mahsulotining qo'shimcha shtrix-kodi (asosiy {tile.Barcode})");
                line = PurchaseReceivingService.FromTile(tile, cardsOnly ? 0 : quantity ?? 0);
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
                // 2026-10-30: правило проекта — штрихкод нового товара: только цифры, 4–14 знаков (как в карточке товара).
                if (!IsCardBarcode(barcode))
                {
                    Fail(row, Tr.T($"штрихкод «{barcode}»: для нового товара только цифры, 4–14 знаков", $"штрих-код «{barcode}»: жаңы товарга цифралар гана, 4–14 белги",
                        $"barcode “{barcode}”: a new product needs digits only, 4–14 characters", $"barkod «{barcode}»: yeni ürün için yalnızca rakamlar, 4–14 karakter",
                        $"shtrix-kod «{barcode}»: yangi mahsulot uchun faqat raqamlar, 4–14 belgi"), ProductImportField.Barcode, cBarcode);
                    continue;
                }
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
                    Quantity = cardsOnly ? 0 : quantity ?? 0,
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
            row._alts.AddRange(rowAlts);
            foreach (var alt in rowAlts)
                seen[Canon(alt)] = row;
            if (nameKey.Length > 0)
                byName[nameKey] = row;
            // Проверка строки — после цикла (Recompute): объединения по названию и повторы ещё меняют её доп. штрихкоды и количество.
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
            CardsOnly = cardsOnly,
        };
        plan.Recompute();
        watch.Stop();
        plan.Elapsed = watch.Elapsed;
        return plan;

    }

    /// <summary>2026-10-30: правило проекта для штрихкода нового товара и доп. штрихкодов: только цифры, 4–14 знаков.</summary>
    internal static bool IsCardBarcode(string code) => code.Length is >= 4 and <= 14 && code.All(char.IsDigit);

    private static readonly char[] AltSeparators = [';', ',', ' ', '|', '\n', '\r', '\t'];

    /// <summary>2026-10-30: «Название» → ключ для объединения: нижний регистр, ё = е, пробелы схлопнуты.</summary>
    internal static string NameKey(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    /// <summary>Разобрать ячейку «Доп. штрихкоды». false — строка помечена ошибкой (научная запись Excel, не цифры, не 4–14 знаков).</summary>
    private static bool TryParseAlts(BulkImportRow row, string raw, int column, string mainBarcode, List<string> alts)
    {
        foreach (var part in raw.Split(AltSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = part;
            if (ExponentBarcode.IsMatch(code))
            {
                Fail(row, Tr.T($"доп. штрихкод «{code}» испорчен Excel — сохраните столбец как текст и загрузите заново",
                    $"кошумча штрих-код «{code}» Excel тарабынан бузулган — мамычаны текст катары сактап, кайра жүктөңүз",
                    $"extra barcode “{code}” was mangled by Excel — save the column as text and load again",
                    $"ek barkod «{code}» Excel tarafından bozulmuş — sütunu metin olarak kaydedip yeniden yükleyin",
                    $"qo'shimcha shtrix-kod «{code}» Excel tomonidan buzilgan — ustunni matn sifatida saqlab, qayta yuklang"), ProductImportField.AltBarcodes, column);
                return false;
            }
            if (TrailingZeroDecimals.Match(code) is { Success: true } trimmed)
                code = trimmed.Groups[1].Value;
            code = ProductCodeLookup.Normalize(code);
            if (code.Length == 0)
                continue;
            if (!IsCardBarcode(code))
            {
                Fail(row, Tr.T($"доп. штрихкод «{code}»: только цифры, 4–14 знаков", $"кошумча штрих-код «{code}»: цифралар гана, 4–14 белги",
                    $"extra barcode “{code}”: digits only, 4–14 characters", $"ek barkod «{code}»: yalnızca rakamlar, 4–14 karakter", $"qo'shimcha shtrix-kod «{code}»: faqat raqamlar, 4–14 belgi"),
                    ProductImportField.AltBarcodes, column);
                return false;
            }
            if (Canon(code) == Canon(mainBarcode) || alts.Any(a => Canon(a) == Canon(code)))
                continue;
            alts.Add(code);
        }
        return true;
    }

    /// <summary>Доп. штрихкоды, которые у товара склада уже есть (с названиями и количествами упаковок — их не затираем).</summary>
    private static List<AlternateBarcodeVariant> ExistingAlts(CatalogProductTileVm tile)
    {
        var list = new List<AlternateBarcodeVariant>();
        if (tile.AlternateBarcodeVariants is { Count: > 0 } variants)
            list.AddRange(variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)));
        if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
        {
            foreach (var part in tile.AlternateBarcodesRaw.Split(['\n', '\r', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!list.Any(v => Canon(v.Barcode) == Canon(part)))
                    list.Add(new AlternateBarcodeVariant { Barcode = part });
            }
        }
        return list;
    }

    /// <summary>Слияние: уже имеющиеся доп. штрихкоды товара + новые из файла; без затирания, без дублей, без основного штрихкода.</summary>
    private static void FinalizeAlts(BulkImportRow row)
    {
        if (row._alts.Count == 0)
        {
            row.AltFull = [];
            row.AltAdded = 0;
            return;
        }
        var full = row.Tile is { } tile ? ExistingAlts(tile) : [];
        var mainCanon = Canon(row.Tile?.Barcode ?? row.Barcode);
        var added = 0;
        foreach (var code in row._alts)
        {
            var c = Canon(code);
            if (c == mainCanon || c == Canon(row.Barcode) || full.Any(v => Canon(v.Barcode) == c))
                continue;
            full.Add(new AlternateBarcodeVariant { Barcode = code, Name = "", Quantity = 0 });
            added++;
        }
        row.AltFull = full;
        row.AltAdded = added;
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
    private readonly record struct Cols(int Quantity, int Name, int Purchase, int Sale, int Unit, bool CardsOnly);

    private Cols _cols;

    /// <summary>Проверка строки теми же правилами, что в приёмке (<see cref="PurchaseReceivingService.Validate"/>), плюс «единица» и «дробное
    /// количество у штучной единицы». Тексты — «укажите цену/количество/единицу». true — строка годна; иначе Kind = Error, Error заполнен, а
    /// <see cref="BulkImportRow.Line"/> остаётся — человек может поправить число в таблице (<see cref="Recompute"/>).</summary>
    private static bool Check(BulkImportRow row, Cols cols)
    {
        var line = row.Line!;
        var isNew = row.BaseKind == BulkImportRowKind.New;
        // 2026-10-30: сервер не принял строку при загрузке — остаётся ошибкой с причиной (и попадает в «Скачать строки с ошибками»).
        if (row.LoadError is { Length: > 0 } loadError)
        {
            row.Error = loadError;
            row.Kind = BulkImportRowKind.Error;
            return false;
        }
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
        if (cols.CardsOnly)
        {
            // 2026-10-30: количество не нужно (остаток не меняется).
        }
        else if (line.Quantity <= 0)
            Add(Tr.T("укажите количество", "санын көрсөтүңүз", "enter the quantity", "miktarı girin", "miqdorni kiriting"), ProductImportField.Quantity, cols.Quantity);
        else if (UnitCatalog.QuantityProblem(line.Quantity, line.Unit, line.IsWeight || row.Weighed) is { } fractional)
            Add(fractional, ProductImportField.Quantity, cols.Quantity);
        if (isNew && string.IsNullOrWhiteSpace(line.ProductName))
            Add(Tr.T("укажите название", "атын көрсөтүңүз", "enter the name", "adı girin", "nomini kiriting"), ProductImportField.Name, cols.Name);
        if (line.PurchasePrice <= 0 && !cols.CardsOnly)
            Add(Tr.T("укажите закупку", "сатып алуу баасын көрсөтүңүз", "enter the purchase price", "alış fiyatını girin", "xarid narxini kiriting"), ProductImportField.Purchase, cols.Purchase);
        if (line.SalePrice <= 0 && (!cols.CardsOnly || isNew))
            Add(Tr.T("укажите цену продажи (или наценку)", "сатуу баасын (же үстөктү) көрсөтүңүз", "enter the sale price (or markup)",
                "satış fiyatını (veya kâr oranını) girin", "sotuv narxini (yoki ustamani) kiriting"), ProductImportField.Sale, cols.Sale);

        // Остальные правила приёмки (размеры, дубль нового штрихкода…) — без изменений.
        if (problems.Count == 0 && !cols.CardsOnly)
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
        // 2026-10-30: «только карточки» — что именно изменится у товара склада (если ничего, строка пропускается).
        row.CardsOnly = cols.CardsOnly;
        row.ChPurchase = cols.CardsOnly && !isNew && line.PurchasePrice > 0 && Math.Abs(line.PurchasePrice - line.OriginalPurchasePrice) > 0.004;
        row.ChSale = cols.CardsOnly && !isNew && line.SalePrice > 0 && Math.Abs(line.SalePrice - line.OriginalSalePrice) > 0.004;
        row.Unchanged = cols.CardsOnly && !isNew && problems.Count == 0 && !row.ChPurchase && !row.ChSale && row.AltAdded == 0;
        return problems.Count == 0;
    }

    /// <summary>Пересчёт после правок строк в таблице (количество, закупка, продажа, наценка, единица): заново проверяет строки, у которых есть
    /// строка прихода, собирает <see cref="Lines"/>/<see cref="FileRows"/> и счётчики. Ошибки разбора числа (строки без прихода) не трогает.</summary>
    public void Recompute()
    {
        var lines = new List<ReceivingLineVm>();
        var fileRows = new List<int>();
        int receipt = 0, created = 0, errors = 0, merged = 0, unchanged = 0;
        foreach (var row in Rows)
        {
            if (row.Line is not null && row.Kind != BulkImportRowKind.Merged)
            {
                FinalizeAlts(row);
                if (Check(row, _cols) && !row.Unchanged)
                {
                    lines.Add(row.Line);
                    fileRows.Add(row.FileRow);
                }
                if (row.Unchanged)
                    unchanged++;
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
        UnchangedCount = unchanged;
    }

    /// <summary>Строк, которые уйдут в приход (приход к товарам склада + новые).</summary>
    public int ReadyCount => ReceiptCount + NewCount - UnchangedCount;

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
