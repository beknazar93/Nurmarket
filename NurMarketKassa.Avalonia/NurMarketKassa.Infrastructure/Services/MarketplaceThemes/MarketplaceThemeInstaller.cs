using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Куда установщик кладёт тему (в программе — «Редактор своих тем», CustomThemeStore; в тестах — память). Только данные.</summary>
public interface IThemeSink
{
    /// <summary>Сохранить тему; <paramref name="existingId"/> — заменить уже поставленную (id сохраняется, выбранная тема остаётся выбранной). Возвращает id.</summary>
    string Save(string? existingId, ThemePackageData data);

    bool Exists(string id);

    void Delete(string id);
}

/// <summary>Установленная с сайта тема (запись о том, что чему соответствует).</summary>
public sealed record InstalledServerTheme(string Code, string CustomId, string Version, string Sha256, DateTime InstalledAtUtc);

/// <summary>2026-11-06: запись «какие темы с сайта поставлены» (server-themes.json в папке данных компании). Сама тема лежит в своих темах (custom-themes.json) —
/// здесь только соответствие код → id своей темы и версия, чтобы показывать «Установлено/Обновить» и не плодить копии.</summary>
public sealed class ServerThemeRegistry
{
    private readonly string? _dirOverride;
    private readonly object _lock = new();
    private List<InstalledServerTheme>? _items;
    private HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);

    public static ServerThemeRegistry Shared { get; } = new();

    public ServerThemeRegistry(string? dirOverride = null) => _dirOverride = dirOverride;

    private string FilePath => Path.Combine(_dirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), "server-themes.json");

    public IReadOnlyList<InstalledServerTheme> All
    {
        get
        {
            lock (_lock)
                return Load().ToArray();
        }
    }

    public InstalledServerTheme? Get(string code)
    {
        lock (_lock)
            return Load().FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public InstalledServerTheme? FindByCustomId(string? customId)
    {
        if (string.IsNullOrEmpty(customId))
            return null;
        lock (_lock)
            return Load().FirstOrDefault(i => string.Equals(i.CustomId, customId, StringComparison.OrdinalIgnoreCase));
    }

    public void Set(InstalledServerTheme item)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(i => string.Equals(i.Code, item.Code, StringComparison.OrdinalIgnoreCase));
            list.Add(item);
            _dismissed.Remove(item.Code);
            Save(list);
        }
    }

    /// <summary>Убрать запись. Пользователь сам убрал тему — автоустановка купленных её больше не возвращает (до ручной установки).</summary>
    public void Remove(string code)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));
            _dismissed.Add(code);
            Save(list);
        }
    }

    /// <summary>Пользователь убирал эту тему с кассы — автоматически её не ставим.</summary>
    public bool IsDismissed(string code)
    {
        lock (_lock)
        {
            Load();
            return _dismissed.Contains(code);
        }
    }

    /// <summary>Забыть память (смена компании: файл уносит AccountDataIsolation).</summary>
    public void ResetMemory()
    {
        lock (_lock)
        {
            _items = new List<InstalledServerTheme>();
            _dismissed = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Только для стенда: заново прочитать файл.</summary>
    public void ReloadFromDiskForStand()
    {
        lock (_lock)
        {
            _items = null;
            _dismissed = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private List<InstalledServerTheme> Load()
    {
        if (_items is not null)
            return _items;
        var list = new List<InstalledServerTheme>();
        try
        {
            var text = AtomicFile.ReadAllText(FilePath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("dismissed", out var dis) && dis.ValueKind == JsonValueKind.Array)
                    foreach (var d in dis.EnumerateArray())
                        if (d.ValueKind == JsonValueKind.String && MarketplaceThemeEntry.IsSafeCode(d.GetString()) && _dismissed.Count < 200)
                            _dismissed.Add(d.GetString()!);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var el in items.EnumerateArray())
                    {
                        string S(string n) => el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        var code = S("code");
                        var customId = S("customId");
                        if (!MarketplaceThemeEntry.IsSafeCode(code) || customId.Length == 0 || list.Count >= 200)
                            continue;
                        DateTime.TryParse(S("installedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at);
                        list.Add(new InstalledServerTheme(code, customId, S("version"), S("sha256"), at));
                    }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Темы с сайта: запись об установленных не прочитана ({ex.Message}).", "WARNING");
        }
        _items = list;
        return list;
    }

    private void Save(List<InstalledServerTheme> list)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                v = 1,
                dismissed = _dismissed.ToList(),
                items = list.Select(i => new { code = i.Code, customId = i.CustomId, version = i.Version, sha256 = i.Sha256, installedAtUtc = i.InstalledAtUtc.ToString("o", CultureInfo.InvariantCulture) }).ToList(),
            });
            AtomicFile.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Темы с сайта: запись об установленных не сохранена ({ex.Message}).", "WARNING");
        }
    }
}

