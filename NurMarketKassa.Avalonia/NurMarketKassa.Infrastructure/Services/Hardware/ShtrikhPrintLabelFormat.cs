using System;
using System.Collections.Generic;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-09-28: формат этикетки весов ШТРИХ-ПРИНТ — координаты и шрифты элементов (команды
/// A0h..A7h), символы валют (C1h/C2h) и курс валюты (2Bh). Просьба владельца: на этикетке
/// под ценой напечатано «ЦЕНА, РУБ/КГ», а магазин в Кыргызстане — «добавь редактор валюты и
/// редактор штрих чека (этикетки) в штрих м».
///
/// Источники (всё строго по ним, ничего не угадано):
/// • «Протокол весов Штрих-Принт v1.6 rel 1», стр. 43–51 (A0h..A7h, C1h, C2h) и
///   Приложение 3 «Элементы этикетки» / Приложение 4 «Символы валют для экрана»;
/// • «Драйвер ШТРИХ-ПРИНТ 1.11», Приложение 5 «Графика и формат этикетки»;
/// • байты сверены с драйвером Штрих-М DrvLP на UDP-эмуляторе (2026-09-28):
///   – каждое «Положение (2 байта)» = X, затем Y (мм); Y = 0 — элемент не печатается;
///   – «Положение и высота ШК (3 байта)» = X, Y, Y + высота − 1 (так шлёт драйвер: Y=30,
///     высота 20 → 05 1E 31; Y=10, высота 0 → 05 0A 09);
///   – рамка: (левый, верхний), затем (правый, нижний);
///   – ВНИМАНИЕ: в последних трёх полях A0h/A1h драйвер шлёт «код товара» РАНЬШЕ «номера ПЛУ»,
///     хотя в тексте протокола порядок обратный. Здесь — как у драйвера (он работает с
///     настоящими весами); на живых весах это стоит проверить первым делом, если эти два
///     элемента вообще используются (по умолчанию они выключены);
///   – курс (2Bh, «4 байта, дробное») — число одинарной точности IEEE-754, little-endian:
///     12,34 → A4 70 45 41, 87,5 → 00 00 AF 42;
///   – символ валюты для печати (C2h): номер, размер = 48 (30h), 48 байт данных;
///     для экрана (C1h): номер, размер = 7, 7 байт.
///
/// Надпись «ЦЕНА, РУБ/КГ» (как и «МАССА», «СТОИМОСТЬ», «УПАКОВАНО», «ГОДЕН ДО») — неизменяемый
/// элемент прошивки: переписать её текст протокол не позволяет («Руководство администратора
/// 4.5», стр. 64–65; «Редактор этикеток»: «для фиксированных надписей редактирование надписи
/// запрещено»). Можно только скрыть её (Y = 0) в пользовательском формате 10..14 и поставить
/// на её место пользовательский текст 1..5 (99h/9Ah) со своим словом.
/// </summary>
public static class ShtrikhLabelFormat
{
    public const byte CmdGetLabelParams = 0xA0;
    public const byte CmdSetLabelParams = 0xA1;
    public const byte CmdGetLabelParamsEx = 0xA2;
    public const byte CmdSetLabelParamsEx = 0xA3;
    public const byte CmdGetFonts = 0xA4;
    public const byte CmdSetFonts = 0xA5;
    public const byte CmdGetAvailableFonts = 0xA6;
    public const byte CmdGetStringLengths = 0xA7;
    public const byte CmdLoadDisplaySymbol = 0xC1;
    public const byte CmdLoadPrintSymbol = 0xC2;
    public const byte CmdSetCurrencyRate = 0x2B;

    /// <summary>Длины полезных данных: ответ A0h / тело A1h после номера формата.</summary>
    public const int MainLength = 54;
    public const int ExLength = 40;
    public const int FontsLength = 39;
    public const int StringLengthsLength = 31;

    /// <summary>Пользовательские форматы «Формат 1..5» — только их можно записывать (A1h/A3h/A5h
    /// «диапазон: 10..14»). Форматы 0..9 — стандартные, только чтение.</summary>
    public const int FirstUserFormat = 10;
    public const int LastUserFormat = 14;
    public const int MaxFormat = 14;

    /// <summary>Зона печати — 54 мм, X от 0 до 53; Y от 1 до 120 (Приложение 3).</summary>
    public const int PrintWidthMm = 54;
    public const int MaxX = 53;
    public const int MaxY = 120;
    public const int DotsPerMm = 8;

    /// <summary>Символ валюты для печати: 12×24 точки, строка дополняется справа до 16 бит →
    /// 2 байта × 24 = 48 байт (Приложение 3). Для экрана: 5×7, строка дополняется СЛЕВА до
    /// 8 бит, биты — от младшего к старшему → 7 байт (Приложение 4).</summary>
    public const int PrintSymbolWidth = 12;
    public const int PrintSymbolHeight = 24;
    public const int PrintSymbolSize = 48;
    public const int DisplaySymbolWidth = 5;
    public const int DisplaySymbolHeight = 7;
    public const int DisplaySymbolSize = 7;

    public static bool IsUserFormat(int format) => format is >= FirstUserFormat and <= LastUserFormat;

    /// <summary>Таблица шрифтов (Приложение 3): ширина и высота символа в точках с учётом
    /// коэффициентов масштабирования.</summary>
    public static (int Width, int Height) FontCell(int font) => font switch
    {
        0 => (8, 8),
        1 => (8, 24),
        2 => (8, 16),
        3 => (8, 32),
        4 => (16, 64),
        5 => (12, 24),
        6 => (12, 48),
        _ => (8, 16),
    };

    public static IReadOnlyList<ShtrikhLabelElement> Elements => ElementList;

    // Порядок и смещения — по A0h/A2h/A4h/A7h (см. шапку). Pos — смещение пары X,Y в буфере
    // A0h (Block 0) или A2h (Block 1); Font — индекс в A4h; Len — индекс в A7h (−1 — длина
    // известна заранее, FixedChars); Kind — как рисовать в предпросмотре.
    private static readonly ShtrikhLabelElement[] ElementList =
    {
        new("GoodsName", 0, 1, 0, -1, 28, ShtrikhElementKind.Text),
        new("ShopName", 0, 3, 1, -1, 28, ShtrikhElementKind.Text),
        new("Date", 0, 5, 2, 0, 8, ShtrikhElementKind.Value),
        new("Time", 0, 7, 3, 1, 5, ShtrikhElementKind.Value),
        new("ExpiryDate", 0, 9, 4, 2, 8, ShtrikhElementKind.Value),
        new("Weight", 0, 11, 5, 3, 6, ShtrikhElementKind.Value),
        new("Tare", 0, 13, 6, 4, 6, ShtrikhElementKind.Value),
        new("Price", 0, 15, 7, 5, 8, ShtrikhElementKind.Value),
        new("LabelNumber", 0, 17, 8, 6, 4, ShtrikhElementKind.Value),
        new("ScaleNumber", 0, 19, 9, 7, 2, ShtrikhElementKind.Value),
        new("GroupCode", 0, 21, 10, 8, 2, ShtrikhElementKind.Value),
        new("Message", 0, 23, 11, -1, 50, ShtrikhElementKind.Text),
        new("Cost", 0, 25, 12, 9, 8, ShtrikhElementKind.Value),
        new("Barcode", 0, 27, -1, -1, 0, ShtrikhElementKind.Barcode),
        new("InscrPacked", 0, 30, 13, 10, 9, ShtrikhElementKind.Inscription),
        new("InscrBestBefore", 0, 32, 14, 11, 8, ShtrikhElementKind.Inscription),
        new("InscrWeight", 0, 34, 15, 12, 5, ShtrikhElementKind.Inscription),
        new("InscrPrice", 0, 36, 16, 13, 12, ShtrikhElementKind.Inscription),
        new("InscrCost", 0, 38, 17, 14, 9, ShtrikhElementKind.Inscription),
        new("Picture1", 0, 40, -1, -1, 0, ShtrikhElementKind.Picture),
        new("Picture2", 0, 42, -1, -1, 0, ShtrikhElementKind.Picture),
        new("Frame", 0, 44, -1, -1, 0, ShtrikhElementKind.Frame),
        new("ItemCode", 0, 48, 19, 16, 6, ShtrikhElementKind.Value),
        new("PluNumber", 0, 50, 18, 15, 4, ShtrikhElementKind.Value),
        new("SumCount", 0, 52, 20, 17, 3, ShtrikhElementKind.Value),
        new("Picture3", 1, 0, -1, -1, 0, ShtrikhElementKind.Picture),
        new("Picture4", 1, 2, -1, -1, 0, ShtrikhElementKind.Picture),
        new("ShelfLifeDays", 1, 4, 21, 18, 3, ShtrikhElementKind.Value),
        new("ManufactureDate", 1, 6, 22, 19, 8, ShtrikhElementKind.Value),
        new("Gross", 1, 8, 23, 20, 6, ShtrikhElementKind.Value),
        new("NetCalc", 1, 10, 24, 21, 6, ShtrikhElementKind.Value),
        new("CurrencyEquiv", 1, 12, 25, 22, 8, ShtrikhElementKind.Value),
        new("InscrShelfLife", 1, 14, 26, 23, 13, ShtrikhElementKind.Inscription),
        new("InscrManufactured", 1, 16, 27, 24, 11, ShtrikhElementKind.Inscription),
        new("InscrGross", 1, 18, 28, 25, 12, ShtrikhElementKind.Inscription),
        new("Text1", 1, 20, 29, -1, 30, ShtrikhElementKind.UserText),
        new("Text2", 1, 22, 30, -1, 30, ShtrikhElementKind.UserText),
        new("Text3", 1, 24, 31, -1, 30, ShtrikhElementKind.UserText),
        new("Text4", 1, 26, 32, -1, 30, ShtrikhElementKind.UserText),
        new("Text5", 1, 28, 33, -1, 30, ShtrikhElementKind.UserText),
    };

    public static ShtrikhLabelElement Element(string key)
    {
        foreach (var e in ElementList)
        {
            if (e.Key == key)
                return e;
        }
        throw new ArgumentException($"Нет элемента этикетки «{key}».", nameof(key));
    }

    /// <summary>Элемент «Пользовательский текст N» (N = 1..5).</summary>
    public static ShtrikhLabelElement UserText(int number) => Element("Text" + Math.Clamp(number, 1, 5));

    /// <summary>Символ 12×24 для печати → 48 байт: построчно сверху вниз, каждая строка
    /// слева направо, 12 точек + 4 белые справа, биты от старшего к младшему, чёрная точка = 1.</summary>
    public static byte[] EncodePrintSymbol(bool[,] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var data = new byte[PrintSymbolSize];
        for (var row = 0; row < PrintSymbolHeight; row++)
        {
            for (var col = 0; col < PrintSymbolWidth; col++)
            {
                if (row < pixels.GetLength(0) && col < pixels.GetLength(1) && pixels[row, col])
                    data[row * 2 + col / 8] |= (byte)(0x80 >> (col % 8));
            }
        }
        return data;
    }

    /// <summary>Символ 5×7 для дисплея весов → 7 байт: строка из 5 точек дополняется СЛЕВА
    /// тремя белыми до 8, строка читается слева направо, биты заполняются от младшего к
    /// старшему (Приложение 4 протокола, Приложение 5 драйвера). То есть крайняя левая
    /// дополнительная точка — бит 0, первая точка символа — бит 3, последняя — бит 7.
    /// На живом дисплее это надо проверить в меню весов «(1.1.5.1) Символ основной валюты»:
    /// если символ окажется зеркальным, порядок битов в прошивке обратный.</summary>
    public static byte[] EncodeDisplaySymbol(bool[,] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var data = new byte[DisplaySymbolSize];
        const int pad = 8 - DisplaySymbolWidth;
        for (var row = 0; row < DisplaySymbolHeight; row++)
        {
            for (var col = 0; col < DisplaySymbolWidth; col++)
            {
                if (row < pixels.GetLength(0) && col < pixels.GetLength(1) && pixels[row, col])
                    data[row] |= (byte)(1 << (pad + col));
            }
        }
        return data;
    }

    /// <summary>Курс валюты для 2Bh — IEEE-754 single, little-endian (сверено с драйвером).</summary>
    public static byte[] EncodeRate(decimal rate)
    {
        var value = (float)Math.Clamp(rate, 0m, 999999m);
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return bytes;
    }

    public static decimal DecodeRate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            return 0m;
        var copy = bytes[..4].ToArray();
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(copy);
        var value = BitConverter.ToSingle(copy, 0);
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0)
            return 0m;
        return Math.Round((decimal)value, 2);
    }
}

