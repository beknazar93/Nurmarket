using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.GlobalProducts;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: план загрузки накладной — строки предпросмотра (статус, строка приёмки ReceivingLineVm), итоги, сверка с итогом из файла.
// Своей логики денег нет: цены, наценку, подорожание считает ReceivingLineVm / ReceivingPricing, проверку — PurchaseReceivingService.Validate,
// проводит обычная приёмка склада (BackgroundReceivingQueue). План лишь готовит строки и переносит их в список приёмки.

public enum InvoiceRowStatus
{
    FoundBarcode,
    FoundArticle,
    FoundName,
    FoundFuzzy,
    NewFromDirectory,
    NewManual,
    Unrecognized,
}

public sealed class InvoicePlanRow : INotifyPropertyChanged
{
    private InvoiceRowStatus _status;
    private bool _confirmed;

    public InvoicePlanRow(InvoiceItem item, InvoiceMatch match, ReceivingLineVm line, InvoiceRowStatus status, GlobalProductInfo? directory)
    {
        Item = item;
        Match = match;
        Line = line;
        _status = status;
        Directory = directory;
        UnitWarning = !string.IsNullOrWhiteSpace(line.ProductId) && InvoiceUnits.Differ(item.Unit, line.OriginalUnit);
        line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReceivingLineVm.Quantity) or nameof(ReceivingLineVm.ProductName) or nameof(ReceivingLineVm.PurchasePrice))
                Reevaluate();
        };
    }

    public InvoiceItem Item { get; }
    public InvoiceMatch Match { get; private set; }
    public ReceivingLineVm Line { get; private set; }
    public GlobalProductInfo? Directory { get; }

    /// <summary>Номера строк файла, склеенные в эту строку (повтор того же товара).</summary>
    public List<int> MergedFrom { get; } = [];

    public InvoiceRowStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            RaiseAll();
        }
    }

    public bool Confirmed
    {
        get => _confirmed;
        private set
        {
            if (_confirmed == value)
                return;
            _confirmed = value;
            RaiseAll();
        }
    }

    public bool IsFound => Status is InvoiceRowStatus.FoundBarcode or InvoiceRowStatus.FoundArticle or InvoiceRowStatus.FoundName or InvoiceRowStatus.FoundFuzzy;
    public bool IsNew => Status is InvoiceRowStatus.NewFromDirectory or InvoiceRowStatus.NewManual;
    public bool Included => Status != InvoiceRowStatus.Unrecognized;

    /// <summary>2026-10-27 (ревью Ск-11): единица накладной («упак», «кор») не совпадает с единицей товара в кассе («шт»): цена в файле может быть ценой упаковки, а не штуки.
    /// Требует «Это он» (кассир проверяет количество и цену) — иначе «Обновить цены продажи» подняло бы цену штуки в N раз.</summary>
    public bool UnitWarning { get; private set; }

    /// <summary>Совпадение требует «Это он» от кассира и пока не подтверждено.</summary>
    public bool NeedsConfirm => IsFound && (Match.NeedsConfirm || UnitWarning) && !Confirmed;

    /// <summary>Можно нажать «Это не тот товар» / «Это он» (найден неточно).</summary>
    public bool CanDecide => IsFound && (Match.NeedsConfirm || UnitWarning);

    /// <summary>Название строки в накладной (нет его в файле — из справочника NurCRM).</summary>
    public string InvoiceName => string.IsNullOrWhiteSpace(Item.Name) ? (Directory?.Name ?? "") : Item.Name;

    /// <summary>Вторая строка под названием: как товар назван в накладной (если отличается от найденного) и замечания разбора.</summary>
    public string SubText
    {
        get
        {
            var parts = new List<string>();
            if (IsFound && Match.Tile is { } t && !string.Equals(t.Title, Item.Name, StringComparison.OrdinalIgnoreCase) && Item.Name.Length > 0)
                parts.Add(Tr.T("в накладной: ", "накладнойдо: ", "on the invoice: ", "faturada: ", "yuk xatida: ") + Item.Name);
            if (UnitWarning && IsFound)
                parts.Add(Tr.T($"⚠ единица в накладной «{Item.Unit}», в карточке «{Line.OriginalUnit}» — проверьте количество и цену (не цена ли это упаковки)",
                    $"⚠ накладнойдо бирдик «{Item.Unit}», карточкада «{Line.OriginalUnit}» — санын жана баасын текшериңиз (таңгактын баасы эмеспи)",
                    $"⚠ the invoice unit is “{Item.Unit}”, the card says “{Line.OriginalUnit}” — check quantity and price (is it a pack price?)",
                    $"⚠ faturada birim «{Item.Unit}», kartta «{Line.OriginalUnit}» — miktar ve fiyatı kontrol edin (paket fiyatı olabilir)",
                    $"⚠ yuk xatida birlik «{Item.Unit}», kartada «{Line.OriginalUnit}» — miqdor va narxni tekshiring (qadoq narxi bo'lishi mumkin)"));
            if (PriceRoundNote is { Length: > 0 } roundNote)
                parts.Add(roundNote);
            if (Item.Warnings.Count > 0)
                parts.Add("⚠ " + string.Join("; ", Item.Warnings));
            if (MergedFrom.Count > 0)
                parts.Add(Tr.T($"сложено со строками {string.Join(", ", MergedFrom)}", $"{string.Join(", ", MergedFrom)}-саптар кошулду", $"merged rows {string.Join(", ", MergedFrom)}",
                    $"birleşen satırlar {string.Join(", ", MergedFrom)}", $"qo'shilgan qatorlar {string.Join(", ", MergedFrom)}"));
            return string.Join(" · ", parts);
        }
    }

    public bool HasSubText => SubText.Length > 0;

    // 2026-10-31 (приёмка 1.17.70, Важное 2): цена за единицу из файла (сумма ÷ количество, НДС) бывает с 3–4 знаками; сервер хранит закупку с 2 —
    // при загрузке округляем до копеек и показываем подсказку, а не блокируем каждую строку. Ручная правка цены подсказку убирает.
    private double _origPrice = double.NaN, _roundedPrice = double.NaN;

    public void SetPriceRounding(double original, double rounded)
    {
        _origPrice = original;
        _roundedPrice = rounded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSubText)));
    }

    public string PriceRoundNote
    {
        get
        {
            if (double.IsNaN(_origPrice) || Math.Abs(Line.PurchasePrice - _roundedPrice) > 1e-9)
                return "";
            var from = $"{ReceivingPricing.LineTotal(Line.Quantity, _origPrice):N2}";
            var to = $"{ReceivingPricing.LineTotal(Line.Quantity, _roundedPrice):N2}";
            return Tr.T($"цена округлена до копеек ({_origPrice:0.####} → {_roundedPrice:0.##}), сумма строки: {from} → {to}",
                $"баа тыйынга чейин тегизделди ({_origPrice:0.####} → {_roundedPrice:0.##}), сап суммасы: {from} → {to}",
                $"price rounded to cents ({_origPrice:0.####} → {_roundedPrice:0.##}), line total: {from} → {to}",
                $"fiyat kuruşa yuvarlandı ({_origPrice:0.####} → {_roundedPrice:0.##}), satır tutarı: {from} → {to}",
                $"narx tiyingacha yaxlitlandi ({_origPrice:0.####} → {_roundedPrice:0.##}), qator summasi: {from} → {to}");
        }
    }

    public string StatusText => IsFound && UnitWarning && !Match.NeedsConfirm && !Confirmed
        ? Tr.T("Проверьте · единица", "Текшериңиз · бирдик", "Check · unit", "Kontrol edin · birim", "Tekshiring · birlik")
        : Status switch
    {
        InvoiceRowStatus.FoundBarcode => Tr.T("Найден · штрихкод", "Табылды · штрих-код", "Found · barcode", "Bulundu · barkod", "Topildi · shtrix-kod"),
        InvoiceRowStatus.FoundArticle => Match.NeedsConfirm && !Confirmed
            ? Tr.T("Проверьте · артикул", "Текшериңиз · артикул", "Check · article", "Kontrol edin · ürün kodu", "Tekshiring · artikul")
            : Tr.T("Найден · артикул", "Табылды · артикул", "Found · article", "Bulundu · ürün kodu", "Topildi · artikul"),
        InvoiceRowStatus.FoundName => Tr.T("Найден · название", "Табылды · аталышы", "Found · name", "Bulundu · ad", "Topildi · nomi"),
        InvoiceRowStatus.FoundFuzzy => Confirmed
            ? Tr.T("Подтверждён · похожий", "Ырасталды · окшош", "Confirmed · similar", "Onaylandı · benzer", "Tasdiqlandi · o'xshash")
            : Tr.T($"Похожий ≈{Match.Score * 100:0} %", $"Окшош ≈{Match.Score * 100:0} %", $"Similar ≈{Match.Score * 100:0}%",
                $"Benzer ≈{Match.Score * 100:0} %", $"O'xshash ≈{Match.Score * 100:0} %"),
        InvoiceRowStatus.NewFromDirectory => Tr.T("Новый · из справочника", "Жаңы · маалымдамадан", "New · from directory", "Yeni · rehberden", "Yangi · ma'lumotnomadan"),
        InvoiceRowStatus.NewManual => Tr.T("Новый · заполнить", "Жаңы · толтуруңуз", "New · fill in", "Yeni · doldurun", "Yangi · to'ldiring"),
        _ => Tr.T("Не распознан", "Таанылган жок", "Not recognized", "Tanınmadı", "Tanilmadi"),
    };

    /// <summary>Ключ цвета статуса в теме (BrushSuccess / BrushWarning / BrushDanger / BrushAccent / BrushTextSoft).</summary>
    public string StatusBrushKey => NeedsConfirm ? "BrushWarning" : Status switch
    {
        InvoiceRowStatus.FoundBarcode or InvoiceRowStatus.FoundName => "BrushSuccess",
        InvoiceRowStatus.FoundArticle => NeedsConfirm ? "BrushWarning" : "BrushSuccess",
        InvoiceRowStatus.FoundFuzzy => Confirmed ? "BrushSuccess" : "BrushWarning",
        InvoiceRowStatus.NewFromDirectory => "BrushText",
        InvoiceRowStatus.NewManual => "BrushText",
        _ => "BrushDanger",
    };

    /// <summary>«Это он»: найденный неточно товар принят.</summary>
    public void Confirm() => Confirmed = true;

    /// <summary>«Это не тот товар»: строка становится новым товаром (название и штрихкод — из накладной).</summary>
    public void MakeNew(Func<InvoiceItem, ReceivingLineVm> buildNewLine)
    {
        var qty = Line.Quantity;
        var purchase = Line.PurchasePrice;
        var fresh = buildNewLine(Item);
        fresh.Quantity = qty;
        fresh.PurchasePrice = purchase;
        Line = fresh;
        Match = InvoiceMatch.Nothing;
        UnitWarning = false;
        _confirmed = false;
        _status = InvoiceRowStatus.NewManual;
        RaiseAll();
    }

    /// <summary>Статус «найден» по виду совпадения (одно место для CreateRow и Reevaluate).</summary>
    internal static InvoiceRowStatus FoundStatusOf(InvoiceMatchKind kind) => kind switch
    {
        InvoiceMatchKind.Barcode => InvoiceRowStatus.FoundBarcode,
        InvoiceMatchKind.Article => InvoiceRowStatus.FoundArticle,
        InvoiceMatchKind.Fuzzy => InvoiceRowStatus.FoundFuzzy,
        _ => InvoiceRowStatus.FoundName,
    };

    /// <summary>Кассир поправил количество или название: нераспознанная строка становится годной.</summary>
    public void Reevaluate()
    {
        if (Status == InvoiceRowStatus.Unrecognized && Line.Quantity > 0 && !string.IsNullOrWhiteSpace(Line.ProductName))
        {
            // 2026-10-27 (ревью Ск-4): строка уже привязана к товару каталога (нашли по штрихкоду/артикулу/названию/«похожему») — статус возвращается по виду совпадения,
            // чтобы неподтверждённое совпадение снова потребовало «Это он». Раньше она становилась «Новый · заполнить» (подтверждение не требовалось), а остаток всё равно
            // зачислялся найденному товару (Line.ProductId).
            _status = !string.IsNullOrWhiteSpace(Line.ProductId) && Match.Kind != InvoiceMatchKind.None ? FoundStatusOf(Match.Kind) : InvoiceRowStatus.NewManual;
            RaiseAll();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Status), nameof(Confirmed), nameof(IsFound), nameof(IsNew), nameof(Included), nameof(NeedsConfirm), nameof(CanDecide), nameof(StatusText),
                     nameof(StatusBrushKey), nameof(SubText), nameof(HasSubText), nameof(Line), nameof(InvoiceName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>2026-10-27 (ревью Ск-5): что делать с поставщиком приёмки, когда накладную переносят в список.</summary>
public enum ReceivingSupplierAction
{
    /// <summary>Ничего особого: поставщик накладной ставится в приёмку (или поставщиков нет вовсе).</summary>
    Keep,

    /// <summary>В приёмке уже есть строки ДРУГОГО поставщика — спросить, добавить ли накладную, поставщик приёмки сменится.</summary>
    ConfirmSwitch,

    /// <summary>Накладная без поставщика, в приёмке есть строки поставщика — спросить, сбросить ли поставщика на «Без поставщика».</summary>
    ConfirmReset,

    /// <summary>Накладная без поставщика, строк в приёмке нет — поставщик приёмки сбрасывается молча (чужой поставщик из прошлой накладной не остаётся).</summary>
    Reset,
}

public static class InvoiceSupplierRule
{
    /// <summary><paramref name="chosen"/> — поставщик, выбранный/сопоставленный в окне накладной (null — «Без поставщика» или не найден); <paramref name="current"/> — поставщик
    /// приёмки сейчас (null — не выбран); <paramref name="existingLines"/> — сколько строк уже лежит в приёмке.</summary>
    public static ReceivingSupplierAction Decide(PurchaseReceivingService.Supplier? chosen, PurchaseReceivingService.Supplier? current, int existingLines)
    {
        var hasCurrent = current is not null && !string.IsNullOrEmpty(current.Id);
        if (chosen is not null)
            return hasCurrent && existingLines > 0 && !string.Equals(current!.Id, chosen.Id, StringComparison.OrdinalIgnoreCase) ? ReceivingSupplierAction.ConfirmSwitch : ReceivingSupplierAction.Keep;
        if (!hasCurrent)
            return ReceivingSupplierAction.Keep;
        return existingLines > 0 ? ReceivingSupplierAction.ConfirmReset : ReceivingSupplierAction.Reset;
    }
}

/// <summary>2026-10-27 (ревью Ск-11): сравнение единицы измерения накладной с единицей товара в кассе (упаковка / штука / вес / объём).</summary>
public static class InvoiceUnits
{
    private static string Canon(string? unit)
    {
        var k = ProductImportMapping.Key(unit);
        if (k.Length == 0)
            return "";
        if (k is "шт" or "штука" or "штук" or "pcs" or "pc" or "piece" or "ед" or "единица" or "дана" or "dona" or "adet")
            return "pcs";
        if (k is "кг" or "килограмм" or "kg")
            return "kg";
        if (k is "г" or "гр" or "грамм" or "g" or "gr")
            return "g";
        if (k is "л" or "литр" or "l" or "lt")
            return "l";
        if (k is "мл" or "ml")
            return "ml";
        if (k is "м" or "метр" or "m")
            return "m";
        if (k.StartsWith("упак", StringComparison.Ordinal) || k is "уп" or "кор" or "коробка" or "ящ" or "ящик" or "пач" or "пачка" or "бл" or "блок" or "набор" or "pack" or "box" or "case" or "ctn" or "карт" or "картон" or "таңгак" or "qadoq")
            return "pack";
        return "";
    }

    /// <summary>true — обе единицы узнаны и это разные единицы (упаковка вместо штуки, г вместо кг…). Неизвестная единица не считается несовпадением.</summary>
    public static bool Differ(string? invoiceUnit, string? catalogUnit)
    {
        var a = Canon(invoiceUnit);
        var b = Canon(catalogUnit);
        return a.Length > 0 && b.Length > 0 && a != b;
    }
}

/// <summary>Итоги накладной: сумма строк по закупке против итога из файла.</summary>
public sealed record InvoiceTotals(double LinesTotal, double? FileTotal, bool Matches, double Difference, int Included, int Skipped);

public sealed class InvoicePlan
{
    private readonly InvoiceCatalogIndex _index;
    private readonly Func<string, GlobalProductInfo?> _directory;

    public InvoiceDocument Document { get; }
    public ObservableCollection<InvoicePlanRow> Rows { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Закупочная цена — с НДС (по умолчанию без: «Цена за ед.» накладной).</summary>
    public bool PricesWithVat { get; private set; }

    /// <summary>Наценка для строк, у которых нет цены продажи (новые товары), %. null — не применять.</summary>
    public double? DefaultMarkup { get; private set; }

    /// <summary>Новым товарам без штрихкода присвоить внутренний код (иначе — без штрихкода). Менять — <see cref="SetGenerateBarcodes"/>.</summary>
    public bool GenerateBarcodes { get; private set; } = true;

    /// <summary>Штрихкоды, уже занятые вне накладной (открытая приёмка) — чтобы внутренний код не повторился.</summary>
    public Func<IEnumerable<string?>> ExternalCodes { get; set; } = () => [];

    private readonly HashSet<ReceivingLineVm> _autoCoded = [];

    /// <summary>Включить/выключить внутренние коды: строки показывают присвоенный код сразу (его видно в таблице), выключение убирает только выданные нами.</summary>
    public void SetGenerateBarcodes(bool on)
    {
        GenerateBarcodes = on;
        ApplyGeneratedBarcodes();
    }

    private void ApplyGeneratedBarcodes()
    {
        var rows = Rows.Where(r => r.Included && r.IsNew).ToList();
        if (!GenerateBarcodes)
        {
            foreach (var row in rows.Where(r => _autoCoded.Contains(r.Line)))
            {
                row.Line.Barcode = "";
                _autoCoded.Remove(row.Line);
            }
            return;
        }

        var taken = new List<string?>(ExternalCodes());
        taken.AddRange(Rows.Select(r => r.Line.Barcode));
        foreach (var row in rows.Where(r => string.IsNullOrWhiteSpace(r.Line.Barcode) && string.IsNullOrWhiteSpace(r.Line.ProductId)))
        {
            row.Line.Barcode = BarcodeGenerator.GenerateForCatalog(taken);
            taken.Add(row.Line.Barcode);
            _autoCoded.Add(row.Line);
        }
    }

    public InvoicePlan(InvoiceDocument document, InvoiceCatalogIndex index, Func<string, GlobalProductInfo?>? directoryLookup = null)
    {
        Document = document;
        _index = index;
        _directory = directoryLookup ?? DefaultDirectory;
    }

    private static GlobalProductInfo? DefaultDirectory(string barcode)
    {
        try
        {
            return GlobalProductCatalog.Instance.Find(barcode);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: справочник NurCRM недоступен: {ex.Message}", "WARNING");
            return null;
        }
    }

    public static CatalogProductTileVm[] SnapshotCatalog()
    {
        try
        {
            return CatalogCacheService.Products.ToArray();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ построение

    public static InvoicePlan Build(InvoiceDocument doc, IReadOnlyList<CatalogProductTileVm>? catalog = null, Func<string, GlobalProductInfo?>? directoryLookup = null)
    {
        var plan = new InvoicePlan(doc, new InvoiceCatalogIndex(catalog ?? SnapshotCatalog()), directoryLookup);
        plan.BuildRows();
        return plan;
    }

    private void BuildRows()
    {
        Rows.Clear();
        var merged = new Dictionary<string, InvoicePlanRow>(StringComparer.Ordinal);
        foreach (var item in Document.Items)
        {
            var match = _index.Match(item);
            var row = CreateRow(item, match);

            // Повтор того же товара в накладной — одна строка (количество складывается).
            // 2026-10-27 (ревью Ск-3): строка с совпадением, требующим «Это он» (похожий товар, артикул при непохожем названии), не склеивается ни с чем и ни во что:
            // раньше она вливалась по id в строку, найденную по штрихкоду, и количество «похожего» товара зачислялось точному БЕЗ подтверждения.
            var key = row.Match.NeedsConfirm || row.UnitWarning ? "" : MergeKey(row);
            if (key.Length > 0 && row.Included && merged.TryGetValue(key, out var first) && Math.Abs(first.Line.PurchasePrice - row.Line.PurchasePrice) < 0.0001)
            {
                first.Line.Quantity += row.Line.Quantity;
                first.MergedFrom.Add(item.SourceRow);
                first.Item.Warnings.Add(Tr.T($"сложено со строкой {item.SourceRow}", $"{item.SourceRow}-сап менен кошулду", $"merged with row {item.SourceRow}",
                    $"{item.SourceRow}. satırla birleştirildi", $"{item.SourceRow}-qator bilan qo'shildi"));
                continue;
            }
            if (key.Length > 0 && row.Included)
                merged.TryAdd(key, row);
            Rows.Add(row);
        }

        ApplyGeneratedBarcodes();
        CollectWarnings();
    }

    private static string MergeKey(InvoicePlanRow row)
    {
        if (row.Line.ProductId is { Length: > 0 } id)
            return "id:" + id;
        if (row.Line.Barcode.Length > 0)
            return "bc:" + BulkImportPlan.Canon(row.Line.Barcode);
        var name = InvoiceCatalogIndex.RawKey(row.Line.ProductName);
        return name.Length > 0 ? "nm:" + name + "|" + row.Line.Unit : "";
    }

    private InvoicePlanRow CreateRow(InvoiceItem item, InvoiceMatch match)
    {
        var original = item.UnitPrice(PricesWithVat);
        var purchase = RoundPurchase(original);
        if (match.Tile is { } tile && match.Kind != InvoiceMatchKind.None)
        {
            var line = PurchaseReceivingService.FromTile(tile, item.Quantity);
            ApplyPrices(line, item, purchase);
            var status = InvoicePlanRow.FoundStatusOf(match.Kind);
            if (item.Quantity <= 0)
                status = InvoiceRowStatus.Unrecognized;
            var foundRow = new InvoicePlanRow(item, match, line, status, null);
            if (purchase != original)
                foundRow.SetPriceRounding(original, purchase);
            return foundRow;
        }

        var directory = item.Barcode.Length > 0 ? _directory(item.Barcode) : null;
        var fresh = BuildNewLine(item, directory);
        ApplyPrices(fresh, item, purchase);
        InvoiceRowStatus st;
        if (item.Quantity <= 0 || string.IsNullOrWhiteSpace(fresh.ProductName))
            st = InvoiceRowStatus.Unrecognized;
        else
            st = directory is not null ? InvoiceRowStatus.NewFromDirectory : InvoiceRowStatus.NewManual;
        var row = new InvoicePlanRow(item, match, fresh, st, directory);
        if (purchase != original)
            row.SetPriceRounding(original, purchase);
        return row;
    }

    /// <summary>2026-10-31: цена закупки до копеек (сервер хранит 2 знака) — для загрузки из файла; ручной ввод в приёмке по-прежнему блокируется (PurchaseReceivingService.Validate).</summary>
    private static double RoundPurchase(double price) =>
        price > 0 && price < 1e9 ? (double)Math.Round((decimal)price, ReceivingPricing.PurchaseDecimals, MidpointRounding.AwayFromZero) : price;

    /// <summary>Строка нового товара из накладной (название из накладной; нет его — из справочника NurCRM).</summary>
    public ReceivingLineVm BuildNewLine(InvoiceItem item) => BuildNewLine(item, item.Barcode.Length > 0 ? _directory(item.Barcode) : null);

    private ReceivingLineVm BuildNewLine(InvoiceItem item, GlobalProductInfo? directory)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? directory?.Name ?? "" : item.Name;
        if (name.Length > 255)
            name = name[..255];
        return new ReceivingLineVm
        {
            Barcode = item.Barcode,
            ProductName = name,
            Unit = BulkImportPlan.NormalizeUnit(item.Unit, out _),
            // Как в «Массовом добавлении»: новый товар создаётся обычной карточкой; Unknown — штрихкод проверяется при проведении.
            Source = ReceivingSource.Unknown,
            Quantity = item.Quantity,
            CategoryName = string.IsNullOrWhiteSpace(directory?.Category) ? null : directory!.Category,
        };
    }

    private void ApplyPrices(ReceivingLineVm line, InvoiceItem item, double purchase)
    {
        line.PurchasePrice = purchase;
        if (item.RetailPrice is > 0)
            line.SalePrice = item.RetailPrice.Value;
        ApplyMarkup(line);
    }

    private void ApplyMarkup(ReceivingLineVm line)
    {
        if (line.SalePrice <= 0 && line.PurchasePrice > 0 && DefaultMarkup is { } m)
            line.SalePrice = ReceivingPricing.SaleFromMarkup(line.PurchasePrice, m);
    }

    // ------------------------------------------------------------------ настройки плана

    /// <summary>Переключить «закупка с НДС / без НДС»: цены закупки всех строк пересчитываются из накладной (ручные правки цены сбрасываются).</summary>
    public void SetPricesWithVat(bool withVat)
    {
        if (PricesWithVat == withVat)
            return;
        PricesWithVat = withVat;
        foreach (var row in Rows)
        {
            var original = row.Item.UnitPrice(withVat);
            var price = RoundPurchase(original); // 2026-10-31: до копеек, как при загрузке
            if (price > 0)
            {
                row.Line.PurchasePrice = price;
                row.SetPriceRounding(price != original ? original : double.NaN, price);
            }
        }
    }

    /// <summary>Наценка для строк без цены продажи (новые товары): цена продажи = закупка × (1 + %).</summary>
    public void SetDefaultMarkup(double? percent)
    {
        DefaultMarkup = percent is > 0 ? percent : null;
        foreach (var row in Rows)
        {
            if (row.IsNew && row.Item.RetailPrice is not > 0)
            {
                if (DefaultMarkup is { } m && row.Line.PurchasePrice > 0)
                    row.Line.SalePrice = ReceivingPricing.SaleFromMarkup(row.Line.PurchasePrice, m);
            }
            else if (row.Line.SalePrice <= 0)
                ApplyMarkup(row.Line);
        }
    }

    // ------------------------------------------------------------------ итоги и замечания

    public InvoiceTotals Totals()
    {
        var included = Rows.Where(r => r.Included).ToList();
        var sum = included.Sum(r => r.Line.LineTotal);
        var file = Document.Header.TotalFromFile;
        var net = Document.Header.NetTotalFromFile;
        var matches = true;
        var diff = 0.0;
        double? reference = null;
        if (file is not null || net is not null)
        {
            reference = new[] { file, net }.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => Math.Abs(v - sum)).First();
            diff = Math.Round(sum - reference.Value, 2);
            matches = Math.Abs(reference.Value - sum) <= Math.Max(1.0, reference.Value * 0.0005);
        }
        return new InvoiceTotals(Math.Round(sum, 2), reference, matches, diff, included.Count, Rows.Count - included.Count);
    }

    private void CollectWarnings()
    {
        Warnings.Clear();
        var h = Document.Header;
        ForeignCurrency = InvoiceText.IsForeignCurrency(h.Currency);
        TotalMismatch = false;
        if (Document.Items.Count(i => i.OnlyGrossPrice) is var grossOnly and > 0)
            Warnings.Add(Tr.T($"В файле только цены с НДС ({grossOnly} {Tr.Plural(grossOnly, "строка", "строки", "строк")}) — закупка взята с НДС (ставка неизвестна или цены без НДС нет). Проверьте цены.",
                $"Файлда НДС менен гана баалар бар ({grossOnly} сап) — сатып алуу НДС менен алынды. Бааларды текшериңиз.",
                $"The file only has prices including VAT ({grossOnly} lines) — purchase prices are taken with VAT. Check the prices.",
                $"Dosyada yalnızca KDV'li fiyatlar var ({grossOnly} satır) — alış fiyatı KDV'li alındı. Fiyatları kontrol edin.",
                $"Faylda faqat QQS bilan narxlar bor ({grossOnly} qator) — xarid narxi QQS bilan olindi. Narxlarni tekshiring."));
        if (ForeignCurrency)
            Warnings.Add(Tr.T($"Валюта накладной — {h.Currency}, касса работает в сомах. Цены не пересчитываются — проверьте их перед приёмкой.",
                $"Накладнойдун валютасы — {h.Currency}, касса сом менен иштейт. Баалар кайра эсептелген жок — кабыл алуудан мурун текшериңиз.",
                $"The invoice currency is {h.Currency}, the till works in som. Prices are not converted — check them before receiving.",
                $"Fatura para birimi {h.Currency}, kasa som ile çalışır. Fiyatlar çevrilmez — mal kabulünden önce kontrol edin.",
                $"Yuk xati valyutasi — {h.Currency}, kassa so'mda ishlaydi. Narxlar qayta hisoblanmaydi — qabuldan oldin tekshiring."));
        if (Document.AnyVat)
            Warnings.Add(Tr.T("В накладной есть НДС. Закупка по умолчанию — цена за единицу без НДС; переключатель «Закупка с НДС» пересчитает цены.",
                "Накладнойдо НДС бар. Сатып алуу баасы демейки НДСсиз; «НДС менен» которгучу бааларды кайра эсептейт.",
                "The invoice has VAT. Purchase price defaults to the unit price without VAT; the “Purchase incl. VAT” switch recalculates prices.",
                "Faturada KDV var. Alış fiyatı varsayılan olarak KDV'siz birim fiyattır; «KDV'li alış» anahtarı fiyatları yeniden hesaplar.",
                "Yuk xatida QQS bor. Xarid narxi odatda QQSsiz birlik narxi; «QQS bilan» tugmasi narxlarni qayta hisoblaydi."));
        foreach (var w in Document.Warnings)
            Warnings.Add(w);

        // Сумма строк против итога файла — на уровне самой накладной (до правок кассира).
        var fileTotal = h.TotalFromFile;
        var net = h.NetTotalFromFile;
        if ((fileTotal ?? net) is not null)
        {
            var gross = Document.LinesTotal(true);
            var netSum = Document.LinesTotal(false);
            bool Close(double a, double? b) => b is not null && Math.Abs(a - b.Value) <= Math.Max(1.0, b.Value * 0.0005);
            if (!(Close(gross, fileTotal) || Close(netSum, fileTotal) || Close(netSum, net) || Close(gross, net)))
            {
                TotalMismatch = true;
                Warnings.Add(Tr.T($"Сумма строк ({netSum:N2}) не совпала с итогом накладной ({(fileTotal ?? net)!.Value:N2}) — возможно, часть строк не прочитана.",
                    $"Саптардын суммасы ({netSum:N2}) накладнойдун жыйынтыгына ({(fileTotal ?? net)!.Value:N2}) дал келген жок — кээ бир саптар окулбай калышы мүмкүн.",
                    $"The sum of lines ({netSum:N2}) does not match the invoice total ({(fileTotal ?? net)!.Value:N2}) — some lines may not have been read.",
                    $"Satırlar toplamı ({netSum:N2}) fatura toplamıyla ({(fileTotal ?? net)!.Value:N2}) uyuşmuyor — bazı satırlar okunmamış olabilir.",
                    $"Qatorlar yig'indisi ({netSum:N2}) yuk xati jami ({(fileTotal ?? net)!.Value:N2}) bilan mos kelmadi — ba'zi qatorlar o'qilmagan bo'lishi mumkin."));
            }
        }
    }

    /// <summary>2026-10-27 (ревью Ск-1/Ск-14): сумма строк не сошлась с итогом файла — возможно, часть строк не прочитана. Окно требует отметки «Всё равно добавить».</summary>
    public bool TotalMismatch { get; private set; }

    /// <summary>2026-10-27 (ревью Ск-14): валюта накладной не сом (рубль, доллар…) — цены НЕ пересчитываются; окно требует подтверждения.</summary>
    public bool ForeignCurrency { get; private set; }

    // ------------------------------------------------------------------ перенос в приёмку

    /// <summary>Сколько строк ждут подтверждения «Это он».</summary>
    public int UnconfirmedCount => Rows.Count(r => r.NeedsConfirm);

    public int UnrecognizedCount => Rows.Count(r => !r.Included);

    /// <summary>Строки для приёмки: только годные (не «Не распознан»). Новым товарам без штрихкода — внутренний код (если выбрано).
    /// <paramref name="takenCodes"/> — штрихкоды, уже занятые в открытой приёмке (если не заданы, берутся из <see cref="ExternalCodes"/>).</summary>
    public List<ReceivingLineVm> ToReceivingLines(IEnumerable<string?>? takenCodes = null)
    {
        if (takenCodes is not null)
        {
            var fixedCodes = takenCodes.ToList();
            ExternalCodes = () => fixedCodes;
        }
        ApplyGeneratedBarcodes();
        // 2026-10-27 (ревью Ск-3/Ск-4): строка с неподтверждённым совпадением в приёмку не идёт никогда (окно этого не допускает кнопкой, это вторая защита на пути «Добавить»).
        var unconfirmed = Rows.Count(r => r.Included && r.NeedsConfirm);
        if (unconfirmed > 0)
            PosLogger.Log($"INVOICE_IMPORT: {unconfirmed} строк с неподтверждённым совпадением не перенесены в приёмку.", "WARNING");
        return Rows.Where(r => r.Included && !r.NeedsConfirm).Select(r => r.Line).ToList();
    }
}
