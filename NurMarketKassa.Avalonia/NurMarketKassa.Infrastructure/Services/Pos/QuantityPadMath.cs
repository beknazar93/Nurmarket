using System.Globalization;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>Результат проверки введённого количества: Value — количество по шагу и минимуму единицы; Error — подпись для кассира (null — всё верно).</summary>
public readonly record struct QuantityPadResult(bool Ok, double Value, string? Error);

/// <summary>
/// 2026-11-12 (ХМ6, ТЗ хозмаг 4.1 п. 2): формулы окна «Количество / длина / площадь» без интерфейса — кнопки шагов по единице,
/// «Д × Ш» с округлением ВВЕРХ до шага, проверка минимума и шага (<see cref="QuantityRules.Normalize"/>). Работает только вместе с
/// <see cref="QuantityRule"/> (то есть в «Хозтоварах» при включённом дробном количестве). Деньги здесь не считаются.
/// </summary>
public static class QuantityPadMath
{
    /// <summary>Кнопки быстрого количества по коду единицы (ТЗ 4.1): м — 0,5…100; м² — 1…20; л — 0,5…10; остальные — 1, 2, 5, 10.</summary>
    public static IReadOnlyList<decimal> StepsFor(string unitCode) => unitCode switch
    {
        "m" => new[] { 0.5m, 1m, 2m, 3m, 5m, 10m, 25m, 50m, 100m },
        "m2" => new[] { 1m, 2m, 5m, 10m, 20m },
        "l" => new[] { 0.5m, 1m, 2m, 5m, 10m },
        _ => new[] { 1m, 2m, 5m, 10m },
    };

    /// <summary>Вверх до шага; перед округлением отсекается шум деления (6 знаков): 7,56 при шаге 0,01 остаётся 7,56, а не 7,57.</summary>
    public static decimal CeilToStep(decimal value, decimal step)
    {
        if (value <= 0m || step <= 0m)
            return 0m;
        var q = Math.Round(value / step, 6, MidpointRounding.AwayFromZero);
        return Math.Ceiling(q) * step;
    }

    /// <summary>«Д × Ш»: площадь в м² вверх до шага единицы. 2,4 × 3,15 = 7,56.</summary>
    public static decimal Area(decimal length, decimal width, decimal step) => CeilToStep(length * width, step);

    /// <summary>Разобрать и проверить введённое количество. Пусто / не число / ≤ 0 / слишком много — подпись; меньше минимума — «Минимум — N ед.»;
    /// иначе значение приводится к шагу (и минимуму) правила.</summary>
    public static QuantityPadResult Check(string? text, QuantityRule rule, string unitText)
    {
        if (!ConsumptionCalculator.TryParseNumber(text, out var value))
            return new(false, 0d, Tr.T("Введите количество числом, например 2,5.", "Санды киргизиңиз, мисалы 2,5.", "Enter a number, for example 2.5.", "Bir sayı girin, örneğin 2,5.", "Son kiriting, masalan 2,5."));
        if (value <= 0m)
            return new(false, 0d, Tr.T("Количество должно быть больше нуля.", "Саны нөлдөн чоң болушу керек.", "The quantity must be above zero.", "Miktar sıfırdan büyük olmalı.", "Miqdor noldan katta bo'lishi kerak."));
        if (value > 1_000_000m)
            return new(false, 0d, Tr.T("Слишком большое количество — проверьте ввод.", "Өтө чоң сан — киргизүүнү текшериңиз.", "The quantity is too large — check the input.", "Miktar çok büyük — girişi kontrol edin.", "Miqdor juda katta — kiritilganini tekshiring."));
        if (value < rule.Min)
            return new(false, 0d, QuantityRules.MinimumMessage(rule, unitText));
        return new(true, QuantityRules.Normalize((double)value, rule), null);
    }

    /// <summary>Сумма строки = количество × цена, до копейки (половина вверх), в decimal.</summary>
    public static decimal LineTotal(double quantity, decimal price) =>
        Math.Round((decimal)quantity * price, 2, MidpointRounding.AwayFromZero);

    public static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
