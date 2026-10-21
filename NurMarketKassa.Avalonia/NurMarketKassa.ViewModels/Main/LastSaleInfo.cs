namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-10-18, ревью этапа 6: снимок только что проведённой продажи для плашки «Продажа прошла» и её
/// «Печать копии». Заполняет BasketPanelViewModel перед CheckoutSucceeded — из ответа оплаты (что реально провёл
/// сервер или сохранила офлайн-очередь), а не из окна оплаты.</summary>
/// <param name="Total">Сумма продажи из результата оплаты (result.TotalAmount — сумма сервера или офлайн-чека).</param>
/// <param name="PaymentMethod">Способ из окна оплаты: cash / transfer / debt / mixed.</param>
/// <param name="CashReceived">Полученные наличные (только для «наличных»; та же цифра, что у экрана покупателя).</param>
/// <param name="CashReceivedText">cash_received, ушедший в оплату, — для текста копии чека.</param>
/// <param name="ChangeDue">Сдача, как у экрана покупателя (от суммы сервера, если она не совпала с окном).</param>
/// <param name="SavedOffline">Продажа сохранена в офлайн-очереди (на сервере её ещё нет).</param>
/// <param name="SaleId">Номер продажи из ответа сервера (у офлайн-продажи — null).</param>
/// <param name="CartJson">Корзина продажи (снимок до сброса чека).</param>
/// <param name="SoldAt">Время продажи (местное).</param>
/// <param name="NonCashReceivedText">2026-10-21 (ревью денег, п. 1): non_cash_received, ушедший в оплату (безнал смешанной
/// оплаты или безналичная предоплата долга), — журнал возвратов не велит выдавать его наличными.</param>
public sealed record LastSaleInfo(
    double Total,
    string PaymentMethod,
    double? CashReceived,
    string? CashReceivedText,
    double? ChangeDue,
    bool SavedOffline,
    string? SaleId,
    string? CartJson,
    DateTime SoldAt,
    string? NonCashReceivedText = null);
