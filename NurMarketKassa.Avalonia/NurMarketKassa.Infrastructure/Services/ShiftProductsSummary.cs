using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>«Товары за смену» по продажам смены с сервера (2026-09-28).
///
/// Раньше список в «Деталях смены» считался по локальной истории кассы (SoldLineItems) за
/// время смены и не сходился с выручкой: на смене d723b36b товары — 2 930, выручка — 2 534,50.
/// Разница 395,50 сложилась из трёх причин:
/// <list type="bullet">
/// <item>338,00 — чужие продажи того же времени: история добирает с сервера чеки ВСЕХ касс
/// компании (SalesHistoryBackfill), а список брал всё по времени, без смены (шесть чеков
/// «Nur Test» со смены bc90098b, в т.ч. продажа в долг на 45);</item>
/// <item>50,00 — офлайн-чек дважды: касса записала его без номера продажи, а после досылки
/// бэкфилл подтянул тот же чек с сервера уже с номером (e803dbed, №1088);</item>
/// <item>7,50 — скидка на строку, которую провёл сервер (№1136: 50 − 7,50), а касса записала
/// цену до скидки.</item>
/// </list>
/// Теперь список — позиции продаж смены с сервера (поле shift продажи), по правилу выручки
/// сайта: статусы paid и partially_returned (частичный возврат — по полной сумме, как в
/// выручке), долг и отменённые не входят. Сумма строки — line_total, то есть уже со скидкой
/// на строку. Скидку на весь чек сервер в строки не раскладывает — она возвращается отдельно
/// (<see cref="Result.OrderDiscount"/>), чтобы подпись объясняла разницу с выручкой.
/// Возвраты считаются отдельно (плитка «Возвраты»).</summary>
public static class ShiftProductsSummary
{
    public sealed record Row(string Name, decimal Quantity, decimal Revenue);

    public sealed record Result(
        IReadOnlyList<Row> Rows,
        int SalesCount,
        decimal LinesTotal,
        decimal OrderDiscount,
        decimal SalesTotal);

    /// <summary>Продажа входит в выручку смены — те же статусы, что считает сайт.</summary>
    public static bool IsRevenueStatus(string? status) =>
        string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "partially_returned", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сводка по деталям продаж (GET api/main/pos/sales/{id}/). Продажи не из выручки
    /// (долг, отмена, открытая корзина) пропускаются здесь же — по их полю status.</summary>
    public static Result Build(IEnumerable<JsonElement> saleDetails)
    {
        var rows = new Dictionary<string, (string Name, decimal Qty, decimal Revenue)>(StringComparer.OrdinalIgnoreCase);
        var salesCount = 0;
        var linesTotal = 0m;
        var salesTotal = 0m;

        foreach (var sale in saleDetails)
        {
            if (sale.ValueKind != JsonValueKind.Object || !IsRevenueStatus(Str(sale, "status")))
                continue;

            salesCount++;
            var saleLines = 0m;
            if (sale.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = CartDisplayHelper.ItemName(it);
                    var productId = CartDisplayHelper.TryProductId(it);
                    var key = string.IsNullOrWhiteSpace(productId) ? "name:" + name : productId.Trim();
                    var qty = Dec(it, "quantity") ?? 0m;
                    // line_total — уже за вычетом скидки на строку (у №1136: 50 − 7,50 = 42,50).
                    var lineTotal = Dec(it, "line_total")
                                    ?? (Dec(it, "unit_price") ?? 0m) * qty - (Dec(it, "line_discount") ?? 0m);
                    saleLines += lineTotal;
                    rows[key] = rows.TryGetValue(key, out var acc)
                        ? (acc.Name, acc.Qty + qty, acc.Revenue + lineTotal)
                        : (name, qty, lineTotal);
                }
            }

            linesTotal += saleLines;
            salesTotal += Dec(sale, "total") ?? saleLines;
        }

        return new Result(
            rows.Values
                .Select(r => new Row(r.Name, r.Qty, Math.Round(r.Revenue, 2)))
                .OrderByDescending(r => r.Revenue)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            salesCount,
            Math.Round(linesTotal, 2),
            // Скидка на чек = сумма строк − итог продаж. Отрицательной не бывает — округления в копейку не в счёт.
            Math.Max(0m, Math.Round(linesTotal - salesTotal, 2)),
            Math.Round(salesTotal, 2));
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d)
            ? Math.Round((decimal)d, 3)
            : null;
}
