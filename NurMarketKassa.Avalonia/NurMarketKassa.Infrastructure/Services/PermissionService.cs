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

        // 2026-10-18: «Интерфейс кассира», «Продажа», «Смены» — со своими правилами (не запирать
        // владельца/админа и компании, где доступы сотрудникам не выставлены).
        if (PosPermissions.CashierWork.Contains(permission, StringComparer.OrdinalIgnoreCase))
            return IsCashierWorkAllowed(permission, _authApi.UserPayload);

        // Раньше роль "owner"/"admin" безусловно проходила ЛЮБУЮ проверку, минуя реальные
        // can_view_market_* флаги с сервера (2026-09-06, УБРАНО по явному запросу пользователя).
        // Эти флаги теперь отражают не только права сотрудника, но и тарифный план компании
        // (Старт/Стандарт — см. subscription_plan в ответе api/users/company/): can_view_market_supplier,
        // can_view_additional_services и т.п. включены сервером только на "Стандарт". Слепой пропуск
        // для владельца означал, что тарифные ограничения не действовали на самого частого
        // пользователя кассы — владельца небольшого магазина. Теперь роль вообще не даёт
        // никаких прав "бесплатно" — только реальные can_view_* флаги и явный список permissions.
        if (ExtractPermissionNames(_authApi.UserPayload).Contains(permission))
            return true;

        // 2026-10-30, живой баг (программа владельца): сессия недействительна → права не подтвердить, а «Клиенты» отвечали
        // «Недостаточно прав». Пока нужен повторный вход, роль owner/admin из последнего успешного профиля (он остаётся в памяти,
        // а на диске — в сохранённом входе) даёт доступ к разделам; данные всё равно не загрузятся без входа, откажет сервер.
        if (SessionExpiryMonitor.IsExpired && HasOwnerOrAdminRole(_authApi.UserPayload))
            return true;

        // 2026-10-01: разделы, которые сайт открывает владельцу всегда (PosPermissions.OwnerSections).
        // Тарифные флаги (поставщики, весы, этикетки…) сюда не входят — их по-прежнему решает сервер.
        return PosPermissions.OwnerSections.Contains(permission, StringComparer.OrdinalIgnoreCase)
               && IsOwnerRole(_authApi.UserPayload);
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-Г, Б3): владелец или админ магазина (роль owner/admin) — ему можно показывать
    /// ключи ИИ в настройках; кассиру — нет. Автономный режим и программа владельца — владелец.</summary>
    public static bool IsOwnerOrAdmin(JsonElement payload)
    {
        if (AppMode.IsOwner)
            return true;
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        return user.ValueKind == JsonValueKind.Object
               && user.TryGetProperty("role", out var role)
               && role.ValueKind == JsonValueKind.String
               && role.GetString() is { } roleName
               && (roleName.Equals("owner", StringComparison.OrdinalIgnoreCase) || roleName.Equals("admin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>2026-10-30, редактор накладных закупок: право «Редактор документов закупок» — ТОЛЬКО роль owner из профиля. Админ, право «Настройки»,
    /// автономный режим и сама программа владельца (AppMode.IsOwner — она бывает у сотрудника) права не дают. Когда сервер начнёт отдавать флаг
    /// can_edit_purchase_documents, его можно будет учитывать здесь; окончательную проверку всё равно делает сервер.</summary>
    public static bool CanEditPurchaseDocuments(JsonElement payload) => ReceiptEditPolicy.IsOwnerRole(payload);

    /// <summary>Роль owner/admin именно из профиля (без AppMode.IsOwner: программа владельца бывает и у сотрудника).</summary>
    internal static bool HasOwnerOrAdminRole(JsonElement payload)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        return user.ValueKind == JsonValueKind.Object
               && user.TryGetProperty("role", out var role)
               && role.ValueKind == JsonValueKind.String
               && role.GetString() is { } roleName
               && (roleName.Equals("owner", StringComparison.OrdinalIgnoreCase) || roleName.Equals("admin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOwnerRole(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("role", out var role)
        && role.ValueKind == JsonValueKind.String
        && string.Equals(role.GetString(), "owner", StringComparison.OrdinalIgnoreCase);

    /// <summary>2026-10-18, ревью безопасности: «доступы сотруднику выставлены» — только по ЛИЧНЫМ флагам
    /// (базовые/секторные доступы формы «Управление доступами», EmployeeAccessFlags). Флаги модулей компании
    /// (showcase, documents, whatsapp, instagram, telegram и т. п.) сервер ставит сам — по ним не судим.</summary>
    private static readonly string[] PersonalAccessFlags =
    {
        "can_view_cashier", "can_view_sale", "can_view_shifts", "can_view_products", "can_view_analytics",
        "can_view_clients", "can_view_employees", "can_view_cashbox", "can_view_settings", "can_view_brand_category",
        // 2026-10-18, повторное ревью: секторный «Документы» и личные доступы «Касса Маркета» (EmployeeAccessFlags).
        "can_view_document", "can_view_market_discount", "can_view_market_edit_price", "can_view_market_delete_cart_item",
        "can_view_market_employee_return",
        // can_view_market_procurement / can_view_market_supplier намеренно не входят: по комментарию выше их сервер
        // ставит по тарифу («Стандарт»), а не владелец сотруднику. ПРОВЕРИТЬ НА NBS: выдаёт ли сайт их роли сам.
    };

    /// <summary>2026-10-18: доступы работы на кассе. Разрешено, если: владелец или админ (роль owner/admin
    /// или доступ «Настройки» — тот же признак, что у EmployeeAccessGate); доступ включён; или сотруднику
    /// не выставлено ни одного личного доступа (PersonalAccessFlags) — как было до проверки.</summary>
    private static bool IsCashierWorkAllowed(string permission, JsonElement payload)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        if (user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && role.GetString() is { } roleName
            && (roleName.Equals("owner", StringComparison.OrdinalIgnoreCase) || roleName.Equals("admin", StringComparison.OrdinalIgnoreCase)))
            return true;

        // 2026-10-28 (Б-16): сервер может явно закрыть работу на кассе сотруднику полем can_use_pos=false (у всех true). Владельца и
        // админа это не касается (проверено выше). Поля нет (старый сервер) — прежнее поведение.
        if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("can_use_pos", out var usePos) && usePos.ValueKind == JsonValueKind.False)
            return false;

        var names = ExtractPermissionNames(payload);
        if (names.Contains(PosPermissions.ViewSettings) || names.Contains(permission))
            return true;

        return !PersonalAccessFlags.Any(names.Contains);
    }

    /// <summary>2026-10-18: понятный отказ по доступу работы на кассе — что включить на сайте.</summary>
    public static string CashierWorkDeniedMessage(string permission)
    {
        var (ru, ky, en, tr, uz) = permission switch
        {
            PosPermissions.Sale => ("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"),
            PosPermissions.ViewShifts => ("Смены", "Сменалар", "Shifts", "Vardiyalar", "Smenalar"),
            _ => ("Интерфейс кассира", "Кассир интерфейси", "Cashier interface", "Kasiyer arayüzü", "Kassir interfeysi"),
        };
        return Tr.T(
            $"Нет доступа «{ru}». Попросите владельца включить его на сайте app.nurcrm.kg: Сотрудники → ваш сотрудник → Доступы → «{ru}» (или в кассе владельца: Настройки → Сотрудники → 🔐).",
            $"«{ky}» укугу жок. Ээсинен аны app.nurcrm.kg сайтында күйгүзүүнү сураныңыз: Кызматкерлер → сиздин кызматкер → Укуктар → «{ky}» (же ээсинин кассасында: Жөндөөлөр → Кызматкерлер → 🔐).",
            $"No “{en}” permission. Ask the owner to turn it on at app.nurcrm.kg: Employees → your employee → Permissions → “{en}” (or in the owner's till: Settings → Employees → 🔐).",
            $"«{tr}» erişimi yok. Sahibinden app.nurcrm.kg sitesinde açmasını isteyin: Personel → personeliniz → Erişimler → «{tr}» (veya sahibinin kasasında: Ayarlar → Personel → 🔐).",
            $"«{uz}» huquqi yo'q. Egasidan uni app.nurcrm.kg saytida yoqishni so'rang: Xodimlar → sizning xodimingiz → Huquqlar → «{uz}» (yoki egasining kassasida: Sozlamalar → Xodimlar → 🔐).");
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
