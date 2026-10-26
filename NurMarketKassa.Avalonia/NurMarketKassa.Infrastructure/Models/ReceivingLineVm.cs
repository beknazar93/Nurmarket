using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NurMarketKassa.Models;

/// <summary>Откуда касса узнала товар строки приёмки — те же три колонки, что у «Массового
/// сканирования» сайта: свой склад, общая база CRM (товар узнан по штрихкоду, но у магазина
/// его ещё нет) и неизвестный штрихкод.</summary>
public enum ReceivingSource
{
    Warehouse,
    GlobalBase,
    Unknown,
}

/// <summary>Строка приёмки: сколько привезли и почём. Цена закупки и цена продажи обязательны —
/// без них приход нельзя провести (2026-09-24, просьба владельца: «обязательно цена приёмки и
/// цена продажи, как на складе»).</summary>
public sealed class ReceivingLineVm : INotifyPropertyChanged
{
    private string? _productId;
    private string _barcode = "";
    private string _productName = "";
    private string _unit = "шт";
    private ReceivingSource _source;
    private double _quantity;
    private double _purchasePrice;
    private double _salePrice;

    public string? ProductId
    {
        get => _productId;
        set => Set(ref _productId, value);
    }

    /// <summary>У отсканированной строки — код со сканера. Вписать руками можно только у нового
    /// товара, добавленного кнопкой «+ Новый товар» (2026-09-26).</summary>
    public string Barcode
    {
        get => _barcode;
        set => Set(ref _barcode, (value ?? "").Trim());
    }

    public string ProductName
    {
        get => _productName;
        set => Set(ref _productName, value ?? "");
    }

    public string Unit
    {
        get => _unit;
        set => Set(ref _unit, string.IsNullOrWhiteSpace(value) ? "шт" : value.Trim());
    }

    public ReceivingSource Source
    {
        get => _source;
        set
        {
            if (Set(ref _source, value))
            {
                OnPropertyChanged(nameof(SourceText));
                OnPropertyChanged(nameof(IsNew));
            }
        }
    }

    /// <summary>Остаток на складе до приёмки — для сверки с накладной.</summary>
    public double StockBefore { get; init; }

