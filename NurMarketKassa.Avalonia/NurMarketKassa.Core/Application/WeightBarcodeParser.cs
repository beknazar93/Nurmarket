using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Application;

/// <summary>Парсер весовых штрих-кодов с префиксами 20-29 (EAN-13).
///
/// Раскладка и режим значения зависят от настроек компании (scale_barcode_layout /
/// scale_barcode_mode, GET /api/users/settings/company/ — см. Layout/Mode ниже,
/// обновляются CompanyInfoService при каждом входе и синхронизации):
///   layout "plu"  (по умолчанию): prefix(2) + PLU(5)  + value(5) + check(1)
///   layout "code" (весы Rongta):  prefix(2) + Код(6)  + value(4) + check(1)
///   mode "weight": value — граммы (÷1000 → кг)
///   mode "amount": value — сом × 100 (AmountUnit="tiyin", по умолчанию) либо сом как есть
///                  (AmountUnit="som")
///   mode "auto"  (по умолчанию): по префиксу — "20" → вес, "25" → сумма, иначе вес
/// Раньше раскладка/режим были жёстко зашиты как "plu"+"weight" — это оставлено поведением
/// по умолчанию, чтобы ничего не сломать для магазинов с обычными весами (префикс 20).</summary>
public static class WeightBarcodeParser
{
    /// <summary>Текущая раскладка компании — задаётся CompanyInfoService из UserPreferences
    /// при старте/синхронизации. "plu" или "code".</summary>
    public static string Layout { get; set; } = "plu";

    /// <summary>Текущий режим значения компании — "auto", "weight" или "amount".</summary>
    public static string Mode { get; set; } = "auto";

    /// <summary>2026-09-14: единица суммы в штрих-коде для режима "amount" — "tiyin" (значение
    /// ÷100, по умолчанию) или "som" (значение как есть). Раньше не читалась вовсе — сумма
    /// всегда считалась в тыйынах, хотя NurCRM поддерживает и "som"; компании с этой настройкой
    /// получали сумму (а значит и вес весового товара) в 100 раз меньше настоящей.</summary>
    public static string AmountUnit { get; set; } = "tiyin";

    /// <summary>2026-11-05: хотфикс (весы Штрих-М у клиента: 25 00001 76800 1 = 768,00 сом, а касса читала 76 800 сом и 384 кг,
    /// потому что на сервере у компании стояла единица «som»). Единица суммы теперь может быть задана на самой кассе
    /// (UserPreferences.ScaleBarcodeAmountUnitLocal) и важнее серверной: AmountUnit = локальная ?? серверная
    /// (см. <see cref="ResolveAmountUnit"/>). Предел ниже — для проверки «сумма/вес нереальные».</summary>
    public const string UnitTiyin = "tiyin";

    /// <summary>См. <see cref="UnitTiyin"/>.</summary>
    public const string UnitSom = "som";

    /// <summary>2026-11-05: предел веса одной этикетки, кг. Тяжелее — почти наверняка не та единица суммы (384 кг вместо 3,84).</summary>
    public const double MaxRealisticWeightKg = 600;

    /// <summary>2026-11-05: предел суммы на этикетке, сом. Больше — «сумма выглядит нереально», скорее всего цифры в тыйынах.
    /// Меняется настройкой кассы (UserPreferences.ScaleMaxLabelAmountSom), по умолчанию 20 000.</summary>
    public static double MaxLabelAmountSom { get; set; } = 20000;

    /// <summary>2026-11-05: итоговая единица — локальная (если задана и допустима) важнее серверной; неизвестное значение = «tiyin».</summary>
    public static string ResolveAmountUnit(string? local, string? server)
    {
        static string? Norm(string? v)
        {
            var t = (v ?? "").Trim().ToLowerInvariant();
            return t is UnitSom or UnitTiyin ? t : null;
        }
        return Norm(local) ?? Norm(server) ?? UnitTiyin;
    }

    /// <summary>2026-11-05: сумма в сомах из цифр этикетки при заданной единице.</summary>
    public static double AmountFromRaw(int raw, string? unit) =>
        string.Equals(unit, UnitSom, StringComparison.OrdinalIgnoreCase) ? raw : raw / 100.0;

    /// <summary>2026-11-05: противоположная единица.</summary>
    public static string OtherAmountUnit(string? unit) =>
        string.Equals(unit, UnitSom, StringComparison.OrdinalIgnoreCase) ? UnitTiyin : UnitSom;

    /// <summary>2026-11-05: вес больше предела этикетки (или не число) — защита от «384 кг».</summary>
    public static bool IsUnrealisticWeight(double kg) => kg > MaxRealisticWeightKg || double.IsNaN(kg) || double.IsInfinity(kg);

    /// <summary>2026-11-05: этикетка нереальна — вес больше предела, либо напечатана СУММА выше <see cref="MaxLabelAmountSom"/>
    /// (76 800 сом при весе 384 кг: вес формально меньше 600 кг, но сумма на этикетке весов такой не бывает).</summary>
    public static bool IsUnrealisticLabel(WeightBarcodeParseResult parsed, double weightKg) =>
        IsUnrealisticWeight(weightKg) || (parsed.Kind == WeightBarcodeValueKind.Amount && parsed.Value > MaxLabelAmountSom);

