using System;
using System.Collections.Generic;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: весовой штрих-код, который печатают весы ШТРИХ-ПРИНТ, — пример и
/// сверка с тем, что умеет разобрать касса.
///
/// Просьба владельца: «2000001003923 — добавь возможность редактировать штрих-код при отправке
/// на весы». Этикетка весов печатает EAN-13 по параметру весов «Структура ШК» (команды 74h/75h,
/// 15 вариантов из протокола v1.6), префикс — по параметру «Тип префикса ШК» (72h/73h) и
/// «Префиксы ШК» (76h/77h). Касса (и сайт, POST /pos/sales/{id}/scan/) разбирает только две
/// раскладки компании (WeightBarcodeParser): «plu» = 2+5+5+1 и «code» = 2+6+4+1. Поэтому
/// выбор структуры на весах обязан совпадать с раскладкой компании — иначе товар «не найден».
///
/// Буквы структур — как в протоколе: П — префикс, Т — код товара (поле «Код товара» записи
/// ПЛУ, НЕ номер ПЛУ), С — стоимость (МДЕ), В — масса (г), З — номер предприятия GS1,
/// К — контрольная цифра EAN-13, к — контрольная цифра кода товара. Алгоритм «к» протокол не
/// описывает — в примере такая цифра показывается знаком «?», а касса такие структуры не
/// разбирает.</summary>
public static class ShtrikhBarcodeFormat
{
    /// <summary>Структуры ШК 1..14 (0 — «не формировать ШК»), дословно из описания 74h/75h.</summary>
    public static readonly string[] Structures =
    {
        "",
        "ППТТТТТкССССК", "ППТТТТкСССССК", "ППТТТТТТССССК", "ППТТТТТСССССК", "ППТТТТССССССК",
        "ППТТТТТТВВВВК", "ППТТТТТВВВВВК", "ППТТТТВВВВВВК", "ПТТТТТТВВВВВК",
        "ПППЗЗЗЗЗЗЗТТК", "ПППЗЗЗЗЗЗТТТК", "ПППЗЗЗЗЗТТТТК", "ПППЗЗЗЗТТТТТК", "ПППЗЗЗТТТТТТК",
    };

    /// <summary>Структура, которую касса умеет разобрать, и в каком режиме: layout "plu"/"code",
    /// значение — масса (true) или стоимость (false). null — касса такую структуру не читает.</summary>
    public static (string Layout, bool IsWeight)? KassaLayoutOf(int structure) => structure switch
    {
        7 => ("plu", true),   // ПП ТТТТТ ВВВВВ К  = prefix(2)+PLU(5)+граммы(5)+check
        4 => ("plu", false),  // ПП ТТТТТ ССССС К  = prefix(2)+PLU(5)+сумма(5)+check
        6 => ("code", true),  // ПП ТТТТТТ ВВВВ К  = prefix(2)+код(6)+граммы(4)+check
        3 => ("code", false), // ПП ТТТТТТ СССС К  = prefix(2)+код(6)+сумма(4)+check
        _ => null,
    };

    /// <summary>Структура весов, совпадающая с раскладкой компании (для кнопки «Подобрать под
    /// кассу»): по массе — 7 («plu») или 6 («code»).</summary>
    public static int RecommendedStructure(string companyLayout, bool byWeight = true) =>
        string.Equals(companyLayout, "code", StringComparison.OrdinalIgnoreCase)
            ? (byWeight ? 6 : 3)
            : (byWeight ? 7 : 4);

    /// <summary>Максимальное значение поля кода товара (Т) в структуре: 99999 для пяти цифр.</summary>
    public static long MaxProductCode(int structure)
    {
        if (structure <= 0 || structure >= Structures.Length)
            return 0;
        var digits = 0;
        foreach (var ch in Structures[structure])
            if (ch == 'Т')
                digits++;
        return (long)Math.Pow(10, digits) - 1;
    }

