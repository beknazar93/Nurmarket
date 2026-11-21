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

    /// <summary>2026-11-20 (мастер «Массовая загрузка», шаг «Проверьте»: «только добавить новые»): товары, которые уже есть на складе, пропускаются
    /// («пропущен: уже есть на складе»), создаются только новые. Остальная логика разбора не меняется.</summary>
    public bool NewOnly { get; set; }

    /// <summary>2026-11-20 («Добавлять доп. штрихкоды»): false — доп. штрихкоды и упаковки из файла НЕ добавляются товарам, которые уже есть на складе
    /// (новым товарам — всегда). По умолчанию включено (как работало с 30.10: файл без количества дописывает доп. коды существующим товарам).</summary>
    public bool AddAltBarcodesToExisting { get; set; } = true;

    /// <summary>2026-11-20: «Пропускать только плохой доп. код»: код занят другим товаром / не цифры / длиннее 14 / плохое «Штук в упаковке» — пропускается только
    /// этот код (с предупреждением), сам товар загружается. Выключено (по умолчанию) — строка с таким кодом целиком получает ошибку, как раньше.</summary>
    public bool SkipBadAltCodes { get; set; }

    /// <summary>2026-11-20 (CT MAX): служебному коду («*10», без цифр) нового товара дать внутренний штрихкод NurCRM (прежний код — в артикул). Выключено — такая строка получает обычную ошибку «только цифры, 4–14 знаков».
    /// Действует только для источника с флагом <see cref="ImportSourceProfile.ServiceCodes"/>.</summary>
    public bool GenerateInternalCodes { get; set; } = true;
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

