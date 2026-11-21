using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Лимиты тарифа. null — без лимита.</summary>
public sealed record PlanLimits(int? MaxStaff, int? MaxDevices, int? MaxCashboxes);

/// <summary>
/// 2026-11-20 (ответ Умара 20.11, В.2, тариф «Мини»): объект <c>plan</c> в GET api/main/capabilities/ — <c>{code, name, names{ru,ky,en,tr,uz}, descriptions{…}, period, limits{max_staff, max_devices, max_cashboxes},
/// allowed_clients[], upgrade_plan}</c>. Для «Мини» allowed_clients = ["mobile"]: касса на ПК, Android-касса и программа владельца получают 403 plan_client_not_allowed; у остальных тарифов allowed_clients = null (все клиенты).
/// Нет компании или тарифа — <c>plan: null</c>. Разбор терпимый: чужая форма или неверные типы полей — поле null/пусто, исключений наружу нет.
/// </summary>
public sealed record PlanInfo(
    string Code,
    string Name,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    string? Period,
    PlanLimits Limits,
    IReadOnlyList<string>? AllowedClients,
    string? UpgradePlan)
{
    /// <summary>Разбор ответа capabilities: свойство <c>plan</c> (в корне или внутри <c>capabilities</c>). null — плана нет (нет компании/тарифа, поле null или неожиданной формы).</summary>
    public static PlanInfo? Parse(JsonElement capabilities)
    {
        try
        {
            if (capabilities.ValueKind != JsonValueKind.Object)
                return null;
            if (!capabilities.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            {
                // {"capabilities": {"plan": {...}}} — на всякий случай, как и для списка признаков.
                if (capabilities.TryGetProperty("capabilities", out var inner) && inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("plan", out var innerPlan) && innerPlan.ValueKind == JsonValueKind.Object)
                    plan = innerPlan;
                else
                    return null;
            }

            var code = Str(plan, "code");
            if (string.IsNullOrWhiteSpace(code))
                return null;
            var limits = plan.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object
                ? new PlanLimits(Int(l, "max_staff"), Int(l, "max_devices"), Int(l, "max_cashboxes"))
                : new PlanLimits(null, null, null);
            IReadOnlyList<string>? allowed = null;
            if (plan.TryGetProperty("allowed_clients", out var ac) && ac.ValueKind == JsonValueKind.Array)
                allowed = ac.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    .Select(x => x.GetString()!.Trim()).ToList();
            return new PlanInfo(code.Trim(), Str(plan, "name") ?? code.Trim(), Map(plan, "names"), Map(plan, "descriptions"), Str(plan, "period"), limits, allowed, Str(plan, "upgrade_plan"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Тариф ограничивает клиентов (allowed_clients не null).</summary>
    public bool RestrictsClients => AllowedClients is not null;

    /// <summary>Этот тип клиента (<c>desktop_pos</c>, <c>android_pos</c>, <c>desktop_owner</c>, <c>mobile</c>) тарифом разрешён. Список не задан — разрешены все.</summary>
    public bool Allows(string clientType) =>
        AllowedClients is null || AllowedClients.Contains(clientType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Название на языке кассы (names → name → code).</summary>
    public string LocalizedName() => Pick(Names) ?? Name;

    /// <summary>Описание на языке кассы; пусто — сервер не прислал.</summary>
    public string LocalizedDescription() => Pick(Descriptions) ?? "";

    private static string? Pick(IReadOnlyDictionary<string, string> map)
    {
        var key = UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => "ky",
            AppLanguage.English => "en",
            AppLanguage.Turkish => "tr",
            AppLanguage.Uzbek => "uz",
            _ => "ru",
        };
        return map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : map.TryGetValue("ru", out var ru) && !string.IsNullOrWhiteSpace(ru) ? ru : null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static Dictionary<string, string> Map(JsonElement o, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.Object)
            foreach (var p in m.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                    map[p.Name] = s;
        return map;
    }
}
