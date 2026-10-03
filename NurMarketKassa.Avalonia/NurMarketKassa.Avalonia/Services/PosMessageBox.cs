using System.Windows;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Модальные диалоги Avalonia (аналог WPF PosMessageBox).</summary>
public static class PosMessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(messageBoxText, Tr.T("Nur Market — Касса", "Nur Market — Касса", "Nur Market — Till", "Nur Market — Kasa", "Nur Market — Kassa"));

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(messageBoxText, caption, MessageBoxButton.OK);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        if (button == MessageBoxButton.YesNo
            && string.Equals(caption, "Подтверждение выхода", StringComparison.Ordinal))
        {
            return PosDialogHost.Show(
                    new ExitConfirmationDialog(),
                    PosDialogHost.ResolveOwner(owner)) == true
                ? MessageBoxResult.Yes
                : MessageBoxResult.No;
        }

        if (button == MessageBoxButton.YesNo
            && string.Equals(caption, "Подтверждение", StringComparison.Ordinal)
            && string.Equals(messageBoxText, "Подтвердить оплату?", StringComparison.Ordinal))
        {
            return PaymentConfirmationDialog.Show(owner)
                ? MessageBoxResult.Yes
                : MessageBoxResult.No;
        }

        if (button == MessageBoxButton.YesNoCancel
            && string.Equals(caption, "Смена не закрыта", StringComparison.Ordinal))
        {
            return ShiftNotClosedDialog.Show(owner) switch
            {
                ShiftNotClosedDialogResult.CloseShift => MessageBoxResult.Yes,
                _ => MessageBoxResult.Cancel,
            };
        }

        switch (button)
        {
            case MessageBoxButton.YesNo:
            case MessageBoxButton.YesNoCancel:
            {
                var confirmed = PosConfirmDialog.Show(
                    owner,
                    caption,
                    messageBoxText,
                    confirmText: Tr.T("Да", "Ооба", "Yes", "Evet", "Ha"),
                    cancelText: button == MessageBoxButton.YesNoCancel ? Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish") : Tr.T("Нет", "Жок", "No", "Hayır", "Yo'q"));
                return confirmed ? MessageBoxResult.Yes : MessageBoxResult.No;
            }
            default:
            {
                var kind = MapAlertKind(icon);
                var buttonText = icon == MessageBoxImage.Question ? Tr.T("ОК", "ОК", "OK", "Tamam", "OK") : Tr.T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli");
                if (string.Equals(caption, "Принтер не подключен", StringComparison.OrdinalIgnoreCase)
                    || (messageBoxText.Contains("принтер", StringComparison.OrdinalIgnoreCase)
                        && messageBoxText.Length < 80))
                {
                    PrinterNotConnectedDialog.ShowOk(owner, messageBoxText);
                    return MessageBoxResult.OK;
                }

                PosAlertDialog.Show(owner, caption, messageBoxText, kind, buttonText);
                return MessageBoxResult.OK;
            }
        }
    }

    /// <summary>2026-10-04, Android-касса: <see cref="Show(Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult)"/>
    /// из async-кода. В Windows — тот же синхронный Show (поведение прежнее); на Android — те же
    /// окна, но без вложенного цикла сообщений (его у Avalonia на Android нет).</summary>
    public static async Task<MessageBoxResult> ShowModalAsync(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
#if NURANDROID
        if (button == MessageBoxButton.YesNo
            && string.Equals(caption, "Подтверждение", StringComparison.Ordinal)
            && string.Equals(messageBoxText, "Подтвердить оплату?", StringComparison.Ordinal))
        {
            return await PaymentConfirmationDialog.ShowModalAsync(owner).ConfigureAwait(true)
                ? MessageBoxResult.Yes
                : MessageBoxResult.No;
        }

        if (button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {
            var confirmed = await PosConfirmDialog.ShowModalAsync(
                owner,
                caption,
                messageBoxText,
                confirmText: Tr.T("Да", "Ооба", "Yes", "Evet", "Ha"),
                cancelText: button == MessageBoxButton.YesNoCancel ? Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish") : Tr.T("Нет", "Жок", "No", "Hayır", "Yo'q")).ConfigureAwait(true);
            return confirmed ? MessageBoxResult.Yes : MessageBoxResult.No;
        }

        var buttonText = icon == MessageBoxImage.Question ? Tr.T("ОК", "ОК", "OK", "Tamam", "OK") : Tr.T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli");
        await PosAlertDialog.ShowAsync(owner, caption, messageBoxText, MapAlertKind(icon), buttonText).ConfigureAwait(true);
        return MessageBoxResult.OK;
#else
        await Task.CompletedTask.ConfigureAwait(true);
        return Show(owner, messageBoxText, caption, button, icon);
#endif
    }

    public static ShiftNotClosedDialogResult ShowShiftNotClosed(Window? owner) =>
        ShiftNotClosedDialog.Prompt(owner);

    public static MessageBoxResult ShowPrinterNotConnected(Window? owner)
    {
        var result = PrinterNotConnectedDialog.ShowCheckout(owner);
        return result == PrinterNotConnectedResult.Cancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
    }

    private static PosAlertKind MapAlertKind(MessageBoxImage icon) =>
        icon switch
        {
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => PosAlertKind.Warning,
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => PosAlertKind.Error,
            MessageBoxImage.Information or MessageBoxImage.Asterisk => PosAlertKind.Info,
            _ => PosAlertKind.Info,
        };
}

public static class PosDialogs
{
    public static bool ConfirmYesNo(Window? owner, string message, string? title = null) =>
        PosConfirmDialog.Show(owner, title ?? Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"), message);

    /// <summary>2026-10-04, Android-касса: то же из async-кода (в Windows — прежний синхронный ConfirmYesNo).</summary>
    public static Task<bool> ConfirmYesNoModalAsync(Window? owner, string message, string? title = null) =>
        PosConfirmDialog.ShowModalAsync(owner, title ?? Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"), message);

    public static void Info(Window? owner, string message, string? title = null) =>
        PosAlertDialog.Show(owner, title ?? Tr.T("Сообщение", "Билдирүү", "Message", "Mesaj", "Xabar"), message, PosAlertKind.Info);

    public static void Warning(Window? owner, string message, string? title = null) =>
        PosAlertDialog.Show(owner, title ?? Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"), message, PosAlertKind.Warning);

    public static void Error(Window? owner, string message, string? title = null) =>
        PosAlertDialog.Show(owner, title ?? Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"), message, PosAlertKind.Error);

    public static PaymentSuccessDialogResult? ShowPaymentSuccess(Window? owner, double totalAmount, bool defaultPrintReceipt)
    {
        var dlg = new SaleSuccessDialog(totalAmount);
        if (PosDialogHost.Show(dlg, owner) != true)
            return null;

        return dlg.Action switch
        {
            SaleSuccessDialogAction.Print => new PaymentSuccessDialogResult { PrintReceipt = true },
            SaleSuccessDialogAction.Preview => HandlePreview(owner),
            _ => new PaymentSuccessDialogResult { PrintReceipt = false },
        };
    }

    private static PaymentSuccessDialogResult HandlePreview(Window? owner)
    {
        ShowReceiptPreviewStub(owner);
        return new PaymentSuccessDialogResult { PrintReceipt = false };
    }

    public static PrinterNotConnectedResult ShowPrinterNotConnected(Window? owner) =>
        PrinterNotConnectedDialog.ShowCheckout(owner);

    public static void ShowReceiptPreviewStub(Window? owner)
    {
        PosLogger.Log("Preview requested. Feature not implemented yet.", "RECEIPT_PREVIEW");
        PosAlertDialog.Show(
            owner,
            Tr.T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish"),
            Tr.T("Предпросмотр чека будет доступен в следующей версии.", "Чекти алдын ала көрүү кийинки версияда жеткиликтүү болот.", "Receipt preview will be available in the next version.", "Fiş önizleme bir sonraki sürümde kullanılabilir olacak.", "Chekni oldindan ko'rish keyingi versiyada mavjud bo'ladi."),
            PosAlertKind.Info);
    }
}

public sealed class PaymentSuccessDialogResult
{
    public bool PrintReceipt { get; init; }
}
