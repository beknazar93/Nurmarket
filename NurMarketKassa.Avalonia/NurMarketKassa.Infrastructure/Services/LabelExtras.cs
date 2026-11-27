using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 («Редактор ценников»): виды своих элементов этикетки поверх встроенных (штрихкод, название, цена, артикул,
/// единица, магазин, размер и цвет).</summary>
public enum LabelExtraKind
{
    /// <summary>Произвольный текст («Акция!», «Годен до…»).</summary>
    FreeText = 0,
    /// <summary>Старая цена (зачёркнутая) — из карточки товара со скидкой.</summary>
    OldPrice = 1,
    /// <summary>Плашка скидки «−20%».</summary>
    Discount = 2,
    Brand = 3,
    /// <summary>Дата печати (по умолчанию дд.ММ.гггг; формат — в поле «Текст»).</summary>
    Date = 4,
    /// <summary>Ячейка/полка хранения.</summary>
    Cell = 5,
    /// <summary>Линия (разделитель): толщина — высота блока.</summary>
    Line = 6,
    /// <summary>Логотип — чёрно-белая картинка из файла.</summary>
    Logo = 7,
    /// <summary>2026-11-26 (отчёт «Конкуренты», п. 3): QR на витрину магазина (market.nurcrm.kg/catalog/{slug}) с подписью «Заказать онлайн».
    /// Адрес берётся у магазина; витрины нет — элемент на этикетке не рисуется. ДОБАВЛЕН В КОНЕЦ: числа прежних видов в сохранённых шаблонах не меняются.</summary>
    ShopQr = 8,
}

/// <summary>Свой элемент этикетки: раскладка и оформление (как у встроенных) + вид, текст, файл картинки.</summary>
public sealed class LabelCustomElement : LabelElementLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public LabelExtraKind Kind { get; set; }

    /// <summary>Текст для «Произвольный текст»; формат даты для «Дата»; пусто — по умолчанию.</summary>
    public string? Text { get; set; }

    /// <summary>Путь к файлу логотипа (PNG/JPG).</summary>
    public string? ImagePath { get; set; }

    public LabelCustomElement CloneExtra()
    {
        var c = new LabelCustomElement { Id = Id, Kind = Kind, Text = Text, ImagePath = ImagePath };
        LabelTemplate.CopyLayout(this, c);
        return c;
    }
}

/// <summary>Данные текущего товара для своих элементов. В шаблон не сохраняются.</summary>
public sealed record LabelItemData(string? Brand = null, string? OldPriceText = null, double DiscountPercent = 0, string? Cell = null, string? ShopUrl = null);

/// <summary>Общие правила своих элементов: текст, значения по умолчанию, образец данных для редактора. Рисуют их GDI+ (BarcodeLabelService),
/// сцена (BuildLabelScene → Avalonia и Skia) — по одним и тем же функциям отсюда, поэтому вид везде одинаков.</summary>
public static class LabelExtras
{
    public static readonly LabelExtraKind[] AllKinds =
    {
        LabelExtraKind.FreeText, LabelExtraKind.OldPrice, LabelExtraKind.Discount, LabelExtraKind.Brand,
        LabelExtraKind.Date, LabelExtraKind.Cell, LabelExtraKind.Line, LabelExtraKind.Logo, LabelExtraKind.ShopQr,
    };

    /// <summary>Образец данных товара для предпросмотра, пока товар не выбран.</summary>
    public static LabelItemData Sample => new("Nur", "350 сом", 18, "A-12", StorefrontLink.SampleUrl);

    /// <summary>2026-11-26: адрес для QR витрины на этикетке: образец предпросмотра, иначе адрес магазина; null — витрины нет (QR не рисуется).</summary>
    public static string? ShopQrUrl(LabelTemplate t) => t.Item?.ShopUrl ?? StorefrontLink.PrintableUrl();

    /// <summary>2026-11-26: раскладка QR витрины в миллиметрах: сам QR и (если блок достаточно высокий) строка подписи «Заказать онлайн» под ним.
    /// Одна на GDI+ (BarcodeLabelService) и сцену (Avalonia/Skia) — вид везде одинаков.</summary>
    public static (LabelElementLayout Qr, LabelElementLayout? Caption) ShopQrLayout(LabelCustomElement e)
    {
        if (e.HeightMm < 8 || e.WidthMm < 7)
            return (new LabelElementLayout { XMm = e.XMm, YMm = e.YMm, WidthMm = e.WidthMm, HeightMm = e.HeightMm }, null);
        var cap = Math.Clamp(e.HeightMm * 0.2, 1.5, 2.2);
        return (new LabelElementLayout { XMm = e.XMm, YMm = e.YMm, WidthMm = e.WidthMm, HeightMm = e.HeightMm - cap },
            new LabelElementLayout { XMm = e.XMm, YMm = e.YMm + e.HeightMm - cap, WidthMm = e.WidthMm, HeightMm = cap, FontFamily = e.FontFamily, FontSizePx = 0, Align = 1 }); // FontSizePx = 0 — подпись подбирает размер сама, ручной размер шаблона её не раздувает
    }

