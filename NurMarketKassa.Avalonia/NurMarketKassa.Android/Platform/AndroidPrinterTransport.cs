using System.Globalization;
using System.Net.Sockets;
using Android;
using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, Android-касса: печать чека (ESC/POS) без драйверов Windows.
///
/// В поле «Порт принтера» настроек кассы пишется:
///   USB                 — первый USB-принтер (встроенный принтер кассового аппарата обычно виден так);
///   USB:0FE6:811E       — USB-принтер с этими VID:PID (шестнадцатеричные, как в «Диспетчере устройств»);
///   BT:00:11:22:33:44:55 — Bluetooth-принтер (сначала сопрягите его в настройках Android);
///   BT                  — первый сопряжённый Bluetooth-принтер;
///   TCP:192.168.1.50    — сетевой принтер (порт 9100 по умолчанию), TCP:адрес:порт — с другим портом.
/// Все команды (чек, денежный ящик, этикетки TSPL, табло) идут через PrinterPortService.SendRawBytes,
/// поэтому работают и они. Отправка уже идёт в фоновом потоке с ограничением времени.
/// НЕ ПРОВЕРЕНО на устройстве.</summary>
internal sealed class AndroidPrinterTransport : IPlatformPrinterTransport
{
    private const string ActionUsbPermission = "kg.nurmarket.kassa.USB_PERMISSION";
    private const int DefaultTcpPort = 9100;
    private const int UsbChunk = 16 * 1024;
    private static readonly Java.Util.UUID SppUuid = Java.Util.UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    public bool Handles(string port) =>
        IsUsb(port) || IsBluetooth(port) || IsTcp(port);

    private static bool IsUsb(string port) => port == "USB" || port.StartsWith("USB:", StringComparison.Ordinal);
    private static bool IsBluetooth(string port) => port == "BT" || port.StartsWith("BT:", StringComparison.Ordinal);
    private static bool IsTcp(string port) =>
        port.StartsWith("TCP:", StringComparison.Ordinal) || port.StartsWith("NET:", StringComparison.Ordinal)
        || port.StartsWith("IP:", StringComparison.Ordinal);

    public void Write(string port, byte[] payload, TimeSpan timeout)
    {
        if (IsTcp(port))
            WriteTcp(port, payload, timeout);
        else if (IsBluetooth(port))
            WriteBluetooth(port, payload);
        else
            WriteUsb(port, payload, timeout);
    }

    public PrinterPortService.PortProbeResult Probe(string port)
    {
        try
        {
            if (IsTcp(port))
            {
                var (host, tcpPort) = ParseTcp(port);
                using var client = new TcpClient();
                var ok = client.ConnectAsync(host, tcpPort).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
                return ok
                    ? new(true, Tr.T($"● Доступен (сеть {host}:{tcpPort})", $"● Жеткиликтүү (тармак {host}:{tcpPort})", $"● Available (network {host}:{tcpPort})", $"● Kullanılabilir (ağ {host}:{tcpPort})", $"● Mavjud (tarmoq {host}:{tcpPort})"), "tcp")
                    : new(false, Tr.T($"○ Нет ответа от {host}:{tcpPort}", $"○ {host}:{tcpPort} жооп бербейт", $"○ No answer from {host}:{tcpPort}", $"○ {host}:{tcpPort} yanıt vermiyor", $"○ {host}:{tcpPort} javob bermayapti"), "tcp");
            }

            if (IsBluetooth(port))
            {
                var device = FindBluetoothDevice(port);
                return device is not null
                    ? new(true, Tr.T($"● Найден (Bluetooth {device.Name})", $"● Табылды (Bluetooth {device.Name})", $"● Found (Bluetooth {device.Name})", $"● Bulundu (Bluetooth {device.Name})", $"● Topildi (Bluetooth {device.Name})"), "bt")
                    : new(false, Tr.T("○ Bluetooth-принтер не сопряжён", "○ Bluetooth-принтер жупташтырылган эмес", "○ Bluetooth printer is not paired", "○ Bluetooth yazıcı eşleştirilmemiş", "○ Bluetooth-printer ulanmagan"), "bt");
            }

            var usb = FindUsbDevice(port, out _);
            return usb is not null
                ? new(true, Tr.T($"● Найден (USB {usb.VendorId:X4}:{usb.ProductId:X4})", $"● Табылды (USB {usb.VendorId:X4}:{usb.ProductId:X4})", $"● Found (USB {usb.VendorId:X4}:{usb.ProductId:X4})", $"● Bulundu (USB {usb.VendorId:X4}:{usb.ProductId:X4})", $"● Topildi (USB {usb.VendorId:X4}:{usb.ProductId:X4})"), "usb")
                : new(false, Tr.T("○ USB-принтер не найден", "○ USB-принтер табылган жок", "○ USB printer not found", "○ USB yazıcı bulunamadı", "○ USB-printer topilmadi"), "usb");
        }
        catch (Exception ex)
        {
            return new(false, "○ " + ex.Message, "android");
        }
    }

