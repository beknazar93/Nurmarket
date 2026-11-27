using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-26, отчёт «Проверка сфер»: Д2 (хранилища сфер в реестре компании), Д4 (серверная сфера autoparts / bazaar без вопроса «Какой у вас магазин?»),
/// Д1 (причина отказа 403 вместо одной общей фразы). Без сети; настройки — общий UserPreferences.Instance, значения возвращаются.</summary>
[Collection("UserPreferences")]
public sealed class SphereReportFixesTests : IDisposable
{
    private readonly string _sphere;
    private readonly string? _detail;
    private readonly string? _seen;
    private readonly bool _wasInFile;
    private readonly AppLanguage _language;

    public SphereReportFixesTests()
    {
        var p = UserPreferences.Instance;
        _sphere = p.MarketSphere;
        _detail = p.MarketSphereDetail;
        _seen = p.MarketSphereServerSeen;
        _wasInFile = p.MarketSphereWasInFile;
        _language = p.Language;
        p.Language = AppLanguage.Russian;
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.MarketSphereServerSeen = _seen;
        SetWasInFile(_wasInFile);
        p.Language = _language;
        Remember("co-reset", null);
    }

    private static void SetWasInFile(bool value) =>
        typeof(UserPreferences).GetProperty(nameof(UserPreferences.MarketSphereWasInFile), BindingFlags.Public | BindingFlags.Instance)!
            .GetSetMethod(nonPublic: true)!.Invoke(UserPreferences.Instance, [value]);

    private static void Remember(string company, string? sphere)
    {
        using var doc = JsonDocument.Parse(sphere is null ? "{}" : "{\"data\":{\"market_sphere\":\"" + sphere + "\"}}");
        MarketSphereSync.RememberFromCompanyJson(company, doc.RootElement);
    }

    private static void FreshInstall()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        p.MarketSphereServerSeen = null;
        SetWasInFile(false);
    }

    // ───────── Д2: реестр хранилищ ─────────

    [Theory]
    [InlineData("autopart-meta.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("volume-prices.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("currency-rates.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("receiving-pack-sizes.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("receiving-carcass-yield.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("phone-print-journal.json", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("product-merge", CompanyStoreRegistry.StoreKind.Company)]
    [InlineData("receiving-view.json", CompanyStoreRegistry.StoreKind.Terminal)]
    [InlineData("offline_auth_session.dat", CompanyStoreRegistry.StoreKind.Terminal)]
    [InlineData("CrashReports", CompanyStoreRegistry.StoreKind.Terminal)]
    public void Sphere_stores_are_in_registry(string name, CompanyStoreRegistry.StoreKind expected) =>
        CompanyStoreRegistry.Classify(name).Should().Be(expected);

    // ───────── Д4: сфера с сервера ─────────

    [Theory]
    [InlineData("autoparts", "autoparts")]
    [InlineData("bazaar", "bazaar")]
    [InlineData("bazaar_retail", "bazaar_retail")]
    [InlineData("bazaar_wholesale", "bazaar_wholesale")]
    public void Server_detail_sphere_is_remembered_on_grocery_base(string server, string detail)
    {
        Remember("co-d4-remember", server);
        MarketSphereSync.ServerSphere.Should().Be(MarketSpheres.Grocery, "по ServerSphere касса не спрашивает «Какой у вас магазин?» и держит замок вида");
        MarketSphereSync.ServerDetail.Should().Be(detail);
    }

    [Fact]
    public void Unknown_server_sphere_is_still_not_set()
    {
        Remember("co-d4-unknown", "spaceships");
        MarketSphereSync.ServerSphere.Should().BeNull();
        MarketSphereSync.ServerDetail.Should().BeNull();
    }

    [Theory]
    [InlineData("autoparts", "autoparts", AutoPartsProfile.Id)]
    [InlineData("bazaar", "bazaar", BazaarProfile.Id)]
    [InlineData("bazaar_wholesale", "bazaar_wholesale", BazaarProfile.Id)]
    public void Fresh_install_takes_detail_sphere_from_server_without_question(string server, string expectedDetail, string profileId)
    {
        FreshInstall();
        Remember("co-d4-" + server, server);

        MarketSphereSync.ApplyServerValueIfNew().Should().BeTrue();

        var p = UserPreferences.Instance;
        p.MarketSphere.Should().Be(MarketSpheres.Grocery);
        p.MarketSphereDetail.Should().Be(expectedDetail);
        MarketSpheres.Profile.Id.Should().Be(profileId);
        p.MarketSphereServerSeen.Should().Be(server);
        if (server == "bazaar_wholesale")
            SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Wholesale);
    }

    [Fact]
    public void Configured_till_keeps_its_own_choice_on_first_sight_of_detail_sphere()
    {
        FreshInstall();
        SetWasInFile(true); // касса уже настроена клиентом: выбор не затираем
        UserPreferences.Instance.MarketSphere = MarketSpheres.Clothing;
        Remember("co-d4-configured", "autoparts");

        MarketSphereSync.ApplyServerValueIfNew().Should().BeFalse();
        UserPreferences.Instance.MarketSphere.Should().Be(MarketSpheres.Clothing);
    }

    // ───────── Д1: причина отказа 403 ─────────

    private static ApiException Forbidden(string json) => new("raw", 403, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Forbidden_without_reason_keeps_general_text()
    {
        var general = ServerOutageMonitor.FriendlyError(new ApiException("raw", 403));
        general.Should().StartWith("Нет права на это действие").And.NotContain("Причина");
        ServerOutageMonitor.FriendlyError(Forbidden("""{"code":"permission_denied"}""")).Should().Be(general);
    }

    [Fact]
    public void Forbidden_with_server_detail_shows_reason()
    {
        var text = ServerOutageMonitor.FriendlyError(Forbidden("""{"code":"permission_denied","detail":"Возврат по этому чеку закрыт правилом компании."}"""));
        text.Should().StartWith("Нет права на это действие").And.Contain("Причина: Возврат по этому чеку закрыт правилом компании.");
    }

    [Fact]
    public void Forbidden_hides_technical_detail()
    {
        var text = ServerOutageMonitor.FriendlyError(Forbidden("""{"code":"permission_denied","detail":"NullReferenceException at Return.cs:42"}"""));
        text.Should().NotContain("Exception").And.NotContain("Причина");
    }

    [Theory]
    [InlineData("plan_limit")]
    [InlineData("plan_client_not_allowed")]
    public void Forbidden_by_plan_shows_plan_reason_in_all_languages(string code)
    {
        var ex = Forbidden("{\"code\":\"" + code + "\",\"detail\":\"Plan blocked\"}");
        var ru = ServerOutageMonitor.FriendlyError(ex);
        ru.Should().Contain("тариф").And.NotContain("Нет права на это действие");
        var seen = new HashSet<string> { ru };
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            var text = ServerOutageMonitor.FriendlyError(ex);
            text.Should().NotBeNullOrWhiteSpace();
            seen.Add(text).Should().BeTrue("текст для " + lang + " должен отличаться от остальных");
        }
    }
}
