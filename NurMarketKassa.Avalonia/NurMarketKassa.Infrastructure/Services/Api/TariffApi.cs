using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Тарифный план из GET public/tariffs/. Решать по <see cref="Code"/> (start / standard / custom), не по имени.</summary>
public sealed record TariffPlan(string Code, string Name, decimal? Price, string? BaseCode);

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
public sealed record TariffQuote(decimal Monthly, decimal Once, decimal Base, IReadOnlyList<TariffQuotePack> Packs, bool StandardCheaper, decimal? StandardPrice);

/// <summary>Принятая заявка (201): сервер только записывает её, менеджер обрабатывает; платёжной ссылки нет (PayUrl null).</summary>
public sealed record TariffOrder(string OrderId, string Status, decimal AmountMonthly, decimal AmountOnce, string? PayUrl);

/// <summary>Что делает окно тарифа «Своё» — интерфейс нужен, чтобы окно можно было проверить без сервера.</summary>
public interface ITariffApi
{
    Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default);
    Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default);
    Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default);
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

    public async Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Post, Root + "users/company/tariff/quote/", Body(packs), null, ct).ConfigureAwait(false);
        return ParseQuote(data);
    }

    public async Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_api.AccessToken))
            throw new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401);

        // Тот же путь, что у оплаты долга: общая цепочка обработчиков клиента (Bearer, обновление токена, щит 429), свой только заголовок Idempotency-Key.
        // Повтор с тем же ключом сервер отдаёт тем же ответом — заявка не задваивается.
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_api.BaseAddress, Root + "users/company/tariff/custom/"));
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new StringContent(JsonSerializer.Serialize(Body(packs)), Encoding.UTF8, "application/json");
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

    private static Dictionary<string, object?> Body(IReadOnlyList<string> packs) =>
        new() { ["packs"] = packs.ToArray(), ["period"] = "month" }; // период пока только month

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
                    plans.Add(new TariffPlan(code, LocalName(item, "name") ?? code, Dec(item, "price"), Str(item, "base")));
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
        return new TariffQuote(Dec(root, "monthly") ?? 0m, Dec(root, "once") ?? 0m, Dec(root, "base") ?? 0m, packs, cheaper, Dec(root, "standard_price"));
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
                return Tr.T("Для вашего текущего тарифа «Своё» недоступно (оно строится поверх «Старта»). Обратитесь к менеджеру.",
                    "Учурдагы тарифиңиз үчүн «Өзүмдүкү» жеткиликсиз («Старттын» үстүнөн курулат). Менеджерге кайрылыңыз.",
                    "The “Custom” plan is not available on your current plan (it is built on top of “Start”). Please contact the manager.",
                    "Mevcut tarifenizde «Özel» kullanılamaz («Start» üzerine kurulur). Lütfen yöneticiyle iletişime geçin.",
                    "Joriy tarifingizda «O'zimniki» mavjud emas («Start» ustiga quriladi). Menejerga murojaat qiling.");
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
