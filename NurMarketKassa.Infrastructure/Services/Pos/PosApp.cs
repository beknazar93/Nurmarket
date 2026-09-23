using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa;

/// <summary>
/// Статический мост POS-сессии для общих сервисов Infrastructure:
/// идентификаторы смены, кассы, пользователя и REST API клиенты сайта.
/// </summary>
public static class PosApp
{
    public static AppSettings Settings { get; set; } = null!;
    public static IAuthApiService AuthApi { get; set; } = null!;
    public static ICatalogApiService CatalogApi { get; set; } = null!;
    public static ISalesApiService SalesApi { get; set; } = null!;
    public static IShiftApiService ShiftApi { get; set; } = null!;
    public static MySqlAuditService AuditDb { get; set; } = null!;
    public static string? CurrentUserId { get; set; }
    public static string? PosCashboxId { get; set; }
    public static string? PosCashboxDisplayName { get; set; }
    public static string? ActiveShiftId { get; set; }
    public static bool IsOfflineBootstrap { get; set; }
    public static string? OfflineBootstrapMessage { get; set; }
}
