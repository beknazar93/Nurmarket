// 2026-10-30: кадры уроков K-02…K-04 (поиск, скидки и бонусы, оплата). См. FeatLessons.cs.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NurMarketKassa;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

public static partial class FeatLessons
{
    static void Register2(Dictionary<string, Action> all)
    {
        all["les-k02"] = K02; all["les-k03"] = K03; all["les-k04"] = K04;
    }

    static T Svc<T>() where T : class => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<T>(Program.Host!.Services);

    static void Notify(object vm, params string[] props)
    {
        foreach (var pn in props)
            vm.GetType().GetMethod("OnPropertyChanged", AnyI, null, new[] { typeof(string) }, null)?.Invoke(vm, new object[] { pn });
    }

    // ================= K-02 Поиск без штрихкода =================
    static void K02()
    {
        Les.Begin("K-02");
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        var cat = FeatMain.Vm.Catalog;
        var search = Les.Watermark(w, "Поиск");
        var catalog = Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "CatalogPanelView");
        Les.Snap("01-main", w, null, false, 0, 0, 0, Les.Rc("search", search, w), Les.Rc("catalog", catalog, w));
        cat.SearchText = "flsutyt"; Rec.Pump(30, 30);
        Les.Snap("02-wrong-layout", w, null, false, 0, 0, 0, Les.Rc("search", search, w), Les.Rc("catalog", catalog, w), Les.Rc("tile", Tile(w, "Адыгене 1,5 л"), w));
        cat.SearchText = "1,5 адыг"; Rec.Pump(30, 30);
        Les.Snap("03-words", w, null, false, 0, 0, 0, Les.Rc("search", search, w), Les.Rc("tile", Tile(w, "Адыгене 1,5 л"), w));
        cat.SearchText = "адыгене"; Rec.Pump(30, 30);
        Les.Snap("04-similar", w, null, false, 0, 0, 0, Les.Rc("search", search, w), Les.Rc("tile", Tile(w, "Адыгене 1,5 л"), w), Les.Rc("catalog", catalog, w));
        FeatMain.AddLine(2); Rec.Pump(20, 25);
        Les.Snap("05-added", w, null, false, 0, 0, 0, Les.Rc("line1", Les.Row(w, "#1"), w), Les.Rc("search", search, w));
        cat.SearchText = ""; Rec.Pump(30, 30);
        foreach (var i in new[] { 0, 1, 2 }) FeatData.Products[i].IsFavorite = true;
        Rec.Pump(20, 25);
        Les.Snap("06-favorites", w, null, false, 0, 0, 0, Les.Rc("catalog", catalog, w), Les.Rc("tile", Tile(w, "Адыгене 1,5 л"), w));
        Les.Cleanup(w);
    }

    static void LineDiscount(string itemId, string mode, string value)
    {
        var cart = Svc<ICartService>();
        ReceiptSnapshotCartEditor.PatchLineDiscount(cart, itemId, mode, value);
        Les.Basket.RefreshFromCart(); Rec.Pump(20, 25);
    }

    // ================= K-03 Скидки и бонусы =================
    static void K03()
    {
        Les.Begin("K-03");
        UserPreferences.Instance.LoyaltyEnabled = true; UserPreferences.Instance.LoyaltyEarnPercent = 10;
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        FeatMain.AddLine(3); FeatMain.AddLine(2); FeatMain.AddLine(10); Rec.Pump(20, 25);
        var row1 = Les.Row(w, "#1"); var pctBtn = row1 == null ? null : Les.All<Button>(row1).FirstOrDefault(b => b.IsEffectivelyVisible);
        var lines0 = Les.Basket.Lines.ToList();
        Console.WriteLine("lines: " + string.Join(";", lines0.Select(l => Les.Get(l, "Title") + "/" + Les.Get(l, "ItemId"))));
        var wl = lines0.First(l => (string?)Les.Get(l, "Title") == "Winston Blue");
        var wid = (string)Les.Get(wl, "ItemId")!;
        Les.Snap("01-cart", w, null, false, 0, 0, 0, Les.Rc("pct", pctBtn, w), Les.Rc("line", Les.Row(w, "#1"), w), Les.Rc("basket", Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "BasketPanelView"), w));
        var d = Svc<OrderDiscountDialog>(); d.SetItemMode("Winston Blue", "percent", 10m); d.SetPreviewBase(205); d.SystemDecorations = SystemDecorations.None; d.Show(w); Rec.Pump(40, 30);
        Les.Snap("02-line-dialog", w, d, false, 0.40, 0, 0, true, Les.Rc("dlg", d, d), Les.Rc("value", Les.All<TextBox>(d).FirstOrDefault(t => t.IsEffectivelyVisible), d));
        Les.Cleanup(d);
        try { LineDiscount(wid, "percent", "10"); } catch (Exception ex) { Console.WriteLine("LINE-DISC FAIL " + ex.GetBaseException().Message); }
        Les.Snap("03-line-discounted", w, null, false, 0, 0, 0, Les.Rc("line", Les.Row(w, "#1"), w), Les.Rc("total", Les.Text(w, "Итого"), w));
        Les.Snap("04-more", w, null, false, 0, 0, 0, Les.Rc("more", Les.Btn(w, "Ещё"), w));
        var d2 = new OrderDiscountDialog("", "20"); d2.SetPreviewBase(280); d2.SystemDecorations = SystemDecorations.None; d2.Show(w); Rec.Pump(40, 30);
        Les.Snap("05-order-dialog", w, d2, false, 0.40, 0, 0, true, Les.Rc("dlg", d2, d2), Les.Rc("value", Les.All<TextBox>(d2).FirstOrDefault(t => t.IsEffectivelyVisible), d2));
        Les.Cleanup(d2);
        try { ReceiptSnapshotCartEditor.PatchOrderDiscount(Svc<ICartService>(), null, "20"); Les.Basket.RefreshFromCart(); Rec.Pump(20, 25); } catch (Exception ex) { Console.WriteLine("ORDER-DISC FAIL " + ex.GetBaseException().Message); }
        Les.Snap("05b-after-order", w, null, false, 0, 0, 0, Les.Rc("total", Les.Text(w, "Итого"), w), Les.Rc("basket", Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "BasketPanelView"), w));
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = 285, LineDiscounts = 20.5, OrderDiscount = 20, TotalDue = 244.5, RawTotal = 285, LineCount = 3 };
        var cvm = new CheckoutViewModel(totals, "", ""); cvm.IsPrintReceiptEnabled = true;
        cvm.PreselectClient(new ClientOption { Id = "ayg", DisplayName = "Айгүл Т. · +996 555 000 001" });
        Rec.Pump(20, 25);
        typeof(CheckoutViewModel).GetField("_clientLoyaltyBalance", AnyI)!.SetValue(cvm, 150.0);
        cvm.CashReceived = "";
        Notify(cvm, "ClientLoyaltyBalance", "ClientLoyaltyBalanceDisplay", "EarnedPointsPreview", "EarnedPointsPreviewDisplay", "ShowLoyaltyBlock", "PointsToRedeemInput");
        var cd = Les.Checkout(w, cvm);
        Les.Snap("06-client", w, cd, true, 0.40, 0, 0, Les.Rc("client", Les.Text(cd, "Айгүл"), cd), Les.Rc("dlg", cd, cd));
        cvm.PointsToRedeemInput = "150"; cvm.CashReceived = "110";
        cvm.GetType().GetMethod("RecalculateTotals", AnyI)?.Invoke(cvm, null);
        Notify(cvm, "ClientLoyaltyBalance", "ClientLoyaltyBalanceDisplay", "EarnedPointsPreview", "EarnedPointsPreviewDisplay", "PointsToRedeemInput", "BigTotalDisplay", "PayableDisplay", "ChangeAmountDisplay");
        Rec.Pump(20, 25);
        var pts = Les.All<TextBox>(cd).Where(t => t.IsEffectivelyVisible).ToList();
        Les.Snap("07-bonus", w, cd, true, 0.40, 0, 0, Les.Rc("bonus", pts.FirstOrDefault(t => t.Text == "150") ?? pts.FirstOrDefault(), cd), Les.Rc("dlg", cd, cd));
        Les.Cleanup(cd, w);
    }

    // ================= K-04 Оплата: карта, QR, смешанная =================
    static void K04()
    {
        Les.Begin("K-04");
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        FeatMain.AddLine(2); FeatMain.AddLine(10, 1); FeatMain.AddLine(0); Rec.Pump(20, 25);
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = 110, TotalDue = 110, RawTotal = 110, LineCount = 3 };
        var cvm = new CheckoutViewModel(totals, "", ""); cvm.IsPrintReceiptEnabled = true;
        var d = Les.Checkout(w, cvm);
        Les.Snap("01-open", w, d, true, 0.40, 0, 0, Les.Rc("card", Les.Btn(d, "Безналичные"), d), Les.Rc("dlg", d, d));
        cvm.IsTransfer = true; Rec.Pump(30, 30);
        Les.Snap("02-card", w, d, true, 0.40, 0, 0, Les.Rc("card", Les.Btn(d, "Безналичные"), d), Les.Rc("paybtn", Les.Btn(d, "Оплатить"), d), Les.Rc("dlg", d, d));
        Les.Cleanup(d);
        var (cw, st) = CustomerWinPublic();
        var lines = new List<CustomerDisplayLine> { new() { Title = "Нан", Quantity = 1, Unit = "шт", LineTotal = 30 }, new() { Title = "Сүт", Quantity = 1, Unit = "шт", LineTotal = 65 }, new() { Title = "Нарзан 1,5 л", Quantity = 1, Unit = "шт", LineTotal = 15 } };
        st.UpdateCart(new CustomerDisplayCartSnapshot { Lines = lines, Subtotal = 110, Discount = 0, Total = 110 }); Rec.Pump(40, 30);
        Les.SnapWin("03-customer-qr", cw);
        Les.Cleanup(cw);
        var cvm2 = new CheckoutViewModel(totals, "", ""); cvm2.IsPrintReceiptEnabled = true;
        var d2 = Les.Checkout(w, cvm2);
        cvm2.IsMixed = true; Rec.Pump(30, 30);
        cvm2.MixedCashAmount = "50"; cvm2.MixedNonCashAmount = "60"; Rec.Pump(30, 30);
        var tbs = Les.All<TextBox>(d2).Where(t => t.IsEffectivelyVisible).ToList();
        Les.Snap("04-mixed", w, d2, true, 0.40, 0, 0, Les.Rc("mixed", Les.Btn(d2, "Смешанная"), d2), Les.Rc("amounts", tbs.FirstOrDefault(), d2), Les.Rc("dlg", d2, d2));
        Les.Snap("05-total-check", w, d2, true, 0.40, 0, 0, Les.Rc("paybtn", Les.Btn(d2, "Оплатить"), d2), Les.Rc("total", Les.Text(d2, "Итог"), d2));
        Les.Cleanup(d2);
        var card = Les.Card("Продажа", "000129", new[] { ("Адыгене 1,5 л", "1 × 55", "55", (string?)null), ("ASU", "1 × 25", "25", null), ("Нан", "1 × 30", "30", null) }, "110 сом", new[] { ("Оплата", "Смешанная", 0), ("Наличные", "50", 0), ("Безналичные", "60", 0) });
        var ok = Les.Receipt(card); ok.Show(w); Rec.Pump(60, 30);
        Les.Snap("06-receipt", w, ok, false, 0.40, 0, 0, true, Les.Rc("dlg", ok, ok));
        Les.Cleanup(ok, w);
    }

    static (Window, NurMarketKassa.Services.CustomerDisplayStateService) CustomerWinPublic()
    {
        var st = Svc<NurMarketKassa.Services.CustomerDisplayStateService>();
        var cd = UserPreferences.Instance.CustomerDisplay; cd.IsEnabled = true; cd.ShowPaymentQr = true; cd.ShowChange = true; cd.ShowPaidAmount = true;
        UserPreferences.Instance.QrCodePath = @"C:\Users\User\NurMarket-video\shots-features\_inv\bank-qr.png";
        var w = (Window)Activator.CreateInstance(Les.AppAsm.GetTypes().First(t => t.Name == "CustomerDisplayWindow"), true)!;
        w.GetType().GetMethod("ApplySettings")!.Invoke(w, new object[] { cd });
        Feat.OpenWindow(w, 1280, 720); Rec.Pump(40, 30);
        return (w, st);
    }
}
