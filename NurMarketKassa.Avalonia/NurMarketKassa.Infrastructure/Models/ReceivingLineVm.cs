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
    /// <summary>2026-11-09 (владелец: «из интернета» как на старом): название/категория подобраны из интернета ПО НАЖАТИЮ кассира («Найти в интернете» у строки «Неизвестно»).
    /// Товар по-прежнему новый (IsNew). Значение — в конце: в сохранённых задачах и сессиях цифры прежних значений не меняются.</summary>
    Internet,
}

/// <summary>2026-10-28: как считается цена продажи строки в ручной приёмке (см. ReceivingLineVm.SaleMode).</summary>
public enum SalePriceMode
{
    None,
    Auto,
    Manual,
    /// <summary>2026-11-06: цена продажи известна (карточка товара или прошлая закупка) — общая наценка её не трогает, только явное «Как у всех» / «Применить ко всем».</summary>
    Card,
}

/// <summary>2026-11-06: откуда подставлена цена закупки новой строки «Приём товара».</summary>
public enum PurchaseDefaultSource
{
    None,
    SupplierHistory,
    Card,
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

    private int _lineNo;

    /// <summary>2026-11-07 (ТЗ «Порядковая нумерация товаров в накладной»): порядковый номер строки в накладной — порядок ДОБАВЛЕНИЯ (первому 1, второму 2…).
    /// Только для показа в колонке «№»: в базу, на сервер, в поиск и штрихкод не уходит. Присваивает и пересчитывает без пропусков
    /// <see cref="NurMarketKassa.Services.ReceivingListOrder.Renumber"/>; правка количества и цен номер не меняет. 0 — номера ещё нет.</summary>
    public int LineNo
    {
        get => _lineNo;
        set
        {
            if (Set(ref _lineNo, value))
                OnPropertyChanged(nameof(LineNoAutomationText));
        }
    }

