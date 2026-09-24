namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Настоящее скачивание и установка обновления (в отличие от <see cref="IUpdateCheckService"/>,
/// который только сравнивает версии и отдаёт ссылку на страницу релиза). Обёртка над
/// Velopack.UpdateManager: тот же релиз на GitHub, но здесь скачиваем и ставим пакет сами,
/// а не открываем кассиру браузер.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Есть ли уже проверенное обновление, готовое к скачиванию
    /// (заполняется после успешного <see cref="CheckAsync"/>).</summary>
    bool HasPendingUpdate { get; }

    Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Скачивает обновление, найденное предыдущим <see cref="CheckAsync"/>.
    /// <paramref name="onProgress"/> получает проценты 0–100.</summary>
    Task DownloadAsync(Action<int> onProgress, CancellationToken cancellationToken = default);

    /// <summary>Завершает работу приложения, накатывает скачанный пакет и перезапускает кассу
    /// уже на новой версии. Не возвращает управление — процесс завершается изнутри вызова.</summary>
    void ApplyUpdateAndRestart();

    /// <summary>Список всех версий, опубликованных в релизах (не только более новых) — нужен
    /// для отката на прошлую версию, если новое обновление оказалось хуже.</summary>
    Task<IReadOnlyList<AppReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Помечает версию из <see cref="ListVersionsAsync"/> как ожидающую скачивания —
    /// после этого <see cref="DownloadAsync"/>/<see cref="ApplyUpdateAndRestart"/> ставят именно
    /// её, как обычное обновление. Возвращает false, если версия не найдена в последнем
    /// полученном списке (нужно сначала вызвать <see cref="ListVersionsAsync"/>).</summary>
    bool PrepareRollback(string version);

    /// <summary>Текст описания релиза для конкретной версии — показывается перед откатом,
    /// чтобы было видно, что реально было в той версии. Null, если получить не удалось
    /// (нет сети, версии нет на GitHub и т.п.) — откат в этом случае всё равно возможен.</summary>
    Task<string?> GetReleaseNotesAsync(string version, CancellationToken cancellationToken = default);
}

/// <param name="TestingVersion">Более новая версия, которая пока в тестировании (pre-release на
/// GitHub): клиенту её не ставим, а говорим, что она в тесте (см. UpdateChannel).</param>
public sealed record AppUpdateCheckResult(
    bool IsConfigured,
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? ErrorMessage,
    string? TestingVersion = null);

public sealed record AppReleaseVersion(string Version, string? Notes, bool IsCurrent);
