using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27: вход кассира по QR-бейджу на экране входа кассы. Порядок:
/// 1) пауза после неудач (<see cref="CashierBadgeThrottle"/>); 2) проверка кода по реестру бейджей магазина (локальная копия общей
/// настройки); 3) PIN, если владелец его задал; 4) сохранённый вход этого кассира на кассе (<see cref="CashierBadgeVault"/>)
/// кладётся в auth.dat и проходит обычный автовход — сервер подтверждает токен; 5) вошёл не тот, кому выдан бейдж — выход;
/// 6) при связи — свежий реестр с сервера: бейдж отозван/истёк там — выход. Секрет бейджа нигде не пишется (журнал — маска id).</summary>
public sealed class CashierBadgeLogin
{
    private readonly IOnlineOfflineAuthenticationService _auth;
    private readonly IAuthSessionManager _storage;
    private readonly CashierBadgeVault _vault;
    private readonly CashierBadgeThrottle _throttle;
    private readonly Func<string?> _localRegistry;
    private readonly Func<CancellationToken, Task<string?>>? _serverRegistry;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="serverRegistry">Свежий JSON реестра с сервера; null из функции — ключа нет (реестр пуст); исключение — сервер не ответил (берём локальный).</param>
    public CashierBadgeLogin(
        IOnlineOfflineAuthenticationService auth,
        IAuthSessionManager storage,
        CashierBadgeVault vault,
        CashierBadgeThrottle throttle,
        Func<string?> localRegistry,
        Func<CancellationToken, Task<string?>>? serverRegistry,
        Func<DateTimeOffset>? clock = null)
    {
        _auth = auth;
        _storage = storage;
        _vault = vault;
        _throttle = throttle;
        _localRegistry = localRegistry;
        _serverRegistry = serverRegistry;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Шаги 1–2. Неудача (кроме «не бейдж») засчитывается в паузу.</summary>
    public CashierBadgeCheck Check(string? raw)
    {
        var now = _clock();
        if (_throttle.LockedFor(now) is { } wait)
            return new CashierBadgeCheck(CashierBadgeStatus.LockedOut, Wait: wait);
        var check = CashierBadgeRegistry.Verify(raw, CashierBadgeRegistry.Parse(_localRegistry()), now);
        // 2026-11-27 (D3d): «не прочитан целиком» — не подбор секрета (хэш не проверялся), в паузу не засчитывается: оборванные сканы
        // раньше копили 5 «неудач», и следующий верный бейдж упирался в паузу 30 с и дольше.
        if (check.Status is not (CashierBadgeStatus.Ok or CashierBadgeStatus.NotBadge))
        {
            if (check.Status != CashierBadgeStatus.Malformed)
                _throttle.RegisterFailure(now);
            PosLogger.Log($"Вход по бейджу отклонён: {check.Status} (бейдж {(CashierBadgeCode.TryParse(raw, out var id, out _) ? CashierBadgeCode.Mask(id) : "?")}).", "AUTH");
        }
        return check;
    }

    /// <summary>Шаг 3. Неверный PIN засчитывается в паузу.</summary>
    public bool CheckPin(CashierBadgeRecord record, string? pin)
    {
        if (CashierBadgeRegistry.VerifyPin(record, pin))
            return true;
        _throttle.RegisterFailure(_clock());
        PosLogger.Log($"Вход по бейджу: неверный PIN (бейдж {CashierBadgeCode.Mask(record.Id)}).", "AUTH");
        return false;
    }

    /// <summary>На этой кассе есть сохранённый вход сотрудника (иначе нужен пароль один раз).</summary>
    public bool HasSavedLogin(CashierBadgeRecord record) => _vault.Has(record.EmployeeId);

    /// <summary>Шаги 4–6. Отказ «нужен пароль» — <see cref="AuthenticationFailure.SessionExpired"/>.</summary>
    public async Task<AuthenticationResult> LoginAsync(CashierBadgeRecord record, bool rememberMe, CancellationToken ct = default)
    {
        var saved = _vault.TryGet(record.EmployeeId);
        if (saved is null)
            return AuthenticationResult.Failed(AuthenticationFailure.SessionExpired, NeedPasswordText(record));

        await _storage.SaveSessionAsync(saved).ConfigureAwait(false);
        var result = await _auth.AutoLoginAsync(ct).ConfigureAwait(false);
        if (!result.IsSuccess || result.Session is null)
        {
            if (result.Failure == AuthenticationFailure.SessionExpired)
            {
                // Сервер отверг сохранённый вход (истёк refresh, сменён пароль, сотрудник отключён) — больше его не пробуем.
                _vault.Remove(record.EmployeeId);
                PosLogger.Log($"Вход по бейджу: сохранённый вход кассира отвергнут сервером — нужен пароль (бейдж {CashierBadgeCode.Mask(record.Id)}).", "AUTH");
                return AuthenticationResult.Failed(AuthenticationFailure.SessionExpired, NeedPasswordText(record));
            }
            return result;
        }

        if (!string.Equals(result.Session.UserId?.Trim(), record.EmployeeId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            PosLogger.Log($"Вход по бейджу: сохранённый вход принадлежит другому сотруднику — отказ (бейдж {CashierBadgeCode.Mask(record.Id)}).", "AUTH");
            _vault.Remove(record.EmployeeId);
            await _auth.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
            return AuthenticationResult.Failed(AuthenticationFailure.InvalidCredentials, CashierBadgeRegistry.Message(CashierBadgeStatus.WrongSecret));
        }

        // Шаг 6: отзыв владельцем мог ещё не дойти до локальной копии — при связи сверяемся с сервером.
        if (result.Mode == AuthenticationMode.Online && _serverRegistry is not null)
        {
            string? fresh = null;
            var fetched = false;
            try
            {
                fresh = await _serverRegistry(ct).ConfigureAwait(false);
                fetched = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiException or OperationCanceledException or JsonException or InvalidDataException)
            {
                PosLogger.Log($"Вход по бейджу: свежий список бейджей не получен ({ex.GetType().Name}) — проверено по списку кассы.", "AUTH");
            }
            if (fetched)
            {
                var current = CashierBadgeRegistry.Parse(fresh).FirstOrDefault(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                var status = current is null ? CashierBadgeStatus.Unknown
                    : !CashierBadgeCode.HashEquals(current.Hash, record.Hash) ? CashierBadgeStatus.WrongSecret
                    : current.RevokedAt is not null ? CashierBadgeStatus.Revoked
                    : !current.IsActive(_clock()) ? CashierBadgeStatus.Expired
                    : CashierBadgeStatus.Ok;
                if (status != CashierBadgeStatus.Ok)
                {
                    PosLogger.Log($"Вход по бейджу: на сервере бейдж {status} — выход (бейдж {CashierBadgeCode.Mask(record.Id)}).", "AUTH");
                    _vault.Remove(record.EmployeeId);
                    await _auth.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
                    _throttle.RegisterFailure(_clock());
                    return AuthenticationResult.Failed(AuthenticationFailure.InvalidCredentials, CashierBadgeRegistry.Message(status));
                }
            }
        }

        _vault.Save(result.Session);
        if (!rememberMe)
            await _storage.ClearSessionAsync(CancellationToken.None).ConfigureAwait(false);
        _throttle.RegisterSuccess();
        PosLogger.Log($"Вход по бейджу: вход выполнен ({(result.Mode == AuthenticationMode.Online ? "онлайн" : "автономно")}, бейдж {CashierBadgeCode.Mask(record.Id)}).", "AUTH");
        return result;
    }

    /// <summary>После входа паролем: у сотрудника есть действующий бейдж (или его только что предъявили) — сохранить вход для бейджа.</summary>
    public static void RememberAfterPasswordLogin(UserSession? session, CashierBadgeRecord? pendingBadge, string? localRegistryJson, CashierBadgeVault vault, DateTimeOffset now)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.UserId))
            return;
        var matchesPending = pendingBadge is not null
                             && string.Equals(pendingBadge.EmployeeId.Trim(), session.UserId.Trim(), StringComparison.OrdinalIgnoreCase);
        var hasActive = CashierBadgeRegistry.ActiveFor(CashierBadgeRegistry.Parse(localRegistryJson), session.UserId, now) is not null;
        if (!matchesPending && !hasActive)
            return;
        var had = vault.Has(session.UserId);
        vault.Save(session);
        if (!had)
            PosLogger.Log("Вход по бейджу: вход кассира сохранён на этой кассе — дальше хватит бейджа.", "AUTH");
    }

