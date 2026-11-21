using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «убрать пункт весов из кассы и поставить на владелец»): настоящие окна в Avalonia.Headless.
/// 2026-11-12, решение владельца («оставь их»): в кассе «Настройки → Весы» — полный набор (4 вкладки, как в 1.17.85), и в автономном режиме тоже;
/// в программе владельца была страница «Товары → Весы» (две вкладки: «Весы с этикетками» и «Штрих-код: вес / сумма»; COM-весы и табло — только в кассе).
/// 2026-11-20 (владелец: «в настройках есть печать! убери из бургер! и весы в настройки засунь»): пунктов «Печать» и «Весы» в меню ☰ нет ни в кассе, ни в программе
/// владельца; «Настройки → Весы» и «Настройки → Печать» есть в обеих программах, открываются; «Весы» скрываются тумблером «Разделы меню».
/// Снимки: %TEMP%\nurmarket-tests\ui-actual\scales-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ScalesMovedToOwnerUiTests : IDisposable
{
    private readonly bool _savedOwnerSections = AppMode.OwnerSectionsInKassa;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;

    public ScalesMovedToOwnerUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        AppMode.OwnerSectionsInKassa = _savedOwnerSections;
        IsOwnerProp.SetValue(null, _savedIsOwner);
        SectionVisibility.Set("scales", false);
    }

    private static async Task LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
    }

    /// <summary>Владелец из поддельного NurCRM: настоящий сервер отдаёт владельцу флаг can_view_settings (раздел «Настройки» открывается по нему).</summary>
    private static async Task LoginOwnerAsync()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
    }

    private static void ShowMenuGroup(OwnerShellWindow shell, string group)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(OwnerShellWindow).GetField("_navGroup", flags)!.SetValue(shell, group);
        typeof(OwnerShellWindow).GetField("_navPeek", flags)!.SetValue(shell, true);
        typeof(OwnerShellWindow).GetMethod("ApplySidebarLayout", flags)!.Invoke(shell, null);
        typeof(OwnerShellWindow).GetMethod("ApplyNavFilter", flags)!.Invoke(shell, null);
        UiShot.Settle(4, 40);
    }

    private static void SaveShot(Window window, string name, UiSize size)
    {
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static Dictionary<string, Button> NavButtons(OwnerShellWindow shell) =>
        (Dictionary<string, Button>)typeof(OwnerShellWindow).GetField("_navButtons", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    private static bool TabVisible(ScaleSettingsView view, string name) => view.FindControl<RadioButton>(name)!.IsVisible;

    private static string OwnerPath(string text) =>
        (string)typeof(PosSettingsWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi")!
            .GetMethod("OwnerPath", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [text])!;

    public static IEnumerable<object[]> Sizes() => new[] { new object[] { 1024, 600 }, new object[] { 1366, 768 } };

    /// <summary>2026-11-12, решение владельца («где настройки весов в кассе и в программе владельца?! оставь их»; отменяет 11.11 «убрать из кассы»):
    /// в кассе «Настройки → Весы» снова полные — «Весы на кассе» (COM), «Весы с этикетками» (список, «Настроить…», отправка товаров),
    /// «Штрих-код: вес / сумма» и «Табло цены», как в 1.17.85; подсказки «весы переехали» нет.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task Kassa_Settings_HasFullScalesSection(int w, int h, AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        AppMode.OwnerSectionsInKassa = false;
        TariffGate.IsStartTariff.Should().BeFalse("проверка — для тарифа выше «Старт»");
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            window.FindControl<Button>("NavScales")!.IsVisible.Should().BeTrue("в кассе вкладка «Весы» в Настройках есть");
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            TabVisible(view, "TabWeighingButton").Should().BeTrue("подключение весов по COM");
            TabVisible(view, "TabLabelButton").Should().BeTrue("«Весы с этикетками» (отправка товаров, подключение сетевых весов) — и в кассе");
            TabVisible(view, "TabBarcodeButton").Should().BeTrue("«Штрих-код: вес / сумма» — и в кассе");
            TabVisible(view, "TabDisplayButton").Should().BeTrue("табло цены");
            view.FindControl<Control>("MovedHintCard").Should().BeNull("подсказки «весы переехали в программу владельца» больше нет");
            view.ShowSection(0);
            UiShot.Settle(3, 30);
            SaveShot(window, $"scales-kassa-settings-{(language == AppLanguage.Russian ? "ru" : "ky")}-{w}x{h}", new UiSize(w, h)); // вкладка «Весы на кассе» (COM)
            view.ShowSection(1);
            UiShot.Settle(3, 30);
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue("вкладка «Весы с этикетками» открывается");
            view.FindControl<Control>("ScaleListHost")!.IsEffectivelyVisible.Should().BeTrue("список весов с кнопками «Настроить…» и «Отправить товары» в кассе есть");
            SaveShot(window, $"scales-kassa-labels-{(language == AppLanguage.Russian ? "ru" : "ky")}-{w}x{h}", new UiSize(w, h));
            view.ShowSection(2);
            UiShot.Settle(3, 30);
            view.FindControl<Control>("BarcodeSection")!.IsVisible.Should().BeTrue("вкладка «Штрих-код: вес / сумма» открывается");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-12: и в автономном режиме (и на «Старте») все вкладки на месте, как в кассе.</summary>
    [AvaloniaFact]
    public async Task Kassa_Autonomous_KeepsFullScalesSection()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync();
        AppMode.OwnerSectionsInKassa = true; // автономный режим (как и «Старт», где ShowOwnerSectionsInKassa тоже true)
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            var view = window.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            TabVisible(view, "TabLabelButton").Should().BeTrue();
            TabVisible(view, "TabWeighingButton").Should().BeTrue();
            TabVisible(view, "TabDisplayButton").Should().BeTrue();
            TabVisible(view, "TabBarcodeButton").Should().BeTrue();
            view.ShowSection(1);
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    /// <summary>2026-11-20: снимки «Настройки → Весы / Печать» и меню ☰ — на трёх языках (ru, ky, uz).</summary>
    public static IEnumerable<object[]> SizesAndThreeLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Lang(AppLanguage language) => language switch { AppLanguage.Russian => "ru", AppLanguage.Kyrgyz => "ky", _ => "uz" };

    // ------------------------------------------------------------------ 2026-11-20 (владелец: «в настройках есть печать! убери из бургер! и весы в настройки засунь»)
    // В меню ☰ нет пунктов «Печать» и «Весы» — ни в кассе, ни в программе владельца; в «Настройках» есть страницы «Печать» и «Весы», обе открываются.

    private static readonly string[] BannedMenuTitles = { "Печать", "Весы", "Басып чыгаруу", "Тараза", "Chop etish", "Tarozi", "Print", "Scales", "Yazdır", "Tartı", "Tartılar", "Tarozilar" };

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndThreeLanguages))]
    public async Task OwnerMenu_HasNoPrintNorScales_SettingsHaveBothPagesAndOpenThem(int w, int h, AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            IsOwnerProp.SetValue(null, true); // каталог «Разделов меню» и окно «Настройки» зависят от режима программы
            var buttons = NavButtons(shell);
            buttons.Should().NotContainKey("print", "в меню ☰ нет пункта «Печать» — это вкладка Настроек");
            buttons.Should().NotContainKey("scales", "в меню ☰ нет пункта «Весы» — это вкладка Настроек");
            buttons.Should().ContainKey("settings");
            buttons.Values.Select(b => (UiDrive.TextOf(b) ?? "").Trim()).Should().NotContain(t => BannedMenuTitles.Contains(t), "ни одного пункта меню с названием «Печать» или «Весы»");
            SectionVisibility.Catalog.Select(c => c.Key).Should().NotContain("print", "«Печать» нельзя скрыть в «Разделах меню» — это не пункт меню");
            foreach (var group in new[] { "products", "manage" })
            {
                ShowMenuGroup(shell, group);
                SaveShot(shell, $"scales-owner-menu-{group}-{Lang(language)}-{w}x{h}", new UiSize(w, h)); // меню без «Весов» и «Печати» — смотреть глазами
            }

            buttons["settings"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4, 40);
            WaitFor(() => shell.OwnedWindows.OfType<PosSettingsWindow>().Any(), 3000);
            var settings = shell.OwnedWindows.OfType<PosSettingsWindow>().FirstOrDefault();
            settings.Should().NotBeNull("«Настройки» открываются из меню");
            UiShot.Settle(12, 60); // раздел показывает «Открываем раздел…», пока строится
            settings!.FindControl<Button>("NavScales")!.IsVisible.Should().BeTrue("в Настройках программы владельца есть страница «Весы»");
            settings.FindControl<Button>("NavPrint")!.IsVisible.Should().BeTrue("в Настройках программы владельца есть страница «Печать»");
            settings.FindControl<Button>("NavMonitor")!.IsVisible.Should().BeFalse("экран покупателя — на ПК кассы, как и раньше");

            settings.SelectNavTab("scales").Should().BeTrue("страница «Весы» открывается");
            UiShot.Settle(6, 40);
            settings.FindControl<Button>("NavScales")!.Classes.Should().Contain("nav-active");
            var view = settings.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            TabVisible(view, "TabLabelButton").Should().BeTrue("«Весы с этикетками» — список весов, подключение, «Настроить…», «Отправить товары»");
            TabVisible(view, "TabBarcodeButton").Should().BeTrue("«Штрих-код: вес / сумма»");
            TabVisible(view, "TabWeighingButton").Should().BeFalse("«Весы на кассе» (COM) — только в кассе");
            TabVisible(view, "TabDisplayButton").Should().BeFalse("«Табло цены» — только в кассе");
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue("первая вкладка — список весов с кнопками «Настроить…» и «Отправить товары»");
            // Страница строится по Loaded; в Headless первый кадр заново созданных надписей пуст (то же у прежних снимков «Весов» программы владельца): текст в дереве есть,
            // а на картинке — нет, пока страницу не покажут заново. Для настоящего снимка уходим на «Печать» и возвращаемся (в живой программе кадры рисуются непрерывно).
            view.ShowSection(1);
            UiShot.Settle(6, 40);
            using (UiShot.Render(settings, new UiSize(w, h))) { }
            settings.SelectNavTab("print");
            UiShot.Settle(6, 40);
            settings.SelectNavTab("scales");
            UiShot.Settle(6, 40);
            SaveShot(settings, $"scales-owner-settings-scales-{Lang(language)}-{w}x{h}", new UiSize(w, h));
            view.ShowSection(2);
            UiShot.Settle(4, 40);
            view.FindControl<Control>("BarcodeSection")!.IsVisible.Should().BeTrue("вкладка «Штрих-код: вес / сумма» открывается");

            settings.SelectNavTab("print").Should().BeTrue("страница «Печать» открывается");
            UiShot.Settle(6, 40);
            settings.FindControl<Button>("NavPrint")!.Classes.Should().Contain("nav-active", "открыта страница «Печать»");
            settings.FindControl<Button>("NavScales")!.Classes.Should().NotContain("nav-active");
            SaveShot(settings, $"scales-owner-settings-print-{Lang(language)}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Находится поиском по меню: слова «весы» и «принтер» ведут на пункт «Настройки» (самих пунктов «Весы» и «Печать» нет).</summary>
    [AvaloniaFact]
    public async Task OwnerMenu_Search_FindsSettings_ByScalesAndPrinterWords()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            var search = shell.FindControl<TextBox>("NavSearchText")!;
            foreach (var word in new[] { "весы", "принтер", "печать" })
            {
                search.Text = word;
                UiShot.Settle(3, 30);
                NavButtons(shell)["settings"].IsEffectivelyVisible.Should().BeTrue($"поиск «{word}» находит «Настройки»");
            }
            search.Text = "";
            UiShot.Settle(2, 30);
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Страница «Весы» видна, как был виден пункт: скрыта тумблером «Разделы меню» (ключ scales); «Печать» скрыть нельзя.</summary>
    [AvaloniaFact]
    public async Task OwnerSettings_ScalesPage_HiddenByMenuSections_PrintStays()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        IsOwnerProp.SetValue(null, true);
        SectionVisibility.Set("scales", true);
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            window.FindControl<Button>("NavScales")!.IsVisible.Should().BeFalse("скрытая в «Разделах меню» страница «Весы» не показывается");
            window.FindControl<Button>("NavPrint")!.IsVisible.Should().BeTrue("«Печать» скрыть нельзя");
            window.SelectNavTab("scales").Should().BeFalse("скрытая страница по ключу не открывается");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            SectionVisibility.Set("scales", false);
        }
    }

    /// <summary>Касса: в меню ☰ нет «Печати» и «Весов» (были только в Настройках), в Настройках — обе страницы.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Kassa_SideMenu_HasNoPrintNorScales_SettingsHaveBoth(AppLanguage language)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, language);
        await LoginAsync();
        AppMode.OwnerSectionsInKassa = false;
        var savedLayout = UserPreferences.Instance.MainLayoutMode;
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
            vm.Toolbar.ToggleSideMenuCommand.Execute(null);
            UiShot.Settle(10, 40);
            // пункты меню ☰ — кнопки с именами Mi… (SideMenuView.axaml); команда «Настройки» — для проверки, что меню собрано
            var items = main.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Command, vm.SideMenu.NavigateSettingsCommand)
                || (b.Name ?? "").StartsWith("Mi", StringComparison.Ordinal)).ToList();
            items.Should().NotBeEmpty("пункты меню ☰ найдены");
            items.Select(b => (UiDrive.TextOf(b) ?? "").Trim()).Should().NotContain(t => BannedMenuTitles.Contains(t), $"в меню ☰ кассы нет пунктов «Печать» и «Весы» ({language})");
            items.Select(b => b.Name).Should().NotContain(new[] { "MiPrint", "MiScales" });
            vm.CloseSideMenu();
            UiShot.Settle(4, 30);
        }
        finally
        {
            UserPreferences.Instance.MainLayoutMode = savedLayout;
            UiShot.Pump();
        }

        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            window.FindControl<Button>("NavScales")!.IsVisible.Should().BeTrue("в кассе «Настройки → Весы» есть");
            window.FindControl<Button>("NavPrint")!.IsVisible.Should().BeTrue("в кассе «Настройки → Печать» есть");
            window.SelectNavTab("scales").Should().BeTrue();
            window.SelectNavTab("print").Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Kassa_Settings_KeepsPrintTab()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginAsync();
        var window = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        try
        {
            window.Show();
            UiShot.Settle(3, 30);
            window.FindControl<Button>("NavPrint")!.IsVisible.Should().BeTrue("в кассе «Печать» остаётся как было");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [Fact]
    public void AdvisorRouter_OpensPrintTab()
    {
        var route = AdvisorNavRouter.Parse("открой настройки печати");
        route.Target.Should().NotBeNull();
        route.Target!.Section.Should().Be("settings");
        route.Target.Tab.Should().Be("print");
    }

    /// <summary>2026-11-20: «открой весы» ведёт на вкладку «Весы» Настроек, а не на раздел меню.</summary>
    [Fact]
    public void AdvisorRouter_OpensScalesTabOfSettings()
    {
        var route = AdvisorNavRouter.Parse("открой весы");
        route.Target.Should().NotBeNull();
        route.Target!.Section.Should().Be("settings");
        route.Target.Tab.Should().Be("scales");
    }

    /// <summary>2026-11-20: путь «Настройки → Весы» верен и в кассе, и в программе владельца — подсказки не переписываются.</summary>
    [Fact]
    public void OwnerPath_KeepsSettingsPath_InBothPrograms()
    {
        const string ru = "Настройки → Весы → «Весы с этикетками»";
        foreach (var owner in new[] { false, true })
        {
            IsOwnerProp.SetValue(null, owner);
            OwnerPath(ru).Should().Be(ru);
            OwnerPath("Settings → Scales → “Label scales”").Should().Be("Settings → Scales → “Label scales”");
            OwnerPath("Жөндөөлөр → Тараза").Should().Be("Жөндөөлөр → Тараза");
        }
    }
}
