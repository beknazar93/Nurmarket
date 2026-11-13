using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: типы весового штрих-кода Rongta RLS1000 / RLS1100 — таблица «Appendix II.
/// Barcode coding table» руководства «Label Scale Software User Manual» (стр. 28–30), только
/// коды EAN-13 (00–19, 21–29). Используется окном «Настройки весов Rongta» для живого примера
/// и сверки с раскладкой компании (WeightBarcodeParser).
///
/// Обозначения руководства: DD — номер отдела (2 цифры), D — отдел (1 цифра), «20..29» —
/// фиксированный префикс, I — код товара (по легенде «PLU No.»; в файле .txp это поле «Code»,
/// см. Appendix I: «Code — see detail in barcode format»), P — сумма, W — вес, C — контрольная
/// цифра EAN-13 (весы считают сами).
///
/// ЧТО НЕ ОПИСАНО в руководстве и здесь принято допущением (помечено в окне):
/// • сумма P печатается в сотых (как «Unit price — non-decimal pattern, 12.34 → 1234» из
///   Appendix I) при «decimal position = 2»;
/// • вес «WWWWW» без точки — единица зависит от настройки весов, поэтому такие типы (09, 19, 29)
///   считаем неподходящими для кассы.</summary>
public static class RongtaBarcodeFormat
{
    public enum PrefixKind { Department2, Fixed2, Department1 }

    public enum ValueKind { None, Price, Weight }

    /// <summary>Один тип штрих-кода. <see cref="GramsPerUnit"/> — сколько граммов в единице
    /// последней цифры веса (W.WWW → 1, WW.WW → 10, WWWW.W → 100); 0 — неизвестно.</summary>
    public sealed record BarcodeType(
        int Type,
        PrefixKind Prefix,
        int FixedPrefix,
        int CodeDigits,
        ValueKind Value,
        int ValueDigits,
        int GramsPerUnit,
        string Pattern);

    public static readonly IReadOnlyList<BarcodeType> Types = BuildTypes();

    private static List<BarcodeType> BuildTypes()
    {
        // Строки 00..09 из таблицы; 10..19 — то же с фиксированным префиксом 20..29.
        var baseRows = new (int Code, ValueKind Kind, int Digits, int Grams, string Value)[]
        {
            (10, ValueKind.None, 0, 0, ""),
            (6, ValueKind.Price, 4, 0, "PPPP"),
            (5, ValueKind.Price, 5, 0, "PPPPP"),
            (4, ValueKind.Price, 6, 0, "PPPPPP"),
            (3, ValueKind.Price, 7, 0, "PPPPPPP"),
            (6, ValueKind.Weight, 4, 1, "W.WWW"),
            (6, ValueKind.Weight, 4, 10, "WW.WW"),
            (5, ValueKind.Weight, 5, 1, "WW.WWW"),
            (5, ValueKind.Weight, 5, 100, "WWWW.W"),
            (5, ValueKind.Weight, 5, 0, "WWWWW"),
        };
        var list = new List<BarcodeType>();
        for (var i = 0; i < baseRows.Length; i++)
        {
            var r = baseRows[i];
            list.Add(new BarcodeType(i, PrefixKind.Department2, 0, r.Code, r.Kind, r.Digits, r.Grams,
                "DD " + new string('I', r.Code) + (r.Value.Length > 0 ? " " + r.Value : "") + " C"));
        }
        for (var i = 0; i < baseRows.Length; i++)
        {
            var r = baseRows[i];
            list.Add(new BarcodeType(10 + i, PrefixKind.Fixed2, 20 + i, r.Code, r.Kind, r.Digits, r.Grams,
                $"{20 + i} " + new string('I', r.Code) + (r.Value.Length > 0 ? " " + r.Value : "") + " C"));
        }

        // 21..29: отдел 1 цифра, код на цифру длиннее.
        var oneDigit = new (int Code, ValueKind Kind, int Digits, int Grams, string Value)[]
        {
            (7, ValueKind.Price, 4, 0, "PPPP"),
            (6, ValueKind.Price, 5, 0, "PPPPP"),
            (5, ValueKind.Price, 6, 0, "PPPPPP"),
            (4, ValueKind.Price, 7, 0, "PPPPPPP"),
            (7, ValueKind.Weight, 4, 1, "W.WWW"),
            (7, ValueKind.Weight, 4, 10, "WW.WW"),
            (6, ValueKind.Weight, 5, 1, "WW.WWW"),
            (6, ValueKind.Weight, 5, 100, "WWWW.W"),
            (6, ValueKind.Weight, 5, 0, "WWWWW"),
        };
        for (var i = 0; i < oneDigit.Length; i++)
        {
            var r = oneDigit[i];
            list.Add(new BarcodeType(21 + i, PrefixKind.Department1, 0, r.Code, r.Kind, r.Digits, r.Grams,
                "D " + new string('I', r.Code) + " " + r.Value + " C"));
        }
        return list;
    }

