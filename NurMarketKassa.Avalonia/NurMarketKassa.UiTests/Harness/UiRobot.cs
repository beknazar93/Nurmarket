using Avalonia.Controls;
using Avalonia.Threading;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-13 (Н-1/Н-2): «робот» модальных окон для сценариев в настоящем окне кассы. Окна кассы в Windows модальные и сами крутят очередь интерфейса, пока открыты, —
/// тест, нажавший кнопку, ждёт внутри нажатия. Поэтому действия в окне задаются заранее: <see cref="WhenOpened{T}"/> — окно этого типа открылось, робот выполняет их внутри него.
/// Сторож (<see cref="Guard"/>) закрывает окно, оставшееся открытым дольше срока, чтобы прогон не висел; такие случаи копятся в <see cref="Problems"/>.</summary>
public static class UiRobot
{
    private static bool _hooked;
    private static readonly Dictionary<Type, Action<Window>> Drivers = new();

    /// <summary>Окна, открытые с момента <see cref="Reset"/> (кроме главного).</summary>
    public static List<Window> Opened { get; } = new();

    /// <summary>Ошибки роботов и окна, закрытые сторожем.</summary>
    public static List<string> Problems { get; } = new();

    /// <summary>Главное окно сценария (его открытие роботом не обрабатывается).</summary>
    public static Window? Main { get; set; }

    public static void Reset(Window? main = null)
    {
        Main = main;
        Drivers.Clear();
        Opened.Clear();
        Problems.Clear();
        if (_hooked)
            return;
        _hooked = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => OnOpened(w));
    }

    public static void WhenOpened<T>(Action<T> drive) where T : Window => Drivers[typeof(T)] = w => drive((T)w);

    public static T? Last<T>() where T : Window => Opened.OfType<T>().LastOrDefault();

    public static string OpenNames() => string.Join(", ", Opened.Where(w => w.IsVisible).Select(w => w.GetType().Name));

    private static void OnOpened(Window w)
    {
        if (Main is null || ReferenceEquals(w, Main))
            return;
        Opened.Add(w);
        if (!Drivers.TryGetValue(w.GetType(), out var driver))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UiShot.Settle(4, 25);
                if (w.IsVisible)
                    driver(w);
            }
            catch (Exception ex)
            {
                Problems.Add($"окно {w.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                try { w.Close(); } catch { /* уже закрыто */ }
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Сторож на время сценария: через <paramref name="afterMs"/> (и дальше каждые 5 с) закрывает самое верхнее оставшееся окно.</summary>
    public static IDisposable Guard(int afterMs = 20000) => new Timer(_ => Dispatcher.UIThread.Post(() =>
    {
        var stuck = Opened.LastOrDefault(w => w.IsVisible);
        if (stuck is null)
            return;
        Problems.Add("сторож закрыл зависшее окно " + stuck.GetType().Name);
        try { stuck.Close(); } catch { /* уже закрыто */ }
    }), null, afterMs, 5000);

    public static void CloseAll()
    {
        foreach (var w in Opened.Where(w => w.IsVisible).Reverse().ToList())
        {
            try { w.Close(); } catch { /* уже закрыто */ }
        }
        UiShot.Settle(3, 20);
    }

    /// <summary>Ждёт условие, прокручивая очередь интерфейса.</summary>
    public static bool WaitFor(Func<bool> condition, int ms = 4000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            UiShot.Pump();
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        UiShot.Pump();
        return condition();
    }
}
