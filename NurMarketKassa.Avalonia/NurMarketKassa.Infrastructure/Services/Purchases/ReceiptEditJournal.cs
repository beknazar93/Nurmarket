using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-30, редактор накладных закупок («Б-лайт», ТЗ 2.5, 2.8): журнал правок на этом ПК — %APPDATA%\NurMarketKassa\purchases\receipt-edits.json
// (атомарная запись через AtomicFile, схема pos_local.db не меняется). Хранит версии прихода (кто, когда, что, причина), шаги к серверу с ключами повтора
// (ключ пишется на диск ДО отправки — после обрыва повтор идёт тем же ключом), ссылки на доприходы («дополнение к ПР-…») и очередь правок без связи.

/// <summary>Одна позиция шага: товар, количество, цена; для возврата — строка-источник.</summary>
public sealed class EditStepItem
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Для возврата — id строки прихода (или доприхода); пусто — найдём по товару.</summary>
    public string LineId { get; set; } = "";
    /// <summary>Для возврата — из какого прихода: пусто = исходный приход документа.</summary>
    public string SourceReceiptId { get; set; } = "";
    public double Qty { get; set; }
    public double Price { get; set; }
}

/// <summary>Шаг правки: возврат поставщику (return) или доприход (addendum). Status: pending / done / failed / unknown (ответ не дошёл).</summary>
public sealed class EditStep
{
    public string Key { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = "return";
    /// <summary>Для возврата — по какому приходу (пусто = исходный).</summary>
    public string ReceiptId { get; set; } = "";
    public List<EditStepItem> Items { get; set; } = new();
    public string Status { get; set; } = "pending";
    /// <summary>Id возврата или нового (дополнительного) прихода на сервере.</summary>
    public string? ServerId { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public int Attempts { get; set; }
    /// <summary>2026-10-30 (приёмка 1.17.66): когда шаг ушёл впервые — по нему повтор после потерянного ответа ищет уже проведённый документ на сервере.</summary>
    public DateTime? FirstSentAt { get; set; }
    /// <summary>Для возврата: id строки → «возвращено» на сервере ДО первой отправки. Если сейчас больше на количество шага — возврат уже проведён.</summary>
    public Dictionary<string, double> BaseReturned { get; set; } = new();
}

public sealed class EditLineChange
{
    public string LineId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double QtyBefore { get; set; }
    public double QtyAfter { get; set; }
    public double Price { get; set; }
    /// <summary>2026-10-31 (приёмка 1.17.68, «Важно» 4б): после отказа от частично проведённой правки строка НЕ проведена целиком —
    /// <see cref="QtyAfter"/> пересчитан по реально проведённым шагам, а плановое значение сохранено в <see cref="PlannedQtyAfter"/>.</summary>
    public bool NotPosted { get; set; }
    public double? PlannedQtyAfter { get; set; }
}

public sealed class ReceiptEditRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ReceiptId { get; set; } = "";
    public int Version { get; set; }
    public int BaseVersion { get; set; }
    /// <summary>edit — правка; revert — откат версии <see cref="RevertedVersion"/> (новая правка с обратным составом).</summary>
    public string Kind { get; set; } = "edit";
    public int RevertedVersion { get; set; }
    public DateTime At { get; set; }
    public string By { get; set; } = "";
    public string Device { get; set; } = "";
    public string Reason { get; set; } = "";
    public double TotalBefore { get; set; }
    public double TotalAfter { get; set; }
    /// <summary>2026-10-31 (приёмка 1.17.68, 4б): после отказа от частично проведённой правки <see cref="TotalAfter"/> — реально проведённая сумма, плановая — здесь.</summary>
    public double? PlannedTotalAfter { get; set; }
    public string Compensation { get; set; } = "none";
    public string Fingerprint { get; set; } = "";
    /// <summary>applying — шаги идут; done — всё проведено; partial — часть шагов проведена, остальные ждут повтора; failed — ничего не проведено;
    /// abandoned (2026-10-30, приёмка 1.17.67) — владелец отказался от недоведённой правки: проведённые шаги остались, остальные закрыты.</summary>
    public string Status { get; set; } = "applying";
    public List<EditLineChange> Lines { get; set; } = new();
    public List<EditStep> Steps { get; set; } = new();

    public bool IsFinished => Status is "done";
}

/// <summary>Доприход по строке документа: сколько осталось от него (из него возвращаем в первую очередь).</summary>
public sealed class AddendumRef
{
    public string ReceiptId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string LineId { get; set; } = "";
    public double Qty { get; set; }
    public double Remaining { get; set; }
    public int FromVersion { get; set; }
    public DateTime At { get; set; }
}

public sealed class ReceiptHistory
{
    public string ReceiptId { get; set; } = "";
    /// <summary>Текущая версия документа: исходный = 1, каждая завершённая правка +1.</summary>
    public int Version { get; set; } = 1;
    public List<ReceiptEditRecord> Edits { get; set; } = new();
    public List<AddendumRef> Addenda { get; set; } = new();
}

/// <summary>Правка, поставленная в очередь без связи: ничего не отправлялось, пароль не хранится — владелец подтверждает её при открытии накладной.</summary>
public sealed class QueuedEdit
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ReceiptId { get; set; } = "";
    public int BaseVersion { get; set; }
    public string BaseFingerprint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string By { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Compensation { get; set; } = "none";
    public Dictionary<string, double> NewQty { get; set; } = new();
    public int RevertedVersion { get; set; }
}

public sealed class ReceiptEditStore
{
    private sealed class FileData
    {
        public Dictionary<string, ReceiptHistory> Receipts { get; set; } = new();
        public List<QueuedEdit> Queue { get; set; } = new();
    }

