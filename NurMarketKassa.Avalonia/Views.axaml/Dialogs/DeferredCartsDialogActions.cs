using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public sealed class DeferredCartsDialogActions
{
    public Func<IReadOnlyList<DeferredCartEntry>, Task<bool>>? MergeIntoCurrentAsync { get; init; }

    public Func<DeferredCartEntry, Task<bool>>? OpenAsSeparateAsync { get; init; }
}
