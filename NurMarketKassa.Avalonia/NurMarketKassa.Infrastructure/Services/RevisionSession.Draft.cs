using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — черновик на диске, замок, акты (разбор огромного файла: типы вынесены без изменений; остальное — RevisionSession.cs)

// ====================================================================== черновик на диске

internal sealed class RevisionDraftCount
{
    public string? P { get; set; }
    public double F { get; set; }
    public double E { get; set; }
    public DateTime? T { get; set; }
}

internal sealed class RevisionDraftPart
{
    public List<string>? Ids { get; set; }
    public List<double>? Sent { get; set; }
    public string? SessionId { get; set; }
    public bool Applied { get; set; }
    public DateTime? CreatedAt { get; set; }
}

internal sealed class RevisionDraftDto
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string User { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime StockAt { get; set; }
    public DateTime SavedAt { get; set; }
    public bool SalesCorrection { get; set; } = true;
    public List<RevisionDraftCount> Counts { get; set; } = new();
    public List<RevisionDraftPart>? Plan { get; set; }

    /// <summary>Порядковый номер записи: запоздавшая старая запись не затирает новую и не воскрешает удалённый черновик.</summary>
    [JsonIgnore]
    public long Seq { get; set; }
}

public sealed record RevisionDraftInfo(int Counted, DateTime StartedAt, DateTime SavedAt, bool PostingStarted);

/// <summary>Черновик ревизии — файл на этом компьютере (%APPDATA%\NurMarketKassa\revision\draft.json), общий для кассы и программы владельца,
/// плюс файл-замок «ревизия открыта в другой программе».</summary>
public static class RevisionDraftStore
{
    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    internal static string DataDir => DataDirOverride
        ?? Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, "NurMarketKassa", "revision");

    private static string FilePath => Path.Combine(DataDir, "draft.json");
    private static string LockPath => Path.Combine(DataDir, "draft.lock");
    private static readonly object Sync = new();
    private static long _seq;
    private static long _floor;
    private static long _lastWritten;
    private static readonly TimeSpan LockMaxAge = TimeSpan.FromHours(2);

    internal static long NextSeq() => Interlocked.Increment(ref _seq);

    internal static string CurrentCompany
    {
        get
        {
            try
            {
                return CompanyInfoService.LastCompany?.Id ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    internal static RevisionDraftDto? Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;
                var draft = JsonSerializer.Deserialize<RevisionDraftDto>(File.ReadAllText(FilePath));
                if (draft is null || (draft.Counts.Count == 0 && draft.Plan is not { Count: > 0 }))
                    return null;
                // Черновик другого магазина (вход под другим аккаунтом) не показываем.
                if (draft.CompanyId.Length > 0 && CurrentCompany.Length > 0
                    && !string.Equals(draft.CompanyId, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                    return null;
                return draft;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не прочитан ({ex.Message}).", "WARNING");
                return null;
            }
        }
    }

    /// <summary>Есть ли черновик: сколько посчитано, когда начали («Продолжить ревизию от 24.10 14:05, посчитано 123»). null — нет.</summary>
    public static RevisionDraftInfo? Peek()
    {
        var d = Load();
        return d is null ? null : new RevisionDraftInfo(d.Counts.Count, d.StartedAt, d.SavedAt, d.Plan?.Any(p => !string.IsNullOrEmpty(p.SessionId)) == true);
    }

    internal static void Save(RevisionDraftDto draft)
    {
        lock (Sync)
        {
            // Запоздавшая старая запись (две записи на пуле потоков) не затирает новую и не воскрешает удалённый черновик.
            if (draft.Seq <= _floor || draft.Seq < _lastWritten)
                return;
            try
            {
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(draft));
                File.Move(tmp, FilePath, overwrite: true);
                _lastWritten = draft.Seq;
                TouchLock();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не записан ({ex.Message}).", "WARNING");
            }
        }
    }

    public static void Delete()
    {
        lock (Sync)
        {
            _floor = Interlocked.Read(ref _seq);
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Ревизия: черновик не удалён ({ex.Message}).", "WARNING");
            }
        }
    }

    // ---- замок: «Ревизия открыта в другой программе этого компьютера» ----

    /// <summary>Занять ревизию за этой программой. false — её держит другая живая программа (holder — её PID); замок старше 2 часов или без процесса снимается.</summary>
    public static bool TryAcquireLock(out string holder)
    {
        holder = "";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        using var w = new StreamWriter(fs);
                        w.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                    catch (IOException) when (File.Exists(LockPath))
                    {
                        var text = "";
                        try { text = File.ReadAllText(LockPath).Trim(); } catch { /* занят записью — считаем живым */ }
                        _ = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
                        if (pid == Environment.ProcessId)
                            return true;
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(LockPath);
                        // Пустой файл моложе 5 с — другая программа только что создала замок и ещё пишет в него номер.
                        if (age <= LockMaxAge && ((pid > 0 && ProcessAlive(pid)) || (pid == 0 && age < TimeSpan.FromSeconds(5))))
                        {
                            holder = pid > 0 ? pid.ToString(CultureInfo.InvariantCulture) : "?";
                            return false;
                        }

                        File.Delete(LockPath); // устарел или процесса нет
                    }
                }

                holder = "?";
                return false;
            }
            catch (Exception ex)
            {
                // Папка недоступна — замок не работает, ревизии это не мешает.
                PosLogger.Log($"Ревизия: замок не занят ({ex.Message}).", "WARNING");
                return true;
            }
        }
    }

    public static void ReleaseLock()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                    File.Delete(LockPath);
            }
            catch
            {
                // замок остался — снимется по возрасту
            }
        }
    }

    /// <summary>Подтвердить, что программа жива (вызывается при каждой записи черновика).</summary>
    public static void TouchLock()
    {
        try
        {
            if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow);
        }
        catch
        {
            // не критично
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true; // не смогли проверить — полагаемся на возраст замка
        }
    }
}

