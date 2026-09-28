using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Что, скорее всего, стоит по адресу (догадка сканера, не гарантия).</summary>
public enum ScaleDeviceGuess
{
    Unknown,
    /// <summary>Ответил на команду FCh протокола ШТРИХ-ПРИНТ — это точно весы Штрих.</summary>
    Shtrikh,
    /// <summary>Открыт TCP 33581 — «Scale's server port» весов JHScale TM-F / TM-xA по
    /// руководству (Spec166, значение по умолчанию). Признак, не доказательство.</summary>
    TmJhScale,
    /// <summary>Открыт TCP 5001. Порт самих весов Rongta в руководствах НЕ указан; 5001 —
    /// порт по умолчанию связи RLS1000 с учётной программой. Признак, не доказательство.</summary>
    Rongta,
}

/// <summary>IPv4-подсеть одного сетевого адаптера этого компьютера и какой диапазон будем
/// перебирать.</summary>
public sealed record LocalSubnet(
    string AdapterName,
    IPAddress LocalAddress,
    int PrefixLength,
    IPAddress? Gateway,
    uint FirstHost,
    uint LastHost,
    bool Narrowed)
{
    public int HostCount => (int)(LastHost - FirstHost + 1);
    public string RangeText => $"{ScaleNetworkScanner.ToIp(FirstHost)} – {ScaleNetworkScanner.ToIp(LastHost)}";
}

/// <summary>Одно найденное устройство.</summary>
public sealed class ScaleNetworkDevice
{
    public required string Ip { get; init; }
    public uint IpNumber { get; init; }
    public string? Mac { get; set; }
    /// <summary>Производитель по префиксу MAC — только для надёжно известных префиксов.</summary>
    public string? Vendor { get; set; }
    /// <summary>MAC «локально назначенный» (телефоны/ноутбуки со случайным MAC).</summary>
    public bool IsRandomMac { get; set; }
    public string? HostName { get; set; }
    public bool PingReplied { get; set; }
    public long? RoundtripMs { get; set; }
    public bool InArpTable { get; set; }
    public bool IsGateway { get; set; }
    public bool? Tcp5001Open { get; set; }
    public bool? TmPortOpen { get; set; }
    /// <summary>Ответ весов ШТРИХ-ПРИНТ на FCh (модель/имя) или null.</summary>
    public string? ShtrikhInfo { get; set; }
    public ScaleDeviceGuess Guess { get; set; }

    // 2026-09-28: просьба владельца «проверка всех подсетей при поиске ip адреса весов даже
    // если комп стоит статичный ip адрес» — в таблице видно, как и где нашли устройство.
    /// <summary>Каким способом найдено (может быть несколько).</summary>
    public ScaleFoundBy FoundBy { get; set; }
    /// <summary>Какой диапазон сканировали, когда нашли (например «192.168.1.0/24»).</summary>
    public string? ScanLabel { get; set; }
    /// <summary>Адрес не входит ни в одну подсеть адаптеров этого компьютера — касса без
    /// роутера или временного адреса с такими весами работать не сможет.</summary>
    public bool OutsideLocalNetworks { get; set; }
}

public enum ScaleScanStage { Ping, Arp, Probe, Done }

public sealed record ScaleScanProgress(ScaleScanStage Stage, int Done, int Total);

