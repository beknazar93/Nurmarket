using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

public sealed class ScaleWeightProvider : IScaleWeightProvider
{
    private readonly IWeightScaleService _scale;

    public ScaleWeightProvider(IWeightScaleService scale) => _scale = scale;

    public IWeightScaleService Scale => _scale;

    public async Task<double?> TryReadWeightAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_scale.IsAvailable)
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var weight = _scale.LastWeight;
                if (weight is > 0)
                    return weight;

                await Task.Delay(120, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Таймаут опроса или отмена окна — не пробрасываем наружу.
        }

        return _scale.LastWeight is > 0 ? _scale.LastWeight : null;
    }
}
