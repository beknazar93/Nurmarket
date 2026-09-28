using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services.Api;

/// <summary>Должник из сводки сервера (GET /api/main/clients/debtors/).</summary>
public sealed record DebtorInfo(
    string ClientId,
    string Name,
    string? Phone,
    string? TelegramChatId,
    double DebtTotal,
    int SalesCount,
    string? OldestDebtAt);

/// <summary>Итог погашения долга одной суммой (POST /api/main/clients/{id}/pay-debt/).</summary>
public sealed record PayDebtResult(double Paid, double Left, int AppliedCount, bool Replayed);

/// <summary>
/// 2026-09-28: новые адреса NurCRM по ТЗ для бэкенда (docs/BACKEND_API.md) — клиенты, долги,
/// остатки и вид магазина. Отдельный файл, а не правка NurMarketApiClient/SalesApiService: те
/// файлы одновременно дорабатывают другие задачи, а здесь всё новое и ничего старого не меняется.
///
/// • BE-03 должники одним запросом — раньше касса складывала их сама из pos/sales/?status=debt;
/// • BE-12 погашение долга одной суммой — раньше по запросу на каждый взнос (~15 с на старых
///   долгах из 30 взносов по 1,30 сом);
/// • BE-04 telegram_chat_id у клиента — связка «чат ↔ клиент» жила только в одной кассе;
/// • BE-05 товары с малым остатком фильтром сервера;
/// • BE-18 вид магазина компании (market_sphere).
///
/// Все методы при отказе сервера бросают <see cref="ApiException"/> с кодом ответа — вызывающий
/// код сам решает, уходить ли на старый путь (<see cref="IsEndpointMissing"/>).
/// </summary>
public sealed class ClientDebtsApiService : IDisposable
{
    private readonly NurMarketApiClient _api;
    private readonly AppSettings _settings;
    private readonly object _httpSync = new();
    private HttpClient? _http;

    public ClientDebtsApiService(NurMarketApiClient api, AppSettings settings)
    {
        _api = api;
        _settings = settings;
    }

    /// <summary>Сервер ещё не знает этот адрес (старая версия NurCRM) — тогда и только тогда
    /// вызывающий уходит на старый путь. На 400/403/500 старый путь НЕ включается: для оплаты это
    /// риск провести деньги дважды, а ошибку проверки сервер и так объяснит текстом.</summary>
    public static bool IsEndpointMissing(Exception ex) =>
        ex is ApiException { StatusCode: 404 or 405 or 501 };

    // ── BE-03: должники ──────────────────────────────────────────────────────────────

    /// <summary>GET /api/main/clients/debtors/?min_amount=…&amp;ordering=-debt_total — сводка по
    /// клиентам с непогашенным долгом, от большего к меньшему. Сервер отдаёт массив целиком;
    /// на случай постраничного ответа ({results, next}) листаем страницы.</summary>
    public async Task<List<DebtorInfo>> GetDebtorsAsync(double minAmount = 0, CancellationToken ct = default)
    {
        var result = new List<DebtorInfo>();
        for (var page = 1; page <= 50; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["min_amount"] = minAmount.ToString("0.##", CultureInfo.InvariantCulture),
                ["ordering"] = "-debt_total",
            };
            if (page > 1)
                query["page"] = page.ToString(CultureInfo.InvariantCulture);

            var data = await _api
                .RequestAsync(HttpMethod.Get, "api/main/clients/debtors/", null, query, ct)
                .ConfigureAwait(false);

            foreach (var row in NurMarketApiClient.UnwrapList(data))
            {
                var clientId = ReadString(row, "client") ?? ReadString(row, "id");
                if (string.IsNullOrWhiteSpace(clientId))
                    continue;
                result.Add(new DebtorInfo(
                    clientId!,
                    ReadString(row, "full_name") ?? ReadString(row, "client_name") ?? "",
                    ReadString(row, "phone"),
                    ReadString(row, "telegram_chat_id"),
                    ReadDouble(row, "debt_total") ?? 0,
                    (int)(ReadDouble(row, "sales_count") ?? 0),
                    ReadString(row, "oldest_debt_at")));
            }

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }

