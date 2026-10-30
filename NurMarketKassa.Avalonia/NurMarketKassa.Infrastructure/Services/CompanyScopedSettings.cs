using System.Reflection;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29, владелец: «при смене аккаунта всё должно смениться, ничего не должно остаться от старого».
/// Настройки кассы (user-settings.json) раньше переживали смену компании целиком: ключи ИИ, платные разблокировки, купленные
/// темы и пакеты, мастер-доступ, бот, принтеры и вид. Новая компания получала всё это чужое. Теперь при смене компании
/// (AccountDataIsolation.SwitchTo) все настройки, кроме перечисленных ниже, сбрасываются к значениям нового экземпляра;
/// настоящие значения нового аккаунта потом приходят из облака (SettingsCloudSync) и от сервера (компания, тариф).
/// Ничего не откладывается «на потом»: при возврате в прежнюю компанию её настройки тоже берутся с сервера.</summary>
public static class CompanyScopedSettings
{
    /// <summary>Что НЕ сбрасывается: вход только что вошедшего пользователя (иначе потеряем его сразу после входа), ключ
    /// набора данных, сведения о самой программе/обновлении и режим окна (свойства экрана, а не компании) и язык
    /// интерфейса (до загрузки облачных настроек экран входа не должен «прыгать» на русский).</summary>
    private static readonly HashSet<string> Keep = new(StringComparer.Ordinal)
    {
        "LastLoginEmail", "LastLoginPassword", "LastCatalogUserKey", "LastDataAccountKey", "LastOnlineContactAtUtc",
        "LastUpdateCheckUtc", "AppliedPublishDefaultsVersion", "LastSeenAppVersion", "UpdateTesterCode", "LanDeviceId",
        "Autostart", "Fullscreen", "TrueFullscreen", "LowPerformanceMode", "UiScalePercent", "CompactLayoutMode",
        "Language", "PostgreSqlConnectionStringEncrypted",
        "AutonomousModeActivated", "AutonomousModeActivationKey", "AutonomousAutoResume", "PreserveCatalogOnNextAutonomousLogin",
    };

    /// <summary>Сбрасывает настройки в <paramref name="target"/> (в программе — <see cref="UserPreferences.Instance"/>) и возвращает
    /// число изменённых свойств. Файл не пишется — это делает вызывающий (SaveToDisk).</summary>
    public static int ResetForAccountSwitch(UserPreferences target)
    {
        var fresh = new UserPreferences();
        var changed = 0;
        foreach (var p in typeof(UserPreferences).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0 || Keep.Contains(p.Name))
                continue;
            try
            {
                var before = System.Text.Json.JsonSerializer.Serialize(p.GetValue(target), p.PropertyType);
                var after = p.GetValue(fresh);
                if (before == System.Text.Json.JsonSerializer.Serialize(after, p.PropertyType))
                    continue;
                p.SetValue(target, after);
                changed++;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена аккаунта: настройка «{p.Name}» не сброшена ({ex.Message}).", "WARNING");
            }
        }
        return changed;
    }
}
