using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using NurMarketKassa.ViewModels;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-14 (ответы Умара 13.11 вечер, метка qf1311): тексты ошибок строймаркета (упаковки, шаг/минимум, объект, лимит долга) на 5 языках, поиск серийника по хвосту, гарантия из ответа
/// pos/checkout/, «Возвраты в счёт долга» смены, «Возвращён частично» в истории чеков, поля object / override_credit_limit в теле продажи.</summary>
[Collection("UserPreferences")]
public sealed class Qf1311AnswersTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly FakeNurCrmServer _fake = new();

    public Qf1311AnswersTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        _fake.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ApiException Err(int status, string body) => new("server", status, J(body));

    // ───────────── 1. ошибки строймаркета ─────────────

    [Fact]
    public void SaleUnitConflict_And_NotFound_AreExplained()
    {
        ServerErrorCodes.Friendly(Err(409, """{"code":"sale_unit_conflict","index":1}"""))!.Should().Contain("Строка 2").And.Contain("разных единицах");
        ServerErrorCodes.Friendly(Err(404, """{"code":"sale_unit_not_found"}"""))!.Should().Contain("Упаковка не найдена");
    }

    [Fact]
    public void StepAndMin_TakeNumbersFromData()
    {
        ServerErrorCodes.Friendly(Err(400, """{"code":"quantity_step_mismatch","data":{"step":"0.5"}}"""))!.Should().Contain("шаг 0.5");
        ServerErrorCodes.Friendly(Err(400, """{"code":"quantity_below_min","data":{"min":2}}"""))!.Should().Contain("минимум 2");
        // Ошибка строки в массиве errors (формат pos/checkout/).
        ServerErrorCodes.Friendly(Err(400, """{"errors":[{"code":"quantity_below_min","index":2,"data":{"min":"1.5"}}]}"""))!.Should().Contain("Строка 3").And.Contain("минимум 1.5");
    }

    [Fact]
    public void ObjectNotFound_404_IsNotShownAsMissingCart()
    {
        var ex = Err(404, """{"code":"object_not_found"}""");
        ServerErrorCodes.Friendly(ex)!.Should().Contain("Объект клиента не найден");
        PaymentErrorMessages.ForCashier(ex).Should().Contain("Объект клиента не найден").And.NotContain("корзину", "общий текст про корзину у 404 не должен перекрывать понятную ошибку");
    }

    [Fact]
    public void CreditLimit_Text_ForCashier_AndForOwner()
    {
        var ex = Err(409, """{"code":"credit_limit_exceeded","data":{"limit":"10000.00","debt":"9500.00","requested":"1200.00"}}""");
        var cashier = ServerErrorCodes.HardwareFriendly(ex, ownerCanOverride: false)!;
        cashier.Should().Contain("10000.00").And.Contain("9500.00").And.Contain("1200.00").And.Contain("позовите владельца");
        var owner = ServerErrorCodes.HardwareFriendly(ex, ownerCanOverride: true)!;
        owner.Should().Contain("10000.00").And.Contain("провести продажу с превышением");
        ServerErrorCodes.IsCreditLimitExceeded(ex).Should().BeTrue();
        ServerErrorCodes.IsCreditLimitExceeded(Err(409, """{"code":"duplicate_line"}""")).Should().BeFalse();
    }

    [Fact]
    public void HardwareErrors_AreTranslatedIntoAllFiveLanguages()
    {
        var bodies = new[]
        {
            """{"code":"sale_unit_conflict"}""", """{"code":"sale_unit_not_found"}""", """{"code":"quantity_step_mismatch","data":{"step":"0.5"}}""",
            """{"code":"quantity_below_min","data":{"min":2}}""", """{"code":"object_not_found"}""",
            """{"code":"credit_limit_exceeded","data":{"limit":"1","debt":"2","requested":"3"}}""",
        };
        foreach (var body in bodies)
        {
            var seen = new HashSet<string>();
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                UserPreferences.Instance.Language = lang;
                seen.Add(ServerErrorCodes.HardwareFriendly(Err(400, body))!);
            }
            seen.Should().HaveCount(5, "на каждом языке свой текст: " + body);
        }
        UserPreferences.Instance.Language = AppLanguage.Russian;
        SaleUnitNotSavedException.Text.Should().Contain("не сохранена на сервере");
    }

    // ───────────── 3. серийники: поиск по хвосту, гарантия из ответа checkout ─────────────

    [Fact]
    public async Task SerialTailLookup_AsksServerWithTail_AndExactStaysSerial()
    {
        _fake.Json("GET", "/api/main/serials/", 200, """{"serial":"1234","sold":true,"results":[{"serial":"490154203231234","sale_id":"s-1","sale_number":7,"sale_status":"paid","name":"Телефон"}]}""");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);

        var found = await sales.SerialTailLookupAsync(" 1234 ");
        await sales.SerialLookupAsync("AAA11111");

        var calls = Requests(_fake, "GET", "/api/main/serials/");
        calls[0].RequestMessage.Query!["tail"].Single().Should().Be("1234");
        calls[0].RequestMessage.Query!.ContainsKey("serial").Should().BeFalse();
        calls[1].RequestMessage.Query!["serial"].Single().Should().Be("AAA11111");
        found.Results.Should().ContainSingle().Which.Serial.Should().Be("490154203231234");
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("490154203237518", true)]
    [InlineData("123", false)]       // короче 4
    [InlineData("12ab", false)]      // буквенный номер ищется точно
    [InlineData("12 34", false)]
    [InlineData("", false)]
    public void IsTailQuery_FourOrMoreDigits(string query, bool expected) => ServerSerials.IsTailQuery(query).Should().Be(expected);

    [Fact]
    public void CheckoutResponse_SerialsAndWarrantyUntil_GoIntoSnapshot_AndCard()
    {
        var pid = "11111111-2222-3333-4444-555555555555";
        var cart = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = "l1", ["product_id"] = pid, ["product_name"] = "Телефон X1", ["quantity"] = 1, ["unit_price"] = 20000.0 }) }.ToJsonString();
        var response = J("{\"id\":\"s-9\",\"number\":1250,\"items\":[{\"product_id\":\"" + pid + "\",\"quantity\":\"1\",\"serial_numbers\":[\"490154203237518\"],\"warranty_until\":\"2027-11-13\"}]}");

        var enriched = ServerSerials.WithServerLines(cart, response);

        var line = JsonDocument.Parse(enriched).RootElement.GetProperty("items")[0];
        line.GetProperty("warranty_until").GetString().Should().Be("2027-11-13");
        ServerSerials.LineSerials(line).Should().Equal("490154203237518");
        var records = SerialJournal.ExtractFromCart(enriched, "s-9", "1250", new DateTime(2026, 11, 13));
        records.Should().ContainSingle().Which.WarrantyUntil.Should().Be(new DateTime(2027, 11, 13), "«гарантия до» — из ответа сервера, не пересчёт кассы");
        records[0].WarrantyMonths.Should().Be(12);

        var p = UserPreferences.Instance;
        var (sphere, detail) = (p.MarketSphere, p.MarketSphereDetail);
        try
        {
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = ElectronicsProfile.Id;
            var lines = WarrantyCard.ItemLines(line, new DateTime(2026, 11, 14), 32);
            lines.Should().Contain("IMEI: 490154203237518").And.Contain("Гарантия до 13.11.2027");
        }
        finally
        {
            p.MarketSphere = sphere;
            p.MarketSphereDetail = detail;
        }

        // Ответ без полей — снимок без изменений.
        ServerSerials.WithServerLines(cart, J("{\"id\":\"s-9\",\"items\":[{\"product_id\":\"" + pid + "\",\"quantity\":\"1\"}]}")).Should().Be(cart);
    }

    // ───────────── 5. смена: возвраты в счёт долга ─────────────

    [Fact]
    public void ShiftReport_ReadsReturnsDebtOffset_AndCount()
    {
        var report = ServerShiftReport.TryParse(J("""{"shift":"x","status":"open","sales_total":"100","returns_total":"30","returns_count":2,"returns_debt_offset":"12.50","returns_debt_offset_count":1,"expected_cash":"500"}"""))!;
        report.ReturnsDebtOffset.Should().Be(12.5m);
        report.ReturnsDebtOffsetCount.Should().Be(1);
        report.ExpectedCash.Should().Be(500m, "в «в ящике должно быть» эта сумма не входит — цифру ожидаемого сервера не трогаем");

        var old = ServerShiftReport.TryParse(J("""{"shift":"x","status":"open","sales_total":"100"}"""))!;
        old.ReturnsDebtOffset.Should().BeNull("старый сервер без поля");
        old.ReturnsDebtOffsetCount.Should().BeNull();
    }

    // ───────────── 4. уровень цен: quote и строки без упаковки ─────────────

    [Fact]
    public void PriceLevel_QuoteSkipsPacksVariantsCustomAndLinesWithoutProduct()
    {
        var plain = J("""{"id":"i1","product_id":"p1","quantity":1,"unit_price":100}""");
        var pack = J("""{"id":"i2","product_id":"p1","quantity":1,"unit_price":749,"sale_unit":"7"}""");
        var package = J("""{"id":"i3","product_id":"p1","quantity":1,"unit_price":50,"sale_package_id":"pk-1"}""");
        var variant = J("""{"id":"i4","product_id":"p1","quantity":1,"unit_price":900,"server_variant_id":"v-1"}""");
        var noProduct = J("""{"id":"i5","quantity":1,"unit_price":10}""");
        ReceiptSnapshotCartEditor.IsPriceLevelEligible(plain).Should().BeTrue();
        ReceiptSnapshotCartEditor.IsPriceLevelEligible(pack).Should().BeFalse("quote про единицы продажи не знает");
        ReceiptSnapshotCartEditor.IsPriceLevelEligible(package).Should().BeFalse();
        ReceiptSnapshotCartEditor.IsPriceLevelEligible(variant).Should().BeFalse();
        ReceiptSnapshotCartEditor.IsPriceLevelEligible(noProduct).Should().BeFalse();
    }

    [Fact]
    public async Task PriceLevel_QuoteAnswerWithAppliedFlag_GivesPricesPerProduct()
    {
        _fake.Json("POST", "/api/main/price-levels/quote/", 200,
            """{"applied":true,"items":[{"product":"p1","qty":"10","price":"90.00","price_source":"level:foreman","base_price":"100.00"},{"product":"p2","qty":"1","price":"50.00","price_source":"retail","base_price":"50.00"}]}""");
        var api = await LoggedInAsync(_fake);

        var prices = await new PriceLevelsApi(api).QuoteAsync("c1", new[] { ("p1", 10d), ("p2", 1d) });

        prices["p1"].Should().Be(90m);
        prices["p2"].Should().Be(50m);
        var sent = BodyOf(Requests(_fake, "POST", "/price-levels/quote/").Single());
        sent["client"]!.ToString().Should().Be("c1");
        sent["items"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task ClientObjects_OnlyActiveOnes_AreOffered()
    {
        _fake.Json("GET", "/api/main/clients/c1/objects/", 200,
            """[{"id":1,"name":"Дом на Ленина","address":"ул. Ленина 5","is_active":true},{"id":2,"name":"Старый склад","address":"","is_active":false}]""");
        var api = await LoggedInAsync(_fake);

        var list = await new PriceLevelsApi(api).GetObjectsAsync("c1");

        list.Should().ContainSingle().Which.Should().Match<ClientObjectInfo>(o => o.Id == "1" && o.DisplayName == "Дом на Ленина — ул. Ленина 5");
    }

    // ───────────── 4. окно оплаты: выбор объекта прораба ─────────────

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Checkout_ClientObjects_AreLoadedForHardwareClient_AndSelectionGoesToTheBody()
    {
        var p = UserPreferences.Instance;
        var (sphere, detail) = (p.MarketSphere, p.MarketSphereDetail);
        var oldLoader = CheckoutViewModel.ClientObjectsLoader;
        try
        {
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = "hardware";
            CheckoutViewModel.ClientObjectsLoader = (clientId, _) => Task.FromResult<IReadOnlyList<ClientObjectInfo>>(
                clientId == "c1" ? new[] { new ClientObjectInfo("1", "Дом на Ленина", "ул. Ленина 5"), new ClientObjectInfo("2", "Гараж", null) } : Array.Empty<ClientObjectInfo>());
            var totals = new CartTotalsCalculator.CartTotals { Subtotal = 100, TotalDue = 100, LineCount = 1 };
            var vm = new CheckoutViewModel(totals, "", "");
            vm.ShowClientObjects.Should().BeFalse("клиента нет");

            vm.PreselectClient(new ClientOption { Id = "c1", DisplayName = "Прораб Иван" });
            await WaitUntilAsync(() => vm.ClientObjects.Count > 0);

            vm.ClientObjects.Should().HaveCount(3, "«Без объекта» + два объекта клиента");
            vm.ShowClientObjects.Should().BeTrue();
            vm.ClientObjectIdForApi.Should().BeNull("по умолчанию — без объекта: выбор необязательный");
            vm.SelectedClientObject = vm.ClientObjects[2];
            vm.ClientObjectIdForApi.Should().Be("2");

            vm.PreselectClient(new ClientOption { Id = "c2", DisplayName = "Без объектов" });
            await WaitUntilAsync(() => vm.ClientObjects.Count == 0);
            vm.ShowClientObjects.Should().BeFalse("у другого клиента объектов нет — блок скрыт, поле object не уходит");
            vm.ClientObjectIdForApi.Should().BeNull();
        }
        finally
        {
            CheckoutViewModel.ClientObjectsLoader = oldLoader;
            p.MarketSphere = sphere;
            p.MarketSphereDetail = detail;
        }
    }

    [Fact]
    public async Task Checkout_ClientObjects_NotLoaded_InOtherSpheres()
    {
        var p = UserPreferences.Instance;
        var (sphere, detail) = (p.MarketSphere, p.MarketSphereDetail);
        var oldLoader = CheckoutViewModel.ClientObjectsLoader;
        try
        {
            p.MarketSphere = MarketSpheres.Grocery;
            p.MarketSphereDetail = null;
            var asked = 0;
            CheckoutViewModel.ClientObjectsLoader = (_, _) => { asked++; return Task.FromResult<IReadOnlyList<ClientObjectInfo>>(new[] { new ClientObjectInfo("1", "Объект", null) }); };
            var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 100, TotalDue = 100, LineCount = 1 }, "", "");

            vm.PreselectClient(new ClientOption { Id = "c1", DisplayName = "Клиент" });
            await Task.Delay(200);

            asked.Should().Be(0, "объекты прораба — только магазин «Хозтовары»");
            vm.ShowClientObjects.Should().BeFalse();
        }
        finally
        {
            CheckoutViewModel.ClientObjectsLoader = oldLoader;
            p.MarketSphere = sphere;
            p.MarketSphereDetail = detail;
        }
    }

    // ───────────── 4. тело продажи: объект прораба и лимит долга ─────────────

    private static readonly string Shift = Guid.NewGuid().ToString();
    private static readonly string ClientId = Guid.NewGuid().ToString();

    private static string Cart() => new JsonObject
    {
        ["items"] = new JsonArray(new JsonObject { ["id"] = "l1", ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Цемент", ["quantity"] = 2, ["unit_price"] = 100.0 }),
    }.ToJsonString();

    [Fact]
    public void QuickBody_ObjectAndOverride_AreSentOnlyWhenAsked()
    {
        var plain = QuickCheckoutBody.TryBuild(Cart(), "debt", "0", null, ClientId, Shift, null, false, null, false, out _)!;
        plain.ContainsKey("object").Should().BeFalse();
        plain.ContainsKey("override_credit_limit").Should().BeFalse();

        var withObject = QuickCheckoutBody.TryBuild(Cart(), "debt", "0", null, ClientId, Shift, null, false, null, false, out _, objectId: "42", overrideCreditLimit: true)!;
        withObject["object"]!.ToString().Should().Be("42");
        withObject["override_credit_limit"]!.GetValue<bool>().Should().BeTrue();

        // Без клиента объекта быть не может; override — только для продажи «в долг».
        var cash = QuickCheckoutBody.TryBuild(Cart(), "cash", "1000", null, null, Shift, null, false, null, false, out _, objectId: "42", overrideCreditLimit: true)!;
        cash.ContainsKey("object").Should().BeFalse();
        cash.ContainsKey("override_credit_limit").Should().BeFalse();
        var cashWithClient = QuickCheckoutBody.TryBuild(Cart(), "cash", "1000", null, ClientId, Shift, null, false, null, false, out _, objectId: "42", overrideCreditLimit: true)!;
        cashWithClient["object"]!.ToString().Should().Be("42", "объект не зависит от способа оплаты");
        cashWithClient.ContainsKey("override_credit_limit").Should().BeFalse();
    }

    [Fact]
    public void KnownTotals_DoNotChange_WithObjectField()
    {
        var a = QuickCheckoutBody.TryBuild(Cart(), "cash", "1000", null, ClientId, Shift, null, false, null, false, out _)!;
        var b = QuickCheckoutBody.TryBuild(Cart(), "cash", "1000", null, ClientId, Shift, null, false, null, false, out _, objectId: "42")!;
        QuickCheckoutBody.ComputeTotal(b).Should().Be(QuickCheckoutBody.ComputeTotal(a), "объект — только метка продажи, итог от неё не зависит");
    }
}

/// <summary>2026-11-14: быстрый путь оплаты при отказах строймаркета — отказ ДО записи продажи, понятный текст, старый путь не включается (он продал бы мимо лимита долга).</summary>
[Collection("ServerFeatures")]
public sealed class Qf1311QuickCheckoutTests : IDisposable
{
    public interface ITestSalesApi : ISalesApiService, IPosQuickCheckoutApi { }

    public class ApiProxy : DispatchProxy
    {
        public readonly List<JsonObject> Bodies = new();
        public Func<ApiException>? Refuse;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "PosQuickCheckoutAsync" && args is { Length: >= 2 })
            {
                Bodies.Add((JsonObject)JsonNode.Parse(((JsonObject)args[0]!).ToJsonString())!);
                return Task.FromException<JsonElement>(Refuse!());
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

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-qf1311-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public Qf1311QuickCheckoutTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests();
        ServerCapabilities.Reset();
        DeviceIdentity.DirOverride = _dir;
        DeviceIdentity.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ServerCapabilities.DirOverride = null;
        ServerCapabilities.SetFeaturesForTests();
        DeviceIdentity.DirOverride = null;
        DeviceIdentity.ResetForTests();
        PosApp.ActiveShiftId = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static async Task<(PosCheckoutResult? Result, ApiProxy Api)> RunAsync(Func<ApiException> refuse, string method, bool overrideLimit = false, string? objectId = null)
    {
        PosApp.ActiveShiftId = Guid.NewGuid().ToString();
        var cartJson = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 3, ["unit_price"] = 10.0 }) }.ToJsonString();
        var cart = DispatchProxy.Create<ICartService, CartProxy>();
        ((CartProxy)(object)cart).Json = cartJson;
        var api = DispatchProxy.Create<ITestSalesApi, ApiProxy>();
        ((ApiProxy)(object)api).Refuse = refuse;
        var svc = new PosCheckoutService(cart, api, null!, null!, null!, null!, null!);
        var request = new PosCheckoutRequest
        {
            PaymentMethod = method, CashReceived = "0", PrintReceipt = false, ClientId = "22222222-2222-2222-2222-222222222222", ExpectedTotal = 30,
            ClientObjectId = objectId, OverrideCreditLimit = overrideLimit,
        };
        var m = typeof(PosCheckoutService).GetMethod("TryQuickCheckoutAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)m.Invoke(svc, new object[] { request, CancellationToken.None, CancellationToken.None, TimeSpan.FromSeconds(30) })!;
        await task;
        var result = (PosCheckoutResult?)task.GetType().GetProperty("Result")!.GetValue(task);
        return (result, (ApiProxy)(object)api);
    }

    [Fact]
    public async Task CreditLimitExceeded_409_StopsTheSale_WithFlag_AndNoFallbackToOldPath()
    {
        var (result, api) = await RunAsync(() => new ApiException("limit", 409,
            JsonDocument.Parse("""{"code":"credit_limit_exceeded","data":{"limit":"1000","debt":"900","requested":"300"}}""").RootElement.Clone()), "debt", objectId: "42");

        result.Should().NotBeNull("null отправил бы чек старым путём — мимо проверки лимита долга");
        result!.IsSuccess.Should().BeFalse();
        result.CreditLimitExceeded.Should().BeTrue();
        result.ErrorMessage.Should().Contain("лимит долга").And.Contain("1000").And.Contain("900");
        api.Bodies.Should().ContainSingle("отказ по существу — повторов нет");
        api.Bodies[0]["object"]!.ToString().Should().Be("42", "объект прораба ушёл в продажу");
        api.Bodies[0].ContainsKey("override_credit_limit").Should().BeFalse("пока владелец не подтвердил");
    }

    [Fact]
    public async Task OwnerOverride_IsSentInTheRetryBody()
    {
        var (result, api) = await RunAsync(() => new ApiException("limit", 409,
            JsonDocument.Parse("""{"code":"credit_limit_exceeded","data":{}}""").RootElement.Clone()), "debt", overrideLimit: true);

        api.Bodies.Single()["override_credit_limit"]!.GetValue<bool>().Should().BeTrue();
        result!.CreditLimitExceeded.Should().BeTrue("сервер мог снова отказать — результат тот же, касса не зацикливается");
    }

    [Theory]
    [InlineData(404, """{"code":"object_not_found"}""", "Объект клиента не найден")]
    [InlineData(404, """{"code":"sale_unit_not_found"}""", "Упаковка не найдена")]
    [InlineData(409, """{"code":"sale_unit_conflict","index":0}""", "разных единицах")]
    [InlineData(400, """{"code":"quantity_step_mismatch","data":{"step":"0.5"}}""", "шаг 0.5")]
    [InlineData(400, """{"code":"quantity_below_min","data":{"min":3}}""", "минимум 3")]
    public async Task HardwareRefusals_ReturnFriendlyFailure_NotOldPath(int status, string body, string expectedText)
    {
        var (result, api) = await RunAsync(() => new ApiException("srv", status, JsonDocument.Parse(body).RootElement.Clone()), "cash");

        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.CreditLimitExceeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedText);
        api.Bodies.Should().ContainSingle();
    }
}

