using System.Globalization;

namespace NurMarketKassa.Services;

public static class CheckoutValidation
{
    public static string NormalizeDecimal(string? raw) => (raw ?? "").Trim().Replace(',', '.');

    /// <summary>2026-10-27 (аудит кассы, Н-8/Н-7): результат разбора суммы, которую кассир вводит руками.</summary>
    public enum AmountParse
    {
        Ok,
        /// <summary>Поле пустое.</summary>
        Empty,
        /// <summary>Не число: «abc», «(100)», «1e3», «-5», «1.5.0».</summary>
        Invalid,
        /// <summary>Больше двух знаков после запятой: «1.000» (тысяча или единица — не угадываем), «33,333».</summary>
        TooManyDecimals,
    }

    /// <summary>2026-10-27 (аудит кассы, Н-8/Н-7): разбор суммы окна оплаты теми же правилами, что <c>CashAmountInput</c> (смена):
    /// запятая и точка — десятичный разделитель, пробелы (в т.ч. неразрывные) убираются, разделитель тысяч — только между
    /// группами по 3 цифры при двух разных знаках («1,500.50»). Раньше окно оплаты разбирало через NumberStyles.Any:
    /// «1.000» = 1, «(100)» = −100, «1e3» = 1000. Больше двух знаков после запятой — ошибка (деньги считаются до копейки).</summary>
    public static AmountParse TryParseAmount(string? text, out double value) => TryParseAmount(text, out value, 2);

    /// <summary>2026-10-28 (дробные цены закупки): те же правила разбора, но число знаков после запятой задаёт вызывающий
    /// (цена закупки — до <c>ReceivingPricing.PurchaseDecimals</c>, например «12,333»). «1.000» (ровно три нуля) всё равно считается
    /// неоднозначным («тысяча или единица») и отклоняется — как и при двух знаках.</summary>
    public static AmountParse TryParseAmount(string? text, out double value, int maxDecimals)
    {
        value = 0;
        // Обычный, неразрывный (U+00A0) и узкий неразрывный (U+202F) пробелы — «1 500,50».
        var raw = new string((text ?? "").Where(ch => ch != ' ' && ch != (char)0x00A0 && ch != (char)0x202F).ToArray()).Trim();
        if (raw.Length == 0)
            return AmountParse.Empty;

        var last = Math.Max(raw.LastIndexOf(','), raw.LastIndexOf('.'));
        if (last >= 0)
        {
            var sep = raw[last];
            var other = sep == ',' ? '.' : ',';
            if (raw.IndexOf(sep) != last)
                return AmountParse.Invalid;
            var groups = raw[..last].Split(other);
            if (groups.Skip(1).Any(g => g.Length != 3))
                return AmountParse.Invalid;
            var fraction = raw[(last + 1)..];
            if (fraction.Length > maxDecimals && fraction.All(char.IsDigit))
                return AmountParse.TooManyDecimals;
            if (maxDecimals > 2 && fraction == "000")
                return AmountParse.TooManyDecimals; // «1.000» — тысяча или единица: не угадываем
            raw = string.Concat(groups) + "." + fraction;
        }

        if (!double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
            return AmountParse.Invalid;
        value = parsed;
        return AmountParse.Ok;
    }

    /// <summary>Текст ошибки разбора суммы на 5 языках; null — суммы нет (пусто) или она разобрана.</summary>
    public static string? AmountProblemText(AmountParse parse) => parse switch
    {
        AmountParse.Invalid => Tr.T(
            "Сумма введена неверно. Пример: 1500 или 1500,50.",
            "Сумма туура эмес киргизилди. Мисалы: 1500 же 1500,50.",
            "The amount is not valid. Example: 1500 or 1500.50.",
            "Tutar geçersiz. Örnek: 1500 veya 1500,50.",
            "Summa noto'g'ri kiritilgan. Masalan: 1500 yoki 1500,50."),
        AmountParse.TooManyDecimals => Tr.T(
            "Не больше двух знаков после запятой. Тысяча — 1000 или 1000,00.",
            "Үтүрдөн кийин экиден ашык цифра болбойт. Миң — 1000 же 1000,00.",
            "No more than two decimal places. One thousand is 1000 or 1000.00.",
            "Virgülden sonra en fazla iki basamak. Bin: 1000 veya 1000,00.",
            "Verguldan keyin ikkitadan ko'p raqam bo'lmaydi. Ming — 1000 yoki 1000,00."),
        _ => null,
    };

    /// <summary>2026-10-28: текст ошибки разбора для поля с другим числом знаков после запятой (цена закупки — до 4).</summary>
    public static string? AmountProblemText(AmountParse parse, int maxDecimals) =>
        parse == AmountParse.TooManyDecimals && maxDecimals != 2
            ? Tr.T($"Не больше {maxDecimals} знаков после запятой. Тысяча — 1000 или 1000,00.",
                $"Үтүрдөн кийин {maxDecimals} цифрадан ашык болбойт. Миң — 1000 же 1000,00.",
                $"No more than {maxDecimals} decimal places. One thousand is 1000 or 1000.00.",
                $"Virgülden sonra en fazla {maxDecimals} basamak. Bin: 1000 veya 1000,00.",
                $"Verguldan keyin {maxDecimals} tadan ko'p raqam bo'lmaydi. Ming — 1000 yoki 1000,00.")
            : AmountProblemText(parse);

    /// <summary>null = ок, иначе текст ошибки (как validate_cash_received).</summary>
    public static string? ValidateCashReceived(string? raw, double totalDue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parse = TryParseAmount(raw, out var v);
        if (parse != AmountParse.Ok)
            return AmountProblemText(parse) ?? Tr.T(
                "Введите сумму «получено наличными»",
                "«Накталай алынды» суммасын киргизиңиз",
                "Enter the amount received in cash",
                "Nakit alınan tutarı girin",
                "«Naqd olingan» summasini kiriting");

        if (v < 0)
            return Tr.T("Сумма не может быть отрицательной", "Сумма терс болушу мүмкүн эмес", "The amount cannot be negative",
                "Tutar negatif olamaz", "Summa manfiy bo'lishi mumkin emas");

        if (totalDue > 0 && v + 1e-9 < totalDue)
        {
            var due = totalDue.ToString("0.00", CultureInfo.InvariantCulture);
            return Tr.T($"Сумма не меньше к оплате ({due} сом)", $"Сумма төлөнүүчү суммадан аз болбошу керек ({due} сом)",
                $"The amount must not be less than the total due ({due} som)", $"Tutar ödenecek tutardan az olmamalı ({due} som)",
                $"Summa to'lanadigan summadan kam bo'lmasligi kerak ({due} so'm)");
        }

        return null;
    }
}
