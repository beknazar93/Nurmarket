using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (волна А, С5 и С7): гарантия / «Электрика» / «Установленные не принимаются» едут на сервер в attributes (warranty_days, electric, no_return); сервер главнее местного файла,
/// местные отметки переносятся при первом сохранении; предупреждение о конфликте правок карточки (ProductConflict) считает изменение только по правимым полям.</summary>
[Collection("UserPreferences")]
public sealed class AutoPartsServerMetaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "autoparts-srv-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsServerMetaTests()
    {
        Directory.CreateDirectory(_dir);
        AutoPartsProductMeta.FileOverride = Path.Combine(_dir, "autoparts-products.json");
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
    }

    public void Dispose()
    {
        AutoPartsProductMeta.FileOverride = null;
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Attributes_WarrantyElectricNoReturn_AreBuiltAndParsed()
    {
        var info = new AutoPartInfo { PartNo = "W81180", WarrantyDays = 45, Electric = true, NoReturn = false };
        var attrs = info.BuildAttributes();
        attrs.Should().Contain(a => a.Key == "warranty_days" && a.Value == "45");
        attrs.Should().Contain(a => a.Key == "electric" && a.Value == "1");
        attrs.Should().Contain(a => a.Key == "no_return" && a.Value == "0", "«0» — явная отметка «можно», она тоже пишется");

        var back = AutoPartInfo.Parse(Card("{\"attributes\":[{\"key\":\"warranty_days\",\"value\":\"45\"},{\"key\":\"electric\",\"value\":1},{\"key\":\"no_return\",\"value\":\"0\"},{\"key\":\"x_site\",\"value\":\"a\"}]}"))!;
        back.WarrantyDays.Should().Be(45);
        back.Electric.Should().BeTrue();
        back.NoReturn.Should().BeFalse();
        back.Other.Should().ContainSingle(o => o.Key == "x_site", "чужие атрибуты не теряются, а наши ключи в «чужие» не попадают");
    }

    [Fact]
    public void Attributes_WithoutMeta_NothingExtraInBody()
    {
        var info = new AutoPartInfo { PartNo = "W81180" };
        info.BuildAttributes().Select(a => a.Key).Should().NotContain(new[] { "warranty_days", "electric", "no_return" });
        AutoPartInfo.Parse(Card("{\"attributes\":[{\"key\":\"warranty_days\",\"value\":\"0\"}]}")).Should().BeNull("гарантия 0 = нет, ничего автозапчастного в карточке нет");
    }

    [Fact]
    public void RequestBody_CarriesMetaAttributes()
    {
        var info = new AutoPartInfo { WarrantyDays = 30, NoReturn = true };
        var request = new ProductEditRequest { Attributes = info.BuildAttributes() };
        var body = new Dictionary<string, object?>();
        PartMetaBody.Apply(body, request);
        var items = ((object[])body["attributes"]!).Cast<Dictionary<string, object?>>().ToDictionary(d => (string)d["key"]!, d => d["value"]);
        items["warranty_days"].Should().Be(30L, "2026-11-20 (ответ Умара 20.11): гарантия и флаги уходят числами, как в примере сервера");
        items["no_return"].Should().Be(1L);
    }

    [Fact]
    public void Meta_ServerWinsPerField_LocalFillsTheRest_AndMigrationBaselineIsServer()
    {
        AutoPartsProductMeta.Set("p1", 10, electric: true, noReturn: false); // местный файл
        AutoPartsProductMeta.Get("p1")!.WarrantyDays.Should().Be(10, "сервер ещё ничего не хранит — местная отметка");

        PartMetaStore.Remember("p1", Card("{\"attributes\":[{\"key\":\"warranty_days\",\"value\":\"90\"}]}"));
        var merged = AutoPartsProductMeta.Get("p1")!;
        merged.WarrantyDays.Should().Be(90, "сервер главнее");
        merged.Electric.Should().BeTrue("этого поля сервер не хранит — берётся местное (по полям)");
        AutoPartsProductMeta.GetLocal("p1")!.WarrantyDays.Should().Be(10, "местный файл остался кэшем и для переноса");

        // Миграция: карточка без отметок на сервере и с отметками в файле — «исходное с сервера» без них, «текущее» с ними => подписи различаются и атрибуты уйдут на сервер.
        var serverView = new AutoPartInfo { PartNo = "P" };
        var current = serverView.Clone();
        var local = AutoPartsProductMeta.GetLocal("p1")!;
        current.WarrantyDays = local.WarrantyDays; current.Electric = local.Electric; current.NoReturn = local.NoReturn;
        JsonSerializer.Serialize(current.BuildAttributes()).Should().NotBe(JsonSerializer.Serialize(serverView.BuildAttributes()));
    }

    [Fact]
    public void Meta_ServerClearsMarks_PartMetaStoreStaysConsistent()
    {
        PartMetaStore.Remember("p2", Card("{\"attributes\":[{\"key\":\"electric\",\"value\":\"1\"}]}"));
        AutoPartsProductMeta.IsNotReturnable("p2", "Масло").Should().BeTrue("сервер сказал «электрика»");
        PartMetaStore.Set("p2", new AutoPartInfo { Electric = false, NoReturn = false });
        AutoPartsProductMeta.IsNotReturnable("p2", "Датчик давления").Should().BeFalse("явная серверная отметка «0» сильнее названия");
    }

    [Fact]
    public void ProductConflict_OnlyEditedFieldsCount_StockAndUpdatedAtAreIgnored()
    {
        var opened = Card("{\"name\":\"Фильтр\",\"price\":\"100.00\",\"quantity\":\"5\",\"updated_at\":\"t1\",\"description\":\"a\"}");
        var fresh = Card("{\"name\":\"Фильтр\",\"price\":130,\"quantity\":\"2\",\"updated_at\":\"t2\",\"description\":\"b\"}");

        ProductConflict.Changed(opened, fresh, ProductConflict.EditedKeys(false, false, false, false, true, false, false))
            .Should().ContainSingle(d => d.Key == "description" && d.Opened == "a" && d.Fresh == "b");
        ProductConflict.Changed(opened, fresh, ProductConflict.EditedKeys(true, false, false, false, false, false, false))
            .Should().BeEmpty("продажа на другой кассе двинула остаток и updated_at, но имя, которое правим, не менялось");
        ProductConflict.Changed(opened, fresh, ProductConflict.EditedKeys(false, true, false, false, false, false, false))
            .Should().ContainSingle(d => d.Key == "price" && d.Opened == "100" && d.Fresh == "130", "«100.00» и 100 — одно число");
        ProductConflict.Changed(opened, opened, ProductConflict.EditedKeys(true, true, true, true, true, true, true)).Should().BeEmpty();
        ProductConflict.Changed(default, fresh, ["price"]).Should().BeEmpty("карточку при открытии не читали — конфликт не проверяем");
    }
}
