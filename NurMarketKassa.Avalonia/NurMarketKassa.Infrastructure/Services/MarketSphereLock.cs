using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-08, владелец: «"Вид магазина меняется только с помощью тех поддержки" — высвечивать эту надпись и поставить кнопки серыми».
/// Вид магазина (сфера) после первого выбора меняет только техподдержка. Правила:
/// • новая установка, вид ещё не выбран (MarketSphereChoicePending) и у компании на сервере его нет — выбрать можно один раз
///   (окно первого запуска MarketSphereChoiceWindow или Настройки → Операции); после выбора — замок;
/// • у работающих касс (в файле настроек нет MarketSphereChoicePending) — замок сразу, кнопки серые и показывают текущий вид;
/// • техподдержка меняет вид «сверху»: (1) market_sphere компании на сервере — как раньше (MarketSphereSync, смена значения
///   применяется); (2) ключ <see cref="CloudKey"/> в общих настройках компании (api/main/app-settings/nurmarket-kassa/) —
///   касса только ЧИТАЕТ его (не отправляет) и применяет каждое новое назначение один раз (по метке stamp), в том числе точную
///   сферу (hardware…); allow_local_change: true снимает замок, пока ключ так стоит; (3) во время сеанса удалённой помощи
///   «Нур Поддержка» (оператор подключён) замок снят — оператор переключает вид прямо в настройках.
/// Защита — от случайного переключения кассиром/владельцем в программе; сервер NurCRM этот ключ пока не охраняет (общие
/// настройки может писать владелец/администратор через API) — окончательно запрет делает сервер (ТЗ «Админ-панель техподдержки»).
/// </summary>
public static class MarketSphereLock
{
    /// <summary>Ключ общих настроек компании с назначением техподдержки:
    /// {"sphere":"clothing","stamp":"2026-11-08T10:00:00Z","allow_local_change":false,"by":"оператор"} (объект или та же строка JSON).</summary>
    public const string CloudKey = "SupportMarketSphere";

    /// <summary>Телефон поддержки — тот же, что RemoteSupportHost.SupportPhone (тот файл на Android не собирается).</summary>
    public const string SupportPhone = "+996 221 068 796";

    /// <summary>Назначение техподдержки из общих настроек компании.</summary>
    public sealed record Assignment(string Sphere, string Stamp, bool AllowLocalChange, string? By);

    private static volatile bool _supportSession;
    private static volatile bool _unlockedBySupport;

    /// <summary>Замок снят/поставлен (сеанс поддержки, назначение, ключ allow_local_change). Приходит из любого потока.</summary>
    public static event Action? Changed;

    /// <summary>Идёт сеанс удалённой помощи с подключённым оператором (ставит RemoteSupportHost).</summary>
    public static bool SupportSessionActive
    {
        get => _supportSession;
        set
        {
            if (_supportSession == value)
                return;
            _supportSession = value;
            PosLogger.Log(value ? "Вид магазина: оператор поддержки подключён — переключение открыто." : "Вид магазина: сеанс поддержки закончен — переключение снова закрыто.", "SETTINGS");
            RaiseChanged();
        }
    }

    /// <summary>Техподдержка разрешила менять вид на кассе (allow_local_change в последнем полученном назначении).</summary>
    public static bool UnlockedBySupport => _unlockedBySupport;

    /// <summary>Правило замка (чистая функция — для проверок): менять вид на кассе можно только при первом выборе (вид ещё не выбран
    /// и на сервере не задан), во время сеанса поддержки или когда поддержка сама разрешила.</summary>
    public static bool CanChangeLocally(bool choicePending, string? serverSphere, bool unlockedBySupport, bool supportSession) =>
        unlockedBySupport || supportSession || (choicePending && serverSphere is null);

