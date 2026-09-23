using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Чек возврата.
///
/// Возврат — это выдача денег из кассы, и подтверждать её бумагой надо так же, как продажу:
/// покупателю — доказательство, что деньги вернули, кассиру — строка, которая сойдётся при
/// пересчёте кассы в конце смены. До этого возврат оформлялся молча, на экране появлялось
/// только окно «Возврат оформлен», и на руках не оставалось ничего.
///
/// Вёрстка общая с чеком продажи (<see cref="ReceiptLineLayout"/>), поэтому оба выходят из
/// принтера одинаковой ширины и в одном стиле. Отличия намеренные: заголовок «ВОЗВРАТ»,
/// номер исходного чека, причина и слово «К ВОЗВРАТУ» вместо «ИТОГО» — кассир не должен
/// перепутать эти две бумажки, разбирая смену.</summary>
public static class ReturnReceiptTextBuilder
{
    /// <summary>Собирает текст чека возврата.</summary>
    /// <param name="originalReceiptNumber">Номер чека, по которому идёт возврат.</param>
    /// <param name="lines">Возвращаемые позиции: название, количество, цена за единицу.</param>
    /// <param name="refundTotal">Сумма к возврату.</param>
    /// <param name="reason">Причина возврата — её вводит кассир.</param>
    /// <param name="isWholeSale">Возвращён весь чек целиком, а не отдельные позиции.</param>
    /// <param name="cashierName">Кто оформил возврат.</param>
    public static string Build(
        string? originalReceiptNumber,
        IReadOnlyList<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> lines,
        decimal refundTotal,
        string? reason,
        bool isWholeSale,
        string? cashierName)
    {
        var w = ReceiptLayout.CharWidth;
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(700);

        void Line(string s = "") => sb.Append(s).Append('\n');
        void Dash() => Line(new string('-', w));

        Line(ReceiptLineLayout.Center("ВОЗВРАТ ТОВАРА", w));
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

        Line($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm}");

        if (!string.IsNullOrWhiteSpace(originalReceiptNumber))
            Line($"По чеку №: {originalReceiptNumber.Trim()}");

        if (!string.IsNullOrWhiteSpace(cashierName))
            Line($"Кассир: {cashierName.Trim()}");

        Line(isWholeSale ? "Возврат: весь чек" : "Возврат: отдельные позиции");

        Dash();

        foreach (var line in lines)
        {
            foreach (var part in ReceiptLineLayout.WrapLeft(line.Name, w))
                Line(part);

            foreach (var part in ReceiptLineLayout.FormatItemBlock(
                         $"{line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} x {line.UnitPrice:N2}",
                         line.Sum.ToString("N2", CultureInfo.CurrentCulture),
                         w))
            {
                Line(part);
            }
        }

        Dash();

        Line(ReceiptLineLayout.FormatLabelAmount(
            "К ВОЗВРАТУ",
            ReceiptLineLayout.WithSom(refundTotal.ToString("N2", CultureInfo.CurrentCulture)),
            w));

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Line();
            Line("Причина:");
            foreach (var part in ReceiptLineLayout.WrapLeft(reason.Trim(), w))
                Line(part);
        }

        Line();
        Line(ReceiptLineLayout.Center("Подпись покупателя ____________", w));

        return sb.ToString().TrimEnd('\n');
    }
}
