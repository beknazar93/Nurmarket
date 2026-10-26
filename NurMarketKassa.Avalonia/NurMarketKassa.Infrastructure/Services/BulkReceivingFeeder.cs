using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (docs/ТЗ Склад — массовое добавление — скан 3 колонки, Excel, CSV (26.10.2026).md), пакет М1.
// Подача большой пачки (до 50 000 строк) в BackgroundReceivingQueue ЧАСТЯМИ. Очередь целиком переписывает queue.json после каждого шага
// (Worker.cs → Save), поэтому задача на 15 000 строк раздула бы файл до ~6 МБ и переписывала бы его десятки тысяч раз. Подающий держит
// пачку в своих файлах и ставит в очередь части по PartSize строк, не больше MaxParallelParts одновременно; следующая — когда закончилась
// предыдущая. Деньги и остатки считает только PurchaseReceivingService.PostAsync (через задачу очереди), здесь их логики нет.
//
// Файлы (папка очереди background-receipts, у стенда — DataDirOverride):
//  • bulk-pending.json — малый индекс пачек: метка, число частей, какие части поставлены, итог по каждой части;
//  • bulk-<пачка>.lines.json — все строки пачки с номерами строк файла; пишется ОДИН раз при Start и удаляется, когда пачка закончена
//    (индекс переписывается после каждой части, поэтому строки в нём не лежат: иначе 15 000 строк × 150 частей = лишние гигабайты записи).
// Переживает закрытие окна и перезапуск: Resume() сверяет индекс с очередью по паре (BatchId, BatchPart) — часть, уже стоящая в очереди,
// второй раз не ставится, даже если программа упала между постановкой и записью индекса.

/// <summary>Одна не проведённая строка пачки — для окна «Скачать строки с ошибками» (номер строки файла).</summary>
public sealed class BulkFailedLine
{
    public int? FileRow { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>true — исход неизвестен, нужна проверка остатка вручную (в окне «Приходы в фоне» такая строка не возвращается).</summary>
    public bool NeedsCheck { get; set; }
}

/// <summary>Итог одной части пачки (по задаче очереди).</summary>
public sealed class BulkPartResult
{
    public int Part { get; set; }
    public string? JobId { get; set; }
    /// <summary>done | partial | failed | needs_check | lost (задача пропала из очереди).</summary>
    public string Status { get; set; } = "";
    public int Lines { get; set; }
    public int Posted { get; set; }
    public int Created { get; set; }
    /// <summary>Строки, которые не приняты (включая «нужна проверка»).</summary>
    public int Failed { get; set; }
    public int Check { get; set; }
    public List<BulkFailedLine> FailedRows { get; set; } = new();
}

/// <summary>Пачка в файле индекса. Строки (<see cref="Lines"/>) в индекс не пишутся.</summary>
public sealed class BulkBatch
{
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Employee { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalLines { get; set; }
    public int PartSize { get; set; }
    public int PartCount { get; set; }
    public bool Cancelled { get; set; }
    public bool Finished { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Номера частей, которые подающий уже поставил в очередь (страховка: поставлена, но в очереди её нет — «пропала», а не «ставим ещё раз»).</summary>
    public List<int> EnqueuedParts { get; set; } = new();
    public List<BulkPartResult> Parts { get; set; } = new();

    [JsonIgnore] public List<JobLine>? Lines { get; set; }
    [JsonIgnore] public int InFlight { get; set; }

    internal int LinesInPart(int part) => Math.Max(0, Math.Min(PartSize, TotalLines - part * PartSize));
}

/// <summary>Снимок пачки для окон (копия): ход и итог «Создано N, принято M, ошибок K».</summary>
public sealed class BulkBatchInfo
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string CompanyId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int TotalLines { get; init; }
    public int PartCount { get; init; }
    /// <summary>Части, по которым есть итог (проведены или окончательно не проведены).</summary>
    public int PartsFinished { get; init; }
    public int PartsInFlight { get; init; }
    /// <summary>Строки частей, по которым есть итог («3 400 из 14 800»).</summary>
    public int ProcessedLines { get; init; }
    public int Created { get; init; }
    public int Received { get; init; }
    /// <summary>Не приняты (включая «нужна проверка»).</summary>
    public int Failed { get; init; }
    public int NeedsCheck { get; init; }
    /// <summary>Отмена: строки частей, которые не успели поставить в очередь.</summary>
    public int NotSent { get; init; }
    public bool Cancelled { get; init; }
    public bool Finished { get; init; }
    /// <summary>Подающий не смог поставить следующую часть (очередь закрыта/диск) — текст для кассира.</summary>
    public string? LastError { get; init; }
    public IReadOnlyList<BulkFailedLine> FailedRows { get; init; } = Array.Empty<BulkFailedLine>();
    /// <summary>Задачи очереди с не принятыми строками: «Повторить ошибочные» → <c>BackgroundReceivingQueue.TakeBackLines(id)</c>.</summary>
    public IReadOnlyList<string> FailedJobIds { get; init; } = Array.Empty<string>();

    public string SummaryText => Tr.T($"Создано {Created}, принято {Received}, ошибок {Failed}",
        $"Түзүлдү {Created}, кабыл алынды {Received}, ката {Failed}",
        $"Created {Created}, received {Received}, errors {Failed}",
        $"Oluşturulan {Created}, kabul edilen {Received}, hata {Failed}",
        $"Yaratildi {Created}, qabul qilindi {Received}, xato {Failed}");
}

public sealed class BulkReceivingFeeder
{
    public static BulkReceivingFeeder Instance { get; } = new();

