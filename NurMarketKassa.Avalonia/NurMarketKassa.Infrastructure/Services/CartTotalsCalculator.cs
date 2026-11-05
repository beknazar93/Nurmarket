using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Локальный расчёт сумм чека: промежуточные итоги, скидки, итого, сдача.</summary>
public static class CartTotalsCalculator
{
    public sealed class CartTotals
    {
        public double Subtotal { get; init; }
        public double LineDiscounts { get; init; }
        public double OrderDiscount { get; init; }
        public double TotalDue { get; init; }
        public int LineCount { get; init; }

        /// <summary>Subtotal - LineDiscounts - OrderDiscount ДО обнуления отрицательных значений
        /// (см. TotalDue) — 2026-09-21, живой баг владельца: чек из одной «Доп. услуги» типа
        /// «Расход» (без единого товара) реально уходит в минус, но TotalDue показывал «0.00»,
        /// как будто это бесплатная продажа — кассир жал «Оплатить 0.00», а сервер честно падал
        /// с «Внутренняя ошибка сервера (500)» на нонсенсной для него отрицательной продаже.
        /// Используется только для блокировки оплаты ДО отправки на сервер (см. PayAsync) —
        /// сами суммы на экране (сдача и т.п.) по-прежнему считаются от неотрицательного TotalDue.</summary>
        public double RawTotal { get; init; }

        /// <summary>2026-11-01 (ответ Умара): суммы БЕЗ округления — сумма строк (цена × количество) и скидки строк.
        /// Сервер складывает строки не округляя и округляет только итог чека; окно оплаты считает скидку чека от этих значений.
        /// Subtotal / LineDiscounts выше — те же суммы, округлённые до копейки для показа.</summary>
        public decimal SubtotalExact { get; init; }
        public decimal LineDiscountsExact { get; init; }

        /// <summary>2026-11-02 (К3-07 аудита №2): разность «подытог по правилу сервера − сумма показанных строк» (каждая строка на экране и на
        /// чеке округлена половиной вверх, а подытог — по правилу сервера: сумма весовых вниз). Нужна только для ПОКАЗА строки
        /// «Округление весовых» (сумма строк + округление = подытог), итог она не меняет. 0 — строки не нужно.</summary>
        public decimal RoundingAdjustment { get; init; }

        /// <summary>В чеке есть весовая строка (товар с is_weight) — для подписи «Округление весовых», иначе «Округление».</summary>
        public bool HasWeighedLines { get; init; }

        public string RoundingLabel => CartTotalsCalculator.RoundingLabel(HasWeighedLines);
        public bool HasRoundingAdjustment => RoundingAdjustment != 0m;
        public string RoundingFormatted => CartTotalsCalculator.FormatSignedAmount(RoundingAdjustment);

        public string SubtotalFormatted => FormatMoney(Subtotal);
        public string LineDiscountsFormatted => FormatMoney(LineDiscounts);
        public string OrderDiscountFormatted => FormatMoney(OrderDiscount);
        public string TotalDueFormatted => FormatMoney(TotalDue);
    }

