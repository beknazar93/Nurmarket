using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 («Базар», модули category_tiles / volume_price / haggle / currencies / invoice_cargo; ТЗ Контейнер-базар, ответ сервера 20.11 В.16 и В.19):
/// чистая логика торга, ступеней цены и валюты; цена по количеству и принятая валюта в строках чека и теле продажи; плитки категорий в модели каталога; включение модулей по профилю.</summary>
public sealed class BazaarLogicTests
{
    // ───────────── торг ─────────────

    [Theory]
    [InlineData(1247, 10, 1240, 7)]
    [InlineData(1247, 50, 1200, 47)]
    [InlineData(1247, 100, 1200, 47)]
    [InlineData(1999.99, 100, 1900, 99.99)]
    public void Haggle_RoundDown_GoesOnlyDown(double total, int step, double agreed, double discount)
    {
        var r = BazaarHaggle.RoundDown((decimal)total, step);
        r.Outcome.Should().Be(HaggleOutcome.Ok);
        r.Agreed.Should().Be((decimal)agreed);
        r.Discount.Should().Be((decimal)discount);
        r.Agreed.Should().BeLessThan(r.Total, "округлить вверх нельзя");
    }

    [Fact]
    public void Haggle_RoundDown_AlreadyRound_OrBelowStep_GivesNothing()
    {
        BazaarHaggle.RoundDown(1200m, 100).Outcome.Should().Be(HaggleOutcome.NoChange);
        BazaarHaggle.RoundDown(40m, 50).Outcome.Should().Be(HaggleOutcome.BelowStep, "сумма меньше шага — в ноль не округляем");
        BazaarHaggle.RoundDown(0m, 10).Outcome.Should().Be(HaggleOutcome.InvalidAmount);
        BazaarHaggle.RoundDown(100m, 0).Outcome.Should().Be(HaggleOutcome.InvalidAmount);
    }

    [Fact]
    public void Haggle_Agreed_DiscountIsDifference_AndCannotRaise()
    {
        var ok = BazaarHaggle.Agreed(1500m, 1300m);
        ok.IsOk.Should().BeTrue();
        ok.Discount.Should().Be(200m);
        ok.Percent.Should().BeApproximately(13.33, 0.01);
        BazaarHaggle.Agreed(1500m, 1600m).Outcome.Should().Be(HaggleOutcome.CannotRaise);
        BazaarHaggle.Agreed(1500m, 1500m).Outcome.Should().Be(HaggleOutcome.NoChange);
        BazaarHaggle.Agreed(1500m, 0m).Outcome.Should().Be(HaggleOutcome.InvalidAmount);
        BazaarHaggle.Agreed(0m, 10m).Outcome.Should().Be(HaggleOutcome.InvalidAmount);
    }

    [Theory]
    [InlineData("1 300", 1300)]
    [InlineData("1300,50", 1300.5)]
    [InlineData("1300.5", 1300.5)]
    public void Haggle_ParseAmount_AcceptsSpacesAndComma(string text, double expected) =>
        BazaarHaggle.ParseAmount(text).Should().Be((decimal)expected);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void Haggle_ParseAmount_RejectsGarbage(string text) => BazaarHaggle.ParseAmount(text).Should().BeNull();

    [Fact]
    public void Haggle_GuardLevels_FreeUntilPinPercent_ThenPin_ThenSeniorOnly()
    {
        BazaarHaggle.GuardLevel(4.9, 5, 10).Should().Be(HaggleGuardLevel.Free);
        BazaarHaggle.GuardLevel(5, 5, 10).Should().Be(HaggleGuardLevel.Free, "ровно порог — ещё свободно");
        BazaarHaggle.GuardLevel(5.1, 5, 10).Should().Be(HaggleGuardLevel.NeedsPin);
        BazaarHaggle.GuardLevel(10, 5, 10).Should().Be(HaggleGuardLevel.NeedsPin);
        BazaarHaggle.GuardLevel(10.1, 5, 10).Should().Be(HaggleGuardLevel.SeniorOnly);
        BazaarHaggle.GuardLevel(30, 5, 3).Should().Be(HaggleGuardLevel.SeniorOnly, "жёсткий предел не бывает ниже порога PIN");
    }

