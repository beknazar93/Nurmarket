using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

// 2026-10-25, ТЗ «оприходование в фоне» (docs/ТЗ срочное — оприходование в фоне (25.10.2026).md), пакет Q1.
// Раньше «Оприходовать» держало модальный слой «Сохранение данных…» на всё окно, пока не кончится вся цепочка запросов к NurCRM
// (на стенде 6 с…4 мин при ответе сервера 0,5…20 с). Теперь окно кладёт снимок строк в эту очередь и сразу освобождается; приход
// проводится в фоне, по одной задаче, и переживает закрытие окна и перезапуск программы.
//
// Гарантии (деньги и остатки):
//  • у каждой задачи СВОЙ ключ идемпотентности прихода, он создаётся и записывается на диск ДО отправки (ReceivingPostState);
//  • повтор после неизвестного исхода сначала ищет приход на сервере (сервер может не смотреть ключ) и не шлёт второй;
//  • без поставщика — те же три исхода PATCH остатка, но состояние хранится в задаче (не задвоит после перезапуска);
//  • остатки плиток каталога меняются только после ответа сервера;
//  • долг поставщику — SupplierDebtRecorder (свой журнал и метки), здесь только вызов.
// Файл: %APPDATA%\<NurMarketKassa|NurMarketOwner>\background-receipts\queue.json (атомарная запись, повреждённый файл
// переименовывается в queue.bad-<время>.json и очередь закрывается с понятным текстом — как журнал SupplierDebtRecorder).

public sealed partial class BackgroundReceivingQueue
{
    public static BackgroundReceivingQueue Instance { get; } = new();

    // ------------------------------------------------------------------ настройки (стенд может подменить)

    /// <summary>Только для стенда проверки: другая папка очереди (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для стенда проверки: компания без входа в NurCRM.</summary>
    public static string? CompanyIdOverride { get; set; }

    /// <summary>Сколько автоматических повторов после первой попытки (потом «Ошибка — Повторить»).</summary>
    public const int MaxRetries = 6;

