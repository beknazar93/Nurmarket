using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-09, жалоба клиента (программа владельца: «Недостаточно прав… доступ „Склад“», «все права выданы, перезапуск не помогает»).
/// Права раздела касса решает САМА по профилю пользователя (<see cref="IAuthApiService.UserPayload"/>), сервер при этом не спрашивается.
/// Профиль читался только при входе/автовходе. Если сервер при запуске не ответил за 3 с (медленный интернет), касса входила по сохранённому
/// входу (auth.dat) — с правами, какие были при последнем удачном входе, — и проверка «сервер снова жив» (GET профиля) ответ выбрасывала:
/// выданный на сайте «Склад» не появлялся ни после возврата связи, ни после перезапуска (снова 3 с — снова сохранённые права).
/// Теперь: профиль перечитывается после возврата связи и перед отказом «Недостаточно прав»; в окне отказа — строка для поддержки
/// (какое право, роль, что прислал сервер, откуда взяты права).</summary>
public static class PermissionRecheck
{
    private static readonly object Gate = new();
    private static Task<bool>? _inFlight;
    private static DateTimeOffset? _liveProfileAt;
    private static bool _fromSavedSession;
    private static string? _lastFailure;
    private static Func<IAuthApiService?>? _apiProvider;
    private static Func<IAutonomousAuthService?>? _autonomousProvider;
    private static bool _attached;

    /// <summary>Когда права последний раз взяты из ответа сервера (GET users/profile/).</summary>
    public static DateTimeOffset? LiveProfileAt { get { lock (Gate) return _liveProfileAt; } }

    /// <summary>Права сейчас из сохранённого входа (сервер при входе не ответил), а не из профиля сервера.</summary>
    public static bool FromSavedSession { get { lock (Gate) return _fromSavedSession; } }

    /// <summary>Профиль только что применён из ответа сервера (вход, автовход, перепроверка).</summary>
    public static void MarkLiveProfile()
    {
        lock (Gate)
        {
            _liveProfileAt = DateTimeOffset.Now;
            _fromSavedSession = false;
            _lastFailure = null;
        }
    }

    /// <summary>Пользователь восстановлен из сохранённого входа (auth.dat) — права могут быть устаревшими.</summary>
    public static void MarkSavedSession()
    {
        lock (Gate)
            _fromSavedSession = true;
    }

    /// <summary>Подписка на возврат связи (ServerOutageMonitor.Recovered): перечитать профиль. Вызывается один раз при запуске приложения.</summary>
    public static void Attach(Func<IAuthApiService?> apiProvider, Func<IAutonomousAuthService?> autonomousProvider)
    {
        lock (Gate)
        {
            _apiProvider = apiProvider;
            _autonomousProvider = autonomousProvider;
            if (_attached)
                return;
            _attached = true;
        }
        ServerOutageMonitor.Recovered += () => _ = OnServerRecoveredAsync();
    }

