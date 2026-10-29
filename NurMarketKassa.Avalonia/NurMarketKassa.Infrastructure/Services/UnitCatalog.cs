using System.Globalization;

namespace NurMarketKassa.Services;

// 2026-10-29, жалоба владельца «в добавлении товаров (ручном и массовом) нет цены, количества и единицы»: единый справочник единиц измерения
// для карточки товара, массового сканирования и загрузки из файла. Раньше единицы были размазаны: пять кнопок в массовом добавлении
// (BulkScan), свой словарь в BulkImportPlan.NormalizeUnit, голый TextBox в карточке товара, 14 строительных единиц в HardwareProfile.
// Здесь — один список: что хранить на сервере (Storage, по-русски коротко, как всегда: «шт», «кг», «л», «м», «уп»), как показывать на
// 5 языках, дробное ли количество и как единицу пишут в файлах поставщиков (ru/ky/uz/en/tr).
// Правила дробного количества в ПРОДАЖЕ (QuantityRules, HardwareProfile) не дублируются и не меняются: здесь только вопрос «можно ли вписать
// остаток 0,5 при заведении товара». Для единиц, известных HardwareProfile (м, м², л, кг, т…), ответ берётся у него.

/// <summary>Одна единица измерения справочника.</summary>
public sealed record UnitInfo(string Storage, L5 Name, bool Fractional, int Decimals, string[] Aliases, bool Common)
{
    /// <summary>Короткое название на языке интерфейса («шт.», «кг», «л»).</summary>
    public string Display => Name.Get();
}

public static class UnitCatalog
{
    public const string DefaultUnit = "шт";

    private static readonly UnitInfo[] Units =
    [
        new("шт", new L5("шт.", "даана", "pcs", "adet", "dona"), false, 0,
            ["шт", "штука", "штуки", "штук", "штучно", "ш", "sht", "pcs", "pc", "piece", "pieces", "ea", "each", "adet", "дана", "даана", "dona", "dn"], true),
        new("кг", new L5("кг", "кг", "kg", "kg", "kg"), true, 3,
            ["кг", "килограмм", "килограмма", "килограммов", "килограм", "кило", "кгр", "kg", "kgs", "kilo", "kilogram", "kilograms", "kilogramm"], true),
        new("г", new L5("г", "г", "g", "g", "g"), true, 3,
            ["г", "гр", "грамм", "грамма", "граммов", "грам", "g", "gr", "gram", "gramm", "grams"], true),
        new("л", new L5("л", "л", "l", "l", "l"), true, 3,
            ["л", "литр", "литра", "литров", "литир", "l", "lt", "ltr", "litre", "liter", "litr"], true),
        new("мл", new L5("мл", "мл", "ml", "ml", "ml"), true, 3,
            ["мл", "миллилитр", "миллилитра", "миллилитров", "ml", "milliliter", "millilitre", "millilitr"], true),
        new("м", new L5("м", "м", "m", "m", "m"), true, 3,
            ["м", "метр", "метра", "метров", "метры", "погм", "пм", "мп", "m", "mt", "meter", "metre", "metr"], true),
        new("уп", new L5("уп.", "таңгак", "pack", "paket", "qadoq"), false, 0,
            ["уп", "упак", "упаковка", "упаковки", "упаковок", "пачка", "pack", "pk", "paket", "qadoq", "таңгак", "тангак"], true),
        new("пара", new L5("пара", "жуп", "pair", "çift", "juft"), false, 0,
            ["пара", "пар", "pair", "çift", "cift", "juft", "жуп"], true),
        new("м²", new L5("м²", "м²", "m²", "m²", "m²"), true, 3,
            ["м2", "квм", "квметр", "квметра", "m2", "sqm"], false),
        new("м³", new L5("м³", "м³", "m³", "m³", "m³"), true, 3,
            ["м3", "кубм", "куб", "кубометр", "m3", "cbm"], false),
        new("т", new L5("т", "т", "t", "t", "t"), true, 3,
            ["т", "тонна", "тонн", "ton", "tonne", "t"], false),
        new("комплект", new L5("комплект", "топтом", "set", "takım", "to'plam"), false, 0,
            ["комплект", "компл", "кмп", "set", "takım", "takim", "toplam", "топтом"], false),
        new("рулон", new L5("рулон", "түрмөк", "roll", "rulo", "rulon"), false, 0,
            ["рулон", "рул", "roll", "rulo", "rulon", "түрмөк", "турмок"], false),
        new("лист", new L5("лист", "барак", "sheet", "levha", "list"), false, 0,
            ["лист", "листов", "sheet", "levha", "list", "барак"], false),
        new("коробка", new L5("коробка", "куту", "box", "kutu", "quti"), false, 0,
            ["коробка", "короб", "кор", "box", "kutu", "quti", "куту"], false),
        new("мешок", new L5("мешок", "кап", "bag", "torba", "qop"), false, 0,
            ["мешок", "меш", "мешков", "bag", "torba", "qop", "кап"], false),
    ];

    private static readonly Dictionary<string, UnitInfo> ByAlias = BuildAliases();

    private static Dictionary<string, UnitInfo> BuildAliases()
    {
        var map = new Dictionary<string, UnitInfo>(StringComparer.Ordinal);
        foreach (var u in Units)
        {
            map.TryAdd(Key(u.Storage), u);
            foreach (var alias in u.Aliases)
                map.TryAdd(Key(alias), u);
        }
        return map;
    }

