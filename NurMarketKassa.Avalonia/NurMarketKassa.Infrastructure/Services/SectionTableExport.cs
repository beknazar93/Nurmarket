namespace NurMarketKassa.Services;

/// <summary>2026-11-02 (аудит №2, В-07): простая таблица раздела программы владельца для выгрузки в Excel — заголовок, период, колонки и строки.
/// Раздел отдаёт её через IOwnerSectionExcel, оболочка владельца одной кнопкой «Excel» сохраняет файл (шапка отчёта, стили и «Итого» — как у
/// остальных выгрузок, <see cref="ExcelWorkbookBuilder"/>).</summary>
public sealed record SectionTable(string Title, string? Period, IReadOnlyList<XlColumn> Columns, IReadOnlyList<object?[]> Rows);

public static class SectionTableExport
{
    /// <summary>Имя файла: «Название период Компания.xlsx», как у остальных выгрузок.</summary>
    public static string SuggestFileName(SectionTable table, string? shopName) =>
        ExportFileName.Build(table.Title, table.Period ?? DateTime.Today.ToString("dd.MM.yyyy"), shopName, "xlsx");

    public static void ExportToExcel(string path, SectionTable table, string? shopName)
    {
        var meta = ReportMeta.Create(table.Title, shopName, table.Period);
        using var book = new ExcelWorkbookBuilder(path, meta);
        var name = table.Title.Length > 28 ? table.Title[..28] : table.Title;
        var sheet = book.AddSheet(name, table.Title);
        sheet.AddTable(table.Columns, table.Rows, main: true, totals: table.Columns.Any(c => c.Total == XlTotal.Sum));
        book.Save();
    }
}
