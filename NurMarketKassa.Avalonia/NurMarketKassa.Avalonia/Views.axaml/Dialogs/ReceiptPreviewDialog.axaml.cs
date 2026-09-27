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
            ? Tr.T("Данные чека недоступны.", "Чектин маалыматтары жеткиликсиз.", "Receipt data is unavailable.", "Fiş verileri kullanılamıyor.", "Chek ma'lumotlari mavjud emas.")
            : content;
    }

    public ReceiptPreviewDialog(string title, string content) : this(content) =>
        TitleText.Text = title;

    public ReceiptPreviewDialog(object? title, object? content)
        : this(title?.ToString() ?? Tr.T("Предпросмотр чека", "Чекти алдын ала көрүү", "Receipt preview", "Fiş önizleme", "Chekni oldindan ko'rish"), content?.ToString() ?? "")
    {
    }

    private void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        IsPrintRequested = true;
        Close(true);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
