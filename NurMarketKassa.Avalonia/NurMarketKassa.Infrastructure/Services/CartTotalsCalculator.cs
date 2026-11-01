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
        decimal subtotalExact = 0;
        decimal lineDiscountsExact = 0;
        var lineCount = 0;

        foreach (var it in CartDisplayHelper.EnumerateItems(cart))
        {
            lineCount++;
            var qty = CartDisplayHelper.LineQuantity(it);
            var unitPrice = CartDisplayHelper.UnitPrice(it);
            subtotalExact += QuantityRules.LineAmount(it, qty, unitPrice); // 2026-11-01: весовая — вниз до 0,01, обычная — без округления

            var disc = ReadDiscount(it);
            if (disc > 0)
                lineDiscountsExact += QuantityRules.ToMoneyDecimal(disc);
        }

        // 2026-11-01 (живая сверка с сервером NBS, чеки 1974–1980): подытог (сумма строк минус скидки строк) округляется ОДИН раз;
        // процент скидки чека берётся от ОКРУГЛЁННОГО подытога, сумма скидки округляется отдельно (половина вверх); итог = подытог − скидка.
        // Чек 1977: 1,425 → подытог 1,43, 50 % = 0,715 → 0,72, итог 0,71; 17,00 × 2,5 % → 0,43 → 16,57; 5,00 × 2,5 % → 0,13 → 4,87.
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
        };
    }

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
