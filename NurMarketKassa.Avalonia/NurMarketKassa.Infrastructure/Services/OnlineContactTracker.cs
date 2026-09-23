namespace NurMarketKassa.Services;

/// <summary>2026-09-09: время последнего РЕАЛЬНОГО успешного онлайн-обращения к API — не
/// отдельный "пинг", а любой успешный HTTP-ответ сервера (см. NurMarketApiClient.SendOnceAsync).
/// Пока касса на связи, обычная фоновая синхронизация каталога (раз в ~2 минуты) сама постоянно
/// подтверждает связь — отдельный опрос не нужен. Как только связь пропадает, эти вызовы просто
/// перестают успешно завершаться, и отметка застывает на последнем реальном успехе — на этом
/// строится 60-часовой потолок офлайн-работы обычного (не автономного) режима.</summary>
public static class OnlineContactTracker
{
    private static DateTime? _lastSuccessUtc;

    public static void RecordSuccess()
    {
        _lastSuccessUtc = DateTime.UtcNow;
        UserPreferences.Instance.LastOnlineContactAtUtc = _lastSuccessUtc.Value.ToString("O");
        // Обычная запись через SaveToDisk сериализует ВСЕ настройки — при частых HTTP-успехах
        // (синхронизация каталога и т.п.) это было бы слишком часто. Здесь достаточно, чтобы
        // значение попало на диск при следующем штатном SaveToDisk (их в приложении и так много);
        // если процесс аварийно завершится раньше — при следующем старте просто останется чуть
        // более старая, но всё ещё актуальная метка, это не критично для 60-часового окна.
    }

    /// <summary>Последний известный успешный контакт — из памяти (если приложение уже
    /// обращалось к серверу в этом запуске) или с диска (значение с прошлого запуска).</summary>
    public static DateTime? LastSuccessUtc =>
        _lastSuccessUtc ??= ParseStored();

    private static DateTime? ParseStored()
    {
        var stored = UserPreferences.Instance.LastOnlineContactAtUtc;
        return !string.IsNullOrWhiteSpace(stored) && DateTime.TryParse(
            stored, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
