using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>Строка серверного заказа покупателя (items[] ответа customer-orders/).</summary>
public sealed record CustomerOrderItem(string? ProductId, string Name, decimal Qty, decimal Price, decimal Total, string? Status);

/// <summary>Серверный заказ покупателя (<c>api/main/customer-orders/</c>): номер, статус, клиент, строки, предоплата, «к доплате», чек выдачи и версия.</summary>
public sealed class CustomerOrderDto
{
    public string Id { get; set; } = "";
    public string Number { get; set; } = "";
    /// <summary>draft → confirmed → reserved / waiting_supply → ready → delivered → closed, а также canceled.</summary>
    public string Status { get; set; } = "";
    public string? ClientId { get; set; }
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public string Note { get; set; } = "";
    public List<CustomerOrderItem> Items { get; set; } = new();
    /// <summary>Предоплата, принятая по заказу (prepayment.amount).</summary>
    public decimal PrepaymentAmount { get; set; }
    /// <summary>Среди движений предоплаты есть возврат (kind = refund) — предоплата возвращена клиенту.</summary>
    public bool PrepaymentRefunded { get; set; }
    public decimal Total { get; set; }
    public decimal? LeftToPay { get; set; }
    public string? SaleId { get; set; }
    public string? Version { get; set; }

    /// <summary>Разбор ответа сервера. Терпимый: незнакомые поля и неверные типы — пусто/ноль; без id заказа — null.</summary>
    public static CustomerOrderDto? Parse(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object)
            return null;
        var id = Str(o, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var dto = new CustomerOrderDto
        {
            Id = id!,
            Number = Str(o, "number") ?? "",
            Status = (Str(o, "status") ?? "").Trim().ToLowerInvariant(),
            ClientId = Str(o, "client"),
            CustomerName = Str(o, "customer_name") ?? "",
            CustomerPhone = Str(o, "customer_phone") ?? "",
            Note = Str(o, "note") ?? "",
            Total = Dec(o, "total") ?? 0m,
            LeftToPay = Dec(o, "left_to_pay"),
            // sale: id чека выдачи (строка) или вложенный объект {id}; sale_id — в ответе deliver/.
            SaleId = Str(o, "sale_id") ?? (o.TryGetProperty("sale", out var sale)
                ? sale.ValueKind == JsonValueKind.Object ? Str(sale, "id") : sale.ValueKind == JsonValueKind.String ? sale.GetString() : null
                : null),
            Version = o.TryGetProperty("version", out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null,
        };
        if (Str(o, "due_date") is { Length: >= 10 } due && DateTime.TryParse(due[..10], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            dto.DueDate = day.Date;
        if (o.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object)
                    continue;
                var qty = Dec(it, "qty") ?? Dec(it, "quantity") ?? 0m;
                var price = Dec(it, "price") ?? 0m;
                string? pid = it.TryGetProperty("product", out var p)
                    ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Object ? Str(p, "id") : null
                    : null;
                dto.Items.Add(new CustomerOrderItem(pid, Str(it, "product_name") ?? Str(it, "name") ?? "", qty, price, Dec(it, "total") ?? Math.Round(qty * price, 2), Str(it, "status")));
            }
        }
        if (o.TryGetProperty("prepayment", out var pre) && pre.ValueKind == JsonValueKind.Object)
        {
            dto.PrepaymentAmount = Dec(pre, "amount") ?? 0m;
            if (pre.TryGetProperty("payments", out var pays) && pays.ValueKind == JsonValueKind.Array)
                dto.PrepaymentRefunded = pays.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && string.Equals(Str(x, "kind"), "refund", StringComparison.OrdinalIgnoreCase));
        }
        return dto;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static decimal? Dec(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
                JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null,
                _ => null,
            }
            : null;
}

/// <summary>Итог выдачи заказа: чек (sale_id), сдача и обновлённый заказ.</summary>
public sealed record CustomerOrderDeliverResult(string? SaleId, decimal Change, CustomerOrderDto? Order);

/// <summary>Серверные операции заказа покупателя, нужные окну «Заказы под клиента» (подменяются в проверках).</summary>
public interface ICustomerOrdersApi
{
    /// <summary>Открытые заказы (confirmed, reserved, waiting_supply, ready) — для списка кассы.</summary>
    Task<IReadOnlyList<CustomerOrderDto>> ListOpenAsync(CancellationToken ct = default);

