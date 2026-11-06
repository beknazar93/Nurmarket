using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

// 2026-11-06: очередь создания новых товаров на диске. Жалоба клиента: товар, заведённый без интернета, остаётся только в этой кассе
// (журнал: «Background product create failed for temp id …», DNS «Этот хост неизвестен»). Раньше фоновое создание пробовало ОДИН раз
// (поиск по штрихкоду, ожидание 60 с, один POST), сдавалось и не повторялось даже после перезапуска; временный товар потом принимали
// на склад — а на сервере его не было.
// Теперь задача (весь ProductEditRequest + временный id + ключ повтора Idempotency-Key) пишется на диск ДО любых запросов, воркер повторяет
// с растущей паузой (5 с … 5 мин) и сразу при возвращении связи; перед каждым POST — поиск «не создан ли уже» (штрихкод + название) и тот же ключ.
// Успех → временный товар в локальной базе заменяется настоящим (LocalProductEditor); приход, где остался временный id, подставит настоящий
// (PurchaseReceivingService.PostAsync → ResolveAsync) или подождёт создания товара. Отказ по существу (4xx кроме 408/429) — «Ошибка» с причиной,
// не повторяется, остальные задачи идут дальше.
// Файл: %APPDATA%\<NurMarketKassa|NurMarketOwner>\pending-products.json (данные компании: CompanyStoreRegistry).

/// <summary>Состояния задачи создания товара.</summary>
public static class PendingProductStatus
{
    /// <summary>Ждёт попытки.</summary>
    public const string Queued = "queued";

    /// <summary>Ждёт повтора (нет связи / сервер не ответил / пауза).</summary>
    public const string RetryWait = "retry";

    /// <summary>Товар создан на сервере (RealId известен).</summary>
    public const string Sent = "sent";

    /// <summary>Сервер отказал по существу — повторов нет.</summary>
    public const string Failed = "failed";
}

/// <summary>Одна задача очереди (лежит в файле).</summary>
public sealed class PendingProductJob
{
    public string TempId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public ProductEditRequest Request { get; set; } = new();
    public string Status { get; set; } = PendingProductStatus.Queued;
    public int Attempts { get; set; }
    public int HardAttempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? NextAttemptAt { get; set; }
    public bool WaitingForServer { get; set; }
    public bool MaybeSent { get; set; }
    public string? LastError { get; set; }
    public string? RealId { get; set; }
    public bool LocalApplied { get; set; }
    public DateTime? FinishedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status is PendingProductStatus.Queued or PendingProductStatus.RetryWait;

    public PendingProductJob Clone() => (PendingProductJob)MemberwiseClone();
}

/// <summary>Что узнал приход о товаре с временным id.</summary>
public enum PendingProductResolveKind
{
    /// <summary>Такого временного id в очереди нет — товар обычный.</summary>
    NotPending,

    /// <summary>Товар создан на сервере — <see cref="PendingProductResolve.RealId"/>.</summary>
    Resolved,

    /// <summary>Товар ещё не создан (нет связи, сервер не ответил) — приход подождёт.</summary>
    Waiting,

    /// <summary>Сервер отказал в создании товара — приход создаст его заново как новый.</summary>
    Rejected,
}

public sealed record PendingProductResolve(PendingProductResolveKind Kind, string? RealId = null, string? Error = null);

public sealed class PendingProductQueue
{
    public static PendingProductQueue Instance { get; } = new();

    // ------------------------------------------------------------------ настройки (стенд может подменить)

    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для стенда проверки: компания без входа в NurCRM.</summary>
    public static string? CompanyIdOverride { get; set; }

