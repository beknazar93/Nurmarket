using System.Globalization;
using System.Windows.Input;

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
    public string Title { get; init; } = "";
    public string Unit { get; init; } = "шт";
    public double UnitPrice { get; init; }
    public bool IsWeight { get; init; }

    public ICommand? RemoveCommand { get; init; }
    public ICommand? IncreaseCommand { get; init; }
    public ICommand? DecreaseCommand { get; init; }
    public ICommand? WeighCommand { get; init; }
    public ICommand? DiscountCommand { get; init; }

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
        }
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
            return $"-{amount.ToString("0.##", CultureInfo.InvariantCulture)} сом";
        }
    }

    public string NumberedTitle => $"#{ItemNumber}. {Title}";

    public string QuantityDisplay => IsWeight
        ? Quantity.ToString("0.000", CultureInfo.InvariantCulture)
        : Quantity.ToString("0.###", CultureInfo.InvariantCulture);

    public string UnitPriceDisplay => $"{UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом";

    /// <summary>Подпись вида «0.500 кг × 115.71 сом» или «1 шт × 150.00 сом».</summary>
    public string PriceQuantityLine =>
        $"{QuantityDisplay} {Unit} × {UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)} сом";

    public string LineTotalDisplay => $"{LineTotal.ToString("0.00", CultureInfo.InvariantCulture)} сом";
    public string LineTotalAmount => LineTotal.ToString("0.00", CultureInfo.InvariantCulture);
}
