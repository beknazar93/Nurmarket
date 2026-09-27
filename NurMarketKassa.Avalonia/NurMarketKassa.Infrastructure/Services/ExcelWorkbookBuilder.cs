using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using A = DocumentFormat.OpenXml.Drawing;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace NurMarketKassa.Services;

/// <summary>Что лежит в колонке — от этого зависит, как число хранится и как выглядит.</summary>
public enum XlKind
{
    /// <summary>Обычный текст, по левому краю.</summary>
    Text,
    /// <summary>Код или штрихкод: цифры, но ТЕКСТОМ — иначе Excel покажет 4,6E+12 и съест ведущие нули.</summary>
    Code,
    /// <summary>Номер строки «№» — число по центру.</summary>
    Index,
    /// <summary>Целое число «# ##0».</summary>
    Integer,
    /// <summary>Количество: целое — без дробной части, весовое — до трёх знаков.</summary>
    Quantity,
    /// <summary>Число с двумя знаками «# ##0,00» (скорость продаж и т. п.).</summary>
    Decimal2,
    /// <summary>Деньги «# ##0,00». Валюта пишется в заголовке колонки, а не в ячейке.</summary>
    Money,
    /// <summary>Доля. На вход — ПРОЦЕНТНЫЕ пункты (0–100), как их считает программа; в ячейку
    /// кладётся настоящая доля (0–1) с форматом «0,0 %», чтобы Excel считал её процентом.</summary>
    Percent,
    /// <summary>Дни с одним знаком.</summary>
    Days,
    /// <summary>Дата — настоящей датой Excel, «ДД.ММ.ГГГГ».</summary>
    Date,
    /// <summary>Дата и время — настоящей датой Excel, «ДД.ММ.ГГГГ чч:мм».</summary>
    DateTime,
    /// <summary>Группа ABC: по центру, полужирно, с заливкой цвета группы.</summary>
    Group,
}

/// <summary>Что писать в строке «Итого» под колонкой.</summary>
public enum XlTotal
{
    None,
    /// <summary>Формула СУММ по колонке — настоящая формула, пересчитается при правке.</summary>
    Sum,
}

/// <summary>Колонка таблицы: заголовок, тип данных, итог и предельная ширина.</summary>
public sealed record XlColumn(string Title, XlKind Kind = XlKind.Text, XlTotal Total = XlTotal.None, double MaxWidth = 60);

/// <summary>Значение ячейки с подсветкой и, при необходимости, своим типом (например, в колонке
/// «Значение» сводки одна строка — деньги, другая — штуки).</summary>
public readonly record struct XlCell(object? Value, ReportTone Tone = ReportTone.None, XlKind? Kind = null);

/// <summary>Строка блока «поле — значение».</summary>
public readonly record struct XlKeyValue(string Label, object? Value, XlKind Kind = XlKind.Text, ReportTone Tone = ReportTone.None);

/// <summary>Где на листе оказалась таблица — нужно диаграммам, чтобы сослаться на её ячейки.</summary>
public sealed class XlTable
{
    internal XlTable(XlSheet sheet, int headerRow, int firstColumn, IReadOnlyList<XlColumn> columns, List<object?[]> values)
    {
        Sheet = sheet;
        HeaderRow = headerRow;
        FirstColumn = firstColumn;
        Columns = columns;
        Values = values;
    }

    public XlSheet Sheet { get; }
    public int HeaderRow { get; }
    public int FirstColumn { get; }
    public IReadOnlyList<XlColumn> Columns { get; }
    public int RowCount => Values.Count;
    public int FirstDataRow => HeaderRow + 1;
    public int LastDataRow => HeaderRow + Values.Count;

    /// <summary>Значения в том виде, в каком они легли в ячейки (числа — double, даты — число
    /// Excel, доли — 0–1): из них заполняется кэш диаграмм.</summary>
    internal List<object?[]> Values { get; }

    internal string ColumnLetter(int index) => ExcelWorkbookBuilder.ColumnLetter(FirstColumn + index);

    /// <summary>Абсолютный диапазон значений колонки, например $C$6:$C$25.</summary>
    internal string DataRange(int column, int count) =>
        $"${ColumnLetter(column)}${FirstDataRow}:${ColumnLetter(column)}${FirstDataRow + count - 1}";

    internal string HeaderCell(int column) => $"${ColumnLetter(column)}${HeaderRow}";
}

/// <summary>
/// Сборка книги Excel в «отчётном» виде: шапка отчёта над каждой таблицей, оформленная строка
/// заголовков, закреплённая шапка, автофильтр, ширины по содержимому, строка «Итого» с формулами,
/// чередование строк, настройки печати и НАСТОЯЩИЕ диаграммы Excel.
///
/// Почему числа пишутся числами, а не текстом: по тексту Excel не умеет ни складывать, ни
/// сортировать, ни строить диаграммы. Поэтому число хранится в инвариантном виде (точка — десятичный
/// разделитель), а как его показать, решает формат ячейки: «#,##0.00» в файле Excel у клиента с
/// русскими настройками Windows покажет как «1 234,56». Так и суммы считаются, и вид правильный в
/// любой локали. Даты — тоже числами Excel с форматом «ДД.ММ.ГГГГ»: их можно фильтровать по месяцам.
///
/// Стили собираются по мере надобности (реестр ниже) — не приходится держать в голове таблицу
/// «индекс стиля 7 — это деньги на полосатой строке».
/// </summary>
public sealed class ExcelWorkbookBuilder : IDisposable
{
    private readonly SpreadsheetDocument _document;
    private readonly WorkbookPart _workbookPart;
    private readonly List<XlSheet> _sheets = new();
    private bool _saved;

    internal XlStyles Styles { get; } = new();
    internal XlSharedStrings Strings { get; } = new();

    public ReportMeta Meta { get; }

    public ExcelWorkbookBuilder(string path, ReportMeta meta)
    {
        Meta = meta;
        _document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        _workbookPart = _document.AddWorkbookPart();
        _workbookPart.Workbook = new Workbook();
    }

    /// <summary>Новый лист с шапкой отчёта (название, магазин и период, кто и когда сформировал).</summary>
    /// <param name="note">Пояснение под шапкой — например, как читать ABC-срез.</param>
    /// <param name="landscape">Альбомная ориентация при печати — для широких таблиц.</param>
    public XlSheet AddSheet(string name, string title, string? note = null, bool landscape = false)
    {
        var part = _workbookPart.AddNewPart<WorksheetPart>();
        var sheet = new XlSheet(this, part, UniqueSheetName(name), (uint)(_sheets.Count + 1), _sheets.Count, landscape);
        _sheets.Add(sheet);
        sheet.WriteTitleBlock(title, note);
        return sheet;
    }

