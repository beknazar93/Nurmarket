using System.Text.Json;

namespace NurSupportBot;

/// <summary>Настройки бота. Читаются из appsettings.json рядом с программой; любое поле можно
/// переопределить переменной окружения (NURBOT_TOKEN, NURBOT_ADMINS, NURBOT_DATA_DIR, NURBOT_NURCRM_URL).
/// Токен бота в репозиторий не попадает: appsettings.json в .gitignore, образец — appsettings.example.json.</summary>
public sealed class BotConfig
{
    /// <summary>Токен от @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Telegram ID администраторов: управляют инструкциями, видят статистику и обращения.</summary>
    public List<long> AdminIds { get; set; } = new();

    /// <summary>Папка для базы и ключа шифрования.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Адрес Bot API. Меняется только для проверки бота на поддельном сервере.</summary>
    public string TelegramApiUrl { get; set; } = "https://api.telegram.org/";

    /// <summary>Сервер NurCRM — вход клиента и финансовый отчёт.</summary>
    public string NurCrmBaseUrl { get; set; } = "https://app.nurcrm.kg/";

    public static BotConfig Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "appsettings.json");
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new BotConfig()
            : new BotConfig();

        if (Environment.GetEnvironmentVariable("NURBOT_TOKEN") is { Length: > 0 } token)
            config.BotToken = token;
        if (Environment.GetEnvironmentVariable("NURBOT_ADMINS") is { Length: > 0 } admins)
            config.AdminIds = admins.Split(',', ';', ' ').Where(s => long.TryParse(s, out _)).Select(long.Parse).ToList();
        if (Environment.GetEnvironmentVariable("NURBOT_DATA_DIR") is { Length: > 0 } dir)
            config.DataDir = dir;
        if (Environment.GetEnvironmentVariable("NURBOT_NURCRM_URL") is { Length: > 0 } url)
            config.NurCrmBaseUrl = url;
        if (Environment.GetEnvironmentVariable("NURBOT_TELEGRAM_URL") is { Length: > 0 } tgUrl)
            config.TelegramApiUrl = tgUrl;

        if (!Path.IsPathRooted(config.DataDir))
            config.DataDir = Path.Combine(baseDir, config.DataDir);
        if (!config.NurCrmBaseUrl.EndsWith('/'))
            config.NurCrmBaseUrl += "/";
        if (!config.TelegramApiUrl.EndsWith('/'))
            config.TelegramApiUrl += "/";
        return config;
    }
}
