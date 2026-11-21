using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-07 (жалоба владельца «Где в приёмке штрихкод?», фото ноутбука): «Склад → Приёмка» в узком виде (таблица уже 1300 px) прятала колонку
/// «Штрихкод» и серую подстроку под названием, у нового товара без названия был виден один значок «новый», заголовки рвались посреди слова
/// («Принимае/м», «Наценк/а %»). Окно настоящее (Avalonia.Headless), две строки: найденный товар (штрихкод, длинное название) и новый без названия и штрихкода.
/// Размеры — логические: 1024×600 и 1280×720 как есть, 1093×614 = 1366×768 при масштабе Windows 125 %, 1366×768 при 100 %.
/// Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\warehouse-receiving-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseReceivingLayoutTests
{
    private const string Barcode = "4600000000011";
    private const string LongName = "Реле зарядки Audi красное длинное название для проверки обрезки";

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1093, 614), (1280, 720), (1300, 740), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_Rows_ShowBarcode_DoNotOverlap_AndHeadersKeepWords(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var tile = new CatalogProductTileVm("w1", LongName, "250 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 3, PurchasePrice = 185 };
            var found = PurchaseReceivingService.FromTile(tile, 1);
            found.EnableReceivingPricing();
            var created = new ReceivingLineVm { Barcode = "", ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(found);
            vm.ReceivingLines.Add(created);
            UiShot.Settle(10);

            var list = UiDrive.Find<ItemsControl>(window, "ReceivingCards"); // 2026-11-21: строки приёмки — карточки
            var tag = $"{width}x{height} {lang}";

            // --- карточки: видны, не наезжают друг на друга, содержимое внутри своей карточки
            var cards = RcvCards.Cards(window).OrderBy(c => c.TranslatePoint(default, window)!.Value.Y).ToList();
            cards.Should().HaveCountGreaterThanOrEqualTo(1, tag); // на низком окне вторая карточка может уйти за нижний край списка (виртуализация)
            for (var i = 0; i < cards.Count; i++)
            {
                var top = cards[i].TranslatePoint(default, window)!.Value.Y;
                if (i > 0)
                    top.Should().BeGreaterThanOrEqualTo(cards[i - 1].TranslatePoint(default, window)!.Value.Y + cards[i - 1].Bounds.Height - 0.5, $"карточки не наезжают ({tag})");
                foreach (var text in cards[i].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var y = text.TranslatePoint(default, cards[i])!.Value.Y;
                    y.Should().BeGreaterThanOrEqualTo(-0.5, $"«{text.Text}» не выше своей карточки ({tag})");
                    (y + text.Bounds.Height).Should().BeLessThanOrEqualTo(cards[i].Bounds.Height + 0.5, $"«{text.Text}» не ниже своей карточки ({tag})");
                }
                // последняя кнопка (✕) целиком в списке: горизонтальной прокрутки нет
                var cross = cards[i].GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible);
                cross.TranslatePoint(new Point(cross.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({tag})");
            }

            // --- штрихкод найденного товара виден (подпись под значком источника), название — широкое поле
            var foundCard = RcvCards.Card(window, found);
            var barcodeText = foundCard.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && (t.Text ?? "").StartsWith(Barcode, StringComparison.Ordinal));
            barcodeText.Should().NotBeNull($"штрихкод товара виден в карточке ({tag})");
            Need(barcodeText!).Should().BeLessThanOrEqualTo(barcodeText!.Bounds.Width + 1, $"штрихкод не обрезан ({tag})");
            RcvCards.Field(window, found, "name").Text.Should().Be(LongName, $"название в поле ({tag})");
            RcvCards.Field(window, found, "name").Bounds.Width.Should().BeGreaterThanOrEqualTo(200, $"поле названия не ужато ({tag})");

            // --- новый товар (если карточка в видимой части списка): значок источника, подпись «Неизвестно», вписывается название и штрихкод
            if (cards.Any(c => ReferenceEquals(((BulkScanRow)c.DataContext!).Line, created)))
            {
                var newCard = RcvCards.Card(window, created);
                var newTexts = newCard.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0).Select(t => t.Text ?? "").ToList();
                newCard.GetVisualDescendants().OfType<ReceivingSourceBadge>().Any(b => b.IsEffectivelyVisible && b.Source == ReceivingSource.Unknown).Should().BeTrue($"значок источника «Неизвестно» ({tag})");
                newTexts.Any(t => t.StartsWith(created.SourceText, StringComparison.Ordinal)).Should().BeTrue($"подпись источника «{created.SourceText}» ({tag})");
                var nameEditor = RcvCards.Field(window, created, "name");
                nameEditor.Bounds.Height.Should().BeGreaterThanOrEqualTo(30, $"поле названия не схлопнуто ({tag})");
                nameEditor.Watermark.Should().NotBeNullOrWhiteSpace();
                var barEditor = RcvCards.Field(window, created, "barcode");
                barEditor.Bounds.Height.Should().BeGreaterThanOrEqualTo(30, $"поле штрихкода не схлопнуто ({tag})");
                barEditor.Bounds.Width.Should().BeGreaterThanOrEqualTo(100, $"поле штрихкода читаемо ({tag})");
                barEditor.TranslatePoint(new Point(0, barEditor.Bounds.Height), newCard)!.Value.Y.Should().BeLessThanOrEqualTo(newCard.Bounds.Height + 0.5, $"поле штрихкода в своей карточке ({tag})");
            }

            // --- шапка колонок над карточками: ни одно слово не рвётся посреди (длиннейшее слово влезает по ширине)
            foreach (var t in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.GetVisualAncestors().OfType<Border>().All(x => !RcvCards.IsCard(x))))
            {
                var longest = (t.Text ?? "").Replace("­", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).First();
                var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
                var need = new FormattedText(longest, CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null).Width;
                t.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 0.5, $"слово «{longest}» шапки «{t.Text}» не должно рваться ({tag})");
            }

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"warehouse-receiving-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static double Need(TextBlock t)
    {
        var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
        return new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection, typeface, t.FontSize, null).Width;
    }
}
