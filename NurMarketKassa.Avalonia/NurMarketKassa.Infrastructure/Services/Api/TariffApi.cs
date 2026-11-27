using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Тарифный план из GET public/tariffs/. Решать по <see cref="Code"/> (start / standard / custom), не по имени.
/// 2026-11-21 (ответ Умара 21.11): YearPrice — plans[].year.price (год = 10 месяцев; null — сервер года не отдал).</summary>
/// TrialDays — trial_days (пробный период при регистрации: «Мини» 14 дней, остальные 10).
public sealed record TariffPlan(string Code, string Name, decimal? Price, string? BaseCode, decimal? YearPrice = null, int? TrialDays = null);

/// <summary>Пакет из GET public/tariffs/ (ai, clients, sales_analytics, restock, salary, debts, service, scales, telegram_bot, shift_analytics, analytics_export).
/// Цены — только с сервера (временные, владелец меняет их в админке). <see cref="IncludedIn"/> — коды планов, куда пакет входит бесплатно.</summary>
public sealed record TariffPack(string Code, string Name, string? Description, decimal ActivationFee, decimal MonthlyFee, IReadOnlyList<string> IncludedIn);

public sealed record TariffCatalog(IReadOnlyList<TariffPlan> Plans, IReadOnlyList<TariffPack> Packs)
{
    public TariffPlan? Plan(string code) => Plans.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
}

public sealed record TariffQuotePack(string Code, decimal Monthly, decimal Once, bool AlreadyActive);

/// <summary>Расчёт POST users/company/tariff/quote/: Monthly — сумма только по пакетам (база тарифа — отдельно в Base), Once — разовая активация.
/// StandardCheaper + StandardPrice — «Стандарт выйдет дешевле».</summary>
/// <summary>2026-11-21 (ответ Умара 20.11, В.23): модуль в расчёте — входит в профиль бесплатно (Included) или добавлен сверх него (Extra, Monthly — сом в месяц).</summary>
public sealed record TariffQuoteModule(string Id, string Name, decimal Monthly);

public sealed record TariffQuote(decimal Monthly, decimal Once, decimal Base, IReadOnlyList<TariffQuotePack> Packs, bool StandardCheaper, decimal? StandardPrice,
    string? BasePlan = null, string? Sphere = null, IReadOnlyList<TariffQuoteModule>? Included = null, IReadOnlyList<TariffQuoteModule>? Extra = null, decimal? ExtraMonth = null, decimal? TotalMonth = null,
    string? PlanAfter = null, IReadOnlyList<string>? Removed = null, decimal? StartPrice = null, string? Period = null, decimal? TotalYear = null, decimal? YearDiscount = null, int? YearPaidMonths = null, decimal? TotalOnce = null)
{
    /// <summary>2026-11-21 (ответ Умара 21.11): после снятия пакета со «Стандарта» сервер возвращает plan_after = "custom" — тариф станет «Своё»: «Старт» + оставшиеся пакеты.</summary>
    public bool BecomesCustom => string.Equals(PlanAfter, "custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сервер посчитал и модули (поле total_month): тариф + пакеты + модули других сфер.</summary>
    public bool HasModules => TotalMonth is not null;
}

/// <summary>Принятая заявка (201): сервер только записывает её, менеджер обрабатывает; платёжной ссылки нет (PayUrl null).</summary>
public sealed record TariffOrder(string OrderId, string Status, decimal AmountMonthly, decimal AmountOnce, string? PayUrl);

/// <summary>Что делает окно тарифа «Своё» — интерфейс нужен, чтобы окно можно было проверить без сервера.</summary>
public interface ITariffApi
{
    Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default);
    Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default);

    /// <summary>2026-11-21: расчёт вместе с модулями (modules[], sphere — можно не передавать: сервер берёт текущие модули и профиль компании). По умолчанию — расчёт без модулей (прежние проверки и заглушки).</summary>
    Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, CancellationToken ct = default) => QuoteAsync(packs, ct);

    /// <summary>2026-11-21 (ответ Умара 21.11): + remove[] (снять пакеты, входящие в тариф «Стандарт») и period ("month" | "year"; год = 10 месяцев). По умолчанию — прежний расчёт без снятия.</summary>
    Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, IReadOnlyList<string>? remove, string period, CancellationToken ct = default) =>
        QuoteAsync(packs, modules, sphere, ct);
    Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default);

    /// <summary>2026-11-21: заявка с modules[], remove[] и period (POST users/company/tariff/custom/). По умолчанию — прежняя заявка только с пакетами.</summary>
    Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, IReadOnlyList<string>? remove, string period, string idempotencyKey, CancellationToken ct = default) =>
        SubmitCustomAsync(packs, idempotencyKey, ct);
}

