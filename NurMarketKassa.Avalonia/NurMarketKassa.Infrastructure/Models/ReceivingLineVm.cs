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
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(PurchasePriceText));
            }
        }
    }

    public double SalePrice
    {
        get => _salePrice;
        set
        {
            if (Set(ref _salePrice, value))
                OnPropertyChanged(nameof(SalePriceText));
        }
    }

    // Ячейки таблицы правятся текстом (2026-09-26, «разрешить редактирование»): прямая привязка
    // к double разбирала число без учёта запятой — «2,5» или «1 234,50» молча откатывались к
    // прежнему значению, и казалось, что таблица не редактируется. Непонятное число оставляет
    // прежнее значение.
    public string QuantityText
    {
        get => Quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => Quantity = v, nameof(QuantityText));
    }

    public string PurchasePriceText
    {
        get => PurchasePrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => PurchasePrice = v, nameof(PurchasePriceText));
    }

    public string SalePriceText
    {
        get => SalePrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set => SetFromText(value, v => SalePrice = v, nameof(SalePriceText));
    }

    private void SetFromText(string? text, Action<double> apply, string name)
    {
        if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(text ?? "", out var value) && value >= 0)
            apply(value);
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

    public double LineTotal => Quantity * PurchasePrice;

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