public enum ShtrikhElementKind { Text, Value, Inscription, UserText, Barcode, Picture, Frame }

/// <summary>Описание элемента этикетки: где лежат его координаты, шрифт и длина строки.</summary>
public sealed record ShtrikhLabelElement(string Key, int Block, int Pos, int Font, int Len, int FixedChars, ShtrikhElementKind Kind)
{
    public bool HasFont => Font >= 0;
    public bool IsGraphic => Kind is ShtrikhElementKind.Barcode or ShtrikhElementKind.Picture or ShtrikhElementKind.Frame;
}

/// <summary>
/// Формат этикетки, как он лежит в весах: сырые буферы A0h (54 байта), A2h (40) и A4h (39).
/// Храним байты целиком, чтобы резервные поля и всё, что касса не редактирует, ушли в весы
/// ровно такими, какими пришли. Поверх — типизированный доступ к координатам и шрифтам.
/// </summary>
public sealed class ShtrikhLabelLayout
{
    public ShtrikhLabelLayout(int format, byte[] main, byte[]? ex, byte[]? fonts)
    {
        Format = format;
        Main = Fit(main, ShtrikhLabelFormat.MainLength);
        Ex = Fit(ex, ShtrikhLabelFormat.ExLength);
        Fonts = Fit(fonts, ShtrikhLabelFormat.FontsLength);
        HasEx = ex is { Length: > 0 };
        HasFonts = fonts is { Length: > 0 };
    }

