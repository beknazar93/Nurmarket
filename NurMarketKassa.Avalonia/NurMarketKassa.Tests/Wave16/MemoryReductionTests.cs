using System.Collections.Concurrent;
using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using Xunit.Abstractions;

namespace NurMarketKassa.Tests.Wave16;

/// <summary>2026-11-06 (В16, «чтобы у нас памяти было меньше»): пул повторяющихся строк плиток каталога, предел кэша картинок по байтам, освобождение списка истории.</summary>
public sealed class MemoryReductionTests
{
    private readonly ITestOutputHelper _out;

    public MemoryReductionTests(ITestOutputHelper output) => _out = output;

    // Размер строки .NET в куче: заголовок 16 + длина 4 + символы по 2 + завершающий ноль 2, выравнивание до 8.
    private static long StringBytes(string? s) => s is null ? 0 : (22 + 2L * s.Length + 7) / 8 * 8;

    /// <summary>Плитка, как её строит чтение базы: каждое значение — НОВАЯ строка (reader.GetString), без общих экземпляров.</summary>
    private static CatalogProductTileVm DbTile(int i, List<string>? sources = null)
    {
        string Fresh(string v) { var f = new string(v.AsSpan()); sources?.Add(f); return f; }
        var vm = new CatalogProductTileVm(Guid.NewGuid().ToString(), "Товар номер " + i, $"{100 + i % 300}.00 сом", false, i % 14 == 0 ? "https://app.nurcrm.kg/media/products/" + i + ".webp" : null)
        {
            Barcode = (4600000000000L + i).ToString(),
            Category = Fresh("Напитки безалкогольные " + i % 39),
            Brand = Fresh("Бренд " + i % 24),
            Unit = Fresh(i % 9 == 0 ? "кг" : "шт"),
            Kind = Fresh(i % 50 == 0 ? "service" : "product"),
            Article = (10000 + i).ToString(),
            ProductCode = (20000 + i).ToString(),
            Quantity = i % 200,
        };
        vm.StockInfo = $"{i % 200} {vm.Unit}";
        return vm;
    }

    [Fact]
    public void StringPool_SameValueFromDifferentInstances_GivesOneInstance_AndKeepsValue()
    {
        var pool = new StringPool();
        var a = pool.Intern(new string("шт-w16".AsSpan()));
        var b = pool.Intern(new string("шт-w16".AsSpan()));
        ReferenceEquals(a, b).Should().BeTrue();
        b.Should().Be("шт-w16");
        pool.Intern(null).Should().BeNull();
        pool.Intern("").Should().BeSameAs("");
    }