/// <summary>2026-11-08 (ТЗ п. 32, Б-39, ответ Умара 08.11): тариф «Своё» — пакеты функций поверх «Старта».
/// GET public/tariffs/ (без входа, кэш 10 минут), POST users/company/tariff/quote/ (расчёт), POST users/company/tariff/custom/ с Idempotency-Key (заявка).
/// Сервер пока только записывает заявку, её обрабатывает менеджер: план custom не выдаётся и пакеты сами не включаются. Ошибки — <see cref="Describe"/>.</summary>
public sealed class TariffApi : ITariffApi
{
    private const string Root = "api/";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly object CacheSync = new();
    private static TariffCatalog? _cached;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    private readonly NurMarketApiClient _api;

    public TariffApi(NurMarketApiClient api) => _api = api;

    /// <summary>Последний полученный каталог тарифов (кэш 10 минут); null — ещё не загружался. Для подписей без запроса (пробный период «Мини» в карточке тарифа).</summary>
    public static TariffCatalog? CachedCatalog
    {
        get { lock (CacheSync) return _cached; }
    }

    /// <summary>2026-11-26: только для проверок — подложить каталог тарифов в кэш (Маркетплейс берёт из него названия пакетов и цену тарифа в полосе «Ваш тариф»).</summary>
    internal static void SetCatalogForTests(TariffCatalog? catalog)
    {
        lock (CacheSync)
        {
            _cached = catalog;
            _cachedAtUtc = catalog is null ? DateTime.MinValue : DateTime.UtcNow;
        }
    }

    internal static void ResetCacheForTests()
    {
        lock (CacheSync)
        {
            _cached = null;
            _cachedAtUtc = DateTime.MinValue;
        }
    }

