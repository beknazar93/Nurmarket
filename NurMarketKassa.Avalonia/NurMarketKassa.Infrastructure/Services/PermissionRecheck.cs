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
    public static Task<bool> RefreshProfileAsync(IAuthApiService api, IAutonomousAuthService? autonomous, string reason, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (autonomous?.IsCurrentSessionAutonomous == true || string.IsNullOrEmpty(api.AccessToken) || SessionExpiryMonitor.IsExpired)
            return Task.FromResult(false);
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false } running)
                return running;
            return _inFlight = RefreshCoreAsync(api, reason, timeout);
        }
    }

    private static async Task<bool> RefreshCoreAsync(IAuthApiService api, string reason, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var profile = await api.GetProfileAsync(cts.Token).ConfigureAwait(false);
            if (profile.ValueKind != JsonValueKind.Object)
            {
                lock (Gate)
                    _lastFailure = Tr.T("сервер прислал пустой профиль", "сервер бош профиль жөнөттү", "the server sent an empty profile",
                        "sunucu boş profil gönderdi", "server bo'sh profil yubordi");
                return false;
            }
            api.ApplyUserFromProfile(profile);
            api.ApplyBranchFromProfile(profile);
            MarkLiveProfile();
            PosLogger.Log($"Права: профиль перечитан с сервера ({reason}); включённых доступов can_*: {CountFlags(api.UserPayload)}.", "AUTH");
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
        }
    }
}
