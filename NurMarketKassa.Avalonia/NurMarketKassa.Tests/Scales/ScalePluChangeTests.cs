using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Catalog;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-13 (сервер «ev1311»: товар в архиве освобождает PLU, при возврате из архива весовой товар получает НОВЫЙ PLU): касса берёт PLU из ответа сервера
/// (лента, точечное обновление, полная загрузка), помнит прежние PLU (ScalePluHistory) и на скане этикетки со старым PLU не продаёт молча другой товар.
/// Тесты без сети: настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1). Запускать только tools/qa/test-safe.ps1.</summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class ScalePluChangeTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly List<CatalogProductTileVm> _savedCatalog = CatalogCacheService.Products.ToList();
    private readonly string _savedLayout = WeightBarcodeParser.Layout;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-plu-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    public ScalePluChangeTests()
    {
        Directory.CreateDirectory(_dir);
        ScalePluHistory.DirOverride = _dir;
        ScalePluHistory.NowUtcOverride = () => _now;
        ScalePluHistory.ResetForTests();
        WeightBarcodeParser.Layout = "plu";
        _repo.EnsureSchema();
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
    }

    public void Dispose()
    {
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_savedCatalog);
        WeightBarcodeParser.Layout = _savedLayout;
        ScalePluHistory.DirOverride = null;
        ScalePluHistory.NowUtcOverride = null;
        ScalePluHistory.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Card(string id, string name, int? plu) =>
        "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"price\":\"120.00\",\"quantity\":\"5\",\"unit\":\"кг\",\"is_weight\":true,\"plu\":"
        + (plu is null ? "null" : plu.ToString()) + ",\"barcode\":\"\",\"article\":\"\",\"code\":\"\",\"updated_at\":\"2026-11-13T09:00:00Z\"}";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage Page(params string[] cards) => new()
    {
        Status = CatalogFeedStatus.Ok,
        Items = cards.Select(J).ToList(),
        NextCursor = "c-1",
    };

    private void Seed(string json) => _repo.SyncReplaceAllWithDiff([ProductCatalogMapper.TryTile(J(json), ApiBase)!], deleteMissing: false);

    /// <summary>Каталог в памяти кассы (по нему ищет скан) — как после ленты (SyncInMemoryCatalog): плитки из базы.</summary>
    private void Reload()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_repo.LoadAllTiles());
    }

    [Fact]
    public void Feed_changes_PLU_in_base_and_remembers_the_old_one()
    {
        Seed(Card("w-1", "Яблоки", 21));

        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 34)), ApiBase, allowStock: true).Changed.Should().Be(1);

        _repo.LoadTileCopy("w-1")!.Plu.Should().Be(34, "PLU берётся из последнего ответа сервера, а не держится старый");
        ScalePluHistory.Find("21")!.Should().Match<ScalePluHistory.Retired>(r => r.ProductId == "w-1" && r.NewPlu == 34);
        ScalePluHistory.HasChange("w-1").Should().BeTrue("окно «Весы» пометит товар «PLU изменился — отправьте на весы»");
        ScalePluHistory.IsRetiredCodeOf("w-1", "0021").Should().BeTrue("закреплённый «Код в ШК» 21 у этого товара устарел (ведущие нули не важны)");
        ScalePluHistory.IsRetiredCodeOf("w-2", "21").Should().BeFalse("чужой товар к старому PLU отношения не имеет");
        ScalePluHistory.Find("34").Should().BeNull("новый PLU не «старый»");
    }

    [Fact]
    public void Archive_frees_PLU_and_return_with_new_PLU_is_found_and_explained()
    {
        Seed(Card("w-1", "Яблоки", 21));

        // товар отправлен в архив: лента присылает is_archived — товар уходит из базы, PLU освобождён
        var archived = Card("w-1", "Яблоки", 21).Replace("\"plu\":21", "\"plu\":null,\"is_archived\":true");
        CatalogFeedSync.Apply(Page(archived), ApiBase, allowStock: true).Deleted.Should().Be(1);
        _repo.LoadTileCopy("w-1").Should().BeNull();
        ScalePluHistory.Find("21")!.NewPlu.Should().BeNull("новый PLU пока неизвестен");

        // этикетка со старым PLU: PLU свободен — понятное сообщение, товар не добавляется
        Reload();
        var problem = ScalePluHistory.CheckLabel("00021", LocalCartService.FindByEmbeddedCode("00021"));
        problem.Should().NotBeNull();
        problem!.Kind.Should().Be(ScalePluHistory.LabelProblemKind.Freed);
        problem.Message.Should().Contain("21").And.Contain("Яблоки");

        // возврат из архива: сервер дал НОВЫЙ PLU — касса берёт его из ответа, а старая этикетка по-прежнему не продаёт чужое
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true).Added.Should().Be(1);
        _repo.LoadTileCopy("w-1")!.Plu.Should().Be(33);
        ScalePluHistory.Find("21")!.NewPlu.Should().Be(33, "в сообщении будет сказано, какой PLU у товара теперь");
        Reload();
        LocalCartService.FindByEmbeddedCode("33")!.Id.Should().Be("w-1", "новая этикетка находит товар");
        var again = ScalePluHistory.CheckLabel("21", LocalCartService.FindByEmbeddedCode("21"));
        again!.Kind.Should().Be(ScalePluHistory.LabelProblemKind.Freed);
        again.Message.Should().Contain("33");
    }

    [Fact]
    public void Old_PLU_taken_by_another_product_asks_the_cashier_and_remembers_the_answer()
    {
        Seed(Card("w-1", "Яблоки", 21));
        Seed(Card("w-2", "Бананы", 50));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true);   // «Яблоки» переехали на 33
        CatalogFeedSync.Apply(Page(Card("w-2", "Бананы", 21)), ApiBase, allowStock: true);   // «Бананы» заняли освободившийся 21
        Reload();

        var found = LocalCartService.FindByEmbeddedCode("21");
        found!.Id.Should().Be("w-2", "каталог видит новый PLU — без проверки старая этикетка «Яблок» молча продала бы «Бананы»");
        var problem = ScalePluHistory.CheckLabel("21", found);
        problem.Should().NotBeNull();
        problem!.Kind.Should().Be(ScalePluHistory.LabelProblemKind.TakenByOther);
        problem.Message.Should().Contain("Яблоки").And.Contain("Бананы");

        ScalePluHistory.Accept(problem);
        ScalePluHistory.CheckLabel("21", found).Should().BeNull("кассир подтвердил «Бананы» — до перезапуска не спрашиваем снова");
        ScalePluHistory.CheckLabel("21", LocalCartService.FindByEmbeddedCode("33")).Should().BeNull("тот же товар, что был под этим PLU (нашёлся по другому полю), — без вопросов");
    }

    [Fact]
    public void Old_PLU_that_still_finds_the_same_product_through_scale_codes_is_not_blocked()
    {
        Seed(Card("w-1", "Яблоки", 21));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true);
        Reload();
        var same = CatalogCacheService.Products.Single(p => p.Id == "w-1");

        ScalePluHistory.CheckLabel("21", same).Should().BeNull("по списку кодов весов нашёлся тот же товар — этикетка верна по существу");
    }

    [Fact]
    public void Full_card_read_replaces_a_different_PLU_in_base()
    {
        Seed(Card("w-1", "Яблоки", 21));

        var fresh = ProductCatalogMapper.TryTile(J(Card("w-1", "Яблоки", 40)), ApiBase)!;
        _repo.FillMissingCodes(fresh).Should().BeTrue();

        _repo.LoadTileCopy("w-1")!.Plu.Should().Be(40, "карточка, только что прочитанная с сервера, главнее локального PLU");
        ScalePluHistory.Find("21")!.NewPlu.Should().Be(40);
    }

    [Fact]
    public void Short_feed_card_without_plu_key_keeps_the_known_PLU_and_remembers_nothing()
    {
        Seed(Card("w-1", "Яблоки", 21));

        CatalogFeedSync.Apply(Page("{\"id\":\"w-1\",\"name\":\"Яблоки\",\"price\":\"130.00\",\"quantity\":\"4\",\"updated_at\":\"2026-11-13T09:30:00Z\"}"), ApiBase, allowStock: true);

        _repo.LoadTileCopy("w-1")!.Plu.Should().Be(21);
        ScalePluHistory.Any.Should().BeFalse();
    }

    [Fact]
    public void Old_PLU_is_forgotten_after_a_month()
    {
        Seed(Card("w-1", "Яблоки", 21));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true);
        ScalePluHistory.Find("21").Should().NotBeNull();

        _now = _now.AddDays(31);

        ScalePluHistory.Find("21").Should().BeNull("этикетки и ячейки весов за месяц обновляются — память не вечная");
        ScalePluHistory.HasChange("w-1").Should().BeFalse();
    }

    [Fact]
    public void Product_that_regains_its_old_PLU_is_no_longer_old()
    {
        Seed(Card("w-1", "Яблоки", 21));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true);
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 21)), ApiBase, allowStock: true);

        ScalePluHistory.Find("21").Should().BeNull("вернул прежний номер — этикетки снова верны");
        ScalePluHistory.Find("33")!.ProductId.Should().Be("w-1", "а 33, который был у него между делом, теперь старый");
    }

    /// <summary>Весовая этикетка «20 00021 01500»: код 21, вес 1,5 кг (EAN-13 с верной контрольной цифрой).</summary>
    private static string Label21() => WithEan13Check("200002101500");

    private void ArrangeApplesMovedBananasTook21()
    {
        Seed(Card("w-1", "Яблоки", 21));
        Seed(Card("w-2", "Бананы", 50));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 33)), ApiBase, allowStock: true);
        CatalogFeedSync.Apply(Page(Card("w-2", "Бананы", 21)), ApiBase, allowStock: true);
        Reload();
    }

    [Fact]
    public void Offline_cart_does_not_add_a_product_by_a_label_with_old_PLU_taken_by_another()
    {
        ArrangeApplesMovedBananasTook21();
        using var h = new BasketVmHarness();

        LocalCartService.TryAddByBarcode(h.Cart, Label21()).Should().BeFalse("старая этикетка «Яблок» не должна молча продать «Бананы»");
        h.Cart.Items.Should().BeEmpty();

        ScalePluHistory.Accept(ScalePluHistory.CheckLabel("21", LocalCartService.FindByEmbeddedCode("21"))!);
        ScalePluHistory.CheckLabel("21", LocalCartService.FindByEmbeddedCode("21")).Should().BeNull("кассир подтвердил «Бананы» — проверка больше не блокирует");
    }

    [Fact]
    public async Task Cashier_screen_asks_about_old_PLU_taken_by_another_product_and_adds_nothing_when_declined()
    {
        ArrangeApplesMovedBananasTook21();
        using var h = new BasketVmHarness();
        h.Prompts.Answer = false;

        h.Vm.EnqueueBarcode(Label21());

        var until = DateTime.UtcNow.AddSeconds(5);
        while (h.Prompts.Questions.Count == 0 && DateTime.UtcNow < until)
            await Task.Delay(30);
        h.Prompts.Questions.Should().ContainSingle().Which.Should().Contain("Яблоки").And.Contain("Бананы");
        await Task.Delay(200);
        h.Cart.Items.Should().BeEmpty();
        ScalePluHistory.CheckLabel("21", LocalCartService.FindByEmbeddedCode("21")).Should().NotBeNull("отказ не запоминается");
    }

    [Fact]
    public async Task Cashier_screen_warns_when_old_PLU_is_free_instead_of_plain_not_found()
    {
        Seed(Card("w-1", "Яблоки", 21));
        CatalogFeedSync.Apply(Page(Card("w-1", "Яблоки", 21).Replace("\"plu\":21", "\"plu\":null,\"is_archived\":true")), ApiBase, allowStock: true);
        Reload();
        using var h = new BasketVmHarness();

        h.Vm.EnqueueBarcode(Label21());

        var until = DateTime.UtcNow.AddSeconds(5);
        while (h.Prompts.Errors.Count == 0 && DateTime.UtcNow < until)
            await Task.Delay(30);
        h.Prompts.Errors.Should().ContainSingle().Which.Should().Contain("PLU 21").And.Contain("Яблоки");
        h.Cart.Items.Should().BeEmpty();
    }

    private static string WithEan13Check(string twelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return twelve + ((10 - sum % 10) % 10);
    }
}
