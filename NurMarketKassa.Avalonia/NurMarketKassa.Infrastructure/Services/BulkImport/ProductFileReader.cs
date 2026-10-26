using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (М3): чтение таблицы товаров из .xlsx, CSV и буфера обмена. Без интерфейса и сети.
// .xlsx — DocumentFormat.OpenXml (уже подключён к проекту), лист читается потоково (OpenXmlReader), без загрузки всего DOM.
// CSV — кодировка по BOM / строгий UTF-8 / Windows-1251, разделитель ; , или табуляция, кавычки с переносами строк внутри.

/// <summary>Откуда пришла таблица.</summary>
public enum ProductFileKind
{
    Xlsx,
    Csv,
    Clipboard,
}

/// <summary>Кодировка CSV: «авто» — по BOM, иначе строгий UTF-8, не получилось — Windows-1251.</summary>
public enum ProductFileEncoding
{
    Auto,
    Utf8,
    Windows1251,
}

/// <summary>Непустая строка файла. <see cref="FileRow"/> — номер строки так, как его видит человек в Excel (с единицы).</summary>
public sealed class ProductFileRow
{
    internal ProductFileRow(int fileRow, string[] cells, ulong numericMask)
    {
        FileRow = fileRow;
        Cells = cells;
        NumericMask = numericMask;
    }

    public int FileRow { get; }

    public string[] Cells { get; }

    /// <summary>Биты столбцов (с нуля, первые 64), чья ячейка в .xlsx была ЧИСЛОМ, а не текстом.</summary>
    public ulong NumericMask { get; }

    public string this[int column] => column >= 0 && column < Cells.Length ? Cells[column] : "";

    public bool IsNumeric(int column) => column is >= 0 and < 64 && (NumericMask & (1UL << column)) != 0;
}

/// <summary>Прочитанная таблица: все непустые строки подряд, строка заголовков найдена или выбрана вручную.</summary>
public sealed class ProductFileTable
{
    public ProductFileKind Kind { get; init; }
    public string FileName { get; init; } = "";

    /// <summary>.xlsx: имя прочитанного листа и список видимых листов.</summary>
    public string? SheetName { get; init; }
    public IReadOnlyList<string> Sheets { get; init; } = [];

    /// <summary>CSV: «UTF-8», «UTF-8 (BOM)», «Windows-1251», «UTF-16».</summary>
    public string? EncodingName { get; init; }

    /// <summary>CSV и буфер: разделитель столбцов.</summary>
    public char Delimiter { get; init; }

    public List<ProductFileRow> Rows { get; init; } = [];
    public int ColumnCount { get; init; }

    /// <summary>Индекс строки заголовков в <see cref="Rows"/>.</summary>
    public int HeaderIndex { get; private set; }

    /// <summary>В первых 20 строках нашлась строка, где узнано хотя бы два заголовка (иначе заголовками считается первая строка).</summary>
    public bool HeaderRecognized { get; private set; }

    /// <summary>Замечания чтения (формулы без значений, ошибки Excel в ячейках).</summary>
    public List<string> Warnings { get; } = [];

    public IReadOnlyList<string> Headers
    {
        get
        {
            if (HeaderIndex < 0 || HeaderIndex >= Rows.Count)
                return [];
            var cells = Rows[HeaderIndex].Cells;
            var list = new string[Math.Max(ColumnCount, cells.Length)];
            for (var i = 0; i < list.Length; i++)
                list[i] = i < cells.Length ? cells[i] : "";
            return list;
        }
    }

    /// <summary>Строки данных — всё, что после строки заголовков.</summary>
    public IEnumerable<ProductFileRow> DataRows => Rows.Skip(HeaderIndex + 1);

    public int DataRowCount => Math.Max(0, Rows.Count - HeaderIndex - 1);

    /// <summary>Первые строки файла «как есть» — для показа на шаге выбора файла.</summary>
    public IReadOnlyList<ProductFileRow> Preview(int count = 5) => Rows.Take(count).ToList();

    /// <summary>Поправить строку заголовков вручную (индекс в <see cref="Rows"/>).</summary>
    public void SetHeaderRow(int index)
    {
        HeaderIndex = Math.Clamp(index, 0, Math.Max(0, Rows.Count - 1));
        HeaderRecognized = Rows.Count > 0 && ProductImportMapping.CountRecognized(Rows[HeaderIndex].Cells) >= 2;
    }

