using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>Строка листа кнопок весов: кнопка на весах, ячейка PLU, товар, цена, код в штрих-коде.</summary>
public sealed record ScaleKeySheetRow(int Key, int Plu, string Name, string Price, string Code);

/// <summary>
/// 2026-09-30, просьба владельца: «сделай печать на А4 номера кнопок с их описанием что там и
/// сохранение в Word». Лист для продавца у весов: какая кнопка какой товар вызывает.
/// Word — через WordReportBuilder (как отчёты и накладные: Word на кассе не нужен),
/// печать — PrintDocument на A4 с таблицей и переносом на следующие страницы.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScaleKeySheetService
{
    private static string HKey => Tr.T("Кнопка", "Баскыч", "Key", "Tuş", "Tugma");
    private static string HPlu => "PLU";
    private static string HName => Tr.T("Товар", "Товар", "Product", "Ürün", "Tovar");
    private static string HPrice => Tr.T("Цена за кг", "Кг баасы", "Price per kg", "Kg fiyatı", "Kg narxi");
    private static string HCode => Tr.T("Код в ШК", "ШКдагы код", "Barcode code", "Barkod kodu", "ShKdagi kod");

    public static string Title(string scaleName) =>
        Tr.T("Кнопки весов", "Тараза баскычтары", "Scale keys", "Tartı tuşları", "Tarozi tugmalari") + " — " + scaleName;

    public static void SaveWord(string path, string scaleName, string? shop, IReadOnlyList<ScaleKeySheetRow> rows)
    {
        var meta = ReportMeta.Create(Title(scaleName), shop, null);
        using var doc = new WordReportBuilder(path, meta);
        doc.TitleBlock();
        doc.Note(Tr.T("Нажмите кнопку на весах — весы вызовут товар из ячейки PLU. Лист сформирован кассой по последней раскладке.",
            "Таразадагы баскычты басыңыз — тараза PLU уячасындагы товарды чакырат. Барак касса тарабынан акыркы жайгашуу боюнча түзүлдү.",
            "Press a key on the scale — the scale calls the product from the PLU slot. The sheet was made by the till from the latest layout.",
            "Tartıdaki tuşa basın — tartı PLU hücresindeki ürünü çağırır. Sayfa kasa tarafından son düzene göre oluşturuldu.",
            "Tarozidagi tugmani bosing — tarozi PLU katagidagi tovarni chaqiradi. Varaq kassa tomonidan oxirgi tartib bo‘yicha tuzildi."));
        doc.Table(
            [
                new WColumn(HKey, 0.7, Center: true),
                new WColumn(HPlu, 0.7, Center: true),
                new WColumn(HName, 3.4),
                new WColumn(HPrice, 1.1, Numeric: true),
                new WColumn(HCode, 1.0, Center: true),
            ],
            rows.Select(r => new[] { r.Key.ToString(), r.Plu.ToString(), r.Name, r.Price, r.Code }).ToList());
        doc.Save();
    }

    public static IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Печать на A4 выбранным принтером: заголовок, таблица, продолжение на следующих
    /// страницах с повтором шапки. Возвращает текст ошибки или null.</summary>
    public static string? Print(string printerName, string scaleName, string? shop, IReadOnlyList<ScaleKeySheetRow> rows)
    {
        try
        {
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            if (!doc.PrinterSettings.IsValid)
                return Tr.T("Принтер не найден: ", "Принтер табылган жок: ", "Printer not found: ", "Yazıcı bulunamadı: ", "Printer topilmadi: ") + printerName;
            doc.DocumentName = Title(scaleName);
            var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
            if (a4 is not null)
                doc.DefaultPageSettings.PaperSize = a4;
            doc.DefaultPageSettings.Margins = new Margins(50, 50, 50, 50);

            var next = 0;
            var page = 1;
            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var area = e.MarginBounds;
                using var titleFont = new Font("Segoe UI", 16, FontStyle.Bold);
                using var smallFont = new Font("Segoe UI", 9);
                using var headFont = new Font("Segoe UI", 10, FontStyle.Bold);
                using var cellFont = new Font("Segoe UI", 11);
                using var keyFont = new Font("Segoe UI", 13, FontStyle.Bold);
                using var line = new Pen(Color.FromArgb(180, 180, 180), 1);
                using var headBack = new SolidBrush(Color.FromArgb(235, 235, 235));
                var y = (float)area.Top;

                g.DrawString(Title(scaleName) + (page > 1 ? $"  ({page})" : ""), titleFont, Brushes.Black, area.Left, y);
                y += titleFont.GetHeight(g) + 4;
                var sub = string.Join("   ·   ", new[] { shop, DateTime.Now.ToString("dd.MM.yyyy HH:mm") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                g.DrawString(sub, smallFont, Brushes.DimGray, area.Left, y);
                y += smallFont.GetHeight(g) + 12;

                // Колонки: кнопка, PLU, товар, цена, код.
                float[] widths = { 0.11f, 0.10f, 0.49f, 0.16f, 0.14f };
                var xs = new float[widths.Length + 1];
                xs[0] = area.Left;
                for (var i = 0; i < widths.Length; i++)
                    xs[i + 1] = xs[i] + widths[i] * area.Width;
                string[] heads = { HKey, HPlu, HName, HPrice, HCode };

                var headH = headFont.GetHeight(g) + 10;
                g.FillRectangle(headBack, area.Left, y, area.Width, headH);
                for (var i = 0; i < heads.Length; i++)
                    g.DrawString(heads[i], headFont, Brushes.Black, new RectangleF(xs[i] + 4, y + 5, xs[i + 1] - xs[i] - 8, headH), Fmt(i));
                y += headH;
                g.DrawLine(line, area.Left, y, area.Right, y);

                var rowH = Math.Max(keyFont.GetHeight(g), cellFont.GetHeight(g)) + 12;
                while (next < rows.Count && y + rowH <= area.Bottom)
                {
                    var r = rows[next];
                    string[] cells = { r.Key.ToString(), r.Plu.ToString(), r.Name, r.Price, r.Code };
                    for (var i = 0; i < cells.Length; i++)
                        g.DrawString(cells[i], i == 0 ? keyFont : cellFont, Brushes.Black,
                            new RectangleF(xs[i] + 4, y + 6, xs[i + 1] - xs[i] - 8, rowH - 6), Fmt(i));
                    y += rowH;
                    g.DrawLine(line, area.Left, y, area.Right, y);
                    next++;
                }
                page++;
                e.HasMorePages = next < rows.Count;
            };
            doc.Print();
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Лист кнопок весов: печать не удалась: {ex}", "SCALES");
            return ex.Message;
        }
    }

    private static StringFormat Fmt(int column) => new()
    {
        Alignment = column is 0 or 1 or 4 ? StringAlignment.Center : column == 3 ? StringAlignment.Far : StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap,
    };
}
