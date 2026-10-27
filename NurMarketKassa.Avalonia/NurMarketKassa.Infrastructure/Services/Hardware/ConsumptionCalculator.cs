using System.Globalization;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Что считаем (ТЗ хозмаг, ХМ5): плитка, ламинат, краска, обои, сухая смесь (клей, затирка, шпаклёвка…).</summary>
public enum CalcKind
{
    Tile,
    Laminate,
    Paint,
    Wallpaper,
    Mix,
}

/// <summary>В чём считается «нужно»: м² (плитка, ламинат), литры (краска), килограммы (смеси), рулоны (обои).</summary>
public enum CalcUnit
{
    SquareMeter,
    Liter,
    Kilogram,
    Roll,
}

/// <summary>Почему расчёт не выполнен. Текст для кассира делает окно, здесь только код.</summary>
public enum CalcError
{
    None,
    /// <summary>Площадь (или периметр) не задана или не больше нуля.</summary>
    AreaMissing,
    /// <summary>Проёмы не меньше площади — считать нечего.</summary>
    OpeningsTooBig,
    /// <summary>Размер упаковки (м², л, кг) не задан.</summary>
    PackMissing,
    /// <summary>Норма расхода (л/м² или кг/м²) или число слоёв не заданы.</summary>
    NormMissing,
    /// <summary>Запас вне 0…100 %.</summary>
    ReserveInvalid,
    /// <summary>Обои: высота, ширина или длина рулона не заданы, либо полоса длиннее рулона.</summary>
    RollInvalid,
    /// <summary>Слишком большое значение (защита от опечатки «1000000»).</summary>
    TooBig,
}

/// <summary>Результат расчёта. Все количества — decimal; в упаковки округляется ВВЕРХ до целой.</summary>
public sealed record CalcResult
{
    public CalcError Error { get; init; }
    public bool IsValid => Error == CalcError.None;
    public CalcKind Kind { get; init; }
    public CalcUnit Unit { get; init; }
    /// <summary>Сколько нужно без запаса (м², л, кг). У обоев — площадь стен, м² (справочно).</summary>
    public decimal Need { get; init; }
    /// <summary>Сколько нужно с запасом (м², л, кг). У обоев равно <see cref="Need"/> — запас в полосах.</summary>
    public decimal WithReserve { get; init; }
    public decimal ReservePercent { get; init; }
    /// <summary>Упаковок / банок / мешков / рулонов — целое, вверх.</summary>
    public int Packs { get; init; }
    /// <summary>Размер одной упаковки (м², л, кг). У обоев — 1 (считаем рулонами).</summary>
    public decimal PackSize { get; init; }
    /// <summary>Сколько всего в купленных упаковках (м², л, кг). У обоев — число рулонов.</summary>
    public decimal PackedTotal { get; init; }
    /// <summary>Обои: нужно полос по периметру и сколько полос выходит из одного рулона.</summary>
    public int Strips { get; init; }
    public int StripsPerRoll { get; init; }

    internal static CalcResult Fail(CalcKind kind, CalcError error) => new() { Kind = kind, Error = error };
}

/// <summary>
/// 2026-10-26 (ТЗ «Хозмаг», ХМ5): калькулятор расхода «сколько нужно» — ЧИСТЫЕ формулы без интерфейса, базы и денег.
/// Правила:
///  • все расчёты в decimal; запас по умолчанию 10 % (15 % — диагональная укладка плитки);
///  • в упаковки округляем ВВЕРХ до целой упаковки (недобор — хуже перебора: плитку другой партии не докупить в тот же тон);
///  • перед округлением вверх отсекаем шум деления (6 знаков), чтобы 14,40 / 1,44 не стало 10,0000001 → 11;
///  • проёмы (двери, окна) вычитаются из площади, но не больше самой площади.
/// Контрольные примеры ТЗ 7.2 (плитка 3×4 → 10 уп, ламинат → 10 уп, краска → 4 банки, обои → 9 рулонов, клей → 3 мешка)
/// проверяет стенд C:\Users\User\NurMarket-test\stands\hardware-calc (без dotnet test).
/// Деньги здесь не считаются: цена и округление денег остаются за корзиной (ХМ2).
/// </summary>
public static class ConsumptionCalculator
{
    public const decimal DefaultReservePercent = 10m;
    /// <summary>Диагональная укладка плитки — запас больше (ТЗ 4.1).</summary>
    public const decimal DiagonalReservePercent = 15m;

