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
public sealed partial class NurMarketApiClient : IDisposable
{
    // 2026-11-06 (ответ Умара: лимита устройств нет, refresh живёт 7 суток от входа по паролю): раньше «часто из‑за входа с другого ПК или телефона» вводило в заблуждение.
    // Префикс «Сессия недействительна» ОСТАВЛЕН — по нему SessionExpiryMonitor узнаёт потерю входа (в том числе в старых текстах из кэша). Пользователь видит перевод (FriendlyError и окна).
    public const string AuthInvalidHintRu =
        "Сессия недействительна: срок входа истёк, войдите заново.";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonWrite = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly JsonSerializerOptions _jsonRead = new() { PropertyNameCaseInsensitive = true };
    /// <summary>Исключает гонки при входе и ручном refresh.</summary>
    private readonly SemaphoreSlim _loginMutex = new(1, 1);
    /// <summary>Параллельные GET/POST к API (раньше один глобальный замок сильно замедлял каталог).</summary>
    /// <summary>2026-11-03 (С-02, аудит «зависание при медленном сервере»): фоновая полоса — 8 мест (было 12 на всех); действия кассира идут в своей «быстрой полосе»
    /// <see cref="_fgSlots"/> (4 места), поэтому зависший фон (каталог, сводка владельца) больше не держит клик кассира в очереди. Всего мест по-прежнему 12.</summary>
    private readonly SemaphoreSlim _httpSlots = new(Math.Min(8, Math.Max(4, Environment.ProcessorCount * 2)), Math.Min(8, Math.Max(4, Environment.ProcessorCount * 2)));
    /// <summary>2026-11-03 (С-02): быстрая полоса переднего плана и критичных операций (оплата, смена): 4 места.</summary>
    private readonly SemaphoreSlim _fgSlots = new(4, 4);

    private static readonly AsyncLocal<bool> ForegroundFlag = new();

    /// <summary>2026-11-03 (С-02): запросы внутри этого блока (и их вложенные await) идут в быструю полосу очереди. Открывают <c>ServerAnswerWait.ForegroundAsync</c>/<c>ForegroundWriteAsync</c>.</summary>
    public static IDisposable Foreground()
    {
        var previous = ForegroundFlag.Value;
        ForegroundFlag.Value = true;
        return new ForegroundScope(previous);
    }

    /// <summary>Запрос сейчас идёт от действия кассира (передний план) или критичной операции.</summary>
    public static bool IsForeground => ForegroundFlag.Value || Api.ApiThrottle.IsCritical;

    private sealed class ForegroundScope(bool previous) : IDisposable
    {
        public void Dispose() => ForegroundFlag.Value = previous;
    }

    /// <summary>2026-11-03 (С-02): ждёт место в нужной полосе очереди и возвращает семафор, который надо освободить в finally.</summary>
    private async Task<SemaphoreSlim> AcquireSlotAsync(CancellationToken ct)
    {
        var slots = IsForeground ? _fgSlots : _httpSlots;
        await slots.WaitAsync(ct).ConfigureAwait(false);
        return slots;
    }
    /// <summary>Серийный refresh токена при 401 из параллельных запросов.</summary>
    private readonly SemaphoreSlim _refreshSync = new(1, 1);

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public JsonElement UserPayload { get; private set; }
    public string? ActiveBranchId { get; private set; }

