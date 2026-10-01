using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28, BE-11: тело запроса «продажа одним запросом» (POST api/main/pos/checkout/) из
/// снимка чека кассы. Формат выяснен у сервера запросами с неполным телом (400 перечисляет поля)
/// и проверен настоящими продажами на тестовом аккаунте:
/// <code>
/// { "shift": uuid, "client": uuid|null,
///   "items": [ { "product": uuid, "qty": "1", "price": "50.00", "discount": "2.00" },
///              { "custom": true, "name": "Доставка", "price": "5.00", "qty": "1" } ],
///   "order_discount_total": "1.00"  |  "order_discount_percent": "10",
///   "payment": { "method": "cash|transfer|mixed|debt|…", "received": "100.00",
///                "cash_amount": "30.00", "card_amount": "22.00" },
///   "consultant_id": uuid, "consultant_commission_enabled": true,
///   "consultant_commission_percent": "5.00", "print_receipt": false }
/// </code>
/// price — цена за единицу, discount — скидка на строку суммой. Кассу сервер берёт из смены.
/// Всё, чего новый адрес не умеет, возвращает null — такой чек идёт старым путём
/// (sales/start → позиции → скидка → checkout):
/// поштучная продажа из пачки (sale_package_id сервер здесь не знает), «Доп. услуга» с
/// отрицательной ценой («Расход» — сервер отвечает «Укажите цену позиции»), строка без товара,
/// которая не «Доп. услуга», товар с не-UUID id (локальные комплекты), нулевое количество.
/// </summary>
public static class QuickCheckoutBody
{
    public static JsonObject? TryBuild(
        string? cartJson,
        string? paymentMethod,
        string? cashReceived,
        string? nonCashReceived,
        string? clientId,
        string? shiftId,
        string? consultantId,
        bool consultantCommissionEnabled,
        string? consultantCommissionPercent,
        bool printReceipt,
        out string? unsupportedReason)
    {
        unsupportedReason = null;
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            unsupportedReason = "нет способа оплаты";
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
        }
        catch (JsonException)
        {
            unsupportedReason = "снимок чека не читается";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var items = new JsonArray();
            // 2026-09-29: строки одного товара по одной цене (основной штрихкод и варианты по доп.
            // штрихкодам — в чеке кассы это разные строки, см. ReceiptSnapshotCartEditor.AddProduct)
            // уходят одной позицией: так сервер получает ровно то же, что и до разделения строк.
            var byProductAndPrice = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in CartDisplayHelper.EnumerateItems(root))
            {
                var line = TryBuildLine(it, out unsupportedReason);
                if (line == null)
                    return null;
                if (line["product"] is JsonValue productValue && productValue.TryGetValue<string>(out var product)
                    && line["price"] is JsonValue priceValue && priceValue.TryGetValue<string>(out var price))
                {
                    var key = product + "|" + price;
                    if (byProductAndPrice.TryGetValue(key, out var first))
                    {
                        first["qty"] = SumDecimalText(first["qty"], line["qty"]);
                        if (line["discount"] is not null)
                            first["discount"] = SumDecimalText(first["discount"], line["discount"], money: true);
                        continue;
                    }

                    byProductAndPrice[key] = line;
                }

                items.Add(line);
            }

            if (items.Count == 0)
            {
                unsupportedReason = "в чеке нет позиций";
                return null;
            }

            var body = new JsonObject();
            if (Guid.TryParse(shiftId, out _))
                body["shift"] = shiftId!.Trim();
            if (Guid.TryParse(clientId, out _))
                body["client"] = clientId!.Trim();
            body["items"] = items;

            // Скидка на чек — как в материализации (StagingCartService.ApplyOrderDiscountFromSnapshotAsync):
            // только одно поле из двух, иначе сервер отказывает.
            var discount = new Dictionary<string, string>();
            foreach (var field in new[] { "order_discount_percent", "order_discount_total" })
            {
                if (!root.TryGetProperty(field, out var el))
                    continue;
                var raw = el.ValueKind == JsonValueKind.Number
                    ? el.GetDouble().ToString(CultureInfo.InvariantCulture)
                    : el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                if (!OrderDiscountHelper.IsEmptyOrZeroLike(raw))
                    discount[field] = OrderDiscountHelper.NormalizeDecimal(raw!);
            }

            foreach (var (key, value) in OrderDiscountHelper.SanitizePatchBody(discount))
            {
                if (!decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) || number < 0)
                {
                    unsupportedReason = $"скидка на чек «{value}» не число";
                    return null;
                }

