using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06 (ответ Умара, сервер NurCRM): refresh-токен живёт 7 ДНЕЙ от последнего входа ПО ПАРОЛЮ, не продлевается и не отзывается
/// другим входом (simplejwt, ротации нет). Наш журнал это подтвердил: вход по паролю 28.10, отказ 04.11 ровно через 7 суток —
/// касса молча жила до внезапной потери входа. Теперь касса и программа владельца заранее знают срок:
/// — читают <c>exp</c> из refresh-токена (JWT; БЕЗ проверки подписи, токен никуда не пишется — только время);
/// — нет JWT/exp: считают 7 суток от запомненного времени входа по паролю (<see cref="NurMarketKassa.Core.Contracts.UserSession.PasswordLoginAt"/>);
/// — остаток неизвестен — молчим;
/// — остаток ≤ 24 ч: ненавязчивая надпись «🔑 Вход истекает через N ч» в шапке (<see cref="WarningText"/>, событие <see cref="Changed"/>);
/// — остаток ≤ 3 ч: ОДИН раз за запуск мягкое окно-вопрос (<see cref="PromptDue"/>), не при чеке с товарами и не во время оплаты
///   (<see cref="IsBusy"/> → повтор через 5 минут). Окно показывает RefreshExpiryPrompt в проекте приложения.
/// Пересчёт — при старте и раз в 30 минут (чистая арифметика, сети нет). Нет сети — не ошибка. Часы подменяются в тестах.
/// </summary>
public sealed class RefreshExpiryWatcher : IDisposable
{
    /// <summary>Срок жизни refresh-токена на сервере NurCRM, если в токене нет <c>exp</c>.</summary>
    public static readonly TimeSpan DefaultRefreshLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan WarnThreshold = TimeSpan.FromHours(24);
    public static readonly TimeSpan PromptThreshold = TimeSpan.FromHours(3);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan BusyRetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Единственный экземпляр приложения (вход его настраивает, окна подписываются).</summary>
    public static RefreshExpiryWatcher Current { get; } = new();

    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _autoTimer;
    private readonly object _sync = new();
    private DateTimeOffset? _expiresAt;
    private DateTimeOffset? _loggedInAt;
    private bool _promptShown;
    private DateTimeOffset _nextPromptAt = DateTimeOffset.MinValue;
    private bool _busyRetryPending;
    private string? _lastWarning;
    private Timer? _timer;
    private int _disposed;

