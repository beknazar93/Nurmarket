using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-13 (сервер «ev1311+apl1311»: ETag у users/profile/ и users/company/addons/): условное чтение профиля — If-None-Match, ответ 304 = «ничего не изменилось».
/// Отдельный необязательный интерфейс (а не член <see cref="IAuthApiService"/>), чтобы заглушки API в тестах и старые реализации не менялись:
/// кто его не реализует, читает профиль как раньше (<see cref="IAuthApiService.GetProfileAsync"/>).</summary>
public interface IConditionalProfileApi
{
    /// <summary>GET api/users/profile/ с If-None-Match (<paramref name="ifNoneMatch"/> — ETag прошлого ответа, null — безусловно).
    /// StatusCode 304 — тела нет; 200 — тело профиля и новый ETag (null, если сервер ETag не отдаёт — старый сервер).</summary>
    Task<(JsonElement Body, int StatusCode, string? ETag)> GetProfileConditionalAsync(string? ifNoneMatch, CancellationToken ct = default);
}
