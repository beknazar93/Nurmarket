using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-09-28: каким способом найдено устройство (колонка «Как найдено» в окне поиска).</summary>
[Flags]
public enum ScaleFoundBy
{
    None = 0,
    /// <summary>Ответил на ping.</summary>
    Ping = 1,
    /// <summary>Есть в ARP-таблице Windows (молчит на ping, но живой).</summary>
    Arp = 2,
    /// <summary>Ответил на широковещательный UDP-запрос FCh протокола ШТРИХ-ПРИНТ.</summary>
    Broadcast = 4,
    /// <summary>Найден, пока у компьютера был временный второй адрес в этой подсети.</summary>
    TempAddress = 8,
    /// <summary>Чужая подсеть проверена без временного адреса — пакеты шли через роутер.</summary>
    Routed = 16,
}

/// <summary>Диапазон адресов для перебора: подсеть (сеть + префикс) и какие адреса в ней проверяем.</summary>
public sealed record ScaleScanRange(string Label, uint Network, int Prefix, uint First, uint Last)
{
    public int HostCount => (int)(Last - First + 1);
    public uint Mask => ScaleNetworkScanner.MaskOf(Prefix);
    public uint Broadcast => Network | ~Mask;
    public string MaskText => ScaleNetworkScanner.ToIp(Mask);
    public string RangeText => $"{ScaleNetworkScanner.ToIp(First)} – {ScaleNetworkScanner.ToIp(Last)}";
}

/// <summary>Типовая «заводская» подсеть весов/роутеров. Key — для подписи в окне (объяснение
/// на 5 языках живёт в окне, здесь только данные).</summary>
public sealed record ScaleSubnetPreset(string Key, ScaleScanRange Range);

/// <summary>Сетевой адаптер, на который можно временно добавить второй адрес.</summary>
public sealed record ScaleScanAdapter(string Name, string Description, int Index, bool IsDhcp, bool IsWireless, IReadOnlyList<IPAddress> Addresses)
{
    public string AddressesText => string.Join(", ", Addresses.Select(a => a.ToString()));
}

/// <summary>Ответ весов ШТРИХ-ПРИНТ на широковещательный запрос: IP отправителя = IP весов.</summary>
public sealed record ScaleBroadcastReply(string Ip, uint IpNumber, string ViaLocalAddress, int Port, string Info);

/// <summary>
/// 2026-09-28: просьба владельца «проверка всех подсетей при поиске ip адреса весов даже если
/// комп стоит статичный ip адрес». Весы часто стоят с заводским адресом в ДРУГОЙ подсети
/// (Rongta 192.168.1.87, Штрих-ПРИНТ 192.168.0.202), а обычный ping туда уходит на шлюз и до
/// весов в том же кабеле не доходит. Здесь:
/// 1. Широковещательный UDP-запрос ШТРИХ-ПРИНТ FCh (без пароля) на 255.255.255.255 и на
///    широковещательный адрес каждой подсети ПК. ЧЕСТНАЯ ОГОВОРКА: по «Протоколу весов
///    Штрих-Принт v1.6» (раздел «Широковещание») весы принимают широковещательные команды,
///    только если их включили командой 0Ah (нужен пароль администратора, по прямому адресу),
///    «по умолчанию поддержка приёма широковещательных команд отключена», ответа на
///    широковещательную команду весы не формируют, а FCh в режиме Broadcast вообще не
///    поддерживается (таблица «Поддерживаемые команды»). То есть заводские весы, скорее всего,
///    промолчат; запрос безвреден (FCh без пароля, ничего не меняет) и ловит прошивки, которые
///    всё-таки отвечают. Для Rongta и TM-30F широковещательный поиск в их руководствах не
///    описан — не делаем.
/// 2. Перебор «чужих» подсетей: типовые заводские подсети и своя подсеть/диапазон. Без
///    временного адреса пакеты идут через роутер (дойдут, только если роутер знает эту сеть).
/// 3. Временный второй адрес ПК в подсети весов (netsh … store=active, одно окно UAC на всю
///    операцию) — тогда ping/ARP/порты работают как в своей сети. См. ScaleTempAddressSession.
/// </summary>
public static partial class ScaleNetworkScanner
{
    /// <summary>Самый широкий диапазон, который разрешаем перебирать за раз (≈ /22).</summary>
    public const int MaxRangeHosts = 1024;

