using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-11: коллекция без параллельности — облачная синхронизация, правила весов, доступы (AccessSync) и платные функции — статическое состояние.</summary>
[CollectionDefinition("CloudSyncSerial", DisableParallelization = true)]
public sealed class CloudSyncSerialCollection;

/// <summary>2026-11-11, решение владельца «правила префиксов весовых штрихкодов — синхронизацию через облако, БЫСТРУЮ»: правило, записанное в программе
/// владельца, приходит на кассу (общие настройки компании app-settings/nurmarket-kassa, ключ ScaleBarcodeRulesShared) и сразу работает при разборе этикетки;
/// битое значение не применяется; подсказка соседа «settings» читает облако сейчас; изменённое правило уходит быстро; пустое облако касса молча не заполняет.
/// Сервер — WireMock, настоящий не трогается; настройки — во временной папке test-safe.</summary>
[Collection("CloudSyncSerial")]
public sealed class ScaleBarcodeRulesCloudSyncTests : IDisposable
{
    private const string CompanyId = "fake-company-1";
    private const string CompanyPath = "/api/main/app-settings/nurmarket-kassa/";
    private readonly FakeNurCrmServer _server = new();
    private readonly UserPreferences _p = UserPreferences.Instance;
    private readonly Func<bool> _wasAuthoritative = ScaleBarcodeRulesSync.IsAuthoritative;
    private readonly (string Amount, string Weight, string? Unit, string? Pc, int RType, int RDept, int TmPrefix, string? TmFormat, double Max) _saved;
    private NurMarketApiClient? _api;

    public ScaleBarcodeRulesCloudSyncTests()
    {
        _saved = (_p.ScaleAmountPrefixes, _p.ScaleWeightPrefixes, _p.ScaleBarcodeAmountUnitLocal, _p.ShtrikhPcConfig, _p.RongtaBarcodeType,
            _p.RongtaLanBarcodePrefix, _p.TmScaleBarcodePrefix, _p.TmScaleDahuaBarcode, _p.ScaleMaxLabelAmountSom);
        ScaleBarcodeRulesSync.ResetForTests();
        ServerOutageMonitor.ResetForTests();
        SettingsCloudSync.OnUserChanged(null);
        // касса: своих правил нет
        _p.ScaleAmountPrefixes = "";
        _p.ScaleWeightPrefixes = "";
        _p.ScaleBarcodeAmountUnitLocal = null;
        _p.ShtrikhPcConfig = null;
        _p.ScaleBarcodeLayout = "plu";
        _p.ScaleBarcodeMode = "auto";
        _p.ScaleBarcodeAmountUnit = "tiyin";
        ScaleBarcodeRulesSync.ApplyToParser();
    }

    public void Dispose()
    {
        ScaleBarcodeRulesSync.IsAuthoritative = _wasAuthoritative;
        SettingsCloudSync.ApiProvider = null;
        SettingsCloudSync.OnUserChanged(null);
        _p.ScaleAmountPrefixes = _saved.Amount;
        _p.ScaleWeightPrefixes = _saved.Weight;
        _p.ScaleBarcodeAmountUnitLocal = _saved.Unit;
        _p.ShtrikhPcConfig = _saved.Pc;
        _p.RongtaBarcodeType = _saved.RType;
        _p.RongtaLanBarcodePrefix = _saved.RDept;
        _p.TmScaleBarcodePrefix = _saved.TmPrefix;
        _p.TmScaleDahuaBarcode = _saved.TmFormat;
        _p.ScaleMaxLabelAmountSom = _saved.Max;
        ScaleBarcodeRulesSync.ResetForTests();
        ScaleBarcodeRulesSync.ApplyToParser();
        _server.Dispose();
    }

    /// <summary>Правила, как их соберёт программа владельца: префикс 21 — «сумма», 22 — «вес», раскладка РС-200 на префиксе 27.</summary>
    private static string OwnerRules(string amount = "21", string weight = "22")
    {
        var owner = new UserPreferences { ScaleAmountPrefixes = amount, ScaleWeightPrefixes = weight, RongtaBarcodeType = 3, RongtaLanBarcodePrefix = 24 };
        var pc = new ShtrikhPcSettings { BarcodeEnabled = true, BarcodePrefix = 27, CodeLength = 5, BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight };
        owner.ShtrikhPcConfig = pc.ToJson();
        return owner.ScaleBarcodeRulesShared!;
    }

