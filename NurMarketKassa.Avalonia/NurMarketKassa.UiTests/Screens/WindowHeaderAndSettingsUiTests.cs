using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (оценка UX/UI, пакеты Д-3 и Д-6): у «Финансов» и «Продаж» одна шапка окна (заголовок слева, период под ним, справа Excel / Word / «Обновить» / «Назад»);
/// в «Настройках» на 1024×600 все вкладки видны без стрелок и не обрезаны, «Сохранить» видна.</summary>
[Collection("UiScenarios")]
public sealed class WindowHeaderAndSettingsUiTests
{
    private static T Svc<T>() where T : notnull => TestHost.Services.GetRequiredService<T>();

    private static Point At(Visual v, Window w) => v.TranslatePoint(new Point(0, 0), w) ?? throw new InvalidOperationException("элемент не на экране: " + v);

    public static IEnumerable<object[]> HeaderCases() =>
        new[] { (1366, 768), (1024, 600) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.Item1, s.Item2, l }));

    [AvaloniaTheory]
    [MemberData(nameof(HeaderCases))]
    public async Task FinanceAndSales_ShareOneHeader(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, lang);
        var finance = Svc<FinanceWindow>();
        var sales = Svc<SalesWindow>();
        try
        {
            using var f = UiShot.Render(finance, new UiSize(width, height));
            using var s = UiShot.Render(sales, new UiSize(width, height));
            UiShot.Settle(20, 40);

            var fTitle = finance.FindControl<TextBlock>("HeaderTitleText")!;
            var sTitle = sales.FindControl<TextBlock>("TitleText")!;
            fTitle.IsEffectivelyVisible.Should().BeTrue();
            sTitle.IsEffectivelyVisible.Should().BeTrue();
            Math.Abs(At(fTitle, finance).Y - At(sTitle, sales).Y).Should().BeLessThan(1.5, "заголовки стоят на одной высоте");
            // Заголовок слева, а не по центру (раньше у «Финансов» он был в средней трети шапки).
            At(fTitle, finance).X.Should().BeLessThan(width * 0.2);
            At(sTitle, sales).X.Should().BeLessThan(width * 0.2);

            // Одна кнопка выхода с одним названием.
            var fExit = finance.FindControl<Button>("ExitButton")!;
            var sBack = sales.FindControl<Button>("BackButton")!;
            fExit.Content.Should().Be(sBack.Content, "у обоих окон кнопка выхода называется одинаково");
            Math.Abs(At(fExit, finance).X + fExit.Bounds.Width - (At(sBack, sales).X + sBack.Bounds.Width)).Should().BeLessThan(1.5, "кнопка выхода у правого края в обоих окнах");
            Math.Abs(At(fExit, finance).Y - At(sBack, sales).Y).Should().BeLessThan(1.5);

            // Действия справа: тот же порядок и те же отступы (Excel, Word, «Обновить», «Назад»).
            var fActions = finance.FindControl<StackPanel>("HeaderActions")!.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
            var sActions = sales.FindControl<StackPanel>("HeaderActions")!.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
            fActions.Select(b => b.Content as string ?? "icon").Should().Equal(sActions.Select(b => b.Content as string ?? "icon"));
            for (var i = 0; i < fActions.Count; i++)
                Math.Abs(fActions[i].Bounds.Left - sActions[i].Bounds.Left).Should().BeLessThan(1.5, $"кнопка {i} стоит одинаково в обоих окнах");

            // Период — отдельной строкой ПОД заголовком и на одной вертикали в обоих окнах; поля поиска в шапке «Продаж» нет.
            var fPeriod = finance.FindControl<WrapPanel>("PeriodBar")!;
            var sPeriod = sales.FindControl<WrapPanel>("PeriodBar")!;
            At(fPeriod, finance).Y.Should().BeGreaterThan(At(fTitle, finance).Y + fTitle.Bounds.Height - 1);
            At(sPeriod, sales).Y.Should().BeGreaterThan(At(sTitle, sales).Y + sTitle.Bounds.Height - 1);
            Math.Abs(At(fPeriod, finance).X - At(sPeriod, sales).X).Should().BeLessThan(1.5, "период начинается у одного левого края");
            Math.Abs(At(fPeriod, finance).Y - At(sPeriod, sales).Y).Should().BeLessThan(1.5, "период стоит на одной высоте");
            var search = sales.FindControl<TextBox>("SearchReceiptBox")!;
            search.GetVisualAncestors().Should().NotContain(sales.FindControl<StackPanel>("HeaderActions")!, "поиск чека ушёл из шапки во вкладку «Чеки»");

            foreach (var (window, name) in new (Window, string)[] { (finance, "finance"), (sales, "sales") }) // 2026-11-12: тип массива явный (сборка тестов падала CS0826)
            {
                var known = KnownClipping.Load();
                var fresh = TextClipChecker.Find(window, name, lang == AppLanguage.Kyrgyz ? "ky" : "ru").Where(v => !known.Contains(v.Key)).ToList();
                fresh.Should().BeEmpty($"{name}: ничего не обрезано: " + string.Join("; ", fresh.Select(v => v.Detail)));
            }
        }
        finally
        {
            finance.Close();
            sales.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> SettingsCases() =>
        new[] { (1024, 600), (1024, 640), (1366, 768) }.SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.Item1, s.Item2, l }));

    [AvaloniaTheory]
    [MemberData(nameof(SettingsCases))]
    public async Task Settings_AllTabsVisibleWithoutArrows_AndSaveIsVisible(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, lang);
        var window = Svc<PosSettingsWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(10, 40);
            var grid = window.FindControl<UniformGrid>("NavTabsGrid")!;
            var tabs = grid.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
            tabs.Count.Should().BeGreaterThanOrEqualTo(7, "в кассе у настроек не меньше семи вкладок");
            window.FindControl<Button>("NavScrollLeft")!.IsVisible.Should().BeFalse("стрелки прокрутки вкладок не нужны: все вкладки видны");
            window.FindControl<Button>("NavScrollRight")!.IsVisible.Should().BeFalse();
            (grid.Rows * grid.Columns).Should().BeGreaterThanOrEqualTo(tabs.Count);

            foreach (var tab in tabs)
            {
                var p = At(tab, window);
                p.X.Should().BeGreaterThanOrEqualTo(-0.5, tab.Name);
                (p.X + tab.Bounds.Width).Should().BeLessThanOrEqualTo(width + 0.5, $"вкладка {tab.Name} внутри окна");
                (p.Y + tab.Bounds.Height).Should().BeLessThanOrEqualTo(height, $"вкладка {tab.Name} внутри окна по высоте");
                tab.Bounds.Width.Should().BeGreaterThan(40);
                // Подпись вкладки целиком (многоточие вместо слова — тоже «обрезано»).
                foreach (var tb in tab.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var ft = new FormattedText(tb.Text!, CultureInfo.CurrentUICulture, tb.FlowDirection,
                        new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, null);
                    ft.Width.Should().BeLessThanOrEqualTo(tb.Bounds.Width + 1.5, $"подпись «{tb.Text}» вкладки {tab.Name} не обрезана");
                }
            }

            // «Сохранить» видна на каждой вкладке, у которой она есть, и содержимое не сжато в ноль.
            foreach (var tab in tabs)
            {
                tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UiShot.Settle(4, 30);
                var bar = window.FindControl<Border>("PinnedSaveBar")!;
                var host = window.FindControl<ContentControl>("ContentHost")!;
                host.Bounds.Height.Should().BeGreaterThan(120, $"вкладка {tab.Name}: на содержимое осталось место");
                var save = window.FindControl<Button>("PinnedSaveButton")!;
                if (!bar.IsVisible || !save.IsEffectivelyVisible)
                    continue; // у вкладок «Обновления», «Операции», «Аккаунт» вместо «Сохранить» пометка «применяется сразу»
                var sp = At(save, window);
                (sp.Y + save.Bounds.Height).Should().BeLessThanOrEqualTo(height + 0.5, $"вкладка {tab.Name}: «Сохранить» внутри окна");
                sp.Y.Should().BeGreaterThan(At(host, window).Y, $"вкладка {tab.Name}: «Сохранить» под содержимым, а не поверх");
            }

            var known = KnownClipping.Load();
            var fresh = TextClipChecker.Find(window, "settings-tabs", lang == AppLanguage.Kyrgyz ? "ky" : "ru").Where(v => !known.Contains(v.Key)).ToList();
            fresh.Should().BeEmpty("в настройках ничего не обрезано: " + string.Join("; ", fresh.Select(v => v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
