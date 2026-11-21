using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 («И наименование и цена не на одном уровне!», фото сенсорной кассы): окно кассы в Avalonia.Headless. (а) строка товара в чеке — название «#1. …» и сумма справа
/// сверху строки, на одной высоте (одна высота строки текста); (б) панель «Ещё» — все плитки одной ширины и одной высоты, подпись по центру и целиком (в том числе «Печать последнего чека» в две строки);
/// (в) плитка каталога и таблица — цена и название на одном уровне со своим рядом. 1024×600 и 1366×768, ru / ky, светлая, тёмная и сенсорная темы.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\basket-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BasketAlignmentUiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public BasketAlignmentUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        SetShift(_savedShift);
    }

    private static NurMarketKassa.Ui.Shared.IAppSession Session => TestHost.Services.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>();

    private static void SetShift(string? shiftId)
    {
        PosApp.ActiveShiftId = shiftId;
        Session.ActiveShiftId = shiftId;
    }

    public static IEnumerable<object[]> Cases() =>
        from theme in new[] { UiTheme.Light, UiTheme.Dark, UiTheme.Touch }
        from size in new[] { new UiSize(1024, 600), new UiSize(1366, 768) }
        from lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }
        select new object[] { theme, size.Width, size.Height, lang };

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static BasketPanelViewModel Basket(Window main) => ((MainWindowViewModel)main.DataContext!).Basket;

    private static Rect InWindow(Control c, Window w) =>
        new(c.TranslatePoint(new Point(0, 0), w) ?? default, c.Bounds.Size);

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Basket_Line_And_MorePanel_AreAligned(UiTheme theme, int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        SetShift(PosApp.ActiveShiftId is { Length: > 0 } open ? open : Guid.NewGuid().ToString());
        UiShot.Setup(theme, lang);
        var size = new UiSize(width, height);
        var tag = $"{theme} {width}x{height} {lang}";
        var slug = $"basket-{theme.ToString().ToLowerInvariant()}-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var main = (MainWindow)ScreenCatalog.Get("main-kassa").Create();
        UiRobot.Reset(main);
        using (var _ = UiShot.Render(main, size)) { }
        main.Activate();
        UiShot.Settle(10, 40);
        using var guard = UiRobot.Guard();
        try
        {
            var tile = UiRobot.WaitFor(() => FindTile(main, "Хлеб") is not null, 8000) ? FindTile(main, "Хлеб")! : throw new InvalidOperationException("Нет плитки «Хлеб»: " + tag);
            UiDrive.Click(main, tile);
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1).Should().BeTrue($"товар добавлен в чек ({tag})");
            UiShot.Settle(6, 30);

            // (а) строка товара: название и сумма — одна высота строки, один верх.
            var line = main.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("cart-line") && b.IsEffectivelyVisible);
            var title = line.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith("#1.", StringComparison.Ordinal));
            var amount = line.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Classes.Contains("num"));
            var titleRect = InWindow(title, main);
            var amountRect = InWindow(amount, main);
            amountRect.Top.Should().BeApproximately(titleRect.Top, 1.5, $"название и сумма сверху строки на одном уровне ({tag})");
            amount.Bounds.Height.Should().BeApproximately(title.Bounds.Height, 1.5, $"одна высота строки текста у названия и суммы ({tag})");
            Save(main, slug + "-line");

            // (б) панель «Ещё»: плитки одной ширины и высоты, подпись по центру.
            Basket(main).IsMoreActionsVisible = true;
            UiShot.Settle(8, 40);
            var view = main.GetVisualDescendants().OfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.BasketPanelView>().First();
            var grid = view.MoreActionsGrid;
            var tiles = grid.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
            tiles.Count.Should().BeGreaterThanOrEqualTo(7, $"в «Ещё» все плитки на месте ({tag})");
            var h0 = tiles[0].Bounds.Height;
            var w0 = tiles[0].Bounds.Width;
            foreach (var b in tiles)
            {
                b.Bounds.Height.Should().BeApproximately(h0, 1, $"плитки одной высоты ({tag})");
                b.Bounds.Width.Should().BeApproximately(w0, 1, $"плитки одной ширины ({tag})");
                var label = b.GetVisualDescendants().OfType<TextBlock>().First();
                label.Bounds.Height.Should().BeLessThanOrEqualTo(b.Bounds.Height, $"подпись целиком в плитке «{label.Text}» ({tag})");
                label.Bounds.Width.Should().BeLessThanOrEqualTo(b.Bounds.Width, $"подпись не шире плитки «{label.Text}» ({tag})");
                var lc = InWindow(label, main).Center;
                var bc = InWindow(b, main).Center;
                lc.Y.Should().BeApproximately(bc.Y, 2, $"подпись по центру плитки по вертикали «{label.Text}» ({tag})");
                lc.X.Should().BeApproximately(bc.X, 2, $"подпись по центру плитки по горизонтали «{label.Text}» ({tag})");
            }
            // «Печать последнего чека» не рвёт ряд: в одной строке сетки у плиток один верх.
            var rows = tiles.GroupBy(b => (int)Math.Round(InWindow(b, main).Top / 4)).Select(g => g.Count()).ToList();
            rows.Count.Should().BeLessThanOrEqualTo(2, $"7–8 плиток — два ряда по 4 ({tag})");
            Save(main, slug + "-more");
            Basket(main).IsMoreActionsVisible = false;
            UiShot.Settle(3);

            // (в) плитка каталога: цена на одном уровне у всех плиток ряда (нижний край блока цены).
            var priceBlocks = main.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Classes.Contains("num") && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null)
                .Select(t => InWindow(t, main)).ToList();
            foreach (var row in priceBlocks.GroupBy(r => (int)Math.Round(r.Top / 40)).Where(g => g.Count() > 1))
                (row.Max(r => r.Bottom) - row.Min(r => r.Bottom)).Should().BeLessThanOrEqualTo(2.5, $"цены одного ряда плиток каталога на одном уровне ({tag})");
        }
        finally
        {
            UiRobot.CloseAll();
            UiRobot.Main = null;
            main.Hide(); // 2026-11-21: закрытие окна кассы подвешивает хост Headless (как в KassaLookGalleryTests) — прячем
            UiShot.Pump();
        }
    }

    private static TextBlock? FindTile(Window main, string title) =>
        main.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == title
            && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null);
}
