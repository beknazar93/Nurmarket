using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

/// <summary>2026-11-07 («Склад → Приёмка»: упаковки, решение владельца «Да» + «типа поштучной продажи»): настоящее окно в Avalonia.Headless. Три строки — «упаковками → на склад штуки»,
/// «упаковка с поштучной продажей» (новый товар) и обычная. Проверяется: поля «Принимаем» / «шт/упак.» / «Закупка» помещаются в строку, строки не наезжают друг на друга,
/// штрихкод по-прежнему виден (узкий вид — подстрока под названием), высота строки не меньше содержимого, порядок Enter «Принимаем → шт/упак. → Закупка», итоги — от пересчитанных штук.
/// Размеры — логические: 1024×600, 1093×614 (1366×768 при масштабе 125 %), 1366×768. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\warehouse-packs-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseReceivingPacksUiTests
{
    private const string Barcode = "4600000000011";
    private const string LongName = "Реле зарядки Audi красное длинное название для проверки обрезки";

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1093, 614), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task WarehouseReceiving_PackModes_FitInRows_KeepBarcode_AndEnterOrderWorks(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = CatalogCacheService.Products.ToList();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;

            var tile = new CatalogProductTileVm("w1", LongName, "250 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 3, PurchasePrice = 185 };
            var byPacks = PurchaseReceivingService.FromTile(tile, 1);
            byPacks.EnableReceivingPricing();
            byPacks.SetPackMode(true, 24);
            byPacks.SetPackCount(5);
            byPacks.SetPackPriceText("600");
            byPacks.SalePrice = 30;

            var packSale = new ReceivingLineVm { Barcode = "4602000000017", ProductName = "Сигареты Космос синие", Source = ReceivingSource.Unknown, Quantity = 1 };
            packSale.SetPackSale(true, 10).Should().BeTrue();
            packSale.SetPackCount(3);
            packSale.SetPackPriceText("480");
            packSale.SalePrice = 600;

            var plain = new ReceivingLineVm { Barcode = "", ProductName = "", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(byPacks);
            vm.ReceivingLines.Add(packSale);
            vm.ReceivingLines.Add(plain);
            UiShot.Settle(10);

            var tag = $"{width}x{height} {lang}";
            // 2026-11-21: строки приёмки — карточки (объединение с «Массовым добавлением»); способ приёмки — кнопка «упак. ▾» в карточке, «шт/упак.» — поле карточки, пояснения упаковок — строка под полями.
            List<Border> Cards() => RcvCards.Cards(window).OrderBy(c => c.TranslatePoint(default, window)!.Value.Y).ToList();

            void CheckCards(string stage)
            {
                var cards = Cards();
                cards.Count.Should().BeGreaterThanOrEqualTo(1, $"{stage} {tag}: в списке видна хотя бы одна карточка");
                for (var i = 0; i < cards.Count; i++)
                {
                    var top = cards[i].TranslatePoint(default, window)!.Value.Y;
                    if (i > 0)
                        top.Should().BeGreaterThanOrEqualTo(cards[i - 1].TranslatePoint(default, window)!.Value.Y + cards[i - 1].Bounds.Height - 0.5, $"карточки не наезжают ({stage} {tag})");
                    foreach (var text in cards[i].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && !string.IsNullOrWhiteSpace(t.Text)))
                    {
                        var y = text.TranslatePoint(default, cards[i])!.Value.Y;
                        y.Should().BeGreaterThanOrEqualTo(-0.5, $"«{text.Text}» не выше своей карточки ({stage} {tag})");
                        (y + text.Bounds.Height).Should().BeLessThanOrEqualTo(cards[i].Bounds.Height + 0.5, $"«{text.Text}» не ниже своей карточки ({stage} {tag})");
                    }
                    var cross = cards[i].GetVisualDescendants().OfType<Button>().Last(b => b.IsEffectivelyVisible);
                    cross.TranslatePoint(new Point(cross.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"нет горизонтальной прокрутки ({stage} {tag})");
                }
            }

            CheckCards("показ");

            var foundCard = RcvCards.Card(window, byPacks);
            // название видно; подпись «упак. ▾» — кнопкой, пояснения упаковок — в строке под полями
            foundCard.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.Text == LongName).Should().BeTrue($"название видно ({tag})");
            var shownTexts = foundCard.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            shownTexts.Should().Contain(byPacks.PackLinkShort, $"кнопка способа приёмки ({tag})");
            var notes = string.Join(" | ", shownTexts);
            notes.Should().Contain(byPacks.PackShortNote, $"«× 24 = 120 шт.» ({tag})");
            notes.Should().Contain(byPacks.PackPriceNote, $"цена за упаковку и за штуку ({tag})");
            // список виртуализирован: сначала всё, что нужно от карточки «упаковками» (она на самом верху), потом прокрутка к карточке поштучной продажи
            // --- режим упаковок: два поля (упаковки и «шт/упак.»), оба в карточке; Enter ведёт «Кол-во → шт/упак. → Закупка»
            var qtyBox = RcvCards.Field(window, byPacks, "qty");
            var piecesBox = RcvCards.Field(window, byPacks, "pieces");
            qtyBox.Text.Should().Be("5", "в режиме упаковок «Кол-во» — упаковки");
            piecesBox.Text.Should().Be("24");
            foreach (var box in new[] { qtyBox, piecesBox })
            {
                box.Bounds.Height.Should().BeGreaterThanOrEqualTo(40 - 0.5, $"поле не схлопнуто ({tag})");
                box.Bounds.Width.Should().BeGreaterThanOrEqualTo(50, $"поле читаемо ({tag})");
                box.TranslatePoint(new Point(0, box.Bounds.Height), foundCard)!.Value.Y.Should().BeLessThanOrEqualTo(foundCard.Bounds.Height + 0.5, $"поле в своей карточке ({tag})");
            }
            qtyBox.Focus();
            UiShot.Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            UiShot.Settle(8);
            (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox).Should().BeSameAs(piecesBox, $"Enter из «Кол-во» ведёт в «шт/упак.» ({tag})");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            UiShot.Settle(8);
            var priceBox = RcvCards.Field(window, byPacks, "purchase");
            (TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox).Should().BeSameAs(priceBox, $"Enter из «шт/упак.» ведёт в «Закупка» ({tag})");
            priceBox.Text.Should().Be("600", "в режиме упаковок «Закупка» — цена за упаковку");
            priceBox.Bounds.Height.Should().BeGreaterThanOrEqualTo(40 - 0.5);
            window.FindControl<TextBox>("ReceivingScanBox")!.Focus();
            UiShot.Settle(6);
            CheckCards("после правки");

            var saleCard = RcvCards.Card(window, packSale);
            saleCard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == packSale.PackLinkShort).Should().BeTrue($"кнопка поштучной продажи ({tag})");
            string.Join(" | ", saleCard.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)).Should().Contain(packSale.PackShortNote, $"«по 10 шт» ({tag})");

            // кнопка способа приёмки: подпись целиком (не обрезана), цель касания
            foreach (var card in new[] { foundCard, saleCard })
            {
                var link = card.GetVisualDescendants().OfType<Border>().First(x => Equals(x.Tag, "rcv-packlink")).GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
                var typeface = new Avalonia.Media.Typeface(link.FontFamily, link.FontStyle, link.FontWeight, link.FontStretch);
                var need = new Avalonia.Media.FormattedText(link.Text ?? "", CultureInfo.CurrentUICulture, link.FlowDirection, typeface, link.FontSize, null).Width;
                link.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 1, $"«{link.Text}» не обрезана ({tag})");
                link.GetVisualAncestors().OfType<Border>().First(x => Equals(x.Tag, "rcv-packlink")).Bounds.Height.Should().BeGreaterThanOrEqualTo(40 - 0.5, $"кнопка способа приёмки — цель касания ({tag})");
            }

            // итоги внизу — от пересчитанных штук: 120 (5 упак. × 24) + 3 (упаковки поштучной продажи) + 1
            UiDrive.Find<TextBlock>(window, "SumQty").Text.Should().Be((120 + 3 + 1).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")), $"итог «Штук» ({tag})");
            UiDrive.Find<TextBlock>(window, "SumPos").Text.Should().Be("3");

            // --- поля у строки поштучной продажи: в карточке
            var packSaleCard = RcvCards.Card(window, packSale);
            foreach (var box in packSaleCard.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Tag is string))
            {
                box.Bounds.Height.Should().BeGreaterThanOrEqualTo(30, $"поле не схлопнуто ({tag})");
                box.TranslatePoint(new Point(0, box.Bounds.Height), packSaleCard)!.Value.Y.Should().BeLessThanOrEqualTo(packSaleCard.Bounds.Height + 0.5, $"поле в своей карточке ({tag})");
            }
            CheckCards("после правок");

            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"warehouse-packs-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    /// <summary>«Провести» со строкой «упаковка с поштучной продажей» спрашивает подтверждение («Товар будет вестись упаковками по N шт…»); отказ — ничего не проводится, строка остаётся в таблице.</summary>
    [AvaloniaFact]
    public async Task WarehouseReceiving_PackSale_AsksConfirmationBeforePosting_AndDeclineKeepsTheLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1280, 720));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var line = new ReceivingLineVm { Barcode = "4602000000017", ProductName = "Сигареты Космос синие", Source = ReceivingSource.Unknown, Quantity = 1 };
            line.SetPackSale(true, 10).Should().BeTrue();
            line.SetPackCount(3);
            line.SetPackPriceText("480");
            line.SalePrice = 600;
            vm.ReceivingLines.Add(line);
            UiShot.Settle(6);

            IReadOnlyList<ReceivingLineVm>? asked = null;
            vm.ConfirmPackSale = lines =>
            {
                asked = lines;
                return Task.FromResult(false); // «Вернуться»
            };
            vm.CommitReceivingCommand.Execute(null);
            UiShot.Settle(12);

            asked.Should().NotBeNull("перед проведением спросили подтверждение");
            asked!.Should().ContainSingle().Which.Should().BeSameAs(line);
            vm.ReceivingLines.Should().Contain(line, "отказ — строка осталась, ничего не проведено");
            ReceivingPacks.PackSaleConfirmMessage(asked).Should().Contain("по 10 шт").And.Contain("остаток — в упаковках");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Меню способа приёмки в таблице Склада: у существующего товара «поштучная продажа» недоступна (только новый товар), «упаковками → штуки» включается, у нового — все три пункта доступны.</summary>
    [AvaloniaFact]
    public async Task WarehouseReceiving_PackMenu_BlocksPackSaleForExistingProduct_AndSwitchesModes()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1280, 720));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle();
            var vm = (WarehouseViewModel)window.DataContext!;
            var tile = new CatalogProductTileVm("w2", "Вода", "30 сом", false) { Barcode = Barcode, Unit = "шт", Quantity = 7, PurchasePrice = 25 };
            var existing = PurchaseReceivingService.FromTile(tile, 1);
            var created = new ReceivingLineVm { Barcode = "4602", ProductName = "Новая", Source = ReceivingSource.Unknown, Quantity = 1 };
            vm.ReceivingLines.Add(existing);
            vm.ReceivingLines.Add(created);
            UiShot.Settle(8);

            Border Link(ReceivingLineVm line) => RcvCards.Card(window, line).GetVisualDescendants().OfType<Border>().First(t => Equals(t.Tag, "rcv-packlink") && t.IsEffectivelyVisible); // 2026-11-21: кнопка способа приёмки в карточке
            MenuItem Item(string start) => window.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.IsEffectivelyVisible && m.Header is string h && h.TrimStart().TrimStart((char)0x2713).TrimStart().StartsWith(start, StringComparison.Ordinal)) ?? throw new InvalidOperationException("нет пункта «" + start + "»; пункты: " + string.Join(" | ", window.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header + "/" + m.IsEffectivelyVisible)) + "; hosts=" + window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.OverlayPopupHost>().Count() + " popups=" + window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Count() + "; ссылки: " + string.Join(" | ", window.GetVisualDescendants().OfType<TextBlock>().Where(t => (t.Text ?? "").EndsWith("▾")).Select(t => t.Text + "@" + t.Bounds + "/" + t.IsEffectivelyVisible)));

            // существующий товар с остатком: поштучная продажа недоступна, а «упаковками → штуки» включается

            UiDrive.Click(window, Link(existing));
            UiShot.Settle(6);
            // 2026-11-10 (владелец: «как в вебе — упаковка, продажа поштучно»): у товара склада тоже доступно — остаток пересчитается через подтверждение «Было → станет»
            Item("Упаковка с поштучной продажей").IsEnabled.Should().BeTrue("товар склада получает упаковку тем же PATCH, что окно товара");
            Item("Упаковками → на склад штуки").IsEnabled.Should().BeTrue();
            UiDrive.Click(window, Item("Упаковками → на склад штуки"));
            UiShot.Settle(8);
            existing.PackMode.Should().BeTrue();
            existing.PackSale.Should().BeFalse();
            existing.PackNeedsSize.Should().BeTrue("размер упаковки ещё не введён — провести нельзя");
            PurchaseReceivingService.Validate(new[] { existing }).Should().Contain(p => p.Contains("упаков"), "окно Склада использует Validate: блокировка без «шт в упак.»");

            // новый товар: поштучная продажа доступна
            UiDrive.Click(window, Link(created));
            UiShot.Settle(6);
            Item("Упаковка с поштучной продажей").IsEnabled.Should().BeTrue();
            UiDrive.Click(window, Item("Упаковка с поштучной продажей"));
            UiShot.Settle(8);
            created.PackSale.Should().BeTrue();
            created.PackLinkText.Should().Contain("поштучно");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
