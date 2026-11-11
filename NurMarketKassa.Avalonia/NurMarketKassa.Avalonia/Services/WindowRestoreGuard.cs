using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11, жалоба владельца: «если часто сворачивать программу (кассу или владельца), с шансом ~60 % — кривые кнопки, программа
/// показывается наполовину». Что выяснилось (код Avalonia 11.2.5 Win32 + обработчики кассы):
/// 1) при сворачивании Windows сначала присылает WM_MOVE с положением (-32000, -32000), и только потом WM_SIZE «свёрнуто» — в момент
///    PositionChanged окно для Avalonia ещё «не свёрнуто», и программа владельца клала окно открытого раздела по экранным координатам
///    свёрнутого окна (SyncSectionBounds);
/// 2) WindowState = Minimized у окна «на весь экран» (FullScreen) Avalonia делает через выход из полного экрана: окно на миг получает рамку
///    и прежний размер «окна», после разворачивания оно обычное (Normal) и FullscreenHelper ставит полный экран заново — две перестройки
///    раскладки подряд; а полный экран, поставленный, пока окно ещё свёрнуто, запоминает «прежний размер» свёрнутого окна (160×28 за экраном);
/// 3) пересчёты по размеру/экрану (масштаб, проверка «окно за экраном») могли сработать на свёрнутом окне.
/// Здесь: признак «окно свёрнуто или размеры вырожденные» (для пропуска пересчётов), сворачивание окна на весь экран средствами Windows
/// (как с панели задач) и после разворачивания — ОДНА отложенная (150 мс, дребезг сворачиваний сливается) перерисовка корня окна и
/// пересчёт раскладки окна. В журнал — сворачивание/разворачивание с размерами, не чаще раза в 10 с на окно.</summary>
public static class WindowRestoreGuard
{
    /// <summary>Пауза после последнего разворачивания до перерисовки — серия «свернул-развернул» даёт одну перерисовку.</summary>
    public const int SettleDelayMs = 150;

    /// <summary>Не чаще одной строки журнала о сворачивании/разворачивании на окно за этот срок.</summary>
    public static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(10);

    /// <summary>Чистая проверка: положение/размер окна вырожденные — свёрнутое окно Windows стоит в (-32000, -32000) размером 160×28,
    /// у только что созданного или «схлопнутого» окна размер 0. По таким числам раскладку, масштаб и положение не считаем.</summary>
    public static bool IsDegenerate(PixelPoint position, Size clientSize) =>
        position.X <= -30000 || position.Y <= -30000
        || !(clientSize.Width >= 100) || !(clientSize.Height >= 60);

    /// <summary>Окно сейчас свёрнуто: по Avalonia или (Windows) по самой системе — WM_MOVE в (-32000, -32000) приходит раньше WM_SIZE,
    /// и в этот момент WindowState ещё прежний.</summary>
    public static bool IsMinimizedNow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            return true;
#if !NURANDROID
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (window.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero && IsIconic(h))
                    return true;
            }
            catch
            {
                // нет окна системы — считаем по Avalonia
            }
        }
