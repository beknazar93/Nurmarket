using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-21 (просьба владельца «при включении режима слабого ПК не обязательно перезаходить»): режим слабого устройства применяется на лету.
/// Что читало настройку один раз при построении и теперь пересобирается по событию <see cref="UserPreferences.LowPerformanceModeChanged"/>:
/// ресурсы темы (прозрачные окна диалогов ↔ обычные, узоры-орнаменты), обои и размытие главного окна кассы, «стекло», узоры экрана покупателя.
/// Что читает настройку при каждом использовании (подхватывает само): анимации и затемнение под модалками (ModalMotion), размытие под модалками,
/// интервалы опроса каталога и сервера (SyncService, AvaloniaCatalogCacheService), анимация меню. Остаётся до перезапуска ТОЛЬКО программная отрисовка
/// (способ отрисовки Avalonia задаётся при запуске) — см. <see cref="RenderModeSettings.LowPerformanceRestartNeeded"/>.</summary>
public static class LowPerformanceLive
{
    private static bool _attached;

    /// <summary>Сколько раз режим применялся на лету за этот запуск (тесты, диагностика).</summary>
    public static int AppliedCount { get; private set; }

    /// <summary>Подписаться на смену режима (App при запуске). Повторный вызов ничего не делает.</summary>
    public static void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        UserPreferences.LowPerformanceModeChanged += () =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        };
    }

    /// <summary>Пересобрать то, что зависит от режима. Вызывается из потока интерфейса; сбой одной части не мешает остальным.</summary>
    public static void Apply()
    {
        var prefs = UserPreferences.Instance;
        AppliedCount++;
        PosLogger.Log($"Режим слабого устройства {(prefs.LowPerformanceMode ? "включён" : "выключен")} — применяется без перезапуска.", "SETTINGS");
        Step("тема", () => AccentThemeService.Apply(prefs.AccentTheme, prefs.DarkTheme));
        Step("обои и размытие кассы", () => NurMarketKassa.AvaloniaHost.App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshBackgroundWallpaper());
        Step("экран покупателя", () => NurMarketKassa.AvaloniaHost.App.GetRequiredService<AvaloniaCustomerDisplayService>().RefreshStyle());
        // Затемнение, уже стоящее под открытой модалкой, пересоздавать не нужно: следующий показ модалки читает режим заново.
    }

    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Режим слабого устройства: «{what}» не обновлено ({ex.Message}).", "WARNING");
        }
    }
}
