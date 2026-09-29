using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-09-30: названия товаров для весов Rongta (RLS1000/RLS1100), которые касса пишет по
/// протоколу Dahua («!0V», TCP 4001 — весы владельца отвечают на него как TM-30F). Просьба
/// владельца «напиши свой драйвер для общения с весами» — без программы RLS1000.
///
/// Всё ниже подобрано на живых весах владельца (192.168.1.87) по напечатанным этикеткам, 6 серий:
/// • Имя — ПАРЫ байт, каждая пара = 4 цифры: байт − 0xA0, по 2 цифры (как «зона/позиция» GB2312).
///   Коды Dahua (3 цифры на байт) весы печатают мусором («µЎ»).
/// • Пара (0xA3, код+0x80) — латинская буква, цифра или знак ASCII («TEST abc 123» — верно).
/// • Пара (0xA1, 0xA1) — пробел.
/// • Остальные пары весы печатают как ДВА символа Windows-1251: «Тест» = D2 E5 F1 F2 — верно.
///   Первый байт пары должен быть 0xB0…0xF7 (иначе весы обрывают имя: «шт» с «ш» = 0xF8 первым,
///   «Ёлка» с «Ё» = 0xA8 первым, мягкий перенос 0xAD первым — пусто), второй — 0xA0…0xFF
///   (0xA0 печатается пробелом, «Мясо» с «я» = 0xFF вторым — верно).
/// • Хвостовые 0xFF/0xA0 весы отрезают («Мя» → «М»): «я» последней буквой не напечатается.
/// • GB2312-кириллица (зона 7) и мягкий перенос как заполнитель не годятся (пусто / «-»).
///
/// Поэтому имя раскладывается по парам перебором (динамическое программирование) с «ценой»
/// замен: латинская буква того же вида вместо «а е о р с у х» (почти не видно) и «Ё» → «Е» —
/// 1; заглавная вместо строчной («молочнЫй», «Шт.») — 5; лишний пробел перед цифрой/знаком
/// после нечётной русской буквы — 4. «я» последней буквой — всегда «Я».
/// </summary>
public static class RongtaNameCodec
{
    /// <summary>Сколько пар (4 цифры каждая) помещается в имя: у Dahua поле имени — 132 цифры,
    /// «0000» в конце тоже в счёт.</summary>
    public const int MaxPairs = 32;

    private const byte AsciiZone = 0xA3;
    private const byte SpaceByte = 0xA1;
    private const byte PadByte = 0xA0;

    private static readonly Encoding Cp1251 = ResolveCp1251();

