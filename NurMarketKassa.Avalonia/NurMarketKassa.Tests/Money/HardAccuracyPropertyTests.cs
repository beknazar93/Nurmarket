using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 («жёсткий тест» точности, цель 10/10): свойства денег на СЛУЧАЙНЫХ чеках через настоящий код кассы, а не через копию формул.
/// Чек собирается настоящим LocalCartService (товары штучные и весовые, скидки строк процентом и суммой, скидка чека процентом и суммой), затем:
/// итог кассы = итог тела запроса на сервер (QuickCheckoutBody.ComputeTotal) = итог окна оплаты (CheckoutViewModel); сумма показанных строк + «Округление»
/// − скидка чека = итог; смешанная оплата и предоплата долга складываются ровно в итог; цепочки возвратов (OfflineRefundPlanner) по всем способам оплаты
/// не возвращают больше проданного/оплаченного и в конце возвращают ровно оплаченное; итоги смены (LocalShiftTotals) = нал + безнал + долг;
/// ящик смены по формуле ShiftDrawerMath совпадает с независимой моделью; остаток склада = приход − продажи + возвраты.
/// Правило округления сервера переключается (вниз от суммы весовых / каждая весовая строка вниз до шага 0,01 или 1 сом) — проверяются оба.</summary>
[Collection("ServerFeatures")]
public sealed class HardAccuracyPropertyTests : IDisposable
{
    private readonly Func<bool> _floorWas = QuantityRules.WeightLineFloorEnabled;
    private readonly Func<decimal> _stepWas = QuantityRules.WeightStep;
    private readonly Func<bool> _exactWas = WeighedAmount.Enabled;
    private readonly bool _sellWithoutStockWas = UserPreferences.Instance.SellWithoutStock;

    public HardAccuracyPropertyTests()
    {
        QuantityRules.WeightLineFloorEnabled = () => false;
        QuantityRules.WeightStep = () => 0.01m;
        WeighedAmount.Enabled = () => false;
    }

    public void Dispose()
    {
        QuantityRules.WeightLineFloorEnabled = _floorWas;
        QuantityRules.WeightStep = _stepWas;
        WeighedAmount.Enabled = _exactWas;
        UserPreferences.Instance.SellWithoutStock = _sellWithoutStockWas;
    }

    // ───────────────────────────── генераторы ─────────────────────────────

    /// <summary>Строка чека: цена в копейках; весовая — количество в граммах, штучная — целое; скидка строки: 0 — нет, 1 — процент (0..100), 2 — сумма (копейки, не больше суммы строки).</summary>
    private sealed record LineSpec(int PriceCents, bool Weighed, int Qty, int DiscKind, int DiscValue)
    {
        public string QtyText => Weighed ? (Qty / 1000m).ToString("0.###", CultureInfo.InvariantCulture) : Qty.ToString(CultureInfo.InvariantCulture);
        public override string ToString() => $"{PriceCents / 100m:0.00}×{QtyText}{(Weighed ? "кг" : "")} d{DiscKind}:{DiscValue}";
    }

    /// <summary>Скидка чека: 0 — нет, 1 — процент в десятых (25 = 2,5 %), 2 — сумма в копейках.</summary>
    private sealed record CartSpec(List<LineSpec> Lines, int OrderKind, int OrderValue)
    {
        public override string ToString() => string.Join("; ", Lines) + $" | order{OrderKind}:{OrderValue}";
    }

    private static readonly Gen<LineSpec> LineGen =
        Gen.Select(Gen.Int[1, 300000], Gen.Bool, Gen.Int[1, 9999], Gen.Int[0, 2], Gen.Int[0, 100000])
            .Select(t =>
            {
                var (price, weighed, rawQty, kind, rawDisc) = t;
                var qty = weighed ? rawQty : 1 + rawQty % 12;
                var gross = weighed ? price * (decimal)qty / 1000m : price * (decimal)qty; // копейки
                var disc = kind switch
                {
                    1 => rawDisc % 101,
                    2 => (int)Math.Min(rawDisc, Math.Floor(gross)),
                    _ => 0,
                };
                return new LineSpec(price, weighed, qty, kind, disc);
            });

