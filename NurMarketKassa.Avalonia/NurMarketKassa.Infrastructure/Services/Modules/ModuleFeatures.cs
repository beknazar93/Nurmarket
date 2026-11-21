namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (ответ Умара 20.11, В.16): «показывать функцию по модулю, а не по локальному жёсткому флагу» — одно место для простых проверок. Каждое свойство =
/// модуль сервера (<see cref="CompanyModules.IsOn"/>) с запасным значением по профилю сферы кассы, когда сервер недоступен или не знает модуля (прежнее поведение).</summary>
public static class ModuleFeatures
{
    /// <summary>Размеры и цвета у товара (модуль variants; профиль «Одежда»).</summary>
    public static bool Variants => CompanyModules.IsOn(ModuleIds.Variants, MarketSpheres.Profile.ShowVariants || MarketSpheres.IsClothing);

    /// <summary>Блок «Консультант» в окне оплаты (модуль consultant; профиль «Одежда»).</summary>
    public static bool Consultant => CompanyModules.IsOn(ModuleIds.Consultant, MarketSpheres.IsClothing);
}
