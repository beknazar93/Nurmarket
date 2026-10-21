using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21, документы клиента: сумма прописью на бланке — «Семь тысяч восемьсот восемьдесят шесть сом 00 тыйын»
/// (как бланк телефона, waybill_pdf.dart amountInWords) и по-кыргызски «Жети миң сегиз жүз сексен алты сом 00 тыйын».</summary>
public static class MoneyWords
{
    private static readonly string[] RuOnesM = { "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
    private static readonly string[] RuOnesF = { "", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять" };
    private static readonly string[] RuTeens = { "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
    private static readonly string[] RuTens = { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
    private static readonly string[] RuHundreds = { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };

    private static readonly string[] KyOnes = { "", "бир", "эки", "үч", "төрт", "беш", "алты", "жети", "сегиз", "тогуз" };
    private static readonly string[] KyTens = { "", "он", "жыйырма", "отуз", "кырк", "элүү", "алтымыш", "жетимиш", "сексен", "токсон" };

    /// <summary>2026-10-21 (ревью документов, мелочи): наибольшая сумма для ввода и прописи — 999 999 999 999,99 (больше — (long)
    /// переполнялся, и по-кыргызски пропись падала на суммах > 9,2·10¹⁸).</summary>
    public const double MaxAmount = 999_999_999_999.99;

    /// <summary>Сумма прописью: lang "ky" — по-кыргызски, иначе по-русски. С большой буквы, тыйыны цифрами.</summary>
    public static string Amount(double amount, string? lang)
    {
        // 2026-10-21 (ревью документов, мелочи): не число или больше предела — цифрами, без падения.
        if (!double.IsFinite(amount))
            amount = 0;
        if (amount > MaxAmount)
            return amount.ToString("#,0.00", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " сом";
        amount = Math.Max(0, Math.Round(amount, 2, MidpointRounding.AwayFromZero));
        var whole = (long)Math.Floor(amount);
        var tyiyn = (int)Math.Round((amount - whole) * 100, MidpointRounding.AwayFromZero);
        if (tyiyn >= 100)
        {
            whole++;
            tyiyn -= 100;
        }
        var words = string.Equals(lang, "ky", StringComparison.OrdinalIgnoreCase) ? Ky(whole) : Ru(whole);
        var text = $"{words} сом {tyiyn:00} тыйын";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string Ru(long n)
    {
        n = Math.Clamp(n, 0, (long)MaxAmount);
        if (n == 0)
            return "ноль";
        var parts = new List<string>();
        var groups = new (long Div, string One, string Few, string Many, bool Female)[]
        {
            (1_000_000_000_000, "триллион", "триллиона", "триллионов", false),
            (1_000_000_000, "миллиард", "миллиарда", "миллиардов", false),
            (1_000_000, "миллион", "миллиона", "миллионов", false),
            (1_000, "тысяча", "тысячи", "тысяч", true),
        };
        foreach (var g in groups)
        {
            var k = (int)(n / g.Div % 1000);
            if (k == 0)
                continue;
            parts.Add(RuTriple(k, g.Female));
            parts.Add(Plural(k, g.One, g.Few, g.Many));
        }
        var rest = (int)(n % 1000);
        if (rest > 0)
            parts.Add(RuTriple(rest, false));
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private static string RuTriple(int n, bool female)
    {
        var sb = new List<string>();
        if (n / 100 > 0)
            sb.Add(RuHundreds[n / 100]);
        var t = n % 100;
        if (t >= 10 && t < 20)
            sb.Add(RuTeens[t - 10]);
        else
        {
            if (t / 10 > 0)
                sb.Add(RuTens[t / 10]);
            if (t % 10 > 0)
                sb.Add((female ? RuOnesF : RuOnesM)[t % 10]);
        }
        return string.Join(" ", sb);
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var t = n % 100;
        if (t is >= 11 and <= 14)
            return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    public static string Ky(long n)
    {
        n = Math.Clamp(n, 0, (long)MaxAmount);
        if (n == 0)
            return "нөл";
        var parts = new List<string>();
        var groups = new (long Div, string Name)[] { (1_000_000_000_000, "триллион"), (1_000_000_000, "миллиард"), (1_000_000, "миллион"), (1_000, "миң") };
        foreach (var g in groups)
        {
            var k = (int)(n / g.Div % 1000);
            if (k == 0)
                continue;
            parts.Add(KyTriple(k));
            parts.Add(g.Name);
        }
        var rest = (int)(n % 1000);
        if (rest > 0)
            parts.Add(KyTriple(rest));
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private static string KyTriple(int n)
    {
        var sb = new StringBuilder();
        void Add(string s)
        {
            if (s.Length == 0)
                return;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(s);
        }
        var h = n / 100;
        if (h > 0)
        {
            // «жүз» (100), «эки жүз» (200)…
            if (h > 1)
                Add(KyOnes[h]);
            Add("жүз");
        }
        Add(KyTens[n % 100 / 10]);
        Add(KyOnes[n % 10]);
        return sb.ToString();
    }
}