    /// <summary>Карточка заказа (<c>GET customer-orders/&lt;id&gt;/</c>): обновить копию, если заказ пропал из списка открытых (выдан или отменён на другом устройстве). null — заказа нет (404).</summary>
    Task<CustomerOrderDto?> GetAsync(string orderId, CancellationToken ct = default);

    /// <summary>Выдача: <c>POST customer-orders/&lt;id&gt;/deliver/</c> — чек пробивает сервер (предоплата строкой «Зачёт», остаток выбранным способом). Не обычный чек кассы: иначе 409 reserved_stock.</summary>
    Task<CustomerOrderDeliverResult> DeliverAsync(string orderId, string method, decimal? received, CancellationToken ct = default);

    /// <summary>Отмена: <c>POST customer-orders/&lt;id&gt;/cancel/</c> — резерв снимается, предоплата возвращается из кассы ТЕКУЩЕЙ смены тем же способом (строки refund).</summary>
    Task<CustomerOrderDto?> CancelAsync(string orderId, CancellationToken ct = default);
}

/// <summary>
/// 2026-11-20 (ответ Умара 20.11, В.4 «Заказ под клиента с предоплатой»): клиент серверного заказа покупателя api/main/customer-orders/. Этап 1 переноса: список, выдача (deliver) и отмена (cancel) — деньги в смене проводит сервер.
/// Все POST — с Idempotency-Key (повтор того же ключа → 200 и replayed: true). Создание, предоплата, резерв и правка с If-Match — этап 2 (docs/Отчеты/«Заказ под клиента — перенос на сервер, план (20.11.2026).md»).
/// </summary>
public sealed class CustomerOrdersApi : ICustomerOrdersApi
{
    private const string Root = "api/main/customer-orders/";
    private readonly NurMarketApiClient _client;

    public CustomerOrdersApi(NurMarketApiClient client) => _client = client;

    /// <summary>Серверные заказы для кассы доступны только с признаком сервера customer_orders (capabilities); без токена — нет.</summary>
    public static ICustomerOrdersApi? TryCreate()
    {
        if (!Api.ServerCapabilities.Has(Api.ServerCapabilities.CustomerOrders))
            return null;
        return ServerLoyalty.ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? new CustomerOrdersApi(api) : null;
    }

    public async Task<IReadOnlyList<CustomerOrderDto>> ListOpenAsync(CancellationToken ct = default)
    {
        var result = new List<CustomerOrderDto>();
        for (var page = 1; page <= 5; page++)
        {
            var query = new Dictionary<string, string> { ["status"] = "confirmed,reserved,waiting_supply,ready", ["page"] = page.ToString(CultureInfo.InvariantCulture), ["page_size"] = "100" };
            var data = await _client.RequestAsync(HttpMethod.Get, Root, null, query, ct).ConfigureAwait(false);
            var results = data.ValueKind == JsonValueKind.Array ? data
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
            if (results.ValueKind != JsonValueKind.Array)
                break;
            foreach (var item in results.EnumerateArray())
                if (CustomerOrderDto.Parse(item) is { } dto)
                    result.Add(dto);
            var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString());
            if (!hasNext)
                break;
        }
        return result;
    }

