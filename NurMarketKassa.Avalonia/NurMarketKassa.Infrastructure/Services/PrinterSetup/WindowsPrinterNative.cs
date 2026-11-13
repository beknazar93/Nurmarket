using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NurMarketKassa.Services.PrinterSetup;

/// <summary>2026-11-13 (мастер «Подключить принтер», диагностика): ТОЛЬКО ЧТЕНИЕ сведений Windows о принтерах — очереди (EnumPrinters, уровень 2:
/// имя, порт, драйвер, состояние, число заданий — прав администратора не нужно), USB-принтеры «Поддержка USB принтера» (SetupAPI, интерфейс
/// GUID_DEVINTERFACE_USBPRINT {28d78fad-5a12-11d1-ae5b-0000f803a8c2}: путь интерфейса, VID/PID, код USBPRINT\Производитель+Модель дочернего устройства,
/// порт USB00x из «Device Parameters»). Ничего не меняет; единственное изменение — удаление СВОИХ заданий из очереди по кнопке «Очистить очередь».</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPrinterNative
{
    public static readonly Guid UsbPrintInterface = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    // ------------------------------------------------------------------ очереди

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobs(IntPtr hPrinter, uint firstJob, uint noJobs, uint level, IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "SetJobW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetJob(IntPtr hPrinter, uint jobId, uint level, IntPtr pJob, uint command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode, pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; // SYSTEMTIME — не нужен, только для размера
    }

    private const uint PrinterEnumLocal = 0x2, PrinterEnumConnections = 0x4;
    private const uint JobControlDelete = 5;

    public static IReadOnlyList<WindowsQueueInfo> ListQueues()
    {
        const uint flags = PrinterEnumLocal | PrinterEnumConnections;
        EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return Array.Empty<WindowsQueueInfo>();
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, 2, buffer, needed, out _, out var returned))
                return Array.Empty<WindowsQueueInfo>();
            var size = Marshal.SizeOf<PRINTER_INFO_2>();
            var list = new List<WindowsQueueInfo>((int)returned);
            for (var i = 0; i < returned; i++)
            {
                var p = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer + i * size);
                var name = Marshal.PtrToStringUni(p.pPrinterName) ?? "";
                if (name.Length == 0)
                    continue;
                list.Add(new WindowsQueueInfo(name, Marshal.PtrToStringUni(p.pPortName) ?? "", Marshal.PtrToStringUni(p.pDriverName) ?? "", p.Status, p.Attributes, (int)p.cJobs));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Удаляет задания очереди, которые разрешено удалить без прав администратора (свои задания кассы). Возвращает, сколько заданий осталось.</summary>
    public static int DeleteOwnJobs(string queueName, out int deleted)
    {
        deleted = 0;
        if (!OpenPrinter(queueName, out var h, IntPtr.Zero))
            return -1;
        try
        {
            EnumJobs(h, 0, 256, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0)
                return 0;
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumJobs(h, 0, 256, 1, buffer, needed, out _, out var returned))
                    return -1;
                var size = Marshal.SizeOf<JOB_INFO_1>();
                var left = 0;
                for (var i = 0; i < returned; i++)
                {
                    var job = Marshal.PtrToStructure<JOB_INFO_1>(buffer + i * size);
                    if (SetJob(h, job.JobId, 0, IntPtr.Zero, JobControlDelete))
                        deleted++;
                    else
                        left++;
                }
                return left;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(h);
        }
    }

    // ------------------------------------------------------------------ USB-принтеры (SetupAPI)

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr detail, uint detailSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint devInst, char[] buffer, int bufferLen, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static IReadOnlyList<UsbPrinterDevice> ListUsbPrinters()
    {
        var guid = UsbPrintInterface;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle || set == IntPtr.Zero)
            return Array.Empty<UsbPrinterDevice>();
        var list = new List<UsbPrinterDevice>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface))
                    break;
                var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, ref info);
                if (required == 0)
                    continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize = 8 (x64) / 6 (x86), затем строка пути.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, ref info))
                        continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0)
                        continue;
                    PrinterUsbCatalog.TryParseVidPid(path, out var vid, out var pid);
                    var child = ChildDeviceId(info.DevInst);
                    var parsed = PrinterUsbCatalog.ParseUsbPrintId(child);
                    list.Add(new UsbPrinterDevice(path, vid, pid, parsed?.Manufacturer, parsed?.Model, ReadPortName(path)));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static string? ChildDeviceId(uint devInst)
    {
        try
        {
            if (CM_Get_Child(out var child, devInst, 0) != 0)
                return null;
            var buf = new char[512];
            return CM_Get_Device_ID(child, buf, buf.Length, 0) == 0 ? new string(buf).TrimEnd('\0') : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer setup: USB child id not read: {ex.GetType().Name}", "DEBUG");
            return null;
        }
    }

    /// <summary>Порт «USB001» интерфейса: HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}\##?#…\#\Device Parameters («Base Name» + «Port Number»).</summary>
    private static string? ReadPortName(string interfacePath)
    {
        try
        {
            var keyName = UsbInterfaceRegistryKey(interfacePath);
            if (keyName is null)
                return null;
            using var key = Registry.LocalMachine.OpenSubKey(keyName);
            if (key is null)
                return null;
            var number = key.GetValue("Port Number");
            var baseName = key.GetValue("Base Name") as string;
            return PortNameFrom(baseName, number);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer setup: USB port number not read: {ex.GetType().Name}", "DEBUG");
            return null;
        }
    }

    /// <summary>Ключ реестра «Device Parameters» интерфейса по его пути «\\?\usb#…#{guid}»; null — путь не того вида.</summary>
    internal static string? UsbInterfaceRegistryKey(string interfacePath)
    {
        if (!interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return null;
        return $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{{{UsbPrintInterface}}}\##?#{interfacePath[4..]}\#\Device Parameters";
    }

    /// <summary>«USB» + 1 → «USB001».</summary>
    internal static string? PortNameFrom(string? baseName, object? number)
    {
        if (number is not int n || n <= 0)
            return null;
        return $"{(string.IsNullOrWhiteSpace(baseName) ? "USB" : baseName.Trim())}{n:000}";
    }

    // ------------------------------------------------------------------ прямое открытие USB-интерфейса (для запроса состояния)

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileFlagOverlapped = 0x40000000;

    /// <summary>Открыть USB-принтер на чтение и запись (асинхронно — чтобы ожидание ответа можно было прервать по таймауту). null — не открылся (занят, вынут).</summary>
    public static FileStream? OpenUsbInterface(string interfacePath)
    {
        var handle = CreateFile(interfacePath, GenericRead | GenericWrite, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            PosLogger.Log($"Printer setup: USB interface not opened (Win32 {Marshal.GetLastWin32Error()}).", "PRINTER");
            handle.Dispose();
            return null;
        }
        return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
    }
}
