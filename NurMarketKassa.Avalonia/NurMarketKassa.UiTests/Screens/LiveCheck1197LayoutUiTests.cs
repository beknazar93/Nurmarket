using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (живая проверка 1.17.97, D4 и D6): раскладка «Карточки» — цена не под кнопкой «+», не обрезана и не вылезает из плитки; окно «QR для входа» в программе
/// владельца после выпуска QR — все четыре кнопки внизу целиком в окне (на пяти языках; кнопка «Выпустить QR» раньше обрезалась справа).</summary>
[Collection("UiScenarios")]
public sealed class LiveCheck1197LayoutUiTests
{
    public LiveCheck1197LayoutUiTests() => TestHost.Ensure();

    private static CatalogProductTileVm Tile(string id, string title, string price, double qty)
    {
        var t = new CatalogProductTileVm(id, title, price + " сом", false) { Barcode = "46" + (100000000000L + id.GetHashCode() % 1000000).ToString("D11"), Unit = "шт", Quantity = qty, Category = "аксесуары" };
        ProductUnitNormalizer.ApplyToTile(t);
        return t;
    }

    private static Rect InWindow(Visual v, Visual window) =>
        new(v.TranslatePoint(default, window) ?? default, v.Bounds.Size);

    [AvaloniaTheory]
    [InlineData(UiTheme.Light, 140.0)]
    [InlineData(UiTheme.Light, 172.0)]
    [InlineData(UiTheme.Light, 210.0)]
    [InlineData(UiTheme.Dark, 156.0)]
    public void CardsTile_PriceIsNotUnderPlusButton_AndFitsTheTile(UiTheme theme, double tileWidth)
    {
        UiShot.Setup(theme, AppLanguage.Russian);
        var view = new CardsLayoutView();
        view.Resources["LxTileWidth"] = tileWidth;
        var window = new Window { Width = 1366, Height = 768, Content = view };
        try
        {
            window.Show();
            UiShot.Settle(6);
            var list = view.FindControl<ListBox>("CardsList")!;
            list.GetVisualAncestors().OfType<ScrollViewer>().ToList().ForEach(s => s.IsVisible = true);
            list.ItemsSource = new[]
            {
                Tile("a1", "Байэл", "208.00", 31),
                Tile("a2", "Механические часы", "7000.00", 99),
                Tile("a3", "сумка гучи", "3675.00", 196),
                Tile("a4", "Дорогой товар", "99999.99", 99999.999),
            };
            UiShot.Settle(8);
            UiShot.Render(window).Dispose();

            var tiles = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lx-tile") && b.IsEffectivelyVisible).ToList();
            tiles.Should().HaveCount(4);
            foreach (var tile in tiles)
            {
                var name = ((CatalogProductTileVm)tile.DataContext!).Title;
                var tag = $"({theme} плитка {tileWidth} «{name}»)";
                var price = tile.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("lx-price"));
                var plus = tile.GetVisualDescendants().OfType<Border>().First(b => b.Width == 30 && b.Height == 30);
                var priceBox = InWindow(price, window);
                var plusBox = InWindow(plus, window);
                var tileBox = InWindow(tile, window);

                priceBox.Intersects(plusBox).Should().BeFalse("цена не под кнопкой «+» " + tag);
                price.Bounds.Width.Should().BeGreaterThanOrEqualTo(price.DesiredSize.Width - 0.5, "цена не обрезана " + tag);
                priceBox.Right.Should().BeLessThanOrEqualTo(tileBox.Right + 0.5, "цена не вылезает из плитки " + tag);
                plusBox.Right.Should().BeLessThanOrEqualTo(tileBox.Right + 0.5, "«+» внутри плитки " + tag);
                plusBox.Bottom.Should().BeLessThanOrEqualTo(priceBox.Top + 1, "«+» над строкой цены " + tag);

                // остаток не заходит под «+»
                var stock = tile.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && Grid.GetRow(t) == 2 && t.Parent is Grid);
                if (stock is not null)
                {
                    var stockBox = InWindow(stock, window);
                    new Rect(stockBox.X, stockBox.Y, Math.Min(stockBox.Width, stock.DesiredSize.Width), stockBox.Height).Intersects(plusBox).Should().BeFalse("остаток не под «+» " + tag);
                }
            }
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Turkish)]
    [InlineData(AppLanguage.Uzbek)]
    public void CashierBadgeWindow_AfterIssue_AllFooterButtonsFitInTheWindow(AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var type = typeof(LoginWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Settings.CashierBadgeWindow")!;
        var window = (Window)Activator.CreateInstance(type, "badge-ui-42", "Кассир Тест", "kassir@nbs.kg")!;
        try
        {
            // После выпуска: картинка QR, «Сохранить картинку» и «Отозвать» видны — внизу четыре кнопки.
            void Show(string field)
            {
                var f = type.GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                ((Control)f.GetValue(window)!).IsVisible = true;
            }
            Show("_qrFrame");
            Show("_save");
            Show("_revoke");
            window.Show();
            UiShot.Settle(8);
            UiShot.Render(window).Dispose();

            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content is string).ToList();
            buttons.Count.Should().BeGreaterThanOrEqualTo(4, "Готово, Сохранить картинку, Отозвать, Выпустить QR");
            var width = window.Bounds.Width;
            foreach (var b in buttons)
            {
                var box = InWindow(b, window);
                var tag = $"({lang} «{b.Content}»)";
                box.Left.Should().BeGreaterThanOrEqualTo(0, "кнопка не левее окна " + tag);
                box.Right.Should().BeLessThanOrEqualTo(width - 8, "кнопка целиком в окне, с полем справа " + tag);
                b.Bounds.Width.Should().BeGreaterThanOrEqualTo(b.DesiredSize.Width - 0.5, "кнопка не сжата и не обрезана " + tag);
            }
            for (var i = 1; i < buttons.Count; i++)
                for (var j = 0; j < i; j++)
                    InWindow(buttons[i], window).Intersects(InWindow(buttons[j], window)).Should().BeFalse($"кнопки «{buttons[i].Content}» и «{buttons[j].Content}» не налезают ({lang})");
        }
        finally { window.Close(); UiShot.Pump(); }
    }
}
