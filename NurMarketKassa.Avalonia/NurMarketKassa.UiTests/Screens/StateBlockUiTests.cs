using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (оценка UX/UI, пакет Д-2): общий блок состояния (StateBlock) — «загрузка / пусто / ошибка с «Повторить» / сервер недоступен».
/// Проверки: правильный вариант для каждого случая, «Повторить» вызывает действие, в тексте нет сырого ответа сервера (он только под «Подробнее»),
/// текст не обрезан (TextClipChecker, ru и ky, 1024×600 и 1366×768), а окна из списка при сбое показывают РОВНО ОДИН блок и не повторяют ошибку.</summary>
[Collection("UiScenarios")]
public sealed class StateBlockUiTests
{
    private static readonly string[] Forbidden = ["Status:", "Exception", "mapping", "{", "}", "StatusCode"];

    private static IReadOnlyList<string> VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text!).ToList();

    private static Window Host(Control block) => new() { Content = new Border { Child = block } };

    private static Border TheBlock(Control root) =>
        root.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("state-block"));

    private static ApiException RawServerError() =>
        new("Status: 500 {\"detail\":\"mapping error at Server.Exception\"}", 400, System.Text.Json.JsonDocument.Parse("{\"mapping\":[\"Status: 500\"]}").RootElement);

    [AvaloniaFact]
    public void Loading_ShowsProgressAndNoButtons()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Host(StateBlock.Loading());
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            var block = TheBlock(window);
            block.Tag.Should().Be(StateKind.Loading);
            window.GetVisualDescendants().OfType<ProgressBar>().Should().ContainSingle();
            window.GetVisualDescendants().OfType<Button>().Should().BeEmpty("при загрузке «Повторить» не нужна");
            VisibleTexts(window).Should().ContainSingle().Which.Should().Contain("Загружаю");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void Empty_ShowsHintWithoutRetryOrProgress()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Host(StateBlock.Empty("Долгов нет", "Здесь появятся клиенты, которые купили в долг."));
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            TheBlock(window).Tag.Should().Be(StateKind.Empty);
            window.GetVisualDescendants().OfType<ProgressBar>().Should().BeEmpty();
            window.GetVisualDescendants().OfType<Button>().Should().BeEmpty("у «пусто» нет кнопки «Повторить»: это не сбой");
            VisibleTexts(window).Should().BeEquivalentTo(["Долгов нет", "Здесь появятся клиенты, которые купили в долг."]);
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void Error_ShowsTitleReasonAndRetry_ThatCallsTheAction()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var retried = 0;
        var window = Host(StateBlock.Failure(new Border(), RawServerError(), "Не удалось загрузить долги", () => retried++));
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            TheBlock(window).Tag.Should().Be(StateKind.Error);
            var retry = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, StateBlock.RetryText));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            retried.Should().Be(1, "«Повторить» вызывает переданное действие ровно один раз");
            VisibleTexts(window).Should().Contain("Не удалось загрузить долги");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void NetworkFailure_ShowsServerUnavailableCard()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Host(StateBlock.Failure(new Border(), new HttpRequestException("connection refused"), "Не удалось загрузить долги", () => { }));
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            TheBlock(window).Tag.Should().Be(StateKind.Offline);
            var texts = VisibleTexts(window);
            texts.Should().Contain(ServerOutageMonitor.UnavailableText);
            texts.Should().NotContain("Не удалось загрузить долги", "при сбое сети заголовок — «Сервер NurCRM недоступен», а не вторая фраза про то же самое");
            window.GetVisualDescendants().OfType<Button>().Should().Contain(b => Equals(b.Content, StateBlock.RetryText));
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public void Error_TextHasNoRawServerResponse_UntilDetailsAreOpened(AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var window = Host(StateBlock.Failure(new Border(), RawServerError(), "Не удалось загрузить долги", () => { }));
        try
        {
            using var _ = UiShot.Render(window, UiSize.Small);
            foreach (var text in VisibleTexts(window))
                foreach (var bad in Forbidden)
                    text.Should().NotContain(bad, "сырой ответ сервера на экран не выводится: «" + text + "»");

            // Подробности для техподдержки есть, но свёрнуты; по «Подробнее» показываются.
            var details = window.GetVisualDescendants().OfType<TextBlock>().Single(t => (t.Text ?? "").Contains("ApiException"));
            details.IsEffectivelyVisible.Should().BeFalse();
            var more = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, StateBlock.DetailsText));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Pump();
            details.IsEffectivelyVisible.Should().BeTrue();
            details.Text.Should().Contain("mapping", "техническая причина доступна под «Подробнее»");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    public static IEnumerable<object[]> ClipCases() =>
        new[] { (1024, 600), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.Item1, s.Item2, l }));

    [AvaloniaTheory]
    [MemberData(nameof(ClipCases))]
    public void AllVariants_FitAndAreNotClipped(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Dark, lang);
        var panel = new StackPanel
        {
            Children =
            {
                StateBlock.Loading(),
                StateBlock.Empty("Здесь пока пусто. Нажмите «＋ Новый филиал» — потом товар можно будет перемещать с главного склада в филиал.", null),
                StateBlock.Failure(new Border(), RawServerError(), "Не удалось загрузить перемещения", () => { }),
                StateBlock.Failure(new Border(), new HttpRequestException("x"), "Не удалось загрузить долги", () => { }),
            },
        };
        var window = new Window { Content = new ScrollViewer { Content = panel } };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var found = TextClipChecker.Find(window, "state-block", lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            found.Should().BeEmpty("текст блока состояния не обрезан: " + string.Join("; ", found.Select(v => v.Detail)));
            window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("state-block")).Should().Be(4);
            panel.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5, "блоки помещаются по ширине окна");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    /// <summary>Окно → какой блок оно показывает при недоступном сервере (заглушка UiFake). Список проверен по снимкам до/после; блок один, ошибка не повторяется.</summary>
    public static IEnumerable<object[]> Windows() =>
    [
        ["DebtsWindow", StateKind.Error, "Не удалось загрузить долги"],
        ["SuppliersWindow", StateKind.Error, "Не удалось загрузить поставщиков"],
        ["BranchesWindow", StateKind.Error, "Не удалось загрузить филиалы"],
        ["AppShopWindow", StateKind.Error, "Не удалось загрузить настройки магазина"],
        ["SiteReviewsWindow", StateKind.Error, "Не удалось загрузить отзывы"],
        ["SiteCollectionsWindow", StateKind.Error, "Не удалось загрузить коллекции"],
        ["RentalsWindow", StateKind.Error, "Не удалось загрузить прокат"],
        ["ClientsWindow", StateKind.Error, "Не удалось загрузить клиентов"],
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Windows))]
    public async Task Window_WhenServerFails_ShowsExactlyOneStateBlock_WithoutRawText(string typeName, StateKind expected, string title)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        var type = typeof(NurMarketKassa.AvaloniaHost.App).Assembly.GetTypes().Single(t => t.Name == typeName && typeof(Window).IsAssignableFrom(t));
        var window = TestHost.Services.GetService(type) as Window;
        if (window == null)
        {
            var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
            window = (Window)ctor.Invoke(ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
        }
        try
        {
            using var _ = UiShot.Render(window, UiSize.Large);
            UiShot.Settle(40, 50);
            var blocks = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("state-block") && b.IsEffectivelyVisible).ToList();
            blocks.Should().ContainSingle($"{typeName}: при сбое сервера на экране один блок состояния");
            blocks[0].Tag.Should().Be(expected);
            var texts = VisibleTexts(window);
            foreach (var text in texts)
                foreach (var bad in Forbidden)
                    text.Should().NotContain(bad, $"{typeName}: сырой ответ сервера на экране: «{text}»");
            if (expected == StateKind.Error)
                texts.Count(t => t.Contains(title)).Should().Be(1, $"{typeName}: «{title}» написано один раз, а не несколько");
            // «Сервер недоступен» не повторяется ни в блоке, ни рядом с ним (строка состояния, плашка снизу).
            if (expected == StateKind.Offline)
                texts.Count(t => t.Contains(ServerOutageMonitor.UnavailableText)).Should().Be(1, $"{typeName}: «{ServerOutageMonitor.UnavailableText}» написано один раз");
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }
}

