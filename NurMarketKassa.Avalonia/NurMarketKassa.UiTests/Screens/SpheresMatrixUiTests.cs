using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (проверка всех сфер и Маркетплейса): девять сфер (Продуктовый, Хозмаг, Строймаг, Одежда, Электроника, Базар, Автозапчасти, Канцтовары, Услуги).
/// 1) состав «из коробки»: Смета и Прокат — только Строймаг, остальным — включить в Маркетплейсе (карточки и признаки совпадают); тексты сфер заполнены на 5 языках;
/// 2) карточка товара под каждую сферу на 1024×600 и 1366×768 (ru, ky, uz): блок «Автозапчасть» только у запчастей, серийники только у электроники, окно умещается.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\sphere-matrix-*.png; найденное обрезание текста пишется в sphere-matrix-clip.txt (тест на него не падает).</summary>
[Collection("UiScenarios")]
public sealed class SpheresMatrixUiTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly bool _rental = UserPreferences.Instance.RentalEnabled;
    private readonly bool _estimate = UserPreferences.Instance.EstimateEnabled;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "sphmatrix-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public SpheresMatrixUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.RentalEnabled = _rental;
        p.EstimateEnabled = _estimate;
        ModalShell.WorkingAreaForTests = null;
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Slug, базовая сфера (то, что уходит серверу) и точная сфера (хранится в MarketSphereDetail).</summary>
    public static readonly (string Slug, string Base, string? Detail)[] Spheres =
    {
        ("grocery", "grocery", null),
        ("hozmag", "grocery", "hozmag"),
        ("stroymag", "grocery", "stroymag"),
        ("clothing", "clothing", null),
        ("electronics", "grocery", "electronics"),
        ("bazaar", "grocery", "bazaar"),
        ("autoparts", "grocery", "autoparts"),
        ("stationery", "grocery", "stationery"),
        ("services", "services", null),
    };

    private static void Apply((string Slug, string Base, string? Detail) s, bool rental = false, bool estimate = false)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = s.Base;
        p.MarketSphereDetail = s.Detail;
        p.RentalEnabled = rental;
        p.EstimateEnabled = estimate;
    }

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    // ───────────── 1. состав «из коробки» ─────────────

    [AvaloniaFact]
    public async Task Defaults_estimate_and_rental_only_in_stroymag_and_marketplace_cards_agree()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        foreach (var s in Spheres)
        {
            Apply(s);
            MarketSpheres.Profile.Id.Should().Be(s.Slug, $"профиль {s.Slug} читается из пары «база + точная»");
            var stroy = s.Slug == "stroymag";
            EstimateFeature.IsAvailable.Should().Be(stroy, $"«Смета» из коробки только у Строймага ({s.Slug})");
            RentalFeature.IsAvailable.Should().Be(stroy, $"«Прокат» из коробки только у Строймага ({s.Slug})");
            MarketSpheres.IsHardware.Should().Be(s.Slug is "hozmag" or "stroymag", $"семейство «Хозмаг / Строймаг» ({s.Slug})");

            var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
            items.Select(i => i.Id).Should().OnlyHaveUniqueItems($"в каталоге Маркетплейса нет дублей ({s.Slug})");
            foreach (var id in new[] { "estimate", "rental" })
            {
                var card = items.Single(i => i.Id == id);
                (card.Status == MarketplaceStatus.Installed).Should().Be(stroy, $"карточка «{id}» ({s.Slug}): «Включено» только у Строймага");
                card.Primary.Should().NotBeNull($"у карточки «{id}» есть кнопка ({s.Slug})");
            }

            // Модуль включили в Маркетплейсе — функция доступна в любой сфере (данные не нужны).
            Apply(s, rental: true, estimate: true);
            EstimateFeature.IsAvailable.Should().BeTrue($"«Смета» включена модулем ({s.Slug})");
            RentalFeature.IsAvailable.Should().BeTrue($"«Прокат» включён модулем ({s.Slug})");
            var on = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
            on.Single(i => i.Id == "estimate").Status.Should().Be(MarketplaceStatus.Installed, $"после включения карточка «Смета» отмечена ({s.Slug})");
            on.Single(i => i.Id == "rental").Status.Should().Be(MarketplaceStatus.Installed, $"после включения карточка «Прокат» отмечена ({s.Slug})");
        }
    }

    [AvaloniaFact]
    public async Task Sphere_texts_are_complete_in_five_languages_and_choice_list_has_nine_in_order()
    {
        await TestHost.WarmAsync();
        var list = SphereCatalog.ChoiceList();
        list.Select(p => p.Id).Should().Equal("grocery", "hozmag", "stroymag", "clothing", "electronics", "bazaar", "autoparts", "stationery", "services");
        foreach (var p in list)
        {
            p.Title.IsComplete.Should().BeTrue($"название {p.Id}");
            p.ShortTitle.IsComplete.Should().BeTrue($"короткое название {p.Id}");
            p.Description.IsComplete.Should().BeTrue($"описание {p.Id}");
            foreach (var u in p.Units) u.Name.IsComplete.Should().BeTrue($"единица {u.Code} у {p.Id}");
            foreach (var c in p.CategoryHints) c.IsComplete.Should().BeTrue($"категория «{c.Ru}» у {p.Id}");
        }
        // Прокат обещают только там, где он есть из коробки; «Базар» не лжёт про функции, которых нет.
        foreach (var p in list.Where(x => x.Id != "stroymag"))
        {
            var ru = p.Description.Ru.ToLowerInvariant();
            if (ru.Contains("прокат") || ru.Contains("смета"))
                ru.Should().Contain("маркетплейс", $"описание {p.Id} упоминает прокат/смету — только как модуль Маркетплейса");
        }
    }

    /// <summary>Нижние плитки окна выбора («Базар», «Автозапчасти», «Канцтовары», «Услуги»): прокрутка до конца и снимок (в SphereChoiceUiTests снимок делается до прокрутки).</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Choice_window_bottom_tiles_are_visible_after_scroll(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var window = new MarketSphereChoiceWindow();
        try
        {
            using (UiShot.Render(window)) { }
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Extent.Height > s.Viewport.Height + 1);
            scroll.ScrollToEnd();
            UiShot.Settle();
            scroll.Offset.Y.Should().BeGreaterThan(0);
            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"sphere-matrix-choice-end-{Code(lang)}-1024x600.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── 2. карточка товара под сферу ─────────────

    public static IEnumerable<object[]> CardCases()
    {
        // Каждая сфера: ru на 1366×768, ky и uz на 1024×600 (самый тесный случай) — 27 снимков на вкладку.
        foreach (var s in Spheres)
        {
            yield return new object[] { s.Slug, 1366, 768, AppLanguage.Russian };
            yield return new object[] { s.Slug, 1024, 600, AppLanguage.Kyrgyz };
            yield return new object[] { s.Slug, 1024, 600, AppLanguage.Uzbek };
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(CardCases))]
    public async Task Product_card_per_sphere_fits_screen_and_shows_only_its_blocks(string slug, int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var sphere = Spheres.Single(x => x.Slug == slug);
        Apply(sphere);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var dlg = new ProductEditDialog();
        try
        {
            using (UiShot.Render(dlg)) { }
            var tabs = UiDrive.Find<TabControl>(dlg, "ProductTabs");
            var clips = new List<string>();
            foreach (var index in new[] { 0, 1, 2, 3 })
            {
                tabs.SelectedIndex = index;
                UiShot.Settle();
                dlg.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.95 + 36, $"окно умещается ({slug} {width}x{height} {lang} вкладка {index})");
                foreach (var v in TextClipChecker.Find(dlg, $"sphere-card-{slug}-t{index}", Code(lang)))
                    clips.Add($"{slug} {width}x{height} {Code(lang)} вкладка {index}: {v.Detail}");
                using var shot = UiShot.Render(dlg);
                using var image = SKImage.FromBitmap(shot);
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"sphere-matrix-card-{slug}-t{index}-{Code(lang)}-{width}x{height}.png"), data.ToArray());
                if (index == 1)
                    UiDrive.Find<StackPanel>(dlg, "AutoPartBlock").IsVisible.Should().Be(slug == "autoparts", $"блок «Автозапчасть» только у запчастей ({slug})");
            }
            tabs.SelectedIndex = 0;
            UiShot.Settle();
            if (slug != "electronics")
                UiDrive.Find<StackPanel>(dlg, "SerialsBlock").IsVisible.Should().BeFalse($"серийные номера только у электроники ({slug})");
            if (clips.Count > 0)
                File.AppendAllLines(Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual", "sphere-matrix-clip.txt"), clips);
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }
}
