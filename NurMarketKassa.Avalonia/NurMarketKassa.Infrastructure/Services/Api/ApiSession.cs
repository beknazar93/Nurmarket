using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Разделяемое состояние сессии Nur CRM (токены, профиль, активный филиал) и
/// примитивы синхронизации HTTP. Один экземпляр на всё приложение (Singleton),
/// чтобы доменные сервисы (auth/catalog/sales/shift) работали поверх единой сессии
/// и единого глобального лимита параллельных запросов.
/// </summary>
public sealed class ApiSession : IDisposable
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public JsonElement UserPayload { get; set; }
    public string? ActiveBranchId { get; set; }

    internal JsonSerializerOptions JsonWrite { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal JsonSerializerOptions JsonRead { get; } = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Исключает гонки при входе и ручном refresh.</summary>
    internal SemaphoreSlim LoginMutex { get; } = new(1, 1);

    /// <summary>Параллельные GET/POST к API (раньше один глобальный замок сильно замедлял каталог).</summary>
    internal SemaphoreSlim HttpSlots { get; } = new(
        Math.Min(12, Math.Max(4, Environment.ProcessorCount * 2)),
        Math.Min(12, Math.Max(4, Environment.ProcessorCount * 2)));

    /// <summary>Серийный refresh токена при 401 из параллельных запросов.</summary>
    internal SemaphoreSlim RefreshSync { get; } = new(1, 1);

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        UserPayload = default;
        ActiveBranchId = null;
        CompanyInfoService.Clear();
    }

    public void Dispose()
    {
        LoginMutex.Dispose();
        HttpSlots.Dispose();
        RefreshSync.Dispose();
    }
}
