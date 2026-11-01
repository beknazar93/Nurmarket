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
/// отличается — строка получает актуальную цену, плашка и запись PRICE_SYNC в журнале.
/// 2026-11-01 (аудит 10 из 10, Ж-02): окно оплаты ждёт сверку не дольше ~0,3 с, запросы идут в фоне (все товары корзины), цены, сверенные
/// менее 45 с назад, заново не запрашиваются. Нет связи или сервер не ответил за 2 секунды —
/// оплата идёт как раньше (оффлайн не блокируем), в журнале «PRICE_SYNC skipped».
/// Тело оплаты (PosCheckout*, QuickCheckoutBody) не менялось: касса и дальше шлёт цену строки, но уже свежую.
/// </summary>
public static class CartPriceSync
{
    /// <summary>Не больше стольких товаров за раз (остальные идут по цене чека — журнал скажет, сколько).
    /// 2026-11-01 (аудит 10 из 10, Ж-02): было 20 — 10 из 30 товаров корзины не сверялись вовсе. Теперь сверяются все (запросы идут в фоне).</summary>
    public const int MaxProducts = 200;
    public const int Parallelism = 8;
    /// <summary>Сколько окно оплаты ждёт сверку. 2026-11-01 (Ж-02): было 2 с — «Оплатить» с 30 товарами открывалось 1,2–2 (до 6) секунд.
    /// Теперь ~0,3 с: что успело прийти (и что сверено недавно) — применяется, остальные запросы доходят в фоне и ложатся в кэш;
    /// если цена всё же устарела, сервер ответит 409 price_changed и чек обновится (это уже есть в оплате).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
    /// <summary>Приёмка Ж-02: после отказа сервера 409 price_changed ждём полный срок (как раньше, 2 с) — окно оплаты уже не открывается, цены нужны свежие.</summary>
    public static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(2);
    /// <summary>Общий срок жизни фоновых запросов сверки (с момента нажатия «Оплатить»).</summary>
    public static readonly TimeSpan BackgroundBudget = TimeSpan.FromSeconds(8);
    /// <summary>Цена товара, сверенная с сервером не дольше этого срока назад, заново не спрашивается (повторное «Оплатить» / «Отмена» в окне оплаты).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(45);

    // 2026-11-01 (Ж-02): кэш сверенных цен (ключ: аккаунт + компания + id товара → цена сервера, время) и запросы «в пути» (повторное нажатие не плодит дубли).
    // Код-ревью: ключ включает аккаунт/компанию — смена аккаунта в течение 45 с не берёт чужие цены.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double Price, long Tick)> FreshPrices = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<double?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim FetchGate = new(Parallelism, Parallelism);
    /// <summary>Поколение кэша: Invalidate() увеличивает его, и запросы, начатые раньше, уже не пишут свои (возможно устаревшие) цены в кэш.</summary>
    private static long _generation;

    private static string CacheKey(string id) =>
        (UserPreferences.Instance.LastDataAccountKey ?? "") + "|" + (CompanyInfoService.LastCompany?.Id ?? "") + "#" + id;