    private void StubCloud(string? rules)
    {
        _server.Server.ResetMappings();
        _server.StubLogin();
        _server.StubProfile();
        var settings = new JsonObject();
        if (rules is not null)
            settings[ScaleBarcodeRulesSync.CloudKey] = rules;
        _server.Json("GET", CompanyPath, 200, new JsonObject { ["settings"] = settings, ["updated_at"] = "2026-11-11T10:00:00Z" }.ToJsonString());
        _server.Json("GET", "/api/users/app-settings/nurmarket-kassa/", 200, "{\"settings\":{}}");
        _server.Json("PATCH", CompanyPath, 200, "{}");
        _server.Json("PATCH", "/api/users/app-settings/nurmarket-kassa/", 200, "{}");
    }

    private async Task LoginAndPullAsync()
    {
        _api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await _api.LoginAsync("cashier@test.kg", "12345678");
        SettingsCloudSync.ApiProvider = () => _api;
        SettingsCloudSync.OnUserChanged("u1");
        await SettingsCloudSync.PullOnceAsync(CompanyId);
    }

    private IEnumerable<string> CompanyPatchBodies() => _server.Server.LogEntries
        .Where(e => e.RequestMessage?.Method == "PATCH" && e.RequestMessage?.Path == CompanyPath)
        .Select(e => e.RequestMessage?.Body ?? "");

