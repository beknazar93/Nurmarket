using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, пакет К-П1 (ТЗ «Мобилка и касса», путь A): настройки «печатать чеки телефона на этой кассе».
/// Печатает только касса, которой это назначено: два условия сразу —
///  1) локально включено на этом ПК (файл phone-print-settings.json в папке data: «enabled», список сотрудников «staff»);
///  2) общая настройка компании — ключ <see cref="CloudKey"/> в api/main/app-settings/nurmarket-kassa/ вида
///     {"&lt;id кассы&gt;": "&lt;id этого ПК&gt;"} (как DebtPaymentSettings: свои GET/PATCH, меняют владелец и администратор,
///     читают все). Вторая касса с той же кассой (cashbox) видит чужой id и не печатает.
/// По умолчанию выключено. Пока нет окна настроек (пакет К-П3), включение — Ctrl+Shift+F9 на кассе
/// (см. MainWindow.PhoneLink.cs) или правка файла вручную.
/// Файл лежит в папке data — она переезжает при смене компании (AccountDataIsolation), плюс сверка companyId.</summary>
public sealed class PhonePrintSettings
{
    public const string CloudKey = "PhonePrintRouting";
    private const string CloudPath = "api/main/app-settings/nurmarket-kassa/";
    private static readonly TimeSpan RoutingTtl = TimeSpan.FromMinutes(2);
    /// <summary>2026-10-27, ревью К-П1 (В-2): сколько можно доверять последнему прочитанному назначению при сбое чтения.</summary>
    private static readonly TimeSpan RoutingMaxAge = TimeSpan.FromMinutes(10);

    public enum SaveOutcome { Saved, Forbidden, Offline, Failed }

    /// <summary>Что хранится в локальном файле.</summary>
    public sealed class State
    {
        public bool Enabled { get; set; }
        /// <summary>Когда включили (метка «нулевого» опроса журнала: чеки, уже бывшие на сервере, не печатаются).</summary>
        public DateTime? EnabledAtUtc { get; set; }
        public string? CompanyId { get; set; }
        /// <summary>Необязательный список сотрудников (user_display), чьи продажи печатать. Пусто — все «чужие» чеки.</summary>
        public List<string> Staff { get; set; } = new();
        /// <summary>Последнее известное назначение с сервера {касса: ПК}; при обрыве связи берём его.</summary>
        public Dictionary<string, string> Routing { get; set; } = new();
        public DateTime? RoutingFetchedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _net = new(1, 1);
    private readonly string _path;
    private readonly Func<string?> _deviceId;
    private readonly Func<string?> _companyId;
    private readonly Func<NurMarketApiClient?> _api;
    private State? _state;

    public PhonePrintSettings(string path, Func<string?> deviceId, Func<string?> companyId, Func<NurMarketApiClient?> api)
    {
        _path = path;
        _deviceId = deviceId;
        _companyId = companyId;
        _api = api;
    }

    private static PhonePrintSettings? _default;

    /// <summary>Настройки этой кассы: свой файл, id ПК — тот же, что в обмене по сети магазина (LAN).</summary>
    public static PhonePrintSettings Default => _default ??= new PhonePrintSettings(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "data", "phone-print-settings.json"),
        () => LanSyncService.DeviceId,
        () => CompanyInfoService.LastCompany?.Id,
        () => SettingsCloudSync.ApiProvider?.Invoke());

    /// <summary>Изменились настройки (включили/выключили, пришло назначение) — окна и значок обновляют состояние.</summary>
    public event Action? Changed;

    public string? DeviceId => _deviceId();

    private State Load()
    {
        if (_state != null)
            return _state;
        try
        {
            if (File.Exists(_path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: настройки не прочитаны ({ex.Message}) — по умолчанию выключено.", "WARNING");
        }
        _state ??= new State();
        _state.Staff ??= new List<string>();
        _state.Routing = new Dictionary<string, string>(_state.Routing ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        return _state;
    }

    private bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: настройки не записаны ({ex.Message}).", "WARNING");
            return false;
        }
    }

    /// <summary>Локально включено на этом ПК (для этой компании).</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                var s = Load();
                var company = _companyId();
                return s.Enabled && !string.IsNullOrWhiteSpace(company)
                       && string.Equals(s.CompanyId, company, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Метка включения: журнал сверяет с ней, делался ли «нулевой» опрос после включения.</summary>
    public string? EnabledToken
    {
        get
        {
            lock (_gate)
                return Load().EnabledAtUtc?.ToString("o");
        }
    }

    public IReadOnlyList<string> Staff
    {
        get
        {
            lock (_gate)
                return Load().Staff.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        }
    }

    /// <summary>Кассы (cashbox), чьи чеки назначено печатать этому ПК. Пусто — не печатаем (выключено, чужое назначение,
    /// назначения нет). Назначение берётся с сервера раз в 2 минуты, при обрыве связи — последнее известное.</summary>
    public HashSet<string> MyCashboxes()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Enabled)
            return result;
        var me = _deviceId();
        if (string.IsNullOrWhiteSpace(me))
            return result;
        lock (_gate)
        {
            var s = Load();
            // 2026-10-27, ревью К-П1 (В-2): назначение старше 10 минут (чтение настроек сбоит) — по старому не печатаем:
            // иначе после переназначения «старая» касса печатала бы бесконечно.
            if (s.RoutingFetchedUtc is not { } at || DateTime.UtcNow - at > RoutingMaxAge)
                return result;
            foreach (var (cashbox, device) in s.Routing)
                if (string.Equals(device, me, StringComparison.OrdinalIgnoreCase))
                    result.Add(cashbox);
        }
        return result;
    }

