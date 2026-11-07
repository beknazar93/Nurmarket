using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-07 (живой тест перед 1.17.81, Б-П2): приход у владельца → касса. В 1 приходе из 7 касса потеряла изменение существующего товара (показывала 15 и 7 шт., на сервере 60 и 8):
/// курсор ленты ушёл на 11 мс дальше времени изменения товара, а запись изменения зафиксировалась позже — товар не попал ни в тот ответ, ни в следующие. Теперь лента читается «с курсора минус запас»,
/// а раз в 2–3 минуты (и по кнопке «Обновить») сверяются последние 100 изменённых товаров. Сервер — запись в памяти, база — временная папка test-safe.ps1.</summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class CatalogFeedLookbackTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), "nurmarket-feed-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _oldStorePath = CatalogFeedSync.StorePath;

    public CatalogFeedLookbackTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        NewProductCardBackfill.AutoRun = false;
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
        CatalogFeedSync.StorePath = _storePath;
        ServerPriceContract.FeedLookbackSeconds = 8;
        ServerOutageMonitor.ResetForTests();
    }

    public void Dispose()
    {
        CatalogFeedSync.StorePath = _oldStorePath;
        ServerPriceContract.FeedLookbackSeconds = 8;
        NewProductCardBackfill.AutoRun = true;
        NewProductCardBackfill.ResetForTests();
        _repo.ClearAll();
        try { File.Delete(_storePath); } catch { /* не критично */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Card(string id, double price, double stock, double purchase = 40) =>
        "{\"id\":\"" + id + "\",\"name\":\"Товар " + id + "\",\"barcode\":\"4600" + string.Concat(id.Select(c => ((int)c).ToString("D3"))) + "\",\"price\":\"" + price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        + "\",\"purchase_price\":\"" + purchase.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\",\"quantity\":\"" + stock.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        + "\",\"unit\":\"шт\",\"kind\":\"product\"}";

    /// <summary>Лента сервера: курсор — время ответа (мс, как у NurCRM). Строка видна ленте, когда «закоммичена» (Commit), и приходит, если её время изменения позже запрошенного since.</summary>
    private sealed class FakeFeed : ICatalogFeedApi
    {
        public string ScopeKey => "";
        public long Now = 1_794_016_867_698;
        private readonly List<(long At, string Json)> _visible = new();
        public readonly List<string?> Requested = new();
        public List<JsonElement>? Recent;

        public void Commit(long at, string json) => _visible.Add((at, json));

        public Task<CatalogFeedPage> ProductsChangesAsync(string? since, int limit, string? etag, CancellationToken ct = default)
        {
            Requested.Add(since);
            var s = long.TryParse(since, out var v) ? v : 0;
            var items = _visible.Where(x => x.At > s).Select(x => J(x.Json)).ToList();
            return Task.FromResult(new CatalogFeedPage { Status = CatalogFeedStatus.Ok, Items = items, NextCursor = Now.ToString(), HasMore = false, Note = "тест" });
        }

        public Task<List<JsonElement>?> ProductsRecentAsync(int limit, CancellationToken ct = default) => Task.FromResult(Recent);
    }

    private CatalogFeedSync NewSync(FakeFeed feed, Func<Func<string, bool>>? guard = null)
    {
        var sync = new CatalogFeedSync(feed, () => ApiBase, () => "test-account", () => true, guard);
        sync.AdoptCursor((feed.Now - 20_000).ToString());
        return sync;
    }

    private void SeedLocal(string id, double price, double stock) =>
        CatalogFeedSync.Apply(new CatalogFeedPage { Status = CatalogFeedStatus.Ok, Items = [J(Card(id, price, stock, 10))] }, ApiBase, allowStock: true);

    [Fact]
    public async Task Item_committed_after_the_cursor_moved_is_picked_up_on_the_next_poll_thanks_to_the_lookback()
    {
        SeedLocal("e-1", 15, 7); // касса знает старое: 15 и 7 шт.
        var feed = new FakeFeed();
        var sync = NewSync(feed);

        // Опрос 1 (08:01:07.698): в ответе новый товар, а изменение «e-1» (время 867 687, на 11 мс раньше курсора) ещё не закоммичено.
        feed.Commit(feed.Now - 100, Card("n-1", 60, 0));
        var first = await sync.TickAsync();
        first.Added.Should().Be(1);
        _repo.LoadTileCopy("e-1")!.PriceValue.Should().Be(15, "в первом ответе товара ещё нет");
        sync.Cursor.Should().Be(feed.Now.ToString(), "курсор ушёл на время ответа сервера");

        // Транзакция прихода закоммитилась: товар изменён в 867 687 (< курсора 867 698).
        feed.Commit(feed.Now - 11, Card("e-1", 60, 8));
        feed.Now += 8_000; // следующий опрос через 8 с

        var second = await sync.TickAsync();
        second.Changed.Should().Be(1, "запас назад вернул товар, который курсор уже «проехал»");
        var tile = _repo.LoadTileCopy("e-1")!;
        tile.PriceValue.Should().Be(60);
        tile.Quantity.Should().Be(8);
        feed.Requested[1].Should().Be("1794016859698", "второй запрос шёл с курсором (…867698) минус запас 8 с, а не с самим курсором");
    }

    [Fact]
    public async Task Without_the_lookback_the_same_item_is_lost_which_is_the_live_failure()
    {
        ServerPriceContract.FeedLookbackSeconds = 0;
        SeedLocal("e-1", 15, 7);
        var feed = new FakeFeed();
        var sync = NewSync(feed);
        feed.Commit(feed.Now - 100, Card("n-1", 60, 0));
        await sync.TickAsync();
        feed.Commit(feed.Now - 11, Card("e-1", 60, 8));
        feed.Now += 8_000;

        await sync.TickAsync();

        _repo.LoadTileCopy("e-1")!.PriceValue.Should().Be(15, "так это и было в журнале: изменение потеряно, остаётся только полная загрузка");
    }

    [Fact]
    public async Task Repeated_items_from_the_lookback_window_are_harmless_and_quiet()
    {
        var feed = new FakeFeed();
        var sync = NewSync(feed);
        feed.Commit(feed.Now - 100, Card("n-1", 60, 3));
        (await sync.TickAsync()).Added.Should().Be(1);

        // Следующие опросы возвращают тот же товар (он внутри запаса) — в базе ничего не меняется, «изменено 0».
        feed.Now += 3_000;
        var again = await sync.TickAsync();
        again.Applied.Should().BeFalse("повтор того же товара не считается изменением");
        again.Ok.Should().BeTrue();
        _repo.LoadTileCopy("n-1")!.Quantity.Should().Be(3);
    }

    [Fact]
    public async Task Lookback_never_overwrites_a_fresh_local_stock_change()
    {
        SeedLocal("e-1", 50, 5); // на этом ПК только что продали: остаток 5 (сервер ещё думает 7)
        var feed = new FakeFeed();
        var sync = NewSync(feed, guard: () => id => id != "e-1"); // FeedStockGuard: у e-1 свежая локальная активность
        feed.Commit(feed.Now - 11, Card("e-1", 60, 7));

        await sync.TickAsync();

        var tile = _repo.LoadTileCopy("e-1")!;
        tile.Quantity.Should().Be(5, "остаток держит защита — устаревший остаток из ленты не откатывает продажу");
        tile.PriceValue.Should().Be(60, "новая цена при этом применяется");
    }

    [Fact]
    public void Cursor_helpers_shift_epoch_times_and_never_go_backwards()
    {
        CatalogFeedSync.ShiftBack("1794016867698", TimeSpan.FromSeconds(8)).Should().Be("1794016859698");
        CatalogFeedSync.ShiftBack("1794016867", TimeSpan.FromSeconds(8)).Should().Be("1794016859");
        CatalogFeedSync.ShiftBack("abc-opaque-cursor", TimeSpan.FromSeconds(8)).Should().Be("abc-opaque-cursor", "непрозрачный курсор не трогаем");
        CatalogFeedSync.ShiftBack("1794016867698", TimeSpan.Zero).Should().Be("1794016867698");
        CatalogFeedSync.ShiftBack(null, TimeSpan.FromSeconds(8)).Should().BeNull();
        CatalogFeedSync.Newer("1794016867698", "1794016859698").Should().Be("1794016867698", "курсор хранения не идёт назад");
        CatalogFeedSync.Newer("1794016867698", "1794016869000").Should().Be("1794016869000");
        CatalogFeedSync.Newer("c1", "c2").Should().Be("c2");
        CatalogFeedSync.Newer("c1", null).Should().Be("c1");
    }

    [Fact]
    public async Task Recent_reconcile_fixes_what_the_feed_missed_and_is_idempotent()
    {
        SeedLocal("e-1", 15, 7);
        SeedLocal("e-2", 30, 4);
        var feed = new FakeFeed { Recent = [J(Card("e-1", 60, 8, 10)), J(Card("e-2", 30, 4, 10))] }; // e-2 уже верен
        var sync = NewSync(feed);

        var first = await sync.ReconcileRecentAsync(manual: true);
        first.Ok.Should().BeTrue();
        first.Checked.Should().Be(2);
        first.Fixed.Should().Be(1, "исправлен один товар — e-1");
        _repo.LoadTileCopy("e-1")!.PriceValue.Should().Be(60);
        _repo.LoadTileCopy("e-1")!.Quantity.Should().Be(8);
        _repo.LoadTileCopy("e-2")!.Quantity.Should().Be(4);

        var second = await sync.ReconcileRecentAsync();
        second.Ok.Should().BeTrue();
        second.Fixed.Should().Be(0, "повторная сверка ничего не меняет");
        sync.LastRecentUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Recent_reconcile_keeps_the_protected_local_stock()
    {
        SeedLocal("e-1", 50, 5);
        var feed = new FakeFeed { Recent = [J(Card("e-1", 60, 7))] };
        var sync = NewSync(feed, guard: () => _ => false);

        var result = await sync.ReconcileRecentAsync();

        result.Ok.Should().BeTrue();
        _repo.LoadTileCopy("e-1")!.Quantity.Should().Be(5, "локальная продажа под защитой");
        _repo.LoadTileCopy("e-1")!.PriceValue.Should().Be(60);
    }

    [Fact]
    public async Task Recent_reconcile_is_skipped_when_the_server_cannot_sort_by_updated_at()
    {
        var feed = new FakeFeed { Recent = null };
        var sync = NewSync(feed);

        var result = await sync.ReconcileRecentAsync();

        result.Ok.Should().BeFalse("без сортировки «последние» были бы случайными — сверка молча выключена");
        sync.LastRecentUtc.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public async Task Feed_cursor_survives_restart_and_stays_monotonic_on_disk()
    {
        var feed = new FakeFeed();
        var sync = NewSync(feed);
        feed.Commit(feed.Now - 100, Card("n-1", 60, 3));
        await sync.TickAsync();
        var saved = sync.Cursor;

        var restarted = new CatalogFeedSync(feed, () => ApiBase, () => "test-account", () => true);
        restarted.HasSavedCursor().Should().BeTrue();
        restarted.Cursor.Should().Be(saved);
    }
}
