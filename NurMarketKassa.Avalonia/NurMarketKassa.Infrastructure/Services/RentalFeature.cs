namespace NurMarketKassa.Services;

/// <summary>2026-11-20, решение владельца: «убери из продуктовой сферы аренду! аренда по умолчанию только в строймаге, у остальных — через Маркетплейс».
/// ОДНО место, через которое проходят ВСЕ входы в прокат (меню кассы ☰ и «Ещё», окно оплаты, раздел «Продажи → Прокат» владельца, штраф в настройках, бот):
/// они спрашивают <see cref="MarketSpheres.HasRentals"/> = <see cref="IsAvailable"/>. Прокат доступен, если: сфера «Строймаг» (входит в тариф) ИЛИ модуль включён в Маркетплейсе / сервером
/// (CompanyModules; без ответа сервера — признак <see cref="UserPreferences.RentalEnabled"/>). 2026-11-21 (решение владельца, дважды): старые данные проката модуль больше НЕ включают
/// (у «Продуктового» с прежними прокатами пункт «Прокат» не должен появляться сам). Смена сферы данные проката не удаляет.
/// <see cref="PriceSom"/> — прежняя местная цена (0); у не-Строймага цену модуля даёт сервер (public/modules), см. MarketplaceModules.</summary>
public static class RentalFeature
{
    public const decimal PriceSom = 0m;

    /// <summary>Чистое правило (для тестов): строймаркет или включённый модуль.</summary>
    public static bool IsEnabled(bool isHardware, bool moduleEnabled) => isHardware || moduleEnabled;

    // 2026-11-21: решает модуль rental с сервера (CompanyModules: профиль + «добавил / убрал» владелец). Нет ответа сервера — Строймаг или признак из Маркетплейса.
    // Данные проката («у компании уже есть») модуль не включают: раньше авто-включение показывало «Прокат» в Продуктовом.
    public static bool IsAvailable =>
        CompanyModules.IsOn(ModuleIds.Rental, MarketSpheres.Profile.HasRentalsByDefault, UserPreferences.Instance.RentalEnabled); // 2026-11-20: по умолчанию только в Строймаге
}
