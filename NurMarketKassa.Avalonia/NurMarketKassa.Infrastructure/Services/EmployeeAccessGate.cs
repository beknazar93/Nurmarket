using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-09-08: гейт для 4 защищённых действий кассы (удаление позиции из корзины,
/// удаление товара со склада, редактирование товара, добавление товара) — по просьбе владельца.
/// Владелец/админ (есть право ViewSettings — тот же признак, что открывает раздел Настройки, где
/// живёт сам редактор кодов) действует без кода; у сотрудника без ViewSettings запрашивается его
/// личный код для конкретного действия. Если для действия НИ У ОДНОГО сотрудника код не задан —
/// гейт не активен (иначе включение этой функции сразу заблокировало бы всех, у кого владелец ещё
/// не успел вписать коды в Настройки → Сотрудники).</summary>
public static class EmployeeAccessGate
{
    public const string CartDelete = "cartDelete";
    public const string WarehouseDelete = "warehouseDelete";
    public const string ProductEdit = "productEdit";
    public const string ProductAdd = "productAdd";

    private static string? CodeFor(string action, EmployeeAccessCode e) => action switch
    {
        CartDelete => e.CartDeleteCode,
        WarehouseDelete => e.WarehouseDeleteCode,
        ProductEdit => e.ProductEditCode,
        ProductAdd => e.ProductAddCode,
        _ => null,
    };

    /// <summary>2026-09-08: для CartDelete на сайте app.nurcrm.kg (Моя компания → Касса) уже
    /// есть настоящее серверное поле "Код на удаление из корзины" (CompanyDto.CartDeleteCode) —
    /// владелец явно попросил, чтобы касса его видела и использовала, а не только локальные
    /// коды из Настройки → Сотрудники. Оба источника работают одновременно: подходит любой —
    /// код с сайта или личный код сотрудника.</summary>
    private static string? ServerCodeFor(string action) =>
        action == CartDelete ? CompanyInfoService.LastCompany?.CartDeleteCode : null;

    /// <summary>true, если для этого действия нужно спрашивать код у текущего аккаунта.</summary>
    public static bool IsActiveFor(string action, IPermissionService? permissions)
    {
        var isOwnerOrAdmin = permissions?.HasPermission(PosPermissions.ViewSettings) ?? true;
        if (isOwnerOrAdmin)
            return false;

        if (!string.IsNullOrWhiteSpace(ServerCodeFor(action)))
            return true;

        foreach (var e in UserPreferences.Instance.EmployeeAccessCodes)
            if (!string.IsNullOrWhiteSpace(CodeFor(action, e)))
                return true;
        return false;
    }

    /// <summary>Введённый код совпадает либо с кодом с сайта (если он для этого действия есть),
    /// либо с личным кодом ХОТЬ ОДНОГО сотрудника.</summary>
    public static bool TryValidate(string action, string? enteredCode)
    {
        if (string.IsNullOrWhiteSpace(enteredCode))
            return false;

        var trimmed = enteredCode.Trim();

        var serverCode = ServerCodeFor(action);
        if (!string.IsNullOrWhiteSpace(serverCode) && string.Equals(serverCode!.Trim(), trimmed, System.StringComparison.Ordinal))
            return true;

        foreach (var e in UserPreferences.Instance.EmployeeAccessCodes)
        {
            var code = CodeFor(action, e);
            if (!string.IsNullOrWhiteSpace(code) && string.Equals(code!.Trim(), trimmed, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
