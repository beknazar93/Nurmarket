using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21, владелец: документы клиента (КП, счета, накладные) в «Клиентах» программы владельца.
///
/// Где хранится. У NurCRM нет API для КП/счетов по каталогу кассы (warehouse/documents/commercial-offer/ — модуль «Склад 2.0» со
/// своими складами, контрагентами и товарами, не каталог main/products), телефон держит КП и счета в своей базе под ключом
/// настроек «quotes». Поэтому общий список — в ОБЩИХ настройках компании NurCRM той же программы, что у телефона:
/// api/main/app-settings/nurmarket-mobile/, ключи:
///   • «quotes» — КП и счета, массив в формате телефона (QuoteDoc.toJson, см. <see cref="ClientDoc"/>);
///   • «waybills» — накладные, тот же формат (kind = "waybill"; отдельно, чтобы телефон не принял их за счета);
///   • «docsProfile» — реквизиты продавца, шаблоны и счётчики номеров (без логотипа: путь к картинке — только этого ПК).
/// Читают все сотрудники; меняют владелец и администратор (кассир — 403, тогда документы остаются на этом ПК).
///
/// Копия и несохранённые изменения — на этом ПК (%APPDATA%\NurMarketOwner\client-docs\{компания}.json): нет связи — работа
/// продолжается, изменения уходят при следующем сохранении/обновлении.
///
/// 2026-10-21 (ревью документов, A3–A5) — слияние, общее с телефоном (тот же формат делает агент мобилки, имена не менять):
///   • у каждого документа в quotes/waybills — «updatedAt» (ISO 8601 UTC, ставится при каждом изменении); удаление — «надгробие»
///     {"id", "deleted": true, "updatedAt"} (хранится 90 дней, потом убирается; в списках не показывается);
///   • запись: свежий GET → по id побеждает больший updatedAt (без поля — старше любого с полем) → PATCH только изменившихся ключей →
///     GET-проверка: если чужая запись затёрла нашу — слияние ещё раз (до 3 раз), потом пауза с ростом (15 с … 10 мин);
///   • своя правка, которая старее серверной версии, снимается (побеждает сервер) и владелец видит «изменён на другом устройстве»;
///   • незнакомые поля документов и строк сохраняются; нечитаемый документ уходит обратно как есть; повреждённый ключ целиком не
///     перезаписывается;
///   • реквизиты продавца — только изменённые на этом ПК поля поверх свежих серверных; счётчики номеров — отдельно (максимум);
///   • номер нового документа перед записью сверяется со свежим списком: занят — следующий свободный (и сообщение).</summary>
public static class ClientDocsStore
{
    public const string CloudPath = "api/main/app-settings/nurmarket-mobile/";
    public const string KeyQuotes = "quotes";
    public const string KeyWaybills = "waybills";
    public const string KeyProfile = "docsProfile";
    /// <summary>2026-10-21 (ревью документов, A4): сколько дней хранится «надгробие» удалённого документа.</summary>
    public const int TombstoneDays = 90;
    /// <summary>2026-10-21 (ревью документов, мелочи): «quotes» больше ~1 МБ — предупреждение (каждая запись шлёт весь список).</summary>
    public const long LargeQuotesBytes = 1_000_000;
    private const int MaxPushRounds = 3;

    public enum SyncState { Unknown, Saving, Saved, LocalOnly, Forbidden }

