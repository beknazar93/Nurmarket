using System.Runtime.CompilerServices;
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
                if (!prefs.Fullscreen || !window.IsVisible || window.WindowState == want || window.WindowState == WindowState.Minimized)
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
            if (!wasMinimized || window.WindowState == WindowState.Minimized)
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
        window.Activated += (_, _) => Dispatcher.UIThread.Post(() => RestoreMode("активация после сворачивания"), DispatcherPriority.Background);

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => Recheck("после показа"), DispatcherPriority.Background);
        if (window.IsVisible)
            Dispatcher.UIThread.Post(() => Recheck("окно уже показано"), DispatcherPriority.Background);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) => { timer.Stop(); Recheck("через 1,5 с"); };
        timer.Start();
    }

    /// <summary>Окно раздела встроено в окно владельца (OwnerShellWindow.PrepareEmbeddedWindow) — режим окна ему не применяется.</summary>
    public static void MarkEmbedded(Window window)
    {
        if (!Embedded.TryGetValue(window, out _))
            Embedded.Add(window, new object());
    }

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
