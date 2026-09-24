using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>In-memory session store for the Avalonia host (replaces static <c>App.*</c> bridge).</summary>
public sealed class AvaloniaAppSession : IAppSession
{
    public string? CurrentUserId { get; set; }

    private string? _currentUserDisplayName;

    /// <summary>Имя кассира. 2026-09-25, живой баг «Кассир —»: имя приходит при входе, а потом
    /// где-то пропадает. Пока источник не найден, каждое стирание имени у вошедшего кассира
    /// пишется в лог вместе с местом вызова — следующий случай покажет виновника.</summary>
    public string? CurrentUserDisplayName
    {
        get => _currentUserDisplayName;
        set
        {
            if (string.IsNullOrWhiteSpace(value)
                && !string.IsNullOrWhiteSpace(_currentUserDisplayName)
                && !string.IsNullOrWhiteSpace(CurrentUserId))
            {
                NurMarketKassa.Services.PosLogger.Log(
                    $"Имя кассира стёрто (было «{_currentUserDisplayName}»): {Environment.StackTrace}", "WARNING");
            }

            _currentUserDisplayName = value;
        }
    }

    public string? ActiveShiftId { get; set; }

    public string? ActiveTerminal { get; set; }

    public string? PosCashboxDisplayName { get; set; }

    public bool IsShiftOpen => !string.IsNullOrEmpty(ActiveShiftId);

    public bool IsOfflineBootstrap { get; set; }

    public string? OfflineBootstrapMessage { get; set; }
}
