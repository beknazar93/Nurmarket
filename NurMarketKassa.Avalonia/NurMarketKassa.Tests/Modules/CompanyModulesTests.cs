using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Modules;

/// <summary>2026-11-21 (ответ Умара 20.11, В.16 / В.23): реестр модулей компании — разбор ответа, правило «включён ли модуль» с запасом по профилю кассы, цены из public/modules,
/// расчёт тарифа с модулями, включить / выключить (If-Match, version_conflict, 403, нет адреса). Заглушка WireMock, форма ответов — как в документе Умара.</summary>
[Collection("UserPreferences")]
public sealed class CompanyModulesTests : IDisposable
{
    // Сервер: профиль stroymag, estimate и rental — из профиля (default), serials добавил владелец (added), price_levels убрал (removed), repair выключен (off).
    private const string StroymagJson = """
        {"profile":"stroymag","profile_base":"grocery","mode":null,"version":"7",
         "modules":["fractional_qty","packages","client_price","estimate","rental","serials"],
         "added":["serials"],"removed":["price_levels"],
         "items":[{"id":"estimate","enabled":true,"source":"default"},{"id":"rental","enabled":true,"source":"default"},{"id":"packages","enabled":true,"source":"default"},
                  {"id":"serials","enabled":true,"source":"added"},{"id":"price_levels","enabled":false,"source":"removed"},{"id":"repair","enabled":false,"source":"off"},{"id":"variants","enabled":false,"source":"off"}],
         "flags":["hardware_2026_11"],
         "catalog":[{"id":"estimate","name":"Смета","names":{"ru":"Смета","en":"Estimate"},"group":"stroymag","default_in":["stroymag"]},
                    {"id":"repair","name":"Ремонт","names":{"ru":"Ремонт","en":"Repair"},"group":"electronics","default_in":["electronics"]},
                    {"id":"variants","name":"Размеры","names":{"ru":"Размеры и цвета","en":"Variants"},"group":"clothing","default_in":["clothing"]}],
         "profiles":[{"id":"stroymag","name":"Строймаг","names":{"ru":"Строймаг"},"base":"grocery","modules":["estimate","rental"],"flags":[]},
                     {"id":"electronics","name":"Электроника","names":{"ru":"Электроника"},"base":"grocery","modules":["repair","trade_in"],"flags":[]}]}
        """;

    private readonly FakeNurCrmServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-modules-" + Guid.NewGuid().ToString("N"));
    private readonly string? _account = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly bool _estimate = UserPreferences.Instance.EstimateEnabled;
    private readonly bool _rental = UserPreferences.Instance.RentalEnabled;
    private readonly AppLanguage _lang = UserPreferences.Instance.Language;