    [Fact]
    public void StringPool_LongStrings_AreNotPooled_AndPoolIsBounded()
    {
        var pool = new StringPool(maxEntries: 100, maxLength: 64);
        var long1 = new string('x', 65);
        var long2 = new string('x', 65);
        ReferenceEquals(pool.Intern(long1), long1).Should().BeTrue("длинные значения (описания) в пул не кладём");
        ReferenceEquals(pool.Intern(long2), long2).Should().BeTrue();

        for (var i = 0; i < 600; i++)
            pool.Intern("w16-bound-" + i).Should().Be("w16-bound-" + i, "значение не меняется, даже когда пул полон");
        pool.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void StringPool_IsThreadSafe_OneInstancePerValue()
    {
        var pool = new StringPool();
        var seen = new ConcurrentBag<string>();
        Parallel.For(0, 4000, i => seen.Add(pool.Intern(new string(("потокобезопасно-" + i % 7).AsSpan()))!));
        seen.Distinct(ReferenceEqualityComparer.Instance as IEqualityComparer<string>).Count().Should().Be(7);
    }

    [Fact]
    public void Tile_PooledFields_ShareInstances_ValuesUnchanged_SearchKeyUnchanged()
    {
        var t1 = DbTile(1);
        var t2 = DbTile(10);
        t1.Kind.Should().Be("product");
        ReferenceEquals(t1.Kind, t2.Kind).Should().BeTrue();
        ReferenceEquals(t1.Unit, t2.Unit).Should().BeTrue();
        t1.Unit.Should().Be("шт");
        t1.Category.Should().Be("Напитки безалкогольные 1");
        t1.PriceLine.Should().Be("101.00 сом");
        t1.StockInfo.Should().Be("1 шт");
        t1.Brand.Should().Be("Бренд 1");

        var plain = new CatalogProductTileVm("id-1", "Асу вода", "55.00 сом", false) { Barcode = "123", Category = "Напитки", Brand = "Асу" };
        plain.SearchKey.Should().Be(SearchMatch.Key("Асу вода", "id-1", "123", null, null, "Напитки", "Асу", null, null));
        plain.Category = null;
        plain.Category.Should().BeNull();
        plain.Unit = "кг";
        plain.IsWeighted.Should().BeTrue();
    }

    [Fact]
    public void Catalog15000Tiles_MemoryBeforeAfter_PoolSavesAndTotalIsBounded()
    {
        const int n = 15_000;
        var sources = new List<string>(n * 6);
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var tiles = new List<CatalogProductTileVm>(n);
        for (var i = 0; i < n; i++)
            tiles.Add(DbTile(i, sources));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        // «До»: каждая плитка держала бы свою копию каждого повторяющегося значения (то, что вернуло чтение базы).
        long before = sources.Sum(StringBytes);
        // «После»: плитки держат только уникальные экземпляры.
        var unique = new HashSet<string>(ReferenceEqualityComparer.Instance as IEqualityComparer<string>);
        long after = 0;
        foreach (var t in tiles)
            foreach (var s in new[] { t.Category, t.Brand, t.Unit, t.Kind })
                if (s is not null && unique.Add(s))
                    after += StringBytes(s);

        _out.WriteLine($"15 000 плиток: выделено {allocated / 1024 / 1024.0:0.0} МБ ({allocated / n} байт на плитку со всем временным), повторяющиеся строки: было {before / 1024 / 1024.0:0.00} МБ, стало {after / 1024.0:0.0} КБ, экономия {(before - after) / 1024 / 1024.0:0.00} МБ.");
        // Для проверки без подробного вывода xunit: те же числа лежат в файле во временной папке.
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "w16-memory-report.txt"), $"{DateTime.Now:u} tiles={n} allocatedPerTile={allocated / n} stringsBefore={before} stringsAfter={after} saved={before - after}"); }
        catch (IOException) { /* отчёт — только для чтения человеком */ }
        (before - after).Should().BeGreaterThan(2_000_000, "общие экземпляры вместо 15 000 копий каждого значения");
        after.Should().BeLessThan(before / 50);
        allocated.Should().BeLessThan(80L * 1024 * 1024, "порог с запасом: 15 000 плиток вместе с временными строками");
        tiles.Count.Should().Be(n);
    }

    [Fact]
    public void ByteBudgetLru_EvictsOldest_WhenBytesExceeded_NotOnlyCount()
    {
        var lru = new ByteBudgetLru(maxCount: 100, maxBytes: 10_000);
        lru.Touch("a", 4000).Should().BeEmpty();
        lru.Touch("b", 4000).Should().BeEmpty();
        var evicted = lru.Touch("c", 4000);
        evicted.Should().Equal("a");
        lru.TotalBytes.Should().Be(8000);
        lru.Count.Should().Be(2);

        lru.Touch("b", 4000).Should().BeEmpty("повторное обращение просто освежает ключ");
        lru.Touch("d", 4000).Should().Equal("c");
        lru.Contains("b").Should().BeTrue();
        lru.Contains("c").Should().BeFalse();
    }

    [Fact]
    public void ByteBudgetLru_KeepsNewest_EvenIfItAloneIsOverBudget_AndCountLimitStillWorks()
    {
        var lru = new ByteBudgetLru(maxCount: 3, maxBytes: 1000);
        lru.Touch("big", 5000).Should().BeEmpty("единственная картинка остаётся, пока её показывают");
        lru.Touch("small1", 10).Should().Equal("big");

        var byCount = new ByteBudgetLru(maxCount: 3, maxBytes: long.MaxValue / 2);
        foreach (var k in new[] { "1", "2", "3" })
            byCount.Touch(k, 1);
        byCount.Touch("4", 1).Should().Equal("1");
        byCount.Trim(1).Should().Equal("2", "3");
        byCount.Count.Should().Be(1);
        byCount.TotalBytes.Should().Be(1);
        byCount.Trim(0).Should().Equal("4");
    }

    [Fact]
    public void ThumbBudget_160Thumbs_Of320x1000_StayUnder48Mb()
    {
        // Худший случай: узкое высокое старое фото, расшифрованное в ширину 320 px: 320 × 1000 × 4 ≈ 1,2 МБ. По счётчику (160) это 195 МБ; по байтам — не больше 48 МБ.
        var lru = new ByteBudgetLru(160, 48L * 1024 * 1024);
        for (var i = 0; i < 160; i++)
            lru.Touch("p" + i, 320L * 1000 * 4);
        lru.TotalBytes.Should().BeLessThanOrEqualTo(48L * 1024 * 1024);
        lru.Count.Should().BeLessThan(45);
        _out.WriteLine($"Кэш миниатюр 320×1000: {lru.Count} шт. / {lru.TotalBytes / 1024 / 1024} МБ вместо 160 шт. / {160L * 320 * 1000 * 4 / 1024 / 1024} МБ.");
    }

    [Fact]
    public void DeferredJsonList_Release_DropsMemory_AndReloadsFromFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nm-w16-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var list = new DeferredJsonList<Row>(() => path, 30);
            list.Store([new Row("1", "один")]);
            list.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();
            list.Release().Should().BeTrue();
            list.Load().Should().ContainSingle(r => r.Id == "1", "после освобождения список читается из файла");
        }
        finally
        {
            try { File.Delete(path); } catch { /* временный файл */ }
        }
    }

    public sealed record Row(string Id, string Text);
}