/// <summary>Снимки «после» для пакетов Д-2 / Д-3 / Д-6: %TEMP%\nurmarket-tests\ux-d236\after (ru светлая 1366×768, ky тёмная 1024×600, ru тёмная 1024×600). Ничего не сравнивают,
/// кроме того, что окно поднялось и кадр не пустой (картинки смотрят глазами).</summary>
[Collection("UiScenarios")]
public sealed class StateBlockGalleryTests
{
    private static readonly string[] Names =
    [
        "DebtsWindow", "SuppliersWindow", "RentalsWindow", "BranchesWindow", "LossSalesWindow", "BonusAnalyticsWindow", "SizesReportWindow", "AppShopWindow",
        "ProfitCashReconcileWindow", "AbcAnalysisWindow", "SalaryWindow", "SiteReviewsWindow", "SiteCollectionsWindow", "ClientsWindow",
        "FinanceWindow", "SalesWindow", "PosSettingsWindow",
    ];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var n in Names)
        {
            yield return [n, UiTheme.Light, AppLanguage.Russian, 1366, 768];
            yield return [n, UiTheme.Dark, AppLanguage.Kyrgyz, 1024, 600];
            yield return [n, UiTheme.Dark, AppLanguage.Russian, 1024, 600];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Shot(string typeName, UiTheme theme, AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ux-d236", "after");
        Directory.CreateDirectory(dir);
        var type = typeof(NurMarketKassa.AvaloniaHost.App).Assembly.GetTypes().Single(t => t.Name == typeName && typeof(Window).IsAssignableFrom(t));
        var window = TestHost.Services.GetService(type) as Window;
        if (window == null)
        {
            var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
            window = (Window)ctor.Invoke(ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
        }
        try
        {
            using var first = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(30, 50);
            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var path = Path.Combine(dir, $"{typeName}-{UiShot.Slug(theme, lang)}-{width}x{height}.png");
            File.WriteAllBytes(path, data.ToArray());
            new FileInfo(path).Length.Should().BeGreaterThan(2000, "кадр не пустой");
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }
}
