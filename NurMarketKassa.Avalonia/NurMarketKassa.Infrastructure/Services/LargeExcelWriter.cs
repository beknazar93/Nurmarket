using System.Globalization;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (библиотеки с GitHub, п. MiniExcel 1.46.0, Apache-2.0): быстрая выгрузка больших таблиц (15 000 товаров и больше).
/// Отчётная книга <see cref="ExcelWorkbookBuilder"/> держит все ячейки в памяти (OpenXml DOM) и красиво оформляет — для сотен строк это хорошо,
/// для десятков тысяч дорого по памяти и времени. Здесь строки пишутся потоком: только числа/даты/текст с форматом колонки, шапка и автофильтр,
/// без цветов, зебры и строки «Итого» (итоги — на втором листе сводки, если она передана). Включается только выше <see cref="Threshold"/> строк;
/// меньшие выгрузки остаются на прежнем OpenXml-построителе со всеми стилями.</summary>
public static class LargeExcelWriter
{
    /// <summary>Выше скольки строк данных выгрузка идёт через MiniExcel.</summary>
    public const int Threshold = 5000;

    public static bool IsLarge(int rowCount) => rowCount > Threshold;

    /// <summary>Записывает файл .xlsx: лист с таблицей и (если задана) лист-сводка «поле — значение».
    /// <paramref name="rows"/> перебирается один раз подряд; значения — те же, что отдаются <see cref="ExcelWorkbookBuilder.AddSheet"/> → AddTable
    /// (числа, даты, строки, <see cref="XlCell"/>); доля <see cref="XlKind.Percent"/> — в процентных пунктах, в ячейку кладётся 0–1.</summary>
    public static void Write(string path, string sheetName, IReadOnlyList<XlColumn> columns, IEnumerable<object?[]> rows,
        string? summaryName = null, IReadOnlyList<XlKeyValue>? summary = null, CancellationToken ct = default)
    {
        var keys = new string[columns.Count];
        var dynamic = new List<DynamicExcelColumn>(columns.Count + 2);
        for (var c = 0; c < columns.Count; c++)
        {
            keys[c] = "c" + c.ToString(CultureInfo.InvariantCulture);
            dynamic.Add(new DynamicExcelColumn(keys[c])
            {
                Name = columns[c].Title,
                Index = c,
                Width = WidthOf(columns[c]),
                Format = FormatOf(columns[c].Kind),
            });
        }

        var sheets = new Dictionary<string, object>
        {
            [SafeSheetName(sheetName)] = TableRows(columns, keys, rows, ct),
        };

        if (summary is { Count: > 0 })
        {
            dynamic.Add(new DynamicExcelColumn("k") { Name = Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), Index = 0, Width = 48 });
            dynamic.Add(new DynamicExcelColumn("v") { Name = Tr.T("Значение", "Мааниси", "Value", "Değer", "Qiymati"), Index = 1, Width = 24 });
            var name = SafeSheetName(summaryName ?? Tr.T("Итоги", "Жыйынтык", "Summary", "Özet", "Yakun"));
            if (string.Equals(name, SafeSheetName(sheetName), StringComparison.OrdinalIgnoreCase))
                name = name[..Math.Min(name.Length, 28)] + " 2";
            sheets[name] = SummaryRows(summary);
        }

        var config = new OpenXmlConfiguration
        {
            DynamicColumns = dynamic.ToArray(),
            AutoFilter = true,
        };
        MiniExcel.SaveAs(path, sheets, printHeader: true, excelType: ExcelType.XLSX, configuration: config, overwriteFile: true);
    }

    private static IEnumerable<Dictionary<string, object>> TableRows(IReadOnlyList<XlColumn> columns, string[] keys, IEnumerable<object?[]> rows, CancellationToken ct)
    {
        var n = 0;
        foreach (var values in rows)
        {
            if ((++n & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object>(columns.Count);
            for (var c = 0; c < columns.Count; c++)
                row[keys[c]] = Convert(c < values.Length ? values[c] : null, columns[c].Kind)!;
            yield return row;
        }
    }

    private static IEnumerable<Dictionary<string, object>> SummaryRows(IReadOnlyList<XlKeyValue> items)
    {
        foreach (var item in items)
            yield return new Dictionary<string, object> { ["k"] = item.Label, ["v"] = Convert(item.Value, item.Kind)! };
    }

    /// <summary>Значение ячейки в виде, понятном Excel: число — числом, дата — числом Excel (формат даёт колонка), код — текстом.</summary>
    internal static object? Convert(object? raw, XlKind kind)
    {
        if (raw is XlCell cell)
        {
            kind = cell.Kind ?? kind;
            raw = cell.Value;
        }
        if (raw is null)
            return null;
        if (kind is XlKind.Date or XlKind.DateTime && raw is DateTime date)
            return kind == XlKind.Date ? Math.Floor(date.ToOADate()) : date.ToOADate();
        if (kind == XlKind.Code || kind == XlKind.Group)
            return System.Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        switch (raw)
        {
            case double d: return Number(d, kind);
            case float f: return Number(f, kind);
            case decimal m: return Number((double)m, kind);
            case int i: return Number(i, kind);
            case long l: return Number(l, kind);
            case uint u: return Number(u, kind);
            case DateTime dt: return dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            default: return System.Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static object Number(double value, XlKind kind)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "";
        return kind switch
        {
            XlKind.Money or XlKind.Decimal2 => Math.Round(value, 2, MidpointRounding.AwayFromZero),
            XlKind.Percent => Math.Round(value / 100.0, 6),
            XlKind.Quantity => Math.Round(value, 3, MidpointRounding.AwayFromZero),
            XlKind.Integer or XlKind.Index => Math.Round(value, 0, MidpointRounding.AwayFromZero),
            _ => Math.Round(value, 2, MidpointRounding.AwayFromZero),
        };
    }

    private static string? FormatOf(XlKind kind) => kind switch
    {
        XlKind.Money or XlKind.Decimal2 => XlStyles.FormatMoney,
        XlKind.Integer => XlStyles.FormatInteger,
        XlKind.Quantity => XlStyles.FormatQuantity,
        XlKind.Percent => XlStyles.FormatPercent,
        XlKind.Days => XlStyles.FormatDays,
        XlKind.Date => XlStyles.FormatDate,
        XlKind.DateTime => XlStyles.FormatDateTime,
        _ => null,
    };

    private static double WidthOf(XlColumn column)
    {
        var byTitle = column.Title.Length * 1.1 + 3;
        var wanted = column.Kind switch
        {
            XlKind.Text => Math.Min(column.MaxWidth, 40),
            XlKind.Code => 18,
            XlKind.Index => 6,
            XlKind.DateTime => 17,
            XlKind.Date => 12,
            _ => 13,
        };
        return Math.Round(Math.Min(Math.Max(wanted, Math.Min(byTitle, column.MaxWidth)), 60), 1);
    }

    private static string SafeSheetName(string name)
    {
        var s = new string(name.Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray()).Trim('\'', ' ');
        if (s.Length == 0)
            s = "Sheet1";
        return s.Length > 31 ? s[..31] : s;
    }
}