    // ───────────── ступени цены ─────────────

    private static readonly VolumeTier[] Tiers = { new(3m, 90m), new(10m, 80m) };

    [Theory]
    [InlineData(1, null)]
    [InlineData(2.9, null)]
    [InlineData(3, 90)]
    [InlineData(9, 90)]
    [InlineData(10, 80)]
    [InlineData(500, 80)]
    public void Volume_Pick_TakesBiggestMinQtyNotAboveQuantity(double qty, int? price)
    {
        var tier = BazaarVolumePrices.Pick(Tiers, (decimal)qty);
        if (price is null)
            tier.Should().BeNull();
        else
            tier!.Price.Should().Be(price.Value);
    }

    [Fact]
    public void Volume_Validate_RulesOfTheEditor()
    {
        BazaarVolumePrices.Validate(Tiers, 100m).Should().Be(VolumeTiersError.None);
        BazaarVolumePrices.Validate(new[] { new VolumeTier(0m, 5m) }).Should().Be(VolumeTiersError.BadQuantity);
        BazaarVolumePrices.Validate(new[] { new VolumeTier(2m, 0m) }).Should().Be(VolumeTiersError.BadPrice);
        BazaarVolumePrices.Validate(new[] { new VolumeTier(2m, 5m), new VolumeTier(2m, 4m) }).Should().Be(VolumeTiersError.DuplicateQuantity);
        BazaarVolumePrices.Validate(new[] { new VolumeTier(2m, 100m) }, 100m).Should().Be(VolumeTiersError.NotCheaperThanRetail);
        BazaarVolumePrices.Validate(new[] { new VolumeTier(3m, 70m), new VolumeTier(10m, 90m) }).Should().Be(VolumeTiersError.NotDescending, "чем больше штук, тем цена ниже");
        BazaarVolumePrices.Validate(Enumerable.Range(1, 9).Select(i => new VolumeTier(i, 100 - i)).ToList()).Should().Be(VolumeTiersError.TooMany);
        foreach (var e in Enum.GetValues<VolumeTiersError>().Where(e => e != VolumeTiersError.None))
            BazaarVolumePrices.ErrorText(e).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Volume_ParseServer_TakesOnlyTiersWithoutLevel()
    {
        using var doc = JsonDocument.Parse("""{"prices":[{"level":"l1","price":"480"}],"volume_prices":[{"min_qty":"10","price":"470","level":null},{"min_qty":3,"price":"490.5"},{"min_qty":"20","price":"400","level":"foreman"},{"min_qty":"0","price":"1"}]}""");
        var tiers = BazaarVolumePrices.ParseServer(doc.RootElement);
        tiers.Should().Equal(new VolumeTier(3m, 490.5m), new VolumeTier(10m, 470m));
        BazaarVolumePrices.ParseServer(JsonDocument.Parse("{}").RootElement).Should().BeEmpty();
    }

    [Fact]
    public void Volume_Caption_ReadsLikeTheOwnerSays()
    {
        var text = BazaarVolumePrices.Caption(new VolumeTier(10m, 450m));
        text.Should().Contain("10").And.Contain("450");
    }

    // ───────────── валюта ─────────────

    private static CurrencyRates Rates(params (string Code, decimal Rate)[] rates) =>
        new(rates.ToDictionary(r => r.Code, r => r.Rate), Array.Empty<string>(), "1", null);

    [Fact]
    public void Currency_ToSom_Is20Dollars_At87_5_Equal1750()
    {
        BazaarCurrencies.ToSom(20m, 87.5m).Should().Be(1750m);
        BazaarCurrencies.ToSom(10.1m, 0.95m).Should().Be(9.60m);
    }

    [Fact]
    public void Currency_TryBuild_RequiresRateAndAmount()
    {
        var rates = Rates(("USD", 87.5m), ("RUB", 0m));
        BazaarCurrencies.TryBuild(rates, "USD", "20", null, out var fx).Should().Be(FxInputResult.Ok);
        fx!.AmountSom.Should().Be(1750m);
        fx.TotalSom.Should().Be(1750m);
        BazaarCurrencies.TryBuild(rates, "USD", "20", "100", out var withSom).Should().Be(FxInputResult.Ok);
        withSom!.TotalSom.Should().Be(1850m);
        BazaarCurrencies.TryBuild(rates, "RUB", "100", null, out _).Should().Be(FxInputResult.NoRate, "курс 0 — кнопка недоступна");
        BazaarCurrencies.TryBuild(rates, "CNY", "100", null, out _).Should().Be(FxInputResult.NoRate, "курса нет совсем");
        BazaarCurrencies.TryBuild(rates, "USD", "", null, out _).Should().Be(FxInputResult.BadAmount);
        BazaarCurrencies.TryBuild(rates, "USD", "0", null, out _).Should().Be(FxInputResult.BadAmount);
        BazaarCurrencies.TryBuild(rates, "XYZ", "5", null, out _).Should().Be(FxInputResult.UnknownCurrency);
    }

    [Fact]
    public void Currency_PaymentEntry_HasServerFields_AndAmountInSom()
    {
        var fx = new FxPayment("USD", 20m, 87.5m, 1750m, 50m);
        var payments = BazaarCurrencies.ToPayments(fx);
        payments.Count.Should().Be(2);
        var entry = payments[0]!.AsObject();
        entry["method"]!.ToString().Should().Be("cash");
        entry["amount"]!.ToString().Should().Be("1750.00");
        entry["currency"]!.ToString().Should().Be("USD");
        entry["amount_currency"]!.ToString().Should().Be("20");
        entry["rate"]!.ToString().Should().Be("87.5");
        payments[1]!["amount"]!.ToString().Should().Be("50.00");
        payments[1]!.AsObject().ContainsKey("currency").Should().BeFalse("доля сомами — обычная наличная строка");
        // сумма строки сходится с amount_currency × rate до копейки (иначе сервер ответит currency_amount_mismatch)
        (decimal.Parse(entry["amount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) - 20m * 87.5m).Should().Be(0m);
    }

    [Fact]
    public void Currency_Accepted_ShowsOnlyUsdRubCnyWithRate_AndRespectsEnabledList()
    {
        BazaarCurrencies.Accepted(Rates(("USD", 87.5m), ("EUR", 95m), ("CNY", 12m), ("RUB", 0m))).Should().Equal("USD", "CNY");
        var onlyUsd = new CurrencyRates(new Dictionary<string, decimal> { ["USD"] = 87.5m, ["CNY"] = 12m }, new[] { "USD" }, "2", null);
        BazaarCurrencies.Accepted(onlyUsd).Should().Equal("USD");
    }

    [Fact]
    public void Currency_Parse_ReadsRatesEnabledAndVersion()
    {
        using var doc = JsonDocument.Parse("""{"base":"KGS","rates":{"USD":"87.5","RUB":0.95,"CNY":""},"enabled":["USD","RUB"],"version":"17","updated_at":"2026-11-20T17:10:00Z"}""");
        var r = BazaarCurrencies.Parse(doc.RootElement);
        r.Rates.Should().ContainKey("USD").WhoseValue.Should().Be(87.5m);
        r.Rates["RUB"].Should().Be(0.95m);
        r.Rates.Should().NotContainKey("CNY", "пустой курс — валюты нет");
        r.Enabled.Should().Equal("USD", "RUB");
        r.Version.Should().Be("17");
        r.UpdatedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("currency_amount_mismatch")]
    [InlineData("currency_unknown")]
    [InlineData("currency_rate_required")]
    public void Currency_ServerErrors_HaveFriendlyText(string code) => BazaarCurrencies.ServerErrorText(code).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void Currency_ServerErrors_UnknownCodeIsNotOurs() => BazaarCurrencies.ServerErrorText("something_else").Should().BeNull();
}

/// <summary>Цена по количеству, торг и валюта в снимке чека и в теле продажи (настоящий CartService).</summary>
[Collection("Wave5Database")]
public sealed class BazaarCartTests
{
    private static readonly string Shift = Guid.NewGuid().ToString();

    private static (CartService Cart, string ProductId) CartWith(double price, double qty)
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var productId = Guid.NewGuid().ToString();
        cart.AddItem(CartTestHelper.CreateProduct(productId, "Кроссовки", (decimal)price), qty);
        return (cart, productId);
    }

    private static IReadOnlyList<VolumeTier>? TiersOf(string _) => new[] { new VolumeTier(3m, 90m), new VolumeTier(10m, 80m) };

    [Fact]
    public void Volume_LineGetsTierPrice_AndGoesBackBelowTheThreshold()
    {
        var (cart, _) = CartWith(100, 3);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf).Should().Be(1);
        var line = cart.Items.Single();
        line.UnitPrice.Should().Be(90m);
        line.LineTotal.Should().Be(270m);
        var json = CartDisplayHelper.EnumerateItems(cart.Root).Single();
        json.GetProperty("retail_price").GetDouble().Should().Be(100d);
        json.GetProperty("is_wholesale").GetBoolean().Should().BeTrue("явная цена — сервер не считает её устаревшей");
        QuickCheckoutBody.IsIntentionalPrice(json).Should().BeTrue();
        ReceiptSnapshotCartEditor.LineVolumeMin(json).Should().Be(3m);

        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf).Should().Be(0, "повтор без изменений чека ничего не делает");

        cart.UpdateQuantity(line.Id!, 12);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf).Should().Be(1);
        cart.Items.Single().UnitPrice.Should().Be(80m);
        cart.Items.Single().LineTotal.Should().Be(960m);

        cart.UpdateQuantity(line.Id!, 2);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf).Should().Be(1);
        cart.Items.Single().UnitPrice.Should().Be(100m, "ушли ниже ступени — розница");
        CartDisplayHelper.EnumerateItems(cart.Root).Single().TryGetProperty("is_wholesale", out _).Should().BeFalse();
        cart.Dispose();
    }

    [Fact]
    public void Volume_ManualWholesale_AndPriceLevel_AreNotTouched_AndCashierCanTurnTheTierOff()
    {
        var (cart, _) = CartWith(100, 12);
        var id = cart.Items.Single().Id!;
        ReceiptSnapshotCartEditor.SetLineWholesale(cart, id, true, 95).Should().BeTrue();
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf).Should().Be(0, "кассир включил «Опт» сам — ручной выбор главнее");
        cart.Items.Single().UnitPrice.Should().Be(95m);

        var (cart2, _) = CartWith(100, 12);
        var id2 = cart2.Items.Single().Id!;
        ReceiptSnapshotCartEditor.SetLinePriceLevel(cart2, id2, "l1", 85m).Should().BeTrue();
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart2, TiersOf).Should().Be(0, "цена серверного уровня клиента главнее ступени");
        cart2.Items.Single().UnitPrice.Should().Be(85m);

        var (cart3, _) = CartWith(100, 12);
        var id3 = cart3.Items.Single().Id!;
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart3, TiersOf).Should().Be(1);
        ReceiptSnapshotCartEditor.SetLineWholesale(cart3, id3, false, 0).Should().BeTrue("кассир снял «Опт» со строки со ступенью");
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart3, TiersOf).Should().Be(1, "метку убираем и запоминаем отказ");
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart3, TiersOf).Should().Be(0);
        cart3.Items.Single().UnitPrice.Should().Be(100m, "после отказа ступень к строке больше не применяется");
        cart.Dispose(); cart2.Dispose(); cart3.Dispose();
    }

    [Fact]
    public void Volume_TierNotCheaperThanRetail_IsIgnored()
    {
        var (cart, _) = CartWith(100, 12);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, _ => new[] { new VolumeTier(3m, 120m) }).Should().Be(0);
        cart.Items.Single().UnitPrice.Should().Be(100m);
        cart.Dispose();
    }

    [Fact]
    public void Volume_ScanningTheSameProductAgain_AddsToTheSameLine_NotASecondOne()
    {
        var (cart, productId) = CartWith(100, 10);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf);
        cart.Items.Single().UnitPrice.Should().Be(80m);

        cart.AddItem(CartTestHelper.CreateProduct(productId, "Кроссовки", 100m), 1);

        cart.Items.Should().HaveCount(1, "строка со ступенью — та же строка товара, а не вторая по розничной цене");
        cart.Items.Single().Quantity.Should().Be(11);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf);
        cart.Items.Single().LineTotal.Should().Be(880m);
        cart.Dispose();
    }

    [Fact]
    public void Volume_TierPrice_GoesToTheSaleBodyAsExplicitPrice()
    {
        var (cart, _) = CartWith(100, 10);
        ReceiptSnapshotCartEditor.ApplyVolumePrices(cart, TiersOf);
        var body = QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "800", null, null, Shift, null, false, null, false, out var why)!;
        why.Should().BeNull();
        var item = body["items"]!.AsArray().Single()!.AsObject();
        item["price"]!.ToString().Should().Be("80.00");
        item["qty"]!.ToString().Should().Be("10");
        item["price_override"]!.GetValue<bool>().Should().BeTrue();
        QuickCheckoutBody.ComputeTotal(body).Should().Be(800d);
        cart.Dispose();
    }

    // ───────────── торг на чеке ─────────────

    [Fact]
    public void Haggle_CheckDiscount_Becomes1240From1247_RowsDoNotChange_ReasonIsKept()
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        cart.AddItem(CartTestHelper.CreateProduct(Guid.NewGuid().ToString(), "Куртка", 1000m), 1);
        cart.AddItem(CartTestHelper.CreateProduct(Guid.NewGuid().ToString(), "Шарф", 247m), 1);
        var baseSum = ReceiptSnapshotCartEditor.HaggleBase(cart.Root);
        baseSum.Should().Be(1247m);

        var r = BazaarHaggle.RoundDown(baseSum, 10);
        ReceiptSnapshotCartEditor.PatchOrderDiscount(cart, null, r.Discount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        ReceiptSnapshotCartEditor.MarkDiscountReason(cart, null, BazaarHaggle.ReasonValue).Should().BeTrue();

        CartTotalsCalculator.Calculate(cart.Root).TotalDue.Should().Be(1240d);
        cart.Items.Select(i => i.UnitPrice).Should().Equal(1000m, 247m);
        ReceiptSnapshotCartEditor.OrderDiscountIsHaggle(cart.Root).Should().BeTrue();

        // новая обычная скидка снимает причину «торг»
        ReceiptSnapshotCartEditor.PatchOrderDiscount(cart, "5", null);
        ReceiptSnapshotCartEditor.OrderDiscountIsHaggle(cart.Root).Should().BeFalse();
        cart.Dispose();
    }

    [Fact]
    public void Haggle_LineDiscount_BecomesAgreedSum_AndReasonIsOnTheLine()
    {
        var (cart, _) = CartWith(1247, 1);
        var id = cart.Items.Single().Id!;
        var r = BazaarHaggle.RoundDown(1247m, 50);
        ReceiptSnapshotCartEditor.PatchLineDiscount(cart, id, "sum", r.Discount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        ReceiptSnapshotCartEditor.MarkDiscountReason(cart, id, BazaarHaggle.ReasonValue).Should().BeTrue();

        cart.Items.Single().LineTotal.Should().Be(1200m);
        CartDisplayHelper.EnumerateItems(cart.Root).Single().GetProperty(BazaarHaggle.ReasonField).GetString().Should().Be("haggle");
        var body = QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "1200", null, null, Shift, null, false, null, false, out _)!;
        body["items"]!.AsArray().Single()!["discount"]!.ToString().Should().Be("47.00");
        body["items"]!.AsArray().Single()!.AsObject().ContainsKey(BazaarHaggle.ReasonField).Should().BeFalse("причина — только в снимке кассы, на сервер поля не идут");
        ReceiptSnapshotCartEditor.MarkDiscountReason(cart, "no-such-line", "haggle").Should().BeFalse();
        cart.Dispose();
    }

    // ───────────── валюта в чеке и теле продажи ─────────────

    [Fact]
    public void Currency_Cash20Dollars_GoesToPaymentsArray_AndChangeStaysInSom()
    {
        var (cart, _) = CartWith(1700, 1);
        var fx = new FxPayment("USD", 20m, 87.5m, 1750m);
        ReceiptSnapshotCartEditor.SetFxPayment(cart, fx);

        var body = QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "1750.00", null, null, Shift, null, false, null, false, out _)!;
        var payment = body["payment"]!.AsObject();
        payment["received"]!.ToString().Should().Be("1750.00");
        var entry = payment["payments"]!.AsArray().Single()!.AsObject();
        entry["currency"]!.ToString().Should().Be("USD");
        entry["amount"]!.ToString().Should().Be("1750.00");
        entry["amount_currency"]!.ToString().Should().Be("20");
        entry["rate"]!.ToString().Should().Be("87.5");
        QuickCheckoutBody.ComputeTotal(body).Should().Be(1700d, "сумма чека в сомах не меняется; сдача 50 — в сомах");

        // не наличные или сумма не сходится — валюта в тело не идёт
        QuickCheckoutBody.TryBuild(cart.GetRawText(), "transfer", "0", null, null, Shift, null, false, null, false, out _)!["payment"]!.AsObject().ContainsKey("payments").Should().BeFalse();
        QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "2000.00", null, null, Shift, null, false, null, false, out _)!["payment"]!.AsObject().ContainsKey("payments").Should().BeFalse("«Получено» правили вручную — сумма уже не сходится");

        ReceiptSnapshotCartEditor.SetFxPayment(cart, null);
        QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "1750.00", null, null, Shift, null, false, null, false, out _)!["payment"]!.AsObject().ContainsKey("payments").Should().BeFalse("оплата в сомах — тело прежнее");
        cart.Dispose();
    }

    [Fact]
    public void Currency_FxInCart_SurvivesAsSnapshotAndPrintsAsRateLine()
    {
        var (cart, _) = CartWith(1700, 1);
        ReceiptSnapshotCartEditor.SetFxPayment(cart, new FxPayment("USD", 20m, 87.5m, 1750m));
        var back = BazaarCurrencies.FromCart(cart.Root);
        back.Should().NotBeNull();
        back!.Currency.Should().Be("USD");
        back.AmountCurrency.Should().Be(20m);
        back.Rate.Should().Be(87.5m);
        back.AmountSom.Should().Be(1750m);

        var text = CartReceiptTextBuilder.BuildSimpleReceipt(cart.GetRawText(), null, "cash", "1750.00", new DateTime(2026, 11, 21, 12, 0, 0));
        text.Should().Contain("ПРИНЯТО 20 USD ПО КУРСУ 87.5");
        text.Should().Contain("СДАЧА");
        ReceiptSnapshotCartEditor.SetFxPayment(cart, null);
        CartReceiptTextBuilder.BuildSimpleReceipt(cart.GetRawText(), null, "cash", "1750.00", new DateTime(2026, 11, 21, 12, 0, 0)).Should().NotContain("ПО КУРСУ");
        cart.Dispose();
    }

    [Fact]
    public void ShiftReport_CashByCurrency_IsParsed()
    {
        using var doc = JsonDocument.Parse("""{"sales_total":"5000","shift":"s1","cash_by_currency":{"USD":{"amount_currency":"40.00","amount":"3500.00"},"BAD":1}}""");
        var report = NurMarketKassa.Models.ServerShiftReport.TryParse(doc.RootElement)!;
        report.CashByCurrency.Should().ContainKey("USD");
        report.CashByCurrency["USD"].AmountCurrency.Should().Be(40m);
        report.CashByCurrency["USD"].AmountSom.Should().Be(3500m);
        report.CashByCurrency.Should().NotContainKey("BAD");
        NurMarketKassa.Models.ServerShiftReport.TryParse(JsonDocument.Parse("""{"sales_total":"1"}""").RootElement)!.CashByCurrency.Should().BeEmpty();
    }
}