    internal static async Task OnServerRecoveredAsync()
    {
        Func<IAuthApiService?>? apiProvider;
        Func<IAutonomousAuthService?>? autonomousProvider;
        lock (Gate)
        {
            apiProvider = _apiProvider;
            autonomousProvider = _autonomousProvider;
        }
        try
        {
            var api = apiProvider?.Invoke();
            if (api is null)
                return;
            await RefreshProfileAsync(api, autonomousProvider?.Invoke(), Tr.T("связь восстановлена", "байланыш калыбына келди", "connection restored",
                "bağlantı yeniden kuruldu", "aloqa tiklandi"), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Права: профиль после возврата связи не перечитан ({ex.GetType().Name}).", "AUTH");
        }
    }

    /// <summary>Перечитать профиль с сервера и применить (как при входе: пользователь + филиал). true — профиль получен.
    /// Не ходит на сервер без входа, в автономном режиме и при недействительной сессии. Одновременные вызовы делят один запрос.</summary>
    /// <remarks>2026-11-11: <paramref name="quiet"/> — периодическая проверка (AccessSync): в журнал только изменения и сбои, а не строка каждые 90 с.</remarks>
    public static Task<bool> RefreshProfileAsync(IAuthApiService api, IAutonomousAuthService? autonomous, string reason, TimeSpan timeout, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (autonomous?.IsCurrentSessionAutonomous == true || string.IsNullOrEmpty(api.AccessToken) || SessionExpiryMonitor.IsExpired)
            return Task.FromResult(false);
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false } running)
                return running;
            return _inFlight = RefreshCoreAsync(api, reason, timeout, quiet);
        }
    }

    private static async Task<bool> RefreshCoreAsync(IAuthApiService api, string reason, TimeSpan timeout, bool quiet = false)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // 2026-11-13 (сервер «ev1311»: ETag у users/profile/): If-None-Match → 304 = права не изменились, тело не разбираем и ничего не применяем.
            // ETag держится по профилю (пользователь + компания + филиал) и только пока права в памяти те же, что были при его получении, и они не из
            // сохранённого входа — иначе 304 оставил бы устаревшее. Нет ETag в ответе (старый сервер) — всё как раньше: каждый раз полный ответ.
            JsonElement profile;
            string? newTag = null;
            var profileKey = ProfileKey(api);
            if (api is IConditionalProfileApi conditional)
            {
                string? sendTag;
                lock (Gate)
                    sendTag = _profileEtag is not null && _profileEtagKey == profileKey && !_fromSavedSession
                        && _profileEtagFingerprint == AccessFingerprint(api.UserPayload) ? _profileEtag : null;
                int status;
                (profile, status, newTag) = await conditional.GetProfileConditionalAsync(sendTag, cts.Token).ConfigureAwait(false);
                if (status == 304 && sendTag is not null)
                {
                    MarkLiveProfile();
                    lock (Gate)
                        _profileEtagSupported = true;
                    return true;
                }
            }
            else
            {
                profile = await api.GetProfileAsync(cts.Token).ConfigureAwait(false);
            }
            if (profile.ValueKind != JsonValueKind.Object)
            {
                lock (Gate)
                    _lastFailure = Tr.T("сервер прислал пустой профиль", "сервер бош профиль жөнөттү", "the server sent an empty profile",
                        "sunucu boş profil gönderdi", "server bo'sh profil yubordi");
                return false;
            }
            var before = AccessFingerprint(api.UserPayload);
            api.ApplyUserFromProfile(profile);
            api.ApplyBranchFromProfile(profile);
            MarkLiveProfile();
            var after = AccessFingerprint(api.UserPayload);
            lock (Gate)
            {
                _profileEtag = string.IsNullOrWhiteSpace(newTag) ? null : newTag;
                _profileEtagKey = profileKey;
                _profileEtagFingerprint = after;
                _profileEtagSupported = _profileEtag is not null;
            }
            if (!quiet || before != after)
                PosLogger.Log($"Права: профиль перечитан с сервера ({reason}); включённых доступов can_*: {CountFlags(api.UserPayload)}.", "AUTH");
            // 2026-11-11: доступы изменились на сайте или в программе владельца — меню, разделы и кнопки пересобираются на лету (без перезапуска).
            if (before != after && before.Length > 0)
            {
                PosLogger.Log("Права: доступы изменились на сервере — интерфейс обновляется без перезапуска.", "AUTH");
                try { PermissionsChanged?.Invoke(); }
                catch (Exception ex) { PosLogger.Log($"Права: обработчик изменения доступов не отработал ({ex.Message}).", "WARNING"); }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var what = ServerOutageMonitor.Describe(ex);
            lock (Gate)
                _lastFailure = what;
            PosLogger.Log($"Права: профиль не перечитан ({reason}): {what}.", "AUTH");
            return false;
        }
    }

    // 2026-11-13: ETag профиля (users/profile/) — по пользователю/компании/филиалу; отпечаток прав на момент получения; «сервер отдаёт ETag» (для частого такта AccessSync).
    private static string? _profileEtag;
    private static string? _profileEtagKey;
    private static string? _profileEtagFingerprint;
    private static bool _profileEtagSupported;

    /// <summary>2026-11-13: последний ответ профиля пришёл с ETag — условные запросы работают, проверку можно делать чаще (304 дёшев).</summary>
    public static bool ProfileEtagSupported { get { lock (Gate) return _profileEtagSupported; } }

    /// <summary>Чей профиль: пользователь, компания и филиал запроса (ETag одного не годится другому).</summary>
    private static string ProfileKey(IAuthApiService api)
    {
        var payload = api.UserPayload;
        var user = payload.ValueKind == JsonValueKind.Object ? OfflineAuthSessionStore.ResolveUserObject(payload) : default;
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        var id = user.ValueKind == JsonValueKind.Object
            ? (user.TryGetProperty("id", out var i) ? i.ToString() : user.TryGetProperty("email", out var e) ? e.ToString() : "")
            : "";
        return $"{id}|{CompanyInfoService.LastCompany?.Id}|{api.ActiveBranchId}";
    }

    /// <summary>2026-11-11: доступы пользователя в профиле изменились (сайт NurCRM, программа владельца) — после любого перечитывания
    /// (возврат связи, отказ по правам, периодическая проверка <see cref="AccessSync"/>). Вызывается в фоновом потоке.</summary>
    public static event Action? PermissionsChanged;

    /// <summary>2026-11-11: «отпечаток» доступов — роль и включённые права (без имени, почты и токенов). Пусто — профиля ещё нет.</summary>
    public static string AccessFingerprint(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return "";
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        var role = user.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        var names = PermissionService.ExtractPermissionNames(payload).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return role + "|" + string.Join(",", names);
    }

    /// <summary>2026-11-11: перечитать профиль через поставщиков, заданных в <see cref="Attach"/> (для <see cref="AccessSync"/>). false — не вышло или нет входа.</summary>
    internal static Task<bool> RefreshFromProvidersAsync(string reason, TimeSpan timeout, bool quiet = true)
    {
        Func<IAuthApiService?>? apiProvider;
        Func<IAutonomousAuthService?>? autonomousProvider;
        lock (Gate)
        {
            apiProvider = _apiProvider;
            autonomousProvider = _autonomousProvider;
        }
        try
        {
            return apiProvider?.Invoke() is { } api
                ? RefreshProfileAsync(api, autonomousProvider?.Invoke(), reason, timeout, quiet)
                : Task.FromResult(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Права: профиль не перечитан ({ex.GetType().Name}).", "AUTH");
            return Task.FromResult(false);
        }
    }

    private static int CountFlags(JsonElement payload) =>
        PermissionService.ExtractPermissionNames(payload).Count(n => n.StartsWith("can_", StringComparison.OrdinalIgnoreCase));

    /// <summary>Строка «для поддержки» к отказу по правам: право и его значение в профиле (true / false / нет поля), роль, сколько доступов
    /// включено, откуда права (сервер со временем / сохранённый вход), ошибка перепроверки. Без токенов, почты и паролей.</summary>
    public static string Describe(string permission, JsonElement payload)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        var role = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(r.GetString())
            ? r.GetString()!.Trim()
            : "—";
        var flag = PermissionService.ExtractPermissionNames(payload).Contains(permission)
            ? "true"
            : PermissionService.ReadExplicitFlag(payload, permission, 0) == false
                ? "false"
                : Tr.T("нет поля", "талаа жок", "missing", "alan yok", "maydon yo'q");
        DateTimeOffset? liveAt;
        bool saved;
        string? failure;
        lock (Gate)
        {
            liveAt = _liveProfileAt;
            saved = _fromSavedSession;
            failure = _lastFailure;
        }
        var source = saved || liveAt is null
            ? Tr.T("сохранённый вход (сервер при входе не ответил)", "сакталган кирүү (кирүүдө сервер жооп берген жок)",
                "saved sign-in (the server did not answer at sign-in)", "kayıtlı oturum (girişte sunucu yanıt vermedi)",
                "saqlangan kirish (kirishda server javob bermadi)")
            : Tr.T($"профиль сервера {liveAt:HH:mm:ss}", $"сервердин профили {liveAt:HH:mm:ss}", $"server profile {liveAt:HH:mm:ss}",
                $"sunucu profili {liveAt:HH:mm:ss}", $"server profili {liveAt:HH:mm:ss}");
        var line = Tr.T(
            $"Для поддержки: {permission} = {flag}; роль: {role}; включено доступов: {CountFlags(payload)}; права из: {source}.",
            $"Колдоо үчүн: {permission} = {flag}; ролу: {role}; күйгүзүлгөн уруксаттар: {CountFlags(payload)}; укуктар: {source}.",
            $"For support: {permission} = {flag}; role: {role}; permissions on: {CountFlags(payload)}; rights from: {source}.",
            $"Destek için: {permission} = {flag}; rol: {role}; açık erişimler: {CountFlags(payload)}; yetki kaynağı: {source}.",
            $"Yordam uchun: {permission} = {flag}; rol: {role}; yoqilgan ruxsatlar: {CountFlags(payload)}; huquqlar manbai: {source}.");
        if (!string.IsNullOrWhiteSpace(failure))
            line += " " + Tr.T($"Перепроверка на сервере не удалась: {failure}.", $"Серверде кайра текшерүү ишке ашкан жок: {failure}.",
                $"Re-check on the server failed: {failure}.", $"Sunucuda yeniden kontrol başarısız: {failure}.",
                $"Serverda qayta tekshirish amalga oshmadi: {failure}.");
        return line;
    }

    /// <summary>Только для тестов: сбросить состояние.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _inFlight = null;
            _liveProfileAt = null;
            _fromSavedSession = false;
            _lastFailure = null;
            _profileEtag = null;
            _profileEtagKey = null;
            _profileEtagFingerprint = null;
            _profileEtagSupported = false;
        }
    }
}
