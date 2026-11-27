using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28: вид магазина компании на сервере (market_sphere в GET/PATCH /api/users/company/,
/// BE-18). Раньше вид жил только в настройках каждой кассы и выбирался вручную на каждом ПК.
///
/// Правила:
/// • при входе серверный вид берётся только на новой установке или если на сервере его сменили с
///   тех пор, как касса видела его в прошлый раз (MarketSphereServerSeen, 2026-09-29) — выбор
///   клиента на уже настроенной кассе при запуске и обновлении не затирается;
/// • null с сервера («не задан») локальный выбор НЕ затирает;
/// • серверное значение применяется один раз на значение: если кассир потом сам переключил вид
///   (а сохранить на сервер не смог — это может только владелец), повторная загрузка компании в
///   этом же запуске его выбор не отменит; поменяет владелец вид на сайте — касса возьмёт новый;
/// • владелец меняет вид в настройках — PATCH на сервер (OperationsSettingsView), при отказе
///   прав остаётся только в этой кассе.
/// </summary>
public static class MarketSphereSync
{
    private static readonly object Sync = new();
    private static string? _companyId;
    private static string? _serverSphere;
    // 2026-11-26 (Д4, отчёт «Проверка сфер»): точная сфера, если сервер вернул её прямо в market_sphere (autoparts, bazaar, bazaar_retail, bazaar_wholesale).
    // _serverSphere при этом — базовая grocery (на ней живут эти сферы), чтобы сравнения в настройках и замок MarketSphereLock работали как прежде.
    private static string? _serverDetail;
    private static string? _appliedKey;

    /// <summary>Вид магазина на сервере из последнего ответа компании; null — не задан или ещё не загружен.</summary>
    public static string? ServerSphere
    {
        get { lock (Sync) return _serverSphere; }
    }

    /// <summary>2026-11-26 (Д4): точная сфера с сервера (autoparts / bazaar…) или null, если сервер вернул обычную базовую сферу или ничего.</summary>
    public static string? ServerDetail
    {
        get { lock (Sync) return _serverDetail; }
    }

    /// <summary>2026-11-26 (Д4): значения market_sphere, которые касса понимает как точную сферу на базе grocery (как в мобилке ShopModeX.fromDetail).</summary>
    internal static bool IsServerDetail(string? raw) =>
        raw is "autoparts" or BazaarProfile.Id or BazaarProfile.RetailDetail or BazaarProfile.WholesaleDetail;

    /// <summary>Запоминает market_sphere из ответа GET /api/users/company/ (вызывается из
    /// NurMarketApiClient.GetCompanyAsync). Неизвестное значение считается «не задан».</summary>
    public static void RememberFromCompanyJson(string? companyId, JsonElement root)
    {
        var data = root;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
            data = inner;

        string? sphere = null;
        string? detail = null;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("market_sphere", out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString()?.Trim().ToLowerInvariant();
            if (raw is MarketSpheres.Grocery or MarketSpheres.Clothing or MarketSpheres.Services)
                sphere = raw;
            else if (IsServerDetail(raw))
            {
                // 2026-11-26 (Д4): раньше такие значения считались «не задан», и на чистой установке касса спрашивала «Какой у вас магазин?».
                sphere = MarketSpheres.Grocery;
                detail = raw;
            }
        }

        lock (Sync)
        {
            _companyId = companyId;
            _serverSphere = sphere;
            _serverDetail = detail;
        }
    }

    /// <summary>Применяет вид с сервера к кассе (вызывается из CompanyInfoService.RefreshAsync после
    /// загрузки компании). Возвращает true, если вид кассы сменился.</summary>
    public static bool ApplyServerValueIfNew()
    {
        string sphere;
        lock (Sync)
        {
            if (_serverSphere is null)
                return false;
            // 2026-11-26 (Д4): для точной сферы серверное значение — она сама (autoparts…), а не базовая grocery.
            sphere = _serverDetail ?? _serverSphere;
            var key = (_companyId ?? "") + "|" + sphere;
            if (key == _appliedKey)
                return false;
            _appliedKey = key;
        }

        // 2026-09-29, правило владельца «при обновлениях не меняй установленные клиентом настройки»:
        // _appliedKey живёт только до закрытия программы, поэтому раньше серверный вид затирал выбор
        // клиента при КАЖДОМ запуске и после каждого обновления («clothing → grocery»). Теперь касса
        // помнит на диске, какой серверный вид уже видела (MarketSphereServerSeen), и берёт его только
        // на новой установке или когда владелец поменял вид на сайте после этого.
        var prefs = UserPreferences.Instance;
        var seen = prefs.MarketSphereServerSeen;
        if (seen != sphere)
        {
            prefs.MarketSphereServerSeen = sphere;
            prefs.SaveToDisk();
        }

        var firstSightOnConfiguredTill = seen is null && prefs.MarketSphereWasInFile;
        if (seen == sphere || firstSightOnConfiguredTill)
        {
            if (firstSightOnConfiguredTill && prefs.MarketSphere != sphere)
                PosLogger.Log($"Вид магазина кассы оставлен: {prefs.MarketSphere} (на сервере {sphere}) — выбор клиента не меняем.", "API");
            return false;
        }

        if (prefs.MarketSphere == sphere)
            return false;
        // 2026-11-26 (Д4): точная сфера уже выбрана в кассе — второй раз не применяем.
        if (IsServerDetail(sphere) && MarketSpheres.Profile.Id == SphereCatalog.TryGet(sphere)?.Id)
            return false;

        PosLogger.Log($"Вид магазина взят с сервера: {prefs.MarketSphere} → {sphere} ("
            + (seen is null ? "новая установка" : $"на сервере сменили {seen} → {sphere}") + ").", "API");
        MarketSpheres.Set(sphere);
        // 2026-11-26 (Д4): «Базар» розница / опт хранится в MarketSphereDetail исходным значением сервера (SphereSettings.GetBazaarMode читает bazaar_wholesale).
        if (sphere is BazaarProfile.RetailDetail or BazaarProfile.WholesaleDetail && prefs.MarketSphereDetail != sphere)
        {
            prefs.MarketSphereDetail = sphere;
            prefs.SaveToDisk();
            MarketSpheres.RaiseChanged();
        }
        return true;
    }

    /// <summary>Владелец сохранил вид на сервере из кассы — значение считается уже применённым.</summary>
    public static void NoteSavedOnServer(string sphere)
    {
        var normalized = MarketSpheres.Normalize(sphere);
        lock (Sync)
        {
            _serverSphere = normalized;
            _serverDetail = null; // 2026-11-26 (Д4): владелец записал базовую сферу — точной с сервера больше нет
            _appliedKey = (_companyId ?? "") + "|" + normalized;
        }

        UserPreferences.Instance.MarketSphereServerSeen = normalized;
        UserPreferences.Instance.SaveToDisk();
    }
}
