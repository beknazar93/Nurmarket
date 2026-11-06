// 2026-10-30: съёмка кадров видеоуроков NurCRM (K-01…K-09, V-01…V-03). Настоящие окна и диалоги кассы/программы владельца на вымышленных данных
// магазина «Нур» (стенд: подменный сервер, песочница базы, запись настроек выключена). Каждая сцена пишет PNG кадров по шагам урока и
// frames.json: { имя: { w, h, scale, rects: { ключ: [x,y,w,h] в пикселях окна (1x) } } } — их читает tools/video/lessons/gen_lesson.py.
// Запуск: run1.sh ru light les-k01   (по одной сцене на процесс; OUT=…/lessons/shots/K-01)
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NurMarketKassa;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

public static class Les
{
    public const BindingFlags AnyI = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    public static readonly Assembly AppAsm = typeof(NurMarketKassa.AvaloniaHost.App).Assembly;
    public static string Id = "";
    static JsonObject _meta = new();
    public static int WinW = 1600, WinH = 900;

    public static void Begin(string id, int w = 1600, int h = 900)
    {
        Id = id; WinW = w; WinH = h; FeatMain.WinW = w; FeatMain.WinH = h;
        Feat.Scale = 2;
        try { UserPreferences.Instance.UiScalePercent = double.Parse(Environment.GetEnvironmentVariable("LES_UI") ?? "50", CultureInfo.InvariantCulture); } catch (Exception ex) { Console.WriteLine("ui: " + ex.Message); }
        var f = Path.Combine(Rec.OutDir, "frames.json");
        _meta = File.Exists(f) ? (JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? new JsonObject()) : new JsonObject();
        Directory.CreateDirectory(Rec.OutDir);
    }

    public sealed class R { public string Key = ""; public Control? C; public Window? W; public double[]? Fixed; }
    public static R Rc(string key, Control? c, Window? w) => new R { Key = key, C = c, W = w };
    public static R Rf(string key, double x, double y, double w, double h) => new R { Key = key, Fixed = new[] { x, y, w, h } };

    /// <summary>Кадр: окно main (+ диалог dlg поверх), PNG + прямоугольники. docked: окно оплаты справа, как в кассе.</summary>
    public static void Snap(string name, Window main, Window? dlg = null, bool docked = false, double dim = 0.40, double dx = 0, double dy = 0, params R[] rects) => Snap(name, main, dlg, docked, dim, dx, dy, false, rects);
    public static void Snap(string name, Window main, Window? dlg, bool docked, double dim, double dx, double dy, bool dlg1x, params R[] rects)
    {
        Rec.Pump(10, 25);
        RenderTargetBitmap bmp;
        double ox = 0, oy = 0;
        if (dlg != null)
        {
            Rec.Pump(10, 25);
            bmp = dlg1x ? Compose1x(main, dlg, dim, dx, dy, docked) : Feat.Compose(main, dlg, dim, dx, dy, docked);
            double W = main.Bounds.Width, H = main.Bounds.Height, w = dlg.Bounds.Width, h = dlg.Bounds.Height;
            ox = docked ? W - 8 - w : (W - w) / 2.0 + dx; oy = (H - h) / 2.0 + dy;
        }
        else bmp = Feat.Render(main);
        var path = Path.Combine(Rec.OutDir, name + ".png");
        bmp.Save(path);
        var rj = new JsonObject();
        foreach (var r in rects)
        {
            double[]? v = r.Fixed;
            if (v == null && r.C != null && r.W != null)
            {
                try
                {
                    v = Rec.Rect(r.C, r.W);
                    if (dlg != null && ReferenceEquals(r.W, dlg)) { v[0] += ox; v[1] += oy; }
                }
                catch (Exception ex) { Console.WriteLine("  rect " + r.Key + ": " + ex.Message); }
            }
            if (v != null) rj[r.Key] = Rec.ToArr(v);
        }
        _meta[name] = new JsonObject { ["w"] = bmp.PixelSize.Width, ["h"] = bmp.PixelSize.Height, ["scale"] = Feat.Scale, ["rects"] = rj, ["win"] = new JsonArray(main.Bounds.Width, main.Bounds.Height) };
        File.WriteAllText(Path.Combine(Rec.OutDir, "frames.json"), Rec.ToJson(_meta));
        Console.WriteLine($"PNG {path}  rects={rj.Count}");
    }