/// <summary>2026-11-20: данные доп. штрихкода из файла: название варианта, штук в упаковке (0 — не задано), код пришёл из столбца «Штрихкод упаковки».</summary>
internal readonly record struct AltMeta(string Name, double Qty, bool Pack);

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

    /// <summary>2026-11-20: данные профиля импорта (прайс автозапчастей) из <see cref="ProductFileRow.Tag"/>; загрузчик карточек пишет их вместе с товаром.</summary>
    public object? Extra { get; internal set; }

    /// <summary>2026-10-30: доп. штрихкоды из файла (проверенные, без основного и без повторов).</summary>
    public IReadOnlyList<string> AltBarcodes => _alts;
    internal readonly List<string> _alts = [];

    /// <summary>2026-11-20: название варианта и штук в упаковке по доп. штрихкоду (ключ — <see cref="BulkImportPlan.Canon"/> кода). «Код:N» и «Штрихкод упаковки» + «Штук в упаковке».</summary>
    internal readonly Dictionary<string, AltMeta> _altMeta = new(StringComparer.Ordinal);

    /// <summary>2026-11-20: сколько доп. кодов этой строки пропущено (занят другим товаром, не цифры, плохое количество) при «Пропускать только плохой код».</summary>
    public int AltSkipped { get; internal set; }

    /// <summary>2026-11-20 (CT MAX): прежний служебный код, записанный в артикул нового товара (штрихкод — внутренний код NurCRM).</summary>
    public string? Article { get; internal set; }

    /// <summary>2026-11-20: бренд из файла (столбец «Бренд»): уходит в brand_name при создании нового товара.</summary>
    public string? Brand { get; internal set; }

    /// <summary>2026-11-20: штрихкод этой строки — внутренний код NurCRM, созданный взамен служебного.</summary>
    public bool InternalCode { get; internal set; }

    /// <summary>2026-11-20: сколько из добавляемых доп. кодов — упаковки (со «Штук в упаковке» больше нуля).</summary>
    public int AltPackAdded { get; internal set; }

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
    public void SetLoadError(string? error, bool needsCheck = false)
    {
        LoadError = string.IsNullOrWhiteSpace(error) ? "не загружено" : error;
        LoadNeedsCheck = needsCheck;
        if (needsCheck)
            LoadError = Tr.T("исход неизвестен — приход мог пройти: проверьте остаток товара на складе (строка не попадает в файл ошибок, чтобы не оприходовать её второй раз). ",
                "жыйынтык белгисиз — кирүү өтүп кетиши мүмкүн: товардын калдыгын кампадан текшериңиз (экинчи жолу кирүү болбошу үчүн сап ката файлына кирбейт). ",
                "outcome unknown — the receipt may have gone through: check the product stock in the warehouse (the row is not put into the error file so it is not received twice). ",
                "sonuç bilinmiyor — giriş geçmiş olabilir: ürün stokunu depoda kontrol edin (satır iki kez girilmesin diye hata dosyasına konmaz). ",
                "natija noma'lum — kirim o'tgan bo'lishi mumkin: mahsulot qoldig'ini ombordan tekshiring (ikki marta kirim bo'lmasligi uchun qator xato fayliga qo'shilmaydi). ") + LoadError;
    }

    /// <summary>2026-11-01 (контрольный аудит, С2-08): строка с неизвестным исходом (приход мог пройти) — в «Скачать строки с ошибками» НЕ кладётся.</summary>
    public bool LoadNeedsCheck { get; private set; }

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
                BulkImportRowKind.Receipt when NewOnly && Unchanged => Tr.T("пропущен: уже есть на складе", "өткөрүлдү: кампада бар", "skipped: already in stock", "atlandı: depoda zaten var", "o'tkazildi: omborda bor"),
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
            // 2026-11-20: сколько доп. кодов добавится (у карточек-обновлений это уже в списке «обновить: …»; пропущенные — в предупреждении)
            if (AltAdded > 0 && Kind is BulkImportRowKind.New or BulkImportRowKind.Receipt && !Unchanged && !(CardsOnly && Kind == BulkImportRowKind.Receipt))
                text += Tr.T($" · доп. кодов +{AltAdded}", $" · кошумча коддор +{AltAdded}", $" · extra codes +{AltAdded}", $" · ek kod +{AltAdded}", $" · qo'shimcha kod +{AltAdded}");
            if (MergedFrom is { Count: > 0 } from)
                text += Tr.T($" · сложено со строками {string.Join(", ", from)}", $" · {string.Join(", ", from)}-саптар кошулду",
                    $" · rows added: {string.Join(", ", from)}", $" · birleşen satırlar: {string.Join(", ", from)}", $" · qo'shilgan qatorlar: {string.Join(", ", from)}");
            return text;
        }
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal bool CardsOnly { get; set; }
    internal bool NewOnly { get; set; }
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

    /// <summary>2026-11-20: сколько доп. штрихкодов добавится (в загружаемых строках), из них упаковок (со «штук в упаковке»), и сколько кодов пропущено как плохие.</summary>
    public int AltCodeCount { get; private set; }
    public int AltPackCount { get; private set; }
    public int AltSkippedCount { get; private set; }

    /// <summary>2026-11-20 (CT MAX): строк-групп пропущено и служебных кодов, получивших внутренний штрихкод.</summary>
    public int GroupRowsSkipped { get; private set; }
    public int InternalCodeCount { get; private set; }

    /// <summary>2026-11-20: сколько категорий из файла (у новых товаров) ещё нет в каталоге — они создадутся при загрузке.</summary>
    public int NewCategoryCount { get; private set; }

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

        var catalogList = catalog ?? SnapshotCatalog();
        var index = new BarcodeIndex(catalogList);
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
        var cPack = mapping.ColumnOf(ProductImportField.PackBarcode); // 2026-11-20: «Штрихкод упаковки» + «Штук в упаковке» + «Название варианта»
        var cPackQty = mapping.ColumnOf(ProductImportField.PackQty);
        var cAltName = mapping.ColumnOf(ProductImportField.AltName);
        var cBrand = mapping.ColumnOf(ProductImportField.Brand); // 2026-11-20: бренд нового товара
        // 2026-10-30: файл без количества — законная загрузка «только карточки и цены»: остаток не меняется.
        var cardsOnly = settings.CardsOnly || cQty < 0;
        var cols = new Cols(cQty, cName, cPurchase, cSale, cUnit, cardsOnly, settings.NewOnly);
        var byName = new Dictionary<string, BulkImportRow>(StringComparer.Ordinal);

        var done = 0;
        var fromDirectoryCount = 0;
        // 2026-11-20 (CT MAX): профиль источника с пропуском строк-групп и служебными кодами
        var skipGroups = mapping.Source?.SkipGroupRows == true;
        var serviceCodes = mapping.Source?.ServiceCodes == true;
        var groupRows = 0;
        HashSet<string>? fileCodes = null;
        foreach (var src in table.DataRows)
        {
            if ((done & 255) == 0)
                ct.ThrowIfCancellationRequested();
            if ((done % 500) == 0)
                progress?.Report(done);
            done++;

            // 2026-11-20: строка-группа (бренд/раздел): нет штрихкода, цены и закупки (пусто или 0) — не товар, в план не входит
            if (skipGroups && src[cBarcode].Trim().Length == 0 && src[cName].Trim().Length > 0 && IsZeroOrEmpty(src[cSale]) && IsZeroOrEmpty(src[cPurchase]))
            {
                groupRows++;
                continue;
            }

            var row = new BulkImportRow { FileRow = src.FileRow, SourceCells = src.Cells, Name = src[cName].Trim(), Extra = src.Tag };
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
            // 2026-11-02 (аудит №2, С2-30): число из Excel без текстового формата теряет ведущий ноль — 7 цифр с верной контрольной цифрой EAN-8 после «0» или 11 цифр UPC-A после «0»:
            // не дописываем сами (код может быть чужим), а предупреждаем и просим сохранить столбец как текст.
            if (src.IsNumeric(cBarcode) && barcode.All(char.IsAsciiDigit) && barcode.Length is 7 or 11 && NurMarketKassa.Services.InvoiceImport.InvoiceText.HasValidCheckDigit("0" + barcode))
                row.Warning = Tr.T($"возможно, Excel срезал ноль в начале штрихкода: «{barcode}» → «0{barcode}»? Сохраните столбец штрихкода как текст и загрузите заново",
                    $"Excel штрих-коддун башындагы нөлдү кесип салган окшойт: «{barcode}» → «0{barcode}»? Штрих-код мамычасын текст катары сактап, кайра жүктөңүз",
                    $"Excel may have cut the leading zero of the barcode: “{barcode}” → “0{barcode}”? Save the barcode column as text and load again",
                    $"Excel barkodun başındaki sıfırı kesmiş olabilir: «{barcode}» → «0{barcode}»? Barkod sütununu metin olarak kaydedip yeniden yükleyin",
                    $"Excel shtrix-kod boshidagi nolni kesgan bo'lishi mumkin: «{barcode}» → «0{barcode}»? Shtrix-kod ustunini matn sifatida saqlab, qayta yuklang");
            if (src.IsNumeric(cBarcode) && barcode.Length >= 16 && barcode.All(char.IsDigit))
                row.Warning = Tr.T("Excel мог обрезать цифры штрихкода — сохраните столбец как текст",
                    "Excel штрих-коддун сандарын кыскартып салышы мүмкүн — мамычаны текст катары сактаңыз",
                    "Excel may have cut the barcode digits — save the column as text",
                    "Excel barkod rakamlarını kesmiş olabilir — sütunu metin olarak kaydedin",
                    "Excel shtrix-kod raqamlarini qisqartirgan bo'lishi mumkin — ustunni matn sifatida saqlang");

            // ---- числа
            if (!TryNumber(row, ProductImportField.Quantity, cardsOnly ? -1 : cQty, cardsOnly ? "" : src[cQty], 1e9, out var quantity)
                || !TryNumber(row, ProductImportField.Purchase, cPurchase, cPurchase < 0 ? "" : src[cPurchase], 1e10, out var purchase)
                || !TryNumber(row, ProductImportField.Sale, cSale, cSale < 0 ? "" : src[cSale], 1e8, out var sale)
                || !TryNumber(row, ProductImportField.Markup, cMarkup, cMarkup < 0 ? "" : src[cMarkup], 1e6, out var markup))
                continue;
            // 2026-11-11 (ответ Умара): пределы цен сервера — продажа 9 999 999,999, закупка 9 999 999 999,9999 (было 1e8 для обеих). Строка с ценой выше предела не загружается.
            if (sale is { } saleValue && ServerPriceLimits.SaleExceeds(saleValue))
            {
                Fail(row, ServerPriceLimits.SaleTooBigText, ProductImportField.Sale, cSale);
                continue;
            }
            if (purchase is { } purchaseValue && ServerPriceLimits.PurchaseExceeds(purchaseValue))
            {
                Fail(row, ServerPriceLimits.PurchaseTooBigText, ProductImportField.Purchase, cPurchase);
                continue;
            }

            // 2026-11-02 (аудит №2, С2-19): цена продажи не выше закупки — скорее всего, в «Цену» попала закупка; строка не блокируется, но помечается
            if (purchase is > 0 && sale is > 0 && sale.Value <= purchase.Value)
                row.Warning = AppendWarning(row.Warning, Tr.T($"цена продажи {sale.Value:0.####} не выше закупки {purchase.Value:0.####} — проверьте столбцы «Цена» и «Закупка»",
                    $"сатуу баасы {sale.Value:0.####} сатып алуудан жогору эмес {purchase.Value:0.####} — «Баа» жана «Сатып алуу» мамычаларын текшериңиз",
                    $"the sale price {sale.Value:0.####} is not above the purchase price {purchase.Value:0.####} — check the “Price” and “Purchase” columns",
                    $"satış fiyatı {sale.Value:0.####} alış fiyatı {purchase.Value:0.####} değerinin üstünde değil — «Fiyat» ve «Alış» sütunlarını kontrol edin",
                    $"sotuv narxi {sale.Value:0.####} xarid narxi {purchase.Value:0.####} dan yuqori emas — «Narx» va «Xarid» ustunlarini tekshiring"));
            // 2026-11-20 (CT MAX): цена продажи 0 (или нет), а закупка есть — предупреждение
            if (serviceCodes && (sale is null or <= 0) && purchase is > 0)
                row.Warning = AppendWarning(row.Warning, Tr.T($"цена продажи 0, закупка {purchase.Value:0.####} — укажите цену продажи или наценку",
                    $"сатуу баасы 0, сатып алуу {purchase.Value:0.####} — сатуу баасын же үстөктү көрсөтүңүз",
                    $"the sale price is 0, the purchase price is {purchase.Value:0.####} — enter the sale price or a markup",
                    $"satış fiyatı 0, alış {purchase.Value:0.####} — satış fiyatını veya kâr oranını girin",
                    $"sotuv narxi 0, xarid {purchase.Value:0.####} — sotuv narxini yoki ustamani kiriting"));
            var name = row.Name;
            if (name.Length > 255)
            {
                Fail(row, Tr.T($"название длиннее 255 символов ({name.Length})", $"аталышы 255 белгиден узун ({name.Length})", $"name is longer than 255 characters ({name.Length})",
                    $"ad 255 karakterden uzun ({name.Length})", $"nomi 255 belgidan uzun ({name.Length})"), ProductImportField.Name, cName);
                continue;
            }

            // ---- 2026-10-30: доп. штрихкоды (несколько значений через ; , пробел | или перенос) — только цифры 4–14, без чужих
            var rowAlts = new List<string>();
            var rowMeta = new Dictionary<string, AltMeta>(StringComparer.Ordinal);
            if ((cAlt >= 0 || cPack >= 0) && !ParseRowAlts(row, src, cAlt, cPack, cPackQty, cAltName, barcode, settings.SkipBadAltCodes, rowAlts, rowMeta))
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
                        if (first.Tile is not null && !settings.AddAltBarcodesToExisting)
                            break; // 2026-11-20: «Добавлять доп. штрихкоды» выключено — товару склада коды не добавляем
                        if (!first._alts.Any(a => Canon(a) == Canon(extra)) && Canon(extra) != Canon(first.Barcode) && !seen.ContainsKey(Canon(extra)) && index.Find(extra) is null)
                        {
                            AddAlt(first, extra, rowMeta); // 2026-11-20: вместе с «код:N» / штуками в упаковке
                            seen[Canon(extra)] = first;
                        }
                        else if (first._alts.Any(a => Canon(a) == Canon(extra)))
                        {
                            if (rowMeta.TryGetValue(Canon(extra), out var again) && again.Qty > 0)
                                AddAlt(first, extra, rowMeta); // тот же код повторён строкой, которая впервые говорит, сколько в нём штук
                        }
                        else if (Canon(extra) != Canon(first.Barcode) && (seen.TryGetValue(Canon(extra), out var other) ? other != first : index.Find(extra) is { } holderTile && !ReferenceEquals(holderTile, first.Tile)))
                        {
                            // код занят другим товаром — в повторную строку он не переносится (раньше пропадал молча)
                            row.AltSkipped++;
                            row.Warning = AppendWarning(row.Warning, Tr.T($"доп. код {extra} занят другим товаром", $"кошумча код {extra} башка товарда бар", $"extra code {extra} belongs to another product",
                                $"ek kod {extra} başka üründe", $"qo'shimcha kod {extra} boshqa mahsulotda") + CodeSkippedText);
                        }
                    }
                    (first.MergedFrom ??= []).Add(row.FileRow);
                    row.Kind = BulkImportRowKind.Merged;
                    row.MergedInto = first.FileRow;
                    // 2026-11-20: строка-дополнение (второй режим: один штрихкод — несколько строк, вторая добавляет доп. код) складывает и количество — предупреждаем
                    if (!cardsOnly && quantity is > 0 && rowAlts.Count > 0)
                        row.Warning = Tr.T($"количество {quantity.Value:0.###} сложено со строкой {first.FileRow} — если строка только добавляет доп. код, оставьте количество пустым",
                            $"саны {quantity.Value:0.###} {first.FileRow}-сап менен кошулду — сап кошумча кодду гана кошсо, санын бош калтырыңыз",
                            $"quantity {quantity.Value:0.###} was added to row {first.FileRow} — if the row only adds an extra code, leave the quantity empty",
                            $"miktar {quantity.Value:0.###} {first.FileRow}. satırla toplandı — satır yalnızca ek kod ekliyorsa miktarı boş bırakın",
                            $"miqdor {quantity.Value:0.###} {first.FileRow}-qator bilan qo'shildi — qator faqat qo'shimcha kod qo'shsa, miqdorni bo'sh qoldiring");
                    if ((purchase is > 0 && Math.Abs(purchase.Value - target.PurchasePrice) > 0.004) || (sale is > 0 && Math.Abs(sale.Value - target.SalePrice) > 0.004))
                        row.Warning = AppendWarning(row.Warning, Tr.T($"цена отличается — взята из строки {first.FileRow}", $"баасы айырмаланат — {first.FileRow}-саптан алынды",
                            $"price differs — taken from row {first.FileRow}", $"fiyat farklı — {first.FileRow}. satırdan alındı", $"narx farq qiladi — {first.FileRow}-qatordan olindi"));
                    continue;
                }

                Fail(row, Tr.T($"штрихкод уже есть в строке {first.FileRow} — пропускается", $"штрих-код {first.FileRow}-сапта мурунтан бар — өткөрүлүп жиберилет",
                    $"barcode is already in row {first.FileRow} — skipped", $"barkod zaten {first.FileRow}. satırda var — atlandı",
                    $"shtrix-kod {first.FileRow}-qatorda allaqachon bor — o'tkazib yuboriladi"), ProductImportField.Barcode, cBarcode);
                continue;
            }

            // ---- 2026-10-30: доп. штрихкод уже основной/доп. у ДРУГОГО товара (в каталоге или выше в файле) — ошибка строки
            var ownTile = index.Find(barcode);
            // 2026-11-20: «Добавлять доп. штрихкоды» выключено — у товара, который уже есть на складе, коды из файла не берём (и не проверяем)
            if (ownTile is not null && !settings.AddAltBarcodesToExisting)
            {
                rowAlts.Clear();
                rowMeta.Clear();
            }
            // 2026-11-20 (CT MAX): служебный код («*10», без цифр, не 4–14 цифр) у товара, которого на складе нет: товар не теряется — внутренний штрихкод NurCRM,
            // прежний код — в артикул. Код считается от прежнего кода и названия, а не случайно: повторная проверка/«Продолжить загрузку» даёт тот же код, дубля не будет
            // (товар, созданный прошлым запуском с тем же названием, находится в каталоге по этому коду и идёт как существующий).
            if (serviceCodes && ownTile is null && settings.GenerateInternalCodes && !IsCardBarcode(barcode))
            {
                fileCodes ??= new HashSet<string>(table.DataRows.Select(r => ProductCodeLookup.Normalize(r[cBarcode])).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);
                var rawCode = barcode;
                var nameKeyForCode = NameKey(row.Name);
                var generated = InternalCode(rawCode + "|" + nameKeyForCode, c =>
                    fileCodes.Contains(c) || (seen.TryGetValue(Canon(c), out var usedBy) && usedBy != row)
                    || (index.Find(c) is { } holderOfCode && NameKey(holderOfCode.Title) != nameKeyForCode));
                var noDigits = !rawCode.Any(char.IsDigit);
                row.Article = rawCode.Length > 64 ? rawCode[..64] : rawCode;
                row.InternalCode = true;
                barcode = generated;
                canon = Canon(generated);
                row.Barcode = generated;
                ownTile = index.Find(generated);
                var shown = rawCode.Length > 20 ? rawCode[..20] + "…" : rawCode;
                row.Warning = AppendWarning(row.Warning, noDigits
                    ? Tr.T($"в штрихкоде «{shown}» нет цифр (похоже на сбой раскладки) — товар загружается с внутренним штрихкодом NurCRM {generated}, прежний код — в артикуле",
                        $"штрих-кодто «{shown}» цифра жок (калып ката окшойт) — товар ички NurCRM штрих-коду {generated} менен жүктөлөт, мурунку код — артикулда",
                        $"the barcode “{shown}” has no digits (looks like a keyboard-layout slip) — the product is loaded with the NurCRM internal barcode {generated}, the old code goes to the article",
                        $"«{shown}» barkodunda rakam yok (klavye düzeni hatasına benziyor) — ürün NurCRM iç barkodu {generated} ile yüklenir, eski kod ürün koduna yazılır",
                        $"«{shown}» shtrix-kodida raqam yo'q (klaviatura xatosiga o'xshaydi) — mahsulot NurCRM ichki shtrix-kodi {generated} bilan yuklanadi, eski kod artikulga yoziladi")
                    : Tr.T($"служебный код «{shown}» — товар загружается с внутренним штрихкодом NurCRM {generated}, прежний код — в артикуле",
                        $"кызматтык код «{shown}» — товар ички NurCRM штрих-коду {generated} менен жүктөлөт, мурунку код — артикулда",
                        $"service code “{shown}” — the product is loaded with the NurCRM internal barcode {generated}, the old code goes to the article",
                        $"hizmet kodu «{shown}» — ürün NurCRM iç barkodu {generated} ile yüklenir, eski kod ürün koduna yazılır",
                        $"xizmat kodi «{shown}» — mahsulot NurCRM ichki shtrix-kodi {generated} bilan yuklanadi, eski kod artikulga yoziladi"));
            }
            var altConflict = false;
            foreach (var alt in rowAlts.ToList())
            {
                string? conflict = null;
                if (index.Find(alt) is { } holder && (ownTile is null || !string.Equals(holder.Id, ownTile.Id, StringComparison.OrdinalIgnoreCase)))
                    conflict = Tr.T($"доп. код {alt} уже у товара «{holder.Title}»", $"кошумча код {alt} «{holder.Title}» товарында мурунтан бар",
                        $"extra code {alt} already belongs to “{holder.Title}”", $"ek kod {alt} zaten «{holder.Title}» ürününde var", $"qo'shimcha kod {alt} «{holder.Title}» mahsulotida allaqachon bor");
                else if (seen.TryGetValue(Canon(alt), out var inFile))
                    conflict = Tr.T($"доп. код {alt} уже в строке {inFile.FileRow} файла", $"кошумча код {alt} файлдын {inFile.FileRow}-сабында мурунтан бар",
                        $"extra code {alt} is already in row {inFile.FileRow} of the file", $"ek kod {alt} dosyanın {inFile.FileRow}. satırında zaten var",
                        $"qo'shimcha kod {alt} faylning {inFile.FileRow}-qatorida allaqachon bor");
                if (conflict is null)
                    continue;
                var packCode = rowMeta.TryGetValue(Canon(alt), out var altInfo) && altInfo.Pack;
                if (settings.SkipBadAltCodes)
                {
                    // 2026-11-20: пропускаем только этот код, товар загружается
                    rowAlts.Remove(alt);
                    rowMeta.Remove(Canon(alt));
                    row.AltSkipped++;
                    row.Warning = AppendWarning(row.Warning, conflict + CodeSkippedText);
                    continue;
                }
                Fail(row, conflict, packCode ? ProductImportField.PackBarcode : ProductImportField.AltBarcodes, packCode ? cPack : cAlt);
                altConflict = true;
                break;
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
                    AddAlt(head, extra, rowMeta); // 2026-11-20: доп. коды и штуки в упаковке из строк-«одно название» собираются в один товар
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
                    row.Warning = AppendWarning(row.Warning, Tr.T($"штрихкод файла — доп. штрихкод товара «{tile.Title}» (основной {tile.Barcode})", $"файлдагы штрих-код — «{tile.Title}» товарынын кошумча штрих-коду (негизги {tile.Barcode})",
                        $"the file barcode is an extra barcode of “{tile.Title}” (main {tile.Barcode})", $"dosyadaki barkod «{tile.Title}» ürününün ek barkodu (ana {tile.Barcode})",
                        $"fayldagi shtrix-kod «{tile.Title}» mahsulotining qo'shimcha shtrix-kodi (asosiy {tile.Barcode})"));
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
                        $"shtrix-kod «{barcode}»: yangi mahsulot uchun faqat raqamlar, 4–14 belgi")
                        + (serviceCodes ? Tr.T(" (включите в «Дополнительно» внутренний код NurCRM для служебных кодов или исправьте код в файле)", " («Кошумча» бөлүмүндө кызматтык коддорго NurCRM ички кодун күйгүзүңүз же файлда оңдоңуз)",
                            " (turn on the NurCRM internal code for service codes in “More”, or fix the code in the file)", " («Ek»te hizmet kodları için NurCRM iç kodunu açın veya dosyadaki kodu düzeltin)",
                            " («Qo'shimcha»da xizmat kodlari uchun NurCRM ichki kodini yoqing yoki fayldagi kodni tuzating)") : ""), ProductImportField.Barcode, cBarcode);
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
                if (cBrand >= 0 && src[cBrand].Trim() is { Length: > 0 } brandText)
                    row.Brand = brandText.Length > 100 ? brandText[..100] : brandText;
            }

            // ---- проверка теми же правилами, что в приёмке (+ единица и дробное количество)
            row.BaseKind = row.Kind;
            row.Weighed = weighed;
            row.UnitProblem = unitProblem;
            row.Line = line;
            seen[canon] = row; // 2026-10-29: и строка с ошибкой проверки: повтор её штрихкода складывается в неё, а не заводит второй товар
            row._alts.AddRange(rowAlts);
            foreach (var (metaKey, metaValue) in rowMeta)
                row._altMeta[metaKey] = metaValue; // 2026-11-20: штуки в упаковке и названия вариантов
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
            GroupRowsSkipped = groupRows,
            _knownCategories = new HashSet<string>(catalogList.Select(t => (t.Category ?? "").Trim()).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase),
        };
        plan.Recompute();
        watch.Stop();
        plan.Elapsed = watch.Elapsed;
        return plan;

    }

    /// <summary>2026-10-30: правило проекта для штрихкода нового товара и доп. штрихкодов: только цифры, 4–14 знаков.</summary>
    internal static bool IsCardBarcode(string code) => code.Length is >= 4 and <= 14 && code.All(char.IsDigit);

    /// <summary>2026-11-20 (скриншот владельца: файл «Компания / Почта владельца / Цена в мес.» загрузили как товары — 111 красных строк «штрихкод market@gmail.com»):
    /// файл не похож на список товаров. Да — когда больше 80 % непустых значений столбца штрихкода не цифровые (почты, адреса, текст), либо столбцы штрихкода/названия
    /// угаданы с низкой уверенностью (по данным, не по заголовку) и половина строк и более — ошибки. Решает окно: показать плашку вместо таблицы ошибок.</summary>
    public static bool LooksLikeNotProductList(ProductFileTable table, ProductImportMapping mapping, BulkImportPlan? plan)
    {
        var cBarcode = mapping.ColumnOf(ProductImportField.Barcode);
        if (cBarcode < 0 || table.DataRowCount == 0)
            return false;
        int nonEmpty = 0, nonDigit = 0, seenRows = 0;
        foreach (var row in table.DataRows)
        {
            if (seenRows++ >= 500)
                break;
            var value = ProductCodeLookup.Normalize(row[cBarcode]);
            if (value.Length == 0)
                continue;
            nonEmpty++;
            if (!value.All(char.IsDigit))
                nonDigit++;
        }
        if (nonEmpty >= 3 && nonDigit * 100 > nonEmpty * 80)
            return true;
        var weak = mapping.ConfidenceOf(ProductImportField.Barcode) == MappingConfidence.Low || mapping.ConfidenceOf(ProductImportField.Name) == MappingConfidence.Low;
        return weak && plan is { Rows.Count: > 0 } && plan.ErrorCount * 100 >= plan.Rows.Count * 50;
    }

    /// <summary>2026-11-20: ячейка цены пуста или это число не больше нуля.</summary>
    private static bool IsZeroOrEmpty(string cell)
    {
        var t = cell.Trim();
        return t.Length == 0 || (ProductCsvImporter.TryParseNumber(t, out var n) && n <= 0);
    }

    /// <summary>2026-11-20 (CT MAX): внутренний штрихкод NurCRM по «семени» (прежний код + название), а не случайный: EAN-13, первая цифра 1, 3–9 (не 0 и не 2 — как у
    /// <see cref="BarcodeGenerator"/>), не «470…», верная контрольная цифра. Занятый код (<paramref name="taken"/>) — следующий вариант того же семени.</summary>
    internal static string InternalCode(string seed, Func<string, bool> taken)
    {
        ReadOnlySpan<char> first = "13456789";
        for (var salt = 0; salt < 1000; salt++)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed + "#" + salt));
            var n = BitConverter.ToUInt64(hash, 0);
            var body = first[(int)(n % 8)].ToString() + ((n / 8) % 100_000_000_000UL).ToString("D11", CultureInfo.InvariantCulture);
            if (body.StartsWith("470", StringComparison.Ordinal))
                continue;
            var sum = 0;
            for (var i = 0; i < 12; i++)
                sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
            var code = body + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
            if (!taken(code))
                return code;
        }
        return BarcodeGenerator.Generate();
    }

    private static readonly char[] AltSeparators =[';', ',', ' ', '|', '\n', '\r', '\t'];

    /// <summary>2026-10-30: «Название» → ключ для объединения: нижний регистр, ё = е, пробелы схлопнуты.</summary>
    internal static string NameKey(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    private static string CodeSkippedText => Tr.T(" — код пропущен", " — код өткөрүлдү", " — code skipped", " — kod atlandı", " — kod o'tkazildi");

    /// <summary>Пробелы вокруг «:» / «×» / «*» в «код : N» убираются до разбиения ячейки по пробелам.</summary>
    private static readonly Regex AltQtySeparator = new(@"\s*[:：×*]\s*", RegexOptions.Compiled);

    /// <summary>2026-11-20: код не годится. Включено «пропускать только плохой код» — код пропущен с предупреждением (true, продолжаем), иначе строка с ошибкой (false).</summary>
    private static bool SoftFail(BulkImportRow row, string error, ProductImportField field, int column, bool skipBad)
    {
        if (!skipBad)
        {
            Fail(row, error, field, column);
            return false;
        }
        row.AltSkipped++;
        row.Warning = AppendWarning(row.Warning, error + CodeSkippedText);
        return true;
    }

    private static bool PackQtyOk(double qty) => double.IsFinite(qty) && qty > 0 && qty <= PackScanRules.MaxPackQuantity;

    private static string BadPackQtyText(string text, string code) => Tr.T($"штук в упаковке «{text}» у кода {code}: нужно число больше нуля (до {PackScanRules.MaxPackQuantity:0})",
        $"{code} кодундагы даана саны «{text}»: нөлдөн чоң сан керек ({PackScanRules.MaxPackQuantity:0} чейин)",
        $"pieces in pack “{text}” for code {code}: a number above zero is needed (up to {PackScanRules.MaxPackQuantity:0})",
        $"{code} kodunda paketteki adet «{text}»: sıfırdan büyük sayı gerekir ({PackScanRules.MaxPackQuantity:0} değerine kadar)",
        $"{code} kodi uchun qadoqdagi dona soni «{text}»: noldan katta son kerak ({PackScanRules.MaxPackQuantity:0} gacha)");

    /// <summary>2026-11-20: доп. коды строки: столбец «Доп. штрихкоды» (код или «код:N» — N штук в упаковке), столбец «Штрихкод упаковки» + «Штук в упаковке»
    /// + «Название варианта». false — строка помечена ошибкой (при выключенном «пропускать только плохой код»).</summary>
    private static bool ParseRowAlts(BulkImportRow row, ProductFileRow src, int cAlt, int cPack, int cPackQty, int cAltName, string main, bool skipBad,
        List<string> alts, Dictionary<string, AltMeta> meta)
    {
        if (cAlt >= 0 && src[cAlt].Trim().Length > 0
            && !TryParseAlts(row, src[cAlt], ProductImportField.AltBarcodes, cAlt, main, skipBad, alts, meta, 0, ""))
            return false;
        var name = cAltName >= 0 ? src[cAltName].Trim() : "";
        var altsBeforePack = alts.Count;
        if (cPack >= 0 && src[cPack].Trim().Length > 0)
        {
            var qtyText = cPackQty >= 0 ? src[cPackQty].Trim() : "";
            double qty = 0;
            if (qtyText.Length > 0 && (!ProductCsvImporter.TryParseNumber(qtyText, out qty) || !PackQtyOk(qty)))
            {
                // «Штук в упаковке» не число больше нуля — код упаковки не берём
                if (!SoftFail(row, BadPackQtyText(qtyText, src[cPack].Trim()), ProductImportField.PackQty, cPackQty, skipBad))
                    return false;
            }
            else if (!TryParseAlts(row, src[cPack], ProductImportField.PackBarcode, cPack, main, skipBad, alts, meta, Math.Round(qty, 3), name))
                return false;
        }
        // название варианта без штрихкода упаковки — относится к единственному доп. коду ячейки; если кодов несколько, к какому из них — неизвестно
        if (name.Length > 0 && alts.Count == altsBeforePack && altsBeforePack > 0)
        {
            if (altsBeforePack == 1)
                meta[Canon(alts[0])] = meta[Canon(alts[0])] with { Name = name };
            else
                row.Warning = AppendWarning(row.Warning, Tr.T("название варианта не загружено: в ячейке несколько кодов, непонятно, к какому оно относится",
                    "вариант аталышы жүктөлгөн жок: уячада бир нече код, кайсынысына тиешелүү экени белгисиз",
                    "variant name not loaded: the cell has several codes and it is unclear which one it belongs to",
                    "varyant adı yüklenmedi: hücrede birkaç kod var, hangisine ait olduğu belli değil",
                    "variant nomi yuklanmadi: katakda bir nechta kod bor, qaysi biriga tegishli ekani noma'lum"));
        }
        return true;
    }

    /// <summary>Разобрать ячейку доп. кодов: несколько кодов через ; , пробел | или перенос, «код:N» — N штук в упаковке. Повтор кода в ячейке схлопывается.
    /// false — строка помечена ошибкой (научная запись Excel, не цифры, не 4–14 знаков, плохое N); при <paramref name="skipBad"/> плохой код просто пропускается.</summary>
    private static bool TryParseAlts(BulkImportRow row, string raw, ProductImportField field, int column, string mainBarcode, bool skipBad, List<string> alts,
        Dictionary<string, AltMeta> meta, double defaultQty, string defaultName)
    {
        var isPack = field == ProductImportField.PackBarcode;
        foreach (var part in AltQtySeparator.Replace(raw, ":").Split(AltSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = part;
            string? inlineQty = null;
            var colon = code.IndexOf(':');
            if (colon >= 0)
            {
                inlineQty = code[(colon + 1)..].Trim();
                code = code[..colon].Trim();
            }
            if (ExponentBarcode.IsMatch(code))
            {
                if (SoftFail(row, Tr.T($"доп. штрихкод «{code}» испорчен Excel — сохраните столбец как текст и загрузите заново",
                        $"кошумча штрих-код «{code}» Excel тарабынан бузулган — мамычаны текст катары сактап, кайра жүктөңүз",
                        $"extra barcode “{code}” was mangled by Excel — save the column as text and load again",
                        $"ek barkod «{code}» Excel tarafından bozulmuş — sütunu metin olarak kaydedip yeniden yükleyin",
                        $"qo'shimcha shtrix-kod «{code}» Excel tomonidan buzilgan — ustunni matn sifatida saqlab, qayta yuklang"), field, column, skipBad))
                    continue;
                return false;
            }
            if (TrailingZeroDecimals.Match(code) is { Success: true } trimmed)
                code = trimmed.Groups[1].Value;
            code = ProductCodeLookup.Normalize(code);
            if (code.Length == 0)
                continue;
            if (!IsCardBarcode(code))
            {
                if (SoftFail(row, Tr.T($"доп. штрихкод «{code}»: только цифры, 4–14 знаков", $"кошумча штрих-код «{code}»: цифралар гана, 4–14 белги",
                        $"extra barcode “{code}”: digits only, 4–14 characters", $"ek barkod «{code}»: yalnızca rakamlar, 4–14 karakter", $"qo'shimcha shtrix-kod «{code}»: faqat raqamlar, 4–14 belgi"),
                        field, column, skipBad))
                    continue;
                return false;
            }
            var qty = defaultQty;
            if (inlineQty is { Length: > 0 })
            {
                if (!ProductCsvImporter.TryParseNumber(inlineQty, out qty) || !PackQtyOk(qty))
                {
                    if (SoftFail(row, BadPackQtyText(inlineQty, code), field, column, skipBad))
                        continue;
                    return false;
                }
                qty = Math.Round(qty, 3);
            }
            if (Canon(code) == Canon(mainBarcode))
            {
                if (isPack) // код упаковки = основной код: упаковку от штуки не отличить — загружать нечего
                    row.Warning = AppendWarning(row.Warning, Tr.T($"штрихкод упаковки {code} совпадает с основным — не загружен", $"куту штрих-коду {code} негизги менен дал келет — жүктөлгөн жок",
                        $"pack barcode {code} is the same as the main one — not loaded", $"paket barkodu {code} ana barkodla aynı — yüklenmedi", $"qadoq shtrix-kodi {code} asosiy bilan bir xil — yuklanmadi"));
                continue;
            }
            var key = Canon(code);
            var found = alts.FindIndex(a => Canon(a) == key);
            if (found >= 0)
            {
                // повтор кода в ячейке — один код; но если повтор впервые сообщает штуки в упаковке, они сохраняются
                if (meta.TryGetValue(key, out var old) && old.Qty <= 0 && qty > 0)
                    meta[key] = old with { Qty = qty, Pack = old.Pack || isPack };
                continue;
            }
            alts.Add(code);
            meta[key] = new AltMeta(defaultName, qty, isPack);
        }
        return true;
    }

    /// <summary>2026-11-20: доп. код с названием и штуками в упаковке — к товару <paramref name="target"/> (без дублей; штуки дописываются, если их там ещё не было).</summary>
    private static void AddAlt(BulkImportRow target, string code, IReadOnlyDictionary<string, AltMeta> meta)
    {
        var key = Canon(code);
        if (!target._alts.Any(a => Canon(a) == key))
            target._alts.Add(code);
        if (meta.TryGetValue(key, out var m) && (!target._altMeta.TryGetValue(key, out var old) || (old.Qty <= 0 && m.Qty > 0)))
            target._altMeta[key] = m;
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

    /// <summary>Слияние: уже имеющиеся доп. штрихкоды товара + новые из файла; без затирания, без дублей, без основного штрихкода.
    /// 2026-11-20: у кода, который у товара уже есть, но без «штук в упаковке», дописываются штуки (и название, если пустое) из файла; заполненное не меняется.</summary>
    private static void FinalizeAlts(BulkImportRow row)
    {
        if (row._alts.Count == 0)
        {
            row.AltFull = [];
            row.AltAdded = 0;
            row.AltPackAdded = 0;
            return;
        }
        var full = row.Tile is { } tile ? ExistingAlts(tile) : [];
        var mainCanon = Canon(row.Tile?.Barcode ?? row.Barcode);
        var added = 0;
        var packs = 0;
        foreach (var code in row._alts)
        {
            var c = Canon(code);
            if (c == mainCanon || c == Canon(row.Barcode))
                continue;
            var meta = row._altMeta.TryGetValue(c, out var m) ? m : default;
            var at = full.FindIndex(v => Canon(v.Barcode) == c);
            if (at >= 0)
            {
                if (full[at].Quantity <= 0 && meta.Qty > 0)
                {
                    full[at] = new AlternateBarcodeVariant { Barcode = full[at].Barcode, Name = string.IsNullOrWhiteSpace(full[at].Name) ? meta.Name : full[at].Name, Quantity = meta.Qty };
                    added++;
                    packs++;
                }
                continue;
            }
            full.Add(new AlternateBarcodeVariant { Barcode = code, Name = meta.Name ?? "", Quantity = meta.Qty });
            added++;
            if (meta.Qty > 0)
                packs++;
        }
        row.AltFull = full;
        row.AltAdded = added;
        row.AltPackAdded = packs;
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
    private readonly record struct Cols(int Quantity, int Name, int Purchase, int Sale, int Unit, bool CardsOnly, bool NewOnly = false);

    private Cols _cols;

    /// <summary>2026-11-20: категории, которые уже есть в каталоге (для «будет создано категорий: N»).</summary>
    private HashSet<string> _knownCategories = new(StringComparer.OrdinalIgnoreCase);

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
        row.NewOnly = cols.NewOnly; // 2026-11-20: «только добавить новые»
        row.ChPurchase = cols.CardsOnly && !isNew && line.PurchasePrice > 0 && Math.Abs(line.PurchasePrice - line.OriginalPurchasePrice) > 0.004;
        row.ChSale = cols.CardsOnly && !isNew && line.SalePrice > 0 && Math.Abs(line.SalePrice - line.OriginalSalePrice) > 0.004;
        // 2026-11-20: прайс автозапчастей — строка с изменившимися полями запчасти (бренд, ячейка, номера, остаток) тоже пишется.
        // 2026-11-20: «только добавить новые» — существующие товары пропускаются целиком.
        row.Unchanged = !isNew && problems.Count == 0 && (cols.NewOnly
            || (cols.CardsOnly && !row.ChPurchase && !row.ChSale && row.AltAdded == 0 && row.Extra is not AutoPartsRowData { NeedsWrite: true }));
        return problems.Count == 0;
    }

    /// <summary>Пересчёт после правок строк в таблице (количество, закупка, продажа, наценка, единица): заново проверяет строки, у которых есть
    /// строка прихода, собирает <see cref="Lines"/>/<see cref="FileRows"/> и счётчики. Ошибки разбора числа (строки без прихода) не трогает.</summary>
    public void Recompute()
    {
        var lines = new List<ReceivingLineVm>();
        var fileRows = new List<int>();
        int receipt = 0, created = 0, errors = 0, merged = 0, unchanged = 0, altCodes = 0, altPacks = 0, altSkipped = 0, internalCodes = 0;
        foreach (var row in Rows)
        {
            if (row.Line is not null && row.Kind != BulkImportRowKind.Merged)
            {
                FinalizeAlts(row);
                if (Check(row, _cols) && !row.Unchanged)
                {
                    lines.Add(row.Line);
                    fileRows.Add(row.FileRow);
                    if (row.InternalCode)
                        internalCodes++;
                    altCodes += row.AltAdded; // 2026-11-20: «доп. кодов: N» в итоге шага
                    altPacks += row.AltPackAdded;
                }
            }
            if (row.Kind != BulkImportRowKind.Error)
                altSkipped += row.AltSkipped;
            if (row.Line is not null && row.Kind != BulkImportRowKind.Merged)
            {
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
        AltCodeCount = altCodes;
        AltPackCount = altPacks;
        AltSkippedCount = altSkipped;
        InternalCodeCount = internalCodes;
        NewCategoryCount = Rows.Where(r => r.Kind == BulkImportRowKind.New && r.Line?.CategoryName is { Length: > 0 }).Select(r => r.Line!.CategoryName!.Trim()).Where(c => !_knownCategories.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
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

        foreach (var row in Rows.Where(r => r.Kind == BulkImportRowKind.Error && !r.LoadNeedsCheck))
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
