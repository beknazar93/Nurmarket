using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Чтение веса с физических весов через COM-порт.
///
/// 2026-09-26, «подключение одновременно 2–3 весов»: кроме основных весов читаются «Весы 2» и
/// «Весы 3» (свой порт и скорость, протокол — как у основных). Кассиру выбирать ничего не нужно:
/// вес берётся с тех весов, на которых сейчас лежит товар, а если товар лежит на нескольких —
/// с тех, где вес появился последним.</summary>
public sealed class ComWeightScaleService : IWeightScaleService
{
    private readonly object _lock = new();
    private readonly List<(string Name, ScaleReaderService Reader)> _scales = new();

    public double? LastWeight
    {
        get
        {
            lock (_lock)
                return PickActive()?.Reader.LastWeight;
        }
    }

    public string Status
    {
        get
        {
            lock (_lock)
            {
                if (_scales.Count == 0)
                    return "не запущены";
                if (_scales.Count == 1)
                    return _scales[0].Reader.Status;
                return string.Join("; ", _scales.Select(s => $"{s.Name}: {s.Reader.Status}"));
            }
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
                return _scales.Count > 0;
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            StopAll();

            var prefs = UserPreferences.Instance;
            var usedPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!prefs.ScaleEnabled)
                PosLogger.Log("Весы COM: не запущены — выключены в настройках кассы.", "SCALE");
            else if (HardwareModeHelper.IsNonePort(prefs.ScaleComPort))
                PosLogger.Log("Весы COM: не запущены — COM-порт не выбран.", "SCALE");
            else
                TryStart("Весы 1", prefs.ToScaleSettings(), usedPorts);

            foreach (var number in new[] { 2, 3 })
            {
                var cfg = prefs.ToExtraScaleSettings(number);
                if (!cfg.Enabled)
                    continue;
                if (string.IsNullOrWhiteSpace(cfg.ComPort) || HardwareModeHelper.IsNonePort(cfg.ComPort))
                {
                    PosLogger.Log($"Весы {number}: не запущены — COM-порт не выбран.", "SCALE");
                    continue;
                }

                TryStart($"Весы {number}", cfg, usedPorts);
            }
        }
    }

    private void TryStart(string name, ScaleSettings cfg, HashSet<string> usedPorts)
    {
        try
        {
            ScaleReaderService.ValidateSettings(cfg);
            var port = HardwarePortHelper.NormalizeComPort(cfg.ComPort);
            if (!usedPorts.Add(port))
            {
                PosLogger.Log($"{name}: не запущены — порт {port} уже занят другими весами.", "SCALE");
                return;
            }

            PosLogger.Log($"{name} COM: запуск фонового чтения {port} @ {cfg.BaudRate}", "SCALE");
            var reader = new ScaleReaderService(cfg);
            reader.Start();
            _scales.Add((name, reader));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"{name} COM: не удалось запустить: {ex.Message}", "SCALE");
        }
    }

    /// <summary>Весы, с которых брать вес: из тех, где лежит товар, — где вес изменился последним;
    /// товара нет ни на одних — основные (первые запущенные).</summary>
    private (string Name, ScaleReaderService Reader)? PickActive()
    {
        if (_scales.Count == 0)
            return null;
        if (_scales.Count == 1)
            return _scales[0];

        (string Name, ScaleReaderService Reader)? best = null;
        foreach (var scale in _scales)
        {
            if (scale.Reader.LastWeight is not > 0)
                continue;
            if (best is null || scale.Reader.WeightChangedAtMs > best.Value.Reader.WeightChangedAtMs)
                best = scale;
        }

        return best ?? _scales[0];
    }

    public void Stop()
    {
        lock (_lock)
            StopAll();
    }

    private void StopAll()
    {
        if (_scales.Count > 0)
            PosLogger.Log("Весы COM: остановка фонового чтения.", "SCALE");
        foreach (var scale in _scales)
            scale.Reader.Dispose();
        _scales.Clear();
    }

    public async Task<double> GetWeightAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_scales.Count == 0)
                throw new InvalidOperationException("Весы не запущены. Включите весы в настройках кассы.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var weight = LastWeight;
                if (weight is > 0)
                    return weight.Value;

                await Task.Delay(120, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var last = LastWeight;
            if (last is > 0)
                return last.Value;

            throw new InvalidOperationException("Не удалось получить стабильный вес с весов.");
        }

        var finalWeight = LastWeight;
        if (finalWeight is > 0)
            return finalWeight.Value;

        throw new InvalidOperationException("Не удалось получить вес с весов.");
    }

    public void Dispose() => Stop();
}
