using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (просьба владельца: «функция для мясных магазинов — при покупке коровы вес сразу убавлять, для продажи остаётся другой вес»): что мешает посчитать выход.</summary>
public enum CarcassYieldProblem
{
    None,
    /// <summary>Не указано, сколько куплено (вес «с костью»).</summary>
    NoGross,
    /// <summary>Вес с больше чем 3 знаками после запятой (сервер не принимает; не округляем молча).</summary>
    GrossPrecision,
    /// <summary>Не указан ни выход в %, ни вес в продажу.</summary>
    NoYield,
    /// <summary>Выход должен быть больше 0 и не больше 100 %.</summary>
    YieldRange,
    /// <summary>Вес в продажу должен быть больше 0 и не больше купленного.</summary>
    NetRange,
    /// <summary>Вес в продажу с больше чем 3 знаками после запятой.</summary>
    NetPrecision,
    /// <summary>Числа больше разумных пределов (защита от штрихкода в поле).</summary>
    TooBig,
}

/// <summary>Входы расчёта выхода. Вес брутто обязателен; цена за единицу брутто ИЛИ общая сумма; выход % ИЛИ вес нетто. Если задано оба из пары — берётся «главное» (сумма, вес нетто):
/// окно хранит только то, что вписали последним, поэтому одновременно приходит одно из двух.</summary>
public sealed record CarcassYieldInput(decimal Gross, decimal? PricePerUnitGross = null, decimal? Total = null, decimal? YieldPercent = null, decimal? Net = null, decimal? MarkupPercent = null);

/// <summary>Итог расчёта выхода. Все суммы — в decimal; <see cref="HasTotal"/> = false — цена и сумма ещё не заданы (вес в продажу уже посчитан).</summary>
public sealed record CarcassYieldResult(
    decimal Gross, decimal Net, decimal Waste, decimal YieldPercent, decimal WastePercent,
    bool HasTotal, decimal Total, decimal PricePerUnitGross, decimal CostPerUnitNet, decimal UnitPrice, decimal ServerLineTotal, decimal RoundingDelta, decimal? SaleHint)
{
    /// <summary>Сервер запишет сумму строки, отличающуюся от общей суммы закупки хотя бы на копейку (цена округляется до 4 знаков, вес до 3).</summary>
    public bool HasRoundingLoss => HasTotal && Math.Abs(RoundingDelta) > 0.01m;
}

/// <summary>
/// 2026-11-26 (просьба владельца, «Выход мяса»): мясной магазин покупает тушу по весу «брутто», а в продажу идёт меньше (кость, жир, усушка). Чистый расчёт без окон и сети (всё в decimal, округления только здесь).
///
/// Пример: 300 кг по 400 сом/кг, выход 75 % → в продаже 225 кг, отходы 75 кг, сумма закупки 120 000, себестоимость 1 кг в продаже 533,33 сом.
/// Общая сумма закупки НЕ меняется (= брутто × цена). На сервер приход идёт без новых полей API: количество = нетто, цена закупки = сумма ÷ нетто (до 4 знаков, как хранит сервер),
/// поэтому «количество × цена» сервера может отличаться от суммы закупки на копейки — <see cref="PriceForTotal"/> выбирает цену так, чтобы расхождение было наименьшим,
/// а <see cref="CarcassYieldResult.RoundingDelta"/> его показывает. Долг поставщику и касса считаются сервером по строке прихода, окно показывает точную сумму закупки.
/// </summary>
public static class CarcassYield
{
    /// <summary>Знаков веса (сервер принимает количество до 3 знаков).</summary>
    public const int WeightDecimals = 3;

    /// <summary>Знаков цены закупки (сервер хранит 4 знака, ReceivingPricing.PurchaseDecimals).</summary>
    public const int PriceDecimals = 4;

    /// <summary>Предел веса и суммы: больше — почти наверняка штрихкод в поле.</summary>
    private const decimal MaxWeight = 1_000_000m;
    private const decimal MaxMoney = 9_999_999_999m;

