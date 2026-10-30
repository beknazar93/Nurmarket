using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-27, жалоба владельца «на складе товар стоит 195, а касса продаёт за 205»: касса сама шлёт серверу цену строки
/// (QuickCheckoutBody: price, StagingCartService: unit_price), а сервер её принимает — значит, устаревшая цена в чеке и есть цена
/// продажи. Каталог кассы отстаёт от склада (версия каталога не меняется при смене цены), строка чека держит цену с момента
/// добавления. Перед оплатой цены обычных строк сверяются с сервером (GET товара, до 4 запросов сразу, всего не больше 2 секунд);
/// отличается — строка получает актуальную цену, плашка и запись PRICE_SYNC в журнале. Нет связи или сервер не ответил за 2 секунды —
/// оплата идёт как раньше (оффлайн не блокируем), в журнале «PRICE_SYNC skipped».
/// Тело оплаты (PosCheckout*, QuickCheckoutBody) не менялось: касса и дальше шлёт цену строки, но уже свежую.
/// </summary>
public static class CartPriceSync
{
    /// <summary>Не больше стольких товаров за раз (остальные идут по цене чека — журнал скажет, сколько).</summary>
    public const int MaxProducts = 20;
    public const int Parallelism = 4;
    /// <summary>Общий срок на все запросы: дольше оплату не задерживаем.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    public sealed record Outcome(
        IReadOnlyList<ReceiptSnapshotCartEditor.LinePriceChange> Changes,
        bool Skipped,
        string Note);

    /// <summary>Достать карточку товара (null — не нашлась / не ответила). В кассе — <c>PosApp.CatalogApi.ProductsDetailAsync</c>.</summary>
    public delegate Task<JsonElement?> ProductFetcher(string productId, CancellationToken ct);

    /// <summary>2026-10-27 (ревью денег, К-6): применить цены в чеке на потоке чека (UI). Получает действие, возвращает его результат (пусто — чек не менялся).</summary>
    public delegate Task<List<ReceiptSnapshotCartEditor.LinePriceChange>> ApplyOnCartThread(Func<List<ReceiptSnapshotCartEditor.LinePriceChange>> apply);

    /// <param name="offline">Авария сервера или офлайн-режим: сервер не спрашиваем (оплата идёт локально).</param>
    /// <param name="applyOnCartThread">2026-10-27 (К-6): раньше цены писались в чек с фонового потока (после ответа сервера) — правка кассира в тот же момент
    /// (удалил строку, «+») терялась: оба читали и писали JSON чека без блокировки. Теперь запись — на потоке чека и только если чек тот же.
    /// null — прежнее прямое применение (стенды).</param>
    public static async Task<Outcome> RefreshAsync(
        ICartService cart, ProductFetcher? fetch, bool offline, TimeSpan? budget = null, CancellationToken ct = default,
        ApplyOnCartThread? applyOnCartThread = null)
    {
        try
        {
            if (fetch is null)
                return Skip("нет связи с каталогом");
            if (offline)
                return Skip("офлайн или авария сервера");
            // 2026-10-29 (приёмка 1.17.65, В-2): чек, начатый без связи (IsLocalOffline), тоже сверяем — после возврата связи его цены из локального
            // каталога так же могли устареть. Раньше такой чек пропускался, сервер отвечал 409 price_changed, и кассир упирался в тупик.
            if (!cart.HasCart || (!cart.IsLocalOffline && !cart.IsStaging))
                return Skip("чек не локальный (цены поставил сервер)");

            var ids = ReceiptSnapshotCartEditor.PriceSyncProductIds(cart);
            if (ids.Count == 0)
                return new Outcome(Array.Empty<ReceiptSnapshotCartEditor.LinePriceChange>(), false, "нет строк для сверки");
            var skippedExtra = Math.Max(0, ids.Count - MaxProducts);
            if (skippedExtra > 0)
                ids = ids.Take(MaxProducts).ToList();

            var limit = budget ?? Budget;
            var timer = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var answered = 0;
            var gate = new SemaphoreSlim(Parallelism, Parallelism);

            var tasks = ids.Select(async id =>
            {
                try
                {
                    await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (await fetch(id, cts.Token).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object } card)
                    {
                        // Акции товара из той же карточки (если сервер их отдал) — чтобы скидка считалась по свежим правилам.
                        PromotionRules.Remember(id, card);
                        if (ProductCatalogMapper.TryPrice(card) is { } price && double.IsFinite(price) && price > 0)
                        {
                            lock (prices)
                                prices[id] = price;
                        }
                        Interlocked.Increment(ref answered);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"PRICE_SYNC: товар {id} не прочитан ({ex.GetType().Name}): {ex.Message}", "PAYMENT");
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            // Клиент API мог не послушаться отмены — ждём не дольше срока, что успело прийти, то и берём.
            var all = Task.WhenAll(tasks);
            var finished = await Task.WhenAny(all, Task.Delay(limit, ct)).ConfigureAwait(false) == all;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();

            Dictionary<string, double> got;
            lock (prices)
                got = new Dictionary<string, double>(prices, StringComparer.OrdinalIgnoreCase);
            PromotionRules.SaveIfDirty();

            if (got.Count == 0)
            {
                var why = finished ? "сервер не вернул цен" : $"сервер не ответил за {limit.TotalSeconds:0.#} с";
                return Skip(why);
            }

            var changes = applyOnCartThread is null
                ? ReceiptSnapshotCartEditor.ApplyCatalogPrices(cart, got)
                : await applyOnCartThread(() => ReceiptSnapshotCartEditor.ApplyCatalogPrices(cart, got)).ConfigureAwait(false);
            foreach (var c in changes)
                PosLogger.Log($"PRICE_SYNC: «{c.Name}» ({c.ProductId}) {Fmt(c.OldPrice)} → {Fmt(c.NewPrice)} — цена строки чека обновлена до оплаты{(c.DiscountRecalculated ? "; скидка пересчитана в тот же процент" : "")}.", "PAYMENT");
            PosLogger.Log(
                $"PRICE_SYNC: сверено {got.Count} из {ids.Count + skippedExtra} товаров за {timer.ElapsedMilliseconds} мс, изменено строк: {changes.Count}"
                + (skippedExtra > 0 ? $", не сверено (лимит {MaxProducts}): {skippedExtra}" : "")
                + (!finished ? ", часть ответов не успела" : "") + ".",
                "PAYMENT");
            return new Outcome(changes, false, "ok");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Skip("отменено по сроку");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сверка цен — помощь кассиру, а не условие оплаты: любая ошибка = оплата как раньше.
            PosLogger.Log($"PRICE_SYNC skipped: ошибка {ex.GetType().Name}: {ex.Message}", "PAYMENT");
            return new Outcome(Array.Empty<ReceiptSnapshotCartEditor.LinePriceChange>(), true, ex.Message);
        }
    }

    private static Outcome Skip(string why)
    {
        PosLogger.Log($"PRICE_SYNC skipped: {why}.", "PAYMENT");
        return new Outcome(Array.Empty<ReceiptSnapshotCartEditor.LinePriceChange>(), true, why);
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Текст плашки: «Цена изменилась: Хлеб 205 → 195 сом. Чек обновлён.» (до трёх товаров, остальные — «и ещё N»).</summary>
    public static string ToastText(IReadOnlyList<ReceiptSnapshotCartEditor.LinePriceChange> changes)
    {
        if (changes.Count == 0)
            return "";
        var shown = string.Join("; ", changes.Take(3).Select(c => $"{c.Name} {Fmt(c.OldPrice)} → {Fmt(c.NewPrice)}"));
        var more = changes.Count > 3 ? changes.Count - 3 : 0;
        // 2026-10-27: скидка суммой пересчитана в тот же процент — говорим об этом кассиру.
        var disc = changes.Any(c => c.DiscountRecalculated);
        return Tr.T(
            $"Цена изменилась: {shown}{(more > 0 ? $" и ещё {more}" : "")} сом. Чек обновлён.{(disc ? " Скидка пересчитана." : "")}",
            $"Баасы өзгөрдү: {shown}{(more > 0 ? $" жана дагы {more}" : "")} сом. Чек жаңыртылды.{(disc ? " Арзандатуу кайра эсептелди." : "")}",
            $"Price changed: {shown}{(more > 0 ? $" and {more} more" : "")} som. Receipt updated.{(disc ? " Discount recalculated." : "")}",
            $"Fiyat değişti: {shown}{(more > 0 ? $" ve {more} tane daha" : "")} som. Fiş güncellendi.{(disc ? " İndirim yeniden hesaplandı." : "")}",
            $"Narx o'zgardi: {shown}{(more > 0 ? $" va yana {more} ta" : "")} so'm. Chek yangilandi.{(disc ? " Chegirma qayta hisoblandi." : "")}");
    }
}