    /// <summary>Диалоги ModalShell при 2x рисуются вдвое крупнее окна: снимаем их в 1x и вставляем в кадр 2x увеличенными (честно: тот же вид, мягче края).</summary>
    static RenderTargetBitmap Compose1x(Window main, Window dlg, double dim, double dx, double dy, bool docked)
    {
        var bm = Feat.Render(main);
        Rec.Pump(6, 20);
        var bd = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Round(dlg.Bounds.Width)), Math.Max(1, (int)Math.Round(dlg.Bounds.Height))));
        bd.Render(dlg);
        double W = bm.PixelSize.Width / (double)Feat.Scale, H = bm.PixelSize.Height / (double)Feat.Scale, w = dlg.Bounds.Width, h = dlg.Bounds.Height;
        using (var dc = bm.CreateDrawingContext(false))
        {
            dc.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb((byte)(dim * 255), 0, 0, 0)), new Rect(0, 0, W, H));
            var x = (W - w) / 2.0 + dx; var y = (H - h) / 2.0 + dy;
            if (docked) { x = W - 8 - w; y = (H - h) / 2.0; }
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                dc.DrawImage(bd, new Rect(0, 0, bd.PixelSize.Width, bd.PixelSize.Height), new Rect(x, y, w, h));
        }
        return bm;
    }

    /// <summary>Отдельное окно целиком (экран покупателя, окна разделов).</summary>
    public static void SnapWin(string name, Window w, params R[] rects)
    {
        var bmp = Feat.Render(w);
        bmp.Save(Path.Combine(Rec.OutDir, name + ".png"));
        var rj = new JsonObject();
        foreach (var r in rects) { try { var v = r.Fixed ?? (r.C != null ? Rec.Rect(r.C, w) : null); if (v != null) rj[r.Key] = Rec.ToArr(v); } catch (Exception ex) { Console.WriteLine("  rect " + r.Key + ": " + ex.Message); } }
        _meta[name] = new JsonObject { ["w"] = bmp.PixelSize.Width, ["h"] = bmp.PixelSize.Height, ["scale"] = Feat.Scale, ["rects"] = rj };
        File.WriteAllText(Path.Combine(Rec.OutDir, "frames.json"), Rec.ToJson(_meta));
        Console.WriteLine("PNG " + name + " rects=" + rj.Count);
    }

    // ---- поиск элементов
    public static IEnumerable<T> All<T>(Visual v) where T : Visual => v.GetVisualDescendants().OfType<T>();
    public static Control? Text(Window w, string text, bool exact = false)
    {
        var tb = All<TextBlock>(w).FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Width > 0 && (exact ? t.Text == text : (t.Text ?? "").Contains(text)));
        return tb;
    }
    /// <summary>Кнопка (или другой ближайший Button) с таким текстом внутри.</summary>
    public static Control? Btn(Window w, string text, bool exact = false)
    {
        var tb = Text(w, text, exact);
        if (tb == null) return null;
        return tb.GetVisualAncestors().OfType<Button>().FirstOrDefault() ?? (Control)tb;
    }
    public static Control? Box(Window w, string name) => All<Control>(w).FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
    public static Control? Row(Window w, string startsWith)
    {
        var tb = All<TextBlock>(w).FirstOrDefault(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith(startsWith));
        if (tb == null) return null;
        // строка списка — ближайший ListBoxItem
        return tb.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() ?? (Control)tb;
    }
    public static Control? Watermark(Window w, string part) => All<TextBox>(w).FirstOrDefault(t => t.IsEffectivelyVisible && (t.Watermark ?? "").Contains(part));
    public static void Set(object o, string prop, object? v) => o.GetType().GetProperty(prop, AnyI)!.GetSetMethod(true)!.Invoke(o, new[] { v });
    public static object? Get(object o, string prop) => o.GetType().GetProperty(prop, AnyI)?.GetGetMethod(true)?.Invoke(o, null);
    public static object? Fld(object o, string f) => o.GetType().GetField(f, AnyI)?.GetValue(o);
    public static void Call(object o, string m, params object?[] a) => o.GetType().GetMethod(m, AnyI)!.Invoke(o, a);

    public static BasketPanelViewModel Basket => FeatMain.Vm.Basket;
    public static ProductX P(string n) => new ProductX(FeatData.Products.First(p => p.Title.StartsWith(n)));
    public sealed record ProductX(CatalogProductTileVm V);

    public static CheckoutDialog Checkout(Window main, CheckoutViewModel vm)
    {
        var d = new CheckoutDialog(vm, Stub.Make<IDialogService>());
        d.DockTo(main); d.SystemDecorations = SystemDecorations.None; d.Show(main); Rec.Pump(30, 25); d.DockTo(main); Rec.Pump(30, 25);
        return d;
    }
    public static SaleReceiptCardData Card(string title, string number, (string, string, string, string?)[] lines, string total, (string, string, int)[] pay, string client = "", bool isReturn = false)
    {
        var c = new SaleReceiptCardData { Shop = "Нур", Address = "г. Бишкек, ул. Жибек Жолу, 12", Number = number, Shift = "128", DateTimeText = DateTime.Now.ToString("dd.MM.yyyy HH:mm"), Cashier = "Айнура", Cashbox = "Касса 1", Client = client,
            Status = isReturn ? SaleReceiptCardData.StatusKind.Return : SaleReceiptCardData.StatusKind.Paid, StatusText = isReturn ? "Возврат" : "Оплачен", Title = SaleReceiptCardData.TitleText(isReturn), TotalLabel = "ИТОГО", TotalText = total, ShowStatusBadge = true, StatusNotable = isReturn };
        foreach (var l in lines) c.Lines.Add(new SaleReceiptCardData.Line(l.Item1, l.Item2, l.Item3, l.Item4));
        foreach (var p in pay) c.Payment.Add(new SaleReceiptCardData.Row(p.Item1, p.Item2, p.Item3));
        return c;
    }
    public static Window Receipt(SaleReceiptCardData card)
    {
        var d = (Window)Activator.CreateInstance(typeof(ReceiptPreviewDialog), AnyI, null, new object[] { card }, null)!;
        d.SystemDecorations = SystemDecorations.None; d.SizeToContent = SizeToContent.Manual; d.MaxHeight = 2000; d.Width = 520; d.Height = 840;
        return d;
    }
    public static void NewCheck() { var c = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<NurMarketKassa.Interfaces.ICartService>(Program.Host!.Services); c.ResetForNewReceipt(); Basket.RefreshFromCart(); Rec.Pump(20, 25); }
    public static void Cleanup(params Window?[] ws) { foreach (var w in ws) try { w?.Close(); } catch { } }
}

