namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Главное окно после входа: касса (MainWindow) или программа владельца
/// (OwnerShellWindow) — см. NurMarketKassa.Services.AppMode и App.ResolveMainShell.</summary>
public interface IMainShell
{
    /// <summary>False — открывать окно нельзя (например, подписка NurCRM не оплачена).</summary>
    Task<bool> InitializeApplicationAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    void PlaceOnPrimaryScreen();
}
