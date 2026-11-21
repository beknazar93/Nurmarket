using System.Text;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (просьба владельца: «у хлеба на реальной базе внутренние 13-значные коды — нужен явный признак»): отметка товара «Без заводского штрихкода» (хлеб, выпечка…).
/// Такие товары стоят в начале каталога кассы (см. <see cref="CatalogProductTileVm.HasNoFactoryBarcode"/>).
/// Хранение: общий список id товаров — ключ общих настроек магазина <see cref="UserPreferences.NoFactoryBarcodeIdsJson"/> (JSON-массив строк, ≤ <see cref="MaxJsonBytes"/>; SettingsCloudSync, CompanyKeys —
/// как «Мои шаблоны» ценников), он же — локальный кэш в user-settings.json. Серверное поле товара позже; тогда список станет кэшем.
/// Автоотметка: код, созданный нашим генератором (<see cref="BarcodeGenerator"/>, «Создать/Сгенерировать штрихкод», barcodes/next), запоминается в короткий локальный список «ждущих»;
/// как только товар с таким штрихкодом появляется в каталоге, его id попадает в общий список (<see cref="ResolvePending"/>). Так id нового товара знать при генерации не нужно.
/// Исключений наружу нет; слишком большой список не записывается (старый остаётся).</summary>
public static class NoFactoryBarcodes
{
    public const string CloudKey = "NoFactoryBarcodeIdsJson";
    public const int MaxJsonBytes = 256 * 1024;
    private const int MaxPending = 20000; // пачка импорта на 15 000 строк — все коды ждут появления товаров в каталоге
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(60);

    private static readonly object Sync = new();
    private static string? _lastJson;
    private static bool _loaded;

    /// <summary>Список отметок изменился (отметили / сняли / пришло из облака).</summary>
    public static event Action? Changed;

    /// <summary>Товар отмечен «Без заводского штрихкода».</summary>
    public static bool IsMarked(string? id)
    {
        Refresh();
        return NoFactoryBarcodeMarks.IsMarked(id);
    }

    /// <summary>Свериться с настройками (облако могло прислать новое значение). Дёшево: без изменений — сравнение строк. true — набор поменялся.</summary>
    public static bool Refresh()
    {
        lock (Sync)
        {
            var json = UserPreferences.Instance.NoFactoryBarcodeIdsJson;
            if (_loaded && string.Equals(json, _lastJson, StringComparison.Ordinal))
                return false;
            _loaded = true;
            _lastJson = json;
            var fresh = Parse(json);
            var same = fresh.Count == NoFactoryBarcodeMarks.Count && fresh.All(NoFactoryBarcodeMarks.IsMarked);
            NoFactoryBarcodeMarks.Replace(fresh);
            return !same;
        }
    }

