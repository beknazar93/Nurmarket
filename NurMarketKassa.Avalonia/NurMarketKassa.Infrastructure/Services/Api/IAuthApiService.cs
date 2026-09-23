using System.Collections.Generic;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис авторизации: вход/выход кассиров, токены сессии, профиль и компания.
/// </summary>
public interface IAuthApiService
{
    /// <summary>Текущий access-токен сессии (Bearer).</summary>
    string? AccessToken { get; }

    /// <summary>Текущий refresh-токен сессии.</summary>
    string? RefreshToken { get; }

    /// <summary>Полезная нагрузка пользователя из ответа логина/профиля.</summary>
    JsonElement UserPayload { get; }

    /// <summary>Активный филиал (для query branch=…).</summary>
    string? ActiveBranchId { get; }

    /// <summary>Проверка доступности API.</summary>
    Task<bool> CanReachApiAsync(CancellationToken ct = default);

    /// <summary>Вход кассира (POST /api/users/auth/login/).</summary>
    Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Ручное обновление access-токена по refresh-токену.</summary>
    Task<bool> RefreshAccessAsync(CancellationToken ct = default);

    /// <summary>Быстрый вход по refresh-токену из DPAPI (без полного POST login).</summary>
    Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default);

    /// <summary>Выход: очистка токенов и данных сессии.</summary>
    void ClearSession();

    /// <summary>Восстановление сессии из локального кэша (офлайн-вход).</summary>
    void RestoreOfflineSession(OfflineAuthSession session);

    /// <summary>GET /api/users/profile/</summary>
    Task<JsonElement> GetProfileAsync(CancellationToken ct = default);

    /// <summary>GET /api/users/company/</summary>
    Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default);

    /// <summary>GET /api/users/settings/company/ — scale_barcode_layout/scale_barcode_mode.</summary>
    Task<(string? Layout, string? Mode, string? AmountUnit)> GetScaleSettingsAsync(CancellationToken ct = default);

    /// <summary>Список сотрудников компании (с app.nurcrm.kg, раздел "Сотрудники"). Точный URL
    /// не подтверждён — реализация пробует несколько вариантов; при неудаче возвращает null.</summary>
    Task<List<EmployeeInfoDto>?> GetEmployeesAsync(CancellationToken ct = default);

    /// <summary>Создать сотрудника на сервере (форма "Новый сотрудник" на сайте: Email, Имя,
    /// Фамилия, Роль — обязательные, Филиал необязательный, плюс доступы — см.
    /// EmployeeAccessFlags). При ошибке валидации сервера бросает ApiException с текстом от сервера.</summary>
    Task<EmployeeInfoDto?> CreateEmployeeAsync(string email, string firstName, string lastName, string roleId, EmployeeAccessFlags? access = null, CancellationToken ct = default);

    /// <summary>Удалить сотрудника на сервере по id.</summary>
    Task DeleteEmployeeAsync(string employeeId, CancellationToken ct = default);

    /// <summary>Изменить доступы (can_view_*) уже созданного сотрудника. Точный путь не
    /// подтверждён живым запросом — см. doc-comment в NurMarketApiClient.</summary>
    Task UpdateEmployeeAccessAsync(string employeeId, EmployeeAccessFlags access, CancellationToken ct = default);

    /// <summary>Список ролей компании (с app.nurcrm.kg, раздел "Сотрудники → Роли").</summary>
    Task<List<RoleInfoDto>?> GetRolesAsync(CancellationToken ct = default);

    /// <summary>Создать роль на сервере (форма "Новая роль": только "Название роли").</summary>
    Task<RoleInfoDto?> CreateRoleAsync(string name, CancellationToken ct = default);

    /// <summary>Удалить роль на сервере по id.</summary>
    Task DeleteRoleAsync(string roleId, CancellationToken ct = default);

    /// <summary>Применить филиал из профиля (если в JWT его нет).</summary>
    void ApplyBranchFromProfile(JsonElement profile);

    /// <summary>Подставить пользователя из профиля (если login вернул только токены).</summary>
    void ApplyUserFromProfile(JsonElement profile);

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default);
}
