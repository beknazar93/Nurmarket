using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum ReturnReasonDialogKind
{
    LineItems,
    FullReceipt,
}

public partial class ReturnLineReasonDialog : Window
{
    public ReturnLineReasonDialog() : this(1) { }

    public ReturnLineReasonDialog(int selectedItemCount = 1, ReturnReasonDialogKind kind = ReturnReasonDialogKind.LineItems)
    {
        InitializeComponent();

        if (kind == ReturnReasonDialogKind.FullReceipt)
        {
            Title = "Причина полного возврата";
            HintText.Text = "Укажите причину. Она будет передана в CRM для возврата всего чека целиком.";
        }
        else
        {
            Title = "Причина возврата";
            HintText.Text = selectedItemCount <= 1
                ? "Комментарий будет передан в CRM вместе с возвратом позиции."
                : $"Одна и та же причина будет указана для {selectedItemCount} выбранных позиций и передана в CRM.";
        }

        Opened += (_, _) => ReasonBox.Focus();
    }

    public string ReasonText => (ReasonBox.Text ?? "").Trim();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        if (ReasonText.Length == 0)
        {
            ErrorText.Text = "Введите причину возврата.";
            ErrorText.IsVisible = true;
            return;
        }

        Close(true);
    }
}
