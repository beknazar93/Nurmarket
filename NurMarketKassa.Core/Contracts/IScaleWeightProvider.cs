namespace NurMarketKassa.Core.Contracts;

public interface IScaleWeightProvider
{
    Task<double?> TryReadWeightAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
