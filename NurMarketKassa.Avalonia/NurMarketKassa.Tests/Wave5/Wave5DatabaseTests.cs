using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave5;

/// <summary>Отдельная коллекция БЕЗ параллельного запуска: тесты подменяют «текущую компанию» (статическое CompanyInfoService.LastCompany) и пишут в общую базу временной папки.</summary>
[CollectionDefinition("Wave5Database", DisableParallelization = true)]
public sealed class Wave5DatabaseCollection
{
}

/// <summary>2026-11-06: В5 — SQLite: индексы шага 30 и версия схемы (миграция на пустой, старой и повторно открытой базе), пакетное чтение строк чеков (К-1),
/// известные чеки (К-2), компания параметром (Б-5), история закупок пачкой (Л-3, Л-4), очередь офлайн-продаж (24.2), чтение без замка записи (25.4).
/// Всё — во временной папке данных (TestDataRoot), настоящая база кассы не читается и не пишется.</summary>
[Collection("Wave5Database")]
public sealed class Wave5DatabaseTests : IDisposable
{
    private const string CompanyA = "w5-company-a";
    private const string CompanyB = "w5-company-b";

    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;
    private readonly List<string> _saleIds = new();

    public Wave5DatabaseTests()
    {
        DatabaseService.Instance.EnsureSchema();
        LocalProductRepository.Instance.EnsureSchema(); // включает WAL, как в настоящей кассе
        SetCompany(CompanyA);
    }