    private const decimal MaxValue = 1_000_000m;

    /// <summary>Площадь прямоугольника, м².</summary>
    public static decimal Area(decimal length, decimal width) => length * width;

    /// <summary>Периметр комнаты, м.</summary>
    public static decimal Perimeter(decimal length, decimal width) => 2m * (length + width);

    /// <summary>Сколько целых упаковок нужно на <paramref name="amount"/> при размере упаковки <paramref name="packSize"/> — вверх.</summary>
    public static int CeilPacks(decimal amount, decimal packSize)
    {
        if (amount <= 0m || packSize <= 0m)
            return 0;
        var q = Math.Round(amount / packSize, 6, MidpointRounding.AwayFromZero);
        var packs = Math.Ceiling(q);
        return packs > int.MaxValue ? int.MaxValue : (int)packs;
    }

    /// <summary>Плитка и ламинат: площадь − проёмы → + запас → упаковки по <paramref name="packArea"/> м² (вверх).</summary>
    public static CalcResult Tiles(CalcKind kind, decimal area, decimal openings, decimal reservePercent, decimal packArea)
    {
        var check = CheckArea(kind, area, openings, reservePercent);
        if (check != CalcError.None)
            return CalcResult.Fail(kind, check);
        if (packArea <= 0m)
            return CalcResult.Fail(kind, CalcError.PackMissing);
        if (packArea > MaxValue)
            return CalcResult.Fail(kind, CalcError.TooBig);
        return Linear(kind, CalcUnit.SquareMeter, area - openings, reservePercent, packArea);
    }

    /// <summary>Краска: (площадь − проёмы) × слоёв × расход л/м² → + запас → банки по <paramref name="canLiters"/> л (вверх).</summary>
    public static CalcResult Paint(decimal area, decimal openings, decimal layers, decimal litersPerM2, decimal reservePercent, decimal canLiters)
    {
        var check = CheckArea(CalcKind.Paint, area, openings, reservePercent);
        if (check != CalcError.None)
            return CalcResult.Fail(CalcKind.Paint, check);
        if (canLiters <= 0m)
            return CalcResult.Fail(CalcKind.Paint, CalcError.PackMissing);
        if (layers <= 0m || litersPerM2 <= 0m)
            return CalcResult.Fail(CalcKind.Paint, CalcError.NormMissing);
        if (canLiters > MaxValue || layers > 100m || litersPerM2 > MaxValue)
            return CalcResult.Fail(CalcKind.Paint, CalcError.TooBig);
        return Linear(CalcKind.Paint, CalcUnit.Liter, (area - openings) * layers * litersPerM2, reservePercent, canLiters);
    }

    /// <summary>Клей, затирка, шпаклёвка, штукатурка (сопутствующие): (площадь − проёмы) × слоёв × расход кг/м² → + запас →
    /// мешки по <paramref name="bagKg"/> кг (вверх).</summary>
    public static CalcResult Mix(decimal area, decimal openings, decimal layers, decimal kgPerM2, decimal reservePercent, decimal bagKg)
    {
        var check = CheckArea(CalcKind.Mix, area, openings, reservePercent);
        if (check != CalcError.None)
            return CalcResult.Fail(CalcKind.Mix, check);
        if (bagKg <= 0m)
            return CalcResult.Fail(CalcKind.Mix, CalcError.PackMissing);
        if (layers <= 0m || kgPerM2 <= 0m)
            return CalcResult.Fail(CalcKind.Mix, CalcError.NormMissing);
        if (bagKg > MaxValue || layers > 100m || kgPerM2 > MaxValue)
            return CalcResult.Fail(CalcKind.Mix, CalcError.TooBig);
        return Linear(CalcKind.Mix, CalcUnit.Kilogram, (area - openings) * layers * kgPerM2, reservePercent, bagKg);
    }

