using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost;

namespace NurMarketKassa.Services;

/// <summary>Продажи одной смены с товарами — для подробного отчёта по плиткам «Деталей смены»
/// (2026-09-24, просьба владельца: «нажали на наличные — должна открываться модалка с суммой и
/// товарами, всё должно нажиматься»).
///
/// Фильтра по смене у списка продаж на сервере нет (параметр shift он молча игнорирует), поэтому
/// берём продажи за даты смены и отбираем по номеру смены сами. Товары чека — из локальной
/// истории продаж, а чего там нет (чек пробит на другой кассе) — из деталей продажи с сервера.</summary>
public static class ShiftReportData
{
    public sealed record SaleLine(string Name, double Quantity, double Price);

    public sealed record ShiftSale(
        string Id,
        DateTime CreatedAt,
        string Status,
        string PaymentMethod,
        double Total,
        double Discount,
        double Debt,
        string? Cashier,
        IReadOnlyList<SaleLine> Lines);

    private const int PageSize = 80;
    private const int MaxPages = 30;
    private const int Parallelism = 6;

    public static async Task<List<ShiftSale>> LoadSalesAsync(
        string shiftId, DateTime? openedAt, DateTime? closedAt, CancellationToken ct = default)
    {
        var from = (openedAt ?? DateTime.Now.AddDays(-1)).Date;
        var to = (closedAt ?? DateTime.Now).Date.AddDays(1);

        var rows = new List<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var items = await App.SalesApi
                .PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: to)
                .ConfigureAwait(false);
            rows.AddRange(items.Where(r => string.Equals(Str(r, "shift"), shiftId, StringComparison.OrdinalIgnoreCase)));
            if (items.Count < PageSize)
                break;
        }

        var ids = rows.Select(r => Str(r, "id")).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
        var local = SoldLineItemsStore.LinesBySale(ids);

        // Чеки, которых нет в локальной истории, дочитываем с сервера — параллельно, порциями.
        var missing = ids.Where(id => !local.ContainsKey(id)).ToList();
        var fetched = new Dictionary<string, List<SaleLine>>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(missing.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var detail = await SaleDetailCache.GetAsync(id, ct).ConfigureAwait(false);
                if (detail.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var lines = items.EnumerateArray()
                        .Select(it => new SaleLine(
                            Str(it, "product_name") ?? Str(it, "name") ?? "?",
                            Num(it, "quantity"),
                            Num(it, "unit_price")))
                        .ToList();
                    lock (fetched)
                        fetched[id] = lines;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Отчёт смены: чек {id} не прочитан: {ex.Message}", "DEBUG");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return rows
            .Select(r =>
            {
                var id = Str(r, "id") ?? "";
                IReadOnlyList<SaleLine> lines = local.TryGetValue(id, out var own)
                    ? own.Select(l => new SaleLine(l.ProductName, l.Quantity, l.UnitPrice)).ToList()
                    : fetched.TryGetValue(id, out var remote) ? remote : Array.Empty<SaleLine>();
                DateTime.TryParse(Str(r, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                return new ShiftSale(
                    id,
                    at,
                    Str(r, "status") ?? "",
                    Str(r, "payment_method") ?? "",
                    Num(r, "total"),
                    Num(r, "discount_total"),
                    Num(r, "debt_amount"),
                    Str(r, "user_display"),
                    lines);
            })
            .OrderBy(s => s.CreatedAt)
            .ToList();
    }

    private static string? Str(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Num(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d)
            ? d
            : 0;
}
