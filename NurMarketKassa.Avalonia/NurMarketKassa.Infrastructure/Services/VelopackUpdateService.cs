using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace NurMarketKassa.Services;

/// <summary>
/// Настоящее скачивание/установка обновления через Velopack.UpdateManager + GithubSource —
/// тот же GitHub-релиз, что читает <see cref="UpdateCheckService"/>, но здесь пакет
/// скачивается и накатывается прямо в кассе, без похода кассира в браузер.
///
/// UpdateManager конструируется с locator=null: это разрешено только потому, что
/// Program.cs уже вызвал VelopackApp.Build().Run() при старте — тот выставляет
/// статический Velopack.Locators.VelopackLocator.Current, на который null здесь и падает
/// (проверено отдельным консольным тестом: без VelopackApp.Run() тот же вызов бросает
/// "No VelopackLocator has been set").
/// </summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private static readonly Regex GithubApiUrlPattern = new(
        @"api\.github\.com/repos/(?<owner>[^/]+)/(?<repo>[^/]+)/releases",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Список для отката показывает только несколько последних версий — откат на
    /// что-то совсем старое почти никогда не нужен и только захламляет список.</summary>
    private const int MaxVersionsToList = 4;

    private readonly UpdateSettings _settings;
    private UpdateManager? _manager;
    private IUpdateSource? _source;
    private UpdateInfo? _pendingUpdate;
    private VelopackAsset[]? _availableAssets;

    public VelopackUpdateService(AppSettings appSettings) => _settings = appSettings.Updates;

    public bool HasPendingUpdate => _pendingUpdate != null;

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manager = GetOrCreateManager();
        if (manager is null)
            return new AppUpdateCheckResult(false, false, null, null, null);

        var currentVersion = manager.CurrentVersion?.ToString() ?? "0.0.0";

        // Velopack умеет обновлять только УСТАНОВЛЕННОЕ приложение: ему нужен свой каталог с
        // Update.exe рядом. При запуске из папки сборки (bin\Debug, publish-out) он бросает
        // InvalidOperationException с английским текстом "This operation can not be performed
        // in an application that is not installed", и раньше этот текст уходил прямо на экран
        // кассиру (живой скриншот владельца 2026-09-22). Проверяем заранее и говорим по-русски,
        // что именно происходит — это не поломка, а запуск не из установленной копии.
        if (!manager.IsInstalled)
        {
            return new AppUpdateCheckResult(true, false, currentVersion, null,
                "приложение запущено не из установленной копии (папка сборки или переносимый " +
                "режим). Обновления работают только в версии, установленной через установщик.");
        }

        try
        {
            _pendingUpdate = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pendingUpdate is null)
                return new AppUpdateCheckResult(true, false, currentVersion, null, null);

            var latestVersion = _pendingUpdate.TargetFullRelease.Version.ToString();

            // 2026-09-21, живой баг (скриншот владельца): Velopack сверяет доступные обновления
            // со своим ВНУТРЕННИМ "locator" — тот иногда отстаёт от реально установленной сборки
            // (см. комментарий у CurrentVersion выше и в MarketplaceView.CheckForUpdatesAsync —
            // особенно после ручной тихой переустановки в обход штатного самообновления, как
            // при локальной разработке). В этот раз результат был ещё хуже: locator решил, что
            // последний GitHub-релиз (1.16.45) новее РЕАЛЬНО запущенной сборки (1.16.49), и
            // предложил "обновиться" — фактически откатиться, стерев более новые локальные
            // правки. Сверяем latestVersion с версией самой запущенной сборки (всегда
            // достоверна) — если запущенная версия уже не старше, обновления нет.
            // ВАЖНО: именно GetEntryAssembly — это .exe приложения с реальной версией (1.16.57).
            // Раньше здесь был GetExecutingAssembly, а он внутри NurMarketKassa.Infrastructure
            // возвращает САМУ Infrastructure, у которой версия в csproj не задана вовсе, то есть
            // 1.0.0.0. Сравнение «1.0.0.0 >= 1.16.56» всегда ложно — и защита от отката, ради
            // которой этот блок писался, не срабатывала НИ РАЗУ: касса 1.16.57 предлагала
            // «обновиться» до 1.16.56 (живой скриншот владельца 2026-09-22).
            var runningVersion = (System.Reflection.Assembly.GetEntryAssembly()
                ?? System.Reflection.Assembly.GetExecutingAssembly()).GetName().Version;
            if (runningVersion != null
                && System.Version.TryParse(latestVersion, out var latestParsed)
                && runningVersion >= latestParsed)
            {
                _pendingUpdate = null;
                return new AppUpdateCheckResult(true, false, currentVersion, null, null);
            }

            return new AppUpdateCheckResult(true, true, currentVersion, latestVersion, null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Velopack update check failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return new AppUpdateCheckResult(true, false, currentVersion, null, ex.Message);
        }
    }

    public async Task DownloadAsync(Action<int> onProgress, CancellationToken cancellationToken = default)
    {
        var manager = GetOrCreateManager()
            ?? throw new InvalidOperationException("Обновление не настроено (нет адреса манифеста).");
        if (!manager.IsInstalled)
            throw new InvalidOperationException(
                "Приложение запущено не из установленной копии — скачивать обновление некуда. " +
                "Установите кассу через установщик и повторите.");
        var update = _pendingUpdate
            ?? throw new InvalidOperationException("Сначала нужно проверить обновления — нечего скачивать.");

        await manager.DownloadUpdatesAsync(update, onProgress, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyUpdateAndRestart()
    {
        var manager = GetOrCreateManager()
            ?? throw new InvalidOperationException("Обновление не настроено (нет адреса манифеста).");
        var update = _pendingUpdate
            ?? throw new InvalidOperationException("Сначала нужно скачать обновление.");

        // Не возвращает управление — Velopack сам завершает процесс и запускает новую версию.
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease, restartArgs: []);
    }

    public async Task<IReadOnlyList<AppReleaseVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        var manager = GetOrCreateManager();
        if (manager is null || _source is null)
            return Array.Empty<AppReleaseVersion>();

        try
        {
            var feed = await _source.GetReleaseFeed(
                    new NullVelopackLogger(), manager.AppId, channel: null, stagingId: null, latestLocalRelease: null)
                .ConfigureAwait(false);

            _availableAssets = feed.Assets
                .Where(a => a.Type == VelopackAssetType.Full)
                .OrderByDescending(a => a.Version)
                .Take(MaxVersionsToList)
                .ToArray();
            var currentVersion = manager.CurrentVersion?.ToString();

            return _availableAssets
                .OrderByDescending(a => a.Version)
                .Select(a => new AppReleaseVersion(
                    a.Version.ToString(),
                    a.NotesMarkdown,
                    string.Equals(a.Version.ToString(), currentVersion, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Velopack version listing failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return Array.Empty<AppReleaseVersion>();
        }
    }

    public bool PrepareRollback(string version)
    {
        var asset = _availableAssets?.FirstOrDefault(
            a => string.Equals(a.Version.ToString(), version, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return false;

        // isDowngrade: true — тот же UpdateInfo, что ждут DownloadAsync/ApplyUpdateAndRestart,
        // только собранный вручную для выбранной прошлой версии, а не для "самой новой".
        _pendingUpdate = new UpdateInfo(asset, isDowngrade: true, deltaBaseRelease: null!, deltasToTarget: []);
        return true;
    }

    /// <summary>Текст релиза (то же, что видно на странице GitHub-релиза) для версии, на
    /// которую откатываются — Velopack не отдаёт его через фид пакетов
    /// (VelopackAsset.NotesMarkdown у GithubSource всегда пустой), поэтому запрашивается
    /// напрямую через GitHub REST API.</summary>
    public async Task<string?> GetReleaseNotesAsync(string version, CancellationToken cancellationToken = default)
    {
        var repo = ExtractGithubOwnerRepo(_settings.ManifestUrl);
        if (repo is null)
            return null;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NurMarketKassa-Kassa");
            var url = $"https://api.github.com/repos/{repo.Value.Owner}/{repo.Value.Repo}/releases/tags/v{version}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"GitHub release notes fetch failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return null;
        }
    }

    private UpdateManager? GetOrCreateManager()
    {
        if (_manager != null)
            return _manager;

        var repoUrl = ExtractGithubRepoUrl(_settings.ManifestUrl);
        if (repoUrl is null)
            return null;

        _source = new GithubSource(repoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(_source, options: new UpdateOptions { AllowVersionDowngrade = true }, locator: null!);
        return _manager;
    }

    /// <summary>Из адреса REST API-манифеста (https://api.github.com/repos/{owner}/{repo}/releases/latest,
    /// который уже настроен для проверки версии) достаёт обычный URL репозитория, который
    /// понимает Velopack.Sources.GithubSource (https://github.com/{owner}/{repo}) — отдельная
    /// настройка не нужна, это один и тот же репозиторий с релизами.</summary>
    private static string? ExtractGithubRepoUrl(string? manifestUrl)
    {
        var repo = ExtractGithubOwnerRepo(manifestUrl);
        return repo is null ? null : $"https://github.com/{repo.Value.Owner}/{repo.Value.Repo}";
    }

    private static (string Owner, string Repo)? ExtractGithubOwnerRepo(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return null;

        var match = GithubApiUrlPattern.Match(manifestUrl);
        return match.Success ? (match.Groups["owner"].Value, match.Groups["repo"].Value) : null;
    }
}