    /// <summary>Забыть сверенные цены (после отказа сервера 409 price_changed следующая сверка должна спросить сервер заново).</summary>
    public static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        FreshPrices.Clear();
        InFlight.Clear();
    }

    private static bool TryFresh(string id, out double price)
    {
        price = 0;
        var key = CacheKey(id);
        if (!FreshPrices.TryGetValue(key, out var entry))
            return false;
        var age = TimeSpan.FromMilliseconds(Environment.TickCount64 - entry.Tick);
        if (age < TimeSpan.Zero || age > FreshFor)
        {
            FreshPrices.TryRemove(key, out _);
            return false;
        }
        price = entry.Price;
        return true;
    }

    /// <summary>Запрос карточки товара в фоне: свой токен отмены (живёт, пока идёт запрос), поэтому «срок вышел» у окна оплаты не рвёт запрос и не даёт
    /// ObjectDisposedException/TaskCanceledException в журнале. Результат кладётся в кэш свежих цен. null — карточка не пришла.</summary>
    private static Task<double?> FetchInBackground(string id, ProductFetcher fetch, TimeSpan lifetime)
    {
        var cacheKey = CacheKey(id);
        // Запрос по этому товару уже идёт (повторное «Оплатить») — ждём его, а не шлём второй.
        if (InFlight.TryGetValue(cacheKey, out var running) && !running.IsCompleted)
            return running;
        var generation = Interlocked.Read(ref _generation);
        var started = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(lifetime);
            try
            {
                await FetchGate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (await fetch(id, cts.Token).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object } card)
                    {
                        // Акции товара из той же карточки (если сервер их отдал) — чтобы скидка считалась по свежим правилам.
                        PromotionRules.Remember(id, card);
                        if (ProductCatalogMapper.TryPrice(card) is { } price && double.IsFinite(price) && price > 0)
                        {
                            // Кэш сброшен, пока запрос шёл (409 price_changed, смена аккаунта) — эту цену в кэш не кладём.
                            if (generation == Interlocked.Read(ref _generation))
                                FreshPrices[cacheKey] = (price, Environment.TickCount64);
                            return (double?)price;
                        }
                    }
                }
                finally
                {
                    FetchGate.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // срок фонового запроса вышел — не ошибка, в журнал не пишем
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"PRICE_SYNC: товар {id} не прочитан ({ex.GetType().Name}): {ex.Message}", "PAYMENT");
            }
            return null;
        });
        InFlight[cacheKey] = started;
        return started;
    }

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
    /// <param name="force">2026-11-01 (Ж-02): true — не брать цены из кэша «сверено недавно», спросить сервер заново (после отказа сервера 409 price_changed).</param>
    public static async Task<Outcome> RefreshAsync(
        ICartService cart, ProductFetcher? fetch, bool offline, TimeSpan? budget = null, CancellationToken ct = default,
        ApplyOnCartThread? applyOnCartThread = null, bool force = false)
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
            var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // 2026-11-01 (аудит 10 из 10, Ж-02): цены, сверенные недавно (FreshFor), заново не спрашиваем; остальные запрашиваются в фоне
            // (свой токен отмены у каждого запроса, поэтому без TaskCanceled/ObjectDisposed в журнале). Окно оплаты ждёт не дольше limit (~0,3 с):
            // что успело — применяется сразу, остальное ложится в кэш и подхватится следующим нажатием; устаревшую цену поймает ответ сервера 409.
            var pending = new List<(string Id, Task<double?> Task)>();
            if (force)
                Invalidate(); // код-ревью: после 409 забываем кэш и идущие запросы — берём только новые ответы сервера
            var fromCache = 0;
            foreach (var id in ids)
            {
                if (!force && TryFresh(id, out var cachedPrice))
                {
                    prices[id] = cachedPrice;
                    fromCache++;
                }
                else
                    pending.Add((id, FetchInBackground(id, fetch, limit > BackgroundBudget ? limit : BackgroundBudget)));
            }

            var finished = true;
            if (pending.Count > 0)
            {
                // Клиент API мог не послушаться отмены — ждём не дольше срока, что успело прийти, то и берём.
                var all = Task.WhenAll(pending.Select(p => p.Task));
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                finished = await Task.WhenAny(all, Task.Delay(limit, delayCts.Token)).ConfigureAwait(false) == all;
                delayCts.Cancel();
                ct.ThrowIfCancellationRequested();
                foreach (var (id, task) in pending)
                    if (task.IsCompletedSuccessfully && task.Result is { } fetched)
                        prices[id] = fetched;
            }

            var got = prices;
            PromotionRules.SaveIfDirty();

            if (got.Count == 0)
            {
                var why = finished ? "сервер не вернул цен" : $"ответы не пришли за {limit.TotalSeconds:0.##} с — сверка продолжается в фоне, цены лягут в кэш";
                return Skip(why);
            }

            var changes = applyOnCartThread is null
                ? ReceiptSnapshotCartEditor.ApplyCatalogPrices(cart, got)
                : await applyOnCartThread(() => ReceiptSnapshotCartEditor.ApplyCatalogPrices(cart, got)).ConfigureAwait(false);
            foreach (var c in changes)
                PosLogger.Log($"PRICE_SYNC: «{c.Name}» ({c.ProductId}) {Fmt(c.OldPrice)} → {Fmt(c.NewPrice)} — цена строки чека обновлена до оплаты{(c.DiscountRecalculated ? "; скидка пересчитана в тот же процент" : "")}.", "PAYMENT");
            PosLogger.Log(
                $"PRICE_SYNC: сверено {got.Count} из {ids.Count + skippedExtra} товаров за {timer.ElapsedMilliseconds} мс, изменено строк: {changes.Count}"
                + (fromCache > 0 ? $", из кэша (сверено менее {FreshFor.TotalSeconds:0} с назад): {fromCache}" : "")
                + (skippedExtra > 0 ? $", не сверено (лимит {MaxProducts}): {skippedExtra}" : "")
                + (!finished ? ", остальные ответы доходят в фоне" : "") + ".",
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
