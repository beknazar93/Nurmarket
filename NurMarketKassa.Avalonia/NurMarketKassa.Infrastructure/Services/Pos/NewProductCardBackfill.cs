using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06 (класс Б, Б-1): товар, которого в базе кассы не было, пришёл лентой products/changes в сокращённой карточке (9 полей: без единицы, «весового»,
/// вида, фасовки, категории, бренда, PLU). Раньше он записывался как штучный «шт» без вида и фасовки до следующей полной загрузки (15–30 минут или сутки),
/// и весовой товар, заведённый на сайте, продавался без окна взвешивания. Теперь полную карточку нового товара дочитываем с сервера: по одной, не больше
/// <see cref="MaxPerMinute"/> в минуту, без связи пропускаем (повтор до <see cref="MaxAttempts"/> раз). Из полной карточки пишутся все поля, остаток
/// остаётся из ленты (он свежее и учитывает продажи этого ПК).
/// </summary>
public static class NewProductCardBackfill
{
    /// <summary>Не больше стольких карточек в минуту (сервер уже отдаёт ленту раз в несколько секунд — лишнюю нагрузку не создаём).</summary>
    public const int MaxPerMinute = 30;

    /// <summary>Очередь больше этого числа не растёт: остальное подтянет полная загрузка каталога.</summary>
    public const int MaxQueue = 300;

    public const int MaxAttempts = 3;

    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60.0 / MaxPerMinute);
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly ConcurrentDictionary<string, int> Attempts = new(StringComparer.OrdinalIgnoreCase);
    private static int _running;
    private static long _lastRequestTicks;

    /// <summary>Подмена источника API (стенды и тесты); по умолчанию — касса.</summary>
    internal static Func<ICatalogApiService?> ApiProvider { get; set; } = () => PosApp.CatalogApi;

    /// <summary>Стенды и тесты отключают фоновую задачу и вызывают <see cref="DrainAsync"/> сами.</summary>
    internal static bool AutoRun { get; set; } = true;

    public static int Pending => Queue.Count;

    /// <summary>Карточка ленты сокращённая: в ней нет ни одного ключа единицы/«весового» и нет вида — такой товар нужно дочитать.</summary>
    public static bool IsShortFeedCard(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && !CatalogFeedSync.HasUnitOrWeightKey(item)
        && !item.TryGetProperty("kind", out _);

    /// <summary>Поставить новые товары в очередь дочитывания. Возвращает, сколько принято.</summary>
    public static int Enqueue(IEnumerable<string> ids)
    {
        var added = 0;
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || Queue.Count >= MaxQueue)
                continue;
            Queue.Enqueue(id.Trim());
            added++;
        }

        if (added > 0 && AutoRun)
            Start();
        return added;
    }

    private static void Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!Queue.IsEmpty)
                {
                    var api = ApiProvider();
                    if (api is null || OfflineModeHelper.SellLocally)
                    {
                        // Без связи не нагружаем: очередь сбрасываем, оставшееся подтянет полная загрузка.
                        while (Queue.TryDequeue(out _)) { }
                        Attempts.Clear();
                        break;
                    }

                    var done = await DrainAsync(api, PosApp.Settings?.ApiBaseUrl ?? "", CancellationToken.None).ConfigureAwait(false);
                    if (!done && !Queue.IsEmpty)
                        await Task.Delay(RetryPause).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"CATALOG: новые товары не дочитаны ({ex.GetType().Name}: {ex.Message}).", "CATALOG");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    /// <summary>Прочитать карточки всей очереди по одной, соблюдая <see cref="MaxPerMinute"/>. false — была ошибка связи (карточка вернулась в очередь, нужна пауза).
    /// Открыто для тестов: пауза между запросами выдерживается только при <paramref name="respectRate"/>.</summary>
    public static async Task<bool> DrainAsync(ICatalogApiService api, string apiBaseUrl, CancellationToken ct, bool respectRate = true)
    {
        var filled = 0;
        var clean = true;
        var count = Queue.Count;
        for (var i = 0; i < count && Queue.TryDequeue(out var id); i++)
        {
            if (respectRate)
            {
                var wait = MinGap - TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequestTicks));
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
            JsonElement? detail = null;
            var failed = false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                detail = await api.ProductsDetailAsync(id, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested
                                       || ex is System.Net.Http.HttpRequestException or ApiException)
            {
                failed = true;
            }

            if (detail is { ValueKind: JsonValueKind.Object } card)
            {
                if (ApplyFullCard(card, apiBaseUrl))
                    filled++;
                Attempts.TryRemove(id, out _);
                continue;
            }

            failed = true; // ProductsDetailAsync при 5xx/429/404 отдаёт null

            // Карточку не прочитали (нет связи, 5xx/429 или 404): ещё раз попробуем позже, но не бесконечно.
            var tries = Attempts.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (tries < MaxAttempts)
                Queue.Enqueue(id);
            else
                Attempts.TryRemove(id, out _);
            if (failed)
                clean = false;
        }

        if (filled > 0)
        {
            PosLogger.Log($"CATALOG: у {filled} новых товаров из ленты дочитана полная карточка (единица, вид, фасовка, категория, бренд, PLU).", "CATALOG");
            CatalogCacheService.NotifyCatalogChanged();
        }

        return clean;
    }

    /// <summary>Записать полную карточку нового товара в базу поверх сокращённой записи. Остаток — из базы (его уже вела лента и продажи этого ПК).
    /// Товара в базе уже нет (удалён) — ничего не пишем. true — запись изменена.</summary>
    public static bool ApplyFullCard(JsonElement card, string apiBaseUrl)
    {
        if (card.ValueKind != JsonValueKind.Object || ProductCatalogMapper.TryTile(card, apiBaseUrl) is not { } fresh)
            return false;
        if (ProductCatalogMapper.IsArchived(card))
            return false;
        var repo = LocalProductRepository.Instance;
        if (repo.LoadTileCopy(fresh.Id) is not { } local)
            return false;
        CatalogFeedSync.KeepLocalForAbsentFields(card, fresh, local);
        StockSyncService.ApplyQuantityToTile(fresh, local.Quantity, fresh.MustWeigh);
        var result = repo.ApplyFeedChanges([fresh], [], []);
        if (result.Changed + result.Added == 0)
            return false;
        // Строка в каталоге памяти (то, что видят окна) — на свежую запись из базы.
        if (repo.LoadTileCopy(fresh.Id) is { } stored)
            UiDispatcherHolderPost(() =>
            {
                var index = CatalogCacheService.Products.FindIndex(p => string.Equals(p.Id, stored.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    CatalogCacheService.Products[index] = stored;
            });
        return true;
    }

    private static void UiDispatcherHolderPost(Action action) => NurMarketKassa.Ui.Shared.UiDispatcherHolder.Post(action);

    /// <summary>Сброс состояния между тестами.</summary>
    internal static void ResetForTests()
    {
        while (Queue.TryDequeue(out _)) { }
        Attempts.Clear();
        Interlocked.Exchange(ref _lastRequestTicks, 0);
    }
}