    public async Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default)
    {
        lock (CacheSync)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;
        }

        var data = await _api.RequestAsync(HttpMethod.Get, Root + "public/tariffs/", null, null, ct).ConfigureAwait(false);
        var catalog = ParseCatalog(data);
        lock (CacheSync)
        {
            _cached = catalog;
            _cachedAtUtc = DateTime.UtcNow;
        }
        return catalog;
    }

    public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default) => QuoteAsync(packs, null, null, ct);

    public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, CancellationToken ct = default) =>
        QuoteAsync(packs, modules, sphere, null, "month", ct);

    public async Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, IReadOnlyList<string>? remove, string period, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Post, Root + "users/company/tariff/quote/", QuoteBody(packs, modules, sphere, remove, period), null, ct).ConfigureAwait(false);
        return ParseQuote(data);
    }

    public Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default) =>
        SubmitCustomAsync(packs, null, null, "month", idempotencyKey, ct);

    public async Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, IReadOnlyList<string>? remove, string period, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_api.AccessToken))
            throw new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401);

        // Тот же путь, что у оплаты долга: общая цепочка обработчиков клиента (Bearer, обновление токена, щит 429), свой только заголовок Idempotency-Key.
        // Повтор с тем же ключом сервер отдаёт тем же ответом — заявка не задваивается.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_api.BaseAddress, Root + "users/company/tariff/custom/"));
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new StringContent(JsonSerializer.Serialize(QuoteBody(packs, modules, null, remove, period)), Encoding.UTF8, "application/json");
        using var outage = CancellationTokenSource.CreateLinkedTokenSource(ct, _api.OutageCancelToken);
        using var resp = await _api.SendRawAsync(req, outage.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(outage.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var message = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? NurMarketApiClient.AuthInvalidHintRu : ApiErrorParser.Parse(resp, text);
            throw new ApiException(message, (int)resp.StatusCode, TryParse(text));
        }

        return ParseOrder(TryParse(text) ?? default);
    }

    /// <summary>Тело расчёта и заявки: пакеты + (если заданы) модули, сфера и remove[] (снять пакеты тарифа «Стандарт»); период month | year (год = 10 месяцев, 2026-11-21).
    /// Нет modules / sphere / remove — полей в теле нет, сервер берёт текущие модули и профиль компании.</summary>
    internal static Dictionary<string, object?> QuoteBody(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, IReadOnlyList<string>? remove = null, string period = "month")
    {
        var body = new Dictionary<string, object?> { ["packs"] = packs.ToArray(), ["period"] = string.Equals(period, "year", StringComparison.OrdinalIgnoreCase) ? "year" : "month" };
        if (modules is not null)
            body["modules"] = modules.ToArray();
        if (remove is { Count: > 0 })
            body["remove"] = remove.ToArray();
        if (!string.IsNullOrWhiteSpace(sphere))
            body["sphere"] = sphere.Trim();
        return body;
    }

    // ── Разбор ответов (internal — для тестов) ───────────────────────────────────────

    internal static TariffCatalog ParseCatalog(JsonElement root)
    {
        var plans = new List<TariffPlan>();
        var packs = new List<TariffPack>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("plans", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var item in p.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var code = Str(item, "code");
                    if (code is null)
                        continue;
                    plans.Add(new TariffPlan(code, LocalName(item, "name") ?? code, Dec(item, "price"), Str(item, "base"),
                        item.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Object ? Dec(yr, "price") : null,
                        Dec(item, "trial_days") is { } td && td > 0 ? (int)td : null));
                }

            if (root.TryGetProperty("packs", out var k) && k.ValueKind == JsonValueKind.Array)
                foreach (var item in k.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var code = Str(item, "code");
                    if (code is null)
                        continue;
                    var included = new List<string>();
                    if (item.TryGetProperty("included_in", out var inc) && inc.ValueKind == JsonValueKind.Array)
                        included.AddRange(inc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                    packs.Add(new TariffPack(code, LocalName(item, "name") ?? code, LocalName(item, "description"),
                        Dec(item, "activation_fee") ?? 0m, Dec(item, "monthly_fee") ?? 0m, included));
                }
        }

        return new TariffCatalog(plans, packs);
    }

    internal static TariffQuote ParseQuote(JsonElement root)
    {
        var packs = new List<TariffQuotePack>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("packs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var code = Str(item, "code");
                if (code is null)
                    continue;
                packs.Add(new TariffQuotePack(code, Dec(item, "monthly") ?? 0m, Dec(item, "once") ?? 0m,
                    item.TryGetProperty("already_active", out var aa) && aa.ValueKind == JsonValueKind.True));
            }

        var cheaper = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("standard_cheaper", out var sc) && sc.ValueKind == JsonValueKind.True;
        // 2026-11-21 (ответ Умара 20.11, В.23): included[{id, name}] — модули профиля (бесплатно), extra[{id, name, monthly}] — модули других сфер, total_month = тариф + пакеты + extra.
        // 2026-11-21 (живая проверка тарифов на ТЕСТ Стандарт): сервер отдаёт base = 1300 (база «Старта») ДАЖЕ на «Стандарте», а цену ТЕКУЩЕГО тарифа — в base_price (3600); итог total_month считается от base_price.
        // Берём base_price, иначе строка «Основа» в окне «Своё» на Стандарте не сходилась с итогом (1 300 + 0 ≠ 3 600).
        // 2026-11-21 (ответ Умара 21.11): base — цена базового плана (на Стандарте 3600; со снятым пакетом база — «Старт» 1300), start_price — цена «Старта» отдельно; plan_after / removed — во что превратится тариф;
        // total_year / year_discount / year_paid_months — год = 10 месяцев. Старый ответ (base = «Старт» даже на Стандарте) читается из base_price — запасной путь.
        return new TariffQuote(Dec(root, "monthly") ?? 0m, Dec(root, "once") ?? 0m, Dec(root, "base") ?? Dec(root, "base_price") ?? 0m, packs, cheaper, Dec(root, "standard_price"),
            Str(root, "base_plan"), Str(root, "sphere"), QuoteModules(root, "included"), QuoteModules(root, "extra"), Dec(root, "extra_month"), Dec(root, "total_month"),
            Str(root, "plan_after"), StringList(root, "removed"), Dec(root, "start_price"), Str(root, "period"), Dec(root, "total_year"), Dec(root, "year_discount"),
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("year_paid_months", out var ypm) && ypm.ValueKind == JsonValueKind.Number && ypm.TryGetInt32(out var ypmN) ? ypmN : null, Dec(root, "total_once"));
    }

    private static List<string> StringList(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())).Select(x => x.GetString()!.Trim()).ToList()
            : new List<string>();

    private static List<TariffQuoteModule> QuoteModules(JsonElement root, string name)
    {
        var list = new List<TariffQuoteModule>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var id = Str(item, "id");
                if (id is null)
                    continue;
                list.Add(new TariffQuoteModule(id, LocalName(item, "name") ?? id, Dec(item, "monthly") ?? 0m));
            }
        return list;
    }

    internal static TariffOrder ParseOrder(JsonElement root) =>
        new(Str(root, "order_id") ?? Str(root, "id") ?? "", Str(root, "status") ?? "pending",
            Dec(root, "amount_monthly") ?? 0m, Dec(root, "amount_once") ?? 0m, Str(root, "pay_url"));

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Сумма: сервер присылает и строкой «1500.00», и числом.</summary>
    private static decimal? Dec(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string CurrentLang() => UserPreferences.Instance.Language switch
    {
        AppLanguage.Kyrgyz => "ky", AppLanguage.English => "en", AppLanguage.Turkish => "tr", AppLanguage.Uzbek => "uz", _ => "ru",
    };

    /// <summary>Название вида {"ru":…, "en":…} — на языке интерфейса, иначе русское, английское, любое; простая строка — как есть.</summary>
    internal static string? LocalName(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim();
        if (v.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { CurrentLang(), "ru", "en" })
            if (v.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                return t.GetString()!.Trim();
        foreach (var prop in v.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                return prop.Value.GetString()!.Trim();
        return null;
    }

    private static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Понятный текст ошибки на языке интерфейса: 400 pack_unknown, 409 plan_not_allowed, 403 не владелец, 401, 429, нет связи; прочее — ответ сервера.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is ApiException api)
        {
            var raw = (api.Payload?.GetRawText() ?? "") + " " + api.Message;
            if (api.StatusCode == 403)
                return Tr.T("Тариф «Своё» оформляет только владелец компании.", "«Өзүмдүкү» тарифин компаниянын ээси гана жөнөтө алат.",
                    "Only the company owner can arrange the “Custom” plan.", "«Özel» tarifeyi yalnızca şirket sahibi oluşturabilir.", "«O'zimniki» tarifini faqat kompaniya egasi rasmiylashtiradi.");
            if (api.StatusCode == 409 || raw.Contains("plan_not_allowed", StringComparison.OrdinalIgnoreCase))
            {
                // 2026-11-20: ограничение ставит СЕРВЕР (409 plan_not_allowed), касса по имени плана ничего не запрещает; жёсткое «поверх Старта» убрано,
                // если сервер прислал свой текст — показываем его.
                var basic = Tr.T("Конструктор «Своё» для вашего тарифа пока недоступен.",
                    "«Өзүмдүкү» конструктору учурдагы тарифиңиз үчүн азырынча жеткиликсиз.",
                    "The “Custom” builder is not available for your current plan yet.",
                    "«Özel» oluşturucu mevcut tarifeniz için henüz kullanılamıyor.",
                    "«O'zimniki» konstruktori joriy tarifingiz uchun hozircha mavjud emas.");
                var server = TariffCustomRules.ServerText(api);
                return server is null ? basic : basic + "\n" + server;
            }
            if (api.StatusCode == 400 && raw.Contains("pack_unknown", StringComparison.OrdinalIgnoreCase))
                return Tr.T("Сервер не знает один из выбранных пакетов — обновите окно и выберите заново.",
                    "Сервер тандалган пакеттердин бирин билбейт — терезени жаңыртып, кайра тандаңыз.",
                    "The server does not know one of the chosen packs — refresh the window and choose again.",
                    "Sunucu seçilen paketlerden birini tanımıyor — pencereyi yenileyip tekrar seçin.",
                    "Server tanlangan paketlardan birini bilmaydi — oynani yangilab, qaytadan tanlang.");
            if (api.StatusCode == 401)
                return Tr.T("Срок входа истёк — войдите заново.", "Кирүү мөөнөтү бүттү — кайра кириңиз.", "Your sign-in has expired — please sign in again.",
                    "Oturum süresi doldu — yeniden giriş yapın.", "Kirish muddati tugadi — qaytadan kiring.");
            if (api.StatusCode == 429)
                return Tr.T("Слишком частые запросы — подождите немного и повторите.", "Өтө тез-тез суроо — бир аз күтүп, кайра аракет кылыңыз.",
                    "Too many requests — wait a moment and try again.", "Çok sık istek — biraz bekleyip tekrar deneyin.", "Juda tez-tez so'rov — biroz kutib, qaytadan urinib ko'ring.");
            return api.Message;
        }

        return ServerOutageMonitor.FriendlyError(ex);
    }
}