                body[key] = number.ToString("0.00", CultureInfo.InvariantCulture);
            }

            var method = paymentMethod.Trim();
            var payment = new JsonObject { ["method"] = method };
            var received = Money(cashReceived);
            if (received != null)
                payment["received"] = received;
            if (string.Equals(method, "mixed", StringComparison.OrdinalIgnoreCase))
            {
                // BE-07: разбивка смешанной оплаты. Сервер хранит её в cash_amount/card_amount
                // продажи (проверено: продажа 1117 — 30 нал + 22 безнал).
                payment["cash_amount"] = received ?? "0.00";
                payment["card_amount"] = Money(nonCashReceived) ?? "0.00";
            }
            else if (string.Equals(method, "debt", StringComparison.OrdinalIgnoreCase)
                     && decimal.TryParse(received, NumberStyles.Any, CultureInfo.InvariantCulture, out var prepaid) && prepaid > 0m)
            {
                // 2026-10-01, ТЗ-BE-2026-04 AN-01 (сервер сделал): внесённое сразу при продаже в долг —
                // с разбивкой. Касса принимает предоплату долга наличными, поэтому всё — в cash_amount:
                // сервер кладёт её в «Наличные» продажи и в ожидаемую наличность смены (AN-02).
                payment["cash_amount"] = received;
                payment["card_amount"] = "0.00";
            }

            body["payment"] = payment;

            // Консультант — те же поля и правила, что у старого пути (PosCheckoutService.AddConsultant).
            if (Guid.TryParse(consultantId, out _))
            {
                body["consultant_id"] = consultantId!.Trim();
                body["consultant_commission_enabled"] = consultantCommissionEnabled;
                body["consultant_commission_percent"] =
                    consultantCommissionEnabled && Money(consultantCommissionPercent) is { } pct ? pct : "0.00";
            }

            body["print_receipt"] = printReceipt;
            return body;
        }
    }

    private static JsonObject? TryBuildLine(JsonElement it, out string? unsupportedReason)
    {
        unsupportedReason = null;
        var name = CartDisplayHelper.ItemName(it);
        var qty = CartDisplayHelper.LineQuantity(it);
        if (!(qty > 0) || double.IsInfinity(qty))
        {
            unsupportedReason = $"«{name}»: количество {qty}";
            return null;
        }

        var productId = CartDisplayHelper.TryProductId(it);
        if (string.IsNullOrEmpty(productId))
        {
            if (!CartDisplayHelper.IsCustomLine(it))
            {
                unsupportedReason = $"«{name}»: строка без товара";
                return null;
            }

            var customPrice = CartDisplayHelper.UnitPrice(it);
            if (customPrice < 0)
            {
                unsupportedReason = $"«{name}»: «Доп. услуга» с отрицательной ценой";
                return null;
            }

            return new JsonObject
            {
                ["custom"] = true,
                ["name"] = name,
                ["price"] = CartDisplayHelper.FormatMoney(customPrice),
                ["qty"] = QuantityText(it, qty),
            };
        }

        if (!Guid.TryParse(productId, out _))
        {
            unsupportedReason = $"«{name}»: id товара не UUID";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(CartDisplayHelper.SalePackageId(it)))
        {
            unsupportedReason = $"«{name}»: поштучно из пачки";
            return null;
        }

        // 2026-10-01: вариант (размер/цвет) — через add-item с variant_id (StagingCartService).
        if (!string.IsNullOrWhiteSpace(CartDisplayHelper.ServerVariantId(it)))
        {
            unsupportedReason = $"«{name}»: размер/цвет";
            return null;
        }

        var line = new JsonObject
        {
            ["product"] = productId.Trim(),
            ["qty"] = QuantityText(it, qty),
            ["price"] = CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(it)),
        };
        if (CartDisplayHelper.OptionalDiscountTotalParam(it) is { } lineDiscount)
            line["discount"] = lineDiscount;
        return line;
    }

    /// <summary>2026-09-28, продажа №1136: итог продажи, который получится у сервера из ЭТОГО тела
    /// запроса, — считается по самому телу, а не по снимку чека: Σ(цена × кол-во − скидка строки),
    /// затем скидка на чек (процент — от суммы после скидок строк, до копейки; сумма — не больше
    /// остатка). Так сервер посчитал продажи 1116–1118 и 1143–1144. Скидка строки по акции товара
    /// в теле уже стоит явно (CartDisplayHelper.OptionalDiscountTotalParam), поэтому тело
    /// «самоописательное». Сверяется с итогом окна оплаты перед отправкой (PosCheckoutService.Quick).</summary>
    public static double ComputeTotal(JsonObject body)
    {
        double subtotal = 0;
        double lineDiscounts = 0;
        if (body["items"] is JsonArray items)
        {
            foreach (var node in items)
            {
                if (node is not JsonObject line)
                    continue;
                var qty = ReadMoney(line["qty"]);
                var price = ReadMoney(line["price"]);
                subtotal = RoundMoney(subtotal + RoundMoney(qty * price));
                lineDiscounts = RoundMoney(lineDiscounts + ReadMoney(line["discount"]));
            }
        }

        var discountBase = Math.Max(0, RoundMoney(subtotal - lineDiscounts));
        double orderDiscount = 0;
        if (body["order_discount_percent"] is { } pct)
            orderDiscount = RoundMoney(discountBase * Math.Clamp(ReadMoney(pct), 0, 100) / 100.0);
        else if (body["order_discount_total"] is { } sum)
            orderDiscount = Math.Min(discountBase, ReadMoney(sum));

        return Math.Max(0, RoundMoney(discountBase - orderDiscount));
    }

    private static double ReadMoney(JsonNode? node) =>
        JsonNumericReader.TryToDouble(node, out var v) && double.IsFinite(v) ? v : 0;

    private static double RoundMoney(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    /// <summary>Как в StagingCartService.PushItemsFromSnapshotAsync: весовой — до граммов,
    /// штучный — целым (без TrimEnd, см. там живой баг «10.000 кг ушло как 1»).</summary>
    private static string QuantityText(JsonElement it, double qty) =>
        CartDisplayHelper.LineMustWeigh(it)
            ? qty.ToString("0.###", CultureInfo.InvariantCulture)
            : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>2026-09-29: сумма двух чисел тела запроса («2» + «1» → «3», «1.50» + «0.50» → «2.00»)
    /// для строк одного товара, объединённых в одну позицию. Количество — без хвостовых нулей
    /// (штучное остаётся целым, весовое — до граммов), деньги — до копейки.</summary>
    private static string SumDecimalText(JsonNode? a, JsonNode? b, bool money = false)
    {
        static decimal Read(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text)
            && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
                ? number
                : 0m;

        var sum = Read(a) + Read(b);
        return money
            ? sum.ToString("0.00", CultureInfo.InvariantCulture)
            : sum.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? Money(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return decimal.TryParse(OrderDiscountHelper.NormalizeDecimal(raw), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.00", CultureInfo.InvariantCulture)
            : null;
    }
}