    /// <summary>Вес в продажу из выхода: брутто × выход / 100, до 3 знаков (грамм); середина — вверх (AwayFromZero). 300 × 75 % = 225.</summary>
    public static decimal NetFromYield(decimal gross, decimal yieldPercent) =>
        Math.Round(gross * yieldPercent / 100m, WeightDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Выход в % из веса в продажу: нетто ÷ брутто × 100, до 2 знаков (для показа).</summary>
    public static decimal YieldFromNet(decimal gross, decimal net) =>
        gross <= 0 ? 0 : Math.Round(net / gross * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сумма строки как её считает сервер: количество × цена, до копеек по итогу (HALF_UP).</summary>
    public static decimal LineTotal(decimal quantity, decimal price) =>
        Math.Round(quantity * price, 2, MidpointRounding.AwayFromZero);

    /// <summary>Цена закупки за единицу нетто для сервера: сумма ÷ нетто до 4 знаков; из трёх соседних цен (−1, 0, +1 в последнем знаке) берётся та,
    /// при которой «нетто × цена» ближе всего к общей сумме (при ничьей — ближайшая к точному частному).</summary>
    public static decimal PriceForTotal(decimal net, decimal total)
    {
        if (net <= 0 || total <= 0)
            return 0;
        var step = 1m / 10000m;
        var exact = total / net;
        var rounded = Math.Round(exact, PriceDecimals, MidpointRounding.AwayFromZero);
        var best = rounded;
        var bestError = Math.Abs(LineTotal(net, rounded) - total);
        foreach (var candidate in new[] { rounded - step, rounded + step })
        {
            if (candidate <= 0)
                continue;
            var error = Math.Abs(LineTotal(net, candidate) - total);
            if (error < bestError)
            {
                best = candidate;
                bestError = error;
            }
        }
        return best;
    }

    private static bool TooPrecise(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero) != value;

    /// <summary>Расчёт выхода. null — посчитать нельзя, причина в <paramref name="problem"/>. Цена и сумма не заданы — результат есть (вес в продажу известен), HasTotal = false.</summary>
    public static CarcassYieldResult? Calculate(CarcassYieldInput input, out CarcassYieldProblem problem)
    {
        problem = CarcassYieldProblem.None;
        var gross = input.Gross;
        if (gross <= 0)
        {
            problem = CarcassYieldProblem.NoGross;
            return null;
        }
        if (gross > MaxWeight)
        {
            problem = CarcassYieldProblem.TooBig;
            return null;
        }
        if (TooPrecise(gross, WeightDecimals))
        {
            problem = CarcassYieldProblem.GrossPrecision;
            return null;
        }

        decimal net;
        decimal yield;
        if (input.Net is { } typedNet)
        {
            if (TooPrecise(typedNet, WeightDecimals))
            {
                problem = CarcassYieldProblem.NetPrecision;
                return null;
            }
            if (typedNet <= 0 || typedNet > gross)
            {
                problem = CarcassYieldProblem.NetRange;
                return null;
            }
            net = typedNet;
            yield = YieldFromNet(gross, net);
        }
        else if (input.YieldPercent is { } typedYield)
        {
            if (typedYield <= 0 || typedYield > 100m)
            {
                problem = CarcassYieldProblem.YieldRange;
                return null;
            }
            net = NetFromYield(gross, typedYield);
            if (net <= 0)
            {
                problem = CarcassYieldProblem.NetRange; // выход так мал, что до грамма не дотягивает
                return null;
            }
            yield = typedYield;
        }
        else
        {
            problem = CarcassYieldProblem.NoYield;
            return null;
        }

        var waste = gross - net;
        var wastePercent = Math.Round(waste / gross * 100m, 2, MidpointRounding.AwayFromZero);

        decimal total = 0, pricePerGross = 0;
        var hasTotal = false;
        // сумма закупки: вписанная (главная) или цена за единицу брутто × брутто; ни того ни другого (или ≤ 0) — суммы ещё нет, вес в продажу уже посчитан
        if (input.Total is { } typedTotal && typedTotal > 0)
        {
            total = Math.Round(typedTotal, 2, MidpointRounding.AwayFromZero);
            pricePerGross = Math.Round(total / gross, PriceDecimals, MidpointRounding.AwayFromZero);
            hasTotal = total > 0;
        }
        else if (input.Total is null && input.PricePerUnitGross is { } typedPrice && typedPrice > 0)
        {
            total = Math.Round(gross * typedPrice, 2, MidpointRounding.AwayFromZero);
            pricePerGross = typedPrice;
            hasTotal = total > 0;
        }
        if (total > MaxMoney)
        {
            problem = CarcassYieldProblem.TooBig;
            return null;
        }

        var cost = hasTotal ? Math.Round(total / net, 2, MidpointRounding.AwayFromZero) : 0m;
        var unitPrice = hasTotal ? PriceForTotal(net, total) : 0m;
        var serverTotal = hasTotal ? LineTotal(net, unitPrice) : 0m;
        decimal? saleHint = hasTotal && input.MarkupPercent is { } markup && markup > -100m
            ? Math.Round(total / net * (1 + markup / 100m), 2, MidpointRounding.AwayFromZero)
            : null;
        return new CarcassYieldResult(gross, net, waste, yield, wastePercent, hasTotal, total, pricePerGross, cost, unitPrice, serverTotal, hasTotal ? serverTotal - total : 0m, saleHint);
    }

    /// <summary>Число из поля ввода: запятая или точка, пробелы разрядов (в том числе неразрывные); без знака, без «1e3», без нескольких разделителей. Пусто — false.</summary>
    public static bool TryParseNumber(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray()).Replace(',', '.');
        if (clean.Length == 0 || clean.Count(c => c == '.') > 1 || clean.Any(c => !(char.IsDigit(c) || c == '.')) || clean == ".")
            return false;
        return decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value < 1e12m;
    }

    /// <summary>Число для показа в поле ввода: без лишних нулей, запятая (как MoneyText.Weight). 225 → «225», 533,3333 → «533,3333».</summary>
    public static string FormatNumber(decimal value, int maxDecimals = 4) =>
        MoneyText.Weight(Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero), "0." + new string('#', maxDecimals));
}