    public static BarcodeType? Find(int type) => Types.FirstOrDefault(t => t.Type == type);

    // 2026-11-06 (жалоба «у Rongta не видит суммы больше 999»): сумма в штрихкоде — ФИКСИРОВАННОЕ число цифр в сотых; тип 02 («отдел + код + сумма», 5 цифр) вмещает
    // не больше 999,99 сом.
    // 2026-11-12 (фото этикетки клиента: «Бука лакым» 1,150 кг × 1100,00 = 1265,00 — текст и суммы есть, ШТРИХКОДА НЕТ): допущение 06.11 «весы печатают обрезанную сумму»
    // неверно — когда сумма не помещается в поле, весы печатают этикетку БЕЗ штрихкода. Штрихкод собирают сами весы по своему типу (касса тип не передаёт:
    // в «!0V» его нет), поэтому касса может только честно предупредить и предложить структуру по весу (07), у которой предела суммы нет.
    // Граница для типа 02 при «Price point» 2: сумма ≥ 1000,00 сом (100000 сотых — 6 цифр в поле из 5).

    /// <summary>2026-11-12: во сколько единиц весы переводят 1 сом в поле суммы штрихкода — 10^«Price point» (2 знака → сотые, 0 → целые сомы).
    /// ДОПУЩЕНИЕ (в руководстве нет): сумма в штрихкоде идёт с тем же числом знаков, что и цена на весах.</summary>
    private static decimal AmountUnitsPerSom(int pricePoint) => (decimal)Math.Pow(10, Math.Clamp(pricePoint, 0, 3));

    /// <summary>Наибольшая сумма (сом), которая помещается в штрихкод типа <paramref name="type"/>: (10^цифр − 1) / 10^Price point (по умолчанию 2 — сотые). 0 — в типе нет суммы.</summary>
    public static decimal MaxAmountSom(BarcodeType type, int pricePoint = 2) =>
        type.Value == ValueKind.Price && type.ValueDigits > 0 ? (decimal)(Math.Pow(10, type.ValueDigits) - 1) / AmountUnitsPerSom(pricePoint) : 0m;

    /// <summary>2026-11-12: напечатают ли весы штрихкод для этой суммы. Тип по сумме — только если сумма (в единицах Price point) помещается в поле;
    /// типы по весу и без значения — всегда (сумму считает касса).</summary>
    public static bool AmountFits(BarcodeType type, decimal amountSom, int pricePoint = 2)
    {
        if (type.Value != ValueKind.Price || type.ValueDigits <= 0)
            return true;
        var units = Math.Round(amountSom * AmountUnitsPerSom(pricePoint), MidpointRounding.AwayFromZero);
        return units >= 0 && units <= (decimal)(Math.Pow(10, type.ValueDigits) - 1);
    }

    /// <summary>2026-11-12: до какого веса (кг, с точностью до грамма) у товара с ценой <paramref name="pricePerKg"/> весы ещё печатают штрихкод типа по сумме.
    /// null — у типа нет предела суммы (по весу) или цена не задана.</summary>
    public static decimal? MaxWeightKgWithBarcode(BarcodeType type, decimal pricePerKg, int pricePoint = 2)
    {
        var max = MaxAmountSom(type, pricePoint);
        if (max <= 0 || pricePerKg <= 0)
            return null;
        var grams = Math.Floor(max * 1000m / pricePerKg);
        // Округление суммы весами (до единиц Price point) может добавить последний грамм сверх предела — проверяем и отступаем.
        while (grams > 0 && !AmountFits(type, pricePerKg * grams / 1000m, pricePoint))
            grams--;
        return grams / 1000m;
    }

