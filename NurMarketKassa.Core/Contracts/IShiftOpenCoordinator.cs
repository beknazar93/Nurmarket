namespace NurMarketKassa.Core.Contracts;

/// <summary>UI-координатор открытия смены (диалог остатка в кассе).</summary>
public interface IShiftOpenCoordinator
{
    Task<bool> TryOpenShiftAsync(CancellationToken cancellationToken = default);
}