    public double Quantity
    {
        get => _quantity;
        set
        {
            if (Set(ref _quantity, value))
            {
                _quantityRaw = null;
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(QuantityText));
            }
        }
    }

    public double PurchasePrice
    {
        get => _purchasePrice;
        set
        {
            if (Set(ref _purchasePrice, value))
            {
                _purchaseRaw = null;
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(PurchasePriceText));
                NotifyPricing();
            }
        }
    }

    public double SalePrice
    {
        get => _salePrice;
        set
        {
            if (Set(ref _salePrice, value))
            {
                _saleRaw = null;
                OnPropertyChanged(nameof(SalePriceText));
                NotifyPricing();
            }
        }
    }

    // Ячейки таблицы правятся текстом (2026-09-26, «разрешить редактирование»): прямая привязка
    // к double разбирала число без учёта запятой — «2,5» или «1 234,50» молча откатывались к
    // прежнему значению, и казалось, что таблица не редактируется. Непонятное число оставляет
    // прежнее значение.
    // 2026-10-11, тестировщик (склад.md, 2.2.2): «невозможно стереть поле «Принято», можно только заменить». Пустое поле
    // не разбиралось как число и тут же возвращалось прежнее значение. Теперь пустое поле остаётся пустым (значение 0),
    // пока не впишут своё число; проведение без количества и цен по-прежнему не пускает.
    private string? _quantityRaw, _purchaseRaw, _saleRaw;

    public string QuantityText
    {
        get => _quantityRaw ?? Quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => Quantity = v, nameof(QuantityText), r => _quantityRaw = r);
    }

    public string PurchasePriceText
    {
        get => _purchaseRaw ?? PurchasePrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => PurchasePrice = v, nameof(PurchasePriceText), r => _purchaseRaw = r);
    }

    public string SalePriceText
    {
        get => _saleRaw ?? SalePrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => SalePrice = v, nameof(SalePriceText), r => _saleRaw = r);
    }

    private void SetFromText(string? text, Action<double> apply, string name, Action<string?> setRaw)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            apply(0);
            setRaw("");
        }
        else if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(text, out var value) && value >= 0)
        {
            apply(value);
            setRaw(null);
        }
        OnPropertyChanged(name);
    }

    /// <summary>Название и единица товара со склада на момент сканирования: если их поправили в
    /// приёмке, при проведении они уходят в карточку товара.</summary>
    public string OriginalName { get; init; } = "";
    public string OriginalUnit { get; init; } = "";

    public bool NameOrUnitChanged =>
        !IsNew
        && ((OriginalName.Length > 0 && !string.Equals(ProductName.Trim(), OriginalName.Trim(), StringComparison.Ordinal))
            || (OriginalUnit.Length > 0 && !string.Equals(Unit, OriginalUnit, StringComparison.Ordinal)));

    /// <summary>Цены товара на момент сканирования: по ним видно, менял ли кладовщик цену, и
    /// только тогда она уходит на сервер.</summary>
    public double OriginalPurchasePrice { get; init; }
    public double OriginalSalePrice { get; init; }

    /// <summary>2026-10-24 (Б0): поле markup_percent товара на сервере на момент сканирования (0 — не задано). Нужно только кнопке
    /// «Обновить цены продажи» как запасной источник прежней наценки, когда прежних цен для расчёта нет.</summary>
    public double OriginalMarkupPercent { get; init; }

    // ------------------------------------------------------------------ 2026-10-24 (Б0, ТЗ «Склад» 4.2–4.3): наценка и подорожание

    private string? _markupRaw;

    /// <summary>Наценка в % от закупки: round((продажа / закупка − 1) × 100, 2). Всегда считается из двух цен (отдельно не хранится), поэтому
    /// правка закупки при неизменной продаже пересчитывает наценку, а продажа не меняется сама. null — одной из цен нет.</summary>
    public double? MarkupPercent => NurMarketKassa.Services.ReceivingPricing.MarkupPercent(PurchasePrice, SalePrice);

    /// <summary>Ячейка «Наценка %» (правится текстом, как остальные цены). Ввод числа меняет ЦЕНУ ПРОДАЖИ: P × (1 + M/100), округление вверх
    /// до целого сома (ReceivingPricing.SalePriceRoundStep). Пустое поле цену не трогает; без закупки считать не из чего — ввод игнорируется.</summary>
    public string MarkupText
    {
        get => _markupRaw ?? (MarkupPercent is { } m ? m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                _markupRaw = "";
            else if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(value, out var markup) && markup >= 0)
            {
                _markupRaw = null;
                // 2026-10-24 (Б1, стенд приёмки): таблица сама «записывает» показанную наценку обратно при создании строки — равное текущему
                // значению не трогает цену продажи (иначе «410» превращалось в «411» из-за округления вверх при каждом показе строки).
                if (PurchasePrice > 0 && !(MarkupPercent is { } current && Math.Abs(current - markup) < 0.005))
                    SalePrice = NurMarketKassa.Services.ReceivingPricing.SaleFromMarkup(PurchasePrice, markup);
            }
            OnPropertyChanged(nameof(MarkupText));
        }
    }

    /// <summary>Закупка выше прежней больше чем на 0,5 % (прежняя цена известна) — «подорожало».</summary>
    public bool IsPurchaseRaised => NurMarketKassa.Services.ReceivingPricing.IsPurchaseRaised(OriginalPurchasePrice, PurchasePrice);

    /// <summary>«▲ +12 %» — на сколько подорожала закупка (пусто, если не подорожала).</summary>
    public string PurchaseRaiseText => IsPurchaseRaised
        ? "▲ +" + NurMarketKassa.Services.ReceivingPricing.PurchaseRaisePercent(OriginalPurchasePrice, PurchasePrice).ToString("0.#") + " %"
        : "";

    /// <summary>Закупка отличается от прежней (вверх или вниз) — под ценой мелко «было 45,00».</summary>
    public bool IsPurchaseChanged => OriginalPurchasePrice > 0 && Math.Abs(PurchasePrice - OriginalPurchasePrice) > 0.004;

    public string PreviousPurchaseText => IsPurchaseChanged
        ? NurMarketKassa.Services.Tr.T($"было {OriginalPurchasePrice:0.00}", $"мурда {OriginalPurchasePrice:0.00}", $"was {OriginalPurchasePrice:0.00}",
            $"önceki {OriginalPurchasePrice:0.00}", $"avval {OriginalPurchasePrice:0.00}")
        : "";

    /// <summary>Наценка ниже порога (ReceivingPricing.LowMarginPercent) или продажа ниже закупки — красная пометка.</summary>
    public bool IsLowMargin => NurMarketKassa.Services.ReceivingPricing.IsLowMargin(PurchasePrice, SalePrice);

    public string LowMarginText => IsLowMargin && MarkupPercent is { } m
        ? NurMarketKassa.Services.Tr.T($"маржа {m:0.#} %", $"маржа {m:0.#} %", $"margin {m:0.#}%", $"marj {m:0.#} %", $"marja {m:0.#} %")
        : "";

    /// <summary>Кнопка «Обновить цены продажи» может поправить эту строку: закупка подорожала, а продажу не трогали.</summary>
    public bool CanRaiseSalePrice => NurMarketKassa.Services.ReceivingPricing.IsRaisable(this);

    private void NotifyPricing()
    {
        _markupRaw = null;
        OnPropertyChanged(nameof(MarkupPercent));
        OnPropertyChanged(nameof(MarkupText));
        OnPropertyChanged(nameof(IsPurchaseRaised));
        OnPropertyChanged(nameof(PurchaseRaiseText));
        OnPropertyChanged(nameof(IsPurchaseChanged));
        OnPropertyChanged(nameof(PreviousPurchaseText));
        OnPropertyChanged(nameof(IsLowMargin));
        OnPropertyChanged(nameof(LowMarginText));
        OnPropertyChanged(nameof(CanRaiseSalePrice));
    }

    public double LineTotal => Quantity * PurchasePrice;

    /// <summary>2026-10-19: закупка по скану накладной (InvoiceEditorWindow) — категория и весовой товар у нового товара, как раньше
    /// давал скан при создании товара напрямую. Приёмка склада их не задаёт (null / false — как прежде).</summary>
    public string? CategoryName { get; set; }
    public bool IsWeight { get; set; }

    /// <summary>Товара у магазина ещё нет — он будет создан при проведении приёмки.</summary>
    public bool IsNew => Source != ReceivingSource.Warehouse;

    public string SourceText => Source switch
    {
        ReceivingSource.GlobalBase => NurMarketKassa.Services.Tr.T("База CRM", "CRM базасы", "CRM catalog", "CRM veritabanı", "CRM bazasi"),
        ReceivingSource.Unknown => NurMarketKassa.Services.Tr.T("Новый", "Жаңы", "New", "Yeni", "Yangi"),
        _ => NurMarketKassa.Services.Tr.T("На складе", "Кампада", "In stock", "Depoda", "Omborda"),
    };

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-70): приёмка сеткой «цвет × размер». Сколько пришло каждого
    /// размера (id варианта NurCRM → штук). Сервер держит остаток товара равным сумме остатков размеров (проверено 06.10:
    /// +2 размеру — +2 товару), поэтому при проведении прибавляются остатки размеров, а количество строки = их сумма.</summary>
    public Dictionary<string, double>? VariantQuantities { get; private set; }

    /// <summary>Подписи размеров строки (id варианта → «44, Чёрный») — для таблицы и этикеток.</summary>
    public Dictionary<string, string>? VariantLabels { get; private set; }

    public double VariantTotal => VariantQuantities?.Values.Sum() ?? 0;

    /// <summary>«44/Чёрный ×2, 46/Красный ×1» — колонка «Размеры» в приёмке (коротко, чтобы помещалось).</summary>
    public string VariantSummary => VariantQuantities is not { Count: > 0 } q
        ? ""
        : string.Join(", ", q.Where(kv => kv.Value > 0).Select(kv =>
            (VariantLabels != null && VariantLabels.TryGetValue(kv.Key, out var l) ? l.Replace(", ", "/") : "?") + " ×" + kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Задать размеры из сетки: количество строки становится их суммой.</summary>
    public void SetVariants(Dictionary<string, double> quantities, Dictionary<string, string> labels)
    {
        VariantQuantities = quantities.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        VariantLabels = new Dictionary<string, string>(labels, StringComparer.OrdinalIgnoreCase);
        Quantity = VariantTotal;
        OnPropertyChanged(nameof(VariantSummary));
    }

    /// <summary>+N к одному размеру (скан этикетки размера в приёмке).</summary>
    public void AddVariant(string variantId, string label, double quantity)
    {
        VariantQuantities ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        VariantLabels ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        VariantQuantities[variantId] = VariantQuantities.GetValueOrDefault(variantId) + quantity;
        VariantLabels[variantId] = label;
        Quantity = VariantTotal;
        OnPropertyChanged(nameof(VariantSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
