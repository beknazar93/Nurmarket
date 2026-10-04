using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

public sealed class ConnectivityService : IConnectivityService
{
    private readonly IAuthApiService _authApi;

    public ConnectivityService(IAuthApiService authApi) => _authApi = authApi;

    /// <summary>2026-09-29: в аварии сервера (ServerOutageMonitor) — «нет связи» без сетевого
    /// запроса: проверку ведёт монитор с нарастающей паузой, а опрос шапки (20 с) и каталог не
    /// должны каждый раз ждать таймаута. Неудачу обычной проверки монитор засчитывает как сбой.
    /// 2026-10-04: через общую проверку (SharedConnectivityCheck) — см. там.</summary>
    public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) =>
        SharedConnectivityCheck.CheckAsync(_authApi.CanReachApiAsync,
            Tr.T("проверка связи", "байланышты текшерүү", "connection check", "bağlantı kontrolü", "aloqa tekshiruvi"),
            cancellationToken);
}

/// <summary>
/// 2026-10-04, отчёт о производительности (п. 6): одна общая проверка связи вместо двух независимых.
/// Раньше шапка кассы (MainStatusViewModel, раз в 20 с) и фоновая синхронизация (SyncService, раз в
/// 45 с) каждая сама ходили на сервер (GET /) — 4,6 запроса в минуту с каждой кассы и программы
/// владельца даже тогда, когда касса и так каждую минуту успешно говорит с сервером.
///
/// Правило: если за последние <see cref="FreshWindow"/> был хоть один удачный ответ сервера (любой
/// запрос API — ServerOutageMonitor.LastSuccessAt — или эта же проверка) и монитор не видел сбоев
/// после него (состояние Online), связь считается подтверждённой без запроса. Иначе — одна проверка
/// на всех: второй вызывающий, пришедший пока она идёт, ждёт её же результата.
///
/// Мгновенный переход в «Автономно» не меняется: авария (ServerOutageMonitor.IsOutage) — сразу false
/// без запроса, а любой сбой переводит монитор в Degraded, и тогда кэш не используется. Проверку
/// «сервер ожил» в аварии по-прежнему ведёт только сам ServerOutageMonitor (HealthProbe).
/// </summary>
public static class SharedConnectivityCheck
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(60);

    private static readonly object Sync = new();
    private static Task<bool>? _inFlight;
    private static DateTimeOffset _lastProbeOkAt = DateTimeOffset.MinValue;
    private static int _probes;

    /// <summary>Сколько настоящих проверок связи (сетевых) сделано в этом запуске — для замеров.</summary>
    public static int ProbeCount => Volatile.Read(ref _probes);

    public static async Task<bool> CheckAsync(
        Func<CancellationToken, Task<bool>> probe,
        string context,
        CancellationToken cancellationToken = default)
    {
        if (ServerOutageMonitor.IsOutage)
            return false;

        if (IsRecentlyConfirmed())
            return true;

        Task<bool> task;
        lock (Sync)
        {
            if (_inFlight is { IsCompleted: false } running)
            {
                task = running;
            }
            else
            {
                task = RunProbeAsync(probe, context);
                _inFlight = task;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Связь подтверждена недавним удачным ответом, и сбоев после него не было.</summary>
    private static bool IsRecentlyConfirmed()
    {
        if (ServerOutageMonitor.State != ServerLinkState.Online)
            return false;

        var now = DateTimeOffset.Now;
        if (ServerOutageMonitor.LastSuccessAt is { } apiOk && now - apiOk < FreshWindow)
            return true;

        lock (Sync)
            return now - _lastProbeOkAt < FreshWindow;
    }

    private static async Task<bool> RunProbeAsync(Func<CancellationToken, Task<bool>> probe, string context)
    {
        // Сама проверка не отменяется отменой одного из ждущих: её результат нужен и остальным.
        // Длительность ограничена внутри (CanReachApiAsync — 4 с на адрес).
        Interlocked.Increment(ref _probes);
        bool online;
        try
        {
            online = await probe(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка связи не выполнена: {ex.GetType().Name}", "DEBUG");
            online = false;
        }

        ServerOutageMonitor.ReportProbeResult(online, context);
        if (online)
        {
            lock (Sync)
                _lastProbeOkAt = DateTimeOffset.Now;
        }

        return online && !ServerOutageMonitor.IsOutage;
    }
}
