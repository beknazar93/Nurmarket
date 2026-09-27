using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace NurMarketKassa.Services;

public enum LabelPrintResult { Success, PrinterNotFound, Failed }

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
    string? StoreName = null);

/// <summary>
/// Генерирует изображение этикетки (штрих-код + название товара + цена) по шаблону
/// <see cref="LabelTemplate"/> и печатает его на выбранном Windows-принтере.
/// </summary>
public static class BarcodeLabelService
{
    internal const int Dpi = 203; // стандартное разрешение термопринтеров этикеток.

    public static Bitmap GenerateLabelBitmap(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null) =>
        GenerateLabelBitmap(productName, barcode, priceText, template, sku, unit, storeName, Dpi);

    /// <summary>То же, но в разрешении конкретного принтера (2026-09-28): раньше этикетка всегда
    /// рисовалась в 203 dpi и потом растягивалась драйвером до 300/600 dpi — штрихи получали
    /// разную ширину. Раскладка по-прежнему считается в точках 203 dpi (масштаб через
    /// ScaleTransform), поэтому вид этикетки не меняется, а штрих-код рисуется точно по точкам
    /// устройства.</summary>
    internal static Bitmap GenerateLabelBitmap(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku, string? unit, string? storeName, int dpi)
    {
        var widthPx = Math.Max((int)(template.WidthMm / 25.4 * dpi), 10);
        var heightPx = Math.Max((int)(template.HeightMm / 25.4 * dpi), 10);

        var label = new Bitmap(widthPx, heightPx);
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

        return label;
    }

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
            codeTextHeight, Math.Clamp(codeTextHeight * 0.72f, 6f, 14f));
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
    internal static void DrawLinearBlock(Graphics g, RectangleF rect, RectangleF labelRect, string? rawCode,
        LabelBarcodeFormat format, int quietModules, float digitsHeight, float digitsFontSize)
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
    internal static void DrawOnPrinterPage(Graphics g, Bitmap bmp, double xMm, double yMm, int renderDpi)
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
            var x = (int)Math.Round(xMm / 25.4 * g.DpiX);
            var y = (int)Math.Round(yMm / 25.4 * g.DpiY);
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

    private static void DrawTextElement(Graphics g, LabelElementLayout box, string text, FontStyle style, string fontFamily, double manualFontSizePx = 0)
    {
        var rect = ToPxRect(box);
        if (!box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
            return;

        // manualFontSizePx > 0 — пользователь задал точный размер в редакторе этикетки вместо
        // старого автоподбора по высоте блока; FitFontToWidth ниже всё равно подрежет его, если
        // текст не помещается по ширине — переполнения/обрезки текста быть не должно.
        var maxFontSize = manualFontSizePx > 0
            ? Math.Clamp((float)manualFontSizePx, 6f, 96f)
            : Math.Clamp(rect.Height * 0.72f, 6f, 40f);
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
        WinUsbPrinterPort.IsWinUsbDevicePath(printerName) ||
        HardwarePortHelper.LooksLikeComPort(printerName) ||
        HardwarePortHelper.LooksLikeLptPort(printerName) ||
        printerName.StartsWith(@"\\.\", StringComparison.Ordinal);

    public static LabelPrintResult Print(LabelPrintRequest request)
    {
        if (IsRawDevicePath(request.PrinterName))
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
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            // Рисуем в разрешении самого принтера (203/300/600 dpi) и кладём точка-в-точку.
            if (label is null)
            {
                labelDpi = RenderDpiFor(g.DpiX);
                label = GenerateLabelBitmap(
                    request.ProductName, request.Barcode, request.PriceText, request.Template,
                    request.Sku, request.Unit, request.StoreName, labelDpi);
            }
            DrawOnPrinterPage(g, label, 0, 0, labelDpi);
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
        try
        {
            using var label = GenerateLabelBitmap(
                request.ProductName, request.Barcode, request.PriceText, request.Template,
                request.Sku, request.Unit, request.StoreName);
            var raster = BitmapToEscPosRaster(label);
            var devicePath = UsbRawPrinterPort.IsRawPortLabel(request.PrinterName)
                ? UsbRawPrinterPort.ToDevicePath(request.PrinterName)
                : request.PrinterName;
            var copies = Math.Clamp(request.Copies, 1, 99);

            for (var i = 0; i < copies; i++)
                PrinterPortService.SendRawBytes(devicePath, raster);

            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Raw device label print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>Encodes a monochrome ESC/POS "GS v 0" raster bit-image command (m=0, no scaling).</summary>
    internal static byte[] BitmapToEscPosRaster(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bytesPerRow = (width + 7) / 8;
        var imageData = new byte[bytesPerRow * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luminance = pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114;
                if (luminance >= 128)
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
