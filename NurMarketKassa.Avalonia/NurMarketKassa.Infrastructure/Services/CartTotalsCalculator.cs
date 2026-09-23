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

        public string SubtotalFormatted => FormatMoney(Subtotal);
        public string LineDiscountsFormatted => FormatMoney(LineDiscounts);
        public string OrderDiscountFormatted => FormatMoney(OrderDiscount);
        public string TotalDueFormatted => FormatMoney(TotalDue);
    }

    public static CartTotals Calculate(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return Empty();

        double subtotal = 0;
        double lineDiscounts = 0;
        var lineCount = 0;

        foreach (var it in CartDisplayHelper.EnumerateItems(cart))
        {
            lineCount++;
            var qty = CartDisplayHelper.LineQuantity(it);
            var unitPrice = CartDisplayHelper.UnitPrice(it);
            var gross = RoundMoney(qty * unitPrice);
            subtotal = RoundMoney(subtotal + gross);

            var disc = ReadDiscount(it);
            if (disc > 0)
                lineDiscounts = RoundMoney(lineDiscounts + disc);
        }

        var orderDiscountBase = Math.Max(0, RoundMoney(subtotal - lineDiscounts));
        var orderDiscount = RoundMoney(ReadOrderDiscount(cart, orderDiscountBase, lineDiscounts));
        var rawTotal = RoundMoney(subtotal - lineDiscounts - orderDiscount);
        var computed = Math.Max(0, rawTotal);
        var totalDue = lineCount == 0 ? 0 : computed;

        return new CartTotals
        {
            Subtotal = subtotal,
            LineDiscounts = lineDiscounts,
            OrderDiscount = orderDiscount,
            TotalDue = totalDue,
            RawTotal = lineCount == 0 ? 0 : rawTotal,
            LineCount = lineCount,
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

    private static double ReadOrderDiscount(JsonElement cart, double discountBase, double lineDiscounts)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return 0;

        if (TryDouble(cart, "order_discount_total") is { } explicitTotal && explicitTotal > 0)
            return Math.Min(discountBase, explicitTotal);
        if (TryDouble(cart, "cart_discount") is { } cartDiscount && cartDiscount > 0)
            return Math.Min(discountBase, cartDiscount);
        foreach (var key in new[] { "discount_total", "total_discount" })
            if (TryDouble(cart, key) is { } aggregate && aggregate > 0)
                return Math.Min(discountBase, Math.Max(0, aggregate - lineDiscounts));

        var totals = TryTotals(cart);
        if (totals.ValueKind == JsonValueKind.Object)
        {
            if (TryDouble(totals, "order_discount_total") is { } nestedOrder && nestedOrder > 0)
                return Math.Min(discountBase, nestedOrder);
            foreach (var key in new[] { "discount_total", "total_discount" })
                if (TryDouble(totals, key) is { } aggregate && aggregate > 0)
                    return Math.Min(discountBase, Math.Max(0, aggregate - lineDiscounts));
        }

        if (TryDouble(cart, "order_discount_percent") is { } pct && pct > 0)
            return RoundMoney(discountBase * Math.Min(pct, 100) / 100.0);

        return 0;
    }

    private static double ReadDiscount(JsonElement line)
    {
        foreach (var key in new[] { "discount_total", "line_discount", "discount" })
        {
            if (TryDouble(line, key) is { } v && v > 0)
                return v;
        }

        if (TryDouble(line, "discount_percent") is { } pct && pct > 0)
        {
            var gross = RoundMoney(CartDisplayHelper.LineQuantity(line) * CartDisplayHelper.UnitPrice(line));
            return RoundMoney(gross * Math.Min(pct, 100) / 100.0);
        }

        return 0;
    }

    private static JsonElement TryTotals(JsonElement cart) =>
        cart.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return null;

        return JsonNumericReader.TryToDouble(v, out var d) ? d : null;
    }
}
