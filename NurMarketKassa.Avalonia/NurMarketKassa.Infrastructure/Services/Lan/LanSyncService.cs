using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.Lan;

/// <summary>Сосед по обмену: другая касса или программа владельца.</summary>
public sealed class LanPeerInfo
{
    public string DeviceId { get; init; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 0);
    /// <summary>На этом же компьютере (найден через общий реестр, а не по сети).</summary>
    public bool IsLocal { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastOkUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Узел обмена по локальной сети (2026-09-27). Каждая запущенная программа — касса или
/// программа владельца — принимает соединения на своём порту (47810–47819) и раз в несколько
/// секунд забирает у соседей новые события их журналов (<see cref="LanJournal"/>).
///
/// Соседей два вида:
///  • на этом же компьютере — через общий реестр в %LOCALAPPDATA%\NurMarketLan (касса и
///    программа владельца на одном моноблоке), соединение только через 127.0.0.1;
///  • в сети магазина — по широковещательному UDP на порт 47809, только если на обоих задан
///    одинаковый «код магазина». Без кода узел слушает только 127.0.0.1 и в сеть не выходит.
/// Каждый запрос подписан HMAC-SHA256: для сети — компанией и кодом магазина, для своего
/// компьютера — компанией и пользователем Windows. Чужой компьютер без кода ничего не получит.
/// </summary>
public sealed class LanSyncService
{
    public static LanSyncService Instance { get; } = new();

    public const int DiscoveryPort = 47809;
    public const int FirstPort = 47810;
    public const int LastPort = 47819;
    private const int PageSize = 300;
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, LanPeerInfo> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private string _company = "";
    private string _code = "";
    private string? _registryFile;

    /// <summary>Пришли новые события от соседей — пересчитать остатки, обновить сводку.</summary>
    public event Action? PeerDataChanged;

    /// <summary>Сменился список соседей или состояние узла — для строки состояния в настройках.</summary>
    public event Action? StatusChanged;

    public bool IsRunning => _cts != null;
    public int ListenPort { get; private set; }
    public bool NetworkMode { get; private set; }
    public string? LastError { get; private set; }

    public IReadOnlyList<LanPeerInfo> Peers =>
        _peers.Values.Where(p => DateTime.UtcNow - p.LastSeenUtc < PeerTimeout).OrderBy(p => p.Name).ToList();

    public static string DeviceId
    {
        get
        {
            var prefs = UserPreferences.Instance;
            if (string.IsNullOrWhiteSpace(prefs.LanDeviceId))
            {
                prefs.LanDeviceId = Guid.NewGuid().ToString("N");
                try { prefs.SaveToDisk(); } catch { /* сохранится со следующими настройками */ }
            }
            return prefs.LanDeviceId!;
        }
    }

    private static string Role => AppMode.IsOwner ? "owner" : "kassa";

    private static string DeviceName
    {
        get
        {
            var place = AppMode.IsOwner
                ? Tr.T("Программа владельца", "Ээсинин программасы", "Owner app", "Sahip programı", "Egasining dasturi")
                : string.IsNullOrWhiteSpace(PosApp.PosCashboxDisplayName)
                    ? Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa")
                    : PosApp.PosCashboxDisplayName!;
            return $"{place} · {Environment.MachineName}";
        }
    }

    // ------------------------------------------------------------------ запуск и остановка

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null || !UserPreferences.Instance.LanSyncEnabled)
                return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(ct), ct);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_cts == null)
                return;
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _cts = null;
            try { _listener?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _listener = null;
            _udp = null;
            RemoveRegistryFile();
            _peers.Clear();
            ListenPort = 0;
        }
        PosLogger.Log("LAN: обмен остановлен.", "LAN");
        StatusChanged?.Invoke();
    }

    /// <summary>Настройки обмена поменялись — перезапуск с новым кодом и режимом.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    private static string? CurrentCompanyId()
    {
        var key = UserPreferences.Instance.LastDataAccountKey;
        if (key != null && key.StartsWith("c:", StringComparison.Ordinal) && key.Length > 2)
            return key[2..];
        var id = CompanyInfoService.LastCompany?.Id;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Компания нужна, чтобы отличать «своих». Сначала — чьи данные сейчас лежат в папке
            // (известно и без интернета: касса могла запуститься при упавшей связи), иначе —
            // компания, которую вернул сервер при входе.
            string? company;
            while ((company = CurrentCompanyId()) == null)
                await Task.Delay(2000, ct).ConfigureAwait(false);

            _company = company;
            _code = (UserPreferences.Instance.LanShopCode ?? "").Trim();
            NetworkMode = _code.Length > 0;

            StartListener();
            if (NetworkMode)
                StartDiscovery(ct);
            WriteRegistryFile();
            PosLogger.Log($"LAN: обмен запущен, порт {ListenPort}, {(NetworkMode ? "сеть магазина + этот ПК" : "только этот ПК (код магазина не задан)")}.", "LAN");
            StatusChanged?.Invoke();

            _ = Task.Run(() => AcceptLoopAsync(ct), ct);
            var lastAnnounce = DateTime.MinValue;
            var lastPrune = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    WriteRegistryFile();
                    if (NetworkMode && DateTime.UtcNow - lastAnnounce > AnnounceInterval)
                    {
                        Announce();
                        lastAnnounce = DateTime.UtcNow;
                    }

                    ReadRegistryPeers();
                    await PullAllAsync(ct).ConfigureAwait(false);

                    if (DateTime.UtcNow - lastPrune > TimeSpan.FromMinutes(30))
                    {
                        LanJournal.Prune();
                        lastPrune = DateTime.UtcNow;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Один неудачный круг (файл занят, база занята) не должен останавливать обмен.
                    if (LastError != ex.Message)
                        PosLogger.Log($"LAN: ошибка круга обмена: {ex.Message}", "LAN");
                    LastError = ex.Message;
                }

                await Task.Delay(PullInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            PosLogger.Log($"LAN: обмен остановился с ошибкой: {ex}", "LAN");
            StatusChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ приём запросов

    private void StartListener()
    {
        var address = NetworkMode ? IPAddress.Any : IPAddress.Loopback;
        for (var port = FirstPort; port <= LastPort; port++)
        {
            try
            {
                var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
                listener.Start();
                _listener = listener;
                ListenPort = port;
                return;
            }
            catch (SocketException)
            {
                // порт занят другой программой (например, второй на этом ПК) — следующий
            }
        }

        throw new InvalidOperationException($"нет свободного порта {FirstPort}–{LastPort}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        while (!ct.IsCancellationRequested && listener != null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"LAN: приём соединения не удался: {ex.Message}", "LAN");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private sealed class LanRequest
    {
        public string Op { get; set; } = "";
        public string D { get; set; } = "";
        public long Ts { get; set; }
        public long After { get; set; }
        public bool Local { get; set; }
        public string Sig { get; set; } = "";
    }

    private sealed class LanResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string D { get; set; } = "";
        public string N { get; set; } = "";
        public string R { get; set; } = "";
        public long Last { get; set; }
        public List<LanEvent>? Events { get; set; }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n" };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || line.Length > 16_384)
                return;

            var request = JsonSerializer.Deserialize<LanRequest>(line, Json);
            LanResponse response;
            if (request == null || !Authorized(request, remote))
            {
                response = new LanResponse { Ok = false, Error = "unauthorized" };
            }
            else
            {
                response = new LanResponse { Ok = true, D = DeviceId, N = DeviceName, R = Role };
                if (request.Op == "journal")
                {
                    var (events, last) = LanJournal.ReadOwnAfter(Math.Max(0, request.After), PageSize);
                    response.Events = events;
                    response.Last = last;
                }
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json)).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or JsonException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: ошибка обработки запроса соседа: {ex.Message}", "LAN");
        }
    }

    private bool Authorized(LanRequest request, IPEndPoint? remote)
    {
        if (string.IsNullOrWhiteSpace(request.D) || request.D == DeviceId || string.IsNullOrWhiteSpace(request.Sig))
            return false;
        var skew = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(request.Ts);
        if (skew.Duration() > MaxClockSkew)
            return false;

        var fromThisPc = remote != null && IPAddress.IsLoopback(remote.Address);
        string key;
        if (request.Local)
        {
            if (!fromThisPc)
                return false;
            key = LocalKey;
        }
        else
        {
            if (_code.Length == 0)
                return false;
            key = NetworkKey;
        }

        var expected = Sign(key, request.D, request.Ts, request.Op, request.After);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(request.Sig));
    }

    private string LocalKey => $"{_company}|local|{Environment.UserName}";
    private string NetworkKey => $"{_company}|{_code}";

    private static string Sign(string key, string device, long ts, string op, long after)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{device}|{ts}|{op}|{after}"));
        return Convert.ToHexString(hash);
    }

    // ------------------------------------------------------------------ забор журналов соседей

    private async Task PullAllAsync(CancellationToken ct)
    {
        var changed = false;
        foreach (var peer in Peers)
        {
            try
            {
                changed |= await PullAsync(peer, ct).ConfigureAwait(false);
                if (peer.LastError != null || peer.LastOkUtc == null)
                    StatusChanged?.Invoke();
                peer.LastOkUtc = DateTime.UtcNow;
                peer.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (peer.LastError != ex.Message)
                {
                    PosLogger.Log($"LAN: сосед {peer.Name} ({peer.EndPoint}) недоступен: {ex.Message}", "LAN");
                    peer.LastError = ex.Message;
                    StatusChanged?.Invoke();
                }
            }
        }

        if (changed)
        {
            try
            {
                LanStockAdjuster.ApplyPeerSales();
                PeerDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"LAN: обработка данных соседей не удалась: {ex.Message}", "LAN");
            }
        }
    }

    /// <summary>Забирает новые события одного соседа. true — появились новые.</summary>
    private async Task<bool> PullAsync(LanPeerInfo peer, CancellationToken ct)
    {
        var after = LanJournal.PeerLastSeq(peer.DeviceId);
        var anyNew = false;
        for (var page = 0; page < 20; page++)
        {
            var response = await RequestAsync(peer, "journal", after, ct).ConfigureAwait(false);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error == "unauthorized"
                    ? "отказ: разный код магазина или компания"
                    : response.Error ?? "отказ");

            peer.Name = response.N;
            peer.Role = response.R;
            if (response.Last < after)
            {
                // у соседа журнал начался заново — забираем с начала (повторы отсеет ключ события)
                LanJournal.ResetPeerCursor(peer.DeviceId);
                after = 0;
                continue;
            }

            var events = response.Events ?? new List<LanEvent>();
            var newLast = events.Count > 0 ? events.Max(e => e.Seq) : Math.Max(after, 0);
            if (events.Count == 0 && response.Last > after)
                newLast = after;
            anyNew |= LanJournal.SavePeerEvents(peer.DeviceId, response.N, response.R, events, newLast) > 0;
            if (events.Count < PageSize)
                break;
            after = newLast;
        }

        return anyNew;
    }

    private async Task<LanResponse> RequestAsync(LanPeerInfo peer, string op, long after, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(peer.EndPoint.Address, peer.EndPoint.Port, timeout.Token).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 65536, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\n" };

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var local = peer.IsLocal;
        var request = new LanRequest
        {
            Op = op,
            D = DeviceId,
            Ts = ts,
            After = after,
            Local = local,
            Sig = Sign(local ? LocalKey : NetworkKey, DeviceId, ts, op, after),
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json)).ConfigureAwait(false);
        await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException("пустой ответ");
        return JsonSerializer.Deserialize<LanResponse>(line, Json) ?? throw new IOException("неверный ответ");
    }

    // ------------------------------------------------------------------ поиск соседей: этот ПК

    private static string RegistryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketLan", "peers");

    private sealed class RegistryEntry
    {
        public string Device { get; set; } = "";
        public string Company { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public int Port { get; set; }
        public int Pid { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private void WriteRegistryFile()
    {
        try
        {
            Directory.CreateDirectory(RegistryDir);
            _registryFile = Path.Combine(RegistryDir, DeviceId + ".json");
            var entry = new RegistryEntry
            {
                Device = DeviceId,
                Company = _company,
                Name = DeviceName,
                Role = Role,
                Port = ListenPort,
                Pid = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow,
            };
            File.WriteAllText(_registryFile, JsonSerializer.Serialize(entry, Json));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: реестр на этом ПК недоступен: {ex.Message}", "LAN");
        }
    }

    private void RemoveRegistryFile()
    {
        try
        {
            if (_registryFile != null && File.Exists(_registryFile))
                File.Delete(_registryFile);
        }
        catch
        {
        }
    }

    private void ReadRegistryPeers()
    {
        try
        {
            if (!Directory.Exists(RegistryDir))
                return;
            foreach (var file in Directory.EnumerateFiles(RegistryDir, "*.json"))
            {
                RegistryEntry? e;
                try
                {
                    e = JsonSerializer.Deserialize<RegistryEntry>(File.ReadAllText(file), Json);
                }
                catch
                {
                    continue;
                }

                if (e == null || e.Device == DeviceId || e.Company != _company || e.Port <= 0)
                    continue;
                if (DateTime.UtcNow - e.UpdatedUtc > PeerTimeout || !ProcessAlive(e.Pid))
                {
                    TryDelete(file, e);
                    continue;
                }

                var peer = _peers.GetOrAdd(e.Device, id => new LanPeerInfo { DeviceId = id });
                var wasNew = peer.LastSeenUtc == default;
                peer.Name = e.Name;
                peer.Role = e.Role;
                peer.EndPoint = new IPEndPoint(IPAddress.Loopback, e.Port);
                peer.IsLocal = true;
                peer.LastSeenUtc = DateTime.UtcNow;
                if (wasNew)
                {
                    PosLogger.Log($"LAN: найден сосед на этом ПК: {e.Name} (порт {e.Port}).", "LAN");
                    StatusChanged?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"LAN: чтение реестра не удалось: {ex.Message}", "LAN");
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string file, RegistryEntry e)
    {
        if (DateTime.UtcNow - e.UpdatedUtc < TimeSpan.FromMinutes(10))
            return;
        try { File.Delete(file); } catch { }
    }

    // ------------------------------------------------------------------ поиск соседей: сеть магазина

    private sealed class Beacon
    {
        public int V { get; set; }
        public string G { get; set; } = "";
        public string D { get; set; } = "";
        public int P { get; set; }
        public string R { get; set; } = "";
        public string N { get; set; } = "";
    }

    /// <summary>Метка группы: одинакова только у программ одной компании с одним кодом магазина.
    /// Сам код и номер компании в сеть не уходят.</summary>
    private string GroupTag
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("nurmarket-lan|" + NetworkKey));
            return Convert.ToHexString(hash)[..20];
        }
    }

    private void StartDiscovery(CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udp = udp;
            _ = Task.Run(() => ReceiveBeaconsAsync(udp, ct), ct);
        }
        catch (Exception ex)
        {
            LastError = $"UDP {DiscoveryPort}: {ex.Message}";
            PosLogger.Log($"LAN: поиск соседей в сети не запущен: {ex.Message}", "LAN");
        }
    }

    private async Task ReceiveBeaconsAsync(UdpClient udp, CancellationToken ct)
    {
        var tag = GroupTag;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            try
            {
                var beacon = JsonSerializer.Deserialize<Beacon>(packet.Buffer, Json);
                if (beacon == null || beacon.V != 1 || beacon.G != tag || beacon.D == DeviceId || beacon.P <= 0)
                    continue;

                var peer = _peers.GetOrAdd(beacon.D, id => new LanPeerInfo { DeviceId = id });
                if (peer.IsLocal && DateTime.UtcNow - peer.LastSeenUtc < PeerTimeout)
                    continue; // этот сосед на нашем ПК — связь через 127.0.0.1 надёжнее
                var wasNew = peer.LastSeenUtc == default;
                peer.Name = beacon.N;
                peer.Role = beacon.R;
                peer.EndPoint = new IPEndPoint(packet.RemoteEndPoint.Address, beacon.P);
                peer.IsLocal = false;
                peer.LastSeenUtc = DateTime.UtcNow;
                if (wasNew)
                {
                    PosLogger.Log($"LAN: найден сосед в сети: {beacon.N} ({peer.EndPoint}).", "LAN");
                    StatusChanged?.Invoke();
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private void Announce()
    {
        var udp = _udp;
        if (udp == null)
            return;
        var beacon = new Beacon { V = 1, G = GroupTag, D = DeviceId, P = ListenPort, R = Role, N = DeviceName };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon, Json);
        foreach (var target in BroadcastAddresses())
        {
            try
            {
                udp.Send(bytes, bytes.Length, new IPEndPoint(target, DiscoveryPort));
            }
            catch (SocketException)
            {
                // интерфейс без сети — не страшно, есть другие
            }
        }
    }

    /// <summary>Общий широковещательный адрес и адрес каждой подсети: у моноблока бывает и
    /// кабель, и Wi-Fi, а 255.255.255.255 Windows отправляет только через один из них.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null)
                        continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++)
                        bc[i] = (byte)(ip[i] | ~mask[i]);
                    result.Add(new IPAddress(bc));
                }
            }
        }
        catch
        {
        }

        return result;
    }

    // ------------------------------------------------------------------ для экрана настроек

    public string StatusLine()
    {
        if (!UserPreferences.Instance.LanSyncEnabled)
            return Tr.T("Обмен выключен.", "Алмашуу өчүрүлгөн.", "Exchange is off.", "Paylaşım kapalı.", "Almashuv o'chirilgan.");
        if (!IsRunning || ListenPort == 0)
            return LastError != null
                ? Tr.T($"Не запущен: {LastError}", $"Иштеген жок: {LastError}", $"Not running: {LastError}", $"Çalışmıyor: {LastError}", $"Ishlamayapti: {LastError}")
                : Tr.T("Запустится после входа в аккаунт.", "Аккаунтка киргенден кийин иштейт.", "Starts after signing in.", "Oturum açtıktan sonra başlar.", "Hisobga kirgandan keyin ishga tushadi.");

        var peers = Peers;
        var mode = NetworkMode
            ? Tr.T("сеть магазина и этот ПК", "дүкөндүн тармагы жана ушул компьютер", "shop network and this PC", "mağaza ağı ve bu bilgisayar", "do'kon tarmog'i va shu kompyuter")
            : Tr.T("только этот ПК, код магазина не задан", "ушул компьютер гана, дүкөндүн коду коюлган эмес", "this PC only, no shop code", "yalnızca bu bilgisayar, mağaza kodu yok", "faqat shu kompyuter, do'kon kodi berilmagan");
        var head = Tr.T($"Работает ({mode}), порт {ListenPort}.", $"Иштеп жатат ({mode}), порт {ListenPort}.", $"Running ({mode}), port {ListenPort}.",
            $"Çalışıyor ({mode}), port {ListenPort}.", $"Ishlayapti ({mode}), port {ListenPort}.");
        if (peers.Count == 0)
            return head + " " + Tr.T("Другие кассы пока не найдены.", "Башка кассалар азырынча табылган жок.", "No other tills found yet.", "Henüz başka kasa bulunamadı.", "Boshqa kassalar hozircha topilmadi.");

        var list = string.Join(", ", peers.Select(p =>
            $"{p.Name}{(p.IsLocal ? "" : $" ({p.EndPoint.Address})")}{(p.LastError != null ? " — " + p.LastError : "")}"));
        return head + " " + Tr.T($"Связь с: {list}", $"Байланыш: {list}", $"Connected to: {list}", $"Bağlantı: {list}", $"Aloqa: {list}");
    }
}
