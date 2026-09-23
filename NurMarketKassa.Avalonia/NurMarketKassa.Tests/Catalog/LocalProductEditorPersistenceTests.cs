using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-09-12: пользователь поймал два реальных бага в LocalProductEditor.SaveLocally — галочка
/// «Поштучная продажа» и состав «Комплекта» молча терялись при сохранении товара офлайн (поля
/// просто не копировались в CatalogProductTileVm перед записью в SQLite, хотя сама БД их хранить
/// умела). Тесты гоняют РЕАЛЬНЫЙ путь сохранения (SaveLocally → LocalProductRepository.
/// UpsertFromTiles → SQL → LoadAllTiles), а не собирают CatalogProductTileVm вручную — иначе
/// баг остался бы незамеченным, как и в первый раз.
/// </summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class LocalProductEditorPersistenceTests : IDisposable
{
    private readonly LocalProductRepository _repository = LocalProductRepository.Instance;

    public LocalProductEditorPersistenceTests() => _repository.EnsureSchema();

    public void Dispose() => _repository.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());

    [Fact]
    public void SaveLocally_PersistsPieceSaleOption_ThroughFullRoundTrip()
    {
        var request = new ProductEditRequest
        {
            Name = "Тестовый штучный товар",
            Unit = "шт",
            Quantity = 5,
            Price = 100,
            EnablePieceSale = true,
            PackageQuantity = 6,
            PackagePiecePrice = 20,
            IsNew = true,
        };

        var id = LocalProductEditor.SaveLocally(request, existingId: null);

        var saved = _repository.LoadAllTiles().Should().ContainSingle().Which;
        saved.Id.Should().Be(id);
        saved.HasPieceOption.Should().BeTrue("галочка «Поштучная продажа» была включена при сохранении");
        saved.PieceOption!.QuantityInPackage.Should().Be(6);
        saved.PieceOption.PieceUnitPrice.Should().Be(20);
    }

    [Fact]
    public void SaveLocally_ClearingPieceSale_RemovesPieceOptionOnUpdate()
    {
        var created = new ProductEditRequest
        {
            Name = "Товар",
            Unit = "шт",
            Price = 50,
            EnablePieceSale = true,
            PackageQuantity = 3,
            PackagePiecePrice = 15,
            IsNew = true,
        };
        var id = LocalProductEditor.SaveLocally(created, existingId: null);

        var updated = new ProductEditRequest
        {
            Name = "Товар",
            Unit = "шт",
            Price = 50,
            EnablePieceSale = false,
            IsNew = false,
        };
        LocalProductEditor.SaveLocally(updated, existingId: id);

        var saved = _repository.LoadAllTiles().Should().ContainSingle().Which;
        saved.HasPieceOption.Should().BeFalse("галочку сняли при редактировании — старая упаковка не должна остаться");
    }

    [Fact]
    public void SaveLocally_PersistsBundleComposition_ThroughFullRoundTrip()
    {
        var component = CatalogTestHelper.CreateTile("component-1", "Компонент", "4600000000017", 30);
        _repository.SyncReplaceAllWithDiff([component]);

        var request = new ProductEditRequest
        {
            Name = "Тестовый комплект",
            Unit = "шт",
            Price = 250,
            Kind = "bundle",
            BundleItems = [new BundleComponent { ProductId = "component-1", ProductName = "Компонент", Quantity = 2 }],
            IsNew = true,
        };

        var id = LocalProductEditor.SaveLocally(request, existingId: null);

        var saved = _repository.LoadAllTiles().First(t => t.Id == id);
        saved.IsBundle.Should().BeTrue("Kind=\"bundle\" был указан при сохранении");
        saved.BundleItems.Should().ContainSingle()
            .Which.Should().Match<BundleComponent>(c => c.ProductId == "component-1" && c.Quantity == 2);
    }

    [Fact]
    public void SaveLocally_RegularProduct_HasNoBundleFlagOrItems()
    {
        var request = new ProductEditRequest { Name = "Обычный товар", Unit = "шт", Price = 10, Kind = "product", IsNew = true };

        var id = LocalProductEditor.SaveLocally(request, existingId: null);

        var saved = _repository.LoadAllTiles().First(t => t.Id == id);
        saved.IsBundle.Should().BeFalse();
        saved.BundleItems.Should().BeNull();
    }
}