    public void Save()
    {
        if (_saved)
            return;
        _saved = true;

        foreach (var sheet in _sheets)
            sheet.Finish();

        var stylesPart = _workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = Styles.Build();
        stylesPart.Stylesheet.Save();

        var stringsPart = _workbookPart.AddNewPart<SharedStringTablePart>();
        stringsPart.SharedStringTable = Strings.Build();
        stringsPart.SharedStringTable.Save();

        var workbook = _workbookPart.Workbook;
        workbook.AppendChild(new BookViews(new WorkbookView { ActiveTab = 0U }));

        var sheets = new Sheets();
        foreach (var sheet in _sheets)
        {
            sheets.AppendChild(new Sheet
            {
                Id = _workbookPart.GetIdOfPart(sheet.Part),
                SheetId = sheet.SheetId,
                Name = sheet.Name,
            });
        }
        workbook.AppendChild(sheets);

        // Служебные имена Excel: область автофильтра и «сквозные строки» печати — шапка таблицы
        // повторяется на каждой странице распечатки.
        var names = new DefinedNames();
        foreach (var sheet in _sheets)
        {
            if (sheet.AutoFilterRef is { } filter)
            {
                names.AppendChild(new DefinedName($"{QuoteSheet(sheet.Name)}!{Absolute(filter)}")
                {
                    Name = "_xlnm._FilterDatabase",
                    LocalSheetId = (uint)sheet.Position,
                    Hidden = true,
                });
            }

            if (sheet.PrintTitleRow is { } titleRow)
            {
                names.AppendChild(new DefinedName($"{QuoteSheet(sheet.Name)}!${titleRow}:${titleRow}")
                {
                    Name = "_xlnm.Print_Titles",
                    LocalSheetId = (uint)sheet.Position,
                });
            }
        }
        if (names.HasChildren)
            workbook.AppendChild(names);

        // Пересчитать формулы при открытии: в файле лежат уже посчитанные итоги, но если кто-то
        // откроет его в программе, которая кэш не читает, пусть посчитает сама.
        workbook.AppendChild(new CalculationProperties { CalculationId = 191029U, FullCalculationOnLoad = true });
        workbook.Save();

        _document.PackageProperties.Title = Meta.Title;
        _document.PackageProperties.Subject = Meta.Subtitle;
        _document.PackageProperties.Creator = Meta.GeneratedBy ?? "NurMarket";
        _document.PackageProperties.Created = Meta.GeneratedAt.ToUniversalTime();
    }

    /// <summary>Закрывает файл. Сохранение — только явным <see cref="Save"/>: если отчёт упал на
    /// середине, вторая ошибка при сохранении из Dispose скрыла бы настоящую причину.</summary>
    public void Dispose() => _document.Dispose();

    // ------------------------------------------------------------------ имена и адреса

    /// <summary>Имя листа Excel: не длиннее 31 символа, без : \ / ? * [ ], не начинается и не
    /// кончается апострофом, не повторяется и не равно «History» (это имя Excel резервирует).</summary>
    private string UniqueSheetName(string title)
    {
        var cleaned = new string(title.Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']') && !char.IsControl(c)).ToArray())
            .Trim().Trim('\'').Trim();
        if (cleaned.Length == 0)
            cleaned = "Sheet";
        if (string.Equals(cleaned, "History", StringComparison.OrdinalIgnoreCase))
            cleaned += " log";
        if (cleaned.Length > 31)
            cleaned = cleaned[..31].TrimEnd().TrimEnd('\'');

        var candidate = cleaned;
        for (var i = 2; _sheets.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            var suffix = $" ({i})";
            candidate = (cleaned.Length + suffix.Length > 31 ? cleaned[..(31 - suffix.Length)] : cleaned) + suffix;
        }

        return candidate;
    }

    internal static string QuoteSheet(string name) => "'" + name.Replace("'", "''") + "'";

    internal static string ColumnLetter(int column)
    {
        var result = "";
        while (column > 0)
        {
            var rem = (column - 1) % 26;
            result = (char)('A' + rem) + result;
            column = (column - 1) / 26;
        }

        return result;
    }

    /// <summary>A5:F40 → $A$5:$F$40.</summary>
    private static string Absolute(string reference) =>
        string.Join(":", reference.Split(':').Select(part =>
        {
            var letters = new string(part.TakeWhile(char.IsLetter).ToArray());
            return "$" + letters + "$" + part[letters.Length..];
        }));
}

/// <summary>Один лист отчёта. Строки пишутся сверху вниз; ширины колонок, закрепление, фильтр и
/// печать собираются в конце (<see cref="Finish"/>), когда известно всё содержимое.</summary>
public sealed class XlSheet
{
    private const double DefaultRowHeight = 15;

    private readonly ExcelWorkbookBuilder _book;
    private readonly bool _landscape;
    private readonly SheetData _data = new();
    private readonly Dictionary<int, double> _widths = new();
    private readonly List<(Row Row, Dictionary<int, string> Titles)> _headerRows = new();
    private readonly List<Hyperlink> _links = new();
    private readonly List<string> _textNumberRanges = new();
    private DrawingsPart? _drawings;
    private uint _shapeId = 2;
    private int? _freezeBelowRow;

    internal XlSheet(ExcelWorkbookBuilder book, WorksheetPart part, string name, uint sheetId, int position, bool landscape)
    {
        _book = book;
        Part = part;
        Name = name;
        SheetId = sheetId;
        Position = position;
        _landscape = landscape;
    }

    internal WorksheetPart Part { get; }
    internal uint SheetId { get; }
    internal int Position { get; }
    internal string? AutoFilterRef { get; private set; }
    internal int? PrintTitleRow { get; private set; }

    public string Name { get; }

    /// <summary>Номер строки, с которой продолжится запись (с единицы).</summary>
    public int NextRow { get; private set; } = 1;

    private XlStyles Styles => _book.Styles;

    // ------------------------------------------------------------------ шапка отчёта

    /// <summary>Три строки над таблицей: название отчёта, магазин и период, кто и когда
    /// сформировал. Без объединения ячеек — текст сам уходит вправо по пустым ячейкам, а
    /// объединения мешают сортировать и выделять колонки.</summary>
    internal void WriteTitleBlock(string title, string? note)
    {
        var meta = _book.Meta;

        var titleRow = NewRow(1, height: 24);
        titleRow.AppendChild(TextCell(1, 1, title, Styles.Title));

        if (!string.IsNullOrWhiteSpace(meta.Subtitle))
            NewRow(2).AppendChild(TextCell(1, 2, meta.Subtitle, Styles.Subtitle));

        NewRow(3).AppendChild(TextCell(1, 3, meta.GeneratedLine, Styles.Meta));

        NextRow = 5;
        if (!string.IsNullOrWhiteSpace(note))
        {
            NewRow(4).AppendChild(TextCell(1, 4, note!, Styles.Note));
            NextRow = 6;
        }
    }

    /// <summary>Подзаголовок раздела внутри листа.</summary>
    public void AddHeading(string text)
    {
        NewRow(NextRow, height: 18).AppendChild(TextCell(1, NextRow, text, Styles.Heading));
        NextRow++;
    }

    /// <summary>Пояснение мелким курсивом.</summary>
    public void AddNote(string text)
    {
        NewRow(NextRow).AppendChild(TextCell(1, NextRow, text, Styles.Note));
        NextRow += 2;
    }

    // ------------------------------------------------------------------ таблица

