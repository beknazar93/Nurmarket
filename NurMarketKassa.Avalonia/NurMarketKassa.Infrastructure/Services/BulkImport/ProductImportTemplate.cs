using System.Text;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (М3): шаблоны для загрузки товаров из файла.
// .xlsx — штрихкод текстовым столбцом (XlKind.Code), чтобы Excel не показал 4,6E+12; CSV — разделитель «;» (русский Excel
// открывает такой файл двойным щелчком по столбцам), UTF-8 с BOM (кириллица не ломается).
public static class ProductImportTemplate
{
    private static string[] Headers() =>
    [
        ProductImportMapping.FieldLabel(ProductImportField.Barcode),
        ProductImportMapping.FieldLabel(ProductImportField.Name),
        ProductImportMapping.FieldLabel(ProductImportField.Quantity),
        ProductImportMapping.FieldLabel(ProductImportField.Purchase),
        ProductImportMapping.FieldLabel(ProductImportField.Markup),
        ProductImportMapping.FieldLabel(ProductImportField.Sale),
        ProductImportMapping.FieldLabel(ProductImportField.Category),
        ProductImportMapping.FieldLabel(ProductImportField.Unit),
        ProductImportMapping.FieldLabel(ProductImportField.Weight),
        ProductImportMapping.FieldLabel(ProductImportField.Article),
        ProductImportMapping.FieldLabel(ProductImportField.Brand),
    ];

    private static readonly (string Barcode, string Name, double Qty, double Purchase, double? Markup, double Sale, string Category, string Unit, string Weight, string Article)[] Examples =
    [
        ("4600000000001", "Хлеб белый", 50, 25, 20, 30, "Хлеб", "шт", "нет", "ХЛБ-01"),
        ("4600000000002", "Картошка", 100, 20, null, 35, "Овощи", "кг", "да", ""),
    ];

    /// <summary>Шаблон .xlsx: шапка отчёта, строка заголовков, две примерные строки (удалите их перед загрузкой).</summary>
    public static void SaveXlsx(string path)
    {
        var meta = ReportMeta.Create(
            Tr.T("Шаблон загрузки товаров", "Товарларды жүктөө үлгүсү", "Product import template", "Ürün yükleme şablonu", "Mahsulotlarni yuklash shabloni"), null, null);
        using var book = new ExcelWorkbookBuilder(path, meta);
        var sheet = book.AddSheet(Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"), meta.Title,
            Tr.T("Заполните столбцы «Штрихкод» и «Количество» (для новых товаров ещё название и цены). Две примерные строки удалите.",
                "«Штрих-код» жана «Саны» мамычаларын толтуруңуз (жаңы товарларга дагы аталышы жана баасы). Эки үлгү сапты өчүрүңүз.",
                "Fill in the “Barcode” and “Quantity” columns (new products also need a name and prices). Delete the two example rows.",
                "«Barkod» ve «Miktar» sütunlarını doldurun (yeni ürünlere ad ve fiyat da gerekir). İki örnek satırı silin.",
                "«Shtrix-kod» va «Miqdor» ustunlarini to'ldiring (yangi mahsulotlarga nom va narx ham kerak). Ikki namuna qatorini o'chiring."));

        var titles = Headers();
        var columns = new[]
        {
            new XlColumn(titles[0], XlKind.Code),
            new XlColumn(titles[1], XlKind.Text),
            new XlColumn(titles[2], XlKind.Quantity),
            new XlColumn(titles[3], XlKind.Money),
            new XlColumn(titles[4], XlKind.Decimal2),
            new XlColumn(titles[5], XlKind.Money),
            new XlColumn(titles[6], XlKind.Text),
            new XlColumn(titles[7], XlKind.Text),
            new XlColumn(titles[8], XlKind.Text),
            new XlColumn(titles[9], XlKind.Text),
            new XlColumn(titles[10], XlKind.Text),
        };
        var rows = Examples.Select(e => new object?[] { e.Barcode, e.Name, e.Qty, e.Purchase, e.Markup, e.Sale, e.Category, e.Unit, e.Weight, e.Article, "" });
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
            sb.Append($"=\"{e.Barcode}\";{BulkImportPlan.CsvCell(e.Name)};{e.Qty};{e.Purchase};{(e.Markup is { } m ? m.ToString() : "")};{e.Sale};")
              .Append($"{BulkImportPlan.CsvCell(e.Category)};{e.Unit};{e.Weight};{BulkImportPlan.CsvCell(e.Article)};\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Текст CSV → байты файла: UTF-8 с BOM (Excel сам узнаёт кодировку и кириллицу).</summary>
    public static byte[] CsvBytes(string csv) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

    public static void SaveCsv(string path) => File.WriteAllBytes(path, CsvBytes(BuildCsv()));
}