        return result;
    }

    // ── BE-12: погашение долга одной суммой ──────────────────────────────────────────

    /// <summary>POST /api/main/clients/{id}/pay-debt/ с заголовком Idempotency-Key. Сервер гасит от
    /// старых долгов к новым (по сроку взноса). Повтор с тем же ключом возвращает прежний итог
    /// ("replayed": true) и второй раз деньги не проводит — поэтому при обрыве связи кассе можно
    /// смело повторить запрос с ТЕМ ЖЕ ключом (проверено вживую 28.09 на тестовом долге).</summary>
    /// <param name="method">Способ оплаты: "cash" (наличные в ящик) — как у старого пути.</param>
    /// <param name="shiftId">Открытая смена кассы: погашение попадает в её итоги на сервере.</param>
    public async Task<PayDebtResult> PayDebtAsync(
        string clientId,
        double amount,
        string method,
        string? shiftId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["method"] = string.IsNullOrWhiteSpace(method) ? "cash" : method.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(shiftId)
            && !shiftId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase))
            body["shift"] = shiftId.Trim();

        var id = Uri.EscapeDataString(clientId.Trim());
        var data = await SendWithIdempotencyKeyAsync(
                HttpMethod.Post, $"api/main/clients/{id}/pay-debt/", body, idempotencyKey, ct)
            .ConfigureAwait(false);

        var applied = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("applied", out var appliedEl)
            && appliedEl.ValueKind == JsonValueKind.Array
                ? appliedEl.GetArrayLength()
                : 0;
        var replayed = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("replayed", out var replayedEl)
            && replayedEl.ValueKind == JsonValueKind.True;

        return new PayDebtResult(
            ReadDouble(data, "paid") ?? amount,
            ReadDouble(data, "left") ?? 0,
            applied,
            replayed);
    }

    // ── BE-04: telegram_chat_id у клиента ────────────────────────────────────────────

    /// <summary>PATCH /api/main/clients/{id}/ {"telegram_chat_id": …}; null — отвязать.</summary>
    public Task<JsonElement> SetClientTelegramChatIdAsync(string clientId, string? chatId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(clientId.Trim());
        var body = new Dictionary<string, string?>
        {
            ["telegram_chat_id"] = string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim(),
        };
        return _api.RequestAsync(HttpMethod.Patch, $"api/main/clients/{id}/", body, null, ct);
    }

    /// <summary>telegram_chat_id из карточки клиента (GET /api/main/clients/{id}/), null — не привязан.</summary>
    public async Task<string?> GetClientTelegramChatIdAsync(string clientId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(clientId.Trim());
        var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/clients/{id}/", null, null, ct).ConfigureAwait(false);
        return ReadString(data, "telegram_chat_id");
    }

    // ── BE-05: малый остаток ─────────────────────────────────────────────────────────

    /// <summary>GET /api/main/products/list/?quantity_lte=N&amp;is_service=false — товары (без услуг)
    /// с остатком не больше N. Возвращает строки как есть и общее число из "count" (если сервер его
    /// прислал). Сортировку по остатку сервер не делает (ordering=quantity проверен 28.09 — без
    /// эффекта), поэтому её делает вызывающий.</summary>
    public async Task<(List<JsonElement> Items, int? Count)> GetLowStockProductsAsync(
        double threshold,
        int maxPages = 10,
        CancellationToken ct = default)
    {
        var result = new List<JsonElement>();
        int? count = null;
        for (var page = 1; page <= maxPages; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["quantity_lte"] = threshold.ToString("0.###", CultureInfo.InvariantCulture),
                ["is_service"] = "false",
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };
            var data = await _api
                .RequestAsync(HttpMethod.Get, "api/main/products/list/", null, query, ct)
                .ConfigureAwait(false);

            if (count is null && ReadDouble(data, "count") is { } total)
                count = (int)total;

            var items = NurMarketApiClient.UnwrapList(data);
            if (items.Count == 0)
                break;
            result.AddRange(items);

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }

        return (result, count);
    }

    // ── BE-18: вид магазина ──────────────────────────────────────────────────────────

    /// <summary>PATCH /api/users/company/ {"market_sphere": …} — только владелец компании; у
    /// остальных сервер отвечает 403 (ApiException со StatusCode 403).</summary>
    public Task<JsonElement> SetCompanyMarketSphereAsync(string sphere, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string> { ["market_sphere"] = MarketSpheres.Normalize(sphere) };
        return _api.RequestAsync(HttpMethod.Patch, "api/users/company/", body, null, ct);
    }

    // ── Транспорт с заголовком Idempotency-Key ───────────────────────────────────────

    /// <summary>NurMarketApiClient.RequestAsync не умеет добавлять свои заголовки, а менять его
    /// ради одного адреса не хочется (файл общий для всех задач). Поэтому здесь свой HttpClient
    /// поверх того же JwtBearerRefreshHandler: тот же Bearer-токен, то же обновление при 401 и
    /// тот же branch=…, что и у остальных запросов кассы.</summary>
    private HttpClient Http
    {
        get
        {
            lock (_httpSync)
            {
                if (_http != null)
                    return _http;
                var handler = new JwtBearerRefreshHandler(_api) { InnerHandler = new HttpClientHandler() };
                _http = new HttpClient(handler)
                {
                    BaseAddress = new Uri(_settings.ApiBaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute),
                    Timeout = TimeSpan.FromSeconds(55),
                };
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return _http;
            }
        }
    }

    private async Task<JsonElement> SendWithIdempotencyKeyAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        string idempotencyKey,
        CancellationToken ct,
        bool retryThrottle = true)
    {
        if (string.IsNullOrEmpty(_api.AccessToken))
            throw new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401);

        relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
        using var req = new HttpRequestMessage(method, BuildUri(relativePath));
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (jsonBody is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if ((int)resp.StatusCode == 429)
        {
            // Отказ по частоте — сервер запрос не обрабатывал; повтор с тем же ключом безопасен.
            var wait = ApiThrottle.ReportThrottled(resp, text);
            if (retryThrottle && wait <= TimeSpan.FromSeconds(15))
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                return await SendWithIdempotencyKeyAsync(method, relativePath, jsonBody, idempotencyKey, ct, retryThrottle: false)
                    .ConfigureAwait(false);
            }
        }

        if (!resp.IsSuccessStatusCode)
        {
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? NurMarketApiClient.AuthInvalidHintRu
                : ApiErrorParser.Parse(resp, text);
            throw new ApiException(msg, (int)resp.StatusCode, TryParse(text));
        }

        if (string.IsNullOrWhiteSpace(text))
            return default;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private Uri BuildUri(string relativePath)
    {
        var path = relativePath.TrimStart('/');
        var branch = _api.ActiveBranchId;
        var rel = string.IsNullOrEmpty(branch) ? path : path + "?branch=" + Uri.EscapeDataString(branch);
        return new Uri(Http.BaseAddress!, rel);
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    internal static double? ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public void Dispose()
    {
        lock (_httpSync)
        {
            _http?.Dispose();
            _http = null;
        }
    }
}
