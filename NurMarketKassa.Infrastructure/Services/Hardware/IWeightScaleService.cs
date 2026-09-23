namespace NurMarketKassa.Services.Hardware;

public interface IWeightScaleService : IDisposable
{
    double? LastWeight { get; }

    string Status { get; }

    bool IsAvailable { get; }

    void Start();

    void Stop();

    Task<double> GetWeightAsync(CancellationToken cancellationToken = default);
}