    /// <summary>Текст «первый вход по бейджу на этой кассе — пароль один раз».</summary>
    public static string NeedPasswordText(CashierBadgeRecord record)
    {
        var who = string.IsNullOrWhiteSpace(record.Name) ? "" : $" ({record.Name})";
        // 2026-11-27: срок входа NurCRM задаёт сервер (сейчас ~30 дней, продлевается при работе) — без числа дней.
        return Tr.T($"Бейдж принят{who}. На этой кассе нужен вход паролем один раз (и снова, только если истечёт вход NurCRM) — введите пароль, дальше будет хватать бейджа.",
            $"Бейдж кабыл алынды{who}. Бул кассада бир жолу сырсөз менен кирүү керек (кайра — NurCRM кирүүсүнүн мөөнөтү бүтсө гана) — сырсөздү киргизиңиз, андан ары бейдж жетиштүү болот.",
            $"Badge accepted{who}. This till needs a password sign-in once (and again only if the NurCRM sign-in expires) — enter the password; after that the badge is enough.",
            $"Rozet kabul edildi{who}. Bu kasada bir kez şifreyle giriş gerekir (yeniden yalnızca NurCRM girişinin süresi dolarsa) — şifreyi girin, sonrası için rozet yeterli olur.",
            $"Beydj qabul qilindi{who}. Bu kassada bir marta parol bilan kirish kerak (qayta — faqat NurCRM kirish muddati tugasa) — parolni kiriting, keyin beydj yetarli bo'ladi.");
    }

    /// <summary>Свежий JSON реестра из общего документа настроек компании (GET api/main/app-settings/nurmarket-kassa/).</summary>
    public static async Task<string?> FetchServerRegistryAsync(NurMarketApiClient api, CancellationToken ct)
    {
        var data = await api.RequestAsync(HttpMethod.Get, "api/main/app-settings/nurmarket-kassa/", null, null, ct, TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings))
            throw new InvalidDataException("ответ без поля settings");
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty(CashierBadgeRegistry.CloudKey, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => value.GetRawText(),
            _ => null,
        };
    }
}
