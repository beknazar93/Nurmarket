using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-17, редизайн (этап 4, макет MCancelled): одна строка, убранная из текущего чека до оплаты.</summary>
public sealed class CartRemovalRecord
{
    public DateTimeOffset At { get; set; }

    /// <summary>Как убрали: <see cref="CartRemovalLogStore.KindLine"/> и другие виды хранилища.</summary>
    public string Kind { get; set; } = CartRemovalLogStore.KindLine;

    /// <summary>Общий ключ строк одного действия («Очистить корзину», удаление вкладки чека).</summary>
    public string? BatchId { get; set; }

    public string? ShiftId { get; set; }
    public string? CashboxId { get; set; }

    /// <summary>Компания NurCRM — журнал общий для всех аккаунтов этого ПК, окно показывает только свою.</summary>
    public string? CompanyId { get; set; }

    public string Cashier { get; set; } = "";
    public string? CartId { get; set; }

    /// <summary>Вкладка чека («Основной чек», «Чек 2»).</summary>
    public string? Receipt { get; set; }

    public string? ProductId { get; set; }
    public string Title { get; set; } = "";
    public double Quantity { get; set; }
    public string? Unit { get; set; }
    public double UnitPrice { get; set; }

    /// <summary>Сумма убранного с учётом скидки строки.</summary>
    public double LineTotal { get; set; }

    /// <summary>Код причины (окно «Убрать из чека?»), null — не спрашивали.</summary>
    public string? Reason { get; set; }

    public string? Note { get; set; }

    /// <summary>Чей код доступа ввели (Настройки → Сотрудники), <see cref="CartRemovalLogStore.ConfirmedBySiteCode"/> —
    /// «Код на удаление из корзины» с сайта; null — код не спрашивали.</summary>
    public string? ConfirmedBy { get; set; }
}

/// <summary>Запрос окна «Убрать из чека?» (2026-10-17, этап 4).</summary>
public sealed record CartRemovalReasonRequest(string Title, double Quantity, string Unit, double LineTotal, bool CanRemoveOne);

/// <summary>Ответ окна: код причины (или null, если только заметка), заметка, убрать только 1 шт.</summary>
public sealed record CartRemovalReasonAnswer(string? Reason, string? Note, bool OnlyOne);

/// <summary>2026-10-17, редизайн (этап 4, макет MCancelled / FIXES.md «Л · MCancelled»): журнал «Удалено из чека».
/// Касса пишет сюда каждую строку, убранную из текущего чека до оплаты (корзина у строки, Num −, голос,
/// «Очистить корзину» Ctrl+F1, удаление вкладки чека, сброс чека при закрытии смены). Отложенные чеки и оплата —
/// не удаление. Сервера для этого в NurCRM нет — журнал локальный, только этой кассы:
/// %LOCALAPPDATA%\NurMarketKassa\cart-removals.jsonl (одна запись JSON на строку — дописывается, а не
/// переписывается целиком), хранится 90 дней и не больше 20 000 записей. Запись — в фоне, по порядку;
/// ошибки — только в журнал кассы (PosLogger), кассиру окон не показывает.</summary>
public static class CartRemovalLogStore
{
    public const string KindLine = "line";
    public const string KindOne = "one";
    public const string KindClear = "clear";
    public const string KindReceipt = "receipt";
    public const string KindShiftClose = "shift_close";

    public const string ConfirmedBySiteCode = "@site";

    public const int KeepDays = 90;
    public const int MaxRecords = 20000;

    /// <summary>Сколько дописанных записей между чистками файла (старше 90 дней / сверх лимита).</summary>
    private const int TrimEvery = 500;

    private static readonly object QueueGate = new();
    private static readonly object FileGate = new();
    private static Task _tail = Task.CompletedTask;
    private static int _appendedSinceTrim = TrimEvery; // первая запись после запуска сразу чистит файл

