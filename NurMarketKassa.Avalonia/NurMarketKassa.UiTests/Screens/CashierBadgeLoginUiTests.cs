using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27: вход кассира по QR-бейджу в настоящем окне входа (Avalonia.Headless): сканер печатает строку бейджа в поле
/// логина и жмёт Enter — строка не остаётся ни в логине, ни в пароле, на экране понятное сообщение (неизвестный, отозванный бейдж;
/// «первый вход на этой кассе — пароль один раз» с подставленным логином). Обычный ввод почты работает как раньше. На открытой
/// кассе скан бейджа не попадает в чек.</summary>
[Collection("UiScenarios")]
public sealed class CashierBadgeLoginUiTests : IDisposable
{
    private readonly string? _savedRegistry = UserPreferences.Instance.CashierBadgesJson;

    public CashierBadgeLoginUiTests()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CashierBadgeThrottle.Instance.RegisterSuccess(); // паузы от прошлых тестов не мешают
    }

    public void Dispose()
    {
        UserPreferences.Instance.CashierBadgesJson = _savedRegistry;
        CashierBadgeThrottle.Instance.RegisterSuccess();
    }

    private static (string Code, CashierBadgeRecord Record) IssueBadge(bool revoke = false)
    {
        var now = DateTimeOffset.UtcNow;
        var (list, code, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "badge-ui-42", "Айгуль Тест", "aigul.test@nbs.kg", now);
        if (revoke)
            CashierBadgeRegistry.Revoke(list, "badge-ui-42", now);
        UserPreferences.Instance.CashierBadgesJson = CashierBadgeRegistry.Serialize(list);
        return (code, record);
    }

    private static LoginWindow OpenLogin()
    {
        var window = (LoginWindow?)TestHost.Services.GetService(typeof(LoginWindow)) ?? new LoginWindow();
        UiShot.Render(window).Dispose();
        window.Activate();
        UiShot.Settle(4, 20);
        return window;
    }

    /// <summary>Как USB-сканер: символы в поле с фокусом, затем Enter.</summary>
    private static void Scan(Window w, TextBox focus, string text)
    {
        focus.Focus();
        UiShot.Pump();
        w.KeyTextInput(text);
        UiShot.Pump();
        w.KeyPress(Key.Return, RawInputModifiers.None, PhysicalKey.Enter, null);
        UiShot.Settle(3, 20);
    }

    private static LoginViewModel Vm(Window w) => (LoginViewModel)w.DataContext!;

    [AvaloniaFact]
    public void UnknownBadge_IsNotLeftInFields_AndExplains()
    {
        IssueBadge();
        var window = OpenLogin();
        try
        {
            var email = window.FindControl<TextBox>("EmailBox")!;
            var password = window.FindControl<TextBox>("PasswordBox")!;
            Scan(window, email, CashierBadgeCode.Generate().Code);
            (email.Text ?? "").Should().BeEmpty("строка бейджа не остаётся в поле логина");
            (password.Text ?? "").Should().BeEmpty();
            Vm(window).ErrorMessage.Should().Be(CashierBadgeRegistry.Message(CashierBadgeStatus.Unknown));
            Vm(window).IsLoading.Should().BeFalse();
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void RevokedBadge_IsRefused()
    {
        var (code, _) = IssueBadge(revoke: true);
        var window = OpenLogin();
        try
        {
            Scan(window, window.FindControl<TextBox>("EmailBox")!, code);
            Vm(window).ErrorMessage.Should().Be(CashierBadgeRegistry.Message(CashierBadgeStatus.Revoked));
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void ValidBadge_FirstTimeOnThisTill_PrefillsLogin_AndAsksPasswordOnce()
    {
        var (code, record) = IssueBadge();
        CashierBadgeVault.Instance.Remove(record.EmployeeId);
        var window = OpenLogin();
        try
        {
            var email = window.FindControl<TextBox>("EmailBox")!;
            var password = window.FindControl<TextBox>("PasswordBox")!;
            // Сканер в русской раскладке: строка приходит буквами ЙЦУКЕН — касса всё равно узнаёт бейдж.
            Scan(window, password, ToRussianLayout(code));
            email.Text.Should().Be("aigul.test@nbs.kg");
            (password.Text ?? "").Should().BeEmpty("строка бейджа не остаётся в поле пароля");
            Vm(window).ErrorMessage.Should().Be(CashierBadgeLogin.NeedPasswordText(record));
            password.IsFocused.Should().BeTrue("остаётся ввести пароль");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void OrdinaryEmail_Enter_StillMovesToPassword()
    {
        IssueBadge();
        var window = OpenLogin();
        try
        {
            var email = window.FindControl<TextBox>("EmailBox")!;
            Scan(window, email, "kassir@nbs.kg");
            email.Text.Should().Be("kassir@nbs.kg");
            window.FindControl<TextBox>("PasswordBox")!.IsFocused.Should().BeTrue();
            // Сообщение кнопки входа по умолчанию («Введите логин и пароль.») — прежнее поведение; про бейдж ничего нет.
            Vm(window).ErrorMessage.Should().NotContain("бейдж");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void OpenTill_BadgeScan_DoesNotGoToReceipt()
    {
        var (code, _) = IssueBadge();
        var main = (MainWindow)ScreenCatalog.Get("main-kassa").Create();
        UiRobot.Reset(main);
        try
        {
            UiShot.Render(main).Dispose();
            var vm = (MainWindowViewModel)main.DataContext!;
            var before = vm.Basket.Lines.Count;
            vm.Basket.EnqueueBarcode(code);
            UiShot.Settle(4, 30);
            vm.Basket.Lines.Count.Should().Be(before);
            vm.Basket.CartMessage.Should().Contain("QR-бейдж кассира");
        }
        finally
        {
            UiRobot.CloseAll();
            UiRobot.Main = null;
            main.Close();
            UiShot.Pump();
        }
    }

    private static string ToRussianLayout(string text)
    {
        const string latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
        const string cyr = "йцукенгшщзхъфывапролджэячсмитьбюё";
        var sb = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c == ':') { sb.Append('Ж'); continue; }
            var i = latin.IndexOf(char.ToLowerInvariant(c));
            sb.Append(i < 0 ? c : char.IsUpper(c) ? char.ToUpperInvariant(cyr[i]) : cyr[i]);
        }
        return sb.ToString();
    }
}
