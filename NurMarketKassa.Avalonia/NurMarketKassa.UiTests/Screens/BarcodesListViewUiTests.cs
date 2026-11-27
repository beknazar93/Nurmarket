using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22 (владелец: «в штрих-кодах добавь отображение товаров в виде списка тоже»): «Склад → Ценники и штрихкоды → Печать» — переключатель «Список / Плитки».
/// Проверки: переключение видов (поиск, фильтры «Все / С кодом / Без кода», «На странице / Весь список», галочки «В печать», копии и очистка поля поиска после выбора одинаковы в обоих видах,
/// выбор не теряется при переключении); выбранный вид запоминается (UserPreferences.BarcodePrintListView, по умолчанию — плитки, как раньше); 15 000 товаров — быстрый показ и фильтр, строки
/// виртуализированы и при прокрутке показывают нужные товары; размеры 1024×600 и 1366×768 (список заполняет высоту, не выходит за окно, строки и кнопки не ниже 44 px); языки ru / ky / uz;
/// поля формы (поле «Копий» в строке, поиск, список категорий) не налезают друг на друга и на подписи (та же проверка, что FormFieldsOverlapUiTests).</summary>
[Collection("UiScenarios")]
public sealed class BarcodesListViewUiTests : IDisposable
{
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private readonly bool _savedList = UserPreferences.Instance.BarcodePrintListView;
    private List<CatalogProductTileVm>? _savedProducts;

