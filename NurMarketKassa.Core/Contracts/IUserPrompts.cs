namespace NurMarketKassa.Core.Contracts;

public interface IUserPrompts
{
    Task<bool> ConfirmAsync(string message);
    void ShowToast(string message, bool isWarning = false);
    void ShowWarning(string message);
    void ShowError(string message);
}