    /// <summary>Все единицы справочника в порядке показа (сначала частые).</summary>
    public static IReadOnlyList<UnitInfo> All => Units;

    /// <summary>Частые единицы — для быстрых кнопок.</summary>
    public static IReadOnlyList<UnitInfo> Common { get; } = Units.Where(u => u.Common).ToArray();

    /// <summary>Строгий ключ сравнения: без регистра, точек, пробелов и кавычек; «м²» = «м2» (как SphereCatalog.NormalizeUnitText).</summary>
    private static string Key(string? text) => SphereCatalog.NormalizeUnitText(text);

    /// <summary>Единица по тексту из файла / поля («Шт.», «KG», «литр», «dona»…). Неизвестный текст — null.</summary>
    public static UnitInfo? Find(string? raw)
    {
        var key = Key(raw);
        return key.Length > 0 && ByAlias.TryGetValue(key, out var unit) ? unit : null;
    }

    /// <summary>Что хранить на сервере: «шт», «кг», «г», «л», «мл», «м»… Неизвестный текст — null (строгая проверка файла).</summary>
    public static string? Normalize(string? raw) => Find(raw)?.Storage;

    /// <summary>Что хранить, когда допустима «своя» единица: известная — по справочнику, пустая — «шт», любая другая — как введена (обрезана).</summary>
    public static string NormalizeOrKeep(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultUnit;
        return Find(raw)?.Storage ?? raw.Trim();
    }

    /// <summary>Короткое название для показа на языке интерфейса; своя единица показывается как есть.</summary>
    public static string Display(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return UnitDefaultDisplay;
        return Find(raw)?.Display ?? raw.Trim();
    }

    private static string UnitDefaultDisplay => Units[0].Display;

    /// <summary>Дробное ли количество у единицы. null — единица своя/неизвестная (ничего не запрещаем). Для единиц, которые знает
    /// <see cref="HardwareProfile"/> (м, м², л, кг, т, шт, уп…), ответ его.</summary>
    public static bool? IsFractional(string? raw)
    {
        var unit = Find(raw);
        if (unit is null)
            return null;
        var hardware = HardwareProfile.FindUnit(unit.Storage);
        return hardware is not null ? hardware.Fractional : unit.Fractional;
    }

    /// <summary>Единица «на вес»: после её выбора разумно включить признак «весовой» (кг, г).</summary>
    public static bool IsWeightUnit(string? raw) => Find(raw)?.Storage is "кг" or "г";

    /// <summary>Подсказки по введённому тексту: единицы, у которых название, код или написание на любом языке начинается с него
    /// (затем содержит). Пустой текст — все единицы.</summary>
    public static IReadOnlyList<UnitInfo> Search(string? text)
    {
        var key = Key(text);
        if (key.Length == 0)
            return Units;
        var starts = new List<UnitInfo>();
        var contains = new List<UnitInfo>();
        foreach (var u in Units)
        {
            var names = u.Aliases.Select(Key).Append(Key(u.Storage)).Append(Key(u.Name.Ru)).Append(Key(u.Name.Ky)).Append(Key(u.Name.En)).Append(Key(u.Name.Tur)).Append(Key(u.Name.Uz)).ToList();
            if (names.Any(n => n.StartsWith(key, StringComparison.Ordinal)))
                starts.Add(u);
            else if (key.Length >= 2 && names.Any(n => n.Contains(key, StringComparison.Ordinal)))
                contains.Add(u);
        }
        starts.AddRange(contains);
        return starts;
    }

    /// <summary>Сколько знаков после запятой допустимо в количестве: целое (0) для штучных единиц, 3 для дробных, null — не ограничиваем (своя единица).</summary>
    public static int? QuantityDecimals(string? raw, bool isWeight)
    {
        if (isWeight)
            return 3;
        return IsFractional(raw) switch
        {
            true => 3,
            false => 0,
            _ => null,
        };
    }

    /// <summary>Проверка количества при заведении товара: «0,5 шт» — ошибка («для «шт» количество целое»), «0,5 кг» и «2,25 л» — можно.
    /// null — всё в порядке. Не трогает деньги: только правило ввода остатка.</summary>
    public static string? QuantityProblem(double quantity, string? unit, bool isWeight)
    {
        if (QuantityDecimals(unit, isWeight) is not 0)
            return null;
        if (Math.Abs(quantity - Math.Round(quantity)) < 0.0005)
            return null;
        var shown = Display(unit);
        var list = string.Join(", ", Common.Where(u => u.Fractional).Select(u => u.Display));
        return Tr.T($"Для единицы «{shown}» количество целое. Для дробного количества выберите {list} или включите «Весовой».",
            $"«{shown}» бирдиги үчүн сан бүтүн болушу керек. Бөлчөк сан үчүн {list} тандаңыз же «Салмактуу» күйгүзүңүз.",
            $"The quantity for “{shown}” must be a whole number. For a fractional quantity choose {list} or turn on “By weight”.",
            $"«{shown}» birimi için miktar tam sayı olmalı. Ondalık miktar için {list} seçin veya «Tartılı»yı açın.",
            $"«{shown}» birligi uchun miqdor butun bo'lishi kerak. Kasr miqdor uchun {list} ni tanlang yoki «Vaznli»ni yoqing.");
    }

    /// <summary>Количество без хвостовых нулей, с запятой («2,25»), для показа человеку.</summary>
    public static string FormatQuantity(double quantity) =>
        quantity.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
}
