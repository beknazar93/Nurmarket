using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-14 (пакет Л-6 «Офлайн»): очередь складских операций без связи — списание товара и перемещение между складами/филиалами.
// Раньше «Списать» без связи отвечало «Сервер недоступен — ничего не изменено», а перемещение просто не проводилось. Теперь операция запоминается на диске (pending-stock-ops.json,
// данные компании — CompanyStoreRegistry), в кассе остаток уменьшается сразу (чтобы не продать списанное), а на сервер она уходит позже, САМА и только один раз:
//  • списание — как онлайн-путь ProductActions.ChangeStockAsync: свежий остаток с сервера → документ ревизии «остаток минус списанное» (create + apply). Номер созданного документа пишется
//    на диск ДО «провести», поэтому после обрыва повтор сначала спрашивает сервер о нём (GetSessionAsync) и второй раз не спишет. Остатка не хватает — «конфликт остатка»: запись получает
//    статус «Ошибка», ничего не списывается, решает человек («Повторить» после пополнения или «Убрать»);
//  • перемещение — тот же POST api/main/branch-transfers/ с Idempotency-Key, который сохранён вместе с телом (повтор уходит с тем же ключом и телом).
// Порядок отправки — по времени создания. Нет связи — остановка без ошибок, запись остаётся в очереди. Ничего из чеков, возвратов и приходов эта очередь не трогает.

public enum StockOpKind
{
    WriteOff,
    Transfer,
}

public enum StockOpStatus
{
    Queued,
    Sending,
    /// <summary>Сервер отказал по существу (или нехватка остатка): ждёт решения человека.</summary>
    Failed,
    Done,
}

public sealed class StockOpJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public StockOpKind Kind { get; set; }
    public StockOpStatus Status { get; set; } = StockOpStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public string CompanyId { get; set; } = "";
    public string? Actor { get; set; }

    // списание
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public double Quantity { get; set; }
    public string? Reason { get; set; }

    /// <summary>Номер документа ревизии, созданного на сервере (пишется на диск до «провести»).</summary>
    public string? SessionId { get; set; }

    /// <summary>Остаток на сервере в момент создания документа — по нему повтор понимает, менялся ли остаток с тех пор.</summary>
    public double? BaseStock { get; set; }

    // перемещение
    public string? Title { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? BodyJson { get; set; }
    public double TotalQuantity { get; set; }

    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Нехватка остатка на сервере при досылке списания.</summary>
    public bool StockConflict { get; set; }

    public bool IsActive => Status is StockOpStatus.Queued or StockOpStatus.Sending;

    public StockOpJob Clone() => (StockOpJob)MemberwiseClone();
}

/// <summary>Что нужно очереди от окружения (тесты подставляют свои).</summary>
public sealed class StockOpsDeps
{
    public required IInventoryApiService Inventory { get; init; }

    /// <summary>Свежий остаток товара на сервере. Бросает исключение при сбое связи; null — сервер остаток не вернул.</summary>
    public required Func<string, CancellationToken, Task<double?>> ReadStock { get; init; }

    /// <summary>POST перемещения: (тело JSON, ключ) → ответ сервера.</summary>
    public required Func<string, string, CancellationToken, Task<JsonElement>> PostTransfer { get; init; }

    /// <summary>Списание проведено, остаток на сервере стал таким — обновить плитку и кэш каталога.</summary>
    public Action<StockOpJob, double>? OnWriteOffPosted { get; init; }
}

public sealed class StockOpsQueue
{
    public static StockOpsQueue Instance { get; } = new();

    /// <summary>Только для тестов: другая папка данных (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для тестов: компания без входа в NurCRM.</summary>
    public static string? CompanyIdOverride { get; set; }

