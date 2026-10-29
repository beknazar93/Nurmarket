using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-29, «остатки обновляются очень медленно»: лента изменений каталога раньше вообще не применяла остаток, если включена
/// локальная сеть касс ИЛИ есть хоть один неотправленный офлайн-чек (общий выключатель на все товары). Теперь решение — по товару:
/// остаток из ленты НЕ применяется только к товарам, у которых на этом ПК есть незавершённые операции —
/// (1) в очереди неотправленных офлайн-чеков есть строка этого товара (сервер ещё не знает продажу — остаток «откатился» бы);
/// (2) соседние кассы по локальной сети продали его, а сервер ещё не знает (LanStockAdjuster);
/// (3) остаток товара менялся на этом ПК за последние <see cref="LocalTouchWindow"/> (продажа/возврат/ревизия/приход ушли на сервер, но
///     ответ ленты мог быть построен до того, как сервер их учёл).
/// Остальные товары получают серверный остаток сразу — приход, списание и ревизия владельца доходят до кассы за период ленты (~8 с).
/// Деньги не затрагиваются: это только показ и проверка остатка при продаже.
/// </summary>
public static class FeedStockGuard
{
    /// <summary>Окно «остаток менялся здесь недавно»: за это время запрос продажи/возврата успевает дойти до сервера.</summary>
    public static readonly TimeSpan LocalTouchWindow = TimeSpan.FromSeconds(90);

    /// <summary>Строит предикат «остаток этого товара из ленты применять можно» на один проход ленты (очередь и журнал LAN читаются один раз).</summary>
    public static Func<string, bool> Create()
    {
        HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in OfflinePendingSalesStore.LoadPendingForSync())
            {
                try
                {
                    using var doc = JsonDocument.Parse(entry.CartJson);
                    foreach (var line in CartDisplayHelper.EnumerateItems(doc.RootElement))
                        if (CartDisplayHelper.TryProductId(line) is { Length: > 0 } id)
                            blocked.Add(id);
                }
                catch (JsonException)
                {
                    // Чек с битым JSON: на всякий случай не применяем остаток вовсе (см. ниже) — нельзя понять, какие товары в нём.
                    return _ => false;
                }
            }

            if (UserPreferences.Instance.LanSyncEnabled)
                foreach (var id in Lan.LanStockAdjuster.PendingProductIds())
                    blocked.Add(id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"STOCK: защита остатка ленты не построена ({ex.Message}) — остатки ленты не применяются.", "STOCK");
            return _ => false;
        }

        return id => !blocked.Contains(id) && !StockSyncService.WasLocallyTouched(id, LocalTouchWindow);
    }
}
