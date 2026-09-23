using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Auth;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Центральный конфигуратор <see cref="HttpClient"/> и транспорт/сессия для Nur CRM
/// (BaseUrl, заголовки Bearer, таймауты, refresh-токен).
/// Доменные операции вынесены в <see cref="IAuthApiService"/>, <see cref="ICatalogApiService"/>,
/// <see cref="ISalesApiService"/> и <see cref="IShiftApiService"/>.
/// </summary>
public sealed class NurMarketApiClient : IDisposable
{
    public const string AuthInvalidHintRu =
        "Сессия недействительна (часто из‑за входа с другого ПК или телефона). " +
        "Нажмите «Выйти» в кассе и войдите снова.";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonWrite = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly JsonSerializerOptions _jsonRead = new() { PropertyNameCaseInsensitive = true };
    /// <summary>Исключает гонки при входе и ручном refresh.</summary>
    private readonly SemaphoreSlim _loginMutex = new(1, 1);
    /// <summary>Параллельные GET/POST к API (раньше один глобальный замок сильно замедлял каталог).</summary>
    private readonly SemaphoreSlim _httpSlots = new(Math.Min(12, Math.Max(4, Environment.ProcessorCount * 2)), Math.Min(12, Math.Max(4, Environment.ProcessorCount * 2)));
    /// <summary>Серийный refresh токена при 401 из параллельных запросов.</summary>
    private readonly SemaphoreSlim _refreshSync = new(1, 1);

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public JsonElement UserPayload { get; private set; }
    public string? ActiveBranchId { get; private set; }

    public NurMarketApiClient(AppSettings settings)
    {
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/') + "/";
        var handler = new JwtBearerRefreshHandler(this) { InnerHandler = new HttpClientHandler() };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(55),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        UserPayload = default;
    }