    // ---------------- Сеть (TCP 9100, «RAW»/JetDirect) ----------------

    private static (string Host, int Port) ParseTcp(string port)
    {
        var rest = port[(port.IndexOf(':') + 1)..].Trim().TrimStart('/');
        var host = rest;
        var tcpPort = DefaultTcpPort;
        var colon = rest.LastIndexOf(':');
        if (colon > 0 && int.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
        {
            host = rest[..colon];
            tcpPort = p;
        }
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(Tr.T("Не указан адрес сетевого принтера (TCP:адрес).", "Тармактык принтердин дареги көрсөтүлгөн эмес (TCP:дарек).", "Network printer address is missing (TCP:address).", "Ağ yazıcısının adresi belirtilmedi (TCP:adres).", "Tarmoq printeri manzili ko'rsatilmagan (TCP:manzil)."));
        return (host.ToLowerInvariant(), tcpPort);
    }

    private static void WriteTcp(string port, byte[] payload, TimeSpan timeout)
    {
        var (host, tcpPort) = ParseTcp(port);
        using var client = new TcpClient { NoDelay = true, SendTimeout = (int)timeout.TotalMilliseconds };
        if (!client.ConnectAsync(host, tcpPort).Wait(TimeSpan.FromSeconds(4)) || !client.Connected)
            throw new IOException(Tr.T($"Сетевой принтер {host}:{tcpPort} не отвечает.", $"Тармактык принтер {host}:{tcpPort} жооп бербейт.", $"Network printer {host}:{tcpPort} is not responding.", $"Ağ yazıcısı {host}:{tcpPort} yanıt vermiyor.", $"Tarmoq printeri {host}:{tcpPort} javob bermayapti."));
        using var stream = client.GetStream();
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
        try { client.Client.Shutdown(SocketShutdown.Send); } catch { /* принтер мог закрыть сам */ }
    }

    // ---------------- Bluetooth (SPP) ----------------

    private static BluetoothAdapter? Adapter =>
        (AndroidBootstrap.AppContext.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;

    private static void EnsureBluetoothPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            return;
        if (AndroidBootstrap.AppContext.CheckSelfPermission(Manifest.Permission.BluetoothConnect) == Permission.Granted)
            return;
        var activity = AndroidBootstrap.CurrentActivity;
        activity?.RunOnUiThread(() => activity.RequestPermissions(new[] { Manifest.Permission.BluetoothConnect, Manifest.Permission.BluetoothScan }, 4711));
        throw new InvalidOperationException(Tr.T("Разрешите программе доступ к Bluetooth (окно Android) и повторите печать.", "Программага Bluetooth'ка уруксат бериңиз (Android терезеси) жана басып чыгарууну кайталаңыз.", "Allow Bluetooth access for the app (Android prompt) and print again.", "Uygulamaya Bluetooth erişimi verin (Android penceresi) ve yeniden yazdırın.", "Dasturga Bluetooth ruxsatini bering (Android oynasi) va qayta chop eting."));
    }

    private static BluetoothDevice? FindBluetoothDevice(string port)
    {
        EnsureBluetoothPermission();
        var adapter = Adapter ?? throw new InvalidOperationException(Tr.T("На аппарате нет Bluetooth.", "Аппаратта Bluetooth жок.", "This device has no Bluetooth.", "Cihazda Bluetooth yok.", "Qurilmada Bluetooth yo'q."));
        if (!adapter.IsEnabled)
            throw new InvalidOperationException(Tr.T("Bluetooth выключен — включите его в настройках Android.", "Bluetooth өчүк — Android жөндөөлөрүнөн күйгүзүңүз.", "Bluetooth is off — turn it on in Android settings.", "Bluetooth kapalı — Android ayarlarından açın.", "Bluetooth o'chiq — Android sozlamalarida yoqing."));

        if (port.StartsWith("BT:", StringComparison.Ordinal) && port.Length > 3)
        {
            var mac = port[3..].Trim();
            return BluetoothAdapter.CheckBluetoothAddress(mac) ? adapter.GetRemoteDevice(mac) : null;
        }

        // «BT» — первый сопряжённый принтер: класс «изображение/печать» или имя с printer/pos.
        // 2026-10-05: встроенный принтер POS-терминала («InnerPrinter» Sunmi/iMin, «BluetoothPrinter») — первым.
        var bonded = adapter.BondedDevices?.ToList() ?? new List<BluetoothDevice>();
        return bonded.FirstOrDefault(AndroidBuiltInPrinter.IsBuiltIn)
               ?? bonded.FirstOrDefault(d => d.BluetoothClass?.MajorDeviceClass == MajorDeviceClass.Imaging)
               ?? bonded.FirstOrDefault(d => (d.Name ?? "").Contains("print", StringComparison.OrdinalIgnoreCase)
                                             || (d.Name ?? "").Contains("pos", StringComparison.OrdinalIgnoreCase)
                                             || (d.Name ?? "").Contains("inner", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteBluetooth(string port, byte[] payload)
    {
        var device = FindBluetoothDevice(port)
                     ?? throw new IOException(Tr.T("Bluetooth-принтер не найден среди сопряжённых устройств.", "Bluetooth-принтер жупташтырылган түзмөктөрдүн арасында табылган жок.", "Bluetooth printer not found among paired devices.", "Bluetooth yazıcı eşleştirilmiş cihazlar arasında bulunamadı.", "Bluetooth-printer ulangan qurilmalar orasida topilmadi."));
        try { Adapter?.CancelDiscovery(); } catch { /* поиск не шёл */ }
        using var socket = device.CreateRfcommSocketToServiceRecord(SppUuid)
                           ?? throw new IOException("Bluetooth: socket = null");
        socket.Connect();
        var stream = socket.OutputStream ?? throw new IOException("Bluetooth: нет потока записи");
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
        // Многие Bluetooth-принтеры теряют хвост чека, если соединение закрыть сразу.
        Thread.Sleep(Math.Min(1500, 200 + payload.Length / 20));
        socket.Close();
    }

    // ---------------- USB host ----------------

    private static UsbManager Usb =>
        AndroidBootstrap.AppContext.GetSystemService(Context.UsbService) as UsbManager
        ?? throw new InvalidOperationException("USB host недоступен на этом аппарате.");

    private static UsbDevice? FindUsbDevice(string port, out (UsbInterface Intf, UsbEndpoint Out)? endpoint)
    {
        endpoint = null;
        int? vid = null, pid = null;
        var parts = port.Split(':');
        if (parts.Length >= 3
            && int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            && int.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p))
        {
            vid = v;
            pid = p;
        }

        UsbDevice? fallback = null;
        (UsbInterface, UsbEndpoint)? fallbackEp = null;
        foreach (var device in Usb.DeviceList?.Values ?? Enumerable.Empty<UsbDevice>())
        {
            if (vid is not null && (device.VendorId != vid || device.ProductId != pid))
                continue;
            for (var i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                if (intf is null)
                    continue;
                var outEp = FindBulkOut(intf);
                if (outEp is null)
                    continue;
                // Класс 7 — принтер. Без VID:PID берём только его; с VID:PID — любой bulk OUT.
                if (intf.InterfaceClass == UsbClass.Printer)
                {
                    endpoint = (intf, outEp);
                    return device;
                }
                if (vid is not null && fallback is null)
                {
                    fallback = device;
                    fallbackEp = (intf, outEp);
                }
            }
        }

        endpoint = fallbackEp;
        return fallback;
    }

    private static UsbEndpoint? FindBulkOut(UsbInterface intf)
    {
        for (var e = 0; e < intf.EndpointCount; e++)
        {
            var ep = intf.GetEndpoint(e);
            if (ep is not null && ep.Type == UsbAddressing.XferBulk && ep.Direction == UsbAddressing.Out)
                return ep;
        }
        return null;
    }

    private static void WriteUsb(string port, byte[] payload, TimeSpan timeout)
    {
        var device = FindUsbDevice(port, out var endpoint);
        if (device is null || endpoint is null)
            throw new IOException(Tr.T("USB-принтер не найден. Проверьте кабель или укажите USB:VID:PID.", "USB-принтер табылган жок. Кабелди текшериңиз же USB:VID:PID көрсөтүңүз.", "USB printer not found. Check the cable or specify USB:VID:PID.", "USB yazıcı bulunamadı. Kabloyu kontrol edin veya USB:VID:PID belirtin.", "USB-printer topilmadi. Kabelni tekshiring yoki USB:VID:PID kiriting."));

        EnsureUsbPermission(device);

        var (intf, outEp) = endpoint.Value;
        var connection = Usb.OpenDevice(device)
                         ?? throw new IOException(Tr.T("Не удалось открыть USB-принтер.", "USB-принтерди ачуу мүмкүн болбоду.", "Could not open the USB printer.", "USB yazıcı açılamadı.", "USB-printerni ochib bo'lmadi."));
        try
        {
            if (!connection.ClaimInterface(intf, true))
                throw new IOException(Tr.T("USB-принтер занят другой программой.", "USB-принтерди башка программа колдонуп жатат.", "The USB printer is used by another app.", "USB yazıcı başka bir uygulama tarafından kullanılıyor.", "USB-printer boshqa dastur tomonidan band."));
            var deadline = DateTime.UtcNow + timeout;
            var offset = 0;
            while (offset < payload.Length)
            {
                var chunk = Math.Min(UsbChunk, payload.Length - offset);
                var left = (int)Math.Max(1000, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var sent = connection.BulkTransfer(outEp, payload, offset, chunk, Math.Min(left, 10_000));
                if (sent <= 0)
                    throw new PrinterPortService.PrinterStalledException(Tr.T("USB-принтер не принимает данные — нет бумаги, открыта крышка или выключен.", "USB-принтер маалымат кабыл албай жатат — кагаз жок, капкагы ачык же өчүк.", "The USB printer does not accept data — out of paper, cover open or turned off.", "USB yazıcı veri kabul etmiyor — kağıt yok, kapak açık veya kapalı.", "USB-printer ma'lumot qabul qilmayapti — qog'oz yo'q, qopqoq ochiq yoki o'chiq."));
                offset += sent;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("USB: время отправки истекло.");
            }
        }
        finally
        {
            try { connection.ReleaseInterface(intf); } catch { /* устройство уже отключено */ }
            connection.Close();
        }
    }

    private static readonly object PermissionLock = new();
    private static ManualResetEventSlim? _permissionWait;

    private static void EnsureUsbPermission(UsbDevice device)
    {
        var usb = Usb;
        if (usb.HasPermission(device))
            return;

        lock (PermissionLock)
        {
            if (usb.HasPermission(device))
                return;
            var wait = new ManualResetEventSlim(false);
            _permissionWait = wait;
            var context = AndroidBootstrap.AppContext;
            var receiver = new PermissionReceiver();
            var filter = new IntentFilter(ActionUsbPermission);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
                context.RegisterReceiver(receiver, filter);
            try
            {
                var intent = new Intent(ActionUsbPermission).SetPackage(context.PackageName);
                var flags = OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0;
                var pending = PendingIntent.GetBroadcast(context, 0, intent, flags);
                usb.RequestPermission(device, pending);
                // Кассир отвечает на окно Android «Разрешить доступ к USB-устройству?».
                wait.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                try { context.UnregisterReceiver(receiver); } catch { /* уже снят */ }
                _permissionWait = null;
            }
        }

        if (!usb.HasPermission(device))
            throw new InvalidOperationException(Tr.T("Нет разрешения на USB-принтер. Нажмите «Разрешить» в окне Android и повторите печать.", "USB-принтерге уруксат жок. Android терезесинде «Уруксат берүү» басып, кайра басып чыгарыңыз.", "No permission for the USB printer. Tap “Allow” in the Android prompt and print again.", "USB yazıcı için izin yok. Android penceresinde “İzin ver”e dokunun ve yeniden yazdırın.", "USB-printerga ruxsat yo'q. Android oynasida «Ruxsat berish»ni bosing va qayta chop eting."));
    }

    /// <summary>Принтер подключили, пока касса открыта (Android прислал USB_DEVICE_ATTACHED):
    /// разрешение уже выдано — просто пишем в журнал.</summary>
    public static void OnDeviceAttached(Intent? intent)
    {
        if (intent?.Action != UsbManager.ActionUsbDeviceAttached)
            return;
        var device = intent.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
        if (device is not null)
            PosLogger.Log($"Android: подключено USB-устройство {device.VendorId:X4}:{device.ProductId:X4} {device.ProductName}.", "PRINTER");
    }

    private sealed class PermissionReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == ActionUsbPermission)
                _permissionWait?.Set();
        }
    }

    /// <summary>Принтеры для списка «найденные» в настройках кассы: USB-устройства с каналом записи
    /// и сопряжённые Bluetooth-устройства. Сетевой принтер вписывается вручную: TCP:адрес.</summary>
    public IReadOnlyList<DiscoveredPrinter> Discover()
    {
        var list = new List<DiscoveredPrinter>();
        try
        {
            foreach (var device in Usb.DeviceList?.Values ?? Enumerable.Empty<UsbDevice>())
            {
                var hasOut = false;
                var isPrinter = false;
                for (var i = 0; i < device.InterfaceCount; i++)
                {
                    if (device.GetInterface(i) is not { } intf || FindBulkOut(intf) is null)
                        continue;
                    hasOut = true;
                    isPrinter |= intf.InterfaceClass == UsbClass.Printer;
                }
                if (!hasOut)
                    continue;
                var name = $"{device.ManufacturerName} {device.ProductName}".Trim();
                var path = $"USB:{device.VendorId:X4}:{device.ProductId:X4}";
                list.Add(new DiscoveredPrinter(
                    Tr.T($"🔌 USB {name} ({path[4..]}){(isPrinter ? "" : " — не принтер?")}",
                        $"🔌 USB {name} ({path[4..]}){(isPrinter ? "" : " — принтер эмеспи?")}",
                        $"🔌 USB {name} ({path[4..]}){(isPrinter ? "" : " — not a printer?")}",
                        $"🔌 USB {name} ({path[4..]}){(isPrinter ? "" : " — yazıcı değil mi?")}",
                        $"🔌 USB {name} ({path[4..]}){(isPrinter ? "" : " — printer emasmi?")}"),
                    path));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: список USB-принтеров — {ex.Message}", "PRINTER");
        }

        try
        {
            var granted = !OperatingSystem.IsAndroidVersionAtLeast(31)
                          || AndroidBootstrap.AppContext.CheckSelfPermission(Manifest.Permission.BluetoothConnect) == Permission.Granted;
            if (granted && Adapter is { IsEnabled: true } adapter)
            {
                foreach (var device in adapter.BondedDevices ?? Enumerable.Empty<BluetoothDevice>())
                {
                    if (string.IsNullOrWhiteSpace(device.Address))
                        continue;
                    // 2026-10-05: встроенный принтер POS-терминала — первым в списке и с понятной подписью.
                    if (AndroidBuiltInPrinter.IsBuiltIn(device))
                        list.Insert(0, new DiscoveredPrinter(
                            Tr.T($"🧾 Встроенный принтер кассы ({device.Name})", $"🧾 Кассанын ичиндеги принтер ({device.Name})",
                                $"🧾 Built-in till printer ({device.Name})", $"🧾 Kasanın dahili yazıcısı ({device.Name})",
                                $"🧾 Kassaning ichki printeri ({device.Name})"),
                            "BT:" + device.Address));
                    else
                        list.Add(new DiscoveredPrinter($"📶 Bluetooth {device.Name} ({device.Address})", "BT:" + device.Address));
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: список Bluetooth-устройств — {ex.Message}", "PRINTER");
        }

        return list;
    }
}
