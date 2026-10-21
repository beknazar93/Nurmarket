using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21: печать документа клиента на A4 — страницы те же, что в предпросмотре (картинки 200 dpi от
/// <see cref="ClientDocRenderer"/>), выбранным принтером Windows. Как лист кнопок весов (ScaleKeySheetService) — без браузера.</summary>
[SupportedOSPlatform("windows")]
public static class ClientDocPrint
{
    public static IReadOnlyList<string> Printers() => ScaleKeySheetService.InstalledPrinters();

    public static string? DefaultPrinter()
    {
        try
        {
            return new PrinterSettings().PrinterName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Печатает страницы. Возвращает текст ошибки или null.</summary>
    public static string? Print(string printerName, string documentName, IReadOnlyList<byte[]> pagePngs)
    {
        if (pagePngs.Count == 0)
            return Tr.T("Нечего печатать.", "Басып чыгара турган эч нерсе жок.", "Nothing to print.", "Yazdırılacak bir şey yok.", "Chop etadigan narsa yo'q.");
        var images = new List<Image>();
        try
        {
            foreach (var png in pagePngs)
                images.Add(Image.FromStream(new MemoryStream(png)));
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            if (!doc.PrinterSettings.IsValid)
                return Tr.T("Принтер не найден: ", "Принтер табылган жок: ", "Printer not found: ", "Yazıcı bulunamadı: ", "Printer topilmadi: ") + printerName;
            doc.DocumentName = documentName;
            var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
            if (a4 is not null)
                doc.DefaultPageSettings.PaperSize = a4;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.OriginAtMargins = false;
            var next = 0;
            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var img = images[next];
                // Поля уже на картинке (весь лист A4): рисуем на всю страницу с учётом непечатной зоны принтера.
                var page = e.PageBounds;
                var hx = e.PageSettings.HardMarginX;
                var hy = e.PageSettings.HardMarginY;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new RectangleF(-hx, -hy, page.Width, page.Height));
                next++;
                e.HasMorePages = next < images.Count;
            };
            doc.Print();
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документ клиента: печать не удалась ({ex.Message}).", "WARNING");
            return Tr.T("Не удалось напечатать: ", "Басып чыгаруу мүмкүн болгон жок: ", "Could not print: ", "Yazdırılamadı: ", "Chop etib bo'lmadi: ") + ex.Message;
        }
        finally
        {
            foreach (var i in images)
                i.Dispose();
        }
    }
}
