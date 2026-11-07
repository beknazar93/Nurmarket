namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит №4, Л4-01/Л4-04): ЕДИНЫЙ источник папок данных кассы и программы владельца.
///
/// Раньше путь собирался вручную в сотне мест через <c>Environment.GetFolderPath(ApplicationData)</c>. Эта функция
/// на Windows НЕ читает переменную APPDATA (путь берётся у системы), поэтому стенды и тесты, подменявшие APPDATA,
/// на деле писали в НАСТОЯЩИЕ данные кассы (так стёрлась база товаров). Теперь все места берут <see cref="Roaming"/> и
/// <see cref="Local"/>:
/// 1) задана переменная <c>NURMARKET_DATA_ROOT</c> — всё лежит внутри неё (<c>Roaming</c> и <c>Local</c>), в настоящие
///    папки пользователя не пишется ничего. Так работают тесты и стенды;
/// 2) переменной нет, но адрес сервера (<c>DESKTOP_MARKET_API_URL</c>) не боевой — данные в своей подпапке
///    <c>%APPDATA%\NurMarketStands\{адрес}</c>, боевая папка кассы не трогается никогда;
/// 3) иначе — обычные папки системы (поведение прежнее).</summary>
public static class AppPaths
{
    public const string DataRootVariable = "NURMARKET_DATA_ROOT";

    /// <summary>Боевой сервер NurCRM. Любой другой адрес API считается стендом/заглушкой.</summary>
    public const string ProductionHost = "app.nurcrm.kg";

    /// <summary>Замена <c>GetFolderPath(ApplicationData)</c>: база папок «Roaming».</summary>
    public static string Roaming => Resolve(Environment.SpecialFolder.ApplicationData, "Roaming");

    /// <summary>Замена <c>GetFolderPath(LocalApplicationData)</c>: база папок «Local».</summary>
    public static string Local => Resolve(Environment.SpecialFolder.LocalApplicationData, "Local");

    // 2026-11-06: В15г — имена папок данных в одном месте вместо сорока строковых литералов «NurMarketKassa» по коду.
    // Значения НЕ менять: это технические идентификаторы (папки данных уже стоят у клиентов). Результат
    // Path.Combine(Local, "NurMarketKassa") и KassaLocal один и тот же (проверяет AppPathsTests).
    public const string KassaFolderName = "NurMarketKassa";
    public const string OwnerFolderName = "NurMarketOwner";

    /// <summary>Общая папка кассы в «Local» (%LOCALAPPDATA%\NurMarketKassa): настройки и журналы, общие для кассы и владельца.</summary>
    public static string KassaLocal => Path.Combine(Local, KassaFolderName);

    /// <summary>Общая папка кассы в «Roaming» (%APPDATA%\NurMarketKassa).</summary>
    public static string KassaRoaming => Path.Combine(Roaming, KassaFolderName);

