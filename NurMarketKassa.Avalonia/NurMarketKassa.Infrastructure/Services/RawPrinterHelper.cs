using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NurMarketKassa.Services;

public static class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    private const uint PrinterAccessAdminister = 0x00000004;
    private const uint PrinterAccessUse = 0x00000008;

    [DllImport("winspool.Drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(
        uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    private const uint PRINTER_ENUM_LOCAL = 0x00000002;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    /// <summary>Все принтеры, установленные в Windows (локальные + подключённые по сети), включая
    /// виртуальные вроде "Generic / Text Only" — многие чековые/этикеточные принтеры ставятся именно так.</summary>
    public static IReadOnlyList<string> GetInstalledPrinterNames()
    {
        const uint level = 4; // PRINTER_INFO_4: минимальный уровень, отдающий только имена — не требует прав администратора.
        const uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;

        EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
                return Array.Empty<string>();

            var names = new List<string>((int)returned);
            var size = Marshal.SizeOf<PRINTER_INFO_4>();
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(buffer + i * size);
                if (!string.IsNullOrWhiteSpace(info.pPrinterName))
                    names.Add(info.pPrinterName);
            }
            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_4
    {
        public string pPrinterName;
        public string pServerName;
        public uint Attributes;
    }

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    /// <summary>Возвращает номер задания в очереди (0 — ошибка).</summary>
    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO docInfo);

    [DllImport("winspool.Drv", EntryPoint = "GetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetJob(IntPtr hPrinter, uint jobId, uint level, IntPtr pJob, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.Drv", EntryPoint = "SetJobW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetJob(IntPtr hPrinter, uint jobId, uint level, IntPtr pJob, uint command);

    [DllImport("winspool.Drv", EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumJobs(IntPtr hPrinter, uint firstJob, uint noJobs, uint level, IntPtr pJob, uint cbBuf,
        out uint pcbNeeded, out uint pcReturned);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SYSTEMTIME Submitted; // UTC
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;

        public DateTime? ToUtc()
        {
            try
            {
                return new DateTime(Year, Month, Day, Hour, Minute, Second, Milliseconds, DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_5
    {
        public IntPtr pPrinterName;
        public IntPtr pPortName;
        public uint Attributes;
        public uint DeviceNotSelectedTimeout;
        public uint TransmissionRetryTimeout;
    }

    private const uint PrinterAttributeWorkOffline = 0x00000400;

    /// <summary>Если первое задание очереди стоит дольше этого, а наше за ним так и не ушло —
    /// принтер задания не забирает. Обычный чек уходит за доли секунды, длинный графический —
    /// за несколько секунд; 10 с с запасом.</summary>
    private static readonly TimeSpan StuckQueueHeadAge = TimeSpan.FromSeconds(10);

    private const uint JobControlDelete = 5;
    private const uint JobStatusPaused = 0x0001;
    private const uint JobStatusError = 0x0002;
    private const uint JobStatusOffline = 0x0020;
    private const uint JobStatusPaperOut = 0x0040;
    private const uint JobStatusPrinted = 0x0080;
    private const uint JobStatusBlockedDevq = 0x0200;
    private const uint JobStatusUserIntervention = 0x0400;
    private const uint JobStatusComplete = 0x1000;

    private const uint PrinterStatusError = 0x00000002;
    private const uint PrinterStatusPaperJam = 0x00000008;
    private const uint PrinterStatusPaperOut = 0x00000010;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusNotAvailable = 0x00001000;
    private const uint PrinterStatusUserIntervention = 0x00100000;
    private const uint PrinterStatusDoorOpen = 0x00400000;

    /// <summary>Имя задания keep-alive (PrinterKeepAliveService) — отличается от чека, чтобы в
    /// очереди Windows было видно, что это не чек.</summary>
    public const string KeepAliveDocumentName = "NurMarket keep-alive";

    public enum JobOutcome
    {
        /// <summary>Задание ушло на принтер (исчезло из очереди или помечено напечатанным).</summary>
        Printed,
        /// <summary>Ещё в очереди, но без признаков ошибки (очередь занята, большой чек печатается).</summary>
        Pending,
        /// <summary>Принтер не печатает (выключен, нет бумаги, ошибка) — задание снято из очереди.</summary>
        Stuck,
        /// <summary>Очередь не приняла задание.</summary>
        NotAccepted,
    }

    /// <summary>Сколько ждать, что принтер забрал чек из очереди, и сколько должна держаться ошибка,
    /// чтобы счесть принтер неработающим (кратковременные сбои очередь переживает сама).</summary>
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StuckGrace = TimeSpan.FromMilliseconds(1500);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.Drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.Drv", EntryPoint = "SetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint command);

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_6
    {
        public uint dwStatus;
    }

    private const uint PrinterControlResume = 2;
    private const uint PrinterStatusPaused = 0x00000001;
    private const uint PrinterStatusPowerSave = 0x01000000;

    /// <summary>2026-09-14, живой баг: встроенный (через очередь Windows) принтер время от
    /// времени "засыпает" (PRINTER_STATUS_POWER_SAVE) или сама очередь Windows встаёт на паузу
    /// (PRINTER_STATUS_PAUSED — Windows делает это сама после нескольких неудачных попыток
    /// достучаться до заснувшего устройства) — и StartDocPrinter/WritePrinter при этом всё равно
    /// молча возвращают успех: спулер просто принял задание в очередь, а печатать его физически
    /// некому/некогда. Кассир видит "чек напечатан", а бумага не выходит, пока кто-то вручную не
    /// откроет очередь принтера и не нажмёт "Продолжить". Снимаем паузу автоматически перед
    /// каждой печатью и даём заснувшему принтеру время проснуться, вместо того чтобы слепо
    /// доверять успешному коду возврата спулера.</summary>
    private static void WakeUpIfSleepingOrPaused(string printerName, IntPtr hPrinter)
    {
        var status = TryGetStatus(hPrinter);

        if (IsWorkOffline(hPrinter))
        {
            // «Работать автономно» Windows включает сама, когда принтер не ответил (уснул,
            // выключали), и НЕ выключает, когда он вернулся: задания копятся, пока кто-нибудь не
            // снимет галочку вручную. Проверено стендом: с этим режимом чеки стоят в очереди
            // «Normal», без единого признака ошибки; снятие режима — очередь печатается сразу.
            PosLogger.Log("Принтер: включён режим «Работать автономно» — выключаю перед печатью.", "PRINTER");
            if (!TryClearWorkOffline(printerName))
                PosLogger.Log("Принтер: выключить «Работать автономно» не удалось — нет прав. Снимите галочку в очереди принтера.", "PRINTER");
        }

        if (status == 0)
            return;

        if ((status & PrinterStatusPaused) != 0)
        {
            PosLogger.Log("Принтер: очередь Windows была на паузе — снимаю паузу перед печатью.", "PRINTER");
            // Снять паузу можно только с правами управления принтером: обычный дескриптор (права
            // «печать») тут молча не срабатывал. Пробуем свой, с правами администратора принтера.
            if (!SetPrinter(hPrinter, 0, IntPtr.Zero, PrinterControlResume) && !TryResumeAsAdministrator(printerName))
                PosLogger.Log("Принтер: снять паузу очереди не удалось — нет прав. Снимите паузу в «Принтеры и сканеры».", "PRINTER");
        }

        if ((status & PrinterStatusPowerSave) != 0)
        {
            PosLogger.Log("Принтер в режиме энергосбережения — жду пробуждения перед печатью.", "PRINTER");
            Thread.Sleep(800);
        }
    }

    private static uint TryGetStatus(IntPtr hPrinter)
    {
        try
        {
            GetPrinter(hPrinter, 6, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return 0;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetPrinter(hPrinter, 6, buffer, needed, out _))
                    return 0;

                return Marshal.PtrToStructure<PRINTER_INFO_6>(buffer).dwStatus;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Printer status check failed: {ex.GetType().Name}", "DEBUG");
            return 0;
        }
    }

    private static bool IsWorkOffline(IntPtr hPrinter)
    {
        GetPrinter(hPrinter, 5, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
            return false;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            return GetPrinter(hPrinter, 5, buffer, needed, out _)
                   && (Marshal.PtrToStructure<PRINTER_INFO_5>(buffer).Attributes & PrinterAttributeWorkOffline) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Снимает «Работать автономно» (атрибут очереди, меняется только через уровень 2 и
    /// только с правами управления принтером).</summary>
    private static bool TryClearWorkOffline(string printerName)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = PrinterAccessAdminister | PrinterAccessUse };
        if (!OpenPrinter(printerName, out var hAdmin, ref defaults))
            return false;

        try
        {
            GetPrinter(hAdmin, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return false;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetPrinter(hAdmin, 2, buffer, needed, out _))
                    return false;

                // PRINTER_INFO_2: 13 указателей (имена, DEVMODE, pSecurityDescriptor), затем Attributes.
                // Дескриптор безопасности обнуляем — иначе SetPrinter попробует переписать и права.
                var ptr = IntPtr.Size;
                Marshal.WriteIntPtr(buffer, 12 * ptr, IntPtr.Zero);
                var attributes = (uint)Marshal.ReadInt32(buffer, 13 * ptr);
                Marshal.WriteInt32(buffer, 13 * ptr, (int)(attributes & ~PrinterAttributeWorkOffline));
                return SetPrinter(hAdmin, 2, buffer, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(hAdmin);
        }
    }

    private static readonly TimeSpan OwnStaleJobAge = TimeSpan.FromSeconds(30);

    /// <summary>2026-09-26, фото из магазина: в очереди POS58 висел чек «Receipt» в состоянии
    /// «Печать, Ошибка». Такой чек уже не нужен — продажа проведена, кассиру сказали «не напечатан»
    /// (или его оставила старая версия кассы). Но он стоит первым, и очередь за ним не двигается:
    /// WaitForJob снимал каждый НОВЫЙ чек как застрявший, а старый оставался — печать не
    /// возвращалась даже после починки принтера. Перед новым чеком убираем свои старые задания
    /// (только наши имена документов — чужие не трогаем).</summary>
    private static void PurgeOwnStaleJobs(IntPtr hPrinter)
    {
        EnumJobs(hPrinter, 0, 64, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumJobs(hPrinter, 0, 64, 1, buffer, needed, out _, out var returned))
                return;

            var size = Marshal.SizeOf<JOB_INFO_1>();
            for (var i = 0; i < returned; i++)
            {
                var job = Marshal.PtrToStructure<JOB_INFO_1>(buffer + i * size);
                var document = job.pDocument == IntPtr.Zero ? null : Marshal.PtrToStringUni(job.pDocument);
                if (document is not ("Receipt" or KeepAliveDocumentName or "NurMarket drawer"))
                    continue;
                if (job.Submitted.ToUtc() is not { } submitted || DateTime.UtcNow - submitted < OwnStaleJobAge)
                    continue;

                var ok = SetJob(hPrinter, job.JobId, 0, IntPtr.Zero, JobControlDelete);
                PosLogger.Log(ok
                    ? $"Принтер: из очереди убран старый чек «{document}» (задание {job.JobId}, стоял {(DateTime.UtcNow - submitted).TotalMinutes:0} мин) — он держал очередь."
                    : $"Принтер: старый чек «{document}» (задание {job.JobId}) убрать не удалось (Win32 {Marshal.GetLastWin32Error()}).", "PRINTER");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Принтер: проверка старых заданий не удалась: {ex.Message}", "PRINTER");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Сколько стоит в очереди самое старое задание, кроме нашего (null — таких нет).</summary>
    private static TimeSpan? OldestOtherJobAge(IntPtr hPrinter, uint ourJobId)
    {
        EnumJobs(hPrinter, 0, 64, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumJobs(hPrinter, 0, 64, 1, buffer, needed, out _, out var returned))
                return null;

            TimeSpan? oldest = null;
            var size = Marshal.SizeOf<JOB_INFO_1>();
            for (var i = 0; i < returned; i++)
            {
                var job = Marshal.PtrToStructure<JOB_INFO_1>(buffer + i * size);
                if (job.JobId == ourJobId || job.Submitted.ToUtc() is not { } submitted)
                    continue;
                var age = DateTime.UtcNow - submitted;
                if (oldest is null || age > oldest)
                    oldest = age;
            }

            return oldest;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryResumeAsAdministrator(string printerName)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = PrinterAccessAdminister | PrinterAccessUse };
        if (!OpenPrinter(printerName, out var hAdmin, ref defaults))
            return false;

        try
        {
            return SetPrinter(hAdmin, 0, IntPtr.Zero, PrinterControlResume);
        }
        finally
        {
            ClosePrinter(hAdmin);
        }
    }

    /// <summary>Есть ли в очереди принтера задания (любые). Нужен keep-alive: в непустую очередь
    /// он своё задание не добавляет.</summary>
    public static bool HasQueuedJobs(string printerName)
    {
        if (!TryOpen(printerName, out var printer, out _))
            return false;

        using (printer)
        {
            // С пустым буфером EnumJobs сообщает нужный размер: 0 — заданий нет.
            EnumJobs(printer.DangerousGetHandle(), 0, 1, 1, IntPtr.Zero, 0, out var needed, out _);
            return needed > 0;
        }
    }

    /// <summary>2026-09-26: отправка чека в очередь Windows с проверкой, что принтер его забрал.
    /// Очередь принимает задание и тогда, когда принтер выключен, без бумаги или в ошибке, — и
    /// касса считала чек напечатанным (проверено стендом: «принтер выключен» — 6 чеков «ok»,
    /// все шесть стоят в очереди). Теперь ждём до <see cref="ConfirmWindow"/>: ушло на принтер —
    /// хорошо; очередь или принтер в ошибке дольше <see cref="StuckGrace"/> — снимаем своё задание
    /// (иначе чек выйдет сам через час, когда кассир уже распечатал его повторно) и сообщаем.</summary>
    public static JobOutcome SendAndConfirm(string printerName, byte[] bytes, out int win32Error, out string stuckReason)
    {
        stuckReason = "";
        if (!TryOpen(printerName, out var printer, out win32Error))
            return JobOutcome.NotAccepted;

        using (printer)
        {
            var hPrinter = printer.DangerousGetHandle();
            WakeUpIfSleepingOrPaused(printerName, hPrinter);
            PurgeOwnStaleJobs(hPrinter);

            var jobId = WriteDocument(hPrinter, bytes, "Receipt", out win32Error);
            if (jobId == 0)
                return JobOutcome.NotAccepted;

            return WaitForJob(hPrinter, (uint)jobId, out stuckReason);
        }
    }

    private static JobOutcome WaitForJob(IntPtr hPrinter, uint jobId, out string stuckReason)
    {
        stuckReason = "";
        var clock = Stopwatch.StartNew();
        TimeSpan? problemSince = null;

        while (clock.Elapsed < ConfirmWindow)
        {
            var jobStatus = TryGetJobStatus(hPrinter, jobId);
            if (jobStatus is null || (jobStatus.Value & (JobStatusPrinted | JobStatusComplete)) != 0)
                return JobOutcome.Printed;

            var problem = DescribeProblem(jobStatus.Value, TryGetStatus(hPrinter));
            if (problem is null)
            {
                problemSince = null;
            }
            else
            {
                problemSince ??= clock.Elapsed;
                if (clock.Elapsed - problemSince.Value >= StuckGrace)
                {
                    stuckReason = problem;
                    if (!SetJob(hPrinter, jobId, 0, IntPtr.Zero, JobControlDelete))
                        PosLogger.Log($"Принтер: снять задание {jobId} из очереди не удалось (Win32 {Marshal.GetLastWin32Error()}).", "PRINTER");
                    PosLogger.Log($"Принтер: задание {jobId} не печатается — {problem}; снято из очереди.", "PRINTER");
                    return JobOutcome.Stuck;
                }
            }

            Thread.Sleep(100);
        }

        // Своё задание не ушло, признаков ошибки нет. Если перед ним давно стоит другое — очередь
        // не движется (TCP-принтер выключен: Windows помечает ошибку лишь через минуту; «Работать
        // автономно» без прав снять его) — чек сам не выйдет, пока принтер не починят.
        var headAge = OldestOtherJobAge(hPrinter, jobId);
        var offline = IsWorkOffline(hPrinter);
        if (offline || headAge >= StuckQueueHeadAge)
        {
            stuckReason = offline
                ? "в очереди включён режим «Работать автономно»"
                : $"очередь не движется: предыдущее задание стоит {headAge!.Value.TotalSeconds:0} с (принтер выключен, нет бумаги или завис)";
            if (!SetJob(hPrinter, jobId, 0, IntPtr.Zero, JobControlDelete))
                PosLogger.Log($"Принтер: снять задание {jobId} из очереди не удалось (Win32 {Marshal.GetLastWin32Error()}).", "PRINTER");
            PosLogger.Log($"Принтер: задание {jobId} не печатается — {stuckReason}; снято из очереди.", "PRINTER");
            return JobOutcome.Stuck;
        }

        PosLogger.Log($"Принтер: задание {jobId} через {ConfirmWindow.TotalSeconds:0} с ещё в очереди Windows, ошибок нет — считаю отправленным.", "PRINTER");
        return JobOutcome.Pending;
    }

    /// <summary>Состояние задания; null — задания в очереди уже нет (ушло на принтер).</summary>
    private static uint? TryGetJobStatus(IntPtr hPrinter, uint jobId)
    {
        GetJob(hPrinter, jobId, 1, IntPtr.Zero, 0, out var needed);
        if (needed == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetJob(hPrinter, jobId, 1, buffer, needed, out _))
                return null;
            return Marshal.PtrToStructure<JOB_INFO_1>(buffer).Status;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? DescribeProblem(uint jobStatus, uint printerStatus)
    {
        if ((printerStatus & PrinterStatusPaperOut) != 0 || (jobStatus & JobStatusPaperOut) != 0)
            return "нет бумаги";
        if ((printerStatus & PrinterStatusPaperJam) != 0)
            return "замялась бумага";
        if ((printerStatus & PrinterStatusDoorOpen) != 0)
            return "открыта крышка";
        if ((printerStatus & (PrinterStatusOffline | PrinterStatusNotAvailable)) != 0 || (jobStatus & JobStatusOffline) != 0)
            return "принтер не в сети (выключен или отключён кабель)";
        if ((printerStatus & PrinterStatusPaused) != 0 || (jobStatus & JobStatusPaused) != 0)
            return "очередь Windows приостановлена";
        if ((printerStatus & PrinterStatusUserIntervention) != 0 || (jobStatus & JobStatusUserIntervention) != 0)
            return "принтер требует вмешательства";
        if ((printerStatus & PrinterStatusError) != 0 || (jobStatus & (JobStatusError | JobStatusBlockedDevq)) != 0)
            return "принтер в ошибке";
        return null;
    }

    internal static bool TryOpen(string printerName, out PrinterSafeHandle handle, out int win32Error)
    {
        win32Error = 0;
        handle = null!;

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        handle = new PrinterSafeHandle(hPrinter);
        return true;
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes, out int win32Error, string documentName = "Receipt")
    {
        win32Error = 0;
        if (!TryOpen(printerName, out var printer, out win32Error))
            return false;

        using (printer)
        {
            WakeUpIfSleepingOrPaused(printerName, printer.DangerousGetHandle());
            return WriteDocument(printer.DangerousGetHandle(), bytes, documentName, out win32Error) != 0;
        }
    }

    /// <summary>Одно RAW-задание в очередь. Возвращает номер задания (0 — ошибка).</summary>
    private static int WriteDocument(IntPtr hPrinter, byte[] bytes, string documentName, out int win32Error)
    {
        win32Error = 0;
        var docInfo = new DOC_INFO { pDocName = documentName, pDataType = "RAW" };
        var jobId = StartDocPrinter(hPrinter, 1, ref docInfo);
        if (jobId == 0)
        {
            win32Error = Marshal.GetLastWin32Error();
            return 0;
        }

        try
        {
            if (!StartPagePrinter(hPrinter))
            {
                win32Error = Marshal.GetLastWin32Error();
                return 0;
            }

            var unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                if (!WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out var written)
                    || written != bytes.Length)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedBytes);
            }

            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
            return jobId;
        }
        catch
        {
            try
            {
                EndDocPrinter(hPrinter);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Printer document cleanup failed: {ex.GetType().Name}", "WARNING");
            }
            throw;
        }
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes) =>
        SendBytesToPrinter(printerName, bytes, out _);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDataType;
    }

    internal sealed class PrinterSafeHandle : SafeHandle
    {
        public PrinterSafeHandle(IntPtr handle) : base(handle, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }
}
