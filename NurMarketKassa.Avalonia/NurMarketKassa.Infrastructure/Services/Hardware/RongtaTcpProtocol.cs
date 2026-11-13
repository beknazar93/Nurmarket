using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Кодек протокола "RLS1000 based on TCP/IP protocol interface specification"
/// (мануал "Label Scale Software User Manual", раздел 2.2-2.4) — используется только когда
/// владелец сам выбрал источник данных "свой сервер (локальная база)" для Rongta в
/// ScalesPluWindow; способ по умолчанию — файл + F9 через RongtaScaleAutomationService,
/// см. её doc-comment.
///
/// ЧЕСТНО, это прочитано из PDF-мануала (pdftotext) и разобрано по байтам на примере из
/// самого мануала ("00080201" / "0022010202100000010000" — сходится), но НЕ проверено на
/// реальных весах/RLS1000: (1) кодировка кириллицы в мануале не описана — поэтому имя
/// товара здесь ВСЕГДА транслитерируется (RongtaTransliterator), как и на сайте для
/// экспорта .txp; (2) точная семантика полей "Rank"/"Sales mark"/"Discount mark" не
/// описана — оставлены нулевыми; (3) какой именно ответ RLS1000 ждёт на "0201 Start" перед
/// тем как начать принимать "0110" записи, в мануале не показано напрямую (пример ACK в
/// мануале — ответ на 0210, не на 0201) — здесь ACK строится по общей форме (эхо кода
/// команды + нулевой fresh/error code), это наилучшая интерпретация, не гарантия.</summary>
public static class RongtaTcpProtocol
{
    public const string CmdStart = "0201";
    public const string CmdStartAck = "0202";
    public const string CmdSalesRecord = "0210";
    public const string CmdSalesEnd = "0220";
    public const string CmdAck = "0102";
    public const string CmdPluSend = "0110";
    public const string CmdRequestSalesUpload = "0120";

    /// <summary>Предел поля «Unit price» (8 цифр, сотые без точки) — 999 999,99 сом.</summary>
    public const long MaxPriceUnits = 99_999_999;

    /// <summary>Пакет = 4 ASCII-цифры длины (весь пакет целиком, включая само поле длины) +
    /// 4 ASCII-цифры код команды + данные. Пример из мануала: "00080201" — длина "0008" (=8
    /// символов всего пакета), команда "0201" (Start), без данных.</summary>
    public static string BuildPacket(string command, string data)
    {
        var body = command + data;
        var length = body.Length + 4; // +4 за само поле длины
        return length.ToString("D4", CultureInfo.InvariantCulture) + body;
    }