    /// <summary>Таблица с оформленной шапкой, чередованием строк и строкой «Итого».</summary>
    /// <param name="rows">Значения по колонкам: числа, даты, строки или <see cref="XlCell"/> —
    /// если ячейке нужна подсветка.</param>
    /// <param name="main">Главная таблица листа: на ней закрепляется шапка, ставится автофильтр
    /// и она же повторяется на каждой странице при печати. Такая таблица на листе одна.</param>
    public XlTable AddTable(IReadOnlyList<XlColumn> columns, IEnumerable<object?[]> rows, bool main = true, bool totals = true)
    {
        const int firstColumn = 1;
        var headerRow = NextRow;
        var header = NewRow(headerRow);
        var titles = new Dictionary<int, string>();
        for (var c = 0; c < columns.Count; c++)
        {
            var column = firstColumn + c;
            header.AppendChild(TextCell(column, headerRow, columns[c].Title, Styles.Header(Align(columns[c].Kind))));
            titles[column] = columns[c].Title;
            MeasureHeader(column, columns[c].Title);
        }
        _headerRows.Add((header, titles));

        var stored = new List<object?[]>();
        var rowNumber = headerRow;
        foreach (var values in rows)
        {
            rowNumber++;
            var zebra = stored.Count % 2 == 1;
            var row = NewRow(rowNumber);
            var cached = new object?[columns.Count];
            for (var c = 0; c < columns.Count; c++)
            {
                var raw = c < values.Length ? values[c] : null;
                var (cell, value) = DataCell(firstColumn + c, rowNumber, raw, columns[c], zebra);
                row.AppendChild(cell);
                cached[c] = value;
            }

            stored.Add(cached);
        }

        var table = new XlTable(this, headerRow, firstColumn, columns, stored);

        for (var c = 0; c < columns.Count; c++)
        {
            if (columns[c].Kind == XlKind.Code && stored.Count > 0)
                _textNumberRanges.Add($"{table.ColumnLetter(c)}{table.FirstDataRow}:{table.ColumnLetter(c)}{table.LastDataRow}");
        }

        if (stored.Count == 0)
        {
            // Пустой период — не пустая таблица без объяснений, а строка «нет данных».
            NewRow(headerRow + 1).AppendChild(TextCell(firstColumn, headerRow + 1, ReportLabels.NoData, Styles.Note));
            NextRow = headerRow + 3;
            return table;
        }

        var lastRow = table.LastDataRow;
        if (totals && columns.Any(c => c.Total == XlTotal.Sum))
        {
            lastRow++;
            var totalRow = NewRow(lastRow, height: 18);
            for (var c = 0; c < columns.Count; c++)
            {
                var column = firstColumn + c;
                var reference = ExcelWorkbookBuilder.ColumnLetter(column) + lastRow;
                if (c == 0)
                {
                    totalRow.AppendChild(TextCell(column, lastRow, ReportLabels.Total, Styles.TotalLabel));
                }
                else if (columns[c].Total == XlTotal.Sum)
                {
                    var sum = stored.Sum(v => v[c] is double d ? d : 0);
                    var letter = table.ColumnLetter(c);
                    totalRow.AppendChild(new Cell
                    {
                        CellReference = reference,
                        StyleIndex = Styles.Total(TotalFormat(columns[c].Kind, sum)),
                        CellFormula = new CellFormula($"SUM({letter}{table.FirstDataRow}:{letter}{table.LastDataRow})"),
                        CellValue = new CellValue(Number(sum)),
                    });
                    Measure(column, columns[c].Kind, sum);
                }
                else
                {
                    totalRow.AppendChild(new Cell { CellReference = reference, StyleIndex = Styles.Total(null) });
                }
            }
        }

        if (main)
        {
            AutoFilterRef = $"{table.ColumnLetter(0)}{headerRow}:{table.ColumnLetter(columns.Count - 1)}{table.LastDataRow}";
            _freezeBelowRow = headerRow;
            PrintTitleRow = headerRow;
        }

        NextRow = lastRow + 2;
        return table;
    }

    /// <summary>Блок «поле — значение»: подписи слева с заливкой, значения справа.</summary>
    public void AddKeyValues(IReadOnlyList<XlKeyValue> items, string? keyHeader = null, string? valueHeader = null)
    {
        if (keyHeader is not null || valueHeader is not null)
        {
            var header = NewRow(NextRow);
            header.AppendChild(TextCell(1, NextRow, keyHeader ?? "", Styles.Header(XlAlign.Left)));
            header.AppendChild(TextCell(2, NextRow, valueHeader ?? "", Styles.Header(items.All(i => IsNumeric(i.Kind)) ? XlAlign.Right : XlAlign.Left)));
            _headerRows.Add((header, new Dictionary<int, string> { [1] = keyHeader ?? "", [2] = valueHeader ?? "" }));
            MeasureHeader(1, keyHeader ?? "");
            MeasureHeader(2, valueHeader ?? "");
            NextRow++;
        }

        // Сводка из одних чисел — по правому краю, как в таблице; карточка документа, где числа
        // перемешаны с текстом, — по левому, иначе значения прыгают от края к краю.
        var allNumbers = items.All(i => IsNumeric(i.Kind) && i.Value is not string);
        var valueAlign = allNumbers ? XlAlign.Right : XlAlign.Left;

        foreach (var item in items)
        {
            var row = NewRow(NextRow);
            row.AppendChild(TextCell(1, NextRow, item.Label, Styles.KeyLabel));
            MeasureText(1, item.Label, bold: true);

            var column = new XlColumn("", item.Kind);
            var (cell, _) = DataCell(2, NextRow, new XlCell(item.Value, item.Tone), column, zebra: false, valueAlign);
            row.AppendChild(cell);
            if (item.Kind == XlKind.Code)
                _textNumberRanges.Add($"B{NextRow}");
            NextRow++;
        }

        NextRow++;
    }

    /// <summary>Список ссылок на другие листы книги — оглавление отчёта.</summary>
    public void AddLinks(IEnumerable<(string Text, XlSheet Target)> links)
    {
        foreach (var (text, target) in links)
        {
            var reference = "A" + NextRow;
            NewRow(NextRow).AppendChild(TextCell(1, NextRow, text, Styles.Link));
            _links.Add(new Hyperlink
            {
                Reference = reference,
                Location = ExcelWorkbookBuilder.QuoteSheet(target.Name) + "!A1",
                Display = text,
            });
            MeasureText(1, text, bold: false);
            NextRow++;
        }

        NextRow++;
    }

    // ------------------------------------------------------------------ ячейки

    private Row NewRow(int index, double? height = null)
    {
        var row = new Row { RowIndex = (uint)index };
        if (height is { } h)
        {
            row.Height = h;
            row.CustomHeight = true;
        }

        _data.AppendChild(row);
        return row;
    }

    private Cell TextCell(int column, int row, string text, uint style) => new()
    {
        CellReference = ExcelWorkbookBuilder.ColumnLetter(column) + row,
        DataType = CellValues.SharedString,
        CellValue = new CellValue(_book.Strings.Get(text).ToString(CultureInfo.InvariantCulture)),
        StyleIndex = style,
    };

