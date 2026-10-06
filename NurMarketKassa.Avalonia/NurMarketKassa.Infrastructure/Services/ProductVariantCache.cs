using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-04, владелец: «по одеждам посмотри бизнес-логику, очень тормозит, и при продаже одежды
/// количество не уменьшается». Общий кеш вариантов товара (размер/цвет) для окна выбора размера, проката и бота.
/// Раньше у каждого был свой кеш на 2 минуты: окно размеров при каждом первом нажатии ждало сервер (до 6 с,
/// без интернета — все 6 с), а после продажи ещё до 2 минут показывало прежний остаток размера — сервер при
/// этом списывал правильно (проверено продажами на тестовом аккаунте: и остаток товара, и остаток размера −1).
/// Теперь: что уже есть в кеше — отдаётся сразу, устаревшее обновляется в фоне; после продажи и выдачи в прокат
/// остаток размера в кеше уменьшается сразу (<see cref="ApplySale"/>, <see cref="Adjust"/>); сервер не отвечает —
/// его не ждём. Масштаб (15 000 клиентов): запросы только по товарам, которые открыл кассир или назвал покупатель,
/// один запрос на товар одновременно.</summary>
public static class ProductVariantCache
{
    /// <summary>Загрузка вариантов с сервера — ставит программа (CatalogApi.GetProductVariantsAsync).</summary>
    public static Func<string, CancellationToken, Task<List<ProductVariantDto>>>? Loader { get; set; }

    /// <summary>Младше этого — свежие, фоновое обновление не запускается.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public DateTime At;
        public List<ProductVariantDto> List = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<List<ProductVariantDto>?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object InFlightLock = new();

    /// <summary>Варианты товара: из кеша — сразу (устаревшие обновляются в фоне), иначе — с сервера, но ждём
    /// не дольше <paramref name="timeout"/>. null — вариантов не узнать (сервер недоступен, в кеше пусто).</summary>
    public static async Task<List<ProductVariantDto>?> GetAsync(string productId, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        var id = productId.Trim();
        if (Cache.TryGetValue(id, out var cached))
        {
            if (DateTime.UtcNow - cached.At >= FreshFor && CanAskServer)
                _ = LoadAsync(id);
            return Clone(cached.List);
        }

        if (!CanAskServer)
            return null;
        var load = LoadAsync(id);
        var finished = await Task.WhenAny(load, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != load)
        {
            PosLogger.Log($"Варианты товара {id}: сервер не ответил за {timeout.TotalSeconds:0.#} с — без размера/цвета.", "CART");
            return null;
        }

        var list = await load.ConfigureAwait(false);
        return list is null ? null : Clone(list);
    }

    /// <summary>То же для потока бота (не UI): ждёт ответа сервера не дольше <paramref name="timeout"/>.</summary>
    public static List<ProductVariantDto>? Get(string productId, TimeSpan timeout)
    {
        try
        {
            return Task.Run(() => GetAsync(productId, timeout)).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Есть ли товар в кеше (без запроса к серверу) — бот берёт сначала такие.</summary>
    public static bool IsCached(string productId) =>
        !string.IsNullOrWhiteSpace(productId) && Cache.ContainsKey(productId.Trim());

    /// <summary>Сразу после проведённой продажи: остаток проданных размеров/цветов в кеше −количество строки
    /// (строки чека с server_variant_id). Остаток самого товара уменьшает StockSyncService.ApplySoldItemsDecrement.</summary>
    public static void ApplySale(JsonElement cart)
    {
        var changed = 0;
        foreach (var line in CartDisplayHelper.EnumerateItems(cart))
        {
            var variantId = CartDisplayHelper.ServerVariantId(line);
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrWhiteSpace(variantId) || string.IsNullOrWhiteSpace(productId))
                continue;
            if (Adjust(productId, variantId, -CartDisplayHelper.LineQuantity(line)))
                changed++;
        }

        if (changed > 0)
            PosLogger.Log($"Остаток размеров после продажи уменьшен сразу: строк {changed}.", "STOCK");
    }

    /// <summary>Изменить остаток одного варианта в кеше (продажа, прокат: delta &lt; 0; возврат: &gt; 0).
    /// false — товара или варианта в кеше нет (тогда при следующем открытии придёт с сервера).</summary>
    public static bool Adjust(string productId, string variantId, double delta)
    {
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(variantId) || !double.IsFinite(delta))
            return false;
        if (!Cache.TryGetValue(productId.Trim(), out var entry))
            return false;
        lock (entry.List)
        {
            var v = entry.List.FirstOrDefault(x => string.Equals(x.Id, variantId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (v is null)
                return false;
            v.Quantity = Math.Max(0, Math.Round(v.Quantity + delta, 3));
            return true;
        }
    }

    /// <summary>Записать свежий список (например, после правки вариантов в карточке товара).</summary>
    public static void Put(string productId, List<ProductVariantDto> list)
    {
        if (!string.IsNullOrWhiteSpace(productId) && list is not null)
        {
            Cache[productId.Trim()] = new Entry { At = DateTime.UtcNow, List = Clone(list) };
            // 2026-10-06 (О-01): штрихкоды размеров — в справочник для скана этикетки размера.
            VariantBarcodeIndex.Update(productId, list);
        }
    }

    /// <summary>Пометить устаревшим: при следующем открытии покажется сразу и обновится в фоне.</summary>
    public static void MarkStale(string productId)
    {
        if (!string.IsNullOrWhiteSpace(productId) && Cache.TryGetValue(productId.Trim(), out var entry))
            entry.At = DateTime.MinValue;
    }

    /// <summary>Смена компании/кассира — варианты чужой компании показывать нельзя.</summary>
    public static void Clear() => Cache.Clear();

    private static bool CanAskServer => Loader is not null && !OfflineModeHelper.SellLocally;

    private static Task<List<ProductVariantDto>?> LoadAsync(string productId)
    {
        lock (InFlightLock)
        {
            if (InFlight.TryGetValue(productId, out var running))
                return running;
            var task = LoadCoreAsync(productId);
            InFlight[productId] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (InFlightLock)
                {
                    if (InFlight.TryGetValue(productId, out var current) && ReferenceEquals(current, task))
                        InFlight.Remove(productId);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<List<ProductVariantDto>?> LoadCoreAsync(string productId)
    {
        var loader = Loader;
        if (loader is null)
            return null;
        try
        {
            // Своё ограничение — для фонового обновления; вызывающий ждёт не дольше своего timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var list = await Task.Run(() => loader(productId, cts.Token)).ConfigureAwait(false) ?? new List<ProductVariantDto>();
            Cache[productId] = new Entry { At = DateTime.UtcNow, List = list };
            // 2026-10-06 (О-01): штрихкоды размеров — в справочник для скана этикетки размера.
            VariantBarcodeIndex.Update(productId, list);
            return list;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Варианты товара {productId} не получены ({ex.Message}).", "CART");
            return null;
        }
    }

    private static List<ProductVariantDto> Clone(List<ProductVariantDto> list)
    {
        lock (list)
        {
            return list.Select(v => new ProductVariantDto
            {
                Id = v.Id,
                Size = v.Size,
                Color = v.Color,
                Barcode = v.Barcode,
                Quantity = v.Quantity,
                Price = v.Price,
                IsActive = v.IsActive,
            }).ToList();
        }
    }
}
