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

// 2026-11-06: В15 — часть класса NurMarketApiClient (разбор огромного файла на partial-файлы), логика не менялась; остальное — NurMarketApiClient.cs
public sealed partial class NurMarketApiClient
{
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

        // 2026-10-04: + отмена при объявлении аварии (см. _outageCancel).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        var slot = await AcquireSlotAsync(linked.Token).ConfigureAwait(false);
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
                ExpireSession(relativePath); // 2026-10-30

            if (!resp.IsSuccessStatusCode)
            {
                // 2026-11-06: В2 Н1-02 — раньше здесь был EnsureSuccessStatusCode: сырой HttpRequestException без тела ответа. Возврат по строкам терял цифры
                // refund_exceeds_paid, а IsOutcomeUnknown принимал отказ по существу за обрыв связи (ключ оставался). Теперь, как в SendOnceAsync, — ApiException
                // с кодом и Payload сервера.
                var errText = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                var errMsg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? AuthInvalidHintRu : ApiErrorParser.Parse(resp, errText);
                throw new ApiException(errMsg, (int)resp.StatusCode, TryParse(errText));
            }

            // Потоковая десериализация (без лишних аллокаций)
            using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<T>(stream, _jsonRead, linked.Token).ConfigureAwait(false);
            // 2026-09-29: успех для ServerOutageMonitor — только разобранный ответ. Ошибку разбора
            // здесь сбоем сервера не считаем: это может быть и несовпадение полей DTO, а не авария.
            ServerOutageMonitor.ReportSuccess(relativePath);
            return result;
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>POST multipart/form-data (загрузка файла, напр. фото товара).</summary>
    public async Task<JsonElement> UploadFileAsync(
        string relativePath,
        string fieldName,
        string filePath,
        IReadOnlyDictionary<string, string>? formFields,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        // 2026-11-03 (С-02, Сис-5): + отмена при объявлении аварии — только пока ждём место в очереди.
        // 2026-11-03 (ревью Н-6): уже отправленную загрузку (запись без ключа повтора) аварией не отменяем: сервер мог файл сохранить, а повтор добавил бы дубль в галерею.
        using var linkedUpload = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        var slot = await AcquireSlotAsync(linkedUpload.Token).ConfigureAwait(false);
        try
        {
            relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, HttpMethod.Post);
            var uri = BuildUri(relativePath, null);
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            ApplyBearerAuthorization(req);

            using var form = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(filePath);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(filePath));
            form.Add(fileContent, fieldName, Path.GetFileName(filePath));
            if (formFields is not null)
            {
                foreach (var kv in formFields)
                    form.Add(new StringContent(kv.Value ?? ""), kv.Key);
            }