    /// <summary>Ячейка данных: число с форматом по типу колонки, дата — числом Excel, текст — строкой.
    /// Возвращает ещё и значение для кэша диаграмм.</summary>
    private (Cell Cell, object? Value) DataCell(int column, int row, object? raw, XlColumn spec, bool zebra, XlAlign? valueAlign = null)
    {
        var tone = ReportTone.None;
        var kind = spec.Kind;
        if (raw is XlCell wrapped)
        {
            tone = wrapped.Tone;
            kind = wrapped.Kind ?? kind;
            raw = wrapped.Value;
        }

        var reference = ExcelWorkbookBuilder.ColumnLetter(column) + row;

        if (kind == XlKind.Group)
        {
            var group = raw?.ToString() ?? "";
            if (tone == ReportTone.None)
                tone = group switch { "A" => ReportTone.Good, "B" => ReportTone.Warn, _ => ReportTone.Info };
            MeasureText(column, group, bold: true);
            return (TextCell(column, row, group, Styles.Data(null, XlAlign.Center, zebra, tone, bold: true)), group);
        }

        if (raw is null)
            return (new Cell { CellReference = reference, StyleIndex = Styles.Data(null, Align(kind), zebra, tone) }, null);

        if (kind is XlKind.Date or XlKind.DateTime && raw is DateTime date)
        {
            var serial = date.ToOADate();
            Measure(column, kind, serial);
            return (new Cell
            {
                CellReference = reference,
                CellValue = new CellValue(Number(kind == XlKind.Date ? Math.Floor(serial) : serial)),
                StyleIndex = Styles.Data(kind == XlKind.Date ? XlStyles.FormatDate : XlStyles.FormatDateTime, valueAlign ?? XlAlign.Center, zebra, tone),
            }, serial);
        }

        if (IsNumeric(kind) && TryNumber(raw, out var number))
        {
            var stored = kind switch
            {
                XlKind.Money or XlKind.Decimal2 => Math.Round(number, 2, MidpointRounding.AwayFromZero),
                XlKind.Percent => Math.Round(number / 100.0, 6),
                XlKind.Quantity => Math.Round(number, 3, MidpointRounding.AwayFromZero),
                XlKind.Integer or XlKind.Index => Math.Round(number, 0, MidpointRounding.AwayFromZero),
                _ => Math.Round(number, 2, MidpointRounding.AwayFromZero),
            };
            Measure(column, kind, stored);
            return (new Cell
            {
                CellReference = reference,
                CellValue = new CellValue(Number(stored)),
                StyleIndex = Styles.Data(NumberFormat(kind, stored), kind == XlKind.Index ? XlAlign.Center : valueAlign ?? XlAlign.Right, zebra, tone),
            }, stored);
        }

        // Текст, код или «—» в числовой колонке.
        var text = raw is DateTime dt ? dt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        MeasureText(column, text, bold: false);
        var textAlign = valueAlign ?? (IsNumeric(kind) ? XlAlign.Right : kind is XlKind.Date or XlKind.DateTime ? XlAlign.Center : XlAlign.Left);
        return (TextCell(column, row, text, Styles.Data(kind == XlKind.Code ? XlStyles.FormatText : null, textAlign, zebra, tone)), text);
    }

    private static bool IsNumeric(XlKind kind) =>
        kind is XlKind.Index or XlKind.Integer or XlKind.Quantity or XlKind.Decimal2 or XlKind.Money or XlKind.Percent or XlKind.Days;

    private static bool TryNumber(object raw, out double number)
    {
        switch (raw)
        {
            case double d: number = d; break;
            case float f: number = f; break;
            case decimal m: number = (double)m; break;
            case int i: number = i; break;
            case long l: number = l; break;
            case uint u: number = u; break;
            default: number = 0; return false;
        }

        return !double.IsNaN(number) && !double.IsInfinity(number);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static XlAlign Align(XlKind kind) => kind switch
    {
        XlKind.Index => XlAlign.Center,
        XlKind.Integer or XlKind.Quantity or XlKind.Decimal2 or XlKind.Money or XlKind.Percent or XlKind.Days => XlAlign.Right,
        XlKind.Date or XlKind.DateTime or XlKind.Group => XlAlign.Center,
        _ => XlAlign.Left,
    };

    private static string? NumberFormat(XlKind kind, double value) => kind switch
    {
        XlKind.Money or XlKind.Decimal2 => XlStyles.FormatMoney,
        XlKind.Integer => XlStyles.FormatInteger,
        XlKind.Index => null,
        // Целое количество — без «,000», весовое — до трёх знаков. Формат «# ##0,###» для целого
        // числа оставил бы висячую запятую («5,»), поэтому формат выбирается по значению.
        XlKind.Quantity => Math.Abs(value % 1) < 0.0000001 ? XlStyles.FormatInteger : XlStyles.FormatQuantity,
        XlKind.Percent => XlStyles.FormatPercent,
        XlKind.Days => XlStyles.FormatDays,
        _ => null,
    };

    private static string? TotalFormat(XlKind kind, double sum) => NumberFormat(kind, sum) ?? XlStyles.FormatMoney;

    // ------------------------------------------------------------------ ширины колонок

    private void Grow(int column, double width)
    {
        if (!_widths.TryGetValue(column, out var current) || width > current)
            _widths[column] = width;
    }

    private void MeasureText(int column, string text, bool bold)
    {
        var longestLine = text.Split('\n').Max(l => l.Length);
        Grow(column, longestLine * (bold ? 1.2 : 1.1) + 2.5);
    }

    /// <summary>Заголовок переносится по словам, поэтому колонке достаточно ширины самого длинного
    /// слова; но короткий заголовок лучше уместить в одну строку.</summary>
    private void MeasureHeader(int column, string title)
    {
        var longestWord = title.Split(' ', '\n').Max(w => w.Length);
        Grow(column, Math.Max(longestWord * 1.2 + 3, Math.Min(title.Length * 1.2 + 3, 14)));
    }

    private void Measure(int column, XlKind kind, double value)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var text = kind switch
        {
            XlKind.Money or XlKind.Decimal2 => value.ToString("#,##0.00", culture),
            XlKind.Integer => value.ToString("#,##0", culture),
            XlKind.Index => value.ToString("0", culture),
            XlKind.Quantity => value.ToString("#,##0.###", culture),
            XlKind.Percent => (value * 100).ToString("0.0", culture) + " %",
            XlKind.Days => value.ToString("#,##0.0", culture),
            XlKind.Date => "00.00.0000",
            XlKind.DateTime => "00.00.0000 00:00",
            _ => value.ToString(culture),
        };
        Grow(column, text.Length * 1.1 + 3.5);
    }

    // ------------------------------------------------------------------ диаграммы

    /// <summary>Столбчатая диаграмма по колонке таблицы. Кладётся справа от таблицы, чтобы было
    /// видно, из чего построена, — и Excel перестроит её, если поправить число в таблице.</summary>
    /// <param name="horizontal">Горизонтальные полосы — для длинных названий товаров: вертикальные
    /// подписи под столбцами пришлось бы поворачивать или резать.</param>
    public void AddBarChart(string title, XlTable table, int categoryColumn, int valueColumn, int maxPoints,
        bool horizontal = false, string? categoryFormat = null, string valueFormat = "#,##0")
    {
        var points = Math.Min(table.RowCount, maxPoints);
        if (points <= 0)
            return;

        const uint catId = 48650112U, valId = 48672768U;
        var series = new C.BarChartSeries(
            new C.Index { Val = 0U },
            new C.Order { Val = 0U },
            SeriesText(table, valueColumn),
            ChartFill(ReportPalette.ChartPrimary),
            new C.InvertIfNegative { Val = false });

        if (points <= 16)
            series.AppendChild(ValueLabels(valueFormat));

        series.AppendChild(Categories(table, categoryColumn, points, categoryFormat));
        series.AppendChild(new C.Values(NumberRef(table, valueColumn, points, valueFormat)));

        var bar = new C.BarChart(
            new C.BarDirection { Val = horizontal ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column },
            new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
            new C.VaryColors { Val = false },
            series,
            new C.GapWidth { Val = (UInt16Value)(ushort)60 },
            new C.AxisId { Val = catId },
            new C.AxisId { Val = valId });

        // В горизонтальной диаграмме первая позиция таблицы должна быть сверху, как в таблице.
        var plot = new C.PlotArea(
            new C.Layout(),
            bar,
            CategoryAxis(catId, valId, horizontal ? C.AxisPositionValues.Left : C.AxisPositionValues.Bottom, reverse: horizontal, categoryFormat),
            ValueAxis(valId, catId, horizontal ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left, valueFormat,
                crossesAtMax: horizontal, gridlines: true));

        AddChart(title, plot, legend: false, table, heightRows: Math.Max(18, horizontal ? points + 6 : 18));
    }

