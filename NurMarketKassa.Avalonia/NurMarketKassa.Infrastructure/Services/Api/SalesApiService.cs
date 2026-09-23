using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация продаж/корзин/возвратов поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// </summary>
public sealed class SalesApiService : ISalesApiService
{
    private readonly NurMarketApiClient _client;
    private string? _activeCartId;

    public SalesApiService(NurMarketApiClient client) => _client = client;

    public string? ActiveCartId => _activeCartId;

    public void SetActiveCartId(string? cartId) =>
        _activeCartId = string.IsNullOrWhiteSpace(cartId) ? null : cartId.Trim();

    public Task<JsonElement> PosSalesStartAsync(string? cashboxId = null, CancellationToken ct = default)
    {
        Dictionary<string, string>? body = null;
        if (!string.IsNullOrWhiteSpace(cashboxId))
            body = new Dictionary<string, string> { ["cashbox_id"] = cashboxId.Trim() };
        return PosSalesStartAsync(body, ct);
    }

    public Task<JsonElement> PosSalesStartAsync(IReadOnlyDictionary<string, string>? body, CancellationToken ct = default)
    {
        var payload = body == null
            ? new Dictionary<string, string>()
            : body.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        return _client.RequestAsync(HttpMethod.Post, "api/main/pos/sales/start/", payload, null, ct);
    }

    public Task<JsonElement> PosCartGetAsync(string cartId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        return _client.RequestAsync(HttpMethod.Get, $"api/main/pos/carts/{id}/", null, null, ct);
    }

