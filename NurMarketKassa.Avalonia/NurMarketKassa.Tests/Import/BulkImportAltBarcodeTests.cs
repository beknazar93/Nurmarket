using System.Text;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20 (просьба владельца: «в массовой загрузке нет доп. штрихкодов — товар приходит пачкой, продаётся поштучно»): доп. штрихкоды в мастере
/// массовой загрузки — разделители, «код:N», столбцы «Штрихкод упаковки» / «Штук в упаковке» / «Название варианта», конфликты, мердж с уже имеющимися кодами,
/// режим «одно название — один товар» и «несколько строк с одним штрихкодом».</summary>
public sealed class BulkImportAltBarcodeTests
{
    private const string Head = "Штрихкод;Название;Доп. штрихкоды;Штрихкод упаковки;Штук в упаковке;Название варианта;Количество;Закупка;Цена продажи;Единица\n";

    private static ProductFileTable Table(string csv) => ProductFileReader.ReadCsv(Encoding.UTF8.GetBytes(csv), "t.csv").Table!;

    private static BulkImportPlan Build(string rows, BulkImportSettings? settings = null, params CatalogProductTileVm[] catalog)
    {
        var table = Table(Head + rows);
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        return BulkImportPlan.Build(table, mapping, settings ?? new BulkImportSettings(), catalog);
    }

    private static string[] Codes(BulkImportRow row) => row.AltFull.Select(v => v.Barcode).ToArray();

    // ------------------------------------------------------------------ автораспознавание столбцов