    /// <summary>Круговая диаграмма долей (группы ABC).</summary>
    public void AddPieChart(string title, XlTable table, int categoryColumn, int valueColumn, int points, IReadOnlyList<string> colors)
    {
        points = Math.Min(points, table.RowCount);
        if (points <= 0)
            return;

        var series = new C.PieChartSeries(
            new C.Index { Val = 0U },
            new C.Order { Val = 0U },
            SeriesText(table, valueColumn));

        for (var i = 0; i < points; i++)
        {
            series.AppendChild(new C.DataPoint(
                new C.Index { Val = (uint)i },
                new C.Bubble3D { Val = false },
                new C.ChartShapeProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = colors[i % colors.Count] }),
                    new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" })) { Width = 19050 })));
        }

        series.AppendChild(new C.DataLabels(
            new C.NumberingFormat { FormatCode = "0.0%", SourceLinked = false },
            new C.ChartShapeProperties(new A.NoFill(), new A.Outline(new A.NoFill())),
            TextProps(1000, bold: true, color: "262626"),
            new C.DataLabelPosition { Val = C.DataLabelPositionValues.OutsideEnd },
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = false },
            new C.ShowCategoryName { Val = false },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = true },
            new C.ShowBubbleSize { Val = false },
            new C.ShowLeaderLines { Val = true }));

        series.AppendChild(Categories(table, categoryColumn, points, null));
        series.AppendChild(new C.Values(NumberRef(table, valueColumn, points, "#,##0.00")));

        var pie = new C.PieChart(new C.VaryColors { Val = true }, series, new C.FirstSliceAngle { Val = (UInt16Value)(ushort)0 });
        AddChart(title, new C.PlotArea(new C.Layout(), pie), legend: true, table, widthColumns: 8, heightRows: 16);
    }

    /// <summary>Диаграмма Парето: столбцы долей, окрашенные по группе ABC, и ломаная накопленной
    /// доли по правой шкале — та же, что на экране ABC-анализа.</summary>
    public void AddParetoChart(string title, XlTable table, int categoryColumn, int shareColumn, int cumulativeColumn,
        int groupColumn, int maxPoints)
    {
        var points = Math.Min(table.RowCount, maxPoints);
        if (points <= 0)
            return;

        const uint catId = 50010001U, valId = 50010002U, lineCatId = 50010003U, lineValId = 50010004U;

        var bars = new C.BarChartSeries(
            new C.Index { Val = 0U },
            new C.Order { Val = 0U },
            SeriesText(table, shareColumn),
            ChartFill(ReportPalette.ChartPrimary),
            new C.InvertIfNegative { Val = false });

        for (var i = 0; i < points; i++)
        {
            var group = table.Values[i][groupColumn]?.ToString() ?? "C";
            bars.AppendChild(new C.DataPoint(
                new C.Index { Val = (uint)i },
                new C.InvertIfNegative { Val = false },
                new C.Bubble3D { Val = false },
                ChartFill(ReportPalette.GroupChartColor(group))));
        }

        bars.AppendChild(Categories(table, categoryColumn, points, null));
        bars.AppendChild(new C.Values(NumberRef(table, shareColumn, points, "0.0%")));

        var bar = new C.BarChart(
            new C.BarDirection { Val = C.BarDirectionValues.Column },
            new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
            new C.VaryColors { Val = false },
            bars,
            new C.GapWidth { Val = (UInt16Value)(ushort)40 },
            new C.AxisId { Val = catId },
            new C.AxisId { Val = valId });

        var line = new C.LineChart(
            new C.Grouping { Val = C.GroupingValues.Standard },
            new C.VaryColors { Val = false },
            new C.LineChartSeries(
                new C.Index { Val = 1U },
                new C.Order { Val = 1U },
                SeriesText(table, cumulativeColumn),
                new C.ChartShapeProperties(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = ReportPalette.ChartLine })) { Width = 25400 }),
                new C.Marker(
                    new C.Symbol { Val = C.MarkerStyleValues.Circle },
                    new C.Size { Val = (ByteValue)(byte)5 },
                    new C.ChartShapeProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" }),
                        new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = ReportPalette.ChartLine })) { Width = 15875 })),
                Categories(table, categoryColumn, points, null),
                new C.Values(NumberRef(table, cumulativeColumn, points, "0.0%")),
                new C.Smooth { Val = false }),
            new C.ShowMarker { Val = true },
            new C.AxisId { Val = lineCatId },
            new C.AxisId { Val = lineValId });

        var plot = new C.PlotArea(
            new C.Layout(),
            bar,
            line,
            CategoryAxis(catId, valId, C.AxisPositionValues.Bottom, reverse: false, null),
            ValueAxis(valId, catId, C.AxisPositionValues.Left, "0%", crossesAtMax: false, gridlines: true),
            HiddenCategoryAxis(lineCatId, lineValId),
            ValueAxis(lineValId, lineCatId, C.AxisPositionValues.Right, "0%", crossesAtMax: true, gridlines: false, min: 0, max: 1));

        AddChart(title, plot, legend: true, table, widthColumns: 12, heightRows: 20);
    }

    private void AddChart(string title, C.PlotArea plot, bool legend, XlTable table, int widthColumns = 9, int heightRows = 18)
    {
        if (_drawings is null)
        {
            _drawings = Part.AddNewPart<DrawingsPart>();
            _drawings.WorksheetDrawing = new XDR.WorksheetDrawing();
        }

        var chart = new C.Chart(
            ChartTitle(title),
            new C.AutoTitleDeleted { Val = false },
            plot);
        if (legend)
            chart.AppendChild(new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }, new C.Overlay { Val = false }, TextProps(900)));
        chart.AppendChild(new C.PlotVisibleOnly { Val = true });
        chart.AppendChild(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        var chartPart = _drawings.AddNewPart<ChartPart>();
        chartPart.ChartSpace = new C.ChartSpace(
            new C.Date1904 { Val = false },
            new C.EditingLanguage { Val = ReportFormat.LanguageTag },
            new C.RoundedCorners { Val = false },
            chart,
            new C.ChartShapeProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" }),
                new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "D9D9D9" })) { Width = 9525 }),
            TextProps(900));
        chartPart.ChartSpace.AddNamespaceDeclaration("c", "http://schemas.openxmlformats.org/drawingml/2006/chart");
        chartPart.ChartSpace.AddNamespaceDeclaration("a", "http://schemas.openxmlformats.org/drawingml/2006/main");
        chartPart.ChartSpace.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        chartPart.ChartSpace.Save();

        // Справа от таблицы через одну пустую колонку; несколько диаграмм одной таблицы — друг под другом.
        var existing = _drawings.WorksheetDrawing.Elements<XDR.TwoCellAnchor>().ToList();
        var fromColumn = table.FirstColumn - 1 + table.Columns.Count + 1;
        var fromRow = existing.Count == 0
            ? table.HeaderRow - 1
            : existing.Max(a => int.Parse(a.ToMarker!.RowId!.Text, CultureInfo.InvariantCulture)) + 2;

        var anchor = new XDR.TwoCellAnchor(
            new XDR.FromMarker(
                new XDR.ColumnId(fromColumn.ToString(CultureInfo.InvariantCulture)),
                new XDR.ColumnOffset("0"),
                new XDR.RowId(fromRow.ToString(CultureInfo.InvariantCulture)),
                new XDR.RowOffset("0")),
            new XDR.ToMarker(
                new XDR.ColumnId((fromColumn + widthColumns).ToString(CultureInfo.InvariantCulture)),
                new XDR.ColumnOffset("0"),
                new XDR.RowId((fromRow + heightRows).ToString(CultureInfo.InvariantCulture)),
                new XDR.RowOffset("0")),
            new XDR.GraphicFrame(
                new XDR.NonVisualGraphicFrameProperties(
                    new XDR.NonVisualDrawingProperties { Id = _shapeId++, Name = title },
                    new XDR.NonVisualGraphicFrameDrawingProperties()),
                new XDR.Transform(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = 0L, Cy = 0L }),
                new A.Graphic(new A.GraphicData(new C.ChartReference { Id = _drawings.GetIdOfPart(chartPart) })
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart",
                }))
            { Macro = "" },
            new XDR.ClientData())
        { EditAs = XDR.EditAsValues.OneCell };

        _drawings.WorksheetDrawing.AppendChild(anchor);
        _drawings.WorksheetDrawing.Save();
    }

    private string Ref(string range) => ExcelWorkbookBuilder.QuoteSheet(Name) + "!" + range;

    private C.SeriesText SeriesText(XlTable table, int column) =>
        new(new C.StringReference(
            new C.Formula(Ref(table.HeaderCell(column))),
            new C.StringCache(
                new C.PointCount { Val = 1U },
                new C.StringPoint(new C.NumericValue(table.Columns[column].Title)) { Index = 0U })));

    private C.CategoryAxisData Categories(XlTable table, int column, int points, string? numberFormat)
    {
        var formula = new C.Formula(Ref(table.DataRange(column, points)));
        if (numberFormat is not null)
        {
            var cache = new C.NumberingCache(new C.FormatCode(numberFormat), new C.PointCount { Val = (uint)points });
            for (var i = 0; i < points; i++)
            {
                if (table.Values[i][column] is double d)
                    cache.AppendChild(new C.NumericPoint(new C.NumericValue(Number(d))) { Index = (uint)i });
            }

            return new C.CategoryAxisData(new C.NumberReference(formula, cache));
        }

        var strings = new C.StringCache(new C.PointCount { Val = (uint)points });
        for (var i = 0; i < points; i++)
        {
            var text = table.Values[i][column] switch
            {
                double d => d.ToString(CultureInfo.InvariantCulture),
                { } other => other.ToString() ?? "",
                null => "",
            };
            strings.AppendChild(new C.StringPoint(new C.NumericValue(text)) { Index = (uint)i });
        }

        return new C.CategoryAxisData(new C.StringReference(formula, strings));
    }

    private C.NumberReference NumberRef(XlTable table, int column, int points, string format)
    {
        var cache = new C.NumberingCache(new C.FormatCode(format), new C.PointCount { Val = (uint)points });
        for (var i = 0; i < points; i++)
        {
            if (table.Values[i][column] is double d)
                cache.AppendChild(new C.NumericPoint(new C.NumericValue(Number(d))) { Index = (uint)i });
        }

        return new C.NumberReference(new C.Formula(Ref(table.DataRange(column, points))), cache);
    }

    private static C.ChartShapeProperties ChartFill(string rgb) =>
        new(new A.SolidFill(new A.RgbColorModelHex { Val = rgb }));

    private static C.DataLabels ValueLabels(string format) =>
        new(
            new C.NumberingFormat { FormatCode = format, SourceLinked = false },
            new C.ChartShapeProperties(new A.NoFill(), new A.Outline(new A.NoFill())),
            TextProps(800, color: "404040"),
            new C.DataLabelPosition { Val = C.DataLabelPositionValues.OutsideEnd },
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = true },
            new C.ShowCategoryName { Val = false },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = false },
            new C.ShowBubbleSize { Val = false });

    private static C.Title ChartTitle(string text) =>
        new(
            new C.ChartText(new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(
                    new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 1200, Bold = true }),
                    new A.Run(
                        new A.RunProperties(
                            new A.SolidFill(new A.RgbColorModelHex { Val = ReportPalette.Accent }),
                            new A.LatinFont { Typeface = "Calibri" })
                        { Language = ReportFormat.LanguageTag, FontSize = 1200, Bold = true },
                        new A.Text(text))))),
            new C.Overlay { Val = false });

    private static C.TextProperties TextProps(int size, bool bold = false, string color = "595959") =>
        new(
            new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(
                new A.ParagraphProperties(new A.DefaultRunProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = color }),
                    new A.LatinFont { Typeface = "Calibri" })
                { FontSize = size, Bold = bold }),
                new A.EndParagraphRunProperties { Language = ReportFormat.LanguageTag }));

    private static C.CategoryAxis CategoryAxis(uint id, uint crossId, C.AxisPositionValues position, bool reverse, string? numberFormat)
    {
        var axis = new C.CategoryAxis(
            new C.AxisId { Val = id },
            new C.Scaling(new C.Orientation { Val = reverse ? C.OrientationValues.MaxMin : C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = position });
        if (numberFormat is not null)
            axis.AppendChild(new C.NumberingFormat { FormatCode = numberFormat, SourceLinked = false });
        axis.Append(
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.ChartShapeProperties(new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "BFBFBF" })) { Width = 9525 }),
            TextProps(800),
            new C.CrossingAxis { Val = crossId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = false },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = (UInt16Value)(ushort)100 },
            new C.NoMultiLevelLabels { Val = false });
        return axis;
    }

    private static C.CategoryAxis HiddenCategoryAxis(uint id, uint crossId) =>
        new(
            new C.AxisId { Val = id },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = true },
            new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.CrossingAxis { Val = crossId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = (UInt16Value)(ushort)100 },
            new C.NoMultiLevelLabels { Val = false });

    private static C.ValueAxis ValueAxis(uint id, uint crossId, C.AxisPositionValues position, string format,
        bool crossesAtMax, bool gridlines, double? min = null, double? max = null)
    {
        var scaling = new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax });
        if (max is { } top)
            scaling.AppendChild(new C.MaxAxisValue { Val = top });
        if (min is { } bottom)
            scaling.AppendChild(new C.MinAxisValue { Val = bottom });

        var axis = new C.ValueAxis(
            new C.AxisId { Val = id },
            scaling,
            new C.Delete { Val = false },
            new C.AxisPosition { Val = position });
        if (gridlines)
        {
            axis.AppendChild(new C.MajorGridlines(new C.ChartShapeProperties(
                new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "E7E6E6" })) { Width = 9525 })));
        }

        axis.Append(
            new C.NumberingFormat { FormatCode = format, SourceLinked = false },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            new C.ChartShapeProperties(new A.Outline(new A.NoFill())),
            TextProps(800),
            new C.CrossingAxis { Val = crossId },
            new C.Crosses { Val = crossesAtMax ? C.CrossesValues.Maximum : C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        return axis;
    }

    // ------------------------------------------------------------------ сборка листа

    internal void Finish()
    {
        // Ширины колонок и высота строк шапки — по содержимому.
        var columns = new Columns();
        foreach (var (column, width) in _widths.OrderBy(w => w.Key))
        {
            columns.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Column
            {
                Min = (uint)column,
                Max = (uint)column,
                Width = Math.Round(Math.Clamp(width, 6, 60), 2),
                CustomWidth = true,
            });
        }

        foreach (var (row, titles) in _headerRows)
        {
            var lines = 1;
            foreach (var (column, title) in titles)
            {
                var width = Math.Clamp(_widths.TryGetValue(column, out var w) ? w : 8.43, 6, 60);
                lines = Math.Max(lines, WrappedLines(title, width));
            }

            row.Height = Math.Max(DefaultRowHeight + 5, lines * DefaultRowHeight + 6);
            row.CustomHeight = true;
        }

        var worksheet = new Worksheet();
        worksheet.AddNamespaceDeclaration("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        worksheet.AppendChild(new SheetProperties(new PageSetupProperties { FitToPage = true }));

        var view = new SheetView { ShowGridLines = false, WorkbookViewId = 0U, TabSelected = Position == 0 };
        if (_freezeBelowRow is { } frozen)
        {
            var topLeft = "A" + (frozen + 1);
            view.AppendChild(new Pane
            {
                VerticalSplit = frozen,
                TopLeftCell = topLeft,
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            });
            view.AppendChild(new Selection
            {
                Pane = PaneValues.BottomLeft,
                ActiveCell = topLeft,
                SequenceOfReferences = new ListValue<StringValue> { InnerText = topLeft },
            });
        }
        worksheet.AppendChild(new SheetViews(view));
        worksheet.AppendChild(new SheetFormatProperties { DefaultRowHeight = DefaultRowHeight });
        if (columns.HasChildren)
            worksheet.AppendChild(columns);
        worksheet.AppendChild(_data);

        if (AutoFilterRef is not null)
            worksheet.AppendChild(new AutoFilter { Reference = AutoFilterRef });
        if (_links.Count > 0)
            worksheet.AppendChild(new Hyperlinks(_links));

        worksheet.AppendChild(new PageMargins { Left = 0.5, Right = 0.5, Top = 0.75, Bottom = 0.75, Header = 0.3, Footer = 0.3 });
        worksheet.AppendChild(new PageSetup
        {
            PaperSize = 9U, // A4
            Orientation = _landscape ? OrientationValues.Landscape : OrientationValues.Portrait,
            FitToWidth = 1U,
            FitToHeight = 0U,
        });

        var meta = _book.Meta;
        worksheet.AppendChild(new HeaderFooter(
            new OddHeader("&L&8&K7F7F7F" + HeaderText(meta.Company ?? "") + "&R&8&K7F7F7F" + HeaderText(meta.Title)),
            new OddFooter("&L&8&K7F7F7F" + HeaderText(ReportLabels.Generated + ": " + meta.GeneratedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture))
                          + "&R&8&K7F7F7F" + ReportLabels.ExcelPageOf)));

        if (_textNumberRanges.Count > 0)
        {
            worksheet.AppendChild(new IgnoredErrors(new IgnoredError
            {
                SequenceOfReferences = new ListValue<StringValue> { InnerText = string.Join(" ", _textNumberRanges) },
                NumberStoredAsText = true,
            }));
        }

        if (_drawings is not null)
            worksheet.AppendChild(new Drawing { Id = Part.GetIdOfPart(_drawings) });

        Part.Worksheet = worksheet;
        Part.Worksheet.Save();
    }

    private static int WrappedLines(string text, double width)
    {
        var capacity = Math.Max(1, (int)Math.Floor((width - 2) / 1.2));
        var lines = 1;
        var current = 0;
        foreach (var word in text.Split(' '))
        {
            var need = current == 0 ? word.Length : current + 1 + word.Length;
            if (need > capacity && current > 0)
            {
                lines++;
                current = word.Length;
            }
            else
            {
                current = need;
            }
        }

        return lines;
    }

    /// <summary>Колонтитул Excel: «&amp;» — управляющий символ, в тексте его надо удваивать; длина
    /// всего колонтитула ограничена, поэтому длинные названия подрезаются.</summary>
    private static string HeaderText(string text)
    {
        var cleaned = text.Replace("&", "&&");
        return cleaned.Length > 90 ? cleaned[..90] + "…" : cleaned;
    }
}

