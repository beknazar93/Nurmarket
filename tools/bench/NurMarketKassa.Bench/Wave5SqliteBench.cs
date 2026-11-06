using System.Reflection;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Bench;

/// <summary>2026-11-06: В5 — замеры «до/после» по SQLite на КОПИИ базы (не на настоящей): журнал чеков (К-1), известные чеки (К-2),
/// история закупок пачкой (Л-3, Л-4), избранное и штрихкод (индексы, шаг 30), списания, отпечатки каталога (24.1).
/// Копия берётся через sqlite3 backup API (BackupDatabase) из файла NURMARKET_PERF_DB (по умолчанию «большой магазин»:
/// 15 тыс. товаров, 300 тыс. строк продаж, 99 тыс. строк истории закупок, 20 тыс. списаний). Папка данных — временная (см. Program.cs).</summary>
[MemoryDiagnoser]
public class Wave5SqliteBench
{
    internal static readonly object Gate = new();
    internal static bool Prepared;
    internal const string CompanyId = "4b759359-2239-4e2a-a07e-f0e42a4dd19d";

    private string[] _saleIds300 = Array.Empty<string>();
    private string[] _productIds300 = Array.Empty<string>();
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private IReadOnlyList<CatalogProductTileVm> _tiles = Array.Empty<CatalogProductTileVm>();

