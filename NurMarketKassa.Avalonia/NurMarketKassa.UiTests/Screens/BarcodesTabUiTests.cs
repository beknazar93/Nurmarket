using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «массовая печать ценников/штрихкодов — выведи в Склад сверху! Как в вебе!»): настоящие окна в Avalonia.Headless.
/// «Склад» — видимая вкладка «Штрихкоды» сразу после «Приёмки», внутри массовая печать (тот же элемент, что в окне «Печать штрихкодов и ценников»); не куплена печать или нет права —
/// вместо неё подсказка с «Ввести серийный номер» (платность не сломана); галочка «В печать» в строке товара и кнопка «Печать ценников (N)» передают выбранные товары во вкладку;
/// программа владельца — пункт меню «Штрихкоды и ценники» (в группе «Товары», ищется поиском, скрывается в «Разделах меню»). Снимки для глаз:
/// %TEMP%\nurmarket-tests\ui-actual\barcodes-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BarcodesTabUiTests : IDisposable
{
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly Func<Window?, string, string?> _savedPrompt = PaidFeatureGate.SerialPrompt;
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;

    public BarcodesTabUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        IsOwnerProp.SetValue(null, _savedIsOwner);
        PaidFeatureGate.SerialPrompt = _savedPrompt;
        SectionVisibility.Set("barcodes", false);
    }

    private static string TabText(AppLanguage lang) => lang == AppLanguage.Kyrgyz ? "Штрих-коддор" : "Штрихкоды";

    private static TabControl Tabs(WarehouseWindow w) => UiDrive.Find<TabControl>(w, "WarehouseTabs");

    private static readonly string[] LabelsTabNames = ["Штрихкоды", "Штрих-коддор", "Barcodes", "Barkodlar", "Shtrix-kodlar", "Ценники и штрихкоды", "Баа белгилери жана штрих-коддор", "Price tags and barcodes", "Fiyat etiketleri ve barkodlar", "Narx yorliqlari va shtrix-kodlar"];

    private static TabItem BarcodesTab(WarehouseWindow w) =>
        Tabs(w).Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text != null && LabelsTabNames.Contains(x.Text)));

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    // ------------------------------------------------------------------ «Склад»: вкладка «Штрихкоды»

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task Warehouse_BarcodesTab_IsVisible_AfterReceiving_AndOpensPrint(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiShot.Settle(6);
            var tabs = Tabs(window);
            var tab = BarcodesTab(window);
            tab.IsVisible.Should().BeTrue("вкладка «Штрихкоды» видна");
            tab.Header.Should().BeOfType<StackPanel>();
            ((StackPanel)tab.Header!).Children.OfType<TextBlock>().Single().Text.Should().Be(TabText(lang));
            var order = tabs.Items.OfType<TabItem>().ToList();
            order.IndexOf(tab).Should().Be(order.IndexOf(UiDrive.Find<TabItem>(window, "ReceivingTabItem")) + 1, "сразу после «Приёмки»");
            order.IndexOf(tab).Should().BeLessThan(order.IndexOf(UiDrive.Find<TabItem>(window, "RevisionTabItem")), "выше «Ревизии», «Списания», «Перемещения» и «Редактора этикетки»");

            // в первом ряду: вкладка стоит на одной линии с «Товарами» и целиком внутри полосы вкладок
            var products = UiDrive.Find<TabItem>(window, "ProductsTabItem");
            var yBarcodes = tab.TranslatePoint(default, window)!.Value.Y;
            var yProducts = products.TranslatePoint(default, window)!.Value.Y;
            yBarcodes.Should().BeApproximately(yProducts, 1, $"«Штрихкоды» в первом ряду вкладок ({w}x{h} {lang})");
            (tab.TranslatePoint(new Point(tab.Bounds.Width, 0), window)!.Value.X).Should().BeLessThanOrEqualTo(window.Bounds.Width, "вкладка не уходит за край окна");
            tab.Bounds.Width.Should().BeGreaterThan(60, "подпись и значок читаются");

            // нажатие по вкладке открывает печать
            UiDrive.Click(window, tab);
            UiShot.Settle(8);
            tabs.SelectedItem.Should().BeSameAs(tab);
            var host = window.GetVisualDescendants().OfType<BarcodePrintTabHost>().Single();
            host.View.Should().NotBeNull("печать куплена — вместо подсказки сама печать");
            host.View!.FoundCount.Should().Be(CatalogCacheService.Products.Count, "в печати весь каталог (поиск пуст)");
            window.GetVisualDescendants().OfType<BarcodePrintView>().Should().ContainSingle();
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "Товар: 0" : "Товаров: 0", "строка выбора печати на месте");
            window.SelectNavTab("barcodes").Should().BeTrue("Нур Советник и словарь переходов находят вкладку по ключу «barcodes»");
            SaveShot(window, $"barcodes-tab-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(1366, 768)]
    [InlineData(1024, 600)]
    public async Task Warehouse_BarcodesTab_NotPurchased_ShowsHint_AndKeepsPaywall(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = false;
        UserPreferences.Instance.BulkPriceTagUnlocked.Should().BeFalse("проверка — без покупки (и без выданного сервером)");
        var prompts = new List<string>();
        PaidFeatureGate.SerialPrompt = (_, name) => { prompts.Add(name); return null; }; // окно серийного номера — без показа, «отмена»
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            var tab = BarcodesTab(window);
            tab.IsVisible.Should().BeTrue("вкладка видна и без покупки — платность проверяется внутри");
            UiDrive.Click(window, tab);
            UiShot.Settle(6);
            var host = window.GetVisualDescendants().OfType<BarcodePrintTabHost>().Single();
            host.View.Should().BeNull("не куплено — печати внутри нет");
            var text = UiDrive.AllText(window);
            text.Should().Contain("Платная функция").And.Contain("Печать штрихкодов и ценников");
            var button = UiDrive.ButtonByText(window, "Ввести серийный номер");
            UiDrive.Click(window, button);
            UiShot.Settle(4);
            prompts.Should().ContainSingle("кнопка просит серийный номер той же проверкой, что окно печати");
            prompts[0].Should().Contain("Массовая печать ценников");
            host.View.Should().BeNull("серийный номер не введён — доступ не появился");
            UserPreferences.Instance.BulkPriceTagUnlocked.Should().BeFalse();
            SaveShot(window, $"barcodes-tab-locked-ru-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-21 (владелец: «собери в складе все редакторы в одну вкладку»): названия вкладок склада (строка или значок + текст).</summary>
    private static List<string> TabTitles(WarehouseWindow w) =>
        Tabs(w).Items.OfType<TabItem>().Where(t => t.IsVisible)
            .Select(t => t.Header is StackPanel sp ? string.Concat(sp.Children.OfType<TextBlock>().Select(x => x.Text)) : t.Header?.ToString() ?? "").ToList();

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Uzbek)]
    public async Task Warehouse_HasExactlyOneLabelsTab_AndNoOtherPrintEntryPoints(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiShot.Settle(8);
            // ровно одна вкладка «Ценники и штрихкоды»; старой «Редактор этикетки» и отдельной «Штрихкоды» нет
            var titles = TabTitles(window);
            var expected = WarehouseWindow.BarcodesTabText;
            titles.Count(t => t == expected).Should().Be(1, $"единственная вкладка печати и редакторов: {string.Join(" | ", titles)}");
            titles.Count(t => t.Contains("шаблон", StringComparison.OrdinalIgnoreCase) || t.Contains("этикет", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Label", StringComparison.OrdinalIgnoreCase) || t.Contains("редактор", StringComparison.OrdinalIgnoreCase)).Should().Be(0, "других вкладок редакторов нет");
            window.LabelsTab.Should().NotBeNull();
            window.FindControl<Control>("LabelEditorTabItem").Should().BeNull("заглушка «Редактор этикетки» удалена");
            // верхняя панель, приёмка: кнопок печати нет; 2026-11-22 (владелец: «чек бокс выделение верни»): панель массовых действий и галочки в строках — есть
            foreach (var name in new[] { "BulkPriceTagButton", "ReceivingLabelsButton", "LabelEditorOpenButton" })
                window.FindControl<Control>(name).Should().BeNull($"«{name}» убрана — печать только на вкладке «Ценники и штрихкоды»");
            window.FindControl<Control>("BulkBar").Should().NotBeNull("панель массовых действий возвращена");
            window.FindControl<Control>("BulkBar")!.IsVisible.Should().BeFalse("пока ничего не отмечено, панели не видно");
            var grid = UiDrive.Find<DataGrid>(window, "ProductsGrid");
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.DataContext is CatalogProductTileVm && r.Bounds.Height > 0).ToList();
            rows.Count.Should().BeGreaterThanOrEqualTo(1);
            rows.SelectMany(r => r.GetVisualDescendants().OfType<CheckBox>()).Should().NotBeEmpty("галочка «В печать» в строке товара вернулась");
            // меню «⋯»: 2026-11-22 (владелец: «верни штрихкод сюда обратно») — «Печать этикетки» есть, и стоит перед «Без заводского штрихкода»; «Этикетка…» и «Ценник» по-прежнему нет
            var product = (CatalogProductTileVm)rows[0].DataContext!;
            var items = window.BuildRowMenu(product).Items.OfType<MenuItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i) ?? "").ToList();
            items.Should().NotBeEmpty();
            items.Count(x => x == WarehouseWindow.RowLabelText).Should().Be(1, $"в «⋯» товара есть «Печать этикетки»: {string.Join(" | ", items)}");
            items.Should().NotContain(x => x.Contains("…", StringComparison.Ordinal) && (x.Contains("Этикетка", StringComparison.OrdinalIgnoreCase) || x.Contains("Label", StringComparison.OrdinalIgnoreCase)),
                "«Этикетка…» убрана");
            items.Should().NotContain(x => x.Contains("ценник", StringComparison.OrdinalIgnoreCase) || x.Contains("Price tag", StringComparison.OrdinalIgnoreCase)
                || x.Contains("Баа белгиси", StringComparison.OrdinalIgnoreCase) || x.Contains("Narx yorlig", StringComparison.OrdinalIgnoreCase), "«Ценник» в «⋯» нет");
            var labelAt = items.IndexOf(WarehouseWindow.RowLabelText);
            var noFactoryAt = items.FindIndex(x => x.Contains(Tr.T("заводского", "Заводдук", "factory", "Fabrika", "Zavod"), StringComparison.OrdinalIgnoreCase));
            noFactoryAt.Should().BeGreaterThan(labelAt, "порядок: … Резервы, Печать этикетки, Без заводского штрихкода, Загрузить фото, Удалить");
            // в строке остались только «Изменить» и «⋯» (значка этикетки нет)
            rows[0].GetVisualDescendants().OfType<Button>().Count(b => b is not CheckBox && b.IsEffectivelyVisible && b.Bounds.Width > 0).Should().BeLessThanOrEqualTo(3, "кнопки строки: остаток, «Изменить», «⋯» (галочка «В печать» — не кнопка)");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_OpenBarcodesTab_WithProducts_SelectsLabelsTab_AndMarksProducts()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var grid = UiDrive.Find<DataGrid>(window, "ProductsGrid");
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.DataContext is CatalogProductTileVm && r.Bounds.Height > 0).ToList();
            rows.Count.Should().BeGreaterThanOrEqualTo(2, "в таблице склада два товара поддельного сервера");
            var picked = (CatalogProductTileVm)rows[0].DataContext!;

            window.OpenBarcodesTab(new[] { picked.Id }).Should().BeTrue();
            UiShot.Settle(8);
            Tabs(window).SelectedItem.Should().BeSameAs(BarcodesTab(window), "вкладка «Ценники и штрихкоды» открылась");
            var view = window.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            view.SelectedCount.Should().Be(1, "переданный товар отмечен в печати");
            view.BuildItems().Should().ContainSingle().Which.Product.Id.Should().Be(picked.Id);

            // позиции с копиями (после прихода товара): те же копии, что передали
            Tabs(window).SelectedItem = UiDrive.Find<TabItem>(window, "ProductsTabItem");
            UiShot.Settle(4);
            window.OpenLabelsTabWith(new[] { (picked.Id, (string?)null, 7) }).Should().BeTrue();
            UiShot.Settle(8);
            Tabs(window).SelectedItem.Should().BeSameAs(BarcodesTab(window));
            window.LabelsHub!.Mode.Should().Be(LabelsHubMode.Print);
            window.GetVisualDescendants().OfType<BarcodePrintView>().Single().BuildItems()[0].Copies.Should().Be(7);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData("barcodes")]
    [InlineData("labels")]
    [InlineData("pricetags")]
    public async Task Warehouse_SelectNavTab_LabelsKeys_OpenTheSameSingleTab(string key)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(6);
            window.SelectNavTab(key).Should().BeTrue();
            Tabs(window).SelectedItem.Should().BeSameAs(BarcodesTab(window));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_OpenBarcodesTab_WithoutPurchase_AsksSerialNumber_AndKeepsTab()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = false;
        var prompts = new List<string>();
        PaidFeatureGate.SerialPrompt = (_, name) => { prompts.Add(name); return null; };
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var grid = UiDrive.Find<DataGrid>(window, "ProductsGrid");
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext is CatalogProductTileVm && r.Bounds.Height > 0);
            var product = (CatalogProductTileVm)row.DataContext!;
            window.OpenBarcodesTab(new[] { product.Id }).Should().BeFalse("печать не куплена — переход с товарами не выполняется");
            UiShot.Settle(4);
            prompts.Should().ContainSingle("просьба спрашивает серийный номер — платность не обойти");
            Tabs(window).SelectedItem.Should().BeSameAs(UiDrive.Find<TabItem>(window, "ProductsTabItem"), "печать не куплена — остаёмся на «Товарах»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ программа владельца

    private static async Task LoginOwnerAsync()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    private static void ShowMenuGroup(OwnerShellWindow shell, string group)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(OwnerShellWindow).GetField("_navGroup", flags)!.SetValue(shell, group);
        typeof(OwnerShellWindow).GetField("_navPeek", flags)!.SetValue(shell, true);
        typeof(OwnerShellWindow).GetMethod("ApplySidebarLayout", flags)!.Invoke(shell, null);
        typeof(OwnerShellWindow).GetMethod("ApplyNavFilter", flags)!.Invoke(shell, null);
        UiShot.Settle(4, 40);
    }

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task OwnerMenu_HasNoSeparateBarcodesItem_LabelsLiveInWarehouseTab(int w, int h, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().NotContainKey("barcodes", "отдельного пункта «Штрихкоды и ценники» в меню программы владельца больше нет");
            IsOwnerProp.SetValue(null, true); // каталог «Разделов меню» зависит от режима программы
            SectionVisibility.Catalog.Select(c => c.Key).Should().NotContain("barcodes", "в «Разделах меню» отдельного пункта печати нет");
            ShowMenuGroup(shell, "products");
            NavButtons(shell)["warehouse"].IsEffectivelyVisible.Should().BeTrue("«Склад» — единственный вход к печати и редакторам");
            var tag = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}";
            SaveShot(shell, $"barcodes-owner-menu-{tag}", new UiSize(w, h));
            NavButtons(shell)["warehouse"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<WarehouseWindow>().Any(), 3000);
            var warehouse = shell.OwnedWindows.OfType<WarehouseWindow>().FirstOrDefault();
            warehouse.Should().NotBeNull();
            UiShot.Settle(6, 40);
            warehouse!.SelectNavTab("barcodes").Should().BeTrue();
            UiShot.Settle(10, 60);
            warehouse.GetVisualDescendants().OfType<BarcodePrintView>().Should().ContainSingle("печать куплена — внутри вкладки сама печать");
            SaveShot(shell, $"barcodes-owner-section-{tag}", new UiSize(w, h));
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_Search_ByLabelWords_FindsWarehouse_AndEnterOpensLabelsTab()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            var search = shell.FindControl<TextBox>("NavSearchText")!;
            // 2026-11-21: окно 1360 ≤ 1366 — колонка пунктов свёрнута сама; как у человека (Ctrl+K), сперва раскрываем её, затем печатаем в поиск
            typeof(OwnerShellWindow).GetMethod("FocusNavSearch", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(shell, null);
            UiShot.Settle(3, 30);
            foreach (var word in new[] { "ценник", "штрихкод", "этикетка" })
            {
                search.Text = word;
                UiShot.Settle(3, 30);
                NavButtons(shell)["warehouse"].IsEffectivelyVisible.Should().BeTrue($"поиск по меню «{word}» находит «Склад»");
            }
            // Enter по найденному — «Склад» сразу на вкладке «Ценники и штрихкоды»
            search.Text = "ценник";
            UiShot.Settle(3, 30);
            search.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            WaitFor(() => shell.OwnedWindows.OfType<WarehouseWindow>().Any(), 3000);
            UiShot.Settle(8, 40);
            var warehouse = shell.OwnedWindows.OfType<WarehouseWindow>().FirstOrDefault();
            warehouse.Should().NotBeNull("Enter открыл «Склад»");
            Tabs(warehouse!).SelectedItem.Should().BeSameAs(BarcodesTab(warehouse!), "и сразу вкладку «Ценники и штрихкоды»");
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerSection_Warehouse_ShowsBarcodesTab()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell)["warehouse"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<WarehouseWindow>().Any(), 3000);
            var warehouse = shell.OwnedWindows.OfType<WarehouseWindow>().FirstOrDefault();
            warehouse.Should().NotBeNull("«Склад» открыт разделом программы владельца");
            UiShot.Settle(6, 40);
            BarcodesTab(warehouse!).IsEffectivelyVisible.Should().BeTrue("вкладка «Ценники и штрихкоды» видна и в разделе «Склад» программы владельца");
            TabTitles(warehouse!).Count(t => t == WarehouseWindow.BarcodesTabText).Should().Be(1);
            warehouse!.SelectNavTab("barcodes").Should().BeTrue();
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void AdvisorNavRouter_LabelWords_OpenTheSingleLabelsTab()
    {
        var target = AdvisorNavRouter.ById("warehouse.barcodes");
        target.Should().NotBeNull();
        target!.Section.Should().Be("warehouse");
        target.Tab.Should().Be("barcodes");
        AdvisorNavRouter.ById("warehouse.labels").Should().BeNull("отдельной вкладки «Редактор этикетки» больше нет");
        foreach (var phrase in new[] { "открой ценники", "открой редактор этикетки", "открой штрихкоды", "перейди в печать ценников" })
        {
            var route = AdvisorNavRouter.Parse(phrase);
            route.Kind.Should().Be(AdvisorNavRouter.RouteKind.Single, phrase);
            route.Target!.Id.Should().Be("warehouse.barcodes", phrase);
        }
    }
}
