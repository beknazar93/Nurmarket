using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-11, владелец: «доступы даются из сайта и из программы владельца — синхронизацию с сайтом, и маркетплейс тоже».
/// Фоновая проверка AccessSync: изменение прав на сервере применяется без перезапуска (PermissionsChanged, HasPermission); серверный отзыв платной функции
/// снимает доступ, а функция, открытая ключом, остаётся; проверка не идёт при открытом чеке; события чаще раза в 20 с не дают лишних запросов;
/// PermissionRecheck (перечитывание при отказе) работает как раньше. Сервер — WireMock.</summary>
[Collection("CloudSyncSerial")]
public sealed class AccessSyncTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();
    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;
    private readonly string? _previousKey = UserPreferences.Instance.LastDataAccountKey;
    private readonly bool _previousLabelKey = UserPreferences.Instance.IsKeyUnlocked("labeleditor");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-as-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private NurMarketApiClient? _api;
    private IAuthApiService? _auth;

    public AccessSyncTests()
    {
        Directory.CreateDirectory(_dir);
        SessionExpiryMonitor.ResetForTests();
        PermissionRecheck.ResetForTests();
        ServerOutageMonitor.ResetForTests();
        AccessSync.ResetForTests();
        SyncCenter.OpenCheck = false;
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, new CompanyDto { Id = "c-test" });
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("c-test");
        ServerAddons.DirOverride = _dir;
        ServerAddons.NowUtcOverride = () => _now;
        ServerAddons.ApiProvider = () => _api;
        ServerAddons.ResetMemory();
    }

    public void Dispose()
    {
        AccessSync.ResetForTests();
        AccessSync.MarkStartedForTests(false);
        PermissionRecheck.Attach(() => null, () => null);
        PermissionRecheck.ResetForTests();
        SessionExpiryMonitor.ResetForTests();
        SyncCenter.OpenCheck = false;
        UserPreferences.Instance.LabelEditorUnlocked = _previousLabelKey;
        ServerAddons.ApiProvider = null;
        ServerAddons.NowUtcOverride = null;
        ServerAddons.DirOverride = null;
        ServerAddons.ResetMemory();
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, _previousCompany);
        UserPreferences.Instance.LastDataAccountKey = _previousKey ?? "";
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void StubProfile(bool warehouse) =>
        _server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u7\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true"
            + (warehouse ? ",\"can_view_products\":true" : "") + ",\"company\":{\"id\":\"c-test\",\"name\":\"Тест\"}}");

    private void StubAddons(params string[] activeCodes) =>
        _server.Json("GET", "/api/users/company/addons/", 200,
            "[" + string.Join(",", activeCodes.Select(c => "{\"code\":\"" + c + "\",\"active\":true,\"until\":null}")) + "]");

    private void Restub(bool warehouse, params string[] addons)
    {
        _server.Server.ResetMappings();
        _server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u7\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true}}");
        StubProfile(warehouse);
        StubAddons(addons);
    }

    private async Task LoginAsync()
    {
        _api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await _api.LoginAsync("seller@test.kg", "12345678");
        _auth = new AuthApiService(_api);
        PermissionRecheck.Attach(() => _auth, () => new NoAutonomous());
    }

    private int Requests(string path) => _server.Server.LogEntries.Count(e => e.RequestMessage?.Path == path);

    [Fact]
    public async Task Rights_changed_on_site_apply_without_restart()
    {
        Restub(warehouse: false);
        await LoginAsync();
        var permissions = new PermissionService(_auth!, new NoAutonomous());
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        permissions.HasPermission(PosPermissions.ViewProducts).Should().BeFalse("на сайте «Склад» не выдан");

        var changed = 0;
        void OnChanged() => Interlocked.Increment(ref changed);
        PermissionRecheck.PermissionsChanged += OnChanged;
        try
        {
            Restub(warehouse: true); // владелец выдал «Склад» на сайте
            (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

            permissions.HasPermission(PosPermissions.ViewProducts).Should().BeTrue("доступ применяется без перезапуска и повторного входа");
            changed.Should().Be(1, "окна пересобирают меню по событию PermissionsChanged");

            (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
            changed.Should().Be(1, "права не менялись — повторного события (и плашки) нет");
        }
        finally
        {
            PermissionRecheck.PermissionsChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task Server_revoke_removes_addon_but_key_purchase_stays()
    {
        Restub(warehouse: false, "kassa-loyalty", "kassa-labeleditor");
        await LoginAsync();
        UserPreferences.Instance.LabelEditorUnlocked = true; // редактор этикеток ещё и открыт КЛЮЧОМ на этой кассе
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        ServerAddons.IsGranted("loyalty").Should().BeTrue();

        Restub(warehouse: false); // на сайте обе функции отключили
        _now = _now.AddSeconds(25);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

        ServerAddons.IsGranted("loyalty").Should().BeFalse("серверный отзыв снимает доступ");
        ServerAddons.IsGranted("labeleditor").Should().BeFalse();
        UserPreferences.Instance.LabelEditorUnlocked.Should().BeTrue("покупка по ключу не отзывается молча — ключ действует до своей даты");
    }

    [Fact]
    public async Task No_check_while_check_is_open_and_events_are_rate_limited()
    {
        Restub(warehouse: false);
        await LoginAsync();
        var atStart = Requests("/api/users/profile/");
        SyncCenter.OpenCheck = true;
        (await AccessSync.CheckNowAsync("timer")).Should().BeFalse("во время работы с открытым чеком/оплаты проверка не идёт");
        Requests("/api/users/profile/").Should().Be(atStart);
        SyncCenter.OpenCheck = false;

        AccessSync.MarkStartedForTests(true);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        var profileCalls = Requests("/api/users/profile/");
        profileCalls.Should().Be(atStart + 1, "одна проверка — один запрос профиля");
        for (var i = 0; i < 10; i++)
            AccessSync.Kick("focus");
        AccessSync.TakeWakeForTests().Should().BeFalse("события чаще раза в 20 с после проверки запросов не вызывают");
        Requests("/api/users/profile/").Should().Be(profileCalls);
        AccessSync.Interval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60), "фоновый такт — не чаще раза в минуту");
        AccessSync.EventMinGap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Kick_after_gap_wakes_the_check()
    {
        AccessSync.MarkStartedForTests(true);
        AccessSync.Kick("focus");
        AccessSync.TakeWakeForTests().Should().BeTrue("окно вернулось — проверка сразу, а не через такт");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PermissionRecheck_on_denial_still_works_and_raises_change()
    {
        Restub(warehouse: false);
        await LoginAsync();
        await PermissionRecheck.RefreshProfileAsync(_auth!, new NoAutonomous(), "вход", TimeSpan.FromSeconds(5));
        var changed = 0;
        void OnChanged() => changed++;
        PermissionRecheck.PermissionsChanged += OnChanged;
        try
        {
            Restub(warehouse: true);
            (await PermissionRecheck.RefreshProfileAsync(_auth!, new NoAutonomous(), "отказ по правам", TimeSpan.FromSeconds(5))).Should().BeTrue();
            new PermissionService(_auth!, new NoAutonomous()).HasPermission(PosPermissions.ViewProducts).Should().BeTrue();
            changed.Should().Be(1);
            PermissionRecheck.Describe(PosPermissions.ViewProducts, _auth!.UserPayload).Should().Contain("can_view_products = true");
        }
        finally
        {
            PermissionRecheck.PermissionsChanged -= OnChanged;
        }
    }

    // ───────────── 2026-11-13: ETag / If-None-Match у users/profile/ и users/company/addons/ (сервер «ev1311») ─────────────

    /// <summary>Как настоящий сервер: слабый ETag W/"…" в ответе; If-None-Match без W/ (так касса и шлёт) → 304 без тела, иначе 200 с телом и ETag.</summary>
    private void StubEtagged(string path, string tag, string body)
    {
        _server.Server.Given(Request.Create().WithPath(path).UsingGet().WithHeader("If-None-Match", "\"" + tag + "\"")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", "W/\"" + tag + "\""));
        _server.Server.Given(Request.Create().WithPath(path).UsingGet()).AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("ETag", "W/\"" + tag + "\"").WithBody(body));
    }

    private static string ProfileBody(bool warehouse) =>
        "{\"id\":\"u7\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true"
        + (warehouse ? ",\"can_view_products\":true" : "") + ",\"company\":{\"id\":\"c-test\",\"name\":\"Тест\"}}";

    private static string AddonsBody(params string[] activeCodes) =>
        "[" + string.Join(",", activeCodes.Select(c => "{\"code\":\"" + c + "\",\"active\":true,\"until\":null}")) + "]";

    private void RestubEtag(string profileTag, bool warehouse, string addonsTag, params string[] addons)
    {
        _server.Server.ResetMappings();
        _server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u7\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true}}");
        StubEtagged("/api/users/profile/", profileTag, ProfileBody(warehouse));
        StubEtagged("/api/users/company/addons/", addonsTag, AddonsBody(addons));
    }

    private IReadOnlyList<string?> IfNoneMatchOf(string path) =>
        _server.Server.LogEntries.Where(e => e.RequestMessage?.Path == path)
            .Select(e => e.RequestMessage!.Headers is { } h && h.TryGetValue("If-None-Match", out var v) ? v.ToString() : null).ToList();

    [Fact]
    public async Task Profile_304_changes_no_rights_and_is_not_applied()
    {
        RestubEtag("p1", warehouse: false, "a1");
        await LoginAsync();
        var permissions = new PermissionService(_auth!, new NoAutonomous());
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        PermissionRecheck.ProfileEtagSupported.Should().BeTrue("сервер отдал ETag профиля");
        IfNoneMatchOf("/api/users/profile/").Last().Should().BeNull("первый запрос безусловный");

        // На сервере теперь «Склад» выдан, но ETag в заглушке тот же, и 304 приходит по If-None-Match: тело (200) не читается вовсе — права остались прежними.
        RestubEtag("p1", warehouse: true, "a1");
        var changed = 0;
        void OnChanged() => Interlocked.Increment(ref changed);
        PermissionRecheck.PermissionsChanged += OnChanged;
        try
        {
            _now = _now.AddSeconds(25);
            (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
            IfNoneMatchOf("/api/users/profile/").Last().Should().Be("\"p1\"", "касса шлёт ETag прошлого ответа без W/ (иначе сервер всегда 200)");
            permissions.HasPermission(PosPermissions.ViewProducts).Should().BeFalse("304 — ничего не меняется и тело не разбирается");
            changed.Should().Be(0);
            PermissionRecheck.LiveProfileAt.Should().NotBeNull("сервер подтвердил профиль — права «живые»");
        }
        finally
        {
            PermissionRecheck.PermissionsChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task Profile_200_with_new_etag_applies_and_next_request_uses_it()
    {
        RestubEtag("p1", warehouse: false, "a1");
        await LoginAsync();
        var permissions = new PermissionService(_auth!, new NoAutonomous());
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

        RestubEtag("p2", warehouse: true, "a1"); // права изменились: ETag другой, 304 по старому не придёт
        var changed = 0;
        void OnChanged() => Interlocked.Increment(ref changed);
        PermissionRecheck.PermissionsChanged += OnChanged;
        try
        {
            _now = _now.AddSeconds(25);
            (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
            IfNoneMatchOf("/api/users/profile/").Last().Should().Be("\"p1\"");
            permissions.HasPermission(PosPermissions.ViewProducts).Should().BeTrue("200 с новым ETag применяется");
            changed.Should().Be(1);

            _now = _now.AddSeconds(25);
            (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
            IfNoneMatchOf("/api/users/profile/").Last().Should().Be("\"p2\"", "дальше — по новому ETag");
            changed.Should().Be(1, "304 событий не поднимает");
        }
        finally
        {
            PermissionRecheck.PermissionsChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task Server_without_etag_works_as_before_and_keeps_the_slow_tick()
    {
        Restub(warehouse: false, "kassa-loyalty"); // старый сервер: ETag нет
        await LoginAsync();
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        _now = _now.AddSeconds(25);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

        IfNoneMatchOf("/api/users/profile/").Should().OnlyContain(v => v == null, "нет ETag — запросы безусловные, как раньше");
        IfNoneMatchOf("/api/users/company/addons/").Should().OnlyContain(v => v == null);
        PermissionRecheck.ProfileEtagSupported.Should().BeFalse();
        ServerAddons.EtagSupported.Should().BeFalse();
        AccessSync.CurrentInterval().Should().Be(AccessSync.Interval, "без ETag такт прежний (90 с)");
        ServerAddons.IsGranted("loyalty").Should().BeTrue();
    }

    [Fact]
    public async Task Etag_on_both_requests_shortens_the_tick_to_30_60_seconds()
    {
        RestubEtag("p1", warehouse: false, "a1", "kassa-loyalty");
        await LoginAsync();
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

        ServerAddons.EtagSupported.Should().BeTrue();
        AccessSync.CurrentInterval().Should().Be(AccessSync.FastInterval);
        AccessSync.FastInterval.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Addons_304_keeps_list_and_200_with_new_etag_applies()
    {
        RestubEtag("p1", warehouse: false, "a1", "kassa-loyalty");
        await LoginAsync();
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        ServerAddons.IsGranted("loyalty").Should().BeTrue();

        // тот же ETag → 304; в теле 200 функции уже нет, но тело не читается — список остаётся
        RestubEtag("p1", warehouse: false, "a1");
        _now = _now.AddSeconds(25);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        IfNoneMatchOf("/api/users/company/addons/").Last().Should().Be("\"a1\"");
        ServerAddons.IsGranted("loyalty").Should().BeTrue("304 — список не изменился");

        // ETag другой → 200 → применяется (функцию отключили на сайте)
        RestubEtag("p1", warehouse: false, "a2");
        _now = _now.AddSeconds(25);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        ServerAddons.IsGranted("loyalty").Should().BeFalse("200 с новым ETag применён");
        _now = _now.AddSeconds(25);
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();
        IfNoneMatchOf("/api/users/company/addons/").Last().Should().Be("\"a2\"");
    }

    [Fact]
    public async Task Addon_source_is_parsed_and_unknown_value_has_no_label()
    {
        _server.Server.ResetMappings();
        _server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u7\",\"email\":\"seller@test.kg\",\"role\":\"salesperson\"}}");
        StubProfile(warehouse: false);
        _server.Json("GET", "/api/users/company/addons/", 200,
            "[{\"code\":\"kassa-loyalty\",\"active\":true,\"until\":null,\"source\":\"key\"},"
            + "{\"code\":\"kassa-timesheet\",\"active\":true,\"until\":\"2026-12-10\",\"source\":\"trial\"},"
            + "{\"code\":\"kassa-analytics\",\"active\":true,\"until\":null,\"source\":\"support\"},"
            + "{\"code\":\"kassa-scales\",\"active\":true,\"until\":null,\"source\":\"gift\"},"
            + "{\"code\":\"kassa-pricetag\",\"active\":true,\"until\":null,\"source\":\"promo\"},"
            + "{\"code\":\"kassa-export\",\"active\":true,\"until\":null},"
            + "{\"code\":\"whatsapp\",\"active\":true,\"until\":\"2026-12-10\",\"source\":\"promo\"}]");
        await LoginAsync();
        (await AccessSync.CheckNowAsync("timer")).Should().BeTrue();

        ServerAddons.GrantedSource("loyalty").Should().Be("key");
        ServerAddons.GrantedSource("timesheet").Should().Be("trial");
        ServerAddons.GrantedSource("analytics").Should().Be("support");
        ServerAddons.GrantedSource("pricetag").Should().Be("promo", "2026-11-13: «promo» — функция включена промокодом");
        ServerAddons.GrantedSource("scales").Should().BeNull("«gift» — неизвестное значение: подписи нет");
        ServerAddons.GrantedSource("export").Should().BeNull("старый сервер без поля source");
        ServerAddons.GrantedSource("bulktag").Should().BeNull("функция не включена");
        ServerAddons.SourceLabel("key").Should().NotBeEmpty();
        ServerAddons.SourceLabel("trial").Should().NotBeEmpty().And.NotBe(ServerAddons.SourceLabel("key"));
        ServerAddons.SourceLabel("support").Should().NotBeEmpty().And.NotBe(ServerAddons.SourceLabel("trial"));
        ServerAddons.SourceLabel("promo").Should().NotBeEmpty().And.NotBe(ServerAddons.SourceLabel("support"));
        ServerAddons.SourceLabel("gift").Should().BeEmpty();
        ServerAddons.SourceLabel(null).Should().BeEmpty();
        ServerAddons.IsGranted("scales").Should().BeTrue("неизвестный источник доступ не отменяет");
    }

    private sealed class NoAutonomous : IAutonomousAuthService
    {
        public bool IsActivated => false;
        public bool HasLocalAccount => false;
        public bool IsCurrentSessionAutonomous => false;
        public Task<(bool Success, string? Error)> ActivateAsync(string activationKey, CancellationToken ct = default) => throw new NotSupportedException();
        public (bool Success, string? Error) CreateLocalAccount(string email, string password, string? displayName) => throw new NotSupportedException();
        public (bool Success, string? Error, string? DisplayName) LoginLocal(string email, string password) => throw new NotSupportedException();
        public (bool Success, string? Email, string? DisplayName) TryAutoResume() => throw new NotSupportedException();
        public void EndAutonomousSession() => throw new NotSupportedException();
        public void MarkCatalogPreservedForNextLogin() => throw new NotSupportedException();
    }
}
