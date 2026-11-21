using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 («Базар», модули category_tiles / haggle / currencies / invoice_cargo): окна на 1024×600 и 1366×768, ru / ky / uz. Плитки категорий — большие цели касания и без наложений; окно торга — кнопки «До 10 / 50 / 100» и поле
/// «Договорились за»; окно оплаты — блок «Принято в валюте» не раздувает окно, «Оплатить» на месте; окно курсов; карточка «Карго» накладной. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\bazaar-*.png.</summary>
[Collection("UiScenarios")]
public sealed class BazaarUiTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _json = UserPreferences.Instance.SphereSettingsJson;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "bazaar-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public BazaarUiTests()
    {
        Directory.CreateDirectory(_dir);
        BazaarCurrencies.FileOverride = Path.Combine(_dir, "rates.json");
        BazaarCurrencies.ApiOverride = () => null;
        BazaarCurrencies.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.SphereSettingsJson = _json;
        BazaarCurrencies.ApiOverride = null;
        BazaarCurrencies.FileOverride = null;
        BazaarCurrencies.ResetForTests();
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Список товаров виден: плитками (CardsScroll) или таблицей (на узком окне каталог сам переходит на таблицу).</summary>
    private static bool ListVisible(Window w) =>
        w.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "CardsScroll").IsEffectivelyVisible
        || w.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "TableGrid").IsEffectivelyVisible;

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private static void Bazaar(BazaarMode mode)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = "bazaar";
        p.SphereSettingsJson = null;
        SphereSettings.SetBazaarMode(mode);
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

    private static readonly string[] Categories =
    [
        "Обувь", "Одежда", "Сумки и рюкзаки", "Носки и бельё", "Головные уборы", "Аксессуары", "Детская одежда", "Спортивная форма", "Куртки зимние", "Джинсы", "Платки и шарфы", "Перчатки",
    ];

    public static IEnumerable<object[]> TileCases() =>
        new (double W, double H)[] { (520, 470), (760, 620) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    // ───────────── плитки категорий ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(TileCases))]
    public async Task CategoryTiles_AreBigTouchTargets_DoNotOverlap_AndOneTapShowsTheProducts(double width, double height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        Bazaar(BazaarMode.Retail);
        var picked = new List<CatalogProductTileVm>();
        var built = CatalogListFixture.Create(width, height, 0, picked.Add, items =>
        {
            for (var i = 0; i < items.Count; i++)
                items[i].Category = Categories[i % 3];
            for (var i = 4; i < 40; i++)
            {
                var t = new CatalogProductTileVm("c" + i, $"Товар {i} из категории «{Categories[i % Categories.Length]}»", "150.00 сом", false) { Category = Categories[i % Categories.Length], Unit = "шт", Quantity = 5 };
                ProductUnitNormalizer.ApplyToTile(t);
                items.Add(t);
            }
        });
        try
        {
            built.Window.Show();
            UiShot.Settle(8);
            var vm = built.Vm;
            vm.ShowCategoryTiles.Should().BeTrue();
            var scroll = built.Window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BazaarTilesScroll");
            scroll.IsEffectivelyVisible.Should().BeTrue("в корне каталога видны плитки категорий");
            ListVisible(built.Window).Should().BeFalse("список товаров на плитках скрыт");

            var tiles = scroll.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is BazaarCategoryTileVm).ToList();
            tiles.Should().HaveCount(Categories.Length, "по плитке на категорию");
            Rect R(Control c) => new(c.TranslatePoint(default, scroll)!.Value, c.Bounds.Size);
            var tag = $"({width}x{height} {Code(lang)})";
            foreach (var t in tiles)
            {
                R(t).Width.Should().BeGreaterThanOrEqualTo(100, $"цель касания плитки категории {tag}");
                R(t).Height.Should().BeGreaterThanOrEqualTo(100, $"цель касания плитки категории {tag}");
                R(t).Right.Should().BeLessThanOrEqualTo(scroll.Bounds.Width + 1, $"плитка не выходит за правый край {tag}");
                R(t).Left.Should().BeGreaterThanOrEqualTo(-1);
                // название и число товаров внутри плитки, не обрезаны по ширине (многоточие допустимо, наложения нет)
                var texts = t.GetVisualDescendants().OfType<TextBlock>().Where(x => x.IsEffectivelyVisible && !string.IsNullOrEmpty(x.Text)).ToList();
                texts.Should().NotBeEmpty();
                foreach (var tb in texts)
                {
                    var rect = new Rect(tb.TranslatePoint(default, t)!.Value, tb.Bounds.Size);
                    rect.Right.Should().BeLessThanOrEqualTo(t.Bounds.Width + 1, $"подпись плитки не вылезает: «{tb.Text}» {tag}");
                    rect.Bottom.Should().BeLessThanOrEqualTo(t.Bounds.Height + 1);
                }
            }
            for (var i = 0; i < tiles.Count; i++)
                for (var j = i + 1; j < tiles.Count; j++)
                {
                    var inter = R(tiles[i]).Intersect(R(tiles[j]));
                    (inter.Width * inter.Height).Should().BeLessThan(4, $"плитки не налезают друг на друга {tag}");
                }
            SavePng(built.Window, $"bazaar-tiles-{Code(lang)}-{width:0}x{height:0}");

            // Один тап по плитке — товары категории; тап по товару — товар добавляется; «← Все категории» возвращает плитки.
            var first = tiles[0];
            UiDrive.Click(built.Window, first);
            UiShot.Settle(6);
            vm.InBazaarCategory.Should().BeTrue();
            vm.ShowCategoryTiles.Should().BeFalse();
            scroll.IsEffectivelyVisible.Should().BeFalse();
            ListVisible(built.Window).Should().BeTrue("в категории виден список товаров");
            vm.Products.Should().NotBeEmpty().And.OnlyContain(p => p.Category == ((BazaarCategoryTileVm)first.DataContext!).Key);
            var back = UiDrive.ButtonByText(built.Window, "←");
            back.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "кнопка «Все категории» — цель касания");
            SavePng(built.Window, $"bazaar-category-{Code(lang)}-{width:0}x{height:0}");

            var product = built.Window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is CatalogProductTileVm p && p == vm.Products[0] && b.GetType() == typeof(Button)
                && Avalonia.Automation.AutomationProperties.GetName(b) == vm.Products[0].Title);
            UiDrive.Click(built.Window, product);
            UiShot.Settle(4);
            picked.Should().ContainSingle("тап по товару категории добавляет его");

            UiDrive.Click(built.Window, back);
            UiShot.Settle(6);
            vm.ShowCategoryTiles.Should().BeTrue();
            scroll.IsEffectivelyVisible.Should().BeTrue();
        }
        finally
        {
            built.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task CategoryTiles_AreNotShownWithoutTheModule_ListIsAsBefore()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        p.SphereSettingsJson = null;
        var built = CatalogListFixture.Create(560, 470, 0, null, items =>
        {
            for (var i = 0; i < items.Count; i++)
                items[i].Category = Categories[i];
        });
        try
        {
            built.Window.Show();
            UiShot.Settle(6);
            built.Vm.ShowCategoryTiles.Should().BeFalse("вне «Базара» плиток категорий нет");
            built.Window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BazaarTilesScroll").IsEffectivelyVisible.Should().BeFalse();
            ListVisible(built.Window).Should().BeTrue();
        }
        finally
        {
            built.Dispose();
        }
    }

    // ───────────── торг ─────────────

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Uzbek)]
    public async Task HaggleDialog_StepButtons_FillTheField_AndApplyReturnsTheAgreedSum(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var dlg = new BazaarHaggleDialog("Куртка зимняя", 1247m);
        try
        {
            using (UiShot.Render(dlg)) { }
            UiShot.Settle(3);
            var steps = dlg.GetVisualDescendants().OfType<Button>().Where(b => b.Height == 52).ToList();
            steps.Should().HaveCount(3, "«До 10 / До 50 / До 100» — три большие кнопки");
            (steps.Max(b => b.Bounds.Width) - steps.Min(b => b.Bounds.Width)).Should().BeLessThanOrEqualTo(1.5, "кнопки одной ширины");
            steps.Should().OnlyContain(b => b.Bounds.Width >= 100 && b.IsEnabled);
            Rect R(Control c) => new(c.TranslatePoint(default, dlg)!.Value, c.Bounds.Size);
            for (var i = 1; i < steps.Count; i++)
                R(steps[i - 1]).Right.Should().BeLessThanOrEqualTo(R(steps[i]).Left + 0.5, "кнопки не налезают друг на друга");
            var field = dlg.GetVisualDescendants().OfType<TextBox>().First();
            R(field).Top.Should().BeGreaterThanOrEqualTo(R(steps[0]).Bottom, "поле «Договорились за» ниже кнопок");
            R(field).Height.Should().Be(52);
            dlg.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.95 + 36, $"окно умещается {width}x{height}");
            SavePng(dlg, $"bazaar-haggle-{Code(lang)}-{width}x{height}");

            UiDrive.Click(dlg, steps[1]); // «До 50»: 1247 → 1200
            UiShot.Settle(3);
            field.Text.Should().Be("1200");
            UiDrive.AllText(dlg).Should().Contain("47");
            var apply = dlg.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("modal-primary"));
            apply.IsEnabled.Should().BeTrue();
            SavePng(dlg, $"bazaar-haggle-picked-{Code(lang)}-{width}x{height}");

            field.Text = "1150"; // «Договорились за 1150»: скидка 97
            UiShot.Settle(3);
            UiDrive.AllText(dlg).Should().Contain("97");
            field.Text = "1500"; // выше суммы — торг только вниз
            UiShot.Settle(3);
            apply.IsEnabled.Should().BeFalse("итог выше суммы нельзя");
            field.Text = "1100";
            UiShot.Settle(3);
            apply.IsEnabled.Should().BeTrue();
            UiDrive.Click(dlg, apply);
            dlg.Agreed.Should().Be(1100m);
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task HaggleDialog_AmountBelowTheStep_DisablesThatStepButton()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dlg = new BazaarHaggleDialog("Носки", 40m);
        try
        {
            using (UiShot.Render(dlg)) { }
            var steps = dlg.GetVisualDescendants().OfType<Button>().Where(b => b.Height == 52).ToList();
            // 40 до 10 = 40 (без скидки) — кнопка выключена; до 50 и 100 — в ноль, тоже выключены
            steps.Should().OnlyContain(b => !b.IsEnabled, "сумма уже круглая или меньше шага — округлять нечего");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    // ───────────── курсы валют ─────────────

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Uzbek)]
    public async Task RatesDialog_ThreeRateFields_LabelsAboveFields_NoOverlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        Bazaar(BazaarMode.Retail);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), """{"base":"KGS","rates":{"USD":"87.5","RUB":"0.95"},"version":"3"}""");
        BazaarCurrencies.ResetForTests();
        var dlg = new BazaarRatesDialog();
        try
        {
            using (UiShot.Render(dlg)) { }
            UiShot.Settle(3);
            var fields = dlg.GetVisualDescendants().OfType<TextBox>().Where(t => t.Height == 52).ToList();
            fields.Should().HaveCount(3, "USD, RUB, CNY");
            Rect R(Control c) => new(c.TranslatePoint(default, dlg)!.Value, c.Bounds.Size);
            for (var i = 1; i < fields.Count; i++)
                R(fields[i - 1]).Bottom.Should().BeLessThan(R(fields[i]).Top, "поля одно под другим, не налезают");
            fields.Should().OnlyContain(f => f.Bounds.Width >= 200);
            dlg.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.95 + 36);
            fields[0].Text.Should().Be("87,5", "курс USD из файла курсов");
            fields[2].Text.Should().BeNullOrEmpty("курса CNY нет");
            SavePng(dlg, $"bazaar-rates-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    // ───────────── окно оплаты: «Принято в валюте» ─────────────

    private sealed class Display : ICustomerDisplayService
    {
        public void Show() { }
        public void Hide() { }
        public void UpdateCart(CustomerDisplayCartSnapshot snapshot) { }
        public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) { }
        public void SetSelectedBankQrPath(string? qrPath) { }
        public bool ShowsPaymentQrToCustomer => false;
    }

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Uzbek)]
    public async Task PaymentWindow_ForeignCurrency_20Dollars_Equals1750_ChangeInSom_AndPayButtonStaysVisible(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        Bazaar(BazaarMode.Retail);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), """{"base":"KGS","rates":{"USD":"87.5","RUB":"0.95","CNY":"12.1"},"version":"3"}""");
        BazaarCurrencies.ResetForTests();
        var savedCard = UserPreferences.Instance.CheckoutCardPay;
        Window? owner = null;
        CheckoutDialog? dialog = null;
        try
        {
            var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 1700, TotalDue = 1700, LineCount = 1 }, "", "", null, new Display());
            vm.IsPrintReceiptEnabled = false;
            vm.HasFxChips.Should().BeTrue("курсы USD / RUB / CNY есть, модуль включён");
            owner = new Window { Width = width, Height = height };
            owner.Show();
            UiShot.Settle(2);
            dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
            dialog.DockTo(owner);
            _ = dialog.ShowDialog<bool?>(owner);
            UiShot.Settle(4);
            var heightBefore = dialog.Bounds.Height;

            var chips = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is FxChipVm).ToList();
            chips.Should().HaveCount(3);
            chips.Should().OnlyContain(c => c.Bounds.Height >= 44, "кнопки валют — цель касания");
            Rect R(Control c) => new(c.TranslatePoint(default, dialog)!.Value, c.Bounds.Size);
            for (var i = 1; i < chips.Count; i++)
                R(chips[i - 1]).Right.Should().BeLessThanOrEqualTo(R(chips[i]).Left + 0.5, "кнопки валют не налезают друг на друга");

            UiDrive.Click(dialog, chips[0]); // USD
            UiShot.Settle(6);
            vm.FxCurrency.Should().Be("USD");
            var amount = UiDrive.Find<TextBox>(dialog, "FxAmountBox");
            var som = UiDrive.Find<TextBox>(dialog, "FxSomBox");
            amount.IsEffectivelyVisible.Should().BeTrue();
            R(amount).Right.Should().BeLessThanOrEqualTo(R(som).Left + 0.5, "поле валюты и поле сомов рядом, не налезают");
            amount.Bounds.Width.Should().BeGreaterThan(48);
            UiDrive.Type(dialog, amount, "20");
            UiShot.Settle(4);

            vm.CashReceived.Should().Be("1750,00", "20 × 87,5 = 1 750 сом — «Получено» в сомах");
            vm.FxPaymentForApi.Should().NotBeNull();
            vm.FxPaymentForApi!.AmountSom.Should().Be(1750m);
            vm.ChangeAmountDisplay.Should().Contain("50", "сдача — в сомах: 1 750 − 1 700 = 50");
            vm.CanPay.Should().BeTrue();
            UiDrive.AllText(dialog).Should().Contain("87,5");

            var pay = UiDrive.Find<Button>(dialog, "PayButton");
            pay.IsEffectivelyVisible.Should().BeTrue();
            var payRect = new Rect(pay.Bounds.Size).TransformToAABB(pay.TransformToVisual(dialog)!.Value);
            payRect.Bottom.Should().BeLessThanOrEqualTo(dialog.Bounds.Height + 1, "«Оплатить» внутри окна");
            var scale = dialog.RenderScaling > 0 ? dialog.RenderScaling : 1.0;
            (dialog.Position.Y + dialog.Bounds.Height * scale).Should().BeLessThanOrEqualTo(owner.Position.Y + owner.ClientSize.Height * (owner.RenderScaling > 0 ? owner.RenderScaling : 1.0) + 2, "окно оплаты внутри главного окна");
            SavePng(dialog, $"bazaar-checkout-fx-{Code(lang)}-{width}x{height}");

            UiDrive.Click(dialog, chips[0]); // повторное нажатие — снова сомы
            UiShot.Settle(4);
            vm.FxCurrency.Should().BeNull();
            vm.FxPaymentForApi.Should().BeNull();
            vm.CashReceived.Should().Be("1700,00");
            heightBefore.Should().BeGreaterThan(0);
        }
        finally
        {
            UserPreferences.Instance.CheckoutCardPay = savedCard;
            dialog?.Close();
            owner?.Close();
            UiShot.Pump();
        }
    }

    // ───────────── накладная: карточка «Карго» ─────────────

    [AvaloniaTheory]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1366, 768, AppLanguage.Kyrgyz)]
    [InlineData(1024, 600, AppLanguage.Uzbek)]
    public async Task WaybillEditor_CargoCard_ShowsLinesWithPlacesAndWeight_OnlyForWaybillWithTheModule(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        Bazaar(BazaarMode.Wholesale);
        var doc = new ClientDoc { Number = "НК-0101", Client = "Азамат (Ош)", Phone = "+996700111222" };
        doc.Kind = ClientDocKind.Waybill;
        doc.Date = new DateTime(2026, 11, 21);
        doc.Lines.Add(new ClientDocLine { Name = "Кроссовки Nike, ряд 36–41", Unit = "ряд", Qty = 10, Price = 6000 });
        doc.Lines.Add(new ClientDocLine { Name = "Сумки спортивные", Unit = "шт", Qty = 4, Price = 2500 });
        var window = new ClientDocEditorWindow(doc, isNew: true);
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            UiShot.Settle(6);
            var all = UiDrive.AllText(window);
            all.Should().Contain(Tr.T("Карго", "Карго", "Cargo", "Kargo", "Kargo"), "карточка «Карго» накладной видна при модуле опта");
            BazaarCargo.Read(doc).Enabled.Should().BeTrue("новая накладная опту — «Карго» включено сразу");
            var cargoFields = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Height == 44).ToList();
            cargoFields.Should().HaveCountGreaterOrEqualTo(8, "две строки × (мест, вес) + мешков, перевозчик, куда, трек");
            Rect R(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
            for (var i = 0; i < cargoFields.Count; i++)
                for (var j = i + 1; j < cargoFields.Count; j++)
                {
                    var inter = R(cargoFields[i]).Intersect(R(cargoFields[j]));
                    (inter.Width * inter.Height).Should().BeLessThan(4, "поля карточки «Карго» не налезают друг на друга");
                }

            var placesBox = cargoFields.First(t => t.MaxLength == 5 && t.Text is null or "");
            UiDrive.Type(window, placesBox, "5");
            UiShot.Settle(3);
            BazaarCargo.Places(doc.Lines[0]).Should().Be(5, "места строки записываются в документ");
            SavePng(window, $"bazaar-cargo-editor-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