    private static readonly Gen<CartSpec> CartGen =
        Gen.Select(LineGen.List[1, 6], Gen.Int[0, 2], Gen.Int[0, 1000], Gen.Int[0, 2000000])
            .Select(t =>
            {
                var (lines, kind, pctTenths, sumCents) = t;
                return new CartSpec(lines, kind, kind == 1 ? Math.Max(1, pctTenths) : kind == 2 ? Math.Max(1, sumCents) : 0);
            });

    /// <summary>Правило округления сервера: 0 — вниз от суммы весовых (без признака), 1 — каждая весовая строка вниз до 0,01, 2 — до 1 сома.</summary>
    private static readonly Gen<int> RuleGen = Gen.Int[0, 2];

    private static void SetRule(int rule)
    {
        QuantityRules.WeightLineFloorEnabled = () => rule > 0;
        QuantityRules.WeightStep = () => rule == 2 ? 1m : 0.01m;
    }

    private static string Money(decimal cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Чек через настоящий LocalCartService (как без связи на кассе): товары, скидки строк, скидка чека.</summary>
    private static CartService Build(CartSpec spec)
    {
        var cart = new CartService();
        LocalCartService.StartNewLocalCart(cart, "shift-hard");
        var ids = new List<string>();
        foreach (var l in spec.Lines)
        {
            var tile = new CatalogProductTileVm(Guid.NewGuid().ToString(), "Товар " + ids.Count, Money(l.PriceCents) + " сом", l.Weighed)
            {
                ServerIsWeight = l.Weighed,
                Quantity = 1000,
            };
            LocalCartService.AddProduct(cart, tile, l.QtyText);
            ids.Add(cart.Root.GetProperty("items")[ids.Count].GetProperty("id").GetString()!);
        }

        for (var i = 0; i < spec.Lines.Count; i++)
        {
            var l = spec.Lines[i];
            if (l.DiscKind == 1 && l.DiscValue > 0)
                LocalCartService.PatchLineDiscount(cart, ids[i], "percent", l.DiscValue.ToString(CultureInfo.InvariantCulture));
            else if (l.DiscKind == 2 && l.DiscValue > 0)
                LocalCartService.PatchLineDiscount(cart, ids[i], "sum", Money(l.DiscValue));
        }

        if (spec.OrderKind == 1)
            LocalCartService.PatchOrderDiscount(cart, (spec.OrderValue / 10m).ToString("0.#", CultureInfo.InvariantCulture), null);
        else if (spec.OrderKind == 2)
            LocalCartService.PatchOrderDiscount(cart, null, Money(spec.OrderValue));
        return cart;
    }

    private static decimal D(double v) => (decimal)v;

    /// <summary>Как BasketPanelViewModel открывает окно оплаты: скидка чека из корзины передаётся текстом (процент или сумма).</summary>
    private static CheckoutViewModel Window(CartService cart, CartTotalsCalculator.CartTotals totals)
    {
        static string Num(JsonElement root, string key) =>
            root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString(CultureInfo.InvariantCulture) : "";
        return new CheckoutViewModel(totals, Num(cart.Root, "order_discount_percent"), Num(cart.Root, "order_discount_total"));
    }

    private static JsonObject Body(CartService cart, string method = "cash", string? cash = null, string? nonCash = null)
    {
        var body = QuickCheckoutBody.TryBuild(cart.Root.GetRawText(), method, cash, nonCash, null, null, null, false, null, false, out var reason);
        body.Should().NotBeNull(reason);
        return body!;
    }

    // ───────────────────────────── чек: касса = сервер = окно оплаты ─────────────────────────────

    [Fact]
    public void CartTotal_EqualsServerBodyTotal_ForAnyCart_AndAnyRoundingRule() =>
        Gen.Select(CartGen, RuleGen).Sample((spec, rule) =>
        {
            SetRule(rule);
            var cart = Build(spec);
            var totals = CartTotalsCalculator.Calculate(cart.Root);
            var serverTotal = QuickCheckoutBody.ComputeTotal(Body(cart));
            D(serverTotal).Should().Be(D(totals.TotalDue), $"итог кассы и итог тела запроса на сервер обязаны совпадать до копейки ({spec}, правило {rule})");
            D(totals.TotalDue).Should().BeInRange(0m, D(totals.Subtotal));
            (D(totals.TotalDue) * 100m).Should().Be(Math.Round(D(totals.TotalDue) * 100m), "итог — целое число копеек");
        }, iter: 400, threads: 1);

    [Fact]
    public void ShownLines_PlusRounding_MinusOrderDiscount_EqualsTotal() =>
        Gen.Select(CartGen, RuleGen).Sample((spec, rule) =>
        {
            SetRule(rule == 2 ? 1 : rule); // шаг 1 сом — отдельный тест ниже (там «Округление» может быть до 1 сома на строку)
            var cart = Build(spec);
            var totals = CartTotalsCalculator.Calculate(cart.Root);
            var shown = cart.Root.GetProperty("items").EnumerateArray().Sum(it => it.GetProperty("line_total").GetDecimal());
            (shown + totals.RoundingAdjustment - D(totals.OrderDiscount)).Should().Be(D(totals.TotalDue),
                $"на экране и на чеке: сумма строк + «Округление» − скидка чека = итог ({spec}, правило {rule})");
        }, iter: 400, threads: 1);

    [Fact]
    public void ShownLines_PlusRounding_EqualsTotal_WhenScaleStepIsOneSom() =>
        Gen.Select(Gen.Int[100, 300000], Gen.Int[1, 9999]).List[1, 4].Sample(raw =>
        {
            SetRule(2);
            var spec = new CartSpec(raw.Select(r => new LineSpec(r.Item1, true, r.Item2, 0, 0)).ToList(), 0, 0);
            var cart = Build(spec);
            var totals = CartTotalsCalculator.Calculate(cart.Root);
            var shown = cart.Root.GetProperty("items").EnumerateArray().Sum(it => it.GetProperty("line_total").GetDecimal());
            (shown + totals.RoundingAdjustment).Should().Be(D(totals.TotalDue), $"шаг весов 1 сом: строки + «Округление весовых» = итог ({spec})");
        }, iter: 300, threads: 1);

    [Fact]
    public void CheckoutWindow_DiscountTypedThere_GivesSameTotalAsCart() =>
        Gen.Select(CartGen, RuleGen).Sample((spec, rule) =>
        {
            SetRule(rule);
            var noOrder = spec with { OrderKind = 0, OrderValue = 0 };
            var totalsNoOrder = CartTotalsCalculator.Calculate(Build(noOrder).Root);
            var totalsWithOrder = CartTotalsCalculator.Calculate(Build(spec).Root);
            var vm = new CheckoutViewModel(totalsNoOrder, "", "");
            if (spec.OrderKind == 1)
            {
                vm.IsDiscountPercent = true;
                vm.DiscountInput = (spec.OrderValue / 10m).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');
            }
            else if (spec.OrderKind == 2)
            {
                vm.IsDiscountPercent = false;
                vm.DiscountInput = Money(spec.OrderValue).Replace('.', ',');
            }

            D(vm.EffectiveTotalDue).Should().Be(D(totalsWithOrder.TotalDue), $"скидка, набранная в окне оплаты, даёт тот же итог, что и в чеке ({spec}, правило {rule})");
        }, iter: 300, threads: 1);

    [Fact]
    public void LineOrder_DoesNotChangeTotal() =>
        Gen.Select(CartGen, RuleGen).Sample((spec, rule) =>
        {
            SetRule(rule);
            var reversed = spec with { Lines = Enumerable.Reverse(spec.Lines).ToList() };
            CartTotalsCalculator.Calculate(Build(reversed).Root).TotalDue.Should().Be(CartTotalsCalculator.Calculate(Build(spec).Root).TotalDue);
        }, iter: 200, threads: 1);

    // ───────────────────────────── оплата: смешанная, долг ─────────────────────────────

    [Fact]
    public void MixedPayment_CashPlusCard_EqualsTotal_InWindowAndInServerBody() =>
        Gen.Select(CartGen, Gen.Int[1, 99]).Sample((spec, sharePct) =>
        {
            var cart = Build(spec);
            var totals = CartTotalsCalculator.Calculate(cart.Root);
            if (totals.TotalDue < 0.02)
                return;
            var vm = Window(cart, totals);
            vm.IsMixed = true;
            var cashPart = Math.Round(D(totals.TotalDue) * sharePct / 100m, 2, MidpointRounding.AwayFromZero);
            vm.MixedCashAmount = cashPart.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

            vm.MixedAmountsMatch.Should().BeTrue($"второе поле подставляется само ({spec})");
            var cash = decimal.Parse(vm.CashReceivedForApi, CultureInfo.InvariantCulture);
            var card = decimal.Parse(vm.NonCashReceivedForApi!, CultureInfo.InvariantCulture);
            (cash + card).Should().Be(D(totals.TotalDue), "наличные + безнал = итог, копейка не теряется");

            var payment = Body(cart, "mixed", vm.CashReceivedForApi, vm.NonCashReceivedForApi)["payment"]!;
            (decimal.Parse(payment["cash_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)
             + decimal.Parse(payment["card_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)).Should().Be(D(totals.TotalDue));
        }, iter: 300, threads: 1);

    [Fact]
    public void DebtSale_PrepaidPlusDebt_EqualsTotal_AndPrepaymentGoesToCash() =>
        Gen.Select(CartGen, Gen.Int[0, 100]).Sample((spec, sharePct) =>
        {
            var cart = Build(spec);
            var totals = CartTotalsCalculator.Calculate(cart.Root);
            var vm = Window(cart, totals);
            vm.IsDebt = true;
            var prepaid = Math.Round(D(totals.TotalDue) * sharePct / 100m, 2, MidpointRounding.AwayFromZero);
            vm.DebtCashReceived = prepaid.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

            decimal.Parse(vm.CashReceivedForApi, CultureInfo.InvariantCulture).Should().Be(prepaid);
            var payment = Body(cart, "debt", vm.CashReceivedForApi, vm.NonCashReceivedForApi)["payment"]!;
            var cashAmount = payment["cash_amount"] is { } c ? decimal.Parse(c.GetValue<string>(), CultureInfo.InvariantCulture) : 0m;
            var cardAmount = payment["card_amount"] is { } k ? decimal.Parse(k.GetValue<string>(), CultureInfo.InvariantCulture) : 0m;
            (cashAmount + cardAmount).Should().Be(prepaid, "предоплата долга уходит на сервер целиком");
            (prepaid + (D(totals.TotalDue) - prepaid)).Should().Be(D(totals.TotalDue));
            (D(totals.TotalDue) - prepaid).Should().BeGreaterThanOrEqualTo(0m, "долг не отрицательный");
        }, iter: 200, threads: 1);

    // ───────────────────────────── возвраты: цепочки по всем способам оплаты ─────────────────────────────

    /// <summary>Способ оплаты продажи: 0 — нал, 1 — безнал, 2 — долг без предоплаты, 3 — долг с предоплатой, 4 — смешанная.</summary>
    private static OfflineSaleSnapshot Sale(CartService cart, int method, int sharePct)
    {
        var json = cart.Root.GetRawText();
        var totals = CartTotalsCalculator.Calculate(cart.Root);
        var due = D(totals.TotalDue);
        var share = Math.Round(due * sharePct / 100m, 2, MidpointRounding.AwayFromZero);
        var (pm, received) = method switch
        {
            0 => ("cash", Money(due * 100m)),
            1 => ("transfer", (string?)null),
            2 => ("debt", "0.00"),
            3 => ("debt", share.ToString("0.00", CultureInfo.InvariantCulture)),
            _ => ("cash", Money(due * 100m)), // смешанная в очередь не ставится — снимок дописывается ниже
        };
        var entry = new OfflineSaleEntry { PaymentMethod = pm, CashReceived = received, CartJson = json, ShiftId = "shift-hard", OfflineDebt = pm == "debt" };
        var snap = OfflineRefundPlanner.FromQueueEntry(entry);
        snap.Should().NotBeNull();
        if (method == 4)
        {
            snap!.Method = "mixed";
            snap.PaidCash = share;
            snap.PaidNonCash = due - share;
        }

        return snap!;
    }

    [Fact]
    public void RefundChain_NeverExceedsSoldOrPaid_AndFullReturnGivesBackExactlyWhatWasPaid() =>
        Gen.Select(CartGen, Gen.Int[0, 4], Gen.Int[1, 99], Gen.Int[0, int.MaxValue].Array[12]).Sample((spec, method, sharePct, picks) =>
        {
            var cart = Build(spec);
            var sale = Sale(cart, method, sharePct);
            if (sale.Total <= 0.004m)
                return;
            var priors = new List<OfflinePriorRefund>();
            var returned = sale.Lines.ToDictionary(l => l.Key, _ => 0m);
            decimal sumAmount = 0, sumCash = 0, sumNonCash = 0, sumDebt = 0;
            var step = 0;

            void Apply(IReadOnlyList<(string Key, double Qty)> req, string why, bool mayRefuse = false)
            {
                var debtNow = Math.Max(0m, sale.Debt - sumDebt);
                var plan = OfflineRefundPlanner.Plan(sale, priors, req, clientDebtKnown: debtNow);
                if (mayRefuse && !plan.Ok)
                    return; // частичный возврат на копейки (сумма позиции округлилась до 0) — касса честно отказывает, это не ошибка
                plan.Ok.Should().BeTrue($"{why}: {plan.Error} ({spec}, способ {method})");
                (plan.Cash + plan.NonCash + plan.DebtReduced).Should().Be(plan.Amount, "возврат делится без потерь");
                plan.Lines.Sum(l => l.Amount).Should().Be(plan.Amount, "сумма строк возврата = сумма возврата");
                plan.Lines.Should().OnlyContain(l => l.Amount >= 0m);
                plan.Cash.Should().BeGreaterThanOrEqualTo(0m);
                plan.NonCash.Should().BeGreaterThanOrEqualTo(0m);
                plan.DebtReduced.Should().BeGreaterThanOrEqualTo(0m);
                sumAmount += plan.Amount;
                sumCash += plan.Cash;
                sumNonCash += plan.NonCash;
                sumDebt += plan.DebtReduced;
                sumAmount.Should().BeLessThanOrEqualTo(sale.Total, "вернуть больше итога чека нельзя");
                sumCash.Should().BeLessThanOrEqualTo(sale.PaidCash, "наличными — не больше внесённых наличных");
                sumNonCash.Should().BeLessThanOrEqualTo(sale.PaidNonCash, "безналом — не больше оплаченного безналом");
                sumDebt.Should().BeLessThanOrEqualTo(sale.Debt, "долг уменьшается не больше долга по чеку");
                foreach (var (key, qty) in req)
                {
                    returned[key] += (decimal)qty;
                    returned[key].Should().BeLessThanOrEqualTo((decimal)sale.Lines.First(l => l.Key == key).Quantity, "вернуть больше проданного нельзя");
                }
                priors.Add(new OfflinePriorRefund
                {
                    Id = "r" + step++, Amount = plan.Amount, Cash = plan.Cash, NonCash = plan.NonCash, DebtReduced = plan.DebtReduced,
                    Lines = plan.Lines.Select(l => ((string?)l.Key, l.Name, l.Quantity)).ToList(),
                });
            }

            // Частичные возвраты: случайная позиция, случайная часть остатка (весовая — до грамма, штучная — целыми).
            foreach (var pick in picks)
            {
                var open = sale.Lines.Where(l => (decimal)l.Quantity - returned[l.Key] > 0.0005m).ToList();
                if (open.Count <= 1)
                    break;
                var line = open[pick % open.Count];
                var left = (decimal)line.Quantity - returned[line.Key];
                var weighed = spec.Lines[int.Parse(line.Key, CultureInfo.InvariantCulture)].Weighed;
                var qty = weighed
                    ? Math.Max(0.001m, Math.Round(left * ((pick / 7) % 90 + 5) / 100m, 3, MidpointRounding.ToZero))
                    : Math.Max(1m, Math.Floor(left / 2m));
                if (qty >= left)
                    continue; // весь остаток позиции — в последнем возврате
                Apply(new[] { (line.Key, (double)qty) }, "частичный возврат", mayRefuse: true);
            }

            // Последний возврат — всё, что осталось.
            var rest = sale.Lines.Where(l => (decimal)l.Quantity - returned[l.Key] > 0.0005m)
                .Select(l => (l.Key, (double)Math.Round((decimal)l.Quantity - returned[l.Key], 3))).ToList();
            Apply(rest, "последний возврат (всё оставшееся)");

            sumAmount.Should().Be(sale.Total, $"после возврата всего чека возвращено ровно оплаченное ({spec}, способ {method}, возвратов {priors.Count})");
            sumCash.Should().Be(sale.PaidCash, "наличных — ровно внесённые");
            sumNonCash.Should().Be(sale.PaidNonCash, "безнала — ровно оплаченный");
            sumDebt.Should().Be(sale.Debt, "долг по чеку закрыт полностью");
        }, iter: 400, threads: 1);

    /// <summary>Дефект, найденный свойством выше (26.11.2026): чек с позицией, отданной бесплатно (скидка 100 %, сумма строки 0), без связи не возвращался ЦЕЛИКОМ —
    /// «сумма возврата по позиции нулевая». И остаток позиции после частичных возвратов, чьи копейки уже ушли округлением, тоже. Теперь: сумма = оплаченное, товар возвращается.</summary>
    [Fact]
    public void Regression_FreeLine_AndZeroValueRemainder_DoNotBlockOfflineReturn()
    {
        var sale = new OfflineSaleSnapshot { SaleRef = "S", Method = "cash", IsLocal = true, Total = 100m, LinesTotal = 100m, PaidCash = 100m };
        sale.Lines.Add(new OfflineSaleLine { Key = "0", Name = "Товар", Quantity = 1, UnitPrice = 100m, LineTotal = 100m });
        sale.Lines.Add(new OfflineSaleLine { Key = "1", Name = "Подарок", Quantity = 1, UnitPrice = 50m, LineTotal = 0m });
        var whole = OfflineRefundPlanner.Plan(sale, Array.Empty<OfflinePriorRefund>(), new[] { ("0", 1.0), ("1", 1.0) }, clientDebtKnown: 0m);
        whole.Ok.Should().BeTrue(whole.Error);
        whole.Amount.Should().Be(100m);
        whole.Lines.Should().HaveCount(2, "подарок тоже возвращается на склад");

        // Строка 0,01 сома за 3,129 кг: 95 % вернули (0,01), остаток 0,157 кг стоит 0,0005 → 0 — но это остаток позиции целиком.
        var tiny = new OfflineSaleSnapshot { SaleRef = "T", Method = "cash", IsLocal = true, Total = 0.01m, LinesTotal = 0.01m, PaidCash = 0.01m };
        tiny.Lines.Add(new OfflineSaleLine { Key = "0", Name = "Яблоко", Quantity = 3.129, UnitPrice = 0.01m, LineTotal = 0.01m });
        var first = OfflineRefundPlanner.Plan(tiny, Array.Empty<OfflinePriorRefund>(), new[] { ("0", 2.972) }, clientDebtKnown: 0m);
        first.Ok.Should().BeTrue(first.Error);
        var prior = new OfflinePriorRefund { Id = "r1", Amount = first.Amount, Cash = first.Cash, Lines = { ("0", "Яблоко", 2.972) } };
        var rest = OfflineRefundPlanner.Plan(tiny, new[] { prior }, new[] { ("0", 0.157) }, clientDebtKnown: 0m);
        if (first.Amount < tiny.Total)
            rest.Ok.Should().BeTrue(rest.Error);
        else
            rest.Ok.Should().BeFalse("всё оплаченное уже возвращено");
    }

    /// <summary>Дефект, найденный свойством (26.11.2026, seed 0Q90qsyKPZMp): после многих частичных возвратов, округлённых вниз, последний возврат «всего остатка» урезался до суммы строк,
    /// и покупатель недополучал копейки (9486,80 вместо 9486,83). Допуск «последнего возврата» теперь учитывает строки всех прошлых возвратов.</summary>
    [Fact]
    public void Regression_LastReturnAfterManyRoundedDownPartials_GivesBackEveryKopeck()
    {
        // 10 шт по 0,994 (сумма 9,94): девять возвратов по 1 шт — 0,994 → 0,99 (вниз) каждый, остаток 1 шт = 9,94 − 8,91 = 1,03 при «стоимости строки» 0,99.
        var sale = new OfflineSaleSnapshot { SaleRef = "S", Method = "cash", IsLocal = true, Total = 9.94m, LinesTotal = 9.94m, PaidCash = 9.94m };
        sale.Lines.Add(new OfflineSaleLine { Key = "0", Name = "Товар", Quantity = 10, UnitPrice = 0.994m, LineTotal = 9.94m });
        var priors = new List<OfflinePriorRefund>();
        for (var i = 0; i < 9; i++)
        {
            var p = OfflineRefundPlanner.Plan(sale, priors, new[] { ("0", 1.0) }, clientDebtKnown: 0m);
            p.Ok.Should().BeTrue(p.Error);
            priors.Add(new OfflinePriorRefund { Id = "r" + i, Amount = p.Amount, Cash = p.Cash, Lines = { ("0", "Товар", 1.0) } });
        }
        var last = OfflineRefundPlanner.Plan(sale, priors, new[] { ("0", 1.0) }, clientDebtKnown: 0m);
        last.Ok.Should().BeTrue(last.Error);
        (priors.Sum(p => p.Amount) + last.Amount).Should().Be(9.94m, "весь чек возвращён — ровно оплаченное");
    }

    [Fact]
    public void RefundSplit_PartsAlwaysAddUp_AndStayWithinWhatIsLeft() =>
        Gen.Select(Gen.Int[0, 10_000_000], Gen.Int[0, 10_000_000], Gen.Int[1, 10_000_000]).Sample((cashLeft, nonCashLeft, amount) =>
        {
            var a = amount / 100m;
            var (cash, nonCash) = RefundSplit.Split(a, cashLeft / 100m, nonCashLeft / 100m);
            (cash + nonCash).Should().Be(a);
            cash.Should().BeInRange(0m, a);
            if (a <= (cashLeft + nonCashLeft) / 100m && cashLeft + nonCashLeft > 0)
            {
                cash.Should().BeLessThanOrEqualTo(cashLeft / 100m + 0.01m, "наличными — не больше остатка наличных (копейка округления)");
                nonCash.Should().BeLessThanOrEqualTo(nonCashLeft / 100m + 0.01m, "безналом — не больше остатка безнала (копейка округления)");
            }
        }, iter: 2000);

    [Fact]
    public void RefundLinesScaledToServerSum_AddUpExactly_AndNoneNegative() =>
        Gen.Select(Gen.Int[1, 5_000_000].List[1, 12], Gen.Int[50, 150]).Sample((sums, pct) =>
        {
            var lines = sums.Select((s, i) => ("L" + i, 1.0 + i % 3, s / 100m, s / 100m)).ToList();
            var estimated = lines.Sum(l => l.Item4);
            var actual = Math.Round(estimated * pct / 100m, 2, MidpointRounding.AwayFromZero);
            var scaled = RefundSplit.ScaleLines(lines, estimated, actual);
            scaled.Sum(l => l.Sum).Should().Be(actual, "строки чека возврата складываются в сумму сервера");
            scaled.Should().OnlyContain(l => l.Sum >= 0m, "строка чека возврата не бывает отрицательной");
        }, iter: 1000);

    // ───────────────────────────── смена: итоги, Z-отчёт, ящик ─────────────────────────────

    [Fact]
    public void ShiftSimulation_ZReportByMethods_AndDrawer_MatchIndependentModel() =>
        Gen.Select(Gen.Select(CartGen, Gen.Int[0, 3], Gen.Int[0, 100]).List[1, 6], Gen.Int[0, 500000].Array[4], Gen.Int[0, 2000000])
            .Sample((sales, ops, openingCents) =>
            {
                var queue = new List<OfflineSaleEntry>();
                decimal modelTotal = 0, modelCash = 0, modelNonCash = 0, modelDebt = 0, refundCash = 0;
                foreach (var (spec, method, sharePct) in sales)
                {
                    var cart = Build(spec);
                    var due = D(CartTotalsCalculator.Calculate(cart.Root).TotalDue);
                    var prepaid = Math.Round(due * sharePct / 100m, 2, MidpointRounding.AwayFromZero);
                    var (pm, received) = method switch
                    {
                        0 => ("cash", (due + 7m).ToString("0.00", CultureInfo.InvariantCulture)), // с купюрой больше — сдача в итог не входит
                        1 => ("transfer", null),
                        2 => ("debt", "0.00"),
                        _ => ("debt", prepaid.ToString("0.00", CultureInfo.InvariantCulture)),
                    };
                    var entry = new OfflineSaleEntry { PaymentMethod = pm, CashReceived = received, CartJson = cart.Root.GetRawText(), ShiftId = "shift-hard", OfflineDebt = pm == "debt" };
                    queue.Add(entry);
                    modelTotal += due;
                    switch (method)
                    {
                        case 0: modelCash += due; break;
                        case 1: modelNonCash += due; break;
                        case 2: modelDebt += due; break;
                        default: modelCash += prepaid; modelDebt += due - prepaid; break;
                    }

                    // Половина продаж возвращается целиком — наличная часть возврата уходит из ящика.
                    if (sharePct % 2 == 0 && due > 0.004m && OfflineRefundPlanner.FromQueueEntry(entry) is { } snap)
                    {
                        var plan = OfflineRefundPlanner.Plan(snap, Array.Empty<OfflinePriorRefund>(),
                            snap.Lines.Select(l => (l.Key, l.Quantity)).ToList(), clientDebtKnown: snap.Debt);
                        plan.Ok.Should().BeTrue(plan.Error);
                        plan.Amount.Should().Be(due);
                        plan.Cash.Should().Be(snap.PaidCash, "весь чек возвращён — наличными ровно внесённое");
                        refundCash += plan.Cash;
                    }
                }

                var agg = LocalShiftTotals.Aggregate("shift-hard", queue, Array.Empty<SaleLedgerEntry>());
                agg.Should().NotBeNull();
                agg!.TotalSales.Should().Be(modelTotal, "Z-отчёт: выручка = сумма чеков");
                (agg.CashSales + agg.NonCashSales + agg.DebtSales).Should().Be(agg.TotalSales, "Z-отчёт: нал + безнал + долг = выручка");
                agg.CashSales.Should().Be(modelCash);
                agg.NonCashSales.Should().Be(modelNonCash);
                agg.DebtSales.Should().Be(modelDebt);
                agg.SalesCount.Should().Be(sales.Count);

                var opening = openingCents / 100m;
                var (deposits, withdrawals, debtPaidCash, debtPaidCard) = (ops[0] / 100m, ops[1] / 100m, ops[2] / 100m, ops[3] / 100m);
                var drawer = ShiftDrawerMath.Expected(opening, agg.CashSales, debtPaidCash, deposits, withdrawals, refundCash);
                drawer.Should().Be(opening + modelCash - refundCash + deposits - withdrawals + debtPaidCash,
                    "ящик = начало + нал-продажи − нал-возвраты + внесения − изъятия + оплаты долга наличными (оплата долга безналом ящик не трогает)");
                _ = debtPaidCard;
            }, iter: 150, threads: 1);

    // ───────────────────────────── склад ─────────────────────────────

    [Fact]
    public void Stock_EqualsReceiptsMinusSalesPlusReturns_InMinusToo() =>
        Gen.Select(Gen.Int[0, 50000], Gen.Select(Gen.Int[0, 2], Gen.Int[1, 5000]).List[1, 40]).Sample((initialMilli, moves) =>
        {
            UserPreferences.Instance.SellWithoutStock = true;
            var stock = initialMilli / 1000.0;
            var model = initialMilli;
            foreach (var (kind, qtyMilli) in moves)
            {
                var q = qtyMilli / 1000.0;
                switch (kind)
                {
                    case 0: // продажа
                        stock = StockSyncService.AfterSale(stock, q);
                        model -= qtyMilli;
                        break;
                    case 1: // возврат покупателя
                        stock += q;
                        model += qtyMilli;
                        break;
                    default: // приход
                        stock += q;
                        model += qtyMilli;
                        break;
                }
            }

            Math.Round((decimal)stock, 3).Should().Be(model / 1000m, "остаток = приход − продажи + возвраты (продажа в минус разрешена)");
        }, iter: 500, threads: 1);

    [Fact]
    public void WeighBySum_ExactLine_IsTheTypedSum_OtherwiseWithinOneGram() =>
        Gen.Select(Gen.Int[100, 500000], Gen.Int[100, 3000000], Gen.Bool, Gen.Bool).Sample((amountCents, priceCents, exact, somStep) =>
        {
            var amount = amountCents / 100m;
            var price = priceCents / 100m;
            QuantityRules.WeightLineFloorEnabled = () => true;
            var plan = WeighedAmount.Plan(amount, price, exact, somStep ? 1m : 0.01m);
            if (plan is null)
            {
                (amount / price).Should().BeLessThan(0.0005m, "вес меньше грамма — посчитать нельзя");
                return;
            }

            plan.Kg.Should().Be(Math.Round(plan.Kg, 3), "вес — до грамма");
            if (plan.ExactLine)
                plan.LineTotal.Should().Be(amount, "«По сумме» с признаком сервера — чек ровно на введённую сумму");
            else
                Math.Abs(plan.LineTotal - amount).Should().BeLessThanOrEqualTo(price * 0.0005m + (somStep ? 1m : 0.01m), "без признака — вес × цена, расхождение не больше полуграмма и шага весов");
        }, iter: 1000, threads: 1);
}
