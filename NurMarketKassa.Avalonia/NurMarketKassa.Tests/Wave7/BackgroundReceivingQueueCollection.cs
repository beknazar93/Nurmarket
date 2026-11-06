namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06: тесты очереди фоновых приходов делят статику (PosApp.CatalogApi, ServerOutageMonitor, ServerClock, BackgroundReceivingQueue.DataDirOverride):
/// любой успешный ответ из соседнего параллельного теста возвращает «Онлайн» посреди проверки «нет связи». Поэтому коллекция идёт одна, без параллельных соседей.</summary>
[CollectionDefinition("BackgroundReceivingQueue", DisableParallelization = true)]
public sealed class BackgroundReceivingQueueCollection
{
}
