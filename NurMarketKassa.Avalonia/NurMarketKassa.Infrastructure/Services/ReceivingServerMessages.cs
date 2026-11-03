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
    /// <summary>2026-11-02 (аудит №2): понятный текст ошибки для экрана — ответ сервера (с разбором «дробь», «знаков после запятой») либо, для сбоя сети/таймаута/непонятной ошибки, общая фраза
    /// (<see cref="ServerOutageMonitor.FriendlyError"/>) вместо сырого «The request was canceled…». Подробности остаются в журнале.</summary>
    public static string Describe(Exception ex) =>
        ex is ApiException { StatusCode: >= 400 and < 500 } api ? Humanize(api.Message) ?? api.Message : ServerOutageMonitor.FriendlyError(ex, showServerText: true);

    public static string? Humanize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;
        // 2026-11-01 (контрольный аудит, С2-10): сервер принимает дробное количество только у весовых и мерных товаров — 422 fractional_not_allowed на строке
        if (message.Contains("fractional", StringComparison.OrdinalIgnoreCase) || message.Contains("дробн", StringComparison.OrdinalIgnoreCase))
            return Tr.T("Сервер не принимает дробное количество у штучного товара: оно допустимо только для весовых и мерных (кг, л, м). Сделайте количество целым или выберите весовую единицу.",
                "Сервер данабай товарда бөлчөк санды кабыл албайт: ал салмактуу жана өлчөмдүү (кг, л, м) товарларга гана уруксат. Санды бүтүн кылыңыз же салмактуу бирдикти тандаңыз.",
                "The server does not accept a fractional quantity for a piece product: it is allowed only for weighed and measured goods (kg, l, m). Make the quantity a whole number or choose a weight unit.",
                "Sunucu adetli üründe ondalık miktarı kabul etmez: yalnızca tartılı ve ölçülü ürünlerde (kg, l, m) olur. Miktarı tam sayı yapın veya ağırlık birimi seçin.",
                "Server donali mahsulotda kasr miqdorni qabul qilmaydi: u faqat vaznli va o'lchovli (kg, l, m) mahsulotlarda mumkin. Miqdorni butun qiling yoki vazn birligini tanlang.");
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