    public RefreshExpiryWatcher(Func<DateTimeOffset>? clock = null, Func<bool>? isBusy = null, bool autoTimer = true)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        IsBusy = isBusy;
        _autoTimer = autoTimer;
    }

    /// <summary>Сейчас нельзя прерывать кассира: в чеке есть товары или идёт оплата. Задаёт окно кассы; вызывается вне замков, может быть из фонового потока.</summary>
    public Func<bool>? IsBusy { get; set; }

    /// <summary>Надпись в шапке изменилась (появилась, обновилась, исчезла).</summary>
    public event Action? Changed;

    /// <summary>Пора показать мягкое окно «Через N ч вход истечёт» (один раз за запуск). Есть подписчик — окно покажет он.</summary>
    public event Action? PromptDue;

    public DateTimeOffset? ExpiresAt { get { lock (_sync) return _expiresAt; } }
    public DateTimeOffset? LoggedInAt { get { lock (_sync) return _loggedInAt; } }

    /// <summary>Сколько осталось до конца входа; null — неизвестно. Может быть отрицательным (срок по времени вышел).</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_sync)
                return _expiresAt is { } exp ? exp - _clock() : null;
        }
    }

    /// <summary>Остаток известен, положителен и ≤ 24 ч: показываем надпись и предлагаем войти заново (при закрытии смены и выходе).</summary>
    public bool IsWarning => Remaining is { } r && r > TimeSpan.Zero && r <= WarnThreshold;

    /// <summary>Часов до конца входа для надписи («через N ч»): вверх, не меньше 1; 0 — остаток неизвестен или срок вышел.</summary>
    public int HoursLeft => Remaining is { } r && r > TimeSpan.Zero ? Math.Max(1, (int)Math.Ceiling(r.TotalHours)) : 0;

    /// <summary>Надпись для шапки или null, пока остаток больше суток, неизвестен или срок уже вышел (дальше работает SessionExpiryMonitor).</summary>
    public string? WarningText => IsWarning ? WarningTextFor(HoursLeft) : null;

    public static string WarningTextFor(int hours) =>
        Tr.T($"🔑 Вход истекает через {hours} ч — войдите заново",
            $"🔑 Кирүүнүн мөөнөтү {hours} сааттан кийин бүтөт — кайра кириңиз",
            $"🔑 Sign-in expires in {hours} h — sign in again",
            $"🔑 Oturum {hours} saat içinde sona erecek — yeniden giriş yapın",
            $"🔑 Kirish muddati {hours} soatdan keyin tugaydi — qaytadan kiring");

    public static string PromptTitle() =>
        Tr.T("Вход скоро истечёт", "Кирүүнүн мөөнөтү бүтөөрүнө жакын", "Sign-in expires soon", "Oturum yakında sona erecek", "Kirish muddati tez orada tugaydi");

    public static string PromptMessage(int hours) =>
        Tr.T($"Через {hours} ч вход истечёт. Войдите заново сейчас, чтобы кассу не остановило посреди смены. Продажи, смена и корзины сохранятся.",
            $"{hours} сааттан кийин кирүүнүн мөөнөтү бүтөт. Касса смена ортосунда токтоп калбашы үчүн азыр кайра кириңиз. Сатуулар, смена жана себеттер сакталат.",
            $"Your sign-in will expire in {hours} h. Sign in again now so the till doesn't stop in the middle of a shift. Sales, the shift and carts will be kept.",
            $"Oturumunuz {hours} saat sonra sona erecek. Kasanın vardiya ortasında durmaması için şimdi yeniden giriş yapın. Satışlar, vardiya ve sepetler korunur.",
            $"Kirish muddati {hours} soatdan keyin tugaydi. Kassa smena o'rtasida to'xtab qolmasligi uchun hozir qaytadan kiring. Sotuvlar, smena va savatlar saqlanadi.");

    public static string PromptSignInText() =>
        Tr.T("Войти заново", "Кайра кирүү", "Sign in again", "Yeniden giriş yap", "Qaytadan kirish");

    /// <summary>Настройка после входа (по паролю, автовход, автономный вход): новый refresh-токен и, если известно, время входа по паролю.
    /// Токен нигде не сохраняется и не пишется в журнал — читается только <c>exp</c>/<c>iat</c>. В журнал уходит одна строка о сроке.</summary>
    public void Configure(string? refreshToken, DateTimeOffset? passwordLoginAt)
    {
        var (exp, iat) = ReadJwtTimes(refreshToken);
        var loggedIn = passwordLoginAt ?? iat;
        var expires = exp ?? (loggedIn is { } at ? at + DefaultRefreshLifetime : (DateTimeOffset?)null);
        string? logLine = null;
        lock (_sync)
        {
            _expiresAt = expires;
            _loggedInAt = loggedIn;
            // Вошли заново (срок ушёл далеко вперёд) — вопрос снова разрешён для следующего срока.
            if (expires is not { } e || e - _clock() > PromptThreshold)
            {
                _promptShown = false;
                _busyRetryPending = false;
                _nextPromptAt = DateTimeOffset.MinValue;
            }
            logLine = expires is { } ex
                ? $"AUTH: вход действует до {ex.ToLocalTime():dd.MM.yyyy HH:mm} (осталось {Math.Max(0, (int)Math.Floor((ex - _clock()).TotalHours))} ч)"
                : "AUTH: срок входа неизвестен — предупреждение о конце входа не работает.";
        }
        PosLogger.Log(logLine, "AUTH");
        StartTimer();
        Tick();
    }

    /// <summary>2026-11-06: сервер выдал НОВЫЙ refresh-токен (ротация — вход продлевается при работе). Срок берём из его <c>exp</c>; время входа по паролю не меняется;
    /// нет exp (не JWT) — срок остаётся прежним. Без записи в журнал (случается каждые ~15 минут).</summary>
    public void RefreshTokenRotated(string? refreshToken)
    {
        var (exp, _) = ReadJwtTimes(refreshToken);
        if (exp is not { } expires)
            return;
        lock (_sync)
        {
            _expiresAt = expires;
            if (expires - _clock() > PromptThreshold)
            {
                _promptShown = false;
                _busyRetryPending = false;
                _nextPromptAt = DateTimeOffset.MinValue;
            }
        }
        StartTimer();
        Tick();
    }

    /// <summary>Выход из учётной записи или отказ входа: срок не известен, надпись исчезает.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _expiresAt = null;
            _loggedInAt = null;
            _busyRetryPending = false;
        }
        StopTimer();
        Tick();
    }

    /// <summary>Пересчёт: обновить надпись и, если пора, позвать окно-вопрос. Вызывается при старте и раз в 30 минут (или через 5 минут, если вопрос отложен).</summary>
    public void Tick()
    {
        try
        {
            string? text;
            bool changed;
            bool askBusy;
            lock (_sync)
            {
                text = WarningText;
                changed = !string.Equals(text, _lastWarning, StringComparison.Ordinal);
                _lastWarning = text;
                askBusy = !_promptShown && _expiresAt is { } e && e - _clock() is var r && r > TimeSpan.Zero && r <= PromptThreshold && _clock() >= _nextPromptAt;
            }

            if (changed)
                Raise(Changed);

            if (askBusy)
            {
                bool busy;
                try { busy = IsBusy?.Invoke() == true; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { busy = false; }

                if (busy)
                {
                    lock (_sync)
                    {
                        _nextPromptAt = _clock() + BusyRetryInterval;
                        _busyRetryPending = true;
                    }
                }
                else if (PromptDue is { } handler)
                {
                    lock (_sync)
                    {
                        _promptShown = true;
                        _busyRetryPending = false;
                    }
                    Raise(handler);
                }
            }
        }
        finally
        {
            Reschedule();
        }
    }

    /// <summary>Для журнала при потере сессии: «срок входа истёк (прошло N ч с входа)». Время входа неизвестно — null (в журнале останется прежний текст).</summary>
    public string? DescribeLoss()
    {
        DateTimeOffset? at;
        DateTimeOffset? exp;
        lock (_sync)
        {
            at = _loggedInAt;
            exp = _expiresAt;
        }
        if (at is not { } loggedIn)
            return null;
        var hours = Math.Max(0, (int)Math.Floor((_clock() - loggedIn).TotalHours));
        return exp is { } e && _clock() >= e - TimeSpan.FromMinutes(1)
            ? $"срок входа истёк (прошло {hours} ч с входа)"
            : $"прошло {hours} ч с входа, срок по токену ещё не вышел — сервер отозвал вход или пользователя";
    }

    /// <summary>Разбор <c>exp</c> и <c>iat</c> из JWT без проверки подписи. Не JWT, пусто или испорчено — (null, null). Токен не логируется.</summary>
    public static (DateTimeOffset? Exp, DateTimeOffset? Iat) ReadJwtTimes(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return (null, null);
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                return (null, null);
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
            return (ReadUnix(document.RootElement, "exp"), ReadUnix(document.RootElement, "iat"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private static DateTimeOffset? ReadUnix(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var unix) && unix > 0 && unix < 253402300799L
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    private void StartTimer()
    {
        if (!_autoTimer || Volatile.Read(ref _disposed) != 0)
            return;
        lock (_sync)
            _timer ??= new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Reschedule()
    {
        lock (_sync)
        {
            if (_timer is null || Volatile.Read(ref _disposed) != 0)
                return;
            try { _timer.Change(_busyRetryPending ? BusyRetryInterval : CheckInterval, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { /* таймер уже остановлен */ }
        }
    }

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Обработчик срока входа упал: {ex.GetType().Name}", "WARNING"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            StopTimer();
    }
}
