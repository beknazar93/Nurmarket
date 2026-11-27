using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Итог расчёта «По сумме»: вес до грамма и сумма, которая будет в чеке; <see cref="ExactLine"/> — сервер получит точную сумму строки (line_total), иначе вес × цена как раньше.</summary>
public sealed record WeighedAmountPlan(decimal Kg, decimal LineTotal, bool ExactLine);

/// <summary>
/// 2026-11-26 (владелец, по фото клиента 24.11: «ввожу 1000, а в чеке 999,90»; ответ Умара 26.11): окно «Взвесить», вкладка «По сумме». Вес считается до грамма (сумма ÷ цена, 3 знака),
/// и вес × цена за кг после округления веса не равна введённой сумме (1000 ÷ 1100 = 0,909 кг → 999,90). Сервер умеет принять в строке весового товара pos/checkout поле
/// <c>line_total</c> — точную сумму строки (признак capabilities <c>weight_line_total</c>): {"product":…, "qty":"0.909", "line_total":"1000"} → чек ровно 1000,00, цену за кг сервер ставит сам
/// (проверено живыми продажами 26.11 на тестовой компании). Правила сервера: только весовой товар без упаковки/единицы продажи; скидку в такой строке НЕ передавать (400 line_total_with_discount);
/// сумма должна сходиться с весом × цена в пределах 1 г (иначе 400 line_total_mismatch); при шаге весов «в сомах» сумма целая (400 line_total_step); две строки одного товара — только одной
/// строкой (400 duplicate_line), поэтому в чеке кассы такой товар всегда одна строка, а суммы складываются.
/// Строка чека хранит введённую сумму в поле <see cref="Field"/>. Нет признака сервера — поле не ставится и не читается: вес × цена, как раньше, а подсказка честно показывает «в чеке: 999,90».
/// </summary>
public static class WeighedAmount
{
    /// <summary>Поле строки чека кассы: точная сумма строки, сом (число). Нет поля — сумма вес × цена.</summary>
    public const string Field = "weigh_amount";

    /// <summary>Сервер принимает line_total (признак weight_line_total). Подмена нужна тестам.</summary>
    public static Func<bool> Enabled { get; set; } = () => ServerCapabilities.Has(ServerCapabilities.WeightLineTotal);

    /// <summary>Допуск сервера: сумма строки отличается от вес × цена не больше чем на 1 г (в сомах — цена за кг / 1000).</summary>
    public static bool WithinTolerance(decimal total, decimal kg, decimal pricePerKg) =>
        Math.Abs(total - kg * pricePerKg) <= pricePerKg * 0.001m + 0.005m;

    /// <summary>Вес до грамма для суммы и цены за кг; 0 — посчитать нельзя.</summary>
    public static decimal KgFor(decimal amount, decimal pricePerKg) =>
        amount > 0m && pricePerKg > 0m ? Math.Round(amount / pricePerKg, 3, MidpointRounding.AwayFromZero) : 0m;

    /// <summary>Расчёт для окна «Взвесить». <paramref name="exactAllowed"/> — сервер принимает line_total (по умолчанию признак сервера); <paramref name="step"/> — шаг весов компании (null — как у компании).
    /// Сумма округляется до копеек; вес меньше грамма — null. Без точной строки сумма — как посчитает сервер из веса и цены (вниз до шага).</summary>
    public static WeighedAmountPlan? Plan(decimal amount, decimal pricePerKg, bool? exactAllowed = null, decimal? step = null)
    {
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var kg = KgFor(amount, pricePerKg);
        if (kg <= 0m)
            return null;
        var floorStep = step is > 0m ? step.Value : QuantityRules.WeightStep();
        var exact = (exactAllowed ?? Enabled()) && WithinTolerance(amount, kg, pricePerKg)
                    && (floorStep <= 0.01m || amount % floorStep == 0m);
        if (exact)
            return new WeighedAmountPlan(kg, amount, true);
        // Без line_total: как раньше — вес × цена (сервер округляет весовую строку вниз до шага весов; без признака floor — до копейки).
        var rule = QuantityRules.WeightLineFloorEnabled() ? floorStep : 0.01m;
        return new WeighedAmountPlan(kg, QuantityRules.FloorToStep(kg * pricePerKg, rule), false);
    }

    /// <summary>Точная сумма строки чека (JSON строки кассы) или null — поля нет, признака сервера нет или сумма не годится.</summary>
    public static decimal? Of(JsonElement it)
    {
        if (!it.TryGetProperty(Field, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDecimal(out var value) || value <= 0m)
            return null;
        return Enabled() ? value : null;
    }

    /// <summary>То же для строки чека в виде JsonObject.</summary>
    public static decimal? Of(JsonObject line)
    {
        if (line[Field] is not JsonValue v || !v.TryGetValue<double>(out var d) || !double.IsFinite(d) || d <= 0)
            return null;
        return Enabled() ? (decimal)d : null;
    }

    /// <summary>Сумма строки без округления: точная сумма, если она есть, иначе количество × цена.</summary>
    public static decimal Gross(JsonElement it, double qty, double price) => Of(it) ?? QuantityRules.GrossExact(qty, price);

    /// <summary>То же для JsonObject.</summary>
    public static decimal Gross(JsonObject line, double qty, double price) => Of(line) ?? QuantityRules.GrossExact(qty, price);
}
