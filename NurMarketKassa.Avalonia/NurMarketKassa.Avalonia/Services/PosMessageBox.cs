using System.Windows;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Модальные диалоги Avalonia (аналог WPF PosMessageBox).</summary>
public static class PosMessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(messageBoxText, "Nur Market — Касса");

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
                    confirmText: "Да",
                    cancelText: button == MessageBoxButton.YesNoCancel ? "Отмена" : "Нет");
                return confirmed ? MessageBoxResult.Yes : MessageBoxResult.No;
            }
            default:
            {
                var kind = MapAlertKind(icon);
                var buttonText = icon == MessageBoxImage.Question ? "ОК" : "Понятно";
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
    public static bool ConfirmYesNo(Window? owner, string message, string title = "Подтверждение") =>
        PosConfirmDialog.Show(owner, title, message);

    public static void Info(Window? owner, string message, string title = "Сообщение") =>
        PosAlertDialog.Show(owner, title, message, PosAlertKind.Info);

    public static void Warning(Window? owner, string message, string title = "Внимание") =>
        PosAlertDialog.Show(owner, title, message, PosAlertKind.Warning);

    public static void Error(Window? owner, string message, string title = "Ошибка") =>
        PosAlertDialog.Show(owner, title, message, PosAlertKind.Error);

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
            "Предпросмотр",
            "Предпросмотр чека будет доступен в следующей версии.",
            PosAlertKind.Info);
    }
}

public sealed class PaymentSuccessDialogResult
{
    public bool PrintReceipt { get; init; }
}
