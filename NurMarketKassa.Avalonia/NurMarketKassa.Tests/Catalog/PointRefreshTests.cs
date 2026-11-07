using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-07, жалоба владельца «при каждом изменении товара весь склад загружается заново» (каталог 6152 товара, «Сверка с сервером: загружено 3 800 из 6 152»).
/// После правки/создания/цены ОДНОГО товара каталог не перекачивается: читается карточка только этого товара и применяется к базе и памяти. Тесты закрепляют:
/// (1) точечное обновление меняет только нужный товар (в базе и в кэше плиток) и спрашивает у сервера только его;
/// (2) не прочитали карточку (нет связи, 429/5xx, сервер вернул ошибку) — тихо, товар как был, исключения нет;
/// (3) путь окна <see cref="CatalogCacheService.RefreshProductsAsync"/> идёт в хост-обработчик точечного обновления и НИКОГДА не зовёт полный общий путь (SharedRefresh) —
///     ни при удаче, ни при провале, ни при исключении (нездоровая лента, пауза 429, NeedFullSync — это решения полного пути, до них дело не доходит);
/// (4) защита остатка не мешает цене, архивный товар из карточки уходит из каталога кассы;
/// (5) лента, принёсшая «неизвестный» товар без карточки, называет его код — карточку дочитывают по id, полная загрузка не нужна.
/// Настоящая база — во временной папке test-safe.ps1.</summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class PointRefreshTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public PointRefreshTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        NewProductCardBackfill.AutoRun = false;
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
        CardProxy.Reset();
    }

    public void Dispose()
    {
        NewProductCardBackfill.AutoRun = true;
        NewProductCardBackfill.ResetForTests();
        CardProxy.Reset();
        _repo.ClearAll();
    }

    /// <summary>Заглушка ICatalogApiService: ProductsDetailAsync отдаёт карточки из словаря (нет записи — null, как 404/5xx/429 у настоящего); считает вызовы.</summary>
    public class CardProxy : DispatchProxy
    {
        public static readonly Dictionary<string, string> Cards = new(StringComparer.OrdinalIgnoreCase);
        public static readonly List<string> Asked = [];
        public static Exception? Throw;

        public static void Reset()
        {
            Cards.Clear();
            Asked.Clear();
            Throw = null;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == nameof(ICatalogApiService.ProductsDetailAsync))
            {
                var id = (string)args![0]!;
                Asked.Add(id);
                if (Throw is { } boom)
                    return Task.FromException<JsonElement?>(boom);
                JsonElement? card = Cards.TryGetValue(id, out var json) ? JsonDocument.Parse(json).RootElement.Clone() : null;
                return Task.FromResult(card);
            }

            var rt = targetMethod.ReturnType;
            if (rt == typeof(Task))
                return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }

            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    private static ICatalogApiService Api() => DispatchProxy.Create<ICatalogApiService, CardProxy>();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage Page(params string[] cards) => new() { Status = CatalogFeedStatus.Ok, Items = cards.Select(J).ToList(), NextCursor = "c" };

    private void Seed()
    {
        CatalogFeedSync.Apply(Page(
            """{"id":"p-1","name":"Яблоки","price":"50.00","purchase_price":"30.00","quantity":"10","unit":"шт","kind":"product"}""",
            """{"id":"p-2","name":"Груши","price":"70.00","purchase_price":"40.00","quantity":"5","unit":"шт","kind":"product"}""",
            """{"id":"p-3","name":"Сливы","price":"90.00","purchase_price":"50.00","quantity":"7","unit":"шт","kind":"product"}"""), ApiBase, allowStock: true);
    }

    [Fact]
    public async Task One_edited_product_is_refreshed_alone_in_the_base_and_in_the_tile_cache()
    {
        Seed();
        _ = _repo.LoadAllTiles(); // кэш плиток собран, как в работающей кассе
        CardProxy.Cards["p-2"] = """{"id":"p-2","name":"Груши","price":"99.00","purchase_price":"40.00","quantity":"5","unit":"шт","kind":"product","markup_percent":"147.5"}""";

        var outcome = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["p-2"], ApiBase);

        CardProxy.Asked.Should().Equal("p-2");
        outcome.Fetched.Should().Be(1);
        outcome.Changed.Should().Be(1);
        outcome.NotFetched.Should().BeEmpty();
        _repo.LoadTileCopy("p-2")!.PriceValue.Should().Be(99, "цена из карточки сервера");
        _repo.LoadTileCopy("p-1")!.PriceValue.Should().Be(50, "соседние товары не тронуты");
        _repo.LoadAllTiles().Single(t => t.Id == "p-2").PriceValue.Should().Be(99, "в кэше плиток тоже новая цена (CATALOG cache patched)");
        _repo.LoadAllTiles().Should().HaveCount(3);
    }

    [Fact]
    public async Task Failed_detail_request_leaves_the_product_as_it_was_without_throwing()
    {
        Seed();
        // Сервер не ответил (null — как 404/429/5xx у настоящего ProductsDetailAsync).
        var outcome = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["p-1"], ApiBase);

        outcome.Fetched.Should().Be(0);
        outcome.NotFetched.Should().Equal("p-1");
        outcome.Applied.Should().BeFalse();
        _repo.LoadTileCopy("p-1")!.PriceValue.Should().Be(50);

        // Сеть оборвалась исключением — тоже тихо.
        CardProxy.Throw = new HttpRequestException("нет сети");
        var thrown = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["p-1"], ApiBase);
        thrown.NotFetched.Should().Equal("p-1");
        _repo.LoadAllTiles().Should().HaveCount(3, "каталог не потерял ни одного товара");
    }

    [Fact]
    public async Task Stock_guard_keeps_the_local_quantity_but_the_new_price_is_applied()
    {
        Seed();
        CardProxy.Cards["p-1"] = """{"id":"p-1","name":"Яблоки","price":"60.00","purchase_price":"30.00","quantity":"13","unit":"шт","kind":"product"}""";

        await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["p-1"], ApiBase, stockGuard: _ => false);

        var tile = _repo.LoadTileCopy("p-1")!;
        tile.PriceValue.Should().Be(60);
        tile.Quantity.Should().Be(10, "остаток держит локальная активность (своя продажа не откатывается)");
    }

    [Fact]
    public async Task Archived_card_removes_the_product_from_the_kassa_catalog()
    {
        Seed();
        CardProxy.Cards["p-3"] = """{"id":"p-3","name":"Сливы","price":"90.00","is_archived":true}""";

        var outcome = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["p-3"], ApiBase);

        outcome.Deleted.Should().Be(1);
        _repo.LoadTileCopy("p-3").Should().BeNull();
        _repo.LoadTileCopy("p-1").Should().NotBeNull();
    }

    [Fact]
    public async Task New_product_is_added_by_its_card_and_duplicates_in_the_request_are_asked_once()
    {
        Seed();
        CardProxy.Cards["n-1"] = """{"id":"n-1","name":"Новый товар","price":"60.00","purchase_price":"40.00","quantity":"3","unit":"шт","kind":"product"}""";

        var outcome = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ["n-1", "N-1", " n-1 ", null, ""], ApiBase);

        CardProxy.Asked.Should().Equal("n-1");
        outcome.Added.Should().Be(1);
        _repo.LoadTileCopy("n-1")!.Title.Should().Be("Новый товар");
        _repo.LoadAllTiles().Should().HaveCount(4);
    }

    [Fact]
    public async Task More_than_the_limit_is_not_read_here_those_stay_for_the_feed()
    {
        var ids = Enumerable.Range(0, CatalogPointRefresh.MaxIds + 5).Select(i => $"m-{i}").ToList();
        foreach (var id in ids)
            CardProxy.Cards[id] = $$"""{"id":"{{id}}","name":"Товар {{id}}","price":"10.00","quantity":"1","unit":"шт","kind":"product"}""";

        var outcome = await CatalogPointRefresh.ApplyByIdsAsync(Api(), ids, ApiBase);

        outcome.Fetched.Should().Be(CatalogPointRefresh.MaxIds);
        outcome.NotFetched.Should().HaveCount(5, "«точечно» — это не весь каталог: лишнее остаётся ленте");
    }

    // ----------------------------------------------------------------- путь окна: полной загрузки нет НИКОГДА

    private sealed class Hooks : IDisposable
    {
        private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CatalogSyncResult>>? _prevPoint = CatalogCacheService.SharedRefreshProducts;
        private readonly Func<CancellationToken, Task<CatalogSyncResult>>? _prevFull = CatalogCacheService.SharedRefresh;
        public int FullCalls;
        public int BackgroundFullCalls;
        private readonly Func<Task<CatalogSyncResult>?>? _prevBackground = CatalogCacheService.RequestBackgroundFullReload;
        public readonly List<string> PointIds = [];

        public Hooks(Func<IReadOnlyList<string>, CancellationToken, Task<CatalogSyncResult>> point)
        {
            CatalogCacheService.SharedRefreshProducts = (ids, ct) =>
            {
                PointIds.AddRange(ids);
                return point(ids, ct);
            };
            // Полный общий путь окон (лента → если не помогает, StartFullSync) и фоновая сверка: если тест их заденет — это провал.
            CatalogCacheService.SharedRefresh = _ =>
            {
                Interlocked.Increment(ref FullCalls);
                return Task.FromResult(CatalogSyncResult.Ok(0, 0, 0));
            };
            CatalogCacheService.RequestBackgroundFullReload = () =>
            {
                Interlocked.Increment(ref BackgroundFullCalls);
                return null;
            };
        }

        public void Dispose()
        {
            CatalogCacheService.SharedRefreshProducts = _prevPoint;
            CatalogCacheService.SharedRefresh = _prevFull;
            CatalogCacheService.RequestBackgroundFullReload = _prevBackground;
        }
    }

    [Fact]
    public async Task Window_refresh_after_an_edit_goes_to_the_point_hook_and_never_to_the_full_path()
    {
        using var hooks = new Hooks((ids, _) => Task.FromResult(CatalogSyncResult.Ok(0, ids.Count, 0)));

        var result = await CatalogCacheService.RefreshProductsAsync(["p-2"]);

        result.Success.Should().BeTrue();
        hooks.PointIds.Should().Equal("p-2");
        hooks.FullCalls.Should().Be(0, "после правки одного товара полная загрузка не запускается");
        hooks.BackgroundFullCalls.Should().Be(0);
    }

    [Fact]
    public async Task Failed_point_refresh_does_not_fall_back_to_the_full_load()
    {
        // Лента нездорова / пауза 429 / NeedFullSync — всё это решения ПОЛНОГО пути; точечный провал (сеть, 429) молча оставляет товар как есть.
        using var hooks = new Hooks((_, _) => Task.FromResult(CatalogSyncResult.Failed("сервер просит паузу (429)")));

        var result = await CatalogCacheService.RefreshProductsAsync(["p-2"]);

        result.Success.Should().BeFalse();
        hooks.FullCalls.Should().Be(0);
        hooks.BackgroundFullCalls.Should().Be(0);
    }

    [Fact]
    public async Task Point_hook_crash_is_swallowed_and_the_full_load_is_not_started()
    {
        using var hooks = new Hooks((_, _) => throw new InvalidOperationException("сбой хоста"));

        var result = await CatalogCacheService.RefreshProductsAsync(["p-2"]);

        result.Success.Should().BeFalse();
        hooks.FullCalls.Should().Be(0);
    }

    [Fact]
    public async Task Empty_or_blank_ids_do_nothing()
    {
        using var hooks = new Hooks((_, _) => Task.FromResult(CatalogSyncResult.Ok(0, 0, 0)));

        (await CatalogCacheService.RefreshProductsAsync([null, " ", ""])).Success.Should().BeTrue();

        hooks.PointIds.Should().BeEmpty("нечего обновлять — ни точечно, ни целиком");
        hooks.FullCalls.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_host_hook_the_window_still_updates_just_that_product()
    {
        // Стенд / хост без AvaloniaCatalogCacheService: тот же точечный путь напрямую (PosApp.CatalogApi), без полной загрузки.
        Seed();
        CardProxy.Cards["p-2"] = """{"id":"p-2","name":"Груши","price":"88.00","purchase_price":"40.00","quantity":"5","unit":"шт","kind":"product"}""";
        var prevApi = PosApp.CatalogApi;
        var prevPoint = CatalogCacheService.SharedRefreshProducts;
        var prevFull = CatalogCacheService.SharedRefresh;
        var fullCalls = 0;
        PosApp.CatalogApi = Api();
        CatalogCacheService.SharedRefreshProducts = null;
        CatalogCacheService.SharedRefresh = _ =>
        {
            fullCalls++;
            return Task.FromResult(CatalogSyncResult.Ok(0, 0, 0));
        };
        try
        {
            var result = await CatalogCacheService.RefreshProductsAsync(["p-2"]);

            result.Success.Should().BeTrue();
            _repo.LoadTileCopy("p-2")!.PriceValue.Should().Be(88);
            fullCalls.Should().Be(0);
            CardProxy.Asked.Should().Equal("p-2");
        }
        finally
        {
            PosApp.CatalogApi = prevApi;
            CatalogCacheService.SharedRefreshProducts = prevPoint;
            CatalogCacheService.SharedRefresh = prevFull;
        }
    }

    // ----------------------------------------------------------------- лента: неизвестный товар называется по коду

    [Fact]
    public async Task Feed_names_the_unknown_product_and_its_card_is_read_by_id_instead_of_a_full_load()
    {
        Seed();
        // Лента принесла короткую запись без названия для товара, которого нет в базе кассы (раньше это всегда была полная загрузка каталога).
        var outcome = CatalogFeedSync.Apply(Page("""{"id":"x-404","price":"60.00","quantity":"3"}"""), ApiBase, allowStock: true);
        outcome.NeedFullSync.Should().BeTrue();
        outcome.MissingIds.Should().Equal("x-404");

        CardProxy.Cards["x-404"] = """{"id":"x-404","name":"Пришёл не из кассы","price":"60.00","purchase_price":"40.00","quantity":"3","unit":"шт","kind":"product"}""";
        var fetched = await CatalogPointRefresh.ApplyByIdsAsync(Api(), outcome.MissingIds!, ApiBase);

        fetched.NotFetched.Should().BeEmpty("все неизвестные прочитаны — полная загрузка не нужна");
        fetched.Added.Should().Be(1);
        _repo.LoadTileCopy("x-404")!.Title.Should().Be("Пришёл не из кассы");
        _repo.LoadAllTiles().Should().HaveCount(4);
    }

    [Fact]
    public void Feed_apply_without_unknown_products_names_nobody()
    {
        Seed();

        var outcome = CatalogFeedSync.Apply(Page("""{"id":"p-1","price":"61.00","quantity":"9"}"""), ApiBase, allowStock: true);

        outcome.NeedFullSync.Should().BeFalse();
        outcome.MissingIds.Should().BeNull();
    }
}
