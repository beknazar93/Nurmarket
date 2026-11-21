using System.IO;
using System.Text;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public static class EscPosSelfCheckPrinter
{
    /// <summary>Самопроверка принтера (как print_printer_self_check_page в receipt_printer.py).</summary>
    public static void PrintSelfCheck(ReceiptPrinterSettings cfg)
    {
        var enc = (cfg.TextEncoding ?? "cp866").Trim().ToLowerInvariant();
        var ts = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
        var w = ReceiptLayout.CharWidth;
        var lpt = (cfg.DevicePath ?? "LPT1").Trim();
        var lines = new[]
        {
            new string('=', w),
            "  САМОПРОВЕРКА (ПРИЛОЖЕНИЕ)",
            "  NurMarketKassa (C#)",
            new string('=', w),
            $"Дата/время: {ts}",
            new string('-', w),
            "Модель (цель): Cashino EP-200",
            "Версия прошивки: см. отчёт",
            "  принтера (у держ. кнопки)",
            new string('-', w),
            "В отчёте принтера часто:",
            "Код.стр. по умол.: CP936 GBK",
            "Это нормально. Печать из",
            "кассы: ESC % 0 + ESC t 46 +",
            "WPC1251 (байты cp1251).",
            new string('-', w),
            $"Кодировка текста: {enc}",
            new string('-', w),
            $"LPT: {lpt}",
            new string('-', w),
            "Кириллица (тест):",
            "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ",
            "абвгдежзийклмнопрстуфхцчшщъыьэюя",
            new string('-', w),
            "Латиница:",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "abcdefghijklmnopqrstuvwxyz",
            new string('-', w),
            "Цифры: 0123456789",
            "Символы: !\"#$%&'()*+,-./:;<=>?",
            new string('-', w),
            "Штрихкод CODE39 (текстом):",
            "*123456*",
            new string('=', w),
            "Самопроверка завершена.",
            "",
        };
        EscPosTextReceiptPrinter.Print(cfg, string.Join("\n", lines));
    }
}

/// <summary>
/// Печать готового текста на ESC/POS через LPT/COM.
/// </summary>
public static class EscPosTextReceiptPrinter
{
    private static bool NoEscPct() =>
        Environment.GetEnvironmentVariable("DESKTOP_MARKET_RECEIPT_NO_ESC_PCT")?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static void Print(ReceiptPrinterSettings cfg, string text, int? charWidth = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ValidateSettings(cfg);

        var port = NormalizeDevicePath(HardwarePortHelper.NormalizeLptPort(cfg.DevicePath));
        var payload = BuildEscPosPayload(cfg, text, charWidth);

        PosLogger.Log($"EscPos Print: port={port}, encoding={cfg.TextEncoding}, bytes={payload.Length}", "PRINTER");
        PrinterPortService.SendRawBytes(port, payload, cfg.RetryCount);
    }

