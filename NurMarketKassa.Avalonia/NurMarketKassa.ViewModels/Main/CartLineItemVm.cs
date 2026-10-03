using System.Globalization;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Этот файл описывает одну строку чека для панели корзины Avalonia-кассы:
/// название товара, количество, цену и сумму позиции.
/// </summary>
public sealed class CartLineItemVm : ViewModelBase
{
    private double _quantity;
    private double _lineTotal;
    private double? _maxStockQuantity;
    private double _discountAmount;
    private double? _discountPercent;
    private double? _fixedDiscountAmount;

    public int ItemNumber { get; init; }
    public string ItemId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string Unit { get; init; } = "шт";
    public double UnitPrice { get; init; }
    public bool IsWeight { get; init; }

    /// <summary>Заполнен, если строка продаётся поштучно из пачки — тогда Quantity в ШТУКАХ,
    /// а остаток товара в каталоге хранится в ПАЧКАХ.</summary>
    public string? SalePackageId { get; init; }

    public ICommand? RemoveCommand { get; init; }
    public ICommand? IncreaseCommand { get; init; }
    public ICommand? DecreaseCommand { get; init; }
    public ICommand? WeighCommand { get; init; }
    public ICommand? DiscountCommand { get; init; }
    public ICommand? SetQuantityCommand { get; init; }
    /// <summary>2026-10-03: строка ↔ оптовая цена.</summary>
    public ICommand? WholesaleCommand { get; init; }
    public bool IsWholesale { get; init; }

    private bool _canWholesale;

    /// <summary>У товара есть оптовая цена (или строка уже по опту) — кнопка «Опт» видна.</summary>
    public bool CanWholesale
    {
        get => _canWholesale;
        set => SetProperty(ref _canWholesale, value);
    }

    public string WholesaleButtonText => IsWholesale
        ? Tr.T("Опт ✓", "Дүң ✓", "Wholesale ✓", "Toptan ✓", "Ulgurji ✓")
        : Tr.T("Опт", "Дүң", "Wholesale", "Toptan", "Ulgurji");

    private string _quantityInput = "";

    public double Quantity
    {
        get => _quantity;
        set
        {
            if (!SetProperty(ref _quantity, value))
                return;
            LineTotal = UnitPrice * _quantity;
            OnPropertyChanged(nameof(QuantityDisplay));
            OnPropertyChanged(nameof(PriceQuantityLine));
            OnPropertyChanged(nameof(CanIncrease));
            QuantityInput = QuantityDisplay;
        }
    }

    /// <summary>Editable text for the cart-row quantity box; committed via SetQuantityCommand
    /// (Enter or losing focus), not applied to Quantity directly while the cashier is typing.</summary>
    public string QuantityInput
    {
        get => _quantityInput;
        set => SetProperty(ref _quantityInput, value);
    }

    /// <summary>Maximum quantity permitted in this receipt after other reservations.</summary>
    public double? MaxStockQuantity
    {
        get => _maxStockQuantity;
        set
        {
            if (!SetProperty(ref _maxStockQuantity, value))
                return;
            OnPropertyChanged(nameof(CanIncrease));
        }
    }

    public bool CanIncrease =>
        !MaxStockQuantity.HasValue || Quantity + (IsWeight ? 0.1 : 1) <= MaxStockQuantity.Value + 1e-6;

