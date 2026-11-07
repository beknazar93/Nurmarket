using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (сервер ceba67c, п. 14 ТЗ «10 из 10», признак checkout_branch): в тело pos/checkout кассa добавляет device_key (стабильный идентификатор установки,
/// 8–64 символа: латиница, цифры, «_», «-»; без персональных данных), device_name (имя кассы из настроек или имя ПК) и branch (id филиала из профиля — сервер берёт его, если у смены
/// и кассы филиала нет). Всё это — только когда сервер объявил признак: старый сервер лишних полей не получает.</summary>
public static class DeviceIdentity
{
    private static readonly Regex KeyRule = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);
    private static string? _key;
    private static readonly object Gate = new();

    /// <summary>Папка для теста/стенда; по умолчанию — AppPaths.Local\NurMarketKassa (общая для кассы и программы владельца, не стирается при смене компании).</summary>
    internal static string? DirOverride { get; set; }

    /// <summary>Имя кассы из настроек («Касса N»); не задано — имя ПК. Подменяется в тестах.</summary>
    internal static Func<string?> NameProvider { get; set; } = () => UserPreferences.Instance.PreferredCashboxName;

    /// <summary>Филиал из профиля (NurMarketApiClient.ActiveBranchId). Подменяется в тестах.</summary>
    internal static Func<string?> BranchProvider { get; set; } = () => ServerLoyalty.ApiProvider?.Invoke()?.ActiveBranchId;

    private static string FilePath => Path.Combine(DirOverride ?? AppPaths.KassaLocal, "device-key.txt");

    /// <summary>Стабильный ключ установки: создаётся один раз и хранится в файле; не читается/не пишется файл — ключ живёт до перезапуска.</summary>
    public static string Key
    {
        get
        {
            lock (Gate)
            {
                if (_key is not null)
                    return _key;
                try
                {
                    var path = FilePath;
                    if (File.Exists(path) && File.ReadAllText(path).Trim() is { } saved && KeyRule.IsMatch(saved))
                        return _key = saved;
                    var fresh = "k" + Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, fresh);
                    return _key = fresh;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    PosLogger.Log($"Ключ установки не сохранён в файл ({ex.GetType().Name}) — до перезапуска используется временный.", "WARNING");
                    return _key = "k" + Guid.NewGuid().ToString("N");
                }
            }
        }
    }

    /// <summary>Только для тестов: забыть прочитанный ключ.</summary>
    internal static void ResetForTests() { lock (Gate) _key = null; }

    /// <summary>Имя устройства для сервера: «Касса N» из настроек или имя ПК; до 64 символов, без управляющих.</summary>
    public static string Name
    {
        get
        {
            string? raw = null;
            try { raw = NameProvider(); } catch { /* настройки недоступны — имя ПК */ }
            if (string.IsNullOrWhiteSpace(raw))
                raw = DefaultName(Environment.MachineName, OperatingSystem.IsAndroid(), () => Key);
            var clean = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            return clean.Length > 64 ? clean[..64] : clean;
        }
    }

    /// <summary>2026-11-06 (аудит №5, А5-08): имя по умолчанию. На Android <c>Environment.MachineName</c> всегда «localhost» — все планшеты выглядели в отчётах сервера одинаково; берём «Android» и 4 знака ключа установки
    /// (стабильно для устройства, различает планшеты). На ПК — имя компьютера, как раньше.</summary>
    internal static string DefaultName(string? machineName, bool isAndroid, Func<string> key)
    {
        var name = (machineName ?? "").Trim();
        if (isAndroid || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            var tail = new string((key() ?? "").Where(char.IsLetterOrDigit).ToArray());
            return "Android " + (tail.Length > 4 ? tail[^4..] : tail);
        }
        return name;
    }

    /// <summary>Дописывает device_key, device_name и branch в тело pos/checkout — только при признаке checkout_branch. Уже заданные поля не трогает.</summary>
    public static void ApplyTo(JsonObject body)
    {
        if (!ServerCapabilities.Has(ServerCapabilities.CheckoutBranch))
            return;
        if (body["device_key"] is null)
            body["device_key"] = Key;
        if (body["device_name"] is null && Name is { Length: > 0 } name)
            body["device_name"] = name;
        string? branch = null;
        try { branch = BranchProvider(); } catch { /* профиль недоступен — без филиала */ }
        if (body["branch"] is null && !string.IsNullOrWhiteSpace(branch))
            body["branch"] = branch!.Trim();
    }
}