    /// <summary>2026-11-19 (eKassa, ТЗ Умара 18.11): фискальный чек ГНС — строки как есть (без регистра и переносов), строка «QR&lt;TAB&gt;ссылка» — QR по центру.</summary>
    public static void PrintFiscal(ReceiptPrinterSettings cfg, string text)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var port = NormalizeDevicePath(HardwarePortHelper.NormalizeLptPort(cfg.DevicePath));
        var payload = BuildFiscalPayload(cfg, text);
        PosLogger.Log($"EscPos Print (фискальный): port={port}, encoding={cfg.TextEncoding}, bytes={payload.Length}", "PRINTER");
        PrinterPortService.SendRawBytes(port, payload, cfg.RetryCount);
    }

    public static byte[] BuildFiscalPayload(ReceiptPrinterSettings cfg, string text)
    {
        var utf8 = IsUtf8Pos(cfg.TextEncoding);
        var encName = MapToDotNetEncoding(cfg.TextEncoding);
        Encoding encoding;
        try { encoding = utf8 ? new UTF8Encoding(false) : Encoding.GetEncoding(encName); }
        catch (ArgumentException) { encoding = Encoding.GetEncoding(866); }

        using var s = new MemoryStream();
        EscPosCommands.WriteInitialize(s);
        EscPosCommands.WriteCancelKanjiMode(s);
        if (!utf8)
            EscPosCommands.WriteCodePage(s, cfg.EscPosTableByte ?? DefaultEscPosTableByte(encName), cfg.EscRByte, NoEscPct());
        EscPosCommands.WriteDefaultLineSpacing(s);
        EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: 1);
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.StartsWith("QR\t", StringComparison.Ordinal))
            {
                WriteQr(s, raw[3..].Trim());
                continue;
            }
            WriteTextLine(s, encoding, raw);
        }
        EscPosCommands.WriteLineFeed(s);
        EscPosCommands.WriteFeedAndCut(s);
        return s.ToArray();
    }

    /// <summary>Родной QR принтера (GS ( k, модель 2, модуль 6 ≈ 30 мм на ленте 80 мм, коррекция L), по центру.</summary>
    private static void WriteQr(Stream s, string url)
    {
        var data = Encoding.ASCII.GetBytes(url);
        if (data.Length == 0 || data.Length > 700)
            return;
        s.Write(new byte[] { 0x1B, 0x61, 0x01 });
        s.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 });
        s.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x06 });
        s.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x30 });
        var len = data.Length + 3;
        s.Write(new byte[] { 0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)(len >> 8), 0x31, 0x50, 0x30 });
        s.Write(data);
        s.Write(new byte[] { 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30 });
        EscPosCommands.WriteLineFeed(s);
        s.Write(new byte[] { 0x1B, 0x61, 0x00 });
    }

    public static byte[] BuildEscPosPayload(ReceiptPrinterSettings cfg, string text, int? charWidth = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var w = charWidth ?? ReceiptLayout.CharWidth;
        // 2026-11-21 (редактор чека): тот же конвейер, что и у предпросмотра в редакторе, — на бумаге то же, что на экране.
        var raw = ReceiptPrintPipeline.Prepare(text ?? string.Empty, w);
        if (raw.Length == 0)
            throw new InvalidOperationException("Пустой текст чека.");

        // 2026-10-05, владелец: «изучи Android POS-кассы … чековый принтер — чтобы работало 100%». Встроенный
        // принтер Sunmi (виртуальный Bluetooth «InnerPrinter») из кириллицы знает только CP866 — без кыргызских
        // ң ө ү. Зато умеет UTF-8 (документация SUNMI Inbuilt Printer: FS & + FS C 0xFF): весь текст как есть.
        if (IsUtf8Pos(cfg.TextEncoding))
            return BuildUtf8PosReceipt(raw);

        var encName = MapToDotNetEncoding(cfg.TextEncoding);
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(encName);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.GetEncoding(866);
        }

        var table = cfg.EscPosTableByte ?? DefaultEscPosTableByte(encName);
        return BuildReceiptBytes(encoding, raw, table, cfg.EscRByte, NoEscPct());
    }

    public static void ValidateSettings(ReceiptPrinterSettings cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DevicePath))
            throw new InvalidOperationException("Укажите порт принтера: LPT1, COM3, имя очереди Windows или сетевой адрес TCP:192.168.1.50:9100.");

        if (cfg.RetryCount < 1)
            throw new InvalidOperationException("Количество повторов печати должно быть не меньше 1.");
    }

    private static byte[] BuildReceiptBytes(Encoding encoding, string text, int tableByte, int? escRByte, bool noEscPct)
    {
        using var ms = new MemoryStream(capacity: Math.Max(512, text.Length * 2));
        WriteReceipt(ms, encoding, text, tableByte, escRByte, noEscPct);
        return ms.ToArray();
    }

    private static void WriteReceipt(Stream s, Encoding encoding, string text, int tableByte, int? escRByte, bool noEscPct)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        EscPosCommands.WriteInitialize(s);
        EscPosCommands.WriteCancelKanjiMode(s);
        EscPosCommands.WriteCodePage(s, tableByte, escRByte, noEscPct);
        EscPosCommands.WriteDefaultLineSpacing(s);
        EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: TextFontHeightMultiplier());

        var boldFirst = true;
        var laidOut = ReceiptLineStyle.IsLaidOut(text);
        foreach (var line in lines)
        {
            if (laidOut)
            {
                WriteLaidOutLine(s, encoding, line, TextFontHeightMultiplier());
                continue;
            }

            if (boldFirst && !string.IsNullOrWhiteSpace(line))
            {
                s.WriteByte(0x1B);
                s.WriteByte(0x45);
                s.WriteByte(0x01);
                WriteTextLine(s, encoding, line);
                s.WriteByte(0x1B);
                s.WriteByte(0x45);
                s.WriteByte(0x00);
                boldFirst = false;
                continue;
            }

            WriteTextLine(s, encoding, line);
        }

        EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: 1);
        EscPosCommands.WriteLineFeed(s);
        EscPosCommands.WriteFeedAndCut(s);
    }

    /// <summary>Множитель высоты символов из общей настройки "Размер шрифта (px)" — той же,
    /// что использует графический режим (UserPreferences.GraphicFontSize). 2026-09-08: прошлая
    /// версия сводила весь диапазон 6–15px к 1x ("обычный" размер) — владелец пожаловался, что
    /// даже минимальное значение 6 визуально ничего не меняет. Это отчасти неизбежно: термопринтер
    /// физически не может печатать МЕНЬШЕ своего встроенного шрифта, только крупнее (GS ! —
    /// только множители ×1 и выше, обратного хода нет) — но теперь шаг между соседними значениями
    /// в выпадающем списке (6/8/10/12/14/16/18/20/22/24) заметен почти везде, а не только в
    /// верхней трети диапазона: 6px→×1 (родной размер принтера), 8-12px→×2, 14-17px→×3,
    /// 18-22px→×4, 24px→×5. Ширина не меняется (см. EscPosCommands.WriteCharacterSize) — только
    /// высота, чтобы не сломать выравнивание сумм под фиксированную ширину в символах.</summary>
    private static int TextFontHeightMultiplier() =>
        Math.Clamp((int)Math.Round(UserPreferences.Instance.GraphicFontSize / 5.0), 1, 5);

    /// <summary>2026-11-21 (редактор чека): строка чека по шаблону — жирность и размер (двойная высота / двойной размер) этой строки, затем возврат к обычному.</summary>
    private static void WriteLaidOutLine(Stream s, Encoding encoding, string rawLine, int baseHeight)
    {
        var text = ReceiptLineStyle.Parse(rawLine, out var bold, out var size);
        if (bold)
            s.Write(new byte[] { 0x1B, 0x45, 0x01 });
        if (size > 1 && !string.IsNullOrWhiteSpace(text))
            EscPosCommands.WriteCharacterSize(s, widthMultiplier: size >= 3 ? 2 : 1, heightMultiplier: Math.Clamp(baseHeight * 2, 2, 8)); // 2026-11-21: «высокий» — вдвое выше обычного текста (обычный уже зависит от размера шрифта в настройках)
        WriteTextLine(s, encoding, text);
        if (size > 1 && !string.IsNullOrWhiteSpace(text))
            EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: baseHeight);
        if (bold)
            s.Write(new byte[] { 0x1B, 0x45, 0x00 });
    }

    private static void WriteTextLine(Stream s, Encoding encoding, string line)
    {
        if (!string.IsNullOrEmpty(line))
            s.Write(encoding.GetBytes(line));

        EscPosCommands.WriteLineFeed(s);
    }

    /// <summary>2026-10-05: кодировка «utf8-pos» — встроенный принтер Android-кассы в режиме UTF-8 (Sunmi).</summary>
    public const string Utf8PosEncoding = "utf8-pos";

    private static bool IsUtf8Pos(string? userEnc) =>
        string.Equals((userEnc ?? "").Trim(), Utf8PosEncoding, StringComparison.OrdinalIgnoreCase);

    private static byte[] BuildUtf8PosReceipt(string text)
    {
        using var s = new MemoryStream(capacity: Math.Max(512, text.Length * 3));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        EscPosCommands.WriteInitialize(s);
        // FS & — многобайтовый режим, FS C 0xFF — кодировка UTF-8.
        s.Write(new byte[] { 0x1C, 0x26, 0x1C, 0x43, 0xFF });
        EscPosCommands.WriteDefaultLineSpacing(s);
        EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: TextFontHeightMultiplier());
        var utf8 = new UTF8Encoding(false);
        var boldFirst = true;
        var laidOut = ReceiptLineStyle.IsLaidOut(text);
        foreach (var line in lines)
        {
            if (laidOut)
            {
                WriteLaidOutLine(s, utf8, line, TextFontHeightMultiplier());
                continue;
            }

            if (boldFirst && !string.IsNullOrWhiteSpace(line))
            {
                s.Write(new byte[] { 0x1B, 0x45, 0x01 });
                WriteTextLine(s, utf8, line);
                s.Write(new byte[] { 0x1B, 0x45, 0x00 });
                boldFirst = false;
                continue;
            }
            WriteTextLine(s, utf8, line);
        }
        EscPosCommands.WriteCharacterSize(s, widthMultiplier: 1, heightMultiplier: 1);
        EscPosCommands.WriteLineFeed(s);
        EscPosCommands.WriteFeedAndCut(s);
        return s.ToArray();
    }

    private static string NormalizeDevicePath(string raw)
    {
        var d = HardwarePortHelper.NormalizeLptPort(raw);
        if (d.StartsWith(@"\\", StringComparison.Ordinal))
            return d;
        return d;
    }

    private static string MapToDotNetEncoding(string userEnc)
    {
        var u = (userEnc ?? "cp866").Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return u switch
        {
            "utf-8" or "utf8" => "windows-1251",
            "cp1251" or "windows-1251" or "wpc1251" => "windows-1251",
            "cp855" => "ibm855",
            "cp866" or "ibm866" => "cp866",
            _ => "cp866",
        };
    }

    private static int DefaultEscPosTableByte(string dotnetEncName)
    {
        if (dotnetEncName.Contains("1251", StringComparison.OrdinalIgnoreCase))
            return 46;
        if (string.Equals(dotnetEncName, "ibm855", StringComparison.OrdinalIgnoreCase))
            return 34;
        return 17;
    }
}
