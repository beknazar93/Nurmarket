using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-21 (сфера «Базар», ТЗ Контейнер-базар, ответ сервера 20.11 В.16): модули «Базара» — id те же, что у сервера (users/company/modules/):
/// <c>category_tiles</c> (плитки категорий), <c>volume_price</c> (цена по количеству), <c>haggle</c> (торг), <c>currencies</c> (валюты), <c>invoice_cargo</c> (накладная с «карго», только опт).
/// Всё поведение «Базара» включается ТОЛЬКО через <see cref="IsOn"/>: в других сферах без включённого модуля касса работает как раньше.
/// Порядок решения: 1) выбор владельца в настройках кассы (раздел <c>bazaar</c>, ключ <c>modules</c>: id → true/false; «выключено» — выключено) → 2) общий реестр модулей
/// (<see cref="CompanyModules"/>: ответ сервера, он же включает модуль в любой сфере) с запасным значением 3) умолчание профиля «Базар» (режим Розница / Опт).
/// </summary>
public static class BazaarModules
{
    public const string CategoryTiles = "category_tiles";
    public const string VolumePrice = "volume_price";
    public const string Haggle = "haggle";
    public const string Currencies = "currencies";
    public const string InvoiceCargo = "invoice_cargo";

    public static readonly IReadOnlyList<string> All = new[] { CategoryTiles, VolumePrice, Haggle, Currencies, InvoiceCargo };

    private const string Section = "bazaar";
    private const string Key = "modules";

    /// <summary>Модуль включён для этой кассы. Решает общий реестр модулей (<see cref="CompanyModules.IsOn"/>: ответ сервера users/company/modules/ с запасным значением по профилю кассы) —
    /// так модуль «Базара» можно добавить и в любой другой сфере. Выбор владельца в настройках кассы главнее: «выключено» — выключено; «включено» — как явный признак.</summary>
    public static bool IsOn(string id)
    {
        if (string.IsNullOrEmpty(id) || !All.Contains(id))
            return false;
        var chosen = OwnerChoice(id);
        if (chosen == false)
            return false;
        var profileDefault = BazaarProfile.IsActive && DefaultFor(id, SphereSettings.GetBazaarMode());
        return CompanyModules.IsOn(id, profileDefault, explicitLocal: chosen == true);
    }

    /// <summary>Умолчание профиля «Базар»: четыре модуля — в обоих режимах, «Накладная + карго» — только в опте (ответ сервера 20.11, В.16).</summary>
    public static bool DefaultFor(string id, BazaarMode mode) => id switch
    {
        CategoryTiles or VolumePrice or Haggle or Currencies => true,
        InvoiceCargo => mode == BazaarMode.Wholesale,
        _ => false,
    };

    /// <summary>Выбор владельца в настройках кассы: true / false; null — не трогал.</summary>
    public static bool? OwnerChoice(string id)
    {
        var section = SphereSettings.Section(UserPreferences.Instance.SphereSettingsJson, Section);
        return section?[Key] is JsonObject modules && modules[id] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>Записать выбор владельца (null — вернуть умолчание профиля). false — не поместилось в лимит настроек.</summary>
    public static bool SetOwnerChoice(string id, bool? value)
    {
        if (!All.Contains(id))
            return false;
        var section = SphereSettings.Section(UserPreferences.Instance.SphereSettingsJson, Section) ?? new JsonObject();
        var modules = section[Key] as JsonObject ?? new JsonObject();
        if (value is { } b)
            modules[id] = b;
        else
            modules.Remove(id);
        if (modules.Count == 0)
            section.Remove(Key);
        else
            section[Key] = modules;
        return SphereSettings.TrySetSection(Section, section);
    }

    public static bool CategoryTilesOn => IsOn(CategoryTiles);
    public static bool VolumePriceOn => IsOn(VolumePrice);
    public static bool HaggleOn => IsOn(Haggle);
    public static bool CurrenciesOn => IsOn(Currencies);
    public static bool InvoiceCargoOn => IsOn(InvoiceCargo);

    /// <summary>Режим «Опт» — оптовая цена по умолчанию, накладная и «вернёт до» (пункт 6 задания). В других сферах — false.</summary>
    public static bool WholesaleMode => BazaarProfile.IsActive && SphereSettings.GetBazaarMode() == BazaarMode.Wholesale;
}
