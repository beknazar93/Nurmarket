using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.ServerCeba67c;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Util;

namespace NurMarketKassa.Tests.Lifecycle;

/// <summary>2026-11-20 (ответ Умара 20.11, раздел А.1; в ~22:00 сервер меняет ключ подписи токенов — все старые access и refresh дают 401): касса и программа владельца (тот же NurMarketApiClient)
/// на 401 от auth/refresh/ и от обычных запросов не падают — объявляют «Сессия недействительна» один раз, токены стираются, а очередь офлайн-чеков и данные остаются; после повторного входа
/// очередь досылается тем же ключом повтора. Сервер — WireMock: «старые» токены после <see cref="KeyChangedServer.ChangeSigningKey"/> недействительны везде.</summary>
[Collection("SessionExpiry")]
public sealed class Session401ResilienceTests : IDisposable
{
    public Session401ResilienceTests() => SessionExpiryMonitor.ResetForTests();

    public void Dispose() => SessionExpiryMonitor.ResetForTests();

    /// <summary>Сервер, у которого можно «сменить ключ подписи»: все выданные до этого токены становятся недействительными (401), а новый вход выдаёт новые.</summary>
    private sealed class KeyChangedServer : IDisposable
    {
        private readonly object _gate = new();
        private int _epoch;
        public WireMockServer Server { get; } = WireMockServer.Start();
        public string Url => Server.Url!;
        public string ValidAccess { get; private set; } = "";
        public string ValidRefresh { get; private set; } = "";
        public int RefreshPosts { get; private set; }
        public int Logins { get; private set; }
        public int CheckoutAccepted { get; private set; }
        public ConcurrentBag<string> CheckoutKeys { get; } = new();

