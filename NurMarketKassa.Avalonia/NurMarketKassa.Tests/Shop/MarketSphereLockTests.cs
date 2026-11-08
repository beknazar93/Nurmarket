using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-08, владелец: «Вид магазина меняется только через техподдержку». Правила замка (MarketSphereLock): первый выбор на новой
/// установке, работающие кассы под замком, назначение техподдержкой через общие настройки компании (ключ SupportMarketSphere, по метке один раз),
/// разрешение поддержки (allow_local_change) и сеанс удалённой помощи. Без сети; настройки — общий UserPreferences.Instance, значения возвращаются.</summary>
[Collection("UserPreferences")]
public sealed class MarketSphereLockTests : IDisposable
{
    private readonly string _sphere;
    private readonly string? _detail;
    private readonly bool _pending;
    private readonly string? _stamp;

    public MarketSphereLockTests()
    {
        var p = UserPreferences.Instance;
        _sphere = p.MarketSphere;
        _detail = p.MarketSphereDetail;
        _pending = p.MarketSphereChoicePending;
        _stamp = p.SupportSphereAppliedStamp;
        MarketSphereLock.ResetForTests();
        SetServerSphere(null);
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.MarketSphereChoicePending = _pending;
        p.SupportSphereAppliedStamp = _stamp;
        MarketSphereLock.ResetForTests();
        SetServerSphere(null);
    }

    private static void SetServerSphere(string? sphere)
    {
        var json = sphere is null ? "{}" : "{\"market_sphere\":\"" + sphere + "\"}";
        using var doc = JsonDocument.Parse(json);
        MarketSphereSync.RememberFromCompanyJson("co-test", doc.RootElement);
    }

    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    // pending, serverSphere, unlockedBySupport, supportSession → можно менять
    [InlineData(true, null, false, false, true)]       // новая установка, вид ещё нигде не выбран — первый выбор
    [InlineData(false, null, false, false, false)]     // работающая касса (поля в файле нет) — замок
    [InlineData(true, "clothing", false, false, false)] // у компании вид уже задан — замок и на новой установке
    [InlineData(false, "grocery", false, false, false)]
    [InlineData(false, null, true, false, true)]       // поддержка разрешила
    [InlineData(false, "grocery", false, true, true)]  // идёт сеанс удалённой помощи
    public void Lock_rules(bool pending, string? server, bool unlocked, bool session, bool expected) =>
        MarketSphereLock.CanChangeLocally(pending, server, unlocked, session).Should().Be(expected);

    [Fact]
    public void Existing_till_is_locked_and_keeps_its_sphere()
    {
        var p = UserPreferences.Instance;
        p.MarketSphereChoicePending = false; // у работающих касс в файле этого поля нет → false
        p.MarketSphere = MarketSpheres.Clothing;
        p.MarketSphereDetail = null;

        MarketSphereLock.CanChangeNow.Should().BeFalse();
        // общий документ без ключа поддержки — ничего не меняется
        MarketSphereLock.ApplyFromCompanyDoc(Doc("{\"StoreName\":\"Тест\"}")).Should().BeFalse();
        p.MarketSphere.Should().Be(MarketSpheres.Clothing);
        MarketSphereLock.CanChangeNow.Should().BeFalse();
    }

    [Fact]
    public void First_run_choice_is_allowed_once()
    {
        var p = UserPreferences.Instance;
        p.MarketSphereChoicePending = true;
        MarketSphereLock.CanChangeNow.Should().BeTrue();
        MarketSphereLock.IsFirstChoice.Should().BeTrue();

        MarketSphereLock.NoteLocalChoice();

        p.MarketSphereChoicePending.Should().BeFalse();
        MarketSphereLock.CanChangeNow.Should().BeFalse("после первого выбора вид меняет только техподдержка");
    }

    [Fact]
    public void Parse_understands_object_string_and_rejects_garbage()
    {
        MarketSphereLock.Parse(JsonNode.Parse("{\"sphere\":\"Clothing\",\"stamp\":\"s1\",\"allow_local_change\":true,\"by\":\"Айбек\"}"))
            .Should().Be(new MarketSphereLock.Assignment("clothing", "s1", true, "Айбек"));
        MarketSphereLock.Parse(JsonValue.Create("{\"sphere\":\"hardware\",\"allow_local_change\":\"true\"}"))
            .Should().Be(new MarketSphereLock.Assignment("hardware", "hardware", true, null), "без stamp меткой служит сама сфера");
        MarketSphereLock.Parse(JsonNode.Parse("{\"sphere\":\"pharmacy\",\"stamp\":\"s\"}")).Should().BeNull("неизвестная каталогу сфера не применяется");
        MarketSphereLock.Parse(JsonNode.Parse("{\"stamp\":\"s\"}")).Should().BeNull();
        MarketSphereLock.Parse(JsonValue.Create("не json")).Should().BeNull();
        MarketSphereLock.Parse(null).Should().BeNull();
    }