/// <summary>Профиль «Базар»: модули по умолчанию и выбор владельца; плитки категорий в модели каталога. Настройки — общий UserPreferences.Instance, значения возвращаются.</summary>
[Collection("UserPreferences")]
public sealed class BazaarSphereTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _json = UserPreferences.Instance.SphereSettingsJson;

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.SphereSettingsJson = _json;
    }

    private static void Bazaar(BazaarMode mode)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = "bazaar";
        p.SphereSettingsJson = null;
        SphereSettings.SetBazaarMode(mode);
    }

    [Fact]
    public void Modules_ByProfile_RetailHasFour_WholesaleAlsoHasInvoiceCargo()
    {
        Bazaar(BazaarMode.Retail);
        BazaarModules.CategoryTilesOn.Should().BeTrue();
        BazaarModules.VolumePriceOn.Should().BeTrue();
        BazaarModules.HaggleOn.Should().BeTrue();
        BazaarModules.CurrenciesOn.Should().BeTrue();
        BazaarModules.InvoiceCargoOn.Should().BeFalse("накладная и «карго» — модуль опта");
        BazaarModules.WholesaleMode.Should().BeFalse();

        Bazaar(BazaarMode.Wholesale);
        BazaarModules.InvoiceCargoOn.Should().BeTrue();
        BazaarModules.WholesaleMode.Should().BeTrue();
    }

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("grocery", "hozmag")]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    public void Modules_OtherSpheres_AreOff_SoNothingChangesThere(string baseSphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = baseSphere;
        p.MarketSphereDetail = detail;
        p.SphereSettingsJson = null;
        foreach (var id in BazaarModules.All)
            BazaarModules.IsOn(id).Should().BeFalse(id);
        BazaarModules.WholesaleMode.Should().BeFalse();
    }

    [Fact]
    public void Modules_OwnerChoice_Wins_AndCanBeReset_WithoutTouchingTheMode()
    {
        Bazaar(BazaarMode.Wholesale);
        BazaarModules.SetOwnerChoice(BazaarModules.Haggle, false).Should().BeTrue();
        BazaarModules.HaggleOn.Should().BeFalse("владелец выключил торг");
        BazaarModules.CategoryTilesOn.Should().BeTrue("остальные модули не тронуты");
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Wholesale);

        BazaarModules.SetOwnerChoice(BazaarModules.Haggle, null).Should().BeTrue();
        BazaarModules.HaggleOn.Should().BeTrue("вернули умолчание профиля");
        BazaarModules.SetOwnerChoice("unknown_module", true).Should().BeFalse();
    }

    [Fact]
    public void Haggle_Limits_AreReadFromSettings_AndValidated()
    {
        Bazaar(BazaarMode.Retail);
        BazaarHaggle.PinPercent.Should().Be(5);
        BazaarHaggle.HardPercent.Should().Be(10);
        BazaarHaggle.SetPercents(3, 8).Should().BeTrue();
        BazaarHaggle.PinPercent.Should().Be(3);
        BazaarHaggle.HardPercent.Should().Be(8);
        BazaarHaggle.GuardLevel(4).Should().Be(HaggleGuardLevel.NeedsPin);
        BazaarHaggle.GuardLevel(9).Should().Be(HaggleGuardLevel.SeniorOnly);
        BazaarHaggle.SetPercents(10, 5).Should().BeFalse("предел не бывает ниже порога PIN");
        BazaarHaggle.SetPercents(5, 120).Should().BeFalse();
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Retail, "режим не тронут");
    }

    // ───────────── плитки категорий ─────────────

    private sealed class FakeCache : NurMarketKassa.Core.Contracts.ICatalogCacheService
    {
        public Task<NurMarketKassa.Core.Contracts.CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new NurMarketKassa.Core.Contracts.CatalogSyncResult { Success = true });
        public bool TryLoadFromDatabase() => true;
        public IReadOnlyList<CatalogProductTileVm> GetProducts() => new List<CatalogProductTileVm>();
    }

    private sealed class Online : NurMarketKassa.Core.Contracts.IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static CatalogPanelViewModel Catalog(params (string Title, string? Category)[] products)
    {
        var list = products.Select((p, i) =>
        {
            var tile = CatalogTestHelper.CreateTile("id" + i, p.Title, "460000" + i.ToString("D6"), 100 + i);
            tile.Category = p.Category;
            return tile;
        }).ToList();
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, new object[] { list, false });
        return vm;
    }

    [Fact]
    public void Tiles_ShowCategories_ThenOneTapOpensItsProducts_AndBackReturnsToTiles()
    {
        Bazaar(BazaarMode.Retail);
        var vm = Catalog(("Кроссовки Nike", "Обувь"), ("Кроссовки Adidas", "Обувь"), ("Куртка", "Одежда"), ("Носки", "Одежда"), ("Шнурки", null));

        vm.CategoryTilesMode.Should().BeTrue();
        vm.ShowCategoryTiles.Should().BeTrue("в корне каталога вместо списка — плитки категорий");
        vm.ShowProductGrid.Should().BeFalse();
        vm.CategoryTiles.Select(t => t.Key).Should().Equal(new[] { "Обувь", "Одежда", "" }, "«Без категории» — последней");
        vm.CategoryTiles.Select(t => t.Count).Should().Equal(2, 2, 1);
        vm.CategoryTiles.Last().IsOther.Should().BeTrue();
        vm.CategoryTiles.All(t => t.Title.Length > 0 && t.CountText.Length > 0).Should().BeTrue();

        vm.OpenCategoryCommand.Execute(vm.CategoryTiles[0]);

        vm.ShowCategoryTiles.Should().BeFalse();
        vm.InBazaarCategory.Should().BeTrue();
        vm.BazaarCategoryTitle.Should().Be("Обувь");
        vm.Products.Select(p => p.Title).Should().Equal("Кроссовки Adidas", "Кроссовки Nike");
        vm.ShowProductGrid.Should().BeTrue();

        vm.BackToCategoriesCommand.Execute(null);
        vm.ShowCategoryTiles.Should().BeTrue();
        vm.InBazaarCategory.Should().BeFalse();
        vm.Products.Should().HaveCount(5);
    }

    [Fact]
    public void Tiles_SearchLooksThroughTheWholeCatalog_AndHidesTheTiles()
    {
        Bazaar(BazaarMode.Retail);
        var vm = Catalog(("Кроссовки Nike", "Обувь"), ("Куртка", "Одежда"));
        vm.OpenCategoryCommand.Execute(vm.CategoryTiles.First(t => t.Key == "Обувь"));
        vm.Products.Should().ContainSingle();

        vm.SearchText = "куртка";
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !(vm.Products.Count == 1 && vm.Products[0].Title == "Куртка"))
            Thread.Sleep(30);

        vm.Products.Select(p => p.Title).Should().Equal("Куртка");
        vm.ShowCategoryTiles.Should().BeFalse();
        vm.InBazaarCategory.Should().BeFalse("при поиске строка «Все категории» не нужна");
    }

    [Fact]
    public void Tiles_WithoutRealCategories_OrWithoutModule_ListStaysAsBefore()
    {
        Bazaar(BazaarMode.Retail);
        var noCategories = Catalog(("А", null), ("Б", null));
        noCategories.ShowCategoryTiles.Should().BeFalse("одна «Без категории» — плитки не нужны");
        noCategories.ShowProductGrid.Should().BeTrue();

        BazaarModules.SetOwnerChoice(BazaarModules.CategoryTiles, false);
        var off = Catalog(("Кроссовки", "Обувь"), ("Куртка", "Одежда"));
        off.CategoryTilesMode.Should().BeFalse();
        off.ShowCategoryTiles.Should().BeFalse();
        off.CategoryTiles.Should().BeEmpty();
        off.Products.Should().HaveCount(2, "без модуля каталог — как всегда");
        off.ShowProductGrid.Should().BeTrue();

        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        p.SphereSettingsJson = null;
        var other = Catalog(("Кроссовки", "Обувь"), ("Куртка", "Одежда"));
        other.CategoryTilesMode.Should().BeFalse("в других сферах плиток категорий нет");
        other.ShowProductGrid.Should().BeTrue();
    }
}
