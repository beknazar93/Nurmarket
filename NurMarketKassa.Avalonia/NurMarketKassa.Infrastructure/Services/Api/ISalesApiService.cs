using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис продаж: корзины, чеки, скан, чекаут, возвраты и история продаж.
/// </summary>
public interface ISalesApiService
{
    /// <summary>POST /api/main/pos/sales/start/ (по идентификатору кассы).</summary>
    Task<JsonElement> PosSalesStartAsync(string? cashboxId = null, CancellationToken ct = default);

    /// <summary>POST /api/main/pos/sales/start/ с произвольным телом (возврат, касса и т.д.).</summary>
    Task<JsonElement> PosSalesStartAsync(IReadOnlyDictionary<string, string>? body, CancellationToken ct = default);

    /// <summary>GET /api/main/pos/carts/{id}/</summary>
    Task<JsonElement> PosCartGetAsync(string cartId, CancellationToken ct = default);

    /// <summary>POST /api/main/pos/sales/{id}/scan/</summary>
    Task<JsonElement> PosScanAsync(string cartId, string barcode, string? quantity = null, CancellationToken ct = default);

    /// <summary>PATCH /api/main/pos/carts/{cart}/items/{item}/</summary>
    Task<JsonElement> PosCartItemPatchAsync(string cartId, string itemId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default);

    /// <summary>DELETE /api/main/pos/carts/{cart}/items/{item}/</summary>
    Task<JsonElement> PosCartItemDeleteAsync(string cartId, string itemId, CancellationToken ct = default);

    /// <summary>POST checkout (перебор URL, повтор для безнала).</summary>
    Task<JsonElement> PosCheckoutAsync(string cartId, Dictionary<string, string> body, CancellationToken ct = default);

    /// <summary>POST checkout с перебором нескольких id (cart / sale) и типовых URL.</summary>
    Task<JsonElement> PosCheckoutAsync(
        IReadOnlyList<string> targetIds,
        Dictionary<string, string> body,
        CancellationToken ct = default);

    /// <summary>GET /api/main/pos/sales/{id}/receipt/ — текст чека для печати.</summary>
    Task<JsonElement> PosSaleReceiptAsync(string saleId, CancellationToken ct = default);

