using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-05 (волна 2-А, функция 1): кнопка «Списать просроченное» в Складе. Не новый способ списания, а цикл по уже существующему:
// каждый просроченный товар списывается тем же путём, что кнопка «Списать» (ProductActions.ChangeStockAsync: свежий остаток с сервера, один акт на товар,
// причина «Просрочка» на русском в примечании акта). Право «Склад: редактирование» проверяет окно.
// 2026-11-06 (В12, С7): вместо одного вопроса «списать всё» — окно со списком и галочками (ExpiredWriteOffDialog); где после даты годности был приход, строка не отмечена.

public static class ExpiredWriteOff
{
    private static int _running;

    /// <summary>Списать отмеченные просроченные товары. Возвращает, сколько товаров списано (0 — ничего или отмена).</summary>
    public static async Task<int> RunAsync(Window? owner)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return 0;
        try
        {
            await ProductExpiryIndex.RefreshAsync(true).ConfigureAwait(true);
            var today = DateTime.Today;
            // 2026-11-06 (удаление товара): удалённый/архивный товар (RemovedProductMemory), которого уже нет в каталоге, не предлагаем списывать — сервер мог ещё держать его отметку срока.
            var expired = ProductExpiryIndex.Marks.Values.Where(m => ExpiryRules.IsExpired(m, today) && m.Quantity > 0.0005
                && !(RemovedProductMemory.Contains(m.ProductId) && ProductActions.Find(m.ProductId) is null)).ToList();
            // Когда последний раз приходовали товар (история закупок этой кассы) — для предупреждения «после даты годности был приход».
            Dictionary<string, List<PurchaseReceivingService.HistoryEntry>> history = new();
            try
            {
                var ids = expired.Select(m => m.ProductId).ToList();
                history = await Task.Run(() => PurchaseReceivingService.Instance.LoadLocalHistoryMany(ids)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Склад: история закупок для списания просроченного не прочитана ({ex.GetType().Name}) — предупреждения о свежей партии не будет.", "WARNING");
            }
            var items = ExpiredWriteOffPlan.Build(expired, today,
                id => ProductActions.Find(id)?.Quantity,
                id => ProductActions.Find(id)?.PurchasePrice ?? 0,
                id => history.TryGetValue(id, out var h) && h.Count > 0 ? h.Max(e => e.At) : null);
            if (items.Count == 0)
            {
                PosDialogs.Info(owner, Tr.T("Просроченных товаров с остатком нет — списывать нечего.", "Калдыгы бар мөөнөтү өткөн товар жок — эсептен чыгаруучу эч нерсе жок.",
                    "No expired products with stock — nothing to write off.", "Stokta süresi geçmiş ürün yok — düşülecek bir şey yok.", "Qoldig'i bor muddati o'tgan mahsulot yo'q — hisobdan chiqariladigan narsa yo'q."));
                return 0;
            }

            if (owner is null)
                return 0; // окно списка открывается только поверх окна Склада
            var dialog = new ExpiredWriteOffDialog(items);
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            if (dialog.Result is not { Count: > 0 } picked)
                return 0;

            var ok = 0;
            var failed = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            foreach (var item in picked)
            {
                var m = item.Mark;
                var qty = item.Qty;
                try
                {
                    var result = await ProductActions.ChangeStockAsync(m.ProductId, -qty, null,
                        $"Списание: Просрочка ({ProductActions.Qty(qty)})", PosApp.CurrentUserDisplayName, cts.Token, historyReason: Tr.T("Просрочка", "Мөөнөтү өткөн", "Expired", "Son kullanma tarihi geçmiş", "Muddati o'tgan")).ConfigureAwait(true);
                    if (result.Ok)
                        ok++;
                    else
                        failed.Add($"{m.Name}: {result.Message}");
                }
                catch (OperationCanceledException)
                {
                    failed.Add($"{m.Name}: " + Tr.T("не дождались ответа сервера", "сервердин жообу күтүлгөн жок", "no answer from the server", "sunucudan yanıt gelmedi", "server javob bermadi"));
                    break;
                }
            }
            PosLogger.Log($"Склад: списано просроченное — товаров {ok} из {picked.Count} отмеченных (просрочено {items.Count}), ошибок {failed.Count}.", "STOCK");
            _ = ProductExpiryIndex.RefreshAsync(true);
            var report = Tr.T($"Списано товаров: {ok} из {picked.Count}.", $"Эсептен чыгарылды: {ok} / {picked.Count}.", $"Written off: {ok} of {picked.Count}.", $"Düşüldü: {ok} / {picked.Count}.", $"Hisobdan chiqarildi: {ok} / {picked.Count}.");
            if (failed.Count > 0)
                PosDialogs.Warning(owner, report + "\n\n" + string.Join("\n", failed.Take(6)));
            else
                PosDialogs.Info(owner, report);
            return ok;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
