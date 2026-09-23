using System.ComponentModel;
using System.Runtime.CompilerServices;

#nullable enable

namespace NurMarketKassa.Models.Pos;

/// <summary>Поштучная продажа из упаковки (поле "packages" в карточке товара NurCRM).</summary>
public sealed class ProductPackageOption
{
    /// <summary>ID самой упаковки на сервере — при добавлении в корзину нужно передавать
    /// именно его ("sale_package_id"), а не выставлять unit_price вручную: сервер сам
    /// подставляет цену пачки и не спотыкается о проверку "не ниже закупочной", которая
    /// применяется только при ручном оверрайде unit_price на базовом товаре.</summary>
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public double QuantityInPackage { get; init; }
    public string Unit { get; init; } = "";
    public double PieceUnitPrice { get; init; }
}

/// <summary>Один товар из состава комплекта (карточка "Комплект" в Складе/на сайте) — сам
/// товар и сколько его штук/кг входит в набор.</summary>
public sealed class BundleComponent
{
    public string ProductId { get; init; } = "";
    /// <summary>Название на момент добавления в комплект — чтобы показать состав, даже если
    /// сам компонент потом переименуют/удалят из каталога.</summary>
    public string ProductName { get; init; } = "";
    public double Quantity { get; init; } = 1;
}

