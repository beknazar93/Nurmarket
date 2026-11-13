using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-31, ТЗ Умара «функции маркетплейса — с сервера»: платные функции кассы, включённые компании НА СЕРВЕРЕ
/// (GET api/users/company/addons/ → [{code:"kassa-loyalty", active, until}]). Второй источник доступа рядом с ключами
/// активации (LicenseKeys, они работают как раньше): функция открыта, если есть действующий ключ ИЛИ сервер включил
/// «kassa-&lt;id&gt;». Место проверки одно — свойства *Unlocked в UserPreferences (читают все окна и TariffGate): они отдают
/// «ключ || сервер» (<see cref="IsGranted"/>), а в файл настроек и в облако пишется только «ключ» (<see cref="RawView"/>),
/// иначе включённое сервером навсегда осталось бы у кассы после выключения.
///
/// • Загрузка: после входа в компанию, затем раз в 15 минут и при открытии окна «Маркетплейс»; ответ + время загрузки — в кэш
///   (server-addons.json, AtomicFile). Нет связи / 401 / 404 / 5xx — работаем по ключам и кэшу, ошибок на экране нет.
/// • Кэш принадлежит компании: сверяется с LastDataAccountKey («c:&lt;id компании&gt;», его ведёт AccountDataIsolation); кэш другой
///   компании ничего не открывает, при смене компании файл стирается вместе с остальными данными аккаунта.
/// • Срок: функция с «until» в прошлом закрыта всегда (в том числе по старому кэшу — кэш старше 7 дней доступ не продлевает);
///   бессрочная (until: null) держится по кэшу.
/// • Выключили на сервере (active:false или кода нет) и ключа нет — функция закрывается при следующей загрузке; данные функции
///   (бонусы, табель…) не удаляются — просто скрыты.
/// </summary>
public static class ServerAddons
{
    public const string CodePrefix = "kassa-";

    /// <summary>Карточки Маркетплейса, у которых есть код на сервере (код = «kassa-» + id карточки).</summary>
    public static readonly string[] Slugs =
    {
        "loyalty", "timesheet", "analytics", "pricetag", "bulktag", "labeleditor", "scales", "export", "telegram", "shiftstats", "language",
        "repair", "trade_in", // 2026-11-13: «Ремонт и сервис», «Trade-in / скупка» (коды сервера «kassa-repair», «kassa-trade_in»; сервер их пока не выдаёт — подключает поддержка)
    };

    /// <summary>Период опроса сервера.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    /// <remarks>2026-11-13 (сервер «ev1311»): <paramref name="Source"/> — откуда подключена функция: «key» (по ключу), «trial» (пробный период), «support» (включила поддержка);
    /// null — поля нет (старый сервер) или значение другое (например «purchase»): подписи нет.</remarks>
    public sealed record AddonEntry(string Code, bool Active, string? Until, string? Source = null);

    /// <summary>2026-11-06 («купил на сайте — программа подхватывает»): итог немедленной проверки покупок.</summary>
    public enum PurchaseCheckStatus
    {
        /// <summary>Ответ сервера получен (новые функции — в <see cref="PurchaseCheck.NewSlugs"/>, может быть пусто).</summary>
        Checked,
        /// <summary>Слишком часто (не чаще раза в 20 секунд) или уже идёт загрузка — остался прежний список.</summary>
        Throttled,
        /// <summary>Нет связи, вход не выполнен, ошибка сервера — работаем по ключам и кэшу.</summary>
        Failed,
    }

    /// <summary>Что дала проверка: статус и коды (slug) функций, которые открылись именно сейчас.</summary>
    public sealed record PurchaseCheck(PurchaseCheckStatus Status, IReadOnlyList<string> NewSlugs)
    {
        public static readonly PurchaseCheck Skipped = new(PurchaseCheckStatus.Throttled, Array.Empty<string>());
        public static readonly PurchaseCheck NotDone = new(PurchaseCheckStatus.Failed, Array.Empty<string>());
    }

