using System.Collections.Generic;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация авторизации поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// Состояние токенов хранится в транспорте, так как используется всеми доменными сервисами.
/// </summary>
public sealed class AuthApiService : IAuthApiService
{
    private readonly NurMarketApiClient _client;

    public AuthApiService(NurMarketApiClient client) => _client = client;

    public string? AccessToken => _client.AccessToken;

    public string? RefreshToken => _client.RefreshToken;

    public JsonElement UserPayload => _client.UserPayload;

    public string? ActiveBranchId => _client.ActiveBranchId;

    public Task<bool> CanReachApiAsync(CancellationToken ct = default) =>
        _client.CanReachApiAsync(ct);

    public Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default) =>
        _client.LoginAsync(email, password, ct);

    public Task<bool> RefreshAccessAsync(CancellationToken ct = default) =>
        _client.RefreshAccessAsync(ct);

    public Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default) =>
        _client.TryRestoreSessionViaRefreshAsync(email, ct);

    public void ClearSession() => _client.ClearSession();

    public void RestoreOfflineSession(OfflineAuthSession session) =>
        _client.RestoreOfflineSession(session);

    public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) =>
        _client.GetProfileAsync(ct);

    public Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default) =>
        _client.GetCompanyAsync(ct);

    public Task<(string? Layout, string? Mode, string? AmountUnit)> GetScaleSettingsAsync(CancellationToken ct = default) =>
        _client.GetScaleSettingsAsync(ct);

    public Task<List<EmployeeInfoDto>?> GetEmployeesAsync(CancellationToken ct = default) =>
        _client.GetEmployeesAsync(ct);

    public Task<EmployeeInfoDto?> CreateEmployeeAsync(string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access = null, CancellationToken ct = default) =>
        _client.CreateEmployeeAsync(email, firstName, lastName, roleId, access, ct);

    public Task DeleteEmployeeAsync(string employeeId, CancellationToken ct = default) =>
        _client.DeleteEmployeeAsync(employeeId, ct);

    public Task UpdateEmployeeAccessAsync(string employeeId, EmployeeAccessFlags access, CancellationToken ct = default) =>
        _client.UpdateEmployeeAccessAsync(employeeId, access, ct);

    public Task<List<RoleInfoDto>?> GetRolesAsync(CancellationToken ct = default) =>
        _client.GetRolesAsync(ct);

    public Task<RoleInfoDto?> CreateRoleAsync(string name, CancellationToken ct = default) =>
        _client.CreateRoleAsync(name, ct);

    public Task DeleteRoleAsync(string roleId, CancellationToken ct = default) =>
        _client.DeleteRoleAsync(roleId, ct);

    public void ApplyBranchFromProfile(JsonElement profile) =>
        _client.ApplyBranchFromProfile(profile);

    public void ApplyUserFromProfile(JsonElement profile) =>
        _client.ApplyUserFromProfile(profile);

    public Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default) =>
        _client.DownloadAuthorizedAsync(absoluteUrl, ct);
}
