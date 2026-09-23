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

        if (!string.IsNullOrWhiteSpace(receiptNumber))
            Line($"Чек №: {receiptNumber.Trim()}");

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

        // Скидку показываем, только если она сходится с итогом: сумма строк минус скидка
        // должна давать ИТОГО. Сервер иногда отдаёт total, в котором скидка УЖЕ учтена в ценах
        // строк, и тогда получался чек с арифметикой, которая не сходится на глазах у
        // покупателя: «Сумма 6 114,70 / Скидка −15,00 / ИТОГО 6 114,70». Лучше показать один
        // честный итог, чем три числа, которые друг другу противоречат.
        var reconciles = Math.Abs(linesTotal - discount - due) < 0.01m;
        if (discount > 0.005m && reconciles)
        {
            Line(ReceiptLineLayout.FormatLabelAmount("Сумма", linesTotal.ToString("N2", CultureInfo.CurrentCulture), w));
            Line(ReceiptLineLayout.FormatLabelAmount("Скидка", "-" + discount.ToString("N2", CultureInfo.CurrentCulture), w));
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
}