    public void Dispose()
    {
        using (var connection = DatabaseService.Instance.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM SoldLineItems WHERE sale_id LIKE 'w5-%' OR company_id LIKE 'w5-%' OR company_id LIKE '%o''brien%';"
                + "DELETE FROM ProductPurchaseHistory WHERE product_id LIKE 'w5-%';";
            try { command.ExecuteNonQuery(); } catch (SqliteException) { /* таблицы истории закупок может ещё не быть */ }
        }
        OfflineDatabase.SaveAll(Array.Empty<OfflineSaleEntry>());
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, _previousCompany);
    }

    private static void SetCompany(string? id) =>
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, id is null ? null : new CompanyDto { Id = id, Name = "w5" });

    private static (string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt) Line(string name, double qty = 1, double price = 10) =>
        ("w5-" + name, name, qty, price, DateTime.UtcNow);

    private static HashSet<string> IndexNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Plan(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (reader.Read())
            sb.AppendLine(reader.GetString(3));
        return sb.ToString();
    }

    // ───────── шаг 30: индексы и версия схемы ─────────

    [Fact]
    public void Fresh_database_gets_perf_indexes_and_schema_version()
    {
        using var connection = DatabaseService.Instance.OpenConnection();
        var names = IndexNames(connection);
        foreach (var (name, _) in DatabaseService.PerfIndexes)
            names.Should().Contain(name);
        DatabaseService.ReadUserVersion(connection).Should().Be(DatabaseService.PerfIndexesSchemaVersion);
    }

    [Fact]
    public void Reopen_after_account_switch_keeps_the_schema_and_does_not_fail()
    {
        DatabaseService.Instance.ReopenAfterAccountSwitch();
        using var connection = DatabaseService.Instance.OpenConnection();
        IndexNames(connection).Should().Contain("idx_products_favorite_name");
        DatabaseService.ReadUserVersion(connection).Should().Be(DatabaseService.PerfIndexesSchemaVersion);
    }

    private static SqliteConnection OldStyleDatabase(out string path)
    {
        path = Path.Combine(TestPaths(), "old-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Products (id TEXT PRIMARY KEY, barcode TEXT, name TEXT NOT NULL, is_favorite INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE WriteOffHistory (id INTEGER PRIMARY KEY AUTOINCREMENT, product_id TEXT, product_name TEXT, quantity REAL, reason TEXT, created_at TEXT, company_id TEXT);
            INSERT INTO Products(id, barcode, name, is_favorite) VALUES ('p1', 'AB123', 'Хлеб', 1), ('p2', '4700000000011', 'Молоко', 0), ('p3', NULL, 'Сыр', 0);
            INSERT INTO WriteOffHistory(product_id, product_name, quantity, reason, created_at, company_id) VALUES ('p2', 'Молоко', 2, 'брак', '2026-11-01T10:00:00Z', 'c1');
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static string TestPaths()
    {
        var dir = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "wave5");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Migration_on_an_old_database_builds_indexes_keeps_data_and_is_repeatable()
    {
        using var connection = OldStyleDatabase(out _);
        DatabaseService.ReadUserVersion(connection).Should().Be(0);

        DatabaseService.EnsurePerfIndexes(connection);

        var names = IndexNames(connection);
        foreach (var (name, _) in DatabaseService.PerfIndexes)
            names.Should().Contain(name);
        DatabaseService.ReadUserVersion(connection).Should().Be(DatabaseService.PerfIndexesSchemaVersion);
        Count(connection, "Products").Should().Be(3);
        Count(connection, "WriteOffHistory").Should().Be(1);

        // повторный запуск: ничего не ломает и не дублирует
        var before = IndexNames(connection).Count;
        DatabaseService.EnsurePerfIndexes(connection);
        IndexNames(connection).Count.Should().Be(before);
        Count(connection, "Products").Should().Be(3);

        // версия уже стоит — удалённый вручную индекс (откат) сам не возвращается
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX idx_writeoff_company_name;";
            drop.ExecuteNonQuery();
        }
        DatabaseService.EnsurePerfIndexes(connection);
        IndexNames(connection).Should().NotContain("idx_writeoff_company_name");

        // а при сбросе версии (PRAGMA user_version = 0) — достраивается
        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = "PRAGMA user_version = 0;";
            reset.ExecuteNonQuery();
        }
        DatabaseService.EnsurePerfIndexes(connection);
        IndexNames(connection).Should().Contain("idx_writeoff_company_name");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Migration_does_not_stamp_the_version_when_an_index_cannot_be_built_and_retries_later()
    {
        var path = Path.Combine(TestPaths(), "partial-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Products (id TEXT PRIMARY KEY, barcode TEXT, name TEXT NOT NULL, is_favorite INTEGER NOT NULL DEFAULT 0);"; // таблицы списаний нет
            create.ExecuteNonQuery();
        }

        DatabaseService.EnsurePerfIndexes(connection);

        var names = IndexNames(connection);
        names.Should().Contain("idx_products_favorite_name").And.Contain("idx_products_barcode_nocase");
        names.Should().NotContain("idx_writeoff_company_created");
        DatabaseService.ReadUserVersion(connection).Should().Be(0, "версия повышается только когда построены все индексы");

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE WriteOffHistory (id INTEGER PRIMARY KEY, product_name TEXT, created_at TEXT, company_id TEXT);";
            create.ExecuteNonQuery();
        }
        DatabaseService.EnsurePerfIndexes(connection);
        DatabaseService.ReadUserVersion(connection).Should().Be(DatabaseService.PerfIndexesSchemaVersion);
        IndexNames(connection).Should().Contain("idx_writeoff_company_name");
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Hot_queries_use_the_new_indexes()
    {
        using var connection = OldStyleDatabase(out _);
        DatabaseService.EnsurePerfIndexes(connection);

        Plan(connection, "SELECT id FROM Products WHERE is_favorite = 1 ORDER BY name COLLATE NOCASE LIMIT 12;")
            .Should().Contain("idx_products_favorite_name");
        Plan(connection, "SELECT id FROM Products WHERE is_favorite = 1;")
            .Should().Contain("idx_products_favorite_name");
        Plan(connection, "SELECT id FROM Products WHERE barcode = $v COLLATE NOCASE LIMIT 1;", ("$v", "ZZ-1"))
            .Should().Contain("idx_products_barcode_nocase");
        Plan(connection, "SELECT product_name FROM WriteOffHistory WHERE 1=1 AND company_id = $c ORDER BY created_at DESC LIMIT $l;", ("$c", "c1"), ("$l", 200))
            .Should().Contain("idx_writeoff_company_created");
        Plan(connection, "SELECT quantity FROM WriteOffHistory WHERE product_name = $n COLLATE NOCASE AND company_id = $c ORDER BY created_at DESC;", ("$n", "Молоко"), ("$c", "c1"))
            .Should().Contain("idx_writeoff_company_name");
        SqliteConnection.ClearAllPools();
    }

    // ───────── шаг 3: К-1, К-2, Б-5 ─────────

    [Fact]
    public void LoadSoldLinesBySaleIds_reads_in_batches_keeps_line_order_and_ignores_other_companies()
    {
        // 1010 чеков: три пачки по 500 (500 + 500 + 10)
        for (var i = 0; i < 1010; i++)
        {
            var saleId = $"w5-sale-{i:D4}";
            _saleIds.Add(saleId);
            DatabaseService.Instance.AppendSoldLineItems(new[] { Line("a" + i, 1, 10), Line("b" + i, 2, 20), Line("c" + i, 3, 30) }, "local", saleId);
        }
        // тот же номер чека у ЧУЖОЙ компании не должен подмешаться
        SetCompany(CompanyB);
        DatabaseService.Instance.AppendSoldLineItems(new[] { Line("foreign", 9, 99) }, "local", "w5-sale-0001");
        SetCompany(CompanyA);

        var lines = DatabaseService.Instance.LoadSoldLinesBySaleIds(_saleIds.Append("w5-sale-unknown").ToList());

        lines.Count.Should().Be(1010, "неизвестный номер в результат не попадает");
        lines["w5-sale-0001"].Select(l => l.ProductName).Should().Equal("a1", "b1", "c1");
        lines["w5-sale-0001"][1].Quantity.Should().Be(2);
        lines["w5-sale-0001"][1].UnitPrice.Should().Be(20);
        lines["w5-sale-1009"].Count.Should().Be(3);
        lines.Should().NotContainKey("w5-sale-unknown");
    }

    [Fact]
    public void LoadSoldLinesBySaleIds_handles_empty_and_blank_input()
    {
        DatabaseService.Instance.LoadSoldLinesBySaleIds(Array.Empty<string>()).Should().BeEmpty();
        DatabaseService.Instance.LoadSoldLinesBySaleIds(new[] { "", "w5-nothing" }).Should().BeEmpty();
    }

    [Fact]
    public void LoadKnownSaleIds_returns_only_the_current_company()
    {
        DatabaseService.Instance.AppendSoldLineItems(new[] { Line("x") }, "local", "w5-known-a");
        SetCompany(CompanyB);
        DatabaseService.Instance.AppendSoldLineItems(new[] { Line("y") }, "local", "w5-known-b");

        SetCompany(CompanyA);
        var a = DatabaseService.Instance.LoadKnownSaleIds();
        a.Should().Contain("w5-known-a").And.NotContain("w5-known-b");

        SetCompany(CompanyB);
        var b = DatabaseService.Instance.LoadKnownSaleIds();
        b.Should().Contain("w5-known-b").And.NotContain("w5-known-a");
    }

    [Fact]
    public void Company_id_with_quotes_is_bound_as_a_parameter_and_cannot_break_the_query()
    {
        const string nasty = "o'brien'; DROP TABLE SoldLineItems; --";
        SetCompany(nasty);
        DatabaseService.Instance.AppendSoldLineItems(new[] { Line("q") }, "local", "w5-nasty-sale");

        DatabaseService.Instance.LoadKnownSaleIds().Should().Contain("w5-nasty-sale");
        DatabaseService.Instance.LoadSoldLinesBySaleIds(new[] { "w5-nasty-sale" }).Should().ContainKey("w5-nasty-sale");

        using var connection = DatabaseService.Instance.OpenConnection();
        Count(connection, "SoldLineItems").Should().BeGreaterThan(0, "таблица цела");
    }

    // ───────── шаг 3: Л-3, Л-4 (история закупок) ─────────

    private static ReceivingLineVm Receive(string productId, double qty, double purchase) => new()
    {
        ProductId = productId,
        ProductName = "Товар " + productId,
        Barcode = "47" + productId,
        Unit = "шт",
        Quantity = qty,
        PurchasePrice = purchase,
        SalePrice = purchase * 1.5,
    };

    [Fact]
    public void AppendHistoryBatch_writes_all_lines_in_order_and_LoadLocalHistoryMany_reads_them_newest_first()
    {
        var service = PurchaseReceivingService.Instance;
        var lines = new List<ReceivingLineVm>();
        for (var i = 0; i < 50; i++)
            lines.Add(Receive("w5-p" + (i % 5), 1 + i, 10 + i)); // 5 товаров по 10 приходов
        service.AppendHistoryBatch(lines, "Поставщик", "Кассир", "w5-receipt-1");

        var many = service.LoadLocalHistoryMany(new[] { "w5-p0", "w5-p1", "w5-p2", "w5-p3", "w5-p4", "w5-none" });

        many.Count.Should().Be(5);
        many.Should().NotContainKey("w5-none");
        foreach (var list in many.Values)
            list.Count.Should().Be(10);

        // новые первыми: у товара w5-p0 последняя строка прихода (i = 45) — количество 46, цена 55
        many["w5-p0"][0].Quantity.Should().Be(46);
        many["w5-p0"][0].PurchasePrice.Should().Be(55);
        many["w5-p0"][0].SupplierName.Should().Be("Поставщик");
        many["w5-p0"][0].ReceiptId.Should().Be("w5-receipt-1");
        many["w5-p0"].Select(h => h.At).Should().BeInDescendingOrder();

        // одиночное чтение даёт то же самое
        service.LoadLocalHistory("w5-p3").Select(h => h.Quantity).Should().Equal(many["w5-p3"].Select(h => h.Quantity));
    }

    [Fact]
    public void LoadLocalHistoryMany_crosses_the_batch_boundary_and_hides_other_companies()
    {
        var service = PurchaseReceivingService.Instance;
        var lines = Enumerable.Range(0, 620).Select(i => Receive("w5-big" + i, 1, 5)).ToList();
        service.AppendHistoryBatch(lines, null, null, null);
        SetCompany(CompanyB);
        service.AppendHistoryBatch(new[] { Receive("w5-big0", 99, 99) }, "Чужой", null, null);
        SetCompany(CompanyA);

        var many = service.LoadLocalHistoryMany(lines.Select(l => l.ProductId!).ToList());

        many.Count.Should().Be(620, "больше одной пачки в 500 кодов");
        many["w5-big0"].Should().ContainSingle().Which.Quantity.Should().Be(1, "запись чужой компании не видна");
        service.LoadLocalHistoryMany(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void AppendHistoryBatch_with_one_bad_line_still_saves_the_others()
    {
        var service = PurchaseReceivingService.Instance;
        var bad = Receive("w5-bad", 1, 1);
        bad.ProductId = null; // колонка product_id NOT NULL: эта строка не запишется
        service.AppendHistoryBatch(new[] { Receive("w5-ok1", 1, 1), bad, Receive("w5-ok2", 2, 2) }, null, null, null);

        var many = service.LoadLocalHistoryMany(new[] { "w5-ok1", "w5-ok2" });
        many.Keys.Should().BeEquivalentTo(new[] { "w5-ok1", "w5-ok2" });
    }

    // ───────── шаг 18: 24.2 (очередь офлайн-продаж) ─────────

    [Fact]
    public void Offline_queue_counts_and_unsynced_list_do_not_need_a_full_scan()
    {
        var entries = new List<OfflineSaleEntry>();
        for (var i = 0; i < 30; i++)
            entries.Add(new OfflineSaleEntry { Id = "w5-s" + i, CreatedAt = DateTimeOffset.Now.AddMinutes(-100 + i), Status = OfflineSaleEntry.Synced });
        entries.Add(new OfflineSaleEntry { Id = "w5-p1", CreatedAt = DateTimeOffset.Now.AddMinutes(-3), Status = OfflineSaleEntry.PendingSync });
        entries.Add(new OfflineSaleEntry { Id = "w5-p2", CreatedAt = DateTimeOffset.Now.AddMinutes(-2), Status = OfflineSaleEntry.Syncing });
        entries.Add(new OfflineSaleEntry { Id = "w5-f1", CreatedAt = DateTimeOffset.Now.AddMinutes(-1), Status = OfflineSaleEntry.Failed });
        OfflineDatabase.SaveAll(entries);

        OfflinePendingSalesStore.SyncedCount.Should().Be(30);
        var unsynced = OfflinePendingSalesStore.LoadAllUnsynced();
        unsynced.Select(e => e.Id).Should().Equal("w5-p1", "w5-p2", "w5-f1");
        OfflinePendingSalesStore.LoadAll().Count.Should().Be(33);
        OfflineDatabase.CountByStatus(Array.Empty<string>()).Should().Be(0);
        OfflineDatabase.CountByStatus(new[] { OfflineSaleEntry.Failed, OfflineSaleEntry.Syncing }).Should().Be(2);
    }

    // ───────── шаг 18: 24.1 (отпечатки каталога потоком) ─────────

    [Fact]
    public void Catalog_diff_sees_added_changed_and_deleted_products_through_streamed_fingerprints()
    {
        var repo = LocalProductRepository.Instance;
        static NurMarketKassa.Models.Pos.CatalogProductTileVm Tile(string id, string title, string? description = null) =>
            new(id, title, "10.00 сом", false) { Barcode = "47" + id, Unit = "шт", Quantity = 5, Category = "Тест", Description = description };

        try
        {
            repo.SyncReplaceAllWithDiff(Array.Empty<NurMarketKassa.Models.Pos.CatalogProductTileVm>());
            repo.SyncReplaceAllWithDiff(new[] { Tile("w5-t1", "Хлеб"), Tile("w5-t2", "Молоко"), Tile("w5-t3", "Сыр") }).Should().Be((3, 0, 0));
            repo.SyncReplaceAllWithDiff(new[] { Tile("w5-t1", "Хлеб"), Tile("w5-t2", "Молоко"), Tile("w5-t3", "Сыр") }).Should().Be((0, 0, 0), "ничего не менялось");
            // описание входит в отпечаток: его правка на сайте — «изменён»
            repo.SyncReplaceAllWithDiff(new[] { Tile("w5-t1", "Хлеб", "свежий"), Tile("w5-t2", "Молоко"), Tile("w5-t3", "Сыр") }).Should().Be((0, 1, 0));
            repo.SyncReplaceAllWithDiff(new[] { Tile("w5-t1", "Хлеб", "свежий"), Tile("w5-t3", "Сыр"), Tile("w5-t4", "Масло") }).Should().Be((1, 0, 1));
        }
        finally
        {
            repo.SyncReplaceAllWithDiff(Array.Empty<NurMarketKassa.Models.Pos.CatalogProductTileVm>());
        }
    }

    // ───────── шаг 19: 25.4 ─────────

    [Fact]
    public void WithReadConnection_does_not_wait_for_a_writer_holding_the_class_lock_in_wal_mode()
    {
        using (var connection = DatabaseService.Instance.OpenConnection())
        using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode;";
            Convert.ToString(mode.ExecuteScalar()).Should().BeEquivalentTo("wal", "журнал pos_local.db включает LocalProductRepository");
        }

        using var writerInside = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var writer = Task.Run(() => DatabaseService.Instance.WithConnection(_ =>
        {
            writerInside.Set();
            releaseWriter.Wait(TimeSpan.FromSeconds(20));
        }));
        try
        {
            writerInside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var reader = Task.Run(() =>
            {
                var rows = 0L;
                DatabaseService.Instance.WithReadConnection(connection => rows = Count(connection, "SoldLineItems"));
                return rows;
            });
            reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("чтение не ждёт, пока писатель держит замок записи");
        }
        finally
        {
            releaseWriter.Set();
            writer.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void LocalProductRepository_busy_timeout_is_5000_like_the_main_connection()
    {
        // 25.3: оба «хозяина» pos_local.db ждут блокировку одинаково долго — и соединение каталога, и основное.
        static int BusyTimeout(SqliteConnection connection)
        {
            using var timeout = connection.CreateCommand();
            timeout.CommandText = "PRAGMA busy_timeout;";
            return Convert.ToInt32(timeout.ExecuteScalar());
        }

        using var main = DatabaseService.Instance.OpenConnection();
        BusyTimeout(main).Should().Be(5000);
        var open = typeof(LocalProductRepository).GetMethod("OpenConnection", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var catalog = (SqliteConnection)open.Invoke(null, null)!;
        BusyTimeout(catalog).Should().Be(5000);
    }
}
