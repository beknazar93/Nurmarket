using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace NurRemoteSupport.Operator;

/// <summary>
/// Оформление по брендбуку NUR CRM (docs/Брендбук NUR CRM — применение в программах, 2026-10-22): Golden Light #FFB208,
/// Deep Night #191919, фон #F4F4F4, светлая и тёмная тема. 2026-10-27 (пакет О2): цвета — изменяемые кисти; переключение темы
/// (<see cref="SetDark"/>) меняет их цвет, и открытые окна перекрашиваются сами, без пересборки.
/// </summary>
internal static class Ui
{
    public static readonly SolidColorBrush Window = new();
    public static readonly SolidColorBrush Panel = new();
    public static readonly SolidColorBrush Soft = new();
    public static readonly SolidColorBrush Border = new();
    public static readonly SolidColorBrush BorderStrong = new();
    public static readonly SolidColorBrush Text = new();
    public static readonly SolidColorBrush TextSoft = new();
    public static readonly SolidColorBrush TextMid = new();
    public static readonly SolidColorBrush Accent = new();
    public static readonly SolidColorBrush AccentStrong = new();
    public static readonly SolidColorBrush AccentSoft = new();
    /// <summary>Текст на жёлтом (белый на #FFB208 не проходит контраст — брендбук, разд. 1).</summary>
    public static readonly SolidColorBrush OnAccent = new();
    /// <summary>Левое меню: Deep Night.</summary>
    public static readonly SolidColorBrush Navy = new();
    public static readonly SolidColorBrush NavyHover = new();
    public static readonly SolidColorBrush NavyText = new();
    public static readonly SolidColorBrush Success = new();
    public static readonly SolidColorBrush SuccessSoft = new();
    public static readonly SolidColorBrush Danger = new();
    public static readonly SolidColorBrush DangerSoft = new();
    /// <summary>Текст предупреждений (по светлому фону плашки).</summary>
    public static readonly SolidColorBrush Warn = new();
    public static readonly SolidColorBrush WarnSoft = new();
    /// <summary>Заливка плашки-предупреждения: Orange Flame, текст на ней — <see cref="OnAccent"/> (тёмный).</summary>
    public static readonly SolidColorBrush WarnFill = new();
    public static readonly SolidColorBrush Info = new();
    public static readonly SolidColorBrush InfoSoft = new();
    public static readonly SolidColorBrush Focus = new();

    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    static Ui() => Apply(false);

    private static void Apply(bool dark)
    {
        IsDark = dark;
        void S(SolidColorBrush b, string light, string darkHex) => b.Color = Color.Parse(dark ? darkHex : light);
        S(Window, "#F4F4F4", "#191919");
        S(Panel, "#FFFFFF", "#222222");
        S(Soft, "#F0F0F0", "#2A2A2A");
        S(Border, "#DDDDDD", "#3D3D3D");
        S(BorderStrong, "#AAAAAA", "#666666");
        S(Text, "#191919", "#F5F5F5");
        S(TextSoft, "#666666", "#AAAAAA");
        S(TextMid, "#3D3D3D", "#CCCCCC");
        S(Accent, "#FFB208", "#FFB208");
        S(AccentStrong, "#E69F00", "#FFC84D");
        S(AccentSoft, "#FFF1DB", "#3A2D10");
        S(OnAccent, "#191919", "#191919");
        S(Navy, "#191919", "#101010");
        S(NavyHover, "#2E2E2E", "#2A2A2A");
        S(NavyText, "#FFFFFF", "#F5F5F5");
        S(Success, "#0E7A3A", "#4ADE80");
        S(SuccessSoft, "#E7F6EC", "#12301D");
        S(Danger, "#C0362C", "#FB6B63");
        S(DangerSoft, "#FDECEA", "#3A1A1C");
        S(Warn, "#B54708", "#F79B4A");
        S(WarnSoft, "#FEF3E2", "#3A2410");
        S(WarnFill, "#F77B14", "#F77B14");
        S(Info, "#3538CD", "#9AA0FF");
        S(InfoSoft, "#EEF4FF", "#262B45");
        S(Focus, "#191919", "#FFB208");
    }