    /// <summary>PATCH /api/main/pos/carts/{id}/ — скидка на чек и др.</summary>
    Task<JsonElement> PosCartPatchAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default);

    /// <summary>POST /api/main/pos/sales/{id}/add-item/</summary>
    Task<JsonElement> PosAddItemAsync(string cartId, string productId, string? quantity = null, string? unitPrice = null, string? discountTotal = null, CancellationToken ct = default);

    /// <summary>POST add-item с произвольными полями (возврат, ссылка на строку исходного чека).</summary>
    Task<JsonElement> PosAddItemRawAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default);

    /// <summary>«Доп. услуга» — POST /pos/carts/{id}/custom-item/ {name, price, quantity} (2026-09-07),
    /// как в «Интерфейсе кассира» на сайте; отрицательная price = «Расход».</summary>
    Task<JsonElement> PosAddCustomItemAsync(string cartId, string name, double price, double quantity, CancellationToken ct = default);

    /// <summary>Активная корзина текущей вкладки чека (контекст POS).</summary>
    string? ActiveCartId { get; }

    /// <summary>Синхронизирует активный идентификатор корзины при переключении вкладок чека.</summary>
    void SetActiveCartId(string? cartId);

    /// <summary>Список продаж (для выбора чека возврата и отчётов). dateFrom/dateToExclusive —
    /// серверная фильтрация по периоду (параметры date_from/date_to, проверены живьём);
    /// dateToExclusive НЕ включает свой день, передавайте день ПОСЛЕ последнего нужного.</summary>
    Task<List<JsonElement>> PosSalesListAsync(
        int page,
        int pageSize,
        string? cashboxId = null,
        CancellationToken ct = default,
        DateTime? dateFrom = null,
        DateTime? dateToExclusive = null);

    /// <summary>GET карточки продажи со строками.</summary>
    Task<JsonElement> PosSaleGetAsync(string saleId, CancellationToken ct = default);

    /// <summary>GET /api/main/pos/cart-item-deletions/get/</summary>
    Task<JsonElement> PosCartItemDeletionsGetAsync(CancellationToken ct = default);

    /// <summary>GET /api/main/pos/cart-item-deletions/ — журнал возвратов/удалений позиций
    /// (товар, количество, кто, когда — без суммы и номера чека).</summary>
    Task<List<JsonElement>> PosCartItemDeletionsListAsync(CancellationToken ct = default);

    /// <summary>Регистрация возврата через cart-item-deletions/get (для оплаченных чеков).</summary>
    Task<bool> TryPosCartItemDeletionReturnAsync(string saleId, string? cartId, PosRefundLineRequest line, string? reason, CancellationToken ct = default);

    /// <summary>PATCH /api/main/pos/sales/{id}/</summary>
    Task<JsonElement> PosSalePatchAsync(string saleId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default);

    /// <summary>DELETE /api/main/pos/sales/{id}/</summary>
    Task<JsonElement> PosSaleDeleteAsync(string saleId, CancellationToken ct = default);

    /// <summary>Возврат позиции по API Nur CRM: регистрация удаления → PATCH (частично) → DELETE строки корзины.</summary>
    Task<JsonElement> PosReturnCartLineAsync(string cartId, PosRefundLineRequest line, string? reason, CancellationToken ct = default);

    /// <summary>Полный возврат чека: PATCH с причиной, затем DELETE продажи.</summary>
    Task<JsonElement> PosReturnWholeSaleAsync(string saleId, string? reason, CancellationToken ct = default);

    /// <summary>POST /api/main/pos/sales/{id}/return/ — тот же запрос, что шлёт сайт.
    /// <paramref name="lines"/> пуст или null — возвращается весь чек; непустой — только эти
    /// позиции (items: sale_item_id + quantity).</summary>
    Task<JsonElement> PosReturnSaleAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, CancellationToken ct = default);

    /// <summary>Покупки клиента (GET /api/main/pos/sales/?client=…) — для карточки клиента.</summary>
    Task<List<JsonElement>> PosSalesByClientAsync(string clientId, int maxPages = 5, CancellationToken ct = default);

    /// <summary>GET /api/main/pos/sales/?status=debt(&client=) — непогашенные продажи «в долг».</summary>
    Task<List<JsonElement>> PosDebtSalesAsync(string? clientId, CancellationToken ct = default);

    /// <summary>POST /api/main/clients/{clientId}/deals/{dealId}/pay/ — погасить продажу «в долг».
    /// 2026-09-15: адрес, installment_id и форма тела подтверждены живым захватом DevTools на
    /// реальной успешной оплате (status 200). installmentId нужно получить заранее через
    /// <see cref="ClientDealGetAsync"/> (поле "installments" в ответе). amount = null — полное
    /// погашение взноса (подтверждённый рабочий случай, в захваченном запросе поля amount не
    /// было вообще); конкретное значение amount — частичная оплата (не подтверждено живым
    /// захватом, может быть ненадёжно).</summary>
    Task<JsonElement> PosPayDebtAsync(string clientId, string dealId, string? installmentId, double? amount, CancellationToken ct = default);

    /// <summary>GET /api/main/clients/{clientId}/deals/{dealId}/ — карточка сделки с полями
    /// remaining_debt и installments (график платежей). 2026-09-15: подтверждено живым захватом
    /// DevTools — предыдущий плоский адрес api/main/clientdeals/{dealId}/ (без clientId) отвечал
    /// 200 и возвращал вроде бы правдоподобный remaining_debt, но БЕЗ рабочего графика платежей,
    /// из-за чего оплата (PosPayDebtAsync, которой обязательно нужен installment_id) не находила
    /// взносов и продолжала падать с "Максимум: <копейки>" даже после починки самого pay/.</summary>
    Task<JsonElement> ClientDealGetAsync(string clientId, string dealId, CancellationToken ct = default);
}
