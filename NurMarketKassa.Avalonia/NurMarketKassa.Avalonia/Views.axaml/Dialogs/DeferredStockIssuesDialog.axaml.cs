using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class DeferredStockIssuesDialog : Window
{
    public DeferredStockIssuesDialog() : this([]) { }

    public DeferredStockIssuesDialog(IReadOnlyList<(string Title, StockLineStatus Status)> issues)
    {
        InitializeComponent();
        var sb = new StringBuilder();
        sb.AppendLine(Tr.T("В отложенном чеке есть товары,", "Калтырылган чекте кампада", "The held receipt contains products", "Bekletilen fişte artık depoda", "Kechiktirilgan chekda omborda"));
        sb.AppendLine(Tr.T("которых больше нет на складе.", "калбаган товарлар бар.", "that are no longer in stock.", "bulunmayan ürünler var.", "qolmagan mahsulotlar bor."));
        sb.AppendLine();

        foreach (var (title, status) in issues)
        {
            sb.AppendLine(title);
            sb.AppendLine(Tr.T($"Было: {FormatQty(status.LineQty)}", $"Чекте: {FormatQty(status.LineQty)}", $"In receipt: {FormatQty(status.LineQty)}", $"Fişte: {FormatQty(status.LineQty)}", $"Chekda: {FormatQty(status.LineQty)}"));
            sb.AppendLine(Tr.T($"Доступно: {FormatQty(status.Available)}", $"Жеткиликтүү: {FormatQty(status.Available)}", $"Available: {FormatQty(status.Available)}", $"Mevcut: {FormatQty(status.Available)}", $"Mavjud: {FormatQty(status.Available)}"));
            sb.AppendLine();
        }

        sb.AppendLine(Tr.T("Продажа невозможна до корректировки.", "Оңдолмоюнча сатуу мүмкүн эмес.", "The sale cannot proceed until this is corrected.", "Düzeltilene kadar satış yapılamaz.", "To'g'rilanmaguncha sotib bo'lmaydi."));
        BodyText.Text = sb.ToString().TrimEnd();
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
}
