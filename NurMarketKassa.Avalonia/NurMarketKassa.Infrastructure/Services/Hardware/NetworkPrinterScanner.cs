using System.Collections.Concurrent;
using System.Net.Sockets;

#nullable enable

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-01 («Найти в сети» для сетевых принтеров): проверка порта 9100 на адресах локальной подсети компьютера (подсети берутся у
/// <see cref="ScaleNetworkScanner.GetLocalSubnets"/>: маски шире /24 сужены до /24). Запускается ТОЛЬКО по явному нажатию кнопки — ни при запуске программы,
/// ни по таймеру. Только подключение к порту (данные не отправляются), параллельно не больше 32, таймаут 400 мс на адрес, с отменой и ходом.
/// Имя и модель принтера не определяются.</summary>
public static class NetworkPrinterScanner
{
    public const int Port = NetworkPrinterAddress.DefaultPort;
    private const int Parallelism = 32;
    private const int TimeoutMs = 400;
    private const int MaxHosts = 1024;

    /// <summary>Ход: проверено адресов из общего числа и сколько найдено.</summary>
    public sealed record Progress(int Done, int Total, int Found);

    /// <summary>Сколько адресов будет проверено (для подписи «проверю N адресов»).</summary>
    public static int HostCount() => EnumerateHosts().Count;

    private static List<uint> EnumerateHosts()
    {
        var hosts = new List<uint>();
        var seen = new HashSet<uint>();
        foreach (var subnet in ScaleNetworkScanner.GetLocalSubnets())
        {
            var own = ScaleNetworkScanner.ToNumber(subnet.LocalAddress);
            for (var h = subnet.FirstHost; h <= subnet.LastHost && h != 0; h++)
            {
                if (h != own && seen.Add(h))
                    hosts.Add(h);
                if (hosts.Count >= MaxHosts || h == uint.MaxValue)
                    return hosts;
            }
        }
        return hosts;
    }

    /// <summary>Адреса вида «TCP:192.168.1.50:9100» (по возрастанию IP), у которых открыт порт 9100. Не вызывать с потока интерфейса.</summary>
    public static Task<IReadOnlyList<string>> ScanAsync(IProgress<Progress>? progress, CancellationToken ct) =>
        ScanAsync(EnumerateHosts(), IsOpenAsync, progress, ct);

    /// <summary>2026-11-13 (мастер «Подключить принтер»): тот же поиск по заданному списку адресов и способу проверки порта — для тестов с подменным «сокетом».</summary>
    internal static async Task<IReadOnlyList<string>> ScanAsync(IReadOnlyList<uint> hosts, Func<string, CancellationToken, Task<bool>> isOpenAsync,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        var found = new ConcurrentBag<uint>();
        var done = 0;
        progress?.Report(new Progress(0, hosts.Count, 0));
        using var gate = new SemaphoreSlim(Parallelism);
        var tasks = hosts.Select(async ip =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (!ct.IsCancellationRequested && await isOpenAsync(ScaleNetworkScanner.ToIp(ip), ct).ConfigureAwait(false))
                    found.Add(ip);
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report(new Progress(n, hosts.Count, found.Count));
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // остановлено кнопкой — отдаём то, что успели найти
        }
        return found.OrderBy(x => x).Select(x => NetworkPrinterAddress.Format(ScaleNetworkScanner.ToIp(x), Port)).ToList();
    }

    private static async Task<bool> IsOpenAsync(string ip, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, Port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