    /// <summary>
    /// Обои. Формула (ТЗ 4.1, контрольный пример 7.2):
    ///  длина полосы = высота стены (с раппортом — высота, округлённая ВВЕРХ до кратной раппорту);
    ///  полос из одного рулона = floor(длина рулона / длина полосы);
    ///  полос по комнате = ceil(периметр × (1 + запас) / ширина рулона);
    ///  рулонов = ceil(полос по комнате / полос из рулона).
    /// Пример: периметр 14 м, высота 2,7 м, рулон 10,05 × 0,53 м, раппорт 0 → 3 полосы из рулона, 27 полос, 9 рулонов.
    /// Запас у обоев по умолчанию 0: целые рулоны уже дают запас. Проёмы не вычитаем — над дверью и под окном полосы
    /// всё равно нужны, а недобор обоев другой партии виден на стене.
    /// </summary>
    public static CalcResult Wallpaper(decimal perimeter, decimal height, decimal rollLength, decimal rollWidth, decimal rapport, decimal reservePercent)
    {
        if (perimeter <= 0m)
            return CalcResult.Fail(CalcKind.Wallpaper, CalcError.AreaMissing);
        if (reservePercent < 0m || reservePercent > 100m)
            return CalcResult.Fail(CalcKind.Wallpaper, CalcError.ReserveInvalid);
        if (height <= 0m || rollLength <= 0m || rollWidth <= 0m || rapport < 0m)
            return CalcResult.Fail(CalcKind.Wallpaper, CalcError.RollInvalid);
        if (perimeter > MaxValue || height > MaxValue || rollLength > MaxValue || rollWidth > MaxValue || rapport > MaxValue)
            return CalcResult.Fail(CalcKind.Wallpaper, CalcError.TooBig);

        var stripLength = rapport > 0m ? Math.Ceiling(Math.Round(height / rapport, 6, MidpointRounding.AwayFromZero)) * rapport : height;
        var perRoll = (int)Math.Floor(Math.Round(rollLength / stripLength, 6, MidpointRounding.AwayFromZero));
        if (perRoll < 1)
            return CalcResult.Fail(CalcKind.Wallpaper, CalcError.RollInvalid);

        var strips = CeilPacks(perimeter * (1m + reservePercent / 100m), rollWidth);
        var rolls = (int)Math.Ceiling((decimal)strips / perRoll);
        var wallArea = perimeter * height;
        return new CalcResult
        {
            Kind = CalcKind.Wallpaper,
            Unit = CalcUnit.Roll,
            Need = wallArea,
            WithReserve = wallArea,
            ReservePercent = reservePercent,
            Packs = rolls,
            PackSize = 1m,
            PackedTotal = rolls,
            Strips = strips,
            StripsPerRoll = perRoll,
        };
    }

    private static CalcError CheckArea(CalcKind kind, decimal area, decimal openings, decimal reservePercent)
    {
        if (area <= 0m)
            return CalcError.AreaMissing;
        if (area > MaxValue || openings > MaxValue)
            return CalcError.TooBig;
        if (openings < 0m || openings >= area)
            return CalcError.OpeningsTooBig;
        if (reservePercent < 0m || reservePercent > 100m)
            return CalcError.ReserveInvalid;
        return CalcError.None;
    }

    private static CalcResult Linear(CalcKind kind, CalcUnit unit, decimal need, decimal reservePercent, decimal packSize)
    {
        var withReserve = need * (1m + reservePercent / 100m);
        var packs = CeilPacks(withReserve, packSize);
        return new CalcResult
        {
            Kind = kind,
            Unit = unit,
            Need = need,
            WithReserve = withReserve,
            ReservePercent = reservePercent,
            Packs = packs,
            PackSize = packSize,
            PackedTotal = packs * packSize,
        };
    }

    /// <summary>
    /// Размер упаковки из названия товара: «Плитка 30×60, 1,44 м²» → 1,44; «Краска 2,5 л» → 2,5; «Клей 25 кг» → 25.
    /// Ищет число перед единицей расчёта (м² / л / кг). Нет — null (кассир впишет сам). Обои размера упаковки не требуют.
    /// </summary>
    public static decimal? GuessPackSize(CalcKind kind, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var unit = kind switch
        {
            CalcKind.Tile or CalcKind.Laminate => @"(?:м²|м2|кв\.?\s?м|m²|m2|sqm)",
            CalcKind.Paint => @"(?:литр\p{L}*|л|l|lt|ltr|litre|liter)",
            CalcKind.Mix => @"(?:кг|килограмм\p{L}*|kg)",
            _ => null,
        };
        if (unit is null)
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(title.ToLowerInvariant(),
            @"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*" + unit + @"(?![\p{L}\d])");
        return m.Success && TryParseNumber(m.Groups[1].Value, out var v) && v > 0m ? v : null;
    }

