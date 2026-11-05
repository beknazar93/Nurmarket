using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Как разнести доп. расходы прихода (доставка, грузчики) на строки.</summary>
public enum LandedMethod
{
    /// <summary>Пропорционально сумме строки (количество × закупка).</summary>
    ByAmount,

    /// <summary>Поровну на каждую единицу товара (пропорционально количеству).</summary>
    ByQuantity,
}

/// <summary>Строка предпросмотра: закупка было → стало.</summary>
public sealed record LandedPreviewRow(ReceivingLineVm Line, double OldCost, double NewCost, double ExtraPerUnit, double ExtraTotal);

/// <summary>2026-11-05 (функция 11 «Себестоимость с доставкой», идея ERPNext Landed Cost Voucher / Odoo stock_landed_costs — код не заимствован):
/// чистые расчёты разнесения доп. расходов прихода на закупочную цену строк (до <see cref="ReceivingPricing.PurchaseDecimals"/> знаков).
/// ДЕНЬГИ: расходы НЕ входят в документ прихода и в долг поставщику — документ уходит с накладными ценами, а «себестоимость» (накладная + доля расходов)
/// пишется в карточку товара отдельным PATCH после прихода (PurchaseReceivingService.PostAsync, ReceivingLineVm.LandedExtraPerUnit).</summary>
public static class LandedCost
{
    /// <summary>Доля расходов на ЕДИНИЦУ товара по каждой строке (в том же порядке). Строки с количеством ≤ 0 получают 0.
    /// Расходы ≤ 0 или не число — нули. Округление до 4 знаков (HALF_UP), как закупка на сервере.</summary>
    public static double[] PerUnit(IReadOnlyList<(double Quantity, double Price)> lines, double extraTotal, LandedMethod method)
    {
        var result = new double[lines.Count];
        if (double.IsNaN(extraTotal) || double.IsInfinity(extraTotal) || extraTotal <= 0 || lines.Count == 0)
            return result;
        double Weight(int i, bool byAmount)
        {
            var (q, p) = lines[i];
            if (q <= 0 || double.IsNaN(q))
                return 0;
            return byAmount ? q * Math.Max(p, 0) : q;
        }
        var byAmount = method == LandedMethod.ByAmount;
        var total = 0.0;
        for (var i = 0; i < lines.Count; i++)
            total += Weight(i, byAmount);
        if (total <= 0 && byAmount) // все цены нулевые — по сумме делить не на что, делим по количеству
        {
            byAmount = false;
            for (var i = 0; i < lines.Count; i++)
                total += Weight(i, false);
        }
        if (total <= 0)
            return result;
        for (var i = 0; i < lines.Count; i++)
        {
            var w = Weight(i, byAmount);
            if (w <= 0)
                continue;
            var share = extraTotal * w / total;               // доля расходов на всю строку
            result[i] = Math.Round(share / lines[i].Quantity, ReceivingPricing.PurchaseDecimals, MidpointRounding.AwayFromZero);
        }
        return result;
    }

    /// <summary>Предпросмотр для строк приёмки: закупка → закупка с расходами. Строки не меняются (меняет <see cref="Apply"/>).</summary>
    public static List<LandedPreviewRow> Preview(IReadOnlyList<ReceivingLineVm> lines, double extraTotal, LandedMethod method)
    {
        var per = PerUnit(lines.Select(l => (l.Quantity, l.PurchasePrice)).ToList(), extraTotal, method);
        var rows = new List<LandedPreviewRow>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var extra = per[i];
            rows.Add(new LandedPreviewRow(l, l.PurchasePrice, Math.Round(l.PurchasePrice + extra, ReceivingPricing.PurchaseDecimals, MidpointRounding.AwayFromZero),
                extra, Math.Round(extra * l.Quantity, 2, MidpointRounding.AwayFromZero)));
        }
        return rows;
    }

    /// <summary>Записать доли расходов в строки (LandedExtraPerUnit). Возвращает, сколько строк получили долю.</summary>
    public static int Apply(IEnumerable<LandedPreviewRow> rows)
    {
        var n = 0;
        foreach (var r in rows)
        {
            r.Line.LandedExtraPerUnit = r.ExtraPerUnit;
            if (r.ExtraPerUnit > 0)
                n++;
        }
        return n;
    }

    /// <summary>Сбросить доли расходов у строк.</summary>
    public static void Clear(IEnumerable<ReceivingLineVm> lines)
    {
        foreach (var l in lines)
            l.LandedExtraPerUnit = 0;
    }

    /// <summary>Сколько расходов реально разнесено на строки (из-за округления до 4 знаков может чуть отличаться от введённого).</summary>
    public static double DistributedTotal(IEnumerable<ReceivingLineVm> lines) =>
        (double)Math.Round(lines.Sum(l => (decimal)l.LandedExtraPerUnit * (decimal)Math.Max(l.Quantity, 0)), 2, MidpointRounding.AwayFromZero);
}
