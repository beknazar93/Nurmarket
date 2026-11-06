// 2026-10-30: кадры уроков владельца V-01…V-03 (сводка, отчёты, зарплата от прибыли). Настоящие окна программы владельца на выдуманных данных магазина «Нур».
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NurMarketKassa;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.AvaloniaHost.Services;

public static partial class FeatLessons
{
    /// <summary>Месяц продаж магазина: ~140 чеков за 30 дней, три кассира, наличные/карта/перевод/долг.</summary>
    static string PId(string n) => FeatData.Products.First(x => x.Title == n).Id;
    static double Buy(string n) => FeatData.Products.First(x => x.Title == n).PurchasePrice;
    static List<JsonElement>? _month;
    public static List<JsonElement> MonthSales()
    {
        if (_month != null) return _month;
        var rnd = new Random(11);
        var prods = new (string N, int P)[] { ("Хлеб белый", 40), ("Адыгене 1,5 л", 55), ("Адыгене 0,5 л", 30), ("Winston Blue", 205), ("ASU", 25), ("Сүт", 65), ("Нан", 30), ("Яйца 10 шт", 120), ("Кефир 1% 0,9 л", 78), ("Печенье овсяное", 95), ("Семечки 100 г", 25), ("Вода 0,5 л", 25) };
        var users = new[] { "Айнура", "Эрлан", "Айбек" }; var pm = new[] { "cash", "cash", "card", "transfer", "cash", "debt" };
        var list = new List<JsonElement>();
        for (var i = 0; i < 140; i++)
        {
            var day = rnd.Next(0, 30); var t = DateTime.Today.AddDays(-day).AddHours(8 + rnd.Next(0, 12)).AddMinutes(rnd.Next(0, 60));
            var n = rnd.Next(1, 4); var items = new List<string>(); double tot = 0;
            for (var k = 0; k < n; k++) { var p = prods[Math.Min(prods.Length - 1, (int)(Math.Pow(rnd.NextDouble(), 1.8) * prods.Length))]; var q = rnd.Next(1, 4); tot += p.P * q; items.Add($"{{\"cart_item_id\":\"{i}-{k}\",\"product_id\":\"{PId(p.N)}\",\"purchase_price\":{Buy(p.N)},\"product_name\":\"{p.N}\",\"name\":\"{p.N}\",\"quantity\":{q},\"price\":{p.P},\"unit_price\":{p.P},\"total\":{p.P * q}}}"); }
            var json = $"{{\"id\":\"s{i}\",\"receipt_number\":\"{1000 + i}\",\"created_at\":\"{t:yyyy-MM-ddTHH:mm:ss}\",\"payment_method\":\"{pm[rnd.Next(pm.Length)]}\",\"total\":{tot},\"status\":\"paid\",\"user_display\":\"{users[rnd.Next(users.Length)]}\",\"cashbox_name\":\"Касса 1\",\"first_item_name\":\"{prods[0].N}\",\"items_count\":{n},\"items\":[{string.Join(",", items)}]}}";
            list.Add(J(json));
        }
        _month = list; return list;
    }
    static void InstallMonthSales()
    {
        var m = MonthSales();
        // сводка по товарам из тех же чеков
        var agg = new Dictionary<string, (double Q, double Rev, double Buy)>();
        double rev = 0, cogs = 0; int cnt = 0;
        foreach (var e in m)
        {
            if (e.GetProperty("payment_method").GetString() == "debt") continue;
            cnt++;
            foreach (var it in e.GetProperty("items").EnumerateArray())
            {
                var n = it.GetProperty("name").GetString()!; var q = it.GetProperty("quantity").GetDouble(); var t = it.GetProperty("total").GetDouble(); var b = it.GetProperty("purchase_price").GetDouble();
                agg.TryGetValue(n, out var a); agg[n] = (a.Q + q, a.Rev + t, b); rev += t; cogs += b * q;
            }
        }
        string I(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var gross = rev - cogs;
        var top = string.Join(",", agg.OrderByDescending(x => x.Value.Rev).Select(x => $"{{\"product_id\":\"{PId(x.Key)}\",\"name\":\"{x.Key}\",\"qty_sold\":{I(x.Value.Q)},\"sold\":{I(x.Value.Q)},\"revenue\":{I(x.Value.Rev)},\"purchase_price\":{I(x.Value.Buy)}}}"));
        var cards = $"{{\"revenue\":{I(rev)},\"transactions\":{cnt},\"avg_check\":{I(Math.Round(rev / cnt, 2))},\"gross_profit\":{I(gross)},\"margin_percent\":{I(Math.Round(gross / rev * 100, 1))}}}";
        var sales = FakeSvc.Make<ISalesApiService>(new()
        {
            ["PosSalesListAsync"] = a => ((int)a[0]! > 1) ? new List<JsonElement>() : m,
            ["PosSaleGetAsync"] = a => m.FirstOrDefault(e => e.GetProperty("id").GetString() == (string)a[0]!),
            ["MarketSummaryCardsAsync"] = a => (IReadOnlyList<JsonElement>?)((IReadOnlyList<(DateTime, DateTime)>)a[0]!).Select(_ => J(cards)).ToList(),
            ["MarketProductsReportAsync"] = a => J($"{{\"tables\":{{\"top_by_revenue\":[{top}],\"categories\":[]}}}}"),
            ["MarketSalesReportAsync"] = a => J($"{{\"cards\":{cards},\"charts\":{{}},\"tables\":{{\"top_products\":[{top}]}}}}"),
        });
        typeof(NurMarketKassa.AvaloniaHost.App).GetProperty("SalesApi", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, sales);
    }

    static Control? Tab(Window w, string text)
    {
        bool M(string? t) => t != null && (t == text || (text.StartsWith("-") && t.EndsWith(text)));
        return Les.All<Control>(w).FirstOrDefault(c => c.IsEffectivelyVisible && ((c is TabItem ti && M(ti.Header?.ToString())) || (c is ToggleButton && Les.All<TextBlock>(c).Any(t => M(t.Text)))));
    }
    static void Pick(Control? c)
    {
        if (c is ToggleButton tb) { tb.IsChecked = true; tb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        else if (c is TabItem ti) ti.IsSelected = true;
        for (var i = 0; i < 30; i++) Rec.Pump(5, 50);
    }

    // ================= V-02 Отчёты: продажи, финансы, ABC =================
    static void V02()
    {
        Les.Begin("V-02", 1296, 879); FeatData.Fill(); InstallMonthSales();
        var sw = (Window)Activator.CreateInstance(Les.AppAsm.GetTypes().First(x => x.Name == "SalesWindow"), true)!;
        Feat.OpenWindow(sw, 1296, 879);
        for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
        Dump(sw, "SW");
        Les.SnapWin("01-sales-today", sw, Les.Rc("period", Tab(sw, "Сегодня"), sw), Les.Rc("grid", Les.All<Control>(sw).FirstOrDefault(c => c is DataGrid && c.IsEffectivelyVisible), sw));
        Pick(Tab(sw, "Месяц"));
        Les.SnapWin("02-sales-month", sw, Les.Rc("period", Tab(sw, "Месяц"), sw), Les.Rc("kpi", Les.Text(sw, "Выручка"), sw), Les.Rc("grid", Les.All<Control>(sw).FirstOrDefault(c => c is DataGrid && c.IsEffectivelyVisible), sw));
        Pick(Tab(sw, "Показатели"));
        Les.SnapWin("03-sales-indicators", sw, Les.Rc("tab", Tab(sw, "Показатели"), sw));
        Pick(Tab(sw, "-анализ"));
        Les.SnapWin("04-sales-abc", sw, Les.Rc("tab", Tab(sw, "-анализ"), sw));
        Dump(sw, "SW2");
        sw.Close();
        var fw = (Window)Activator.CreateInstance(Les.AppAsm.GetTypes().First(x => x.Name == "FinanceWindow"), true)!;
        Feat.OpenWindow(fw, 1296, 879);
        for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
        Pick(Tab(fw, "Месяц"));
        Les.SnapWin("05-finance-month", fw, Les.Rc("period", Tab(fw, "Месяц"), fw), Les.Rc("excel", Les.Btn(fw, "Excel"), fw));
        Pick(Tab(fw, "-анализ"));
        Les.SnapWin("06-finance-abc", fw, Les.Rc("tab", Tab(fw, "-анализ"), fw));
        Pick(Tab(fw, "Аналитика"));
        Les.SnapWin("07-finance-analytics", fw, Les.Rc("tab", Tab(fw, "Аналитика"), fw));
        Dump(fw, "FW");
        fw.Close();
    }

    // ================= V-01 Сводка =================
    static void V01()
    {
        Les.Begin("V-01", 1920, 1080); Feat.Scale = 1;
        typeof(FeatDebts).GetMethod("Seed", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { true });
        var w = (Window)typeof(AdScenes).GetMethod("OwnerBrief", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        Feat.OpenWindow(w, 1920, 1080); Rec.Pump(60, 30);
        foreach (var a in Screens.Post.ToList()) a();
        Screens.Post.Clear(); Rec.Pump(20, 30);
        var t = w.GetType();
        var dt = t.GetNestedType("AttentionDebts", BindingFlags.NonPublic)!;
        t.GetField("_attnDebts", AnyI)!.SetValue(w, Activator.CreateInstance(dt, new object?[] { 275000.0, 5, 60000.0, false, null }));
        t.GetField("_attnDebtsLoaded", AnyI)!.SetValue(w, true);
        t.GetMethod("ApplyLowStock", AnyI)!.Invoke(w, null); Rec.Pump(20, 30);
        Dump(w, "OW");
        var side = Les.Text(w, "Сводка", true);
        Les.SnapWin("01-brief", w, Les.Rc("title", Les.Text(w, "Сводка", true), w), Les.Rc("side", side, w));
        Les.SnapWin("02-period", w, Les.Rc("period", Les.Text(w, "Сегодня", true)?.GetVisualAncestors().OfType<Control>().Skip(1).FirstOrDefault(), w), Les.Rc("today", Les.Text(w, "Сегодня", true), w), Les.Rc("advisor", Les.Btn(w, "Спросить Нур Советника"), w));
        Les.SnapWin("03-kpi", w, Les.Rc("rev", Les.Text(w, "Выручка (без долга)"), w), Les.Rc("checks", Les.Text(w, "Чеки", true), w), Les.Rc("avg", Les.Text(w, "Средний чек", true), w), Les.Rc("profit", Les.Text(w, "Валовая прибыль"), w));
        Les.SnapWin("04-attention", w, Les.Rc("attn", Les.Text(w, "Требует внимания", true), w), Les.Rc("debts", Les.Text(w, "Долги клиентов"), w), Les.Rc("stock", Les.Text(w, "Гречка"), w), Les.Rc("more", Les.Text(w, "Ещё товаров на исходе"), w));
        Les.SnapWin("05-week", w, Les.Rc("week", Les.Text(w, "Выручка за 7 дней"), w), Les.Rc("pay", Les.Text(w, "Способы оплаты", true), w));
        Les.SnapWin("06-recent", w, Les.Rc("recent", Les.Text(w, "Последние продажи", true), w), Les.Rc("best", Les.Text(w, "Лучшие товары", true), w));
        w.Close();
    }

    // ================= V-03 Зарплата от прибыли =================
    static void V03()
    {
        Les.Begin("V-03", 1296, 879);
        var from = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); var to = DateTime.Today;
        var pnl = JsonDocument.Parse("{\"revenue\":392000,\"cogs\":273000,\"gross_profit\":119000,\"opex\":{\"total\":57000,\"salaries\":30000,\"rent\":12000,\"taxes\":4000,\"utilities\":3000,\"other\":8000},\"operating_profit\":62000}").RootElement.Clone();
        var snap = ProfitReport.Parse(pnl, null);
        var doc = new ProfitPayDocument();
        doc.Shop.SubtractSalaries = true; doc.Shop.Rounding = ProfitPayRounding.Som;
        doc.Schemes["u-ainura"] = new ProfitPayScheme { UserId = "u-ainura", Name = "Айнура", Scope = ProfitPayScope.Own, Rule = new ProfitPayRule { Base = ProfitPayBase.NetProfit, Percent = 10 } };
        doc.Schemes["u-erlan"] = new ProfitPayScheme { UserId = "u-erlan", Name = "Эрлан", Scope = ProfitPayScope.Own, Rule = new ProfitPayRule { Base = ProfitPayBase.NetProfit, Percent = 5 } };
        var data = new ProfitPayData
        {
            From = from, To = to, Snapshot = snap, Doc = doc, SalaryLoaded = true, HoursLoaded = false,
            Server = new List<ProfitPayData.ServerRow> { new("u-ainura", "Айнура", "оклад 15 000 сом", 15000m, 241000m), new("u-erlan", "Эрлан", "оклад 12 000 сом", 12000m, 151000m) },
        };
        data.Recompute();
        typeof(ProfitPayWindow).GetProperty("LoadOverride", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, (Func<DateTime, DateTime, CancellationToken, Task<ProfitPayData>>)((f, tt, ct) => Task.FromResult(data)));
        var win = new ProfitPayWindow(from, to);
        Feat.OpenWindow(win, 1296, 879); Rec.Pump(60, 30);
        Les.SnapWin("01-top", win, Les.Rc("title", Les.Text(win, "Зарплата от прибыли", true), win), Les.Rc("period", Les.Text(win, "Период:"), win), Les.Rc("buttons", Les.Btn(win, "Excel"), win));
        Les.SnapWin("02-result", win, Les.Rc("result", Les.Text(win, "Результат магазина"), win), Les.Rc("rev", Les.Text(win, "Выручка", true), win), Les.Rc("cogs", Les.Text(win, "Закупочная цена"), win), Les.Rc("net", Les.Text(win, "Чистая прибыль", true), win));
        var tgls = win.GetVisualDescendants().OfType<Button>().Where(x => (x.Content?.ToString() ?? "").Contains("Как посчитано")).ToList();
        foreach (var tg in tgls.Take(1)) tg.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Rec.Pump(30, 25);
        Les.SnapWin("03-how", win, Les.Rc("how", tgls.FirstOrDefault(), win), Les.Rc("net", Les.Text(win, "Чистая прибыль", true), win));
        Les.SnapWin("04-employees", win, Les.Rc("emp", Les.Text(win, "Сотрудники", true), win), Les.Rc("ainura", Les.Text(win, "Айнура", true), win), Les.Rc("rule", Les.Text(win, "от чистой прибыли"), win), Les.Rc("total", Les.Text(win, "Итого", true), win), Les.Rc("scheme", Les.Btn(win, "Схема"), win));
        foreach (var tg in tgls.Skip(1).Take(1)) tg.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Rec.Pump(30, 25);
        Les.SnapWin("05-employee-how", win, Les.Rc("how", tgls.Skip(1).FirstOrDefault(), win), Les.Rc("ainura", Les.Text(win, "Айнура", true), win));
        Les.SnapWin("06-export", win, Les.Rc("excel", Les.Btn(win, "Excel", true), win), Les.Rc("csv", Les.Btn(win, "CSV", true), win), Les.Rc("pdf", Les.Btn(win, "PDF", true), win), Les.Rc("settings", Les.Btn(win, "Настройки магазина"), win));
        win.Close();
    }

    static void Register4(Dictionary<string, Action> all)
    {
        all["les-v01"] = V01; all["les-v03"] = V03;
        all["les-v02"] = V02;
        all["les-dbg3"] = () =>
        {
            Les.Begin("dbg"); FeatData.Fill(); InstallMonthSales();
            foreach (var nm in new[] { "SalesWindow", "FinanceWindow", "AbcAnalysisWindow" })
            {
                try
                {
                    var t = Les.AppAsm.GetTypes().First(x => x.Name == nm);
                    var w = (Window)Activator.CreateInstance(t, true)!;
                    Feat.OpenWindow(w, 1296, 879);
                    for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
                    Les.SnapWin("dbg-" + nm, w);
                    w.Close();
                }
                catch (Exception ex) { Console.WriteLine("FAIL " + nm + " " + ex.GetBaseException().Message); }
            }
        };
    }
}