    /// <summary>Число из поля ввода: запятая или точка, пробелы и неразрывные пробелы внутри игнорируются. Пусто, минус, буквы — false.</summary>
    public static bool TryParseNumber(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var clean = text.Replace("\u00A0", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0m;
    }
}

/// <summary>Почему «В чек» недоступно.</summary>
public enum CartPlanProblem
{
    None,
    /// <summary>Нет рассчитанного результата.</summary>
    NoResult,
    /// <summary>Единица товара не подходит к расчёту (плитка в «м», краска в «кг» и т. п.).</summary>
    UnitMismatch,
}

/// <summary>Что положить в корзину по результату расчёта.</summary>
public sealed record CartPlan
{
    public CartPlanProblem Problem { get; init; }
    public bool CanAdd => Problem == CartPlanProblem.None;
    /// <summary>Количество для строки чека: упаковки (целое) или единицы товара (м², л, кг — может быть дробным).</summary>
    public decimal Quantity { get; init; }
    /// <summary>true — количество дробное в единицах товара (м², л, кг): нужна поддержка дробного количества в корзине (ХМ2).</summary>
    public bool IsFractional { get; init; }
    /// <summary>Цена одной купленной упаковки по цене товара (только для показа; сумму чека считает корзина).</summary>
    public decimal PackPrice { get; init; }
    /// <summary>Сумма «упаковок × цена упаковки» (только для показа в окне).</summary>
    public decimal DisplayTotal { get; init; }
}

/// <summary>
/// 2026-10-26 (ХМ5): как результат расчёта превращается в строку чека. Чистая логика, корзину не трогает.
///  • товар продаётся УПАКОВКАМИ (единица уп / коробка / мешок / рулон / шт / лист / комплект / пара, либо неизвестная,
///    например «банка») — в чек идёт число упаковок, цена упаковки = цена товара;
///  • товар продаётся в единицах расчёта (плитка в м², краска в л, смесь в кг) — в чек идёт «упаковок × размер упаковки»
///    (например 14,40 м²), цена упаковки = цена за единицу × размер упаковки;
///  • любая другая известная единица (плитка в «м», краска в «кг») — не подходит, «В чек» выключено.
/// </summary>
public static class CalcCartPlanner
{
    public static CalcUnit? ExpectedBaseUnit(CalcKind kind) => kind switch
    {
        CalcKind.Tile or CalcKind.Laminate => CalcUnit.SquareMeter,
        CalcKind.Paint => CalcUnit.Liter,
        CalcKind.Mix => CalcUnit.Kilogram,
        _ => null,
    };

    private static string? CodeOf(CalcUnit unit) => unit switch
    {
        CalcUnit.SquareMeter => "m2",
        CalcUnit.Liter => "l",
        CalcUnit.Kilogram => "kg",
        _ => null,
    };

    private static bool IsPackLike(string? code) =>
        code is null or "pack" or "box" or "bag" or "roll" or "pcs" or "sheet" or "set" or "pair";

    /// <summary>Единица товара — «упаковочная» (см. описание класса).</summary>
    public static bool SoldByPacks(string? productUnitText) => IsPackLike(HardwareProfile.UnitCode(productUnitText));

    public static CartPlan Plan(CalcResult result, string? productUnitText, decimal productPrice)
    {
        if (!result.IsValid || result.Packs <= 0)
            return new CartPlan { Problem = CartPlanProblem.NoResult };
        var code = HardwareProfile.UnitCode(productUnitText);
        if (IsPackLike(code))
        {
            var total = Math.Round(result.Packs * productPrice, 2, MidpointRounding.AwayFromZero);
            return new CartPlan { Quantity = result.Packs, PackPrice = productPrice, DisplayTotal = total };
        }
        if (ExpectedBaseUnit(result.Kind) is { } expected && CodeOf(expected) == code)
        {
            var packPrice = Math.Round(productPrice * result.PackSize, 2, MidpointRounding.AwayFromZero);
            var total = Math.Round(result.PackedTotal * productPrice, 2, MidpointRounding.AwayFromZero);
            return new CartPlan { Quantity = result.PackedTotal, IsFractional = true, PackPrice = packPrice, DisplayTotal = total };
        }
        return new CartPlan { Problem = CartPlanProblem.UnitMismatch };
    }
}