    /// <summary>Папка из NURMARKET_DATA_ROOT (полный путь) или null.</summary>
    public static string? DataRootOverride
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DataRootVariable)?.Trim();
            if (string.IsNullOrEmpty(value))
                return null;
            try { return Path.GetFullPath(value); }
            catch { return null; }
        }
    }

    /// <summary>Адрес API из DESKTOP_MARKET_API_URL или, если переменной нет, из ApiBaseUrl файла appsettings.json рядом с программой (хост, строчными);
    /// null — задан боевой или не задан вовсе.
    /// 2026-11-06: В3 Л5-16 — раньше стенд узнавался только по переменной окружения: ApiBaseUrl из appsettings.json (не боевой адрес) писал в боевую папку данных кассы.</summary>
    public static string? NonProductionHost => NonProductionHostOf(EffectiveApiUrl);

    /// <summary>Итоговый адрес API для выбора папки данных: переменная DESKTOP_MARKET_API_URL важнее файла appsettings.json (так же, как в AppSettings.Load).</summary>
    internal static string? EffectiveApiUrl =>
        Environment.GetEnvironmentVariable("DESKTOP_MARKET_API_URL") is { } env && !string.IsNullOrWhiteSpace(env) ? env : FileApiUrl.Value;

    // Читается один раз и без журнала: PosLogger сам берёт папку из AppPaths, журнал здесь дал бы рекурсию.
    private static readonly Lazy<string?> FileApiUrl = new(ReadApiUrlFromAppSettings);

    private static string? ReadApiUrlFromAppSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            foreach (var property in doc.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "ApiBaseUrl", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return property.Value.GetString();
        }
        catch (Exception)
        {
            // файл не читается — как будто адреса в нём нет (боевой по умолчанию)
        }
        return null;
    }

    /// <summary>Чистая функция для <see cref="NonProductionHost"/> (проверяется тестами без правки переменных окружения).</summary>
    public static string? NonProductionHostOf(string? apiUrl)
    {
        var raw = apiUrl?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        var parsed = Uri.TryCreate(raw, UriKind.Absolute, out var uri);
        var host = parsed ? uri!.Authority : raw;
        host = host.Trim().ToLowerInvariant();
        // 2026-11-05 (ревью волн 0–3, п. 10): боевой адрес узнаём по имени узла без порта и с поддоменами nurcrm.kg
        // («https://app.nurcrm.kg:443», «www.app.nurcrm.kg») — иначе данные «пропадали» в чужой папке стенда.
        var bareHost = (parsed ? uri!.Host : host.Split(':')[0]).Trim().ToLowerInvariant();
        if (host.Length == 0 || host == ProductionHost || bareHost == ProductionHost || bareHost.EndsWith(".nurcrm.kg", StringComparison.Ordinal))
            return null;
        return host;
    }

    /// <summary>Адрес сервера для ключа данных: боевой — пусто (ключ «c:{id}» остаётся прежним), иначе хост стенда.</summary>
    public static string ServerKeyPrefix => NonProductionHost is { } host ? host + "|" : "";

    /// <summary>Ключ данных компании: адрес сервера (только для стенда) + «c:{id}». Единый метод для AccountDataIsolation, ServerAddons и CompanyInfoService
    /// (2026-11-05, ревью волн 0–3, п. 9: раньше два места собирали «c:{id}» без префикса стенда и на стенде никогда не совпадали с LastDataAccountKey).</summary>
    public static string CompanyKey(string? companyId, string? prefix = null)
    {
        var company = (companyId ?? "").Trim();
        return company.Length > 0 ? (prefix ?? ServerKeyPrefix) + "c:" + company : "";
    }

    private static string Resolve(Environment.SpecialFolder folder, string subFolder)
    {
        var root = DataRootOverride;
        if (root is not null)
            return Path.Combine(root, subFolder);
        var real = Environment.GetFolderPath(folder);
        if (NonProductionHost is { } host)
            return Path.Combine(real, "NurMarketStands", SafeName(host), subFolder);
        return real;
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Защита от «стенда без своей папки данных»: на Windows переменные APPDATA/LOCALAPPDATA процесса подменены,
    /// а NURMARKET_DATA_ROOT не задана. .NET такую подмену не видит и писал бы в настоящие данные кассы.
    /// Возвращает текст причины (ru) или null, если можно запускаться.</summary>
    public static string? CheckStandIsolation()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var problem = CheckStandIsolation(DataRootOverride,
            Environment.GetEnvironmentVariable("APPDATA"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (problem is null)
            return null;
        // 2026-11-05 (ревью волн 0–3, п. 11): у обычного пользователя APPDATA может отличаться от системной папки без всякого стенда (runas /env, смена «User Shell Folders»
        // без перезахода, короткие пути 8.3) — касса молча не открывалась. Отказываем только при явном признаке стенда (адрес сервера не боевой или задана NURMARKET_STAND);
        // иначе пишем в журнал и продолжаем.
        if (NonProductionHost is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NURMARKET_STAND")))
        {
            try { PosLogger.Log("Папки данных: " + problem + " Признаков стенда нет — запуск продолжен.", "WARNING"); }
            catch { /* журнал ещё недоступен — не мешаем запуску */ }
            return null;
        }
        return problem;
    }

    /// <summary>Чистая проверка (без чтения окружения) — для тестов.</summary>
    public static string? CheckStandIsolation(string? dataRoot, string? appDataVariable, string? realAppData, string? localVariable, string? realLocal)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
            return null;
        var variable = Differs("APPDATA", appDataVariable, realAppData) ?? Differs("LOCALAPPDATA", localVariable, realLocal);
        if (variable is null)
            return null;
        return $"Переменная окружения {variable} подменена, а {DataRootVariable} не задана: программа писала бы в настоящие данные кассы. " +
               $"Задайте {DataRootVariable} (отдельная папка стенда) или уберите подмену.";
    }

    private static string? Differs(string name, string? env, string? real)
    {
        if (string.IsNullOrWhiteSpace(env) || string.IsNullOrEmpty(real))
            return null;
        return string.Equals(Normalize(env), Normalize(real), StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.Trim().TrimEnd('\\', '/'); }
    }
}
