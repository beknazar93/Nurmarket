using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NurMarketKassa.Services;

// 2026-11-20 (автоподтяжка базы из папки программы): универсальное чтение локальной базы SQLite — схем конкретных программ у нас нет (образцов не было),
// поэтому таблица товаров ищется по названиям столбцов теми же синонимами, что и заголовки Excel (ProductImportMapping): «name/название/наименование»,
// «barcode/штрихкод», «price/цена», «qty/остаток», «category/группа», «unit/единица» и т.д. Недостающее подтягивается из соседних таблиц:
// штрихкоды (в т. ч. дополнительные), цены и остатки — по столбцу-ссылке на товар (product_id/item_id/goods_id…), категория и единица — по category_id/unit_id.
// ВАЖНО: вызывается только на КОПИИ базы во временной папке (ProgramFolderImporter); исходный файл не открывается. Результат — обычная таблица с русскими
// заголовками «Штрихкод, Наименование, …» — дальше мастер ведёт себя как с файлом. Проверено на синтетической базе (тесты), на настоящих базах программ — нет.

/// <summary>Итог чтения одной базы: таблица товаров (+ клиенты, если нашлись), замечания, описание источника.</summary>
internal sealed class ExtractedTables
{
    public ProductFileTable? Products { get; init; }
    public ProductFileTable? Clients { get; init; }
    public string? Description { get; init; }
    public string? Error { get; init; }
}

internal static class FolderTableBuilder
{
    /// <summary>Стандартные заголовки таблицы товаров, понятные автоподбору столбцов (ProductImportMapping).</summary>
    internal static string Header(ProductImportField f) => f switch
    {
        ProductImportField.Barcode => "Штрихкод",
        ProductImportField.Name => "Наименование",
        ProductImportField.AltBarcodes => "Доп. штрихкоды",
        ProductImportField.Article => "Артикул",
        ProductImportField.Unit => "Единица измерения",
        ProductImportField.Category => "Категория",
        ProductImportField.Purchase => "Закупочная цена",
        ProductImportField.Sale => "Цена продажи",
        ProductImportField.Quantity => "Остаток",
        _ => f.ToString(),
    };

    internal static readonly ProductImportField[] Order =
    [
        ProductImportField.Barcode, ProductImportField.Name, ProductImportField.AltBarcodes, ProductImportField.Article, ProductImportField.Unit,
        ProductImportField.Category, ProductImportField.Purchase, ProductImportField.Sale, ProductImportField.Quantity,
    ];

    /// <summary>Собрать таблицу: столбцы без единого значения выбрасываются (кроме штрихкода и названия).</summary>
    internal static ProductFileTable Build(string fileName, List<Dictionary<ProductImportField, string>> items, IEnumerable<string> warnings)
    {
        var fields = Order.Where(f => f is ProductImportField.Barcode or ProductImportField.Name || items.Any(i => i.TryGetValue(f, out var v) && v.Length > 0)).ToList();
        var rows = new List<ProductFileRow> { new(1, fields.Select(Header).ToArray(), 0UL) };
        foreach (var item in items)
            rows.Add(new ProductFileRow(rows.Count + 1, fields.Select(f => item.TryGetValue(f, out var v) ? v : "").ToArray(), 0UL));
        var table = new ProductFileTable { Kind = ProductFileKind.Database, FileName = fileName, Rows = rows, ColumnCount = fields.Count };
        table.DetectHeaderRow();
        table.Warnings.AddRange(warnings);
        return table;
    }

    /// <summary>Таблица клиентов «как есть» (заголовки из базы) — её читает окно «Клиенты и долги».</summary>
    internal static ProductFileTable BuildRaw(string fileName, string[] headers, List<string[]> data)
    {
        var rows = new List<ProductFileRow> { new(1, headers, 0UL) };
        foreach (var d in data)
            rows.Add(new ProductFileRow(rows.Count + 1, d, 0UL));
        var table = new ProductFileTable { Kind = ProductFileKind.Database, FileName = fileName, Rows = rows, ColumnCount = headers.Length };
        table.DetectHeaderRow();
        return table;
    }

    internal static bool Truthy(string v) => v is "1" or "true" or "True" or "TRUE" or "да" or "Да" or "yes" or "Y" or "T";
}

internal static class SqliteProductReader
{
    private sealed record Col(string Name, bool Pk);