    /// <summary>«Позиция 3» — имя ячейки «№» для программ экранного чтения.</summary>
    public string LineNoAutomationText => NurMarketKassa.Services.Tr.T("Позиция", "Позиция", "Item", "Kalem", "Pozitsiya") + " " + _lineNo.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
        set
        {
            if (Set(ref _barcode, (value ?? "").Trim()))
                OnPropertyChanged(nameof(CanSearchInternet)); // 2026-11-09
        }
    }

    public string ProductName
    {
        get => _productName;
        set
        {
            if (Set(ref _productName, value ?? ""))
            {
                // 2026-11-09: подсветка «название изменено» и подсказка «было: …» у товара склада.
                OnPropertyChanged(nameof(NameRenamed));
                OnPropertyChanged(nameof(NameWasHint));
                OnPropertyChanged(nameof(NameSubText));
            }
        }
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
                OnPropertyChanged(nameof(SourceHint)); // 2026-11-09: метка источника в начале строки
                OnPropertyChanged(nameof(SourceAutomationText));
                OnPropertyChanged(nameof(CanSearchInternet));
                OnPropertyChanged(nameof(NameRenamed));
                OnPropertyChanged(nameof(NameWasHint));
                OnPropertyChanged(nameof(IsNew));
                OnPropertyChanged(nameof(PurchaseChangeText)); // 2026-10-29: «новый товар» зависит от источника
                OnPropertyChanged(nameof(IsChangeNew));
                OnPropertyChanged(nameof(IsChangeUp));
                OnPropertyChanged(nameof(IsChangeDown));
                OnPropertyChanged(nameof(IsChangeSame));
                NotifyView();
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
                if (!_inTextSet) _quantityRaw = null;
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(QuantityText));
                OnPropertyChanged(nameof(QtyInputText)); // 2026-11-07: таблица Склада читает «Принимаем» через QtyInputText (упаковки или штуки)
                OnPropertyChanged(nameof(PriceProblemText)); // 2026-10-31: пояснение «больше 3 знаков» зависит от самого количества
                OnPropertyChanged(nameof(LowMarginText));
                if (_packMode && !_inPackCalc)
                    SyncPackFromQuantity(); // 2026-11-07: штуки поменяли снаружи (повторный скан +1, «Обнулить») — число упаковок следует за ними
            }
        }
    }

    /// <summary>2026-10-31 («Не округляй»): количество с больше чем 3 знаками после запятой сервер не принимает (проверено 2026-10-29). Раньше такое число
    /// молча округлялось (12,3456 → 12,346); теперь значение остаётся как набрано, строка помечена красным и не проводится (<see cref="PurchaseReceivingService.Validate"/>).
    /// Шум double (12,345000000000001) не считается: сравнение идёт через decimal.</summary>
    public static bool QuantityTooPrecise(double quantity)
    {
        if (double.IsNaN(quantity) || double.IsInfinity(quantity) || Math.Abs(quantity) >= 1e9)
            return false;
        var d = (decimal)quantity;
        return Math.Round(d, 3, MidpointRounding.AwayFromZero) != d;
    }

    public static string QuantityPrecisionText => NurMarketKassa.Services.Tr.T(
        "Количество — не больше 3 знаков после запятой (сервер не принимает больше)",
        "Саны — үтүрдөн кийин 3 цифрадан ашык эмес (сервер көбүн кабыл албайт)",
        "Quantity — at most 3 decimal places (the server does not accept more)",
        "Miktar — virgülden sonra en fazla 3 basamak (sunucu fazlasını kabul etmez)",
        "Miqdor — verguldan keyin ko'pi bilan 3 raqam (server ko'pini qabul qilmaydi)");

    public double PurchasePrice
    {
        get => _purchasePrice;
        set
        {
            if (Set(ref _purchasePrice, value))
            {
                if (!_inTextSet) _purchaseRaw = null;
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(PurchasePriceText));
                OnPropertyChanged(nameof(PurchaseInputText)); // 2026-11-07: «Закупка» таблицы Склада — за упаковку или за штуку
                NotifyPricing();
                if (_packMode && !_inPackCalc)
                    SyncPackPriceFromPurchase(); // 2026-11-07: цену за штуку поставили снаружи (поставщик, заказ) — цена упаковки следует за ней
            }
        }
    }

    /// <summary>2026-11-05 (функция 11): доля доп. расходов прихода (доставка, грузчики) на ЕДИНИЦУ товара. В документ прихода и в долг поставщику
    /// НЕ входит (там накладная <see cref="PurchasePrice"/>); себестоимость <see cref="CostPrice"/> уходит в карточку товара отдельным PATCH после прихода.</summary>
    public double LandedExtraPerUnit
    {
        get => _landedExtraPerUnit;
        set
        {
            if (_landedExtraPerUnit == value)
                return;
            _landedExtraPerUnit = value;
            // 2026-11-06 (Б2-5, живой тест Этапа 2): после «Доп. расходы → Применить» строка не показывала «с расходами …» и считала прибыль без расходов,
            // пока её не правили — доля расходов менялась молча. Теперь строка узнаёт об этом сразу (таблица слушает PropertyChanged).
            OnPropertyChanged(nameof(LandedExtraPerUnit));
            OnPropertyChanged(nameof(CostPrice));
            OnPropertyChanged(nameof(LandedCostText));
            OnPropertyChanged(nameof(ProfitText));
            OnPropertyChanged(nameof(LowMarginText));
        }
    }

    private double _landedExtraPerUnit;

    /// <summary>Себестоимость единицы: накладная закупка + доля доп. расходов (до 4 знаков).</summary>
    public double CostPrice => Math.Round(PurchasePrice + Math.Max(LandedExtraPerUnit, 0), 4, MidpointRounding.AwayFromZero);

    public double SalePrice
    {
        get => _salePrice;
        set
        {
            if (Set(ref _salePrice, value))
            {
                if (!_inTextSet) _saleRaw = null;
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
        get => _quantityRaw ?? Quantity.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture); // 2026-10-31: без округления показа (было 0.###)
        set => SetFromText(value, v => Quantity = v, nameof(QuantityText), r => _quantityRaw = r);
    }

    // 2026-10-28 (дробные цены закупки): цены разбираются единым CheckoutValidation.TryParseAmount (запятая/точка, пробелы разрядов; без «1e3»,
    // «(10)», «-5», «1.000»). Непонятное число оставляет прежнее значение и показывает причину в колонке «Изменение» (PriceProblemText).
    // 2026-10-28 (ДЕНЬГИ, живой тест): набор «100.60» символ за символом давал 10060 — ячейка обновляет источник на КАЖДУЮ букву, «100.» разбиралось
    // и getter возвращал «100» без точки, таблица подменяла текст, и дальше набиралось «10060». Теперь, пока ячейка правится, getter отдаёт
    // СЫРОЙ набранный текст (число применяется на лету, разделитель не теряется); нормализует показ («100,60») CommitText — по выходу из ячейки.
    public string PurchasePriceText
    {
        get => _purchaseRaw ?? FormatPrice(PurchasePrice);
        set => SetPurchaseText(value);
    }

    public string SalePriceText
    {
        get => _saleRaw ?? FormatPrice(SalePrice);
        set => SetSaleText(value);
    }

    /// <summary>Цена для показа в инвариантной форме: целая — «100», дробная — всегда с копейками («100.60», «0.50»; больше двух знаков — как есть).
    /// Окна заменяют точку на запятую. Ноль — «0».</summary>
    public static string FormatPrice(double value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return Math.Abs(value - Math.Round(value)) < 1e-9 ? Math.Round(value).ToString("0", inv) : value.ToString("0.00##", inv);
    }

    /// <summary>Правка ячейки закончена (выход из ячейки, Enter, потеря фокуса): сырой набранный текст заменяется нормальным показом
    /// («100,60»), непринятый ввод откатывается к последнему принятому значению.</summary>
    public void CommitText()
    {
        _quantityRaw = _purchaseRaw = _saleRaw = _markupRaw = null;
        _packCountRaw = _piecesRaw = _packPriceRaw = null; // 2026-11-07: то же для полей упаковок
        if (_priceProblem is not null)
        {
            _priceProblem = null;
            OnPropertyChanged(nameof(PriceProblemText));
            OnPropertyChanged(nameof(LowMarginText));
        }
        OnPropertyChanged(nameof(QuantityText));
        OnPropertyChanged(nameof(PurchasePriceText));
        OnPropertyChanged(nameof(SalePriceText));
        OnPropertyChanged(nameof(MarkupText));
        OnPropertyChanged(nameof(QtyInputText));
        OnPropertyChanged(nameof(PiecesInputText));
        OnPropertyChanged(nameof(PurchaseInputText));
    }

    /// <summary>Ввод закупки руками: значение; с автонаценкой (строка в режиме «авто») цена продажи пересчитывается.</summary>
    public void SetPurchaseText(string? text)
    {
        var before = PurchasePrice;
        SetPriceFromText(text, NurMarketKassa.Services.ReceivingPricing.PurchaseDecimals, v => PurchasePrice = v, nameof(PurchasePriceText), r => _purchaseRaw = r);
        if (Math.Abs(PurchasePrice - before) > 1e-9)
        {
            _purchaseManual = true; // 2026-11-06: цену закупки вписали руками — при смене поставщика её не подменяем
            ApplyAutoSale();
        }
    }

    // ------------------------------------------------------------------ 2026-11-07: «Принять упаковками»
    // Главные значения строки остаются прежними и ВСЕГДА в штуках: Quantity = штуки, PurchasePrice = цена ЗА ШТУКУ (до 4 знаков) — их читают проведение, очередь
    // «Неотправленное», накладная, итоги, этикетки, цена продажи и наценка. Режим упаковок только ВВОДИТ их иначе: упаковок × штук в упаковке → Quantity,
    // цена упаковки ÷ штук в упаковке → PurchasePrice (расчёты — ReceivingPacks). Выключили режим — штуки и цена за штуку остаются как были.

    private bool _packMode, _packPriceTyped, _inPackCalc;
    private double _packCount, _piecesPerPack, _packPrice;
    // 2026-11-07: вариант «упаковка с поштучной продажей» (_packSale при _packMode): на склад идут УПАКОВКИ (Quantity = упаковки, PurchasePrice = цена упаковки, SalePrice = цена упаковки),
    // а в карточку НОВОГО товара при создании пишется упаковка (packages_input: штук в упаковке, цена штуки = цена продажи упаковки ÷ штук — как «Поштучная продажа» в карточке товара).
    private bool _packSale;
    private string? _packCountRaw, _piecesRaw, _packPriceRaw; // сырой набранный текст полей таблицы Склада (как _quantityRaw)

    /// <summary>Строка принимается упаковками (поля «упаковок», «шт в упаковке», «цена за упаковку»).</summary>
    public bool PackMode => _packMode;

    /// <summary>Сколько упаковок.</summary>
    public double PackCount => _packCount;

    /// <summary>Сколько штук в одной упаковке (0 — не задано).</summary>
    public double PiecesPerPack => _piecesPerPack;

    /// <summary>Цена закупки ЗА УПАКОВКУ, как её ввели.</summary>
    public double PackPrice => _packPrice;

    /// <summary>Режим включён, а сколько штук в упаковке не знаем — строку нельзя проводить (PurchaseReceivingService.Validate).</summary>
    public bool PackNeedsSize => _packMode && _piecesPerPack <= 0;

    // Пока размер упаковки не задан, число упаковок считается штуками (множитель 1): строка остаётся «активной» (Quantity > 0), и проверка перед проведением скажет, чего не хватает.
    // В варианте «поштучная продажа» склад ведёт упаковки: множителя нет (упаковки = Quantity).
    private double PackFactor => _packSale ? 1 : _piecesPerPack > 0 ? _piecesPerPack : 1;

    /// <summary>Строка принимается упаковкой с поштучной продажей (на склад — упаковки, в карточку нового товара — упаковка с N штук).</summary>
    public bool PackSale => _packMode && _packSale;

    /// <summary>Сколько штук в упаковке, которую заведут в карточку нового товара (0 — строка не в этом варианте). Читают очередь приходов и создание товара.</summary>
    public double PackSaleSize => PackSale ? _piecesPerPack : 0;

    /// <summary>Вариант «поштучная продажа» разрешён только для НОВОГО товара (создаётся при проведении, остатка нет): у существующего товара остаток в штуках — смена единицы
    /// остатка на «упаковки» исказила бы склад. Весовой товар и строка с размерами — тоже нет (в карточке поштучная продажа только у штучного товара без размеров).</summary>
    public bool CanPackSale => Source is ReceivingSource.Unknown or ReceivingSource.Internet && !IsWeight && VariantQuantities is not { Count: > 0 };

    /// <summary>Почему «поштучная продажа» недоступна (показывается у пункта меню).</summary>
    public static string PackSaleBlockedText => NurMarketKassa.Services.Tr.T(
        "Только для НОВОГО товара: у товара с остатком смена единицы остатка («упаковки») исказит склад. Поштучную продажу включите в карточке товара.",
        "Жаңы товар үчүн гана: калдыгы бар товарда калдык бирдигин («таңгак») алмаштыруу кампаны бузат. Даана менен сатууну товардын картасынан иштетиңиз.",
        "Only for a NEW product: for a product with stock, changing the stock unit (“packs”) would distort the warehouse. Enable piece sale in the product card.",
        "Yalnızca YENİ ürün için: stoklu üründe stok birimini («koli») değiştirmek depoyu bozar. Adetli satışı ürün kartında açın.",
        "Faqat YANGI mahsulot uchun: qoldig'i bor mahsulotda qoldiq birligini («qadoq») almashtirish omborni buzadi. Donalab sotishni mahsulot kartasida yoqing.");

    private double PackPriceOfPurchase() => _packSale ? PurchasePrice : _piecesPerPack > 0 ? NurMarketKassa.Services.ReceivingPacks.PackPriceFromPiece(PurchasePrice, _piecesPerPack) : 0;

    /// <summary>Включить/выключить режим. <paramref name="suggestedPiecesPerPack"/> — размер упаковки из памяти кассы (ReceivingPackSizes), если свой ещё не вводили.
    /// При включении число штук, оставшееся от сканирования («1»), становится ОДНОЙ упаковкой; любое другое число штук остаётся и пересчитывается в упаковки.
    /// Цена за штуку при включении не меняется: цена упаковки = цена за штуку × штук в упаковке.</summary>
    public void SetPackMode(bool on, double suggestedPiecesPerPack = 0)
    {
        if (_packSale)
            LeavePackSale(); // из «поштучной продажи» — сначала обратно в штуки (количество и цены пересчитываются), затем обычная логика
        if (on == _packMode)
            return;
        if (!on)
        {
            _packMode = false;
            NotifyPack();
            return;
        }
        if (_piecesPerPack <= 0 && suggestedPiecesPerPack > 0 && suggestedPiecesPerPack < 1e9)
            _piecesPerPack = suggestedPiecesPerPack;
        _packMode = true;
        _packPriceTyped = false;
        if (_piecesPerPack > 0 && Quantity == 1)
        {
            _packCount = 1;
            RecalcQuantity();
        }
        else
            _packCount = NurMarketKassa.Services.ReceivingPacks.PacksFromPieces(Quantity, PackFactor);
        _packPrice = PackPriceOfPurchase();
        _packCountRaw = _piecesRaw = _packPriceRaw = null;
        NotifyPack();
    }

    /// <summary>Включить/выключить вариант «упаковка с поштучной продажей». Включить можно только у НОВОГО товара (<see cref="CanPackSale"/>) — иначе false, ничего не меняется.
    /// Из обычного ввода «упаковками» (в штуки) переход пересчитывает: упаковки → Quantity, цена упаковки → закупка, цена продажи штуки × штук → цена продажи упаковки; из «штук» — числа остаются
    /// как введены (вводят уже упаковки и цену за упаковку). Выключение — обратно в штуки (упаковки × штук, цены ÷ штук).</summary>
    public bool SetPackSale(bool on, double suggestedPiecesPerPack = 0)
    {
        if (on == PackSale)
            return true;
        if (!on)
        {
            LeavePackSale();
            return true;
        }
        if (!CanPackSale)
            return false;
        var wasPieces = _packMode;
        var packs = wasPieces ? _packCount : Quantity;
        var packPrice = wasPieces ? _packPrice : PurchasePrice;
        var per = _piecesPerPack;
        var sale = SalePrice;
        if (!wasPieces && per <= 0 && suggestedPiecesPerPack > 0 && suggestedPiecesPerPack < 1e9)
            per = suggestedPiecesPerPack;
        _piecesPerPack = per;
        _packMode = true;
        _packSale = true;
        _packPriceTyped = false;
        _packCountRaw = _piecesRaw = _packPriceRaw = null;
        _inPackCalc = true;
        try
        {
            Quantity = packs;
            if (wasPieces)
            {
                if (packPrice > 0 && per > 0)
                    PurchasePrice = packPrice;
                if (per > 0 && sale > 0)
                    SalePrice = Math.Round(sale * per, 2, MidpointRounding.AwayFromZero); // цена продажи ШТУКИ → цена продажи УПАКОВКИ
            }
        }
        finally
        {
            _inPackCalc = false;
        }
        _packCount = Quantity;
        _packPrice = PurchasePrice;
        if (wasPieces)
            ApplyAutoSale();
        NotifyPack();
        return true;
    }

    /// <summary>Выйти из «поштучной продажи» в обычные штуки: упаковки × штук в упаковке, цена закупки и продажи упаковки ÷ штук. Размер упаковки неизвестен — числа остаются.</summary>
    private void LeavePackSale()
    {
        if (!_packSale)
            return;
        var per = _piecesPerPack;
        _packSale = false;
        _packMode = false;
        _packCountRaw = _piecesRaw = _packPriceRaw = null;
        if (per > 0)
        {
            var sale = SalePrice;
            Quantity = NurMarketKassa.Services.ReceivingPacks.Pieces(Quantity, per);
            PurchasePrice = NurMarketKassa.Services.ReceivingPacks.PiecePrice(PurchasePrice, per);
            if (sale > 0)
                SalePrice = Math.Round(sale / per, 2, MidpointRounding.AwayFromZero);
            ApplyAutoSale();
        }
        NotifyPack();
    }

    /// <summary>Ввод «упаковок»: штуки пересчитываются.</summary>
    public void SetPackCount(double packs)
    {
        _packCount = packs > 0 ? packs : 0;
        RecalcQuantity();
        NotifyPack();
    }

    /// <summary>Ввод «штук в упаковке»: штуки пересчитываются; цена за штуку — если цену упаковки вводили, от неё, иначе остаётся, а цена упаковки следует за ней.</summary>
    public void SetPiecesPerPack(double perPack)
    {
        _piecesPerPack = perPack > 0 && perPack < 1e9 ? perPack : 0;
        RecalcQuantity();
        var before = PurchasePrice;
        if (_packPriceTyped)
            RecalcPurchase();
        else
            _packPrice = PackPriceOfPurchase();
        if (Math.Abs(PurchasePrice - before) > 1e-9)
            ApplyAutoSale();
        NotifyPack();
    }

    /// <summary>Ввод «цены за упаковку» (текстом, как остальные цены: запятая/точка, до 4 знаков): цена за штуку = упаковка ÷ штук в упаковке; с автонаценкой цена продажи пересчитывается.</summary>
    public void SetPackPriceText(string? text)
    {
        var before = PurchasePrice;
        SetPriceFromText(text, NurMarketKassa.Services.ReceivingPricing.PurchaseDecimals, v =>
        {
            _packPrice = v;
            _packPriceTyped = true;
            _purchaseManual = true; // цену вписали руками — при смене поставщика её не подменяем
            RecalcPurchase();
        }, nameof(PackPrice), r => _packPriceRaw = r);
        if (Math.Abs(PurchasePrice - before) > 1e-9)
            ApplyAutoSale();
        NotifyPack();
    }

    private void RecalcQuantity()
    {
        _inPackCalc = true;
        try
        {
            Quantity = NurMarketKassa.Services.ReceivingPacks.Pieces(_packCount, PackFactor);
        }
        finally
        {
            _inPackCalc = false;
        }
    }

    private void RecalcPurchase()
    {
        if (_packSale)
        {
            // поштучная продажа: склад ведёт упаковки — закупка и есть цена упаковки, делить не на что
            _inPackCalc = true;
            try
            {
                PurchasePrice = _packPrice;
            }
            finally
            {
                _inPackCalc = false;
            }
            return;
        }
        if (_piecesPerPack <= 0)
            return; // размера упаковки нет — цену за штуку считать нечем, прежняя остаётся (строку не проведут, пока размер не введён)
        _inPackCalc = true;
        try
        {
            PurchasePrice = NurMarketKassa.Services.ReceivingPacks.PiecePrice(_packPrice, _piecesPerPack);
        }
        finally
        {
            _inPackCalc = false;
        }
    }

    private void SyncPackFromQuantity()
    {
        _packCount = NurMarketKassa.Services.ReceivingPacks.PacksFromPieces(Quantity, PackFactor);
        NotifyPack();
    }

    private void SyncPackPriceFromPurchase()
    {
        _packPriceTyped = false;
        _packPrice = PackPriceOfPurchase();
        NotifyPack();
    }

    /// <summary>Подпись под числом упаковок: «5 × 24 = 120 шт.» (или «укажите, сколько штук в упаковке»).</summary>
    public string PackNote
    {
        get
        {
            if (!_packMode)
                return "";
            if (_piecesPerPack <= 0)
                return NurMarketKassa.Services.Tr.T("укажите, сколько штук в упаковке", "таңгакта канча дана экенин жазыңыз", "enter how many pieces are in a pack", "kolide kaç adet olduğunu girin", "qadoqda nechta dona borligini kiriting");
            static string N(double v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            if (_packSale)
                return Quantity > 0 ? N(_packCount) + " × " + N(_piecesPerPack) + " = " + N(NurMarketKassa.Services.ReceivingPacks.Pieces(_packCount, _piecesPerPack)) + " " + NurMarketKassa.Services.UnitCatalog.Display(Unit) : ""; // остаток — в упаковках, штуки лишь для показа
            return Quantity > 0 ? N(_packCount) + " × " + N(_piecesPerPack) + " = " + N(Quantity) + " " + NurMarketKassa.Services.UnitCatalog.Display(Unit) : ""; // «5 × 24 = 120 шт.»
        }
    }

    /// <summary>Короткая подпись под числом упаковок в узкой ячейке таблицы Склада: «× 24 = 120 шт.» (поштучная продажа: «по 24 шт»); нет размера — «укажите, сколько штук в упаковке».</summary>
    public string PackShortNote
    {
        get
        {
            if (!_packMode)
                return "";
            if (_piecesPerPack <= 0)
                return PackNote;
            static string N(double v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
            var unit = NurMarketKassa.Services.UnitCatalog.Display(Unit);
            if (_packSale)
                return NurMarketKassa.Services.Tr.T($"по {N(_piecesPerPack)} {unit}", $"ар бири {N(_piecesPerPack)} {unit}", $"{N(_piecesPerPack)} {unit} each", $"her biri {N(_piecesPerPack)} {unit}", $"har biri {N(_piecesPerPack)} {unit}");
            return Quantity > 0 ? "× " + N(_piecesPerPack) + " = " + N(Quantity) + " " + unit : "";
        }
    }

    private double? _cardPackSize;

    /// <summary>У товара в каталоге уже есть «поштучная продажа из упаковки» — штук в упаковке (0 — нет). Склад ведёт такой товар упаковками: «Принимаем» и закупка у него и так за упаковку,
    /// режим «упаковками» не предлагается, только подсказка «упак. = N шт». Читается из каталога один раз на строку.</summary>
    public double CardPackSize => string.IsNullOrEmpty(_productId) ? 0 : _cardPackSize ??= NurMarketKassa.Services.ReceivingPacks.PieceOptionSize(this);

    /// <summary>Подпись-ссылка нажимается (у товара с упаковкой в карточке — это просто подсказка).</summary>
    public bool PackLinkClickable => CardPackSize <= 0;

    /// <summary>Подпись-ссылка под «Принимаем» в таблице Склада: какой способ приёмки включён (нажатие открывает выбор).</summary>
    public string PackLinkText => CardPackSize > 0
        ? NurMarketKassa.Services.Tr.T($"упак. = {CardPackSize:0.###} шт", $"таңгак = {CardPackSize:0.###} даана", $"pack = {CardPackSize:0.###} pcs", $"koli = {CardPackSize:0.###} adet", $"qadoq = {CardPackSize:0.###} dona")
        : _packSale
        ? NurMarketKassa.Services.Tr.T("упак. + поштучно ▾", "таңгак + даана ▾", "pack + pieces ▾", "koli + adet ▾", "qadoq + dona ▾")
        : _packMode
            ? NurMarketKassa.Services.Tr.T("упак. → штуки ▾", "таңгак → даана ▾", "packs → pcs ▾", "koli → adet ▾", "qadoq → dona ▾")
            : NurMarketKassa.Services.Tr.T("упаковками ▾", "таңгак менен ▾", "by packs ▾", "kolilerle ▾", "qadoq bilan ▾");

    /// <summary>Короткая подпись-ссылка для узкой ячейки таблицы Склада (колонка «Принимаем» 96 px): «упак. ▾» / «упак.→шт ▾» / «упак.+шт ▾». Подробно — <see cref="PackLinkHint"/>.</summary>
    public string PackLinkShort => CardPackSize > 0
        ? PackLinkText
        : _packSale
            ? NurMarketKassa.Services.Tr.T("упак.+шт ▾", "таңгак+дн ▾", "pack+pcs ▾", "koli+adt ▾", "qadoq+dn ▾")
            : _packMode
                ? NurMarketKassa.Services.Tr.T("упак.→шт ▾", "таңгак→дн ▾", "packs→pcs ▾", "koli→adt ▾", "qadoq→dn ▾")
                : NurMarketKassa.Services.Tr.T("упак. ▾", "таңгак ▾", "packs ▾", "koli ▾", "qadoq ▾");

    /// <summary>Подсказка к подписи-ссылке: что включено и что будет на складе.</summary>
    public string PackLinkHint => CardPackSize > 0
        ? NurMarketKassa.Services.Tr.T($"Склад ведёт этот товар упаковками (в упаковке {CardPackSize:0.###} шт): «Принимаем» и закупка — за упаковку.", $"Кампа бул товарды таңгак менен жүргүзөт (таңгакта {CardPackSize:0.###} даана): «Кабыл алабыз» жана баасы — таңгак үчүн.",
            $"The warehouse keeps this product in packs ({CardPackSize:0.###} pcs each): “Receiving” and the purchase price are per pack.", $"Depo bu ürünü koli ile tutar (kolide {CardPackSize:0.###} adet): «Alınıyor» ve alış fiyatı koli başınadır.", $"Ombor bu mahsulotni qadoq bilan yuritadi (qadoqda {CardPackSize:0.###} dona): «Qabul qilinadi» va xarid narxi qadoq uchun.")
        : _packSale
            ? NurMarketKassa.Services.Tr.T("Упаковка с поштучной продажей: на склад идут УПАКОВКИ, новый товар заводится с упаковкой. Закупка и цена продажи — за упаковку. Нажмите, чтобы сменить способ.",
                "Даана менен сатылуучу таңгак: кампага ТАҢГАКТАР барат, жаңы товар таңгак менен түзүлөт. Сатып алуу жана сатуу баасы — таңгак үчүн. Ыкманы өзгөртүү үчүн басыңыз.",
                "Pack with single-piece sale: the warehouse gets PACKS, the new product is created with a pack. Purchase and sale price — per pack. Press to change.",
                "Adetli satışlı koli: depoya KOLİLER gider, yeni ürün koli ile oluşturulur. Alış ve satış fiyatı — koli başına. Değiştirmek için basın.",
                "Donalab sotiladigan qadoq: omborga QADOQLAR boradi, yangi mahsulot qadoq bilan yaratiladi. Xarid va sotuv narxi — qadoq uchun. O'zgartirish uchun bosing.")
            : _packMode
                ? NurMarketKassa.Services.Tr.T("Упаковками → на склад штуки: вводите упаковки, штук в упаковке и цену за упаковку — на склад уйдут штуки и цена за штуку. Нажмите, чтобы сменить способ.",
                    "Таңгак менен → кампага даана: таңгакты, таңгактагы даананы жана таңгактын баасын киргизиңиз — кампага даана жана даанасынын баасы барат. Ыкманы өзгөртүү үчүн басыңыз.",
                    "By packs → pieces to stock: enter packs, pieces per pack and the price per pack — the warehouse gets pieces and the price per piece. Press to change.",
                    "Kolilerle → depoya adet: koli sayısını, koli başına adedi ve koli fiyatını girin — depoya adet ve adet fiyatı gider. Değiştirmek için basın.",
                    "Qadoq bilan → omborga dona: qadoq sonini, qadoqdagi donani va qadoq narxini kiriting — omborga dona va dona narxi boradi. O'zgartirish uchun bosing.")
                : NurMarketKassa.Services.Tr.T("Способ приёмки: в штуках (как сейчас), упаковками (на склад штуки) или упаковка с поштучной продажей (новый товар). Нажмите, чтобы выбрать.",
                    "Кабыл алуу ыкмасы: даана менен (азыркыдай), таңгак менен (кампага даана) же даана менен сатылуучу таңгак (жаңы товар). Тандоо үчүн басыңыз.",
                    "How to receive: in pieces (as now), by packs (pieces to stock) or a pack with single-piece sale (new product). Press to choose.",
                    "Kabul yolu: adetle (şimdiki gibi), kolilerle (depoya adet) veya adetli satışlı koli (yeni ürün). Seçmek için basın.",
                    "Qabul usuli: donalab (hozirgidek), qadoq bilan (omborga dona) yoki donalab sotiladigan qadoq (yangi mahsulot). Tanlash uchun bosing.");

    /// <summary>Красная короткая пометка под суммой: «вместо 99 999,90» — упаковка не делится ровно (подробно — <see cref="PackLossText"/>).</summary>
    public string PackLossShort
    {
        get
        {
            if (!_packMode || _packSale || !NurMarketKassa.Services.ReceivingPacks.HasRoundingLoss(_packCount, _piecesPerPack, _packPrice))
                return "";
            var exact = NurMarketKassa.Services.MoneyText.Amount(NurMarketKassa.Services.ReceivingPacks.PackTotal(_packCount, _packPrice));
            return NurMarketKassa.Services.Tr.T($"вместо {exact}", $"{exact} ордуна", $"instead of {exact}", $"{exact} yerine", $"{exact} o'rniga");
        }
    }

    /// <summary>Ввод «Принимаем» в таблице Склада: в режиме упаковок — число упаковок, иначе количество (штуки). Хранит набранный текст до выхода из ячейки.</summary>
    public string QtyInputText
    {
        get => !_packMode ? QuantityText : _packCountRaw ?? _packCount.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (!_packMode)
            {
                QuantityText = value;
                return;
            }
            _packCountRaw = (value ?? "").Trim();
            if (_packCountRaw.Length == 0)
                SetPackCount(0);
            else if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(_packCountRaw, out var v) && v >= 0)
                SetPackCount(v);
            OnPropertyChanged(nameof(QtyInputText));
        }
    }

    /// <summary>Ввод «шт в упак.» в таблице Склада (пусто — не задано).</summary>
    public string PiecesInputText
    {
        get => _piecesRaw ?? (_piecesPerPack > 0 ? _piecesPerPack.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) : "");
        set
        {
            _piecesRaw = (value ?? "").Trim();
            if (_piecesRaw.Length == 0)
                SetPiecesPerPack(0);
            else if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(_piecesRaw, out var v) && v >= 0)
                SetPiecesPerPack(v);
            OnPropertyChanged(nameof(PiecesInputText));
        }
    }

    /// <summary>Ввод «Закупка» в таблице Склада: в режиме упаковок — цена за упаковку, иначе цена за штуку.</summary>
    public string PurchaseInputText
    {
        get => !_packMode ? PurchasePriceText : _packPriceRaw ?? FormatPrice(_packPrice);
        set
        {
            if (_packMode)
                SetPackPriceText(value);
            else
                PurchasePriceText = value;
            OnPropertyChanged(nameof(PurchaseInputText));
        }
    }

    /// <summary>Подпись под ценой упаковки: «за упак. · 33,3333 за шт» — сколько уйдёт на склад и в сервер ценой за штуку.</summary>
    public string PackPriceNote
    {
        get
        {
            if (!_packMode)
                return "";
            if (_piecesPerPack <= 0 || PurchasePrice <= 0)
                return NurMarketKassa.Services.Tr.T("цена за упаковку", "таңгактын баасы", "price per pack", "koli fiyatı", "qadoq narxi");
            var piece = (_packSale ? NurMarketKassa.Services.ReceivingPacks.PiecePrice(PurchasePrice, _piecesPerPack) : PurchasePrice).ToString("0.00##", RuCulture); // 2026-11-07: «поштучная продажа» — цена за штуку считается для показа
            return NurMarketKassa.Services.Tr.T($"за упак. · {piece} за шт", $"таңгак үчүн · даанасы {piece}", $"per pack · {piece} per piece", $"koli başına · adedi {piece}", $"qadoq uchun · donasi {piece}");
        }
    }

    /// <summary>Красная пометка: упаковка не делится ровно, цена за штуку округлена до 4 знаков и сумма строки стала отличаться от «упаковки × цена упаковки» (пусто, если всё сходится).</summary>
    public string PackLossText => _packMode && !_packSale ? NurMarketKassa.Services.ReceivingPacks.LossText(_packCount, _piecesPerPack, _packPrice) : "";

    private void NotifyPack()
    {
        OnPropertyChanged(nameof(PackSale));
        OnPropertyChanged(nameof(PackSaleSize));
        OnPropertyChanged(nameof(CanPackSale));
        OnPropertyChanged(nameof(PackShortNote));
        OnPropertyChanged(nameof(PackLinkText));
        OnPropertyChanged(nameof(PackLinkShort));
        OnPropertyChanged(nameof(PackLinkHint));
        OnPropertyChanged(nameof(PackLossShort));
        OnPropertyChanged(nameof(QtyInputText));
        OnPropertyChanged(nameof(PiecesInputText));
        OnPropertyChanged(nameof(PurchaseInputText));
        OnPropertyChanged(nameof(PackMode));
        OnPropertyChanged(nameof(PackCount));
        OnPropertyChanged(nameof(PiecesPerPack));
        OnPropertyChanged(nameof(PackPrice));
        OnPropertyChanged(nameof(PackNeedsSize));
        OnPropertyChanged(nameof(PackNote));
        OnPropertyChanged(nameof(PackPriceNote));
        OnPropertyChanged(nameof(PackLossText));
    }

    // ------------------------------------------------------------------ 2026-11-06: цена закупки по умолчанию (поставщик → карточка → 0)

    private bool _purchaseManual;
    private bool _saleFromHistory;

    /// <summary>Откуда сейчас взята цена закупки строки (None — вписана вручную или не подставлялась).</summary>
    public PurchaseDefaultSource PurchaseSource { get; private set; }

    /// <summary>Цену закупки вписали руками (или она пришла из заказа) — подстановка по поставщику её не меняет.</summary>
    public bool PurchaseIsManual => _purchaseManual;

    /// <summary>Закрепить текущую цену закупки (пришла из заказа/прошлого прихода) — смена поставщика её не трогает.</summary>
    public void LockPurchase() => _purchaseManual = true;

    /// <summary>Подставить цену закупки по умолчанию. Вручную введённую (или закреплённую) цену не затирает. true — цена изменилась.</summary>
    public bool TrySetDefaultPurchase(double price, PurchaseDefaultSource source)
    {
        if (_purchaseManual)
            return false;
        PurchaseSource = source;
        if (Math.Abs(PurchasePrice - price) < 1e-9)
        {
            NotifyMode();
            return false;
        }
        PurchasePrice = price; // свойство, не ввод: ручной признак не ставится
        ApplyAutoSale();
        NotifyMode();
        return true;
    }

    /// <summary>Ввод цены продажи руками: пока значение отличается от прежнего, строка переходит в «вручную» (автопересчёт выключается).</summary>
    public void SetSaleText(string? text)
    {
        var before = SalePrice;
        SetPriceFromText(text, 2, v => SalePrice = v, nameof(SalePriceText), r => _saleRaw = r);
        if (Math.Abs(SalePrice - before) > 1e-9)
            MarkSaleManual();
    }

    private string? _priceProblem;
    private bool _inTextSet; // идёт ввод текста в ячейку: сеттеры значений не сбрасывают сырой текст

    /// <summary>Причина, по которой введённое число цены не принято (пусто — всё принято). Показывается красным в колонке «Изменение».</summary>
    public string PriceProblemText => _priceProblem ?? (QuantityTooPrecise(Quantity) ? QuantityPrecisionText : "");

    private void SetPriceFromText(string? text, int decimals, Action<double> apply, string name, Action<string?> setRaw)
    {
        var problem = (string?)null;
        // Сырой набранный текст ставится ДО применения числа и не сбрасывается сеттерами значения (_inTextSet): иначе таблица на миг получает
        // «100» вместо «100.» и подменяет текст в ячейке.
        _inTextSet = true;
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                setRaw("");
                apply(0);
            }
            else
            {
                setRaw(text.Trim()); // «100.», «100,6», «12,333» остаются в ячейке как набраны, пока её правят
                var parse = NurMarketKassa.Services.CheckoutValidation.TryParseAmount(text, out var value, decimals);
                if (parse == NurMarketKassa.Services.CheckoutValidation.AmountParse.Ok)
                    apply(value);
                else
                    problem = NurMarketKassa.Services.CheckoutValidation.AmountProblemText(parse, decimals);
            }
        }
        finally
        {
            _inTextSet = false;
        }
        if (problem != _priceProblem)
        {
            _priceProblem = problem;
            OnPropertyChanged(nameof(PriceProblemText));
            OnPropertyChanged(nameof(LowMarginText));
        }
        OnPropertyChanged(name);
    }

    // ------------------------------------------------------------------ 2026-10-28: автонаценка в ручной приёмке

    private SalePriceMode _saleMode;

    /// <summary>Режим цены продажи: None — автонаценка не применяется (импорт накладной, массовая загрузка, выключено в настройках),
    /// Auto — считается по закупке, Manual — правили руками (автопересчёт выключен до кнопки «Авто»).</summary>
    public SalePriceMode SaleMode => _saleMode;

    public bool HasSaleMode => _saleMode != SalePriceMode.None;
    public bool CanReturnToAuto => _saleMode == SalePriceMode.Manual || (_saleMode == SalePriceMode.Card && PurchasePrice > 0 && NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled);

    public string SaleModeText => _saleMode switch
    {
        SalePriceMode.Auto => NurMarketKassa.Services.Tr.T("авто", "авто", "auto", "oto", "avto"),
        SalePriceMode.Manual => NurMarketKassa.Services.Tr.T("вручную", "колдо", "manual", "elle", "qo'lda"),
        _ => "",
    };

    /// <summary>Подпись кнопки в колонке «Цена: авто»: «авто» (считается по общей наценке) или «Как у всех» (строка правилась руками — нажатие возвращает).</summary>
    public string SaleButtonText => _saleMode is SalePriceMode.Manual or SalePriceMode.Card
        ? NurMarketKassa.Services.Tr.T("Как у всех", "Баарындай", "Like all", "Hepsi gibi", "Hammasi kabi")
        : SaleModeText;

    /// <summary>Подсказка к метке: откуда наценка, как вернуть автоматику.</summary>
    public string SaleModeHint
    {
        get
        {
            var settings = NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current;
            if (_saleMode == SalePriceMode.Manual)
                return NurMarketKassa.Services.Tr.T("Цена продажи задана вручную и не следует общей наценке. Нажмите «Как у всех» — снова считать по общему проценту.", "Сатуу баасы кол менен коюлган, жалпы кошумчага баш ийбейт. «Баарындай» басыңыз — кайра жалпы % боюнча эсептелет.",
                    "The sale price is set by hand and does not follow the common markup. Press “Like all” to calculate it from the common percent again.", "Satış fiyatı elle girildi ve ortak kâr oranını izlemiyor. «Hepsi gibi»ye basın — tekrar ortak orandan hesaplansın.", "Sotuv narxi qo'lda kiritilgan va umumiy ustamaga bo'ysunmaydi. «Hammasi kabi» ni bosing — yana umumiy foiz bo'yicha hisoblanadi.");
            // 2026-11-06: цена известна (карточка / прошлая закупка) — общая наценка её не меняет, пока не нажмут «Как у всех».
            if (_saleMode == SalePriceMode.Card)
            {
                var pct = NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, settings, out _) is { } cm ? cm.ToString("0.##", RuCulture) : "";
                var origin = _saleFromHistory
                    ? NurMarketKassa.Services.Tr.T("по прошлой закупке", "мурунку сатып алуу боюнча", "from the previous purchase", "önceki alıma göre", "oldingi xarid bo'yicha")
                    : NurMarketKassa.Services.Tr.T("из карточки товара", "товардын карточкасынан", "from the product card", "ürün kartından", "mahsulot kartasidan");
                return NurMarketKassa.Services.Tr.T($"Цена продажи {origin} и не меняется от общей наценки. Нажмите «Как у всех» — пересчитать по {pct} %.",
                    $"Сатуу баасы {origin} алынган жана жалпы кошумчадан өзгөрбөйт. «Баарындай» басыңыз — {pct} % боюнча кайра эсептөө.",
                    $"The sale price is {origin} and does not change with the common markup. Press “Like all” to recalculate it at {pct}%.",
                    $"Satış fiyatı {origin} ve ortak kâr oranıyla değişmez. «Hepsi gibi»ye basın — {pct} % ile yeniden hesaplansın.",
                    $"Sotuv narxi {origin} va umumiy ustama bilan o'zgarmaydi. «Hammasi kabi» ni bosing — {pct} % bo'yicha qayta hisoblanadi.");
            }
            if (_saleMode != SalePriceMode.Auto || NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, settings, out var source) is not { } m)
                return "";
            var from = source switch
            {
                NurMarketKassa.Services.ReceivingPricing.MarkupSource.Category => NurMarketKassa.Services.Tr.T("наценка категории", "категориянын кошумчасы", "category markup", "kategori kârı", "kategoriya ustamasi"),
                _ => NurMarketKassa.Services.Tr.T("наценка для всех", "баарына кошумча", "markup for all", "tümü için kâr", "hammasi uchun ustama"),
            };
            return NurMarketKassa.Services.Tr.T($"Цена продажи считается сама: {m:0.##} % ({from}). Правка цены вручную отключает автоматику.",
                $"Сатуу баасы өзү эсептелет: {m:0.##} % ({from}). Баасын кол менен өзгөртсөңүз, автоматика өчөт.",
                $"The sale price is calculated automatically: {m:0.##}% ({from}). Editing it by hand turns this off.",
                $"Satış fiyatı otomatik hesaplanır: {m:0.##} % ({from}). Elle değiştirmek bunu kapatır.",
                $"Sotuv narxi o'zi hisoblanadi: {m:0.##} % ({from}). Qo'lda o'zgartirsangiz, avtomatika o'chadi.");
        }
    }

    /// <summary>2026-10-29, жалоба владельца «непонятно»: «прибыль 14 сом с шт.» — прибыль на единицу (продажа − закупка); при убытке «убыток 5 сом с шт.»;
    /// пусто, пока нет обеих цен.</summary>
    public string ProfitText
    {
        get
        {
            if (PurchasePrice <= 0 || SalePrice <= 0)
                return "";
            // 2026-11-06 (В12, ЖП Н-6): с доп. расходами прибыль строки — от себестоимости (закупка + доля расходов), как итог под списком; раньше строка показывала «прибыль 0,88»,
            // а итог «−1,13».
            var profit = (decimal)SalePrice - (decimal)(LandedExtraPerUnit > 0 ? CostPrice : PurchasePrice);
            var x = Math.Abs(profit).ToString("0.##", RuCulture);
            var unit = string.IsNullOrWhiteSpace(Unit) ? "шт" : Unit.Trim();
            return profit >= 0
                ? NurMarketKassa.Services.Tr.T($"прибыль {x} сом с {unit}.", $"{unit} үчүн пайда {x} сом", $"profit {x} som per {unit}", $"{unit} başına kâr {x} som", $"{unit} uchun foyda {x} so'm")
                : NurMarketKassa.Services.Tr.T($"убыток {x} сом с {unit}.", $"{unit} үчүн чыгым {x} сом", $"loss {x} som per {unit}", $"{unit} başına zarar {x} som", $"{unit} uchun zarar {x} so'm");
        }
    }

    /// <summary>2026-11-06 (В12, ЖП Н-6): «с расходами 2,125» — закупка с долей доп. расходов под полем «Закупка»; пусто, если расходов на строке нет.</summary>
    public string LandedCostText => LandedExtraPerUnit > 0
        ? NurMarketKassa.Services.Tr.T($"с расходами {CostPrice.ToString("0.00##", RuCulture)}", $"чыгым менен {CostPrice.ToString("0.00##", RuCulture)}", $"with costs {CostPrice.ToString("0.00##", RuCulture)}",
            $"masrafla {CostPrice.ToString("0.00##", RuCulture)}", $"xarajat bilan {CostPrice.ToString("0.00##", RuCulture)}")
        : "";

    private static readonly System.Globalization.CultureInfo RuCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    public bool IsSaleAuto => _saleMode == SalePriceMode.Auto;

    private static string L(string ru, string ky, string en, string tr, string uz) => NurMarketKassa.Services.Tr.T(ru, ky, en, tr, uz);

    /// <summary>Статус цены продажи в колонке «Наценка/цена»: «авто (по общей наценке 30 %)» или «вручную».</summary>
    public string SaleStatusText
    {
        get
        {
            if (_saleMode == SalePriceMode.Manual)
                return NurMarketKassa.Services.Tr.T("вручную", "кол менен", "manual", "elle", "qo'lda");
            // 2026-11-06: «из карточки товара: 50» / «по прошлой закупке: 50» — цену не считали, она уже была известна.
            if (_saleMode == SalePriceMode.Card)
            {
                var price = SalePrice.ToString("0.##", RuCulture);
                return _saleFromHistory
                    ? NurMarketKassa.Services.Tr.T($"по прошлой закупке: {price}", $"мурунку сатып алуу боюнча: {price}", $"from the previous purchase: {price}", $"önceki alıma göre: {price}", $"oldingi xarid bo'yicha: {price}")
                    : NurMarketKassa.Services.Tr.T($"из карточки товара: {price}", $"товардын карточкасынан: {price}", $"from the product card: {price}", $"ürün kartından: {price}", $"mahsulot kartasidan: {price}");
            }
            // 2026-11-06: «авто (по общей наценке…)» — только когда цена продажи действительно получилась по наценке (закупка введена и цена равна расчётной).
            if (_saleMode != SalePriceMode.Auto || !AutoPriceComputed || NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current, out var source) is not { } m)
                return "";
            var p = m.ToString("0.##", RuCulture);
            return source == NurMarketKassa.Services.ReceivingPricing.MarkupSource.Category
                ? NurMarketKassa.Services.Tr.T($"авто (наценка категории {p} %)", $"авто (категориянын кошумчасы {p} %)", $"auto (category markup {p}%)", $"oto (kategori kârı {p} %)", $"avto (kategoriya ustamasi {p} %)")
                : NurMarketKassa.Services.Tr.T($"авто (по общей наценке {p} %)", $"авто (жалпы кошумча {p} %)", $"auto (common markup {p}%)", $"oto (ortak kâr {p} %)", $"avto (umumiy ustama {p} %)");
        }
    }

    // ------------------------------------------------------------------ 2026-10-29: «Закупка изменилась» — сравнение с прошлой закупкой простыми словами

    /// <summary>Товар новый или прежней закупки нет — сравнивать не с чем.</summary>
    public bool IsChangeNew => PurchasePrice > 0 && (IsNew || OriginalPurchasePrice <= 0);
    public bool IsChangeUp => PurchasePrice > 0 && !IsChangeNew && PurchasePrice - OriginalPurchasePrice > 0.004;
    public bool IsChangeDown => PurchasePrice > 0 && !IsChangeNew && OriginalPurchasePrice - PurchasePrice > 0.004;
    public bool IsChangeSame => PurchasePrice > 0 && !IsChangeNew && !IsChangeUp && !IsChangeDown;

    /// <summary>«↑ дороже на 5 сом (+10 %)» / «↓ дешевле на 2 сом» / «без изменений» / «новый товар»; пусто, пока закупка не введена.</summary>
    public string PurchaseChangeText
    {
        get
        {
            if (PurchasePrice <= 0)
                return "";
            if (IsChangeNew)
                return L("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot");
            var delta = Math.Abs(PurchasePrice - OriginalPurchasePrice).ToString("0.##", RuCulture);
            if (IsChangeUp)
            {
                var pct = ChangePercentText(PurchasePrice / OriginalPurchasePrice - 1);
                return L($"↑ дороже на {delta} сом (+{pct} %)", $"↑ {delta} сомго кымбат (+{pct} %)", $"↑ up by {delta} som (+{pct}%)", $"↑ {delta} som pahalı (+{pct} %)", $"↑ {delta} so'mga qimmat (+{pct} %)");
            }
            if (IsChangeDown)
            {
                var pct = ChangePercentText(1 - PurchasePrice / OriginalPurchasePrice);
                return L($"↓ дешевле на {delta} сом (−{pct} %)", $"↓ {delta} сомго арзан (−{pct} %)", $"↓ down by {delta} som (−{pct}%)", $"↓ {delta} som ucuz (−{pct} %)", $"↓ {delta} so'mga arzon (−{pct} %)");
            }
            return L("без изменений", "өзгөрүүсүз", "no change", "değişiklik yok", "o'zgarishsiz");
        }
    }

    /// <summary>Процент без лишних знаков: от 1 % — целое («3»), меньше — с десятой («0,5»).</summary>
    private static string ChangePercentText(double fraction) => (Math.Abs(fraction) * 100) is var p && p >= 1 ? Math.Round(p).ToString("0", RuCulture) : p.ToString("0.#", RuCulture);

    /// <summary>2026-10-29 (спека Fable, режим 1024): короткая форма «↑ +3 %» / «↓ −7 %» / «без изменений» / «новый товар».</summary>
    public string PurchaseChangeShort
    {
        get
        {
            if (PurchasePrice <= 0)
                return "";
            if (IsChangeUp)
                return "↑ +" + ChangePercentText(PurchasePrice / OriginalPurchasePrice - 1) + " %";
            if (IsChangeDown)
                return "↓ −" + ChangePercentText(1 - PurchasePrice / OriginalPurchasePrice) + " %";
            return PurchaseChangeText;
        }
    }

    /// <summary>Подстрока под «Закупка изменилась»: «было 145,00» (и при «без изменений»), у нового товара — «прошлой закупки нет».</summary>
    public string PurchaseWasText
    {
        get
        {
            if (PurchasePrice <= 0)
                return "";
            if (IsChangeNew)
                return L("прошлой закупки нет", "мурунку сатып алуу жок", "no previous purchase", "önceki alış yok", "oldingi xarid yo'q");
            return L($"было {OriginalPurchasePrice.ToString("0.00", RuCulture)}", $"болгон {OriginalPurchasePrice.ToString("0.00", RuCulture)}", $"was {OriginalPurchasePrice.ToString("0.00", RuCulture)}", $"önceki {OriginalPurchasePrice.ToString("0.00", RuCulture)}", $"avval {OriginalPurchasePrice.ToString("0.00", RuCulture)}");
        }
    }

    // ------------------------------------------------------------------ 2026-10-29 (спека Fable): подстрока названия, «Как посчитана цена», «Прибыль с шт.»

    /// <summary>Серая подстрока под названием: «Источник · категория» (отдельной колонки «Откуда» нет).</summary>
    public string NameSubText
    {
        get
        {
            var parts = new List<string> { SourceText };
            if (NameRenamed) // 2026-11-09: рядом с источником — отметка, что название карточки поменяется (не одним цветом)
                parts.Add(L("название изменено", "аты өзгөрдү", "name changed", "ad değişti", "nomi o'zgargan"));
            var category = NurMarketKassa.Services.ReceivingPricing.CategoryOf(this);
            if (!string.IsNullOrWhiteSpace(category))
                parts.Add(category!);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Бейдж «новый» у нового товара (AccentSoft).</summary>
    public string NewBadgeText => L("новый", "жаңы", "new", "yeni", "yangi");

    /// <summary>Прибыль с единицы числом: «45,00», «−10,00»; «—», пока нет обеих цен.</summary>
    public string ProfitNumberText
    {
        get
        {
            if (PurchasePrice <= 0 || SalePrice <= 0)
                return "—";
            var profit = (double)((decimal)SalePrice - (decimal)PurchasePrice);
            return NurMarketKassa.Services.MoneyText.Amount(profit).Replace("-", "−");
        }
    }

    /// <summary>Маржа низкая (&lt; 10 %), но цена не ниже закупки — жёлтая строка.</summary>
    public bool IsLowOnly => IsLowMargin && !IsBelowCost;

    /// <summary>Что показывает колонка «Как посчитана цена»: below → «⛔ ниже закупки», low → «⚠ маржа 8 % — мало», manual → «вручную · +10 %», auto → «авто · общая 30 %».</summary>
    public bool IsModeBelow => IsBelowCost;
    public bool IsModeLow => IsLowOnly;
    public bool IsModeManual => !IsBelowCost && !IsLowOnly && _saleMode == SalePriceMode.Manual;
    public bool IsModeAuto => !IsBelowCost && !IsLowOnly && (_saleMode == SalePriceMode.Auto || _saleMode == SalePriceMode.Card);

    /// <summary>2026-11-06: цена продажи равна расчётной по общей наценке (а не оставлена прежней правилом «не снижать» или не посчитана из-за нулевой закупки).</summary>
    private bool AutoPriceComputed => PurchasePrice > 0 && NurMarketKassa.Services.ReceivingPricing.AutoSalePrice(this, NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current) is { } auto && Math.Abs(auto - SalePrice) < 0.005;

    /// <summary>Кнопка «Как у всех» — у «вручную», «мало» и «ниже закупки» (если автонаценка вообще включена для этой строки).</summary>
    public bool ShowReturnButton => _saleMode != SalePriceMode.None && NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled
                                    && (IsBelowCost || IsLowOnly || _saleMode == SalePriceMode.Manual || (_saleMode == SalePriceMode.Card && PurchasePrice > 0));

    public string ReturnToAutoText => L("Как у всех", "Баарындай", "Like all", "Hepsi gibi", "Hammasi kabi");

    public string ModeText
    {
        get
        {
            if (IsBelowCost)
                return L("⛔ ниже закупки", "⛔ сатып алуудан төмөн", "⛔ below cost", "⛔ alışın altında", "⛔ xariddan past");
            if (IsLowOnly)
            {
                var m = (MarkupPercent ?? 0).ToString("0.#", RuCulture);
                return L($"⚠ маржа {m} % — мало", $"⚠ маржа {m} % — аз", $"⚠ margin {m}% — low", $"⚠ marj {m} % — düşük", $"⚠ marja {m} % — kam");
            }
            if (_saleMode == SalePriceMode.Manual)
            {
                var m = MarkupPercent is { } v ? (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.#", RuCulture) + " %" : "";
                return L("вручную", "кол менен", "manual", "elle", "qo'lda") + (m.Length > 0 ? " · " + m : "");
            }
            if (_saleMode == SalePriceMode.Card)
                return _saleFromHistory
                    ? L("по прошлой закупке", "мурунку сатып алуу боюнча", "previous purchase", "önceki alım", "oldingi xarid")
                    : L("из карточки", "карточкадан", "from the card", "karttan", "kartadan");
            if (_saleMode == SalePriceMode.Auto && AutoPriceComputed && NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current, out var source) is { } pct)
            {
                var p = pct.ToString("0.##", RuCulture);
                return source == NurMarketKassa.Services.ReceivingPricing.MarkupSource.Category
                    ? L($"авто · кат. {p} %", $"авто · кат. {p} %", $"auto · cat. {p}%", $"oto · kat. {p} %", $"avto · kat. {p} %")
                    : L($"авто · общая {p} %", $"авто · жалпы {p} %", $"auto · common {p}%", $"oto · ortak {p} %", $"avto · umumiy {p} %");
            }
            return "";
        }
    }

    /// <summary>Всплывающая подсказка «Как посчитана цена»: пояснение + прибыль с единицы (в 1024 колонки «Прибыль» нет).</summary>
    public string ModeHint => (SaleModeHint + (ProfitText.Length > 0 ? "\n" + ProfitText : "")).Trim();

    /// <summary>Подсказка к ячейке: что с чем сравнивается.</summary>
    public string PurchaseChangeHint
    {
        get
        {
            var head = L("Сравнение с прошлой закупкой этого товара.", "Бул товардын мурунку сатып алуу баасы менен салыштыруу.", "Comparison with the previous purchase price of this product.",
                "Bu ürünün önceki alış fiyatıyla karşılaştırma.", "Bu mahsulotning oldingi xarid narxi bilan taqqoslash.");
            return IsChangeNew || OriginalPurchasePrice <= 0
                ? head
                : head + " " + L($"Было {OriginalPurchasePrice.ToString("0.00", RuCulture)}, стало {PurchasePrice.ToString("0.00", RuCulture)}.", $"Болгон {OriginalPurchasePrice.ToString("0.00", RuCulture)}, азыр {PurchasePrice.ToString("0.00", RuCulture)}.",
                    $"Was {OriginalPurchasePrice.ToString("0.00", RuCulture)}, now {PurchasePrice.ToString("0.00", RuCulture)}.", $"Önceki {OriginalPurchasePrice.ToString("0.00", RuCulture)}, şimdi {PurchasePrice.ToString("0.00", RuCulture)}.", $"Avval {OriginalPurchasePrice.ToString("0.00", RuCulture)}, hozir {PurchasePrice.ToString("0.00", RuCulture)}.");
        }
    }

    /// <summary>Цена продажи ниже закупки — вся строка красным.</summary>
    public bool IsBelowCost => PurchasePrice > 0 && SalePrice > 0 && SalePrice < PurchasePrice;

    /// <summary>2026-11-06: у строки с количеством нет цены продажи — строка подсвечивается, а перед отправкой прихода спрашиваем цену (программа сама её не ставит).</summary>
    public bool IsSalePriceMissing => Quantity > 0 && SalePrice <= 0;

    /// <summary>Строка создана вручную (скан, список, «Новый товар») — цена продажи считается по наценке, если автонаценка включена в настройках.</summary>
    public void EnableAutoSale()
    {
        if (!NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled)
            return;
        _saleMode = SalePriceMode.Auto;
        ApplyAutoSale(); // 2026-10-28: общая наценка сразу действует и на уже известную закупку (скан товара со склада)
        NotifyMode();
    }

    /// <summary>2026-11-06 («Приём товара»): цена продажи по умолчанию. Товар со склада с известной ценой продажи (карточка, а если её нет — прошлая закупка
    /// <paramref name="historySale"/>) оставляет её: общая «Наценка для всех» эту цену молча не меняет, только «Как у всех» / «Применить ко всем».
    /// Новый товар и товар без известной цены считаются по общей наценке (режим «авто»), когда введена закупка.</summary>
    public void EnableReceivingPricing(double historySale = 0)
    {
        if (!IsNew && OriginalSalePrice > 0)
        {
            _saleMode = SalePriceMode.Card;
            _saleFromHistory = false;
            NotifyMode();
            return;
        }
        if (!IsNew && historySale > 0)
        {
            SalePrice = historySale;
            _saleMode = SalePriceMode.Card;
            _saleFromHistory = true;
            NotifyMode();
            return;
        }
        EnableAutoSale();
    }

    /// <summary>Кнопка «Как у всех»: вернуть строке расчёт по общей наценке и сразу пересчитать.</summary>
    public void ReturnToAuto()
    {
        if (!NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled)
            return;
        if (_saleMode == SalePriceMode.Card && PurchasePrice <= 0)
            return; // 2026-11-06: считать не от чего — прежняя известная цена остаётся и не помечается «авто»
        _saleMode = SalePriceMode.Auto;
        ApplyAutoSale(allowBelowCurrent: true); // 2026-11-05 (С-3): «Как у всех» — явная просьба, цена может быть и ниже прежней
        NotifyMode();
    }

    /// <summary>Пересчитать цену продажи по закупке и общей наценке, если строка в режиме «авто». Нулевая закупка цену не трогает.
    /// true — цена изменилась.</summary>
    public bool ApplyAutoSale(bool allowBelowCurrent = false)
    {
        var settings = NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current;
        if (_saleMode != SalePriceMode.Auto || !settings.AutoEnabled || PurchasePrice <= 0)
            return false;
        var target = NurMarketKassa.Services.ReceivingPricing.AutoSalePrice(this, settings);
        if (target is not { } sale || sale <= 0 || Math.Abs(sale - SalePrice) < 1e-9)
        {
            NotifyMode();
            return false;
        }
        // 2026-11-05 (приёмка волн 0–3, С-3): у товара склада с уже заданной ценой продажи автонаценка только повышает цену (не ниже прежней); понижение —
        // руками или кнопкой «Как у всех» (явное действие, allowBelowCurrent). Иначе приход молча ронял цену (1,00 → 0,50 при общей наценке 20 %).
        if (!allowBelowCurrent && !IsNew && OriginalSalePrice > 0 && sale < OriginalSalePrice - 0.004)
        {
            if (Math.Abs(SalePrice - OriginalSalePrice) < 1e-9)
            {
                NotifyMode();
                return false;
            }
            sale = OriginalSalePrice;
        }
        SalePrice = sale; // свойство, не ввод: режим остаётся «авто»
        return true;
    }

    private void MarkSaleManual()
    {
        if (_saleMode is not (SalePriceMode.Auto or SalePriceMode.Card))
            return;
        _saleMode = SalePriceMode.Manual;
        NotifyMode();
    }

    private void NotifyMode()
    {
        OnPropertyChanged(nameof(SaleMode));
        OnPropertyChanged(nameof(HasSaleMode));
        OnPropertyChanged(nameof(CanReturnToAuto));
        OnPropertyChanged(nameof(SaleModeText));
        OnPropertyChanged(nameof(SaleButtonText));
        OnPropertyChanged(nameof(SaleStatusText));
        OnPropertyChanged(nameof(IsSaleAuto));
        NotifyView();
        OnPropertyChanged(nameof(SaleModeHint));
        OnPropertyChanged(nameof(ProfitText));
    }

    private void SetFromText(string? text, Action<double> apply, string name, Action<string?> setRaw)
    {
        string? qtyProblem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            apply(0);
            setRaw("");
        }
        else
        {
            _inTextSet = true;
            try
            {
                setRaw(text.Trim()); // 2026-10-28: «Принято» тоже хранит набранный текст («2.», «2,5») до выхода из ячейки (CommitText)
                if (NurMarketKassa.Services.ProductCsvImporter.TryParseNumber(text, out var value) && value >= 0)
                {
                    // 2026-10-29: сервер принимает количество не больше чем с 3 знаками после запятой (кг, л).
                    // 2026-10-31 («Не округляй»): больше 3 знаков НЕ округляем и не обрезаем — значение остаётся как набрано, красное пояснение
                    // (PriceProblemText → QuantityPrecisionText) и строку не проведут (PurchaseReceivingService.Validate), пока не исправят.
                    apply(value);
                }
            }
            finally
            {
                _inTextSet = false;
            }
        }
        if (qtyProblem != _priceProblem)
        {
            _priceProblem = qtyProblem;
            OnPropertyChanged(nameof(PriceProblemText));
            OnPropertyChanged(nameof(LowMarginText));
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
    private bool _inMarkupSet; // идёт ввод наценки в ячейку: пересчёт цены не сбрасывает набранный текст наценки

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
                // 2026-10-24 (Б1, стенд приёмки): таблица сама «записывает» показанную наценку обратно при создании строки — равное текущему
                // значению не трогает цену продажи (иначе «410» превращалось в «411» из-за округления вверх при каждом показе строки).
                _markupRaw = value.Trim(); // «12.», «12,5» остаются в ячейке как набраны до CommitText (ставится ДО пересчёта цены)
                if (PurchasePrice > 0 && !(MarkupPercent is { } current && Math.Abs(current - markup) < 0.005))
                {
                    _inMarkupSet = true;
                    try
                    {
                        SalePrice = NurMarketKassa.Services.ReceivingPricing.SaleFromMarkup(PurchasePrice, markup);
                    }
                    finally
                    {
                        _inMarkupSet = false;
                    }
                    MarkSaleManual(); // 2026-10-28: наценку вписали руками — автопересчёт цены этой строки выключен
                }
            }
            else
                _markupRaw = value.Trim();
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
    public bool IsPurchaseChanged => OriginalPurchasePrice > 0 && Math.Abs(PurchasePrice - OriginalPurchasePrice) > 0.00005; // 2026-11-02: закупка до 4 знаков

    public string PreviousPurchaseText => IsPurchaseChanged
        ? NurMarketKassa.Services.Tr.T($"было {OriginalPurchasePrice:0.00##}", $"мурда {OriginalPurchasePrice:0.00##}", $"was {OriginalPurchasePrice:0.00##}",
            $"önceki {OriginalPurchasePrice:0.00##}", $"avval {OriginalPurchasePrice:0.00##}")
        : "";

    /// <summary>Наценка ниже порога (ReceivingPricing.LowMarginPercent) или продажа ниже закупки — красная пометка.</summary>
    public bool IsLowMargin => NurMarketKassa.Services.ReceivingPricing.IsLowMargin(PurchasePrice, SalePrice);

    // 2026-10-28: непринятое число цены (PriceProblemText) показывается здесь же, в красной строке под ценой.
    public string LowMarginText => PriceProblemText is { Length: > 0 } problem ? problem
        : IsLowMargin && MarkupPercent is { } m
            ? NurMarketKassa.Services.Tr.T($"маржа {m:0.#} %", $"маржа {m:0.#} %", $"margin {m:0.#}%", $"marj {m:0.#} %", $"marja {m:0.#} %")
            : "";

    /// <summary>Кнопка «Обновить цены продажи» может поправить эту строку: закупка подорожала, а продажу не трогали.</summary>
    public bool CanRaiseSalePrice => NurMarketKassa.Services.ReceivingPricing.IsRaisable(this);

    private void NotifyPricing()
    {
        if (!_inMarkupSet) _markupRaw = null;
        OnPropertyChanged(nameof(MarkupPercent));
        OnPropertyChanged(nameof(MarkupText));
        OnPropertyChanged(nameof(IsPurchaseRaised));
        OnPropertyChanged(nameof(PurchaseRaiseText));
        OnPropertyChanged(nameof(IsPurchaseChanged));
        OnPropertyChanged(nameof(PreviousPurchaseText));
        OnPropertyChanged(nameof(IsLowMargin));
        OnPropertyChanged(nameof(LowMarginText));
        OnPropertyChanged(nameof(CanRaiseSalePrice));
        OnPropertyChanged(nameof(ProfitText));
        OnPropertyChanged(nameof(SaleModeHint));
        OnPropertyChanged(nameof(SaleStatusText));
        OnPropertyChanged(nameof(PurchaseChangeText));
        OnPropertyChanged(nameof(PurchaseChangeHint));
        OnPropertyChanged(nameof(IsChangeNew));
        OnPropertyChanged(nameof(IsChangeUp));
        OnPropertyChanged(nameof(IsChangeDown));
        OnPropertyChanged(nameof(IsChangeSame));
        OnPropertyChanged(nameof(IsBelowCost));
        NotifyView();
    }

    /// <summary>2026-10-29: свойства колонок по спеке Fable — пересчитываются при любой смене цены, режима или источника.</summary>
    private void NotifyView()
    {
        OnPropertyChanged(nameof(PurchaseChangeShort));
        OnPropertyChanged(nameof(PurchaseWasText));
        OnPropertyChanged(nameof(NameSubText));
        OnPropertyChanged(nameof(ProfitNumberText));
        OnPropertyChanged(nameof(IsLowOnly));
        OnPropertyChanged(nameof(IsModeBelow));
        OnPropertyChanged(nameof(IsModeLow));
        OnPropertyChanged(nameof(IsModeManual));
        OnPropertyChanged(nameof(IsModeAuto));
        OnPropertyChanged(nameof(ShowReturnButton));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ModeHint));
    }

    /// <summary>2026-10-28: сумма строки — округление до копеек по ИТОГУ (3 × 12,333 = 36,999 → 37,00), а не по цене (ReceivingPricing.LineTotal).</summary>
    public double LineTotal => NurMarketKassa.Services.ReceivingPricing.LineTotal(Quantity, PurchasePrice);

    /// <summary>2026-10-19: закупка по скану накладной (InvoiceEditorWindow) — категория и весовой товар у нового товара, как раньше
    /// давал скан при создании товара напрямую. Приёмка склада их не задаёт (null / false — как прежде).</summary>
    public string? CategoryName { get; set; }

    private bool _isWeight;

    /// <summary>2026-10-31 (владелец: «в приёмке весовые добавляются???»): новый товар с единицей «кг» создаётся весовым, даже если признак не ставили
    /// отдельно (приёмка «Приём товара», массовое сканирование, накладная давали «кг» без is_weight — товар был «кг» на кассе, но не весовой на сервере).
    /// Граммы весовыми не считаются (как ProductUnitNormalizer.IsKilogramWord).</summary>
    public bool IsWeight
    {
        get => _isWeight || string.Equals(NurMarketKassa.Services.UnitCatalog.Normalize(_unit), "кг", StringComparison.Ordinal);
        set => _isWeight = value;
    }

    /// <summary>Товара у магазина ещё нет — он будет создан при проведении приёмки.</summary>
    public bool IsNew => Source != ReceivingSource.Warehouse;

    /// <summary>2026-11-09 (владелец: «указывать вначале как на старом — база / база NurCRM / неизвестно / из интернета»): короткое название источника строки.</summary>
    public string SourceText => SourceTitle(Source);

    /// <summary>Подсказка к метке источника (ToolTip значка в начале строки).</summary>
    public string SourceHint => SourceHintFor(Source);

    /// <summary>Название + пояснение одной строкой — для подсказки и для AutomationProperties.Name значка.</summary>
    public string SourceAutomationText => SourceText + ": " + SourceHint;

    public static string SourceTitle(ReceivingSource source) => source switch
    {
        ReceivingSource.GlobalBase => NurMarketKassa.Services.Tr.T("База NurCRM", "NurCRM базасы", "NurCRM base", "NurCRM bazı", "NurCRM bazasi"),
        ReceivingSource.Unknown => NurMarketKassa.Services.Tr.T("Неизвестно", "Белгисиз", "Unknown", "Bilinmiyor", "Noma'lum"),
        ReceivingSource.Internet => NurMarketKassa.Services.Tr.T("Из интернета", "Интернеттен", "From the internet", "İnternetten", "Internetdan"),
        _ => NurMarketKassa.Services.Tr.T("База", "База", "Store base", "Mağaza bazı", "Baza"),
    };

    public static string SourceHintFor(ReceivingSource source) => source switch
    {
        ReceivingSource.GlobalBase => NurMarketKassa.Services.Tr.T(
            "Товар из общей базы NurCRM: у вашего магазина его ещё нет, карточка будет создана из общей базы при проведении прихода.",
            "NurCRM жалпы базасындагы товар: магазиныңызда али жок, кирүүнү өткөргөндө карточка жалпы базадан түзүлөт.",
            "A product from the shared NurCRM base: your store does not have it yet, the card will be created from the shared base when the receiving is posted.",
            "Ortak NurCRM bazından ürün: mağazanızda henüz yok, mal kabul işlenirken kart ortak bazdan oluşturulacak.",
            "Umumiy NurCRM bazasidagi mahsulot: do'koningizda hali yo'q, kirim o'tkazilganda karta umumiy bazadan yaratiladi."),
        ReceivingSource.Unknown => NurMarketKassa.Services.Tr.T(
            "Штрихкод нигде не найден: нужен новый товар — впишите название или нажмите «Найти в интернете».",
            "Штрихкод эч жерден табылган жок: жаңы товар керек — атын жазыңыз же «Интернеттен издөө» басыңыз.",
            "The barcode was not found anywhere: a new product is needed — type a name or press “Find on the internet”.",
            "Barkod hiçbir yerde bulunamadı: yeni ürün gerekli — adı yazın veya «İnternette bul»a basın.",
            "Shtrix-kod hech qayerda topilmadi: yangi mahsulot kerak — nomini yozing yoki «Internetdan topish»ni bosing."),
        ReceivingSource.Internet => NurMarketKassa.Services.Tr.T(
            "Название и категория подобраны из интернета по вашему нажатию — проверьте их перед проведением. Товар будет создан новым.",
            "Аталышы жана категориясы сиздин басууңуз боюнча интернеттен алынды — өткөрүүдөн мурун текшериңиз. Товар жаңы болуп түзүлөт.",
            "The name and category were taken from the internet when you pressed the button — check them before posting. The product will be created as new.",
            "Ad ve kategori düğmeye bastığınızda internetten alındı — işlemeden önce kontrol edin. Ürün yeni olarak oluşturulacak.",
            "Nomi va toifasi siz bosganingizda internetdan olindi — o'tkazishdan oldin tekshiring. Mahsulot yangi sifatida yaratiladi."),
        _ => NurMarketKassa.Services.Tr.T(
            "Товар из вашей базы (склад магазина): карточка уже есть, приход добавит остаток.",
            "Сиздин базадагы товар (магазиндин кампасы): карточка бар, кирүү калдыкты кошот.",
            "A product from your own base (the store warehouse): the card exists, the receiving adds stock.",
            "Kendi bazanızdaki ürün (mağaza deposu): kart var, mal kabul stoğu artırır.",
            "O'z bazangizdagi mahsulot (do'kon ombori): karta bor, kirim qoldiqni oshiradi."),
    };

    // ------------------------------------------------------------------ 2026-11-09: правка названия и «Найти в интернете»

    /// <summary>Название товара склада поправили в приёмке (при проведении оно уйдёт в карточку — PurchaseReceivingService.AddNameAndUnit). У нового товара название и есть название.</summary>
    public bool NameRenamed =>
        !IsNew && OriginalName.Length > 0 && ProductName.Trim().Length > 0 && !string.Equals(ProductName.Trim(), OriginalName.Trim(), StringComparison.Ordinal);

    /// <summary>Подсказка к названию: «Было: …» и что произойдёт при проведении; пусто, если название не менялось.</summary>
    public string NameWasHint => NameRenamed
        ? L($"Было: {OriginalName.Trim()}. При проведении прихода карточка товара будет переименована.",
            $"Мурун: {OriginalName.Trim()}. Кирүүнү өткөргөндө товардын карточкасынын аты өзгөрөт.",
            $"Was: {OriginalName.Trim()}. When the receiving is posted the product card will be renamed.",
            $"Önceki: {OriginalName.Trim()}. Mal kabul işlenirken ürün kartı yeniden adlandırılacak.",
            $"Avval: {OriginalName.Trim()}. Kirim o'tkazilganda mahsulot kartasi qayta nomlanadi.")
        : "";

    /// <summary>Пустое название нельзя: у товара склада после правки возвращается прежнее, у нового — остаётся пустым (проведение попросит ввести). true — название вернули.</summary>
    public bool CommitName()
    {
        if (!string.IsNullOrWhiteSpace(ProductName))
        {
            var trimmed = ProductName.Trim();
            if (trimmed != ProductName)
                ProductName = trimmed;
            return false;
        }
        if (!IsNew && OriginalName.Length > 0)
        {
            ProductName = OriginalName;
            return true;
        }
        return false;
    }

    private bool _internetSearching;

    /// <summary>Идёт поиск в интернете по нажатию «Найти в интернете».</summary>
    public bool InternetSearching
    {
        get => _internetSearching;
        set
        {
            if (Set(ref _internetSearching, value))
                OnPropertyChanged(nameof(NotSearchingInternet));
        }
    }

    /// <summary>Кнопка «Найти в интернете» у строки видна: штрихкод не найден нигде («Неизвестно») и похож на настоящий (8–14 цифр). Сам поиск никогда не запускается — только по нажатию.</summary>
    public bool CanSearchInternet => Source == ReceivingSource.Unknown && NurMarketKassa.Services.ReceivingInternetLookup.IsSearchable(Barcode);

    /// <summary>Кнопка «Найти в интернете» доступна (поиск не идёт) — для IsEnabled в разметке.</summary>
    public bool NotSearchingInternet => !_internetSearching;

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
