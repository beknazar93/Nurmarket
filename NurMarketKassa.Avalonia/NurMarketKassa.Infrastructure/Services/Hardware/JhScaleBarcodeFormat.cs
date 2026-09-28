using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: строка формата штрих-кода весов TM-30F / TM-F / TM-xA (JHScale) —
/// «Table 5-3 Descriptions of Barcode Data Items» и «Table 5-4 Factory Default Barcode»
/// руководства (2014, V2.50A). Формат — 6 групп по 4 символа: источник данных (A..Z), длина
/// (0–9), сдвиг вправо (0–9), переполнение (0 — не печатать, 1 — обрезать, 2 — заполнить 0,
/// 3 — заполнить 9). Пример заводского «B-Item 1»: B201E500K500A000A000A000 = флаг (Spec002,
/// по умолчанию 20) 2 цифры + номер PLU 5 цифр + сумма PLU 5 цифр. Контрольную цифру EAN-13
/// весы добавляют сами (12 цифр данных → EAN-13).
///
/// Допущения (в руководстве не описаны, отмечены в окне): вес J печатается без точки — при
/// весе в кг с тремя знаками это граммы; сумма K — в сотых (тыйынах) при двух знаках цены;
/// «сдвиг вправо» — отбрасывание младших цифр.</summary>
public static class JhScaleBarcodeFormat
{
    /// <summary>Источники, которые предлагает конструктор (остальные буквы весы тоже знают,
    /// но касса их не использует).</summary>
    public static readonly char[] Sources = { 'A', 'B', 'E', 'F', 'J', 'K' };

    public const string FactoryItem1 = "B201E500K500A000A000A000";

    public sealed record Group(char Source, int Length, int Shift, int Overflow)
    {
        public string Text => $"{Source}{Length}{Shift}{Overflow}";
    }

    /// <summary>Рекомендуемый формат под раскладку компании: «по PLU» 2+5+5+1 → флаг 2 + PLU 5 +
    /// вес 5 (или сумма 5); «по коду» 2+6+4+1 → флаг 2 + PLU 6 + вес 4 (или сумма 4) — касса при
    /// раскладке «по коду» ищет товар и по PLU (LocalCartService.FindByEmbeddedCode).</summary>
    public static string Recommended(string companyLayout, bool byWeight)
    {
        var isCode = string.Equals(companyLayout, "code", StringComparison.OrdinalIgnoreCase);
        var value = byWeight ? 'J' : 'K';
        return isCode
            ? $"B201E600{value}400A000A000A000"
            : $"B201E500{value}500A000A000A000";
    }

    /// <summary>Разбирает строку формата (24 символа, 6 групп). null — строка неверная.</summary>
    public static List<Group>? Parse(string? format)
    {
        var text = (format ?? "").Trim().ToUpperInvariant();
        if (text.Length != 24)
            return null;
        var groups = new List<Group>(6);
        for (var i = 0; i < 24; i += 4)
        {
            var source = text[i];
            if (source is < 'A' or > 'Z' || !char.IsDigit(text[i + 1]) || !char.IsDigit(text[i + 2]) || !char.IsDigit(text[i + 3]))
                return null;
            var overflow = text[i + 3] - '0';
            if (overflow > 3)
                return null;
            groups.Add(new Group(source, text[i + 1] - '0', text[i + 2] - '0', overflow));
        }
        return groups;
    }

    public static string Format(IEnumerable<Group> groups) => string.Concat(groups.Select(g => g.Text));

    /// <summary>Собирает пример: цифры данных и, если их ровно 12, контрольную цифру EAN-13.
    /// Неизвестный кассе источник даёт «?».</summary>
    public static string BuildSample(IReadOnlyList<Group> groups, int flag, long pluNumber, long itemCode, int grams, decimal amountSom)
    {
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            if (g.Source == 'A' || g.Length == 0)
                continue;
            long? value = g.Source switch
            {
                'B' => flag,
                'E' => pluNumber,
                'F' => itemCode,
                'J' => grams,
                'K' => (long)Math.Round(amountSom * 100m, MidpointRounding.AwayFromZero),
                _ => null,
            };
            if (value is null)
            {
                sb.Append('?', g.Length);
                continue;
            }
            var v = value.Value;
            for (var s = 0; s < g.Shift; s++)
                v /= 10;
            var digits = v.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > g.Length)
            {
                if (g.Overflow == 0)
                    continue; // «не печатать» — группа выпадает, штрих-код станет короче
                digits = g.Overflow switch
                {
                    2 => new string('0', g.Length),
                    3 => new string('9', g.Length),
                    _ => digits[^g.Length..],
                };
            }
            sb.Append(digits.PadLeft(g.Length, '0'));
        }

        var body = sb.ToString();
        if (body.Length == 12 && !body.Contains('?'))
            return body + ShtrikhBarcodeFormat.Ean13CheckDigit(body);
        return body;
    }
}
