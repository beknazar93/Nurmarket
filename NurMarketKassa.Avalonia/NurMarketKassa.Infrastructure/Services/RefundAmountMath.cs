using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-30, «Некорректные чеки» («Возврат больше положенного: сервер записал 2,00, положено 1,80»): арифметика суммы возврата
/// строки чека со скидкой на чек — так же, как на сервере (ответ Умара по Д-4): сумма = цена строки с долей скидки на чек, копейки по
/// правилу «от нуля», последний возврат по чеку = остаток, не больше <c>refundable_total</c>. Плюс разбор ответа 409/400
/// <c>refund_exceeds_paid</c> (<c>data: {paid, refunded, requested, refundable}</c>) — только чтобы показать понятный текст: касса сумму по ответу сервера НЕ подгоняет и не переотправляет
/// (решение приёмки 30.10: расчёт кассы верен, ошибка на стороне сервера; такая операция — «Требует проверки»). Чистая арифметика без сети и файлов — проверяется в консоли.</summary>
public static class RefundAmountMath
{
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сколько вернуть за строку: <paramref name="lineTotal"/> × (оплачено / сумма строк), не больше 1 доли.
    /// Пример: строки 15 + 15 = 30, оплачено 28 (скидка на чек 2) → строка 15 = 14,00.</summary>
    public static decimal LineShare(decimal lineTotal, decimal linesSum, decimal paidTotal)
    {
        if (lineTotal <= 0m)
            return 0m;
        if (linesSum <= 0.005m || paidTotal <= 0m)
            return Round2(lineTotal);
        return Round2(lineTotal * Math.Min(1m, paidTotal / linesSum));
    }

    /// <summary>Возврат не больше положенного: <paramref name="refundable"/> — сколько сервер ещё разрешает (null или ≤ 0 — не знаем, сумма как есть).</summary>
    public static decimal ClampToDue(decimal requested, decimal? refundable) =>
        refundable is { } left && left > 0m ? Math.Min(Round2(requested), Round2(left)) : Round2(requested);

    /// <summary>Последний возврат по чеку (все оставшиеся строки): ровно остаток, без копеек округления; иначе — доля строк, но не больше остатка.</summary>
    public static decimal AmountForReturn(decimal sharesSum, decimal remaining, bool returnsAllLeft)
    {
        var rem = Math.Max(0m, Round2(remaining));
        return returnsAllLeft ? rem : Math.Min(Round2(sharesSum), rem);
    }

    /// <summary>Что сервер сказал в ответе <c>refund_exceeds_paid</c>.</summary>
    public sealed record ExceedsInfo(decimal? Paid, decimal? Refunded, decimal? Requested, decimal? Refundable);

    /// <summary>true — ответ сервера «возврат больше положенного» (по коду или по тексту); <paramref name="info"/> — цифры из <c>data</c>, если есть.</summary>
    public static bool TryParseExceeds(ApiException ex, out ExceedsInfo info)
    {
        info = new ExceedsInfo(null, null, null, null);
        var raw = (ex.Payload?.ToString() ?? "") + " " + ex.Message;
        if (!raw.Contains("refund_exceeds_paid", StringComparison.OrdinalIgnoreCase)
            && !raw.Contains("больше положенного", StringComparison.OrdinalIgnoreCase)
            && !raw.Contains("больше, чем оплачено", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (ex.Payload is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                info = new ExceedsInfo(Num(data, "paid"), Num(data, "refunded"), Num(data, "requested"), Num(data, "refundable"));
        }
        catch
        {
            // цифры — подсказка; без них достаточно текста
        }
        return true;
    }

    private static decimal? Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;
        var text = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind == JsonValueKind.Number ? el.GetRawText() : null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
