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
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (просьба владельца для мясных магазинов: «при покупке коровы вес сразу убавлять, для продажи остаётся другой вес»): блок «Выход мяса» в карточке «Склад → Приёмка».
/// Кнопка видна только у весового товара при включённом модуле; блок — «Куплено · Цена за кг · Сумма закупки · Выход % · В продажу» и строка результата; поля не налезают друг на друга и не выходят
/// за карточку на ru / ky / uz при 1024×600 и 1366×768; кнопка и поля ≥ 44 px; набор с клавиатуры пересчитывает вес и цену строки. Окно собирается как в ReceivingCardFlyoutsUiTests.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingCarcassUiTests : IDisposable
{
    private readonly bool _flag = UserPreferences.Instance.CarcassYieldEnabled;
    private readonly string? _account = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "carcass-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ReceivingCarcassUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        UserPreferences.Instance.CarcassYieldEnabled = true;
        UserPreferences.Instance.LastDataAccountKey = null;
        CompanyModules.ResetForTests();
        CarcassYieldMemory.DataDirOverride = _dir;
        CarcassYieldMemory.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.CarcassYieldEnabled = _flag;
        UserPreferences.Instance.LastDataAccountKey = _account;
        CompanyModules.ResetForTests();
        CarcassYieldMemory.DataDirOverride = null;
        CarcassYieldMemory.Reset();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var (w, h) in new[] { (1024, 600), (1366, 768) })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [w, h, lang];
    }

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    private static T ById<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);

    private static T? ByIdOrNull<T>(Visual root, string id) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == id);

    private static (WarehouseWindow window, WarehouseViewModel vm, ReceivingLineVm meat, ReceivingLineVm pieces) Open(int w, int h, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        using var _ = UiShot.Render(window, new UiSize(w, h));
        UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
        UiShot.Settle(10);
        var vm = (WarehouseViewModel)window.DataContext!;
        vm.ReceivingLines.Clear();
        var meat = new ReceivingLineVm { Barcode = "2000000000011", ProductName = "Говядина на кости", Source = ReceivingSource.Warehouse, Quantity = 300, PurchasePrice = 400, SalePrice = 650, Unit = "кг" };
        var pieces = new ReceivingLineVm { Barcode = "4600000000019", ProductName = "Сок яблочный 1 л", Source = ReceivingSource.Warehouse, Quantity = 12, PurchasePrice = 64.5, SalePrice = 89, Unit = "шт" };
        vm.ReceivingLines.Add(meat);
        vm.ReceivingLines.Add(pieces);
        UiShot.Settle(12);
        return (window, vm, meat, pieces);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task CarcassBlock_FitsInTheCard_WithoutOverlap_AndTouchSizes(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var (window, _, meat, _) = Open(w, h, lang);
        try
        {
            var card = RcvCards.Card(window, meat);
            var toggle = ById<Button>(card, "carcass-toggle");
            toggle.IsEffectivelyVisible.Should().BeTrue("весовой товар, модуль включён — кнопка «Выход мяса» видна");
            toggle.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "кнопка под палец");
            ById<Border>(card, "carcass-block").IsVisible.Should().BeFalse("пока не включили — блока нет");

            UiDrive.Click(window, toggle);
            UiShot.Settle(6);
            meat.CarcassOn.Should().BeTrue();
            var block = ById<Border>(card, "carcass-block");
            block.IsEffectivelyVisible.Should().BeTrue();

            UiDrive.Type(window, ById<TextBox>(card, "carcass-yield"), "75");
            UiShot.Settle(6);
            meat.Quantity.Should().Be(225);

            // все поля блока: видны, ≥ 44 px в высоту, целиком внутри карточки и своего блока, друг на друга не налезают
            var cardRect = RectIn(RcvCards.Card(window, meat), window);
            var blockRect = RectIn(block, window);
            blockRect.Left.Should().BeGreaterThanOrEqualTo(cardRect.Left - 0.5);
            blockRect.Right.Should().BeLessThanOrEqualTo(cardRect.Right + 0.5, "блок не выходит за карточку");
            var boxes = new[] { "carcass-gross", "carcass-price", "carcass-total", "carcass-yield", "carcass-net" }.Select(id => ById<TextBox>(card, id)).ToList();
            foreach (var box in boxes)
            {
                box.IsEffectivelyVisible.Should().BeTrue();
                box.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, "поле под палец");
                var r = RectIn(box, window);
                r.Left.Should().BeGreaterThanOrEqualTo(blockRect.Left - 0.5);
                r.Right.Should().BeLessThanOrEqualTo(blockRect.Right + 0.5, "поле не выходит за блок");
            }
            for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                    RectIn(boxes[i], window).Intersect(RectIn(boxes[j], window)).Should().Match<Rect>(x => x.Width <= 0.5 || x.Height <= 0.5, $"поля {i} и {j} не налезают друг на друга");

            // всплывающая подсказка кнопки (мышь осталась над ней после клика) — не часть раскладки формы: убираем курсор и закрываем её
            ToolTip.SetIsOpen(toggle, false);
            Avalonia.Headless.HeadlessWindowExtensions.MouseMove(window, new Point(2, 2));
            UiShot.Settle(4);
            // общая проверка «поля форм не налезают» (подписи, поля, кнопки, выход за окно) по всему окну с раскрытым блоком
            var violations = FormFieldsOverlapUiTests.FindViolations(window, "carcass").Select(v => v.Key + " — " + v.Detail).ToList();
            violations.Should().BeEmpty($"поля карточки с блоком «Выход мяса» не налезают ({w}×{h}, {lang})");

            // строка результата не обрезана
            var prefix = meat.CarcassResultText[..Math.Min(18, meat.CarcassResultText.Length)];
            TextClipChecker.Find(window, "carcass", lang.ToString()).Where(v => v.Key.Contains(prefix)).Should().BeEmpty("итоговая строка выхода видна целиком");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Toggle_IsHidden_ForPieceProducts_AndWhenTheModuleIsOff()
    {
        await TestHost.WarmAsync();
        var (window, _, meat, pieces) = Open(1366, 768, AppLanguage.Russian);
        try
        {
            ByIdOrNull<Button>(RcvCards.Card(window, pieces), "carcass-toggle")?.IsEffectivelyVisible.Should().NotBe(true, "штучный товар — выхода нет");
            ById<Button>(RcvCards.Card(window, meat), "carcass-toggle").IsEffectivelyVisible.Should().BeTrue();

            UserPreferences.Instance.CarcassYieldEnabled = false; // модуль выключили в Маркетплейсе
            TariffGate.RaisePacksChanged(); // как после «Выключить» в Маркетплейсе: кнопка пропадает без пересборки карточки
            UiShot.Settle(6);
            ById<Button>(RcvCards.Card(window, meat), "carcass-toggle").IsEffectivelyVisible.Should().BeFalse("модуль выключен — кнопки нет");

            UserPreferences.Instance.CarcassYieldEnabled = true;
            TariffGate.RaisePacksChanged();
            meat.Unit = "шт";
            UiShot.Settle(4);
            ById<Button>(RcvCards.Card(window, meat), "carcass-toggle").IsEffectivelyVisible.Should().BeFalse("единицу сменили на штуки");
            meat.Unit = "кг";
            UiShot.Settle(4);
            ById<Button>(RcvCards.Card(window, meat), "carcass-toggle").IsEffectivelyVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Typing_Recalculates_WeightPriceResultAndTheSummary()
    {
        await TestHost.WarmAsync();
        var (window, _, meat, _) = Open(1366, 768, AppLanguage.Russian);
        try
        {
            var card = RcvCards.Card(window, meat);
            UiDrive.Click(window, ById<Button>(card, "carcass-toggle"));
            UiShot.Settle(6);

            // с клавиатуры, как кассир: купили 300 кг, выход 75 %
            UiDrive.Type(window, ById<TextBox>(card, "carcass-gross"), "300");
            UiDrive.Type(window, ById<TextBox>(card, "carcass-yield"), "75");
            UiShot.Settle(6);

            meat.Quantity.Should().Be(225);
            meat.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
            ById<TextBlock>(card, "carcass-result").Text.Should().Be("Куплено 300 кг → в продаже 225 кг (отходы 75 кг · 25 %) · себестоимость 533,33 сом/кг");
            ById<TextBlock>(card, "carcass-pay").Text.Should().Be("К оплате поставщику: " + MoneyText.Som(120000m));
            ById<TextBox>(card, "carcass-net").Text.Should().Be("225");
            ById<TextBox>(card, "carcass-total").Text.Should().Be("120000");

            // дробное число набирается по символам и не ломается («400,5» не превращается в 4005)
            UiDrive.Type(window, ById<TextBox>(card, "carcass-price"), "400,5");
            UiShot.Settle(6);
            meat.CarcassPriceText.Should().Be("400.5".Replace('.', ',')); // показ с запятой — поле хранит то, что набрано, до выхода из поля
            meat.CarcassTotalText.Should().Be("120150");

            // вес в продажу вписали руками — выход пересчитался, «Кол-во» строки — тот же вес
            UiDrive.Type(window, ById<TextBox>(card, "carcass-net"), "200");
            UiShot.Settle(6);
            meat.Quantity.Should().Be(200);
            ById<TextBox>(card, "carcass-yield").Text.Should().Be("66,67");

            // неверный выход — красная причина, «Кол-во» 0 (строку не проведут)
            UiDrive.Type(window, ById<TextBox>(card, "carcass-yield"), "150");
            UiShot.Settle(6);
            meat.Quantity.Should().Be(0);
            ById<TextBlock>(card, "carcass-problem").IsEffectivelyVisible.Should().BeTrue();
            ById<TextBlock>(card, "carcass-problem").Text.Should().Contain("100 %");

            // выключили блок — вес и цена остаются, блок прячется
            UiDrive.Type(window, ById<TextBox>(card, "carcass-yield"), "75");
            UiDrive.Click(window, ById<Button>(card, "carcass-toggle"));
            UiShot.Settle(6);
            meat.CarcassOn.Should().BeFalse();
            ById<Border>(card, "carcass-block").IsEffectivelyVisible.Should().BeFalse();
            meat.Quantity.Should().Be(225);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
