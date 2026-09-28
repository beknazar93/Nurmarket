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
        var rows = await LoadSaleRowsAsync(shiftId, openedAt, closedAt, ct).ConfigureAwait(false);
        return await AttachLinesAsync(rows, ct).ConfigureAwait(false);
    }

    /// <summary>Чеки смены без товаров — только список продаж. 2026-09-27 («медленно открывает
    /// отчёты»): раньше каждая плитка «Деталей смены» заново листала список продаж и дочитывала
    /// товары ВСЕХ чеков смены, даже если открыли «Долг» с двумя чеками. Теперь список один на
    /// окно «Детали смены» (см. ShiftDetailsDialog), а товары дочитываются только для чеков
    /// выбранной плитки (<see cref="AttachLinesAsync"/>). Сами цифры считаются как прежде.</summary>
    public static async Task<List<ShiftSale>> LoadSaleRowsAsync(
        string shiftId, DateTime? openedAt, DateTime? closedAt, CancellationToken ct = default)
    {
        var from = (openedAt ?? DateTime.Now.AddDays(-1)).Date;
        var to = (closedAt ?? DateTime.Now).Date.AddDays(1);

        var rows = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var items = await App.SalesApi
                .PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: to)
                .ConfigureAwait(false);

            // Страницу за последней сервер не отвергает, а отдаёт ещё раз последнюю (проверено
            // 2026-09-27: page=5 при трёх страницах вернул строки третьей). Если продаж за даты
            // смены ровно кратно 80, цикл шёл до MaxPages и задваивал чеки смены — поэтому
            // повторы отбрасываем и останавливаемся, когда страница не принесла ничего нового.
            var added = 0;
            foreach (var r in items)
            {
                var id = Str(r, "id");
                if (id is not null && !seen.Add(id))
                    continue;
                added++;
                if (string.Equals(Str(r, "shift"), shiftId, StringComparison.OrdinalIgnoreCase))
                    rows.Add(r);
            }

            if (items.Count < PageSize || added == 0)
                break;
        }

        return rows
            .Select(r =>
            {
                DateTime.TryParse(Str(r, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                return new ShiftSale(
                    Str(r, "id") ?? "",
                    at,
                    Str(r, "status") ?? "",
                    Str(r, "payment_method") ?? "",
                    Num(r, "total"),
                    Num(r, "discount_total"),
                    Num(r, "debt_amount"),
                    Str(r, "user_display"),
                    Array.Empty<SaleLine>());
            })
            .OrderBy(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>Товары чеков: из локальной истории продаж, а чего там нет (чек пробит на другой
    /// кассе) — из деталей продажи с сервера. Порядок чеков сохраняется.</summary>
    public static async Task<List<ShiftSale>> AttachLinesAsync(IReadOnlyList<ShiftSale> sales, CancellationToken ct = default)
    {
        var ids = sales.Select(s => s.Id).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Локальная база — не на потоке интерфейса: метод вызывают прямо из окна отчёта.
        var local = await Task.Run(() => SoldLineItemsStore.LinesBySale(ids), ct).ConfigureAwait(false);

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

        return sales
            .Select(s =>
            {
                IReadOnlyList<SaleLine> lines = local.TryGetValue(s.Id, out var own)
                    ? own.Select(l => new SaleLine(l.ProductName, l.Quantity, l.UnitPrice)).ToList()
                    : fetched.TryGetValue(s.Id, out var remote) ? remote : Array.Empty<SaleLine>();
                return s with { Lines = lines };
            })
            .ToList();
    }

    /// <summary>2026-09-28: «Товары за смену» с сервера — позиции продаж смены, входящих в выручку
    /// (paid, partially_returned), см. <see cref="ShiftProductsSummary"/>. Раньше список считался по
    /// локальной истории за время смены и не сходился с выручкой (чужие чеки того же времени,
    /// офлайн-чек дважды, скидка сервера). Детали чеков — из общего кэша отчётов, по шесть сразу.
    /// Бросает исключение, если хоть один чек не прочитан: неполный список хуже запасного.</summary>
    public static async Task<ShiftProductsSummary.Result> LoadProductsAsync(
        IReadOnlyList<ShiftSale> sales, CancellationToken ct = default)
    {
        var ids = sales
            .Where(s => ShiftProductsSummary.IsRevenueStatus(s.Status) && !string.IsNullOrEmpty(s.Id))
            .Select(s => s.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gate = new SemaphoreSlim(Parallelism);
        var details = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SaleDetailCache.GetWithRetryAsync(id, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return ShiftProductsSummary.Build(details);
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
