using SkiaSharp;

namespace NurMarketKassa.Services;

// 2026-11-06 (В18, АЛ-4 и А4-08): лист A4 с сеткой этикеток на SkiaSharp — предпросмотр (раньше на Linux/Android картинка была пустой: A4SheetService.RenderPage
// на System.Drawing), картинка одной этикетки и PDF всего задания (SKDocument — для печати через CUPS и для сохранения). Раскладка — построчно та же, что в
// A4SheetService.DrawCell: название, штрихкод, ПЛУ, описание, цена (старая зачёркнутая над новой), значок «−N%», пунктирная рамка реза. Высоты строк
// считает SkiaText (подъём + спуск + межстрочный), перенос — по словам, не больше двух строк (как LineLimit в GDI+).

public static class A4SheetSkia
{
    private const float WorldDpi = 203f;
    private const uint Gray90 = 0xFF5A5A5A;

    private static float Px(double mm) => (float)(mm / 25.4 * WorldDpi);

    private static SkText Font(A4TextStyle s, uint argb = 0xFF000000, bool strike = false) =>
        new("Arial", (float)Math.Max(Math.Clamp(s.SizePt, 4, 96) * WorldDpi / 72.0, 4), s.Bold, s.Italic, strike, s.Underline, argb);

    private static void DashedRect(SKCanvas canvas, float w, float h, byte gray)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(gray, gray, gray),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = false,
            PathEffect = SKPathEffect.CreateDash([3f, 1f], 0),
        };
        canvas.DrawRect(new SKRect(0.5f, 0.5f, 0.5f + w - 1, 0.5f + h - 1), paint);
    }

    private static void Centered(SKCanvas canvas, IReadOnlyList<string> lines, float x, float y, float width, SkText style)
    {
        var lineH = SkiaText.LineHeight(style);
        foreach (var line in lines)
        {
            var w = SkiaText.Measure(line, style);
            SkiaText.Draw(canvas, line, x + (width - w) / 2f, y, style);
            y += lineH;
        }
    }

    /// <summary>Рисует одну этикетку в мировых координатах canvas (точки 203 dpi, начало — левый верхний угол ячейки). <paramref name="scale"/> — точек устройства
    /// на мировую точку, <paramref name="originX"/>/<paramref name="originY"/> — угол ячейки в устройстве (нужны, чтобы модуль штрихкода был целым).</summary>
    public static void DrawCell(SKCanvas canvas, A4SheetItem it, A4SheetOptions o, double cellWmm, double cellHmm, float scale = 1f, float originX = 0, float originY = 0)
    {
        var W = Px(cellWmm);
        var H = Px(cellHmm);
        var pad = Px(1.2);
        var innerW = Math.Max(W - 2 * pad, 4);
        var innerH = Math.Max(H - 2 * pad, 4);
        var gap = Px(0.8);

        if (o.CutLines)
            DashedRect(canvas, W, H, 170);

        var data = it.Data;
        var nameStyle = Font(o.Name);
        var priceStyle = Font(o.Price);
        var oldStyle = Font(o.OldPrice ?? new A4TextStyle { SizePt = 12, Bold = false }, Gray90, strike: true);
        var pluStyle = Font(o.Plu ?? new A4TextStyle { SizePt = 12, Bold = true });
        var descStyle = Font(o.Description ?? new A4TextStyle { SizePt = 10, Bold = false }, Gray90);
        var badgeStyle = Font(new A4TextStyle { SizePt = 9, Bold = true }, 0xFFFFFFFF);
        var badgeText = o.ShowDiscount && !string.IsNullOrWhiteSpace(data.DiscountText) ? data.DiscountText!.Trim() : null;

        var showName = o.ShowName && !string.IsNullOrWhiteSpace(data.ProductName);
        var code = data.Barcode?.Trim();
        var showBar = o.ShowBarcode && !string.IsNullOrWhiteSpace(code);
        var pluText = o.ShowPlu && !string.IsNullOrWhiteSpace(it.Plu) ? Tr.T("ПЛУ", "ПЛУ", "PLU", "PLU", "PLU") + " " + it.Plu!.Trim() : null;
        var desc = o.ShowDescription && !string.IsNullOrWhiteSpace(it.Description) ? it.Description!.Trim() : null;
        var showPrice = o.ShowPrice && !string.IsNullOrWhiteSpace(data.PriceText);
        var oldText = showPrice && o.ShowDiscount && !string.IsNullOrWhiteSpace(data.OldPriceText) ? data.OldPriceText : null;

        List<string> nameLines = [], descLines = [];
        float hName = 0f, hPlu = 0f, hDesc = 0f, hPrice = 0f, hOld = 0f;
        if (showName)
        {
            nameLines = SkiaText.Wrap(data.ProductName, innerW, nameStyle, 2);
            hName = Math.Min(nameLines.Count * SkiaText.LineHeight(nameStyle), SkiaText.LineHeight(nameStyle) * 2.05f) + 1;
        }
        if (pluText != null)
            hPlu = SkiaText.LineHeight(pluStyle);
        if (desc != null)
        {
            descLines = SkiaText.Wrap(desc, innerW, descStyle, 2);
            hDesc = Math.Min(descLines.Count * SkiaText.LineHeight(descStyle), SkiaText.LineHeight(descStyle) * 2.05f) + 1;
        }
        if (showPrice)
        {
            hPrice = SkiaText.LineHeight(priceStyle);
            if (oldText != null)
                hOld = SkiaText.LineHeight(oldStyle);
        }

        var blocks = (showName ? 1 : 0) + (showBar ? 1 : 0) + (pluText != null ? 1 : 0) + (desc != null ? 1 : 0) + (showPrice ? 1 : 0);
        var gaps = gap * Math.Max(blocks - 1, 0);
        var others = hName + hPlu + hDesc + hPrice + hOld;
        var hBar = 0f;
        if (showBar)
        {
            var wanted = Px(LabelAutoLayout.PxToMm(Math.Clamp(o.BarcodeHeightPx, 8, 300)));
            var room = innerH - others - gaps;
            hBar = Math.Min(wanted, Math.Max(room, Px(5)));
        }
        var total = others + hBar + gaps;
        var y = pad + Math.Max(0, (innerH - total) / 2f);

        if (showName)
        {
            Centered(canvas, nameLines, pad, y, innerW, nameStyle);
            y += hName + gap;
        }
        if (showBar)
        {
            var rect = new SKRect(pad, y, pad + innerW, y + hBar);
            var digitsH = Math.Min(rect.Height * 0.2f, 14f);
            BarcodeLabelService.DrawLinearBlockSkia(canvas, scale, originX, originY, rect, new SKRect(0, 0, W, H), code, LabelBarcodeFormat.Auto, 2,
                digitsH, Math.Clamp(digitsH * 0.7f, 6f, 12f));
            y += hBar + gap;
        }
        if (pluText != null)
        {
            Centered(canvas, [SkiaText.Truncate(pluText, innerW, pluStyle)], pad, y, innerW, pluStyle);
            y += hPlu + gap;
        }
        if (desc != null)
        {
            Centered(canvas, descLines, pad, y, innerW, descStyle);
            y += hDesc + gap;
        }
        if (showPrice)
        {
            if (oldText != null)
            {
                // Значок «−N%» — чёрная плашка с белым текстом справа в строке старой цены.
                var badgeW = 0f;
                if (badgeText != null)
                {
                    var bw = SkiaText.Measure(badgeText, badgeStyle);
                    var bhText = SkiaText.LineHeight(badgeStyle);
                    badgeW = Math.Min(bw + Px(1.6f), innerW * 0.45f);
                    var bh = Math.Min(Math.Max(bhText, hOld), hOld + Px(1));
                    using (var black = new SKPaint { Color = SKColors.Black, IsAntialias = false })
                        canvas.DrawRect(new SKRect(pad + innerW - badgeW, y, pad + innerW, y + bh), black);
                    var shown = SkiaText.Truncate(badgeText, badgeW, badgeStyle);
                    SkiaText.Draw(canvas, shown, pad + innerW - badgeW + (badgeW - SkiaText.Measure(shown, badgeStyle)) / 2f, y + (bh - bhText) / 2f, badgeStyle);
                    badgeW += Px(1);
                }
                var oldShown = SkiaText.Truncate(oldText, innerW - badgeW, oldStyle);
                SkiaText.Draw(canvas, oldShown, pad + (innerW - badgeW - SkiaText.Measure(oldShown, oldStyle)) / 2f, y, oldStyle);
                y += hOld;
            }
            var priceShown = SkiaText.Truncate(data.PriceText, innerW, priceStyle);
            SkiaText.Draw(canvas, priceShown, pad + (innerW - SkiaText.Measure(priceShown, priceStyle)) / 2f, y, priceStyle);
        }
    }

    private static void DrawHeader(SKCanvas canvas, string title, double widthMm)
    {
        var style = Font(new A4TextStyle { SizePt = 11, Bold = true });
        var w = Px(widthMm);
        var text = SkiaText.Truncate(title.Trim(), w, style);
        SkiaText.Draw(canvas, text, (w - SkiaText.Measure(text, style)) / 2f, (Px(A4SheetService.HeaderMm) - SkiaText.LineHeight(style)) / 2f, style);
    }

    /// <summary>Картинка одной этикетки в разрешении <paramref name="dpi"/>.</summary>
    public static SKBitmap RenderCell(A4SheetItem it, A4SheetOptions o, SheetGeometry geo, int dpi)
    {
        var bmp = new SKBitmap(new SKImageInfo(Math.Max((int)Math.Round(geo.CellWmm / 25.4 * dpi), 10), Math.Max((int)Math.Round(geo.CellHmm / 25.4 * dpi), 10),
            SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        var scale = dpi / WorldDpi;
        canvas.Scale(scale);
        DrawCell(canvas, it, o, geo.CellWmm, geo.CellHmm, scale);
        return bmp;
    }

    /// <summary>Предпросмотр одного листа (номер с нуля) — целая страница в разрешении <paramref name="dpi"/>.</summary>
    public static SKBitmap RenderPage(IReadOnlyList<A4SheetItem> expanded, int pageIndex, A4SheetOptions o, int dpi = 110)
    {
        var geo = A4SheetService.GetGeometry(o);
        var bmp = new SKBitmap(new SKImageInfo((int)Math.Round(geo.PageWmm / 25.4 * dpi), (int)Math.Round(geo.PageHmm / 25.4 * dpi), SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        DrawPage(canvas, dpi, expanded, pageIndex, o, geo, forPreview: true);
        return bmp;
    }

    /// <summary>Лист в мировых единицах: <paramref name="dpi"/> — точек устройства на дюйм (для PDF — 600: сетка модулей штрихкода как у принтера).</summary>
    private static void DrawPage(SKCanvas canvas, float dpi, IReadOnlyList<A4SheetItem> expanded, int pageIndex, A4SheetOptions o, SheetGeometry geo, bool forPreview)
    {
        var scale = dpi / WorldDpi;
        if (geo.HasHeader)
        {
            canvas.Save();
            canvas.Scale(scale);
            canvas.Translate(Px(geo.GridLeftMm), Px(A4SheetService.MarginMm));
            DrawHeader(canvas, o.Title, geo.PageWmm - 2 * A4SheetService.MarginMm);
            canvas.Restore();
        }
        var skip = A4SheetService.SkipCells(o);
        var start = Math.Max(pageIndex, 0) * geo.PerPage - skip;
        for (var i = 0; i < geo.PerPage && start + i < expanded.Count; i++)
        {
            var col = i % geo.Columns;
            var row = i / geo.Columns;
            // Угол ячейки в устройстве: для печати — целая точка принтера (как у картинки ячейки RenderCell, которая кладётся в целые точки),
            // для предпросмотра — как есть (так же рисовал GDI+).
            var originX = Px(geo.GridLeftMm + col * geo.StepX) * scale;
            var originY = Px(geo.GridTopMm + row * geo.StepY) * scale;
            if (!forPreview)
            {
                originX = (float)Math.Round(originX);
                originY = (float)Math.Round(originY);
            }
            canvas.Save();
            canvas.Translate(originX, originY);
            canvas.Scale(scale);
            if (start + i < 0)
            {
                // Пропущенная ячейка («Начать с ячейки №») — серый пунктир, только в предпросмотре; на печать не идёт.
                if (forPreview)
                    DashedRect(canvas, Px(geo.CellWmm), Px(geo.CellHmm), 190);
            }
            else
                DrawCell(canvas, expanded[start + i], o, geo.CellWmm, geo.CellHmm, scale, originX, originY);
            canvas.Restore();
        }
    }

    /// <summary>PDF всех листов задания (SKDocument): векторный текст и штрихи, страница A4 (книжная/альбомная — как у настроек). Для печати через CUPS и «Сохранить PDF».</summary>
    public static byte[] CreatePdf(IReadOnlyList<A4SheetItem> items, A4SheetOptions o)
    {
        var expanded = A4SheetService.Expand(items);
        var geo = A4SheetService.GetGeometry(o);
        var pages = Math.Max(1, A4SheetService.PageCount(expanded.Count, o));
        const float printDpi = 600f;
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            var pageW = (float)(geo.PageWmm / 25.4 * 72);
            var pageH = (float)(geo.PageHmm / 25.4 * 72);
            for (var p = 0; p < pages; p++)
            {
                var canvas = doc.BeginPage(pageW, pageH);
                canvas.Scale(72f / printDpi); // дальше рисуем в точках принтера 600 dpi
                DrawPage(canvas, printDpi, expanded, p, o, geo, forPreview: false);
                doc.EndPage();
            }
            doc.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