#endif
        return false;
    }

    /// <summary>Пересчёт раскладки/масштаба/положения сейчас пропустить: окно свёрнуто, или его положение/размер вырожденные.</summary>
    public static bool ShouldSkipLayoutWork(Window window)
    {
        if (!window.IsVisible || IsMinimizedNow(window))
            return true;
#if NURANDROID
        return false;
#else
        return IsDegenerate(window.Position, window.ClientSize);
#endif
    }

    /// <summary>Кнопка «Свернуть» своей шапки. Окно на весь экран (FullScreen) в Windows сворачиваем системой (как кнопкой на панели задач):
    /// Avalonia сохраняет полный экран и после разворачивания возвращает окно сразу на весь экран, без промежуточного «окна с рамкой».</summary>
    public static void Minimize(Window window)
    {
#if !NURANDROID
        if (OperatingSystem.IsWindows() && window.WindowState == WindowState.FullScreen)
        {
            try
            {
                if (window.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero)
                {
                    ShowWindow(h, SwMinimize);
                    return;
                }
            }
            catch
            {
                // не вышло — как раньше
            }
        }
#endif
        window.WindowState = WindowState.Minimized;
    }

    /// <summary>Сколько раз окно перерисовано после разворачивания (для тестов).</summary>
    public static int SettledCount(Window window) => States.TryGetValue(window, out var s) ? s.Settled : 0;

    private sealed class State
    {
        public readonly List<Action> Callbacks = new();
        public DispatcherTimer? Timer;
        public bool WasMinimized;
        public int Retries;
        public int Settled;
        public DateTime LastLogUtc = DateTime.MinValue;
        public int SkippedLogs;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    /// <summary>Следить за сворачиванием окна. <paramref name="onRestored"/> — пересчёт раскладки окна после разворачивания (один раз на серию).
    /// Повторный вызов для того же окна только добавляет действие.</summary>
    public static void Attach(Window window, Action? onRestored = null)
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (States.TryGetValue(window, out var existing))
        {
            if (onRestored is not null)
                existing.Callbacks.Add(onRestored);
            return;
        }
        var state = new State();
        if (onRestored is not null)
            state.Callbacks.Add(onRestored);
        States.Add(window, state);

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty)
                return;
            var newState = window.WindowState;
            if (newState == WindowState.Minimized)
            {
                state.WasMinimized = true;
                state.Timer?.Stop();
                Log(window, state, $"свёрнуто (было {e.OldValue})");
                // 2026-11-11 (владелец: «после сворачивания кнопки перестают работать»): захват указателя, палец сенсора, правка ячейки — сбросить.
                InputStateReset.Reset(window, "свёрнуто");
                return;
            }
            if (!state.WasMinimized)
                return;
            state.Retries = 0;
            Schedule(window, state);
        };
        window.Closed += (_, _) => state.Timer?.Stop();
    }

    private static void Schedule(Window window, State state)
    {
        if (state.Timer is null)
        {
            state.Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleDelayMs) };
            state.Timer.Tick += (_, _) => Settle(window, state);
        }
        state.Timer.Stop();
        state.Timer.Start();
    }

    private static void Settle(Window window, State state)
    {
        state.Timer?.Stop();
        try
        {
            if (IsMinimizedNow(window))
                return; // снова свёрнуто — ждём следующего разворачивания
            if (ShouldSkipLayoutWork(window))
            {
                // Окно развёрнуто, но система ещё не отдала настоящий размер — подождём ещё (не больше 5 раз).
                if (++state.Retries <= 5 && window.IsVisible)
                    Schedule(window, state);
                return;
            }
            state.WasMinimized = false;
            state.Settled++;
            // 2026-11-11: после разворачивания — только палец сенсора (захват и правку ячейки не трогаем: человек уже может нажимать).
            InputStateReset.OnActivated(window);
            // Одна принудительная перестройка и перерисовка корня: раскладка — по текущему (настоящему) размеру окна, кадр — целиком
            // (на части видеодрайверов после разворачивания оставалась старая/половинная картинка).
            if (window.Content is Control content)
            {
                content.InvalidateMeasure();
                content.InvalidateArrange();
                content.InvalidateVisual();
            }
            window.InvalidateMeasure();
            window.InvalidateArrange();
            window.InvalidateVisual();
            foreach (var callback in state.Callbacks.ToList())
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Окно {window.GetType().Name}: пересчёт после разворачивания не удался: {ex.Message}", "WARNING");
                }
            }
            Log(window, state, "развёрнуто");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно {window.GetType().Name}: перерисовка после разворачивания не удалась: {ex.Message}", "WARNING");
        }
    }

    private static void Log(Window window, State state, string what)
    {
        var now = DateTime.UtcNow;
        if (now - state.LastLogUtc < LogThrottle)
        {
            state.SkippedLogs++;
            return;
        }
        state.LastLogUtc = now;
        var skipped = state.SkippedLogs;
        state.SkippedLogs = 0;
        try
        {
            var size = window.ClientSize;
            var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
            PosLogger.Log(
                $"Окно {window.GetType().Name}: {what}, состояние {window.WindowState}, окно {size.Width * scale:0}x{size.Height * scale:0} px, " +
                $"позиция {window.Position.X},{window.Position.Y}, масштаб {scale * 100:0}%" + (skipped > 0 ? $"; событий без записи: {skipped}." : "."),
                "UI");
        }
        catch
        {
            // журнал — диагностика
        }
    }

#if !NURANDROID
    private const int SwMinimize = 6;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
#endif
}
