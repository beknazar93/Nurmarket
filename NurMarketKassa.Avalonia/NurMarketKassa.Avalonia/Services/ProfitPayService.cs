using System.Text.Json;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи». Собирает данные для окна «Зарплата от прибыли»:
/// отчёт «Прибыль» (общая функция <see cref="ProfitReport"/> — те же цифры, что в «Прибыли и деньгах»), зарплата по схемам сервера
/// (analytics/market/?tab=salary — продажи и начислено по каждому сотруднику), часы по табелю (закрытые смены) и схемы «от прибыли»
/// (<see cref="ProfitPayStore"/>). Сам расчёт — чистый <see cref="ProfitPayCalculator"/>.</summary>
public sealed class ProfitPayData
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public ProfitReport.Snapshot? Snapshot { get; init; }
    public ProfitPayDocument Doc { get; set; } = new();
    public List<ServerRow> Server { get; init; } = new();
    public bool SalaryLoaded { get; init; }
    public Dictionary<string, decimal> Hours { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HoursLoaded { get; init; }
    public ProfitPayResult? Result { get; private set; }
    public List<ProfitPayExport.Row> Rows { get; private set; } = new();

    public sealed record ServerRow(string UserId, string Name, string SchemeLine, decimal Total, decimal Sales);

    /// <summary>Пересчёт по текущим схемам (после смены настроек или схемы данные с сервера заново не грузятся).</summary>
    public void Recompute()
    {
        if (Snapshot is null)
        {
            Result = null;
            Rows = new();
            return;
        }
        var f = new ProfitPayFigures
        {
            Revenue = (decimal)Snapshot.Revenue, Cogs = (decimal)Snapshot.Cogs, Gross = (decimal)Snapshot.Gross,
            ServerNet = (decimal)Snapshot.Net, Returns = (decimal)(Snapshot.Returns ?? 0), SupplierCash = (decimal)(Snapshot.SupplierCash ?? 0),
        };
        foreach (var l in Snapshot.Lines)
            f.Expenses[l.Key] = (decimal)l.Amount;
        var emps = new List<ProfitPayEmployee>();
        foreach (var r in Server)
            emps.Add(new ProfitPayEmployee
            {
                UserId = r.UserId, Name = r.Name, Sales = r.Sales,
                Hours = Hours.TryGetValue(ProfitPayCalculator.NameKey(r.Name), out var h) ? h : null,
            });
        foreach (var s in Doc.Schemes.Values.Where(s => emps.All(e => !string.Equals(e.UserId, s.UserId, StringComparison.OrdinalIgnoreCase))))
            emps.Add(new ProfitPayEmployee
            {
                UserId = s.UserId, Name = s.Name, Sales = 0,
                Hours = Hours.TryGetValue(ProfitPayCalculator.NameKey(s.Name), out var h) ? h : null,
            });
        Result = ProfitPayCalculator.Compute(Doc.Shop, Doc.Schemes.Values.ToList(), f, emps);

        Rows = new();
        foreach (var e in emps.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var server = Server.FirstOrDefault(x => string.Equals(x.UserId, e.UserId, StringComparison.OrdinalIgnoreCase));
            var line = Result.Lines.FirstOrDefault(l => string.Equals(l.UserId, e.UserId, StringComparison.OrdinalIgnoreCase));
            Doc.Schemes.TryGetValue(e.UserId, out var scheme);
            if (line is null && server is null)
                continue;
            var schemeText = server?.SchemeLine ?? "";
            if (scheme is { Active: true })
                schemeText = (schemeText.Length > 0 ? schemeText + " + " : "") + Describe(scheme, Doc.Shop);
            Rows.Add(new ProfitPayExport.Row(e.UserId, e.Name, schemeText, server?.Total ?? 0m, line?.Amount ?? 0m, line?.Note ?? "", line?.Steps ?? new List<string>()));
        }
    }

    public static string BaseTitle(ProfitPayBase b) => b switch
    {
        ProfitPayBase.Margin => Tr.T("чистой маржи", "таза маржадан", "net margin", "net marjdan", "sof marjadan"),
        ProfitPayBase.NetProfit => Tr.T("чистой прибыли", "таза пайдадан", "net profit", "net kârdan", "sof foydadan"),
        _ => "",
    };

    /// <summary>«5 % от чистой маржи (по своим продажам)».</summary>
    public static string Describe(ProfitPayScheme s, ProfitPayShopSettings shop)
    {
        var rule = s.Scope == ProfitPayScope.Pool ? shop.Pool : s.Rule;
        var pct = rule.Progressive
            ? $"{rule.Percent:0.##} % / {rule.PercentAbove:0.##} %"
            : $"{rule.Percent:0.##} %";
        var scope = s.Scope switch
        {
            ProfitPayScope.Own => Tr.T("по своим продажам", "өз сатуулары боюнча", "by own sales", "kendi satışlarına göre", "o'z sotuvlari bo'yicha"),
            ProfitPayScope.Shop => Tr.T("по результату магазина", "дүкөндүн жыйынтыгы боюнча", "by shop result", "mağaza sonucuna göre", "do'kon natijasi bo'yicha"),
            _ => Tr.T("общий пул", "жалпы пул", "shared pool", "ortak havuz", "umumiy pul"),
        };
        return pct + " " + Tr.T("от ", "", "of ", "", "") + BaseTitle(rule.Base) + " (" + scope + ")";
    }

    public static async Task<ProfitPayData> LoadAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var api = App.GetRequiredService<NurMarketApiClient>();
        var docTask = ProfitPayStore.LoadAsync(ct);
        var snapTask = ProfitReport.FetchAsync(api, from, to, wantSupplierCash: true, wantReturns: true, ct);
        var salaryTask = App.SalesApi.MarketSalaryReportAsync(from, to, ct);
        var shiftsTask = ShiftHistoryService.LoadAsync(ct, fresh: false);
        var doc = await docTask.ConfigureAwait(true);
        var snap = await snapTask.ConfigureAwait(true);

        var server = new List<ServerRow>();
        var salaryOk = false;
        try
        {
            var data = await salaryTask.ConfigureAwait(true);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                salaryOk = true;
                foreach (var r in rows.EnumerateArray())
                {
                    var id = SalaryWindow.Str(r, "user_id");
                    if (id.Length == 0)
                        continue;
                    var row = SalaryWindow.SalaryRow.From(r);
                    // Продажи сотрудника — как кассира (каждый чек у одного кассира, сумма по всем ≤ выручки магазина).
                    var sales = r.TryGetProperty("cashier_sales_period", out _) ? SalaryWindow.Num(r, "cashier_sales_period") : SalaryWindow.Num(r, "employee_sales_period");
                    server.Add(new ServerRow(id, row.Name, row.SchemeLine, (decimal)SalaryWindow.Num(r, "total"), (decimal)sales));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Зарплата от прибыли: зарплата по схемам сервера не получена: {ex.Message}", "WARNING");
        }

        var shifts = await shiftsTask.ConfigureAwait(true);
        var hours = ProfitPayCalculator.HoursInPeriod(shifts, from, to);
        var result = new ProfitPayData { From = from, To = to, Snapshot = snap, Doc = doc, Server = server, SalaryLoaded = salaryOk, Hours = hours, HoursLoaded = shifts.Count > 0 };
        result.Recompute();
        return result;
    }
}