// ====================================================================== локальные акты

public sealed class RevisionActRow
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Учёт на момент пересчёта.</summary>
    public double Expected { get; set; }
    /// <summary>Введённый факт; null — не считали.</summary>
    public double? Fact { get; set; }
    /// <summary>Что записано в акт (с поправкой на продажи); null — в акт не входила.</summary>
    public double? Written { get; set; }
    public double SalePrice { get; set; }
    public double PurchasePrice { get; set; }
    public DateTime? CountedAt { get; set; }
}

/// <summary>Проведённый акт ревизии для повторной выгрузки (пакет Р3). Хранит ВСЕ строки склада (непосчитанные — Fact = null).</summary>
public sealed class RevisionActRecord
{
    public string Id { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public DateTime StockAt { get; set; }
    public string UserName { get; set; } = "";
    public bool SalesCorrection { get; set; }
    /// <summary>Номера актов на сервере (по 500 строк).</summary>
    public List<string> SessionIds { get; set; } = new();
    public List<RevisionActRow> Rows { get; set; } = new();
}

public sealed record RevisionActSummary(string FileName, string Id, DateTime StartedAt, DateTime FinishedAt, int Counted, int Total, IReadOnlyList<string> SessionIds);

/// <summary>Последние 20 проведённых ревизий — %APPDATA%\NurMarketKassa\revision\acts\*.json.gz (сжатые: 15 000 строк ≈ 0,2 МБ).</summary>
public static class RevisionActStore
{
    private const int Keep = 20;

    private static string ActsDir => Path.Combine(RevisionDraftStore.DataDir, "acts");

    /// <summary>Записать акт; возвращает имя файла. Ошибка записи не мешает ревизии (пустое имя).</summary>
    public static string Save(RevisionActRecord act)
    {
        try
        {
            Directory.CreateDirectory(ActsDir);
            var name = $"{act.StartedAt:yyyyMMdd-HHmmss}-{(act.Id.Length >= 8 ? act.Id[..8] : act.Id)}.json.gz";
            var path = Path.Combine(ActsDir, name);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, act);
            File.Move(tmp, path, overwrite: true);
            foreach (var old in new DirectoryInfo(ActsDir).GetFiles("*.json.gz").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
            {
                try { old.Delete(); } catch { /* останется до следующего раза */ }
            }

            return name;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: акт не сохранён локально ({ex.Message}).", "WARNING");
            return "";
        }
    }

    /// <summary>Список прошлых ревизий, новые сверху.</summary>
    public static List<RevisionActSummary> List()
    {
        var result = new List<RevisionActSummary>();
        try
        {
            if (!Directory.Exists(ActsDir))
                return result;
            foreach (var f in new DirectoryInfo(ActsDir).GetFiles("*.json.gz").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var act = Load(f.Name);
                if (act is not null)
                    result.Add(new RevisionActSummary(f.Name, act.Id, act.StartedAt, act.FinishedAt, act.Rows.Count(r => r.Fact is not null), act.Rows.Count, act.SessionIds));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: список актов не прочитан ({ex.Message}).", "WARNING");
        }

        return result;
    }

    public static RevisionActRecord? Load(string fileName)
    {
        try
        {
            var path = Path.Combine(ActsDir, Path.GetFileName(fileName));
            if (!File.Exists(path))
                return null;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<RevisionActRecord>(gz);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: акт {fileName} не прочитан ({ex.Message}).", "WARNING");
            return null;
        }
    }
}
