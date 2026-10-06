using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, владелец: «при открытии на другом устройстве все установки слетают!!! сделай так, чтобы при открытии на другом
/// устройстве все настройки аккаунта подтягивались». Сервер добавил по ТЗ ч.13 (05.10, 23:41):
/// • личные настройки пользователя — GET/PATCH api/users/app-settings/{app}/;
/// • общие настройки компании — GET/PATCH api/main/app-settings/{app}/ (меняют владелец и администратор, читают все).
/// {app} — nurmarket-kassa или nurmarket-owner. После входа настройки берутся с сервера (сервер главнее); если на сервере ещё
/// пусто — туда уходят настройки этого компьютера (первое устройство заполняет). Каждое сохранение настроек отправляется на
/// сервер через 3 с (только то, что изменилось). Переносится только то, что не зависит от компьютера: порты весов и принтеров,
/// IP весов, масштаб экрана, полноэкранный режим, фильтры поиска остаются на каждом компьютере свои.
/// </summary>
public static class SettingsCloudSync
{
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    /// <summary>Личное: язык, тема, вид каталога, привычки кассира, скрытые разделы, ключи ИИ.</summary>
    private static readonly string[] PersonalKeys =
    {
        "Language", "DarkTheme", "AccentTheme", "MainLayoutMode", "CatalogViewMode", "ShowCatalogPhotos", "CatalogTileScalePercent",
        "SingleClickToCart", "ResetManualAddQtyAfterAdd", "DiscountModePercent", "ShowQuickProducts", "QuickCheckoutEnabled",
        "AutoShowTouchKeyboard", "HiddenSections", "OwnerSidebarCollapsed",
    };

    /// <summary>Ключи ИИ — в личных настройках, но в отдельном поле "secrets" (бэкенд 05.10: «секреты передавать в secrets» —
    /// хранятся отдельно и отдаются только самому пользователю).</summary>
    private static readonly string[] SecretKeys = { "TelegramAiKey", "GroqApiKey", "OpenRouterApiKey" };
    private static Dictionary<string, string> _lastSecrets = new();

    /// <summary>Общее для магазина: вид чека, бонусы, допродажа, купленные функции, коды доступа, банки, телефон владельца.</summary>
    private static readonly string[] CompanyKeys =
    {
        "ShowStoreName", "ShowAddress", "ShowInn", "ShowReceiptNumber", "ShowDate", "ShowItems", "ShowTotal", "ShowQrCode",
        "StoreName", "StoreAddress", "StoreInn", "LoyaltyEnabled", "UpsellEnabled", "UnlockedPacks", "UnlockedThemeIds",
        "VoiceControlUnlocked", "LanguagePackUnlocked", "WarehouseAnalyticsUnlocked", "PriceTagEditorUnlocked", "LabelEditorUnlocked",
        "BulkPriceTagUnlocked", "AnalyticsExportUnlocked", "TelegramBotUnlocked", "ShiftAnalyticsUnlocked", "StaffTimesheetUnlocked",
        "ScalesUnlocked", "EmployeeAccessCodes", "DynamicPaymentQrEnabled", "VisibleBankNames", "CustomBankNames",
        "DebtPrepaymentChooseMethod", "OwnerPhone",
        // 2026-10-06: штрафы за просрочку проката и долга — одинаковые в чеках всех касс.
        "RentalLatePenaltyPerDay", "DebtLatePenaltyText",
        // 2026-10-06: срок обмена и категории без обмена (магазин одежды) — одинаковые на всех кассах.
        "ExchangeDaysLimit", "NonExchangeableCategories",
    };

    private static readonly object Sync = new();
    private static Dictionary<string, string> _lastPersonal = new();
    private static Dictionary<string, string> _lastCompany = new();
    private static bool _ready;
    private static bool _applying;
    private static bool _companyForbidden;
    private static CancellationTokenSource? _debounce;

    private static string AppName => AppMode.IsOwner ? "nurmarket-owner" : "nurmarket-kassa";