    /// <summary>Наибольший вес (кг), который помещается в штрихкод весового типа (с известной единицей). 0 — нет веса или единица неизвестна.</summary>
    public static decimal MaxWeightKg(BarcodeType type) =>
        type.Value == ValueKind.Weight && type.ValueDigits > 0 && type.GramsPerUnit > 0 ? (decimal)(Math.Pow(10, type.ValueDigits) - 1) * type.GramsPerUnit / 1000m : 0m;

    /// <summary>Итог сверки потолка суммы со списком цен весовых товаров каталога.</summary>
    public sealed record CeilingReport(decimal MaxAmount, int Products, int OverAtOneKg, int OverAtFiveKg, decimal MaxPrice)
    {
        public bool HasProblem => MaxAmount > 0 && (OverAtOneKg > 0 || OverAtFiveKg > 0);
    }

    /// <summary>Сколько весовых товаров не поместится в штрихкод типа: цена за кг выше потолка (уже 1 кг не влезет) и цена × 5 кг выше потолка (обычная покупка). Для типов без суммы проблем нет.</summary>
    public static CeilingReport CheckCatalog(BarcodeType type, IEnumerable<double> weighedPricesPerKg, int pricePoint = 2)
    {
        var max = MaxAmountSom(type, pricePoint);
        var prices = weighedPricesPerKg.Where(p => p > 0).ToList();
        if (max <= 0)
            return new CeilingReport(0m, prices.Count, 0, 0, prices.Count == 0 ? 0m : (decimal)prices.Max());
        var one = prices.Count(p => (decimal)p > max);
        var five = prices.Count(p => (decimal)p * 5m > max);
        return new CeilingReport(max, prices.Count, one, five, prices.Count == 0 ? 0m : (decimal)prices.Max());
    }

    /// <summary>Структура «по весу» без предела суммы с тем же префиксом и (по возможности) тем же числом цифр кода, чтобы коды товаров на этикетках не менялись: например, 02 → 07
    /// («отдел + код 5 + вес WW.WWW»). Вес в граммах (единица известна); 1 цифра отдела не рекомендуется — касса её читает ненадёжно.</summary>
    public static BarcodeType? RecommendedWeightType(BarcodeType current) =>
        Types.Where(t => t.Value == ValueKind.Weight && t.GramsPerUnit == 1 && (t.Prefix != PrefixKind.Department1 || current.Prefix == PrefixKind.Department1))
            .OrderBy(t => t.Prefix == current.Prefix ? 0 : 1)
            .ThenBy(t => Math.Abs(t.CodeDigits - current.CodeDigits))
            .ThenBy(t => t.ValueDigits == 5 ? 0 : 1)
            .ThenBy(t => t.Type)
            .FirstOrDefault();

    /// <summary>2026-11-13: рекомендуемый для Rongta тип — 07 («DD IIIII WW.WWW C», префикс 20, вес в граммах). Проверено вживую на RLS1000 владельца 13.11.2026:
    /// этикетка 5,165 кг на 1074,32 сом напечатала 2000011051655, касса отсканировала верно. У типа по весу нет предела суммы (тип 02 терял штрихкод от 1000 сом).</summary>
    public const int RecommendedTypeNumber = 7;

    /// <summary>2026-11-13: структура 07 (<see cref="RecommendedTypeNumber"/>).</summary>
    public static BarcodeType RecommendedType => Find(RecommendedTypeNumber)!;

    /// <summary>2026-11-13: нужна ли кнопка «Исправить: тип 07» — тип не 07, и либо в нём есть предел суммы / нет веса (сумма, «без значения»), либо касса не прочтёт этикетку
    /// (<paramref name="kassaReadsLabel"/> = false: сверка с кассой дала «НЕ узнает», например тип 27 с отделом из одной цифры → «00…»). Тип по весу, который касса читает, исправлять не нужно.</summary>
    public static bool NeedsRecommendedFix(BarcodeType type, bool kassaReadsLabel) =>
        type.Type != RecommendedTypeNumber && (type.Value != ValueKind.Weight || !kassaReadsLabel);

