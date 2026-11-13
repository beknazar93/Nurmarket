using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Wave1;
using NurMarketKassa.ViewModels;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Util;

namespace NurMarketKassa.Tests.Lifecycle;

/// <summary>2026-11-13 (ответ Умара 13.11, п. 5): слоты устройств. 409 device_slot_taken → вопрос → повтор входа с replace_device; 401 session_revoked на работающей кассе → потеря входа с причиной
/// (окно «Вход выполнен на другом устройстве»), профиль и сохранённый вход не стираются; заголовок X-NurMarket-Client для Android. Сеть — только поддельный сервер (WireMock).</summary>
[Collection("SessionExpiry")]
public sealed class DeviceSlotTests : IDisposable
{
    public DeviceSlotTests() => SessionExpiryMonitor.ResetForTests();

    public void Dispose() => SessionExpiryMonitor.ResetForTests();

    private const string SlotBody = "{\"code\":\"device_slot_taken\",\"slot\":\"desktop_pos\",\"device\":{\"id\":\"d1\",\"name\":\"Касса у входа\",\"platform\":\"windows\",\"last_seen_at\":\"2026-11-13T08:30:00Z\"}}";
    private const string RevokedBody = "{\"code\":\"session_revoked\",\"reason\":\"device_replaced\",\"by_device_name\":\"Касса 2\",\"at\":\"2026-11-13T09:00:00Z\"}";

    /// <summary>Сервер со слотом: вход без replace_device — 409, с replace_device:true — 200 (слот освобождён). После замены профиль и обновление токена отвечают 401 session_revoked, если включено Revoked.</summary>
    private sealed class SlotServer : IDisposable
    {
        public WireMockServer Server { get; } = WireMockServer.Start();
        public string Url => Server.Url!;
        public bool SlotBusy { get; set; } = true;
        public bool Revoked { get; set; }
        public List<JsonElement> LoginBodies { get; } = new();

