using System;
using System.Collections.Generic;
using System.Globalization;
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
    // 2026-09-28, сверка ABC с сайтом: сервер отдаёт не больше 80 продаж на страницу
    // (SalesApiService.PosSalesListAsync урезает page_size до 80), а цикл ниже ждал 200 и после
    // первой же страницы считал список законченным — история добиралась только по 80 последним
    // продажам, и всё, что старше, в ABC и сезонность не попадало никогда. Теперь страница — 80,
    // страниц — 8 (те же ~600 чеков, что и задумывались).
    private const int PageSize = 80;
    private const int MaxPages = 8;   // до 640 чеков — тот же порядок, что и в «Финансах»

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

    /// <summary>2026-10-04, отчёт о производительности (п. 11): прошлый полный проход дочитал всё, что было
    /// нужно (порция не упёрлась в <see cref="MaxSalesPerPass"/>), — история за 8 страниц полная, и следующий
    /// проход останавливается на первой странице, где нечего добирать.</summary>
    private static bool _backlogComplete;

    /// <summary>Чеки, у которых на сервере нет строк с товаром (например, только «Доп. услуга»): в
    /// локальную историю их записать нечем, и без этого списка такая страница никогда не считалась бы
    /// «уже известной».</summary>
    private static readonly HashSet<string> NothingToRecord = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<int> LoadAsync(CancellationToken ct)
    {
        var known = SoldLineItemsStore.KnownSaleIds();
        var legacyWatermark = SoldLineItemsStore.LegacyWatermark();
        var stopEarly = _backlogComplete;
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
            // 2026-10-04, п. 11: раньше каждые 30 минут листались все 8 страниц (640 чеков), хотя новых —
            // единицы и все на первой странице. Теперь: страница, где все чеки уже в истории (или отменены,
            // или старше отсечки), — дальше листать незачем. Первый проход после запуска — полный (сверка
            // удалённых продаж по всем 8 страницам и добор истории новой кассы).
            if (stopEarly && pageItems.All(sale => !NeedsFetch(sale, known, legacyWatermark)))
                break;
        }

        await PruneRemovedSalesAsync(raw, ct).ConfigureAwait(false);

        // Пропускаем только те чеки, которые в локальной истории уже есть — по номеру
        // продажи. Старая проверка «всё, что новее самой старой локальной записи, уже учтено»
        // верна лишь для одной кассы: на второй она отсекала как раз чужие чеки.
        // 2026-10-04: known/legacyWatermark читаются выше, до листания (нужны для ранней остановки);
        // PruneRemovedSalesAsync убирает из истории только отменённые/удалённые — их и так не тянем.
        var added = 0;

        // Отбираем, что вообще нужно тянуть, и только потом идём в сеть — так видно объём
        // работы и можно честно ограничить порцию.
        var todo = new List<(string SaleId, DateTime CreatedAt)>();
        foreach (var sale in raw)
        {
            ct.ThrowIfCancellationRequested();

            // 2026-10-04: правила отбора вынесены в NeedsFetch без изменений — ими же пользуется ранняя
            // остановка листания выше.
            if (!NeedsFetch(sale, known, legacyWatermark, out var saleId, out var createdAt))
                continue;

            todo.Add((saleId, createdAt));
            if (todo.Count >= MaxSalesPerPass)
                break;
        }

        // 2026-10-04, п. 11: порция не упёрлась в предел — всё нужное из просмотренных страниц дочитано
        // (или дочитается сейчас), и следующий проход может остановиться на первой «известной» странице.
        _backlogComplete = todo.Count < MaxSalesPerPass;

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
                var detail = await SaleDetailCache.GetAsync(item.SaleId, ct).ConfigureAwait(false);
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
                // 2026-09-28: цена — фактическая, после скидки на строку (line_total / количество),
                // как пишет свои продажи сама касса (BasketPanelViewModel.RecordSoldLineItemsForHistory).
                // Раньше бралась unit_price — цена ДО скидки, и выгрузка считала «Проверку товара
                // 18+» по 205 вместо 184,50, завышая выручку ровно на сумму скидок.
                var quantity = CartDisplayHelper.LineQuantity(line);
                var unitPrice = CartDisplayHelper.UnitPrice(line);
                if (quantity > 1e-9
                    && double.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Number, CultureInfo.InvariantCulture, out var lineTotal)
                    && lineTotal >= 0)
                    unitPrice = lineTotal / quantity;
                lines.Add((
                    productId,
                    name,
                    quantity,
                    unitPrice,
                    createdAt));
            }

            if (lines.Count == 0)
            {
                // 2026-10-04: записывать нечего — помним, чтобы не тянуть этот чек каждые 30 минут.
                lock (NothingToRecord)
                    NothingToRecord.Add(saleId);
                continue;
            }

            SoldLineItemsStore.AppendBackfill(lines, saleId);
            known.Add(saleId);
            added += lines.Count;
        }

        if (added > 0)
            PosLogger.Log($"История продаж: с сервера добрано {added} строк(и).", "SYNC");

        return added;
    }

    /// <summary>Нужно ли тянуть этот чек в локальную историю (правила — как были в цикле отбора LoadAsync).</summary>
    private static bool NeedsFetch(JsonElement sale, HashSet<string> known, DateTime? legacyWatermark,
        out string saleId, out DateTime createdAtUtc)
    {
        saleId = "";
        createdAtUtc = default;
        if (!sale.TryGetProperty("id", out var idProp))
            return false;

        saleId = idProp.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(saleId) || known.Contains(saleId))
            return false;
        lock (NothingToRecord)
        {
            if (NothingToRecord.Contains(saleId))
                return false;
        }

        // Отменённая продажа — не продажа: в ABC и выручку её тянуть нельзя (2026-09-24,
        // раньше тянулась, а новая сверка тут же убирала её обратно — по кругу).
        if (sale.TryGetProperty("status", out var statusProp)
            && string.Equals(statusProp.GetString(), "canceled", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!sale.TryGetProperty("created_at", out var dateProp) ||
            !DateTime.TryParse(dateProp.GetString(), out var createdAt))
            return false;

        // Всё, что старше отсечки, локальная история уже содержит — просто без номера,
        // поэтому по номеру этого не видно. Подтянуть такие чеки — значит задвоить их.
        if (legacyWatermark is { } watermark && createdAt.ToUniversalTime() <= watermark)
            return false;

        createdAtUtc = createdAt.ToUniversalTime();
        return true;
    }

    private static bool NeedsFetch(JsonElement sale, HashSet<string> known, DateTime? legacyWatermark) =>
        NeedsFetch(sale, known, legacyWatermark, out _, out _);

    /// <summary>Сколько подозрительных продаж проверяем за проход — каждая это отдельный запрос.</summary>
    private const int MaxPruneChecksPerPass = 30;

    /// <summary>Убирает из локальной истории продажи, которых на сервере больше нет или которые
    /// там отменены. 2026-09-24, найдено сверкой аналитики: «Кир машина» за 20 000 в долг
    /// удалили на сайте, а касса продолжала считать её в ABC, «Финансах» и прогнозах —
    /// история только дописывалась и ни разу не сверялась обратно.
    ///
    /// Продажу убираем, только когда сервер ПРЯМО ответил про неё самой: «нет такой» (404) или
    /// статус «отменена». По одному лишь её отсутствию в списке судить нельзя: первая версия
    /// так и делала, решила, что список полный (сервер отдаёт по 100 продаж на страницу, а не
    /// по 200), и убрала 39 живых продаж — их пришлось возвращать из копии базы.
    /// Список служит только фильтром — кого проверить. Свежие продажи и строки без номера
    /// (офлайн-чеки, ещё не выгруженные на сервер) не трогаем.</summary>
    private static async Task PruneRemovedSalesAsync(List<JsonElement> raw, CancellationToken ct)
    {
        if (raw.Count == 0)
            return;

        try
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oldest = DateTime.MaxValue;
            foreach (var sale in raw)
            {
                if (!sale.TryGetProperty("id", out var idProp))
                    continue;
                var status = sale.TryGetProperty("status", out var st) ? st.GetString() : null;
                // Отменённую из списка не считаем «живой» — пусть пройдёт прямую проверку ниже.
                if (!string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                    listed.Add(idProp.ToString());
                if (sale.TryGetProperty("created_at", out var dateProp)
                    && DateTime.TryParse(dateProp.GetString(), out var createdAt)
                    && createdAt.ToUniversalTime() < oldest)
                    oldest = createdAt.ToUniversalTime();
            }

            // Пробитая, пока скачивался список, в нём ещё не появилась — свежие не проверяем.
            var until = DateTime.UtcNow.AddMinutes(-10);
            if (oldest == DateTime.MaxValue || oldest >= until)
                return;

            var suspects = SoldLineItemsStore.KnownSaleIdsBetween(oldest, until)
                .Where(id => !listed.Contains(id))
                .Take(MaxPruneChecksPerPass)
                .ToList();

            var gone = new List<string>();
            foreach (var saleId in suspects)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Свежий статус, не из кэша отчётов, но в общем темпе массовых загрузок.
                    var sale = await NurMarketKassa.Services.Api.ApiThrottle
                        .RunBulkAsync(() => App.SalesApi.PosSaleGetAsync(saleId, ct), ct).ConfigureAwait(false);
                    var status = sale.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                        gone.Add(saleId);
                }
                catch (ApiException ex) when (ex.StatusCode == 404)
                {
                    gone.Add(saleId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Нет связи или сервер ответил чем-то другим — ничего не решаем.
                    PosLogger.Log($"История продаж: продажа {saleId} не проверена: {ex.Message}", "DEBUG");
                }
            }

            if (gone.Count == 0)
                return;

            var removed = SoldLineItemsStore.RemoveSales(gone);
            PosLogger.Log($"История продаж: убрано {gone.Count} продаж(и), удалённых или отменённых на сервере ({removed} строк).", "SYNC");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"История продаж: сверка удалённых продаж пропущена: {ex.Message}", "WARNING");
        }
    }
}