/// <summary>2026-11-14 (ответ Умара 13.11 вечер, слоты): программа владельца шлёт заголовок «owner/версия» и client_type «desktop_owner» — иначе она займёт слот кассы на ПК и выбьет кассира.</summary>
[Collection("OwnerModeSerial")]
public sealed class Qf1311OwnerSlotTests : IDisposable
{
    private static readonly PropertyInfo IsOwner = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _was = AppMode.IsOwner;
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose()
    {
        IsOwner.SetValue(null, _was);
        _fake.Dispose();
    }

    [Theory]
    [InlineData(false, false, "1.17.87", false, "kassa/1.17.87")]
    [InlineData(false, true, "1.17.87", false, "kassa/1.17.87 (linux)")]
    [InlineData(true, false, "1.17.87", false, "android-pos/1.17.87")]
    [InlineData(false, false, "1.17.87", true, "owner/1.17.87")]
    [InlineData(false, true, "1.17.87", true, "owner/1.17.87")]   // владелец на Linux — всё равно owner/
    public void ClientHeader_IsOwnerForOwnerProgram(bool android, bool linux, string version, bool owner, string expected) =>
        NurMarketApiClient.BuildClientHeader(android, linux, version, owner).Should().Be(expected);

    [Fact]
    public async Task OwnerProgram_SendsOwnerHeader_AndDesktopOwnerClientType_OnTheWire()
    {
        IsOwner.SetValue(null, true);
        _fake.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });

        await api.LoginAsync("owner@test.kg", "12345678");

        var entry = _fake.Server.LogEntries.First(e => e.RequestMessage.Path.Contains("/auth/login/"));
        entry.RequestMessage.Headers!["X-NurMarket-Client"].First().Should().StartWith("owner/");
        using var body = JsonDocument.Parse(entry.RequestMessage.Body!);
        body.RootElement.GetProperty("client_type").GetString().Should().Be("desktop_owner");
    }

    [Fact]
    public async Task Kassa_SendsKassaHeader_AndDesktopPosClientType_OnTheWire()
    {
        IsOwner.SetValue(null, false);
        _fake.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });

        await api.LoginAsync("cashier@test.kg", "12345678");

        var entry = _fake.Server.LogEntries.First(e => e.RequestMessage.Path.Contains("/auth/login/"));
        entry.RequestMessage.Headers!["X-NurMarket-Client"].First().Should().StartWith("kassa/");
        using var body = JsonDocument.Parse(entry.RequestMessage.Body!);
        body.RootElement.GetProperty("client_type").GetString().Should().BeOneOf("desktop_pos", "android_pos").And.NotBe("desktop_owner");
    }

    [Fact]
    public void ClientType_IsFromTheAllowedSet()
    {
        var allowed = new[] { "mobile", "android_pos", "desktop_pos", "desktop_owner" };
        IsOwner.SetValue(null, false);
        allowed.Should().Contain(NurMarketApiClient.ClientTypeForLogin());
        IsOwner.SetValue(null, true);
        NurMarketApiClient.ClientTypeForLogin().Should().Be("desktop_owner");
    }
}
