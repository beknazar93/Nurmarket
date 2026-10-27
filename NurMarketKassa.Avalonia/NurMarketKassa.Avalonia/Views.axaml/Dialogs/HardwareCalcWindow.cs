using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-26 (ТЗ «Хозмаг», ХМ5): окно «Сколько нужно?» — калькулятор расхода: плитка, ламинат, краска, обои, сухая смесь.
/// Площадь (или Д × Ш) минус проёмы → запас → упаковки вверх → «В чек». Формулы — в <see cref="ConsumptionCalculator"/>
/// (без интерфейса и денег), здесь только ввод, показ итога и кнопка «В чек». Окно собрано в коде, цвета — из темы,
/// оболочка ModalShell, поля и кнопки ≥ 44 px (сенсор 1024×600), светлая и тёмная тема. Результат ShowDialog — true, если
/// товар отправлен в чек. Макеты Д-ХМ2 (design\NurMarket-design\screens\Hardware-*.html) на момент пакета не готовы —
/// вид сделан по соседним диалогам; сверить с макетом, когда он появится.
/// Доступность: окно открывает только карточка товара при <see cref="MarketSpheres.IsHardware"/>.
/// </summary>
internal sealed class HardwareCalcWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>2026-10-26: можно ли добавить в чек ДРОБНОЕ количество этого товара (14,4 м²). Корзина сейчас округляет
    /// количество до целого, если товар не весовой (BasketPanelViewModel.ParseQuantity), — молча положить 14 вместо 14,4
    /// нельзя. Пока дробное только у весового; когда ХМ2 (QuantityRules) включит дробное количество по единице, достаточно
    /// заменить эту проверку, например на <c>p =&gt; QuantityRules.For(p).Fractional</c>. Целые упаковки идут всегда.</summary>
    internal static Func<CatalogProductTileVm, bool> SupportsFractionalCart { get; set; } = p => p.MustWeigh;

    private readonly CatalogProductTileVm _product;
    private readonly Func<double, Task> _addToCart;
    private readonly HardwareCalcSettings _settings = HardwareCalcSettings.Current;
    private readonly decimal _price;

    private CalcKind _kind;
    private bool _byDims;
    private bool _suspend;
    private bool _adding;
    private bool _narrow;
    private CartPlan _plan = new() { Problem = CartPlanProblem.NoResult };
    private CalcNorm? _norm;
    private readonly List<CalcNorm> _normChoices = new();

    private readonly Dictionary<CalcKind, RadioButton> _kindButtons = new();
    private readonly List<(decimal Value, RadioButton Button)> _reserveChips = new();
    private RadioButton _modeArea = null!;
    private RadioButton _modeDims = null!;

    private readonly TextBox _area = new();
    private readonly TextBox _length = new();
    private readonly TextBox _width = new();
    private readonly TextBox _openings = new();
    private readonly TextBox _pack = new();
    private readonly TextBox _consumption = new();
    private readonly TextBox _layers = new();
    private readonly TextBox _perimeter = new();
    private readonly TextBox _height = new();
    private readonly TextBox _rollLength = new();
    private readonly TextBox _rollWidth = new();
    private readonly TextBox _rapport = new();
    private readonly TextBox _reserve = new();
    private readonly ComboBox _normBox = new();

    private StackPanel _areaPanel = null!;
    private Control _areaField = null!;
    private Control _dimsField = null!;
    private StackPanel _normPanel = null!;
    private StackPanel _wallPanel = null!;
    private Control _packField = null!;
    private TextBlock _packLabel = null!;
    private TextBlock _diagonalHint = null!;
    private Grid _body = null!;
    private Border _resultBox = null!;

    private readonly TextBlock _l1 = Label(), _v1 = Value(), _l2 = Label(), _v2 = Value(), _l3 = Label(), _v3 = Value(26);
    private readonly TextBlock _sub = Label(), _priceText = new() { FontSize = 18, FontWeight = FontWeight.Bold }, _note = Label();
    private readonly TextBlock _hint = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private TextBlock _error = null!;
    private Border _errorBox = null!;
    private Button _add = null!;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public HardwareCalcWindow(CatalogProductTileVm product, Func<double, Task> addToCart)
    {
        _product = product;
        _addToCart = addToCart;
        _price = (decimal)LocalCartService.ParsePrice(product.PriceLine);
        Title = T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?");
        SectionCards.AsModal(this, 760);

        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(BuildKinds());

        _body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,260") };
        var inputs = BuildInputs();
        Grid.SetColumn(inputs, 0);
        _body.Children.Add(inputs);
        _resultBox = BuildResult();
        Grid.SetColumn(_resultBox, 2);
        _body.Children.Add(_resultBox);
        // Узкое окно (телефон, малый экран): итог уходит под ввод.
        _body.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        root.Children.Add(_body);

        _error = ErrorLine();
        _errorBox = SectionCards.ErrorBox(this, _error);
        // Ошибка и подсказка — в подвале рядом с кнопками (не прокручиваются): на экране 1024×600 строка под полями уходила за край.

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close(false);
        // Enter не делает «В чек»: сканер штрихкодов шлёт Enter в активное поле, товар ушёл бы в чек случайно.
        _add = SectionCards.Primary(T("В чек", "Чекке", "To receipt", "Fişe", "Chekka"));
        _add.IsEnabled = false;
        _add.Click += async (_, _) => await AddAsync().ConfigureAwait(true);

        var shell = SectionCards.Shell(
            T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?"),
            product.Title, root, SectionCards.Buttons(close, _add), _errorBox);
        shell.InitialFocus = _area;
        Content = shell;

        _suspend = true;
        _reserve.Text = Fmt(_settings.ReservePercent);
        _suspend = false;
        SelectKind(_settings.GuessKind(product.Title, product.Category));
    }

    // ───────────────────────── построение ─────────────────────────

    private static TextBlock Label() => new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Value(double size = 20) => new() { FontSize = size, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));

    private TextBlock ErrorLine()
    {
        var t = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        return t;
    }

    private RadioButton Seg(string text, string group)
    {
        var r = new RadioButton { Content = text, GroupName = group, Focusable = true, Margin = new Thickness(0, 0, 8, 8) };
        r.Classes.Add("modal-seg");
        AutomationProperties.SetName(r, text);
        return r;
    }

    private Control BuildKinds()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(CalcKind kind, string text)
        {
            var r = Seg(text, "hwcalc-kind");
            r.IsCheckedChanged += (_, _) =>
            {
                if (r.IsChecked == true && !_suspend && _kind != kind)
                    SelectKind(kind);
            };
            _kindButtons[kind] = r;
            wrap.Children.Add(r);
        }
        Add(CalcKind.Tile, T("Плитка", "Плитка", "Tiles", "Fayans", "Plitka"));
        Add(CalcKind.Laminate, T("Ламинат", "Ламинат", "Laminate", "Laminat", "Laminat"));
        Add(CalcKind.Paint, T("Краска", "Боёк", "Paint", "Boya", "Bo'yoq"));
        Add(CalcKind.Wallpaper, T("Обои", "Обои", "Wallpaper", "Duvar kağıdı", "Oboy"));
        Add(CalcKind.Mix, T("Смесь, клей", "Аралашма, желим", "Mix, glue", "Karışım, yapıştırıcı", "Aralashma, yelim"));
        return wrap;
    }

    private TextBlock FieldLabel(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
        return l;
    }

    private StackPanel Labeled(string label, TextBox box, out TextBlock labelBlock)
    {
        box.FontSize = 18;
        box.MaxLength = 10;
        SectionCards.Field(box);
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => OnInput();
        labelBlock = FieldLabel(label);
        return new StackPanel { Spacing = 4, Children = { labelBlock, box } };
    }

    private StackPanel Labeled(string label, TextBox box) => Labeled(label, box, out _);

    private static Grid Two(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(b, 2);
        g.Children.Add(a);
        g.Children.Add(b);
        return g;
    }

    private Control BuildInputs()
    {
        var left = new StackPanel { Spacing = 12 };

        // — площадь: числом или Длина × Ширина, проёмы —
        _areaPanel = new StackPanel { Spacing = 10 };
        _modeArea = Seg(T("Площадь, м²", "Аянты, м²", "Area, m²", "Alan, m²", "Maydon, m²"), "hwcalc-mode");
        _modeDims = Seg(T("Длина × ширина", "Узундугу × туурасы", "Length × width", "Uzunluk × genişlik", "Uzunligi × kengligi"), "hwcalc-mode");
        _modeArea.IsChecked = true;
        _modeArea.IsCheckedChanged += (_, _) => { if (_modeArea.IsChecked == true) SetMode(false); };
        _modeDims.IsCheckedChanged += (_, _) => { if (_modeDims.IsChecked == true) SetMode(true); };
        _areaPanel.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _modeArea, _modeDims } });
        _areaField = Labeled(T("Площадь стен или пола, м²", "Дубал же полдун аянты, м²", "Wall or floor area, m²", "Duvar veya zemin alanı, m²", "Devor yoki pol maydoni, m²"), _area);
        _dimsField = Two(
            Labeled(T("Длина, м", "Узундугу, м", "Length, m", "Uzunluk, m", "Uzunligi, m"), _length),
            Labeled(T("Ширина, м", "Туурасы, м", "Width, m", "Genişlik, m", "Kengligi, m"), _width));
        _dimsField.IsVisible = false;
        _areaPanel.Children.Add(_areaField);
        _areaPanel.Children.Add(_dimsField);
        // Проёмы и размер упаковки — в одной строке (экран 1024×600: меньше прокрутки).
        _packField = Labeled("", _pack, out _packLabel);
        _areaPanel.Children.Add(Two(
            Labeled(T("Проёмы (двери, окна), м²", "Тешиктер (эшик, терезе), м²", "Openings (doors, windows), m²", "Boşluklar (kapı, pencere), m²", "Teshiklar (eshik, deraza), m²"), _openings),
            _packField));
        left.Children.Add(_areaPanel);

        // — обои —
        _wallPanel = new StackPanel { Spacing = 10 };
        _wallPanel.Children.Add(Two(
            Labeled(T("Периметр комнаты, м", "Бөлмөнүн периметри, м", "Room perimeter, m", "Oda çevresi, m", "Xona perimetri, m"), _perimeter),
            Labeled(T("Высота стен, м", "Дубалдын бийиктиги, м", "Wall height, m", "Duvar yüksekliği, m", "Devor balandligi, m"), _height)));
        _wallPanel.Children.Add(Two(
            Labeled(T("Длина рулона, м", "Рулондун узундугу, м", "Roll length, m", "Rulo uzunluğu, m", "Rulon uzunligi, m"), _rollLength),
            Labeled(T("Ширина рулона, м", "Рулондун туурасы, м", "Roll width, m", "Rulo genişliği, m", "Rulon kengligi, m"), _rollWidth)));
        _wallPanel.Children.Add(Labeled(
            T("Раппорт (шаг рисунка), см — 0, если без подгонки", "Раппорт (сүрөт кадамы), см — тууралоосуз болсо 0", "Pattern repeat, cm — 0 if none",
                "Desen aralığı, cm — yoksa 0", "Rapport (naqsh qadami), sm — yo'q bo'lsa 0"), _rapport));
        left.Children.Add(_wallPanel);

        // — норма расхода (краска, смеси) —
        _normPanel = new StackPanel { Spacing = 10 };
        _normPanel.Children.Add(FieldLabel(T("Что считаем", "Эмнени эсептейбиз", "What to calculate", "Ne hesaplanıyor", "Nimani hisoblaymiz")));
        _normBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        SectionCards.Field(_normBox);
        AutomationProperties.SetName(_normBox, T("Норма расхода", "Сарптоо нормасы", "Consumption rate", "Tüketim normu", "Sarf normasi"));
        _normBox.SelectionChanged += (_, _) => OnNormChosen();
        _normPanel.Children.Add(_normBox);
        _normPanel.Children.Add(Two(
            Labeled(T("Расход на м², за слой", "1 м² үчүн сарпталышы, бир катмарга", "Rate per m², per coat", "m² başına tüketim, kat başına", "1 m² uchun sarfi, bir qatlamga"), _consumption, out _consumptionLabel),
            Labeled(T("Слоёв", "Катмар", "Coats", "Kat", "Qatlam"), _layers)));
        left.Children.Add(_normPanel);


        // — запас —
        left.Children.Add(BuildReserve());
        return left;
    }

    private TextBlock _consumptionLabel = null!;

    private Control BuildReserve()
    {
        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(FieldLabel(T("Запас, %", "Запас, %", "Allowance, %", "Fire payı, %", "Zaxira, %")));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var v in new[] { 0m, 5m, 10m, 15m })
        {
            var value = v;
            var chip = Seg(Fmt(v) + " %", "hwcalc-reserve");
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked == true && !_suspend)
                {
                    _suspend = true;
                    _reserve.Text = Fmt(value);
                    _suspend = false;
                    Recalc();
                }
            };
            _reserveChips.Add((v, chip));
            row.Children.Add(chip);
        }
        _reserve.FontSize = 18;
        _reserve.MaxLength = 5;
        _reserve.Width = 96;
        _reserve.HorizontalAlignment = HorizontalAlignment.Left;
        SectionCards.Field(_reserve);
        AutomationProperties.SetName(_reserve, T("Свой запас, %", "Өз запасыңыз, %", "Custom allowance, %", "Özel fire payı, %", "O'z zaxirangiz, %"));
        _reserve.TextChanged += (_, _) => OnInput();
        row.Children.Add(_reserve);
        box.Children.Add(row);
        _diagonalHint = new TextBlock
        {
            Text = T("Диагональная укладка плитки — 15 %.", "Плитканы диагоналдуу төшөө — 15 %.", "Diagonal tile laying — 15 %.",
                "Fayansın çapraz döşenmesi — %15.", "Plitkani diagonal yotqizish — 15 %."),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        Use(_diagonalHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        box.Children.Add(_diagonalHint);
        return box;
    }

    private Border BuildResult()
    {
        foreach (var l in new[] { _l1, _l2, _l3, _sub, _note })
            Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
        foreach (var v in new[] { _v1, _v2, _v3, _priceText })
            Use(v, TextBlock.ForegroundProperty, "BrushText");
        _note.FontSize = 12;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(_hint);
        Use(_hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(_l1);
        panel.Children.Add(_v1);
        panel.Children.Add(new Border { Height = 4 });
        panel.Children.Add(_l2);
        panel.Children.Add(_v2);
        panel.Children.Add(new Border { Height = 4 });
        panel.Children.Add(_l3);
        panel.Children.Add(_v3);
        panel.Children.Add(_sub);
        panel.Children.Add(new Border { Height = 6 });
        panel.Children.Add(_priceText);
        panel.Children.Add(_note);
        var box = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14), Child = panel, VerticalAlignment = VerticalAlignment.Top };
        Use(box, Border.BackgroundProperty, "BrushPanelSoft");
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        return box;
    }

    private void ApplyLayout(double width)
    {
        var narrow = width < 560;
        if (narrow == _narrow)
            return;
        _narrow = narrow;
        if (narrow)
        {
            _body.ColumnDefinitions = new ColumnDefinitions("*");
            _body.RowDefinitions = new RowDefinitions("Auto,14,Auto");
            Grid.SetColumn(_resultBox, 0);
            Grid.SetRow(_resultBox, 2);
        }
        else
        {
            _body.ColumnDefinitions = new ColumnDefinitions("*,16,260");
            _body.RowDefinitions = new RowDefinitions();
            Grid.SetColumn(_resultBox, 2);
            Grid.SetRow(_resultBox, 0);
        }
    }

    // ───────────────────────── выбор вида и ввод ─────────────────────────

    private void SetMode(bool byDims)
    {
        if (_byDims == byDims)
            return;
        _byDims = byDims;
        _areaField.IsVisible = !byDims;
        _dimsField.IsVisible = byDims;
        Recalc();
    }

    private void SelectKind(CalcKind kind)
    {
        _suspend = true;
        _kind = kind;
        _kindButtons[kind].IsChecked = true;
        var wall = kind == CalcKind.Wallpaper;
        var normKind = kind is CalcKind.Paint or CalcKind.Mix;
        _areaPanel.IsVisible = !wall;
        _wallPanel.IsVisible = wall;
        _normPanel.IsVisible = normKind;
        _packField.IsVisible = !wall;
        _diagonalHint.IsVisible = kind == CalcKind.Tile;
        _packLabel.Text = kind switch
        {
            CalcKind.Paint => T("В банке, л", "Банкада, л", "Per can, l", "Kutuda, l", "Bankada, l"),
            CalcKind.Mix => T("В мешке (упаковке), кг", "Капта (таңгакта), кг", "Per bag (pack), kg", "Torbada (pakette), kg", "Qopda (qadoqda), kg"),
            _ => T("В упаковке, м²", "Таңгакта, м²", "Per pack, m²", "Pakette, m²", "Qadoqda, m²"),
        };
        _consumptionLabel.Text = kind == CalcKind.Paint
            ? T("Расход, л на м² за слой", "Сарпталышы, 1 м² үчүн л, бир катмарга", "Rate, l per m² per coat", "Tüketim, m² başına l, kat başına", "Sarfi, 1 m² uchun l, bir qatlamga")
            : T("Расход, кг на м² за слой", "Сарпталышы, 1 м² үчүн кг, бир катмарга", "Rate, kg per m² per coat", "Tüketim, m² başına kg, kat başına", "Sarfi, 1 m² uchun kg, bir qatlamga");

        _pack.Text = ConsumptionCalculator.GuessPackSize(kind, _product.Title) is { } size ? Fmt(size)
            : PackFromPieceOption(kind) is { } fromOption ? Fmt(fromOption) : "";

        if (normKind)
            FillNorms(kind);
        if (wall)
        {
            if (string.IsNullOrWhiteSpace(_height.Text)) _height.Text = "2,7";
            if (string.IsNullOrWhiteSpace(_rollLength.Text)) _rollLength.Text = "10,05";
            if (string.IsNullOrWhiteSpace(_rollWidth.Text)) _rollWidth.Text = "0,53";
            if (string.IsNullOrWhiteSpace(_rapport.Text)) _rapport.Text = "0";
        }
        // Запас: 10 % (или заданный владельцем); у обоев 0 — целые рулоны уже дают запас.
        _reserve.Text = Fmt(wall ? 0m : _settings.ReservePercent);
        _suspend = false;
        SyncReserveChips();
        Recalc();
    }

    /// <summary>Упаковка из карточки товара (поле «packages»): если товар продаётся в единицах расчёта (м², л, кг),
    /// QuantityInPackage — сколько этих единиц в упаковке.</summary>
    private decimal? PackFromPieceOption(CalcKind kind)
    {
        if (_product.PieceOption is not { QuantityInPackage: > 0 } option)
            return null;
        var code = HardwareProfile.UnitCode(_product.Unit);
        var expected = CalcCartPlanner.ExpectedBaseUnit(kind) switch
        {
            CalcUnit.SquareMeter => "m2",
            CalcUnit.Liter => "l",
            CalcUnit.Kilogram => "kg",
            _ => null,
        };
        return expected is not null && code == expected ? (decimal)option.QuantityInPackage : null;
    }

    private void FillNorms(CalcKind kind)
    {
        var wanted = kind == CalcKind.Paint ? CalcKind.Paint : CalcKind.Mix;
        _normChoices.Clear();
        _normChoices.AddRange(_settings.Norms.Where(n => n.Kind == wanted));
        _normBox.ItemsSource = _normChoices.Select(HardwareCalcSettings.TitleOf).ToList();
        var guess = _settings.FindNorm(_product.Title, _product.Category);
        var index = guess is not null ? _normChoices.IndexOf(guess) : -1;
        _normBox.SelectedIndex = index >= 0 ? index : (_normChoices.Count > 0 ? 0 : -1);
        ApplyNorm();
    }

    private void OnNormChosen()
    {
        if (_suspend)
            return;
        ApplyNorm();
        Recalc();
    }

    private void ApplyNorm()
    {
        _norm = _normBox.SelectedIndex >= 0 && _normBox.SelectedIndex < _normChoices.Count ? _normChoices[_normBox.SelectedIndex] : null;
        var was = _suspend;
        _suspend = true;
        _consumption.Text = _norm is null ? "" : Fmt(_norm.PerM2);
        _layers.Text = _norm is null ? "1" : Fmt(_norm.Layers);
        _suspend = was;
    }

    private void SyncReserveChips()
    {
        _suspend = true;
        var current = ConsumptionCalculator.TryParseNumber(_reserve.Text, out var v) ? v : -1m;
        foreach (var (value, button) in _reserveChips)
            button.IsChecked = value == current;
        _suspend = false;
    }

    private void OnInput()
    {
        if (_suspend)
            return;
        SyncReserveChips();
        Recalc();
    }

    // ───────────────────────── расчёт и показ ─────────────────────────

    private static string Fmt(decimal v) => v.ToString("0.###", Ru);

    private static string Fmt2(decimal v) => v.ToString("0.00", Ru);

    /// <summary>Число из поля: null — поле пустое; <see cref="bad"/> — текст не число.</summary>
    private static decimal? Num(TextBox box, ref bool bad)
    {
        if (string.IsNullOrWhiteSpace(box.Text))
            return null;
        if (ConsumptionCalculator.TryParseNumber(box.Text, out var v))
            return v;
        bad = true;
        return null;
    }

    private void Recalc()
    {
        if (_suspend)
            return;
        var bad = false;
        CalcResult result;
        var reserve = Num(_reserve, ref bad) ?? 0m;
        bool empty;
        if (_kind == CalcKind.Wallpaper)
        {
            var perimeter = Num(_perimeter, ref bad);
            var height = Num(_height, ref bad) ?? 0m;
            var rollLength = Num(_rollLength, ref bad) ?? 0m;
            var rollWidth = Num(_rollWidth, ref bad) ?? 0m;
            var rapportCm = Num(_rapport, ref bad) ?? 0m;
            empty = perimeter is null;
            result = ConsumptionCalculator.Wallpaper(perimeter ?? 0m, height, rollLength, rollWidth, rapportCm / 100m, reserve);
        }
        else
        {
            decimal? area;
            if (_byDims)
            {
                var length = Num(_length, ref bad);
                var width = Num(_width, ref bad);
                area = length is not null && width is not null ? ConsumptionCalculator.Area(length.Value, width.Value) : null;
            }
            else
                area = Num(_area, ref bad);
            empty = area is null;
            var openings = Num(_openings, ref bad) ?? 0m;
            var pack = Num(_pack, ref bad) ?? 0m;
            var layers = Num(_layers, ref bad) ?? 0m;
            var rate = Num(_consumption, ref bad) ?? 0m;
            result = _kind switch
            {
                CalcKind.Paint => ConsumptionCalculator.Paint(area ?? 0m, openings, layers, rate, reserve, pack),
                CalcKind.Mix => ConsumptionCalculator.Mix(area ?? 0m, openings, layers, rate, reserve, pack),
                _ => ConsumptionCalculator.Tiles(_kind, area ?? 0m, openings, reserve, pack),
            };
        }

        _plan = result.IsValid
            ? CalcCartPlanner.Plan(result, _product.Unit, _price)
            : new CartPlan { Problem = CartPlanProblem.NoResult };
        Show(result, empty, bad);
    }

    private string Unit(CalcUnit unit) => unit switch
    {
        CalcUnit.SquareMeter => T("м²", "м²", "m²", "m²", "m²"),
        CalcUnit.Liter => T("л", "л", "l", "l", "l"),
        CalcUnit.Kilogram => T("кг", "кг", "kg", "kg", "kg"),
        _ => "",
    };

    private string PackWord(CalcKind kind) => kind switch
    {
        CalcKind.Paint => T("Банок", "Банка", "Cans", "Kutu", "Banka"),
        CalcKind.Mix => T("Мешков (упаковок)", "Кап (таңгак)", "Bags (packs)", "Torba (paket)", "Qop (qadoq)"),
        CalcKind.Wallpaper => T("Рулонов", "Түрмөк", "Rolls", "Rulo", "Rulon"),
        _ => T("Упаковок", "Таңгак", "Packs", "Paket", "Qadoq"),
    };

    private string PackShort(CalcKind kind) => kind switch
    {
        CalcKind.Paint => T("банк.", "банка", "cans", "kutu", "banka"),
        CalcKind.Mix => T("меш.", "кап", "bags", "torba", "qop"),
        CalcKind.Wallpaper => T("рул.", "түрмөк", "rolls", "rulo", "rulon"),
        _ => T("уп.", "таңгак", "packs", "paket", "qadoq"),
    };

    private void Show(CalcResult r, bool empty, bool bad)
    {
        string? problem = null;
        if (bad)
            problem = T("Введите число, например 12,5.", "Санды киргизиңиз, мисалы 12,5.", "Enter a number, for example 12.5.", "Bir sayı girin, örneğin 12,5.", "Son kiriting, masalan 12,5.");
        else if (!empty && !r.IsValid)
            problem = r.Error switch
            {
                CalcError.AreaMissing => T("Укажите размеры больше нуля.", "Нөлдөн чоң өлчөмдөрдү көрсөтүңүз.", "Enter sizes above zero.", "Sıfırdan büyük ölçüler girin.", "Noldan katta o'lchamlarni kiriting."),
                CalcError.OpeningsTooBig => T("Проёмы не могут быть больше площади.", "Тешиктер аянттан чоң болбошу керек.", "Openings cannot exceed the area.", "Boşluklar alandan büyük olamaz.", "Teshiklar maydondan katta bo'lishi mumkin emas."),
                CalcError.PackMissing => _kind == CalcKind.Paint
                    ? T("Укажите, сколько литров в банке.", "Банкада канча литр экенин көрсөтүңүз.", "Enter how many litres are in a can.", "Kutuda kaç litre olduğunu girin.", "Bankada necha litr borligini kiriting.")
                    : _kind == CalcKind.Mix
                        ? T("Укажите, сколько килограммов в упаковке.", "Таңгакта канча килограмм экенин көрсөтүңүз.", "Enter how many kilograms are in a pack.", "Pakette kaç kilogram olduğunu girin.", "Qadoqda necha kilogramm borligini kiriting.")
                        : T("Укажите, сколько м² в упаковке.", "Таңгакта канча м² экенин көрсөтүңүз.", "Enter how many m² are in a pack.", "Pakette kaç m² olduğunu girin.", "Qadoqda necha m² borligini kiriting."),
                CalcError.NormMissing => T("Укажите расход и число слоёв.", "Сарпталышты жана катмарлардын санын көрсөтүңүз.", "Enter the rate and number of coats.", "Tüketimi ve kat sayısını girin.", "Sarf va qatlamlar sonini kiriting."),
                CalcError.ReserveInvalid => T("Запас — от 0 до 100 %.", "Запас — 0дөн 100 % чейин.", "The allowance must be 0 to 100 %.", "Fire payı %0 ile %100 arasında olmalı.", "Zaxira 0 dan 100 % gacha bo'lishi kerak."),
                CalcError.RollInvalid => T("Проверьте высоту стен и размеры рулона: полоса не должна быть длиннее рулона.", "Дубалдын бийиктигин жана рулондун өлчөмүн текшериңиз: тилке рулондон узун болбошу керек.", "Check the wall height and roll size: a strip cannot be longer than the roll.", "Duvar yüksekliğini ve rulo ölçüsünü kontrol edin: şerit rulodan uzun olamaz.", "Devor balandligi va rulon o'lchamini tekshiring: tasma rulondan uzun bo'lmasligi kerak."),
                CalcError.TooBig => T("Слишком большое число — проверьте ввод.", "Өтө чоң сан — киргизүүнү текшериңиз.", "The number is too large — check the input.", "Sayı çok büyük — girişi kontrol edin.", "Son juda katta — kiritilganini tekshiring."),
                _ => null,
            };

        // Дробное количество в чек — только если корзина его не округлит (см. SupportsFractionalCart).
        string? cartProblem = null;
        if (_plan.CanAdd && _plan.IsFractional && _plan.Quantity != Math.Truncate(_plan.Quantity) && !SupportsFractionalCart(_product))
            cartProblem = T(
                $"Дробное количество ({Fmt2(_plan.Quantity)} {_product.Unit}) в этой кассе пока нельзя добавить в чек — впишите его в чеке вручную.",
                $"Бөлчөк сан ({Fmt2(_plan.Quantity)} {_product.Unit}) бул кассада чекке азырынча кошулбайт — чекте кол менен жазыңыз.",
                $"A fractional quantity ({Fmt2(_plan.Quantity)} {_product.Unit}) can't be added to the receipt yet — enter it in the receipt by hand.",
                $"Kesirli miktar ({Fmt2(_plan.Quantity)} {_product.Unit}) henüz fişe eklenemiyor — fişte elle girin.",
                $"Kasrli miqdor ({Fmt2(_plan.Quantity)} {_product.Unit}) hozircha chekka qo'shilmaydi — chekda qo'lda kiriting.");
        else if (r.IsValid && _plan.Problem == CartPlanProblem.UnitMismatch)
        {
            var expected = CalcCartPlanner.ExpectedBaseUnit(r.Kind);
            var need = expected is { } u ? Unit(u) : PackShort(r.Kind);
            cartProblem = T(
                $"Товар продаётся в «{_product.Unit}» — для этого расчёта нужна упаковка или {need}. Добавьте товар в чек вручную.",
                $"Товар «{_product.Unit}» менен сатылат — бул эсеп үчүн таңгак же {need} керек. Товарды чекке кол менен кошуңуз.",
                $"The product is sold in “{_product.Unit}” — this calculation needs packs or {need}. Add the product to the receipt by hand.",
                $"Ürün “{_product.Unit}” ile satılıyor — bu hesap için paket veya {need} gerekir. Ürünü fişe elle ekleyin.",
                $"Mahsulot «{_product.Unit}» bilan sotiladi — bu hisob uchun qadoq yoki {need} kerak. Mahsulotni chekka qo'lda qo'shing.");
        }

        var message = problem ?? cartProblem;
        _error.Text = message ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(message);

        var ok = r.IsValid && !empty && !bad;
        _hint.IsVisible = !ok;
        _hint.Text = T("Введите размеры — расчёт появится сразу.", "Өлчөмдөрдү киргизиңиз — эсеп дароо чыгат.", "Enter the sizes — the result appears at once.", "Ölçüleri girin — sonuç hemen görünür.", "O'lchamlarni kiriting — natija darhol chiqadi.");
        foreach (var c in new Control[] { _l1, _v1, _l2, _v2, _l3, _v3, _sub, _priceText, _note })
            c.IsVisible = ok;

        _add.IsEnabled = ok && _plan.CanAdd && cartProblem is null;
        if (!ok)
        {
            _add.Content = T("В чек", "Чекке", "To receipt", "Fişe", "Chekka");
            return;
        }

        if (r.Kind == CalcKind.Wallpaper)
        {
            _l1.Text = T("Полос по комнате", "Бөлмө боюнча тилкелер", "Strips for the room", "Oda için şerit", "Xona uchun tasmalar");
            _v1.Text = r.Strips.ToString(Ru);
            _l2.Text = T("Полос из одного рулона", "Бир рулондон тилкелер", "Strips per roll", "Rulo başına şerit", "Bir rulondan tasmalar");
            _v2.Text = r.StripsPerRoll.ToString(Ru);
            _sub.Text = T($"Стены: {Fmt2(r.Need)} м²", $"Дубалдар: {Fmt2(r.Need)} м²", $"Walls: {Fmt2(r.Need)} m²", $"Duvarlar: {Fmt2(r.Need)} m²", $"Devorlar: {Fmt2(r.Need)} m²");
        }
        else
        {
            _l1.Text = T("Нужно", "Керек", "Needed", "Gerekli", "Kerak");
            _v1.Text = Fmt2(r.Need) + " " + Unit(r.Unit);
            _l2.Text = T($"С запасом {Fmt(r.ReservePercent)} %", $"{Fmt(r.ReservePercent)} % запас менен", $"With {Fmt(r.ReservePercent)}% allowance", $"%{Fmt(r.ReservePercent)} fire payı ile", $"{Fmt(r.ReservePercent)} % zaxira bilan");
            _v2.Text = Fmt2(r.WithReserve) + " " + Unit(r.Unit);
            _sub.Text = T($"В купленных упаковках: {Fmt2(r.PackedTotal)} {Unit(r.Unit)}", $"Сатылып алынган таңгактарда: {Fmt2(r.PackedTotal)} {Unit(r.Unit)}",
                $"In the packs bought: {Fmt2(r.PackedTotal)} {Unit(r.Unit)}", $"Alınan paketlerde: {Fmt2(r.PackedTotal)} {Unit(r.Unit)}", $"Sotib olingan qadoqlarda: {Fmt2(r.PackedTotal)} {Unit(r.Unit)}");
        }
        _l3.Text = PackWord(r.Kind);
        _v3.Text = r.Packs.ToString(Ru);

        if (_plan.CanAdd && _price > 0m)
        {
            _priceText.Text = T($"≈ {_plan.DisplayTotal.ToString("N2", Ru)} сом", $"≈ {_plan.DisplayTotal.ToString("N2", Ru)} сом", $"≈ {_plan.DisplayTotal.ToString("N2", Ru)} som",
                $"≈ {_plan.DisplayTotal.ToString("N2", Ru)} som", $"≈ {_plan.DisplayTotal.ToString("N2", Ru)} so'm");
            _note.Text = T("Сумма ориентировочная — точную считает чек.", "Сумма болжолдуу — так суммасын чек эсептейт.", "The sum is approximate — the receipt calculates the exact amount.",
                "Tutar yaklaşıktır — kesin tutarı fiş hesaplar.", "Summa taxminiy — aniq summani chek hisoblaydi.");
            _priceText.IsVisible = true;
            _note.IsVisible = true;
        }
        else
        {
            _priceText.IsVisible = false;
            _note.IsVisible = false;
        }

        if (_plan.CanAdd)
        {
            var what = _plan.IsFractional
                ? $"{Fmt2(_plan.Quantity)} {Unit(r.Unit)}"
                : $"{_plan.Quantity.ToString("0", Ru)} {PackShort(r.Kind)}";
            _add.Content = T("В чек", "Чекке", "To receipt", "Fişe", "Chekka") + ": " + what;
        }
        else
            _add.Content = T("В чек", "Чекке", "To receipt", "Fişe", "Chekka");
    }

    // ───────────────────────── «В чек» ─────────────────────────

    private async Task AddAsync()
    {
        if (_adding || !_plan.CanAdd)
            return;
        _adding = true;
        try
        {
            // Количество уходит существующим способом добавления в корзину (MainWindow.AddWeighedProductWithKnownWeightAsync:
            // проверка смены и остатка, затем BasketPanelViewModel.AddProductFromCatalog). Округление и деньги там не менялись.
            await _addToCart((double)Math.Round(_plan.Quantity, 3)).ConfigureAwait(true);
            Close(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Калькулятор расхода: «{_product.Title}» не добавлен в чек: {ex.Message}", "ERROR");
            _error.Text = T("Не удалось добавить товар в чек.", "Товарды чекке кошуу мүмкүн болгон жок.", "Could not add the product to the receipt.", "Ürün fişe eklenemedi.", "Mahsulotni chekka qo'shib bo'lmadi.");
            _error.IsVisible = true;
        }
        finally
        {
            _adding = false;
        }
    }
}
