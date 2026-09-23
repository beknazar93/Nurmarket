namespace NurMarketKassa.Core.Contracts;

public interface IPosSessionService
{
    Task<bool> EnsureOperationalAsync(CancellationToken cancellationToken = default, bool silent = false);
}
