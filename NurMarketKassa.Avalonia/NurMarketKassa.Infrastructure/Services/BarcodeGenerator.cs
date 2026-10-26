using System.Security.Cryptography;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-10-26 (требование владельца): общий генератор внутреннего штрихкода EAN-13 для НОВЫХ товаров.
/// Правила: первая цифра 1–9 и никогда 0 (ведущий ноль путают с UPC-A и теряют при экспорте); первая цифра
/// не 2 — касса считает любой 13-значный код на «2…» с верной контрольной цифрой весовым
/// (WeightBarcodeParser.TryParse: префиксы 20–29), и штучный товар продавался бы как весовой с неверной ценой;
/// цифры берутся из криптослучайного генератора; повторов с занятыми кодами нет.</summary>
public static class BarcodeGenerator
{
    /// <summary>Первые цифры, которые генератор НЕ использует: 0 (по просьбе владельца) и 2 (весовые префиксы 20–29).</summary>
    private const string ForbiddenFirstDigits = "02";

    /// <summary>Сколько раз пробовать, прежде чем сдаться (занятых кодов всегда ничтожно мало против ~8·10^11 вариантов).</summary>
    private const int MaxAttempts = 1000;

    /// <summary>Новый EAN-13, которого нет среди <paramref name="taken"/> (сравнение без учёта регистра и пробелов).
    /// Дополнительно <paramref name="isTaken"/> — внешняя проверка (например, индекс размеров).</summary>
    public static string Generate(IEnumerable<string?>? taken = null, Func<string, bool>? isTaken = null)
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (taken is not null)
            foreach (var t in taken)
                if (!string.IsNullOrWhiteSpace(t))
                    busy.Add(t.Trim());

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var code = Create();
            if (!busy.Contains(code) && (isTaken is null || !isTaken(code)))
                return code;
        }
        // Практически недостижимо; вернём последний кандидат — дубль всё равно поймает проверка при сохранении.
        return Create();
    }

    /// <summary>Новый код, свободный в каталоге кассы (основные и дополнительные штрихкоды, размеры) и среди
    /// <paramref name="extraTaken"/> — кодов, уже введённых в открытом окне.</summary>
    public static string GenerateForCatalog(IEnumerable<string?>? extraTaken = null)
    {
        var taken = new List<string?>();
        try
        {
            foreach (var p in CatalogCacheService.Products.ToList())
            {
                if (!string.IsNullOrWhiteSpace(p.Barcode))
                    taken.Add(p.Barcode);
                foreach (var a in p.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>())
                    if (!string.IsNullOrWhiteSpace(a.Barcode))
                        taken.Add(a.Barcode);
            }
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — остаётся индекс размеров и коды окна, дубль поймает проверка при сохранении
        }
        if (extraTaken is not null)
            taken.AddRange(extraTaken);

        return Generate(taken, code =>
        {
            try { return VariantBarcodeIndex.Contains(code); }
            catch { return false; }
        });
    }

    /// <summary>Верна ли контрольная цифра 13-значного кода (для проверки в стенде и в тестах).</summary>
    public static bool HasValidCheckDigit(string code)
    {
        if (code.Length != 13 || !code.All(char.IsAsciiDigit))
            return false;
        return CheckDigit(code.AsSpan(0, 12)) == code[12] - '0';
    }

    private static string Create()
    {
        Span<char> chars = stackalloc char[13];
        int first;
        do
        {
            first = RandomNumberGenerator.GetInt32(1, 10);
        }
        while (ForbiddenFirstDigits.Contains((char)('0' + first)));
        chars[0] = (char)('0' + first);
        for (var i = 1; i < 12; i++)
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        chars[12] = (char)('0' + CheckDigit(chars[..12]));
        return new string(chars);
    }

    /// <summary>Контрольная цифра EAN-13: веса 1/3 по первым 12 цифрам.</summary>
    private static int CheckDigit(ReadOnlySpan<char> first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }
}
