using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace NurRemoteSupport.Operator;

/// <summary>Оформление — те же цвета, что у нового десктопа NurMarket (Theme/Tokens.axaml): жёлтый акцент, тёмно-синее меню.</summary>
internal static class Ui
{
    public static readonly IBrush Window = B("#F4F5F7");
    public static readonly IBrush Panel = B("#FFFFFF");
    public static readonly IBrush Soft = B("#F9FAFB");
    public static readonly IBrush Border = B("#E5E7EB");
    public static readonly IBrush BorderStrong = B("#D0D5DD");
    public static readonly IBrush Text = B("#111827");
    public static readonly IBrush TextSoft = B("#667085");
    public static readonly IBrush TextMid = B("#475467");
    public static readonly IBrush Accent = B("#FFD600");
    public static readonly IBrush AccentStrong = B("#E5C000");
    public static readonly IBrush AccentSoft = B("#FFF7CC");
    public static readonly IBrush Navy = B("#0B2A3C");
    public static readonly IBrush NavyHover = B("#163B52");
    public static readonly IBrush Success = B("#0E7A3A");
    public static readonly IBrush SuccessSoft = B("#E7F6EC");
    public static readonly IBrush Danger = B("#C0362C");
    public static readonly IBrush DangerSoft = B("#FDECEA");
    public static readonly IBrush Warn = B("#B54708");
    public static readonly IBrush WarnSoft = B("#FEF3E2");
    public static readonly IBrush Info = B("#3538CD");
    public static readonly IBrush InfoSoft = B("#EEF4FF");

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static void AddStyles(Styles styles)
    {
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)),
                new Setter(Layoutable.MinHeightProperty, 40.0),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 9)),
                new Setter(TemplatedControl.BorderBrushProperty, BorderStrong),
                new Setter(TemplatedControl.BackgroundProperty, Panel),
            },
        });
        styles.Add(new Style(x => x.OfType<ComboBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)), new Setter(Layoutable.MinHeightProperty, 38.0) },
        });
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(10)), new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)) },
        });
    }

    /// <summary>Кнопка: primary (жёлтая), secondary (белая с рамкой), danger (красная), nav (меню), ghost (без рамки).</summary>
    public static Button Btn(string text, string kind = "secondary", Action? click = null)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(16, 9), FontWeight = FontWeight.SemiBold, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var (bg, hover, fg, border) = kind switch
        {
            "primary" => (Accent, AccentStrong, Text, Accent),
            "danger" => (Danger, B("#A42E25"), (IBrush)Brushes.White, Danger),
            "nav" => ((IBrush)Brushes.Transparent, NavyHover, (IBrush)Brushes.White, (IBrush)Brushes.Transparent),
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

    public static TextBox Input(string watermark = "", string text = "") => new() { Watermark = watermark, Text = text };

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
}

/// <summary>Короткое сообщение внизу активного окна программы.</summary>
internal static class Toast
{
    public static Action<string, bool>? Sink { get; set; }

    public static void Show(string text, bool error = false) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Sink?.Invoke(text, error));
}