    internal void DetectHeaderRow()
    {
        for (var i = 0; i < Math.Min(Rows.Count, ProductFileReader.HeaderScanRows); i++)
        {
            if (ProductImportMapping.CountRecognized(Rows[i].Cells) >= 2)
            {
                HeaderIndex = i;
                HeaderRecognized = true;
                return;
            }
        }

        HeaderIndex = 0;
        HeaderRecognized = false;
    }
}

/// <summary>Итог чтения: таблица или понятное объяснение, почему файл не прочитан (исключений наружу не бросает).</summary>
public sealed class ProductFileReadResult
{
    public ProductFileTable? Table { get; init; }
    public string? Error { get; init; }
    public bool Ok => Table is not null;

    internal static ProductFileReadResult Fail(string error) => new() { Error = error };
}

public static class ProductFileReader
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const int MaxRows = 50_000;
    public const int HeaderScanRows = 20;
    private const int MaxCells = 3_000_000;

    private sealed class ReadLimitException(string message) : Exception(message);

    // ------------------------------------------------------------------ вход

    /// <summary>Прочитать файл с диска. Тип определяется по содержимому (zip → .xlsx, старый двоичный .xls → понятная ошибка, иначе CSV).</summary>
    /// <param name="sheet">.xlsx: имя листа (по умолчанию — первый видимый).</param>
    /// <param name="delimiter">CSV: разделитель вручную (по умолчанию — по строке заголовков).</param>
    public static ProductFileReadResult ReadFile(string path, string? sheet = null, ProductFileEncoding encoding = ProductFileEncoding.Auto, char? delimiter = null)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return ProductFileReadResult.Fail(Tr.T("Файл не найден.", "Файл табылган жок.", "File not found.", "Dosya bulunamadı.", "Fayl topilmadi."));
            if (info.Length > MaxFileBytes)
                return ProductFileReadResult.Fail(Tr.T("Файл больше 20 МБ. Разделите его на части.", "Файл 20 МБдан чоң. Бөлүктөргө бөлүңүз.",
                    "The file is larger than 20 MB. Split it into parts.", "Dosya 20 MB'tan büyük. Parçalara bölün.", "Fayl 20 MB dan katta. Uni qismlarga bo'ling."));
            // Файл может быть открыт в Excel (он держит запись) — читаем, разрешая другим писать.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Импорт товаров: файл {path} не прочитан: {ex.Message}", "WARNING");
            return ProductFileReadResult.Fail(Tr.T("Не удалось открыть файл: " + ex.Message, "Файлды ачуу мүмкүн болгон жок: " + ex.Message,
                "Could not open the file: " + ex.Message, "Dosya açılamadı: " + ex.Message, "Faylni ochib bo'lmadi: " + ex.Message));
        }

        var name = Path.GetFileName(path);
        var xlsMessage = Tr.T("Старый формат Excel (.xls) не читается. Сохраните файл как .xlsx или CSV.",
            "Эски Excel форматы (.xls) окулбайт. Файлды .xlsx же CSV катары сактаңыз.",
            "The old Excel format (.xls) is not supported. Save the file as .xlsx or CSV.",
            "Eski Excel biçimi (.xls) desteklenmiyor. Dosyayı .xlsx veya CSV olarak kaydedin.",
            "Eski Excel formati (.xls) qo'llab-quvvatlanmaydi. Faylni .xlsx yoki CSV sifatida saqlang.");
        if (IsOleCompound(bytes))
            return ProductFileReadResult.Fail(xlsMessage);
        if (IsZip(bytes))
            return ReadXlsx(new MemoryStream(bytes, writable: false), name, sheet);
        if (path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            return ProductFileReadResult.Fail(xlsMessage);
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            return ProductFileReadResult.Fail(BrokenFileText);
        return ReadCsv(bytes, name, encoding, delimiter);
    }

    private static string BrokenFileText => Tr.T("Файл повреждён или это не таблица Excel. Откройте его в Excel и сохраните заново (.xlsx или CSV).",
        "Файл бузулган же бул Excel таблицасы эмес. Аны Excel'де ачып, кайра сактаңыз (.xlsx же CSV).",
        "The file is damaged or is not an Excel workbook. Open it in Excel and save it again (.xlsx or CSV).",
        "Dosya bozuk veya bir Excel tablosu değil. Excel'de açıp yeniden kaydedin (.xlsx veya CSV).",
        "Fayl shikastlangan yoki Excel jadvali emas. Uni Excel'da ochib, qayta saqlang (.xlsx yoki CSV).");

    private static bool IsZip(byte[] b) => b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && (b[2] == 0x03 || b[2] == 0x05) && (b[3] == 0x04 || b[3] == 0x06);

    private static bool IsOleCompound(byte[] b) => b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0 && b[4] == 0xA1 && b[5] == 0xB1;

    private static ProductFileReadResult TooManyRows() =>
        ProductFileReadResult.Fail(Tr.T("В файле больше 50 000 строк. Разделите его на части.", "Файлда 50 000 саптан ашык. Бөлүктөргө бөлүңүз.",
            "The file has more than 50,000 rows. Split it into parts.", "Dosyada 50.000 satırdan fazla var. Parçalara bölün.",
            "Faylda 50 000 qatordan ko'p. Uni qismlarga bo'ling."));

    // ------------------------------------------------------------------ .xlsx

    public static ProductFileReadResult ReadXlsx(Stream stream, string fileName, string? sheet = null)
    {
        try
        {
            using var doc = SpreadsheetDocument.Open(stream, false);
            var book = doc.WorkbookPart;
            if (book?.Workbook?.Sheets is null)
                return ProductFileReadResult.Fail(BrokenFileText);

            var visible = book.Workbook.Sheets.Elements<Sheet>()
                .Where(s => s.State?.Value != SheetStateValues.Hidden && s.State?.Value != SheetStateValues.VeryHidden && s.Id?.Value is not null)
                .ToList();
            if (visible.Count == 0)
                return ProductFileReadResult.Fail(Tr.T("В книге нет ни одного видимого листа.", "Китепте бир да көрүнүүчү барак жок.",
                    "The workbook has no visible sheets.", "Çalışma kitabında görünür sayfa yok.", "Kitobda ko'rinadigan varaq yo'q."));

            var chosen = (sheet is null ? null : visible.FirstOrDefault(s => string.Equals(s.Name?.Value, sheet, StringComparison.OrdinalIgnoreCase))) ?? visible[0];
            var sst = ReadSharedStrings(book);
            var part = (WorksheetPart)book.GetPartById(chosen.Id!.Value!);

            var warnings = new List<string>();
            var rows = ReadSheet(part, sst, warnings, out var columns);
            if (rows is null)
                return TooManyRows();

            var table = new ProductFileTable
            {
                Kind = ProductFileKind.Xlsx,
                FileName = fileName,
                SheetName = chosen.Name?.Value,
                Sheets = visible.Select(s => s.Name?.Value ?? "").ToList(),
                Rows = rows,
                ColumnCount = columns,
            };
            table.Warnings.AddRange(warnings);
            table.DetectHeaderRow();
            return new ProductFileReadResult { Table = table };
        }
        catch (ReadLimitException)
        {
            return ProductFileReadResult.Fail(Tr.T("Файл слишком большой по числу ячеек. Разделите его на части.", "Файл уячыкалардын саны боюнча өтө чоң. Бөлүктөргө бөлүңүз.",
                "The file has too many cells. Split it into parts.", "Dosyada çok fazla hücre var. Parçalara bölün.", "Faylda kataklar juda ko'p. Uni qismlarga bo'ling."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Импорт товаров: .xlsx не прочитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return ProductFileReadResult.Fail(BrokenFileText);
        }
    }

    private static List<string> ReadSharedStrings(WorkbookPart book)
    {
        var list = new List<string>();
        var part = book.SharedStringTablePart;
        if (part is null)
            return list;

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(SharedStringTable) || !reader.IsStartElement)
                continue;
            if (!reader.ReadFirstChild())
                break;
            do
            {
                if (reader.ElementType != typeof(SharedStringItem))
                    continue;
                var item = (SharedStringItem)reader.LoadCurrentElement()!;
                var sb = new StringBuilder();
                // Текст ячейки: прямой <t> или куски <r><t>. Фонетическая запись (<rPh>) в значение не входит.
                foreach (var child in item.ChildElements)
                {
                    if (child is Text t)
                        sb.Append(t.Text);
                    else if (child is Run r && r.Text is { } rt)
                        sb.Append(rt.Text);
                }
                list.Add(sb.ToString());
            }
            while (reader.ReadNextSibling());
            break;
        }

        return list;
    }

    /// <summary>null — строк больше предела.</summary>
    private static List<ProductFileRow>? ReadSheet(WorksheetPart part, List<string> sst, List<string> warnings, out int columns)
    {
        var rows = new List<ProductFileRow>();
        columns = 0;
        var cellsSeen = 0;
        var rowCounter = 0;
        var formulaNoValue = 0;
        var errorCells = 0;
        var buffer = new List<(int Col, string Text)>(32);

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Worksheet) || !reader.IsStartElement)
                continue;
            if (!reader.ReadFirstChild())
                break;
            do
            {
                if (reader.ElementType != typeof(SheetData))
                    continue;
                if (!reader.ReadFirstChild())
                    break;
                do
                {
                    if (reader.ElementType != typeof(Row))
                        continue;
                    var row = (Row)reader.LoadCurrentElement()!;
                    rowCounter = row.RowIndex?.Value is { } ri ? (int)ri : rowCounter + 1;

                    buffer.Clear();
                    ulong mask = 0;
                    var nextCol = 0;
                    var any = false;
                    foreach (var cell in row.Elements<Cell>())
                    {
                        var col = cell.CellReference?.Value is { } cr ? ColumnIndex(cr) : nextCol;
                        nextCol = col + 1;
                        if (++cellsSeen > MaxCells)
                            throw new ReadLimitException("cells");
                        var text = CellText(cell, sst, out var numeric, ref formulaNoValue, ref errorCells);
                        if (text.Length == 0)
                            continue;
                        any = true;
                        buffer.Add((col, text));
                        if (numeric && col < 64)
                            mask |= 1UL << col;
                    }

                    if (!any)
                        continue;
                    var width = buffer.Max(b => b.Col) + 1;
                    var values = new string[width];
                    Array.Fill(values, "");
                    foreach (var (col, text) in buffer)
                        values[col] = text.Trim();
                    if (values.All(v => v.Length == 0))
                        continue;
                    if (rows.Count >= MaxRows + 1)
                        return null;
                    rows.Add(new ProductFileRow(rowCounter, values, mask));
                    columns = Math.Max(columns, width);
                }
                while (reader.ReadNextSibling());
            }
            while (reader.ReadNextSibling());
            break;
        }

        if (formulaNoValue > 0)
            warnings.Add(Tr.T($"Формулы без сохранённого значения: {formulaNoValue}. Откройте файл в Excel, сохраните и загрузите снова.",
                $"Сакталган мааниси жок формулалар: {formulaNoValue}. Файлды Excel'де ачып, сактап, кайра жүктөңүз.",
                $"Formulas without a stored value: {formulaNoValue}. Open the file in Excel, save it and load again.",
                $"Kaydedilmiş değeri olmayan formüller: {formulaNoValue}. Dosyayı Excel'de açıp kaydedin ve yeniden yükleyin.",
                $"Saqlangan qiymati yo'q formulalar: {formulaNoValue}. Faylni Excel'da ochib, saqlang va qayta yuklang."));
        if (errorCells > 0)
            warnings.Add(Tr.T($"Ячеек с ошибкой Excel (#Н/Д, #ДЕЛ/0! и т. п.): {errorCells} — они прочитаны пустыми.",
                $"Excel катасы бар уячыкалар: {errorCells} — бош катары окулду.",
                $"Cells with an Excel error (#N/A, #DIV/0! and so on): {errorCells} — read as empty.",
                $"Excel hatası olan hücreler: {errorCells} — boş okundu.",
                $"Excel xatosi bor kataklar: {errorCells} — bo'sh o'qildi."));
        return rows;
    }

    private static string CellText(Cell cell, List<string> sst, out bool numeric, ref int formulaNoValue, ref int errorCells)
    {
        numeric = false;
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";

        var raw = cell.CellValue?.Text;
        if (raw is null)
        {
            if (cell.CellFormula is not null)
                formulaNoValue++;
            return "";
        }

        if (type == CellValues.SharedString)
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < sst.Count ? sst[idx] : "";
        if (type == CellValues.String || type == CellValues.Date)
            return raw;
        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";
        if (type == CellValues.Error)
        {
            errorCells++;
            return "";
        }

        numeric = true;
        return FormatNumber(raw);
    }

    /// <summary>Число из .xlsx без экспоненты: 4600000000001 остаётся «4600000000001» (не «4,6E+12»), а 0,30000000000000004 → «0.3».</summary>
    internal static string FormatNumber(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d))
            return raw;
        if (d == Math.Floor(d))
        {
            if (Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return Math.Abs(d) < 7e28 ? ((decimal)d).ToString(CultureInfo.InvariantCulture) : raw;
        }

        var text = d.ToString("G15", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.OrdinalIgnoreCase)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            text = dec.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>«AB12» → 27 (индекс столбца с нуля).</summary>
    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
                n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z')
                n = n * 26 + (ch - 'a' + 1);
            else
                break;
        }
        return Math.Max(0, n - 1);
    }

    // ------------------------------------------------------------------ CSV и буфер обмена

    public static ProductFileReadResult ReadCsv(byte[] bytes, string fileName, ProductFileEncoding encoding = ProductFileEncoding.Auto, char? delimiter = null)
    {
        try
        {
            var text = Decode(bytes, encoding, out var encodingName);
            return ParseText(text, ProductFileKind.Csv, fileName, delimiter, encodingName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Импорт товаров: CSV не прочитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return ProductFileReadResult.Fail(BrokenFileText);
        }
    }

    /// <summary>Таблица, скопированная из Excel (столбцы разделены табуляцией).</summary>
    public static ProductFileReadResult ReadClipboard(string text, char? delimiter = null)
    {
        try
        {
            return ParseText(text ?? "", ProductFileKind.Clipboard, "", delimiter ?? (text?.Contains('\t') == true ? '\t' : null), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Импорт товаров: буфер обмена не разобран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return ProductFileReadResult.Fail(BrokenFileText);
        }
    }

    private static string Decode(byte[] bytes, ProductFileEncoding encoding, out string name)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            name = "UTF-8 (BOM)";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            name = "UTF-16";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            name = "UTF-16";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (encoding == ProductFileEncoding.Windows1251)
        {
            name = "Windows-1251";
            return Cp1251.GetString(bytes);
        }
        if (encoding == ProductFileEncoding.Utf8)
        {
            name = "UTF-8";
            return Encoding.UTF8.GetString(bytes);
        }

        // UTF-16 без BOM: в тексте с латиницей и цифрами каждый второй байт нулевой.
        var probe = Math.Min(bytes.Length, 400);
        int zerosOdd = 0, zerosEven = 0;
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] != 0)
                continue;
            if ((i & 1) == 1)
                zerosOdd++;
            else
                zerosEven++;
        }
        if (probe >= 8 && zerosOdd * 4 > probe)
        {
            name = "UTF-16";
            return Encoding.Unicode.GetString(bytes);
        }
        if (probe >= 8 && zerosEven * 4 > probe)
        {
            name = "UTF-16";
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            name = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            // Не UTF-8 — «CSV (разделители — запятые)» русского Excel сохраняется в Windows-1251.
            name = "Windows-1251";
            return Cp1251.GetString(bytes);
        }
    }

    private static Encoding Cp1251 => CodePagesEncodingProvider.Instance.GetEncoding(1251)!;

    private static ProductFileReadResult ParseText(string text, ProductFileKind kind, string fileName, char? delimiter, string? encodingName)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var delim = delimiter ?? DetectDelimiter(text);
        var rows = new List<ProductFileRow>();
        var columns = 0;
        var tooMany = false;

        foreach (var (fileRow, cells) in Records(text, delim))
        {
            if (cells.All(c => c.Length == 0))
                continue;
            if (rows.Count >= MaxRows + 1)
            {
                tooMany = true;
                break;
            }
            // Хвостовые пустые ячейки отбрасываем — ширина строки по последней непустой.
            var last = cells.Length;
            while (last > 0 && cells[last - 1].Length == 0)
                last--;
            var values = last == cells.Length ? cells : cells[..last];
            rows.Add(new ProductFileRow(fileRow, values, 0));
            columns = Math.Max(columns, values.Length);
        }

        if (tooMany)
            return TooManyRows();

        var table = new ProductFileTable
        {
            Kind = kind,
            FileName = fileName,
            EncodingName = encodingName,
            Delimiter = delim,
            Rows = rows,
            ColumnCount = columns,
        };
        table.DetectHeaderRow();
        return new ProductFileReadResult { Table = table };
    }

    /// <summary>Записи CSV с номерами (запись с переносом строки внутри кавычек — одна запись, как в Excel). Кавычка открывает ячейку
    /// только в её начале: «12" труба» остаётся как есть.</summary>
    private static IEnumerable<(int FileRow, string[] Cells)> Records(string text, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var atFieldStart = true;
        var recordNo = 1;

        string Finish(StringBuilder b)
        {
            var s = b.ToString().Trim();
            b.Clear();
            // ="4600000000001" — приём Excel, чтобы длинный код не превратился в число.
            if (s.Length >= 4 && s[0] == '=' && s[1] == '"' && s[^1] == '"')
                s = s[2..^1];
            return s;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }

            if (c == '"' && atFieldStart)
            {
                inQuotes = true;
                atFieldStart = false;
            }
            else if (c == delimiter)
            {
                fields.Add(Finish(sb));
                atFieldStart = true;
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(Finish(sb));
                yield return (recordNo, fields.ToArray());
                fields.Clear();
                recordNo++;
                atFieldStart = true;
            }
            else
            {
                sb.Append(c);
                if (!char.IsWhiteSpace(c))
                    atFieldStart = false;
            }
        }

        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(Finish(sb));
            yield return (recordNo, fields.ToArray());
        }
    }

    /// <summary>Разделитель по первым строкам: кавычки не считаются, выигрывает тот, при котором узнаются заголовки, затем — тот, что
    /// даёт одинаковое число столбцов в большинстве строк. Ничего не нашли — «;» (русский Excel).</summary>
    internal static char DetectDelimiter(string text)
    {
        char[] candidates = [';', '\t', ','];
        var lines = new List<string>();
        var counts = candidates.Select(_ => new List<int>()).ToArray();
        var cur = new int[3];
        var line = new StringBuilder();
        var inQuotes = false;
        var limit = Math.Min(text.Length, 200_000);

        void EndLine()
        {
            if (line.ToString().Trim().Length > 0 && lines.Count < 30)
            {
                lines.Add(line.ToString());
                for (var k = 0; k < 3; k++)
                    counts[k].Add(cur[k]);
            }
            line.Clear();
            Array.Clear(cur);
        }

        for (var i = 0; i < limit && lines.Count < 30; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                line.Append(c);
                continue;
            }
            if (!inQuotes && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndLine();
                continue;
            }
            line.Append(c);
            if (!inQuotes)
            {
                for (var k = 0; k < 3; k++)
                {
                    if (c == candidates[k])
                        cur[k]++;
                }
            }
        }
        if (line.Length > 0)
            EndLine();

        var best = ';';
        (int Recognized, int Frequency, int Count) bestScore = (-1, -1, -1);
        for (var k = 0; k < 3; k++)
        {
            var recognized = 0;
            foreach (var l in lines.Take(HeaderScanRows))
            {
                var recognizedHere = ProductImportMapping.CountRecognized(SplitSimple(l, candidates[k]));
                if (recognizedHere >= 2)
                    recognized = Math.Max(recognized, recognizedHere);
            }

            var nonZero = counts[k].Where(n => n > 0).ToList();
            var (frequency, count) = (0, 0);
            if (nonZero.Count > 0)
            {
                var top = nonZero.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
                (frequency, count) = (top.Count(), top.Key);
            }

            var score = (recognized, frequency, count);
            if (score.CompareTo(bestScore) > 0)
            {
                best = candidates[k];
                bestScore = score;
            }
        }

        return bestScore.Frequency <= 0 ? ';' : best;
    }

    private static string[] SplitSimple(string line, char delimiter)
    {
        var parts = line.Split(delimiter);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim().Trim('"');
        return parts;
    }
}
