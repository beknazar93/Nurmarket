using System.IO;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27: защита от перебора при входе по QR-бейджу (неверный/чужой код, неверный PIN). После
/// <see cref="FreeAttempts"/> неудач подряд — пауза 30 с, дальше каждая неудача удваивает её (до 15 мин). Удачный вход
/// обнуляет счётчик. Состояние в файле (перезапуск кассы паузу не снимает); кода в файле нет — только счётчик и время.</summary>
public sealed class CashierBadgeThrottle
{
    public const int FreeAttempts = 5;
    public static readonly TimeSpan FirstLock = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);

    private sealed class State
    {
        public int Failures { get; set; }
        public long LockedUntilMs { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private State _state;

    public CashierBadgeThrottle(string path)
    {
        _path = path;
        _state = Load(path);
    }

    private static CashierBadgeThrottle? _instance;

    public static CashierBadgeThrottle Instance =>
        _instance ??= new CashierBadgeThrottle(Path.Combine(AppPaths.Local, AppMode.DataFolderName, "badge_attempts.json"));

    public int Failures
    {
        get { lock (_gate) return _state.Failures; }
    }

    /// <summary>Сколько ещё ждать (null — можно пробовать).</summary>
    public TimeSpan? LockedFor(DateTimeOffset now)
    {
        lock (_gate)
        {
            var left = _state.LockedUntilMs - now.ToUnixTimeMilliseconds();
            return left > 0 ? TimeSpan.FromMilliseconds(left) : null;
        }
    }

    public void RegisterFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            _state.Failures++;
            if (_state.Failures >= FreeAttempts)
            {
                var steps = Math.Min(_state.Failures - FreeAttempts, 10);
                var lockFor = TimeSpan.FromTicks(Math.Min(FirstLock.Ticks << steps, MaxLock.Ticks));
                _state.LockedUntilMs = now.Add(lockFor).ToUnixTimeMilliseconds();
            }
            Save();
        }
    }

    public void RegisterSuccess()
    {
        lock (_gate)
        {
            if (_state.Failures == 0 && _state.LockedUntilMs == 0)
                return;
            _state = new State();
            Save();
        }
    }

    private static State Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Бейдж: счётчик попыток не прочитан ({ex.GetType().Name}) — начинаю заново.", "AUTH");
        }
        return new State();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Бейдж: счётчик попыток не сохранён ({ex.GetType().Name}).", "AUTH");
        }
    }
}
