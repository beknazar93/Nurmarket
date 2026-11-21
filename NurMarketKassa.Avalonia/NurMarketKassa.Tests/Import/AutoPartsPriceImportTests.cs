using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20 (ТЗ «Сфера Автозапчасти», пакеты импорта): профиль «Прайс поставщика (автозапчасти)» — синонимы столбцов, строка
/// заголовков не первая, строки-группы и без цены, остаток диапазоном, склейка названия, CP1251, повторная загрузка без дублей,
/// чтение образцов из docs/Образцы/Автозапчасти (только чтение; если файлов нет локально — проверка пропускается).</summary>
[Collection("UserPreferences")] // PartMetaStore / AutoPartsProductMeta — общее состояние
public sealed class AutoPartsPriceImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ap-price-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsPriceImportTests()
    {
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
        AutoPartsProductMeta.FileOverride = Path.Combine(_dir, "autoparts-products.json");
        AutoPartsImportLog.FileOverride = Path.Combine(_dir, "autoparts-import-log.json"); // 2026-11-20: журнал импорта (AfterSaved пишет в него)
        AutoPartsImportLog.ResetForTests();
    }

    public void Dispose()
    {
        AutoPartsImportLog.FileOverride = null;
        AutoPartsImportLog.ResetForTests();
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        AutoPartsProductMeta.FileOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static ProductFileTable Table(string text)
    {
        var r = ProductFileReader.ReadClipboard(text);
        Assert.True(r.Ok, r.Error);
        return r.Table!;
    }

    /// <summary>Таблица с найденной строкой заголовков (как в окне).</summary>
    private static ProductFileTable Prepared(string text)
    {
        var t = Table(text);
        var h = AutoPartsPriceImport.FindHeaderRow(t);
        Assert.True(h >= 0, "строка заголовков не найдена");
        t.SetHeaderRow(h);
        return t;
    }

    private static CatalogProductTileVm Tile(string id, string barcode, string article, string brand, double qty = 3) =>
        new(id, "Товар " + id, "", false) { Barcode = barcode, Article = article, Brand = brand, Quantity = qty };

    // ------------------------------------------------------------------ синонимы столбцов

    [Fact]
    public void Detect_maps_all_synonyms_of_the_demo_catalog()
    {
        var headers = "Название;Бренд;Каталожный номер;OEM;Кросс-номера (через запятую);Подходит к;Состояние;Страна;Ячейка;Закупка;Розница;Цена СТО;Остаток;Гарантия (дней);Штрихкод".Split(';');
        var map = AutoPartsPriceImport.Detect(headers, out var stock);
        Assert.Equal(2, map[PartColumn.PartNo]);
        Assert.Equal(1, map[PartColumn.Brand]);
        Assert.Equal(3, map[PartColumn.Oem]);
        Assert.Equal(4, map[PartColumn.Cross]);
        Assert.Equal(5, map[PartColumn.Fits]);
        Assert.Equal(6, map[PartColumn.Condition]);
        Assert.Equal(8, map[PartColumn.Location]);
        Assert.Equal(9, map[PartColumn.Purchase]);
        Assert.Equal(10, map[PartColumn.Sale]); // «Цена СТО» не становится ценой
        Assert.Equal(12, map[PartColumn.Stock]);
        Assert.Equal(13, map[PartColumn.Warranty]);
        Assert.Equal(14, map[PartColumn.Barcode]);
        Assert.Equal(0, map[PartColumn.Name]);
        Assert.Equal([12], stock);
    }

    [Theory]
    [InlineData("Part Number", PartColumn.PartNo)]
    [InlineData("Номер детали", PartColumn.PartNo)]
    [InlineData("Артикул", PartColumn.PartNo)]
    [InlineData("Оригинальный номер", PartColumn.Oem)]
    [InlineData("Аналог", PartColumn.Cross)]
    [InlineData("Производитель", PartColumn.Brand)]
    [InlineData("Применимость", PartColumn.Fits)]
    [InlineData("Место", PartColumn.Location)]
    [InlineData("Наличие", PartColumn.Stock)]
    [InlineData("Цена опт", PartColumn.Purchase)]
    [InlineData("Прайс", PartColumn.Sale)]
    public void Detect_knows_each_synonym(string header, PartColumn expected)
    {
        var map = AutoPartsPriceImport.Detect([header], out _);
        Assert.True(map.TryGetValue(expected, out var col) && col == 0, $"«{header}» → ожидали {expected}");
    }

    [Fact]
    public void Detect_article_wins_over_product_code_and_sums_warehouse_stock()
    {
        var map = AutoPartsPriceImport.Detect(["Код1с", "Артикул", "OEM", "Наименование", "Цена Руб", "ПЕЧАТНИКИ(Остаток)", "ОМСК(Остаток)", "ПЕЧАТНИКИ(Ожидаем)", "Производитель", "НомерПроизводителя"], out var stock);
        Assert.Equal(1, map[PartColumn.PartNo]);
        Assert.Equal(8, map[PartColumn.Brand]);
        Assert.Equal([5, 6], stock); // «Ожидаем» не остаток
    }

    // ------------------------------------------------------------------ строка заголовков, группы, мусор

    private const string AvtoSkazka =
        "19 ноября 2026\n"
        + "Наименование;Код товара;Производство;Артикул;Цена\n"
        + "Амортизаторы\n"
        + "Амортизатор газовый передний левый Kia PJB049A;i05115;;PJB049A;5240\n"
        + "Амортизатор газовый передний лев. Ford DG11057;i14337;;DG11057;5700\n"
        + "Герметик\n"
        + "Герметик шин 450 мл., ABRO;00571;ABRO;;500\n"
        + "Фонарь без цены;i1;;ZZ1;\n";

    [Fact]
    public void Header_is_not_the_first_row_and_groups_and_priceless_rows_are_skipped()
    {
        var t = Table(AvtoSkazka);
        Assert.Equal(1, AutoPartsPriceImport.FindHeaderRow(t)); // 0 — дата
        t.SetHeaderRow(1);
        var p = AutoPartsPriceImport.Prepare(t, null, []);
        Assert.Equal(3, p.Created);
        Assert.Equal(3, p.Skipped);
        Assert.Equal(["группа", "группа", "нет цены"], p.Rows.Where(r => r.Status == AutoPartsRowStatus.Skipped).Select(r => r.SkipReason).ToArray());
        // без штрихкода — внутренний EAN-13 (как «Сгенерировать штрихкоды»); номер детали — в артикул
        var first = p.Rows.First(r => r.Status == AutoPartsRowStatus.New);
        Assert.Equal(13, first.Barcode.Length);
        Assert.Equal("PJB049A", first.PartNo);
        Assert.Equal(p.Rows.Where(r => r.Status == AutoPartsRowStatus.New).Select(r => r.Barcode).Distinct().Count(), 3);
    }

    // ------------------------------------------------------------------ остаток диапазоном

    [Theory]
    [InlineData("20–50", 20)]
    [InlineData("20-50", 20)]
    [InlineData("менее 8", 8)]
    [InlineData("<8", 8)]
    [InlineData(">10", 10)]
    [InlineData("более 10", 10)]
    [InlineData("есть", 1)]
    [InlineData("в наличии", 1)]
    [InlineData("нет", 0)]
    [InlineData("12.0", 12)]
    [InlineData("7", 7)]
    public void ParseStock_handles_ranges_and_words(string text, double expected)
    {
        var v = AutoPartsPriceImport.ParseStock(text, out var note);
        Assert.Equal(expected, v);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            Assert.NotEqual("", note); // пометка для предпросмотра
    }

    [Fact]
    public void ParseStock_empty_and_garbage_are_not_loaded()
    {
        Assert.Null(AutoPartsPriceImport.ParseStock("", out _));
        Assert.Null(AutoPartsPriceImport.ParseStock("уточняйте у менеджера", out var note));
        Assert.NotEqual("", note);
    }

    [Theory]
    [InlineData("5240", 5240)]
    [InlineData("1 234,50", 1234.5)]
    [InlineData("22230.0", 22230)]
    [InlineData("1.234,50", 1234.5)]
    [InlineData("500 сом", 500)]
    public void ParseNumber_reads_prices(string text, double expected) => Assert.Equal(expected, AutoPartsPriceImport.ParseNumber(text));

    // ------------------------------------------------------------------ склейка названия

    [Fact]
    public void Name_is_joined_from_several_columns_when_there_is_no_name_column()
    {
        var t = Prepared("Бренд;Модель;Типоразмер;Сезон;Цена, руб.;Наличие;Артикул\n"
            + "Bridgestone;Alenza 001;255/55 R19;лето;22230.0;8–20;244524\n"
            + "Bridgestone;Alenza 001;255/55 R19;лето;24020.0;менее 8;255511\n");
        var p = AutoPartsPriceImport.Prepare(t, null, []);
        Assert.Equal(2, p.Created);
        Assert.Equal("Bridgestone Alenza 001 255/55 R19 лето", p.Rows[0].Name);
        Assert.Equal(8, p.Rows[0].Stock); // «8–20» → нижняя граница
        Assert.Equal(8, p.Rows[1].Stock); // «менее 8» → 8
        Assert.Equal(2, p.ApproxStock);
        Assert.Contains("8–20", p.Rows[0].StockNote);
    }

    [Fact]
    public void Name_columns_chosen_by_the_user_are_used()
    {
        var t = Prepared("Наименование;Модель;Цена;Артикул\nФильтр масляный;Mann;300;W 811/80\n");
        var p = AutoPartsPriceImport.Prepare(t, new AutoPartsPriceOptions { NameColumns = [0, 1] }, []);
        Assert.Equal("Фильтр масляный Mann", p.Rows[0].Name);
    }

    // ------------------------------------------------------------------ CP1251

    [Fact]
    public void Windows1251_csv_is_decoded_and_prepared()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var csv = "Код1с;Артикул;Наименование;Цена Руб;Остаток;Производитель\r\n33891;AI0Q508-220;СПОЙЛЕР БАМПЕРА ПЕРЕДН;6370;0;API\r\n";
        var r = ProductFileReader.ReadCsv(Encoding.GetEncoding(1251).GetBytes(csv), "p.csv");
        Assert.True(r.Ok, r.Error);
        Assert.Contains("1251", r.Table!.EncodingName);
        r.Table.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(r.Table));
        var p = AutoPartsPriceImport.Prepare(r.Table, null, []);
        Assert.Equal(1, p.Created);
        Assert.Equal("СПОЙЛЕР БАМПЕРА ПЕРЕДН", p.Rows[0].Name);
        Assert.Equal("API", p.Rows[0].Brand);
    }

    // ------------------------------------------------------------------ повторная загрузка без дублей

    [Fact]
    public void Reimport_updates_by_part_number_and_brand_and_does_not_create_duplicates()
    {
        var csv = "Название;Бренд;Каталожный номер;Остаток;Розница\n"
            + "Фильтр масляный;Mann;W 811/80;5;450\n"
            + "Фильтр масляный;Bosch;W 811/80;2;500\n"
            + "Колодки;Ferodo;FDB 1234;4;2500\n";
        var first = AutoPartsPriceImport.Prepare(Prepared(csv), null, []);
        Assert.Equal(3, first.Created);

        // «создаём» товары: плитки каталога с теми же артикулом и брендом (номер в другом написании не мешает)
        var catalog = first.Rows.Select((r, i) => Tile("id" + i, r.Barcode, r.PartNo.Replace(" ", "-"), r.Brand, 5)).ToList();
        var again = AutoPartsPriceImport.Prepare(Prepared(csv.Replace("450", "470")), null, catalog);
        Assert.Equal(0, again.Created);
        Assert.Equal(3, again.Updated);
        // тот же штрихкод у обновляемых — план найдёт существующий товар, а не заведёт новый
        Assert.Equal(first.Rows.Select(r => r.Barcode), again.Rows.Select(r => r.Barcode));
        // Mann и Bosch с одним номером — разные товары (по бренду)
        Assert.NotEqual(again.Rows[0].Barcode, again.Rows[1].Barcode);
    }

    [Fact]
    public void Duplicate_part_number_and_brand_inside_one_file_is_skipped()
    {
        var p = AutoPartsPriceImport.Prepare(Prepared("Название;Бренд;Артикул;Цена\nА;Mann;W 811/80;100\nБ;mann;W-811-80;120\n"), null, []);
        Assert.Equal(1, p.Created);
        Assert.Equal(1, p.Skipped);
        Assert.Contains("повтор", p.Rows[1].SkipReason);
    }

    [Fact]
    public void Plan_built_from_prepared_table_creates_new_and_updates_existing_with_part_data()
    {
        var csv = "Название;Бренд;Каталожный номер;OEM;Кросс;Подходит к;Состояние;Ячейка;Гарантия;Остаток;Закупка;Розница\n"
            + "Фильтр;Mann;W 811/80;1K0 129 620;Bosch 0 451 103 316, Knecht OC 983;Audi A3 | VW Golf;Оригинал;B-01-1;30;5;300;450\n"
            + "Колодки;Ferodo;FDB 1234;;;;Аналог;;;2;1000;1500\n";
        var existing = Tile("e1", "4600000000011", "FDB1234", "Ferodo", 1);
        var prepared = AutoPartsPriceImport.Prepare(Prepared(csv), null, [existing]);
        Assert.Equal(1, prepared.Created);
        Assert.Equal(1, prepared.Updated);

        var plan = BulkImportPlan.Build(prepared.ToTable(), AutoPartsPrepared.TableMapping(), new BulkImportSettings { CardsOnly = true }, [existing]);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(1, plan.ReceiptCount);
        var newRow = plan.Rows.Single(r => r.Kind == BulkImportRowKind.New);
        var data = Assert.IsType<AutoPartsRowData>(newRow.Extra);
        Assert.Equal("Mann", data.Brand);
        Assert.Equal("W 811/80", data.Article);
        Assert.Equal("W811/80".Replace("/", ""), data.Info.PartNo.Replace("/", ""));
        Assert.Contains("1K0129620", data.Info.Oem);
        Assert.Equal("B-01-1", data.Info.Location);
        Assert.Equal(("new", "original"), (data.Info.Condition, data.Info.PartType));
        Assert.Equal(30, data.WarrantyDays);
        Assert.Equal(5, data.Stock);

        // тело создания — те же поля, что у карточки (PartMetaBody)
        var request = new ProductEditRequest { Name = "Фильтр", Barcode = newRow.Line!.Barcode };
        data.ApplyToCreate(request);
        Assert.Equal("Mann", request.BrandName);
        Assert.Equal("W 811/80", request.Article);
        Assert.NotNull(request.Attributes);
        Assert.Contains(request.Attributes!, a => a.Key == "oem");
        Assert.True(request.SendStorageLocation);
        Assert.Equal("B-01-1", request.StorageLocation);
        Assert.Equal(5, request.Quantity);

        // запись после создания: данные запчасти и гарантия запоминаются по id
        data.AfterSaved("p-77");
        Assert.Equal("B-01-1", PartMetaStore.Get("p-77")!.Location);
        Assert.Equal(30, AutoPartsProductMeta.Get("p-77")!.WarrantyDays);

        // существующий: бренд, ячейка и остаток могли измениться — PATCH только изменившихся полей
        var upd = plan.Rows.Single(r => r.Kind == BulkImportRowKind.Receipt);
        var updData = Assert.IsType<AutoPartsRowData>(upd.Extra);
        Assert.False(updData.IsNew);
        var fields = new Dictionary<string, object?>();
        updData.AddPatchFields(fields);
        Assert.Equal(2d, fields["quantity"]);
        Assert.DoesNotContain("brand_name", fields.Keys); // бренд тот же
    }

    [Fact]
    public void Cashless_price_column_is_not_taken_for_stock()
    {
        // 2026-11-20: avtogarant-offline.xls — «Наличный расчет» / «Безналичный расчет» (две цены), остатка в файле нет
        var map = AutoPartsPriceImport.Detect(["№", "Наименование товаров", "Артикул", "Наличный расчет", "Безналичный расчет", "Ед.", "Заказ", "Бренд", "Ссылка"], out var stock);
        Assert.Equal(3, map[PartColumn.Sale]);
        Assert.False(map.ContainsKey(PartColumn.Stock));
        Assert.Empty(stock);
    }

    [Fact]
    public void Existing_product_without_article_is_found_by_the_barcode_from_the_file()
    {
        // 2026-11-20: товар заведён прежней загрузкой по штрихкоду (артикула и бренда нет) — прайс с тем же штрихкодом обновляет его, а не уходит «без изменений»
        var existing = Tile("e1", "4600000000011", "", "", 3);
        var p = AutoPartsPriceImport.Prepare(Prepared("Штрихкод;Название;Бренд;Каталожный номер;Остаток;Розница\n4600000000011;Фильтр;Mann;W 811/80;3;450\n"), null, [existing]);
        Assert.Equal(0, p.Created);
        Assert.Equal(1, p.Updated);
        Assert.Equal("4600000000011", p.Rows[0].Barcode);
        Assert.True(p.Rows[0].Data!.NeedsWrite);
        Assert.True(p.Rows[0].Data!.NeedsArticle);
        Assert.True(p.Rows[0].Data!.NeedsBrand);
    }

    [Fact]
    public void Stock_of_existing_is_not_changed_when_the_option_is_off()
    {
        var existing = Tile("e1", "4600000000011", "W811", "Mann", 1);
        var p = AutoPartsPriceImport.Prepare(Prepared("Название;Бренд;Артикул;Остаток;Цена\nФильтр;Mann;W811;9;100\n"), new AutoPartsPriceOptions { UpdateStock = false }, [existing]);
        Assert.False(p.Rows[0].Data!.NeedsStock);
    }

    // ------------------------------------------------------------------ образцы из docs (только чтение)

    private static string? Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "docs", "Образцы", "Автозапчасти", name);
            if (File.Exists(path))
                return path;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void Demo_catalog_csv_is_prepared_without_skips()
    {
        var path = Sample("demo-autoparts-catalog.csv");
        if (path is null)
            return; // образцов нет в этой копии
        var r = ProductFileReader.ReadFile(path);
        Assert.True(r.Ok, r.Error);
        Assert.True(AutoPartsPriceImport.LooksLikePrice(r.Table!));
        r.Table!.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(r.Table));
        var p = AutoPartsPriceImport.Prepare(r.Table, null, []);
        Assert.Equal(170, p.Created);
        Assert.Equal(0, p.Skipped);
        Assert.True(p.Rows.All(x => x.Barcode.Length >= 4 && x.PartNo.Length > 0 && x.Sale > 0));
        Assert.True(p.Rows.Any(x => x.Data!.Info.Oem.Count > 0 && x.Data.Info.Location.Length > 0 && x.Data.WarrantyDays > 0));
        // план на очищенной таблице принимает все 170 позиций
        var plan = BulkImportPlan.Build(p.ToTable(), AutoPartsPrepared.TableMapping(), new BulkImportSettings { CardsOnly = true }, []);
        Assert.Equal(170, plan.NewCount);
        Assert.Equal(0, plan.ErrorCount);
    }

    [Theory]
    [InlineData("avtoskazka-price-foreign.xlsx")]
    [InlineData("avtoskazka-price-oil.xlsx")]
    public void Avtoskazka_xlsx_skips_groups_and_reads_part_numbers(string file)
    {
        var path = Sample(file);
        if (path is null)
            return; // образцов нет в этой копии
        var r = ProductFileReader.ReadFile(path);
        Assert.True(r.Ok, r.Error);
        Assert.True(AutoPartsPriceImport.LooksLikePrice(r.Table!));
        r.Table!.SetHeaderRow(AutoPartsPriceImport.FindHeaderRow(r.Table));
        Assert.True(r.Table.HeaderIndex >= 1, "шапка с датой выше заголовков");
        var p = AutoPartsPriceImport.Prepare(r.Table, null, []);
        Assert.True(p.Created > 300);
        Assert.True(p.Rows.Any(x => x.Status == AutoPartsRowStatus.Skipped && x.SkipReason == "группа"));
        Assert.True(p.Rows.Where(x => x.Status != AutoPartsRowStatus.Skipped).All(x => x.Sale > 0));
    }
}
