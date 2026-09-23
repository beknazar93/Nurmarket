using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost;

namespace NurMarketKassa.Services;

/// <summary>
/// Подтягивает историю продаж компании с сервера в локальную таблицу SoldLineItems.
///
/// Аналитика (ABC, сезонность, прогноз пополнения) считается по локальным строкам — их пишет
/// касса после каждой продажи. На компьютере, где эта компания ещё не продавала, история пуста,
/// и раздел выглядел бы пустым, хотя продажи на сервере есть. Раньше это скрывалось тем, что в
/// таблице лежали строки предыдущего аккаунта — то есть вместо своих цифр показывались чужие.
///
/// Раньше метод жил внутри окна «Пополнение склада» и запускался только по кнопке. Вынесен
/// сюда, чтобы им могли пользоваться и другие разделы.
/// </summary>
public static class SalesHistoryBackfill
{
    private const int PageSize = 200;
    private const int MaxPages = 3;   // до 600 чеков — тот же порядок, что и в «Финансах»

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Тянет продажи с сервера и дописывает те, которых ещё нет локально.
    /// Возвращает число добавленных строк.</summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        // Два окна могут попросить бэкфилл одновременно (ABC при открытии и «Пополнение
        // склада» по кнопке) — без замка история задвоилась бы.
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<int> LoadAsync(CancellationToken ct)
    {
        var raw = new List<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var pageItems = await App.SalesApi.PosSalesListAsync(page, PageSize, null, ct).ConfigureAwait(false);
            if (pageItems.Count == 0)
                break;
            raw.AddRange(pageItems);
            if (pageItems.Count < PageSize)
                break;
        }

        // Локальная запись делается сразу после продажи, поэтому всё, что новее самой старой
        // локальной строки, уже учтено — второй раз не пишем.
        var earliestLocal = SoldLineItemsStore.GetEarliestDate();

        var lines = new List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>();
        foreach (var sale in raw)
        {
            ct.ThrowIfCancellationRequested();

            if (!sale.TryGetProperty("id", out var idProp))
                continue;
            if (!sale.TryGetProperty("created_at", out var dateProp) ||
                !DateTime.TryParse(dateProp.GetString(), out var createdAt))
                continue;
            if (earliestLocal.HasValue && createdAt.ToUniversalTime() >= earliestLocal.Value)
                continue;

            JsonElement detail;
            try
            {
                detail = await App.SalesApi.PosSaleGetAsync(idProp.ToString() ?? "", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Чек не прочитан при загрузке истории: {ex.Message}", "DEBUG");
                continue;
            }

            if (!detail.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var line in items.EnumerateArray())
            {
                var productId = CartDisplayHelper.TryProductId(line);
                if (string.IsNullOrEmpty(productId))
                    continue;

                var name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                lines.Add((
                    productId,
                    name,
                    CartDisplayHelper.LineQuantity(line),
                    CartDisplayHelper.UnitPrice(line),
                    createdAt.ToUniversalTime()));
            }
        }

        if (lines.Count > 0)
            SoldLineItemsStore.AppendBackfill(lines);

        return lines.Count;
    }
}
