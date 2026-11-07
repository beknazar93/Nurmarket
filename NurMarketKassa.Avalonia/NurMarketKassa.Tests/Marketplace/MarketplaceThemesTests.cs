using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Marketplace;

/// <summary>2026-11-06 («темы с сайта»): каталог тем и видов кассы с сервера, скачивание пакета, проверка sha256/размера, установка в «свои темы» (здесь — память), автоустановка купленного.
/// Сервер — WireMock; папки данных — временные (dirOverride), настоящие данные кассы не трогаются. Пакет — только данные: ничего не исполняется.</summary>
public sealed class MarketplaceThemesTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-mt-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private NurMarketApiClient? _api;

    public MarketplaceThemesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<NurMarketApiClient> ApiAsync()
    {
        if (_api is not null)
            return _api;
        _api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await _api.LoginAsync("cashier@test.kg", "12345678");
        return _api;
    }

    private MarketplaceThemesClient NewClient(bool supported = true, bool signedIn = true) =>
        new(() => signedIn ? _api : null, () => "c:test", (_, _) => Task.FromResult(supported), () => supported, () => _now, _dir);

    private sealed class MemorySink : IThemeSink
    {
        public readonly Dictionary<string, ThemePackageData> Themes = new();
        private int _next;

        public string Save(string? existingId, ThemePackageData data)
        {
            var id = existingId ?? "custom:t" + (++_next);
            Themes[id] = data;
            return id;
        }

        public bool Exists(string id) => Themes.ContainsKey(id);

        public void Delete(string id) => Themes.Remove(id);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string PackageJson(string name = "Закат", string accent = "#FF8800") => JsonSerializer.Serialize(new
    {
        format = "nurmarket-theme",
        version = 1,
        extra = new { whatever = 1 },
        theme = new
        {
            name,
            baseThemeId = "classic",
            light = new { accent, background = "#FFFFFF", text = "#111111", unknownColor = "#000000" },
            dark = new { accent = "#FFAA33", background = "#101010" },
            cornerRadius = 8,
            fontSize = 14,
            scripts = new[] { "calc.exe" }, // неизвестное поле: игнорируется, ничего не запускается
        },
    });

    private static string EntryJson(string id, string code, bool entitled, string? sha, long size, string kind = "theme", string? minApp = null, bool free = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["code"] = code, ["kind"] = kind,
            ["name"] = new { ru = "Тема " + id, en = "Theme " + id },
            ["description"] = new { ru = "Описание. Вторая фраза." },
            ["preview_url"] = "https://example.com/p.png", ["version"] = "1.0.0",
            ["price"] = free ? null : 300, ["entitled"] = entitled || free,
            ["package_url"] = $"/api/main/marketplace/themes/{id}/package/", ["sha256"] = sha, ["size"] = size,
            ["min_app_version"] = minApp, ["accent"] = "#FF8800",
        });

    private void StubCatalog(params string[] entries) =>
        _server.Json("GET", "/api/main/marketplace/themes/", 200, "{\"count\":" + entries.Length + ",\"results\":[" + string.Join(",", entries) + "]}");

    private int Requests(string path) => _server.Server.LogEntries.Count(e => e.RequestMessage.Path == path);

    private MarketplaceThemeInstaller NewInstaller(MarketplaceThemesClient client, out MemorySink sink, out ServerThemeRegistry registry)
    {
        sink = new MemorySink();
        registry = new ServerThemeRegistry(_dir);
        return new MarketplaceThemeInstaller(client, registry, sink);
    }

    // ------------------------------------------------------------------ каталог → купленная тема скачивается и ставится

    [Fact]
    public async Task Catalog_then_entitled_theme_is_downloaded_verified_and_installed()
    {
        await ApiAsync();
        var pkg = PackageJson();
        var bytes = Encoding.UTF8.GetBytes(pkg);
        StubCatalog(EntryJson("t1", "theme-sunset", true, Sha(bytes), bytes.Length), EntryJson("t2", "theme-night", false, new string('a', 64), 100));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out var registry);

        var catalog = await client.RefreshAsync(force: true);

        catalog.Status.Should().Be(MarketplaceCatalogStatus.Loaded);
        client.Entries.Should().HaveCount(2);
        installer.StateOf(client.Find("theme-sunset")!).Should().Be(ThemeInstallState.NotInstalled);

        var result = await installer.InstallAsync(client.Find("theme-sunset")!);

        result.Status.Should().Be(ThemeInstallStatus.Installed);
        var data = sink.Themes[result.CustomId!];
        data.Name.Should().Be("Закат");
        data.Light["accent"].Should().Be("#FF8800");
        data.Light.Keys.Should().BeSubsetOf(MarketplaceThemePackage.ColorKeys, "неизвестные поля пакета отбрасываются");
        data.CornerRadius.Should().Be(8);
        installer.StateOf(client.Find("theme-sunset")!).Should().Be(ThemeInstallState.Installed);
        registry.Get("theme-sunset")!.CustomId.Should().Be(result.CustomId);
        // токен ушёл на сервер NurCRM
        _server.Server.LogEntries.Last(e => e.RequestMessage.Path == "/api/main/marketplace/themes/t1/package/").RequestMessage.Headers!["Authorization"].Single().Should().Be("Bearer test-access");
    }

    [Fact]
    public async Task Not_purchased_theme_gets_403_and_is_not_installed()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t2", "theme-night", false, new string('a', 64), 100));
        _server.Json("GET", "/api/main/marketplace/themes/t2/package/", 403, "{\"code\":\"not_purchased\"}");
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out _);
        await client.RefreshAsync(true);

        var result = await installer.InstallAsync(client.Find("theme-night")!);

        result.Status.Should().Be(ThemeInstallStatus.NotPurchased);
        sink.Themes.Should().BeEmpty();
    }

    [Fact]
    public async Task Damaged_hash_is_refused_and_nothing_is_installed()
    {
        await ApiAsync();
        var pkg = PackageJson();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('0', 64), Encoding.UTF8.GetByteCount(pkg)));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out var registry);
        await client.RefreshAsync(true);

        var result = await installer.InstallAsync(client.Find("theme-sunset")!);

        result.Status.Should().Be(ThemeInstallStatus.HashMismatch);
        sink.Themes.Should().BeEmpty();
        registry.All.Should().BeEmpty();
    }

    [Fact]
    public async Task Package_without_hash_is_refused()
    {
        await ApiAsync();
        var pkg = PackageJson();
        StubCatalog(EntryJson("t1", "theme-sunset", true, null, Encoding.UTF8.GetByteCount(pkg)));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out _);
        await client.RefreshAsync(true);

        (await installer.InstallAsync(client.Find("theme-sunset")!)).Status.Should().Be(ThemeInstallStatus.NoHash);
        sink.Themes.Should().BeEmpty();
    }

    [Fact]
    public async Task Package_over_5_MB_is_rejected_while_reading_and_by_declared_size()
    {
        await ApiAsync();
        var big = new string('x', (int)MarketplaceThemesClient.MaxPackageBytes + 10);
        // заявленный размер честный — но тело больше предела: читаем не дальше предела
        StubCatalog(EntryJson("t1", "theme-big", true, new string('1', 64), 1000), EntryJson("t3", "theme-huge", true, new string('2', 64), MarketplaceThemesClient.MaxPackageBytes + 1));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, big);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out _);
        await client.RefreshAsync(true);

        (await installer.InstallAsync(client.Find("theme-big")!)).Status.Should().Be(ThemeInstallStatus.TooLarge);
        (await installer.InstallAsync(client.Find("theme-huge")!)).Status.Should().Be(ThemeInstallStatus.TooLarge);
        Requests("/api/main/marketplace/themes/t3/package/").Should().Be(0, "по заявленному размеру пакет даже не запрашивается");
        sink.Themes.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_capability_old_behaviour_empty_catalog_and_no_requests()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('a', 64), 10));
        var client = NewClient(supported: false);

        var catalog = await client.RefreshAsync(true);

        catalog.Status.Should().Be(MarketplaceCatalogStatus.NotSupported);
        client.Entries.Should().BeEmpty();
        Requests("/api/main/marketplace/themes/").Should().Be(0);
    }

    [Fact]
    public async Task Server_404_on_catalog_means_not_supported_without_errors()
    {
        await ApiAsync();
        _server.Error("GET", "/api/main/marketplace/themes/", 404, "Not found");
        var client = NewClient();

        (await client.RefreshAsync(true)).Status.Should().Be(MarketplaceCatalogStatus.NotSupported);
        client.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_purchases_not_more_often_than_once_per_20_seconds()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('a', 64), 10));
        var client = NewClient();

        (await client.RefreshAsync(true)).Status.Should().Be(MarketplaceCatalogStatus.Loaded);
        _now = _now.AddSeconds(5);
        (await client.RefreshAsync(true)).Status.Should().Be(MarketplaceCatalogStatus.Throttled);
        _now = _now.AddSeconds(16);
        (await client.RefreshAsync(true)).Status.Should().Be(MarketplaceCatalogStatus.Loaded);

        Requests("/api/main/marketplace/themes/").Should().Be(2);
    }

    [Fact]
    public async Task Offline_catalog_is_taken_from_cache_after_restart()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('a', 64), 10));
        await NewClient().RefreshAsync(true);

        _server.Error("GET", "/api/main/marketplace/themes/", 500);
        var restarted = NewClient(); // как после перезапуска кассы: память пуста, файл на диске
        _now = _now.AddMinutes(10);
        var result = await restarted.RefreshAsync(true);

        result.Status.Should().Be(MarketplaceCatalogStatus.Offline);
        restarted.Entries.Should().ContainSingle().Which.Code.Should().Be("theme-sunset");
        var noSession = NewClient(signedIn: false);
        noSession.Entries.Should().ContainSingle("и без входа каталог берётся из кэша");
    }

    [Fact]
    public async Task Cache_of_another_account_is_not_shown()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('a', 64), 10));
        await NewClient().RefreshAsync(true);

        var other = new MarketplaceThemesClient(() => _api, () => "c:other", (_, _) => Task.FromResult(true), () => true, () => _now, _dir);

        other.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Newly_purchased_theme_is_reported_once_and_auto_install_skips_removed_ones()
    {
        await ApiAsync();
        var pkg = PackageJson();
        var bytes = Encoding.UTF8.GetBytes(pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out var registry);

        StubCatalog(EntryJson("t1", "theme-sunset", false, Sha(bytes), bytes.Length));
        (await client.RefreshAsync(true)).NewlyEntitled.Should().BeEmpty("первая загрузка — «новыми» ничего не считаем");

        _server.Server.ResetMappings();
        StubCatalog(EntryJson("t1", "theme-sunset", true, Sha(bytes), bytes.Length));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        _now = _now.AddSeconds(30);
        var second = await client.RefreshAsync(true);
        second.NewlyEntitled.Should().Equal("theme-sunset");

        var auto = await installer.AutoInstallEntitledAsync();
        auto.Should().ContainSingle().Which.Result.Ok.Should().BeTrue();
        sink.Themes.Should().HaveCount(1);

        // пользователь убрал тему — автоустановка её не возвращает
        installer.Remove("theme-sunset").Should().BeTrue();
        (await installer.AutoInstallEntitledAsync()).Should().BeEmpty();
        sink.Themes.Should().BeEmpty();
        registry.IsDismissed("theme-sunset").Should().BeTrue();

        // но по кнопке «Установить» ставится снова и снова считается «не убранной»
        (await installer.InstallAsync(client.Find("theme-sunset")!)).Ok.Should().BeTrue();
        registry.IsDismissed("theme-sunset").Should().BeFalse();
    }

    [Fact]
    public async Task Update_keeps_custom_id_and_view_kind_is_not_installable()
    {
        await ApiAsync();
        var pkg = PackageJson();
        var bytes = Encoding.UTF8.GetBytes(pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out _);
        StubCatalog(EntryJson("t1", "theme-sunset", true, Sha(bytes), bytes.Length), EntryJson("v1", "theme-view-1", true, new string('b', 64), 10, kind: "view"));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        await client.RefreshAsync(true);
        var first = await installer.InstallAsync(client.Find("theme-sunset")!);

        // версия на сервере выросла
        var pkg2 = PackageJson("Закат 2", "#00AA00");
        var bytes2 = Encoding.UTF8.GetBytes(pkg2);
        _server.Server.ResetMappings();
        var upd = JsonSerializer.Deserialize<Dictionary<string, object?>>(EntryJson("t1", "theme-sunset", true, Sha(bytes2), bytes2.Length))!;
        upd["version"] = "1.1.0";
        StubCatalog(JsonSerializer.Serialize(upd));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg2);
        _now = _now.AddSeconds(30);
        await client.RefreshAsync(true);

        installer.StateOf(client.Find("theme-sunset")!).Should().Be(ThemeInstallState.UpdateAvailable);
        var second = await installer.InstallAsync(client.Find("theme-sunset")!);
        second.Status.Should().Be(ThemeInstallStatus.Updated);
        second.CustomId.Should().Be(first.CustomId);
        sink.Themes[first.CustomId!].Name.Should().Be("Закат 2");
        sink.Themes.Should().HaveCount(1);
    }

    [Fact]
    public async Task View_package_is_not_installed_by_this_version()
    {
        await ApiAsync();
        StubCatalog(EntryJson("v1", "theme-view-1", true, new string('b', 64), 10, kind: "view"));
        var client = NewClient();
        var installer = NewInstaller(client, out var sink, out _);
        await client.RefreshAsync(true);

        (await installer.InstallAsync(client.Find("theme-view-1")!)).Status.Should().Be(ThemeInstallStatus.Unsupported);
        sink.Themes.Should().BeEmpty();
        Requests("/api/main/marketplace/themes/v1/package/").Should().Be(0);
    }

    [Fact]
    public async Task Package_needing_newer_app_is_not_downloaded()
    {
        await ApiAsync();
        StubCatalog(EntryJson("t1", "theme-sunset", true, new string('a', 64), 10, minApp: "99.0.0"));
        var client = NewClient();
        var installer = NewInstaller(client, out _, out _);
        await client.RefreshAsync(true);

        (await installer.InstallAsync(client.Find("theme-sunset")!)).Status.Should().Be(ThemeInstallStatus.NeedsNewerApp);
        Requests("/api/main/marketplace/themes/t1/package/").Should().Be(0);
    }

    [Fact]
    public async Task Package_url_on_a_foreign_host_never_receives_the_token()
    {
        await ApiAsync();
        var pkg = PackageJson();
        var bytes = Encoding.UTF8.GetBytes(pkg);
        var entry = JsonSerializer.Deserialize<Dictionary<string, object?>>(EntryJson("t1", "theme-sunset", true, Sha(bytes), bytes.Length))!;
        entry["package_url"] = "https://evil.example.com/steal/";
        StubCatalog(JsonSerializer.Serialize(entry));
        _server.Json("GET", "/api/main/marketplace/themes/t1/package/", 200, pkg);
        var client = NewClient();
        var installer = NewInstaller(client, out _, out _);
        await client.RefreshAsync(true);

        (await installer.InstallAsync(client.Find("theme-sunset")!)).Ok.Should().BeTrue("пакет взят с собственного адреса сервера NurCRM, а не с чужого хоста");
        Requests("/api/main/marketplace/themes/t1/package/").Should().Be(1);
    }

    // ------------------------------------------------------------------ разбор пакета: только данные

    [Fact]
    public void Package_with_unknown_fields_is_parsed_and_nothing_is_executed()
    {
        var data = MarketplaceThemePackage.Parse(Encoding.UTF8.GetBytes(PackageJson()));

        data.Light.Should().ContainKey("accent").And.NotContainKey("unknownColor");
        data.FontSize.Should().Be(14);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"other\",\"theme\":{\"light\":{\"accent\":\"#FFF\"}}}")]
    [InlineData("{\"theme\":{\"name\":\"Без цветов\"}}")]
    [InlineData("{\"theme\":{\"light\":{\"accent\":\"javascript:alert(1)\"}}}")]
    public void Bad_packages_are_refused(string json)
    {
        var act = () => MarketplaceThemePackage.Parse(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Deeply_nested_package_and_huge_values_are_limited()
    {
        var deep = new string('[', 40) + new string(']', 40);
        var act = () => MarketplaceThemePackage.Parse(Encoding.UTF8.GetBytes("{\"theme\":{\"light\":{\"accent\":\"#FFF\"},\"x\":" + deep + "}}"));
        act.Should().Throw<InvalidDataException>();

        var longName = new string('Я', 5000);
        var data = MarketplaceThemePackage.Parse(Encoding.UTF8.GetBytes("{\"theme\":{\"name\":\"" + longName + "\",\"cornerRadius\":999,\"fontSize\":1,\"light\":{\"accent\":\"#abc\"}}}"));
        data.Name.Length.Should().BeLessOrEqualTo(60);
        data.CornerRadius.Should().Be(24);
        data.FontSize.Should().Be(12);
        data.Light["accent"].Should().Be("#AABBCC");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.17.79", true)]
    [InlineData("99.0.0", false)]
    [InlineData("garbage", true)]
    public void Min_app_version_check(string? min, bool ok) =>
        MarketplaceThemesClient.MeetsMinAppVersion(min, new Version(1, 17, 79, 0)).Should().Be(ok);

    // ------------------------------------------------------------------ адрес сайта

    [Fact]
    public void Web_link_default_custom_and_item_without_secrets()
    {
        MarketplaceWebLink.Build(null).Should().Be("https://market.nurcrm.kg/crm/marketplace");
        MarketplaceWebLink.Build("https://front-market.vercel.app/crm/marketplace", "kassa-loyalty").Should().Be("https://front-market.vercel.app/crm/marketplace?item=kassa-loyalty");
        MarketplaceWebLink.Build("https://market.nurcrm.kg/crm/marketplace?lang=ru", "theme-sunset").Should().Be("https://market.nurcrm.kg/crm/marketplace?lang=ru&item=theme-sunset");
        MarketplaceWebLink.Build("https://market.nurcrm.kg/crm/marketplace", "a b&token=1").Should().Be("https://market.nurcrm.kg/crm/marketplace", "небезопасный код не добавляется");
    }

    [Theory]
    [InlineData("http://market.nurcrm.kg/crm/marketplace")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://user:pass@market.nurcrm.kg/")]
    [InlineData("not a url")]
    public void Web_link_rejects_unsafe_addresses(string configured) =>
        MarketplaceWebLink.Build(configured, "kassa-loyalty").Should().BeNull();

    [Fact]
    public void Web_link_for_theme_prefers_server_page_on_own_domain_only()
    {
        var own = new MarketplaceThemeEntry { Code = "theme-sunset", WebUrl = "https://market.nurcrm.kg/crm/marketplace/themes/sunset" };
        var foreign = new MarketplaceThemeEntry { Code = "theme-sunset", WebUrl = "https://evil.example.com/x" };

        MarketplaceWebLink.ForTheme(own).Should().Be("https://market.nurcrm.kg/crm/marketplace/themes/sunset");
        MarketplaceWebLink.ForTheme(foreign, "https://market.nurcrm.kg/crm/marketplace").Should().Be("https://market.nurcrm.kg/crm/marketplace?item=theme-sunset");
    }
}