    /// <summary>Номер формата 0..14. У копии можно сменить номер на пользовательский.</summary>
    public int Format { get; set; }
    public byte[] Main { get; }
    public byte[] Ex { get; }
    public byte[] Fonts { get; }

    /// <summary>Весы отдали доп. параметры/шрифты (у прошивок с протоколом ниже 1.4 их нет —
    /// тогда и записывать их нельзя).</summary>
    public bool HasEx { get; }
    public bool HasFonts { get; }

    private static byte[] Fit(byte[]? source, int length)
    {
        var buffer = new byte[length];
        if (source is not null)
            Array.Copy(source, buffer, Math.Min(length, source.Length));
        return buffer;
    }

    public ShtrikhLabelLayout Clone(int? format = null) =>
        new(format ?? Format, (byte[])Main.Clone(), HasEx ? (byte[])Ex.Clone() : null, HasFonts ? (byte[])Fonts.Clone() : null);

    public int PaperLength
    {
        get => Main[0];
        set => Main[0] = (byte)Math.Clamp(value, 0, ShtrikhLabelFormat.MaxY);
    }

    private byte[] BlockOf(ShtrikhLabelElement e) => e.Block == 0 ? Main : Ex;

    public (int X, int Y) GetPosition(ShtrikhLabelElement e)
    {
        var b = BlockOf(e);
        return (b[e.Pos], b[e.Pos + 1]);
    }

