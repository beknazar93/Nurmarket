using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26, «Клиенты → Рассылка» (ТОП-15 п. 2 отчёта о конкурентах, поручение владельца «создай то, чего у нас нет»). Сегмент клиентов (должники,
/// «давно не покупали», VIP, день рождения, все с телефоном) → список получателей с числом → текст с подстановками {имя} {долг} {магазин} {дней} →
/// «Открыть WhatsApp по очереди»: каждое нажатие открывает ОДИН чат wa.me с готовым текстом, «Отправить» нажимает владелец (без автоотправки и без обхода правил
/// WhatsApp). Защита: «не беспокоить», один клиент — не чаще раза в N дней, один номер — один раз, дневной лимит чатов, тихие часы, отказ клиента по данным
/// сервера (проверяется перед каждым чатом). Журнал — кому и когда открыт чат; отметку можно снять. Правила и файл — <see cref="ClientMailing"/>,
/// данные клиентов — <see cref="ClientMailingData"/>. Только программа владельца (раздел меню «Рассылка» в группе «Клиенты»).</summary>
public sealed class ClientMailingWindow : Window, IOwnerSection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly Grid _root;
    private readonly WrapPanel _segmentChips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _quietNote = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _journal = new() { Spacing = 4 };
    private readonly StackPanel _dndList = new() { Spacing = 6 };
    private readonly Grid _recipientsView = new() { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
    private readonly Border _journalView = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1), IsVisible = false };
    private readonly Border _dndView = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1), IsVisible = false };
    private readonly Button _tabRecipients, _tabJournal, _tabDnd, _refresh, _tickAll, _openNext, _skipNext;
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

    // Параметр выбранного сегмента (дней / сумма) и общие настройки.
    private readonly StackPanel _paramBox = new() { Spacing = 4, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _paramLabel;
    private readonly TextBox _paramInput;
    private readonly TextBlock _segmentHint = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _gapBox, _limitBox;
    private readonly TextBlock _settingsNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    // Правая карточка: язык, текст, подстановки, предпросмотр, карточка клиента.
    // 2026-11-27: WrapPanel вместо StackPanel — на экране 800 px кнопка «O'zbekcha» уходила за край карточки (OwnerSectionsSmallScreens).
    private readonly WrapPanel _langs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _templateBox;
    private readonly TextBlock _templateCount = new() { FontSize = 12.5 };
    private readonly SelectableTextBlock _previewText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly Border _previewBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1) };
    private readonly TextBlock _previewTitle = new() { FontSize = 14.5, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _clientTools = new() { Spacing = 6 };
    private readonly TextBox _birthdayBox;
    private readonly Button _dndButton;
    private readonly TextBlock _clientToolsNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    private ClientMailing.Segment _segment = ClientMailing.Segment.Debtors;
    private string _lang = ClientMailing.DefaultLanguage();
    private ClientMailingData.Snapshot? _snapshot;
    private ClientMailing.PlanResult _plan = new(Array.Empty<ClientMailing.Recipient>(), 0, 0);
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _openedNow = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private ClientMailing.Recipient? _selected;
    private List<ClientMailing.Recipient>? _queue;
    private int _next;
    private bool _busy;
    private bool _loadedOnce;
    private int _loadGen;
    private bool _templateLoading;
    private readonly CancellationTokenSource _cts = new();
    private const int MaxRowsShown = 150;

    /// <summary>Для тестов: вместо запуска ссылки wa.me (иначе тест откроет настоящий браузер) и вместо модального вопроса перед началом очереди.</summary>
    public static Func<string, bool>? OpenUrlForTests { get; set; }
    public static Func<int, Task<bool>>? ConfirmStartForTests { get; set; }

    /// <summary>Для тестов: «сейчас тихие часы» — иначе исход зависел бы от времени суток, в которое идёт прогон.</summary>
    public static Func<bool>? QuietHoursForTests { get; set; }

    private static bool IsQuiet() => QuietHoursForTests?.Invoke() ?? DebtReminderPlanner.InQuietHours();

    public ClientMailingWindow()
    {
        Title = T("Рассылка", "Жөнөтмө", "Mailing", "Toplu mesaj", "Xabarnoma");
        Width = 1100;
        Height = 760;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        var settings = ClientMailing.GetSettings();

        // ── Вкладки и «Обновить» ──
        _tabRecipients = UiKit.Seg(T("Получатели", "Алуучулар", "Recipients", "Alıcılar", "Qabul qiluvchilar"), true);
        _tabJournal = UiKit.Seg(T("Журнал", "Журнал", "Log", "Kayıt", "Jurnal"), false);
        _tabDnd = UiKit.Seg(T("Не беспокоить", "Тынчсыздандырбоо", "Do not disturb", "Rahatsız etme", "Bezovta qilmaslik"), false);
        _tabRecipients.Click += (_, _) => ShowTab(0);
        _tabJournal.Click += (_, _) => ShowTab(1);
        _tabDnd.Click += (_, _) => ShowTab(2);
        var tabs = UiKit.Segment(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _tabRecipients, _tabJournal, _tabDnd } });
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        _refresh = Small(UiKit.Ghost(this, T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash")));
        AutomationProperties.SetName(_refresh, (string)_refresh.Content!);
        _refresh.Click += async (_, _) => await LoadAsync(force: true).ConfigureAwait(true);
        var topBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        topBar.Children.Add(tabs);
        Grid.SetColumn(_refresh, 1);
        _refresh.Margin = new Thickness(8, 0, 0, 0);
        topBar.Children.Add(_refresh);

        // ── Выбор сегмента ──
        RenderSegmentChips();

        // ── Левая карточка: параметр сегмента, описание, список ──
        _paramLabel = UiKit.Label(this, "");
        _paramLabel.TextWrapping = TextWrapping.Wrap;
        _paramLabel.MaxWidth = 200;
        _paramInput = NumberBox("");
        _paramInput.LostFocus += (_, _) => ApplyParam();
        _paramInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                ApplyParam();
        };
        _paramBox.Children.Add(_paramLabel);
        _paramBox.Children.Add(_paramInput);
        Use(_segmentHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        var band = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 8) };
        band.Children.Add(_paramBox);
        Grid.SetColumn(_segmentHint, 1);
        band.Children.Add(_segmentHint);
        var listScroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listPanel.Children.Add(band);
        Grid.SetRow(listScroll, 1);
        listPanel.Children.Add(listScroll);
        _recipientsView.Children.Add(Card(listPanel));

        // ── Правая карточка ──
        _templateBox = UiKit.Input(this, "", 120);
        _templateBox.AcceptsReturn = true;
        _templateBox.TextWrapping = TextWrapping.Wrap;
        _templateBox.VerticalContentAlignment = VerticalAlignment.Top;
        _templateBox.Padding = new Thickness(10, 8);
        _templateBox.FontSize = 13.5;
        _templateBox.MaxLength = ClientMailing.MaxTextLength;
        AutomationProperties.SetName(_templateBox, T("Текст сообщения", "Билдирүүнүн тексти", "Message text", "Mesaj metni", "Xabar matni"));
        _templateBox.TextChanged += (_, _) =>
        {
            if (_templateLoading)
                return;
            UpdateTemplateCount();
            ShowPreview();
        };
        _templateBox.LostFocus += (_, _) => CommitTemplate();
        var tokens = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var token in new[] { "{имя}", "{долг}", "{магазин}", "{дней}" })
        {
            var t = token;
            var chip = Small(UiKit.Ghost(this, token));
            chip.Padding = new Thickness(12, 0);
            chip.Margin = new Thickness(0, 0, 6, 6);
            AutomationProperties.SetName(chip, T("Вставить в текст: ", "Тексттке коюу: ", "Insert into text: ", "Metne ekle: ", "Matnga qo'yish: ") + token);
            chip.Click += (_, _) => InsertToken(t);
            tokens.Children.Add(chip);
        }
        var resetText = Small(UiKit.Ghost(this, T("Стандартный текст", "Стандарттык текст", "Default text", "Varsayılan metin", "Standart matn")));
        AutomationProperties.SetName(resetText, (string)resetText.Content!);
        resetText.HorizontalAlignment = HorizontalAlignment.Left;
        resetText.Click += (_, _) =>
        {
            _templateBox.Text = ClientMailing.DefaultTemplate(_segment, _lang);
            CommitTemplate();
        };
        Use(_templateCount, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_previewTitle, TextBlock.ForegroundProperty, "BrushText");
        Use(_previewInfo, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_previewText, TextBlock.ForegroundProperty, "BrushText");
        Use(_previewBox, Border.BackgroundProperty, "BrushWindowBackdrop");
        Use(_previewBox, Border.BorderBrushProperty, "BrushBorder");
        _previewBox.Child = _previewText;

        _birthdayBox = UiKit.Input(this, T("дд.мм", "кк.аа", "dd.mm", "gg.aa", "kk.oo"), 38);
        _birthdayBox.MaxLength = 10;
        AutomationProperties.SetName(_birthdayBox, T("День рождения клиента (дд.мм)", "Кардардын туулган күнү (кк.аа)", "Customer birthday (dd.mm)", "Müşterinin doğum günü (gg.aa)", "Mijozning tug'ilgan kuni (kk.oo)"));
        var saveBirthday = Small(UiKit.Ghost(this, T("Запомнить", "Эстеп калуу", "Remember", "Hatırla", "Eslab qolish")));
        AutomationProperties.SetName(saveBirthday, (string)saveBirthday.Content!);
        saveBirthday.Click += (_, _) => SaveBirthday();
        var birthdayRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        birthdayRow.Children.Add(_birthdayBox);
        Grid.SetColumn(saveBirthday, 1);
        saveBirthday.Margin = new Thickness(8, 0, 0, 0);
        birthdayRow.Children.Add(saveBirthday);
        _dndButton = Small(UiKit.Ghost(this, ""));
        _dndButton.HorizontalAlignment = HorizontalAlignment.Left;
        _dndButton.Click += (_, _) => ToggleDnd();
        Use(_clientToolsNote, TextBlock.ForegroundProperty, "BrushTextSoft");
        _clientTools.Children.Add(UiKit.Label(this, T("День рождения клиента (дд.мм)", "Кардардын туулган күнү (кк.аа)", "Customer birthday (dd.mm)", "Müşterinin doğum günü (gg.aa)", "Mijozning tug'ilgan kuni (kk.oo)")));
        _clientTools.Children.Add(birthdayRow);
        _clientTools.Children.Add(_dndButton);
        _clientTools.Children.Add(_clientToolsNote);

        _gapBox = NumberBox(settings.MinGapDays.ToString(CultureInfo.InvariantCulture));
        _limitBox = NumberBox(settings.DailyLimit.ToString(CultureInfo.InvariantCulture));
        _gapBox.LostFocus += (_, _) => ApplySettings();
        _limitBox.LostFocus += (_, _) => ApplySettings();
        _gapBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySettings(); };
        _limitBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySettings(); };
        AutomationProperties.SetName(_gapBox, T("Одному клиенту не чаще раза в, дней", "Бир кардарга эң көп, күндө бир жолу", "Per customer at most once in, days", "Bir müşteriye en fazla, günde bir", "Bir mijozga ko'pi bilan, kunda bir marta"));
        AutomationProperties.SetName(_limitBox, T("Не больше чатов за день", "Күнүнө чат саны", "Chats per day at most", "Günde en fazla sohbet", "Kuniga ko'pi bilan chat"));
        var limits = new WrapPanel { Orientation = Orientation.Horizontal };
        limits.Children.Add(Field(T("Одному клиенту не чаще раза в, дней", "Бир кардарга эң көп, күндө бир жолу", "Per customer at most once in, days", "Bir müşteriye en fazla, günde bir", "Bir mijozga ko'pi bilan, kunda bir marta"), _gapBox));
        limits.Children.Add(Field(T("Не больше чатов за день", "Күнүнө эң көп чат", "Chats per day at most", "Günde en fazla sohbet", "Kuniga ko'pi bilan chat"), _limitBox));
        Use(_settingsNote, TextBlock.ForegroundProperty, "BrushTextSoft");

        var rightStack = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                UiKit.Label(this, T("Язык сообщения", "Билдирүүнүн тили", "Message language", "Mesaj dili", "Xabar tili")),
                UiKit.Segment(_langs),
                UiKit.Label(this, T("Текст сообщения", "Билдирүүнүн тексти", "Message text", "Mesaj metni", "Xabar matni")),
                _templateBox, _templateCount,
                UiKit.Label(this, T("Вставить в текст", "Тексттке коюу", "Insert into text", "Metne ekle", "Matnga qo'yish")),
                tokens, resetText,
                UiKit.Label(this, T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish")),
                _previewTitle, _previewInfo, _previewBox,
                _clientTools,
                UiKit.Label(this, T("Защита от лишних сообщений", "Ашыкча билдирүүдөн коргоо", "Protection from too many messages", "Fazla mesajlardan koruma", "Ortiqcha xabarlardan himoya")),
                limits, _settingsNote,
            },
        };
        var rightCard = Card(new ScrollViewer { Content = rightStack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        rightCard.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(rightCard, 1);
        _recipientsView.Children.Add(rightCard);

        // ── Журнал и «не беспокоить» ──
        _journalView.Child = new ScrollViewer { Content = _journal, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _dndView.Child = new ScrollViewer { Content = _dndList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        foreach (var view in new[] { _journalView, _dndView })
        {
            Use(view, Border.BackgroundProperty, "BrushPanel");
            Use(view, Border.BorderBrushProperty, "BrushBorder");
        }

        // ── Кнопки действий ──
        _openNext = UiKit.Primary(this, "");
        _openNext.Click += (_, _) => OnOpenNext();
        _skipNext = Small(UiKit.Ghost(this, T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish")));
        AutomationProperties.SetName(_skipNext, (string)_skipNext.Content!);
        ToolTip.SetTip(_skipNext, T("Перейти к следующему клиенту, не открывая чат с этим.", "Бул менен чатты ачпай, кийинки кардарга өтүү.", "Move to the next customer without opening a chat with this one.",
            "Bu kişiyle sohbet açmadan sıradakine geç.", "Bu mijoz bilan chat ochmasdan keyingisiga o'tish."));
        _skipNext.Click += (_, _) => OnSkip();
        _tickAll = Small(UiKit.Ghost(this, ""));
        _tickAll.Click += (_, _) => ToggleAll();
        foreach (var b in new[] { _openNext, _skipNext, _tickAll })
        {
            b.Margin = new Thickness(0, 0, 10, 6);
            _actions.Children.Add(b);
        }

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_quietNote, TextBlock.ForegroundProperty, "BrushWarning");
        var statusStack = new StackPanel { Children = { _quietNote, _status } };

        _root = new Grid { Margin = new Thickness(20, 16, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        AddRow(_root, topBar, 0);
        AddRow(_root, _segmentChips, 1);
        AddRow(_root, statusStack, 2);
        AddRow(_root, _recipientsView, 3);
        AddRow(_root, _journalView, 3);
        AddRow(_root, _dndView, 3);
        AddRow(_root, _actions, 4);
        Content = _root;
        NurMarketKassa.AvaloniaHost.Services.WindowChrome.Apply(this);

        Closed += (_, _) =>
        {
            CommitTemplate();
            _cts.Cancel();
        };
        Opened += async (_, _) => await LoadAsync(force: false).ConfigureAwait(true);

        ApplySegmentUi();
        RenderLangs();
        LoadTemplateIntoBox();
        ShowPreview();
        UpdateButtons();
        SetStatus(IdleText(), "BrushTextSoft");
    }

    /// <summary>Вызывается программой владельца один раз — сразу после создания окна, до показа.</summary>
    public void AsOwnerSection() => _root.Margin = OwnerSectionLayout.Margin;

    private static string IdleText() => T("Программа сама ничего не отправляет: она открывает чат WhatsApp с готовым текстом, «Отправить» нажимаете вы. Клиентам, которые просили не писать, — отметка «Не беспокоить».",
        "Программа өзү эч нерсе жөнөтпөйт: WhatsApp чатын даяр текст менен ачат, «Жөнөтүү» баскычын өзүңүз басасыз. Жазбоону суранган кардарларга — «Тынчсыздандырбоо» белгиси.",
        "The program never sends anything by itself: it opens a WhatsApp chat with the text ready, you press Send. Customers who asked not to be written to get the “Do not disturb” mark.",
        "Program kendiliğinden hiçbir şey göndermez: hazır metinle WhatsApp sohbetini açar, Gönder'e siz basarsınız. Yazılmasını istemeyen müşterilere «Rahatsız etme» işareti konur.",
        "Dastur o'zi hech narsa yubormaydi: tayyor matn bilan WhatsApp chatini ochadi, «Yuborish»ni o'zingiz bosasiz. Yozmaslikni so'ragan mijozlarga «Bezovta qilmaslik» belgisi qo'yiladi.");

    // ── Загрузка и список ───────────────────────────────────────────────────────

    private async Task LoadAsync(bool force)
    {
        var gen = ++_loadGen;
        _refresh.IsEnabled = false;
        SetStatus(T("Загружаю клиентов, долги и чеки за год…", "Кардарларды, карыздарды жана бир жылдын чектерин жүктөп жатам…", "Loading customers, debts and receipts for the year…",
            "Müşteriler, borçlar ve yıllık fişler yükleniyor…", "Mijozlar, qarzlar va yillik cheklar yuklanmoqda…"), "BrushTextSoft");
        try
        {
            var snapshot = await ClientMailingData.LoadAsync(_cts.Token, force).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            _snapshot = snapshot;
            _loadedOnce = true;
            _queue = null;
            Replan(resetChecks: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            PosLogger.Log($"Рассылка: список клиентов не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(T("Сервер NurCRM недоступен — список клиентов не загружен. Проверьте интернет и нажмите «Обновить».",
                "NurCRM сервери жеткиликсиз — кардарлардын тизмеси жүктөлгөн жок. Интернетти текшерип, «Жаңылоо» басыңыз.",
                "The NurCRM server is unavailable — the customer list did not load. Check the internet and press Refresh.",
                "NurCRM sunucusu kullanılamıyor — müşteri listesi yüklenmedi. İnterneti kontrol edip Yenile'ye basın.",
                "NurCRM serveri mavjud emas — mijozlar ro'yxati yuklanmadi. Internetni tekshirib, «Yangilash»ni bosing."), "BrushDanger");
        }
        finally
        {
            if (gen == _loadGen)
            {
                _refresh.IsEnabled = true;
                UpdateButtons();
            }
        }
    }

    /// <summary>Пересчитать список получателей из загруженных данных (без запросов).</summary>
    private void Replan(bool resetChecks)
    {
        if (_snapshot is null)
            return;
        var s = ClientMailing.GetSettings();
        _plan = ClientMailing.Plan(_segment, _snapshot.Contacts, s, ClientMailing.DoNotDisturb(), ClientMailing.LastMailed(), DateTime.Now, ClientMailing.OpenedToday());
        if (resetChecks)
        {
            _checked.Clear();
            foreach (var r in _plan.Items.Where(r => r.IsDue))
                _checked.Add(r.Contact.ClientId);
        }
        else
            _checked.RemoveWhere(id => !_plan.Items.Any(r => r.IsDue && string.Equals(r.Contact.ClientId, id, StringComparison.OrdinalIgnoreCase)));
        _selected = _plan.Items.FirstOrDefault(r => _selected is not null && r.Contact.ClientId == _selected.Contact.ClientId) ?? _plan.Items.FirstOrDefault();
        RenderList();
        RenderStatus();
        ShowPreview();
        UpdateButtons();
    }

    private void RenderStatus()
    {
        if (_snapshot is null)
            return;
        var due = _plan.Items.Count(r => r.IsDue && !_refused.Contains(r.Contact.ClientId));
        var skipped = _plan.Items.Count - due;
        // 2026-11-26 (стресс-тест): «без мобильного номера» — сюда входят и городские номера (WhatsApp на них нет), и пустые/неверные
        var text = T($"В сегменте: {_plan.InSegment} · к рассылке: {due} · недоступны: {skipped} · без мобильного номера: {_plan.NoPhone}.",
            $"Сегментте: {_plan.InSegment} · жөнөтүүгө: {due} · жеткиликсиз: {skipped} · мобилдик номери жок: {_plan.NoPhone}.",
            $"In the segment: {_plan.InSegment} · to mail: {due} · unavailable: {skipped} · no mobile number: {_plan.NoPhone}.",
            $"Segmentte: {_plan.InSegment} · gönderilecek: {due} · kullanılamaz: {skipped} · cep numarası yok: {_plan.NoPhone}.",
            $"Segmentda: {_plan.InSegment} · yuboriladi: {due} · mavjud emas: {skipped} · mobil raqami yo'q: {_plan.NoPhone}.");
        if (!_snapshot.SalesKnown && _segment is ClientMailing.Segment.Inactive or ClientMailing.Segment.Vip)
            text += " " + T("Чеки не загрузились — этот сегмент пока пуст, нажмите «Обновить».", "Чектер жүктөлгөн жок — бул сегмент азырынча бош, «Жаңылоо» басыңыз.",
                "Receipts did not load — this segment is empty for now, press Refresh.", "Fişler yüklenmedi — bu segment şimdilik boş, Yenile'ye basın.", "Cheklar yuklanmadi — bu segment hozircha bo'sh, «Yangilash»ni bosing.");
        else if (_snapshot.SalesTruncated && _segment is ClientMailing.Segment.Inactive or ClientMailing.Segment.Vip)
            text += " " + T("Чеков за год очень много — часть не загружена, суммы могут быть занижены.", "Бир жылдагы чектер өтө көп — бир бөлүгү жүктөлгөн жок, суммалар аз болушу мүмкүн.",
                "There are very many receipts for the year — some did not load, sums may be understated.", "Yıllık fişler çok fazla — bir kısmı yüklenmedi, tutarlar eksik olabilir.", "Yillik cheklar juda ko'p — bir qismi yuklanmadi, summalar kam bo'lishi mumkin.");
        if (!_snapshot.DebtsKnown && _segment == ClientMailing.Segment.Debtors)
            text += " " + T("Долги не загрузились — нажмите «Обновить».", "Карыздар жүктөлгөн жок — «Жаңылоо» басыңыз.", "Debts did not load — press Refresh.", "Borçlar yüklenmedi — Yenile'ye basın.", "Qarzlar yuklanmadi — «Yangilash»ni bosing.");
        var today = ClientMailing.OpenedToday();
        var limit = ClientMailing.GetSettings().DailyLimit;
        text += " " + T($"Сегодня открыто чатов: {today} из {limit}.", $"Бүгүн ачылган чаттар: {today} / {limit}.", $"Chats opened today: {today} of {limit}.", $"Bugün açılan sohbet: {today} / {limit}.", $"Bugun ochilgan chatlar: {today} / {limit}.");
        SetStatus(text, "BrushTextSoft");
    }

    private void RenderList()
    {
        _list.Children.Clear();
        if (_snapshot is null)
            return;
        if (_plan.Items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("В этом сегменте сейчас никого нет.", "Бул сегментте азыр эч ким жок.", "Nobody in this segment right now.", "Bu segmentte şu an kimse yok.", "Bu segmentda hozir hech kim yo'q."),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 40, 16, 0),
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(empty);
        }
        var shown = 0;
        foreach (var r in _plan.Items)
        {
            if (!r.IsDue && shown >= MaxRowsShown)
                break;
            _list.Children.Add(BuildRow(r));
            shown++;
        }
        if (shown < _plan.Items.Count)
        {
            var more = new TextBlock
            {
                Text = T($"…и ещё {_plan.Items.Count - shown} недоступных клиентов (не показаны).", $"…жана дагы {_plan.Items.Count - shown} жеткиликсиз кардар (көрсөтүлгөн жок).",
                    $"…and {_plan.Items.Count - shown} more unavailable customers (not shown).", $"…ve {_plan.Items.Count - shown} kullanılamayan müşteri daha (gösterilmedi).",
                    $"…va yana {_plan.Items.Count - shown} mavjud bo'lmagan mijoz (ko'rsatilmadi)."),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            };
            Use(more, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(more);
        }
        UpdateTickAll();
    }

    private Control BuildRow(ClientMailing.Recipient r)
    {
        var c = r.Contact;
        var selected = _selected is not null && _selected.Contact.ClientId == c.ClientId;
        var available = r.IsDue && !_refused.Contains(c.ClientId);
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(row, Border.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
        Use(row, Border.BorderBrushProperty, selected ? "BrushAccent" : "BrushBorder");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var name = c.Name.Length > 0 ? c.Name : T("Клиент без имени", "Аты жок кардар", "Unnamed customer", "Adsız müşteri", "Nomsiz mijoz");

        // Цель нажатия не меньше 44 px (правило аудита дизайна; у голого CheckBox было 32).
        var check = new CheckBox { IsChecked = available && _checked.Contains(c.ClientId), IsEnabled = available, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), MinWidth = 44, MinHeight = 44 };
        AutomationProperties.SetName(check, T($"Написать: {name}", $"Жазуу: {name}", $"Write to: {name}", $"Yaz: {name}", $"Yozish: {name}"));
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true)
                _checked.Add(c.ClientId);
            else
                _checked.Remove(c.ClientId);
            _queue = null;
            UpdateButtons();
            UpdateTickAll();
        };
        grid.Children.Add(check);

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(nameText, TextBlock.ForegroundProperty, "BrushText");
        info.Children.Add(nameText);
        var parts = new List<string> { c.Phone };
        if (_segment == ClientMailing.Segment.Inactive && c.LastPurchase is { } lp)
            parts.Add(T($"последняя покупка {lp:dd.MM.yyyy}", $"акыркы сатып алуу {lp:dd.MM.yyyy}", $"last purchase {lp:dd.MM.yyyy}", $"son alışveriş {lp:dd.MM.yyyy}", $"oxirgi xarid {lp:dd.MM.yyyy}"));
        if (r.LastMailedAt is { } mailed)
            parts.Add(T($"писали {mailed:dd.MM}", $"жазылган {mailed:dd.MM}", $"messaged {mailed:dd.MM}", $"yazıldı {mailed:dd.MM}", $"yozilgan {mailed:dd.MM}"));
        if (_openedNow.Contains(c.ClientId))
            parts.Add(T("чат открыт", "чат ачылды", "chat opened", "sohbet açıldı", "chat ochildi"));
        var line = new TextBlock { Text = string.Join(" · ", parts), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
        info.Children.Add(line);
        if (!r.IsDue || _refused.Contains(c.ClientId))
        {
            var reason = new TextBlock
            {
                Text = _refused.Contains(c.ClientId)
                    ? T("клиент отказался от сообщений (по данным сервера)", "кардар билдирүүдөн баш тарткан (сервердин маалыматы боюнча)", "the customer declined messages (per the server)",
                        "müşteri mesajları reddetti (sunucu verisine göre)", "mijoz xabarlardan voz kechgan (server ma'lumoti bo'yicha)")
                    : ClientMailing.SkipText(r.Skip, ClientMailing.GetSettings().MinGapDays, ClientMailing.GetSettings().DailyLimit),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            };
            Use(reason, TextBlock.ForegroundProperty, "BrushWarning");
            info.Children.Add(reason);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var metric = MetricText(r);
        if (metric.Length > 0)
        {
            var m = new TextBlock { Text = metric, FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Use(m, TextBlock.ForegroundProperty, _segment == ClientMailing.Segment.Debtors ? "BrushDanger" : "BrushText");
            Grid.SetColumn(m, 2);
            grid.Children.Add(m);
        }
        row.Child = grid;
        AutomationProperties.SetName(row, name + (metric.Length > 0 ? " — " + metric : ""));
        row.PointerPressed += (_, _) => Select(r);
        return row;
    }

    private string MetricText(ClientMailing.Recipient r) => _segment switch
    {
        ClientMailing.Segment.Debtors => Money(r.Contact.Debt),
        ClientMailing.Segment.Inactive => T($"{r.DaysSincePurchase} дн.", $"{r.DaysSincePurchase} күн", $"{r.DaysSincePurchase} d", $"{r.DaysSincePurchase} gün", $"{r.DaysSincePurchase} kun"),
        ClientMailing.Segment.Vip => Money(r.Contact.Spent),
        ClientMailing.Segment.Birthday when r.Contact.Birthday is { } b => b.ToString("dd.MM", Ru),
        _ => "",
    };

    private void Select(ClientMailing.Recipient r)
    {
        _selected = r;
        RenderList();
        ShowPreview();
    }

    private void ToggleAll()
    {
        var due = _plan.Items.Where(r => r.IsDue && !_refused.Contains(r.Contact.ClientId)).ToList();
        if (due.Count > 0 && due.All(r => _checked.Contains(r.Contact.ClientId)))
            _checked.Clear();
        else
            foreach (var r in due)
                _checked.Add(r.Contact.ClientId);
        _queue = null;
        RenderList();
        UpdateButtons();
    }

    private void UpdateTickAll()
    {
        var due = _plan.Items.Where(r => r.IsDue && !_refused.Contains(r.Contact.ClientId)).ToList();
        var all = due.Count > 0 && due.All(r => _checked.Contains(r.Contact.ClientId));
        _tickAll.Content = all
            ? T("Снять все", "Баарын алып салуу", "Untick all", "Tümünü kaldır", "Hammasini olib tashlash")
            : T("Отметить всех", "Баарын белгилөө", "Tick all", "Tümünü işaretle", "Hammasini belgilash");
        AutomationProperties.SetName(_tickAll, (string)_tickAll.Content!);
        _tickAll.IsEnabled = due.Count > 0;
    }

    private List<ClientMailing.Recipient> Chosen() =>
        _plan.Items.Where(r => r.IsDue && !_refused.Contains(r.Contact.ClientId) && _checked.Contains(r.Contact.ClientId)).ToList();

    // ── Сегмент, параметры, язык, текст ─────────────────────────────────────────

    private void RenderSegmentChips()
    {
        _segmentChips.Children.Clear();
        foreach (var segment in ClientMailing.Segments)
        {
            var seg = segment;
            var chip = UiKit.Chip(this, ClientMailing.SegmentTitle(seg), seg == _segment);
            chip.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetName(chip, ClientMailing.SegmentTitle(seg));
            chip.Click += (_, _) => SelectSegment(seg);
            _segmentChips.Children.Add(chip);
        }
    }

    private void SelectSegment(ClientMailing.Segment segment)
    {
        if (segment == _segment)
            return;
        CommitTemplate();
        _segment = segment;
        _queue = null;
        _refused.Clear();
        _openedNow.Clear();
        RenderSegmentChips();
        ApplySegmentUi();
        LoadTemplateIntoBox();
        Replan(resetChecks: true);
    }

    /// <summary>Подпись и значение параметра выбранного сегмента (дней / сумма / ближайшие дни) и его описание.</summary>
    private void ApplySegmentUi()
    {
        var s = ClientMailing.GetSettings();
        _segmentHint.Text = ClientMailing.SegmentHint(_segment, s);
        _paramBox.IsVisible = _segment is ClientMailing.Segment.Inactive or ClientMailing.Segment.Vip or ClientMailing.Segment.Birthday;
        switch (_segment)
        {
            case ClientMailing.Segment.Inactive:
                _paramLabel.Text = T("Не покупали, дней (7–300)", "Сатып албаган, күн (7–300)", "Not bought for, days (7–300)", "Almayan, gün (7–300)", "Xarid qilmagan, kun (7–300)");
                _paramInput.Text = s.InactiveDays.ToString(CultureInfo.InvariantCulture);
                break;
            case ClientMailing.Segment.Vip:
                _paramLabel.Text = T("Покупки за год от, сом", "Бир жылдагы сатып алуу, сом", "Purchases over a year from, som", "Yıllık alışveriş en az, som", "Yillik xarid kamida, so'm");
                // 2026-11-27: только показ целого порога (формат «F0»), а не расчёт — чтобы check-money-in-views не краснел.
                _paramInput.Text = MoneyText.Weight(s.VipMinSpent, "F0");
                break;
            case ClientMailing.Segment.Birthday:
                _paramLabel.Text = T("Ближайшие дней (0–30)", "Алдыдагы күн (0–30)", "Next days (0–30)", "Önümüzdeki gün (0–30)", "Keyingi kunlar (0–30)");
                _paramInput.Text = s.BirthdayDaysAhead.ToString(CultureInfo.InvariantCulture);
                break;
        }
        AutomationProperties.SetName(_paramInput, _paramLabel.Text ?? "");
    }

    private void ApplyParam()
    {
        var text = (_paramInput.Text ?? "").Trim().Replace(" ", "");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            ApplySegmentUi();
            return;
        }
        var s = ClientMailing.GetSettings();
        switch (_segment)
        {
            case ClientMailing.Segment.Inactive: s.InactiveDays = (int)value; break;
            case ClientMailing.Segment.Vip: s.VipMinSpent = value; break;
            case ClientMailing.Segment.Birthday: s.BirthdayDaysAhead = (int)value; break;
            default: return;
        }
        ClientMailing.SaveSettings(s);
        ApplySegmentUi();
        _queue = null;
        Replan(resetChecks: true);
    }

    private void ApplySettings()
    {
        var s = ClientMailing.GetSettings();
        if (int.TryParse((_gapBox.Text ?? "").Trim(), out var gap))
            s.MinGapDays = gap;
        if (int.TryParse((_limitBox.Text ?? "").Trim(), out var limit))
            s.DailyLimit = limit;
        ClientMailing.SaveSettings(s);
        var saved = ClientMailing.GetSettings();
        _gapBox.Text = saved.MinGapDays.ToString(CultureInfo.InvariantCulture);
        _limitBox.Text = saved.DailyLimit.ToString(CultureInfo.InvariantCulture);
        _settingsNote.Text = T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi.");
        _queue = null;
        Replan(resetChecks: true);
    }

    private void RenderLangs()
    {
        _langs.Children.Clear();
        foreach (var l in ClientMailing.Languages)
        {
            var seg = UiKit.Seg(ClientMailing.LanguageTitle(l), l == _lang);
            var code = l;
            seg.Click += (_, _) =>
            {
                if (code == _lang)
                    return;
                CommitTemplate();
                _lang = code;
                var s = ClientMailing.GetSettings();
                s.Language = code;
                ClientMailing.SaveSettings(s);
                RenderLangs();
                LoadTemplateIntoBox();
                ShowPreview();
            };
            _langs.Children.Add(seg);
        }
    }

    private void LoadTemplateIntoBox()
    {
        _templateLoading = true;
        try
        {
            _templateBox.Text = ClientMailing.GetTemplate(_segment, _lang);
        }
        finally
        {
            _templateLoading = false;
        }
        UpdateTemplateCount();
    }

    private void CommitTemplate()
    {
        if (_templateLoading)
            return;
        ClientMailing.SaveTemplate(_segment, _lang, _templateBox.Text);
    }

    private void InsertToken(string token)
    {
        var text = _templateBox.Text ?? "";
        var at = Math.Clamp(_templateBox.CaretIndex, 0, text.Length);
        if (text.Length + token.Length > ClientMailing.MaxTextLength)
            return;
        _templateBox.Text = text.Insert(at, token);
        _templateBox.CaretIndex = at + token.Length;
        _templateBox.Focus();
    }

    private void UpdateTemplateCount()
    {
        var len = (_templateBox.Text ?? "").Length;
        _templateCount.Text = $"{len} / {ClientMailing.MaxTextLength}";
    }

    /// <summary>Текст, который уйдёт этому клиенту: шаблон выбранного сегмента и языка с данными только этого клиента.</summary>
    private string TextFor(ClientMailing.Contact c) =>
        ClientMailing.Render(_templateBox.Text ?? "", c, UserPreferences.Instance.StoreName);

    private void ShowPreview()
    {
        if (_selected is not { } r)
        {
            _previewTitle.Text = T("Предпросмотр текста", "Тексттин алдын ала көрүнүшү", "Message preview", "Mesaj önizlemesi", "Matnni oldindan ko'rish");
            _previewInfo.Text = T("Выберите клиента в списке — здесь будет текст, который он получит.", "Тизмеден кардарды тандаңыз — бул жерде ал ала турган текст чыгат.",
                "Pick a customer in the list — the text they will get appears here.", "Listeden bir müşteri seçin — alacağı metin burada görünür.", "Ro'yxatdan mijozni tanlang — u oladigan matn shu yerda chiqadi.");
            _previewText.Text = "";
            _previewBox.IsVisible = false;
            _clientTools.IsVisible = false;
            return;
        }
        var c = r.Contact;
        _previewTitle.Text = T($"Текст для: {c.Name}", $"Текст: {c.Name}", $"Message for: {c.Name}", $"Mesaj: {c.Name}", $"Matn: {c.Name}");
        _previewInfo.Text = c.Phone;
        _previewText.Text = TextFor(c);
        _previewBox.IsVisible = true;
        _clientTools.IsVisible = true;
        _birthdayBox.Text = c.Birthday is { } b ? b.ToString("dd.MM", CultureInfo.InvariantCulture) : "";
        var dnd = ClientMailing.DoNotDisturb().Contains(c.ClientId);
        _dndButton.Content = dnd
            ? T("Вернуть в рассылки", "Жөнөтмөлөргө кайтаруу", "Return to mailings", "Toplu mesajlara geri al", "Xabarnomalarga qaytarish")
            : T("Не беспокоить этого клиента", "Бул кардарды тынчсыздандырбоо", "Do not disturb this customer", "Bu müşteriyi rahatsız etme", "Bu mijozni bezovta qilmaslik");
        AutomationProperties.SetName(_dndButton, (string)_dndButton.Content!);
        _clientToolsNote.Text = T("«Не беспокоить» — клиент просил не писать: он больше не попадёт ни в одну рассылку.", "«Тынчсыздандырбоо» — кардар жазбоону суранган: ал эми эч бир жөнөтмөгө кирбейт.",
            "“Do not disturb” — the customer asked not to be written to: they will not be in any mailing.", "«Rahatsız etme» — müşteri yazılmasını istemedi: artık hiçbir toplu mesaja girmez.",
            "«Bezovta qilmaslik» — mijoz yozmaslikni so'ragan: u endi hech bir xabarnomaga kirmaydi.");
    }

    private void SaveBirthday()
    {
        if (_selected is not { } r)
            return;
        if (!ClientMailing.TryParseBirthday(_birthdayBox.Text, out var value))
        {
            _clientToolsNote.Text = T("Введите дату как день и месяц, например 15.03.", "Датаны күн жана ай катары жазыңыз, мисалы 15.03.", "Enter the date as day and month, e.g. 15.03.",
                "Tarihi gün ve ay olarak girin, örneğin 15.03.", "Sanani kun va oy sifatida kiriting, masalan 15.03.");
            return;
        }
        ClientMailing.SetBirthday(r.Contact.ClientId, value);
        _snapshot = _snapshot is null ? null : _snapshot with
        {
            Contacts = _snapshot.Contacts.Select(c => c.ClientId == r.Contact.ClientId ? c with { Birthday = value } : c).ToList(),
        };
        Replan(resetChecks: false);
        _clientToolsNote.Text = T("Запомнил.", "Эстеп калдым.", "Remembered.", "Hatırladım.", "Eslab qoldim.");
    }

    private void ToggleDnd()
    {
        if (_selected is not { } r)
            return;
        var on = !ClientMailing.DoNotDisturb().Contains(r.Contact.ClientId);
        ClientMailing.SetDoNotDisturb(r.Contact.ClientId, r.Contact.Name, on);
        _checked.Remove(r.Contact.ClientId);
        _queue = null;
        Replan(resetChecks: false);
    }

    // ── Отправка по очереди ─────────────────────────────────────────────────────

    private void UpdateButtons()
    {
        var quiet = IsQuiet();
        _quietNote.IsVisible = quiet;
        _quietNote.Text = quiet
            ? DebtReminderPlanner.QuietHoursText() + " " + T("Список можно готовить, чаты — утром.", "Тизмени даярдаса болот, чаттар — эртең менен.", "You can prepare the list; chats — in the morning.",
                "Listeyi hazırlayabilirsiniz; sohbetler — sabah.", "Ro'yxatni tayyorlash mumkin, chatlar — ertalab.")
            : "";
        var chosen = Chosen();
        if (_queue is { } queue)
        {
            _openNext.Content = _next < queue.Count
                ? T($"Следующий ▶ {queue[_next].Contact.Name} ({_next + 1} из {queue.Count})", $"Кийинки ▶ {queue[_next].Contact.Name} ({queue.Count} ичинен {_next + 1})",
                    $"Next ▶ {queue[_next].Contact.Name} ({_next + 1} of {queue.Count})", $"Sonraki ▶ {queue[_next].Contact.Name} ({_next + 1}/{queue.Count})", $"Keyingi ▶ {queue[_next].Contact.Name} ({_next + 1}/{queue.Count})")
                : T("Все чаты открыты", "Бардык чаттар ачылды", "All chats opened", "Tüm sohbetler açıldı", "Barcha chatlar ochildi");
            _openNext.IsEnabled = !quiet && !_busy && _next < queue.Count;
            _skipNext.IsVisible = true;
            _skipNext.IsEnabled = !_busy && _next < queue.Count;
        }
        else
        {
            _openNext.Content = T($"Открыть WhatsApp по очереди ({chosen.Count})", $"WhatsApp'ты кезек менен ачуу ({chosen.Count})", $"Open WhatsApp one by one ({chosen.Count})",
                $"WhatsApp'ı sırayla aç ({chosen.Count})", $"WhatsApp'ni navbat bilan ochish ({chosen.Count})");
            _openNext.IsEnabled = !quiet && !_busy && chosen.Count > 0 && _loadedOnce;
            _skipNext.IsVisible = false;
        }
        AutomationProperties.SetName(_openNext, (string)_openNext.Content!);
    }

    private async void OnOpenNext()
    {
        try
        {
            if (_busy || IsQuiet())
            {
                UpdateButtons();
                return;
            }
            CommitTemplate();
            if (_queue is null)
            {
                var chosen = Chosen();
                if (chosen.Count == 0)
                    return;
                _busy = true;
                UpdateButtons();
                var answer = ConfirmStartForTests is { } confirmHook
                    ? await confirmHook(chosen.Count).ConfigureAwait(true) ? System.Windows.MessageBoxResult.Yes : System.Windows.MessageBoxResult.No
                    : await PosMessageBox.ShowModalAsync(this,
                    T($"Открывать чаты WhatsApp по очереди для {chosen.Count} клиентов (сегмент «{ClientMailing.SegmentTitle(_segment)}»)? Каждое нажатие откроет один чат с готовым текстом; «Отправить» нажимаете вы сами. Программа ничего не отправляет.",
                        $"{chosen.Count} кардар үчүн WhatsApp чаттарын кезек менен ачасызбы («{ClientMailing.SegmentTitle(_segment)}» сегменти)? Ар бир басуу даяр текст менен бир чатты ачат; «Жөнөтүү» баскычын өзүңүз басасыз. Программа эч нерсе жөнөтпөйт.",
                        $"Open WhatsApp chats one by one for {chosen.Count} customers (segment “{ClientMailing.SegmentTitle(_segment)}”)? Each press opens one chat with the text ready; you press Send yourself. The program sends nothing.",
                        $"{chosen.Count} müşteri için WhatsApp sohbetleri sırayla açılsın mı («{ClientMailing.SegmentTitle(_segment)}» segmenti)? Her basış hazır metinle bir sohbet açar; Gönder'e siz basarsınız. Program hiçbir şey göndermez.",
                        $"{chosen.Count} mijoz uchun WhatsApp chatlari navbat bilan ochilsinmi («{ClientMailing.SegmentTitle(_segment)}» segmenti)? Har bosish tayyor matn bilan bitta chatni ochadi; «Yuborish»ni o'zingiz bosasiz. Dastur hech narsa yubormaydi."),
                    T("Рассылка", "Жөнөтмө", "Mailing", "Toplu mesaj", "Xabarnoma"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
                    confirmText: T("Начать", "Баштоо", "Start", "Başla", "Boshlash")).ConfigureAwait(true);
                _busy = false;
                if (answer != System.Windows.MessageBoxResult.Yes)
                {
                    UpdateButtons();
                    return;
                }
                _queue = chosen;
                _next = 0;
            }
            await OpenNextChatAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _busy = false;
            PosLogger.Log($"Рассылка: чат WhatsApp не открыт ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(T("Не получилось открыть WhatsApp. Попробуйте ещё раз.", "WhatsApp ачылган жок. Дагы аракет кылыңыз.", "Could not open WhatsApp. Please try again.",
                "WhatsApp açılamadı. Tekrar deneyin.", "WhatsApp ochilmadi. Yana urinib ko'ring."), "BrushDanger");
            UpdateButtons();
        }
    }

    /// <summary>Открыть чат со следующим клиентом очереди. Перед каждым чатом заново проверяется «не беспокоить», недавняя переписка, дневной лимит
    /// и согласие клиента на сервере (нет связи — без проверки согласия, остальное проверяется).</summary>
    private async Task OpenNextChatAsync()
    {
        if (_queue is not { } queue)
            return;
        _busy = true;
        UpdateButtons();
        try
        {
            var s = ClientMailing.GetSettings();
            var skippedNow = 0;
            while (_next < queue.Count)
            {
                var r = queue[_next++];
                var c = r.Contact;
                if (ClientMailing.DoNotDisturb().Contains(c.ClientId)
                    || ClientMailing.LastMailed().TryGetValue(c.ClientId, out var last) && (DateTime.Now - last).TotalDays < s.MinGapDays)
                {
                    skippedNow++;
                    continue;
                }
                if (ClientMailing.OpenedToday() >= s.DailyLimit)
                {
                    _next = queue.Count;
                    SetStatus(T($"Дневной лимит {s.DailyLimit} чатов достигнут — продолжим завтра. Так мы не нарушаем правила WhatsApp.", $"Күндүк чек {s.DailyLimit} чатка жетти — эртең улантабыз. Ошентип WhatsApp эрежелерин бузбайбыз.",
                        $"The daily limit of {s.DailyLimit} chats is reached — we continue tomorrow. This keeps us within WhatsApp rules.", $"Günlük {s.DailyLimit} sohbet sınırına ulaşıldı — yarın devam. Böylece WhatsApp kurallarını çiğnemeyiz.",
                        $"Kunlik {s.DailyLimit} chat chegarasiga yetildi — ertaga davom etamiz. Shunda WhatsApp qoidalarini buzmaymiz."), "BrushWarning");
                    break;
                }
                if (await IsRefusedByServerAsync(c).ConfigureAwait(true))
                {
                    _refused.Add(c.ClientId);
                    _checked.Remove(c.ClientId);
                    skippedNow++;
                    continue;
                }
                var text = TextFor(c);
                var url = DebtWhatsAppLink.BuildUrl(c.Phone, text);
                if (url is null || ClientMailing.UrlTooLong(url) || !(OpenUrlForTests?.Invoke(url) ?? PlatformLauncher.OpenUrl(url)))
                {
                    SetStatus(url is not null && ClientMailing.UrlTooLong(url)
                        ? T("Текст слишком длинный для ссылки WhatsApp — сократите его.", "Текст WhatsApp шилтемеси үчүн өтө узун — кыскартыңыз.", "The text is too long for a WhatsApp link — shorten it.",
                            "Metin WhatsApp bağlantısı için çok uzun — kısaltın.", "Matn WhatsApp havolasi uchun juda uzun — qisqartiring.")
                        : T("Не удалось открыть WhatsApp.", "WhatsApp'ты ачуу мүмкүн болгон жок.", "Could not open WhatsApp.", "WhatsApp açılamadı.", "WhatsApp'ni ochib bo'lmadi."), "BrushDanger");
                    break;
                }
                ClientMailing.LogOpened(c, _segment, _lang);
                _openedNow.Add(c.ClientId);
                SetStatus(T($"Открыт чат с {c.Name} — проверьте текст и нажмите «Отправить» в WhatsApp." + (skippedNow > 0 ? $" Пропущено по пути: {skippedNow}." : ""),
                    $"{c.Name} менен чат ачылды — текстти текшерип, WhatsApp'та «Жөнөтүү» басыңыз." + (skippedNow > 0 ? $" Жолдо өткөрүлдү: {skippedNow}." : ""),
                    $"Chat with {c.Name} is open — check the text and press Send in WhatsApp." + (skippedNow > 0 ? $" Skipped on the way: {skippedNow}." : ""),
                    $"{c.Name} ile sohbet açıldı — metni kontrol edip WhatsApp'ta Gönder'e basın." + (skippedNow > 0 ? $" Yolda atlanan: {skippedNow}." : ""),
                    $"{c.Name} bilan chat ochildi — matnni tekshirib, WhatsApp'da «Yuborish»ni bosing." + (skippedNow > 0 ? $" Yo'lda o'tkazildi: {skippedNow}." : "")), "BrushSuccess");
                break;
            }
            if (_next >= queue.Count)
                RenderStatusAfterQueue(skippedNow);
        }
        finally
        {
            _busy = false;
            RenderList();
            UpdateButtons();
            RenderJournal();
        }
    }

    private void RenderStatusAfterQueue(int skipped)
    {
        if (_refused.Count > 0 || skipped > 0)
            SetStatus(T($"Очередь закончилась. Пропущено: {skipped} (отказались, недавно писали или лимит).", $"Кезек бүттү. Өткөрүлдү: {skipped} (баш тарткан, жакында жазылган же чек).",
                $"The queue is finished. Skipped: {skipped} (declined, messaged recently or the limit).", $"Sıra bitti. Atlanan: {skipped} (reddetti, yakında yazıldı veya sınır).",
                $"Navbat tugadi. O'tkazildi: {skipped} (voz kechgan, yaqinda yozilgan yoki chegara)."), "BrushTextSoft");
    }

    private void OnSkip()
    {
        if (_queue is not { } queue || _next >= queue.Count)
            return;
        _next++;
        UpdateButtons();
    }

    /// <summary>Согласие клиента на сообщения по данным сервера (один запрос на клиента, 3 с). Нет связи, права или метода — не отказ.</summary>
    private async Task<bool> IsRefusedByServerAsync(ClientMailing.Contact c)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var consents = await App.GetRequiredService<ClientMessagingApi>().GetConsentsAsync(c.ClientId, cts.Token).ConfigureAwait(true);
            return ClientMailing.IsRefused(_segment, consents.Service, consents.Marketing, consents.UpdatedAt is not null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Рассылка: согласия клиента не получены ({ex.GetType().Name}) — без проверки согласия.", "INFO");
            return false;
        }
    }

    // ── Вкладки: журнал и «не беспокоить» ───────────────────────────────────────

    private void ShowTab(int index)
    {
        UiKit.SetActive(_tabRecipients, index == 0);
        UiKit.SetActive(_tabJournal, index == 1);
        UiKit.SetActive(_tabDnd, index == 2);
        _recipientsView.IsVisible = index == 0;
        _segmentChips.IsVisible = index == 0;
        _actions.IsVisible = index == 0;
        _journalView.IsVisible = index == 1;
        _dndView.IsVisible = index == 2;
        if (index == 1)
            RenderJournal();
        else if (index == 2)
            RenderDnd();
    }

    private void RenderJournal()
    {
        if (!_journalView.IsVisible)
            return;
        _journal.Children.Clear();
        var entries = ClientMailing.Journal(300);
        if (entries.Count == 0)
        {
            _journal.Children.Add(EmptyNote(T("Журнал пуст: рассылок ещё не было.", "Журнал бош: жөнөтмөлөр али болгон эмес.", "The log is empty: no mailings yet.", "Kayıt boş: henüz toplu mesaj yok.", "Jurnal bo'sh: hali xabarnomalar bo'lmagan.")));
            return;
        }
        foreach (var e in entries)
        {
            var entry = e;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 2) };
            var at = new TextBlock { Text = e.At.ToString("dd.MM.yyyy HH:mm", Ru), FontSize = 13, Width = 130, VerticalAlignment = VerticalAlignment.Center };
            Use(at, TextBlock.ForegroundProperty, "BrushTextSoft");
            row.Children.Add(at);
            var who = new TextBlock
            {
                Text = e.Name + (e.Phone.Length > 0 ? " · " + e.Phone : "") + " · " + ClientMailing.SegmentTitleByKey(e.Segment) + " · " + T("чат открыт", "чат ачылды", "chat opened", "sohbet açıldı", "chat ochildi"),
                FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            Use(who, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(who, 1);
            row.Children.Add(who);
            var undo = Small(UiKit.Ghost(this, T("Снять отметку", "Белгини алуу", "Undo mark", "İşareti kaldır", "Belgini olib tashlash")));
            undo.Margin = new Thickness(10, 0, 0, 0);
            ToolTip.SetTip(undo, T("Чат открывали, но не отправили: клиент снова станет доступен для рассылки.", "Чатты ачкан, бирок жөнөтпөгөн: кардар кайра жеткиликтүү болот.",
                "The chat was opened but nothing was sent: the customer becomes available again.", "Sohbet açıldı ama gönderilmedi: müşteri yeniden kullanılabilir olur.", "Chat ochilgan, lekin yuborilmagan: mijoz yana mavjud bo'ladi."));
            AutomationProperties.SetName(undo, (string)undo.Content! + ": " + e.Name);
            undo.Click += (_, _) =>
            {
                ClientMailing.RemoveLog(entry);
                RenderJournal();
                Replan(resetChecks: false);
            };
            Grid.SetColumn(undo, 2);
            row.Children.Add(undo);
            _journal.Children.Add(row);
        }
    }

    private void RenderDnd()
    {
        _dndList.Children.Clear();
        var names = ClientMailing.DoNotDisturbNames();
        if (names.Count == 0)
        {
            _dndList.Children.Add(EmptyNote(T("Список пуст: никто не просил не беспокоить.", "Тизме бош: эч ким тынчсыздандырбоону суранган эмес.", "The list is empty: nobody asked not to be disturbed.",
                "Liste boş: kimse rahatsız edilmek istemedi.", "Ro'yxat bo'sh: hech kim bezovta qilmaslikni so'ramagan.")));
            return;
        }
        foreach (var (id, name) in names.OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var clientId = id;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            var title = new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Use(title, TextBlock.ForegroundProperty, "BrushText");
            row.Children.Add(title);
            var back = Small(UiKit.Ghost(this, T("Вернуть в рассылки", "Жөнөтмөлөргө кайтаруу", "Return to mailings", "Toplu mesajlara geri al", "Xabarnomalarga qaytarish")));
            AutomationProperties.SetName(back, (string)back.Content! + ": " + name);
            back.Click += (_, _) =>
            {
                ClientMailing.SetDoNotDisturb(clientId, name, false);
                RenderDnd();
                Replan(resetChecks: false);
            };
            Grid.SetColumn(back, 1);
            row.Children.Add(back);
            _dndList.Children.Add(row);
        }
    }

    private TextBlock EmptyNote(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 40, 16, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    // ── Помощники ───────────────────────────────────────────────────────────────

    private Border Card(Control child)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = child };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    private Control Field(string label, TextBox box)
    {
        var l = UiKit.Label(this, label);
        l.TextWrapping = TextWrapping.Wrap;
        l.MaxWidth = 220;
        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 16, 6), Children = { l, box } };
    }

    private TextBox NumberBox(string value)
    {
        var box = UiKit.Input(this, "", 38);
        box.Width = 110;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.MaxLength = 9;
        box.Text = value;
        return box;
    }

    private static Button Small(Button b)
    {
        b.Height = 44;
        b.FontSize = 13.5;
        return b;
    }

    private static void AddRow(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private void SetStatus(string text, string brush)
    {
        _status.Text = text;
        Use(_status, TextBlock.ForegroundProperty, brush);
    }

    private static string Money(double v) => DebtWhatsAppLink.FormatAmount(v) + " " + T("сом", "сом", "som", "som", "so'm");

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