/// <summary>Тексты окна «Выход мяса» и примечания прихода (все на 5 языках).</summary>
public static class CarcassYieldTexts
{
    private static string W(decimal v) => MoneyText.Weight(v);

    private static string UnitOf(string? unit) => string.IsNullOrWhiteSpace(unit) ? "кг" : UnitCatalog.Display(unit);

    /// <summary>«Куплено 300 кг → в продаже 225 кг (отходы 75 кг · 25 %) · себестоимость 533,33 сом/кг».</summary>
    public static string ResultLine(CarcassYieldResult r, string? unit)
    {
        var u = UnitOf(unit);
        var cur = MoneyText.Currency;
        var cost = r.HasTotal ? MoneyText.Amount(r.CostPerUnitNet) : "—";
        return Tr.T(
            $"Куплено {W(r.Gross)} {u} → в продаже {W(r.Net)} {u} (отходы {W(r.Waste)} {u} · {W(r.WastePercent)} %) · себестоимость {cost} {cur}/{u}",
            $"Сатып алынды {W(r.Gross)} {u} → сатууда {W(r.Net)} {u} (калдык {W(r.Waste)} {u} · {W(r.WastePercent)} %) · өздүк баа {cost} {cur}/{u}",
            $"Bought {W(r.Gross)} {u} → for sale {W(r.Net)} {u} (waste {W(r.Waste)} {u} · {W(r.WastePercent)} %) · cost {cost} {cur}/{u}",
            $"Alınan {W(r.Gross)} {u} → satışta {W(r.Net)} {u} (fire {W(r.Waste)} {u} · %{W(r.WastePercent)}) · maliyet {cost} {cur}/{u}",
            $"Sotib olindi {W(r.Gross)} {u} → sotuvda {W(r.Net)} {u} (chiqindi {W(r.Waste)} {u} · {W(r.WastePercent)} %) · tannarx {cost} {cur}/{u}");
    }

