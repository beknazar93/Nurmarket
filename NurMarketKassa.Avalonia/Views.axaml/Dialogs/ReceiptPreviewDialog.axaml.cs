using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReceiptPreviewDialog : Window
{
    public bool IsPrintRequested { get; private set; }

    public ReceiptPreviewDialog() : this("") { }

    public ReceiptPreviewDialog(string content)
    {
        InitializeComponent();
        ReceiptText.Text = string.IsNullOrWhiteSpace(content)
            ? "Данные чека недоступны."
            : content;
    }

    public ReceiptPreviewDialog(string title, string content) : this(content) =>
        TitleText.Text = title;

    public ReceiptPreviewDialog(object? title, object? content)
        : this(title?.ToString() ?? "Предпросмотр чека", content?.ToString() ?? "")
    {
    }

    private void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        IsPrintRequested = true;
        Close(true);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
