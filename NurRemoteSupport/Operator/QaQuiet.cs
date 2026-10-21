using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

/// <summary>Проверка программы на ПК, где работают люди (как у нового десктопа, Host/QaQuiet.cs): с --qa-quiet (только отладочная
/// сборка) окна открываются за краем экрана и без фокуса; снимки — по hwnd (tools/qa/shoth.ps1).</summary>
internal static class QaQuiet
{
    public static bool On =>
#if DEBUG
        Environment.GetCommandLineArgs().Contains("--qa-quiet");
#else
        false;
#endif

    public static void Install(Application app)
    {
        if (!On)
            return;
        app.Styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(Window.ShowActivatedProperty, false) } });
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Dispatcher.UIThread.Post(() => Away(w), DispatcherPriority.Send), RoutingStrategies.Direct);
    }

    private static void Away(Window w)
    {
        try
        {
            w.WindowState = WindowState.Normal;
            w.Position = new PixelPoint(-6000, 20);
        }
        catch (Exception)
        {
            // окно уже закрыто
        }
    }
}
