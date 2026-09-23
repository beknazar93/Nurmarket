using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentStockBlockedDialog : Window
{
    public PaymentStockBlockedDialog() : this([]) { }

    public PaymentStockBlockedDialog(IReadOnlyList<(string Title, StockLineStatus Status)> issues)
    {
        InitializeComponent();
        var sb = new StringBuilder();
        sb.AppendLine("Недостаточно товара на складе для оплаты:");
        sb.AppendLine();

        foreach (var (title, status) in issues)
        {
            sb.AppendLine(title);
            sb.AppendLine($"В чеке: {FormatQty(status.LineQty)}");
            sb.AppendLine($"Доступно: {FormatQty(status.Available)}");
            sb.AppendLine();
        }

        BodyText.Text = sb.ToString().TrimEnd();
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
}