    [Fact]
    public void Headers_of_pack_columns_are_detected_and_do_not_steal_barcode_or_quantity()
    {
        var table = Table(Head + "4600000000011;Хлеб;;;;;5;10;15;шт\n");
        var m = ProductImportMapping.AutoDetect(table.Headers, table);
        Assert.Equal(0, m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(2, m.ColumnOf(ProductImportField.AltBarcodes));
        Assert.Equal(3, m.ColumnOf(ProductImportField.PackBarcode));
        Assert.Equal(4, m.ColumnOf(ProductImportField.PackQty));
        Assert.Equal(5, m.ColumnOf(ProductImportField.AltName));
        Assert.Equal(6, m.ColumnOf(ProductImportField.Quantity));
    }

    [Theory]
    [InlineData("Кол-во в упаковке")]
    [InlineData("Количество в упаковке")]
    [InlineData("В упаковке")]
    [InlineData("Фасовка")]
    [InlineData("Pack")]
    [InlineData("Multiplicity")]
    [InlineData("Коэффициент")]
    [InlineData("Штук в пачке")]
    public void Pack_quantity_synonyms(string header)
    {
        var m = ProductImportMapping.AutoDetect(["Штрихкод", "Название", header, "Количество"]);
        Assert.Equal(2, m.ColumnOf(ProductImportField.PackQty));
        Assert.Equal(3, m.ColumnOf(ProductImportField.Quantity));
    }

    [Theory]
    [InlineData("Штрихкод пачки")]
    [InlineData("Штрихкод коробки")]
    [InlineData("Штрих-код упаковки")]
    [InlineData("Pack barcode")]
    [InlineData("Box barcode")]
    public void Pack_barcode_synonyms(string header)
    {
        var m = ProductImportMapping.AutoDetect(["Штрихкод", "Название", header]);
        Assert.Equal(0, m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(2, m.ColumnOf(ProductImportField.PackBarcode));
    }

    // ------------------------------------------------------------------ ячейка доп. кодов

    [Fact]
    public void Alt_cell_accepts_all_separators_and_collapses_duplicates_and_the_main_code()
    {
        var plan = Build("2000000000028;Новый;\"4600000000018; 4600000000025 4600000000032|4600000000049\n4600000000056,4600000000018,2000000000028\";;;;2;10;15;шт\n");
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.New, row.Kind);
        Assert.Equal(["4600000000018", "4600000000025", "4600000000032", "4600000000049", "4600000000056"], Codes(row));
        Assert.Equal(5, plan.AltCodeCount);
    }

    [Fact]
    public void Code_colon_quantity_makes_a_pack_variant()
    {
        var plan = Build("2000000000028;Новый;\"4600000000018:12; 4600000000025 : 6; 4600000000032\";;;;2;10;15;шт\n");
        var v = plan.Rows.Single().AltFull;
        Assert.Equal(3, v.Count);
        Assert.Equal(12, v.Single(x => x.Barcode == "4600000000018").Quantity);
        Assert.Equal(6, v.Single(x => x.Barcode == "4600000000025").Quantity);
        Assert.Equal(0, v.Single(x => x.Barcode == "4600000000032").Quantity);
        Assert.Equal(2, plan.AltPackCount);
    }

    [Fact]
    public void Repeated_code_in_cell_keeps_the_quantity_from_the_repeat()
    {
        var plan = Build("2000000000028;Новый;\"4600000000018; 4600000000018:12\";;;;2;10;15;шт\n");
        var v = plan.Rows.Single().AltFull.Single();
        Assert.Equal(12, v.Quantity);
    }

    // ------------------------------------------------------------------ упаковка отдельными столбцами

    [Fact]
    public void Pack_columns_give_a_variant_with_quantity_and_name()
    {
        var plan = Build("2000000000028;Вода 0,5;;4600000000100;24;Блок 24 шт;2;10;15;шт\n");
        var row = plan.Rows.Single();
        var v = Assert.Single(row.AltFull);
        Assert.Equal("4600000000100", v.Barcode);
        Assert.Equal(24, v.Quantity);
        Assert.Equal("Блок 24 шт", v.Name);
        Assert.Equal(1, plan.AltPackCount);
    }

    [Fact]
    public void Pack_quantity_empty_keeps_a_plain_extra_code()
    {
        var plan = Build("2000000000028;Вода;;4600000000100;;;2;10;15;шт\n");
        var v = Assert.Single(plan.Rows.Single().AltFull);
        Assert.Equal(0, v.Quantity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("200000")]
    public void Bad_pack_quantity_is_an_error_of_the_row_by_default(string qty)
    {
        var plan = Build($"2000000000028;Вода;;4600000000100;{qty};;2;10;15;шт\n");
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.Error, row.Kind);
        Assert.Equal(ProductImportField.PackQty, row.ErrorField);
        Assert.Contains("штук в упаковке", row.Error);
    }

    [Fact]
    public void Bad_pack_quantity_skips_only_the_code_when_asked()
    {
        var plan = Build("2000000000028;Вода;;4600000000100;0;;2;10;15;шт\n", new BulkImportSettings { SkipBadAltCodes = true });
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.New, row.Kind);
        Assert.Empty(row.AltFull);
        Assert.Equal(1, row.AltSkipped);
        Assert.Equal(1, plan.AltSkippedCount);
        Assert.Contains("пропущен", row.Warning);
    }

    [Fact]
    public void Pack_barcode_equal_to_main_is_not_loaded_with_a_warning()
    {
        var plan = Build("2000000000028;Вода;;2000000000028;24;;2;10;15;шт\n");
        var row = plan.Rows.Single();
        Assert.Empty(row.AltFull);
        Assert.Contains("совпадает с основным", row.Warning);
    }

    [Fact]
    public void Variant_name_goes_to_the_only_alt_code_and_is_ignored_for_several()
    {
        var one = Build("2000000000028;Вода;4600000000018;;;Пачка;2;10;15;шт\n").Rows.Single();
        Assert.Equal("Пачка", one.AltFull.Single().Name);
        var many = Build("2000000000028;Вода;\"4600000000018;4600000000025\";;;Пачка;2;10;15;шт\n").Rows.Single();
        Assert.All(many.AltFull, v => Assert.True(string.IsNullOrEmpty(v.Name)));
        Assert.Contains("несколько кодов", many.Warning);
    }

    // ------------------------------------------------------------------ проверка кодов

