using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-20: профиль импорта «CT MAX» (выгрузка товаров: «Штрих Код», «Наименование», «Цена», «Закупочная»; заголовок не в первой строке; строки-группы без
/// штрихкода; служебные коды «*10»; остатка нет) и плашка «это не список товаров». Фикстура синтетическая (не копия настоящей выгрузки).</summary>
public sealed class CtMaxImportTests
{
    // две пустые строки сверху, как в выгрузке; группы идут отдельным блоком в начале
    private const string Csv =
        ";;;\n;;;\n"
        + "Штрих Код;Наименование;Цена;Закупочная\n"
        + ";Группа Альфа;0;\n;Группа Бета;0;\n;Группа Гамма ;0;\n"
        + "4600000000011;Мыло Альфа 90 г;45;30\n"
        + "4600000000028;Шампунь Бета 250 мл;180;120\n"
        + "460000000003;Крем 50 мл;95;60\n"       // 12 цифр (UPC-A)
        + "46000007;Спички;10;6\n"                // 8 цифр
        + "4600000000042;Зубная паста;0;55\n"     // цена 0, закупка есть
        + "4600000000059;Зубная щётка;70;\n"      // закупки нет
        + "*10;Пакет малый;5;2\n"                 // служебный код
        + "*3030;Мелочь по весу;1;\n"
        + "*-;Пакет большой;8;3\n"
        + ",,ыфысфыс;Товар с мусором в коде;50;30\n"
        + "4600000000066;Мыло Альфа 90 г;45;30\n"  // то же название, другой код
        + ";Группа Омега;0;\n";                    // группа в конце файла

    private static ProductFileTable Table(string csv = Csv) => ProductFileReader.ReadCsv(Encoding.UTF8.GetBytes(csv), "ctmax.csv").Table!;

    private static BulkImportPlan Build(BulkImportSettings? settings = null, params CatalogProductTileVm[] catalog)
    {
        var table = Table();
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        return BulkImportPlan.Build(table, mapping, settings ?? new BulkImportSettings(), catalog);
    }

    [Fact]
    public void Header_below_empty_rows_is_found_and_the_source_is_recognised_as_ct_max()
    {
        var table = Table();
        Assert.Equal("Штрих Код", table.Headers[0]);
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        Assert.Equal("ctmax", mapping.Source?.Id);
        Assert.Equal("CT MAX", mapping.Source?.Name);
        Assert.Equal(0, mapping.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(1, mapping.ColumnOf(ProductImportField.Name));
        Assert.Equal(2, mapping.ColumnOf(ProductImportField.Sale));
        Assert.Equal(3, mapping.ColumnOf(ProductImportField.Purchase));
        Assert.False(mapping.HasQuantity);
    }

    [Fact]
    public void Ct_max_is_in_the_source_list_with_a_how_to_export_text()
    {
        var profile = ImportSourceProfiles.ById("ctmax");
        Assert.NotNull(profile);
        Assert.Contains("CT MAX", profile!.HowToExport);
        Assert.DoesNotContain("Ситимакс", profile.HowToExport);
    }

    [Fact]
    public void Plain_header_names_do_not_look_like_ct_max()
    {
        Assert.Null(ImportSourceProfiles.Detect(["Штрихкод", "Название", "Цена продажи", "Закупка"]));
    }

    [Fact]
    public void Group_rows_are_skipped_counted_and_are_not_errors_and_load_is_cards_and_prices_only()
    {
        var plan = Build();
        Assert.Equal(4, plan.GroupRowsSkipped);
        Assert.True(plan.CardsOnly);
        Assert.DoesNotContain(plan.Rows, r => r.Name.StartsWith("Группа", StringComparison.Ordinal));
        Assert.Equal(0, plan.Rows.Count(r => r.Error is not null && r.Error.Contains("нет штрихкода")));
    }

    [Fact]
    public void Service_codes_get_a_stable_valid_internal_barcode_and_keep_the_old_code_in_the_article()
    {
        var first = Build();
        var again = Build();
        var service = first.Rows.Where(r => r.InternalCode).ToList();
        Assert.Equal(4, service.Count); // *10, *3030, *-, мусор
        foreach (var row in service)
        {
            Assert.Equal(BulkImportRowKind.New, row.Kind);
            Assert.Equal(13, row.Barcode.Length);
            Assert.All(row.Barcode, ch => Assert.True(char.IsDigit(ch)));
            Assert.NotEqual('0', row.Barcode[0]);
            Assert.NotEqual('2', row.Barcode[0]);
            Assert.True(BarcodeGenerator.HasValidCheckDigit(row.Barcode));
            Assert.False(string.IsNullOrEmpty(row.Article));
            Assert.Contains("NurCRM", row.Warning);
        }
        Assert.Equal(["*10", "*3030", "*-", ",,ыфысфыс"], service.Select(r => r.Article).ToArray());
        // повторная проверка даёт те же коды: «Продолжить загрузку» не создаст дублей
        Assert.Equal(service.Select(r => r.Barcode), again.Rows.Where(r => r.InternalCode).Select(r => r.Barcode));
        Assert.Equal(4, first.InternalCodeCount);
        Assert.Contains("нет цифр", service[3].Warning);
        Assert.Equal(4, service.Select(r => r.Barcode).Distinct().Count());
    }

    [Fact]
    public void Service_code_product_created_by_the_previous_run_is_found_by_its_code_and_not_duplicated()
    {
        var first = Build();
        var made = first.Rows.First(r => r.Article == "*10");
        var existing = new CatalogProductTileVm("p1", "Пакет малый", "", false) { Barcode = made.Barcode, Quantity = 0 };
        var second = Build(null, existing);
        var row = second.Rows.First(r => r.Article == "*10");
        Assert.Equal(made.Barcode, row.Barcode);
        Assert.Equal(BulkImportRowKind.Receipt, row.Kind);
        Assert.Equal(first.NewCount - 1, second.NewCount);
    }

    [Fact]
    public void Switch_off_keeps_service_codes_as_errors_with_a_hint_and_no_product_is_silently_changed()
    {
        var plan = Build(new BulkImportSettings { GenerateInternalCodes = false });
        var bad = plan.Rows.Where(r => r.Kind == BulkImportRowKind.Error && r.Error!.Contains("только цифры")).ToList(); // (строка с нулевой ценой — отдельная ошибка «укажите цену»)
        Assert.Equal(4, bad.Count);
        Assert.All(bad, r => Assert.Contains("только цифры", r.Error));
        Assert.Equal(0, plan.InternalCodeCount);
    }

    [Fact]
    public void Zero_sale_price_with_purchase_gives_a_warning_and_normal_codes_are_kept()
    {
        var plan = Build();
        var paste = plan.Rows.Single(r => r.Barcode == "4600000000042");
        Assert.Contains("цена продажи 0", paste.Warning);
        Assert.Contains(plan.Rows, r => r.Barcode == "460000000003" && r.Kind == BulkImportRowKind.New); // UPC-A
        Assert.Contains(plan.Rows, r => r.Barcode == "46000007" && r.Kind == BulkImportRowKind.New);   // 8 цифр
        Assert.Equal(2, plan.Rows.Count(r => r.Name == "Мыло Альфа 90 г")); // одинаковые названия с разными кодами — два товара
    }

    // ------------------------------------------------------------------ «это не список товаров»

    [Fact]
    public void A_file_with_emails_in_the_barcode_column_looks_like_not_a_product_list()
    {
        var csv = "Компания;Почта владельца;Цена в мес, сом\n"
            + string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Фирма {i};owner{i}@mail.test;{1000 + i}"));
        var table = Table(csv);
        var mapping = new ProductImportMapping();
        mapping.Set(ProductImportField.Barcode, 1);
        mapping.Set(ProductImportField.Name, 0);
        mapping.Set(ProductImportField.Sale, 2);
        var plan = BulkImportPlan.Build(table, mapping, new BulkImportSettings(), []);
        Assert.True(plan.ErrorCount > 0);
        Assert.True(BulkImportPlan.LooksLikeNotProductList(table, mapping, plan));
    }

    [Fact]
    public void Real_product_files_do_not_trigger_the_not_a_product_list_notice()
    {
        var table = Table();
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        var plan = BulkImportPlan.Build(table, mapping, new BulkImportSettings(), []);
        Assert.False(BulkImportPlan.LooksLikeNotProductList(table, mapping, plan));
        var template = Table(ProductImportTemplate.BuildCsv());
        var tm = ProductImportMapping.AutoDetect(template.Headers, template);
        Assert.False(BulkImportPlan.LooksLikeNotProductList(template, tm, BulkImportPlan.Build(template, tm, new BulkImportSettings(), [])));
    }

    // ------------------------------------------------------------------ бренд и категория в общем импорте

    [Fact]
    public void Brand_and_category_columns_are_mapped_by_synonyms_and_carried_to_new_products()
    {
        var csv = "Штрихкод;Название;Производитель;Группа;Количество;Закупка;Цена продажи;Единица\n"
            + "4600000000011;Мыло;Альфа;Гигиена;5;10;15;шт\n4600000000028;Шампунь;;Гигиена;5;10;15;шт\n4600000000035;Чай;Бета;Чай и кофе;5;10;15;шт\n";
        var table = Table(csv);
        var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
        Assert.Equal(2, mapping.ColumnOf(ProductImportField.Brand));
        Assert.Equal(3, mapping.ColumnOf(ProductImportField.Category));
        var known = new CatalogProductTileVm("k1", "Старый", "", false) { Barcode = "4600000000999", Category = "Гигиена" };
        var plan = BulkImportPlan.Build(table, mapping, new BulkImportSettings(), [known]);
        Assert.Equal("Альфа", plan.Rows[0].Brand);
        Assert.Null(plan.Rows[1].Brand);
        Assert.Equal("Гигиена", plan.Rows[0].Line!.CategoryName);
        Assert.Equal(1, plan.NewCategoryCount); // «Чай и кофе» ещё нет в каталоге, «Гигиена» есть
    }

    [Theory]
    [InlineData("Бренд")]
    [InlineData("Brand")]
    [InlineData("Марка")]
    [InlineData("Производитель")]
    [InlineData("Торговая марка")]
    [InlineData("Manufacturer")]
    [InlineData("Vendor")]
    public void Brand_synonyms(string header)
    {
        Assert.Equal(2, ProductImportMapping.AutoDetect(["Штрихкод", "Название", header]).ColumnOf(ProductImportField.Brand));
    }

    [Theory]
    [InlineData("Категория")]
    [InlineData("Группа")]
    [InlineData("Родитель")]
    [InlineData("Группы")]
    [InlineData("Category")]
    [InlineData("Товарная группа")]
    public void Category_synonyms(string header)
    {
        Assert.Equal(2, ProductImportMapping.AutoDetect(["Штрихкод", "Название", header]).ColumnOf(ProductImportField.Category));
    }
}
