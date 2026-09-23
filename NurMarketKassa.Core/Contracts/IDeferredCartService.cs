namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Контракт отложенных чеков: сохранение текущей корзины и открытие нового чека.
/// </summary>
public interface IDeferredCartService
{
    Task<DeferredCartResult> DeferCurrentCartAsync(
        string? label = null,
        bool startNewSale = true,
        CancellationToken cancellationToken = default);

    int PendingCount { get; }
}

/// <summary>Результат откладывания чека.</summary>
public sealed class DeferredCartResult
{
    public bool IsSuccess { get; init; }
    public string? Label { get; init; }
    public string? ErrorMessage { get; init; }

    public static DeferredCartResult Succeeded(string label) =>
        new() { IsSuccess = true, Label = label };

    public static DeferredCartResult Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}
