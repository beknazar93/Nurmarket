namespace NurMarketKassa.Services;

/// <summary>
/// Офлайн-проверка серийных номеров для платных доп. функций (темы, голосовое управление,
/// аналитика склада, редактор ценников, табель, лояльность, языковой пакет) — нет сервера
/// лицензий, ключи проверяются локально.
///
/// 2026-09-07: два разных вида ключей.
/// - <see cref="MasterTestSerial"/> — один тестовый ключ на всё, даёт доступ ровно на
///   <see cref="AccessDuration"/> (15 минут) с момента активации; по истечении фича, открытая
///   ИМЕННО им, автоматически закрывается обратно и ключ нужно ввести заново (см.
///   <see cref="ActivateMasterAccessFor"/>/<see cref="ExpireIfDue"/>).
/// - Постоянные ключи (<see cref="IsPermanentSerial"/>) — по одному на фичу, разблокируют её
///   навсегда, как настоящая покупка. Список ключей и что каждый открывает — см.
///   docs/paid-serial-keys.md (не для показа покупателю в интерфейсе, только для владельца).
/// </summary>
public static class LicenseKeys
{
    public const string MasterTestSerial = "NMK-TEST-MASTER-2026";

    private static readonly TimeSpan AccessDuration = TimeSpan.FromMinutes(15);

    /// <summary>slug -> постоянный ключ. Slug — внутренний идентификатор фичи, используется
    /// и в UserPreferences.MasterUnlockedFeatureFlags. Значения продублированы в открытом виде в
    /// docs/paid-serial-keys.md — этот словарь единственный источник истины, при смене ключа
    /// менять надо и там, и там.</summary>
    private static readonly Dictionary<string, string> PermanentSerials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["voice"] = "NMK-VOICE-8F21-PERM",
        ["loyalty"] = "NMK-LOYAL-4C77-PERM",
        ["timesheet"] = "NMK-SHIFT-9K02-PERM",
        ["analytics"] = "NMK-STAT-3R58-PERM",
        ["language"] = "NMK-LANG-6T14-PERM",
        ["pricetag"] = "NMK-TAG-2Q90-PERM",
        ["labeleditor"] = "NMK-LABEL-1V64-PERM",
        ["bulktag"] = "NMK-BULK-7Y36-PERM",
        ["theme"] = "NMK-THEME-5W83-PERM",
        ["scales"] = "NMK-SCALE-4D71-PERM",
        ["telegram"] = "NMK-TGBOT-8H52-PERM",
        ["shiftstats"] = "NMK-SHSTAT-3N19-PERM",
        ["export"] = "NMK-XPORT-6B47-PERM",
    };

    public static bool IsMasterSerial(string? serial) =>
        string.Equals(serial?.Trim(), MasterTestSerial, StringComparison.OrdinalIgnoreCase);

    /// <summary>Постоянный ключ для конкретной фичи (slug — "voice"/"loyalty"/"timesheet"/
    /// "analytics"/"language"/"pricetag"/"bulktag"/"theme").</summary>
    public static bool IsPermanentSerial(string featureSlug, string? serial) =>
        PermanentSerials.TryGetValue(featureSlug, out var real) &&
        string.Equals(serial?.Trim(), real, StringComparison.OrdinalIgnoreCase);

    /// <summary>Мастер-ключ принят для этой фичи — включает её на 15 минут. Флаг фичи
    /// (UserPreferences.XxxUnlocked) выставляет вызывающий код, как и раньше — этот метод только
    /// запоминает, что доступ временный и когда его нужно снять.</summary>
    public static void ActivateMasterAccessFor(string featureSlug)
    {
        var prefs = UserPreferences.Instance;
        prefs.MasterAccessExpiresAtUtc = DateTime.UtcNow.Add(AccessDuration);
        if (!prefs.MasterUnlockedFeatureFlags.Contains(featureSlug, StringComparer.OrdinalIgnoreCase))
            prefs.MasterUnlockedFeatureFlags.Add(featureSlug);
        prefs.SaveToDisk();
        ScheduleExpiryTimer();
    }

    /// <summary>То же самое для тем — отдельный список ID (одна активация мастер-ключа
    /// открывает сразу все платные темы, как и раньше), а не единый флаг.</summary>
    public static void ActivateMasterThemeAccess(IEnumerable<string> themeIds)
    {
        var prefs = UserPreferences.Instance;
        prefs.MasterAccessExpiresAtUtc = DateTime.UtcNow.Add(AccessDuration);
        foreach (var id in themeIds)
            if (!prefs.MasterUnlockedThemeIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                prefs.MasterUnlockedThemeIds.Add(id);
        prefs.SaveToDisk();
        ScheduleExpiryTimer();
    }

    public static bool IsMasterAccessActive =>
        UserPreferences.Instance.MasterAccessExpiresAtUtc is { } exp && DateTime.UtcNow < exp;

    private static Timer? _expiryTimer;

    private static void ScheduleExpiryTimer()
    {
        _expiryTimer?.Dispose();
        _expiryTimer = new Timer(_ => ExpireIfDue(), null, AccessDuration, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Проверяет, истёк ли временный доступ, и если да — закрывает обратно ровно те
    /// фичи/темы, что были открыты мастер-ключом (постоянные ключи этот список не трогают,
    /// т.к. они пишут XxxUnlocked=true напрямую, минуя ActivateMasterAccessFor). Вызывается
    /// таймером выше и один раз при старте приложения (App.axaml.cs) — на случай, если 15 минут
    /// истекли, пока касса была закрыта.</summary>
    public static void ExpireIfDue()
    {
        var prefs = UserPreferences.Instance;
        if (prefs.MasterAccessExpiresAtUtc is not { } exp || DateTime.UtcNow < exp)
        {
            if (prefs.MasterAccessExpiresAtUtc is not null)
                ScheduleExpiryTimer();
            return;
        }

        foreach (var slug in prefs.MasterUnlockedFeatureFlags)
            RevertFeatureFlag(prefs, slug);
        foreach (var themeId in prefs.MasterUnlockedThemeIds)
            prefs.UnlockedThemeIds.Remove(themeId);

        prefs.MasterUnlockedFeatureFlags.Clear();
        prefs.MasterUnlockedThemeIds.Clear();
        prefs.MasterAccessExpiresAtUtc = null;
        prefs.SaveToDisk();
    }

    private static void RevertFeatureFlag(UserPreferences prefs, string slug)
    {
        switch (slug)
        {
            case "voice": prefs.VoiceControlUnlocked = false; break;
            case "loyalty": prefs.LoyaltyEnabled = false; break;
            case "timesheet": prefs.StaffTimesheetUnlocked = false; break;
            case "analytics": prefs.WarehouseAnalyticsUnlocked = false; break;
            case "language": prefs.LanguagePackUnlocked = false; break;
            case "pricetag": prefs.PriceTagEditorUnlocked = false; break;
            case "labeleditor": prefs.LabelEditorUnlocked = false; break;
            case "bulktag": prefs.BulkPriceTagUnlocked = false; break;
            case "scales": prefs.ScalesUnlocked = false; break;
            case "telegram": prefs.TelegramBotUnlocked = false; break;
            case "shiftstats": prefs.ShiftAnalyticsUnlocked = false; break;
            case "export": prefs.AnalyticsExportUnlocked = false; break;
        }
    }
}
