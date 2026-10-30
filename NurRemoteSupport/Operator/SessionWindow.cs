using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

/// <summary>Окно сеанса с кассой: экран (вписать / 1:1 / на весь экран), мышь и клавиатура, «только просмотр», качество,
/// комбинации клавиш; справа — чат с кассиром, сведения о кассе, диагностика, журнал кассы (и zip), файлы на кассу, буфер
/// обмена, заметки по кассе (история обращений). Подключение — только после «Разрешить» кассира.
/// 2026-10-27 (пакет О1): значок «кассир разрешил только просмотр» (ввод выключен), вкладки, которые касса не поддерживает
/// (нет в caps), честные состояния «ждём ответ / касса не ответила», отсчёт ожидания согласия, «Отменить / Ввести PIN заново /
/// Закрыть», мышь без залипания, полный экран с панелью, окно по размеру экрана, выбор монитора, пауза экрана (UAC).</summary>
internal sealed class SessionWindow : Window
{
    private readonly Api _api;
    private readonly string? _sessionId;
    private readonly string? _code;
    private string _pin;
    private string _company, _machine;
    private readonly string _user, _note;
    private SessionClient? _client;
    private string _serverSessionId = "";
    private bool _live;
    private bool _ended;
    private bool _viewOnly;
    // 2026-10-27 (В-1): кассир выбрал «Только просмотр» — ввод выключен и у оператора, и на сервере.
    private bool _cashierViewOnly;
    // 2026-10-26: приоритет кассира — кассир работает сам, касса не исполняет ввод оператора (сообщение client-active от кассы).
    private bool _clientActive;
    private bool _fit = true;
    private bool _fullscreen;
    private bool _syncing;
    // Что касса умеет сверх экрана и ввода (started.host.caps). Нет списка — старая касса (1.17.61): чат, сведения, файлы и буфер молчат.
    private HashSet<string> _caps = new();
    private int _monitors = 1;