    private static Encoding ResolveCp1251()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // уже зарегистрирован — ниже GetEncoding скажет
        }
        return Encoding.GetEncoding(1251, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    private static bool HiOk(byte b) => b is >= 0xB0 and <= 0xF7;
    private static bool LoOk(byte b) => b >= 0xA0;

    /// <summary>Русские буквы, у которых есть латинский двойник того же вида.</summary>
    private static readonly Dictionary<char, char> LookAlike = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x',
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
        ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X', ['Ё'] = 'E', ['ё'] = 'e',
    };

    private readonly record struct Step(int Cost, int PrevState, byte[] Bytes);

    /// <summary>Байты имени парами (чётной длины), не больше <see cref="MaxPairs"/> пар.</summary>
    public static byte[] EncodeBytes(string? name)
    {
        var text = DahuaTmProtocol.ReplaceUnsupportedLetters((name ?? "").Replace(' ', ' ').Replace('—', '-').Replace('–', '-').Trim());
        // «я» в самом конце весы отрежут (0xFF в хвосте) — заглавная видна.
        if (text.EndsWith('я'))
            text = text[..^1] + "Я";

        // Состояние: -1 — пара закрыта, 0…255 — открыта пара с этим первым байтом.
        const int closed = -1;
        var n = text.Length;
        var dp = new Dictionary<int, Step>[n + 1];
        for (var i = 0; i <= n; i++)
            dp[i] = new Dictionary<int, Step>();
        dp[0][closed] = new Step(0, closed, Array.Empty<byte>());

        void Put(int i, int state, int cost, int prev, byte[] bytes)
        {
            if (!dp[i].TryGetValue(state, out var old) || old.Cost > cost)
                dp[i][state] = new Step(cost, prev, bytes);
        }

        for (var i = 0; i < n; i++)
        {
            var ch = text[i];
            foreach (var (state, step) in dp[i])
            {
                var cost = step.Cost;
                var open = state != closed;
                // Закрыть открытую пару «пробелом» 0xA0 (видно как лишний пробел) — перед ASCII.
                byte[] Close() => open ? new[] { (byte)state, PadByte } : Array.Empty<byte>();

                if (ch == ' ')
                {
                    // Пробел после нечётной буквы — вторым байтом пары, иначе — пара A1 A1.
                    Put(i + 1, closed, cost, state, open ? new[] { (byte)state, PadByte } : new[] { SpaceByte, SpaceByte });
                    continue;
                }

                if (ch is >= '!' and <= '~')
                {
                    Put(i + 1, closed, cost + (open ? 4 : 0), state, Concat(Close(), AsciiZone, (byte)(ch + 0x80)));
                    continue;
                }

                var variants = new List<(int Cost, byte Byte)>();
                if (SingleByte(ch) is { } b && b >= 0xA0)
                    variants.Add((0, b));
                if (ch == 'ё' && SingleByte('е') is { } e1)
                    variants.Add((1, e1));
                if (ch == 'Ё' && SingleByte('Е') is { } e2)
                    variants.Add((1, e2));
                var upper = char.ToUpperInvariant(ch);
                if (upper != ch && SingleByte(upper) is { } ub && ub >= 0xA0)
                    variants.Add((5, ub));

                foreach (var (extra, bb) in variants)
                {
                    if (!open && HiOk(bb))
                        Put(i + 1, bb, cost + extra, state, Array.Empty<byte>());
                    if (open && LoOk(bb))
                        Put(i + 1, closed, cost + extra, state, new[] { (byte)state, bb });
                }

                if (LookAlike.TryGetValue(ch, out var latin))
                    Put(i + 1, closed, cost + 1 + (open ? 4 : 0), state, Concat(Close(), AsciiZone, (byte)(latin + 0x80)));

                if (variants.Count == 0 && !LookAlike.ContainsKey(ch))
                {
                    // Знак, которого нет ни в CP1251, ни в ASCII, — «?».
                    Put(i + 1, closed, cost + 8 + (open ? 4 : 0), state, Concat(Close(), AsciiZone, (byte)('?' + 0x80)));
                }
            }
        }

        // Лучший конец: открытая пара закрывается 0xA0 (весы его отрезают — не видно).
        var bestState = closed;
        var bestCost = int.MaxValue;
        foreach (var (state, step) in dp[n])
        {
            if (step.Cost < bestCost)
            {
                bestCost = step.Cost;
                bestState = state;
            }
        }
        if (n == 0 || bestCost == int.MaxValue)
            return Array.Empty<byte>();

        var chunks = new List<byte[]>();
        var s = bestState;
        for (var i = n; i > 0; i--)
        {
            var step = dp[i][s];
            chunks.Add(step.Bytes);
            s = step.PrevState;
        }
        chunks.Reverse();
        var result = new List<byte>(n * 2 + 2);
        foreach (var c in chunks)
            result.AddRange(c);
        if (bestState != closed)
        {
            result.Add((byte)bestState);
            result.Add(PadByte);
        }

        // Обрезка по целым парам; хвост имени без пары-обрубка.
        if (result.Count > MaxPairs * 2)
            result.RemoveRange(MaxPairs * 2, result.Count - MaxPairs * 2);
        return result.ToArray();
    }

    /// <summary>Поле имени для строки «!0V»: по 2 цифры на байт (байт − 0xA0) и «0000» в конце.</summary>
    public static string Encode(string? name)
    {
        var bytes = EncodeBytes(name);
        var sb = new StringBuilder(bytes.Length * 2 + 4);
        foreach (var b in bytes)
            sb.Append((b - PadByte).ToString("00", CultureInfo.InvariantCulture));
        sb.Append("0000");
        return sb.ToString();
    }

    /// <summary>Что напечатают весы (для подсказки в окне и журнала).</summary>
    public static string Preview(string? name)
    {
        var bytes = EncodeBytes(name);
        var sb = new StringBuilder(bytes.Length);
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var (hi, lo) = (bytes[i], bytes[i + 1]);
            if (hi == SpaceByte && lo == SpaceByte)
                sb.Append(' ');
            else if (hi == AsciiZone)
                sb.Append((char)(lo - 0x80));
            else
                sb.Append(Cp1251.GetString(new[] { hi, lo }).Replace(' ', ' '));
        }
        return sb.ToString().TrimEnd();
    }

    private static byte? SingleByte(char ch)
    {
        var bytes = Cp1251.GetBytes(new[] { ch });
        return bytes.Length == 1 && !(bytes[0] == (byte)'?' && ch != '?') ? bytes[0] : null;
    }

    private static byte[] Concat(byte[] head, byte a, byte b)
    {
        var result = new byte[head.Length + 2];
        head.CopyTo(result, 0);
        result[^2] = a;
        result[^1] = b;
        return result;
    }
}
