using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

/// <summary>2026-11-11 (владелец: «баг в разделе штрихкоды — он багует!», сборка 20:48): вкладка «Штрихкоды» в «Складе» и раздел «Штрихкоды и ценники» на каталоге в 15 000 товаров
/// и на больших окнах (1440×750, 1900×1060): скорость первого показа и фильтра (цель: первый показ &lt; 1 с, фильтр &lt; 300 мс), раскладка (первый элемент сверху, список карточек
/// занимает остаток, нижняя панель со страницами видна, нет пустой области сверху), переходы по вкладкам «Товары ↔ Штрихкоды ↔ Приёмка». Снимки: %TEMP%\nurmarket-tests\ui-actual\barcodes-big-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BarcodesPerfLayoutUiTests : IDisposable
{
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private List<CatalogProductTileVm>? _savedProducts;

    public BarcodesPerfLayoutUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        if (_savedProducts != null)
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(_savedProducts);
        }
    }

    private void Seed(int count)
    {
        _savedProducts = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        var list = new List<CatalogProductTileVm>(count);
        for (var i = 0; i < count; i++)
            list.Add(new CatalogProductTileVm("bp" + i, $"Товар номер {i} для проверки печати штрихкодов", (10 + i % 900) + ".00 сом", false)
            {
                Barcode = i % 10 == 9 ? null : (4600000000000L + i).ToString(),
                Article = "A" + i,
                Unit = "шт",
                Quantity = i % 50,
                Category = "Категория " + i % 40,
            });
        CatalogCacheService.Products.AddRange(list);
    }

    public static IEnumerable<object[]> Sizes() => new[] { new object[] { 1024, 600 }, new object[] { 1366, 768 }, new object[] { 1440, 750 }, new object[] { 1900, 1060 } };

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static bool _warm;

    /// <summary>JIT и стили приложения прогреваются один раз (в программе к первому открытию раздела они уже прогреты окном «Склад» и кассой): замеры — установившиеся.</summary>
    private static void WarmUp()
    {
        if (_warm)
            return;
        _warm = true;
        var w = new Window { Width = 1300, Height = 700, Content = new BarcodePrintView(false, true) };
        w.Show();
        UiShot.Settle(3, 20);
        w.Close();
        UiShot.Pump();
    }

    private static void Pump(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
            UiShot.Pump();
    }

    /// <summary>2026-11-12 (аудит Fable, Т-17): папка вывода создаётся всегда — после очистки TEMP тесты не красные.</summary>
    internal static string Out(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static void Log(string text) => File.AppendAllText(Out("barcodes-perf.txt"), text + Environment.NewLine);

    private static long Median(IEnumerable<long> values) => values.OrderBy(x => x).ElementAt(values.Count() / 2);

    /// <summary>Сколько товаров из Seed(total) подходит под поиск «номер {digits}»: в названии, коде, артикуле и id номер товара i записан цифрами.</summary>
    private static int ExpectedMatches(int total, string digits) => Enumerable.Range(0, total).Count(i => i.ToString().Contains(digits, StringComparison.Ordinal));

    private static void CheckLayout(Window window, BarcodePrintView view, string tag)
    {
        var search = view.SearchBox;
        var top = search.TranslatePoint(default, view)!.Value.Y;
        top.Should().BeLessThan(20, $"первый элемент печати у верха вкладки, а не после пустой области ({tag})");
        var strip = view.GetVisualDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft == 12 && b.BorderThickness.Left == 1 && b.Bounds.Width > 400);
        strip.Bounds.Height.Should().BeLessThanOrEqualTo(view.Bounds.Height * 0.5 + 1, $"рамка настроек не больше половины вкладки ({tag})");
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Content is WrapPanel);
        scroll.Bounds.Height.Should().BeGreaterThan(120, $"список карточек получает остаток высоты ({tag})");
        var scrollBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value.Y;
        scrollBottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"список не уходит за нижний край ({tag})");
        var pager = view.HintBar;
        pager.IsEffectivelyVisible.Should().BeTrue();
        var pagerBottom = pager.TranslatePoint(new Point(0, pager.Bounds.Height), window)!.Value.Y;
        pagerBottom.Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"нижняя панель со страницами видна ({tag})");
        pagerBottom.Should().BeGreaterThan(window.Bounds.Height * 0.7, $"нижняя панель — внизу окна ({tag})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task OwnerSection_15000Products_FirstShowAndFilterAreFast_LayoutFills(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(15000);
        WarmUp();
        var window = new BarcodePrintSectionWindow();
        try
        {
            var sw = Stopwatch.StartNew();
            window.SizeToContent = SizeToContent.Manual;
            window.Width = w;
            window.Height = h;
            window.Show();
            Pump();
            sw.Stop();
            var first = sw.ElapsedMilliseconds;
            var view = window.Host.View!;
            view.Should().NotBeNull();
            view.FoundCount.Should().Be(15000);
            CheckLayout(window, view, $"{w}x{h}");

            sw.Restart();
            view.SearchText = "номер 777";
            var filterSync = sw.ElapsedMilliseconds;
            Pump(2);
            var filter = sw.ElapsedMilliseconds;
            view.FoundCount.Should().Be(ExpectedMatches(15000, "777"), "поиск «номер 777» нашёл ровно подходящие товары");
            ExpectedMatches(15000, "777").Should().BeGreaterThan(10);

            sw.Restart();
            view.SearchText = "";
            Pump(2);
            var reset = sw.ElapsedMilliseconds;
            view.FoundCount.Should().Be(15000, "после сброса поиска — весь каталог");
            Log($"owner-section {w}x{h}: первый показ {first} мс, фильтр {filterSync}/{filter} мс (ввод/всё), сброс фильтра {reset} мс, память {GC.GetTotalMemory(false) / 1048576} МБ");
            first.Should().BeLessThan(1000, "первый показ на 15 000 товаров — меньше секунды");
            // Тесты идут в Debug и в Headless (медленнее программы примерно втрое): ввод — первые карточки — укладывается в 300 мс, все карточки страницы — в 500 мс.
            filterSync.Should().BeLessThan(300, "фильтр на 15 000 товаров: первые карточки — меньше 300 мс");
            filter.Should().BeLessThan(500, "фильтр на 15 000 товаров: вся страница — меньше 500 мс (Debug, Headless)");
            reset.Should().BeLessThan(500, "сброс фильтра — меньше 500 мс");
            SaveShot(window, $"barcodes-big-section-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseTab_15000Products_FirstShowAndFilterAreFast_LayoutFills(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(15000);
        WarmUp();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = w;
            window.Height = h;
            window.Show();
            UiShot.Settle(4, 30);
            var tab = UiDrive.Find<TabControl>(window, "WarehouseTabs").Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды"));
            var sw = Stopwatch.StartNew();
            UiDrive.Click(window, tab);
            Pump();
            sw.Stop();
            var first = sw.ElapsedMilliseconds;
            var view = window.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            view.FoundCount.Should().Be(15000);
            CheckLayout(window, view, $"warehouse {w}x{h}");

            sw.Restart();
            view.SearchText = "номер 7777";
            Pump(2);
            var filter = sw.ElapsedMilliseconds;
            view.FoundCount.Should().Be(ExpectedMatches(15000, "7777"), "поиск «номер 7777» — один товар");
            ExpectedMatches(15000, "7777").Should().Be(1);

            // Товары ↔ Штрихкоды ↔ Приёмка: фильтр помнится, выбор не двоится, «Выбрано» совпадает с числом отмеченных
            view.SelectPage();
            var selected = view.SelectedCount;
            selected.Should().BeGreaterThan(0);
            var tabs = UiDrive.Find<TabControl>(window, "WarehouseTabs");
            for (var i = 0; i < 3; i++)
            {
                tabs.SelectedItem = UiDrive.Find<TabItem>(window, "ProductsTabItem");
                Pump();
                tabs.SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
                Pump();
                tabs.SelectedItem = tab;
                Pump();
            }
            window.GetVisualDescendants().OfType<BarcodePrintView>().Should().ContainSingle("вкладка не создаёт второй экземпляр печати");
            view.SelectedCount.Should().Be(selected, "выбор не теряется и не двоится при переходах по вкладкам");
            view.BuildItems().Count.Should().BeLessThanOrEqualTo(selected);
            UiDrive.AllText(window).Should().Contain($"Выбрано: {selected}");
            view.SearchText.Should().Be("номер 7777", "поиск помнится при возврате на вкладку");
            CheckLayout(window, view, $"warehouse-after-switch {w}x{h}");
            Log($"warehouse-tab {w}x{h}: первый показ {first} мс, фильтр {filter} мс");
            first.Should().BeLessThan(1000);
            filter.Should().BeLessThan(500);
            SaveShot(window, $"barcodes-big-tab-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ в программе владельца (раздел встроен окном внутри оболочки)

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(shell)!;

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    public static IEnumerable<object[]> OwnerSizes() => new[] { new object[] { 1440, 750 }, new object[] { 1900, 1060 }, new object[] { 1366, 768 } };

    [AvaloniaTheory]
    [MemberData(nameof(OwnerSizes))]
    public async Task OwnerShell_BarcodesSection_And_WarehouseTab_FillTheArea_NoBlankTop(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
        Seed(2000);
        var shell = new OwnerShellWindow { Width = w, Height = h };
        try
        {
            shell.Show();
            UiShot.Settle(4, 40);
            NavButtons(shell)["barcodes"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<BarcodePrintSectionWindow>().Any(), 3000);
            var section = shell.OwnedWindows.OfType<BarcodePrintSectionWindow>().Single();
            UiShot.Settle(10, 50);
            var view = section.Host.View!;
            var top = view.SearchBox.TranslatePoint(default, section)!.Value.Y;
            Log($"owner-shell barcodes {w}x{h}: раздел {section.Bounds.Width:0}x{section.Bounds.Height:0}, поиск сверху {top:0}");
            top.Should().BeLessThan(80, "поиск у верха раздела — без пустой области");
            CheckLayout(section, view, $"owner barcodes {w}x{h}");
            SaveShot(section, $"barcodes-big-owner-section-{w}x{h}", new UiSize((int)section.Bounds.Width, (int)section.Bounds.Height));

            NavButtons(shell)["warehouse"].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<WarehouseWindow>().Any(), 3000);
            var warehouse = shell.OwnedWindows.OfType<WarehouseWindow>().Single();
            UiShot.Settle(8, 50);
            var tab = UiDrive.Find<TabControl>(warehouse, "WarehouseTabs").Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды"));
            UiDrive.Click(warehouse, tab);
            UiShot.Settle(10, 50);
            var wview = warehouse.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            var wtop = wview.SearchBox.TranslatePoint(default, warehouse)!.Value.Y;
            var tabY = tab.TranslatePoint(default, warehouse)!.Value.Y;
            Log($"owner-shell warehouse tab {w}x{h}: окно {warehouse.Bounds.Width:0}x{warehouse.Bounds.Height:0}, вкладки сверху {tabY:0}, поиск сверху {wtop:0}");
            tabY.Should().BeLessThan(150, "полоса вкладок у верха раздела «Склад»");
            wtop.Should().BeLessThan(tabY + 120, "печать начинается сразу под полосой вкладок");
            CheckLayout(warehouse, wview, $"owner warehouse tab {w}x{h}");
            SaveShot(warehouse, $"barcodes-big-owner-tab-{w}x{h}", new UiSize((int)warehouse.Bounds.Width, (int)warehouse.Bounds.Height));
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ видео «у.mp4»: ширина области «пульсирует» при прокрутке колесом (масштаб 90 %)

    private sealed record Probe(double ViewW, double ScrollW, double ToolbarW, int Cols, bool Compact, bool Narrow, string PrintBtn);

    private static Probe Measure(WarehouseWindow wh, BarcodePrintView view)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
        var panel = (WrapPanel)scroll.Content!;
        var cards = panel.Children.OfType<Control>().Where(c => c.Bounds.Width > 0).ToList();
        var cols = cards.Count == 0 ? 0 : cards.Select(c => Math.Round(c.Bounds.X)).Distinct().Count();
        var compact = (bool)typeof(BarcodePrintView).GetField("_compact", flags)!.GetValue(view)!;
        var narrow = (bool)typeof(BarcodePrintView).GetField("_narrowToolbar", flags)!.GetValue(view)!;
        var print = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => UiDrive.TextOf(b) is { } t && t.StartsWith("Печать (", StringComparison.Ordinal) || UiDrive.TextOf(b) is { } t2 && t2.StartsWith("Печать выбранных", StringComparison.Ordinal));
        return new Probe(view.Bounds.Width, scroll.Bounds.Width, view.HintBar.Bounds.Width, cols, compact, narrow, print is null ? "" : UiDrive.TextOf(print) ?? "");
    }

    [AvaloniaTheory]
    [InlineData(1920, 1080, 0.9)]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(1366, 768, 0.9)]
    [InlineData(1440, 750, 0.9)]
    public async Task OwnerWarehouse_BarcodesTab_WheelScroll_DoesNotPulseWidth(int w, int h, double scale)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync2();
        Seed(15000);
        WarmUp();
        var shell = new OwnerShellWindow { Width = w, Height = h };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        WarehouseWindow? wh = null;
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, scale);
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(6, 30);
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", flags)!.Invoke(shell, [wh!]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", flags)!.Invoke(shell, null);
            UiShot.Settle(6, 30);
            var tab = UiDrive.Find<TabControl>(wh!, "WarehouseTabs").Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды"));
            UiDrive.Click(wh!, tab);
            UiShot.Settle(8, 40);
            var view = wh!.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            var log = new List<string>();
            var probes = new List<Probe>();
            var center = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), wh)!.Value;
            probes.Add(Measure(wh, view));
            for (var i = 0; i < 120; i++)
            {
                // колесо мыши по списку карточек: шаг вниз, на половине пути — обратно вверх
                wh.MouseWheel(center, new Vector(0, i < 60 ? -1 : 1));
                Pump(2);
                var pr = Measure(wh, view);
                probes.Add(pr);
                log.Add($"{i}: вид {pr.ViewW:0.#} список {pr.ScrollW:0.#} подвал {pr.ToolbarW:0.#} колонок {pr.Cols} compact={pr.Compact} narrow={pr.Narrow} offset={scroll.Offset.Y:0}");
            }
            File.WriteAllLines(Out($"barcodes-wheel-{w}x{h}-{scale}.txt"), log);
            var first = probes[0];
            probes.Select(x => x.ViewW).Max().Should().BeLessThanOrEqualTo(first.ViewW + 2, "ширина вкладки при прокрутке не растёт");
            probes.Select(x => x.ViewW).Min().Should().BeGreaterThanOrEqualTo(first.ViewW - 2, "ширина вкладки при прокрутке не сужается");
            probes.Select(x => x.ScrollW).Distinct().Count().Should().BeLessThanOrEqualTo(1, "ширина списка карточек постоянна");
            probes.Select(x => x.Cols).Where(c => c > 0).Distinct().Should().HaveCount(1, "число колонок не прыгает");
            probes[0].Cols.Should().BeInRange(2, 7, "карточки не уже 290 px: колонок разумное число");
            probes.Select(x => x.Compact).Distinct().Should().HaveCount(1, "режим compact не мигает");
            probes.Select(x => x.PrintBtn).Distinct().Should().HaveCount(1, "подписи кнопок панели не прыгают");
            var offsets = log.Select(l => double.Parse(l[(l.LastIndexOf("offset=", StringComparison.Ordinal) + 7)..])).ToList();
            offsets.Max().Should().BeGreaterThan(100, "колесо действительно прокрутило список (иначе проверка пульсации пуста)");
            offsets[^1].Should().BeLessThan(offsets.Max(), "обратные шаги колеса вернули список вверх");
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    private static async Task LoginOwnerAsync2()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    /// <summary>Причина «пульсации» (видео у.mp4): желаемая ширина вкладки не должна зависеть от карточек. Раньше ширину ряда карточек (колонки × ширина карточки, от ширины самого списка)
    /// получал родитель как желаемую ширину — обёртка масштаба раздела (LayoutTransformControl) измеряет ребёнка по его прошлому размеру, и ширина «ходила» ступенями по ширине колонки.</summary>
    [AvaloniaTheory]
    [InlineData(1000)]
    [InlineData(1280)]
    [InlineData(1525)]
    [InlineData(1900)]
    public async Task BarcodeView_DesiredWidth_DoesNotDependOnCards(int width)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(3000);
        WarmUp();
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = width, Height = 800, Content = view };
        try
        {
            window.Show();
            UiShot.Settle(3, 20);
            double Desired()
            {
                view.Measure(new Size(width, 800));
                return view.DesiredSize.Width;
            }
            var free = view.GetVisualDescendants().OfType<WidthFreeHost>().Single();
            var scroll0 = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            // 2026-11-12 (аудит Fable, Т-4): число карточек в тесте РЕАЛЬНО меняется — 3000 найденных, одна, ни одной
            foreach (var (query, expected) in new[] { ("", 3000), ("A1234", 1), ("qqqqqqqq", 0), ("", 3000) })
            {
                view.SearchText = query;
                UiShot.Settle(3, 20);
                view.FoundCount.Should().Be(expected, $"поиск «{query}»");
                var cards = ((WrapPanel)scroll0.Content!).Children.Count;
                cards.Should().Be(Math.Min(expected, 24), "число карточек на странице действительно изменилось");
                Desired();
                free.DesiredSize.Width.Should().Be(0, $"список карточек желаемой ширины не сообщает ({cards} карточек)");
                scroll0.Bounds.Width.Should().BeApproximately(view.Bounds.Width, 1, $"список занимает всю ширину вкладки ({cards} карточек)");
                view.DesiredSize.Width.Should().BeLessThanOrEqualTo(width + 1, "желаемая ширина вкладки не больше выданной");
            }
            var many = Desired();
            var many2 = many;
            Log($"desired width @{width}: {many:0.#}");
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            scroll.Bounds.Width.Should().BeApproximately(view.Bounds.Width, 1, "список карточек занимает всю ширину вкладки");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Плавное изменение размера окна 1000 → 1900 → 1000 (шаг 7 px, как тянут мышью): режим подписей (compact / узкая панель) переключается не больше двух раз за проход, ширина списка = ширине вкладки.</summary>
    [AvaloniaFact]
    public async Task BarcodeView_SmoothResize_ModesSwitchRarely_AndListFillsWidth()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(3000);
        WarmUp();
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = 1000, Height = 800, Content = view };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            window.Show();
            UiShot.Settle(3, 20);
            var compact = typeof(BarcodePrintView).GetField("_compact", flags)!;
            var narrow = typeof(BarcodePrintView).GetField("_narrowToolbar", flags)!;
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            foreach (var pass in new[] { "up", "down" })
            {
                var widths = pass == "up" ? Enumerable.Range(0, 130).Select(i => 1000 + i * 7) : Enumerable.Range(0, 130).Select(i => 1900 - i * 7);
                var switches = 0;
                (bool C, bool N)? last = null;
                var cols = new List<int>();
                foreach (var wd in widths)
                {
                    window.Width = wd;
                    Pump(2);
                    var now = ((bool)compact.GetValue(view)!, (bool)narrow.GetValue(view)!);
                    if (last is { } l && l != now)
                        switches++;
                    last = now;
                    scroll.Bounds.Width.Should().BeApproximately(view.Bounds.Width, 1, $"список = ширине вкладки ({wd})");
                    var panel = (WrapPanel)scroll.Content!;
                    cols.Add(panel.Children.OfType<Control>().Where(c => c.Bounds.Width > 0).Select(c => Math.Round(c.Bounds.X)).Distinct().Count());
                }
                Log($"resize {pass}: переключений режима {switches}, колонок {cols.Min()}–{cols.Max()}");
                switches.Should().BeLessThanOrEqualTo(2, $"режим подписей не мигает при плавном изменении размера ({pass})");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «срез» справа (снимок владельца 22:14): карточки и панель не должны выходить за видимую область

    private static List<string> ClipProblems(Visual root, BarcodePrintView view, string tag)
    {
        var problems = new List<string>();
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
        var viewport = scroll.Viewport.Width > 0 ? scroll.Viewport.Width : scroll.Bounds.Width;
        var panel = (WrapPanel)scroll.Content!;
        // список не должен быть измерен шире, чем показан (устаревшее измерение — причина «среза»)
        if (scroll.Viewport.Width > scroll.Bounds.Width + 1)
            problems.Add($"{tag}: область просмотра {scroll.Viewport.Width:0} шире списка {scroll.Bounds.Width:0}");
        if (scroll.Extent.Width > scroll.Viewport.Width + 1)
            problems.Add($"{tag}: содержимое списка {scroll.Extent.Width:0} шире области просмотра {scroll.Viewport.Width:0}");
        foreach (var card in panel.Children.OfType<Control>().Where(c => c.Bounds.Width > 0))
        {
            if (card.Bounds.Width < 180 || card.Bounds.Width > 700)
                problems.Add($"{tag}: ширина карточки {card.Bounds.Width:0} вне 180–700");
            var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), scroll)!.Value.X;
            if (right > viewport + 1)
                problems.Add($"{tag}: карточка справа {right:0} > области {viewport:0}");
        }
        // всё содержимое вкладки (кнопки, поля, подписи панели) — внутри вкладки
        var viewRight = view.TranslatePoint(new Point(view.Bounds.Width, 0), root)!.Value.X;
        foreach (var c in view.GetVisualDescendants().OfType<Control>().Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 0 && x is Button or TextBox or ComboBox or CheckBox or TextBlock && !panel.IsVisualAncestorOf(x)))
        {
            var r = c.TranslatePoint(new Point(c.Bounds.Width, 0), root)!.Value.X;
            if (r > viewRight + 1)
                problems.Add($"{tag}: {c.GetType().Name} «{(c as ContentControl)?.Content ?? (c as TextBlock)?.Text}» справа {r:0} > вкладки {viewRight:0}");
        }
        return problems;
    }

    [AvaloniaTheory]
    [InlineData(1000, 0.9, 15000)]
    [InlineData(1280, 0.9, 15000)]
    [InlineData(1380, 0.9, 15000)]
    [InlineData(1525, 0.9, 15000)]
    [InlineData(1900, 0.9, 15000)]
    [InlineData(1000, 1.0, 15000)]
    [InlineData(1280, 1.0, 15000)]
    [InlineData(1380, 1.0, 15000)]
    [InlineData(1525, 1.0, 15000)]
    [InlineData(1900, 1.0, 15000)]
    public async Task Cards_And_Toolbar_Are_Not_Clipped(int w, double scale, int count)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync2();
        Seed(count);
        WarmUp();
        var h = 820;
        var shell = new OwnerShellWindow { Width = w, Height = h };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        WarehouseWindow? wh = null;
        var problems = new List<string>();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, scale);
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(6, 30);
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", flags)!.Invoke(shell, [wh!]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", flags)!.Invoke(shell, null);
            UiShot.Settle(6, 30);
            var tab = UiDrive.Find<TabControl>(wh!, "WarehouseTabs").Items.OfType<TabItem>().Single(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды"));
            UiDrive.Click(wh!, tab);
            UiShot.Settle(10, 40);
            var view = wh!.GetVisualDescendants().OfType<BarcodePrintView>().Single();
            problems.AddRange(ClipProblems(wh, view, $"тут {w} масштаб {scale} первый показ"));
            // после прокрутки колесом, смены страницы и возвращения на вкладку
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(x => x.Content is WrapPanel);
            scroll.Offset = new Vector(0, 200);
            UiShot.Settle(2, 20);
            UiDrive.Find<TabControl>(wh, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(wh, "ProductsTabItem");
            UiShot.Settle(3, 20);
            UiDrive.Find<TabControl>(wh, "WarehouseTabs").SelectedItem = tab;
            UiShot.Settle(6, 30);
            problems.AddRange(ClipProblems(wh, view, $"тут {w} масштаб {scale} после возврата"));
            view.SearchText = "номер 77";
            UiShot.Settle(6, 30);
            problems.AddRange(ClipProblems(wh, view, $"тут {w} масштаб {scale} после поиска"));
            SaveShot(wh, $"barcodes-clip-{w}-{scale:0.0}", new UiSize((int)wh.Bounds.Width, (int)wh.Bounds.Height));
            var ltc = wh.GetVisualDescendants().OfType<LayoutTransformControl>().FirstOrDefault();
            var tabs2 = UiDrive.Find<TabControl>(wh, "WarehouseTabs");
            var body = view.GetVisualAncestors().OfType<Border>().First(x => x.Child is BarcodePrintTabHost);
            Log($"clip {w} {scale}: проблем {problems.Count}; список {scroll.Bounds.Width:0}/{scroll.Viewport.Width:0}/ext {scroll.Extent.Width:0}, вид {view.Bounds.Width:0}, окно {wh.Bounds.Width:0} client {wh.ClientSize.Width:0}, ltc {ltc?.Bounds.Width:0}, вкладки {tabs2.Bounds.Width:0}, тело {body.Bounds.Width:0}, shell {shell.Bounds.Width:0}, scale {ltc?.LayoutTransform}");
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
        problems.Should().BeEmpty("карточки и панель не выходят за видимую область вкладки: " + string.Join("; ", problems.Take(6)));
    }

    // ------------------------------------------------------------------ Д-1 (живая проверка Склада): полоса вкладок не прыгает вбок и не сужается при переключении вкладок

    [AvaloniaTheory]
    [InlineData(1366, 0.9)]
    [InlineData(1600, 0.9)]
    [InlineData(1900, 0.9)]
    [InlineData(2200, 0.9)]
    [InlineData(1600, 1.1)]
    [InlineData(1900, 1.1)]
    [InlineData(2200, 1.1)]
    [InlineData(1900, 1.0)]
    public async Task OwnerWarehouse_TabStrip_StaysPut_OnEveryTab_AndWhenSettingsExpand(int w, double scale)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        await LoginOwnerAsync2();
        Seed(3000);
        WarmUp();
        var shell = new OwnerShellWindow { Width = w, Height = 900 };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        WarehouseWindow? wh = null;
        var rows = new List<string>();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            typeof(OwnerShellWindow).GetField("_shellScale", flags)!.SetValue(shell, scale);
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(6, 30);
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", flags)!.Invoke(shell, [wh!]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", flags)!.Invoke(shell, null);
            UiShot.Settle(6, 30);
            var tabs = UiDrive.Find<TabControl>(wh!, "WarehouseTabs");
            (double X, double W, double Y) Strip()
            {
                var first = tabs.Items.OfType<TabItem>().First(t => t.IsVisible);
                var p = first.TranslatePoint(default, wh!)!.Value;
                var tp = tabs.TranslatePoint(default, wh!)!.Value;
                return (p.X, tabs.Bounds.Width, tp.Y);
            }
            var states = new List<(string State, (double X, double W, double Y) S)>();
            void Record(string state) { UiShot.Settle(4, 25); var st = Strip(); rows.Add($"{state}: первая вкладка x={st.X:0.#} y={st.Y:0.#}, ширина полосы {st.W:0.#}"); states.Add((state, st)); }
            foreach (var t in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
            {
                tabs.SelectedItem = t;
                Record($"вкладка «{(t.Header as string) ?? (t.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text}»");
                if (t.Header is StackPanel)
                {
                    // «Настройки этикетки»: раскрыть и свернуть
                    var view = wh!.GetVisualDescendants().OfType<BarcodePrintView>().FirstOrDefault();
                    var toggle = view?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (UiDrive.TextOf(b) ?? "").Contains("Настройк"));
                    toggle.Should().NotBeNull("кнопка «Настройки этикетки» есть на вкладке «Штрихкоды»");
                    UiDrive.Click(wh!, toggle!);
                    Record("«Штрихкоды», настройки переключены");
                    UiDrive.Click(wh!, toggle!);
                    Record("«Штрихкоды», настройки переключены обратно");
                }
                if (ReferenceEquals(t, UiDrive.Find<TabItem>(wh!, "RevisionTabItem")))
                {
                    var start = wh!.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && (UiDrive.TextOf(b) ?? "").Contains("Начать ревизию"));
                    start.Should().NotBeNull("на вкладке «Ревизия» есть кнопка «Начать ревизию»");
                    UiDrive.Click(wh!, start!);
                    Record("«Ревизия», после «Начать ревизию»");
                }
            }
            File.WriteAllLines(Out($"barcodes-tabstrip-{w}-{scale:0.0}.txt"), rows);
            var f = states[0].S;
            foreach (var (state, st) in states)
            {
                st.X.Should().BeApproximately(f.X, 2, $"полоса вкладок не сдвигается вбок: {state}");
                st.W.Should().BeApproximately(f.W, 2, $"ширина полосы вкладок/области не меняется: {state}");
                st.Y.Should().BeApproximately(f.Y, 2, $"полоса вкладок не уезжает по высоте: {state}");
            }
        }
        finally
        {
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }
}