    /// <summary>Можно ли сейчас переключать вид в настройках этой кассы.</summary>
    public static bool CanChangeNow =>
        CanChangeLocally(UserPreferences.Instance.MarketSphereChoicePending, MarketSphereSync.ServerSphere, _unlockedBySupport, _supportSession);

    /// <summary>Сейчас открыт именно первый (одноразовый) выбор, а не разрешение поддержки.</summary>
    public static bool IsFirstChoice => !_unlockedBySupport && !_supportSession && CanChangeNow;

    /// <summary>Выбор в настройках сделан — первый выбор израсходован (дальше — через поддержку).</summary>
    public static void NoteLocalChoice()
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.MarketSphereChoicePending)
            return;
        prefs.MarketSphereChoicePending = false;
        prefs.SaveToDisk();
        RaiseChanged();
    }

    /// <summary>Разбор ключа <see cref="CloudKey"/>: объект или строка с JSON. Неизвестная каталогу сфера и мусор → null.
    /// Без stamp меткой служит сама сфера (сменили сферу — применится снова).</summary>
    public static Assignment? Parse(JsonNode? node)
    {
        try
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var text))
                node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            if (node is not JsonObject obj)
                return null;
            var sphere = SphereCatalog.SanitizeDetail(Str(obj["sphere"]));
            if (sphere is null || SphereCatalog.TryGet(sphere) is null)
                return null;
            var stamp = Str(obj["stamp"])?.Trim();
            var allow = obj["allow_local_change"] is JsonValue a
                        && ((a.TryGetValue<bool>(out var b) && b) || (a.TryGetValue<string>(out var s) && string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)));
            var by = Str(obj["by"])?.Trim();
            return new Assignment(sphere, string.IsNullOrEmpty(stamp) ? sphere : stamp, allow, string.IsNullOrEmpty(by) ? null : by);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? _lastBadRaw;

    /// <summary>Общие настройки компании получены (SettingsCloudSync: вход и опрос на лету) — применить назначение техподдержки.
    /// Каждое назначение (метка stamp) применяется один раз: вид ставится, первый выбор считается сделанным. true — вид сменился.</summary>
    public static bool ApplyFromCompanyDoc(JsonObject? company)
    {
        if (company is null)
            return false;
        var node = company[CloudKey];
        var assignment = Parse(node);
        if (assignment is null && node is not null)
        {
            var raw = node.ToJsonString();
            if (raw != _lastBadRaw)
            {
                _lastBadRaw = raw;
                PosLogger.Log($"Вид магазина от поддержки не понят ({(raw.Length > 200 ? raw[..200] + "…" : raw)}) — не применён.", "WARNING");
            }
        }

        var unlocked = assignment?.AllowLocalChange == true;
        var lockChanged = unlocked != _unlockedBySupport;
        _unlockedBySupport = unlocked;

        var changed = false;
        if (assignment is not null)
        {
            var prefs = UserPreferences.Instance;
            if (!string.Equals(prefs.SupportSphereAppliedStamp, assignment.Stamp, StringComparison.Ordinal))
            {
                var before = MarketSpheres.Profile.Id;
                prefs.SupportSphereAppliedStamp = assignment.Stamp;
                prefs.MarketSphereChoicePending = false;
                prefs.SaveToDisk();
                MarketSpheres.Set(assignment.Sphere);
                changed = MarketSpheres.Profile.Id != before;
                PosLogger.Log($"Вид магазина назначен техподдержкой{(assignment.By is null ? "" : " (" + assignment.By + ")")}: {before} → {assignment.Sphere}, метка {assignment.Stamp}.", "SETTINGS");
                lockChanged = true;
            }
        }

        if (lockChanged)
            RaiseChanged();
        return changed;
    }

    /// <summary>Только для проверок: сбросить память замка.</summary>
    internal static void ResetForTests()
    {
        _supportSession = false;
        _unlockedBySupport = false;
        _lastBadRaw = null;
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Вид магазина: обработчик замка упал ({ex.Message}).", "WARNING");
        }
    }
}
