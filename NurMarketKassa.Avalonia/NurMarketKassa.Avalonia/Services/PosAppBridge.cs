using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa;

/// <summary>
/// Синхронизирует Avalonia-хост с общим статическим мостом <see cref="PosApp"/> Infrastructure.
/// </summary>
public static class App
{
    public static AppSettings Settings
    {
        get => PosApp.Settings;
        set => PosApp.Settings = value;
    }

    public static IAuthApiService AuthApi
    {
        get => PosApp.AuthApi;
        set => PosApp.AuthApi = value;
    }

    public static ICatalogApiService CatalogApi
    {
        get => PosApp.CatalogApi;
        set => PosApp.CatalogApi = value;
    }

    public static ISalesApiService SalesApi
    {
        get => PosApp.SalesApi;
        set => PosApp.SalesApi = value;
    }

    public static IShiftApiService ShiftApi
    {
        get => PosApp.ShiftApi;
        set => PosApp.ShiftApi = value;
    }

    public static MySqlAuditService AuditDb
    {
        get => PosApp.AuditDb;
        set => PosApp.AuditDb = value;
    }

    public static string? CurrentUserId
    {
        get => PosApp.CurrentUserId;
        set => PosApp.CurrentUserId = value;
    }

    public static string? CurrentUserDisplayName
    {
        get => PosApp.CurrentUserDisplayName;
        set => PosApp.CurrentUserDisplayName = value;
    }

    public static string? PosCashboxId
    {
        get => PosApp.PosCashboxId;
        set => PosApp.PosCashboxId = value;
    }

    public static string? PosCashboxDisplayName
    {
        get => PosApp.PosCashboxDisplayName;
        set => PosApp.PosCashboxDisplayName = value;
    }

    public static string? ActiveShiftId
    {
        get => PosApp.ActiveShiftId;
        set => PosApp.ActiveShiftId = value;
    }

    public static bool IsOfflineBootstrap
    {
        get => PosApp.IsOfflineBootstrap;
        set => PosApp.IsOfflineBootstrap = value;
    }

    public static string? OfflineBootstrapMessage
    {
        get => PosApp.OfflineBootstrapMessage;
        set => PosApp.OfflineBootstrapMessage = value;
    }

    public static void InitializeFromHost(
        AppSettings settings,
        IAuthApiService authApi,
        ICatalogApiService catalogApi,
        ISalesApiService salesApi,
        IShiftApiService shiftApi,
        MySqlAuditService auditDb,
        IAppSession session)
    {
        PosApp.Settings = settings;
        PosApp.AuthApi = authApi;
        PosApp.CatalogApi = catalogApi;
        PosApp.SalesApi = salesApi;
        PosApp.ShiftApi = shiftApi;
        PosApp.AuditDb = auditDb;
        SyncFromSession(session);
    }

    public static void SyncFromSession(IAppSession session)
    {
        var sameUser = string.Equals(PosApp.CurrentUserId, session.CurrentUserId, StringComparison.OrdinalIgnoreCase);
        PosApp.CurrentUserId = session.CurrentUserId;
        if (!sameUser || !string.IsNullOrWhiteSpace(session.CurrentUserDisplayName))
            PosApp.CurrentUserDisplayName = session.CurrentUserDisplayName;
        PosApp.ActiveShiftId = session.ActiveShiftId;
        PosApp.PosCashboxDisplayName = session.PosCashboxDisplayName;
        PosApp.IsOfflineBootstrap = session.IsOfflineBootstrap;
        PosApp.OfflineBootstrapMessage = session.OfflineBootstrapMessage;
        if (!string.IsNullOrWhiteSpace(session.ActiveTerminal))
            PosApp.PosCashboxId = session.ActiveTerminal;

        // Чинит старые записи истории списаний, где вместо имени кассира сохранился сырой
        // GUID (см. WriteOffHistoryStore.BackfillCashierName) — теперь, когда DisplayName
        // известен, подменяет их на реальное имя.
        if (!string.IsNullOrWhiteSpace(PosApp.CurrentUserId) && !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName))
            WriteOffHistoryStore.BackfillCashierName(PosApp.CurrentUserId, PosApp.CurrentUserDisplayName);
    }

    public static void SyncToSession(IAppSession session)
    {
        // Пустое имя не затирает известное имя того же кассира (баг «Кассир —»): другой кассир
        // появляется только через новый вход, и там имя приходит заново.
        var sameUser = string.Equals(session.CurrentUserId, PosApp.CurrentUserId, StringComparison.OrdinalIgnoreCase);
        session.CurrentUserId = PosApp.CurrentUserId;
        if (!sameUser || !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName))
            session.CurrentUserDisplayName = PosApp.CurrentUserDisplayName;
        session.ActiveShiftId = PosApp.ActiveShiftId;
        session.PosCashboxDisplayName = PosApp.PosCashboxDisplayName;
        session.IsOfflineBootstrap = PosApp.IsOfflineBootstrap;
        session.OfflineBootstrapMessage = PosApp.OfflineBootstrapMessage;
        if (!string.IsNullOrWhiteSpace(PosApp.PosCashboxId))
            session.ActiveTerminal = PosApp.PosCashboxId;
    }
}