internal enum XlAlign
{
    General,
    Left,
    Center,
    Right,
}

internal enum XlBorder
{
    None,
    Grid,
    Header,
    Total,
}

/// <summary>Реестр стилей книги: каждая уникальная комбинация «формат числа + шрифт + заливка +
/// рамка + выравнивание» получает свой номер при первом обращении.</summary>
internal sealed class XlStyles
{
    public const string FormatMoney = "#,##0.00";
    public const string FormatInteger = "#,##0";
    public const string FormatQuantity = "#,##0.###";
    public const string FormatPercent = "0.0%";
    public const string FormatDays = "#,##0.0";
    public const string FormatDate = "dd.mm.yyyy";
    public const string FormatDateTime = "dd.mm.yyyy hh:mm";
    public const string FormatText = "@";

    private readonly record struct FontKey(bool Bold, bool Italic, bool Underline, double Size, string? Color);

    private readonly record struct XfKey(uint NumFmtId, int Font, int Fill, int Border, XlAlign Align, bool Wrap);

    private static readonly Dictionary<string, uint> BuiltInFormats = new()
    {
        ["General"] = 0U,
        ["0"] = 1U,
        ["0.00"] = 2U,
        ["#,##0"] = 3U,
        ["#,##0.00"] = 4U,
        ["0%"] = 9U,
        ["0.00%"] = 10U,
        ["@"] = 49U,
    };