    [Fact]
    public void Non_digit_or_long_alt_code_is_an_error_or_a_skipped_code()
    {
        const string rows = "2000000000028;Вода;\"ABC123; 4600000000018\";;;;2;10;15;шт\n2000000000035;Чай;12345678901234567;;;;2;10;15;шт\n";
        var strict = Build(rows);
        Assert.All(strict.Rows, r => Assert.Equal(BulkImportRowKind.Error, r.Kind));
        var soft = Build(rows, new BulkImportSettings { SkipBadAltCodes = true });
        Assert.All(soft.Rows, r => Assert.Equal(BulkImportRowKind.New, r.Kind));
        Assert.Equal(["4600000000018"], Codes(soft.Rows[0]));
        Assert.Empty(soft.Rows[1].AltFull);
        Assert.Equal(2, soft.AltSkippedCount);
    }

    [Fact]
    public void Alt_code_of_another_catalog_product_fails_the_row_with_the_holder_name()
    {
        var other = new CatalogProductTileVm("o1", "Чужой товар", "", false) { Barcode = "4600000000018", Quantity = 1 };
        var plan = Build("2000000000028;Новый;4600000000018;;;;2;10;15;шт\n", null, other);
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.Error, row.Kind);
        Assert.Equal("доп. код 4600000000018 уже у товара «Чужой товар»", row.Error);
    }

    [Fact]
    public void Alt_code_of_another_catalog_product_skips_only_that_code_when_asked()
    {
        var other = new CatalogProductTileVm("o1", "Чужой товар", "", false) { Barcode = "4600000000018", Quantity = 1 };
        var plan = Build("2000000000028;Новый;\"4600000000018;4600000000025\";;;;2;10;15;шт\n", new BulkImportSettings { SkipBadAltCodes = true }, other);
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.New, row.Kind);
        Assert.Equal(["4600000000025"], Codes(row));
        Assert.Contains("уже у товара «Чужой товар»", row.Warning);
        Assert.Equal(1, plan.AltCodeCount);
        Assert.Equal(1, plan.AltSkippedCount);
    }

    [Fact]
    public void Alt_code_already_used_by_another_row_of_the_file_is_a_conflict()
    {
        var plan = Build("2000000000028;Первый;4600000000018;;;;2;10;15;шт\n2000000000035;Второй;4600000000018;;;;2;10;15;шт\n");
        Assert.Equal(BulkImportRowKind.New, plan.Rows[0].Kind);
        Assert.Equal(BulkImportRowKind.Error, plan.Rows[1].Kind);
        Assert.Contains("уже в строке", plan.Rows[1].Error);
        var soft = Build("2000000000028;Первый;4600000000018;;;;2;10;15;шт\n2000000000035;Второй;4600000000018;;;;2;10;15;шт\n", new BulkImportSettings { SkipBadAltCodes = true });
        Assert.Equal(BulkImportRowKind.New, soft.Rows[1].Kind);
        Assert.Empty(soft.Rows[1].AltFull);
    }

    [Fact]
    public void Alt_code_of_the_same_stock_product_is_not_a_conflict()
    {
        var own = new CatalogProductTileVm("e1", "Свой", "", false) { Barcode = "4600000000011", Quantity = 3 };
        var plan = Build("4600000000011;Свой;4600000000018;;;;2;10;15;шт\n", null, own);
        var row = plan.Rows.Single();
        Assert.Equal(BulkImportRowKind.Receipt, row.Kind);
        Assert.Equal(["4600000000018"], Codes(row));
    }

    // ------------------------------------------------------------------ товар склада: слияние, не теряя имеющееся

    private static CatalogProductTileVm StockProduct(params AlternateBarcodeVariant[] variants) =>
        new("e1", "Хлеб", "", false) { Barcode = "4600000000011", Quantity = 3, AlternateBarcodeVariants = variants.ToList() };

    [Fact]
    public void Existing_product_keeps_its_codes_and_gets_new_ones()
    {
        var tile = StockProduct(new AlternateBarcodeVariant { Barcode = "4600000000100", Name = "Блок", Quantity = 6 });
        var plan = Build("4600000000011;Хлеб;4600000000018:12;;;;2;10;15;шт\n", null, tile);
        var row = plan.Rows.Single();
        Assert.Equal(["4600000000100", "4600000000018"], Codes(row));
        Assert.Equal(6, row.AltFull[0].Quantity);
        Assert.Equal("Блок", row.AltFull[0].Name);
        Assert.Equal(12, row.AltFull[1].Quantity);
        Assert.Equal(1, row.AltAdded);
        Assert.Equal(1, plan.AltCodeCount);
    }

    [Fact]
    public void Existing_code_without_pack_quantity_gets_it_from_the_file_but_filled_quantity_is_kept()
    {
        var tile = StockProduct(new AlternateBarcodeVariant { Barcode = "4600000000100", Quantity = 0 }, new AlternateBarcodeVariant { Barcode = "4600000000118", Name = "Коробка", Quantity = 40 });
        var plan = Build("4600000000011;Хлеб;\"4600000000100:10; 4600000000118:99\";;;;2;10;15;шт\n", null, tile);
        var row = plan.Rows.Single();
        Assert.Equal(2, row.AltFull.Count);
        Assert.Equal(10, row.AltFull.Single(v => v.Barcode == "4600000000100").Quantity);
        Assert.Equal(40, row.AltFull.Single(v => v.Barcode == "4600000000118").Quantity);
        Assert.Equal("Коробка", row.AltFull.Single(v => v.Barcode == "4600000000118").Name);
        Assert.Equal(1, row.AltAdded);
    }

    [Fact]
    public void Switch_off_leaves_existing_products_alone_but_new_products_still_get_codes()
    {
        var tile = StockProduct();
        var plan = Build("4600000000011;Хлеб;4600000000018;;;;2;10;15;шт\n2000000000028;Новый;4600000000025;;;;2;10;15;шт\n",
            new BulkImportSettings { AddAltBarcodesToExisting = false }, tile);
        Assert.Empty(plan.Rows[0].AltFull);
        Assert.Equal(0, plan.Rows[0].AltAdded);
        Assert.Equal(["4600000000025"], Codes(plan.Rows[1]));
        Assert.Equal(1, plan.AltCodeCount);
    }

    [Fact]
    public void New_only_mode_does_not_touch_existing_products_codes()
    {
        var tile = StockProduct();
        var plan = Build("4600000000011;Хлеб;4600000000018;;;;2;10;15;шт\n", new BulkImportSettings { NewOnly = true }, tile);
        Assert.True(plan.Rows.Single().Unchanged);
        Assert.Equal(0, plan.AltCodeCount);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void Server_merge_adds_pack_quantity_to_a_code_that_the_server_has_without_it()
    {
        using var doc = JsonDocument.Parse("{\"alternate_barcodes\":[{\"barcode\":\"4600000000100\",\"name\":\"\",\"quantity\":0},{\"barcode\":\"4600000000118\",\"name\":\"Короб\",\"quantity\":40},\"4600000000125\"]}");
        var planned = new List<AlternateBarcodeVariant>
        {
            new() { Barcode = "4600000000100", Name = "Пачка", Quantity = 10 },
            new() { Barcode = "4600000000118", Quantity = 99 },
            new() { Barcode = "4600000000132", Quantity = 5 },
        };
        var merged = BulkCardsLoader.MergeAlternates(doc.RootElement, planned);
        Assert.Equal(4, merged.Count);
        Assert.Equal(10, merged.Single(v => v.Barcode == "4600000000100").Quantity);
        Assert.Equal("Пачка", merged.Single(v => v.Barcode == "4600000000100").Name);
        Assert.Equal(40, merged.Single(v => v.Barcode == "4600000000118").Quantity); // уже заполненное на сервере не меняем
        Assert.Contains(merged, v => v.Barcode == "4600000000125"); // код, добавленный на сайте после загрузки каталога, не теряется
        Assert.Equal(5, merged.Single(v => v.Barcode == "4600000000132").Quantity);
    }

    // ------------------------------------------------------------------ несколько строк на один товар

    [Fact]
    public void Several_rows_with_one_barcode_collect_extra_codes_into_one_product()
    {
        var plan = Build("2000000000028;Вода;4600000000018;;;;5;10;15;шт\n2000000000028;Вода;;4600000000100;24;Блок;;;;шт\n");
        Assert.Equal(BulkImportRowKind.New, plan.Rows[0].Kind);
        Assert.Equal(BulkImportRowKind.Merged, plan.Rows[1].Kind);
        var row = plan.Rows[0];
        Assert.Equal(2, row.AltFull.Count);
        var pack = row.AltFull.Single(v => v.Barcode == "4600000000100");
        Assert.Equal(24, pack.Quantity);
        Assert.Equal("Блок", pack.Name);
        Assert.Equal(5, plan.Lines.Single().Quantity); // пустое количество второй строки остаток не меняет
    }

    [Fact]
    public void Add_row_with_a_quantity_warns_that_it_was_added()
    {
        var plan = Build("2000000000028;Вода;;;;;5;10;15;шт\n2000000000028;Вода;4600000000018;;;;5;;;шт\n");
        Assert.Equal(10, plan.Lines.Single().Quantity);
        Assert.Contains("оставьте количество пустым", plan.Rows[1].Warning);
    }

    [Fact]
    public void Same_name_different_codes_merge_into_one_product_with_extra_codes_and_pack_quantity()
    {
        var plan = Build("2000000000028;Печенье;;;;;5;10;15;шт\n2000000000035;Печенье;;4600000000100;12;Пачка;3;10;15;шт\n2000000000042;Печенье;4600000000018:6;;;;2;10;15;шт\n",
            new BulkImportSettings { MergeSameName = true });
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(2, plan.MergedCount);
        var row = plan.Rows[0];
        Assert.Equal(10, plan.Lines.Single().Quantity);
        Assert.Equal(["2000000000035", "4600000000100", "2000000000042", "4600000000018"], Codes(row));
        Assert.Equal(12, row.AltFull.Single(v => v.Barcode == "4600000000100").Quantity);
        Assert.Equal(6, row.AltFull.Single(v => v.Barcode == "4600000000018").Quantity);
        Assert.Equal(4, plan.AltCodeCount);
    }

    [Fact]
    public void First_row_mode_still_drops_the_repeated_row()
    {
        var plan = Build("2000000000028;Вода;;;;;5;10;15;шт\n2000000000028;Вода;4600000000018;;;;5;;;шт\n", new BulkImportSettings { Duplicates = BulkImportDuplicateMode.FirstRow });
        Assert.Equal(BulkImportRowKind.Error, plan.Rows[1].Kind);
        Assert.Equal(5, plan.Lines.Single().Quantity);
    }

    // ------------------------------------------------------------------ образец файла

    [Fact]
    public void Template_csv_has_the_pack_columns_and_loads_with_pack_quantity()
    {
        var table = Table(ProductImportTemplate.BuildCsv());
        var m = ProductImportMapping.AutoDetect(table.Headers, table);
        Assert.True(m.ColumnOf(ProductImportField.AltBarcodes) >= 0);
        Assert.True(m.ColumnOf(ProductImportField.PackBarcode) >= 0);
        Assert.True(m.ColumnOf(ProductImportField.PackQty) >= 0);
        var plan = BulkImportPlan.Build(table, m, new BulkImportSettings(), []);
        var bread = plan.Rows[0];
        Assert.Equal(BulkImportRowKind.New, bread.Kind);
        Assert.Equal(3, bread.AltFull.Count);
        Assert.Equal(10, bread.AltFull.Single(v => v.Barcode == "4600000000100").Quantity);
        Assert.Equal(BulkImportRowKind.New, plan.Rows[1].Kind);
    }

    [Fact]
    public void Sample_file_in_docs_loads_without_errors()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "docs", "Образцы", "bulk-import-extra-barcodes.csv");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }
        if (path is null)
            return; // тест запущен не из репозитория
        var table = ProductFileReader.ReadCsv(File.ReadAllBytes(path), "bulk-import-extra-barcodes.csv").Table!;
        var m = ProductImportMapping.AutoDetect(table.Headers, table);
        var plan = BulkImportPlan.Build(table, m, new BulkImportSettings(), []);
        Assert.Equal(0, plan.ErrorCount);
        Assert.True(plan.AltCodeCount >= 4);
        Assert.True(plan.AltPackCount >= 3);
    }
}