    /// <summary>Паузы между повторами: 5 с, 15 с, 30 с, 1 мин, 2 мин, 5 мин (дальше — каждые 5 мин, пока нет ответа сервера).</summary>
    public static TimeSpan[] RetryDelays { get; set; } =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];

    /// <summary>Сколько раз повторять при «странных» ответах сервера (500, не тот формат): потом «Ошибка». Обрывы связи и 502–504/408/520–524 не считаются.</summary>
    public const int MaxHardRetries = 6;

    /// <summary>Как часто проверять связь, пока сервер недоступен (дополнительно к событиям ServerOutageMonitor).</summary>
    public static TimeSpan ServerPoll { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Если первый POST мог дойти до сервера (таймаут), первую повторную попытку делаем не раньше этого срока: сервер отвечает и по 40–60 с,
    /// а второй товар заводить нельзя (2026-11-01, С-03).</summary>
    public static TimeSpan MaybeSentDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Страховка одной попытки.</summary>
    public static TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Сколько хранить завершённые задачи: для прихода, где остался временный id.</summary>
    public static TimeSpan KeepSent { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Откуда взять клиент каталога, если Resume ещё не вызывали.</summary>
    public static Func<ICatalogApiService?>? ApiResolver { get; set; } = () => PosApp.CatalogApi;

    /// <summary>Замена временного товара настоящим в локальной базе (стенд подменяет).</summary>
    public static Action<string>? DeleteLocalOverride { get; set; }

    public static Func<ProductEditRequest, string, string>? SaveLocalOverride { get; set; }

    /// <summary>Тост кассиру (стенд подменяет). По умолчанию — общий тост каталога.</summary>
    public static Action<string, bool>? ToastOverride { get; set; }

    // ------------------------------------------------------------------ события

    /// <summary>Любое изменение очереди (постановка, переход статуса, удаление). Может прийти не с UI-потока.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ состояние

    private readonly object _sync = new();
    private List<PendingProductJob> _jobs = new();
    private bool _loaded;
    private ICatalogApiService? _api;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private bool _subscribed;
    private readonly object _wakeSync = new();
    private TaskCompletionSource _wakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string CurrentCompany => CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";

    private static bool SameCompany(string jobCompany, string company) =>
        jobCompany.Length == 0 || company.Length == 0 || string.Equals(jobCompany, company, StringComparison.OrdinalIgnoreCase);

    private ICatalogApiService? Api => _api ?? ApiResolver?.Invoke();

    private static string DataDir => DataDirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName);

    private static string FilePath => Path.Combine(DataDir, "pending-products.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // ------------------------------------------------------------------ тексты

    /// <summary>«Неотправленных новых товаров: N» (значок, вопрос при выходе).</summary>
    public static string UnsentText(int n) => Tr.T($"Неотправленных новых товаров: {n}", $"Жөнөтүлбөгөн жаңы товарлар: {n}", $"Unsent new products: {n}",
        $"Gönderilmemiş yeni ürünler: {n}", $"Yuborilmagan yangi mahsulotlar: {n}");

    private static string QueuedToastText(int n) => Tr.T(
        $"Новый товар сохранён в кассе и будет создан на сервере, когда появится интернет. {UnsentText(n)}.",
        $"Жаңы товар кассада сакталды жана интернет пайда болгондо серверде түзүлөт. {UnsentText(n)}.",
        $"The new product is saved in the till and will be created on the server when the internet is back. {UnsentText(n)}.",
        $"Yeni ürün kasada kaydedildi, internet geldiğinde sunucuda oluşturulacak. {UnsentText(n)}.",
        $"Yangi mahsulot kassada saqlandi va internet paydo bo'lganda serverda yaratiladi. {UnsentText(n)}.");

    private static string FailedToastText(string name, string reason) => Tr.T(
        $"Сервер не принял новый товар «{name}»: {reason} Он остался только в этой кассе — исправьте данные и заведите товар заново.",
        $"Сервер жаңы «{name}» товарын кабыл алган жок: {reason} Ал ушул кассада гана калды — маалыматты оңдоп, товарды кайра түзүңүз.",
        $"The server did not accept the new product “{name}”: {reason} It stayed in this till only — fix the data and create the product again.",
        $"Sunucu yeni «{name}» ürününü kabul etmedi: {reason} Yalnızca bu kasada kaldı — verileri düzeltip ürünü yeniden oluşturun.",
        $"Server yangi «{name}» mahsulotini qabul qilmadi: {reason} U faqat shu kassada qoldi — ma'lumotni to'g'rilab, mahsulotni qayta yarating.");

    private static string SaveFailedText => Tr.T(
        "Не удалось записать новый товар в очередь отправки на диск — он сохранён только локально.",
        "Жаңы товарды дисктеги жөнөтүү кезегине жазуу мүмкүн болгон жок — ал локалдык гана сакталды.",
        "Could not write the new product to the send queue on disk — it is saved locally only.",
        "Yeni ürün diskteki gönderim kuyruğuna yazılamadı — yalnızca yerel olarak kaydedildi.",
        "Yangi mahsulotni diskdagi yuborish navbatiga yozib bo'lmadi — u faqat lokal saqlandi.");

    private static void Toast(string text, bool error)
    {
        try
        {
            if (ToastOverride is { } sink)
                sink(text, error);
            else
                CatalogCacheService.RaiseToast(text, error);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Новые товары в фоне: тост не показан ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ публичный API

    /// <summary>Поставить создание товара в очередь. Синхронно, без сети: запись на диск и пробуждение воркера. <paramref name="tempLocalId"/> — id, под которым товар уже лежит
    /// в локальной базе. <paramref name="maybeSent"/> = true — первый POST мог дойти до сервера (таймаут): первая повторная попытка не раньше <see cref="MaybeSentDelay"/>.
    /// Бросает InvalidOperationException, если файл не записался (товар остаётся только локально — вызывающий скажет об этом кассиру).</summary>
    public string Enqueue(ProductEditRequest request, string tempLocalId, bool maybeSent = false)
    {
        EnsureLoaded();
        // Глубокая копия: окно может продолжать менять свой request.
        var copy = JsonSerializer.Deserialize<ProductEditRequest>(JsonSerializer.Serialize(request, Json), Json) ?? throw new ArgumentException("request");
        if (string.IsNullOrWhiteSpace(copy.IdempotencyKey))
            copy.IdempotencyKey = Guid.NewGuid().ToString("N");
        var job = new PendingProductJob
        {
            TempId = tempLocalId,
            CompanyId = CurrentCompany,
            Request = copy,
            Status = maybeSent ? PendingProductStatus.RetryWait : PendingProductStatus.Queued,
            MaybeSent = maybeSent,
            NextAttemptAt = maybeSent ? DateTime.UtcNow + MaybeSentDelay : null,
            CreatedAt = DateTime.Now,
        };
        int unsent;
        lock (_sync)
        {
            _jobs.RemoveAll(j => string.Equals(j.TempId, tempLocalId, StringComparison.OrdinalIgnoreCase) && !j.IsActive); // повторное нажатие после «Ошибка»
            if (_jobs.Any(j => string.Equals(j.TempId, tempLocalId, StringComparison.OrdinalIgnoreCase)))
                return tempLocalId;
            _jobs.Add(job);
            if (!Save())
            {
                _jobs.Remove(job);
                throw new InvalidOperationException(SaveFailedText);
            }
            unsent = _jobs.Count(j => j.IsActive && SameCompany(j.CompanyId, job.CompanyId));
        }
        PosLogger.Log($"Новые товары в фоне: принят «{copy.Name}» (временный id {tempLocalId})"
                      + (BackgroundReceivingQueue.NoConnectionNow ? " — связи нет, товар на диске и будет создан на сервере, когда она появится." : "."), "WAREHOUSE");
        RaiseChanged();
        Toast(QueuedToastText(unsent), false);
        EnsureWorker();
        Wake();
        return tempLocalId;
    }

    /// <summary>Копии задач текущей компании (новые сверху).</summary>
    public IReadOnlyList<PendingProductJob> Snapshot()
    {
        EnsureLoaded();
        var company = CurrentCompany;
        lock (_sync)
            return Enumerable.Reverse(_jobs).Where(j => SameCompany(j.CompanyId, company)).Select(j => j.Clone()).ToList();
    }

    /// <summary>Есть ли хоть одна задача (быстрая проверка для прихода: без очереди ничего подставлять не нужно).</summary>
    public bool HasAny
    {
        get
        {
            EnsureLoaded();
            lock (_sync)
                return _jobs.Count > 0;
        }
    }

    /// <summary>Задачи текущей компании, которые ещё ждут создания.</summary>
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

    /// <summary>Задачи любой компании, которые ещё ждут создания (вопрос при выходе).</summary>
    public int ActiveCountAll
    {
        get
        {
            EnsureLoaded();
            lock (_sync)
                return _jobs.Count(j => j.IsActive);
        }
    }

    /// <summary>Задачи текущей компании с отказом сервера («Ошибка»).</summary>
    public int FailedCount
    {
        get
        {
            EnsureLoaded();
            var company = CurrentCompany;
            lock (_sync)
                return _jobs.Count(j => j.Status == PendingProductStatus.Failed && SameCompany(j.CompanyId, company));
        }
    }

    /// <summary>Ждущие и отказанные задачи любой компании — для вопроса при смене компании.</summary>
    public int UnsentCountAll
    {
        get
        {
            EnsureLoaded();
            lock (_sync)
                return _jobs.Count(j => j.IsActive || j.Status == PendingProductStatus.Failed);
        }
    }

    /// <summary>Копии задач любой компании, ещё не доехавших до сервера (выгрузка в файл при смене компании).</summary>
    public IReadOnlyList<PendingProductJob> SnapshotUnsentAll()
    {
        EnsureLoaded();
        lock (_sync)
            return _jobs.Where(j => j.IsActive || j.Status == PendingProductStatus.Failed).Select(j => j.Clone()).ToList();
    }

    /// <summary>После входа и загрузки компании: прочитать файл и запустить воркер (незавершённые задачи продолжаются).</summary>
    public void Resume(ICatalogApiService api)
    {
        _api = api;
        EnsureLoaded();
        EnsureWorker();
        Wake();
    }

    /// <summary>«Повторить» у задачи с ошибкой.</summary>
    public bool Retry(string tempId)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => string.Equals(j.TempId, tempId, StringComparison.OrdinalIgnoreCase));
            if (job is null || job.Status != PendingProductStatus.Failed)
                return false;
            job.Status = PendingProductStatus.Queued;
            job.Attempts = 0;
            job.HardAttempts = 0;
            job.NextAttemptAt = null;
            job.LastError = null;
            job.FinishedAt = null;
            Save();
        }
        RaiseChanged();
        EnsureWorker();
        Wake();
        return true;
    }

    /// <summary>«Убрать из списка» задачу с ошибкой (товар остаётся в этой кассе, на сервер не пойдёт).</summary>
    public bool Dismiss(string tempId)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => string.Equals(j.TempId, tempId, StringComparison.OrdinalIgnoreCase));
            if (job is null || job.Status != PendingProductStatus.Failed)
                return false;
            _jobs.Remove(job);
            Save();
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Остановить воркер (выход из программы, стенд).</summary>
    public void Shutdown()
    {
        try
        {
            _stop?.Cancel();
            Wake();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Только для стенда: остановить воркер и дождаться его конца (иначе опоздавший ответ сервера портит состояние следующего теста).</summary>
    internal Task StopAsync()
    {
        Shutdown();
        return _worker ?? Task.CompletedTask;
    }

    /// <summary>Смена компании: остановить воркер и забыть все задачи (память и диск). Вызывается после того, как пользователь подтвердил потерю или выгрузил их в файл.</summary>
    public void DiscardAllForAccountSwitch()
    {
        Shutdown();
        lock (_sync)
        {
            _jobs = new List<PendingProductJob>();
            _loaded = true;
        }
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            if (File.Exists(FilePath + ".bak"))
                File.Delete(FilePath + ".bak");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Новые товары в фоне: файл очереди не удалён при смене аккаунта ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Смена компании не состоялась — воркер, остановленный перед этим, запускаем снова.</summary>
    public void ResumeAfterAbortedSwitch()
    {
        bool any;
        lock (_sync)
            any = _jobs.Any(j => j.IsActive);
        if (any)
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
                EnsureWorker();
            });
    }

    // ------------------------------------------------------------------ для прихода

    /// <summary>Приход встретил товар с id <paramref name="productId"/>: если это временный id из очереди — вернуть настоящий (создав товар на сервере прямо сейчас, если ещё не создан).
    /// Waiting — создать не удалось из-за связи: приход должен подождать (не отправлять товар с несуществующим на сервере id).</summary>
    public async Task<PendingProductResolve> ResolveAsync(string productId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return new PendingProductResolve(PendingProductResolveKind.NotPending);
        EnsureLoaded();
        PendingProductJob? job;
        lock (_sync)
            job = _jobs.FirstOrDefault(j => string.Equals(j.TempId, productId, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return new PendingProductResolve(PendingProductResolveKind.NotPending);

        var done = Evaluate(job);
        if (done is not null)
            return done;
        if (!ServerReady())
            return new PendingProductResolve(PendingProductResolveKind.Waiting);
        // Первый POST мог дойти до сервера (таймаут при создании) и ещё обрабатываться: до конца паузы второй не шлём (см. MaybeSentDelay) — приход подождёт.
        lock (_sync)
            if (job.MaybeSent && !job.WaitingForServer && job.IsActive && job.NextAttemptAt is { } holdUntil && holdUntil > DateTime.UtcNow)
                return new PendingProductResolve(PendingProductResolveKind.Waiting);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            done = Evaluate(job);
            if (done is null)
            {
                await AttemptAsync(job, ct).ConfigureAwait(false);
                done = Evaluate(job);
            }
            return done ?? new PendingProductResolve(PendingProductResolveKind.Waiting);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PendingProductResolve? Evaluate(PendingProductJob job)
    {
        lock (_sync)
        {
            if (job.Status == PendingProductStatus.Sent && !string.IsNullOrEmpty(job.RealId))
                return new PendingProductResolve(PendingProductResolveKind.Resolved, job.RealId);
            if (job.Status == PendingProductStatus.Failed)
                return new PendingProductResolve(PendingProductResolveKind.Rejected, null, job.LastError);
        }
        return null;
    }

    // ------------------------------------------------------------------ загрузка и запись

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                var text = AtomicFile.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(text))
                    return;
                var list = JsonSerializer.Deserialize<List<PendingProductJob?>>(text, Json) ?? new();
                _jobs = list.Where(j => j is not null && !string.IsNullOrWhiteSpace(j.TempId)).Select(j => j!).ToList();
                // Перезапуск: «связи нет» устарело — первая попытка сразу, при живой связи.
                foreach (var j in _jobs.Where(j => j.IsActive))
                {
                    j.WaitingForServer = false;
                    if (!j.MaybeSent)
                        j.NextAttemptAt = null;
                }
            }
            catch (Exception ex)
            {
                // Файл не прочитать — НЕ пустая очередь, но и закрывать нечего: начинаем с пустой (повреждённый файл AtomicFile уже убрал в .bad), причина в журнале.
                PosLogger.Log($"Новые товары в фоне: файл очереди не прочитан ({ex.GetType().Name}: {ex.Message}) — очередь начинается пустой.", "ERROR");
                _jobs = new List<PendingProductJob>();
            }
        }
    }

    /// <summary>Запись под <c>lock (_sync)</c>. Завершённые старше <see cref="KeepSent"/> убираются.</summary>
    private bool Save()
    {
        try
        {
            var now = DateTime.Now;
            _jobs.RemoveAll(j => j.Status == PendingProductStatus.Sent && j.LocalApplied && (now - (j.FinishedAt ?? j.CreatedAt)) > KeepSent);
            Directory.CreateDirectory(DataDir);
            AtomicFile.WriteAllTextWithRetry(FilePath, JsonSerializer.Serialize(_jobs, Json));
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Новые товары в фоне: файл очереди не записан ({ex.Message}).", "ERROR");
            return false;
        }
    }

    // ------------------------------------------------------------------ воркер

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

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Новые товары в фоне: обработчик события упал ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
    }

    private void EnsureWorker()
    {
        lock (_sync)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                // Связь вернулась — повторять сразу, не дожидаясь расписания.
                ServerOutageMonitor.StateChanged += (_, _) => Wake();
                ServerOutageMonitor.Recovered += () =>
                {
                    ExpediteWaiting();
                    Wake();
                };
            }
            if (_worker is { IsCompleted: false })
                return;
            _stop = new CancellationTokenSource();
            var stop = _stop.Token;
            _worker = Task.Run(() => WorkerLoopAsync(stop));
        }
    }

    /// <summary>Связь вернулась — пауза у задач, ждавших связь, снимается.</summary>
    internal void ExpediteWaiting()
    {
        var any = false;
        lock (_sync)
        {
            foreach (var j in _jobs.Where(j => j.Status == PendingProductStatus.RetryWait && j.WaitingForServer && j.NextAttemptAt is not null))
            {
                j.NextAttemptAt = null;
                any = true;
            }
            if (any)
                Save();
        }
        if (any)
            PosLogger.Log("Новые товары в фоне: связь с сервером вернулась — ждущие товары создаются сразу.", "WAREHOUSE");
    }

    /// <summary>2026-11-06 («Неотправленное → Отправить сейчас»): снять паузу повтора у этого товара и у всех, кто стоит в очереди раньше (порядок не нарушаем), разбудить воркер.
    /// Создаёт по-прежнему тот же воркер, с тем же ключом повтора (IdempotencyKey запроса) и проверкой «не создан ли уже». false — такой активной задачи нет.</summary>
    public bool ForceNow(string tempId)
    {
        EnsureLoaded();
        var any = false;
        lock (_sync)
        {
            var company = CurrentCompany;
            var target = _jobs.FirstOrDefault(j => j.IsActive && SameCompany(j.CompanyId, company) && string.Equals(j.TempId, tempId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return false;
            foreach (var j in _jobs)
            {
                if (j.IsActive && SameCompany(j.CompanyId, company) && j.Status == PendingProductStatus.RetryWait && j.NextAttemptAt is not null)
                {
                    j.NextAttemptAt = null;
                    any = true;
                }
                if (ReferenceEquals(j, target))
                    break;
            }
            if (any)
                Save();
        }
        if (any)
            PosLogger.Log($"Новые товары в фоне: товар {tempId} создаётся по просьбе кассира («Неотправленное»), паузы повтора сняты.", "WAREHOUSE");
        EnsureWorker();
        Wake();
        return true;
    }

    private bool ServerReady() => !ServerOutageMonitor.IsOutage && !OfflineModeHelper.SellLocally;

    private async Task WorkerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var wake = WakeTask();

                // Товар создан, а локальная замена не дописана (перезапуск посреди шага) — дописываем.
                PendingProductJob? toApply;
                lock (_sync)
                    toApply = _jobs.FirstOrDefault(j => j.Status == PendingProductStatus.Sent && !j.LocalApplied && !string.IsNullOrEmpty(j.RealId));
                if (toApply is not null)
                {
                    if (!ApplyLocal(toApply))
                        await Task.WhenAny(wake, Task.Delay(TimeSpan.FromSeconds(5), stop)).ConfigureAwait(false);
                    continue;
                }

                PendingProductJob? job;
                TimeSpan? wait;
                lock (_sync)
                    job = PickNextLocked(out wait);
                if (job is null)
                {
                    await Task.WhenAny(wake, Task.Delay(wait ?? TimeSpan.FromMinutes(1), stop)).ConfigureAwait(false);
                    continue;
                }

                if (!ServerReady() || Api is null)
                {
                    // Авария сервера / офлайн-режим / нет клиента: попытки НЕ расходуются, ждём связь.
                    SetWaiting(job);
                    await Task.WhenAny(wake, Task.Delay(ServerPoll, stop)).ConfigureAwait(false);
                    continue;
                }

                await _gate.WaitAsync(stop).ConfigureAwait(false);
                try
                {
                    await AttemptAsync(job, stop).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Новые товары в фоне: сбой цикла очереди ({ex.GetType().Name}: {ex.Message}).", "ERROR");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Первая готовая задача текущей компании. Задача в паузе другие НЕ блокирует (товары независимы); готовых нет — сколько ждать до ближайшей.</summary>
    private PendingProductJob? PickNextLocked(out TimeSpan? wait)
    {
        wait = null;
        var company = CurrentCompany;
        var now = DateTime.UtcNow;
        PendingProductJob? soonest = null;
        foreach (var j in _jobs)
        {
            if (!j.IsActive || !SameCompany(j.CompanyId, company))
                continue;
            if (j.NextAttemptAt is { } at && at > now)
            {
                if (soonest is null || at < soonest.NextAttemptAt)
                    soonest = j;
                continue;
            }
            return j;
        }
        if (soonest?.NextAttemptAt is { } next)
            wait = next - now;
        return null;
    }

    private void SetWaiting(PendingProductJob job)
    {
        lock (_sync)
        {
            if (job.Status == PendingProductStatus.RetryWait && job.WaitingForServer && job.NextAttemptAt is null)
                return;
            job.Status = PendingProductStatus.RetryWait;
            job.WaitingForServer = true;
            if (!job.MaybeSent)
                job.NextAttemptAt = null;
            Save();
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ одна попытка

    /// <summary>Одна попытка создания. Вызывать под <c>_gate</c>. Сама классифицирует сбой и назначает следующий шаг; наружу — только отмену выхода из программы.</summary>
    private async Task AttemptAsync(PendingProductJob job, CancellationToken stop)
    {
        lock (_sync)
        {
            if (!job.IsActive)
                return;
        }
        var api = Api;
        if (api is null)
        {
            SetWaiting(job);
            return;
        }

        string realId;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            cts.CancelAfter(AttemptTimeout);
            var request = job.Request;
            // 2026-11-01 (С-03): первый POST мог дойти до сервера — прежде чем создавать (ещё раз), ищем товар по штрихкоду.
            var found = await FindOwnCreatedAsync(api, request, cts.Token).ConfigureAwait(false);
            JsonElement created;
            if (found is { } already)
            {
                PosLogger.Log($"Новые товары в фоне: «{request.Name}» уже есть на сервере (предыдущая попытка дошла) — второй не создаём.", "WAREHOUSE");
                created = already;
            }
            else
            {
                try
                {
                    created = await api.CreateProductAsync(request, cts.Token).ConfigureAwait(false);
                }
                catch (ApiException)
                {
                    // Отказ, а товар на сервере уже есть (создала предыдущая попытка) — берём его, иначе ошибка как есть.
                    var late = await FindOwnCreatedAsync(api, request, cts.Token).ConfigureAwait(false);
                    if (late is null)
                        throw;
                    created = late.Value;
                }
            }
            realId = ProductCatalogMapper.TryId(created) ?? "";
            if (string.IsNullOrEmpty(realId))
                throw new InvalidOperationException("сервер не вернул номер созданного товара");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return; // выход из программы: задача остаётся в очереди
        }
        catch (Exception ex)
        {
            OnFailure(job, ex);
            return;
        }

        lock (_sync)
        {
            job.Status = PendingProductStatus.Sent;
            job.RealId = realId;
            job.FinishedAt = DateTime.Now;
            job.NextAttemptAt = null;
            job.WaitingForServer = false;
            job.LastError = null;
            Save();
        }
        PosLogger.Log($"Новые товары в фоне: «{job.Request.Name}» создан на сервере (временный id {job.TempId} → {realId}), попыток {job.Attempts + 1}.", "WAREHOUSE");
        ApplyLocal(job);
        RaiseChanged();
    }

    /// <summary>Заменить временный товар настоящим в локальной базе. Повторяемо (после перезапуска дописывается недоделанное).</summary>
    private bool ApplyLocal(PendingProductJob job)
    {
        try
        {
            if (!string.Equals(job.RealId, job.TempId, StringComparison.OrdinalIgnoreCase))
            {
                if (DeleteLocalOverride is { } del)
                    del(job.TempId);
                else
                    LocalProductEditor.DeleteLocally(job.TempId);
            }
            if (SaveLocalOverride is { } save)
                save(job.Request, job.RealId!);
            else
                LocalProductEditor.SaveLocally(job.Request, job.RealId);
            lock (_sync)
            {
                job.LocalApplied = true;
                Save();
            }
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Новые товары в фоне: замена временного товара {job.TempId} настоящим не удалась ({ex.GetType().Name}: {ex.Message}) — повторим.", "WARNING");
            return false;
        }
    }

    private void OnFailure(PendingProductJob job, Exception ex)
    {
        var code = (ex as ApiException)?.StatusCode;
        TimeSpan? delay = null;
        var waitingForServer = false;
        string? failReason = null;

        lock (_sync)
        {
            if (code == 401)
            {
                // Нет входа / сессия истекла: попытки не тратим, проверим через ServerPoll.
                delay = ServerPoll;
                waitingForServer = true;
            }
            else if (code == 429)
            {
                var block = ApiThrottle.RemainingBlock + TimeSpan.FromSeconds(1);
                delay = block > TimeSpan.FromSeconds(5) ? block : TimeSpan.FromSeconds(5);
            }
            else if (IsNoAnswer(ex))
            {
                // Сервер недоступен (обрыв, таймаут, 502–504, 408, 520–524): попыток не жалеем, пауза растёт до 5 минут.
                job.Attempts++;
                job.MaybeSent = job.MaybeSent || ex is OperationCanceledException;
                delay = RetryDelays[Math.Min(job.Attempts - 1, RetryDelays.Length - 1)];
                waitingForServer = true;
            }
            else if (ex is ApiException { StatusCode: >= 400 and < 500 })
            {
                failReason = ServerOutageMonitor.FriendlyError(ex, showServerText: true);
            }
            else
            {
                // Прочее (500, непонятный ответ): несколько повторов, потом «Ошибка» (не бесконечно).
                job.Attempts++;
                job.HardAttempts++;
                if (job.HardAttempts > MaxHardRetries)
                    failReason = ServerOutageMonitor.FriendlyError(ex, showServerText: true);
                else
                    delay = RetryDelays[Math.Min(job.HardAttempts - 1, RetryDelays.Length - 1)];
            }

            job.LastError = failReason ?? ServerOutageMonitor.Describe(ex);
            if (failReason is not null)
            {
                job.Status = PendingProductStatus.Failed;
                job.NextAttemptAt = null;
                job.WaitingForServer = false;
                job.FinishedAt = DateTime.Now;
            }
            else
            {
                job.Status = PendingProductStatus.RetryWait;
                job.WaitingForServer = waitingForServer;
                job.NextAttemptAt = DateTime.UtcNow + (delay ?? ServerPoll);
            }
            Save();
        }

        if (failReason is not null)
        {
            PosLogger.Log($"Новые товары в фоне: «{job.Request.Name}» (временный id {job.TempId}) отклонён сервером: {ServerOutageMonitor.Describe(ex)}.", "WAREHOUSE");
            Toast(FailedToastText(job.Request.Name, failReason), true);
        }
        else
        {
            PosLogger.Log($"Новые товары в фоне: «{job.Request.Name}» не создан ({ServerOutageMonitor.Describe(ex)}); следующая попытка через {(delay ?? ServerPoll).TotalSeconds:0} с или сразу при возвращении связи.", "WAREHOUSE");
        }
        RaiseChanged();
    }

    private static bool IsNoAnswer(Exception ex) =>
        ex is ApiException api ? BackgroundReceivingQueue.IsNoAnswerStatus(api.StatusCode) : ServerOutageMonitor.IsServerFailure(ex);

    /// <summary>Ищет на сервере товар по штрихкоду (основной товар и его доп. штрихкоды не ищем — только свой). Нашёлся с тем же названием — вернуть; иначе (или поиск не удался) null.
    /// Без штрихкода искать нечем — защита только ключом повтора.</summary>
    private static async Task<JsonElement?> FindOwnCreatedAsync(ICatalogApiService api, ProductEditRequest request, CancellationToken ct)
    {
        var barcode = request.Barcode?.Trim();
        if (string.IsNullOrEmpty(barcode))
            return null;
        try
        {
            var found = await api.FindWarehouseProductByBarcodeAsync(barcode, ct).ConfigureAwait(false);
            if (found is not { ValueKind: JsonValueKind.Object } product)
                return null;
            // Товар с таким штрихкодом, но другим названием — не наш (заведён раньше): не подменяем.
            if (product.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                && !string.Equals(nameProp.GetString()?.Trim(), request.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            return product;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Новые товары в фоне: поиск товара по штрихкоду не удался ({ex.GetType().Name}).", "WAREHOUSE");
            return null;
        }
    }
}
