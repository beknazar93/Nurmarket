using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20 («Автоподтяжка базы из папки программы»): поиск и распознавание программы по набору файлов, копирование базы во временную папку и уборка,
/// чтение SQLite (штрихкоды, доп. штрихкоды, цены, остатки, категории, единицы), DBF, выгрузок 1С (CommerceML), Excel/CSV, закрытых форматов и ошибок.
/// Все папки — синтетические, во временной папке; настоящие данные кассы не трогаются.</summary>
[Collection("UserPreferences")] // Tr.T зависит от языка интерфейса — идём вместе с остальными тестами импорта
public sealed class ProgramFolderImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pf-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _tempCopies;

    public ProgramFolderImportTests()
    {
        Directory.CreateDirectory(_root);
        _tempCopies = Path.Combine(_root, "_copies");
        Directory.CreateDirectory(_tempCopies);
        ProgramFolderImporter.TempRootForTests = _tempCopies;
    }

    public void Dispose()
    {
        ProgramFolderImporter.TempRootForTests = null;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* временная папка теста */ }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>База «как у кассовой программы»: товары + штрихкоды, цены и остатки в отдельных таблицах, справочники групп и единиц, помеченный удалённым товар.</summary>
    private string MakeSqlite(string dir, string fileName = "shop.db")
    {
        var path = Path.Combine(dir, fileName);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE goods(id INTEGER PRIMARY KEY, name TEXT, article TEXT, category_id INTEGER, unit_id INTEGER, deleted INTEGER)");
            Exec(c, "CREATE TABLE barcodes(goods_id INTEGER, barcode TEXT)");
            Exec(c, "CREATE TABLE prices(goods_id INTEGER, price REAL, purchase_price REAL)");
            Exec(c, "CREATE TABLE stock(product_id INTEGER, qty REAL)");
            Exec(c, "CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT)");
            Exec(c, "CREATE TABLE units(id INTEGER PRIMARY KEY, name TEXT)");
            Exec(c, "INSERT INTO categories VALUES (1,'Бакалея'),(2,'Напитки')");
            Exec(c, "INSERT INTO units VALUES (1,'шт'),(2,'кг')");
            Exec(c, "INSERT INTO goods VALUES (1,'Сахар 1 кг','S-1',1,1,0),(2,'Вода 0,5',NULL,2,1,0),(3,'Старый товар','X',1,1,1)");
            Exec(c, "INSERT INTO barcodes VALUES (1,'4600000000059'),(1,'4600000000066'),(2,'4600000000073'),(3,'4600000000080')");
            Exec(c, "INSERT INTO prices VALUES (1,85.5,70),(2,30,20)");
            Exec(c, "INSERT INTO stock VALUES (1,50),(1,5),(2,12)");
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static int Col(ProductFileTable t, string header) => t.Headers.ToList().IndexOf(header);

    // ------------------------------------------------------------------ SQLite

    [Fact]
    public void Sqlite_database_is_read_with_barcodes_prices_stock_categories_units()
    {
        var dir = Dir("paloma");
        var db = MakeSqlite(dir);
        var before = Hash(db);

        var r = ProgramFolderImporter.Read(dir, "paloma365");

        Assert.True(r.Ok, r.Error);
        var t = r.Read.Table!;
        Assert.Equal(2, t.DataRowCount); // удалённый «Старый товар» пропущен
        var row1 = t.DataRows.First(x => x[Col(t, "Наименование")] == "Сахар 1 кг");
        Assert.Equal("4600000000059", row1[Col(t, "Штрихкод")]);
        Assert.Equal("4600000000066", row1[Col(t, "Доп. штрихкоды")]);
        Assert.Equal("85.5", row1[Col(t, "Цена продажи")]);
        Assert.Equal("70", row1[Col(t, "Закупочная цена")]);
        Assert.Equal("55", row1[Col(t, "Остаток")]); // сложено по двум строкам склада
        Assert.Equal("Бакалея", row1[Col(t, "Категория")]);
        Assert.Equal("шт", row1[Col(t, "Единица измерения")]);
        Assert.Equal("S-1", row1[Col(t, "Артикул")]);
        Assert.Equal("база SQLite", r.Source, ignoreCase: true);
        // это обычная таблица: автоподбор столбцов видит все поля
        var m = ProductImportMapping.AutoDetect(t.Headers, t);
        foreach (var f in new[] { ProductImportField.Barcode, ProductImportField.Name, ProductImportField.AltBarcodes, ProductImportField.Sale, ProductImportField.Purchase, ProductImportField.Quantity })
            Assert.True(m.Columns.ContainsKey(f), f.ToString());
        // источник не тронут, временные копии удалены
        Assert.Equal(before, Hash(db));
        Assert.Empty(Directory.GetDirectories(_tempCopies, ProgramFolderImporter.TempPrefix + "*"));
        Assert.False(File.Exists(db + "-wal") || File.Exists(db + "-journal"));
    }

    [Fact]
    public void Sqlite_database_held_open_by_another_process_is_still_copied_and_read()
    {
        var dir = Dir("open");
        var db = MakeSqlite(dir);
        using var holder = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); // «программа держит базу»
        var r = ProgramFolderImporter.Read(dir);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.Read.Table!.DataRowCount);
    }

    [Fact]
    public void Sqlite_database_locked_exclusively_gives_busy_message_and_cleans_up()
    {
        if (!OperatingSystem.IsWindows())
            return; // блокировки FileShare.None действуют только в Windows
        var dir = Dir("busy");
        var db = MakeSqlite(dir);
        using var holder = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = ProgramFolderImporter.Read(dir);
        Assert.False(r.Ok);
        Assert.Equal(FolderProblem.Busy, r.Problem);
        Assert.Empty(Directory.GetDirectories(_tempCopies, ProgramFolderImporter.TempPrefix + "*"));
    }

    [Fact]
    public void Sqlite_without_product_table_is_reported_not_crashed()
    {
        var dir = Dir("nothing");
        var path = Path.Combine(dir, "settings.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE config(key TEXT, value TEXT)");
            Exec(c, "INSERT INTO config VALUES ('lang','ru')");
        }
        SqliteConnection.ClearAllPools();
        var r = ProgramFolderImporter.Read(dir);
        Assert.False(r.Ok);
        Assert.Equal(FolderProblem.NothingReadable, r.Problem);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Sqlite_clients_table_is_found_next_to_products()
    {
        var dir = Dir("withclients");
        var db = MakeSqlite(dir);
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE customers(id INTEGER PRIMARY KEY, name TEXT, phone TEXT, debt REAL)");
            Exec(c, "INSERT INTO customers VALUES (1,'Айбек','0555123456',150),(2,'Нургуль','0700111222',0)");
        }
        SqliteConnection.ClearAllPools();
        var r = ProgramFolderImporter.Read(dir);
        Assert.True(r.Ok, r.Error);
        Assert.NotNull(r.Clients);
        Assert.Equal(2, r.Clients!.DataRowCount);
        Assert.True(ClientsImportPlan.LooksLikeClients(r.Clients.Headers));
    }

    [Fact]
    public void Encrypted_sqlite_file_gets_clear_message()
    {
        var dir = Dir("enc");
        File.WriteAllBytes(Path.Combine(dir, "data.sqlite3"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray());
        var r = ProgramFolderImporter.Read(dir);
        Assert.False(r.Ok);
        Assert.Equal(FolderProblem.Encrypted, r.Problem);
    }

    // ------------------------------------------------------------------ DBF

    private static byte[] MakeDbf(params (string Name, string Barcode, string Price, string Qty, bool Deleted)[] rows)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = Encoding.GetEncoding(866);
        var fields = new (string Name, char Type, int Len, int Dec)[] { ("NAME", 'C', 30, 0), ("BARCODE", 'C', 13, 0), ("PRICE", 'N', 10, 2), ("QTY", 'N', 10, 3) };
        var recLen = 1 + fields.Sum(f => f.Len);
        var headerLen = 32 + fields.Length * 32 + 1;
        using var ms = new MemoryStream();
        var h = new byte[32];
        h[0] = 0x03;
        BitConverter.GetBytes((uint)rows.Length).CopyTo(h, 4);
        BitConverter.GetBytes((ushort)headerLen).CopyTo(h, 8);
        BitConverter.GetBytes((ushort)recLen).CopyTo(h, 10);
        h[29] = 0x65; // язык: 866
        ms.Write(h);
        foreach (var f in fields)
        {
            var d = new byte[32];
            Encoding.ASCII.GetBytes(f.Name).CopyTo(d, 0);
            d[11] = (byte)f.Type;
            d[16] = (byte)f.Len;
            d[17] = (byte)f.Dec;
            ms.Write(d);
        }
        ms.WriteByte(0x0D);
        foreach (var r in rows)
        {
            ms.WriteByte(r.Deleted ? (byte)'*' : (byte)' ');
            foreach (var (value, f) in new[] { r.Name, r.Barcode, r.Price, r.Qty }.Zip(fields))
            {
                var bytes = new byte[f.Len];
                Array.Fill(bytes, (byte)' ');
                var src = f.Type == 'N' ? Encoding.ASCII.GetBytes(value.PadLeft(f.Len)) : enc.GetBytes(value);
                Array.Copy(src, bytes, Math.Min(src.Length, f.Len));
                ms.Write(bytes);
            }
        }
        ms.WriteByte(0x1A);
        return ms.ToArray();
    }

    [Fact]
    public void Dbf_table_is_read_in_dos_encoding_and_deleted_records_are_skipped()
    {
        var dir = Dir("dbf");
        File.WriteAllBytes(Path.Combine(dir, "GOODS.DBF"), MakeDbf(
            ("Хлеб белый", "4600000000011", "35.00", "40.000", false),
            ("Удалённый", "4600000000028", "10.00", "1.000", true),
            ("Рис 1 кг", "4600000000035", "105.50", "60.000", false)));
        var r = ProgramFolderImporter.Read(dir);
        Assert.True(r.Ok, r.Error);
        var t = r.Read.Table!;
        Assert.Equal(2, t.DataRowCount);
        Assert.Contains(t.DataRows, x => x[0] == "Хлеб белый" && x[1] == "4600000000011" && x[2] == "35.00");
        Assert.Contains(t.DataRows, x => x[0] == "Рис 1 кг" && x[3] == "60.000");
        Assert.Equal("таблица DBF", r.Source, ignoreCase: true);
    }

    [Fact]
    public void Random_file_with_dbf_extension_is_ignored()
    {
        var dir = Dir("fakedbf");
        File.WriteAllText(Path.Combine(dir, "readme.dbf"), "это не таблица dBase");
        var scan = ProgramFolderImporter.Scan(dir);
        Assert.Empty(scan.Files);
    }

    // ------------------------------------------------------------------ 1С, Excel/CSV

    private const string ImportXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><КоммерческаяИнформация><Классификатор><Группы><Группа><Ид>g1</Ид><Наименование>Бакалея</Наименование></Группа></Группы></Классификатор>"
        + "<Каталог><Товары><Товар><Ид>t1</Ид><Артикул>A-1</Артикул><Наименование>Сахар</Наименование><Штрихкод>4600000000059</Штрихкод><БазоваяЕдиница>кг</БазоваяЕдиница><Группы><Ид>g1</Ид></Группы></Товар></Товары></Каталог></КоммерческаяИнформация>";

    private const string OffersXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><КоммерческаяИнформация><ПакетПредложений><Предложения><Предложение><Ид>t1</Ид><Артикул>A-1</Артикул><Наименование>Сахар</Наименование>"
        + "<Штрихкод>4600000000059</Штрихкод><Количество>40</Количество><Цены><Цена><ЦенаЗаЕдиницу>85</ЦенаЗаЕдиницу></Цена></Цены></Предложение></Предложения></ПакетПредложений></КоммерческаяИнформация>";

    [Fact]
    public void OneC_folder_with_commerceml_merges_catalog_and_offers_and_is_recognised()
    {
        var dir = Dir("1c-export");
        File.WriteAllText(Path.Combine(dir, "import.xml"), ImportXml, Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "offers.xml"), OffersXml, Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(dir, "1Cv8.1CD"), new byte[64]);

        var r = ProgramFolderImporter.Read(dir);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("1c", r.Program?.ProfileId);
        Assert.Equal(FolderConfidence.High, r.Confidence);
        var t = r.Read.Table!;
        Assert.Equal(1, t.DataRowCount); // каталог и предложение слиты в одну строку
        var row = t.DataRows.Single();
        Assert.Equal("85", row[Col(t, "Цена продажи")]);
        Assert.Equal("40", row[Col(t, "Остаток")]);
        Assert.Equal("Бакалея", row[Col(t, "Категория")]);
        Assert.Contains(r.Unsupported, f => f.Kind == FolderFileKind.OneCFile); // закрытая база найдена, но не читается
    }

    [Fact]
    public void OneC_closed_database_alone_gives_unsupported_message()
    {
        var dir = Dir("1c-base");
        File.WriteAllBytes(Path.Combine(dir, "1Cv8.1CD"), new byte[256]);
        var r = ProgramFolderImporter.Read(dir, "1c");
        Assert.False(r.Ok);
        Assert.Equal(FolderProblem.UnsupportedFormat, r.Problem);
        Assert.Equal("1c", r.Program?.ProfileId);
        Assert.Contains("1Cv8.1CD", r.Error);
    }

    [Theory]
    [InlineData("base.fdb", "Firebird")]
    [InlineData("base.gdb", "Firebird")]
    [InlineData("shop.mdb", "Access")]
    [InlineData("shop.accdb", "Access")]
    [InlineData("shop.mdf", "SQL Server")]
    public void Closed_formats_are_detected_with_clear_message_and_no_dependencies(string file, string expected)
    {
        var dir = Dir("closed-" + file.Replace('.', '_'));
        File.WriteAllBytes(Path.Combine(dir, file), new byte[512]);
        var r = ProgramFolderImporter.Read(dir);
        Assert.False(r.Ok);
        Assert.Equal(FolderProblem.UnsupportedFormat, r.Problem);
        Assert.Contains(expected, r.Error);
    }

    [Fact]
    public void Csv_export_inside_folder_is_read_and_program_recognised_by_headers()
    {
        var dir = Dir("exports");
        File.WriteAllText(Path.Combine(dir, "goods.csv"),
            "Группы;Код;Наименование;Артикул;Внешний код;Единица измерения;Цена: Цена продажи;Закупочная цена;Штрихкод EAN13;Штрихкод EAN8;Остаток\n"
            + "Бакалея;00012;Сахар 1 кг;S-1;abc123;шт;85;70;4600000000059;46000058;50\n", Encoding.UTF8);
        var r = ProgramFolderImporter.Read(dir);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("moysklad", r.Program?.ProfileId);
        Assert.Equal(1, r.Read.Table!.DataRowCount);
    }

    [Fact]
    public void Best_table_wins_over_clients_list_and_clients_are_offered_separately()
    {
        var dir = Dir("mixed");
        File.WriteAllText(Path.Combine(dir, "clients.csv"), "ФИО;Телефон;Долг\nАйбек;0555123456;100\nНургуль;0700111222;0\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "items.csv"), "Штрихкод;Название;Цена\n4600000000011;Хлеб;35\n4600000000028;Рис;105\n4600000000035;Соль;20\n", Encoding.UTF8);
        var r = ProgramFolderImporter.Read(dir);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, r.Read.Table!.DataRowCount);
        Assert.Equal(2, r.Clients!.DataRowCount);
    }

    // ------------------------------------------------------------------ поиск, распознавание, ошибки

    [Fact]
    public void Scan_depth_is_limited_and_system_folders_are_skipped()
    {
        var dir = Dir("deep");
        var deep = Path.Combine(dir, "a", "b", "c", "d", "e", "f");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "far.csv"), "Штрихкод;Название\n4600000000011;Х\n");
        Directory.CreateDirectory(Path.Combine(dir, "Windows"));
        File.WriteAllText(Path.Combine(dir, "Windows", "skipped.csv"), "Штрихкод;Название\n4600000000011;Х\n");
        var near = Path.Combine(dir, "a", "b");
        File.WriteAllText(Path.Combine(near, "near.csv"), "Штрихкод;Название\n4600000000011;Х\n");

        var scan = ProgramFolderImporter.Scan(dir);

        Assert.Contains(scan.Files, f => f.Path.EndsWith("near.csv"));
        Assert.DoesNotContain(scan.Files, f => f.Path.EndsWith("far.csv"));
        Assert.DoesNotContain(scan.Files, f => f.Path.EndsWith("skipped.csv"));
    }

    [Fact]
    public void Program_is_recognised_by_folder_name_with_high_confidence()
    {
        var dir = Dir("Rosta");
        MakeSqlite(dir);
        var scan = ProgramFolderImporter.Scan(dir);
        Assert.Equal("rosta", scan.Program?.ProfileId);
        Assert.Equal(FolderConfidence.High, scan.Confidence);
        // схема таблиц — наша догадка, поэтому после чтения уверенность «средняя»
        Assert.Equal(FolderConfidence.Medium, ProgramFolderImporter.Read(dir).Confidence);
    }

    [Fact]
    public void Selected_tile_without_matching_folder_gives_low_confidence()
    {
        var dir = Dir("misc");
        MakeSqlite(dir);
        var r = ProgramFolderImporter.Read(dir, "umag");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("umag", r.Program?.ProfileId);
        Assert.Equal(FolderConfidence.Low, r.Confidence);
    }

    [Fact]
    public void Missing_empty_and_unrelated_folders_give_clear_errors()
    {
        var missing = ProgramFolderImporter.Read(Path.Combine(_root, "нет-такой"));
        Assert.Equal(FolderProblem.NotFound, missing.Problem);

        var empty = ProgramFolderImporter.Read(Dir("empty"));
        Assert.Equal(FolderProblem.Empty, empty.Problem);

        var dir = Dir("photos");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), new byte[100]);
        Assert.Equal(FolderProblem.Empty, ProgramFolderImporter.Read(dir).Problem);

        foreach (var r in new[] { missing, empty })
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void A_single_file_path_can_be_given_instead_of_a_folder()
    {
        var dir = Dir("single");
        var db = MakeSqlite(dir);
        var r = ProgramFolderImporter.Read(db);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.Read.Table!.DataRowCount);
    }

    [Fact]
    public void Cancelled_read_cleans_up_temp_copies()
    {
        var dir = Dir("cancel");
        MakeSqlite(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ProgramFolderImporter.Read(dir, null, cts.Token));
        Assert.Empty(Directory.GetDirectories(_tempCopies, ProgramFolderImporter.TempPrefix + "*"));
    }

    [Fact]
    public void Ctmax_real_sample_export_is_read_when_present_in_working_copy()
    {
        // образец владельца (docs/Образцы/Ситимакс, в .gitignore): Excel из 4 столбцов, шапка в 3-й строке, группы стоят строками среди товаров
        string? dir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "docs", "Образцы", "Ситимакс");
            if (Directory.Exists(p))
            {
                dir = p;
                break;
            }
        }
        if (dir is null)
            return; // образца нет в этой копии
        var r = ProgramFolderImporter.Read(dir, "ctmax");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("ctmax", r.Program?.ProfileId);
        var t = r.Read.Table!;
        Assert.True(t.DataRowCount > 10_000);
        var m = ProductImportMapping.AutoDetect(t.Headers, t);
        foreach (var f in new[] { ProductImportField.Barcode, ProductImportField.Name, ProductImportField.Sale, ProductImportField.Purchase })
            Assert.True(m.Columns.ContainsKey(f), f.ToString());
        Assert.False(m.Columns.ContainsKey(ProductImportField.Quantity)); // остатков в выгрузке нет
    }

    // ------------------------------------------------------------------ справочник подсказок

    [Fact]
    public void Hints_cloud_programs_have_no_paths_and_local_programs_show_usual_places()
    {
        foreach (var id in new[] { "moysklad", "poster", "loyverse" })
        {
            var h = ProgramFolderHints.For(id)!;
            Assert.Equal(ProgramFolderSupport.Cloud, h.Support);
            Assert.Empty(h.StandardPaths);
            Assert.Equal("", h.UsuallyHereText());
            Assert.False(string.IsNullOrWhiteSpace(h.Note));
        }
        foreach (var id in new[] { "1c", "paloma365", "umag", "rosta" })
        {
            var h = ProgramFolderHints.For(id)!;
            Assert.NotEmpty(h.StandardPaths);
            Assert.Contains(":", h.UsuallyHereText());
            Assert.All(h.ExpandedStandardPaths(), p => Assert.DoesNotContain("%", p));
        }
        Assert.Equal(ProgramFolderSupport.NeedsExport, ProgramFolderHints.For("ctmax")!.Support);
        Assert.Equal(ProgramFolderSupport.NeedsExport, ProgramFolderHints.For("1c")!.Support);
    }

    [Fact]
    public void Hints_guess_by_path_ignores_short_words_inside_other_names()
    {
        Assert.Equal("paloma365", ProgramFolderHints.GuessByPath(@"C:\Program Files\Paloma365\data")?.ProfileId);
        Assert.Equal("1c", ProgramFolderHints.GuessByPath(@"C:\Users\x\AppData\Roaming\1C\1CEStart")?.ProfileId);
        Assert.Null(ProgramFolderHints.GuessByPath(@"C:\Users\x\Documents\a1c2b3\data"));
        Assert.Equal("ctmax", ProgramFolderHints.GuessByPath(@"C:\CTmax\Data")?.ProfileId);
    }

    [Fact]
    public void Hints_parse_onec_base_list_file_bases_only()
    {
        var v8i = "[Магазин]\r\nConnect=File=\"C:\\Bases\\Shop\";\r\nID=abc\r\n[Сервер]\r\nConnect=Srvr=\"srv\";Ref=\"shop\";\r\n[Склад]\r\nConnect=File=\"D:\\Склад 1С\";\r\n";
        var list = ProgramFolderHints.ParseOneCBaseList(v8i);
        Assert.Equal(["C:\\Bases\\Shop", "D:\\Склад 1С"], list);
    }
}
