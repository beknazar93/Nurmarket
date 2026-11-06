using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — сквозные тесты на уровне служб против поддельного NurCRM (WireMock, FakeNurCrmServer): продажа, возврат, смешанная оплата,
/// продажа в долг и погашение, смена. Клиент API, SalesApiService, ShiftApiService, PosCheckoutService, PendingIdempotencyKey, RefundAmountMath и
/// ShiftExpectedCash — настоящие; печать, шина сообщений, состояние смены — заглушки; окон нет (сквозные через окна — В14). Проверяется то, что уходит
/// на сервер (путь, тело, ключ повтора) и что кассир получает в ответ. Настоящего сервера и настоящей базы тесты не касаются: адрес — localhost,
/// данные — во временной NURMARKET_DATA_ROOT. Общая статика (PosApp.ActiveShiftId, признаки сервера) — коллекция «ServerFeatures».</summary>
[Collection("ServerFeatures")]
public sealed class ServiceScenarioTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();
    private readonly string _shiftId = Guid.NewGuid().ToString();
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public ServiceScenarioTests() => PosApp.ActiveShiftId = _shiftId;

    public void Dispose()
    {
        PosApp.ActiveShiftId = _savedShift;
        _fake.Dispose();
    }

    private async Task<(PosCheckoutService Service, ICartService Cart)> CheckoutServiceAsync(string cartJson)
    {
        var api = await LoggedInAsync(_fake);
        var cart = MakeCart(cartJson);
        var service = new PosCheckoutService(cart, new SalesApiService(api), new ShiftApiService(api),
            Null<IShiftStateService>(), Null<IReceiptPrinterService>(), Null<IMediator>(), Null<IAutonomousAuthService>());
        return (service, cart);
    }

    private static PosCheckoutRequest Pay(string method, string cash, double expectedTotal, string? nonCash = null, string? clientId = null) =>
        new() { PaymentMethod = method, CashReceived = cash, NonCashReceived = nonCash, ClientId = clientId, PrintReceipt = false, ExpectedTotal = expectedTotal };

    private void StubCheckout(string total, string number = "1001") =>
        _fake.Json("POST", "/api/main/pos/checkout/", 200, "{\"id\":\"sale-" + number + "\",\"number\":" + number + ",\"status\":\"paid\",\"total\":\"" + total + "\"}");

    // ───────────── 1. продажа ─────────────

    [Fact]
    public async Task Sale_Cash_SendsWholeReceiptInOneRequest_WithShiftAndIdempotencyKey()
    {
        StubCheckout("30.00");
        var (service, cart) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        var result = await service.CheckoutAsync(Pay("cash", "50", 30));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.SavedOffline.Should().BeFalse();
        result.TotalAmount.Should().Be(30);
        result.TotalMismatchWarning.Should().BeNull();

        var posts = Requests(_fake, "POST", "/api/main/pos/checkout/");
        posts.Should().ContainSingle("продажа — один запрос, без переноса корзины по позициям");
        Header(posts[0], "Idempotency-Key").Should().NotBeNullOrWhiteSpace("денежный POST без ключа повтора не уходит");
        var body = BodyOf(posts[0]);
        body["shift"]!.GetValue<string>().Should().Be(_shiftId);
        body["payment"]!["method"]!.GetValue<string>().Should().Be("cash");
        body["payment"]!["received"]!.GetValue<string>().Should().Be("50.00");
        var line = body["items"]!.AsArray().Should().ContainSingle().Subject!;
        line["qty"]!.GetValue<string>().Should().Be("3");
        line["price"]!.GetValue<string>().Should().Be("10.00");
        body["print_receipt"]!.GetValue<bool>().Should().BeFalse();
        ((CartProxy)(object)cart).ResetCalls.Should().Be(1, "после оплаты чек сброшен — касса готова к следующему покупателю");
    }

    [Fact]
    public async Task Sale_TwoDifferentReceipts_GetDifferentKeys()
    {
        StubCheckout("30.00");
        var (first, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));
        var (second, _) = await CheckoutServiceAsync(CartJson([("Молоко", 3, 10, 0)]));

        (await first.CheckoutAsync(Pay("cash", "30", 30))).IsSuccess.Should().BeTrue();
        (await second.CheckoutAsync(Pay("cash", "30", 30))).IsSuccess.Should().BeTrue();

        var keys = Requests(_fake, "POST", "/api/main/pos/checkout/").Select(e => Header(e, "Idempotency-Key")).ToList();
        keys.Should().HaveCount(2);
        keys.Distinct().Should().HaveCount(2, "разные чеки — разные ключи: иначе сервер вернул бы второму покупателю продажу первого");
    }

    [Fact]
    public async Task Sale_ServerTotalDiffers_SaleIsDone_ButCashierGetsWarning()
    {
        // акцию товара поменяли на сайте: касса взяла по 30, сервер провёл на 25 — продажа есть, кассиру сказано вернуть разницу
        StubCheckout("25.00");
        var (service, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        var result = await service.CheckoutAsync(Pay("cash", "30", 30));

        result.IsSuccess.Should().BeTrue();
        result.TotalMismatchWarning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Sale_WindowTotalDiffersFromReceiptTotal_IsNotSent()
    {
        // защита продажи №1136: итог в окне оплаты не сходится с итогом запроса — продажа молча на сервер не уходит
        StubCheckout("30.00");
        var (service, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        // окно оплаты «видело» 50, чек стоит 30: быстрый путь не отправляет, дальше старый путь сверяет корзину сервера
        var result = await service.CheckoutAsync(Pay("cash", "50", 50));

        Requests(_fake, "POST", "/api/main/pos/checkout/").Should().BeEmpty("расхождение итога — запрос оплаты не отправлен");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Sale_ServerRefusesByBusinessRule_NothingIsPaid()
    {
        _fake.Json("POST", "/api/main/pos/checkout/", 400, "{\"detail\":\"Смена не открыта\"}");
        var (service, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        var result = await service.CheckoutAsync(Pay("cash", "30", 30));

        result.IsSuccess.Should().BeFalse();
        result.SavedOffline.Should().BeFalse("отказ сервера по существу — не повод класть чек в очередь как оплаченный");
    }

    // ───────────── 2. смешанная оплата ─────────────

    [Fact]
    public async Task MixedPayment_SplitsCashAndCard_AndSendsDiscountsAsOnTheReceipt()
    {
        // Хлеб 3 × 10 со скидкой строки 5, Молоко 1 × 20; скидка чека 10 %: (50 − 5) × 10 % = 4,50; итог 40,50 = 20 налом + 20,50 картой
        StubCheckout("40.50");
        var json = CartJson([("Хлеб", 3, 10, 5), ("Молоко", 1, 20, 0)], orderDiscountPercent: 10);
        var (service, _) = await CheckoutServiceAsync(json);

        var result = await service.CheckoutAsync(Pay("mixed", "20", 40.50, nonCash: "20.50"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/checkout/").Should().ContainSingle().Subject);
        var payment = body["payment"]!;
        payment["method"]!.GetValue<string>().Should().Be("mixed");
        payment["cash_amount"]!.GetValue<string>().Should().Be("20.00");
        payment["card_amount"]!.GetValue<string>().Should().Be("20.50");
        body["order_discount_percent"]!.GetValue<string>().Should().Be("10.00");
        body["items"]!.AsArray().Select(i => i!["discount"]?.GetValue<string>()).Should().Contain("5.00");
        // сумма частей оплаты = итог чека
        (decimal.Parse(payment["cash_amount"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
         + decimal.Parse(payment["card_amount"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be((decimal)result.TotalAmount);
    }

    [Fact]
    public async Task MixedPayment_ServerCannotBeReached_IsNotQueuedAsPaid()
    {
        // смешанная оплата офлайн недоступна: очередь не хранит безналичную часть. Здесь сервер отвечает 400 — результат не «успех»
        _fake.Json("POST", "/api/main/pos/checkout/", 400, "{\"detail\":\"Недостаточно товара\"}");
        var (service, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        var result = await service.CheckoutAsync(Pay("mixed", "10", 30, nonCash: "20"));

        result.IsSuccess.Should().BeFalse();
        result.SavedOffline.Should().BeFalse();
    }

    // ───────────── 3. возврат ─────────────

    [Fact]
    public async Task Return_Partial_SendsLinesAndOwnKey()
    {
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var pending = new PendingIdempotencyKey();
        var key = pending.Get("sale-1|line-1:2");

        await sales.PosReturnSaleAsync("sale-1", [new PosRefundLineRequest { LineId = "line-1", Title = "Хлеб", Quantity = 2, OriginalQuantity = 3 }], default, key);
        pending.OnSuccess();

        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/sales/sale-1/return/").Should().ContainSingle().Subject);
        body["idempotency_key"]!.GetValue<string>().Should().Be(key);
        body["cashbox_role"]!.GetValue<string>().Should().Be("pos_main");
        var item = body["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["sale_item_id"]!.GetValue<string>().Should().Be("line-1");
        item["quantity"]!.GetValue<string>().Should().Be("2");
    }

    [Fact]
    public async Task Return_WholeSale_HasNoItems()
    {
        var api = await LoggedInAsync(_fake);

        await new SalesApiService(api).PosReturnSaleAsync("sale-2", null);

        var body = BodyOf(Requests(_fake, "POST", "/return/").Should().ContainSingle().Subject);
        body.ContainsKey("items").Should().BeFalse("без items сервер возвращает весь чек");
        body["idempotency_key"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Return_EmptyLineList_IsRefusedBeforeSending()
    {
        var api = await LoggedInAsync(_fake);

        var act = async () => await new SalesApiService(api).PosReturnSaleAsync("sale-3", Array.Empty<PosRefundLineRequest>());

        await act.Should().ThrowAsync<ArgumentException>("пустой список строк — ошибка вызова, иначе сервер вернул бы весь чек");
        Requests(_fake, "POST", "/return/").Should().BeEmpty();
    }

    [Fact]
    public async Task Return_ConnectionLost_RetryGoesWithSameKey_UntilServerAnswers()
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 502, "{\"detail\":\"bad gateway\"}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var pending = new PendingIdempotencyKey();
        const string signature = "sale-9|line-1:1";

        var key1 = pending.Get(signature);
        var act = async () => await sales.PosReturnWholeSaleAsync("sale-9", null, default, key1);
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        pending.OnError(ex).Should().BeTrue("исход неизвестен — ключ остаётся");

        _fake.StubReturn(); // сервер ожил
        var key2 = pending.Get(signature);
        await sales.PosReturnWholeSaleAsync("sale-9", null, default, key2);
        pending.OnSuccess();

        key2.Should().Be(key1, "повтор того же возврата — тем же ключом, сервер вернёт прежний итог, а не вернёт деньги второй раз");
        Requests(_fake, "POST", "/return/").Select(e => BodyOf(e)["idempotency_key"]!.GetValue<string>()).Should().OnlyContain(k => k == key1);
        pending.Get(signature).Should().NotBe(key1, "после успеха следующий возврат — новая операция");
    }

    [Fact]
    public async Task Return_ExceedsPaid_ServerNumbersReachTheCashier_AndKeyIsForgotten()
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 409,
            "{\"code\":\"refund_exceeds_paid\",\"detail\":\"Возврат больше положенного\",\"data\":{\"paid\":\"28.00\",\"refunded\":\"0.00\",\"requested\":\"30.00\",\"refundable\":\"28.00\"}}");
        var api = await LoggedInAsync(_fake);
        var pending = new PendingIdempotencyKey();
        var key = pending.Get("sig");

        var act = async () => await new SalesApiService(api).PosReturnWholeSaleAsync("sale-7", null, default, key);
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;

        ex.StatusCode.Should().Be(409);
        RefundAmountMath.TryParseExceeds(ex, out var info).Should().BeTrue();
        info.Paid.Should().Be(28m);
        info.Requested.Should().Be(30m);
        info.Refundable.Should().Be(28m);
        RefundAmountMath.ClampToDue(30m, info.Refundable).Should().Be(28m);
        pending.OnError(ex).Should().BeFalse("«возврат больше положенного» — отказ по существу: деньги не выдаются, ключ забыт");
    }

    [Fact]
    public async Task Return_PartialByLines_ServerRefusal_ComesAsApiExceptionWithServerFigures()
    {
        // 2026-11-06: В2 Н1-02 — ожидание ИЗМЕНЕНО осознанно. Раньше (В1) тест фиксировал дефект: возврат по строкам шёл через RequestDataAsync, который на 4xx/5xx
        // бросал сырой HttpRequestException без тела, и цифры refund_exceeds_paid до кассира не доходили. Теперь это ApiException с кодом и Payload сервера,
        // «возврат больше положенного» — отказ по существу (ключ забывается), как у полного возврата.
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 409,
            "{\"code\":\"refund_exceeds_paid\",\"data\":{\"paid\":\"28.00\",\"refunded\":\"0.00\",\"requested\":\"30.00\",\"refundable\":\"28.00\"}}");
        var api = await LoggedInAsync(_fake);
        var line = new PosRefundLineRequest { LineId = "line-1", Title = "Хлеб", Quantity = 3, OriginalQuantity = 3 };

        var act = async () => await new SalesApiService(api).PosReturnSaleAsync("sale-8", [line]);

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be(409);
        RefundAmountMath.TryParseExceeds(ex, out var info).Should().BeTrue("цифры сервера дошли до окна возврата");
        info.Paid.Should().Be(28m);
        info.Refundable.Should().Be(28m);
        PendingIdempotencyKey.IsOutcomeUnknown(ex).Should().BeFalse("отказ по существу: ключ забывается, офлайн-повтор не принимает его за обрыв связи");
    }

    // ───────────── 4. долг и погашение ─────────────

    private const string ClientId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task DebtSale_SendsClient_AndWholeAmountIsOnCredit()
    {
        StubCheckout("30.00");
        var (service, _) = await CheckoutServiceAsync(CartJson([("Хлеб", 3, 10, 0)]));

        var result = await service.CheckoutAsync(Pay("debt", "0", 30, clientId: ClientId));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/checkout/").Should().ContainSingle().Subject);
        body["client"]!.GetValue<string>().Should().Be(ClientId);
        body["payment"]!["method"]!.GetValue<string>().Should().Be("debt");
        body["payment"]!.AsObject().ContainsKey("cash_amount").Should().BeFalse("предоплаты нет — всё в долг");
    }

    [Fact]
    public void DebtSale_Prepayment_GoesToCashAndCardAmounts()
    {
        var cart = CartJson([("Хлеб", 3, 10, 0)]);
        var cash = QuickCheckoutBody.TryBuild(cart, "debt", "10", null, ClientId, _shiftId, null, false, null, false, out _)!;
        cash["payment"]!["cash_amount"]!.GetValue<string>().Should().Be("10.00");
        cash["payment"]!["card_amount"]!.GetValue<string>().Should().Be("0.00");

        var card = QuickCheckoutBody.TryBuild(cart, "debt", "10", "10", ClientId, _shiftId, null, false, null, false, out _)!;
        card["payment"]!["cash_amount"]!.GetValue<string>().Should().Be("0.00", "предоплата безналом в ящик не попадает");
        card["payment"]!["card_amount"]!.GetValue<string>().Should().Be("10.00");
    }

    [Fact]
    public async Task DebtRepayment_SendsInstallment_AndOwnKey_RetryKeepsKey()
    {
        _fake.Json("POST", "/api/main/clients/*/deals/*/pay/", 504, "{\"detail\":\"timeout\"}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var pending = new PendingIdempotencyKey();
        const string signature = "deal-5|inst-1|50.00";

        var key1 = pending.Get(signature);
        var failed = async () => await sales.PosPayDebtAsync(ClientId, "deal-5", "inst-1", 50, default, key1);
        pending.OnError((await failed.Should().ThrowAsync<ApiException>()).Which).Should().BeTrue();

        _fake.Json("POST", "/api/main/clients/*/deals/*/pay/", 200, "{\"ok\":true}");
        var key2 = pending.Get(signature);
        await sales.PosPayDebtAsync(ClientId, "deal-5", "inst-1", 50, default, key2);
        pending.OnSuccess();

        key2.Should().Be(key1);
        var calls = Requests(_fake, "POST", "/pay/");
        calls.Should().HaveCount(2);
        calls.Select(c => BodyOf(c)["idempotency_key"]!.GetValue<string>()).Should().OnlyContain(k => k == key1);
        var body = BodyOf(calls[1]);
        body["amount"]!.GetValue<string>().Should().Be("50.00");
        body["installment_id"]!.GetValue<string>().Should().Be("inst-1");
        calls[1].RequestMessage.Path.Should().Be($"/api/main/clients/{ClientId}/deals/deal-5/pay/");
    }

    [Fact]
    public async Task DebtRepayment_FullInstallment_HasNoAmountField()
    {
        _fake.Json("POST", "/api/main/clients/*/deals/*/pay/", 200, "{\"ok\":true}");
        var api = await LoggedInAsync(_fake);

        await new SalesApiService(api).PosPayDebtAsync(ClientId, "deal-6", "inst-2", null);

        BodyOf(Requests(_fake, "POST", "/pay/").Should().ContainSingle().Subject).ContainsKey("amount").Should().BeFalse("полное погашение взноса — без суммы (сумма ломает проверку сервера)");
    }

    // ───────────── 5. денежные POST без собственного ключа получают ключ по умолчанию ─────────────

    [Fact]
    public async Task CashFlow_Deposit_GetsDefaultKey_SameOnRetryAfterFailure_NewAfterSuccess()
    {
        _fake.Json("POST", "/api/construction/cashflows/", 500, "{\"detail\":\"boom\"}");
        var api = await LoggedInAsync(_fake);
        var shiftApi = new ShiftApiService(api);
        var body = new Dictionary<string, string> { ["shift"] = _shiftId, ["amount"] = "100.00", ["kind"] = "income", ["note"] = "w1-" + Guid.NewGuid().ToString("N")[..8] };

        var failed = async () => await shiftApi.ConstructionCashFlowCreateAsync(body);
        await failed.Should().ThrowAsync<ApiException>();

        _fake.Json("POST", "/api/construction/cashflows/", 200, "{\"id\":\"cf-1\"}");
        await shiftApi.ConstructionCashFlowCreateAsync(body);
        await shiftApi.ConstructionCashFlowCreateAsync(body); // та же сумма ещё раз, но прошлая прошла — это новая операция

        var keys = Requests(_fake, "POST", "/cashflows/").Select(e => Header(e, "Idempotency-Key")).ToList();
        keys.Should().HaveCount(3);
        keys.Should().OnlyContain(k => !string.IsNullOrWhiteSpace(k), "каждый денежный POST — с ключом");
        keys[1].Should().Be(keys[0], "повтор после сбоя — тем же ключом");
        keys[2].Should().NotBe(keys[1], "после успеха — новая операция, новый ключ");
    }

    // ───────────── 6. смена ─────────────

    [Fact]
    public async Task Shift_OpenAndClose_ThroughRealApiClient()
    {
        var shiftRow = "{\"id\":\"" + _shiftId + "\",\"status\":\"open\",\"opening_cash\":\"1000.00\",\"opened_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}";
        _fake.Json("POST", "/api/construction/shifts/open/", 200, shiftRow);
        _fake.Json("POST", "/api/construction/shifts/" + _shiftId + "/close/", 200,
            "{\"id\":\"" + _shiftId + "\",\"status\":\"closed\",\"closing_cash\":\"5750.00\"}");
        var api = await LoggedInAsync(_fake);
        var shifts = new ShiftApiService(api);

        var opened = await shifts.ConstructionShiftOpenAsync("cashbox-1", "1000.00");

        CartDisplayHelper.TryShiftIdFromOpenResponse(opened).Should().Be(_shiftId);
        ShiftOpenResponse.Classify(opened, 1000m, DateTimeOffset.UtcNow).Resumed.Should().BeFalse("свежая смена с той же суммой — новая");
        BodyOf(Requests(_fake, "POST", "/shifts/open/").Should().ContainSingle().Subject)["opening_cash"]!.GetValue<string>().Should().Be("1000.00");

        var closed = await ShiftCloseRetry.CloseAsync(shifts, _shiftId, "5750.00", default);

        closed.Outcome.Should().Be(ShiftCloseOutcome.Closed);
        BodyOf(Requests(_fake, "POST", "/close/").Should().ContainSingle().Subject)["closing_cash"]!.GetValue<string>().Should().Be("5750.00");
    }

    [Fact]
    public async Task Shift_ServerReturnsAlreadyOpenedOldShift_IsRecognizedAsResumed()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-5).ToString("O");
        _fake.Json("POST", "/api/construction/shifts/open/", 200, "{\"id\":\"" + _shiftId + "\",\"status\":\"open\",\"opening_cash\":\"700.00\",\"opened_at\":\"" + old + "\"}");
        var api = await LoggedInAsync(_fake);

        var opened = await new ShiftApiService(api).ConstructionShiftOpenAsync("cashbox-1", "1000.00");
        var (resumed, serverOpening, _) = ShiftOpenResponse.Classify(opened, 1000m, DateTimeOffset.UtcNow);

        resumed.Should().BeTrue("сервер отдал смену, открытую раньше: сумма и время не те, что ввёл кассир");
        serverOpening.Should().Be(700m);
    }

    [Fact]
    public async Task Shift_Close_AlreadyClosedOnServer_IsNotAnError()
    {
        _fake.Json("POST", "/api/construction/shifts/" + _shiftId + "/close/", 400, "{\"detail\":\"Смена уже закрыта\"}");
        var api = await LoggedInAsync(_fake);

        var closed = await ShiftCloseRetry.CloseAsync(new ShiftApiService(api), _shiftId, "100.00", default);

        closed.Outcome.Should().Be(ShiftCloseOutcome.AlreadyClosed, "смену закрыли на сайте — очередь закрытий по ней не нужна");
    }

    [Fact]
    public async Task Shift_Close_ServerDown_StaysFailed_ForRetryLater()
    {
        _fake.Json("POST", "/api/construction/shifts/" + _shiftId + "/close/", 502, "{\"detail\":\"bad gateway\"}");
        _fake.Json("POST", "/api/construction/shift/" + _shiftId + "/close/", 502, "{\"detail\":\"bad gateway\"}");
        var api = await LoggedInAsync(_fake);

        var closed = await ShiftCloseRetry.CloseAsync(new ShiftApiService(api), _shiftId, "100.00", default);

        closed.Outcome.Should().Be(ShiftCloseOutcome.Failed);
    }

    [Fact]
    public async Task Shift_OpenShiftRow_GivesExpectedCash_WithCorrectionApplied()
    {
        var row = "{\"id\":\"" + _shiftId + "\",\"status\":\"open\",\"opening_cash\":\"1000.00\",\"sales_total\":\"5000.00\",\"cash_sales_total\":\"4000.00\"," +
                  "\"noncash_sales_total\":\"1000.00\",\"expected_cash\":\"5000.00\",\"sales_count\":12}";
        _fake.Json("GET", "/api/construction/shifts/", 200, "{\"count\":1,\"next\":null,\"results\":[" + row + "]}");
        var api = await LoggedInAsync(_fake);
        var saved = ShiftExpectedCash.CorrectionProvider;
        try
        {
            ShiftExpectedCash.CorrectionProvider = id => id == _shiftId ? -300m : 0m;

            var list = await new ShiftApiService(api).ConstructionShiftsListAsync(openOnly: true);
            var totals = ShiftBalanceHelper.FindOpenShiftTotals(list, null)!;

            totals.OpeningCash.Should().Be(1000m);
            totals.CashSales.Should().Be(4000m);
            totals.NonCashSales.Should().Be(1000m);
            totals.SalesCount.Should().Be(12);
            totals.ExpectedCash.Should().Be(5000m);
            ShiftExpectedCash.WithFix(totals.ExpectedCash, _shiftId).Should().Be(4700m, "«ожидаемо в ящике» = сумма сервера + поправка кассы");
            Requests(_fake, "GET", "/api/construction/shifts/").Should().OnlyContain(e => e.RequestMessage.Query!.ContainsKey("status"), "открытая смена спрашивается с ?status=open");
        }
        finally
        {
            ShiftExpectedCash.CorrectionProvider = saved;
        }
    }
}