    /// <summary>«К оплате поставщику: 120 000,00 сом» — точная сумма закупки (не меняется от выхода).</summary>
    public static string PayLine(CarcassYieldResult r)
    {
        var sum = MoneyText.Som(r.Total);
        return Tr.T($"К оплате поставщику: {sum}", $"Жеткирүүчүгө төлөө: {sum}", $"To pay the supplier: {sum}", $"Tedarikçiye ödenecek: {sum}", $"Yetkazib beruvchiga to'lanadi: {sum}");
    }

    /// <summary>Сервер запишет сумму строки чуть иную (цена до 4 знаков, вес до 3): «в приход запишется 119 999,99 вместо 120 000,00».</summary>
    public static string LossLine(CarcassYieldResult r)
    {
        var served = MoneyText.Som(r.ServerLineTotal);
        var exact = MoneyText.Som(r.Total);
        return Tr.T($"Цена округляется до 4 знаков: в приход запишется {served} вместо {exact}",
            $"Баа 4 цифрага тегизделет: кирешеге {exact} ордуна {served} жазылат",
            $"The price is rounded to 4 decimals: the receipt will record {served} instead of {exact}",
            $"Fiyat 4 basamağa yuvarlanır: girişe {exact} yerine {served} yazılır",
            $"Narx 4 xonagacha yaxlitlanadi: kirimga {exact} o'rniga {served} yoziladi");
    }

    public static string ProblemText(CarcassYieldProblem p) => p switch
    {
        CarcassYieldProblem.NoGross => Tr.T("Укажите, сколько куплено (вес с костью)", "Канча сатып алынганын жазыңыз (сөөгү менен салмагы)", "Enter how much was bought (weight with bones)", "Ne kadar alındığını yazın (kemikli ağırlık)", "Qancha sotib olinganini kiriting (suyagi bilan vazni)"),
        CarcassYieldProblem.GrossPrecision or CarcassYieldProblem.NetPrecision => Tr.T("Вес — не больше 3 знаков после запятой", "Салмак — үтүрдөн кийин 3 цифрадан ашык эмес", "Weight — at most 3 decimal places", "Ağırlık — virgülden sonra en fazla 3 basamak", "Vazn — verguldan keyin ko'pi bilan 3 raqam"),
        CarcassYieldProblem.NoYield => Tr.T("Укажите выход в % или вес в продажу", "Чыгымды % менен же сатуудагы салмакты жазыңыз", "Enter the yield in % or the weight for sale", "Verimi % olarak veya satıştaki ağırlığı yazın", "Chiqishni % bilan yoki sotuvdagi vaznni kiriting"),
        CarcassYieldProblem.YieldRange => Tr.T("Выход — больше 0 и не больше 100 %", "Чыгым — 0дөн чоң жана 100 %дан ашпаган", "Yield — more than 0 and at most 100 %", "Verim — 0'dan büyük ve en fazla %100", "Chiqish — 0 dan katta va 100 % dan oshmasligi kerak"),
        CarcassYieldProblem.NetRange => Tr.T("Вес в продажу — больше 0 и не больше купленного", "Сатуудагы салмак — 0дөн чоң жана сатып алынгандан ашпаган", "Weight for sale — more than 0 and not more than bought", "Satıştaki ağırlık — 0'dan büyük ve alınandan fazla olmamalı", "Sotuvdagi vazn — 0 dan katta va sotib olingandan oshmasligi kerak"),
        CarcassYieldProblem.TooBig => Tr.T("Слишком большое число — проверьте, не штрихкод ли это", "Сан өтө чоң — штрихкод эмеспи, текшериңиз", "The number is too big — check that it is not a barcode", "Sayı çok büyük — barkod olmadığını kontrol edin", "Son juda katta — shtrixkod emasligini tekshiring"),
        _ => "",
    };

    /// <summary>Примечание прихода: «Брутто 300 кг, выход 75 %, отходы 75 кг» (поле comment документа прихода).</summary>
    public static string Note(CarcassYieldResult r, string? unit)
    {
        var u = UnitOf(unit);
        return Tr.T($"Брутто {W(r.Gross)} {u}, выход {W(r.YieldPercent)} %, отходы {W(r.Waste)} {u}",
            $"Брутто {W(r.Gross)} {u}, чыгым {W(r.YieldPercent)} %, калдык {W(r.Waste)} {u}",
            $"Gross {W(r.Gross)} {u}, yield {W(r.YieldPercent)} %, waste {W(r.Waste)} {u}",
            $"Brüt {W(r.Gross)} {u}, verim %{W(r.YieldPercent)}, fire {W(r.Waste)} {u}",
            $"Brutto {W(r.Gross)} {u}, chiqish {W(r.YieldPercent)} %, chiqindi {W(r.Waste)} {u}");
    }
}

