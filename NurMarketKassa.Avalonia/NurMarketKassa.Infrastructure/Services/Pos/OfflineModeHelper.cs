namespace NurMarketKassa.Services;

/// <summary>
/// Определяет режим офлайн-работы кассы и доступность операций без REST API сайта.
/// </summary>
public static class OfflineModeHelper
{
    public static bool IsNetworkOnline => !UseLocalOperations;

    public static bool UseLocalOperations => PosApp.IsOfflineBootstrap;

    public static bool CanOperateWithoutServer => UseLocalOperations;
}