    [Fact]
    public void Remote_assignment_applies_once_per_stamp_including_exact_sphere()
    {
        var p = UserPreferences.Instance;
        p.MarketSphereChoicePending = false;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        p.SupportSphereAppliedStamp = null;
        var events = 0;
        void OnChanged() => events++;
        MarketSphereLock.Changed += OnChanged;
        try
        {
            var doc = Doc("{\"SupportMarketSphere\":{\"sphere\":\"clothing\",\"stamp\":\"2026-11-08T10:00:00Z\",\"by\":\"оператор\"}}");
            MarketSphereLock.ApplyFromCompanyDoc(doc).Should().BeTrue();
            p.MarketSphere.Should().Be(MarketSpheres.Clothing);
            p.SupportSphereAppliedStamp.Should().Be("2026-11-08T10:00:00Z");
            events.Should().BeGreaterThan(0);
            MarketSphereLock.CanChangeNow.Should().BeFalse("назначение не снимает замок");

            // Вид поменяли во время сеанса поддержки — повторное чтение того же назначения его не откатывает.
            MarketSpheres.Set(MarketSpheres.Services);
            MarketSphereLock.ApplyFromCompanyDoc(doc).Should().BeFalse();
            p.MarketSphere.Should().Be(MarketSpheres.Services);

            // Новое назначение (новая метка) — точная сфера «Хозтовары»: база grocery + detail hardware.
            MarketSphereLock.ApplyFromCompanyDoc(Doc("{\"SupportMarketSphere\":\"{\\\"sphere\\\":\\\"hardware\\\",\\\"stamp\\\":\\\"s2\\\"}\"}")).Should().BeTrue();
            p.MarketSphere.Should().Be(MarketSpheres.Grocery);
            p.MarketSphereDetail.Should().Be(MarketSpheres.Hardware);
            MarketSpheres.IsHardware.Should().BeTrue();
        }
        finally
        {
            MarketSphereLock.Changed -= OnChanged;
        }
    }

    [Fact]
    public void Remote_assignment_ends_first_run_choice()
    {
        var p = UserPreferences.Instance;
        p.MarketSphereChoicePending = true;
        p.SupportSphereAppliedStamp = null;
        MarketSphereLock.ApplyFromCompanyDoc(Doc("{\"SupportMarketSphere\":{\"sphere\":\"services\",\"stamp\":\"x1\"}}"));
        p.MarketSphereChoicePending.Should().BeFalse("вид назначен — окно первого запуска больше не спрашивает");
        p.MarketSphere.Should().Be(MarketSpheres.Services);
    }

    [Fact]
    public void Support_can_unlock_via_flag_and_lock_again()
    {
        var p = UserPreferences.Instance;
        p.MarketSphereChoicePending = false;
        p.SupportSphereAppliedStamp = "u1";
        MarketSphereLock.ApplyFromCompanyDoc(Doc("{\"SupportMarketSphere\":{\"sphere\":\"grocery\",\"stamp\":\"u1\",\"allow_local_change\":true}}"));
        MarketSphereLock.UnlockedBySupport.Should().BeTrue();
        MarketSphereLock.CanChangeNow.Should().BeTrue();
        MarketSphereLock.IsFirstChoice.Should().BeFalse();

        MarketSphereLock.ApplyFromCompanyDoc(Doc("{}"));
        MarketSphereLock.UnlockedBySupport.Should().BeFalse("ключ убрали — замок вернулся");
        MarketSphereLock.CanChangeNow.Should().BeFalse();
    }

    [Fact]
    public void Support_session_unlocks_while_operator_is_connected()
    {
        UserPreferences.Instance.MarketSphereChoicePending = false;
        SetServerSphere("grocery");
        MarketSphereLock.CanChangeNow.Should().BeFalse();
        MarketSphereLock.SupportSessionActive = true;
        MarketSphereLock.CanChangeNow.Should().BeTrue();
        MarketSphereLock.SupportSessionActive = false;
        MarketSphereLock.CanChangeNow.Should().BeFalse();
    }

    [Fact]
    public void Cloud_sync_never_sends_the_support_key()
    {
        var keys = (string[])typeof(SettingsCloudSync).GetField("CompanyKeys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        keys.Should().NotContain(MarketSphereLock.CloudKey, "ключ назначения пишет только техподдержка; касса его только читает");
        typeof(UserPreferences).GetProperty(MarketSphereLock.CloudKey).Should().BeNull("нет свойства с тем же именем — облачная синхронизация по отражению его не подхватит");
    }
}
