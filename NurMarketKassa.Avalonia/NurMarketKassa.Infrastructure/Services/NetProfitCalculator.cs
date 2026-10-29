using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «для аналитики как расход на начало бизнеса» (стартовые расходы) и параллельная задача «схемы зарплат от
/// чистой прибыли / маржи» — ЕДИНАЯ функция чистой прибыли и маржи, чтобы «Прибыль и деньги», «Сводка», «Окупаемость вложений» и
/// зарплата считали одинаково и не расходились. Здесь только чистые функции (без сети и окон); формулы те же, что давали окна до этого:
///   • валовая прибыль = выручка − закупочная цена проданного (ProfitTerms.GrossHow);
///   • чистая прибыль = валовая − расходы (ProfitTerms.NetHow) — «операционная прибыль» отчёта сервера NurCRM
///     (GET api/main/analytics/pnl/, поле operating_profit); есть ответ сервера — берётся он, иначе валовая − расходы;
///   • маржа чистой прибыли, % = чистая прибыль ÷ выручка × 100 (выручки нет — маржи нет);
///   • чистая прибыль с учётом амортизации = чистая − амортизация стартовых расходов за тот же период (по умолчанию НЕ вычитается:
///     стартовые расходы лежат вне операционной прибыли — см. <see cref="StartupPayback"/>).</summary>
public static class NetProfitCalculator
{
    /// <summary>Показатели отчёта прибыли за период (все суммы — в сомах).</summary>
    public readonly record struct Pnl(decimal Revenue, decimal Cogs, decimal Gross, decimal Expenses, decimal Net,
        IReadOnlyDictionary<string, decimal>? OpexParts = null)
    {
        /// <summary>Маржа чистой прибыли, % от выручки; null — выручки нет.</summary>
        public decimal? NetMarginPercent => MarginPercent(Net, Revenue);

        /// <summary>Маржа валовой прибыли, % от выручки; null — выручки нет.</summary>
        public decimal? GrossMarginPercent => MarginPercent(Gross, Revenue);
    }

    /// <summary>Валовая прибыль = выручка − закупочная цена проданного.</summary>
    public static decimal Gross(decimal revenue, decimal cogs) => revenue - cogs;

    /// <summary>Чистая прибыль = валовая прибыль − расходы.</summary>
    public static decimal Net(decimal gross, decimal expenses) => gross - expenses;

    /// <summary>Чистая прибыль: цифра сервера (operating_profit), если она есть, иначе валовая − расходы.</summary>
    public static decimal Net(decimal? serverOperatingProfit, decimal gross, decimal expenses) =>
        serverOperatingProfit ?? Net(gross, expenses);

    /// <summary>Маржа, % от выручки; null — выручки нет (делить не на что).</summary>
    public static decimal? MarginPercent(decimal profit, decimal revenue) => revenue > 0m ? profit / revenue * 100m : null;

    /// <summary>Чистая прибыль с учётом амортизации стартовых расходов: чистая − амортизация за тот же период.</summary>
    public static decimal AfterAmortization(decimal net, decimal amortization) => net - amortization;

    /// <summary>Разбор ответа GET api/main/analytics/pnl/ (revenue, cogs, gross_profit, opex.total, operating_profit).
    /// Нет ни operating_profit, ни gross_profit/расходов — null (отчёт не получен или формат незнаком).</summary>
    public static Pnl? FromPnl(JsonElement pnl)
    {
        if (pnl.ValueKind != JsonValueKind.Object)
            return null;
        var revenue = Num(pnl, "revenue");
        var cogs = Num(pnl, "cogs");
        var gross = Num(pnl, "gross_profit") ?? (revenue is { } r && cogs is { } c ? Gross(r, c) : null);
        decimal? expenses = null;
        if (pnl.TryGetProperty("opex", out var opex) && opex.ValueKind == JsonValueKind.Object)
            expenses = Num(opex, "total");
        var server = Num(pnl, "operating_profit");
        if (server is null && (gross is null || expenses is null))
            return null;
        var g = gross ?? 0m;
        var e = expenses ?? (server is { } s ? g - s : 0m);
        // 2026-10-29: статьи расходов отчёта (зарплата, аренда, налоги, коммунальные, прочие) — для долей инвесторов
        // («какие расходы вычитать», раздел «Инвестиции и окупаемость»).
        Dictionary<string, decimal>? parts = null;
        if (pnl.TryGetProperty("opex", out var ox) && ox.ValueKind == JsonValueKind.Object)
        {
            parts = new Dictionary<string, decimal>();
            foreach (var key in OpexKeys)
                if (Num(ox, key) is { } v)
                    parts[key] = v;
        }
        return new Pnl(revenue ?? 0m, cogs ?? 0m, g, e, Net(server, g, e), parts);
    }

    /// <summary>Статьи расходов отчёта сервера (opex.*).</summary>
    public static readonly string[] OpexKeys = { "salaries", "rent", "taxes", "utilities", "other" };

    /// <summary>База доли за месяц. kind: «revenue» — выручка, «gross» — валовая прибыль, «net» — чистая прибыль. Для «net» можно
    /// исключить статьи расходов (excludedOpex: ключи из <see cref="OpexKeys"/>) — они не вычитаются; ничего не исключено — это
    /// ровно чистая прибыль отчёта (<see cref="Pnl.Net"/>). Остаток расходов вне статей вычитается всегда.</summary>
    public static decimal BaseFor(Pnl pnl, string kind, IReadOnlyCollection<string>? excludedOpex = null)
    {
        switch (kind)
        {
            case "revenue": return pnl.Revenue;
            case "gross": return pnl.Gross;
        }
        if (excludedOpex is not { Count: > 0 } || pnl.OpexParts is null)
            return pnl.Net;
        return pnl.Net + pnl.OpexParts.Where(p => excludedOpex.Contains(p.Key)).Sum(p => p.Value);
    }

    private static decimal? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
