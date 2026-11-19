using System.Text.Json;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14: запись очереди фискальных чеков — по одному чеку/возврату. Ключ повтора (<see cref="Key"/>) один на чек и на все повторы:
/// повтор после таймаута не должен создавать второй фискальный чек.</summary>
public sealed class FiscalQueueEntry
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public FiscalDocKind Kind { get; set; }
    /// <summary>id продажи/возврата на сервере NurCRM (по нему пишутся реквизиты). Чек из офлайн-очереди — после его досылки.</summary>
    public string DocId { get; set; } = "";
    public string ReceiptNumber { get; set; } = "";
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>pending — ждёт пробития (или повтора); failed — отказ, нужен человек; accepted — ККМ приняла чек.</summary>
    public FiscalStatus Status { get; set; } = FiscalStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? LastError { get; set; }
    /// <summary>Запрос к ККМ (<see cref="FiscalReceiptRequest"/> в JSON): повтор берёт его, а не пересобирает чек из корзины.</summary>
    public string? RequestJson { get; set; }
    /// <summary>Реквизиты от ККМ (после accepted).</summary>
    public FiscalRequisites? Requisites { get; set; }
    /// <summary>Реквизиты записаны на сервер NurCRM (POST …/fiscal/ ответил 200).</summary>
    public bool ServerWritten { get; set; }
    public DateTimeOffset? ServerWrittenAt { get; set; }
    /// <summary>Сервер ответил отказом, который не лечится повтором (fiscal_already_set, return_voided, fiscal_mode_server, …) — нужен человек.</summary>
    public string? ServerProblem { get; set; }
}

public sealed class FiscalQueueData
{
    public List<FiscalQueueEntry> Items { get; set; } = new();
}

/// <summary>2026-11-14 (по образцу <see cref="OfflinePendingSalesStore"/>): очередь фискальных чеков — каркас без реального провайдера. Хранит статус каждого чека/возврата
/// (pending / failed / accepted), число попыток, причину и реквизиты, а также «записано ли на сервер». Данные — JSON-файл fiscal-queue.json в %LOCALAPPDATA%\NurMarketKassa
/// (схема pos_local.db не меняется), общий для кассы и программы владельца, запись под межпроцессным замком <see cref="AtomicFile.Update"/>; файл — данные компании (CompanyStoreRegistry).
/// В оплату эта очередь пока НЕ подключена: чеки в неё ставит только будущий путь «оплата → ККМ» (Ф1).</summary>
public sealed class FiscalQueueStore
{
    public const string FileName = "fiscal-queue.json";

    /// <summary>Не больше записей в файле: сначала уходят самые старые из уже записанных на сервер.</summary>
    public const int MaxItems = 5000;

    /// <summary>Принятые и записанные на сервер чеки хранятся столько дней, потом убираются.</summary>
    public const int KeepAcceptedDays = 30;

    /// <summary>Ждущий чек дольше этого времени — «Требует внимания» (баннер кассиру и владельцу).</summary>
    public static readonly TimeSpan PendingAttention = TimeSpan.FromMinutes(10);

    /// <summary>Столько неудачных попыток подряд — «Требует внимания», даже если ошибка «временная».</summary>
    public const int AttemptsAttention = 5;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;

    public FiscalQueueStore(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static FiscalQueueStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public FiscalQueueData Load() => Parse(AtomicFile.ReadAllText(_path));

    private static FiscalQueueData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new FiscalQueueData();
        try { return JsonSerializer.Deserialize<FiscalQueueData>(text, Json) ?? new FiscalQueueData(); }
        catch (JsonException) { return new FiscalQueueData(); }
    }

