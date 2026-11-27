using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
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
            var confirms = 0;
            UiRobot.WhenOpened<PosConfirmDialog>(d => { confirms++; d.Close(); }); // «Сменить кассира на …?» → Отмена
            var vm = (MainWindowViewModel)main.DataContext!;
            var before = vm.Basket.Lines.Count;
            // 2026-11-27 (вечер): путь камеры/телефона — через чек; вопрос о смене кассира, а не товар.
            vm.Basket.EnqueueBarcode(code);
            UiRobot.WaitFor(() => confirms == 1).Should().BeTrue("скан бейджа спрашивает «Сменить кассира?»; открыто: " + UiRobot.OpenNames());
            vm.Basket.Lines.Count.Should().Be(before);

            // Сканер-клавиатура с фокусом в поиске товара: в поле ничего от бейджа не остаётся, Enter не уходит в поиск/оплату.
            var search = main.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ProductSearchBox");
            search.Focus();
            UiShot.Pump();
            main.KeyTextInput("ab");
            Scan(main, search, code);
            UiRobot.WaitFor(() => confirms == 2).Should().BeTrue("и со сканера-клавиатуры тот же вопрос; открыто: " + UiRobot.OpenNames());
            (search.Text ?? "").Should().Be("ab", "набранное до скана осталось, строка бейджа в поле не попала");
            vm.Basket.Lines.Count.Should().Be(before);
        }
        finally
        {
            UiRobot.CloseAll();
            UiRobot.Main = null;
            main.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-27 (вечер, баг с кассы клиента): в «Смене кассира» фокус в Email, скан бейджа — строка не попадает в поле
    /// (набранное раньше остаётся), уходит в смену по бейджу, пароль не проверяется.</summary>
    [AvaloniaFact]
    public void SwitchCashierDialog_BadgeScan_NeverLandsInEmail_AndSwitchesByBadge()
    {
        var (code, _) = IssueBadge();
        string? got = null;
        var passwordCalls = 0;
        var dialog = new SwitchCashierDialog(
            (_, _) => { passwordCalls++; return Task.FromResult<(SwitchCashierOutcome, string?)>((SwitchCashierOutcome.InvalidCredentials, "нет")); },
            (raw, _) => { got = raw; return Task.FromResult<(SwitchCashierOutcome, string?, string?)>((SwitchCashierOutcome.InvalidCredentials, "Один раз войдите паролем", "aigul.test@nbs.kg")); });
        try
        {
            UiShot.Render(dialog).Dispose();
            dialog.Activate();
            var email = dialog.FindControl<TextBox>("EmailBox")!;
            var password = dialog.FindControl<TextBox>("PasswordBox")!;
            email.Focus();
            UiShot.Pump();
            dialog.KeyTextInput("kas");
            Scan(dialog, email, code);
            UiRobot.WaitFor(() => got != null).Should().BeTrue();
            got.Should().Be(code);
            passwordCalls.Should().Be(0, "Enter скана не нажимает «Сменить» по паролю");
            email.Text.Should().Be("aigul.test@nbs.kg", "после отказа подставлен логин владельца бейджа");
            (password.Text ?? "").Should().BeEmpty();
            dialog.FindControl<TextBlock>("ErrorText")!.Text.Should().Be("Один раз войдите паролем");
            foreach (var t in dialog.GetVisualDescendants().OfType<TextBox>())
                (t.Text ?? "").Should().NotContain(code.Split(':')[3], "секрет бейджа не виден ни в одном поле");
        }
        finally { dialog.Close(); UiShot.Pump(); }
    }

    /// <summary>2026-11-27 (вечер): в окне, которое бейджи не обрабатывает (PIN, пароль владельца и т. п.), строка бейджа просто не попадает в поле.</summary>
    [AvaloniaFact]
    public void AnyOtherWindow_BadgeScan_IsSwallowed()
    {
        var (code, _) = IssueBadge();
        var box = new TextBox();
        var window = new Window { Width = 400, Height = 200, Content = box };
        NurMarketKassa.AvaloniaHost.Services.CashierBadgeInputGuard.EnsureRegistered();
        try
        {
            UiShot.Render(window).Dispose();
            window.Activate();
            Scan(window, box, ToRussianLayout(code));
            (box.Text ?? "").Should().BeEmpty();
            // Обычный ввод работает как раньше.
            window.KeyTextInput("1234");
            UiShot.Pump();
            box.Text.Should().Be("1234");
        }
        finally { window.Close(); UiShot.Pump(); }
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