    /// <summary>Тёмная/светлая тема: цвета кистей и тема Fluent (поля ввода, меню) меняются сразу.</summary>
    public static void SetDark(bool dark)
    {
        Apply(dark);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeChanged?.Invoke();
    }

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>Брендовые цвета Fluent-элементов (переключатель, выбор в списке, полоса прогресса, рамка фокуса).</summary>
    public static void AddBrandResources(IResourceDictionary res)
    {
        res["ToggleSwitchFillOn"] = Accent;
        res["ToggleSwitchFillOnPointerOver"] = AccentStrong;
        res["ToggleSwitchFillOnPressed"] = AccentStrong;
        res["ToggleSwitchKnobFillOn"] = OnAccent;
        res["ToggleSwitchKnobFillOnPointerOver"] = OnAccent;
        res["ToggleSwitchKnobFillOnPressed"] = OnAccent;
        res["ToggleSwitchStrokeOn"] = Accent;
        res["ToggleSwitchStrokeOnPointerOver"] = AccentStrong;
        res["ToggleSwitchStrokeOnPressed"] = AccentStrong;
        res["ComboBoxItemBackgroundSelected"] = AccentSoft;
        res["ComboBoxItemBackgroundSelectedPointerOver"] = AccentSoft;
        res["ComboBoxItemBackgroundSelectedPressed"] = AccentSoft;
        res["ComboBoxItemBackgroundPointerOver"] = Soft;
        res["ComboBoxBorderBrushFocused"] = Focus;
        res["TextControlBorderBrushFocused"] = Focus;
        res["TextControlSelectionHighlightColor"] = Accent;
        res["SystemControlHighlightAccentBrush"] = Accent;
        res["ProgressBarForeground"] = Accent;
        res["SystemAccentColor"] = Color.Parse("#FFB208");
    }