    public Task<JsonElement> PosScanAsync(string cartId, string barcode, string? quantity = null, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["barcode"] = barcode.Trim() };
        if (!string.IsNullOrEmpty(quantity))
            body["quantity"] = quantity;
        return _client.RequestAsync(HttpMethod.Post, $"api/main/pos/sales/{id}/scan/", body, null, ct, TimeSpan.FromSeconds(22));
    }

    public Task<JsonElement> PosCartItemPatchAsync(
        string cartId,
        string itemId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/items/{i}/", body, null, ct);
    }

    public Task<JsonElement> PosCartItemDeleteAsync(string cartId, string itemId, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return _client.RequestAsync(HttpMethod.Delete, $"api/main/pos/carts/{c}/items/{i}/", null, null, ct);
    }

    public Task<JsonElement> PosCheckoutAsync(
        string cartId,
        Dictionary<string, string> body,
        CancellationToken ct = default) =>
        PosCheckoutAsync(new[] { cartId.Trim() }, body, ct);

    public async Task<JsonElement> PosCheckoutAsync(
        IReadOnlyList<string> targetIds,
        Dictionary<string, string> body,
        CancellationToken ct = default)
    {
        if (targetIds == null || targetIds.Count == 0)
            throw new ApiException("Checkout: не указан идентификатор корзины или продажи.", 400);

        var ids = targetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            throw new ApiException("Checkout: не указан идентификатор корзины или продажи.", 400);

        var timeout = TimeSpan.FromSeconds(90);
        ApiException? last404 = null;

        foreach (var rawId in ids)
        {
            var id = Uri.EscapeDataString(rawId);
            var paths = new[]
            {
                $"api/main/pos/sales/{id}/checkout/",
                $"api/main/pos/carts/{id}/checkout/",
            };

            foreach (var path in paths)
            {
                try
                {
                    return await _client.RequestAsync(HttpMethod.Post, path, body, null, ct, timeout)
                        .ConfigureAwait(false);
                }
                catch (ApiException e)
                {
                    if (e.StatusCode == 404)
                    {
                        last404 = e;
                        continue;
                    }

                    var pm = body.GetValueOrDefault("payment_method") ?? "";
                    if (e.StatusCode == 400
                        && !body.ContainsKey("cash_received")
                        && !string.Equals(pm, "cash", StringComparison.OrdinalIgnoreCase))
                    {
                        var retry = new Dictionary<string, string>(body) { ["cash_received"] = "0.00" };
                        try
                        {
                            return await _client.RequestAsync(HttpMethod.Post, path, retry, null, ct, timeout)
                                .ConfigureAwait(false);
                        }
                        catch (ApiException)
                        {
                            throw e;
                        }
                    }

                    throw;
                }
            }
        }

        if (last404 != null)
            throw last404;
        throw new ApiException("Checkout: пустой список путей", 500);
    }

    public Task<JsonElement> PosSaleReceiptAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Get, $"api/main/pos/sales/{id}/receipt/", null, null, ct);
    }

    public Task<JsonElement> PosCartPatchAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/", body, null, ct);
    }

    public Task<JsonElement> PosAddItemAsync(
        string cartId,
        string productId,
        string? quantity = null,
        string? unitPrice = null,
        string? discountTotal = null,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["product_id"] = productId.Trim() };
        if (!string.IsNullOrWhiteSpace(quantity))
            body["quantity"] = quantity.Trim();
        if (!string.IsNullOrWhiteSpace(unitPrice))
            body["unit_price"] = unitPrice.Trim();
        if (!string.IsNullOrWhiteSpace(discountTotal))
            body["discount_total"] = discountTotal.Trim();
        return _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{id}/add-item/",
            body,
            null,
            ct,
            TimeSpan.FromSeconds(28));
    }

    public Task<JsonElement> PosAddItemRawAsync(
        string cartId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var payload = body.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        return _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{id}/add-item/",
            payload,
            null,
            ct,
            TimeSpan.FromSeconds(28));
    }

    /// <summary>«Доп. услуга» (2026-09-07): произвольная строка чека без товара — тот же запрос,
    /// что делает сайт в «Интерфейсе кассира» (POST /pos/carts/{id}/custom-item/ с телом
    /// {name, price, quantity}); второй путь — на случай, если сервер отдаёт чек только как sale.
    /// Отрицательная price = «Расход» (вычитается из чека); если сервер её не примет,
    /// ApiException с его текстом дойдёт до кассира при оплате.</summary>
    public async Task<JsonElement> PosAddCustomItemAsync(
        string cartId,
        string name,
        double price,
        double quantity,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, object>
        {
            ["name"] = name.Trim(),
            ["price"] = Math.Round(price, 2),
            ["quantity"] = quantity,
        };

        foreach (var path in new[] { $"api/main/pos/carts/{id}/custom-item/", $"api/main/pos/sales/{id}/custom-item/" })
        {
            try
            {
                return await _client.RequestAsync(HttpMethod.Post, path, body, null, ct, TimeSpan.FromSeconds(28))
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next */
            }
        }

        throw new ApiException("Сервер не поддерживает добавление услуги в чек.", 404);
    }

    /// <summary><paramref name="dateFrom"/>/<paramref name="dateToExclusive"/> — серверная фильтрация
    /// по периоду. Проверено живыми запросами к app.nurcrm.kg 2026-09-21: из всех вариантов имён
    /// работают ровно <c>date_from</c> и <c>date_to</c> (у created_at__gte, created_at__date__gte и
    /// start_date ответ не меняется — сервер их игнорирует). ВАЖНО: <c>date_to</c> НЕ включает свой
    /// день — запрос date_from=21&amp;date_to=21 вернул 0 записей, хотя за 21-е число продажи есть,
    /// поэтому параметр назван Exclusive и вызывающий должен передавать день ПОСЛЕ последнего
    /// нужного. Без этой фильтрации «Финансы» скачивали всю историю магазина целиком.</summary>
    public async Task<List<JsonElement>> PosSalesListAsync(
        int page,
        int pageSize,
        string? cashboxId = null,
        CancellationToken ct = default,
        DateTime? dateFrom = null,
        DateTime? dateToExclusive = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 80);
        var pageStr = page.ToString(CultureInfo.InvariantCulture);
        var sizeStr = pageSize.ToString(CultureInfo.InvariantCulture);

        var period = new Dictionary<string, string>();
        if (dateFrom is { } df)
            period["date_from"] = df.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (dateToExclusive is { } dt)
            period["date_to"] = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Dictionary<string, string> Q(params (string Key, string Value)[] pairs)
        {
            var q = new Dictionary<string, string>();
            foreach (var (k, v) in pairs)
                q[k] = v;
            foreach (var (k, v) in period)
                q[k] = v;
            return q;
        }

        var queries = new List<Dictionary<string, string>>
        {
            Q(("page", pageStr), ("page_size", sizeStr)),
            Q(("page", pageStr), ("limit", sizeStr)),
            Q(("page", pageStr), ("page_size", sizeStr), ("ordering", "-created_at")),
            Q(("page", pageStr), ("page_size", sizeStr), ("ordering", "-id")),
        };

        if (!string.IsNullOrWhiteSpace(cashboxId))
        {
            var cb = cashboxId.Trim();
            queries.Add(Q(("page", pageStr), ("page_size", sizeStr), ("cashbox_id", cb)));
            queries.Add(Q(("page", pageStr), ("limit", sizeStr), ("cashbox_id", cb)));
        }

        var paths = new[] { "api/main/pos/sales/", "api/main/pos/sales/list/", "api/main/pos/sale/list/" };

        ApiException? last = null;
        var sawEmptySuccess = false;
        foreach (var path in paths)
        {
            foreach (var qs in queries)
            {
                try
                {
                    var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                    var root = UnwrapListRootElement(data);
                    var list = NurMarketApiClient.UnwrapList(root);
                    if (list.Count > 0)
                        return list;
                    sawEmptySuccess = true;
                }
                catch (ApiException e)
                {
                    last = e;
                    if (e.StatusCode is 404 or 405 or 410)
                        break;
                    if (e.StatusCode == 400)
                        continue;
                    throw;
                }
            }
        }

        if (sawEmptySuccess)
            return new List<JsonElement>();
        if (last != null)
            throw last;
        return new List<JsonElement>();
    }

    public async Task<JsonElement> PosSaleGetAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи (UUID или id из чека).", 400);

        var paths = new[]
        {
            $"api/main/pos/sales/{id}/",
        };

        ApiException? last = null;
        foreach (var path in paths)
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                return UnwrapDataObject(data);
            }
            catch (ApiException e)
            {
                last = e;
                if (e.StatusCode is 404 or 405 or 410)
                    continue;
                throw;
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Чек не найден.", 404);
    }

    /// <summary>GET /api/main/pos/cart-item-deletions/ — журнал удалений позиций из корзины
    /// (товар, количество, кто удалил, время). Это единственный след возвратов по уже
    /// оплаченным чекам, которые регистрируются через <see cref="PosCartItemDeletionsGetAsync"/>:
    /// у продажи (SaleDetail) в реальном API нет поля "это возврат" и нет статуса "refunded" —
    /// только "new"/"paid"/"debt"/"canceled". Запись в журнале не содержит суммы/номера чека.</summary>
    public async Task<List<JsonElement>> PosCartItemDeletionsListAsync(CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["page_size"] = "200" };
        var data = await _client.RequestAsync(HttpMethod.Get, "api/main/pos/cart-item-deletions/", null, query, ct)
            .ConfigureAwait(false);
        var root = UnwrapListRootElement(data);
        return NurMarketApiClient.UnwrapList(root);
    }

    public Task<JsonElement> PosCartItemDeletionsGetAsync(CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Get, "api/main/pos/cart-item-deletions/get/", null, null, ct);

    public async Task<bool> TryPosCartItemDeletionReturnAsync(
        string saleId,
        string? cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default)
    {
        var n = (reason ?? "").Trim();
        var qty = FormatRefundQty(line.Quantity);
        var itemId = line.LineId.Trim();
        var productId = (line.ProductId ?? "").Trim();
        var sid = saleId.Trim();

        if (itemId.Length == 0)
            return false;
        if (sid.Length == 0 && string.IsNullOrEmpty(cartId))
            return false;

        var paramSets = new List<Dictionary<string, string>>();

        void AddParams(Action<Dictionary<string, string>> fill)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            fill(d);
            paramSets.Add(d);
        }

        AddParams(d =>
        {
            d["sale_id"] = sid;
            d["item_id"] = itemId;
            d["quantity"] = qty;
            if (!string.IsNullOrEmpty(n))
            {
                d["reason"] = n;
                d["refund_reason"] = n;
            }

            if (!string.IsNullOrEmpty(productId))
                d["product_id"] = productId;
            if (!string.IsNullOrEmpty(cartId))
                d["cart_id"] = cartId!;
        });

        if (!string.IsNullOrEmpty(sid))
        {
            AddParams(d =>
            {
                d["original_sale_id"] = sid;
                d["cart_item_id"] = itemId;
                d["quantity"] = qty;
                if (!string.IsNullOrEmpty(n))
                {
                    d["reason"] = n;
                    d["refund_reason"] = n;
                }

                if (!string.IsNullOrEmpty(productId))
                    d["product_id"] = productId;
            });
        }

        if (!string.IsNullOrEmpty(cartId))
        {
            AddParams(d =>
            {
                d["cart_id"] = cartId!;
                d["item_id"] = itemId;
                d["quantity"] = qty;
                if (!string.IsNullOrEmpty(n))
                    d["reason"] = n;
            });
        }

        foreach (var query in paramSets)
        {
            try
            {
                await _client.RequestAsync(
                        HttpMethod.Get,
                        "api/main/pos/cart-item-deletions/get/",
                        null,
                        query,
                        ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 410)
            {
                // Раньше эта ошибка тихо проглатывалась — при неудаче всех наборов полей
                // не оставалось никакой зацепки, что именно сервер отверг. Текст ответа DRF
                // на 400 обычно называет ожидаемые поля напрямую.
                PosLogger.Log(
                    $"cart-item-deletions/get GET попытка отклонена ({ex.StatusCode}): {ex.Message}", "RETURN");
            }
        }

        foreach (var body in paramSets)
        {
            try
            {
                await _client.RequestAsync(
                        HttpMethod.Post,
                        "api/main/pos/cart-item-deletions/get/",
                        body,
                        null,
                        ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 410)
            {
                PosLogger.Log(
                    $"cart-item-deletions/get POST попытка отклонена ({ex.StatusCode}): {ex.Message}", "RETURN");
            }
        }

        // 2026-09-21: раньше здесь был ещё один запасной путь — POST на тот же
        // api/main/pos/sales/{id}/return/ с угаданным телом (line_id/cart_item_id/items).
        // Живой захват DevTools настоящей кнопки "Возврат" на сайте показал, что этот эндпоинт
        // не принимает и не понимает позиции вообще — рабочее тело состоит РОВНО из двух полей
        // (idempotency_key, cashbox_role) и всегда возвращает ВЕСЬ чек целиком (см.
        // PosReturnWholeSaleAsync). Если сервер молча игнорирует незнакомые поля (обычное
        // поведение DRF-сериализаторов), этот POST мог тихо "успешно" вернуть 200, реально
        // оформив возврат ВСЕЙ продажи вместо одной выбранной позиции — гораздо хуже, чем явная
        // ошибка. Убрано: если cart-item-deletions не сработал ни в одном варианте, честно
        // сообщаем о неудаче, а не рискуем случайно вернуть весь чек.
        return false;
    }

    private async Task TryRegisterCartItemDeletionAsync(
        string cartId,
        string itemId,
        string reason,
        double returnQty,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        var line = new PosRefundLineRequest
        {
            LineId = itemId,
            Title = itemId,
            Quantity = returnQty,
        };

        await TryPosCartItemDeletionReturnAsync(string.Empty, cartId, line, reason, ct).ConfigureAwait(false);
    }

    public Task<JsonElement> PosSalePatchAsync(
        string saleId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/sales/{id}/", body, null, ct);
    }

    public Task<JsonElement> PosSaleDeleteAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Delete, $"api/main/pos/sales/{id}/", null, null, ct);
    }

    /// <summary>Покупки конкретного клиента — фильтр client= у списка продаж (тот же параметр уже
    /// используется для долгов, см. PosDebtSalesAsync). Нужен карточке клиента, чтобы рядом с
    /// бонусным балансом была видна история: когда и на сколько человек покупал.</summary>
    public async Task<List<JsonElement>> PosSalesByClientAsync(
        string clientId,
        int maxPages = 5,
        CancellationToken ct = default)
    {
        var result = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(clientId))
            return result;

        for (var page = 1; page <= Math.Max(1, maxPages); page++)
        {
            var query = new Dictionary<string, string>
            {
                ["client"] = clientId.Trim(),
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "50",
                ["ordering"] = "-created_at",
            };

            var data = await _client
                .RequestAsync(HttpMethod.Get, "api/main/pos/sales/", null, query, ct)
                .ConfigureAwait(false);
            var pageItems = NurMarketApiClient.UnwrapList(data);
            if (pageItems.Count == 0)
                break;

            result.AddRange(pageItems);

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }

        return result;
    }

    public async Task<List<JsonElement>> PosDebtSalesAsync(string? clientId, CancellationToken ct = default)
    {
        var result = new List<JsonElement>();
        for (var page = 1; page <= 20; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["status"] = "debt",
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(clientId))
                query["client"] = clientId.Trim();

            var data = await _client
                .RequestAsync(HttpMethod.Get, "api/main/pos/sales/", null, query, ct)
                .ConfigureAwait(false);
            var pageItems = NurMarketApiClient.UnwrapList(data);
            if (pageItems.Count == 0)
                break;

            result.AddRange(pageItems);

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }

        return result;
    }

    // Продажа со status=debt и её "сделка" (deal) — разные сущности в NurCRM: настоящая
    // оплата (та же, что использует веб-CRM под капотом) идёт через отдельный API
    // сделок по её deal_id, а не через саму продажу. POST api/main/pos/sales/{id}/pay-debt/
    // существует в схеме API, но у сервера падает с HTTP 500 при любых данных (проверено
    // вживую: JSON/form-urlencoded, с/без cashbox, полная/частичная сумма — всегда 500).
    //
    // 2026-09-15: адрес api/main/clients/{clientId}/deals/{dealId}/pay/ и installment_id
    // подтверждены живым захватом DevTools (200 OK) — но даже так сумма всё ещё отклонялась с
    // "Максимум: <копейки>" (5.33 / 5.00 / 1.41 / 6.75 подряд, каждый раз другое случайное
    // число). Разгадка нашлась в самом захваченном теле УСПЕШНОГО запроса: поля "amount" там
    // вообще НЕ БЫЛО — веб-CRM гасит взнос целиком, просто не передавая amount. Похоже, именно
    // ПЕРЕДАЧА amount включает на сервере какую-то сломанную проверку "сумма vs что-то не то".
    // Поэтому теперь amount — необязательный: null (полное погашение конкретного взноса) просто
    // не попадает в тело запроса, как в реальном рабочем вызове; передаётся только при настоящей
    // частичной оплате (сумма меньше остатка взноса) — этот случай в живом захвате ни разу не
    // подтверждён и может по-прежнему быть ненадёжным на стороне сервера.
    public Task<JsonElement> PosPayDebtAsync(string clientId, string dealId, string? installmentId, double? amount, CancellationToken ct = default)
    {
        var client = Uri.EscapeDataString(clientId.Trim());
        var deal = Uri.EscapeDataString(dealId.Trim());
        var body = new Dictionary<string, string>
        {
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cashbox_role"] = "pos_main",
            ["note"] = "",
            ["idempotency_key"] = Guid.NewGuid().ToString(),
        };
        if (amount is { } explicitAmount)
            body["amount"] = explicitAmount.ToString("0.00", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(installmentId))
            body["installment_id"] = installmentId.Trim();
        return _client.RequestAsync(HttpMethod.Post, $"api/main/clients/{client}/deals/{deal}/pay/", body, null, ct);
    }

    /// <summary>GET /api/main/clients/{clientId}/deals/{dealId}/ — карточка сделки с
    /// remaining_debt и графиком платежей (installments — см. PosPayDebtAsync). 2026-09-15:
    /// адрес подтверждён живым захватом DevTools — плоский api/main/clientdeals/{dealId}/ (без
    /// clientId) отвечал 200 с правдоподобным remaining_debt, но без рабочего графика платежей,
    /// поэтому installment_id для оплаты найти было неоткуда.</summary>
    public Task<JsonElement> ClientDealGetAsync(string clientId, string dealId, CancellationToken ct = default)
    {
        var client = Uri.EscapeDataString(clientId.Trim());
        var deal = Uri.EscapeDataString(dealId.Trim());
        return _client.RequestAsync(HttpMethod.Get, $"api/main/clients/{client}/deals/{deal}/", null, null, ct);
    }

    public async Task<JsonElement> PosReturnCartLineAsync(
        string cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default)
    {
        var c = cartId.Trim();
        var itemId = line.LineId.Trim();
        if (c.Length == 0 || itemId.Length == 0)
            throw new ApiException("Укажите корзину и строку чека.", 400);

        var n = (reason ?? "").Trim();
        var returnQty = line.Quantity;
        var originalQty = line.OriginalQuantity > 0 ? line.OriginalQuantity : returnQty;
        var isPartial = returnQty > 0 && returnQty < originalQty - 1e-5;
        var remainingQty = originalQty - returnQty;

        await TryRegisterCartItemDeletionAsync(c, itemId, n, returnQty, ct).ConfigureAwait(false);

        if (isPartial)
        {
            var qtyStr = FormatRefundQty(remainingQty);
            var patchBodies = new List<Dictionary<string, string>>
            {
                new() { ["quantity"] = qtyStr, ["reason"] = n, ["refund_reason"] = n },
                new() { ["quantity"] = qtyStr },
            };
            ApiException? patchError = null;
            foreach (var body in patchBodies.Where(b => b.Count > 0))
            {
                try
                {
                    return await PosCartItemPatchAsync(c, itemId, body, ct).ConfigureAwait(false);
                }
                catch (ApiException ex)
                {
                    patchError = ex;
                    if (ex.StatusCode == 400)
                        continue;
                    throw;
                }
            }

            if (patchError != null)
                throw patchError;
        }

        if (!string.IsNullOrEmpty(n))
        {
            try
            {
                await PosCartItemPatchAsync(
                        c,
                        itemId,
                        new Dictionary<string, string> { ["reason"] = n, ["refund_reason"] = n, ["note"] = n },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405)
            {
                /* причина необязательна для DELETE */
            }
        }

        try
        {
            return await PosCartItemDeleteAsync(c, itemId, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 409)
        {
            return await TryMarkCartLineReturnedAsync(c, itemId, returnQty, n, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> TryMarkCartLineReturnedAsync(
        string cartId,
        string itemId,
        double returnQty,
        string reason,
        CancellationToken ct)
    {
        var qty = FormatRefundQty(returnQty);
        var patchBodies = new List<Dictionary<string, string>>
        {
            new() { ["quantity"] = "0", ["reason"] = reason, ["refund_reason"] = reason },
            new() { ["returned_quantity"] = qty, ["reason"] = reason, ["refund_reason"] = reason },
            new() { ["quantity_refunded"] = qty, ["reason"] = reason },
            new() { ["refunded_quantity"] = qty, ["reason"] = reason },
            new() { ["is_returned"] = "true", ["refund_reason"] = reason },
            new() { ["quantity"] = "0" },
        };

        ApiException? last = null;
        foreach (var body in patchBodies.Where(b => b.Count > 0))
        {
            try
            {
                return await PosCartItemPatchAsync(cartId, itemId, body, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                last = ex;
                if (ex.StatusCode == 400)
                    continue;
                throw;
            }
        }

        throw last ?? new ApiException("Не удалось оформить возврат позиции.", 502);
    }

    /// <summary>Возврат чека целиком ИЛИ отдельных его позиций — один и тот же эндпоинт, что и на
    /// сайте. Разобрано 2026-09-21 по коду market.nurcrm.kg (модуль sale-*.js, действие
    /// products/returnSale): тело собирается функцией, которая кладёт items в запрос ТОЛЬКО если
    /// массив непустой, иначе возвращается весь чек. Ключ идемпотентности там же строится как
    /// "{saleId}:partial:{sale_item_id}:{quantity}|..." — отсюда точно известен состав элемента:
    /// sale_item_id и quantity, больше ничего.
    ///
    /// До этого построчный возврат в кассе ходил в api/main/pos/cart-item-deletions/get/ и получал
    /// 404 на все варианты запроса (живой лог владельца): такого эндпоинта нет — есть
    /// api/main/pos/cart-item-deletions/ БЕЗ /get/, и это просто журнал удалений, который сайт
    /// только читает. Кассир видел «Не удалось вернуть позицию: <товар>», будто дело в товаре.</summary>
    public async Task<JsonElement> PosReturnSaleAsync(
        string saleId,
        IReadOnlyList<PosRefundLineRequest>? lines,
        CancellationToken ct = default)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);

        var body = new Dictionary<string, object>
        {
            ["idempotency_key"] = Guid.NewGuid().ToString(),
            ["cashbox_role"] = "pos_main",
        };

        if (lines is { Count: > 0 })
        {
            body["items"] = lines
                .Select(l => new Dictionary<string, object>
                {
                    ["sale_item_id"] = l.LineId.Trim(),
                    ["quantity"] = FormatRefundQty(l.Quantity),
                })
                .ToList();
        }

        return await _client.RequestDataAsync<JsonElement>(
                HttpMethod.Post,
                $"api/main/pos/sales/{Uri.EscapeDataString(id)}/return/",
                body,
                null,
                ct)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement> PosReturnWholeSaleAsync(string saleId, string? reason, CancellationToken ct = default)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);

        // 2026-09-21, живой баг владельца ("возврат с кассы сайт видит как удаление продажи"):
        // раньше здесь после необязательного PATCH с причиной вызывался PosSaleDeleteAsync —
        // настоящий HTTP DELETE продажи, поэтому в веб-CRM чек просто исчезал из истории вместо
        // того чтобы отображаться возвращённым. Подтверждено живым захватом DevTools настоящей
        // кнопки "Возврат" на сайте: она шлёт POST на тот же api/main/pos/sales/{id}/return/ с
        // телом ровно из двух полей (idempotency_key, cashbox_role), без reason/note/items —
        // причина возврата сервером здесь не принимается, поэтому дальше никуда не отправляется.
        var body = new Dictionary<string, string>
        {
            ["idempotency_key"] = Guid.NewGuid().ToString(),
            ["cashbox_role"] = "pos_main",
        };
        return await _client.RequestAsync(
                HttpMethod.Post, $"api/main/pos/sales/{Uri.EscapeDataString(id)}/return/", body, null, ct)
            .ConfigureAwait(false);
    }

    private static JsonElement UnwrapListRootElement(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
        {
            if (inner.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return inner.Clone();
        }

        return data.Clone();
    }

    private static JsonElement UnwrapDataObject(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner.Clone();
        return data.Clone();
    }

    private static string FormatRefundQty(double q)
    {
        // Без TrimEnd — см. StagingCartService: формат сам не печатает хвостовые нули, а у целого
        // количества нет точки, поэтому TrimEnd('0') резал само число и возврат 20 шт оформлялся
        // как возврат 2 шт (и на склад возвращалось 2).
        var s = q.ToString("0.####", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(s) ? "0" : s;
    }
}