    /// <summary>2026-11-13: типы в порядке списка: рекомендуемый 07 первым, дальше 00–29 по номеру (в списке окна 07 не нужно искать прокруткой).</summary>
    public static IReadOnlyList<BarcodeType> PickerOrder() =>
        Types.OrderBy(t => t.Type == RecommendedTypeNumber ? 0 : 1).ThenBy(t => t.Type).ToList();

    /// <summary>Текст про потолок суммы выбранного типа на 5 языках («Макс. сумма на этикетке N сом»); для типа по весу — «предела суммы нет, максимум N кг».</summary>
    public static string CeilingText(BarcodeType type, int pricePoint = 2)
    {
        var amount = MaxAmountSom(type, pricePoint);
        if (amount > 0)
        {
            var text = amount.ToString("N" + Math.Clamp(pricePoint, 0, 3), CultureInfo.GetCultureInfo("ru-RU"));
            // 2026-11-12: было «весы напечатают обрезанную сумму» — по этикетке клиента весы печатают этикетку без штрихкода.
            return Tr.T($"Макс. сумма в штрихкоде: {text} сом. Если сумма больше, весы печатают этикетку БЕЗ штрихкода — кассир не сможет её отсканировать.", $"Штрих-коддогу эң чоң сумма: {text} сом. Сумма андан чоң болсо, тараза этикетканы штрих-кодсуз басат — кассир аны сканерлей албайт.",
                $"Maximum amount in the barcode: {text} som. If the amount is larger, the scale prints the label WITHOUT a barcode — the cashier cannot scan it.", $"Barkoddaki en yüksek tutar: {text} som. Tutar daha büyükse tartı etiketi barkodsuz basar — kasiyer onu okutamaz.", $"Shtrix-koddagi eng katta summa: {text} so‘m. Summa kattaroq bo‘lsa, tarozi yorliqni shtrix-kodsiz chop etadi — kassir uni skanerlay olmaydi.");
        }
        var kg = MaxWeightKg(type);
        if (kg > 0)
        {
            var text = kg.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
            return Tr.T($"Предела суммы нет: в штрихкоде вес (до {text} кг), сумму касса считает сама.", $"Сумма чеги жок: штрих-кодто салмак ({text} кг га чейин), сумманы касса өзү эсептейт.",
                $"No amount limit: the barcode holds the weight (up to {text} kg), the till calculates the amount.", $"Tutar sınırı yok: barkodda ağırlık var ({text} kg'a kadar), tutarı kasa hesaplar.", $"Summa chegarasi yo‘q: shtrix-kodda vazn ({text} kg gacha), summani kassa o‘zi hisoblaydi.");
        }
        return "";
    }

    /// <summary>Предупреждение, если в каталоге есть весовые товары, которые не поместятся в потолок суммы типа. Пустая строка — проблем нет.</summary>
    public static string CatalogWarning(CeilingReport report)
    {
        if (!report.HasProblem)
            return "";
        var max = report.MaxAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        var top = report.MaxPrice.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        var five = Math.Max(0, report.OverAtFiveKg - report.OverAtOneKg);
        // 2026-11-12: было «этикетки будут с неверной суммой» — весы печатают такие этикетки без штрихкода.
        return Tr.T($"В каталоге весовых товаров с ценой за кг выше {max} сом: {report.OverAtOneKg}; ещё {five} — при весе около 5 кг (самая высокая цена {top}). Их этикетки выйдут без штрихкода — выберите структуру по весу.",
            $"Каталогдо кг баасы {max} сомдон жогору салмактуу товарлар: {report.OverAtOneKg}; дагы {five} — 5 кг чамасындагы салмакта (эң жогорку баа {top}). Алардын этикеткалары штрих-кодсуз чыгат — салмак боюнча түзүлүштү тандаңыз.",
            $"The catalog has weighed products with a price per kg above {max} som: {report.OverAtOneKg}; {five} more at about 5 kg (highest price {top}). Their labels will come out without a barcode — choose the by-weight structure.",
            $"Katalogda kg fiyatı {max} som'un üzerinde tartılı ürün: {report.OverAtOneKg}; 5 kg civarında {five} tane daha (en yüksek fiyat {top}). Etiketleri barkodsuz çıkar — ağırlığa göre yapıyı seçin.",
            $"Katalogda kg narxi {max} so‘mdan yuqori vaznli tovarlar: {report.OverAtOneKg}; 5 kg atrofida yana {five} ta (eng yuqori narx {top}). Ularning yorliqlari shtrix-kodsiz chiqadi — vazn bo‘yicha tuzilmani tanlang.");
    }

