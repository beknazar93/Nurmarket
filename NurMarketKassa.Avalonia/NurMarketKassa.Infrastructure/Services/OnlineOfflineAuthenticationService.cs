using System.Net.Http;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Coordinates server authentication, token refresh and the strictly limited
/// offline fallback. Server rejection never becomes an offline login.
/// </summary>
public sealed class OnlineOfflineAuthenticationService : IOnlineOfflineAuthenticationService
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly IAuthApiService _api;
    private readonly IAuthSessionManager _storage;

    public OnlineOfflineAuthenticationService(IAuthApiService api, IAuthSessionManager storage)
    {
        _api = api;
        _storage = storage;
        // 2026-10-04: токены, обновлённые посреди работы, — в auth.dat (см. NurMarketApiClient.SessionTokensRefreshed).
        NurMarketApiClient.SessionTokensRefreshed += OnSessionTokensRefreshed;
    }

    /// <summary>2026-10-04: запись auth.dat из этого сервиса — по одной: обновление токенов посреди
    /// работы (из события) и сохранение сессии входом/автовходом не перетирают друг друга.
    /// SemaphoreSlim отпускает ждущих по очереди — сохранения идут в порядке обновлений. Общий на
    /// процесс: событие обновления статическое, и при двух экземплярах сервиса (проверочная обвязка)
    /// раздельные замки давали одновременную запись одного файла (IOException на auth.dat.tmp).</summary>
    private static readonly SemaphoreSlim _persistGate = new(1, 1);

    private async Task SaveSessionGatedAsync(UserSession session)
    {
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _storage.SaveSessionAsync(session).ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private void OnSessionTokensRefreshed(string? usedRefresh, string? access, string? refresh) =>
        _ = PersistRefreshedTokensAsync(usedRefresh, access, refresh);

    /// <summary>2026-10-04, отчёт «офлайн и сбои сервера», раздел «Риски»: обновлённые посреди работы
    /// токены не попадали в auth.dat, откуда их берёт автовход (подробно — у
    /// NurMarketApiClient.SessionTokensRefreshed). Переписываем сохранённую сессию (токены, срок,
    /// отметку связи), только если она та самая, чей refresh-токен только что обменяли: после выхода
    /// (auth.dat удалён), без «Запомнить меня» или при сохранённом входе другого кассира файл не трогаем.</summary>
    private async Task PersistRefreshedTokensAsync(string? usedRefresh, string? access, string? refresh)
    {
        if (string.IsNullOrWhiteSpace(usedRefresh) || string.IsNullOrWhiteSpace(access))
            return;

        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var saved = await _storage.LoadSessionAsync().ConfigureAwait(false);
            if (saved is null || !string.Equals(saved.RefreshToken, usedRefresh, StringComparison.Ordinal))
                return;

            var updated = new UserSession
            {
                AccessToken = access!,
                RefreshToken = string.IsNullOrWhiteSpace(refresh) ? saved.RefreshToken : refresh!,
                ExpiresAt = ReadJwtExpiration(access!)?.ToUniversalTime() ?? saved.ExpiresAt,
                UserId = saved.UserId,
                Login = saved.Login,
                DisplayName = saved.DisplayName,
                Role = saved.Role,
                BranchId = saved.BranchId,
                Permissions = saved.Permissions,
                // Обновление токена — успешный ответ сервера.
                LastOnlineContactAt = DateTimeOffset.UtcNow,
            };
            await _storage.SaveSessionAsync(updated).ConfigureAwait(false);
            PosLogger.Log("Обновлённые токены сохранены для автовхода (auth.dat).", "AUTH");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обновлённые токены не сохранены в auth.dat: {ex.GetType().Name}", "AUTH");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    public async Task<AuthenticationResult> LoginAsync(
        string username,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0)
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.InvalidCredentials,
                Tr.T("Введите логин и пароль.", "Логин менен сырсөздү киргизиңиз.", "Enter your login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting."));
        }

        // 2026-10-04, отчёт «офлайн и сбои сервера», раздел «Не сделано»: ручной вход по паролю при
        // «чёрной дыре» ждал сервер до 55 с (таймаут HttpClient) на запрос входа и ещё до 55 с на
        // профиль. Теперь каждый шаг ждёт ответа не дольше ManualLoginServerBudget (окно отсчитывается
        // заново после ответа на вход): живой сервер отвечает за доли секунды, медленный (3–4 с) —
        // укладывается; молчащий — понятный текст через 8 с, а если на этом ПК есть сохранённый вход
        // этим логином — предложение войти автономно (ContinueOfflineAsync, как автовход).
        using var serverBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        serverBudget.CancelAfter(ManualLoginServerBudget);
        try
        {
            var loginPayload = await _api.LoginAsync(username, password, serverBudget.Token)
                .ConfigureAwait(false);
            serverBudget.CancelAfter(ManualLoginServerBudget);
            var profile = await LoadAndApplyProfileAsync(serverBudget.Token).ConfigureAwait(false);
            serverBudget.CancelAfter(Timeout.InfiniteTimeSpan);
            var session = CreateSession(username, loginPayload, profile);

            // Never keep a second token copy in the legacy session file.
            OfflineAuthSessionStore.Clear();

            if (rememberMe)
                await SaveSessionGatedAsync(session).ConfigureAwait(false);
            else
                await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);

            return AuthenticationResult.Success(session, AuthenticationMode.Online);
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            _api.ClearSession();
            return AuthenticationResult.Failed(
                AuthenticationFailure.InvalidCredentials,
                Tr.T("Неверный логин или пароль.", "Логин же сырсөз туура эмес.", "Incorrect login or password.", "Kullanıcı adı veya şifre hatalı.", "Login yoki parol noto'g'ri."));
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            var offline = await CanContinueOfflineAsync(username).ConfigureAwait(false);
            PosLogger.Log($"Вход по паролю: сервер не ответил ({ServerOutageMonitor.Describe(ex)}){(offline ? ", есть сохранённый вход этим логином — предложен автономный вход" : "")}.", "AUTH");
            return new AuthenticationResult
            {
                Failure = AuthenticationFailure.NetworkUnavailable,
                CanContinueOffline = offline,
                ErrorMessage = Tr.T(
                    "Сервер NurCRM не отвечает. Проверьте интернет и повторите вход позже.",
                    "NurCRM сервери жооп бербей жатат. Интернетти текшерип, кийинчерээк кайра кириңиз.",
                    "The NurCRM server is not responding. Check the internet and sign in again later.",
                    "NurCRM sunucusu yanıt vermiyor. İnterneti kontrol edin ve daha sonra tekrar giriş yapın.",
                    "NurCRM serveri javob bermayapti. Internetni tekshiring va keyinroq qayta kiring."),
            };
        }
        catch (ApiException ex)
        {
            // 2026-10-04: сбой сервера (5xx, HTML вместо JSON) — тоже можно продолжить автономно
            // (429 — сервер жив и просит паузу: не предлагаем).
            var offline = ex.StatusCode is >= 500 and <= 599
                          && await CanContinueOfflineAsync(username).ConfigureAwait(false);
            return new AuthenticationResult
            {
                Failure = AuthenticationFailure.ServerError,
                CanContinueOffline = offline,
                ErrorMessage = Tr.T("Сервер временно недоступен. Повторите попытку позже.",
                    "Сервер убактылуу жеткиликсиз. Кийинчерээк кайра аракет кылыңыз.",
                    "The server is temporarily unavailable. Try again later.",
                    "Sunucu geçici olarak kullanılamıyor. Daha sonra tekrar deneyin.",
                    "Server vaqtincha mavjud emas. Keyinroq qayta urinib ko'ring."),
            };
        }
    }

    /// <summary>2026-10-04: сколько ручной вход по паролю ждёт сервер на каждом шаге (вход, затем
    /// профиль), прежде чем сказать «сервер не отвечает». Владелец: «8–10 с, не больше».</summary>
    internal static readonly TimeSpan ManualLoginServerBudget = TimeSpan.FromSeconds(8);

    /// <summary>2026-10-04: на этом ПК есть сохранённый вход (auth.dat) этим логином, по которому
    /// автовход пустил бы кассу автономно: токен доступа жив или его можно продлить refresh-токеном,
    /// и не вышли 60 часов без связи. Тогда при молчащем сервере окно входа предлагает войти
    /// автономно — без этого кассир, введший пароль, ждал бы сервер, хотя перезапуск кассы пустил бы
    /// его сразу.</summary>
    private async Task<bool> CanContinueOfflineAsync(string username)
    {
        try
        {
            var saved = await _storage.LoadSessionAsync().ConfigureAwait(false);
            return saved is not null
                   && string.Equals(saved.Login.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase)
                   && (IsLocallyValid(saved) || IsRefreshLocallyValid(saved))
                   && IsWithinOfflineGracePeriod(saved);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сохранённый вход не прочитан: {ex.GetType().Name}", "AUTH");
            return false;
        }
    }

    /// <summary>2026-10-04: «Войти автономно» в окне входа после того, как сервер не ответил на вход по
    /// паролю (см. <see cref="AuthenticationResult.CanContinueOffline"/>): вход по сохранённой сессии
    /// этого логина — тем же путём, что автовход при сбое сервера (авария объявляется, проверку
    /// «сервер снова жив» ведёт ServerOutageMonitor, сессия не стирается).</summary>
    public async Task<AuthenticationResult> ContinueOfflineAsync(string username, CancellationToken cancellationToken = default)
    {
        var saved = await _storage.LoadSessionAsync().ConfigureAwait(false);
        if (saved is null || !string.Equals(saved.Login.Trim(), (username ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            return AuthenticationResult.Failed(
                AuthenticationFailure.SessionExpired,
                Tr.T("На этом компьютере нет сохранённого входа для этого логина. Войдите, когда сервер заработает.",
                    "Бул компьютерде бул логин үчүн сакталган кирүү жок. Сервер иштегенде кириңиз.",
                    "There is no saved sign-in for this login on this computer. Sign in once the server is back.",
                    "Bu bilgisayarda bu kullanıcı adı için kayıtlı oturum yok. Sunucu çalışınca giriş yapın.",
                    "Bu kompyuterda ushbu login uchun saqlangan kirish yo'q. Server ishlaganda kiring."));

        _api.RestoreOfflineSession(ToLegacySession(saved));
        return await ContinueOfflineAfterServerFailureAsync(
                saved,
                new HttpRequestException(Tr.T("сервер не ответил на вход по паролю", "сервер сырсөз менен кирүүгө жооп берген жок",
                    "the server did not answer the password sign-in", "sunucu şifreyle girişe yanıt vermedi",
                    "server parol bilan kirishga javob bermadi")),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AuthenticationResult> AutoLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var saved = await _storage.LoadSessionAsync().ConfigureAwait(false);
        if (saved is null)
        {
            return AuthenticationResult.Failed(
                AuthenticationFailure.SessionExpired,
                "Сохранённая сессия отсутствует.");
        }

        _api.RestoreOfflineSession(ToLegacySession(saved));

        // 2026-10-04, стенд «сбои сервера»: при запуске кассы сервер ждём не дольше
        // AutoLoginServerBudget. Раньше обновление токена и профиль шли с общим таймаутом HttpClient
        // (55 с каждый): при «чёрной дыре» или медленном сервере кассир смотрел на заставку минуту и
        // дольше. Не ответил вовремя — вход по сохранённой сессии (как без интернета), касса работает
        // автономно, а проверку «сервер снова жив» ведёт ServerOutageMonitor в фоне.
        using var serverBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        serverBudget.CancelAfter(AutoLoginServerBudget);
        var serverCt = serverBudget.Token;

        try
        {
            // Refresh first when the local JWT lifetime has ended. An expired
            // token is never accepted offline, even if the network is down.
            // 2026-10-04: кроме сбоя связи/сервера — тогда вход по живому refresh-токену (см. ниже).
            if (!IsLocallyValid(saved))
            {
                if (!await _api.RefreshAccessAsync(serverCt).ConfigureAwait(false))
                    return await RejectSavedSessionAsync(Tr.T("Сессия истекла. Войдите снова.",
                        "Сессиянын мөөнөтү бүттү. Кайра кириңиз.", "Your session has expired. Please sign in again.",
                        "Oturumun süresi doldu. Tekrar giriş yapın.", "Sessiya muddati tugadi. Qaytadan kiring."),
                        cancellationToken).ConfigureAwait(false);

                var refreshedProfile = await LoadAndApplyProfileAsync(serverCt).ConfigureAwait(false);
                var refreshed = CreateSession(saved.Login, default, refreshedProfile, saved);
                await SaveSessionGatedAsync(refreshed).ConfigureAwait(false);
                return AuthenticationResult.Success(refreshed, AuthenticationMode.Online);
            }

            // This request is the authoritative server-side validation.
            var profile = await LoadAndApplyProfileAsync(serverCt).ConfigureAwait(false);
            var validated = CreateSession(saved.Login, default, profile, saved);
            await SaveSessionGatedAsync(validated).ConfigureAwait(false);
            return AuthenticationResult.Success(validated, AuthenticationMode.Online);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            try
            {
                if (!await _api.RefreshAccessAsync(serverCt).ConfigureAwait(false))
                    return await RejectSavedSessionAsync(Tr.T("Сессия отозвана. Войдите снова.", "Сессия жокко чыгарылды. Кайра кириңиз.",
                        "Your session was revoked. Please sign in again.", "Oturum iptal edildi. Tekrar giriş yapın.",
                        "Sessiya bekor qilindi. Qaytadan kiring."),
                        cancellationToken).ConfigureAwait(false);

                var profile = await LoadAndApplyProfileAsync(serverCt).ConfigureAwait(false);
                var refreshed = CreateSession(saved.Login, default, profile, saved);
                await SaveSessionGatedAsync(refreshed).ConfigureAwait(false);
                return AuthenticationResult.Success(refreshed, AuthenticationMode.Online);
            }
            catch (Exception refreshError) when (IsNetworkFailure(refreshError, cancellationToken)
                                                 || refreshError is ApiException { StatusCode: var code }
                                                 && ServerOutageMonitor.IsServerFailureStatus(code))
            {
                // 2026-10-04: и при 5xx/429 на обновлении токена — сервер не ответил по существу, это не
                // «сессия отозвана» (раньше 5xx здесь стирал сохранённую сессию).
                return await ContinueOfflineAfterServerFailureAsync(saved, refreshError, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException)
            {
                return await RejectSavedSessionAsync(Tr.T("Сессия отозвана. Войдите снова.", "Сессия жокко чыгарылды. Кайра кириңиз.",
                        "Your session was revoked. Please sign in again.", "Oturum iptal edildi. Tekrar giriş yapın.",
                        "Sessiya bekor qilindi. Qaytadan kiring."),
                        cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            return await RejectSavedSessionAsync(Tr.T("Доступ к учётной записи отозван. Войдите снова.",
                "Эсептик жазууга кирүү укугу жокко чыгарылды. Кайра кириңиз.",
                "Access to the account was revoked. Please sign in again.",
                "Hesaba erişim iptal edildi. Tekrar giriş yapın.",
                "Hisobga kirish huquqi bekor qilindi. Qaytadan kiring."), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            // 2026-10-04, стенд «сбои сервера»: нет интернета, «чёрная дыра» или сервер не ответил за
            // AutoLoginServerBudget — как при аварии сервера ниже. Раньше здесь был OfflineOrExpiredAsync
            // без refresh-токена: токен доступа живёт ~15 минут, поэтому почти любой перезапуск кассы без
            // связи кончался «Сессия истекла. Для входа подключитесь к интернету.» и СТИРАЛ сохранённую
            // сессию — кассир не мог работать, пока не вернётся интернет (а 60 часов офлайн-работы,
            // которые разрешил владелец, на деле были 15 минутами).
            return await ContinueOfflineAfterServerFailureAsync(saved, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode))
        {
            // 2026-09-29, требование владельца «при сбое бэка не выводи ошибку — работай автономно
            // до исправления бэка»: касса, перезапущенная во время аварии сервера (5xx, 408, 429,
            // HTML вместо JSON), раньше не пускала кассира вообще — «Автономный вход разрешён только
            // при сетевой ошибке», а при истёкшем токене доступа (он живёт ~15 минут, то есть почти
            // всегда) ещё и стирала сохранённую сессию: обновить токен не дал тот же лежащий сервер.
            //
            // Сервер, который не отвечает, так же не может ни подтвердить, ни отвергнуть сессию, как
            // и отсутствие сети. Поэтому вход автономно — по правилам офлайн-входа (60 часов без
            // связи), а срок сессии в аварии определяет refresh-токен: именно им касса продлит
            // доступ, как только сервер оживёт. Отозванный на сервере токен здесь не пройдёт
            // дальше первого же ответа сервера (401 → «Сессия недействительна», как и раньше).
            // 2026-10-04: вынесено в ContinueOfflineAfterServerFailureAsync — тем же путём теперь идёт и
            // сбой связи (см. catch выше).
            return await ContinueOfflineAfterServerFailureAsync(saved, ex, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // A reachable server returning 5xx is not proof that credentials are
            // invalid, but it is also not a "no internet" condition.
            return AuthenticationResult.Failed(
                AuthenticationFailure.ServerError,
                Tr.T("Сервер временно недоступен. Автономный вход разрешён только при сетевой ошибке.",
                    "Сервер убактылуу жеткиликсиз. Автономдук кирүүгө тармак катасы болгондо гана уруксат берилет.",
                    "The server is temporarily unavailable. Offline sign-in is allowed only when there's a network error.",
                    "Sunucu geçici olarak kullanılamıyor. Çevrimdışı girişe yalnızca ağ hatası olduğunda izin verilir.",
                    "Server vaqtincha mavjud emas. Oflayn kirishga faqat tarmoq xatosi bo'lganda ruxsat beriladi."));
        }
    }

    /// <summary>2026-10-04: сколько автовход ждёт сервер (обновление токена + профиль) при запуске кассы,
    /// прежде чем войти по сохранённой сессии автономно. Живой сервер отвечает на оба запроса меньше
    /// чем за секунду; ошибся (сервер просто медленный) — касса вернётся в «Онлайн» сама по первой
    /// проверке монитора (через 10 с), чеки за это время уйдут из очереди.</summary>
    internal static readonly TimeSpan AutoLoginServerBudget = TimeSpan.FromSeconds(3);

    /// <summary>Сервер не ответил при автовходе (нет связи, «чёрная дыра», 5xx/429, HTML вместо JSON):
    /// вход по сохранённой сессии автономно, если её ещё можно продлить refresh-токеном, и авария
    /// объявляется сразу (ServerOutageMonitor) — смена, каталог и данные компании при запуске берутся
    /// из кассы без ожидания сервера, а проверку «сервер снова жив» монитор ведёт в фоне. Сессия не
    /// стирается: ни подтвердить, ни отвергнуть её сервер сейчас не может. 2026-09-29 — для 5xx,
    /// 2026-10-04 вынесено в отдельный метод и для сбоя связи.</summary>
    private async Task<AuthenticationResult> ContinueOfflineAfterServerFailureAsync(
        UserSession saved,
        Exception failure,
        CancellationToken cancellationToken)
    {
        var session = WithCurrentTokens(saved);
        if (!ReferenceEquals(session, saved))
            await SaveSessionGatedAsync(session).ConfigureAwait(false);
        if (IsLocallyValid(session) || IsRefreshLocallyValid(session))
        {
            PosLogger.Log($"Автовход: сервер не отвечает ({ServerOutageMonitor.Describe(failure)}) — вход по сохранённой сессии, касса работает автономно.", "OUTAGE");
            var result = await OfflineOrExpiredAsync(session, cancellationToken, acceptValidRefresh: true).ConfigureAwait(false);
            if (result.IsSuccess)
                ServerOutageMonitor.ReportFailure(Tr.T("вход в кассу", "кассага кирүү", "signing in to the till",
                    "kasaya giriş", "kassaga kirish"), failure, hard: true);
            return result;
        }

        // Истёк и refresh-токен — войти всё равно нельзя, но сессию не стираем: как только
        // сервер оживёт, автовход сам скажет, действительна ли она.
        return AuthenticationResult.Failed(
            AuthenticationFailure.ServerError,
            Tr.T("Сервер NurCRM временно не отвечает, а сохранённый вход истёк. Повторите вход, когда сервер заработает.",
                "NurCRM сервери убактылуу жооп бербей жатат, сакталган кирүүнүн мөөнөтү бүткөн. Сервер иштегенде кайра кириңиз.",
                "The NurCRM server is temporarily not responding and the saved sign-in has expired. Sign in again once the server is back.",
                "NurCRM sunucusu geçici olarak yanıt vermiyor ve kayıtlı oturumun süresi dolmuş. Sunucu çalışınca yeniden giriş yapın.",
                "NurCRM serveri vaqtincha javob bermayapti, saqlangan kirish muddati tugagan. Server ishlaganda qayta kiring."));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        OfflineAuthSessionStore.Clear(); // remove the pre-auth.dat session format
        _api.ClearSession();
    }

    private async Task<JsonElement> LoadAndApplyProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _api.GetProfileAsync(cancellationToken).ConfigureAwait(false);
        _api.ApplyUserFromProfile(profile);
        _api.ApplyBranchFromProfile(profile);
        return profile;
    }

    /// <param name="acceptValidRefresh">2026-09-29: только для аварии сервера (см. AutoLoginAsync) —
    /// истёкший токен доступа допустим, если refresh-токен ещё действует.</param>
    private async Task<AuthenticationResult> OfflineOrExpiredAsync(
        UserSession session,
        CancellationToken cancellationToken,
        bool acceptValidRefresh = false)
    {
        if (!IsLocallyValid(session) && !(acceptValidRefresh && IsRefreshLocallyValid(session)))
            return await RejectSavedSessionAsync(
                Tr.T("Сессия истекла. Для входа подключитесь к интернету.",
                    "Сессиянын мөөнөтү бүттү. Кирүү үчүн интернетке туташыңыз.",
                    "Your session has expired. Connect to the internet to sign in.",
                    "Oturumun süresi doldu. Giriş yapmak için internete bağlanın.",
                    "Sessiya muddati tugadi. Kirish uchun internetga ulaning."),
                cancellationToken).ConfigureAwait(false);

        // 2026-09-09: 60-часовой потолок офлайн-работы для ОБЫЧНОГО (не автономного) режима —
        // владелец явно попросил остановить офлайн-работу после 60 часов подряд без связи с
        // сервером, а не разрешать её бессрочно (как было раньше — единственная проверка тут
        // была на срок жизни JWT, который может быть куда длиннее). Автономный режим
        // (LocalAuthApiService) этой проверке не подчиняется — там офлайн-работа без лимита,
        // это его смысл.
        if (!IsWithinOfflineGracePeriod(session))
            return AuthenticationResult.Failed(
                AuthenticationFailure.NetworkUnavailable,
                Tr.T("Автономная работа без интернета ограничена 60 часами. Подключитесь к интернету, чтобы продолжить.",
                    "Интернетсиз автономдук иштөө 60 саат менен чектелген. Улантуу үчүн интернетке туташыңыз.",
                    "Offline work is limited to 60 hours. Connect to the internet to continue.",
                    "İnternetsiz çalışma 60 saatle sınırlıdır. Devam etmek için internete bağlanın.",
                    "Internetsiz oflayn ishlash 60 soat bilan cheklangan. Davom etish uchun internetga ulaning."));

        _api.RestoreOfflineSession(ToLegacySession(session));
        return AuthenticationResult.Success(session, AuthenticationMode.Offline);
    }

    private static bool IsWithinOfflineGracePeriod(UserSession session) =>
        session.LastOnlineContactAt is not { } last
        || DateTimeOffset.UtcNow - last <= OfflineAuthSessionStore.MaxOfflineDuration;

    private async Task<AuthenticationResult> RejectSavedSessionAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await _storage.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        _api.ClearSession();
        return AuthenticationResult.Failed(AuthenticationFailure.SessionExpired, message);
    }

    private static bool IsLocallyValid(UserSession session) =>
        session.ExpiresAt > DateTimeOffset.UtcNow.Add(ClockSkew);

    /// <summary>2026-09-29: refresh-токен ещё не истёк по своему сроку (поле exp JWT).</summary>
    private static bool IsRefreshLocallyValid(UserSession session) =>
        ReadJwtExpiration(session.RefreshToken) is { } refreshExpires
        && refreshExpires > DateTimeOffset.UtcNow.Add(ClockSkew);

    /// <summary>2026-09-29: сохранённая сессия с токенами, которые сейчас у клиента API. Если в
    /// автовходе токен успели обновить, а сервер упал уже на профиле, прежний refresh-токен сервер
    /// отозвал (он выдаёт новый при каждом обновлении) — войти по нему потом было бы нельзя.</summary>
    private UserSession WithCurrentTokens(UserSession saved)
    {
        var access = _api.AccessToken;
        if (string.IsNullOrEmpty(access) || string.Equals(access, saved.AccessToken, StringComparison.Ordinal))
            return saved;

        return new UserSession
        {
            AccessToken = access,
            RefreshToken = _api.RefreshToken ?? saved.RefreshToken,
            ExpiresAt = ReadJwtExpiration(access)?.ToUniversalTime() ?? saved.ExpiresAt,
            UserId = saved.UserId,
            Login = saved.Login,
            DisplayName = saved.DisplayName,
            Role = saved.Role,
            BranchId = saved.BranchId,
            Permissions = saved.Permissions,
            // Обновление токена — успешный ответ сервера.
            LastOnlineContactAt = DateTimeOffset.UtcNow,
        };
    }

    private UserSession CreateSession(
        string login,
        JsonElement loginPayload,
        JsonElement profile,
        UserSession? fallback = null)
    {
        var accessToken = _api.AccessToken ?? fallback?.AccessToken ?? "";
        var refreshToken = _api.RefreshToken ?? fallback?.RefreshToken ?? "";
        var user = OfflineAuthSessionStore.ResolveUserObject(profile.ValueKind == JsonValueKind.Object
            ? profile
            : _api.UserPayload);

        var userId = ReadString(user, "id", "pk", "uuid", "user_id") ?? fallback?.UserId ?? "";
        // 2026-09-24, живой случай: в меню «Кассир —». Сервер отдаёт имя в first_name/last_name
        // («Кассир Тест»), а их здесь не читали; при неудачной загрузке профиля имя бралось из
        // прошлой сессии, и пустая строка оттуда перекрывала запасной вариант — логин.
        var firstLast = string.Join(" ", new[] { ReadString(user, "first_name"), ReadString(user, "last_name") }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var displayName = ReadString(user, "full_name", "name")
            ?? (firstLast.Length > 0 ? firstLast : null)
            ?? ReadString(user, "username", "email")
            ?? (string.IsNullOrWhiteSpace(fallback?.DisplayName) ? null : fallback!.DisplayName)
            ?? login;
        var role = ReadString(user, "role", "user_role", "position") ?? fallback?.Role ?? "";
        var expiresAt = ReadExpiration(loginPayload)
            ?? ReadJwtExpiration(accessToken)
            ?? throw new JsonException("The server did not provide a valid token expiration.");

        var permissions = PermissionService.ExtractPermissionNames(
            profile.ValueKind == JsonValueKind.Object ? profile : _api.UserPayload);
        if (permissions.Count == 0 && fallback is not null)
            permissions.UnionWith(fallback.Permissions);

        return new UserSession
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt.ToUniversalTime(),
            UserId = userId,
            Login = login,
            DisplayName = displayName,
            Role = role,
            BranchId = _api.ActiveBranchId ?? fallback?.BranchId,
            Permissions = permissions.ToList(),
            // 2026-09-09: CreateSession вызывается ТОЛЬКО после реального успешного обращения к
            // серверу (логин или онлайн-валидация в AutoLoginAsync) — значит связь только что
            // подтверждена, независимо от того, что там с ExpiresAt.
            LastOnlineContactAt = DateTimeOffset.UtcNow,
        };
    }

    private static DateTimeOffset? ReadExpiration(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "expiresAt", "expires_at", "expires" })
        {
            if (!payload.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var date))
                return date;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    private static DateTimeOffset? ReadJwtExpiration(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                return null;
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement source, params string[] names)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var result = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(result))
                    return result;
            }
        }
        return null;
    }

    private static OfflineAuthSession ToLegacySession(UserSession session) => new()
    {
        AccessToken = session.AccessToken,
        RefreshToken = session.RefreshToken,
        UserId = session.UserId,
        Login = session.Login,
        CashierName = session.DisplayName,
        Role = session.Role,
        BranchId = session.BranchId,
        Permissions = session.Permissions.ToList(),
        LastAuthAt = DateTimeOffset.UtcNow,
    };

    /// <summary>2026-10-04: OperationCanceledException, а не только TaskCanceledException — отмену по
    /// бюджету автовхода (AutoLoginServerBudget) во время ожидания замка входа SemaphoreSlim бросает
    /// именно его; настоящая отмена вызывающим (касса закрывается) по-прежнему не сбой связи.</summary>
    private static bool IsNetworkFailure(Exception exception, CancellationToken callerToken) =>
        exception is HttpRequestException ||
        exception is OperationCanceledException && !callerToken.IsCancellationRequested;
}