    /// <summary>Собирает пример штрих-кода этикетки: 13 цифр, либо с «?» на месте цифры «к»
    /// (её алгоритм протокол не описывает). null — структура 0 («не формировать ШК»).
    /// <paramref name="prefix"/> — число, которое весы ставят в префикс (например 20);
    /// <paramref name="gs1Prefix"/>/<paramref name="gs1Plant"/> — для структур 10..14.</summary>
    public static string? BuildSample(int structure, int prefix, long productCode, int grams, long costMde,
        int gs1Prefix = 0, long gs1Plant = 0)
    {
        if (structure <= 0 || structure >= Structures.Length)
            return null;

        var pattern = Structures[structure];
        var sb = new StringBuilder(13);
        var index = 0;
        while (index < pattern.Length)
        {
            var ch = pattern[index];
            var run = 1;
            while (index + run < pattern.Length && pattern[index + run] == ch)
                run++;

            switch (ch)
            {
                case 'П':
                    // ППП (структуры 10..14) — префикс GS1; ПП/П — префикс из «Типа префикса ШК».
                    sb.Append(Digits(run == 3 ? gs1Prefix : prefix, run));
                    break;
                case 'Т':
                    sb.Append(Digits(productCode, run));
                    break;
                case 'С':
                    sb.Append(Digits(costMde, run));
                    break;
                case 'В':
                    sb.Append(Digits(grams, run));
                    break;
                case 'З':
                    sb.Append(Digits(gs1Plant, run));
                    break;
                case 'к':
                    sb.Append('?');
                    break;
                case 'К':
                    // Контрольная цифра считается по первым 12 символам; при «?» не считается.
                    var body = sb.ToString();
                    sb.Append(body.Contains('?') ? '?' : Ean13CheckDigit(body));
                    break;
            }
            index += run;
        }

        return sb.ToString();
    }

    /// <summary>Правые <paramref name="width"/> цифр числа с ведущими нулями (так весы
    /// укладывают значение в поле фиксированной ширины).</summary>
    private static string Digits(long value, int width)
    {
        if (value < 0)
            value = 0;
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
        return text.Length > width ? text[^width..] : text;
    }

    /// <summary>Контрольная цифра EAN-13 по первым 12 цифрам (веса 1/3) — тот же алгоритм, что
    /// в WeightBarcodeParser.</summary>
    public static char Ean13CheckDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12 && i < first12.Length; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>Какой префикс весы поставят в ШК весового товара: по «Типу префикса ШК»
    /// 0 — номер весов, 1 — групповой код товара (у каждого свой), 2 — весовой префикс,
    /// 3 — префикс GS1. null — префикс зависит от товара (групповой код).</summary>
    public static int? EffectivePrefix(int prefixType, int scaleNumber, int weightPrefix, int gs1Prefix) => prefixType switch
    {
        0 => scaleNumber,
        2 => weightPrefix,
        3 => gs1Prefix,
        _ => null,
    };