    /// <summary>Разбирает один пакет из начала буфера. Возвращает null, если пакет ещё не
    /// пришёл полностью (нужно читать дальше из сокета).</summary>
    public static (string Command, string Data)? TryParsePacket(StringBuilder buffer)
    {
        if (buffer.Length < 8)
            return null;

        var lengthText = buffer.ToString(0, 4);
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var totalLength) || totalLength < 8)
            return null;

        if (buffer.Length < totalLength)
            return null;

        var command = buffer.ToString(4, 4);
        var data = totalLength > 8 ? buffer.ToString(8, totalLength - 8) : "";
        buffer.Remove(0, totalLength);
        return (command, data);
    }

    /// <summary>0102 "Response for demand(ACK)": Command Code(4) + Fresh code(6) + Error
    /// code(4) — эхо принятой команды, нулевой fresh code (Start своего fresh code не
    /// несёт), "0000" = нет ошибки.</summary>
    public static string BuildAck(string acknowledgedCommand, string freshCode = "000000")
    {
        var fresh = (freshCode ?? "000000").PadLeft(6, '0')[..6];
        return BuildPacket(CmdAck, acknowledgedCommand.PadLeft(4, '0')[..4] + fresh + "0000");
    }

    /// <summary>0110 "PLU back stage sending" — одна запись товара, ровно 100 символов
    /// данных (проверено суммированием ширин полей из мануала). PLU-номер товара уходит в
    /// поле "Fresh food code"/LFCode (6 цифр) — именно оно, а не формально названное "PLU
    /// No.", служит идентификатором PLU (см. Appendix мануала: "PLU No. — Retain for
    /// compatibility, no meaning"; "LFCode — unique identification... using for imputing
    /// PLU").</summary>
    public static string BuildPluRecord(int plu, string name, double priceSom, char weighingUnit = '4', int pricePoint = 2, int barcodeType = 5, int department = 21)
    {
        var transliteratedName = RongtaTransliterator.Transliterate(name);
        var sb = new StringBuilder(100);
        sb.Append('I');                                    // Operate: I = добавить/обновить
        sb.Append("00");                                    // Rank — семантика не описана, 0
        sb.Append(PadRightFixed(transliteratedName, 36));    // Name
        sb.Append(ClampDigits(plu, 6));                      // Fresh food code / LFCode = PLU
        // 2026-09-28: Code, тип штрихкода и отдел — как в рабочем .txp с сайта (products.txp:
        // «LF код» = «Код» = PLU, «Тип штрихкода» = 5, «Отдел» = 21). Раньше здесь шли нули —
        // по руководству весы берут «номер товара» штрихкода из поля Code, и этикетка вышла бы
        // с нулевым кодом, а касса не нашла бы товар. Тип 05 = отдел(2)+код(6)+вес(4) —
        // раскладка компании «по коду», как советует документация сайта для Rongta.
        sb.Append(ClampDigits(plu, 10));                     // Art. No. / Code = PLU
        // 2026-11-13 (руководство RLS1000, команда 0110: «Barcode type, 2 цифры» сразу после «Art. No. (10)»): тип из «Настроек весов Rongta» → «Штрих-код»;
        // по умолчанию 05, как было всегда.
        sb.Append(ClampDigits(Math.Clamp(barcodeType, 0, 99), 2)); // Barcode type
        // 2026-11-06: раньше ClampDigits брал «последние 8 цифр» (цена ≥ 1 000 000 сом молча превращалась в другую). Теперь — явная ошибка.
        // 2026-11-07 (жалоба «цена бала 800, а на этикетке 80,00»): цена уходит в единицах «знаков после запятой» ВЕСОВ (параметр «Price point», настройка «Знаков после запятой в цене на весах»),
        // а не всегда ×100 — иначе при другом Price point на весах цена искажалась в 10 раз.
        pricePoint = Math.Clamp(pricePoint, 0, 3);
        var priceUnits = (long)Math.Round(priceSom * Math.Pow(10, pricePoint), MidpointRounding.AwayFromZero);
        if (priceUnits < 0 || priceUnits > MaxPriceUnits)
            throw new ArgumentOutOfRangeException(nameof(priceSom), priceSom, "Цена не помещается в 8 цифр поля Unit price (максимум 999 999,99 сом).");
        sb.Append(ClampDigits(priceUnits, 8));               // Unit price ×10^Price point, без точки
        sb.Append(weighingUnit);                             // Weighing unit (по умолчанию '4' = кг)
        sb.Append(ClampDigits(Math.Clamp(department, 0, 99), 2)); // Dept. (префикс штрихкода), по умолчанию 21 — 2026-11-13: вместе с выбранным типом
        sb.Append(ClampDigits(0, 6));                        // Tare weight (по умолчанию 0)
        sb.Append(ClampDigits(15, 3));                        // Saving period (по умолчанию 15, как в Appendix)
        sb.Append(ClampDigits(0, 1));                         // Packing type
        sb.Append(ClampDigits(0, 6));                         // Packing weight
        sb.Append(ClampDigits(5, 2));                         // Packing error (по умолчанию 5, как в Appendix)
        sb.Append(ClampDigits(0, 3));                         // Message1
        sb.Append(ClampDigits(0, 3));                         // Message2
        sb.Append(ClampDigits(0, 3));                         // Multi-barcode
        sb.Append(ClampDigits(0, 3));                         // Discount
        sb.Append('0');                                       // Sales mark — не описано, 0
        sb.Append('0');                                       // Discount mark — не описано, 0

        var data = sb.ToString();
        return BuildPacket(CmdPluSend, data);
    }

    private static string PadRightFixed(string value, int width)
    {
        value ??= "";
        if (value.Length > width)
            return value[..width];
        return value.PadRight(width, ' ');
    }

    private static string ClampDigits(long value, int width)
    {
        if (value < 0)
            value = 0;
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length > width ? text[^width..] : text.PadLeft(width, '0');
    }
}
