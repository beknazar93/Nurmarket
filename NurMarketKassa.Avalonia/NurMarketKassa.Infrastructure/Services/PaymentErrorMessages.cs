namespace NurMarketKassa.Services;

public static class PaymentErrorMessages
{
    public static string DiscountFailure => Tr.T(
        "Не удалось выполнить оплату. Проверьте параметры скидки.",
        "Төлөм аткарылган жок. Арзандатуунун параметрлерин текшериңиз.",
        "Payment failed. Check the discount settings.",
        "Ödeme yapılamadı. İndirim ayarlarını kontrol edin.",
        "To'lovni amalga oshirib bo'lmadi. Chegirma parametrlarini tekshiring.");

    public static string GenericFailure => Tr.T(
        "Не удалось выполнить оплату. Попробуйте ещё раз или обратитесь к администратору.",
        "Төлөм аткарылган жок. Кайра аракет кылыңыз же администраторго кайрылыңыз.",
        "Payment failed. Try again or contact your administrator.",
        "Ödeme yapılamadı. Tekrar deneyin veya yöneticinize başvurun.",
        "To'lovni amalga oshirib bo'lmadi. Qayta urinib ko'ring yoki administratorga murojaat qiling.");

    public static bool LooksLikeDiscountError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("order_discount", StringComparison.OrdinalIgnoreCase)
               || message.Contains("non_field_errors", StringComparison.OrdinalIgnoreCase)
               || message.Contains("фиксированную скидку", StringComparison.OrdinalIgnoreCase)
               || message.Contains("скидку в процентах", StringComparison.OrdinalIgnoreCase);
    }

    public static string ForCashier(Exception ex)
    {
        if (ex is ApiException api)
        {
            if (LooksLikeDiscountError(api.Message))
                return DiscountFailure;

            if (api.StatusCode == 404)
            {
                return Tr.T("Сервер не нашёл активную корзину для оплаты. " +
                            "Товары сохранены — повторите оплату или обновите кассу.",
                    "Сервер төлөм үчүн активдүү себетти тапкан жок. " +
                    "Товарлар сакталды — төлөмдү кайталаңыз же кассаны жаңылаңыз.",
                    "The server couldn't find an active cart for payment. " +
                    "The items are saved — retry the payment or refresh the till.",
                    "Sunucu ödeme için aktif sepet bulamadı. " +
                    "Ürünler kaydedildi — ödemeyi tekrarlayın veya kasayı yenileyin.",
                    "Server to'lov uchun faol savatni topmadi. " +
                    "Mahsulotlar saqlandi — to'lovni takrorlang yoki kassani yangilang.");
            }

            // 2026-09-28, стресс-тест: на 21-й продаже сервер NurCRM ответил 502, и кассир увидел
            // голое «error code: 502». Ошибки 5xx — сбой на стороне сервера: деньги не списаны,
            // чек остаётся в кассе, оплату можно просто повторить.
            if (api.StatusCode is >= 500 and <= 599)
            {
                return Tr.T($"Сервер NurCRM временно не отвечает (ошибка {api.StatusCode}). " +
                            "Деньги не списаны, чек сохранён — повторите оплату через минуту.",
                    $"NurCRM сервери убактылуу жооп бербей жатат ({api.StatusCode} катасы). " +
                    "Акча алынган жок, чек сакталды — бир мүнөттөн кийин төлөмдү кайталаңыз.",
                    $"The NurCRM server is temporarily not responding (error {api.StatusCode}). " +
                    "No money was taken, the receipt is saved — retry the payment in a minute.",
                    $"NurCRM sunucusu geçici olarak yanıt vermiyor (hata {api.StatusCode}). " +
                    "Para alınmadı, fiş kaydedildi — bir dakika sonra ödemeyi tekrarlayın.",
                    $"NurCRM serveri vaqtincha javob bermayapti ({api.StatusCode} xatosi). " +
                    "Pul yechilmadi, chek saqlandi — bir daqiqadan so'ng to'lovni takrorlang.");
            }

            return string.IsNullOrWhiteSpace(api.Message) ? GenericFailure : api.Message;
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? GenericFailure : ex.Message;
    }

    public static void Log(string context, Exception ex) =>
        PosLogger.Log($"{context}: {ex}", "PAYMENT ERROR");
}
