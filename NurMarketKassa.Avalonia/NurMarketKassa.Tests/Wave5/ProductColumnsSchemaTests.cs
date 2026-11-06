using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave5;

/// <summary>2026-11-06 (Б-13): после смены компании новая локальная база создавалась без столбцов piece_option_json / intake_date и др. (DatabaseService создаёт «сокращённую»
/// Products, а недостающие столбцы добавлял только LocalProductRepository.EnsureSchema, который никто не вызывал) — товары не читались, журнал заливало ошибками.
/// Здесь: любая открываемая/создаваемая база достраивается до актуальной схемы; повтор безопасен; данные не теряются; самолечение «no such column»; журнал не спамит.</summary>
[Collection("Wave5Database")]
public sealed class ProductColumnsSchemaTests
{
    private static readonly string[] Expected =
    {
        "piece_option_json", "plu", "hotkey_group", "is_bundle", "article", "bundle_items_json", "alternate_barcodes",
        "alternate_barcode_variants", "product_code", "kind", "intake_date", "description", "wholesale_price", "IsWeight",
        "discount_percent", "markup_percent", "country", "weight_kg", "min_quantity", // 2026-11-06 (В12, Б-5, Б-8)
    };

    private static HashSet<string> Columns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Products);";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    /// <summary>Таблица в том виде, как её создаёт DatabaseService.EnsureSchema (без новых столбцов), плюс catalog_meta и две строки.</summary>
    private static SqliteConnection ShortProductsDatabase()
    {
        var dir = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "wave5");
        Directory.CreateDirectory(dir);
        var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "b13-" + Guid.NewGuid().ToString("N")[..8] + ".db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Products (id TEXT PRIMARY KEY, barcode TEXT, name TEXT NOT NULL, price REAL NOT NULL DEFAULT 0, stock REAL NOT NULL DEFAULT 0,
                unit TEXT NOT NULL DEFAULT 'шт', is_favorite INTEGER NOT NULL DEFAULT 0, must_weigh INTEGER NOT NULL DEFAULT 0, image_url TEXT, category TEXT, brand TEXT,
                purchase_price REAL NOT NULL DEFAULT 0);
            CREATE TABLE catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO catalog_meta(key, value) VALUES ('catalog_version', 'v1');
            INSERT INTO Products(id, barcode, name, price, stock) VALUES ('b13-1', '4700000000011', 'Молоко', 90, 5), ('b13-2', NULL, 'Сыр', 400, 2);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    public void Short_products_table_gets_every_missing_column_and_keeps_data()
    {
        using var connection = ShortProductsDatabase();
        Columns(connection).Should().NotContain("piece_option_json").And.NotContain("intake_date");

        var added = LocalProductRepository.EnsureProductColumns(connection);

        added.Should().Be(Expected.Length);
        Columns(connection).Should().Contain(Expected);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Products;";
        Convert.ToInt64(count.ExecuteScalar()).Should().Be(2);
        using var stock = connection.CreateCommand();
        stock.CommandText = "SELECT stock FROM Products WHERE id = 'b13-1';";
        Convert.ToDouble(stock.ExecuteScalar()).Should().Be(5);
        // столбцы wholesale_price/IsWeight новые — версия каталога сброшена (ближайшая полная загрузка запишет поля)
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT COUNT(*) FROM catalog_meta WHERE key = 'catalog_version';";
        Convert.ToInt64(version.ExecuteScalar()).Should().Be(0);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Repeated_migration_is_safe_and_adds_nothing()
    {
        using var connection = ShortProductsDatabase();
        LocalProductRepository.EnsureProductColumns(connection);
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "INSERT INTO catalog_meta(key, value) VALUES ('catalog_version', 'v2');";
            version.ExecuteNonQuery();
        }

        LocalProductRepository.EnsureProductColumns(connection).Should().Be(0);
        LocalProductRepository.EnsureProductColumns(connection).Should().Be(0);

        using var kept = connection.CreateCommand();
        kept.CommandText = "SELECT value FROM catalog_meta WHERE key = 'catalog_version';";
        kept.ExecuteScalar().Should().Be("v2", "повторная проверка ничего не добавляет и версию каталога не сбрасывает");
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Products;";
        Convert.ToInt64(count.ExecuteScalar()).Should().Be(2);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Missing_products_table_is_left_to_the_caller()
    {
        var dir = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "wave5");
        Directory.CreateDirectory(dir);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "b13-empty-" + Guid.NewGuid().ToString("N")[..8] + ".db")};Pooling=False");
        connection.Open();
        LocalProductRepository.EnsureProductColumns(connection).Should().Be(0);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Database_opened_after_account_switch_has_all_product_columns()
    {
        DatabaseService.Instance.EnsureSchema();
        DatabaseService.Instance.ReopenAfterAccountSwitch(); // путь смены компании: схему накатывает только DatabaseService, репозиторий не вызывается

        using var connection = DatabaseService.Instance.OpenConnection();
        Columns(connection).Should().Contain(Expected);
    }

    [Fact]
    public void Self_heal_runs_once_per_open_database_and_ignores_other_errors()
    {
        DatabaseService.Instance.ReopenAfterAccountSwitch(); // новый ключ «путь|поколение» — самолечение ещё не было

        LocalProductRepository.TryHealMissingColumn(new InvalidOperationException("no such column: x")).Should().BeFalse("не ошибка SQLite");
        LocalProductRepository.TryHealMissingColumn(new SqliteException("database is locked", 5)).Should().BeFalse("другая причина");

        var missing = new SqliteException("SQLite Error 1: 'no such column: piece_option_json'.", 1);
        LocalProductRepository.TryHealMissingColumn(missing).Should().BeTrue("первый раз за открытие базы: столбцы перепроверены, запрос повторить");
        LocalProductRepository.TryHealMissingColumn(missing).Should().BeFalse("второй раз тот же запуск базы — не зацикливаемся");
    }

    [Fact]
    public void Reading_favorites_after_schema_loss_does_not_throw()
    {
        DatabaseService.Instance.ReopenAfterAccountSwitch();
        var act = () => LocalProductRepository.Instance.LoadFavoriteTiles();
        act.Should().NotThrow();
    }

    [Fact]
    public void Throttled_log_writes_one_line_per_reason_per_window()
    {
        var key = "b13-" + Guid.NewGuid().ToString("N");
        PosLogger.LogThrottled(key, "первая").Should().BeTrue();
        PosLogger.LogThrottled(key, "вторая").Should().BeFalse("та же причина в окне 10 минут");
        PosLogger.LogThrottled(key + "-other", "другая причина").Should().BeTrue();
        PosLogger.LogThrottled(key, "после окна", "INFO", TimeSpan.Zero).Should().BeTrue("окно нулевое — пишется");
    }
}
