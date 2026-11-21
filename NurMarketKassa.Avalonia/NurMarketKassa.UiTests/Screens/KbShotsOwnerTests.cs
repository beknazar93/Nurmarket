using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19: снимки программы владельца для базы знаний 2.0 (склад и его вкладки, калькуляция, маркетплейс, разделы-отчёты). См. KbShots.</summary>
[Collection("UiScenarios")]
public sealed class KbShotsOwnerTests
{
    private static readonly UiSize Wide = new(1280, 762);
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;

    private static void Pick(TabControl tabs, TabItem item) => tabs.SelectedItem = item;

    private static TabItem BarcodesTab(TabControl tabs) =>
        tabs.Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text is "Штрихкоды"));

    [AvaloniaFact]
    public async Task Owner_Warehouse_Tabs()
    {
        var tabs = new (string Name, string Tab)[]
        {
            ("owner-warehouse", "ProductsTabItem"), ("owner-warehouse-receiving", "ReceivingTabItem"), ("owner-warehouse-revision", "RevisionTabItem"),
            ("owner-warehouse-writeoff", "WriteOffTabItem"), ("owner-warehouse-transfer", "MovementsTabItem"), ("owner-warehouse-label", "Barcodes"),
            ("owner-price-tag-setup", "Barcodes"),
        };
        if (!tabs.Any(t => KbShots.Want(t.Name)))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var savedOwner = AppMode.IsOwner;
        var savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
        IsOwnerProp.SetValue(null, true);
        UserPreferences.Instance.BulkPriceTagUnlocked = true; // вкладка «Штрихкоды» — с содержимым, а не с подсказкой о покупке
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using (UiShot.Render(window, Wide)) { }
            UiShot.Settle(10, 40);
            var tc = UiDrive.Find<TabControl>(window, "WarehouseTabs");
            var barcodes = BarcodesTab(tc); // по русской подписи, пока язык русский
            foreach (var (name, tab) in tabs.Where(t => KbShots.Want(t.Name)))
            {
                foreach (var lang in KbShots.Langs)
                {
                    UiShot.Setup(UiTheme.Light, lang);
                    Pick(tc, tab == "Barcodes" ? barcodes : UiDrive.Find<TabItem>(window, tab));
                    UiShot.Settle(10, 40);
                    KbShots.Shot(window, name, lang, Wide);
                }
            }
        }
        finally
        {
            IsOwnerProp.SetValue(null, savedOwner);
            UserPreferences.Instance.BulkPriceTagUnlocked = savedUnlocked;
            window.Close();
            UiShot.Pump();
            restore();
        }
    }

    private static readonly BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static void Set(Window w, string box, string text) => UiDrive.Type(w, UiDrive.Find<TextBox>(w, box), text);

    [AvaloniaFact]
    public async Task Owner_Calculator_Marketplace()
    {
        var names = new[] { "owner-calc-price", "owner-calc-margin", "owner-calculator", "owner-marketplace" };
        if (!names.Any(KbShots.Want))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var savedOwner = AppMode.IsOwner;
        IsOwnerProp.SetValue(null, true);
        try
        {
            if (names.Take(3).Any(KbShots.Want))
            {
                UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
                var calc = new CalculatorWindow();
                try
                {
                    using (UiShot.Render(calc, Wide)) { }
                    UiShot.Settle(6, 40);
                    foreach (var lang in KbShots.Langs)
                    {
                        UiShot.Setup(UiTheme.Light, lang);
                        UiDrive.Click(calc, UiDrive.Find<Button>(calc, "TabPriceButton"));
                        UiDrive.Click(calc, UiDrive.Find<Button>(calc, "ModeMarkupButton"));
                        Set(calc, "CostBox", "35");
                        Set(calc, "ModeValueBox", "30");
                        Set(calc, "TaxBox", "0");
                        UiShot.Settle(6, 40);
                        if (KbShots.Want("owner-calc-price"))
                            KbShots.Shot(calc, "owner-calc-price", lang, Wide);
                        UiDrive.Click(calc, UiDrive.Find<Button>(calc, "ModeMarginButton"));
                        Set(calc, "ModeValueBox", "25");
                        Set(calc, "TaxBox", "1");
                        UiShot.Settle(6, 40);
                        if (KbShots.Want("owner-calc-margin"))
                            KbShots.Shot(calc, "owner-calc-margin", lang, Wide);
                        if (KbShots.Want("owner-calculator"))
                        {
                            UiDrive.Click(calc, UiDrive.Find<Button>(calc, "TabBatchButton"));
                            var rows = (System.Collections.IList)typeof(CalculatorWindow).GetField("_batchRows", Inst)!.GetValue(calc)!;
                            string[][] demo = [["Молоко 3,2 % 1 л", "100", "62"], ["Сыр Российский", "20", "448"], ["Сахар 1 кг", "200", "68"]];
                            for (var i = 0; i < rows.Count && i < demo.Length; i++)
                            {
                                var r = rows[i]!;
                                r.GetType().GetProperty("Name")!.SetValue(r, demo[i][0]);
                                r.GetType().GetProperty("QuantityText")!.SetValue(r, demo[i][1]);
                                r.GetType().GetProperty("PriceText")!.SetValue(r, demo[i][2]);
                            }
                            Set(calc, "ExtraCostsBox", "6000");
                            typeof(CalculatorWindow).GetMethod("RecalcBatch", Inst)!.Invoke(calc, null);
                            var grid = UiDrive.Find<DataGrid>(calc, "BatchGrid"); // строки менялись напрямую — таблице нужно перечитать их
                            grid.ItemsSource = null;
                            grid.ItemsSource = rows;
                            UiShot.Settle(8, 40);
                            KbShots.Shot(calc, "owner-calculator", lang, Wide);
                        }
                    }
                }
                finally
                {
                    calc.Close();
                    UiShot.Pump();
                }
            }
            if (KbShots.Want("owner-marketplace"))
            {
                // Карточки Маркетплейса собираются при создании окна — для каждого языка окно создаётся заново.
                foreach (var lang in KbShots.Langs)
                {
                    UiShot.Setup(UiTheme.Light, lang);
                    var market = new MarketplaceWindow().AsSection();
                    try
                    {
                        using (UiShot.Render(market, Wide)) { }
                        UiShot.Settle(12, 40);
                        KbShots.Shot(market, "owner-marketplace", lang, Wide);
                    }
                    finally
                    {
                        market.Close();
                        UiShot.Pump();
                    }
                }
            }
        }
        finally
        {
            IsOwnerProp.SetValue(null, savedOwner);
            restore();
        }
    }

    private static string SaleRow(string id, int number, string total, string item, int daysAgo, string method, string? client = null) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"total\":\"" + total + "\",\"payment_method\":\"" + method + "\""
        + ",\"created_at\":\"" + new DateTimeOffset(DateTime.Today.AddHours(15 - daysAgo % 5).AddDays(-daysAgo)).ToString("O") + "\",\"first_item_name\":\"" + item + "\",\"user_display\":\"Айбек\""
        + (client is null ? "" : ",\"client\":\"c-1\",\"client_name\":\"" + client + "\"") + "}";

    /// <summary>«Продажи» и «Сводка» программы владельца: список чеков — из заглушки (вымышленные чеки), остальное — как отдаёт стенд.</summary>
    [AvaloniaFact]
    public async Task Owner_Sales_Overview()
    {
        var names = new[] { "owner-sales", "owner-overview" };
        if (!names.Any(KbShots.Want))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var savedOwner = AppMode.IsOwner;
        IsOwnerProp.SetValue(null, true);
        try
        {
            await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
            var rows = new[]
            {
                SaleRow("s-1", 2101, "414.00", "Кока-Кола 1 л", 0, "cash"), SaleRow("s-2", 2102, "260.00", "Сыр Российский", 0, "card"),
                SaleRow("s-3", 2103, "12500.00", "Стиральный порошок 3 кг", 1, "cash", "Азамат Усупов"), SaleRow("s-4", 2104, "135.50", "Шоколад молочный", 1, "cash"),
                SaleRow("s-5", 2105, "98.00", "Сахар 1 кг", 2, "cash"), SaleRow("s-6", 2106, "1280.00", "Курица охлаждённая", 2, "card"),
                SaleRow("s-7", 2107, "310.00", "Чай чёрный листовой 100 пакетиков", 3, "cash"), SaleRow("s-8", 2108, "720.00", "Масло подсолнечное 1 л", 3, "cash"),
            };
            UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":8,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
            if (KbShots.Want("owner-sales"))
            {
                UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
                var sales = TestHost.Services.GetRequiredService<SalesWindow>();
                try
                {
                    using (UiShot.Render(sales, Wide)) { }
                    UiShot.Settle(12, 40);
                    UiDrive.Click(sales, UiDrive.ButtonByText(sales, "Неделя")); // за неделю чеков больше — таблица живее
                    KbShots.WaitFor(() => UiDrive.AllText(sales).Contains("Сыр Российский") && UiDrive.AllText(sales).Contains("Сахар"), 6000);
                    foreach (var lang in KbShots.Langs)
                    {
                        UiShot.Setup(UiTheme.Light, lang);
                        UiShot.Settle(8, 40);
                        KbShots.Shot(sales, "owner-sales", lang, Wide);
                    }
                }
                finally
                {
                    sales.Close();
                    UiShot.Pump();
                }
            }
            if (KbShots.Want("owner-overview"))
            {
                UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
                var shell = new OwnerShellWindow { Width = Wide.Width, Height = Wide.Height };
                try
                {
                    using (UiShot.Render(shell, Wide)) { }
                    UiShot.Settle(14, 40);
                    foreach (var lang in KbShots.Langs)
                    {
                        UiShot.Setup(UiTheme.Light, lang);
                        UiShot.Settle(8, 40);
                        KbShots.Shot(shell, "owner-overview", lang, Wide);
                    }
                }
                finally
                {
                    shell.Close();
                    UiShot.Pump();
                }
            }
        }
        finally
        {
            IsOwnerProp.SetValue(null, savedOwner);
            restore();
        }
    }
}