    private const int BroadcastListenMs = 1500;

    /// <summary>Типовые подсети, где весы оказываются «из коробки» или после другой сети.
    /// Откуда каждая — в объяснениях окна (ScaleNetworkScanWindow.PresetWhy).</summary>
    public static IReadOnlyList<ScaleSubnetPreset> TypicalScaleSubnets { get; } = new[]
    {
        Preset("shtrikh", 192, 168, 0),    // ШТРИХ-ПРИНТ: заводской 192.168.0.202 (руководство администратора 4.5, п. 1.6.1.1)
        Preset("rongta", 192, 168, 1),     // Rongta RLS: заводской 192.168.1.87 (Label Scale User Manual)
        Preset("huawei", 192, 168, 8),     // 4G-модемы/роутеры Huawei раздают 192.168.8.x
        Preset("mikrotik", 192, 168, 88),  // MikroTik по умолчанию 192.168.88.1
        Preset("gpon", 192, 168, 100),     // оптические терминалы провайдеров (Huawei/ZTE) 192.168.100.1
        Preset("xiaomi", 192, 168, 31),    // роутеры Xiaomi 192.168.31.1
        Preset("ten", 10, 0, 0),           // часть роутеров и точек доступа раздаёт 10.0.0.x
    };

    private static ScaleSubnetPreset Preset(string key, byte a, byte b, byte c)
    {
        var network = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8);
        return new ScaleSubnetPreset(key, new ScaleScanRange($"{a}.{b}.{c}.0/24", network, 24, network + 1, network + 254));
    }

    public static uint MaskOf(int prefix) => prefix <= 0 ? 0u : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);

    /// <summary>Разбирает «своя подсеть или диапазон»: 192.168.5.0/24, 192.168.5.10-192.168.5.60,
    /// 192.168.5.10-60 или один адрес. <paramref name="error"/>: empty / format / too_wide.</summary>
    public static bool TryParseRange(string? text, out ScaleScanRange? range, out string? error)
    {
        range = null;
        error = null;
        var s = (text ?? "").Trim().Replace(" ", "").Replace('–', '-').Replace('—', '-');
        if (s.Length == 0)
        {
            error = "empty";
            return false;
        }

        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length != 2 || !TryParseIp(parts[0], out var ip)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 1 or > 32)
            {
                error = "format";
                return false;
            }
            if (prefix < 22)
            {
                error = "too_wide";
                return false;
            }
            var mask = MaskOf(prefix);
            var network = ip & mask;
            var broadcast = network | ~mask;
            var first = prefix >= 31 ? network : network + 1;
            var last = prefix >= 31 ? broadcast : broadcast - 1;
            range = new ScaleScanRange($"{ToIp(network)}/{prefix}", network, prefix, first, last);
            return true;
        }

        uint from, to;
        if (s.Contains('-'))
        {
            var parts = s.Split('-');
            if (parts.Length != 2 || !TryParseIp(parts[0], out from))
            {
                error = "format";
                return false;
            }
            if (!TryParseIp(parts[1], out to))
            {
                // «192.168.5.10-60» — только последнее число.
                if (!byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var lastOctet))
                {
                    error = "format";
                    return false;
                }
                to = (from & 0xFFFFFF00u) | lastOctet;
            }
        }
        else if (TryParseIp(s, out from))
        {
            to = from;
        }
        else
        {
            error = "format";
            return false;
        }

        if (to < from)
            (from, to) = (to, from);
        if (to - from + 1 > MaxRangeHosts)
        {
            error = "too_wide";
            return false;
        }

        // Подсеть для временного адреса: самый узкий общий префикс, но не уже /24.
        var p = 24;
        while (p > 0 && (from & MaskOf(p)) != (to & MaskOf(p)))
            p--;
        if (p < 22)
        {
            error = "too_wide";
            return false;
        }
        var net = from & MaskOf(p);
        var bc = net | ~MaskOf(p);
        // Адрес сети и широковещательный не пингуем.
        var f = Math.Max(from, net + 1);
        var l = Math.Min(to, bc - 1);
        if (l < f)
        {
            error = "format";
            return false;
        }
        var label = from == to ? ToIp(from) : $"{ToIp(from)}-{ToIp(to)}";
        range = new ScaleScanRange(label, net, p, f, l);
        return true;
    }

    private static bool TryParseIp(string text, out uint number)
    {
        number = 0;
        // IPAddress.TryParse принимает и «10» (как 0.0.0.10) — требуем четыре числа.
        if (text.Count(c => c == '.') != 3 || !IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        number = ToNumber(ip);
        return true;
    }

    /// <summary>Входит ли адрес в настоящую (не суженную) подсеть какого-нибудь адаптера ПК.</summary>
    public static bool IsInLocalNetworks(uint ip, IReadOnlyList<LocalSubnet> subnets) =>
        subnets.Any(s => (ToNumber(s.LocalAddress) & MaskOf(s.PrefixLength)) == (ip & MaskOf(s.PrefixLength)));

    /// <summary>Подсеть ПК, пересекающаяся с диапазоном (тогда временный адрес не нужен).</summary>
    public static LocalSubnet? LocalSubnetOverlapping(ScaleScanRange range, IReadOnlyList<LocalSubnet> subnets) =>
        subnets.FirstOrDefault(s =>
        {
            var p = Math.Min(s.PrefixLength, range.Prefix);
            var m = MaskOf(p);
            return (ToNumber(s.LocalAddress) & m) == (range.Network & m);
        });

    /// <summary>Подсеть для сканирования диапазона: localAddress — адрес ПК, с которого идём
    /// (временный адрес или 0.0.0.0, если пакеты пойдут через роутер).</summary>
    public static LocalSubnet SubnetForRange(ScaleScanRange range, string adapterName, IPAddress localAddress, IPAddress? gateway = null) =>
        new(adapterName, localAddress, range.Prefix, gateway, range.First, range.Last, false);

    /// <summary>Предлагаемый адрес в подсети: с конца (.250, .249, …), не занятый и не свой.</summary>
    public static uint SuggestFreeAddress(uint network, int prefix, ICollection<uint> taken)
    {
        var broadcast = network | ~MaskOf(prefix);
        var hosts = broadcast - network - 1;
        var start = hosts >= 8 ? broadcast - 5 : broadcast - 1;
        for (var candidate = start; candidate > network; candidate--)
        {
            if (!taken.Contains(candidate))
                return candidate;
        }
        return start;
    }

    // ------------------------------------------------------------------ широковещание

    /// <summary>Отправляет ШТРИХ-ПРИНТ FCh «Получить тип устройства» (кадр 02 01 FC — тот же, что
    /// шлёт официальный драйвер, сверено по логу эмулятора) на 255.255.255.255 и на
    /// широковещательный адрес каждой подсети ПК и собирает ответы.
    /// Адрес отправителя ответа — адрес весов, даже если весы в чужой подсети.</summary>
    public static async Task<IReadOnlyList<ScaleBroadcastReply>> BroadcastShtrikhProbeAsync(
        IReadOnlyList<LocalSubnet> subnets,
        IReadOnlyCollection<int> ports,
        int listenMs = BroadcastListenMs,
        CancellationToken ct = default)
    {
        var replies = new ConcurrentDictionary<uint, ScaleBroadcastReply>();
        var frame = ShtrikhPrintProtocol.BuildFrame(ShtrikhPrintProtocol.CmdGetDeviceType, ReadOnlySpan<byte>.Empty);
        var own = new HashSet<uint>(subnets.Select(s => ToNumber(s.LocalAddress)));

        var tasks = subnets
            .GroupBy(s => ToNumber(s.LocalAddress))
            .Select(g => g.First())
            .Where(s => !IPAddress.Any.Equals(s.LocalAddress))
            .Select(async s =>
            {
                // Сокет без явной привязки к адресу — как у ShtrikhPrintLanScaleService: явный
                // bind «слушающего» сокета может вызвать окно брандмауэра Windows. Направленный
                // широковещательный адрес подсети Windows сама отправит в нужный адаптер;
                // 255.255.255.255 уходит в основной адаптер (обычно он и есть сеть весов).
                using (var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true })
                {
                    DisableUdpConnReset(udp);
                    var mask = MaskOf(s.PrefixLength);
                    var directed = (ToNumber(s.LocalAddress) & mask) | ~mask;
                    var targets = new List<IPAddress> { IPAddress.Broadcast };
                    if (s.PrefixLength < 31)
                        targets.Add(ToAddress(directed));

                    // Два раза с паузой — UDP может потерять пакет.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        foreach (var port in ports)
                        foreach (var target in targets)
                        {
                            try
                            {
                                await udp.SendAsync(frame, new IPEndPoint(target, port), ct).ConfigureAwait(false);
                            }
                            catch (SocketException)
                            {
                                // Нет маршрута для широковещания на этом адаптере — не страшно.
                            }
                        }
                        if (attempt == 0)
                            await ReceiveRepliesAsync(udp, s, own, replies, 250, ct).ConfigureAwait(false);
                    }
                    await ReceiveRepliesAsync(udp, s, own, replies, listenMs, ct).ConfigureAwait(false);
                }
            })
            .ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return replies.Values.OrderBy(r => r.IpNumber).ToList();
    }

    private static async Task ReceiveRepliesAsync(
        UdpClient udp, LocalSubnet via, HashSet<uint> own, ConcurrentDictionary<uint, ScaleBroadcastReply> replies, int listenMs, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(listenMs);
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // окно ожидания закончилось
            }
            catch (SocketException)
            {
                continue; // ICMP «порт недоступен» от чужого устройства
            }

            var from = ToNumber(result.RemoteEndPoint.Address);
            if (own.Contains(from))
                continue; // собственный широковещательный пакет вернулся к нам
            if (!ShtrikhPrintProtocol.TryParseFrame(result.Buffer, out var response)
                || response.Command != ShtrikhPrintProtocol.CmdGetDeviceType
                || response.ErrorCode != 0
                || response.Payload.Length < 6
                || response.Payload[0] != 1) // тип 1 — «Весы»
                continue;

            var name = response.Payload.Length > 6 ? ShtrikhPrintProtocol.DecodeText(response.Payload.AsSpan(6)).Trim() : "";
            replies.TryAdd(from, new ScaleBroadcastReply(
                result.RemoteEndPoint.Address.ToString(), from, via.LocalAddress.ToString(), result.RemoteEndPoint.Port,
                name.Length > 0 ? name : $"model {response.Payload[4]}"));
        }
    }

    /// <summary>Windows после ICMP «порт недоступен» роняет следующий приём UDP с ошибкой 10054;
    /// SIO_UDP_CONNRESET = false это отключает.</summary>
    private static void DisableUdpConnReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            const int SioUdpConnReset = -1744830452;
            udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch (Exception)
        {
            // Не критично: SocketException в приёме всё равно ловим.
        }
    }

    // ------------------------------------------------------------------ адаптеры

    /// <summary>Проводные и беспроводные адаптеры с IPv4-адресом — куда можно временно добавить
    /// второй адрес.</summary>
    public static IReadOnlyList<ScaleScanAdapter> GetAdaptersForTempAddress()
    {
        var result = new List<ScaleScanAdapter>();
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
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            var wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var wired = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit;
            if (!wireless && !wired)
                continue;
            try
            {
                var props = nic.GetIPProperties();
                var v4 = props.GetIPv4Properties();
                if (v4 is null)
                    continue;
                var addresses = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => u.Address)
                    .ToList();
                if (addresses.Count == 0)
                    continue;
                // Не Windows — считаем DHCP (осторожный вариант: временный адрес там всё равно не ставим).
                var dhcp = !OperatingSystem.IsWindows() || v4.IsDhcpEnabled;
                result.Add(new ScaleScanAdapter(nic.Name, nic.Description, v4.Index, dhcp, wireless, addresses));
            }
            catch (Exception)
            {
                // Адаптер без IPv4 или пропал между вызовами — пропускаем.
            }
        }

        return result
            .OrderBy(a => a.IsWireless ? 1 : 0) // проводной первым: весы обычно по кабелю
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Все IPv4-адреса этого компьютера (числами).</summary>
    public static HashSet<uint> AllLocalAddresses()
    {
        var set = new HashSet<uint>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                    set.Add(ToNumber(u.Address));
            }
        }
        catch (Exception)
        {
            // Нет доступа к списку адаптеров — вернём что успели.
        }
        return set;
    }

    /// <summary>Состояние проверки дубликата (DAD) для адреса на этом ПК; null — адреса нет.</summary>
    public static DuplicateAddressDetectionState? AddressDadState(IPAddress address)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.Equals(address))
                    return u.DuplicateAddressDetectionState;
            }
        }
        catch (Exception)
        {
            // см. выше
        }
        return null;
    }

    /// <summary>Отвечает ли адрес на ping (проверка «свободен ли адрес» перед временным адресом).</summary>
    public static async Task<bool> PingRepliesAsync(IPAddress address, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), null, null, ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Один временный адрес: IP и маска.</summary>
