using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Portable;

/// <summary>2026-10-29, владелец: «адаптация в Linux плохая». Печать на Linux и macOS: у кассы не было ни одного способа
/// отправить чек — очередь Windows (winspool) и LPT/COM там не работают, а путь «/dev/usb/lp0» превращался в «/DEV/USB/LP0».
/// В поле «Порт принтера» теперь пишется:
///   /dev/usb/lp0, /dev/ttyUSB0, /dev/cu.usbserial-… — устройство (USB-принтер lp, переходник COM);
///   TCP:192.168.1.50 или 192.168.1.50:9100 — сетевой принтер (порт 9100 по умолчанию);
///   CUPS:ИмяОчереди — очередь CUPS (сырые данные: lp -d имя -o raw).
/// Подставляется при запуске (инициализатор модуля), Android подставляет свой транспорт позже.
/// НЕ ПРОВЕРЕНО на живом Linux/macOS с принтером — только сборка.</summary>
internal sealed class PosixPrinterTransport : IPlatformPrinterTransport
{
    private const int DefaultTcpPort = 9100;

    [ModuleInitializer]
    internal static void Register()
    {
        if (OperatingSystem.IsAndroid() || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;
        PrinterPortService.PlatformTransport ??= new PosixPrinterTransport();
    }

    public bool Handles(string port) => IsTcp(port) || IsCups(port) || HardwarePortHelper.IsUnixDevicePath(port);

    private static bool IsTcp(string port) =>
        port.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase) || port.StartsWith("NET:", StringComparison.OrdinalIgnoreCase)
        || port.StartsWith("IP:", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(port, @"^\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?$");

    private static bool IsCups(string port) => port.StartsWith("CUPS:", StringComparison.OrdinalIgnoreCase) || port.StartsWith("LP:", StringComparison.OrdinalIgnoreCase);

    private static bool IsSerial(string port) => HardwarePortHelper.LooksLikeComPort(port);

    public void Write(string port, byte[] payload, TimeSpan timeout)
    {
        if (IsTcp(port))
            WriteTcp(port, payload, timeout);
        else if (IsCups(port))
            WriteCups(port, payload, timeout);
        else if (IsSerial(port))
            WriteSerial(port, payload);
        else
            WriteDevice(port, payload);
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

            if (IsCups(port))
            {
                var name = CupsName(port);
                var (code, _) = RunTool("lpstat", new[] { "-p", name }, TimeSpan.FromSeconds(3));
                return code == 0
                    ? new(true, Tr.T($"● Доступен (CUPS {name})", $"● Жеткиликтүү (CUPS {name})", $"● Available (CUPS {name})", $"● Kullanılabilir (CUPS {name})", $"● Mavjud (CUPS {name})"), "cups")
                    : new(false, Tr.T($"○ Очередь CUPS «{name}» не найдена", $"○ CUPS кезеги «{name}» табылган жок", $"○ CUPS queue \"{name}\" not found", $"○ CUPS kuyruğu \"{name}\" bulunamadı", $"○ CUPS navbati \"{name}\" topilmadi"), "cups");
            }

            if (!File.Exists(port))
                return new(false, Tr.T("○ Устройство не найдено (проверьте кабель)", "○ Түзмөк табылган жок (кабелди текшериңиз)", "○ Device not found (check the cable)", "○ Cihaz bulunamadı (kabloyu kontrol edin)", "○ Qurilma topilmadi (kabelni tekshiring)"), "device");
            try
            {
                using var fs = new FileStream(port, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (UnauthorizedAccessException)
            {
                return new(false, Tr.T("○ Нет прав на устройство: добавьте пользователя в группу lp или dialout и перезайдите в систему", "○ Түзмөккө укук жок: колдонуучуну lp же dialout тобуна кошуп, кайра кириңиз", "○ No permission for the device: add the user to the lp or dialout group and log in again", "○ Cihaz için izin yok: kullanıcıyı lp veya dialout grubuna ekleyip yeniden giriş yapın", "○ Qurilmaga ruxsat yo'q: foydalanuvchini lp yoki dialout guruhiga qo'shib qayta kiring"), "device");
            }
            return new(true, Tr.T("● Доступен (устройство)", "● Жеткиликтүү (түзмөк)", "● Available (device)", "● Kullanılabilir (cihaz)", "● Mavjud (qurilma)"), "device");
        }
        catch (Exception ex)
        {
            return new(false, "○ " + ex.Message, "device");
        }
    }

    public IReadOnlyList<DiscoveredPrinter> Discover()
    {
        var result = new List<DiscoveredPrinter>();
        try
        {
            if (Directory.Exists("/dev/usb"))
                foreach (var path in Directory.GetFiles("/dev/usb", "lp*").OrderBy(x => x, StringComparer.Ordinal))
                    result.Add(new DiscoveredPrinter($"🔌 USB {path}", path));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer enumeration (/dev/usb) failed: {ex.GetType().Name}", "WARNING");
        }

        try
        {
            // lpstat -e — имена всех назначений CUPS по одному в строке (быстро, без опроса принтеров).
            var (code, output) = RunTool("lpstat", new[] { "-e" }, TimeSpan.FromSeconds(3));
            if (code == 0)
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(new DiscoveredPrinter($"🖨 {line} (CUPS)", "CUPS:" + line));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer enumeration (CUPS) failed: {ex.GetType().Name}", "WARNING");
        }

        return result;
    }

    // ---------------- запись ----------------

    private static (string Host, int Port) ParseTcp(string port)
    {
        var rest = port;
        var colon0 = rest.IndexOf(':');
        if (rest.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("NET:", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            rest = rest[(colon0 + 1)..];
        rest = rest.Trim().TrimStart('/');
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

    private static string CupsName(string port) => port[(port.IndexOf(':') + 1)..].Trim();

    private static void WriteCups(string port, byte[] payload, TimeSpan timeout)
    {
        var name = CupsName(port);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Не указана очередь CUPS (CUPS:имя).");
        var psi = new ProcessStartInfo("lp")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("raw");
        using var proc = Process.Start(psi) ?? throw new IOException("Не удалось запустить lp (CUPS).");
        proc.StandardInput.BaseStream.Write(payload, 0, payload.Length);
        proc.StandardInput.Close();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(true); } catch { /* уже завершился */ }
            throw new TimeoutException($"CUPS не принял задание для «{name}» за {timeout.TotalSeconds:0} с.");
        }
        if (proc.ExitCode != 0)
            throw new IOException($"CUPS «{name}»: {proc.StandardError.ReadToEnd().Trim()}");
    }

    private static void WriteSerial(string port, byte[] payload)
    {
        using var serial = new SerialPort(port, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true,
        };
        serial.Open();
        serial.Write(payload, 0, payload.Length);
        serial.BaseStream.Flush();
        Thread.Sleep(50);
    }

    private static void WriteDevice(string port, byte[] payload)
    {
        try
        {
            using var stream = new FileStream(port, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
            Thread.Sleep(50);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Нет прав на {port}: добавьте пользователя в группу lp (или dialout) и перезайдите в систему.", ex);
        }
    }

    private static (int Code, string Output) RunTool(string exe, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, "");
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { /* уже завершился */ }
                return (-1, output);
            }
            return (proc.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, ""); // нет CUPS (lp/lpstat не установлены)
        }
    }
}
