using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (просьба владельца «Маркетплейс с сайта: купил — темы скачиваются в программу»; ТЗ Умару — docs\ТЗ Умару — темы и виды кассы из Маркетплейса (06.11.2026).md).
/// Каталог платных/бесплатных тем и видов кассы с сервера и скачивание пакета. Работает ТОЛЬКО при признаке сервера <c>marketplace_themes</c>
/// (api/main/capabilities/): без него (и при 404) каталог пуст, окно «Маркетплейс» показывает только свои темы, как раньше.
/// • Каталог: GET api/main/marketplace/themes/ → список {id, code «theme-&lt;slug&gt;», name{5 языков}, description, preview_url, version, price, entitled, package_url, sha256, size, min_app_version, kind}.
///   Кэш на диске (marketplace-themes.json, по компании): нет связи — показываем кэш. Не чаще раза в 20 секунд при ручной проверке, не чаще раза в 5 минут без неё.
/// • Пакет: GET …/themes/{id}/package/ — только при entitled (иначе сервер 403 not_purchased → «Купить на сайте»). Проверка размера (≤ 5 МБ), sha256 и версии программы (min_app_version);
///   разбор — <see cref="MarketplaceThemePackage"/> (только данные). Токен уходит ТОЛЬКО на сервер NurCRM (адрес пакета с другого хоста не используется).</summary>
public sealed class MarketplaceThemesClient
{
    /// <summary>Предел размера пакета (по ТЗ — 5 МБ).</summary>
    public const long MaxPackageBytes = 5 * 1024 * 1024;

    public const string CatalogPath = "api/main/marketplace/themes/";

    public enum PackageStatus { Ok, NotPurchased, NotFound, TooLarge, SizeMismatch, HashMismatch, NeedsNewerApp, NoHash, NoConnection, NotSignedIn, Failed }

    public sealed record PackageResult(PackageStatus Status, byte[]? Bytes = null, string? Sha256 = null);

    private readonly Func<NurMarketApiClient?> _api;
    private readonly Func<string> _account;
    private readonly Func<CancellationToken, NurMarketApiClient, Task<bool>> _supports;
    private readonly Func<bool?> _supportsCached;
    private readonly Func<DateTime> _now;
    private readonly string? _dirOverride;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<MarketplaceThemeEntry> _entries = new();
    private string _entriesAccount = "";
    private DateTime _loadedAtUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _diskLoaded;
    private bool _supported;

    /// <summary>Общий клиент программы: тот же NurMarketApiClient, что у бонусов (ServerLoyalty.ApiProvider), признаки — ServerCapabilities.</summary>
    public static MarketplaceThemesClient Shared { get; } = new();

    public MarketplaceThemesClient(Func<NurMarketApiClient?>? api = null, Func<string>? account = null,
        Func<CancellationToken, NurMarketApiClient, Task<bool>>? supports = null, Func<bool?>? supportsCached = null,
        Func<DateTime>? nowUtc = null, string? dirOverride = null)
    {
        _api = api ?? (() => ServerLoyalty.ApiProvider?.Invoke() is { } a && !string.IsNullOrEmpty(a.AccessToken) ? a : null);
        _account = account ?? (() => UserPreferences.Instance.LastDataAccountKey ?? "");
        _supports = supports ?? ((ct, a) => ServerCapabilities.HasAsync(a, ServerCapabilities.MarketplaceThemes, ct));
        _supportsCached = supportsCached ?? (() => ServerCapabilities.Cached(ServerCapabilities.MarketplaceThemes));
        _now = nowUtc ?? (() => DateTime.UtcNow);
        _dirOverride = dirOverride;
    }

