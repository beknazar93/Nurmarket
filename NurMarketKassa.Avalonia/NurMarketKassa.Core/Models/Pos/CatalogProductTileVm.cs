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

    /// <summary>2026-11-06 (В12, Б-8): «заканчивается». Если у товара задан «Минимальный остаток» (карточка, minimum_quantity) — остаток не выше него; иначе общий порог 10
    /// (остаток меньше 10). Раньше поле «Минимальный остаток» писалось на сервер, но нигде не использовалось: товар с минимумом 50 при остатке 20 не помечался.</summary>
    public static bool IsLow(double quantity, double? minQuantity) =>
        minQuantity is > 0 ? quantity <= minQuantity.Value : quantity < LowStockQuantityThreshold;

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

    // 2026-11-06 (В16, память): повторяющиеся короткие значения — один общий экземпляр на значение (StringPool), а не по копии на каждый из 15 000 товаров.
    private string? _category, _brand, _country, _kind, _statusDisplay, _hotkeyGroupName, _status, _hotkeyGroup;

    public string? Category { get => _category; set => _category = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }
    public string? Brand { get => _brand; set => _brand = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }

    // ── 2026-10-06, редизайн склада: подпись «категория · бренд», срок годности, цена числом для сортировки ──

    /// <summary>«Категория · Бренд» под названием в таблице склада.</summary>
    public string SubtitleText => string.Join(" · ", new[] { Category, Brand }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));

    public bool HasSubtitle => SubtitleText.Length > 0;

    private string? _expiryBadgeText;
    private bool _expiryExpired;

    /// <summary>Метка срока годности («Просрочен 12.09», «Срок до 10.10») — из справочника сроков сервера (ProductExpiryIndex).</summary>
    public string? ExpiryBadgeText
    {
        get => _expiryBadgeText;
        set
        {
            if (_expiryBadgeText == value)
                return;
            _expiryBadgeText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasExpiryBadge));
        }
    }

    public bool HasExpiryBadge => !string.IsNullOrEmpty(_expiryBadgeText);

    public bool ExpiryExpired
    {
        get => _expiryExpired;
        set
        {
            if (_expiryExpired == value)
                return;
            _expiryExpired = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Цена числом — для сортировки колонки «Цена» (по строке «1800.00» шло раньше «200.00»).</summary>
    public double PriceValue
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(PriceLine ?? "", @"-?\d[\d\s ]*(?:[.,]\d+)?");
            return m.Success && double.TryParse(m.Value.Replace(" ", "").Replace(" ", "").Replace(',', '.'),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }

    public string Id { get; }
    public string Title { get; }
    public string PriceLine { get; }

    // 2026-10-29 (аудит поиска): ключ для единого поиска (SearchMatch) — название, штрихкод, код, артикул, категория, бренд в нормальном виде.
    // Считается один раз и пересчитывается, только если одно из полей заменили (проверка по ссылкам строк) — на 15 000 товаров поиск <100 мс.
    private string? _searchKey;
    private string? _skBarcode, _skArticle, _skCode, _skCategory, _skBrand, _skAlternate;
    private int? _skPlu;

    public string SearchKey
    {
        get
        {
            if (_searchKey is null || !ReferenceEquals(_skBarcode, _barcode) || !ReferenceEquals(_skArticle, Article)
                || !ReferenceEquals(_skCode, ProductCode) || !ReferenceEquals(_skCategory, Category) || !ReferenceEquals(_skBrand, Brand)
                || !ReferenceEquals(_skAlternate, AlternateBarcodesRaw) || _skPlu != Plu)
            {
                _skBarcode = _barcode; _skArticle = Article; _skCode = ProductCode; _skCategory = Category; _skBrand = Brand;
                _skAlternate = AlternateBarcodesRaw; _skPlu = Plu;
                // 2026-11-06 (класс Б, Б-9): в ключ добавлены PLU весового товара и доп. штрихкоды — раньше «12» не находило товар по PLU, а доп. код находился только точным совпадением.
                _searchKey = NurMarketKassa.Core.Application.SearchMatch.Key(Title, Id, _barcode, Article, ProductCode, Category, Brand,
                    Plu is > 0 ? Plu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, AlternateBarcodesRaw);
            }
            return _searchKey;
        }
    }

    /// <summary>2026-10-22 (модалки, пакет 1, живой аудит): экранный диктор читал у плитки каталога имя класса
    /// «CatalogProductTileVm» (элемент списка без своего имени) — теперь название товара.</summary>
    public override string ToString() => Title;

    /// <summary>2026-10-06, редизайн программы владельца: цена в таблице склада — с разрядами, как закупка рядом
    /// («1 800,00 сом», было «1800.00 сом»). Только для показа; везде, где цену считают, — прежний <see cref="PriceLine"/>.</summary>
    public string PriceLineDisplay
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(PriceLine ?? "", @"^\s*(-?\d[\d\s ]*(?:[.,]\d+)?)\s*(.*)$");
            if (!m.Success || !decimal.TryParse(m.Groups[1].Value.Replace(" ", "").Replace(" ", "").Replace(',', '.'),
                    System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return PriceLine ?? "";
            var suffix = m.Groups[2].Value.Trim();
            return value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + (suffix.Length > 0 ? " " + suffix : "");
        }
    }

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

    /// <summary>Remote catalog image URL (API). 2026-11-06 (Б-1, И-3): setter — лента изменений без ключа картинки не должна стирать фото из базы (CatalogFeedSync.KeepLocalForAbsentFields).</summary>
    public string? ImageUrl { get; set; }

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

    public string? StatusDisplay { get => _statusDisplay; set => _statusDisplay = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }
    public string? HotkeyGroupName { get => _hotkeyGroupName; set => _hotkeyGroupName = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }
    public DateTime CreatedAt { get; set; }
    public string? ClientName { get; set; }
    public string? Status { get => _status; set => _status = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }
    public string? HotkeyGroup { get => _hotkeyGroup; set => _hotkeyGroup = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }

    public string? Barcode
    {
        get => _barcode;
        set
        {
            if (_barcode == value)
                return;
            _barcode = value;
            OnPropertyChanged(nameof(Barcode));
            OnPropertyChanged(nameof(BarcodeDisplay)); // 2026-11-06: колонка «Код / штрихкод» склада
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
            var low = IsLow(value, _minQuantity);
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

    private double? _minQuantity;

    /// <summary>2026-11-06 (В12, Б-8): «Минимальный остаток» из карточки (minimum_quantity). null или 0 — не задан, действует общий порог 10 (<see cref="IsLow"/>).</summary>
    public double? MinQuantity
    {
        get => _minQuantity;
        set
        {
            if (_minQuantity == value)
                return;
            _minQuantity = value;
            OnPropertyChanged(nameof(MinQuantity));
            OnPropertyChanged(nameof(StockForeground));
            var low = IsLow(_quantity, value);
            if (_isLowStock != low)
            {
                _isLowStock = low;
                OnPropertyChanged(nameof(IsLowStock));
            }
        }
    }

    /// <summary>Остаток вместе с единицей измерения: «93 шт», «12,5 кг». Отдельная колонка
    /// «Ед. изм.» из таблицы склада убрана — она повторяла одно и то же слово во всех строках и
    /// занимала место, из-за которого заголовки соседних колонок обрезались.</summary>
    public string StockWithUnitText =>
        Quantity.ToString("0.###") + (string.IsNullOrWhiteSpace(Unit) ? "" : " " + Unit);

    /// <summary>Сколько денег лежит на полке этим товаром: закупочная цена × остаток. Показывается
    /// в таблице склада под ценой закупки — по образцу «Остатков товаров», где рядом стоят
    /// себестоимость единицы и общая.</summary>
    public string StockCostText => PurchasePrice > 0 ? (PurchasePrice * Quantity).ToString("N2") : "—";

    /// <summary>Остальные поля карточки товара из NurCRM, которых не было в "лёгкой" версии
    /// этой модели — нужны, чтобы форма редактирования товара (ProductEditDialog) могла
    /// показать и сохранить их, а не молча стирать при каждом сохранении (баг: раньше эти
    /// поля никогда не копировались из карточки в форму, и Save отправлял их пустыми).</summary>
    public double MarkupPercent { get; set; }
    public string? Description { get; set; }
    public double WholesalePrice { get; set; }
    /// <summary>2026-11-01 (владелец «10 из 10», is_weight): признак товара is_weight с сервера. Он, а не единица «кг» (MustWeigh), определяет, что сервер округляет сумму строки вниз.
    /// null — неизвестно (старая запись базы), тогда прежняя эвристика (см. CartDisplayHelper.LineIsWeightProduct).</summary>
    public bool? ServerIsWeight { get; set; }
    public double DiscountPercent { get; set; }
    /// <summary>Есть ли скидка на товар — для колонки "Скидка" в Складе: плашка "-N%" вместо "0%".</summary>
    public bool HasDiscount => DiscountPercent > 0.0001;
    public string? Country { get => _country; set => _country = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }
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

    /// <summary>2026-11-06 (жалоба клиента «Код / штрихкод не показывает»): верхняя строка колонки «Код / штрихкод» в Складе — артикул, нет его — код товара, нет и его — PLU.</summary>
    public string CodeDisplay =>
        !string.IsNullOrWhiteSpace(Article) ? Article!.Trim()
        : !string.IsNullOrWhiteSpace(ProductCode) ? ProductCode!.Trim()
        : Plu is > 0 ? Plu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Нижняя строка колонки — штрихкод; нет основного — первый дополнительный.</summary>
    public string BarcodeDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Barcode))
                return Barcode!.Trim();
            var alt = AlternateBarcodesRaw?.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return alt is { Length: > 0 } ? alt[0] : "";
        }
    }

    /// <summary>Коды (артикул, код, PLU, доп. штрихкоды) дописаны после показа строки — обновить колонку.</summary>
    public void RaiseCodesChanged()
    {
        OnPropertyChanged(nameof(CodeDisplay));
        OnPropertyChanged(nameof(BarcodeDisplay));
    }

    /// <summary>Поштучная продажа из упаковки — первая запись поля "packages", если она есть.</summary>
    public ProductPackageOption? PieceOption { get; set; }

    public bool HasPieceOption => PieceOption != null;

    /// <summary>Показывать ли плашку типа товара.
    ///
    /// Обычный штучный товар плашки не получает: она стояла на каждой второй карточке и
    /// перестала что-либо различать — цвет, который есть у всех, не несёт информации, только
    /// шумит. Плашка остаётся там, где тип МЕНЯЕТ ПОВЕДЕНИЕ кассы: весовой (спросит вес),
    /// комплект (разложится на составляющие), поштучный из пачки (спросит, целая или штука).</summary>
    public bool ShowTypeBadge => IsWeighted || IsBundle || HasPieceOption || IsService;

    /// <summary>Комплект/набор из нескольких разных товаров (поле "kind"="bundle" в NurCRM).</summary>
    public bool IsBundle { get; set; }

    /// <summary>Вид товара сайта: product / service / bundle.</summary>
    public string? Kind { get => _kind; set => _kind = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value); }

    /// <summary>Услуга: у неё нет остатка, и касса не проверяет его при продаже (2026-09-24,
    /// «в услуге требует количество» — «Тест услуги» с нулевым остатком не пробивалась).</summary>
    public bool IsService => string.Equals(Kind, "service", StringComparison.OrdinalIgnoreCase);

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
            _unit = NurMarketKassa.Core.Application.StringPool.Shared.Intern(value);
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
        IsLow(Quantity, _minQuantity) ? "#DC2626" : "#64748B"; // 2026-11-06 (Б-8): с учётом «Минимального остатка» карточки

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