    public static void AddStyles(Styles styles)
    {
        // Цели касания и клика — не меньше 44 px (брендбук/аудит 27.10: «кнопки ≥ 44 px»).
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Layoutable.MinHeightProperty, 44.0),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 10)),
                new Setter(TemplatedControl.BorderBrushProperty, BorderStrong),
                new Setter(TemplatedControl.BackgroundProperty, Panel),
                new Setter(TemplatedControl.ForegroundProperty, Text),
            },
        });
        styles.Add(new Style(x => x.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Layoutable.MinHeightProperty, 44.0),
                new Setter(TemplatedControl.ForegroundProperty, Text),
                new Setter(TemplatedControl.BackgroundProperty, Panel),
                new Setter(TemplatedControl.BorderBrushProperty, BorderStrong),
            },
        });
        styles.Add(new Style(x => x.OfType<ToggleSwitch>())
        {
            Setters = { new Setter(Layoutable.MinHeightProperty, 44.0), new Setter(TemplatedControl.ForegroundProperty, Text) },
        });
        styles.Add(new Style(x => x.OfType<MenuItem>())
        {
            Setters = { new Setter(Layoutable.MinHeightProperty, 44.0) },
        });
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)),
                new Setter(Layoutable.MinHeightProperty, 44.0),
            },
        });
        styles.Add(new Style(x => x.OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, Text) },
        });
    }

    /// <summary>Имя элемента для программ чтения экрана и проверки через UI Automation (2026-10-27).</summary>
    public static T A11y<T>(this T c, string name) where T : Control
    {
        AutomationProperties.SetName(c, name);
        return c;
    }

    /// <summary>Кнопка: primary (Golden Light), secondary (с рамкой), danger (красная), nav (меню), ghost (без рамки).</summary>
    public static Button Btn(string text, string kind = "secondary", Action? click = null)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(16, 9), FontWeight = FontWeight.SemiBold, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 44,
        };
        AutomationProperties.SetName(b, text);
        var (bg, hover, fg, border) = kind switch
        {
            "primary" => ((IBrush)Accent, (IBrush)AccentStrong, (IBrush)OnAccent, (IBrush)Accent),
            "danger" => (Danger, B("#A42E25"), (IBrush)Brushes.White, Danger),
            "nav" => ((IBrush)Brushes.Transparent, NavyHover, NavyText, (IBrush)Brushes.Transparent),
            "ghost" => ((IBrush)Brushes.Transparent, Soft, Text, (IBrush)Brushes.Transparent),
            _ => (Panel, Soft, Text, BorderStrong),
        };
        b.Resources["ButtonBackground"] = bg;
        b.Resources["ButtonBackgroundPointerOver"] = hover;
        b.Resources["ButtonBackgroundPressed"] = hover;
        b.Resources["ButtonForeground"] = fg;
        b.Resources["ButtonForegroundPointerOver"] = fg;
        b.Resources["ButtonForegroundPressed"] = fg;
        b.Resources["ButtonBorderBrush"] = border;
        b.Resources["ButtonBorderBrushPointerOver"] = border;
        b.Resources["ButtonBorderBrushPressed"] = border;
        b.Resources["ButtonBackgroundDisabled"] = Soft;
        b.Resources["ButtonForegroundDisabled"] = TextSoft;
        b.BorderThickness = new Thickness(kind is "secondary" ? 1 : 0);
        if (click != null)
            b.Click += (_, _) => click();
        return b;
    }

    public static Button BtnAsync(string text, string kind, Func<Task> click)
    {
        var b = Btn(text, kind);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try
            {
                await click();
            }
            catch (Exception ex)
            {
                Toast.Show(ex.Message, error: true);
            }
            finally
            {
                b.IsEnabled = true;
            }
        };
        return b;
    }

    public static TextBox Input(string watermark = "", string text = "") => new TextBox { Watermark = watermark, Text = text }.A11y(watermark);

    public static TextBlock H1(string text) => new() { Text = text, FontSize = 24, FontWeight = FontWeight.ExtraBold, Foreground = Text };

    public static TextBlock H2(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Text };

    public static TextBlock SoftText(string text, double size = 13) => new() { Text = text, FontSize = size, Foreground = TextSoft, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Caption(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = TextSoft, Margin = new Thickness(0, 4, 0, 2) };

    public static Border Card(Control child, double padding = 16) => new()
    {
        Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(padding), Child = child,
    };

    public static Border Pill(string text, IBrush bg, IBrush fg) => new()
    {
        Background = bg, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = fg, FontSize = 12, FontWeight = FontWeight.SemiBold },
    };

    public static Button Chip(string text, bool on, Action click)
    {
        var b = Btn(text, on ? "primary" : "secondary", click);
        b.Padding = new Thickness(14, 6);
        return b;
    }

    public static StackPanel Field(string caption, Control input) => new() { Spacing = 2, Children = { Caption(caption), input } };

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalSeconds < 60)
            return L.T("только что", "азыр эле", "just now", "az önce", "hozirgina");
        if (d.TotalMinutes < 60)
            return L.T($"{(int)d.TotalMinutes} мин назад", $"{(int)d.TotalMinutes} мүн мурун", $"{(int)d.TotalMinutes} min ago", $"{(int)d.TotalMinutes} dk önce", $"{(int)d.TotalMinutes} daq oldin");
        return t.ToLocalTime().ToString("dd.MM HH:mm");
    }

    /// <summary>Размер окна по рабочей области экрана (без панели задач): окно всегда помещается, даже на 1024×600.</summary>
    public static void FitToScreen(Avalonia.Controls.Window w, double wantW, double wantH, double minW, double minH)
    {
        var wa = w.Screens?.Primary is { } s ? s.WorkingArea : new PixelRect(0, 0, 1366, 768);
        var scale = w.Screens?.Primary?.Scaling ?? 1.0;
        var maxW = Math.Max(320, wa.Width / scale - 16);
        var maxH = Math.Max(320, wa.Height / scale - 16);
        w.MinWidth = Math.Min(minW, maxW);
        w.MinHeight = Math.Min(minH, maxH);
        w.Width = Math.Min(wantW, maxW);
        w.Height = Math.Min(wantH, maxH);
    }
}

