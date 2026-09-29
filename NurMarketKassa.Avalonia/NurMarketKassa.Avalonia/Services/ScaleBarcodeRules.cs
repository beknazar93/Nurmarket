using System.Globalization;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Прочтение этикетки весов: префикс, код товара, найденный товар и оба варианта значения
/// (вес и сумма) — для мастера «Настроить по этикетке».</summary>
internal sealed record ScaleLabelReading(
    string Barcode,
    string Prefix,
    string ProductCode,
    string ValueDigits,
    CatalogProductTileVmRef? Product,
    double PricePerKg,
    double WeightKg,
    double AmountSom,
    double WeightFromAmountKg,
    WeightBarcodeValueKind CurrentKind);

/// <summary>Товар из каталога кассы для прочтения этикетки (название, PLU, цена).</summary>
internal sealed record CatalogProductTileVmRef(string Title, int? Plu, string? Article, string PriceLine);

/// <summary>2026-09-28 (поздно вечером), живой баг владельца «опять сумма неправильно»: весы TM-30F
/// печатают СУММУ с префиксом 21 («Алма» 0,220 кг × 60 = 13,20 → 2101003013207), а касса читала
/// 01320 как вес 1,320 кг. Правило «что в штрихкоде после кода товара — вес или сумма» теперь
/// задаётся по КАЖДОМУ префиксу 20–29 явно: список в Настройки → Весы → «Штрих-код: вес / сумма»
/// и мастер «Настроить по этикетке». Правило важнее режима компании с сайта и применяется сразу
/// (WeightBarcodeParser.AmountPrefixes / WeightPrefixes), без перезапуска.</summary>
internal static class ScaleBarcodeRules
{
    public static readonly string[] AllPrefixes = { "20", "21", "22", "23", "24", "25", "26", "27", "28", "29" };

    private static HashSet<string> AmountSet() => UserPreferences.ParseAmountPrefixes(UserPreferences.Instance.ScaleAmountPrefixes);
    private static HashSet<string> WeightSet() => UserPreferences.ParseAmountPrefixes(UserPreferences.Instance.ScaleWeightPrefixes);

    /// <summary>Что по режиму компании (сайт → Весы → Настройки) стоит в штрихкоде с этим префиксом.</summary>
    public static WeightBarcodeValueKind CompanyDefault(string prefix)
    {
        var mode = (WeightBarcodeParser.Mode ?? "auto").ToLowerInvariant();
        return mode switch
        {
            "weight" => WeightBarcodeValueKind.Weight,
            "amount" => WeightBarcodeValueKind.Amount,
            _ => prefix == "25" ? WeightBarcodeValueKind.Amount : WeightBarcodeValueKind.Weight,
        };
    }

    /// <summary>Как касса прочтёт префикс сейчас (правило кассы важнее режима компании).</summary>
    public static WeightBarcodeValueKind Effective(string prefix)
    {
        if (WeightBarcodeParser.AmountPrefixes.Contains(prefix))
            return WeightBarcodeValueKind.Amount;
        if (WeightBarcodeParser.WeightPrefixes.Contains(prefix))
            return WeightBarcodeValueKind.Weight;
        return CompanyDefault(prefix);
    }

    /// <summary>Для префикса задано своё правило кассы (а не режим компании).</summary>
    public static bool IsExplicit(string prefix) =>
        WeightBarcodeParser.AmountPrefixes.Contains(prefix) || WeightBarcodeParser.WeightPrefixes.Contains(prefix);

    /// <summary>1.17.27: для префиксов кроме 20 и 25 без правила касса при первом скане этикетки сама
    /// спрашивает «сумма или вес» (BasketPanelViewModel.ConfirmWeightBarcodeKindAsync).</summary>
    public static bool AsksOnScan(string prefix) => prefix is not ("20" or "25") && !IsExplicit(prefix);