/// <summary>
/// 2026-09-28: «Поиск весов в сети» — просьба владельца «анализ ip адресов добавь чтобы узнать
/// ip адрес подключенных весов». Только локальная подсеть этого компьютера.
///
/// Как ищем:
/// 1. Пингуем каждый адрес подсети (параллельно, не больше 48 одновременно, таймаут 400 мс).
///    Весы часто не отвечают на ping, но Windows перед отправкой ping сама спрашивает MAC
///    адреса по ARP — и живое устройство на ARP отвечает всегда, даже с закрытым ICMP.
/// 2. Читаем ARP-таблицу Windows (GetIpNetTable) — так находятся и «молчащие» весы.
/// 3. Для каждого найденного адреса: имя по обратному DNS (таймаут), TCP-подключение к 5001
///    (Rongta — см. <see cref="ScaleDeviceGuess.Rongta"/>) и 33581 (TM-F/TM-xA, Spec166 из
///    руководства JHScale), и запрос ШТРИХ-ПРИНТ FCh «Получить тип устройства» — он без
///    пароля, поэтому не тратит попытки входа и не может заблокировать весы.
/// Ничего не записывает ни в какие устройства.
/// </summary>
public static partial class ScaleNetworkScanner // 2026-09-28: partial — поиск в чужих подсетях в ScaleNetworkScanner.Subnets.cs
{
    /// <summary>TCP-порт, который проверяем для Rongta (см. оговорку у <see cref="ScaleDeviceGuess.Rongta"/>).</summary>
    public const int RongtaProbePort = 5001;

    /// <summary>Порт весов TM-30F. 2026-09-28: было 33581 (Spec166 из руководства JHScale), но
    /// программа весов владельца «Русский масштаб» — Dahua, и её datatransters.dll по умолчанию
    /// подключается к весам по TCP 4001 (сетевой модуль ZLG ZNE-100T), см. DahuaTmScaleService.</summary>
    public const int TmServerPort = 4001;

    private const int PingParallelism = 48;
    private const int PingTimeoutMs = 400;
    private const int ProbeParallelism = 16;
    private const int TcpTimeoutMs = 700;
    private const int DnsTimeoutMs = 1500;

    // ------------------------------------------------------------------ подсети

    /// <summary>IPv4-подсети активных адаптеров. Маски шире /24 сужаем до /24 вокруг адреса
    /// компьютера (Narrowed=true): перебирать 65 тысяч адресов долго, а весы почти всегда в той
    /// же «сотне», что и касса.</summary>
    public static IReadOnlyList<LocalSubnet> GetLocalSubnets()
    {
        var result = new List<LocalSubnet>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return result;
        }

        foreach (var nic in adapters)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ip = ToNumber(unicast.Address);
                var prefix = unicast.PrefixLength is > 0 and <= 32 ? unicast.PrefixLength : 24;
                if (prefix > 30)
                    continue; // /31, /32 — соседей нет

                var narrowed = prefix < 24;
                var effective = narrowed ? 24 : prefix;
                var mask = effective == 0 ? 0u : uint.MaxValue << (32 - effective);
                var network = ip & mask;
                var broadcast = network | ~mask;

