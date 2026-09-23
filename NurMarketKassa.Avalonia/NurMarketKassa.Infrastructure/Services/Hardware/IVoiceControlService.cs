using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Результат разобранной и (частично) разрешённой голосовой команды — передаётся
/// подписчикам CommandRecognized, которые уже решают, что с ним делать (добавить в корзину,
/// показать список кандидатов на уточнение, дёрнуть команду удаления/очистки и т.д.).
/// Product != null — однозначное совпадение; Candidates.Count > 1 без Product — неоднозначность,
/// нужно уточнение у кассира; Candidates.Count == 0 — товар не найден.</summary>
public sealed record VoiceCommandResult
{
    public VoiceIntent Intent { get; init; } = VoiceIntent.Unknown;
    public CatalogProductTileVm? Product { get; init; }
    public IReadOnlyList<CatalogProductTileVm> Candidates { get; init; } = [];
    public double Quantity { get; init; } = 1;
    public VoiceUnitKind UnitKind { get; init; } = VoiceUnitKind.None;
    public string ProductQuery { get; init; } = "";
    public string RawText { get; init; } = "";

    /// <summary>Голосовой замок (2026-09-05, SpeakerVerificationService) — null, если проверка не
    /// применялась вовсе (замок выключен, голос не зарегистрирован, или это не команда — Intent
    /// не про добавление/поиск); true — голос совпал с зарегистрированным; false — не совпал
    /// (подписчики на CommandRecognized должны в этом случае НЕ выполнять команду и показать
    /// предупреждение, а не молча игнорировать).</summary>
    public bool? VoiceMatched { get; init; }
}

public interface IVoiceControlService : IDisposable
{
    bool IsListening { get; }

    string Status { get; }

    /// <summary>Запускает (или перезапускает) прослушивание — самогасится, если голосовое
    /// управление выключено в UserPreferences.VoiceControlEnabled (см. ComWeightScaleService —
    /// тот же принцип: Start() всегда безопасно вызывать, состояние читается из настроек).</summary>
    void Start();

    void Stop();

    event Action<VoiceCommandResult>? CommandRecognized;

    /// <summary>Каждая финализированная фраза Vosk целиком, включая ту, что не содержала
    /// ключевого слова — для панели диагностики/"регистрации голоса" в настройках, чтобы
    /// кассир видел вживую, что именно слышит касса, не копаясь в файле логов.</summary>
    event Action<string>? RawTextRecognized;

    /// <summary>Голосовой замок (2026-09-05) — зарегистрирован ли сейчас голос (независимо от
    /// того, включена ли ПРОВЕРКА по нему, UserPreferences.VoiceLockEnabled).</summary>
    bool IsVoiceLockEnrolled { get; }

    /// <summary>Записывает sampleCount коротких фраз (по одному законченному произнесённому
    /// предложению каждая, определяется той же паузой/тишиной, что обычно завершает голосовую
    /// команду — не фиксированная длительность) как образец голоса кассира, и включает голосовой
    /// замок. Останавливает обычное прослушивание команд на время записи и восстанавливает его
    /// (если было включено) по завершении, независимо от результата.
    /// onSampleRecorded(current, total) — для UI-прогресса ("Записано 2 из 3").</summary>
    Task<bool> EnrollVoiceAsync(int sampleCount, Action<int, int>? onSampleRecorded, CancellationToken ct = default);

    /// <summary>Удаляет запись голоса и выключает голосовой замок.</summary>
    void ClearVoiceEnrollment();
}
