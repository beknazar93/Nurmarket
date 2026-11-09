using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-09, жалоба клиента: «Недостаточно прав… доступ „Склад“», хотя все права выданы и программа перезапущена.
/// Права раздела касса решает сама по профилю, прочитанному при входе (или по сохранённому входу, если сервер не ответил за 3 с).
/// Проверяем: профиль перечитывается (перед отказом и после возврата связи), строка «для поддержки», роль владельца в обёртке.
/// Сервер — заглушка, настоящий не трогается. Коллекция SessionExpiry — без параллельности (статическое состояние).</summary>
[Collection("SessionExpiry")]
public sealed class PermissionRecheckTests : IDisposable
{
    public PermissionRecheckTests()
    {
        SessionExpiryMonitor.ResetForTests();
        PermissionRecheck.ResetForTests();
    }

    public void Dispose()
    {
        SessionExpiryMonitor.ResetForTests();
        PermissionRecheck.ResetForTests();
    }

    [Fact]
    public async Task Refresh_ServerNowSendsWarehouseFlag_SectionOpens()
    {
        // Сохранённый вход сотрудника без «Склада» (права на момент прошлого входа); на сайте «Склад» уже выдан.
        var api = new StubAuthApi("""{ "id": "7", "role": "salesperson", "permissions": ["can_view_sale"] }""")
        {
            Profile = """{ "id": "7", "role": "salesperson", "can_view_sale": true, "can_view_products": true }""",
        };
        PermissionRecheck.MarkSavedSession();
        var permissions = new PermissionService(api, new StubAutonomous());
        permissions.HasPermission(PosPermissions.ViewProducts).Should().BeFalse("права из сохранённого входа устарели");

        var ok = await PermissionRecheck.RefreshProfileAsync(api, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5));

        ok.Should().BeTrue();
        api.ProfileCalls.Should().Be(1);
        permissions.HasPermission(PosPermissions.ViewProducts).Should().BeTrue("после перечитывания профиля «Склад» есть");
        PermissionRecheck.FromSavedSession.Should().BeFalse();
        PermissionRecheck.LiveProfileAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_ServerStillWithoutFlag_DeniedAndDescribed()
    {
        var api = new StubAuthApi("""{ "id": "7", "role": "admin" }""")
        {
            Profile = """{ "id": "7", "role": "admin", "can_view_sale": true, "can_view_products": false }""",
        };
        var ok = await PermissionRecheck.RefreshProfileAsync(api, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5));

