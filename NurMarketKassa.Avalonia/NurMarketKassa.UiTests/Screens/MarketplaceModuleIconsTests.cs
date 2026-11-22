using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
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

/// <summary>2026-11-22 (просьба владельца: «исправь иконки новых функций» и «нет описания, что он делает»): у каждого модуля-функции Маркетплейса — свой значок (не «пазл» и не запасной),
/// свой цвет плитки и описание на 5 языках. ПРАВИЛО: новая функция, модуль, тема или вид Маркетплейса без значка и описания делает этот тест красным
/// (значок — MarketplaceIcons.Paths, описание — MarketplaceModuleContent.Specs). Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\marketplace-module-icons-*.png.</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceModuleIconsTests : IDisposable
{
    private const string UnknownId = "zz_new_module";

    private readonly string? _account = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "module-icons-" + Guid.NewGuid().ToString("N")[..8]);

    public MarketplaceModuleIconsTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        CompanyModules.DirOverride = _dir;
        UserPreferences.Instance.LastDataAccountKey = "c:ui-module-icons";
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = null;
        CompanyModules.ResetForTests();
        var ids = MarketplaceModuleContent.KnownIds.Concat([UnknownId]).ToList();
        var catalog = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"name\":\"{id}\",\"names\":{{\"ru\":\"Модуль {id}\"}},\"group\":\"test\",\"default_in\":[]}}"));
        var items = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"enabled\":false,\"source\":\"off\"}}"));
        using var doc = JsonDocument.Parse($"{{\"profile\":\"grocery\",\"version\":\"1\",\"modules\":[],\"items\":[{items}],\"catalog\":[{catalog}],\"profiles\":[]}}");
        CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "grocery");
    }

    public void Dispose()
    {
        CompanyModules.ResetForTests();
        CompanyModules.DirOverride = null;
        var p = UserPreferences.Instance;
        p.LastDataAccountKey = _account;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static IEnumerable<string> ServerModuleIds() =>
        typeof(ModuleIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    [AvaloniaFact]
    public async Task Every_known_module_id_has_its_own_icon_color_and_text()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);

        // Все коды из ModuleIds, у которых нет своей карточки (Смета, Прокат, Ремонт, Trade-in), обязаны быть в MarketplaceModuleContent.
        foreach (var id in ServerModuleIds().Where(id => !ModuleIds.WithOwnCard.Contains(id)))
            MarketplaceModuleContent.Has(id).Should().BeTrue($"у модуля «{id}» нет значка и описания: добавьте его в MarketplaceModuleContent.Specs (и значок в MarketplaceIcons)");

        var known = MarketplaceModuleContent.KnownIds.ToList();
        var keys = known.Select(MarketplaceModuleContent.IconKeyOf).ToList();
        keys.Should().OnlyHaveUniqueItems("у каждого модуля свой значок — соседние карточки различаются");
        known.Select(MarketplaceModuleContent.ColorOf).Should().OnlyHaveUniqueItems("у каждого модуля свой цвет плитки");
        foreach (var key in keys)
        {
            MarketplaceIcons.Has(key).Should().BeTrue($"значок «{key}» есть в MarketplaceIcons");
            key.Should().NotBe(MarketplaceIcons.FallbackKey).And.NotBe("puzzle-outline").And.NotBe(MarketplaceModuleContent.FallbackIcon);
            // Geometry.Parse не бросает на кривом пути у Get(): проверяем напрямую.
            MarketplaceIcons.Get(key).Should().NotBeNull(key);
            Geometry.Parse(PathData(key)).Bounds.Width.Should().BeGreaterThan(8, $"значок «{key}» должен быть различим в 40–48 px");
        }
        foreach (var id in known)
            Color.TryParse(MarketplaceModuleContent.ColorOf(id), out _).Should().BeTrue($"цвет «{id}» — hex");
        MarketplaceModuleContent.Has(UnknownId).Should().BeFalse();
        MarketplaceModuleContent.IconKeyOf(UnknownId).Should().Be(MarketplaceModuleContent.FallbackIcon);
        Color.TryParse(MarketplaceModuleContent.ColorOf(UnknownId), out _).Should().BeTrue();
    }

    /// <summary>Строка path-data значка (словарь закрытый): берём её из исходного словаря через рефлексию, чтобы проверить Geometry.Parse без кэша.</summary>
    private static string PathData(string key)
    {
        var dict = (Dictionary<string, string>)typeof(MarketplaceIcons).GetField("Paths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        return dict[key];
    }

    [AvaloniaFact]
    public async Task All_icon_paths_in_the_set_parse()
    {
        await TestHost.WarmAsync();
        var dict = (Dictionary<string, string>)typeof(MarketplaceIcons).GetField("Paths", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        foreach (var (key, data) in dict)
            ((Action)(() => Geometry.Parse(data))).Should().NotThrow($"путь значка «{key}» разбирается");
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Turkish)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Module_cards_have_real_description_howto_audience_and_icon_in_every_language(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
        foreach (var id in MarketplaceModuleContent.KnownIds)
        {
            var card = items.SingleOrDefault(i => i.Id == "module:" + id);
            card.Should().NotBeNull($"{id}: карточка есть в каталоге");
            card!.IconKey.Should().Be(MarketplaceModuleContent.IconKeyOf(id), $"{id} {lang}");
            card.IconColor.Should().Be(MarketplaceModuleContent.ColorOf(id), $"{id} {lang}");
            card.IconGlyph.Should().NotBe("🧩", $"{id} {lang}: не «пазл»");
            card.ShortDescription.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            card.Tagline.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            card.LongDescription.Length.Should().BeGreaterThan(80, $"{id} {lang}: «Что делает» — настоящее описание с примером");
            card.HowTo.Should().HaveCountGreaterThanOrEqualTo(2, $"{id} {lang}: «Как включить / где найти»");
            card.HowTo.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s), $"{id} {lang}");
            card.Audience.Should().NotBeEmpty($"{id} {lang}: «Для кого»");
            // не запасной шаблон «из набора модулей»
            card.LongDescription.Should().NotContain("из набора модулей").And.NotContain("module set").And.NotContain("модулдар топтомунан")
                .And.NotContain("Modül setinden").And.NotContain("modullar to'plamidan", $"{id} {lang}: шаблонный текст — только запасной");
        }

        // Неизвестный модуль: запасные значок и текст, но не «пазл».
        var unknown = items.Single(i => i.Id == "module:" + UnknownId);
        unknown.IconKey.Should().Be(MarketplaceModuleContent.FallbackIcon);
        unknown.IconGlyph.Should().NotBe("🧩");
        unknown.LongDescription.Should().NotBeNullOrWhiteSpace();
    }

    [AvaloniaFact]
    public async Task Texts_of_a_module_are_translated_not_copied_between_languages()
    {
        await TestHost.WarmAsync();
        var byLang = new Dictionary<AppLanguage, string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            byLang[lang] = string.Join("|", MarketplaceModuleContent.KnownIds.Select(id => MarketplaceModuleContent.TextOf(id)!).Select(t => t.Short + t.What + string.Join("", t.Steps) + string.Join("", t.Audience)));
        }
        byLang.Values.Should().OnlyHaveUniqueItems("все пять языков — разные тексты");
    }

    /// <summary>Каждый элемент витрины (функция, пакет, вид, тема): значок (ключ или нарисованный) и описание. Новая карточка без них — красный тест.</summary>
    [AvaloniaFact]
    public async Task Every_marketplace_card_has_an_icon_and_a_description()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
        foreach (var i in items)
        {
            (i.IconVisual != null || !string.IsNullOrEmpty(i.IconKey) || !string.IsNullOrEmpty(i.IconGlyph)).Should().BeTrue($"{i.Id}: нет значка");
            (!string.IsNullOrWhiteSpace(i.ShortDescription) || !string.IsNullOrWhiteSpace(i.Tagline) || !string.IsNullOrWhiteSpace(i.LongDescription)).Should().BeTrue($"{i.Id}: нет описания");
            if (!string.IsNullOrEmpty(i.IconKey))
                MarketplaceIcons.Has(i.IconKey).Should().BeTrue($"{i.Id}: значок «{i.IconKey}» отсутствует в MarketplaceIcons (показался бы запасной)");
        }
    }

    // ───────────── снимки для глаз ─────────────

    [AvaloniaTheory]
    [InlineData(UiTheme.Light)]
    [InlineData(UiTheme.Dark)]
    public async Task Icon_sheet_of_all_modules_is_saved_for_eyes(UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        foreach (var id in MarketplaceModuleContent.KnownIds.Concat([UnknownId]))
        {
            var color = Color.Parse(MarketplaceModuleContent.ColorOf(id));
            var tile = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(12), ClipToBounds = true,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(color, 0), new GradientStop(Color.Parse(AccentThemeService.MixHex(MarketplaceModuleContent.ColorOf(id), "#000000", 0.18)), 1) },
                },
                Child = new PathIcon { Width = 24, Height = 24, Data = MarketplaceIcons.Get(MarketplaceModuleContent.IconKeyOf(id)), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var cell = new StackPanel { Width = 110, Margin = new Thickness(4), Spacing = 4 };
            cell.Children.Add(tile);
            cell.Children.Add(new TextBlock { Text = id, FontSize = 10, TextWrapping = TextWrapping.Wrap });
            wrap.Children.Add(cell);
        }
        var window = new Window { Content = new ScrollViewer { Content = wrap } };
        try
        {
            using var shot = UiShot.Render(window, new UiSize(900, 420));
            Save(shot, $"marketplace-module-icons-sheet-{(theme == UiTheme.Dark ? "dark" : "light")}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(UiTheme.Light)]
    [InlineData(UiTheme.Dark)]
    public async Task Marketplace_functions_page_and_module_page_are_saved_for_eyes(UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        ModalShell.WorkingAreaForTests = new Size(1366, 768);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            store.SetFilter(MarketplaceFilter.Features);
            UiShot.Settle();
            var t = theme == UiTheme.Dark ? "dark" : "light";
            using (var shot = UiShot.Render(window))
                Save(shot, $"marketplace-module-icons-functions-{t}");
            store.OpenItem("module:" + ModuleIds.FractionalQty);
            UiShot.Settle();
            using (var shot = UiShot.Render(window))
                Save(shot, $"marketplace-module-icons-page-fractional-{t}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void Save(SKBitmap shot, string name)
    {
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }
}
