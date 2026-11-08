namespace NurMarketKassa.Services;

/// <summary>2026-11-08 (запрос владельца «включить в настройках авто отметку»): точки вызова автоотметки сотрудника в табеле. Режим — UserPreferences.StaffAutoMarkMode:
/// "off"/пусто (по умолчанию — ничего не пишется), "shift" (при открытии смены кассиром), "login" (при входе в кассу). Закрытие смены дописывает часы в любом включённом
/// режиме. Любая ошибка только в журнал — открытие/закрытие смены и вход не должны от неё зависеть. Деньги и зарплату не трогает.</summary>
public static class StaffAutoMark
{
    public const string ModeOff = "off";
    public const string ModeShift = "shift";
    public const string ModeLogin = "login";

    public static string Mode
    {
        get
        {
            var m = UserPreferences.Instance.StaffAutoMarkMode?.Trim().ToLowerInvariant();
            return m is ModeShift or ModeLogin ? m : ModeOff;
        }
    }

    public static void OnLogin() => Run(Mode == ModeLogin, arrive: true);

    public static void OnShiftOpened() => Run(Mode == ModeShift, arrive: true);

    public static void OnShiftClosed() => Run(Mode != ModeOff, arrive: false);

    private static void Run(bool enabled, bool arrive)
    {
        if (!enabled)
            return;
        try
        {
            var key = PosApp.CurrentUserId;
            var name = PosApp.CurrentUserDisplayName;
            var service = TimesheetMarksService.Default;
            if (arrive)
            {
                if (service.AutoArrive(true, key, name))
                    PosLogger.Log($"Табель: автоотметка прихода — {name ?? key}.", "STAFF");
            }
            else if (service.AutoLeave(true, key))
                PosLogger.Log($"Табель: автоотметка ухода (часы дописаны) — {name ?? key}.", "STAFF");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Табель: автоотметка не записана: {ex.Message}", "STAFF");
        }
    }
}