    private readonly Func<string?> _dir;
    private readonly Func<bool> _readOnly;
    private readonly Action<string, string> _log;
    private readonly object _sync = new();
    private FileData? _data;

    public ReceiptEditStore(Func<string?> dir, Func<bool>? readOnly = null, Action<string, string>? log = null)
    {
        _dir = dir;
        _readOnly = readOnly ?? (() => false);
        _log = log ?? ((_, _) => { });
    }

    private static ReceiptEditStore? _default;

    /// <summary>Журнал этого ПК (рядом с журналом закупок). Папку и запрет записи берёт у PurchasesStore.</summary>
    public static ReceiptEditStore Default => _default ??= new ReceiptEditStore(() => PurchasesStore.StorageDir, () => PurchasesStore.ReadOnly, PosLogger.Log);

    public event Action? Changed;

    private string? FilePath => _dir() is { Length: > 0 } d ? Path.Combine(d, "receipt-edits.json") : null;

    private FileData Data()
    {
        if (_data is not null)
            return _data;
        _data = new FileData();
        try
        {
            if (FilePath is { } path && !_readOnly() && AtomicFile.ReadAllText(path) is { } text)
                _data = JsonSerializer.Deserialize<FileData>(text) ?? new FileData();
        }
        catch (Exception ex)
        {
            _log($"Правки накладных: журнал не прочитан ({ex.Message}) — начат заново.", "WARNING");
            _data = new FileData();
        }
        return _data;
    }

    private void Save()
    {
        if (_readOnly() || FilePath is not { } path)
            return;
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Data()));
    }

    /// <summary>Забыть прочитанное (другая папка, проверка).</summary>
    public void Reset()
    {
        lock (_sync)
            _data = null;
    }

    public ReceiptHistory Get(string receiptId)
    {
        lock (_sync)
            return Data().Receipts.TryGetValue(receiptId, out var h) ? h : new ReceiptHistory { ReceiptId = receiptId };
    }

    public int Version(string receiptId) => Get(receiptId).Version;

    /// <summary>Исправлена ли накладная (есть завершённая или частичная правка).</summary>
    public bool IsEdited(string receiptId) => Get(receiptId).Edits.Any(e => e.Status is "done" or "partial" or "abandoned");

    /// <summary>Сохранить изменённую историю (под замком).</summary>
    public void Mutate(string receiptId, Action<ReceiptHistory> change)
    {
        lock (_sync)
        {
            var d = Data();
            if (!d.Receipts.TryGetValue(receiptId, out var h))
                d.Receipts[receiptId] = h = new ReceiptHistory { ReceiptId = receiptId };
            change(h);
            Save();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ очередь без связи

    public IReadOnlyList<QueuedEdit> Queue(string? receiptId = null)
    {
        lock (_sync)
            return Data().Queue.Where(q => receiptId is null || q.ReceiptId == receiptId).ToList();
    }

    public void Enqueue(QueuedEdit q)
    {
        lock (_sync)
        {
            // одна правка на приход: новая заменяет прежнюю очередную
            Data().Queue.RemoveAll(x => x.ReceiptId == q.ReceiptId);
            Data().Queue.Add(q);
            Save();
        }
        Changed?.Invoke();
    }

    public void Dequeue(string id)
    {
        lock (_sync)
        {
            Data().Queue.RemoveAll(x => x.Id == id);
            Save();
        }
        Changed?.Invoke();
    }
}
