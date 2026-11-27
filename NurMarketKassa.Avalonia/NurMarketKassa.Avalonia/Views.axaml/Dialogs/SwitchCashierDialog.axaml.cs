using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Кто-то из подтверждённого результата: успех, неверные данные (остаёмся в диалоге,
/// кассир может повторить), или отмена уже В ПРОЦЕССЕ переключения (например, кассир передумал
/// на шаге закрытия смены — это не ошибка ввода, диалог просто тихо закрывается).</summary>
public enum SwitchCashierOutcome
{
    Success,
    InvalidCredentials,
    Cancelled,
}

/// <summary>«Сменить кассира» (AI-фичи/бэклог 2026-09-03, этап 10) — вся реальная логика
/// (проверка логина/пароля на сервере, безопасный откат сессии при ошибке, закрытие смены)
/// живёт у вызывающего кода через <see cref="_validate"/> — этот класс только UI-обёртка:
/// не выходит из текущего аккаунта, пока не введён верный логин/пароль (см. явное требование
/// пользователя), и не закрывается сам при неверных данных — только показывает ошибку и даёт
/// попробовать снова.</summary>
public partial class SwitchCashierDialog : Window, ICashierBadgeScanTarget
{
    private readonly Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>>? _validate;
    /// <summary>2026-11-27 (вечер, баг с кассы клиента): скан QR-бейджа в этом окне = смена кассира на владельца бейджа (та же
    /// проверка и закрытие смены, что у «Сменить»). Возвращает итог, текст ошибки и логин для подстановки (нужен пароль один раз).</summary>
    private readonly Func<string, Window, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage, string? PrefillLogin)>>? _validateBadge;
    private bool _isBusy;

    public SwitchCashierDialog()
    {
        InitializeComponent();
    }

    public SwitchCashierDialog(
        Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>> validate,
        Func<string, Window, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage, string? PrefillLogin)>>? validateBadge = null) : this()
    {
        _validate = validate;
        _validateBadge = validateBadge;
        CashierBadgeInputGuard.EnsureRegistered(); // 2026-11-27 (вечер): строка бейджа не попадает в Email/Пароль
        // 2026-11-27 (вечер, владелец): окно сразу готово к скану — бейдж ловится в любом поле (CashierBadgeInputGuard), смена без кнопок.
        if (validateBadge is not null && EmailBox.Parent is Panel form)
        {
            var hint = new TextBlock
            {
                Text = Tr.T("Поднесите QR-бейдж к сканеру — касса сменит кассира сразу. Или введите логин и пароль.",
                    "QR-бейджди сканерге жакындатыңыз — касса кассирди дароо алмаштырат. Же логин менен сырсөздү киргизиңиз.",
                    "Hold the QR badge to the scanner — the till switches the cashier at once. Or enter the login and password.",
                    "QR rozeti tarayıcıya tutun — kasa kasiyeri hemen değiştirir. Veya kullanıcı adı ve şifreyi girin.",
                    "QR-beydjni skanerga yaqinlashtiring — kassa kassirni darhol almashtiradi. Yoki login va parolni kiriting."),
                FontSize = 13,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 6),
            };
            hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushText"));
            form.Children.Insert(0, hint);
        }
        Opened += (_, _) => EmailBox.Focus();
    }

    public static async Task ShowAsync(
        Window owner,
        Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>> validate,
        Func<string, Window, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage, string? PrefillLogin)>>? validateBadge = null)
    {
        var dialog = new SwitchCashierDialog(validate, validateBadge);
        await PosDialogHost.ShowAsync(dialog, owner);
    }

    /// <summary>2026-11-27 (вечер): скан бейджа (от CashierBadgeInputGuard по Enter). Код не показывается — только итог.</summary>
    public async void OnCashierBadgeScanned(string raw)
    {
        if (_isBusy)
            return;
        if (_validateBadge is null)
        {
            ShowError(Tr.T("Здесь вход по QR-бейджу недоступен — введите логин и пароль.", "Бул жерде QR-бейдж менен кирүү жок — логин менен сырсөздү киргизиңиз.",
                "QR badge sign-in is not available here — enter the login and password.", "Burada QR rozetle giriş yok — kullanıcı adı ve şifreyi girin.", "Bu yerda QR-beydj bilan kirish yo'q — login va parolni kiriting."));
            return;
        }
        SetBusy(true);
        try
        {
            var (outcome, errorMessage, prefill) = await _validateBadge(raw, this);
            switch (outcome)
            {
                case SwitchCashierOutcome.Success:
                    Close(true);
                    break;
                case SwitchCashierOutcome.Cancelled:
                    Close(false);
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(prefill))
                    {
                        EmailBox.Text = prefill;
                        PasswordBox.Text = "";
                        PasswordBox.Focus();
                    }
                    ShowError(errorMessage ?? Tr.T("Не удалось выполнить вход.", "Кирүү мүмкүн болгон жок.", "Could not sign in.", "Giriş yapılamadı.", "Tizimga kirib bo'lmadi."));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NurMarketKassa.Services.PosLogger.Log($"Смена кассира по бейджу: сбой ({ex.GetType().Name}).", "WARNING");
            ShowError(Tr.T("Не удалось сменить кассира по бейджу. Введите логин и пароль.", "Бейдж менен кассирди алмаштыруу мүмкүн болгон жок. Логин менен сырсөздү киргизиңиз.",
                "Could not switch cashier with the badge. Enter the login and password.", "Rozetle kasiyer değiştirilemedi. Kullanıcı adı ve şifreyi girin.", "Beydj bilan kassirni almashtirib bo'lmadi. Login va parolni kiriting."));
        }
        finally
        {
            if (IsVisible)
                SetBusy(false);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isBusy)
            Close(false);
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e) => await TryConfirmAsync();

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // 2026-10-22 (модалки, пакет 2): Enter обработан здесь — кнопка по умолчанию не запускает проверку второй раз.
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            await TryConfirmAsync();
        }
    }

    private async Task TryConfirmAsync()
    {
        if (_isBusy || _validate is null)
            return;

        var email = (EmailBox.Text ?? "").Trim();
        var password = PasswordBox.Text ?? "";
        if (email.Length == 0 || password.Length == 0)
        {
            ShowError(Tr.T("Введите логин и пароль.", "Логин менен сырсөздү киргизиңиз.", "Enter the login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting."));
            return;
        }

        SetBusy(true);
        try
        {
            var (outcome, errorMessage) = await _validate(email, password);
            switch (outcome)
            {
                case SwitchCashierOutcome.Success:
                    Close(true);
                    break;
                case SwitchCashierOutcome.Cancelled:
                    // Кассир отменил где-то дальше в процессе (например, на закрытии смены) —
                    // не ошибка ввода, просто тихо закрываем без сообщения.
                    Close(false);
                    break;
                default:
                    ShowError(errorMessage ?? Tr.T("Не удалось выполнить вход.", "Кирүү мүмкүн болгон жок.", "Could not sign in.", "Giriş yapılamadı.", "Tizimga kirib bo'lmadi."));
                    PasswordBox.Text = "";
                    PasswordBox.Focus();
                    break;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ConfirmButton.Content = busy
            ? Tr.T("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…")
            : Tr.T("Сменить", "Алмаштыруу", "Switch", "Değiştir", "Almashtirish");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
