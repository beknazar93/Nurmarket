using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using AvIControlled = Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime;

namespace NurMarketKassa;

/// <summary>2026-10-04, Android-касса: замена Avalonia IClassicDesktopStyleApplicationLifetime
/// для кода кассы (App.axaml.cs, окно входа, главное окно и т.д. пишут desktop.MainWindow = …,
/// desktop.Windows, desktop.Shutdown()). Тип лежит в пространстве имён NurMarketKassa, поэтому
/// «IClassicDesktopStyleApplicationLifetime» без полного имени в Android-сборке указывает сюда.</summary>
public interface IClassicDesktopStyleApplicationLifetime : AvIControlled
{
    string[]? Args { get; }
    ShutdownMode ShutdownMode { get; set; }
    Window? MainWindow { get; set; }
    IReadOnlyList<Window> Windows { get; }
    event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;
    bool TryShutdown(int exitCode = 0);
}

/// <summary>Время жизни программы на Android: снаружи — обычное ISingleViewApplicationLifetime
/// Avalonia (вид = <see cref="WindowLayerHost"/>), для кода кассы — «настольное» с окнами-слоями.
/// Ставится вместо настоящего в AppBuilder.AfterSetup (до OnFrameworkInitializationCompleted).</summary>
public sealed class AndroidDesktopLifetime : IClassicDesktopStyleApplicationLifetime, ISingleViewApplicationLifetime
{
    private readonly ISingleViewApplicationLifetime _inner;
    private Window? _mainWindow;
    private bool _exited;

    public AndroidDesktopLifetime(ISingleViewApplicationLifetime inner, WindowLayerHost host)
    {
        _inner = inner;
        Host = host;
        Host.IsMainWindow = w => ReferenceEquals(w, _mainWindow)
                                 || w.GetType().Name is "MainWindow" or "OwnerShellWindow" or "LoginWindow" or "SplashWindow";
        Instance = this;
    }

    public static AndroidDesktopLifetime? Instance { get; private set; }

    public WindowLayerHost Host { get; }

    public Avalonia.Controls.Control? MainView
    {
        get => _inner.MainView;
        set => _inner.MainView = value;
    }

    public string[]? Args => Array.Empty<string>();

    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnExplicitShutdown;

    public Window? MainWindow
    {
        get => _mainWindow;
        set
        {
            _mainWindow = value;
            if (value is not null)
                Host.Relayout(value);
        }
    }

    public IReadOnlyList<Window> Windows => Host.Windows;

#pragma warning disable CS0067 // на Android запуск уже произошёл, Startup не наступает
    public event EventHandler<ControlledApplicationLifetimeStartupEventArgs>? Startup;
#pragma warning restore CS0067
    public event EventHandler<ControlledApplicationLifetimeExitEventArgs>? Exit;
    public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;

    /// <summary>Android закрывает программу сам и без предупреждения (нехватка памяти, смахнули из
    /// списка). Поэтому при уходе программы в фон делаем то же, что Windows-касса делает при
    /// выключении компьютера: просим подписчиков сохраниться (WAL-чекпоинт базы).</summary>
    internal void OnEnteredBackground()
    {
        try
        {
            ShutdownRequested?.Invoke(this, new ShutdownRequestedEventArgs());
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: сохранение при уходе в фон — {ex.Message}", "WARNING");
        }
    }

    public bool TryShutdown(int exitCode = 0)
    {
        var args = new ShutdownRequestedEventArgs();
        ShutdownRequested?.Invoke(this, args);
        if (args.Cancel)
            return false;
        Shutdown(exitCode);
        return true;
    }

    public void Shutdown(int exitCode = 0)
    {
        if (_exited)
            return;
        _exited = true;
        foreach (var w in Host.Windows.Reverse().ToList())
        {
            try { w.Close(); } catch { /* закрываем всё, что можно */ }
        }
        try
        {
            Exit?.Invoke(this, new ControlledApplicationLifetimeExitEventArgs(exitCode));
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: завершение — {ex.Message}", "WARNING");
        }
        AndroidPlatformHooks.FinishApplication?.Invoke(exitCode);
    }
}

/// <summary>Связь кода кассы с Android-частью (MainActivity задаёт при старте).</summary>
public static class AndroidPlatformHooks
{
    /// <summary>Закрыть программу (FinishAffinity + завершение процесса).</summary>
    public static Action<int>? FinishApplication { get; set; }

    /// <summary>Увести программу в фон (кнопка «Свернуть» окна кассы).</summary>
    public static Action? MoveToBackground { get; set; }
}
