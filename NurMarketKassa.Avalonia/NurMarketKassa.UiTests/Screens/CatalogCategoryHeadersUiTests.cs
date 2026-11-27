using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (владелец по фото кассы: «Добавь линии, разделение по категориям»): настоящая панель каталога на 1024×600 и 1920×1080, ru / ky / uz,
/// плитки и список, светлая и тёмная тема. Проверяется: над каждой группой — заголовок (метка, название, число), группа начинается с новой строки,
/// заголовок не налезает на плитки и не обрезан по высоте, текст в пределах полосы (длинное название — с многоточием, без вылезания), «Без категории» — последней
/// и на языке программы; переключатель и поиск убирают заголовки. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\catalog-groups-*.png.</summary>
[Collection("UiScenarios")]
public sealed class CatalogCategoryHeadersUiTests : IDisposable
{
    private readonly bool _before = UserPreferences.Instance.CatalogGroupByCategory;
    private readonly CatalogViewMode _mode = UserPreferences.Instance.CatalogViewMode;

    private const string LongCategory = "Үй тиричилик химиясы, тазалоочу жана жуучу каражаттар (оптом жана чекене)";

    public void Dispose()
    {
        UserPreferences.Instance.CatalogGroupByCategory = _before;
        UserPreferences.Instance.CatalogViewMode = _mode;
    }

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private static void SavePng(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    public static IEnumerable<object[]> Cases() =>
        new (double W, double H)[] { (1024, 600), (1920, 1080) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }
                .SelectMany(l => new[] { CatalogViewMode.Cards, CatalogViewMode.Table }.Select(m => new object[] { s.W, s.H, l, m, UiTheme.Light })))
            .Append(new object[] { 1366.0, 768.0, AppLanguage.Russian, CatalogViewMode.Cards, UiTheme.Dark })
            .Append(new object[] { 1024.0, 600.0, AppLanguage.Kyrgyz, CatalogViewMode.Table, UiTheme.Dark });

    private static void AddShop(List<CatalogProductTileVm> items)
    {
        // четыре товара фикстуры — в две категории, плюс напитки, длинная категория и товары без категории
        items[0].Category = "Сухофрукты";
        items[1].Category = "Бакалея";
        items[2].Category = "Сухофрукты";
        items[3].Category = "Электроника";
        void Add(string id, string title, string? category)
        {
            var t = new CatalogProductTileVm(id, title, "55.00 сом", false) { Barcode = "46" + (100000000000L + id.GetHashCode() % 1000000).ToString("D11"), Unit = "шт", Quantity = 12, Category = category };
            ProductUnitNormalizer.ApplyToTile(t);
            items.Add(t);
        }
        Add("d1", "Кола 1 л", "Напитки");
        Add("d2", "Сок яблочный", "Напитки");
        Add("d3", "Вода «Легенда» 0,5", "Напитки");
        Add("b2", "Рис девзира", "Бакалея");
        Add("b3", "Гречка 900 г", "Бакалея");
        Add("h1", "Порошок «Тайд» 3 кг", LongCategory);
        Add("h2", "Мыло детское", LongCategory);
        Add("u1", "Пакет-майка", null);
        Add("u2", "Зажигалка", "");
    }