    // экран
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Border _screen = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Path _cursor = new()
    {
        Data = Geometry.Parse("M0,0 L0,16 L4.5,12 L7.5,19 L10,18 L7,11 L12.5,11 Z"), Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, IsVisible = false,
    };
    private readonly Border _clientBanner;
    private readonly Border _pausedBanner;
    private readonly Border _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Ui.Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 16), MaxWidth = 520, Margin = new Thickness(16) };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Ui.Text, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _statusButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    private WriteableBitmap? _bitmap;
    private int _cx = -1, _cy = -1;
    private DateTime _consentDeadline;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    // шапка
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Ui.TextSoft, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    private readonly TextBlock _stats = new() { FontSize = 11, Foreground = Ui.TextSoft, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToastHost _toast = new();
    private readonly Border _viewPill;
    private readonly ToggleSwitch _viewToggle = new() { OnContent = L.T("Только просмотр", "Көрүү гана", "View only", "Yalnızca izle", "Faqat ko'rish"), OffContent = L.T("Управление", "Башкаруу", "Control", "Kontrol", "Boshqaruv"), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _quality = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    private readonly ComboBox _monitor = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 120, IsVisible = false };
    private Button _keysBtn = null!, _fitBtn = null!, _fullBtn = null!;
    private Border _headBorder = null!;
    private Border _side = null!;
    private Grid _body = null!;
    private StackPanel _fsBar = null!;
    private Button _panelBtn = null!;

    // инструменты
    private readonly StackPanel _chatList = new() { Spacing = 6 };
    private readonly ScrollViewer _chatScroll = new();
    private TextBox _chatInput = null!;
    private readonly TextBox _info = Mono();
    private readonly TextBox _logs = Mono();
    private readonly TextBlock _logsStatus = new() { Foreground = Ui.TextSoft, TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly StackPanel _diag = new() { Spacing = 6 };
    private readonly StackPanel _files = new() { Spacing = 6 };
    private readonly TextBox _clip = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, Watermark = L.T("Текст для буфера кассы или ввода", "Кассанын буфери үчүн текст", "Text for the till clipboard or typing", "Kasa panosu için metin", "Kassa buferi uchun matn") };
    private readonly StackPanel _notes = new() { Spacing = 8 };
    private readonly TextBox _noteInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Watermark = L.T("Что случилось и что сделали…", "Эмне болду жана эмне жасалды…", "What happened and what was done…", "Ne oldu ve ne yapıldı…", "Nima bo'ldi va nima qilindi…") };
    private readonly List<Button> _viewOnlyDisabled = new();
    private string? _clipSetText; // текст, который мы положили в буфер кассы и ждём обратно для проверки
    private readonly Dictionary<string, CancellationTokenSource> _waits = new();
    private readonly Dictionary<string, TextBlock> _fileLines = new();

    // вкладки: свои кнопки в одну строку с прокруткой (у обычного TabControl кыргызские подписи переносились на вторую строку)
    private sealed record Tab(string Key, string Title, string? Cap, Control Panel, Control? Gate);
    private readonly List<Tab> _tabList = new();
    private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ContentControl _tabContent = new();
    private string _tabKey = "chat";

    // ввод
    private readonly HashSet<string> _keysDown = new();
    private readonly HashSet<int> _buttonsDown = new();
    private (int X, int Y) _lastPoint;
    private (int X, int Y)? _pendingMove;
    private readonly DispatcherTimer _moveTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public SessionWindow(Api api, string? sessionId, string? code, string pin, string company, string machine, string user, string note)
    {
        _api = api;
        _sessionId = sessionId;
        _code = code;
        _pin = pin;
        _company = company;
        _machine = machine;
        _user = user;
        _note = note;
        // 2026-10-27 (С-9): размер — по рабочей области экрана (без панели задач), а не фиксированные 1500×920 с минимумом 1000×640.
        Ui.FitToScreen(this, 1500, 920, 900, 520);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        _clientBanner = Banner(
            L.T("Кассир работает — ваше управление приостановлено", "Кассир иштеп жатат — сиздин башкаруу токтотулду", "The cashier is working — your control is paused", "Kasiyer çalışıyor — kontrolünüz durduruldu", "Kassir ishlayapti — boshqaruvingiz to'xtatildi"),
            L.T("Ввод включится сам через несколько секунд после того, как кассир закончит.", "Кассир бүткөндөн бир нече секунддан кийин киргизүү өзү күйөт.", "Input resumes by itself a few seconds after the cashier stops.",
                "Kasiyer bitirdikten birkaç saniye sonra giriş kendiliğinden açılır.", "Kassir tugatgandan bir necha soniya o'tgach kiritish o'zi yoqiladi."));
        _pausedBanner = Banner(
            L.T("Экран кассы защищён Windows (UAC или блокировка) — сеанс продолжается", "Кассанын экраны Windows тарабынан корголгон (UAC же бөгөттөө) — сеанс уланууда", "The till's screen is protected by Windows (UAC or lock) — the session continues",
                "Kasa ekranı Windows tarafından korunuyor (UAC veya kilit) — oturum sürüyor", "Kassa ekrani Windows tomonidan himoyalangan (UAC yoki qulf) — seans davom etadi"),
            L.T("Попросите кассира ответить на запрос Windows. Картинка вернётся сама.", "Кассирден Windows суроосуна жооп берүүнү сураңыз. Сүрөт өзү кайтат.", "Ask the cashier to answer the Windows prompt. The picture returns by itself.",
                "Kasiyerden Windows isteğini yanıtlamasını isteyin. Görüntü kendiliğinden döner.", "Kassirdan Windows so'roviga javob berishini so'rang. Rasm o'zi qaytadi."));
        _viewPill = Ui.Pill(L.T("Кассир разрешил только просмотр", "Кассир көрүүгө гана уруксат берди", "The cashier allowed view only", "Kasiyer yalnızca izlemeye izin verdi", "Kassir faqat ko'rishga ruxsat berdi"), Ui.WarnFill, Ui.OnAccent);
        _viewPill.IsVisible = false;
        _viewPill.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(_viewPill, L.T("Кассир разрешил только просмотр", "Кассир көрүүгө гана уруксат берди", "The cashier allowed view only", "Kasiyer yalnızca izlemeye izin verdi", "Kassir faqat ko'rishga ruxsat berdi"));
        UpdateHeader(L.T("подключаюсь…", "туташып жатам…", "connecting…", "bağlanıyor…", "ulanmoqda…"));
        Content = new Panel { Children = { Build(), _toast } };
        Activated += (_, _) => Toast.Sink = _toast.Show;
        Opened += async (_, _) =>
        {
            App.Sessions.Add(this);
            await ConnectAsync();
        };
        Closing += (_, _) =>
        {
            ReleaseInput();
            _countdown.Stop();
            _moveTimer.Stop();
            foreach (var c in _waits.Values)
                c.Cancel();
            _ = _client?.DisposeAsync().AsTask();
        };
        Closed += (_, _) =>
        {
            App.Sessions.Remove(this);
            var old = _bitmap;
            _image.Source = null;
            _bitmap = null;
            old?.Dispose();
        };
        // Esc в полном экране — выход из него (на кассу Esc можно отправить через меню «Клавиши»).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_fullscreen && e.Key == Key.Escape)
            {
                e.Handled = true;
                SetFullscreen(false);
            }
        }, RoutingStrategies.Tunnel);
        _countdown.Tick += (_, _) => UpdateWaitingText();
        _moveTimer.Tick += (_, _) => FlushMove();
        PropertyChanged += (_, e) =>
        {
            // окно развернули/свернули сами — если вышли из полного экрана кнопкой окна, интерфейс возвращается
            if (e.Property == WindowStateProperty && _fullscreen && WindowState != WindowState.FullScreen)
                SetFullscreen(false);
        };
    }

    private static TextBox Mono() => new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Courier New, monospace"), FontSize = 12,
        Background = Ui.Soft,
    };

    private static Border Banner(string title, string sub) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 14, 16, 0), Background = Ui.WarnFill,
        CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 10), IsHitTestVisible = false, IsVisible = false, MaxWidth = 620,
        Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, Foreground = Ui.OnAccent, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                new TextBlock { Text = sub, Foreground = Ui.OnAccent, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            },
        },
    };

    private string Place() => _company.Length > 0 ? _company : L.T("Касса", "Касса", "Till", "Kasa", "Kassa");

    private void UpdateHeader(string state)
    {
        // 2026-10-27: пока касса неизвестна (вход по коду), заголовок без пустого «Касса ·  —».
        var head = _company.Length > 0 || _machine.Length > 0
            ? $"{Place()}{(_machine.Length > 0 ? " · " + _machine : "")}"
            : L.T("Подключение к кассе", "Кассага туташуу", "Connecting to a till", "Kasaya bağlanılıyor", "Kassaga ulanish");
        Title = $"{head} — NurCRM {L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam")}";
        _title.Text = head;
        _subtitle.Text = (_user.Length > 0 ? L.T("кассир: ", "кассир: ", "cashier: ", "kasiyer: ", "kassir: ") + _user + " · " : "") + state + (_note.Length > 0 ? $" · «{_note}»" : "");
    }

    private void SetStatus(string? text, params Button[] buttons)
    {
        _statusText.Text = text ?? "";
        _statusButtons.Children.Clear();
        foreach (var b in buttons)
            _statusButtons.Children.Add(b);
        _statusButtons.IsVisible = buttons.Length > 0;
        _status.IsVisible = text != null;
    }

    // ------------------------------------------------------------------ разметка

    private Control Build()
    {
        // Шапка: WrapPanel — на узком окне кнопки переносятся на вторую строку, а не наезжают на название.
        _viewToggle.IsCheckedChanged += async (_, _) =>
        {
            if (_syncing)
                return;
            _viewOnly = _viewToggle.IsChecked == true;
            ReleaseInput();
            if (_live && _client != null)
                await SafeSend(new { type = "view-only", value = _viewOnly });
        };
        AutomationProperties.SetName(_viewToggle, L.T("Только просмотр", "Көрүү гана", "View only", "Yalnızca izle", "Faqat ko'rish"));
        foreach (var (q, name) in new[] { (30, L.T("Качество: низкое", "Сапат: төмөн", "Quality: low", "Kalite: düşük", "Sifat: past")), (60, L.T("Качество: среднее", "Сапат: орто", "Quality: medium", "Kalite: orta", "Sifat: o'rta")), (85, L.T("Качество: высокое", "Сапат: жогору", "Quality: high", "Kalite: yüksek", "Sifat: yuqori")) })
            _quality.Items.Add(new ComboBoxItem { Content = name, Tag = q });
        _quality.SelectedIndex = Settings.Current.Quality <= 40 ? 0 : Settings.Current.Quality >= 80 ? 2 : 1;
        AutomationProperties.SetName(_quality, L.T("Качество картинки", "Сүрөт сапаты", "Picture quality", "Görüntü kalitesi", "Rasm sifati"));
        _quality.SelectionChanged += async (_, _) =>
        {
            if (_quality.SelectedItem is ComboBoxItem { Tag: int q })
            {
                Settings.Current.Quality = q;
                Settings.Current.Save();
                if (_live)
                    await SafeSend(new { type = "quality", value = q });
            }
        };
        AutomationProperties.SetName(_monitor, L.T("Монитор кассы", "Кассанын монитору", "Till monitor", "Kasa monitörü", "Kassa monitori"));
        _monitor.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && _live && _monitor.SelectedIndex >= 0)
                await SafeSend(new { type = "monitor", index = _monitor.SelectedIndex });
        };
        _keysBtn = Ui.Btn(L.T("Клавиши ▾", "Баскычтар ▾", "Keys ▾", "Tuşlar ▾", "Tugmalar ▾"));
        var menu = new MenuFlyout();
        foreach (var (title, combo) in new[]
                 {
                     ("Ctrl+Shift+Esc — " + L.T("диспетчер задач", "тапшырмалар диспетчери", "task manager", "görev yöneticisi", "vazifalar dispetcheri"), new[] { "ControlLeft", "ShiftLeft", "Escape" }),
                     ("Alt+Tab", new[] { "AltLeft", "Tab" }),
                     ("Win", new[] { "MetaLeft" }),
                     ("Win+R — " + L.T("выполнить", "аткаруу", "run", "çalıştır", "bajarish"), new[] { "MetaLeft", "KeyR" }),
                     ("Win+D — " + L.T("рабочий стол", "иш столу", "desktop", "masaüstü", "ish stoli"), new[] { "MetaLeft", "KeyD" }),
                     ("Win+E — " + L.T("проводник", "өткөргүч", "explorer", "gezgin", "provodnik"), new[] { "MetaLeft", "KeyE" }),
                     ("Alt+F4", new[] { "AltLeft", "F4" }),
                     ("Ctrl+C", new[] { "ControlLeft", "KeyC" }),
                     ("Ctrl+V", new[] { "ControlLeft", "KeyV" }),
                     ("Ctrl+A", new[] { "ControlLeft", "KeyA" }),
                     ("Esc", new[] { "Escape" }),
                     ("F5", new[] { "F5" }),
                     ("PrintScreen", new[] { "PrintScreen" }),
                 })
        {
            var c = combo;
            menu.Items.Add(new MenuItem { Header = title, Command = new Relay(async () => await SendComboAsync(c)) });
        }
        _keysBtn.Flyout = menu;
        _fitBtn = Ui.Btn("1:1");
        _fitBtn.Click += (_, _) =>
        {
            _fit = !_fit;
            _fitBtn.Content = _fit ? "1:1" : L.T("Вписать", "Батыруу", "Fit", "Sığdır", "Sig'dirish");
            ApplyFit();
        };
        _fullBtn = Ui.Btn(L.T("На весь экран", "Толук экран", "Full screen", "Tam ekran", "To'liq ekran"));
        _fullBtn.Click += (_, _) => SetFullscreen(!_fullscreen);
        var end = Ui.BtnAsync(L.T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), "danger", async () => await EndAndCloseAsync());
        var title0 = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { _title, _subtitle } };
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new Control[] { title0, _viewPill, _stats, _viewToggle, _quality, _monitor, _keysBtn, _fitBtn, _fullBtn, end })
        {
            c.Margin = c == title0 ? new Thickness(0, 4, 16, 4) : c == _viewPill ? new Thickness(0, 4, 8, 4) : new Thickness(0, 4, 8, 4);
            tools.Children.Add(c);
        }
        _headBorder = new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 6), Child = tools };

        // Экран
        _scroll.Content = _image;
        _overlay.Children.Add(_cursor);
        _status.Child = new StackPanel { Children = { _statusText, _statusButtons } };
        _fsBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0), IsVisible = false,
        };
        _panelBtn = Ui.Btn(L.T("Панель ⟩", "Панель ⟩", "Panel ⟩", "Panel ⟩", "Panel ⟩"), "primary", () => ToggleSidePanel());
        var exitFs = Ui.Btn(L.T("Выйти из полного экрана (Esc)", "Толук экрандан чыгуу (Esc)", "Exit full screen (Esc)", "Tam ekrandan çık (Esc)", "To'liq ekrandan chiqish (Esc)"), "secondary", () => SetFullscreen(false));
        _fsBar.Children.Add(_panelBtn);
        _fsBar.Children.Add(exitFs);
        _screen.Child = new Panel { Children = { _scroll, _overlay, _status, _clientBanner, _pausedBanner, _fsBar } };
        AutomationProperties.SetName(_screen, L.T("Экран кассы", "Кассанын экраны", "Till screen", "Kasa ekranı", "Kassa ekrani"));
        // 2026-10-27: экран кассы виден программам чтения экрана и проверке через UI Automation (обычная рамка по умолчанию не отдаётся)
        AutomationProperties.SetAccessibilityView(_screen, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(_screen, AutomationControlType.Image);
        AttachInput();

        // Инструменты справа: свои вкладки (в одну строку, с прокруткой), панели создаются один раз и сохраняют ввод
        AddTab("chat", L.T("Чат", "Чат", "Chat", "Sohbet", "Chat"), "chat", ChatPanel());
        AddTab("info", L.T("Касса", "Касса", "Till", "Kasa", "Kassa"), "info", InfoPanel());
        AddTab("diag", L.T("Диагностика", "Диагностика", "Diagnostics", "Tanılama", "Diagnostika"), "diag", DiagPanel());
        AddTab("logs", L.T("Журнал", "Журнал", "Log", "Günlük", "Jurnal"), "logs", LogsPanel());
        AddTab("files", L.T("Файлы", "Файлдар", "Files", "Dosyalar", "Fayllar"), "files", FilesPanel());
        AddTab("clip", L.T("Буфер", "Буфер", "Clipboard", "Pano", "Bufer"), "clipboard", ClipPanel());
        AddTab("notes", L.T("Заметки", "Эскертмелер", "Notes", "Notlar", "Eslatmalar"), null, NotesPanel());
        var bar = new ScrollViewer
        {
            Content = _tabBar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var sideDock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        sideDock.Children.Add(bar);
        sideDock.Children.Add(_tabContent);
        _side = new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(10, 6), Child = sideDock };
        SelectTab("chat");

        _body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,460") };
        _body.Children.Add(_screen);
        Grid.SetColumn(_side, 1);
        _body.Children.Add(_side);
        var root = new DockPanel();
        DockPanel.SetDock(_headBorder, Dock.Top);
        root.Children.Add(_headBorder);
        root.Children.Add(_body);
        RefreshEnabled();
        return root;
    }

    private void AddTab(string key, string title, string? cap, Control panel)
    {
        Control? gate = null;
        Control shown = panel;
        if (cap != null)
        {
            // Если касса не поддерживает возможность — вместо вечного «Проверяю…» показывается понятный текст.
            var card = Ui.Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Ui.H2(title),
                    Ui.SoftText(L.T($"Эта версия кассы не поддерживает «{title}» (нужна версия 1.17.62 или новее). Экран и управление работают.",
                        $"Кассанын бул версиясы «{title}» өтмөгүн колдобойт (1.17.62 же жаңыраак керек). Экран жана башкаруу иштейт.",
                        $"This till version does not support “{title}” (version 1.17.62 or newer is needed). Screen and control work.",
                        $"Bu kasa sürümü «{title}» özelliğini desteklemiyor (1.17.62 veya daha yeni gerekir). Ekran ve kontrol çalışır.",
                        $"Kassaning bu versiyasi «{title}»ni qo'llamaydi (1.17.62 yoki yangisi kerak). Ekran va boshqaruv ishlaydi."), 13),
                },
            }, 14);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(0, 8, 0, 0);
            card.IsVisible = false;
            gate = card;
            shown = new Panel { Children = { panel, card } };
        }
        _tabList.Add(new Tab(key, title, cap, panel, gate));
        _tabPanels[key] = shown;
    }

    private readonly Dictionary<string, Control> _tabPanels = new();

    private void SelectTab(string key)
    {
        _tabKey = key;
        _tabBar.Children.Clear();
        foreach (var t in _tabList)
        {
            var k = t.Key;
            var supported = t.Cap == null || !_live && !_ended || _caps.Contains(t.Cap);
            var b = Ui.Chip(t.Title, k == key, () => SelectTab(k));
            b.FontSize = 12;
            b.Padding = new Thickness(8, 5);
            b.Opacity = supported ? 1 : 0.6;
            AutomationProperties.SetName(b, t.Title + (supported ? "" : " — " + L.T("не поддерживается кассой", "касса колдобойт", "not supported by the till", "kasa desteklemiyor", "kassa qo'llamaydi")));
            _tabBar.Children.Add(b);
        }
        _tabContent.Content = _tabPanels[key];
    }

    private void ApplyCaps()
    {
        foreach (var t in _tabList)
            if (t.Gate != null && t.Cap != null)
            {
                t.Gate.IsVisible = (_live || _ended) && !_caps.Contains(t.Cap);
                t.Panel.IsVisible = !t.Gate.IsVisible; // сама вкладка под плашкой «не поддерживается» не показывается
            }
        SelectTab(_tabKey);
        RefreshEnabled();
    }

    private bool Supports(string cap) => _caps.Contains(cap);

    /// <summary>Что включено: пока сеанс не начался (или уже кончился) ввод, чат и инструменты выключены; без «Только просмотр» и поддержки кассы — тоже.</summary>
    private void RefreshEnabled()
    {
        _viewToggle.IsEnabled = _live && !_cashierViewOnly;
        _quality.IsEnabled = _live;
        _monitor.IsEnabled = _live;
        _keysBtn.IsEnabled = _live && !_cashierViewOnly;
        _fitBtn.IsEnabled = true;
        _fullBtn.IsEnabled = true;
        foreach (var t in _tabList)
            if (t.Key != "notes")
                t.Panel.IsEnabled = _live && (t.Cap == null || Supports(t.Cap));
        foreach (var b in _viewOnlyDisabled)
            b.IsEnabled = _live && !_cashierViewOnly;
        _viewPill.IsVisible = _cashierViewOnly && _live;
        _noteInput.IsEnabled = true;
    }

    private void SetFullscreen(bool on)
    {
        _fullscreen = on;
        _headBorder.IsVisible = !on;
        _side.IsVisible = !on;
        _body.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(460);
        _fsBar.IsVisible = on;
        WindowState = on ? WindowState.FullScreen : WindowState.Normal;
        if (!on)
        {
            // вернули панель, если она была показана поверх экрана
            if (_body.Children.Contains(_side) && Grid.GetColumn(_side) != 1)
                Grid.SetColumn(_side, 1);
            _side.HorizontalAlignment = HorizontalAlignment.Stretch;
            _side.Width = double.NaN;
        }
        Dispatcher.UIThread.Post(PlaceCursor, DispatcherPriority.Background);
        _screen.Focus();
    }

    /// <summary>В полном экране панель «Чат / Касса / …» показывается поверх экрана справа по кнопке «Панель ⟩».</summary>
    private void ToggleSidePanel()
    {
        if (!_fullscreen)
            return;
        if (_side.IsVisible)
        {
            _side.IsVisible = false;
            return;
        }
        _side.IsVisible = true;
        Grid.SetColumn(_side, 0);
        _side.HorizontalAlignment = HorizontalAlignment.Right;
        _side.Width = 460;
    }

    private void ApplyFit()
    {
        _image.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        var vis = _fit ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = vis;
        _scroll.VerticalScrollBarVisibility = vis;
        Dispatcher.UIThread.Post(PlaceCursor, DispatcherPriority.Background);
    }

    private Control ChatPanel()
    {
        _chatScroll.Content = _chatList;
        _chatInput = new TextBox { Watermark = L.T("Сообщение кассиру…", "Кассирге билдирүү…", "Message to the cashier…", "Kasiyere mesaj…", "Kassirga xabar…"), AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_chatInput, L.T("Сообщение кассиру", "Кассирге билдирүү", "Message to the cashier", "Kasiyere mesaj", "Kassirga xabar"));
        var input = _chatInput;
        async Task Send()
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length == 0 || !_live)
                return;
            input.Text = "";
            await SafeSend(new { type = "chat", text });
            AddChat(true, text);
        }
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Send();
            }
        };
        var send = Ui.BtnAsync(L.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), "primary", Send);
        var quick = new WrapPanel();
        foreach (var phrase in new[]
                 {
                     L.T("Здравствуйте! Я подключился, сейчас посмотрю.", "Саламатсызбы! Туташтым, азыр карайм.", "Hello! I'm connected, taking a look now.", "Merhaba! Bağlandım, şimdi bakıyorum.", "Assalomu alaykum! Ulandim, hozir ko'raman."),
                     L.T("Пожалуйста, не трогайте мышь пару минут.", "Сураныч, бир нече мүнөт чычканга тийбеңиз.", "Please don't touch the mouse for a couple of minutes.", "Lütfen birkaç dakika fareye dokunmayın.", "Iltimos, bir necha daqiqa sichqonchaga tegmang."),
                     L.T("Готово, проверьте, пожалуйста.", "Даяр, текшерип көрүңүз.", "Done, please check.", "Tamam, lütfen kontrol edin.", "Tayyor, tekshirib ko'ring."),
                 })
        {
            var text = phrase;
            var b = Ui.Btn(text.Length > 26 ? text[..26] + "…" : text, "secondary", () => input.Text = text);
            b.FontSize = 11;
            b.Padding = new Thickness(8, 4);
            b.Margin = new Thickness(0, 0, 4, 4);
            ToolTip.SetTip(b, text);
            AutomationProperties.SetName(b, text);
            quick.Children.Add(b);
        }
        var bottom = new StackPanel { Spacing = 6, Children = { quick, new DockPanel { Children = { send, input } } } };
        DockPanel.SetDock(send, Dock.Right);
        send.Margin = new Thickness(6, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_chatScroll);
        return dock;
    }

    private void AddChat(bool mine, string text)
    {
        _chatList.Children.Add(new Border
        {
            Background = mine ? Ui.AccentSoft : Ui.InfoSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), MaxWidth = 320,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text },
                    new TextBlock { Text = (mine ? L.T("вы", "сиз", "you", "siz", "siz") : L.T("кассир", "кассир", "cashier", "kasiyer", "kassir")) + " · " + DateTime.Now.ToString("HH:mm"), FontSize = 10, Foreground = Ui.TextSoft },
                },
            },
        });
        _chatScroll.ScrollToEnd();
    }

    // ---------- ожидание ответа кассы: ни вечной загрузки, ни ложного «Готово»

    private const int AnswerSeconds = 10;

    private void Wait(string key, int seconds, Action onTimeout)
    {
        Resolve(key);
        var cts = new CancellationTokenSource();
        _waits[key] = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_waits.TryGetValue(key, out var cur) && cur == cts)
                {
                    _waits.Remove(key);
                    onTimeout();
                }
            });
        });
    }

    private void Resolve(string key)
    {
        if (_waits.Remove(key, out var c))
            c.Cancel();
    }

    private static string NoAnswer(int s) => L.T($"Касса не ответила за {s} с. Попробуйте ещё раз.", $"Касса {s} с ичинде жооп берген жок. Кайра аракет кылыңыз.", $"The till did not answer in {s} s. Try again.",
        $"Kasa {s} sn içinde yanıt vermedi. Tekrar deneyin.", $"Kassa {s} soniyada javob bermadi. Qayta urinib ko'ring.");

    private void RequestInfo()
    {
        if (!_live || !Supports("info"))
            return;
        _info.Text = L.T("Запрашиваю у кассы…", "Кассадан сурап жатам…", "Asking the till…", "Kasaya soruluyor…", "Kassadan so'ralmoqda…");
        Wait("info", AnswerSeconds, () => _info.Text = NoAnswer(AnswerSeconds));
        _ = SafeSend(new { type = "info-request" });
    }

    private void RequestDiag()
    {
        if (!_live || !Supports("diag"))
            return;
        _diag.Children.Clear();
        _diag.Children.Add(Ui.SoftText(L.T("Проверяю… (до 10 с)", "Текшерип жатам… (10 с чейин)", "Checking… (up to 10 s)", "Kontrol ediliyor… (10 sn'ye kadar)", "Tekshirilmoqda… (10 s gacha)")));
        Wait("diag", AnswerSeconds, () =>
        {
            _diag.Children.Clear();
            _diag.Children.Add(Ui.SoftText(NoAnswer(AnswerSeconds)));
        });
        _ = SafeSend(new { type = "diag-request" });
    }

    private Control InfoPanel() => ToolPanel(_info, Ui.BtnAsync(L.T("Обновить сведения", "Маалыматты жаңыртуу", "Refresh info", "Bilgiyi yenile", "Ma'lumotni yangilash"), "secondary",
        () =>
        {
            RequestInfo();
            return Task.CompletedTask;
        }));

    private Control DiagPanel()
    {
        var run = Ui.BtnAsync(L.T("Проверить кассу", "Кассаны текшерүү", "Check the till", "Kasayı kontrol et", "Kassani tekshirish"), "primary", () =>
        {
            RequestDiag();
            return Task.CompletedTask;
        });
        var dock = new DockPanel();
        DockPanel.SetDock(run, Dock.Top);
        run.Margin = new Thickness(0, 6, 0, 8);
        dock.Children.Add(run);
        dock.Children.Add(new ScrollViewer { Content = _diag });
        return dock;
    }

    private Control LogsPanel()
    {
        void LogsRequest(int lines)
        {
            ShowLogsStatus(L.T("Запрашиваю журнал у кассы…", "Кассадан журналды сурап жатам…", "Asking the till for its log…", "Kasadan günlük isteniyor…", "Kassadan jurnal so'ralmoqda…"));
            Wait("logs", 15, () => ShowLogsStatus(NoAnswer(15)));
            _ = SafeSend(new { type = "logs-request", lines });
        }
        var b300 = Ui.BtnAsync(L.T("300 строк", "300 сап", "300 lines", "300 satır", "300 qator"), "secondary", () =>
        {
            LogsRequest(300);
            return Task.CompletedTask;
        });
        var b2000 = Ui.BtnAsync(L.T("2000 строк", "2000 сап", "2000 lines", "2000 satır", "2000 qator"), "secondary", () =>
        {
            LogsRequest(2000);
            return Task.CompletedTask;
        });
        var zip = Ui.BtnAsync(L.T("Скачать все (zip)", "Баарын жүктөө (zip)", "Download all (zip)", "Tümünü indir (zip)", "Hammasini yuklash (zip)"), "primary", async () =>
        {
            ShowLogsStatus(L.T("Касса собирает журналы — файл сохранится в «Загрузки\\NurMarketSupport», когда придёт.", "Касса журналдарды чогултууда — файл келгенде «Жүктөөлөр\\NurMarketSupport» папкасына сакталат.",
                "The till is packing its logs — the file goes to “Downloads\\NurMarketSupport” when it arrives.", "Kasa günlükleri topluyor — dosya gelince «İndirilenler\\NurMarketSupport» klasörüne kaydedilir.",
                "Kassa jurnallarni yig'moqda — fayl kelganda «Yuklanmalar\\NurMarketSupport» papkasiga saqlanadi."));
            Wait("zip", 60, () => ShowLogsStatus(NoAnswer(60)));
            await SafeSend(new { type = "logs-zip-request" });
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 8), Children = { b300, b2000, zip } };
        foreach (var c in bar.Children)
            c.Margin = new Thickness(0, 0, 6, 6);
        var top = new StackPanel { Children = { bar, _logsStatus } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_logs);
        return dock;
    }

    private void ShowLogsStatus(string? text)
    {
        _logsStatus.Text = text ?? "";
        _logsStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private Control FilesPanel()
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        var send = Ui.BtnAsync(L.T("Отправить файл кассиру…", "Кассирге файл жөнөтүү…", "Send a file to the cashier…", "Kasiyere dosya gönder…", "Kassirga fayl yuborish…"), "primary", async () =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = L.T("Файл для кассы", "Касса үчүн файл", "File for the till", "Kasa için dosya", "Kassa uchun fayl") });
            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (path == null || _client == null || !_live || _cashierViewOnly)
                return;
            progress.IsVisible = true;
            progress.Value = 0;
            try
            {
                var id = await _client.SendFileAsync(path, new Progress<double>(v => progress.Value = v));
                // «Отправлен» — не «принят»: подтверждение приходит от кассы (file-saved / file-error), иначе по таймеру — «касса не подтвердила».
                var line = AddFileLine("→ " + System.IO.Path.GetFileName(path) + " — " + L.T("отправлен, ждём подтверждения кассы…", "жөнөтүлдү, кассанын ырастоосун күтөбүз…", "sent, waiting for the till to confirm…", "gönderildi, kasanın onayı bekleniyor…", "yuborildi, kassa tasdiqlashini kutmoqdamiz…"));
                _fileLines[id] = line;
                var name = System.IO.Path.GetFileName(path);
                Wait("file:" + id, 30, () =>
                {
                    line.Text = DateTime.Now.ToString("HH:mm") + "  ✗ " + name + " — " + L.T("касса не подтвердила получение", "касса кабыл алганын ырастаган жок", "the till did not confirm receipt", "kasa alındığını onaylamadı", "kassa qabul qilganini tasdiqlamadi");
                    _fileLines.Remove(id);
                });
            }
            finally
            {
                progress.IsVisible = false;
            }
        });
        _viewOnlyDisabled.Add(send);
        var open = Ui.Btn(L.T("Папка полученных файлов", "Алынган файлдар папкасы", "Received files folder", "Alınan dosyalar klasörü", "Olingan fayllar papkasi"), "secondary", () => OpenFolder(DownloadDir()));
        var top = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 6, 0, 8),
            Children =
            {
                Ui.SoftText(L.T("Файл попадёт на кассу в «Загрузки\\NurMarket поддержка», кассир увидит уведомление. До 100 МБ. Сам файл не запускается.",
                    "Файл кассадагы «Жүктөлүүлөр\\NurMarket поддержка» папкасына түшөт. 100 МБга чейин.", "The file lands in “Downloads\\NurMarket поддержка” on the till; the cashier is notified. Up to 100 MB. It is not run.",
                    "Dosya kasada «İndirilenler\\NurMarket поддержка» klasörüne gider. 100 MB'a kadar.", "Fayl kassada «Yuklanmalar\\NurMarket поддержка» papkasiga tushadi. 100 MB gacha.")),
                send, progress, open,
            },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _files });
        return dock;
    }

    private TextBlock AddFileLine(string text)
    {
        var t = new TextBlock { Text = DateTime.Now.ToString("HH:mm") + "  " + text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
        _files.Children.Insert(0, t);
        return t;
    }

    private Control ClipPanel()
    {
        var set = Ui.BtnAsync(L.T("Положить в буфер кассы", "Кассанын буферине салуу", "Put into the till clipboard", "Kasa panosuna koy", "Kassa buferiga qo'yish"), "primary", async () =>
        {
            // «Готово» — только после ответа кассы: положили → тут же просим буфер обратно и сверяем.
            _clipSetText = _clip.Text ?? "";
            await SafeSend(new { type = "clipboard-set", text = _clipSetText });
            Wait("clip", 8, () =>
            {
                _clipSetText = null;
                _toast.Show(L.T("Касса не подтвердила, что текст попал в буфер. Попробуйте ещё раз.", "Текст кассанын буферине түшкөнүн касса ырастаган жок. Кайра аракет кылыңыз.",
                    "The till did not confirm that the text reached its clipboard. Try again.", "Kasa metnin panoya girdiğini onaylamadı. Tekrar deneyin.", "Kassa matn buferga tushganini tasdiqlamadi. Qayta urinib ko'ring."), true);
            });
            await SafeSend(new { type = "clipboard-get" });
        });
        var type = Ui.BtnAsync(L.T("Напечатать на кассе", "Кассада терүү", "Type on the till", "Kasada yaz", "Kassada yozish"), "secondary",
            () => SafeSend(new { type = "text", text = _clip.Text ?? "" }));
        ToolTip.SetTip(type, L.T("Текст печатается туда, где сейчас курсор на кассе (кириллица тоже).", "Текст кассадагы курсор турган жерге терилет.", "Typed where the cursor is on the till (Cyrillic too).",
            "İmlecin olduğu yere yazılır.", "Kursor turgan joyga yoziladi."));
        var get = Ui.BtnAsync(L.T("Взять буфер кассы", "Кассанын буферин алуу", "Get the till clipboard", "Kasa panosunu al", "Kassa buferini olish"), "secondary", async () =>
        {
            _clipSetText = null;
            Wait("clip", 8, () => _toast.Show(NoAnswer(8), true));
            await SafeSend(new { type = "clipboard-get" });
        });
        var mine = Ui.Btn(L.T("Скопировать себе", "Өзүмө көчүрүү", "Copy to my clipboard", "Kendime kopyala", "O'zimga nusxalash"), "ghost", async () =>
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(_clip.Text ?? "");
        });
        _viewOnlyDisabled.Add(set);
        _viewOnlyDisabled.Add(type);
        _viewOnlyDisabled.Add(get);
        var bar = new WrapPanel { Children = { set, type, get, mine } };
        foreach (var c in bar.Children)
            c.Margin = new Thickness(0, 0, 6, 6);
        return new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { _clip, bar } };
    }

    private Control NotesPanel()
    {
        var save = Ui.BtnAsync(L.T("Сохранить заметку", "Эскертмени сактоо", "Save note", "Notu kaydet", "Eslatmani saqlash"), "primary", async () =>
        {
            var text = (_noteInput.Text ?? "").Trim();
            if (text.Length == 0)
                return;
            await _api.AddNoteAsync(_company, _machine, _serverSessionId, text);
            _noteInput.Text = "";
            _toast.Show(L.T("Заметка сохранена — её увидят все операторы.", "Эскертме сакталды.", "Note saved — all operators will see it.", "Not kaydedildi.", "Eslatma saqlandi."), false);
            await LoadNotesAsync();
        });
        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 8), Children = { _noteInput, save, Ui.Caption(L.T("Прошлые обращения этой кассы", "Бул кассанын мурунку кайрылуулары", "Earlier cases of this till", "Bu kasanın önceki talepleri", "Bu kassaning oldingi murojaatlari")) } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _notes });
        return dock;
    }

    private async Task LoadNotesAsync()
    {
        try
        {
            var list = await _api.NotesAsync(_company, _machine, 50);
            _notes.Children.Clear();
            if (list.Count == 0)
                _notes.Children.Add(Ui.SoftText(L.T("Раньше по этой кассе заметок не было.", "Мурун бул касса боюнча эскертме болгон эмес.", "No earlier notes for this till.", "Bu kasa için önceki not yok.", "Bu kassa bo'yicha oldin eslatma bo'lmagan.")));
            foreach (var n in list)
                _notes.Children.Add(MainWindow.NoteCard(n));
        }
        catch (Exception ex)
        {
            _notes.Children.Clear();
            _notes.Children.Add(Ui.SoftText(ex.Message));
        }
    }

    private static Control ToolPanel(Control content, Control button)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Top);
        button.Margin = new Thickness(0, 6, 0, 8);
        dock.Children.Add(button);
        dock.Children.Add(content);
        return dock;
    }

    // ------------------------------------------------------------------ соединение

    private async Task ConnectAsync()
    {
        // Повторное подключение (после ошибки): прежний клиент закрывается, его события игнорируются.
        var previous = _client;
        _client = null;
        if (previous != null)
            _ = previous.DisposeAsync().AsTask();
        _live = false;
        _ended = false;
        _caps = new HashSet<string>();
        RefreshEnabled();
        SetStatus(L.T("Подключаюсь к серверу поддержки…", "Колдоо серверине туташып жатам…", "Connecting to the support server…", "Destek sunucusuna bağlanılıyor…", "Yordam serveriga ulanmoqda…"), CancelButton());
        var c = new SessionClient(_api.OperatorWs, _api.Token);
        _client = c;
        bool Mine() => ReferenceEquals(_client, c);
        void OnUi(Action a) => Dispatcher.UIThread.Post(() =>
        {
            if (Mine())
                a();
        });
        c.Error += (code, left) => Dispatcher.UIThread.Post(async () =>
        {
            if (Mine())
                await OnErrorAsync(code, left);
        });
        c.WaitingConsent += () => OnUi(() =>
        {
            _consentDeadline = DateTime.UtcNow.AddSeconds(90);
            _countdown.Start();
            UpdateHeader(L.T("ждём «Разрешить» на кассе", "кассада «Уруксат берүү» күтүлүүдө", "waiting for “Allow” on the till", "kasada «İzin ver» bekleniyor", "kassada «Ruxsat berish» kutilmoqda"));
            SetStatus(WaitingText(), CancelButton());
        });
        c.Started += m => OnUi(() => OnStarted(m));
        c.ClientActive += active => OnUi(() => SetClientActive(active));
        c.ViewOnlyByCashier += () => OnUi(() => SetCashierViewOnly(true));
        c.ScreenSize += (w, h, monitors) => OnUi(() =>
        {
            var old = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
            old?.Dispose(); // 2026-10-27: прежняя картинка освобождается (раньше копилась при каждой смене разрешения)
            _pausedBanner.IsVisible = false;
            SetMonitors(monitors);
        });
        c.ScreenPaused += _ => OnUi(() => _pausedBanner.IsVisible = _live);
        c.FrameReady += (x, y, w, h, frame) => Dispatcher.UIThread.Post(() =>
        {
            if (!Mine())
                return;
            _pausedBanner.IsVisible = false;
            DrawFrame(x, y, w, h, frame);
        }, DispatcherPriority.Render);
        c.Cursor += (x, y) => OnUi(() =>
        {
            _cx = x;
            _cy = y;
            PlaceCursor();
        });
        c.Chat += text => OnUi(() =>
        {
            AddChat(false, text);
            if (_tabKey != "chat")
                _toast.Show(L.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + text, false);
        });
        c.Info += text => OnUi(() =>
        {
            Resolve("info");
            _info.Text = text;
        });
        c.Logs += text => OnUi(() =>
        {
            Resolve("logs");
            ShowLogsStatus(null);
            _logs.Text = text;
            _logs.CaretIndex = text.Length;
        });
        c.Diag += items => OnUi(() =>
        {
            Resolve("diag");
            RenderDiag(items);
        });
        c.Clipboard += text => OnUi(() =>
        {
            Resolve("clip");
            if (_clipSetText != null)
            {
                // Ответ на проверку после «Положить в буфер»: «Готово» только если касса вернула именно наш текст.
                var same = text == _clipSetText;
                _clipSetText = null;
                _toast.Show(same
                    ? L.T("Готово — касса подтвердила: текст в её буфере, можно вставить Ctrl+V.", "Даяр — касса ырастады: текст анын буферинде, Ctrl+V менен коюуга болот.", "Done — the till confirmed the text is in its clipboard; paste with Ctrl+V.",
                        "Tamam — kasa onayladı: metin panosunda, Ctrl+V ile yapıştırın.", "Tayyor — kassa tasdiqladi: matn uning buferida, Ctrl+V bilan qo'ying.")
                    : L.T("Касса приняла запрос, но в её буфере другой текст. Попробуйте ещё раз.", "Касса суроону кабыл алды, бирок анын буферинде башка текст. Кайра аракет кылыңыз.", "The till accepted the request but its clipboard holds different text. Try again.",
                        "Kasa isteği aldı ama panosunda farklı metin var. Tekrar deneyin.", "Kassa so'rovni oldi, lekin buferida boshqa matn bor. Qayta urinib ko'ring."), !same);
                return;
            }
            _clip.Text = text;
            _toast.Show(L.T("Буфер кассы получен.", "Кассанын буфери алынды.", "Got the till clipboard.", "Kasa panosu alındı.", "Kassa buferi olindi."), false);
        });
        c.FileFromHost += (name, bytes) => OnUi(() =>
        {
            Resolve("zip");
            ShowLogsStatus(null);
            SaveFromHost(name, bytes);
        });
        c.FileStatus += (id, status, detail) => OnUi(() =>
        {
            Resolve("file:" + id);
            var text = DateTime.Now.ToString("HH:mm") + "  " + (status == "saved"
                ? "✓ " + L.T("касса сохранила: ", "касса сактады: ", "saved on the till: ", "kasa kaydetti: ", "kassa saqladi: ") + detail
                : "✗ " + L.T("касса не приняла файл: ", "касса файлды кабыл алган жок: ", "the till rejected the file: ", "kasa dosyayı kabul etmedi: ", "kassa faylni qabul qilmadi: ") + detail);
            if (_fileLines.Remove(id, out var line))
                line.Text = text;
            else
                AddFileLine(text.Length > 7 ? text[7..] : text);
        });
        c.Stats += (fps, kbps) => OnUi(() => _stats.Text = $"{fps:0} {L.T("к/с", "к/с", "fps", "fps", "k/s")} · {kbps / 1000:0.0} {L.T("Мбит/с", "Мбит/с", "Mbit/s", "Mbit/s", "Mbit/s")}");
        c.Ended += reason => OnUi(() => OnEnded(reason));
        try
        {
            await c.ConnectAsync();
            await Join();
        }
        catch (Exception ex)
        {
            ShowFailure(L.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ", "Destek sunucusuna bağlantı yok: ", "Server bilan aloqa yo'q: ") + ex.Message, retry: true);
        }
    }

    private Button CancelButton() => Ui.Btn(L.T("Отменить", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), "secondary", () => _ = EndAndCloseAsync());

    private string WaitingText()
    {
        var left = Math.Max(0, (int)Math.Ceiling((_consentDeadline - DateTime.UtcNow).TotalSeconds));
        return L.T($"PIN верный. Ждём, когда кассир нажмёт «Разрешить»… осталось {left} с",
            $"PIN туура. Кассир «Уруксат берүү» басканын күтөбүз… калды {left} с",
            $"The PIN is correct. Waiting for the cashier to press “Allow”… {left} s left",
            $"PIN doğru. Kasiyerin «İzin ver»e basması bekleniyor… kalan {left} sn",
            $"PIN to'g'ri. Kassir «Ruxsat berish»ni bosishini kutyapmiz… {left} s qoldi");
    }

    private void UpdateWaitingText()
    {
        if (_live || _ended || !_status.IsVisible)
        {
            _countdown.Stop();
            return;
        }
        if (_consentDeadline != default)
            _statusText.Text = WaitingText();
    }

    private Task Join() => _sessionId != null ? _client!.JoinBySessionAsync(_sessionId, _pin) : _client!.JoinByCodeAsync(_code!, _pin);

    /// <summary>Ошибка до начала сеанса: понятный текст и кнопки «Ввести PIN заново» (если имеет смысл) и «Закрыть».</summary>
    private void ShowFailure(string text, bool retry)
    {
        _countdown.Stop();
        var buttons = new List<Button>();
        if (retry)
            buttons.Add(Ui.Btn(L.T("Ввести PIN заново", "PIN'ди кайра киргизүү", "Enter the PIN again", "PIN'i yeniden gir", "PIN'ni qayta kiritish"), "primary", () => _ = RetryAsync()));
        buttons.Add(Ui.Btn(L.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), "secondary", () => Close()));
        SetStatus(text, buttons.ToArray());
    }

    private async Task RetryAsync()
    {
        var pin = await Dialogs.AskAsync(this, L.T("PIN кассы", "Кассанын PIN'и", "Till PIN", "Kasa PIN'i", "Kassa PIN"),
            L.T("Попросите кассира назвать PIN (4 цифры) с экрана «Тех. поддержка».", "Кассирден «Тех. колдоо» экранындагы PIN'ди (4 сан) айтып берүүнү сураңыз.",
                "Ask the cashier for the PIN (4 digits) from the “Support” screen.", "Kasiyerden «Destek» ekranındaki PIN'i (4 hane) isteyin.", "Kassirdan «Texnik yordam» ekranidagi PIN'ni (4 raqam) so'rang."), "0000", digits: 4);
        if (pin == null)
            return;
        _pin = pin;
        await ConnectAsync();
    }

    private async Task OnErrorAsync(string code, int? left)
    {
        if (code == "bad-pin")
        {
            var attempts = left is { } n
                ? L.T($" Осталось попыток: {n}.", $" Калган аракет: {n}.", $" Attempts left: {n}.", $" Kalan deneme: {n}.", $" Qolgan urinish: {n}.")
                : "";
            var pin = await Dialogs.AskAsync(this, L.T("Неверный PIN", "PIN туура эмес", "Wrong PIN", "Yanlış PIN", "Noto'g'ri PIN"),
                L.T("Попросите кассира назвать PIN ещё раз.", "Кассирден PIN'ди кайра сураңыз.", "Ask the cashier for the PIN again.", "Kasiyerden PIN'i tekrar isteyin.", "Kassirdan PIN'ni qayta so'rang.") + attempts, "0000", digits: 4);
            if (pin == null)
            {
                Close();
                return;
            }
            _pin = pin;
            await Join();
            return;
        }
        if (code == "unauthorized")
            _api.NotifyExpired();
        var retry = code is "busy" or "rate-limited" or "bad-request";
        ShowFailure(code switch
        {
            "no-session" => L.T("Такой кассы нет или код уже использован. Попросите кассира получить новый код.", "Мындай касса жок же код колдонулган.", "No such till, or the code was already used. Ask for a new code.", "Böyle kasa yok veya kod kullanıldı.", "Bunday kassa yo'q yoki kod ishlatilgan."),
            "locked" => Reasons.Long("locked"),
            "busy" => L.T("К этой кассе уже подключается другой оператор.", "Бул кассага башка оператор туташууда.", "Another operator is already connecting to this till.", "Bu kasaya başka operatör bağlanıyor.", "Bu kassaga boshqa operator ulanmoqda."),
            "declined" => L.T("Кассир отказал в доступе (или не ответил). Код сгорел: пусть кассир получит новый.", "Кассир уруксат берген жок (же жооп берген жок). Код күйдү: кассир жаңысын алсын.", "The cashier declined (or did not answer). The code is used up: the cashier needs a new one.",
                "Kasiyer reddetti (veya yanıt vermedi). Kod yandı: kasiyer yenisini almalı.", "Kassir rad etdi (yoki javob bermadi). Kod yondi: kassir yangisini olsin."),
            "expired" => Reasons.Long("expired"),
            "rate-limited" => L.T("Слишком много попыток — подождите минуту и введите PIN заново.", "Өтө көп аракет — бир мүнөт күтүп, PIN'ди кайра киргизиңиз.", "Too many attempts — wait a minute and enter the PIN again.", "Çok fazla deneme — bir dakika bekleyip PIN'i yeniden girin.", "Juda ko'p urinish — bir daqiqa kuting va PIN'ni qayta kiriting."),
            "unauthorized" => Reasons.Long("revoked"),
            _ => L.T("Сервер ответил: ", "Сервер жооп берди: ", "The server replied: ", "Sunucu yanıtı: ", "Server javobi: ") + code,
        }, retry);
    }

    private void OnStarted(JsonElement m)
    {
        _live = true;
        _countdown.Stop();
        _serverSessionId = MainWindow.Str(m, "sessionId");
        _caps = new HashSet<string>();
        if (m.TryGetProperty("host", out var h))
        {
            if (MainWindow.Str(h, "company") is { Length: > 0 } company)
                _company = company;
            if (MainWindow.Str(h, "machine") is { Length: > 0 } machine)
                _machine = machine;
            if (h.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
                foreach (var c in caps.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cs)
                        _caps.Add(cs);
        }
        UpdateHeader(L.T("сеанс идёт", "сеанс жүрүп жатат", "session live", "oturum canlı", "seans davom etmoqda") + (h.ValueKind == JsonValueKind.Object ? " · NurCRM " + MainWindow.Str(h, "version") : ""));
        SetStatus(null);
        _moveTimer.Start();
        ApplyCaps();
        // «Только просмотр» от кассира: у старого сервера поля нет — тогда как раньше.
        SetCashierViewOnly(m.TryGetProperty("viewOnly", out var vo) && vo.ValueKind == JsonValueKind.True);
        _screen.Focus();
        _ = SafeSend(new { type = "quality", value = Settings.Current.Quality });
        // Просим сведения и диагностику только у кассы, которая это умеет: старая касса молчит, а вечное «Проверяю…» — ложь.
        RequestInfo();
        RequestDiag();
        _ = LoadNotesAsync();
    }

    private void SetCashierViewOnly(bool on)
    {
        if (_cashierViewOnly == on)
        {
            RefreshEnabled();
            return;
        }
        _cashierViewOnly = on;
        if (on)
        {
            ReleaseInput();
            _syncing = true;
            _viewOnly = true;
            _viewToggle.IsChecked = true;
            _syncing = false;
            _pendingMove = null;
        }
        RefreshEnabled();
        if (on)
            _toast.Show(L.T("Кассир разрешил только просмотр — мышь, клавиши, буфер и файлы выключены.", "Кассир көрүүгө гана уруксат берди — чычкан, баскычтар, буфер жана файлдар өчүрүлдү.", "The cashier allowed view only — mouse, keys, clipboard and files are off.",
                "Kasiyer yalnızca izlemeye izin verdi — fare, tuşlar, pano ve dosyalar kapalı.", "Kassir faqat ko'rishga ruxsat berdi — sichqoncha, tugmalar, bufer va fayllar o'chirilgan."), false);
    }

    private void SetMonitors(int n)
    {
        if (n == _monitors && _monitor.IsVisible == (n > 1))
            return;
        _monitors = n;
        _syncing = true;
        _monitor.Items.Clear();
        for (var i = 0; i < n; i++)
            _monitor.Items.Add(new ComboBoxItem { Content = L.T($"Монитор {i + 1}", $"Монитор {i + 1}", $"Monitor {i + 1}", $"Monitör {i + 1}", $"Monitor {i + 1}") });
        _monitor.SelectedIndex = 0;
        _monitor.IsVisible = n > 1;
        _syncing = false;
    }

    private void OnEnded(string reason)
    {
        _live = false;
        _ended = true;
        _countdown.Stop();
        _moveTimer.Stop();
        _clientActive = false;
        _clientBanner.IsVisible = false;
        _pausedBanner.IsVisible = false;
        foreach (var k in _waits.Keys.ToList())
            Resolve(k);
        ReleaseInput();
        RefreshEnabled();
        ApplyCaps();
        UpdateHeader(L.T("сеанс завершён", "сеанс аяктады", "session ended", "oturum bitti", "seans tugadi"));
        if (!IsVisible)
            return;
        if (_fullscreen)
            SetFullscreen(false);
        SetStatus(Reasons.Long(reason) + "\n" + L.T("Запишите итог во вкладке «Заметки».", "Жыйынтыкты «Эскертмелер» өтмөгүнө жазыңыз.", "Write the outcome in the “Notes” tab.", "Sonucu «Notlar» sekmesine yazın.", "Natijani «Eslatmalar» bo'limiga yozing."),
            Ui.Btn(L.T("Закрыть окно", "Терезени жабуу", "Close the window", "Pencereyi kapat", "Oynani yopish"), "secondary", () => Close()));
        SelectTab("notes");
        _ = LoadNotesAsync();
    }

    /// <summary>Завершить сеанс (если идёт) и закрыть окно.</summary>
    public async Task EndAndCloseAsync()
    {
        if (_client != null)
            await _client.EndAsync();
        Close();
    }

    private async Task SafeSend(object o)
    {
        if (_client == null || !_client.IsOpen)
            return;
        try
        {
            await _client.SendAsync(o);
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, true);
        }
    }

    private void RenderDiag(List<(string Name, bool? Ok, string Detail)> items)
    {
        _diag.Children.Clear();
        foreach (var (name, ok, detail) in items)
        {
            var (mark, fg, bg) = ok switch
            {
                true => ("✓", Ui.Success, Ui.SuccessSoft),
                false => ("✗", Ui.Danger, Ui.DangerSoft),
                _ => ("•", Ui.TextMid, Ui.Soft),
            };
            _diag.Children.Add(new Border
            {
                Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7),
                Child = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { Text = mark, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = fg, Width = 22, [DockPanel.DockProperty] = Dock.Left },
                        new StackPanel { Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = Ui.Text }, new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextMid, FontSize = 12 } } },
                    },
                },
            });
        }
    }

    private static string DownloadDir() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NurMarketSupport");

    private void SaveFromHost(string name, byte[] bytes)
    {
        try
        {
            var dir = DownloadDir();
            Directory.CreateDirectory(dir);
            var safe = string.Concat(System.IO.Path.GetFileName(name).Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (safe.Length == 0)
                safe = "file";
            // 2026-10-27: файл с тем же именем не затирается молча — к имени добавляется (1), (2)…
            var path = System.IO.Path.Combine(dir, safe);
            for (var i = 1; File.Exists(path); i++)
                path = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(safe)} ({i}){System.IO.Path.GetExtension(safe)}");
            File.WriteAllBytes(path, bytes);
            AddFileLine("← " + System.IO.Path.GetFileName(path) + $" ({bytes.Length / 1024} {L.T("КБ", "КБ", "KB", "KB", "KB")}) — " + L.T("сохранён", "сакталды", "saved", "kaydedildi", "saqlandi"));
            _toast.Show(L.T("Файл с кассы сохранён: ", "Кассадан файл сакталды: ", "File from the till saved: ", "Kasadan dosya kaydedildi: ", "Kassadan fayl saqlandi: ") + path, false);
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, true);
        }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // проводник недоступен
        }
    }

    // ------------------------------------------------------------------ экран

    private void DrawFrame(int x, int y, int w, int h, uint frame)
    {
        if (_bitmap is { } bmp && _client is { } c)
        {
            using (var fb = bmp.Lock())
            {
                lock (c.FrameLock)
                {
                    if (c.W == bmp.PixelSize.Width && c.H == bmp.PixelSize.Height && x >= 0 && y >= 0 && x + w <= c.W && y + h <= c.H)
                    {
                        for (var row = y; row < y + h; row++)
                            System.Runtime.InteropServices.Marshal.Copy(c.Frame, (row * c.W + x) * 4, fb.Address + row * fb.RowBytes + x * 4, w * 4);
                    }
                }
            }
            _image.InvalidateVisual();
        }
        _ = SafeSend(new { type = "ack", frame });
    }

    /// <summary>Курсор кассы (на снимке экрана его нет) — стрелка поверх картинки.</summary>
    private void PlaceCursor()
    {
        if (_client is not { W: > 0 } c || _cx < 0 || _image.Bounds.Width <= 0)
        {
            _cursor.IsVisible = false;
            return;
        }
        var origin = _image.TranslatePoint(new Point(0, 0), _overlay) ?? new Point(0, 0);
        var sx = _image.Bounds.Width / c.W;
        var sy = _image.Bounds.Height / c.H;
        Canvas.SetLeft(_cursor, origin.X + _cx * sx);
        Canvas.SetTop(_cursor, origin.Y + _cy * sy);
        _cursor.IsVisible = true;
    }

    /// <summary>Точка на экране кассы под указателем; null — указатель вне картинки.</summary>
    private (int X, int Y)? HostPoint(PointerEventArgs e)
    {
        if (_client is not { W: > 0 } c || _image.Bounds.Width <= 0 || _image.Bounds.Height <= 0)
            return null;
        var p = e.GetPosition(_image);
        var x = (int)(p.X * c.W / _image.Bounds.Width);
        var y = (int)(p.Y * c.H / _image.Bounds.Height);
        if (x < 0 || y < 0 || x >= c.W || y >= c.H)
            return null;
        return (x, y);
    }

    /// <summary>То же, но за краем картинки координаты прижимаются к краю: отпускание кнопки за краем не теряется (кнопка не залипает на кассе).</summary>
    private (int X, int Y)? HostPointClamped(PointerEventArgs e)
    {
        if (_client is not { W: > 0 } c || _image.Bounds.Width <= 0 || _image.Bounds.Height <= 0)
            return null;
        var p = e.GetPosition(_image);
        var x = Math.Clamp((int)Math.Floor(p.X * c.W / _image.Bounds.Width), 0, c.W - 1);
        var y = Math.Clamp((int)Math.Floor(p.Y * c.H / _image.Bounds.Height), 0, c.H - 1);
        return (x, y);
    }

    // 2026-10-26: пока кассир работает сам, ввод оператора не отправляем (касса его всё равно не исполнит).
    // 2026-10-27: и при «Только просмотр» кассира (ввод выключен совсем).
    private bool CanControl => _live && !_viewOnly && !_cashierViewOnly && !_clientActive;

    private void SetClientActive(bool active)
    {
        if (_clientActive == active)
            return;
        _clientActive = active;
        _pendingMove = null;
        _clientBanner.IsVisible = active && _live;
        if (active)
            ReleaseInput();
    }

    private static int ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
        _ => -1, // боковые кнопки мыши (назад/вперёд) на кассу не передаются: раньше уходили левым щелчком
    };

    private void AttachInput()
    {
        _image.PointerMoved += (_, e) =>
        {
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            // Не чаще ~60 раз в секунду; последнее положение не теряется (его досылает таймер 16 мс).
            _pendingMove = p;
            _lastPoint = p;
        };
        _image.PointerPressed += (_, e) =>
        {
            _screen.Focus();
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            var button = ButtonOf(e.GetCurrentPoint(_image).Properties.PointerUpdateKind);
            if (button < 0)
            {
                e.Handled = true;
                return;
            }
            FlushMove();
            e.Pointer.Capture(_image); // отпускание придёт сюда, даже если указатель ушёл за край картинки
            _buttonsDown.Add(button);
            _lastPoint = p;
            _ = SafeSend(new { type = "mouse", action = "down", x = p.X, y = p.Y, button });
            e.Handled = true;
        };
        _image.PointerReleased += (_, e) =>
        {
            var button = e.InitialPressMouseButton switch { MouseButton.Left => 0, MouseButton.Right => 2, MouseButton.Middle => 1, _ => -1 };
            if (button < 0)
                return;
            if (!_buttonsDown.Remove(button))
            {
                if (_buttonsDown.Count == 0)
                    e.Pointer.Capture(null);
                return; // это нажатие мы не отправляли (ввод был выключен)
            }
            // «up» уходит ДО снятия захвата: снятие вызывает PointerCaptureLost, а он отпускает кнопки в прежней точке.
            var p = HostPointClamped(e) ?? _lastPoint;
            _ = SafeSend(new { type = "mouse", action = "up", x = p.X, y = p.Y, button });
            if (_buttonsDown.Count == 0)
                e.Pointer.Capture(null);
            e.Handled = true;
        };
        _image.PointerCaptureLost += (_, _) => ReleaseMouse();
        _image.PointerWheelChanged += (_, e) =>
        {
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            _ = SafeSend(new { type = "mouse", action = "wheel", x = p.X, y = p.Y, delta = (int)Math.Round(e.Delta.Y * 120) });
            e.Handled = true;
        };
        _image.LayoutUpdated += (_, _) => PlaceCursor();
        _screen.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!CanControl)
                return;
            e.Handled = true;
            if (Code(e) is not { } code)
                return;
            _keysDown.Add(code);
            _ = SafeSend(new { type = "key", action = "down", code, key = e.KeySymbol ?? "" });
        }, RoutingStrategies.Tunnel);
        _screen.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (!CanControl)
                return;
            e.Handled = true;
            if (Code(e) is not { } code)
                return;
            _keysDown.Remove(code);
            _ = SafeSend(new { type = "key", action = "up", code, key = e.KeySymbol ?? "" });
        }, RoutingStrategies.Tunnel);
        _screen.LostFocus += (_, _) => ReleaseInput();
        Deactivated += (_, _) => ReleaseInput();
    }

    private void FlushMove()
    {
        if (_pendingMove is not { } p)
            return;
        _pendingMove = null;
        if (!CanControl)
            return;
        _ = SafeSend(new { type = "mouse", action = "move", x = p.X, y = p.Y });
    }

    /// <summary>Физическая клавиша Avalonia → KeyboardEvent.code (что понимает касса: KeyA, Digit1, Numpad1, ArrowLeft…).</summary>
    private static string? Code(KeyEventArgs e)
    {
        var name = e.PhysicalKey.ToString();
        if (e.PhysicalKey == PhysicalKey.None)
            return null;
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0]))
            return "Key" + name;
        if (name.StartsWith("NumPad", StringComparison.Ordinal))
            return "Numpad" + name[6..];
        return name;
    }

    /// <summary>Отпустить на кассе зажатые кнопки мыши (в последней известной точке).</summary>
    private void ReleaseMouse()
    {
        if (_buttonsDown.Count == 0)
            return;
        foreach (var b in _buttonsDown.ToList())
            _ = SafeSend(new { type = "mouse", action = "up", x = _lastPoint.X, y = _lastPoint.Y, button = b });
        _buttonsDown.Clear();
    }

    /// <summary>Потеря фокуса, «только просмотр», кассир заработал сам — отпустить на кассе всё, что держали нажатым (клавиши и кнопки мыши).</summary>
    private void ReleaseInput()
    {
        ReleaseMouse();
        if (_keysDown.Count == 0)
            return;
        foreach (var code in _keysDown.ToList())
            _ = SafeSend(new { type = "key", action = "up", code, key = "" });
        _keysDown.Clear();
    }

    private async Task SendComboAsync(string[] combo)
    {
        if (!CanControl)
        {
            _toast.Show(L.T("Ввод сейчас выключен: «Только просмотр», кассир работает или сеанс не идёт.", "Киргизүү азыр өчүк: «Көрүү гана», кассир иштеп жатат же сеанс жок.", "Input is off right now: view only, the cashier is working or there is no live session.",
                "Giriş şu an kapalı: yalnızca izleme, kasiyer çalışıyor veya oturum yok.", "Kiritish hozir o'chiq: faqat ko'rish, kassir ishlayapti yoki seans yo'q."), true);
            return;
        }
        foreach (var code in combo)
            await SafeSend(new { type = "key", action = "down", code, key = "" });
        await Task.Delay(60);
        foreach (var code in combo.Reverse())
            await SafeSend(new { type = "key", action = "up", code, key = "" });
        _screen.Focus();
    }
}

/// <summary>Команда для пунктов меню.</summary>
internal sealed class Relay(Func<Task> run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await run();
}
