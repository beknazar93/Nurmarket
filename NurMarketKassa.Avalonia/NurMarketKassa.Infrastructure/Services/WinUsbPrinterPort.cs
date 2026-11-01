using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NurMarketKassa.Services;

/// <summary>
/// Термопринтеры, драйвер которых заменён на WinUSB через Zadig (обычно потому, что Windows
/// вообще не опознаёт устройство как принтер/COM-порт — см. <see cref="UsbRawPrinterPort"/>
/// для случая, когда порт всё же появляется в системе). Такие устройства видны только
/// напрямую по VID/PID, поэтому у них свой путь обнаружения и записи, отдельный от
/// <see cref="PrinterPortService"/>.
///
/// Реализовано через прямые вызовы winusb.dll/setupapi.dll (без сторонних библиотек):
/// более ранняя версия использовала LibUsbDotNet, чей класс UsbContext падал с
/// NullReferenceException в собственном Finalize() на потоке финализатора (неперехватываемо,
/// валило весь процесс) — подтверждено дважды в реальных логах кассы, включая попытку явного
/// GC.SuppressFinalize сразу после создания, которая тоже не помогла. SafeHandle из .NET,
/// используемый здесь, — проверенный временем механизм именно для такого сценария: его
/// ReleaseHandle() гарантированно безопасен даже при вызове из финализатора.
/// </summary>
public static class WinUsbPrinterPort
{
    public const string SchemePrefix = "winusb://";

    // Стандартный GUID интерфейса WinUSB, который Zadig прописывает в реестр для каждого
    // устройства при установке драйвера (DeviceInterfaceGUIDs) — тот же, что использует libusb.
    private static readonly Guid WinUsbInterfaceGuid = new("88bae032-5a81-49f0-bc3d-a4ff138216d6");