    /// <summary>Стенд снимков: своя папка вместо %APPDATA% и без сервера.</summary>
    public static string? DataDirOverride { get; set; }
    public static bool CloudDisabled { get; set; }
    /// <summary>2026-10-21 (ревью документов): стенд проверки слияния — свой «сервер» вместо NurCRM (GET → ответ, PATCH → тело).</summary>
    public static Func<HttpMethod, JsonObject?, Task<JsonElement>>? TransportOverride { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Sync = new();
    private static readonly SemaphoreSlim PushGate = new(1, 1);
    /// <summary>2026-10-21 (ревью документов, A4): список сервера как есть (вместе с надгробиями и нечитаемыми документами).</summary>
    private static List<RemoteItem> _remote = new();
    private static readonly Dictionary<string, ClientDoc> Pending = new();
    /// <summary>2026-10-21 (ревью документов, A4): удалённые здесь, ещё не подтверждённые сервером.</summary>
    private static readonly Dictionary<string, Tomb> Tombs = new();
    private static ClientDocProfile _profile = new();
    /// <summary>2026-10-21 (ревью документов, A5): изменённые на этом ПК поля реквизитов («bank», «templates/quote»…).</summary>
    private static readonly HashSet<string> ProfileDirty = new();
    /// <summary>2026-10-21 (ревью документов, A5): счётчики номеров — отдельно от реквизитов.</summary>
    private static bool _countersDirty;
    /// <summary>2026-10-21 (ревью документов, A5): номер, заменённый при записи (занят другим устройством): id → (старый, новый).</summary>
    private static readonly Dictionary<string, (string Old, string New)> Renumbered = new();
    private static string? _loadedFor;
    private static bool _remoteLoaded;
    private static CancellationTokenSource? _pushDelay;
    private static int _remoteGen;
    private static bool _pushing;
    private static int _failures;
    /// <summary>2026-10-21 (ревью документов, A4): сколько ещё раз перепроверить сервер после успешной записи (сбрасывается правкой).</summary>
    private static int _recheckBudget;
    /// <summary>2026-10-21 (ревью документов, A5): поля реквизитов, записанные этим ПК: значение, прежнее значение сервера, когда.
    /// 10 минут: если сервер снова показывает ровно прежнее значение — его вернула чужая запись по старой копии, поле отправляется
    /// ещё раз (у реквизитов нет updatedAt).</summary>
    private static readonly Dictionary<string, (string Value, string Was, DateTime At)> ProfileWritten = new();
    private static DateTime _pauseUntilUtc = DateTime.MinValue;

    public static SyncState State { get; private set; } = SyncState.Unknown;
    public static DateTime? LastSavedAt { get; private set; }
    /// <summary>2026-10-21 (ревью документов, A3/A5): последнее сообщение для владельца (документ изменён на другом устройстве,
    /// номер занят…). null — нет.</summary>
    public static string? Notice { get; private set; }
    /// <summary>2026-10-21 (ревью документов, мелочи): размер «quotes» на сервере, байт (UTF-8).</summary>
    public static long QuotesBytes { get; private set; }
    public static bool QuotesLarge => QuotesBytes > LargeQuotesBytes;

    /// <summary>Список или профиль изменились (на UI-потоке).</summary>
    public static event Action? Changed;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string CompanyKey => CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : "default";

    private static string FilePath
    {
        get
        {
            var dir = DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "client-docs");
            var safe = string.Concat(CompanyKey.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
            return Path.Combine(dir, (safe.Length == 0 ? "default" : safe) + ".json");
        }
    }

    // ───────────────────────────── список сервера ─────────────────────────────

    /// <summary>2026-10-21 (ревью документов, A4): элемент quotes/waybills как на сервере. Doc = null — надгробие или нечитаемый
    /// (такой уходит обратно без изменений).</summary>
    internal sealed class RemoteItem
    {
        public string Key = KeyQuotes;
        public JsonNode Raw = new JsonObject();
        public ClientDoc? Doc;
        public string? Id;
        public string? At;
        public string? Legacy;
        /// <summary>Номер (и у надгробия — телефон пишет его туда же) — для следующего номера без повторов.</summary>
        public string? Number;
        public bool IsTomb;
        public int Bytes;
    }

    /// <summary>2026-10-21 (ревью документов, A4): удаление на этом ПК, ещё не подтверждённое сервером.</summary>
    internal sealed class Tomb
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("key")] public string? Key { get; set; }
        /// <summary>Номер удалённого (как у телефона в надгробии) — номер не выдаётся повторно.</summary>
        [JsonPropertyName("number")] public string? Number { get; set; }
    }

    internal static RemoteItem ParseItem(string key, JsonNode raw)
    {
        var text = raw.ToJsonString(Json);
        var item = new RemoteItem { Key = key, Raw = raw, Bytes = Encoding.UTF8.GetByteCount(text) };
        if (raw is not JsonObject o)
            return item;
        item.Id = Str(o["id"]);
        item.At = Str(o["updatedAt"]);
        item.Legacy = Str(o["updated"]);
        item.Number = Str(o["number"]);
        item.IsTomb = o["deleted"] is JsonValue dv && dv.TryGetValue<bool>(out var del) && del;
        if (item.Id is not { Length: > 0 } || item.IsTomb)
            return item;
        try
        {
            item.Doc = JsonSerializer.Deserialize<ClientDoc>(text, Json);
            if (item.Doc is { } d && d.Id != item.Id)
                item.Doc = null;
            if (item.Doc != null && key == KeyWaybills)
                item.Doc.Kind = ClientDocKind.Waybill;
            // Телефон при смене цены строки снимает pr0 (и dp): нет цены до скидки — нет и скидки строки (бланк не покажет «5%»
            // к уже сниженной цене).
            foreach (var l in item.Doc?.Lines ?? new())
                if (l.ListPrice == null)
                    l.DiscountPercent = null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            item.Doc = null;
            PosLogger.Log($"Документы клиента: документ {item.Id} с сервера не прочитан — сохраняется как есть ({ex.Message}).", "WARNING");
        }
        return item;
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : v.ToJsonString()) : null;

    /// <summary>Самый новый элемент сервера по каждому id (повторы id — побеждает больший updatedAt).</summary>
    private static Dictionary<string, RemoteItem> NewestById(List<RemoteItem> items)
    {
        var map = new Dictionary<string, RemoteItem>();
        foreach (var i in items)
            if (i.Id is { Length: > 0 } id && (!map.TryGetValue(id, out var cur) || Cmp(i, cur) > 0))
                map[id] = i;
        return map;
    }

    /// <summary>2026-10-21 (ревью документов, A4): сравнение версий: больше 0 — первая новее. Без updatedAt — старше любой с ним;
    /// у обеих нет — по старому полю «updated» (местное время).</summary>
    public static int CompareVersion(string? aAt, string? aLegacy, string? bAt, string? bLegacy)
    {
        var a = ClientDoc.ParseUtc(aAt);
        var b = ClientDoc.ParseUtc(bAt);
        if (a.HasValue || b.HasValue)
            return Nullable.Compare(a, b);
        return Nullable.Compare(ClientDoc.ParseDate(aLegacy), ClientDoc.ParseDate(bLegacy));
    }

    public static int CompareVersion(ClientDoc a, ClientDoc b) => CompareVersion(a.UpdatedAtRaw, a.UpdatedRaw, b.UpdatedAtRaw, b.UpdatedRaw);
    private static int Cmp(RemoteItem a, RemoteItem b) => CompareVersion(a.At, a.Legacy, b.At, b.Legacy);
    private static int Cmp(ClientDoc p, RemoteItem r) => CompareVersion(p.UpdatedAtRaw, p.UpdatedRaw, r.At, r.Legacy);
    private static int Cmp(Tomb t, RemoteItem r) => CompareVersion(t.UpdatedAt, null, r.At, r.Legacy);

    private static bool SameNumber(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── чтение ─────────────────────────────

    /// <summary>Все документы (копии): с сервера + изменения этого ПК. Новые сверху.</summary>
    public static List<ClientDoc> All()
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            return Compose().Select(d => d.Clone()).OrderByDescending(d => d.Date).ThenByDescending(d => d.UpdatedAt).ToList();
        }
    }

    public static ClientDoc? Get(string id)
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            return Compose().FirstOrDefault(d => d.Id == id)?.Clone();
        }
    }

    public static ClientDocProfile Profile
    {
        get
        {
            lock (Sync)
            {
                EnsureLocalLoaded();
                return _profile.Clone();
            }
        }
    }

    /// <summary>Есть изменения, ещё не ушедшие на сервер.</summary>
    public static bool HasPending
    {
        get { lock (Sync) return Pending.Count > 0 || Tombs.Count > 0 || ProfileDirty.Count > 0 || _countersDirty; }
    }

    // 2026-10-21 (ревью документов, A3/A4): по id — свежее из сервера и своих правок/удалений; надгробия не показываются.
    private static List<ClientDoc> Compose()
    {
        var list = new List<ClientDoc>();
        var seen = new HashSet<string>();
        foreach (var (id, r) in NewestById(_remote))
        {
            seen.Add(id);
            if (Tombs.TryGetValue(id, out var t) && Cmp(t, r) > 0)
                continue;
            if (Pending.TryGetValue(id, out var mine) && Cmp(mine, r) > 0)
                list.Add(mine);
            else if (r.Doc != null && !r.IsTomb)
                list.Add(r.Doc);
        }
        foreach (var p in Pending.Values)
            if (seen.Add(p.Id) && !Tombs.ContainsKey(p.Id))
                list.Add(p);
        return list;
    }

    // ───────────────────────────── запись ─────────────────────────────

    /// <summary>Сохранить документ (новый или изменённый): сразу на этом ПК, на сервер — через пару секунд (черновик пишется часто).</summary>
    public static void Save(ClientDoc doc, int pushDelayMs = 2500)
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            // 2026-10-21 (ревью документов, A5): номер заменён при записи (был занят другим устройством) — у открытого окна старый.
            if (Renumbered.TryGetValue(doc.Id, out var rn) && SameNumber(doc.Number, rn.Old))
                doc.Number = rn.New;
            TouchAfterRemote(doc);
            Tombs.Remove(doc.Id);
            // Ручной номер больше счётчика — счётчик догоняет, следующий автоматический не повторит его.
            if (ClientDoc.NumberIndex(doc.Number, doc.Kind) is { } n)
                BumpCounter(doc.Kind, n);
            if (!NewestById(_remote).ContainsKey(doc.Id) && doc.History.Count == 0)
                doc.AddHistory(T("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"));
            Pending[doc.Id] = doc.Clone();
            _recheckBudget = 2;
            WriteLocal();
        }
        RaiseChanged();
        SchedulePush(pushDelayMs);
    }

    /// <summary>2026-10-21 (ревью документов, A3/A4): updatedAt = сейчас (UTC), но строго новее версии сервера, которую этот ПК уже
    /// видел, и прежней своей правки — правка поверх увиденного побеждает и при отстающих часах этого ПК.</summary>
    private static void TouchAfterRemote(ClientDoc doc)
    {
        var floors = new[]
        {
            NewestById(_remote).TryGetValue(doc.Id, out var r) ? ClientDoc.ParseUtc(r.At) : null,
            ClientDoc.ParseUtc(doc.UpdatedAtRaw),
            Pending.TryGetValue(doc.Id, out var p) ? ClientDoc.ParseUtc(p.UpdatedAtRaw) : null,
        };
        doc.Touch();
        var stamp = DateTime.UtcNow;
        foreach (var f in floors)
            if (f is { } v && v >= stamp)
                stamp = v.AddMilliseconds(1);
        doc.UpdatedAtRaw = ClientDoc.FormatUtc(stamp);
    }

    public static void Delete(string id)
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            // 2026-10-21 (ревью документов, A4): надгробие вместо простого удаления — копия другого ПК/телефона его не «воскресит».
            NewestById(_remote).TryGetValue(id, out var r);
            Pending.TryGetValue(id, out var mine);
            var stamp = DateTime.UtcNow;
            foreach (var f in new[] { r != null ? ClientDoc.ParseUtc(r.At) : null, mine != null ? ClientDoc.ParseUtc(mine.UpdatedAtRaw) : null })
                if (f is { } v && v >= stamp)
                    stamp = v.AddMilliseconds(1);
            Pending.Remove(id);
            Tombs[id] = new Tomb { Id = id, UpdatedAt = ClientDoc.FormatUtc(stamp), Key = r?.Key ?? (mine?.Kind == ClientDocKind.Waybill ? KeyWaybills : KeyQuotes), Number = r?.Doc?.Number ?? mine?.Number };
            _recheckBudget = 2;
            WriteLocal();
        }
        RaiseChanged();
        SchedulePush(500);
    }

    /// <summary>2026-10-21 (ревью документов, A5): сохраняются только поля, изменённые относительно <paramref name="baseProfile"/>
    /// (то, что было у окна при открытии/последнем сохранении; null — текущие реквизиты), и на сервер уходят только они — поверх
    /// свежих серверных. Устаревшие реквизиты открытого окна не затирают изменённые на другом ПК.</summary>
    public static void SaveProfile(ClientDocProfile profile, ClientDocProfile? baseProfile = null)
    {
        var changed = false;
        lock (Sync)
        {
            EnsureLocalLoaded();
            var mine = ToJson(profile);
            var basis = ToJson(baseProfile ?? _profile);
            var cur = ToJson(_profile);
            foreach (var path in ProfilePaths(mine, basis).ToList())
            {
                var v = GetPath(mine, path);
                if (J(v) == J(GetPath(basis, path)))
                    continue;
                SetPath(cur, path, v?.DeepClone());
                if (path != "logo")
                    ProfileDirty.Add(path);
                changed = true;
            }
            if (changed && FromJson(cur) is { } next)
            {
                MaxInto(next.Counters, _profile.Counters);
                _profile = next;
                _recheckBudget = 2;
                WriteLocal();
            }
        }
        if (!changed)
            return;
        RaiseChanged();
        SchedulePush(1500);
    }

    /// <summary>Следующий номер вида: больше и счётчика, и всех номеров в списке (свои, телефона, удалённые) — без повторов.
    /// Счётчик не меняется: он догоняет номер при первом сохранении документа (<see cref="Save"/>) — пустой документ, закрытый
    /// без изменений, номер не «съедает». Перед записью на сервер номер нового документа ещё раз сверяется со свежим списком.</summary>
    public static string NextNumber(ClientDocKind kind)
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            return NextNumberLocked(kind);
        }
    }

    private static string NextNumberLocked(ClientDocKind kind)
    {
        var key = ClientDocProfile.CounterKey(kind);
        var max = _profile.Counters.TryGetValue(key, out var c) ? c : 0;
        foreach (var d in Compose())
            if (d.Kind == kind && ClientDoc.NumberIndex(d.Number, kind) is { } n && n > max)
                max = n;
        // 2026-10-21 (ревью документов, A5): и номера надгробий (как у телефона) — номер удалённого не повторяется.
        foreach (var number in _remote.Where(i => i.IsTomb).Select(i => i.Number).Concat(Tombs.Values.Select(t => t.Number)))
            if (ClientDoc.NumberIndex(number, kind) is { } n && n > max)
                max = n;
        return $"{ClientDoc.Prefix(kind)}-{max + 1:0000}";
    }

    /// <summary>Такой номер уже есть у другого документа того же вида.</summary>
    public static bool NumberTaken(string number, ClientDocKind kind, string exceptId)
    {
        lock (Sync)
        {
            EnsureLocalLoaded();
            return Compose().Any(d => d.Id != exceptId && d.Kind == kind && SameNumber(d.Number, number));
        }
    }

    private static void BumpCounter(ClientDocKind kind, int n)
    {
        var key = ClientDocProfile.CounterKey(kind);
        if (!_profile.Counters.TryGetValue(key, out var c) || c < n)
        {
            _profile.Counters[key] = n;
            // 2026-10-21 (ревью документов, A5): только счётчики — реквизиты при этом не помечаются изменёнными.
            _countersDirty = true;
        }
    }

    /// <summary>2026-10-21 (ревью документов, A5): новый (или с новым номером) документ, номер которого в свежем списке уже у
    /// другого документа того же вида (телефон, другой ПК), получает следующий свободный номер.</summary>
    private static void RenumberCollisionsLocked()
    {
        var map = NewestById(_remote);
        foreach (var p in Pending.Values.ToList())
        {
            if (map.TryGetValue(p.Id, out var r) && (Cmp(p, r) <= 0 || (r.Doc != null && SameNumber(r.Doc.Number, p.Number))))
                continue;
            if (!Compose().Any(d => d.Id != p.Id && d.Kind == p.Kind && SameNumber(d.Number, p.Number)))
                continue;
            var old = p.Number;
            p.Number = NextNumberLocked(p.Kind);
            p.AddHistory(T($"Номер {old} занят другим устройством — новый номер {p.Number}", $"{old} номери башка түзмөктө бар — жаңы номер {p.Number}",
                $"Number {old} was taken by another device — new number {p.Number}", $"{old} numarası başka bir cihazda kullanılıyor — yeni numara {p.Number}",
                $"{old} raqami boshqa qurilmada band — yangi raqam {p.Number}"));
            TouchAfterRemote(p);
            if (ClientDoc.NumberIndex(p.Number, p.Kind) is { } n)
                BumpCounter(p.Kind, n);
            Renumbered[p.Id] = (old, p.Number);
            SetNotice(T($"Номер {old} уже занят на другом устройстве — документу дан номер {p.Number}.", $"{old} номери башка түзмөктө бар — документке {p.Number} номери берилди.",
                $"Number {old} is already used on another device — the document got number {p.Number}.", $"{old} numarası başka bir cihazda kullanılıyor — belgeye {p.Number} numarası verildi.",
                $"{old} raqami boshqa qurilmada band — hujjatga {p.Number} raqami berildi."));
        }
    }

    // ───────────────────────────── реквизиты как JSON ─────────────────────────────

    private static readonly string[] DictKeys = { "templates", "terms" };

    private static JsonObject ToJson(ClientDocProfile p) => JsonSerializer.SerializeToNode(p, Json) as JsonObject ?? new JsonObject();

    private static ClientDocProfile? FromJson(JsonObject o)
    {
        try
        {
            return o.Deserialize<ClientDocProfile>(Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            PosLogger.Log($"Документы клиента: реквизиты не прочитаны ({ex.Message}).", "WARNING");
            return null;
        }
    }

    private static string J(JsonNode? n) => n?.ToJsonString(Json) ?? "null";

    /// <summary>Поля реквизитов: верхние ключи, у шаблонов и условий — по виду («templates/quote»). Счётчики — отдельно.</summary>
    private static IEnumerable<string> ProfilePaths(JsonObject a, JsonObject b)
    {
        foreach (var k in a.Select(kv => kv.Key).Union(b.Select(kv => kv.Key)).Where(k => k != "counters").ToList())
        {
            if (DictKeys.Contains(k) && a[k] is JsonObject or null && b[k] is JsonObject or null)
            {
                var sub = ((a[k] as JsonObject)?.Select(kv => kv.Key) ?? Enumerable.Empty<string>())
                    .Union((b[k] as JsonObject)?.Select(kv => kv.Key) ?? Enumerable.Empty<string>()).ToList();
                foreach (var s in sub)
                    yield return k + "/" + s;
            }
            else
                yield return k;
        }
    }

    private static JsonNode? GetPath(JsonObject o, string path)
    {
        var i = path.IndexOf('/');
        return i < 0 ? o[path] : (o[path[..i]] as JsonObject)?[path[(i + 1)..]];
    }

    private static void SetPath(JsonObject o, string path, JsonNode? value)
    {
        var i = path.IndexOf('/');
        if (i < 0)
        {
            if (value == null)
                o.Remove(path);
            else
                o[path] = value;
            return;
        }
        var head = path[..i];
        var tail = path[(i + 1)..];
        if (o[head] is not JsonObject sub)
        {
            if (value == null)
                return;
            sub = new JsonObject();
            o[head] = sub;
        }
        if (value == null)
            sub.Remove(tail);
        else
            sub[tail] = value;
    }

    private static int? NumOf(JsonNode? n)
    {
        if (n is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<double>(out var d))
            return (int)d;
        if (v.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var ed))
                return (int)ed;
            if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var es))
                return es;
        }
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var si) ? si : null;
    }

    private static void MaxInto(Dictionary<string, int> into, Dictionary<string, int> from)
    {
        foreach (var (k, v) in from)
            if (!into.TryGetValue(k, out var cur) || cur < v)
                into[k] = v;
    }

    // ───────────────────────────── этот ПК ─────────────────────────────

    private sealed class LocalFile
    {
        [JsonPropertyName("company")] public string? Company { get; set; }
        /// <summary>2026-10-21 (ревью документов, A4): quotes/waybills как на сервере (с надгробиями и нечитаемыми).</summary>
        [JsonPropertyName("server")] public JsonObject? Server { get; set; }
        /// <summary>До ревью 21.10 — только чтение старой копии.</summary>
        [JsonPropertyName("remote")] public List<ClientDoc>? Remote { get; set; }
        [JsonPropertyName("pending")] public List<ClientDoc> Pending { get; set; } = new();
        [JsonPropertyName("tombs")] public List<Tomb> Tombs { get; set; } = new();
        /// <summary>До ревью 21.10 — только чтение.</summary>
        [JsonPropertyName("deleted")] public List<string>? Deleted { get; set; }
        [JsonPropertyName("profile")] public ClientDocProfile Profile { get; set; } = new();
        [JsonPropertyName("profileDirtyFields")] public List<string> ProfileDirtyFields { get; set; } = new();
        [JsonPropertyName("countersDirty")] public bool CountersDirty { get; set; }
        /// <summary>До ревью 21.10 (весь профиль «изменён») — только чтение, считается как изменённые счётчики.</summary>
        [JsonPropertyName("profileDirty")] public bool? ProfileDirtyLegacy { get; set; }
    }

    private static void EnsureLocalLoaded()
    {
        var key = CompanyKey;
        if (_loadedFor == key)
            return;
        _loadedFor = key;
        _remote = new();
        Pending.Clear();
        Tombs.Clear();
        ProfileDirty.Clear();
        ProfileWritten.Clear();
        Renumbered.Clear();
        _countersDirty = false;
        _profile = new ClientDocProfile();
        _remoteLoaded = false;
        _failures = 0;
        _pauseUntilUtc = DateTime.MinValue;
        Notice = null;
        State = SyncState.Unknown;
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<LocalFile>(File.ReadAllText(path), Json);
                if (file != null)
                {
                    if (file.Server != null)
                        _remote = ItemsFrom(file.Server);
                    else
                        foreach (var d in file.Remote ?? new())
                            if (JsonSerializer.SerializeToNode(d, Json) is { } node)
                                _remote.Add(ParseItem(d.Kind == ClientDocKind.Waybill ? KeyWaybills : KeyQuotes, node));
                    foreach (var p in file.Pending ?? new())
                        Pending[p.Id] = p;
                    foreach (var t in file.Tombs ?? new())
                        Tombs[t.Id] = t;
                    foreach (var id in file.Deleted ?? new())
                        Tombs.TryAdd(id, new Tomb { Id = id, UpdatedAt = ClientDoc.FormatUtc(DateTime.UtcNow) });
                    _profile = file.Profile ?? new();
                    foreach (var f in file.ProfileDirtyFields ?? new())
                        ProfileDirty.Add(f);
                    _countersDirty = file.CountersDirty || file.ProfileDirtyLegacy == true;
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: копия на этом ПК не прочитана ({ex.Message}).", "WARNING");
        }
        QuotesBytes = QuotesSize(_remote);
        if (string.IsNullOrWhiteSpace(_profile.Name))
            SeedProfile(_profile);
    }

    private static List<RemoteItem> ItemsFrom(JsonObject server)
    {
        var list = new List<RemoteItem>();
        foreach (var key in new[] { KeyQuotes, KeyWaybills })
            if (server[key] is JsonArray arr)
                foreach (var n in arr)
                    if (n != null)
                        list.Add(ParseItem(key, n.DeepClone()));
        return list;
    }

    private static long QuotesSize(List<RemoteItem> items) => items.Where(i => i.Key == KeyQuotes).Sum(i => (long)i.Bytes + 1) + 1;

    /// <summary>Реквизиты по умолчанию — из настроек чека и компании NurCRM (название, ИНН, адрес).</summary>
    private static void SeedProfile(ClientDocProfile p)
    {
        var prefs = UserPreferences.Instance;
        var company = CompanyInfoService.LastCompany;
        p.Name = FirstText(company?.Name, prefs.StoreName);
        p.Inn ??= FirstText(company?.Inn, prefs.StoreInn);
        p.Address ??= FirstText(company?.Address, prefs.StoreAddress);
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static void WriteLocal()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var server = new JsonObject();
            foreach (var key in new[] { KeyQuotes, KeyWaybills })
                server[key] = new JsonArray(_remote.Where(i => i.Key == key).Select(i => (JsonNode?)i.Raw.DeepClone()).ToArray());
            var file = new LocalFile
            {
                Company = CompanyKey, Server = server, Pending = Pending.Values.ToList(), Tombs = Tombs.Values.ToList(),
                Profile = _profile, ProfileDirtyFields = ProfileDirty.ToList(), CountersDirty = _countersDirty,
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: копия на этом ПК не записана ({ex.Message}).", "WARNING");
        }
    }

    // ───────────────────────────── сервер ─────────────────────────────

    /// <summary>Перечитать общий список с сервера. Сбой сети — молча (false), остаётся копия этого ПК.</summary>
    public static async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (!CanSync(out var api))
        {
            SetState(SyncState.LocalOnly);
            return false;
        }
        var company = CompanyKey;
        int gen;
        lock (Sync)
            gen = _remoteGen;
        try
        {
            var data = await Request(api, HttpMethod.Get, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (!TryParseRemote(data, out var items, out var profile, out var damaged))
                return false;
            lock (Sync)
            {
                if (company != CompanyKey)
                    return false;
                EnsureLocalLoaded();
                // 2026-10-21 (ревью документов, мелочи): пока шёл запрос, отправка записала и перечитала список — этот ответ
                // устарел (вернул бы на время удалённое).
                if (_pushing || gen != _remoteGen)
                    return true;
                ApplyRemote(items, profile, damaged);
                WriteLocal();
            }
            if (State is SyncState.Unknown or SyncState.LocalOnly)
                SetState(HasPending ? SyncState.LocalOnly : SyncState.Saved);
            RaiseChanged();
            if (HasPending)
                SchedulePush(300);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: список с сервера не получен ({ex.Message}).", "INFO");
            SetState(SyncState.LocalOnly);
            return false;
        }
    }

    /// <summary>Отправить несохранённое сейчас (закрытие редактора). true — на сервере.</summary>
    public static Task<bool> FlushAsync() => PushAsync();

    private static void SchedulePush(int delayMs)
    {
        if (CloudDisabled)
        {
            SetState(SyncState.LocalOnly);
            return;
        }
        CancellationTokenSource cts;
        lock (Sync)
        {
            // 2026-10-21 (ревью документов, A3): после повторных сбоев — пауза (15 с … 10 мин), а не запрос каждые 0,8 с.
            var pause = (_pauseUntilUtc - DateTime.UtcNow).TotalMilliseconds;
            if (pause > delayMs)
                delayMs = (int)Math.Ceiling(pause);
            _pushDelay?.Cancel();
            _pushDelay = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                await PushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // новое сохранение — отправка позже, одна
            }
        });
    }

    /// <summary>2026-10-21 (ревью документов, A4): что ушло в PATCH — снимается из очереди только подтверждённое GET-проверкой.</summary>
    private sealed class SentSet
    {
        public readonly Dictionary<string, string?> Docs = new();
        public readonly Dictionary<string, string> Tombs = new();
        public readonly Dictionary<string, string> Paths = new();
        public readonly Dictionary<string, string> PathsWas = new();
        public bool Counters;
    }

    private static async Task<bool> PushAsync()
    {
        if (!HasPending)
            return true;
        if (!CanSync(out var api))
        {
            SetState(SyncState.LocalOnly);
            return false;
        }
        await PushGate.WaitAsync().ConfigureAwait(false);
        var company = CompanyKey;
        lock (Sync)
            _pushing = true;
        try
        {
            // 2026-10-21 (ревью документов, A4): свежий GET → слияние по updatedAt → PATCH → GET-проверка; чужая запись затёрла
            // нашу — ещё раз, не больше 3 раз.
            for (var round = 1; ; round++)
            {
                if (!HasPending)
                {
                    Succeeded();
                    return true;
                }
                SetState(SyncState.Saving);
                var data = await Request(api, HttpMethod.Get, null, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (!TryParseRemote(data, out var items, out var remoteProfile, out var damaged))
                {
                    PosLogger.Log("Документы клиента: ответ сервера не разобран — запись отложена.", "WARNING");
                    Failed(SyncState.LocalOnly);
                    return false;
                }
                var sent = new SentSet();
                JsonObject? body;
                lock (Sync)
                {
                    if (company != CompanyKey)
                        return false;
                    EnsureLocalLoaded();
                    ApplyRemote(items, remoteProfile, damaged);
                    body = HasPending ? BuildBodyLocked(items, remoteProfile, damaged, sent) : new JsonObject();
                    WriteLocal();
                }
                RaiseChanged();
                if (body == null)
                {
                    // Повреждённый на сервере ключ не перезаписываем (там могут быть чужие документы) — свои ждут на этом ПК.
                    SetNotice(T("Список документов на сервере повреждён — не перезаписываю его; изменения сохранены на этом ПК.",
                        "Сервердеги документтер тизмеси бузулган — үстүнөн жазбайм; өзгөртүүлөр ушул ПКда сакталды.",
                        "The document list on the server is damaged — not overwriting it; changes are kept on this PC.",
                        "Sunucudaki belge listesi bozuk — üzerine yazmıyorum; değişiklikler bu bilgisayarda saklandı.",
                        "Serverdagi hujjatlar ro'yxati buzilgan — ustidan yozmayman; o'zgarishlar shu kompyuterda saqlandi."));
                    Failed(SyncState.LocalOnly);
                    return false;
                }
                if (body.Count == 0)
                {
                    Succeeded(reschedule: false);
                    return true;
                }
                if (QuotesLarge)
                    PosLogger.Log($"Документы клиента: общий список КП и счетов большой ({QuotesBytes / 1024} КБ) — старые документы стоит удалить.", "WARNING");
                await Request(api, new HttpMethod("PATCH"), body, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                var check = await Request(api, HttpMethod.Get, null, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var confirmed = false;
                if (TryParseRemote(check, out var items2, out var profile2, out var damaged2))
                {
                    lock (Sync)
                    {
                        if (company != CompanyKey)
                            return false;
                        ApplyRemote(items2, profile2, damaged2);
                        confirmed = ConfirmedLocked(sent);
                        if (confirmed)
                            foreach (var (path, json) in sent.Paths)
                                ProfileWritten[path] = (json, sent.PathsWas.TryGetValue(path, out var was) ? was : "null", DateTime.UtcNow);
                        WriteLocal();
                    }
                }
                RaiseChanged();
                if (confirmed)
                {
                    LastSavedAt = DateTime.Now;
                    Succeeded();
                    ScheduleRecheck();
                    return true;
                }
                if (round >= MaxPushRounds)
                {
                    PosLogger.Log("Документы клиента: запись трижды затёрта другим устройством — повтор позже.", "WARNING");
                    Failed(SyncState.LocalOnly);
                    return false;
                }
                PosLogger.Log("Документы клиента: запись другого устройства затёрла нашу — сливаю ещё раз.", "INFO");
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            PosLogger.Log("Документы клиента: нет прав на общие настройки компании — документы сохранены только на этом ПК.", "INFO");
            SetState(SyncState.Forbidden);
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: на сервер не ушли ({ex.Message}) — сохранены на этом ПК, отправятся позже.", "INFO");
            Failed(SyncState.LocalOnly);
            return false;
        }
        finally
        {
            lock (Sync)
                _pushing = false;
            PushGate.Release();
        }
    }

    private static void Succeeded(bool reschedule = true)
    {
        lock (Sync)
        {
            _failures = 0;
            _pauseUntilUtc = DateTime.MinValue;
        }
        var pending = HasPending;
        SetState(pending ? SyncState.Saving : SyncState.Saved);
        // Пока шла запись, сохранили ещё — отправится следующей.
        if (pending && reschedule)
            SchedulePush(800);
        else if (pending)
            SetState(SyncState.LocalOnly);
    }

    /// <summary>2026-10-21 (ревью документов, A4): чужая запись по старой копии может лечь ПОСЛЕ нашей проверки — через 4 с ещё
    /// раз читаем список: «откатившийся» документ вернётся (см. <see cref="ApplyRemote"/>). Не больше 2 раз на одну правку.</summary>
    private static void ScheduleRecheck()
    {
        lock (Sync)
        {
            if (_recheckBudget <= 0)
                return;
            _recheckBudget--;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4000).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Документы клиента: перепроверка не удалась ({ex.Message}).", "INFO");
            }
        });
    }

    /// <summary>2026-10-21 (ревью документов, A3): сбой — повтор с паузой 15 с, 30 с, 1 мин … до 10 мин.</summary>
    private static void Failed(SyncState state)
    {
        int delayMs;
        lock (Sync)
        {
            _failures++;
            delayMs = (int)Math.Min(600_000, 15_000 * Math.Pow(2, Math.Min(6, _failures - 1)));
            _pauseUntilUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
        }
        SetState(state);
        SchedulePush(delayMs);
    }

    private static bool ConfirmedLocked(SentSet s)
    {
        foreach (var (id, at) in s.Docs)
            if (Pending.TryGetValue(id, out var p) && p.UpdatedAtRaw == at)
                return false;
        foreach (var (id, at) in s.Tombs)
            if (Tombs.TryGetValue(id, out var t) && t.UpdatedAt == at)
                return false;
        if (s.Paths.Count > 0)
        {
            var mine = ToJson(_profile);
            foreach (var (path, json) in s.Paths)
                if (ProfileDirty.Contains(path) && J(GetPath(mine, path)) == json)
                    return false;
        }
        return !(s.Counters && _countersDirty);
    }

    /// <summary>2026-10-21 (ревью документов, A3/A4): свежий список сервера. Своя правка того же или более старого updatedAt снимается
    /// (подтверждена / проиграла — сообщение владельцу); надгробие, которое сервер уже знает, снимается; надгробия старше 90 дней
    /// забываются. Повреждённый ключ — прежняя копия этого ПК (документы не пропадают из вида).</summary>
    private static void ApplyRemote(List<RemoteItem> items, JsonObject? remoteProfile, HashSet<string> damaged)
    {
        if (damaged.Count > 0)
            items = items.Concat(_remote.Where(i => damaged.Contains(i.Key))).ToList();
        var prevMap = NewestById(_remote);
        _remote = items;
        _remoteLoaded = true;
        _remoteGen++;
        QuotesBytes = QuotesSize(items);
        var map = NewestById(items);
        // 2026-10-21 (ревью документов, A4): версия на сервере «откатилась» — документ пропал без надгробия или стал старее, чем этот
        // ПК уже видел (чужая запись по старой копии затёрла новую). Удаление всегда оставляет надгробие, updatedAt только растёт —
        // значит, это потерянная запись: возвращаем самую новую известную версию (и надгробие) в очередь.
        foreach (var (id, prev) in prevMap)
        {
            if ((prev.Doc == null && !prev.IsTomb) || Pending.ContainsKey(id) || Tombs.ContainsKey(id))
                continue;
            map.TryGetValue(id, out var now);
            if (now == null ? prev.IsTomb || damaged.Contains(prev.Key) : Cmp(prev, now) <= 0)
                continue;
            if (prev.IsTomb)
                Tombs[id] = new Tomb { Id = id, UpdatedAt = prev.At ?? ClientDoc.FormatUtc(DateTime.UtcNow), Key = prev.Key, Number = prev.Number };
            else
                Pending[id] = prev.Doc!.Clone();
            PosLogger.Log($"Документы клиента: {prev.Doc?.Number ?? id} затёрт старой копией другого устройства — возвращаю.", "INFO");
        }
        foreach (var p in Pending.Values.ToList())
        {
            if (!map.TryGetValue(p.Id, out var r))
                continue;
            var c = Cmp(p, r);
            if (c > 0)
                continue;
            Pending.Remove(p.Id);
            if (c < 0)
                SetNotice(r.IsTomb
                    ? T($"Документ {p.Number} удалён на другом устройстве — правка этого ПК не сохранена.", $"{p.Number} документи башка түзмөктө өчүрүлгөн — бул ПКдагы өзгөртүү сакталган жок.",
                        $"Document {p.Number} was deleted on another device — this PC's edit was not saved.", $"{p.Number} belgesi başka bir cihazda silindi — bu bilgisayardaki düzenleme kaydedilmedi.",
                        $"{p.Number} hujjati boshqa qurilmada o'chirilgan — shu kompyuterdagi o'zgartirish saqlanmadi.")
                    : T($"Документ {p.Number} позже изменён на другом устройстве — оставлена та версия, правка этого ПК не сохранена.", $"{p.Number} документи кийин башка түзмөктө өзгөртүлгөн — ошол версия калды, бул ПКдагы өзгөртүү сакталган жок.",
                        $"Document {p.Number} was changed later on another device — that version is kept, this PC's edit was not saved.", $"{p.Number} belgesi daha sonra başka bir cihazda değiştirildi — o sürüm kaldı, bu bilgisayardaki düzenleme kaydedilmedi.",
                        $"{p.Number} hujjati keyinroq boshqa qurilmada o'zgartirilgan — o'sha versiya qoldi, shu kompyuterdagi o'zgartirish saqlanmadi."));
        }
        var cutoff = DateTime.UtcNow.AddDays(-TombstoneDays);
        foreach (var t in Tombs.Values.ToList())
        {
            if (ClientDoc.ParseUtc(t.UpdatedAt) is { } ts && ts < cutoff)
            {
                Tombs.Remove(t.Id);
                continue;
            }
            if (!map.TryGetValue(t.Id, out var r))
                continue;
            t.Key ??= r.Key;
            if (r.IsTomb)
                Tombs.Remove(t.Id);
            else if (Cmp(t, r) < 0)
            {
                Tombs.Remove(t.Id);
                SetNotice(T($"Документ {r.Doc?.Number} изменён на другом устройстве после удаления — он оставлен.", $"{r.Doc?.Number} документи өчүрүлгөндөн кийин башка түзмөктө өзгөртүлгөн — ал калтырылды.",
                    $"Document {r.Doc?.Number} was changed on another device after deletion — it is kept.", $"{r.Doc?.Number} belgesi silindikten sonra başka bir cihazda değiştirildi — korundu.",
                    $"{r.Doc?.Number} hujjati o'chirilgandan keyin boshqa qurilmada o'zgartirilgan — u qoldirildi."));
            }
        }
        if (!damaged.Contains(KeyProfile))
            ApplyProfile(remoteProfile);
    }

    /// <summary>2026-10-21 (ревью документов, A5): реквизиты сервера + поля, изменённые на этом ПК (ещё не ушедшие); счётчики —
    /// максимум; логотип — только этого ПК. Поле, которое на сервере уже такое же, больше не «изменённое».</summary>
    private static void ApplyProfile(JsonObject? remote)
    {
        if (remote == null)
            return;
        var mine = ToJson(_profile);
        // 2026-10-21 (ревью документов, A5): поле, записанное здесь < 10 мин назад, на сервере снова прежнее — вернуть своё.
        foreach (var (path, w) in ProfileWritten.ToList())
        {
            var now = J(GetPath(remote, path));
            if (DateTime.UtcNow - w.At > TimeSpan.FromMinutes(10) || now == w.Value || now != w.Was || ProfileDirty.Contains(path))
            {
                if (now != w.Value)
                    ProfileWritten.Remove(path);
                continue;
            }
            ProfileWritten.Remove(path);
            SetPath(mine, path, JsonNode.Parse(w.Value));
            ProfileDirty.Add(path);
            PosLogger.Log($"Документы клиента: реквизит «{path}» затёрт старой копией другого устройства — возвращаю.", "INFO");
        }
        foreach (var path in ProfileDirty.ToList())
            if (J(GetPath(remote, path)) == J(GetPath(mine, path)))
                ProfileDirty.Remove(path);
        // Счётчики — максимум: на сервере меньше нашего (в том числе после чужой записи) — отправить.
        _countersDirty = _profile.Counters.Any(kv => !(NumOf((remote["counters"] as JsonObject)?[kv.Key]) is { } r && r >= kv.Value));
        var merged = (JsonObject)remote.DeepClone();
        merged.Remove("logo");
        foreach (var path in ProfileDirty)
            SetPath(merged, path, GetPath(mine, path)?.DeepClone());
        if (FromJson(merged) is not { } next)
            return;
        MaxInto(next.Counters, _profile.Counters);
        next.LogoPath = _profile.LogoPath;
        if (string.IsNullOrWhiteSpace(next.Name))
            SeedProfile(next);
        _profile = next;
    }

    /// <summary>2026-10-21 (ревью документов, A4/A5): тело PATCH — список сервера как есть, поверх него свои новые правки и надгробия
    /// (по updatedAt), надгробия старше 90 дней убраны; только изменившиеся ключи. null — нужно писать в повреждённый ключ.</summary>
    private static JsonObject? BuildBodyLocked(List<RemoteItem> items, JsonObject? remoteProfile, HashSet<string> damaged, SentSet sent)
    {
        RenumberCollisionsLocked();
        var map = NewestById(items);
        var output = new Dictionary<string, JsonArray> { [KeyQuotes] = new(), [KeyWaybills] = new() };
        var handled = new HashSet<string>();
        var cutoff = DateTime.UtcNow.AddDays(-TombstoneDays);
        string KeyOf(ClientDoc d) => d.Kind == ClientDocKind.Waybill ? KeyWaybills : KeyQuotes;
        JsonNode DocNode(ClientDoc d) => JsonSerializer.SerializeToNode(d, Json)!;
        JsonNode TombNode(Tomb t)
        {
            var o = new JsonObject { ["id"] = t.Id, ["deleted"] = true, ["updatedAt"] = t.UpdatedAt };
            if (!string.IsNullOrWhiteSpace(t.Number))
                o["number"] = t.Number;   // как у телефона: номер удалённого не выдаётся повторно
            return o;
        }
        foreach (var item in items)
        {
            if (item.Id is not { Length: > 0 } id)
            {
                output[item.Key].Add(item.Raw.DeepClone());   // нечитаемый без id — как есть
                continue;
            }
            if (!ReferenceEquals(map[id], item) || !handled.Add(id))
                continue;   // повтор id — остаётся самый новый
            if (Pending.TryGetValue(id, out var p) && Cmp(p, item) > 0)
            {
                output[KeyOf(p)].Add(DocNode(p));
                sent.Docs[id] = p.UpdatedAtRaw;
            }
            else if (Tombs.TryGetValue(id, out var t) && !item.IsTomb && Cmp(t, item) > 0)
            {
                output[item.Key].Add(TombNode(t));
                sent.Tombs[id] = t.UpdatedAt;
            }
            else if (item.IsTomb && ClientDoc.ParseUtc(item.At) is { } ts && ts < cutoff)
            {
                // надгробие старше 90 дней — убираем
            }
            else
                output[item.Key].Add(item.Raw.DeepClone());
        }
        foreach (var p in Pending.Values.Where(p => !handled.Contains(p.Id)).OrderBy(p => p.Date))
        {
            output[KeyOf(p)].Insert(0, DocNode(p));
            sent.Docs[p.Id] = p.UpdatedAtRaw;
        }
        foreach (var t in Tombs.Values.Where(t => !handled.Contains(t.Id)))
        {
            output[t.Key ?? KeyQuotes].Add(TombNode(t));
            sent.Tombs[t.Id] = t.UpdatedAt;
        }

        var body = new JsonObject();
        foreach (var key in new[] { KeyQuotes, KeyWaybills })
        {
            var before = new JsonArray(items.Where(i => i.Key == key).Select(i => (JsonNode?)i.Raw.DeepClone()).ToArray());
            if (J(output[key]) == J(before))
                continue;
            if (damaged.Contains(key))
                return null;
            if (key == KeyQuotes)
                QuotesBytes = Encoding.UTF8.GetByteCount(J(output[key]));
            body[key] = output[key];
        }
        if (ProfileDirty.Count > 0 || _countersDirty)
        {
            if (damaged.Contains(KeyProfile))
                return null;
            var mine = ToJson(_profile);
            var prof = remoteProfile?.DeepClone() as JsonObject ?? new JsonObject();
            prof.Remove("logo");
            foreach (var path in ProfileDirty)
            {
                var v = GetPath(mine, path);
                sent.PathsWas[path] = J(GetPath(prof, path));
                SetPath(prof, path, v?.DeepClone());
                sent.Paths[path] = J(v);
            }
            if (prof["counters"] is not JsonObject counters)
            {
                counters = new JsonObject();
                prof["counters"] = counters;
            }
            foreach (var (k, v) in _profile.Counters)
                if (NumOf(counters[k]) is not { } r || r < v)
                    counters[k] = v;
            sent.Counters = _countersDirty;
            body[KeyProfile] = prof;
        }
        return body;
    }

    /// <summary>Ответ GET: {"settings": {...}}. Ключи — массивы (или строка с JSON-массивом — так их может положить телефон).
    /// 2026-10-21 (ревью документов, мелочи): каждый элемент — как есть (нечитаемый не теряется); ключ, который не разобрать
    /// целиком, — в <paramref name="damaged"/> (его не перезаписываем).</summary>
    internal static bool TryParseRemote(JsonElement data, out List<RemoteItem> items, out JsonObject? profile, out HashSet<string> damaged)
    {
        items = new();
        profile = null;
        damaged = new();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings)
            || settings.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return false;
        if (settings.ValueKind == JsonValueKind.Null)
            return true;
        foreach (var key in new[] { KeyQuotes, KeyWaybills })
        {
            if (!settings.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
                continue;
            JsonNode? parsed = null;
            try
            {
                if (node.ValueKind == JsonValueKind.String)
                {
                    var raw = node.GetString();
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    parsed = JsonNode.Parse(raw);
                }
                else
                    parsed = JsonNode.Parse(node.GetRawText());
            }
            catch (JsonException ex)
            {
                PosLogger.Log($"Документы клиента: «{key}» на сервере не прочитан ({ex.Message}).", "WARNING");
            }
            if (parsed is not JsonArray arr)
            {
                damaged.Add(key);
                continue;
            }
            foreach (var n in arr)
                if (n != null)
                    items.Add(ParseItem(key, n.DeepClone()));
        }
        if (settings.TryGetProperty(KeyProfile, out var prof) && prof.ValueKind != JsonValueKind.Null)
        {
            if (prof.ValueKind == JsonValueKind.Object)
                profile = JsonNode.Parse(prof.GetRawText()) as JsonObject;
            else
                damaged.Add(KeyProfile);
        }
        return true;
    }

    private static bool CanSync(out NurMarketApiClient? api)
    {
        api = null;
        if (CloudDisabled)
            return false;
        if (TransportOverride != null)
            return true;
        api = Api();
        return api != null;
    }

    private static Task<JsonElement> Request(NurMarketApiClient? api, HttpMethod method, JsonObject? body, TimeSpan timeout, CancellationToken ct = default) =>
        TransportOverride is { } t ? t(method, body) : api!.RequestAsync(method, CloudPath, body, null, ct, timeout);

    private static NurMarketApiClient? Api()
    {
        if (CloudDisabled || ServerOutageMonitor.IsOutage || CompanyInfoService.LastCompany?.Id is not { Length: > 0 })
            return null;
        return SettingsCloudSync.ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;
    }

    private static void SetNotice(string text)
    {
        Notice = text;
        PosLogger.Log("Документы клиента: " + text, "INFO");
    }

    private static void SetState(SyncState state)
    {
        if (State == state)
            return;
        State = state;
        RaiseChanged();
    }

    private static void RaiseChanged()
    {
        try
        {
            _ = UiDispatcherHolder.InvokeAsync(() =>
            {
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Документы клиента: окно не обновилось ({ex.Message}).", "WARNING");
                }
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Документы клиента: событие не отправлено ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Только для стенда: забыть загруженное (другая папка/компания).</summary>
    public static void ResetForTests()
    {
        lock (Sync)
            _loadedFor = null;
    }

    public static bool RemoteLoaded
    {
        get { lock (Sync) return _remoteLoaded; }
    }
}