/// <summary>Знак бренда «солнце над горой» (брендбук NUR CRM 3.1–3.4): та же геометрия, что в кассе (BrandGeometry), рисуется кодом.</summary>
internal sealed class BrandMark : Control
{
    private static readonly (double X1, double Y1, double X2, double Y2)[] Rays = BuildRays();
    private static readonly double[] FanX = { 0, 14, 27, 39, 50, 61, 73, 86, 100 };

    public BrandMark()
    {
        Width = Height = 56;
        Ui.ThemeChanged += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => Ui.ThemeChanged -= InvalidateVisual;
        AttachedToVisualTree += (_, _) => { Ui.ThemeChanged -= InvalidateVisual; Ui.ThemeChanged += InvalidateVisual; };
    }

    private static (double, double, double, double)[] BuildRays()
    {
        const double size = 100, cx = 50, cy = 47, r0 = 22 + 3.5;
        var list = new List<(double, double, double, double)>();
        for (var deg = -12; deg <= 192; deg += 12)
        {
            var a = deg * Math.PI / 180;
            double dx = Math.Cos(a), dy = -Math.Sin(a);
            var t = double.MaxValue;
            if (dx > 1e-9) t = Math.Min(t, (size - cx) / dx);
            else if (dx < -1e-9) t = Math.Min(t, -cx / dx);
            if (dy > 1e-9) t = Math.Min(t, (size - cy) / dy);
            else if (dy < -1e-9) t = Math.Min(t, -cy / dy);
            list.Add((cx + dx * r0, cy + dy * r0, cx + dx * t, cy + dy * t));
        }
        return list.ToArray();
    }

    public override void Render(DrawingContext ctx)
    {
        var k = Math.Min(Bounds.Width, Bounds.Height) / 100.0;
        if (k <= 0)
            return;
        // На светлом: жёлтые лучи и солнце, чёрная гора. На тёмном: белые лучи, жёлтые солнце и гора.
        IBrush rays = Ui.IsDark ? Brushes.White : Ui.Accent, sun = Ui.Accent, mountain = Ui.IsDark ? Ui.Accent : new SolidColorBrush(Color.Parse("#191919"));
        var rayPen = new Pen(rays, 3.2 * k, lineCap: PenLineCap.Round);
        foreach (var (x1, y1, x2, y2) in Rays)
            ctx.DrawLine(rayPen, new Point(x1 * k, y1 * k), new Point(x2 * k, y2 * k));
        ctx.DrawEllipse(sun, null, new Point(50 * k, 47 * k), 22 * k, 22 * k);
        var fanPen = new Pen(mountain, 3.4 * k, lineCap: PenLineCap.Round);
        foreach (var x in FanX)
            ctx.DrawLine(fanPen, new Point((50 + (x - 50) * 0.3) * k, (47 + 53 * 0.3) * k), new Point(x * k, 100 * k));
        var peak = new StreamGeometry();
        using (var g = peak.Open())
        {
            g.BeginFigure(new Point(50 * k, 45.8 * k), true);
            g.LineTo(new Point((50 - 0.34 * 50 - 1.5) * k, (47 + 0.34 * 53) * k));
            g.LineTo(new Point((50 + 0.34 * 50 + 1.5) * k, (47 + 0.34 * 53) * k));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(mountain, null, peak);
    }
}

/// <summary>Короткое сообщение внизу активного окна программы.</summary>
internal static class Toast
{
    public static Action<string, bool>? Sink { get; set; }

    public static void Show(string text, bool error = false) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Sink?.Invoke(text, error));
}

