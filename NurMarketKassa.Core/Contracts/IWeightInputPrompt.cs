namespace NurMarketKassa.Core.Contracts;

public interface IWeightInputPrompt
{
    Task<double?> PromptWeightKgAsync(string productTitle, CancellationToken cancellationToken = default);
}