/// <summary>2026-11-20 (замечание владельца: «в Стандарте тоже можешь»): правила окна «Своё», не зависящие от окна — чтобы их проверять тестами.
/// Недоступность тарифа определяет СЕРВЕР (409 plan_not_allowed), касса по имени плана ничего не запрещает.</summary>
public static class TariffCustomRules
{
    /// <summary>Код текущего тарифа компании: по коду или по названию из каталога (касса пока знает только имя плана — TariffGate). Не нашли — null.</summary>
    public static string? ResolvePlanCode(TariffCatalog? catalog, string? planNameOrCode)
    {
        if (catalog is null || string.IsNullOrWhiteSpace(planNameOrCode))
            return null;
        var key = planNameOrCode.Trim();
        return catalog.Plans.FirstOrDefault(p => string.Equals(p.Code, key, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Code;
    }

    /// <summary>Пакет уже входит в текущий тариф (included_in каталога) — «Входит в ваш тариф», в цену не считается.</summary>
    public static bool IsIncludedInPlan(TariffPack pack, string? planCode) =>
        !string.IsNullOrWhiteSpace(planCode) && pack.IncludedIn.Any(c => string.Equals(c, planCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>Текст причины от сервера (detail / message / error / reason) — только настоящая фраза, а не код вида plan_not_allowed.</summary>
    public static string? ServerText(ApiException api)
    {
        if (api.Payload is not { ValueKind: JsonValueKind.Object } p)
            return null;
        foreach (var name in new[] { "detail", "message", "error", "reason" })
        {
            if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var text = v.GetString()?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 300 || text.Contains("plan_not_allowed", StringComparison.OrdinalIgnoreCase))
                continue;
            if (text.Any(char.IsWhiteSpace) || text.Any(ch => ch > 127))
                return text;
        }

        return null;
    }
}
