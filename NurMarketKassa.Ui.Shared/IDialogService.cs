namespace NurMarketKassa.Ui.Shared;

/// <summary>
/// Cross-platform user dialogs (confirm / info / error / POS-specific prompts).
/// </summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);

    Task ShowInfoAsync(string message);

    Task ShowErrorAsync(string message);

    Task<PrinterNotConnectedResult> ShowPrinterNotConnectedAsync();

    Task<bool> ConfirmPaymentAsync();
}

public enum PrinterNotConnectedResult
{
    ContinueWithoutPrint,
    Cancel
}
