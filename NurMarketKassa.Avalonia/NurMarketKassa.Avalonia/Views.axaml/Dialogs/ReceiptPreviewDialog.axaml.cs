using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

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

    // 2026-10-22 (модалки, пакет 1): заголовок — в шапке общей оболочки ModalShell.
    public ReceiptPreviewDialog(string title, string content) : this(content) =>
        Shell.Title = title;

    public ReceiptPreviewDialog(object? title, object? content)
        : this(title?.ToString() ?? Tr.T("Предпросмотр чека", "Чекти алдын ала көрүү", "Receipt preview", "Fiş önizleme", "Chekni oldindan ko'rish"), content?.ToString() ?? "")
    {
    }

    /// <summary>2026-10-27, владелец: «вид чека как в мобилке» — предпросмотр копии чека после оплаты показывает бумагу чека (общий контрол)
    /// и панель «QR / PDF / Отправить»; печатная лента (текст ESC/POS) этим не заменяется — это её просмотр в настройках кассы.</summary>
    internal ReceiptPreviewDialog(SaleReceiptCardData card) : this("")
    {
        var bar = new ReceiptActionBar { GetData = () => card, OwnerWindow = this, IsReady = true };
        Shell.Content = new StackPanel
        {
            Children =
            {
                new Border { Padding = new Avalonia.Thickness(0, 4, 0, 14), Child = SaleReceiptCardView.Build(card) },
                bar,
            },
        };
    }

    private void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        IsPrintRequested = true;
        Close(true);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
