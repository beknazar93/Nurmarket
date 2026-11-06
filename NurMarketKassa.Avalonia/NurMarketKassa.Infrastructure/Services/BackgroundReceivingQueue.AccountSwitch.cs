namespace NurMarketKassa.Services;

public sealed partial class BackgroundReceivingQueue
{
    /// <summary>2026-11-06: В3 Р8 — смена компании не состоялась (файлы возвращены на место), а воркер перед этим был остановлен (<see cref="Shutdown"/>) —
    /// запускаем его снова, если в очереди есть незавершённые приходы. Остановленный воркер сам не поднимается до следующего «Оприходовать».</summary>
    public void ResumeAfterAbortedSwitch()
    {
        bool any;
        lock (_sync)
            any = _jobs.Any(j => j.IsActive);
        if (any) // воркер после отмены завершается не мгновенно — даём ему выйти, иначе EnsureWorker увидит «ещё работает» и не запустит новый
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
                EnsureWorker();
            });
    }
}
