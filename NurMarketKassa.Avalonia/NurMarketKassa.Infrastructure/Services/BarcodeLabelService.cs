using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>PartiallySent (2026-11-02, финальная приёмка 1.17.73): часть этикеток задания отправлена, исход остального неизвестен — автоповтора нет,
/// сколько ушло — в <see cref="BarcodeLabelService.LastPartial"/>.</summary>
public enum LabelPrintResult { Success, PrinterNotFound, Failed, PartiallySent }

/// <summary>Сколько этикеток задания отправлено целиком (<paramref name="Sent"/>) из <paramref name="Total"/>.</summary>
/// <summary>2026-11-02 (аудит №2, С3-16): сколько этикеток задания ушло. Printer — принтер, JobId — идентификатор задания; запись неизменяема, хранится по принтеру
/// (<see cref="BarcodeLabelService.PartialFor"/>), поэтому две одновременные печати на разные принтеры (этикетки и ценники) не показывают чужое «N из M».</summary>
public sealed record LabelPartial(int Sent, int Total, string Printer = "", string JobId = "", DateTime AtUtc = default);

/// <summary>Сбой отправки этикеток по частям: <see cref="Sent"/> этикеток из <see cref="Total"/> ушли целиком, исход текущей части неизвестен.</summary>
internal sealed class LabelSendException : IOException
{
    public LabelSendException(int sent, int total, Exception inner) : base(inner.Message, inner)
    {
        Sent = sent;
        Total = total;
    }

    public int Sent { get; }
    public int Total { get; }
}

/// <summary>Параметры печати одной этикетки со штрих-кодом по заданному шаблону раскладки.</summary>
public sealed record LabelPrintRequest(
    string ProductName,
    string Barcode,
    string? PriceText,
    int Copies,
    string PrinterName,
    LabelTemplate Template,
    string? Sku = null,
    string? Unit = null,
    string? StoreName = null,
    string? VariantText = null,
    string? JobId = null);

/// <summary>2026-11-01 (П-7): необязательные команды TSPL. null в поле — команда не отправляется.</summary>
public sealed record TsplExtras(int? Density, int? Speed, bool? Flip, string? Sensor);

/// <summary>
/// Генерирует изображение этикетки (штрих-код + название товара + цена) по шаблону
/// <see cref="LabelTemplate"/> и печатает его на выбранном Windows-принтере.
/// </summary>
public static partial class BarcodeLabelService
{
    internal const int Dpi = 203; // стандартное разрешение термопринтеров этикеток.

    /// <param name="variantText">2026-10-06 (О-25): размер и цвет («44, Красный») — для этикетки размера одежды.</param>
    public static Bitmap GenerateLabelBitmap(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null, string? variantText = null) =>
        GenerateLabelBitmap(productName, barcode, priceText, template, sku, unit, storeName, Dpi, variantText);

    /// <summary>То же, но в разрешении конкретного принтера (2026-09-28): раньше этикетка всегда
    /// рисовалась в 203 dpi и потом растягивалась драйвером до 300/600 dpi — штрихи получали
    /// разную ширину. Раскладка по-прежнему считается в точках 203 dpi (масштаб через
    /// ScaleTransform), поэтому вид этикетки не меняется, а штрих-код рисуется точно по точкам
    /// устройства.</summary>
    internal static Bitmap GenerateLabelBitmap(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku, string? unit, string? storeName, int dpi, string? variantText = null)
    {
        // 2026-10-06 (О-25): строка «Размер и цвет» выключена в шаблоне — дописываем к названию, иначе размер не виден.
        var variantElement = template.Variant;
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is not { Enabled: true })
            productName = productName + " — " + variantText!.Trim();

        var widthPx = Math.Max((int)(template.WidthMm / 25.4 * dpi), 10);
        var heightPx = Math.Max((int)(template.HeightMm / 25.4 * dpi), 10);

        var label = new Bitmap(widthPx, heightPx);
        PinTextDpi(label);
        using var g = Graphics.FromImage(label);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        if (dpi != Dpi)
            g.ScaleTransform(dpi / (float)Dpi, dpi / (float)Dpi);