/// <summary>2026-11-26: «Выход мяса» включён (модуль Маркетплейса carcass_yield). Сервер этот модуль пока не знает — тогда решает местный признак из Маркетплейса
/// (<see cref="UserPreferences.CarcassYieldEnabled"/>, включается кнопкой «Включить», бесплатно); когда сервер модуль заведёт, решит он (<see cref="CompanyModules.IsOn"/>).
/// Профиль сферы функцию сам не включает — мясной магазин включает её один раз в Маркетплейсе.</summary>
public static class CarcassYieldFeature
{
    public static bool IsAvailable => CompanyModules.IsOn(ModuleIds.CarcassYield, false, UserPreferences.Instance.CarcassYieldEnabled);
}

/// <summary>2026-11-26: выход мяса в % по товару, который уже принимали с выходом, — запоминается ЛОКАЛЬНО (файл receiving-carcass-yield.json рядом с остальными данными кассы;
/// в карточку товара на сервере не пишется). Следующий приход того же товара предлагает прошлый %. Ключ — id товара и/или штрихкод. Ошибки чтения и записи не мешают приёмке.</summary>
public static class CarcassYieldMemory
{
    private static readonly object Gate = new();
    private static Dictionary<string, decimal>? _map;

    /// <summary>Стенд проверки: своя папка вместо %APPDATA% (до первого обращения).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Стенд проверки: ничего не писать на диск.</summary>
    public static bool PersistenceDisabled { get; set; }

    private static string FilePath => Path.Combine(DataDirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), "receiving-carcass-yield.json");

    /// <summary>Стенд: забыть прочитанное (следующее обращение читает файл заново).</summary>
    public static void Reset()
    {
        lock (Gate)
            _map = null;
    }

    private static Dictionary<string, decimal> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, decimal>>(File.ReadAllText(FilePath)) is { } read)
                return new Dictionary<string, decimal>(read.Where(kv => kv.Value > 0 && kv.Value <= 100m), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выход мяса: файл прошлых выходов не прочитан ({ex.GetType().Name}) — начнём заново.", "WARNING");
        }
        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Keys(string? productId, string? barcode)
    {
        if (!string.IsNullOrWhiteSpace(productId))
            yield return "id:" + productId.Trim();
        if (!string.IsNullOrWhiteSpace(barcode))
            yield return "bc:" + barcode.Trim();
    }

    /// <summary>Выход в % при прошлом приходе этого товара (null — не запоминали).</summary>
    public static decimal? Suggest(string? productId, string? barcode)
    {
        lock (Gate)
        {
            _map ??= Load();
            foreach (var key in Keys(productId, barcode))
                if (_map.TryGetValue(key, out var v) && v > 0)
                    return v;
        }
        return null;
    }

    /// <summary>Запомнить выход строки (если он посчитан). Вызывается при постановке прихода в очередь.</summary>
    public static void Remember(string? productId, string? barcode, decimal yieldPercent)
    {
        if (yieldPercent <= 0 || yieldPercent > 100m)
            return;
        try
        {
            Dictionary<string, decimal> snapshot;
            lock (Gate)
            {
                _map ??= Load();
                var changed = false;
                foreach (var key in Keys(productId, barcode))
                    if (!_map.TryGetValue(key, out var old) || old != yieldPercent)
                    {
                        _map[key] = yieldPercent;
                        changed = true;
                    }
                if (!changed || PersistenceDisabled)
                    return;
                snapshot = new Dictionary<string, decimal>(_map);
            }
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выход мяса: выход не записан ({ex.GetType().Name}) — не страшно, введите в следующий раз.", "WARNING");
        }
    }
}
