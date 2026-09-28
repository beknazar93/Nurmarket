namespace NurMarketKassa.Core.Domain;

public enum WeightBarcodeValueKind
{
    /// <summary>Значение в штрих-коде — вес (граммы, переведённые в кг).</summary>
    Weight,
    /// <summary>Значение в штрих-коде — сумма (сом × 100); фактический вес получается
    /// делением суммы на цену товара за кг.</summary>
    Amount,
}

public sealed record WeightBarcodeParseResult(string ProductCode, double Value, WeightBarcodeValueKind Kind)
{
    /// <summary>Шаг дискретности торговых весов (SCALE_WEIGHT_STEP_KG в документации NurCRM) —
    /// 5 грамм. Используется алгоритмом восстановления точного веса из усечённой суммы ниже.</summary>
    private const double ScaleWeightStepKg = 0.005;

    /// <summary>Итоговое количество в кг для строки чека: для Kind=Weight — Value как есть,
    /// для Kind=Amount — вес, восстановленный из напечатанной суммы (см. ResolveWeightFromAmount:
    /// наивное Value/pricePerKg даёт систематическую недостачу на ~1 грамм).</summary>
    public double ResolveWeightKg(double pricePerKg)
    {
        if (Kind == WeightBarcodeValueKind.Weight || pricePerKg <= 0)
            return Value;
        return ResolveWeightFromAmount(Value, pricePerKg);
    }

    /// <summary>2026-09-14: раньше вес весового товара, проданного по суммовому штрих-коду
    /// (mode="amount"), вычислялся наивным делением Value/pricePerKg. Документация NurCRM
    /// (алгоритм _weight_from_amount) описывает известную проблему: сами весы при печати
    /// этикетки ОТБРАСЫВАЮТ дробную часть суммы (91.80 сом печатается как "91"), поэтому
    /// наивное деление 91/540 даёт 0.168518.. → округляется до 0.169 кг вместо настоящих
    /// 0.170 кг — систематическая недостача ровно в момент продажи почти каждого весового
    /// товара по суммовому штрих-коду. Правильный вес восстанавливается точно: печатная сумма A
    /// могла получиться из ЛЮБОГО реального веса в денежном интервале [A/price, (A+1)/price)
    /// (+1, т.к. следующий целый сом уже дал бы A+1 на этикетке) — если в этот интервал попадает
    /// РОВНО ОДНА точка весовой сетки (шаг 5 г), это и есть настоящий вес. Если точек 0 или
    /// больше одной — однозначно восстановить нельзя, используется обычное деление.</summary>
    private static double ResolveWeightFromAmount(double amount, double pricePerKg)
    {
        var minWeight = amount / pricePerKg;

        // 2026-09-28, живая проверка владельца (весы TM-30F): эти весы печатают сумму С ТЫЙЫНАМИ
        // (13,20 = 0,220 кг × 60), а «+1 сом» ниже рассчитан на весы, отбрасывающие дробь. Для
        // 16,00 при цене 123 он давал 0,135 кг = 16,61 вместо 16,00 на этикетке. Если сумма
        // ровно делится на цену по сетке 5 г — это и есть вес; если в сумме есть тыйыны — сумма
        // точная, следующее значение на этикетке +0,01, а не +1.
        var exactIndex = Math.Round(minWeight / ScaleWeightStepKg);
        if (exactIndex > 0 && Math.Abs(exactIndex * ScaleWeightStepKg * pricePerKg - amount) < 0.005)
            return exactIndex * ScaleWeightStepKg;
        var hasTiyin = Math.Abs(amount * 100 - Math.Round(amount) * 100) > 0.5;
        var maxWeight = (amount + (hasTiyin ? 0.01 : 1.0)) / pricePerKg; // следующее значение на этикетке

        var firstIndex = (long)Math.Ceiling(minWeight / ScaleWeightStepKg - 1e-9);
        var lastIndex = (long)Math.Floor(maxWeight / ScaleWeightStepKg - 1e-9);
        if (lastIndex * ScaleWeightStepKg >= maxWeight - 1e-9)
            lastIndex--;

        return firstIndex == lastIndex && firstIndex > 0
            ? firstIndex * ScaleWeightStepKg
            : minWeight;
    }
}
