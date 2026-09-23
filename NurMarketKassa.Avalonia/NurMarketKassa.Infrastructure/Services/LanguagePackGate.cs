namespace NurMarketKassa.Services;

/// <summary>Платный «Языковой пакет» (2026-09-07, по решению владельца): по умолчанию в кассе
/// доступны только русский и кыргызский; английский, турецкий и узбекский открываются серийным
/// номером в Маркетплейс → Доп. функции (UserPreferences.LanguagePackUnlocked). Словари этих
/// языков остаются встроенными — скачивать нечего, активация мгновенная.</summary>
public static class LanguagePackGate
{
    public static bool IsPremium(AppLanguage language) =>
        language is AppLanguage.English or AppLanguage.Turkish or AppLanguage.Uzbek;

    public static bool IsUnlocked => UserPreferences.Instance.LanguagePackUnlocked;

    public static bool IsAvailable(AppLanguage language) => !IsPremium(language) || IsUnlocked;

    /// <summary>При старте: если пакет не активирован, а сохранён платный язык (например, до
    /// этого обновления) — переключаем на русский и сохраняем, чтобы XAML и Tr.T не разошлись.</summary>
    public static AppLanguage EnforceOnStartup()
    {
        var prefs = UserPreferences.Instance;
        if (!IsAvailable(prefs.Language))
        {
            prefs.Language = AppLanguage.Russian;
            prefs.SaveToDisk();
        }

        return prefs.Language;
    }
}
