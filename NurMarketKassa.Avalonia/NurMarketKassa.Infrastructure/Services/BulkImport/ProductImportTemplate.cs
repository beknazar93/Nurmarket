using System.Text;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (М3): шаблоны для загрузки товаров из файла.
// .xlsx — штрихкод текстовым столбцом (XlKind.Code), чтобы Excel не показал 4,6E+12; CSV — разделитель «;» (русский Excel
// открывает такой файл двойным щелчком по столбцам), UTF-8 с BOM (кириллица не ломается).
public static class ProductImportTemplate
{
    // 2026-10-30: колонки как в мастере: Штрихкод, Название, Доп. штрихкоды, Количество, Единица, Закупка, Наценка %, Цена продажи, Категория, Весовой.
    private static string[] Headers() =>
    [
        ProductImportMapping.FieldLabel(ProductImportField.Barcode),
        ProductImportMapping.FieldLabel(ProductImportField.Name),
        ProductImportMapping.FieldLabel(ProductImportField.AltBarcodes),
        ProductImportMapping.FieldLabel(ProductImportField.Quantity),
        ProductImportMapping.FieldLabel(ProductImportField.Unit),
        ProductImportMapping.FieldLabel(ProductImportField.Purchase),
        ProductImportMapping.FieldLabel(ProductImportField.Markup),
        ProductImportMapping.FieldLabel(ProductImportField.Sale),
        ProductImportMapping.FieldLabel(ProductImportField.Category),
        ProductImportMapping.FieldLabel(ProductImportField.Weight),
    ];

    /// <summary>Примерные строки: «Доп. штрихкоды» — через «;»; «Количество» можно не заполнять (тогда остаток не меняется).</summary>
    public static readonly (string Barcode, string Name, string Alt, double? Qty, string Unit, double Purchase, double? Markup, double? Sale, string Category, string Weight)[] Examples =
    [
        ("4600000000001", "Хлеб белый", "4600000000018; 4600000000025", 50, "шт", 25, 20, 30, "Хлеб", "нет"),
        ("4600000000032", "Картошка", "", 100, "кг", 20, null, 35, "Овощи", "да"),
    ];

    /// <summary>Шаблон .xlsx: шапка отчёта, строка заголовков, две примерные строки (удалите их перед загрузкой).</summary>
    public static void SaveXlsx(string path)
    {
        var meta = ReportMeta.Create(
            Tr.T("Шаблон загрузки товаров", "Товарларды жүктөө үлгүсү", "Product import template", "Ürün yükleme şablonu", "Mahsulotlarni yuklash shabloni"), null, null);
        using var book = new ExcelWorkbookBuilder(path, meta);
        var sheet = book.AddSheet(Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"), meta.Title,
            Tr.T("Обязателен только «Штрихкод» (для новых товаров ещё «Название» и цена продажи). «Количество» можно не заполнять — тогда остаток не изменится. Две примерные строки удалите.",
                "Милдеттүү — «Штрих-код» гана (жаңы товарларга дагы «Аталышы» жана сатуу баасы). «Саны» милдеттүү эмес — калдык өзгөрбөйт. Эки үлгү сапты өчүрүңүз.",
                "Only “Barcode” is required (new products also need a name and a sale price). “Quantity” may be left empty — the stock then stays as it is. Delete the two example rows.",
                "Yalnızca «Barkod» zorunludur (yeni ürünlere ad ve satış fiyatı da gerekir). «Miktar» boş bırakılabilir — stok değişmez. İki örnek satırı silin.",
                "Faqat «Shtrix-kod» majburiy (yangi mahsulotlarga nom va sotuv narxi ham kerak). «Miqdor»ni bo'sh qoldirish mumkin — qoldiq o'zgarmaydi. Ikki namuna qatorini o'chiring."));

        var titles = Headers();
        var columns = new[]
        {
            new XlColumn(titles[0], XlKind.Code),
            new XlColumn(titles[1], XlKind.Text),
            new XlColumn(titles[2], XlKind.Text),
            new XlColumn(titles[3], XlKind.Quantity),
            new XlColumn(titles[4], XlKind.Text),
            new XlColumn(titles[5], XlKind.Money),
            new XlColumn(titles[6], XlKind.Decimal2),
            new XlColumn(titles[7], XlKind.Money),
            new XlColumn(titles[8], XlKind.Text),
            new XlColumn(titles[9], XlKind.Text),
        };
        var rows = Examples.Select(e => new object?[] { e.Barcode, e.Name, e.Alt, e.Qty, e.Unit, e.Purchase, e.Markup, e.Sale, e.Category, e.Weight });
        sheet.AddTable(columns, rows, main: true, totals: false);
        book.Save();
    }

    /// <summary>Шаблон CSV (разделитель «;»). В файл писать через <see cref="CsvBytes"/> (UTF-8 с BOM).</summary>
    public static string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(';', Headers().Select(BulkImportPlan.CsvCell))).Append("\r\n");
        foreach (var e in Examples)
        {
            // ="…" — чтобы Excel при двойном щелчке не превратил штрихкод в 4,6E+12.
            sb.Append($"=\"{e.Barcode}\";{BulkImportPlan.CsvCell(e.Name)};{BulkImportPlan.CsvCell(e.Alt)};{e.Qty};{BulkImportPlan.CsvCell(e.Unit)};{e.Purchase};{(e.Markup is { } m ? m.ToString() : "")};{e.Sale};")
              .Append($"{BulkImportPlan.CsvCell(e.Category)};{e.Weight}\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Текст CSV → байты файла: UTF-8 с BOM (Excel сам узнаёт кодировку и кириллицу).</summary>
    public static byte[] CsvBytes(string csv) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

    public static void SaveCsv(string path) => File.WriteAllBytes(path, CsvBytes(BuildCsv()));
}
