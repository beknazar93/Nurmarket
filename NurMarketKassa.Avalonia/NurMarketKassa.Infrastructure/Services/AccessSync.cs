namespace NurMarketKassa.Services;

/// <summary>2026-11-11, владелец: «По поводу ДОСТУПОВ: они даются из сайта, из программы владельца! Сделай синхронизацию с сайтом! И МАРКЕТПЛЕЙС тоже!»
/// Раньше доступы сотрудника (профиль users/profile/, can_view_*) перечитывались только при входе, после возврата связи и при отказе в разделе
/// программы владельца, а купленные на сайте функции (users/company/addons/) — раз в 15 минут; тариф и пакеты компании (users/company/) — только при входе.
/// Выданный на сайте «Склад» или отключённая там функция доходили до кассы после перезапуска.
///
/// Теперь — лёгкая фоновая проверка «с сайта»:
/// • по таймеру раз в <see cref="Interval"/> (90 с ±15 %; 2026-11-13: с ETag/304 на сервере — <see cref="FastInterval"/>, 45 с; в тихом режиме — свёрнуто/простой — ×4, то есть 6 мин): профиль (1 запрос) и платные функции
///   (1 запрос; сервер сам не чаще раза в 20 с); раз в <see cref="CompanyEvery"/> (10 мин) — ещё компания (тариф, пакеты) и купленные темы;
/// • по событию (<see cref="Kick"/>): окно вернулось после отлучки, открыты «Маркетплейс» или «Настройки → Сотрудники», вернулась связь — не чаще раза
///   в <see cref="EventMinGap"/> (20 с);
/// • не идёт во время работы с открытым чеком/оплаты (<see cref="SyncCenter.OpenCheck"/>, <see cref="BusyProbe"/>), при сбое сервера, паузе 429, в автономном
///   режиме и без входа — такт просто пропускается.
/// Изменение применяется на лету: профиль — PermissionRecheck.PermissionsChanged (меню/разделы/кнопки пересобирают окна), функции —
/// ServerAddons.Changed → TariffGate.PacksChanged. Источник правды по функциям — сервер; функция, открытая КЛЮЧОМ, остаётся (UserPreferences.XxxUnlocked =
/// ключ ИЛИ сервер): серверный отзыв снимает только серверную часть.</summary>
public static class AccessSync
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>2026-11-13 (сервер «ev1311»: ETag у профиля и addons, 304 без тела): такт, когда сервер отдаёт ETag у обоих запросов — проверка дешёвая, права доходят быстрее.
    /// Старый сервер (нет ETag) — прежний <see cref="Interval"/>. В тихом режиме (свёрнуто, простой, Android в фоне) оба ×4.</summary>
    public static TimeSpan FastInterval { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Текущий базовый такт: быстрый, если сервер умеет условные запросы (ETag/304) и у профиля, и у addons.</summary>
    public static TimeSpan CurrentInterval() => PermissionRecheck.ProfileEtagSupported && ServerAddons.EtagSupported ? FastInterval : Interval;
    public static TimeSpan EventMinGap { get; set; } = TimeSpan.FromSeconds(20);
    public static TimeSpan CompanyEvery { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Задаёт приложение: обновить компанию (тариф, пакеты) — CompanyInfoService.RefreshAsync с API из контейнера.</summary>
    public static Func<CancellationToken, Task>? CompanyRefresher { get; set; }

    /// <summary>Задаёт приложение: обновить купленные темы/виды магазина (MarketplaceThemesClient), если сервер их поддерживает.</summary>
    public static Func<CancellationToken, Task>? ThemesRefresher { get; set; }

    /// <summary>Задаёт приложение: true — сейчас нельзя отвлекать (идёт оплата). Сам по себе такт ничего на экране не меняет, но перестраивать меню
    /// посреди оплаты не нужно.</summary>
    public static Func<bool>? BusyProbe { get; set; }

    /// <summary>Только для тестов: подмена проверки профиля и функций.</summary>
    internal static Func<string, Task<bool>>? ProfileOverride { get; set; }
    internal static Func<Task<bool>>? AddonsOverride { get; set; }

    private static readonly SyncCenter.KickSignal Wake = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _started;
    private static long _lastCheckTicks;
    private static long _lastCompanyTicks;
    private static string _pendingReason = "";

    /// <summary>Сколько проверок «с сайта» выполнено (для журнала и проверки нагрузки).</summary>
    public static int ChecksDone => Volatile.Read(ref _checksDone);
    private static int _checksDone;

    /// <summary>Запустить фоновую проверку (один раз за запуск; после входа в компанию).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        ServerOutageMonitor.Recovered += () => Kick("connection");
        CompanyEventsFeed.Start(); // 2026-11-13: лента событий компании (features/staff/settings/product/stock) будит эти проверки сразу
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await SyncCenter.WaitAdaptiveAsync(() => SyncCenter.QuietInterval(CurrentInterval()), Wake, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    var reason = Interlocked.Exchange(ref _pendingReason, "");
                    await CheckNowAsync(reason.Length > 0 ? reason : "timer", forceCompany: Interlocked.Exchange(ref _forceCompanyNext, 0) == 1).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"ACCESS_SYNC: сбой цикла: {ex.GetType().Name}: {ex.Message}", "ACCESS_SYNC");
                }
            }
        });
    }

    /// <summary>Проверить сейчас по событию (окно вернулось, открыт «Маркетплейс»/«Сотрудники», вернулась связь). Не чаще раза в <see cref="EventMinGap"/>.</summary>
    public static void Kick(string reason)
    {
        if (Volatile.Read(ref _started) == 0)
            return;
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastCheckTicks) < EventMinGap.Ticks)
            return;
        Interlocked.Exchange(ref _pendingReason, reason);
        Wake.Kick();
    }

    private static int _forceCompanyNext;

    /// <summary>2026-11-13 (лента событий компании: features_changed — тариф/функции): как <see cref="Kick"/>, но следующая проверка читает ещё и компанию (тариф, пакеты) и темы.
    /// Если Kick сейчас отброшен (проверка была меньше <see cref="EventMinGap"/> назад), отметка остаётся до ближайшей проверки.</summary>
    public static void KickCompany(string reason)
    {
        Volatile.Write(ref _forceCompanyNext, 1);
        Kick(reason);
    }

    /// <summary>Сейчас нельзя: нет связи/пауза сервера, открытый чек или оплата.</summary>
    public static bool ShouldSkip()
    {
        if (SyncCenter.ShouldPause() || SyncCenter.OpenCheck)
            return true;
        try { return BusyProbe?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>Одна проверка: профиль (доступы) и платные функции; раз в <see cref="CompanyEvery"/> — компания и темы. Возвращает true, если проверка шла.</summary>
    internal static async Task<bool> CheckNowAsync(string reason, bool forceCompany = false)
    {
        if (ShouldSkip())
            return false;
        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            return false;
        try
        {
            Interlocked.Exchange(ref _lastCheckTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _checksDone);
            // Профиль: при изменении доступов PermissionRecheck сам поднимет PermissionsChanged.
            if (ProfileOverride is { } profile)
                await profile(reason).ConfigureAwait(false);
            else
                await PermissionRecheck.RefreshFromProvidersAsync(ReasonText(reason), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            // Платные функции с сервера (сервер — источник правды; ключевые покупки остаются, см. UserPreferences.XxxUnlocked).
            if (AddonsOverride is { } addons)
                await addons().ConfigureAwait(false);
            else
                await ServerAddons.RefreshAsync(force: true).ConfigureAwait(false);
            var companyDue = forceCompany || DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastCompanyTicks) >= CompanyEvery.Ticks;
            if (companyDue && (CompanyRefresher is not null || ThemesRefresher is not null))
            {
                Interlocked.Exchange(ref _lastCompanyTicks, DateTime.UtcNow.Ticks);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (CompanyRefresher is { } company)
                    await Safe(() => company(cts.Token), "компания").ConfigureAwait(false);
                if (ThemesRefresher is { } themes)
                    await Safe(() => themes(cts.Token), "темы").ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task Safe(Func<Task> step, string what)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"ACCESS_SYNC: {what} не обновлена ({ex.GetType().Name}: {ex.Message}).", "ACCESS_SYNC");
        }
    }

    private static string ReasonText(string reason) => reason switch
    {
        "focus" => "окно снова активно",
        "connection" => "связь восстановлена",
        "marketplace" => "открыт Маркетплейс",
        "employees" => "открыты Сотрудники",
        "peer" => "подсказка программы владельца",
        "events" => "событие компании на сервере",
        _ => "проверка с сайта",
    };

    /// <summary>Только для тестов.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _lastCheckTicks, 0);
        Interlocked.Exchange(ref _lastCompanyTicks, 0);
        Interlocked.Exchange(ref _checksDone, 0);
        Interlocked.Exchange(ref _pendingReason, "");
        Interlocked.Exchange(ref _forceCompanyNext, 0);
        ProfileOverride = null;
        AddonsOverride = null;
        BusyProbe = null;
    }

    /// <summary>Только для тестов: «запущено» без фонового цикла (Kick только будит сигнал).</summary>
    internal static void MarkStartedForTests(bool started) => Interlocked.Exchange(ref _started, started ? 1 : 0);

    /// <summary>Только для тестов: был ли сигнал «проверить сейчас» (и сбросить его).</summary>
    internal static bool TakeWakeForTests() => Wake.WaitAsync(TimeSpan.Zero).GetAwaiter().GetResult();
}
