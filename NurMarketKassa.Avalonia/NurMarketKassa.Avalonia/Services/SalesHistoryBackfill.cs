using System;
using System.Collections.Generic;
using System.Linq;
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
///
/// Он же решает вторую задачу: две кассы под одним аккаунтом не видели продаж друг друга.
/// Каждая писала историю только о своих чеках, и в ABC, сезонности и отчётах на одной кассе
/// не было того, что продали на другой. Теперь недостающие чеки добираются с сервера по
/// номеру продажи, а не по дате: раньше пропускалось всё, что новее самой старой локальной
/// записи, то есть ровно то, чего не хватало.
/// </summary>
public static class SalesHistoryBackfill
{
    private const int PageSize = 200;
    private const int MaxPages = 3;   // до 600 чеков — тот же порядок, что и в «Финансах»

    /// <summary>Сколько чеков дочитываем за один проход.
    ///
    /// Раньше ограничения не было: на кассе с пустой историей бэкфилл запрашивал детали всех
    /// 600 чеков ПОДРЯД. Замер живого API — 0,28–0,47 с на запрос, то есть до четырёх минут, и
    /// всё это время раздел ABC показывал «Загружаю историю продаж с сервера…». Теперь за проход
    /// берём порцию, а остальное доберут следующие проходы фоновой синхронизации.</summary>
    private const int MaxSalesPerPass = 120;

    /// <summary>Сколько запросов деталей идёт одновременно. Последовательно 120 чеков — это
    /// около минуты; по шесть — около десяти секунд. Больше не ставим: это чужой сервер, и
    /// заваливать его ради фоновой задачи незачем.</summary>
    private const int Parallelism = 6;

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

        // Пропускаем только те чеки, которые в локальной истории уже есть — по номеру
        // продажи. Старая проверка «всё, что новее самой старой локальной записи, уже учтено»
        // верна лишь для одной кассы: на второй она отсекала как раз чужие чеки.
        var known = SoldLineItemsStore.KnownSaleIds();
        var legacyWatermark = SoldLineItemsStore.LegacyWatermark();
        var added = 0;

        // Отбираем, что вообще нужно тянуть, и только потом идём в сеть — так видно объём
        // работы и можно честно ограничить порцию.
        var todo = new List<(string SaleId, DateTime CreatedAt)>();
        foreach (var sale in raw)
        {
            ct.ThrowIfCancellationRequested();

            if (!sale.TryGetProperty("id", out var idProp))
                continue;

            var saleId = idProp.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(saleId) || known.Contains(saleId))
                continue;

            if (!sale.TryGetProperty("created_at", out var dateProp) ||
                !DateTime.TryParse(dateProp.GetString(), out var createdAt))
                continue;

            // Всё, что старше отсечки, локальная история уже содержит — просто без номера,
            // поэтому по номеру этого не видно. Подтянуть такие чеки — значит задвоить их.
            if (legacyWatermark is { } watermark && createdAt.ToUniversalTime() <= watermark)
                continue;

            todo.Add((saleId, createdAt.ToUniversalTime()));
            if (todo.Count >= MaxSalesPerPass)
                break;
        }

        if (todo.Count == 0)
            return 0;

        // Детали чеков качаем параллельно: это независимые запросы, и последовательность в них
        // ничего не даёт, кроме ожидания.
        var gate = new SemaphoreSlim(Parallelism);
        var fetched = await Task.WhenAll(todo.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var detail = await App.SalesApi.PosSaleGetAsync(item.SaleId, ct).ConfigureAwait(false);
                return (item.SaleId, item.CreatedAt, Detail: (JsonElement?)detail);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Чек не прочитан при загрузке истории: {ex.Message}", "DEBUG");
                return (item.SaleId, item.CreatedAt, Detail: (JsonElement?)null);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        foreach (var (saleId, createdAt, detail) in fetched)
        {
            ct.ThrowIfCancellationRequested();
            if (detail is not { } sale)
                continue;
            if (!sale.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            // Пишем по одному чеку за раз: номер продажи общий для всех его строк, и при
            // обрыве связи посередине уже записанные чеки повторно не подтянутся.
            var lines = new List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>();
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
                    createdAt));
            }

            if (lines.Count == 0)
                continue;

            SoldLineItemsStore.AppendBackfill(lines, saleId);
            known.Add(saleId);
            added += lines.Count;
        }

        if (added > 0)
            PosLogger.Log($"История продаж: с сервера добрано {added} строк(и).", "SYNC");

        return added;
    }
}
