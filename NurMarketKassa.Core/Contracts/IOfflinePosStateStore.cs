namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Контракт сохранения офлайн-состояния кассы (баланс смены и идентификаторы) в локальное хранилище.
/// </summary>
public interface IOfflinePosStateStore
{
    void SaveFromApp(decimal? shiftBalance);
}
