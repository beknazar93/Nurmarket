using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (владелец: «Поставщиктин карызын төлөгөндө жарымын же 20% бере албайт экенсиң, только 100 пайыз төлөш керек экен»): оплата долга поставщику частями.
/// Арифметика окна (SupplierDebtPayMath) и тело POST deals/{d}/pay/ (SupplierApiService.PayInstallmentAsync): взнос целиком — без amount (как всегда),
/// частичная оплата — amount с двумя знаками, безнал — payment_method=transfer, наличные — поле не шлётся.</summary>
public sealed class SupplierDebtPartialPayTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    // ------------------------------------------------------------------ разбор и границы суммы

    [Theory]
    [InlineData("500", 500)]
    [InlineData("500,50", 500.5)]
    [InlineData("500.50", 500.5)]
    [InlineData(" 1 200,5 ", 1200.5)]
    [InlineData("1 200,5", 1200.5)] // неразрывный пробел
    [InlineData("0,01", 0.01)]
    [InlineData("10,005", 10.01)] // лишние знаки — до копейки от нуля
    public void TryParse_accepts_comma_dot_and_spaces(string text, double expected)
    {
        SupplierDebtPayMath.TryParse(text, out var amount).Should().BeTrue();
        amount.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12,3,4")]
    [InlineData("--5")]
    public void TryParse_rejects_text(string text) => SupplierDebtPayMath.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Check_allows_from_one_kopeck_up_to_the_debt_and_nothing_else()
    {
        SupplierDebtPayMath.Check(0.01, 1000).Should().Be(SupplierDebtPayMath.Problem.None, "наименьшая оплата — копейка");
        SupplierDebtPayMath.Check(200, 1000).Should().Be(SupplierDebtPayMath.Problem.None, "20 % — можно");
        SupplierDebtPayMath.Check(500, 1000).Should().Be(SupplierDebtPayMath.Problem.None, "50 % — можно");
        SupplierDebtPayMath.Check(1000, 1000).Should().Be(SupplierDebtPayMath.Problem.None, "100 % — можно");
        SupplierDebtPayMath.Check(0, 1000).Should().Be(SupplierDebtPayMath.Problem.TooSmall);
        SupplierDebtPayMath.Check(-5, 1000).Should().Be(SupplierDebtPayMath.Problem.TooSmall);
        SupplierDebtPayMath.Check(1000.01, 1000).Should().Be(SupplierDebtPayMath.Problem.TooBig, "больше долга нельзя");
        SupplierDebtPayMath.Check(double.NaN, 1000).Should().Be(SupplierDebtPayMath.Problem.NotNumber);
    }

    [Fact]
    public void Percent_buttons_give_half_and_a_fifth_and_stay_inside_the_debt()
    {
        SupplierDebtPayMath.Percent(1000, 50).Should().Be(500);
        SupplierDebtPayMath.Percent(1000, 20).Should().Be(200);
        SupplierDebtPayMath.Percent(1000, 100).Should().Be(1000, "«Весь долг» — ровно остаток");
        SupplierDebtPayMath.Percent(333.33, 50).Should().Be(166.67, "до копейки от нуля");
        SupplierDebtPayMath.Percent(0.03, 20).Should().Be(0.01, "не меньше копейки");
        SupplierDebtPayMath.Percent(0.01, 50).Should().Be(0.01);
        foreach (var debt in new[] { 0.5, 7.77, 123456.78 })
        foreach (var percent in new[] { 20, 50, 100 })
            SupplierDebtPayMath.Check(SupplierDebtPayMath.Percent(debt, percent), debt).Should().Be(SupplierDebtPayMath.Problem.None, $"{percent} % от {debt} проходит проверку окна");
    }

    [Fact]
    public void Left_is_the_debt_after_payment_without_double_noise()
    {
        SupplierDebtPayMath.Left(1000, 200).Should().Be(800);
        SupplierDebtPayMath.Left(0.3, 0.1).Should().Be(0.2, "0,3 − 0,1 без шума double");
        SupplierDebtPayMath.Left(1000, 1000).Should().Be(0);
        SupplierDebtPayMath.Left(100, 150).Should().Be(0, "не уходит в минус");
        SupplierDebtPayMath.IsFull(1000, 1000).Should().BeTrue();
        SupplierDebtPayMath.IsFull(1000, 999.996).Should().BeTrue("в пределах полкопейки — целиком");
        SupplierDebtPayMath.IsFull(1000, 500).Should().BeFalse();
    }

    // ------------------------------------------------------------------ раскладка по взносам

    private static readonly (string, string, double)[] Queue =
    [
        ("d1", "i1", 300),
        ("d1", "i2", 300),
        ("d2", "i1", 400),
    ];

    [Fact]
    public void Plan_full_debt_closes_every_instalment_without_amount()
    {
        var steps = SupplierDebtPayMath.Plan(Queue, 1000);
        steps.Should().HaveCount(3);
        steps.Should().OnlyContain(s => s.IsFull);
        steps.Sum(s => s.Amount).Should().Be(1000);
    }

    [Fact]
    public void Plan_half_closes_first_instalments_then_one_part()
    {
        var steps = SupplierDebtPayMath.Plan(Queue, 500);
        steps.Should().HaveCount(2);
        steps[0].Should().Be(new SupplierDebtPayMath.Step("d1", "i1", 300, 300, true));
        steps[1].Should().Be(new SupplierDebtPayMath.Step("d1", "i2", 300, 200, false), "вторая часть — частично: amount 200 из остатка 300");
        steps.Sum(s => s.Amount).Should().Be(500);
    }

    [Fact]
    public void Plan_a_fifth_stays_inside_the_first_instalment()
    {
        var steps = SupplierDebtPayMath.Plan(Queue, SupplierDebtPayMath.Percent(SupplierDebtPayMath.Total(Queue), 20));
        steps.Should().ContainSingle().Which.Should().Be(new SupplierDebtPayMath.Step("d1", "i1", 300, 200, false));
    }

    [Fact]
    public void Plan_ignores_closed_instalments_and_empty_amount()
    {
        SupplierDebtPayMath.Plan(Queue, 0).Should().BeEmpty();
        SupplierDebtPayMath.Plan([], 100).Should().BeEmpty();
        var withClosed = new (string, string, double)[] { ("d1", "i0", 0), ("d1", "i1", 50) };
        SupplierDebtPayMath.Plan(withClosed, 20).Should().ContainSingle().Which.InstallmentId.Should().Be("i1", "закрытый взнос пропускается");
    }

    [Fact]
    public void Plan_repeat_payment_continues_after_a_part()
    {
        // остаток после первой частичной оплаты — 100 по второму взносу: повторная оплата 100 закрывает его целиком
        var afterFirst = new (string, string, double)[] { ("d1", "i2", 100), ("d2", "i1", 400) };
        var steps = SupplierDebtPayMath.Plan(afterFirst, 100);
        steps.Should().ContainSingle().Which.Should().Be(new SupplierDebtPayMath.Step("d1", "i2", 100, 100, true));
    }

    // ------------------------------------------------------------------ тело запроса оплаты взноса

    [Fact]
    public async Task Pay_installment_whole_goes_without_amount_and_method_like_before()
    {
        _fake.Json("POST", "/api/main/clients/s1/deals/d1/pay/", 200, "{\"ok\":true}");
        var api = await LoggedInAsync(_fake);

        await new SupplierApiService(api).PayInstallmentAsync("s1", "d1", "i1", "key-1", "note");

        var body = BodyOf(Requests(_fake, "POST", "/deals/d1/pay/").Should().ContainSingle().Subject);
        body.ContainsKey("amount").Should().BeFalse("взнос целиком — как всегда, без amount (подтверждённый рабочий вызов)");
        body.ContainsKey("payment_method").Should().BeFalse("наличные — тело прежнее");
        body["installment_id"]!.GetValue<string>().Should().Be("i1");
        body["cashbox_role"]!.GetValue<string>().Should().Be("expense_variable");
    }

    [Fact]
    public async Task Pay_installment_part_sends_amount_with_two_decimals()
    {
        _fake.Json("POST", "/api/main/clients/s1/deals/d1/pay/", 200, "{\"ok\":true}");
        var api = await LoggedInAsync(_fake);

        await new SupplierApiService(api).PayInstallmentAsync("s1", "d1", "i1", "key-2", "note", default, 200, "cash");

        var body = BodyOf(Requests(_fake, "POST", "/deals/d1/pay/").Should().ContainSingle().Subject);
        body["amount"]!.GetValue<string>().Should().Be("200.00", "частичная оплата — сумма с двумя знаками, точкой");
        body.ContainsKey("payment_method").Should().BeFalse("«cash» поля не добавляет");
    }

    [Fact]
    public async Task Pay_installment_cashless_adds_payment_method_transfer()
    {
        _fake.Json("POST", "/api/main/clients/s1/deals/d1/pay/", 200, "{\"ok\":true}");
        var api = await LoggedInAsync(_fake);

        await new SupplierApiService(api).PayInstallmentAsync("s1", "d1", "i1", "key-3", "note", default, 99.5, "transfer");

        var body = BodyOf(Requests(_fake, "POST", "/deals/d1/pay/").Should().ContainSingle().Subject);
        body["amount"]!.GetValue<string>().Should().Be("99.50");
        body["payment_method"]!.GetValue<string>().Should().Be("transfer");
    }

    [Fact]
    public async Task Pay_installment_amount_over_the_remainder_is_refused_by_server_with_its_code()
    {
        // контракт ТЗ Б-13 п. 6: amount больше остатка взноса — 400 amount_exceeds_installment, data.max; касса показывает отказ, деньги не двигаются
        _fake.Json("POST", "/api/main/clients/s1/deals/d1/pay/", 400,
            "{\"code\":\"amount_exceeds_installment\",\"detail\":\"Сумма больше остатка взноса. Максимум: 300.00\",\"data\":{\"max\":\"300.00\"}}");
        var api = await LoggedInAsync(_fake);

        var act = () => new SupplierApiService(api).PayInstallmentAsync("s1", "d1", "i1", "key-4", "note", default, 500, "cash");

        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.Message.Should().Contain("300", "в тексте отказа — максимум, который назвал сервер");
    }

    // ------------------------------------------------------------------ 2026-11-26: сервер не принял часть суммы

    private static ApiException Refusal(int status, string json) =>
        new("refused", status, System.Text.Json.JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Partial_refusal_is_told_apart_from_amount_over_the_remainder()
    {
        SupplierDebtPayMath.IsPartialRefused(Refusal(400, "{\"amount\":[\"Неверное поле\"]}"), wasPartial: true).Should().BeTrue("400 с полем amount при частичной оплате — сервер не принимает часть");
        SupplierDebtPayMath.IsPartialRefused(Refusal(422, "{\"detail\":\"amount is not supported\"}"), wasPartial: true).Should().BeTrue();
        SupplierDebtPayMath.IsPartialRefused(Refusal(400, "{\"code\":\"amount_exceeds_installment\",\"detail\":\"Максимум: 300.00\"}"), wasPartial: true).Should().BeFalse("сумма больше остатка — сервер сам называет максимум");
        SupplierDebtPayMath.IsPartialRefused(Refusal(400, "{\"amount\":[\"x\"]}"), wasPartial: false).Should().BeFalse("оплата целиком — amount не шлётся, подсказка про часть ни к чему");
        SupplierDebtPayMath.IsPartialRefused(Refusal(500, "{\"amount\":[\"x\"]}"), wasPartial: true).Should().BeFalse("5xx — не отказ по сумме");
        SupplierDebtPayMath.IsPartialRefused(new TimeoutException(), wasPartial: true).Should().BeFalse();
    }

    // ------------------------------------------------------------------ 2026-11-26 (ответ Умара 26.11): pay-supplier-debt — любая часть долга одним запросом

    [Theory]
    [InlineData(200, "cash", "200.00")]   // 20 % от 1000
    [InlineData(500, "transfer", "500.00")] // 50 %
    public async Task Pay_supplier_debt_posts_one_sum_with_method_and_idempotency_key_and_reads_paid_and_left(double amount, string method, string expectedAmount)
    {
        _fake.Json("POST", "/api/main/clients/s1/pay-supplier-debt/", 200, "{\"paid\":\"" + expectedAmount + "\",\"left\":\"" + (1000 - amount).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\",\"applied\":[],\"cashflows\":[]}");
        var api = await LoggedInAsync(_fake);

        var result = await new SupplierApiService(api).PaySupplierDebtAsync("s1", amount, method, "key-psd-" + method, "note");

        var entry = Requests(_fake, "POST", "/clients/s1/pay-supplier-debt/").Should().ContainSingle().Subject;
        var body = BodyOf(entry);
        body["amount"]!.GetValue<string>().Should().Be(expectedAmount, "любая часть долга — сумма с двумя знаками");
        body["method"]!.GetValue<string>().Should().Be(method);
        body["idempotency_key"]!.GetValue<string>().Should().Be("key-psd-" + method, "ключ повтора обязателен и в теле");
        body.ContainsKey("shift").Should().BeFalse("владелец платит не из ящика смены");
        body.ContainsKey("cashbox").Should().BeFalse("кассу выбирает сервер");
        entry.RequestMessage.Headers!["Idempotency-Key"].Should().Contain("key-psd-" + method, "и заголовком");
        result.Paid.Should().Be(double.Parse(expectedAmount, System.Globalization.CultureInfo.InvariantCulture));
        result.Left.Should().Be(1000 - amount, "left из ответа сервера");
    }

    [Fact]
    public async Task Pay_supplier_debt_sends_the_shift_when_paying_from_the_drawer_and_requires_a_key()
    {
        _fake.Json("POST", "/api/main/clients/s1/pay-supplier-debt/", 200, "{\"paid\":\"100.00\",\"left\":\"0.00\"}");
        var api = await LoggedInAsync(_fake);

        await new SupplierApiService(api).PaySupplierDebtAsync("s1", 100, "cash", "key-shift", "note", default, "shift-1");
        BodyOf(Requests(_fake, "POST", "/pay-supplier-debt/").Should().ContainSingle().Subject)["shift"]!.GetValue<string>().Should().Be("shift-1");

        var act = () => new SupplierApiService(api).PaySupplierDebtAsync("s1", 100, "cash", " ", "note");
        await act.Should().ThrowAsync<ArgumentException>("без Idempotency-Key сервер не принимает оплату");
    }

    // ------------------------------------------------------------------ 2026-11-26 (ответ Умара 26.11 16:20): installment_id / deal_id

    [Fact]
    public async Task Pay_supplier_debt_sends_installment_id_and_deal_id_only_when_given()
    {
        _fake.Json("POST", "/api/main/clients/s1/pay-supplier-debt/", 200, "{\"paid\":\"200.00\",\"left\":\"800.00\"}");
        var api = await LoggedInAsync(_fake);

        await new SupplierApiService(api).PaySupplierDebtAsync("s1", 200, "cash", "key-inst", "note", default, null, "i7", "d3");
        await new SupplierApiService(api).PaySupplierDebtAsync("s1", 200, "cash", "key-plain", "note");

        var entries = Requests(_fake, "POST", "/clients/s1/pay-supplier-debt/");
        entries.Should().HaveCount(2);
        var withIds = BodyOf(entries[0]);
        withIds["installment_id"]!.GetValue<string>().Should().Be("i7", "сначала этот взнос");
        withIds["deal_id"]!.GetValue<string>().Should().Be("d3", "только эта сделка");
        var plain = BodyOf(entries[1]);
        plain.ContainsKey("installment_id").Should().BeFalse("без взноса — как раньше, от старых к новым");
        plain.ContainsKey("deal_id").Should().BeFalse();
    }

    [Theory]
    [InlineData("installment_not_found")]
    [InlineData("deal_not_found")]
    public async Task Pay_supplier_debt_400_not_found_codes_come_back_as_api_exception(string code)
    {
        _fake.Json("POST", "/api/main/clients/s1/pay-supplier-debt/", 400, "{\"code\":\"" + code + "\",\"detail\":\"" + code + "\"}");
        var api = await LoggedInAsync(_fake);

        var act = () => new SupplierApiService(api).PaySupplierDebtAsync("s1", 100, "cash", "key-nf-" + code, "note", default, null, "i-x", "d-x");

        var ex = await act.Should().ThrowAsync<ApiException>();
        ex.Which.StatusCode.Should().Be(400);
        ex.Which.Message.Should().Contain(code);
        SupplierDebtPayMath.IsPartialRefused(ex.Which, wasPartial: true).Should().BeFalse("это не отказ от частичной оплаты, а неверный взнос/сделка");
    }

    // ------------------------------------------------------------------ 2026-11-26 (ответ Умара 26.11 16:20): suppliers/{id}/returns/ несёт supplier_debt

    [Theory]
    [InlineData("{\"id\":\"r9\",\"compensation\":\"debt_offset\",\"supplier_debt\":\"2700.00\"}", 2700.0)]
    [InlineData("{\"id\":\"r9\",\"supplier_debt\":0}", 0.0)]
    [InlineData("{\"id\":\"r9\",\"supplier_debt\":null}", null)]
    [InlineData("{\"id\":\"r9\"}", null)]
    public async Task Post_return_reads_supplier_debt_after_the_return_when_the_server_sends_it(string answer, double? expectedDebt)
    {
        _fake.Json("POST", "/api/main/suppliers/s1/returns/", 201, answer);
        var api = await LoggedInAsync(_fake);

        var posted = await new SupplierApiService(api).PostReturnDetailedAsync("s1", new Dictionary<string, object?> { ["reason"] = "defect" }, "key-ret-" + expectedDebt);

        posted.Id.Should().Be("r9");
        posted.SupplierDebt.Should().Be(expectedDebt, "долг после возврата — из ответа; нет поля — null, окно перечитает kpis");
        BodyOf(Requests(_fake, "POST", "/suppliers/s1/returns/").Should().ContainSingle().Subject)["idempotency_key"]!.GetValue<string>().Should().Be("key-ret-" + expectedDebt);
    }

    [Fact]
    public async Task Pay_supplier_debt_without_left_in_the_answer_keeps_it_unknown()
    {
        _fake.Json("POST", "/api/main/clients/s1/pay-supplier-debt/", 200, "{\"ok\":true}");
        var api = await LoggedInAsync(_fake);

        var result = await new SupplierApiService(api).PaySupplierDebtAsync("s1", 300, "cash", "key-nl", "note");

        result.Paid.Should().Be(300);
        result.Left.Should().BeNull("сервер «left» не прислал — карточка перечитывает долг");
    }
}
