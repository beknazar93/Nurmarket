using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using System.Globalization;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-07 (живая проверка 1.17.81, пп. 1–7): проверки исправлений вида и точек входа — автосписок каталога (цена не обрезается), панель приёмки при 1024,
/// окно настроек весов по марке, подпись «Весовой», строка «в долг» под плиткой «Чеки». Окна — настоящие, в Avalonia.Headless.</summary>
[Collection("UiScenarios")]
public sealed class ScreenFixesTests
{
    // ───────────── 1. автосписок каталога: цена не обрезается ─────────────

    /// <summary>Ширина окна → панель каталога: 330 — окно 1024 при масштабе 150 %, 430 — 1366 при 150 %, 510 — 1024 при 100 %, 640/800/1000 — шире.</summary>
    public static IEnumerable<object[]> ListWidths() =>
        new[] { 400.0, 430, 510, 640, 800, 1000 }.SelectMany(w => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { w, l }));

    [AvaloniaTheory]
    [MemberData(nameof(ListWidths))]
    public void CatalogList_PriceAndCurrency_AreNeverClipped(double width, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var built = CatalogListFixture.Create(width);
        try
        {
            using var _ = UiShot.Render(built.Window);
            var list = built.View.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TableListBox");
            list.IsEffectivelyVisible.Should().BeTrue("окно уже 1280 — каталог сам становится списком");

            var prices = list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tbl-price-amount") && t.IsEffectivelyVisible).ToList();
            prices.Should().HaveCountGreaterThanOrEqualTo(4, "все четыре товара в списке");
            foreach (var price in prices)
            {
                var row = price.GetVisualAncestors().OfType<Border>().First(b => b.ClipToBounds);
                var typeface = new Typeface(price.FontFamily, price.FontStyle, price.FontWeight, price.FontStretch);
                var need = new FormattedText(price.Text ?? "", CultureInfo.CurrentUICulture, price.FlowDirection, typeface, price.FontSize, null).Width;
                price.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 1, $"цена «{price.Text}» не должна сжиматься (окно {width})");
                var right = price.TranslatePoint(new Point(price.Bounds.Width, 0), row)!.Value.X;
                right.Should().BeLessThanOrEqualTo(row.Bounds.Width + 0.5, $"цена «{price.Text}» не должна уходить за край строки (окно {width})");
            }

            // «сом» рядом с ценой (если показан) тоже внутри строки
            foreach (var cur in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tbl-currency") && t.IsEffectivelyVisible))
            {
                var row = cur.GetVisualAncestors().OfType<Border>().First(b => b.ClipToBounds);
                cur.TranslatePoint(new Point(cur.Bounds.Width, 0), row)!.Value.X.Should().BeLessThanOrEqualTo(row.Bounds.Width + 0.5);
            }

            // Остаток — под ценой (узкий) или в своей колонке — без обрезки; название сжимается многоточием.
            foreach (var stock in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tbl-stock") && t.IsEffectivelyVisible))
            {
                var row = stock.GetVisualAncestors().OfType<Border>().First(b => b.ClipToBounds);
                stock.TranslatePoint(new Point(stock.Bounds.Width, 0), row)!.Value.X.Should().BeLessThanOrEqualTo(row.Bounds.Width + 0.5);
            }
            list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == "Алма").Should().OnlyContain(t => t.TextTrimming == TextTrimming.CharacterEllipsis);
        }
        finally
        {
            built.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(400.0, true, true)]
    [InlineData(430.0, true, true)]
    [InlineData(510.0, true, false)]
    [InlineData(900.0, false, false)]
    public void CatalogList_Density_FollowsWidth(double width, bool narrow, bool tight)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var built = CatalogListFixture.Create(width);
        try
        {
            using var _ = UiShot.Render(built.Window);
            var root = built.View.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CatalogRoot");
            root.Classes.Contains("tbl-narrow").Should().Be(narrow);
            root.Classes.Contains("tbl-tight").Should().Be(tight);
        }
        finally
        {
            built.Dispose();
        }
    }

    [Fact]
    public void TableDensity_HasHysteresis_NoFlicker()
    {
        CatalogPanelView.TableDensity(759, false, false).Should().Be((true, false));
        CatalogPanelView.TableDensity(765, false, false).Should().Be((false, false), "вошли в широкий режим на 760");
        CatalogPanelView.TableDensity(765, true, false).Should().Be((true, false), "из узкого выходим только после 784");
        CatalogPanelView.TableDensity(790, true, false).Should().Be((false, false));
        CatalogPanelView.TableDensity(395, false, false).Should().Be((true, true));
        CatalogPanelView.TableDensity(405, false, true).Should().Be((true, true), "из тесного выходим только после 424");
        CatalogPanelView.TableDensity(430, false, true).Should().Be((true, false));
    }

    // ───────────── 6. «Весовой» только во «Все товары» и в поиске ─────────────

    [AvaloniaFact]
    public void CatalogList_TypeBadge_OnlyInAllTab()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var all = CatalogListFixture.Create(640, tab: 0);
        try
        {
            using var _ = UiShot.Render(all.Window);
            BadgeCount(all.View).Should().BeGreaterThan(0, "во «Все товары» подпись типа показана");
        }
        finally
        {
            all.Dispose();
        }

        var weighted = CatalogListFixture.Create(640, tab: 1);
        try
        {
            using var _ = UiShot.Render(weighted.Window);
            weighted.Vm.Products.Should().NotBeEmpty();
            BadgeCount(weighted.View).Should().Be(0, "во вкладке «Весовые» подпись «Весовой» избыточна");
        }
        finally
        {
            weighted.Dispose();
        }

        static int BadgeCount(CatalogPanelView view) =>
            view.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TableListBox").GetVisualDescendants().OfType<TextBlock>()
                .Count(t => t.IsEffectivelyVisible && (t.Text == "Весовой" || t.Text == "Штучный" || t.Text == "Штучный + Поштучно"));
    }

    // ───────────── 2. приёмка при 1024: поле скана и «Очистить» читаемы ─────────────

    [AvaloniaTheory]
    [InlineData(1024, 640)]
    [InlineData(1100, 700)]
    [InlineData(1366, 768)]
    public async Task PurchasesReceive_ScanField_AndClearButton_FitAtNarrowWidth(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(null);
            UiShot.Settle();
            var scan = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark is { } w && w.Contains("рихкод", StringComparison.OrdinalIgnoreCase));
            scan.Bounds.Width.Should().BeGreaterThanOrEqualTo(250, $"поле скана читаемо при ширине окна {width}");
            var clear = UiDrive.ButtonByText(window, "Очистить список товаров");
            var right = clear.TranslatePoint(new Point(clear.Bounds.Width, 0), window)!.Value.X;
            right.Should().BeLessThanOrEqualTo(window.Bounds.Width, "«Очистить» целиком в окне");
            clear.Bounds.Width.Should().BeGreaterThan(40);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 3. окно «Настройки весов» — по марке ─────────────

    // ScaleUi — внутренний класс приложения; вызываем через отражение (приложение для теста не менялось).
    private static Type? SettingsWindowTypeFor(string brand) =>
        (Type?)typeof(PurchasesWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi")!
            .GetMethod("SettingsWindowTypeFor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, [brand]);

    [Theory]
    [InlineData("shtrikhpc", typeof(ShtrikhPcSettingsWindow))]
    [InlineData("rongta", typeof(RongtaScaleSettingsWindow))]
    [InlineData("tm", typeof(TmScaleSettingsWindow))]
    [InlineData("shtrikh", typeof(ShtrikhScaleSettingsWindow))]
    [InlineData("что-то-неизвестное", typeof(ShtrikhScaleSettingsWindow))]
    public void ScaleSettings_WindowByBrand(string brand, Type expected) =>
        SettingsWindowTypeFor(brand).Should().Be(expected);

    [Fact]
    public void ScaleSettings_AiScales_HaveNoDeviceWindow() => SettingsWindowTypeFor("ai").Should().BeNull();

    [AvaloniaFact]
    public void ScaleSettings_Rs200Window_OpensOnBarcodeTab_AndShowsAllTabs()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new ShtrikhPcSettingsWindow();
        try
        {
            window.ShowBarcodeTab();
            using var _ = UiShot.Render(window);
            var tabs = UiDrive.Find<TabControl>(window, "Tabs");
            tabs.Items.Count.Should().Be(8);
            tabs.SelectedIndex.Should().Be(2);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [Theory]
    [InlineData(2, "99 999,99")]
    [InlineData(0, "9 999 999")]
    public void Rs200_ExportHint_NamesPriceLimit(int decimals, string max)
    {
        var text = ScalesPluWindow.ShtrikhPcPriceLimitHint(decimals);
        Norm(text).Should().Contain(max).And.Contain($"{decimals} знаках");
    }

    // ───────────── 7. строка «в долг» под плиткой «Чеки» ─────────────

    /// <summary>Неразрывные пробелы разрядов (U+00A0, U+202F) → обычные: так проще сравнивать «99 999,99».</summary>
    private static string Norm(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    [Fact]
    public void SalesTiles_DebtReceipts_AreCountedSeparately()
    {
        var receipts = new (string? Status, decimal Total)[] { ("paid", 60m), ("debt", 60m), ("debt", 60m), ("", 60m), ("partially_returned", 120m) };
        var (count, amount) = SalesTileRules.OpenDebt(receipts);
        count.Should().Be(2);
        amount.Should().Be(120m);
        Norm(SalesTileRules.DebtNote(count, amount)).Should().Contain("в долг: 2 на 120,00 сом");
        SalesTileRules.DebtNote(0, 0m).Should().BeEmpty("долговых чеков нет — строки нет");
        SalesTileRules.IsOpenDebt("DEBT").Should().BeTrue();
        SalesTileRules.IsOpenDebt("paid").Should().BeFalse("оплаченный долг входит в плитки как обычный чек");
        SalesTileRules.IsOpenDebt(null).Should().BeFalse();
    }
}