/// <summary>Человеческие фразы для причин конца сеанса и записей журнала (5 языков). 2026-10-27, пакет О1: все причины сервера и кассы.</summary>
internal static class Reasons
{
    /// <summary>Коротко, для журнала сеансов.</summary>
    public static string Short(string reason) => reason switch
    {
        "operator" => L.T("завершил оператор", "оператор бүтүрдү", "ended by operator", "operatör bitirdi", "operator tugatdi"),
        "host" => L.T("завершил кассир", "кассир бүтүрдү", "ended by cashier", "kasiyer bitirdi", "kassir tugatdi"),
        "declined" => L.T("кассир отказал", "кассир баш тартты", "cashier declined", "kasiyer reddetti", "kassir rad etdi"),
        "consent-timeout" => L.T("нет ответа кассира", "кассир жооп берген жок", "no answer", "yanıt yok", "javob yo'q"),
        "locked" => L.T("неверный PIN ×5", "туура эмес PIN ×5", "wrong PIN ×5", "yanlış PIN ×5", "noto'g'ri PIN ×5"),
        "timeout" or "disconnect" => L.T("связь прервалась", "байланыш үзүлдү", "connection lost", "bağlantı koptu", "aloqa uzildi"),
        "idle" => L.T("долго без действий", "узак убакыт аракетсиз", "idle too long", "uzun süre işlem yok", "uzoq harakatsiz"),
        "max" => L.T("предел времени сеанса", "сеанстын убакыт чеги", "session time limit", "oturum süre sınırı", "seans vaqt chegarasi"),
        "revoked" => L.T("вход оператора отозван", "оператордун кириши жокко чыгарылды", "operator sign-in revoked", "operatör girişi iptal edildi", "operator kirishi bekor qilindi"),
        "logout" => L.T("оператор вышел", "оператор чыкты", "operator signed out", "operatör çıkış yaptı", "operator chiqdi"),
        "shutdown" => L.T("сервер перезапущен", "сервер кайра иштетилди", "server restarted", "sunucu yeniden başlatıldı", "server qayta ishga tushdi"),
        "expired" => L.T("код устарел", "код эскирди", "code expired", "kodun süresi doldu", "kod eskirdi"),
        "error" => L.T("ошибка связи", "байланыш катасы", "link error", "bağlantı hatası", "aloqa xatosi"),
        "" => "—",
        _ => reason,
    };

