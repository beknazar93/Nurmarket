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

    public static byte[] BuildEscPosPayload(ReceiptPrinterSettings cfg, string text, int? charWidth = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var prepared = ReceiptSanitizer.StripShiftCashboxAndEnsureWelcome(text ?? string.Empty);
        var w = charWidth ?? ReceiptLayout.CharWidth;
        var raw = ReceiptTextFormatter.FormatForPrinter(prepared, w).Trim().ToUpperInvariant();
        if (raw.Length == 0)
            throw new InvalidOperationException("Пустой текст чека.");

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
            throw new InvalidOperationException("Укажите порт принтера: LPT1, COM3 или имя очереди Windows.");

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
        EscPosCommands.WriteCodePage(s, tableByte, escRByte, noEscPct);
        EscPosCommands.WriteDefaultLineSpacing(s);

        var boldFirst = true;
        foreach (var line in lines)
        {
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

        EscPosCommands.WriteLineFeed(s);
        EscPosCommands.WriteFeedAndCut(s);
    }

    private static void WriteTextLine(Stream s, Encoding encoding, string line)
    {
        if (!string.IsNullOrEmpty(line))
            s.Write(encoding.GetBytes(line));

        EscPosCommands.WriteLineFeed(s);
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
