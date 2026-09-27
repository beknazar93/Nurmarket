using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentStockBlockedDialog : Window
{
    public PaymentStockBlockedDialog() : this([]) { }

    public PaymentStockBlockedDialog(IReadOnlyList<(string Title, StockLineStatus Status)> issues)
    {
        InitializeComponent();
        var sb = new StringBuilder();
        sb.AppendLine(Tr.T("Недостаточно товара на складе для оплаты:", "Төлөө үчүн кампада товар жетишсиз:", "Not enough stock to complete the payment:", "Ödeme için depoda yeterli ürün yok:", "To'lov uchun omborda mahsulot yetarli emas:"));
        sb.AppendLine();

        foreach (var (title, status) in issues)
        {
            sb.AppendLine(title);
            sb.AppendLine(Tr.T($"В чеке: {FormatQty(status.LineQty)}", $"Чекте: {FormatQty(status.LineQty)}", $"In receipt: {FormatQty(status.LineQty)}", $"Fişte: {FormatQty(status.LineQty)}", $"Chekda: {FormatQty(status.LineQty)}"));
            sb.AppendLine(Tr.T($"Доступно: {FormatQty(status.Available)}", $"Жеткиликтүү: {FormatQty(status.Available)}", $"Available: {FormatQty(status.Available)}", $"Mevcut: {FormatQty(status.Available)}", $"Mavjud: {FormatQty(status.Available)}"));
            sb.AppendLine();
        }

        BodyText.Text = sb.ToString().TrimEnd();
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
}
