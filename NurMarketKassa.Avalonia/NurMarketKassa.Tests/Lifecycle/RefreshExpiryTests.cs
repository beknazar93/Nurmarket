using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Util;

namespace NurMarketKassa.Tests.Lifecycle;

/// <summary>2026-11-06 (ответ Умара: refresh-токен живёт 7 суток от входа по паролю, возможна ротация refresh): заблаговременное предупреждение «Вход истекает через N ч»
/// (RefreshExpiryWatcher, часы подменяются), чтение старого auth.dat без нового поля, токен не в журнале, распознавание потери сессии по новому и старому тексту,
/// а также ротация refresh на настоящем клиенте против сервера с ротацией (WireMock): одно обновление на много параллельных запросов, auth.dat с последним refresh,
/// автовход после перезапуска, пересчёт срока входа.</summary>
[Collection("SessionExpiry")]
public sealed class RefreshExpiryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = T0;

    public RefreshExpiryTests()
    {
        SessionExpiryMonitor.ResetForTests();
        RefreshExpiryWatcher.Current.Clear();
    }

    public void Dispose()
    {
        SessionExpiryMonitor.ResetForTests();
        RefreshExpiryWatcher.Current.Clear();
    }

    private RefreshExpiryWatcher NewWatcher(Func<bool>? busy = null) => new(() => _now, busy, autoTimer: false);

    /// <summary>JWT без подписи (клиент подпись не проверяет): header.payload.sig с exp/iat/jti.</summary>
    internal static string Jwt(DateTimeOffset exp, DateTimeOffset? iat = null, string? jti = null)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = "{\"token_type\":\"refresh\",\"exp\":" + exp.ToUnixTimeSeconds()
            + (iat is { } i ? ",\"iat\":" + i.ToUnixTimeSeconds() : "")
            + ",\"jti\":\"" + (jti ?? Guid.NewGuid().ToString("N")) + "\"}";
        return B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + B64(payload) + ".sig";
    }

    // ------------------------------------------------------------------ остаток и надпись

    [Fact]
    public void More_than_a_day_left_shows_nothing()
    {
        var w = NewWatcher();
        var changes = 0;
        w.Changed += () => changes++;
        w.Configure(Jwt(_now.AddDays(5)), null);

        w.Remaining.Should().BeCloseTo(TimeSpan.FromDays(5), TimeSpan.FromSeconds(1));
        w.IsWarning.Should().BeFalse();
        w.WarningText.Should().BeNull();
        changes.Should().Be(0);
    }

    [Fact]
    public void Within_a_day_the_header_text_appears_with_hours_and_goes_away_after_a_new_login()
    {
        var w = NewWatcher();
        var changes = 0;
        w.Changed += () => changes++;
        w.Configure(Jwt(_now.AddHours(9.5)), null);

        w.IsWarning.Should().BeTrue();
        w.HoursLeft.Should().Be(10);
        w.WarningText.Should().Contain("10").And.Contain("🔑");
        changes.Should().Be(1);

        _now = _now.AddHours(2); // 7,5 ч → «через 8 ч»
        w.Tick();
        w.WarningText.Should().Contain("8");
        changes.Should().Be(2);

        w.Configure(Jwt(_now.AddDays(7)), _now); // вошли заново по паролю — надпись исчезла
        w.WarningText.Should().BeNull();
        changes.Should().Be(3);
    }

    [Fact]
    public void Unknown_expiry_shows_nothing()
    {
        var w = NewWatcher();
        w.Configure("not-a-jwt", null);
        w.Remaining.Should().BeNull();
        w.WarningText.Should().BeNull();
        w.IsWarning.Should().BeFalse();

        w.Configure("", null);
        w.WarningText.Should().BeNull();
    }

    [Fact]
    public void Without_jwt_exp_seven_days_are_counted_from_the_password_login()
    {
        var w = NewWatcher();
        w.Configure("opaque-refresh-token", _now.AddHours(-(6 * 24 + 12))); // 6,5 суток назад → осталось 12 ч
        w.IsWarning.Should().BeTrue();
        w.HoursLeft.Should().Be(12);
        w.ExpiresAt.Should().Be(_now.AddHours(12));
    }

    [Fact]
    public void Expiry_time_in_the_past_is_not_a_warning()
    {
        var w = NewWatcher();
        w.Configure(Jwt(_now.AddMinutes(-5)), null);
        w.IsWarning.Should().BeFalse("срок вышел — дальше работает окно «Сессия истекла»");
        w.WarningText.Should().BeNull();
    }

    // ------------------------------------------------------------------ окно-вопрос

    [Fact]
    public void Prompt_is_due_once_when_three_hours_are_left()
    {
        var w = NewWatcher();
        var prompts = 0;
        w.PromptDue += () => prompts++;

        w.Configure(Jwt(_now.AddHours(5)), null);
        prompts.Should().Be(0, "5 ч — только надпись");

        _now = _now.AddHours(2.5); // осталось 2,5 ч
        w.Tick();
        prompts.Should().Be(1);

        _now = _now.AddMinutes(30);
        w.Tick();
        w.Tick();
        prompts.Should().Be(1, "один раз за запуск");
    }

    [Fact]
    public void Prompt_waits_while_the_receipt_has_items_or_payment_is_running()
    {
        var busy = true;
        var w = NewWatcher(() => busy);
        var prompts = 0;
        w.PromptDue += () => prompts++;

        w.Configure(Jwt(_now.AddHours(2)), null);
        prompts.Should().Be(0, "в чеке товары — окно не показываем");

        _now = _now.AddMinutes(4);
        w.Tick();
        prompts.Should().Be(0, "повтор не раньше чем через 5 минут");

        _now = _now.AddMinutes(2);
        w.Tick();
        prompts.Should().Be(0, "через 6 минут чек всё ещё занят — снова ждём");

        busy = false;
        _now = _now.AddMinutes(5);
        w.Tick();
        prompts.Should().Be(1, "чек закрыт — вопрос показан");
    }

    [Fact]
    public void Prompt_is_not_marked_as_shown_when_nobody_listens()
    {
        var w = NewWatcher();
        w.Configure(Jwt(_now.AddHours(2)), null); // окна ещё нет (экран входа)
        var prompts = 0;
        w.PromptDue += () => prompts++;
        w.Tick();
        prompts.Should().Be(1, "окно появилось позже — вопрос не потерян");
    }

    [Fact]
    public void Prompt_can_come_again_after_a_new_login()
    {
        var w = NewWatcher();
        var prompts = 0;
        w.PromptDue += () => prompts++;
        w.Configure(Jwt(_now.AddHours(1)), null);
        prompts.Should().Be(1);

        w.Configure(Jwt(_now.AddDays(7)), _now); // вошли заново
        _now = _now.AddDays(7).AddHours(-2);
        w.Tick();
        prompts.Should().Be(2, "новый срок — новый вопрос");
    }

    // ------------------------------------------------------------------ ротация refresh: срок по новому токену

    [Fact]
    public void Rotated_refresh_moves_the_expiry_and_keeps_the_password_login_time()
    {
        var w = NewWatcher();
        var login = _now.AddDays(-6);
        w.Configure(Jwt(_now.AddHours(8), login), login);
        w.IsWarning.Should().BeTrue();

        w.RefreshTokenRotated(Jwt(_now.AddDays(7), _now));

        w.IsWarning.Should().BeFalse();
        w.WarningText.Should().BeNull();
        w.LoggedInAt.Should().Be(login, "ротация не меняет время входа по паролю");
    }

    // ------------------------------------------------------------------ старый auth.dat и журнал

    [Fact]
    public async Task Old_auth_dat_without_PasswordLoginAt_is_read_as_unknown_and_new_field_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-refresh-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "auth.dat");
            var legacy = "{\"accessToken\":\"a.b.c\",\"refreshToken\":\"r.r.r\",\"expiresAt\":\"2026-11-06T12:15:00+00:00\",\"userId\":\"u1\",\"login\":\"cashier@test.kg\","
                         + "\"displayName\":\"Кассир\",\"role\":\"owner\",\"branchId\":null,\"permissions\":[],\"lastOnlineContactAt\":\"2026-11-06T12:00:00+00:00\"}";
            File.WriteAllBytes(file, PortableSecret.Protect(Encoding.UTF8.GetBytes(legacy), Encoding.UTF8.GetBytes("NurMarketKassa:AuthSession:v1")));
            var storage = new SecureStorageService(file);

            var loaded = await storage.LoadSessionAsync();
            loaded.Should().NotBeNull();
            loaded!.Login.Should().Be("cashier@test.kg");
            loaded.PasswordLoginAt.Should().BeNull("старый файл — «время входа неизвестно»");

            var at = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
            await storage.SaveSessionAsync(new UserSession
            {
                AccessToken = loaded.AccessToken, RefreshToken = loaded.RefreshToken, ExpiresAt = loaded.ExpiresAt, UserId = loaded.UserId, Login = loaded.Login,
                PasswordLoginAt = at,
            });
            (await storage.LoadSessionAsync())!.PasswordLoginAt.Should().Be(at);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* временная папка */ }
        }
    }

    [Fact]
    public void Token_never_reaches_the_log_only_the_expiry_time()
    {
        var lines = new List<string>();
        var previous = PosLogger.Observer;
        PosLogger.Observer = (_, category, text) => { lock (lines) lines.Add(category + " " + text); };
        try
        {
            var secret = Jwt(_now.AddHours(30), _now, "SECRET-JTI-MARKER-12345");
            var payloadSegment = secret.Split('.')[1];
            var w = NewWatcher();
            w.Configure(secret, _now);
            w.RefreshTokenRotated(Jwt(_now.AddHours(40), _now, "SECRET-JTI-ROTATED"));

            lock (lines)
            {
                lines.Should().Contain(l => l.Contains("AUTH: вход действует до") && l.Contains("осталось 30 ч"));
                lines.Should().NotContain(l => l.Contains(secret) || l.Contains(payloadSegment) || l.Contains("SECRET-JTI"));
            }
        }
        finally
        {
            PosLogger.Observer = previous;
        }
    }

    [Fact]
    public void Loss_description_says_how_many_hours_passed_when_the_login_time_is_known()
    {
        var w = NewWatcher();
        w.DescribeLoss().Should().BeNull("время входа неизвестно");

        w.Configure(Jwt(_now.AddDays(7)), _now);
        _now = _now.AddDays(7).AddMinutes(10);
        w.DescribeLoss().Should().Be("срок входа истёк (прошло 168 ч с входа)");
    }

    // ------------------------------------------------------------------ распознавание потери сессии

    [Fact]
    public void Session_lost_is_recognized_by_the_new_text_and_by_the_old_text_from_caches()
    {
        NurMarketApiClient.AuthInvalidHintRu.Should().StartWith("Сессия недействительна").And.NotContain("Выйти").And.NotContain("другого ПК");
        SessionExpiryMonitor.IsSessionLost(new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401)).Should().BeTrue();
        SessionExpiryMonitor.IsSessionLost(new ApiException(NurMarketApiClient.AuthInvalidHintRu)).Should().BeTrue("без кода — по тексту");

        const string oldText = "Сессия недействительна (часто из‑за входа с другого ПК или телефона). Нажмите «Выйти» в кассе и войдите снова.";
        SessionExpiryMonitor.IsSessionLost(new ApiException(oldText, 401)).Should().BeTrue();
        SessionExpiryMonitor.IsAuthFailure(new ApiException(oldText)).Should().BeTrue();

        SessionExpiryMonitor.IsSessionLost(new ApiException("Нет прав", 401)).Should().BeFalse("голый 401 при живой сессии — не потеря входа");
        SessionExpiryMonitor.HasSessionLostText("что-то другое").Should().BeFalse();
    }

    // ------------------------------------------------------------------ ротация refresh на настоящем клиенте

    /// <summary>NurCRM с ротацией refresh: каждый auth/refresh/ отдаёт НОВЫЙ refresh, прежний сразу недействителен (401). Токены — JWT с exp.</summary>
    private sealed class RotatingServer : IDisposable
    {
        private readonly object _gate = new();
        private int _n;
        public WireMockServer Server { get; } = WireMockServer.Start();
        public string Url => Server.Url!;
        public bool Rotate { get; set; } = true;
        public DateTimeOffset RefreshExp { get; set; } = DateTimeOffset.UtcNow.AddDays(7);
        public string CurrentRefresh { get; private set; } = "";
        public string ValidAccess { get; private set; } = "";
        public int RefreshPosts { get; private set; }
        public int ProfileOk { get; private set; }
        public int Unauthorized { get; private set; }

        public RotatingServer()
        {
            Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(_ =>
            {
                lock (_gate)
                {
                    ValidAccess = NewAccess();
                    CurrentRefresh = Jwt(RefreshExp, DateTimeOffset.UtcNow);
                    return Ok("{\"access\":\"" + ValidAccess + "\",\"refresh\":\"" + CurrentRefresh + "\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"role\":\"owner\"}}");
                }
            }));
            Server.Given(Request.Create().WithPath("/api/users/auth/refresh/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req =>
            {
                Thread.Sleep(150); // обновление идёт заметное время — параллельные запросы успевают встать в очередь
                lock (_gate)
                {
                    RefreshPosts++;
                    using var doc = JsonDocument.Parse(req.Body ?? "{}");
                    var sent = doc.RootElement.TryGetProperty("refresh", out var r) ? r.GetString() : null;
                    if (sent != CurrentRefresh)
                        return Status(401, "{\"detail\":\"Token is invalid or expired\"}");
                    ValidAccess = NewAccess();
                    if (!Rotate)
                        return Ok("{\"access\":\"" + ValidAccess + "\"}");
                    CurrentRefresh = Jwt(RefreshExp, DateTimeOffset.UtcNow);
                    return Ok("{\"access\":\"" + ValidAccess + "\",\"refresh\":\"" + CurrentRefresh + "\"}");
                }
            }));
            Server.Given(Request.Create().WithPath("/api/users/profile/").UsingMethod("GET")).RespondWith(Response.Create().WithCallback(req =>
            {
                lock (_gate)
                {
                    var auth = req.Headers is not null && req.Headers.TryGetValue("Authorization", out var h) ? h.ToString() : "";
                    if (auth != "Bearer " + ValidAccess)
                    {
                        Unauthorized++;
                        return Status(401, "{\"detail\":\"Given token not valid\"}");
                    }
                    ProfileOk++;
                    return Ok("{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"owner\",\"company\":{\"id\":\"c1\",\"name\":\"Тест\"}}");
                }
            }));
        }

        private string NewAccess() => Jwt(DateTimeOffset.UtcNow.AddMinutes(15), null, "acc" + (++_n));

        /// <summary>Сервер «забыл» выданный access (прошло 15 минут): клиентский токен теперь даёт 401.</summary>
        public void ExpireAccess() { lock (_gate) ValidAccess = "expired-on-server"; }

        private static IResponseMessage Ok(string body) => Status(200, body);

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
        public int Saves { get; private set; }

        public Task SaveSessionAsync(UserSession session)
        {
            lock (this) { Saved = session; Saves++; }
            return Task.CompletedTask;
        }

        public Task<UserSession?> LoadSessionAsync() { lock (this) return Task.FromResult(Saved); }
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { lock (this) Saved = null; return Task.CompletedTask; }
    }

    private static NurMarketApiClient NewClient(RotatingServer server) => new(new AppSettings { ApiBaseUrl = server.Url });

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task Twenty_parallel_requests_with_a_stale_access_token_make_exactly_one_refresh_and_keep_the_session()
    {
        using var server = new RotatingServer();
        var api = NewClient(server);
        await api.LoginAsync("cashier@test.kg", "12345678");
        var refreshBefore = api.RefreshToken;
        server.ExpireAccess();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try { return (await api.GetProfileAsync()).GetProperty("email").GetString(); }
            catch (Exception ex) { return "ERR " + ex.GetType().Name + ": " + ex.Message; }
        })));

        results.Should().AllBe("cashier@test.kg");
        server.RefreshPosts.Should().Be(1, "при ротации второй запрос по старому refresh дал бы 401 и ложную потерю входа");
        SessionExpiryMonitor.IsExpired.Should().BeFalse();
        api.RefreshToken.Should().NotBe(refreshBefore).And.Be(server.CurrentRefresh);
    }

    [Fact]
    public async Task Autologin_refresh_and_401_refresh_share_one_lock_and_one_request()
    {
        using var server = new RotatingServer();
        var api = NewClient(server);
        await api.LoginAsync("cashier@test.kg", "12345678");
        server.ExpireAccess();

        // автовход (RefreshAccessAsync) и обновление по 401 одновременно — раньше два разных замка, второй POST шёл по старому refresh
        var tasks = new List<Task<bool>>();
        for (var i = 0; i < 6; i++)
            tasks.Add(api.RefreshAccessAsync());
        tasks.Add(Task.Run(async () => { await api.GetProfileAsync(); return true; }));
        (await Task.WhenAll(tasks)).Should().AllBeEquivalentTo(true);

        server.RefreshPosts.Should().Be(1);
        SessionExpiryMonitor.IsExpired.Should().BeFalse();
    }

    [Fact]
    public async Task Rotated_refresh_reaches_auth_dat_password_login_time_stays_and_autologin_after_restart_works()
    {
        using var server = new RotatingServer { RefreshExp = DateTimeOffset.UtcNow.AddHours(10) };
        var storage = new MemorySessionStore();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), storage);

        var login = await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: true);
        login.IsSuccess.Should().BeTrue();
        var firstRefresh = storage.Saved!.RefreshToken;
        var passwordLoginAt = storage.Saved.PasswordLoginAt;
        passwordLoginAt.Should().NotBeNull("время входа по паролю запомнено");
        RefreshExpiryWatcher.Current.IsWarning.Should().BeTrue("до конца входа 10 ч");

        // сервер продлевает вход ротацией: новый refresh живёт 7 суток
        server.RefreshExp = DateTimeOffset.UtcNow.AddDays(7);
        server.ExpireAccess();
        (await api.GetProfileAsync()).GetProperty("email").GetString().Should().Be("cashier@test.kg");

        await WaitUntilAsync(() => storage.Saved!.RefreshToken == server.CurrentRefresh, "auth.dat получает последний refresh");
        storage.Saved!.RefreshToken.Should().NotBe(firstRefresh);
        storage.Saved.PasswordLoginAt.Should().Be(passwordLoginAt, "ротация не меняет время входа по паролю");
        RefreshExpiryWatcher.Current.IsWarning.Should().BeFalse("срок пересчитан по новому refresh — предупреждение исчезло");
        SessionExpiryMonitor.IsExpired.Should().BeFalse();

        // перезапуск кассы: новый клиент и новый сервис из того же auth.dat; access на сервере уже «просрочен»
        server.ExpireAccess();
        var postsBefore = server.RefreshPosts;
        var api2 = NewClient(server);
        var auth2 = new OnlineOfflineAuthenticationService(new AuthApiService(api2), storage);
        var restored = await auth2.AutoLoginAsync();

        restored.IsSuccess.Should().BeTrue("автовход идёт по новому refresh, а не по устаревшему");
        server.RefreshPosts.Should().BeGreaterThan(postsBefore);
        SessionExpiryMonitor.IsExpired.Should().BeFalse();
        storage.Saved.Should().NotBeNull("сохранённый вход не стёрт");
    }

    [Fact]
    public async Task Server_without_rotation_works_as_before()
    {
        using var server = new RotatingServer { Rotate = false };
        var storage = new MemorySessionStore();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), storage);
        (await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: true)).IsSuccess.Should().BeTrue();
        var refresh = api.RefreshToken;
        server.ExpireAccess();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () => (await api.GetProfileAsync()).GetProperty("email").GetString())));

        results.Should().AllBe("cashier@test.kg");
        server.RefreshPosts.Should().Be(1);
        api.RefreshToken.Should().Be(refresh, "ответ без поля refresh — токен прежний");
        await WaitUntilAsync(() => storage.Saves >= 2, "обновлённый access сохранён");
        storage.Saved!.RefreshToken.Should().Be(refresh);
        SessionExpiryMonitor.IsExpired.Should().BeFalse();
    }
}