    /// <summary>Сверка настроек весов с настройками компании (раскладка/режим/единица суммы).
    /// Возвращает список проблем на русском; пустой — касса разберёт этикетку.</summary>
    public static List<string> CheckCompatibility(int structure, int? prefix, string companyLayout, string companyMode,
        string amountUnit, int decimalPointDigits)
    {
        var problems = new List<string>();
        if (structure == 0)
        {
            problems.Add(Tr.T("на весах выключен штрих-код (структура 0) — этикетки будут без него", "таразада штрих-код өчүк (түзүлүш 0) — этикеткалар ансыз болот", "the barcode is turned off on the scale (structure 0) — labels will have none", "tartıda barkod kapalı (yapı 0) — etiketlerde barkod olmayacak", "tarozida shtrix-kod o‘chirilgan (tuzilma 0) — yorliqlar usiz bo‘ladi"));
            return problems;
        }

        var kassa = KassaLayoutOf(structure);
        if (kassa is null)
        {
            var s = $"{structure} ({Structures[structure]})";
            problems.Add(Tr.T($"касса не разбирает структуру {s}: подходят только 7 и 4 («по PLU») или 6 и 3 («по коду»)",
                $"касса {s} түзүлүштү окубайт: 7 жана 4 («PLU боюнча») же 6 жана 3 («код боюнча») гана туура келет",
                $"the till cannot read structure {s}: only 7 and 4 (“by PLU”) or 6 and 3 (“by code”) work",
                $"kasa {s} yapısını okuyamaz: yalnızca 7 ve 4 (“PLU’ya göre”) veya 6 ve 3 (“koda göre”) uygun",
                $"kassa {s} tuzilmani o‘qiy olmaydi: faqat 7 va 4 («PLU bo‘yicha») yoki 6 va 3 («kod bo‘yicha») mos"));
            return problems;
        }

        var (layout, isWeight) = kassa.Value;
        var normalizedLayout = string.Equals(companyLayout, "code", StringComparison.OrdinalIgnoreCase) ? "code" : "plu";
        if (!string.Equals(layout, normalizedLayout, StringComparison.Ordinal))
            problems.Add(layout == "plu"
                ? Tr.T("структура на весах «по PLU» (2+5+5+1), а в настройках компании раскладка «по коду» (2+6+4+1)", "таразадагы түзүлүш «PLU боюнча» (2+5+5+1), ал эми компаниянын жөндөөсүндө «код боюнча» (2+6+4+1)", "the scale uses “by PLU” (2+5+5+1) but the company setting is “by code” (2+6+4+1)", "tartı “PLU’ya göre” (2+5+5+1), şirket ayarı ise “koda göre” (2+6+4+1)", "tarozida «PLU bo‘yicha» (2+5+5+1), kompaniya sozlamasida esa «kod bo‘yicha» (2+6+4+1)")
                : Tr.T("структура на весах «по коду» (2+6+4+1), а в настройках компании раскладка «по PLU» (2+5+5+1)", "таразадагы түзүлүш «код боюнча» (2+6+4+1), ал эми компаниянын жөндөөсүндө «PLU боюнча» (2+5+5+1)", "the scale uses “by code” (2+6+4+1) but the company setting is “by PLU” (2+5+5+1)", "tartı “koda göre” (2+6+4+1), şirket ayarı ise “PLU’ya göre” (2+5+5+1)", "tarozida «kod bo‘yicha» (2+6+4+1), kompaniya sozlamasida esa «PLU bo‘yicha» (2+5+5+1)"));

        if (prefix is null)
            problems.Add(Tr.T("префикс зависит от группового кода товара — касса узнаёт только префиксы 20–29", "префикс товардын топтук кодуна жараша — касса 20–29 префикстерин гана тааныйт", "the prefix depends on the item group code — the till only recognises prefixes 20–29", "önek ürün grup koduna bağlı — kasa yalnızca 20–29 öneklerini tanır", "prefiks tovar guruh kodiga bog‘liq — kassa faqat 20–29 prefikslarini taniydi"));
        else if (prefix is < 20 or > 29)
            problems.Add(Tr.T($"префикс {prefix} — касса считает весовыми только штрих-коды с префиксом 20–29", $"префикс {prefix} — касса 20–29 префикстүү штрих-коддорду гана салмактуу деп эсептейт", $"prefix {prefix} — the till treats only prefixes 20–29 as weight barcodes", $"önek {prefix} — kasa yalnızca 20–29 önekli barkodları tartı barkodu sayar", $"prefiks {prefix} — kassa faqat 20–29 prefiksli shtrix-kodlarni vaznli deb hisoblaydi"));

        // Режим значения: «auto» — префикс 25 = сумма, остальные = масса.
        var mode = (companyMode ?? "auto").ToLowerInvariant();
        var kassaReadsWeight = mode switch
        {
            "weight" => true,
            "amount" => false,
            _ => prefix != 25,
        };
        if (kassaReadsWeight != isWeight)
            problems.Add(isWeight
                ? Tr.T("весы кладут в ШК массу, а касса при этом префиксе/режиме прочитает число как сумму", "тараза ШКга массаны коёт, ал эми касса бул префикс/режимде санды сумма катары окуйт", "the scale puts weight into the barcode, but with this prefix/mode the till reads it as an amount", "tartı barkoda ağırlık yazar, ama kasa bu önek/modda sayıyı tutar olarak okur", "tarozi ShKga massani qo‘yadi, kassa esa bu prefiks/rejimda sonni summa deb o‘qiydi")
                : Tr.T("весы кладут в ШК стоимость, а касса при этом префиксе/режиме прочитает число как массу", "тараза ШКга наркты коёт, ал эми касса бул префикс/режимде санды масса катары окуйт", "the scale puts the total price into the barcode, but with this prefix/mode the till reads it as weight", "tartı barkoda tutarı yazar, ama kasa bu önek/modda sayıyı ağırlık olarak okur", "tarozi ShKga qiymatni qo‘yadi, kassa esa bu prefiks/rejimda sonni massa deb o‘qiydi"));

        if (!isWeight)
        {
            var scaleInTiyin = decimalPointDigits >= 2;
            var kassaInTiyin = !string.Equals(amountUnit, "som", StringComparison.OrdinalIgnoreCase);
            if (scaleInTiyin != kassaInTiyin)
                problems.Add(scaleInTiyin
                    ? Tr.T("весы пишут сумму в тыйынах, а касса ждёт целые сомы", "тараза сумманы тыйын менен жазат, ал эми касса бүтүн сом күтөт", "the scale writes the amount in tyiyn, but the till expects whole som", "tartı tutarı tiyin olarak yazar, kasa ise tam som bekler", "tarozi summani tiyinda yozadi, kassa esa butun so‘m kutadi")
                    : Tr.T("весы пишут сумму в целых сомах, а касса ждёт тыйыны", "тараза сумманы бүтүн сом менен жазат, ал эми касса тыйын күтөт", "the scale writes the amount in whole som, but the till expects tyiyn", "tartı tutarı tam som olarak yazar, kasa ise tiyin bekler", "tarozi summani butun so‘mda yozadi, kassa esa tiyin kutadi"));
        }

        return problems;
    }
}