    public CompanyModulesTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        UserPreferences.Instance.LastDataAccountKey = "c:modules-test";
        UserPreferences.Instance.EstimateEnabled = false;
        UserPreferences.Instance.RentalEnabled = false;
        Directory.CreateDirectory(_dir);
        CompanyModules.DirOverride = _dir;
        CompanyModules.ResetForTests();
        PublicModules.SetForTests(null);
    }

    public void Dispose()
    {
        CompanyModules.ResetForTests();
        CompanyModules.DirOverride = null;
        PublicModules.SetForTests(null);
        var p = UserPreferences.Instance;
        p.LastDataAccountKey = _account;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.EstimateEnabled = _estimate;
        p.RentalEnabled = _rental;
        p.Language = _lang;
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static CompanyModulesSnapshot Snap(string json = StroymagJson)
    {
        using var doc = JsonDocument.Parse(json);
        return CompanyModulesSnapshot.Parse(doc.RootElement)!;
    }

    private static void Sphere(string baseSphere, string? detail)
    {
        UserPreferences.Instance.MarketSphere = baseSphere;
        UserPreferences.Instance.MarketSphereDetail = detail;
    }

    private async Task<NurMarketApiClient> LoginAsync()
    {
        _server.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");
        return api;
    }

    // ───────────── разбор ─────────────

    [Fact]
    public void Parse_ReadsProfileVersionItemsCatalogAndProfiles()
    {
        var s = Snap();

        s.Profile.Should().Be("stroymag");
        s.ProfileBase.Should().Be("grocery");
        s.Version.Should().Be("7");
        s.Flags.Should().Equal("hardware_2026_11");
        s.IsEnabled("estimate").Should().BeTrue();
        s.IsEnabled("repair").Should().BeFalse();
        s.Item("serials")!.Source.Should().Be("added");
        s.Item("price_levels")!.Source.Should().Be("removed");
        s.Info("variants")!.Name.Should().Be("Размеры и цвета", "имя — на языке интерфейса из names{}");
        s.Info("estimate")!.DefaultIn.Should().Equal("stroymag");
        s.Profiles.Single(p => p.Id == "electronics").Modules.Should().Equal("repair", "trade_in");
        s.Knows("repair").Should().BeTrue();
        s.Knows("nonsense").Should().BeFalse();
    }

    [Fact]
    public void Parse_OnlyModulesList_StillWorks_AndGarbageIsNull()
    {
        using var onlyModules = JsonDocument.Parse("""{"profile":"grocery","version":3,"modules":["scales_labels"]}""");
        var s = CompanyModulesSnapshot.Parse(onlyModules.RootElement)!;
        s.Version.Should().Be("3", "версия числом");
        s.IsEnabled("scales_labels").Should().BeTrue();
        s.Items.Should().BeEmpty();

        using var garbage = JsonDocument.Parse("""{"detail":"nope"}""");
        CompanyModulesSnapshot.Parse(garbage.RootElement).Should().BeNull();
        using var arr = JsonDocument.Parse("[1,2]");
        CompanyModulesSnapshot.Parse(arr.RootElement).Should().BeNull();
    }

    // ───────────── правило IsOn ─────────────

    [Fact]
    public void NoServerAnswer_FallsBackToSphereProfile_AsBefore()
    {
        Sphere("grocery", "stroymag");
        EstimateFeature.IsAvailable.Should().BeTrue("Строймаг — Смета из коробки");
        RentalFeature.IsAvailable.Should().BeTrue();
        Sphere("grocery", "hozmag");
        EstimateFeature.IsAvailable.Should().BeFalse("Хозмаг — только через Маркетплейс");
        UserPreferences.Instance.EstimateEnabled = true;
        EstimateFeature.IsAvailable.Should().BeTrue("прежний признак из Маркетплейса");
        CompanyModules.IsKnown.Should().BeFalse();
    }

    [Fact]
    public void ServerAnswer_InSyncProfile_ServerDecides_RemovedAlwaysOff_UnknownModuleFallsBack()
    {
        Sphere("grocery", "stroymag");
        CompanyModules.SetForTests(Snap());

        CompanyModules.IsKnown.Should().BeTrue();
        CompanyModules.IsOn("estimate", false).Should().BeTrue("включён сервером");
        CompanyModules.IsOn("serials", false).Should().BeTrue("добавил владелец");
        CompanyModules.IsOn("price_levels", true, explicitLocal: true).Should().BeFalse("владелец убрал — выключено, даже с местным признаком");
        CompanyModules.IsOn("repair", true).Should().BeFalse("профили совпали — решает сервер, а не запас кассы");
        // 2026-11-22: раньше здесь было BeTrue («прежний местный признак остаётся»). Теперь, когда сервер ответил про модуль (repair есть в items, enabled=false), он единственный источник:
        // старый местный флаг (EstimateEnabled / RentalEnabled в user-settings.json) не удерживает функцию — иначе у не-Строймага оставались «Смета» и «Прокат».
        CompanyModules.IsOn("repair", false, explicitLocal: true).Should().BeFalse("сервер ответил «выкл» — местный признак не учитывается");
        CompanyModules.IsOn("module_from_future", true).Should().BeTrue("сервер такого модуля не знает — прежнее поведение");
        CompanyModules.IsOn("module_from_future", false, explicitLocal: true).Should().BeTrue("по модулю ответа сервера нет — местный признак работает (как оффлайн)");
    }

    [Fact]
    public void Decide_ServerOff_LocalFlagTrue_IsOff_ServerOn_IsOn_NoAnswer_LocalFlagWorks()
    {
        // 2026-11-22: у не-Строймага в user-settings.json остались EstimateEnabled / RentalEnabled = true, а сервер говорит «модуль выключен» — функция должна быть выключена.
        var off = Snap("""{"profile":"grocery","version":"1","modules":[],"items":[{"id":"estimate","enabled":false,"source":"off"},{"id":"rental","enabled":false,"source":"off"}]}""");
        CompanyModules.Decide(off, "estimate", profileDefault: false, explicitLocal: true, profileInSync: true).Should().BeFalse("сервер «выкл» + старый местный флаг → выкл");
        CompanyModules.Decide(off, "rental", profileDefault: false, explicitLocal: true, profileInSync: false).Should().BeFalse("профили разные: тоже без местного флага");
        CompanyModules.Decide(off, "estimate", profileDefault: false, explicitLocal: false, profileInSync: true).Should().BeFalse();

        // сервер «вкл» (включили в Маркетплейсе: PUT enable → ответ с enabled=true) — функция включена и без местного флага
        var on = Snap("""{"profile":"grocery","version":"2","modules":["estimate"],"items":[{"id":"estimate","enabled":true,"source":"added"}]}""");
        CompanyModules.Decide(on, "estimate", profileDefault: false, explicitLocal: false, profileInSync: true).Should().BeTrue();
        CompanyModules.Decide(on, "estimate", profileDefault: false, explicitLocal: false, profileInSync: false).Should().BeTrue("added — берётся с сервера и при разных профилях");

        // removed — выключено всегда
        var removed = Snap("""{"profile":"stroymag","version":"3","modules":[],"items":[{"id":"estimate","enabled":false,"source":"removed"}]}""");
        CompanyModules.Decide(removed, "estimate", profileDefault: true, explicitLocal: true, profileInSync: true).Should().BeFalse();
        CompanyModules.Decide(removed, "estimate", profileDefault: true, explicitLocal: true, profileInSync: false).Should().BeFalse();

        // trial — как добавленный владельцем
        var trial = Snap("""{"profile":"grocery","version":"4","modules":["estimate"],"items":[{"id":"estimate","enabled":true,"source":"trial","trial_until":"2026-11-28T12:00:00+00:00"}]}""");
        CompanyModules.Decide(trial, "estimate", profileDefault: false, explicitLocal: false, profileInSync: false).Should().BeTrue();
        CompanyModules.Decide(trial, "estimate", profileDefault: false, explicitLocal: false, profileInSync: true).Should().BeTrue();

        // по модулю ответа сервера нет (нет в items) — местный флаг работает, как оффлайн
        var none = Snap("""{"profile":"grocery","version":"5","modules":[],"items":[{"id":"serials","enabled":false,"source":"off"}]}""");
        CompanyModules.Decide(none, "estimate", profileDefault: false, explicitLocal: true, profileInSync: true).Should().BeTrue("в ответе про Смету ничего нет — местный признак");
        CompanyModules.Decide(none, "estimate", profileDefault: false, explicitLocal: false, profileInSync: true).Should().BeFalse();
    }

    [Fact]
    public void ServerOff_OldLocalFlags_DoNotKeepEstimateAndRental_NoServer_LocalFlagsStillWork()
    {
        // 2026-11-22: именно случай владельца — аккаунт не Строймаг, в настройках старые EstimateEnabled / RentalEnabled = true.
        Sphere("grocery", null);
        UserPreferences.Instance.EstimateEnabled = true;
        UserPreferences.Instance.RentalEnabled = true;

        EstimateFeature.IsAvailable.Should().BeTrue("ответа сервера нет (оффлайн) — местный признак, как раньше");
        RentalFeature.IsAvailable.Should().BeTrue();

        CompanyModules.SetForTests(Snap("""{"profile":"grocery","version":"1","modules":[],"items":[{"id":"estimate","enabled":false,"source":"off"},{"id":"rental","enabled":false,"source":"off"}]}"""));
        EstimateFeature.IsAvailable.Should().BeFalse("сервер ответил «выкл» — Смета пропала, несмотря на старый флаг");
        RentalFeature.IsAvailable.Should().BeFalse();

        // включили в Маркетплейсе: PUT enable → ответ сервера с enabled=true — пункт появляется сразу, флаг и сервер согласованы
        CompanyModules.SetForTests(Snap("""{"profile":"grocery","version":"2","modules":["estimate"],"items":[{"id":"estimate","enabled":true,"source":"added"},{"id":"rental","enabled":false,"source":"off"}]}"""));
        EstimateFeature.IsAvailable.Should().BeTrue();
        RentalFeature.IsAvailable.Should().BeFalse("Прокат на сервере всё ещё выключен");

        // выключили: DELETE → ответ «removed» — пункт исчезает
        CompanyModules.SetForTests(Snap("""{"profile":"grocery","version":"3","modules":[],"items":[{"id":"estimate","enabled":false,"source":"removed"}]}"""));
        EstimateFeature.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void ServerAnswer_ProfileDiffersFromKassa_KeepsKassaDefaults_ExceptWhatOwnerAdded()
    {
        // Сервер вывел профиль сам (stroymag), а в кассе выбран Хозмаг: Смета и прокат Хозмага — только через Маркетплейс, как решил владелец.
        Sphere("grocery", "hozmag");
        CompanyModules.SetForTests(Snap());

        EstimateFeature.IsAvailable.Should().BeFalse("профиль кассы без Сметы; сервер её «из профиля», а не «добавил»");
        CompanyModules.IsOn("serials", false).Should().BeTrue("а то, что добавил владелец, берётся с сервера");
    }

    [Fact]
    public void EstimateAndRental_FollowServer()
    {
        Sphere("grocery", "stroymag");
        CompanyModules.SetForTests(Snap("""{"profile":"stroymag","version":"8","modules":[],"items":[{"id":"estimate","enabled":false,"source":"removed"},{"id":"rental","enabled":false,"source":"removed"}]}"""));

        EstimateFeature.IsAvailable.Should().BeFalse("владелец убрал Смету на сайте — меню скрывает её без перезапуска");
        RentalFeature.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void ProfileIdsAreCanonical_HardwareIsStroymag_BazaarVariantsAreBazaar()
    {
        CompanyModules.CanonProfile("hardware").Should().Be("stroymag");
        CompanyModules.CanonProfile("bazaar_retail").Should().Be("bazaar");
        CompanyModules.CanonProfile("bazaar_wholesale").Should().Be("bazaar");
        CompanyModules.CanonProfile(" Electronics ").Should().Be("electronics");
        CompanyModules.ProfilesMatch("hardware", "stroymag").Should().BeTrue();
    }

    [Fact]
    public void RepairAndTradeIn_ComeFromElectronicsProfile_WithoutKey()
    {
        Sphere("grocery", "electronics");
        CompanyModules.SetForTests(Snap("""{"profile":"electronics","version":"1","modules":["serials","warranty","repair","trade_in"],"items":[{"id":"repair","enabled":true,"source":"default"},{"id":"trade_in","enabled":true,"source":"default"}]}"""));

        UserPreferences.Instance.RepairUnlocked.Should().BeTrue("ремонт входит в профиль «Электроника» — ключ не нужен");
        UserPreferences.Instance.TradeInUnlocked.Should().BeTrue();
        using (ServerAddons.RawView())
            UserPreferences.Instance.RepairUnlocked.Should().BeFalse("в файл настроек модуль не пишется — только ключ");

        CompanyModules.SetForTests(Snap("""{"profile":"electronics","version":"2","modules":[],"items":[{"id":"repair","enabled":false,"source":"removed"}]}"""));
        UserPreferences.Instance.RepairUnlocked.Should().BeFalse("владелец убрал — закрыто");
    }

    [Fact]
    public void VariantsTab_OnlyWithModule_ClothingProfileByDefault()
    {
        Sphere("grocery", null);
        ModuleFeatures.Variants.Should().BeFalse("продуктовый: нет модуля variants");
        Sphere("clothing", null);
        ModuleFeatures.Variants.Should().BeTrue("одежда без ответа сервера — как раньше");
        Sphere("grocery", "stroymag");
        CompanyModules.SetForTests(Snap("""{"profile":"stroymag","version":"1","modules":["variants"],"items":[{"id":"variants","enabled":true,"source":"added"}]}"""));
        ModuleFeatures.Variants.Should().BeTrue("владелец добавил модуль «Размеры и цвета» любой сфере");
    }

    [Fact]
    public void PackScanAndClientPrice_AreModules_NotHardwareOnly()
    {
        // Канцтовары: сервер даёт packages и client_price — «скан коробки» и «цена по клиенту» без требования Хозмаг / Строймаг.
        Sphere("grocery", "stationery");
        PackScanRules.Available.Should().BeTrue("профиль канцтоваров несёт PackScan");
        SphereCatalog.Get("stationery").ForemanPriceAvailable.Should().BeTrue();
        CompanyModules.SetForTests(Snap("""{"profile":"stationery","version":"1","modules":["quick_items"],"items":[{"id":"packages","enabled":false,"source":"removed"}]}"""));
        PackScanRules.Available.Should().BeFalse("владелец убрал модуль packages");
        Sphere("grocery", null);
        CompanyModules.SetForTests(Snap("""{"profile":"grocery","version":"2","modules":["packages"],"items":[{"id":"packages","enabled":true,"source":"added"}]}"""));
        PackScanRules.Available.Should().BeTrue("модуль packages добавлен продуктовому — скан коробки доступен");
    }

    // ───────────── цены ─────────────

    [Fact]
    public void PublicModules_ParsesPrices_AndProfileInclusion()
    {
        using var doc = JsonDocument.Parse("""
            {"currency":"KGS","modules":[{"id":"estimate","name":{"ru":"Смета","en":"Estimate"},"group":"stroymag","monthly":"300.00","once":"0.00","default_in":["stroymag"]},
                                          {"id":"quick_items","name":{"ru":"Быстрые товары"},"group":"grocery","monthly":100,"once":0,"default_in":["grocery","stationery"]}],
             "profiles":[{"id":"grocery","name":{"ru":"Продуктовый"},"modules":["quick_items"]}]}
            """);
        var list = PublicModules.Parse(doc.RootElement, out var currency)!;
        PublicModules.SetForTests(list);

        currency.Should().Be("KGS");
        PublicModules.PriceOf("estimate").Should().Be(300m);
        PublicModules.PriceOf("quick_items").Should().Be(100m, "число вместо строки");
        PublicModules.PriceOf("unknown").Should().BeNull("цену не придумываем");
        PublicModules.IncludedInProfile("estimate", "hardware").Should().BeTrue("hardware = stroymag");
        PublicModules.IncludedInProfile("estimate", "grocery").Should().BeFalse();
        list.Single(m => m.Id == "estimate").Name.Should().Be("Смета");
    }

    [Fact]
    public async Task PublicModules_LoadsFromServer_AndRaisesChangedOnce()
    {
        _server.Json("GET", "/api/public/modules/", 200, """{"currency":"KGS","modules":[{"id":"rental","name":{"ru":"Прокат"},"group":"stroymag","monthly":"300.00","once":"0.00","default_in":["stroymag"]}]}""");
        var api = await LoginAsync();
        var raised = 0;
        void On() => raised++;
        PublicModules.Changed += On;
        try
        {
            (await PublicModules.LoadAsync(api)).Should().BeTrue();
            (await PublicModules.LoadAsync(api)).Should().BeTrue();
        }
        finally
        {
            PublicModules.Changed -= On;
        }

        PublicModules.PriceOf("rental").Should().Be(300m);
        raised.Should().Be(1, "второй такой же ответ ничего не меняет");
    }

    [Fact]
    public async Task PublicModules_ServerError_KeepsNoPrices()
    {
        _server.Error("GET", "/api/public/modules/", 500);
        var api = await LoginAsync();

        (await PublicModules.LoadAsync(api)).Should().BeFalse();

        PublicModules.Cached.Should().BeNull();
    }

    // ───────────── расчёт тарифа ─────────────

    [Fact]
    public void Quote_ParsesIncludedExtraAndTotalMonth()
    {
        using var doc = JsonDocument.Parse("""
            {"monthly":"0.00","once":"0.00","base":"3600.00","packs":[],"standard_cheaper":false,"standard_price":"3600.00",
             "base_plan":"standard","base_price":"3600.00","sphere":"grocery","modules":["serials","repair"],
             "included":[{"id":"quick_items","name":{"ru":"Быстрые товары"}}],
             "extra":[{"id":"serials","name":{"ru":"Серийные номера"},"monthly":"200.00"},{"id":"repair","name":"Ремонт","monthly":300}],
             "extra_month":"500.00","total_month":"4100.00","total_once":"0.00"}
            """);

        var q = TariffApi.ParseQuote(doc.RootElement);

        q.HasModules.Should().BeTrue();
        q.BasePlan.Should().Be("standard");
        q.Sphere.Should().Be("grocery");
        q.Included!.Single().Name.Should().Be("Быстрые товары");
        q.Extra!.Select(e => (e.Id, e.Monthly)).Should().Equal(("serials", 200m), ("repair", 300m));
        q.ExtraMonth.Should().Be(500m);
        q.TotalMonth.Should().Be(4100m);
    }

    [Fact]
    public void Quote_OldServerAnswerWithoutModules_StillParses()
    {
        using var doc = JsonDocument.Parse("""{"monthly":"400.00","once":"1500.00","base":"1300.00","packs":[],"standard_cheaper":true,"standard_price":"3600.00"}""");

        var q = TariffApi.ParseQuote(doc.RootElement);

        q.HasModules.Should().BeFalse();
        q.TotalMonth.Should().BeNull();
        q.Monthly.Should().Be(400m);
    }

    [Fact]
    public void QuoteBody_SendsModulesAndSphereOnlyWhenGiven()
    {
        var plain = TariffApi.QuoteBody(["ai"], null, null);
        plain.Should().NotContainKey("modules").And.NotContainKey("sphere");
        plain["period"].Should().Be("month");

        var full = TariffApi.QuoteBody(["ai"], ["serials", "repair"], "grocery");
        ((string[])full["modules"]!).Should().Equal("serials", "repair");
        full["sphere"].Should().Be("grocery");
    }

    [Fact]
    public async Task QuoteAsync_WithModules_PostsModulesAndSphere_And409PlanNotAllowedIsNotRaisedOnStandard()
    {
        _server.Json("POST", "/api/users/company/tariff/quote/", 200,
            """{"monthly":"0","once":"0","base":"3600.00","packs":[],"base_plan":"standard","extra":[{"id":"serials","name":"Серийные номера","monthly":"200.00"}],"extra_month":"200.00","total_month":"3800.00"}""");
        var api = new TariffApi(await LoginAsync());

        var q = await api.QuoteAsync(["ai"], ["serials"], "grocery");

        q.TotalMonth.Should().Be(3800m);
        var body = JsonDocument.Parse(_server.Server.LogEntries.Single(e => e.RequestMessage.Path == "/api/users/company/tariff/quote/").RequestMessage.Body!).RootElement;
        body.GetProperty("modules").EnumerateArray().Select(x => x.GetString()).Should().Equal("serials");
        body.GetProperty("sphere").GetString().Should().Be("grocery");
    }

    // ───────────── сервер: чтение, включить, выключить ─────────────

    [Fact]
    public async Task Refresh_ReadsModules_StoresThem_AndRaisesChanged()
    {
        _server.Json("GET", "/api/users/company/modules/", 200, StroymagJson);
        var api = await LoginAsync();
        Sphere("grocery", "stroymag");
        var raised = 0;
        void On() => raised++;
        CompanyModules.Changed += On;
        try
        {
            (await CompanyModules.RefreshAsync(api, force: true)).Should().BeTrue();
        }
        finally
        {
            CompanyModules.Changed -= On;
        }

        CompanyModules.IsKnown.Should().BeTrue();
        CompanyModules.Snapshot!.Version.Should().Be("7");
        raised.Should().Be(1);
        File.Exists(Path.Combine(_dir, "company-modules.json")).Should().BeTrue("последний известный ответ — на диске");

        // как перезапуск: память забыта, ответ берётся с диска
        CompanyModules.Reset();
        CompanyModules.IsKnown.Should().BeTrue();
        CompanyModules.IsOn("serials", false).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_OldServerWithoutAddress_KeepsBehaviourAsBefore()
    {
        _server.Error("GET", "/api/users/company/modules/", 404);
        var api = await LoginAsync();

        (await CompanyModules.RefreshAsync(api, force: true)).Should().BeFalse();

        CompanyModules.IsKnown.Should().BeFalse();
        Sphere("grocery", "stroymag");
        EstimateFeature.IsAvailable.Should().BeTrue("прежнее поведение: профиль кассы");
    }

    [Fact]
    public async Task Enable_PutsEnableWithIfMatch_AndAppliesTheAnswer()
    {
        _server.Json("GET", "/api/users/company/modules/", 200, StroymagJson);
        _server.Json("PUT", "/api/users/company/modules/", 200, StroymagJson.Replace("\"version\":\"7\"", "\"version\":\"8\"").Replace("{\"id\":\"repair\",\"enabled\":false,\"source\":\"off\"}", "{\"id\":\"repair\",\"enabled\":true,\"source\":\"added\"}").Replace("\"serials\"]", "\"serials\",\"repair\"]"));
        var api = await LoginAsync();
        Sphere("grocery", "stroymag");
        await CompanyModules.RefreshAsync(api, force: true);

        var result = await CompanyModules.ChangeAsync(api, "repair", true, default);

        result.Status.Should().Be(ModuleChangeStatus.Ok);
        var put = _server.Server.LogEntries.Single(e => e.RequestMessage.Method == "PUT").RequestMessage;
        put.Headers!["If-Match"].Single().Should().Be("7", "версия из последнего ответа");
        JsonDocument.Parse(put.Body!).RootElement.GetProperty("enable").EnumerateArray().Select(x => x.GetString()).Should().Equal("repair");
        CompanyModules.Snapshot!.Version.Should().Be("8");
        CompanyModules.IsOn("repair", false).Should().BeTrue();
    }

    [Fact]
    public async Task Disable_UsesDeleteTariffModules_AndModuleNotEnabledCountsAsDone()
    {
        _server.Json("GET", "/api/users/company/modules/", 200, StroymagJson);
        _server.Json("DELETE", "/api/users/company/tariff/modules/estimate/", 200, StroymagJson.Replace("{\"id\":\"estimate\",\"enabled\":true,\"source\":\"default\"}", "{\"id\":\"estimate\",\"enabled\":false,\"source\":\"removed\"}").Replace("\"estimate\",\"rental\"", "\"rental\"").Replace("\"version\":\"7\"", "\"version\":\"9\""));
        _server.Json("DELETE", "/api/users/company/tariff/modules/repair/", 409, """{"code":"module_not_enabled"}""");
        var api = await LoginAsync();
        Sphere("grocery", "stroymag");
        await CompanyModules.RefreshAsync(api, force: true);

        (await CompanyModules.ChangeAsync(api, "estimate", false, default)).Status.Should().Be(ModuleChangeStatus.Ok);
        CompanyModules.IsOn("estimate", true).Should().BeFalse("убрали — Смета пропала без перезапуска");
        EstimateFeature.IsAvailable.Should().BeFalse();
        (await CompanyModules.ChangeAsync(api, "repair", false, default)).Status.Should().Be(ModuleChangeStatus.Ok, "уже выключен — цель достигнута");
    }

    [Theory]
    [InlineData(403, "{\"code\":\"permission_denied\"}", ModuleChangeStatus.Forbidden)]
    [InlineData(400, "{\"code\":\"module_unknown\",\"fields\":{\"enable\":[\"zzz\"]}}", ModuleChangeStatus.UnknownModule)]
    [InlineData(404, "{\"detail\":\"Not found\"}", ModuleChangeStatus.Unsupported)]
    [InlineData(503, "{\"detail\":\"down\"}", ModuleChangeStatus.NoConnection)]
    public async Task Enable_Errors_AreMapped(int status, string body, ModuleChangeStatus expected)
    {
        _server.Json("PUT", "/api/users/company/modules/", status, body);
        var api = await LoginAsync();

        var result = await CompanyModules.ChangeAsync(api, "repair", true, default);

        result.Status.Should().Be(expected);
        CompanyModules.Describe(result).Should().NotBeNull();
    }

    [Fact]
    public async Task Enable_VersionConflict_ReReadsAndRetriesOnce_ThenReportsConflict()
    {
        _server.Json("GET", "/api/users/company/modules/", 200, StroymagJson);
        _server.Json("PUT", "/api/users/company/modules/", 409, """{"code":"version_conflict","version":"9"}""");
        var api = await LoginAsync();
        Sphere("grocery", "stroymag");
        await CompanyModules.RefreshAsync(api, force: true);

        var result = await CompanyModules.ChangeAsync(api, "repair", true, default);

        result.Status.Should().Be(ModuleChangeStatus.Conflict);
        _server.Server.LogEntries.Count(e => e.RequestMessage.Method == "PUT").Should().Be(2, "один повтор после перечитывания");
        _server.Server.LogEntries.Count(e => e.RequestMessage.Method == "GET" && e.RequestMessage.Path == "/api/users/company/modules/").Should().BeGreaterThanOrEqualTo(2, "перед повтором модули перечитаны");
    }

    // ───────────── пробный период и profile_source (ответ Умара 21.11) ─────────────

    [Fact]
    public void Parse_ReadsProfileSource_AndTrialUntil_AndTrialCountsAsAdded()
    {
        var snap = Snap("""{"profile":"stroymag","profile_source":"auto","version":"3","modules":["estimate"],"items":[{"id":"estimate","enabled":true,"source":"trial","trial_until":"2026-11-28T12:00:00+00:00"}]}""");
        snap.ProfileIsAuto.Should().BeTrue();
        snap.Item("estimate")!.IsTrial.Should().BeTrue();
        snap.Item("estimate")!.TrialUntil.Should().NotBeNull();
        CompanyModules.Decide(snap, "estimate", profileDefault: false, explicitLocal: false, profileInSync: false).Should().BeTrue("проба — как добавленный владельцем");
        Snap("""{"profile":"stroymag","version":"3","modules":[],"items":[]}""").ProfileIsAuto.Should().BeFalse("нет поля (старый сервер) — как owner");
    }

    [Fact]
    public async Task AutoProfile_DoesNotOverwriteKassaSphere_OwnerProfileIsApplied()
    {
        Sphere("grocery", null);
        _server.Json("GET", "/api/users/company/modules/", 200, """{"profile":"electronics","profile_source":"auto","profile_base":"grocery","version":"2","modules":[],"items":[]}""");
        var api = await LoginAsync();
        UserPreferences.Instance.MarketSphereWasInFile.Should().BeFalse();

        await CompanyModules.RefreshAsync(api, force: true);

        MarketSpheres.Profile.Id.Should().NotBe("electronics", "профиль вывел сервер — это не выбор клиента, сфера кассы не перезаписывается (даже на новой установке)");

        _server.Json("GET", "/api/users/company/modules/", 200, """{"profile":"electronics","profile_source":"owner","profile_base":"grocery","version":"3","modules":[],"items":[]}""");
        await CompanyModules.RefreshAsync(api, force: true);
        MarketSpheres.Profile.Id.Should().Be("electronics", "профиль выбрал владелец на сайте — применяется как раньше");
    }

    [Fact]
    public async Task Trial_PostsCode_RefreshesModules_AndMaps409()
    {
        _server.Json("GET", "/api/users/company/modules/", 200, """{"profile":"grocery","version":"1","modules":[],"items":[{"id":"estimate","enabled":false,"source":"off"}]}""");
        _server.Json("POST", "/api/users/company/features/trial/", 200, """{"code":"estimate","kind":"module","expires_at":"2026-11-28T12:00:00+00:00","modules_version":"2"}""");
        var api = await LoginAsync();
        Sphere("grocery", null);
        await CompanyModules.RefreshAsync(api, force: true);

        var ok = await CompanyModules.StartTrialAsync(api, "estimate", default);

        ok.Status.Should().Be(ModuleChangeStatus.Ok);
        var post = _server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/users/company/features/trial/").RequestMessage;
        JsonDocument.Parse(post.Body!).RootElement.GetProperty("code").GetString().Should().Be("estimate");

        _server.Json("POST", "/api/users/company/features/trial/", 409, """{"code":"trial_used"}""");
        var used = await CompanyModules.StartTrialAsync(api, "estimate", default);
        used.Status.Should().Be(ModuleChangeStatus.TrialUsed);
        CompanyModules.IsTrialUsed("estimate").Should().BeTrue();
        CompanyModules.Describe(used).Should().Contain("уже использован");

        _server.Json("POST", "/api/users/company/features/trial/", 409, """{"code":"already_active"}""");
        (await CompanyModules.StartTrialAsync(api, "estimate", default)).Status.Should().Be(ModuleChangeStatus.AlreadyActive);
    }

    // ───────────── профиль с сайта ─────────────

    [Fact]
    public async Task ProfileChangedOnSite_IsTakenByKassa_ButDerivedProfileIsNotForcedOnConfiguredTill()
    {
        // Касса видела на сервере grocery; на сайте выбрали «электронику» — касса берёт её.
        Sphere("grocery", null);
        CompanyModules.SetForTests(Snap("""{"profile":"grocery","version":"1","modules":[],"items":[]}"""), seenProfile: "grocery");
        _server.Json("GET", "/api/users/company/modules/", 200, """{"profile":"electronics","profile_base":"grocery","version":"2","modules":["serials","warranty","repair","trade_in"],"items":[{"id":"repair","enabled":true,"source":"default"}]}""");
        var api = await LoginAsync();

        await CompanyModules.RefreshAsync(api, force: true);

        MarketSpheres.Profile.Id.Should().Be("electronics", "профиль, выбранный на сайте, дошёл в кассу");
        CompanyModules.SeenProfileForTests.Should().Be("electronics");

        // Другой случай: на кассе выбран Хозмаг, сервер вывел stroymag сам и касса его уже видела — выбор клиента не затираем.
        Sphere("grocery", "hozmag");
        CompanyModules.ResetForTests();
        UserPreferences.Instance.LastDataAccountKey = "c:modules-test-2";
        CompanyModules.SetForTests(Snap("""{"profile":"stroymag","version":"1","modules":[],"items":[]}"""), seenProfile: "stroymag");
        _server.Json("GET", "/api/users/company/modules/", 200, StroymagJson);
        await CompanyModules.RefreshAsync(api, force: true);

        MarketSpheres.Profile.Id.Should().Be("hozmag", "профиль на сервере не менялся — выбор кассы остаётся");

        // Новая установка (профиль сервера ещё не виделся, в настройках кассы сферы нет) — касса берёт профиль сервера.
        UserPreferences.Instance.MarketSphereWasInFile.Should().BeFalse("в тестовой папке данных настроек кассы нет");
        Sphere("grocery", null);
        CompanyModules.ResetForTests();
        UserPreferences.Instance.LastDataAccountKey = "c:modules-test-3";
        await CompanyModules.RefreshAsync(api, force: true);

        MarketSpheres.Profile.Id.Should().Be("stroymag", "новая установка берёт профиль компании");
    }
}
