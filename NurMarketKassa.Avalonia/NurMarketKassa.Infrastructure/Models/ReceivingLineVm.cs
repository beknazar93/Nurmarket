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

/// <summary>2026-10-28: как считается цена продажи строки в ручной приёмке (см. ReceivingLineVm.SaleMode).</summary>
public enum SalePriceMode
{
    None,
    Auto,
    Manual,
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
                if (!_inTextSet) _purchaseRaw = null;
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
        get => _quantityRaw ?? Quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
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
    }

    /// <summary>Ввод закупки руками: значение; с автонаценкой (строка в режиме «авто») цена продажи пересчитывается.</summary>
    public void SetPurchaseText(string? text)
    {
        var before = PurchasePrice;
        SetPriceFromText(text, NurMarketKassa.Services.ReceivingPricing.PurchaseDecimals, v => PurchasePrice = v, nameof(PurchasePriceText), r => _purchaseRaw = r);
        if (Math.Abs(PurchasePrice - before) > 1e-9)
            ApplyAutoSale();
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
    public string PriceProblemText => _priceProblem ?? "";

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
    public bool CanReturnToAuto => _saleMode == SalePriceMode.Manual;

    public string SaleModeText => _saleMode switch
    {
        SalePriceMode.Auto => NurMarketKassa.Services.Tr.T("авто", "авто", "auto", "oto", "avto"),
        SalePriceMode.Manual => NurMarketKassa.Services.Tr.T("вручную", "колдо", "manual", "elle", "qo'lda"),
        _ => "",
    };

    /// <summary>Подпись кнопки в колонке «Цена: авто»: «авто» (считается по общей наценке) или «Как у всех» (строка правилась руками — нажатие возвращает).</summary>
    public string SaleButtonText => _saleMode == SalePriceMode.Manual
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
            var profit = (decimal)SalePrice - (decimal)PurchasePrice;
            var x = Math.Abs(profit).ToString("0.##", RuCulture);
            var unit = string.IsNullOrWhiteSpace(Unit) ? "шт" : Unit.Trim();
            return profit >= 0
                ? NurMarketKassa.Services.Tr.T($"прибыль {x} сом с {unit}.", $"{unit} үчүн пайда {x} сом", $"profit {x} som per {unit}", $"{unit} başına kâr {x} som", $"{unit} uchun foyda {x} so'm")
                : NurMarketKassa.Services.Tr.T($"убыток {x} сом с {unit}.", $"{unit} үчүн чыгым {x} сом", $"loss {x} som per {unit}", $"{unit} başına zarar {x} som", $"{unit} uchun zarar {x} so'm");
        }
    }

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
            if (_saleMode != SalePriceMode.Auto || NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current, out var source) is not { } m)
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
    public bool IsModeAuto => !IsBelowCost && !IsLowOnly && _saleMode == SalePriceMode.Auto;

    /// <summary>Кнопка «Как у всех» — у «вручную», «мало» и «ниже закупки» (если автонаценка вообще включена для этой строки).</summary>
    public bool ShowReturnButton => _saleMode != SalePriceMode.None && NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled
                                    && (IsBelowCost || IsLowOnly || _saleMode == SalePriceMode.Manual);

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
            if (_saleMode == SalePriceMode.Auto && NurMarketKassa.Services.ReceivingPricing.AutoMarkup(this, NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current, out var source) is { } pct)
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

    /// <summary>Строка создана вручную (скан, список, «Новый товар») — цена продажи считается по наценке, если автонаценка включена в настройках.</summary>
    public void EnableAutoSale()
    {
        if (!NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled)
            return;
        _saleMode = SalePriceMode.Auto;
        ApplyAutoSale(); // 2026-10-28: общая наценка сразу действует и на уже известную закупку (скан товара со склада)
        NotifyMode();
    }

    /// <summary>Кнопка «Как у всех»: вернуть строке расчёт по общей наценке и сразу пересчитать.</summary>
    public void ReturnToAuto()
    {
        if (!NurMarketKassa.Services.ReceivingMarkupSettingsStore.Current.AutoEnabled)
            return;
        _saleMode = SalePriceMode.Auto;
        ApplyAutoSale();
        NotifyMode();
    }

    /// <summary>Пересчитать цену продажи по закупке и общей наценке, если строка в режиме «авто». Нулевая закупка цену не трогает.
    /// true — цена изменилась.</summary>
    public bool ApplyAutoSale()
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
        SalePrice = sale; // свойство, не ввод: режим остаётся «авто»
        return true;
    }

    private void MarkSaleManual()
    {
        if (_saleMode != SalePriceMode.Auto)
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
                    apply(value);
            }
            finally
            {
                _inTextSet = false;
            }
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
    public bool IsPurchaseChanged => OriginalPurchasePrice > 0 && Math.Abs(PurchasePrice - OriginalPurchasePrice) > 0.004;

    public string PreviousPurchaseText => IsPurchaseChanged
        ? NurMarketKassa.Services.Tr.T($"было {OriginalPurchasePrice:0.00}", $"мурда {OriginalPurchasePrice:0.00}", $"was {OriginalPurchasePrice:0.00}",
            $"önceki {OriginalPurchasePrice:0.00}", $"avval {OriginalPurchasePrice:0.00}")
        : "";

    /// <summary>Наценка ниже порога (ReceivingPricing.LowMarginPercent) или продажа ниже закупки — красная пометка.</summary>
    public bool IsLowMargin => NurMarketKassa.Services.ReceivingPricing.IsLowMargin(PurchasePrice, SalePrice);

    // 2026-10-28: непринятое число цены (PriceProblemText) показывается здесь же, в красной строке под ценой.
    public string LowMarginText => _priceProblem is { Length: > 0 } problem ? problem
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
