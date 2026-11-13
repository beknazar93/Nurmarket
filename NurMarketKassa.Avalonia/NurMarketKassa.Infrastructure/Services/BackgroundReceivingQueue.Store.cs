using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-25, ТЗ «оприходование в фоне» (Q1): файл очереди на диске. По образцу SupplierDebtRecorder: JSON, атомарная запись
// (временный файл + замена), межпроцессный замок на время попытки, повреждённый файл переименовывается в queue.bad-<время>.json.

public sealed partial class BackgroundReceivingQueue
{
    private const int KeepDays = 7;
    private const int KeepDone = 200;
    private const int LockWaitSeconds = 45;

    private static string DataDir => DataDirOverride
        ?? Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "background-receipts");

    private static string FilePath => Path.Combine(DataDir, "queue.json");
    private static string LockPath => Path.Combine(DataDir, "queue.lock");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>2026-11-12 (Л-1): ключи строк, прочитанных из файла очереди без отметки StockApplied (под lock (_sync)). Только в памяти — в файл не пишется.</summary>
    private readonly HashSet<string> _restoredKeys = new(StringComparer.Ordinal);

    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (Directory.Exists(DataDir) && Directory.GetFiles(DataDir, "queue.bad-*.json").FirstOrDefault() is { } bad)
                {
                    _closed = true;
                    _closedFile = bad;
                    PosLogger.Log($"Приход в фоне: в папке повреждённый файл очереди ({bad}) — очередь закрыта до разбора поддержкой.", "ERROR");
                    return;
                }
                if (!File.Exists(FilePath))
                    return;
                string text;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                    text = reader.ReadToEnd();
                string? problem = null;
                List<ReceivingJob>? list = null;
                try
                {
                    var raw = JsonSerializer.Deserialize<List<ReceivingJob?>>(text, Json);
                    if (raw is null)
                        problem = "документ null";
                    else
                    {
                        var ids = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var j in raw)
                        {
                            if (j is null)
                                problem = "пустая задача";
                            else if (string.IsNullOrWhiteSpace(j.Id) || !ids.Add(j.Id))
                                problem = "задача без номера или два одинаковых номера";
                            // 2026-10-26 (М1): сжатая готовая часть bulk — без строк, но со счётчиком.
                            else if (j.Lines is null || (j.Lines.Count == 0 && !(j.Status == ReceivingJobStatus.Done && j.CompactedLineCount > 0)))
                                problem = $"задача {j.Id} без строк";
                            else if (j.Status is not (ReceivingJobStatus.Queued or ReceivingJobStatus.Sending or ReceivingJobStatus.RetryWait or ReceivingJobStatus.Finishing
                                         or ReceivingJobStatus.Done or ReceivingJobStatus.Partial or ReceivingJobStatus.Failed or ReceivingJobStatus.NeedsCheck))
                                problem = $"задача {j.Id}: статус «{j.Status}»";
                            if (problem is not null)
                                break;
                        }
                        if (problem is null)
                            list = raw.Select(j => j!).ToList();
                    }
                }
                catch (JsonException ex)
                {
                    problem = ex.Message;
                }
                if (list is not null)
                {
                    _jobs = list;
                    // 2026-11-12 (Л-1, аудит Fable): строки, поднятые с диска и ещё не прибавленные к плитке. Каталог после перезапуска уже загружен с сервера и может
                    // включать их приход (приём был до выключения, ответ принят, а StockApplied не записан) — «плитка + принятое» задвоило бы его. Для таких строк остаток берётся с сервера.
                    _restoredKeys.Clear();
                    foreach (var rl in list.SelectMany(j => j.Lines).Where(l => !l.IsNew && !l.StockApplied && l.Outcome != ReceivingLineOutcome.Rejected && !string.IsNullOrWhiteSpace(l.ProductId)))
                        _restoredKeys.Add(rl.Key);
                    PublishReceiptHolds(); // 2026-11-11 (Д-4): незавершённые приходы с прошлого запуска держат остаток своих товаров с самого старта
                    return;
                }
                var badPath = Path.Combine(DataDir, $"queue.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try
                {
                    File.Move(FilePath, badPath);
                }
                catch (Exception moveEx)
                {
                    PosLogger.Log($"Приход в фоне: повреждённый файл очереди не переименован ({moveEx.Message}).", "ERROR");
                    badPath = FilePath;
                }
                _closed = true;
                _closedFile = badPath;
                PosLogger.Log($"Приход в фоне: файл очереди повреждён ({problem}) — сохранён как {badPath}; очередь закрыта.", "ERROR");
            }
            catch (Exception ex)
            {
                // Файл не прочитать (занят, нет прав) — это НЕ пустая очередь: не начинаем, чтобы не затереть.
                _closed = true;
                PosLogger.Log($"Приход в фоне: файл очереди не прочитан ({ex.Message}) — очередь закрыта.", "ERROR");
            }
        }
    }

    /// <summary>Атомарная запись. Вызывать под <c>lock (_sync)</c>. Незавершённые и ошибочные — всегда; готовые — не старше 7 дней и не больше 200.</summary>
    private bool Save()
    {
        if (_closed)
            return false;
        try
        {
            var now = DateTime.Now;
            var done = _jobs.Where(j => j.Status == ReceivingJobStatus.Done).OrderByDescending(j => j.FinishedAt ?? j.CreatedAt).ToList();
            foreach (var old in done.Where(j => (now - (j.FinishedAt ?? j.CreatedAt)).TotalDays > KeepDays).Concat(done.Skip(KeepDone)).ToList())
                _jobs.Remove(old);
            Directory.CreateDirectory(DataDir);
            // 2026-11-01 (Н2-01): запись через AtomicFile (сброс на диск + .bak) с короткими повторами, если файл на миг занят антивирусом/индексатором (М1, 2026-10-26).
            AtomicFile.WriteAllTextWithRetry(FilePath, JsonSerializer.Serialize(_jobs, Json));
            PublishReceiptHolds(); // 2026-11-11 (Д-4): каждое изменение задач проходит через Save — набор «приход в пути» всегда свежий
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приход в фоне: файл очереди не записан ({ex.Message}).", "ERROR");
            return false;
        }
    }

    /// <summary>2026-11-11 (Д-4): товары склада из незавершённых приходов, чьё количество ещё не прибавлено к плитке (строка ждёт отправки, отправляется или уже принята сервером,
    /// но ApplyStockToTileAsync ещё не прошёл), — для ленты, точечного обновления и полной загрузки «трогали сейчас» (StockSyncService.IsReceiptHeld): серверный остаток
    /// с этим приходом не ложится на плитку раньше прибавки (иначе прибавка задваивала бы приход). Новые товары не держим — их остаток приносит лента. Вызывать под lock (_sync).</summary>
    private void PublishReceiptHolds()
    {
        try
        {
            StockSyncService.SetReceiptHolds(this, _jobs.Where(j => !j.IsFinal)
                .SelectMany(j => j.Lines)
                .Where(l => !l.IsNew && !l.StockApplied && l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown or ReceivingLineOutcome.Posted)
                .Select(l => l.ProductId)
                .ToList());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Приход в фоне: защита остатка товаров в пути не обновлена ({ex.Message}).", "WARNING");
        }
    }

    private sealed record LockHolder(FileStream Stream) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    /// <summary>Межпроцессный замок (FileShare.None) на время попытки: вторая копия программы на этом ПК не отправит ту же задачу.
    /// null — не удалось (занят дольше 45 с или нет доступа): попытка откладывается.</summary>
    private static async Task<IDisposable?> AcquireFileLockAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(LockWaitSeconds);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                return new LockHolder(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Приход в фоне: замок очереди занят или недоступен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                return null;
            }
        }
    }
}
