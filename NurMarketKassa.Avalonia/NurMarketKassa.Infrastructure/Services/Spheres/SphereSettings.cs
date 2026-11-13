using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-12 (ХМ15, ТЗ хозмаг): общие настройки сфер магазина — ОДИН ключ общих настроек компании
/// <see cref="UserPreferences.SphereSettingsJson"/> (облако — SettingsCloudSync.CompanyKeys, одинаково на всех кассах и в программе владельца).
/// Внутри — JSON-объект «раздел → значение»: <c>hardware</c> (размер магазина, «скан коробки»), позже другие сферы (electronics…).
/// Правила: пустое / не JSON / не объект / больше <see cref="MaxJsonBytes"/> → пустые настройки (умолчания), исключений наружу нет;
/// запись, которая не помещается в лимит, отклоняется (<see cref="TrySetSection"/> = false), старое значение остаётся.
/// У магазинов других сфер раздел <c>hardware</c> не читается — ничего не меняется.
/// </summary>
public static class SphereSettings
{
    public const int MaxJsonBytes = 16 * 1024;
    public const string CloudKey = "SphereSettingsJson";

    private const string HardwareSection = "hardware";

    /// <summary>Разбор строки ключа: безопасно. Пусто, битый JSON, не объект или больше лимита → пустой объект.</summary>
    public static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Раздел как объект (копия) или null.</summary>
    public static JsonObject? Section(string? json, string name) =>
        Parse(json)[name] is JsonObject o ? (JsonObject)o.DeepClone() : null;

    /// <summary>Новая строка ключа с заменённым разделом (null — раздел убран). null — не поместилась в лимит.</summary>
    public static string? WithSection(string? json, string name, JsonNode? value)
    {
        var root = Parse(json);
        if (value is null)
            root.Remove(name);
        else
            root[name] = value.DeepClone();
        var text = root.ToJsonString();
        return Encoding.UTF8.GetByteCount(text) > MaxJsonBytes ? null : text;
    }

    /// <summary>Записать раздел в настройки кассы (на диск и в облако — общим путём настроек). false — не поместилось.</summary>
    public static bool TrySetSection(string name, JsonNode? value)
    {
        var prefs = UserPreferences.Instance;
        var text = WithSection(prefs.SphereSettingsJson, name, value);
        if (text is null)
            return false;
        if (text == prefs.SphereSettingsJson)
            return true;
        prefs.SphereSettingsJson = text;
        prefs.SaveToDisk();
        return true;
    }

    // ───────────── hardware ─────────────

    /// <summary>Размер магазина «Хозтовары»: хозмаг у дома (по умолчанию) или строительный маркет (ТЗ хозмаг 3.2, решение В-Х1).</summary>
    public static HardwareSize GetHardwareSize(string? json = null)
    {
        var section = Section(json ?? UserPreferences.Instance.SphereSettingsJson, HardwareSection);
        return section?["size"] is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, "market", StringComparison.OrdinalIgnoreCase)
            ? HardwareSize.Market
            : HardwareSize.Shop;
    }

    /// <summary>«Скан коробки добавляет все штуки» (ХМ3, В-Х4). По умолчанию ВЫКЛ — владелец включает сам; новому магазину
    /// включается при выборе сферы (см. <see cref="SetHardwareChoice"/>).</summary>
    public static bool GetHardwarePackScan(string? json = null) =>
        Section(json ?? UserPreferences.Instance.SphereSettingsJson, HardwareSection)?["packScan"] is JsonValue v
        && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Признаки размера магазина (ТЗ 3.2) по сохранённому размеру.</summary>
    public static HardwareFeatures HardwareFeatures => HardwareProfile.FeaturesFor(GetHardwareSize());

    /// <summary>Записать размер магазина и (необязательно) галочку «скан коробки». false — не поместилось.</summary>
    public static bool SetHardwareChoice(HardwareSize size, bool? packScan = null)
    {
        var section = Section(UserPreferences.Instance.SphereSettingsJson, HardwareSection) ?? new JsonObject();
        section["size"] = size == HardwareSize.Market ? "market" : "shop";
        if (packScan is { } p)
            section["packScan"] = p;
        return TrySetSection(HardwareSection, section);
    }
}

/// <summary>
/// 2026-11-12 (ХМ15 / ХМ8): «цена прораба» — локальная отметка клиентов, которые покупают по оптовой цене. Ключ общих настроек
/// <see cref="UserPreferences.ClientPriceGroupsJson"/> (≤ <see cref="MaxJsonBytes"/>): JSON-массив id клиентов. Серверные уровни цен
/// (ТЗ бэкенду Б-55) заменят это после ответа Умара. Битый / слишком большой JSON → пустой список.
/// </summary>
public static class ClientPriceGroups
{
    public const int MaxJsonBytes = 16 * 1024;
    public const string CloudKey = "ClientPriceGroupsJson";

    public static HashSet<string> Parse(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return set;
        try
        {
            if (JsonNode.Parse(json) is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) && id.Length <= 64)
                        set.Add(id.Trim());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            set.Clear();
        }
        return set;
    }

    /// <summary>Клиент отмечен «Покупает по оптовой цене (прораб)».</summary>
    public static bool IsForeman(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId) && Parse(UserPreferences.Instance.ClientPriceGroupsJson).Contains(clientId.Trim());

    /// <summary>2026-11-13 (проверка, находка «высокая»): отметка «прораб» ведёт к оптовым ценам во всех чеках клиента и уходит облаком на все кассы — менять её может только тот, кто вправе менять цену
    /// (право «Изменение цены в кассе»; владелец и админ — всегда по роли). Сервиса прав нет (автономный режим, тесты) — не ограничиваем, как и остальные проверки цены.</summary>
    public static bool CanEdit(NurMarketKassa.Core.Contracts.IPermissionService? permissions) =>
        permissions is null || permissions.HasPermission(NurMarketKassa.Core.Contracts.PosPermissions.EditPrice);

    /// <summary>Поставить / снять отметку с проверкой права на цену. false — права нет или список не поместился в лимит (отметка не записана).</summary>
    public static bool TrySetForeman(string? clientId, bool value, NurMarketKassa.Core.Contracts.IPermissionService? permissions) =>
        CanEdit(permissions) && SetForeman(clientId, value);

    /// <summary>Поставить / снять отметку. false — список не поместился в лимит (отметка не записана).</summary>
    public static bool SetForeman(string? clientId, bool value)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return false;
        var prefs = UserPreferences.Instance;
        var set = Parse(prefs.ClientPriceGroupsJson);
        if (!(value ? set.Add(clientId.Trim()) : set.Remove(clientId.Trim())))
            return true;
        var arr = new JsonArray();
        foreach (var id in set.OrderBy(x => x, StringComparer.Ordinal))
            arr.Add(id);
        var text = arr.ToJsonString();
        if (Encoding.UTF8.GetByteCount(text) > MaxJsonBytes)
            return false;
        prefs.ClientPriceGroupsJson = text;
        prefs.SaveToDisk();
        return true;
    }
}
