using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>
/// Адаптирует статическое хранилище офлайн-состояния кассы
/// к интерфейсу <see cref="IOfflinePosStateStore"/> для внедрения через DI.
/// </summary>
public sealed class OfflinePosStateStoreAdapter : IOfflinePosStateStore
{
    public void SaveFromApp(decimal? shiftBalance) => OfflinePosStateStore.SaveFromApp(shiftBalance ?? 0m);
}
