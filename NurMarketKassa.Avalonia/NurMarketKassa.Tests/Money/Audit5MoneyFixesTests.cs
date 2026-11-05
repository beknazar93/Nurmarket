using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-06: исправления по «Аудит №5 — Логика и деньги кассы» и «Аудит №5 — Android-касса и паритет» (зона денег и признаков сервера):
/// Л5-01 (признаки сервера не пропадают при сбое, ключ повтора не зависит от «мигания»), Л5-02 / А5-01 (шаг округления весовых — серверная единица),
/// Л5-04 / Л5-05 (архив поправок только по долговым возвратам, Ж-01), Л5-12 (предохранитель: путь без id), Л5-13 (412 при удалении прихода),
/// А5-07 (часы устройства больше суток), А5-08 (имя устройства). Тесты меняют общие статические настройки — не параллельны другим.</summary>
[Collection("ServerFeatures")]
public sealed class Audit5MoneyFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-audit5-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedAccountKey = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _savedServerUnit = UserPreferences.Instance.ScaleBarcodeAmountUnit;
    private readonly string? _savedLocalUnit = UserPreferences.Instance.ScaleBarcodeAmountUnitLocal;

    public Audit5MoneyFixesTests()
    {
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests();
        ServerCapabilities.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.LastDataAccountKey = _savedAccountKey;
        UserPreferences.Instance.ScaleBarcodeAmountUnit = _savedServerUnit;
        UserPreferences.Instance.ScaleBarcodeAmountUnitLocal = _savedLocalUnit;
        ServerCapabilities.DirOverride = null;
        ServerCapabilities.SetFeaturesForTests();
        QuantityRules.WeightLineFloorEnabled = () => ServerCapabilities.Has(ServerCapabilities.WeightLineTotalFloor);
        QuantityRules.WeightStep = QuantityRules.DefaultWeightStep;
        DeviceIdentity.DirOverride = null;
        DeviceIdentity.ResetForTests();
        ServerClock.ResetForTests();
        PosApp.ActiveShiftId = null;
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => ServerCapabilities.Has(ServerCapabilities.DebtReturnDrawerCash);
        ExchangeCashCorrections.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    // ───────────── Л5-01: признаки сервера ─────────────

    private static NurMarketApiClient Client(WireMockServer server) => new(new AppSettings { ApiBaseUrl = server.Url! });

    private static async Task<NurMarketApiClient> LoggedIn(WireMockServer server)
    {
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        return api;
    }

    private static void Caps(WireMockServer server, int status, string body) =>
        server.Given(Request.Create().WithPath("/api/main/capabilities/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task Capabilities_FailedRefresh_KeepsLastGoodList()
    {
        UserPreferences.Instance.LastDataAccountKey = "audit5-" + Guid.NewGuid().ToString("N")[..6];
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Caps(server, 200, "{\"features\":[\"checkout_branch\",\"weight_line_total_floor\"]}");
        (await ServerCapabilities.LoadAsync(api, force: true)).Should().Contain("checkout_branch");
        ServerCapabilities.Has(ServerCapabilities.CheckoutBranch).Should().BeTrue();

        server.Reset();
        Caps(server, 400, "{\"detail\":\"boom\"}");
        var after = await ServerCapabilities.LoadAsync(api, force: true);
        after.Should().Contain("checkout_branch").And.Contain("weight_line_total_floor", "сбой обновления не заменяет хороший список пустым");
        ServerCapabilities.Cached(ServerCapabilities.CheckoutBranch).Should().BeTrue();
        ServerCapabilities.Cached(ServerCapabilities.WeightLineTotalFloor).Should().BeTrue();
        ServerCapabilities.IsStaleForTests.Should().BeTrue("список — последний известный, не свежий");
        var (_, error) = await ServerCapabilities.TryLoadAsync(api, force: true);
        error.Should().NotBeNull("вызывающий по-прежнему видит, что последний запрос не удался");

        // сервер снова ответил — список свежий
        server.Reset();
        Caps(server, 200, "{\"features\":[\"checkout_branch\"]}");
        (await ServerCapabilities.LoadAsync(api, force: true)).Should().BeEquivalentTo(new[] { "checkout_branch" });
        ServerCapabilities.IsStaleForTests.Should().BeFalse();
    }

    [Fact]
    public async Task Capabilities_AfterRestart_WithoutConnection_UsesListFromDisk()
    {
        UserPreferences.Instance.LastDataAccountKey = "audit5-" + Guid.NewGuid().ToString("N")[..6];
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Caps(server, 200, "{\"features\":[\"weight_line_total_floor\",\"debt_return_drawer_cash\"]}");
        await ServerCapabilities.LoadAsync(api, force: true);
        File.Exists(Path.Combine(_dir, "server-capabilities.json")).Should().BeTrue();

        ServerCapabilities.ResetMemoryForTests(); // «перезапуск кассы»
        ServerCapabilities.Cached(ServerCapabilities.WeightLineTotalFloor).Should().BeTrue("список взят с диска до первого запроса");
        ServerCapabilities.Cached(ServerCapabilities.CheckoutBranch).Should().BeFalse();

        server.Reset();
        Caps(server, 400, "{}"); // и обновление не удалось
        (await ServerCapabilities.LoadAsync(api, force: true)).Should().Contain("weight_line_total_floor").And.Contain("debt_return_drawer_cash");

        // другой аккаунт чужих признаков не получает
        UserPreferences.Instance.LastDataAccountKey = "audit5-other";
        ServerCapabilities.Cached(ServerCapabilities.WeightLineTotalFloor).Should().BeNull();
    }

    [Fact]
    public async Task Capabilities_ServerAnsweredWithoutFeatures_MeansNoFeatures()
    {
        UserPreferences.Instance.LastDataAccountKey = "audit5-" + Guid.NewGuid().ToString("N")[..6];
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Caps(server, 200, "{\"features\":[\"checkout_branch\"]}");
        await ServerCapabilities.LoadAsync(api, force: true);
        server.Reset();
        Caps(server, 200, "{\"features\":[]}"); // сервер реально ответил без признаков
        (await ServerCapabilities.LoadAsync(api, force: true)).Should().BeEmpty();
        ServerCapabilities.Cached(ServerCapabilities.CheckoutBranch).Should().BeFalse();
        ServerCapabilities.IsStaleForTests.Should().BeFalse();
    }

    [Fact]
    public void QuickBodyFingerprint_IgnoresDeviceFields_AndDeviceFieldsCanBeFrozen()
    {
        DeviceIdentity.DirOverride = _dir;
        DeviceIdentity.ResetForTests();
        DeviceIdentity.NameProvider = () => "Касса 3";
        DeviceIdentity.BranchProvider = () => "11111111-1111-1111-1111-111111111111";
        var cart = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 2, ["unit_price"] = 15.0 }) }.ToJsonString();
        var shift = Guid.NewGuid().ToString();

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.CheckoutBranch);
        var with = QuickCheckoutBody.TryBuild(cart, "debt", "0", null, Guid.NewGuid().ToString(), shift, null, false, null, false, out _)!;
        ServerCapabilities.SetFeaturesForTests();
        var without = QuickCheckoutBody.TryBuild(cart, "debt", "0", null, with["client"]!.GetValue<string>(), shift, null, false, null, false, out _)!;

        with.ToJsonString().Should().NotBe(without.ToJsonString(), "тело с полями устройства и без — разное");
        QuickCheckoutBody.Fingerprint(with).Should().Be(QuickCheckoutBody.Fingerprint(without), "ключ повтора от полей устройства не зависит");
        QuickCheckoutBody.Fingerprint(with).Should().NotContain("device_key").And.NotContain("branch");

        // зафиксированные при первой отправке поля возвращаются в тело повтора
        var frozen = QuickCheckoutBody.DeviceFields(with.ToJsonString());
        frozen.Keys.Should().Contain("device_key").And.Contain("device_name").And.Contain("branch");
        QuickCheckoutBody.ReplaceDeviceFields(without, frozen);
        without.ToJsonString().Should().Be(with.ToJsonString());
        QuickCheckoutBody.ReplaceDeviceFields(with, new Dictionary<string, JsonNode?>());
        with.ToJsonString().Should().Be(QuickCheckoutBody.TryBuild(cart, "debt", "0", null, with["client"]!.GetValue<string>(), shift, null, false, null, false, out _)!.ToJsonString(),
            "без признака — тело без полей устройства");
    }

    // ───────────── Л5-01: ключ повтора неясной оплаты «в долг» не меняется от «мигания» признака ─────────────

    public interface ITestSalesApi : ISalesApiService, IPosQuickCheckoutApi { }

    public class ApiProxy : DispatchProxy
    {
        public readonly List<(string Key, JsonObject Body)> Calls = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "PosQuickCheckoutAsync" && args is { Length: >= 2 })
            {
                Calls.Add(((string)args[1]!, (JsonObject)JsonNode.Parse(((JsonObject)args[0]!).ToJsonString())!));
                // 429 «сервер занят»: запрос отклонён до обработки, ключ остаётся за чеком для повтора кассиром
                return Task.FromException<JsonElement>(new ApiException("busy", 429));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class CartProxy : DispatchProxy
    {
        public string Json = "{}";
        private JsonElement _root;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_HasCart": return true;
                case "get_LineCount": return 1;
                case "GetRawText": return Json;
                case "get_Root":
                    if (_root.ValueKind == JsonValueKind.Undefined)
                        _root = JsonDocument.Parse(Json).RootElement.Clone();
                    return _root;
                default:
                    return targetMethod is { ReturnType.IsValueType: true } && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }
    }

    private static async Task<(List<(string Key, JsonObject Body)> Calls, PosCheckoutService Service)> RunQuickAsync(PosCheckoutService? existing, ITestSalesApi api, ICartService cart, string method)
    {
        var svc = existing ?? new PosCheckoutService(cart, api, null!, null!, null!, null!, null!);
        var request = new PosCheckoutRequest { PaymentMethod = method, CashReceived = "0", PrintReceipt = false, ClientId = "22222222-2222-2222-2222-222222222222", ExpectedTotal = 30 };
        var m = typeof(PosCheckoutService).GetMethod("TryQuickCheckoutAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)m.Invoke(svc, new object[] { request, CancellationToken.None, CancellationToken.None, TimeSpan.FromSeconds(30) })!;
        await task;
        return (((ApiProxy)(object)api).Calls, svc);
    }

    [Theory]
    [InlineData(true)]  // признак был, «мигнул» и пропал
    [InlineData(false)] // признака не было, появился
    public async Task QuickPayment_RetryKeepsKeyAndBody_WhenCheckoutBranchFlickers(bool featureAtStart)
    {
        DeviceIdentity.DirOverride = _dir;
        DeviceIdentity.ResetForTests();
        DeviceIdentity.NameProvider = () => "Касса 7";
        DeviceIdentity.BranchProvider = () => null;
        PosApp.ActiveShiftId = Guid.NewGuid().ToString();
        QuantityRules.WeightLineFloorEnabled = () => false;
        var cartJson = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 3, ["unit_price"] = 10.0 }) }.ToJsonString();
        var cart = DispatchProxy.Create<ICartService, CartProxy>();
        ((CartProxy)(object)cart).Json = cartJson;
        var api = DispatchProxy.Create<ITestSalesApi, ApiProxy>();

        if (featureAtStart)
            ServerCapabilities.SetFeaturesForTests(ServerCapabilities.CheckoutBranch);
        var (calls, svc) = await RunQuickAsync(null, api, cart, "debt");
        calls.Should().HaveCount(2, "429 дважды подряд — «сервер занят», выбор кассира");

        // обновление признаков не удалось — раньше список становился пустым (или, наоборот, признак появлялся) и тело менялось
        if (featureAtStart)
            ServerCapabilities.SetFeaturesForTests();
        else
            ServerCapabilities.SetFeaturesForTests(ServerCapabilities.CheckoutBranch);
        await RunQuickAsync(svc, api, cart, "debt");

        calls.Should().HaveCount(4);
        calls.Select(c => c.Key).Distinct().Should().HaveCount(1, "повтор той же оплаты — тем же ключом, иначе «в долг» мог пройти дважды");
        calls.Select(c => c.Body.ToJsonString()).Distinct().Should().HaveCount(1, "поля устройства зафиксированы при создании ключа");
        calls[0].Body.ContainsKey("device_key").Should().Be(featureAtStart);
    }

    // ───────────── Л5-02 / А5-01: шаг округления весовых — серверная единица ─────────────

    private static CartTotalsCalculator.CartTotals CalcWeighed(double qty, double price)
    {
        var cart = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product_id"] = "p1", ["quantity"] = qty, ["unit_price"] = price, ["server_is_weight"] = true, ["is_weight"] = true }) };
        using var doc = JsonDocument.Parse(cart.ToJsonString());
        return CartTotalsCalculator.Calculate(doc.RootElement);
    }

    [Theory]
    [InlineData("som", "tiyin", 768.00)]  // на сайте «сомы», на кассе «тыйыны» (клиент с весами TM-30F): итог — по серверному правилу
    [InlineData("tiyin", "som", 768.38)]  // на сайте «тыйыны», на кассе «сомы»: итог не режется до сома
    [InlineData("som", null, 768.00)]
    [InlineData("tiyin", null, 768.38)]
    [InlineData("som", "som", 768.00)]
    [InlineData("tiyin", "tiyin", 768.38)]
    public void WeightedTotal_FollowsServerUnit_NotLocalChoice(string server, string? local, double expected)
    {
        UserPreferences.Instance.ScaleBarcodeAmountUnit = server;
        UserPreferences.Instance.ScaleBarcodeAmountUnitLocal = local;
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = QuantityRules.DefaultWeightStep; // правило по умолчанию, а не подмена теста

        // 3,840 кг × 200,10 = 768,384
        var totals = CalcWeighed(3.840, 200.10);
        totals.TotalDue.Should().Be(expected);

        // тело запроса считает так же (итог кассы = итог сервера, и «верните/доберите» после оплаты не возникает)
        var cartJson = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 3.840, ["unit_price"] = 200.10, ["server_is_weight"] = true, ["is_weight"] = true }) }.ToJsonString();
        var body = QuickCheckoutBody.TryBuild(cartJson, "cash", "1000", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
        body.Should().NotBeNull();
        QuickCheckoutBody.ComputeTotal(body!).Should().Be(expected);
    }

    [Fact]
    public void LocalUnit_StillChangesBarcodeReading_ButNotMoney()
    {
        UserPreferences.Instance.ScaleBarcodeAmountUnit = "som";
        UserPreferences.Instance.ScaleBarcodeAmountUnitLocal = "tiyin";
        UserPreferences.Instance.EffectiveScaleAmountUnit.Should().Be("tiyin", "разбор этикетки — по выбору кассы");
        QuantityRules.DefaultWeightStep().Should().Be(1m, "деньги чека — по серверной единице");
    }

    [Fact]
    public void RoundingRow_IsShown_WhenStepIsWholeSom()
    {
        UserPreferences.Instance.ScaleBarcodeAmountUnit = "som";
        UserPreferences.Instance.ScaleBarcodeAmountUnitLocal = null;
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = QuantityRules.DefaultWeightStep;
        var totals = CalcWeighed(3.840, 200.10);
        totals.RoundingAdjustment.Should().Be(-0.38m, "строки чека (768,38) не сходятся с итогом (768) — «Округление весовых» не прячется");
    }

    // ───────────── Л5-04 / Л5-05: поправки «Касса:» ─────────────

    private const string DrawerShift = "shift-audit5";

    private static string ArchivePath => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "exchange_cash_fix.archived.json");

    [Fact]
    public void Archive_TakesOnlyDebtRefundCorrections_ExchangeAndPrepayStayWorking()
    {
        ExchangeCashCorrections.ResetForTests();
        var shift = DrawerShift + "-a";
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => false;
        ExchangeCashCorrections.RecordIfKnownServerError(shift, "refund:old-1", overstated: 5m, suspect: 5m, "тест", debtRefund: true);
        ExchangeCashCorrections.RecordAdjustment(shift, "exchange-overcredit:ex-1", -4m, "тест обмена");
        ExchangeCashCorrections.RecordIfKnownServerError(shift, "debtprepay:z", overstated: 3m, suspect: 3m, "тест предоплаты");
        ExchangeCashCorrections.ForShift(shift).Should().Be(-12m);

        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => true; // обновление до сервера с debt_return_drawer_cash
        ExchangeCashCorrections.ForShift(shift).Should().Be(-7m, "возврат долгового чека (−5) в архиве; обмен (−4) и предоплата безналом (−3) остаются");
        var archive = File.ReadAllText(ArchivePath);
        archive.Should().Contain("refund:old-1").And.NotContain("exchange-overcredit").And.NotContain("debtprepay").And.Contain("\"Version\": 2");
    }

    [Fact]
    public void Archive_Version1From1_17_76_ReturnsExchangeAndPrepayToWork()
    {
        ExchangeCashCorrections.ResetForTests();
        var shift = DrawerShift + "-b";
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => true;
        object E(string key, decimal amount) => new { ShiftId = shift, ExchangeId = key, Amount = amount, CreatedAt = DateTime.Now.AddHours(-2) };
        var v1 = JsonSerializer.Serialize(new { ArchivedAt = DateTime.Now.AddHours(-1), Reason = "1.17.76", Entries = new[] { E("refund:a", -5m), E("debtprepay:z", -3m), E("exch-guid-1", -2m) } });
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        File.WriteAllText(ArchivePath, v1);

        ExchangeCashCorrections.ForShift(shift).Should().Be(-5m, "предоплата (−3) и обмен (−2) вернулись в работу, возврат долгового чека (−5) остался в архиве");
        var archive = File.ReadAllText(ArchivePath);
        archive.Should().Contain("refund:a").And.NotContain("debtprepay").And.Contain("\"Version\": 2");
        ExchangeCashCorrections.ForShift(shift).Should().Be(-5m, "повторный пересчёт ничего не меняет");
    }

    [Fact]
    public void ZhOne_CashierGaveMoreThanServerRecorded_CorrectionStillWorksWithFeature()
    {
        ExchangeCashCorrections.ResetForTests();
        var shift = DrawerShift + "-c";
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => true;

        // чек как 001967: касса выдала 2, сервер записал 1 и снял с ожидаемой наличности 1 → остаток завышен на 1
        ExchangeCashCorrections.RecordServerUnderRecordedDebtRefund(shift, "k1", expectedBefore: 100m, expectedAfter: 99m, cashOut: 2m, serverCashOut: 1m).Should().BeTrue();
        ExchangeCashCorrections.ForShift(shift).Should().Be(-1m);
        ExchangeCashCorrections.ForShift(shift).Should().Be(-1m, "запись «refund:» не уносится в архив при последующих пересчётах");

        // сервер снял всё выданное — поправки нет
        ExchangeCashCorrections.RecordServerUnderRecordedDebtRefund(shift + "x", "k2", 100m, 98m, 2m, 2m).Should().BeFalse();
        // сервер не вычел ничего — это не Ж-01, не пишем
        ExchangeCashCorrections.RecordServerUnderRecordedDebtRefund(shift + "y", "k3", 100m, 100m, 2m, 1m).Should().BeFalse();
        // запись сервера неизвестна
        ExchangeCashCorrections.RecordServerUnderRecordedDebtRefund(shift + "z", "k4", 100m, 99m, 2m, null).Should().BeFalse();
        ExchangeCashCorrections.ForShift(shift + "x").Should().Be(0m);
        ExchangeCashCorrections.ForShift(shift + "y").Should().Be(0m);
        ExchangeCashCorrections.ForShift(shift + "z").Should().Be(0m);
    }

    // ───────────── Л5-12: предохранитель считает пути без id ─────────────

    [Fact]
    public void BreakerPaths_DifferentIdsOfOneEndpoint_AreOnePath()
    {
        TimeoutPathTracker.NormalizePath("/api/main/pos/sales/11111111-1111-1111-1111-111111111111/").Should().Be("api/main/pos/sales/{id}");
        TimeoutPathTracker.NormalizePath("/api/main/clients/42/deals/7/").Should().Be("api/main/clients/{id}/deals/{id}");
        TimeoutPathTracker.NormalizePath("/api/main/products/").Should().Be("api/main/products");
        TimeoutPathTracker.NormalizePath(null).Should().BeEmpty();

        TimeoutPathTracker.Reset();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
            TimeoutPathTracker.CountsAsServerFailure($"/api/main/pos/sales/{Guid.NewGuid()}/", now.AddSeconds(i)).Should().BeFalse("десятки медленных pos/sales/{id} — один медленный путь, не мёртвый сервер");
        TimeoutPathTracker.CountsAsServerFailure("/api/main/products/", now.AddSeconds(9)).Should().BeTrue("второй РАЗНЫЙ путь — уже сбой сервера");
        TimeoutPathTracker.Reset();
    }

    // ───────────── Л5-13: удаление прихода при 412 ─────────────

    [Fact]
    public async Task DeleteReceipt_412_DoesNotDeleteNewVersion_UntilUserConfirmsAgain()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        // приход правили на другом устройстве: теперь версия 4 и сумма другая
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r9/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r9\",\"version\":4,\"total\":\"1500.00\",\"items\":[]}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r9/").UsingMethod("DELETE").WithHeader("If-Match", "\"4\"")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r9/").UsingMethod("DELETE")).AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(412).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"version_conflict\"}"));
        var svc = new SupplierApiService(api);

        var first = async () => await svc.DeleteReceiptAsync("r9", "причина теста", "key-old", default, knownVersion: 3);
        var ex = (await first.Should().ThrowAsync<ReceiptChangedException>()).Which;
        ex.CurrentVersion.Should().Be(4);
        ex.KnownVersion.Should().Be(3);
        ex.Current.Should().NotBeNull();
        server.LogEntries.Count(e => e.RequestMessage.Method == "DELETE").Should().Be(1, "касса НЕ повторяет удаление молча — новая версия не удалена");

        // пользователь увидел расхождение и подтвердил заново — удаление с новой версией и НОВЫМ ключом
        await svc.DeleteReceiptAsync("r9", "причина теста", "key-new", default, knownVersion: ex.CurrentVersion);
        var deletes = server.LogEntries.Where(e => e.RequestMessage.Method == "DELETE").ToList();
        deletes.Should().HaveCount(2);
        deletes[1].RequestMessage.Headers!["If-Match"].Single().Should().Be("\"4\"");
        deletes[1].RequestMessage.Headers!["Idempotency-Key"].Single().Should().Be("key-new").And.NotBe(deletes[0].RequestMessage.Headers!["Idempotency-Key"].Single());
    }

    // ───────────── А5-07: часы устройства больше суток ─────────────

    private static void Observe(TimeSpan serverMinusPc)
    {
        var sent = DateTime.UtcNow;
        ServerClock.Observe(new DateTimeOffset(sent + serverMinusPc, TimeSpan.Zero), sent, sent.AddMilliseconds(100));
    }

    [Fact]
    public void ServerClock_ConsistentSamplesBeyondOneDay_AreApplied_AndWarnOnce()
    {
        ServerClock.ResetForTests();
        var warned = new List<TimeSpan>();
        void Handler(TimeSpan d) => warned.Add(d);
        ServerClock.WrongClockDetected += Handler;
        try
        {
            var shift = TimeSpan.FromDays(420); // терминал включили с датой «год назад»
            Observe(shift);
            Observe(shift);
            ServerClock.Skew.Should().Be(TimeSpan.Zero, "две пробы — ещё не согласие");
            Observe(shift);
            ServerClock.Skew.TotalDays.Should().BeApproximately(420, 0.01);
            ServerClock.ClockLooksWrong.Should().BeTrue();
            Observe(shift);
            warned.Should().HaveCount(1, "предупреждение — раз в 30 минут");
            warned[0].TotalDays.Should().BeApproximately(420, 0.01);

            // sold_at офлайн-чека — со сдвигом больше суток (раньше 30 минут максимум → «старше 7 суток»)
            var created = new DateTimeOffset(2025, 1, 1, 10, 0, 0, TimeSpan.Zero);
            var sold = ServerClock.SoldAt(created, ServerClock.Skew.TotalSeconds, created.AddDays(430));
            (sold - created).TotalDays.Should().BeApproximately(420, 0.01);
            // сдвиг в пределах суток по-прежнему не больше 30 минут
            ServerClock.SoldAt(created, 7200, created.AddDays(1)).Should().Be(created.AddMinutes(30));
        }
        finally
        {
            ServerClock.WrongClockDetected -= Handler;
        }
    }

    [Fact]
    public void ServerClock_SingleOutlier_BeyondOneDay_IsIgnored()
    {
        ServerClock.ResetForTests();
        Observe(TimeSpan.FromDays(3));
        Observe(TimeSpan.Zero);
        Observe(TimeSpan.Zero);
        Observe(TimeSpan.Zero);
        ServerClock.Skew.Should().Be(TimeSpan.Zero, "разовый сбой заголовка Date поправку не двигает");
        ServerClock.ClockLooksWrong.Should().BeFalse();
    }

    // ───────────── А5-08: имя устройства на Android ─────────────

    [Fact]
    public void DeviceName_OnAndroid_IsNotLocalhost()
    {
        DeviceIdentity.DefaultName("PC-KASSA", isAndroid: false, () => "kabcdef1234567890").Should().Be("PC-KASSA");
        DeviceIdentity.DefaultName("localhost", isAndroid: true, () => "kabcdef1234567890").Should().Be("Android 7890");
        DeviceIdentity.DefaultName("localhost", isAndroid: false, () => "k0000000000001111").Should().Be("Android 1111");
    }
}
