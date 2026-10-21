using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, И9 «Напоминания должникам по расписанию». Окно открывается из Нур Советника (<see cref="Open"/>):
/// вверху — настройки расписания (свёрнуты), список должников с просрочкой и галочками (имя, сумма, просрочка, телефон, когда
/// напоминали; пропущенные — с причиной), предпросмотр текста выбранного клиента и выбор языка (ru / ky / uz), «Открыть WhatsApp
/// по очереди» — только открывает чат с готовым текстом (отправляет владелец сам, в журнале «открыт чат»), «Отправить в Telegram» —
/// только подписанным на бота, после подтверждения, не чаще раза в 3–7 дней одному клиенту. Вкладка «Журнал» — кому, когда,
/// каким способом. В тихие часы (21:00–09:00) кнопки отправки выключены. Данные и правила — <see cref="DebtReminderPlanner"/>.</summary>
public sealed class DebtReminderWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static DebtReminderWindow? _openWindow;

    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _quietNote = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _journal = new() { Spacing = 4 };
    private readonly TextBlock _previewTitle = new() { FontSize = 14.5, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewInfo = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _previewText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _previewLangs = new() { Orientation = Orientation.Horizontal };
    private readonly Border _previewTextBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1) };
    private readonly Border _settingsPanel = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10), IsVisible = false };
    private readonly Grid _candidatesView = new() { ColumnDefinitions = new ColumnDefinitions("3*,2*") };
    private readonly Border _journalView = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1), IsVisible = false };
    private readonly Button _whatsAppButton;
    private readonly Button _telegramButton;
    private readonly Button _excludeButton;
    private readonly Button _refreshButton;
    private readonly Button _settingsToggle;
    private readonly Button _tabCandidates;
    private readonly Button _tabJournal;
    private readonly Button _checkAll;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };

    // Настройки (поля формы).
    private readonly CheckBox _enabledBox = new();
    private readonly TextBox _everyBox, _minDaysBox, _perClientBox;
    // 2026-10-21: тихие часы — меняются здесь же (раньше только показывались) (пакет 7, хвост Советника И9).
    private readonly TextBox _quietFromBox, _quietToBox;
    private readonly TextBlock _quietText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _templateRu, _templateKy, _templateUz;
    private readonly StackPanel _defaultLangs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _settingsStatus = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private string _defaultLang = "ru";

    private List<DebtReminderPlanner.Candidate> _candidates = new();
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private DebtReminderPlanner.Candidate? _selected;
    private List<DebtReminderPlanner.Candidate>? _waQueue;
    private int _waNext;
    private Window? _whatsAppWindow;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;
    private int _loadGen;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Открыть окно напоминаний (одно на программу: повторный вызов поднимает уже открытое).</summary>
    public static void Open(Window owner)
    {
        if (_openWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var window = new DebtReminderWindow();
        _openWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openWindow, window))
                _openWindow = null;
        };
        if (owner is { IsVisible: true })
            window.Show(owner);
        else
            window.Show();
    }

    public DebtReminderWindow()
    {
        Title = T("Напоминания должникам", "Карыздарга эскертүү", "Debt reminders", "Borç hatırlatmaları", "Qarzdorlarga eslatmalar");
        Width = 1000;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var settings = DebtReminderPlanner.GetSettings();

        // ── Шапка ──
        var title = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = T("Программа сама ничего не отправляет: отметьте, кому напомнить. WhatsApp откроет чат с готовым текстом — «Отправить» нажимаете вы. В Telegram — только подписанным на бота и после подтверждения.",
                "Программа өзү эч нерсе жөнөтпөйт: кимге эскертүүнү белгилеңиз. WhatsApp даяр текст менен чатты ачат — «Жөнөтүү» баскычын өзүңүз басасыз. Telegram'га — ботко жазылгандарга гана жана ырастагандан кийин.",
                "The program never sends anything by itself: tick who to remind. WhatsApp opens a chat with the text ready — you press Send. Telegram — only to bot subscribers and after you confirm.",
                "Program kendiliğinden hiçbir şey göndermez: kime hatırlatılacağını işaretleyin. WhatsApp hazır metinle sohbeti açar — Gönder'e siz basarsınız. Telegram — yalnızca bota abone olanlara ve onayınızdan sonra.",
                "Dastur o'zi hech narsa yubormaydi: kimga eslatishni belgilang. WhatsApp tayyor matn bilan chatni ochadi — «Yuborish»ni o'zingiz bosasiz. Telegram — faqat botga obuna bo'lganlarga va tasdiqlagandan keyin."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        // ── Вкладки и кнопки шапки ──
        _tabCandidates = UiKit.Seg(T("Кому напомнить", "Кимге эскертүү", "Who to remind", "Kime hatırlatılacak", "Kimga eslatish"), true);
        _tabJournal = UiKit.Seg(T("Журнал", "Журнал", "Log", "Kayıt", "Jurnal"), false);
        _tabCandidates.Click += (_, _) => ShowTab(journal: false);
        _tabJournal.Click += (_, _) => ShowTab(journal: true);
        var tabs = UiKit.Segment(new StackPanel { Orientation = Orientation.Horizontal, Children = { _tabCandidates, _tabJournal } });
        tabs.VerticalAlignment = VerticalAlignment.Center;

        _settingsToggle = Small(UiKit.Ghost(this, ""));
        _settingsToggle.Click += (_, _) =>
        {
            _settingsPanel.IsVisible = !_settingsPanel.IsVisible;
            UpdateSettingsToggle();
        };
        UpdateSettingsToggle();
        _refreshButton = Small(UiKit.Ghost(this, T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash")));
        SetUiaName(_refreshButton, (string)_refreshButton.Content!);
        _refreshButton.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);

        var topBar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        topBar.Children.Add(tabs);
        Grid.SetColumn(_settingsToggle, 2);
        _settingsToggle.Margin = new Thickness(8, 0, 0, 0);
        topBar.Children.Add(_settingsToggle);
        Grid.SetColumn(_refreshButton, 3);
        _refreshButton.Margin = new Thickness(8, 0, 0, 0);
        topBar.Children.Add(_refreshButton);

        // ── Настройки расписания (свёрнуты) ──
        _everyBox = NumberBox(settings.EveryDays);
        _minDaysBox = NumberBox(settings.MinOverdueDays);
        _perClientBox = NumberBox(settings.PerClientDays);
        _quietFromBox = NumberBox(settings.QuietFromHour);
        _quietToBox = NumberBox(settings.QuietToHour);
        _templateRu = TemplateBox(settings.TemplateRu);
        _templateKy = TemplateBox(settings.TemplateKy);
        _templateUz = TemplateBox(settings.TemplateUz);
        _defaultLang = settings.Language;
        BuildSettingsPanel(settings);

        // ── Список кандидатов и предпросмотр ──
        _checkAll = Small(UiKit.Ghost(this, T("Отметить всех", "Баарын белгилөө", "Tick all", "Tümünü işaretle", "Hammasini belgilash")));
        SetUiaName(_checkAll, (string)_checkAll.Content!);
        _checkAll.Click += (_, _) => ToggleAll();
        var listHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var listTitle = new TextBlock { Text = T("Должники с просрочкой", "Мөөнөтү өткөн карыздар", "Overdue debtors", "Gecikmiş borçlular", "Muddati o'tgan qarzdorlar"),
            FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(listTitle, TextBlock.ForegroundProperty, "BrushText");
        listHeader.Children.Add(listTitle);
        Grid.SetColumn(_checkAll, 1);
        listHeader.Children.Add(_checkAll);
        var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listPanel.Children.Add(listHeader);
        var listScroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(listScroll, 1);
        listPanel.Children.Add(listScroll);
        var listCard = Card(listPanel);
        _candidatesView.Children.Add(listCard);

        Use(_previewTitle, TextBlock.ForegroundProperty, "BrushText");
        Use(_previewInfo, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_previewText, TextBlock.ForegroundProperty, "BrushText");
        Use(_previewTextBox, Border.BackgroundProperty, "BrushWindowBackdrop");
        Use(_previewTextBox, Border.BorderBrushProperty, "BrushBorder");
        _previewTextBox.Child = _previewText;
        _excludeButton = Small(UiKit.Ghost(this, ""));
        _excludeButton.HorizontalAlignment = HorizontalAlignment.Left;
        _excludeButton.Click += (_, _) => ToggleExcludeSelected();
        var langLabel = UiKit.Label(this, T("Язык сообщения", "Билдирүүнүн тили", "Message language", "Mesaj dili", "Xabar tili"));
        var previewStack = new StackPanel { Spacing = 8, Children = { _previewTitle, _previewInfo, langLabel, UiKit.Segment(_previewLangs), _previewTextBox, _excludeButton } };
        var previewCard = Card(new ScrollViewer { Content = previewStack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        previewCard.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(previewCard, 1);
        _candidatesView.Children.Add(previewCard);

        // ── Журнал ──
        Use(_journalView, Border.BackgroundProperty, "BrushPanel");
        Use(_journalView, Border.BorderBrushProperty, "BrushBorder");
        _journalView.Child = new ScrollViewer { Content = _journal, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        // ── Кнопки отправки ──
        _whatsAppButton = UiKit.Primary(this, "");
        _whatsAppButton.Click += (_, _) => OnWhatsApp();
        _telegramButton = UiKit.Ghost(this, "");
        _telegramButton.Click += async (_, _) => await OnTelegramAsync().ConfigureAwait(true);
        var later = UiKit.Ghost(this, T("Напомнить завтра", "Эртең эскертүү", "Remind me tomorrow", "Yarın hatırlat", "Ertaga eslatish"));
        SetUiaName(later, (string)later.Content!);
        ToolTip.SetTip(later, T("Карточка «Пора напомнить» в Советнике не появится до завтра 10:00.", "Кеңешчидеги «Эскертүү убагы» карточкасы эртең 10:00гө чейин чыкпайт.",
            "The “Time to remind” card in the Advisor won’t show until tomorrow 10:00.", "Danışmandaki «Hatırlatma zamanı» kartı yarın 10:00'a kadar görünmez.",
            "Maslahatchidagi «Eslatish vaqti» kartasi ertaga 10:00 gacha chiqmaydi."));
        later.Click += (_, _) =>
        {
            DebtReminderPlanner.Postpone();
            SetStatus(T("Хорошо, напомню завтра.", "Макул, эртең эскертем.", "OK, I’ll remind you tomorrow.", "Tamam, yarın hatırlatırım.", "Yaxshi, ertaga eslataman."), "BrushTextSoft");
        };
        var actionsWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _whatsAppButton, _telegramButton, later })
        {
            b.Margin = new Thickness(0, 0, 10, 6);
            actionsWrap.Children.Add(b);
        }

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_quietNote, TextBlock.ForegroundProperty, "BrushWarning");

        var root = new Grid { Margin = new Thickness(20, 16, 20, 16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto") };
        AddRow(root, title, 0);
        AddRow(root, hint, 1);
        AddRow(root, topBar, 2);
        var settingsScroll = new ScrollViewer { Content = _settingsPanel, MaxHeight = 380, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        settingsScroll.Bind(IsVisibleProperty, _settingsPanel.GetObservable(IsVisibleProperty));
        AddRow(root, settingsScroll, 3);
        AddRow(root, _quietNote, 4);
        AddRow(root, _status, 5);
        AddRow(root, _candidatesView, 6);
        AddRow(root, _journalView, 6);
        AddRow(root, actionsWrap, 7);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Closed += (_, _) =>
        {
            _cts.Cancel();
        };
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);

        ShowPreview(null);
        UpdateButtons();
    }

    // ── Загрузка ────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        var gen = ++_loadGen;
        _refreshButton.IsEnabled = false;
        SetStatus(T("Загружаю должников…", "Карыздарды жүктөп жатам…", "Loading debtors…", "Borçlular yükleniyor…", "Qarzdorlar yuklanmoqda…"), "BrushTextSoft");
        try
        {
            var list = await DebtReminderPlanner.CandidatesAsync(_cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            var settings = DebtReminderPlanner.GetSettings();
            _candidates = list.Where(c => c.Skip != DebtReminderPlanner.SkipReason.NotOverdue).ToList();
            // Отмечены сразу: к напоминанию и (до первого подтверждения — все; потом — уже подтверждённые владельцем).
            _checked.Clear();
            foreach (var c in _candidates.Where(c => c.IsDue && (!settings.ListConfirmed || c.Confirmed)))
                _checked.Add(c.ClientId);
            _waQueue = null;
            _selected = _candidates.FirstOrDefault(c => _selected is not null && c.ClientId == _selected.ClientId) ?? _candidates.FirstOrDefault();
            RenderList();
            ShowPreview(_selected);
            var due = _candidates.Count(c => c.IsDue);
            var skipped = _candidates.Count - due;
            var notOverdue = list.Count(c => c.Skip == DebtReminderPlanner.SkipReason.NotOverdue);
            var text = T($"К напоминанию: {due} · пропущены: {skipped} · ещё не просрочены (меньше {settings.MinOverdueDays} дн.): {notOverdue}.",
                $"Эскертүүгө: {due} · өткөрүлдү: {skipped} · мөөнөтү өтө элек ({settings.MinOverdueDays} күндөн аз): {notOverdue}.",
                $"To remind: {due} · skipped: {skipped} · not overdue yet (under {settings.MinOverdueDays} days): {notOverdue}.",
                $"Hatırlatılacak: {due} · atlanan: {skipped} · henüz gecikmemiş ({settings.MinOverdueDays} günden az): {notOverdue}.",
                $"Eslatishga: {due} · o'tkazib yuborildi: {skipped} · hali muddati o'tmagan ({settings.MinOverdueDays} kundan kam): {notOverdue}.");
            if (list.Count > 0 && !list[0].Exact)
                text += " " + T("Суммы предварительные (≈) — точный расчёт долгов ещё идёт, нажмите «Обновить» через минуту.",
                    "Суммалар болжолдуу (≈) — карыздардын так эсеби жүрүп жатат, бир мүнөттөн кийин «Жаңылоо» басыңыз.",
                    "Amounts are preliminary (≈) — the exact debt calculation is still running, press Refresh in a minute.",
                    "Tutarlar ön hesap (≈) — kesin borç hesabı sürüyor, bir dakika sonra Yenile'ye basın.",
                    "Summalar taxminiy (≈) — qarzlarning aniq hisobi davom etmoqda, bir daqiqadan keyin «Yangilash»ni bosing.");
            if (settings.ListConfirmed && _candidates.Any(c => c.IsDue && !c.Confirmed))
                text += " " + T("Новых в списке не отметил — проверьте и отметьте сами.", "Тизмедеги жаңыларды белгилеген жокмун — текшерип, өзүңүз белгилеңиз.",
                    "New people in the list are not ticked — check and tick them yourself.", "Listedeki yenileri işaretlemedim — kontrol edip kendiniz işaretleyin.",
                    "Ro'yxatdagi yangilarni belgilamadim — tekshirib, o'zingiz belgilang.");
            SetStatus(text, "BrushTextSoft");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            PosLogger.Log($"Напоминания должникам: список не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(T("Нет связи с сервером NurCRM — список должников не загружен. Проверьте интернет и нажмите «Обновить».",
                "NurCRM сервери менен байланыш жок — карыздардын тизмеси жүктөлгөн жок. Интернетти текшерип, «Жаңылоо» басыңыз.",
                "No connection to the NurCRM server — the debtor list did not load. Check the internet and press Refresh.",
                "NurCRM sunucusuna bağlantı yok — borçlu listesi yüklenmedi. İnterneti kontrol edip Yenile'ye basın.",
                "NurCRM serveri bilan aloqa yo'q — qarzdorlar ro'yxati yuklanmadi. Internetni tekshirib, «Yangilash»ni bosing."), "BrushDanger");
        }
        finally
        {
            if (gen == _loadGen)
            {
                _refreshButton.IsEnabled = true;
                UpdateButtons();
                RenderJournal();
            }
        }
    }

    // ── Список ──────────────────────────────────────────────────────────────────

    private void RenderList()
    {
        _list.Children.Clear();
        if (_candidates.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Напоминать некому: должников с просрочкой нет.", "Эскертүүгө эч ким жок: мөөнөтү өткөн карыздар жок.", "Nobody to remind: no overdue debtors.",
                    "Hatırlatılacak kimse yok: gecikmiş borçlu yok.", "Eslatishga hech kim yo'q: muddati o'tgan qarzdorlar yo'q."),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 40, 16, 0),
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(empty);
        }
        foreach (var c in _candidates)
            _list.Children.Add(BuildRow(c));
        var dueCount = _candidates.Count(c => c.IsDue);
        var allChecked = dueCount > 0 && _candidates.Where(c => c.IsDue).All(c => _checked.Contains(c.ClientId));
        _checkAll.Content = allChecked
            ? T("Снять все", "Баарын алып салуу", "Untick all", "Tümünü kaldır", "Hammasini olib tashlash")
            : T("Отметить всех", "Баарын белгилөө", "Tick all", "Tümünü işaretle", "Hammasini belgilash");
        SetUiaName(_checkAll, (string)_checkAll.Content!);
        _checkAll.IsEnabled = dueCount > 0;
    }

    private Control BuildRow(DebtReminderPlanner.Candidate c)
    {
        var selected = _selected is not null && _selected.ClientId == c.ClientId;
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(row, Border.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
        Use(row, Border.BorderBrushProperty, selected ? "BrushAccent" : "BrushBorder");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var check = new CheckBox { IsChecked = c.IsDue && _checked.Contains(c.ClientId), IsEnabled = c.IsDue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(check, T($"Напомнить: {c.Name}", $"Эскертүү: {c.Name}", $"Remind: {c.Name}", $"Hatırlat: {c.Name}", $"Eslatish: {c.Name}"));
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true)
                _checked.Add(c.ClientId);
            else
                _checked.Remove(c.ClientId);
            _waQueue = null;
            UpdateButtons();
        };
        grid.Children.Add(check);

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = c.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        info.Children.Add(name);
        var parts = new List<string>
        {
            c.Phone.Length > 0 ? c.Phone : T("нет телефона", "телефон жок", "no phone", "telefon yok", "telefon yo'q"),
            T($"просрочка {c.OverdueDays} дн.", $"мөөнөтү {c.OverdueDays} күн өттү", $"overdue {c.OverdueDays} days", $"{c.OverdueDays} gün gecikme", $"{c.OverdueDays} kun kechikdi"),
        };
        if (c.HasTelegram)
            parts.Add("Telegram");
        parts.Add(c.LastRemindedAt is { } at
            ? T($"напоминали {at:dd.MM} ({ChannelTitle(c.LastChannel)})", $"{at:dd.MM} эскертилген ({ChannelTitle(c.LastChannel)})", $"reminded {at:dd.MM} ({ChannelTitle(c.LastChannel)})",
                $"{at:dd.MM} hatırlatıldı ({ChannelTitle(c.LastChannel)})", $"{at:dd.MM} eslatilgan ({ChannelTitle(c.LastChannel)})")
            : T("ещё не напоминали", "али эскертиле элек", "not reminded yet", "henüz hatırlatılmadı", "hali eslatilmagan"));
        var line = new TextBlock { Text = string.Join(" · ", parts), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
        info.Children.Add(line);
        if (!c.IsDue)
        {
            var reason = new TextBlock { Text = SkipText(c), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Use(reason, TextBlock.ForegroundProperty, c.Skip == DebtReminderPlanner.SkipReason.NoContact ? "BrushWarning" : "BrushTextSoft");
            info.Children.Add(reason);
        }
        else if (!c.Confirmed && DebtReminderPlanner.GetSettings().ListConfirmed)
        {
            var fresh = new TextBlock { Text = T("новый в списке — проверьте", "тизмеде жаңы — текшериңиз", "new in the list — check", "listede yeni — kontrol edin", "ro'yxatda yangi — tekshiring"),
                FontSize = 12.5 };
            Use(fresh, TextBlock.ForegroundProperty, "BrushWarning");
            info.Children.Add(fresh);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        // Сумма долга — целиком, без «…» (правило редизайна 3); красным — это долг.
        var sum = new TextBlock { Text = Money(c.Amount) + (c.Exact ? "" : " ≈"), FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Use(sum, TextBlock.ForegroundProperty, "BrushDanger");
        Grid.SetColumn(sum, 2);
        grid.Children.Add(sum);

        row.Child = grid;
        AutomationProperties.SetName(row, c.Name + " — " + Money(c.Amount));
        row.PointerPressed += (_, _) => Select(c);
        return row;
    }

    private void Select(DebtReminderPlanner.Candidate c)
    {
        _selected = c;
        RenderList();
        ShowPreview(c);
    }

    private void ToggleAll()
    {
        var due = _candidates.Where(c => c.IsDue).ToList();
        if (due.Count > 0 && due.All(c => _checked.Contains(c.ClientId)))
            _checked.Clear();
        else
            foreach (var c in due)
                _checked.Add(c.ClientId);
        _waQueue = null;
        RenderList();
        UpdateButtons();
    }

    private static string SkipText(DebtReminderPlanner.Candidate c) => c.Skip switch
    {
        DebtReminderPlanner.SkipReason.Excluded => T("исключён из напоминаний", "эскертүүлөрдөн чыгарылган", "excluded from reminders", "hatırlatmalardan çıkarıldı", "eslatmalardan chiqarilgan"),
        DebtReminderPlanner.SkipReason.RecentlyReminded => T($"недавно напоминали — снова не раньше чем через {DebtReminderPlanner.GetSettings().PerClientDays} дн.",
            $"жакында эскертилген — кайра {DebtReminderPlanner.GetSettings().PerClientDays} күндөн кийин",
            $"reminded recently — again in no less than {DebtReminderPlanner.GetSettings().PerClientDays} days",
            $"yakın zamanda hatırlatıldı — tekrar en erken {DebtReminderPlanner.GetSettings().PerClientDays} gün sonra",
            $"yaqinda eslatilgan — qayta {DebtReminderPlanner.GetSettings().PerClientDays} kundan keyin"),
        DebtReminderPlanner.SkipReason.NoContact => T("нет телефона и не подписан на бота — напомнить нечем", "телефону жок жана ботко жазылган эмес — эскертүүгө мүмкүн эмес",
            "no phone and not subscribed to the bot — no way to remind", "telefon yok ve bota abone değil — hatırlatılamaz", "telefon yo'q va botga obuna emas — eslatib bo'lmaydi"),
        _ => "",
    };

    private static string ChannelTitle(string? channel) => channel switch
    {
        "telegram" => "Telegram",
        "whatsapp" => "WhatsApp",
        _ => "—",
    };

    // ── Предпросмотр ─────────────────────────────────────────────────────────────

    private void ShowPreview(DebtReminderPlanner.Candidate? c)
    {
        _previewLangs.Children.Clear();
        if (c is null)
        {
            _previewTitle.Text = T("Предпросмотр текста", "Тексттин алдын ала көрүнүшү", "Message preview", "Mesaj önizlemesi", "Matnni oldindan ko'rish");
            _previewInfo.Text = T("Выберите клиента в списке — здесь будет текст, который он получит.", "Тизмеден кардарды тандаңыз — бул жерде ал ала турган текст чыгат.",
                "Pick a customer in the list — the text they will get appears here.", "Listeden bir müşteri seçin — alacağı metin burada görünür.",
                "Ro'yxatdan mijozni tanlang — u oladigan matn shu yerda chiqadi.");
            _previewText.Text = "";
            _previewTextBox.IsVisible = false;
            _excludeButton.IsVisible = false;
            return;
        }
        _previewTitle.Text = T($"Текст для: {c.Name}", $"Текст: {c.Name}", $"Message for: {c.Name}", $"Mesaj: {c.Name}", $"Matn: {c.Name}");
        _previewInfo.Text = T($"Долг {Money(c.Amount)} · чеков в долг: {c.Checks} · самый старый с {c.Oldest:dd.MM.yyyy}",
            $"Карыз {Money(c.Amount)} · карыз чектер: {c.Checks} · эң эскиси {c.Oldest:dd.MM.yyyy} баштап",
            $"Debt {Money(c.Amount)} · receipts on credit: {c.Checks} · oldest since {c.Oldest:dd.MM.yyyy}",
            $"Borç {Money(c.Amount)} · veresiye fiş: {c.Checks} · en eski {c.Oldest:dd.MM.yyyy}",
            $"Qarz {Money(c.Amount)} · qarzga cheklar: {c.Checks} · eng eskisi {c.Oldest:dd.MM.yyyy} dan");
        var lang = DebtReminderPlanner.LanguageFor(c.ClientId);
        foreach (var l in DebtReminderPlanner.Languages)
        {
            var seg = UiKit.Seg(DebtReminderPlanner.LanguageTitle(l), l == lang);
            var code = l;
            seg.Click += (_, _) =>
            {
                DebtReminderPlanner.SetLanguage(c.ClientId, code);
                ShowPreview(c);
            };
            _previewLangs.Children.Add(seg);
        }
        _previewText.Text = DebtReminderPlanner.Render(c, lang);
        _previewTextBox.IsVisible = true;
        _excludeButton.IsVisible = true;
        _excludeButton.Content = c.Skip == DebtReminderPlanner.SkipReason.Excluded
            ? T("Вернуть в напоминания", "Эскертүүлөргө кайтаруу", "Return to reminders", "Hatırlatmalara geri al", "Eslatmalarga qaytarish")
            : T("Исключить из напоминаний", "Эскертүүлөрдөн чыгаруу", "Exclude from reminders", "Hatırlatmalardan çıkar", "Eslatmalardan chiqarish");
        SetUiaName(_excludeButton, (string)_excludeButton.Content!);
    }

    private void ToggleExcludeSelected()
    {
        if (_selected is not { } c)
            return;
        if (c.Skip == DebtReminderPlanner.SkipReason.Excluded)
            DebtReminderPlanner.Include(c.ClientId);
        else
        {
            DebtReminderPlanner.Exclude(c.ClientId, c.Name);
            _checked.Remove(c.ClientId);
        }
        _ = LoadAsync();
    }

    // ── Кнопки отправки ─────────────────────────────────────────────────────────

    private List<DebtReminderPlanner.Candidate> Chosen() =>
        _candidates.Where(c => c.IsDue && _checked.Contains(c.ClientId)).ToList();

    private static bool TelegramReady =>
        !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotToken) && TariffGate.CanUseTelegramBot;

    private void UpdateButtons()
    {
        var quiet = DebtReminderPlanner.InQuietHours();
        _quietNote.IsVisible = quiet;
        _quietNote.Text = quiet
            ? DebtReminderPlanner.QuietHoursText() + " " + T("Список можно посмотреть, отправка — утром.", "Тизмени көрсө болот, жөнөтүү — эртең менен.",
                "You can review the list; sending — in the morning.", "Listeye bakabilirsiniz; gönderim — sabah.", "Ro'yxatni ko'rish mumkin, yuborish — ertalab.")
            : "";
        var chosen = Chosen();
        var wa = chosen.Count(c => c.WhatsApp is not null);
        if (_waQueue is { } queue)
        {
            _whatsAppButton.Content = _waNext < queue.Count
                ? T($"Следующий ▶ {queue[_waNext].Name} ({_waNext + 1} из {queue.Count})", $"Кийинки ▶ {queue[_waNext].Name} ({queue.Count} ичинен {_waNext + 1})",
                    $"Next ▶ {queue[_waNext].Name} ({_waNext + 1} of {queue.Count})", $"Sonraki ▶ {queue[_waNext].Name} ({_waNext + 1}/{queue.Count})",
                    $"Keyingi ▶ {queue[_waNext].Name} ({_waNext + 1}/{queue.Count})")
                : T("Все чаты открыты", "Бардык чаттар ачылды", "All chats opened", "Tüm sohbetler açıldı", "Barcha chatlar ochildi");
            _whatsAppButton.IsEnabled = !quiet && !_busy && _waNext < queue.Count;
        }
        else
        {
            _whatsAppButton.Content = T($"Открыть WhatsApp по очереди ({wa})", $"WhatsApp'ты кезек менен ачуу ({wa})", $"Open WhatsApp one by one ({wa})",
                $"WhatsApp'ı sırayla aç ({wa})", $"WhatsApp'ni navbat bilan ochish ({wa})");
            _whatsAppButton.IsEnabled = !quiet && !_busy && wa > 0;
        }
        SetUiaName(_whatsAppButton, (string)_whatsAppButton.Content!);

        var tg = chosen.Count(c => c.HasTelegram);
        _telegramButton.Content = T($"Отправить в Telegram ({tg} подписанных)", $"Telegram'га жөнөтүү ({tg} жазылган)", $"Send via Telegram ({tg} subscribed)",
            $"Telegram'dan gönder ({tg} abone)", $"Telegram'da yuborish ({tg} obunachi)");
        _telegramButton.IsEnabled = !quiet && !_busy && tg > 0 && TelegramReady;
        SetUiaName(_telegramButton, (string)_telegramButton.Content!);
        ToolTip.SetTip(_telegramButton, TelegramReady
            ? T("Только клиентам, которые нажали «Старт» у бота магазина.", "Дүкөндүн ботунда «Старт» баскан кардарларга гана.", "Only to customers who pressed Start in the shop bot.",
                "Yalnızca mağaza botunda Başlat'a basan müşterilere.", "Faqat do'kon botida «Start»ni bosgan mijozlarga.")
            : T("Telegram-бот не настроен (Настройки → Telegram) или недоступен на тарифе.", "Telegram-бот жөндөлгөн эмес (Жөндөөлөр → Telegram) же тарифте жок.",
                "The Telegram bot is not set up (Settings → Telegram) or not in your plan.", "Telegram botu ayarlanmadı (Ayarlar → Telegram) veya paketinizde yok.",
                "Telegram-bot sozlanmagan (Sozlamalar → Telegram) yoki tarifda yo'q."));
    }

    /// <summary>Перед первой отправкой (или если отмечены новые, ещё не подтверждённые клиенты) — подтверждение списка.</summary>
    private async Task<bool> EnsureListConfirmedAsync(IReadOnlyList<DebtReminderPlanner.Candidate> chosen)
    {
        var settings = DebtReminderPlanner.GetSettings();
        if (settings.ListConfirmed && chosen.All(c => c.Confirmed))
            return true;
        var names = string.Join(", ", chosen.Take(12).Select(c => c.Name)) + (chosen.Count > 12 ? "…" : "");
        var answer = await PosMessageBox.ShowModalAsync(this,
            T($"Подтвердите список: напомнить о долге {chosen.Count} клиентам — {names}. Отмеченные клиенты в следующий раз будут отмечены сразу.",
                $"Тизмени ырастаңыз: {chosen.Count} кардарга карызды эскертүү — {names}. Белгиленгендер кийинки жолу дароо белгиленет.",
                $"Confirm the list: remind {chosen.Count} customers about their debt — {names}. Ticked customers will be pre-ticked next time.",
                $"Listeyi onaylayın: {chosen.Count} müşteriye borç hatırlatması — {names}. İşaretlenenler bir dahaki sefere hazır işaretli olur.",
                $"Ro'yxatni tasdiqlang: {chosen.Count} mijozga qarzni eslatish — {names}. Belgilanganlar keyingi safar darhol belgilangan bo'ladi."),
            T("Подтверждение списка", "Тизмени ырастоо", "Confirm the list", "Listeyi onayla", "Ro'yxatni tasdiqlash"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question).ConfigureAwait(true);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return false;
        DebtReminderPlanner.ConfirmList(chosen.Select(c => c.ClientId));
        return true;
    }

    private async void OnWhatsApp()
    {
        try
        {
            if (DebtReminderPlanner.InQuietHours())
            {
                UpdateButtons();
                return;
            }
            if (_waQueue is null)
            {
                var queue = Chosen().Where(c => c.WhatsApp is not null).ToList();
                if (queue.Count == 0)
                    return;
                _busy = true;
                UpdateButtons();
                var ok = await EnsureListConfirmedAsync(queue).ConfigureAwait(true);
                _busy = false;
                if (!ok)
                {
                    UpdateButtons();
                    return;
                }
                _waQueue = queue;
                _waNext = 0;
            }
            // Пропускаем тех, кому уже напомнили за эти дни (например, только что в Telegram).
            while (_waNext < _waQueue.Count && DebtReminderPlanner.RecentlyReminded(_waQueue[_waNext].ClientId))
                _waNext++;
            if (_waNext < _waQueue.Count)
            {
                var c = _waQueue[_waNext++];
                await OpenWhatsAppAsync(c).ConfigureAwait(true);
            }
            UpdateButtons();
            RenderJournal();
        }
        catch (Exception ex)
        {
            _busy = false;
            PosLogger.Log($"Напоминания должникам: чат WhatsApp не открыт ({ex.GetType().Name}).", "WARNING");
            SetStatus(T("Не получилось открыть WhatsApp. Попробуйте ещё раз.", "WhatsApp ачылган жок. Дагы аракет кылыңыз.", "Could not open WhatsApp. Please try again.",
                "WhatsApp açılamadı. Tekrar deneyin.", "WhatsApp ochilmadi. Yana urinib ko'ring."), "BrushDanger");
            UpdateButtons();
        }
    }

    /// <summary>Чат WhatsApp с готовым текстом (как «Написать в WhatsApp» в Советнике): в Windows — встроенный WhatsApp Web в одном
    /// окне (следующий должник открывается в нём же), на других системах — wa.me. Отправляет владелец сам — в журнале «открыт чат».</summary>
    private async Task OpenWhatsAppAsync(DebtReminderPlanner.Candidate c)
    {
        var digits = c.WhatsApp!;
        var lang = DebtReminderPlanner.LanguageFor(c.ClientId);
        var text = DebtReminderPlanner.Render(c, lang);
        var query = "phone=" + digits + "&text=" + Uri.EscapeDataString(text);
        if (OperatingSystem.IsWindows())
        {
            if (_whatsAppWindow is { IsVisible: true } previous)
                previous.Close();
            var window = new CrmWebViewWindow(CrmWebViewWindow.WhatsAppWebUrl + "/send?" + query, "WhatsApp Web");
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_whatsAppWindow, window))
                    _whatsAppWindow = null;
            };
            _whatsAppWindow = window;
            window.Show();
        }
        else if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(text))).ConfigureAwait(true);
        DebtReminderPlanner.LogOpened(c, lang);
        SetStatus(T($"Открыт чат с {c.Name} — проверьте текст и нажмите «Отправить» в WhatsApp.", $"{c.Name} менен чат ачылды — текстти текшерип, WhatsApp'та «Жөнөтүү» басыңыз.",
            $"Chat with {c.Name} is open — check the text and press Send in WhatsApp.", $"{c.Name} ile sohbet açıldı — metni kontrol edip WhatsApp'ta Gönder'e basın.",
            $"{c.Name} bilan chat ochildi — matnni tekshirib, WhatsApp'da «Yuborish»ni bosing."), "BrushSuccess");
    }

    private async Task OnTelegramAsync()
    {
        if (_busy || DebtReminderPlanner.InQuietHours() || !TelegramReady)
        {
            UpdateButtons();
            return;
        }
        var chosen = Chosen().Where(c => c.HasTelegram && !string.IsNullOrWhiteSpace(c.TelegramChatId)).ToList();
        if (chosen.Count == 0)
            return;
        _busy = true;
        UpdateButtons();
        try
        {
            if (!await EnsureListConfirmedAsync(chosen).ConfigureAwait(true))
                return;
            var perClient = DebtReminderPlanner.GetSettings().PerClientDays;
            var answer = await PosMessageBox.ShowModalAsync(this,
                T($"Отправить напоминание о долге в Telegram {chosen.Count} подписанным клиентам? Одному клиенту — не чаще раза в {perClient} дн.",
                    $"Telegram'га {chosen.Count} жазылган кардарга карыз тууралуу эскертүү жөнөтөлүбү? Бир кардарга — {perClient} күндө бир жолудан көп эмес.",
                    $"Send a debt reminder via Telegram to {chosen.Count} subscribed customers? No more than once every {perClient} days per customer.",
                    $"Telegram'dan {chosen.Count} abone müşteriye borç hatırlatması gönderilsin mi? Bir müşteriye en fazla {perClient} günde bir.",
                    $"Telegram'da {chosen.Count} obunachi mijozga qarz haqida eslatma yuborilsinmi? Bir mijozga — {perClient} kunda bir martadan ko'p emas."),
                T("Отправка в Telegram", "Telegram'га жөнөтүү", "Send via Telegram", "Telegram'dan gönder", "Telegram'da yuborish"),
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question).ConfigureAwait(true);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;

            int sent = 0, failed = 0, skipped = 0, n = 0;
            foreach (var c in chosen)
            {
                n++;
                SetStatus(T($"Отправляю в Telegram: {n} из {chosen.Count}…", $"Telegram'га жөнөтүп жатам: {chosen.Count} ичинен {n}…", $"Sending via Telegram: {n} of {chosen.Count}…",
                    $"Telegram'dan gönderiliyor: {n}/{chosen.Count}…", $"Telegram'da yuborilmoqda: {n}/{chosen.Count}…"), "BrushTextSoft");
                // Повторная проверка перед каждой отправкой: за это время могли напомнить в WhatsApp.
                if (DebtReminderPlanner.RecentlyReminded(c.ClientId) || DebtReminderPlanner.IsExcluded(c.ClientId))
                {
                    skipped++;
                    continue;
                }
                var lang = DebtReminderPlanner.LanguageFor(c.ClientId);
                var text = EscapeHtml(DebtReminderPlanner.Render(c, lang));
                var error = await TelegramBotService.SendToAsync(c.TelegramChatId!, text, _cts.Token).ConfigureAwait(true);
                DebtReminderPlanner.LogSent(c, lang, error is null, error);
                if (error is null)
                    sent++;
                else
                    failed++;
                if (_cts.IsCancellationRequested)
                    break;
                // Не больше ~3 сообщений в секунду — далеко от предела Telegram.
                await Task.Delay(350, _cts.Token).ConfigureAwait(true);
            }
            SetStatus(T($"Telegram: отправлено {sent}, не отправлено {failed}, пропущено (недавно напоминали) {skipped}. Подробно — во вкладке «Журнал».",
                    $"Telegram: жөнөтүлдү {sent}, жөнөтүлгөн жок {failed}, өткөрүлдү (жакында эскертилген) {skipped}. Толугу — «Журнал» өтмөгүндө.",
                    $"Telegram: sent {sent}, failed {failed}, skipped (reminded recently) {skipped}. Details — in the Log tab.",
                    $"Telegram: gönderildi {sent}, gönderilemedi {failed}, atlandı (yakında hatırlatıldı) {skipped}. Ayrıntılar — Kayıt sekmesinde.",
                    $"Telegram: yuborildi {sent}, yuborilmadi {failed}, o'tkazildi (yaqinda eslatilgan) {skipped}. Batafsil — «Jurnal» bo'limida."),
                failed > 0 ? "BrushWarning" : "BrushSuccess");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Напоминания должникам: отправка в Telegram прервана ({ex.GetType().Name}).", "WARNING");
            SetStatus(T("Отправка в Telegram прервана — проверьте интернет.", "Telegram'га жөнөтүү үзүлдү — интернетти текшериңиз.", "Sending via Telegram stopped — check the internet.",
                "Telegram gönderimi kesildi — interneti kontrol edin.", "Telegram'da yuborish to'xtadi — internetni tekshiring."), "BrushDanger");
        }
        finally
        {
            _busy = false;
            UpdateButtons();
            RenderJournal();
        }
    }

    /// <summary>Сообщение уходит с parse_mode=HTML — символы &lt; &gt; &amp; в имени или шаблоне иначе сломали бы отправку.</summary>
    private static string EscapeHtml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── Журнал ──────────────────────────────────────────────────────────────────

    private void ShowTab(bool journal)
    {
        UiKit.SetActive(_tabCandidates, !journal);
        UiKit.SetActive(_tabJournal, journal);
        _candidatesView.IsVisible = !journal;
        _journalView.IsVisible = journal;
        if (journal)
            RenderJournal();
    }

    private void RenderJournal()
    {
        if (!_journalView.IsVisible)
            return;
        _journal.Children.Clear();
        var entries = DebtReminderPlanner.Journal(300);
        if (entries.Count == 0)
        {
            var empty = new TextBlock { Text = T("Журнал пуст: напоминаний ещё не было.", "Журнал бош: эскертүүлөр али болгон эмес.", "The log is empty: no reminders yet.",
                    "Kayıt boş: henüz hatırlatma yok.", "Jurnal bo'sh: hali eslatmalar bo'lmagan."),
                FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 40, 16, 0) };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _journal.Children.Add(empty);
            return;
        }
        foreach (var e in entries)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 2) };
            var at = new TextBlock { Text = e.At.ToString("dd.MM.yyyy HH:mm", Ru), FontSize = 13, Width = 130, VerticalAlignment = VerticalAlignment.Center };
            Use(at, TextBlock.ForegroundProperty, "BrushTextSoft");
            row.Children.Add(at);
            var who = new TextBlock { Text = e.Name + (e.Phone.Length > 0 ? " · " + e.Phone : ""), FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Use(who, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(who, 1);
            row.Children.Add(who);
            var how = new TextBlock { Text = StatusTitle(e), FontSize = 13, Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
            Use(how, TextBlock.ForegroundProperty, e.Status == "failed" ? "BrushDanger" : e.Status == "sent" ? "BrushSuccess" : "BrushText");
            if (e.Error is { Length: > 0 } err)
                ToolTip.SetTip(how, err);
            Grid.SetColumn(how, 2);
            row.Children.Add(how);
            var sum = new TextBlock { Text = Money(e.Amount), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Use(sum, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(sum, 3);
            row.Children.Add(sum);
            AutomationProperties.SetName(row, $"{at.Text} {e.Name} {StatusTitle(e)}");
            _journal.Children.Add(row);
        }
    }

    private static string StatusTitle(DebtReminderPlanner.LogEntry e) => (e.Channel, e.Status) switch
    {
        ("whatsapp", _) => T("WhatsApp — открыт чат", "WhatsApp — чат ачылды", "WhatsApp — chat opened", "WhatsApp — sohbet açıldı", "WhatsApp — chat ochildi"),
        ("telegram", "sent") => T("Telegram — отправлено", "Telegram — жөнөтүлдү", "Telegram — sent", "Telegram — gönderildi", "Telegram — yuborildi"),
        ("telegram", _) => T("Telegram — не отправлено", "Telegram — жөнөтүлгөн жок", "Telegram — not sent", "Telegram — gönderilemedi", "Telegram — yuborilmadi"),
        _ => e.Channel,
    };

    // ── Настройки ───────────────────────────────────────────────────────────────

    private void UpdateSettingsToggle()
    {
        _settingsToggle.Content = (_settingsPanel.IsVisible
            ? T("Скрыть настройки", "Жөндөөлөрдү жашыруу", "Hide settings", "Ayarları gizle", "Sozlamalarni yashirish")
            : T("Расписание и текст", "Мөөнөт жана текст", "Schedule and text", "Plan ve metin", "Jadval va matn")) + (_settingsPanel.IsVisible ? " ▴" : " ▾");
        SetUiaName(_settingsToggle, (string)_settingsToggle.Content!);
    }

    private void BuildSettingsPanel(DebtReminderPlanner.Settings s)
    {
        Use(_settingsPanel, Border.BackgroundProperty, "BrushPanel");
        Use(_settingsPanel, Border.BorderBrushProperty, "BrushBorder");
        _enabledBox.IsChecked = s.Enabled;
        _enabledBox.Content = T("Напоминать по расписанию (карточка «Пора напомнить» в Нур Советнике)", "Мөөнөт боюнча эскертүү (Нур Кеңешчиде «Эскертүү убагы» карточкасы)",
            "Remind on schedule (“Time to remind” card in Nur Advisor)", "Plana göre hatırlat (Nur Danışman'da «Hatırlatma zamanı» kartı)",
            "Jadval bo'yicha eslatish (Nur Maslahatchida «Eslatish vaqti» kartasi)");
        Use(_enabledBox, CheckBox.ForegroundProperty, "BrushText");
        AutomationProperties.SetName(_enabledBox, (string)_enabledBox.Content);

        var numbers = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        numbers.Children.Add(Field(T("Раз в (дней)", "Ар бир (күн)", "Every (days)", "Her (gün)", "Har (kun)"), _everyBox));
        numbers.Children.Add(Field(T("Просрочка от (дней)", "Мөөнөтү өткөнү (күндөн)", "Overdue from (days)", "Gecikme en az (gün)", "Kechikish (kundan)"), _minDaysBox));
        numbers.Children.Add(Field(T("Одному клиенту не чаще раза в (3–7 дней)", "Бир кардарга эң көп (3–7 күндө бир)", "Per customer at most once in (3–7 days)",
            "Bir müşteriye en fazla (3–7 günde bir)", "Bir mijozga ko'pi bilan (3–7 kunda bir)"), _perClientBox));
        // 2026-10-21: тихие часы «с … до …» (часы 0–23; одинаковые — тихих часов нет) (пакет 7, И9).
        numbers.Children.Add(Field(T("Тихие часы: с (час, 0–23)", "Тынч сааттар: саат (0–23) тартып", "Quiet hours: from (hour, 0–23)",
            "Sessiz saatler: başlangıç (saat, 0–23)", "Tinch soatlar: dan (soat, 0–23)"), _quietFromBox));
        numbers.Children.Add(Field(T("до (час, 0–23)", "чейин (саат, 0–23)", "to (hour, 0–23)", "bitiş (saat, 0–23)", "gacha (soat, 0–23)"), _quietToBox));
        AutomationProperties.SetName(_quietFromBox, T("Тихие часы: с какого часа", "Тынч сааттар: кайсы сааттан", "Quiet hours: from hour", "Sessiz saatler: başlangıç saati", "Tinch soatlar: qaysi soatdan"));
        AutomationProperties.SetName(_quietToBox, T("Тихие часы: до какого часа", "Тынч сааттар: кайсы саатка чейин", "Quiet hours: to hour", "Sessiz saatler: bitiş saati", "Tinch soatlar: qaysi soatgacha"));

        RenderDefaultLangs();
        var placeholders = new TextBlock
        {
            Text = T("В тексте можно использовать: {name} — имя, {amount} — сумма долга, {shop} — название магазина, {days} — дней просрочки.",
                "Текстте колдонсо болот: {name} — аты, {amount} — карыздын суммасы, {shop} — дүкөндүн аты, {days} — мөөнөтү өткөн күндөр.",
                "You can use in the text: {name} — name, {amount} — debt amount, {shop} — shop name, {days} — days overdue.",
                "Metinde kullanılabilir: {name} — ad, {amount} — borç tutarı, {shop} — mağaza adı, {days} — gecikme günü.",
                "Matnda ishlatish mumkin: {name} — ism, {amount} — qarz summasi, {shop} — do'kon nomi, {days} — kechikkan kunlar."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Use(placeholders, TextBlock.ForegroundProperty, "BrushTextSoft");
        var quiet = _quietText;
        quiet.Text = DebtReminderPlanner.QuietHoursText();
        Use(quiet, TextBlock.ForegroundProperty, "BrushTextSoft");

        var save = Small(UiKit.Ghost(this, T("Сохранить настройки", "Жөндөөлөрдү сактоо", "Save settings", "Ayarları kaydet", "Sozlamalarni saqlash")));
        SetUiaName(save, (string)save.Content!);
        save.Click += async (_, _) => await SaveSettingsAsync().ConfigureAwait(true);
        var reset = Small(UiKit.Ghost(this, T("Вернуть стандартный текст", "Стандарттык текстти кайтаруу", "Restore default text", "Varsayılan metni geri yükle", "Standart matnni qaytarish")));
        SetUiaName(reset, (string)reset.Content!);
        reset.Click += (_, _) =>
        {
            _templateRu.Text = DebtReminderPlanner.DefaultTemplateRu;
            _templateKy.Text = DebtReminderPlanner.DefaultTemplateKy;
            _templateUz.Text = DebtReminderPlanner.DefaultTemplateUz;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0), Children = { save, reset } };
        Use(_settingsStatus, TextBlock.ForegroundProperty, "BrushTextSoft");

        _settingsPanel.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _enabledBox, numbers, quiet,
                UiKit.Label(this, T("Язык сообщения по умолчанию", "Билдирүүнүн демейки тили", "Default message language", "Varsayılan mesaj dili", "Xabarning asosiy tili")),
                UiKit.Segment(_defaultLangs),
                UiKit.Label(this, T("Текст на русском", "Орусча текст", "Text in Russian", "Rusça metin", "Rus tilidagi matn")), _templateRu,
                UiKit.Label(this, T("Текст на кыргызском", "Кыргызча текст", "Text in Kyrgyz", "Kırgızca metin", "Qirg'iz tilidagi matn")), _templateKy,
                UiKit.Label(this, T("Текст на узбекском", "Өзбекче текст", "Text in Uzbek", "Özbekçe metin", "O'zbek tilidagi matn")), _templateUz,
                placeholders, buttons, _settingsStatus,
            },
        };
    }

    private void RenderDefaultLangs()
    {
        _defaultLangs.Children.Clear();
        foreach (var l in DebtReminderPlanner.Languages)
        {
            var seg = UiKit.Seg(DebtReminderPlanner.LanguageTitle(l), l == _defaultLang);
            var code = l;
            seg.Click += (_, _) =>
            {
                _defaultLang = code;
                RenderDefaultLangs();
            };
            _defaultLangs.Children.Add(seg);
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(_everyBox.Text?.Trim(), out var every) || !int.TryParse(_minDaysBox.Text?.Trim(), out var minDays)
            || !int.TryParse(_perClientBox.Text?.Trim(), out var perClient)
            || !int.TryParse(_quietFromBox.Text?.Trim(), out var quietFrom) || !int.TryParse(_quietToBox.Text?.Trim(), out var quietTo))
        {
            _settingsStatus.Text = T("Введите числа: дни и часы.", "Күндөрдү жана сааттарды сан менен жазыңыз.", "Enter numbers: days and hours.", "Sayı girin: gün ve saat.", "Raqam kiriting: kunlar va soatlar.");
            Use(_settingsStatus, TextBlock.ForegroundProperty, "BrushDanger");
            return;
        }
        var s = DebtReminderPlanner.GetSettings();
        s.Enabled = _enabledBox.IsChecked == true;
        s.EveryDays = every;
        s.MinOverdueDays = minDays;
        s.PerClientDays = perClient;
        s.QuietFromHour = quietFrom;
        s.QuietToHour = quietTo;
        s.Language = _defaultLang;
        s.TemplateRu = _templateRu.Text ?? "";
        s.TemplateKy = _templateKy.Text ?? "";
        s.TemplateUz = _templateUz.Text ?? "";
        DebtReminderPlanner.SaveSettings(s);
        var saved = DebtReminderPlanner.GetSettings();
        // Значения вне допустимого (например, 10 дней на клиента) планировщик поправил — показываем поправленные.
        _everyBox.Text = saved.EveryDays.ToString(CultureInfo.InvariantCulture);
        _minDaysBox.Text = saved.MinOverdueDays.ToString(CultureInfo.InvariantCulture);
        _perClientBox.Text = saved.PerClientDays.ToString(CultureInfo.InvariantCulture);
        _quietFromBox.Text = saved.QuietFromHour.ToString(CultureInfo.InvariantCulture);
        _quietToBox.Text = saved.QuietToHour.ToString(CultureInfo.InvariantCulture);
        _quietText.Text = DebtReminderPlanner.QuietHoursText();
        _templateRu.Text = saved.TemplateRu;
        _templateKy.Text = saved.TemplateKy;
        _templateUz.Text = saved.TemplateUz;
        _settingsStatus.Text = T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi.");
        Use(_settingsStatus, TextBlock.ForegroundProperty, "BrushSuccess");
        await LoadAsync().ConfigureAwait(true);
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
        l.MaxWidth = 260;
        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 16, 6), Children = { l, box } };
    }

    private TextBox NumberBox(int value)
    {
        var box = UiKit.Input(this, "", 38);
        box.Width = 110;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.Text = value.ToString(CultureInfo.InvariantCulture);
        return box;
    }

    private TextBox TemplateBox(string text)
    {
        var box = UiKit.Input(this, "", 76);
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        box.Padding = new Thickness(10, 8);
        box.FontSize = 13.5;
        box.Text = text;
        return box;
    }

    private static Button Small(Button b)
    {
        b.Height = 38;
        b.FontSize = 13.5;
        return b;
    }

    private static void SetUiaName(Control c, string text) => AutomationProperties.SetName(c, text);

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

    private static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