public sealed record ScaleTempAddress(IPAddress Address, int Prefix)
{
    public string MaskText => ScaleNetworkScanner.ToIp(ScaleNetworkScanner.MaskOf(Prefix));
}

public enum ScaleTempAddressStart
{
    /// <summary>Адреса добавлены, можно сканировать.</summary>
    Ready,
    /// <summary>Пользователь отказал в запросе прав администратора (UAC).</summary>
    UacDeclined,
    /// <summary>Не получилось (подробности — в Lines / FailureText).</summary>
    Failed,
}

/// <summary>
/// 2026-09-28: временный второй адрес ПК на время поиска весов в чужой подсети.
///
/// Одно окно UAC на всю операцию: запускаем с правами администратора один скрипт PowerShell,
/// который (1) добавляет адреса командой
/// <c>netsh interface ipv4 add address name=&lt;индекс&gt; address=… mask=… store=active</c>
/// — БЕЗ шлюза (шлюз не трогаем), store=active: даже если что-то пойдёт не так, адрес пропадёт
/// сам после перезагрузки; (2) пишет «READY» и ждёт флаг «готово» от кассы; (3) в блоке
/// finally ОБЯЗАТЕЛЬНО удаляет добавленные адреса (<c>netsh interface ipv4 delete address</c>) —
/// и при ошибке, и при отмене, и если касса закрылась/упала (скрипт следит за её процессом),
/// и по таймауту 20 минут.
///
/// DHCP-адаптер: справка netsh прямо говорит «If DHCP is enabled on the interface, it will be
/// disabled» — то есть add address перевёл бы адаптер в статику. Поэтому для DHCP-адаптера
/// скрипт сначала включает «DHCP/Static IP coexistence» (Windows 10 2004+, сборка 19041),
/// проверяет, что DHCP остался включён (иначе сразу откатывает и возвращает DHCP), а в конце
/// возвращает coexistence в прежнее состояние. На старых Windows с DHCP — отказ с объяснением.
/// </summary>
public sealed class ScaleTempAddressSession : IAsyncDisposable
{
    /// <summary>Windows 10 2004 — первая сборка с dhcpstaticipcoexistence.</summary>
    public const int MinBuildForDhcpCoexistence = 19041;

