using Avalonia.Controls;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Общая логика применения масштаба интерфейса (Настройки → Экран → "Масштаб", 50–200%,
/// см. UserPreferences.UiScalePercent) — используется и MainWindow, и PosSettingsWindow (и
/// любым другим окном, которое захочет тот же масштаб позже), чтобы не дублировать одну и ту
/// же формулу clamp+ScaleTransform в каждом окне отдельно.
/// </summary>
public static class UiScaleHelper
{
    /// <param name="designWidth">Ширина, на которую реально рассчитан макет ЭТОГО конкретного
    /// окна (обычно то же число, что и XAML Width) — у разных окон разный "родной" размер
    /// (MainWindow ~1280×840, PosSettingsWindow ~1000×820), поэтому общей константы на все
    /// окна быть не может.</param>
    /// <param name="designHeight">См. designWidth.</param>
    public static void Apply(LayoutTransformControl transformRoot, double designWidth, double designHeight)
    {
        var preferred = Math.Clamp(UserPreferences.Instance.UiScalePercent, 50, 200) / 100.0;
        var autoFit = ComputeAutoFitScale(transformRoot, designWidth, designHeight);
        var scale = Math.Min(preferred, autoFit);

        transformRoot.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(scale, scale);
    }

    /// <summary>Автоподбор при запуске (2026-09-04): на квадратных/маленьких экранах кассовых
    /// терминалов (сообщено пользователем — 4 терминала 1024×768, кнопки "Закрыть" в настройках
    /// и "Закрыть смену" на главном экране обрезались/уходили за пределы видимой области) 100%
    /// масштаб физически не помещается. При каждом запуске меряем реальную рабочую область
    /// экрана ЭТОГО окна и, если родной размер окна в неё не влезает, уменьшаем — но никогда не
    /// увеличиваем сверх выбранного пользователем масштаба, и никогда не уменьшаем, если место
    /// есть (на обычном широкоформатном мониторе autoFit всегда даёт 1.0, поведение не меняется).
    /// Небольшой запас (SafetyMarginPx) — на непредсказуемую высоту заголовка окна/рамки/
    /// панели задач Windows, которую WorkingArea не всегда учитывает день в день одинаково.</summary>
    private const double SafetyMarginPx = 24;

    private static double ComputeAutoFitScale(LayoutTransformControl transformRoot, double designWidth, double designHeight)
    {
        if (designWidth <= 0 || designHeight <= 0)
            return 1.0;

        try
        {
            var window = TopLevel.GetTopLevel(transformRoot) as Window;
            var screen = window is null ? null : (window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary);
            if (screen is null)
                return 1.0;

            var workArea = screen.WorkingArea;
            // WorkingArea — в физических пикселях; окно измеряется в DIP, поэтому делим на
            // системный масштаб Windows, иначе при 125%/150% масштабе ОС автоподбор ужимал бы
            // интерфейс без реальной нехватки места.
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var widthDip = workArea.Width / scaling - SafetyMarginPx;
            var heightDip = workArea.Height / scaling - SafetyMarginPx;

            var fitByWidth = widthDip / designWidth;
            var fitByHeight = heightDip / designHeight;
            return Math.Clamp(Math.Min(fitByWidth, fitByHeight), 0.5, 1.0);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"UI auto-fit scale detection failed: {ex.GetType().Name}", "WARNING");
            return 1.0;
        }
    }
}