    private sealed class Tbl
    {
        public required string Name { get; init; }
        public required List<Col> Cols { get; init; }
        public ProductImportMapping Map => _map ??= ProductImportMapping.AutoDetect(Cols.Select(c => c.Name).ToList());
        private ProductImportMapping? _map;
        public long Rows;
    }

    private static readonly string[] DeletedKeys = ["deleted", "isdeleted", "removed", "isremoved", "deletionmark", "isarchived", "archived", "пометкаудаления", "удален", "удалён"];
    private static readonly string[] GroupKeys = ["isgroup", "isfolder", "этогруппа", "группа1с", "isparent"];
    private static readonly string[] ProductFkWords = ["product", "item", "good", "tovar", "ware", "sku", "nomenclat", "номенклатур", "товар"];

    internal static bool HasSqliteMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[16];
            return fs.Read(b, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(b, 0, 15) == "SQLite format 3";
        }
        catch (IOException)
        {
            return true; // файл занят программой: считаем базой, дальше копирование скажет «файл занят»
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Прочитать КОПИЮ базы. Исключения наружу не бросает.</summary>
    internal static ExtractedTables Extract(string dbCopyPath, string displayName, CancellationToken ct = default)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = dbCopyPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            return ExtractFrom(conn, displayName, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 26 or 21)
        {
            return new ExtractedTables { Error = "encrypted" };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return new ExtractedTables { Error = "busy" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log("Подтяжка базы из папки: SQLite не прочитан: " + ex.GetType().Name, "WARNING"); // без путей и содержимого
            return new ExtractedTables { Error = "error" };
        }
    }

    private static ExtractedTables ExtractFrom(SqliteConnection conn, string displayName, CancellationToken ct)
    {
        var tables = new List<Tbl>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            var names = new List<string>();
            while (r.Read())
                names.Add(r.GetString(0));
            foreach (var n in names.Take(400))
            {
                using var c2 = conn.CreateCommand();
                c2.CommandText = $"PRAGMA table_info({Q(n)})";
                using var cr = c2.ExecuteReader();
                var cols = new List<Col>();
                while (cr.Read())
                    cols.Add(new Col(cr.GetString(1), cr.GetInt32(5) > 0));
                if (cols.Count > 0)
                    tables.Add(new Tbl { Name = n, Cols = cols });
            }
        }
        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            t.Rows = Scalar(conn, $"SELECT COUNT(*) FROM {Q(t.Name)}");
        }

        // таблица товаров: есть название и (штрихкод, цена или артикул); больше узнанных полей и строк — лучше
        var main = tables.Where(t => t.Rows > 0 && t.Map.Columns.ContainsKey(ProductImportField.Name)
                && (t.Map.Columns.ContainsKey(ProductImportField.Barcode) || t.Map.Columns.ContainsKey(ProductImportField.Sale) || t.Map.Columns.ContainsKey(ProductImportField.Article)
                    || tables.Any(o => o != t && o.Rows > 0 && o.Map.Columns.ContainsKey(ProductImportField.Barcode) && FkColumn(o, t) is not null)))
            .OrderByDescending(t => Score(t.Map)).ThenByDescending(t => t.Rows).FirstOrDefault();
        var clients = FindClients(conn, tables, main);
        if (main is null)
            return new ExtractedTables { Clients = clients, Error = "no-products" };
        if (main.Rows > ProductFileReader.MaxRows)
            return new ExtractedTables { Error = "too-many" };