        public SlotServer()
        {
            Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req =>
            {
                using var doc = JsonDocument.Parse(req.Body ?? "{}");
                var body = doc.RootElement.Clone();
                lock (LoginBodies) LoginBodies.Add(body);
                var replace = body.TryGetProperty("replace_device", out var r) && r.ValueKind == JsonValueKind.True;
                if (SlotBusy && !replace)
                    return Status(409, SlotBody);
                SlotBusy = true;
                return Status(200, "{\"access\":\"" + RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddMinutes(15), null, "acc1") + "\",\"refresh\":\"" + RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow) + "\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"owner\"}}");
            }));
            Server.Given(Request.Create().WithPath("/api/users/auth/refresh/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(_ =>
                Revoked ? Status(401, RevokedBody) : Status(200, "{\"access\":\"" + RefreshExpiryTests.Jwt(DateTimeOffset.UtcNow.AddMinutes(15), null, "acc2") + "\"}")));
            Server.Given(Request.Create().WithPath("/api/users/profile/").UsingMethod("GET")).RespondWith(Response.Create().WithCallback(_ =>
                Revoked ? Status(401, RevokedBody)
                        : Status(200, "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"owner\",\"company\":{\"id\":\"c1\",\"name\":\"Тест\"}}")));
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

    private static NurMarketApiClient NewClient(SlotServer s) => new(new AppSettings { ApiBaseUrl = s.Url });

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }

    // ---------------------------------------------------------------- 1. вход: 409 → replace_device

    [Fact]
    public async Task Login_409_device_slot_taken_gives_DeviceSlotTaken_with_the_other_device_and_no_tokens()
    {
        using var server = new SlotServer();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), new MemorySessionStore());

        var result = await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: true);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(AuthenticationFailure.DeviceSlotTaken);
        result.DeviceSlot.Should().NotBeNull();
        result.DeviceSlot!.Slot.Should().Be("desktop_pos");
        result.DeviceSlot.DeviceName.Should().Be("Касса у входа");
        api.AccessToken.Should().BeNull("вход не выполнен — чужую кассу молча не отключаем");
        // тело входа: тип устройства, ключ установки и имя; replace_device без согласия кассира не шлётся
        var body = server.LoginBodies.Single();
        body.GetProperty("client_type").GetString().Should().Be("desktop_pos");
        body.GetProperty("device_key").GetString().Should().MatchRegex("^[A-Za-z0-9_-]{8,64}$");
        body.TryGetProperty("replace_device", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Repeat_login_with_replaceDevice_sends_replace_device_true_and_signs_in()
    {
        using var server = new SlotServer();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), new MemorySessionStore());

        (await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: false)).Failure.Should().Be(AuthenticationFailure.DeviceSlotTaken);
        var again = await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: false, replaceDevice: true);

        again.IsSuccess.Should().BeTrue();
        api.AccessToken.Should().NotBeNullOrEmpty();
        server.LoginBodies.Should().HaveCount(2);
        server.LoginBodies[1].GetProperty("replace_device").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Other_409_is_not_a_slot_question()
    {
        using var server = new SlotServer();
        server.Server.Reset();
        server.Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(409).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"something_else\"}"));
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(NewClient(server)), new MemorySessionStore());

        var result = await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: false);

        result.Failure.Should().NotBe(AuthenticationFailure.DeviceSlotTaken);
    }

    // ---------------------------------------------------------------- 2. окно входа: вопрос и повтор (через модель окна)

    private static AuthViewModel NewVm(SlotServer server)
    {
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(NewClient(server)), new MemorySessionStore());
        return new AuthViewModel(auth, ScenarioSupport.Null<IAutonomousAuthService>(), ScenarioSupport.Null<IAppSession>())
        {
            Username = "cashier@test.kg",
            Password = "12345678",
        };
    }

    [Fact]
    public async Task Viewmodel_asks_then_repeats_login_with_replace_device_when_cashier_agrees()
    {
        using var server = new SlotServer();
        var vm = NewVm(server);
        DeviceSlotInfo? asked = null;
        vm.ConfirmReplaceDevice = info => { asked = info; return Task.FromResult(true); };
        var signedIn = false;
        vm.LoginSuccess += () => { signedIn = true; return Task.CompletedTask; };

        vm.LoginCommand.Execute(null);
        await WaitUntilAsync(() => signedIn, "после согласия вход выполнен");

        asked.Should().NotBeNull();
        asked!.DeviceName.Should().Be("Касса у входа");
        server.LoginBodies.Should().HaveCount(2);
        server.LoginBodies[1].GetProperty("replace_device").GetBoolean().Should().BeTrue();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Viewmodel_cancel_keeps_the_other_till_and_shows_the_reason()
    {
        using var server = new SlotServer();
        var vm = NewVm(server);
        vm.ConfirmReplaceDevice = _ => Task.FromResult(false);
        var signedIn = false;
        vm.LoginSuccess += () => { signedIn = true; return Task.CompletedTask; };

        vm.LoginCommand.Execute(null);
        await WaitUntilAsync(() => vm.HasError, "причина показана");

        signedIn.Should().BeFalse();
        server.LoginBodies.Should().HaveCount(1, "повторного входа с заменой не было — чужая касса осталась");
        vm.ErrorMessage.Should().Contain("другая касса");
        vm.ErrorMessage.Should().Contain("поддержка");
    }

    // ---------------------------------------------------------------- тексты окна

    [Fact]
    public void Prompt_texts_mention_the_other_till_and_support_hint_in_every_language()
    {
        var info = new DeviceSlotInfo("android_pos", "Планшет 1", "android", "2026-11-13T08:30:00Z");
        var saved = UserPreferences.Instance.Language;
        try
        {
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                UserPreferences.Instance.Language = lang;
                var text = DeviceSlotPrompt.Message(info);
                text.Should().Contain("Планшет 1", lang.ToString());
                text.Should().NotBeNullOrWhiteSpace();
                DeviceSlotPrompt.ConfirmText.Should().NotBeNullOrWhiteSpace();
                DeviceSlotPrompt.RevokedMessage("Касса 2", "2026-11-13T09:00:00Z").Should().Contain("Касса 2", lang.ToString());
            }
            UserPreferences.Instance.Language = AppLanguage.Russian;
            DeviceSlotPrompt.Message(info).Should().Contain("касса на Android").And.Contain("поддержка");
            DeviceSlotPrompt.ConfirmText.Should().Be("Войти и отключить другую");
        }
        finally { UserPreferences.Instance.Language = saved; }
    }

    // ---------------------------------------------------------------- 3. 401 session_revoked на работающей кассе

    [Fact]
    public async Task Session_revoked_on_a_working_till_expires_the_session_with_the_reason_and_keeps_profile_and_saved_login()
    {
        using var server = new SlotServer { SlotBusy = false };
        var storage = new MemorySessionStore();
        var api = NewClient(server);
        var auth = new OnlineOfflineAuthenticationService(new AuthApiService(api), storage);
        (await auth.LoginAsync("cashier@test.kg", "12345678", rememberMe: true)).IsSuccess.Should().BeTrue();
        storage.Saved.Should().NotBeNull();
        var raised = 0;
        void OnExpired() => Interlocked.Increment(ref raised);
        SessionExpiryMonitor.Expired += OnExpired;
        try
        {
            server.Revoked = true; // другое устройство вошло с заменой: наш вход закрыт, access ещё «жив» по времени, но сервер отвечает 401 session_revoked

            var act = () => api.GetProfileAsync();
            await act.Should().ThrowAsync<ApiException>();

            SessionExpiryMonitor.IsExpired.Should().BeTrue();
            SessionExpiryMonitor.Revoked.Should().NotBeNull();
            SessionExpiryMonitor.Revoked!.ByDeviceName.Should().Be("Касса 2");
            SessionExpiryMonitor.Revoked.At.Should().Be("2026-11-13T09:00:00Z");
            raised.Should().Be(1, "окно «Вход выполнен на другом устройстве» объявляется один раз");
            api.AccessToken.Should().BeNull();
            api.UserPayload.ValueKind.Should().Be(JsonValueKind.Object, "профиль (роль, права) остаётся — как при истёкшем входе");
            storage.Saved.Should().NotBeNull("сохранённый вход не стирается: очередь продаж и корзины ждут повторного входа, а не пропадают");
        }
        finally { SessionExpiryMonitor.Expired -= OnExpired; }
    }

    [Fact]
    public async Task Ordinary_401_without_the_code_is_not_a_revoked_session()
    {
        using var server = new SlotServer { SlotBusy = false };
        var api = NewClient(server);
        await api.LoginAsync("cashier@test.kg", "12345678");
        server.Server.Reset();
        server.Server.Given(Request.Create().WithPath("/api/users/profile/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"Given token not valid\"}"));
        server.Server.Given(Request.Create().WithPath("/api/users/auth/refresh/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"Token is invalid or expired\"}"));

        var act = () => api.GetProfileAsync();
        await act.Should().ThrowAsync<ApiException>();

        SessionExpiryMonitor.IsExpired.Should().BeTrue("refresh отклонён — вход потерян, как раньше");
        SessionExpiryMonitor.Revoked.Should().BeNull("причина «вошли на другом устройстве» не выдумывается");
    }

    // ---------------------------------------------------------------- 4. заголовок X-NurMarket-Client

    [Theory]
    [InlineData(true, false, "1.17.86", "android-pos/1.17.86")]
    [InlineData(false, false, "1.17.86", "kassa/1.17.86")]
    [InlineData(false, true, "1.17.86", "kassa/1.17.86 (linux)")]
    public void Client_header_is_android_pos_only_on_android(bool android, bool linux, string version, string expected) =>
        NurMarketApiClient.BuildClientHeader(android, linux, version).Should().Be(expected);

    [Fact]
    public async Task Windows_client_sends_kassa_header_on_the_wire()
    {
        using var server = new SlotServer { SlotBusy = false };
        var api = NewClient(server);
        await api.LoginAsync("cashier@test.kg", "12345678");

        var entry = server.Server.LogEntries.First(e => e.RequestMessage.Path.Contains("/auth/login/"));
        entry.RequestMessage.Headers!["X-NurMarket-Client"].First().Should().StartWith("kassa/");
        NurMarketApiClient.ClientTypeForLogin().Should().Be("desktop_pos");
    }
}
