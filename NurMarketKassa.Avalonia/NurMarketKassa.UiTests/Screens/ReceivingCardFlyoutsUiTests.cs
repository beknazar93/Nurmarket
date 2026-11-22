using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22 (владелец, «Склад → Приёмка», новый товар): (1) «в приёмке уже существующие категории не выходит» — у поля «Категория» теперь выпадающий список существующих категорий каталога с поиском по набору;
/// (2) «единица выбор … открывается и закрывает все внизу» — список единиц всплывает поверх карточки (Popup), высота карточек и положение следующей не меняются; закрывается по выбору, Esc и нажатию вне;
/// плитки единиц крупные (сенсор); (4) на кнопке «Срок годности и партия» после ввода видно, что задано. Поля карточки с новым полем-списком не налезают друг на друга (ru, ky, uz; 1024×600, 1366×768).</summary>
[Collection("UiScenarios")]
public sealed class ReceivingCardFlyoutsUiTests
{
    private static readonly string[] Categories = ["Канцтовары", "Каналы связи", "Напитки", "Тормозная система"];

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var (w, h) in new[] { (1024, 600), (1366, 768) })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [w, h, lang];
    }

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    private static void SeedCategories(WarehouseWindow window)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var list = (System.Collections.ObjectModel.ObservableCollection<string>)typeof(WarehouseWindow).GetField("_bulkCategories", flags)!.GetValue(window)!;
        list.Clear();
        foreach (var c in Categories)
            list.Add(c);
        typeof(WarehouseWindow).GetField("_bulkCategoriesLoadedAt", flags)!.SetValue(window, DateTime.UtcNow); // свежий список: фоновая загрузка не перетирает
    }

    private static (WarehouseWindow window, WarehouseViewModel vm, ReceivingLineVm fresh, ReceivingLineVm next) Open(int w, int h, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        using var _ = UiShot.Render(window, new UiSize(w, h));
        UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
        UiShot.Settle(10);
        var vm = (WarehouseViewModel)window.DataContext!;
        vm.ReceivingLines.Clear();
        var fresh = new ReceivingLineVm { ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
        var next = new ReceivingLineVm { Barcode = "4600000000019", ProductName = "Сок яблочный 1 л", Source = ReceivingSource.Warehouse, Quantity = 12, PurchasePrice = 64.5, SalePrice = 89, Unit = "шт" };
        vm.ReceivingLines.Add(fresh);
        vm.ReceivingLines.Add(next);
        SeedCategories(window);
        UiShot.Settle(12);
        return (window, vm, fresh, next);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task UnitList_FloatsOverTheCard_AndDoesNotMoveTheRowsBelow(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, _, fresh, next) = Open(w, h, lang);
        try
        {
            var card = RcvCards.Card(window, fresh);
            var picker = card.GetVisualDescendants().OfType<UnitPickerBox>().First();
            var heightBefore = card.Bounds.Height;
            var nextTopBefore = RectIn(RcvCards.Card(window, next), window).Top;

            picker.Open();
            UiShot.Settle(6);

            picker.IsOpen.Should().BeTrue("кнопка ▾ раскрывает список единиц");
            var popup = picker.GetVisualDescendants().OfType<Popup>().Single();
            popup.IsOpen.Should().BeTrue("список единиц — всплывающий (Popup), а не часть карточки");
            card.Bounds.Height.Should().BeApproximately(heightBefore, 0.5, "раскрытый список не раздвигает карточку");
            RectIn(RcvCards.Card(window, next), window).Top.Should().BeApproximately(nextTopBefore, 0.5, "строки ниже не сдвигаются и не закрываются");

            var chips = ((Control)popup.Child!).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            chips.Should().NotBeEmpty("плитки единиц показаны");
            chips.Should().OnlyContain(b => b.Bounds.Height >= 50 && b.Bounds.Width >= 60, "для сенсора плитки крупные (≥ 50 × 60)");

            // выбор плитки закрывает список и ставит единицу
            var kg = chips.First(b => Equals(b.Tag, "кг"));
            UiDrive.Click(window, kg);
            UiShot.Settle(4);
            picker.IsOpen.Should().BeFalse("выбор единицы закрывает список");
            popup.IsOpen.Should().BeFalse();
            fresh.Unit.Should().Be("кг");
            card.Bounds.Height.Should().BeApproximately(heightBefore, 0.5);

            // Esc закрывает без выбора
            picker.Open();
            UiShot.Settle(3);
            picker.InputBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = picker.InputBox });
            UiShot.Settle(3);
            picker.IsOpen.Should().BeFalse("Esc закрывает список единиц");
            popup.IsOpen.Should().BeFalse();
            fresh.Unit.Should().Be("кг", "Esc единицу не меняет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task CategoryList_ShowsExistingCategories_FiltersByTyping_AndPicks(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, _, fresh, next) = Open(w, h, lang);
        try
        {
            var card = RcvCards.Card(window, fresh);
            var category = card.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && Equals(t.Watermark, Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya")));
            var host = (Grid)category.GetVisualParent()!;
            var popup = host.Children.OfType<Popup>().Single();
            var arrow = host.Children.OfType<Button>().Single();
            var heightBefore = card.Bounds.Height;
            var nextTopBefore = RectIn(RcvCards.Card(window, next), window).Top;

            // кнопка ▾ — полный список существующих категорий
            UiDrive.Click(window, arrow);
            UiShot.Settle(6);
            popup.IsOpen.Should().BeTrue("▾ открывает выпадающий список категорий");
            Names(popup).Should().BeEquivalentTo(Categories, "показаны все существующие категории каталога");
            card.Bounds.Height.Should().BeApproximately(heightBefore, 0.5, "список поверх карточки, а не в ней");
            RectIn(RcvCards.Card(window, next), window).Top.Should().BeApproximately(nextTopBefore, 0.5);
            UiDrive.Click(window, arrow);
            UiShot.Settle(3);
            popup.IsOpen.Should().BeFalse("повторное нажатие ▾ закрывает список");

            // набор текста сужает список (поиск по вхождению)
            category.Focus();
            UiShot.Pump();
            window.KeyTextInput("кан");
            UiShot.Settle(6);
            popup.IsOpen.Should().BeTrue();
            Names(popup).Should().BeEquivalentTo(["Канцтовары", "Каналы связи"], "по набору «кан» остаются подходящие категории");

            // выбор строки списка записывает категорию в строку приёмки и закрывает список
            var pick = ((Control)popup.Child!).GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Канцтовары"));
            pick.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "строка списка — цель касания ≥ 44 px");
            UiDrive.Click(window, pick);
            UiShot.Settle(4);
            popup.IsOpen.Should().BeFalse("выбор закрывает список");
            fresh.CategoryName.Should().Be("Канцтовары");
            category.Text.Should().Be("Канцтовары");

            // Esc закрывает список, не трогая введённое
            category.Text = "";
            UiShot.Settle(3);
            if (!popup.IsOpen) // очистка поля с курсором сама открывает список (все категории)
            {
                UiDrive.Click(window, arrow);
                UiShot.Settle(4);
            }
            popup.IsOpen.Should().BeTrue();
            category.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = category });
            UiShot.Settle(3);
            popup.IsOpen.Should().BeFalse("Esc закрывает список категорий");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static List<string> Names(Popup popup) =>
        ((Control)popup.Child!).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string ?? "").ToList();

    [AvaloniaFact]
    public async Task CategoryList_IsLoadedFromTheCatalog_WhenTheLocalListIsEmpty()
    {
        // Раньше список категорий грузила вкладка «Массовое добавление», которой больше нет, — поле оставалось без подсказок. Теперь список берётся из каталога кассы при первом обращении.
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var added = new CatalogProductTileVm("cat-test-1", "Тестовый товар с категорией", "10 сом", false) { Category = "Категория-из-каталога-Ж", Quantity = 1 };
        CatalogCacheService.Products.Add(added);
        try
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var list = (System.Collections.ObjectModel.ObservableCollection<string>)typeof(WarehouseWindow).GetField("_bulkCategories", flags)!.GetValue(window)!;
            list.Clear();
            await (Task)typeof(WarehouseWindow).GetMethod("LoadBulkCategoriesAsync", flags)!.Invoke(window, null)!;
            list.Should().Contain("Категория-из-каталога-Ж", "категория товара из каталога кассы попадает в выпадающий список");
        }
        finally
        {
            CatalogCacheService.Products.Remove(added);
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void BatchButtonLabel_ShowsWhatIsSet_OtherwiseTheUsualName()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var line = new ReceivingLineVm { ProductName = "Йогурт", Source = ReceivingSource.Unknown };
        WarehouseWindow.BatchButtonLabel(line).Should().Be(WarehouseWindow.BatchButtonText, "ничего не задано — обычное название кнопки");
        line.ExpiresAt = new DateTime(2026, 12, 1);
        line.LotNo = "A-12";
        WarehouseWindow.BatchButtonLabel(line).Should().Be("Срок до 01.12.2026 · партия A-12");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task NewProductCard_FieldsDoNotOverlap_WithTheCategoryList(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, _, fresh, _) = Open(w, h, lang);
        try
        {
            var card = RcvCards.Card(window, fresh);
            var cr = RectIn(card, window);
            var failures = new List<string>();
            var fields = card.GetVisualDescendants().OfType<Control>()
                .Where(c => (c is TextBox || c is Button) && c.IsEffectivelyVisible && c.Bounds.Width > 0 && !c.GetVisualAncestors().OfType<TextBox>().Any()).ToList();
            for (var i = 0; i < fields.Count; i++)
            {
                var a = RectIn(fields[i], window);
                if (a.Right > cr.Right + 1 || a.Left < cr.Left - 1)
                    failures.Add($"{Label(fields[i])} выходит за карточку по ширине");
                if (fields[i] is TextBox && a.Width < 48)
                    failures.Add($"{Label(fields[i])} уже 48 px");
                for (var j = i + 1; j < fields.Count; j++)
                {
                    if (fields[i].GetVisualAncestors().Contains(fields[j]) || fields[j].GetVisualAncestors().Contains(fields[i]))
                        continue;
                    var inter = a.Intersect(RectIn(fields[j], window));
                    if (inter.Width > 2 && inter.Height > 2)
                        failures.Add($"{Label(fields[i])} и {Label(fields[j])} налезают друг на друга ({inter.Width:0}×{inter.Height:0})");
                }
            }
            failures.Should().BeEmpty("поля карточки нового товара (с выпадающим списком категорий) не налезают друг на друга и не выходят за карточку");

            // и с открытыми списками (единицы и категории) карточка по-прежнему целая, а списки не вылезают за окно
            card.GetVisualDescendants().OfType<UnitPickerBox>().First().Open();
            UiShot.Settle(5);
            var popups = card.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen && p.Child is Control).ToList();
            popups.Should().NotBeEmpty();
            foreach (var p in popups)
                if (((Control)p.Child!).TranslatePoint(default, window) is { } at)
                {
                    at.X.Should().BeGreaterThanOrEqualTo(-1, "список не уходит за левый край окна");
                    (at.X + ((Control)p.Child!).Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 1, "список не уходит за правый край окна");
                }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static string Label(Control c) => c switch
    {
        TextBox t => $"поле «{t.Watermark}»",
        Button b => $"кнопка «{b.Content}»",
        _ => c.GetType().Name,
    };
}