    private const int ReadyTimeoutSeconds = 180; // человек читает окно UAC
    private const int FinishTimeoutSeconds = 30;

    private readonly ScaleScanAdapter _adapter;
    private readonly IReadOnlyList<ScaleTempAddress> _addresses;
    private readonly string _dir;
    private readonly string _scriptPath;
    private readonly string _statusPath;
    private readonly string _donePath;
    private Process? _process;
    private bool _finished;

    public ScaleTempAddressSession(ScaleScanAdapter adapter, IReadOnlyList<ScaleTempAddress> addresses)
    {
        _adapter = adapter;
        _addresses = addresses;
        _dir = Path.Combine(Path.GetTempPath(), "NurMarketKassa", "scale-scan-" + Guid.NewGuid().ToString("N")[..12]);
        _scriptPath = Path.Combine(_dir, "temp-address.ps1");
        _statusPath = Path.Combine(_dir, "status.txt");
        _donePath = Path.Combine(_dir, "done.flag");
    }

    public static bool DhcpCoexistenceSupported =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= MinBuildForDhcpCoexistence;

    /// <summary>Строки статуса, которые написал скрипт (ADD_OK …, DEL_OK …, FINISHED).</summary>
    public IReadOnlyList<string> Lines { get; private set; } = Array.Empty<string>();