        DrawBarcodeElement(g, template, barcode);
        DrawTextElement(g, template.ProductName, productName, FontStyle.Regular, ResolveFontFamily(template.ProductName, template), ResolveFontSizePx(template.ProductName, template));
        if (!string.IsNullOrWhiteSpace(priceText))
            DrawTextElement(g, template.Price, FormatPriceText(priceText!, template), FontStyle.Bold, ResolveFontFamily(template.Price, template), ResolveFontSizePx(template.Price, template));
        var skuText = string.IsNullOrWhiteSpace(template.SkuCustomText) ? sku : template.SkuCustomText;
        if (!string.IsNullOrWhiteSpace(skuText))
            DrawTextElement(g, template.Sku, skuText!, FontStyle.Regular, ResolveFontFamily(template.Sku, template), ResolveFontSizePx(template.Sku, template));
        if (!string.IsNullOrWhiteSpace(unit))
            DrawTextElement(g, template.Unit, unit!, FontStyle.Regular, ResolveFontFamily(template.Unit, template), ResolveFontSizePx(template.Unit, template));
        if (!string.IsNullOrWhiteSpace(storeName))
            DrawTextElement(g, template.StoreName, storeName!, FontStyle.Regular, ResolveFontFamily(template.StoreName, template), ResolveFontSizePx(template.StoreName, template));
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is { Enabled: true })
            DrawTextElement(g, variantElement, variantText!.Trim(), FontStyle.Bold, ResolveFontFamily(variantElement, template), ResolveFontSizePx(variantElement, template));

        return label;
    }

    /// <summary>Разрешение картинки, по которому GDI+ переводит пункты шрифта в точки: 96 на дюйм, как в Skia (SkiaText.PtToPx) и предпросмотре Avalonia.</summary>
    internal const float TextDpi = 96f;

    /// <summary>2026-11-08 (жалоба «этикетки не подходят по размеру»): new Bitmap(w, h) берёт разрешение экрана; у программы, знающей о масштабе Windows
    /// (Avalonia), при 125 % / 150 % это 120 / 144 — шрифт в пунктах выходил в 1,25–1,5 раза крупнее (автоподбор «0,72 × высота блока» давал текст выше
    /// своего блока: название наезжало на цену, цена — за нижний край этикетки). Теперь всегда 96, как в Skia: размер текста не зависит от масштаба экрана.</summary>
    internal static void PinTextDpi(Bitmap bitmap) => bitmap.SetResolution(TextDpi, TextDpi);

    private static string ResolveFontFamily(LabelElementLayout element, LabelTemplate template) =>
        string.IsNullOrWhiteSpace(element.FontFamily)
            ? (string.IsNullOrWhiteSpace(template.FontFamily) ? "Arial" : template.FontFamily)
            : element.FontFamily;

    private static double ResolveFontSizePx(LabelElementLayout element, LabelTemplate template) =>
        element.FontSizePx ?? template.FontSizePx;

    /// <summary>Вычленяет ведущую числовую часть входящей строки цены (уже отформатированной
    /// вызывающим кодом, напр. "285 сом"), опционально обрезает копейки/тыйын и подставляет
    /// валюту, выбранную в шаблоне этикетки. Если строка не начинается с числа (неожиданный
    /// формат) — возвращается как есть, без исключений.</summary>
    internal static string FormatPriceText(string priceText, LabelTemplate template)
    {
        var match = Regex.Match(priceText, @"^[\d\s.,]+");
        if (!match.Success)
            return priceText;

        var numeric = match.Value.Trim();
        if (template.PriceHideDecimals)
            numeric = Regex.Replace(numeric, @"[.,]0+$", "");

        var currency = string.IsNullOrWhiteSpace(template.PriceCurrencyText) ? "" : " " + template.PriceCurrencyText;
        return numeric + currency;
    }

    private static void DrawBarcodeElement(Graphics g, LabelTemplate template, string code)
    {
        var box = template.Barcode;
        var rect = ToPxRect(box);
        if (!box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(code))
            return;

        if (template.BarcodeFormat == LabelBarcodeFormat.QrCode)
        {
            DrawQrBlock(g, rect, code, template.BarcodeMargin);
            return;
        }

        var codeTextHeight = template.BarcodeShowDigits ? Math.Min(rect.Height * 0.22f, 16f) : 0f;
        var labelRect = new RectangleF(0, 0, MmToPx(template.WidthMm), MmToPx(template.HeightMm));
        DrawLinearBlock(g, rect, labelRect, code, template.BarcodeFormat, template.BarcodeMargin,
            codeTextHeight, Math.Clamp(codeTextHeight * 0.72f, 6f, 14f), template.BarcodeModuleDots);
    }

    // ---------- Штрих-код: выбор символики и точная отрисовка (2026-09-28) ----------
    //
    // Баг «штрих-код на ценнике не читается/не печатается» — причины, найденные стендом
    // (scratchpad/pricetags): 1) EAN-13/EAN-8/UPC-A с неверной контрольной цифрой и любые
    // не-ASCII символы (кириллица) бросали исключение ZXing — падала вся печать, в массовой
    // печати на A4 из-за одного товара не печаталось ничего; 2) принудительный EAN-13 для
    // 12 цифр молча дописывал контрольную цифру — сканер читал ДРУГОЙ код, чем у товара;
    // 3) EAN-13 с ведущим нулём сканеры читают как 12-значный UPC-A — товар не находился;
    // 4) картинка штрих-кода вписывалась в блок с дробным смещением и билинейным сглаживанием,
    // а если не помещалась — сжималась (модуль < 1 точки): штрихи серые/неравные, на
    // термопечати не читаются. Теперь модуль — целое число точек устройства, без сглаживания.

    /// <summary>Как будет напечатан штрих-код: символика (null — линейный код невозможен),
    /// точное содержимое и предупреждение для окна печати/редактора.</summary>
    internal sealed record BarcodePlan(BarcodeFormat? Format, string Content, string? Warning);

    /// <summary>Правило выбора: сканер должен прочитать РОВНО тот код, что записан у товара
    /// (поиск товара при сканировании — точное совпадение). Что нельзя честно закодировать в
    /// EAN/UPC — печатается Code 128 (кодирует строку как есть), с предупреждением.</summary>
    internal static BarcodePlan PlanBarcode(string? rawCode, LabelBarcodeFormat requested)
    {
        var code = (rawCode ?? "").Trim();
        if (requested == LabelBarcodeFormat.QrCode)
            return new BarcodePlan(BarcodeFormat.QR_CODE, code, null);

        var digits = code.Length > 0 && code.All(char.IsAsciiDigit);
        if (requested != LabelBarcodeFormat.Code128 && digits)
        {
            if (code.Length == 13 && code[0] != '0' && HasValidGtinCheckDigit(code))
                return new BarcodePlan(BarcodeFormat.EAN_13, code, null);
            // 12 цифр с верной контрольной — UPC-A (те же штрихи, что EAN-13 с ведущим 0);
            // сканер отдаёт те же 12 цифр.
            if (code.Length == 12 && HasValidGtinCheckDigit(code))
                return new BarcodePlan(BarcodeFormat.UPC_A, code, null);
            if (requested == LabelBarcodeFormat.Auto && code.Length == 8 && HasValidGtinCheckDigit(code))
                return new BarcodePlan(BarcodeFormat.EAN_8, code, null);
        }

        // Code 128 кодирует только ASCII; кириллица/буквы с точками линейным кодом невозможны.
        if (!code.All(c => c >= ' ' && c <= '~'))
            return new BarcodePlan(null, code, Tr.T(
                $"В коде «{code}» есть символы, которых не бывает в линейном штрих-коде (кириллица, буквы с точками и т. п.) — штрих-код не напечатан. Исправьте код товара (латиница и цифры) или выберите QR-код.",
                $"«{code}» кодунда сызыктуу штрих-коддо болбогон белгилер бар (кирилл тамгалары, чекиттүү тамгалар ж.б.) — штрих-код басылган жок. Товардын кодун оңдоңуз (латын тамгалары жана цифралар) же QR-кодду тандаңыз.",
                $"Code “{code}” contains characters a linear barcode cannot hold (Cyrillic, accented letters, etc.) — the barcode is not printed. Fix the product code (Latin letters and digits) or choose QR code.",
                $"“{code}” kodunda doğrusal barkodun taşıyamayacağı karakterler var (Kiril, noktalı harfler vb.) — barkod basılmadı. Ürün kodunu düzeltin (Latin harfleri ve rakamlar) veya QR kodu seçin.",
                $"«{code}» kodida chiziqli shtrix-kodga sig'maydigan belgilar bor (kirill, nuqtali harflar va h.k.) — shtrix-kod chop etilmadi. Mahsulot kodini tuzating (lotin harflari va raqamlar) yoki QR-kodni tanlang."));

        string? warning = null;
        if (requested != LabelBarcodeFormat.Code128 && digits && code.Length == 13 && code[0] == '0' && HasValidGtinCheckDigit(code))
            warning = Tr.T(
                $"EAN-13 с ведущим нулём сканеры читают как 12 цифр (UPC-A) — чтобы код «{code}» читался полностью, он напечатан как Code 128.",
                $"Нөл менен башталган EAN-13 кодун сканерлер 12 цифра (UPC-A) катары окушат — «{code}» толук окулушу үчүн Code 128 катары басылды.",
                $"Scanners read an EAN-13 with a leading zero as 12 digits (UPC-A) — so that “{code}” is read in full, it is printed as Code 128.",
                $"Tarayıcılar sıfırla başlayan EAN-13'ü 12 rakam (UPC-A) olarak okur — “{code}” tam okunsun diye Code 128 olarak basıldı.",
                $"Nol bilan boshlanadigan EAN-13 ni skanerlar 12 ta raqam (UPC-A) sifatida o'qiydi — «{code}» to'liq o'qilishi uchun Code 128 sifatida chop etildi.");
        else if (requested == LabelBarcodeFormat.Ean13)
            warning = Tr.T(
                $"Код «{code}» не является правильным EAN-13 (нужно 13 цифр с верной контрольной цифрой) — напечатан как Code 128, сканер прочитает его без изменений.",
                $"«{code}» коду туура EAN-13 эмес (туура текшерүү цифрасы менен 13 цифра керек) — Code 128 катары басылды, сканер аны өзгөртпөй окуйт.",
                $"Code “{code}” is not a valid EAN-13 (13 digits with a correct check digit are required) — printed as Code 128, the scanner will read it unchanged.",
                $"“{code}” kodu geçerli bir EAN-13 değil (doğru kontrol basamaklı 13 rakam gerekir) — Code 128 olarak basıldı, tarayıcı onu değiştirmeden okur.",
                $"«{code}» kodi to'g'ri EAN-13 emas (to'g'ri nazorat raqamli 13 ta raqam kerak) — Code 128 sifatida chop etildi, skaner uni o'zgartirmasdan o'qiydi.");
        else if (requested == LabelBarcodeFormat.Auto && digits && code.Length is 13 or 8)
            warning = Tr.T(
                $"У кода «{code}» неверная контрольная цифра EAN — напечатан как Code 128 (сканер прочитает ровно этот код). Проверьте, нет ли ошибки в штрих-коде товара.",
                $"«{code}» кодунун EAN текшерүү цифрасы туура эмес — Code 128 катары басылды (сканер так ушул кодду окуйт). Товардын штрих-кодунда ката жокпу, текшериңиз.",
                $"Code “{code}” has a wrong EAN check digit — printed as Code 128 (the scanner will read exactly this code). Check the product's barcode for a typo.",
                $"“{code}” kodunun EAN kontrol basamağı yanlış — Code 128 olarak basıldı (tarayıcı tam olarak bu kodu okur). Ürün barkodunda hata olup olmadığını kontrol edin.",
                $"«{code}» kodining EAN nazorat raqami noto'g'ri — Code 128 sifatida chop etildi (skaner aynan shu kodni o'qiydi). Mahsulot shtrix-kodida xato yo'qligini tekshiring.");
        return new BarcodePlan(BarcodeFormat.CODE_128, code, warning);
    }

    /// <summary>Контрольная цифра GTIN (EAN-8, UPC-A, EAN-13, GTIN-14): веса 3,1,3,1… справа налево.</summary>
    private static bool HasValidGtinCheckDigit(string digits)
    {
        var sum = 0;
        for (int i = digits.Length - 2, weight = 3; i >= 0; i--, weight = 4 - weight)
            sum += (digits[i] - '0') * weight;
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }

    /// <summary>Матрица модулей 1 модуль = 1 ячейка, без полей. null — закодировать нельзя
    /// (например, Code 128 длиннее 80 символов): печать не должна падать из-за штрих-кода.</summary>
    private static BitMatrix? EncodeModules(BarcodePlan plan)
    {
        if (plan.Format is not { } format || plan.Content.Length == 0)
            return null;
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        // По умолчанию ZXing кодирует QR в ISO-8859-1 — кириллица превращалась в «????».
        if (format == BarcodeFormat.QR_CODE && plan.Content.Any(c => c > 127))
            hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
        try
        {
            return new MultiFormatWriter().encode(plan.Content, format, 0, 0, hints);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Сколько точек устройства на модуль: целое, максимально возможное при полях
    /// <paramref name="quietModules"/> с каждой стороны внутри блока. Если не помещается даже
    /// по 1 точке — разрешаем выйти за блок в пределах этикетки (0 — не помещается вовсе).</summary>
    private static int FitLinearModule(int modules, int quietModules, float boxWidthPx, float labelWidthPx)
    {
        var m = (int)(boxWidthPx / (modules + 2 * Math.Max(quietModules, 0)));
        if (m >= 1)
            return m;
        return modules + 2 <= labelWidthPx ? 1 : 0;
    }

    /// <summary>Линейный штрих-код и (если <paramref name="digitsHeight"/> &gt; 0) цифры под ним.
    /// rect/labelRect — в мировых координатах g (точки 203 dpi, при печати масштабируются).</summary>
    /// <param name="maxModuleDots">2026-10-26: «Толщина штриха» — потолок ширины узкого штриха в точках 203 dpi (0 — без потолка).</param>
    internal static void DrawLinearBlock(Graphics g, RectangleF rect, RectangleF labelRect, string? rawCode,
        LabelBarcodeFormat format, int quietModules, float digitsHeight, float digitsFontSize, int maxModuleDots = 0)
    {
        var plan = PlanBarcode(rawCode, format);
        if (plan.Content.Length == 0)
            return;

        var barsHeight = digitsHeight > 0 ? Math.Max(rect.Height - digitsHeight - 2, 8f) : rect.Height;
        var matrix = EncodeModules(plan);
        if (matrix is not null)
        {
            var dev = ToDeviceRect(g, new RectangleF(rect.X, rect.Y, rect.Width, barsHeight));
            var devLabel = ToDeviceRect(g, labelRect);
            var m = FitLinearModule(matrix.Width, quietModules, dev.Width, devLabel.Width);
            // В 300/600 dpi модуль не должен стать физически тоньше, чем в предпросмотре 203 dpi
            // (по нему же считается предупреждение): иначе длинный код на A4 выходил 0,085 мм.
            // Если в 203 dpi код не помещается вовсе, не печатаем его и в 600 dpi — печать должна
            // совпадать с предпросмотром и предупреждением «штрих-код не напечатан».
            var scale = rect.Width > 0 ? dev.Width / rect.Width : 1f;
            if (m > 0 && scale > 1.01f)
            {
                var m203 = FitLinearModule(matrix.Width, quietModules, rect.Width, labelRect.Width);
                var target = (int)Math.Round(m203 * scale);
                if (m203 == 0)
                    m = 0;
                else if (target > m && matrix.Width * target + 2 <= devLabel.Width)
                    m = target;
            }
            // 2026-10-26: «Толщина штриха» — штрих не толще заданного (в 300/600 dpi — тот же физический размер).
            if (m > 0 && maxModuleDots > 0)
                m = Math.Max(1, Math.Min(m, (int)Math.Round(maxModuleDots * Math.Max(scale, 1f))));
            if (m > 0)
            {
                var widthPx = matrix.Width * m;
                var x0 = (int)Math.Round(dev.X + (dev.Width - widthPx) / 2f);
                // Вылет за блок (длинный код) — не дальше краёв этикетки.
                x0 = Math.Clamp(x0, (int)Math.Ceiling(devLabel.Left) + 1, Math.Max((int)Math.Ceiling(devLabel.Left) + 1, (int)devLabel.Right - 1 - widthPx));
                BlitModules(g, matrix, x0, (int)Math.Round(dev.Y), m, Math.Max((int)Math.Round(dev.Height), 1));
            }
        }

        if (digitsHeight <= 0)
            return;
        // Цифры — ровно то, что закодировано (без пробелов по краям); если штрихов нет
        // (кириллица/слишком длинный код), цифры всё равно печатаются для ручного ввода.
        using var codeFont = new Font("Consolas", digitsFontSize, FontStyle.Regular);
        var code = plan.Content;
        var codeSize = g.MeasureString(code, codeFont);
        var codeText = codeSize.Width <= rect.Width ? code : TruncateToWidth(g, code, codeFont, rect.Width);
        codeSize = g.MeasureString(codeText, codeFont);
        g.DrawString(codeText, codeFont, Brushes.Black,
            rect.X + Math.Max(0, (rect.Width - codeSize.Width) / 2f), rect.Y + barsHeight + 1);
    }

    /// <summary>QR-код в центре квадрата, вписанного в rect; модуль — целое число точек устройства.</summary>
    internal static void DrawQrBlock(Graphics g, RectangleF rect, string? rawCode, int quietModules)
    {
        var matrix = EncodeModules(PlanBarcode(rawCode, LabelBarcodeFormat.QrCode));
        if (matrix is null)
            return;
        var dev = ToDeviceRect(g, rect);
        var cells = matrix.Width + 2 * Math.Max(quietModules, 0);
        var m = (int)(Math.Min(dev.Width, dev.Height) / cells);
        // Как и у линейного кода: чего нет в предпросмотре 203 dpi, того нет и в печати.
        if (m < 1 || Math.Min(rect.Width, rect.Height) < cells)
            return;
        var size = matrix.Width * m;
        BlitModules(g, matrix,
            (int)Math.Round(dev.X + (dev.Width - size) / 2f),
            (int)Math.Round(dev.Y + (dev.Height - size) / 2f), m, size);
    }

    private static RectangleF ToDeviceRect(Graphics g, RectangleF r)
    {
        var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Bottom) };
        g.TransformPoints(System.Drawing.Drawing2D.CoordinateSpace.Device, System.Drawing.Drawing2D.CoordinateSpace.World, pts);
        return RectangleF.FromLTRB(Math.Min(pts[0].X, pts[1].X), Math.Min(pts[0].Y, pts[1].Y),
            Math.Max(pts[0].X, pts[1].X), Math.Max(pts[0].Y, pts[1].Y));
    }

    /// <summary>Рисует модули пиксель-в-пиксель: каждый модуль = m×m точек устройства (для 1D
    /// матрицы высотой 1 — полоса высотой heightPx), без сглаживания и дробных смещений.</summary>
    private static void BlitModules(Graphics g, BitMatrix matrix, int x0, int y0, int m, int heightPx)
    {
        var oneRow = matrix.Height == 1;
        var w = matrix.Width * m;
        var h = oneRow ? heightPx : matrix.Height * m;
        using var bars = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bars.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (var y = 0; y < h; y++)
            {
                if (y == 0 || !oneRow)
                {
                    var my = oneRow ? 0 : y / m;
                    for (var x = 0; x < w; x++)
                        row[x] = matrix[x / m, my] ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
            }
        }
        finally
        {
            bars.UnlockBits(data);
        }

        var state = g.Save();
        var unit = g.PageUnit;
        var pageScale = g.PageScale;
        try
        {
            g.ResetTransform();
            g.PageUnit = GraphicsUnit.Pixel;
            g.PageScale = 1f;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(bars, new Rectangle(x0, y0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
        }
        finally
        {
            g.Restore(state);
            g.PageUnit = unit;
            g.PageScale = pageScale;
        }
    }

    /// <summary>
    /// Понятное предупреждение для окна печати/редактора этикетки — null, если штрих-код
    /// напечатается и прочитается как есть. Проверяется худший случай — термопринтер 203 dpi.
    /// </summary>
    public static string? DescribeBarcodeProblem(string? code, LabelTemplate template) =>
        !template.Barcode.Enabled
            ? null
            : DescribeBarcodeProblem(code, template.BarcodeFormat, template.Barcode.WidthMm, template.Barcode.HeightMm,
                template.WidthMm, template.BarcodeMargin);

    internal static string? DescribeBarcodeProblem(string? rawCode, LabelBarcodeFormat format,
        double boxWidthMm, double boxHeightMm, double labelWidthMm, int quietModules)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return null; // «нет штрих-кода» диалоги сообщают сами
        var plan = PlanBarcode(rawCode, format);
        if (plan.Format is null)
            return plan.Warning;

        var code = plan.Content;
        var tooLong = Tr.T(
            $"Код «{code}» слишком длинный для этикетки шириной {labelWidthMm:0.#} мм — штрих-код не напечатан. Увеличьте ширину этикетки или сократите код.",
            $"«{code}» коду {labelWidthMm:0.#} мм кеңдиктеги этикетка үчүн өтө узун — штрих-код басылган жок. Этикетканын кеңдигин чоңойтуңуз же кодду кыскартыңыз.",
            $"Code “{code}” is too long for a {labelWidthMm:0.#} mm wide label — the barcode is not printed. Make the label wider or shorten the code.",
            $"“{code}” kodu {labelWidthMm:0.#} mm genişliğindeki etiket için çok uzun — barkod basılmadı. Etiketi genişletin veya kodu kısaltın.",
            $"«{code}» kodi {labelWidthMm:0.#} mm kenglikdagi yorliq uchun juda uzun — shtrix-kod chop etilmadi. Yorliqni kengaytiring yoki kodni qisqartiring.");
        var matrix = EncodeModules(plan);
        if (matrix is null)
            return Join(plan.Warning, tooLong);

        if (plan.Format == BarcodeFormat.QR_CODE)
        {
            var qm = (int)(Math.Min(MmToPx(boxWidthMm), MmToPx(boxHeightMm)) / (matrix.Width + 2 * Math.Max(quietModules, 0)));
            return qm >= 2 ? null : Tr.T(
                $"QR-код «{code}» не помещается в свой блок или получается слишком мелким — увеличьте блок QR-кода.",
                $"«{code}» QR-коду өз блогуна батпайт же өтө майда чыгат — QR-код блогун чоңойтуңуз.",
                $"QR code “{code}” does not fit its box or comes out too small — enlarge the QR code box.",
                $"“{code}” QR kodu alanına sığmıyor veya çok küçük çıkıyor — QR kod alanını büyütün.",
                $"«{code}» QR-kodi o'z blokiga sig'maydi yoki juda mayda chiqadi — QR-kod blokini kattalashtiring.");
        }

        var m = FitLinearModule(matrix.Width, quietModules, MmToPx(boxWidthMm), MmToPx(labelWidthMm));
        if (m == 0)
            return Join(plan.Warning, tooLong);
        if (m == 1)
            return Join(plan.Warning, Tr.T(
                $"Штрих-код «{code}» получается очень плотным (штрих 0,125 мм) — на термопринтере 203 dpi сканер может его не прочитать. Увеличьте ширину блока штрих-кода или сократите код.",
                $"«{code}» штрих-коду өтө тыгыз чыгат (сызыгы 0,125 мм) — 203 dpi термопринтеринде сканер аны окубай калышы мүмкүн. Штрих-код блогун кеңейтиңиз же кодду кыскартыңыз.",
                $"Barcode “{code}” comes out very dense (0.125 mm bars) — a scanner may fail to read it from a 203 dpi thermal printer. Widen the barcode box or shorten the code.",
                $"“{code}” barkodu çok sık çıkıyor (0,125 mm çizgi) — 203 dpi termal yazıcıda tarayıcı okuyamayabilir. Barkod alanını genişletin veya kodu kısaltın.",
                $"«{code}» shtrix-kodi juda zich chiqadi (chiziq 0,125 mm) — 203 dpi termoprinterda skaner uni o'qiy olmasligi mumkin. Shtrix-kod blokini kengaytiring yoki kodni qisqartiring."));
        return plan.Warning;
    }

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : a + " " + b;

    /// <summary>В каком разрешении рисовать этикетку для принтера с разрешением
    /// <paramref name="deviceDpi"/>: в его собственном (1 точка картинки = 1 точка принтера),
    /// но не выше 600 dpi — для 1200 dpi ровно половина, чтобы масштаб оставался целым.</summary>
    internal static int RenderDpiFor(float deviceDpi)
    {
        var dpi = (int)Math.Round(deviceDpi);
        if (dpi < 100)
            return Dpi;
        return dpi / (int)Math.Ceiling(dpi / 600.0);
    }

    /// <summary>Кладёт готовую картинку этикетки на страницу принтера точка-в-точку.
    /// Раньше DrawImage(bmp, 0, 0, bmp.Width, bmp.Height) трактовал пиксели как единицы
    /// страницы принтера (1/100 дюйма) — этикетка 40 мм печаталась шириной 81 мм и
    /// обрезалась по краю ленты, на A4 (600 dpi) ценник выходил в 6 раз крупнее.</summary>
    /// <param name="hardMarginX">2026-11-08: непечатное поле принтера слева, сотые доли дюйма (<see cref="PageSettings.HardMarginX"/>). При
    /// OriginAtMargins = false начало координат страницы — угол ПЕЧАТНОЙ области, а не бумаги: без вычета все этикетки сдвигались на это поле
    /// (на офисном принтере ~4 мм вправо и вниз — сетка не попадала в вырубку самоклеящегося листа A4, нижний ряд уходил за край). Так же
    /// делает ClientDocPrint; на Linux (PDF через CUPS) координаты и так от края бумаги.</param>
    internal static void DrawOnPrinterPage(Graphics g, Bitmap bmp, double xMm, double yMm, int renderDpi, float hardMarginX = 0, float hardMarginY = 0)
    {
        var state = g.Save();
        var unit = g.PageUnit;
        var pageScale = g.PageScale;
        try
        {
            g.PageUnit = GraphicsUnit.Pixel; // точки принтера
            g.PageScale = 1f;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            var x = (int)Math.Round((xMm / 25.4 - Math.Max(hardMarginX, 0) / 100.0) * g.DpiX);
            var y = (int)Math.Round((yMm / 25.4 - Math.Max(hardMarginY, 0) / 100.0) * g.DpiY);
            var w = (int)Math.Round(bmp.Width * (double)g.DpiX / renderDpi);
            var h = (int)Math.Round(bmp.Height * (double)g.DpiY / renderDpi);
            g.DrawImage(bmp, new Rectangle(x, y, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
        }
        finally
        {
            g.Restore(state);
            g.PageUnit = unit;
            g.PageScale = pageScale;
        }
    }

    /// <summary>2026-11-08: непечатные поля принтера (сотые доли дюйма) для <see cref="DrawOnPrinterPage"/>; не прочитались — 0, как раньше.</summary>
    internal static (float X, float Y) HardMargins(PrintPageEventArgs e)
    {
        try
        {
            return (e.PageSettings.HardMarginX, e.PageSettings.HardMarginY);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer hard margins not read: {ex.GetType().Name}", "WARNING");
            return (0, 0);
        }
    }

    private static void DrawTextElement(Graphics g, LabelElementLayout box, string text, FontStyle style, string fontFamily, double manualFontSizePx = 0)
    {
        var rect = ToPxRect(box);
        if (!box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
            return;

        // 2026-10-26: ручной размер шрифта больше не ограничен (был потолок 96 и подгонка под ширину блока) —
        // владелец сам решает, насколько крупным будет текст. Строка переносится по ширине блока, по высоте
        // и ширине выходит за блок без обрезки (край этикетки — естественная граница).
        if (manualFontSizePx > 0)
        {
            using var manualFont = new Font(fontFamily, Math.Max((float)manualFontSizePx, 1f), style);
            using var format = new StringFormat(StringFormatFlags.NoClip)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            // 2026-10-27 (приёмка Opus 26.10, Д-10): крупный шрифт (120 px) рисовался поверх штрихкода и цены и мог сделать штрихкод нечитаемым.
            // Теперь текст обрезается по прямоугольнику своего блока; в редакторе — жёлтая строка «Текст не помещается в блок» (HasTextOverflow).
            var clipState = g.Save();
            g.SetClip(rect, System.Drawing.Drawing2D.CombineMode.Intersect);
            g.DrawString(text, manualFont, Brushes.Black, rect, format);
            g.Restore(clipState);
            return;
        }

        // Автоподбор: размер по высоте блока, FitFontToWidth уменьшает его, если текст не помещается по ширине.
        var maxFontSize = Math.Clamp(rect.Height * 0.72f, 6f, 40f);
        using var font = FitFontToWidth(g, text, style, maxFontSize, rect.Width, fontFamily);
        var displayText = text;
        var size = g.MeasureString(displayText, font);
        if (size.Width > rect.Width)
        {
            displayText = TruncateToWidth(g, displayText, font, rect.Width);
            size = g.MeasureString(displayText, font);
        }
        g.DrawString(displayText, font, Brushes.Black,
            rect.X + Math.Max(0, (rect.Width - size.Width) / 2f),
            rect.Y + Math.Max(0, (rect.Height - size.Height) / 2f));
    }

    /// <summary>2026-10-27 (приёмка 26.10, Д-10): есть ли блок, где текст с РУЧНЫМ размером шрифта не помещается в свой прямоугольник (по высоте —
    /// с переносом по ширине блока — или по ширине одного слова). Для жёлтой строки в редакторе этикетки; рисование не меняет.</summary>
    public static bool HasTextOverflow(string productName, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null, string? variantText = null)
    {
        // 2026-10-29: Android/Linux — System.Drawing недоступен; жёлтая строка «текст не помещается» там не показывалась.
        // 2026-11-06 (В18): теперь проверка идёт по размерам текста Skia (перенос по словам, высота строк) — строка видна и там.
        if (!OperatingSystem.IsWindows())
            return HasTextOverflowSkia(productName, priceText, template, sku, unit, storeName, variantText);
        using var probe = new Bitmap(1, 1);
        PinTextDpi(probe); // 2026-11-08: мерить в том же разрешении, в котором рисуется этикетка
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        bool Over(LabelElementLayout box, string? text, FontStyle style)
        {
            var size = ResolveFontSizePx(box, template);
            var rect = ToPxRect(box);
            if (size <= 0 || !box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
                return false;
            using var font = new Font(ResolveFontFamily(box, template), Math.Max((float)size, 1f), style);
            var measured = g.MeasureString(text, font, (int)Math.Ceiling(rect.Width));
            return measured.Height > rect.Height + 1f || measured.Width > rect.Width + 1f;
        }

        var variantElement = template.Variant;
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is not { Enabled: true })
            productName = productName + " — " + variantText!.Trim();
        var skuText = string.IsNullOrWhiteSpace(template.SkuCustomText) ? sku : template.SkuCustomText;
        return Over(template.ProductName, productName, FontStyle.Regular)
               || (!string.IsNullOrWhiteSpace(priceText) && Over(template.Price, FormatPriceText(priceText!, template), FontStyle.Bold))
               || Over(template.Sku, skuText, FontStyle.Regular)
               || Over(template.Unit, unit, FontStyle.Regular)
               || Over(template.StoreName, storeName, FontStyle.Regular)
               || (!string.IsNullOrWhiteSpace(variantText) && variantElement is { Enabled: true } && Over(variantElement, variantText!.Trim(), FontStyle.Bold));
    }

    /// <summary>
    /// Уменьшает кегль шрифта (от <paramref name="startSize"/> до минимума 6pt), пока строка
    /// не впишется по ширине бокса — иначе широкая, но невысокая цена/название всегда
    /// обрезались бы многоточием даже когда достаточно уменьшить шрифт.
    /// </summary>
    internal static Font FitFontToWidth(Graphics g, string text, FontStyle style, float startSize, float maxWidthPx, string fontFamily = "Arial")
    {
        const float minFontSize = 6f;
        for (var size = startSize; size > minFontSize; size -= 1f)
        {
            var font = new Font(fontFamily, size, style);
            if (g.MeasureString(text, font).Width <= maxWidthPx)
                return font;
            font.Dispose();
        }
        return new Font(fontFamily, minFontSize, style);
    }

    private static RectangleF ToPxRect(LabelElementLayout box) => new(
        MmToPx(box.XMm), MmToPx(box.YMm), MmToPx(box.WidthMm), MmToPx(box.HeightMm));

    internal static string TruncateToWidth(Graphics g, string text, Font font, float maxWidthPx)
    {
        if (g.MeasureString(text, font).Width <= maxWidthPx)
            return text;

        var truncated = text;
        while (truncated.Length > 1 && g.MeasureString(truncated + "…", font).Width > maxWidthPx)
            truncated = truncated[..^1];
        return truncated + "…";
    }

    /// <summary>Готовая картинка кода нужного размера — сейчас только для динамического QR оплаты
    /// (DynamicPaymentQrService). Ценники и этикетки рисуют штрих-код через DrawLinearBlock /
    /// DrawQrBlock: там модуль выравнивается по точкам принтера и нет исключений на «плохих» кодах.</summary>
    internal static Bitmap RenderBarcode(string code, int widthPx, int heightPx, LabelBarcodeFormat format, int margin)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = ResolveFormat(code, format),
            Options = new EncodingOptions
            {
                Width = Math.Max(widthPx, 50),
                Height = Math.Max(heightPx, 30),
                Margin = margin,
                PureBarcode = true,
            }
        };
        var pixelData = writer.Write(code);
        return ToBitmap(pixelData);
    }

    private static BarcodeFormat ResolveFormat(string code, LabelBarcodeFormat format) => format switch
    {
        LabelBarcodeFormat.Ean13 => BarcodeFormat.EAN_13,
        LabelBarcodeFormat.Code128 => BarcodeFormat.CODE_128,
        LabelBarcodeFormat.QrCode => BarcodeFormat.QR_CODE,
        _ => DetectFormat(code),
    };

    private static BarcodeFormat DetectFormat(string code)
    {
        var digitsOnly = code.Length > 0 && code.All(char.IsDigit);
        return (digitsOnly, code.Length) switch
        {
            (true, 13) => BarcodeFormat.EAN_13,
            (true, 8) => BarcodeFormat.EAN_8,
            (true, 12) => BarcodeFormat.UPC_A,
            _ => BarcodeFormat.CODE_128,
        };
    }

    internal static float MmToPx(double mm) => (float)(mm / 25.4 * Dpi);

    internal static Bitmap ToBitmap(PixelData pixelData)
    {
        var bitmap = new Bitmap(pixelData.Width, pixelData.Height, PixelFormat.Format32bppRgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, pixelData.Width, pixelData.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
        return bitmap;
    }

    /// <summary>
    /// Все варианты подключения принтера этикеток: спулер Windows, WinUSB-устройства
    /// (Zadig), "сырые" USB-порты без драйвера, LPT и COM — тот же полный список, что и
    /// у чекового принтера (<see cref="PrinterDiscoveryService"/>), вместо прежнего
    /// урезанного набора «только спулер + driverless USB».
    /// </summary>
    public static IReadOnlyList<DiscoveredPrinter> GetAvailablePrinters() =>
        PrinterDiscoveryService.Discover();

    /// <summary>Путь устройства не является именем очереди спулера Windows — печать
    /// должна идти в обход GDI, напрямую через PrinterPortService (WinUSB/LPT/COM/raw-USB).</summary>
    public static bool IsRawDevicePath(string printerName) =>
        UsbRawPrinterPort.IsRawPortLabel(printerName) ||
        NetworkPrinterAddress.IsNetworkPort(printerName) || // 2026-11-01: сетевой принтер этикеток (LAN, TCP 9100) — как прямой порт, язык LabelRawLanguage
        WinUsbPrinterPort.IsWinUsbDevicePath(printerName) ||
        HardwarePortHelper.LooksLikeComPort(printerName) ||
        HardwarePortHelper.LooksLikeLptPort(printerName) ||
        printerName.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        // 2026-11-13 (мастер «Подключить принтер»): USB напрямую через интерфейс «\\?\usb#vid_…» и очередь «NurCRM …» на встроенном «Generic / Text Only»
        // (картинку драйвер «Text Only» не печатает — этикетка уходит командами TSPL/ESC/POS в очередь как RAW).
        printerName.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        PrinterSetup.PrinterQueueInstaller.IsRawTextOnlyQueue(printerName);

    // 2026-11-01: своя очередь на каждый принтер этикеток (LabelPrinterSelector.RunQueued) — не общая с чеками.
    public static LabelPrintResult Print(LabelPrintRequest request) =>
        LabelPrinterSelector.RunQueued(request.PrinterName, () => PrintUnqueued(request));

    private static LabelPrintResult PrintUnqueued(LabelPrintRequest request)
    {
        // 2026-11-06 (В18, А4-08): на Linux и Android нет ни спулера Windows, ни System.Drawing — этикетка рисуется Skia и уходит принтеру командами
        // (CUPS «raw», /dev/usb/lp, TCP 9100, USB/Bluetooth Android) через PrinterPortService, как на Windows для прямого подключения.
        if (IsRawDevicePath(request.PrinterName) || !OperatingSystem.IsWindows())
            return PrintToRawDevice(request);

        try
        {
            if (!RawPrinterHelper.GetInstalledPrinterNames()
                    .Any(p => string.Equals(p, request.PrinterName, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"Label print: printer not found '{request.PrinterName}'", "WARNING");
                return LabelPrintResult.PrinterNotFound;
            }

            using var doc = CreateSpoolerDocument(request);
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>Документ печати этикетки через спулер Windows. Вынесен отдельно, чтобы
    /// проверочный стенд мог прогнать ровно этот путь печати в картинку (свой PrintController)
    /// без отправки на настоящий принтер.</summary>
    internal static PrintDocument CreateSpoolerDocument(LabelPrintRequest request)
    {
        var copiesRemaining = Math.Clamp(request.Copies, 1, 99);
        Bitmap? label = null;
        var labelDpi = 0;

        var doc = new PrintDocument();
        doc.Disposed += (_, _) => label?.Dispose();
        doc.PrinterSettings.PrinterName = request.PrinterName;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        ApplyLabelPageSize(doc, request.Template.WidthMm, request.Template.HeightMm);
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            // Рисуем в разрешении самого принтера (203/300/600 dpi) и кладём точка-в-точку.
            if (label is null)
            {
                labelDpi = RenderDpiFor(g.DpiX);
                label = GenerateLabelBitmap(
                    request.ProductName, request.Barcode, request.PriceText, request.Template,
                    request.Sku, request.Unit, request.StoreName, labelDpi, request.VariantText);
            }
            var (hx, hy) = HardMargins(e); // 2026-11-08: этикетка от края бумаги, а не от печатной области драйвера
            DrawOnPrinterPage(g, label, UserPreferences.Instance.LabelOffsetXMm, UserPreferences.Instance.LabelOffsetYMm, labelDpi, hx, hy);
            copiesRemaining--;
            e.HasMorePages = copiesRemaining > 0;
        };
        return doc;
    }

    /// <summary>
    /// No Windows print queue means no GDI print pipeline — the label bitmap is rasterized
    /// into an ESC/POS "GS v 0" bit-image command instead and written straight to the device
    /// via PrinterPortService, which already knows how to route a WinUSB address, a raw
    /// \\.\USBxxx port, LPT or COM (same retrying raw-device write receipts use).
    /// </summary>
    private static LabelPrintResult PrintToRawDevice(LabelPrintRequest request)
    {
        ClearPartial(request.PrinterName); // новое задание на этот принтер — прежний «N из M» устарел
        try
        {
            var devicePath = UsbRawPrinterPort.IsRawPortLabel(request.PrinterName)
                ? UsbRawPrinterPort.ToDevicePath(request.PrinterName)
                : request.PrinterName;
            var copies = Math.Clamp(request.Copies, 1, 99);
            if (OperatingSystem.IsWindows())
                SendRawLabelGdi(request, devicePath, copies);
            else
            {
                // 2026-11-06 (В18): растр — Skia по готовой сцене этикетки (203 dpi).
                using var skLabel = LabelSceneSkia.Render(BuildLabelScene(
                    request.ProductName, request.Barcode, request.PriceText, request.Template,
                    request.Sku, request.Unit, request.StoreName, request.VariantText));
                SendRawLabel(devicePath, skLabel, request.Template.WidthMm, request.Template.HeightMm, copies);
            }

            return LabelPrintResult.Success;
        }
        catch (LabelSendException ex)
        {
            ReportPartial(request.PrinterName, ex.Sent, ex.Total, request.JobId);
            PosLogger.Log($"Raw device label print interrupted: sent {ex.Sent} of {ex.Total} ({ex.InnerException?.GetType().Name}); no automatic retry.", "ERROR");
            return LabelPrintResult.PartiallySent;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Raw device label print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>Windows: растр этикетки GDI+ (как раньше) → задание принтеру. Отдельный метод, чтобы JIT не трогал System.Drawing на Linux/Android.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SendRawLabelGdi(LabelPrintRequest request, string devicePath, int copies)
    {
        using var label = GenerateLabelBitmap(
            request.ProductName, request.Barcode, request.PriceText, request.Template,
            request.Sku, request.Unit, request.StoreName, request.VariantText);
        SendRawLabel(devicePath, label, request.Template.WidthMm, request.Template.HeightMm, copies);
    }

    /// <summary>2026-10-01, «пропускает наклейки и печатает не на том месте»: печать через
    /// драйвер шла на бумагу размера из драйвера (часто 100×150 или 58×40), а не этикетки —
    /// принтер протягивал лишнее и проскакивал наклейку, а картинка съезжала. Теперь драйверу
    /// задаётся размер страницы ровно по этикетке (выключается в настройках этикетки).</summary>
    internal static void ApplyLabelPageSize(PrintDocument doc, double widthMm, double heightMm)
    {
        if (!UserPreferences.Instance.LabelPageFromTemplate || widthMm <= 0 || heightMm <= 0)
            return;
        try
        {
            // PaperSize — в сотых долях дюйма.
            var w = (int)Math.Round(widthMm / 25.4 * 100);
            var h = (int)Math.Round(heightMm / 25.4 * 100);
            doc.DefaultPageSettings.PaperSize = new PaperSize(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "NurMarket {0:0.#}x{1:0.#} mm", widthMm, heightMm), w, h);
            doc.DefaultPageSettings.Landscape = false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label page size not applied: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-01: прямая печать этикетки (без драйвера) на выбранном языке принтера
    /// (UserPreferences.LabelRawLanguage): escpos — как раньше; escpos_gap — после картинки
    /// GS FF (подача до начала следующей этикетки по зазору/метке); tspl — SIZE/GAP/BITMAP/PRINT,
    /// принтер сам находит зазор и печатает с начала наклейки.</summary>
    internal static void SendRawLabel(string devicePath, Bitmap label, double widthMm, double heightMm, int copies) =>
        SendRawLabelRaster(devicePath, BitmapToEscPosRaster(label), widthMm, heightMm, copies);

    /// <summary>2026-11-06 (В18): то же для растра Skia (Linux/Android — System.Drawing там нет).</summary>
    internal static void SendRawLabel(string devicePath, SKBitmap label, double widthMm, double heightMm, int copies) =>
        SendRawLabelRaster(devicePath, BitmapToEscPosRaster(label), widthMm, heightMm, copies);

    /// <param name="raster">Команда «GS v 0» (8 байт заголовка + строки) — результат <see cref="BitmapToEscPosRaster(Bitmap)"/> или <see cref="BitmapToEscPosRaster(SKBitmap)"/>.</param>
    internal static void SendRawLabelRaster(string devicePath, byte[] raster, double widthMm, double heightMm, int copies)
    {
        var prefs = UserPreferences.Instance;
        copies = Math.Clamp(copies, 1, 99);
        Func<int, byte[]> buildJob;
        if (prefs.LabelRawLanguage == "tspl")
        {
            // 2026-11-01 (П-7): плотность, скорость, поворот, датчик — только если заданы в настройках принтера этикеток; иначе задание прежнее.
            var extras = prefs.LabelTsplDensity is null && prefs.LabelTsplSpeed is null && prefs.LabelTsplFlip is null && prefs.LabelTsplSensor is null
                ? null
                : new TsplExtras(prefs.LabelTsplDensity, prefs.LabelTsplSpeed, prefs.LabelTsplFlip, prefs.LabelTsplSensor);
            buildJob = n => BuildTsplJob(raster, widthMm, heightMm, prefs.LabelGapMm, prefs.LabelOffsetXMm, prefs.LabelOffsetYMm, n, extras);
        }
        else
        {
            if (prefs.LabelRawLanguage == "escpos_gap")
            {
                var withFeed = new byte[raster.Length + 2];
                Buffer.BlockCopy(raster, 0, withFeed, 0, raster.Length);
                withFeed[^2] = 0x1D; // GS FF — подать до начала следующей этикетки
                withFeed[^1] = 0x0C;
                raster = withFeed;
            }
            var rasterCopy = raster;
            buildJob = n =>
            {
                var job = new byte[rasterCopy.Length * n];
                for (var i = 0; i < n; i++)
                    Buffer.BlockCopy(rasterCopy, 0, job, i * rasterCopy.Length, rasterCopy.Length);
                return job;
            };
        }

        // 2026-11-02 (финальная приёмка 1.17.73): копии уходят ЧАСТЯМИ по RawChunkLabels этикеток, по очереди, с учётом уже отправленного. Раньше все копии
        // шли одним заданием: потолок 60 с и повтор всего задания при сбое давали дубли. Теперь таймаут пропорционален размеру части (PrinterPortService.WriteTimeout),
        // повтор после сбоя касается только текущей части, а при неизвестном исходе (принтер не забирает данные, обрыв посреди записи) автоповтора нет —
        // LabelSendException: «Напечатано N из M, проверьте ленту».
        var sent = 0;
        while (sent < copies)
        {
            var n = Math.Min(RawChunkLabels, copies - sent);
            try
            {
                PrinterPortService.SendRawBytes(devicePath, buildJob(n));
            }
            catch (Exception ex) when (sent > 0 || ex is PrinterPortService.PrinterStalledException)
            {
                throw new LabelSendException(sent, copies, ex);
            }
            sent += n;
        }
    }

    /// <summary>Сколько этикеток уходит принтеру за одну отправку (остальные — следующими частями).</summary>
    public const int RawChunkLabels = 50;

    /// <summary>Последнее задание, у которого часть этикеток ушла, а исход остальных неизвестен (для сообщения «Напечатано N из M, проверьте ленту»).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LabelPartial> PartialByPrinter = new(StringComparer.OrdinalIgnoreCase);
    private static volatile LabelPartial? _lastPartial;

    /// <summary>Совместимость: последнее неполное задание по всем принтерам (запись неизменяемая — чтение и запись потокобезопасны). Присвоение null сбрасывает только это поле; точный ответ
    /// для конкретного принтера или задания — <see cref="PartialFor"/> / <see cref="PartialOfJob"/>.</summary>
    public static LabelPartial? LastPartial
    {
        get => _lastPartial;
        set => _lastPartial = value;
    }

    /// <summary>Записать неполное задание принтера (JobId пуст — создаётся новый).</summary>
    public static LabelPartial ReportPartial(string? printer, int sent, int total, string? jobId = null)
    {
        var p = new LabelPartial(sent, total, printer ?? "", string.IsNullOrEmpty(jobId) ? Guid.NewGuid().ToString("N") : jobId, DateTime.UtcNow);
        PartialByPrinter[p.Printer] = p;
        _lastPartial = p;
        return p;
    }

    /// <summary>Неполное задание ИМЕННО этого принтера; null — у него такого нет.</summary>
    public static LabelPartial? PartialFor(string? printer) => PartialByPrinter.TryGetValue(printer ?? "", out var p) ? p : null;

    /// <summary>Неполное задание по идентификатору задания.</summary>
    public static LabelPartial? PartialOfJob(string? jobId) =>
        string.IsNullOrEmpty(jobId) ? null : PartialByPrinter.Values.FirstOrDefault(p => p.JobId == jobId);

    public static void ClearPartial(string? printer) => PartialByPrinter.TryRemove(printer ?? "", out _);

    /// <summary>Задание TSPL (TSC, Xprinter, Gprinter, Rongta в режиме этикеток): размер, зазор,
    /// картинка BITMAP (в TSPL бит 0 — печать, поэтому байты инвертируются), копии.</summary>
    internal static byte[] BuildTsplJob(Bitmap label, double widthMm, double heightMm, double gapMm,
        double offsetXMm, double offsetYMm, int copies, TsplExtras? extras = null) =>
        BuildTsplJob(BitmapToEscPosRaster(label), widthMm, heightMm, gapMm, offsetXMm, offsetYMm, copies, extras);

    /// <summary>2026-11-06 (В18): то же для растра Skia.</summary>
    internal static byte[] BuildTsplJob(SKBitmap label, double widthMm, double heightMm, double gapMm,
        double offsetXMm, double offsetYMm, int copies, TsplExtras? extras = null) =>
        BuildTsplJob(BitmapToEscPosRaster(label), widthMm, heightMm, gapMm, offsetXMm, offsetYMm, copies, extras);

    internal static byte[] BuildTsplJob(byte[] raster, double widthMm, double heightMm, double gapMm,
        double offsetXMm, double offsetYMm, int copies, TsplExtras? extras = null)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // Заголовок GS v 0 — 8 байт: 1D 76 30 00 xL xH yL yH.
        var bytesPerRow = raster[4] | (raster[5] << 8);
        var height = raster[6] | (raster[7] << 8);
        var x = Math.Max(0, (int)Math.Round(offsetXMm / 25.4 * Dpi));
        var y = Math.Max(0, (int)Math.Round(offsetYMm / 25.4 * Dpi));
        using var ms = new MemoryStream();
        void W(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        W(string.Format(ci, "SIZE {0:0.##} mm,{1:0.##} mm\r\n", widthMm, heightMm));
        // 2026-11-01 (П-7): без extras задание побайтно такое же, как раньше.
        if (extras?.Sensor == "gap")
            W("SET GAP ON\r\n");
        if (extras?.Sensor == "bline")
            W(string.Format(ci, "BLINE {0:0.##} mm,0 mm\r\n", Math.Max(0, gapMm)));
        else
            W(string.Format(ci, "GAP {0:0.##} mm,0 mm\r\n", Math.Max(0, gapMm)));
        if (extras?.Density is { } density)
            W(string.Format(ci, "DENSITY {0}\r\n", Math.Clamp(density, 0, 15)));
        if (extras?.Speed is { } speed)
            W(string.Format(ci, "SPEED {0}\r\n", Math.Clamp(speed, 2, 6)));
        if (extras?.Flip is { } flip)
            W(flip ? "DIRECTION 1\r\n" : "DIRECTION 0\r\n");
        W("CLS\r\n");
        W(string.Format(ci, "BITMAP {0},{1},{2},{3},0,", x, y, bytesPerRow, height));
        for (var i = 8; i < raster.Length; i++)
            ms.WriteByte((byte)~raster[i]);
        W("\r\n");
        W(string.Format(ci, "PRINT 1,{0}\r\n", Math.Clamp(copies, 1, 99)));
        return ms.ToArray();
    }

    /// <summary>2026-11-01 (П-7): калибровка датчика принтера этикеток по TSPL — принтер протянет несколько этикеток и запомнит зазор/метку.
    /// Только для прямого подключения на языке TSPL; идёт по очереди принтера этикеток, чековый принтер не затрагивается.</summary>
    public static LabelPrintResult CalibrateSensor(string printerPath)
    {
        if (string.IsNullOrWhiteSpace(printerPath) || !IsRawDevicePath(printerPath))
            return LabelPrintResult.Failed;
        var prefs = UserPreferences.Instance;
        if (prefs.LabelRawLanguage != "tspl")
            return LabelPrintResult.Failed;
        var template = LabelTemplateStore.Load();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var devicePath = UsbRawPrinterPort.IsRawPortLabel(printerPath) ? UsbRawPrinterPort.ToDevicePath(printerPath) : printerPath;
        var command = string.Format(ci, "SIZE {0:0.##} mm,{1:0.##} mm\r\n", template.WidthMm, template.HeightMm)
            + (prefs.LabelTsplSensor == "bline"
                ? string.Format(ci, "BLINE {0:0.##} mm,0 mm\r\nBLINEDETECT\r\n", Math.Max(0, prefs.LabelGapMm))
                : string.Format(ci, "GAP {0:0.##} mm,0 mm\r\nGAPDETECT\r\n", Math.Max(0, prefs.LabelGapMm)));
        return LabelPrinterSelector.RunQueued(printerPath, () =>
        {
            try
            {
                PrinterPortService.SendRawBytes(devicePath, System.Text.Encoding.ASCII.GetBytes(command));
                PosLogger.Log("Принтер этикеток: отправлена калибровка датчика.", "PRINTER");
                return LabelPrintResult.Success;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Label sensor calibration failed: {ex.GetType().Name}", "ERROR");
                return LabelPrintResult.Failed;
            }
        });
    }

    /// <summary>Encodes a monochrome ESC/POS "GS v 0" raster bit-image command (m=0, no scaling).</summary>
    internal static byte[] BitmapToEscPosRaster(Bitmap bitmap) =>
        EncodeGsV0(bitmap.Width, bitmap.Height, (x, y) =>
        {
            var pixel = bitmap.GetPixel(x, y);
            return pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114 < 128;
        });

    /// <summary>2026-11-06 (В18): то же для растра Skia — тот же порог яркости (&lt; 128 — чёрная точка), поэтому одинаковая картинка даёт те же байты.</summary>
    internal static byte[] BitmapToEscPosRaster(SKBitmap bitmap) =>
        EncodeGsV0(bitmap.Width, bitmap.Height, (x, y) => SkiaRaster.Luminance(bitmap.GetPixel(x, y)) < 128);

    private static byte[] EncodeGsV0(int width, int height, Func<int, int, bool> isBlack)
    {
        var bytesPerRow = (width + 7) / 8;
        var imageData = new byte[bytesPerRow * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!isBlack(x, y))
                    continue;

                var byteIndex = y * bytesPerRow + x / 8;
                var bitIndex = 7 - x % 8;
                imageData[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x1D); // GS
        ms.WriteByte(0x76); // v
        ms.WriteByte(0x30); // 0
        ms.WriteByte(0x00); // m = 0 (normal size)
        ms.WriteByte((byte)(bytesPerRow & 0xFF));
        ms.WriteByte((byte)((bytesPerRow >> 8) & 0xFF));
        ms.WriteByte((byte)(height & 0xFF));
        ms.WriteByte((byte)((height >> 8) & 0xFF));
        ms.Write(imageData, 0, imageData.Length);
        return ms.ToArray();
    }
}