    public double LineTotal
    {
        get => _lineTotal;
        set
        {
            if (!SetProperty(ref _lineTotal, value))
                return;
            OnPropertyChanged(nameof(LineTotalDisplay));
            OnPropertyChanged(nameof(LineTotalAmount));
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public double DiscountAmount
    {
        get => _discountAmount;
        set
        {
            if (!SetProperty(ref _discountAmount, value))
                return;
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public bool HasDiscount =>
        DiscountAmount > 1e-6 || LineTotal + 1e-6 < UnitPrice * Quantity;

    public double? DiscountPercent
    {
        get => _discountPercent;
        set
        {
            if (!SetProperty(ref _discountPercent, value))
                return;
            OnPropertyChanged(nameof(IsPercentageDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public double? FixedDiscountAmount
    {
        get => _fixedDiscountAmount;
        set
        {
            if (!SetProperty(ref _fixedDiscountAmount, value))
                return;
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public bool IsPercentageDiscount => DiscountPercent is > 1e-6;

    public string DiscountDisplayText
    {
        get
        {
            if (!HasDiscount)
                return string.Empty;

            if (DiscountPercent is { } percent && percent > 1e-6)
                return $"-{percent.ToString("0.##", CultureInfo.InvariantCulture)}%";

            var amount = FixedDiscountAmount is > 1e-6
                ? FixedDiscountAmount.Value
                : DiscountAmount;
            return $"-{amount.ToString("0.##", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}";
        }
    }

    public string NumberedTitle => $"#{ItemNumber}. {Title}";

    public string QuantityDisplay => IsWeight
        ? Quantity.ToString("0.000", CultureInfo.InvariantCulture)
        : Quantity.ToString("0.###", CultureInfo.InvariantCulture);

    public string UnitPriceDisplay => $"{UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}";

    /// <summary>2026-10-02: обычная цена, если вариант продаётся по акции (см. CartItem.PromoBasePrice).</summary>
    public double? PromoBasePrice { get; init; }

    /// <summary>Подпись вида «0.500 кг × 115.71 сом» или «1 шт × 150.00 сом»; у варианта по акции —
    /// «1 шт × 2200.00 сом · было 2500.00 (−12%)».</summary>
    public string PriceQuantityLine =>
        $"{QuantityDisplay} {Unit} × {UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}"
        + (PromoBasePrice is { } basePrice && basePrice > UnitPrice + 0.005
            ? " · " + Tr.T("было", "болгон", "was", "önce", "edi") + $" {basePrice.ToString("0.00", CultureInfo.InvariantCulture)} (−{Math.Round((1 - UnitPrice / basePrice) * 100):0}%)"
            : "");

    // 2026-10-03, владелец: «при скидке, если в убыток продаёт, — на экране в корзине рядом с товаром показывать,
    // сколько убытка». Считает BasketPanelViewModel.UpdateLossMarks: закупка × количество − сумма строки
    // после скидки на позицию и её доли скидки на весь чек.
    private double _lossAmount;
    private double _unitCost;

    public double LossAmount
    {
        get => _lossAmount;
        set
        {
            if (!SetProperty(ref _lossAmount, value))
                return;
            OnPropertyChanged(nameof(HasLoss));
            OnPropertyChanged(nameof(LossDisplayText));
            OnPropertyChanged(nameof(LossTooltip));
        }
    }

    /// <summary>Закупочная цена за единицу строки (за штуку при продаже поштучно из пачки).</summary>
    public double UnitCost
    {
        get => _unitCost;
        set
        {
            if (SetProperty(ref _unitCost, value))
                OnPropertyChanged(nameof(LossTooltip));
        }
    }

    public bool HasLoss => LossAmount > 0.005;

    public string LossDisplayText => HasLoss
        ? Tr.T("убыток", "зыян", "loss", "zarar", "zarar") + $" {LossAmount.ToString("0.00", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}"
        : "";

    public string LossTooltip => HasLoss
        ? Tr.T($"Продаётся дешевле закупки: закупка {UnitCost:0.00} × {QuantityDisplay}, со скидкой — {LossAmount:0.00} сом убытка.",
            $"Сатып алуу баасынан арзан сатылат: сатып алуу {UnitCost:0.00} × {QuantityDisplay}, арзандатуу менен — {LossAmount:0.00} сом зыян.",
            $"Sold below cost: cost {UnitCost:0.00} × {QuantityDisplay}, with the discount — {LossAmount:0.00} som loss.",
            $"Alış fiyatının altında satılıyor: alış {UnitCost:0.00} × {QuantityDisplay}, indirimle — {LossAmount:0.00} som zarar.",
            $"Xarid narxidan arzon sotilmoqda: xarid {UnitCost:0.00} × {QuantityDisplay}, chegirma bilan — {LossAmount:0.00} so'm zarar.")
        : "";

    public string LineTotalDisplay =>$"{LineTotal.ToString("0.00", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}";
    public string LineTotalAmount => LineTotal.ToString("0.00", CultureInfo.InvariantCulture);
}
