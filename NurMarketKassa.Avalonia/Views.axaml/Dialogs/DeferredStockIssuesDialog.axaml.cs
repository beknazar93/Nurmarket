using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class DeferredStockIssuesDialog : Window
{
    public DeferredStockIssuesDialog() : this([]) { }

    public DeferredStockIssuesDialog(IReadOnlyList<(string Title, StockLineStatus Status)> issues)
    {
        InitializeComponent();
        var sb = new StringBuilder();
        sb.AppendLine("В отложенном чеке есть товары,");
        sb.AppendLine("которых больше нет на складе.");
        sb.AppendLine();

        foreach (var (title, status) in issues)
        {
            sb.AppendLine(title);
            sb.AppendLine($"Было: {FormatQty(status.LineQty)}");
            sb.AppendLine($"Доступно: {FormatQty(status.Available)}");
            sb.AppendLine();
        }

        sb.AppendLine("Продажа невозможна до корректировки.");
        BodyText.Text = sb.ToString().TrimEnd();
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
}
