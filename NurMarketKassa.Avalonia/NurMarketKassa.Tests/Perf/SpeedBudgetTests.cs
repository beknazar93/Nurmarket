using System.Diagnostics;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Perf;

/// <summary>2026-11-13 (Л-12 / Н-5): замеры скорости основных действий кассира с пределами-«храповиками». Предел = базовая цифра этого ПК × 3–5 (запас на загруженный
/// ПК и параллельные сборки): тест падает не от шума, а когда стало ЗАМЕТНО медленнее. Цифры каждого прогона дописываются в %TEMP%\nurmarket-tests\speed-budget.txt.
/// Сознательно ужесточить предел — после ускорения (базовая цифра записана в комментарии у теста). Масштаб пределов для медленного ПК: NURMARKET_SPEED_FACTOR=2.
/// Окна (запуск, оплата, склад, вкладки владельца) меряет проект UiTests — SpeedBudgetUiTests.</summary>
[Collection("Wave5Database")]
public sealed class SpeedBudgetTests
{
    private const int CatalogSize = 15_000;

    private sealed class FakeCache : ICatalogCacheService
    {
        public List<CatalogProductTileVm> Items { get; } = new();

        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CatalogSyncResult { Success = true });

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => Items;
    }

    private sealed class Online : IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static List<CatalogProductTileVm> LongNamedCatalog(int count)
    {
        var list = new List<CatalogProductTileVm>(count);
        for (var i = 0; i < count; i++)
        {
            var tile = CatalogTestHelper.CreateTile(
                "spd" + i,
                $"Макаронные изделия «Макфа» спагетти высший сорт длинное название партия {i:D5} упаковка 450 г",
                "46" + i.ToString("D11"),
                10 + i % 90);
            tile.Category = "Категория " + i % 40;
            tile.Brand = "Бренд " + i % 25;
            list.Add(tile);
        }
        return list;
    }

    /// <summary>Поиск по каталогу 15 000 товаров (то, что кассир видит после набора букв: отбор + порядок). Базовая цифра (13.11.2026, этот ПК): см. speed-budget.txt.</summary>
    [Theory]
    [InlineData("спагетти", "поиск по слову (совпадает почти везде)", 2000)]
    [InlineData("партия 07777", "поиск по номеру (одно совпадение)", 2000)]
    [InlineData("zzqqxx", "ничего не найдено (запасной поиск по раскладке/транслиту — самый долгий путь)", 4000)]
    public void CatalogSearch_On15kProducts_StaysWithinBudget(string query, string what, int limitMs)
    {
        var all = LongNamedCatalog(CatalogSize);
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        var compute = typeof(CatalogPanelViewModel).GetMethod("ComputeFiltered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object Run() => compute.Invoke(vm, [all, query])!;

        Run(); // прогрев JIT
        var median = SpeedProbe.MedianMs(5, () => Run());
        SpeedProbe.Check("Поиск 15 тыс. товаров: " + what, median, limitMs);
    }

    /// <summary>Скан → строка в чеке на уровне корзины: чек из 30 строк, добавляется 31-я (медиана из 15 добавлений разных товаров). Предел «окно ≤ 0,1 с» берём по Н-5.</summary>
    [Fact]
    public void ScanToCartLine_OnThirtyLineReceipt_StaysWithinBudget()
    {
        using var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        for (var i = 0; i < 30; i++)
            cart.AddItem(CartTestHelper.CreateProduct("spd-line-" + i, "Хлеб «Бородинский» нарезной длинное название " + i, 40 + i), 1);

        var extra = Enumerable.Range(0, 16).Select(i => CartTestHelper.CreateProduct("spd-extra-" + i, "Товар скана " + i, 55 + i)).ToList();
        cart.AddItem(extra[0], 1); // прогрев
        var times = new List<double>();
        for (var i = 1; i < extra.Count; i++)
        {
            var w = Stopwatch.StartNew();
            cart.AddItem(extra[i], 1);
            w.Stop();
            times.Add(w.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        cart.LineCount.Should().Be(46);
        SpeedProbe.Check("Скан → строка в чеке (чек 30 строк, добавление товара в корзину)", times[times.Count / 2], 300);
    }

    /// <summary>Тот же товар сканируют ещё раз (количество +1 в существующей строке): самый частый скан у кассира.</summary>
    [Fact]
    public void RepeatScanSameProduct_OnThirtyLineReceipt_StaysWithinBudget()
    {
        using var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var products = Enumerable.Range(0, 30).Select(i => CartTestHelper.CreateProduct("spd-rep-" + i, "Молоко «Сузбек» 1 л партия " + i, 80 + i)).ToList();
        foreach (var p in products)
            cart.AddItem(p, 1);
        cart.AddItem(products[10], 1); // прогрев
        var median = SpeedProbe.MedianMs(15, () => cart.AddItem(products[10], 1));
        SpeedProbe.Check("Повторный скан того же товара (чек 30 строк)", median, 300);
    }

    /// <summary>Поиск штрихкода и текста в индексе базы (LocalProductRepository) на 15 000 товаров — путь «скан» до корзины.</summary>
    [Fact]
    public void RepositoryBarcodeAndTextLookup_On15kProducts_StaysWithinBudget()
    {
        var repo = LocalProductRepository.Instance;
        repo.EnsureSchema();
        try
        {
            repo.SyncReplaceAllWithDiff(LongNamedCatalog(CatalogSize));
            repo.TryGetTileByBarcode("46" + 7777.ToString("D11")).Should().NotBeNull();
            var byBarcode = SpeedProbe.MedianMs(15, () => repo.TryGetTileByBarcode("46" + 7777.ToString("D11")));
            SpeedProbe.Check("Поиск товара по штрихкоду в базе (15 тыс.)", byBarcode, 100);
            repo.SearchFullCatalogText("партия 07777");
            var byText = SpeedProbe.MedianMs(5, () => repo.SearchFullCatalogText("партия 07777"));
            SpeedProbe.Check("Полнотекстовый поиск в базе (15 тыс.)", byText, 2000);
        }
        finally
        {
            repo.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());
        }
    }
}

/// <summary>2026-11-13 (Л-12): общий помощник замеров — медиана, предел с масштабом NURMARKET_SPEED_FACTOR и запись в speed-budget.txt.</summary>
internal static class SpeedProbe
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "speed-budget.txt");

    public static double Factor =>
        double.TryParse(Environment.GetEnvironmentVariable("NURMARKET_SPEED_FACTOR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;

    public static double MedianMs(int runs, Action action)
    {
        var times = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var w = Stopwatch.StartNew();
            action();
            w.Stop();
            times.Add(w.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    public static void Check(string what, double ms, double limitMs)
    {
        var limit = limitMs * Factor;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {what} | {ms:0.0} мс | предел {limit:0} мс\n");
        }
        catch { /* журнал замеров не должен ронять тест */ }
        ms.Should().BeLessThan(limit, "«{0}» занял {1:0.0} мс при пределе {2:0} мс — стало заметно медленнее (журнал: {3})", what, ms, limit, LogPath);
    }
}
