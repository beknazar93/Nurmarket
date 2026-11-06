namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (аудит №4, Л4-19; решение владельца «на все ок»): программа владельца (--owner) на ПК, где стоит касса, входила сама по сохранённой
/// сессии — кассир под той же учётной записью Windows нажимал ярлык «NurCRM Владелец» и видел зарплату, финансы и долги без пароля. Теперь на таком ПК
/// после автовхода спрашивается PIN владельца (проверка на сервере, как «проведение старого чека»); отказ или нет связи — обычный экран входа с паролем.
/// «ПК с кассой» — есть база кассы (<c>NurMarketKassa\data\pos_local.db</c>); на ПК без кассы (директор дома) ничего не меняется.
/// Не сделано (частично): повтор PIN после 15 минут простоя и запрет запоминать сессию владельца без галочки — отдельным шагом.</summary>
public static class OwnerStartupGate
{
    /// <summary>Спрашивать ли PIN после автовхода: только программа владельца и только если на этом ПК есть касса.</summary>
    public static bool ShouldAskPin(bool isOwnerMode, bool kassaInstalledHere) => isOwnerMode && kassaInstalledHere;

    /// <summary>2026-11-06 (Л4-19, решение владельца): защита — ОПЦИЯ, по умолчанию выключена (<see cref="UserPreferences.AskOwnerPinOnKassaPc"/>). У большинства PIN владельца не задан, и без опции
    /// владелец на ПК с кассой при каждом запуске упирался в окно PIN, которое нечем пройти. <paramref name="enabledByOwner"/> — значение настройки.</summary>
    public static bool ShouldAskPin(bool isOwnerMode, bool kassaInstalledHere, bool enabledByOwner) => enabledByOwner && ShouldAskPin(isOwnerMode, kassaInstalledHere);

    /// <summary>Есть ли на этом ПК база кассы (папка кассы в Roaming). <paramref name="roamingRoot"/> — для теста; по умолчанию AppPaths.Roaming.</summary>
    public static bool KassaInstalledHere(string? roamingRoot = null)
    {
        try
        {
            var root = roamingRoot ?? AppPaths.Roaming;
            return File.Exists(Path.Combine(root, "NurMarketKassa", "data", "pos_local.db"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
