using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20, решение владельца: «убери из продуктовой сферы аренду! аренда по умолчанию только в строймаге, у остальных — через Маркетплейс».
/// В окне оплаты и в меню ☰ кассы пункт «Прокат» / «Оформить как прокат» виден только в строймаркете (или при включённом модуле); на ru/ky/uz не обрезан.</summary>
[Collection("UiScenarios")]
public sealed class RentalVisibilityUiTests
{
    private static readonly string[] RentalWords = ["прокат", "prokat", "rental", "kiralama"];

    private static void SetSphere(bool hardware, bool moduleOn = false)
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = hardware ? "hardware" : null;
        UserPreferences.Instance.RentalEnabled = moduleOn;
    }

    private static List<string> RentalTexts(Window w) =>
        w.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && RentalWords.Any(r => t.Text!.Contains(r, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Text!).ToList();

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task CheckoutDialog_rental_link_only_in_hardware(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = (UserPreferences.Instance.MarketSphere, UserPreferences.Instance.MarketSphereDetail, UserPreferences.Instance.RentalEnabled);
        try
        {
            foreach (var (hardware, moduleOn) in new[] { (false, false), (true, false), (false, true) })
            {
                SetSphere(hardware, moduleOn);
                var shown = hardware || moduleOn;
                var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 500, TotalDue = 500, LineCount = 1 }, "", "", null);
                vm.IsPrintReceiptEnabled = false;
                var owner = new Window { Width = 1280, Height = 800 };
                owner.Show();
                UiShot.Settle(2);
                var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
                dialog.DockTo(owner);
                _ = dialog.ShowDialog<bool?>(owner);
                UiShot.Settle(6);
                try
                {
                    var tag = $"{lang}, строймаркет={hardware}, модуль={moduleOn}";
                    var link = dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("rental-link"));
                    (link?.IsEffectivelyVisible ?? false).Should().Be(shown, $"«Оформить как прокат» в окне оплаты ({tag})");
                    if (!shown)
                        RentalTexts(dialog).Should().BeEmpty($"в окне оплаты нет ни слова про прокат ({tag})");
                    else
                    {
                        var text = UiDrive.TextOf(link!)!;
                        TextClipChecker.Find(dialog, "checkout-rental", lang.ToString()).Where(v => v.Detail.Contains(text)).Should().BeEmpty($"подпись «{text}» не обрезана ({tag})");
                    }
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                    UiShot.Pump();
                }
            }
        }
        finally
        {
            (UserPreferences.Instance.MarketSphere, UserPreferences.Instance.MarketSphereDetail, UserPreferences.Instance.RentalEnabled) = saved;
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task SideMenu_rental_item_only_in_hardware_and_not_clipped(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var saved = (UserPreferences.Instance.MarketSphere, UserPreferences.Instance.MarketSphereDetail, UserPreferences.Instance.RentalEnabled, UserPreferences.Instance.MainLayoutMode);
        UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using var guard = UiRobot.Guard();
        try
        {
            using (UiShot.Render(main, UiSize.Large)) { }
            main.Activate();
            UiShot.Settle(10, 40);
            var vm = (MainWindowViewModel)main.DataContext!;
            vm.SideMenu.NavigateRentalAction ??= () => { }; // в программе действие задаёт InitializeApplicationAsync — здесь его нет
            foreach (var hardware in new[] { false, true })
            {
                SetSphere(hardware);
                vm.SideMenu.RefreshEntitlements();
                vm.Toolbar.ToggleSideMenuCommand.Execute(null);
                UiShot.Settle(10, 40);
                try
                {
                    var tag = $"{lang}, строймаркет={hardware}";
                    // Пункты проката в обоих видах меню — кнопки с командой «Прокат»; свой признак видимости должен совпасть с сферой.
                    var buttons = main.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Command, vm.SideMenu.NavigateRentalCommand)).ToList();
                    buttons.Should().NotBeEmpty($"пункты проката есть в разметке меню ({tag})");
                    buttons.Should().OnlyContain(b => b.IsVisible == hardware, $"«Прокат» в меню ☰ виден только в строймаркете ({tag}): " + string.Join(", ", buttons.Select(b => $"{b.Name}:{b.IsVisible}/{b.IsEffectivelyVisible}")));
                    if (!hardware)
                        RentalTexts(main).Should().BeEmpty($"в меню ☰ нет слова «Прокат» ({tag})");
                    else
                        foreach (var b in buttons.Where(x => x.IsEffectivelyVisible))
                        {
                            var text = UiDrive.TextOf(b)!;
                            TextClipChecker.Find(main, "side-menu-rental", lang.ToString()).Where(v => v.Detail.Contains(text)).Should().BeEmpty($"подпись «{text}» не обрезана ({tag})");
                        }
                }
                finally
                {
                    vm.CloseSideMenu();
                    UiShot.Settle(4, 30);
                }
            }
        }
        finally
        {
            (UserPreferences.Instance.MarketSphere, UserPreferences.Instance.MarketSphereDetail, UserPreferences.Instance.RentalEnabled, UserPreferences.Instance.MainLayoutMode) = saved;
            // 2026-11-20: тест оставлял UiRobot.Main и общее окно кассы живыми — следующие сценарии (плитка «Хлеб», кнопки штрихкодов, память окон)
            // видели чужое состояние, а UiRobot.Opened копил окна. Убираем так же, как KassaLookGalleryTests.
            NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Normalize(saved.Item4));
            UiRobot.CloseAll();
            UiRobot.Main = null;
            main.Hide();
            UiShot.Pump();
        }
    }
}