    private string CachePath => Path.Combine(_dirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), "marketplace-themes.json");

    /// <summary>Список для показа: пусто, если сервер не умеет (признак marketplace_themes известен как «нет») или каталога ещё нет.</summary>
    public IReadOnlyList<MarketplaceThemeEntry> Entries
    {
        get
        {
            EnsureDiskLoaded();
            lock (_lock)
            {
                if (_entries.Count == 0 || !string.Equals(_entriesAccount, _account(), StringComparison.OrdinalIgnoreCase))
                    return Array.Empty<MarketplaceThemeEntry>();
                // Признак известен и он «нет» — сервер больше не умеет; неизвестен (нет списка) — показываем кэш каталога (офлайн).
                if (_supportsCached() == false)
                    return Array.Empty<MarketplaceThemeEntry>();
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Сервер умеет темы с сайта: каталог получен в этом сеансе или лежит в кэше (признак не опровергнут).</summary>
    public bool IsAvailable => Entries.Count > 0 || (_supported && _supportsCached() != false);

    public DateTime LoadedAtUtc => _loadedAtUtc;

    /// <summary>Каталог изменился (получен новый список). Приходит из любого потока.</summary>
    public event Action? Changed;

    public MarketplaceThemeEntry? Find(string code)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    /// <summary>Забыть всё в памяти (смена компании; файл кэша уносит AccountDataIsolation).</summary>
    public void ResetMemory()
    {
        lock (_lock)
        {
            _entries = new();
            _entriesAccount = "";
            _loadedAtUtc = DateTime.MinValue;
            _lastAttemptUtc = DateTime.MinValue;
            _diskLoaded = true;
            _supported = false;
        }
    }

    /// <summary>Только для стенда: заново прочитать файл (как после перезапуска).</summary>
    public void ReloadFromDiskForStand()
    {
        lock (_lock)
        {
            _entries = new();
            _entriesAccount = "";
            _diskLoaded = false;
            _lastAttemptUtc = DateTime.MinValue;
            _supported = false;
        }
    }

    // ───────────────────────────── каталог ─────────────────────────────

    /// <param name="force">Ручная проверка («Обновить покупки», открытие окна): не чаще раза в 20 секунд. Без неё — не чаще раза в 5 минут.</param>
    public async Task<MarketplaceCatalogResult> RefreshAsync(bool force, CancellationToken ct = default)
    {
        var api = _api();
        var account = _account();
        if (api is null || string.IsNullOrWhiteSpace(account))
            return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Offline, Entries.Count);
        var minGap = force ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(5);
        if (_now() - _lastAttemptUtc < minGap)
            return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Throttled, Entries.Count);
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Throttled, Entries.Count);
        try
        {
            if (_now() - _lastAttemptUtc < minGap)
                return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Throttled, Entries.Count);
            _lastAttemptUtc = _now();
            EnsureDiskLoaded();

            bool supported;
            try
            {
                supported = await _supports(ct, api).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                supported = _supportsCached() == true; // признак не удалось спросить — держимся за последний известный
            }
            if (!supported)
            {
                lock (_lock)
                    _supported = false;
                return new MarketplaceCatalogResult(MarketplaceCatalogStatus.NotSupported);
            }

            JsonElement data;
            try
            {
                data = await api.RequestAsync(HttpMethod.Get, CatalogPath, null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405 or 501)
            {
                lock (_lock)
                    _supported = false;
                return new MarketplaceCatalogResult(MarketplaceCatalogStatus.NotSupported);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Темы с сайта: каталог не загружен ({(ex is ApiException a ? "HTTP " + a.StatusCode : ex.GetType().Name)}) — показан кэш.", "WARNING");
                return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Offline, Entries.Count);
            }

            var list = new List<MarketplaceThemeEntry>();
            foreach (var el in NurMarketApiClient.UnwrapList(data))
            {
                if (list.Count >= 200)
                    break;
                if (MarketplaceThemeEntry.Parse(el) is { } entry && list.All(x => !string.Equals(x.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
                    list.Add(entry);
            }

            List<string> newly;
            lock (_lock)
            {
                var before = _entriesAccount.Equals(account, StringComparison.OrdinalIgnoreCase)
                    ? _entries.Where(e => e.Entitled).Select(e => e.Code).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // «Новые» — купленные, которых не было среди доступных в прошлом каталоге (бесплатные, видимые с самого начала, сюда не попадают).
                newly = !_wasLoadedBefore
                    ? new List<string>()
                    : list.Where(e => e.Entitled && !e.IsFree && !before.Contains(e.Code)).Select(e => e.Code).ToList();
                _entries = list;
                _entriesAccount = account;
                _loadedAtUtc = _now();
                _supported = true;
                _wasLoadedBefore = true;
            }
            SaveToDisk(account, list);
            try { Changed?.Invoke(); }
            catch (Exception ex) { PosLogger.Log($"Темы с сайта: обработчик изменения упал ({ex.Message}).", "WARNING"); }
            return new MarketplaceCatalogResult(MarketplaceCatalogStatus.Loaded, list.Count, newly);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool _wasLoadedBefore;

    // ───────────────────────────── пакет ─────────────────────────────

    /// <summary>Скачивает и проверяет пакет: право (entitled → иначе 403), размер ≤ 5 МБ (и совпадает с заявленным), sha256 (обязателен), версия программы. Установка — отдельно.</summary>
    public async Task<PackageResult> DownloadPackageAsync(MarketplaceThemeEntry entry, CancellationToken ct = default)
    {
        var api = _api();
        if (api is null)
            return new PackageResult(PackageStatus.NotSignedIn);
        if (!MeetsMinAppVersion(entry.MinAppVersion))
            return new PackageResult(PackageStatus.NeedsNewerApp);
        if (string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Length != 64)
            return new PackageResult(PackageStatus.NoHash); // пакет без контрольной суммы не ставим: подмену не отличить от порчи
        if (entry.Size > MaxPackageBytes)
            return new PackageResult(PackageStatus.TooLarge);

        var (status, body, tooLarge) = await api.DownloadLimitedAsync(PackagePath(entry, api), MaxPackageBytes, ct).ConfigureAwait(false);
        if (tooLarge)
            return new PackageResult(PackageStatus.TooLarge);
        switch (status)
        {
            case 0:
                return new PackageResult(PackageStatus.NoConnection);
            case 401:
                return new PackageResult(PackageStatus.NotSignedIn);
            case 402:
            case 403:
                return new PackageResult(PackageStatus.NotPurchased);
            case 404:
            case 410:
                return new PackageResult(PackageStatus.NotFound);
        }
        if (status is < 200 or >= 300 || body is null)
        {
            PosLogger.Log($"Темы с сайта: пакет «{entry.Code}» не получен (HTTP {status}).", "WARNING");
            return new PackageResult(PackageStatus.Failed);
        }
        if (entry.Size > 0 && body.LongLength != entry.Size)
            return new PackageResult(PackageStatus.SizeMismatch);
        var actual = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            PosLogger.Log($"Темы с сайта: контрольная сумма пакета «{entry.Code}» не совпала — отказ.", "WARNING");
            return new PackageResult(PackageStatus.HashMismatch);
        }
        return new PackageResult(PackageStatus.Ok, body, actual);
    }

    /// <summary>Адрес пакета: свой стандартный; package_url берётся только относительный или с того же хоста, что сервер NurCRM (токен на чужой адрес не уходит).</summary>
    private static string PackagePath(MarketplaceThemeEntry entry, NurMarketApiClient api)
    {
        var fallback = $"api/main/marketplace/themes/{entry.Id}/package/";
        var url = entry.PackageUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return fallback;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            var baseUri = api.BaseAddress;
            if (!string.Equals(abs.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || abs.Port != baseUri.Port || abs.Scheme != baseUri.Scheme)
                return fallback;
            return abs.AbsolutePath.TrimStart('/');
        }
        var rel = url.TrimStart('/');
        return rel.StartsWith("api/", StringComparison.OrdinalIgnoreCase) && !rel.Contains("..", StringComparison.Ordinal) && !rel.Contains('?') ? rel : fallback;
    }

    private static readonly Version AppVersion = typeof(MarketplaceThemesClient).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Версия программы не ниже требуемой (min_app_version); пусто/непонятно — подходит.</summary>
    public static bool MeetsMinAppVersion(string? minVersion, Version? app = null)
    {
        if (string.IsNullOrWhiteSpace(minVersion) || !Version.TryParse(minVersion.Trim(), out var min))
            return true;
        var current = app ?? AppVersion;
        // Сравниваем три числа (мажор.минор.сборка): четвёртое в версиях программы не ведётся.
        return new Version(current.Major, current.Minor, Math.Max(current.Build, 0)) >= new Version(min.Major, min.Minor, Math.Max(min.Build, 0));
    }

    // ───────────────────────────── кэш на диске ─────────────────────────────

    private void EnsureDiskLoaded()
    {
        lock (_lock)
        {
            if (_diskLoaded)
                return;
            _diskLoaded = true;
            try
            {
                var text = AtomicFile.ReadAllText(CachePath);
                if (string.IsNullOrWhiteSpace(text))
                    return;
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var account = root.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return;
                var list = new List<MarketplaceThemeEntry>();
                foreach (var el in items.EnumerateArray())
                    if (list.Count < 200 && MarketplaceThemeEntry.Parse(el) is { } e)
                        list.Add(e);
                _entries = list;
                _entriesAccount = account;
                _wasLoadedBefore = list.Count > 0;
                if (root.TryGetProperty("loadedAtUtc", out var l) && l.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    _loadedAtUtc = t;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Темы с сайта: кэш каталога не прочитан ({ex.Message}).", "WARNING");
            }
        }
    }

    private void SaveToDisk(string account, List<MarketplaceThemeEntry> list)
    {
        try
        {
            // Пишем в том же формате, который читает Parse (имена полей сервера), чтобы кэш и ответ разбирались одним кодом.
            var items = list.Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id, ["code"] = e.Code, ["kind"] = e.Kind,
                ["name"] = e.Name.Values, ["description"] = e.Description.Values,
                ["preview_url"] = e.PreviewUrl, ["version"] = e.Version, ["price"] = e.Price, ["currency"] = e.Currency,
                ["entitled"] = e.Entitled, ["package_url"] = e.PackageUrl, ["sha256"] = e.Sha256, ["size"] = e.Size,
                ["min_app_version"] = e.MinAppVersion, ["web_url"] = e.WebUrl, ["accent"] = e.Accent, ["until"] = e.Until,
            }).ToList();
            var json = JsonSerializer.Serialize(new { v = 1, account, loadedAtUtc = _now().ToString("o", CultureInfo.InvariantCulture), items });
            AtomicFile.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Темы с сайта: кэш каталога не записан ({ex.Message}).", "WARNING");
        }
    }
}
