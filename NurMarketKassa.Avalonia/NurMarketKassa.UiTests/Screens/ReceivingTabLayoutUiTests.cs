using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (владелец прислал два фото «Склад → Приёмка»: «неудобства и баг дизайна»): панели действий не обрезаются краем окна, строка таблицы — название и штрихкод аккуратной
/// колонкой без наезда, «Принимаем» с подписью «упак.» целиком, числа выровнены, подсказка способа приёмки и итоги читаются, статус «Черновик сохранён» не обрезан.
/// Размеры 1024×600, 1366×768, 1024×768, 1024×1024; языки ru, ky, uz; темы светлая, тёмная, сенсорная. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\rcv-tab-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingTabLayoutUiTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var (w, h) in new[] { (1024, 600), (1366, 768), (1024, 768), (1024, 1024) })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [w, h, lang, UiTheme.Light];
        foreach (var theme in new[] { UiTheme.Dark, UiTheme.Touch })
        foreach (var (w, h) in new[] { (1024, 600), (1366, 768) })
            yield return [w, h, AppLanguage.Russian, theme];
    }

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Receiving_TabAndRow_AreNotClippedOrOverlapped(int w, int h, AppLanguage lang, UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{w}x{h} {lang} {theme}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(w, h));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(12);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            vm.ReceivingLines.Insert(0, new ReceivingLineVm { Barcode = "4870202521254", ProductName = "maxi Чай", Source = ReceivingSource.Unknown, Quantity = 1 });
            vm.ReceivingLines.Insert(0, new ReceivingLineVm { Barcode = "4600000000019", ProductName = "Сок яблочный 1 л", Source = ReceivingSource.Warehouse, Quantity = 12, PurchasePrice = 64.5, SalePrice = 89, Unit = "шт" });
            UiShot.Settle(14);
            SaveShot(window, $"rcv-tab-{theme.ToString().ToLowerInvariant()}-{lang}-{w}x{h}");

            var failures = new List<string>();
            var tab = UiDrive.Find<Border>(window, "ReceivingTabBorder");
            var area = RectIn(tab, window);
            foreach (var name in new[] { "ReceivingScanBox", "ReceivingQuantityBox", "ReceivingClearButton", "ReceivingMarkupButton", "ReceivingInvoiceImportButton", "ReceivingSupplierButton", "ReceivingCommitButton" })
            {
                var c = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == name);
                if (c is null || !c.IsEffectivelyVisible)
                    continue;
                var r = RectIn(c, window);
                if (r.Right > window.Bounds.Width + 1 || r.Right > area.Right + 1)
                    failures.Add($"{name} выходит за край: справа {r.Right:0} при {Math.Min(window.Bounds.Width, area.Right):0}");
            }
            // карточки строк: по одной на строку приёмки, поля одной линии не перекрываются и не выходят за карточку и окно
            var cards = window.GetVisualDescendants().OfType<Border>().Where(x => x.DataContext is BulkScanRow && x.GetVisualParent()?.DataContext is not BulkScanRow && x.IsEffectivelyVisible).ToList(); // только внешняя рамка карточки (вложенные рамки наследуют DataContext)
            if (cards.Count != vm.ReceivingLines.Count)
                failures.Add($"карточек {cards.Count}, строк приёмки {vm.ReceivingLines.Count}");
            foreach (var card in cards)
            {
                var cr = RectIn(card, window);
                if (cr.Right > window.Bounds.Width + 1)
                    failures.Add($"карточка выходит за окно: {cr.Right:0} > {window.Bounds.Width:0}");
                var inputs = card.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0).ToList();
                for (var i = 0; i < inputs.Count; i++)
                {
                    var r1 = RectIn(inputs[i], window);
                    if (r1.Right > cr.Right + 1 || r1.Left < cr.Left - 1)
                        failures.Add($"поле «{inputs[i].Watermark}» выходит за карточку");
                    if (inputs[i].Bounds.Width < 48)
                        failures.Add($"поле «{inputs[i].Watermark}» уже 48 px");
                    for (var j = i + 1; j < inputs.Count; j++)
                    {
                        var inter = r1.Intersect(RectIn(inputs[j], window));
                        if (inter.Width > 2 && inter.Height > 2)
                            failures.Add($"поля «{inputs[i].Watermark}» и «{inputs[j].Watermark}» налезают друг на друга");
                    }
                }
                foreach (var btn in card.GetVisualDescendants().OfType<Button>().Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 0))
                    if (btn.Bounds.Height < 40 - 0.5)
                        failures.Add($"кнопка {btn.GetType().Name}#{btn.Name} «{btn.Content}» ниже 40 px ({btn.Bounds.Height:0}), родитель {btn.GetVisualParent()?.GetType().Name}, выше {btn.GetVisualAncestors().Skip(1).Take(3).Select(x => x.GetType().Name).Aggregate((x, y) => x + '>' + y)}");
            }
            TextClipChecker.Find(window, "warehouse-receiving", lang.ToString()).Where(v => v.Detail.Contains("ReceivingCards") || v.Detail.Contains("ReceivingTabBorder")).Select(v => v.Detail).ToList().ForEach(failures.Add);
            // вкладки: нет «Массового добавления» и «Редактора этикетки»; ряд не обрезан справа
            var tabsCtl = UiDrive.Find<TabControl>(window, "WarehouseTabs");
            var headers = tabsCtl.Items.OfType<TabItem>().Where(t => t.IsVisible).Select(t => t.Header is StackPanel sp ? string.Join(" ", sp.Children.OfType<TextBlock>().Select(x => x.Text)) : t.Header?.ToString() ?? "").ToList();
            if (headers.Any(x => x.Contains("Массовое") || x.Contains("Массалык") || x.Contains("Редактор этикетки")))
                failures.Add("в ряду вкладок осталась лишняя вкладка: " + string.Join(" | ", headers));
            foreach (var t in tabsCtl.Items.OfType<TabItem>().Where(t => t.IsVisible))
                if (RectIn(t, window).Right > window.Bounds.Width + 1)
                    failures.Add($"вкладка «{t.Header}» за краем окна");
            failures.Should().BeEmpty(tag);
        }
        finally
        {
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    private static TextBox CardField(Window window, ReceivingLineVm line, string tag) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is BulkScanRow r && ReferenceEquals(r.Line, line) && b.GetVisualParent()?.DataContext is not BulkScanRow)
            .GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, tag) && t.IsEffectivelyVisible);

    /// <summary>2026-11-21: «Приёмка» после объединения с «Массовым добавлением»: карточки — те же строки приёмки; ввод в поля карточки доходит до строки, «+» прибавляет как повторный скан,
    /// «Оприходовать» запускает прежний путь проведения (проверка цен продажи, поставщик, фон) — суммы, партии и единицы не менялись.</summary>
    [AvaloniaFact]
    public async Task Cards_EditAndCommit_GoThroughTheSameReceivingPipeline()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(10);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var known = new ReceivingLineVm { Barcode = "4600000000019", ProductName = "Сок яблочный 1 л", Source = ReceivingSource.Warehouse, Quantity = 1, PurchasePrice = 60, SalePrice = 80, Unit = "шт" };
            var fresh = new ReceivingLineVm { Barcode = "4870202521254", ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Insert(0, known);
            vm.ReceivingLines.Insert(0, fresh);
            UiShot.Settle(12);

            // известный товар: название только для чтения, «+» прибавляет единицу
            CardField(window, known, "name").IsReadOnly.Should().BeFalse("название товара склада правится (переименование при проведении)");
            var plus = window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is BulkScanRow r && ReferenceEquals(r.Line, known) && b.GetVisualParent()?.DataContext is not BulkScanRow)
                .GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "+");
            UiDrive.Click(window, plus);
            UiShot.Settle(6);
            known.Quantity.Should().Be(2, "«+» — как повторный скан: +1 к количеству");

            // неизвестный штрихкод: название и цены вписываются в карточку и доходят до строки
            var name = CardField(window, fresh, "name");
            name.IsReadOnly.Should().BeFalse("у нового товара название вписывается");
            name.Text = "Чай новый";
            CardField(window, fresh, "qty").Text = "5";
            CardField(window, fresh, "purchase").Text = "40";
            UiShot.Settle(6);
            fresh.ProductName.Should().Be("Чай новый");
            fresh.Quantity.Should().Be(5);
            fresh.PurchasePrice.Should().Be(40);

            // «Оприходовать»: прежний путь проверки — у новой строки нет цены продажи, касса спрашивает (до записи на сервер)
            IReadOnlyList<ReceivingLineVm>? asked = null;
            vm.ConfirmMissingSalePrices = lines =>
            {
                asked = lines;
                return Task.FromResult(false);
            };
            UiDrive.Click(window, UiDrive.Find<Button>(window, "ReceivingCommitButton"));
            UiShot.Settle(20);
            asked.Should().NotBeNull("проведение через карточки дошло до прежней проверки цен продажи");
            asked!.Should().Contain(fresh);
            vm.ReceivingLines.Should().HaveCount(2, "отказ — строки на месте, ничего не записано");
        }
        finally
        {
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }
}