    private static readonly Regex VidPidPattern = new(
        "vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Запасной вариант на случай, если запрос пайпов через WinUsb_QueryPipe не сработал
    // (например, устройство не сообщает дескрипторы) — почти все дешёвые термопринтеры
    // используют первую bulk OUT конечную точку.
    private static readonly byte[] FallbackEndpoints = [0x01, 0x02];

    private const int UsbdPipeTypeBulk = 2;
    private const byte UsbEndpointDirectionInMask = 0x80; // bit 7: 0 = OUT, 1 = IN

    public sealed record WinUsbDeviceInfo(int VendorId, int ProductId, string Label, string DevicePath);

    private sealed record NativeDevice(int VendorId, int ProductId, string NativePath,
        string? Service = null, string? DeviceClass = null, bool FromGenericGuid = false)
    {
        /// <summary>2026-11-01: экземпляр устройства (из пути Windows: usb#vid_..&pid_..#ЭКЗЕМПЛЯР#{guid}) — серийный номер или адрес порта.
        /// Одно физическое устройство под двумя GUID имеет один экземпляр; два принтера на одной плате — разные.</summary>
        public string Instance
        {
            get
            {
                var parts = NativePath.Split('#');
                return (parts.Length >= 3 ? parts[2] : NativePath).ToLowerInvariant();
            }
        }
    }

    /// <summary>Короткая метка экземпляра для адреса (FNV-1a, одинаковая при каждом запуске).</summary>
    private static string InstanceTag(string instance)
    {
        var h = 2166136261u;
        foreach (var c in instance)
            h = (h ^ c) * 16777619u;
        return h.ToString("X8");
    }

    /// <summary>Устройства без повторов (один экземпляр = одно устройство), по порядку экземпляров — для стабильной нумерации.</summary>
    private static List<NativeDevice> DistinctDevices(Func<NativeDevice, bool> filter) => EnumerateNativeDevices()
        .Where(filter)
        .GroupBy(d => d.Instance)
        .Select(g => g.OrderBy(d => d.FromGenericGuid).First())
        .OrderBy(d => d.VendorId).ThenBy(d => d.ProductId).ThenBy(d => d.Instance, StringComparer.Ordinal)
        .ToList();

    /// <summary>Перечисляет USB-устройства, доступные через WinUSB (после установки драйвера
    /// Zadig) — обычные Windows-принтеры сюда не попадают, у них свой драйвер.</summary>
    public static IReadOnlyList<WinUsbDeviceInfo> EnumerateDevices()
    {
        var result = new List<WinUsbDeviceInfo>();
        try
        {
            // 2026-11-01: в списке принтеров этикеток были камера и Bluetooth — общий список USB-устройств Windows
            // (GUID a5dcbf10…) содержит вообще все USB-устройства. Показываем только то, чем можно печатать.
            // Одно устройство под двумя GUID — одна строка; два принтера на одной плате (одинаковые VID/PID) — две строки:
            // первый с прежним адресом, остальные с меткой экземпляра (&INST_…), чтобы чековый и этикеточный принтер не склеивались.
            var number = new Dictionary<(int, int), int>();
            foreach (var device in DistinctDevices(LooksLikePrinterCandidate))
            {
                var key = (device.VendorId, device.ProductId);
                var n = number.GetValueOrDefault(key) + 1;
                number[key] = n;
                var label = $"VID_{device.VendorId:X4}&PID_{device.ProductId:X4}";
                var path = ToDevicePath(device.VendorId, device.ProductId);
                if (n > 1)
                {
                    label += $" #{n}";
                    path += "&INST_" + InstanceTag(device.Instance);
                }
                result.Add(new WinUsbDeviceInfo(device.VendorId, device.ProductId, label, path));
            }
        }
        catch (Exception ex)
        {
            // Штатно, если WinUSB не установлен ни для одного устройства (Zadig не применялся).
            PosLogger.Log($"WinUSB enumeration unavailable: {ex.GetType().Name}", "DEBUG");
        }

        return result;
    }

    // 2026-11-01: классы устройств Windows, которые точно не принтер (камера, Bluetooth, ввод, звук, диски, сеть, хабы).
    private static readonly string[] NonPrinterClasses =
        ["Bluetooth", "Camera", "Image", "MEDIA", "AudioEndpoint", "Mouse", "Keyboard", "HIDClass", "USB", "DiskDrive", "WPD", "Net", "SmartCardReader"];

    /// <summary>Можно ли показывать устройство в списке принтеров. Если свойства драйвера прочитать не удалось — показываем (как раньше).
    /// Из общего списка USB-устройств берём только те, у которых драйвер winusb.sys (Zadig): иначе запись через WinUSB всё равно невозможна.</summary>
    private static bool LooksLikePrinterCandidate(NativeDevice d)
    {
        if (!string.IsNullOrEmpty(d.DeviceClass)
            && NonPrinterClasses.Any(c => string.Equals(c, d.DeviceClass, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (d.FromGenericGuid && !string.IsNullOrEmpty(d.Service)
            && !string.Equals(d.Service, "WinUSB", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool IsWinUsbDevicePath(string? devicePath) =>
        !string.IsNullOrWhiteSpace(devicePath) && devicePath.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);

    public static string ToDevicePath(int vendorId, int productId) =>
        $"{SchemePrefix}VID_{vendorId:X4}&PID_{productId:X4}";

    public static bool TryParseDevicePath(string devicePath, out int vendorId, out int productId) =>
        TryParseDevicePath(devicePath, out vendorId, out productId, out _);

    /// <summary><paramref name="instanceTag"/> — метка экземпляра (&amp;INST_…) для второго и следующих принтеров с теми же VID/PID; null — первый.</summary>
    public static bool TryParseDevicePath(string devicePath, out int vendorId, out int productId, out string? instanceTag)
    {
        vendorId = 0;
        productId = 0;
        instanceTag = null;
        if (!IsWinUsbDevicePath(devicePath))
            return false;

        var raw = devicePath[SchemePrefix.Length..];
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out var vid))
                vendorId = vid;
            else if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out var pid))
                productId = pid;
            else if (part.StartsWith("INST_", StringComparison.OrdinalIgnoreCase))
                instanceTag = part[5..].ToUpperInvariant();
        }

        return vendorId != 0 && productId != 0;
    }

    /// <summary>Проверка доступности без отправки данных — используется в настройках принтера.</summary>
    public static bool Probe(int vendorId, int productId, string? instanceTag = null)
    {
        try
        {
            return FindDevice(vendorId, productId, instanceTag) is not null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WinUSB probe failed: {ex.GetType().Name}", "DEBUG");
            return false;
        }
    }

    /// <param name="transferTimeoutMs">Тайм-аут одной передачи (PIPE_TRANSFER_TIMEOUT). Без него
    /// WinUsb_WritePipe на принтере, который перестал забирать данные (кончилась бумага, открыта
    /// крышка), ждёт вечно — и касса больше не печатала до перезапуска (2026-09-26).</param>
    /// <summary>Устройство по VID/PID; при нескольких одинаковых — по метке экземпляра (нет метки — первое, как раньше).</summary>
    private static NativeDevice? FindDevice(int vendorId, int productId, string? instanceTag)
    {
        var list = DistinctDevices(d => d.VendorId == vendorId && d.ProductId == productId);
        if (string.IsNullOrEmpty(instanceTag))
            return list.FirstOrDefault();
        return list.FirstOrDefault(d => string.Equals(InstanceTag(d.Instance), instanceTag, StringComparison.OrdinalIgnoreCase));
    }

    public static void SendRawBytes(int vendorId, int productId, byte[] payload, uint transferTimeoutMs = 10_000, string? instanceTag = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
            throw new InvalidOperationException("Пустой буфер печати.");

        var device = FindDevice(vendorId, productId, instanceTag)
            ?? throw new InvalidOperationException(
                $"USB-устройство VID_{vendorId:X4}&PID_{productId:X4} не найдено. " +
                "Проверьте подключение и что для него установлен драйвер WinUSB (Zadig).");

        // 2026-09-07: пробовал убрать FILE_FLAG_OVERLAPPED (гипотеза, что синхронный вызов
        // WinUsb_WritePipe(overlapped=NULL) на overlapped-хендле сам по себе некорректен) —
        // ОШИБСЯ: без этого флага WinUsb_Initialize стал падать с ERROR_INVALID_HANDLE (код 6)
        // на реальном устройстве — драйвер WinUSB требует FILE_FLAG_OVERLAPPED именно на этапе
        // открытия хендла (это подтверждают и официальные примеры Microsoft), а вызов
        // WinUsb_WritePipe с overlapped=NULL на таком хендле — штатный, задокументированный
        // синхронный режим, а не баг. Флаг вернул. Реальная причина зависаний — запись в
        // застрявшую (STALL) конечную точку без сброса и без таймаута, обе причины устранены
        // ниже (WinUsb_ResetPipe + внешний таймаут в LptReceiptPrinterService).
        using var fileHandle = CreateFile(
            device.NativePath,
            GENERIC_WRITE | GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (fileHandle.IsInvalid)
            throw new InvalidOperationException(
                $"Не удалось открыть USB-устройство VID_{vendorId:X4}&PID_{productId:X4} " +
                $"(код ошибки {Marshal.GetLastWin32Error()}).");

        if (!WinUsb_Initialize(fileHandle, out var winUsbHandle))
            throw new InvalidOperationException(
                $"WinUsb_Initialize не удался для VID_{vendorId:X4}&PID_{productId:X4} " +
                $"(код ошибки {Marshal.GetLastWin32Error()}).");

        using (winUsbHandle)
        {
            var discovered = DiscoverBulkOutEndpoints(winUsbHandle);
            IReadOnlyList<byte> endpoints = discovered.Count == 0
                ? FallbackEndpoints
                : discovered.Concat(FallbackEndpoints.Except(discovered)).ToArray();

            Exception? lastError = null;
            foreach (var endpointId in endpoints)
            {
                // 2026-09-07: конечная точка часто застревает в состоянии STALL после
                // предыдущей неудачной/оборванной записи (в реальном логе — "код 31",
                // ERROR_GEN_FAILURE, классический симптом застрявшего bulk-эндпоинта) и
                // остаётся такой между запусками процесса, т.к. физическое устройство не
                // сбрасывается при простом переоткрытии хендла. WinUsb_ResetPipe снимает
                // STALL перед записью — без этого повторные попытки продолжали бы падать
                // с той же ошибкой независимо от FILE_FLAG_OVERLAPPED-фикса выше.
                WinUsb_ResetPipe(winUsbHandle, endpointId);
                var timeout = transferTimeoutMs;
                if (!WinUsb_SetPipePolicy(winUsbHandle, endpointId, PipeTransferTimeout, sizeof(uint), ref timeout))
                    PosLogger.Log($"WinUSB 0x{endpointId:X2}: тайм-аут канала не установлен (код {Marshal.GetLastWin32Error()}).", "PRINTER");

                if (TryWriteOnce(winUsbHandle, endpointId, payload, out var written, out var writeErr))
                {
                    if (written != payload.Length)
                    {
                        lastError = new InvalidOperationException(
                            $"Передано {written} из {payload.Length} байт через 0x{endpointId:X2}.");
                        continue;
                    }

                    PosLogger.Log(
                        $"WinUSB VID_{vendorId:X4}&PID_{productId:X4} (0x{endpointId:X2}): отправлено {written} байт",
                        "PRINTER");
                    return;
                }

                PosLogger.Log($"WinUSB write via 0x{endpointId:X2} failed: код {writeErr}", "PRINTER");

                // Тайм-аут — принтер жив, но не забирает данные. Другая конечная точка и повтор тут не
                // помогут, а только растянут ожидание кассира.
                if (writeErr == ErrorSemTimeout)
                    throw new PrinterPortService.PrinterStalledException(
                        $"Принтер VID_{vendorId:X4}&PID_{productId:X4} не принял данные за {transferTimeoutMs / 1000} с — нет бумаги, открыта крышка или завис.");

                // Один раз пробуем снять STALL и повторить запись на ТОЙ ЖЕ точке, прежде
                // чем переходить к следующей — это именно то, что реально помогает при
                // ERROR_GEN_FAILURE, в отличие от простого перебора конечных точек.
                if (WinUsb_ResetPipe(winUsbHandle, endpointId) &&
                    TryWriteOnce(winUsbHandle, endpointId, payload, out written, out writeErr) &&
                    written == payload.Length)
                {
                    PosLogger.Log(
                        $"WinUSB VID_{vendorId:X4}&PID_{productId:X4} (0x{endpointId:X2}): отправлено {written} байт после сброса точки",
                        "PRINTER");
                    return;
                }

                lastError = new InvalidOperationException($"WinUsb_WritePipe(0x{endpointId:X2}) ошибка {writeErr}.");
            }

            throw new InvalidOperationException(
                $"Не удалось записать данные на VID_{vendorId:X4}&PID_{productId:X4} ни через одну конечную точку.",
                lastError);
        }
    }

    private static bool TryWriteOnce(SafeWinUsbHandle winUsbHandle, byte endpointId, byte[] payload, out uint written, out int win32Error)
    {
        var ok = WinUsb_WritePipe(winUsbHandle, endpointId, payload, (uint)payload.Length, out written, IntPtr.Zero);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>Спрашивает у самого устройства реальные bulk OUT конечные точки через
    /// WinUsb_QueryInterfaceSettings/WinUsb_QueryPipe, вместо угадывания 0x01/0x02 — на
    /// части термопринтеров реальный endpoint отличается, и печать через него не проходит.</summary>
    private static List<byte> DiscoverBulkOutEndpoints(SafeWinUsbHandle winUsbHandle)
    {
        var result = new List<byte>();
        try
        {
            if (!WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var ifaceDescriptor))
                return result;

            for (byte i = 0; i < ifaceDescriptor.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(winUsbHandle, 0, i, out var pipe))
                    continue;

                var isOut = (pipe.PipeId & UsbEndpointDirectionInMask) == 0;
                if (isOut && pipe.PipeType == UsbdPipeTypeBulk)
                    result.Add(pipe.PipeId);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WinUSB pipe query failed: {ex.GetType().Name}", "DEBUG");
        }

        return result;
    }

    /// <summary>
    /// Стандартный GUID интерфейса "любое USB-устройство" — регистрируется самим USB-стеком
    /// (не конкретным драйвером-функцией) для КАЖДОГО подключённого устройства независимо от
    /// того, какой драйвер на него посажен. Нужен как запасной вариант к
    /// <see cref="WinUsbInterfaceGuid"/>, потому что Zadig по умолчанию генерирует СВОЙ,
    /// случайный GUID для каждой конкретной установки драйвера (если явно не задать его вручную
    /// в поле "Device GUID" перед установкой) — то есть найденное на практике устройство почти
    /// никогда не совпадает с фиксированным `88bae032-...` ниже, из-за чего касса "не видела"
    /// реально подключённый и корректно настроенный через Zadig принтер. Путь, найденный через
    /// этот GUID, всё равно открывается и опрашивается через WinUsb_Initialize/WinUsb_WritePipe
    /// как обычно — это работает, пока драйвером устройства реально является winusb.sys
    /// (что Zadig и ставит при выборе "WinUSB"), независимо от того, под каким GUID
    /// зарегистрирован сам интерфейс.
    /// </summary>
    private static readonly Guid GenericUsbDeviceInterfaceGuid = new("a5dcbf10-6530-11d2-901f-00c04fb951ed");

    private static IEnumerable<NativeDevice> EnumerateNativeDevices()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in new[] { WinUsbInterfaceGuid, GenericUsbDeviceInterfaceGuid })
        {
            foreach (var device in EnumerateNativeDevicesForGuid(guid))
            {
                if (seenPaths.Add(device.NativePath))
                    yield return device;
            }
        }
    }

