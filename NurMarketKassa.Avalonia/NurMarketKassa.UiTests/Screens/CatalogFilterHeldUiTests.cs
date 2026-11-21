using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (владелец, касса): «Кнопка ещё открывает чё то, но очень странно выглядит» (окно «Фильтр»), «Убрать поисковик в фильтре»,
/// «В фильтре кнопка "Сбросить" сливается с фоном», «Список отложенных нужно, чтобы все отложенные показывал».
/// Окно «Фильтр»: нет своего поля «Поиск»; «Сбросить» — с обводкой, контраст рамки к фону окна не ниже 3:1, текста — 4,5:1 (в светлой, тёмной и сенсорной теме),
/// выключена, пока нечего сбрасывать; ничего не налезает на 1024×600 и 1366×768 (ru/ky/uz). Окно «Отложенные»: 35 записей очереди + 3 чека-вкладки — все в списке,
/// число в заголовке, список прокручивается.</summary>
[Collection("UiScenarios")]
public sealed class CatalogFilterHeldUiTests
{
    private static readonly UiSize[] Sizes = [new(1024, 600), new(1366, 768)];

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark, UiTheme.Touch })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [theme, lang];
    }

    private static CatalogFilterViewModel Model() => new(
        Enumerable.Range(1, 6).Select(i => new CatalogProductTileVm("p" + i, "Товар " + i, "10.00 сом", false) { Category = i % 2 == 0 ? "Молочка" : "Выпечка", Brand = i % 3 == 0 ? "Нурсут" : null }));

    private static string Hex(string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, app.ActualThemeVariant, out var value).Should().BeTrue($"в теме есть кисть {key}");
        var color = ((ISolidColorBrush)value!).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task FilterWindow_NoOwnSearch_ResetIsVisibleAndNothingOverlaps(UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var model = Model();
        var window = new FilterWindow(model);
        try
        {
            foreach (var size in Sizes)
            {
                using (var _ = UiShot.Render(window, size)) { }
                UiShot.Settle(3);

                // нет собственного поиска: только два поля цены
                window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).Select(t => t.Name).Should().BeEquivalentTo("PriceMinBox", "PriceMaxBox");
                window.FindControl<TextBox>("SearchBox").Should().BeNull();

                var reset = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("filter-reset"));
                reset.IsEffectivelyVisible.Should().BeTrue();
                reset.Bounds.Width.Should().BeGreaterThanOrEqualTo(100);
                reset.Bounds.Height.Should().BeGreaterThanOrEqualTo(40);
                reset.IsEnabled.Should().BeFalse("пока нечего сбрасывать");
                reset.BorderThickness.Left.Should().BeGreaterThan(0, "у «Сбросить» есть рамка — иначе сливается с фоном");

                // контраст: рамка кнопки к фону окна >= 3:1, текст >= 4,5:1
                AccentThemeService.Contrast(Hex("BrushInputBorder"), Hex("BrushDialogPanel")).Should().BeGreaterThanOrEqualTo(3.0, "рамка «Сбросить» читается на фоне окна");
                AccentThemeService.Contrast(Hex("BrushText"), Hex("BrushDialogPanel")).Should().BeGreaterThanOrEqualTo(4.5);

                // включённая после ввода цены
                model.PriceMin = "100";
                UiShot.Settle(3);
                reset.IsEnabled.Should().BeTrue();
                var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel && b.Classes.Contains("modal-cancel"));
                var apply = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
                var rects = new[] { reset, cancel, apply }.Select(b => (b, r: new Rect(b.TranslatePoint(default, window) ?? default, b.Bounds.Size))).ToList();
                for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                    rects[i].r.Intersects(rects[j].r).Should().BeFalse($"кнопки «{UiDrive.TextOf(rects[i].b)}» и «{UiDrive.TextOf(rects[j].b)}» не налезают ({size})");
                foreach (var (b, r) in rects)
                    new Rect(0, 0, window.Bounds.Width, window.Bounds.Height).Contains(r).Should().BeTrue($"кнопка «{UiDrive.TextOf(b)}» целиком в окне ({size})");

                if (size.Width == 1024 && lang == AppLanguage.Russian)
                    Save(window, $"filter-window-{theme}-ru-1024x600-reset-on.png");
                model.Reset();
                UiShot.Settle(3);
                reset.IsEnabled.Should().BeFalse();
                if (size.Width == 1024 && lang == AppLanguage.Russian)
                    Save(window, $"filter-window-{theme}-ru-1024x600-reset-off.png");
            }
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public async Task FilterWindow_ApplyUsesValuesFromModel()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var model = Model();
        model.SelectedCategory = "Молочка";
        var window = new FilterWindow(model);
        try
        {
            using var _ = UiShot.Render(window);
            window.FindControl<ComboBox>("CategoryCombo")!.SelectedItem.Should().Be("Молочка");
            window.FindControl<ComboBox>("CategoryCombo")!.SelectedItem = "Выпечка";
            window.FindControl<TextBox>("PriceMaxBox")!.Text = "500";
            UiShot.Settle(3);

            model.SelectedCategory.Should().Be("Выпечка");
            model.PriceMax.Should().Be("500");
            model.BuildCriteria().SearchText.Should().BeEmpty();
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    // ---- «Отложенные»: все в списке ----

    private static string DeferredFile => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "deferred_carts.json");

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task DeferredCartsDialog_ShowsAll_QueueAndTabs_WithCountInTitle(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var existed = File.Exists(DeferredFile);
        var backup = existed ? File.ReadAllBytes(DeferredFile) : null;
        var ids = Enumerable.Range(0, 35).Select(i => "ui-held-" + i).ToList();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
            DeferredCartsStore.Add(new DeferredCartEntry { Id = id, Label = "Чек " + i, SavedAt = DateTimeOffset.Now.AddDays(-i * 3), CartJson = "{\"items\":[]}" });
        var tabs = Enumerable.Range(0, 3).Select(i => new DeferredCartEntry { Id = "ui-tab-" + i, Label = "Отложен 1" + i + ":00", SavedAt = DateTimeOffset.Now.AddMinutes(-i), CartJson = "{\"items\":[]}" }).ToList();
        var dialog = new DeferredCartsDialog(new DeferredCartsDialogActions { GetHeldTabs = () => tabs, IsHeldTab = id => id.StartsWith("ui-tab-", StringComparison.Ordinal) });
        try
        {
            foreach (var size in Sizes)
            {
                using (var _ = UiShot.Render(dialog, size)) { }
                UiShot.Settle(3);
                var list = dialog.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CartListBox");
                var queueInStore = DeferredCartsStore.LoadAll().Count(x => x.Id.StartsWith("ui-held-", StringComparison.Ordinal));
                queueInStore.Should().Be(35);
                list.Items.Count.Should().BeGreaterThanOrEqualTo(38, "в списке вся очередь и все чеки-вкладки");
                dialog.GetVisualDescendants().OfType<ModalShell>().Single().Title.Should().Contain(list.Items.Count.ToString(), "число отложенных — в заголовке");
                // список прокручивается: у него есть ScrollViewer с запасом по высоте
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height, "38 строк не помещаются — нужна прокрутка");
                if (size.Width == 1024 && lang == AppLanguage.Russian)
                    Save(dialog, "deferred-carts-ru-1024x600-38.png");
            }
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
            DeferredCartsStore.RemoveIds(ids);
            if (existed && backup != null)
                File.WriteAllBytes(DeferredFile, backup);
        }
    }
}
