using System.Runtime.InteropServices;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Итог подключения к сетевой папке весов.</summary>
public enum ShtrikhPcNetStatus
{
    Ok,
    /// <summary>Ошибки Windows 53/67 (путь/имя сети не найдены) и тайм-аут: весы выключены, кабель, IP.</summary>
    NoConnection,
    /// <summary>86 / 1326: неверный логин или пароль Windows весов.</summary>
    BadCredentials,
    /// <summary>5: нет прав.</summary>
    AccessDenied,
    /// <summary>Сетевой (UNC) путь на системе без Windows.</summary>
    Unsupported,
    Failed,
}

/// <summary>Подключение сетевой папки с логином и паролем. Обёртка над WNetAddConnection2 — в тестах подставляется поддельная.</summary>
public interface IShtrikhPcShareConnector
{
    /// <summary>Подключает <paramref name="share"/> (\\сервер\ресурс) под пользователем. Возвращает код ошибки Windows (0 — успех).</summary>
    int Connect(string share, string user, string password);

    /// <summary>Отключает подключение (код ошибки Windows, 0 — успех или подключения не было).</summary>
    int Disconnect(string share);
}

/// <summary>2026-11-06: настоящее подключение — mpr.dll (WNetAddConnection2 / WNetCancelConnection2). Вызывается только в Windows; в Linux/Android класс
/// не используется (ShtrikhPcShare.Open возвращает «Unsupported» до вызова).</summary>
public sealed class WindowsShareConnector : IShtrikhPcShareConnector
{
    private const int ResourceTypeDisk = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    public int Connect(string share, string user, string password)
    {
        if (!OperatingSystem.IsWindows())
            return 50; // ERROR_NOT_SUPPORTED
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };
        return WNetAddConnection2(ref resource, password, user, 0);
    }

    // 2026-11-06 (аудит Этапа 2, Н-1): force=false — подключение, которым кто-то пользуется (диск в Проводнике, запись из программы владельца), не рвём насильно.
    public int Disconnect(string share) =>
        OperatingSystem.IsWindows() ? WNetCancelConnection2(share, 0, false) : 0;
}

/// <summary>2026-11-06: доступ к папке обмена весов РС-200 по сети с логином и паролем Windows весов (у весов на Windows 7 папка Shared без пароля не открывается).
/// Перед записью в UNC-путь — подключение, после — отключение. Код ошибки 1219 (другие учётные данные уже подключены к этому серверу) — сначала отключаем и
/// повторяем. Не дольше 3 с на подключение. Пароль в журнал не пишется.</summary>
public static class ShtrikhPcShare
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorBadNetPath = 53;
    public const int ErrorBadNetName = 67;
    public const int ErrorInvalidPassword = 86;
    public const int ErrorSessionCredentialConflict = 1219;
    public const int ErrorLogonFailure = 1326;

    /// <summary>Подключение (подставляется в тестах).</summary>
    public static IShtrikhPcShareConnector Connector { get; set; } = new WindowsShareConnector();

    public static bool IsUnc(string? path) => (path ?? "").Trim().StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>«\\192.168.0.200\Shared\Import» → «\\192.168.0.200\Shared».</summary>
    public static string ShareRoot(string uncPath)
    {
        var parts = uncPath.Trim().TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : uncPath.Trim();
    }

    /// <summary>Имя или адрес весов из UNC-пути («192.168.0.200»).</summary>
    public static string Host(string uncPath)
    {
        var parts = uncPath.Trim().TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : "";
    }

    /// <summary>Код ошибки Windows → понятная причина: 53/67 — нет связи, 86/1326 — неверный логин/пароль, 5 — нет прав.</summary>
    public static ShtrikhPcNetStatus Classify(int code) => code switch
    {
        0 => ShtrikhPcNetStatus.Ok,
        ErrorBadNetPath or ErrorBadNetName or 1231 or 1222 or 1203 => ShtrikhPcNetStatus.NoConnection,
        ErrorInvalidPassword or ErrorLogonFailure => ShtrikhPcNetStatus.BadCredentials,
        ErrorAccessDenied => ShtrikhPcNetStatus.AccessDenied,
        50 => ShtrikhPcNetStatus.Unsupported,
        _ => ShtrikhPcNetStatus.Failed,
    };

    /// <summary>Подключённая папка: <see cref="Dispose"/> отключает подключение, которое сделали мы.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly string? _share;

        internal Lease(string? share) => _share = share;

        public void Dispose()
        {
            if (_share is null)
                return;
            try
            {
                Connector.Disconnect(_share);
            }
            catch (Exception)
            {
                // отключение — уборка: сбой не должен ронять запись
            }
        }
    }

    /// <summary>Готовит доступ к папке: UNC без логина — как есть (под текущим пользователем); UNC с логином — подключение; не-UNC — как есть.
    /// Возвращает статус, код Windows и «аренду» (null — отключать нечего).</summary>
    public static async Task<(ShtrikhPcNetStatus Status, int Code, Lease? Lease)> OpenAsync(ShtrikhPcSettings settings, CancellationToken ct = default)
    {
        if (!IsUnc(settings.Folder))
            return (ShtrikhPcNetStatus.Ok, 0, null);
        if (!OperatingSystem.IsWindows() && Connector is WindowsShareConnector)
            return (ShtrikhPcNetStatus.Unsupported, 50, null);
        if (settings.Login.Length == 0)
            return (ShtrikhPcNetStatus.Ok, 0, null);

        var share = ShareRoot(settings.Folder);
        var connector = Connector;
        var work = Task.Run(() =>
        {
            var code = connector.Connect(share, settings.Login, settings.Password);
            if (code == ErrorSessionCredentialConflict)
            {
                // К этому серверу уже подключились под другим пользователем — Windows такого не допускает: отключаем и подключаем заново.
                connector.Disconnect(share);
                code = connector.Connect(share, settings.Login, settings.Password);
            }
            return code;
        }, CancellationToken.None);
        var finished = await Task.WhenAny(work, Task.Delay(ShtrikhPcImportWriter.AvailabilityTimeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != work)
        {
            // 2026-11-06 (Н-1): подключение могло удаться уже ПОСЛЕ ожидания — тогда оно «ничьё» и повисло бы с учёткой весов до выхода из Windows: отключаем (мягко).
            _ = work.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _ = t.Exception;
                    return;
                }
                if (t.Result != 0)
                    return;
                try { connector.Disconnect(share); }
                catch (Exception) { /* уборка: сбой не важен */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return (ShtrikhPcNetStatus.NoConnection, 0, null);
        }
        var result = await work.ConfigureAwait(false);
        return result == 0
            ? (ShtrikhPcNetStatus.Ok, 0, new Lease(share))
            : (Classify(result), result, null);
    }
}