    /// <summary>Разбор строки ключа: безопасно. Пусто, битый JSON, не массив или больше лимита → пустой набор.</summary>
    public static HashSet<string> Parse(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            return set;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            if (parsed is not null)
                foreach (var id in parsed)
                    if (!string.IsNullOrWhiteSpace(id) && id.Length <= 64)
                        set.Add(id.Trim());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            set.Clear();
        }
        return set;
    }

    /// <summary>Отметить (<paramref name="marked"/> = true) или снять отметку с товаров. false — список не поместился в лимит, ничего не изменено.</summary>
    public static bool TrySet(IEnumerable<string?> ids, bool marked)
    {
        try
        {
            string? json;
            lock (Sync)
            {
                Refresh();
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in Snapshot())
                    set.Add(id);
                var changed = false;
                foreach (var raw in ids)
                {
                    var id = raw?.Trim();
                    if (string.IsNullOrEmpty(id) || id.Length > 64)
                        continue;
                    changed |= marked ? set.Add(id) : set.Remove(id);
                }
                if (!changed)
                    return true;
                json = set.Count == 0 ? null : JsonSerializer.Serialize(set.OrderBy(x => x, StringComparer.Ordinal).ToList());
                if (json is not null && Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
                {
                    PosLogger.Log("Без заводского штрихкода: список слишком большой для общих настроек — отметки не записаны.", "WARNING");
                    return false;
                }
                _lastJson = json;
                NoFactoryBarcodeMarks.Replace(set);
                UserPreferences.Instance.NoFactoryBarcodeIdsJson = json;
            }
            UserPreferences.Instance.SaveToDisk();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"NoFactoryBarcodes.TrySet failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return false;
        }
    }

    private static IEnumerable<string> Snapshot()
    {
        var json = UserPreferences.Instance.NoFactoryBarcodeIdsJson;
        return Parse(json);
    }

    // ───────────── автоотметка по коду из нашего генератора ─────────────

    private sealed record Pending(string Code, DateTime AtUtc);

    private static List<Pending>? _pending;
    private static HashSet<string> _pendingCodes = new(StringComparer.Ordinal);

    private static string PendingFile => Path.Combine(AppPaths.KassaRoaming, "data", "no-factory-pending.json");

    private static List<Pending> LoadPending()
    {
        if (_pending is not null)
            return _pending;
        _pending = new List<Pending>();
        _pendingCodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(PendingFile) && JsonSerializer.Deserialize<List<Pending>>(File.ReadAllText(PendingFile)) is { } list)
                _pending.AddRange(list.Where(x => !string.IsNullOrWhiteSpace(x.Code) && DateTime.UtcNow - x.AtUtc < PendingLifetime));
            foreach (var p in _pending)
                _pendingCodes.Add(p.Code);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"NoFactoryBarcodes pending read failed: {ex.GetType().Name}", "WARNING");
        }
        return _pending;
    }

    private static int _saveScheduled;

    /// <summary>Файл «ждущих» пишется не чаще раза в пару секунд: импорт создаёт тысячи кодов подряд.</summary>
    private static void SavePending()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            Interlocked.Exchange(ref _saveScheduled, 0);
            lock (Sync)
                SavePendingNow();
        });
    }

    private static void SavePendingNow()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
            File.WriteAllText(PendingFile, JsonSerializer.Serialize(_pending ?? new List<Pending>()));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"NoFactoryBarcodes pending write failed: {ex.GetType().Name}", "WARNING");
        }
    }

    /// <summary>Код создан нашим генератором (кнопка «Создать штрихкод», приёмка, накладная, ИИ-план, barcodes/next) — товар с ним получит отметку, как только появится в каталоге.</summary>
    public static void NoteGenerated(IEnumerable<string?> codes)
    {
        lock (Sync)
        {
            var pending = LoadPending();
            var any = false;
            foreach (var raw in codes)
            {
                var code = raw?.Trim();
                if (string.IsNullOrEmpty(code) || !_pendingCodes.Add(code))
                    continue;
                pending.Add(new Pending(code, DateTime.UtcNow));
                any = true;
            }
            if (!any)
                return;
            if (pending.Count > MaxPending)
            {
                foreach (var old in pending.Take(pending.Count - MaxPending))
                    _pendingCodes.Remove(old.Code);
                pending.RemoveRange(0, pending.Count - MaxPending);
            }
            SavePending();
        }
    }

    public static void NoteGenerated(string? code) => NoteGenerated(new[] { code });

    /// <summary>Сколько кодов ждут появления товара в каталоге (для тестов и журнала).</summary>
    public static int PendingCount
    {
        get
        {
            lock (Sync)
                return LoadPending().Count;
        }
    }

    /// <summary>Для тестов: забыть всё в памяти (настройки и файл «ждущих» тест подменяет сам).</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _loaded = false;
            _lastJson = null;
            _pending = null;
            _pendingCodes = new HashSet<string>(StringComparer.Ordinal);
            try { if (File.Exists(PendingFile)) File.Delete(PendingFile); } catch (IOException) { /* file busy: the test starts with empty memory anyway */ }
            NoFactoryBarcodeMarks.Replace(new HashSet<string>(StringComparer.Ordinal));
        }
    }

    /// <summary>Каталог перечитан: товары, чей штрихкод создан нашим генератором, получают отметку и выходят из списка «ждущих». Дёшево, если ждущих нет.</summary>
    public static void ResolvePending(IReadOnlyList<CatalogProductTileVm> products)
    {
        List<string>? hits = null;
        lock (Sync)
        {
            var pending = LoadPending();
            if (pending.Count == 0 || products.Count == 0)
                return;
            var byCode = new Dictionary<string, Pending>(StringComparer.Ordinal);
            foreach (var p in pending)
                byCode[p.Code] = p;
            var resolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tile in products)
            {
                var code = tile.Barcode?.Trim();
                if (string.IsNullOrEmpty(code) || !byCode.ContainsKey(code))
                    continue;
                (hits ??= new List<string>()).Add(tile.Id);
                resolved.Add(code);
            }
            var expired = pending.RemoveAll(p => resolved.Contains(p.Code) || DateTime.UtcNow - p.AtUtc >= PendingLifetime);
            if (expired > 0)
            {
                _pendingCodes = new HashSet<string>(pending.Select(p => p.Code), StringComparer.Ordinal);
                SavePending();
            }
        }
        if (hits is not null)
            TrySet(hits, true);
    }
}
