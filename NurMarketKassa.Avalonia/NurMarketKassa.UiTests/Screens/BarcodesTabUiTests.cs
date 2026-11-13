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

    private static TabItem BarcodesTab(WarehouseWindow w) =>
        Tabs(w).Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text is "Штрихкоды" or "Штрих-коддор"));

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
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "Тандалды: 0" : "Выбрано: 0", "строка выбора печати на месте");
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

    [AvaloniaFact]
    public async Task Warehouse_ProductPick_TransfersSelectedProducts_ToBarcodesTab()
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
            var box = rows[0].GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible);
            box.IsChecked.Should().BeFalse();
            box.Bounds.Width.Should().BeGreaterThanOrEqualTo(24).And.BeLessThanOrEqualTo(40, "галочка — отдельная цель касания в строке");
            Avalonia.Automation.AutomationProperties.GetName(box).Should().Contain("печат");
            var button = UiDrive.Find<Button>(window, "BulkPriceTagButton");
            var baseText = UiDrive.TextOf(button);

            UiDrive.Click(window, box);
            UiShot.Settle(3);
            box.IsChecked.Should().BeTrue();
            window.PrintPickedIds.Should().BeEquivalentTo(new[] { picked.Id });
            UiDrive.TextOf(button).Should().Be("Печать ценников (1)", "в кнопке — число выбранных");
            // рядом с прежним окном печати (пункт «⋯» и прочие) ничего не пропало: тот же товар открывается двойным щелчком, а не галочкой
            UiDrive.Click(window, button);
            UiShot.Settle(8);
            Tabs(window).SelectedItem.Should().BeSameAs(BarcodesTab(window), "кнопка открывает вкладку «Штрихкоды»");
            var view = window.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            view.SelectedCount.Should().Be(1, "выбранный товар отмечен в печати");
            view.FoundCount.Should().Be(1, "пока поиск пуст, показан только выбранный");
            view.BuildItems().Should().ContainSingle().Which.Product.Id.Should().Be(picked.Id);
            view.BuildItems()[0].Copies.Should().Be(Math.Clamp(BarcodePrintSettingsStore.Load().DefaultCopies, 1, 99), "копии — по умолчанию из настроек печати");

            // вернуться на «Товары»: галочка помнится, снять — кнопка возвращается к прежней подписи
            Tabs(window).SelectedItem = UiDrive.Find<TabItem>(window, "ProductsTabItem");
            UiShot.Settle(6);
            var box2 = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, picked)).GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible);
            box2.IsChecked.Should().BeTrue("отметка не потерялась при переходе по вкладкам");
            UiDrive.Click(window, box2);
            UiShot.Settle(3);
            window.PrintPickedIds.Should().BeEmpty();
            UiDrive.TextOf(button).Should().Be(baseText, "без выбранных — прежняя подпись «Печать штрихкодов и ценников»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_ProductPick_WithoutPurchase_AsksSerialNumber_AndKeepsTab()
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
            var button = UiDrive.Find<Button>(window, "BulkPriceTagButton");
            button.IsVisible.Should().BeFalse("печать не куплена и ничего не выбрано — кнопки нет (как раньше)");
            UiDrive.Click(window, row.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible));
            UiShot.Settle(3);
            button.IsVisible.Should().BeTrue("с выбранным товаром кнопка есть, чтобы можно было ввести серийный номер");
            UiDrive.Click(window, button);
            UiShot.Settle(4);
            prompts.Should().ContainSingle("нажатие спрашивает серийный номер — платность не обойти");
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
    public async Task OwnerMenu_BarcodesItem_OpensPrintSection(int w, int h, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("barcodes", "пункт «Штрихкоды и ценники» в группе «Товары» виден владельцу");
            IsOwnerProp.SetValue(null, true); // каталог «Разделов меню» зависит от режима программы
            SectionVisibility.Catalog.Select(c => c.Key).Should().Contain("barcodes", "пункт можно скрыть в «Разделах меню»");
            IsOwnerProp.SetValue(null, true);
            ShowMenuGroup(shell, "products");
            NavButtons(shell)["barcodes"].IsEffectivelyVisible.Should().BeTrue("пункт в раскрытой группе «Товары»");
            NavButtons(shell)["warehouse"].IsEffectivelyVisible.Should().BeTrue();
            var tag = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}";
            SaveShot(shell, $"barcodes-owner-menu-{tag}", new UiSize(w, h));
            NavButtons(shell)["barcodes"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<BarcodePrintSectionWindow>().Any(), 3000);
            var section = shell.OwnedWindows.OfType<BarcodePrintSectionWindow>().FirstOrDefault();
            section.Should().NotBeNull("пункт открывает раздел печати штрихкодов и ценников");
            UiShot.Settle(6, 40);
            section!.Host.View.Should().NotBeNull("печать куплена — внутри сама печать");
            section.GetVisualDescendants().OfType<BarcodePrintView>().Should().ContainSingle();
            UiShot.Settle(12, 60); // раздел показывает «Открываем раздел…», пока строится
            SaveShot(section, $"barcodes-owner-section-{tag}", new UiSize(w, h));
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_BarcodesItem_FoundBySearch_HiddenByMenuSections()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("barcodes");
            var search = shell.FindControl<TextBox>("NavSearchText")!;
            foreach (var word in new[] { "ценник", "штрихкод" })
            {
                search.Text = word;
                UiShot.Settle(3, 30);
                NavButtons(shell)["barcodes"].IsEffectivelyVisible.Should().BeTrue($"поиск по меню «{word}» находит «Штрихкоды и ценники»");
            }
            search.Text = "";
            UiShot.Settle(2, 30);
            SectionVisibility.Set("barcodes", true);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().NotContainKey("barcodes", "скрытый в «Разделах меню» пункт в меню не показывается");
            SectionVisibility.Set("barcodes", false);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().ContainKey("barcodes");
        }
        finally
        {
            SectionVisibility.Set("barcodes", false);
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
            BarcodesTab(warehouse!).IsEffectivelyVisible.Should().BeTrue("вкладка «Штрихкоды» видна и в разделе «Склад» программы владельца");
            warehouse!.SelectNavTab("barcodes").Should().BeTrue();
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void AdvisorNavRouter_KnowsBarcodesTab()
    {
        var target = AdvisorNavRouter.ById("warehouse.barcodes");
        target.Should().NotBeNull();
        target!.Section.Should().Be("warehouse");
        target.Tab.Should().Be("barcodes");
    }
}
