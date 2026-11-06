namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-30, живой баг («Ошибка закрытия смены: Сессия недействительна… Нажмите «Выйти»…»): сервер отозвал вход
/// (вошли с другого ПК/телефона, refresh-токен отклонён) — раньше кассир видел тупиковую ошибку в каждом окне, а журнал
/// засыпали повторы. Теперь об этом узнаём ОДИН раз: флаг, одна запись в журнале и событие <see cref="Expired"/> — окно
/// «Сессия истекла. Войдите снова» и надпись «🔑 Войдите снова» в шапке. Очередь продаж, смена и корзины не трогаются:
/// после входа тем же аккаунтом всё уходит на сервер само. Сбрасывается успешным входом (<see cref="Reset"/>).
/// </summary>
public static class SessionExpiryMonitor
{
    private static int _expired;

    /// <summary>Сессия недействительна: ждём повторного входа.</summary>
    public static bool IsExpired => Volatile.Read(ref _expired) != 0;

    /// <summary>Сессия объявлена недействительной (один раз до следующего входа). Вызывается в фоновом потоке.</summary>
    public static event Action? Expired;

    /// <summary>Состояние изменилось (объявлено или сброшено) — обновить надпись в шапке.</summary>
    public static event Action? Changed;

    // 2026-11-06 (разбор журнала клиента, баг 3): «Позже» в окне «Сессия истекла» раньше значило «навсегда» — владелец час работал вхолостую и 170 раз нажимал «Сохранить»
    // с тем же отказом, а журнал засыпали повторы. Теперь пока сессия недействительна: напоминание не чаще раза в 10 минут (таймер), сразу при действии, упавшем из-за
    // потери сессии, но не чаще раза в 2 минуты; одинаковые предупреждения пишутся раз в 10 минут на источник. Время — через Clock (тесты без Thread.Sleep).
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ActionReminderInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TimerPeriod = TimeSpan.FromSeconds(30);

    /// <summary>Часы монитора (UTC); подменяются только в тестах.</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    private static long _lastReminderTicks;
    private static Timer? _timer;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTime> LoggedAt = new(StringComparer.Ordinal);

    /// <summary>Пора снова спросить «Войдите снова». Аргумент — вызвано действием (true) или таймером (false); окно приложения (SessionExpiredPrompt) показывает вопрос.</summary>
    public static event Action<bool>? Reminder;

    /// <summary>Объявляет сессию недействительной. true — это первое объявление (повторные ничего не делают и не пишут в журнал).</summary>
    public static bool Report(string source)
    {
        if (Interlocked.Exchange(ref _expired, 1) != 0)
            return false;
        Interlocked.Exchange(ref _lastReminderTicks, Clock().Ticks); // первое окно показывает событие Expired — отсчёт напоминаний от него
        StartTimer();
        // 2026-11-06: если время входа известно — причина в журнале («срок входа истёк (прошло N ч с входа)»), без токенов.
        var cause = RefreshExpiryWatcher.Current.DescribeLoss();
        PosLogger.Log($"Сессия недействительна ({source}){(cause is null ? "" : ": " + cause)} — нужен повторный вход; очередь и данные сохранены.", "AUTH");
        Raise(Changed);
        Raise(Expired);
        return true;
    }

    /// <summary>Успешный вход (или восстановление сессии): снова можно объявлять.</summary>
    public static void Reset()
    {
        if (Interlocked.Exchange(ref _expired, 0) != 0)
        {
            StopTimer();
            PosLogger.Log("Сессия восстановлена — вход выполнен.", "AUTH");
            Raise(Changed);
        }
    }