    public NurMarketApiClient(AppSettings settings)
    {
        // 2026-11-06: В3 Л4-21 — и здесь адрес API принимается только по https (http — лишь для петли и частной сети: стенды, тесты), на случай настроек, собранных мимо AppSettings.Load.
        var baseUrl = NurMarketKassa.Configuration.AppSettings.NormalizeApiUrl(settings.ApiBaseUrl).TrimEnd('/') + "/";
        // 2026-10-04, стресс-тест: сервер NurCRM умеет gzip (страница каталога 155 КБ → 18 КБ), а касса его не
        // просила — каждые 2 минуты качался полный каталог без сжатия. Accept-Encoding ставится сам.
        // 2026-10-29 (замер скорости): HTTP/2 с откатом на 1.1 и долгое хранение соединения (TransportTuningHandler + SocketsHttpHandler) —
        // соединение не закрывается между тактами ленты (раньше новое TLS-рукопожатие ~170 мс), пинг HTTP/2 вовремя замечает оборванную связь.
        var handler = new JwtBearerRefreshHandler(this)
        {
            InnerHandler = new TransportTuningHandler
            {
                InnerHandler = new SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    // 2026-10-29: было 15 с — на медленной линии ответ на пинг запаздывал, и живое соединение обрывали (все запросы на нём падали).
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                    EnableMultipleHttp2Connections = true,
                },
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            // 2026-11-06: В3 Р2 — общий предел 55 с обрывал тяжёлые отчёты (analytics/pnl считается 60–90 с), хотя конвейер GET (TransportTuningHandler) и вызывающие
            // давали им до 90 с. Предел клиента теперь 100 с (больше HeavyGetTimeout); обычным запросам 55 с держит сам обработчик (TransportTuningHandler, DefaultRequestTimeout).
            Timeout = TimeSpan.FromSeconds(100),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 2026-11-05 (аудит №4, п. 4): сервер видит, какая версия кассы пришла (поддержка, будущие проверки совместимости).
        // 2026-11-06: В3 А5-19 — сервер и поддержка отличают платформу: у Android и Linux после версии добавляется «(android)» / «(linux)»; у Windows заголовок прежний.
        var platformTail = OperatingSystem.IsAndroid() ? " (android)" : OperatingSystem.IsLinux() ? " (linux)" : "";
        try { _http.DefaultRequestHeaders.TryAddWithoutValidation("X-NurMarket-Client", "kassa/" + AppVersionInfo.CurrentVersionLabel + platformTail); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Заголовок X-NurMarket-Client не добавлен: {ex.GetType().Name}", "WARNING"); }
        UserPayload = default;
        // 2026-09-29: этим клиентом ServerOutageMonitor проверяет, ожил ли сервер после аварии.
        ServerOutageMonitor.HealthProbe = ProbeServerHealthAsync;
        ServerOutageMonitor.StateChanged += OnServerLinkStateChanged;
    }

    /// <summary>2026-10-04, стенд «сбои сервера»: запросы, ушедшие ДО объявления аварии, при аварии
    /// отменяются сразу, а не висят до таймаута HttpClient (55 с). При «чёрной дыре» фоновые загрузки
    /// (каталог, история продаж, остатки) занимали все места общей очереди запросов (_httpSlots, 12 на
    /// этом ПК): сервер ожил, а первая оплата после восстановления ждала свободного места, не
    /// дожидалась за 1,8 с, снова уходила в очередь и снова объявляла аварию — до минуты после
    /// восстановления касса продавала «офлайн» на живом сервере. Отменённые запросы для вызывающих —
    /// тот же таймаут, который они и так получили бы позже: фоновые повторят при следующем цикле,
    /// оплата и досылка уже работают с ключом идемпотентности / сверкой корзины.</summary>
    private CancellationTokenSource _outageCancel = new();

    // 2026-11-03 (С-02): internal — ClientDebtsApiService (свой HttpClient с Idempotency-Key) тоже отменяется при объявлении аварии.
    internal CancellationToken OutageCancelToken => Volatile.Read(ref _outageCancel).Token;

    private void OnServerLinkStateChanged(ServerLinkState from, ServerLinkState to)
    {
        if (to != ServerLinkState.Outage)
            return;
        var previous = Interlocked.Exchange(ref _outageCancel, new CancellationTokenSource());
        // В фоне: событие приходит из потока, объявившего аварию (например, оплаты), — отмена чужих
        // запросов не должна выполнять их продолжения в нём. Не Dispose: на её токен могут как раз
        // сейчас связываться новые запросы.
        _ = Task.Run(() =>
        {
            try
            {
                previous.Cancel();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Отмена зависших запросов при аварии не удалась: {ex.Message}", "OUTAGE WARNING");
            }
        });
    }

    /// <summary>2026-11-06 (В11, 25.1): корень адреса API (с «/» в конце) — для сборки адресов вызывающим, что шлёт запросы через <see cref="SendRawAsync"/>.</summary>
    internal Uri BaseAddress => _http.BaseAddress!;

    /// <summary>2026-11-06 (В11, 25.1): отправка готового запроса ОБЩЕЙ цепочкой обработчиков этого клиента (Bearer и обновление токена, HTTP/2 и общий пул соединений,
    /// щит 429, срок ожидания, поправка часов ServerClock, «Сервер медлит»). Для запросов со своими заголовками (Idempotency-Key у ClientDebtsApiService), которые
    /// раньше шли через отдельный HttpClient с собственным пулом и без этих обработчиков. Очередь мест (_httpSlots) и разбор ответа остаются за вызывающим.</summary>
    internal Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct) => _http.SendAsync(request, ct);

    internal void ApplyBearerAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return;

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        MaybeRefreshAhead();
    }

