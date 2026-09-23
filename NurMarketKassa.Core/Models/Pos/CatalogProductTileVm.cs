using System.ComponentModel;
using System.Runtime.CompilerServices;

#nullable enable

namespace NurMarketKassa.Models.Pos;

/// <summary>
/// Catalog product tile view-model (UI-agnostic). Image paths are resolved by host converters.
/// Color properties expose hex strings; hosts convert them to brushes via HexToBrushConverter.
/// </summary>
public sealed class CatalogProductTileVm : INotifyPropertyChanged
{
    public const double LowStockQuantityThreshold = 10.0;

    private string? _barcode;
    private string? _stockInfo;
    private bool _isFavorite;
    private bool _isUnitInvalid;
    private bool _isLowStock;
    private double _quantity;
    private string? _productImagePath;
    private bool _mustWeigh;
    private string? _unit;

    public CatalogProductTileVm(
        string id,
        string title,
        string priceLine,
        bool mustWeigh,
        string? imageUrl = null)
    {
        Id = id;
        Title = title;
        PriceLine = priceLine;
        _mustWeigh = mustWeigh;
        ImageUrl = imageUrl;
        OnPropertyChanged(nameof(MustWeigh));
        OnPropertyChanged(nameof(IsWeighted));
        OnPropertyChanged(nameof(TypeBadgeBackground));
        OnPropertyChanged(nameof(TypeBadgeText));
    }

    public string? Category { get; set; }
    public string? Brand { get; set; }
    public string Id { get; }
    public string Title { get; }
    public string PriceLine { get; }

    public bool MustWeigh
    {
        get => _mustWeigh;
        set
        {
            if (_mustWeigh == value)
                return;
            _mustWeigh = value;
            OnPropertyChanged(nameof(MustWeigh));
            NotifyTypeBadgeChanged();
        }
    }

    /// <summary>Remote catalog image URL (API).</summary>
    public string? ImageUrl { get; }

    /// <summary>
    /// Local file path or embedded asset name for UI binding (via <c>AssetPathToBitmapConverter</c>).
    /// </summary>
    public string? ProductImagePath
    {
        get => _productImagePath;
        set
        {
            if (_productImagePath == value)
                return;
            _productImagePath = value;
            OnPropertyChanged(nameof(ProductImagePath));
        }
    }

    public string? StatusDisplay { get; set; }
    public string? HotkeyGroupName { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ClientName { get; set; }
    public string? Status { get; set; }
    public string? HotkeyGroup { get; set; }

    public string? Barcode
    {
        get => _barcode;
        set
        {
            if (_barcode == value)
                return;
            _barcode = value;
            OnPropertyChanged(nameof(Barcode));
        }
    }

    public string? StockInfo
    {
        get => _stockInfo;
        set
        {
            _stockInfo = value;
            OnPropertyChanged(nameof(StockInfo));
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged(nameof(IsFavorite));
        }
    }

    public bool IsUnitInvalid
    {
        get => _isUnitInvalid;
        set
        {
            if (_isUnitInvalid == value)
                return;
            _isUnitInvalid = value;
            OnPropertyChanged(nameof(IsUnitInvalid));
        }
    }

    public double Quantity
    {
        get => _quantity;
        set
        {
            if (Math.Abs(_quantity - value) < double.Epsilon)
                return;
            _quantity = value;
            OnPropertyChanged(nameof(Quantity));
            OnPropertyChanged(nameof(StockForeground));
            var low = value < LowStockQuantityThreshold;
            if (_isLowStock != low)
            {
                _isLowStock = low;
                OnPropertyChanged(nameof(IsLowStock));
            }
        }
    }

    public bool IsLowStock
    {
        get => _isLowStock;
        set
        {
            if (_isLowStock == value)
                return;
            _isLowStock = value;
            OnPropertyChanged(nameof(IsLowStock));
        }
    }

    public double PurchasePrice { get; set; }

    public string? Unit
    {
        get => _unit;
        set
        {
            if (_unit == value)
                return;
            _unit = value;
            OnPropertyChanged(nameof(Unit));
            NotifyTypeBadgeChanged();
        }
    }

    /// <summary>Весовой товар (кг) или явно помечен как MustWeigh.</summary>
    public bool IsWeighted
    {
        get
        {
            if (_mustWeigh)
                return true;
            var unit = (_unit ?? string.Empty).Trim().ToLowerInvariant();
            return unit is "кг" or "kg";
        }
    }

    /// <summary>
    /// Hex-цвет остатка: красный при Quantity &lt; 10, иначе серый.
    /// Hosts bind via HexToBrushConverter → SolidColorBrush.
    /// </summary>
    public string StockForeground =>
        Quantity < LowStockQuantityThreshold ? "#DC2626" : "#64748B";

    /// <summary>Hex-фон плашки типа товара.</summary>
    public string TypeBadgeBackground =>
        IsWeighted ? "#16A34A" : "#F59E0B";

    public string TypeBadgeText =>
        IsWeighted ? "⚖️ Весовой" : "📦 Штучный";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyTypeBadgeChanged()
    {
        OnPropertyChanged(nameof(IsWeighted));
        OnPropertyChanged(nameof(TypeBadgeBackground));
        OnPropertyChanged(nameof(TypeBadgeText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
