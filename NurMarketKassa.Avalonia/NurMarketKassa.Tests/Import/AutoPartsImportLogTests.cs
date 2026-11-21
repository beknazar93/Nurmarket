using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20 (проверка «Автозапчастей», дефект А): журнал импорта прайса. Повторная загрузка того же прайса без штрихкодов сразу после первой
/// не должна создавать дубли, пока каталог кассы не получил артикул и бренд новых товаров; запись чистится, когда каталог догнал; удалённый на сервере
/// товар не мешает.</summary>
[Collection("UserPreferences")] // PartMetaStore / AutoPartsProductMeta / журнал — общее состояние
public sealed class AutoPartsImportLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ap-log-" + Guid.NewGuid().ToString("N")[..8]);
    private const string Csv = "Название;Бренд;Артикул;Цена\nФильтр масляный;Mann;W 811/80;450\nФильтр воздушный;Bosch;0 451 103 092;300\nСвеча;NGK;BKR6E;120\n";

    public AutoPartsImportLogTests()
    {
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
        AutoPartsProductMeta.FileOverride = Path.Combine(_dir, "autoparts-products.json");
        AutoPartsImportLog.FileOverride = Path.Combine(_dir, "autoparts-import-log.json");
        AutoPartsImportLog.CompanyIdOverride = "company-1";
        AutoPartsImportLog.ResetForTests();
    }

    public void Dispose()
    {
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        AutoPartsProductMeta.FileOverride = null;
        AutoPartsImportLog.FileOverride = null;
        AutoPartsImportLog.CompanyIdOverride = null;
        AutoPartsImportLog.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string N(string s) => NurMarketKassa.Core.Application.PartNumbers.Norm(s);

    private static ProductFileTable Prepared(string text)
    {
        var r = ProductFileReader.ReadClipboard(text);
        Assert.True(r.Ok, r.Error);
        var t = r.Table!;
        t.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(t));
        return t;
    }

    /// <summary>Первая загрузка: как загрузчик — AfterSaved по каждому новому товару; плитки в каталоге без артикула и бренда (карточку ещё не докачали).</summary>
    private static List<CatalogProductTileVm> FirstLoad(bool tilesInCatalog)
    {
        var first = AutoPartsPriceImport.Prepare(Prepared(Csv), null, []);
        Assert.Equal(3, first.Created);
        var tiles = new List<CatalogProductTileVm>();
        var n = 0;
        foreach (var row in first.Rows.Where(r => r.Status == AutoPartsRowStatus.New))
        {
            var id = "srv-" + ++n;
            row.Data!.AfterSaved(id);
            tiles.Add(new CatalogProductTileVm(id, row.Name, "", false) { Barcode = row.Barcode, Article = "", Brand = "", Quantity = 0 });
        }
        PartMetaStore.ResetForTests(); // данные запчастей в каталоге ещё не появились (как у чистой папки/после перезапуска)
        return tilesInCatalog ? tiles : [];
    }

    [Fact]
    public void Second_load_right_after_first_creates_nothing()
    {
        var tiles = FirstLoad(tilesInCatalog: true);
        var again = AutoPartsPriceImport.Prepare(Prepared(Csv), null, tiles);
        Assert.Equal(0, again.Created);
        Assert.Equal(3, again.Updated);
        Assert.All(again.Rows, r => Assert.NotEqual(AutoPartsRowStatus.New, r.Status));
        // существующему дописываются артикул и бренд
        Assert.All(again.Rows.Select(r => r.Data!), d => Assert.True(d.NeedsBrand && d.NeedsArticle));
    }

    [Fact]
    public void Product_not_yet_in_catalog_is_skipped_not_duplicated()
    {
        FirstLoad(tilesInCatalog: false);
        var again = AutoPartsPriceImport.Prepare(Prepared(Csv), null, []);
        Assert.Equal(0, again.Created);
        Assert.Equal(3, again.Skipped);
    }

    [Fact]
    public void Product_deleted_on_server_is_created_again()
    {
        var first = AutoPartsPriceImport.Prepare(Prepared(Csv), null, []);
        foreach (var row in first.Rows)
            AutoPartsImportLog.Record("gone-" + row.FileRow, N(row.PartNo), ProductImportMapping.Key(row.Brand), row.Brand,
                DateTime.UtcNow - AutoPartsImportLog.PendingWindow - TimeSpan.FromMinutes(1));
        var again = AutoPartsPriceImport.Prepare(Prepared(Csv), null, []);
        Assert.Equal(3, again.Created);
        Assert.Equal(0, again.Skipped);
    }

    [Fact]
    public void Journal_is_pruned_when_catalog_caught_up_and_survives_restart()
    {
        var tiles = FirstLoad(tilesInCatalog: true);
        AutoPartsImportLog.SaveIfDirty();
        AutoPartsImportLog.ResetForTests(); // перезапуск: читается из файла
        Assert.NotNull(AutoPartsImportLog.Find(N("W 811/80"), "mann"));
        Assert.Equal("Mann", AutoPartsImportLog.BrandOf("srv-1"));

        // каталог догнал первый товар (появился артикул), второй ещё нет, третий пропал из каталога давно, четвёртый — только что
        tiles[0].Article = "W 811/80";
        AutoPartsImportLog.Prune(tiles.Take(2).ToList(), DateTime.UtcNow);
        Assert.Null(AutoPartsImportLog.Find(N("W 811/80"), "mann"));
        Assert.NotNull(AutoPartsImportLog.Find(N("0 451 103 092"), "bosch"));
        Assert.NotNull(AutoPartsImportLog.Find(N("BKR6E"), "ngk")); // третьего нет в каталоге, но создан только что — ещё едет
        AutoPartsImportLog.Prune(tiles.Take(2).ToList(), DateTime.UtcNow + AutoPartsImportLog.PendingWindow + TimeSpan.FromMinutes(1));
        Assert.Null(AutoPartsImportLog.Find(N("BKR6E"), "ngk")); // давно нет в каталоге — удалён на сервере
        Assert.Null(AutoPartsImportLog.BrandOf("srv-3"));
    }

    [Fact]
    public void Journal_is_per_company()
    {
        AutoPartsImportLog.Record("a-1", N("W 811/80"), "mann", "Mann");
        AutoPartsImportLog.CompanyIdOverride = "company-2";
        Assert.Null(AutoPartsImportLog.Find(N("W 811/80"), "mann"));
        AutoPartsImportLog.CompanyIdOverride = "company-1";
        Assert.NotNull(AutoPartsImportLog.Find(N("W 811/80"), "mann"));
    }
}