    /// <summary>Префиксы, которые показываются в списке: 20, всё, для чего есть правило, префикс
    /// весов TM-30F из их настроек и дополнительно показанные (мастер, кнопка «Другой префикс»).</summary>
    public static List<string> PrefixesInUse(IEnumerable<string>? extra = null)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal) { "20" };
        foreach (var p in AmountSet().Concat(WeightSet()))
            set.Add(p);
        var tm = UserPreferences.Instance.TmScaleBarcodePrefix;
        if (string.Equals(UserPreferences.Instance.ScaleBrand, "tm", StringComparison.Ordinal) && tm is >= 20 and <= 29)
            set.Add(tm.ToString("00", CultureInfo.InvariantCulture));
        if (!string.Equals(WeightBarcodeParser.Mode, "weight", StringComparison.OrdinalIgnoreCase))
            set.Add("25");
        if (extra is not null)
        {
            foreach (var p in extra)
            {
                if (AllPrefixes.Contains(p))
                    set.Add(p);
            }
        }
        return set.ToList();
    }

    /// <summary>Задаёт правило для префикса в памяти (парсер и настройки), без записи на диск —
    /// запись делает вызывающий (SaveToDisk), чтобы всё сохранялось одним разом.</summary>
    public static void Apply(string prefix, WeightBarcodeValueKind kind)
    {
        var amount = AmountSet();
        var weight = WeightSet();
        amount.Remove(prefix);
        weight.Remove(prefix);
        (kind == WeightBarcodeValueKind.Amount ? amount : weight).Add(prefix);
        Store(amount, weight);
    }

    /// <summary>Правила кассы по всем префиксам снимаются — снова решает режим компании.</summary>
    public static void ResetAll() => Store(new HashSet<string>(), new HashSet<string>());

    private static void Store(HashSet<string> amount, HashSet<string> weight)
    {
        var prefs = UserPreferences.Instance;
        prefs.ScaleAmountPrefixes = string.Join(",", amount.OrderBy(p => p, StringComparer.Ordinal));
        prefs.ScaleWeightPrefixes = string.Join(",", weight.OrderBy(p => p, StringComparer.Ordinal));
        WeightBarcodeParser.AmountPrefixes = amount;
        WeightBarcodeParser.WeightPrefixes = weight;
    }

    /// <summary>Задаёт правило и сразу сохраняет настройки на диск.</summary>
    public static void ApplyAndSave(string prefix, WeightBarcodeValueKind kind)
    {
        Apply(prefix, kind);
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log($"Весы: правило штрихкода — префикс {prefix}: {(kind == WeightBarcodeValueKind.Amount ? "сумма" : "вес")}", "SCALES");
    }

    /// <summary>2026-09-28 (просьба владельца): у весов TM-30F выбран формат с суммой без веса
    /// (например FFWWWWWEEEEEC) — префикс весов сам получает правило «сумма». Только ДОБАВЛЯЕТ
    /// правило (формат с весом ничего не снимает). Возвращает префикс, если правило поменялось.</summary>
    public static string? EnsureTmAmountRule()
    {
        var prefs = UserPreferences.Instance;
        var format = prefs.TmScaleDahuaBarcode;
        if (string.IsNullOrEmpty(format) || !DahuaTmBarcodeFormat.HasAmount(format) || DahuaTmBarcodeFormat.HasWeight(format))
            return null;
        if (prefs.TmScaleBarcodePrefix is < 20 or > 29)
            return null;
        var prefix = prefs.TmScaleBarcodePrefix.ToString("00", CultureInfo.InvariantCulture);
        if (Effective(prefix) == WeightBarcodeValueKind.Amount && WeightBarcodeParser.AmountPrefixes.Contains(prefix))
            return null;
        ApplyAndSave(prefix, WeightBarcodeValueKind.Amount);
        return prefix;
    }

    // ------------------------------------------------------------------ прочтение этикетки

    /// <summary>Контрольная цифра EAN-13 по первым 12 цифрам.</summary>
    public static int Ean13Check(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }

    /// <summary>Длины частей по раскладке компании: «по PLU» 2+5+5+1, «по коду» 2+6+4+1.</summary>
    public static (int CodeLength, int ValueLength) Layout() =>
        string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase) ? (6, 4) : (5, 5);

    /// <summary>Сумма из цифр значения по единице компании (тыйын — ÷100, сом — как есть).</summary>
    public static double AmountFromRaw(int raw) =>
        string.Equals(WeightBarcodeParser.AmountUnit, "som", StringComparison.OrdinalIgnoreCase) ? raw : raw / 100.0;

    /// <summary>Разбирает штрихкод с этикетки в ОБА прочтения. null и текст ошибки — если это не
    /// весовой EAN-13 (не 13 цифр, не 2…, неверная контрольная цифра, значение 0).</summary>
    public static ScaleLabelReading? Read(string? input, out string error)
    {
        error = "";
        var code = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 13)
        {
            error = Tr.T($"Нужно 13 цифр штрихкода, а введено {code.Length}.", $"Штрих-коддун 13 саны керек, киргизилгени {code.Length}.",
                $"A barcode needs 13 digits; {code.Length} entered.", $"Barkod 13 hane olmalı; {code.Length} girildi.", $"Shtrix-kod 13 raqam bo‘lishi kerak, kiritilgani {code.Length}.");
            return null;
        }
        if (code[0] != '2')
        {
            error = Tr.T("Весовой штрихкод начинается с 2 (префикс 20–29). Это штрихкод обычного товара.",
                "Салмактуу штрих-код 2 менен башталат (20–29 префикси). Бул кадимки товардын штрих-коду.",
                "A scale barcode starts with 2 (prefix 20–29). This is a regular product barcode.",
                "Tartı barkodu 2 ile başlar (20–29 öneki). Bu normal bir ürün barkodu.",
                "Vaznli shtrix-kod 2 bilan boshlanadi (20–29 prefiks). Bu oddiy tovar shtrix-kodi.");
            return null;
        }
        if (Ean13Check(code[..12]) != code[12] - '0')
        {
            error = Tr.T("Последняя (контрольная) цифра не сходится — проверьте, правильно ли введён штрихкод.",
                "Акыркы (текшерүү) сан туура келбейт — штрих-код туура киргизилгенин текшериңиз.",
                "The last (check) digit does not match — check the barcode was entered correctly.",
                "Son (kontrol) hane tutmuyor — barkodun doğru girildiğini kontrol edin.",
                "Oxirgi (nazorat) raqam mos kelmaydi — shtrix-kod to‘g‘ri kiritilganini tekshiring.");
            return null;
        }

        var (codeLength, valueLength) = Layout();
        var prefix = code[..2];
        var productCode = code.Substring(2, codeLength);
        var valueDigits = code.Substring(2 + codeLength, valueLength);
        var raw = int.Parse(valueDigits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (raw <= 0)
        {
            error = Tr.T("Значение в штрихкоде — ноль.", "Штрих-коддогу маани — нөл.", "The value in the barcode is zero.", "Barkoddaki değer sıfır.", "Shtrix-koddagi qiymat — nol.");
            return null;
        }

        var tile = LocalCartService.FindByEmbeddedCode(productCode);
        var price = tile is null ? 0 : LocalCartService.ParsePrice(tile.PriceLine);
        var amount = AmountFromRaw(raw);
        var weightFromAmount = new WeightBarcodeParseResult(productCode, amount, WeightBarcodeValueKind.Amount).ResolveWeightKg(price);
        return new ScaleLabelReading(
            code,
            prefix,
            productCode,
            valueDigits,
            tile is null ? null : new CatalogProductTileVmRef(tile.Title, tile.Plu, tile.Article, tile.PriceLine),
            price,
            raw / 1000.0,
            amount,
            weightFromAmount,
            Effective(prefix));
    }

    /// <summary>«0,220 кг × 60,00 = 13,20 сом» — строка чека, как её посчитает касса.</summary>
    public static string LineText(double kg, double price)
    {
        var qty = Math.Round(kg, 3, MidpointRounding.AwayFromZero);
        var total = Math.Round((decimal)qty * (decimal)price, 2, MidpointRounding.AwayFromZero);
        return price > 0
            ? $"{Kg(qty)} × {Money(price)} = {Money((double)total)} {Som()}"
            : Kg(qty);
    }

    public static string Kg(double kg) => kg.ToString("0.000", CultureInfo.CurrentCulture) + Tr.T(" кг", " кг", " kg", " kg", " kg");
    public static string Money(double som) => som.ToString("0.00", CultureInfo.CurrentCulture);
    public static string Som() => Tr.T("сом", "сом", "som", "som", "so‘m");

    /// <summary>Правило префикса одним словом для сводки: «вес» / «сумма» / «спросит».</summary>
    public static string RuleWord(string prefix) => AsksOnScan(prefix)
        ? Tr.T("спросит при скане", "сканда сурайт", "asks on scan", "okutmada sorar", "skanda so‘raydi")
        : KindWord(Effective(prefix));

    /// <summary>«вес» / «сумма» на языке интерфейса.</summary>
    public static string KindWord(WeightBarcodeValueKind kind) => kind == WeightBarcodeValueKind.Amount
        ? Tr.T("сумма", "сумма", "amount", "tutar", "summa")
        : Tr.T("вес", "салмак", "weight", "ağırlık", "vazn");

    /// <summary>Живой пример для префикса: «2101003013207 → Алма: 0,220 кг × 60,00 = 13,20 сом»
    /// по первому весовому товару каталога (или условному PLU 1 по цене 100).</summary>
    public static string ExampleFor(string prefix, WeightBarcodeValueKind kind)
    {
        var (codeLength, valueLength) = Layout();
        var tile = CatalogCacheService.Products.FirstOrDefault(p => p.IsWeighted && p.Plu is > 0 && p.Plu < Math.Pow(10, codeLength));
        var plu = tile?.Plu ?? 1;
        var price = tile is null ? 100.0 : LocalCartService.ParsePrice(tile.PriceLine);
        if (price <= 0)
            price = 100.0;
        const int grams = 220;
        long raw = kind == WeightBarcodeValueKind.Weight
            ? grams
            : (long)Math.Round((decimal)price * grams / 1000m * (string.Equals(WeightBarcodeParser.AmountUnit, "som", StringComparison.OrdinalIgnoreCase) ? 1m : 100m), MidpointRounding.AwayFromZero);
        if (raw >= (long)Math.Pow(10, valueLength))
            return "";
        var first12 = prefix + plu.ToString(new string('0', codeLength), CultureInfo.InvariantCulture) + raw.ToString(new string('0', valueLength), CultureInfo.InvariantCulture);
        var barcode = first12 + Ean13Check(first12).ToString(CultureInfo.InvariantCulture);
        var name = tile?.Title ?? Tr.T("товар PLU 1", "PLU 1 товары", "item PLU 1", "PLU 1 ürünü", "PLU 1 tovari");
        return $"{barcode} → {name}: {LineText(grams / 1000.0, price)}";
    }
}
