using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (Д-5, оценка UX/UI): языки и числа. Проверки с настоящими условиями (могут упасть): в ky/uz на экранах нет русских слов из словаря
/// (фильтр, воронка), названия этапов воронки меняются вместе с языком без перезапуска, статусы журнала сообщений переведены, вес в окне взвешивания
/// с запятой, вкладки каталога в ky/uz на 1024×600 не уходят за край, у «Входа» одно слово в подписи и в поле, таблица «Истории покупок» без горизонтальной прокрутки.</summary>
public sealed class LanguagesAndNumbersD5UiTests
{
    private static readonly string[] RussianWords =
    [
        "Все товары", "Все категории", "Все бренды", "Весовые", "Штучные", "Избранные",
        "Новые обращения", "В работе", "Договорились", "Оплачено", "Отказ",
    ];

    private static IEnumerable<string> VisibleTexts(Window w)
    {
        foreach (var t in w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
            yield return t.Text!;
        // Содержимое-строка учитывается, только если у элемента нет шаблона содержимого (иначе на экране рисуется шаблон, а не сырая строка).
        foreach (var b in w.GetVisualDescendants().OfType<ContentControl>().Where(c => c.IsEffectivelyVisible && c.Content is string && c.ContentTemplate == null))
            yield return (string)b.Content!;
    }

    private static void AssertNoRussian(Window w, string screen, AppLanguage lang)
    {
        var bad = VisibleTexts(w).Where(t => RussianWords.Any(r => t.Contains(r, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        Assert.True(bad.Count == 0, $"{screen} ({lang}): русские слова на экране: " + string.Join("; ", bad));
    }

    public static IEnumerable<object[]> NonRussian() => [[AppLanguage.Kyrgyz], [AppLanguage.Uzbek]];

    [AvaloniaTheory]
    [MemberData(nameof(NonRussian))]
    public async Task FilterWindow_HasNoRussianWords(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new FilterWindow();
        try
        {
            using var _ = UiShot.Render(window);
            AssertNoRussian(window, "FilterWindow", lang);
            // Контроль теста: комбо действительно показывают значение (иначе проверка пуста).
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(NonRussian))]
    public async Task Funnel_StageTitles_FollowLanguageWithoutRestart(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        // Сначала русский (как будто программа запущена на русском), потом переключаем язык: названия этапов обязаны смениться.
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var first = new FunnelWindow();
        using (var _ = UiShot.Render(first)) { }
        first.Close();
        UiShot.Pump();

        UiShot.Setup(UiTheme.Light, lang);
        var window = new FunnelWindow();
        try
        {
            using var _ = UiShot.Render(window);
            AssertNoRussian(window, "FunnelWindow", lang);
            Assert.Contains(VisibleTexts(window), t => t.Contains('·')); // заголовки этапов «Название · N» на месте
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public async Task BotMessages_StatusesAndKinds_AreTranslated()
    {
        await TestHost.WarmAsync();
        var t = typeof(BotMessagesWindow);
        var status = t.GetMethod("StatusText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var kind = t.GetMethod("KindText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            foreach (var s in new[] { "sent", "deferred", "skipped", "failed", "queued" })
                Assert.NotEqual(s, (string)status.Invoke(null, [s])!);
            Assert.NotEqual("receipt", (string)kind.Invoke(null, ["receipt"])!);
        }
        UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
        Assert.Equal("Кечиктирилди", (string)status.Invoke(null, ["deferred"])!);
        Assert.Equal("новый-статус", (string)status.Invoke(null, ["новый-статус"])!); // неизвестное слово сервера — как есть
    }

    [AvaloniaTheory]
    [MemberData(nameof(NonRussian))]
    public async Task WeighedDialog_WeightUsesComma(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new WeighedProductDialog();
        try
        {
            using var _ = UiShot.Render(window);
            var box = window.FindControl<TextBox>("WeightBox")!;
            Assert.Equal("0,00", box.Text);
            var live = window.FindControl<TextBlock>("LiveScaleText")!;
            Assert.DoesNotContain('.', live.Text ?? "");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    public static IEnumerable<object[]> CatalogCases()
    {
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Russian })
            yield return [lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(CatalogCases))]
    public async Task MainWindow_CatalogTabs_FitAt1024_AndCartButtonsAreReadable(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = ScreenCatalog.Get("main-kassa").Create();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(10, 40);
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Classes.Contains("catalog-tabs-scroll") && s.IsEffectivelyVisible);
            Assert.True(scroll != null, "не найдена полоса вкладок каталога");
            Assert.True(scroll!.Extent.Width <= scroll.Viewport.Width + 1.5,
                $"вкладки каталога ({lang}) шире панели: нужно {scroll.Extent.Width:0.#} px, есть {scroll.Viewport.Width:0.#} px — «Кызматта…» уходит за край");

            var cartButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("cart-action") && b.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(cartButtons);
            Assert.All(cartButtons, b =>
            {
                Assert.True(b.Bounds.Height >= 40, $"кнопка под чеком ниже 40 px: {b.Bounds.Height}");
                Assert.True(b.FontSize >= 12.5, $"шрифт кнопки под чеком {b.FontSize} < 12,5");
            });
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(CatalogCases))]
    public async Task ClientPurchases_NoHorizontalScrollAt1024(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = new ClientPurchasesWindow("1", "Тест");
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(10, 40);
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First();
            var hbar = grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
            Assert.True(hbar == null || !hbar.IsEffectivelyVisible || hbar.Maximum <= 0.5,
                $"в таблице «Истории покупок» ({lang}) горизонтальная прокрутка, запас {hbar?.Maximum:0.#} px");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(CatalogCases))]
    public async Task Login_LabelAndFieldUseTheSameWord(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = (LoginWindow?)TestHost.Services.GetService(typeof(LoginWindow)) ?? new LoginWindow();
        try
        {
            using var _ = UiShot.Render(window);
            var emailBox = window.FindControl<TextBox>("EmailBox")!;
            var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("LoginFieldLabel") && !string.IsNullOrEmpty(t.Text));
            Assert.Equal(label.Text, emailBox.Watermark);
        }
        finally { window.Close(); UiShot.Pump(); }
    }
}
