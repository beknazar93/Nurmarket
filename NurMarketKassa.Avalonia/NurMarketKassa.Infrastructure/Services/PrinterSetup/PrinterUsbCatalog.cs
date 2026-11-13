using System.Globalization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13 (мастер «Подключить принтер»): распознавание USB-принтеров — марка, модель, чековый или этикеточный.
/// Главный признак — ответ самого принтера (IEEE 1284), который Windows кладёт в код устройства «USBPRINT\XprinterXP-420B83D7»
/// (так в базах драйверов: XprinterXP-365B6494, XPRINTERXP-420B83D7, XprinterXP-TT436B8058). Код производителя USB (VID) — запасной признак:
/// дешёвые китайские принтеры (Xprinter, Zjiang, POS-58/80) часто идут на чужих VID контроллеров — Winbond 0416 (POS58, 0416:5011 — подтверждено в журналах
/// Linux/Odoo), ICS Advent 0FE6 (0FE6:811E — Zjiang ZJ-58, многие POS-80), STMicro 0483, NXP 1FC9 — такие помечаются «вероятно Xprinter».</summary>
public static class PrinterUsbCatalog
{
    public const string XprinterBrand = "Xprinter";

    /// <summary>VID контроллеров, на которых обычно сделаны недорогие ESC/POS/TSPL-принтеры (Xprinter и похожие) — «вероятно Xprinter».</summary>
    private static readonly HashSet<ushort> ProbableXprinterVids = [0x0416, 0x0FE6, 0x0483, 0x1FC9];

    /// <summary>VID известных марок (код производителя USB однозначен).</summary>
    private static readonly Dictionary<ushort, string> KnownVids = new()
    {
        [0x04B8] = "Epson",
        [0x0519] = "Star",
        [0x1504] = "Bixolon",
        [0x1D90] = "Citizen",
        [0x0DD4] = "Custom",
        [0x154F] = "SNBC",
        [0x0A5F] = "Zebra",
        [0x1203] = "TSC",
        [0x195F] = "Godex",
    };

    /// <summary>Производители из ответа принтера (IEEE 1284 MFG) — префикс склеенного кода «XprinterXP-58IIH».</summary>
    private static readonly string[] KnownManufacturers =
    [
        "Xprinter", "Gprinter", "Gainscha", "Zjiang", "Rongta", "HPRT", "Citizen", "Epson", "Star", "Bixolon", "TSC", "Zebra", "Godex", "SNBC", "Sunmi", "Munbyn",
    ];

    private static readonly Regex VidPidRx = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Этикеточные модели: Xprinter XP-3xxB/4xxB (365B, 360B, 420B, 460B, 470B…), TT/DT/D4xx, а также марки этикеточных принтеров.
    private static readonly Regex LabelModelRx = new(@"(?:XP-?)?(?:[234]\d{2}B|TT\d{3}|DT\d{3}|D4\d{2}|H500)\b|LABEL|TSPL|ZPL|TSC|ZEBRA|GODEX|ЭТИКЕТ|BARCODE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Чековые: XP-58 (58IIH, 58IIL…), XP-80 (80T, 80C), Q80/Q90/Q200, C230/C260, E200, N160, V3, POS-58/80, «Receipt».
    private static readonly Regex ReceiptModelRx = new(@"58|80|76|Q\d{2,3}|C2[36]0|E2\d{2}|N160|POS|RECEIPT|ЧЕК",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>VID и PID из кода устройства «USB\VID_0416&amp;PID_5011\…» или пути «\\?\usb#vid_0416&amp;pid_5011#…».</summary>
    public static bool TryParseVidPid(string? text, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        if (string.IsNullOrEmpty(text))
            return false;
        var m = VidPidRx.Match(text);
        if (!m.Success)
            return false;
        vid = ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        pid = ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Производитель и модель из кода «USBPRINT\XprinterXP-420B83D7\7&amp;…»: последние 4 знака — контрольная сумма Windows, отбрасываются.
    /// null — это не код USBPRINT.</summary>
    public static (string? Manufacturer, string Model)? ParseUsbPrintId(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
            return null;
        var parts = hardwareId.Trim().Split('\\');
        if (parts.Length < 2 || !parts[0].Equals("USBPRINT", StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0)
            return null;
        var id = parts[1];
        if (id.Length > 5 && id[^4..].All(Uri.IsHexDigit))
            id = id[..^4];
        foreach (var mfg in KnownManufacturers)
        {
            if (id.Length > mfg.Length && id.StartsWith(mfg, StringComparison.OrdinalIgnoreCase))
                return (mfg, id[mfg.Length..].Trim('-', '_', ' '));
        }
        return (null, id);
    }

    /// <summary>Марка по ответу принтера, имени очереди/драйвера и VID.</summary>
    public static PrinterBrandGuess GuessBrand(ushort vid, params string?[] names)
    {
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var n = raw.Trim();
            if (n.Contains("xprinter", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(n, @"^XP-?[A-Z]?\d", RegexOptions.IgnoreCase))
                return new PrinterBrandGuess(XprinterBrand, false);
            if (n.Contains("gprinter", StringComparison.OrdinalIgnoreCase) || n.Contains("gainscha", StringComparison.OrdinalIgnoreCase))
                return new PrinterBrandGuess("Gprinter", false);
            if (n.Contains("zjiang", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(n, @"^ZJ-?\d", RegexOptions.IgnoreCase))
                return new PrinterBrandGuess("Zjiang", false);
            if (n.Contains("rongta", StringComparison.OrdinalIgnoreCase))
                return new PrinterBrandGuess("Rongta", false);
        }
        if (KnownVids.TryGetValue(vid, out var brand))
            return new PrinterBrandGuess(brand, false);
        if (ProbableXprinterVids.Contains(vid))
            return new PrinterBrandGuess(XprinterBrand, true);
        return PrinterBrandGuess.None;
    }

    /// <summary>Чековый или этикеточный — по модели и имени (этикеточные признаки проверяются первыми: «XP-420B» содержит и «420»).</summary>
    public static PrinterRole GuessRole(params string?[] names)
    {
        var text = string.Join(" ", names.Where(n => !string.IsNullOrWhiteSpace(n)));
        if (text.Length == 0)
            return PrinterRole.Auto;
        if (LabelModelRx.IsMatch(text))
            return PrinterRole.Label;
        if (ReceiptModelRx.IsMatch(text))
            return PrinterRole.Receipt;
        return PrinterRole.Auto;
    }

    /// <summary>Язык команд по роли: этикеточные Xprinter/Gprinter/TSC — TSPL, чековые — ESC/POS.</summary>
    public static PrinterLanguage LanguageFor(PrinterRole role) => role switch
    {
        PrinterRole.Label => PrinterLanguage.Tspl,
        PrinterRole.Receipt => PrinterLanguage.EscPos,
        _ => PrinterLanguage.Unknown,
    };

    /// <summary>Подпись марки для списка: «Xprinter», «вероятно Xprinter», «Epson» или пусто.</summary>
    public static string BrandText(PrinterBrandGuess brand)
    {
        if (brand.Brand is null)
            return "";
        if (!brand.Probable)
            return brand.Brand;
        return Tr.T($"вероятно {brand.Brand}", $"болжолу {brand.Brand}", $"probably {brand.Brand}", $"muhtemelen {brand.Brand}", $"ehtimol {brand.Brand}");
    }

    /// <summary>«VID 0416 / PID 5011».</summary>
    public static string VidPidText(ushort vid, ushort pid) => $"VID {vid:X4} / PID {pid:X4}";
}
