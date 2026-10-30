using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

// 2026-10-30 (реестр «Покупки»): выгрузка списка в Excel (.xlsx, итог — настоящей формулой СУММ), CSV и PDF (он же — печать списка).
// Один набор колонок на все форматы: те же, что на экране, без колонок, на которые у пользователя нет права (закупка, сумма, наценка, поставщик
// — по WarehouseWindow.RevisionShowPurchase; флаг money передаёт окно). Итоговая строка — по текущему фильтру.

/// <summary>Шапка выгрузки: заголовок, магазин, период и описание фильтров.</summary>
public sealed record RegistryExportMeta(string Title, string? Shop, string Period, string Filters);

public static class PurchasesRegistryExport
{
    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed record Col(string Title, XlKind Kind, float Mm, XlTotal Total = XlTotal.None, double Max = 60)
    {
        public bool Right => Kind is XlKind.Integer or XlKind.Quantity or XlKind.Money or XlKind.Percent or XlKind.Decimal2;
    }

    private sealed class Table
    {
        public List<Col> Cols { get; } = new();
        public List<object?[]> Rows { get; } = new();
        public object?[] Total { get; set; } = Array.Empty<object?>();
        /// <summary>Индекс колонки, занимающей остаток ширины в PDF (название / поставщик).</summary>
        public int Star { get; set; }
    }

    private static string Money(string title) => ReportLabels.WithUnit(title, ReportLabels.Currency);

    // ------------------------------------------------------------------ таблица

