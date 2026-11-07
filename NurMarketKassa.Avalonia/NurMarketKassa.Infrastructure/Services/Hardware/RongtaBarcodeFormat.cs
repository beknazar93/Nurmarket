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
    // не больше 999,99 сом. Что не поместилось, весы печатают обрезанным (старшие цифры теряются) — этикетка выглядит нормальной, но сумма в ней неверная.

    /// <summary>Наибольшая сумма (сом), которая помещается в штрихкод типа <paramref name="type"/>: (10^цифр − 1) / 100. 0 — в типе нет суммы.</summary>
    public static decimal MaxAmountSom(BarcodeType type) =>
        type.Value == ValueKind.Price && type.ValueDigits > 0 ? (decimal)(Math.Pow(10, type.ValueDigits) - 1) / 100m : 0m;

    /// <summary>Наибольший вес (кг), который помещается в штрихкод весового типа (с известной единицей). 0 — нет веса или единица неизвестна.</summary>
    public static decimal MaxWeightKg(BarcodeType type) =>
        type.Value == ValueKind.Weight && type.ValueDigits > 0 && type.GramsPerUnit > 0 ? (decimal)(Math.Pow(10, type.ValueDigits) - 1) * type.GramsPerUnit / 1000m : 0m;

    /// <summary>Итог сверки потолка суммы со списком цен весовых товаров каталога.</summary>
    public sealed record CeilingReport(decimal MaxAmount, int Products, int OverAtOneKg, int OverAtFiveKg, decimal MaxPrice)
    {
        public bool HasProblem => MaxAmount > 0 && (OverAtOneKg > 0 || OverAtFiveKg > 0);
    }

    /// <summary>Сколько весовых товаров не поместится в штрихкод типа: цена за кг выше потолка (уже 1 кг не влезет) и цена × 5 кг выше потолка (обычная покупка). Для типов без суммы проблем нет.</summary>
    public static CeilingReport CheckCatalog(BarcodeType type, IEnumerable<double> weighedPricesPerKg)
    {
        var max = MaxAmountSom(type);
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

    /// <summary>Текст про потолок суммы выбранного типа на 5 языках («Макс. сумма на этикетке N сом»); для типа по весу — «предела суммы нет, максимум N кг».</summary>
    public static string CeilingText(BarcodeType type)
    {
        var amount = MaxAmountSom(type);
        if (amount > 0)
        {
            var text = amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
            return Tr.T($"Макс. сумма на этикетке: {text} сом. Больше в штрихкод не помещается — весы напечатают обрезанную сумму.", $"Этикеткадагы эң чоң сумма: {text} сом. Андан чоңу штрих-кодго батпайт — тараза кесилген сумманы басат.",
                $"Maximum amount on the label: {text} som. More does not fit into the barcode — the scale will print a truncated amount.", $"Etiketteki en yüksek tutar: {text} som. Fazlası barkoda sığmaz — tartı kesilmiş tutarı basar.", $"Yorliqdagi eng katta summa: {text} so‘m. Undan ko‘pi shtrix-kodga sig‘maydi — tarozi qirqilgan summani chop etadi.");
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
        return Tr.T($"В каталоге весовых товаров с ценой за кг выше {max} сом: {report.OverAtOneKg}; ещё {five} — при весе около 5 кг (самая высокая цена {top}). Их этикетки будут с неверной суммой — выберите структуру по весу.",
            $"Каталогдо кг баасы {max} сомдон жогору салмактуу товарлар: {report.OverAtOneKg}; дагы {five} — 5 кг чамасындагы салмакта (эң жогорку баа {top}). Алардын этикеткаларында сумма туура эмес болот — салмак боюнча түзүлүштү тандаңыз.",
            $"The catalog has weighed products with a price per kg above {max} som: {report.OverAtOneKg}; {five} more at about 5 kg (highest price {top}). Their labels will carry a wrong amount — choose the by-weight structure.",
            $"Katalogda kg fiyatı {max} som'un üzerinde tartılı ürün: {report.OverAtOneKg}; 5 kg civarında {five} tane daha (en yüksek fiyat {top}). Etiketlerinde tutar yanlış olur — ağırlığa göre yapıyı seçin.",
            $"Katalogda kg narxi {max} so‘mdan yuqori vaznli tovarlar: {report.OverAtOneKg}; 5 kg atrofida yana {five} ta (eng yuqori narx {top}). Ularning yorliqlarida summa noto‘g‘ri bo‘ladi — vazn bo‘yicha tuzilmani tanlang.");
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
    /// (для DD — 2 цифры, для D — 1 цифра). null — тип не EAN-13 с данными или вес не выразить.</summary>
    public static string? BuildSample(BarcodeType type, int department, long code, int grams, decimal amountSom)
    {
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
                sb.Append(Digits((long)Math.Round(amountSom * 100m, MidpointRounding.AwayFromZero), type.ValueDigits));
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
