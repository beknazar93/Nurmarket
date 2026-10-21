using System.Text.Json;

namespace NurRemoteSupport.Operator;

/// <summary>Надписи на 5 языках (правило проекта): ru, ky, en, tr, uz.</summary>
internal static class L
{
    public static string Lang { get; set; } = "ru";

    public static string T(string ru, string ky, string en, string tr, string uz) => Lang switch
    {
        "ky" => ky,
        "en" => en,
        "tr" => tr,
        "uz" => uz,
        _ => ru,
    };
}

/// <summary>Настройки программы на этом компьютере: адрес сервера, логин, язык. Пароль и токен не сохраняются.</summary>
internal sealed class Settings
{
    public string Server { get; set; } = "https://support.nurcrm.kg";
    public string Login { get; set; } = "";
    public string Lang { get; set; } = "ru";
    public int Quality { get; set; } = 60;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketSupport", "settings.json");

    private static Settings? _current;

    public static Settings Current => _current ??= Load();

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) is { } s)
                return s;
        }
        catch
        {
            // испорченный файл — настройки по умолчанию
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // не записалось — не страшно, в следующий раз спросим снова
        }
    }
}
