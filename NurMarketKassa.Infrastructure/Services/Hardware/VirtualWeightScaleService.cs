namespace NurMarketKassa.Services.Hardware;

/// <summary>Виртуальные весы для разработки без COM-порта.</summary>
public sealed class VirtualWeightScaleService : IWeightScaleService
{
    private const double DemoWeightKg = 1.250;
    private double? _lastWeight = DemoWeightKg;

    public double? LastWeight => _lastWeight;

    public string Status => "Демо-режим (виртуальные весы)";

    public bool IsAvailable => true;

    public void Start()
    {
        _lastWeight = DemoWeightKg;
        PosLogger.Log("Виртуальные весы: запущены (демо-режим).", "SCALE");
    }

    public void Stop()
    {
        PosLogger.Log("Виртуальные весы: остановлены.", "SCALE");
    }

    public async Task<double> GetWeightAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        _lastWeight = DemoWeightKg;
        return DemoWeightKg;
    }

    public void Dispose() => Stop();
}
