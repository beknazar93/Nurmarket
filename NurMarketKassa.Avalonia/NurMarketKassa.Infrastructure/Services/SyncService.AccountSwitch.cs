namespace NurMarketKassa.Services;

public sealed partial class SyncService
{
    /// <summary>2026-11-06: В3 Р8 — на время переноса данных прежней компании (AccountDataIsolation.SwitchTo) занимает замок прохода синхронизации: пока он у вызывающего,
    /// очередь продаж, возвратов, закрытий смен и каталог ничего не читают и не отправляют — иначе проход мог бы писать в базу и файлы, которые как раз уносят в карантин,
    /// или отправить чек прежней компании под токеном новой. Ждёт идущий проход не дольше <paramref name="wait"/>; не дождались — null (перенос продолжается, как раньше,
    /// строка в журнале). Освободить: Dispose результата.</summary>
    public IDisposable? PauseForAccountSwitch(TimeSpan wait)
    {
        try
        {
            if (_disposed || !_syncGate.Wait(wait))
            {
                PosLogger.Log("Смена аккаунта: проход синхронизации не остановился за отведённое время — перенос данных продолжается без паузы.", "WARNING");
                return null;
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        return new GateRelease(_syncGate);
    }

    private sealed class GateRelease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public GateRelease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            try { gate?.Release(); }
            catch (ObjectDisposedException) { /* программа закрывается */ }
        }
    }
}
