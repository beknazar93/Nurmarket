using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Чек на движение денег помимо продажи: возврат товара и внесение/изъятие из кассы.
///
/// Обе операции — это деньги, вынутые из ящика или положенные в него, и обе до сих пор
/// проходили молча: на экране появлялось «Возврат оформлен» или «Изъятие оформлено», а на
/// руках не оставалось ничего. При пересчёте кассы в конце смены такую строку нечем
/// подтвердить, а покупателю нечего показать.
///
/// Печать вынесена сюда, а не в каждое окно, потому что правило одно на оба случая: если
/// печать чеков включена — бумага обязана выйти, и об отказе принтера кассир должен узнать
/// сразу, а не из расхождения в Z-отчёте.</summary>
public static class OperationReceiptPrinter
{
    /// <summary>Печатает чек возврата. Возвращает текст ошибки, если напечатать не удалось,
    /// иначе null. Сам возврат к этому моменту уже проведён, поэтому исключение наружу не
    /// пускаем: сорванная печать не должна выглядеть как несостоявшийся возврат.</summary>
    public static string? PrintReturn(
        string? originalReceiptNumber,
        IReadOnlyList<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> lines,
        decimal refundTotal,
        string? reason,
        bool isWholeSale,
        string? cashierName)
    {
        return Print(() => ReturnReceiptTextBuilder.Build(
            originalReceiptNumber, lines, refundTotal, reason, isWholeSale, cashierName), "возврата");
    }

    /// <summary>Печатает приходный или расходный чек по операции с кассой.</summary>
    public static string? PrintCashOperation(
        bool isWithdrawal,
        decimal amount,
        string? reason,
        string? cashierName)
    {
        return Print(() => BuildCashOperation(isWithdrawal, amount, reason, cashierName),
            isWithdrawal ? "расхода" : "прихода");
    }

    private static string? Print(Func<string> buildText, string what)
    {
        if (!UserPreferences.Instance.ReceiptEnabled)
        {
            PosLogger.Log($"Чек {what} не печатается: печать выключена в настройках кассы.", "PRINTER");
            return null;
        }

        try
        {
            ReceiptPrintService.PrintReceipt("{}", receiptText: buildText());
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чек {what} не напечатан: {ex}", "PRINTER");
            return Tr.T(
                $"Операция проведена, но чек не напечатался: {ex.Message}",
                $"Операция жасалды, бирок чек басылган жок: {ex.Message}",
                $"The operation went through, but the receipt did not print: {ex.Message}",
                $"İşlem tamamlandı, ancak fiş yazdırılmadı: {ex.Message}",
                $"Amal bajarildi, lekin chek chop etilmadi: {ex.Message}");
        }
    }

    /// <summary>Приходный/расходный чек. Формат общий с чеком продажи, но заголовок и слово
    /// у суммы разные: кассир, разбирая смену, не должен гадать, что за бумажка у него в руках.</summary>
    private static string BuildCashOperation(
        bool isWithdrawal,
        decimal amount,
        string? reason,
        string? cashierName)
    {
        var w = ReceiptLayout.CharWidth;
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(400);

        void Line(string s = "") => sb.Append(s).Append('\n');

        Line(ReceiptLineLayout.Center(isWithdrawal ? "РАСХОДНЫЙ ЧЕК" : "ПРИХОДНЫЙ ЧЕК", w));
        Line();

        if (prefs.ShowStoreName)
            Line($"Маркет - {prefs.StoreName ?? "MARKET PLUS"}");

        if (prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn))
            Line($"ИНН: {prefs.StoreInn.Trim()}");

        Line($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm}");

        if (!string.IsNullOrWhiteSpace(cashierName))
            Line($"Кассир: {cashierName.Trim()}");

        Line(new string('-', w));

        Line(ReceiptLineLayout.FormatLabelAmount(
            isWithdrawal ? "ИЗЪЯТО" : "ВНЕСЕНО",
            ReceiptLineLayout.WithSom(amount.ToString("N2", CultureInfo.CurrentCulture)),
            w));

        if (!string.IsNullOrWhiteSpace(reason))
        {
            Line();
            Line("Основание:");
            foreach (var part in ReceiptLineLayout.WrapLeft(reason.Trim(), w))
                Line(part);
        }

        Line();
        Line(ReceiptLineLayout.Center("Подпись ____________", w));

        return sb.ToString().TrimEnd('\n');
    }
}