    /// <summary>Сколько хранить завершённые записи (для журнала и проверки).</summary>
    public static TimeSpan KeepDone { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Как часто проверять очередь, пока в ней что-то есть.</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    private static string CurrentCompany => CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";
    private static string DataDir => DataDirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName);
    private static string FilePath => Path.Combine(DataDir, "pending-stock-ops.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _sync = new();
    private List<StockOpJob> _jobs = new();
    private bool _loaded;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private Func<StockOpsDeps?>? _depsResolver;
    private bool _subscribed;

    public event Action? Changed;

    private static bool SameCompany(string jobCompany, string company) =>
        jobCompany.Length == 0 || company.Length == 0 || string.Equals(jobCompany, company, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ постановка

    /// <summary>Списание принято без связи: запись на диск. Остаток в кассе и журнал списаний обновляет вызывающий (OfflineWriteOff).</summary>
    public StockOpJob EnqueueWriteOff(string productId, string productName, double quantity, string reason, string? actor)
    {
        var job = new StockOpJob
        {
            Kind = StockOpKind.WriteOff,
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            Reason = reason,
            Actor = actor,
            CompanyId = CurrentCompany,
        };
        Add(job);
        PosLogger.Log($"Склад без связи: списание «{productName}» × {quantity.ToString("0.###", CultureInfo.InvariantCulture)} поставлено в очередь ({job.Id}).", "STOCK");
        return job;
    }

    /// <summary>Перемещение принято без связи: ключ повтора и тело запроса запоминаются как есть.</summary>
    public StockOpJob EnqueueTransfer(string title, string idempotencyKey, string bodyJson, double totalQuantity, string? actor)
    {
        lock (_sync)
        {
            EnsureLoaded();
            // Тот же ключ — та же операция: вторая постановка (повторное нажатие) её не удваивает.
            if (_jobs.FirstOrDefault(j => j.Kind == StockOpKind.Transfer && j.IsActive && j.IdempotencyKey == idempotencyKey) is { } existing)
                return existing.Clone();
        }
        var job = new StockOpJob
        {
            Kind = StockOpKind.Transfer,
            Title = title,
            IdempotencyKey = idempotencyKey,
            BodyJson = bodyJson,
            TotalQuantity = totalQuantity,
            Actor = actor,
            CompanyId = CurrentCompany,
        };
        Add(job);
        PosLogger.Log($"Склад без связи: перемещение «{title}» поставлено в очередь ({job.Id}).", "STOCK");
        return job;
    }

    private void Add(StockOpJob job)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _jobs.Add(job);
            Save();
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ чтение

    /// <summary>Активные записи и записи с отказом текущей компании (старые — первыми).</summary>
    public IReadOnlyList<StockOpJob> Snapshot()
    {
        lock (_sync)
        {
            EnsureLoaded();
            var company = CurrentCompany;
            return _jobs.Where(j => j.Status != StockOpStatus.Done && SameCompany(j.CompanyId, company)).OrderBy(j => j.CreatedAt).Select(j => j.Clone()).ToList();
        }
    }

    public StockOpJob? Get(string id)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    public int ActiveCount => Snapshot().Count(j => j.IsActive);

    /// <summary>Сколько единиц товара уже списано в кассе, но ещё не на сервере (для сверки остатков).</summary>
    public double PendingWriteOffQuantity(string productId)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _jobs.Where(j => j.Kind == StockOpKind.WriteOff && j.Status != StockOpStatus.Done && string.Equals(j.ProductId, productId, StringComparison.OrdinalIgnoreCase)).Sum(j => j.Quantity);
        }
    }

    // ------------------------------------------------------------------ действия человека

    /// <summary>«Повторить» для записи с отказом: снова в очередь (документ ревизии, если он остался непроведённым, повтор проверит сам).</summary>
    public bool Retry(string id)
    {
        lock (_sync)
        {
            EnsureLoaded();
            var job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (job is null || job.Status != StockOpStatus.Failed)
                return false;
            job.Status = StockOpStatus.Queued;
            job.LastError = null;
            job.StockConflict = false;
            Save();
        }
        RaiseChanged();
        return true;
    }

    /// <summary>«Убрать из очереди»: только запись с отказом. Остаток в кассе вернуть нужно вручную (сервер о списании не узнает).</summary>
    public bool Dismiss(string id)
    {
        lock (_sync)
        {
            EnsureLoaded();
            var job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (job is null || job.Status != StockOpStatus.Failed)
                return false;
            _jobs.Remove(job);
            Save();
        }
        RaiseChanged();
        return true;
    }

    // ------------------------------------------------------------------ отправка

    /// <summary>Отправить ждущие записи по порядку создания. Нет связи или входа — остановка (запись остаётся). Возвращает, сколько записей завершено.</summary>
    public Task<int> ProcessAsync(StockOpsDeps deps, CancellationToken ct = default) => ProcessCoreAsync(deps, null, ct);

    /// <summary>Одна запись «Отправить сейчас» (порядок других записей не нарушается: более ранние тоже уходят первыми).</summary>
    public Task<int> ProcessOneAsync(string id, StockOpsDeps deps, CancellationToken ct = default) => ProcessCoreAsync(deps, id, ct);

    private async Task<int> ProcessCoreAsync(StockOpsDeps deps, string? onlyId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0; // проход уже идёт (автоматика или другое нажатие) — второй не запускаем
        var done = 0;
        try
        {
            var company = CurrentCompany;
            foreach (var snap in Snapshot().Where(j => j.Status == StockOpStatus.Queued && SameCompany(j.CompanyId, company)))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await SendOneAsync(snap.Id, deps, ct).ConfigureAwait(false);
                if (outcome == SendOutcome.Done)
                    done++;
                else if (outcome == SendOutcome.NoConnection)
                    break; // порядок: пока впереди стоящая не ушла, следующие не обгоняют её
                if (onlyId is not null && string.Equals(snap.Id, onlyId, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
        return done;
    }

    private enum SendOutcome { Done, Failed, NoConnection }

    private async Task<SendOutcome> SendOneAsync(string id, StockOpsDeps deps, CancellationToken ct)
    {
        StockOpJob job;
        lock (_sync)
        {
            EnsureLoaded();
            var real = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (real is null || real.Status != StockOpStatus.Queued)
                return SendOutcome.Failed;
            real.Status = StockOpStatus.Sending;
            real.Attempts++;
            real.LastAttemptAt = DateTime.Now;
            job = real;
            Save();
        }
        RaiseChanged();
        try
        {
            if (job.Kind == StockOpKind.Transfer)
                await SendTransferAsync(job, deps, ct).ConfigureAwait(false);
            else
                await SendWriteOffAsync(job, deps, ct).ConfigureAwait(false);
            Finish(job, StockOpStatus.Done, null);
            return SendOutcome.Done;
        }
        catch (StockOpRejectedException rej)
        {
            Finish(job, StockOpStatus.Failed, rej.Message, conflict: rej.Conflict);
            return SendOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Requeue(job, null);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is ApiException { StatusCode: 401 } || SessionExpiryMonitor.IsAuthFailure(ex))
            {
                Requeue(job, null);
                return SendOutcome.NoConnection; // нужен вход — ждём, ничего не теряется
            }
            if (PendingIdempotencyKey.IsOutcomeUnknown(ex) || ex is HttpRequestException or TimeoutException or IOException or OperationCanceledException)
            {
                PosLogger.Log($"Склад без связи: запись {job.Id} не отправлена ({ex.GetType().Name}: {ex.Message}) — остаётся в очереди.", "STOCK");
                Requeue(job, null);
                return SendOutcome.NoConnection;
            }
            Finish(job, StockOpStatus.Failed, ReceivingServerMessages.Describe(ex));
            return SendOutcome.Failed;
        }
    }

    private sealed class StockOpRejectedException(string message, bool conflict = false) : Exception(message)
    {
        public bool Conflict { get; } = conflict;
    }

    private async Task SendTransferAsync(StockOpJob job, StockOpsDeps deps, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.BodyJson) || string.IsNullOrWhiteSpace(job.IdempotencyKey))
            throw new StockOpRejectedException(Tr.T("В записи нет данных перемещения — её нужно провести заново.", "Жазууда жылдыруунун маалыматы жок — аны кайра өткөрүү керек.",
                "The item has no transfer data — it has to be made again.", "Kayıtta transfer verisi yok — yeniden yapılmalı.", "Yozuvda ko'chirish ma'lumoti yo'q — uni qayta o'tkazish kerak."));
        try
        {
            await deps.PostTransfer(job.BodyJson!, job.IdempotencyKey!, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 and not 401 and not 408 and not 429 and not 423 and not 425 && !PendingIdempotencyKey.IsOutcomeUnknown(ex))
        {
            // Сервер отказал по существу (обычно: на складе уже не хватает товара) — человек решает; тот же ключ при «Повторить» сервер ответит тем же.
            throw new StockOpRejectedException(ReceivingServerMessages.Describe(ex));
        }
    }

    private async Task SendWriteOffAsync(StockOpJob job, StockOpsDeps deps, CancellationToken ct)
    {
        var productId = job.ProductId ?? throw new StockOpRejectedException("—");
        var name = job.ProductName ?? productId;
        var current = await deps.ReadStock(productId, ct).ConfigureAwait(false)
                      ?? throw new HttpRequestException("Сервер не вернул остаток товара.");

        // Документ, созданный прошлой попыткой, мог дойти до «провести» (обрыв без ответа): сначала спрашиваем сервер, второй раз не списываем.
        if (!string.IsNullOrWhiteSpace(job.SessionId))
        {
            var info = await deps.Inventory.GetSessionAsync(job.SessionId!, ct).ConfigureAwait(false);
            if (info is { IsApplied: true })
            {
                PosLogger.Log($"Склад без связи: документ {job.SessionId} по «{name}» уже проведён на сервере — второй раз не списываем.", "STOCK");
                deps.OnWriteOffPosted?.Invoke(job, current);
                return;
            }
            if (info is not null && job.BaseStock is { } baseStock && Math.Abs(baseStock - current) < 0.0005)
            {
                await deps.Inventory.ApplySessionAsync(job.SessionId!, allowNegative: false, CancellationToken.None).ConfigureAwait(false);
                PosLogger.Log($"Склад без связи: непроведённый документ {job.SessionId} по «{name}» доведён до конца.", "STOCK");
                deps.OnWriteOffPosted?.Invoke(job, Math.Round(baseStock - job.Quantity, 3));
                return;
            }
            // Остаток с тех пор менялся (продали, пришёл товар) — итог старого документа устарел: считаем заново от текущего остатка новым документом.
            PosLogger.Log($"Склад без связи: документ {job.SessionId} по «{name}» устарел (остаток {job.BaseStock} → {current}) — создаём новый.", "WARNING");
            UpdateJob(job.Id, j => { j.SessionId = null; j.BaseStock = null; });
            job.SessionId = null;
        }

        var target = Math.Round(current - job.Quantity, 3);
        if (target < 0)
            throw new StockOpRejectedException(Tr.T(
                $"«{name}»: на сервере {Qty(current)}, а списано без связи {Qty(job.Quantity)} — остатка не хватает, ничего не списано. Проверьте остаток и нажмите «Повторить» (или «Убрать из очереди»).",
                $"«{name}»: серверде {Qty(current)}, байланышсыз эсептен чыгарылганы {Qty(job.Quantity)} — калдык жетишпейт, эч нерсе чыгарылган жок. Калдыкты текшерип, «Кайра аракет» басыңыз (же «Кезектен алып салуу»).",
                $"“{name}”: the server has {Qty(current)} but {Qty(job.Quantity)} was written off offline — not enough stock, nothing was written off. Check the stock and press “Retry” (or “Remove from queue”).",
                $"«{name}»: sunucuda {Qty(current)} var, bağlantısız {Qty(job.Quantity)} düşüldü — stok yetmiyor, hiçbir şey düşülmedi. Stoğu kontrol edip «Tekrar dene»ye basın (veya «Kuyruktan kaldır»).",
                $"«{name}»: serverda {Qty(current)}, aloqasiz {Qty(job.Quantity)} hisobdan chiqarilgan — qoldiq yetmaydi, hech narsa chiqarilmadi. Qoldiqni tekshirib «Qayta urinish»ni bosing (yoki «Navbatdan olib tashlash»)."), conflict: true);
        target = InventoryApiService.RoundForServer(target);

        var note = $"Списание без связи: {job.Reason} ({Qty(job.Quantity)}); принято {job.CreatedAt:dd.MM.yyyy HH:mm}";
        var sessionId = await deps.Inventory.CreateSessionAsync(note, new[] { new InventorySessionItem(productId, target) }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new HttpRequestException("Сервер не создал документ.");
        // Номер документа — на диск ДО «провести»: обрыв после этого места не создаст второй документ.
        UpdateJob(job.Id, j => { j.SessionId = sessionId; j.BaseStock = current; });
        job.SessionId = sessionId;
        await deps.Inventory.ApplySessionAsync(sessionId!, allowNegative: false, CancellationToken.None).ConfigureAwait(false);
        InventoryApiService.InvalidateActsCache();
        PosLogger.Log($"Склад без связи: списание «{name}» проведено на сервере: {Qty(current)} → {Qty(target)} (документ {sessionId}).", "STOCK");
        deps.OnWriteOffPosted?.Invoke(job, target);
    }

    private static string Qty(double v) => v.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    private void UpdateJob(string id, Action<StockOpJob> change)
    {
        lock (_sync)
        {
            var real = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
            if (real is null)
                return;
            change(real);
            Save();
        }
    }

    private void Finish(StockOpJob job, StockOpStatus status, string? error, bool conflict = false)
    {
        lock (_sync)
        {
            var real = _jobs.FirstOrDefault(j => string.Equals(j.Id, job.Id, StringComparison.OrdinalIgnoreCase));
            if (real is not null)
            {
                real.Status = status;
                real.LastError = error;
                real.StockConflict = conflict;
                real.FinishedAt = status == StockOpStatus.Done ? DateTime.Now : null;
                Save();
            }
        }
        RaiseChanged();
    }

    private void Requeue(StockOpJob job, string? error)
    {
        UpdateJob(job.Id, j => { if (j.Status == StockOpStatus.Sending) j.Status = StockOpStatus.Queued; if (error is not null) j.LastError = error; });
        RaiseChanged();
    }

    // ------------------------------------------------------------------ автоотправка

    /// <summary>Запуск автоотправки: при восстановлении связи и раз в минуту, пока в очереди что-то есть. Повторный вызов безвреден.</summary>
    public void Resume(Func<StockOpsDeps?> depsResolver)
    {
        _depsResolver = depsResolver;
        lock (_sync)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                ServerOutageMonitor.Recovered += () => _ = TryProcessAsync();
            }
            _timer ??= new Timer(_ => _ = TryProcessAsync(), null, TimeSpan.FromSeconds(10), PollInterval);
        }
    }

    /// <summary>Один проход автоотправки (не при аварии сервера и входе, который истёк).</summary>
    public async Task TryProcessAsync()
    {
        try
        {
            if (ActiveCount == 0 || ServerOutageMonitor.IsOutage || OfflineModeHelper.UseLocalOperations || SessionExpiryMonitor.IsExpired)
                return;
            if (_depsResolver?.Invoke() is not { } deps)
                return;
            await ProcessAsync(deps).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад без связи: проход отправки не удался ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ диск

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Склад без связи: обработчик изменения упал ({ex.Message}).", "WARNING"); }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            var text = AtomicFile.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text))
                return;
            var list = JsonSerializer.Deserialize<List<StockOpJob?>>(text, Json) ?? new();
            _jobs = list.Where(j => j is not null && !string.IsNullOrWhiteSpace(j.Id)).Select(j => j!).ToList();
            foreach (var j in _jobs.Where(j => j.Status == StockOpStatus.Sending))
                j.Status = StockOpStatus.Queued; // перезапуск посреди отправки: повтор проверит документ сам
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад без связи: файл очереди не прочитан ({ex.GetType().Name}: {ex.Message}) — очередь начинается пустой.", "ERROR");
            _jobs = new List<StockOpJob>();
        }
    }

    /// <summary>Только для тестов: перечитать файл (имитация перезапуска кассы).</summary>
    internal void ResetForTests()
    {
        lock (_sync)
        {
            _loaded = false;
            _jobs = new List<StockOpJob>();
        }
    }

    private bool Save()
    {
        try
        {
            var now = DateTime.Now;
            _jobs.RemoveAll(j => j.Status == StockOpStatus.Done && now - (j.FinishedAt ?? j.CreatedAt) > KeepDone);
            Directory.CreateDirectory(DataDir);
            AtomicFile.WriteAllTextWithRetry(FilePath, JsonSerializer.Serialize(_jobs, Json));
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад без связи: файл очереди не записан ({ex.Message}).", "ERROR");
            return false;
        }
    }
}
