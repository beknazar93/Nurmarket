using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-06, дефект Б-1 (расследование «каталог без единиц и PLU»): лента products/changes несёт сокращённую карточку (9 полей), и пустая база,
/// заполненная этой лентой, теряла единицы/«весовой»/PLU/штрихкоды. Тесты без сети: настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1),
/// настоящий разбор ответа ленты и карточек. Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class CatalogFullLoadB1Tests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public CatalogFullLoadB1Tests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
    }

    public void Dispose() => _repo.ClearAll();

    // Карточка products/list (полная) и products/changes (9 полей) — как отдаёт сервер (проверено 06.11.2026).
    private const string FullWeightCard = """
        {"id":"w-1","name":"Сыр весовой","price":"450.00","purchase_price":"300.00","quantity":"12.5","unit":"кг","is_weight":true,"plu":12,
         "barcode":"7265439353995","alternate_barcodes":[],"kind":"simple","article":"A1","code":"77","image_url":"https://app.nurcrm.kg/media/cheese.jpg",
         "hotkey_group":"cheese","category":"Сыры","discount_percent":"0","markup_percent":"50","wholesale_price":"430.00","updated_at":"2026-11-06T01:00:00Z"}
        """;

    private const string FullPieceCard = """
        {"id":"p-1","name":"Хлеб","price":"50.00","quantity":"30","unit":"шт","is_weight":false,"plu":null,"barcode":"4600000000017",
         "alternate_barcodes":[],"kind":"simple","updated_at":"2026-11-06T01:00:00Z"}
        """;

    private const string ShortWeightCard = """
        {"id":"w-1","name":"Сыр весовой","price":"470.00","purchase_price":"300.00","quantity":"11","discount_percent":"0","markup_percent":"50",
         "wholesale_price":"430.00","updated_at":"2026-11-06T02:00:00Z"}
        """;

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage FeedPage(params string[] cards) => new()
    {
        Status = CatalogFeedStatus.Ok,
        Items = cards.Select(J).ToList(),
        NextCursor = "cursor-1",
    };

    // ---------------------------------------------------------------- И-1: сокращённая лента не должна быть источником первой загрузки

    [Fact]
    public void ShortFeedPage_FromRealParse_IsDetected_AndFullCards_AreNot()
    {
        var shortBody = J($$"""{"catalog_version":5,"changes":[{{ShortWeightCard}}],"cursor":"c1","deleted":[],"has_more":false}""");
        var shortPage = CatalogApiService.ParseFeed(shortBody, null);
        shortPage.Status.Should().Be(CatalogFeedStatus.Ok);
        CatalogFullLoadMarks.IsShortFeedPage(shortPage.Items).Should().BeTrue("в карточке ленты нет ни unit/is_weight, ни штрихкода");

        var fullBody = J($$"""{"changes":[{{FullWeightCard}},{{FullPieceCard}}],"cursor":"c1","has_more":false}""");
        CatalogFullLoadMarks.IsShortFeedPage(CatalogApiService.ParseFeed(fullBody, null).Items).Should().BeFalse("когда сервер добавит поля в ленту, быстрая загрузка лентой снова допустима");

        CatalogFullLoadMarks.IsShortFeedPage([]).Should().BeFalse("пустая страница не «сокращённая»");
    }

    [Fact]
    public void EmptyBase_FullPageLoad_KeepsUnitWeightPluBarcode()
    {
        // Первая загрузка пустой базы — полными карточками (как постраничная products/list): единица, «весовой», PLU, штрихкод на месте.
        var tiles = new[] { FullWeightCard, FullPieceCard }.Select(c => ProductCatalogMapper.TryTile(J(c), ApiBase)!).ToList();
        _repo.SyncReplaceAllWithDiff(tiles);
        CatalogFullLoadMarks.MarkFullLoad(_repo, DateTime.UtcNow);

        var weight = _repo.LoadTileCopy("w-1")!;
        weight.MustWeigh.Should().BeTrue();
        weight.Unit.Should().Be("кг");
        weight.Plu.Should().Be(12);
        weight.Barcode.Should().Be("7265439353995");
        weight.ServerIsWeight.Should().BeTrue();
        weight.ImageUrl.Should().Be("https://app.nurcrm.kg/media/cheese.jpg");
        _repo.TryGetTileByBarcode("4600000000017")!.Id.Should().Be("p-1");
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, out _).Should().BeFalse("после полной загрузки база здорова");
    }

    // ---------------------------------------------------------------- И-3: лента не стирает поля

    [Fact]
    public void FeedDelta_OverFullBase_UpdatesPriceAndStock_ButKeepsPhotoHotkeyUnitPluBarcode()
    {
        _repo.SyncReplaceAllWithDiff([ProductCatalogMapper.TryTile(J(FullWeightCard), ApiBase)!]);

        var outcome = CatalogFeedSync.Apply(FeedPage(ShortWeightCard), ApiBase, allowStock: true);
        outcome.Changed.Should().Be(1);

        var after = _repo.LoadTileCopy("w-1")!;
        after.PriceLine.Should().Contain("470.00", "цену лента обновила");
        after.Quantity.Should().Be(11, "остаток из ленты применён");
        after.ImageUrl.Should().Be("https://app.nurcrm.kg/media/cheese.jpg", "фото в ленте нет — остаётся из базы");
        after.HotkeyGroup.Should().Be("cheese", "горячая клавиша в ленте не приходит — остаётся из базы");
        after.MustWeigh.Should().BeTrue();
        after.Unit.Should().Be("кг");
        after.Plu.Should().Be(12);
        after.Barcode.Should().Be("7265439353995");
        after.ServerIsWeight.Should().BeTrue();
    }

    [Fact]
    public void FeedDelta_WithImageKey_OverridesLocalPhoto()
    {
        _repo.SyncReplaceAllWithDiff([ProductCatalogMapper.TryTile(J(FullWeightCard), ApiBase)!]);
        const string withNewPhoto = """{"id":"w-1","name":"Сыр весовой","price":"450.00","quantity":"12.5","image_url":"https://app.nurcrm.kg/media/new.jpg","hotkey_group":"","updated_at":"2026-11-06T03:00:00Z"}""";

        CatalogFeedSync.Apply(FeedPage(withNewPhoto), ApiBase, allowStock: true);

        var after = _repo.LoadTileCopy("w-1")!;
        after.ImageUrl.Should().Be("https://app.nurcrm.kg/media/new.jpg", "ключ пришёл — применяется как пришло");
        (after.HotkeyGroup ?? "").Should().BeEmpty("сервер сознательно очистил клавишу");
    }

    // ---------------------------------------------------------------- И-2: самолечение

    [Fact]
    public void SelfHeal_FiresForFeedFilledBase_OnlyOnce()
    {
        // База, заполненная сокращённой лентой: единицы «шт», is_weight NULL у всех.
        var broken = Enumerable.Range(1, 20)
            .Select(i => ProductCatalogMapper.TryTile(J($$"""{"id":"s-{{i}}","name":"Товар {{i}}","price":"10.00","quantity":"5","updated_at":"2026-11-06T01:00:00Z"}"""), ApiBase)!)
            .ToList();
        _repo.SyncReplaceAllWithDiff(broken);

        CatalogFullLoadMarks.NeedsSelfHeal(_repo, out var why).Should().BeTrue();
        why.Should().Contain("20 товаров");

        // Полная загрузка выполнена (метка) — повторно не лечим, даже если сервер вообще не присылает is_weight.
        CatalogFullLoadMarks.MarkFullLoad(_repo, DateTime.UtcNow);
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, out _).Should().BeFalse();
    }

    [Fact]
    public void SelfHeal_DoesNotFire_ForHealthyOrEmptyBase()
    {
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, out _).Should().BeFalse("пустая база — обычная первая загрузка, а не лечение");

        _repo.SyncReplaceAllWithDiff([ProductCatalogMapper.TryTile(J(FullWeightCard), ApiBase)!, ProductCatalogMapper.TryTile(J(FullPieceCard), ApiBase)!]);
        // Метки нет (база от версии до этой правки), но is_weight у всех есть — лечить нечего.
        CatalogFullLoadMarks.GetLastFullLoadUtc(_repo).Should().BeNull();
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------- перезапуск не обнуляет отсчёт полной загрузки

    [Fact]
    public void LastFullLoad_IsPersistedInDatabase_AndSurvivesRestart()
    {
        var when = DateTime.UtcNow.AddHours(-5);
        CatalogFullLoadMarks.MarkFullLoad(_repo, when);

        // «Перезапуск»: метку читаем заново из базы, а не из памяти службы.
        var restored = CatalogFullLoadMarks.GetLastFullLoadUtc(_repo);
        restored.Should().NotBeNull();
        (restored!.Value - when).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
        CatalogFullLoadMarks.LastFullLoadOrLegacy(_repo)!.Value.Should().Be(restored.Value);
    }

    [Fact]
    public void LastFullLoad_FallsBackToLegacyLastSync_AndIsClearedWithAccountChange()
    {
        var legacy = DateTime.UtcNow.AddHours(-2);
        _repo.SetLastSyncTime(legacy);
        CatalogFullLoadMarks.GetLastFullLoadUtc(_repo).Should().BeNull();
        (CatalogFullLoadMarks.LastFullLoadOrLegacy(_repo)!.Value - legacy).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1),
            "базе до обновления отсчёт идёт от last_sync_utc, а не от нуля");

        CatalogFullLoadMarks.MarkFullLoad(_repo, DateTime.UtcNow);
        _repo.ClearAll(); // смена аккаунта
        CatalogFullLoadMarks.GetLastFullLoadUtc(_repo).Should().BeNull("метка принадлежала прежнему аккаунту");
        CatalogFullLoadMarks.LastFullLoadOrLegacy(_repo).Should().BeNull();
    }
}