            req.Content = form;
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken))
                ExpireSession(relativePath); // 2026-10-30

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
        finally
        {
            slot.Release();
        }
    }

    private static string GuessContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };

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

        // 2026-10-04: + отмена при объявлении аварии (см. _outageCancel).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        // 2026-11-05 (аудит №4, Л4-07/Л4-08): денежный POST без собственного ключа получает Idempotency-Key по подписи запроса (MoneyRequestKeys):
        // повтор того же запроса после неизвестного исхода уходит с тем же ключом, а пока такой же ещё выполняется — второй не отправляется.
        string? moneySig = null;
        string? moneyKey = null;
        if (MoneyRequestKeys.Applies(method, relativePath))
        {
            var json = jsonBody is null ? null : JsonSerializer.Serialize(jsonBody, _jsonWrite);
            moneySig = MoneyRequestKeys.Signature(method, relativePath, json);
            if (moneySig != null && !MoneyRequestKeys.TryBegin(moneySig, out moneyKey))
            {
                PosLogger.Log($"Денежный запрос {relativePath} уже выполняется — повтор не отправлен.", "WARNING");
                throw MoneyRequestKeys.StillRunning();
            }
        }

        try
        {
            var slot = await AcquireSlotAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var result = await SendOnceAsync(method, relativePath, jsonBody, query, linked.Token, idempotencyKey: moneyKey)
                    .ConfigureAwait(false);
                if (moneySig != null)
                    MoneyRequestKeys.OnSuccess(moneySig);
                return result;
            }
            finally
            {
                slot.Release();
            }
        }
        catch (Exception ex) when (moneySig != null && moneyKey != null)
        {
            MoneyRequestKeys.OnError(moneySig, ex);
            throw;
        }
    }

    private async Task<JsonElement> SendOnceAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct,
        bool retryThrottle = true,
        string? idempotencyKey = null)
    {
        relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
        var uri = BuildUri(relativePath, query);
        using var req = new HttpRequestMessage(method, uri);
        ApplyBearerAuthorization(req);
        if (idempotencyKey != null)
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey); // 2026-11-05 (Л4-08)
        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, _jsonWrite);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 2026-10-31 (ТЗ Умара): 401 на необязательном «company/addons/» не должен выкидывать кассира из сессии — касса работает по ключам и кэшу.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken)
            && !relativePath.Contains("company/addons", StringComparison.OrdinalIgnoreCase))
            ExpireSession(relativePath); // 2026-10-30

        // 2026-10-30 (приёмка 1.17.65, «Важно» 2): 429 — сервер отклонил запрос ДО обработки (ограничение частоты), повтор безопасен, в том числе POST.
        // Один повтор: общий щит (JwtBearerRefreshHandler) сам выдержит паузу Retry-After; не помогло — ApiException 429 («Сервер занят»), а не обрыв связи.
        // 2026-11-05 (сервер ceba67c): 429 pin_locked на users/auth/pin/verify — блокировка PIN на 15 минут, а не «слишком частые запросы»: повторять бессмысленно.
        if ((int)resp.StatusCode == 429 && retryThrottle && ApiThrottle.RemainingBlock <= TimeSpan.FromSeconds(15) && !ct.IsCancellationRequested
            && !relativePath.Contains("pin/verify", StringComparison.OrdinalIgnoreCase))
            return await SendOnceAsync(method, relativePath, jsonBody, query, ct, retryThrottle: false, idempotencyKey: idempotencyKey).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? AuthInvalidHintRu
                : ApiErrorParser.Parse(resp, text);
            JsonElement? payload = TryParse(text);
            throw new ApiException(msg, (int)resp.StatusCode, payload);
        }

        // 2026-09-09: любой успешный ответ сервера — доказательство, что связь есть, независимо
        // от того, какой именно запрос это был (каталог/продажи/сотрудники/...). Основа для
        // 60-часового потолка офлайн-работы — см. OnlineContactTracker.
        OnlineContactTracker.RecordSuccess();

        return ParseSuccessBody(text, relativePath);
    }

    /// <summary>2026-09-29: тело успешного ответа. Сервер за Cloudflare в аварии отвечает и
    /// кодом 200 со страницей HTML или обрывком JSON — раньше это вылетало из запроса голым
    /// JsonException, и оплата показывала кассиру «'&lt;' is an invalid start of a value».
    /// Такой ответ — сбой сервера: ApiException 502, с которым оплата и досылка уже умеют
    /// обращаться безопасно (повтор тем же ключом идемпотентности / очередь), а
    /// ServerOutageMonitor засчитывает его как сбой, а не как «сервер ответил».</summary>
    internal static JsonElement ParseSuccessBody(string? text, string context)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ServerOutageMonitor.ReportSuccess(context);
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            ServerOutageMonitor.ReportSuccess(context);
            return root;
        }
        catch (JsonException ex)
        {
            ServerOutageMonitor.ReportFailure(context, ex);
            throw new ApiException(
                Tr.T("Сервер NurCRM прислал повреждённый ответ (не JSON).", "NurCRM сервери бузулган жооп жөнөттү (JSON эмес).",
                    "The NurCRM server sent a corrupted response (not JSON).", "NurCRM sunucusu bozuk bir yanıt gönderdi (JSON değil).",
                    "NurCRM serveri buzilgan javob yubordi (JSON emas)."),
                502);
        }
    }

    /// <summary>2026-09-29: проверка «сервер снова отвечает» для ServerOutageMonitor — лёгкий
    /// запрос API с авторизацией (профиль: Django + база), а не корень сайта: корень может
    /// отдаваться, когда сам API лежит. Ответ 4xx (кроме 408/429) — сервер жив.</summary>
    public async Task<bool> ProbeServerHealthAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return await CanReachApiAsync(ct).ConfigureAwait(false);

        try
        {
            // 2026-10-04, стенд «сбои сервера»: проверка идёт мимо общей очереди запросов (_httpSlots).
            // При «чёрной дыре» фоновые загрузки занимают все места очереди до таймаута HttpClient
            // (55 с), и проверка ждала свободного места дольше своих 8 с: сервер давно ожил, а касса
            // оставалась «Автономно» ещё до минуты. Проверка — один лёгкий запрос раз в 10–60 с.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await SendOnceAsync(HttpMethod.Get, "api/users/profile/", null, null, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (ApiException ex) when (!ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode) || ex.StatusCode == 429) // 2026-10-30: 429 — сервер жив, просит паузу
        {
            return true;
        }
    }


    private async Task<bool> RefreshAccessUnlockedAsync(CancellationToken ct)
    {
        // 2026-11-06: токены на момент вызова — пока ждём замок, обновление мог сделать другой поток (ротация refresh: второй POST по старому refresh дал бы 401).
        var accessBefore = AccessToken;
        var refreshBefore = RefreshToken;
        // 2026-11-03 (С-04, Сис-4): замок и запрос обновления токена — не дольше RefreshBudget (10 с), раньше висели до 55 с и держали места очереди.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RefreshBudget);
        try
        {
            await _refreshSync.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw RefreshSlowException();
        }

        try
        {
            if (string.IsNullOrEmpty(RefreshToken))
                return false;
            // 2026-11-06: пока ждали замок, токены уже обновили (или вошли заново) — второй запрос не шлём, результат тот же.
            if (!string.IsNullOrEmpty(AccessToken)
                && (!string.Equals(AccessToken, accessBefore, StringComparison.Ordinal) || !string.Equals(RefreshToken, refreshBefore, StringComparison.Ordinal)))
                return true;
            var body = new RefreshRequest { Refresh = RefreshToken };
            using var content = new StringContent(JsonSerializer.Serialize(body, _jsonWrite), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.PostAsync("api/users/auth/refresh/", content, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw RefreshSlowException();
            }

            using var respGuard = resp;
            ct = budget.Token;
            if (!resp.IsSuccessStatusCode)
            {
                // 2026-10-04, клиент: «если сервер временно не отвечает — выкидывает из аккаунта». Обновление
                // токена во время аварии NurCRM (5xx, 408, 429) возвращало false, и вызывающий стирал сессию
                // (ClearSession/Session.Clear) — кассир оказывался разлогинен. Сбой сервера — исключение, как
                // в RefreshAccessAsync (исправлено 2026-09-29 только там); false — только отказ по существу.
                if (ServerOutageMonitor.IsServerFailureStatus((int)resp.StatusCode))
                    throw new ApiException($"HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
                await ThrowIfForeignForbiddenAsync(resp, ct).ConfigureAwait(false); // 2026-11-03 (Д-1)
                return false;
            }
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // 2026-10-04: 200 со страницей HTML вместо JSON — сбой сервера (ApiException 502), как в
            // RefreshAccessAsync. Раньше здесь был голый JsonException: обновление токена посреди досылки
            // очереди превращало его в «отказ по существу» (SyncService.SyncBatchAsync, catch JsonException),
            // и оплаченный чек навсегда уходил в «Некорректные чеки».
            var root = ParseSuccessBody(text, "api/users/auth/refresh/");
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
            {
                // 2026-10-04: refresh-токен, по которому обновили, — для SessionTokensRefreshed.
                var usedRefresh = RefreshToken;
                // 2026-11-06: пока шёл запрос, вошли заново (другой refresh) — старый ответ новый вход не затирает.
                if (!string.Equals(usedRefresh, body.Refresh, StringComparison.Ordinal))
                    return true;
                AccessToken = acc.GetString();
                if (root.TryGetProperty("refresh", out var refr) && refr.ValueKind == JsonValueKind.String)
                {
                    var newRefresh = refr.GetString();
                    if (!string.IsNullOrWhiteSpace(newRefresh))
                        RefreshToken = newRefresh;
                }

                PersistTokensToSecureStore();
                RaiseSessionTokensRefreshed(usedRefresh, AccessToken, RefreshToken);
                return true;
            }

            return false;
        }
        finally
        {
            _refreshSync.Release();
        }
    }

    /// <summary>2026-11-03 (Д-1, живая проверка 1.17.73): 403 без JSON-объекта в теле — не ответ NurCRM (Django отдаёт {"detail": …}),
    /// а страница прокси/CDN/WAF/провайдера. Такой ответ не «доступ отозван»: сессию стирать нельзя.</summary>
    internal static bool IsForeignForbidden(ApiException ex) =>
        ex.StatusCode == 403 && !IsNurCrmBody(ex.Payload);

    /// <summary>2026-11-03 (Д-1): отказ 403 от самого NurCRM (в теле JSON-объект) — настоящий отзыв доступа.</summary>
    internal static bool IsGenuineAccessDenied(ApiException ex) =>
        ex.StatusCode == 403 && IsNurCrmBody(ex.Payload);

    /// <summary>2026-11-03 (аудит №3, П-1): тело ответа считается ответом NurCRM, только если это JSON-объект с полем <c>detail</c> или <c>code</c> (Django/DRF) и в нём нет
    /// признаков Cloudflare (<c>error_code</c>, <c>cloudflare_error</c>, <c>ray_id</c>, <c>title</c> вида «Error 10xx»). Раньше любой JSON-объект считался «настоящим отказом NurCRM»,
    /// а Cloudflare тоже отдаёт JSON — вход стирался при отказе посредника. Единственное место правила: его используют все три проверки 403.</summary>
    internal static bool IsNurCrmBody(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } body)
            return false;
        if (body.TryGetProperty("error_code", out _) || body.TryGetProperty("cloudflare_error", out _) || body.TryGetProperty("ray_id", out _))
            return false;
        if (body.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            && System.Text.RegularExpressions.Regex.IsMatch(title.GetString() ?? "", @"^\s*Error\s+10\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;
        return body.TryGetProperty("detail", out _) || body.TryGetProperty("code", out _);
    }

    /// <summary>2026-11-03 (Д-1): отказ на обновлении токена с кодом 403 и чужим телом (не JSON-объект) — сбой посредника (502), а не «refresh отозван»:
    /// иначе автовход и JwtBearerRefreshHandler стирали сохранённый вход.</summary>
    private static async Task ThrowIfForeignForbiddenAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if ((int)resp.StatusCode != 403)
            return;
        string text;
        try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { text = ""; }
        if (IsNurCrmBody(TryParse(text)))
            return;
        PosLogger.Log("Обновление токена: ответ 403 не от NurCRM (нет JSON с detail/code или признаки Cloudflare) — вход не стираем, считаем сбоем сервера.", "AUTH");
        throw new ApiException("HTTP 403 (ответ промежуточного узла, не NurCRM)", 502);
    }

    /// <summary>2026-11-03 (С-04): обновление токена не уложилось в срок — «сервер медленно», НЕ «сессия истекла» (сессию не стираем).</summary>
    internal static ApiException RefreshSlowException()
    {
        ServerOutageMonitor.ReportSlow("auth/refresh");
        return new ApiException(Tr.T("Сервер NurCRM отвечает медленно. Попробуйте ещё раз чуть позже.",
            "NurCRM сервери жай жооп берип жатат. Бир аздан кийин кайра аракет кылыңыз.",
            "The NurCRM server is responding slowly. Please try again shortly.",
            "NurCRM sunucusu yavaş yanıt veriyor. Biraz sonra tekrar deneyin.",
            "NurCRM serveri sekin javob bermoqda. Birozdan keyin qayta urinib ko'ring."), 408);
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
        SessionExpiryMonitor.Reset(); // 2026-10-30: успешный вход снимает «нужно войти»
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
        Shift.ConstructionShiftsListAsync(openOnly: false, ct: ct);

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
        Shift.ConstructionShiftCloseAsync(shiftId, closingCash, null, ct);

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

    /// <summary>Скачивание файла с относительного пути (с query-параметрами и авторизацией) —
    /// в отличие от RequestAsync/SendOnceAsync не пытается парсить ответ как JSON (для
    /// текстовых/бинарных выгрузок вроде api/main/products/scale-export/).</summary>
    public async Task<byte[]?> DownloadAsync(string relativePath, IReadOnlyDictionary<string, string>? query, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return null;

        var uri = BuildUri(relativePath, query);
        var slot = await AcquireSlotAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyBearerAuthorization(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    public async Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return null;
        if (string.IsNullOrEmpty(AccessToken))
            return null;

        var slot = await AcquireSlotAsync(ct).ConfigureAwait(false);
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
            slot.Release();
        }
    }

    /// <summary>Разворачивает ответ (массив или {results:[…]}) в список элементов. Используется доменными сервисами.
    /// 2026-10-19: public — им же разбирает ответы выплата зарплаты программы владельца (SalaryPayoutService).</summary>
    public static List<JsonElement> UnwrapList(JsonElement data)
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
        ServerOutageMonitor.StateChanged -= OnServerLinkStateChanged;
        _http.Dispose();
        _loginMutex.Dispose();
        _httpSlots.Dispose();
        _fgSlots.Dispose();
        _refreshSync.Dispose();
    }
}
