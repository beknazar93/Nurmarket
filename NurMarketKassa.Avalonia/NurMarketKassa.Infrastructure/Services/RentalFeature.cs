namespace NurMarketKassa.Services;

/// <summary>2026-11-20, решение владельца: «убери из продуктовой сферы аренду! аренда по умолчанию только в строймаге, у остальных — через Маркетплейс».
/// ОДНО место, через которое проходят ВСЕ входы в прокат (меню кассы ☰ и «Ещё», окно оплаты, раздел «Продажи → Прокат» владельца, штраф в настройках, бот):
/// они спрашивают <see cref="MarketSpheres.HasRentals"/> = <see cref="IsAvailable"/>. Прокат доступен, если: сфера «Строймаг» (2026-11-20: Хозмаг — нет) ИЛИ модуль включён в Маркетплейсе
/// (<see cref="UserPreferences.RentalEnabled"/>) ИЛИ у компании уже есть данные проката (миграция: действующих клиентов не ломаем). Смена сферы данные проката не удаляет.
/// <see cref="PriceSom"/> — цена модуля (0 — бесплатно; владелец цену не задавал).</summary>
public static class RentalFeature
{
    public const decimal PriceSom = 0m;

    /// <summary>Чистое правило (для тестов): строймаркет, включённый модуль или уже имеющиеся данные проката. Данные проверяются лениво — только если первые два условия не дали ответа.</summary>
    public static bool IsEnabled(bool isHardware, bool moduleEnabled, Func<bool> hasExistingData) =>
        isHardware || moduleEnabled || hasExistingData();

    public static bool IsAvailable =>
        IsEnabled(MarketSpheres.Profile.HasRentalsByDefault, UserPreferences.Instance.RentalEnabled, RentalReceiptInfoStore.HasAny); // 2026-11-20: по умолчанию только в Строймаге

    /// <summary>Миграция: у компании есть прокат (файл условий чеков на этой кассе, открытые прокаты на сервере) — модуль включается сам и запоминается (уходит и на другие кассы через облачные настройки),
    /// чтобы пункты не пропали после смены сферы. Возвращает true, если признак только что поставлен.</summary>
    public static bool EnableForExisting()
    {
        var prefs = UserPreferences.Instance;
        if (prefs.RentalEnabled)
            return false;
        prefs.RentalEnabled = true;
        prefs.SaveToDisk();
        TariffGate.RaisePacksChanged(); // меню кассы и владельца перечитывают пункты
        PosLogger.Log("Прокат: у компании уже есть данные проката — модуль «Прокат (аренда)» включён автоматически.", "RENTAL");
        return true;
    }

    /// <summary>Запуск программы: если на кассе уже оформлялись прокаты — запомнить признак (без запроса к серверу).</summary>
    public static void MigrateLocal()
    {
        try
        {
            if (!UserPreferences.Instance.RentalEnabled && RentalReceiptInfoStore.HasAny())
                EnableForExisting();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: проверка данных не удалась ({ex.Message}).", "DEBUG");
        }
    }
}