    private static async Task WaitFor(Func<bool> condition, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Owner_rule_reaches_the_till_and_parses_the_label_by_new_rule()
    {
        WeightBarcodeParser.WeightPrefixes.Should().NotContain("22", "до синхронизации своих правил у кассы нет");
        StubCloud(OwnerRules());
        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes.Should().Be("21");
        _p.ScaleWeightPrefixes.Should().Be("22");
        _p.RongtaBarcodeType.Should().Be(3, "тип штрихкода весов Rongta — тоже правило магазина");
        _p.RongtaLanBarcodePrefix.Should().Be(24);
        WeightBarcodeParser.TryParse("2101003013207", out var after).Should().BeTrue();
        after.Kind.Should().Be(WeightBarcodeValueKind.Amount, "правило владельца: префикс 21 — сумма");
        after.Value.Should().BeApproximately(13.20, 0.001);
        WeightBarcodeParser.FindCustomLayout("27").Should().NotBeNull("раскладка РС-200 включена правилом из облака без перезапуска");
        ShtrikhPcSettings.Parse(_p.ShtrikhPcConfig).Folder.Should().BeEmpty("папка обмена — оборудование ПК, из облака не приходит");
    }

    [Theory]
    [InlineData("garbage{")]
    [InlineData("{\"v\":2,\"amount\":[\"21\"],\"weight\":[]}")]
    [InlineData("{\"v\":1,\"amount\":[\"31\"],\"weight\":[]}")]
    [InlineData("{\"v\":1,\"amount\":[\"21\"],\"weight\":[\"21\"]}")]
    [InlineData("{\"v\":1,\"weight\":[]}")]
    [InlineData("{\"v\":1,\"amount\":[],\"weight\":[],\"pc200\":{\"enabled\":true,\"prefix\":99}}")]
    [InlineData("{\"v\":1,\"amount\":[],\"weight\":[],\"unit\":\"rub\"}")]
    public async Task Broken_cloud_value_is_ignored_and_local_rule_kept(string broken)
    {
        _p.ScaleAmountPrefixes = "24";
        StubCloud(broken);
        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes.Should().Be("24", "битое/чужой версии значение облака не применяется — касса работает по своим правилам");
        ScaleBarcodeRulesSync.TryParse(broken, out var reason).Should().BeNull();
        reason.Should().NotBeEmpty();
        CompanyPatchBodies().Should().NotContain(b => b.Contains(ScaleBarcodeRulesSync.CloudKey), "касса не затирает облако своим значением молча");
    }

    /// <summary>Значение ключа правил из тела PATCH (тело — сами настройки; значение — строка JSON).</summary>
    private static string? RulesIn(string patchBody) => JsonNode.Parse(patchBody) is JsonObject o && o[ScaleBarcodeRulesSync.CloudKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private List<string> RulesPatches() => CompanyPatchBodies().Select(RulesIn).Where(r => r is not null).Select(r => r!).ToList();

    private static HashSet<string> Set(string csv) => UserPreferences.ParseAmountPrefixes(csv);

    [Fact]
    public async Task Empty_cloud_is_filled_once_with_the_local_rules_of_the_till()
    {
        // 2026-11-12 (Л-3, аудит Fable; заменяет прежнее «пустое облако молча не заполняем»): иначе первое же правило владельца заменяло бы всё, что настроено на кассе.
        _p.ScaleAmountPrefixes = "21";
        _p.ScaleWeightPrefixes = "22";
        StubCloud(null);
        await LoginAndPullAsync();
        await WaitFor(() => RulesPatches().Count > 0, 4000);

        var sent = RulesPatches();
        sent.Should().HaveCount(1, "правила кассы подняты в пустое облако один раз");
        var rules = ScaleBarcodeRulesSync.TryParse(sent[0], out var reason);
        rules.Should().NotBeNull(reason);
        rules!.Amount.Should().BeEquivalentTo(new[] { "21" });
        rules.Weight.Should().BeEquivalentTo(new[] { "22" });
        _p.ScaleAmountPrefixes.Should().Be("21", "местное правило остаётся");

        // Облако теперь не пустое: повторный вход ничего не отправляет заново.
        StubCloud(sent[0]);
        SettingsCloudSync.OnUserChanged(null);
        SettingsCloudSync.OnUserChanged("u1");
        await SettingsCloudSync.PullOnceAsync(CompanyId);
        await Task.Delay(900);
        RulesPatches().Should().HaveCount(1, "поднимается один раз");
    }

    [Fact]
    public async Task Empty_cloud_is_not_filled_by_a_till_without_own_rules()
    {
        StubCloud(null);
        await LoginAndPullAsync();
        await Task.Delay(900);

        RulesPatches().Should().BeEmpty("умолчания облако не заполняют: своих правил у кассы нет");
    }

    [Fact]
    public async Task Owner_edit_keeps_the_amount_unit_tiyin_set_on_the_till()
    {
        // Л-2: у владельца единица не выбрана (null) — раньше облачное «нет единицы» стирало на кассе «тыйын», и сумма с этикетки становилась в 100 раз больше.
        _p.ScaleBarcodeAmountUnit = "som"; // с сайта — «сом»; на кассе выбрано «тыйын» (весы печатают сумму в тыйынах)
        _p.ScaleBarcodeAmountUnitLocal = "tiyin";
        ScaleBarcodeRulesSync.ApplyToParser();
        var ownerBlob = OwnerRules();
        JsonNode.Parse(ownerBlob)!.AsObject().ContainsKey("unit").Should().BeFalse("у владельца единица не задана — в облако не пишется");
        StubCloud(ownerBlob);

        await LoginAndPullAsync();

        _p.ScaleBarcodeAmountUnitLocal.Should().Be("tiyin", "правка префиксов у владельца не меняет единицу суммы кассы");
        WeightBarcodeParser.TryParse("2101003013207", out var parsed).Should().BeTrue();
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        parsed.Value.Should().BeApproximately(13.20, 0.001, "сумма 01320 тыйынов = 13,20 сом, а не 1320");
    }

    [Theory]
    [InlineData("{\"v\":1,\"amount\":[\"21\"],\"weight\":[],\"unit\":null}")]
    [InlineData("{\"v\":1,\"amount\":[\"21\"],\"weight\":[]}")]
    public async Task Cloud_without_a_unit_never_erases_the_local_unit(string cloud)
    {
        _p.ScaleBarcodeAmountUnitLocal = "tiyin";
        StubCloud(cloud);
        await LoginAndPullAsync();

        _p.ScaleBarcodeAmountUnitLocal.Should().Be("tiyin");
        _p.ScaleAmountPrefixes.Should().Be("21", "остальное правило применилось");
    }

    [Fact]
    public async Task Cloud_with_an_explicit_unit_sets_it()
    {
        _p.ScaleBarcodeAmountUnitLocal = "tiyin";
        StubCloud("{\"v\":1,\"amount\":[\"21\"],\"weight\":[],\"unit\":\"som\"}");
        await LoginAndPullAsync();

        _p.ScaleBarcodeAmountUnitLocal.Should().Be("som", "владелец задал единицу явно — применяется");
    }

    [Fact]
    public async Task Owner_edit_does_not_erase_the_equipment_of_the_till()
    {
        // Л-3: Rongta, TM и РС-200 — оборудование этой кассы; владелец, не менявший их (умолчания), не должен их затирать.
        _p.RongtaBarcodeType = 3;
        _p.RongtaLanBarcodePrefix = 24;
        _p.TmScaleBarcodePrefix = 25;
        _p.ShtrikhPcConfig = new ShtrikhPcSettings { BarcodeEnabled = true, BarcodePrefix = 27, CodeLength = 5, BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight }.ToJson();
        _p.ScaleMaxLabelAmountSom = 30000;
        ScaleBarcodeRulesSync.ApplyToParser();
        var defaults = new UserPreferences { ScaleAmountPrefixes = "23" };
        var ownerBlob = defaults.ScaleBarcodeRulesShared!;
        var o = JsonNode.Parse(ownerBlob)!.AsObject();
        o.ContainsKey("rongta").Should().BeFalse("умолчания оборудования в облако не пишутся");
        o.ContainsKey("tm").Should().BeFalse();
        o.ContainsKey("pc200").Should().BeFalse();
        o.ContainsKey("max_amount").Should().BeFalse();
        StubCloud(ownerBlob);

        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes.Should().Be("23", "правило владельца применено");
        _p.RongtaBarcodeType.Should().Be(3);
        _p.RongtaLanBarcodePrefix.Should().Be(24);
        _p.TmScaleBarcodePrefix.Should().Be(25);
        _p.ScaleMaxLabelAmountSom.Should().Be(30000);
        ShtrikhPcSettings.Parse(_p.ShtrikhPcConfig).BarcodeEnabled.Should().BeTrue("РС-200 кассы осталась включённой");
        WeightBarcodeParser.FindCustomLayout("27").Should().NotBeNull();
    }

    [Fact]
    public async Task Owner_adding_a_prefix_keeps_the_till_prefixes()
    {
        // Л-3: на кассе 21 = сумма, 22 = вес. Владелец добавляет 23 (о 21 и 22 он ничего не знает) — на кассе остаются все три.
        _p.ScaleAmountPrefixes = "21";
        _p.ScaleWeightPrefixes = "22";
        ScaleBarcodeRulesSync.ApplyToParser();
        StubCloud(OwnerRules(amount: "23", weight: ""));

        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes.Should().Be("21,23");
        _p.ScaleWeightPrefixes.Should().Be("22", "префикс «вес» кассы не пропал");
        WeightBarcodeParser.AmountPrefixes.Should().BeEquivalentTo(new[] { "21", "23" });
        WeightBarcodeParser.WeightPrefixes.Should().Contain("22");
    }

    [Fact]
    public async Task On_a_conflict_over_one_prefix_the_cloud_rule_of_the_owner_wins()
    {
        _p.ScaleAmountPrefixes = "21";
        _p.ScaleWeightPrefixes = "22";
        ScaleBarcodeRulesSync.ApplyToParser();
        StubCloud(OwnerRules(amount: "22", weight: "21")); // владелец: 22 — сумма, 21 — вес

        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes.Should().Be("22");
        _p.ScaleWeightPrefixes.Should().Be("21", "об этих префиксах владелец сказал прямо — его правило главнее");
    }

    [Fact]
    public async Task Owner_program_mirrors_the_cloud_exactly()
    {
        // Программа владельца — источник правды: облако заменяет её префиксы целиком (иначе снятый префикс возвращался бы).
        var wasAuthoritative = ScaleBarcodeRulesSync.IsAuthoritative;
        ScaleBarcodeRulesSync.IsAuthoritative = () => true;
        try
        {
            _p.ScaleAmountPrefixes = "21";
            _p.ScaleWeightPrefixes = "22";
            StubCloud(OwnerRules(amount: "23", weight: ""));

            await LoginAndPullAsync();

            _p.ScaleAmountPrefixes.Should().Be("23");
            _p.ScaleWeightPrefixes.Should().BeEmpty();
        }
        finally
        {
            ScaleBarcodeRulesSync.IsAuthoritative = wasAuthoritative;
        }
    }

    [Fact]
    public async Task Edits_made_at_almost_the_same_time_are_merged_not_lost()
    {
        // Л-20: владелец добавил 23 в облако, а касса (администратор), ещё не успев его прочитать, получила ответ кассира «25 — вес» и отправляет своё.
        // Раньше целый кусок правил принадлежал «последнему записавшему» — правка владельца пропадала.
        StubCloud(OwnerRules()); // сумма 21, вес 22
        await LoginAndPullAsync();
        _p.ScaleAmountPrefixes.Should().Be("21");

        StubCloud(OwnerRules(amount: "21,23", weight: "22")); // владелец уже записал 23, касса ещё не опрашивала облако
        _p.ScaleWeightPrefixes = "22,25"; // ответ кассира: в штрихкоде с префиксом 25 — вес
        _p.SaveToDisk();
        await WaitFor(() => RulesPatches().Count > 0, 4000);

        var sent = RulesPatches();
        sent.Should().NotBeEmpty("правка кассы отправлена");
        var rules = ScaleBarcodeRulesSync.TryParse(sent[^1], out var reason);
        rules.Should().NotBeNull(reason);
        rules!.Amount.Should().BeEquivalentTo(new[] { "21", "23" }, "правка владельца (23) не потеряна");
        rules.Weight.Should().BeEquivalentTo(new[] { "22", "25" }, "и ответ кассира (25) не потерян");
        _p.ScaleAmountPrefixes.Should().Be("21,23", "касса взяла к себе слитое: 23 владельца теперь и у неё");
        _p.ScaleWeightPrefixes.Should().Be("22,25");
        await Task.Delay(900);
        RulesPatches().Should().HaveCount(sent.Count, "слитое не уходит снова и снова");
    }

    [Fact]
    public async Task Rules_are_not_sent_when_the_cloud_cannot_be_read_for_the_merge()
    {
        StubCloud(OwnerRules());
        await LoginAndPullAsync();
        // облако не читается — слить не с чем
        _server.Server.ResetMappings();
        _server.StubLogin();
        _server.StubProfile();
        _server.Json("GET", CompanyPath, 500, "{\"detail\":\"boom\"}");
        _server.Json("PATCH", CompanyPath, 200, "{}");

        _p.ScaleWeightPrefixes = "22,25";
        _p.SaveToDisk();
        await Task.Delay(1500);

        RulesPatches().Should().BeEmpty("не прочитав облако, касса вслепую не затирает его");
    }

    [Fact]
    public void MergeForPush_unions_prefixes_prefers_the_senders_choice_and_copies_missing_fields_from_the_cloud()
    {
        var local = "{\"v\":1,\"amount\":[\"21\"],\"weight\":[\"22\"],\"shared_amount\":[]}";
        var cloud = "{\"v\":1,\"amount\":[\"22\",\"23\"],\"weight\":[\"24\"],\"unit\":\"tiyin\",\"max_amount\":30000,\"shared_amount\":[\"23\"],\"rongta\":{\"type\":3,\"dept\":24}}";

        var merged = ScaleBarcodeRulesSync.TryParse(ScaleBarcodeRulesSync.MergeForPush(local, cloud), out var reason);

        merged.Should().NotBeNull(reason);
        merged!.Amount.Should().BeEquivalentTo(new[] { "21", "23" }, "22 — спор: отправитель (касса) сказал «вес», его выбор главнее; 23 — только в облаке, сохраняется");
        merged.Weight.Should().BeEquivalentTo(new[] { "22", "24" });
        merged.Unit.Should().Be("tiyin", "у отправителя единицы нет — берётся облачная");
        merged.MaxAmount.Should().Be(30000);
        merged.RongtaType.Should().Be(3);
        merged.RongtaDept.Should().Be(24);
        merged.SharedAmount.Should().BeEmpty("прямая отправка — выбор самого отправителя (снял префикс — он не возвращается)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage{")]
    public void MergeForPush_with_an_empty_or_broken_cloud_sends_own_value(string? cloud)
    {
        var local = "{\"v\":1,\"amount\":[\"21\"],\"weight\":[],\"shared_amount\":[]}";
        ScaleBarcodeRulesSync.MergeForPush(local, cloud).Should().Be(local);
    }

    [Fact]
    public void MergeForPush_in_the_owner_program_sends_own_value_as_is()
    {
        var wasAuthoritative = ScaleBarcodeRulesSync.IsAuthoritative;
        ScaleBarcodeRulesSync.IsAuthoritative = () => true;
        try
        {
            var local = "{\"v\":1,\"amount\":[\"23\"],\"weight\":[],\"shared_amount\":[]}";
            var cloud = "{\"v\":1,\"amount\":[\"21\",\"23\"],\"weight\":[\"22\"],\"shared_amount\":[]}";
            ScaleBarcodeRulesSync.MergeForPush(local, cloud).Should().Be(local, "владелец снял 21 и 22 — снятое из облака уходит (источник правды)");
        }
        finally
        {
            ScaleBarcodeRulesSync.IsAuthoritative = wasAuthoritative;
        }
    }

    [Fact]
    public async Task Changed_rule_is_sent_fast()
    {
        StubCloud(OwnerRules());
        await LoginAndPullAsync();

        _p.ScaleAmountPrefixes = "21,23";
        var started = DateTime.UtcNow;
        _p.SaveToDisk();
        await WaitFor(() => CompanyPatchBodies().Any(b => b.Contains(ScaleBarcodeRulesSync.CloudKey)), 4000);

        CompanyPatchBodies().Should().Contain(b => b.Contains(ScaleBarcodeRulesSync.CloudKey) && b.Contains("23"), "изменённое правило уходит в облако");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3), "правило весов уходит через ~0,5 с, а не через обычные 3 с");
    }

