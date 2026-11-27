using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-08 (ТЗ п. 32, Б-39): тариф «Своё» в программе владельца — пакеты функций поверх «Старта». Список пакетов с галочками и ценами С СЕРВЕРА
/// (GET public/tariffs/, цены временные — владелец меняет их в админке), живой пересчёт через users/company/tariff/quote/ (с задержкой 350 мс после последней галочки),
/// подсказка «Стандарт дешевле», пометка «уже подключено», кнопка «Отправить заявку» с подтверждением и Idempotency-Key (GUID на попытку; повтор с тем же ключом безопасен).
/// Сервер только записывает заявку, её обрабатывает менеджер: платёжной ссылки нет.</summary>
public sealed class TariffCustomWindow : Window
{
    private sealed class PackRow
    {
        public required TariffPack Pack { get; init; }
        public required CheckBox Box { get; init; }
        public required TextBlock Monthly { get; init; }
        public required TextBlock Once { get; init; }
        public required Border Active { get; init; }
        public required TextBlock ActiveText { get; init; } // 2026-11-20: «уже подключено» или «Входит в ваш тариф»
    }

    /// <summary>2026-11-26: строка функции-модуля (та же раскладка, что у пакета: значок, название, «что делает», цена, переключатель). Baseline — включён ли модуль в компании сейчас.</summary>
    private sealed class ModuleRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required decimal? Price { get; init; }
        public required bool Baseline { get; init; }
        public required bool InSphere { get; init; }
        public required CheckBox Box { get; init; }
        public required TextBlock Monthly { get; init; }
        public required Border Active { get; init; }
        public required TextBlock ActiveText { get; init; }
        public string? Line { get; init; }
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly ITariffApi _api;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _intro = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _info = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _standardHint = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _done = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _submit;
    private readonly Button _close;
    private readonly Button _manager; // 2026-11-20: «Написать менеджеру» — только когда сервер отказал по тарифу (409)
    private readonly string? _currentPlan; // название/код текущего тарифа компании (TariffGate.CurrentPlanName)
    private string? _planCode;
    private readonly HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase); // пакеты, уже входящие в текущий тариф
    private readonly Dictionary<string, PackRow> _packRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private TariffCatalog? _catalog;
    private TariffQuote? _quote;
    private int _version;
    private int _loadStarted;
    private bool _locked;
    private bool _submitting;
    private TariffQuote? _baseQuote; // 2026-11-21: расчёт «ничего не меняли» (Стандарт как есть) — к нему возвращаемся, когда все галочки снова стоят
    private string _period = "month"; // 2026-11-21 (ответ Умара 21.11): "month" | "year" (год = 10 месяцев)
    private readonly WrapPanel _periodRow = new() { Orientation = Orientation.Horizontal, IsVisible = false };
    private readonly Dictionary<string, Button> _periodButtons = new();
    private readonly TextBlock _removeHint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // 2026-11-26 («Своё» собирается из Маркетплейса): набор выбора — общий (TariffDraft); здесь галочки только отражают его. Модули — вторая часть списка, с теми же строками.
    private readonly StackPanel _moduleRowsHost = new() { Spacing = 2 };
    private readonly Dictionary<string, ModuleRow> _moduleRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _packsHeader = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _modulesHeader = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _modulesCaption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _changes = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool _applyingDraft;
    private bool _suppress; // MarkActive ставит галочку из кода — это не выбор владельца
    private CancellationTokenSource? _quoteCts;
    private string? _attemptKey;

    /// <summary>Свой вопрос подтверждения (для проверок без окна-вопроса); по умолчанию — обычное окно «Отправить / Отмена».</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>Что видно в итоге и на кнопках — для проверок.</summary>
    public string SummaryText => _summary.Text ?? "";
    public bool CanSubmit => _submit.IsEnabled;
    public string? DoneText => _done.IsVisible ? _done.Text : null;
    public string? ErrorText => _error.IsVisible ? _error.Text : null;
    public bool ManagerButtonVisible => _manager.IsVisible;
    public bool IsIncluded(string code) => _included.Contains(code);
    public bool IsPackChecked(string code) => _packRows.TryGetValue(code, out var r) && r.Box.IsChecked == true;
    public bool IsPackEnabled(string code) => _packRows.TryGetValue(code, out var r) && r.Box.IsEnabled;
    public string RemoveHintText => _removeHint.IsVisible ? _removeHint.Text ?? "" : "";
    public bool PeriodRowVisible => _periodRow.IsVisible;
    public string Period => _period;
    public void SetPeriodForTest(string period) => SetPeriod(period);
    public string PackStatusText(string code) => _packRows.TryGetValue(code, out var r) && r.Active.IsVisible ? r.ActiveText.Text ?? "" : "";

    // 2026-11-26: функции-модули в списке «Своё» — для проверок (то же, что для пакетов выше).
    public IReadOnlyList<string> ModuleIdsShown => _moduleRows.Keys.ToList();
    public bool IsModuleChecked(string id) => _moduleRows.TryGetValue(id, out var r) && r.Box.IsChecked == true;
    public void SetModuleChecked(string id, bool value) => _moduleRows[id].Box.IsChecked = value;
    public string? ModuleLineText(string id) => _moduleRows.TryGetValue(id, out var r) ? r.Line : null;
    public string ModuleStatusText(string id) => _moduleRows.TryGetValue(id, out var r) && r.Active.IsVisible ? r.ActiveText.Text ?? "" : "";
    public string ChangesLineText => _changes.IsVisible ? _changes.Text ?? "" : "";
    public void RebuildModulesForTest() => BuildModuleRows();

    /// <summary>Открыть чат менеджера (общий путь поддержки — номер из RemoteSupportHost.SupportPhone); для проверок подменяется.</summary>
    public Func<string, bool> OpenChat { get; set; } = url => PlatformLauncher.OpenUrl(url);
    public void ManagerClickForTest() => _manager.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    public Task SubmitForTestAsync() => SubmitAsync();
    public Task QuoteForTestAsync() { _debounce.Stop(); return RequestQuoteAsync(); }
    public void SetChecked(string code, bool value) => _packRows[code].Box.IsChecked = value;

    public TariffCustomWindow(ITariffApi api, string? currentPlan = null)
    {
        _api = api;
        _currentPlan = currentPlan;
        Title = T("Тариф «Своё»", "«Өзүмдүкү» тарифи", "“Custom” plan", "«Özel» tarife", "«O'zimniki» tarifi");
        SectionCards.AsModal(this, 672);

        // 2026-11-20: «поверх вашего тарифа» (было «поверх Старта») — конструктор нужен и на «Стандарте»; доступность решает сервер.
        // 2026-11-26: «пакеты и функции» — список теперь собран из Маркетплейса (выбор общий с кнопкой «+» на его карточках).
        _intro.Text = T("Соберите свой набор функций поверх вашего тарифа. Отметьте нужные пакеты и функции — те же, что в Маркетплейсе, выбор общий; цены считает сервер. Заявку обработает менеджер и свяжется с вами; платить сейчас ничего не нужно.",
            "Тарифиңиздин үстүнө өз функциялар топтомун чогултуңуз. Керектүү пакеттерди жана функцияларды белгилеңиз — Маркетплейстегидей эле, тандоо жалпы; баасын сервер эсептейт. Өтүнүчтү менеджер иштетип, сиз менен байланышат; азыр эч нерсе төлөбөйсүз.",
            "Build your own set of features on top of your plan. Tick the packs and features you need — the same as in the Marketplace, the selection is shared; the server calculates the prices. A manager will process the request and contact you; nothing to pay now.",
            "Tarifenizin üzerine kendi özellik setinizi oluşturun. İhtiyacınız olan paketleri ve özellikleri işaretleyin — Marketplace'tekilerle aynı, seçim ortaktır; fiyatları sunucu hesaplar. Talebi bir yönetici işleyip sizinle iletişime geçer; şimdi ödeme yapmanız gerekmez.",
            "Tarifingiz ustiga o'z funksiyalar to'plamingizni yig'ing. Kerakli paketlar va funksiyalarni belgilang — Marketpleysdagi bilan bir xil, tanlov umumiy; narxni server hisoblaydi. Arizani menejer ko'rib chiqib, siz bilan bog'lanadi; hozir hech narsa to'lash shart emas.");
        Soft(_intro);
        Soft(_info);
        Soft(_removeHint);
        Soft(_modulesCaption);
        _packsHeader.Bind(TextBlock.ForegroundProperty, _packsHeader.GetResourceObservable("BrushText"));
        _modulesHeader.Bind(TextBlock.ForegroundProperty, _modulesHeader.GetResourceObservable("BrushText"));
        _changes.Bind(TextBlock.ForegroundProperty, _changes.GetResourceObservable("BrushText"));
        _packsHeader.Text = T("Пакеты", "Пакеттер", "Packs", "Paketler", "Paketlar");
        _modulesHeader.Text = T("Функции магазина", "Дүкөндүн функциялары", "Shop features", "Mağaza özellikleri", "Do'kon funksiyalari");
        _modulesCaption.Text = T("Те же функции, что в Маркетплейсе. Отметка — функция в тарифе; цена в месяц с сервера, итог считает сервер. Функции вашей сферы входят в тариф.",
            "Маркетплейстегидей эле функциялар. Белги — функция тарифте; айлык баасы серверден, жыйынтыкты сервер эсептейт. Чөйрөңүздүн функциялары тарифке кирет.",
            "The same features as in the Marketplace. A tick means the feature is in the plan; the monthly price comes from the server, which also calculates the total. Features of your sphere are included.",
            "Marketplace'tekiyle aynı özellikler. İşaret, özelliğin tarifede olduğu anlamına gelir; aylık fiyat sunucudan gelir, toplamı da sunucu hesaplar. Alanınızın özellikleri tarife dahildir.",
            "Marketpleysdagi bilan bir xil funksiyalar. Belgi — funksiya tarifda; oylik narx serverdan, jami summani server hisoblaydi. Sohangiz funksiyalari tarifga kiradi.");
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        _standardHint.Bind(TextBlock.ForegroundProperty, _standardHint.GetResourceObservable("BrushSuccess"));
        _done.Bind(TextBlock.ForegroundProperty, _done.GetResourceObservable("BrushSuccess"));
        _info.Text = T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");

        var summaryBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = new StackPanel { Spacing = 4, Children = { _changes, _summary, _standardHint } } };
        summaryBox.Bind(Border.BackgroundProperty, summaryBox.GetResourceObservable("BrushPanelSoft"));

        _close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.Click += (_, _) => Close();
        _submit = SectionCards.Primary(T("Отправить заявку", "Өтүнүч жөнөтүү", "Send request", "Talep gönder", "Ariza yuborish"));
        _submit.IsEnabled = false;
        _submit.Click += async (_, _) => await SubmitAsync().ConfigureAwait(true);
        _manager = SectionCards.Cancel(T("Написать менеджеру", "Менеджерге жазуу", "Message the manager", "Yöneticiye yaz", "Menejerga yozish"));
        _manager.IsCancel = false; // Esc закрывает окно кнопкой «Закрыть», не эту
        _manager.IsVisible = false;
        _manager.HorizontalAlignment = HorizontalAlignment.Left;
        _manager.Click += (_, _) => OpenManagerChat();

        Content = SectionCards.Shell(Title, null,
            // 2026-11-08: итог и результат — над списком: на 1024×600 список прокручивается, итог должен быть виден без прокрутки.
            new StackPanel { Spacing = 10, Children = { _intro, _info, SectionCards.ErrorBox(this, _error), _manager, _done, _periodRow, summaryBox, _removeHint, _packsHeader, _rows, _modulesHeader, _modulesCaption, _moduleRowsHost } },
            SectionCards.Buttons(_close, _submit));

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RequestQuoteAsync();
        };
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        // 2026-11-26: общий набор (Маркетплейс ↔ диалог) и обновление списка модулей (цены и реестр пришли позже окна).
        TariffDraft.Changed += OnDraftChanged;
        PublicModules.Changed += OnModulesChanged;
        CompanyModules.Changed += OnModulesChanged;
        PublicModules.RefreshInBackground();
        Closed += (_, _) =>
        {
            _debounce.Stop();
            _quoteCts?.Cancel();
            TariffDraft.Changed -= OnDraftChanged;
            PublicModules.Changed -= OnModulesChanged;
            CompanyModules.Changed -= OnModulesChanged;
        };
        RenderSummary();
    }

    private void Soft(TextBlock t) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));

    private static string M(decimal value)
    {
        var a = MoneyText.Amount(value);
        if (a.EndsWith(",00", StringComparison.Ordinal))
            a = a[..^3];
        return a + " " + MoneyText.Currency;
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Каталог пакетов и первый расчёт по всем пакетам: из него видно, какие уже подключены. Для тестов — вызывается и вручную; повторный вызов ничего не делает.</summary>
    public async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) != 0)
            return;
        try
        {
            _catalog = await _api.GetCatalogAsync().ConfigureAwait(true);
            if (_catalog.Packs.Count == 0)
            {
                _info.Text = T("Сервер пока не отдал список пакетов. Попробуйте позже.", "Сервер пакеттердин тизмесин азырынча берген жок. Кийинчерээк аракет кылыңыз.",
                    "The server has not returned the pack list yet. Try again later.", "Sunucu paket listesini henüz vermedi. Daha sonra deneyin.", "Server paketlar ro'yxatini hali bermadi. Keyinroq urinib ko'ring.");
                return;
            }

            BuildRows(_catalog);
            BuildPeriodRow();
            BuildModuleRows(); // 2026-11-26: функции-модули — вторая часть списка (из Маркетплейса: public/modules + реестр компании)
            // 2026-11-20: пакеты, что уже входят в текущий тариф (например, в «Стандарт»), — «Входит в ваш тариф».
            // 2026-11-21 (ответ Умара 21.11): на Стандарте их снова можно СНИМАТЬ — отметка = пакет остаётся; снятые уходят в remove[], сервер считает «Своё» (Старт + оставшиеся пакеты).
            _planCode = TariffCustomRules.ResolvePlanCode(_catalog, _currentPlan);
            foreach (var pack in _catalog.Packs.Where(p => TariffCustomRules.IsIncludedInPlan(p, _planCode)))
                MarkIncluded(pack.Code);
            _info.Text = "";
            _info.IsVisible = false;
            try
            {
                // Один расчёт по всем пакетам — узнаём, какие уже подключены (already_active), и сразу проверяем, что тариф «Своё» этой компании доступен.
                var rest = _catalog.Packs.Select(p => p.Code).Where(c => !_included.Contains(c)).ToList();
                if (rest.Count > 0)
                {
                    var all = await _api.QuoteAsync(rest, QuoteModules(), QuoteSphere(), null, _period).ConfigureAwait(true);
                    foreach (var qp in all.Packs.Where(p => p.AlreadyActive))
                        MarkActive(qp.Code);
                }

                if (_included.Count > 0)
                {
                    // 2026-11-21: часть пакетов входит в тариф (Стандарт) — показываем текущую стоимость и модули (расчёт без изменений: цену и «Входит в тариф» даёт сервер) и подсказку про снятие.
                    _baseQuote = _quote = await _api.QuoteAsync(Array.Empty<string>(), QuoteModules(), QuoteSphere(), null, _period).ConfigureAwait(true);
                    var custom = PlanName("custom", T("Своё", "Өзүмдүкү", "Custom", "Özel", "O'zimniki"));
                    var start = PlanName("start", T("Старт", "Старт", "Start", "Start", "Start"));
                    _removeHint.Text = T($"Снимете галочку с пакета — тариф станет «{custom}»: {start} + оставшиеся пакеты. Цену считает сервер.",
                        $"Пакеттен белгини алсаңыз — тариф «{custom}» болот: {start} + калган пакеттер. Баасын сервер эсептейт.",
                        $"Untick a pack and the plan becomes “{custom}”: {start} + the remaining packs. The server calculates the price.",
                        $"Bir paketin işaretini kaldırırsanız tarife «{custom}» olur: {start} + kalan paketler. Fiyatı sunucu hesaplar.",
                        $"Paketdan belgini olib tashlasangiz, tarif «{custom}» bo'ladi: {start} + qolgan paketlar. Narxni server hisoblaydi.");
                    _removeHint.IsVisible = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowError(TariffApi.Describe(ex));
                if (ex is ApiException { StatusCode: 403 or 409 })
                    Lock(ex is ApiException { StatusCode: 409 });
            }

            // 2026-11-26: что входит в тариф — Маркетплейсу (его плитки показывают то же), а выбор, сделанный там раньше, — в галочки этого окна.
            PublishBaselines();
            ApplyDraft();
            RenderSummary();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Тариф «Своё»: каталог не загружен: {ex.Message}", "WARNING");
            _info.IsVisible = true;
            _info.Text = "";
            ShowError(TariffApi.Describe(ex));
            Interlocked.Exchange(ref _loadStarted, 0); // «Закрыть и открыть заново» — повтор загрузки
        }
    }

    /// <summary>2026-11-20: ссылка на чат WhatsApp менеджера с готовым текстом — тот же номер поддержки, что в окне удалённой помощи.</summary>
    private void OpenManagerChat()
    {
        var digits = new string(MarketSphereLock.SupportPhone.Where(char.IsDigit).ToArray()); // 2026-11-21: RemoteSupportHost не собирается на Android — тот же номер из MarketSphereLock
        var text = T($"Здравствуйте! Хочу собрать тариф «Своё» на тарифе «{_currentPlan ?? "—"}», в программе пишет, что недоступно.",
            $"Саламатсызбы! «Өзүмдүкү» тарифин «{_currentPlan ?? "—"}» тарифинде түзгүм келет, программа жеткиликсиз деп жазат.",
            $"Hello! I want to build the “Custom” plan on the “{_currentPlan ?? "—"}” plan, the app says it is unavailable.",
            $"Merhaba! «{_currentPlan ?? "—"}» tarifesinde «Özel» tarife oluşturmak istiyorum, uygulama kullanılamaz diyor.",
            $"Assalomu alaykum! «{_currentPlan ?? "—"}» tarifida «O'zimniki» tarifini yig'moqchiman, dastur mavjud emas deydi.");
        if (!OpenChat("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(text)))
            ShowError(T("Не удалось открыть WhatsApp.", "WhatsApp'ты ачуу мүмкүн болгон жок.", "Could not open WhatsApp.", "WhatsApp açılamadı.", "WhatsApp'ni ochib bo'lmadi."));
    }

    private void Lock(bool offerManager = false)
    {
        _manager.IsVisible = offerManager;
        _locked = true;
        foreach (var row in _packRows.Values)
            row.Box.IsEnabled = false;
        foreach (var row in _moduleRows.Values)
            row.Box.IsEnabled = false;
        _submit.IsEnabled = false;
    }

    private void BuildRows(TariffCatalog catalog)
    {
        _rows.Children.Clear();
        _packRows.Clear();
        _packsHeader.IsVisible = catalog.Packs.Count > 0;
        foreach (var pack in catalog.Packs)
        {
            // 2026-11-26: значок и «что делает» — как у карточки пакета в Маркетплейсе (описание пакета — с сервера, значок — TariffRowVisuals).
            var (packIcon, packColor) = TariffRowVisuals.Pack(pack.Code);
            var content = RowContent(packIcon, packColor, pack.Name, pack.Description);

            var box = new CheckBox { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, MinHeight = 44 };
            Avalonia.Automation.AutomationProperties.SetName(box, pack.Name);
            var monthly = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            monthly.Bind(TextBlock.ForegroundProperty, monthly.GetResourceObservable("BrushText"));
            var once = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            Soft(once);
            var activeText = new TextBlock { Text = T("уже подключено", "мурунтан туташкан", "already connected", "zaten bağlı", "allaqachon ulangan"), FontSize = 12, FontWeight = FontWeight.SemiBold };
            activeText.Bind(TextBlock.ForegroundProperty, activeText.GetResourceObservable("BrushSuccess"));
            var active = new Border { Child = activeText, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            var price = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { monthly, once, active } };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(box, 0);
            Grid.SetColumn(price, 1);
            grid.Children.Add(box);
            grid.Children.Add(price);
            _rows.Children.Add(grid);

            var row = new PackRow { Pack = pack, Box = box, Monthly = monthly, Once = once, Active = active, ActiveText = activeText };
            _packRows[pack.Code] = row;
            ShowListPrice(row);
            box.IsCheckedChanged += (_, _) =>
            {
                if (_suppress)
                    return;
                // 2026-11-26: выбор владельца уходит в общий набор (его видят плитки Маркетплейса); база — уже входит в тариф или подключён.
                TariffDraft.SetPack(pack.Code, box.IsChecked == true, PackBaseline(pack.Code));
                OnSelectionChanged();
            };
        }
    }

    private bool PackBaseline(string code) => _included.Contains(code) || _active.Contains(code);

    /// <summary>2026-11-26: общая раскладка строки (пакет и функция): значок 40 px слева, справа название и ОДНА строка «что делает». Пустое описание строку не создаёт.</summary>
    private static Control RowContent(string iconKey, string iconColor, string title, string? description)
    {
        var name = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (!string.IsNullOrWhiteSpace(description))
        {
            var desc = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            desc.Bind(TextBlock.ForegroundProperty, desc.GetResourceObservable("BrushTextSoft"));
            texts.Children.Add(desc);
        }

        var icon = MarketplaceStoreView.BuildIcon(new MarketplaceItem { Id = "tariff-row", Title = title, IconKey = iconKey, IconColor = iconColor }, 40);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>Цена из каталога: «400 сом / мес» и «+ 1 500 сом разово».</summary>
    private void ShowListPrice(PackRow row)
    {
        row.Monthly.Text = M(row.Pack.MonthlyFee) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy");
        row.Once.Text = T("+ ", "+ ", "+ ", "+ ", "+ ") + M(row.Pack.ActivationFee) + " " + T("разово", "бир жолу", "one-off", "tek sefer", "bir marta");
    }

    /// <summary>Название тарифа с сервера (каталог, на языке интерфейса); нет — запасное.</summary>
    private string PlanName(string code, string fallback) => _catalog?.Plan(code)?.Name is { Length: > 0 } n ? n : fallback;

    /// <summary>2026-11-21: пакет входит в текущий тариф (Стандарт): отмечен, галочку можно СНЯТЬ (пакет уйдёт в remove[]), «Входит в ваш тариф» — пока не снят.</summary>
    private void MarkIncluded(string code)
    {
        if (!_packRows.TryGetValue(code, out var row))
            return;
        _included.Add(code);
        _suppress = true;
        row.Box.IsChecked = true;
        _suppress = false;
        row.Box.IsEnabled = true;
        ShowIncludedState(row, null);
    }

    private void ShowIncludedState(PackRow row, TariffQuotePack? qp)
    {
        var kept = row.Box.IsChecked == true;
        if (kept && qp is { AlreadyActive: false } && _quote is { BecomesCustom: true })
        {
            // тариф станет «Своё»: оставшийся пакет платный — цена с сервера (monthly из расчёта)
            row.Monthly.Text = M(qp.Monthly) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy");
            row.Monthly.IsVisible = true;
            row.Once.IsVisible = false;
            row.Active.IsVisible = false;
            return;
        }

        row.ActiveText.Text = kept
            ? T("Входит в ваш тариф", "Тарифиңизге кирет", "Included in your plan", "Tarifinize dahil", "Tarifingizga kiradi")
            : T("Будет снят", "Алынып салынат", "Will be removed", "Kaldırılacak", "Olib tashlanadi");
        row.ActiveText.Bind(TextBlock.ForegroundProperty, row.ActiveText.GetResourceObservable(kept ? "BrushSuccess" : "BrushWarning"));
        row.Monthly.IsVisible = false;
        row.Once.IsVisible = false;
        row.Active.IsVisible = true;
    }

    private void RefreshIncludedRows()
    {
        foreach (var code in _included)
            if (_packRows.TryGetValue(code, out var row))
                ShowIncludedState(row, _quote?.Packs.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)));
    }

    private void MarkActive(string code)
    {
        if (!_packRows.TryGetValue(code, out var row) || _included.Contains(code))
            return;
        _active.Add(row.Pack.Code);
        _suppress = true;
        row.Box.IsChecked = true;
        _suppress = false;
        row.Box.IsEnabled = false;
        row.Monthly.IsVisible = false;
        row.Once.IsVisible = false;
        row.Active.IsVisible = true;
        TariffDraft.SetPack(code, true, true); // 2026-11-26: пакет уже подключён — «добавить» в общем наборе больше не нужно
    }

    /// <summary>Пакеты, которые владелец добавляет (отмечены, ещё не подключены и не входят в тариф).</summary>
    private List<string> Selected() =>
        _packRows.Values.Where(r => r.Box.IsChecked == true && !_active.Contains(r.Pack.Code) && !_included.Contains(r.Pack.Code)).Select(r => r.Pack.Code).ToList();

    /// <summary>2026-11-26: функции, которые владелец добавляет (отмечены, а в компании не включены) и снимает (включены, а отметка снята).</summary>
    private List<string> SelectedModules() => _moduleRows.Values.Where(r => r.Box.IsChecked == true && !r.Baseline).Select(r => r.Id).ToList();

    private List<string> RemovedModules() => _moduleRows.Values.Where(r => r.Box.IsChecked != true && r.Baseline).Select(r => r.Id).ToList();

    /// <summary>Набор модулей для расчёта выбора и заявки: включённые в компании + добавленные − снятые (единое правило — TariffDraft.EffectiveModules).</summary>
    private static List<string>? SelectionModules() => TariffDraft.EffectiveModules();

    /// <summary>2026-11-21: пакеты тарифа «Стандарт», с которых владелец снял галочку — уходят в remove[].</summary>
    private List<string> Removed() =>
        _included.Where(c => _packRows.TryGetValue(c, out var r) && r.Box.IsChecked != true).ToList();

    private bool HasChanges() => Selected().Count > 0 || Removed().Count > 0 || SelectedModules().Count > 0 || RemovedModules().Count > 0;

    private string PackName(string code) => _packRows.TryGetValue(code, out var r) ? r.Pack.Name : code;

    private string ModuleName(string id) => _moduleRows.TryGetValue(id, out var r) ? r.Name : id;

    /// <summary>2026-11-26: «Что изменится: +Ремонт, −ИИ-помощник» над итогом — из общего набора.</summary>
    private void RenderChanges()
    {
        var any = TariffDraft.HasChanges;
        _changes.IsVisible = any;
        _changes.Text = any ? T("Что изменится: ", "Эмне өзгөрөт: ", "What changes: ", "Neler değişecek: ", "Nima o'zgaradi: ") + TariffDraft.ChangesText(6) : "";
    }

    /// <summary>2026-11-26: что входит в тариф — в общий набор (для плиток Маркетплейса).</summary>
    private void PublishBaselines() => TariffDraft.PublishPackBaselines(_packRows.Keys.Select(c => (c, PackBaseline(c))));

    private void OnDraftChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyDraft();
        else
            Dispatcher.UIThread.Post(ApplyDraft);
    }

    private void OnModulesChanged()
    {
        if (!CatalogLoaded)
            return;
        void Rebuild()
        {
            if (!_locked)
                BuildModuleRows();
        }
        if (Dispatcher.UIThread.CheckAccess())
            Rebuild();
        else
            Dispatcher.UIThread.Post(Rebuild);
    }

    private bool CatalogLoaded => _catalog is { Packs.Count: > 0 };

    /// <summary>2026-11-26: общий набор (его правят плитки Маркетплейса и сам диалог) → галочки окна. Выбор в самом окне уже в наборе, поэтому здесь ничего не меняется и расчёт не дёргается.</summary>
    private void ApplyDraft()
    {
        if (_applyingDraft || _locked || _catalog is null)
            return;
        _applyingDraft = true;
        try
        {
            var any = false;
            foreach (var row in _packRows.Values)
            {
                if (!row.Box.IsEnabled)
                    continue; // уже подключён отдельно — снять нельзя
                var want = TariffDraft.IsPackSelected(row.Pack.Code, PackBaseline(row.Pack.Code));
                if (row.Box.IsChecked == want)
                    continue;
                _suppress = true;
                row.Box.IsChecked = want;
                _suppress = false;
                any = true;
            }

            foreach (var row in _moduleRows.Values)
            {
                var want = TariffDraft.IsModuleSelected(row.Id, row.Baseline);
                if (row.Box.IsChecked == want)
                    continue;
                _suppress = true;
                row.Box.IsChecked = want;
                _suppress = false;
                RefreshModuleRow(row);
                any = true;
            }

            if (any)
                OnSelectionChanged();
            else
                RenderChanges();
        }
        finally
        {
            _applyingDraft = false;
        }
    }

    /// <summary>2026-11-26: функции магазина — те же, что в Маркетплейсе (GET public/modules/ с ценами; нет цен — реестр компании). Сначала те, что уже в тарифе; у каждой — значок и ОДНА строка «что делает»
    /// (MarketplaceModuleContent.LineOf; нет записи — строка не показывается, а тест красный). Цена в месяц — с сервера, как на плитке; итог считает сервер.</summary>
    private void BuildModuleRows()
    {
        _moduleRowsHost.Children.Clear();
        _moduleRows.Clear();
        var snap = CompanyModules.Snapshot;
        var list = new List<(string Id, string Name, string Group, decimal? Price)>();
        if (PublicModules.Cached is { Count: > 0 } pub)
            list.AddRange(pub.Select(m => (m.Id, m.Name, m.Group, (decimal?)m.Monthly)));
        else if (snap is not null)
            list.AddRange(snap.Catalog.Select(i => (i.Id, i.Name, i.Group, (decimal?)null))); // цен пока нет — «ничего не придумываем»: строка без суммы
        _modulesHeader.IsVisible = list.Count > 0;
        _modulesCaption.IsVisible = list.Count > 0;
        if (list.Count == 0)
            return;

        // В тарифе (включены в компании) — выше, дальше по группам (сферам) и названию.
        var sorted = list.Select(m => (m, on: TariffDraft.ModuleBaseline(m.Id), sphere: TariffDraft.ModuleInSphere(m.Id)))
            .OrderByDescending(x => x.on && x.sphere).ThenByDescending(x => x.on).ThenBy(x => x.m.Group, StringComparer.Ordinal).ThenBy(x => x.m.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var (m, on, sphere) in sorted)
        {
            var line = MarketplaceModuleContent.LineOf(m.Id);
            if (line is null)
                PosLogger.Log($"Тариф «Своё»: у модуля «{m.Id}» нет строки «что делает» (MarketplaceModuleContent.Lines)", "WARNING");
            var content = RowContent(MarketplaceModuleContent.ListIconKey(m.Id), MarketplaceModuleContent.ListColor(m.Id), m.Name, line);
            var box = new CheckBox { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, MinHeight = 44, IsEnabled = !_locked };
            Avalonia.Automation.AutomationProperties.SetName(box, m.Name);
            var monthly = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
            monthly.Bind(TextBlock.ForegroundProperty, monthly.GetResourceObservable("BrushText"));
            var activeText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold };
            activeText.Bind(TextBlock.ForegroundProperty, activeText.GetResourceObservable("BrushSuccess"));
            var active = new Border { Child = activeText, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            var price = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { monthly, active } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(box, 0);
            Grid.SetColumn(price, 1);
            grid.Children.Add(box);
            grid.Children.Add(price);
            _moduleRowsHost.Children.Add(grid);

            var row = new ModuleRow { Id = m.Id, Name = m.Name, Price = m.Price, Baseline = on, InSphere = sphere, Box = box, Monthly = monthly, Active = active, ActiveText = activeText, Line = line };
            _moduleRows[m.Id] = row;
            _suppress = true;
            box.IsChecked = TariffDraft.IsModuleSelected(m.Id, on);
            _suppress = false;
            RefreshModuleRow(row);
            box.IsCheckedChanged += (_, _) =>
            {
                if (_suppress)
                    return;
                TariffDraft.SetModule(row.Id, box.IsChecked == true, row.Baseline);
                RefreshModuleRow(row);
                OnSelectionChanged();
            };
        }
    }

    /// <summary>Цена и состояние строки функции: «Входит в вашу сферу» / «Включено» + цена; «Будет добавлено» / «Будет снято» — пока заявка не отправлена.</summary>
    private void RefreshModuleRow(ModuleRow row)
    {
        var selected = row.Box.IsChecked == true;
        var priceText = row.Price is { } p ? M(p) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy") : "";
        string? status = null;
        var warn = false;
        if (selected && row.Baseline)
            status = row.InSphere
                ? T("Входит в ваш тариф", "Тарифиңизге кирет", "Included in your plan", "Tarifinize dahil", "Tarifingizga kiradi")
                : T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan");
        else if (selected)
        {
            status = T("Будет добавлено", "Кошулат", "Will be added", "Eklenecek", "Qo'shiladi");
            warn = true;
        }
        else if (row.Baseline)
        {
            status = T("Будет снято", "Алынып салынат", "Will be removed", "Kaldırılacak", "Olib tashlanadi");
            warn = true;
        }

        // Цена видна, пока функция платная и не «входит в сферу»: чтобы было видно, сколько она стоит в месяц.
        row.Monthly.Text = priceText;
        row.Monthly.IsVisible = priceText.Length > 0 && !(row.InSphere && selected && row.Baseline) && !(row.InSphere && !selected);
        row.ActiveText.Text = status ?? "";
        row.ActiveText.Bind(TextBlock.ForegroundProperty, row.ActiveText.GetResourceObservable(warn ? "BrushWarning" : "BrushSuccess"));
        row.Active.IsVisible = status is not null;
    }

    private void BuildPeriodRow()
    {
        _periodRow.Children.Clear();
        _periodButtons.Clear();
        var show = _catalog?.Plans.Any(p => p.YearPrice is > 0) == true;
        _periodRow.IsVisible = show;
        if (!show)
            return;
        foreach (var (code, label) in new[]
                 {
                     ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")),
                     ("year", T("Год — платите за 10 мес.", "Жыл — 10 ай үчүн төлөйсүз", "Year — pay for 10 months", "Yıl — 10 ay için ödersiniz", "Yil — 10 oy uchun to'laysiz")),
                 })
        {
            var c = code;
            var b = UiKit.Chip(this, label, c == _period);
            b.Margin = new Thickness(0, 0, 8, 0);
            b.Click += (_, _) => SetPeriod(c);
            _periodButtons[c] = b;
            _periodRow.Children.Add(b);
        }
    }

    /// <summary>2026-11-21: «Месяц / Год» (год = 10 месяцев): перерасчёт у сервера с period.</summary>
    private void SetPeriod(string period)
    {
        period = string.Equals(period, "year", StringComparison.OrdinalIgnoreCase) ? "year" : "month";
        if (_period == period || _locked)
            return;
        _period = period;
        foreach (var (code, b) in _periodButtons)
            UiKit.SetActive(b, code == _period);
        _baseQuote = null;
        OnSelectionChanged();
        if (!HasChanges() && _included.Count > 0)
            _ = ReloadBaseQuoteAsync(_version);
    }

    private async Task ReloadBaseQuoteAsync(int version)
    {
        try
        {
            var q = await _api.QuoteAsync(Array.Empty<string>(), QuoteModules(), QuoteSphere(), null, _period).ConfigureAwait(true);
            if (version != _version)
                return;
            _baseQuote = _quote = q;
            RenderSummary();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(TariffApi.Describe(ex));
        }
    }

    private void OnSelectionChanged()
    {
        if (_suppress)
            return;
        // Набор изменился: прежний расчёт и ключ попытки больше не относятся к нему.
        _version++;
        _quote = null;
        _attemptKey = null;
        _done.IsVisible = false;
        ShowError(null);
        _submit.IsEnabled = false;
        _debounce.Stop();
        _quoteCts?.Cancel();
        if (!HasChanges() && _baseQuote is not null)
            _quote = _baseQuote; // всё вернули как было — снова текущая стоимость тарифа
        RefreshIncludedRows();
        RenderChanges();
        RenderSummary();
        if (HasChanges())
            _debounce.Start();
    }

    private async Task RequestQuoteAsync()
    {
        var codes = Selected();
        var removed = Removed();
        if (!HasChanges())
            return;
        var version = _version;
        _quoteCts?.Cancel();
        var cts = _quoteCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _summary.Text = T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
        try
        {
            var quote = await _api.QuoteAsync(codes, SelectionModules(), QuoteSphere(), removed, _period, cts.Token).ConfigureAwait(true);
            if (version != _version)
                return; // пока считали, отметки поменялись — ответ устарел
            _quote = quote;
            foreach (var qp in quote.Packs)
            {
                if (_included.Contains(qp.Code))
                    continue; // пакеты тарифа показывает RefreshIncludedRows
                if (qp.AlreadyActive)
                    MarkActive(qp.Code);
                else if (_packRows.TryGetValue(qp.Code, out var row))
                {
                    row.Monthly.Text = M(qp.Monthly) + " " + T("/ мес", "/ ай", "/ mo", "/ ay", "/ oy");
                    row.Once.Text = "+ " + M(qp.Once) + " " + T("разово", "бир жолу", "one-off", "tek sefer", "bir marta");
                }
            }

            PublishBaselines();
            RefreshIncludedRows();
            RenderSummary();
            _submit.IsEnabled = !_locked && !_submitting && HasChanges();
        }
        catch (OperationCanceledException) when (version != _version || cts.IsCancellationRequested && !IsVisible)
        {
            // устаревший расчёт — молча
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            ShowError(TariffApi.Describe(ex));
            RenderSummary();
            if (ex is ApiException { StatusCode: 403 or 409 })
                Lock(ex is ApiException { StatusCode: 409 });
        }
    }

    /// <summary>2026-11-21 (ответ Умара 20.11, В.23): расчёт идёт вместе с модулями компании. Реестр модулей известен — шлём включённые модули и профиль; нет — поля не шлём, сервер берёт текущие сам.</summary>
    private static List<string>? QuoteModules() => CompanyModules.Snapshot is { } s ? s.Enabled.OrderBy(x => x, StringComparer.Ordinal).ToList() : null;

    private static string? QuoteSphere() => CompanyModules.Snapshot is { Profile.Length: > 0 } s ? s.Profile : null;

    /// <summary>Итого в месяц: если сервер посчитал и модули — его total_month (тариф + пакеты + модули других сфер), иначе основа + пакеты.</summary>
    private static decimal MonthTotal(TariffQuote q) => q.TotalMonth ?? q.Base + q.Monthly;

    private void RenderSummary()
    {
        _standardHint.IsVisible = false;
        if (_quote is null)
        {
            _summary.Text = !HasChanges()
                ? T("Отметьте пакеты — здесь появится стоимость.", "Пакеттерди белгилеңиз — бул жерде баасы пайда болот.", "Tick packs — the cost will appear here.", "Paketleri işaretleyin — maliyet burada görünecek.", "Paketlarni belgilang — narxi shu yerda paydo bo'ladi.")
                : _summary.Text;
            return;
        }

        var q = _quote;
        // 2026-11-20: основа — ваш тариф («Старт» или «Стандарт»); цифру основы даёт сервер (quote.base), касса её не считает.
        // 2026-11-21 (ответ Умара 21.11): base_plan — база расчёта (при снятии пакета со Стандарта — «Старт»), base — её цена; plan_after "custom" — тариф станет «Своё».
        var basePlanCode = q.BasePlan is { Length: > 0 } bp ? bp : _planCode is "start" or "standard" ? _planCode : "start";
        var baseName = _catalog?.Plan(basePlanCode)?.Name ?? (basePlanCode == "standard" ? T("Стандарт", "Стандарт", "Standard", "Standart", "Standart") : T("Старт", "Старт", "Start", "Start", "Start"));
        var lines = new List<string>
        {
            T($"Основа ({baseName}): {M(q.Base)} в месяц", $"Негиз ({baseName}): айына {M(q.Base)}", $"Base ({baseName}): {M(q.Base)} a month", $"Temel ({baseName}): ayda {M(q.Base)}", $"Asos ({baseName}): oyiga {M(q.Base)}"),
            T($"Пакеты: {M(q.Monthly)} в месяц", $"Пакеттер: айына {M(q.Monthly)}", $"Packs: {M(q.Monthly)} a month", $"Paketler: ayda {M(q.Monthly)}", $"Paketlar: oyiga {M(q.Monthly)}"),
            T($"Подключение (один раз): {M(q.Once)}", $"Туташтыруу (бир жолу): {M(q.Once)}", $"Activation (one-off): {M(q.Once)}", $"Etkinleştirme (tek sefer): {M(q.Once)}", $"Ulash (bir marta): {M(q.Once)}"),
            T($"Итого в месяц: {M(MonthTotal(q))}", $"Айына бардыгы: {M(MonthTotal(q))}", $"Total a month: {M(MonthTotal(q))}", $"Aylık toplam: {M(MonthTotal(q))}", $"Oyiga jami: {M(MonthTotal(q))}"),
        };
        if (!HasChanges())
            lines.RemoveRange(1, 2); // ничего не меняли — строки «Пакеты» и «Подключение» с нулями не нужны
        if (q.HasModules)
        {
            // 2026-11-21: модули профиля входят бесплатно (included), модули других сфер — extra с ценой в месяц; total_month считает сервер.
            // 2026-11-26: имена функций не перечисляем сплошным абзацем — они в списке ниже (значок, название, «что делает», цена); здесь только число и сумма от сервера.
            var extraLines = new List<string>();
            if (q.Included is { Count: > 0 })
            {
                var n = q.Included.Count;
                extraLines.Add(T($"Функции вашей сферы (входят в тариф): {n}", $"Чөйрөңүздүн функциялары (тарифке кирет): {n}", $"Features of your sphere (included): {n}", $"Alanınızın özellikleri (dahil): {n}", $"Sohangiz funksiyalari (tarifga kiradi): {n}"));
            }
            if (q.Extra is { Count: > 0 })
            {
                var n = q.Extra.Count;
                var extraSum = M(q.ExtraMonth ?? q.Extra.Sum(x => x.Monthly));
                extraLines.Add(T($"Функции других сфер: {n} — {extraSum} в месяц", $"Башка чөйрөлөрдүн функциялары: {n} — айына {extraSum}", $"Features from other spheres: {n} — {extraSum} a month", $"Diğer alanların özellikleri: {n} — ayda {extraSum}", $"Boshqa sohalar funksiyalari: {n} — oyiga {extraSum}"));
            }
            lines.InsertRange(Math.Min(2, lines.Count), extraLines);
        }

        // 2026-11-21 (ответ Умара 21.11): год = 10 месяцев — итог за год и скидка только с сервера (total_year, year_discount).
        if (string.Equals(_period, "year", StringComparison.OrdinalIgnoreCase) && q.TotalYear is { } ty)
        {
            lines.Add(T($"Итого за год (12 мес.): {M(ty)}", $"Жылына бардыгы (12 ай): {M(ty)}", $"Total for the year (12 months): {M(ty)}", $"Yıllık toplam (12 ay): {M(ty)}", $"Yillik jami (12 oy): {M(ty)}"));
            if (q.YearDiscount is { } yd and > 0)
            {
                var paid = q.YearPaidMonths ?? 10;
                lines.Add(T($"Скидка за год: {M(yd)} (платите за {paid} мес.)", $"Жылдык арзандатуу: {M(yd)} ({paid} ай үчүн төлөйсүз)", $"Year discount: {M(yd)} (you pay for {paid} months)", $"Yıllık indirim: {M(yd)} ({paid} ay için ödersiniz)", $"Yillik chegirma: {M(yd)} ({paid} oy uchun to'laysiz)"));
            }
        }

        // plan_after "custom": тариф станет «Своё» — Старт + оставшиеся пакеты; removed — что снято.
        if (q.BecomesCustom)
        {
            var custom = PlanName("custom", T("Своё", "Өзүмдүкү", "Custom", "Özel", "O'zimniki"));
            var removedNames = string.Join(", ", (q.Removed is { Count: > 0 } ? q.Removed : Removed()).Select(PackName));
            lines.Insert(0, T($"Тариф станет «{custom}»: {baseName} + оставшиеся пакеты. Снято: {removedNames}.", $"Тариф «{custom}» болот: {baseName} + калган пакеттер. Алынды: {removedNames}.",
                $"The plan becomes “{custom}”: {baseName} + the remaining packs. Removed: {removedNames}.", $"Tarife «{custom}» olur: {baseName} + kalan paketler. Kaldırılan: {removedNames}.", $"Tarif «{custom}» bo'ladi: {baseName} + qolgan paketlar. Olib tashlandi: {removedNames}."));
        }
        _summary.Text = string.Join("\n", lines);

        if (q.StandardCheaper && !string.Equals(_planCode, "standard", StringComparison.OrdinalIgnoreCase)) // на «Стандарте» подсказка «Стандарт дешевле» бессмысленна
        {
            var standard = _catalog?.Plan("standard")?.Name ?? T("Стандарт", "Стандарт", "Standard", "Standart", "Standart");
            var price = q.StandardPrice is { } sp ? M(sp) : "";
            _standardHint.Text = T($"«{standard}» выйдет дешевле: {price} в месяц, и все пакеты уже входят.", $"«{standard}» арзаныраак болот: айына {price}, бардык пакеттер кирет.",
                $"“{standard}” is cheaper: {price} a month, all packs included.", $"«{standard}» daha ucuz: ayda {price}, tüm paketler dahil.", $"«{standard}» arzonroq: oyiga {price}, barcha paketlar kiradi.");
            _standardHint.IsVisible = true;
        }
    }

    private async Task SubmitAsync()
    {
        var codes = Selected();
        var removed = Removed();
        if (_submitting || _locked || !HasChanges() || _quote is null)
            return;
        var q = _quote;
        var names = codes.Count == 0 ? "—" : string.Join(", ", codes.Select(PackName));
        var ask = T($"Отправить заявку на тариф «Своё»?\n\nПакеты: {names}.\nВ месяц: {M(MonthTotal(q))} (из них пакеты — {M(q.Monthly)}). Подключение один раз: {M(q.Once)}.\n\nЗаявку обработает менеджер и свяжется с вами; тариф и пакеты включатся после этого.",
            $"«Өзүмдүкү» тарифине өтүнүч жөнөтөсүзбү?\n\nПакеттер: {names}.\nАйына: {M(MonthTotal(q))} (анын ичинде пакеттер — {M(q.Monthly)}). Туташтыруу бир жолу: {M(q.Once)}.\n\nӨтүнүчтү менеджер иштетип, сиз менен байланышат; тариф жана пакеттер ошондон кийин иштейт.",
            $"Send a request for the “Custom” plan?\n\nPacks: {names}.\nPer month: {M(MonthTotal(q))} (packs — {M(q.Monthly)}). Activation, once: {M(q.Once)}.\n\nA manager will process it and contact you; the plan and packs are switched on after that.",
            $"«Özel» tarife için talep gönderilsin mi?\n\nPaketler: {names}.\nAylık: {M(MonthTotal(q))} (paketler — {M(q.Monthly)}). Etkinleştirme, tek sefer: {M(q.Once)}.\n\nTalebi bir yönetici işleyip sizinle iletişime geçer; tarife ve paketler bundan sonra açılır.",
            $"«O'zimniki» tarifiga ariza yuborilsinmi?\n\nPaketlar: {names}.\nOyiga: {M(MonthTotal(q))} (paketlar — {M(q.Monthly)}). Ulash, bir marta: {M(q.Once)}.\n\nArizani menejer ko'rib chiqib, siz bilan bog'lanadi; tarif va paketlar shundan keyin yoqiladi.");
        // 2026-11-21: снятые пакеты и годовой период — отдельными строками подтверждения (цифры — только сервера).
        if (removed.Count > 0)
        {
            var removedNames = string.Join(", ", removed.Select(PackName));
            ask += "\n\n" + T($"Снять пакеты: {removedNames}. Тариф станет «Своё»: {PlanName("start", "Старт")} + оставшиеся пакеты.", $"Пакеттер алынат: {removedNames}. Тариф «Өзүмдүкү» болот: {PlanName("start", "Старт")} + калган пакеттер.",
                $"Packs removed: {removedNames}. The plan becomes “Custom”: {PlanName("start", "Start")} + the remaining packs.", $"Kaldırılacak paketler: {removedNames}. Tarife «Özel» olur: {PlanName("start", "Start")} + kalan paketler.", $"Olib tashlanadigan paketlar: {removedNames}. Tarif «O'zimniki» bo'ladi: {PlanName("start", "Start")} + qolgan paketlar.");
        }
        // 2026-11-26: функции магазина (модули) — добавить / снять; цифры по ним уже в итоге сервера (total_month).
        var addedModules = SelectedModules();
        var removedModules = RemovedModules();
        if (addedModules.Count > 0)
        {
            var addNames = string.Join(", ", addedModules.Select(ModuleName));
            ask += "\n\n" + T($"Добавить функции: {addNames}.", $"Функцияларды кошуу: {addNames}.", $"Add features: {addNames}.", $"Eklenecek özellikler: {addNames}.", $"Qo'shiladigan funksiyalar: {addNames}.");
        }
        if (removedModules.Count > 0)
        {
            var delNames = string.Join(", ", removedModules.Select(ModuleName));
            ask += "\n" + T($"Снять функции: {delNames}.", $"Функцияларды алып салуу: {delNames}.", $"Remove features: {delNames}.", $"Kaldırılacak özellikler: {delNames}.", $"Olib tashlanadigan funksiyalar: {delNames}.");
        }
        if (string.Equals(_period, "year", StringComparison.OrdinalIgnoreCase) && q.TotalYear is { } totalYear)
            ask += "\n" + T($"Период: год (платите за 10 мес. из 12) — {M(totalYear)}.", $"Мөөнөт: жыл (12 айдан 10 ай үчүн төлөйсүз) — {M(totalYear)}.", $"Period: year (you pay for 10 of 12 months) — {M(totalYear)}.",
                $"Dönem: yıl (12 ayın 10'unu ödersiniz) — {M(totalYear)}.", $"Davr: yil (12 oydan 10 tasi uchun to'laysiz) — {M(totalYear)}.");
        var confirmed = Confirm is { } custom
            ? await custom(ask).ConfigureAwait(true)
            : await PosDialogs.ConfirmYesNoModalAsync(this, ask, Title, T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish")).ConfigureAwait(true);
        if (!confirmed)
            return;

        // Ключ — на попытку: при обрыве связи нажатие «Отправить» ещё раз уходит с ТЕМ ЖЕ ключом (сервер вернёт ту же заявку, не вторую).
        // Набор пакетов изменился — ключ новый (OnSelectionChanged обнуляет), успех или окончательный отказ сервера — тоже.
        var key = _attemptKey ??= Guid.NewGuid().ToString("D");
        _submitting = true;
        _submit.IsEnabled = false;
        ShowError(null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var order = await _api.SubmitCustomAsync(codes, SelectionModules(), removed, _period, key, cts.Token).ConfigureAwait(true);
            _attemptKey = null;
            // 2026-11-21 (живая проверка на ТЕСТ Своё): сервер отдаёт номер заявки GUID-ом (36 знаков) — в тексте это некрасиво; длинный номер показываем первыми 8 знаками.
            var shownId = order.OrderId.Length > 12 ? order.OrderId[..8].ToUpperInvariant() : order.OrderId;
            _done.Text = T($"Заявка №{shownId} принята — менеджер свяжется с вами. Платить сейчас ничего не нужно.",
                $"№{shownId} өтүнүч кабыл алынды — менеджер сиз менен байланышат. Азыр эч нерсе төлөбөйсүз.",
                $"Request No. {shownId} received — a manager will contact you. Nothing to pay now.",
                $"{shownId} numaralı talep alındı — bir yönetici sizinle iletişime geçecek. Şimdi ödeme gerekmez.",
                $"№{shownId} ariza qabul qilindi — menejer siz bilan bog'lanadi. Hozir to'lash shart emas.");
            _done.IsVisible = true;
            foreach (var row in _packRows.Values)
                row.Box.IsEnabled = false;
            foreach (var row in _moduleRows.Values)
                row.Box.IsEnabled = false;
            _locked = true;
            _close.Content = T("Готово", "Даяр", "Done", "Tamam", "Tayyor");
            TariffDraft.Finish(); // 2026-11-26: заявка отправлена (режим сборки тоже выключается) — общий набор (полоса и плитки Маркетплейса) очищается; галочки этого окна остаются как были отправлены
            _changes.IsVisible = false;
        }
        catch (Exception ex)
        {
            if (ex is ApiException { StatusCode: >= 400 and < 500 and not 408 and not 429 })
                _attemptKey = null; // сервер ответил по существу — следующая попытка новая
            PosLogger.Log($"Тариф «Своё»: заявка не отправлена: {ex.Message}", "WARNING");
            ShowError(TariffApi.Describe(ex) + (_attemptKey is null ? "" : "\n" + T("Можно нажать «Отправить заявку» ещё раз — заявка не задвоится.", "«Өтүнүч жөнөтүү» баскычын дагы бир жолу басса болот — өтүнүч эки эсе болбойт.",
                "You can press “Send request” again — the request will not be duplicated.", "«Talep gönder»e tekrar basabilirsiniz — talep ikiye katlanmaz.", "«Ariza yuborish»ni yana bosish mumkin — ariza ikkilanmaydi.")));
            if (ex is ApiException { StatusCode: 403 or 409 })
                Lock(ex is ApiException { StatusCode: 409 });
        }
        finally
        {
            _submitting = false;
            _submit.IsEnabled = !_locked && HasChanges() && _quote is not null;
        }
    }
}
