using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (жалоба владельца с фото: окно «Печать на A4» и поверх общий диалог «Произошла ошибка в программе»). Перебор всего, что можно
/// сделать в окне: каждое числовое поле — стрелки вверх/вниз на границах и ввод с клавиатуры (пусто, 0, минус, запятая/точка, огромное число, буквы),
/// бумага (все форматы и «Свой формат»), ориентация, ползунок высоты штрихкода, листание, «Сбросить к общему», копии по позициям, 3 и 1000 товаров.
/// Необработанное исключение валит тест (в программе это и есть общий диалог ошибки).</summary>
[Collection("UiScenarios")]
public sealed class A4SheetWindowStressUiTests
{
    public A4SheetWindowStressUiTests() => TestHost.Ensure();

    private static readonly string[] Inputs = { "", "0", "-5", "1,5", "1.5", "abc", "99999999999999999999999999999999", "400", "401", "-0", ",", "1e5", " 7 " };

    private static List<BarcodePrintItem> Items(int n) => Enumerable.Range(0, n)
        .Select(i => new BarcodePrintItem(new CatalogProductTileVm("a4-" + i, i switch { 0 => "CIF Крем чистящий 500мл", 1 => "COMET универсальный 475г", 2 => "Corona ice Лимон 1л", _ => "Товар " + i }, (75 + i) + ".00 сом", false)
        { Barcode = (4600000000000L + i).ToString(), Unit = "шт", Quantity = 5 }, (4600000000000L + i).ToString(), 1))
        .ToList();

    private static void Press(Window w, Key key)
    {
        w.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        w.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        UiShot.Pump();
    }

    private static void Spin(NumericUpDown n, bool up, int times)
    {
        var name = up ? "PART_IncreaseButton" : "PART_DecreaseButton";
        var b = n.GetVisualDescendants().OfType<RepeatButton>().FirstOrDefault(x => x.Name == name);
        for (var i = 0; i < times && b is not null; i++)
        {
            if (b.IsEffectivelyEnabled)
                b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Pump();
        }
    }

    private static int HammerNumbers(Window w)
    {
        var done = 0;
        foreach (var n in w.GetVisualDescendants().OfType<NumericUpDown>().ToList())
        {
            if (!n.IsEffectivelyVisible || !n.IsEffectivelyEnabled)
                continue;
            Spin(n, up: false, times: 3);
            Spin(n, up: true, times: 3);
            n.Value = n.Minimum;
            UiShot.Pump();
            Spin(n, up: false, times: 2); // «вниз» на минимуме (на фото — «Начать с ячейки №» = 1)
            n.Value = n.Maximum;
            UiShot.Pump();
            Spin(n, up: true, times: 2);
            var box = n.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (box is null)
                continue;
            foreach (var text in Inputs)
            {
                UiDrive.Type(w, box, text);
                w.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && !ReferenceEquals(t, box)).Focus(); // уход фокуса — значение применяется
                Spin(n, up: false, times: 1);
                w.IsVisible.Should().BeTrue("окно не закрылось");
                UiShot.Settle(1, 20);
            }
            done++;
            n.Value = null; // поле очищено
            UiShot.Pump();
            Spin(n, up: false, times: 1);
            Spin(n, up: true, times: 1);
        }
        return done;
    }

    [AvaloniaFact] public Task Three_Products() => Run(3, false);
    [AvaloniaFact] public Task Three_Products_DesignerTemplate() => Run(3, true);
    [AvaloniaFact] public Task Thousand_Products() => Run(1000, false);

    private static async Task Run(int count, bool designerTemplate)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var savedTemplate = LabelTemplateStore.Load();
        if (designerTemplate)
            LabelTemplateStore.Save(LabelDesignPresets.ById("shelf")!.Build());
        var items = Items(count);
        var settings = new BarcodePrintSettings { A4 = new A4SheetOptions { Columns = 3, Rows = 6, BarcodeHeightPx = 83 } };
        var perItem = items.Select((_, i) => i == 1 ? (int?)150 : i == 2 ? 0 : null).ToList(); // копии позиций из главного окна: сверх 99 и 0
        var window = new BarcodeA4SheetWindow(items, perItem, settings, "Магазин");
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 860))) { }
            UiShot.Settle(6);

            HammerNumbers(window).Should().BeGreaterThan(8, "перебраны числовые поля окна");

            // Список «Копии по позициям»: прокрутка туда-обратно (виртуализация пересоздаёт строки) и быстрые «вниз» у строк на значении 1.
            var positions = window.GetVisualDescendants().OfType<ListBox>().First();
            for (var k = 0; k < 3; k++)
            {
                positions.ScrollIntoView(count - 1);
                UiShot.Settle(1, 20);
                positions.ScrollIntoView(0);
                UiShot.Settle(1, 20);
            }
            foreach (var rowBox in positions.GetVisualDescendants().OfType<NumericUpDown>().ToList())
            {
                rowBox.Value = 1;
                Spin(rowBox, up: false, times: 5);
                Spin(rowBox, up: true, times: 2);
                Spin(rowBox, up: false, times: 5);
            }

            var slider = window.GetVisualDescendants().OfType<Slider>().First();
            foreach (var v in new[] { slider.Minimum, slider.Maximum, 83.0, -5, 10000 })
            {
                slider.Value = v;
                UiShot.Settle(1, 20);
            }

            var combos = window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.Items.Count > 1).ToList();
            foreach (var c in combos)
                for (var i = 0; i < c.Items.Count; i++)
                {
                    c.SelectedIndex = i;
                    UiShot.Settle(1, 20);
                    if (c.Items.Count > 5 && i == c.Items.Count - 1) // бумага: «Свой формат» — его поля (поля, ячейка, шаг) тоже перебираем
                        HammerNumbers(window);
                }

            foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled).ToList())
            {
                var t = UiDrive.LabelOf(b) ?? "";
                if (t is "‹" or "›" || t.StartsWith("Сбросить", StringComparison.Ordinal))
                    for (var i = 0; i < 4; i++)
                    {
                        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        UiShot.Settle(1, 20);
                    }
            }
            // Галочки (Штрих-код, Название, Цена, Скидка, ПЛУ, Описание, Рамка, Пунктир) и Ж/К/П — туда и обратно.
            foreach (var t in window.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled).ToList())
            {
                t.IsChecked = !(t.IsChecked == true);
                UiShot.Pump();
                t.IsChecked = !(t.IsChecked == true);
                UiShot.Pump();
            }
            UiShot.Settle(10);
            window.Options.StartCell.Should().BeInRange(1, 400);
            window.Options.Columns.Should().BeInRange(1, 10);
            window.Options.Rows.Should().BeInRange(1, 30);
        }
        finally
        {
            window.Close();
            UiShot.Settle(4);
            LabelTemplateStore.Save(savedTemplate);
        }
    }
}