        var warnings = new List<string>();
        var items = ReadMain(conn, main, tables, warnings, ct, out var pkValues);
        if (items.Count == 0)
            return new ExtractedTables { Clients = clients, Error = "no-products" };
        MergeSatellites(conn, main, tables, items, pkValues, warnings, ct);
        var table = FolderTableBuilder.Build(displayName, items, warnings);
        return new ExtractedTables { Products = table, Clients = clients, Description = main.Name };
    }

    private static int Score(ProductImportMapping m) =>
        m.Columns.Keys.Count(k => k is ProductImportField.Barcode or ProductImportField.Name or ProductImportField.Sale or ProductImportField.Purchase or ProductImportField.Quantity
            or ProductImportField.Category or ProductImportField.Unit or ProductImportField.Article or ProductImportField.AltBarcodes);

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static string Str(object? v) => v switch
    {
        null or DBNull => "",
        string s => s.Trim(),
        double d => d.ToString("0.########", CultureInfo.InvariantCulture),
        float f => f.ToString("0.########", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        byte[] => "",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    private static bool IsIdColumn(string name)
    {
        var k = ProductImportMapping.Key(name);
        return k.Length > 2 && (k.EndsWith("id", StringComparison.Ordinal) || k.EndsWith("ид", StringComparison.Ordinal)) && k != "id";
    }

    private static string? PkColumn(Tbl t) =>
        t.Cols.FirstOrDefault(c => c.Pk)?.Name ?? t.Cols.FirstOrDefault(c => ProductImportMapping.Key(c.Name) is "id" or "ид")?.Name;

    /// <summary>Столбец таблицы <paramref name="side"/>, ссылающийся на товар из <paramref name="main"/>.</summary>
    private static string? FkColumn(Tbl side, Tbl main)
    {
        var pk = PkColumn(main);
        var mainKey = ProductImportMapping.Key(main.Name);
        foreach (var c in side.Cols)
        {
            if (!IsIdColumn(c.Name) || c.Name == PkColumn(side) && c.Pk)
                continue;
            var k = ProductImportMapping.Key(c.Name);
            if (ProductFkWords.Any(w => k.Contains(w, StringComparison.Ordinal)) || k == mainKey + "id" || (mainKey.EndsWith('s') && k == mainKey[..^1] + "id")
                || (pk is not null && k == ProductImportMapping.Key(pk) && !side.Cols.Any(x => x.Pk && x.Name == c.Name)))
                return c.Name;
        }
        return null;
    }

    private static List<Dictionary<ProductImportField, string>> ReadMain(SqliteConnection conn, Tbl main, List<Tbl> all, List<string> warnings, CancellationToken ct,
        out List<string> pkValues)
    {
        pkValues = [];
        var items = new List<Dictionary<ProductImportField, string>>();
        // столбцы полей: автоподбор по названиям + ссылки category_id/group_id/unit_id/measure_id, которые синонимы не знают
        var cols = new Dictionary<ProductImportField, int>(main.Map.Columns);
        AddForeignKey(main, cols, ProductImportField.Category, ["categor", "group", "razdel", "категор", "групп", "раздел"]);
        AddForeignKey(main, cols, ProductImportField.Unit, ["unit", "measure", "uom", "единиц", "едизм", "birim"]);
        var pk = PkColumn(main);
        // столбцы-ссылки (category_id, unit_id): значение — номер, имя берётся из справочника
        var lookups = new Dictionary<ProductImportField, Dictionary<string, string>>();
        foreach (var f in new[] { ProductImportField.Category, ProductImportField.Unit })
        {
            if (!cols.TryGetValue(f, out var ci) || !IsIdColumn(main.Cols[ci].Name))
                continue;
            var dict = LoadDictionary(conn, all, main, f);
            if (dict is null)
                warnings.Add(f == ProductImportField.Category
                    ? Tr.T("Категории не подтянуты: справочник групп в базе не найден.", "Категориялар алынган жок: базадан топтор справочниги табылган жок.", "Categories were not pulled: no group list in the database.", "Kategoriler alınamadı: veritabanında grup listesi bulunamadı.", "Kategoriyalar olinmadi: bazada guruhlar ro'yxati topilmadi.")
                    : Tr.T("Единицы измерения не подтянуты: справочник единиц в базе не найден.", "Өлчөм бирдиктери алынган жок: базадан справочник табылган жок.", "Units were not pulled: no unit list in the database.", "Birimler alınamadı: veritabanında birim listesi bulunamadı.", "Birliklar olinmadi: bazada birliklar ro'yxati topilmadi."));
            else
                lookups[f] = dict;
        }
        var deletedIdx = main.Cols.FindIndex(c => DeletedKeys.Contains(ProductImportMapping.Key(c.Name)));
        var groupIdx = main.Cols.FindIndex(c => GroupKeys.Contains(ProductImportMapping.Key(c.Name)));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", main.Cols.Select(c => Q(c.Name)))} FROM {Q(main.Name)}";
        using var r = cmd.ExecuteReader();
        var skippedFolders = 0;
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (deletedIdx >= 0 && FolderTableBuilder.Truthy(Str(r.GetValue(deletedIdx))))
                continue;
            if (groupIdx >= 0 && FolderTableBuilder.Truthy(Str(r.GetValue(groupIdx))))
            {
                skippedFolders++;
                continue;
            }
            var item = new Dictionary<ProductImportField, string>();
            foreach (var (field, ci) in cols)
            {
                if (field is not (ProductImportField.Barcode or ProductImportField.Name or ProductImportField.AltBarcodes or ProductImportField.Article or ProductImportField.Unit
                    or ProductImportField.Category or ProductImportField.Purchase or ProductImportField.Sale or ProductImportField.Quantity))
                    continue;
                var v = Str(r.GetValue(ci));
                if (lookups.TryGetValue(field, out var dict))
                    v = dict.TryGetValue(v, out var named) ? named : "";
                else if (field is ProductImportField.Category or ProductImportField.Unit && IsIdColumn(main.Cols[ci].Name))
                    v = ""; // ссылка без справочника — число в названии категории не нужно
                if (v.Length > 0)
                    item[field] = v;
            }
            if (!item.ContainsKey(ProductImportField.Name))
                continue;
            items.Add(item);
            pkValues.Add(pk is null ? "" : Str(r.GetValue(main.Cols.FindIndex(c => c.Name == pk))));
        }
        if (skippedFolders > 0)
            warnings.Add(Tr.T($"Строки-группы пропущены: {skippedFolders}.", $"Топ-саптар өткөрүлдү: {skippedFolders}.", $"Group rows skipped: {skippedFolders}.", $"Grup satırları atlandı: {skippedFolders}.", $"Guruh qatorlari o'tkazildi: {skippedFolders}."));
        return items;
    }

    private static void AddForeignKey(Tbl t, Dictionary<ProductImportField, int> cols, ProductImportField field, string[] words)
    {
        if (cols.TryGetValue(field, out var existing) && !IsIdColumn(t.Cols[existing].Name))
            return; // нормальный текстовый столбец («Категория», «Единица») — берём его
        for (var i = 0; i < t.Cols.Count; i++)
        {
            if (cols.ContainsValue(i) && !(cols.TryGetValue(field, out var e) && e == i))
                continue;
            var k = ProductImportMapping.Key(t.Cols[i].Name);
            if (IsIdColumn(t.Cols[i].Name) && words.Any(w => k.Contains(w, StringComparison.Ordinal)))
            {
                cols[field] = i;
                return;
            }
        }
    }

    /// <summary>Справочник «номер → название» для category_id / unit_id: таблица с «групп/категор/unit/measure/единиц» в имени.</summary>
    private static Dictionary<string, string>? LoadDictionary(SqliteConnection conn, List<Tbl> all, Tbl main, ProductImportField field)
    {
        var words = field == ProductImportField.Category
            ? new[] { "categor", "group", "категор", "групп", "razdel", "раздел" }
            : new[] { "unit", "measure", "uom", "единиц", "едизм", "birim" };
        foreach (var t in all.Where(t => t != main && t.Rows > 0 && t.Rows <= 100_000))
        {
            var k = ProductImportMapping.Key(t.Name);
            if (!words.Any(w => k.Contains(w, StringComparison.Ordinal)))
                continue;
            var pk = PkColumn(t);
            if (pk is null || !t.Map.Columns.TryGetValue(ProductImportField.Name, out var nameIdx))
                continue;
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Q(pk)}, {Q(t.Cols[nameIdx].Name)} FROM {Q(t.Name)}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                dict[Str(r.GetValue(0))] = Str(r.GetValue(1));
            return dict;
        }
        return null;
    }

    private static void MergeSatellites(SqliteConnection conn, Tbl main, List<Tbl> all, List<Dictionary<ProductImportField, string>> items, List<string> pkValues,
        List<string> warnings, CancellationToken ct)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pkValues.Count; i++)
            if (pkValues[i].Length > 0)
                index.TryAdd(pkValues[i], i);
        if (index.Count == 0)
            return;
        var manyPrices = false;
        var manyStocks = false;
        foreach (var side in all.Where(t => t != main && t.Rows > 0))
        {
            var fk = FkColumn(side, main);
            if (fk is null)
                continue;
            var m = side.Map;
            var want = new[] { ProductImportField.Barcode, ProductImportField.Sale, ProductImportField.Purchase, ProductImportField.Quantity }
                .Where(f => m.Columns.TryGetValue(f, out var ci) && side.Cols[ci].Name != fk).ToList();
            if (want.Count == 0)
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Q(fk)}, {string.Join(", ", want.Select(f => Q(side.Cols[m.Columns[f]].Name)))} FROM {Q(side.Name)}";
            using var r = cmd.ExecuteReader();
            var alts = new Dictionary<int, List<string>>();
            var stock = new Dictionary<int, decimal>();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!index.TryGetValue(Str(r.GetValue(0)), out var i))
                    continue;
                var item = items[i];
                for (var j = 0; j < want.Count; j++)
                {
                    var v = Str(r.GetValue(j + 1));
                    if (v.Length == 0)
                        continue;
                    switch (want[j])
                    {
                        case ProductImportField.Barcode:
                            if (!item.TryGetValue(ProductImportField.Barcode, out var main1) || main1.Length == 0)
                                item[ProductImportField.Barcode] = v;
                            else if (main1 != v)
                            {
                                if (!alts.TryGetValue(i, out var list))
                                    alts[i] = list = [];
                                if (!list.Contains(v))
                                    list.Add(v);
                            }
                            break;
                        case ProductImportField.Sale or ProductImportField.Purchase:
                            if (!item.ContainsKey(want[j]))
                                item[want[j]] = v;
                            else if (item[want[j]] != v)
                                manyPrices = true;
                            break;
                        case ProductImportField.Quantity:
                            if (!item.ContainsKey(ProductImportField.Quantity) && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var q))
                            {
                                stock[i] = stock.TryGetValue(i, out var cur) ? cur + q : q;
                                manyStocks |= cur != 0;
                            }
                            break;
                    }
                }
            }
            foreach (var (i, list) in alts)
            {
                var existing = new List<string>();
                if (items[i].TryGetValue(ProductImportField.AltBarcodes, out var e) && e.Length > 0)
                    existing.Add(e);
                existing.AddRange(list.Where(x => !existing.Contains(x)));
                items[i][ProductImportField.AltBarcodes] = string.Join("; ", existing);
            }
            foreach (var (i, q) in stock)
                items[i][ProductImportField.Quantity] = q.ToString(CultureInfo.InvariantCulture);
        }
        if (manyPrices)
            warnings.Add(Tr.T("У части товаров в базе несколько цен — взята первая. Проверьте цены на шаге «Проверьте».", "Айрым товарлардын базада бир нече баасы бар — биринчиси алынды. Баасын «Текшерүү» кадамында текшериңиз.",
                "Some products have several prices in the database — the first one was taken. Check the prices at the “Check” step.", "Bazı ürünlerin veritabanında birkaç fiyatı var — ilki alındı. Fiyatları «Kontrol» adımında kontrol edin.",
                "Ba'zi mahsulotlarning bazada bir nechta narxi bor — birinchisi olindi. Narxlarni «Tekshiruv» qadamida tekshiring."));
        if (manyStocks)
            warnings.Add(Tr.T("Остатки сложены по всем складам базы.", "Калдыктар базанын бардык кампалары боюнча кошулду.", "Stock was summed over all warehouses in the database.", "Stoklar veritabanındaki tüm depolar için toplandı.", "Qoldiqlar bazadagi barcha omborlar bo'yicha qo'shildi."));
    }

    private static ProductFileTable? FindClients(SqliteConnection conn, List<Tbl> tables, Tbl? main)
    {
        var t = tables.Where(x => x != main && x.Rows > 0 && x.Rows <= ProductFileReader.MaxRows && ClientsImportPlan.LooksLikeClients(x.Cols.Select(c => c.Name).ToList()))
            .OrderByDescending(x => x.Rows).FirstOrDefault();
        if (t is null)
            return null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", t.Cols.Select(c => Q(c.Name)))} FROM {Q(t.Name)}";
        using var r = cmd.ExecuteReader();
        var data = new List<string[]>();
        while (r.Read())
        {
            var row = new string[t.Cols.Count];
            for (var i = 0; i < row.Length; i++)
                row[i] = Str(r.GetValue(i));
            data.Add(row);
        }
        return FolderTableBuilder.BuildRaw(t.Name, t.Cols.Select(c => c.Name).ToArray(), data);
    }
}