    public BarcodesListViewUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        UserPreferences.Instance.BarcodePrintListView = _savedList;
        if (_savedProducts != null)
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(_savedProducts);
        }
    }

    private void Seed(int count)
    {
        _savedProducts ??= CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        var list = new List<CatalogProductTileVm>(count);
        for (var i = 0; i < count; i++)
            list.Add(new CatalogProductTileVm("bl" + i, $"Товар номер {i} для проверки списка штрихкодов", (10 + i % 900) + ".50 сом", false)
            {
                Barcode = i % 10 == 9 ? null : (4600000000000L + i).ToString(),
                Article = "A" + i,
                Unit = "шт",
                Quantity = i % 50,
                Category = "Категория " + i % 40,
            });
        CatalogCacheService.Products.AddRange(list);
    }

    /// <summary>Ждать, пока раскладка и шаблоны применятся (с паузами: список строится лениво при первом включении).</summary>
    private static void Wait(int rounds = 4) => UiShot.Settle(rounds, 30);

    private static void Pump(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
            UiShot.Pump();
    }

    private static List<Border> Rows(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.GetType().Name == "ListRow" && b.IsEffectivelyVisible && b.Bounds.Height > 0).OrderBy(b => b.TranslatePoint(default, root)?.Y ?? 0).ToList();

    private static ItemsControl ListItems(Visual root) => Rows(root).First().GetVisualAncestors().OfType<ItemsControl>().First();

    private static string RowName(Border row) => row.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 13.5 && t.FontWeight == FontWeight.SemiBold).Text ?? "";

    private static int RowIndex(Border row)
    {
        var name = RowName(row); // «Товар номер 123 для …»
        return int.Parse(name.Split(' ')[2]);
    }

    private static Button Btn(Visual root, params string[] labels) =>
        root.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && (UiDrive.LabelOf(b) is { } t && labels.Contains(t) || labels.Contains(Avalonia.Automation.AutomationProperties.GetName(b))));

    private static (string List, string Tiles) ToggleLabels(AppLanguage lang) => lang switch
    {
        AppLanguage.Kyrgyz => ("Тизме", "Плиткалар"),
        AppLanguage.Uzbek => ("Ro'yxat", "Plitkalar"),
        _ => ("Список", "Плитки"),
    };

    private static ScrollViewer ListScroll(Visual root) => root.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is ItemsPresenter && s.IsEffectivelyVisible);

    private static ScrollViewer CardsScroll(Visual root) => root.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is WrapPanel);

    private static List<Border> Cards(Visual root) => ((WrapPanel)CardsScroll(root).Content!).Children.OfType<Border>().ToList();

    // ------------------------------------------------------------------ переключение видов

    [AvaloniaFact]
    public async Task Toggle_ListAndTiles_SearchFiltersSelectionAndCopiesAreTheSame()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(300);
        UserPreferences.Instance.BarcodePrintListView = false;
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            window.Show();
            UiShot.Settle(4, 30);
            view.ListMode.Should().BeFalse("по умолчанию — плитки, как раньше");
            CardsScroll(view).IsVisible.Should().BeTrue();
            Cards(view).Should().NotBeEmpty();

            // фильтры и поиск: числа найденных в плитках
            var cardsCounts = new Dictionary<string, int>();
            foreach (var (name, labels) in new[] { ("all", new[] { "Все" }), ("with", new[] { "Со штрихкодом", "С кодом" }), ("without", new[] { "Без штрихкода", "Без кода" }) })
            {
                UiDrive.Click(window, Btn(view, labels));
                Wait(3);
                cardsCounts[name] = view.FoundCount;
                view.SearchText = "номер 1";
                cardsCounts[name + "+q"] = view.FoundCount;
                view.SearchText = "";
            }
            cardsCounts["with"].Should().BeLessThan(cardsCounts["all"]);
            cardsCounts["without"].Should().Be(30);

            // включаем список кнопкой «Список»
            UiDrive.Click(window, Btn(view, "Список"));
            Wait(4);
            view.ListMode.Should().BeTrue();
            CardsScroll(view).IsVisible.Should().BeFalse("плитки скрыты");
            ListScroll(view).IsVisible.Should().BeTrue();
            UserPreferences.Instance.BarcodePrintListView.Should().BeTrue("выбор вида запомнен");

            foreach (var (name, labels) in new[] { ("all", new[] { "Все" }), ("with", new[] { "Со штрихкодом", "С кодом" }), ("without", new[] { "Без штрихкода", "Без кода" }) })
            {
                UiDrive.Click(window, Btn(view, labels));
                Wait(3);
                view.FoundCount.Should().Be(cardsCounts[name], $"фильтр «{name}» в списке находит столько же, сколько в плитках");
                view.SearchText = "номер 1";
                view.FoundCount.Should().Be(cardsCounts[name + "+q"], $"поиск при фильтре «{name}» в списке находит столько же");
                Wait(3);
                Rows(view).Count.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(view.FoundCount);
                view.SearchText = "";
            }
            UiDrive.Click(window, Btn(view, "Все"));
            Wait(3);
            view.FoundCount.Should().Be(300);

            // «На странице» в списке = весь показанный список; «Весь список» — то же число; «Снять» очищает
            UiDrive.Click(window, Btn(view, "На странице", "Выбрать все на странице"));
            Wait(3);
            var printable = CatalogCacheService.Products.Count(p => !string.IsNullOrWhiteSpace(p.Barcode));
            view.SelectedCount.Should().Be(printable, "выбраны все товары со штрихкодом (без кода выбрать нельзя)");
            Rows(view).Where(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsEnabled).All(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsChecked == true).Should().BeTrue("галочки в строках стоят");
            UiDrive.AllText(window).Should().Contain($"Товаров: {printable}");
            UiDrive.Click(window, Btn(view, "Снять", "Снять выбор"));
            Wait(3);
            view.SelectedCount.Should().Be(0);
            Rows(view).All(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsChecked != true).Should().BeTrue("галочки сняты");
            UiDrive.Click(window, Btn(view, "Весь список (300)", "Выбрать все в списке (300)"));
            Wait(3);
            view.SelectedCount.Should().Be(printable);
            view.ClearSelection();
            Wait(2);

            // поиск, галочка «В печать» → поле поиска очищается, строка остаётся отмеченной (как в плитках)
            view.SearchText = "номер 55 ";
            Wait(3);
            view.FoundCount.Should().BeGreaterThan(0);
            var row = Rows(view).First(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsEnabled);
            var rowIndex = RowIndex(row);
            UiDrive.Click(window, row.GetVisualDescendants().OfType<CheckBox>().First());
            Wait(3);
            view.SelectedCount.Should().Be(1);
            view.SearchText.Should().BeEmpty("после выбора найденного товара поле поиска очищено");
            Rows(view).Single(r => RowIndex(r) == rowIndex).GetVisualDescendants().OfType<CheckBox>().First().IsChecked.Should().BeTrue("отмеченная строка остаётся на виду");

            // копии: поле «Копий» в строке становится настоящим, значение попадает в печать и переживает переключение на плитки
            var copies = Rows(view).Single(r => RowIndex(r) == rowIndex).GetVisualDescendants().OfType<NumericUpDown>().SingleOrDefault();
            copies.Should().NotBeNull("у отмеченной строки — настоящее поле «Копий»");
            copies!.Value = 3;
            Wait(2);
            view.BuildItems().Single().Copies.Should().Be(3);
            UiDrive.AllText(window).Should().Contain("этикеток: 3");

            // обратно на плитки: выбор и копии на месте, поиск/фильтр прежние
            view.SearchText = "";
            Wait(3);
            UiDrive.Click(window, Btn(view, "Плитки"));
            Wait(4);
            view.ListMode.Should().BeFalse();
            // 2026-11-23: в плитках список скрыт — прежняя проверка «ListScroll не null» падала (ищет только видимый список); теперь: список скрыт, плитки видны
            view.GetVisualDescendants().OfType<ScrollViewer>().Any(sc => sc.Content is ItemsPresenter && sc.IsEffectivelyVisible).Should().BeFalse("список скрыт");
            CardsScroll(view).IsVisible.Should().BeTrue();
            UserPreferences.Instance.BarcodePrintListView.Should().BeFalse();
            view.SelectedCount.Should().Be(1, "выбор не потерян при переключении");
            view.BuildItems().Single().Copies.Should().Be(3, "копии не потеряны");
            view.FoundCount.Should().Be(300);
            // выбранный товар — в первых плитках, только если он на первой странице (номер ≤ 48 не гарантирует) — ищем его поиском и смотрим карточку
            view.SearchText = $"номер {rowIndex} ";
            Wait(4);
            var card = Cards(view).First(c => c.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith($"Товар номер {rowIndex} ", StringComparison.Ordinal)));
            card.GetVisualDescendants().OfType<CheckBox>().First().IsChecked.Should().BeTrue("в плитках товар остался отмеченным");
            card.GetVisualDescendants().OfType<NumericUpDown>().Single().Value.Should().Be(3);
            // и в плитках галочка очищает поиск
            view.ClearSelection();
            Wait(2);
            // 2026-11-23: после снятия выбора страница перерисована — прежняя карточка отсоединена от окна; берём новую
            card = Cards(view).First(c => c.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith($"Товар номер {rowIndex} ", StringComparison.Ordinal)));
            UiDrive.Click(window, card.GetVisualDescendants().OfType<CheckBox>().First());
            Wait(3);
            view.SearchText.Should().BeEmpty("в плитках поле поиска тоже очищается после выбора");
            view.SelectedCount.Should().Be(1);

            // и снова список: тот же выбор видно в строках
            UiDrive.Click(window, Btn(view, "Список"));
            Wait(4);
            view.SelectedCount.Should().Be(1);
            view.SearchText = $"номер {rowIndex} ";
            Wait(4);
            Rows(view).Single(r => RowIndex(r) == rowIndex).GetVisualDescendants().OfType<CheckBox>().First().IsChecked.Should().BeTrue("после возврата в список строка отмечена");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ListView_RowButtons_PrintOneAndCreateBarcode_Work()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(40);
        UserPreferences.Instance.BarcodePrintListView = true;
        var printed = new List<LabelPrintRequest>();
        var savedThermal = BarcodePrintView.ThermalPrintOverride;
        var savedConfirm = BarcodePrintView.ConfirmOverride;
        var savedSave = BarcodePrintView.BarcodeSaveOverride;
        var savedPrinters = BarcodePrintView.PrinterListOverride;
        var savedPrinterPath = UserPreferences.Instance.LabelPrinterDevicePath;
        UserPreferences.Instance.LabelPrinterDevicePath = "TEST-PRN"; // сохранённый принтер этикеток = тестовый, окно выбирает его само
        BarcodePrintView.ThermalPrintOverride = r => { printed.Add(r); return LabelPrintResult.Success; };
        BarcodePrintView.ConfirmOverride = _ => Task.FromResult(true);
        BarcodePrintView.BarcodeSaveOverride = (_, _) => Task.FromResult<string?>(null);
        BarcodePrintView.PrinterListOverride = () => [new DiscoveredPrinter("Тестовый принтер", "TEST-PRN")];
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            window.Show();
            UiShot.Settle(6, 40);
            view.ListMode.Should().BeTrue("вид списка взят из запомненного выбора");
            // «Распечатать штрих-код» в строке — печать одного товара
            var row = Rows(view).First(r => r.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Распечатать штрих-код"));
            var print = row.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Распечатать штрих-код");
            print.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "на сенсоре кнопка не ниже 44 px");
            UiDrive.Click(window, print);
            UiShot.Settle(6, 40);
            printed.Should().HaveCount(1, "кнопка в строке печатает один товар");
            printed[0].ProductName.Should().Be(RowName(row));
            // «＋» у товара без штрихкода (каждый десятый) — создание кода
            var noCode = Rows(view).First(r => r.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Создать штрих-код"));
            var create = noCode.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Создать штрих-код");
            var index = RowIndex(noCode);
            // 2026-11-23: строка могла быть создана ниже видимой части списка (под нижней панелью) — клик попал бы в чужую кнопку; сначала прокрутка к строке
            create.BringIntoView();
            UiShot.Settle(4, 30);
            UiDrive.Click(window, create);
            UiShot.Settle(6, 40);
            CatalogCacheService.Products.Single(p => p.Id == "bl" + index).Barcode.Should().NotBeNullOrEmpty("штрихкод создан и записан в карточку");
            Rows(view).Single(r => RowIndex(r) == index).GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "Создать штрих-код")
                .Should().BeFalse("строка перерисована: «＋» заменена печатью");
        }
        finally
        {
            BarcodePrintView.ThermalPrintOverride = savedThermal;
            BarcodePrintView.ConfirmOverride = savedConfirm;
            BarcodePrintView.BarcodeSaveOverride = savedSave;
            BarcodePrintView.PrinterListOverride = savedPrinters;
            UserPreferences.Instance.LabelPrinterDevicePath = savedPrinterPath;
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ запоминание выбора

    [AvaloniaFact]
    public async Task ChosenView_IsRemembered_AndNewViewsStartInIt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(60);
        UserPreferences.Instance.BarcodePrintListView = false;
        var first = new BarcodePrintView(false, true);
        var window = new Window { Width = 1366, Height = 768, Content = first };
        try
        {
            window.Show();
            UiShot.Settle(4, 30);
            first.ListMode.Should().BeFalse("по умолчанию — плитки");
            first.SetListMode(true);
            Wait(3);
            UserPreferences.Instance.BarcodePrintListView.Should().BeTrue("выбор записан в настройки");

            // новое окно/вкладка начинает в списке; кнопка «Список» подсвечена
            var second = new BarcodePrintView(false, true);
            var window2 = new Window { Width = 1366, Height = 768, Content = second };
            try
            {
                window2.Show();
                UiShot.Settle(4, 30);
                second.ListMode.Should().BeTrue("следующее открытие — снова в списке");
                Rows(second).Should().NotBeEmpty();
                CardsScroll(second).IsVisible.Should().BeFalse();
                Btn(second, "Список").Classes.Contains("active").Should().BeTrue("активный вид отмечен на переключателе");
                Btn(second, "Плитки").Classes.Contains("active").Should().BeFalse();
                second.SetListMode(false);
                Wait(3);
            }
            finally
            {
                window2.Close();
                UiShot.Pump();
            }
            UserPreferences.Instance.BarcodePrintListView.Should().BeFalse();
            var third = new BarcodePrintView(false, true);
            var window3 = new Window { Width = 1366, Height = 768, Content = third };
            try
            {
                window3.Show();
                UiShot.Settle(4, 30);
                third.ListMode.Should().BeFalse();
                Cards(third).Should().NotBeEmpty();
            }
            finally
            {
                window3.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ 15 000 товаров

    [AvaloniaFact]
    public async Task ListView_15000Products_IsFast_Virtualized_AndScrollShowsTheRightProducts()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(15000);
        UserPreferences.Instance.BarcodePrintListView = true;
        // прогрев JIT и стилей (в программе к первому открытию раздела они прогреты)
        var warm = new Window { Width = 1300, Height = 700, Content = new BarcodePrintView(false, true) };
        warm.Show();
        UiShot.Settle(3, 20);
        warm.Close();
        UiShot.Pump();

        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            var sw = Stopwatch.StartNew();
            window.Show();
            Pump();
            var first = sw.ElapsedMilliseconds;
            view.ListMode.Should().BeTrue();
            view.FoundCount.Should().Be(15000);
            var rows = Rows(view);
            rows.Count.Should().BeInRange(4, 60, "строки виртуализированы: на экране десятки строк, а не 15 000");
            view.ListRealizedRows.Should().BeLessThan(120);
            rows.Select(RowIndex).Should().Equal(Enumerable.Range(0, rows.Count), "список начинается с первого товара");

            sw.Restart();
            view.SearchText = "номер 777";
            var filterSync = sw.ElapsedMilliseconds;
            Pump(2);
            var filter = sw.ElapsedMilliseconds;
            var expected = Enumerable.Range(0, 15000).Count(i => i.ToString().Contains("777", StringComparison.Ordinal));
            view.FoundCount.Should().Be(expected);
            Rows(view).Should().NotBeEmpty();
            sw.Restart();
            view.SearchText = "";
            Pump(2);
            var reset = sw.ElapsedMilliseconds;
            view.FoundCount.Should().Be(15000);

            // прокрутка в середину: строки — те самые товары (повторное использование строк не показывает чужие данные)
            var scroll = ListScroll(view);
            scroll.Extent.Height.Should().BeGreaterThan(15000 * 40, "высота списка отражает все 15 000 строк");
            sw.Restart();
            scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
            Pump(3);
            var jump = sw.ElapsedMilliseconds;
            var mid = Rows(view);
            mid.Should().NotBeEmpty();
            var indexes = mid.Select(RowIndex).ToList();
            indexes.Min().Should().BeInRange(6500, 8500, "середина списка — товары из середины каталога");
            indexes.Zip(indexes.Skip(1), (a, b) => b - a).Should().OnlyContain(d => d == 1, "строки идут подряд, без чужих");
            view.ListRealizedRows.Should().BeLessThan(120, "после прокрутки строки переиспользуются, а не копятся");
            // много шагов колесом: число строк не растёт
            for (var i = 0; i < 60; i++)
            {
                scroll.Offset = new Vector(0, scroll.Offset.Y + 480);
                UiShot.Pump();
            }
            view.ListRealizedRows.Should().BeLessThan(120, "прокрутка не накапливает строки");
            // выбор всего списка — быстро, видимые строки отмечены
            sw.Restart();
            view.SelectFound();
            Pump(2);
            var selectAll = sw.ElapsedMilliseconds;
            view.SelectedCount.Should().Be(13500);
            Rows(view).Where(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsEnabled).All(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsChecked == true).Should().BeTrue();
            File.AppendAllText(BarcodesPerfLayoutUiTests.Out("barcodes-list-perf.txt"),
                $"list 15000: первый показ {first} мс, фильтр {filterSync}/{filter} мс, сброс {reset} мс, прыжок прокрутки {jump} мс, выбрать все {selectAll} мс, строк на экране {rows.Count}, создано {view.ListRealizedRows}{Environment.NewLine}");
            first.Should().BeLessThan(1000, "первый показ списка на 15 000 товаров — меньше секунды");
            filterSync.Should().BeLessThan(300);
            filter.Should().BeLessThan(500);
            reset.Should().BeLessThan(500);
            jump.Should().BeLessThan(700, "прыжок прокрутки в середину 15 000 строк — быстро");
            selectAll.Should().BeLessThan(1500);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ размеры окон и языки

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task ListView_FillsTheHeight_FitsTheWindow_RowsAreTouchSize(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        UserPreferences.Instance.BarcodePrintListView = false;
        Seed(3000);
        var window = new BarcodePrintSectionWindow { SizeToContent = Avalonia.Controls.SizeToContent.Manual, Width = w, Height = h };
        try
        {
            window.Show();
            UiShot.Settle(6, 40);
            var view = window.Host.View!;
            view.Should().NotBeNull();
            view.SetListMode(true);
            UiShot.Settle(6, 40);
            var (listText, tilesText) = ToggleLabels(lang);

            // переключатель: обе кнопки на месте, не ниже 44 px, внутри окна
            foreach (var caption in new[] { listText, tilesText })
            {
                var b = Btn(view, caption);
                b.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, $"кнопка «{caption}» — сенсорного размера");
                b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), window)!.Value.X.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"«{caption}» не выходит за правый край ({w}x{h} {lang})");
            }
            Btn(view, listText).Classes.Contains("active").Should().BeTrue();

            // список: заполняет остаток высоты и не выходит за окно, подвал на месте
            var items = ListItems(view);
            var listTop = items.TranslatePoint(default, window)!.Value.Y;
            var listBottom = items.TranslatePoint(new Point(0, items.Bounds.Height), window)!.Value.Y;
            listBottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"список не уходит за нижний край ({w}x{h} {lang})");
            items.Bounds.Height.Should().BeGreaterThanOrEqualTo(2 * 48, $"в списке видны хотя бы две строки ({w}x{h} {lang})");
            var pager = view.HintBar;
            var pagerTop = pager.TranslatePoint(default, window)!.Value.Y;
            var pagerBottom = pager.TranslatePoint(new Point(0, pager.Bounds.Height), window)!.Value.Y;
            pagerBottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, "нижняя панель видна");
            (pagerTop - listBottom).Should().BeLessThan(60, $"список заполняет высоту до нижней панели: зазор {pagerTop - listBottom:0} ({w}x{h} {lang})");
            listBottom.Should().BeGreaterThan(listTop);
            UiDrive.AllText(window).Should().Contain(lang switch { AppLanguage.Kyrgyz => "Тизмеде:", AppLanguage.Uzbek => "Ro'yxatda:", _ => "В списке:" }, "подвал в списке показывает число товаров");

            // строки: не ниже 44, внутри списка по ширине, кнопки и галочка не ниже 44
            var rows = Rows(view);
            rows.Should().HaveCountGreaterThanOrEqualTo(2);
            var itemsRight = items.TranslatePoint(new Point(items.Bounds.Width, 0), window)!.Value.X;
            foreach (var row in rows)
            {
                row.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "строка списка — не ниже 44 px");
                foreach (var c in row.GetVisualDescendants().OfType<Control>().Where(c => c is Button or CheckBox && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                {
                    c.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, $"{c.GetType().Name} в строке — сенсорного размера");
                    c.TranslatePoint(new Point(c.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(itemsRight + 0.5, $"{c.GetType().Name} в строке не выходит за список ({w}x{h} {lang})");
                }
                foreach (var t in row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0))
                    t.TranslatePoint(new Point(t.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(itemsRight + 0.5, "текст строки не выходит за список");
            }

            // шапка: подписи колонок на языке интерфейса
            var header = lang switch
            {
                AppLanguage.Kyrgyz => new[] { "Аталышы", "Штрих-код", "Баасы", "Көчүрмө" },
                AppLanguage.Uzbek => new[] { "Nomi", "Shtrix-kod", "Narx", "Nusxa" },
                _ => new[] { "Название", "Штрих-код", "Цена", "Копий" },
            };
            var text = UiDrive.AllText(window);
            foreach (var label in header)
                text.Should().Contain(label, $"шапка списка на языке интерфейса ({lang})");
            if (items.Bounds.Width >= 910) // 2026-11-27: порог колонки остатка 840 → 910 вместе с шириной колонки штрихкода (246 px)
                text.Should().Contain(lang switch { AppLanguage.Kyrgyz => "Калдык", AppLanguage.Uzbek => "Qoldiq", _ => "Остаток" }, "на широком списке есть колонка остатка");

            // поле поиска не сжато до щели (панель инструментов с переключателем помещается)
            view.SearchBox.Bounds.Width.Should().BeGreaterThan(150, $"поиск не сжат переключателем ({w}x{h} {lang}): {view.SearchBox.Bounds.Width:0}");
            File.AppendAllText(BarcodesPerfLayoutUiTests.Out("barcodes-list-layout.txt"),
                $"list {w}x{h} {lang}: список {items.Bounds.Width:0}x{items.Bounds.Height:0}, низ {listBottom:0} (окно {window.Bounds.Height:0}), зазор до подвала {pagerTop - listBottom:0}, строк {rows.Count}, поиск {view.SearchBox.Bounds.Width:0}{Environment.NewLine}");

            // возврат к плиткам и снова в список: раскладка та же
            view.SetListMode(false);
            UiShot.Settle(4, 30);
            CardsScroll(view).Bounds.Height.Should().BeGreaterThan(50, "плитки вернулись");
            var cardsBottom = CardsScroll(view).TranslatePoint(new Point(0, CardsScroll(view).Bounds.Height), window)!.Value.Y;
            cardsBottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, "плитки тоже не уходят за нижний край");
            view.SetListMode(true);
            UiShot.Settle(4, 30);
            var items2 = ListItems(view);
            items2.TranslatePoint(new Point(0, items2.Bounds.Height), window)!.Value.Y.Should().BeApproximately(listBottom, 1, "после туда-обратно список на прежнем месте");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ поля формы не налезают

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task ListView_FormFields_DoNotOverlap(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        UserPreferences.Instance.BarcodePrintListView = true;
        Seed(500);
        var window = new BarcodePrintSectionWindow { SizeToContent = Avalonia.Controls.SizeToContent.Manual, Width = w, Height = h };
        try
        {
            window.Show();
            UiShot.Settle(6, 40);
            var view = window.Host.View!;
            view.ListMode.Should().BeTrue();
            var found = new List<string>();
            found.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "list").Select(v => v.Detail));
            // отметить две строки — у них появляются настоящие поля «Копий» рядом с полем поиска и списком категорий
            foreach (var row in Rows(view).Take(2))
                UiDrive.Click(window, row.GetVisualDescendants().OfType<CheckBox>().First());
            UiShot.Settle(4, 30);
            Rows(view).Take(2).All(r => r.GetVisualDescendants().OfType<NumericUpDown>().Any()).Should().BeTrue("у отмеченных строк — настоящие поля «Копий»");
            found.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "list-selected").Select(v => v.Detail));
            // строка с несколькими штрихкодами (список выбора кода) и поиск с введённым текстом
            view.SearchText = "номер 12";
            UiShot.Settle(4, 30);
            found.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "list-search").Select(v => v.Detail));
            // плитки после списка — прежняя проверка
            view.SetListMode(false);
            UiShot.Settle(4, 30);
            found.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "tiles").Select(v => v.Detail));
            found.Distinct().Should().BeEmpty($"поля формы не налезают ({w}x{h} {lang}): " + string.Join("; ", found.Distinct().Take(6)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Проверка самой проверки: поле «Копий» второй отмеченной строки сдвигаем на поле «Копий» первой — проверка «поля не налезают» обязана это увидеть
    /// (иначе зелёный тест перекрытия ничего не стоит).</summary>
    [AvaloniaFact]
    public async Task OverlapChecker_SeesAFieldMovedOverAnother_InTheListRows()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        UserPreferences.Instance.BarcodePrintListView = true;
        Seed(50);
        var window = new BarcodePrintSectionWindow { SizeToContent = Avalonia.Controls.SizeToContent.Manual, Width = 1366, Height = 768 };
        try
        {
            window.Show();
            UiShot.Settle(6, 40);
            var view = window.Host.View!;
            var rows = Rows(view).Where(r => r.GetVisualDescendants().OfType<CheckBox>().First().IsEnabled).Take(2).ToList();
            foreach (var r in rows)
                UiDrive.Click(window, r.GetVisualDescendants().OfType<CheckBox>().First());
            UiShot.Settle(4, 30);
            FormFieldsOverlapUiTests.FindViolations(window, "before").Should().BeEmpty("до сдвига поля не налезают");
            var a = rows[0].GetVisualDescendants().OfType<NumericUpDown>().Single();
            var b = rows[1].GetVisualDescendants().OfType<NumericUpDown>().Single();
            var pa = a.TranslatePoint(default, window)!.Value;
            var pb = b.TranslatePoint(default, window)!.Value;
            ((Border)b.Parent!).RenderTransform = new TranslateTransform(pa.X - pb.X, pa.Y - pb.Y);
            UiShot.Settle(2, 20);
            FormFieldsOverlapUiTests.FindViolations(window, "after").Should().Contain(v => v.Key.Contains("поля-перекрытие"), "проверка видит два поля друг на друге");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