    /// <summary>Адреса, которые реально добавились.</summary>
    public IReadOnlyList<string> Added => Lines.Where(l => l.StartsWith("ADD_OK ", StringComparison.Ordinal)).Select(l => l[7..].Trim()).ToList();

    /// <summary>Адреса, которые не удалось удалить (или скрипт не успел отчитаться).</summary>
    public IReadOnlyList<string> NotRemoved { get; private set; } = Array.Empty<string>();

    public string ScriptText { get; private set; } = "";

    /// <summary>Команды netsh, которые выполнит скрипт, — для окна подтверждения и лога.</summary>
    public static IReadOnlyList<string> DescribeCommands(ScaleScanAdapter adapter, IReadOnlyList<ScaleTempAddress> addresses)
    {
        var lines = new List<string>();
        if (adapter.IsDhcp)
            lines.Add($"netsh interface ipv4 set interface interface={adapter.Index} dhcpstaticipcoexistence=enabled");
        foreach (var a in addresses)
            lines.Add($"netsh interface ipv4 add address name={adapter.Index} address={a.Address} mask={a.MaskText} store=active");
        foreach (var a in addresses)
            lines.Add($"netsh interface ipv4 delete address name={adapter.Index} address={a.Address}");
        if (adapter.IsDhcp)
            lines.Add($"netsh interface ipv4 set interface interface={adapter.Index} dhcpstaticipcoexistence=disabled");
        return lines;
    }

