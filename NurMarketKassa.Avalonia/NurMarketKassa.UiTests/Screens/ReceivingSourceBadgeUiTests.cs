using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

/// <summary>2026-11-09 (владелец: «Приёмка: 1. редактировать наименование. 2. вначале указывать источник — база / база NurCRM / неизвестно / из интернета, значками, при наведении — что значит»
/// + «не найден — только предложить найти в интернете, по нажатию»). Настоящие окна в Avalonia.Headless: «Склад → Приёмка» (4 строки — по одной на источник) и «Закупки → Приём товара».
/// Проверяем при 1024×600 и 1366×768 (ru, ky): значок источника — первый в строке, левее названия и правее «№»; у каждого — подсказка (ToolTip) и имя для скринридера; штрихкод виден;
/// строки не наезжают и нет горизонтальной прокрутки; название правится у товара склада (переименование отмечается), кнопка «Найти в интернете» только у «Неизвестно» и сама ничего не ищет.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\receiving-source-{окно}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingSourceBadgeUiTests
{
    private const string Barcode = "4600000000011";
    private const string UnknownBarcode = "4607000000012";

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static ReceivingLineVm Line(ReceivingSource source, string barcode, string name) => new()
    {
        Barcode = barcode, ProductName = name, OriginalName = source == ReceivingSource.Warehouse ? name : "", Unit = "шт", Source = source, Quantity = 1, PurchasePrice = 100, SalePrice = 130,
        OriginalPurchasePrice = source == ReceivingSource.Warehouse ? 100 : 0, OriginalSalePrice = source == ReceivingSource.Warehouse ? 130 : 0,
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task WarehouseReceiving_ShowsSourceBadgeFirst_WithTooltip_AndOffersInternetOnlyForUnknown(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var calls = 0;
        var oldProvider = ReceivingInternetLookup.Provider;
        ReceivingInternetLookup.Provider = (_, _) =>
        {
            calls++;
            return Task.FromResult<ReceivingInternetLookup.Found?>(null);
        };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var warehouse = Line(ReceivingSource.Warehouse, Barcode, "Молоко 1 л");
            var global = Line(ReceivingSource.GlobalBase, "4601000000013", "Кефир из общей базы");
            var unknown = Line(ReceivingSource.Unknown, UnknownBarcode, "");
            var internet = Line(ReceivingSource.Internet, "4602000000014", "Печенье из интернета");
            foreach (var l in new[] { unknown, warehouse, global, internet })
                vm.ReceivingLines.Add(l);
            UiShot.Settle(10);

            var tag = $"{width}x{height} {lang}";
            // 2026-11-21: строки приёмки — карточки (объединение с «Массовым добавлением»): значок источника — первым в карточке, у каждой строки своя подпись и подсказка.
            var cards = RcvCards.Cards(window).OrderBy(c => c.TranslatePoint(default, window)!.Value.Y).ToList();
            cards.Count.Should().BeGreaterThanOrEqualTo(1, tag); // на низком окне нижние карточки уходят за край списка (виртуализация)
            for (var i = 1; i < cards.Count; i++)
                cards[i].TranslatePoint(default, window)!.Value.Y.Should().BeGreaterThanOrEqualTo(cards[i - 1].TranslatePoint(default, window)!.Value.Y + cards[i - 1].Bounds.Height - 0.5, $"карточки не наезжают ({tag})");
            foreach (var card in cards)
            {
                var line = ((BulkScanRow)card.DataContext!).Line;
                var info = $"{line.Source} ({tag})";
                var badge = card.GetVisualDescendants().OfType<ReceivingSourceBadge>().Single(x => x.IsEffectivelyVisible);
                badge.Source.Should().Be(line.Source, info);
                badge.Bounds.Width.Should().BeGreaterThanOrEqualTo(20, $"значок виден ({info})");
                (card.TranslatePoint(new Point(card.Bounds.Width, 0), window)!.Value.X).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({tag})");

                // подсказка при наведении и имя для скринридера: «Название: пояснение» на языке интерфейса
                var tip = ToolTip.GetTip(badge) as string;
                tip.Should().NotBeNullOrWhiteSpace($"у значка есть подсказка ({info})");
                tip.Should().Contain(ReceivingLineVm.SourceTitle(line.Source)).And.Contain(ReceivingLineVm.SourceHintFor(line.Source), info);
                AutomationProperties.GetName(badge).Should().Be(tip, $"имя значка для скринридера ({info})");

                // значок — первый в строке: левее штрихкода и названия
                var badgeX = badge.TranslatePoint(default, card)!.Value.X;
                var nameBox = card.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "name") && t.IsEffectivelyVisible);
                badgeX.Should().BeLessThan(nameBox.TranslatePoint(default, card)!.Value.X, $"значок левее названия — первый в строке ({info})");
                if (line.ProductName.Length > 0)
                    nameBox.Text.Should().Be(line.ProductName, info);
                nameBox.Bounds.Width.Should().BeGreaterThanOrEqualTo(60, $"название читается ({info})");
                // подпись источника под штрихкодом совпадает с названием источника
                card.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith(line.SourceText, StringComparison.Ordinal)).Should().BeTrue($"подпись источника «{line.SourceText}» ({info})");
                // штрихкод виден: подпись у товара склада и общей базы, поле у нового товара
                var barcodeShown = card.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && (t.Text ?? "").StartsWith(line.Barcode, StringComparison.Ordinal))
                    || card.GetVisualDescendants().OfType<TextBox>().Any(t => Equals(t.Tag, "barcode") && t.IsEffectivelyVisible && t.Text == line.Barcode);
                barcodeShown.Should().BeTrue($"штрихкод виден ({info})");

                // «Найти в интернете» — только у «Неизвестно»
                var net = card.GetVisualDescendants().OfType<Button>().Where(x => x.Tag as string == "rcv-netlink").ToList();
                var netVisible = net.Any(x => x.IsEffectivelyVisible);
                netVisible.Should().Be(line.Source == ReceivingSource.Unknown, $"кнопка поиска в интернете только у «Неизвестно» ({info})");
                if (netVisible)
                {
                    var b = net.Single(x => x.IsEffectivelyVisible);
                    b.Bounds.Width.Should().BeGreaterThanOrEqualTo(28, $"кнопка нажимаема ({info})");
                }
            }
            calls.Should().Be(0, "поиск в интернете сам не запускается — ни при показе строки «Неизвестно», ни при отрисовке");

            // нажатие кнопки: один вызов, отказ — строка остаётся «Неизвестно»
            var netButton = RcvCards.Card(window, unknown).GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "rcv-netlink" && x.IsEffectivelyVisible);
            UiDrive.Click(window, netButton);
            UiShot.Settle(8);
            calls.Should().Be(1, "одно нажатие — один запрос");
            unknown.Source.Should().Be(ReceivingSource.Unknown, "ничего не нашлось — «Неизвестно»");

            // название товара склада правится (переименование карточки) и отмечается
            var editor = RcvCards.Field(window, warehouse, "name");
            editor.IsReadOnly.Should().BeFalse($"название товара склада можно править ({tag})");
            editor.Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле названия не схлопнуто ({tag})");
            RcvCards.Card(window, warehouse).GetVisualDescendants().OfType<ReceivingSourceBadge>().Any(x => x.IsEffectivelyVisible).Should().BeTrue($"значок остаётся и при правке названия ({tag})");
            editor.Focus();
            UiShot.Settle(4);
            UiDrive.Type(window, editor, "Молоко 1 л 3,2 %");
            window.FindControl<TextBox>("ReceivingScanBox")!.Focus(); // уход курсора заканчивает правку
            UiShot.Settle(6);
            warehouse.ProductName.Should().Be("Молоко 1 л 3,2 %");
            warehouse.NameRenamed.Should().BeTrue();
            warehouse.NameOrUnitChanged.Should().BeTrue("название уйдёт в карточку при проведении");
            // 2026-11-10 (владелец: «Наименование должно быть длинным!»): подсказка — полное название, а под ним «было: …»
            (ToolTip.GetTip(RcvCards.Field(window, warehouse, "name")) as string).Should().Contain("Молоко 1 л").And.Contain(warehouse.NameWasHint, $"в подсказке «было: …» ({tag})").And.StartWith(warehouse.ProductName.Trim(), $"полное название в подсказке ({tag})");

            SaveShot(window, $"receiving-source-warehouse-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            ReceivingInternetLookup.Provider = oldProvider;
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Turkish)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Badge_HasOwnShapeTooltipAndAccessibleName_ForEverySource_InAllLanguages(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var shapes = new List<string>();
        foreach (var source in new[] { ReceivingSource.Warehouse, ReceivingSource.GlobalBase, ReceivingSource.Unknown, ReceivingSource.Internet })
        {
            var badge = new ReceivingSourceBadge { Source = source };
            var tip = ToolTip.GetTip(badge) as string;
            tip.Should().StartWith(ReceivingLineVm.SourceTitle(source) + ":", $"{source} / {lang}");
            tip.Should().Contain(ReceivingLineVm.SourceHintFor(source));
            AutomationProperties.GetName(badge).Should().Be(tip);
            // форма значка у каждого источника своя: смысл не держится на одном цвете
            shapes.Add(badge.Child switch
            {
                Avalonia.Controls.Shapes.Path p => "path:" + p.Data!.Bounds,
                TextBlock t => "text:" + t.Text,
                _ => "none",
            });
            badge.Source = source == ReceivingSource.Unknown ? ReceivingSource.Warehouse : ReceivingSource.Unknown; // смена источника перерисовывает значок и подсказку
            (ToolTip.GetTip(badge) as string).Should().StartWith(ReceivingLineVm.SourceTitle(badge.Source) + ":");
        }
        shapes.Should().OnlyHaveUniqueItems().And.NotContain("none");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task PurchasesReceive_NameIsEditableForExistingProduct_AndBadgeComesFirst(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        const string title = "Вода питьевая негазированная 0,5 л";
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", title, "30 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", title, "шт", 1.0, (double?)20) }));
            UiShot.Settle(12);
            var tag = $"{width}x{height} {lang}";

            var badge = window.GetVisualDescendants().OfType<ReceivingSourceBadge>().Single(b => b.IsEffectivelyVisible);
            badge.Source.Should().Be(ReceivingSource.Warehouse, tag);
            var tip = ToolTip.GetTip(badge) as string;
            tip.Should().Contain(ReceivingLineVm.SourceTitle(ReceivingSource.Warehouse)).And.Contain(ReceivingLineVm.SourceHintFor(ReceivingSource.Warehouse), tag);
            AutomationProperties.GetName(badge).Should().Be(tip, tag);

            // название — поле ввода и у товара склада; значок левее него, правее «№»
            var nameBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.Text == title);
            nameBox.Bounds.Width.Should().BeGreaterThanOrEqualTo(120, $"название читается ({tag})");
            badge.TranslatePoint(default, window)!.Value.X.Should().BeLessThan(nameBox.TranslatePoint(default, window)!.Value.X, $"значок первый, левее названия ({tag})");
            var number = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "1");
            badge.TranslatePoint(default, window)!.Value.X.Should().BeGreaterThan(number.TranslatePoint(default, window)!.Value.X, $"значок правее «№» ({tag})");
            (nameBox.TranslatePoint(new Point(nameBox.Bounds.Width, 0), window)!.Value.X).Should().BeLessThanOrEqualTo(window.Bounds.Width, $"поле названия в окне ({tag})");
            window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Tag as string == "rcv-netlink").Should().BeFalse("у товара склада кнопки поиска в интернете нет");

            // правка названия: пометка «Было: …» и подсветка
            UiDrive.Type(window, nameBox, title + " ПЭТ");
            UiShot.Settle(6);
            UiDrive.AllText(window).Should().Contain(title + " ПЭТ");
            window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith(lang == AppLanguage.Kyrgyz ? "Мурун:" : "Было:", StringComparison.Ordinal)).Should().BeTrue($"под названием — «Было: …» ({tag})");

            // пустое название нельзя: после ухода из поля возвращается прежнее
            nameBox.Focus();
            UiShot.Pump();
            nameBox.Text = ""; // пустое название
            UiDrive.Click(window, window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell" && t != nameBox)); // уход из поля названия кликом по соседнему полю
            UiShot.Settle(6);
            nameBox.Text.Should().Be(title, $"пустое название не принимается ({tag})");

            SaveShot(window, $"receiving-source-purchases-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