    /// <summary>Имя крючка подготовки в очереди (<see cref="BackgroundReceivingQueue.RegisterPreparer"/>); у задач пачки <see cref="ReceivingJob.Preparer"/>.</summary>
    public const string PreparerName = "bulk";

    // ------------------------------------------------------------------ настройки (стенд может менять)

    /// <summary>Только для стенда: другая папка (по умолчанию — папка очереди; если у очереди она подменена, берётся её же).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для стенда: компания без входа в NurCRM (по умолчанию — как у очереди).</summary>
    public static string? CompanyIdOverride { get; set; }

    /// <summary>Строк в одной части (одна задача очереди).</summary>
    public static int PartSize { get; set; } = 100;

    /// <summary>Сколько частей одной пачки может одновременно стоять в очереди.</summary>
    public static int MaxParallelParts { get; set; } = 2;

    /// <summary>Через сколько повторить постановку части, если очередь отказала (файл занят и т. п.) и подавать больше нечем будить.</summary>
    public static TimeSpan RetryPumpDelay { get; set; } = TimeSpan.FromSeconds(5);

    private const int KeepFinishedDays = 7;
    private const int MaxFailedRowsPerPart = 100;
    private const int MaxFailedRowsPerBatch = 2000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ------------------------------------------------------------------ события (приходят НЕ из потока окна — Dispatcher.UIThread.Post)

    /// <summary>Пачка изменилась: поставлена, закончилась очередная часть, отмена.</summary>
    public event Action<BulkBatchInfo>? BatchChanged;

    /// <summary>Все части пачки закончены (или пачку отменили и в очереди ничего не осталось): итог «Создано N, принято M, ошибок K».</summary>
    public event Action<BulkBatchInfo>? BatchFinished;

    // ------------------------------------------------------------------ состояние

    private readonly object _gate = new();
    private List<BulkBatch> _batches = new();
    private bool _loaded;
    private bool _closed;
    private bool _subscribed;

    private static string DataDir => DataDirOverride ?? BackgroundReceivingQueue.DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "background-receipts");

    private static string IndexPath => Path.Combine(DataDir, "bulk-pending.json");
    private static string LinesPath(string batchId) => Path.Combine(DataDir, $"bulk-{batchId}.lines.json");

    private static string CurrentCompany => CompanyIdOverride ?? BackgroundReceivingQueue.CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";

    private static bool SameCompany(string batchCompany, string company) =>
        batchCompany.Length == 0 || company.Length == 0 || string.Equals(batchCompany, company, StringComparison.OrdinalIgnoreCase);