    private readonly List<FontKey> _fonts = [new(false, false, false, 11, null)];
    private readonly List<string?> _fills = [null, null]; // 0 — без заливки, 1 — gray125 (обязательные)
    private readonly List<XlBorder> _borders = [XlBorder.None];
    private readonly Dictionary<string, uint> _customFormats = new();
    private readonly List<XfKey> _xfs = [new(0U, 0, 0, 0, XlAlign.General, false)];

    // Готовые стили оформления.
    public uint Title => Get(null, new FontKey(true, false, false, 16, ReportPalette.Accent), null, XlBorder.None, XlAlign.General);
    public uint Subtitle => Get(null, new FontKey(false, false, false, 11, ReportPalette.SubtitleText), null, XlBorder.None, XlAlign.General);
    public uint Meta => Get(null, new FontKey(false, true, false, 9, ReportPalette.Muted), null, XlBorder.None, XlAlign.General);
    public uint Note => Get(null, new FontKey(false, true, false, 10, ReportPalette.SubtitleText), null, XlBorder.None, XlAlign.General);
    public uint Heading => Get(null, new FontKey(true, false, false, 12, ReportPalette.Accent), null, XlBorder.None, XlAlign.General);
    public uint Link => Get(null, new FontKey(false, false, true, 11, ReportPalette.Link), null, XlBorder.None, XlAlign.General);
    public uint KeyLabel => Get(null, new FontKey(true, false, false, 11, null), ReportPalette.LabelFill, XlBorder.Grid, XlAlign.Left);
    public uint TotalLabel => Get(null, new FontKey(true, false, false, 11, null), ReportPalette.TotalFill, XlBorder.Total, XlAlign.Left);

    public uint Header(XlAlign align) =>
        Get(null, new FontKey(true, false, false, 11, ReportPalette.HeaderText), ReportPalette.Accent, XlBorder.Header,
            align == XlAlign.Right ? XlAlign.Center : align == XlAlign.Left ? XlAlign.Left : XlAlign.Center, wrap: true);

    public uint Total(string? format) =>
        Get(format, new FontKey(true, false, false, 11, null), ReportPalette.TotalFill, XlBorder.Total, XlAlign.Right);

