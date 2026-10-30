using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-29, Android: «не отображаются примеры штрихкода» — предпросмотр этикетки/ценника строился через
/// System.Drawing (GDI+), которого на Android и Linux нет (PlatformNotSupportedException, в журнале лишь WARNING),
/// и вместо картинки было пусто. Теперь в Windows картинка строится как раньше, а на остальных системах — сцена
/// (<see cref="LabelScene"/>, Infrastructure) рисуется средствами Avalonia (RenderTargetBitmap). Печать на принтер не затронута.</summary>
internal static class LabelSceneRenderer
{
    /// <summary>Предпросмотр этикетки по шаблону.</summary>
    public static Bitmap LabelPreview(string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null, string? variantText = null)
    {
        if (OperatingSystem.IsWindows())
        {
            using var bmp = BarcodeLabelService.GenerateLabelBitmap(productName, barcode, priceText, template, sku, unit, storeName, variantText);
            return FromGdi(bmp);
        }
        return Render(BarcodeLabelService.BuildLabelScene(productName, barcode, priceText, template, sku, unit, storeName, variantText));
    }

    /// <summary>Предпросмотр готового ценника (не «свой шаблон»).</summary>
    public static Bitmap PriceTagPreview(PriceTagKind kind, PriceTagData data, double widthMm, double heightMm)
    {
        if (OperatingSystem.IsWindows())
        {
            using var bmp = PriceTagService.GenerateBitmap(kind, data, widthMm, heightMm);
            return FromGdi(bmp);
        }
        return Render(PriceTagService.BuildScene(kind, data, widthMm, heightMm));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Bitmap FromGdi(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private const double PtToPx = 96.0 / 72.0;

    public static Bitmap Render(LabelScene scene)
    {
        var target = new RenderTargetBitmap(new PixelSize(scene.WidthPx, scene.HeightPx), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, scene.WidthPx, scene.HeightPx));
            foreach (var item in scene.Items)
            {
                switch (item)
                {
                    case LabelSceneRect r:
                        ctx.DrawRectangle(new SolidColorBrush(Color.FromUInt32(r.Argb)), null, new Rect(r.X, r.Y, r.W, r.H));
                        break;
                    case LabelSceneBars bars:
                        DrawBars(ctx, bars);
                        break;
                    case LabelSceneText text:
                        DrawText(ctx, text);
                        break;
                }
            }
        }
        // В обычный Bitmap через PNG: RenderTargetBitmap держит ресурсы рисования, а окна хранят и освобождают Bitmap.
        using var ms = new MemoryStream();
        target.Save(ms);
        target.Dispose();
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static void DrawBars(DrawingContext ctx, LabelSceneBars b)
    {
        var width = b.Cells.GetLength(0);
        var rows = b.Cells.GetLength(1);
        var oneRow = rows == 1;
        for (var y = 0; y < rows; y++)
        {
            var x = 0;
            while (x < width)
            {
                if (!b.Cells[x, y])
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < width && b.Cells[x, y])
                    x++;
                var top = oneRow ? b.Y : b.Y + y * b.Module;
                var height = oneRow ? b.HeightPx : b.Module;
                ctx.DrawRectangle(Brushes.Black, null, new Rect(b.X + start * b.Module, top, (x - start) * b.Module, height));
            }
        }
    }

    private static FormattedText Format(string text, LabelSceneText t, double sizePt)
    {
        var typeface = new Typeface(t.Family, t.Italic ? FontStyle.Italic : FontStyle.Normal, t.Bold ? FontWeight.Bold : FontWeight.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, sizePt * PtToPx,
            new SolidColorBrush(Color.FromUInt32(t.Argb)));
        if (t.Strike)
            ft.SetTextDecorations(TextDecorations.Strikethrough);
        return ft;
    }

    private static void DrawText(DrawingContext ctx, LabelSceneText t)
    {
        FormattedText ft;
        if (t.SizePt > 0)
        {
            // Ручной размер: перенос по ширине блока, по центру, обрезка по рамке.
            ft = Format(t.Text, t, t.SizePt);
            ft.MaxTextWidth = t.W;
            ft.TextAlignment = TextAlignment.Center;
            using (ctx.PushClip(new Rect(t.X, t.Y, t.W, t.H)))
                ctx.DrawText(ft, new Point(t.X, t.Y + (t.H - ft.Height) / 2));
            return;
        }

        // Автоподбор: от максимума вниз до 6 пт, пока строка не влезет по ширине; не влезла — с многоточием.
        var size = t.MaxAutoPt;
        ft = Format(t.Text, t, size);
        while (size > 6 && ft.Width > t.W)
        {
            size -= 1;
            ft = Format(t.Text, t, size);
        }
        var display = t.Text;
        if (ft.Width > t.W)
        {
            while (display.Length > 1 && Format(display + "…", t, size).Width > t.W)
                display = display[..^1];
            ft = Format(display + "…", t, size);
        }
        ctx.DrawText(ft, new Point(t.X + Math.Max(0, (t.W - ft.Width) / 2), t.Y + Math.Max(0, (t.H - ft.Height) / 2)));
    }
}
