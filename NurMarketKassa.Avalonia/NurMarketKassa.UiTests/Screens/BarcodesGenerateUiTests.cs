using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (владелец: «а где генерация штрихкода, если их нет?»): кнопка «Сгенерировать штрихкоды (N)» в «Штрихкоды и ценники» (Headless, сервер и ожидание подменены):
/// счётчик N по списку, видимость только при N &gt; 0, подсветка при фильтре «Без штрихкода», неактивна без права/связи, подтверждение с примером и списком, проход пакетами, итог «Создано N, пропущено M, ошибок K»,
/// «Распечатать созданные», пропуск весовых, 15 000 товаров без тормозов, снимки панели без среза (1024×600 и 1366×768, ru и ky). Снимки: %TEMP%\nurmarket-tests\ui-actual\barcodes-gen-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BarcodesGenerateUiTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();
    private readonly Dictionary<string, string> _server = new();
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;

    public BarcodesGenerateUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        BulkBarcodeGenerator.ReadBarcodeOverride = (id, _) => Task.FromResult<string?>(_server.GetValueOrDefault(id) ?? "");
        BulkBarcodeGenerator.SaveOverride = (id, code, _) => { _server[id] = code; return Task.FromResult<string?>(null); };
        BulkBarcodeGenerator.RefreshOverride = _ => Task.CompletedTask;
        BulkBarcodeGenerator.DelayOverride = (_, _) => Task.CompletedTask;
        BarcodePrintView.GenerateAllowedOverride = () => true;
    }

    public void Dispose()
    {
        BulkBarcodeGenerator.ReadBarcodeOverride = null;
        BulkBarcodeGenerator.SaveOverride = null;
        BulkBarcodeGenerator.RefreshOverride = null;
        BulkBarcodeGenerator.DelayOverride = null;
        BarcodePrintView.GenerateAllowedOverride = null;
        BarcodePrintWindow.ConfirmOverride = null;
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    private static void Seed(int withCode, int withoutCode, int weighed = 0)
    {
        CatalogCacheService.Products.Clear();
        var list = new List<CatalogProductTileVm>();
        for (var i = 0; i < withCode; i++)
            list.Add(new CatalogProductTileVm("c" + i, "С кодом " + i, "10.00 сом", false) { Barcode = (4600000000000L + i).ToString(), Unit = "шт", Quantity = 5 });
        for (var i = 0; i < withoutCode; i++)
            list.Add(new CatalogProductTileVm("n" + i, "Без кода " + i, "10.00 сом", false) { Unit = "шт", Quantity = 5 });
        for (var i = 0; i < weighed; i++)
            list.Add(new CatalogProductTileVm("w" + i, "Весовой " + i, "100.00 сом", true) { Unit = "кг", Quantity = 5 });
        CatalogCacheService.Products.AddRange(list);
    }

    private static (Window Window, BarcodePrintView View) Open(int w, int h)
    {
        var view = new BarcodePrintView(false, true);
        var window = new Window { Width = w, Height = h, Content = view };
        window.Show();
        UiShot.Settle(4, 20);
        return (window, view);
    }

    private static Button GenButton(BarcodePrintView view) =>
        view.GetVisualDescendants().OfType<Button>().First(b => (UiDrive.TextOf(b) ?? "") is var t && (t.StartsWith("Сгенерировать штрихкоды") || t.StartsWith("Создать коды") || t.StartsWith("Штрих-коддорду түзүү") || t.StartsWith("Коддорду түзүү")));

    [AvaloniaFact]
    public async Task Button_ShowsMissingCount_OnlyWhenThereAreProductsWithoutBarcode_AndHighlightsOnFilter()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(withCode: 5, withoutCode: 7, weighed: 2);
        var (window, view) = Open(1366, 768);
        try
        {
            view.MissingBarcodeCount.Should().Be(9, "7 штучных и 2 весовых без штрихкода");
            var button = GenButton(view);
            button.IsVisible.Should().BeTrue();
            UiDrive.TextOf(button).Should().Contain("(9)");
            button.IsEnabled.Should().BeTrue();
            button.Classes.Should().NotContain("sec-primary", "при фильтре «Все» кнопка обычная");
            // фильтр «Без штрихкода» — подсвечена
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Без штрихкода"));
            UiShot.Settle(3, 20);
            button.Classes.Should().Contain("sec-primary", "при фильтре «Без штрихкода» кнопка подсвечена");
            Avalonia.Controls.ToolTip.GetTip(button)!.ToString().Should().Contain("нет штрихкода");
            // фильтр «Со штрихкодом» — все с кодом, кнопки нет
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Со штрихкодом"));
            UiShot.Settle(3, 20);
            button.IsVisible.Should().BeFalse("N = 0 — кнопки нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Button_IsDisabled_WithoutRightOrConnection_WithHint()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(2, 4);
        BarcodePrintView.GenerateAllowedOverride = () => false;
        var (window, view) = Open(1366, 768);
        try
        {
            var button = GenButton(view);
            button.IsEnabled.Should().BeFalse();
            var hint = Avalonia.Controls.ToolTip.GetTip(button)!.ToString()!;
            (hint.Contains("права") || hint.Contains("связи")).Should().BeTrue("подсказка объясняет, почему кнопка неактивна: " + hint);
            await view.GenerateAsync();
            view.LastGenerateResult.Should().BeNull("без права проход не стартует");
            _server.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Generate_ConfirmsWithExampleAndList_RunsAndReports_SkipsWeighed_PrintsCreated()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(withCode: 3, withoutCode: 25, weighed: 2);
        string? asked = null;
        BarcodePrintWindow.ConfirmOverride = q => { asked = q; return Task.FromResult(true); };
        var (window, view) = Open(1366, 768);
        try
        {
            await view.GenerateAsync();
            UiShot.Settle(3, 20);
            asked.Should().NotBeNull();
            asked.Should().Contain("25 товаров").And.Contain("Пример кода:").And.Contain("• Без кода 0").And.Contain("… и ещё 15").And.Contain("Пропустим 2");
            var result = view.LastGenerateResult!;
            result.Created.Should().HaveCount(25);
            result.Skipped.Should().HaveCount(2, "два весовых");
            _server.Values.Should().OnlyHaveUniqueItems();
            UiDrive.AllText(window).Should().Contain("Создано 25, пропущено 2, ошибок 0.");
            view.MissingBarcodeCount.Should().Be(2, "остались только весовые");
            // «Распечатать созданные»
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Распечатать созданные"));
            UiShot.Settle(4, 20);
            view.SelectedCount.Should().Be(25);
            view.FoundCount.Should().Be(25);
            SaveShot(window, "barcodes-gen-after-1366x768-ru", 1366, 768);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Generate_Declined_ChangesNothing()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        Seed(1, 3);
        BarcodePrintWindow.ConfirmOverride = _ => Task.FromResult(false);
        var (window, view) = Open(1366, 768);
        try
        {
            await view.GenerateAsync();
            _server.Should().BeEmpty();
            view.LastGenerateResult.Should().BeNull();
            view.MissingBarcodeCount.Should().Be(3);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task MissingCount_On15000_IsInstant()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(withCode: 10000, withoutCode: 5000);
        var (window, view) = Open(1366, 768);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                view.SelectPage(); // каждый вызов пересчитывает панель (UpdateSelectionUi → N)
            sw.Stop();
            view.MissingBarcodeCount.Should().Be(5000);
            (sw.ElapsedMilliseconds / 20.0).Should().BeLessThan(60, "пересчёт N на 15 000 товаров вместе с панелью — десятки миллисекунд, не секунды");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Toolbar_WithGenerateButton_IsNotClipped(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        Seed(withCode: 6, withoutCode: 6);
        BarcodePrintWindow.ConfirmOverride = _ => Task.FromResult(true);
        var (window, view) = Open(w, h);
        try
        {
            await view.GenerateAsync(); // после прохода видны и «Распечатать созданные», и «Скопировать журнал»
            UiShot.Settle(4, 20);
            var right = view.TranslatePoint(new Point(view.Bounds.Width, 0), window)!.Value.X;
            foreach (var c in view.GetVisualDescendants().OfType<Control>().Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 0 && x is Button or TextBox or ComboBox or CheckBox))
                c.TranslatePoint(new Point(c.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(right + 1, $"{c.GetType().Name} «{UiDrive.TextOf(c)}» не срезан справа ({w}x{h} {lang})");
            SaveShot(window, $"barcodes-gen-toolbar-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}", w, h);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void SaveShot(Window window, string name, int w, int h)
    {
        using var shot = UiShot.Render(window, new UiSize(w, h));
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }
}