    public void SetPosition(ShtrikhLabelElement e, int x, int y)
    {
        var b = BlockOf(e);
        var oldX = b[e.Pos];
        var oldY = b[e.Pos + 1];
        var height = e.Kind == ShtrikhElementKind.Barcode ? BarcodeHeight : 0;
        b[e.Pos] = (byte)Math.Clamp(x, 0, 255);
        b[e.Pos + 1] = (byte)Math.Clamp(y, 0, 255);
        if (e.Kind == ShtrikhElementKind.Barcode && oldY != b[e.Pos + 1])
            BarcodeHeight = height; // третий байт ШК хранит низ (Y + высота − 1) — пересчитываем
        if (e.Kind == ShtrikhElementKind.Frame && oldY > 0 && b[e.Pos + 1] > 0)
        {
            // Рамку двигаем целиком: правый нижний угол смещается вместе с левым верхним.
            Main[46] = (byte)Math.Clamp(Main[46] + (b[e.Pos] - oldX), 0, 255);
            Main[47] = (byte)Math.Clamp(Main[47] + (b[e.Pos + 1] - oldY), 0, 255);
        }
    }

    public bool IsVisible(ShtrikhLabelElement e) => GetPosition(e).Y > 0;

    /// <summary>Высота тела ШК, мм: третий байт = Y + высота − 1 (так кодирует драйвер).</summary>
    public int BarcodeHeight
    {
        get => (Main[29] - Main[28] + 1) & 0xFF;
        set => Main[29] = (byte)((Main[28] + Math.Clamp(value, 0, 120) - 1) & 0xFF);
    }

