using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

// 2026-11-11 (О-5): восстановление строк приёмки из черновика. Черновик не заменяет серверную проверку: у товаров СКЛАДА остаток и «прежние» цены/название
// перечитываются из каталога (при проведении всё сверяется как обычно: «цена продажи изменится», «ниже закупки» и т.д.), а у кого остаток изменился —
// считаем и сообщаем кассиру.

/// <summary>Итог восстановления: строки в порядке файла и сколько товаров склада изменили остаток с момента сохранения.</summary>
public sealed record ReceivingDraftRestored(List<ReceivingLineVm> Lines, int StockChanged);

public static class ReceivingDraftRestore
{
    /// <summary>Снимок строк приёмки для записи (порядок на экране сохраняется, «№» — тоже).</summary>
    public static List<ReceivingDraftLine> Capture(IEnumerable<ReceivingLineVm> lines) => lines.Select(l => l.ToDraft()).ToList();

    /// <summary>Каталог кассы по id товара (для перечитывания остатка).</summary>
    public static Func<string, CatalogProductTileVm?> CatalogLookup()
    {
        Dictionary<string, CatalogProductTileVm>? map = null;
        return id =>
        {
            if (map is null)
            {
                map = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in CatalogCacheService.Products.ToList())
                    if (!string.IsNullOrEmpty(p.Id))
                        map.TryAdd(p.Id, p);
            }
            return map.TryGetValue(id, out var tile) ? tile : null;
        };
    }

    /// <summary>Собрать строки из черновика. <paramref name="lookup"/> — поиск карточки товара склада по id (по умолчанию — каталог кассы).</summary>
    public static ReceivingDraftRestored Build(ReceivingDraftFile file, Func<string, CatalogProductTileVm?>? lookup = null)
    {
        lookup ??= CatalogLookup();
        var lines = new List<ReceivingLineVm>(file.Lines.Count);
        var changed = 0;
        foreach (var d in file.Lines)
        {
            ReceivingLineVm? fresh = null;
            if (d.Source == ReceivingSource.Warehouse && !string.IsNullOrWhiteSpace(d.ProductId))
            {
                try
                {
                    if (lookup(d.ProductId) is { } tile)
                    {
                        fresh = PurchaseReceivingService.FromTile(tile, 0);
                        if (Math.Abs(fresh.StockBefore - d.StockBefore) > 1e-9)
                            changed++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Черновик приёмки: карточка товара не перечитана ({ex.Message}) — остаются значения из черновика.", "WARNING");
                }
            }
            lines.Add(ReceivingLineVm.FromDraft(d, fresh));
        }
        return new ReceivingDraftRestored(lines, changed);
    }
}