    /// <summary>2026-11-05: сумма или вес, вычисленный из суммы и цены за кг, нереальны (сумма выше предела
    /// <see cref="MaxLabelAmountSom"/> либо вес выше <see cref="MaxRealisticWeightKg"/>). Цена ≤ 0 — вес не проверяется.</summary>
    public static bool IsUnrealisticAmount(double amountSom, double pricePerKg) =>
        amountSom > MaxLabelAmountSom || (pricePerKg > 0 && IsUnrealisticWeight(amountSom / pricePerKg));

    /// <summary>2026-11-05: автоопределение единицы суммы по цифрам этикетки и цене за кг. Возвращает единицу, которую
    /// стоит предложить, — только если при ТЕКУЩЕЙ единице сумма/вес нереальны, а при другой — реальны. Иначе null (не угадываем).</summary>
    public static string? SuggestAmountUnit(int raw, double pricePerKg, string? currentUnit)
    {
        if (raw <= 0)
            return null;
        var current = string.Equals(currentUnit, UnitSom, StringComparison.OrdinalIgnoreCase) ? UnitSom : UnitTiyin;
        var other = OtherAmountUnit(current);
        return IsUnrealisticAmount(AmountFromRaw(raw, current), pricePerKg) && !IsUnrealisticAmount(AmountFromRaw(raw, other), pricePerKg)
            ? other
            : null;
    }

    /// <summary>2026-09-28: префиксы, у которых в штрихкоде записана СУММА, а не вес — например,
    /// весы TM-30F (Dahua) владельца печатают формат FFWWWWWEEEEEC с префиксом 21, а режим компании
    /// «По весу». Без этого касса находила товар, но читала сумму 12,07 как вес 1,207 кг.
    /// Задаётся в окне «Настройки весов TM-30F» → «Штрих-код»; важнее режима компании.</summary>
    public static IReadOnlySet<string> AmountPrefixes { get; set; } = new HashSet<string>();

    /// <summary>2026-09-28: префиксы, у которых в штрихкоде точно ВЕС, — пара к AmountPrefixes, чтобы
    /// правило по префиксу переключалось в обе стороны (Настройки → Весы → «Штрих-код этикеток»,
    /// мастер «Настроить по этикетке»). Тоже важнее режима компании; AmountPrefixes — ещё важнее.</summary>
    public static IReadOnlySet<string> WeightPrefixes { get; set; } = new HashSet<string>();

    /// <summary>2026-11-06: свои раскладки для отдельных префиксов (весы «Штрих РС-200»: число знаков кода 1…6, порядок «код-значение» или
    /// «значение-код», значение — вес или стоимость, контрольная цифра кода). Пусто (по умолчанию) — парсер работает как раньше, по
    /// <see cref="Layout"/>. Раскладка префикса важнее правил <see cref="AmountPrefixes"/>/<see cref="WeightPrefixes"/> и режима компании.</summary>
    public static IReadOnlyList<WeightBarcodeCustomLayout> CustomLayouts { get; set; } = Array.Empty<WeightBarcodeCustomLayout>();

    public static WeightBarcodeCustomLayout? FindCustomLayout(string prefix)
    {
        foreach (var layout in CustomLayouts)
        {
            if (layout.Prefix == prefix)
                return layout;
        }
        return null;
    }

    /// <summary>2026-11-06: этикетка своей раскладки (РС-200), в которой значение (сумма/вес) равно нулю — весы напечатали без стоимости, потому что
    /// цены в них не загружены. Обычный разбор такой код отклоняет (значение 0); касса по этому признаку говорит «загрузите цены», а не «товар не найден».</summary>
    public static bool IsCustomLabelWithZeroValue(string barcode, out string productCode, out WeightBarcodeValueKind kind)
    {
        productCode = "";
        kind = WeightBarcodeValueKind.Weight;
        var code = (barcode ?? "").Trim();
        if (code.Length != 13 || code[0] != '2' || !code.All(char.IsDigit) || !HasValidEan13CheckDigit(code))
            return false;
        var layout = FindCustomLayout(code[..2]);
        if (layout is null)
            return false;
        TryParseCustom(code, layout, out _, out var zero, out productCode);
        kind = layout.Kind;
        return zero;
    }

    /// <summary>2026-11-06: разбор по указанной раскладке без включения её в парсер — для примера и сверки в настройках весов РС-200.</summary>
    public static bool TryParseWithLayout(string barcode, WeightBarcodeCustomLayout layout, out WeightBarcodeParseResult result)
    {
        result = null!;
        var code = (barcode ?? "").Trim();
        if (code.Length != 13 || !code.All(char.IsDigit) || !HasValidEan13CheckDigit(code) || code[..2] != layout.Prefix)
            return false;
        return TryParseCustom(code, layout, out result, out _, out _);
    }