    /// <summary>Текст скрипта (чистая функция — проверяется без запуска).</summary>
    public static string BuildScript(ScaleScanAdapter adapter, IReadOnlyList<ScaleTempAddress> addresses, string statusPath, string donePath, int ownerPid)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("# NurMarket Kassa: temporary IPv4 address for the scale search. Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$status = {Q(statusPath)}");
        sb.AppendLine($"$doneFlag = {Q(donePath)}");
        sb.AppendLine($"$ownerPid = {ownerPid}");
        sb.AppendLine($"$idx = {adapter.Index}");
        sb.AppendLine($"$dhcp = {(adapter.IsDhcp ? "$true" : "$false")}");
        sb.AppendLine("$addrs = @(" + string.Join(", ", addresses.Select(a => $"@({Q(a.Address.ToString())}, {Q(a.MaskText)})")) + ")");
        sb.AppendLine("function Log([string]$m) { Add-Content -LiteralPath $status -Value $m -Encoding UTF8 }");
        sb.AppendLine("function DhcpState { \"\" + (Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).Dhcp }");
        sb.AppendLine("$added = New-Object System.Collections.ArrayList");
        sb.AppendLine("$coexTurnedOn = $false");
        sb.AppendLine("Log 'STARTED'");
        sb.AppendLine("try {");
        sb.AppendLine("  if ($dhcp) {");
        sb.AppendLine("    $cfg = (& netsh.exe interface ipv4 show interface $idx | Out-String)");
        sb.AppendLine("    if ($cfg -notmatch 'coexistence\\s*:\\s*enabled') {");
        sb.AppendLine("      & netsh.exe interface ipv4 set interface interface=$idx dhcpstaticipcoexistence=enabled | Out-Null");
        sb.AppendLine("      if ($LASTEXITCODE -ne 0) { Log \"COEX_FAIL $LASTEXITCODE\"; return }");
        sb.AppendLine("      $coexTurnedOn = $true");
        sb.AppendLine("      Log 'COEX_ENABLED'");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($a in $addrs) {");
        sb.AppendLine("    $out = (& netsh.exe interface ipv4 add address name=$idx address=$($a[0]) mask=$($a[1]) store=active 2>&1 | Out-String).Trim() -replace '\\s+', ' '");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) { [void]$added.Add($a[0]); Log \"ADD_OK $($a[0])\" } else { Log \"ADD_FAIL $($a[0]) $LASTEXITCODE $out\" }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($dhcp -and $added.Count -gt 0) {");
        sb.AppendLine("    $state = DhcpState");
        sb.AppendLine("    if ($state -ne 'Enabled') { Log \"DHCP_LOST $state\"; return }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($added.Count -eq 0) { Log 'NONE_ADDED'; return }");
        sb.AppendLine("  Log 'READY'");
        sb.AppendLine("  $deadline = (Get-Date).AddMinutes(20)");
        sb.AppendLine("  while (-not (Test-Path -LiteralPath $doneFlag)) {");
        sb.AppendLine("    if ((Get-Date) -gt $deadline) { Log 'TIMEOUT'; break }");
        sb.AppendLine("    if (-not (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue)) { Log 'OWNER_GONE'; break }");
        sb.AppendLine("    Start-Sleep -Milliseconds 300");
        sb.AppendLine("  }");
        sb.AppendLine("} finally {");
        sb.AppendLine("  foreach ($ip in $added) {");
        sb.AppendLine("    & netsh.exe interface ipv4 delete address name=$idx address=$ip | Out-Null");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) { Log \"DEL_OK $ip\" } else { Log \"DEL_FAIL $ip $LASTEXITCODE\" }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($dhcp) {");
        sb.AppendLine("    if ((DhcpState) -ne 'Enabled') { & netsh.exe interface ipv4 set address name=$idx source=dhcp | Out-Null; Log \"DHCP_RESTORED $LASTEXITCODE\" }");
        sb.AppendLine("    if ($coexTurnedOn) { & netsh.exe interface ipv4 set interface interface=$idx dhcpstaticipcoexistence=disabled | Out-Null; Log \"COEX_RESTORED $LASTEXITCODE\" }");
        sb.AppendLine("  }");
        sb.AppendLine("  Log 'FINISHED'");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Пишет скрипт и запускает его с правами администратора (одно окно UAC), ждёт
    /// «READY». При отмене — сразу сворачивает (удаляет адреса) и бросает OperationCanceledException.</summary>
    public async Task<ScaleTempAddressStart> StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        ScriptText = BuildScript(_adapter, _addresses, _statusPath, _donePath, Environment.ProcessId);
        await File.WriteAllTextAsync(_scriptPath, ScriptText, new UTF8Encoding(true), CancellationToken.None).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{_scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            _process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — «Нет» в окне UAC
        {
            _finished = true;
            return ScaleTempAddressStart.UacDeclined;
        }
        catch (Exception ex)
        {
            _finished = true;
            Lines = new[] { "START_FAIL " + ex.Message };
            return ScaleTempAddressStart.Failed;
        }

        var deadline = DateTime.UtcNow.AddSeconds(ReadyTimeoutSeconds);
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                await FinishAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            ReadStatus();
            if (Lines.Contains("READY"))
                return ScaleTempAddressStart.Ready;
            if (Lines.Contains("FINISHED") || _process is null || _process.HasExited)
            {
                await FinishAsync().ConfigureAwait(false);
                return ScaleTempAddressStart.Failed;
            }
            if (DateTime.UtcNow > deadline)
            {
                await FinishAsync().ConfigureAwait(false);
                Lines = Lines.Append("READY_TIMEOUT").ToList();
                return ScaleTempAddressStart.Failed;
            }
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Просит скрипт удалить адреса и ждёт отчёта. Повторный вызов ничего не делает.</summary>
    public async Task FinishAsync()
    {
        if (_finished)
            return;
        _finished = true;
        try
        {
            await File.WriteAllTextAsync(_donePath, "done").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Если флаг не записался — скрипт всё равно удалит адреса по таймауту/при выходе кассы.
        }

        var deadline = DateTime.UtcNow.AddSeconds(FinishTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ReadStatus();
            if (Lines.Contains("FINISHED") || _process is null || _process.HasExited)
                break;
            await Task.Delay(200).ConfigureAwait(false);
        }
        ReadStatus();

        var added = Added;
        var removed = Lines.Where(l => l.StartsWith("DEL_OK ", StringComparison.Ordinal)).Select(l => l[7..].Trim()).ToHashSet();
        NotRemoved = added.Where(a => !removed.Contains(a)).ToList();

        if (NotRemoved.Count == 0 && Lines.Contains("FINISHED"))
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception)
            {
                // Временная папка — не страшно, если осталась.
            }
        }
    }

    private void ReadStatus()
    {
        try
        {
            if (!File.Exists(_statusPath))
                return;
            using var stream = new FileStream(_statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            Lines = reader.ReadToEnd()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();
        }
        catch (IOException)
        {
            // Скрипт как раз пишет — прочитаем в следующий раз.
        }
        catch (UnauthorizedAccessException)
        {
            // см. выше
        }
    }

    public async ValueTask DisposeAsync() => await FinishAsync().ConfigureAwait(false);
}
