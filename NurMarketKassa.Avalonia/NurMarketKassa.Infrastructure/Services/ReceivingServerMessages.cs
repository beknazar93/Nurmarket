using System.Globalization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: понятный текст типичных отказов сервера при оприходовании. Раньше в «Приходах в фоне» красной строкой висел сырой ответ
/// «Убедитесь, что вы ввели не более 3 цифры после запятой» — без слов, какое поле и что делать.</summary>
public static class ReceivingServerMessages
{
    private static readonly Regex RuDecimals = new(@"не\s+более\s+(\d+)\s+цифр\w*\s+после\s+запят", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnDecimals = new(@"no\s+more\s+than\s+(\d+)\s+decimal\s+place", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Сырой ответ сервера → понятный текст; незнакомый ответ возвращается как есть (null/пусто — как есть).</summary>
    public static string? Humanize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;
        var m = RuDecimals.Match(message);
        if (!m.Success)
            m = EnDecimals.Match(message);
        if (!m.Success)
            return message;
        var n = int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 3;
        return Tr.T(
            $"Сервер принимает не больше {n} знаков после запятой в закупке и количестве. Исправьте число и повторите.",
            $"Сервер закупкада жана санда үтүрдөн кийин {n} цифрадан ашык кабыл албайт. Санды оңдоп, кайра жасаңыз.",
            $"The server accepts no more than {n} decimal places in the purchase price and quantity. Fix the number and try again.",
            $"Sunucu alış fiyatı ve miktarda virgülden sonra en fazla {n} basamak kabul eder. Sayıyı düzeltip tekrar deneyin.",
            $"Server xarid narxi va miqdorda verguldan keyin {n} tadan ko'p raqamni qabul qilmaydi. Sonni to'g'rilab, qayta urinib ko'ring.");
    }
}
