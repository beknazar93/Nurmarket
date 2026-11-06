// 2026-10-30: кадры уроков K-05…K-08 (долг, возврат, смена, офлайн). Подменные сервисы продаж и клиентов — в памяти, настоящих запросов нет.
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using NurMarketKassa;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

/// <summary>Подмена любого интерфейса сервиса: имя метода -> функция; остальное — пустой ответ.</summary>
public class FakeSvc : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> H = new();
    public static T Make<T>(Dictionary<string, Func<object?[], object?>> h) where T : class
    {
        var p = DispatchProxy.Create<T, FakeSvc>(); ((FakeSvc)(object)p).H = h; return p;
    }
    protected override object? Invoke(MethodInfo? m, object?[]? a)
    {
        var rt = m!.ReturnType;
        if (H.TryGetValue(m.Name, out var f))
        {
            var v = f(a ?? Array.Empty<object?>());
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(rt.GetGenericArguments()[0]).Invoke(null, new[] { v })!;
            return v;
        }
        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = rt.GetGenericArguments()[0];
            object? val = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>)) val = Activator.CreateInstance(inner);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { val })!;
        }
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }
}

public static partial class FeatLessons
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Четыре чека за сегодня; №1001 — три строки (Нан, Сүт, Адыгене), оплата наличными.</summary>
    public static string SaleJson(int n)
    {
        var d = DateTime.Today.AddHours(8 + (n - 1000)).AddMinutes(17 * (n - 1000)).ToString("yyyy-MM-ddTHH:mm:ss");
        var items = n switch
        {
            1001 => new[] { ("l1", "nur0", "Нан", 1, 30), ("l2", "nur1", "Сүт", 1, 65), ("l3", "nur2", "Адыгене 1,5 л", 1, 55) },
            1002 => new[] { ("l1", "nur3", "Winston Blue", 1, 205) },
            1003 => new[] { ("l1", "nur4", "Семечки 100 г", 2, 25), ("l2", "nur6", "Вода 0,5 л", 1, 25) },
            _ => new[] { ("l1", "nur7", "Кефир 1% 0,9 л", 1, 78) },
        };
        var total = items.Sum(i => i.Item4 * i.Item5);
        var its = string.Join(",", items.Select(i => $"{{\"cart_item_id\":\"{n}-{i.Item1}\",\"product_id\":\"{i.Item2}\",\"product_name\":\"{i.Item3}\",\"name\":\"{i.Item3}\",\"quantity\":{i.Item4},\"price\":{i.Item5},\"unit_price\":{i.Item5},\"total\":{i.Item4 * i.Item5}}}"));
        return $"{{\"id\":\"sale-{n}\",\"receipt_number\":\"{n}\",\"created_at\":\"{d}\",\"total\":{total},\"payment_method\":\"cash\",\"status\":\"completed\",\"customer\":\"Айнура\",\"items\":[{its}]}}";
    }

    public static void InstallFakeSales()
    {
        var sales = FakeSvc.Make<ISalesApiService>(new()
        {
            ["PosSalesListAsync"] = a => new List<JsonElement> { J(SaleJson(1004)), J(SaleJson(1003)), J(SaleJson(1002)), J(SaleJson(1001)) },
            ["PosSaleGetAsync"] = a => J(SaleJson(int.Parse(((string)a[0]!).Replace("sale-", "")))),
        });
        typeof(NurMarketKassa.AvaloniaHost.App).GetProperty("SalesApi", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, sales);
    }

    static Window ModalDlg(Window d, int w, int h) { d.SystemDecorations = SystemDecorations.None; d.SizeToContent = SizeToContent.Manual; d.MaxHeight = 2000; d.Width = w; d.Height = h; return d; }
    static void Click(Control? c) { c?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Rec.Pump(20, 30); }
    static void Dump(Window d, string tag) { foreach (var c in Les.All<Control>(d).Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && (c is TextBlock || c is CheckBox || c is Button || c is TextBox || c is ComboBox || c is ListBox || c is DataGrid || c is ToggleButton))) { var t = (c as TextBlock)?.Text ?? (c as TextBox)?.Text ?? (c as Button)?.Content?.ToString() ?? (c as CheckBox)?.Content?.ToString(); Console.WriteLine($"{tag} {c.GetType().Name} [{c.Name}] {c.Bounds} {t}"); } }

    // ================= K-06 Возврат по чеку =================
    static void K06()
    {
        Les.Begin("K-06"); InstallFakeSales();
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        Les.Snap("01-main", w, null, false, 0, 0, 0, Les.Rc("more", Les.Btn(w, "Ещё"), w), Les.Rc("hist", Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "MainToolbarView"), w));
        var d = (Window)new ReturnSaleDialog(); ModalDlg(d, 1000, 830); d.Show(w);
        for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
        var sel = Les.All<Button>(d).Where(b => b.IsEffectivelyVisible && (b.Content?.ToString() ?? "") == "Выбрать").ToList();
        Les.Snap("02-list", w, d, false, 0.4, 0, 0, true, Les.Rc("list", Les.Box(d, "SalesDataGrid"), d), Les.Rc("search", Les.Box(d, "SearchReceiptBox"), d), Les.Rc("row", sel[3], d));
        var sb = (TextBox)Les.Box(d, "SearchReceiptBox")!; sb.Text = "1001"; for (var i = 0; i < 10; i++) Rec.Pump(5, 50);
        sel = Les.All<Button>(d).Where(b => b.IsEffectivelyVisible && (b.Content?.ToString() ?? "") == "Выбрать").ToList();
        Les.Snap("03-search", w, d, false, 0.4, 0, 0, true, Les.Rc("search", sb, d), Les.Rc("row", sel.FirstOrDefault(), d));
        Click(sel.First());
        for (var i = 0; i < 30; i++) Rec.Pump(5, 50);
        var cbs = Les.All<CheckBox>(d).Where(c => c.IsEffectivelyVisible).ToList();
        Les.Snap("04-selected", w, d, false, 0.4, 0, 0, true, Les.Rc("lines", cbs.FirstOrDefault(), d), Les.Rc("title", Les.Box(d, "SelectedSaleText"), d), Les.Rc("sel", Les.Box(d, "ReturnSelectedButton"), d));
        cbs[0].IsChecked = true; Rec.Pump(20, 30);
        Les.Snap("05-checked", w, d, false, 0.4, 0, 0, true, Les.Rc("check", cbs[0], d), Les.Rc("sel", Les.Box(d, "ReturnSelectedButton"), d), Les.Rc("whole", Les.Box(d, "ReturnWholeReceiptButton"), d));
        Les.Cleanup(d);
        var card = Les.Card("Возврат", "000130", new[] { ("Нан", "1 × 30", "30", (string?)null) }, "30 сом", new[] { ("Возврат", "Наличные", 0), ("Возвращено покупателю", "30", 3) }, "", true);
        var rc = Les.Receipt(card); rc.Show(w); Rec.Pump(60, 30);
        Les.Snap("06-return-receipt", w, rc, false, 0.40, 0, 0, true, Les.Rc("dlg", rc, rc));
        Les.Cleanup(rc, w);
    }

    // ================= K-07 Смена =================
    static void K07()
    {
        Les.Begin("K-07");
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        Les.Snap("01-main", w, null, false, 0, 0, 0, Les.Rc("open", Les.Btn(w, "Открыть смену"), w), Les.Rc("close", Les.Btn(w, "Закрыть смену"), w), Les.Rc("cashbal", Les.Text(w, "Касса:"), w));
        var d = new OpenShiftDialog(); ModalDlg(d, 700, 520); d.Show(w); Rec.Pump(40, 30);
        Dump(d, "OPEN");
        Les.Snap("02a-open-empty", w, d, false, 0.4, 0, 0, true, Les.Rc("cash", Les.Box(d, "OpeningCashBox"), d));
        ((TextBox)Les.Box(d, "OpeningCashBox")!).Text = "3000"; Rec.Pump(20, 30);
        Les.Snap("02-open-dialog", w, d, false, 0.4, 0, 0, true, Les.Rc("dlg", d, d), Les.Rc("cash", Les.Box(d, "OpeningCashBox"), d), Les.Rc("okbtn", Les.Btn(d, "Открыть смену", true), d));
        Les.Cleanup(d);
        var dep = new NewOperationDialog(true); ModalDlg(dep, 640, 560); dep.Show(w); Rec.Pump(40, 30); Dump(dep, "DEP");
        ((TextBox)Les.Box(dep, "AmountBox")!).Text = "1000"; ((TextBox)Les.Box(dep, "CommentBox")!).Text = "Размен на начало дня"; Rec.Pump(20, 30);
        Les.Snap("03-deposit", w, dep, false, 0.4, 0, 0, true, Les.Rc("dlg", dep, dep), Les.Rc("amount", Les.Box(dep, "AmountBox"), dep), Les.Rc("tab", Les.Box(dep, "DepositTab"), dep));
        Les.Cleanup(dep);
        var wd = new NewOperationDialog(false); ModalDlg(wd, 640, 560); wd.Show(w); Rec.Pump(40, 30); Dump(wd, "WD");
        ((TextBox)Les.Box(wd, "AmountBox")!).Text = "450"; Rec.Pump(20, 30);
        Les.Snap("04-withdraw", w, wd, false, 0.4, 0, 0, true, Les.Rc("dlg", wd, wd), Les.Rc("amount", Les.Box(wd, "AmountBox"), wd), Les.Rc("tab", Les.Box(wd, "WithdrawTab"), wd), Les.Rc("article", Les.Box(wd, "ArticleLabel"), wd));
        Les.Cleanup(wd);
        var cl = new CloseShiftDialog(); ModalDlg(cl, 820, 760); cl.Show(w); Rec.Pump(40, 30); Dump(cl, "CLOSE");
        var fb = Les.All<TextBox>(cl).FirstOrDefault(t => t.IsEffectivelyVisible); if (fb != null) fb.Text = "12390"; Rec.Pump(20, 30);
        Les.Snap("05-close-dialog", w, cl, false, 0.4, 0, 0, true, Les.Rc("dlg", cl, cl), Les.Rc("fact", fb, cl), Les.Rc("okbtn", Les.Btn(cl, "Закрыть смену", true), cl));
        Les.Cleanup(cl);
        var today = DateTime.Today;
        var shift = new ShiftModel { Id = "shift-z", ShiftNumber = "128", OpenedAt = today.AddHours(8), ClosedAt = today.AddHours(20).AddMinutes(12), Cashier = "Айнура", Status = "Закрыта", Revenue = 18640m, OpeningCash = 3000m, CashSales = 9840m, NonCashSales = 7200m, DebtSales = 1600m, SalesCount = 52, ExpenseTotal = 450m, IncomeTotal = 0m, ExpectedCash = 12390m, ClosingCash = 12390m, CashDiff = 0m };
        var bp = typeof(ShiftDetailsDialog).GetMethod("BuildPrintableReport", BindingFlags.NonPublic | BindingFlags.Static)!;
        var text = (string)bp.Invoke(null, new object?[] { shift, null, null, null })!;
        var xshift = new ShiftModel { Id = "shift-x", ShiftNumber = "128", OpenedAt = today.AddHours(8), Cashier = "Айнура", Status = "Открыта", Revenue = 9640m, OpeningCash = 3000m, CashSales = 5240m, NonCashSales = 3200m, DebtSales = 1200m, SalesCount = 27, ExpenseTotal = 450m, IncomeTotal = 1000m, ExpectedCash = 8790m };
        var xtext = (string)bp.Invoke(null, new object?[] { xshift, null, null, null })!;
        var xd = ModalDlg(new ReceiptPreviewDialog("X-отчёт", xtext), 900, 830); xd.Show(w); Rec.Pump(60, 30);
        Les.Snap("05a-x-report", w, xd, false, 0.4, 0, 0, true, Les.Rc("dlg", xd, xd));
        Les.Cleanup(xd);
        var zd = ModalDlg(new ReceiptPreviewDialog("Z-отчёт", text), 900, 830); zd.Show(w); Rec.Pump(60, 30);
        Les.Snap("06-z-report", w, zd, false, 0.4, 0, 0, true, Les.Rc("dlg", zd, zd));
        Les.Cleanup(zd, w);
    }

    // ================= K-08 Без интернета =================
    static void K08()
    {
        Les.Begin("K-08"); InstallFakeSales();
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        var st = Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "MainToolbarView");
        Les.Snap("01-online", w, null, false, 0, 0, 0, Les.Rc("status", Les.Text(w, "Онлайн"), w));
        FeatMain.AddLine(2); FeatMain.AddLine(10); FeatMain.Online(false, 0); Rec.Pump(20, 25);
        Les.Snap("02-offline", w, null, false, 0, 0, 0, Les.Rc("status", Les.Text(w, "Офлайн") ?? Les.Text(w, "Нет связи"), w), Les.Rc("pay", Les.Btn(w, "Оплатить"), w));
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = 80, TotalDue = 80, RawTotal = 80, LineCount = 2 };
        var cvm = new CheckoutViewModel(totals, "", ""); cvm.IsPrintReceiptEnabled = true; cvm.CashReceived = "100";
        var d = Les.Checkout(w, cvm);
        Les.Snap("03-offline-pay", w, d, true, 0.4, 0, 0, Les.Rc("paybtn", Les.Btn(d, "Оплатить"), d), Les.Rc("dlg", d, d));
        Les.Cleanup(d); Les.NewCheck();
        FeatMain.Online(false, 1, "Чек сохранён, отправится автоматически. В очереди: 1");
        Les.Snap("04-queued", w, null, false, 0, 0, 0, Les.Rc("queue", Les.Text(w, "В очереди"), w), Les.Rc("msg", Les.Text(w, "Чек сохранён"), w));
        // долг без связи
        FeatMain.AddLine(0); FeatMain.AddLine(1);
        var dv = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 95, TotalDue = 95, RawTotal = 95, LineCount = 2 }, "", ""); dv.IsPrintReceiptEnabled = true; dv.IsDebt = true;
        dv.PreselectClient(new ClientOption { Id = "ata", DisplayName = "Токтогул-ата · +996 555 112 233" });
        var dd = Les.Checkout(w, dv);
        Les.Snap("05-offline-debt", w, dd, true, 0.4, 0, 0, Les.Rc("debt", Les.Btn(dd, "В долг"), dd), Les.Rc("dlg", dd, dd));
        Les.Cleanup(dd); Les.NewCheck();
        // возврат без связи
        ServerOutageMonitor.ReportFailure("stand", "нет связи", true); Rec.Pump(10, 30);
        var rd = (Window)new ReturnSaleDialog(); ModalDlg(rd, 1000, 830); rd.Show(w);
        for (var i = 0; i < 20; i++) Rec.Pump(5, 50);
        Les.Snap("06-offline-return", w, rd, false, 0.4, 0, 0, true, Les.Rc("dlg", rd, rd));
        Les.Cleanup(rd);
        FeatMain.Online(true, 0, "Все чеки из очереди отправлены на сервер."); Rec.Pump(20, 25);
        Les.Snap("07-sent", w, null, false, 0, 0, 0, Les.Rc("msg", Les.Text(w, "Все чеки"), w), Les.Rc("status", Les.Text(w, "Онлайн"), w));
        var refresh = Les.All<Button>(w).FirstOrDefault(b => b.IsEffectivelyVisible && (Avalonia.Controls.ToolTip.GetTip(b)?.ToString() ?? "").Contains("Обнов"));
        Les.Snap("08-refresh", w, null, false, 0, 0, 0, Les.Rc("refresh", refresh, w));
        Les.Cleanup(w);
    }

    // ================= K-05 Долг =================
    static void K05()
    {
        Les.Begin("K-05"); InstallFakeSales();
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        FeatMain.AddLine(1); FeatMain.AddLine(7); Rec.Pump(20, 25);
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = 143, TotalDue = 143, RawTotal = 143, LineCount = 2 };
        var cvm = new CheckoutViewModel(totals, "", ""); cvm.IsPrintReceiptEnabled = true;
        var d = Les.Checkout(w, cvm);
        Les.Snap("01-open", w, d, true, 0.4, 0, 0, Les.Rc("debt", Les.Btn(d, "В долг"), d));
        cvm.IsDebt = true; Rec.Pump(30, 30);
        Les.Snap("02-debt-tab", w, d, true, 0.4, 0, 0, Les.Rc("debt", Les.Btn(d, "В долг"), d), Les.Rc("pick", Les.Btn(d, "Выбрать клиента"), d));
        cvm.PreselectClient(new ClientOption { Id = "ata", DisplayName = "Токтогул-ата · +996 555 112 233" }); Rec.Pump(30, 30);
        Les.Snap("03-client", w, d, true, 0.4, 0, 0, Les.Rc("client", Les.Text(d, "Токтогул"), d), Les.Rc("paybtn", Les.Btn(d, "Оплатить"), d), Les.Rc("dlg", d, d));
        Les.Cleanup(d, w); Les.NewCheck();
        // оплата долга
        FeatDebtsSeed();
        var clients = FakeSvc.Make<IClientsApiService>(new()
        {
            ["GetClientsPageAsync"] = a => (new List<JsonElement> {
                J("{\"id\":\"c-ata\",\"full_name\":\"Токтогул-ата\",\"name\":\"Токтогул-ата\",\"phone\":\"+996 555 00 00 01\",\"type\":\"client\"}"),
                J("{\"id\":\"c-apa\",\"full_name\":\"Бүбү-апа\",\"name\":\"Бүбү-апа\",\"phone\":\"+996 555 00 00 02\",\"type\":\"client\"}") }, false),
        });
        var sales = FakeSvc.Make<ISalesApiService>(new());
        var w2 = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        var pd = new PayDebtDialog(clients, sales); ModalDlg(pd, 1000, 830); pd.Show(w2);
        for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
        Dump(pd, "PD");
        Les.Snap("04-paydebt", w2, pd, false, 0.4, 0, 0, true, Les.Rc("dlg", pd, pd), Les.Rc("search", Les.Box(pd, "ClientSearchBox"), pd), Les.Rc("client", Les.Text(pd, "Токтогул"), pd));
        Les.Cleanup(pd, w2);
        // оплата долга: окно «Оплата долга без связи» (настоящее окно кассы; онлайн-окно берёт долги с сервера, их стенд отдать не может)
        var dir = Path.Combine(Path.GetTempPath(), "les-offline-" + Environment.ProcessId); Directory.CreateDirectory(dir);
        OfflineClientCache.FilePath = Path.Combine(dir, "offline_clients.json"); OfflineClientCache.ResetMemory();
        OfflineOperationsStore.FilePath = Path.Combine(dir, "offline_ops.json");
        OfflineClientCache.RememberDebtors(new[] { ("ata", "Токтогул-ата", (string?)"+996 555 112 233", 1200m), ("apa", "Бүбү-апа", (string?)"+996 700 445 566", 350m), ("jyl", "Жылдыз", (string?)"+996 555 778 899", 90m) });
        var w3 = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        PosApp.ActiveShiftId = "shift-ad";
        var wt = Les.AppAsm.GetTypes().First(t => t.Name == "OfflineOperationsWindow");
        var modeT = wt.GetNestedType("Mode")!;
        var od = (Window)Activator.CreateInstance(wt, AnyI, null, new object?[] { Enum.Parse(modeT, "DebtPayment"), null }, null)!;
        ModalDlg(od, 860, 760); od.Show(w3); Rec.Pump(60, 30);
        Dump(od, "OD");
        Les.Snap("04b-debtors", w3, od, false, 0.4, 0, 0, true, Les.Rc("dlg", od, od), Les.Rc("list", Les.Fld(od, "_clientsList") as Control, od));
        var list = Les.Fld(od, "_clientsList") as ListBox;
        if (list != null) { var items = list.ItemsSource?.Cast<object>().Select(x => x?.ToString() ?? "").ToList() ?? new List<string>(); var ix = items.FindIndex(x => x.StartsWith("Токтогул")); list.SelectedIndex = ix >= 0 ? ix : 0; Rec.Pump(30, 30); }
        Les.Snap("05-client-debts", w3, od, false, 0.4, 0, 0, true, Les.Rc("dlg", od, od), Les.Rc("list", list, od), Les.Rc("amount", Les.All<TextBox>(od).FirstOrDefault(t => t.IsEffectivelyVisible && t.Name != "ClientSearchBox"), od), Les.Rc("okbtn", Les.Btn(od, "Принять оплату"), od));
        Les.Cleanup(od, w3);
    }
    static void K05_unused()
    {

    }
    static void FeatDebtsSeed()
    {
        var t = typeof(FeatDebts).GetMethod("Seed", BindingFlags.NonPublic | BindingFlags.Static)!;
        t.Invoke(null, new object[] { true });
    }

    static void Register3(Dictionary<string, Action> all)
    {
        all["les-k05"] = K05; all["les-k08"] = K08;
        all["les-k06"] = K06; all["les-k07"] = K07;
        all["les-dbg2"] = () =>
        {
            Les.Begin("dbg");
            InstallFakeSales();
            var w = FeatMain.Build(); FeatMain.Online(true);
            var d = new ReturnSaleDialog(); d.SystemDecorations = SystemDecorations.None; d.SizeToContent = SizeToContent.Manual; d.MaxHeight = 2000; d.Width = 1000; d.Height = 830; d.Show(w);
            for (var i = 0; i < 40; i++) Rec.Pump(5, 50);
            foreach (var c in Les.All<Control>(d).Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0)) { var t = (c as TextBlock)?.Text ?? (c as TextBox)?.Text ?? (c as Button)?.Content?.ToString(); Console.WriteLine($"{c.GetType().Name} [{c.Name}] {c.Bounds} {t}"); }
            Les.Snap("dbg-return", w, d, false, 0.4, 0, 0, true);
            var sel = Les.All<Button>(d).Where(b => b.IsEffectivelyVisible && (b.Content?.ToString() ?? "") == "Выбрать").ToList();
            sel[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 30; i++) Rec.Pump(5, 50);
            foreach (var c in Les.All<Control>(d).Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && (c is TextBlock || c is CheckBox || c is Button || c is TextBox || c is DataGrid || c is ListBox))) { var t = (c as TextBlock)?.Text ?? (c as TextBox)?.Text ?? (c as Button)?.Content?.ToString() ?? (c as CheckBox)?.Content?.ToString(); Console.WriteLine($"AFTER {c.GetType().Name} [{c.Name}] {c.Bounds} {t}"); }
            Les.Snap("dbg-return2", w, d, false, 0.4, 0, 0, true);
        };
    }
}
