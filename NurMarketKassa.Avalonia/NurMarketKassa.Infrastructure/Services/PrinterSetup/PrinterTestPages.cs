using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>Вариант кодировки кириллицы для пробной печати: номер на ленте, кодировка кассы (UserPreferences.ReceiptEncoding) и таблица ESC t.</summary>
public sealed record EncodingVariant(int Number, string UserEncoding, int TableByte, string Title);

/// <summary>2026-11-13 (мастер «Подключить принтер»): пробная печать. Чек — оба варианта кириллицы на одной ленте (CP866 / таблица 17 и CP1251 / таблица 46,
/// как у Xprinter, Epson и большинства ESC/POS), кассир выбирает строку, которая читается. Линейка ширины показывает, сколько знаков влезает (58 мм — 32, 80 мм — 48,
/// у XP-80 часто 42). Этикетка — команды TSPL встроенными шрифтами принтера (текст латиницей и штрихкод Code 128), без картинки: если вышло — принтер понимает TSPL.</summary>
public static class PrinterTestPages
{
    public static readonly IReadOnlyList<EncodingVariant> EncodingVariants =
    [
        new(1, "cp866", 17, "CP866"),
        new(2, "wpc1251", 46, "CP1251"),
    ];

    /// <summary>Текст проверки кириллицы (буквы, на которых видно путаницу таблиц: Ё, Ж, Щ, Ы, Я и строчные).</summary>
    public const string CyrillicSample = "Проверка печати: АБВГДЕЁЖЗИЙ ЩЫЭЮЯ абвгдеёжз щыэюя 0123456789";

    /// <summary>Ширина линейки по выбору ленты: 58 мм — 32 знака, 80 мм — 48, «80 мм узкая» (XP-80) — 42.</summary>
    public static int RulerChars(int paperMm, bool narrow80) =>
        paperMm >= ReceiptPaperProfile.Paper80mm ? (narrow80 ? 42 : 48) : 32;

    /// <summary>Линейка «1234567890123…» нужной длины.</summary>
    public static string Ruler(int chars)
    {
        var sb = new StringBuilder(chars);
        for (var i = 1; i <= chars; i++)
            sb.Append((char)('0' + i % 10));
        return sb.ToString();
    }

    /// <summary>Пробный чек ESC/POS: заголовок, оба варианта кириллицы, линейка ширины, затем (по выбору) импульс денежного ящика и отрез.</summary>
    public static byte[] BuildReceiptTest(int paperMm, bool narrow80, bool cut, bool openDrawer, int drawerPin)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // CP866/CP1251 (повторная регистрация безопасна)
        var ascii = Encoding.ASCII;
        using var s = new MemoryStream();
        EscPosCommands.WriteInitialize(s);
        EscPosCommands.WriteCancelKanjiMode(s);
        s.Write([0x1B, 0x61, 0x01]); // по центру
        s.Write([0x1B, 0x45, 0x01]); // жирный
        s.Write(ascii.GetBytes("NurCRM - TEST"));
        EscPosCommands.WriteLineFeed(s);
        s.Write([0x1B, 0x45, 0x00]);
        s.Write([0x1B, 0x61, 0x00]); // влево
        EscPosCommands.WriteLineFeed(s);
        foreach (var v in EncodingVariants)
        {
            EscPosCommands.WriteCodePage(s, v.TableByte, null, noEscPct: false);
            var enc = ReceiptEncodingHelper.GetEncodingWithFallback(v.UserEncoding);
            s.Write(ascii.GetBytes($"{v.Number}) {v.Title}:"));
            EscPosCommands.WriteLineFeed(s);
            s.Write(enc.GetBytes(CyrillicSample));
            EscPosCommands.WriteLineFeed(s);
            EscPosCommands.WriteLineFeed(s);
        }
        s.Write(ascii.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0} mm:", paperMm)));
        EscPosCommands.WriteLineFeed(s);
        s.Write(ascii.GetBytes(Ruler(RulerChars(paperMm, narrow80))));
        EscPosCommands.WriteLineFeed(s);
        if (openDrawer)
            EscPosCommands.WriteOpenCashDrawer(s, drawerPin);
        if (cut)
            EscPosCommands.WriteFeedAndCut(s);
        else
            EscPosCommands.WriteFeedLines(s, 4);
        return s.ToArray();
    }

    /// <summary>Пробная этикетка TSPL (203 dpi, 8 точек на мм): размер, зазор, надпись встроенным шрифтом и штрихкод Code 128 по размеру этикетки.</summary>
    public static byte[] BuildTsplTestLabel(double widthMm, double heightMm, double gapMm, string barcode = "4600000000017")
    {
        var ci = CultureInfo.InvariantCulture;
        widthMm = Math.Clamp(widthMm, 15, 120);
        heightMm = Math.Clamp(heightMm, 10, 200);
        var wDots = (int)Math.Round(widthMm * 8);
        var hDots = (int)Math.Round(heightMm * 8);
        var narrow = widthMm >= 40 ? 2 : 1;
        var barHeight = Math.Clamp(hDots - 96, 32, 120);
        var sb = new StringBuilder();
        sb.AppendFormat(ci, "SIZE {0:0.##} mm,{1:0.##} mm\r\n", widthMm, heightMm);
        sb.AppendFormat(ci, "GAP {0:0.##} mm,0 mm\r\n", Math.Max(0, gapMm));
        sb.Append("DIRECTION 1\r\n");
        sb.Append("CLS\r\n");
        sb.AppendFormat(ci, "TEXT 16,16,\"{0}\",0,1,1,\"NurCRM TEST\"\r\n", widthMm >= 40 ? "3" : "2");
        sb.AppendFormat(ci, "BARCODE 16,{0},\"128\",{1},1,0,{2},{3},\"{4}\"\r\n", 56, barHeight, narrow, narrow * 2, barcode);
        sb.AppendFormat(ci, "BOX 4,4,{0},{1},2\r\n", Math.Max(8, wDots - 8), Math.Max(8, hDots - 8));
        sb.Append("PRINT 1,1\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Пробная «этикетка» для ESC/POS-принтера в режиме этикеток: короткий текст и подача до следующей наклейки (GS FF).</summary>
    public static byte[] BuildEscPosLabelTest()
    {
        using var s = new MemoryStream();
        EscPosCommands.WriteInitialize(s);
        s.Write(Encoding.ASCII.GetBytes("NurCRM - LABEL TEST"));
        EscPosCommands.WriteLineFeed(s);
        s.Write(Encoding.ASCII.GetBytes("4600000000017"));
        EscPosCommands.WriteLineFeed(s);
        s.Write([0x1D, 0x0C]);
        return s.ToArray();
    }

    /// <summary>Кодировка по номеру варианта, который кассир выбрал на ленте (1 или 2); неизвестный номер — null.</summary>
    public static EncodingVariant? VariantByNumber(int number) => EncodingVariants.FirstOrDefault(v => v.Number == number);

    /// <summary>Какой вариант соответствует текущей кодировке кассы (для выбора по умолчанию).</summary>
    public static EncodingVariant VariantForEncoding(string? userEncoding)
    {
        var dotnet = ReceiptEncodingHelper.ResolveDotNetEncodingName(userEncoding ?? "");
        return dotnet == "windows-1251" ? EncodingVariants[1] : EncodingVariants[0];
    }
}
