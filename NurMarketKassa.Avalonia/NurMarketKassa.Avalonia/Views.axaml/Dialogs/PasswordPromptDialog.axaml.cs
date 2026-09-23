using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PasswordPromptDialog : Window
{
    private readonly string _expectedPassword;
    private readonly System.Func<string, bool>? _validator;

    public PasswordPromptDialog()
    {
        InitializeComponent();
        _expectedPassword = "";
    }

    public PasswordPromptDialog(string title, string message, string expectedPassword)
    {
        InitializeComponent();
        _expectedPassword = expectedPassword;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>2026-09-08: вариант с произвольной проверкой ввода вместо сравнения с одним
    /// заранее известным паролем — нужен для личных кодов сотрудников (EmployeeAccessGate),
    /// где верных значений несколько (свой код у каждого сотрудника).</summary>
    public PasswordPromptDialog(string title, string message, System.Func<string, bool> validator)
    {
        InitializeComponent();
        _expectedPassword = "";
        _validator = validator;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>Показывает диалог и сверяет ввод с паролем кассы (Company.cashier_password)
    /// прямо здесь — при неверном пароле окно не закрывается, а показывает ошибку.</summary>
    public static bool Show(Window? owner, string title, string message, string expectedPassword) =>
        PosDialogHost.Show(new PasswordPromptDialog(title, message, expectedPassword), owner) == true;

    public static bool Show(Window? owner, string title, string message, System.Func<string, bool> validator) =>
        PosDialogHost.Show(new PasswordPromptDialog(title, message, validator), owner) == true;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => TryConfirm();

    private void PasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TryConfirm();
    }

    private void TryConfirm()
    {
        var entered = PasswordBox.Text ?? "";
        var accepted = _validator != null
            ? _validator(entered)
            : !string.IsNullOrEmpty(_expectedPassword) && string.Equals(entered, _expectedPassword, System.StringComparison.Ordinal);
        if (accepted)
        {
            Close(true);
            return;
        }

        ErrorText.Text = Tr.T("Неверный пароль.", "Пароль туура эмес.", "Incorrect password.", "Yanlış şifre.", "Parol noto'g'ri.");
        ErrorText.IsVisible = true;
        PasswordBox.Text = "";
        PasswordBox.Focus();
    }
}