    /// <summary>Общая подготовка для всех классов замеров: копия базы в папку данных и «текущая компания».</summary>
    internal static void PrepareDatabase()
    {
        lock (Gate)
        {
            if (Prepared)
                return;
            var source = Environment.GetEnvironmentVariable("NURMARKET_PERF_DB") ?? @"C:\Users\User\NurMarket-test\perf-db-copy\pos_scaled.db";
            if (!File.Exists(source))
                throw new FileNotFoundException("Нет копии базы для замеров: " + source);
            var target = Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "data", "pos_local.db");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var src = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (var dst = new SqliteConnection($"Data Source={target};Pooling=False"))
            {
                src.Open();
                dst.Open();
                src.BackupDatabase(dst); // sqlite3 backup API
            }
            var dto = new NurMarketKassa.Models.CompanyDto { Id = CompanyId, Name = "perf-copy" };
            typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, dto);
            // Шаг 30: первое открытие копии строит индексы (user_version 0 -> 1); повторное открытие (версия уже стоит) их не трогает — разница и есть цена миграции.
            var first = System.Diagnostics.Stopwatch.StartNew();
            _ = DatabaseService.Instance;
            var firstMs = first.Elapsed.TotalMilliseconds;
            var second = System.Diagnostics.Stopwatch.StartNew();
            DatabaseService.Instance.ReopenAfterAccountSwitch();
            Console.WriteLine($"[Wave5] открытие базы 176 МБ: первое (с миграцией индексов) {firstMs:F0} мс, повторное (версия схемы уже стоит) {second.Elapsed.TotalMilliseconds:F0} мс");
            Prepared = true;
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        PrepareDatabase();
        var started = DateTime.UtcNow;
        var db = DatabaseService.Instance; // здесь же (после обновления) строятся индексы
        Console.WriteLine($"[Wave5] открытие базы и схема: {(DateTime.UtcNow - started).TotalMilliseconds:F0} мс");
        _repo.EnsureSchema();
        _saleIds300 = Enumerable.Range(0, 300).Select(i => $"sale-{i * 300:D7}").ToArray();
        using (var c = db.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT product_id FROM ProductPurchaseHistory LIMIT 300;";
            var ids = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                ids.Add(r.GetString(0));
            _productIds300 = ids.ToArray();
        }
        _tiles = _repo.LoadAllTiles();
        SeedOfflineQueue();
    }

    /// <summary>5000 выгруженных и 20 ждущих чеков (по ~1 КБ JSON): очередь кассы, проработавшей несколько месяцев.</summary>
    private static void SeedOfflineQueue()
    {
        var cart = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 12).Select(i => "{\"product_id\":\"" + Guid.NewGuid() + "\",\"name\":\"Товар " + i + "\",\"qty\":\"2\",\"price\":\"123.45\"}")) + "]}";
        var list = new List<OfflineSaleEntry>();
        for (var i = 0; i < 5020; i++)
            list.Add(new OfflineSaleEntry { Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.Now.AddMinutes(-i), Status = i < 20 ? OfflineSaleEntry.PendingSync : OfflineSaleEntry.Synced, CartJson = cart, PaymentMethod = "cash" });
        OfflineDatabase.SaveAll(list);
    }

    [Benchmark(Description = "К-1: строки 300 чеков (LoadSoldLinesBySaleIds)")]
    public int SoldLines_300Sales() => SoldLineItemsStore.LinesBySale(_saleIds300).Count;

    [Benchmark(Description = "К-2: известные номера чеков (LoadKnownSaleIds)")]
    public int KnownSaleIds() => SoldLineItemsStore.KnownSaleIds().Count;

    [Benchmark(Description = "Л-3: история закупок 300 товаров (по одному)")]
    public int PurchaseHistory_Loop300()
    {
        var n = 0;
        foreach (var id in _productIds300)
            n += PurchaseReceivingService.Instance.LoadLocalHistory(id).Count;
        return n;
    }

    [Benchmark(Description = "Л-3: история закупок 300 товаров (одним запросом)")]
    public int PurchaseHistory_Many300() => PurchaseReceivingService.Instance.LoadLocalHistoryMany(_productIds300).Count;

    [Benchmark(Description = "24.2: очередь офлайн-продаж, число выгруженных (LoadAll + Count)")]
    public int Offline_SyncedCount_ViaLoadAll() => OfflinePendingSalesStore.LoadAll().Count(s => s.Status == OfflineSaleEntry.Synced);

    [Benchmark(Description = "24.2: очередь офлайн-продаж, число выгруженных (COUNT в SQL)")]
    public int Offline_SyncedCount_Sql() => OfflinePendingSalesStore.SyncedCount;

    [Benchmark(Description = "24.2: очередь офлайн-продаж, невыгруженные (LoadAll + отсев)")]
    public int Offline_Unsynced_ViaLoadAll() => OfflinePendingSalesStore.LoadAll().Count(s => s.Status != OfflineSaleEntry.Synced);

    [Benchmark(Description = "24.2: очередь офлайн-продаж, невыгруженные (LoadAllUnsynced)")]
    public int Offline_Unsynced_Sql() => OfflinePendingSalesStore.LoadAllUnsynced().Count;

    [Benchmark(Description = "И-1: избранные плитки (LoadFavoriteTiles)")]
    public int FavoriteTiles() => _repo.LoadFavoriteTiles().Count;

    [Benchmark(Description = "И-1: GetFavoriteIds")]
    public int FavoriteIds() => _repo.GetFavoriteIds().Count;

    [Benchmark(Description = "И-3: неизвестный штрихкод с буквами")]
    public object? BarcodeUnknownWithLetters() => _repo.TryGetTileByBarcode("ZZ-NOT-FOUND-77");

    [Benchmark(Description = "И-2: последние 200 списаний")]
    public int WriteOffs_Last200() => DatabaseService.Instance.LoadWriteOffHistory(200).Count;

    [Benchmark(Description = "И-2: списания одного товара (по названию)")]
    public int WriteOffs_ByProduct() => DatabaseService.Instance.LoadWriteOffsForProduct(_tiles[100].Title).Count;

    [Benchmark(Description = "И-2: отчёт по списаниям (500 строк)")]
    public int WriteOffs_Report() => DatabaseService.Instance.LoadWriteOffReport(500).Count;

    [Benchmark(Description = "24.1: SyncReplaceAllWithDiff без изменений (15 тыс. товаров)")]
    public (int, int, int) CatalogDiffNoChange() => _repo.SyncReplaceAllWithDiff(_tiles);
}

