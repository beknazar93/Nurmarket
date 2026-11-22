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
    /// <param name="note">2026-11-21: запомнить код как «созданный нашим генератором» — товар с ним получит отметку «Без заводского штрихкода» (<see cref="NoFactoryBarcodes"/>). Массовая генерация отмечает товары сама и передаёт false.</param>
    public static string Generate(IEnumerable<string?>? taken = null, Func<string, bool>? isTaken = null, bool note = true)
    {
        var code = GenerateCore(taken, isTaken);
        if (note)
            NoFactoryBarcodes.NoteGenerated(code);
        return code;
    }

    private static string GenerateCore(IEnumerable<string?>? taken, Func<string, bool>? isTaken)
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
    public static string GenerateForCatalog(IEnumerable<string?>? extraTaken = null) =>
        Generate(CatalogTaken(extraTaken), IsVariantCode);

    private static bool IsVariantCode(string code)
    {
        try { return VariantBarcodeIndex.Contains(code); }
        catch { return false; }
    }

    /// <summary>Занятые коды каталога кассы (основные и дополнительные) и <paramref name="extraTaken"/> (введённые в открытом окне).</summary>
    private static List<string?> CatalogTaken(IEnumerable<string?>? extraTaken)
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
        return taken;
    }

    /// <summary>2026-11-21 (ответ Умара 20.11): новый код берётся у сервера (<see cref="ServerBarcodes"/>: POST main/barcodes/next/), запасной — локальный <see cref="Generate"/>, если сервер недоступен или не дал годный код.
    /// Код, который уже есть в каталоге кассы или в <paramref name="extraTaken"/>, пропускается (сервер коды не резервирует). 409 barcode_duplicate при сохранении — вызывающий берёт следующий код тем же методом.</summary>
    public static async Task<string> GenerateForCatalogAsync(IEnumerable<string?>? extraTaken = null, CancellationToken ct = default) =>
        (await GenerateManyForCatalogAsync(1, extraTaken, ct).ConfigureAwait(false))[0];

    /// <summary>То же для <paramref name="count"/> кодов сразу (запрос к серверу — не больше 100 за раз). Всегда возвращает ровно <paramref name="count"/> разных кодов.</summary>
    public static async Task<IReadOnlyList<string>> GenerateManyForCatalogAsync(int count, IEnumerable<string?>? extraTaken = null, CancellationToken ct = default)
    {
        count = Math.Max(1, count);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in CatalogTaken(extraTaken))
            if (!string.IsNullOrWhiteSpace(t))
                busy.Add(t.Trim());
        var result = new List<string>(count);
        for (var round = 0; round < 3 && result.Count < count; round++)
        {
            var server = await ServerBarcodes.NextAsync(Math.Min(count - result.Count, ServerBarcodes.MaxCount), ct).ConfigureAwait(false);
            if (server is null)
                break; // сервер недоступен — запасной путь
            var before = result.Count;
            foreach (var code in server)
            {
                if (!ServerBarcodes.LooksUsable(code) || busy.Contains(code) || IsVariantCode(code))
                    continue;
                busy.Add(code);
                result.Add(code);
                if (result.Count == count)
                    break;
            }
            if (result.Count == before)
                break; // ничего годного не пришло — не крутимся
        }
        while (result.Count < count)
        {
            var code = Generate(busy, IsVariantCode);
            busy.Add(code);
            result.Add(code);
        }
        NoFactoryBarcodes.NoteGenerated(result); // 2026-11-21: коды сервера (barcodes/next) — тоже «наши»
        return result;
    }

    /// <summary>2026-11-10 (владелец: «Генерация штрихкода как в вебе!»): штрихкод новому товару строки приёмки — тот же генератор, что «Сгенерировать» в окне товара
    /// (EAN-13, не с 0 и не с 2), свободный в каталоге кассы, индексе размеров и среди штрихкодов ВСЕХ строк этой приёмки. Ставится в строку; false — строке генерировать нельзя
    /// (<see cref="Models.ReceivingLineVm.CanGenerateBarcode"/>: только новый товар без штрихкода).</summary>
    public static bool GenerateForReceivingLine(Models.ReceivingLineVm line, IEnumerable<Models.ReceivingLineVm> allLines)
    {
        if (!line.CanGenerateBarcode)
            return false;
        line.Barcode = GenerateForCatalog(allLines.Select(l => l.Barcode).ToList());
        return true;
    }

    /// <summary>2026-11-21: то же, что <see cref="GenerateForReceivingLine"/>, но код берётся у сервера (запасной — локальный).</summary>
    public static async Task<bool> GenerateForReceivingLineAsync(Models.ReceivingLineVm line, IEnumerable<Models.ReceivingLineVm> allLines, CancellationToken ct = default)
    {
        if (!line.CanGenerateBarcode)
            return false;
        // 2026-11-22 (владелец: «при нажатии на сгенерировать штрих код выходит ошибка» в «Склад → Приёмка»): было ConfigureAwait(false) — после ответа сервера код записывался в строку в потоке пула,
        // а поле «Штрихкод» привязано к окну: «Call from invalid thread» → окно «Ошибка». Теперь возвращаемся в поток вызова (поток окна); ожидание запроса к серверу на пул это не переносит.
        line.Barcode = await GenerateForCatalogAsync(allLines.Select(l => l.Barcode).ToList(), ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>Верна ли контрольная цифра 13-значного кода (для проверки в стенде и в тестах).</summary>
    public static bool HasValidCheckDigit(string code)
    {
        if (code.Length != 13 || !code.All(char.IsAsciiDigit))
            return false;
        return CheckDigit(code.AsSpan(0, 12)) == code[12] - '0';
    }

    /// <summary>2026-11-11 (разбор кыргызстанских штрихкодов): «470» — префикс GS1 Кыргызстана; сгенерированный код не начинается с него, чтобы не выглядеть как настоящий товарный код республики
    /// (клиенты путали «4…» от программы с «470…» с упаковок; шанс совпасть с настоящим кодом ничтожен, но 0,125 % кодов всё равно начинались бы на 470).</summary>
    private const string ReservedPrefix = "470";

    private static string Create()
    {
        Span<char> chars = stackalloc char[13];
        while (true)
        {
            int first;
            do
            {
                first = RandomNumberGenerator.GetInt32(1, 10);
            }
            while (ForbiddenFirstDigits.Contains((char)('0' + first)));
            chars[0] = (char)('0' + first);
            for (var i = 1; i < 12; i++)
                chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
            if (chars[..3].SequenceEqual(ReservedPrefix.AsSpan()))
                continue;
            chars[12] = (char)('0' + CheckDigit(chars[..12]));
            return new string(chars);
        }
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