    [Fact]
    public async Task Peer_hint_settings_reads_the_cloud_now()
    {
        StubCloud(OwnerRules());
        await LoginAndPullAsync();
        SettingsCloudSync.StartCompanyPolling();
        _p.ScaleAmountPrefixes.Should().Be("21");

        // Владелец поменял правило: 25 — сумма, 21 — вес. Сосед по сети (программа владельца) шлёт подсказку «settings».
        StubCloud(OwnerRules(amount: "25", weight: "21"));
        await Task.Delay(3100); // пинок не чаще раза в 3 с
        SyncCenter.OnPeerHint("settings");
        await WaitFor(() => _p.ScaleAmountPrefixes == "25", 8000);

        _p.ScaleAmountPrefixes.Should().Be("25", "по подсказке соседа касса читает облако сразу, а не через такт 30 с");
        WeightBarcodeParser.AmountPrefixes.Should().Contain("25");
        WeightBarcodeParser.WeightPrefixes.Should().Contain("21", "правило применено к разбору этикеток без перезапуска");
    }

    [Fact]
    public void Build_is_deterministic_and_round_trips()
    {
        var rules = OwnerRules();
        var p = new UserPreferences();
        ScaleBarcodeRulesSync.TryApply(p, rules).Should().BeTrue();
        p.ScaleBarcodeRulesShared.Should().Be(rules, "одни и те же правила — одна и та же строка (иначе облако считало бы их изменёнными)");
        JsonNode.Parse(rules)!["v"]!.GetValue<int>().Should().Be(ScaleBarcodeRulesSync.FormatVersion);
    }
}