    private static Table BuildTable(bool lines, IReadOnlyList<RegistryDoc> docs, IReadOnlyList<RegistryLine> ls, RegistryTotals t, bool money, bool warehouse)
    {
        var tb = new Table();
        var total = new List<object?>();
        // Колонки: название, вид, ширина в PDF (мм; 0 — остаток ширины), итог в строке «ИТОГО»; значения строк — ниже по режиму.
        var no = L("№", "№", "№", "№", "№");
        if (lines)
        {
            tb.Cols.Add(new Col(no, XlKind.Index, 8)); total.Add(L("ИТОГО", "ЖАЛПЫ", "TOTAL", "TOPLAM", "JAMI"));
            tb.Cols.Add(new Col(L("Код", "Код", "Code", "Kod", "Kod"), XlKind.Code, 14)); total.Add(null);
            tb.Cols.Add(new Col(L("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), XlKind.Code, 25)); total.Add(null);
            tb.Cols.Add(new Col(L("Наименование товара", "Товардын аталышы", "Product name", "Ürün adı", "Mahsulot nomi"), XlKind.Text, 0, Max: 50)); total.Add(null);
            tb.Star = 3;
            tb.Cols.Add(new Col(L("Ед.", "Бирдик", "Unit", "Birim", "Birlik"), XlKind.Text, 9, Max: 12)); total.Add(null);
            tb.Cols.Add(new Col(L("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), XlKind.Quantity, 15, XlTotal.Sum)); total.Add(t.Qty);
            if (money)
            {
                tb.Cols.Add(new Col(Money(L("Цена покупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi")), XlKind.Money, 17)); total.Add(null);
                tb.Cols.Add(new Col(Money(L("Сумма", "Сумма", "Amount", "Tutar", "Summa")), XlKind.Money, 20, XlTotal.Sum)); total.Add(t.Purchase);
                tb.Cols.Add(new Col(L("Наценка, %", "Үстөк, %", "Markup, %", "Kâr, %", "Ustama, %"), XlKind.Percent, 13)); total.Add(t.AvgMarkup);
                tb.Cols.Add(new Col(Money(L("Цена реализации", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi")), XlKind.Money, 17)); total.Add(null);
            }
            tb.Cols.Add(new Col(L("Документ №", "Документ №", "Document №", "Belge №", "Hujjat №"), XlKind.Text, 27)); total.Add(null);
            tb.Cols.Add(new Col(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime, 23)); total.Add(null);
            if (money)
            {
                tb.Cols.Add(new Col(L("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), XlKind.Text, 26, Max: 40)); total.Add(null);
            }
            if (warehouse)
            {
                tb.Cols.Add(new Col(L("Склад", "Склад", "Warehouse", "Depo", "Ombor"), XlKind.Text, 18, Max: 24)); total.Add(null);
            }
            tb.Cols.Add(new Col(L("Пользователь", "Колдонуучу", "User", "Kullanıcı", "Foydalanuvchi"), XlKind.Text, 18, Max: 30)); total.Add(null);
            foreach (var l in ls)
            {
                var row = new List<object?> { l.No, l.Code, l.Barcode, l.Name, l.Unit, l.Qty };
                if (money)
                {
                    row.Add(l.Purchase);
                    row.Add(l.Sum);
                    row.Add(l.View.Percent);
                    row.Add(l.View.Sale > 0 ? l.View.Sale : null);
                }
                row.Add(l.Doc.Number);
                row.Add(l.Doc.At);
                if (money)
                    row.Add(l.Doc.SupplierName);
                if (warehouse)
                    row.Add(l.Doc.Warehouse);
                row.Add(l.Doc.CreatedBy);
                tb.Rows.Add(row.ToArray());
            }
        }
        else
        {
            var n = 0;
            tb.Cols.Add(new Col(no, XlKind.Index, 8)); total.Add(L("ИТОГО", "ЖАЛПЫ", "TOTAL", "TOPLAM", "JAMI"));
            tb.Cols.Add(new Col(L("Номер документа", "Документтин номери", "Document number", "Belge numarası", "Hujjat raqami"), XlKind.Text, 29, Max: 24)); total.Add(null);
            tb.Cols.Add(new Col(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime, 24)); total.Add(null);
            tb.Cols.Add(new Col(L("№ накладной поставщика", "Жеткирүүчүнүн накладной №", "Supplier invoice №", "Tedarikçi fatura №", "Yetkazib beruvchi yuk xati №"), XlKind.Text, 22, Max: 22)); total.Add(null);
            if (money)
            {
                tb.Cols.Add(new Col(L("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), XlKind.Text, 0, Max: 40)); total.Add(null);
                tb.Star = tb.Cols.Count - 1;
            }
            if (warehouse)
            {
                tb.Cols.Add(new Col(L("Склад", "Склад", "Warehouse", "Depo", "Ombor"), XlKind.Text, 18, Max: 24)); total.Add(null);
            }
            tb.Cols.Add(new Col(L("Принял", "Кабыл алды", "Received by", "Kabul eden", "Qabul qildi"), XlKind.Text, 22, Max: 30)); total.Add(null);
            if (!money)
                tb.Star = tb.Cols.Count - 1;
            tb.Cols.Add(new Col(L("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), XlKind.Integer, 13, XlTotal.Sum)); total.Add(t.Lines);
            tb.Cols.Add(new Col(L("Штук", "Даана", "Pieces", "Adet", "Dona"), XlKind.Quantity, 15, XlTotal.Sum)); total.Add(t.Qty);
            if (money)
            {
                tb.Cols.Add(new Col(Money(L("Сумма закупки", "Сатып алуу суммасы", "Purchase amount", "Alış tutarı", "Xarid summasi")), XlKind.Money, 24, XlTotal.Sum)); total.Add(t.Purchase);
                tb.Cols.Add(new Col(L("Ср. наценка, %", "Орточо үстөк, %", "Avg markup, %", "Ort. kâr, %", "O'rtacha ustama, %"), XlKind.Percent, 17)); total.Add(t.AvgMarkup);
                tb.Cols.Add(new Col(L("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), XlKind.Text, 20, Max: 22)); total.Add(null);
            }
            tb.Cols.Add(new Col(L("Статус", "Абалы", "Status", "Durum", "Holat"), XlKind.Text, 22, Max: 22)); total.Add(null);
            if (money)
            {
                tb.Cols.Add(new Col(Money(L("Возврат", "Кайтаруу", "Returned", "İade", "Qaytarma")), XlKind.Money, 20, XlTotal.Sum)); total.Add(docs.Sum(d => d.Returned));
            }
            foreach (var d in docs)
            {
                var row = new List<object?> { ++n, d.Number, d.At, d.SupplierDoc };
                if (money)
                    row.Add(d.SupplierName);
                if (warehouse)
                    row.Add(d.Warehouse);
                row.Add(d.CreatedBy);
                row.Add(d.Items);
                row.Add(d.Qty);
                if (money)
                {
                    row.Add(d.Total);
                    row.Add(d.AvgMarkup);
                    row.Add(d.Pay);
                }
                row.Add(PurchasesRegistry.StatusText(d));
                if (money)
                    row.Add(d.Returned > 0.005 ? d.Returned : null);
                tb.Rows.Add(row.ToArray());
            }
        }
        tb.Total = total.ToArray();
        return tb;
    }

    // ------------------------------------------------------------------ вызов

    public static string Extension(bool csv) => csv ? "csv" : "xlsx";

    public static string SuggestFileName(RegistryExportMeta meta, bool csv, bool lines) =>
        ExportFileName.Build(lines ? L("Покупки (позиции)", "Сатып алуулар (позициялар)", "Purchases (items)", "Alışlar (kalemler)", "Xaridlar (pozitsiyalar)")
                                   : L("Покупки (документы)", "Сатып алуулар (документтер)", "Purchases (documents)", "Alışlar (belgeler)", "Xaridlar (hujjatlar)"),
            meta.Period, meta.Shop, Extension(csv));

    /// <summary>Сохранить реестр в файл (Excel/CSV) атомарно: сначала .part, потом замена. Вызывать с фонового потока.</summary>
    public static void Save(string path, bool csv, bool lines, IReadOnlyList<RegistryDoc> docs, IReadOnlyList<RegistryLine> ls, RegistryTotals totals,
        RegistryExportMeta meta, bool money, bool warehouse, CancellationToken ct = default)
    {
        var table = BuildTable(lines, docs, ls, totals, money, warehouse);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".part";
        try
        {
            if (csv)
                WriteCsv(temp, table, ct);
            else
                WriteExcel(temp, table, lines, totals, meta, money, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* временный файл не критичен */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ Excel

    private static void WriteExcel(string path, Table table, bool lines, RegistryTotals totals, RegistryExportMeta meta, bool money, CancellationToken ct)
    {
        var reportMeta = ReportMeta.Create(meta.Title, meta.Shop, meta.Period);
        using var book = new ExcelWorkbookBuilder(path, reportMeta);
        var sheet = book.AddSheet(lines ? L("Позиции", "Позициялар", "Items", "Kalemler", "Pozitsiyalar") : L("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar"),
            meta.Title, meta.Filters.Length > 0 ? meta.Filters : null, landscape: true);
        IEnumerable<object?[]> Rows()
        {
            var i = 0;
            foreach (var r in table.Rows)
            {
                if ((++i & 0x3FFF) == 0)
                    ct.ThrowIfCancellationRequested();
                yield return r;
            }
        }
        sheet.AddTable(table.Cols.Select(c => new XlColumn(c.Title, c.Kind, c.Total, c.Max)).ToList(), Rows());
        ct.ThrowIfCancellationRequested();

        var summary = book.AddSheet(L("Итоги", "Жыйынтык", "Summary", "Özet", "Yakun"), meta.Title);
        var items = new List<XlKeyValue>
        {
            new(L("Документ", "Документ", "Document", "Belge", "Hujjat"), meta.Title),
            new(ReportLabels.Shop, string.IsNullOrWhiteSpace(meta.Shop) ? "—" : meta.Shop!.Trim()),
            new(ReportLabels.Period, meta.Period.Length > 0 ? meta.Period : L("за всё время", "бардык убакыт", "all time", "tüm zamanlar", "butun davr")),
            new(L("Фильтры", "Чыпкалар", "Filters", "Filtreler", "Filtrlar"), meta.Filters.Length > 0 ? meta.Filters : "—"),
            new(L("Документов", "Документтер", "Documents", "Belgeler", "Hujjatlar"), totals.Docs, XlKind.Integer),
            new(L("Позиций", "Позициялар", "Items", "Kalemler", "Pozitsiyalar"), totals.Lines, XlKind.Integer),
            new(L("Штук", "Даана", "Pieces", "Adet", "Dona"), totals.Qty, XlKind.Quantity),
        };
        if (money)
        {
            items.Add(new XlKeyValue(Money(L("Сумма закупки", "Сатып алуу суммасы", "Purchase amount", "Alış tutarı", "Xarid summasi")), totals.Purchase, XlKind.Money));
            if (totals.AvgMarkup is { } avg)
            {
                items.Add(new XlKeyValue(L("Средняя наценка, %", "Орточо үстөк, %", "Average markup, %", "Ortalama kâr, %", "O'rtacha ustama, %"), avg, XlKind.Decimal2));
                items.Add(new XlKeyValue(Money(L("Ожидаемая прибыль", "Күтүлгөн пайда", "Expected profit", "Beklenen kâr", "Kutilgan foyda")), totals.Profit, XlKind.Money));
            }
        }
        summary.AddKeyValues(items);
        book.Save();
    }

    // ------------------------------------------------------------------ CSV

    private static void WriteCsv(string path, Table table, CancellationToken ct)
    {
        const char sep = ';';
        var culture = ReportFormat.Culture;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16) { NewLine = "\r\n" };
        void Line(IEnumerable<string> cells) => w.WriteLine(string.Join(sep, cells.Select(Escape)));
        Line(table.Cols.Select(c => c.Title));
        var i = 0;
        foreach (var row in table.Rows)
        {
            if ((++i & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            Line(row.Select((v, k) => Format(table.Cols[k].Kind, v, culture)));
        }
        Line(table.Total.Select((v, k) => Format(table.Cols[k].Kind, v, culture, total: true)));
    }

    /// <summary>Значение ячейки для CSV/PDF. Числа без разделителя тысяч (CSV читают и Excel, и учётные программы); в PDF — с разрядами.</summary>
    private static string Format(XlKind kind, object? v, CultureInfo culture, bool total = false, bool grouped = false)
    {
        if (v is null)
            return "";
        if (v is string s)
            return s;
        return v switch
        {
            DateTime d => d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
            int n => n.ToString(grouped ? "#,0" : "0", culture),
            double x when kind == XlKind.Money => x.ToString(grouped ? "#,0.00" : "0.00", culture),
            double x when kind == XlKind.Percent => x.ToString("0.0", culture),
            double x => x.ToString(grouped ? "#,0.###" : "0.###", culture),
            _ => Convert.ToString(v, culture) ?? "",
        };
    }

    /// <summary>Кавычки для «;», «"» и переводов строки; текст, начинающийся с «= + @», получает апостроф (Excel не примет его за формулу).</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = value;
        if (text[0] is '=' or '+' or '@' or '\t' or '\r' || (text[0] == '-' && !(text.Length > 1 && (char.IsDigit(text[1]) || text[1] == ',' || text[1] == '.'))))
            text = "'" + text;
        return text.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    // ------------------------------------------------------------------ PDF (и печать)

    /// <summary>PDF списка, A4 альбомная, шапка таблицы повторяется на каждой странице, внизу — «ИТОГО». Печать списка — те же страницы картинками.</summary>
    public static Document BuildPdf(bool lines, IReadOnlyList<RegistryDoc> docs, IReadOnlyList<RegistryLine> ls, RegistryTotals totals, RegistryExportMeta meta, bool money, bool warehouse)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var table = BuildTable(lines, docs, ls, totals, money, warehouse);
        const string Ink = "#1B2330", Soft = "#6B7686", Rule = "#C9D3E0", HeadFill = "#1F4E78", Zebra = "#F3F6FA", TotalFill = "#FDE9B0";
        var culture = ReportFormat.Culture;
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(8, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(7.5f).FontColor(Ink));
                page.Header().Column(col =>
                {
                    col.Item().Text(meta.Title).FontSize(14).Bold();
                    var info = new List<string>();
                    if (!string.IsNullOrWhiteSpace(meta.Shop))
                        info.Add(meta.Shop!.Trim());
                    if (meta.Period.Length > 0)
                        info.Add(ReportLabels.Period + ": " + meta.Period);
                    if (meta.Filters.Length > 0)
                        info.Add(meta.Filters);
                    info.Add(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
                    col.Item().PaddingBottom(4).Text(string.Join("   ·   ", info)).FontColor(Soft);
                });
                page.Content().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        for (var k = 0; k < table.Cols.Count; k++)
                        {
                            if (k == table.Star)
                                c.RelativeColumn(1);
                            else
                                c.ConstantColumn(table.Cols[k].Mm, Unit.Millimetre);
                        }
                    });
                    IContainer Head(IContainer x) => x.Background(HeadFill).PaddingVertical(3).PaddingHorizontal(2).DefaultTextStyle(s => s.SemiBold().FontSize(7f).FontColor("#FFFFFF"));
                    t.Header(h =>
                    {
                        foreach (var c in table.Cols)
                        {
                            var cell = h.Cell().Element(Head);
                            if (c.Right)
                                cell.AlignRight().Text(c.Title);
                            else
                                cell.Text(c.Title);
                        }
                    });
                    var i = 0;
                    foreach (var row in table.Rows)
                    {
                        var bg = ++i % 2 == 0 ? Zebra : "#FFFFFF";
                        IContainer Cell(IContainer x) => x.Background(bg).BorderBottom(0.4f).BorderColor(Rule).PaddingVertical(2).PaddingHorizontal(2);
                        for (var k = 0; k < table.Cols.Count; k++)
                        {
                            var text = Format(table.Cols[k].Kind, row[k], culture, grouped: true);
                            var cell = t.Cell().Element(Cell);
                            if (table.Cols[k].Right)
                                cell.AlignRight().Text(text);
                            else
                                cell.Text(text);
                        }
                    }
                    IContainer Total(IContainer x) => x.Background(TotalFill).PaddingVertical(3).PaddingHorizontal(2).DefaultTextStyle(s => s.SemiBold());
                    for (var k = 0; k < table.Cols.Count; k++)
                    {
                        var text = Format(table.Cols[k].Kind, table.Total[k], culture, total: true, grouped: true);
                        var cell = t.Cell().Element(Total);
                        if (table.Cols[k].Right)
                            cell.AlignRight().Text(text);
                        else
                            cell.Text(text);
                    }
                });
                page.Footer().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(7f).FontColor(Soft));
                    t.Span(L("стр. ", "бет ", "page ", "sayfa ", "bet "));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
    }

    public static void SavePdf(string path, bool lines, IReadOnlyList<RegistryDoc> docs, IReadOnlyList<RegistryLine> ls, RegistryTotals totals, RegistryExportMeta meta, bool money, bool warehouse)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        BuildPdf(lines, docs, ls, totals, meta, money, warehouse).GeneratePdf(path);
    }

    /// <summary>Страницы списка PNG (150 dpi) — для печати на принтер по умолчанию.</summary>
    public static List<byte[]> PageImages(bool lines, IReadOnlyList<RegistryDoc> docs, IReadOnlyList<RegistryLine> ls, RegistryTotals totals, RegistryExportMeta meta, bool money, bool warehouse) =>
        BuildPdf(lines, docs, ls, totals, meta, money, warehouse).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 150 }).ToList();

    /// <summary>Печать страниц списка выбранным принтером на A4 альбомной (как ClientDocPrint, но с поворотом листа: колонок много). Возвращает текст ошибки или null.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? PrintLandscape(string printerName, string documentName, IReadOnlyList<byte[]> pagePngs)
    {
        if (pagePngs.Count == 0)
            return L("Нечего печатать.", "Басып чыгара турган эч нерсе жок.", "Nothing to print.", "Yazdırılacak bir şey yok.", "Chop etadigan narsa yo'q.");
        var images = new List<System.Drawing.Image>();
        try
        {
            foreach (var png in pagePngs)
                images.Add(System.Drawing.Image.FromStream(new MemoryStream(png)));
            using var doc = new System.Drawing.Printing.PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            if (!doc.PrinterSettings.IsValid)
                return L("Принтер не найден: ", "Принтер табылган жок: ", "Printer not found: ", "Yazıcı bulunamadı: ", "Printer topilmadi: ") + printerName;
            doc.DocumentName = documentName;
            var a4 = doc.PrinterSettings.PaperSizes.Cast<System.Drawing.Printing.PaperSize>().FirstOrDefault(p => p.Kind == System.Drawing.Printing.PaperKind.A4);
            if (a4 is not null)
                doc.DefaultPageSettings.PaperSize = a4;
            doc.DefaultPageSettings.Landscape = true;
            doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);
            doc.OriginAtMargins = false;
            var next = 0;
            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var page = e.PageBounds;
                var hx = e.PageSettings.HardMarginX;
                var hy = e.PageSettings.HardMarginY;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(images[next], new System.Drawing.RectangleF(-hx, -hy, page.Width, page.Height));
                next++;
                e.HasMorePages = next < images.Count;
            };
            doc.Print();
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: печать не удалась ({ex.Message}).", "WARNING");
            return L("Не удалось напечатать: ", "Басып чыгаруу мүмкүн болгон жок: ", "Could not print: ", "Yazdırılamadı: ", "Chop etib bo'lmadi: ") + ex.Message;
        }
        finally
        {
            foreach (var i in images)
                i.Dispose();
        }
    }
}
