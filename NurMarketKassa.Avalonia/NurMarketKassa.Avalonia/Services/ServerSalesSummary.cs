using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost;

namespace NurMarketKassa.Services;

/// <summary>Сводка продаж сервера за период — те же цифры, что у сайта (2026-09-25, «сверяй с
/// вебом»): GET api/main/analytics/market/?tab=sales.
///
/// Возвраты: сайт берёт их из «Документы» → «Возврат продажи» и учитывает возвраты, сделанные
/// где угодно — на сайте, на другой кассе. Локальный журнал кассы знает только свои, поэтому
/// «Продажи» и «Финансы» показывали 0, когда возврат делали на сайте. Списка возвратов у сервера нет.
/// 2026-09-28: теперь есть (GET api/main/pos/returns/, BE-09) — сумма берётся из него.
///
/// Прибыль: сайт считает себестоимость по своим данным о закупках. Касса умножала количество на
/// ТЕКУЩУЮ закупочную цену из каталога (а удалённые товары шли по нулю) — за сентябрь это дало
/// 25,4 % против 30,5 % у сайта даже при полностью загруженных чеках. Поэтому прибыль и маржа
/// берутся отсюда; локальный расчёт остаётся только на случай, когда сервер недоступен.</summary>
public sealed class ServerSalesSummary
{
    private const string SaleReturnDocument = "Возврат продажи";

    public decimal? Returns { get; private init; }
    public decimal? GrossProfit { get; private init; }
    public decimal? MarginPercent { get; private init; }

    /// <summary>2026-09-28, жалоба клиентов «аналитика не похожа на веб, грузится очень долго»:
    /// выручка и число чеков — ровно cards.revenue / cards.transactions сайта. Раньше окна складывали
    /// их сами из списка продаж, который листали по 80 строк и не дальше 60 страниц (4 800 чеков) —
    /// у магазина с сотнями чеков в день «Месяц» обрезался, а загрузка шла десятки секунд.</summary>
    public decimal? Revenue { get; private init; }
    public int? Transactions { get; private init; }

    /// <summary>Сумма оплаченных чеков по способу оплаты (charts.payment_methods сайта: cash,
    /// transfer, mixed, mbank…); ключ — в нижнем регистре.</summary>
    public IReadOnlyDictionary<string, decimal> PaymentTotals { get; private init; } = new Dictionary<string, decimal>();

    /// <summary>Выручка по дням (charts.sales_dynamics сайта).</summary>
    public IReadOnlyList<(DateTime Day, decimal Revenue)> ByDay { get; private init; } = Array.Empty<(DateTime, decimal)>();

    /// <summary>Сводка за дни [from; to] включительно; null — сервер недоступен (окно считает само, как раньше).</summary>
    public static async Task<ServerSalesSummary?> FetchAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            // 2026-09-28: список возвратов запрашивается одновременно со сводкой, а не после неё.
            var returnsTask = NurMarketKassa.Services.Api.NurCrmReportsApi.ReturnsTotalsAsync(from.Date, to.Date, ct);
            var data = await App.SalesApi.MarketSalesReportAsync(from.Date, to.Date, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object)
                return null;

            decimal? returns = null;
            if (data.TryGetProperty("tables", out var tables)
                && tables.TryGetProperty("documents", out var docs)
                && docs.ValueKind == JsonValueKind.Array)
            {
                foreach (var doc in docs.EnumerateArray())
                {
                    if (doc.TryGetProperty("name", out var name) && name.GetString() == SaleReturnDocument
                        && doc.TryGetProperty("sum", out var sum))
                        returns = Parse(sum);
                }
            }

            decimal? profit = null, margin = null, revenue = null;
            int? transactions = null;
            if (data.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Object)
            {
                if (cards.TryGetProperty("gross_profit", out var gp))
                    profit = Parse(gp);
                if (cards.TryGetProperty("margin_percent", out var mp))
                    margin = Parse(mp);
                if (cards.TryGetProperty("revenue", out var rv))
                    revenue = Parse(rv);
                if (cards.TryGetProperty("transactions", out var tx) && Parse(tx) is { } txCount)
                    transactions = (int)Math.Round(txCount);
            }

            var payments = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var byDay = new List<(DateTime Day, decimal Revenue)>();
            if (data.TryGetProperty("charts", out var charts) && charts.ValueKind == JsonValueKind.Object)
            {
                if (charts.TryGetProperty("payment_methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in methods.EnumerateArray())
                    {
                        var key = m.TryGetProperty("method", out var mk) && mk.ValueKind == JsonValueKind.String
                            ? (mk.GetString() ?? "").Trim().ToLowerInvariant()
                            : "";
                        if (m.TryGetProperty("total", out var mt) && Parse(mt) is { } total)
                            payments[key] = payments.TryGetValue(key, out var was) ? was + total : total;
                    }
                }

                if (charts.TryGetProperty("sales_dynamics", out var days) && days.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in days.EnumerateArray())
                    {
                        if (d.TryGetProperty("date", out var dd) && dd.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(dd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                            && d.TryGetProperty("value", out var dv) && Parse(dv) is { } value)
                            byDay.Add((day.Date, value));
                    }
                }
            }

            // 2026-09-28 (BE-09): у сервера появился список возвратов — сумма за период из него
            // (сверено: те же число и сумма, что «Документы → Возврат продажи»). Не ответил —
            // остаётся цифра сводной аналитики выше.
            if (await returnsTask.ConfigureAwait(false) is { } listed)
                returns = listed.Sum;

            return new ServerSalesSummary
            {
                Returns = returns,
                GrossProfit = profit,
                MarginPercent = margin,
                Revenue = revenue,
                Transactions = transactions,
                PaymentTotals = payments,
                ByDay = byDay,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка продаж с сервера не получена: {ex.Message}", "WARNING");
            return null;
        }
    }

    /// <summary>Раскладка выручки сайта по плиткам окна (2026-09-28): наличные, смешанная, в долг и
    /// безнал = остальное — то же правило, что окна применяли к списку продаж.</summary>
    public (decimal Cash, decimal Mixed, decimal Debt, decimal NonCash) Split()
    {
        decimal cash = 0m, mixed = 0m, debt = 0m;
        foreach (var (method, total) in PaymentTotals)
        {
            if (method == "cash" || method.Contains("нал", StringComparison.Ordinal))
                cash += total;
            else if (method is "mixed" or "split")
                mixed += total;
            else if (method == "debt")
                debt += total;
        }

        return (cash, mixed, debt, (Revenue ?? 0m) - cash - mixed - debt);
    }

    private static decimal? Parse(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
