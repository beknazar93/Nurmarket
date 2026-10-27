using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.PhoneLink;

/// <summary>2026-10-27, пакет К-П2 (ТЗ «Мобилка и касса», путь B): сопряжение телефонов с кассой.
/// Каждое нажатие «Подключить телефон» создаёт НОВЫЙ случайный ключ (ожидание 5 минут, только он один). Первый hello
/// с верной подписью этим ключом привязывает ключ к id телефона (одноразово). Дальше у телефона свой постоянный ключ.
/// Список телефонов — файл phone-link.json в папке data (id, имя, когда на связи; без ключей), сами ключи — отдельным
/// файлом phone-link.keys, зашифрованным через <see cref="PortableSecret"/> (Windows — DPAPI, Linux — AES-GCM).
/// Папка data переезжает при смене компании (AccountDataIsolation); дополнительно сверяется id компании.</summary>
public sealed class PhoneLinkPairing
{
    public sealed class PhoneRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime PairedUtc { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public bool Revoked { get; set; }
        /// <summary>2026-10-27, ревью Ф-5: номера последних принятых сканов телефона (seq) — переживают перезапуск кассы, чтобы повтор
        /// запроса после перезапуска не добавил товар второй раз. Не больше <see cref="ScanSeqKeep"/> последних.</summary>
        public List<long>? ScanSeqs { get; set; }
    }

    /// <summary>Сколько последних seq сканов помнит касса на каждый телефон (очередь телефона — не больше 50).</summary>
    public const int ScanSeqKeep = 200;

    public sealed class State
    {
        public bool Enabled { get; set; }
        public string? CompanyId { get; set; }
        public List<PhoneRecord> Phones { get; set; } = new();
    }

    /// <summary>Как касса относится к телефону с таким id.</summary>
    public enum Standing { Unknown, Paired, Revoked }

    /// <summary>Сведения о телефоне для списка в окне.</summary>
    public sealed record PhoneInfo(string Id, string Name, DateTime PairedUtc, DateTime? LastSeenUtc);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NurMarketKassa.PhoneLink.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan LastSeenSaveEvery = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly string _jsonPath;
    private readonly string _keysPath;
    private readonly Func<string?> _companyId;
    private readonly Func<DateTime> _utcNow;
    private State? _state;
    private Dictionary<string, string>? _keys; // id телефона → ключ (base64url)
    private DateTime _lastSavedUtc = DateTime.MinValue;
    /// <summary>2026-10-27, ревью Ф-1: для какой компании прочитаны <see cref="_state"/> и <see cref="_keys"/> (смена — перечитать с диска).</summary>
    private string? _loadedFor;
    private bool _loadedOnce;

    // Ожидающее сопряжение: живёт только в памяти.
    private string? _pendingKey;
    private DateTime _pendingUntilUtc;