    private static IEnumerable<NativeDevice> EnumerateNativeDevicesForGuid(Guid guid)
    {
        var deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            yield break;

        try
        {
            uint index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                    yield break; // ERROR_NO_MORE_ITEMS — устройств больше нет.

                index++;

                var path = TryGetDevicePath(deviceInfoSet, ref interfaceData, out var devInfo);
                if (path is null)
                    continue;

                var match = VidPidPattern.Match(path);
                if (!match.Success)
                    continue;
                if (!int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var vid))
                    continue;
                if (!int.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out var pid))
                    continue;

                // 2026-11-01: драйвер (Service) и класс устройства — чтобы не показывать камеру/Bluetooth как принтер.
                var service = devInfo.cbSize != 0 ? TryReadRegistryText(deviceInfoSet, ref devInfo, SPDRP_SERVICE) : null;
                var deviceClass = devInfo.cbSize != 0 ? TryReadRegistryText(deviceInfoSet, ref devInfo, SPDRP_CLASS) : null;
                yield return new NativeDevice(vid, pid, path, service, deviceClass, guid == GenericUsbDeviceInterfaceGuid);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? TryGetDevicePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, out SP_DEVINFO_DATA devInfo)
    {
        devInfo = default;
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            // cbSize первого поля SP_DEVICE_INTERFACE_DETAIL_DATA — фиксированное платформенное
            // значение (не sizeof всей структуры с переменной строкой). Проект собирается только
            // под win-x64, поэтому используем именно это значение.
            Marshal.WriteInt32(buffer, 8);
            // 2026-11-01: заодно просим у Windows описание устройства (SP_DEVINFO_DATA) — по нему читаем драйвер и класс.
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (SetupDiGetDeviceInterfaceDetailWithInfo(deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref info))
                devInfo = info;
            else if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, IntPtr.Zero))
                return null;

            var pathPtr = buffer + 4;
            return Marshal.PtrToStringAuto(pathPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- SetupAPI ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    private const uint SPDRP_SERVICE = 0x4;
    private const uint SPDRP_CLASS = 0x7;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "SetupDiGetDeviceInterfaceDetail")]
    private static extern bool SetupDiGetDeviceInterfaceDetailWithInfo(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    /// <summary>Текстовое свойство устройства из реестра Windows (драйвер, класс); null — не удалось прочитать.</summary>
    private static string? TryReadRegistryText(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfo, uint property)
    {
        try
        {
            var buffer = new byte[512];
            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfo, property, out _, buffer, (uint)buffer.Length, out var size) || size < 2)
                return null;
            var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd((char)0);
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---- Kernel32 ----

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // ---- WinUsb ----

    private sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeWinUsbHandle() : base(true) { }
        protected override bool ReleaseHandle() => WinUsb_Free(handle);
    }

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out SafeWinUsbHandle interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(
        SafeWinUsbHandle interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
        out uint lengthTransferred, IntPtr overlapped);

    /// <summary>Снимает состояние STALL с конечной точки (ERROR_GEN_FAILURE/код 31 в логе —
    /// типичный симптом застрявшего bulk-эндпоинта после оборванной записи).</summary>
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ResetPipe(SafeWinUsbHandle interfaceHandle, byte pipeId);

    private const uint PipeTransferTimeout = 0x03; // PIPE_TRANSFER_TIMEOUT, мс
    private const int ErrorSemTimeout = 121;       // ERROR_SEM_TIMEOUT — передача не уложилась в тайм-аут

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(
        SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(
        SafeWinUsbHandle interfaceHandle, byte altSettingIndex, out USB_INTERFACE_DESCRIPTOR usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(
        SafeWinUsbHandle interfaceHandle, byte altInterfaceNumber, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipeInformation);
}
