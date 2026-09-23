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
    public static string BuildPluRecord(int plu, string name, double priceSom, char weighingUnit = '4')
    {
        var transliteratedName = RongtaTransliterator.Transliterate(name);
        var sb = new StringBuilder(100);
        sb.Append('I');                                    // Operate: I = добавить/обновить
        sb.Append("00");                                    // Rank — семантика не описана, 0
        sb.Append(PadRightFixed(transliteratedName, 36));    // Name
        sb.Append(ClampDigits(plu, 6));                      // Fresh food code / LFCode = PLU
        sb.Append(ClampDigits(0, 10));                       // Art. No. / Code — не используем
        sb.Append(ClampDigits(0, 2));                        // Barcode type — 0 = не задан
        sb.Append(ClampDigits((long)Math.Round(priceSom * 100), 8)); // Unit price ×100, без точки
        sb.Append(weighingUnit);                             // Weighing unit (по умолчанию '4' = кг)
        sb.Append(ClampDigits(0, 2));                        // Dept.
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
