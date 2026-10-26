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
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "background-receipts");

    private static string FilePath => Path.Combine(DataDir, "queue.json");
    private static string LockPath => Path.Combine(DataDir, "queue.lock");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

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
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs, Json));
            // 2026-10-26 (М1): пачка из сотен частей переписывает файл десятки тысяч раз — замену иногда на миг блокирует антивирус/индексатор
            // («Access denied»). Несколько коротких повторов вместо потерянной записи (под замком не дольше ~80 мс).
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, FilePath, overwrite: true);
                    break;
                }
                catch (Exception moveEx) when (attempt < 4 && moveEx is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приход в фоне: файл очереди не записан ({ex.Message}).", "ERROR");
            return false;
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
