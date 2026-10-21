using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-18, десятый круг ревью (деньги): «Оплата есть на сайте» для оплаты ОДНОЙ СУММОЙ — сервер мог провести не
/// всю сумму (часть ушла бы в погашенные чеки), поэтому кассир вводит сумму, которую видит на сайте NurCRM: по умолчанию — сумма
/// записи, больше неё нельзя. В смену пишется именно она. Результат ShowDialog — сумма или null (отмена).</summary>
internal sealed class DebtAppliedAmountDialog : Window
{
    private readonly double _max;
    private readonly TextBox _amount;
    private readonly TextBlock _error = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _ok;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public DebtAppliedAmountDialog(UnknownDebtPayment p)
    {
        _max = p.Amount;
        Title = DebtPaymentRunner.PaidOnSiteTitle;
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var message = new TextBlock { Text = DebtPaymentRunner.PaidOnSiteMessage(p), FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        message.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var label = new TextBlock
        {
            Text = T("Сумма, которую сервер провёл (по сайту NurCRM), сом:", "Сервер өткөргөн сумма (NurCRM сайты боюнча), сом:",
                "Amount the server applied (per the NurCRM site), som:", "Sunucunun işlediği tutar (NurCRM sitesine göre), som:",
                "Server o'tkazgan summa (NurCRM sayti bo'yicha), so'm:"),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _amount = new TextBox { Text = p.Amount.ToString("0.##", CultureInfo.InvariantCulture), FontSize = 20, FontWeight = FontWeight.Bold, Height = 48, MaxLength = 14 };
        _amount.TextChanged += (_, _) => Validate();
        _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));

        var cancel = new Button { Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(null);
        _ok = new Button { Content = DebtPaymentRunner.PaidOnSiteConfirmText, IsDefault = true };
        _ok.Classes.Add("modal-primary");
        _ok.Classes.Add("danger");
        _ok.Click += OnOk;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title, Footer = buttons,
            Content = new StackPanel { Spacing = 8, Children = { message, label, _amount, _error } },
        };
        Opened += (_, _) => _amount.Focus();
    }

    private double? Value =>
        CashAmountInput.TryParse(_amount.Text, out var v) && v > 0m ? (double)Math.Round(v, 2) : null;

    private bool Validate()
    {
        string? problem = null;
        if (Value is not { } v)
            problem = T("Сумма — число больше нуля.", "Сумма — нөлдөн чоң сан.", "The amount must be a number above zero.", "Tutar sıfırdan büyük bir sayı olmalı.", "Summa — noldan katta son.");
        else if (v > _max + 0.005)
            problem = T($"Не больше суммы этой оплаты ({DebtPaymentFlow.Money(_max)}).", $"Бул төлөмдүн суммасынан ({DebtPaymentFlow.Money(_max)}) ашпайт.",
                $"No more than this payment's amount ({DebtPaymentFlow.Money(_max)}).", $"Bu ödemenin tutarından ({DebtPaymentFlow.Money(_max)}) fazla olamaz.",
                $"Bu to'lov summasidan ({DebtPaymentFlow.Money(_max)}) ko'p emas.");
        _error.Text = problem ?? "";
        _error.IsVisible = problem is not null;
        _ok.IsEnabled = problem is null;
        return problem is null;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (Validate() && Value is { } v)
            Close((double?)v);
    }
}
