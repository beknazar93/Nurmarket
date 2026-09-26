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
    }

    public static List<Entry> LoadAll()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new List<Entry>();

                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), Json)
                       ?? new List<Entry>();
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
            var all = LoadAllNoLock();
            var existing = all.FirstOrDefault(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ClosingCash = closingCash ?? existing.ClosingCash;
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
            var all = LoadAllNoLock();
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
            var all = LoadAllNoLock();
            var entry = all.FirstOrDefault(e =>
                string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;

            entry.Attempts++;
            entry.LastError = error;
            SaveNoLock(all);
        }
    }

    public static int Count => LoadAll().Count;

    private static List<Entry> LoadAllNoLock()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), Json) ?? new List<Entry>()
                : new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static void SaveNoLock(List<Entry> all)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Очередь закрытия смен не сохранена: {ex.Message}", "WARNING");
        }
    }
}
