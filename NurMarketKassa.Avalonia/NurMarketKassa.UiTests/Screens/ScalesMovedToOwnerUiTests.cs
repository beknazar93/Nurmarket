using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «убрать пункт весов из кассы и поставить на владелец»): настоящие окна в Avalonia.Headless.
/// 2026-11-12, решение владельца («оставь их»): в кассе «Настройки → Весы» — полный набор (4 вкладки, как в 1.17.85), и в автономном режиме тоже;
/// в программе владельца есть пункт «Товары → Весы», он открывает страницу
/// (с 2026-11-12 — две вкладки: «Весы с этикетками» и «Штрих-код: вес / сумма»; COM-весы и табло — только в кассе), находится поиском по меню,
/// скрывается в «Разделах меню».
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
        (string)typeof(ScalesSectionWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi")!
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

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task OwnerMenu_ScalesItem_OpensSection_WithLabelScalesOnly(int w, int h)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("scales", "пункт «Весы» в группе «Товары» виден владельцу");
            IsOwnerProp.SetValue(null, true); // каталог «Разделов меню» зависит от режима программы
            SectionVisibility.Catalog.Select(c => c.Key).Should().Contain("scales", "пункт можно скрыть в «Разделах меню»");
            // группа «Товары» в меню — раскрыть и снять снимок меню с новым пунктом «Весы»
            IsOwnerProp.SetValue(null, true);
            ShowMenuGroup(shell, "products");
            SaveShot(shell, $"scales-owner-menu-{w}x{h}", new UiSize(w, h));
            NavButtons(shell)["scales"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(() => shell.OwnedWindows.OfType<ScalesSectionWindow>().Any(), 3000);
            var section = shell.OwnedWindows.OfType<ScalesSectionWindow>().FirstOrDefault();
            section.Should().NotBeNull("пункт открывает страницу весов");
            UiShot.Settle(3, 30);
            var view = section!.GetVisualDescendants().OfType<ScaleSettingsView>().Single();
            TabVisible(view, "TabLabelButton").Should().BeTrue();
            TabVisible(view, "TabBarcodeButton").Should().BeTrue();
            // 2026-11-12, решение владельца: у владельца только две вкладки — COM-весы и табло остались в кассе (ScalesOwnerFullSetUiTests)
            TabVisible(view, "TabWeighingButton").Should().BeFalse("«Весы на кассе» (COM) — только в кассе");
            TabVisible(view, "TabDisplayButton").Should().BeFalse("«Табло цены» — только в кассе");
            view.FindControl<Control>("PluCard")!.IsVisible.Should().BeTrue("первая вкладка — список весов с кнопками «Настроить…» и «Отправить товары»");
            UiShot.Settle(12, 60); // раздел показывает «Открываем раздел…», пока строится
            SaveShot(section, $"scales-owner-section-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_ScalesItem_FoundBySearch_HiddenByMenuSections()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            var buttons = NavButtons(shell);
            buttons.Should().ContainKey("scales");

            // поиск по меню («Найти раздел · Ctrl+K»): слово «весы» оставляет пункт
            var search = shell.FindControl<TextBox>("NavSearchText")!;
            search.Text = "весы";
            UiShot.Settle(3, 30);
            NavButtons(shell)["scales"].IsEffectivelyVisible.Should().BeTrue("поиск по меню находит «Весы»");
            search.Text = "";
            UiShot.Settle(2, 30);

            // «Разделы меню»: скрыт — пункта нет, показан — вернулся
            SectionVisibility.Set("scales", true);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().NotContainKey("scales", "скрытый в «Разделах меню» пункт в меню не показывается");
            SectionVisibility.Set("scales", false);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().ContainKey("scales");
        }
        finally
        {
            SectionVisibility.Set("scales", false);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_ScalesItem_HiddenWithoutSettingsPermission()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().NotContainKey("scales", "без права «Настройки» пункта «Весы» нет");
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
            UiFake.Reset();
            await LoginAsync(); // вернуть владельца для следующих тестов
        }
    }

    public static IEnumerable<object[]> SizesAndLanguages() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    // ------------------------------------------------------------------ «Печать» в программе владельца (2026-11-11)

    [AvaloniaTheory]
    [MemberData(nameof(SizesAndLanguages))]
    public async Task OwnerMenu_PrintItem_OpensSettingsOnPrintTab(int w, int h, AppLanguage language)
    {
        UiShot.Setup(UiTheme.Light, language);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("print", "пункт «Печать» виден владельцу");
            IsOwnerProp.SetValue(null, true); // каталог «Разделов меню» зависит от режима программы
            SectionVisibility.Catalog.Select(c => c.Key).Should().Contain("print", "пункт можно скрыть в «Разделах меню»");
            IsOwnerProp.SetValue(null, true);
            NavButtons(shell)["print"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(4, 40);
            // раздел «Настройки» встроен как отдельное окно, принадлежащее оболочке
            WaitFor(() => shell.OwnedWindows.OfType<PosSettingsWindow>().Any(), 3000);
            var settings = shell.OwnedWindows.OfType<PosSettingsWindow>().FirstOrDefault();
            settings.Should().NotBeNull("«Печать» открывает те же настройки печати");
            settings!.FindControl<Button>("NavPrint")!.IsVisible.Should().BeTrue("вкладка «Печать» в программе владельца показана");
            settings.FindControl<Button>("NavPrint")!.Classes.Should().Contain("nav-active", "открыта сразу страница «Печать»");
            settings.FindControl<Button>("NavScales")!.IsVisible.Should().BeFalse("весы — раздел «Товары → Весы»");
            settings.FindControl<Button>("NavMonitor")!.IsVisible.Should().BeFalse("экран покупателя — на ПК кассы, как и раньше");
            UiShot.Settle(12, 60); // раздел показывает «Открываем раздел…», пока строится
            SaveShot(settings, $"scales-owner-print-{(language == AppLanguage.Russian ? "ru" : "ky")}-{w}x{h}", new UiSize(w, h));
        }
        finally
        {
            IsOwnerProp.SetValue(null, _savedIsOwner);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_PrintItem_FoundBySearch_HiddenByMenuSections()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        await LoginOwnerAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().ContainKey("print");
            var search = shell.FindControl<TextBox>("NavSearchText")!;
            search.Text = "принтер";
            UiShot.Settle(3, 30);
            NavButtons(shell)["print"].IsEffectivelyVisible.Should().BeTrue("поиск по меню находит «Печать»");
            search.Text = "";
            UiShot.Settle(2, 30);
            SectionVisibility.Set("print", true);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().NotContainKey("print", "скрытый в «Разделах меню» пункт в меню не показывается");
            SectionVisibility.Set("print", false);
            UiShot.Settle(5, 40);
            NavButtons(shell).Should().ContainKey("print");
        }
        finally
        {
            SectionVisibility.Set("print", false);
            shell.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerMenu_PrintItem_HiddenWithoutSettingsPermission()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
        var shell = new OwnerShellWindow();
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            NavButtons(shell).Should().NotContainKey("print", "без права «Настройки» пункта «Печать» нет");
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
            UiFake.Reset();
            await LoginAsync();
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

    [Fact]
    public void AdvisorRouter_OpensScalesSection()
    {
        var route = AdvisorNavRouter.Parse("открой весы");
        route.Target.Should().NotBeNull();
        route.Target!.Section.Should().Be("scales");
    }

    [Fact]
    public void OwnerPath_ReplacesSettingsPath_OnlyInOwnerProgram()
    {
        const string ru = "Настройки → Весы → «Весы с этикетками»";
        IsOwnerProp.SetValue(null, false);
        OwnerPath(ru).Should().Be(ru, "в кассе подсказка не меняется");
        IsOwnerProp.SetValue(null, true);
        OwnerPath(ru).Should().Be("Товары → Весы → «Весы с этикетками»");
        OwnerPath("Settings → Scales → “Label scales”").Should().Be("Products → Scales → “Label scales”");
        OwnerPath("Sozlamalar → Tarozilar").Should().Be("Mahsulotlar → Tarozilar");
        OwnerPath("Ayarlar → Tartılar").Should().Be("Ürünler → Tartılar");
        OwnerPath("Жөндөөлөр → Тараза").Should().Be("Товарлар → Тараза");
    }
}
