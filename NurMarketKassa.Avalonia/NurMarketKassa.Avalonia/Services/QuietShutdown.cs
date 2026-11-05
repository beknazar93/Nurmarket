using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №3, Р3-02): закрытие программы «извне» — выключение Windows, установщик/скрипт обновления
/// (сигнал «--close-quiet»). В этом случае вопросы «Смена не закрыта» / «Сохранить и выйти?» / «Приходы в фоне» не задаются:
/// без срока они держали окно, а через 15 с установщик убивал процесс (отложенные записи терялись). Смена при этом НЕ закрывается —
/// остаётся открытой, как при «Выйти без закрытия смены». Обычное закрытие кнопкой пользователя — как раньше.</summary>
public static class QuietShutdown
{
    private static volatile bool _requested;

    /// <summary>Пришёл сигнал «закройся тихо» (второй запуск с --close-quiet) либо уже видели закрытие по сигналу системы.</summary>
    public static bool Requested
    {
        get => _requested;
        set => _requested = value;
    }

    /// <summary>true — закрытие нельзя блокировать вопросом: сигнал тихого закрытия, причина окна — выключение Windows / остановка
    /// приложения, либо Windows сейчас завершает сеанс.</summary>
    public static bool ShouldSkipPrompts(WindowClosingEventArgs? e = null)
    {
        if (_requested)
            return true;
        if (e is not null && (e.CloseReason == WindowCloseReason.OSShutdown || e.CloseReason == WindowCloseReason.ApplicationShutdown))
        {
            _requested = true;
            return true;
        }
        if (IsWindowsShuttingDown())
        {
            _requested = true;
            return true;
        }
        return false;
    }

    private const int SM_SHUTTINGDOWN = 0x2000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Windows завершает сеанс (SM_SHUTTINGDOWN). Вне Windows — всегда false.</summary>
    public static bool IsWindowsShuttingDown()
    {
        try
        {
            return OperatingSystem.IsWindows() && GetSystemMetrics(SM_SHUTTINGDOWN) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
