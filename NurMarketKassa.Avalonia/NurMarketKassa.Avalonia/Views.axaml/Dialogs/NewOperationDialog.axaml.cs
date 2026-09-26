using System.Globalization;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class NewOperationDialog : Window
{
    private bool _isDeposit = true;

    public CashOperationModel? ResultOperation { get; set; }
    public bool? DialogResult { get; set; }

    public NewOperationDialog() : this(isDeposit: true) { }

    public NewOperationDialog(bool isDeposit)
    {
        _isDeposit = isDeposit;
        InitializeComponent();
        DepositTab.IsChecked = isDeposit;
        WithdrawTab.IsChecked = !isDeposit;
        DepositTab.IsCheckedChanged += OnOpTypeChanged;
        WithdrawTab.IsCheckedChanged += OnOpTypeChanged;
        DateBox.SelectedDate = DateTime.Today;
        // В списке — имя кассира, а не его внутренний ID (раньше показывался GUID
        // «6e39345d-ba15-…»). В саму операцию по-прежнему пишется ID, см. Save_Click.
        var cashier = !string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserDisplayName)
            ? NurMarketKassa.PosApp.CurrentUserDisplayName!
            : App.CurrentUserId ?? "Кассир 1";
        CashierBox.Items.Clear();
        CashierBox.Items.Add(cashier);
        CashierBox.SelectedIndex = 0;
    }

    public NewOperationDialog(object? arg) : this(arg is true) { }

    private void OnOpTypeChanged(object? sender, RoutedEventArgs e) =>
        _isDeposit = DepositTab.IsChecked == true;

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var raw = (AmountBox.Text ?? "").Replace(',', '.').Trim();
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            PosMessageBox.Show(this,
                Tr.T("Укажите корректную сумму.", "Туура сумманы көрсөтүңүз.", "Enter a valid amount.",
                    "Geçerli bir tutar girin.", "To'g'ri summani kiriting."),
                Tr.T("Новая операция", "Жаңы операция", "New operation", "Yeni işlem", "Yangi operatsiya"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var type = _isDeposit ? "Внесение" : "Изъятие";
        var createdAt = DateTime.Now;
        var cashier = App.CurrentUserId ?? CashierBox.SelectedItem?.ToString() ?? "—";
        var reason = (ReasonBox.Text ?? "").Trim();
        var comment = (CommentBox.Text ?? "").Trim();
        var note = string.IsNullOrWhiteSpace(comment) ? reason : comment;

        ResultOperation = new CashOperationModel
        {
            CreatedAt = createdAt,
            Type = type,
            Kind = CashOperationModel.ResolveKind(type),
            Amount = amount,
            Cashier = cashier,
            Reason = reason,
            Comment = note,
        };

        DialogResult = true;
        Close(true);
    }
}