public static partial class FeatLessons
{
    const BindingFlags AnyI = Les.AnyI;
    static Control? Tile(Window w, string name)
    {
        var tb = Les.All<TextBlock>(w).FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == name);
        return tb?.GetVisualAncestors().OfType<Button>().FirstOrDefault() ?? tb?.GetVisualAncestors().OfType<Border>().Skip(1).FirstOrDefault() ?? (Control?)tb;
    }

    public static void Register(Dictionary<string, Action> all)
    {
        all["les-k01"] = K01; Register2(all); Register3(all); Register4(all);
        all["les-dbg"] = () =>
        {
            Les.Begin("dbg");
            var card = Les.Card("Продажа", "000128", new[] { ("Адыгене 1,5 л", "1 × 55", "55", (string?)null) }, "55 сом", new[] { ("Оплата", "Наличные", 0) });
            var d = Les.Receipt(card); d.Show(); Rec.Pump(60, 30);
            foreach (var c in Les.All<Control>(d).Take(40)) Console.WriteLine($"{c.GetType().Name} {c.Name} b={c.Bounds} vis={c.IsEffectivelyVisible}");
            Feat.Scale = 1; Feat.Png(d, "dbg-receipt");
        };
    }

    // ================= K-01 Первая продажа =================
    static void K01()
    {
        Les.Begin("K-01");
        var w = FeatMain.Build(); FeatMain.Online(true); FeatMain.ResetBasket();
        var vm = FeatMain.Vm;
        var bc = Les.Box(w, "BarcodeInputBox");
        var pay = Les.Btn(w, "Оплатить");
        var newChk = Les.Btn(w, "Новый чек");
        var catalog = Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "CatalogPanelView");
        var basket = Les.All<Control>(w).FirstOrDefault(c => c.GetType().Name == "BasketPanelView");
        Les.Snap("01-main-empty", w, null, false, 0, 0, 0, Les.Rc("barcode", bc, w), Les.Rc("pay", pay, w), Les.Rc("catalog", catalog, w), Les.Rc("basket", basket, w), Les.Rc("newcheck", newChk, w));

        // 2. штрихкод в поле (сканер «печатает» код и жмёт Enter)
        var ady = FeatData.Products[2];
        Les.Set(vm.Basket, "BarcodeInput", ady.Barcode); Rec.Pump(10, 20);
        Les.Snap("02-barcode-typed", w, null, false, 0, 0, 0, Les.Rc("barcode", bc, w));
        vm.Basket.BarcodeInput = "";
        FeatMain.AddLine(2); Rec.Pump(10, 20);
        var line1 = Les.Row(w, "#1");
        Les.Snap("03-line-added", w, null, false, 0, 0, 0, Les.Rc("line1", line1, w), Les.Rc("total", Les.Text(w, "Итого"), w));

        // 3. количество 2 → скан ASU
        var qbox = Les.All<TextBox>(w).Where(t => t.IsEffectivelyVisible).OrderByDescending(t => t.Bounds.Y).FirstOrDefault(t => t.Text == "1" && t.Name != "BarcodeInputBox");
        Les.Set(vm.Basket, "ManualQuantity", "2"); Rec.Pump(10, 20);
        var asu = FeatData.Products[10];
        Les.Set(vm.Basket, "BarcodeInput", asu.Barcode); Rec.Pump(10, 20);
        Les.Snap("04-qty2-typed", w, null, false, 0, 0, 0, Les.Rc("barcode", bc, w), Les.Rc("qty", qbox, w));
        vm.Basket.BarcodeInput = "";
        FeatMain.AddLine(10, 1); Les.Set(vm.Basket, "ManualQuantity", "1"); Rec.Pump(10, 20);
        var line2 = Les.Row(w, "#2");
        Les.Snap("05-qty2-added", w, null, false, 0, 0, 0, Les.Rc("line2", line2, w), Les.Rc("line1", Les.Row(w, "#1"), w), Les.Rc("total", Les.Text(w, "Итого"), w), Les.Rc("pay", pay, w));

        // 4. оплата: окно оплаты, наличные
        var totals = new CartTotalsCalculator.CartTotals { Subtotal = 105, TotalDue = 105, RawTotal = 105, LineCount = 2 };
        var cvm = new CheckoutViewModel(totals, "", ""); cvm.CashReceived = ""; cvm.IsPrintReceiptEnabled = true;
        var d = Les.Checkout(w, cvm);
        Les.Snap("06-checkout-open", w, d, true, 0.40, 0, 0, Les.Rc("cashbox", Les.All<TextBox>(d).FirstOrDefault(t => t.IsEffectivelyVisible), d), Les.Rc("cashbtn", Les.Btn(d, "Наличные"), d), Les.Rc("paybtn", Les.Btn(d, "Оплатить 105"), d));
        cvm.CashReceived = "200"; Rec.Pump(20, 25);
        var tbs = Les.All<TextBlock>(d).Where(t => t.IsEffectivelyVisible).ToList();
        var chg = tbs.FirstOrDefault(t => (t.Text ?? "").Contains("Сдача") || (t.Text ?? "").Contains("95"));
        Les.Snap("07-checkout-cash", w, d, true, 0.40, 0, 0, Les.Rc("change", chg, d), Les.Rc("paybtn", Les.Btn(d, "Оплатить 105"), d), Les.Rc("cashbox", Les.All<TextBox>(d).FirstOrDefault(t => t.IsEffectivelyVisible), d));
        Les.Cleanup(d);
        // 5. успех и печать
        var card = Les.Card("Продажа", "000128", new[] { ("Адыгене 1,5 л", "1 × 55", "55", (string?)null), ("ASU", "2 × 25", "50", null) }, "105 сом",
            new[] { ("Оплата", "Наличные", 0), ("Внесено", "200", 0), ("Сдача", "95", 3) });
        var ok = Les.Receipt(card); ok.Show(w); Rec.Pump(60, 30);
        Les.Snap("08-receipt", w, ok, false, 0.40, 0, 0, true, Les.Rc("dlg", ok, ok));
        Les.Cleanup(ok);
        // 6. новый чек
        Les.NewCheck();
        Les.Snap("09-new-check", w, null, false, 0, 0, 0, Les.Rc("newcheck", newChk, w), Les.Rc("barcode", bc, w));
        Les.Cleanup(w);
    }
}
