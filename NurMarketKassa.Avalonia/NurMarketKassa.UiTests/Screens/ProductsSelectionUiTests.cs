using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22 (владелец: «в товарах чек бокс выделение тоже верни как было раньше»; «при нажатии на печать этикетки выводил настройки и печать, либо открывал штрихкоды с выбранным товаром»):
/// галочки выбора товаров в списке и плитках «Склад → Товары», «выбрать все», панель массовых действий, зона касания не меньше 44 px, выбор переживает поиск;
/// пункт «Печать этикетки» в «⋯» открывает вкладку «Штрихкоды» с товаром (или со всеми отмеченными) и ничего не печатает молча.</summary>
[Collection("UiScenarios")]
public sealed class ProductsSelectionUiTests : IDisposable
{
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;

    public ProductsSelectionUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
    }

    public void Dispose() => UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;

    private static List<CheckBox> RowChecks(WarehouseWindow w) =>
        UiDrive.Find<DataGrid>(w, "ProductsGrid").GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is CatalogProductTileVm && r.Bounds.Height > 0)
            .Select(r => r.GetVisualDescendants().OfType<CheckBox>().Single()).ToList();

    private static CatalogProductTileVm Product(CheckBox box) => (CatalogProductTileVm)box.DataContext!;

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Uzbek)]
    public async Task List_Checkboxes_Select_Counter_SelectAll_Clear_AndTouchSize(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiShot.Settle(8);
            var checks = RowChecks(window);
            checks.Count.Should().BeGreaterThanOrEqualTo(2);
            checks.Should().OnlyContain(c => c.Bounds.Width >= 44 && c.Bounds.Height >= 44, "зона касания галочки не меньше 44 px");
            var bar = UiDrive.Find<Control>(window, "BulkBar");
            bar.IsVisible.Should().BeFalse();

            UiDrive.Click(window, checks[0]);
            UiShot.Settle(4);
            window.PrintPickedIds.Should().BeEquivalentTo(new[] { Product(checks[0]).Id });
            bar.IsVisible.Should().BeTrue("отмечен товар — видна панель массовых действий");
            UiDrive.Find<TextBlock>(window, "BulkBarCount").Text.Should().Contain("1", "счётчик отмеченных");

            // «выбрать все» в шапке таблицы отмечает все товары страницы
            var header = UiDrive.Find<DataGrid>(window, "ProductsGrid").GetVisualDescendants().OfType<DataGridColumnHeader>()
                .SelectMany(x => x.GetVisualDescendants().OfType<CheckBox>()).Single();
            header.Bounds.Width.Should().BeGreaterThanOrEqualTo(44);
            header.IsChecked.Should().BeNull("отмечена часть страницы — «чёрточка»");
            UiDrive.Click(window, header);
            UiShot.Settle(4);
            window.PrintPickedIds.Should().BeEmpty("клик по «чёрточке» снимает отметки страницы (как раньше)");
            UiDrive.Click(window, header);
            UiShot.Settle(4);
            window.PrintPickedIds.Count.Should().Be(checks.Count, "выбраны все товары страницы");
            RowChecks(window).Should().OnlyContain(c => c.IsChecked == true);

            // «Снять выбор»
            UiDrive.Click(window, UiDrive.Find<Button>(window, "BulkBarClearButton"));
            UiShot.Settle(4);
            window.PrintPickedIds.Should().BeEmpty();
            bar.IsVisible.Should().BeFalse();
            RowChecks(window).Should().OnlyContain(c => c.IsChecked != true);
            FormFieldsOverlapUiTests.FindViolations(window, "products-selection").Select(v => v.Key + " — " + v.Detail).Should().BeEmpty("поля и подписи не налезают");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Selection_SurvivesSearch_AndShowsInTiles()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var first = RowChecks(window)[0];
            var picked = Product(first);
            UiDrive.Click(window, first);
            UiShot.Settle(4);
            window.PrintPickedIds.Should().Contain(picked.Id);

            // поиск, которому ничего не соответствует, не сбрасывает выбор
            UiDrive.Type(window, UiDrive.Find<TextBox>(window, "ProductSearchBox"), "zzzzqqq-нет-такого");
            UiShot.Settle(20, 50);
            window.PrintPickedIds.Should().Contain(picked.Id, "выбор сохраняется при поиске/фильтре");
            UiDrive.Find<Control>(window, "BulkBar").IsVisible.Should().BeTrue();
            UiDrive.Find<TextBox>(window, "ProductSearchBox").Text = "";
            UiShot.Settle(20, 50);
            RowChecks(window).Single(c => Product(c).Id == picked.Id).IsChecked.Should().BeTrue("после очистки поиска товар по-прежнему отмечен");

            // плитки: галочка есть, отражает выбор, зона касания 44 px
            var tiles = UiDrive.Find<RadioButton>(window, "ViewTilesRadio");
            tiles.IsChecked = true;
            tiles.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            UiShot.Settle(8);
            var scroll = UiDrive.Find<ScrollViewer>(window, "ProductsTilesScroll");
            scroll.IsVisible.Should().BeTrue();
            var tileChecks = scroll.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is CatalogProductTileVm && c.Bounds.Height > 0).ToList();
            tileChecks.Should().NotBeEmpty("в плитках тоже есть галочка выбора");
            tileChecks.Should().OnlyContain(c => c.Bounds.Width >= 44 && c.Bounds.Height >= 44);
            tileChecks.Single(c => Product(c).Id == picked.Id).IsChecked.Should().BeTrue();
            var other = tileChecks.First(c => Product(c).Id != picked.Id);
            UiDrive.Click(window, other);
            UiShot.Settle(4);
            window.PrintPickedIds.Should().BeEquivalentTo(new[] { picked.Id, Product(other).Id });
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static MenuItem LabelItem(WarehouseWindow w, CatalogProductTileVm p) =>
        w.BuildRowMenu(p).Items.OfType<MenuItem>().Single(i => (Avalonia.Automation.AutomationProperties.GetName(i) ?? "") == WarehouseWindow.RowLabelText);

    [AvaloniaFact]
    public async Task PrintLabelMenuItem_OpensBarcodesTab_WithThisProduct_NotSilentPrint()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var checks = RowChecks(window);
            var product = Product(checks[0]);
            var tabs = UiDrive.Find<TabControl>(window, "WarehouseTabs");
            tabs.SelectedItem.Should().BeSameAs(UiDrive.Find<TabItem>(window, "ProductsTabItem"));

            LabelItem(window, product).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            UiShot.Settle(8);
            tabs.SelectedItem.Should().BeSameAs(window.LabelsTab, "открылась вкладка «Штрихкоды» — настройки и печать там");
            var view = window.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            view.SelectedCount.Should().Be(1);
            view.BuildItems().Should().ContainSingle().Which.Product.Id.Should().Be(product.Id);

            // несколько отмеченных галочкой — открываются все
            tabs.SelectedItem = UiDrive.Find<TabItem>(window, "ProductsTabItem");
            UiShot.Settle(4);
            // 2026-11-23: во вкладку «Штрихкоды» попадают только товары с печатным штрихкодом — отмечаем две такие строки (у остальных тестового каталога кода нет)
            var boxes = RowChecks(window).Where(c => BarcodePrintCatalog.HasPrintableCode(Product(c))).Take(2).ToList();
            boxes.Should().HaveCount(2, "в тестовом каталоге есть минимум два товара со штрихкодом");
            UiDrive.Click(window, boxes[0]);
            UiDrive.Click(window, boxes[1]);
            UiShot.Settle(4);
            window.LabelMenuTargets(Product(boxes[0])).Should().BeEquivalentTo(new[] { Product(boxes[0]).Id, Product(boxes[1]).Id });
            LabelItem(window, Product(boxes[0])).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            UiShot.Settle(8);
            tabs.SelectedItem.Should().BeSameAs(window.LabelsTab);
            window.GetVisualDescendants().OfType<BarcodePrintView>().Single().SelectedCount.Should().Be(2, "для нескольких отмеченных — Штрихкоды со всеми выбранными");
            // товар, не входящий в отмеченные, открывается один
            window.LabelMenuTargets(new CatalogProductTileVm("not-picked", "Другой", "1.00 сом", false)).Should().BeEquivalentTo(new[] { "not-picked" });
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