    /// <summary>2026-11-03 (С-04, аудит «зависание при медленном сервере»): срок на обновление токена — и на замок, и на сам запрос. Дольше сервер не ждём:
    /// запрос получает ApiException 408 «отвечает медленно», сессия НЕ сбрасывается.</summary>
    internal static readonly TimeSpan RefreshBudget = TimeSpan.FromSeconds(10);

    private string? _expParsedToken;
    private DateTimeOffset _expParsedAt;
    private int _aheadRefreshRunning;
    private long _aheadNextTryTicks;

    /// <summary>2026-11-03 (С-04): за 60 с до конца access-токена (поле exp в JWT) обновляем его заранее фоновой задачей, чтобы передний план почти не встречал 401
    /// и не стоял у ворот обновления при медленном сервере. Не чаще раза в 30 с, никогда не сбрасывает сессию.</summary>
    private void MaybeRefreshAhead()
    {
        try
        {
            var token = AccessToken;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(RefreshToken))
                return;
            if (!string.Equals(token, _expParsedToken, StringComparison.Ordinal))
            {
                _expParsedAt = TryReadJwtExpiry(token);
                _expParsedToken = token;
            }

            if (_expParsedAt == default || _expParsedAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
                return;
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _aheadNextTryTicks))
                return;
            if (Interlocked.CompareExchange(ref _aheadRefreshRunning, 1, 0) != 0)
                return;
            Interlocked.Exchange(ref _aheadNextTryTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(RefreshBudget);
                    if (await RefreshAccessAndPersistAsync(cts.Token).ConfigureAwait(false))
                        PosLogger.Log("Токен доступа обновлён заранее (до истечения).", "AUTH");
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Заблаговременное обновление токена не удалось ({ex.GetType().Name}) — обновим по требованию.", "AUTH");
                }
                finally
                {
                    Interlocked.Exchange(ref _aheadRefreshRunning, 0);
                }
            });
        }
        catch
        {
            // Заблаговременное обновление — удобство, не обязанность: любой сбой молча уступает обычному пути (401 → refresh).
        }
    }

    /// <summary>Время окончания access-токена из поля exp JWT; default — не разобрали.</summary>
    private static DateTimeOffset TryReadJwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return default;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : default;
        }
        catch
        {
            return default;
        }
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

    /// <summary>2026-10-04, отчёт «офлайн и сбои сервера», раздел «Риски»: токены, обновлённые посреди
    /// работы (401 → refresh в JwtBearerRefreshHandler), сохранялись только в старый файл сессии
    /// (offline_auth_session.dat), который вход очищает, — в auth.dat, откуда их берёт автовход,
    /// оставались токены и отметка связи момента входа/автовхода. Проверено на тестовом аккаунте 04.10:
    /// сейчас NurCRM на обновление отдаёт только новый access (живёт 15 мин), refresh-токен не меняет и
    /// старый не отзывает (живёт 7 дней от входа) — поэтому перезапуск до сих пор не ломался. Но
    /// (1) отметка «последняя связь с сервером» в auth.dat стояла на моменте запуска, и 60 часов
    /// офлайн-работы считались от него, а не от последнего ответа сервера; (2) включи сервер смену
    /// refresh-токена при обновлении — касса после перезапуска открывала бы окно входа. Событие:
    /// (refresh, по которому обновили; новый access; refresh после обновления) — его слушает
    /// OnlineOfflineAuthenticationService и переписывает auth.dat. Вызывается под замком входа,
    /// обработчик не должен ждать сеть.</summary>
    public static event Action<string?, string?, string?>? SessionTokensRefreshed;

    private static void RaiseSessionTokensRefreshed(string? usedRefresh, string? access, string? refresh)
    {
        try
        {
            SessionTokensRefreshed?.Invoke(usedRefresh, access, refresh);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сохранение обновлённых токенов: подписчик упал: {ex.GetType().Name}", "AUTH");
        }
    }

    // Ленивые экземпляры доменных сервисов для делегирования из устаревших методов.
    private CatalogApiService? _catalogApi;
    private SalesApiService? _salesApi;
    private ShiftApiService? _shiftApi;

    internal CatalogApiService Catalog => _catalogApi ??= new CatalogApiService(this);
    internal SalesApiService Sales => _salesApi ??= new SalesApiService(this);
    internal ShiftApiService Shift => _shiftApi ??= new ShiftApiService(this);

    /// <summary>Проверка доступности API (аналог can_reach_api).</summary>
    /// <summary>Bounds each probe well under the client's 55s request timeout — otherwise a
    /// "black hole" network (adapter up, no route) can leave the UI reporting online status
    /// for up to ~110s (two endpoints × full timeout) before falling back to offline mode.</summary>
    private static readonly TimeSpan ConnectivityProbeTimeout = TimeSpan.FromSeconds(4);

    public async Task<bool> CanReachApiAsync(CancellationToken ct = default)
    {
        foreach (var path in new[] { "", "api/users/auth/login/" })
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ConnectivityProbeTimeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
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
            // На выходе приложения хост может освободить этот клиент (и семафор) раньше,
            // чем завершится уже запущенный сетевой запрос — тогда Release() здесь
            // достаётся уже уничтоженному объекту. Само приложение в этот момент уже
            // закрывается, поэтому эту ошибку можно спокойно проглотить.
            try { _loginMutex.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>2026-11-06: обновление access-токена по refresh. Раньше здесь был свой замок (_loginMutex) и своя копия запроса, а обновление по 401 и «заранее» шло под другим замком
    /// (_refreshSync): два обновления по ОДНОМУ refresh-токену могли идти параллельно. Когда сервер включит ротацию refresh (каждый ответ — новый refresh, старый недействителен), второе
    /// получило бы 401 и объявило бы потерю входа ложно. Теперь все пути идут через один метод и один замок (<see cref="RefreshAccessUnlockedAsync"/>).</summary>
    public Task<bool> RefreshAccessAsync(CancellationToken ct = default) => RefreshAccessUnlockedAsync(ct);

    public void ClearSession()
    {
        AccessToken = null;
        RefreshToken = null;
        UserPayload = default;
        ActiveBranchId = null;
        CompanyInfoService.Clear();
    }

    /// <summary>2026-10-30: сервер отозвал вход (401, refresh отклонён) — токены стираем, а профиль (роль, права, филиал, ИНН)
    /// и данные компании оставляем: раньше полный ClearSession обнулял UserPayload, и «Клиенты» в программе владельца
    /// отвечали «Недостаточно прав» (права = пусто). Объявляет сессию недействительной один раз (SessionExpiryMonitor).</summary>
    public void ExpireSession(string source)
    {
        AccessToken = null;
        RefreshToken = null;
        SessionExpiryMonitor.Report(source);
    }

    /// <summary>2026-10-18, аудит («Сменить кассира» с неверным паролем): полный снимок текущего входа —
    /// токены, профиль пользователя (роль, доступы, id, email), филиал и ИНН компании. Вход нового кассира
    /// при ошибке стирает всё это (ClearSession), а восстановление через RestoreOfflineSession возвращало
    /// только токены — до перезапуска у владельца пропадали Настройки/склад.</summary>
    public sealed record SessionState(string? AccessToken, string? RefreshToken, JsonElement UserPayload, string? ActiveBranchId, string StoreInn);

    public SessionState CaptureSessionState() =>
        new(AccessToken, RefreshToken, UserPayload.ValueKind == JsonValueKind.Undefined ? default : UserPayload.Clone(), ActiveBranchId,
            UserPreferences.Instance.StoreInn ?? string.Empty);

    public void RestoreSessionState(SessionState state)
    {
        AccessToken = state.AccessToken;
        RefreshToken = state.RefreshToken;
        UserPayload = state.UserPayload;
        ActiveBranchId = state.ActiveBranchId;
        UserPreferences.Instance.StoreInn = state.StoreInn;
    }

    /// <summary>Восстановление сессии из локального кэша (офлайн-вход).</summary>
    public void RestoreOfflineSession(OfflineAuthSession session)
    {
        SessionExpiryMonitor.Reset(); // 2026-10-30
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
        // 2026-11-09: права — из сохранённого входа (могут быть устаревшими); перечитываются при возврате связи (PermissionRecheck).
        PermissionRecheck.MarkSavedSession();
    }

    /// <summary>GET /api/users/profile/</summary>
    public Task<JsonElement> GetProfileAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "api/users/profile/", null, null, ct);

    /// <summary>GET /api/users/company/</summary>
    public async Task<CompanyDto?> GetCompanyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        // 2026-11-03 (С-02, Сис-5): + отмена при объявлении аварии, как в RequestAsync — раньше запрос компании висел до 55 с при уже объявленной аварии.
        using var linkedCompany = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        ct = linkedCompany.Token;
        var slot = await AcquireSlotAsync(ct).ConfigureAwait(false);
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
            // 2026-09-28: вид магазина компании (market_sphere, BE-18) — см. MarketSphereSync.
            MarketSphereSync.RememberFromCompanyJson(company?.Id, doc.RootElement);
            PosLogger.Log(
                $"Company loaded. CompanyId={MaskIdentifier(company?.Id)}, " +
                $"INNConfigured={!string.IsNullOrWhiteSpace(company?.Inn)}, " +
                $"AddressConfigured={!string.IsNullOrWhiteSpace(company?.Address)}",
                "API");
            return company;
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>GET /api/users/settings/company/ — scale_barcode_layout/scale_barcode_mode/
    /// scale_barcode_amount_unit (настройки формата весового штрих-кода, отдельный эндпоинт от
    /// api/users/company/). 2026-09-14, живой баг: amount_unit раньше не запрашивался вообще —
    /// касса всегда считала суммовой штрих-код в тыйынах (÷100), хотя документация NurCRM
    /// поддерживает и "som" (без деления) — компании с этой настройкой получали сумму в 100 раз
    /// меньше настоящей на каждом суммовом весовом штрих-коде.</summary>
    public async Task<(string? Layout, string? Mode, string? AmountUnit)> GetScaleSettingsAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(HttpMethod.Get, "api/users/settings/company/", null, null, ct).ConfigureAwait(false);
        var root = data;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            root = inner;

        string? layout = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("scale_barcode_layout", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() : null;
        string? mode = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("scale_barcode_mode", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null;
        string? amountUnit = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("scale_barcode_amount_unit", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() : null;
        return (layout, mode, amountUnit);
    }
}