    private static NurMarketApiClient? Api => ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static PropertyInfo? Prop(string name) => typeof(UserPreferences).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    private static Dictionary<string, string> Snapshot(IEnumerable<string> keys)
    {
        var prefs = UserPreferences.Instance;
        var map = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            if (Prop(key) is not { CanRead: true } p)
                continue;
            try
            {
                map[key] = JsonSerializer.Serialize(p.GetValue(prefs), p.PropertyType);
            }
            catch
            {
                // значение не сериализуется — пропускаем
            }
        }
        return map;
    }

    private static string? _pulledFor;

    /// <summary>Один раз на вход в компанию (обновление компании бывает часто — повторно не тянем, чтобы не затереть
    /// только что сделанные и ещё не отправленные правки).</summary>
    public static Task PullOnceAsync(string? companyId)
    {
        var key = (companyId ?? "") + "|" + AppName;
        if (string.IsNullOrWhiteSpace(companyId) || _pulledFor == key)
            return Task.CompletedTask;
        _pulledFor = key;
        return PullAsync();
    }

    /// <summary>После входа: настройки с сервера — в эту программу; пусто на сервере — туда уходят настройки этого компьютера.</summary>
    public static async Task PullAsync()
    {
        if (Api is not { } api)
            return;
        try
        {
            var personal = await GetAsync(api, $"api/users/app-settings/{AppName}/").ConfigureAwait(false);
            JsonObject? company = null;
            try
            {
                company = await GetAsync(api, $"api/main/app-settings/{AppName}/").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Настройки с сервера: общие настройки компании не получены ({ex.Message}).", "WARNING");
            }
            var appliedKeys = new List<string>();
            lock (Sync)
            {
                _applying = true;
                try
                {
                    appliedKeys.AddRange(Apply(personal, PersonalKeys));
                    appliedKeys.AddRange(Apply(personal?["secrets"] as JsonObject, SecretKeys));
                    appliedKeys.AddRange(Apply(company, CompanyKeys));
                    if (appliedKeys.Count > 0)
                        UserPreferences.Instance.SaveToDisk();
                }
                finally
                {
                    _applying = false;
                }
            }
            // То, чего на сервере ещё нет (первое устройство или новые настройки), — на сервер.
            var localPersonal = Snapshot(PersonalKeys);
            var localCompany = Snapshot(CompanyKeys);
            var missingPersonal = localPersonal.Where(kv => personal is null || !personal.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var missingCompany = localCompany.Where(kv => company is null || !company.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            _lastPersonal = localPersonal.Where(kv => !missingPersonal.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            _lastCompany = localCompany.Where(kv => !missingCompany.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var serverSecrets = personal?["secrets"] as JsonObject;
            _lastSecrets = Snapshot(SecretKeys).Where(kv => serverSecrets is not null && serverSecrets.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            // 2026-10-05: ключи ИИ, отправленные раньше открытыми полями, — стереть (теперь они в "secrets").
            var plain = SecretKeys.Where(k => personal is not null && personal.ContainsKey(k)).ToList();
            if (plain.Count > 0)
            {
                var wipe = new JsonObject();
                foreach (var k in plain)
                    wipe[k] = null;
                await api.RequestAsync(new HttpMethod("PATCH"), $"api/users/app-settings/{AppName}/", wipe, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                PosLogger.Log($"Настройки на сервере: ключи ИИ перенесены в secrets ({plain.Count}).", "INFO");
            }
            _ready = true;
            PosLogger.Log($"Настройки с сервера ({AppName}): применено {appliedKeys.Count}{(appliedKeys.Count > 0 ? " (" + string.Join(", ", appliedKeys) + ")" : "")}, " +
                          $"отправляю недостающие — личные {missingPersonal.Count}, компании {missingCompany.Count}.", "INFO");
            await PushAsync().ConfigureAwait(false);
            if (appliedKeys.Count > 0)
            {
                // Меню (скрытые разделы) и купленные функции — пересобрать сразу, остальное — при следующем открытии окон.
                if (appliedKeys.Contains("HiddenSections"))
                    SectionVisibility.RaiseChanged();
                if (appliedKeys.Contains("UnlockedPacks"))
                    TariffGate.RaisePacksChanged();
                Applied?.Invoke();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки с сервера: не получены ({ex.Message}) — работают настройки этого компьютера.", "WARNING");
        }
    }

    /// <summary>Настройки пришли с сервера и применены (окна могут обновить вид).</summary>
    public static event Action? Applied;

    private static async Task<JsonObject?> GetAsync(NurMarketApiClient api, string path)
    {
        var data = await api.RequestAsync(HttpMethod.Get, path, null, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(s.GetRawText()) as JsonObject
            : null;
    }

    private static List<string> Apply(JsonObject? server, string[] keys)
    {
        var applied = new List<string>();
        if (server is null)
            return applied;
        var prefs = UserPreferences.Instance;
        foreach (var key in keys)
        {
            if (!server.TryGetPropertyValue(key, out var node) || node is null || Prop(key) is not { CanWrite: true } p)
                continue;
            try
            {
                var local = JsonSerializer.Serialize(p.GetValue(prefs), p.PropertyType);
                var remote = node.ToJsonString();
                // Сервер может отдать секрет скрытым («****») — такое значение не применяем.
                if (SecretKeys.Contains(key) && (remote.Contains('*') || remote.Contains('•')))
                    continue;
                // Сервер (JSONB) переставляет поля внутри объектов — сравниваем по смыслу, а не текстом.
                if (local == remote || JsonNode.DeepEquals(JsonNode.Parse(local), node))
                    continue;
                p.SetValue(prefs, JsonSerializer.Deserialize(remote, p.PropertyType));
                applied.Add(key);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Настройки с сервера: «{key}» не применена ({ex.Message}).", "WARNING");
            }
        }
        return applied;
    }

    /// <summary>Настройки сохранены на этом компьютере — через 3 с изменения уходят на сервер.</summary>
    public static void OnSaved()
    {
        if (!_ready || _applying)
            return;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                await PushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static async Task PushAsync()
    {
        if (Api is not { } api)
            return;
        var personal = Snapshot(PersonalKeys);
        var company = Snapshot(CompanyKeys);
        var personalChanged = personal.Where(kv => !_lastPersonal.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        var companyChanged = company.Where(kv => !_lastCompany.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        if (personalChanged.Count > 0 && await PatchAsync(api, $"api/users/app-settings/{AppName}/", personalChanged).ConfigureAwait(false))
            foreach (var (k, v) in personalChanged)
                _lastPersonal[k] = v;
        var secrets = Snapshot(SecretKeys);
        var secretsChanged = secrets.Where(kv => !_lastSecrets.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        if (secretsChanged.Count > 0)
        {
            var inner = new JsonObject();
            foreach (var (key, json) in secretsChanged)
                inner[key] = JsonNode.Parse(json);
            try
            {
                await api.RequestAsync(new HttpMethod("PATCH"), $"api/users/app-settings/{AppName}/", new JsonObject { ["secrets"] = inner }, null,
                    CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                foreach (var (k, v) in secretsChanged)
                    _lastSecrets[k] = v;
                PosLogger.Log($"Настройки на сервере: ключи ИИ (secrets) — {secretsChanged.Count}.", "INFO");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Настройки на сервере: ключи ИИ не отправлены ({ex.Message}).", "WARNING");
            }
        }
        if (companyChanged.Count > 0 && !_companyForbidden)
        {
            if (await PatchAsync(api, $"api/main/app-settings/{AppName}/", companyChanged).ConfigureAwait(false))
                foreach (var (k, v) in companyChanged)
                    _lastCompany[k] = v;
        }
    }

    private static async Task<bool> PatchAsync(NurMarketApiClient api, string path, List<KeyValuePair<string, string>> changed)
    {
        var settings = new JsonObject();
        foreach (var (key, json) in changed)
            settings[key] = JsonNode.Parse(json);
        try
        {
            // Тело — сами настройки (проверено 05.10: сервер сливает верхний уровень, null удаляет ключ).
            await api.RequestAsync(new HttpMethod("PATCH"), path, settings, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            PosLogger.Log($"Настройки на сервере: {path.Split('/')[1]} — {string.Join(", ", changed.Select(c => c.Key))}.", "INFO");
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            // Кассир: общие настройки компании меняют только владелец и администратор.
            _companyForbidden = path.Contains("/main/", StringComparison.Ordinal);
            PosLogger.Log($"Настройки на сервере: нет прав менять {path} — остаются на этом компьютере.", "INFO");
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки на сервере: не отправлены ({ex.Message}) — отправятся при следующем сохранении.", "WARNING");
            return false;
        }
    }
}
