using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (аудит №4/№5, Л4-13): список в JSON-файле с записью в фоне. Раньше история разговоров Нур Советника после каждого ответа целиком
/// читалась с диска и атомарно (со сбросом на диск и копией .bak) писалась в потоке окна. Теперь последняя версия лежит в памяти, чтение
/// идёт из памяти (если файл не менялся чужой рукой), а запись выполняется в фоне — несколько быстрых сохранений подряд сливаются в одну запись.
/// Совместимость: тот же формат файла (массив JSON), что был раньше; файл, изменённый другой программой или стёртый при смене компании
/// (время записи не совпало), перечитывается заново.</summary>
public sealed class DeferredJsonList<T>
{
    private readonly Func<string> _path;
    private readonly int _maxItems;
    private readonly object _gate = new();
    private List<T>? _mem;
    private DateTime _stamp;
    private int _version;
    private bool _writing;
    private bool _dirty;
    private Task _writer = Task.CompletedTask;

    public DeferredJsonList(Func<string> path, int maxItems)
    {
        _path = path;
        _maxItems = maxItems;
    }

    /// <summary>Копия списка (вызывающий может менять её свободно).</summary>
    public List<T> Load()
    {
        lock (_gate)
        {
            if (_dirty && _mem is not null)
                return new List<T>(_mem); // в памяти новее, чем на диске: запись ещё идёт
            var path = _path();
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { stamp = DateTime.MinValue; }
            if (_mem is not null && stamp == _stamp)
                return new List<T>(_mem);
            var read = ReadFile(path) ?? new List<T>();
            _mem = read;
            _stamp = stamp;
            return new List<T>(read);
        }
    }

    /// <summary>Запомнить список (не больше <c>maxItems</c> первых) и записать в фоне.</summary>
    public void Store(List<T> items)
    {
        lock (_gate)
        {
            _mem = items.Take(_maxItems).ToList();
            _version++;
            _dirty = true;
            if (_writing)
                return; // работающий писарь после записи увидит новую версию и запишет её
            _writing = true;
            _writer = Task.Run(WriteLoop);
        }
    }

    /// <summary>Дождаться записи в фоне (например, при закрытии окна), но не дольше <paramref name="timeout"/>.</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task writer;
        lock (_gate)
            writer = _writer;
        try { return writer.Wait(timeout); }
        catch { return true; }
    }

    private void WriteLoop()
    {
        while (true)
        {
            string path;
            string json;
            int version;
            lock (_gate)
            {
                version = _version;
                path = _path();
                json = JsonSerializer.Serialize(_mem ?? new List<T>());
            }
            DateTime stamp = DateTime.MinValue;
            var ok = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, json);
                stamp = File.GetLastWriteTimeUtc(path);
                ok = true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Список {Path.GetFileName(path)} не сохранён: {ex.Message}", "WARNING");
            }
            lock (_gate)
            {
                if (version == _version)
                {
                    if (ok)
                        _stamp = stamp;
                    _dirty = false;
                    _writing = false;
                    return;
                }
                // за время записи список менялся — пишем последнюю версию
            }
        }
    }

    private static List<T>? ReadFile(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список {Path.GetFileName(path)} не прочитан: {ex.Message}", "WARNING");
        }
        return null;
    }
}
