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

            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var tag = $"{width}x{height} {lang}";
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.Bounds.Height > 0 && r.DataContext is ReceivingLineVm)
                .OrderBy(r => r.TranslatePoint(default, grid)!.Value.Y).ToList();
            rows.Count.Should().BeGreaterThanOrEqualTo(3, tag); // в окне 1024×600 четвёртая строка уходит за нижний край таблицы (виртуализация)
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                    rows[i].TranslatePoint(default, grid)!.Value.Y.Should().BeGreaterThanOrEqualTo(rows[i - 1].TranslatePoint(default, grid)!.Value.Y + rows[i - 1].Bounds.Height - 0.5, $"строки не наезжают ({tag})");
                var cross = rows[i].GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible);
                cross.TranslatePoint(new Point(cross.Bounds.Width, 0), grid)!.Value.X.Should().BeLessThanOrEqualTo(grid.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({tag})");
            }

            foreach (var line in rows.Select(r => (ReceivingLineVm)r.DataContext!).ToList())
            {
                var row = rows.First(r => ReferenceEquals(r.DataContext, line));
                var badge = row.GetVisualDescendants().OfType<ReceivingSourceBadge>().Single(b => b.IsEffectivelyVisible);
                var info = $"{line.Source} ({tag})";
                badge.Source.Should().Be(line.Source, info);
                badge.Bounds.Width.Should().BeGreaterThanOrEqualTo(20, $"значок виден ({info})");

                // подсказка при наведении и имя для скринридера: «Название: пояснение» на языке интерфейса
                var tip = ToolTip.GetTip(badge) as string;
                tip.Should().NotBeNullOrWhiteSpace($"у значка есть подсказка ({info})");
                tip.Should().Contain(ReceivingLineVm.SourceTitle(line.Source)).And.Contain(ReceivingLineVm.SourceHintFor(line.Source), info);
                AutomationProperties.GetName(badge).Should().Be(tip, $"имя значка для скринридера ({info})");

                // значок — первый в строке: левее названия и правее колонки «№»
                var badgeX = badge.TranslatePoint(default, row)!.Value.X;
                var numberText = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == line.LineNo.ToString());
                badgeX.Should().BeGreaterThan(numberText.TranslatePoint(default, row)!.Value.X, $"значок правее «№» ({info})");
                var nameText = row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == line.ProductName && line.ProductName.Length > 0);
                if (nameText is not null)
                {
                    badgeX.Should().BeLessThan(nameText.TranslatePoint(default, row)!.Value.X, $"значок левее названия — первый в строке ({info})");
                    nameText.Bounds.Width.Should().BeGreaterThanOrEqualTo(60, $"название читается ({info}); таблица {grid.Bounds.Width}, колонка названия {grid.Columns[1].ActualWidth}, текст «{nameText.Text}»");
                }
                // значок и строка источника: подпись под названием совпадает с подсказкой
                row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && (t.Text ?? "").StartsWith(line.SourceText, StringComparison.Ordinal)).Should().BeTrue($"подпись источника «{line.SourceText}» ({info})");
                // штрихкод по-прежнему виден (подстрока под названием в узком виде, колонка — в широком)
                row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && (t.Text ?? "").StartsWith(line.Barcode, StringComparison.Ordinal)).Should().BeTrue($"штрихкод виден ({info})");

                // «Найти в интернете» — только у «Неизвестно»
                var net = row.GetVisualDescendants().OfType<Button>().Where(b => b.Tag as string == "rcv-netlink").ToList();
                var netVisible = net.Any(b => b.IsEffectivelyVisible);
                netVisible.Should().Be(line.Source == ReceivingSource.Unknown, $"кнопка поиска в интернете только у «Неизвестно» ({info})");
                if (netVisible)
                {
                    var b = net.Single(x => x.IsEffectivelyVisible);
                    (ToolTip.GetTip(b) as string).Should().Be(WarehouseWindow.InternetSearchHintText, info);
                    AutomationProperties.GetName(b).Should().Be(WarehouseWindow.InternetSearchText, info);
                    b.Bounds.Width.Should().BeGreaterThanOrEqualTo(28, $"кнопка нажимаема ({info})");
                }
            }
            calls.Should().Be(0, "поиск в интернете сам не запускается — ни при показе строки «Неизвестно», ни при отрисовке");

            // нажатие кнопки: один вызов, отказ — строка остаётся «Неизвестно»
            var unknownRow = rows.First(r => ReferenceEquals(r.DataContext, unknown));
            var netButton = unknownRow.GetVisualDescendants().OfType<Button>().Single(b => b.Tag as string == "rcv-netlink" && b.IsEffectivelyVisible);
            UiDrive.Click(window, netButton);
            UiShot.Settle(8);
            calls.Should().Be(1, "одно нажатие — один запрос");
            unknown.Source.Should().Be(ReceivingSource.Unknown, "ничего не нашлось — «Неизвестно»");

            // название товара склада правится (переименование карточки) и отмечается
            grid.SelectedItem = warehouse;
            grid.CurrentColumn = grid.Columns[1];
            grid.BeginEdit().Should().BeTrue($"название товара склада можно править ({tag})");
            UiShot.Settle(6);
            var warehouseRow = rows.First(r => ReferenceEquals(r.DataContext, warehouse));
            var editor = warehouseRow.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && !t.Classes.Contains("rcv-baredit") && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            editor.Bounds.Height.Should().BeGreaterThanOrEqualTo(20, $"поле названия не схлопнуто ({tag})");
            warehouseRow.GetVisualDescendants().OfType<ReceivingSourceBadge>().Any(b => b.IsEffectivelyVisible).Should().BeTrue($"значок остаётся и при правке названия ({tag})");
            UiDrive.Type(window, editor, "Молоко 1 л 3,2 %");
            grid.CommitEdit();
            UiShot.Settle(6);
            warehouse.ProductName.Should().Be("Молоко 1 л 3,2 %");
            warehouse.NameRenamed.Should().BeTrue();
            warehouse.NameOrUnitChanged.Should().BeTrue("название уйдёт в карточку при проведении");
            var renamedText = warehouseRow.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "Молоко 1 л 3,2 %");
            renamedText.Classes.Contains("rcv-renamed").Should().BeTrue($"изменённое название подсвечено ({tag})");
            (ToolTip.GetTip(renamedText) as string).Should().Contain("Молоко 1 л").And.Be(warehouse.NameWasHint, $"в подсказке «было: …» ({tag})");

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