    /// <summary>Рамка: левый верхний и правый нижний углы, мм.</summary>
    public (int Left, int Top, int Right, int Bottom) Frame
    {
        get => (Main[44], Main[45], Main[46], Main[47]);
        set
        {
            Main[44] = (byte)Math.Clamp(value.Left, 0, 255);
            Main[45] = (byte)Math.Clamp(value.Top, 0, 255);
            Main[46] = (byte)Math.Clamp(value.Right, 0, 255);
            Main[47] = (byte)Math.Clamp(value.Bottom, 0, 255);
        }
    }

    /// <summary>«Признак печати проверочной линии ШК» — байт 30 доп. параметров.</summary>
    public bool BarcodeTestLine
    {
        get => Ex[30] != 0;
        set => Ex[30] = (byte)(value ? 1 : 0);
    }

    public int GetFont(ShtrikhLabelElement e) => e.HasFont ? Fonts[e.Font] : -1;

    public void SetFont(ShtrikhLabelElement e, int font)
    {
        if (e.HasFont)
            Fonts[e.Font] = (byte)Math.Clamp(font, 0, 6);
    }

    /// <summary>Тело A1h после пароля: номер формата + 54 байта.</summary>
    public byte[] EncodeMainWrite() => Prefix(Main);

    public byte[] EncodeExWrite() => Prefix(Ex);

    public byte[] EncodeFontsWrite() => Prefix(Fonts);

    private byte[] Prefix(byte[] body)
    {
        if (!ShtrikhLabelFormat.IsUserFormat(Format))
            throw new InvalidOperationException($"Формат {Format} — стандартный, весы разрешают записывать только форматы 10..14 («Формат 1..5»).");
        var result = new byte[1 + body.Length];
        result[0] = (byte)Format;
        body.CopyTo(result, 1);
        return result;
    }

    /// <summary>Ширина и высота элемента, мм (для предпросмотра и проверки наложений).
    /// Текст — число знаков × ширина символа шрифта (8 точек на мм); ШК — 26 мм плюс цифры
    /// шрифтом 5 (Приложение 3); рисунки — их размер с весов не читаем, показываем 10×10.</summary>
    public (double Width, double Height) SizeOf(ShtrikhLabelElement e, IReadOnlyList<byte>? lengths, int nameLines, int messageLines)
    {
        const double dpm = ShtrikhLabelFormat.DotsPerMm;
        switch (e.Kind)
        {
            case ShtrikhElementKind.Barcode:
                return (208 / dpm, Math.Max(1, BarcodeHeight) + 24 / dpm);
            case ShtrikhElementKind.Picture:
                return (10, 10);
            case ShtrikhElementKind.Frame:
                var f = Frame;
                return (Math.Max(0, f.Right - f.Left), Math.Max(0, f.Bottom - f.Top));
        }

        var chars = e.FixedChars;
        if (e.Len >= 0 && lengths is not null && e.Len < lengths.Count && lengths[e.Len] > 0)
            chars = lengths[e.Len];
        var lines = e.Key switch
        {
            "GoodsName" => Math.Max(1, nameLines),
            "ShopName" => 2,
            "Message" => Math.Max(1, messageLines),
            _ => 1,
        };
        var (w, h) = ShtrikhLabelFormat.FontCell(e.HasFont ? GetFont(e) : 2);
        return (chars * w / dpm, lines * h / dpm);
    }
}
