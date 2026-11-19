using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using NurMarketKassa.Core.Contracts;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Interfaces;
using NurMarketKassa.ViewModels;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19: снимки кассы для базы знаний 2.0 (главный экран, меню ☰, «Настройки»). См. KbShots — включается NURMARKET_KB_SHOTS=1.</summary>
[Collection("UiScenarios")]
public sealed class KbShotsKassaTests
{
    private static readonly UiSize Hd = new(1280, 720);

    [AvaloniaFact]
    public async Task Kassa_Main_And_Menu()
    {
        if (!KbShots.Want("kassa-main") && !KbShots.Want("kassa-menu"))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var savedLayout = UserPreferences.Instance.MainLayoutMode;
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.MainLayoutMode = KassaLayouts.Standard;
        NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Standard);
        var main = TestHost.Services.GetRequiredService<MainWindow>();
        UiRobot.Reset(main);
        using var guard = UiRobot.Guard(600000);
        try
        {
            using (UiShot.Render(main, Hd)) { }
            main.Activate();
            UiShot.Settle(10, 40);
            // Чек из трёх демо-товаров: 3 хлеба, молоко, 2 кока-колы.
            KbShots.Scan(main, "3*4700000000001");
            KbShots.Scan(main, "4700000000003");
            KbShots.Scan(main, "2*4700000000004");
            foreach (var lang in KbShots.Langs)
            {
                UiShot.Setup(UiTheme.Light, lang);
                if (KbShots.Want("kassa-main"))
                    KbShots.Shot(main, "kassa-main", lang, Hd);
            }
            if (KbShots.Want("kassa-menu"))
            {
                KbShots.Vm(main).Toolbar.ToggleSideMenuCommand.Execute(null);
                UiShot.Settle(10, 40);
                foreach (var lang in KbShots.Langs)
                {
                    UiShot.Setup(UiTheme.Light, lang);
                    UiShot.Settle(6, 40);
                    KbShots.Shot(main, "kassa-menu", lang, Hd);
                }
                KbShots.Vm(main).CloseSideMenu();
                UiShot.Settle(4, 30);
            }
        }
        finally
        {
            try { KbShots.Vm(main).Basket.ClearTrainingReceipt(); } catch { /* чек теста не оставляем */ }
            UserPreferences.Instance.MainLayoutMode = savedLayout;
            NurMarketKassa.AvaloniaHost.App.ApplyMainLayoutMode(KassaLayouts.Normalize(savedLayout));
            UiRobot.CloseAll();
            UiRobot.Main = null;
            main.Hide(); // закрытие окна кассы подвешивает хост Headless — прячем
            UiShot.Pump();
            restore();
        }
    }

    private static object Member(object o, string name)
    {
        var f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        return o.GetType().GetProperty(name, f)?.GetValue(o) ?? o.GetType().GetField(name, f)?.GetValue(o)
            ?? throw new InvalidOperationException("Нет члена " + name + " у " + o.GetType().Name);
    }

    [AvaloniaFact]
    public async Task Kassa_Settings_Tabs()
    {
        var tabs = new (string Name, string Key)[]
        {
            ("kassa-settings-screen", "screen"), ("kassa-settings-print", "print"), ("kassa-settings-operations", "operations"),
            ("kassa-settings-updates", "updates"), ("kassa-settings-rollback", "rollback"), ("kassa-settings-scales", "scales"),
        };
        if (!tabs.Any(t => KbShots.Want(t.Name)))
            return;
        var restore = await KbShots.BeginDemoShopAsync();
        var savedPack = UserPreferences.Instance.LanguagePackUnlocked;
        UserPreferences.Instance.LanguagePackUnlocked = true; // на снимке видны все пять языков (в настоящей программе — после покупки «Языкового пакета»)
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        try
        {
            var size = new UiSize(1280, 909);
            using (UiShot.Render(window, size)) { }
            foreach (var (name, key) in tabs.Where(t => KbShots.Want(t.Name)))
            {
                if (key == "scales")
                    typeof(PosSettingsWindow).GetMethod("NavigateTo", flags)!.Invoke(window, [0]);
                else
                    window.SelectNavTab(key == "rollback" ? "updates" : key);
                UiShot.Settle(6, 40);
                if (key == "screen")
                {
                    // Язык и масштаб — в свёрнутых группах: открываем их, первую («Экран и работа») сворачиваем, чтобы всё нужное уместилось.
                    UiDrive.Find<ToggleButton>(window, "DisplayGroupToggle").IsChecked = false;
                    UiDrive.Find<ToggleButton>(window, "CashierGroupToggle").IsChecked = true;
                    UiDrive.Find<ToggleButton>(window, "LayoutGroupToggle").IsChecked = true;
                }
                foreach (var lang in KbShots.Langs)
                {
                    UiShot.Setup(UiTheme.Light, lang);
                    if (key == "rollback")
                    {
                        // Список версий в настоящей программе приходит с GitHub; здесь — вымышленные номера (строки и подписи собирает сама программа, на языке снимка).
                        var view = typeof(PosSettingsWindow).GetField("_updatesView", flags)!.GetValue(window)!;
                        var panel = (StackPanel)Member(view, "VersionsListPanel");
                        var build = typeof(PosSettingsWindow).GetMethod("BuildVersionRow", flags)!;
                        var service = TestHost.Services.GetRequiredService<IAppUpdateService>();
                        panel.Children.Clear();
                        foreach (var v in new[] { new AppReleaseVersion("1.17.87", null, true), new AppReleaseVersion("1.17.86", null, false), new AppReleaseVersion("1.17.85", null, false), new AppReleaseVersion("1.17.81", null, false), new AppReleaseVersion("1.17.77", null, false) })
                            panel.Children.Add((Control)build.Invoke(window, [v, service])!);
                        panel.IsVisible = true;
                        ((Button)Member(view, "ShowVersionsButton")).Content = Tr.T("Скрыть версии", "Версияларды жашыруу", "Hide versions", "Sürümleri gizle", "Versiyalarni yashirish");
                        UiShot.Settle(3, 40);
                        panel.BringIntoView();
                    }
                    UiShot.Settle(6, 40);
                    KbShots.Shot(window, name, lang, size);
                }
            }
        }
        finally
        {
            UserPreferences.Instance.LanguagePackUnlocked = savedPack;
            window.Close();
            UiShot.Pump();
            restore();
        }
    }
}
