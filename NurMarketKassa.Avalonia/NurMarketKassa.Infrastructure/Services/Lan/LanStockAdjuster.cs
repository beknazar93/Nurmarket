using System.Globalization;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Lan;

/// <summary>
/// Остаток на кассе с учётом продаж соседних касс, которых сервер ещё не знает.
///
/// Остаток кассы = последний снимок склада с сервера минус продажи соседей, не вошедшие в этот
/// снимок. Продажа вошла в снимок, если снимок начали брать ПОСЛЕ того, как эта касса узнала,
/// что продажа уже на сервере (время по своим часам — часы касс могут расходиться). Сколько
/// вычтено сейчас, лежит в LanStockAdjust: между снимками довычитается только разница, а новый
/// снимок заменяет остаток целиком и поправка считается заново — ошибки не накапливаются.
/// </summary>
public static class LanStockAdjuster
{
    private const string SnapshotMetaKey = "stock_snapshot_utc";
    private static readonly TimeSpan Window = TimeSpan.FromDays(4);
    private static readonly object Gate = new();

    /// <summary>Снимок склада с сервера, ещё не записанный в базу: вычитает из него продажи
    /// соседей, которых в нём нет, и запоминает, сколько вычтено.</summary>
    /// <param name="startedUtc">Когда начали загружать снимок (по своим часам).</param>
    public static void AdjustSnapshot(IReadOnlyList<CatalogProductTileVm> snapshot, DateTime startedUtc)
    {
        try
        {
            lock (Gate)
            {
                var pending = UserPreferences.Instance.LanSyncEnabled
                    ? Pending(startedUtc)
                    : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                if (pending.Count > 0)
                    foreach (var tile in snapshot)
                        if (tile.Id != null && pending.TryGetValue(tile.Id, out var qty) && qty > 0)
                            StockSyncService.ApplyQuantityToTile(tile, Math.Max(0, tile.Quantity - qty), tile.MustWeigh);

                LanJournal.WriteStockAdjust(pending);
                LanJournal.WriteMeta(SnapshotMetaKey, startedUtc.ToString("o", CultureInfo.InvariantCulture));
                if (pending.Count > 0)
                    PosLogger.Log($"LAN: к снимку склада применены продажи соседей по {pending.Count} товарам", "LAN");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: поправка снимка склада не удалась: {ex.Message}", "LAN");
        }
    }

    /// <summary>Пришли новые продажи соседей — сразу уменьшить остаток на экране и в базе.</summary>
    public static void ApplyPeerSales()
    {
        try
        {
            if (AppMode.IsOwner || !UserPreferences.Instance.LanSyncEnabled)
                return;

            lock (Gate)
            {
                var pending = Pending(SnapshotTime());
                var applied = LanJournal.ReadStockAdjust();
                var changed = 0;
                foreach (var (id, qty) in pending)
                {
                    var delta = qty - (applied.TryGetValue(id, out var was) ? was : 0);
                    // Между снимками продажи соседей только добавляются; уменьшение возможно лишь
                    // после очистки старого журнала — остаток за это не возвращаем, это сделает снимок.
                    if (delta <= 1e-9)
                        continue;

                    var tile = LanSalePublisher.FindTile(id);
                    if (tile != null)
                    {
                        var next = Math.Max(0, tile.Quantity - delta);
                        LocalProductRepository.Instance.UpdateStock(id, next, tile.MustWeigh);
                        StockSyncService.ApplyQuantityToTileOnUi(tile, next, tile.MustWeigh);
                    }

                    applied[id] = qty;
                    changed++;
                }

                if (changed > 0)
                {
                    LanJournal.WriteStockAdjust(applied);
                    PosLogger.Log($"LAN: остаток уменьшен по продажам соседей: {changed} товаров", "LAN");
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: поправка остатка не удалась: {ex.Message}", "LAN");
        }
    }

    /// <summary>Сколько каждого товара продали соседи сверх того, что знает снимок.</summary>
    private static Dictionary<string, double> Pending(DateTime snapshotUtc)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in LanJournal.ReadPeerSales(DateTime.UtcNow - Window))
        {
            if (s.KnownOnServerSinceUtc is { } known && known <= snapshotUtc)
                continue;
            foreach (var d in s.Sale.Stock)
                if (!string.IsNullOrEmpty(d.ProductId) && d.Qty > 0)
                    map[d.ProductId] = map.TryGetValue(d.ProductId, out var q) ? q + d.Qty : d.Qty;
        }

        return map;
    }

    private static DateTime SnapshotTime()
    {
        var raw = LanJournal.ReadMeta(SnapshotMetaKey);
        if (raw != null && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return at;
        // Снимков с обменом ещё не было: остаток в базе — от последней полной загрузки каталога.
        return LocalProductRepository.Instance.GetLastSyncTime()?.ToUniversalTime() ?? DateTime.MinValue;
    }
}
