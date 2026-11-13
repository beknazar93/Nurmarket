using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13: окно «Настройки весов Rongta» после переделки — 4 вкладки («Подключение / Загрузка товаров / Штрих-код / Дата и параметры»),
/// крупно текущий тип и пример 1265,00 сом, предупреждение для типа 02 с кнопкой «Исправить: тип 07», без поля «TCP-порт для проверки»; текст не обрезан
/// на 1024×600 и 1366×768 (ru, ky). На весы окно ничего не шлёт (кнопки записи не нажимаются). Снимки для глаз:
/// %TEMP%\nurmarket-tests\ui-actual\rongta-settings-{вкладка}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class RongtaScaleSettingsUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static Button? VisibleFixButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && (UiDrive.TextOf(b) ?? "").Contains("Исправить: тип 07"));

    /// <summary>2026-11-13 (живая проверка на RLS1000: при типе 27 кнопки «Исправить» не было — она показывалась только для типов по сумме): кнопка «Исправить: тип 07 (рекомендуется)»
    /// видна для типа 02 (предел суммы), 27 (касса не узнаёт «00…»), 10 (без веса и суммы); не видна при 07 и при типе по весу, который касса читает (17).
    /// В списке типов 07 — первая строка с пометкой «★ рекомендуется», 00–09 в списке есть. Нажатие кнопки выбирает 07 и запоминает его (в карточки и в «выбранный тип»).</summary>
    [AvaloniaTheory]
    [InlineData(2, true)]
    [InlineData(27, true)]
    [InlineData(10, true)]
    [InlineData(7, false)]
    [InlineData(17, false)]
    public async Task FixButton_ShownForAnyTypeTheTillCannotRead_AndPickerStartsWith07(int type, bool fixVisible)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.RongtaScaleIp = "192.168.1.87";
        prefs.RongtaBarcodeType = type;
        prefs.RongtaServerBarcodeType = null;
        prefs.RongtaLanBarcodePrefix = 20;
        prefs.RongtaPricePoint = 2;
        var window = new RongtaScaleSettingsWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.ShowBarcodeTab();
            UiShot.Settle(8);
            (VisibleFixButton(window) is not null).Should().Be(fixVisible, $"кнопка «Исправить: тип 07» при типе {type:00}");
            UiDrive.AllText(window).Should().Contain("рекомендуем тип 07", "крупная рекомендация видна при любом типе");

            var combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemCount == RongtaBarcodeFormat.Types.Count);
            var items = combo.Items.Cast<string>().ToList();
            items[0].Should().StartWith("07").And.Contain("★", "07 первым и с пометкой");
            items.Take(11).Select(i => i[..2]).Should().Contain(new[] { "00", "01", "02", "03", "04", "05", "06", "08", "09" }, "00–09 рядом с началом списка");
            combo.SelectedItem.Should().Be(items[items.FindIndex(i => i.StartsWith($"{type:00}"))], "выбран текущий тип");

            if (fixVisible)
            {
                VisibleFixButton(window)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                UiShot.Settle(8);
                prefs.RongtaBarcodeType.Should().Be(7, "кнопка выбрала 07");
                prefs.RongtaServerBarcodeType.Should().Be(7, "07 уходит в весовые карточки");
                VisibleFixButton(window).Should().BeNull("после исправления кнопки нет");
                UiDrive.AllText(window).Should().Contain("✓", "рекомендуемый тип выбран");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-13: тексты плашки после записи типа — что проверить на весах и запасной путь, если весы не ответили по порту 5001 (журнал обмена называется).</summary>
    [AvaloniaFact]
    public async Task CheckBanners_NameScaleStepsAndExchangeLog()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var ok = NurMarketKassa.AvaloniaHost.Services.RongtaBarcodeTypeWriter.CheckHint(7);
        ok.Should().Contain("[PLU]").And.Contain("Bar Code Type").And.Contain("07").And.Contain("отсканируйте");
        var bad = NurMarketKassa.AvaloniaHost.Services.RongtaBarcodeTypeWriter.NoReplyHint(7);
        bad.Should().Contain("[SETTING]").And.Contain("barcode type: set all barcodes' type").And.Contain("rongta-udp-exchange.log").And.Contain("5001");
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            NurMarketKassa.AvaloniaHost.Services.RongtaBarcodeTypeWriter.CheckHint(7).Should().NotBe(ok, $"плашка переведена ({lang})").And.Contain("[PLU]");
            NurMarketKassa.AvaloniaHost.Services.RongtaBarcodeTypeWriter.NoReplyHint(7).Should().NotBe(bad, $"запасной путь переведён ({lang})").And.Contain("rongta-udp-exchange.log");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task FourTabs_CurrentTypeWarning_FixButton_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        prefs.RongtaScaleIp = "192.168.1.87";
        prefs.RongtaBarcodeType = 2;
        prefs.RongtaServerBarcodeType = null;
        prefs.RongtaLanBarcodePrefix = 20;
        prefs.RongtaPricePoint = 2;
        var window = new RongtaScaleSettingsWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.ItemCount.Should().Be(4, $"четыре вкладки ({tag})");

            var names = new[] { "connection", "upload", "barcode", "date" };
            for (var i = 0; i < 4; i++)
            {
                tabs.SelectedIndex = i;
                UiShot.Settle(8);
                var text = UiDrive.AllText(window);
                if (i == 0)
                    text.Should().NotContain("TCP-порт для проверки", "устаревшее поле убрано");
                if (i == 2)
                {
                    text.Should().Contain("1265", $"пример суммы ≥ 1000 ({tag})");
                    var fix = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && (UiDrive.TextOf(b) ?? "").Contains("07"));
                    fix.Should().NotBeNull($"кнопка исправления на тип 07 видна при типе 02 ({tag})");
                    // 2026-11-13: крупная рекомендация «Для Rongta рекомендуем тип 07 — проверено».
                    text.Should().Contain("07", $"рекомендация типа 07 ({tag})");
                    text.Should().Contain("★", $"пометка рекомендации ({tag})");
                }
                var clipped = TextClipChecker.Find(window, "rongta-settings-" + names[i], langCode);
                clipped.Should().BeEmpty($"текст не обрезан ({tag}, {names[i]}): " + string.Join("; ", clipped.Select(v => v.Detail)));

                using var shot = UiShot.Render(window);
                using var image = SKImage.FromBitmap(shot);
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"rongta-settings-{names[i]}-{langCode}-{width}x{height}.png"), data.ToArray());
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
