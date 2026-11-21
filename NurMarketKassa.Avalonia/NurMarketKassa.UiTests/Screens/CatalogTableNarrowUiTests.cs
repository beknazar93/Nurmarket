using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (обход кассы, п. 21–22): таблица каталога на 1024×600 (панель каталога ≈ 500 px) — название товара не режется по полуслову
/// (переносится на 2 строки), колонки не налезают друг на друга, клик по строке добавляет товар, лупа фото его не добавляет; вкладки чеков — сумма
/// активной вкладки видна целиком. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\catalog-table-*.png, receipt-tabs-*.png.</summary>
[Collection("UiScenarios")]
public sealed class CatalogTableNarrowUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new[] { 500.0, 560, 650 }.SelectMany(w => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { w, l }));

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private static string MakePhoto(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "catalog-narrow");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        using var bmp = new SKBitmap(120, 120);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0xE8, 0xEE, 0xF6));
            canvas.DrawRect(6, 6, 108, 108, new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6), Style = SKPaintStyle.Stroke, StrokeWidth = 8 });
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void SavePng(Window w, string name)
    {
        using var shot = UiShot.Render(w);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    /// <summary>Весь текст виден: при ширине блока он занимает не больше строк, чем блоку разрешено (нет «…»).</summary>
    private static bool FullyShown(TextBlock t)
    {
        var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
        var ft = new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null) { MaxTextWidth = Math.Max(1, t.Bounds.Width) };
        return ft.Height <= t.Bounds.Height + 2;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Table_Names_NotClipped_NoOverlap(double width, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var built = CatalogListFixture.Create(width, 600, 0, null, items =>
        {
            foreach (var t in items)
                t.Description = "Описание"; // у товара есть описание — значок «i» виден
        });
        try
        {
            using var _ = UiShot.Render(built.Window);
            var root = built.View.GetVisualDescendants().OfType<Border>().First(b => b.Name == "CatalogRoot");
            root.Classes.Contains("tbl-narrow").Should().BeTrue($"список уже 760 px ({width})");
            var list = built.View.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TableListBox");

            var rows = list.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("tbl-row") && g.IsEffectivelyVisible).ToList();
            rows.Should().HaveCountGreaterThanOrEqualTo(3);
            foreach (var row in rows)
            {
                Rect R(Control c) => new(c.TranslatePoint(default, row)!.Value, c.Bounds.Size);
                var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("tbl-title"));
                var price = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("tbl-price-amount"));
                var actions = row.GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("tbl-actions"));
                var info = actions.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("info-btn"));
                var star = actions.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tbl-star"));

                var tag = $"«{title.Text}» ({width}, {lang})";
                FullyShown(title).Should().BeTrue($"название целиком, максимум в 3 строки: {tag}; ширина блока {title.Bounds.Width:0}");
                title.Bounds.Width.Should().BeGreaterThan(150, $"название получает всё свободное место: {tag}");
                R(title).Right.Should().BeLessThanOrEqualTo(R(price).Left + 0.5, $"название не налезает на цену: {tag}");
                R(price).Right.Should().BeLessThanOrEqualTo(R(actions).Left + 0.5, $"цена не налезает на значки: {tag}");
                info.IsEffectivelyVisible.Should().BeTrue();
                R(info).Intersects(R(star)).Should().BeFalse($"«i» и звезда не налезают друг на друга: {tag}");
                R(info).Width.Should().BeGreaterThanOrEqualTo(36);
                R(star).Width.Should().BeGreaterThanOrEqualTo(36, "зона касания звезды");
                R(star).Height.Should().BeGreaterThanOrEqualTo(40);
                R(star).Right.Should().BeLessThanOrEqualTo(row.Bounds.Width + 0.5);
            }

            // Заголовок колонок «Цена» стоит над ценами (не уехал после скрытия «сом»).
            var head = built.View.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("tbl-price-head"));
            var firstPrice = rows[0].GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("tbl-price-amount"));
            var headRight = head.TranslatePoint(new Point(head.Bounds.Width, 0), built.Window)!.Value.X;
            var priceRight = firstPrice.TranslatePoint(new Point(firstPrice.Bounds.Width, 0), built.Window)!.Value.X;
            Math.Abs(headRight - priceRight).Should().BeLessThanOrEqualTo(12, "заголовок «Цена» над колонкой цен");

            // «сом» в узком списке скрыт (колонка называется «Цена»).
            list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("tbl-currency")).Should().OnlyContain(t => !t.IsEffectivelyVisible);

            SavePng(built.Window, $"catalog-table-{Code(lang)}-{width:0}");
        }
        finally
        {
            built.Dispose();
        }
    }

    [AvaloniaTheory]
    [InlineData(500.0)]
    [InlineData(650.0)]
    public async Task Table_RowClick_Adds_PhotoLoupe_DoesNot(double width)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var photo = MakePhoto("row");
        var picked = new List<CatalogProductTileVm>();
        CatalogProductTileVm? withPhoto = null;
        var built = CatalogListFixture.Create(width, 600, 0, picked.Add, items =>
        {
            withPhoto = items.First(t => t.Id == "p1");
            withPhoto.ImageUrl = "https://x.kg/p1.jpg";
            withPhoto.ProductImagePath = photo;
        });
        var opened = new List<ProductPhotoViewerWindow>();
        ProductPhotoViewer.WindowCreated = opened.Add;
        try
        {
            built.Window.Show();
            UiShot.Settle(8);
            var row = built.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.DataContext == withPhoto && b.GetType() == typeof(Button) && Avalonia.Automation.AutomationProperties.GetName(b) == withPhoto!.Title);
            var title = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == withPhoto!.Title);
            UiDrive.Click(built.Window, title);
            picked.Should().ContainSingle().Which.Should().BeSameAs(withPhoto, "клик по строке (названию) добавляет товар");
            opened.Should().BeEmpty();

            picked.Clear();
            var loupe = built.Window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tbl-photo") && b.IsEffectivelyVisible && b.Child is Image);
            UiDrive.Click(built.Window, loupe);
            UiShot.Settle(6);
            picked.Should().BeEmpty("фото/лупа товар не добавляет");
            opened.Should().ContainSingle("нажатие на фото открывает просмотр");
            opened[0].Close();
            UiShot.Pump();
        }
        finally
        {
            ProductPhotoViewer.WindowCreated = null;
            foreach (var w in opened.Where(o => o.IsVisible))
                w.Close();
            built.Dispose();
        }
    }

    // ───────────── вкладки чеков ─────────────

    private static (Window Window, ScrollViewer Scroll, List<Button> Tabs, List<ReceiptTabLabel> Labels) Tabs(double width, params string[] titles)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var buttons = new List<Button>();
        var labels = new List<ReceiptTabLabel>();
        foreach (var title in titles)
        {
            var label = new ReceiptTabLabel { Text = title, [TextElement.FontSizeProperty] = 12.0, [TextElement.FontWeightProperty] = FontWeight.SemiBold };
            var b = new Button { Content = label, MinHeight = 44, Padding = new Thickness(9, 7), Focusable = false };
            buttons.Add(b);
            labels.Add(label);
            panel.Children.Add(b);
        }
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = panel };
        var window = new Window { Width = width, Height = 120, SizeToContent = SizeToContent.Manual, Content = scroll };
        return (window, scroll, buttons, labels);
    }

    [AvaloniaTheory]
    [InlineData(240.0, AppLanguage.Russian)]
    [InlineData(250.0, AppLanguage.Kyrgyz)]
    [InlineData(260.0, AppLanguage.Uzbek)]
    public async Task ReceiptTabs_ActiveTab_FullyVisible_WithFullAmount(double width, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var main = lang == AppLanguage.Kyrgyz ? "Негизги чек" : lang == AppLanguage.Uzbek ? "Asosiy chek" : "Основной чек";
        var (window, scroll, tabs, labels) = Tabs(width, $"{main} · 5,00", "Чек 2 · 25,00", "Чек 3 · 1 517,96");
        try
        {
            window.Show();
            UiShot.Settle(6);
            // Активной становится самая правая — как после «+ Новый чек».
            labels[2].IsActive = true;
            UiShot.Settle(8);

            var active = tabs[2];
            var left = active.TranslatePoint(default, scroll)!.Value.X;
            var right = left + active.Bounds.Width;
            left.Should().BeGreaterThanOrEqualTo(-0.5, "активная вкладка не уходит за левый край");
            right.Should().BeLessThanOrEqualTo(scroll.Bounds.Width + 0.5, "активная вкладка видна целиком, не под краем прокрутки");

            var amount = labels[2].GetVisualDescendants().OfType<TextBlock>().First(t => (t.Text ?? "").Contains("1 517,96"));
            FullyShown(amount).Should().BeTrue("сумма активной вкладки не обрезана");
            amount.Bounds.Width.Should().BeGreaterThan(40);

            // Теперь активной делаем первую — она тоже прокручивается в видимую область.
            labels[2].IsActive = false;
            labels[0].IsActive = true;
            UiShot.Settle(8);
            var l0 = tabs[0].TranslatePoint(default, scroll)!.Value.X;
            l0.Should().BeGreaterThanOrEqualTo(-0.5);
            (l0 + tabs[0].Bounds.Width).Should().BeLessThanOrEqualTo(scroll.Bounds.Width + 0.5);

            SavePng(window, $"receipt-tabs-{Code(lang)}-{width:0}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [Theory]
    [InlineData("Основной чек · 5,00", "Основной чек", "5,00")]
    [InlineData("Чек 2 · 1 517,96", "Чек 2", "1 517,96")]
    [InlineData("Чек 3", "Чек 3", "")]
    [InlineData("Чек · а · 7,00", "Чек · а", "7,00")]
    public void ReceiptTabLabel_Split_NameAndAmount(string title, string name, string amount)
    {
        var (n, a) = ReceiptTabLabel.Split(title);
        n.Should().Be(name);
        a.Should().Be(amount);
    }
}
