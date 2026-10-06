using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, исследование «Кассы для одежды» (О-30): чек обмена. Одна бумажка на всю операцию:
/// что вернули, что выдали взамен, сколько зачтено и сколько доплатил покупатель или сколько ему выдали.
/// Вёрстка общая с чеком продажи и возврата (<see cref="ReceiptLineLayout"/>).</summary>
public static class ExchangeReceiptTextBuilder
{
    /// <param name="originalReceiptNumber">Номер чека, по которому обмен.</param>
    /// <param name="originalDate">Дата этого чека (срок обмена считается от неё).</param>
    /// <param name="newReceiptNumber">Номер нового чека на выданный товар (его создаёт сервер).</param>
    /// <param name="returned">Возвращённые позиции.</param>
    /// <param name="issued">Выданные взамен позиции.</param>
    /// <param name="returnedAmount">Сумма возвращённого — по серверу.</param>
    /// <param name="newAmount">Сумма нового товара — по серверу.</param>
    /// <param name="difference">&gt; 0 — доплата покупателя, &lt; 0 — выдано покупателю.</param>
    /// <param name="paymentMethod">Способ доплаты: cash или transfer.</param>
    public static string Build(
        string? originalReceiptNumber,
        DateTime? originalDate,
        string? newReceiptNumber,
        IReadOnlyList<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> returned,
        IReadOnlyList<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> issued,
        decimal returnedAmount,
        decimal newAmount,
        decimal difference,
        string? paymentMethod,
        string? reason,
        string? cashierName)
    {
        var w = ReceiptLayout.CharWidth;
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(900);

        void Line(string s = "") => sb.Append(s).Append('\n');
        void Dash() => Line(new string('-', w));
        string Money(decimal v) => ReceiptLineLayout.WithSom(v.ToString("N2", CultureInfo.CurrentCulture));

        void Items(IReadOnlyList<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> lines)
        {
            foreach (var line in lines)
            {
                foreach (var part in ReceiptLineLayout.WrapLeft(line.Name, w))
                    Line(part);
                foreach (var part in ReceiptLineLayout.FormatItemBlock(
                             $"{line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} x {line.UnitPrice:N2}",
                             line.Sum.ToString("N2", CultureInfo.CurrentCulture),
                             w))
                    Line(part);
            }
        }

        Line(ReceiptLineLayout.Center("ОБМЕН ТОВАРА", w));
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
            Line($"По чеку №: {originalReceiptNumber.Trim().TrimStart('№')}" + (originalDate is { } d ? $" от {d:dd.MM.yyyy}" : ""));
        if (!string.IsNullOrWhiteSpace(newReceiptNumber))
            Line($"Новый чек №: {newReceiptNumber.Trim().TrimStart('№')}");
        if (!string.IsNullOrWhiteSpace(cashierName))
            Line($"Кассир: {cashierName.Trim()}");

        Dash();
        Line("ВОЗВРАЩЕНО:");
        Items(returned);
        Dash();
        Line("ВЫДАНО ВЗАМЕН:");
        Items(issued);
        Dash();

        Line(ReceiptLineLayout.FormatLabelAmount("Возвращено на", Money(returnedAmount), w));
        Line(ReceiptLineLayout.FormatLabelAmount("Выдано на", Money(newAmount), w));
        Line(ReceiptLineLayout.FormatLabelAmount("ЗАЧТЕНО", Money(Math.Min(returnedAmount, newAmount)), w));
        if (difference > 0.004m)
        {
            var how = string.Equals(paymentMethod, "transfer", StringComparison.OrdinalIgnoreCase) ? "ДОПЛАТА ПЕРЕВОДОМ" : "ДОПЛАТА НАЛИЧНЫМИ";
            Line(ReceiptLineLayout.FormatLabelAmount(how, Money(difference), w));
        }
        else if (difference < -0.004m)
            Line(ReceiptLineLayout.FormatLabelAmount("ВЫДАНО ПОКУПАТЕЛЮ", Money(-difference), w));
        else
            Line("Без доплаты");

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