        ok.Should().BeTrue();
        new PermissionService(api, new StubAutonomous()).HasPermission(PosPermissions.ViewProducts).Should().BeFalse("сервер прислал false");
        var line = PermissionRecheck.Describe(PosPermissions.ViewProducts, api.UserPayload);
        line.Should().Contain("can_view_products = false").And.Contain("admin").And.Contain(": 1;", "включён один доступ can_view_sale");
    }

    [Fact]
    public async Task Refresh_ServerFails_RightsKept_FailureInDescription()
    {
        var api = new StubAuthApi("""{ "id": "7", "role": "salesperson", "permissions": ["can_view_sale"] }""")
        {
            ProfileError = new ApiException("Service Unavailable", 503),
        };
        var before = api.UserPayload.GetRawText();

        var ok = await PermissionRecheck.RefreshProfileAsync(api, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5));

        ok.Should().BeFalse();
        api.UserPayload.GetRawText().Should().Be(before, "профиль не получен — права не трогаем");
        PermissionRecheck.Describe(PosPermissions.ViewProducts, api.UserPayload).Should().Contain("HTTP 503");
    }

    [Fact]
    public async Task Refresh_NoTokenAutonomousOrExpired_NoRequest()
    {
        var noToken = new StubAuthApi("""{ "id": "7" }""") { Token = null, Profile = """{ "id": "7" }""" };
        (await PermissionRecheck.RefreshProfileAsync(noToken, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5))).Should().BeFalse();
        noToken.ProfileCalls.Should().Be(0);

        var autonomous = new StubAuthApi("""{ "id": "7" }""") { Profile = """{ "id": "7" }""" };
        (await PermissionRecheck.RefreshProfileAsync(autonomous, new StubAutonomous(isAutonomous: true), "тест", TimeSpan.FromSeconds(5))).Should().BeFalse();
        autonomous.ProfileCalls.Should().Be(0);

        SessionExpiryMonitor.Report("тест");
        var expired = new StubAuthApi("""{ "id": "7" }""") { Profile = """{ "id": "7" }""" };
        (await PermissionRecheck.RefreshProfileAsync(expired, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5))).Should().BeFalse();
        expired.ProfileCalls.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_ConcurrentCalls_ShareOneRequest()
    {
        var gate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new StubAuthApi("""{ "id": "7" }""") { PendingProfile = gate.Task };

        var first = PermissionRecheck.RefreshProfileAsync(api, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5));
        var second = PermissionRecheck.RefreshProfileAsync(api, new StubAutonomous(), "тест", TimeSpan.FromSeconds(5));
        gate.SetResult(JsonDocument.Parse("""{ "id": "7", "can_view_products": true }""").RootElement.Clone());

        (await first).Should().BeTrue();
        (await second).Should().BeTrue();
        api.ProfileCalls.Should().Be(1);
    }

    [Fact]
    public async Task ServerRecovered_ReloadsProfile()
    {
        var api = new StubAuthApi("""{ "id": "7", "role": "salesperson" }""")
        {
            Profile = """{ "id": "7", "role": "salesperson", "can_view_products": true }""",
        };
        PermissionRecheck.Attach(() => api, () => new StubAutonomous());

        await PermissionRecheck.OnServerRecoveredAsync();

        api.ProfileCalls.Should().Be(1);
        new PermissionService(api, new StubAutonomous()).HasPermission(PosPermissions.ViewProducts).Should().BeTrue();
        PermissionRecheck.Attach(() => null, () => null); // не держать заглушку в статическом поле
    }

    [Theory]
    [InlineData("""{ "user": { "id": "1", "role": "owner" } }""", true)]
    [InlineData("""{ "user": { "id": "1", "role": "salesperson" } }""", false)]
    [InlineData("""{ "id": "1", "role": "owner" }""", true)]
    public void OwnerRole_InWrappedPayload_OpensOwnerSections(string payload, bool expected)
    {
        var service = new PermissionService(new StubAuthApi(payload), new StubAutonomous());
        service.HasPermission(PosPermissions.ViewProducts).Should().Be(expected);
        service.HasPermission(PosPermissions.ViewSupplier).Should().BeFalse("тарифные флаги роль по-прежнему не даёт");
    }

    private sealed class StubAutonomous : IAutonomousAuthService
    {
        public StubAutonomous(bool isAutonomous = false) => IsCurrentSessionAutonomous = isAutonomous;
        public bool IsActivated => false;
        public bool HasLocalAccount => false;
        public bool IsCurrentSessionAutonomous { get; }
        public Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default) => throw new NotSupportedException();
        public (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName) => throw new NotSupportedException();
        public (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password) => throw new NotSupportedException();
        public (bool Success, string? Email, string? DisplayName) TryAutoResume() => throw new NotSupportedException();
        public void EndAutonomousSession() => throw new NotSupportedException();
        public void MarkCatalogPreservedForNextLogin() => throw new NotSupportedException();
    }

    /// <summary>Заглушка API: профиль — из строки, применение — как в NurMarketApiClient.ApplyUserFromProfile (объект пользователя).</summary>
    private sealed class StubAuthApi : IAuthApiService
    {
        public StubAuthApi(string payload) => UserPayload = JsonDocument.Parse(payload).RootElement.Clone();

        public string? Token { get; init; } = "test-access";
        public string? Profile { get; init; }
        public Exception? ProfileError { get; init; }
        public Task<JsonElement>? PendingProfile { get; init; }
        public int ProfileCalls;

        public JsonElement UserPayload { get; private set; }
        public string? AccessToken => Token;
        public string? RefreshToken => null;
        public string? ActiveBranchId => null;

        public Task<JsonElement> GetProfileAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ProfileCalls);
            if (ProfileError is not null)
                return Task.FromException<JsonElement>(ProfileError);
            if (PendingProfile is not null)
                return PendingProfile;
            return Task.FromResult(JsonDocument.Parse(Profile!).RootElement.Clone());
        }

        public void ApplyUserFromProfile(JsonElement profile)
        {
            var user = OfflineAuthSessionStore.ResolveUserObject(profile);
            if (user.ValueKind == JsonValueKind.Object)
                UserPayload = user.Clone();
        }

        public void ApplyBranchFromProfile(JsonElement profile) { }

        public Task<bool> CanReachApiAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RefreshAccessAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public void ClearSession() => throw new NotSupportedException();
        public void RestoreOfflineSession(OfflineAuthSession session) => throw new NotSupportedException();
        public Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string? Layout, string? Mode, string? AmountUnit)> GetScaleSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<EmployeeInfoDto>?> GetEmployeesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EmployeeInfoDto?> CreateEmployeeAsync(string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteEmployeeAsync(string employeeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateEmployeeAccessAsync(string employeeId, EmployeeAccessFlags access, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<RoleInfoDto>?> GetRolesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RoleInfoDto?> CreateRoleAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteRoleAsync(string roleId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