    /// <summary>2026-11-12 (этикетка «Бука лакым» 1265,00 без штрихкода): предупреждение после отправки на весы со структурой по сумме — у каких товаров и с какого веса
    /// весы перестанут печатать штрихкод (сумма не помещается в поле). Показываются товары, у которых штрихкод пропадёт раньше 5 кг, самые дорогие первыми.
    /// Пустая строка — структура по весу или все товары помещаются до 5 кг.</summary>
    public static string SendWarning(BarcodeType type, IEnumerable<(string Name, decimal PricePerKg)> products, int pricePoint = 2)
    {
        var max = MaxAmountSom(type, pricePoint);
        if (max <= 0)
            return "";
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var risky = products
            .Where(p => p.PricePerKg > 0)
            .Select(p => (p.Name, p.PricePerKg, Kg: MaxWeightKgWithBarcode(type, p.PricePerKg, pricePoint) ?? 0m))
            .Where(p => p.Kg < 5m)
            .OrderByDescending(p => p.PricePerKg)
            .ToList();
        if (risky.Count == 0)
            return "";
        var list = string.Join(", ", risky.Take(3).Select(p => $"«{p.Name}» — {p.Kg.ToString("0.000", ru)}"))
                   + (risky.Count > 3 ? $" (+{risky.Count - 3})" : "");
        var maxText = max.ToString("N" + Math.Clamp(pricePoint, 0, 3), ru);
        return Tr.T($"Структура штрихкода весов {type.Type:00} вмещает сумму только до {maxText} сом — при большей сумме весы печатают этикетку без штрихкода. Штрихкод будет только до веса (кг): {list}. Чтобы штрихкод был всегда: «Настройки весов Rongta» → «Штрих-код» → «Выбрать структуру без ограничения суммы» и тот же тип на самих весах.",
            $"Таразанын {type.Type:00} штрих-код түзүлүшүнө сумма {maxText} сомго чейин гана батат — сумма чоң болсо, тараза этикетканы штрих-кодсуз басат. Штрих-код салмак (кг) ушуга чейин гана болот: {list}. Штрих-код дайыма болушу үчүн: «Rongta таразасынын жөндөөлөрү» → «Штрих-код» → «Сумма чегисиз түзүлүштү тандоо» жана таразанын өзүндө ошол эле түр.",
            $"Scale barcode structure {type.Type:00} holds an amount only up to {maxText} som — with a larger amount the scale prints the label without a barcode. The barcode is printed only up to the weight (kg): {list}. To always get a barcode: “Rongta scale settings” → “Barcode” → “Choose the structure without an amount limit” and the same type on the scale itself.",
            $"Tartının {type.Type:00} barkod yapısı yalnızca {maxText} som'a kadar tutar alır — daha büyük tutarda tartı etiketi barkodsuz basar. Barkod yalnızca şu ağırlığa (kg) kadar olur: {list}. Barkodun her zaman olması için: «Rongta tartı ayarları» → «Barkod» → «Tutar sınırı olmayan yapıyı seç» ve tartının kendisinde aynı tür.",
            $"Tarozining {type.Type:00} shtrix-kod tuzilmasi summani faqat {maxText} so‘mgacha sig‘diradi — summa kattaroq bo‘lsa, tarozi yorliqni shtrix-kodsiz chop etadi. Shtrix-kod faqat shu vazngacha (kg) bo‘ladi: {list}. Shtrix-kod doim bo‘lishi uchun: «Rongta tarozi sozlamalari» → «Shtrix-kod» → «Summa chegarasiz tuzilmani tanlash» va tarozining o‘zida xuddi shu tur.");
    }