    /// <summary>Фраза для окна сеанса: что случилось и что делать дальше.</summary>
    public static string Long(string reason) => reason switch
    {
        "host" => L.T("Сеанс завершён на кассе. Кассир нажал «Завершить» — либо Windows закрыла экран кассы (окно разрешения UAC или блокировка Win+L у старой версии кассы).",
            "Сеанс кассада аяктады. Кассир «Аяктоо» басты — же Windows кассанын экранын жапты (UAC суроосу же Win+L бөгөттөө, кассанын эски версиясы).",
            "The session was ended on the till. The cashier pressed “End” — or Windows closed the till's screen (a UAC prompt or Win+L lock with an older till version).",
            "Oturum kasada sonlandırıldı. Kasiyer «Bitir»e bastı — ya da Windows kasanın ekranını kapattı (UAC penceresi veya Win+L kilidi, eski kasa sürümü).",
            "Seans kassada tugatildi. Kassir «Tugatish»ni bosdi — yoki Windows kassa ekranini yopdi (UAC oynasi yoki Win+L qulfi, kassaning eski versiyasi)."),
        "operator" => L.T("Вы завершили сеанс.", "Сиз сеансты аяктадыңыз.", "You ended the session.", "Oturumu sonlandırdınız.", "Siz seansni tugatdingiz."),
        "timeout" or "disconnect" => L.T("Связь с кассой или сервером прервалась. Подключитесь снова: кассе нужен новый код.", "Касса же сервер менен байланыш үзүлдү. Кайра туташыңыз: кассага жаңы код керек.",
            "The connection to the till or the server was lost. Connect again: the till needs a new code.", "Kasa veya sunucu ile bağlantı koptu. Yeniden bağlanın: kasa yeni kod alsın.",
            "Kassa yoki server bilan aloqa uzildi. Qayta ulaning: kassa yangi kod olsin."),
        "error" => L.T("Сеанс оборван из-за ошибки связи.", "Сеанс байланыш катасынан улам үзүлдү.", "The session was cut off by a link error.", "Oturum bağlantı hatası nedeniyle kesildi.", "Seans aloqa xatosi tufayli uzildi."),
        "shutdown" => L.T("Сервер поддержки перезапускается. Подождите минуту и подключитесь снова.", "Колдоо сервери кайра иштетилүүдө. Бир мүнөттөн кийин кайра туташыңыз.",
            "The support server is restarting. Wait a minute and connect again.", "Destek sunucusu yeniden başlıyor. Bir dakika bekleyip yeniden bağlanın.", "Yordam serveri qayta ishga tushmoqda. Bir daqiqadan keyin qayta ulaning."),
        "locked" => L.T("5 раз неверный PIN — код заблокирован. Кассиру нужно получить новый.", "PIN 5 жолу туура эмес — код бөгөттөлдү. Кассир жаңысын алсын.",
            "Wrong PIN 5 times — the code is locked. The cashier needs a new one.", "5 kez yanlış PIN — kod kilitlendi. Kasiyer yenisini almalı.", "PIN 5 marta noto'g'ri — kod bloklandi. Kassir yangisini olsin."),
        "expired" => L.T("Код устарел (15 минут). Попросите кассира получить новый.", "Код эскирди (15 мүнөт). Кассирден жаңысын сураңыз.", "The code expired (15 minutes). Ask the cashier for a new one.",
            "Kodun süresi doldu (15 dakika). Kasiyerden yenisini isteyin.", "Kod eskirdi (15 daqiqa). Kassirdan yangisini so'rang."),
        "declined" => L.T("Кассир отказал в доступе.", "Кассир уруксат берген жок.", "The cashier declined access.", "Kasiyer erişimi reddetti.", "Kassir ruxsat bermadi."),
        "consent-timeout" => L.T("Кассир не ответил на запрос — доступ не дан.", "Кассир суроого жооп берген жок — уруксат жок.", "The cashier did not answer — access was not granted.",
            "Kasiyer yanıt vermedi — erişim verilmedi.", "Kassir javob bermadi — ruxsat berilmadi."),
        "idle" => L.T("Сеанс завершён: 30 минут без действий оператора.", "Сеанс аяктады: оператордун аракети 30 мүнөт болгон жок.", "The session ended: 30 minutes without operator activity.",
            "Oturum bitti: 30 dakika operatör işlemi yok.", "Seans tugadi: 30 daqiqa operator harakatsiz."),
        "max" => L.T("Сеанс завершён: достигнут предел длительности (4 часа). Подключитесь снова, если нужно.", "Сеанс аяктады: узактык чегине жетти (4 саат). Керек болсо кайра туташыңыз.",
            "The session ended: the length limit (4 hours) was reached. Connect again if needed.", "Oturum bitti: süre sınırına (4 saat) ulaşıldı. Gerekirse yeniden bağlanın.", "Seans tugadi: davomiylik chegarasiga (4 soat) yetildi. Kerak bo'lsa qayta ulaning."),
        "revoked" => L.T("Ваш вход отозван (пароль сменён или оператор удалён). Войдите снова.", "Кирүүңүз жокко чыгарылды (сырсөз алмашты же оператор өчүрүлдү). Кайра кириңиз.",
            "Your sign-in was revoked (password changed or operator removed). Sign in again.", "Girişiniz iptal edildi (şifre değişti veya operatör silindi). Yeniden giriş yapın.", "Kirishingiz bekor qilindi (parol almashdi yoki operator o'chirildi). Qayta kiring."),
        "logout" => L.T("Вы вышли из программы.", "Сиз программадан чыктыңыз.", "You signed out.", "Çıkış yaptınız.", "Siz dasturdan chiqdingiz."),
        _ => L.T($"Сеанс завершён ({reason}).", $"Сеанс аяктады ({reason}).", $"The session ended ({reason}).", $"Oturum bitti ({reason}).", $"Seans tugadi ({reason})."),
    };
}