    /// <summary>true — файл индекса повреждён и сохранён отдельно; <see cref="ClosedText"/> — что сказать кассиру.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _closed;
            }
        }
    }

    public string ClosedText => Tr.T(
        "Файл очереди массового добавления повреждён и сохранён отдельно — массовая загрузка сейчас недоступна. Обратитесь в поддержку.",
        "Жаппай кошуу кезегинин файлы бузулган жана өзүнчө сакталды — жаппай жүктөө азыр жеткиликсиз. Колдоо кызматына кайрылыңыз.",
        "The bulk add queue file is damaged and was saved separately — bulk loading is unavailable now. Contact support.",
        "Toplu ekleme kuyruğu dosyası bozuk ve ayrı kaydedildi — toplu yükleme şu anda kullanılamıyor. Destekle iletişime geçin.",
        "Ommaviy qo'shish navbati fayli buzilgan va alohida saqlandi — ommaviy yuklash hozir mavjud emas. Qo'llab-quvvatlashga murojaat qiling.");

    private static string SaveFailedText => Tr.T(
        "Не удалось записать пачку на диск — строки остались в списке, повторите.",
        "Топтомду дискке жазуу мүмкүн болгон жок — саптар тизмеде калды, кайталаңыз.",
        "Could not save the batch to disk — the lines stay in the list, try again.",
        "Toplu yükleme diske yazılamadı — satırlar listede kaldı, tekrar deneyin.",
        "To'plamni diskka yozib bo'lmadi — qatorlar ro'yxatda qoldi, qayta urining.");

    // ------------------------------------------------------------------ публичный API

    /// <summary>Принять пачку. Синхронно (без сети): снимок строк, запись файлов на диск, постановка первых частей в очередь.
    /// <paramref name="fileRows"/> — номера строк файла параллельно <paramref name="lines"/> (для «Скачать строки с ошибками»), можно null.
    /// Бросает InvalidOperationException с понятным текстом, если очередь/индекс закрыты или файлы не записались (строки окна не трогать).</summary>
    public string Start(IReadOnlyList<ReceivingLineVm> lines, string label, IReadOnlyList<int>? fileRows = null)
    {
        if (lines.Count == 0)
            throw new ArgumentException("нет строк", nameof(lines));
        EnsureSubscribed();
        BulkBatch batch;
        lock (_gate)
        {
            EnsureLoaded();
            if (_closed)
                throw new InvalidOperationException(ClosedText);
            if (BackgroundReceivingQueue.Instance.IsClosed)
                throw new InvalidOperationException(BackgroundReceivingQueue.Instance.ClosedText);

            var size = Math.Max(1, PartSize);
            batch = new BulkBatch
            {
                Id = Guid.NewGuid().ToString("N"),
                CompanyId = CurrentCompany,
                Label = label ?? "",
                Employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId,
                CreatedAt = DateTime.Now,
                TotalLines = lines.Count,
                PartSize = size,
                PartCount = (lines.Count + size - 1) / size,
                Lines = lines.Select(JobLine.From).ToList(),
            };
            for (var i = 0; i < batch.Lines.Count; i++)
                if (fileRows is not null && i < fileRows.Count)
                    batch.Lines[i].FileRow = fileRows[i];

            if (!WriteLines(batch))
                throw new InvalidOperationException(SaveFailedText);
            _batches.Add(batch);
            if (!SaveIndexLocked())
            {
                _batches.Remove(batch);
                TryDelete(LinesPath(batch.Id));
                throw new InvalidOperationException(SaveFailedText);
            }
        }

        Pump();

        lock (_gate)
        {
            // Ни одной части не поставлено (очередь отказала) — пачка не принята: строки остаются у окна.
            if (batch.EnqueuedParts.Count == 0 && !batch.Finished)
            {
                var why = batch.LastError ?? SaveFailedText;
                _batches.Remove(batch);
                TryDelete(LinesPath(batch.Id));
                SaveIndexLocked();
                throw new InvalidOperationException(why);
            }
        }
        PosLogger.Log($"Массовое добавление: пачка {batch.Id} «{batch.Label}» принята — строк {batch.TotalLines}, частей {batch.PartCount} по {batch.PartSize}.", "STOCK");
        return batch.Id;
    }

    /// <summary>После входа и запуска очереди: прочитать индекс, сверить с очередью и продолжить подачу (вызывает BackgroundReceiptsHost.Start).</summary>
    public void Resume()
    {
        EnsureSubscribed();
        Pump();
    }

    /// <summary>Снимки пачек текущей компании (новые сверху), в том числе законченные не старше 7 дней.</summary>
    public IReadOnlyList<BulkBatchInfo> Batches
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                var company = CurrentCompany;
                return _batches.Where(b => SameCompany(b.CompanyId, company)).OrderByDescending(b => b.CreatedAt).Select(ToInfo).ToList();
            }
        }
    }

    public BulkBatchInfo? Get(string batchId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var b = _batches.FirstOrDefault(x => x.Id == batchId);
            return b is null ? null : ToInfo(b);
        }
    }

    /// <summary>Отменить ещё не поставленные части: поставленные в очередь идут до конца. Возвращает число строк, которые уже не уйдут.</summary>
    public int CancelPending(string batchId)
    {
        var notSent = 0;
        lock (_gate)
        {
            EnsureLoaded();
            var b = _batches.FirstOrDefault(x => x.Id == batchId);
            if (b is null || b.Finished)
                return 0;
            b.Cancelled = true;
            notSent = NotSentLines(b);
            SaveIndexLocked();
        }
        PosLogger.Log($"Массовое добавление: пачка {batchId} — отмена, не поставлено строк: {notSent}.", "STOCK");
        Pump();
        return notSent;
    }

    /// <summary>Убрать законченную пачку из списка (итог уже показан).</summary>
    public bool Dismiss(string batchId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var b = _batches.FirstOrDefault(x => x.Id == batchId);
            if (b is null || !b.Finished)
                return false;
            _batches.Remove(b);
            SaveIndexLocked();
            return true;
        }
    }

    // ------------------------------------------------------------------ подготовка задачи (крючок очереди)

    /// <summary>Проверка штрихкода перед созданием: у каждой НОВОЙ строки со штрихкодом и без ProductId — товар по штрихкоду на сервере
    /// (<c>FindWarehouseProductByBarcodeAsync</c>, только чтение, повтор безопасен). Товар есть (завели на сайте или другой кассой, или
    /// в прошлой части этой же пачки) — строка становится приходом к нему, а не дублем. Сервер не ответил — исключение: попытка
    /// считается неудачной, очередь повторит сама. Строки, для которых прошлая попытка уже слала создание, пропускаются:
    /// их сверяет сам PostAsync (CreateAttempted). Крючок получает КОПИЮ задачи; изменения возвращает очередь.</summary>
    internal static async Task PrepareJobAsync(ReceivingJob job, CancellationToken ct)
    {
        var api = PosApp.CatalogApi ?? throw new InvalidOperationException("нет клиента API");
        var st = job.State;
        var todo = job.Lines.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown
                                        && l.IsNew && string.IsNullOrWhiteSpace(l.ProductId) && !string.IsNullOrWhiteSpace(l.Barcode)).ToList();
        foreach (var line in todo)
        {
            ct.ThrowIfCancellationRequested();
            bool attempted;
            lock (st.Gate)
                attempted = st.CreateAttempted.Contains(line.Key);
            if (attempted)
                continue;

            var code = line.Barcode.Trim();
            var found = await api.FindWarehouseProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            // 12 ↔ 13 цифр (UPC-A / EAN-13 с нулём) — та же запись кода, как в приёмке.
            if (found is null && BarcodeForms.Alternate(code) is { } alternate)
                found = await api.FindWarehouseProductByBarcodeAsync(alternate, ct).ConfigureAwait(false);
            if (found is not { } product)
                continue;

            var id = Str(product, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(Tr.T("сервер не вернул номер найденного товара", "сервер табылган товардын номерин кайтарган жок",
                    "the server didn't return the ID of the found product", "sunucu bulunan ürünün numarasını döndürmedi", "server topilgan mahsulot raqamini qaytarmadi"));

            var name = Str(product, "name");
            var unit = Str(product, "unit");
            var purchase = Num(product, "purchase_price");
            var price = Num(product, "price");
            line.ProductId = id;
            line.Source = ReceivingSource.Warehouse;
            line.CategoryName = null;
            if (name is not null)
                line.ProductName = name;
            if (unit is not null)
                line.Unit = unit;
            line.OriginalName = line.ProductName;
            line.OriginalUnit = line.Unit;
            line.StockBefore = Num(product, "quantity");
            line.OriginalPurchasePrice = purchase;
            line.OriginalSalePrice = price;
            line.OriginalMarkupPercent = Num(product, "markup_percent");
            // Цены найденного товара не перетираем; если у него цены нет (0) — остаются цены строки.
            if (purchase > 0)
                line.PurchasePrice = purchase;
            if (price > 0)
                line.SalePrice = price;
            PosLogger.Log($"Массовое добавление: штрихкод {code} уже есть на сервере (товар {id}) — приход к нему, дубль не создаётся.", "STOCK");
        }
    }

    // ------------------------------------------------------------------ подача

    private void EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed)
                return;
            _subscribed = true;
            BackgroundReceivingQueue.Instance.JobFinished += OnJobFinished;
        }
    }

    private void OnJobFinished(ReceivingJob job)
    {
        if (job.BatchId is null)
            return;
        try
        {
            Pump();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: сбой подачи после части {job.BatchPart} пачки {job.BatchId}: {ex}", "ERROR");
        }
    }

    /// <summary>Сверить пачки текущей компании с очередью, записать итоги закончившихся частей, поставить следующие, закончить пачку.</summary>
    private void Pump()
    {
        var changed = new List<BulkBatchInfo>();
        var finished = new List<BulkBatchInfo>();
        var needRetry = false;
        lock (_gate)
        {
            EnsureLoaded();
            if (_closed)
                return;
            var company = CurrentCompany;
            var dirty = false;
            foreach (var b in _batches.Where(x => !x.Finished && SameCompany(x.CompanyId, company)).OrderBy(x => x.CreatedAt).ToList())
            {
                if (!PumpBatch(b))
                    continue;
                dirty = true;
                changed.Add(ToInfo(b));
                if (b.Finished)
                    finished.Add(ToInfo(b));
            }
            if (dirty)
                SaveIndexLocked();
            // Часть не поставилась, а в очереди ничего не идёт — будить подачу некому (JobFinished не придёт): повторим сами.
            needRetry = _batches.Any(x => !x.Finished && !x.Cancelled && x.LastError is not null && x.InFlight == 0 && SameCompany(x.CompanyId, company));
        }
        if (needRetry && !BackgroundReceivingQueue.Instance.IsClosed)
            ScheduleRetry();
        foreach (var info in changed)
            Raise(BatchChanged, info);
        foreach (var info in finished)
        {
            PosLogger.Log($"Массовое добавление: пачка {info.Id} закончена — {info.SummaryText}.", "STOCK");
            Raise(BatchFinished, info);
        }
    }

    private int _retryScheduled;

    private void ScheduleRetry()
    {
        if (Interlocked.Exchange(ref _retryScheduled, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(RetryPumpDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref _retryScheduled, 0);
            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: повторная подача не удалась: {ex}", "ERROR");
            }
        });
    }

    /// <summary>Под <c>_gate</c>. true — пачка изменилась (нужно записать индекс и сообщить окнам).</summary>
    private bool PumpBatch(BulkBatch b)
    {
        var dirty = false;
        if (b.Lines is null && !b.Cancelled)
        {
            var errorBefore = b.LastError;
            if (!LoadLines(b))
                return b.LastError != errorBefore;
        }

        var byPart = BackgroundReceivingQueue.Instance.JobsOfBatch(b.Id)
            .Where(j => j.BatchPart.HasValue)
            .GroupBy(j => j.BatchPart!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());

        b.InFlight = 0;
        for (var p = 0; p < b.PartCount; p++)
        {
            var res = b.Parts.FirstOrDefault(r => r.Part == p);
            byPart.TryGetValue(p, out var job);
            if (job is not null && job.IsFinal)
            {
                var now = Account(b, p, job);
                // Индекс переписывается после каждой части — тексты ошибок копим не бесконечно (счётчики остаются точными, сами строки — в задачах очереди).
                if (b.Parts.Where(r => r.Part != p).Sum(r => r.FailedRows.Count) >= MaxFailedRowsPerBatch)
                    now.FailedRows.Clear();
                if (res is null || !SameResult(res, now))
                {
                    if (res is not null)
                        b.Parts.Remove(res);
                    b.Parts.Add(now);
                    dirty = true;
                }
            }
            else if (job is not null)
                b.InFlight++;
            else if (res is null && b.EnqueuedParts.Contains(p))
            {
                // Часть была поставлена, а в очереди её нет (задачу убрали вручную/файл очереди восстановлен) — второй раз НЕ ставим:
                // исход неизвестен, остатки этих товаров нужно проверить.
                b.Parts.Add(LostPart(b, p));
                dirty = true;
            }
        }

        // Ставим следующие части (по порядку), пока в очереди меньше MaxParallelParts.
        while (!b.Cancelled && b.Lines is not null && b.InFlight < Math.Max(1, MaxParallelParts))
        {
            var next = -1;
            for (var p = 0; p < b.PartCount; p++)
                if (!byPart.ContainsKey(p) && !b.EnqueuedParts.Contains(p) && b.Parts.All(r => r.Part != p))
                {
                    next = p;
                    break;
                }
            if (next < 0)
                break;
            try
            {
                EnqueuePart(b, next);
            }
            catch (InvalidOperationException ex)
            {
                if (b.LastError != ex.Message)
                {
                    b.LastError = ex.Message;
                    dirty = true;
                }
                PosLogger.Log($"Массовое добавление: часть {next} пачки {b.Id} не поставлена в очередь: {ex.Message}", "WARNING");
                break;
            }
            b.EnqueuedParts.Add(next);
            b.InFlight++;
            b.LastError = null;
            dirty = true;
        }

        if (b.InFlight == 0 && (b.Cancelled || b.Parts.Count >= b.PartCount) && !b.Finished)
        {
            b.Finished = true;
            b.FinishedAt = DateTime.Now;
            b.Lines = null;
            TryDelete(LinesPath(b.Id));
            dirty = true;
        }
        return dirty;
    }

    private void EnqueuePart(BulkBatch b, int part)
    {
        var slice = b.Lines!.Skip(part * b.PartSize).Take(b.PartSize).ToList();
        var request = new ReceivingJobRequest(
            ReceivingJobSource.Bulk,
            slice.Select(l => l.ToVm()).ToList(),
            Supplier: null,
            PaidNow: false,
            Employee: b.Employee,
            BatchId: b.Id,
            BatchPart: part,
            Preparer: PreparerName,
            FileRows: slice.Select(l => l.FileRow).ToList());
        BackgroundReceivingQueue.Instance.Enqueue(request);
    }

    /// <summary>Итог закончившейся задачи-части. Строки не принятые — с номерами строк файла и текстом причины.</summary>
    private static BulkPartResult Account(BulkBatch b, int part, ReceivingJob job)
    {
        var r = new BulkPartResult
        {
            Part = part,
            JobId = job.Id,
            Status = job.Status,
            Lines = job.LineCount,
            Posted = job.PostedCount,
            Created = job.Created,
        };
        foreach (var l in job.Lines.Where(l => l.Outcome != ReceivingLineOutcome.Posted))
        {
            var check = l.Outcome == ReceivingLineOutcome.Check;
            r.Failed++;
            if (check)
                r.Check++;
            if (r.FailedRows.Count < MaxFailedRowsPerPart)
                r.FailedRows.Add(new BulkFailedLine { FileRow = l.FileRow, Barcode = l.Barcode, Name = l.ProductName, Error = ErrorOf(job, l), NeedsCheck = check });
        }
        // Задача закончилась не «done», но строк с неудачей не нашли (не должно быть) — не теряем счёт.
        if (r.Failed == 0 && r.Posted < r.Lines)
            r.Failed = r.Lines - r.Posted;
        return r;
    }

    private static string ErrorOf(ReceivingJob job, JobLine l)
    {
        string text;
        if (!string.IsNullOrWhiteSpace(l.Error))
            text = l.Error!;
        else if (l.ProductName.Length > 0 && job.Errors.FirstOrDefault(e => e.StartsWith(l.ProductName + ":", StringComparison.Ordinal)) is { } byName)
            text = byName;
        else
            text = job.LastError ?? job.StatusText;
        return text.Length > 300 ? text[..300] : text;
    }

    private static bool SameResult(BulkPartResult a, BulkPartResult b) =>
        a.JobId == b.JobId && a.Status == b.Status && a.Posted == b.Posted && a.Failed == b.Failed && a.Created == b.Created && a.Lines == b.Lines;

    private BulkPartResult LostPart(BulkBatch b, int part)
    {
        var text = Tr.T("часть пачки пропала из очереди — проверьте остаток этих товаров вручную",
            "топтомдун бир бөлүгү кезектен жоголду — бул товарлардын калдыгын кол менен текшериңиз",
            "this part of the batch disappeared from the queue — check the stock of these products manually",
            "toplu yüklemenin bu kısmı kuyruktan kayboldu — bu ürünlerin stokunu elle kontrol edin",
            "to'plamning bu qismi navbatdan yo'qoldi — bu mahsulotlar qoldig'ini qo'lda tekshiring");
        var r = new BulkPartResult { Part = part, Status = "lost", Lines = b.LinesInPart(part) };
        r.Failed = r.Check = r.Lines;
        if (b.Lines is not null)
            foreach (var l in b.Lines.Skip(part * b.PartSize).Take(Math.Min(b.PartSize, MaxFailedRowsPerPart)))
                r.FailedRows.Add(new BulkFailedLine { FileRow = l.FileRow, Barcode = l.Barcode, Name = l.ProductName, Error = text, NeedsCheck = true });
        return r;
    }

    private static int NotSentLines(BulkBatch b)
    {
        var n = 0;
        for (var p = 0; p < b.PartCount; p++)
            if (b.Parts.All(r => r.Part != p) && !b.EnqueuedParts.Contains(p))
                n += b.LinesInPart(p);
        return n;
    }

    private static BulkBatchInfo ToInfo(BulkBatch b)
    {
        var parts = b.Parts.ToList();
        return new BulkBatchInfo
        {
            Id = b.Id,
            Label = b.Label,
            CompanyId = b.CompanyId,
            CreatedAt = b.CreatedAt,
            FinishedAt = b.FinishedAt,
            TotalLines = b.TotalLines,
            PartCount = b.PartCount,
            PartsFinished = parts.Count,
            PartsInFlight = b.InFlight,
            ProcessedLines = parts.Sum(r => r.Lines),
            Created = parts.Sum(r => r.Created),
            Received = parts.Sum(r => r.Posted),
            Failed = parts.Sum(r => r.Failed),
            NeedsCheck = parts.Sum(r => r.Check),
            NotSent = b.Cancelled ? NotSentLines(b) : 0,
            Cancelled = b.Cancelled,
            Finished = b.Finished,
            LastError = b.LastError,
            FailedRows = parts.OrderBy(r => r.Part).SelectMany(r => r.FailedRows).ToList(),
            FailedJobIds = parts.Where(r => r.Status != ReceivingJobStatus.Done && r.JobId is not null && r.Status != "lost").Select(r => r.JobId!).ToList(),
        };
    }

    private static void Raise(Action<BulkBatchInfo>? handler, BulkBatchInfo arg)
    {
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action<BulkBatchInfo>)d)(arg);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: обработчик события упал ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
    }

    // ------------------------------------------------------------------ файлы

    /// <summary>Под <c>_gate</c>. Читает индекс; повреждённый переименовывает в bulk-pending.bad-&lt;время&gt;.json и закрывается (как очередь).</summary>
    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (Directory.Exists(DataDir) && Directory.GetFiles(DataDir, "bulk-pending.bad-*.json").FirstOrDefault() is { } bad)
            {
                _closed = true;
                PosLogger.Log($"Массовое добавление: в папке повреждённый индекс ({bad}) — подача закрыта до разбора поддержкой.", "ERROR");
                return;
            }
            if (!File.Exists(IndexPath))
                return;
            string text;
            using (var fs = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                text = reader.ReadToEnd();
            string? problem = null;
            List<BulkBatch>? list = null;
            try
            {
                var raw = JsonSerializer.Deserialize<List<BulkBatch?>>(text, Json);
                if (raw is null)
                    problem = "документ null";
                else
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var b in raw)
                    {
                        if (b is null)
                            problem = "пустая пачка";
                        else if (string.IsNullOrWhiteSpace(b.Id) || !ids.Add(b.Id))
                            problem = "пачка без номера или два одинаковых номера";
                        else if (b.PartSize <= 0 || b.PartCount <= 0 || b.TotalLines <= 0)
                            problem = $"пачка {b.Id}: размеры частей";
                        if (problem is not null)
                            break;
                    }
                    if (problem is null)
                        list = raw.Select(b => b!).ToList();
                }
            }
            catch (JsonException ex)
            {
                problem = ex.Message;
            }
            if (list is not null)
            {
                _batches = list;
                return;
            }
            var badPath = Path.Combine(DataDir, $"bulk-pending.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Move(IndexPath, badPath);
            }
            catch (Exception moveEx)
            {
                PosLogger.Log($"Массовое добавление: повреждённый индекс не переименован ({moveEx.Message}).", "ERROR");
            }
            _closed = true;
            PosLogger.Log($"Массовое добавление: индекс повреждён ({problem}) — сохранён как {badPath}; подача закрыта.", "ERROR");
        }
        catch (Exception ex)
        {
            // Индекс не прочитать (занят, нет прав) — это НЕ пустой список: не начинаем, чтобы не затереть.
            _closed = true;
            PosLogger.Log($"Массовое добавление: индекс не прочитан ({ex.Message}) — подача закрыта.", "ERROR");
        }
    }

    /// <summary>Под <c>_gate</c>. Атомарная запись индекса (временный файл + замена). Законченные пачки старше 7 дней забываются.</summary>
    private bool SaveIndexLocked()
    {
        if (_closed)
            return false;
        try
        {
            var now = DateTime.Now;
            foreach (var old in _batches.Where(b => b.Finished && (now - (b.FinishedAt ?? b.CreatedAt)).TotalDays > KeepFinishedDays).ToList())
            {
                _batches.Remove(old);
                TryDelete(LinesPath(old.Id));
            }
            Directory.CreateDirectory(DataDir);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_batches, Json));
            MoveWithRetry(tmp, IndexPath);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: индекс не записан ({ex.Message}).", "ERROR");
            return false;
        }
    }

    private static bool WriteLines(BulkBatch b)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var path = LinesPath(b.Id);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(b.Lines, Json));
            MoveWithRetry(tmp, path);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: строки пачки не записаны ({ex.Message}).", "ERROR");
            return false;
        }
    }

    /// <summary>Под <c>_gate</c>. Строки пачки после перезапуска. Файла нет/не читается — пачка не двигается, причина в LastError.</summary>
    private bool LoadLines(BulkBatch b)
    {
        string? problem = null;
        try
        {
            var list = JsonSerializer.Deserialize<List<JobLine>>(File.ReadAllText(LinesPath(b.Id)), Json);
            if (list is null || list.Count != b.TotalLines)
                problem = $"строк {list?.Count ?? 0} вместо {b.TotalLines}";
            else
            {
                b.Lines = list;
                return true;
            }
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        var text = Tr.T("не удалось прочитать строки пачки с диска — обратитесь в поддержку",
            "топтомдун саптарын дисктен окуу мүмкүн болгон жок — колдоо кызматына кайрылыңыз",
            "could not read the batch lines from disk — contact support",
            "toplu yükleme satırları diskten okunamadı — destekle iletişime geçin",
            "to'plam qatorlarini diskdan o'qib bo'lmadi — qo'llab-quvvatlashga murojaat qiling");
        if (b.LastError != text)
        {
            b.LastError = text;
            PosLogger.Log($"Массовое добавление: строки пачки {b.Id} не прочитаны: {problem}", "ERROR");
            return false;
        }
        return false;
    }

    /// <summary>Замена файла с короткими повторами: антивирус/индексатор иногда на миг держат файл («Access denied»).</summary>
    private static void MoveWithRetry(string tmp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------ JSON сервера

    private static string? Str(System.Text.Json.JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Num(System.Text.Json.JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d) ? d : 0;
}
