namespace NurMarketKassa.Core.Domain;

/// <summary>2026-11-06: своя раскладка весового штрихкода для одного префикса (весы «Штрих РС-200», руководство администратора, п. 7.6).
/// EAN-13 = префикс (2) + 10 знаков «код товара + значение» + контрольная цифра. <see cref="CodeLength"/> знаков отдано коду (1…6),
/// остальные (10 − CodeLength) — значению; порядок задаёт <see cref="CodeFirst"/> («Код-Вес» / «Вес-Код»). Значение — вес в граммах
/// (<see cref="WeightBarcodeValueKind.Weight"/>) либо стоимость в минимальных единицах по числу десятичных знаков цены
/// (<see cref="AmountDecimals"/>: 2 — тыйыны, 0 — целые сомы). <see cref="CodeChecksum"/> («КС кода товара»): последняя цифра поля кода —
/// младший разряд суммы остальных его цифр.</summary>
public sealed record WeightBarcodeCustomLayout(
    string Prefix,
    int CodeLength,
    bool CodeFirst,
    WeightBarcodeValueKind Kind,
    bool CodeChecksum = false,
    int AmountDecimals = 2)
{
    /// <summary>Знаков под значение.</summary>
    public int ValueLength => 10 - CodeLength;

    /// <summary>Сколько цифр самого кода товара (без КС).</summary>
    public int RealCodeDigits => CodeChecksum ? CodeLength - 1 : CodeLength;

    /// <summary>Наибольший код товара, помещающийся в этикетку.</summary>
    public long MaxCode
    {
        get
        {
            var digits = Math.Clamp(RealCodeDigits, 0, 9);
            long max = 1;
            for (var i = 0; i < digits; i++)
                max *= 10;
            return max - 1;
        }
    }

    /// <summary>КС по коду товара: младший разряд суммы цифр кода (руководство, п. 12.1).</summary>
    public int ControlDigit(string codeDigits)
    {
        var sum = 0;
        foreach (var c in codeDigits)
        {
            if (c is >= '0' and <= '9')
                sum += c - '0';
        }
        return sum % 10;
    }

    /// <summary>Собирает 13-значный штрихкод (с контрольной цифрой EAN-13) для проверки и примеров. <paramref name="value"/> — граммы или
    /// минимальные единицы стоимости. null — не помещается (код или значение длиннее отведённого места).</summary>
    public string? BuildBarcode(long code, long value)
    {
        if (code < 1 || code > MaxCode || value < 0)
            return null;
        var valueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (valueText.Length > ValueLength)
            return null;
        var codeText = code.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(RealCodeDigits, '0');
        if (CodeChecksum)
            codeText += ControlDigit(codeText).ToString(System.Globalization.CultureInfo.InvariantCulture);
        valueText = valueText.PadLeft(ValueLength, '0');
        var first12 = Prefix + (CodeFirst ? codeText + valueText : valueText + codeText);
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