/// <summary>Один вариант доп. штрихкода (поле "alternate_barcodes" в новом виде на сайте,
/// 2026-09-21) — свой штрихкод, своё название (сочетается с названием товара при сканировании,
/// например «Асу вода» + «Клубничный» → «Асу вода Клубничный») и количество в упаковке
/// (только для отображения — на количество строки в чеке не влияет).</summary>
public sealed class AlternateBarcodeVariant
{
    public string Barcode { get; init; } = "";
    public string? Name { get; init; }
    public double Quantity { get; init; }
}

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
        OnPropertyChanged(nameof(TypeBadgeForeground));
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

    /// <summary>Остальные поля карточки товара из NurCRM, которых не было в "лёгкой" версии
    /// этой модели — нужны, чтобы форма редактирования товара (ProductEditDialog) могла
    /// показать и сохранить их, а не молча стирать при каждом сохранении (баг: раньше эти
    /// поля никогда не копировались из карточки в форму, и Save отправлял их пустыми).</summary>
    public double MarkupPercent { get; set; }
    public string? Description { get; set; }
    public double WholesalePrice { get; set; }
    public double DiscountPercent { get; set; }
    /// <summary>Есть ли скидка на товар — для колонки "Скидка" в Складе: плашка "-N%" вместо "0%".</summary>
    public bool HasDiscount => DiscountPercent > 0.0001;
    public string? Country { get; set; }
    public double? WeightKg { get; set; }

    /// <summary>Доп. штрихкоды товара (поле "alternate_barcodes"), объединённые переносами
    /// строк — как их ожидает многострочное поле ввода в форме редактирования.</summary>
    public string? AlternateBarcodesRaw { get; set; }

    /// <summary>Доп. штрихкоды с названием варианта и количеством в упаковке — новый формат
    /// поля "alternate_barcodes" на сайте (2026-09-21, объекты вместо голых строк). Null/пусто,
    /// если у товара нет доп. штрихкодов или все они в старом (строковом) формате.</summary>
    public List<AlternateBarcodeVariant>? AlternateBarcodeVariants { get; set; }

    /// <summary>Короткий PLU-код товара (поле "plu" в NurCRM) — им, а не полным штрих-кодом,
    /// весы обычно кодируют товар в весовом штрих-коде (см. WeightBarcodeParser).</summary>
    public int? Plu { get; set; }

    /// <summary>Артикул товара (поле "article" в NurCRM) — редактируемое поле карточки товара,
    /// используется для печати ценников/этикеток и поиска.</summary>
    public string? Article { get; set; }

    /// <summary>2026-09-21, живой баг владельца ("весовые не находит когда артикул есть"):
    /// «Код товара» (поле "code" в NurCRM) — ОТДЕЛЬНОЕ от Article поле на сайте (карточка
    /// товара показывает оба, разными значениями). Раньше Article заполнялся как
    /// "article" ?? "code" — если у товара было заполнено И то, И другое (как обычно и
    /// бывает), реальный "code" просто терялся и нигде не сохранялся. Весы в раскладке
    /// "по коду" (ScaleBarcodeLayout=code) кодируют именно этот код — без отдельного поля
    /// найти товар по такому штрих-коду было невозможно, если у товара также был Артикул
    /// (см. LocalCartService.FindByEmbeddedCode, которая теперь проверяет оба поля).</summary>
    public string? ProductCode { get; set; }

    /// <summary>Поштучная продажа из упаковки — первая запись поля "packages", если она есть.</summary>
    public ProductPackageOption? PieceOption { get; set; }

    public bool HasPieceOption => PieceOption != null;

    /// <summary>Комплект/набор из нескольких разных товаров (поле "kind"="bundle" в NurCRM).</summary>
    public bool IsBundle { get; set; }

    /// <summary>Состав комплекта (только когда IsBundle) — какие товары и в каком количестве
    /// входят в набор. Для товаров, синхронизированных с сервера, сервер сам списывает остатки
    /// компонентов при продаже комплекта — этот список здесь только для отображения/редактирования
    /// карточки. Для локально (офлайн) созданных комплектов это единственный источник состава.</summary>
    public List<BundleComponent>? BundleItems { get; set; }

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

    /// <summary>Hex-фон плашки типа товара. 2026-09-08: у "Комплект" свой цвет (фиолетовый),
    /// чтобы отличаться и от весового (зелёный), и от штучного (оранжевый) — см.
    /// TypeBadgeTextConverter в UI-проекте, который аналогично учитывает IsBundle/HasPieceOption
    /// (этот текстовый вариант TypeBadgeText не переведён и реально нигде не используется —
    /// TypeBadgeTextConverter вычисляет локализованный текст напрямую по флагам модели).</summary>
    /// <summary>Фон плашки типа товара — БЛЕДНЫЙ тон, а не заливка во всю силу цвета.
    /// 2026-09-22: раньше здесь стояли #7C3AED / #16A34A / #F59E0B, и плашка шла полосой во
    /// всю ширину плитки. На экране, где таких плиток два десятка, получалась пёстрая мозаика,
    /// в которой кричащие полосы перебивали и название, и цену — то есть ровно то, на что
    /// кассир смотрит. В кассовых программах тип товара помечают маленьким спокойным чипом:
    /// заметно, когда ищешь глазами, и не мешает, когда не ищешь.</summary>
    public string TypeBadgeBackground =>
        IsBundle ? "#EDE9FE" : IsWeighted ? "#DCFCE7" : "#FEF3C7";

    /// <summary>Текст плашки — насыщенный тон того же цвета, что и фон. Тёмным по бледному
    /// контраст выше, чем белым по насыщенному, и читается спокойнее.</summary>
    public string TypeBadgeForeground =>
        IsBundle ? "#5B21B6" : IsWeighted ? "#15803D" : "#92400E";

    public string TypeBadgeText =>
        IsBundle ? "📦 Комплект" : IsWeighted ? "⚖️ Весовой" : HasPieceOption ? "📦 Штучный + Поштучно" : "📦 Штучный";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-raises ProductImagePath changed with no actual value change — lets a
    /// catalog-wide "show photos" setting toggle already-rendered tiles without a full reload.</summary>
    public void RefreshPhotoVisibility() => OnPropertyChanged(nameof(ProductImagePath));

    private void NotifyTypeBadgeChanged()
    {
        OnPropertyChanged(nameof(IsWeighted));
        OnPropertyChanged(nameof(TypeBadgeBackground));
        OnPropertyChanged(nameof(TypeBadgeText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