    private static ListBox VisibleList(Window window) =>
        window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name is "CardsListBox" or "TableListBox" && l.IsEffectivelyVisible);

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Headers_SplitGroups_NoOverlap_NotClipped_AndToggleOrSearchRemoves(double width, double height, AppLanguage lang, CatalogViewMode mode, UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        UserPreferences.Instance.CatalogGroupByCategory = true;
        var built = CatalogListFixture.Create(width, height, 0, null, AddShop);
        try
        {
            built.Vm.SetViewMode(mode);
            built.Window.Show();
            UiShot.Settle(8);
            var tag = $"({width}x{height} {Code(lang)} {mode} {theme})";

            built.Vm.IsGroupedByCategory.Should().BeTrue(tag);
            var list = VisibleList(built.Window);
            var panel = list.ItemsPanelRoot.Should().BeOfType<CatalogGroupedPanel>(tag).Subject;
            var items = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible && i.DataContext is CatalogProductTileVm).ToList();
            items.Should().HaveCount(13, tag);

            // 6 групп: Бакалея, Напитки, Сухофрукты, Электроника, длинная, «Без категории» (порядок — по названию, «Без категории» — последней)
            panel.Headers.Should().HaveCount(6, tag);
            panel.Headers[^1].Title.Should().Be(CatalogCategoryGrouping.UncategorizedTitle, tag);
            panel.Headers[^1].CountText.Should().Be("2", tag);
            panel.Headers.First(h => h.Key == "Напитки").CountText.Should().Be("3", tag);

            foreach (var h in panel.Headers)
            {
                var b = h.Bounds;
                b.Height.Should().BeGreaterThanOrEqualTo(h.TitleBounds.Height, $"название «{h.Title}» не обрезано по высоте {tag}");
                h.TitleBounds.Top.Should().BeGreaterThanOrEqualTo(b.Top - 0.5, tag);
                h.TitleBounds.Bottom.Should().BeLessThanOrEqualTo(b.Bottom + 0.5, tag);
                h.TitleBounds.Left.Should().BeGreaterThanOrEqualTo(b.Left, tag);
                h.TitleBounds.Right.Should().BeLessThanOrEqualTo(b.Right + 0.5, $"название «{h.Title}» не вылезает за полосу {tag}");
                if (h.CountText.Length > 0)
                {
                    h.CountBounds.Left.Should().BeGreaterThanOrEqualTo(h.TitleBounds.Right, tag);
                    h.CountBounds.Right.Should().BeLessThanOrEqualTo(b.Right + 0.5, $"число товаров «{h.Title}» видно {tag}");
                }
                if (h.Key != LongCategory)
                    h.TitleTrimmed.Should().BeFalse($"короткое название «{h.Title}» целиком {tag}");

                // заголовок не налезает ни на одну плитку
                foreach (var item in items)
                    item.Bounds.Intersects(b.Deflate(0.5)).Should().BeFalse($"плитка «{((CatalogProductTileVm)item.DataContext!).Title}» не под заголовком «{h.Title}» {tag}");

                // группа начинается с новой строки, сразу под своим заголовком, и заголовок не остаётся один
                h.FirstIndex.Should().BeLessThan(items.Count, $"под заголовком «{h.Title}» есть товар {tag}");
                var first = list.ContainerFromIndex(h.FirstIndex)!;
                first.Bounds.X.Should().BeLessThan(1, $"первая плитка группы «{h.Title}» — с начала строки {tag}");
                first.Bounds.Y.Should().BeApproximately(b.Bottom, 1, $"первая плитка группы «{h.Title}» — сразу под заголовком {tag}");
                CatalogCategoryGrouping.SameGroup(CatalogCategoryGrouping.KeyOf((CatalogProductTileVm)first.DataContext!), h.Key).Should().BeTrue(tag);
            }

            // плитки одной строки — одной категории (новая категория не продолжает чужую строку)
            foreach (var row in items.GroupBy(i => Math.Round(i.Bounds.Y)))
                row.Select(i => CatalogCategoryGrouping.KeyOf((CatalogProductTileVm)i.DataContext!)).Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(1, tag);

            SavePng(built.Window, $"catalog-groups-{Code(lang)}-{width:0}x{height:0}-{mode.ToString().ToLowerInvariant()}-{theme.ToString().ToLowerInvariant()}");

            // переключатель «Разделять каталог по категориям» — заголовков нет, прежний порядок
            UserPreferences.SetCatalogGroupByCategory(false);
            UiShot.Settle(6);
            built.Vm.IsGroupedByCategory.Should().BeFalse(tag);
            ((CatalogGroupedPanel)VisibleList(built.Window).ItemsPanelRoot!).Headers.Should().BeEmpty(tag);
            UserPreferences.SetCatalogGroupByCategory(true);
            UiShot.Settle(6);
            var again = (CatalogGroupedPanel)VisibleList(built.Window).ItemsPanelRoot!;
            again.Headers.Should().HaveCount(6, tag);
            // слой рисования заголовков пережил пересборку страницы и остался последним визуальным ребёнком (поверх, но не среди плиток)
            again.GetVisualChildren().Last().Should().NotBeOfType<ListBoxItem>(tag);
            again.GetVisualChildren().Count().Should().Be(again.Children.Count + 1, tag);

            // поиск — порядок по совпадению, без заголовков
            built.Vm.RestoreState(new CatalogPanelState { SearchText = "со" });
            UiShot.Settle(6);
            built.Vm.IsGroupedByCategory.Should().BeFalse(tag);
            ((CatalogGroupedPanel)VisibleList(built.Window).ItemsPanelRoot!).Headers.Should().BeEmpty(tag);
        }
        finally
        {
            built.Dispose();
        }
    }

    /// <summary>2026-11-27 (живая проверка 1.17.97, D1): после смены темы «на лету» (светлая → тёмная и обратно; палитру кладёт AccentThemeService прямо в ресурсы приложения)
    /// названия категорий и числа остаются читаемыми — в «Табличном» виде они показывали цвет прежней темы (тёмно-серый на тёмном). Проверка по пикселям снимка:
    /// в полосе названия есть и фон, и текст с контрастом не ниже 4,5.</summary>
    [AvaloniaTheory]
    [InlineData(UiTheme.Light, UiTheme.Dark, CatalogViewMode.Table)]
    [InlineData(UiTheme.Light, UiTheme.Dark, CatalogViewMode.Cards)]
    [InlineData(UiTheme.Dark, UiTheme.Light, CatalogViewMode.Table)]
    [InlineData(UiTheme.Dark, UiTheme.Light, CatalogViewMode.Cards)]
    public async Task Headers_StayReadable_AfterThemeSwitch(UiTheme from, UiTheme to, CatalogViewMode mode)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(from, AppLanguage.Russian);
        UserPreferences.Instance.CatalogGroupByCategory = true;
        var built = CatalogListFixture.Create(1024, 600, 0, null, AddShop);
        try
        {
            built.Vm.SetViewMode(mode);
            built.Window.Show();
            UiShot.Settle(8);
            UiShot.Render(built.Window).Dispose(); // панель успела нарисоваться в прежней теме
            UiShot.Setup(to, AppLanguage.Russian);
            UiShot.Settle(8);
            using var bmp = UiShot.Render(built.Window);
            var panel = (CatalogGroupedPanel)VisibleList(built.Window).ItemsPanelRoot!;
            panel.Headers.Should().NotBeEmpty();
            var checkedCount = 0;
            foreach (var h in panel.Headers)
            {
                var at = panel.TranslatePoint(h.TitleBounds.BottomRight, built.Window) ?? default;
                if (at.Y > bmp.Height - 40 || at.X > bmp.Width - 2 || at.Y < 0)
                    continue; // нижние группы за краем окна (страница длиннее экрана) — их пикселей в снимке нет
                checkedCount++;
                var tag = $"({from}→{to} {mode} «{h.Title}»)";
                HeaderContrast(bmp, panel, built.Window, h.TitleBounds).Should().BeGreaterThanOrEqualTo(4.5, "название категории читаемо " + tag);
                if (h.CountText.Length > 0)
                    HeaderContrast(bmp, panel, built.Window, h.CountBounds).Should().BeGreaterThanOrEqualTo(2.5, "число товаров читаемо " + tag);
            }
            checkedCount.Should().BeGreaterThanOrEqualTo(2, "в кадре есть хотя бы две группы");
        }
        finally
        {
            built.Dispose();
        }
    }

    /// <summary>2026-11-27 (D1, механизм): палитру кладут прямо в ресурсы приложения (AccentThemeService) без смены ThemeVariant — заголовки групп уже нарисованы и должны
    /// сами подхватить новый цвет текста (раньше хранили кисть, взятую при подготовке, и оставались прежнего цвета, пока не сменятся товары).</summary>
    [AvaloniaTheory]
    [InlineData(CatalogViewMode.Table)]
    [InlineData(CatalogViewMode.Cards)]
    public async Task Headers_FollowPaletteChange_WithoutThemeVariantChange(CatalogViewMode mode)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Dark, AppLanguage.Russian);
        UserPreferences.Instance.CatalogGroupByCategory = true;
        var app = Avalonia.Application.Current!;
        app.Resources.TryGetValue("BrushText", out var oldText);
        var built = CatalogListFixture.Create(1024, 600, 0, null, AddShop);
        try
        {
            built.Vm.SetViewMode(mode);
            built.Window.Show();
            UiShot.Settle(8);
            UiShot.Render(built.Window).Dispose();
            app.Resources["BrushText"] = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 0)); // палитра сменилась, вариант темы тот же
            UiShot.Settle(8);
            using var bmp = UiShot.Render(built.Window);
            var panel = (CatalogGroupedPanel)VisibleList(built.Window).ItemsPanelRoot!;
            var h = panel.Headers[0];
            var p = panel.TranslatePoint(h.TitleBounds.TopLeft, built.Window) ?? default;
            var red = 0;
            for (var x = (int)p.X; x < Math.Min(bmp.Width, (int)Math.Ceiling(p.X + h.TitleBounds.Width)); x++)
                for (var y = (int)p.Y; y < Math.Min(bmp.Height, (int)Math.Ceiling(p.Y + h.TitleBounds.Height)); y++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > 200 && c.Green < 90 && c.Blue < 90)
                        red++;
                }
            red.Should().BeGreaterThan(10, $"название «{h.Title}» перекрасилось новой палитрой ({mode})");
        }
        finally
        {
            if (oldText is not null)
                app.Resources["BrushText"] = oldText;
            built.Dispose();
        }
    }

    /// <summary>Контраст самого яркого и самого тёмного пикселя в прямоугольнике (координаты панели → окно): фон против текста.</summary>
    private static double HeaderContrast(SKBitmap bmp, Visual panel, Visual window, Rect r)
    {
        var p = panel.TranslatePoint(r.TopLeft, window) ?? default;
        double lo = 1, hi = 0;
        for (var x = (int)Math.Max(0, Math.Floor(p.X)); x < Math.Min(bmp.Width, Math.Ceiling(p.X + r.Width)); x++)
            for (var y = (int)Math.Max(0, Math.Floor(p.Y)); y < Math.Min(bmp.Height, Math.Ceiling(p.Y + r.Height)); y++)
            {
                var c = bmp.GetPixel(x, y);
                var l = 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
                lo = Math.Min(lo, l);
                hi = Math.Max(hi, l);
            }
        return (hi + 0.05) / (lo + 0.05);

        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
