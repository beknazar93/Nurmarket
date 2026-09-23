using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Fail-closed authorization for cashier actions.</summary>
public sealed class PermissionService : IPermissionService
{
    private static readonly string[] PermissionContainers =
        ["permissions", "user_permissions", "access_rights", "rights"];

    private readonly IAuthApiService _authApi;
    private readonly IAutonomousAuthService _autonomous;

    public PermissionService(IAuthApiService authApi, IAutonomousAuthService autonomous)
    {
        _authApi = authApi;
        _autonomous = autonomous;
    }

    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        // 2026-09-10: автономный (офлайн, без NurCRM) режим — UserPayload здесь всегда пустой
        // (никакого логина через сервер не было вообще), поэтому обычная проверка ниже возвращала
        // "нет прав" НА ВСЁ, включая Склад — баг, пойманный на живом тесте ("в офлайн режиме нет
        // склада"). У автономного режима нет концепции тарифов/ролей NurCRM — единственный
        // локальный владелец имеет полный доступ; ограничивают его только личные PIN-коды
        // сотрудников (EmployeeAccessGate), это отдельный, уже полностью локальный механизм.
        if (_autonomous.IsCurrentSessionAutonomous)
            return true;

        // Просмотр продаж/клиентов/оплаты долга (старые чеки, повторная печать) открыт всем
        // кассирам независимо от роли в NurCRM — по явному решению владельца, а не по правам,
        // выставленным для сотрудника в панели NurCRM.
        if (permission == PosPermissions.ViewSales)
            return true;

        // Раньше роль "owner"/"admin" безусловно проходила ЛЮБУЮ проверку, минуя реальные
        // can_view_market_* флаги с сервера (2026-09-06, УБРАНО по явному запросу пользователя).
        // Эти флаги теперь отражают не только права сотрудника, но и тарифный план компании
        // (Старт/Стандарт — см. subscription_plan в ответе api/users/company/): can_view_market_supplier,
        // can_view_additional_services и т.п. включены сервером только на "Стандарт". Слепой пропуск
        // для владельца означал, что тарифные ограничения не действовали на самого частого
        // пользователя кассы — владельца небольшого магазина. Теперь роль вообще не даёт
        // никаких прав "бесплатно" — только реальные can_view_* флаги и явный список permissions.
        return ExtractPermissionNames(_authApi.UserPayload).Contains(permission);
    }

    public void Demand(string permission)
    {
        if (!HasPermission(permission))
            throw new PermissionDeniedException(permission);
    }

    internal static HashSet<string> ExtractPermissionNames(JsonElement payload)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(payload, result, 0);
        return result;
    }

    private static void Visit(JsonElement element, HashSet<string> result, int depth)
    {
        if (depth > 5)
            return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    result.Add(name);
                else
                    Visit(item, result, depth + 1);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("can_", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True)
            {
                result.Add(property.Name);
                continue;
            }

            if (PermissionContainers.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Name is "user" or "profile" or "data" or "cashier")
            {
                Visit(property.Value, result, depth + 1);
            }
        }
    }
}