public enum ThemeInstallStatus
{
    Installed, Updated, NotPurchased, NotFound, Unsupported, NeedsNewerApp, TooLarge, SizeMismatch, HashMismatch, NoHash, BadPackage, NoConnection, NotSignedIn, Failed,
}

public sealed record ThemeInstallResult(ThemeInstallStatus Status, string? CustomId = null, string? Detail = null)
{
    public bool Ok => Status is ThemeInstallStatus.Installed or ThemeInstallStatus.Updated;
}

public enum ThemeInstallState { NotInstalled, Installed, UpdateAvailable }

/// <summary>2026-11-06: установка тем с сайта: скачать (MarketplaceThemesClient), проверить, разобрать как данные (MarketplaceThemePackage) и положить в «свои темы» (IThemeSink).
/// Ничего не исполняется. Видов кроме «theme» (view, labels, scale-profile) эта версия программы не ставит: Unsupported — карточка пишет «нужна новая версия».</summary>
public sealed class MarketplaceThemeInstaller
{
    private readonly MarketplaceThemesClient _client;
    private readonly ServerThemeRegistry _registry;
    private readonly IThemeSink _sink;

    public MarketplaceThemeInstaller(MarketplaceThemesClient client, ServerThemeRegistry registry, IThemeSink sink)
    {
        _client = client;
        _registry = registry;
        _sink = sink;
    }

    /// <summary>Эта версия программы умеет ставить такой вид пакета.</summary>
    public static bool CanInstall(MarketplaceThemeEntry entry) => entry.IsTheme;

