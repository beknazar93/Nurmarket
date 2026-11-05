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

    // 2026-11-01: разбор адреса — общий для Windows/Linux/Android (NetworkPrinterAddress в Infrastructure).
    private static (string Host, int Port) ParseTcp(string port) => NetworkPrinterAddress.ParseOrThrow(port);

    // 2026-11-02 (финальная приёмка 1.17.73): запись по сети — общая с Windows (NetworkPrinterAddress.Write): подключение можно повторить, а обрыв
    // после начала записи — PrinterStalledException, SendRawBytes его не повторяет (иначе частичный чек и дубли).
    private static void WriteTcp(string port, byte[] payload, TimeSpan timeout) => NetworkPrinterAddress.Write(port, payload, timeout);

    private static string CupsName(string port) => port[(port.IndexOf(':') + 1)..].Trim();

    private static void WriteCups(string port, byte[] payload, TimeSpan timeout)
    {
        var name = CupsName(port);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(Tr.T("Не указана очередь CUPS (CUPS:имя).", "CUPS кезеги көрсөтүлгөн эмес (CUPS:аты).", "CUPS queue is not specified (CUPS:name).", "CUPS kuyruğu belirtilmedi (CUPS:ad).", "CUPS navbati ko'rsatilmagan (CUPS:nom)."));
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
        using var proc = Process.Start(psi) ?? throw new IOException(Tr.T("Не удалось запустить lp (CUPS).", "lp (CUPS) иштетүү мүмкүн болгон жок.", "Could not start lp (CUPS).", "lp (CUPS) başlatılamadı.", "lp (CUPS) ishga tushmadi."));
        proc.StandardInput.BaseStream.Write(payload, 0, payload.Length);
        proc.StandardInput.Close();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(true); } catch { /* уже завершился */ }
            throw new TimeoutException(Tr.T($"CUPS не принял задание для «{name}» за {timeout.TotalSeconds:0} с.", $"CUPS «{name}» үчүн тапшырманы {timeout.TotalSeconds:0} сек ичинде кабыл алган жок.", $"CUPS did not accept the job for \"{name}\" within {timeout.TotalSeconds:0} s.", $"CUPS \"{name}\" için işi {timeout.TotalSeconds:0} sn içinde kabul etmedi.", $"CUPS \"{name}\" uchun vazifani {timeout.TotalSeconds:0} soniyada qabul qilmadi."));
        }
        if (proc.ExitCode != 0)
            throw new IOException($"CUPS «{name}»: {proc.StandardError.ReadToEnd().Trim()}"); // текст — от самого CUPS
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
        // 2026-11-05 (аудит №4, А4-26): обрыв ПОСЛЕ начала записи — часть чека уже напечатана; обычный IOException SendRawBytes повторил бы
        // (дубль). Поэтому — PrinterStalledException (не повторяется), как для сети и Bluetooth. Ошибка открытия порта — обычная (повтор безопасен).
        try
        {
            serial.Write(payload, 0, payload.Length);
            serial.BaseStream.Flush();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            throw new PrinterPortService.PrinterStalledException(InterruptedText(), ex);
        }
        Thread.Sleep(50);
    }

    private static string InterruptedText() => Tr.T(
        "Связь с принтером оборвалась. Проверьте, не напечатался ли чек, и только потом печатайте заново.",
        "Принтер менен байланыш үзүлдү. Чек басылганын текшерип, андан кийин кайра басыңыз.",
        "The connection to the printer was lost. Check whether the receipt printed before printing again.",
        "Yazıcı bağlantısı koptu. Fişin basılıp basılmadığını kontrol edin, sonra yeniden yazdırın.",
        "Printer bilan aloqa uzildi. Chek chop etilganini tekshiring, keyin qayta chop eting.");

    private static void WriteDevice(string port, byte[] payload)
    {
        try
        {
            using var stream = new FileStream(port, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            // 2026-11-05 (А4-26): обрыв после начала записи — не повторять (иначе часть чека печатается дважды).
            try
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (IOException ex)
            {
                throw new PrinterPortService.PrinterStalledException(InterruptedText(), ex);
            }
            Thread.Sleep(50);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(Tr.T($"Нет прав на {port}: добавьте пользователя в группу lp (или dialout) и перезайдите в систему.", $"{port} үчүн укук жок: колдонуучуну lp (же dialout) тобуна кошуп, кайра кириңиз.", $"No permission for {port}: add the user to the lp (or dialout) group and log in again.", $"{port} için izin yok: kullanıcıyı lp (veya dialout) grubuna ekleyip yeniden giriş yapın.", $"{port} uchun ruxsat yo'q: foydalanuvchini lp (yoki dialout) guruhiga qo'shib qayta kiring."), ex);
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
