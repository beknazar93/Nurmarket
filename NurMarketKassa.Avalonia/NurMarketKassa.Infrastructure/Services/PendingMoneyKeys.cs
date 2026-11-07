using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В2 Л5-14 / Л4-24 — ключ повтора (Idempotency-Key) денежной операции ПЕРЕЖИВАЕТ перезапуск кассы. Раньше ключи денежных POST
/// (<c>MoneyRequestKeys</c>, оплата взноса поставщику в карточке) жили только в памяти: ответ не дошёл, касса перезапущена — повтор уходил с новым
/// ключом, и сервер проводил операцию второй раз. Теперь ключ пишется на диск ДО отправки и забывается после ответа по существу (успех или отказ).
/// Подпись операции (метод + путь + хэш тела или номер взноса) → ключ; старше <see cref="Lifetime"/> — не используется (новая операция).
/// Файл <c>%LOCALAPPDATA%\NurMarketKassa\pending-money-keys.json</c> — данные компании (уносится вместе с остальным при смене компании; запись атомарная).
/// Изменение файла другой программой (владелец) или стирание при смене компании видно сразу: файл перечитывается по отпечатку (время + длина).
/// Неустранимая неоднозначность остаётся: две ОДИНАКОВЫЕ законные операции подряд, первая из которых завершилась «ответ не получен», неотличимы от
/// повтора — для них нужен идентификатор «открытия окна» (client_ref) на стороне окон; здесь он не вводится.</summary>
public static class PendingMoneyKeys
{
    /// <summary>Сколько живёт ключ без ответа сервера.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private sealed class Row
    {
        public string Signature { get; set; } = "";
        public string Key { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    private static readonly object Sync = new();
    private static Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private static string? _stamp;

    private static string FilePath => Path.Combine(AppPaths.KassaLocal, "pending-money-keys.json");

    /// <summary>Ключ для операции с этой подписью: живой прежний (в том числе записанный до перезапуска) или новый (сразу пишется на диск).
    /// <paramref name="createdUtc"/> — когда ключ создан.</summary>
    public static string GetOrCreate(string signature, out DateTime createdUtc)
    {
        lock (Sync)
        {
            Refresh();
            var now = DateTime.UtcNow;
            if (_rows.TryGetValue(signature, out var row) && now - row.CreatedUtc <= Lifetime && row.Key.Length > 0)
            {
                createdUtc = row.CreatedUtc;
                return row.Key;
            }
            Prune(now);
            row = new Row { Signature = signature, Key = Guid.NewGuid().ToString(), CreatedUtc = now };
            _rows[signature] = row;
            Save();
            createdUtc = now;
            return row.Key;
        }
    }

    public static string GetOrCreate(string signature) => GetOrCreate(signature, out _);

    /// <summary>Есть ли живой ключ для этой подписи (ничего не создаёт и не пишет).</summary>
    public static bool Has(string signature)
    {
        lock (Sync)
        {
            Refresh();
            return _rows.TryGetValue(signature, out var row) && DateTime.UtcNow - row.CreatedUtc <= Lifetime;
        }
    }

    /// <summary>Ответ по существу получен (успех или отказ) — ключ больше не нужен.</summary>
    public static void Forget(string signature)
    {
        lock (Sync)
        {
            Refresh();
            if (_rows.Remove(signature))
                Save();
        }
    }

    /// <summary>Для тестов: имитация перезапуска кассы — память забывается, ключи читаются с диска.</summary>
    internal static void ResetMemoryForTests()
    {
        lock (Sync)
        {
            _rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            _stamp = null;
        }
    }

    private static string Stamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length.ToString(CultureInfo.InvariantCulture) : "none";
        }
        catch
        {
            return "?" + Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>Перечитать файл, если он изменился не нами (другая программа, стирание при смене компании, первый вызов).</summary>
    private static void Refresh()
    {
        var stamp = Stamp();
        if (_stamp != null && string.Equals(stamp, _stamp, StringComparison.Ordinal))
            return;
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(FilePath)) ?? new List<Row>();
                var now = DateTime.UtcNow;
                foreach (var r in list.Where(r => !string.IsNullOrEmpty(r.Signature) && !string.IsNullOrEmpty(r.Key) && now - r.CreatedUtc <= Lifetime))
                    rows[r.Signature] = r;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ключи повтора денежных операций не прочитаны ({ex.GetType().Name}: {ex.Message}) — начинаем с пустого списка.", "WARNING");
        }
        _rows = rows;
        _stamp = stamp;
    }

    private static void Prune(DateTime now)
    {
        foreach (var dead in _rows.Where(e => now - e.Value.CreatedUtc > Lifetime).Select(e => e.Key).ToList())
            _rows.Remove(dead);
    }

    private static void Save()
    {
        try
        {
            Prune(DateTime.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_rows.Values.ToList()));
            _stamp = Stamp();
        }
        catch (Exception ex)
        {
            // Диск — страховка на случай перезапуска; сама отправка с ключом из памяти продолжается.
            PosLogger.Log($"Ключи повтора денежных операций не записаны на диск ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            _stamp = Stamp(); // память остаётся рабочей: не перечитываем файл и не теряем ключи этого запуска
        }
    }
}