    public ThemeInstallState StateOf(MarketplaceThemeEntry entry)
    {
        var installed = _registry.Get(entry.Code);
        if (installed is null || !_sink.Exists(installed.CustomId))
            return ThemeInstallState.NotInstalled; // запись есть, а темы нет (удалена в «Редакторе тем») — можно поставить заново
        return !string.Equals(installed.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(installed.Version, entry.Version, StringComparison.Ordinal)
            ? ThemeInstallState.UpdateAvailable
            : ThemeInstallState.Installed;
    }

    public string? CustomIdOf(string code) => _registry.Get(code) is { } i && _sink.Exists(i.CustomId) ? i.CustomId : null;

    public async Task<ThemeInstallResult> InstallAsync(MarketplaceThemeEntry entry, CancellationToken ct = default)
    {
        if (!CanInstall(entry))
            return new ThemeInstallResult(ThemeInstallStatus.Unsupported);
        var pack = await _client.DownloadPackageAsync(entry, ct).ConfigureAwait(false);
        switch (pack.Status)
        {
            case MarketplaceThemesClient.PackageStatus.Ok:
                break;
            case MarketplaceThemesClient.PackageStatus.NotPurchased: return new ThemeInstallResult(ThemeInstallStatus.NotPurchased);
            case MarketplaceThemesClient.PackageStatus.NotFound: return new ThemeInstallResult(ThemeInstallStatus.NotFound);
            case MarketplaceThemesClient.PackageStatus.TooLarge: return new ThemeInstallResult(ThemeInstallStatus.TooLarge);
            case MarketplaceThemesClient.PackageStatus.SizeMismatch: return new ThemeInstallResult(ThemeInstallStatus.SizeMismatch);
            case MarketplaceThemesClient.PackageStatus.HashMismatch: return new ThemeInstallResult(ThemeInstallStatus.HashMismatch);
            case MarketplaceThemesClient.PackageStatus.NoHash: return new ThemeInstallResult(ThemeInstallStatus.NoHash);
            case MarketplaceThemesClient.PackageStatus.NeedsNewerApp: return new ThemeInstallResult(ThemeInstallStatus.NeedsNewerApp);
            case MarketplaceThemesClient.PackageStatus.NoConnection: return new ThemeInstallResult(ThemeInstallStatus.NoConnection);
            case MarketplaceThemesClient.PackageStatus.NotSignedIn: return new ThemeInstallResult(ThemeInstallStatus.NotSignedIn);
            default: return new ThemeInstallResult(ThemeInstallStatus.Failed);
        }

        ThemePackageData data;
        try
        {
            data = MarketplaceThemePackage.Parse(pack.Bytes!);
        }
        catch (InvalidDataException ex)
        {
            PosLogger.Log($"Темы с сайта: пакет «{entry.Code}» не принят: {ex.Message}", "WARNING");
            return new ThemeInstallResult(ThemeInstallStatus.BadPackage, null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(data.Name))
            data.Name = entry.Name.Get();
        var previous = _registry.Get(entry.Code);
        var existingId = previous is not null && _sink.Exists(previous.CustomId) ? previous.CustomId : null;
        string id;
        try
        {
            id = _sink.Save(existingId, data);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Темы с сайта: тема «{entry.Code}» не сохранена ({ex.Message}).", "WARNING");
            return new ThemeInstallResult(ThemeInstallStatus.Failed, null, ex.Message);
        }
        _registry.Set(new InstalledServerTheme(entry.Code, id, entry.Version, pack.Sha256 ?? entry.Sha256 ?? "", DateTime.UtcNow));
        PosLogger.Log($"Темы с сайта: «{entry.Code}» версия {entry.Version} {(existingId is null ? "установлена" : "обновлена")}.", "INFO");
        return new ThemeInstallResult(existingId is null ? ThemeInstallStatus.Installed : ThemeInstallStatus.Updated, id);
    }

    /// <summary>Убрать тему с кассы (покупка на сайте остаётся — её можно поставить снова).</summary>
    public bool Remove(string code)
    {
        var installed = _registry.Get(code);
        if (installed is null)
            return false;
        if (_sink.Exists(installed.CustomId))
            _sink.Delete(installed.CustomId);
        _registry.Remove(code);
        return true;
    }

    /// <summary>Ставит все купленные темы (entitled, вид «theme»), которых на кассе ещё нет. Не более <paramref name="max"/> за раз. Обновления версий сама не ставит — кнопка «Обновить» на карточке.</summary>
    public async Task<IReadOnlyList<(MarketplaceThemeEntry Entry, ThemeInstallResult Result)>> AutoInstallEntitledAsync(int max = 10, CancellationToken ct = default)
    {
        var done = new List<(MarketplaceThemeEntry, ThemeInstallResult)>();
        foreach (var entry in _client.Entries)
        {
            if (done.Count >= max || ct.IsCancellationRequested)
                break;
            // Тему убрали (кнопкой «Удалить» или в «Редакторе тем») — купленное не возвращаем само, только по кнопке «Установить».
            if (_registry.IsDismissed(entry.Code) || _registry.Get(entry.Code) is not null)
                continue;
            // Бесплатные темы сами не ставим: пользователь выбирает их в каталоге; ставим только то, что куплено на сайте.
            if (!entry.Entitled || entry.IsFree || !CanInstall(entry) || StateOf(entry) != ThemeInstallState.NotInstalled)
                continue;
            done.Add((entry, await InstallAsync(entry, ct).ConfigureAwait(false)));
        }
        return done;
    }
}
