using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): окно «Списки к школе» и редактор списков в Avalonia.Headless на 1024×600 и 1366×768 (ru, ky):
/// 12 позиций (есть, нет в каталоге, нет в наличии, мало), галочки по умолчанию, итог, «В чек» отдаёт отмеченные строки; текст не обрезан.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\school-lists-{язык}-{ширина}x{высота}.png (и school-lists-editor-…).</summary>
[Collection("UiScenarios")]
public sealed class SchoolListsUiTests
{
    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static CatalogProductTileVm Tile(string id, string title, string price, double stock) =>
        new(id, title, price + " сом", false) { Quantity = stock };

    private static (List<SchoolList> Lists, Dictionary<string, CatalogProductTileVm> Catalog) Data()
    {
        var names = new[]
        {
            "Тетрадь 12 л", "Ручка синяя", "Карандаш простой", "Пенал", "Линейка 20 см", "Ластик", "Альбом для рисования", "Краски акварельные",
            "Пластилин 12 цветов", "Ножницы детские", "Папка для тетрадей", "Дневник школьный",
        };
        var list = new SchoolList { Name = "1 класс" };
        var catalog = new Dictionary<string, CatalogProductTileVm>();
        for (var i = 0; i < names.Length; i++)
        {
            var id = "p" + i;
            list.Items.Add(new SchoolListItem { ProductId = id, Name = names[i], Quantity = i == 0 ? 10 : i == 6 ? 4 : i % 3 + 1 });
            if (i == 4)
                continue; // удалён из каталога
            catalog[id] = Tile(id, names[i], (15 + i * 7) + ".50", i == 5 ? 0 : i == 6 ? 1 : 50);
        }
        var lists = new List<SchoolList>
        {
            list,
            new() { Name = "5 класс", Items = { new SchoolListItem { ProductId = "p0", Name = "Тетрадь 12 л", Quantity = 6 } } },
            new() { Name = "Офис-старт" },
        };
        return (lists, catalog);
    }

    private static string Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task List_window_shows_rows_warnings_total_and_sends_checked_rows(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (lists, catalog) = Data();
        var window = new SchoolListWindow(lists, id => catalog.GetValueOrDefault(id), canEdit: true);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var tag = $"{width}x{height} {lang}";
            var rows = window.RowsForTests();
            rows.Should().HaveCount(12, tag);
            rows.Count(r => r.Plan.Status == SchoolLineStatus.Missing).Should().Be(1);
            rows.Count(r => r.Plan.Status == SchoolLineStatus.OutOfStock).Should().Be(1);
            rows.Count(r => r.Plan.Status == SchoolLineStatus.NotEnough).Should().Be(1);
            rows.Count(r => r.Checked).Should().Be(10, "отмечены найденные с остатком; без остатка и удалённые — нет");
            rows.Where(r => !r.Plan.CanAdd).Should().OnlyContain(r => !r.Checked);

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "school-lists", langCode);
            clipped.Should().BeEmpty($"текст окна не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            Save(window, $"school-lists-{langCode}-{width}x{height}");

            // «В чек» отдаёт отмеченные строки с количеством из списка
            window.CommitForTests();
            window.Result.Should().HaveCount(10);
            window.Result.First(r => r.Tile.Id == "p0").Quantity.Should().Be(10);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_lists_show_hint_and_disable_add(bool canEdit)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new SchoolListWindow(new List<SchoolList>(), _ => null, canEdit);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.RowsForTests().Should().BeEmpty();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Concat(window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? "")).ToList();
            texts.Should().Contain(t => t.Contains("Списков пока нет"));
            (texts.Any(t => t.Contains("Править списки"))).Should().Be(canEdit);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Editor_shows_lists_and_items_without_clipping(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (lists, catalog) = Data();
        var window = new SchoolListsEditorWindow(lists, catalog.Values.ToList());
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var tag = $"{width}x{height} {lang}";
            window.ListsForTests.Should().HaveCount(3);
            window.ListsForTests[0].Items.Should().HaveCount(12);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "school-lists-editor", langCode);
            clipped.Should().BeEmpty($"текст редактора не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            Save(window, $"school-lists-editor-{langCode}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
