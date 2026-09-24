using Avalonia.Controls;
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
}
