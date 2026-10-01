using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Локальный черновик активного чека после откладывания: сервер не держит «вторую» корзину,
/// поэтому новый чек живёт в памяти до оплаты.
/// </summary>
public static class StagingCartService
{
    public static void StartEmpty(ICartService cart)
    {
        var root = new JsonObject
        {
            ["items"] = new JsonArray(),
            ["is_staging"] = true,
        };
        using var doc = JsonDocument.Parse(root.ToJsonString());
        cart.SetCart(doc.RootElement);
    }

    /// <summary>
    /// Перед оплатой: sales/start → очистка серверной корзины → перенос позиций из снимка.
    /// </summary>
    public static async Task MaterializeSnapshotOnServerAsync(
        ISalesApiService api,
        ICartService cart,
        string? cashboxId,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        if (!cart.HasCart)
            throw new ApiException(Tr.T("Нет данных чека для оплаты.", "Төлөм үчүн чектин маалыматы жок.",
                "No receipt data to pay for.", "Ödeme için fiş verisi yok.", "To'lov uchun chek ma'lumotlari yo'q."), 400);

        var snapshotJson = cart.Root.GetRawText();
        var wasStaging = cart.IsStaging;

        if (!force && !wasStaging && cart.CanRefresh)
            return;

        var lineCount = CartDisplayHelper.EnumerateItems(cart.Root).Count();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PosLogger.Log($"STAGING materialize start: lines={lineCount}, wasStaging={wasStaging}, force={force}", "PAYMENT");

        cart.Clear();

        var serverCart = await api.PosSalesStartAsync(
            string.IsNullOrWhiteSpace(cashboxId) ? null : cashboxId,
            cancellationToken).ConfigureAwait(false);
        cart.SetCart(serverCart);
        PosLogger.Log($"STAGING materialize: sales/start done at {sw.ElapsedMilliseconds}ms", "PAYMENT");

        if (!cart.CanRefresh || string.IsNullOrEmpty(cart.CartId))
            throw new ApiException(Tr.T("Не удалось получить серверную корзину для оплаты.",
                "Төлөм үчүн сервердеги себетти алуу мүмкүн болгон жок.", "Could not get the server cart for payment.",
                "Ödeme için sunucu sepeti alınamadı.", "To'lov uchun server savatini olib bo'lmadi."), 409);

        var cartId = cart.CartId!;
        await CartSaleSessionHelper.EnsureServerCartEmptyAsync(api, cart, cancellationToken).ConfigureAwait(false);
        DeferredCartsStore.ReleaseServerCartId(cartId);
        PosLogger.Log($"STAGING materialize: ensure-empty done at {sw.ElapsedMilliseconds}ms", "PAYMENT");

        await PushItemsFromSnapshotAsync(api, cartId, snapshotJson, cancellationToken).ConfigureAwait(false);
        PosLogger.Log($"STAGING materialize: {lineCount} item(s) pushed at {sw.ElapsedMilliseconds}ms", "PAYMENT");

        await ApplyOrderDiscountFromSnapshotAsync(api, cartId, snapshotJson, cancellationToken, cart.Root).ConfigureAwait(false);
        PosLogger.Log($"STAGING materialize: discount applied at {sw.ElapsedMilliseconds}ms", "PAYMENT");

        // 2026-09-29: названия строк — те, что видел кассир (варианты по доп. штрихкоду), см.
        // WithSnapshotLineNames; суммы и количества — как у сервера.
        cart.SetCart(WithSnapshotLineNames(
            await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false), snapshotJson));
        PosLogger.Log(
            $"STAGING: снимок перенесён на сервер cartId={cartId}, total={sw.ElapsedMilliseconds}ms for {lineCount} line(s)",
            "PAYMENT");
    }

    /// <summary>internal, а не private: ровно эту же выгрузку снимка на сервер делает и повтор
    /// офлайн-чека (SyncService.ReplayOfflineSaleAsync). Там жила вторая, урезанная копия, которая
    /// молча теряла строки «Доп. услуга» (у них нет product_id) и sale_package_id у поштучной
    /// продажи из пачки. Одна реализация на оба пути — чтобы они больше не расходились.</summary>
    internal static async Task PushItemsFromSnapshotAsync(
        ISalesApiService api,
        string cartId,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);

        // 2026-09-29: строки одного товара по одной цене (основной штрихкод и варианты по доп.
        // штрихкодам — в чеке кассы это разные строки, см. ReceiptSnapshotCartEditor.AddProduct)
        // уходят на сервер одной позицией с общим количеством и общей скидкой — как и до
        // разделения строк. Сервер сам сливает add-item одного товара в одну строку, и вторая
        // скидка строки могла бы заменить первую.
        var groups = new Dictionary<string, (double Qty, double Discount)>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in CartDisplayHelper.EnumerateItems(doc.RootElement))
        {
            if (PushGroupKey(it) is not { } groupKey)
                continue;
            groups.TryGetValue(groupKey, out var sum);
            groups[groupKey] = (sum.Qty + CartDisplayHelper.LineQuantity(it),
                sum.Discount + CartDisplayHelper.EffectiveLineDiscount(it));
        }

        var pushedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in CartDisplayHelper.EnumerateItems(doc.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var productId = CartDisplayHelper.TryProductId(it);
            if (string.IsNullOrEmpty(productId))
            {
                // «Доп. услуга» (2026-09-07): строка без товара уходит на сервер отдельным
                // запросом custom-item (name/price/quantity) — так же делает сайт.
                if (CartDisplayHelper.IsCustomLine(it))
                {
                    await api.PosAddCustomItemAsync(
                        cartId,
                        CartDisplayHelper.ItemName(it),
                        CartDisplayHelper.UnitPrice(it),
                        CartDisplayHelper.LineQuantity(it),
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var qty = CartDisplayHelper.LineQuantity(it);
            var disc = CartDisplayHelper.OptionalDiscountTotalParam(it);
            // 2026-09-29: вторая и следующие строки того же товара уже ушли первой (см. groups выше).
            if (PushGroupKey(it) is { } groupKey)
            {
                if (!pushedGroups.Add(groupKey))
                    continue;
                var group = groups[groupKey];
                qty = group.Qty;
                disc = group.Discount > 1e-6 ? CartDisplayHelper.FormatMoney(group.Discount) : null;
            }

            // Без TrimEnd: формат "0.###" и так не печатает хвостовые нули, а у ЦЕЛОГО количества
            // он не печатает и точку — поэтому TrimEnd('0') откусывал нули самого числа и
            // 10.000 кг уходило на сервер как "1" (живой баг: чек на 1000 сом проводился как 100).
            var qtyStr = CartDisplayHelper.LineMustWeigh(it)
                ? qty.ToString("0.###", CultureInfo.InvariantCulture)
                : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);
            var unitPrice = CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(it));
            var salePackageId = CartDisplayHelper.SalePackageId(it);
            var serverVariantId = CartDisplayHelper.ServerVariantId(it);

            try
            {
                if (!string.IsNullOrWhiteSpace(serverVariantId))
                {
                    // 2026-10-01, магазин одежды: вариант (размер/цвет) — сервер сам ставит цену и
                    // списывает остаток варианта (проверено на тестовом аккаунте: строка получает
                    // variant, variant_size, variant_color и цену варианта).
                    var variantBody = new Dictionary<string, string>
                    {
                        ["product_id"] = productId,
                        ["quantity"] = qtyStr,
                        ["variant_id"] = serverVariantId,
                    };
                    if (!string.IsNullOrWhiteSpace(disc))
                        variantBody["discount_total"] = disc;
                    await api.PosAddItemRawAsync(cartId, variantBody, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(salePackageId))
                {
                    // Поштучная продажа из упаковки: сайт передаёт ID пачки вместо unit_price —
                    // сервер сам подставляет цену пачки и не спотыкается о проверку "цена не
                    // ниже закупочной" (та применяется только при ручном оверрайде unit_price
                    // на базовом товаре, см. CartDisplayHelper.SalePackageId).
                    var rawBody = new Dictionary<string, string>
                    {
                        ["product_id"] = productId,
                        ["quantity"] = qtyStr,
                        ["sale_package_id"] = salePackageId,
                    };
                    if (!string.IsNullOrWhiteSpace(disc))
                        rawBody["discount_total"] = disc;
                    await api.PosAddItemRawAsync(cartId, rawBody, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await api.PosAddItemAsync(cartId, productId, qtyStr, unitPrice, disc, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (ApiException ex)
            {
                // Сервер отвечает общей ошибкой поля ("unit_price: ...") без указания, какая
                // именно позиция чека её вызвала — кассиру приходится угадывать по составу
                // корзины. Добавляем название товара впереди сообщения сервера.
                var itemName = CartDisplayHelper.ItemName(it);
                throw new ApiException(
                    string.IsNullOrWhiteSpace(itemName) ? ex.Message : $"«{itemName}»: {ex.Message}",
                    ex.StatusCode,
                    ex.Payload);
            }
        }
    }

    /// <summary>2026-09-29: ключ «одна позиция на сервере» для строки чека с товаром: товар, цена за
    /// единицу и упаковка поштучной продажи. null — строка без товара («Доп. услуга»).</summary>
    private static string? PushGroupKey(JsonElement it)
    {
        var productId = CartDisplayHelper.TryProductId(it);
        if (string.IsNullOrEmpty(productId))
            return null;
        return productId.Trim() + "|"
               + CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(it)) + "|"
               + (CartDisplayHelper.SalePackageId(it) ?? "") + "|"
               + (CartDisplayHelper.ServerVariantId(it) ?? "");
    }

    /// <summary>2026-09-29, жалоба магазина «доп. штрихкод резко переходит на основное»: после
    /// переноса чека на сервер касса брала строки серверной корзины, а сервер вариантов не знает —
    /// «Asu Клубничный» становилась «Asu» и на экране (если оплата не прошла), и в напечатанном
    /// чеке. Название, которое видел кассир, возвращается в строку серверной корзины, когда ей
    /// соответствует одна строка кассы (тот же товар и цена). Если строк несколько (основной
    /// товар и вариант), сервер держит их одной строкой — у неё остаётся название сервера.
    /// Суммы и количества не трогаются.</summary>
    internal static JsonElement WithSnapshotLineNames(JsonElement serverCart, string? snapshotJson)
    {
        if (serverCart.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(snapshotJson))
            return serverCart;

        try
        {
            var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            using (var snapshot = JsonDocument.Parse(snapshotJson))
            {
                foreach (var it in CartDisplayHelper.EnumerateItems(snapshot.RootElement))
                {
                    if (PushGroupKey(it) is not { } key)
                        continue;
                    if (!names.TryGetValue(key, out var set))
                        names[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(CartDisplayHelper.ItemName(it));
                }
            }

            if (names.Count == 0 || JsonNode.Parse(serverCart.GetRawText()) is not JsonObject root
                || root["items"] is not JsonArray items)
                return serverCart;

            var changed = false;
            foreach (var node in items)
            {
                if (node is not JsonObject line)
                    continue;
                using var lineDoc = JsonDocument.Parse(line.ToJsonString());
                if (PushGroupKey(lineDoc.RootElement) is not { } key
                    || !names.TryGetValue(key, out var set) || set.Count != 1)
                    continue;
                var name = set.First();
                if (string.IsNullOrWhiteSpace(name) || name == "—"
                    || string.Equals(CartDisplayHelper.ItemName(lineDoc.RootElement), name, StringComparison.Ordinal))
                    continue;

                // CartDisplayHelper.ItemName читает product{}.name, затем product_snapshot{}.name,
                // затем product_name — ставим во все, что есть у строки.
                line["product_name"] = name;
                if (line.ContainsKey("display_name"))
                    line["display_name"] = name;
                if (line["product"] is JsonObject product)
                    product["name"] = name;
                if (line["product_snapshot"] is JsonObject productSnapshot)
                    productSnapshot["name"] = name;
                changed = true;
            }

            if (!changed)
                return serverCart;
            using var result = JsonDocument.Parse(root.ToJsonString());
            return result.RootElement.Clone();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Названия строк чека не перенесены в серверную корзину: {ex.Message}", "WARNING");
            return serverCart;
        }
    }

    /// <summary>Есть ли в корзине непустое (не нулевое) значение денежного поля.
    /// Сервер отдаёт такие поля строками вида «10.00», иногда числом.</summary>
    private static bool HasNonZeroDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return false;

        decimal value;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                value = element.GetDecimal();
                break;
            case JsonValueKind.String:
                if (!decimal.TryParse(
                        OrderDiscountHelper.NormalizeDecimal(element.GetString() ?? ""),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        return Math.Abs(value) > 0.005m;
    }

    /// <summary>Скидка на чек, только что проставленная на серверной корзине при
    /// материализации: идентификатор корзины и отправленное тело PATCH. Нужна, чтобы оплата
    /// не отправила то же самое второй раз (сервер отвечает на это 500).
    ///
    /// Статическое поле, а не параметр: материализация вызывается из трёх мест (оплата,
    /// отложенные чеки, повтор офлайн-очереди), и протаскивать возврат через все три ради
    /// одной проверки — больше кода и больше шансов забыть в четвёртом месте. Касса работает
    /// с одной корзиной за раз, поэтому гонки здесь нет; привязка к cartId страхует от того,
    /// что признак «прилипнет» к следующему чеку.</summary>
    internal static (string CartId, Dictionary<string, string> Body)? LastAppliedOrderDiscount { get; private set; }

    internal static async Task ApplyOrderDiscountFromSnapshotAsync(
        ISalesApiService api,
        string cartId,
        string snapshotJson,
        CancellationToken cancellationToken,
        JsonElement? currentServerCart = null)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
        var root = doc.RootElement;
        var body = new Dictionary<string, string>();

        if (root.TryGetProperty("order_discount_percent", out var pct))
        {
            var value = pct.ValueKind == JsonValueKind.Number
                ? pct.GetDouble().ToString(CultureInfo.InvariantCulture)
                : pct.GetString();
            if (!OrderDiscountHelper.IsEmptyOrZeroLike(value))
                body["order_discount_percent"] = OrderDiscountHelper.NormalizeDecimal(value!);
        }

        if (root.TryGetProperty("order_discount_total", out var total))
        {
            var value = total.ValueKind == JsonValueKind.Number
                ? total.GetDouble().ToString(CultureInfo.InvariantCulture)
                : total.GetString();
            if (!OrderDiscountHelper.IsEmptyOrZeroLike(value))
                body["order_discount_total"] = OrderDiscountHelper.NormalizeDecimal(value!);
        }

        // Сервер принимает ЛИБО процент, ЛИБО сумму («Выберите либо фиксированную скидку, либо
        // скидку в процентах»). В снимке могут оказаться оба поля сразу — тогда PATCH уходил с
        // двумя ключами и отклонялся целиком, то есть скидка на сервер не попадала вовсе.
        body = OrderDiscountHelper.SanitizePatchBody(body);

        // Серверная корзина ПЕРЕИСПОЛЬЗУЕТСЯ, а её очистка (CartSaleSessionHelper.
        // EnsureServerCartEmptyAsync) удаляет только позиции — поля скидки она не трогает.
        // Поэтому скидка от ПРЕДЫДУЩЕГО чека оставалась на корзине и молча применялась к
        // следующему: касса показывала кассиру полную сумму, а сервер записывал продажу со
        // старой скидкой — недобор денег, которого никто не видит.
        // Прочитано с сервера 2026-09-22: корзина с 0 позиций несла order_discount_percent=10.00.
        // Гасим то, чего в текущем чеке нет. По одному ключу за запрос: два ключа сразу сервер
        // не принимает («Выберите либо фиксированную скидку, либо скидку в процентах»).
        if (currentServerCart is { ValueKind: JsonValueKind.Object } serverCart)
        {
            foreach (var field in new[] { "order_discount_percent", "order_discount_total" })
            {
                if (body.ContainsKey(field))
                    continue;
                if (!HasNonZeroDecimal(serverCart, field))
                    continue;

                // ВАЖНО: снятие чужой скидки — страховка, а не условие продажи. Если сервер
                // на это ответит ошибкой, продажу ронять нельзя: чек без скидки корректен и
                // должен пробиться. Ровно на этом 2026-09-22 сломалась оплата чека БЕЗ скидки
                // (500 прилетал из этого запроса и убивал всю оплату).
                try
                {
                    await api.PosCartPatchAsync(
                        cartId,
                        new Dictionary<string, string> { [field] = "0" },
                        cancellationToken).ConfigureAwait(false);
                    PosLogger.Log($"Корзина переиспользована: снята чужая скидка {field}.", "PAYMENT");
                }
                catch (Exception ex)
                {
                    PosLogger.Log(
                        $"Не удалось снять чужую скидку {field} (продажу не прерываем): {ex.GetType().Name}: {ex.Message}",
                        "WARNING");
                }
            }
        }

        if (body.Count == 0)
        {
            LastAppliedOrderDiscount = null;
            return;
        }

        PosLogger.Log(
            "Скидка на чек из снимка: " + string.Join(", ", body.Select(kv => kv.Key + "=" + kv.Value)),
            "PAYMENT");

        try
        {
            await api.PosCartPatchAsync(cartId, body, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode >= 500 && body.ContainsKey("order_discount_total"))
        {
            // Скидка ФИКСИРОВАННОЙ СУММОЙ (так уходят списанные бонусы) валит сервер пятисоткой,
            // а кассир видит только «Оплата не прошла» и не может пробить чек. Проверено
            // 2026-09-22: тот же чек тем же телом запроса под ВЛАДЕЛЬЦЕМ проходит с 200, под
            // кассиром — 500. У компании выставлен потолок скидки max_discount_percent = 10 %;
            // на владельца он не действует, на кассира действует, и проверка потолка на пути
            // «скидка суммой» у сервера падает даже когда скидка НИЖЕ потолка (2 сома с чека
            // на 39 — это 5,13 %).
            //
            // Поэтому ту же самую сумму раскладываем по строкам чека как ПОСТРОЧНУЮ ДЕНЕЖНУЮ
            // скидку. Это по-прежнему деньги, а не процент (покупателю обещано «минус 8 сом»),
            // и это другое поле сервера, проверку потолка оно не задевает.
            PosLogger.Log(
                $"Сервер отклонил скидку суммой ({ex.StatusCode}): {DescribePayload(ex.Payload)}",
                "WARNING");

            if (!await TrySpreadDiscountOverLinesAsync(
                    api, cartId, body["order_discount_total"], cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            // Скидки на самой корзине теперь нет — она разложена по строкам.
            body = new Dictionary<string, string>();
        }

        // Запоминаем, ЧТО именно уже проставлено на сервере для этой корзины. Оплата ниже
        // (PosCheckoutService.ApplyOrderDiscountAsync) иначе отправит ровно то же значение
        // второй раз, а этого сервер не переживает — отвечает 500, и кассир видит
        // «Проверьте параметры скидки» на совершенно нормальной скидке.
        LastAppliedOrderDiscount = body.Count == 0
            ? null
            : (cartId, new Dictionary<string, string>(body));
    }

    /// <summary>Раскладывает скидку на чек по строкам корзины как построчную ДЕНЕЖНУЮ скидку —
    /// пропорционально суммам строк, с точностью до копейки. Возвращает false, если разложить
    /// ровно эту сумму не получилось: недобрать или перебрать копейки нельзя, кассир уже назвал
    /// покупателю итог.</summary>
    private static async Task<bool> TrySpreadDiscountOverLinesAsync(
        ISalesApiService api,
        string cartId,
        string amountText,
        CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(
                OrderDiscountHelper.NormalizeDecimal(amountText),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var amount)
            || amount <= 0m)
        {
            return false;
        }

        JsonElement cart;
        try
        {
            cart = await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось прочитать чек для раскладки скидки: {ex.Message}", "WARNING");
            return false;
        }

        if (cart.ValueKind != JsonValueKind.Object
            || !cart.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var ids = new List<string>();
        var room = new List<decimal>();      // сколько денег на строке ещё можно скинуть
        var already = new List<decimal>();   // что на строке уже скинуто
        var totalRoom = 0m;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
                continue;
            var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!TryReadDecimal(item, "line_total", out var lineTotal))
                continue;
            // 2026-09-28: на строку с акцией товара NurCRM сервер скидку кассира не принимает
            // (PATCH discount_total молча остаётся скидкой по акции) — долю туда не кладём.
            if (PromotionRules.LineHasRules(item))
                continue;

            TryReadDecimal(item, "line_discount", out var lineDiscount);
            var free = lineTotal - lineDiscount;
            if (free <= 0m)
                continue;

            ids.Add(id!);
            room.Add(free);
            already.Add(lineDiscount);
            totalRoom += free;
        }

        if (ids.Count == 0 || totalRoom < amount)
        {
            PosLogger.Log(
                $"Скидку {amount:0.##} не на что разложить: в чеке свободно {totalRoom:0.##}.",
                "WARNING");
            return false;
        }

        var shares = new decimal[ids.Count];
        var assigned = 0m;
        for (var i = 0; i < ids.Count; i++)
        {
            shares[i] = Math.Round(amount * room[i] / totalRoom, 2, MidpointRounding.AwayFromZero);
            if (shares[i] > room[i])
                shares[i] = room[i];
            assigned += shares[i];
        }

        // Копейки, потерянные или набежавшие при округлении долей, доводим по строкам, пока
        // сумма не сойдётся ТОЧНО.
        var drift = amount - assigned;
        for (var i = 0; i < ids.Count && drift != 0m; i++)
        {
            var step = drift > 0m
                ? Math.Min(drift, room[i] - shares[i])
                : Math.Max(drift, -shares[i]);
            shares[i] += step;
            drift -= step;
        }

        if (drift != 0m)
        {
            PosLogger.Log($"Скидку {amount:0.##} не удалось разложить по строкам до копейки.", "WARNING");
            return false;
        }

        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (shares[i] <= 0m)
                    continue;

                await api.PosCartItemPatchAsync(
                    cartId,
                    ids[i],
                    new Dictionary<string, string>
                    {
                        ["discount_total"] = (already[i] + shares[i]).ToString("0.00", CultureInfo.InvariantCulture),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Часть строк могла уже получить скидку, но эта корзина всё равно не доживёт до
            // продажи: следующая попытка оплаты переносит чек заново.
            PosLogger.Log($"Раскладка скидки по строкам не удалась: {ex.Message}", "WARNING");
            return false;
        }

        PosLogger.Log($"Скидка {amount:0.##} разложена по строкам чека ({ids.Count} шт.).", "PAYMENT");
        return true;
    }

    private static bool TryReadDecimal(JsonElement root, string property, out decimal value)
    {
        value = 0m;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(
                OrderDiscountHelper.NormalizeDecimal(element.GetString() ?? ""),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    /// <summary>Ответ сервера на неудачный запрос — в лог, одной строкой. Без него в журнале
    /// оставалось только «Внутренняя ошибка сервера (500)», по которому причину не найти.</summary>
    private static string DescribePayload(JsonElement? payload)
    {
        if (payload is not { } element)
            return "тело ответа пустое";

        var text = element.ValueKind == JsonValueKind.Undefined ? "" : element.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return "тело ответа пустое";

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 600 ? text.Substring(0, 600) + "…" : text;
    }
}
