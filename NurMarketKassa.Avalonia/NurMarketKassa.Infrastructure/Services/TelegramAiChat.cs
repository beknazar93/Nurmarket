using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-30, владелец: «добавь ИИ, чтобы бот отвечал и общался».
///
/// Разговорная нейросеть для бота владельца — Google Gemini по БЕСПЛАТНОМУ ключу
/// (aistudio.google.com → Get API key; карта не нужна). Точные вопросы (выручка, должники, цена
/// товара) по-прежнему считает сама касса (TelegramAssistant); сюда приходит только разговор:
/// приветствия, «почему упала выручка», «как поднять продажи», непонятные фразы. Нейросеть
/// получает короткую сводку магазина (выручка, топ, что заканчивается, найденные товары) и
/// отвечает по ней, не выдумывая цифр.
///
/// Честно: на бесплатном уровне Google может использовать запросы для улучшения своих продуктов —
/// это написано владельцу в настройках рядом с полем ключа. Без ключа ничего никуда не уходит.
/// Имена моделей Google меняет — пробуем несколько по очереди и запоминаем ту, что ответила.
/// </summary>
public static class TelegramAiChat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) }; // 2026-10-06: 25 → 45 с — накладная по фото отвечает дольше

    // 2026-09-30: проверено на ключе владельца — «лёгкие» модели отвечают за 1,5–2,5 с, gemini-flash-latest и
    // gemini-3.5-flash «думают» 4–9 с и бывают перегружены (503); gemini-2.5-flash-lite этому ключу недоступна (404).
    private static readonly string[] Models = { "gemini-3.5-flash-lite", "gemini-flash-lite-latest", "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.5-flash" };

    private static string? _workingModel;

    /// <summary>Последние реплики по каждому чату — чтобы бот помнил, о чём только что говорили.</summary>
    private static readonly Dictionary<string, List<(string Role, string Text)>> History = new();

    private const int HistoryTurns = 8;

    // 2026-10-05: на тарифе «Старт» ИИ нет (TariffGate.CanUseAi) — ключ как будто не задан.
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(AiKey);

    // 2026-10-26: «настроен ли ИИ» — есть хоть один включённый ключ Gemini (список ключей, GeminiKeyPool); сам запрос берёт ключ из списка по очереди.
    internal static string? AiKey => TariffGate.CanUseAi ? GeminiKeyPool.FirstEnabledKey() : null;

    private const string SystemPrompt =
        "Ты — ИИ-помощник владельца магазина в Кыргызстане и общаешься с ним в Telegram. "
        + "Отвечай коротко (до 6–8 предложений), дружелюбно и по делу, на языке собеседника: по-русски или по-кыргызски. "
        + "Цифры магазина бери ТОЛЬКО из сводки ниже; если нужных данных в ней нет — честно скажи об этом и подскажи команду бота: "
        + "/segodnya — выручка сегодня, /nedelya — за неделю, /top — лучшие товары, /abc — ABC-анализ, /zakaz — что заказать, "
        + "/ostatki — что заканчивается, /dolgi — должники. Никогда не выдумывай суммы, остатки и цены. "
        + "Можно давать общие советы по торговле, выкладке, закупкам и работе с покупателями. Валюта — сом. "
        + ListRules;

    /// <summary>2026-09-30, владелец: «списки в боте некрасивые, всё смешано». Единые правила
    /// оформления для обоих режимов — нейросеть пишет список в одном и том же простом виде,
    /// а ToTelegramHtml переводит его в разметку Telegram.</summary>
    private const string ListRules =
        " ОФОРМЛЕНИЕ (Telegram, не Markdown): не используй таблицы, решётки (#), звёздочки для курсива. "
        + "Список — каждый пункт С НОВОЙ СТРОКИ и начинается с «• », например: «• Кока-Кола 1л — 85 сом». "
        + "Не больше 10–15 пунктов; если товары разного вида — сначала короткий заголовок группы отдельной строкой "
        + "(например «Напитки:»), под ним пункты. Между группами — пустая строка. "
        + "Название товара можно выделить **жирным**. Перед списком и после — не больше одной короткой фразы.";

    /// <summary>2026-10-05, владелец: «в десктопе открой чат с ИИ для владельца, чтобы владелец советовался с ним —
    /// специальную вкладку». Раздел «Нур Советник» программы владельца (AiAdvisorWindow).
    /// 2026-10-21, «доведи Нур Советника до 10»: данные магазина — частями по важности (<see cref="AiContextBudget"/>), без молча
    /// обрезанных разделов (О1); права пользователя (О4); сотрудники кодами [С1], запасной ИИ — без персональных данных (О5);
    /// этапы ответа на экране и время каждого этапа в журнале (О3).</summary>
    /// <summary>2026-10-06, владелец: «добавь загрузку фото накладной в наш ИИ, чтобы он мог загрузить с маржей на склад».
    /// Фото к текущему вопросу советника (накладная, чек поставщика, товар) — уходит в Gemini частью inline_data; в историю
    /// разговора не попадает (только текст).</summary>
    private static readonly AsyncLocal<(byte[] Data, string Mime)?> QuestionImage = new();

    /// <summary>2026-10-11, тестировщик (иишка.md, 1): «несколько фото/файлов для накладной, чтобы не путаться при загрузке нескольких
    /// страниц». Вторая и следующие страницы (фото или PDF) — к тому же вопросу, после первой.</summary>
    private static readonly AsyncLocal<List<(byte[] Data, string Mime)>?> QuestionExtraImages = new();

    /// <summary>2026-10-21: ответ советника и всё, что нужно окну: разделы данных, которые ушли в ИИ (для «откуда цифра»),
    /// время этапов, ответившая модель и ответил ли запасной ИИ.</summary>
    public sealed record OwnerAskResult(string? Answer, string? Error, IReadOnlyList<WebSource> Sources, IReadOnlyList<AiSection> Included,
        string Timings, string? Model, bool Fallback);

    /// <summary>Этап ответа для экрана: 1 — собираю данные, 2 — спрашиваю ИИ, 3 — готовлю ответ.</summary>
    public const int StageCollect = 1, StageAsk = 2, StageCompose = 3;

    /// <summary>2026-10-21 (О1): бюджет данных магазина для одного вопроса (символов). Инструкции — отдельно, короткие (~2–4 тыс.).
    /// 16 000 на всё (1.17.58) не хватало даже складу; Gemini принимает гораздо больше, но большой запрос отвечает медленнее (О3).</summary>
    public const int OwnerDataBudget = 22000;

    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>2026-10-21: модели, которые отказали на thinkingBudget 0 (400) — им шлём запрос без этой настройки.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ThinkingStep = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Токены ответа Gemini для журнала: вопрос, размышления, ответ (usageMetadata).</summary>
    private static string UsageText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("usageMetadata", out var u))
                return "токены —";
            int N(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return $"токены: вопрос {N("promptTokenCount")}, размышления {N("thoughtsTokenCount")}, ответ {N("candidatesTokenCount")}";
        }
        catch (JsonException)
        {
            return "токены —";
        }
    }

    // 2026-10-21: подмена ответа ИИ для стенда проверки (см. GenerateGeminiAsync). В программе не задаётся.
    private static Func<string, string, string?>? _standAnswer;
    private static string? _standLastSystem;

    /// <summary>Модель Gemini, ответившая последней (для журнала).</summary>
    public static string? LastModel => _workingModel;

    /// <summary>2026-10-27: общий срок ответа на вопрос (без фото). Задан — первая модель получает не больше 7 с, дальше остаток; не задан — прежние 40 с.
    /// Окно советника ставит 12 с и при превышении показывает «ИИ долго отвечает — повторить?».</summary>
    private static readonly AsyncLocal<TimeSpan?> QuestionBudget = new();

    public static async Task<OwnerAskResult> AskOwnerAppAsync(string question, IReadOnlyList<AiSection> shop, AiAccess access, CancellationToken ct,
        IReadOnlyList<(byte[] Data, string Mime)>? attachments = null, Action<int>? stage = null, bool useHistory = true, ISet<string>? forceFull = null,
        TimeSpan? budget = null)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        QuestionBudget.Value = attachments is { Count: > 0 } ? null : budget;
        var timings = new StringBuilder();
        stage?.Invoke(StageCollect);
        QuestionImage.Value = attachments is { Count: > 0 } ? (attachments[0].Data, attachments[0].Mime) : null;
        QuestionExtraImages.Value = attachments is { Count: > 1 } ? attachments.Skip(1).ToList() : null;
        try
        {
            var (questionSections, researchSources, partTimes) = await QuestionSectionsAsync(question, access, ct).ConfigureAwait(false);
            timings.Append(partTimes);
            var all = shop.Concat(questionSections).ToList();
            var explicitWebSearch = ProductActionPlan.IsExplicitWebSearchRequest(question);
            var groqSearch = explicitWebSearch && WebSearchUnavailable && AiProviders.CanSearchWeb;
            var build = System.Diagnostics.Stopwatch.StartNew();
            var (system, included, fallbackSystem) = BuildOwnerSystem(question, all, access, QuestionImage.Value is not null, forceFull, groqSearch);
            timings.Append($"контекст {system.Length} симв. ({build.ElapsedMilliseconds} мс); ");
            // 2026-10-21 (стенд): запасной ИИ получает обезличенный вариант всегда, кроме вопросов именно о сотрудниках, должниках и
            // чеках (без персональных разделов он на них не ответит) — раньше раздел должников был почти в каждом запросе и запасной ИИ не включался.
            var personalNeeded = PersonalQuestion(question);
            stage?.Invoke(StageAsk);
            var ai = System.Diagnostics.Stopwatch.StartNew();
            var (answer, error, generatedSources, fallback) = await AskCoreOwnerAsync(question, system, personalNeeded ? "" : fallbackSystem, ct, useHistory,
                webSearch: explicitWebSearch && !groqSearch && researchSources.Count == 0).ConfigureAwait(false);
            timings.Append($"ИИ {(fallback ? "запасной" : _workingModel ?? "—")} {ai.ElapsedMilliseconds} мс; ");
            var sources = researchSources.Count > 0 ? researchSources : generatedSources;

            // 2026-10-21 (О1): ИИ попросил раздел, который не поместился («НУЖНО: warehouse»), — второй шаг: только этот раздел целиком.
            if (answer is not null && AiContextBudget.NeedRequest(answer) is { } need && all.Any(s => s.Key == need) && forceFull?.Contains(need) != true)
            {
                DropLastTurn(useHistory);
                var force = new HashSet<string>(forceFull ?? new HashSet<string>()) { need };
                (system, included, fallbackSystem) = BuildOwnerSystem(question, all, access, QuestionImage.Value is not null, force, groqSearch);
                var again = System.Diagnostics.Stopwatch.StartNew();
                (answer, error, generatedSources, fallback) = await AskCoreOwnerAsync(question, system, personalNeeded ? "" : fallbackSystem, ct, useHistory,
                    webSearch: false).ConfigureAwait(false);
                timings.Append($"второй шаг «{need}» {again.ElapsedMilliseconds} мс; ");
                // Снова «НУЖНО:» — служебную строку владельцу не показываем.
                if (answer is not null && AiContextBudget.NeedRequest(answer) is not null)
                    answer = Tr.T("Для точного ответа данных не хватило — откройте нужный раздел программы (склад, ABC-анализ, долги) или задайте вопрос уже.",
                        "Так жооп үчүн маалымат жетишкен жок — программанын керектүү бөлүмүн ачыңыз же суроону тагыраак бериңиз.",
                        "Not enough data for an exact answer — open the relevant section of the program or ask a narrower question.",
                        "Kesin yanıt için veri yetmedi — programın ilgili bölümünü açın veya soruyu daraltın.",
                        "Aniq javob uchun ma'lumot yetmadi — dasturning kerakli bo'limini oching yoki savolni aniqroq bering.");
            }

            // 2026-10-05: у бесплатного ключа Gemini поиска Google нет — тогда ищет Groq (в Groq уходит только запрос, без сводки).
            if (groqSearch && answer is not null && SearchRequest(answer) is { } query)
            {
                DropLastTurn(useHistory);
                var (found, webFound, searchError) = await AiProviders.SearchWebAsync(query, ct).ConfigureAwait(false);
                var withResults = question + "\n\nРЕЗУЛЬТАТЫ ПОИСКА В ИНТЕРНЕТЕ по запросу «" + query + "»:\n"
                                  + (found ?? "не нашлось (" + searchError + ")")
                                  + "\n\nОтветь на вопрос по данным магазина и этим результатам. Строку «ПОИСК:» больше не пиши.";
                (answer, error, _, fallback) = await AskCoreOwnerAsync(withResults, system.Replace(GroqSearchHint, ""), "", ct, useHistory, webSearch: false).ConfigureAwait(false);
                sources = webFound;
            }
            stage?.Invoke(StageCompose);
            timings.Append($"всего {total.ElapsedMilliseconds} мс");
            // 2026-10-27: замер этапов в журнал. Потоковой выдачи нет (generateContent целиком), поэтому «первый токен» = конец ответа модели.
            AdvisorTurn.Perf("ответ", total.ElapsedMilliseconds, $"модель {(fallback ? "запасной ИИ" : _workingModel ?? "—")}, ИИ {ai.ElapsedMilliseconds} мс, {(budget is { } b ? "предел " + b.TotalSeconds.ToString("0") + " с" : "без предела")}");
            return new OwnerAskResult(answer, error, sources, included, timings.ToString(), fallback ? "запасной ИИ" : _workingModel, fallback);
        }
        finally
        {
            QuestionImage.Value = null;
            QuestionExtraImages.Value = null;
            QuestionBudget.Value = null;
        }
    }

    private static void DropLastTurn(bool useHistory)
    {
        if (!useHistory)
            return;
        lock (History)
        {
            if (History.TryGetValue(OwnerAppHistoryKey, out var list) && list.Count >= 2)
                list.RemoveRange(list.Count - 2, 2);
        }
    }

    /// <summary>Части данных, которые зависят от вопроса: карточки товаров из вопроса, сведения из интернета, сотрудники, чеки за
    /// период, остатки и сроки — ОДНОВРЕМЕННО (2026-10-21, О3: раньше по очереди, сроки годности ждали отчёт сервера каждый раз).</summary>
    private static async Task<(List<AiSection> Sections, IReadOnlyList<WebSource> Research, string Timings)> QuestionSectionsAsync(
        string question, AiAccess access, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long msExpiry = -1, msCards = -1, msStaff = -1, msSales = -1, msResearch = -1;
        // Сроки годности: не ждём сервер дольше 1,5 с — нет свежих, берём прошлые (обновятся к следующему вопросу).
        var expiry = access.Products ? ProductExpiryIndex.RefreshAsync(false, ct) : Task.FromResult(ProductExpiryIndex.Marks);
        _ = expiry.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        async Task<T> Timed<T>(Func<Task<T>> f, Action<long> done)
        {
            try { return await f().ConfigureAwait(false); }
            finally { done(watch.ElapsedMilliseconds); }
        }
        var cardsTask = access.Products ? Timed(() => ProductActionPlan.MentionedCardsContextAsync(question, ct), ms => msCards = ms) : Task.FromResult("");
        var researchTask = access.Products ? Timed(() => ResearchMentionedAsync(question, ct), ms => msResearch = ms) : Task.FromResult(("", (IReadOnlyList<WebSource>)Array.Empty<WebSource>()));
        var staffTask = access.Staff ? Timed(() => StaffContextAsync(question, ct), ms => msStaff = ms) : Task.FromResult("");
        var salesTask = Timed(() => SalesDetailContextAsync(question, ct), ms => msSales = ms);
        await Task.WhenAny(expiry, Task.Delay(1500, ct)).ConfigureAwait(false);
        msExpiry = expiry.IsCompleted ? watch.ElapsedMilliseconds : -2;
        string cards, staff, sales;
        (string Text, IReadOnlyList<WebSource> Sources) research;
        try { cards = await cardsTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"ИИ: карточки товаров не получены ({ex.Message}).", "WARNING"); cards = ""; }
        try { research = await researchTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"ИИ: сведения из интернета не получены ({ex.Message}).", "WARNING"); research = ("", Array.Empty<WebSource>()); }
        try { staff = await staffTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"ИИ: сотрудники не получены ({ex.Message}).", "WARNING"); staff = ""; }
        try { sales = await salesTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { PosLogger.Log($"ИИ: чеки за период не получены ({ex.Message}).", "WARNING"); sales = ""; }

        var list = new List<AiSection>();
        if (cards.Length > 0)
            list.Add(new AiSection("question_cards", "Карточки товаров из вопроса", 1, cards, SourceSection: "warehouse", SourceLabel: "Склад — карточки товаров"));
        if (research.Text.Length > 0)
            list.Add(new AiSection("research", "Найдено в интернете о товарах", 1, research.Text));
        if (staff.Length > 0)
            list.Add(new AiSection("staff", "Сотрудники, зарплата и табель", 2, staff, Personal: true, SourceSection: "salary", SourceLabel: "Зарплата и табель (сервер NurCRM)"));
        if (sales.Length > 0)
            list.Add(new AiSection("receipts", "Чеки за период", 2, sales, Short: sales.Length > 6000 ? sales[..6000] + "\n(показана часть чеков — итоги выше по всем)" : null,
                Personal: true, SourceSection: "sales", SourceLabel: "Продажи — чеки всех касс (сервер NurCRM)"));
        if (access.Products)
        {
            var exp = ProductExpiryIndex.ContextText(40);
            if (exp.Length > 0)
                list.Add(new AiSection("expiry", "Сроки годности", LooksLikeExpiryQuestion(question) ? 1 : 6, exp,
                    Short: ProductExpiryIndex.Marks.Count > 15 ? ProductExpiryIndex.ContextText(15) + $"\n(показаны 15 из {ProductExpiryIndex.Marks.Count} — остальное: НУЖНО: expiry)" : null,
                    SourceSection: "restock", SourceLabel: "Пополнение и сроки (сервер NurCRM)"));
        }
        var local = BuildOwnerLocalContext(question, access);
        if (local.Length > 0)
            list.Add(new AiSection("local", "Обращения в бот, прокат, товары из вопроса", 7, local));
        var t = $"сроки {(msExpiry == -2 ? "не ждали (>1,5 с)" : msExpiry + " мс")}, карточки {msCards} мс, интернет {msResearch} мс, сотрудники {msStaff} мс, чеки {msSales} мс; ";
        return (list, research.Sources, t);
    }

    /// <summary>Локальные мелочи для советника: обращения покупателей в бот, прокат, товары из вопроса (без выручки — она из «Сводки»).</summary>
    private static string BuildOwnerLocalContext(string question, AiAccess access)
    {
        var sb = new StringBuilder();
        try { sb.AppendLine(TelegramInquiryStore.ShortSummary()); }
        catch (Exception ex) { PosLogger.Log($"ИИ-помощник: обращения не собраны ({ex.Message}).", "TELEGRAM"); }
        if (TelegramAssistant.RentalContext(question) is { } rentals)
            sb.AppendLine(rentals);
        if (access.Products && TelegramAssistant.ProductContext(question) is { } products)
            sb.AppendLine("Товары из вопроса (каталог):\n" + products);
        return StripHtml(sb.ToString()).Trim();
    }

    private static bool Has(string q, params string[] words) => words.Any(q.Contains);

    /// <summary>Вопрос именно о сотрудниках, должниках или чеках (с кассирами) — ответ только по персональным разделам.</summary>
    private static bool PersonalQuestion(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        return LooksLikeStaffQuestion(q) || Has(q, "долж", "долг", "карыз", "debt", "qarz", "borç", "чек", "receipt", "fiş", "chek", "клиент", "кардар");
    }

    public static bool LooksLikeExpiryQuestion(string question) => Has((question ?? "").ToLowerInvariant(), "срок", "просроч", "годн", "мөөнөт", "expir", "skt", "muddat");

    /// <summary>2026-10-21 (О6): просьба ПОКАЗАТЬ список («выведи списком», «покажи все», «таблицей») — отвечаем таблицей, не планом действий.</summary>
    public static bool LooksLikeListRequest(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        return Has(q, "выведи", "выведите", "вывести", "списком", "список", "перечисли", "покажи все", "покажи всех", "покажи таблиц", "таблицей", "таблицу",
                   "тизме", "тизмеси", "көрсөт", "list", "show all", "listele", "ro'yxat", "jadval")
               && !Has(q, "добавь в список", "внеси в список");
    }

    /// <summary>Нужны правила действий с товарами (длинные — только когда о них речь).</summary>
    private static bool NeedsActionRules(string question, bool image)
    {
        if (image)
            return true;
        var q = (question ?? "").ToLowerInvariant();
        return ProductActionPlan.LooksLikeAction(q) || Has(q, "пополн", "закаж", "заказать", "списа", "цен", "акци", "скидк", "фото", "наценк", "срок год", "товар:");
    }

    private static bool NeedsBotRules(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        return Has(q, "бот", "telegram", "телеграм", "сценари", "команд", "консультант", "ответы голосом");
    }

    private static bool NeedsRemindRules(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        return Has(q, "напомн", "whatsapp", "ватсап", "вотсап", "эскерт");
    }

    /// <summary>2026-10-21 (О1): инструкция советника = короткое ядро + только нужные правила + данные по бюджету. Возвращает и
    /// вариант для запасного ИИ — без персональных частей (О5).</summary>
    private static (string System, IReadOnlyList<AiSection> Included, string FallbackSystem) BuildOwnerSystem(string question, IReadOnlyList<AiSection> sections,
        AiAccess access, bool image, ISet<string>? forceFull, bool groqSearch)
    {
        var rules = new StringBuilder(OwnerCorePrompt);
        if (access.DeniedPrompt() is { Length: > 0 } denied)
            rules.Append(' ').Append(denied);
        var q = (question ?? "").ToLowerInvariant();
        if (access.Clients && Has(q, "долж", "долг", "карыз", "debt", "borç", "qarz"))
            rules.Append(' ').Append(DebtorTableRule);
        if (access.Products && NeedsActionRules(question ?? "", image) && !(LooksLikeListRequest(question ?? "") && !ProductActionPlan.LooksLikeAction(q.Replace("выведи", "").Replace("покажи", ""))))
            rules.Append(' ').Append(ActionRules);
        else if (access.Products)
            rules.Append(" Изменения товаров (строки «ТОВАР:») предлагай только по прямой просьбе владельца что-то изменить.");
        if (access.Settings && NeedsBotRules(question ?? ""))
            rules.Append(' ').Append(BotRules);
        if (access.Clients && NeedsRemindRules(question ?? ""))
            rules.Append(' ').Append(RemindRules);
        if (image && access.Products)
            rules.Append("\n").Append(ProductActionPlan.PhotoPromptText).Append(' ').Append(InvoiceException);
        if (groqSearch)
            rules.Append('\n').Append(GroqSearchHint);
        // 2026-10-27, жалоба владельца («ИИ открывает не те разделы и врёт „открыла“»): переходы выполняет программа сама и сообщает результат;
        // нейросеть ничего не открывает и не обещает. Список id — только когда во фразе есть «открой/перейди/…» (иначе лишние токены).
        rules.Append(' ').Append(NavNoPretendRule);
        if (AdvisorNavRouter.LooksLikeNav(question))
            rules.Append(" РАЗДЕЛЫ ПРОГРАММЫ (id — название): ").Append(AdvisorNavRouter.PromptList()).Append('.');
        var head = rules + "\n\nДАННЫЕ МАГАЗИНА на " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + " (части по важности):\n";
        // 2026-10-21 (стенд): раздел, присланный целиком по «НУЖНО:», — сверх бюджета (раньше склад целиком вытеснял всё остальное).
        var extra = Math.Min(40000, sections.Where(x => forceFull?.Contains(x.Key) == true).Sum(x => x.Full.Length));
        // Сначала обезличенный вариант, потом основной — в AiContextBudget.LastReport остаётся отчёт о том, что ушло на самом деле.
        var (safeData, _, _) = AiContextBudget.Compose(sections, OwnerDataBudget + extra, withoutPersonal: true, forceFull: forceFull);
        var (data, report, included) = AiContextBudget.Compose(sections, OwnerDataBudget + extra, forceFull: forceFull);
        PosLogger.Log($"ИИ-советник: в ИИ — инструкции {head.Length} симв., {report}.", "INFO");
        return (head + data, included, head + safeData + "\n(Персональные данные сотрудников и должников запасному ИИ не передаются — на такие вопросы скажи, что основной ИИ недоступен, пусть владелец повторит позже.)");
    }

    /// <summary>2026-10-27: правило против «притворства» — модель не открывает разделы и не говорит, что открыла.</summary>
    internal const string NavNoPretendRule =
        "РАЗДЕЛЫ ОТКРЫВАЕТ ТОЛЬКО ПРОГРАММА, не ты: никогда не пиши «открываю», «открыла», «открылось», «вот раздел» и не выдумывай названия разделов и вкладок. "
        + "Нужно открыть раздел — выведи ТОВАР: {\"op\": \"open_section\", \"section\": \"id\"} (id строго из списка), а в тексте скажи только «Сейчас открою — проверьте результат от программы».";

    private const string OwnerCorePrompt =
        "Тебя зовут Нур Советник (по-кыргызски — Нур Кеңешчи; называй себя одним именем на языке ответа): ИИ-советник владельца магазина в Кыргызстане в программе NurMarket. "
        + "Отвечай на языке вопроса (русский, кыргызский, английский, турецкий, узбекский), по делу и дружелюбно, до 8 предложений. Валюта — сом. "
        + "ЦИФРЫ — ТОЛЬКО из данных магазина ниже; никогда не выдумывай суммы, остатки, цены и даты. Готовые итоги и проценты (СРАВНЕНИЕ ПЕРИОДОВ, ДОЛГИ, СКЛАД) называй как есть, не пересчитывай. "
        + "Нужных данных нет (другой период, будущая дата, нет такого товара) — честно скажи и назови раздел программы («Сводка», «Продажи», «ABC-анализ», «Пополнение и сроки», «Долги клиентов», «Склад», «Зарплата»). "
        + "Должники обозначены кодами [Д1], сотрудники — [С1]: пиши код ВСЕГДА в квадратных скобках ровно как в данных — программа сама покажет имя; не пиши, что имена скрыты. "
        + "Просят список, таблицу, «выведи», «покажи все» — выведи ТАБЛИЦЕЙ Markdown все строки из данных (строка заголовков, строка |---|, строки, «Итого» последней), без строк «ТОВАР:». "
        + "Однотипные данные (товары, дни, должники, сотрудники) — таблицей 2–7 колонок с короткими заголовками, числа с пробелами между разрядами и единицей (сом, шт); "
        + "перед таблицей одна фраза, после — 1–2 предложения вывода. Остальное — простой текст без # и звёздочек; список — пункты «• » с новой строки, не больше 10–15. "
        + "Советы — конкретные: что заказать, что продвигать, где теряются деньги, как поднять продажи; акции не ниже закупки + 5 %, на товары с низкой наценкой — без скидки. "
        + "Поиск в интернете и внешние источники — только по прямой просьбе искать в интернете; найденное называй найденным. "
        + "Не начинай ответ со своего имени и приветствия — сразу по делу.";

    private const string DebtorTableRule =
        "Список должников — таблицей | Должник | Долг, сом | Чеков | Долг с |, в колонке «Должник» только код [Д1] как в данных, в конце строка «Итого». Итог долгов бери из «ДОЛГИ КЛИЕНТОВ».";

    private const string ActionRules =
        "ДЕЙСТВИЯ С ТОВАРАМИ: ничего не меняется до кнопки владельца — пиши «предлагаю», «подтвердите», не пиши, что уже сделал. "
        + "Каждое действие — отдельной строкой в конце ответа: ТОВАР: {\"op\": \"...\", \"product\": \"точное название или id из данных\", ...} — одной строкой JSON. "
        + "op: stock_in / stock_out / stock_set (\"qty\"; у stock_out \"reason\": \"Просрочка\"/\"Брак\"/\"Порча\"/\"Кража\"/\"Другое\"), set_price / set_purchase (\"price\"), "
        + "set_expiry (\"date\": \"ГГГГ-ММ-ДД\"), set_shelf_life (\"days\"), set_min (\"qty\"), set_description (\"text\": 2–5 предложений без выдуманных цифр), "
        + "set_country / set_name / set_brand / set_category / set_barcode / set_article (\"text\"), receive (\"qty\", \"purchase\", \"price\"), "
        + "create (\"name\", \"qty\", \"purchase\", \"price\", \"barcode\"?, \"unit\"?, \"category\"?), open_section (\"section\": id из списка РАЗДЕЛЫ ПРОГРАММЫ ниже, например warehouse.receiving), "
        + "open (открыть товар на складе), photo (только по прямой просьбе искать фото в интернете). «добавь/пришло» — stock_in, «спиши/убери» — stock_out, «ровно N» — stock_set; "
        + "просрочку — stock_out с reason «Просрочка». Название — точно как в данных; товар не найден или неясен — спроси, не угадывай; название и штрихкод без просьбы не меняй. "
        + "Блок «НАЙДЕНО В ИНТЕРНЕТЕ О ТОВАРАХ» — источник описания, страны, бренда; сведений нет — не придумывай. Обрабатывай все перечисленные позиции.";

    private const string BotRules =
        "БОТ: состояние функций Телеграм-бота — в данных (раздел «БОТ»). Просят включить или выключить функцию — одна-две фразы, что изменится, и ПОСЛЕДНЕЙ строкой ровно: "
        + "БОТ: {\"ключ\": true/false} — ключи shift_summary_enabled, commands_enabled, ai_enabled, consultant_enabled, voice_replies_enabled; до кнопки «Применить» ничего не меняется. "
        + "Свои ответы бота (если в данных НЕ написано «сервер пока не поддерживает»): короткий вежливый ответ покупателю и ПОСЛЕДНЕЙ строкой: "
        + "СЦЕНАРИЙ: {\"kind\": \"command\" или \"keywords\", \"command\": \"латиница_без_слеша\", \"keywords\": [\"слово\", …], \"title\": \"название\", \"reply_text\": \"ответ\", \"audience\": \"customers\"} — "
        + "одной строкой JSON; адрес, телефон и цены не выдумывай — спроси.";

    private const string RemindRules =
        "Просят напомнить должникам о долге — коротко скажи, кому, и ПОСЛЕДНЕЙ строкой ровно: НАПОМНИТЬ: [Д1], [Д2] (всем — НАПОМНИТЬ: все). "
        + "Программа покажет кнопки «Написать в WhatsApp» с готовым текстом — отправляет владелец, не пиши, что уже отправил.";

    private const string InvoiceException =
        "ИСКЛЮЧЕНИЕ для фото накладной: не применяй ограничение 10–15 пунктов и общие правила оформления; выведи все строки в таблице строго по инструкции «ФОТО К ВОПРОСУ», "
        + "затем ровно три строки итогов; другого видимого текста не добавляй.";

    private const string GroqSearchHint =
        "ПОИСК В ИНТЕРНЕТЕ: если для ответа нужны свежие сведения извне (цены у конкурентов и поставщиков, курсы валют, новости, законы и налоги, "
        + "новинки, сезонный спрос), а в сводке их нет — ответь ТОЛЬКО одной строкой: ПОИСК: <короткий запрос для поиска на русском>. "
        + "Программа найдёт в интернете и пришлёт результаты. Если интернет не нужен — отвечай как обычно.";

    private static string? SearchRequest(string answer)
    {
        var m = Regex.Match(answer.Trim(), @"^\**\s*ПОИСК\s*:\s*(.+)$", RegexOptions.Multiline);
        return m.Success && answer.Trim().Length < 300 ? m.Groups[1].Value.Trim().Trim('*', '"', '«', '»') : null;
    }

    /// <summary>Источник из интернета, на который опирался ответ (поиск Google в Gemini).</summary>
    public sealed record WebSource(string Title, string Uri);

    /// <summary>2026-10-05, проверка на ключе владельца: обычные запросы — 200, с поиском Google — 429 (у бесплатного ключа
    /// Gemini квоты на поиск нет). До этого времени поиск не пробуем, отвечаем без него.</summary>
    private static DateTime _webSearchBlockedUntilUtc = DateTime.MinValue;

    /// <summary>Поиск в интернете сейчас недоступен для ключа (последний запрос с поиском получил 429).</summary>
    public static bool WebSearchUnavailable => DateTime.UtcNow < _webSearchBlockedUntilUtc;

    /// <summary>Один вопрос с поиском Google, без истории (поиск фото товара в интернете — ProductPhotoFinder).</summary>
    public static async Task<(string? Answer, IReadOnlyList<WebSource> Sources, string? Error)> AskWebAsync(string system, string question, CancellationToken ct)
    {
        var key = AiKey;
        // 2026-10-05: у Gemini поиска нет (бесплатный ключ) — ищет Groq (AiProviders), если задан его ключ.
        if ((string.IsNullOrWhiteSpace(key) || WebSearchUnavailable) && AiProviders.CanSearchWeb)
        {
            var (text, found, err) = await AiProviders.SearchWebAsync(question, ct).ConfigureAwait(false);
            return (text, found, err);
        }
        if (string.IsNullOrWhiteSpace(key))
            return (null, Array.Empty<WebSource>(), "ключ ИИ не задан");
        if (WebSearchUnavailable)
            return (null, Array.Empty<WebSource>(), "поиск в интернете недоступен для этого ключа ИИ");
        var (answer, error, sources) = await GenerateCoreAsync(key!, system, new List<(string, string)>(), question, ct,
            webSearch: true, fallbackWithoutSearch: false).ConfigureAwait(false);
        return (answer, sources, error);
    }

    /// <summary>«Новый разговор» в разделе «ИИ-советник».</summary>
    public static void ResetOwnerAppHistory()
    {
        lock (History)
            History.Remove(OwnerAppHistoryKey);
    }

    /// <summary>2026-10-05: продолжить сохранённый разговор ИИ-советника — его вопросы и ответы снова в памяти разговора.</summary>
    public static void RestoreOwnerAppHistory(IEnumerable<(string Role, string Text)> turns)
    {
        lock (History)
            History[OwnerAppHistoryKey] = turns.TakeLast(HistoryTurns * 2).ToList();
    }

    private const string OwnerAppHistoryKey = "ownerapp";

    /// <summary>2026-10-05, владелец: «постоянное голосовое общение как ChatGPT» (ответ «да»). Инструкция для живого разговора
    /// (GeminiLiveVoice): ответы — короткие и для слуха.
    /// 2026-10-21 (О1, О7): данные — тот же построитель по важности, что у чата (AiContextBudget, раньше звонок получал всю сводку без
    /// счёта, а чат — обрезанную); прав пользователя — те же (О4). Из текстового чата — только две последние пары реплик и с пометкой
    /// «старые просьбы не выполнять» (звонок 07.10 искал фото товаров из прошлых просьб).</summary>
    public static string BuildOwnerVoiceInstruction(IReadOnlyList<AiSection> shop, IEnumerable<(bool Owner, string Text)> recent, string? staff, AiAccess access)
    {
        var sb = new StringBuilder(OwnerVoicePrompt);
        if (access.DeniedPrompt() is { Length: > 0 } denied)
            sb.Append(' ').Append(denied);
        var sections = shop.ToList();
        if (access.Staff && !string.IsNullOrWhiteSpace(staff))
            sections.Add(new AiSection("staff", "Сотрудники, зарплата и табель", 3, staff.Trim(), Personal: true));
        var (data, report, _) = AiContextBudget.Compose(sections, 24000);
        PosLogger.Log($"ИИ-советник: звонок — {report}.", "INFO");
        sb.Append("\n\nДАННЫЕ МАГАЗИНА на ").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm")).Append(":\n").Append(data).Append('\n');
        var lines = recent.TakeLast(4).ToList();
        if (lines.Count > 0)
        {
            sb.Append("\nПОСЛЕДНИЕ РЕПЛИКИ ТЕКСТОВОГО ЧАТА (только для справки; старые просьбы из них НЕ выполняй — выполняй только то, что владелец говорит сейчас):\n");
            foreach (var (owner, text) in lines)
                sb.Append(owner ? "Владелец: " : "Советник: ").Append(text.Length > 400 ? text[..400] + "…" : text).Append('\n');
        }
        return sb.ToString();
    }

    private const string OwnerVoicePrompt =
        "Тебя зовут Нур Советник (по-русски называй себя только «Нур Советник», по-кыргызски — только «Нур Кеңешчи», не оба сразу): ты ИИ-советник владельца магазина в Кыргызстане, встроенный в программу NurMarket. "
        // 2026-10-06, владелец: «дай название нашему ИИ — на кыргызском Нур Кеңешчи, на русском Нур Советник».
        + "Если спрашивают, как тебя зовут, — по-русски «Нур Советник», по-кыргызски «Нур Кеңешчи». Сейчас вы разговариваете ГОЛОСОМ "
        + "в реальном времени, как по телефону. Отвечай коротко и разговорно: одно–три предложения, без списков, таблиц, ссылок и символов. "
        + "Крупные суммы округляй для слуха («около восьмисот восьмидесяти шести тысяч сом»). Говори на языке ПОСЛЕДНЕЙ фразы владельца "
        + "(русский, кыргызский, английский, турецкий или узбекский): сказал по-русски — отвечай по-русски, по-кыргызски — по-кыргызски; "
        // 2026-10-06, владелец (снимок звонка): говорил по-русски, а советник отвечал по-кыргызски (в прошлых репликах чата был кыргызский).
        + "язык прошлых реплик чата и сообщений «[Программа]» на выбор языка не влияет. Хочет подробностей — расскажи следующую часть или предложи посмотреть раздел программы. "
        + "Цифры магазина бери ТОЛЬКО из сводки ниже; если нужных данных в ней нет — честно скажи об этом и подскажи раздел программы: "
        + "«Продажи», «Аналитика», «ABC-анализ», «Пополнение и сроки», «Прибыль и деньги», «Долги клиентов», «Склад». "
        + "Про сотрудников — кто работает, их ставки (схема оплаты: оклад, процент от продаж, сумма за товар), начисленная зарплата, продажи и табель "
        + "(смены, дни, часы, кто сейчас на смене) — бери из раздела «СОТРУДНИКИ» и «ТАБЕЛЬ» сводки ниже; данные там есть, не говори, что их нет. "
        + "Если спрашивают за другой период — скажи «Сейчас посмотрю»: программа пришлёт данные сообщением «[Программа]». "
        + "Должники в сводке обозначены кодами [Д1], [Д2] — коды вслух не произноси: назови, сколько должников и на какую сумму, "
        + "а имена владелец увидит в разделе «Долги клиентов». Сотрудники обозначены кодами [С1], [С2] — вслух их не произноси, говори «сотрудник», "
        // 2026-10-21 (О5): имена сотрудников в ИИ не уходят — в чате программа сама подставит имя вместо кода.
        + "в чате пиши код в квадратных скобках. Никогда не выдумывай суммы, остатки и цены. "
        + "Давай конкретные советы: что заказать, что продвигать, где теряются деньги, как поднять продажи; акции — не ниже закупки плюс пять процентов. "
        // 2026-10-07, владелец: «перестань выдавать фото из базы NurCRM, ищи прямо в интернете».
        + "Поиск фото в интернете запускай только по прямой просьбе найти / поискать фото именно в интернете или в сети. "
        + "Слова «фото», «покажи фото», «есть ли фото», «удали фото» сами по себе не разрешают сетевой поиск. "
        + "Функции Телеграм-бота и сценарии бота голосом не меняются — для этого предложи написать в чат советника. "
        // 2026-10-06, владелец сказал «поставь четвёртое фото» — советник обещал, а программа не умела. Теперь умеет.
        // 2026-10-06, владелец: «дай ИИ звонку полный доступ к программе — открывать вкладки и разделы и искать товары».
        // 2026-10-27, жалоба «ИИ неправильно открывает разделы и врёт „открыла“»: раздел открывает ИНСТРУМЕНТ open_section, ответ — из его результата.
        + "Разделы и вкладки программы открывает инструмент open_section (склад и его вкладки: приёмка, ревизия, списание, перемещение; закупки, "
        + "пополнение и сроки, прибыль и деньги, продажи и финансы, долги, клиенты, зарплата, настройки и др.): когда владелец просит открыть, перейти или зайти — "
        + "вызови его и скажи владельцу ТОЛЬКО то, что инструмент вернул («Открыл …» / «Не получилось …» / варианты на выбор). Пока результата нет — не говори, что открыл; "
        + "не называй разделов, которых нет в инструменте, и не придумывай, где что находится. Товар на складе по названию открывает программа и пришлёт «[Программа] Открыт …» — скажи коротко. "
        // 2026-10-07, владелец: «ИИ должен сам собирать информацию и выводить в чате таблицей, и на звонок добавь».
        + "На вопросы с цифрами (продажи, чеки за день или период, остатки, долги, сотрудники, товары) программа САМА собирает данные "
        + "с сервера и показывает в чате таблицу, а тебе присылает «[Программа] На экране таблица…». Если в сводке ответа нет — скажи коротко "
        + "«Сейчас покажу таблицей» и дождись данных; НИКОГДА не говори, что сервер не передаёт чеки или что надо зайти в другую программу. "
        + "Разделы программы открывай только когда владелец сам попросил открыть. "
        // 2026-10-06, владелец: «добавь редактор к нашему ИИ звонку».
        + "Сайт магазина тоже меняет ПРОГРАММА: «поставь тему …» (темы: Солнечный, Ночной, Свежий рынок, Бутик, Ала-Тоо, Чистый), «тёмный стиль сайта», "
        + "«опубликуй сайт» — программа сама открывает «Редактор сайта», делает и пришлёт результат «[Программа]»; скажи коротко «Сейчас поставлю». "
        + "Опубликованный вид сайта покупатели видят на market.nurcrm.kg (на nurcrm.kg/catalog — пока прежний вид, это обновляет NurCRM). "
        + "Найденные в интернете фото на выбор пронумерованы на экране (№1, №2…); владелец говорит «поставь фото номер 4» или «четвёртое» — "
        + "программа сама ставит это фото в карточку товара и пришлёт результат сообщением «[Программа]»; скажи коротко «Ставлю». "
        // 2026-10-06, владелец спросил голосом «пополни товары»: в звонке действий с товарами нет — честно направляем в чат.
        // 2026-10-06, владелец: «дай возможность голосом менять информацию, открывать товар на складе, добавлять и удалять информацию».
        + "Товары в звонке меняет ПРОГРАММА: когда владелец просит изменить, дополнить или удалить информацию о товаре (описание, страна, бренд, "
        + "категория, цена, срок годности, остаток — приход или списание), найти сведения о товаре в интернете или открыть товар на складе, — "
        + "коротко скажи «Сейчас подготовлю» (или «Открываю»): программа сама найдёт сведения в интернете и покажет на экране список изменений; "
        + "владелец подтверждает словами «да, выполни» или кнопкой «Выполнить». НИКОГДА не говори, что не можешь менять товары или искать в интернете, "
        + "и не выдумывай факты о товаре — их покажет программа. Сообщения, которые начинаются с «[Программа]», — от программы: "
        + "коротко перескажи их владельцу. Валюта — сом.";

    /// <summary>2026-10-05: ответ нейросети для окна программы (не Telegram): единые «• », без разметки Markdown.</summary>
    public static string ToPlainText(string text)
    {
        // 2026-10-06: строки таблиц Markdown («| … |») не разбиваем — «•» внутри строки таблицы превращался в новую строку списка,
        // и таблица должников разваливалась. В ячейках «•», «*» и «-» в начале просто убираются.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Replace("**", "").Replace("__", "").Replace("`", "");
            if (Regex.IsMatch(l, @"^\s*[\*•\-]?\s*\|"))
            {
                l = Regex.Replace(l, @"^\s*[\*•\-]\s*(?=\|)", "");
                lines[i] = Regex.Replace(l, @"(?<=\|)\s*[\*•]\s+(?=\S)", " ");
                continue;
            }
            l = Regex.Replace(l, @"(?<=\S)[ \t]+[\*•][ \t]+(?=\S)", "\n• ");
            l = Regex.Replace(l, @"(?m)^[ \t]*[\*\-\+•][ \t]+", "• ");
            lines[i] = Regex.Replace(l, @"(?m)^[ \t]*#{1,6}[ \t]*", "");
        }
        var s = string.Join("\n", lines);
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        // 2026-10-21, живая проверка: модель иногда начинает ответ строкой «Нур Кеңешчи» / «Нур Советник» — убираем.
        s = Regex.Replace(s, @"^\s*(Нур Советник|Нур Кеңешчи|Nur Advisor|Nur Danışman|Nur Maslahatchi)\s*[:.!]?\s*\n+", "", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>2026-10-06, владелец: «к ИИ и боту дай полный доступ к товарам». Ответ бота владельцу с предложенными
    /// действиями с товарами (строки «ТОВАР: {…}» убраны из текста — бот покажет их кнопками «Выполнить / Отмена»).</summary>
    public static async Task<(string? Answer, List<ProductActionPlan.Step> Steps, string? Error)> AskOwnerBotAsync(string chatId, string question, CancellationToken ct)
    {
        await ProductExpiryIndex.RefreshAsync(false, ct).ConfigureAwait(false);
        var cards = await ProductActionPlan.MentionedCardsContextAsync(question, ct).ConfigureAwait(false);
        var (research, _) = await ResearchMentionedAsync(question, ct).ConfigureAwait(false);
        if (research.Length > 0)
            cards += (cards.Length > 0 ? "\n\n" : "") + research;
        var staff = await StaffContextAsync(question, ct).ConfigureAwait(false);
        if (staff.Length > 0)
            cards += (cards.Length > 0 ? "\n\n" : "") + staff;
        // 2026-10-07: «чеки за вчера» — программа сама загружает чеки за период, ИИ выводит таблицей (раньше: «зайдите в NurMarket»).
        var salesDetail = await SalesDetailContextAsync(question, ct).ConfigureAwait(false);
        if (salesDetail.Length > 0)
            cards += (cards.Length > 0 ? "\n\n" : "") + salesDetail;
        // 2026-10-21 (бот, К2): выручка в сводке ИИ бота — с сервера, как «Сводка» программы; местная история — только без связи.
        var serverRevenue = await TelegramReportBuilder.ServerRevenueAiTextAsync(ct).ConfigureAwait(false);
        var system = SystemPrompt + "\n" + ProductActionPlan.PromptText + (cards.Length > 0 ? "\n\n" + cards : "") + "\n\nСВОДКА МАГАЗИНА на " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ":\n"
                     + (serverRevenue is null ? "" : serverRevenue + "\n") + BuildShopContext(question, localRevenue: serverRevenue is null);
        var (answer, error, _) = await AskCoreAsync("owner:" + chatId, question, system, ct, raw: true).ConfigureAwait(false);
        if (answer is null)
            return (null, new List<ProductActionPlan.Step>(), error);
        // 2026-10-21 (О5): сотрудники в ИИ ушли кодами [С1] — владельцу в боте показываем имена.
        var (text, steps, _) = ProductActionPlan.Extract(AiPseudonyms.RevealStaff(answer), question);
        // «Открыть на складе» — только в программе владельца.
        steps = steps.Where(st => st.Op is not ("open" or "open_section")).ToList();
        return (ToTelegramHtml(text.Length > 0 ? text : "Подтвердите действия:"), steps, null);
    }

    private static readonly string[] StaffWords =
    {
        "сотрудник", "зарплат", "зп", "кассир", "продавц", "продавец", "консультант", "оклад", "преми", "бонус сотруд", "выработк",
        "кызматкер", "айлык", "иштеген", "кто продал", "кто больше продал", "персонал",
        // 2026-10-06, владелец: «дай доступ ИИ к списку сотрудников, их зарплате, к табелю».
        "табел", "смен", "отработ", "кто работал", "на смене", "график", "часов", "иштеди", "табель",
        "архив", "z-отч", "z отч", "зет", "отчёт", "отчет", "касс",
    };

    /// <summary>2026-10-06, владелец: «архив смен тоже показывай в Телеграм-боте». Последние закрытые смены (Z-отчёты) —
    /// подставляется в App.axaml.cs (тот же список смен, что табель).</summary>
    public static Func<CancellationToken, Task<string>>? ShiftArchiveProvider { get; set; }

    /// <summary>2026-10-06, владелец: «дай доступ нашему ИИ к списку сотрудников, их зарплате, доступ к табелю». Табель — те же смены
    /// и тот же расчёт, что окно «Табель» (ShiftHistoryService живёт в проекте программы, подставляется в App.axaml.cs).
    /// Параметры: начало и конец периода (дни включительно).</summary>
    public static Func<DateTime, DateTime, CancellationToken, Task<string>>? TimesheetProvider { get; set; }

    /// <summary>2026-10-07, владелец: «полный доступ ИИ — сам собирает информацию и выводит таблицей». Вопрос про чеки/продажи за
    /// период (задаёт программа: OwnerSalesData.LooksLikeQuestion) и таблица чеков за этот период с сервера.</summary>
    public static Func<string, bool>? SalesDetailMatcher { get; set; }

    public static Func<string, CancellationToken, Task<string>>? SalesDetailProvider { get; set; }

    /// <summary>Таблица чеков за период вопроса — если вопрос про чеки; иначе пусто.</summary>
    private static async Task<string> SalesDetailContextAsync(string question, CancellationToken ct)
    {
        if (SalesDetailMatcher?.Invoke(question) != true || SalesDetailProvider is not { } provider)
            return "";
        try
        {
            return await provider(question, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ: чеки за период не загружены ({ex.Message}).", "WARNING");
            return "";
        }
    }

    /// <summary>2026-10-06: вопрос про сотрудников, зарплату или табель (для звонка — догрузить данные за названный период).</summary>
    public static bool LooksLikeStaffQuestion(string? question) =>
        (question ?? "").ToLowerInvariant() is { Length: > 0 } q && StaffWords.Any(q.Contains);

    /// <summary>2026-10-06: сведения о сотрудниках (список, ставки и начисления, табель) за период из вопроса; без периода — с начала месяца.</summary>
    public static Task<string> StaffContextForAsync(string question, CancellationToken ct) =>
        StaffContextAsync(LooksLikeStaffQuestion(question) ? question : question + " сотрудники зарплата табель", ct);

    /// <summary>2026-10-06, владелец: «дай доступ ИИ к сотрудникам, чтобы ИИ мог считать зарплату и их продажи». Зарплату считает
    /// сервер, как на сайте (analytics/market/?tab=salary): по каждому — схема, оклад, процент, продажи (кассиром и консультантом),
    /// продано штук, начислено. Период — из вопроса (сегодня, неделя, прошлый месяц), иначе с начала месяца.</summary>
    private static async Task<string> StaffContextAsync(string question, CancellationToken ct)
    {
        var q = (question ?? "").ToLowerInvariant();
        if (!StaffWords.Any(q.Contains))
            return "";
        var today = DateTime.Today;
        var (from, to, label) = q.Contains("сегодн") || q.Contains("бүгүн") ? (today, today, "сегодня")
            : q.Contains("вчера") || q.Contains("кечээ") ? (today.AddDays(-1), today.AddDays(-1), "вчера")
            : q.Contains("недел") || q.Contains("жума") ? (today.AddDays(-6), today, "7 дней")
            : q.Contains("прошл") && q.Contains("месяц") ? (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddDays(-1), "прошлый месяц")
            : (new DateTime(today.Year, today.Month, 1), today, "с начала месяца");
        var sb = new StringBuilder();
        // 2026-10-06: три запроса сразу, а не по очереди (по очереди — 5–8 с, звонок не дожидался).
        static Task<TR> Start<TR>(Func<Task<TR>> f)
        {
            try { return f(); }
            catch (Exception ex) { return Task.FromException<TR>(ex); }
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long msPeople = -1, msTimesheet = -1, msSalary = -1;
        var peopleTask = Start(async () => { var r = await PosApp.SalesApi.ListConsultantsAsync(ct).ConfigureAwait(false); msPeople = watch.ElapsedMilliseconds; return r; });
        var timesheetTask = TimesheetProvider is { } provider
            ? Start(async () => { var r = await provider(from, to, ct).ConfigureAwait(false); msTimesheet = watch.ElapsedMilliseconds; return r; })
            : null;
        var salaryTask = Start(async () => { var r = await PosApp.SalesApi.MarketSalaryReportAsync(from, to, ct).ConfigureAwait(false); msSalary = watch.ElapsedMilliseconds; return r; });
        var archiveTask = ShiftArchiveProvider is { } archiveProvider ? Start(() => archiveProvider(ct)) : null;
        // Список сотрудников — как у сайта (api/users/employees/), тот же, что выбор консультанта при оплате.
        // 2026-10-21 (О5): имена — кодами [С1]… (AiPseudonyms), настоящие подставляет программа на этом ПК.
        try
        {
            var people = await peopleTask.ConfigureAwait(false);
            if (people.Count > 0)
                sb.Append($"СПИСОК СОТРУДНИКОВ (сервер NurCRM, всего {people.Count}; имена скрыты кодами, пиши код в скобках): ").Append(string.Join(", ", people.Select(p => AiPseudonyms.Staff(p.Name)))).Append(".\n");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ: список сотрудников не получен ({ex.Message}).", "WARNING");
        }
        // Табель — смены кассы за тот же период.
        if (archiveTask is not null)
        {
            try
            {
                var text = await archiveTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append(text.TrimEnd()).Append('\n');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ: архив смен не получен ({ex.Message}).", "WARNING");
            }
        }
        if (timesheetTask is not null)
        {
            try
            {
                var text = await timesheetTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append(text.TrimEnd()).Append('\n');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ: табель не получен ({ex.Message}).", "WARNING");
            }
        }
        try
        {
            var report = await salaryTask.ConfigureAwait(false);
            string S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
            sb.Append($"СОТРУДНИКИ И ЗАРПЛАТА (сервер NurCRM, {label}: {from:dd.MM}–{to:dd.MM}; считает сервер по схемам из раздела «Зарплата»):\n");
            if (report.TryGetProperty("cards", out var cards))
                sb.Append($"Итого начислено {S(cards, "total_payroll")} сом (оклады {S(cards, "total_base_prorated")}, проценты {S(cards, "total_percent_bonus")}, за товар {S(cards, "total_per_item_bonus")}); "
                          + $"продажи сотрудников {S(cards, "total_employee_sales")} сом, чеков {S(cards, "sales_count")}.\n");
            if (report.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var r in rows.EnumerateArray())
                    sb.Append($"• {AiPseudonyms.Staff(S(r, "employee_label"))}: {S(r, "pay_scheme_label")}"
                              + (S(r, "monthly_base_salary") is { Length: > 0 } b && b != "0.00" ? $", оклад {b}/мес" : "")
                              + (S(r, "sales_percent") is { Length: > 0 } pc && pc != "0.00" ? $", {pc}% от продаж" : "")
                              + (S(r, "per_item_amount") is { Length: > 0 } pi && pi != "0.00" ? $", {pi} сом за проданный товар" : "")
                              + $" | продажи {S(r, "employee_sales_period")} сом ({S(r, "sales_count")} чеков: кассиром {S(r, "cashier_sales_period")}, консультантом {S(r, "consultant_sales_period")})"
                              + $", продано {S(r, "items_sold_period")} шт | начислено {S(r, "total")} сом (оклад {S(r, "base_prorated")} + процент {S(r, "percent_bonus")} + за товар {S(r, "per_item_bonus")}"
                              + (S(r, "consultant_commission_period") is { Length: > 0 } cc && cc != "0.00" ? $" + консультант {cc}" : "") + ")\n");
            sb.Append("Отвечая про сотрудников, их зарплату, продажи и табель (смены, дни, часы), бери цифры только отсюда; схемы меняются в разделе «Зарплата», табель — раздел «Табель».");
            PosLogger.Log($"ИИ: сотрудники ({label}) — список {msPeople} мс, табель {msTimesheet} мс, зарплата {msSalary} мс.", "INFO");
            // 2026-10-21 (О5): табель и архив смен приходят с именами кассиров — известные имена тоже кодами.
            return AiPseudonyms.MaskStaff(sb.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ: зарплата сотрудников не получена ({ex.Message}).", "WARNING");
            return AiPseudonyms.MaskStaff(sb.Append("СОТРУДНИКИ И ЗАРПЛАТА: сервер не ответил — скажи владельцу открыть раздел «Зарплата».").ToString());
        }
    }

    /// <summary>2026-10-06: сведения из интернета о товарах из вопроса — только если просят описать / дополнить / найти сведения.</summary>
    private static async Task<(string Text, IReadOnlyList<WebSource> Sources)> ResearchMentionedAsync(string question, CancellationToken ct)
    {
        if (!ProductActionPlan.IsExplicitWebSearchRequest(question) || !ProductInfoResearch.LooksLikeInfoRequest(question))
            return ("", Array.Empty<WebSource>());
        var targets = ProductActionPlan.Mentioned(question, 3);
        if (targets.Count == 0)
            return ("", Array.Empty<WebSource>());
        var findings = await ProductInfoResearch.ResearchAsync(targets, ct).ConfigureAwait(false);
        var sources = findings.SelectMany(f => f.Sources).GroupBy(s => s.Uri).Select(g => g.First()).Take(8).ToList();
        return (ProductInfoResearch.ContextText(findings), sources);
    }

    /// <summary>Ответ на реплику владельца. Error — понятная владельцу причина, если не вышло.</summary>
    public static async Task<(string? Answer, string? Error)> AskAsync(string chatId, string question, CancellationToken ct)
    {
        // 2026-10-21 (бот, К2): выручка — с сервера, как «Сводка»; местная история — только без связи.
        var serverRevenue = await TelegramReportBuilder.ServerRevenueAiTextAsync(ct).ConfigureAwait(false);
        var (answer, error, _) = await AskCoreAsync("owner:" + chatId, question,
            SystemPrompt + "\n\nСВОДКА МАГАЗИНА на " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ":\n"
            + (serverRevenue is null ? "" : serverRevenue + "\n") + BuildShopContext(question, localRevenue: serverRevenue is null), ct).ConfigureAwait(false);
        return (answer, error);
    }

    /// <summary>2026-09-30, решение владельца «консультант для всех»: любой, кто пишет боту, общается
    /// с ИИ-продавцом. Сводка — ТОЛЬКО каталог (товары, цены, есть ли в наличии) и контакты магазина:
    /// выручки, долгов и данных других покупателей в ней нет вовсе, поэтому ИИ не может их выдать даже
    /// по просьбе. Не больше <see cref="CustomerLimitPerHour"/> вопросов в час от одного человека —
    /// чтобы посторонние не выбрали бесплатный лимит Google.</summary>
    public static async Task<(string? Answer, string? Error)> AskCustomerAsync(string chatId, string question, CancellationToken ct)
    {
        if (await HandlePendingCustomerOrderAsync(chatId, question, ct).ConfigureAwait(false) is { } pendingReply)
            return (ToTelegramHtml(pendingReply), null);

        lock (CustomerRequests)
        {
            if (!CustomerRequests.TryGetValue(chatId, out var times))
                CustomerRequests[chatId] = times = new Queue<DateTime>();
            while (times.Count > 0 && times.Peek() < DateTime.UtcNow.AddHours(-1))
                times.Dequeue();
            if (times.Count >= CustomerLimitPerHour)
                return ("Вы задали много вопросов подряд 🙂 Давайте продолжим через час — или позвоните в магазин.", null);
            times.Enqueue(DateTime.UtcNow);
        }

        var prefs = UserPreferences.Instance;
        var system = CustomerPrompt
            .Replace("{shop}", prefs.StoreName)
            + "\n\nМАГАЗИН: " + prefs.StoreName
            + (string.IsNullOrWhiteSpace(prefs.StoreAddress) ? "" : "\nАдрес: " + prefs.StoreAddress)
            + (string.IsNullOrWhiteSpace(prefs.OwnerPhone) ? "" : "\nТелефон магазина: " + prefs.OwnerPhone)
            + (MarketSpheres.IsClothing || MarketSpheres.IsServices
                ? "\nПРОКАТ: магазин даёт вещи напрокат с залогом (деньги или паспорт). Если спрашивают про прокат или аренду — "
                  + "скажи, что это можно оформить в магазине, цена и срок — у продавца; назови вещи из каталога в наличии, размеры и цвета, если они указаны."
                : "")
            + "\n\nКАТАЛОГ (цена и наличие):\n" + TelegramAssistant.CustomerCatalogContext(question, EarlierCustomerText(chatId))
            + CustomerProfileBlock(chatId);
        var (answer, error, _) = await AskCoreAsync("client:" + chatId, question, system, ct, raw: true).ConfigureAwait(false);
        if (answer == null)
            return (null, error);
        answer = ProcessOrder(chatId, answer);
        return (ToTelegramHtml(answer), null);
    }

    /// <summary>2026-10-01, решение владельца «обращения → заказы с сайта». Создание заказа витрины —
    /// ставит программа (ShowcaseApiService.CreateBotOrderAsync); null — заказы из бота не подключены.</summary>
    /// 2026-10-22 (витрина одежды): у строки — ещё вариант (размер/цвет), если покупатель его назвал.
    public static Func<string, string, IReadOnlyList<(string ProductId, double Qty, string? VariantId)>, string?, CancellationToken,
        Task<(string Id, string Number, decimal Total)>>? OrderCreator { get; set; }

    private sealed record PendingCustomerOrder(string Name, string Phone, string Comment,
        IReadOnlyList<(string ProductId, double Qty, string? VariantId)> Items, IReadOnlyList<string> Titles, DateTime CreatedUtc);

    private static readonly object PendingCustomerOrdersLock = new();
    private static readonly Dictionary<string, PendingCustomerOrder> PendingCustomerOrders = new();
    private static readonly TimeSpan PendingCustomerOrderLifetime = TimeSpan.FromMinutes(15);

    private static string NormalizeCustomerOrderAnswer(string? text) =>
        Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static bool IsCustomerOrderConfirmation(string? text) => NormalizeCustomerOrderAnswer(text) is
        "оформить заказ" or "да оформить заказ" or "да оформляйте" or "оформляйте" or "подтверждаю заказ"
        or "заказываю" or "да" or "ооба" or "заказды тастыктайм" or "заказ берейин" or "yes" or "place order" or "confirm order";

    private static bool IsCustomerOrderRejection(string? text) => NormalizeCustomerOrderAnswer(text) is
        "отменить заказ" or "отмена" or "нет" or "не оформлять" or "не надо" or "жок" or "заказды жокко чыгар" or "cancel" or "no";

    public static async Task<string?> HandlePendingCustomerOrderAsync(string chatId, string question, CancellationToken ct)
    {
        PendingCustomerOrder? draft;
        lock (PendingCustomerOrdersLock)
        {
            if (!PendingCustomerOrders.TryGetValue(chatId, out draft))
                return null;
            if (DateTime.UtcNow - draft.CreatedUtc > PendingCustomerOrderLifetime)
            {
                PendingCustomerOrders.Remove(chatId);
                return null;
            }
            if (IsCustomerOrderConfirmation(question) || IsCustomerOrderRejection(question))
                PendingCustomerOrders.Remove(chatId);
        }

        if (IsCustomerOrderRejection(question))
            return "Заказ отменён. Ничего не оформляли.";
        if (!IsCustomerOrderConfirmation(question))
            return "У вас подготовлен заказ. Чтобы продолжить, ответьте «Оформить заказ» или «Отменить заказ». Если нужно что-то изменить, сначала отмените текущий заказ.";
        // 2026-10-21 (бот, ВАЖНО 5): антиспам заказов — не чаще 1 в 10 минут и 3 в сутки с чата, 200 в час на бота (TelegramOrderGate).
        if (TelegramOrderGate.Check(chatId) is { } refusal)
            return refusal;

        if (!string.IsNullOrWhiteSpace(draft.Name))
            TelegramInquiryStore.SaveProfile(chatId, draft.Name, draft.Phone);
        if (OrderCreator is null)
        {
            // 2026-10-24 (Г1): заказы — по настройке «Группа заказов» (SendOrderAsync), не только владельцу.
            _ = TelegramBotService.SendOrderAsync("🛒 <b>Подтверждён заказ из Telegram</b>", draft.Name, draft.Phone, draft.Titles, null, draft.Comment);
            return "Заказ подтверждён и передан продавцу. Магазин свяжется с вами.";
        }

        try
        {
            var (_, number, total) = await OrderCreator(string.IsNullOrWhiteSpace(draft.Name) ? "Покупатель из Telegram" : draft.Name,
                draft.Phone, draft.Items, draft.Comment, ct).ConfigureAwait(false);
            TelegramInquiryStore.MarkLastOrder(chatId, number);
            PosLogger.Log($"Заказ из бота №{number} оформлен после подтверждения: {draft.Items.Count} поз., {total:0.##} сом.", "TELEGRAM");
            _ = TelegramBotService.SendOrderAsync($"🛒 <b>Новый заказ из бота №{Esc(number)}</b>", draft.Name, draft.Phone, draft.Titles,
                $"Сумма: {total:N2} сом", draft.Comment);
            return $"✅ Заказ №{number} оформлен на сумму {total:N2} сом. Магазин свяжется с вами по номеру {draft.Phone}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Заказ из бота: сервер не принял подтверждённый заказ ({ex.Message}).", "TELEGRAM");
            _ = TelegramBotService.SendOrderAsync("🛒 <b>Подтверждённый заказ из бота не записался на сервер</b> — свяжитесь с покупателем:",
                draft.Name, draft.Phone, draft.Titles, null, null);
            return "Заказ передан продавцу для проверки. Магазин свяжется с вами.";
        }
    }

    private static readonly Regex OrderLine = new(@"(?m)^[ \t]*ЗАКАЗ:[ \t]*(\{.*\})[ \t]*$", RegexOptions.Compiled);

    /// <summary>Строка модели «ЗАКАЗ: {…}» создаёт только черновик. Отдельное подтверждение покупателя
    /// проверяется приложением в следующем сообщении, до вызова API создания заказа.</summary>
    private static string ProcessOrder(string chatId, string answer)
    {
        var match = OrderLine.Match(answer);
        if (!match.Success)
            return answer;
        string name, phone, comment;
        var items = new List<(string ProductId, double Qty, string? VariantId)>();
        var titles = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
            phone = root.TryGetProperty("phone", out var ph) ? ph.GetString()?.Trim() ?? "" : "";
            comment = root.TryGetProperty("comment", out var c) ? c.GetString()?.Trim() ?? "" : "";
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (items.Count >= 50)
                        break;
                    var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    double qty = 1;
                    if (it.TryGetProperty("qty", out var q))
                    {
                        if (q.ValueKind == JsonValueKind.Number)
                            qty = q.GetDouble();
                        else if (q.ValueKind == JsonValueKind.String
                                 && double.TryParse(q.GetString()?.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture, out var qs))
                            qty = qs;
                    }
                    var product = TelegramAssistant.FindProductByTitle(title);
                    if (product != null && double.IsFinite(qty) && qty is > 0 and <= 10000)
                    {
                        // 2026-10-22 (витрина одежды): размер/цвет из строки → вариант товара (кеш вариантов бота, ≤ 3 с).
                        var size = it.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.String ? sz.GetString()?.Trim() ?? "" : "";
                        var color = it.TryGetProperty("color", out var cl) && cl.ValueKind == JsonValueKind.String ? cl.GetString()?.Trim() ?? "" : "";
                        var variant = FindOrderVariant(product.Id, size, color);
                        items.Add((product.Id, qty, variant?.Id));
                        var variantText = variant is null ? "" : string.Join(", ", new[] { variant.Size, variant.Color }.Where(v => !string.IsNullOrWhiteSpace(v)));
                        titles.Add(variantText.Length > 0 ? $"{product.Title} — {variantText} × {qty:0.###}" : $"{product.Title} × {qty:0.###}");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            PosLogger.Log($"Заказ из бота: не разобран ({ex.Message}).", "TELEGRAM");
            return "Не удалось разобрать заказ. Напишите товары и количество ещё раз.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            PosLogger.Log($"Заказ из бота: некорректные данные ({ex.Message}).", "TELEGRAM");
            return "Не удалось разобрать заказ. Напишите товары и количество ещё раз.";
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (items.Count == 0 || digits.Length < 9)
        {
            PosLogger.Log($"Заказ из бота не оформлен: товаров {items.Count}, телефон {(digits.Length < 9 ? "не указан" : "есть")}.", "TELEGRAM");
            return "Чтобы подготовить заказ, напишите товары из нашего каталога и номер телефона.";
        }
        lock (PendingCustomerOrdersLock)
            PendingCustomerOrders[chatId] = new PendingCustomerOrder(name, phone, comment, items, titles, DateTime.UtcNow);
        PosLogger.Log($"ИИ подготовил черновик заказа для Telegram ({items.Count} поз.) — ждёт отдельного подтверждения покупателя.", "TELEGRAM");
        return "Проверьте заказ:\n" + string.Join("\n", titles.Select(t => "• " + t))
               + (string.IsNullOrWhiteSpace(name) ? "" : "\nИмя: " + name) + "\nТелефон: " + phone
               + "\n\nОформить заказ? Ответьте «Оформить заказ» или «Отменить заказ».";
    }

    /// <summary>2026-10-22 (витрина одежды): вариант по размеру и цвету, которые назвал ИИ (без учёта регистра и «ё»).
    /// Только продающийся и с остатком; один подходящий — берём, иначе null (заказ уйдёт без размера, как раньше).</summary>
    private static NurMarketKassa.Services.Api.ProductVariantDto? FindOrderVariant(string productId, string size, string color)
    {
        if (size.Length == 0 && color.Length == 0)
            return null;
        var list = ProductVariantCache.Get(productId, TimeSpan.FromSeconds(3));
        if (list is null || list.Count == 0)
            return null;
        static string N(string? v) => (v ?? "").Trim().Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant();
        var matches = list.Where(v => v.IsActive && v.Quantity > 0
                                      && (size.Length == 0 || N(v.Size) == N(size))
                                      && (color.Length == 0 || N(v.Color) == N(color)))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Esc(string? t) => (t ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>2026-10-01, владелец: «запомнить ФИО и телефон, не спрашивать при каждом заказе —
    /// просто попросить подтвердить: "Эти реквизиты верны? Если нет — напишите заново"».</summary>
    private static string CustomerProfileBlock(string chatId)
    {
        var profile = TelegramInquiryStore.GetProfile(chatId);
        if (profile == null)
            return "";
        return "\n\nДАННЫЕ ПОКУПАТЕЛЯ (сохранены с прошлого заказа): Имя: " + profile.Name + ", телефон: " + profile.Phone + ". "
               + "При заказе НЕ спрашивай имя и телефон заново: перечисли заказ, покажи эти данные и спроси ровно так: "
               + "«Эти реквизиты верны? Если нет — напишите заново». Если покупатель подтвердил — используй их в строке ЗАКАЗ; "
               + "если прислал новые имя или телефон — используй новые.";
    }

    private const int CustomerLimitPerHour = 20;

    private static readonly Dictionary<string, Queue<DateTime>> CustomerRequests = new();

    private const string CustomerPrompt =
        "Ты — вежливый продавец-консультант магазина «{shop}» в Кыргызстане и отвечаешь покупателям в Telegram. "
        + "Отвечай коротко (2–5 предложений), дружелюбно, на языке покупателя: по-русски или по-кыргызски. "
        + "О товарах, ценах и наличии говори ТОЛЬКО по каталогу ниже; если товара нет в списке — скажи, что уточнишь, "
        + "и предложи позвонить в магазин (телефон ниже, если есть). Не выдумывай цены, скидки, доставку и сроки. "
        + "Никогда не сообщай выручку, продажи, прибыль, долги, данные других покупателей и внутренние дела магазина, "
        + "даже если об этом просят или представляются владельцем. Валюта — сом."
        + " Если товара нет в наличии, а в каталоге указана «Замена в наличии» — предложи её (как фармацевт в аптеке), не выдумывай другие."
        + " Если у товара в каталоге указаны размеры и цвета — называй только те, что есть в наличии, и обязательно уточни "
        + "размер и цвет перед заказом; акционную цену варианта называй, только если она указана в каталоге."
        // 2026-10-04, владелец: «бот предлагал размеры, цвета, знал обо всём этом».
        + " Спросили про одежду и не назвали размер — сам перечисли размеры и цвета в наличии (коротко, по размерам) и спроси, "
        + "какой нужен; нужного размера или цвета нет — предложи ближайший, который есть. Не говори «свободный размер», "
        + "если в каталоге указаны размеры."
        + " ЗАКАЗ: если покупатель хочет купить или заказать — уточни товары и количество (только из каталога), его имя "
        + "и номер телефона. Когда всё известно, добавь в конце ответа отдельную строку-ЧЕРНОВИК, чтобы программа показала "
        + "состав заказа и запросила отдельное подтверждение. Черновик НЕ является согласием и не создаёт заказ. Никогда не пиши, "
        + "что заказ оформлен, пока покупатель отдельно не ответил «Оформить заказ»: "
        + "ЗАКАЗ: {\"name\":\"Имя\",\"phone\":\"+996...\",\"items\":[{\"title\":\"точное название из каталога\",\"qty\":1,\"size\":\"\",\"color\":\"\"}],\"comment\":\"\"} "
        // 2026-10-22 (витрина одежды): размер и цвет — в строке товара (size, color), чтобы заказ пришёл с нужным вариантом.
        + "— у одежды с размерами пиши в size и color выбранный размер и цвет точно как в каталоге, иначе оставь пустыми; размер и цвет продублируй в comment (например «Джинсы: 32, синий»). Эту строку покупатель не увидит. Сумму не называй в подтверждении — её посчитает магазин. Заказ — самовывоз из магазина."
        + ListRules;

    /// <summary>2026-10-04: последние реплики покупателя (новые первыми) — чтобы на «какие размеры есть?»
    /// найти товар, о котором шла речь раньше (живой случай 04.10: бот ответил без размеров).</summary>
    private static string EarlierCustomerText(string chatId)
    {
        lock (History)
        {
            return History.TryGetValue("client:" + chatId, out var list)
                ? string.Join(" ", list.Where(x => x.Role == "user").Reverse().Take(3).Select(x => x.Text))
                : "";
        }
    }

    private static string ClampPromptContext(string system, int maxChars = 16000)
    {
        if (string.IsNullOrWhiteSpace(system))
            return system ?? string.Empty;
        if (system.Length <= maxChars)
            return system;
        return system[..maxChars] + "\n\n[Содержимое контекста обрезано — модель получила только начало, чтобы сохранить стабильность ответа.]";
    }

    private static async Task<(string? Answer, string? Error, IReadOnlyList<WebSource> Sources)> AskCoreAsync(
        string historyKey, string question, string system, CancellationToken ct, bool raw = false, bool webSearch = false)
    {
        var key = AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, "ключ ИИ не задан", Array.Empty<WebSource>());
        system = ClampPromptContext(system, 16000);

        List<(string Role, string Text)> history;
        lock (History)
        {
            if (!History.TryGetValue(historyKey, out history!))
                History[historyKey] = history = new List<(string, string)>();
            history = history.ToList();
        }

        var chatId = historyKey;
        var (answer, error, sources) = await GenerateCoreAsync(key!, system, history, question, ct, webSearch).ConfigureAwait(false);
        if (answer == null)
            return (null, error, sources);

        lock (History)
        {
            var list = History[chatId];
            list.Add(("user", question));
            list.Add(("model", answer));
            while (list.Count > HistoryTurns * 2)
                list.RemoveAt(0);
        }

        return (raw ? answer : ToTelegramHtml(answer), null, sources);
    }

    /// <summary>2026-10-21: запрос советника. История — последние реплики, каждая не длиннее 1 500 символов (длинные таблицы прошлых
    /// ответов замедляли ответ); useHistory=false — без истории (разбор фразы в звонке, О7). fallbackSystem — инструкция для
    /// запасного ИИ без персональных данных (О5); пустая строка — запасной ИИ не спрашиваем (в вопросе персональные данные).</summary>
    private static async Task<(string? Answer, string? Error, IReadOnlyList<WebSource> Sources, bool Fallback)> AskCoreOwnerAsync(
        string question, string system, string fallbackSystem, CancellationToken ct, bool useHistory, bool webSearch)
    {
        var key = AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, "ключ ИИ не задан", Array.Empty<WebSource>(), false);
        List<(string Role, string Text)> history;
        lock (History)
        {
            if (!History.TryGetValue(OwnerAppHistoryKey, out history!))
                History[OwnerAppHistoryKey] = history = new List<(string, string)>();
            history = useHistory ? history.Select(h => (h.Role, h.Text.Length > 1500 ? h.Text[..1500] + "…" : h.Text)).ToList() : new List<(string, string)>();
        }
        var result = await GenerateGeminiAsync(key!, system, history, question, ct, webSearch).ConfigureAwait(false);
        var fallback = false;
        if (result.Answer is null && !ct.IsCancellationRequested && AiProviders.HasFallback && fallbackSystem.Length > 0 && QuestionImage.Value is null)
        {
            var (fa, fe) = await AiProviders.ChatFallbackAsync(fallbackSystem, history, question, ct).ConfigureAwait(false);
            if (fa is not null)
            {
                result = (fa, null, result.Sources);
                fallback = true;
                PosLogger.Log("ИИ-советник: Gemini не ответил — ответил запасной ИИ (без персональных данных).", "INFO");
            }
            else
                result = (null, result.Error + "; запасные модели: " + fe, result.Sources);
        }
        if (result.Answer is null)
            return (null, result.Error, result.Sources, false);
        if (useHistory)
        {
            lock (History)
            {
                var list = History[OwnerAppHistoryKey];
                list.Add(("user", question));
                list.Add(("model", result.Answer));
                while (list.Count > HistoryTurns * 2)
                    list.RemoveAt(0);
            }
        }
        return (result.Answer, null, result.Sources, fallback);
    }

    /// <summary>2026-10-05: один вопрос без истории разговора (голосовое управление кассы — VoiceAi).</summary>
    public static async Task<(string? Answer, string? Error)> AskOnceAsync(string system, string question, CancellationToken ct)
    {
        var key = AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, "ключ ИИ не задан");
        return await GenerateAsync(key!, system, new List<(string, string)>(), question, ct).ConfigureAwait(false);
    }

    /// <summary>Проверка ключа из настроек: короткий запрос без данных магазина.</summary>
    public static async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct)
    {
        // 2026-10-26: проверяется именно этот ключ — без перебора списка (rotate: false).
        var (answer, error, _) = await GenerateGeminiAsync(key.Trim(), "Отвечай одним коротким предложением.",
            new List<(string, string)>(), "Скажи по-русски, что ты на связи.", ct, webSearch: false, rotate: false).ConfigureAwait(false);
        return answer != null ? (true, $"{answer.Trim()} ({_workingModel})") : (false, error ?? "нет ответа");
    }

    private static async Task<(string? Answer, string? Error)> GenerateAsync(
        string key, string system, List<(string Role, string Text)> history, string question, CancellationToken ct)
    {
        var (answer, error, _) = await GenerateCoreAsync(key, system, history, question, ct, webSearch: false).ConfigureAwait(false);
        return (answer, error);
    }

    /// <summary>2026-10-05, владелец: «настрой несколько моделей». Gemini не ответил (лимит, перегрузка, нет модели, нет
    /// связи) — отвечает запасная модель (AiProviders: OpenRouter, Groq), если владелец задал их ключи.</summary>
    private static async Task<(string? Answer, string? Error, IReadOnlyList<WebSource> Sources)> GenerateCoreAsync(
        string key, string system, List<(string Role, string Text)> history, string question, CancellationToken ct, bool webSearch,
        bool fallbackWithoutSearch = true)
    {
        var result = await GenerateGeminiAsync(key, system, history, question, ct, webSearch, fallbackWithoutSearch).ConfigureAwait(false);
        if (result.Answer is not null || !AiProviders.HasFallback || ct.IsCancellationRequested || !fallbackWithoutSearch)
            return result;
        var (answer, error) = await AiProviders.ChatFallbackAsync(system, history, question, ct).ConfigureAwait(false);
        return answer is not null ? (answer, null, result.Sources) : (null, result.Error + "; запасные модели: " + error, result.Sources);
    }

    /// <summary>2026-10-05, владелец: «включи поиск по интернету для ИИ» — webSearch добавляет инструмент google_search
    /// (Gemini сам решает, когда искать). Модель без поддержки поиска (400 про tool) — тот же запрос без поиска.</summary>
    private static async Task<(string? Answer, string? Error, IReadOnlyList<WebSource> Sources)> GenerateGeminiAsync(
        string key, string system, List<(string Role, string Text)> history, string question, CancellationToken ct, bool webSearch,
        bool fallbackWithoutSearch = true, bool rotate = true)
    {
        if (webSearch && WebSearchUnavailable && fallbackWithoutSearch)
            webSearch = false;
        // 2026-10-21: только для стенда проверки в scratchpad (ставится через reflection; в программе всегда null) — ответ подменой,
        // без сети: проверка «что ушло в ИИ», прав и обезличивания без платных запросов.
        if (_standAnswer is { } stand)
        {
            _standLastSystem = system;
            return (stand(system, question), null, Array.Empty<WebSource>());
        }
        var contents = new JsonArray();
        foreach (var (role, text) in history)
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });
        var userParts = new JsonArray(new JsonObject { ["text"] = question });
        // 2026-10-06: фото накладной / товара к вопросу советника.
        var image = QuestionImage.Value;
        if (image is { } img)
            userParts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = img.Mime, ["data"] = Convert.ToBase64String(img.Data) } });
        // 2026-10-11: следующие страницы накладной — в том же сообщении, по порядку.
        if (image is not null && QuestionExtraImages.Value is { Count: > 0 } extra)
            foreach (var page in extra)
                userParts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = page.Mime, ["data"] = Convert.ToBase64String(page.Data) } });
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = userParts });

        string Body(bool search)
        {
            var b = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = contents.DeepClone(),
                // 2026-10-09: длинные накладные должны помещать все строки таблицы и действия без обрыва на прежнем лимите.
                // 2026-10-21, живая проверка: лимит ответа включает «размышления» модели (1148 размышлений + 48 токенов ответа — обрезанный
                // ответ «спагетти 450г, Байэл…») — 4096 вместо 1200; длину ответа держит инструкция.
                ["generationConfig"] = new JsonObject { ["temperature"] = image is null ? 0.5 : 0.2, ["maxOutputTokens"] = image is not null ? 32768 : 4096 },
            };
            if (search)
                b["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });
            // 2026-10-21 (О3): кириллица — как есть, без \uXXXX (запрос был 78 тыс. символов вместо ~30).
            return b.ToJsonString(RelaxedJson);
        }
        var body = Body(webSearch);
        var none = (IReadOnlyList<WebSource>)Array.Empty<WebSource>();

        var models = (_workingModel is { } known ? new[] { known }.Concat(Models.Where(m => m != known)) : Models).ToList();
        string? lastError = null;
        // 2026-10-21 (О3, ответ 25 с): у каждой попытки свой срок (первая — 20 с, следующие — 12 с) и общий — 40 с (с фото накладной —
        // как раньше, до 45 с на модель); модель не ответила вовремя — сразу следующая, а не ожидание до конца общего таймаута.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var budgetSet = image is null && QuestionBudget.Value is not null;
        var overall = image is not null ? TimeSpan.FromSeconds(90) : QuestionBudget.Value ?? TimeSpan.FromSeconds(40);
        var keySwitches = 0;
        for (var mi = 0; mi < models.Count; mi++)
        {
            var model = models[mi];
            // 2026-10-26, владелец: «добавь возможность добавить API Gemini из нескольких Google-аккаунтов». Ключ — первый рабочий из списка
            // (GeminiKeyPool); у исчерпанного ключа этой модели запрос идёт со следующим. Список пуст (ключ передан напрямую) — как раньше.
            var usedKey = key;
            if (rotate && GeminiKeyPool.Entries().Count > 0)
            {
                var picked = GeminiKeyPool.Pick(model);
                if (picked is null)
                {
                    lastError = GeminiKeyPool.UnavailableMessage(model);
                    continue;
                }
                usedKey = picked;
            }
            var left = overall - started.Elapsed;
            if (left < TimeSpan.FromSeconds(budgetSet ? 1.5 : 5))
            {
                lastError ??= "Google не ответил вовремя";
                break;
            }
            // 2026-10-27: с общим пределом первая модель — не дольше 7 с (лёгкие модели отвечают за 1,5–2,5 с), остальное время — следующим.
            var attemptLimit = image is not null ? TimeSpan.FromSeconds(45)
                : budgetSet ? (mi == 0 ? TimeSpan.FromSeconds(Math.Min(7, overall.TotalSeconds * 0.6)) : left)
                : mi == 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(12);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(attemptLimit < left ? attemptLimit : left);
            var attemptWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{GeminiKeyPool.BaseUrl}/v1beta/models/{model}:generateContent");
                request.Headers.Add("x-goog-api-key", usedKey);
                // 2026-10-21 (О3, ответ 13–25 с): обычный вопрос — без «размышлений» модели (thinkingBudget 0): лёгкие модели Gemini
                // по умолчанию «думали» по 10+ с. Накладная по фото — как раньше. Модель не принимает настройку — запоминаем и шлём без неё.
                // Шаг для модели: 0 — thinkingBudget 0 (Gemini 2.x), 1 — thinkingLevel «low» (Gemini 3), 2 — как есть.
                // Живая проверка 21.10: gemini-3.x на thinkingBudget 0 — 400, на thinkingLevel «low» — 1,2 с и 0 токенов размышлений.
                var thinkStep = image is not null ? 2 : ThinkingStep.TryGetValue(model, out var ts) ? ts : model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var noThinking = thinkStep < 2;
                var sendBody = thinkStep switch
                {
                    0 => body.Replace("\"generationConfig\":{", "\"generationConfig\":{\"thinkingConfig\":{\"thinkingBudget\":0},"),
                    1 => body.Replace("\"generationConfig\":{", "\"generationConfig\":{\"thinkingConfig\":{\"thinkingLevel\":\"low\"},"),
                    _ => body,
                };
                request.Content = new StringContent(sendBody, Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request, attempt.Token).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                PosLogger.Log($"ИИ: {model} — {(int)response.StatusCode} за {attemptWatch.ElapsedMilliseconds} мс (запрос {sendBody.Length} симв.{(noThinking ? ", без размышлений" : "")}; {UsageText(json)}).", "INFO");
                // 2026-10-26: лимит (429/RESOURCE_EXHAUSTED) или недействительный ключ — ключ помечается, запрос читает/генерирует (побочных
                // действий нет) и повторяется со следующим ключом той же модели; ключей не осталось — следующая модель. 429 на запрос с поиском —
                // это квота поиска, а не ключа (ниже), ключ не трогаем.
                if (rotate && !response.IsSuccessStatusCode && !(webSearch && (int)response.StatusCode == 429)
                    && GeminiKeyPool.ReportFailure(usedKey, model, (int)response.StatusCode, json))
                {
                    lastError = GeminiKeyPool.UnavailableMessage(model);
                    if (GeminiKeyPool.Pick(model) is not null && ++keySwitches <= GeminiKeyPool.MaxKeys * 2)
                        mi--;
                    continue;
                }
                // 2026-10-21: живая проверка — Gemini 3.5 на thinkingBudget 0 отвечает общим «invalid argument»: любая 400 без размышлений → повтор с ними.
                if (noThinking && response.StatusCode == HttpStatusCode.BadRequest)
                {
                    ThinkingStep[model] = thinkStep + 1;
                    mi--;
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var text = ReadText(json);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _workingModel = model;
                        AdvisorTurn.Perf("выбор модели", started.ElapsedMilliseconds, $"{model}, попытка {mi + 1}, ответ модели {attemptWatch.ElapsedMilliseconds} мс");
                        if (rotate)
                            GeminiKeyPool.ReportSuccess(usedKey);
                        return (text, null, webSearch ? ReadSources(json) : none);
                    }

                    lastError = "нейросеть вернула пустой ответ";
                    continue;
                }

                var message = ReadError(json);
                if (webSearch && response.StatusCode == (HttpStatusCode)429)
                {
                    // Поиск Google не входит в квоту ключа — полчаса не пробуем; ответ без поиска (если можно).
                    _webSearchBlockedUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    PosLogger.Log($"ИИ: поиск в интернете недоступен для ключа (429 у {model}) — {(fallbackWithoutSearch ? "отвечаю без поиска" : "пропускаю")}.", "TELEGRAM");
                    if (!fallbackWithoutSearch)
                        return (null, "поиск в интернете недоступен для этого ключа ИИ", none);
                    webSearch = false;
                    body = Body(false);
                    mi--;
                    continue;
                }
                if (webSearch && response.StatusCode == HttpStatusCode.BadRequest
                    && (message.Contains("google_search", StringComparison.OrdinalIgnoreCase) || message.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("grounding", StringComparison.OrdinalIgnoreCase)))
                {
                    PosLogger.Log($"ИИ: модель {model} не умеет поиск в интернете — отвечаю без поиска ({message}).", "TELEGRAM");
                    webSearch = false;
                    body = Body(false);
                    mi--;
                    continue;
                }
                // 2026-09-30 (живой случай): 503 «This model is currently experiencing high demand» —
                // модель перегружена; раньше бот сразу сдавался. Теперь — следующая модель по списку.
                if ((int)response.StatusCode >= 500)
                {
                    lastError = "нейросеть Google сейчас перегружена — попробуйте через минуту";
                    if (_workingModel == model)
                        _workingModel = null;
                    continue;
                }

                // Модель не найдена или недоступна этому ключу — пробуем следующую.
                if (response.StatusCode is HttpStatusCode.NotFound
                    || (response.StatusCode == HttpStatusCode.BadRequest && message.Contains("model", StringComparison.OrdinalIgnoreCase) && !message.Contains("API key", StringComparison.OrdinalIgnoreCase))
                    || (response.StatusCode == HttpStatusCode.Forbidden && message.Contains("model", StringComparison.OrdinalIgnoreCase)))
                {
                    lastError = $"модель {model} недоступна";
                    continue;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                    return (null, "исчерпан бесплатный лимит запросов Google — попробуйте через минуту", none);
                if (message.Contains("API key", StringComparison.OrdinalIgnoreCase) || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    return (null, "ключ Google не подходит — проверьте его в настройках бота", none);
                return (null, $"ошибка Google {(int)response.StatusCode}: {message}", none);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 2026-10-21 (О3): эта модель не успела — следующая (если общий срок позволяет).
                PosLogger.Log($"ИИ: {model} не ответила за {attemptWatch.ElapsedMilliseconds} мс — пробую следующую.", "WARNING");
                lastError = "Google не ответил вовремя";
                if (_workingModel == model)
                    _workingModel = null;
                continue;
            }
            catch (HttpRequestException ex)
            {
                return (null, "нет связи с Google: " + ex.Message, none);
            }
        }

        return (null, lastError ?? "нет подходящей модели", none);
    }

    /// <summary>Источники ответа с поиском: candidates[0].groundingMetadata.groundingChunks[].web (uri, title).</summary>
    private static IReadOnlyList<WebSource> ReadSources(string json)
    {
        var list = new List<WebSource>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("groundingMetadata", out var meta)
                && meta.TryGetProperty("groundingChunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("web", out var web) && web.TryGetProperty("uri", out var uri) && uri.GetString() is { Length: > 0 } u)
                        list.Add(new WebSource(web.TryGetProperty("title", out var t) ? t.GetString() ?? u : u, u));
                }
            }
        }
        catch (JsonException)
        {
            // без источников
        }
        return list.DistinctBy(x => x.Uri).Take(8).ToList();
    }

    private static string? ReadText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return null;
            if (!candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                return null;
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                // «Размышления» модели (thought) владельцу не показываем.
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    continue;
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return message.GetString() ?? "";
        }
        catch (JsonException)
        {
        }

        return json.Length > 200 ? json[..200] : json;
    }

    /// <summary>Сводка для нейросети: те же отчёты, что шлёт бот, без HTML-разметки.</summary>
    private static string BuildShopContext(string question, bool localRevenue = true)
    {
        var sb = new StringBuilder();
        void Add(Func<string> build)
        {
            try
            {
                sb.AppendLine(StripHtml(build()));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-помощник: часть сводки не собрана ({ex.Message}).", "TELEGRAM");
            }
        }

        // 2026-10-05: «ИИ-советник» программы владельца передаёт выручку с сервера — локальную не добавляем.
        if (localRevenue)
        {
            Add(() => TelegramReportBuilder.BuildRevenue(1, "Выручка сегодня"));
            Add(() => TelegramReportBuilder.BuildRevenue(7, "Выручка за 7 дней"));
            Add(() => TelegramReportBuilder.BuildTopProducts(7, 8));
        }
        Add(() => TelegramReportBuilder.BuildLowStock(take: 10));
        // 2026-10-06: просроченные и скоро истекающие товары (сервер) — для советов и действий «списать просрочку».
        Add(() => ProductExpiryIndex.ContextText());
        Add(TelegramInquiryStore.ShortSummary);
        if (TelegramAssistant.RentalContext(question) is { } rentals)
            sb.AppendLine(rentals);
        if (TelegramAssistant.ProductContext(question) is { } products)
            sb.AppendLine("Товары из вопроса:\n" + products);

        var text = sb.ToString();
        return text.Length > 6000 ? text[..6000] : text;
    }

    private static string StripHtml(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    /// <summary>Markdown нейросети → HTML Telegram: экранируем, **жирный** → &lt;b&gt;, «* » → «• ».</summary>
    private static string ToTelegramHtml(string text)
    {
        var s = text.Replace("\r\n", "\n");
        // Пункты, слепленные в одну строку («Есть: * Кола * Фанта», «…сом • Сахар…»), — каждый с новой строки.
        s = Regex.Replace(s, @"(?<=\S)[ \t]+[\*•][ \t]+(?=\S)", "\n• ");
        // Маркеры списка в начале строки (*, -, +, •, с отступом) — единый «• ».
        s = Regex.Replace(s, @"(?m)^[ \t]*[\*\-\+•][ \t]+", "• ");
        // Заголовки Markdown «### Напитки» → «Напитки».
        s = Regex.Replace(s, @"(?m)^[ \t]*#{1,6}[ \t]*", "");
        s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
        s = Regex.Replace(s, @"__(.+?)__", "<b>$1</b>");
        // Одиночные звёздочки (курсив Markdown) и обратные кавычки — убираем.
        s = s.Replace("*", "").Replace("`", "");
        // Строка-заголовок группы («Напитки:») — жирным.
        s = Regex.Replace(s, @"(?m)^(?!• )([^\n<]{2,40}):[ \t]*$", "<b>$1:</b>");
        // Не больше одной пустой строки подряд.
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        // 2026-10-21 (бот, S0): режем по концу строки — <b> не переходит через строку, так разметка не рвётся (раньше 35 % длинных
        // ответов ломались внутри тега → 400 → повтор без оформления).
        if (s.Length <= 3800)
            return s;
        var cut = s.LastIndexOf('\n', 3800);
        if (cut < 2000)
        {
            cut = 3800;
            var open = s.LastIndexOf('<', cut - 1);
            if (open >= 0 && s.IndexOf('>', open) >= cut)
                cut = open;
            var amp = s.LastIndexOf('&', cut - 1);
            if (amp >= 0 && s.IndexOf(';', amp) >= cut)
                cut = amp;
            var head = s[..cut];
            return head + (Regex.Matches(head, "<b>").Count > Regex.Matches(head, "</b>").Count ? "</b>" : "") + "…";
        }
        return s[..cut] + "\n…";
    }
}
