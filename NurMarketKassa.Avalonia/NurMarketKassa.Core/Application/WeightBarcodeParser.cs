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