/// <summary>2026-11-06: В5 25.4 — чтение справочника, пока идёт запись продаж. «Писатель» в фоне держит замок записи базы по 20 мс с паузой 5 мс (занят ≈80 % времени).
/// Старый способ (WithConnection — замок ЗАПИСИ даже на чтение) ждёт писателя; новый (WithReadConnection) — нет. Те же SQL и данные.</summary>
[MemoryDiagnoser]
public class Wave5ContentionBench
{
    private string _productId = "";
    private CancellationTokenSource? _stop;
    private Task? _writer;

    [GlobalSetup]
    public void Setup()
    {
        Wave5SqliteBench.PrepareDatabase();
        var db = DatabaseService.Instance;
        using (var c = db.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT product_id FROM ProductPurchaseHistory LIMIT 1;";
            _productId = (string)cmd.ExecuteScalar()!;
        }
        PurchaseReceivingService.Instance.LoadLocalHistory(_productId); // создаёт таблицу, если её ещё нет
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _writer = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                db.WithConnection(_ => Thread.Sleep(20));
                Thread.Sleep(5);
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stop?.Cancel();
        try { _writer?.Wait(2000); } catch { /* остановка фонового писателя */ }
    }

    private int Read(Action<Action<Microsoft.Data.Sqlite.SqliteConnection>> run)
    {
        var n = 0;
        run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at, quantity FROM ProductPurchaseHistory WHERE product_id = $id ORDER BY at DESC;";
            command.Parameters.AddWithValue("$id", _productId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                n++;
        });
        return n;
    }

    [Benchmark(Baseline = true, Description = "25.4: чтение через WithConnection (замок записи), писатель занят")]
    public int Read_WriteLock() => Read(a => DatabaseService.Instance.WithConnection(a));

    [Benchmark(Description = "25.4: чтение через WithReadConnection (замок чтения), писатель занят")]
    public int Read_ReadLock() => Read(a => DatabaseService.Instance.WithReadConnection(a));
}

/// <summary>2026-11-06: В5 Л-4 — запись истории закупок прихода из 50 строк. Отдельный класс: строки, добавленные замером, удаляются после каждой итерации,
/// чтобы не раздувать историю для соседних замеров (чтение истории зависит от числа строк у товара).</summary>
[MemoryDiagnoser]
public class Wave5AppendBench
{
    private ReceivingLineVm[] _lines50 = Array.Empty<ReceivingLineVm>();

    [GlobalSetup]
    public void Setup()
    {
        Wave5SqliteBench.PrepareDatabase();
        var db = DatabaseService.Instance;
        PurchaseReceivingService.Instance.LoadLocalHistory("none"); // создаёт таблицу, если её нет
        var ids = new List<string>();
        using (var c = db.OpenConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT product_id FROM ProductPurchaseHistory LIMIT 50;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                ids.Add(r.GetString(0));
        }
        _lines50 = ids.Select((id, i) => new ReceivingLineVm { ProductId = id, ProductName = "Товар приход " + i, Barcode = "47000000" + i, Unit = "шт", Quantity = 3, PurchasePrice = 10, SalePrice = 15 }).ToArray();
    }

    [IterationCleanup]
    public void DeleteBenchRows()
    {
        DatabaseService.Instance.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM ProductPurchaseHistory WHERE receipt_id = 'bench-receipt';";
            cmd.ExecuteNonQuery();
        });
    }

    [Benchmark(Baseline = true, Description = "Л-4: приход 50 строк, запись истории (по одной)")]
    public void AppendHistory_50_OneByOne()
    {
        var m = typeof(PurchaseReceivingService).GetMethod("AppendHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var line in _lines50)
            m.Invoke(PurchaseReceivingService.Instance, new object?[] { line, "Поставщик", "Кассир", "bench-receipt" });
    }

    [Benchmark(Description = "Л-4: приход 50 строк, запись истории (одной транзакцией)")]
    public void AppendHistory_50_Batch()
    {
        var m = typeof(PurchaseReceivingService).GetMethod("AppendHistoryBatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        m.Invoke(PurchaseReceivingService.Instance, new object?[] { _lines50, "Поставщик", "Кассир", "bench-receipt", null });
    }
}
