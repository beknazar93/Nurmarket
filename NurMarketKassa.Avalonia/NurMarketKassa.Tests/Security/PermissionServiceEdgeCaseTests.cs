using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Security;

/// <summary>
/// БЕЗОПАСНОСТЬ (OWASP A01 Broken Access Control): граничные случаи разбора прав
/// в <see cref="PermissionService"/>. Сервис объявлен fail-closed — любые неожиданные,
/// пустые или враждебные полезные нагрузки обязаны приводить к ОТСУТСТВИЮ прав,
/// а не к их выдаче.
/// </summary>
public sealed class PermissionServiceEdgeCaseTests
{
    [Fact]
    public void ExtractPermissionNames_OnDefaultJsonElement_IsEmpty_FailClosed()
    {
        PermissionService.ExtractPermissionNames(default).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "user": {} }""")]
    [InlineData("""{ "data": { "user": { "role": "cashier" } } }""")]
    [InlineData("""{ "permissions": [] }""")]
    [InlineData("""{ "permissions": null }""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""123""")]
    [InlineData("\"some string\"")]
    public void ExtractPermissionNames_OnPayloadWithoutPermissions_IsEmpty_FailClosed(string json)
    {
        using var document = JsonDocument.Parse(json);

        PermissionService.ExtractPermissionNames(document.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void ExtractPermissionNames_StopsRecursion_BeyondDepthLimit()
    {
        // Маркер на «нормальной» глубине (root → data → user → permissions) обязан быть найден,
        // маркер, спрятанный за 8 уровнями контейнеров, — нет: Visit() отсекает depth > 5.
        var json = BuildNestedPermissionPayload(
            shallowMarker: "can_shallow_marker",
            deepMarker: "can_deep_marker");

        using var document = JsonDocument.Parse(json);

        var permissions = PermissionService.ExtractPermissionNames(document.RootElement);

        permissions.Should().Contain("can_shallow_marker");
        permissions.Should().NotContain(
            "can_deep_marker",
            "рекурсия по вложенным контейнерам ограничена глубиной 5 — сверхглубокая нагрузка не должна давать прав");
    }

    [Fact]
    public void ExtractPermissionNames_OnPathologicallyDeepPayload_TerminatesQuickly()
    {
        // Защита от DoS через рекурсию: 400 уровней вложенности не должны уронить стек
        // и не должны выполняться сколь-нибудь заметное время.
        var builder = new StringBuilder();
        const int depth = 400;
        for (var i = 0; i < depth; i++)
            builder.Append("""{"data":""");
        builder.Append("""{"permissions":["can_very_deep"]}""");
        builder.Append('}', depth);

        using var document = JsonDocument.Parse(
            builder.ToString(),
            new JsonDocumentOptions { MaxDepth = depth + 10 });

        var act = () => PermissionService.ExtractPermissionNames(document.RootElement);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void ExtractPermissionNames_IgnoresStringLiteralTrue_OnlyRealBooleanCounts()
    {
        using var document = JsonDocument.Parse("""
            {
              "user": {
                "can_view_settings": "true",
                "can_open_shift": "True",
                "can_void_receipt": 1,
                "can_apply_discount": "yes",
                "can_print_receipt": true
              }
            }
            """);

        var permissions = PermissionService.ExtractPermissionNames(document.RootElement);

        permissions.Should().BeEquivalentTo("can_print_receipt");
        permissions.Should().NotContain("can_view_settings", "строка \"true\" — это не JsonValueKind.True");
        permissions.Should().NotContain("can_open_shift");
        permissions.Should().NotContain("can_void_receipt", "число 1 — это не булево true");
        permissions.Should().NotContain("can_apply_discount");
    }

    [Fact]
    public void ExtractPermissionNames_OnMixedTypeArray_TakesOnlyStrings_WithoutThrowing()
    {
        using var document = JsonDocument.Parse("""
            {
              "permissions": [
                "can_view_sales",
                42,
                null,
                true,
                { "can_nested_object": true },
                "",
                "can_view_shifts",
                [ "can_view_reports" ]
              ]
            }
            """);

        var act = () => PermissionService.ExtractPermissionNames(document.RootElement);

        act.Should().NotThrow();
        var permissions = act();

        permissions.Should().Contain("can_view_sales");
        permissions.Should().Contain("can_view_shifts");
        permissions.Should().NotContain(string.Empty, "пустая строка не является именем права");
        permissions.Should().NotContain("42");
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    [InlineData("Owner")]
    [InlineData("OWNER")]
    [InlineData("superadmin")]
    [InlineData("SuperAdmin")]
    [InlineData("super_admin")]
    [InlineData("Administrator")]
    public void HasPermission_ForPrivilegedRole_WithoutRealFlags_DeniesEverything_ExceptViewSales(string role)
    {
        // 2026-09-06: роль "owner"/"admin" раньше безусловно проходила любую проверку —
        // это убрано по решению пользователя, чтобы владельца магазина на тарифе "Старт"
        // тоже ограничивали реальные can_view_market_* флаги с сервера, а не роль в NurCRM.
        var service = CreateService($$"""
            { "id": "42", "username": "boss", "role": "{{role}}" }
            """);

        service.HasPermission("can_view_settings").Should().BeFalse();
        service.HasPermission("can_do_absolutely_anything").Should().BeFalse();
        service.Invoking(x => x.Demand("can_close_shift")).Should().Throw<PermissionDeniedException>();

        // Единственное исключение — отдельная, не связанная с тарифами бизнес-логика:
        // просмотр продаж открыт всем независимо от роли и флагов (см. комментарий в HasPermission).
        service.HasPermission(PosPermissions.ViewSales).Should().BeTrue();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public void HasPermission_ForPrivilegedRole_WithRealFlag_GrantsOnlyThatFlag(string role)
    {
        var service = CreateService($$"""
            { "id": "42", "role": "{{role}}", "can_view_market_supplier": true }
            """);

        service.HasPermission(PosPermissions.ViewSupplier).Should().BeTrue();
        service.HasPermission("can_view_settings").Should().BeFalse();
    }

    [Theory]
    [InlineData("cashier")]
    [InlineData("Кассир")]
    [InlineData("manager")]
    [InlineData("admin_assistant")]
    [InlineData("")]
    public void HasPermission_ForNonPrivilegedRole_DeniesUnlistedPermission(string role)
    {
        var service = CreateService($$"""
            { "id": "7", "username": "user", "role": "{{role}}", "permissions": ["can_view_sales"] }
            """);

        service.HasPermission("can_view_sales").Should().BeTrue();
        service.HasPermission("can_view_settings").Should().BeFalse();
        service.Invoking(x => x.Demand("can_view_settings")).Should().Throw<PermissionDeniedException>();
    }

    [Fact]
    public void HasPermission_OnEmptyPayload_DeniesEverything_FailClosed()
    {
        var service = CreateService("{}");

        service.HasPermission("can_view_settings").Should().BeFalse();
        service.Invoking(x => x.Demand("can_view_settings")).Should().Throw<PermissionDeniedException>();
    }

    [Fact]
    public void HasPermission_IsCaseInsensitiveForPermissionName_ButRejectsUnknownOnes()
    {
        var service = CreateService("""
            { "id": "9", "permissions": ["can_view_sales"] }
            """);

        service.HasPermission("CAN_VIEW_SALES").Should().BeTrue();
        service.HasPermission("can_view_sales_extended").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasPermission_RejectsEmptyPermissionName(string? permission)
    {
        var service = CreateService("""{ "id": "9", "role": "admin" }""");

        service.Invoking(x => x.HasPermission(permission!)).Should().Throw<ArgumentException>();
    }

    private static PermissionService CreateService(string userPayloadJson)
    {
        var document = JsonDocument.Parse(userPayloadJson);
        return new PermissionService(new StubAuthApiService(document.RootElement.Clone()), new StubAutonomousAuthService());
    }

    /// <summary>Эти тесты проверяют именно обычный (NurCRM) fail-closed путь — автономный режим
    /// тут всегда выключен.</summary>
    private sealed class StubAutonomousAuthService : IAutonomousAuthService
    {
        public bool IsActivated => false;
        public bool HasLocalAccount => false;
        public bool IsCurrentSessionAutonomous => false;
        public Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default) => throw new NotSupportedException();
        public (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName) => throw new NotSupportedException();
        public (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password) => throw new NotSupportedException();
        public (bool Success, string? Email, string? DisplayName) TryAutoResume() => throw new NotSupportedException();
        public void EndAutonomousSession() => throw new NotSupportedException();
        public void MarkCatalogPreservedForNextLogin() => throw new NotSupportedException();
    }

    /// <summary>
    /// Строит payload с двумя ветками, обе — только из имён, по которым Visit() реально спускается
    /// ("data"/"user"/"profile"/"cashier"/"permissions"), иначе тест мерил бы не глубину, а фильтр имён.
    /// <list type="bullet">
    /// <item>ветка "data" — permissions на глубине 2 (Visit вызывается с depth 2), маркер попадает в результат;</item>
    /// <item>ветка "user" — 6 промежуточных контейнеров, Visit упирается в depth &gt; 5 и обрывается.</item>
    /// </list>
    /// </summary>
    private static string BuildNestedPermissionPayload(string shallowMarker, string deepMarker)
    {
        static string Wrap(int containerCount, string inner)
        {
            var containers = new[] { "data", "user", "profile", "cashier" };
            var json = inner;
            for (var i = containerCount - 1; i >= 0; i--)
                json = $$"""{"{{containers[i % containers.Length]}}":{{json}}}""";
            return json;
        }

        var shallow = $$"""{"permissions":["{{shallowMarker}}"]}""";
        var deep = Wrap(6, $$"""{"permissions":["{{deepMarker}}"]}""");

        return $$"""{"data":{{shallow}},"user":{{deep}}}""";
    }

    /// <summary>
    /// Минимальная заглушка <see cref="IAuthApiService"/>: <see cref="PermissionService"/>
    /// читает только <see cref="UserPayload"/>. Любой другой член — явный сбой теста,
    /// а не «тихая» заглушка.
    /// </summary>
    private sealed class StubAuthApiService : IAuthApiService
    {
        public StubAuthApiService(JsonElement userPayload) => UserPayload = userPayload;

        public JsonElement UserPayload { get; }

        public string? AccessToken => throw new NotSupportedException();
        public string? RefreshToken => throw new NotSupportedException();
        public string? ActiveBranchId => throw new NotSupportedException();

        public Task<bool> CanReachApiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RefreshAccessAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public void ClearSession() => throw new NotSupportedException();
        public void RestoreOfflineSession(OfflineAuthSession session) => throw new NotSupportedException();
        public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string? Layout, string? Mode, string? AmountUnit)> GetScaleSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<EmployeeInfoDto>?> GetEmployeesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EmployeeInfoDto?> CreateEmployeeAsync(string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteEmployeeAsync(string employeeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateEmployeeAccessAsync(string employeeId, EmployeeAccessFlags access, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<RoleInfoDto>?> GetRolesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RoleInfoDto?> CreateRoleAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRoleAsync(string roleId, CancellationToken ct = default) => throw new NotSupportedException();
        public void ApplyBranchFromProfile(JsonElement profile) => throw new NotSupportedException();
        public void ApplyUserFromProfile(JsonElement profile) => throw new NotSupportedException();
        public Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
