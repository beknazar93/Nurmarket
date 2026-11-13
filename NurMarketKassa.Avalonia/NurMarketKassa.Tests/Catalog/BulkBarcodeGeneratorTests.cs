using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-12 (владелец: «а где генерация штрихкода, если их нет?»): массовая генерация штрихкодов — счётчик, пропуск весовых/комплектов, уникальность, пакеты по 20, отмена,
/// ошибки отдельных товаров, не затирает код, появившийся в карточке. Сервер и ожидание подменены (BulkBarcodeGenerator.*Override). Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("Wave5Database")]
public sealed class BulkBarcodeGeneratorTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();
    private readonly Dictionary<string, string> _server = new();
    private readonly List<string> _saves = new();
    private readonly List<int> _refreshSizes = new();

    public BulkBarcodeGeneratorTests()
    {
        CatalogCacheService.Products.Clear();
        BulkBarcodeGenerator.ReadBarcodeOverride = (id, _) => Task.FromResult<string?>(_server.GetValueOrDefault(id) ?? ""); // "" — карточка прочитана, штрихкода нет (null — не прочиталась)
        BulkBarcodeGenerator.SaveOverride = (id, code, _) =>
        {
            _saves.Add(id);
            _server[id] = code;
            return Task.FromResult<string?>(null);
        };
        BulkBarcodeGenerator.RefreshOverride = ids =>
        {
            _refreshSizes.Add(ids.Count);
            return Task.CompletedTask;
        };
        BulkBarcodeGenerator.DelayOverride = (_, _) => Task.CompletedTask;
    }

    public void Dispose()
    {
        BulkBarcodeGenerator.ReadBarcodeOverride = null;
        BulkBarcodeGenerator.SaveOverride = null;
        BulkBarcodeGenerator.RefreshOverride = null;
        BulkBarcodeGenerator.DelayOverride = null;
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    private static CatalogProductTileVm Product(string id, string? barcode = null, bool weighed = false) =>
        new(id, "Товар " + id, "10.00 сом", weighed) { Barcode = barcode, Unit = weighed ? "кг" : "шт" };

    private List<CatalogProductTileVm> Seed(int withoutCode, int withCode = 3)
    {
        var list = new List<CatalogProductTileVm>();
        for (var i = 0; i < withCode; i++)
            list.Add(Product("has" + i, "46000000000" + (10 + i)));
        for (var i = 0; i < withoutCode; i++)
            list.Add(Product("no" + i));
        CatalogCacheService.Products.AddRange(list);
        return list;
    }

    [Fact]
    public void CountMissing_CountsOnlyProductsWithoutAnyBarcode_AndIsFastOn15000()
    {
        var list = Seed(withoutCode: 20, withCode: 5);
        BulkBarcodeGenerator.CountMissing(list).Should().Be(20);

        var big = new List<CatalogProductTileVm>();
        for (var i = 0; i < 15000; i++)
            big.Add(Product("b" + i, i % 3 == 0 ? null : (4600000000000L + i).ToString()));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = BulkBarcodeGenerator.CountMissing(big);
        sw.Stop();
        n.Should().Be(5000);
        sw.ElapsedMilliseconds.Should().BeLessThan(50, "счётчик по каталогу в памяти — мгновенно");
    }

    [Fact]
    public void Classify_SkipsWeighedBundlesAndServices_WithReason()
    {
        var list = Seed(withoutCode: 3);
        var weighed = Product("w1", null, weighed: true);
        var bundle = Product("bundle1");
        bundle.IsBundle = true;
        var service = Product("svc1");
        service.Kind = "service";
        var all = list.Concat(new[] { weighed, bundle, service }).ToList();

        var (eligible, skipped) = BulkBarcodeGenerator.Classify(all);

        eligible.Select(p => p.Id).Should().BeEquivalentTo("no0", "no1", "no2");
        skipped.Select(s => s.Product.Id).Should().BeEquivalentTo("w1", "bundle1", "svc1");
        skipped.Should().OnlyContain(s => s.Reason.Length > 5);
    }

    [Fact]
    public async Task Run_CreatesUniqueValidCodes_InBatchesOf20_AndRefreshesCatalogPerBatch()
    {
        var list = Seed(withoutCode: 45, withCode: 4);
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);

        var progress = new List<int>();
        var result = await BulkBarcodeGenerator.RunAsync(eligible, new Progress<(int Done, int Total)>(p => progress.Add(p.Done)), "тест", CancellationToken.None);

        result.Created.Should().HaveCount(45);
        result.Failed.Should().BeEmpty();
        var codes = result.Created.Select(c => c.Code).ToList();
        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => c.Length == 13 && BarcodeGenerator.HasValidCheckDigit(c) && c[0] != '0' && c[0] != '2' && !c.StartsWith("470"));
        codes.Should().NotContain(list.Where(p => p.Id.StartsWith("has")).Select(p => p.Barcode!), "занятые коды каталога не повторяются");
        _server.Count.Should().Be(45);
        eligible.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Barcode), "в карточке в памяти код появился");
        _refreshSizes.Should().Equal(20, 20, 5);
        BulkBarcodeGenerator.CountMissing(list).Should().Be(0);
    }

    [Fact]
    public async Task Run_DoesNotOverwriteBarcode_ThatAppearedOnTheServer()
    {
        var list = Seed(withoutCode: 3);
        _server["no1"] = "9990001112223"; // кто-то уже завёл код в карточке
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);

        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None);

        result.Created.Select(c => c.Product.Id).Should().BeEquivalentTo("no0", "no2");
        result.Skipped.Should().ContainSingle(s => s.Product.Id == "no1");
        _saves.Should().NotContain("no1", "запись в карточку с уже появившимся кодом не отправляется");
        _server["no1"].Should().Be("9990001112223");
        list.Single(p => p.Id == "no1").Barcode.Should().Be("9990001112223", "локальная карточка подхватила настоящий код");
    }

    [Fact]
    public async Task Run_ErrorOnOneProduct_DoesNotStopTheBatch_AndIsReported()
    {
        var list = Seed(withoutCode: 5);
        BulkBarcodeGenerator.SaveOverride = (id, code, _) =>
        {
            if (id == "no2")
                return Task.FromResult<string?>("сервер отказал");
            _server[id] = code;
            return Task.FromResult<string?>(null);
        };
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);

        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None);

        result.Created.Should().HaveCount(4);
        result.Failed.Should().ContainSingle().Which.Product.Id.Should().Be("no2");
        list.Single(p => p.Id == "no2").Barcode.Should().BeNullOrEmpty("при ошибке код в карточке не появляется");
        BulkBarcodeGenerator.BuildLog(result).Should().Contain("сервер отказал").And.Contain("Товар no0 — ");
    }

    [Fact]
    public async Task Run_Cancel_StopsBetweenProducts_AndKeepsWhatWasDone()
    {
        var list = Seed(withoutCode: 30);
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);
        using var cts = new CancellationTokenSource();
        BulkBarcodeGenerator.SaveOverride = (id, code, _) =>
        {
            _server[id] = code;
            if (_server.Count == 7)
                cts.Cancel();
            return Task.FromResult<string?>(null);
        };

        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Created.Should().HaveCount(7);
        _refreshSizes.Sum().Should().Be(7, "созданные до отмены обновлены в каталоге");
    }

    [Fact]
    public async Task Run_UsesAtMostConfiguredRequestRate()
    {
        var list = Seed(withoutCode: 3);
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);
        var delays = new List<TimeSpan>();
        BulkBarcodeGenerator.DelayOverride = (d, _) => { delays.Add(d); return Task.CompletedTask; };
        var old = BulkBarcodeGenerator.RequestsPerSecond;
        BulkBarcodeGenerator.RequestsPerSecond = 5;
        try
        {
            await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None);
        }
        finally
        {
            BulkBarcodeGenerator.RequestsPerSecond = old;
        }
        delays.Should().HaveCount(3).And.OnlyContain(d => d >= TimeSpan.FromMilliseconds(199));
    }

    [Fact]
    public async Task Run_CardNotReadable_IsReportedAsError_AndNothingIsWritten()
    {
        // 2026-11-12 (аудит Fable, Л-7): карточка не прочиталась (500/403) — это не «штрихкода нет»
        var list = Seed(withoutCode: 3);
        BulkBarcodeGenerator.ReadBarcodeOverride = (id, _) => Task.FromResult<string?>(id == "no1" ? null : "");
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);

        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None);

        result.Failed.Should().ContainSingle().Which.Product.Id.Should().Be("no1");
        result.Created.Select(c => c.Product.Id).Should().BeEquivalentTo("no0", "no2");
        _saves.Should().NotContain("no1", "непрочитанная карточка не перезаписывается");
    }

    [Fact]
    public async Task Run_DuplicateRefusal_RetriesWithNewCode()
    {
        // 2026-11-12 (Л-18): товар заведён на другой кассе, код оказался занят на сервере — новый код и повтор
        var list = Seed(withoutCode: 2);
        var attempts = new Dictionary<string, List<string>>();
        BulkBarcodeGenerator.SaveOverride = (id, code, _) =>
        {
            if (!attempts.TryGetValue(id, out var l))
                attempts[id] = l = new List<string>();
            l.Add(code);
            if (id == "no0" && l.Count == 1)
                return Task.FromResult<string?>("Штрихкод уже существует у другого товара");
            _server[id] = code;
            return Task.FromResult<string?>(null);
        };
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);

        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None);

        result.Failed.Should().BeEmpty();
        result.Created.Should().HaveCount(2);
        attempts["no0"].Should().HaveCount(2).And.OnlyHaveUniqueItems();
        _server["no0"].Should().Be(attempts["no0"][1]);
    }

    [Fact]
    public async Task Run_ChangesCatalogCards_ThroughTheUiCallback()
    {
        // 2026-11-12 (Л-17): общие карточки каталога меняются через переданный вызов (в программе — поток интерфейса), а не напрямую из фонового потока
        var list = Seed(withoutCode: 4);
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);
        var calls = 0;
        var result = await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None, a => { calls++; a(); });
        result.Created.Should().HaveCount(4);
        calls.Should().Be(4);
    }

    [Fact]
    public async Task Run_SinkKeepsPartialResult_WhenSomethingBreaks()
    {
        var list = Seed(withoutCode: 6);
        var (eligible, _) = BulkBarcodeGenerator.Classify(list);
        var sink = new BulkBarcodeGenerator.RunResult();
        BulkBarcodeGenerator.DelayOverride = (_, _) => throw new InvalidOperationException("сбой после третьего");
        var act = async () => await BulkBarcodeGenerator.RunAsync(eligible, null, null, CancellationToken.None, null, sink);
        await act.Should().ThrowAsync<InvalidOperationException>();
        sink.Created.Should().HaveCount(1, "что успело — осталось в итоге");
    }
}