    public static CartTotals Calculate(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return Empty();

        // 2026-11-01 (ответ Умара, округление): как у сервера — строки складываются БЕЗ округления (decimal, полная точность),
        // скидка чека процентом считается от неокруглённой суммы, и округляется ОДИН раз — итог чека (до 0,01, половина вверх).
        // Раньше каждая строка и каждая промежуточная сумма округлялись отдельно — у весовых чеков копейки расходились с сервером.
        // Скидки строк — уже суммы (до копейки, их кассир ввёл или они ушли на сервер так же), в decimal не округляются повторно.
        var weighedLines = new List<decimal>(); // 2026-11-05: точные суммы весовых строк (при weight_line_total_floor каждая округляется вниз отдельно)
        decimal otherSum = 0;
        decimal lineDiscountsExact = 0;
        decimal shownLinesSum = 0; // 2026-11-02 (К3-07): сумма строк, как они показаны (каждая — половиной вверх, за вычетом скидки строки)
        var hasWeighed = false;
        var lineCount = 0;

        foreach (var it in CartDisplayHelper.EnumerateItems(cart))
        {
            lineCount++;
            var qty = CartDisplayHelper.LineQuantity(it);
            var unitPrice = CartDisplayHelper.UnitPrice(it);
            // 2026-11-01 (опыты на сервере NBS): весовые строки (is_weight товара) и остальные суммируются раздельно, без округления строк.
            var exact = QuantityRules.GrossExact(qty, unitPrice);
            if (CartDisplayHelper.LineIsWeightProduct(it))
            {
                weighedLines.Add(exact);
                hasWeighed = true;
            }
            else
                otherSum += exact;

            var disc = ReadDiscount(it);
            if (disc > 0)
                lineDiscountsExact += QuantityRules.ToMoneyDecimal(disc);

            // Как строка показана (CartService.ProjectItem / LocalCartService.RecalcLine): половина вверх, скидка строки вычтена.
            shownLinesSum += exact < 0
                ? QuantityRules.RoundMoney(exact)
                : Math.Max(0m, QuantityRules.RoundMoney(exact - QuantityRules.ToMoneyDecimal(disc)));
        }

        // 2026-11-01 (опыты на сервере NBS, чеки 1971–1988): подытог = ВНИЗ до 0,01 (сумма весовых) + ВВЕРХ до 0,01 (сумма остальных);
        // процент скидки чека — от (подытог − скидки строк), сумма скидки округляется отдельно (половина вверх); итог = подытог − скидки.
        // 4,875 + 22,638 → 27,51; 22,638 → 22,63; чек 1977: 1,425 → 1,43, 50 % = 0,715 → 0,72, итог 0,71; 17,00 × 2,5 % → 0,43 → 16,57.
        var subtotalExact = QuantityRules.Subtotal(weighedLines, otherSum);
        var orderDiscountBase = Math.Max(0m, QuantityRules.RoundMoney(subtotalExact - lineDiscountsExact));
        var orderDiscountExact = QuantityRules.RoundMoney(ReadOrderDiscount(cart, orderDiscountBase, lineDiscountsExact));
        var rawTotal = QuantityRules.RoundMoney(subtotalExact - lineDiscountsExact) - orderDiscountExact;
        var computed = Math.Max(0m, rawTotal);
        var totalDue = lineCount == 0 ? 0m : computed;

        return new CartTotals
        {
            Subtotal = (double)QuantityRules.RoundMoney(subtotalExact),
            LineDiscounts = (double)QuantityRules.RoundMoney(lineDiscountsExact),
            OrderDiscount = (double)QuantityRules.RoundMoney(orderDiscountExact),
            TotalDue = (double)totalDue,
            RawTotal = lineCount == 0 ? 0 : (double)rawTotal,
            LineCount = lineCount,
            SubtotalExact = subtotalExact,
            LineDiscountsExact = lineDiscountsExact,
            RoundingAdjustment = lineCount == 0 ? 0m : ShownRounding(subtotalExact - lineDiscountsExact - shownLinesSum, lineCount,
                // 2026-11-06 (Л5-02): при шаге весов 1 сом строка «Округление весовых» может быть до 1 сома на строку — не прятать её (раньше предел был 0,01 на строку)
                hasWeighed && QuantityRules.WeightLineFloorEnabled() ? Math.Max(0.01m, QuantityRules.WeightStep()) : 0.01m),
            HasWeighedLines = hasWeighed,
        };
    }

    /// <summary>2026-11-02 (К3-07): разность округляется до копейки; больше «по копейке на строку» быть не может (это уже не округление, а скидка
    /// больше суммы строки и т.п.) — тогда строку не показываем. Меньше копейки — тоже 0.</summary>
    private static decimal ShownRounding(decimal difference, int lineCount, decimal perLine = 0.01m)
    {
        var adj = QuantityRules.RoundMoney(difference);
        return Math.Abs(adj) >= 0.005m && Math.Abs(adj) <= perLine * lineCount + 0.001m ? adj : 0m;
    }

    /// <summary>2026-11-02 (К3-07): то же для чека с сервера (история, повторная печать): строки — как показал сервер (line_total, половина вверх),
    /// скидка и итог — тоже его. Сумма строк − скидка + округление = итог. 0 — разности нет или она не похожа на округление.</summary>
    public static decimal SaleRoundingAdjustment(decimal linesTotal, decimal discount, decimal due, int lineCount)
    {
        if (lineCount <= 0)
            return 0m;
        // Скидка может быть уже внутри цен строк (тогда сумма строк = итог) — тогда вычитать её не нужно.
        var viaDiscount = discount > 0.005m && Math.Abs(linesTotal - discount - due) <= Math.Abs(linesTotal - due);
        return ShownRounding(due - (viaDiscount ? linesTotal - discount : linesTotal), lineCount);
    }