    /// <summary>Текст элемента для рисования; null/пусто — элемент не рисуется (нет данных у товара).</summary>
    public static string? TextFor(LabelCustomElement e, LabelTemplate t)
    {
        var item = t.Item;
        switch (e.Kind)
        {
            case LabelExtraKind.FreeText:
                return e.Text;
            case LabelExtraKind.OldPrice:
                return string.IsNullOrWhiteSpace(item?.OldPriceText) ? null : BarcodeLabelService.FormatPriceText(item!.OldPriceText!, t);
            case LabelExtraKind.Discount:
                return item is { DiscountPercent: > 0 and <= 100 } ? "−" + item.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%" : null;
            case LabelExtraKind.Brand:
                return item?.Brand;
            case LabelExtraKind.Cell:
                return item?.Cell;
            case LabelExtraKind.Date:
                try
                {
                    return DateTime.Now.ToString(string.IsNullOrWhiteSpace(e.Text) ? "dd.MM.yyyy" : e.Text, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    return DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                }
            default:
                return null;
        }
    }

    /// <summary>Жирность по умолчанию: скидка и цена-подобные — жирные.</summary>
    public static bool IsBold(LabelCustomElement e) => e.Bold ?? (e.Kind == LabelExtraKind.Discount);

    public static bool IsStrike(LabelCustomElement e) => e.Kind == LabelExtraKind.OldPrice;

    /// <summary>Новый элемент: размер по виду, положение — в первом свободном месте внизу этикетки (дальше редактор двигает руками).</summary>
    public static LabelCustomElement Create(LabelExtraKind kind, LabelTemplate t)
    {
        var w = Math.Max(t.WidthMm - 4, 4);
        var e = kind switch
        {
            LabelExtraKind.Discount => new LabelCustomElement { WidthMm = Math.Min(14, w), HeightMm = 5 },
            LabelExtraKind.Line => new LabelCustomElement { WidthMm = w, HeightMm = 0.4 },
            LabelExtraKind.Logo => new LabelCustomElement { WidthMm = Math.Min(10, w), HeightMm = Math.Min(10, Math.Max(t.HeightMm - 2, 3)) },
            LabelExtraKind.ShopQr => new LabelCustomElement { WidthMm = Math.Min(14, w), HeightMm = Math.Min(14, Math.Max(t.HeightMm - 2, 4)) },
            LabelExtraKind.Date or LabelExtraKind.Cell => new LabelCustomElement { WidthMm = Math.Min(16, w), HeightMm = 3.5 },
            _ => new LabelCustomElement { WidthMm = Math.Min(20, w), HeightMm = 4.5 },
        };
        e.Kind = kind;
        e.Enabled = true;
        if (kind == LabelExtraKind.FreeText)
            e.Text = "Текст";
        e.XMm = 2;
        e.YMm = Math.Max(0, t.HeightMm - e.HeightMm - 1);
        LabelDesignerMath.ClampInside(e, t);
        return e;
    }
}

/// <summary>Логотип как чёрно-белая сетка точек 203 dpi: рисуется теми же прямоугольниками, что штрихкод, — одинаково на GDI+, Avalonia и Skia.</summary>
public static class LabelLogo
{
    private static readonly ConcurrentDictionary<string, bool[,]?> Cache = new();

    /// <summary>Сетка [x, y] размером до wPx × hPx (картинка вписана с сохранением пропорций, по центру). null — файла нет или он не читается.</summary>
    public static bool[,]? Cells(string? path, int wPx, int hPx, out int offX, out int offY)
    {
        offX = offY = 0;
        if (string.IsNullOrWhiteSpace(path) || wPx < 4 || hPx < 4)
            return null;
        try
        {
            if (!File.Exists(path))
                return null;
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            var key = $"{path}|{stamp}|{wPx}x{hPx}";
            var cells = Cache.GetOrAdd(key, _ => Build(path, wPx, hPx));
            if (cells is null)
                return null;
            offX = (wPx - cells.GetLength(0)) / 2;
            offY = (hPx - cells.GetLength(1)) / 2;
            return cells;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label logo failed: {ex.GetType().Name}", "WARNING");
            return null;
        }
    }

    private static bool[,]? Build(string path, int wPx, int hPx)
    {
        using var src = SKBitmap.Decode(path);
        if (src is null || src.Width < 1 || src.Height < 1)
            return null;
        var k = Math.Min(wPx / (double)src.Width, hPx / (double)src.Height);
        var w = Math.Max(1, (int)Math.Round(src.Width * k));
        var h = Math.Max(1, (int)Math.Round(src.Height * k));
        using var scaled = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.Medium);
        if (scaled is null)
            return null;
        var cells = new bool[w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = scaled.GetPixel(x, y);
                cells[x, y] = c.Alpha > 128 && SkiaRaster.Luminance(c) < 140;
            }
        return cells;
    }
}
