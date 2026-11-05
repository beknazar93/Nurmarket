using System.Text;
using Avalonia.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-05 (волна 2-А, функция 1): кнопка «Списать просроченное» в Складе. Не новый способ списания, а цикл по уже существующему:
// каждый просроченный товар списывается тем же путём, что кнопка «Списать» (ProductActions.ChangeStockAsync: свежий остаток с сервера, один акт на товар,
// причина «Просрочка» на русском в примечании акта). Перед этим — одно подтверждение со списком и суммой; право «Склад: редактирование» проверяет окно.

public static class ExpiredWriteOff
{
    private static int _running;

    /// <summary>Списать весь остаток просроченных товаров. Возвращает, сколько товаров списано (0 — ничего или отмена).</summary>
    public static async Task<int> RunAsync(Window? owner)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return 0;
        try
        {
            await ProductExpiryIndex.RefreshAsync(true).ConfigureAwait(true);
            var today = DateTime.Today;
            var items = new List<(ProductExpiryIndex.Mark Mark, double Qty)>();
            foreach (var m in ProductExpiryIndex.Marks.Values.Where(m => ExpiryRules.IsExpired(m, today) && m.Quantity > 0.0005))
            {
                // Остаток в каталоге мог уменьшиться после продаж — не списываем больше, чем есть.
                var tile = ProductActions.Find(m.ProductId);
                var qty = tile is null ? m.Quantity : Math.Min(m.Quantity, tile.Quantity);
                if (qty > 0.0005)
                    items.Add((m, qty));
            }
            if (items.Count == 0)
            {
                PosDialogs.Info(owner, Tr.T("Просроченных товаров с остатком нет — списывать нечего.", "Калдыгы бар мөөнөтү өткөн товар жок — эсептен чыгаруучу эч нерсе жок.",
                    "No expired products with stock — nothing to write off.", "Stokta süresi geçmiş ürün yok — düşülecek bir şey yok.", "Qoldig'i bor muddati o'tgan mahsulot yo'q — hisobdan chiqariladigan narsa yo'q."));
                return 0;
            }

            // 2026-11-05 (ревью волн 0–3, С7): сумма — по тем же количествам, что в списке (min(остаток срока, остаток в каталоге)), а не по полному остатку из справочника.
            var cost = items.Sum(i => i.Qty * Math.Max(0, ProductActions.Find(i.Mark.ProductId)?.PurchasePrice ?? 0));
            var list = new StringBuilder();
            foreach (var (m, qty) in items.Take(12))
                list.AppendLine($"• {m.Name} — {ProductActions.Qty(qty)} ({m.Date:dd.MM})");
            if (items.Count > 12)
                list.AppendLine(Tr.T($"… и ещё {items.Count - 12}", $"… дагы {items.Count - 12}", $"… and {items.Count - 12} more", $"… ve {items.Count - 12} tane daha", $"… yana {items.Count - 12} ta"));
            var question = Tr.T($"Списать весь остаток просроченных товаров: {items.Count} позиц." + (cost > 0.005 ? $" на {MoneyText.Som(cost)} по закупке" : "") + "?\n\n" + list + "\nПричина акта: «Просрочка». Вернуть остаток можно только приходом или ревизией.",
                $"Мөөнөтү өткөн товарлардын калдыгын эсептен чыгарабызбы: {items.Count} позиция" + (cost > 0.005 ? $", сатып алуу баасы боюнча {MoneyText.Som(cost)}" : "") + "?\n\n" + list + "\nАктынын себеби: «Просрочка». Калдыкты кирим же ревизия менен гана кайтарууга болот.",
                $"Write off the whole stock of expired products: {items.Count} items" + (cost > 0.005 ? $", {MoneyText.Som(cost)} at cost" : "") + "?\n\n" + list + "\nReason: “Expired”. Stock can only be restored by a delivery or a stock count.",
                $"Süresi geçmiş ürünlerin tüm stoğu düşülsün mü: {items.Count} kalem" + (cost > 0.005 ? $", maliyet {MoneyText.Som(cost)}" : "") + "?\n\n" + list + "\nNeden: «Son kullanma tarihi geçmiş». Stok yalnızca mal kabulü veya sayımla geri getirilebilir.",
                $"Muddati o'tgan mahsulotlar qoldig'i to'liq hisobdan chiqarilsinmi: {items.Count} ta pozitsiya" + (cost > 0.005 ? $", xarid narxida {MoneyText.Som(cost)}" : "") + "?\n\n" + list + "\nSabab: «Muddati o'tgan». Qoldiqni faqat kirim yoki inventarizatsiya qaytaradi.");
            // Срок хранится один на карточку: если после даты годности пришла свежая партия, а дата не обновлена, спишется и свежий товар.
            question += "\n\n" + Tr.T("Внимание: дата годности одна на всю карточку. Если после неё пришла свежая партия, а дата не обновлена, спишется и свежий товар — проверьте список.",
                "Көңүл буруңуз: жарактуулук мөөнөтү бүтүндөй карточкага бир. Андан кийин жаңы партия келип, күнү жаңыланбаса, жаңы товар да эсептен чыгат — тизмени текшериңиз.",
                "Note: there is one expiry date per card. If a fresh batch arrived after it and the date was not updated, the fresh stock will be written off too — check the list.",
                "Dikkat: kart başına tek son kullanma tarihi var. Sonrasında taze parti geldiyse ve tarih güncellenmediyse taze mal da düşülür — listeyi kontrol edin.",
                "Diqqat: yaroqlilik muddati butun kartochkaga bitta. Undan keyin yangi partiya kelgan va sana yangilanmagan bo'lsa, yangi tovar ham hisobdan chiqadi — ro'yxatni tekshiring.");
            if (!await PosDialogs.ConfirmYesNoModalAsync(owner, question, Tr.T("Списать просроченное", "Мөөнөтү өткөндү эсептен чыгаруу", "Write off expired", "Süresi geçmişi düş", "Muddati o'tganni hisobdan chiqarish"),
                    confirmText: Tr.T("Списать", "Эсептен чыгаруу", "Write off", "Düş", "Hisobdan chiqarish"), danger: true).ConfigureAwait(true))
                return 0;

            var ok = 0;
            var failed = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            foreach (var (m, qty) in items)
            {
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
            PosLogger.Log($"Склад: списано просроченное — товаров {ok} из {items.Count}, ошибок {failed.Count}.", "STOCK");
            _ = ProductExpiryIndex.RefreshAsync(true);
            var report = Tr.T($"Списано товаров: {ok} из {items.Count}.", $"Эсептен чыгарылды: {ok} / {items.Count}.", $"Written off: {ok} of {items.Count}.", $"Düşüldü: {ok} / {items.Count}.", $"Hisobdan chiqarildi: {ok} / {items.Count}.");
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
