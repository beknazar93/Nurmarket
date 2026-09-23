using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Чтение веса с физических весов через COM-порт.</summary>
public sealed class ComWeightScaleService : IWeightScaleService
{
    private readonly object _lock = new();
    private ScaleReaderService? _scale;

    public double? LastWeight
    {
        get
        {
            lock (_lock)
                return _scale?.LastWeight;
        }
    }

    public string Status
    {
        get
        {
            lock (_lock)
                return _scale?.Status ?? "не запущены";
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
                return _scale != null;
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _scale?.Dispose();
            _scale = null;

            var prefs = UserPreferences.Instance;
            if (!prefs.ScaleEnabled)
            {
                PosLogger.Log("Весы COM: не запущены — выключены в настройках кассы.", "SCALE");
                return;
            }

            if (HardwareModeHelper.IsNonePort(prefs.ScaleComPort))
            {
                PosLogger.Log("Весы COM: не запущены — COM-порт не выбран.", "SCALE");
                return;
            }

            try
            {
                var cfg = prefs.ToScaleSettings();
                ScaleReaderService.ValidateSettings(cfg);
                var port = HardwarePortHelper.NormalizeComPort(cfg.ComPort);
                PosLogger.Log($"Весы COM: запуск фонового чтения {port} @ {cfg.BaudRate}", "SCALE");
                _scale = new ScaleReaderService(cfg);
                _scale.Start();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы COM: не удалось запустить: {ex.Message}", "SCALE");
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_scale != null)
                PosLogger.Log("Весы COM: остановка фонового чтения.", "SCALE");
            _scale?.Dispose();
            _scale = null;
        }
    }

    public async Task<double> GetWeightAsync(CancellationToken cancellationToken = default)
    {
        ScaleReaderService? scale;
        lock (_lock)
            scale = _scale;

        if (scale == null)
            throw new InvalidOperationException("Весы не запущены. Включите весы в настройках кассы.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var weight = scale.LastWeight;
                if (weight is > 0)
                    return weight.Value;

                await Task.Delay(120, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var last = scale.LastWeight;
            if (last is > 0)
                return last.Value;

            throw new InvalidOperationException("Не удалось получить стабильный вес с весов.");
        }

        var finalWeight = scale.LastWeight;
        if (finalWeight is > 0)
            return finalWeight.Value;

        throw new InvalidOperationException("Не удалось получить вес с весов.");
    }

    public void Dispose() => Stop();
}
