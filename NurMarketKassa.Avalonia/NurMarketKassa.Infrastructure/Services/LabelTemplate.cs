namespace NurMarketKassa.Services;

/// <summary>Ручной выбор формата штрих-кода в редакторе этикеток. <see cref="Auto"/> — старое
/// поведение по умолчанию: формат определяется автоматически по длине кода
/// (см. BarcodeLabelService.DetectFormat).</summary>
public enum LabelBarcodeFormat { Auto = 0, Ean13 = 1, Code128 = 2, QrCode = 3 }

/// <summary>Позиция и размер одного элемента этикетки, в миллиметрах от левого верхнего угла.</summary>
public sealed class LabelElementLayout
{
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }

    /// <summary>Необязательные элементы (артикул/единица/магазин) по умолчанию выключены —
    /// показываются на этикетке и в редакторе только когда пользователь включит их.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Переопределение шрифта для этого конкретного элемента (2026-09-06, панель
    /// свойств по выбранному элементу). null — наследовать общий LabelTemplate.FontFamily,
    /// как было раньше единственным способом задать шрифт.</summary>
    public string? FontFamily { get; set; }

    /// <summary>null — наследовать общий LabelTemplate.FontSizePx.</summary>
    public double? FontSizePx { get; set; }
}

/// <summary>Редактируемый пользователем шаблон этикетки: размер этикетки и раскладка элементов.</summary>
public sealed class LabelTemplate
{
    public double WidthMm { get; set; } = 40;
    public double HeightMm { get; set; } = 30;

    /// <summary>Шрифт всех текстовых элементов этикетки (штрих-код печатается моноширинным
    /// Consolas отдельно от этого выбора — см. BarcodeLabelService.DrawBarcodeElement).</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Ручной размер шрифта в пикселях для всех текстовых элементов, кроме цифр под
    /// штрих-кодом (2026-09-05, по просьбе пользователя: "добавить возможность менять шрифт по
    /// пикселям"). 0 — старое поведение по умолчанию: размер подбирается автоматически под
    /// высоту блока (см. BarcodeLabelService.DrawTextElement). Ширина блока по-прежнему
    /// подрезает слишком крупный ручной размер — текст никогда не обрезается вместо уменьшения.</summary>
    public double FontSizePx { get; set; }

    public LabelElementLayout Barcode { get; set; } = new() { XMm = 2, YMm = 1, WidthMm = 36, HeightMm = 16 };
    public LabelElementLayout ProductName { get; set; } = new() { XMm = 2, YMm = 18, WidthMm = 36, HeightMm = 6 };
    public LabelElementLayout Price { get; set; } = new() { XMm = 2, YMm = 24.5, WidthMm = 36, HeightMm = 5 };

    // Необязательные элементы — выключены по умолчанию, чтобы не менять вид уже
    // существующих у пользователей шаблонов после обновления.
    public LabelElementLayout Sku { get; set; } = new() { XMm = 2, YMm = 0, WidthMm = 17, HeightMm = 3, Enabled = false };
    public LabelElementLayout Unit { get; set; } = new() { XMm = 21, YMm = 0, WidthMm = 17, HeightMm = 3, Enabled = false };
    public LabelElementLayout StoreName { get; set; } = new() { XMm = 2, YMm = 0, WidthMm = 36, HeightMm = 3, Enabled = false };

    /// <summary>Ручной выбор типа кодирования штрих-кода (2026-09-06). Auto воспроизводит
    /// прежнее поведение — формат подбирается по длине кода.</summary>
    public LabelBarcodeFormat BarcodeFormat { get; set; } = LabelBarcodeFormat.Auto;

    /// <summary>Строка цифр под штрих-кодом — раньше рисовалась всегда, теперь опциональна.</summary>
    public bool BarcodeShowDigits { get; set; } = true;

    /// <summary>"Плотность" штрих-кода — поле (quiet zone) в EncodingOptions.Margin у ZXing.
    /// Раньше было захардкожено значением 2.</summary>
    public int BarcodeMargin { get; set; } = 2;

    /// <summary>Валюта, подставляемая вместо суффикса во входящей строке цены. Раньше цена
    /// печаталась как есть, без возможности сменить валюту в самом шаблоне этикетки.</summary>
    public string PriceCurrencyText { get; set; } = "сом";

    /// <summary>Обрезать ".00"/",00" в конце цены.</summary>
    public bool PriceHideDecimals { get; set; }

    /// <summary>Если задано — печатается вместо реального артикула товара, переданного при
    /// генерации этикетки.</summary>
    public string? SkuCustomText { get; set; }

    public static LabelTemplate CreateDefault() => new();

    public LabelTemplate Clone() => new()
    {
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        FontFamily = FontFamily,
        FontSizePx = FontSizePx,
        Barcode = CloneLayout(Barcode),
        ProductName = CloneLayout(ProductName),
        Price = CloneLayout(Price),
        Sku = CloneLayout(Sku),
        Unit = CloneLayout(Unit),
        StoreName = CloneLayout(StoreName),
        BarcodeFormat = BarcodeFormat,
        BarcodeShowDigits = BarcodeShowDigits,
        BarcodeMargin = BarcodeMargin,
        PriceCurrencyText = PriceCurrencyText,
        PriceHideDecimals = PriceHideDecimals,
        SkuCustomText = SkuCustomText,
    };

    private static LabelElementLayout CloneLayout(LabelElementLayout source) => new()
    {
        XMm = source.XMm,
        YMm = source.YMm,
        WidthMm = source.WidthMm,
        HeightMm = source.HeightMm,
        Enabled = source.Enabled,
        FontFamily = source.FontFamily,
        FontSizePx = source.FontSizePx,
    };
}
