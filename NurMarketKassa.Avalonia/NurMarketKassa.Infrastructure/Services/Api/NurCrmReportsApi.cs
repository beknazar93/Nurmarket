using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>Новые адреса NurCRM, сделанные по ТЗ кассы (2026-09-28): отчёт смены (BE-10),
/// список возвратов (BE-09), поиск продажи по постоянному номеру (BE-08) и движения денег по
/// source_id (BE-06).
///
/// Отдельный файл, а не новые методы в ShiftApiService/SalesApiService: те правят и другие
/// доработки, а здесь только чтение. Транспорт тот же — NurMarketApiClient из DI (с защитой от
/// 429 и обновлением токена); его отдаёт <see cref="ClientResolver"/>, который приложение
/// задаёт при запуске (NurCrmReportsApiSetup в проекте кассы).
///
/// Все методы при любой ошибке сервера возвращают null (или пустой список) и пишут в журнал —
/// вызывающий код тогда считает по-старому (локальные счётчики, сводная аналитика).</summary>
public static class NurCrmReportsApi
{
    /// <summary>Откуда взять клиента API. null или исключение — «сервера нет», методы вернут null.</summary>
    public static Func<NurMarketApiClient?>? ClientResolver { get; set; }

    private static NurMarketApiClient? Client
    {
        get
        {
            try
            {
                var client = ClientResolver?.Invoke();
                return client is { AccessToken.Length: > 0 } ? client : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ── BE-10: отчёт смены ─────────────────────────────────────────────────────────────

    /// <summary>GET api/construction/shifts/{id}/report/ (~0,3 с). null — смена открыта без
    /// связи («offline-…»), сервер недоступен или ответил не отчётом.</summary>
    public static async Task<ServerShiftReport?> GetShiftReportAsync(string? shiftId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || !Guid.TryParse(shiftId.Trim(), out _) || Client is not { } client)
            return null;

        try
        {
            var data = await client.RequestAsync(
                HttpMethod.Get,
                $"api/construction/shifts/{Uri.EscapeDataString(shiftId.Trim())}/report/",
                null,
                null,
                ct).ConfigureAwait(false);
            return ServerShiftReport.TryParse(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёт смены {shiftId} с сервера не получен: {ex.Message}", "SHIFTS");
            return null;
        }
    }

    // ── BE-09: возвраты ────────────────────────────────────────────────────────────────

    /// <summary>Строка списка возвратов сервера.</summary>
    public sealed record PosReturn(
        string Id,
        string? SaleId,
        long? SaleNumber,
        decimal Amount,
        bool IsFull,
        string? CashierId,
        string? ShiftId,
        DateTimeOffset? CreatedAt);

    /// <summary>GET api/main/pos/returns/?date_from&amp;date_to&amp;shift&amp;sale.
    ///
    /// Проверено 2026-09-28: date_to ВКЛЮЧАЕТ свой день (в отличие от списка продаж), сумма и
    /// число возвратов за день и за месяц совпадают с «Документы → Возврат продажи» сайта
    /// (21–28.09: 21 возврат, 21 973,00; 1–28.09: 22, 22 785,99). Ответ — простой массив.
    /// Пока у строк пустые items и shift=null (сервер не пишет смену возврата).
    /// null — сервер недоступен.</summary>
    public static async Task<IReadOnlyList<PosReturn>?> ListReturnsAsync(
        DateTime? dateFrom,
        DateTime? dateToInclusive,
        string? shiftId = null,
        string? saleId = null,
        CancellationToken ct = default)
    {
        if (Client is not { } client)
            return null;

        var query = new Dictionary<string, string>();
        if (dateFrom is { } from)
            query["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateToInclusive is { } to)
            query["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(shiftId))
            query["shift"] = shiftId.Trim();
        if (!string.IsNullOrWhiteSpace(saleId))
            query["sale"] = saleId.Trim();

        try
        {
            var result = new List<PosReturn>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Сейчас сервер отдаёт массив целиком; если когда-нибудь станет страницами
            // ({count, next, results}) — дочитываем, но не бесконечно.
            for (var page = 1; page <= 20; page++)
            {
                if (page > 1)
                    query["page"] = page.ToString(CultureInfo.InvariantCulture);
                var data = await client.RequestAsync(HttpMethod.Get, "api/main/pos/returns/", null, query, ct).ConfigureAwait(false);
                var rows = data.ValueKind == JsonValueKind.Array
                    ? data
                    : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                        ? r
                        : default;
                if (rows.ValueKind != JsonValueKind.Array)
                    break;

                var added = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Str(row, "id");
                    if (id is null || !seen.Add(id))
                        continue;
                    added++;
                    result.Add(new PosReturn(
                        id,
                        Str(row, "sale"),
                        Long(row, "sale_number"),
                        Dec(row, "amount") ?? 0m,
                        row.TryGetProperty("is_full", out var full) && full.ValueKind == JsonValueKind.True,
                        Str(row, "cashier"),
                        Str(row, "shift"),
                        Str(row, "created_at") is { } at
                            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                            ? created
                            : null));
                }

                var hasNext = data.ValueKind == JsonValueKind.Object
                              && data.TryGetProperty("next", out var next)
                              && next.ValueKind == JsonValueKind.String;
                if (!hasNext || added == 0)
                    break;
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список возвратов с сервера не получен: {ex.Message}", "SALES");
            return null;
        }
    }

    /// <summary>Число и сумма возвратов за дни [from; to] включительно — те же цифры, что
    /// «Документы → Возврат продажи» сайта. null — сервер недоступен.</summary>
    public static async Task<(int Count, decimal Sum)?> ReturnsTotalsAsync(DateTime from, DateTime toInclusive, CancellationToken ct = default)
    {
        var list = await ListReturnsAsync(from.Date, toInclusive.Date, ct: ct).ConfigureAwait(false);
        return list is null ? null : (list.Count, list.Sum(r => r.Amount));
    }

    /// <summary>Возвраты одной смены. Сначала — по полю shift самого возврата (так будет, когда
    /// сервер начнёт его заполнять). ВРЕМЕННО (2026-09-28): shift у возвратов всегда пустой, поэтому
    /// отбираем по кассиру смены и времени: возврат сделан этим кассиром между открытием и закрытием
    /// смены (у кассира одновременно открыта только одна своя смена). null — сервер недоступен.</summary>
    public static async Task<(int Count, decimal Sum)?> ReturnsForShiftAsync(ServerShiftReport report, CancellationToken ct = default)
    {
        if (report.OpenedAt is not { } opened || string.IsNullOrWhiteSpace(report.ShiftId))
            return null;

        var closed = report.ClosedAt ?? DateTimeOffset.Now;
        // Даты сервера — местные; берём с запасом в день с обеих сторон, точный отбор — по времени.
        var list = await ListReturnsAsync(
            opened.LocalDateTime.Date.AddDays(-1),
            closed.LocalDateTime.Date.AddDays(1),
            ct: ct).ConfigureAwait(false);
        if (list is null)
            return null;

        var byShift = list.Where(r => string.Equals(r.ShiftId, report.ShiftId, StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = byShift.Count > 0 || list.Any(r => !string.IsNullOrEmpty(r.ShiftId))
            ? byShift
            : list.Where(r => r.CreatedAt is { } at
                              && at >= opened && at <= closed
                              && !string.IsNullOrEmpty(report.CashierId)
                              && string.Equals(r.CashierId, report.CashierId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return (chosen.Count, chosen.Sum(r => r.Amount));
    }

    // ── BE-08: постоянный номер чека ───────────────────────────────────────────────────

    /// <summary>Постоянный номер продажи (поле «number», присваивается сервером при оплате и
    /// больше не меняется). null — сервер его не прислал.</summary>
    public static long? TryReadSaleNumber(JsonElement sale) =>
        sale.ValueKind == JsonValueKind.Object ? Long(sale, "number") : null;

    /// <summary>Номер из того, что ввёл кассир: «1115», «№1115», «#001115». null — это не номер.</summary>
    public static long? ParseSaleNumber(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('№', '#').Trim();
        return t.Length is > 0 and <= 12
               && t.All(char.IsAsciiDigit)
               && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
               && n > 0
            ? n
            : null;
    }

    /// <summary>GET api/main/pos/sales/?number=N — продажа по постоянному номеру (строка списка
    /// продаж). Номер сверяется ещё раз: старый сервер параметр number проигнорировал бы и отдал
    /// первую страницу всех продаж. null — не найдено или сервер недоступен.</summary>
    public static async Task<JsonElement?> FindSaleByNumberAsync(long number, CancellationToken ct = default)
    {
        if (number <= 0 || Client is not { } client)
            return null;

        try
        {
            var data = await client.RequestAsync(
                HttpMethod.Get,
                "api/main/pos/sales/",
                null,
                new Dictionary<string, string> { ["number"] = number.ToString(CultureInfo.InvariantCulture) },
                ct).ConfigureAwait(false);
            var rows = data.ValueKind == JsonValueKind.Array
                ? data
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r : default;
            if (rows.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var row in rows.EnumerateArray())
            {
                if (TryReadSaleNumber(row) == number)
                    return row.Clone();
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Продажа №{number} не найдена на сервере: {ex.Message}", "SALES");
            return null;
        }
    }

    // ── BE-06: движения денег по source_id ─────────────────────────────────────────────

    /// <summary>GET api/construction/cashflows/?source_id=… — движения, записанные кассой под этим
    /// ID операции (внесение/изъятие). Фильтр сервер поддерживает с 2026-09-28; строки
    /// сверяются ещё раз на случай, если он его проигнорирует. null — сервер недоступен.</summary>
    public static async Task<IReadOnlyList<(string Id, string? SourceKind)>?> CashFlowsBySourceIdAsync(
        string sourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || Client is not { } client)
            return null;

        try
        {
            var data = await client.RequestAsync(
                HttpMethod.Get,
                "api/construction/cashflows/",
                null,
                new Dictionary<string, string> { ["source_id"] = sourceId.Trim() },
                ct).ConfigureAwait(false);
            var rows = data.ValueKind == JsonValueKind.Array
                ? data
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r : default;
            if (rows.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<(string, string?)>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!string.Equals(Str(row, "source_id"), sourceId.Trim(), StringComparison.OrdinalIgnoreCase)
                    || Str(row, "id") is not { } id)
                    continue;
                result.Add((id, Str(row, "source_kind")));
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Движения денег по source_id не прочитаны: {ex.Message}", "SHIFT");
            return null;
        }
    }

    private static string? Str(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static decimal? Dec(JsonElement e, string key) =>
        Str(e, key) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Long(JsonElement e, string key) =>
        Str(e, key) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