    /// <summary>2026-11-02 (К3-07): подпись строки-выравнивания, 5 языков.</summary>
    public static string RoundingLabel(bool weighed) => weighed
        ? Tr.T("Округление весовых", "Салмактуу товарлардын тегизделиши", "Weighed items rounding", "Tartılı ürün yuvarlaması", "Tortiladigan tovarlar yaxlitlanishi")
        : Tr.T("Округление", "Тегиздөө", "Rounding", "Yuvarlama", "Yaxlitlash");

    /// <summary>«−0,02» / «+0,01» — со знаком, как число на экране (1 234,50).</summary>
    public static string FormatSignedAmount(decimal value) =>
        (value < 0 ? "−" : "+") + MoneyText.Amount(Math.Abs(value));

    public static double CalculateChange(double cashReceived, double totalDue) =>
        cashReceived > totalDue + 1e-9 ? RoundMoney(cashReceived - totalDue) : 0;

    public static string FormatMoney(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Копейки: промежуточные суммы округляются сразу, чтобы двоичная погрешность
    /// double не накапливалась между строками чека и не расходилась с сервером.</summary>
    private static double RoundMoney(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    private static CartTotals Empty() => new();

    private static double ReadApiTotal(JsonElement cart)
    {
        foreach (var src in new[] { cart, TryTotals(cart) })
        {
            if (src.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var key in new[]
                     {
                         "total", "grand_total", "total_amount", "amount_due", "payable_total",
                         "order_total", "total_to_pay", "amount_total",
                     })
            {
                if (TryDouble(src, key) is { } v && v > 0)
                    return v;
            }
        }

        return 0;
    }

    /// <summary>2026-11-01: скидка чека в decimal БЕЗ округления: сумма — как задана, процент — от неокруглённой базы.</summary>
    private static decimal ReadOrderDiscount(JsonElement cart, decimal discountBase, decimal lineDiscounts)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return 0;

        if (TryDouble(cart, "order_discount_total") is { } explicitTotal && explicitTotal > 0)
            return Math.Min(discountBase, QuantityRules.ToMoneyDecimal(explicitTotal));
        if (TryDouble(cart, "cart_discount") is { } cartDiscount && cartDiscount > 0)
            return Math.Min(discountBase, QuantityRules.ToMoneyDecimal(cartDiscount));
        foreach (var key in new[] { "discount_total", "total_discount" })
            if (TryDouble(cart, key) is { } aggregate && aggregate > 0)
                return Math.Min(discountBase, Math.Max(0m, QuantityRules.ToMoneyDecimal(aggregate) - lineDiscounts));

        var totals = TryTotals(cart);
        if (totals.ValueKind == JsonValueKind.Object)
        {
            if (TryDouble(totals, "order_discount_total") is { } nestedOrder && nestedOrder > 0)
                return Math.Min(discountBase, QuantityRules.ToMoneyDecimal(nestedOrder));
            foreach (var key in new[] { "discount_total", "total_discount" })
                if (TryDouble(totals, key) is { } aggregate && aggregate > 0)
                    return Math.Min(discountBase, Math.Max(0m, QuantityRules.ToMoneyDecimal(aggregate) - lineDiscounts));
        }

        if (TryDouble(cart, "order_discount_percent") is { } pct && pct > 0)
            return discountBase * QuantityRules.ToMoneyDecimal(Math.Min(pct, 100)) / 100m;

        return 0;
    }

    /// <summary>2026-09-28, продажа №1136: скидка строки — из одной функции с телом запроса на
    /// сервер (CartDisplayHelper.EffectiveLineDiscount): скидка кассира или акция товара NurCRM.
    /// Прежний разбор (discount_total / line_discount / discount / discount_percent) перенесён
    /// туда без изменений — CartDisplayHelper.ManualLineDiscount.</summary>
    private static double ReadDiscount(JsonElement line) =>
        RoundMoney(CartDisplayHelper.EffectiveLineDiscount(line));

    private static JsonElement TryTotals(JsonElement cart) =>
        cart.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return null;

        return JsonNumericReader.TryToDouble(v, out var d) ? d : null;
    }
}
