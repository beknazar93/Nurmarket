using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentConfirmationDialog : Window
{
    public PaymentConfirmationDialog()
    {
        InitializeComponent();
        // 2026-10-22 (модалки, пакет 1): глаголы вместо «Нет» / «Подтвердить»; Enter сканера оплату не подтверждает.
        NoButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
        YesButton.Content = Tr.T("Провести оплату", "Төлөмдү өткөрүү", "Complete payment", "Ödemeyi tamamla", "To'lovni o'tkazish");
        MessageText.Text = Tr.T("Проверьте сумму и способ оплаты. После подтверждения чек будет проведён.",
            "Сумманы жана төлөө ыкмасын текшериңиз. Ырастагандан кийин чек өткөрүлөт.",
            "Check the amount and payment method. The receipt is completed after you confirm.",
            "Tutarı ve ödeme yöntemini kontrol edin. Onayladıktan sonra fiş tamamlanır.",
            "Summa va to'lov usulini tekshiring. Tasdiqlagandan keyin chek o'tkaziladi.");
        Shell.InitialFocus = YesButton;
        ScannerEnterGuard.Attach(this);
    }

    public static new bool Show(Window? owner) =>
        PosDialogHost.Show(new PaymentConfirmationDialog(), owner) == true;

    /// <summary>2026-10-04, Android-касса: из async-кода (в Windows — прежний синхронный Show).</summary>
    public static async Task<bool> ShowModalAsync(Window? owner) =>
        await PosDialogHost.ShowModalAsync(new PaymentConfirmationDialog(), owner).ConfigureAwait(true) == true;

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
