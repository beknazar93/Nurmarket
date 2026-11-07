using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Marketplace;

/// <summary>2026-11-06 («купил на сайте — программа подхватывает»): немедленная проверка покупок (ServerAddons.RefreshDetailedAsync): итог «что открылось», не чаще раза в 20 секунд,
/// функции открываются без перезапуска (в том числе «Языковой пакет» и платные пункты склада), после заявки «Подключить» — частая проверка 15 минут.
/// Статические данные ServerAddons подменяются и возвращаются; файлы — во временной папке; сервер — WireMock.</summary>
[Collection("ServerAddonsStatic")]
public sealed class PurchaseRefreshTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-pr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;
    private readonly string? _previousKey = UserPreferences.Instance.LastDataAccountKey;
    private DateTime _now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private NurMarketApiClient? _api;

    public PurchaseRefreshTests()
    {
        Directory.CreateDirectory(_dir);
        ServerOutageMonitor.ResetForTests();
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, new CompanyDto { Id = "c-test" });
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("c-test");
        ServerAddons.DirOverride = _dir;
        ServerAddons.NowUtcOverride = () => _now;
        ServerAddons.ApiProvider = () => _api;
        ServerAddons.ResetMemory();
    }

    public void Dispose()
    {
        // частые проверки после заявки: гасим таймер, переведя «время» за срок
        _now = _now.AddHours(1);
        ServerAddons.BoostTick();
        ServerAddons.ApiProvider = null;
        ServerAddons.NowUtcOverride = null;
        ServerAddons.DirOverride = null;
        ServerAddons.ResetMemory();
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, _previousCompany);
        UserPreferences.Instance.LastDataAccountKey = _previousKey;
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task LoginAsync()
    {
        _api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await _api.LoginAsync("cashier@test.kg", "12345678");
    }

    private void StubAddons(params string[] activeCodes) =>
        _server.Json("GET", "/api/users/company/addons/", 200,
            "[" + string.Join(",", activeCodes.Select(c => "{\"code\":\"" + c + "\",\"active\":true,\"until\":null}").Append("{\"code\":\"analytics\",\"active\":true,\"until\":null}")) + "]");

    private int Requests() => _server.Server.LogEntries.Count(e => e.RequestMessage.Path == "/api/users/company/addons/");

    [Fact]
    public async Task Purchase_made_on_the_site_is_picked_up_immediately_and_reported()
    {
        await LoginAsync();
        StubAddons();
        (await ServerAddons.RefreshDetailedAsync(force: true)).NewSlugs.Should().BeEmpty();
        ServerAddons.IsGranted("loyalty").Should().BeFalse();
        var packsChanged = 0;
        void OnChanged() => packsChanged++;
        TariffGate.PacksChanged += OnChanged;
        try
        {
            _server.Server.ResetMappings();
            StubAddons("kassa-loyalty", "kassa-labeleditor");
            _now = _now.AddSeconds(25); // не 15 минут, а только 20-секундная пауза

            var check = await ServerAddons.RefreshDetailedAsync(force: true);

            check.Status.Should().Be(ServerAddons.PurchaseCheckStatus.Checked);
            check.NewSlugs.Should().BeEquivalentTo("labeleditor", "loyalty");
            ServerAddons.IsGranted("loyalty").Should().BeTrue();
            UserPreferences.Instance.LabelEditorUnlocked.Should().BeTrue("пункт «Редактор этикеток» открывается сразу, без перезапуска");
            packsChanged.Should().BeGreaterThan(0, "меню и вкладки пересобираются по TariffGate.PacksChanged");
        }
        finally
        {
            TariffGate.PacksChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task No_new_purchases_is_reported_as_empty_list()
    {
        await LoginAsync();
        StubAddons("kassa-loyalty");
        await ServerAddons.RefreshDetailedAsync(force: true);
        _now = _now.AddSeconds(30);

        var check = await ServerAddons.RefreshDetailedAsync(force: true);

        check.Status.Should().Be(ServerAddons.PurchaseCheckStatus.Checked);
        check.NewSlugs.Should().BeEmpty("«Новых покупок не найдено»");
    }

    [Fact]
    public async Task Refresh_not_more_often_than_once_per_20_seconds()
    {
        await LoginAsync();
        StubAddons("kassa-loyalty");

        (await ServerAddons.RefreshDetailedAsync(force: true)).Status.Should().Be(ServerAddons.PurchaseCheckStatus.Checked);
        _now = _now.AddSeconds(10);
        (await ServerAddons.RefreshDetailedAsync(force: true)).Status.Should().Be(ServerAddons.PurchaseCheckStatus.Throttled);
        _now = _now.AddSeconds(11);
        (await ServerAddons.RefreshDetailedAsync(force: true)).Status.Should().Be(ServerAddons.PurchaseCheckStatus.Checked);

        Requests().Should().Be(2);
    }

    [Fact]
    public async Task Server_failure_keeps_cache_and_reports_failed()
    {
        await LoginAsync();
        StubAddons("kassa-loyalty");
        await ServerAddons.RefreshDetailedAsync(force: true);
        _server.Server.ResetMappings();
        _server.Error("GET", "/api/users/company/addons/", 500);
        _now = _now.AddSeconds(30);

        var check = await ServerAddons.RefreshDetailedAsync(force: true);

        check.Status.Should().Be(ServerAddons.PurchaseCheckStatus.Failed);
        ServerAddons.IsGranted("loyalty").Should().BeTrue("работаем по кэшу");
    }

    [Fact]
    public async Task Language_pack_bought_on_the_site_opens_paid_languages_without_restart()
    {
        await LoginAsync();
        StubAddons();
        await ServerAddons.RefreshDetailedAsync(force: true);
        LanguagePackGate.IsAvailable(AppLanguage.English).Should().BeFalse();

        _server.Server.ResetMappings();
        StubAddons("kassa-language");
        _now = _now.AddSeconds(30);
        var check = await ServerAddons.RefreshDetailedAsync(force: true);

        check.NewSlugs.Should().Equal("language");
        LanguagePackGate.IsUnlocked.Should().BeTrue();
        LanguagePackGate.IsAvailable(AppLanguage.English).Should().BeTrue();
        LanguagePackGate.IsAvailable(AppLanguage.Turkish).Should().BeTrue();
        LanguagePackGate.IsAvailable(AppLanguage.Uzbek).Should().BeTrue();
    }

    [Fact]
    public async Task Scales_pricetag_and_labeleditor_open_right_after_purchase()
    {
        await LoginAsync();
        StubAddons("kassa-scales", "kassa-pricetag", "kassa-labeleditor");

        await ServerAddons.RefreshDetailedAsync(force: true);

        var prefs = UserPreferences.Instance;
        prefs.ScalesUnlocked.Should().BeTrue();
        prefs.PriceTagEditorUnlocked.Should().BeTrue();
        prefs.LabelEditorUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task After_request_to_connect_the_server_is_polled_every_minute_for_15_minutes()
    {
        await LoginAsync();
        _server.Json("POST", "/api/support/addon-requests/", 201, "{\"ok\":true}");
        ServerAddons.IsBoosted.Should().BeFalse();

        var result = await ServerAddons.SubmitRequestAsync("loyalty", "Лояльность", "1 500 сом разово", "+996555123456");

        result.Status.Should().Be(ServerAddons.RequestStatus.Sent);
        ServerAddons.IsBoosted.Should().BeTrue();
        ServerAddons.BoostEvery.Should().Be(TimeSpan.FromMinutes(1));
        ServerAddons.BoostFor.Should().Be(TimeSpan.FromMinutes(15));
        _now = _now.AddMinutes(14);
        ServerAddons.IsBoosted.Should().BeTrue();
        _now = _now.AddMinutes(2);
        ServerAddons.IsBoosted.Should().BeFalse("через 15 минут частые проверки заканчиваются, дальше — обычные раз в 15 минут");
    }

    [Fact]
    public async Task Rejected_request_does_not_start_frequent_polling()
    {
        await LoginAsync();
        _server.Json("POST", "/api/support/addon-requests/", 400, "{\"detail\":\"phone\"}");

        var result = await ServerAddons.SubmitRequestAsync("loyalty", "Лояльность", "", "123");

        result.Status.Should().Be(ServerAddons.RequestStatus.PhoneTooShort);
        ServerAddons.IsBoosted.Should().BeFalse();
    }

    [Fact]
    public async Task Offline_without_session_reports_failed_without_exceptions()
    {
        // вход не выполнен: клиента нет
        var check = await ServerAddons.RefreshDetailedAsync(force: true);

        check.Status.Should().Be(ServerAddons.PurchaseCheckStatus.Failed);
        Requests().Should().Be(0);
    }
}
