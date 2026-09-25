using System;
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

    /// <summary>Сводка за дни [from; to] включительно; null — сервер недоступен (окно считает само, как раньше).</summary>
    public static async Task<ServerSalesSummary?> FetchAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
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

            decimal? profit = null, margin = null;
            if (data.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Object)
            {
                if (cards.TryGetProperty("gross_profit", out var gp))
                    profit = Parse(gp);
                if (cards.TryGetProperty("margin_percent", out var mp))
                    margin = Parse(mp);
            }

            return new ServerSalesSummary { Returns = returns, GrossProfit = profit, MarginPercent = margin };
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