    private sealed class Snap
    {
        public string Account = "";
        public DateTime LoadedAtUtc;
        public List<AddonEntry> Items = new();
    }

    /// <summary>Клиент API сервера; не задан — берётся тот же, что у бонусов (ServerLoyalty.ApiProvider, ставит App при запуске).</summary>
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    /// <summary>Только для стенда проверки: другая папка данных вместо %APPDATA%.</summary>
    public static string? DirOverride { get; set; }

    /// <summary>Только для стенда проверки: «сейчас» (UTC).</summary>
    public static Func<DateTime>? NowUtcOverride { get; set; }

    /// <summary>Список включённого на сервере изменился (после загрузки). Приходит из любого потока.</summary>
    public static event Action? Changed;

    private static volatile Snap? _snap;
    // 2026-11-13 (сервер «ev1311»: ETag у users/company/addons/): ETag последнего 200 и компания, чей он; 304 — список тот же, разбор и запись кэша не нужны.
    private static string? _etag;
    private static string? _etagAccount;
    private static volatile bool _etagSupported;
    private static int _diskLoaded;
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    /// <summary>2026-10-31 (приёмка 1.17.68): компания последней попытки загрузки; другая компания (смена аккаунта) обходит паузу между попытками.</summary>
    private static string? _attemptCompany;
    /// <summary>Запрос пришёл, пока шла другая загрузка — после неё проверяем, не сменилась ли компания.</summary>
    private static int _rerunRequested;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object DiskLock = new();
    private static Timer? _timer;
    private static string _lastIssue = "";

    private static DateTime NowUtc => NowUtcOverride?.Invoke() ?? DateTime.UtcNow;

