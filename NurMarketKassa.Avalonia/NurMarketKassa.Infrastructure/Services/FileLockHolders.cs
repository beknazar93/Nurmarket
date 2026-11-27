using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (жалоба клиента «Не удалось сменить компанию… Access to the path …\data is denied»): кто держит файлы открытыми.
/// Через Windows Restart Manager (rstrtmgr.dll) — тот же способ, которым установщики узнают «файл занят программой X». Только чтение, ничего не закрывает.
/// Не Windows или любая ошибка — пустой список (это подсказка для сообщения и журнала, а не условие работы).</summary>
public static class FileLockHolders
{
    /// <summary>Процесс, который держит файл: номер, имя процесса (без пути) и название программы из Restart Manager.</summary>
    public sealed record Holder(int ProcessId, string ProcessName, string AppName)
    {
        /// <summary>Это сама программа (тот же процесс).</summary>
        public bool IsSelf => ProcessId == Environment.ProcessId;

        /// <summary>Та же программа NurCRM, но другой процесс (у кассы — программа владельца, у владельца — касса).</summary>
        public bool IsSiblingNurCrm => !IsSelf && string.Equals(ProcessName, CurrentProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentProcessName
    {
        get
        {
            try { return Process.GetCurrentProcess().ProcessName; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>Не больше стольких файлов за один запрос (папка data — несколько файлов; защита от огромных папок фото).</summary>
    public const int MaxFiles = 300;

    /// <summary>Кто держит указанные файлы или файлы внутри указанных папок.</summary>
    public static IReadOnlyList<Holder> Find(IEnumerable<string> paths)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return [];
            var files = new List<string>();
            foreach (var path in paths)
            {
                if (files.Count >= MaxFiles)
                    break;
                if (File.Exists(path))
                    files.Add(path);
                else if (Directory.Exists(path))
                    files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(MaxFiles - files.Count));
            }
            return files.Count == 0 ? [] : Query(files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Кто держит файлы: не определено ({ex.Message}).", "WARNING");
            return [];
        }
    }

    private static IReadOnlyList<Holder> Query(string[] files)
    {
        var key = new StringBuilder(64);
        if (RmStartSession(out var session, 0, key) != 0)
            return [];
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
                return [];
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == 0 || needed == 0)
                return [];
            if (rc != ErrorMoreData)
                return [];
            var infos = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                return [];
            var result = new List<Holder>();
            for (var i = 0; i < count; i++)
            {
                var pid = infos[i].Process.ProcessId;
                string name;
                try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
                catch (Exception) { name = ""; }
                result.Add(new Holder(pid, name, infos[i].AppName ?? ""));
            }
            return result.GroupBy(h => h.ProcessId).Select(g => g.First()).ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] fileNames, uint applicationCount, IntPtr applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? infos, ref uint rebootReasons);
}
