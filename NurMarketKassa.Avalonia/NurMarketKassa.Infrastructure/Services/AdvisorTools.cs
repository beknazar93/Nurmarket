using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27: запрос данных раздела у программы (провайдер живёт в программе владельца и берёт данные из её сервисов и кэшей).</summary>
/// <param name="Area">Область данных раздела (<see cref="AdvisorSection.DataArea"/>).</param>
/// <param name="Section">Раздел, который просил ИИ.</param>
/// <param name="PurchaseVisible">Закупочные цены и прибыль видны этому пользователю (иначе провайдер их не кладёт, а очистка вырежет).</param>
public sealed record AdvisorDataRequest(string Area, AdvisorSection Section, DateTime From, DateTime To, string PeriodLabel, string? Query, int Top, bool PurchaseVisible,
    Func<string, bool> HasPermission);

/// <summary>2026-11-27: что программа даёт инструментам советника. В программе — OwnerShellWindow/AiAdvisorWindow, в тестах — подставные функции.</summary>
public sealed class AdvisorToolHost
{
    /// <summary>Право текущего пользователя (PosPermissions.*). По умолчанию — нет (безопасно).</summary>
    public Func<string, bool> HasPermission { get; init; } = _ => false;

    /// <summary>Тумблер Нур Советника (AdvisorCapabilities.IsOn).</summary>
    public Func<string, bool> CapOn { get; init; } = AdvisorCapabilities.IsOn;

    /// <summary>Пункт меню виден сейчас (тариф, признак сервера, модуль, «Разделы меню», права) — из построенного меню владельца. null — не проверять.</summary>
    public Func<string, bool>? InMenu { get; init; }

    /// <summary>Открыть раздел/вкладку по id словаря и вернуть ФАКТ (OwnerShellWindow.OpenNavTargetAsync).</summary>
    public Func<string, CancellationToken, Task<NavOutcome>>? Open { get; init; }

    /// <summary>Данные раздела (только чтение). Возвращает любой объект — он будет очищен и ужат.</summary>
    public Func<AdvisorDataRequest, CancellationToken, Task<object?>>? Data { get; init; }

    /// <summary>Описание функции из Маркетплейса (что делает, как включить, для кого, цена/статус) по слову-подсказке раздела.</summary>
    public Func<string, object?>? Marketplace { get; init; }

    /// <summary>«Что нового»: новые функции Маркетплейса и разделы программы.</summary>
    public Func<object?>? WhatsNew { get; init; }

    /// <summary>Окно программы «Нур-советник предлагает: … Выполнить?» — true, только если владелец нажал «Выполнить».</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; init; }

    /// <summary>Безопасные действия с подтверждением (черновик акции, черновик заказа поставщику). По умолчанию ВЫКЛЮЧЕНЫ — инструмент не объявляется.</summary>
    public Func<string, CancellationToken, Task<string>>? Draft { get; init; }

    public bool ActionsEnabled { get; init; }

    /// <summary>Сегодня (для периодов; в тестах — фиксированная дата).</summary>
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;
}