    public uint Data(string? format, XlAlign align, bool zebra, ReportTone tone, bool bold = false)
    {
        var (fill, color) = tone switch
        {
            ReportTone.Good => (ReportPalette.GoodFill, ReportPalette.GoodText),
            ReportTone.Warn => (ReportPalette.WarnFill, ReportPalette.WarnText),
            ReportTone.Info => (ReportPalette.InfoFill, ReportPalette.InfoText),
            ReportTone.Bad => (ReportPalette.BadFill, ReportPalette.BadText),
            ReportTone.Muted => (ReportPalette.MutedFill, ReportPalette.MutedText),
            _ => (zebra ? ReportPalette.Zebra : null, (string?)null),
        };

        return Get(format, new FontKey(bold, false, false, 11, color), fill, XlBorder.Grid, align);
    }

    private uint Get(string? format, FontKey font, string? fill, XlBorder border, XlAlign align, bool wrap = false)
    {
        var key = new XfKey(FormatId(format), IndexOf(_fonts, font), FillIndex(fill), IndexOf(_borders, border), align, wrap);
        var index = _xfs.IndexOf(key);
        if (index < 0)
        {
            _xfs.Add(key);
            index = _xfs.Count - 1;
        }

        return (uint)index;
    }

    private static int IndexOf<T>(List<T> list, T item)
    {
        var index = list.IndexOf(item);
        if (index >= 0)
            return index;
        list.Add(item);
        return list.Count - 1;
    }

    private int FillIndex(string? rgb)
    {
        if (rgb is null)
            return 0;
        for (var i = 2; i < _fills.Count; i++)
        {
            if (_fills[i] == rgb)
                return i;
        }

        _fills.Add(rgb);
        return _fills.Count - 1;
    }

    private uint FormatId(string? code)
    {
        if (code is null)
            return 0U;
        if (BuiltInFormats.TryGetValue(code, out var builtIn))
            return builtIn;
        if (!_customFormats.TryGetValue(code, out var id))
        {
            id = 164U + (uint)_customFormats.Count;
            _customFormats[code] = id;
        }

        return id;
    }

    public Stylesheet Build()
    {
        var stylesheet = new Stylesheet();

        if (_customFormats.Count > 0)
        {
            stylesheet.AppendChild(new NumberingFormats(
                _customFormats.Select(f => new NumberingFormat { NumberFormatId = f.Value, FormatCode = f.Key }))
            { Count = (uint)_customFormats.Count });
        }

        stylesheet.AppendChild(new Fonts(_fonts.Select(BuildFont)) { Count = (uint)_fonts.Count });

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }));
        foreach (var rgb in _fills.Skip(2))
        {
            fills.AppendChild(new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF" + rgb },
                new BackgroundColor { Indexed = 64U })
            { PatternType = PatternValues.Solid }));
        }
        fills.Count = (uint)_fills.Count;
        stylesheet.AppendChild(fills);

        stylesheet.AppendChild(new Borders(_borders.Select(BuildBorder)) { Count = (uint)_borders.Count });

        stylesheet.AppendChild(new CellStyleFormats(new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U });

        var formats = new CellFormats { Count = (uint)_xfs.Count };
        foreach (var xf in _xfs)
        {
            var format = new CellFormat
            {
                NumberFormatId = xf.NumFmtId,
                FontId = (uint)xf.Font,
                FillId = (uint)xf.Fill,
                BorderId = (uint)xf.Border,
                FormatId = 0U,
            };
            if (xf.NumFmtId != 0)
                format.ApplyNumberFormat = true;
            if (xf.Font != 0)
                format.ApplyFont = true;
            if (xf.Fill != 0)
                format.ApplyFill = true;
            if (xf.Border != 0)
                format.ApplyBorder = true;

            if (xf.Align != XlAlign.General || xf.Wrap)
            {
                var alignment = new Alignment { Vertical = VerticalAlignmentValues.Center };
                if (xf.Align == XlAlign.Left)
                    alignment.Horizontal = HorizontalAlignmentValues.Left;
                else if (xf.Align == XlAlign.Center)
                    alignment.Horizontal = HorizontalAlignmentValues.Center;
                else if (xf.Align == XlAlign.Right)
                    alignment.Horizontal = HorizontalAlignmentValues.Right;
                if (xf.Wrap)
                    alignment.WrapText = true;
                format.AppendChild(alignment);
                format.ApplyAlignment = true;
            }

            formats.AppendChild(format);
        }
        stylesheet.AppendChild(formats);

        stylesheet.AppendChild(new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
        stylesheet.AppendChild(new DifferentialFormats { Count = 0U });
        stylesheet.AppendChild(new TableStyles { Count = 0U, DefaultTableStyle = "TableStyleMedium2", DefaultPivotStyle = "PivotStyleLight16" });
        return stylesheet;
    }

    private static Font BuildFont(FontKey key)
    {
        var font = new Font();
        if (key.Bold)
            font.AppendChild(new Bold());
        if (key.Italic)
            font.AppendChild(new Italic());
        if (key.Underline)
            font.AppendChild(new Underline());
        font.AppendChild(new FontSize { Val = key.Size });
        if (key.Color is not null)
            font.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + key.Color });
        font.AppendChild(new FontName { Val = "Calibri" });
        font.AppendChild(new FontFamilyNumbering { Val = 2 });
        return font;
    }

    private static Border BuildBorder(XlBorder kind)
    {
        static T Side<T>(BorderStyleValues style, string rgb) where T : BorderPropertiesType, new()
        {
            var side = new T { Style = style };
            side.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FF" + rgb });
            return side;
        }

        return kind switch
        {
            XlBorder.Grid => new Border(
                Side<LeftBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<RightBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<TopBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<BottomBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                new DiagonalBorder()),
            XlBorder.Header => new Border(
                Side<LeftBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<RightBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<TopBorder>(BorderStyleValues.Thin, ReportPalette.Accent),
                Side<BottomBorder>(BorderStyleValues.Thin, ReportPalette.Accent),
                new DiagonalBorder()),
            XlBorder.Total => new Border(
                Side<LeftBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<RightBorder>(BorderStyleValues.Thin, ReportPalette.GridLine),
                Side<TopBorder>(BorderStyleValues.Medium, ReportPalette.Accent),
                Side<BottomBorder>(BorderStyleValues.Thin, ReportPalette.Accent),
                new DiagonalBorder()),
            _ => new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
        };
    }
}

/// <summary>Общая таблица строк книги: одинаковые названия товаров хранятся один раз.</summary>
internal sealed class XlSharedStrings
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _items = new();
    private int _references;

    public int Get(string text)
    {
        text = Sanitize(text);
        _references++;
        if (_index.TryGetValue(text, out var index))
            return index;

        _items.Add(text);
        _index[text] = _items.Count - 1;
        return _items.Count - 1;
    }

    public SharedStringTable Build() =>
        new(_items.Select(s => new SharedStringItem(new Text(s)
        {
            Space = s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) ? SpaceProcessingModeValues.Preserve : null,
        })))
        {
            Count = (uint)_references,
            UniqueCount = (uint)_items.Count,
        };

    /// <summary>Управляющие символы (кроме табуляции и переносов) запрещены в XML: одно такое
    /// название товара из базы — и Excel откажется открывать весь файл.</summary>
    internal static string Sanitize(string text) =>
        text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            ? new string(text.Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r').ToArray())
            : text;
}
