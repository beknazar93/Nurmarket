using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Общая логика применения полноэкранного режима (Настройки → Экран) — раньше каждое окно
/// само копировало один и тот же блок `if (Fullscreen) { SystemDecorations=None;
/// WindowState=Maximized; }`. Этап 8 бэклога "Доработки" добавляет третий режим — "настоящий"
/// полный экран (Avalonia WindowState.FullScreen), который в отличие от "безрамочного
/// развёрнутого окна" реально перекрывает панель задач/меню "Пуск" на моноблоках и части
/// неоптимизированных Windows, где старый режим этого не делал. Вынесено в один метод, чтобы
/// не дублировать новую 3-вариантную логику в 6+ окнах по отдельности.
/// </summary>
public static class FullscreenHelper
{
    public static void Apply(Window window)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.Fullscreen)
            return;

        // Окна, которые до этого вызвали FitToScreen(), несут потолок «рабочая область минус
        // поле в 24 px» — он нужен, чтобы окно с фиксированными Width/Height целиком влезало
        // на компактный моноблок. В полноэкранном режиме этот же потолок не даёт окну занять
        // экран: оно останавливается на пару десятков пикселей раньше, и по краям — снизу
        // заметнее всего — просвечивает окно кассы под ним.
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;

        if (prefs.TrueFullscreen)
        {
            window.WindowState = WindowState.FullScreen;
        }
        else
        {
            window.SystemDecorations = SystemDecorations.None;
            window.WindowState = WindowState.Maximized;
        }
    }

    // 2026-10-27: окна, за которыми уже следим (повторная проверка режима — один раз на окно), и окна раздела,
    // встроенные в программу владельца (их место и размер задаёт оболочка владельца — разворачивать нельзя).
    private static readonly ConditionalWeakTable<Window, object> Watched = new();
    private static readonly ConditionalWeakTable<Window, object> Embedded = new();

    private static WindowState ExpectedState(UserPreferences prefs) =>
        prefs.TrueFullscreen ? WindowState.FullScreen : WindowState.Maximized;

    /// <summary>2026-10-27, жалоба владельца «и касса, и программа владельца запускаются в окне»: главное окно (касса,
    /// программа владельца, вход) при «Полный экран» включённом должно встать на весь экран, а не остаться окном.
    /// Применяет режим сразу и проверяет ещё раз после показа окна (смена окон вход → главное, окно «Касса обновлена»,
    /// экран покупателя могли сбросить состояние) — если состояние не то, ставит заново и пишет в журнал (UI).</summary>
    public static void ApplyToMainWindow(Window window)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.Fullscreen || OperatingSystem.IsAndroid())
            return;
        Apply(window);
        if (Watched.TryGetValue(window, out _))
            return;
        Watched.Add(window, new object());

        void Recheck(string when)
        {
            try
            {
                var want = ExpectedState(prefs);
                // 2026-11-11 (жалоба «частое сворачивание — программа наполовину»): и окно, которое система уже свернула, а Avalonia ещё не знает
                // (IsIconic): полный экран, поставленный свёрнутому окну, запоминает «прежний размер» 160×28 за краем экрана.
                if (!prefs.Fullscreen || !window.IsVisible || window.WindowState == want || WindowRestoreGuard.IsMinimizedNow(window))
                    return;
                PosLogger.Log($"Окно {window.GetType().Name}: состояние {window.WindowState} вместо {want} ({when}) — ставлю заново.", "UI");
                Apply(window);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Окно {window.GetType().Name}: проверка полного экрана не удалась: {ex.Message}", "WARNING");
            }
        }

        // 2026-10-27 (приёмка 1.17.63, Д-2): «Свернуть» → возврат с панели задач возвращал обычное окно с рамкой (Avalonia ставит
        // WindowState=Normal), а проверка выше срабатывала только при открытии. Теперь следим за сменой состояния окна:
        // после выхода из Minimized (и при активации окна после сворачивания) режим применяется заново.
        // Само сворачивание не трогаем (Recheck пропускает Minimized). Цикла быть не должно (переустановка не ставит Minimized), но на всякий случай — не больше 30 переустановок за 5 с.
        var wasMinimized = false;
        var reapplyTimes = new Queue<DateTime>();

        void RestoreMode(string when)
        {
            if (!wasMinimized || WindowRestoreGuard.IsMinimizedNow(window)) // 2026-11-11: и по самой системе (см. Recheck)
                return;
            wasMinimized = false;
            var now = DateTime.UtcNow;
            while (reapplyTimes.Count > 0 && now - reapplyTimes.Peek() > TimeSpan.FromSeconds(5))
                reapplyTimes.Dequeue();
            if (reapplyTimes.Count >= 30)
                return;
            reapplyTimes.Enqueue(now);
            Recheck(when);
            // Windows может довести состояние окна уже после события — проверяем ещё раз чуть позже.
            var again = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            again.Tick += (_, _) => { again.Stop(); Recheck(when + ", повторно"); };
            again.Start();
            // 2026-11-11 (Д-3): и место/размер — после того как состояние устоялось.
            var geometry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            geometry.Tick += (_, _) => { geometry.Stop(); CheckGeometry(when); };
            geometry.Start();
        }

        // 2026-11-11 (Д-3, живая проверка «Склада»): со второго сворачивания окно владельца вставало «FullScreen», но 1936×1040 в (−8,0) — по 8 px за краями экрана,
        // снизу панель задач. Avalonia 11.2.5 (Win32) ставит полный экран окну, которое Windows вернула из свёрнутого РАЗВЁРНУТЫМ (Maximized, со стилем WS_MAXIMIZE):
        // флаг «полный экран» есть, а место/размер остаются от развёрнутого окна с рамкой (рабочая область + невидимая рамка 8 px). Повторное «FullScreen» ничего не меняет
        // (состояние уже FullScreen). Лечим: если место/размер не совпали с экраном (полный экран — весь экран, развёрнутое без рамки — рабочая область), окно ставим
        // обычным и сразу заново в нужный режим (из обычного окна Avalonia ставит полный экран верно). Не больше 2 исправлений за 10 с; в журнал — факт и размеры.
        // 2026-11-12 (аудит У-5): раньше исправление шло ТОЛЬКО после сворачивания, всегда через обычное окно (мигание рамкой) и не больше 2 раз за 10 с — пятое быстрое
        // сворачивание оставалось с окном за краями. Теперь: (1) геометрия проверяется и при активации окна; (2) сначала пробуем поставить место и размер прямо, без смены состояния
        // (нет мигания), и только если не помогло через 300 мс — прежний путь через обычное окно; (3) предел защиты от цикла — 6 исправлений за 10 с.
        var geometryFixes = new Queue<DateTime>();

        void CheckGeometry(string when)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !prefs.Fullscreen || !window.IsVisible || WindowRestoreGuard.IsMinimizedNow(window))
                    return;
                var want = ExpectedState(prefs);
                if (window.WindowState != want || !TryGetGeometry(window, out var actual, out var expected)
                    || !NeedsGeometryFix(actual, expected, want == WindowState.FullScreen ? 2 : MaximizedTolerance))
                    return;
                var now = DateTime.UtcNow;
                while (geometryFixes.Count > 0 && now - geometryFixes.Peek() > TimeSpan.FromSeconds(10))
                    geometryFixes.Dequeue();
                if (geometryFixes.Count >= 6)
                    return;
                geometryFixes.Enqueue(now);
                var target = expected!.Value;
                PosLogger.Log($"Окно {window.GetType().Name}: {want}, но стоит {Describe(actual)} вместо {Describe(expected)} ({when}) — ставлю место и размер напрямую.", "UI");
                var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
                window.Position = target.Position;
                window.Width = target.Width / scale;
                window.Height = target.Height / scale;
                var verify = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                verify.Tick += (_, _) =>
                {
                    verify.Stop();
                    try
                    {
                        if (!window.IsVisible || WindowRestoreGuard.IsMinimizedNow(window) || window.WindowState != want
                            || !TryGetGeometry(window, out var after, out var exp)
                            || !NeedsGeometryFix(after, exp, want == WindowState.FullScreen ? 2 : MaximizedTolerance))
                            return;
                        PosLogger.Log($"Окно {window.GetType().Name}: напрямую не вышло ({Describe(after)}) — ставлю заново через обычное окно.", "UI");
                        window.WindowState = WindowState.Normal;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!window.IsVisible || WindowRestoreGuard.IsMinimizedNow(window))
                                return;
                            Apply(window);
                            var report = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                            report.Tick += (_, _) =>
                            {
                                report.Stop();
                                if (TryGetGeometry(window, out var last, out var need))
                                    PosLogger.Log($"Окно {window.GetType().Name}: после исправления {window.WindowState}, {Describe(last)} (нужно {Describe(need)}).", "UI");
                            };
                            report.Start();
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"Окно {window.GetType().Name}: повторное исправление места не удалось: {ex.Message}", "WARNING");
                    }
                };
                verify.Start();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Окно {window.GetType().Name}: проверка места на экране не удалась: {ex.Message}", "WARNING");
            }
        }

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty)
                return;
            if (window.WindowState == WindowState.Minimized)
            {
                wasMinimized = true;
                return;
            }
            if (wasMinimized)
                Dispatcher.UIThread.Post(() => RestoreMode("возврат из свёрнутого"), DispatcherPriority.Background);
        };
        window.Activated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var restoring = wasMinimized; // после сворачивания геометрию проверит RestoreMode через 800 мс, когда состояние устоится
            RestoreMode("активация после сворачивания");
            if (!restoring)
                CheckGeometry("активация"); // 2026-11-12 (У-5): геометрия — и при обычной активации, не только после сворачивания
        }, DispatcherPriority.Background);

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => Recheck("после показа"), DispatcherPriority.Background);
        if (window.IsVisible)
            Dispatcher.UIThread.Post(() => Recheck("окно уже показано"), DispatcherPriority.Background);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) => { timer.Stop(); Recheck("через 1,5 с"); };
        timer.Start();
    }

    /// <summary>2026-11-11 (Д-3): где должно стоять окно в этом состоянии (в пикселях экрана): «на весь экран» — весь экран, развёрнутое без системной рамки —
    /// рабочая область (без панели задач). null — не проверяем (обычное окно; развёрнутое с рамкой ОС законно выходит на 8 px невидимой рамки).</summary>
    public static PixelRect? ExpectedRect(WindowState state, bool borderless, PixelRect screenBounds, PixelRect workArea) => state switch
    {
        WindowState.FullScreen => screenBounds,
        WindowState.Maximized when borderless => workArea,
        _ => null,
    };

    /// <summary>2026-11-11 (Д-3): окно стоит не там: левый верхний угол отличается от нужного больше чем на <paramref name="tolerance"/> px, размер — больше чем на 2 × <paramref name="tolerance"/>
    /// (живой случай: 1936×1040 в (−8,0) при экране 1920×1080).</summary>
    public static bool NeedsGeometryFix(PixelRect actual, PixelRect? expected, int tolerance = 2) =>
        expected is { Width: > 0, Height: > 0 } e
        && (Math.Abs(actual.X - e.X) > tolerance || Math.Abs(actual.Y - e.Y) > tolerance
            || Math.Abs(actual.Width - e.Width) > 2 * tolerance || Math.Abs(actual.Height - e.Height) > 2 * tolerance); // рамка — с обеих сторон

    /// <summary>2026-11-11 (Д-3): допуск для развёрнутого окна без рамки — 10 px (невидимая рамка Windows 8 px не считается ошибкой; не дёргаем окно зря).</summary>
    public const int MaximizedTolerance = 10;

    private static string Describe(PixelRect? rect) => rect is { } r ? $"{r.Width}x{r.Height} в ({r.X},{r.Y})" : "—";

    /// <summary>Место/размер окна и нужные по экрану, на котором оно стоит (пиксели экрана). false — экран не известен или окно не проверяем.</summary>
    private static bool TryGetGeometry(Window window, out PixelRect actual, out PixelRect? expected)
    {
        actual = default;
        expected = null;
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null)
            return false;
        var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        var size = window.FrameSize ?? window.ClientSize;
        actual = new PixelRect(window.Position, new PixelSize((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale)));
        expected = ExpectedRect(window.WindowState, window.SystemDecorations == SystemDecorations.None, screen.Bounds, screen.WorkingArea);
        return expected is not null;
    }

    /// <summary>Окно раздела встроено в окно владельца (OwnerShellWindow.PrepareEmbeddedWindow) — режим окна ему не применяется.</summary>
    public static void MarkEmbedded(Window window)
    {
        if (!Embedded.TryGetValue(window, out _))
            Embedded.Add(window, new object());
    }

    /// <summary>2026-10-30: окно встроено в программу владельца (ModalWindowManager не считает его отдельной модалкой).</summary>
    public static bool IsEmbedded(Window window) => Embedded.TryGetValue(window, out _);

    private static readonly HashSet<string> SectionWindowTypes = new(StringComparer.Ordinal)
    {
        "WarehouseWindow", "PurchasesWindow", "ClientsWindow", "SalaryWindow", "AbcAnalysisWindow", "RestockSuggestionsWindow",
        "SuppliersWindow", "AiAdvisorWindow", "KnowledgeBaseWindow", "SiteOrdersWindow",
    };

    /// <summary>2026-10-27: единая точка для окон разделов (Склад, Закупки, Клиенты, Сотрудники, Аналитика, Нур Советник…),
    /// которые открываются отдельным окном: при «Полный экран» они разворачиваются на рабочую область (не под панель
    /// задач). Окно с собственной шапкой (ExtendClientAreaToDecorationsHint — свои кнопки «свернуть/закрыть») остаётся
    /// без системной рамки, как в режиме «Полный экран»; окно с системной рамкой — с рамкой. Диалоги, редакторы,
    /// Маркетплейс и окна, встроенные в программу владельца, не затрагиваются. Вызывается из DialogScreenFit при открытии окна.</summary>
    public static bool TryApplySectionWindowMode(Window window)
    {
        if (OperatingSystem.IsAndroid() || !UserPreferences.Instance.Fullscreen)
            return false;
        if (!SectionWindowTypes.Contains(window.GetType().Name) || Embedded.TryGetValue(window, out _))
            return false;
        if (window.WindowState != WindowState.Normal)
            return false;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        if (window.ExtendClientAreaToDecorationsHint)
            window.SystemDecorations = SystemDecorations.None;
        window.WindowState = WindowState.Maximized;
        return true;
    }
}