    /// <summary>Только для тестов: чистое состояние без событий и записей в журнал.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _expired, 0);
        StopTimer();
        Interlocked.Exchange(ref _lastReminderTicks, 0);
        lock (Sync)
            LoggedAt.Clear();
        Clock = () => DateTime.UtcNow;
    }

    private static void StartTimer()
    {
        lock (Sync)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => { try { RemindIfDue(false); } catch { /* таймер не должен ронять кассу */ } }, null, TimerPeriod, TimerPeriod);
        }
    }

    private static void StopTimer()
    {
        lock (Sync)
        {
            _timer?.Dispose();
            _timer = null;
            LoggedAt.Clear(); // после входа новая потеря сессии снова пишется в журнал с первой записи
        }
    }

    /// <summary>Можно ли сейчас напомнить: сессия недействительна и прошло ≥ 10 минут (по таймеру) либо ≥ 2 минут (после действия, упавшего из-за сессии) с прошлого вопроса.
    /// true — «слот» занят (повторный вызов сразу вернёт false); сам вопрос показывает подписчик <see cref="Reminder"/>.</summary>
    public static bool RemindIfDue(bool fromAction)
    {
        if (!IsExpired)
            return false;
        var need = (fromAction ? ActionReminderInterval : ReminderInterval).Ticks;
        var now = Clock().Ticks;
        while (true)
        {
            var last = Interlocked.Read(ref _lastReminderTicks);
            if (now - last < need)
                return false;
            if (Interlocked.CompareExchange(ref _lastReminderTicks, now, last) == last)
                break;
        }
        Raise(() => Reminder?.Invoke(fromAction));
        return true;
    }

    /// <summary>Общий вызов из окон: операция упала из-за потери сессии — напомнить «Войдите снова» (не чаще раза в 2 минуты). false — это другая ошибка или напоминание рано.</summary>
    public static bool RemindOnSessionLost(Exception? ex) => IsSessionLost(ex) && RemindIfDue(true);

    /// <summary>Окно показало вопрос само (например, при отказе в правах): следующее напоминание отсчитывается от этого момента.</summary>
    public static void NoteReminderShown() => Interlocked.Exchange(ref _lastReminderTicks, Clock().Ticks);

    /// <summary>Вопрос показывать сейчас неудобно (кассир печатает): повторить попытку таймером через минуту, а не ждать полные 10 минут.</summary>
    public static void DeferReminder() =>
        Interlocked.Exchange(ref _lastReminderTicks, Clock().Ticks - (ReminderInterval - TimeSpan.FromMinutes(1)).Ticks);

    /// <summary>Запись в журнал не чаще раза в 10 минут на источник (повторы «…не удалась (Сессия недействительна…)» засоряли журнал). true — запись сделана.</summary>
    public static bool LogOnce(string source, string message, string level = "WARNING")
    {
        var now = Clock();
        lock (Sync)
        {
            if (LoggedAt.TryGetValue(source, out var at) && now - at < LogInterval)
                return false;
            LoggedAt[source] = now;
        }
        PosLogger.Log(message, level);
        return true;
    }

    /// <summary>Запись о сбое: если причина — потеря сессии, то раз в 10 минут на источник (<see cref="LogOnce"/>), иначе как обычно.</summary>
    public static void LogFailure(string source, Exception ex, string message, string level = "WARNING")
    {
        if (IsSessionLost(ex))
            LogOnce(source, message, level);
        else
            PosLogger.Log(message, level);
    }

    /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): сессию теряет только настоящий отказ входа: клиент уже объявил её недействительной (refresh отклонён,
    /// токена нет) либо ошибка — наш текст «Сессия недействительна». Голый 401, пришедший при живом refresh-токене (в том числе после удачного обновления
    /// токена: нет прав на конкретный запрос), сессию недействительной не объявляет — раньше CashShiftService объявлял её на любой 401.</summary>
    public static bool IsSessionLost(Exception? ex) =>
        IsAuthFailure(ex) && (IsExpired || (ex is ApiException api && HasSessionLostText(api.Message)));

    /// <summary>Ошибка — отказ по сессии (401 или наш текст «Сессия недействительна»).</summary>
    public static bool IsAuthFailure(Exception? ex) =>
        ex is ApiException { StatusCode: 401 }
        || (ex is ApiException api && HasSessionLostText(api.Message));

    /// <summary>2026-11-06: русский префикс нашего текста потери входа. Новый текст — «Сессия недействительна: срок входа истёк…», старый — «Сессия недействительна (часто из‑за входа…»;
    /// оба узнаются (старый мог остаться в кэше/очереди). Текст в окнах пользователь видит переведённым (Tr.T), а не этот сырой.</summary>
    public static bool HasSessionLostText(string? message) =>
        message is not null && message.StartsWith("Сессия недействительна", StringComparison.Ordinal);

    /// <summary>2026-11-06 (Б-8): надпись в шапке программы владельца, когда сессия потеряна («🔑 Войдите снова», как в кассе). null — сессия в порядке.</summary>
    public static string? HeaderLabel => IsExpired
        ? Tr.T("🔑 Войдите снова", "🔑 Кайра кириңиз", "🔑 Sign in again", "🔑 Yeniden giriş yapın", "🔑 Qaytadan kiring")
        : null;

    /// <summary>2026-11-06 (Б-8): подпись «Сводки» владельца при потерянной сессии вместо «Сервер недоступен · по данным касс в локальной сети». null — сессия в порядке.</summary>
    public static string? SummaryHeaderText => IsExpired
        ? Tr.T("Войдите снова — срок входа истёк", "Кайра кириңиз — кирүүнүн мөөнөтү бүттү", "Sign in again — the sign-in has expired",
            "Yeniden giriş yapın — oturum süresi doldu", "Qaytadan kiring — kirish muddati tugadi")
        : null;

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Обработчик сессии упал: {ex.Message}", "WARNING"); }
    }
}