/// <summary>2026-11-27, владелец: «Дай Нур-советнику доступы ко всем разделам … Полный доступ» — инструменты (function calling Gemini) чата и живого
/// разговора и их диспетчер. Правила (тест на каждое — AdvisorToolsTests):
/// (а) права: раздел и данные — только по правам пользователя; закупка и прибыль — только при праве «Аналитика» и тумблере «priv.purchase»;
/// (б) секреты, PIN, токены, карты, полные телефоны в ИИ не уходят (<see cref="AdvisorRedactor"/>);
/// (в) только чтение и переход: инструментов записи нет; любое имя «изменить / удалить / отправить …» — отказ; черновики — только с окном подтверждения
///     и только если программа их включила (<see cref="AdvisorToolHost.ActionsEnabled"/>, по умолчанию нет);
/// (г) в журнал пишутся имя инструмента, раздел, время и размер — без содержимого;
/// (д) не больше <see cref="MaxCallsPerAnswer"/> вызовов на ответ, каждый — не дольше <see cref="CallTimeout"/>, ответ — не длиннее <see cref="MaxResultChars"/>;
/// (е) <see cref="Used"/> — какие разделы использованы (строка «Использовано: …» под ответом).</summary>
public sealed class AdvisorToolDispatcher
{
    public const int MaxCallsPerAnswer = 6;
    public const int MaxRounds = 3;
    public const int MaxResultChars = 6000;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5.5);

    /// <summary>Сколько времени добавить к общему сроку ответа, если ИИ вызывал инструменты (второй запрос к модели + сами инструменты).</summary>
    public static readonly TimeSpan ExtraBudget = TimeSpan.FromSeconds(8);

    public const string ListSections = "list_sections", OpenSection = "open_section", DescribeSection = "describe_section", GetSectionData = "get_section_data",
        WhatsNewTool = "whats_new", PrepareDraft = "prepare_draft";

    /// <summary>Инструменты только чтения и перехода — их объявляет программа всегда.</summary>
    public static readonly IReadOnlyList<string> ReadTools = new[] { ListSections, OpenSection, DescribeSection, GetSectionData, WhatsNewTool };

    /// <summary>«Действия с подтверждением» — отдельный список, по умолчанию не объявляются (<see cref="AdvisorToolHost.ActionsEnabled"/>).</summary>
    public static readonly IReadOnlyList<string> ConfirmTools = new[] { PrepareDraft };

    private static readonly string[] WritePrefixes =
    {
        "set", "update", "delete", "remove", "create", "add", "send", "pay", "sell", "refund", "return", "change", "edit", "apply", "cancel", "execute", "write",
        "post", "put", "patch", "save", "publish", "enable", "disable", "toggle", "move", "transfer", "close", "open_shift", "make", "run", "reply", "notify",
    };

    /// <summary>Журнал (по умолчанию PosLogger). Только имя инструмента, раздел, время, размер — без данных (правило «г»).</summary>
    public static Action<string, string> Log { get; set; } = (m, c) => PosLogger.Log(m, c);

    private readonly AdvisorToolHost _host;
    private readonly List<string> _used = new();
    private int _calls;

    public AdvisorToolDispatcher(AdvisorToolHost host) => _host = host;

    /// <summary>Сколько вызовов уже было в этом ответе.</summary>
    public int Calls => _calls;

    /// <summary>Разделы/инструменты, которые использовал советник (по порядку, без повторов) — для строки «Использовано: …».</summary>
    public IReadOnlyList<string> Used
    {
        get
        {
            lock (_used)
                return _used.ToList();
        }
    }

    private void MarkUsed(string label)
    {
        lock (_used)
        {
            if (!_used.Contains(label))
                _used.Add(label);
        }
    }

    /// <summary>Закупка и прибыль видны: право «Аналитика» (как разделы «Прибыль и деньги», «ABC») и тумблер «priv.purchase».</summary>
    public bool PurchaseVisible => Safe(() => _host.HasPermission(PosPermissions.ViewAnalytics)) && Safe(() => _host.CapOn("priv.purchase"));

    private static bool Safe(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Доступ к разделу ──────────────────────────────────────────────────────────────────────────────────────

    public enum AccessKind { Ok, NoPermission, Disabled, NotConnected }

    public sealed record Access(AccessKind Kind, string Reason)
    {
        public bool Ok => Kind == AccessKind.Ok;
    }

    private static string RightName(string? permission) => permission switch
    {
        PosPermissions.ViewProducts => "Склад",
        PosPermissions.ViewAnalytics => "Аналитика",
        PosPermissions.ViewClients => "Клиенты",
        PosPermissions.ViewSettings => "Настройки",
        PosPermissions.ViewSales => "Продажи",
        PosPermissions.ViewOrders => "Заказы",
        PosPermissions.EmployeeReturn => "Возврат",
        PosPermissions.EditProducts => "Склад: редактирование",
        _ => permission ?? "",
    };

    /// <summary>Можно ли этому пользователю раздел: право → тумблер советника → виден в меню (тариф, модуль, «Разделы меню»).</summary>
    public Access Check(AdvisorSection section, bool forData)
    {
        if (section.Permission is { } p && !Safe(() => _host.HasPermission(p)))
            return new Access(AccessKind.NoPermission, $"НЕТ ПРАВА: у пользователя нет доступа «{RightName(p)}» к разделу «{section.TitleRu}». Доступ даёт владелец на сайте NurCRM: Сотрудники → доступы. Скажи это одной фразой, данных не придумывай.");
        if (forData && section.Capability is { } c && !Safe(() => _host.CapOn(c)))
            return new Access(AccessKind.Disabled, $"ВЫКЛЮЧЕНО владельцем: советнику не разрешено читать «{section.TitleRu}» (Настройки → Операции → Нур Советник). Скажи это одной фразой.");
        if (!forData && !Safe(() => _host.CapOn("nav.open")))
            return new Access(AccessKind.Disabled, "ВЫКЛЮЧЕНО владельцем: открывать разделы советнику запрещено (Настройки → Операции → Нур Советник). Скажи это одной фразой.");
        if (_host.InMenu is { } inMenu && !Safe(() => inMenu(section.MenuKey)) && !AdvisorSections.Excluded.ContainsKey(section.MenuKey))
            return new Access(AccessKind.NotConnected, $"НЕ ПОДКЛЮЧЕНО: раздел «{section.TitleRu}» сейчас недоступен (не входит в тариф, функция не включена или раздел скрыт в «Разделах меню»). "
                                                       + "Предложи открыть Маркетплейс (раздел «Маркетплейс»), где функцию можно включить, или включить раздел в Настройки → Экран → «Разделы меню».");
        return new Access(AccessKind.Ok, "");
    }

    // ── Объявления функций ─────────────────────────────────────────────────────────────────────────────────────

    private static JsonObject Str(string description, IEnumerable<string>? values = null)
    {
        var o = new JsonObject { ["type"] = "STRING", ["description"] = description };
        if (values is not null)
            o["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return o;
    }

    private static JsonObject Fn(string name, string description, JsonObject? props = null, params string[] required)
    {
        var fn = new JsonObject { ["name"] = name, ["description"] = description };
        if (props is not null)
        {
            var parameters = new JsonObject { ["type"] = "OBJECT", ["properties"] = props };
            if (required.Length > 0)
                parameters["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
            fn["parameters"] = parameters;
        }
        return fn;
    }

    /// <summary>Объявления функций (одинаковые для чата generateContent и живого разговора Live). Список разделов — из реестра: новый раздел попадает сам.</summary>
    public JsonArray Declarations()
    {
        var sections = AdvisorSections.All.Select(s => s.Id).ToList();
        var openable = AdvisorSections.All.Where(s => s.Openable).Select(s => s.Id).Append("unknown").ToList();
        var areas = AdvisorSections.All.Where(s => s.DataArea is not null).Select(s => s.Id).ToList();
        var list = new JsonArray
        {
            Fn(ListSections, "Список разделов программы владельца: id, название, доступен ли этому пользователю и есть ли данные. Вызывай, когда не знаешь, где что находится."),
            Fn(OpenSection, "Открыть раздел или вкладку программы на экране. Вызывай ТОЛЬКО когда просят открыть, перейти, показать раздел. target — СТРОГО id из списка "
                            + "(например telegrambot.dialogs — Диалоги бота, telegrambot.offers — Отчёт торга, settings.onec — Экспорт в 1С, repair — Ремонт, mailing — Рассылка). "
                            + "Инструмент вернёт ФАКТ (открыто / нет права / не подключено) — скажи только его; пока нет результата, не говори «открыл».",
                new JsonObject { ["target"] = Str("id раздела", openable), ["words"] = Str("слова владельца про раздел, если id не нашёлся") }, "target"),
            Fn(DescribeSection, "Описание раздела или функции: что делает, для кого, как включить, шаги, доступен ли сейчас. Для объяснений и обучения.",
                new JsonObject { ["section"] = Str("id раздела", sections) }, "section"),
            Fn(GetSectionData, "Данные раздела ТОЛЬКО ДЛЯ ЧТЕНИЯ за период: продажи, чеки, возвраты, смены, склад, сроки, приходы и ЭСФ, поставщики, долги, клиенты (без телефонов), "
                               + "аналитика, ABC, сотрудники и табель (кодами), бот (настройки, диалоги, продажи, торг, отчёт торга), ремонт, рассылка (сегменты и число получателей), "
                               + "акции, Маркетплейс, настройки (без ключей и паролей), тариф. Вызывай, когда в сводке нет нужных цифр. Суммы пиши так, как они пришли.",
                new JsonObject
                {
                    ["section"] = Str("id раздела", areas),
                    ["period"] = Str("период", new[] { "today", "yesterday", "week", "month", "prev_month", "last30", "year", "custom" }),
                    ["date_from"] = Str("начало периода ГГГГ-ММ-ДД (для custom)"),
                    ["date_to"] = Str("конец периода ГГГГ-ММ-ДД (для custom)"),
                    ["query"] = Str("что искать: товар, поставщик, статус, слово"),
                    ["top"] = new JsonObject { ["type"] = "INTEGER", ["description"] = "сколько строк вернуть (1–30)" },
                }, "section"),
            Fn(WhatsNewTool, "Что нового и какие функции есть: новые функции Маркетплейса и разделы программы."),
        };
        if (_host.ActionsEnabled && _host.Draft is not null && _host.Confirm is not null)
            list.Add(Fn(PrepareDraft, "Подготовить ЧЕРНОВИК (ничего не проводится): kind=promo — черновик акции, kind=supplier_order — черновик заказа поставщику. "
                                      + "Программа сначала спросит владельца «Выполнить?».",
                new JsonObject { ["kind"] = Str("вид черновика", new[] { "promo", "supplier_order" }), ["reason"] = Str("зачем, одной фразой") }, "kind"));
        return list;
    }

    // ── Вызов ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Разбор аргументов вызова (JSON-текст от Gemini). Испорченный JSON — пустой объект.</summary>
    public static JsonObject ParseArgs(string? argsJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? Arg(JsonObject args, string name) =>
        args[name] is JsonValue v ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString() : null;

    /// <summary>Похоже на запись/изменение (имя не из списка чтения).</summary>
    public static bool LooksLikeWrite(string name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return WritePrefixes.Any(p => n == p || n.StartsWith(p + "_", StringComparison.Ordinal) || n.StartsWith(p, StringComparison.Ordinal) && n.Length > p.Length && char.IsUpper(name![p.Length]));
    }

    public const string WriteRefusal = "ОТКАЗ: Нур-советник только читает данные и открывает разделы. Изменения (продажа, возврат, оплата, удаление, цены, акции, настройки, "
                                       + "сообщения покупателям) делает сам пользователь в разделе программы. Предложи открыть нужный раздел.";

    /// <summary>Выполнить вызов инструмента моделью. Возвращает ТЕКСТ-ответ для модели (JSON или служебную фразу). Никогда не бросает (кроме отмены владельцем).</summary>
    public async Task<string> CallAsync(string name, string? argsJson, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        name = (name ?? "").Trim();
        if (Interlocked.Increment(ref _calls) > MaxCallsPerAnswer)
        {
            Log($"Нур-советник: инструмент {name} — лимит {MaxCallsPerAnswer} вызовов на ответ.", "INFO");
            return $"ЛИМИТ: в одном ответе не больше {MaxCallsPerAnswer} обращений к программе. Ответь по уже полученным данным.";
        }
        if (!ReadTools.Contains(name) && !(ConfirmTools.Contains(name) && _host.ActionsEnabled))
        {
            Log($"Нур-советник: отказ инструменту «{Trim(name)}» (не чтение).", "INFO");
            return LooksLikeWrite(name) || ConfirmTools.Contains(name) ? WriteRefusal : "НЕТ ТАКОГО ИНСТРУМЕНТА. " + WriteRefusal;
        }
        var args = ParseArgs(argsJson);
        string result;
        string area = "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            var work = RunAsync(name, args, timeout.Token, a => area = a);
            var delay = Task.Delay(CallTimeout, ct);
            if (await Task.WhenAny(work, delay).ConfigureAwait(false) != work)
            {
                ct.ThrowIfCancellationRequested();
                timeout.Cancel();
                _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                result = $"НЕ УСПЕЛО: программа не получила данные за {CallTimeout.TotalSeconds:0.#} с. Скажи, что данные сейчас недоступны, и предложи открыть раздел.";
            }
            else
                result = await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Только тип ошибки — текст исключения может нести данные.
            Log($"Нур-советник: инструмент {name} упал ({ex.GetType().Name}).", "WARNING");
            result = "НЕ ВЫПОЛНЕНО: ошибка программы при получении данных. Скажи, что не получилось, ничего не придумывай.";
        }
        if (result.Length > MaxResultChars)
            result = result[..(MaxResultChars - 20)] + "…(обрезано)";
        Log($"Нур-советник: инструмент {name}{(area.Length > 0 ? " (" + area + ")" : "")} — {watch.ElapsedMilliseconds} мс, {result.Length} симв.", "INFO");
        return result;
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] : s;

    private async Task<string> RunAsync(string name, JsonObject args, CancellationToken ct, Action<string> area)
    {
        switch (name)
        {
            case ListSections:
            {
                MarkUsed(Tr.T("Список разделов", "Бөлүмдөрдүн тизмеси", "Section list", "Bölüm listesi", "Bo'limlar ro'yxati"));
                var rows = AdvisorSections.All.Select(s =>
                {
                    var access = Check(s, forData: false);
                    var row = new JsonObject { ["id"] = s.Id, ["title"] = s.TitleRu, ["available"] = access.Ok, ["data"] = s.DataArea is not null };
                    if (!access.Ok)
                        row["why"] = access.Kind.ToString();
                    return (JsonNode)row;
                }).ToArray();
                return new JsonObject { ["sections"] = new JsonArray(rows) }.ToJsonString();
            }
            case OpenSection:
            {
                var target = Arg(args, "target") ?? "";
                var words = Arg(args, "words") ?? "";
                var section = target.Length > 0 && target != "unknown" ? AdvisorSections.Find(target) : AdvisorSections.Find(words);
                if (section is null || !section.Openable)
                    return section is { Openable: false }
                        ? $"НЕ ОТКРЫТО: у «{section.TitleRu}» нет своего окна — данные дай инструментом get_section_data, а открыть можно раздел «{AdvisorSections.Find(section.MenuKey)?.TitleRu ?? section.MenuKey}»."
                        : "НЕ ОТКРЫТО: такого раздела в программе нет. Скажи это и ничего не придумывай; можно вызвать list_sections.";
                area(section.Id);
                var access = Check(section, forData: false);
                if (!access.Ok)
                    return access.Reason;
                if (_host.Open is not { } open)
                    return "НЕ ОТКРЫТО: открывать разделы можно только в программе владельца.";
                var outcome = await open(section.Id, ct).ConfigureAwait(false);
                if (outcome.Ok)
                    MarkUsed(section.Title + Tr.T(" (открыт)", " (ачылды)", " (opened)", " (açıldı)", " (ochildi)"));
                return outcome.Ok && outcome.Problem is null
                    ? $"ОТКРЫТО (проверено программой): «{outcome.Shown}». Скажи владельцу ровно это, коротко."
                    : outcome.Ok
                        ? $"ОТКРЫТО частично: «{outcome.Shown}», но «{section.TitleRu}» показать не получилось: {outcome.Problem}."
                        : $"НЕ ОТКРЫТО: «{section.TitleRu}» — {outcome.Problem ?? "раздел не открылся"}. Скажи причину.";
            }
            case DescribeSection:
            {
                var section = AdvisorSections.Find(Arg(args, "section"));
                if (section is null)
                    return "НЕТ ТАКОГО РАЗДЕЛА. Можно вызвать list_sections.";
                area(section.Id);
                MarkUsed(section.Title + Tr.T(" (описание)", " (сүрөттөмө)", " (description)", " (açıklama)", " (tavsif)"));
                var access = Check(section, forData: false);
                var o = new JsonObject
                {
                    ["id"] = section.Id,
                    ["title"] = section.TitleRu,
                    ["about"] = section.About,
                    ["available"] = access.Ok,
                    ["can_open"] = section.Openable,
                    ["has_data"] = section.DataArea is not null,
                };
                if (!access.Ok)
                    o["why_unavailable"] = access.Reason;
                if (section.MarketplaceHint is { } hint && _host.Marketplace?.Invoke(hint) is { } market)
                    o["marketplace"] = JsonNode.Parse(AdvisorRedactor.Compact(market, PurchaseVisible, 2500));
                return AdvisorRedactor.Compact(o, PurchaseVisible, MaxResultChars);
            }
            case GetSectionData:
            {
                var section = AdvisorSections.Find(Arg(args, "section"));
                if (section?.DataArea is null)
                    return section is null ? "НЕТ ТАКОГО РАЗДЕЛА. Можно вызвать list_sections." : $"ДАННЫХ НЕТ: по разделу «{section.TitleRu}» программа данных для ИИ не даёт — можно описать его (describe_section) или открыть (open_section).";
                area(section.Id);
                var access = Check(section, forData: true);
                if (!access.Ok)
                    return access.Reason;
                if (_host.Data is not { } data)
                    return "ДАННЫХ НЕТ: данные разделов доступны только в программе владельца.";
                var (from, to, label) = AdvisorPeriod.Parse(Arg(args, "period"), Arg(args, "date_from"), Arg(args, "date_to"), _host.Today());
                var top = int.TryParse(Arg(args, "top"), out var t) ? Math.Clamp(t, 1, 30) : 15;
                var request = new AdvisorDataRequest(section.DataArea, section, from, to, label, Arg(args, "query") is { Length: > 0 } q ? (q.Length > 80 ? q[..80] : q) : null, top,
                    PurchaseVisible, p => Safe(() => _host.HasPermission(p)));
                var value = await data(request, ct).ConfigureAwait(false);
                MarkUsed(section.Title + (label.Length > 0 && section.DataArea is "sales" or "summary" or "shifts" or "staff" or "analytics" or "bot_sales" or "bot_offers" or "bot_dialogs" or "purchases"
                    ? " · " + label : ""));
                if (value is null)
                    return $"ДАННЫХ НЕТ: «{section.TitleRu}» за {label} — пусто или сервер не ответил. Так и скажи.";
                var payload = new JsonObject { ["section"] = section.TitleRu, ["period"] = label, ["data"] = JsonNode.Parse(AdvisorRedactor.Compact(value, PurchaseVisible, MaxResultChars - 300)) };
                return AdvisorRedactor.Compact(payload, PurchaseVisible, MaxResultChars);
            }
            case WhatsNewTool:
            {
                MarkUsed(Tr.T("Что нового", "Эмне жаңы", "What's new", "Yenilikler", "Nima yangi"));
                return AdvisorRedactor.Compact(_host.WhatsNew?.Invoke() ?? new JsonObject { ["note"] = "списка новых функций нет" }, PurchaseVisible, MaxResultChars);
            }
            case PrepareDraft:
            {
                if (!_host.ActionsEnabled || _host.Draft is not { } draft || _host.Confirm is not { } confirm)
                    return WriteRefusal;
                var kind = Arg(args, "kind") ?? "";
                if (kind is not ("promo" or "supplier_order"))
                    return "НЕ ВЫПОЛНЕНО: такого черновика нет (только promo или supplier_order).";
                var permission = PosPermissions.ViewProducts;
                if (!Safe(() => _host.HasPermission(permission)))
                    return $"НЕТ ПРАВА: черновик требует доступа «{RightName(permission)}».";
                area(kind);
                var what = kind == "promo"
                    ? Tr.T("подготовить черновик акции", "акциянын долбоорун даярдоо", "prepare a promotion draft", "kampanya taslağı hazırla", "aksiya qoralamasini tayyorlash")
                    : Tr.T("подготовить черновик заказа поставщику", "жеткирүүчүгө заказдын долбоорун даярдоо", "prepare a supplier order draft", "tedarikçi sipariş taslağı hazırla", "yetkazib beruvchiga buyurtma qoralamasini tayyorlash");
                if (!await confirm(what, ct).ConfigureAwait(false))
                    return "НЕ ВЫПОЛНЕНО: владелец отказался. Ничего не изменено.";
                MarkUsed(Tr.T("Черновик: ", "Долбоор: ", "Draft: ", "Taslak: ", "Qoralama: ") + what);
                return await draft(kind, ct).ConfigureAwait(false);
            }
            default:
                return WriteRefusal;
        }
    }
}

/// <summary>2026-11-27: период инструментов советника: «today / week / month / prev_month / last30 / year / custom» → даты и подпись (не длиннее 366 дней).</summary>
public static class AdvisorPeriod
{
    public static (DateTime From, DateTime To, string Label) Parse(string? period, string? from, string? to, DateTime today)
    {
        today = today.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        switch ((period ?? "").Trim().ToLowerInvariant())
        {
            case "today":
                return (today, today, Tr.T("сегодня", "бүгүн", "today", "bugün", "bugun"));
            case "yesterday":
                return (today.AddDays(-1), today.AddDays(-1), Tr.T("вчера", "кечээ", "yesterday", "dün", "kecha"));
            case "week":
                return (today.AddDays(-6), today, Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"));
            case "prev_month":
                return (monthStart.AddMonths(-1), monthStart.AddDays(-1), Tr.T("прошлый месяц", "өткөн ай", "last month", "geçen ay", "o'tgan oy"));
            case "last30":
                return (today.AddDays(-29), today, Tr.T("30 дней", "30 күн", "30 days", "30 gün", "30 kun"));
            case "year":
                return (new DateTime(today.Year, 1, 1), today, Tr.T("с начала года", "жылдын башынан", "year to date", "yılbaşından beri", "yil boshidan"));
            case "custom" when TryDate(from, out var f):
            {
                var t = TryDate(to, out var tt) ? tt : f;
                if (t < f)
                    (f, t) = (t, f);
                if (t > today)
                    t = today;
                if ((t - f).TotalDays > 366)
                    f = t.AddDays(-366);
                return (f, t, f == t ? f.ToString("dd.MM.yyyy") : f.ToString("dd.MM.yyyy") + "–" + t.ToString("dd.MM.yyyy"));
            }
            default:
                return (monthStart, today, Tr.T("с начала месяца", "айдын башынан", "month to date", "ay başından beri", "oy boshidan"));
        }
    }

    private static bool TryDate(string? s, out DateTime d) =>
        DateTime.TryParseExact((s ?? "").Trim(), new[] { "yyyy-MM-dd", "dd.MM.yyyy", "yyyy-MM-ddTHH:mm:ss" }, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out d);
}