    /// <summary>Кто назначен на кассу (для строки состояния): id ПК или null.</summary>
    public string? AssignedTo(string? cashboxId)
    {
        if (string.IsNullOrWhiteSpace(cashboxId))
            return null;
        lock (_gate)
            return Load().Routing.TryGetValue(cashboxId.Trim(), out var d) ? d : null;
    }

    public bool RoutingKnown
    {
        get
        {
            lock (_gate)
                return Load().RoutingFetchedUtc != null;
        }
    }

    // ------------------------------------------------------------------ назначение (облако)

    private static Dictionary<string, string> ParseRouting(JsonElement data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(CloudKey, out var node)
            && node.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in node.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    map[p.Name.Trim()] = p.Value.GetString()!.Trim();
        }
        return map;
    }

    /// <summary>Перечитать назначение с сервера (не чаще раза в 2 минуты, force — сразу). Ошибки — молча (false): остаётся
    /// последнее известное. Ответ без поля settings — сбой, а не «пусто».</summary>
    public async Task<bool> RefreshRoutingAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var s = Load();
            if (!force && s.RoutingFetchedUtc is { } at && DateTime.UtcNow - at < RoutingTtl)
                return true;
        }
        var api = _api();
        var company = _companyId();
        if (api is null || string.IsNullOrEmpty(api.AccessToken) || string.IsNullOrWhiteSpace(company) || ServerOutageMonitor.IsOutage)
            return false;

        await _net.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, CloudPath, null, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings)
                || settings.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return false;
            if (!string.Equals(_companyId(), company, StringComparison.Ordinal))
                return false;
            var remote = ParseRouting(data);
            bool changed;
            lock (_gate)
            {
                var s = Load();
                changed = s.Routing.Count != remote.Count || remote.Any(kv => !s.Routing.TryGetValue(kv.Key, out var v) || v != kv.Value);
                s.Routing = remote;
                s.RoutingFetchedUtc = DateTime.UtcNow;
                Save();
            }
            if (changed)
                RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _net.Release();
        }
    }

    /// <summary>Назначить (on) или снять (off) ЭТОТ ПК печатающим для кассы <paramref name="cashboxId"/>. Сначала свежее чтение
    /// с сервера, потом запись целиком (PATCH заменяет значение ключа). Кассир получает 403 → Forbidden, назначение не меняется.</summary>
    public async Task<(SaveOutcome Outcome, string? Error)> SetAssignmentAsync(string cashboxId, bool on, CancellationToken ct = default)
    {
        var me = _deviceId();
        var api = _api();
        var company = _companyId();
        if (string.IsNullOrWhiteSpace(cashboxId) || string.IsNullOrWhiteSpace(me))
            return (SaveOutcome.Failed, null);
        if (api is null || string.IsNullOrEmpty(api.AccessToken) || string.IsNullOrWhiteSpace(company) || ServerOutageMonitor.IsOutage)
            return (SaveOutcome.Offline, null);
        await _net.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, CloudPath, null, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings)
                || settings.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return (SaveOutcome.Offline, null);
            var map = ParseRouting(data);
            var key = cashboxId.Trim();
            if (on)
                map[key] = me!;
            else if (map.TryGetValue(key, out var current) && string.Equals(current, me, StringComparison.OrdinalIgnoreCase))
                map.Remove(key);
            else
                return (SaveOutcome.Saved, null); // и так не мы — снимать нечего

            var body = new JsonObject { [CloudKey] = JsonSerializer.SerializeToNode(map) };
            await api.RequestAsync(new HttpMethod("PATCH"), CloudPath, body, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            lock (_gate)
            {
                var s = Load();
                s.Routing = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                s.RoutingFetchedUtc = DateTime.UtcNow;
                Save();
            }
            RaiseChanged();
            return (SaveOutcome.Saved, null);
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            PosLogger.Log("Чеки с телефона: назначение печатающей кассы — нет прав на запись (меняют владелец и администратор).", "INFO");
            return (SaveOutcome.Forbidden, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: назначение не сохранено ({ex.Message}).", "WARNING");
            return (SaveOutcome.Failed, ex.Message);
        }
        finally
        {
            _net.Release();
        }
    }

    // ------------------------------------------------------------------ локальное включение

    /// <summary>Включить/выключить на этом ПК. Включение запоминает время (журнал сделает «нулевой» опрос: чеки, уже бывшие на
    /// сервере, не печатаются). Повторное «включить» при уже включённом время не сбрасывает.</summary>
    public bool SetEnabled(bool on)
    {
        lock (_gate)
        {
            var s = Load();
            var company = _companyId();
            if (on)
            {
                if (string.IsNullOrWhiteSpace(company))
                    return false;
                if (!(s.Enabled && string.Equals(s.CompanyId, company, StringComparison.Ordinal)))
                {
                    s.Enabled = true;
                    s.EnabledAtUtc = DateTime.UtcNow;
                    s.CompanyId = company;
                }
            }
            else
            {
                s.Enabled = false;
            }
            var ok = Save();
            if (ok)
                RaiseChangedLater();
            return ok;
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Чеки с телефона: подписчик не обработал изменение ({ex.Message}).", "WARNING"); }
    }

    private void RaiseChangedLater() => _ = Task.Run(RaiseChanged);
}
