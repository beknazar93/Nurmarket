namespace NurRemoteSupport.Server;

/// <summary>
/// Настройки ретранслятора (секция "Relay" в appsettings.json или переменные окружения Relay__*).
/// Секретов здесь по умолчанию нет: ключ подписи токенов берётся из Relay__TokenSecret
/// или создаётся один раз в {DataDir}/token-secret (2026-09-29).
/// </summary>
public sealed class RelayOptions
{
    /// <summary>Папка данных: operators.json, journal.jsonl, token-secret.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Файл операторов. Пусто — {DataDir}/operators.json.</summary>
    public string? OperatorsFile { get; set; }

    /// <summary>Файл журнала. Пусто — {DataDir}/journal.jsonl.</summary>
    public string? JournalFile { get; set; }

    /// <summary>Ключ подписи токенов (base64 или любая строка ≥ 32 символов). Пусто — из файла {DataDir}/token-secret.</summary>
    public string? TokenSecret { get; set; }

    public int TokenLifetimeHours { get; set; } = 12;

    /// <summary>Сколько живёт код+PIN до подключения оператора.</summary>
    public int SessionTtlSeconds { get; set; } = 900;

    /// <summary>Сервер считает отказом, если кассир не ответил (хост сам считает 60 с).</summary>
    public int ConsentTimeoutSeconds { get; set; } = 90;

    public int MaxPinAttempts { get; set; } = 5;

    public int MaxBinaryMessageBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxTextMessageBytes { get; set; } = 256 * 1024;

    /// <summary>Предел очереди отправки одного соединения (байт). Полна — сервер перестаёт читать от отправителя.</summary>
    public int SendQueueBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Сколько секунд можно ждать места в очереди, прежде чем завершить сеанс как застрявший.</summary>
    public int SendStallSeconds { get; set; } = 30;

    public int PingIntervalSeconds { get; set; } = 15;
    public int IdleTimeoutSeconds { get; set; } = 45;
    public int HelloTimeoutSeconds { get; set; } = 10;

    /// <summary>Сколько сеансов (всех состояний) одновременно держит сервер.</summary>
    public int MaxSessions { get; set; } = 2000;

    /// <summary>Не больше стольких ожидающих кодов с одного IP хоста.</summary>
    public int MaxPendingPerIp { get; set; } = 10;

    public int LoginPerMinutePerIp { get; set; } = 10;
    /// <summary>Устарело (2026-10-27): не используется, см. LoginFailuresPer15MinPerLoginIp. Оставлено, чтобы старые relay.json не ломались.</summary>
    public int LoginFailuresPer15MinPerLogin { get; set; } = 10;
    public int JoinPerMinute { get; set; } = 20;
    public int HostConnectsPerMinutePerIp { get; set; } = 30;

    /// <summary>
    /// Устарело: доверять X-Forwarded-For/Proto ЛЮБОМУ отправителю (только если порт сервера наружу не открыт).
    /// 2026-10-27 (В-11): вместо него — TrustedProxies.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// 2026-10-27 (В-11): чьим X-Forwarded-For/Proto верить — адреса и сети через запятую ("172.16.0.0/12,10.0.0.5").
    /// Пусто — только loopback (nginx/Caddy на этой же машине). От остальных заголовки игнорируются: подделка в обход прокси не работает.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>2026-10-27 (С-6): сеанс без ввода оператора (мышь, клавиши, текст, чат…) дольше стольких минут завершается (end idle). 0 — без предела.</summary>
    public double SessionIdleMinutes { get; set; } = 30;

    /// <summary>2026-10-27 (С-6): наибольшая длительность сеанса, минут (end max). 0 — без предела.</summary>
    public double SessionMaxMinutes { get; set; } = 240;

    /// <summary>2026-10-27 (В-6): предел очереди служебных сообщений одного соединения (байт); больше — разрыв.</summary>
    public int ControlQueueBytes { get; set; } = 1024 * 1024;

    /// <summary>2026-10-27 (В-6): наибольший размер текста от кассы до начала сеанса (hello и пр.), байт; двоичное до сеанса — разрыв.</summary>
    public int PreSessionTextBytes { get; set; } = 4 * 1024;

    /// <summary>2026-10-27 (В-6): одновременных соединений кассы с одной сети (IPv4-адрес или IPv6 /64).</summary>
    public int MaxHostConnectionsPerIp { get; set; } = 200;

    /// <summary>2026-10-27 (С-3): ошибок входа с одного логина с ОДНОГО IP за 15 мин до блокировки (чужие IP не мешают).</summary>
    public int LoginFailuresPer15MinPerLoginIp { get; set; } = 10;

    /// <summary>2026-10-27 (С-3): ошибок входа с логина со ВСЕХ IP за 15 мин, после которых ответ задерживается (растёт с числом ошибок). Блокировки нет.</summary>
    public int LoginGlobalFailureDelayAfter { get; set; } = 100;

    /// <summary>2026-10-27 (С-5): наименьшая длина нового пароля оператора.</summary>
    public int MinPasswordLength { get; set; } = 10;

    /// <summary>2026-10-27 (С-7): заметок в минуту от одного оператора.</summary>
    public int NotesPerMinute { get; set; } = 30;

    /// <summary>2026-10-27 (В-5): как часто (секунд) закрываются соединения операторов с отозванным/истёкшим токеном.</summary>
    public int OperatorRecheckSeconds { get; set; } = 60;

    /// <summary>Папка данных относительно папки программы, а не текущей папки (2026-10-27): add-operator из любого места пишет в тот же operators.json.</summary>
    public RelayOptions ResolvePaths()
    {
        if (!Path.IsPathRooted(DataDir)) DataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DataDir));
        return this;
    }

    /// <summary>Сколько записей журнала отдавать в /api/sessions по умолчанию.</summary>
    public int JournalPageSize { get; set; } = 100;

    public string OperatorsPath => string.IsNullOrWhiteSpace(OperatorsFile) ? Path.Combine(DataDir, "operators.json") : OperatorsFile!;
    public string JournalPath => string.IsNullOrWhiteSpace(JournalFile) ? Path.Combine(DataDir, "journal.jsonl") : JournalFile!;
}
