namespace NurMarketKassa.Services;

/// <summary>
/// Определяет режим офлайн-работы кассы и доступность операций без REST API сайта.
/// </summary>
public static class OfflineModeHelper
{
    public static bool IsNetworkOnline => !UseLocalOperations;

    public static bool UseLocalOperations => PosApp.IsOfflineBootstrap;

    public static bool CanOperateWithoutServer => UseLocalOperations;

    /// <summary>2026-09-29: сервер NurCRM сейчас не отвечает (ServerOutageMonitor) — касса
    /// работает автономно до его восстановления. Отдельно от <see cref="UseLocalOperations"/>
    /// намеренно: тот флаг включает ещё и чисто локальные правки (товар, созданный офлайн, на
    /// сервер не уходит никогда), а временная авария сервера не должна создавать такие данные.
    /// Где что разрешено в аварии — таблица в комментарии к PosCheckoutService.CheckoutAsync.</summary>
    public static bool IsServerOutage => ServerOutageMonitor.IsOutage;

    /// <summary>Продажа проводится локально (в офлайн-очередь), без обращения к серверу.</summary>
    public static bool SellLocally => UseLocalOperations || IsServerOutage;

    /// <summary>2026-09-29: возврат в аварии сервера не проводится — нужна продажа с сервера, а
    /// выдать деньги без записи о возврате нельзя. Текст — не ошибка, а что делать.</summary>
    public static string ReturnUnavailableInOutage => Tr.T(
        "Возврат недоступен, пока сервер NurCRM не отвечает. Продажи работают — оформите возврат, когда связь восстановится.",
        "NurCRM сервери жооп бербей турганда кайтаруу жеткиликсиз. Сатуулар иштейт — байланыш калыбына келгенде кайтарууну жасаңыз.",
        "Returns are unavailable while the NurCRM server is not responding. Sales keep working — process the return once the connection is restored.",
        "NurCRM sunucusu yanıt vermediği sürece iade yapılamaz. Satışlar çalışıyor — bağlantı yeniden kurulunca iadeyi yapın.",
        "NurCRM serveri javob bermayotgan paytda qaytarish mavjud emas. Sotuvlar ishlayapti — aloqa tiklanganda qaytarishni rasmiylashtiring.");

    /// <summary>2026-09-29: оплата долга в аварии сервера — список долгов есть только на сервере.</summary>
    public static string DebtPaymentUnavailableInOutage => Tr.T(
        "Оплата долга недоступна, пока сервер NurCRM не отвечает. Продажи работают — примите оплату долга, когда связь восстановится.",
        "NurCRM сервери жооп бербей турганда карыз төлөө жеткиликсиз. Сатуулар иштейт — байланыш калыбына келгенде карыз төлөмүн алыңыз.",
        "Debt payments are unavailable while the NurCRM server is not responding. Sales keep working — accept the debt payment once the connection is restored.",
        "NurCRM sunucusu yanıt vermediği sürece borç ödemesi yapılamaz. Satışlar çalışıyor — bağlantı yeniden kurulunca borç ödemesini alın.",
        "NurCRM serveri javob bermayotgan paytda qarzni to'lash mavjud emas. Sotuvlar ishlayapti — aloqa tiklanganda qarz to'lovini qabul qiling.");
}