    public PhoneLinkPairing(string dataDirectory, Func<string?>? companyId = null, Func<DateTime>? utcNow = null)
    {
        _jsonPath = Path.Combine(dataDirectory, "phone-link.json");
        _keysPath = Path.Combine(dataDirectory, "phone-link.keys");
        _companyId = companyId ?? (() => null);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Изменился список телефонов или настройка (подключён, отключён, включён приём).</summary>
    public event Action? Changed;

    /// <summary>Телефон привязан (id, имя) — окно сопряжения показывает «Телефон “…” подключён».</summary>
    public event Action<string, string>? Paired;

    // ------------------------------------------------------------------ состояние на диске

    /// <summary>2026-10-27, ревью Ф-1: компания сменилась — кэш ключей и список телефонов прежней компании сбрасываются (телефоны компании A
    /// не должны попасть в чеки компании B), ожидающее сопряжение отменяется; данные перечитываются с диска (папку data при смене
    /// компании подменяет AccountDataIsolation). Вызывать под замком.</summary>
    private void EnsureCompanyCurrent()
    {
        if (!_loadedOnce)
            return;
        var company = _companyId();
        if (string.Equals(_loadedFor, company, StringComparison.Ordinal))
            return;
        PosLogger.Log("PHONELINK: компания сменилась — список телефонов и ключи перечитываются.", "INFO");
        _state = null;
        _keys = null;
        _pendingKey = null;
    }

    /// <summary>Сбросить кэш и перечитать список телефонов и ключи с диска (смена кассира или компании). Ожидающее сопряжение отменяется.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _state = null;
            _keys = null;
            _pendingKey = null;
        }
        RaiseChanged();
    }

    private State Load()
    {
        EnsureCompanyCurrent();
        if (_state is not null)
            return _state;
        var company = _companyId();
        _loadedFor = company;
        _loadedOnce = true;
        try
        {
            if (File.Exists(_jsonPath))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_jsonPath, Encoding.UTF8), Json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: список телефонов не прочитан ({ex.Message}) — начинаем с пустого, приём выключен.", "WARNING");
        }
        _state ??= new State();
        _state.Phones ??= new List<PhoneRecord>();
        // Данные другой компании (папку не переключили) — не используем.
        if (!string.IsNullOrWhiteSpace(_state.CompanyId) && !string.IsNullOrWhiteSpace(company)
            && !string.Equals(_state.CompanyId, company, StringComparison.Ordinal))
        {
            PosLogger.Log("PHONELINK: список телефонов принадлежит другой компании — игнорируется.", "WARNING");
            _state = new State();
            _keys = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return _state;
    }

    private Dictionary<string, string> LoadKeys()
    {
        EnsureCompanyCurrent();
        if (_keys is not null)
            return _keys;
        Load(); // мог сбросить _keys при чужой компании
        if (_keys is not null)
            return _keys;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_keysPath))
            {
                var plain = PortableSecret.Unprotect(File.ReadAllBytes(_keysPath), Entropy);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain), Json);
                if (map is not null)
                    foreach (var (id, key) in map)
                        keys[id] = key;
            }
        }
        catch (Exception ex)
        {
            // Ключи не расшифровались (другой пользователь Windows, повреждение) — телефоны придётся подключить заново.
            PosLogger.Log($"PHONELINK: ключи телефонов не прочитаны ({ex.GetType().Name}) — подключите телефоны заново.", "WARNING");
        }
        return _keys = keys;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }

    private bool SaveLocked(bool withKeys)
    {
        try
        {
            var state = Load();
            state.CompanyId = _companyId() ?? state.CompanyId;
            WriteAtomic(_jsonPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json)));
            if (withKeys)
            {
                var keysJson = JsonSerializer.Serialize(LoadKeys(), Json);
                WriteAtomic(_keysPath, PortableSecret.Protect(Encoding.UTF8.GetBytes(keysJson), Entropy));
            }
            _lastSavedUtc = _utcNow();
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: не удалось записать данные связи с телефонами ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return false;
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"PHONELINK: подписчик не обработал изменение ({ex.Message}).", "WARNING"); }
    }

    // ------------------------------------------------------------------ включение приёма

    /// <summary>Принимать запросы с телефонов (по умолчанию выключено).</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
                return Load().Enabled;
        }
    }

    public bool SetEnabled(bool on)
    {
        bool ok;
        lock (_gate)
        {
            var state = Load();
            if (state.Enabled == on)
                return true;
            state.Enabled = on;
            if (!on)
                _pendingKey = null; // выключили приём — ожидание сопряжения тоже закрываем
            ok = SaveLocked(withKeys: false);
            if (!ok)
                state.Enabled = !on;
        }
        if (ok)
            RaiseChanged();
        return ok;
    }

    // ------------------------------------------------------------------ сопряжение

    /// <summary>Новое ожидание сопряжения: свежий случайный ключ, действует 5 минут; прежний ожидающий ключ отменяется.</summary>
    public string BeginPairing()
    {
        lock (_gate)
        {
            _pendingKey = PhoneLinkProtocol.NewKey();
            _pendingUntilUtc = _utcNow() + PhoneLinkProtocol.PairingLifetime;
            return _pendingKey;
        }
    }

    public void CancelPairing()
    {
        lock (_gate)
            _pendingKey = null;
    }

    /// <summary>Есть ли действующее ожидание и когда оно закончится.</summary>
    public bool HasPending(out DateTime untilUtc)
    {
        lock (_gate)
        {
            untilUtc = _pendingUntilUtc;
            return _pendingKey is not null && _utcNow() <= _pendingUntilUtc;
        }
    }

    /// <summary>Ключ ожидающего сопряжения (для проверки подписи первого hello) или null.</summary>
    public byte[]? PendingKeyBytes()
    {
        lock (_gate)
        {
            if (_pendingKey is null || _utcNow() > _pendingUntilUtc)
            {
                _pendingKey = null;
                return null;
            }
            return PhoneLinkProtocol.TryBase64UrlDecode(_pendingKey, out var bytes) ? bytes : null;
        }
    }

    /// <summary>Привязать ключ ожидающего сопряжения к телефону. Одноразово: после успеха ожидание закрывается.
    /// false — ожидания уже нет (истекло или использовано другим телефоном) либо не удалось сохранить.</summary>
    public bool CompletePairing(string phoneId, string name)
    {
        string? savedName;
        lock (_gate)
        {
            if (_pendingKey is null || _utcNow() > _pendingUntilUtc)
            {
                _pendingKey = null;
                return false;
            }
            var state = Load();
            var keys = LoadKeys();
            var previousKey = keys.TryGetValue(phoneId, out var oldKey) ? oldKey : null;
            var record = state.Phones.FirstOrDefault(p => p.Id == phoneId);
            var previousRecord = record is null ? null : new PhoneRecord
            {
                Id = record.Id, Name = record.Name, PairedUtc = record.PairedUtc, LastSeenUtc = record.LastSeenUtc, Revoked = record.Revoked,
            };
            var now = _utcNow();
            savedName = CleanName(name);
            if (record is null)
            {
                record = new PhoneRecord { Id = phoneId };
                state.Phones.Add(record);
            }
            record.Name = savedName;
            record.PairedUtc = now;
            record.LastSeenUtc = now;
            record.Revoked = false;
            keys[phoneId] = _pendingKey;
            if (!SaveLocked(withKeys: true))
            {
                // Не записали — откатываем, ожидание остаётся (можно повторить).
                if (previousKey is null) keys.Remove(phoneId); else keys[phoneId] = previousKey;
                if (previousRecord is null) state.Phones.Remove(record);
                else { record.Name = previousRecord.Name; record.PairedUtc = previousRecord.PairedUtc; record.LastSeenUtc = previousRecord.LastSeenUtc; record.Revoked = previousRecord.Revoked; }
                return false;
            }
            _pendingKey = null;
        }
        RaiseChanged();
        try { Paired?.Invoke(phoneId, savedName); }
        catch (Exception ex) { PosLogger.Log($"PHONELINK: подписчик не обработал подключение ({ex.Message}).", "WARNING"); }
        return true;
    }

    // ------------------------------------------------------------------ проверка телефона при запросах

    public Standing GetStanding(string phoneId, out byte[]? key)
    {
        key = null;
        lock (_gate)
        {
            var record = Load().Phones.FirstOrDefault(p => p.Id == phoneId);
            if (record is null)
                return Standing.Unknown;
            if (record.Revoked)
                return Standing.Revoked;
            if (LoadKeys().TryGetValue(phoneId, out var text) && PhoneLinkProtocol.TryBase64UrlDecode(text, out var bytes))
            {
                key = bytes;
                return Standing.Paired;
            }
            return Standing.Unknown; // запись есть, ключа нет — как незнакомый (нужно подключить заново)
        }
    }

    public string NameOf(string phoneId)
    {
        lock (_gate)
            return Load().Phones.FirstOrDefault(p => p.Id == phoneId)?.Name ?? "";
    }

    /// <summary>Телефон на связи (и, если пришло, новое имя). Запись на диск — не чаще раза в 30 секунд.</summary>
    public void Touch(string phoneId, string? newName = null)
    {
        var changed = false;
        lock (_gate)
        {
            var record = Load().Phones.FirstOrDefault(p => p.Id == phoneId);
            if (record is null || record.Revoked)
                return;
            var now = _utcNow();
            record.LastSeenUtc = now;
            if (!string.IsNullOrWhiteSpace(newName) && CleanName(newName) is { Length: > 0 } clean && clean != record.Name)
            {
                record.Name = clean;
                changed = true;
                SaveLocked(withKeys: false);
            }
            else if (now - _lastSavedUtc > LastSeenSaveEvery)
            {
                SaveLocked(withKeys: false);
            }
        }
        if (changed)
            RaiseChanged();
    }

    /// <summary>2026-10-27, ревью Ф-5: этот скан (seq) телефона уже принят — в том числе до перезапуска кассы.</summary>
    public bool IsScanSeqKnown(string phoneId, long seq)
    {
        lock (_gate)
            return Load().Phones.FirstOrDefault(p => p.Id == phoneId)?.ScanSeqs?.Contains(seq) == true;
    }

    /// <summary>2026-10-27, ревью Ф-5: запомнить принятый скан (seq) телефона на диске. false — такой seq уже был (повтор).
    /// Не записалось на диск — seq всё равно запомнен до перезапуска (запись повторится при следующем скане).</summary>
    public bool RememberScanSeq(string phoneId, long seq)
    {
        lock (_gate)
        {
            var record = Load().Phones.FirstOrDefault(p => p.Id == phoneId);
            if (record is null)
                return true; // телефона нет в списке (отключён во время запроса) — запоминать некуда, решает вызывающий
            record.ScanSeqs ??= new List<long>();
            if (record.ScanSeqs.Contains(seq))
                return false;
            record.ScanSeqs.Add(seq);
            while (record.ScanSeqs.Count > ScanSeqKeep)
                record.ScanSeqs.RemoveAt(0);
            SaveLocked(withKeys: false);
            return true;
        }
    }

    /// <summary>«Отключить телефон»: ключ стирается, дальше телефон получает revoked, пока кассир не подключит его заново.</summary>
    public bool Revoke(string phoneId)
    {
        lock (_gate)
        {
            var record = Load().Phones.FirstOrDefault(p => p.Id == phoneId);
            if (record is null || record.Revoked)
                return false;
            record.Revoked = true;
            LoadKeys().Remove(phoneId);
            if (!SaveLocked(withKeys: true))
            {
                record.Revoked = false;
                _keys = null; // перечитаем с диска
                return false;
            }
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Подключённые (не отключённые) телефоны, новые сверху.</summary>
    public IReadOnlyList<PhoneInfo> Phones()
    {
        lock (_gate)
            return Load().Phones.Where(p => !p.Revoked)
                .OrderByDescending(p => p.LastSeenUtc ?? p.PairedUtc)
                .Select(p => new PhoneInfo(p.Id, p.Name, p.PairedUtc, p.LastSeenUtc))
                .ToList();
    }

    private static string CleanName(string? name)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 64)
            text = text[..64];
        return text.Length == 0 ? "Телефон" : text;
    }
}