                result.Add(new LocalSubnet(
                    nic.Name,
                    unicast.Address,
                    prefix,
                    gateway,
                    network + 1,
                    broadcast - 1,
                    narrowed));
            }
        }

        // Сначала подсети с шлюзом (обычная сеть магазина), потом остальные.
        return result
            .OrderByDescending(s => s.Gateway is not null)
            .ThenBy(s => s.AdapterName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ сканирование

    public static async Task<IReadOnlyList<ScaleNetworkDevice>> ScanAsync(
        LocalSubnet subnet,
        int shtrikhPort,
        IProgress<ScaleScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var devices = new ConcurrentDictionary<uint, ScaleNetworkDevice>();
        var own = ToNumber(subnet.LocalAddress);
        var gateway = subnet.Gateway is null ? 0u : ToNumber(subnet.Gateway);
        var hosts = new List<uint>(subnet.HostCount);
        for (var h = subnet.FirstHost; h <= subnet.LastHost && h != 0; h++)
        {
            if (h != own)
                hosts.Add(h);
            if (h == uint.MaxValue)
                break;
        }

        // 1. Ping.
        var done = 0;
        progress?.Report(new ScaleScanProgress(ScaleScanStage.Ping, 0, hosts.Count));
        using (var gate = new SemaphoreSlim(PingParallelism))
        {
            var tasks = hosts.Select(async h =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ToAddress(h), TimeSpan.FromMilliseconds(PingTimeoutMs), null, null, ct)
                        .ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        var d = devices.GetOrAdd(h, n => NewDevice(n));
                        d.PingReplied = true;
                        d.RoundtripMs = reply.RoundtripTime;
                        d.FoundBy |= ScaleFoundBy.Ping; // 2026-09-28: колонка «Как найдено»
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Нет ответа/ошибка сети на этом адресе — просто пропускаем.
                }
                finally
                {
                    gate.Release();
                    progress?.Report(new ScaleScanProgress(ScaleScanStage.Ping, Interlocked.Increment(ref done), hosts.Count));
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // 2. ARP-таблица — «молчащие» на ping устройства.
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ScaleScanProgress(ScaleScanStage.Arp, 0, 1));
        foreach (var (address, mac) in ReadArpTable())
        {
            if (address < subnet.FirstHost || address > subnet.LastHost || address == own)
                continue;
            var d = devices.GetOrAdd(address, n => NewDevice(n));
            d.InArpTable = true;
            d.Mac ??= mac;
            d.FoundBy |= ScaleFoundBy.Arp; // 2026-09-28: колонка «Как найдено»
        }

        foreach (var d in devices.Values)
        {
            d.IsGateway = d.IpNumber == gateway;
            if (d.Mac is not null)
            {
                d.Vendor = VendorOf(d.Mac);
                d.IsRandomMac = IsLocallyAdministered(d.Mac);
            }
        }

        // 3. Проверки портов и имя.
        var list = devices.Values.OrderBy(d => d.IpNumber).ToList();
        done = 0;
        progress?.Report(new ScaleScanProgress(ScaleScanStage.Probe, 0, list.Count));
        using (var gate = new SemaphoreSlim(ProbeParallelism))
        {
            var tasks = list.Select(async d =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var dnsTask = ReverseDnsAsync(d.Ip);
                    var rongtaTask = TcpPortOpenAsync(d.Ip, RongtaProbePort, TcpTimeoutMs, ct);
                    var tmTask = TcpPortOpenAsync(d.Ip, TmServerPort, TcpTimeoutMs, ct);
                    var shtrikhTask = ShtrikhProbeAsync(d.Ip, shtrikhPort, ct);
                    await Task.WhenAll(dnsTask, rongtaTask, tmTask, shtrikhTask).ConfigureAwait(false);

                    d.HostName = dnsTask.Result;
                    d.Tcp5001Open = rongtaTask.Result;
                    d.TmPortOpen = tmTask.Result;
                    d.ShtrikhInfo = shtrikhTask.Result;
                    d.Guess = d.ShtrikhInfo is not null ? ScaleDeviceGuess.Shtrikh
                        : d.TmPortOpen == true ? ScaleDeviceGuess.TmJhScale
                        : d.Tcp5001Open == true ? ScaleDeviceGuess.Rongta
                        : ScaleDeviceGuess.Unknown;
                }
                finally
                {
                    gate.Release();
                    progress?.Report(new ScaleScanProgress(ScaleScanStage.Probe, Interlocked.Increment(ref done), list.Count));
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        progress?.Report(new ScaleScanProgress(ScaleScanStage.Done, list.Count, list.Count));

        // Похожие на весы — наверх, дальше по адресу.
        return list
            .OrderBy(d => d.Guess == ScaleDeviceGuess.Unknown ? 1 : 0)
            .ThenBy(d => d.IpNumber)
            .ToList();
    }

    private static ScaleNetworkDevice NewDevice(uint number) => new() { Ip = ToIp(number), IpNumber = number };

    /// <summary>TCP-подключение к порту: true — принят, false — отказ/таймаут.</summary>
    public static async Task<bool> TcpPortOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(IPAddress.Parse(ip), port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>ШТРИХ-ПРИНТ FCh «Получить тип устройства» — без пароля. Возвращает текст с
    /// моделью или null, если по адресу не весы Штрих.</summary>
    private static async Task<string?> ShtrikhProbeAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var probe = new ShtrikhPrintLanScaleService(ip, port, ShtrikhPrintLanScaleService.DefaultPassword, 500, retries: 1);
            var info = await probe.GetDeviceInfoAsync(ct).ConfigureAwait(false);
            if (!info.IsScale)
                return null;
            return string.IsNullOrWhiteSpace(info.Name) ? $"model {info.Model}" : info.Name.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> ReverseDnsAsync(string ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var finished = await Task.WhenAny(lookup, Task.Delay(DnsTimeoutMs)).ConfigureAwait(false);
            if (finished != lookup)
            {
                _ = lookup.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // не оставляем необработанную ошибку
                return null;
            }
            var name = lookup.Result.HostName;
            // Windows при отсутствии имени иногда возвращает сам адрес.
            return string.IsNullOrWhiteSpace(name) || name == ip ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ ARP

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public int Index;
        public int PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PhysAddr;
        public uint Addr;
        public int Type;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    private const int ErrorInsufficientBuffer = 122;
    private const int ArpTypeInvalid = 2;

    /// <summary>ARP-таблица Windows: IP (число) → MAC «AA:BB:CC:DD:EE:FF». Недействительные
    /// записи (Windows не дождалась ответа), широковещательные и групповые адреса отбрасываются.</summary>
    public static IReadOnlyList<(uint Address, string Mac)> ReadArpTable()
    {
        var result = new List<(uint, string)>();
        if (!OperatingSystem.IsWindows())
            return result;

        var size = 0;
        var rc = GetIpNetTable(IntPtr.Zero, ref size, true);
        if (rc != ErrorInsufficientBuffer || size <= 0)
            return result;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetIpNetTable(buffer, ref size, true);
            if (rc != 0)
                return result;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpNetRow>();
            var rowPtr = buffer + 4;
            for (var i = 0; i < count; i++, rowPtr += rowSize)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(rowPtr);
                if (row.Type == ArpTypeInvalid || row.PhysAddrLen != 6 || row.PhysAddr is null)
                    continue;
                var mac = row.PhysAddr.Take(6).ToArray();
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 0x01) != 0)
                    continue; // пусто, широковещательный, групповой
                // Addr хранится в сетевом порядке байт — как лежит в памяти.
                var bytes = BitConverter.GetBytes(row.Addr);
                var address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                result.Add((address, string.Join(":", mac.Select(b => b.ToString("X2")))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    // ------------------------------------------------------------------ производитель по MAC

    /// <summary>Только префиксы, в которых мы уверены. Для весов Штрих-М, Rongta и JHScale
    /// префиксы неизвестны (свои MAC у них могут быть от производителя сетевого модуля),
    /// поэтому догадку «весы» даёт не MAC, а ответ протокола/открытый порт.</summary>
    private static readonly Dictionary<string, string> KnownVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:08:DC"] = "WIZnet",            // сетевые модули встраиваемых устройств
        ["00:50:56"] = "VMware",
        ["00:0C:29"] = "VMware",
        ["00:05:69"] = "VMware",
        ["08:00:27"] = "VirtualBox",
        ["00:15:5D"] = "Microsoft Hyper-V",
        ["B8:27:EB"] = "Raspberry Pi",
        ["DC:A6:32"] = "Raspberry Pi",
        ["E4:5F:01"] = "Raspberry Pi",
        ["24:0A:C4"] = "Espressif (ESP32)",
        ["30:AE:A4"] = "Espressif (ESP32)",
    };

    public static string? VendorOf(string mac) =>
        mac.Length >= 8 && KnownVendors.TryGetValue(mac[..8], out var vendor) ? vendor : null;

    /// <summary>Бит «локально назначенный» во втором разряде первого байта — такие MAC
    /// придумывают телефоны и ноутбуки для приватности; весы так не делают.</summary>
    public static bool IsLocallyAdministered(string mac) =>
        mac.Length >= 2 && byte.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber, null, out var first)
        && (first & 0x02) != 0;

    // ------------------------------------------------------------------ адреса

    public static uint ToNumber(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress ToAddress(uint number) =>
        new(new[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number });

    public static string ToIp(uint number) => ToAddress(number).ToString();
}
