using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// Minimal credential-free window displayed while the encrypted session is
/// loaded and validated before either LoginWindow or MainWindow is created.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _slowTimer;

    /// <summary>2026-11-05 (С-03): кассир нажал «Выйти» на затянувшемся запуске — вызывающий код закрывает программу.</summary>
    public event Action? ExitRequested;

    public SplashWindow()
    {
        InitializeComponent();
        ApplyApplicationTheme();
        // 2026-11-05 (С-03, аудит «зависание при медленном сервере»): запуск ждёт сервер (вход, компания) — через 15 с заставка объясняет это и даёт «Выйти».
        // Сам запуск при известной компании на ПК открывает кассу с кэшем через 8 с; долго ждёт только первый вход (чужие данные открывать нельзя).
        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _slowTimer.Tick += (_, _) => ShowSlowPanel();
        _slowTimer.Start();
        Closed += (_, _) => _slowTimer.Stop();
    }

    private void ShowSlowPanel()
    {
        _slowTimer.Stop();
        SlowText.Text = Tr.T(
            "Сервер NurCRM отвечает медленно. Касса откроется, как только он ответит.",
            "NurCRM сервери жай жооп берүүдө. Ал жооп бергенде касса ачылат.",
            "The NurCRM server is responding slowly. The till will open as soon as it answers.",
            "NurCRM sunucusu yavaş yanıt veriyor. Yanıt verir vermez kasa açılacak.",
            "NurCRM serveri sekin javob bermoqda. U javob bergach kassa ochiladi.");
        ExitButton.Content = Tr.T("Выйти", "Чыгуу", "Exit", "Çıkış", "Chiqish");
        SlowPanel.IsVisible = true;
        // Окно фиксированной высоты — растягиваем, оставляя по центру экрана.
        var extra = 90;
        Height += extra;
        Position = new PixelPoint(Position.X, Math.Max(0, Position.Y - (int)(extra * (VisualRoot?.RenderScaling ?? 1.0) / 2)));
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void ApplyApplicationTheme()
    {
        // App.RequestedThemeVariant is set from the user's application theme
        // before this window is constructed. ThemeVariant.Default delegates the
        // choice to Avalonia/the operating system.
        RequestedThemeVariant = Application.Current?.RequestedThemeVariant
            ?? ThemeVariant.Default;
    }
}
