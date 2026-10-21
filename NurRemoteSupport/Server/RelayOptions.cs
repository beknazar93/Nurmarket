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
    public int LoginFailuresPer15MinPerLogin { get; set; } = 10;
    public int JoinPerMinute { get; set; } = 20;
    public int HostConnectsPerMinutePerIp { get; set; } = 30;

    /// <summary>Доверять X-Forwarded-For/Proto (включать только за своим Caddy, когда порт сервера наружу не открыт).</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>Сколько записей журнала отдавать в /api/sessions по умолчанию.</summary>
    public int JournalPageSize { get; set; } = 100;

    public string OperatorsPath => string.IsNullOrWhiteSpace(OperatorsFile) ? Path.Combine(DataDir, "operators.json") : OperatorsFile!;
    public string JournalPath => string.IsNullOrWhiteSpace(JournalFile) ? Path.Combine(DataDir, "journal.jsonl") : JournalFile!;
}
