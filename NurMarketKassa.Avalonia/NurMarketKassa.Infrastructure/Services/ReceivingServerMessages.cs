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
        ex is ApiException { StatusCode: >= 400 and < 500 } api ? KnownCode(api) ?? Humanize(api.Message) ?? api.Message : ServerOutageMonitor.FriendlyError(ex, showServerText: true);

    /// <summary>2026-11-14: код ошибки лежит в теле ответа (code), а не в тексте — офлайн-коды Умара распознаём и там.</summary>
    private static string? KnownCode(ApiException api)
    {
        var raw = api.Payload is { } p ? p.ToString() : "";
        foreach (var code in new[] { "offline_too_old", "period_closed" })
            if (raw.Contains(code, StringComparison.OrdinalIgnoreCase))
                return Humanize(code);
        return null;
    }

    public static string? Humanize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;
        // 2026-11-14 (ответ Умара 13.11, П0-2): офлайн-приход старше 7 суток и закрытый период — понятным текстом и с действием.
        if (message.Contains("offline_too_old", StringComparison.OrdinalIgnoreCase))
            return Tr.T("Приход принят без связи больше 7 суток назад — сервер такой автоматически не берёт. Не удаляйте его: проведите приход вручную на сайте (владелец/администратор) с настоящей датой и уберите эту запись из очереди.",
                "Приход байланышсыз 7 суткадан ашык мурун кабыл алынган — сервер аны автоматтык кабыл албайт. Өчүрбөңүз: сайтта кол менен (ээси/администратор) чыныгы датасы менен өткөрүп, бул жазууну кезектен алып салыңыз.",
                "The delivery was received offline more than 7 days ago — the server does not take it automatically. Do not delete it: post it by hand on the website (owner/administrator) with the real date and remove this item from the queue.",
                "Mal kabulü bağlantısız 7 günden önce yapıldı — sunucu bunu otomatik kabul etmez. Silmeyin: siteden elle (sahip/yönetici) gerçek tarihle işleyin ve bu kaydı kuyruktan kaldırın.",
                "Kirim aloqasiz 7 kundan oldin qabul qilingan — server uni avtomatik qabul qilmaydi. O'chirmang: saytda qo'lda (egasi/administrator) haqiqiy sana bilan o'tkazing va bu yozuvni navbatdan olib tashlang.");
        if (message.Contains("period_closed", StringComparison.OrdinalIgnoreCase))
            return Tr.T("Период закрыт на сервере — приход с такой датой не принимается. Обратитесь к владельцу: он может провести приход вручную или открыть период.",
                "Сервердеги мезгил жабык — мындай датадагы кирүү кабыл алынбайт. Ээсине кайрылыңыз: ал кирүүнү кол менен өткөрө алат же мезгилди ача алат.",
                "The period is closed on the server — a delivery with this date is not accepted. Ask the owner: they can post it by hand or reopen the period.",
                "Dönem sunucuda kapalı — bu tarihli mal kabulü kabul edilmez. Sahibine başvurun: elle işleyebilir veya dönemi açabilir.",
                "Davr serverda yopiq — bunday sanali kirim qabul qilinmaydi. Egasiga murojaat qiling: u qo'lda o'tkazishi yoki davrni ochishi mumkin.");
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