    public async Task<CustomerOrderDto?> GetAsync(string orderId, CancellationToken ct = default)
    {
        try
        {
            var data = await _client.RequestAsync(HttpMethod.Get, Root + Uri.EscapeDataString(orderId.Trim()) + "/", null, null, ct).ConfigureAwait(false);
            return CustomerOrderDto.Parse(data);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<CustomerOrderDeliverResult> DeliverAsync(string orderId, string method, decimal? received, CancellationToken ct = default)
    {
        var payment = new Dictionary<string, object?> { ["method"] = method };
        if (received is { } money)
            payment["received"] = money.ToString("0.##", CultureInfo.InvariantCulture);
        var body = new Dictionary<string, object?> { ["payment"] = payment };
        // Ключ повтора — на попытку (не на заказ): отказ 409 shift_not_open не должен «залипнуть» при повторе после открытия смены.
        var data = await _client.PostWithIdempotencyKeyAsync(Root + Uri.EscapeDataString(orderId.Trim()) + "/deliver/", body, "co-deliver-" + Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        var change = 0m;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("change", out var ch))
            change = ch.ValueKind switch
            {
                JsonValueKind.Number => ch.TryGetDecimal(out var d) ? d : 0m,
                JsonValueKind.String => decimal.TryParse(ch.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0m,
                _ => 0m,
            };
        string? saleId = null;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sale_id", out var sid))
            saleId = sid.ValueKind == JsonValueKind.String ? sid.GetString() : sid.ValueKind == JsonValueKind.Number ? sid.GetRawText() : null;
        var order = CustomerOrderDto.Parse(data) ?? (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("order", out var o) ? CustomerOrderDto.Parse(o) : null);
        return new CustomerOrderDeliverResult(saleId ?? order?.SaleId, change, order);
    }

    public async Task<CustomerOrderDto?> CancelAsync(string orderId, CancellationToken ct = default)
    {
        var data = await _client.PostWithIdempotencyKeyAsync(Root + Uri.EscapeDataString(orderId.Trim()) + "/cancel/", new Dictionary<string, object?>(), "co-cancel-" + Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        return CustomerOrderDto.Parse(data) ?? (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("order", out var o) ? CustomerOrderDto.Parse(o) : null);
    }

    /// <summary>Номер заказа сервера (число из «CO-000123» или «123») — для показа рядом с локальными; нет цифр — 0.</summary>
    public static int NumberOf(string? serverNumber)
    {
        var m = Regex.Match(serverNumber ?? "", @"(\d+)\s*$");
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}

/// <summary>Понятные кассиру тексты ошибок серверного заказа покупателя (5 языков): коды из ответа Умара 20.11, В.4 и отказ 403 на отмене заказа с предоплатой.</summary>
public static class CustomerOrderErrors
{
    public const string ShiftNotOpen = "shift_not_open";
    public const string OrderAlreadyDelivered = "order_already_delivered";

    public static bool IsAlreadyDelivered(Exception? ex) => ServerErrorCodes.Is(ex, OrderAlreadyDelivered);

    /// <summary>Текст для окна. <paramref name="cancel"/> — ошибка на отмене (там 403 значит «нет права возвращать предоплату»).</summary>
    public static string Text(Exception ex, bool cancel = false)
    {
        if (ex is not ApiException api)
            return ServerOutageMonitor.FriendlyError(ex);
        var code = ServerErrorCodes.Code(ex);
        return code switch
        {
            ShiftNotOpen => Tr.T("Нет открытой смены. Откройте смену и повторите: предоплата и выдача проходят через кассу смены.",
                "Ачык смена жок. Сменаны ачып, кайра аракет кылыңыз: алдын ала төлөм жана берүү смена кассасы аркылуу өтөт.",
                "No open shift. Open a shift and try again: the prepayment and the hand-over go through the shift cash.",
                "Açık vardiya yok. Vardiya açıp tekrar deneyin: ön ödeme ve teslim vardiya kasasından geçer.",
                "Ochiq smena yo'q. Smenani ochib, qayta urinib ko'ring: oldindan to'lov va topshirish smena kassasi orqali o'tadi."),
            "order_not_confirmed" => Tr.T("Заказ ещё не подтверждён на сервере — выдать его нельзя.", "Заказ серверде али ырасталган жок — аны берүүгө болбойт.", "The order is not confirmed on the server yet — it cannot be handed over.", "Sipariş sunucuda henüz onaylanmadı — teslim edilemez.", "Buyurtma serverda hali tasdiqlanmagan — uni topshirib bo'lmaydi."),
            "order_waiting_supply" => Tr.T("Товар по заказу ещё не пришёл от поставщика — выдать нельзя.", "Заказ боюнча товар жеткирүүчүдөн али келген жок — берүүгө болбойт.", "The goods for this order have not arrived from the supplier yet — it cannot be handed over.", "Siparişin ürünleri tedarikçiden henüz gelmedi — teslim edilemez.", "Buyurtma tovari yetkazib beruvchidan hali kelmagan — topshirib bo'lmaydi."),
            "order_closed" => Tr.T("Заказ уже закрыт.", "Заказ мурунтан жабылган.", "The order is already closed.", "Sipariş zaten kapatıldı.", "Buyurtma allaqachon yopilgan."),
            OrderAlreadyDelivered => Tr.T("Заказ уже выдан.", "Заказ мурунтан берилген.", "The order has already been handed over.", "Sipariş zaten teslim edildi.", "Buyurtma allaqachon topshirilgan."),
            "order_reserved" => Tr.T("Товар заказа зарезервирован — действие недоступно. Обновите список заказов.", "Заказдын товары резервде — аракет жеткиликсиз. Заказдар тизмесин жаңыртыңыз.", "The order's goods are reserved — the action is unavailable. Refresh the order list.", "Siparişin ürünleri rezerve — işlem kullanılamıyor. Sipariş listesini yenileyin.", "Buyurtma tovari zaxirada — amal mavjud emas. Buyurtmalar ro'yxatini yangilang."),
            "prepayment_exceeds_total" => Tr.T("Предоплата больше суммы заказа.", "Алдын ала төлөм заказдын суммасынан чоң.", "The prepayment is larger than the order total.", "Ön ödeme sipariş tutarından büyük.", "Oldindan to'lov buyurtma summasidan katta."),
            "version_conflict" => Tr.T("Заказ изменили на другом устройстве. Обновите список и повторите.", "Заказ башка түзмөктө өзгөртүлдү. Тизмени жаңыртып, кайра аракет кылыңыз.", "The order was changed on another device. Refresh the list and try again.", "Sipariş başka bir cihazda değiştirildi. Listeyi yenileyip tekrar deneyin.", "Buyurtma boshqa qurilmada o'zgartirildi. Ro'yxatni yangilab, qayta urinib ko'ring."),
            "reserved_stock" => Tr.T("Товар зарезервирован под заказ — выдавайте через «Выдать» в заказе, а не обычным чеком.", "Товар заказга резервделген — кадимки чек менен эмес, заказдагы «Берүү» аркылуу бериңиз.", "The goods are reserved for the order — hand them over via “Hand over” in the order, not with a normal receipt.", "Ürün siparişe rezerve — normal fişle değil, siparişteki «Teslim et» ile verin.", "Tovar buyurtma uchun zaxirada — oddiy chek bilan emas, buyurtmadagi «Topshirish» orqali bering."),
            _ when api.StatusCode == 403 && cancel => Tr.T("Отменить заказ с предоплатой может владелец, администратор или кассир с правом «Касса». Обратитесь к руководителю.",
                "Алдын ала төлөмү бар заказды ээси, администратор же «Касса» укугу бар кассир жокко чыгара алат. Жетекчиге кайрылыңыз.",
                "An order with a prepayment can be cancelled by the owner, an administrator or a cashier with the “Cashier” right. Ask your manager.",
                "Ön ödemeli siparişi sahip, yönetici veya «Kasa» yetkisi olan kasiyer iptal edebilir. Yöneticinize başvurun.",
                "Oldindan to'lovli buyurtmani egasi, administrator yoki «Kassa» huquqi bor kassir bekor qila oladi. Rahbarga murojaat qiling."),
            _ when api.StatusCode == 404 => Tr.T("Заказ не найден на сервере. Обновите список.", "Заказ серверден табылган жок. Тизмени жаңыртыңыз.", "The order was not found on the server. Refresh the list.", "Sipariş sunucuda bulunamadı. Listeyi yenileyin.", "Buyurtma serverda topilmadi. Ro'yxatni yangilang."),
            _ => ServerOutageMonitor.FriendlyError(ex, showServerText: true),
        };
    }
}