    private static string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "cart-removals.jsonl");

    /// <summary>Путь файла. Меняют только снимки окон (headless), чтобы не писать в настоящий журнал этого ПК.</summary>
    public static string FilePath
    {
        get { lock (FileGate) return _filePath; }
        set { lock (FileGate) _filePath = value; }
    }

    /// <summary>Журнал изменился (вызывается из фонового потока).</summary>
    public static event Action? Changed;

    /// <summary>Ставит записи в очередь записи и сразу возвращается — скан и удаление не ждут диск.</summary>
    public static void Append(IReadOnlyList<CartRemovalRecord> records)
    {
        if (records is not { Count: > 0 })
            return;
        var copy = records.ToList();
        lock (QueueGate)
            _tail = _tail.ContinueWith(_ => WriteCore(copy), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Дождаться записи очереди (для выхода из программы и снимков).</summary>
    public static Task FlushAsync()
    {
        lock (QueueGate)
            return _tail;
    }

    /// <summary>Все записи, новые сверху. Читает файл — вызывать не на UI-потоке.</summary>
    public static IReadOnlyList<CartRemovalRecord> ReadAll()
    {
        lock (FileGate)
            return ReadUnlocked().OrderByDescending(r => r.At).ToList();
    }

    private static void WriteCore(List<CartRemovalRecord> records)
    {
        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                // 2026-10-17, ревью этапа 4: последняя строка недописана (выключили питание) — новая запись начинается
                // с новой строки, иначе склеится с битой и пропадёт при чтении.
                if (!EndsWithNewLine(_filePath))
                    File.AppendAllText(_filePath, Environment.NewLine);
                File.AppendAllLines(_filePath, records.Select(r => JsonSerializer.Serialize(r)));
                _appendedSinceTrim += records.Count;
                if (_appendedSinceTrim >= TrimEvery)
                {
                    _appendedSinceTrim = 0;
                    TrimUnlocked();
                }
            }

            var sum = records.Sum(r => r.LineTotal);
            PosLogger.Log($"CART: удалено из чека — {records[0].Kind}, позиций {records.Count}, сумма {sum:0.00}" +
                          (records[0].Reason is { } reason ? $", причина {reason}" : "") + ".", "CART");
        }
        catch (Exception ex)
        {
            // Журнал удалений не должен мешать продаже: строка уже убрана, кассир ничего не видит.
            PosLogger.Log($"Журнал «Удалено из чека» не записан: {ex.Message}", "WARNING");
        }

        try { Changed?.Invoke(); } catch { /* подписчик окна — не повод ронять очередь записи */ }
    }

    /// <summary>Убирает записи старше 90 дней и сверх 20 000 (самые старые) — файл переписывается через .tmp.</summary>
    private static void TrimUnlocked()
    {
        if (!File.Exists(_filePath))
            return;
        // 2026-10-17, ревью этапа 4: чтение оборвалось (диск, доступ) — не переписываем файл прочитанной частью.
        List<CartRemovalRecord> all;
        try { all = ReadUnlocked(throwOnError: true); }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал «Удалено из чека» не почищен (не прочитан): {ex.Message}", "WARNING");
            return;
        }
        var cutoff = DateTimeOffset.Now.AddDays(-KeepDays);
        var keep = all.Where(r => r.At >= cutoff).OrderBy(r => r.At).ToList();
        if (keep.Count > MaxRecords)
            keep = keep.Skip(keep.Count - MaxRecords).ToList();
        if (keep.Count == all.Count)
            return;
        var tmp = _filePath + ".tmp";
        File.WriteAllLines(tmp, keep.Select(r => JsonSerializer.Serialize(r)));
        File.Move(tmp, _filePath, true);
    }

    private static bool EndsWithNewLine(string path)
    {
        if (!File.Exists(path))
            return true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
            return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    private static List<CartRemovalRecord> ReadUnlocked(bool throwOnError = false)
    {
        var result = new List<CartRemovalRecord>();
        try
        {
            if (!File.Exists(_filePath))
                return result;
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<CartRemovalRecord>(line) is { } record)
                        result.Add(record);
                }
                catch (JsonException)
                {
                    // Недописанная строка (выключили питание посреди записи) — пропускаем только её.
                }
            }
        }
        catch (Exception ex) when (!throwOnError)
        {
            PosLogger.Log($"Журнал «Удалено из чека» не прочитан: {ex.Message}", "WARNING");
        }
        return result;
    }
}