        public KeyChangedServer()
        {
            Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(_ =>
            {
                lock (_gate)
                {
                    Logins++;
                    _epoch++;
                    ValidAccess = RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddMinutes(15), null, "access-" + _epoch);
                    ValidRefresh = RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow, "refresh-" + _epoch);
                    return Status(200, "{\"access\":\"" + ValidAccess + "\",\"refresh\":\"" + ValidRefresh + "\",\"user\":{\"id\":\"u1\",\"email\":\"owner@test.kg\",\"role\":\"owner\"}}");
                }
            }));
            Server.Given(Request.Create().WithPath("/api/users/auth/refresh/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req =>
            {
                Thread.Sleep(100);
                lock (_gate)
                {
                    RefreshPosts++;
                    using var doc = JsonDocument.Parse(req.Body ?? "{}");
                    var sent = doc.RootElement.TryGetProperty("refresh", out var r) ? r.GetString() : null;
                    if (sent != ValidRefresh || ValidRefresh.Length == 0)
                        return Status(401, "{\"detail\":\"Token is invalid or expired\",\"code\":\"token_not_valid\"}");
                    _epoch++;
                    ValidAccess = RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddMinutes(15), null, "access-" + _epoch);
                    return Status(200, "{\"access\":\"" + ValidAccess + "\"}");
                }
            }));
            Server.Given(Request.Create().WithPath("/api/users/profile/").UsingMethod("GET")).RespondWith(Response.Create().WithCallback(req =>
            {
                lock (_gate)
                    return Authorized(req)
                        ? Status(200, "{\"id\":\"u1\",\"email\":\"owner@test.kg\",\"role\":\"owner\",\"company\":{\"id\":\"c1\",\"name\":\"Тест\"}}")
                        : Status(401, "{\"detail\":\"Given token not valid for any token type\",\"code\":\"token_not_valid\"}");
            }));
            Server.Given(Request.Create().WithPath("/api/main/pos/checkout/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req =>
            {
                lock (_gate)
                {
                    if (!Authorized(req))
                        return Status(401, "{\"detail\":\"Given token not valid for any token type\",\"code\":\"token_not_valid\"}");
                    CheckoutAccepted++;
                    if (req.Headers is not null && req.Headers.TryGetValue("Idempotency-Key", out var k))
                        CheckoutKeys.Add(k.ToString());
                    return Status(201, "{\"id\":\"sale-" + CheckoutAccepted + "\"}");
                }
            }));
        }

        private bool Authorized(IRequestMessage req) =>
            ValidAccess.Length > 0 && req.Headers is not null && req.Headers.TryGetValue("Authorization", out var h) && h.ToString() == "Bearer " + ValidAccess;

        /// <summary>Ключ подписи сменили: ни один выданный раньше access и refresh больше не действует.</summary>
        public void ChangeSigningKey()
        {
            lock (_gate)
            {
                ValidAccess = "";
                ValidRefresh = "";
            }
        }

        private static IResponseMessage Status(int code, string body)
        {
            var message = new ResponseMessage { StatusCode = code, BodyData = new BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = body } };
            message.AddHeader("Content-Type", "application/json");
            return message;
        }

        public void Dispose() => Server.Stop();
    }

    private sealed class MemorySessionStore : IAuthSessionManager
    {
        public UserSession? Saved { get; private set; }
        public Task SaveSessionAsync(UserSession session) { lock (this) Saved = session; return Task.CompletedTask; }
        public Task<UserSession?> LoadSessionAsync() { lock (this) return Task.FromResult(Saved); }
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { lock (this) Saved = null; return Task.CompletedTask; }
    }

    private static NurMarketApiClient NewClient(KeyChangedServer s) => new(new AppSettings { ApiBaseUrl = s.Url });

    private static string Cart() => new JsonObject
    {
        ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Хлеб", ["quantity"] = 2, ["unit_price"] = 30.0 }),
    }.ToJsonString();

    [Fact]
    public async Task Parallel_requests_after_the_key_change_end_in_one_session_lost_no_exception_other_than_ApiException_401()
    {
        using var server = new KeyChangedServer();
        var api = NewClient(server);
        await api.LoginAsync("owner@test.kg", "12345678");
        server.ChangeSigningKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            try { await api.GetProfileAsync(); return null; }
            catch (Exception ex) { return ex; }
        })));

        results.Should().OnlyContain(e => e is ApiException, "ни одного падения вне ApiException (окно входа получает понятную ошибку, а не необработанное исключение)");
        results.Cast<ApiException>().Should().OnlyContain(e => e.StatusCode == 401);
        SessionExpiryMonitor.IsExpired.Should().BeTrue("потеря входа объявлена — касса показывает «Войдите снова»");
        server.RefreshPosts.Should().Be(1, "обновление токена по мёртвому refresh одно на всех, а не по числу запросов");
        api.AccessToken.Should().BeNull();
        api.RefreshToken.Should().BeNull("мёртвые токены стёрты — повторный POST auth/refresh/ не нужен");
    }

    [Fact]
    public async Task Session_lost_event_is_raised_once_and_the_login_again_restores_work()
    {
        using var server = new KeyChangedServer();
        var api = NewClient(server);
        await api.LoginAsync("owner@test.kg", "12345678");
        var expiredEvents = 0;
        void OnExpired() => Interlocked.Increment(ref expiredEvents);
        SessionExpiryMonitor.Expired += OnExpired;
        try
        {
            server.ChangeSigningKey();
            for (var i = 0; i < 3; i++)
                await FluentActions.Awaiting(() => api.GetProfileAsync()).Should().ThrowAsync<ApiException>();
            expiredEvents.Should().Be(1, "окно «Войдите снова» открывается один раз, а не на каждый запрос");

            await api.LoginAsync("owner@test.kg", "12345678"); // кассир вошёл заново — сервер выдал новые токены
            SessionExpiryMonitor.IsExpired.Should().BeFalse();
            (await api.GetProfileAsync()).GetProperty("email").GetString().Should().Be("owner@test.kg");
        }
        finally
        {
            SessionExpiryMonitor.Expired -= OnExpired;
        }
    }

    [Fact]
    public async Task Autologin_after_restart_with_a_dead_refresh_gives_the_login_screen_without_exception_and_login_works()
    {
        using var server = new KeyChangedServer();
        var storage = new MemorySessionStore();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), storage);
        (await auth.LoginAsync("owner@test.kg", "12345678", rememberMe: true)).IsSuccess.Should().BeTrue();
        storage.Saved.Should().NotBeNull();
        server.ChangeSigningKey(); // сервер сменил ключ, пока касса была закрыта

        var api2 = NewClient(server);
        var auth2 = new OnlineOfflineAuthenticationService(new AuthApiService(api2), storage);
        var restored = await auth2.AutoLoginAsync();

        restored.IsSuccess.Should().BeFalse();
        restored.Failure.Should().Be(AuthenticationFailure.SessionExpired, "окно входа с пояснением «Сессия истекла/отозвана», а не падение");
        restored.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        storage.Saved.Should().BeNull("мёртвый сохранённый вход стёрт — иначе каждый запуск повторял бы запрос");
        api2.AccessToken.Should().BeNull();

        var again = await auth2.LoginAsync("owner@test.kg", "12345678", rememberMe: true);
        again.IsSuccess.Should().BeTrue("повторный вход по паролю работает");
        storage.Saved.Should().NotBeNull();
    }

    [Fact]
    public async Task Offline_queue_survives_the_key_change_and_is_sent_after_login_again_with_the_same_idempotency_key()
    {
        using var server = new KeyChangedServer();
        var api = NewClient(server);
        await api.LoginAsync("owner@test.kg", "12345678");
        var id = Guid.NewGuid().ToString("N");
        OfflinePendingSalesStore.Append(new OfflineSaleEntry
        {
            Id = id,
            PaymentMethod = "cash",
            CashReceived = "100",
            CartJson = Cart(),
            QuickCheckoutAttempted = true,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-5),
        });
        var auth = DispatchProxy.Create<IAuthApiService, BelowCostRightTests.AuthOnlineStub>();
        var sync = new SyncService(new SalesApiService(api), auth, null!, null!, null!, null!);
        try
        {
            server.ChangeSigningKey();

            // Проход очереди на мёртвых токенах: 401 на запросе → refresh 401 → вход потерян. Чек НЕ уходит в «Ошибки отправки» и не «Требует проверки».
            await TrySyncAsync(sync);
            var waiting = OfflinePendingSalesStore.TryGetById(id);
            waiting.Should().NotBeNull("очередь не потеряна");
            waiting!.Status.Should().NotBe(OfflineSaleEntry.Failed, "401 — не отказ по чеку, а потеря входа");
            waiting.Status.Should().NotBe(OfflineSaleEntry.Synced);
            waiting.ReviewNeeded.Should().BeFalse();
            server.CheckoutAccepted.Should().Be(0);
            SessionExpiryMonitor.IsExpired.Should().BeTrue();

            // Пока вход потерян, проход очереди ничего не отправляет и не помечает ошибкой.
            await TrySyncAsync(sync);
            OfflinePendingSalesStore.TryGetById(id)!.Status.Should().Be(waiting.Status);

            // Кассир вошёл заново — очередь досылается (сама по событию или проходом ниже), ключ повтора — id записи.
            await api.LoginAsync("owner@test.kg", "12345678");
            SessionExpiryMonitor.IsExpired.Should().BeFalse();
            for (var i = 0; i < 40 && server.CheckoutAccepted == 0; i++)
            {
                await TrySyncAsync(sync);
                if (server.CheckoutAccepted == 0)
                    await Task.Delay(100);
            }

            server.CheckoutAccepted.Should().Be(1, "чек из очереди ушёл на сервер после повторного входа");
            server.CheckoutKeys.Should().ContainSingle().Which.Should().Be(id);
            var done = OfflinePendingSalesStore.TryGetById(id);
            (done is null || done.Status == OfflineSaleEntry.Synced).Should().BeTrue("отправленный чек уходит из очереди (или помечен отправленным)");
        }
        finally
        {
            sync.Dispose();
            OfflinePendingSalesStore.RemoveSynced(id);
        }
    }

    private static async Task TrySyncAsync(SyncService sync)
    {
        try { await sync.ProbeNowAsync(); await sync.TriggerSyncNowAsync(); }
        catch (Exception) { /* у подставных служб нет каталога и т. п. — для проверки важна только очередь чеков */ }
    }
}
