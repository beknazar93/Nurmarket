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
public partial class SwitchCashierDialog : Window
{
    private readonly Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>>? _validate;
    private bool _isBusy;

    public SwitchCashierDialog()
    {
        InitializeComponent();
    }

    public SwitchCashierDialog(
        Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>> validate) : this()
    {
        _validate = validate;
        Opened += (_, _) => EmailBox.Focus();
    }

    public static async Task ShowAsync(
        Window owner,
        Func<string, string, Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)>> validate)
    {
        var dialog = new SwitchCashierDialog(validate);
        await PosDialogHost.ShowAsync(dialog, owner);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isBusy)
            Close(false);
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e) => await TryConfirmAsync();

    private async void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
            await TryConfirmAsync();
    }

    private async Task TryConfirmAsync()
    {
        if (_isBusy || _validate is null)
            return;

        var email = (EmailBox.Text ?? "").Trim();
        var password = PasswordBox.Text ?? "";
        if (email.Length == 0 || password.Length == 0)
        {
            ShowError(Tr.T("Введите логин и пароль.", "Логин менен паролду киргизиңиз.", "Enter your login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting."));
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
            ? Tr.T("Проверка…", "Текшерүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…")
            : Tr.T("Сменить", "Алмаштыруу", "Switch", "Değiştir", "Almashtirish");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
