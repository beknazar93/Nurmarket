using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи». Единый разбор отчёта сервера «Прибыль»
/// (GET api/main/analytics/pnl/: выручка, закупочная цена проданного, валовая прибыль, расходы по статьям, операционная =
/// «чистая» прибыль) — ОДНА функция и для окна «Прибыль и деньги», и для зарплаты от прибыли: цифры совпадают, формулы здесь
/// не новые. Строки расходов «по названию статьи» (зарплата, аренда, налоги, коммунальные — как в разделе «Зарплата» и при
/// изъятии из кассы) берутся тем же правилом, что раньше жило в ProfitCashReconcileWindow (перенесено без изменений).
///
/// API (публичное, без окон и без состояния; безопасно звать из любого потока):
///   • <see cref="FetchAsync"/>(api, from, to, wantSupplierCash, wantReturns, ct) → <see cref="Snapshot"/>? — те же запросы, что у окна
///     «Прибыль и деньги» (analytics/pnl + расходы по названию статьи); Snapshot: Revenue, Cogs, Gross (маржа), Lines (зарплата, аренда,
///     налоги, коммунальные, прочие), Net (чистая прибыль), SupplierCash, Returns;
///   • <see cref="Parse"/>(pnlJson, named) → Snapshot? — чистый разбор ответа сервера (для стендов и окон, у которых ответ уже есть);
///   • <see cref="ExpenseLines"/>(opex, named, notes) — строки расходов, как в окне; <see cref="Named"/> — перенос «по названию статьи»;
///   • простые формулы валовой/чистой прибыли и маржи в процентах — <see cref="NetProfitCalculator"/> (Gross, Net, MarginPercent, FromPnl).</summary>
public static class ProfitReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public sealed record ExpenseLine(string Key, string Title, double Amount);

    /// <summary>Снимок «Прибыли» за период (суммы в сомах).</summary>
    public sealed class Snapshot
    {
        public double Revenue { get; init; }
        public double Cogs { get; init; }
        /// <summary>Валовая прибыль (маржа) сервера: gross_profit; нет — выручка − закупочная цена.</summary>
        public double Gross { get; init; }
        /// <summary>Строки расходов: salaries, rent, taxes, utilities, other (после переноса «по названию статьи»).</summary>
        public IReadOnlyList<ExpenseLine> Lines { get; init; } = Array.Empty<ExpenseLine>();
        /// <summary>«Чистая прибыль» сервера (operating_profit) — ровно то, что показывает «Прибыль и деньги».</summary>
        public double Net { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        /// <summary>Изъятия из кассы на закупку товара (статья «Закупка товара / оплата поставщику») за период; null — не получены.</summary>
        public double? SupplierCash { get; init; }
        /// <summary>Возвраты покупателям за период (список возвратов сервера); null — не получены.</summary>
        public double? Returns { get; init; }
        /// <summary>Расходы получены «по названию статьи» (cashflows); false — только итоги сервера.</summary>
        public bool NamedLoaded { get; init; }
    }

    public static string ExpenseTitle(string key) => key switch
    {
        "salaries" => Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi"),
        "rent" => Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara"),
        "taxes" => Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar"),
        "utilities" => Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal"),
        _ => Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar"),
    };

    /// <summary>Строка расхода «по названию статьи»: сумма из расходов (cashflow), если она больше серверной (сервер не опознал статью —
    /// у выплат и изъятий без системного slug там 0,00). Разница вычитается из «Прочих расходов» (итог сервера не меняется).
    /// Перенесено из ProfitCashReconcileWindow без изменения правила (2026-10-28).</summary>
    public static double Named(JsonElement block, string key, Dictionary<string, double>? named, ref double moved, List<string> notes, string title)
    {
        var server = Num(block, key) ?? 0;
        if (named is null || !named.TryGetValue(key, out var byName) || byName <= server + 0.005)
            return server;
        moved += byName - server;
        notes.Add($"{title}: " + Tr.T($"по статьям расходов {byName.ToString("N2", Ru)}, сервер даёт {server.ToString("N2", Ru)}",
            $"чыгым беренелери боюнча {byName.ToString("N2", Ru)}, сервер {server.ToString("N2", Ru)} берет",
            $"by expense categories {byName.ToString("N2", Ru)}, the server gives {server.ToString("N2", Ru)}",
            $"gider kalemlerine göre {byName.ToString("N2", Ru)}, sunucu {server.ToString("N2", Ru)} veriyor",
            $"xarajat moddalari bo'yicha {byName.ToString("N2", Ru)}, server {server.ToString("N2", Ru)} beradi"));
        return byName;
    }

    /// <summary>Пять строк расходов отчёта «Прибыль» в порядке окна: зарплата, аренда, налоги, коммунальные, прочие («Прочие» —
    /// последними: из них вычтено перенесённое в именованные строки). Нулевые строки тоже возвращаются — окно решает, что скрывать.</summary>
    public static IReadOnlyList<ExpenseLine> ExpenseLines(JsonElement opex, Dictionary<string, double>? named, List<string> notes)
    {
        double moved = 0;
        var lines = new List<ExpenseLine>();
        foreach (var key in new[] { "salaries", "rent", "taxes", "utilities", "other" })
        {
            var title = ExpenseTitle(key);
            var value = key == "other" ? Math.Max(0, (Num(opex, key) ?? 0) - moved) : Named(opex, key, named, ref moved, notes, title);
            lines.Add(new ExpenseLine(key, title, value));
        }
        return lines;
    }

    /// <summary>Разбор ответа analytics/pnl/. null — ответ не объект.</summary>
    public static Snapshot? Parse(JsonElement? pnl, Dictionary<string, double>? named)
    {
        if (pnl is not { ValueKind: JsonValueKind.Object } d)
            return null;
        var revenue = Num(d, "revenue") ?? 0;
        var cogs = Num(d, "cogs") ?? 0;
        // 2026-10-29: валовая и чистая — единая функция NetProfitCalculator (её же берут «Прибыль и деньги», окупаемость и зарплата).
        var gross = Num(d, "gross_profit") ?? (double)NetProfitCalculator.Gross((decimal)revenue, (decimal)cogs);
        var notes = new List<string>();
        IReadOnlyList<ExpenseLine> lines = Array.Empty<ExpenseLine>();
        var opexTotal = 0d;
        if (d.TryGetProperty("opex", out var opex) && opex.ValueKind == JsonValueKind.Object)
        {
            lines = ExpenseLines(opex, named, notes);
            opexTotal = Num(opex, "total") ?? lines.Sum(l => l.Amount);
        }
        // «Чистая прибыль» — operating_profit сервера; нет — валовая − расходы всего (та же формула, ProfitTerms.NetHow).
        var net = (double)NetProfitCalculator.Net((decimal?)Num(d, "operating_profit"), (decimal)gross, (decimal)opexTotal);
        return new Snapshot
        {
            Revenue = revenue, Cogs = cogs, Gross = gross, Lines = lines, Net = net, Notes = notes, NamedLoaded = named is not null,
        };
    }

    /// <summary>Те же три запроса, что окно «Прибыль и деньги» (pnl + расходы по названию статьи), плюс — по запросу — изъятия на
    /// закупку из ящика и возвраты. null — сервер не ответил по pnl.</summary>
    public static async Task<Snapshot?> FetchAsync(NurMarketApiClient api, DateTime from, DateTime to, bool wantSupplierCash, bool wantReturns, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var pnlTask = api.RequestAsync(HttpMethod.Get, "api/main/analytics/pnl/", null, query, ct, TimeSpan.FromSeconds(60));
        var namedTask = CashArticles.ExpensesByNamedCategoryAsync(api, from, to, ct);
        Task<IReadOnlyList<CashArticles.ArticleTotal>?> articlesTask = wantSupplierCash
            ? CashArticles.WithdrawalsByArticleAsync(api, from, to, ct)
            : Task.FromResult<IReadOnlyList<CashArticles.ArticleTotal>?>(null);
        Task<(int Count, decimal Sum)?> returnsTask = wantReturns
            ? Api.NurCrmReportsApi.ReturnsTotalsAsync(from.Date, to.Date, ct)
            : Task.FromResult<(int Count, decimal Sum)?>(null);
        JsonElement pnl;
        try
        {
            pnl = await pnlTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прибыль (для зарплаты): analytics/pnl не получен: {ex.Message}", "WARNING");
            return null;
        }
        var named = await namedTask.ConfigureAwait(false);
        var articles = await articlesTask.ConfigureAwait(false);
        var returns = await returnsTask.ConfigureAwait(false);
        var snap = Parse(pnl, named);
        if (snap is null)
            return null;
        var supplier = CashArticles.Withdrawals.FirstOrDefault(a => a.Key == "supplier");
        double? supplierCash = null;
        if (articles is not null && supplier is not null)
            supplierCash = articles.Where(a => !a.IsCollection && (a.Title.Equals(supplier.Title, StringComparison.OrdinalIgnoreCase) || CashArticles.Matches(supplier, a.Title))).Sum(a => a.Amount);
        return new Snapshot
        {
            Revenue = snap.Revenue, Cogs = snap.Cogs, Gross = snap.Gross, Lines = snap.Lines, Net = snap.Net, Notes = snap.Notes,
            NamedLoaded = snap.NamedLoaded, SupplierCash = supplierCash, Returns = returns is { } r ? (double)r.Sum : null,
        };
    }

    public static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            }
            : null;
}
