using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>
/// Смены, закрытые НА КАССЕ, но ещё не закрытые на сервере.
///
/// Зачем: 2026-09-22 сервер NurCRM перестал принимать закрытие смены — он сам хранит
/// income_total с пятью знаками после запятой ('150.00000') и сам же отклоняет это значение
/// своей проверкой «не более 2 цифр». Исправить нельзя ничем: касса это поле не отправляет,
/// PATCH на смену запрещён (405), другого эндпоинта закрытия нет. Кассир при этом оказывался
/// заперт — смену не закрыть, новую не открыть.
///
/// Решение владельца: закрывать смену локально и дожимать сервер в фоне. Смена ложится сюда,
/// а <see cref="SyncService"/> при каждом удачном выходе на связь пробует закрыть её снова —
/// как только на сервере починят, закрытие пройдёт само, без участия кассира.
/// </summary>
public static class PendingShiftCloseStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName,
        "pending_shift_closes.json");

    public sealed class Entry
    {
        public string ShiftId { get; set; } = "";

        /// <summary>Наличные в кассе на момент закрытия — ровно то, что кассир ввёл в диалоге.
        /// Дожимая сервер позже, отправляем ту же сумму, а не пересчитанную задним числом.</summary>
        public string? ClosingCash { get; set; }

        public DateTime ClosedLocallyAtUtc { get; set; }

        public int Attempts { get; set; }

        public string? LastError { get; set; }

        /// <summary>2026-10-27 (ревью, См-2): сколько раз кассир нажимал «Открыть смену», а сервер не принял закрытие этой смены.</summary>
        public int OpenRetries { get; set; }

        /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): компания, в которой смена закрыта на кассе. Фоновое закрытие выполняется только для неё: после входа в другую
        /// компанию чужую смену «закрыть» нельзя (она не найдётся или, хуже, закроется по чужому токену). Пусто — запись старого формата (как раньше, без проверки).</summary>
        public string? CompanyId { get; set; }
    }

    public static List<Entry> LoadAll()
    {
        lock (Gate)
        {
            try
            {
                // 2026-10-29 (аварийный аудит, п. 7): битый файл — из копии .bak, а не «пусто» (иначе очередь закрытий смен терялась).
                // 2026-10-30: «файл занят» для показа не ошибка очереди — отдаём то, что ещё не записано на диск (_unsaved).
                return LoadAllNoLock(forWrite: false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Очередь закрытия смен не прочитана: {ex.Message}", "WARNING");
                return new List<Entry>();
            }
        }
    }

    /// <summary>Ставит смену в очередь. Повторная постановка той же смены не дублирует
    /// запись — обновляет её, иначе после нескольких неудачных попыток закрытия в файле
    /// накопились бы копии одной и той же смены.</summary>
    public static void Enqueue(string shiftId, string? closingCash, string? lastError)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return;

        lock (Gate)
        {
            if (!TryLoadForWrite(out var all))
            {
                // 2026-10-30: файл занят — держим запись в памяти, допишем при следующей записи (смена уже закрыта локально).
                Unsaved.RemoveAll(u => string.Equals(u.ShiftId, shiftId.Trim(), StringComparison.OrdinalIgnoreCase));
                Unsaved.Add(new Entry { ShiftId = shiftId.Trim(), ClosingCash = closingCash, ClosedLocallyAtUtc = DateTime.UtcNow, LastError = lastError, CompanyId = CompanyInfoService.LastCompany?.Id });
                return;
            }

            var existing = all.FirstOrDefault(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ClosingCash = closingCash ?? existing.ClosingCash;
                existing.CompanyId ??= CompanyInfoService.LastCompany?.Id;
                existing.LastError = lastError;
            }
            else
            {
                all.Add(new Entry
                {
                    ShiftId = shiftId.Trim(),
                    ClosingCash = closingCash,
                    ClosedLocallyAtUtc = DateTime.UtcNow,
                    LastError = lastError,
                    CompanyId = CompanyInfoService.LastCompany?.Id,
                });
            }

            SaveNoLock(all);
            PosLogger.Log($"Смена {shiftId} закрыта локально и поставлена в очередь на закрытие сервера.", "SHIFT");
        }
    }

    public static void Remove(string shiftId)
    {
        lock (Gate)
        {
            Unsaved.RemoveAll(u => string.Equals(u.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
            if (!TryLoadForWrite(out var all))
                return;
            var removed = all.RemoveAll(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveNoLock(all);
                PosLogger.Log($"Смена {shiftId} закрыта на сервере — убрана из очереди.", "SHIFT");
            }
        }
    }

    public static void RecordFailure(string shiftId, string? error)
    {
        lock (Gate)
        {
            if (!TryLoadForWrite(out var all))
                return;
            var entry = all.FirstOrDefault(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;

            entry.Attempts++;
            entry.LastError = error;
            SaveNoLock(all);
        }
    }

    /// <summary>2026-10-27 (ревью, См-2): ещё одно нажатие «Открыть смену» не закрыло эту смену на сервере. Возвращает, сколько их уже
    /// было (0 — записи нет).</summary>
    public static int RecordOpenRetry(string shiftId, string? error)
    {
        lock (Gate)
        {
            if (!TryLoadForWrite(out var all))
                return 0;
            var entry = all.FirstOrDefault(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return 0;

            entry.OpenRetries++;
            entry.LastError = error;
            SaveNoLock(all);
            return entry.OpenRetries;
        }
    }

    /// <summary>2026-10-27 (аудит кассы, В-7): запись очереди по смене; null — этой смены в очереди нет.</summary>
    public static Entry? TryGet(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        return LoadAll().FirstOrDefault(e => string.Equals(e.ShiftId, shiftId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static int Count => LoadAll().Count;

    /// <summary>2026-10-30 (приёмка 1.17.65, блокер 3): записи, которые не удалось сохранить из-за занятого файла (антивирус держит
    /// pending_shift_closes.json). Держим в памяти и дописываем при следующей успешной записи — очередь закрытия смен не теряется,
    /// а «файл занят» при чтении больше не превращается в «пусто» с затиранием остальных записей.</summary>
    private static readonly List<Entry> Unsaved = new();

    /// <param name="forWrite">true — читаем, чтобы записать: «файл занят» пробрасывается (запись не выполняется, файл не затирается).</param>
    private static List<Entry> LoadAllNoLock(bool forWrite = true)
    {
        List<Entry> list;
        try
        {
            var text = AtomicFile.ReadAllText(FilePath); // 2026-10-29: см. LoadAll — битый файл читается из .bak
            list = text is not null
                ? JsonSerializer.Deserialize<List<Entry>>(text, Json) ?? new List<Entry>()
                : new List<Entry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (forWrite)
                throw;
            list = new List<Entry>();
        }
        catch
        {
            list = new List<Entry>();
        }

        foreach (var u in Unsaved)
        {
            if (!list.Any(e => string.Equals(e.ShiftId, u.ShiftId, StringComparison.OrdinalIgnoreCase)))
                list.Add(u);
        }

        return list;
    }

    /// <summary>Загрузка для записи; false — файл занят, ничего менять нельзя (запись отложена).</summary>
    private static bool TryLoadForWrite(out List<Entry> all)
    {
        try
        {
            all = LoadAllNoLock(forWrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Очередь закрытия смен: файл занят ({ex.Message}) — запись отложена, прежние записи не затираем.", "WARNING");
            all = new List<Entry>();
            return false;
        }
    }

    private static void SaveNoLock(List<Entry> all)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json)); // 2026-10-29: атомарно, с копией .bak
            Unsaved.Clear();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Очередь закрытия смен не сохранена: {ex.Message}", "WARNING");
        }
    }
}