    internal void ApplyBearerAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
    }

    internal async Task<bool> RefreshAccessAndPersistAsync(CancellationToken ct = default)
    {
        if (!await RefreshAccessUnlockedAsync(ct).ConfigureAwait(false))
            return false;

        PersistTokensToSecureStore();
        return true;
    }

    /// <summary>Быстрый вход по refresh-токену из DPAPI (без полного login).</summary>
    public async Task<bool> TryRestoreSessionViaRefreshAsync(string email, CancellationToken ct = default)
    {
        var session = OfflineAuthSessionStore.TryLoad();
        if (session == null
            || !string.Equals(session.Login, email.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return false;
        }

        RestoreOfflineSession(session);
        if (!await RefreshAccessAsync(ct).ConfigureAwait(false))
            return false;

        PersistTokensToSecureStore();
        return !string.IsNullOrWhiteSpace(AccessToken);
    }

    private void PersistTokensToSecureStore() =>
        OfflineAuthSessionStore.UpdateTokens(AccessToken, RefreshToken);

    // Ленивые экземпляры доменных сервисов для делегирования из устаревших методов.
    private CatalogApiService? _catalogApi;
    private SalesApiService? _salesApi;
    private ShiftApiService? _shiftApi;

    internal CatalogApiService Catalog => _catalogApi ??= new CatalogApiService(this);
    internal SalesApiService Sales => _salesApi ??= new SalesApiService(this);
    internal ShiftApiService Shift => _shiftApi ??= new ShiftApiService(this);

    /// <summary>Проверка доступности API (аналог can_reach_api).</summary>
    public async Task<bool> CanReachApiAsync(CancellationToken ct = default)
    {
        foreach (var path in new[] { "", "api/users/auth/login/" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Connectivity probe failed for endpoint: {ex.GetType().Name}", "DEBUG");
            }
        }

        return false;
    }

    /// <summary>GET список товаров агента с остатками (для склада).</summary>
    [Obsolete("Используйте ICatalogApiService.GetAgentProductsAsync.")]
    public Task<List<JsonElement>> GetAgentProductsAsync(CancellationToken ct = default) =>
        Catalog.GetAgentProductsAsync(ct);

    /// <summary>Синхронизация статуса «избранный» с сайтом.</summary>
    [Obsolete("Используйте ICatalogApiService.SetProductFavoriteAsync.")]
    public Task<bool> SetProductFavoriteAsync(string productId, bool isFavorite, CancellationToken ct = default) =>
        Catalog.SetProductFavoriteAsync(productId, isFavorite, ct);

    public async Task<JsonElement> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        await _loginMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = new LoginRequest { Email = email.Trim(), Password = password };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/users/auth/login/", content, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                JsonElement? payload = TryParse(text);
                throw new ApiException(ApiErrorParser.Parse(resp, text), (int)resp.StatusCode, payload);
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            ApplyLoginResponse(root);
            PersistTokensToSecureStore();
            return root;
        }
        finally
        {
            _loginMutex.Release();
        }
    }

    public async Task<bool> RefreshAccessAsync(CancellationToken ct = default)
    {
        await _loginMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(RefreshToken))
                return false;
            var body = new RefreshRequest { Refresh = RefreshToken };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/users/auth/refresh/", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
            {
                AccessToken = acc.GetString();
                if (root.TryGetProperty("refresh", out var refr) && refr.ValueKind == JsonValueKind.String)
                {
                    var newRefresh = refr.GetString();
                    if (!string.IsNullOrWhiteSpace(newRefresh))
                        RefreshToken = newRefresh;
                }

                PersistTokensToSecureStore();
                return true;
            }

            return false;
        }
        finally
        {
            _loginMutex.Release();
        }
    }

    public void ClearSession()
    {
        AccessToken = null;
        RefreshToken = null;
        UserPayload = default;
        ActiveBranchId = null;
        CompanyInfoService.Clear();
    }

    /// <summary>Восстановление сессии из локального кэша (офлайн-вход).</summary>
    public void RestoreOfflineSession(OfflineAuthSession session)
    {
        AccessToken = session.AccessToken;
        RefreshToken = session.RefreshToken;
        ActiveBranchId = session.BranchId;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(session.UserId))
            {
                writer.WriteString("id", session.UserId);
                writer.WriteString("pk", session.UserId);
            }

            writer.WriteString("email", session.Login);
            writer.WriteString("full_name", session.CashierName);
            if (!string.IsNullOrWhiteSpace(session.Role))
                writer.WriteString("role", session.Role);
            if (!string.IsNullOrWhiteSpace(session.BranchId))
                writer.WriteString("primary_branch_id", session.BranchId);
            writer.WriteStartArray("permissions");
            foreach (var permission in session.Permissions)
                writer.WriteStringValue(permission);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        UserPayload = doc.RootElement.Clone();
    }

    /// <summary>GET /api/users/profile/</summary>
    public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "api/users/profile/", null, null, ct);

    /// <summary>GET /api/users/company/</summary>
    public async Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        await _httpSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var uri = BuildUri("api/users/company/", null);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var jsonResponse = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                JsonElement? payload = TryParse(jsonResponse);
                throw new ApiException(ApiErrorParser.Parse(resp, jsonResponse), (int)resp.StatusCode, payload);
            }

            using var doc = JsonDocument.Parse(jsonResponse);
            var company = ParseCompanyDto(doc.RootElement);
            PosLogger.Log(
                $"Company loaded. CompanyId={MaskIdentifier(company?.Id)}, " +
                $"INNConfigured={!string.IsNullOrWhiteSpace(company?.Inn)}, " +
                $"AddressConfigured={!string.IsNullOrWhiteSpace(company?.Address)}",
                "API");
            return company;
        }
        finally
        {
            _httpSlots.Release();
        }
    }

    private static string MaskIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "not-set";
        var visibleLength = Math.Min(4, value.Length);
        return value[..visibleLength] + "****";
    }

    internal static CompanyDto? ParseCompanyDto(JsonElement root)
    {
        var data = root;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            data = inner;
        }

        if (data.ValueKind != JsonValueKind.Object)
            return null;

        return new CompanyDto
        {
            Id = ReadTopLevelString(data, "id"),
            Name = ReadTopLevelString(data, "name"),
            Inn = TrimToMaxLength(ReadTopLevelString(data, "inn"), 32),
            Address = ReadTopLevelString(data, "address"),
        };
    }

    private static string? ReadTopLevelString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? TrimToMaxLength(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    /// <summary>
    /// Универсальный запрос с Bearer и query branch=… (как branch_params() в Python).
    /// </summary>
    /// <param name="requestTimeout">Ограничение времени запроса (например scan 22 с).</param>
    /// 

    /// <summary>
    /// Оптимизированный метод для потоковой десериализации без создания промежуточных строк.
    /// Заменяет RequestAsync в горячих путях (каталог, поиск).
    /// </summary>
    public async Task<T?> RequestDataAsync<T>(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await _httpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
            var uri = BuildUri(relativePath, query);
            using var req = new HttpRequestMessage(method, uri);
            ApplyBearerAuthorization(req);
            if (jsonBody is not null)
            {
                var json = JsonSerializer.Serialize(jsonBody, _jsonWrite);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken))
                ClearSession();

            resp.EnsureSuccessStatusCode();

            // Потоковая десериализация (без лишних аллокаций)
            using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonRead, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _httpSlots.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await _httpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await SendOnceAsync(method, relativePath, jsonBody, query, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _httpSlots.Release();
        }
    }

    private async Task<JsonElement> SendOnceAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct)
    {
        relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
        var uri = BuildUri(relativePath, query);
        using var req = new HttpRequestMessage(method, uri);
        ApplyBearerAuthorization(req);
        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, _jsonWrite);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken))
            ClearSession();

        if (!resp.IsSuccessStatusCode)
        {
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? AuthInvalidHintRu
                : ApiErrorParser.Parse(resp, text);
            JsonElement? payload = TryParse(text);
            throw new ApiException(msg, (int)resp.StatusCode, payload);
        }

        if (string.IsNullOrWhiteSpace(text))
            return default;

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }


    private async Task<bool> RefreshAccessUnlockedAsync(CancellationToken ct)
    {
        await _refreshSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(RefreshToken))
                return false;
            var body = new RefreshRequest { Refresh = RefreshToken };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/users/auth/refresh/", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
            {
                AccessToken = acc.GetString();
                if (root.TryGetProperty("refresh", out var refr) && refr.ValueKind == JsonValueKind.String)
                {
                    var newRefresh = refr.GetString();
                    if (!string.IsNullOrWhiteSpace(newRefresh))
                        RefreshToken = newRefresh;
                }

                PersistTokensToSecureStore();
                return true;
            }

            return false;
        }
        finally
        {
            _refreshSync.Release();
        }
    }

    private Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string>? query)
    {
        var path = relativePath.TrimStart('/');
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(ActiveBranchId))
            qs.Add("branch=" + Uri.EscapeDataString(ActiveBranchId));
        if (query is not null)
        {
            foreach (var kv in query)
                qs.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""));
        }

        var rel = qs.Count == 0 ? path : path + "?" + string.Join("&", qs);
        return new Uri(_http.BaseAddress!, rel);
    }

    private void ApplyLoginResponse(JsonElement root)
    {
        if (root.TryGetProperty("access", out var a) && a.ValueKind == JsonValueKind.String)
            AccessToken = a.GetString();
        if (root.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.String)
            RefreshToken = r.GetString();

        // Вложенный user/profile чаще, чем поля на корне рядом с access/refresh.
        foreach (var key in new[] { "user", "profile", "data", "cashier" })
        {
            if (!root.TryGetProperty(key, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            if (OfflineAuthSessionStore.TryExtractUserId(nested) == null)
                continue;

            UserPayload = nested.Clone();
            SyncBranchFromUser();
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("access") || prop.NameEquals("refresh"))
                    continue;
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        if (bytes.Length > 2)
        {
            using var doc = JsonDocument.Parse(bytes);
            UserPayload = doc.RootElement.Clone();
        }
        else
            UserPayload = default;

        SyncBranchFromUser();
    }

    /// <summary>
    /// После GET /api/users/profile/ — подставить пользователя, если login вернул только токены.
    /// </summary>
    public void ApplyUserFromProfile(JsonElement profile)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(profile);
        if (user.ValueKind != JsonValueKind.Object)
            return;

        UserPayload = user.Clone();
        SyncBranchFromUser();
    }

    /// <summary>
    /// После GET /api/users/profile/ — в JWT часто нет филиала; запросы с ?branch= должны использовать id из профиля.
    /// </summary>
    public void ApplyBranchFromProfile(JsonElement profile)
    {
        var bid = TryExtractBranchId(profile);
        if (!string.IsNullOrEmpty(bid))
            ActiveBranchId = bid;
    }

    private void SyncBranchFromUser() => ActiveBranchId = TryExtractBranchId(UserPayload);

    private static string? TryExtractBranchId(JsonElement user)
    {
        var source = OfflineAuthSessionStore.ResolveUserObject(user);
        if (source.ValueKind != JsonValueKind.Object)
            return null;

        var primary = ReadJsonScalar(source, "primary_branch_id", "branch_id", "active_branch_id");
        if (!string.IsNullOrEmpty(primary))
            return primary;

        if (source.TryGetProperty("branch_ids", out var bids) && bids.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in bids.EnumerateArray())
            {
                var s = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString()?.Trim(),
                    JsonValueKind.Number => el.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    private static string? ReadJsonScalar(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v))
                continue;
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString()?.Trim(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GET /api/construction/cashboxes/ — при 404 возвращает [], как в Python.</summary>
    [Obsolete("Используйте IShiftApiService.ConstructionCashboxesListAsync.")]
    public Task<JsonElement> ConstructionCashboxesListAsync(CancellationToken ct = default) =>
        Shift.ConstructionCashboxesListAsync(ct);

    /// <summary>GET /api/construction/shifts/</summary>
    [Obsolete("Используйте IShiftApiService.ConstructionShiftsListAsync.")]
    public Task<JsonElement> ConstructionShiftsListAsync(CancellationToken ct = default) =>
        Shift.ConstructionShiftsListAsync(ct);

    /// <summary>POST открытия смены — два URL и два варианта тела (как construction_shift_open).</summary>
    [Obsolete("Используйте IShiftApiService.ConstructionShiftOpenAsync.")]
    public Task<JsonElement> ConstructionShiftOpenAsync(
        string cashboxId,
        string openingCash = "0.00",
        CancellationToken ct = default) =>
        Shift.ConstructionShiftOpenAsync(cashboxId, openingCash, ct);

    /// <summary>POST закрытия смены — два URL (как construction_shift_close).</summary>
    [Obsolete("Используйте IShiftApiService.ConstructionShiftCloseAsync.")]
    public Task<JsonElement> ConstructionShiftCloseAsync(
        string shiftId,
        string? closingCash = null,
        CancellationToken ct = default) =>
        Shift.ConstructionShiftCloseAsync(shiftId, closingCash, ct);

    /// <summary>POST /api/main/pos/sales/start/</summary>
    [Obsolete("Используйте ISalesApiService.PosSalesStartAsync.")]
    public Task<JsonElement> PosSalesStartAsync(string? cashboxId = null, CancellationToken ct = default) =>
        Sales.PosSalesStartAsync(cashboxId, ct);

    /// <summary>POST /api/main/pos/sales/start/ с произвольным телом (возврат, касса и т.д.).</summary>
    [Obsolete("Используйте ISalesApiService.PosSalesStartAsync.")]
    public Task<JsonElement> PosSalesStartAsync(IReadOnlyDictionary<string, string>? body, CancellationToken ct = default) =>
        Sales.PosSalesStartAsync(body, ct);

    /// <summary>GET /api/main/pos/carts/{id}/</summary>
    [Obsolete("Используйте ISalesApiService.PosCartGetAsync.")]
    public Task<JsonElement> PosCartGetAsync(string cartId, CancellationToken ct = default) =>
        Sales.PosCartGetAsync(cartId, ct);

    /// <summary>POST /api/main/pos/sales/{id}/scan/ — таймаут как в Python (3+22 с).</summary>
    [Obsolete("Используйте ISalesApiService.PosScanAsync.")]
    public Task<JsonElement> PosScanAsync(string cartId, string barcode, string? quantity = null, CancellationToken ct = default) =>
        Sales.PosScanAsync(cartId, barcode, quantity, ct);

    /// <summary>PATCH /api/main/pos/carts/{cart}/items/{item}/</summary>
    [Obsolete("Используйте ISalesApiService.PosCartItemPatchAsync.")]
    public Task<JsonElement> PosCartItemPatchAsync(
        string cartId,
        string itemId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default) =>
        Sales.PosCartItemPatchAsync(cartId, itemId, body, ct);

    /// <summary>DELETE /api/main/pos/carts/{cart}/items/{item}/</summary>
    [Obsolete("Используйте ISalesApiService.PosCartItemDeleteAsync.")]
    public Task<JsonElement> PosCartItemDeleteAsync(string cartId, string itemId, CancellationToken ct = default) =>
        Sales.PosCartItemDeleteAsync(cartId, itemId, ct);

    /// <summary>
    /// POST checkout — два URL, таймаут до 90 с; при 400 без cash_received для безнала — повтор с 0.00 (как pos_checkout).
    /// </summary>
    [Obsolete("Используйте ISalesApiService.PosCheckoutAsync.")]
    public Task<JsonElement> PosCheckoutAsync(
        string cartId,
        Dictionary<string, string> body,
        CancellationToken ct = default) =>
        Sales.PosCheckoutAsync(cartId, body, ct);

    /// <summary>GET /api/main/pos/sales/{id}/receipt/ — текст чека для печати.</summary>
    [Obsolete("Используйте ISalesApiService.PosSaleReceiptAsync.")]
    public Task<JsonElement> PosSaleReceiptAsync(string saleId, CancellationToken ct = default) =>
        Sales.PosSaleReceiptAsync(saleId, ct);

    /// <summary>PATCH /api/main/pos/carts/{id}/ — скидка на чек и др.</summary>
    [Obsolete("Используйте ISalesApiService.PosCartPatchAsync.")]
    public Task<JsonElement> PosCartPatchAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default) =>
        Sales.PosCartPatchAsync(cartId, body, ct);

    /// <summary>POST /api/main/pos/sales/{id}/add-item/ — как pos_add_item (таймаут до 28 с).</summary>
    [Obsolete("Используйте ISalesApiService.PosAddItemAsync.")]
    public Task<JsonElement> PosAddItemAsync(
        string cartId,
        string productId,
        string? quantity = null,
        string? unitPrice = null,
        string? discountTotal = null,
        CancellationToken ct = default) =>
        Sales.PosAddItemAsync(cartId, productId, quantity, unitPrice, discountTotal, ct);

    /// <summary>POST add-item с произвольными полями (возврат, ссылка на строку исходного чека).</summary>
    [Obsolete("Используйте ISalesApiService.PosAddItemRawAsync.")]
    public Task<JsonElement> PosAddItemRawAsync(
        string cartId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default) =>
        Sales.PosAddItemRawAsync(cartId, body, ct);

    /// <summary>Поиск товаров (быстрый, через потоковый парсинг).</summary>
    [Obsolete("Используйте ICatalogApiService.ProductsSearchAsync.")]
    public Task<List<ProductDto>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default) =>
        Catalog.ProductsSearchAsync(query, limit, ct);

    /// <summary>Лёгкая проверка версии каталога без полной загрузки SKU.</summary>
    [Obsolete("Используйте ICatalogApiService.ProductsCatalogVersionAsync.")]
    public Task<CatalogVersionInfo?> ProductsCatalogVersionAsync(CancellationToken ct = default) =>
        Catalog.ProductsCatalogVersionAsync(ct);

    /// <summary>Полный каталог с пагинацией (все SKU, до limit).</summary>
    [Obsolete("Используйте ICatalogApiService.ProductsCatalogAsync.")]
    public Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default) =>
        Catalog.ProductsCatalogAsync(limit, maxPages, ct);

    /// <summary>Карточка товара с картинками (как products_detail).</summary>
    [Obsolete("Используйте ICatalogApiService.ProductsDetailAsync.")]
    public Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default) =>
        Catalog.ProductsDetailAsync(productId, ct);

    /// <summary>Список продаж (для выбора чека возврата). Пробует типовые GET с пагинацией.</summary>
    [Obsolete("Используйте ISalesApiService.PosSalesListAsync.")]
    public Task<List<JsonElement>> PosSalesListAsync(
        int page,
        int pageSize,
        string? cashboxId = null,
        CancellationToken ct = default) =>
        Sales.PosSalesListAsync(page, pageSize, cashboxId, ct);

    /// <summary>GET карточки продажи со строками (типовые пути).</summary>
    [Obsolete("Используйте ISalesApiService.PosSaleGetAsync.")]
    public Task<JsonElement> PosSaleGetAsync(string saleId, CancellationToken ct = default) =>
        Sales.PosSaleGetAsync(saleId, ct);

    /// <summary>GET /api/main/pos/cart-item-deletions/get/</summary>
    [Obsolete("Используйте ISalesApiService.PosCartItemDeletionsGetAsync.")]
    public Task<JsonElement> PosCartItemDeletionsGetAsync(CancellationToken ct = default) =>
        Sales.PosCartItemDeletionsGetAsync(ct);

    /// <summary>Регистрация возврата через cart-item-deletions/get (для оплаченных чеков).</summary>
    [Obsolete("Используйте ISalesApiService.TryPosCartItemDeletionReturnAsync.")]
    public Task<bool> TryPosCartItemDeletionReturnAsync(
        string saleId,
        string? cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default) =>
        Sales.TryPosCartItemDeletionReturnAsync(saleId, cartId, line, reason, ct);

    /// <summary>PATCH /api/main/pos/sales/{id}/</summary>
    [Obsolete("Используйте ISalesApiService.PosSalePatchAsync.")]
    public Task<JsonElement> PosSalePatchAsync(
        string saleId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default) =>
        Sales.PosSalePatchAsync(saleId, body, ct);

    /// <summary>DELETE /api/main/pos/sales/{id}/</summary>
    [Obsolete("Используйте ISalesApiService.PosSaleDeleteAsync.")]
    public Task<JsonElement> PosSaleDeleteAsync(string saleId, CancellationToken ct = default) =>
        Sales.PosSaleDeleteAsync(saleId, ct);

    /// <summary>
    /// Возврат позиции по API Nur CRM: регистрация удаления (если нужно) → PATCH (частично) → DELETE строки корзины.
    /// </summary>
    [Obsolete("Используйте ISalesApiService.PosReturnCartLineAsync.")]
    public Task<JsonElement> PosReturnCartLineAsync(
        string cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default) =>
        Sales.PosReturnCartLineAsync(cartId, line, reason, ct);

    /// <summary>Полный возврат чека: PATCH с причиной, затем DELETE продажи.</summary>
    [Obsolete("Используйте ISalesApiService.PosReturnWholeSaleAsync.")]
    public Task<JsonElement> PosReturnWholeSaleAsync(string saleId, string? reason, CancellationToken ct = default) =>
        Sales.PosReturnWholeSaleAsync(saleId, reason, ct);

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    public async Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return null;
        if (string.IsNullOrEmpty(AccessToken))
            return null;

        await _httpSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _httpSlots.Release();
        }
    }

    /// <summary>Разворачивает ответ (массив или {results:[…]}) в список элементов. Используется доменными сервисами.</summary>
    internal static List<JsonElement> UnwrapList(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                list.Add(el.Clone());
            return list;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                list.Add(el.Clone());
        }

        return list;
    }

    public Task<JsonElement> GetAsync(string relativePath, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, relativePath, null, query, ct);

    public void Dispose()
    {
        _http.Dispose();
        _loginMutex.Dispose();
        _httpSlots.Dispose();
        _refreshSync.Dispose();
    }
}