    private static bool TryParseCustom(string code, WeightBarcodeCustomLayout layout, out WeightBarcodeParseResult result, out bool zeroValue, out string productCode)
    {
        result = null!;
        zeroValue = false;
        productCode = "";
        var n = layout.CodeLength;
        if (n is < 1 or > 9)
            return false;
        var body = code.Substring(2, 10);
        var codeDigits = layout.CodeFirst ? body[..n] : body[(10 - n)..];
        var valueDigits = layout.CodeFirst ? body[n..] : body[..(10 - n)];
        if (layout.CodeChecksum)
        {
            // КС кода (руководство РС-200, п. 12.1): сумма цифр кода, младший разряд суммы — последняя цифра поля кода.
            if (n < 2 || codeDigits[^1] - '0' != layout.ControlDigit(codeDigits[..^1]))
                return false;
            codeDigits = codeDigits[..^1];
        }
        if (!long.TryParse(valueDigits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var raw))
            return false;
        if (raw <= 0)
        {
            zeroValue = true;
            productCode = codeDigits;
            return false;
        }
        var value = layout.Kind == WeightBarcodeValueKind.Weight
            ? raw / 1000.0
            : raw / Math.Pow(10, Math.Clamp(layout.AmountDecimals, 0, 3));
        productCode = codeDigits;
        result = new WeightBarcodeParseResult(codeDigits, value, layout.Kind);
        return true;
    }

    public static bool IsEmbeddedWeightBarcode(string barcode) => TryParse(barcode, out _);

    public static bool TryParse(string barcode, out WeightBarcodeParseResult result)
    {
        result = null!;
        var code = (barcode ?? "").Trim();
        if (code.Length != 13)
            return false;

        // Префикс "20" тоже встречается у весов (реальный пример: "2005259002728" с этих
        // весов) — раньше требовался второй символ 1-9, что ошибочно отбрасывало код с "20".
        if (code[0] != '2')
            return false;

        for (var i = 0; i < code.Length; i++)
        {
            if (!char.IsDigit(code[i]))
                return false;
        }

        // Без проверки контрольной цифры любой внутренний код «2…» считался бы весовым,
        // и штучный товар никогда не находился бы по штрих-коду в каталоге.
        if (!HasValidEan13CheckDigit(code))
            return false;

        var prefix = code[..2];

        // 2026-11-06: у префикса своя раскладка (весы Штрих РС-200) — разбираем по ней; остальные префиксы — как раньше.
        if (CustomLayouts.Count > 0 && FindCustomLayout(prefix) is { } custom)
            return TryParseCustom(code, custom, out result, out _, out _);

        var useCodeLayout = string.Equals(Layout, "code", StringComparison.OrdinalIgnoreCase);

        string productCode;
        int rawValue;
        if (useCodeLayout)
        {
            // prefix(2) + Код(6) + value(4) + check(1)
            productCode = code.Substring(2, 6);
            if (!int.TryParse(code.AsSpan(8, 4), out rawValue) || rawValue <= 0)
                return false;
        }
        else
        {
            // prefix(2) + PLU(5) + value(5) + check(1)
            productCode = code.Substring(2, 5);
            if (!int.TryParse(code.AsSpan(7, 5), out rawValue) || rawValue <= 0)
                return false;
        }

        var kind = ResolveKind(prefix);
        var useSomUnit = string.Equals(AmountUnit, "som", StringComparison.OrdinalIgnoreCase);
        double value = kind == WeightBarcodeValueKind.Weight
            ? rawValue / 1000.0                     // граммы → кг
            : useSomUnit ? rawValue : rawValue / 100.0; // сом как есть, либо тыйын×100 → сом

        if (value <= 0)
            return false;

        result = new WeightBarcodeParseResult(productCode, value, kind);
        return true;
    }

    private static WeightBarcodeValueKind ResolveKind(string prefix)
    {
        if (AmountPrefixes.Contains(prefix))
            return WeightBarcodeValueKind.Amount;
        if (WeightPrefixes.Contains(prefix))
            return WeightBarcodeValueKind.Weight;
        if (string.Equals(Mode, "weight", StringComparison.OrdinalIgnoreCase))
            return WeightBarcodeValueKind.Weight;
        if (string.Equals(Mode, "amount", StringComparison.OrdinalIgnoreCase))
            return WeightBarcodeValueKind.Amount;

        // "auto" (или неизвестный режим): по префиксу — 25 = по сумме, иначе по весу.
        return prefix == "25" ? WeightBarcodeValueKind.Amount : WeightBarcodeValueKind.Weight;
    }

    /// <summary>Стандартная контрольная цифра EAN-13: веса 1/3 по первым 12 цифрам.</summary>
    private static bool HasValidEan13CheckDigit(string code)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (code[i] - '0') * (i % 2 == 0 ? 1 : 3);

        var expected = (10 - sum % 10) % 10;
        return expected == code[12] - '0';
    }
}
