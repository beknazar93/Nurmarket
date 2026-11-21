using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 («Неудобства от владельца», п. 10–11): склад программы владельца — действия строки «Изменить» + «⋯», выбор галочками и панель массовых
/// действий, вкладки-чипы, пустые состояния; вкладка «Штрихкоды» — настройка ценника. Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\wh1119-*.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseDesign1119UiTests
{
    private const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;

    [AvaloniaTheory]
    [InlineData(UiTheme.Dark, 1366, 768, true)]
    [InlineData(UiTheme.Light, 1366, 768, false)]
    [InlineData(UiTheme.Light, 1024, 600, false)]
    [InlineData(UiTheme.Dark, 1920, 1080, false)]
    public async Task OwnerWarehouse_Shots(UiTheme theme, int w, int h, bool allTabs)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        var shell = new OwnerShellWindow { Width = w, Height = h };
        shell.Show();
        UiShot.Settle(3, 30);
        WarehouseWindow? wh = null;
        var unlocked = NurMarketKassa.Services.UserPreferences.Instance.BulkPriceTagUnlocked;
        NurMarketKassa.Services.UserPreferences.Instance.BulkPriceTagUnlocked = true; // вкладка «Штрихкоды» — с содержимым, а не с подсказкой о покупке
        try
        {
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Inst, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(8, 30);
            var tabs = UiDrive.Find<TabControl>(wh!, "WarehouseTabs");
            tabs.SelectedItem = UiDrive.Find<TabItem>(wh!, "ProductsTabItem");
            UiShot.Settle(6, 30);
            var tag = $"{(theme == UiTheme.Dark ? "dark" : "light")}-{w}x{h}";
            Shot(wh!, $"wh1119-products-{tag}");
            ChecksProducts(wh!, tag);
            if (!allTabs)
                return;
            var i = 0;
            foreach (var item in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
            {
                tabs.SelectedItem = item;
                UiShot.Settle(6, 30);
                Shot(wh!, $"wh1119-tab{i++}-{tag}");
            }
        }
        finally
        {
            NurMarketKassa.Services.UserPreferences.Instance.BulkPriceTagUnlocked = unlocked;
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    private static void ChecksProducts(WarehouseWindow wh, string tag)
    {
        var grid = UiDrive.Find<DataGrid>(wh, "ProductsGrid");
        grid.IsVisible.Should().BeTrue(tag);
        var vm = (NurMarketKassa.AvaloniaHost.ViewModels.WarehouseViewModel)wh.DataContext!;
        vm.PagedProducts.Count.Should().BeGreaterThan(0, $"{tag}: в тестовом каталоге есть товары");

        // В строке — ровно два действия («Изменить» и «⋯»), оба целиком внутри таблицы; остаток — кнопка-плашка.
        var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible).ToList();
        rows.Should().NotBeEmpty(tag);
        var right = grid.TranslatePoint(new Avalonia.Point(grid.Bounds.Width, 0), wh)!.Value.X;
        foreach (var row in rows)
        {
            var acts = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("row-act") && b.IsEffectivelyVisible).ToList();
            acts.Should().HaveCount(2, $"{tag}: действия строки — «Изменить» и «⋯»");
            foreach (var b in acts)
                b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), wh)!.Value.X.Should().BeLessThanOrEqualTo(right + 0.5, $"{tag}: кнопка строки не обрезана справа");
            row.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("stock-btn")).Should().Be(1, $"{tag}: остаток — кнопка");
        }

        // 2026-11-21 (владелец: «все редакторы — в одну вкладку»): выбора галочками и панели массовых действий больше нет — печать только на вкладке «Ценники и штрихкоды».
        wh.FindControl<Control>("BulkBar").Should().BeNull($"{tag}: панели массовых действий нет");
        rows.SelectMany(r => r.GetVisualDescendants().OfType<CheckBox>()).Should().BeEmpty($"{tag}: галочек «в печать» в строках нет");

        // Пустое состояние: ничего не найдено — блок с кнопкой «Сбросить поиск и фильтры».
        vm.ProductSearchText = "zzqq-нет-такого-товара";
        for (var i = 0; i < 40 && vm.PagedProducts.Count > 0; i++)
            UiShot.Settle(1, 30);
        UiShot.Settle(3, 30);
        var empty = UiDrive.Find<ContentControl>(wh, "ProductsEmptyHint");
        empty.IsVisible.Should().BeTrue($"{tag}: пустое состояние показано");
        empty.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible).Should().BeGreaterThan(0, $"{tag}: у пустого состояния есть действие");
        var gridBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), wh)!.Value.Y;
        foreach (var b in empty.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            b.TranslatePoint(new Point(0, b.Bounds.Height), wh)!.Value.Y.Should().BeLessThanOrEqualTo(gridBottom + 0.5, $"{tag}: кнопка пустого состояния не уходит под край таблицы");
        Shot(wh, $"wh1119-empty-{tag}");
        vm.ProductSearchText = "";
        UiShot.Settle(4, 30);
    }

    private static void Shot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }
}
