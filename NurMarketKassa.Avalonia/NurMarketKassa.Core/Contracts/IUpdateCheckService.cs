namespace NurMarketKassa.Core.Contracts;

/// <summary>Проверяет манифест обновления и сравнивает версию с текущей сборкой.</summary>
public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Результат проверки обновления.
/// <see cref="IsConfigured"/> = false, когда ManifestUrl не задан — это не ошибка,
/// а нормальное "выключенное" состояние функции.
/// </summary>
public sealed record UpdateCheckResult(
    bool IsConfigured,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    string? ErrorMessage);
