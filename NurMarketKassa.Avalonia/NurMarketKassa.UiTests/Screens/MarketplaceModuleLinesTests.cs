using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (замечание владельца: «описание новым функциям что он делает ты не добавил!!!»): у КАЖДОГО модуля, который сервер отдаёт в GET public/modules/ (список зафиксирован в
/// <see cref="TariffBuilderFixture.Live"/>), есть значок, цвет, ОДНА строка «что делает» на 5 языках и (кроме модулей со своей карточкой) полное описание страницы. Новый модуль сервера без записи — красный тест.
/// Плитки Маркетплейса показывают строку под названием. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\marketplace-module-lines-*.png.</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceModuleLinesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "module-lines-" + Guid.NewGuid().ToString("N")[..8]);
    private IDisposable? _fixture;

    public MarketplaceModuleLinesTests() => TestHost.Ensure();

    public void Dispose()
    {
        _fixture?.Dispose();
        ModalShell.WorkingAreaForTests = null;
        PublicModules.SetForTests(null);
    }

    private static IEnumerable<string> ModuleIdConstants() =>
        typeof(ModuleIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l => new object[] { l });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Every_live_server_module_has_icon_color_line_and_page_text(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var ids = TariffBuilderFixture.LiveIds;
        ids.Should().HaveCount(29, "живой ответ public/modules на 26.11.2026 — 29 модулей (при новом модуле сервера обновить TariffBuilderFixture.Live и отчёт)");
        ids.Should().OnlyHaveUniqueItems();

        foreach (var id in ids)
        {
            MarketplaceModuleContent.HasLine(id).Should().BeTrue($"у модуля «{id}» нет строки «что делает»: добавьте её в MarketplaceModuleContent.Lines");
            var line = MarketplaceModuleContent.LineOf(id);
            line.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            line!.Length.Should().BeInRange(25, 140, $"{id} {lang}: одна строка «что делает», не пустая и не простыня");
            line.Should().NotEndWith("...").And.NotContain("TODO").And.NotContain("  ", $"{id} {lang}");

            var icon = MarketplaceModuleContent.ListIconKey(id);
            MarketplaceIcons.Has(icon).Should().BeTrue($"{id}: значок «{icon}» есть в MarketplaceIcons");
            icon.Should().NotBe(MarketplaceIcons.FallbackKey).And.NotBe("puzzle-outline").And.NotBe(MarketplaceModuleContent.FallbackIcon, $"{id}: свой значок, не запасной");
            Color.TryParse(MarketplaceModuleContent.ListColor(id), out _).Should().BeTrue($"{id}: цвет — hex");

            if (Array.IndexOf(ModuleIds.WithOwnCard, id) >= 0)
                continue; // Смета, Прокат, Ремонт, Trade-in — своя карточка и своя страница
            MarketplaceModuleContent.Has(id).Should().BeTrue($"у модуля «{id}» нет записи страницы: добавьте её в MarketplaceModuleContent.Specs");
            var text = MarketplaceModuleContent.TextOf(id)!;
            text.Short.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            text.What.Length.Should().BeGreaterThan(80, $"{id} {lang}: «Что делает» — настоящее описание с примером");
            text.Steps.Should().HaveCountGreaterThanOrEqualTo(2, $"{id} {lang}: «Как включить / где найти»");
            text.Audience.Should().NotBeEmpty($"{id} {lang}: «Для кого»");
        }

        ids.Select(MarketplaceModuleContent.ListIconKey).Should().OnlyHaveUniqueItems("у каждого из 29 модулей свой значок: соседние строки списка различаются");
        // Константы кода (ModuleIds) — тоже без дыр: то, что касса умеет, а сервер ещё не отдаёт, всё равно описано.
        foreach (var id in ModuleIdConstants())
            MarketplaceModuleContent.HasLine(id).Should().BeTrue($"ModuleIds.{id}: нет строки «что делает»");
    }

    [AvaloniaFact]
    public async Task Lines_are_translated_not_copied_between_languages_and_differ_from_titles()
    {
        await TestHost.WarmAsync();
        var byLang = new Dictionary<AppLanguage, string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            byLang[lang] = string.Join("|", TariffBuilderFixture.LiveIds.Select(id => MarketplaceModuleContent.LineOf(id)));
        }
        byLang.Values.Should().OnlyHaveUniqueItems("все пять языков — разные тексты");

        // Кыргызский не должен быть русским текстом (запасной вариант «скопировать ru» незаметен глазу, но виден в сравнении).
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var ru = TariffBuilderFixture.LiveIds.ToDictionary(id => id, id => MarketplaceModuleContent.LineOf(id)!);
        UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
        foreach (var id in TariffBuilderFixture.LiveIds)
            MarketplaceModuleContent.LineOf(id).Should().NotBe(ru[id], $"{id}: кыргызская строка — перевод, а не русский текст");

        // Строка — не повтор названия модуля (название рядом уже есть).
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        foreach (var m in TariffBuilderFixture.Live)
            MarketplaceModuleContent.LineOf(m.Id)!.Should().NotBe(m.Name, $"{m.Id}: строка «что делает» не должна повторять название");
    }

    [AvaloniaFact]
    public async Task Server_description_is_only_a_fallback_ours_wins_and_unknown_without_text_is_null()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var withDesc = new[]
        {
            new PublicModule("haggle", "Торг", "bazaar", 100m, 0m, ["bazaar_retail"], 7, "Описание сервера, которое не должно победить нашу строку"),
            new PublicModule("zz_new_module", "Новый модуль", "x", 50m, 0m, [], 0, "  Строка от сервера для нового модуля  "),
            new PublicModule("zz_no_text", "Без описания", "x", 50m, 0m, []),
        };
        PublicModules.SetForTests(withDesc);
        MarketplaceModuleContent.LineOf("haggle").Should().Be(MarketplaceModuleContent.LineOf("haggle")).And.NotContain("сервера", "наша строка главнее описания сервера");
        MarketplaceModuleContent.LineOf("zz_new_module").Should().Be("Строка от сервера для нового модуля", "нет нашей строки — берём описание сервера (запасное)");
        MarketplaceModuleContent.LineOf("zz_no_text").Should().BeNull("нет ни нашей строки, ни описания сервера — пустую строку не показываем");
        MarketplaceModuleContent.LineOf("совсем_неизвестный").Should().BeNull();

        // Разбор: description / summary из public/modules попадает в запись.
        using var doc = System.Text.Json.JsonDocument.Parse("""{"currency":"KGS","modules":[{"id":"a","name":{"ru":"A"},"monthly":"1.00","description":{"ru":"Описание А"}},{"id":"b","name":{"ru":"B"},"monthly":"2.00","summary":"Кратко Б"},{"id":"c","name":{"ru":"C"},"monthly":"3.00"}]}""");
        var parsed = PublicModules.Parse(doc.RootElement, out _)!;
        parsed.Single(m => m.Id == "a").Description.Should().Be("Описание А");
        parsed.Single(m => m.Id == "b").Description.Should().Be("Кратко Б");
        parsed.Single(m => m.Id == "c").Description.Should().BeNull();
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Marketplace_cards_of_every_live_module_carry_the_line(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
        foreach (var id in TariffBuilderFixture.LiveIds)
        {
            var own = Array.IndexOf(ModuleIds.WithOwnCard, id) >= 0;
            var card = items.SingleOrDefault(i => i.Id == (own ? id : "module:" + id));
            card.Should().NotBeNull($"{id} {lang}: карточка модуля есть в каталоге");
            card!.Line.Should().Be(MarketplaceModuleContent.LineOf(id), $"{id} {lang}: строка «что делает» на карточке");
            card.BlurbText.Should().Be(card.Line, $"{id} {lang}: под названием плитки — она");
        }

        // У прочих функций (не модули) под названием тоже фраза: описание есть у каждой карточки витрины.
        foreach (var i in items.Where(i => i.Kind is MarketplaceKind.Feature or MarketplaceKind.Service && !i.IsComingSoon))
            i.BlurbText.Should().NotBeNullOrWhiteSpace($"{i.Id} {lang}: у функции Маркетплейса есть короткая фраза «что делает»");
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian, 1366, 768)]
    [InlineData(AppLanguage.Kyrgyz, 1366, 768)]
    [InlineData(AppLanguage.Uzbek, 1024, 600)]
    public async Task Marketplace_tiles_show_the_line_under_the_title_and_are_saved_for_eyes(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            store.SetFilter(MarketplaceFilter.Features);
            UiShot.Settle(6);
            var all = UiDrive.AllText(window);
            foreach (var id in new[] { ModuleIds.ExpiryBatches, ModuleIds.QuickItems, ModuleIds.ScalesLabels, "condition", ModuleIds.Repair, "haggle", ModuleIds.FractionalQty })
                all.Should().Contain(MarketplaceModuleContent.LineOf(id)!, $"плитка «{id}» показывает строку «что делает» ({lang} {width}×{height})");

            // Плитки одной высоты, текст не вылезает за плитку (строка ограничена тремя строками).
            foreach (var tile in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("mp-tile") && b.IsEffectivelyVisible))
            {
                tile.Bounds.Height.Should().BeGreaterThan(200, "плитка выросла под строку «что делает»");
                var rects = new List<(string Text, double Top, double Bottom, double Left, double Right)>();
                foreach (var tb in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Bounds.Height > 0))
                {
                    var p = tb.TranslatePoint(default, tile)!.Value;
                    (p.Y + tb.Bounds.Height).Should().BeLessThanOrEqualTo(tile.Bounds.Height + 0.5, $"текст «{tb.Text}» внутри плитки");
                    rects.Add((tb.Text!, p.Y, p.Y + tb.Bounds.Height, p.X, p.X + tb.Bounds.Width));
                }
                rects.Sort((a, b) => a.Top.CompareTo(b.Top));
                for (var k = 1; k < rects.Count; k++)
                {
                    var (a, b) = (rects[k - 1], rects[k]);
                    var sideBySide = b.Left >= a.Right - 0.5 || a.Left >= b.Right - 0.5; // значок-эмодзи и метка «НОВОЕ» в одной строке — рядом, не друг на друге
                    if (!sideBySide)
                        b.Top.Should().BeGreaterThanOrEqualTo(a.Bottom - 1.0, $"«{a.Text}» и «{b.Text}» на плитке не налезают друг на друга");
                }
            }

            using var shot = UiShot.Render(window);
            Save(shot, $"marketplace-module-lines-{(lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru")}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    internal static void Save(SKBitmap shot, string name)
    {
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }
}
