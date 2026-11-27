using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (владелец: «Поставщиктин карызын төлөгөндө жарымын же 20% бере албайт экенсиң, только 100 пайыз төлөш керек экен»): оплата долга поставщику частями.
/// Раньше «Оплатить взнос» и «Погасить весь долг» гасили только взнос/долг целиком (POST deals/{d}/pay/ без amount). Теперь окно оплаты берёт сумму от 0,01 до остатка,
/// а здесь — вся арифметика (деньги не считаются во view): разбор поля, проверка границ, кнопки «50 %» / «20 %», остаток после оплаты и раскладка суммы по взносам в порядке сроков.
/// Взнос, закрытый целиком, уходит БЕЗ amount (как всегда — подтверждённый рабочий вызов), amount шлётся только последнему, частично закрываемому взносу
/// (сервер по ТЗ Б-13 п. 6 принимает amount ≤ остатка взноса, больше — 400 amount_exceeds_installment с data.max).</summary>
public static class SupplierDebtPayMath
{
    /// <summary>Наименьшая сумма оплаты — копейка (тыйын).</summary>
    public const double MinAmount = 0.01;

    public enum Problem
    {
        None,
        /// <summary>Поле пустое или не число.</summary>
        NotNumber,
        /// <summary>Меньше копейки (ноль, минус).</summary>
        TooSmall,
        /// <summary>Больше остатка долга.</summary>
        TooBig,
    }

    /// <summary>Часть оплаты: какой взнос какой сумме гасится. <see cref="IsFull"/> — взнос закрывается целиком (amount серверу не шлём).</summary>
    public sealed record Step(string DealId, string InstallmentId, double Remaining, double Amount, bool IsFull);

    /// <summary>Сумма из поля ввода: пробелы (в т. ч. неразрывные) и запятая/точка допустимы, до копейки (лишние знаки округляются от нуля). Пусто/не число — false.</summary>
    public static bool TryParse(string? text, out double amount)
    {
        amount = 0;
        var raw = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace(',', '.');
        if (raw.Length == 0 || !decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return false;
        amount = (double)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>Проверка суммы: от копейки до остатка долга (допуск полкопейки на шум double).</summary>
    public static Problem Check(double amount, double max)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount))
            return Problem.NotNumber;
        if (amount < MinAmount - 0.0049)
            return Problem.TooSmall;
        return amount > max + 0.005 ? Problem.TooBig : Problem.None;
    }

    /// <summary>Сумма для кнопки «50 %» / «20 %»: доля остатка до копейки (от нуля), не меньше копейки и не больше остатка. 100 % — ровно остаток.</summary>
    public static double Percent(double max, int percent)
    {
        if (max <= MinAmount)
            return Math.Max(0, max);
        if (percent >= 100)
            return max;
        var share = Math.Round((decimal)max * percent / 100m, 2, MidpointRounding.AwayFromZero);
        return Math.Min(max, Math.Max(MinAmount, (double)share));
    }

    /// <summary>Сколько долга останется после оплаты <paramref name="amount"/> из <paramref name="max"/> (не меньше нуля, без шума double).</summary>
    public static double Left(double max, double amount) =>
        (double)Math.Max(0m, Math.Round((decimal)max - (decimal)amount, 2, MidpointRounding.AwayFromZero));

    /// <summary>Сумма равна остатку (оплата целиком), допуск полкопейки.</summary>
    public static bool IsFull(double max, double amount) => amount >= max - 0.005;

    /// <summary>2026-11-26: сервер отказал именно в ЧАСТИ суммы (400/422 с полем «amount»), но не потому, что сумма больше остатка (это amount_exceeds_* — он сам называет максимум).
    /// Тогда кассиру честно говорим: частичную оплату сервер не принимает — платите взнос целиком и сообщите в поддержку (контракт «amount» у deals/{d}/pay/ для поставщиков Умаром не подтверждён).</summary>
    public static bool IsPartialRefused(Exception ex, bool wasPartial)
    {
        if (!wasPartial || ex is not ApiException { StatusCode: 400 or 422 } api)
            return false;
        var text = api.Payload?.ToString() ?? api.Message;
        return text.Contains("amount", StringComparison.OrdinalIgnoreCase) && !text.Contains("amount_exceeds", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Остаток долга по очереди взносов.</summary>
    public static double Total(IEnumerable<(string DealId, string InstallmentId, double Remaining)> queue) =>
        (double)queue.Sum(x => (decimal)x.Remaining);

    /// <summary>Раскладка суммы по очереди взносов (очередь уже в порядке сроков): каждый взнос по порядку закрывается целиком, пока хватает суммы, последний — частично.
    /// Пустой результат — сумма не положительна или очередь пуста. Сумма больше очереди — все взносы закрываются целиком (лишнее не раскладывается: проверку границы делает <see cref="Check"/>).</summary>
    public static IReadOnlyList<Step> Plan(IEnumerable<(string DealId, string InstallmentId, double Remaining)> queue, double amount)
    {
        var steps = new List<Step>();
        var left = Math.Round((decimal)amount, 2, MidpointRounding.AwayFromZero);
        foreach (var (dealId, installmentId, remaining) in queue)
        {
            if (left < 0.01m)
                break;
            var rest = Math.Round((decimal)remaining, 2, MidpointRounding.AwayFromZero);
            if (rest < 0.01m)
                continue;
            var take = Math.Min(rest, left);
            var full = take >= rest;
            steps.Add(new Step(dealId, installmentId, (double)rest, (double)take, full));
            left -= take;
        }
        return steps;
    }
}