    /// <summary>Сообщение кассы при скане этикетки, у которой поле суммы заполнено до предела (все девятки): настоящая сумма могла не поместиться.</summary>
    public static string AmountFieldFullMessage(double amountSom)
    {
        var text = amountSom.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));
        return Tr.T($"Этикетка весов не вмещает сумму: в ней предельное значение {text} сом, настоящая сумма может быть больше. Выберите на весах структуру штрихкода по весу (Настройки → Весы → Rongta → Штрих-код → «Выбрать структуру без ограничения суммы»).",
            $"Тараза этикеткасы сумманы батыра албайт: анда чектүү маани {text} сом, чыныгы сумма андан чоң болушу мүмкүн. Таразада салмак боюнча штрих-код түзүлүшүн тандаңыз (Жөндөөлөр → Тараза → Rongta → Штрих-код → «Сумма чегисиз түзүлүштү тандоо»).",
            $"The scale label cannot hold the amount: it carries the limit value {text} som, the real amount may be larger. Choose the by-weight barcode structure on the scale (Settings → Scales → Rongta → Barcode → “Choose the structure without an amount limit”).",
            $"Tartı etiketi tutarı barındıramıyor: üzerinde sınır değeri {text} som var, gerçek tutar daha büyük olabilir. Tartıda ağırlığa göre barkod yapısını seçin (Ayarlar → Tartılar → Rongta → Barkod → «Tutar sınırı olmayan yapıyı seç»).",
            $"Tarozi yorlig‘i summani sig‘dira olmaydi: unda chegaraviy qiymat {text} so‘m, haqiqiy summa undan katta bo‘lishi mumkin. Tarozida vazn bo‘yicha shtrix-kod tuzilmasini tanlang (Sozlamalar → Tarozilar → Rongta → Shtrix-kod → «Summa chegarasiz tuzilmani tanlash»).");
    }

    /// <summary>Собирает пример EAN-13. <paramref name="department"/> — номер отдела товара
    /// (для DD — 2 цифры, для D — 1 цифра). null — тип не EAN-13 с данными или вес не выразить.
    /// 2026-11-12: null и тогда, когда сумма не помещается в поле (<see cref="AmountFits"/>) — весы такой штрихкод не печатают вовсе; раньше здесь
    /// получался обрезанный код с ложной суммой (1250,00 → «25000»). <paramref name="pricePoint"/> — знаки цены на весах (единица суммы в штрихкоде).</summary>
    public static string? BuildSample(BarcodeType type, int department, long code, int grams, decimal amountSom, int pricePoint = 2)
    {
        if (!AmountFits(type, amountSom, pricePoint))
            return null;
        var sb = new StringBuilder(13);
        switch (type.Prefix)
        {
            case PrefixKind.Department2: sb.Append(Digits(department, 2)); break;
            case PrefixKind.Fixed2: sb.Append(Digits(type.FixedPrefix, 2)); break;
            case PrefixKind.Department1: sb.Append(Digits(department, 1)); break;
        }
        sb.Append(Digits(code, type.CodeDigits));
        switch (type.Value)
        {
            case ValueKind.Price:
                sb.Append(Digits((long)Math.Round(amountSom * AmountUnitsPerSom(pricePoint), MidpointRounding.AwayFromZero), type.ValueDigits));
                break;
            case ValueKind.Weight:
                if (type.GramsPerUnit <= 0)
                    return null;
                sb.Append(Digits(grams / type.GramsPerUnit, type.ValueDigits));
                break;
        }
        if (sb.Length != 12)
            return null;
        sb.Append(ShtrikhBarcodeFormat.Ean13CheckDigit(sb.ToString()));
        return sb.ToString();
    }

    private static string Digits(long value, int width)
    {
        if (value < 0)
            value = 0;
        var text = value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        return text.Length > width ? text[^width..] : text;
    }
}
