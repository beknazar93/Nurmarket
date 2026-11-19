using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (перенос из веб-редактора ценников): «Сколько на товар» (по одной / по остатку / вручную), подпись «Товаров: N · этикеток: M», «Только отсканированные»,
/// сворачиваемый блок «Настройки принтера» (сдвиги в точках, «Рамка» — всегда; зазор, скорость, плотность, ориентация, «Калибровка» — только у принтера без драйвера на TSPL),
/// подпись под предпросмотром в точках принтера. Настройки реально пишутся в UserPreferences; текст не обрезается на ru/ky/uz/tr при 1024×600 и 1366×768.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\tagsetup-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BarcodesTagSetup1119UiTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private readonly BarcodePrintSettings _savedSettings = BarcodePrintSettingsStore.Load();
    private readonly (double Gap, double X, double Y, int? Dens, int? Speed, bool? Flip, string Lang, string? Path) _savedPrefs;

    public BarcodesTagSetup1119UiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        var p = UserPreferences.Instance;
        _savedPrefs = (p.LabelGapMm, p.LabelOffsetXMm, p.LabelOffsetYMm, p.LabelTsplDensity, p.LabelTsplSpeed, p.LabelTsplFlip, p.LabelRawLanguage, p.LabelPrinterDevicePath);
        p.BulkPriceTagUnlocked = true;
    }

    public void Dispose()
    {
        BarcodePrintWindow.PrinterListOverride = null;
        BarcodePrintWindow.ConfirmOverride = null;
        var p = UserPreferences.Instance;
        p.BulkPriceTagUnlocked = _savedUnlocked;
        (p.LabelGapMm, p.LabelOffsetXMm, p.LabelOffsetYMm, p.LabelTsplDensity, p.LabelTsplSpeed, p.LabelTsplFlip, p.LabelRawLanguage, p.LabelPrinterDevicePath) = _savedPrefs;
        BarcodePrintSettingsStore.Save(_savedSettings);
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    private static void Seed(params double[] quantities)
    {
        CatalogCacheService.Products.Clear();
        for (var i = 0; i < quantities.Length; i++)
            CatalogCacheService.Products.Add(new CatalogProductTileVm("c" + i, "Товар " + i, "10.00 сом", false) { Barcode = (4600000000000L + i).ToString(), Unit = "шт", Quantity = quantities[i] });
    }

    private static T F<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;

    private static void Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Any)!.Invoke(o, args);

    private static (Window Window, BarcodePrintView View) Open(int w, int h)
    {
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = w, Height = h, Content = view };
        window.Show();
        UiShot.Settle(6, 40);
        // первое открытие (JIT) может достроиться позже: ждём конца начальной настройки (_init), иначе выбор в списках игнорируется
        for (var i = 0; i < 100 && F<bool>(view, "_init"); i++)
            UiShot.Settle(2, 50);
        return (window, view);
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    public static IEnumerable<object[]> Matrix() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish }.Select(l => new object[] { s.W, s.H, l }));

    /// <summary>Блок «Настройки принтера»: у принтера без драйвера на TSPL — все поля и записываются в настройки кассы; у принтера через драйвер — только сдвиги и «Рамка».</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task PrinterBlock_WritesRealSettings_ShowsTsplOnlyForTsplPrinter_AndTextIsNotClipped(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        Seed(5, 5, 5);
        UserPreferences.Instance.LabelRawLanguage = "tspl";
        UserPreferences.Instance.LabelPrinterDevicePath = "COM7";
        BarcodePrintWindow.PrinterListOverride = () => new[] { new DiscoveredPrinter("Xprinter XP-365B (TSPL)", "COM7"), new DiscoveredPrinter("Офисный принтер (драйвер Windows)", "Office Laser") };
        var (window, view) = Open(w, h);
        try
        {
            UiShot.Settle(8, 50);
            Call(view, "SetSettingsOpen", true, false);
            var toggle = F<Button>(view, "_ptToggle");
            Click(toggle);
            UiShot.Settle(6, 40);
            F<StackPanel>(view, "_ptBody").IsVisible.Should().BeTrue("блок раскрылся");
            F<ComboBox>(view, "_printerCombo").SelectedIndex.Should().Be(0, "выбран принтер без драйвера (COM)");
            var tspl = F<StackPanel>(view, "_ptTspl");
            tspl.IsVisible.Should().BeTrue("принтер без драйвера на TSPL — зазор, скорость, плотность, ориентация видны");
            F<Button>(view, "_calibrateButton").IsVisible.Should().BeTrue();

            // изменения доходят до настроек кассы (печать читает те же поля)
            var prefs = UserPreferences.Instance;
            F<NumericUpDown>(view, "_offXBox").Value = 16;
            F<NumericUpDown>(view, "_offYBox").Value = -8;
            F<NumericUpDown>(view, "_gapBox").Value = 3m;
            F<ComboBox>(view, "_speedCombo").SelectedIndex = 3;     // 4
            F<ComboBox>(view, "_densityCombo").SelectedIndex = 9;   // 8
            F<ComboBox>(view, "_dirCombo").SelectedIndex = 2;       // 180°
            prefs.LabelOffsetXMm.Should().BeApproximately(2.0, 0.01, "16 точек = 2 мм");
            prefs.LabelOffsetYMm.Should().BeApproximately(-1.0, 0.01);
            prefs.LabelGapMm.Should().Be(3);
            prefs.LabelTsplSpeed.Should().Be(4);
            prefs.LabelTsplDensity.Should().Be(8);
            prefs.LabelTsplFlip.Should().BeTrue();

            // «Рамка» — поле шаблона, влияет на предпросмотр/печать
            var frame = F<CheckBox>(view, "_frameCheck");
            frame.IsChecked = true;
            F<LabelTemplate>(view, "_template").Frame.Should().BeTrue();

            // подпись под предпросмотром — в точках принтера
            F<ComboBox>(view, "_sizeCombo").SelectedIndex = 2; // 58×40
            F<LabelTemplate>(view, "_template").WidthMm.Should().Be(58, "выбран размер 58×40");
            Call(view, "RefreshPreview");
            for (var i = 0; i < 40 && !(F<TextBlock>(view, "_previewDots").Text ?? "").Contains("463×319"); i++)
                UiShot.Settle(2, 60); // таймер предпросмотра 120 мс
            var dots = F<TextBlock>(view, "_previewDots").Text!;
            dots.Should().Contain("463×319").And.Contain("203 dpi");

            // нет текстовых обрезок во всём окне
            var clipped = TextClipChecker.Find(window, $"tagsetup@{w}x{h}", lang.ToString());
            var cat = F<ComboBox>(view, "_categoryCombo");
            clipped.Should().BeEmpty("текст не обрезается: " + string.Join("; ", clipped.Select(v => v.Detail)) + $" [категории: ширина {cat.Bounds.Width:0.#}, MinWidth {cat.MinWidth}, окно {view.Bounds.Width:0.#}]");
            SaveShot(window, $"tagsetup-tspl-{lang}-{w}x{h}", new UiSize(w, h));

            // «Сбросить настройки» возвращает значения по умолчанию (и поля блока)
            Call(view, "ResetPrinterSetup");
            prefs.LabelOffsetXMm.Should().Be(0);
            prefs.LabelGapMm.Should().Be(2);
            prefs.LabelTsplSpeed.Should().BeNull();
            prefs.LabelTsplFlip.Should().BeNull();
            F<NumericUpDown>(view, "_offXBox").Value.Should().Be(0);
            F<LabelTemplate>(view, "_template").Frame.Should().BeFalse();

            // принтер через драйвер Windows: параметров TSPL нет (кнопки были бы без эффекта), сдвиги и рамка остаются
            F<ComboBox>(view, "_printerCombo").SelectedIndex = 1;
            UiShot.Settle(4, 30);
            tspl.IsVisible.Should().BeFalse("у принтера через драйвер зазор/скорость/плотность задаёт драйвер");
            F<Button>(view, "_calibrateButton").IsVisible.Should().BeFalse();
            F<NumericUpDown>(view, "_offXBox").IsEffectivelyVisible.Should().BeTrue("сдвиги действуют и при печати через драйвер");
            F<CheckBox>(view, "_frameCheck").IsEffectivelyVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>«Сколько на товар»: по одной (+ «Копий каждой») / по остатку (с потолком 99) / вручную (1, число в карточке); подпись «Товаров: N · этикеток: M».</summary>
    [AvaloniaFact]
    public async Task CopiesModes_CountLabels_AndCaptionShowsProductsAndLabels()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(5, 3, 250);
        var s = BarcodePrintSettingsStore.Load();
        s.CopiesMode = "default";
        s.DefaultCopies = 1;
        BarcodePrintSettingsStore.Save(s);
        var (window, view) = Open(1366, 768);
        try
        {
            var combo = F<ComboBox>(view, "_copiesModeCombo");
            var box = F<NumericUpDown>(view, "_copiesBox");
            combo.Items.Count.Should().Be(3, "по одной / по остатку / вручную");
            view.SelectFound();
            UiShot.Settle(4, 30);
            view.SelectedCount.Should().Be(3);
            UiDrive.AllText(window).Should().Contain("Товаров: 3").And.Contain("этикеток: 3");

            box.Value = 2;
            view.BuildItems().Select(i => i.Copies).Should().Equal(2, 2, 2);
            UiDrive.AllText(window).Should().Contain("этикеток: 6");

            combo.SelectedIndex = 1; // по остатку
            UiShot.Settle(4, 30);
            box.IsEnabled.Should().BeFalse("«Копий каждой» только у режима «По одной»");
            view.BuildItems().Select(i => i.Copies).Should().Equal(5, 3, 99);
            UiDrive.AllText(window).Should().Contain("этикеток: 107");
            F<TextBlock>(view, "_status").Text.Should().Contain("99", "предупреждение: остаток больше потолка");

            combo.SelectedIndex = 2; // вручную
            UiShot.Settle(4, 30);
            box.IsEnabled.Should().BeFalse();
            view.BuildItems().Select(i => i.Copies).Should().Equal(1, 1, 1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>«Только отсканированные»: список — товары, отсканированные в этой сессии; пока ничего — понятная подсказка; режим в настройках не запоминается.</summary>
    [AvaloniaFact]
    public async Task ScannedFilter_ShowsOnlyScannedThisSession_AndSelectAllInListWorks()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(5, 5, 5, 5, 5);
        var (window, view) = Open(1366, 768);
        try
        {
            view.FoundCount.Should().Be(5);
            var buttons = F<Dictionary<BarcodeFilter, Button>>(view, "_filterButtons");
            buttons.Should().ContainKey(BarcodeFilter.Scanned);
            Click(buttons[BarcodeFilter.Scanned]);
            UiShot.Settle(4, 30);
            view.FoundCount.Should().Be(0, "в этой сессии ничего не сканировали");
            F<TextBlock>(view, "_emptyText").Text.Should().Contain("Сканер: добавлять в печать");

            F<HashSet<string>>(view, "_scannedIds").Add("c1");
            F<HashSet<string>>(view, "_scannedIds").Add("c3");
            Call(view, "ApplyFilter", false);
            UiShot.Settle(4, 30);
            view.FoundCount.Should().Be(2);
            view.SelectFound(); // «Выбрать все в списке»
            view.SelectedCount.Should().Be(2, "выбраны только отсканированные");
            UiDrive.AllText(window).Should().MatchRegex(@"(Выбрать все в списке|Весь список) \(2\)");

            Call(view, "SaveState");
            BarcodePrintSettingsStore.Load().LastFilter.Should().NotBe((int)BarcodeFilter.Scanned, "«Отсканированные» не запоминается между запусками");
            Click(buttons[BarcodeFilter.All]);
            UiShot.Settle(4, 30);
            view.FoundCount.Should().Be(5);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