    private bool Mutate(Func<FiscalQueueData, bool> change)
    {
        var changed = false;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            changed = change(data);
            if (!changed)
                return null;
            Trim(data);
            return JsonSerializer.Serialize(data, Json);
        });
        return changed;
    }

    private void Trim(FiscalQueueData data)
    {
        var limit = _now().AddDays(-KeepAcceptedDays);
        data.Items.RemoveAll(e => e.Status == FiscalStatus.Accepted && e.ServerWritten && (e.ServerWrittenAt ?? e.CreatedAt) < limit);
        if (data.Items.Count <= MaxItems)
            return;
        foreach (var old in data.Items.Where(e => e.Status == FiscalStatus.Accepted && e.ServerWritten).OrderBy(e => e.CreatedAt).ToList())
        {
            if (data.Items.Count <= MaxItems)
                break;
            data.Items.Remove(old);
        }
    }

    public List<FiscalQueueEntry> All() => Load().Items.OrderBy(e => e.CreatedAt).ToList();

    public FiscalQueueEntry? Find(FiscalDocKind kind, string docId) =>
        Load().Items.FirstOrDefault(e => e.Kind == kind && string.Equals(e.DocId, docId, StringComparison.OrdinalIgnoreCase));

    public FiscalQueueEntry? FindByKey(string key) => Load().Items.FirstOrDefault(e => e.Key == key);

    /// <summary>Поставить чек в очередь. Один чек — одна запись: повторный вызов для той же продажи/возврата возвращает существующую (ключ повтора сохраняется).</summary>
    public FiscalQueueEntry Enqueue(FiscalDocKind kind, string docId, string receiptNumber, decimal total, string? requestJson = null, DateTimeOffset? createdAt = null)
    {
        FiscalQueueEntry? result = null;
        // 2026-11-14: учебный режим — в очередь ничего не пишется (как в офлайн-очереди продаж).
        if (TrainingMode.IsActive)
        {
            PosLogger.Log("Учебный режим: постановка фискального чека в очередь отклонена.", "TRAINING");
            return new FiscalQueueEntry { Kind = kind, DocId = docId, ReceiptNumber = receiptNumber, Total = total, RequestJson = requestJson };
        }
        Mutate(d =>
        {
            var existing = d.Items.FirstOrDefault(e => e.Kind == kind && string.Equals(e.DocId, docId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                result = existing;
                return false;
            }
            result = new FiscalQueueEntry
            {
                Kind = kind, DocId = docId.Trim(), ReceiptNumber = receiptNumber ?? "", Total = total, RequestJson = requestJson,
                CreatedAt = createdAt ?? _now(),
            };
            d.Items.Add(result);
            return true;
        });
        return result!;
    }

    private bool Update(string key, Action<FiscalQueueEntry> change) => Mutate(d =>
    {
        var e = d.Items.FirstOrDefault(x => x.Key == key);
        if (e is null)
            return false;
        change(e);
        return true;
    });

    /// <summary>Итог обращения к ККМ: успех — accepted с реквизитами; ошибка с повтором — остаётся pending (счётчик попыток растёт), без повтора — failed.</summary>
    public bool RecordResult(string key, FiscalResult result) => Update(key, e =>
    {
        e.Attempts++;
        e.LastAttemptAt = _now();
        if (result.Success && result.Requisites is { } req)
        {
            e.Status = FiscalStatus.Accepted;
            e.Requisites = req with { Status = FiscalStatus.Accepted };
            e.ErrorCode = null;
            e.LastError = null;
            return;
        }
        e.Status = result.Retryable ? FiscalStatus.Pending : FiscalStatus.Failed;
        e.ErrorCode = result.ErrorCode;
        e.LastError = string.IsNullOrWhiteSpace(result.ErrorText) ? result.ErrorCode : result.ErrorText!.Trim();
    });

    /// <summary>ККМ принял чек, а сервер NurCRM ответил — реквизиты записаны (или уже были записаны ранее).</summary>
    public bool MarkServerWritten(string key) => Update(key, e =>
    {
        e.ServerWritten = true;
        e.ServerWrittenAt = _now();
        e.ServerProblem = null;
    });

    /// <summary>Сервер отказал так, что повтор не поможет — запоминаем причину (запись считается требующей внимания).</summary>
    public bool MarkServerProblem(string key, string problem) => Update(key, e => e.ServerProblem = problem);

    /// <summary>Повторить: failed → pending, счётчик попыток с нуля. accepted и pending не трогаются. true — вернули в очередь.</summary>
    public bool Requeue(string key)
    {
        var done = false;
        Update(key, e =>
        {
            if (e.Status != FiscalStatus.Failed)
                return;
            e.Status = FiscalStatus.Pending;
            e.Attempts = 0;
            e.ErrorCode = null;
            e.LastError = null;
            done = true;
        });
        return done;
    }

    /// <summary>Вернуть в очередь все отказы. Сколько вернули.</summary>
    public int RequeueAllFailed()
    {
        var count = 0;
        Mutate(d =>
        {
            foreach (var e in d.Items.Where(x => x.Status == FiscalStatus.Failed))
            {
                e.Status = FiscalStatus.Pending;
                e.Attempts = 0;
                e.ErrorCode = null;
                e.LastError = null;
                count++;
            }
            return count > 0;
        });
        return count;
    }

    /// <summary>Чеки, которые касса ещё должна пробить (pending), от старых к новым.</summary>
    public List<FiscalQueueEntry> Pending() => All().Where(e => e.Status == FiscalStatus.Pending).ToList();

    /// <summary>Принятые ККМ, но не записанные на сервер NurCRM (запись надо повторить).</summary>
    public List<FiscalQueueEntry> AcceptedNotWritten() => All().Where(e => e.Status == FiscalStatus.Accepted && !e.ServerWritten && e.ServerProblem is null).ToList();

    /// <summary>«Требует внимания»: отказ (failed); ждёт дольше <see cref="PendingAttention"/> или ≥ <see cref="AttemptsAttention"/> неудачных попыток;
    /// принят ККМ, но не записан на сервер дольше <see cref="PendingAttention"/>; сервер отказал без надежды на повтор.</summary>
    public List<FiscalQueueEntry> NeedsAttention() => All().Where(e => NeedsAttentionNow(e, _now())).ToList();

    public static bool NeedsAttentionNow(FiscalQueueEntry e, DateTimeOffset now) =>
        e.ServerProblem is not null
        || e.Status == FiscalStatus.Failed
        || (e.Status == FiscalStatus.Pending && (now - e.CreatedAt >= PendingAttention || e.Attempts >= AttemptsAttention))
        || (e.Status == FiscalStatus.Accepted && !e.ServerWritten && now - (e.LastAttemptAt ?? e.CreatedAt) >= PendingAttention);

    /// <summary>Сколько чеков не пробито окончательно (pending + failed): для баннера кассиру и проверки закрытия смены.</summary>
    public int UnfinishedCount => Load().Items.Count(e => e.Status is FiscalStatus.Pending or FiscalStatus.Failed);

    /// <summary>Убрать запись (владелец решил: чек пробит вручную / не нужен). Принятый чек удалять нельзя — реквизиты потеряются.</summary>
    public bool Remove(string key)
    {
        var done = false;
        Mutate(d =>
        {
            var e = d.Items.FirstOrDefault(x => x.Key == key);
            if (e is null || e.Status == FiscalStatus.Accepted && !e.ServerWritten)
                return false;
            d.Items.Remove(e);
            done = true;
            return true;
        });
        return done;
    }
}
