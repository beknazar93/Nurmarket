using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Чек по УЖЕ ПРОВЕДЁННОЙ продаже — для предпросмотра в «Продажах»/«Финансах»
/// и для повторной печати.
///
/// Зачем отдельно от <see cref="CartReceiptTextBuilder"/>: тот читает корзину, а здесь на входе
/// ответ сервера о продаже, где поля называются иначе (цена — «unit_price», не «price»), и
/// разбирать их умеет <see cref="CartDisplayHelper"/>. Вёрстка строк при этом общая
/// (<see cref="ReceiptLineLayout"/>), поэтому предпросмотр на экране выглядит так же, как то,
/// что выйдет из принтера.
///
/// 2026-09-23: до этого предпросмотр показывал маркированный список вида
/// «• товар — 1 × 95,00 = 95.00» — на чек это не походило совсем, и владелец не мог сверить
/// его с бумажным. Повторная печать при этом строила СВОЙ, третий по счёту формат.</summary>
public static class SaleReceiptTextBuilder
{
    /// <summary>Собирает текст чека.</summary>
    /// <param name="saleJson">Ответ сервера о продаже.</param>
    /// <param name="receiptNumber">Номер чека в том виде, в каком его показывает список продаж.</param>
    /// <param name="discount">Скидка по чеку (включая оплату бонусами), уже посчитанная вызывающим.</param>
    /// <param name="total">Итог к оплате. Если null — считается как сумма строк минус скидка.</param>
    /// <param name="isReprint">Повторная печать: на бумаге это обязательно помечается.</param>
    public static string Build(
        string? saleJson,
        string? receiptNumber,
        decimal discount,
        decimal? total,
        bool isReprint = false)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(saleJson) ? "{}" : saleJson);
        return Build(doc.RootElement, receiptNumber, discount, total, isReprint);
    }

    /// <summary>Та же сборка, когда ответ сервера уже разобран: вызывающий код обычно
    /// держит JsonElement и повторный разбор строки ему не нужен.</summary>
    public static string Build(
        JsonElement sale,
        string? receiptNumber,
        decimal discount,
        decimal? total,
        bool isReprint = false)
    {
        var w = ReceiptLayout.CharWidth;
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(700);

        void Line(string s = "") => sb.Append(s).Append('\n');
        void Dash() => Line(new string('-', w));

        Line(ReceiptLineLayout.Center("Контрольно-кассовый чек", w));
        Line();

        if (prefs.ShowStoreName)
            Line($"Маркет - {prefs.StoreName ?? "MARKET PLUS"}");

        if (prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn))
            Line($"ИНН: {prefs.StoreInn.Trim()}");

        if (prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress))
        {
            foreach (var addr in ReceiptLineLayout.WrapCenter(prefs.StoreAddress.Trim(), w))
                Line(addr);
        }

        // Кассир. В чеке, который печатается сразу после продажи, эта строка есть
        // (CartReceiptTextBuilder), а в предпросмотре и повторной печати её не было — один и
        // тот же чек выглядел по-разному, и по бумаге из «Продаж» нельзя было понять, кто
        // пробил. Берём из самой продажи, если сервер вернул, иначе — текущего кассира.
        var cashier = CartDisplayHelper.TryCashierName(sale) ?? PosApp.CurrentUserDisplayName;
        if (!string.IsNullOrWhiteSpace(cashier))
            Line($"Кассир - {cashier.Trim()}");

        // Консультант продажи — сервер отдаёт его имя в consultant_display.
        if (sale.ValueKind == JsonValueKind.Object
            && sale.TryGetProperty("consultant_display", out var consultant)
            && consultant.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(consultant.GetString()))
            Line($"Консультант - {consultant.GetString()!.Trim()}");

        if (!string.IsNullOrWhiteSpace(receiptNumber))
            Line($"Чек №: {receiptNumber.Trim()}");

        // 2026-09-25: дата, время, способ оплаты, «Внесено/Сдача» и долг — как в чеке, который
        // печатается сразу при продаже (CartReceiptTextBuilder). Раньше в предпросмотре и
        // повторной печати их не было, и один и тот же чек на бумаге выглядел по-разному.
        if (prefs.ShowDate && Str(sale, "created_at") is { } createdText
            && DateTime.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
        {
            Line($"Дата - {created:dd.MM.yyyy}");
            Line($"Время - {created:HH:mm}");
        }

        if (Str(sale, "client_name") is { } client)
            Line($"Клиент - {client}");

        Dash();

        // Позиции. Сумму строки берём ту же, что показывает список: пересчёт qty × price заново
        // разошёлся бы со строкой при скидке на позицию или при пакетной продаже.
        decimal linesTotal = 0;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var name = CartDisplayHelper.ItemName(line);
            var qty = CartDisplayHelper.LineQuantity(line);
            var price = (decimal)CartDisplayHelper.UnitPrice(line);
            var sum = CartDisplayHelper.LineTotal(line);

            if (!decimal.TryParse(sum, NumberStyles.Any, CultureInfo.InvariantCulture, out var sumDec))
                sumDec = (decimal)qty * price;
            linesTotal += sumDec;

            // Название целиком с переносом — обрезать нельзя, кассир по нему сверяет товар.
            foreach (var part in ReceiptLineLayout.WrapLeft(name, w))
                Line(part);

            foreach (var part in ReceiptLineLayout.FormatItemBlock(
                         $"{qty.ToString("0.###", CultureInfo.InvariantCulture)} x {price:N2}",
                         sumDec.ToString("N2", CultureInfo.CurrentCulture),
                         w))
            {
                Line(part);
            }
        }

        Dash();

        var due = total ?? linesTotal - discount;

        // Скидка приходит от сервера отдельным числом, но попасть в чек она могла двумя
        // разными путями, и от этого зависит, что считать строкой «Сумма».
        //
        // 1) Обычный путь: сервер принял скидку на весь чек. Цены позиций остались полными,
        //    итог уже уменьшен: сумма строк − скидка = итог.
        // 2) Наш обходной путь: скидку суммой сервер у кассира отбивает ошибкой 500, поэтому
        //    касса раскладывает её по позициям. Цены строк тогда УЖЕ уменьшены, и сумма строк
        //    равна итогу.
        //
        // Во втором случае показывать «Сумма = сумма строк» нельзя: получался чек, где
        // «Сумма 6 114,70 / Скидка −15,00 / ИТОГО 6 114,70» — скидка есть, а итог от неё не
        // изменился. Правильная «Сумма» там — итог ПЛЮС скидка, то есть цена до скидки.
        if (discount > 0.005m)
        {
            decimal? subtotal = null;
            if (Math.Abs(linesTotal - discount - due) < 0.01m)
                subtotal = linesTotal;                 // путь 1
            else if (Math.Abs(linesTotal - due) < 0.01m)
                subtotal = due + discount;             // путь 2

            // Если не сходится ни так, ни так — печатаем только итог. Три числа, которые
            // противоречат друг другу, хуже одного честного.
            if (subtotal is { } sum)
            {
                Line(ReceiptLineLayout.FormatLabelAmount(
                    "Сумма", sum.ToString("N2", CultureInfo.CurrentCulture), w));
                Line(ReceiptLineLayout.FormatLabelAmount(
                    "Скидка", "-" + discount.ToString("N2", CultureInfo.CurrentCulture), w));
            }
        }

        var method = (Str(sale, "payment_method") ?? "").ToLowerInvariant();
        if (CartReceiptTextBuilder.FormatPaymentMethodLine(method) is { } methodLine)
            Line(methodLine);
        var received = Money(sale, "cash_received");
        if (method == "cash" && received is > 0)
        {
            Line(ReceiptLineLayout.FormatLabelAmount(
                "Внесено", ReceiptLineLayout.WithSom(received.Value.ToString("N2", CultureInfo.CurrentCulture)), w));
            var change = Money(sale, "change") ?? Math.Max(0, received.Value - due);
            Line(ReceiptLineLayout.FormatLabelAmount(
                "Сдача", ReceiptLineLayout.WithSom(change.ToString("N2", CultureInfo.CurrentCulture)), w));
        }
        if (method == "debt" && Money(sale, "remaining_debt") is { } remaining)
        {
            Line(ReceiptLineLayout.FormatLabelAmount(
                "Остаток долга", ReceiptLineLayout.WithSom(remaining.ToString("N2", CultureInfo.CurrentCulture)), w));
        }

        Line(ReceiptLineLayout.FormatLabelAmount(
            "ИТОГО", ReceiptLineLayout.WithSom(due.ToString("N2", CultureInfo.CurrentCulture)), w));

        if (isReprint)
        {
            Line();
            Line(ReceiptLineLayout.Center("(повторная печать)", w));
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static decimal? Money(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