    /// <summary>Паузы перед повторами: 5 с, 15 с, 30 с, 1 мин, 2 мин, 5 мин.</summary>
    public static TimeSpan[] RetryDelays { get; set; } =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];

    /// <summary>Страховка одной попытки (без поставщика запросов много). Не «таймаут запроса» — тот 55 с в NurMarketApiClient.</summary>
    public static TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>2026-10-30 (скорость массовой загрузки): сколько строк части из мастера загрузки создаётся и записывается одновременно (для остальных приходов — по одной).
    /// Общая очередь запросов клиента — 12 мест, часть из них нужна кассе (продажи, лента).</summary>
    public static int BulkPostParallel { get; set; } = 8;

    /// <summary>Как часто проверять связь, пока сервер недоступен (дополнительно к событию ServerOutageMonitor).</summary>
    public static TimeSpan ServerPoll { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Не чаще раза в это время обновляем каталог после создания новых товаров (пачка задач — один раз).</summary>
    public static TimeSpan CatalogRefreshMinInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Для стенда: что делать вместо CatalogCacheService.RefreshFromApiAsync.</summary>
    public static Func<Task>? CatalogRefresher { get; set; }

    /// <summary>Откуда взять клиент API, если Resume ещё не вызывали (касса: App.GetRequiredService).</summary>
    public static Func<NurMarketApiClient?>? ApiResolver { get; set; }

    /// <summary>Только для стенда: на сколько откладывать повтор при 429 (минимум; сервер может просить больше).</summary>
    public static TimeSpan ThrottleMinWait { get; set; } = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------ события

    /// <summary>Любой переход статуса задачи (копия). Может прийти не с UI-потока.</summary>
    public event Action<ReceivingJob>? JobChanged;

    /// <summary>Задача завершена: done / partial / failed / needs_check (копия). Может прийти не с UI-потока.</summary>
    public event Action<ReceivingJob>? JobFinished;

    /// <summary>Каталог кассы обновлён после создания новых товаров (окно склада может перечитать плитки).</summary>
    public event Action? CatalogRefreshed;

    // ------------------------------------------------------------------ состояние

    private readonly object _sync = new();
    private List<ReceivingJob> _jobs = new();
    private bool _loaded;
    private bool _closed;
    private string? _closedFile;
    private NurMarketApiClient? _api;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private readonly object _wakeSync = new();
    private TaskCompletionSource _wakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TaskCompletionSource<ReceivingJob>> _waiters = new();
    private readonly Dictionary<string, PurchaseFinisher> _finishers = new(StringComparer.OrdinalIgnoreCase);
    // 2026-10-26 (М1): крючки «подготовка» — воркер вызывает до PostAsync. «bulk» подключён всегда (не зависит от порядка запуска хоста).
    private readonly Dictionary<string, Func<ReceivingJob, CancellationToken, Task>> _preparers = new(StringComparer.OrdinalIgnoreCase)
    {
        [BulkReceivingFeeder.PreparerName] = BulkReceivingFeeder.PrepareJobAsync,
    };
    private bool _subscribed;

    private sealed record PurchaseFinisher(Func<ReceivingJob, PurchaseReceivingService.PostResult, CancellationToken, Task<FinisherResult>> Run);

    private static string CurrentCompany => CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";

    private static bool SameCompany(string jobCompany, string company) =>
        jobCompany.Length == 0 || company.Length == 0 || string.Equals(jobCompany, company, StringComparison.OrdinalIgnoreCase);

    /// <summary>true — файл очереди повреждён и закрыт (до разбора поддержкой); <see cref="ClosedText"/> — что сказать кассиру.</summary>
    public bool IsClosed
    {
        get
        {
            EnsureLoaded();
            return _closed;
        }
    }

    public string ClosedText => Tr.T(
        "Файл очереди приходов в фоне повреждён и сохранён отдельно — приход в фоне сейчас недоступен. Обратитесь в поддержку.",
        "Фондогу кирүүлөр кезегинин файлы бузулган жана өзүнчө сакталды — фондо кабыл алуу азыр жеткиликсиз. Колдоо кызматына кайрылыңыз.",
        "The background receiving queue file is damaged and was saved separately — background receiving is unavailable now. Contact support.",
        "Arka plandaki mal kabul kuyruğu dosyası bozuk ve ayrı kaydedildi — arka planda mal kabul şu anda kullanılamıyor. Destekle iletişime geçin.",
        "Fondagi kirimlar navbati fayli buzilgan va alohida saqlandi — fonda qabul qilish hozir mavjud emas. Qo'llab-quvvatlashga murojaat qiling.");

    private static string SaveFailedText => Tr.T(
        "Не удалось записать приход в очередь на диск — строки остались в списке, повторите.",
        "Кирүүнү дисктеги кезекке жазуу мүмкүн болгон жок — саптар тизмеде калды, кайталаңыз.",
        "Could not save the receiving to the queue on disk — the lines stay in the list, try again.",
        "Mal kabul diskteki kuyruğa yazılamadı — satırlar listede kaldı, tekrar deneyin.",
        "Kirimni diskdagi navbatga yozib bo'lmadi — qatorlar ro'yxatda qoldi, qayta urining.");

    // ------------------------------------------------------------------ публичный API

    /// <summary>Принять приход в очередь. Синхронно (без сети): запись на диск, пробуждение воркера. Возвращает Id задачи.
    /// Бросает InvalidOperationException с понятным текстом, если очередь закрыта или файл не записался (строки окна не трогать).</summary>
    public string Enqueue(ReceivingJobRequest request) => Enqueue(request, out _);

    /// <summary><paramref name="duplicate"/> = true — такая же задача (поставщик + строки) создана меньше 3 с назад (двойное нажатие):
    /// новая не создаётся, возвращается Id уже принятой.</summary>
    public string Enqueue(ReceivingJobRequest request, out bool duplicate)
    {
        duplicate = false;
        if (request.Lines.Count == 0)
            throw new ArgumentException("нет строк", nameof(request));
        EnsureLoaded();
        if (_closed)
            throw new InvalidOperationException(ClosedText);

        var job = new ReceivingJob
        {
            Id = Guid.NewGuid().ToString("N"),
            CompanyId = CurrentCompany,
            Source = request.Source,
            CreatedAt = DateTime.Now,
            Employee = request.Employee,
            Supplier = JobSupplier.From(request.Supplier),
            PaidNow = request.PaidNow,
            PaymentFields = request.PaymentFields is { Count: > 0 } ? new Dictionary<string, string>(request.PaymentFields) : null,
            DebtDays = request.DebtDays,
            Finisher = request.Finisher,
            FinisherPayload = request.FinisherPayload,
            BatchId = request.BatchId,
            BatchPart = request.BatchPart,
            Preparer = request.Preparer,
            Lines = request.Lines.Select(JobLine.From).ToList(),
            Status = ReceivingJobStatus.Queued,
        };
        // 2026-10-26 (М1): номера строк файла — параллельно строкам (для «Скачать строки с ошибками»).
        if (request.FileRows is { } fileRows)
            for (var i = 0; i < job.Lines.Count && i < fileRows.Count; i++)
                job.Lines[i].FileRow = fileRows[i];
        if (!string.IsNullOrWhiteSpace(request.SupplierDocNumber))
        {
            job.State.SupplierDocNumber = request.SupplierDocNumber.Trim();
            job.State.SupplierDocDate = request.SupplierDocDate;
        }
        job.Signature = SignatureOf(job);

        lock (_sync)
        {
            var same = _jobs.FirstOrDefault(j => j.Signature == job.Signature && SameCompany(j.CompanyId, job.CompanyId)
                                                 && (DateTime.Now - j.CreatedAt) < TimeSpan.FromSeconds(3));
            if (same is not null)
            {
                duplicate = true;
                PosLogger.Log($"Приход в фоне: повторное нажатие — задача {same.Id} уже принята.", "STOCK");
                return same.Id;
            }
            _jobs.Add(job);
            if (!Save())
            {
                _jobs.Remove(job);
                throw new InvalidOperationException(SaveFailedText);
            }
        }

        PosLogger.Log($"Приход в фоне: принята задача {job.Id} ({job.Source}, строк {job.Lines.Count}, поставщик {job.Supplier?.Name ?? "—"}).", "STOCK");
        RaiseChanged(job);
        EnsureWorker();
        Wake();
        return job.Id;
    }

    /// <summary>Дождаться завершения задачи не дольше <paramref name="max"/> (правило 0,5 с). Завершилась (done/partial/failed/needs_check) —
    /// копия задачи, иначе null (задача идёт или ждёт повтора — окно показывает плашку «в фоне»).</summary>
    public async Task<ReceivingJob?> WaitAsync(string id, TimeSpan max)
    {
        TaskCompletionSource<ReceivingJob> tcs;
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null)
                return null;
            if (job.IsFinal)
                return job.Clone();
            if (!_waiters.TryGetValue(id, out tcs!))
                _waiters[id] = tcs = new TaskCompletionSource<ReceivingJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(max)).ConfigureAwait(false);
        return winner == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
    }

    /// <summary>Копии задач текущей компании (новые сверху). allCompanies — и чужих (у них <see cref="ReceivingJob.ForeignCompany"/>).</summary>
    public IReadOnlyList<ReceivingJob> Snapshot(bool allCompanies = false)
    {
        EnsureLoaded();
        var company = CurrentCompany;
        lock (_sync)
        {
            var list = new List<ReceivingJob>();
            foreach (var j in _jobs.OrderByDescending(x => x.CreatedAt))
            {
                var own = SameCompany(j.CompanyId, company);
                if (!own && !allCompanies)
                    continue;
                var copy = j.Clone();
                copy.ForeignCompany = !own;
                list.Add(copy);
            }
            return list;
        }
    }

    /// <summary>Копия одной задачи или null.</summary>
    public ReceivingJob? Get(string id)
    {
        EnsureLoaded();
        lock (_sync)
            return _jobs.FirstOrDefault(j => j.Id == id)?.Clone();
    }

    /// <summary>«в фоне: N» — задачи текущей компании, которые ещё идут или ждут повтора/связь.</summary>
    public int ActiveCount
    {
        get
        {
            EnsureLoaded();
            var company = CurrentCompany;
            lock (_sync)
                return _jobs.Count(j => j.IsActive && SameCompany(j.CompanyId, company));
        }
    }

    /// <summary>«ошибка: K» — задачи текущей компании в итоге failed / needs_check / partial (нужно внимание кассира).</summary>
    public int FailedCount
    {
        get
        {
            EnsureLoaded();
            var company = CurrentCompany;
            lock (_sync)
                return _jobs.Count(j => j.Status is ReceivingJobStatus.Failed or ReceivingJobStatus.NeedsCheck or ReceivingJobStatus.Partial && SameCompany(j.CompanyId, company));
        }
    }

    /// <summary>Все задачи любой компании, которые ещё не закончены (для предупреждения при выходе из программы).</summary>
    public int ActiveCountAll
    {
        get
        {
            EnsureLoaded();
            lock (_sync)
                return _jobs.Count(j => j.IsActive);
        }
    }

    /// <summary>После входа и загрузки компании: прочитать файл и запустить воркер (незавершённые задачи догружаются сами).</summary>
    public void Resume(NurMarketApiClient api)
    {
        _api = api;
        // Первый Enqueue не должен платить за разогрев JSON (≈ 100 мс): греем заранее, в фоне.
        _ = Task.Run(() =>
        {
            try
            {
                new ReceivingJob { Id = "warmup", Lines = { new JobLine() } }.Clone();
            }
            catch
            {
            }
        });
        EnsureLoaded();
        EnsureWorker();
        Wake();
    }

    /// <summary>Шаги после прихода, которые выполняет окно-источник (Source «purchases»: расход/изъятие/история закупок). Регистрировать до Resume.</summary>
    public void RegisterFinisher(string name, Func<ReceivingJob, PurchaseReceivingService.PostResult, CancellationToken, Task<FinisherResult>> finisher)
    {
        lock (_sync)
            _finishers[name] = new PurchaseFinisher(finisher);
    }

    /// <summary>2026-10-26 (М1): крючок «подготовка» (по образцу <see cref="RegisterFinisher"/>): воркер вызывает его с КОПИЕЙ задачи до PostAsync,
    /// изменённые строки копии возвращаются в задачу и пишутся на диск. Исключение — обычная неудачная попытка (повторы как у очереди).</summary>
    public void RegisterPreparer(string name, Func<ReceivingJob, CancellationToken, Task> preparer)
    {
        lock (_sync)
            _preparers[name] = preparer;
    }

    /// <summary>2026-10-26 (М1): копии задач одной пачки (любой компании) — подающий сверяет, какие части уже стоят в очереди.</summary>
    public IReadOnlyList<ReceivingJob> JobsOfBatch(string batchId)
    {
        EnsureLoaded();
        lock (_sync)
            return _jobs.Where(j => j.BatchId == batchId).Select(j => j.Clone()).ToList();
    }

    /// <summary>«Повторить» у задачи с ошибкой: счётчик попыток обнуляется, ключ прихода остаётся прежний (тот же документ).
    /// false — повторять нечего (приход документом уже проведён, остались только строки «нужна проверка», задача идёт).</summary>
    public bool Retry(string id)
    {
        ReceivingJob job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id)!;
            if (job is null || job.Status is not (ReceivingJobStatus.Failed or ReceivingJobStatus.Partial or ReceivingJobStatus.NeedsCheck))
                return false;
            // Документ прихода уже проведён — второй раз его не отправить; оставшиеся строки возвращают в приёмку.
            if (job.Supplier is not null && job.State.ReceiptOutcome == ReceivingReceiptOutcome.Posted)
                return false;
            var any = false;
            foreach (var l in job.Lines.Where(l => l.Outcome is ReceivingLineOutcome.Rejected or ReceivingLineOutcome.Unknown))
            {
                l.Outcome = ReceivingLineOutcome.Pending;
                l.Error = null;
                lock (job.State.Gate)
                    job.State.LineOutcome.Remove(l.Key);
                any = true;
            }
            if (!any && !job.Lines.Any(l => l.Outcome == ReceivingLineOutcome.Pending))
                return false;
            job.Attempts = 0;
            job.NextAttemptAt = null;
            job.WaitingForServer = false;
            job.LastError = null;
            job.FinishedAt = null;
            job.Status = ReceivingJobStatus.Queued;
            Save();
        }
        RaiseChanged(job);
        EnsureWorker();
        Wake();
        return true;
    }

    /// <summary>«Убрать из списка»: только завершённые задачи (для «нужна проверка» окно сначала спрашивает «Я проверил остаток вручную»).</summary>
    public bool Dismiss(string id)
    {
        ReceivingJob job;
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id)!;
            if (job is null || !job.IsFinal)
                return false;
            _jobs.Remove(job);
            Save();
        }
        // 2026-10-29: красная плашка «ошибка: N» в шапке пересчитывается сразу (раньше после «Убрать из списка» события не было).
        RaiseChanged(job);
        return true;
    }

    /// <summary>«Вернуть строки в приёмку»: только строки, которые ТОЧНО не проведены (отказ сервера 4xx или не отправлялись).
    /// Они удаляются из задачи (пустая задача без принятых строк — из списка). Окно добавляет их в свой список.</summary>
    public IReadOnlyList<ReceivingLineVm> TakeBackLines(string id)
    {
        var result = new List<ReceivingLineVm>();
        ReceivingJob? changedJob = null;
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.Status is not (ReceivingJobStatus.Failed or ReceivingJobStatus.Partial or ReceivingJobStatus.NeedsCheck))
                return result;
            changedJob = job;
            foreach (var l in job.Lines.ToList())
            {
                if (!SurelyNotPosted(job, l))
                    continue;
                result.Add(l.ToVm());
                job.Lines.Remove(l);
            }
            if (result.Count == 0)
                return result;
            if (job.Lines.Count == 0)
                _jobs.Remove(job);
            else if (!job.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown or ReceivingLineOutcome.Rejected))
            {
                // Остались только принятые/«нужна проверка» — задача завершена как есть.
                if (job.Lines.All(l => l.Outcome == ReceivingLineOutcome.Posted))
                    job.Status = ReceivingJobStatus.Done;
            }
            Save();
        }
        if (changedJob is not null && result.Count > 0)
            RaiseChanged(changedJob); // 2026-10-29: плашка «ошибка» в шапке обновляется сразу
        return result;
    }

    /// <summary>2026-10-29, смена компании («ничего не должно остаться от старого аккаунта»): остановить воркер и забыть все задачи
    /// (и в памяти, и на диске). Вызывается только после того, как пользователь подтвердил потерю или выгрузил их в файл.</summary>
    public void DiscardAllForAccountSwitch()
    {
        Shutdown();
        lock (_sync)
        {
            _jobs = new List<ReceivingJob>();
            _loaded = true;
            _closed = false;
            _closedFile = null;
        }
        try
        {
            if (Directory.Exists(DataDir))
                Directory.Delete(DataDir, recursive: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приход в фоне: папка очереди не удалена при смене аккаунта ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Остановить воркер (выход из программы, стенд). Незавершённая попытка останется «отправляется» и после запуска сверится с сервером.</summary>
    public void Shutdown()
    {
        _stop?.Cancel();
        Wake();
    }

    // ------------------------------------------------------------------ вспомогательное

    private static bool SurelyNotPosted(ReceivingJob job, JobLine l)
    {
        if (l.Outcome == ReceivingLineOutcome.Rejected)
            return true;
        if (l.Outcome != ReceivingLineOutcome.Pending)
            return false;
        // Не отправлялась: с поставщиком — документ не уходил; без — по строке не было ни PATCH остатка, ни ответа.
        var st = job.State;
        lock (st.Gate)
        {
            if (job.Supplier is not null)
                return st.ReceiptOutcome is ReceivingReceiptOutcome.NotSent or ReceivingReceiptOutcome.Rejected;
            return !st.StockPatches.ContainsKey(l.Key) && !st.LineOutcome.ContainsKey(l.Key);
        }
    }

    private static string SignatureOf(ReceivingJob job) =>
        (job.BatchId is null ? "" : job.BatchId + "#" + job.BatchPart + "|") + (job.Supplier?.Id ?? "") + "|" + job.PaidNow + "|" + job.Source + "|"
        + string.Join(";", job.Lines.Select(l => $"{l.ProductId}:{l.Barcode}:{l.Quantity:0.###}:{l.PurchasePrice:0.##}").OrderBy(x => x, StringComparer.Ordinal));

    private NurMarketApiClient? Api => _api ?? ApiResolver?.Invoke();

    private void Wake()
    {
        TaskCompletionSource old;
        lock (_wakeSync)
        {
            old = _wakeTcs;
            _wakeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private Task WakeTask()
    {
        lock (_wakeSync)
            return _wakeTcs.Task;
    }

    private void RaiseChanged(ReceivingJob job)
    {
        ReceivingJob copy;
        lock (_sync)
            copy = job.Clone();
        Fire(JobChanged, copy);
    }

    private void RaiseFinished(ReceivingJob job)
    {
        ReceivingJob copy;
        TaskCompletionSource<ReceivingJob>? waiter;
        lock (_sync)
        {
            copy = job.Clone();
            _waiters.Remove(job.Id, out waiter);
        }
        waiter?.TrySetResult(copy);
        Fire(JobFinished, copy);
        PosLogger.Log($"Приход в фоне: задача {job.Id} — {job.Status}. {job.Summary}", "STOCK");
    }

    private static void Fire(Action<ReceivingJob>? handler, ReceivingJob arg)
    {
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action<ReceivingJob>)d)(arg);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приход в фоне: обработчик события упал ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
    }
}
