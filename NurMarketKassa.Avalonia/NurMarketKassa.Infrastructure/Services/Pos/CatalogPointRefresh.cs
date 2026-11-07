using System.Diagnostics;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-07, жалоба владельца «при каждом изменении товара весь склад загружается заново» (каталог 6152 товара, «Сверка с сервером: загружено 3 800 из 6 152»).
/// Точечное обновление: после правки, создания, цены или остатка ОДНОГО (или нескольких известных) товара читаем с сервера только карточки этих товаров
/// (GET products/{id}) и применяем к базе и к памяти теми же правилами, что страницу ленты (<see cref="CatalogFeedSync.Apply"/>): защита остатка FeedStockGuard,
/// «CATALOG cache patched» без пересборки всего каталога, цена продажи — как её отдал сервер. Полную загрузку этот путь НЕ запускает никогда: карточку не прочитали
/// (сеть, 429, 5xx) — товар остаётся как был, правка уже на сервере, её донесёт лента изменений.
/// Тот же путь используется лентой для «неизвестных» товаров (короткая запись без названия, а в базе такого нет): достаточно прочитать эти карточки по id.
/// </summary>
public static class CatalogPointRefresh
{
    /// <summary>Не больше стольких карточек за один вызов; больше — это уже не «точечно» (вызывающий решает сам: лента, а в крайнем случае полная загрузка).</summary>
    public const int MaxIds = 25;

    /// <summary>Общий срок чтения карточек: сервер медлит — остальные не ждём (они остаются на ленте).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <param name="Requested">Сколько разных товаров просили.</param>
    /// <param name="Fetched">Сколько карточек пришло от сервера.</param>
    /// <param name="NotFetched">Код товаров, карточку которых не прочитали (нет связи, 429/5xx, 404, вышел срок, больше <see cref="MaxIds"/>).</param>
    public sealed record Outcome(int Requested, int Fetched, int Added, int Changed, int Deleted, IReadOnlyList<string> NotFetched)
    {
        public bool Applied => Added + Changed + Deleted > 0;
        public static Outcome Empty { get; } = new(0, 0, 0, 0, 0, []);
    }

    /// <summary>Читает карточки <paramref name="ids"/> по одной и применяет к базе кассы. Не бросает (кроме отмены): любая ошибка чтения = товар в <see cref="Outcome.NotFetched"/>.</summary>
    /// <param name="stockGuard">Решение по остатку товара (FeedStockGuard.Create()); null — остаток применяется всегда.</param>
    public static async Task<Outcome> ApplyByIdsAsync(ICatalogApiService api, IEnumerable<string?> ids, string apiBaseUrl, Func<string, bool>? stockGuard = null, CancellationToken ct = default)
    {
        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
            return Outcome.Empty;

        var notFetched = new List<string>();
        var cards = new List<JsonElement>(wanted.Count);
        var watch = Stopwatch.StartNew();
        foreach (var id in wanted)
        {
            if (cards.Count + notFetched.Count >= MaxIds || watch.Elapsed > Budget)
            {
                notFetched.Add(id);
                continue;
            }

            try
            {
                if (await api.ProductsDetailAsync(id, ct).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object } card)
                    cards.Add(card);
                else
                    notFetched.Add(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"CATALOG: точечное обновление — карточка {id} не прочитана ({ex.GetType().Name}: {ex.Message}); товар останется как был, донесёт лента.", "CATALOG");
                notFetched.Add(id);
            }
        }

        if (cards.Count == 0)
            return new Outcome(wanted.Count, 0, 0, 0, 0, notFetched);

        var page = new CatalogFeedPage { Status = CatalogFeedStatus.Ok, Items = cards, Note = $"точечно {cards.Count}" };
        var applied = CatalogFeedSync.Apply(page, apiBaseUrl, allowStock: true, stockGuard);
        PosLogger.Log($"CATALOG: точечное обновление — прочитано {cards.Count} из {wanted.Count} карточек за {watch.ElapsedMilliseconds} мс; в базе добавлено {applied.Added}, изменено {applied.Changed}, удалено {applied.Deleted}; весь каталог не качаем.", "CATALOG");
        return new Outcome(wanted.Count, cards.Count, applied.Added, applied.Changed, applied.Deleted, notFetched);
    }
}