    private static NurMarketApiClient? Api =>
        (ApiProvider ?? ServerLoyalty.ApiProvider)?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static string CachePath => Path.Combine(
        DirOverride ?? Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName),
        "server-addons.json");

    // ───────────────────────────── проверка доступа ─────────────────────────────

    [ThreadStatic] private static int _rawDepth;

    /// <summary>Пока открыт, свойства *Unlocked отдают только «ключ» (без сервера) — для записи настроек на диск и в облако.</summary>
    public static IDisposable RawView()
    {
        _rawDepth++;
        return new RawScope();
    }

    private sealed class RawScope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            if (_rawDepth > 0)
                _rawDepth--;
        }
    }

    /// <summary>Сервер включил функцию «kassa-&lt;slug&gt;» для компании, чьи данные сейчас на кассе (<paramref name="account"/> =
    /// UserPreferences.LastDataAccountKey), и срок не вышел.</summary>
    public static bool IsGranted(string slug, string? account)
    {
        if (_rawDepth > 0 || string.IsNullOrEmpty(account))
            return false;
        try
        {
            var snap = Current(account);
            if (snap is null)
                return false;
            var code = CodePrefix + slug;
            foreach (var item in snap.Items)
                if (string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
                    return IsActive(item);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>То же для текущей компании кассы.</summary>
    public static bool IsGranted(string slug) => IsGranted(slug, UserPreferences.Instance.LastDataAccountKey);

    /// <summary>Открыта сервером, а ключа на этой кассе нет — на плитке «Подключено» вместо «Куплено».</summary>
    public static bool IsServerOnly(string slug) => IsGranted(slug) && !UserPreferences.Instance.IsKeyUnlocked(slug);

    /// <summary>2026-11-13: сервер отдаёт ETag у addons — условные запросы работают (304), проверку можно делать чаще.</summary>
    public static bool EtagSupported => _etagSupported;

    /// <summary>2026-11-13: откуда подключена функция на сервере: «key» / «trial» / «support» / «promo» (2026-11-13: промокод, ответ Умара); null — не включена, поля нет или значение неизвестное.</summary>
    public static string? GrantedSource(string slug)
    {
        var snap = Current(UserPreferences.Instance.LastDataAccountKey);
        var item = snap?.Items.FirstOrDefault(i => string.Equals(i.Code, CodePrefix + slug, StringComparison.OrdinalIgnoreCase));
        return item is not null && IsActive(item) && item.Source is "key" or "trial" or "support" or "promo" ? item.Source : null;
    }

    /// <summary>2026-11-13: подпись источника подключения на 5 языках; пусто — источник неизвестен (подписи нет).</summary>
    public static string SourceLabel(string? source) => source switch
    {
        "key" => Tr.T("по ключу", "ачкыч боюнча", "by key", "anahtarla", "kalit bo'yicha"),
        "trial" => Tr.T("пробный период", "сыноо мөөнөтү", "trial period", "deneme süresi", "sinov muddati"),
        "support" => Tr.T("включено поддержкой", "колдоо кызматы күйгүздү", "enabled by support", "destek tarafından açıldı", "qo'llab-quvvatlash yoqdi"),
        "promo" => Tr.T("по промокоду", "промокод менен", "by promo code", "promosyon koduyla", "promokod orqali"),
        _ => "",
    };

    /// <summary>Срок функции с сервера: null — бессрочно или не включена.</summary>
    public static DateOnly? GrantedUntil(string slug)
    {
        var snap = Current(UserPreferences.Instance.LastDataAccountKey);
        var item = snap?.Items.FirstOrDefault(i => string.Equals(i.Code, CodePrefix + slug, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : ParseUntil(item.Until);
    }

    /// <summary>Срок: сервер учёл «until» при ответе, но касса может долго работать без связи — поэтому «until» в прошлом
    /// закрывает функцию и по кэшу (старше 7 дней кэш доступ не продлевает); бессрочная держится по кэшу.</summary>
    private static bool IsActive(AddonEntry item)
    {
        if (!item.Active)
            return false;
        var until = ParseUntil(item.Until);
        if (until is null)
            return true;
        var today = DateOnly.FromDateTime(NowUtcOverride is null ? DateTime.Now : NowUtc.ToLocalTime());
        return until.Value >= today;
    }

    private static DateOnly? ParseUntil(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null; // непонятный срок не считаем сроком: «active» от сервера уже учёл его
    }

    private static Snap? Current(string account)
    {
        if (Volatile.Read(ref _diskLoaded) == 0)
            LoadFromDisk();
        var snap = _snap;
        if (snap is null || !string.Equals(snap.Account, account, StringComparison.OrdinalIgnoreCase))
            return null;
        return snap;
    }

    // ───────────────────────────── кэш на диске ─────────────────────────────

    private static void LoadFromDisk()
    {
        lock (DiskLock)
        {
            if (Volatile.Read(ref _diskLoaded) != 0)
                return;
            try
            {
                var text = AtomicFile.ReadAllText(CachePath);
                if (!string.IsNullOrWhiteSpace(text) && _snap is null)
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    var snap = new Snap
                    {
                        Account = root.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "",
                        LoadedAtUtc = root.TryGetProperty("loadedAtUtc", out var l) && l.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue,
                    };
                    if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        snap.Items = ParseItems(items) ?? new List<AddonEntry>();
                    _snap = snap;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Платные функции с сервера: кэш не прочитан ({ex.Message}) — работаем по ключам.", "WARNING");
            }
            finally
            {
                Volatile.Write(ref _diskLoaded, 1);
            }
        }
    }

    private static void SaveToDisk(Snap snap)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                v = 1,
                account = snap.Account,
                loadedAtUtc = snap.LoadedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                items = snap.Items.Select(i => new { code = i.Code, active = i.Active, until = i.Until, source = i.Source }).ToList(),
            });
            AtomicFile.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Платные функции с сервера: кэш не записан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Забыть всё в памяти (смена компании: файл стирает AccountDataIsolation).</summary>
    public static void ResetMemory()
    {
        lock (DiskLock)
        {
            _boostUntilUtc = DateTime.MinValue; // 2026-11-06: частые проверки после заявки относились к прежней компании
            _boostTimer?.Dispose();
            _boostTimer = null;
        }
        _snap = null;
        _etag = null;
        _etagAccount = null;
        _etagSupported = false;
        Volatile.Write(ref _diskLoaded, 1);
        _lastAttemptUtc = DateTime.MinValue;
    }

    /// <summary>Только для стенда проверки: заново прочитать файл (как после перезапуска).</summary>
    public static void ReloadFromDiskForStand()
    {
        _snap = null;
        _etag = null;
        _etagAccount = null;
        Volatile.Write(ref _diskLoaded, 0);
        _lastAttemptUtc = DateTime.MinValue;
    }

    // ───────────────────────────── загрузка с сервера ─────────────────────────────

    private static List<AddonEntry>? ParseItems(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<AddonEntry>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.String)
                continue;
            var code = (c.GetString() ?? "").Trim();
            // Коды веб-админки (analytics, procurement, clients…) касса пропускает: нужны только «kassa-…».
            if (!code.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var active = el.TryGetProperty("active", out var a)
                && (a.ValueKind == JsonValueKind.True || (a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            string? until = el.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            string? source = el.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.String ? so.GetString()?.Trim().ToLowerInvariant() : null;
            list.Add(new AddonEntry(code, active, until, source));
        }
        return list;
    }

    // ───────────── 2026-11-06: после заявки «Подключить» — частые проверки на время оплаты ─────────────

    /// <summary>Период частых проверок после заявки на подключение и сколько они длятся (владелец платит через поддержку — функция включается в течение минут).</summary>
    public static readonly TimeSpan BoostEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan BoostFor = TimeSpan.FromMinutes(15);

    private static DateTime _boostUntilUtc = DateTime.MinValue;
    private static Timer? _boostTimer;

    /// <summary>Идут ли частые проверки (раз в минуту, 15 минут после заявки).</summary>
    public static bool IsBoosted => NowUtc < _boostUntilUtc;

    /// <summary>Заявка на подключение отправлена: следующие 15 минут сервер опрашивается раз в минуту (потом — обычные 15 минут). Повторная заявка продлевает срок.</summary>
    public static void BoostAfterRequest()
    {
        lock (DiskLock)
        {
            _boostUntilUtc = NowUtc + BoostFor;
            _boostTimer ??= new Timer(_ => BoostTick(), null, BoostEvery, BoostEvery);
        }
    }

    internal static void BoostTick()
    {
        if (NowUtc >= _boostUntilUtc)
        {
            lock (DiskLock)
            {
                if (NowUtc >= _boostUntilUtc)
                {
                    _boostTimer?.Dispose();
                    _boostTimer = null;
                }
            }
            return;
        }
        _ = RefreshAsync(force: true);
    }

    /// <summary>Запуск после входа в компанию (CompanyInfoService.RefreshAsync): первая загрузка и таймер раз в 15 минут.</summary>
    public static void AfterCompanyRefresh()
    {
        StartAutoRefresh();
        _ = RefreshAsync();
        NurMarketKassa.Services.Api.ServerCapabilities.WarmUp(); // 2026-11-05: признаки сервера (capabilities) — заранее, чтобы синхронные проверки сразу знали ответ
    }

    public static void StartAutoRefresh()
    {
        if (_timer is not null)
            return;
        lock (DiskLock)
        {
            _timer ??= new Timer(_ => _ = RefreshAsync(force: true), null, RefreshEvery, RefreshEvery);
        }
    }

    /// <summary>GET users/company/addons/. false — запрос не состоялся или не удался (кэш и ключи остаются как были).
    /// <paramref name="force"/>: окно «Маркетплейс» просит свежие данные сразу (не чаще раза в 20 секунд); без него — не чаще раза в 15 минут.</summary>
    public static async Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default) =>
        (await RefreshDetailedAsync(force, ct).ConfigureAwait(false)).Status == PurchaseCheckStatus.Checked;

    /// <summary>2026-11-06: то же, но с итогом — какие функции открылись только что («Подключено новых функций: N» в окне «Маркетплейс»).</summary>
    public static async Task<PurchaseCheck> RefreshDetailedAsync(bool force = false, CancellationToken ct = default)
    {
        var minGap = force ? TimeSpan.FromSeconds(20) : RefreshEvery - TimeSpan.FromSeconds(30);
        // Кэш принадлежит компании: без номера компании (ответ /company/ ещё не пришёл) ключ кэша назвать нечем.
        var companyId = CompanyInfoService.LastCompany?.Id;
        // 2026-10-31 (приёмка 1.17.68): после смены аккаунта пауза между попытками не действует — функции новой компании нужны сразу, а не через 15 минут.
        var sameCompany = string.Equals(companyId, _attemptCompany, StringComparison.Ordinal);
        if (sameCompany && NowUtc - _lastAttemptUtc < minGap)
            return PurchaseCheck.Skipped;
        if (Api is not { } api || ServerOutageMonitor.IsOutage)
            return PurchaseCheck.NotDone;
        if (string.IsNullOrWhiteSpace(companyId))
            return PurchaseCheck.NotDone;
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Volatile.Write(ref _rerunRequested, 1); // идёт загрузка (возможно, прежней компании): по её окончании проверим ещё раз
            return PurchaseCheck.Skipped;
        }
        try
        {
            if (sameCompany && NowUtc - _lastAttemptUtc < minGap)
                return PurchaseCheck.Skipped;
            _lastAttemptUtc = NowUtc;
            _attemptCompany = companyId;
            var before = GrantedSet();
            JsonElement data;
            var account = AppPaths.CompanyKey(companyId);
            string? newTag;
            try
            {
                // 2026-10-31: общий клиент NurMarketApiClient — через него идут Bearer, щит 429 (Retry-After) и счёт сбоев сервера.
                // 2026-11-13 (сервер «ev1311»: ETag у addons): If-None-Match, если список этой компании уже в памяти; 304 — ничего не меняется и ничего не разбирается.
                var sendTag = _etag is not null && string.Equals(_etagAccount, account, StringComparison.Ordinal) && Current(account) is not null ? _etag : null;
                int status;
                (data, status, newTag) = await api.GetConditionalAsync("api/users/company/addons/", null, sendTag, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (status == 304 && sendTag is not null && Current(account) is { } same)
                {
                    same.LoadedAtUtc = NowUtc; // кэш подтверждён сервером (на диск не пишем — список тот же)
                    _etagSupported = true;
                    _lastIssue = "";
                    return new PurchaseCheck(PurchaseCheckStatus.Checked, Array.Empty<string>());
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return PurchaseCheck.NotDone;
            }
            catch (Exception ex)
            {
                // 401/403/404/405/5xx/нет связи — не ошибка для кассира: ключи и кэш остаются, в журнал — один раз на вид сбоя.
                var issue = ex is ApiException api2 ? $"HTTP {api2.StatusCode}" : ex.GetType().Name;
                if (issue != _lastIssue)
                {
                    _lastIssue = issue;
                    PosLogger.Log($"Платные функции с сервера не загружены ({issue}) — работаем по ключам и кэшу.", "WARNING");
                }
                return PurchaseCheck.NotDone;
            }

            var arr = data.ValueKind == JsonValueKind.Array ? data
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r
                : default;
            var items = ParseItems(arr);
            if (items is null)
            {
                if (_lastIssue != "bad-format")
                {
                    _lastIssue = "bad-format";
                    PosLogger.Log("Платные функции с сервера: неожиданный ответ addons — работаем по ключам и кэшу.", "WARNING");
                }
                return PurchaseCheck.NotDone;
            }

            _lastIssue = "";
            var snap = new Snap { Account = account /* 2026-11-05: с префиксом стенда, как LastDataAccountKey */, LoadedAtUtc = NowUtc, Items = items };
            _snap = snap;
            _etag = string.IsNullOrWhiteSpace(newTag) ? null : newTag; // 2026-11-13: нет ETag (старый сервер) — запросы безусловные, как раньше
            _etagAccount = account;
            _etagSupported = _etag is not null;
            Volatile.Write(ref _diskLoaded, 1);
            SaveToDisk(snap);
            var after = GrantedSet();
            if (!before.SetEquals(after))
            {
                PosLogger.Log($"Платные функции с сервера: включено {after.Count} ({string.Join(", ", after.OrderBy(s => s))}).", "INFO");
                RaiseChanged();
            }
            // 2026-11-06: что открылось именно сейчас (в списке до загрузки этого не было) — для сообщения «Подключено новых функций».
            return new PurchaseCheck(PurchaseCheckStatus.Checked, after.Where(a => !before.Contains(a)).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally
        {
            Gate.Release();
            // 2026-10-31 (приёмка 1.17.68): аккаунт сменился во время загрузки — функции новой компании запрашиваем сразу.
            if (Interlocked.Exchange(ref _rerunRequested, 0) == 1 && !ct.IsCancellationRequested
                && !string.Equals(CompanyInfoService.LastCompany?.Id, companyId, StringComparison.Ordinal))
                _ = RefreshAsync(force, ct);
        }
    }

    private static HashSet<string> GrantedSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in Slugs)
            if (IsGranted(slug))
                set.Add(slug);
        return set;
    }

    private static void RaiseChanged()
    {
        try
        {
            TariffGate.RaisePacksChanged(); // меню и разделы пересобираются
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Платные функции с сервера: обработчик изменения упал ({ex.Message}).", "WARNING");
        }
    }

    // ───────────────────────────── заявка «Подключить» ─────────────────────────────

    public enum RequestStatus { Sent, PhoneTooShort, TooMany, Unavailable, NoConnection, NotSignedIn, Failed }

    public sealed record RequestResult(RequestStatus Status, string? SupportPhone = null);

    /// <summary>Телефон годится для заявки: не меньше 9 цифр (так же проверяет сервер).</summary>
    public static bool PhoneLooksValid(string? phone) => (phone ?? "").Count(char.IsDigit) >= 9;

    /// <summary>POST api/support/addon-requests/ — заявка в поддержку тем же запросом, что у сайта. Оплата пока через поддержку:
    /// перезвонят, примут оплату и включат функцию на сервере. 201 {ok:true}; 400 — телефон короче 9 цифр; 429 — больше 5 заявок
    /// за 10 минут; 503 — заявки не принимаются, в ответе телефон поддержки.</summary>
    public static async Task<RequestResult> SubmitRequestAsync(string slug, string title, string price, string phone, string comment = "", CancellationToken ct = default)
    {
        if (Api is not { } api)
            return new RequestResult(RequestStatus.NotSignedIn);
        var body = new Dictionary<string, object?>
        {
            ["item"] = CodePrefix + slug,
            ["title"] = title,
            ["price"] = price,
            ["phone"] = phone.Trim(),
            ["comment"] = comment,
        };
        try
        {
            var data = await api.RequestAsync(HttpMethod.Post, "api/support/addon-requests/", body, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var ok = data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("ok", out var o) || o.ValueKind != JsonValueKind.False;
            PosLogger.Log($"Заявка на подключение функции {CodePrefix}{slug} отправлена в поддержку.", "INFO");
            if (ok)
                BoostAfterRequest(); // 2026-11-06: пока владелец платит, проверяем сервер раз в минуту (15 минут)
            return new RequestResult(ok ? RequestStatus.Sent : RequestStatus.Failed);
        }
        catch (ApiException ex)
        {
            switch (ex.StatusCode)
            {
                case 400:
                    return new RequestResult(RequestStatus.PhoneTooShort);
                case 429:
                    return new RequestResult(RequestStatus.TooMany);
                case 503:
                    string? phoneOfSupport = null;
                    if (ex.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.String)
                        phoneOfSupport = ph.GetString();
                    return new RequestResult(RequestStatus.Unavailable, phoneOfSupport);
                case 401:
                    return new RequestResult(RequestStatus.NotSignedIn);
                default:
                    PosLogger.Log($"Заявка на подключение функции {CodePrefix}{slug} не принята (HTTP {ex.StatusCode}).", "WARNING");
                    return new RequestResult(ex.StatusCode is >= 500 ? RequestStatus.NoConnection : RequestStatus.Failed);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            return new RequestResult(RequestStatus.NoConnection);
        }
    }
}
