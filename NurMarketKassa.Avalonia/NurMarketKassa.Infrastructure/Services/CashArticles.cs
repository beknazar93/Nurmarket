using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Статья операции с денежным ящиком (внесение / изъятие). 2026-10-22 (статьи изъятия): владелец — «Налог, аренда
/// тоже добавить при изъятии из кассы как селект». Key — постоянный код (журнал кассы), Ru — канон для сервера, чека и
/// группировки в отчётах владельца (одинаков на всех кассах при любом языке), Names — подпись плитки на 5 языках.</summary>
public sealed class CashArticle
{
    public string Key { get; init; } = "";
    public bool IsWithdrawal { get; init; }
    public string Ru { get; init; } = "";
    public string[] Names { get; init; } = Array.Empty<string>();
    /// <summary>Как статья может называться в справочнике NurCRM (api/construction/cashflow-categories/, поле title).</summary>
    public string[] ServerTitles { get; init; } = Array.Empty<string>();
    /// <summary>Статьи нет среди стандартных на сервере — создать в справочнике при первом изъятии (как «Зарплата» у выплат).</summary>
    public bool CreateOnServer { get; init; }
    /// <summary>Статья только из справочника сервера (её завёл владелец на сайте) — её id.</summary>
    public string? ServerCategoryId { get; init; }

    public string Title => Names.Length >= 5 ? Tr.T(Names[0], Names[1], Names[2], Names[3], Names[4]) : Ru;
    /// <summary>«Прочее» — комментарий обязателен (иначе в отчёте не понять, на что ушли деньги).</summary>
    public bool CommentRequired => Key == CashArticles.OtherKey;
    /// <summary>Инкассация — деньги переданы владельцу: из ящика ушли, но это не расход магазина (прибыль не уменьшает).</summary>
    public bool IsCollection => Key == CashArticles.CollectionKey;
}

/// <summary>2026-10-22 (статьи изъятия): список статей внесения/изъятия и связь со справочником статей расходов NurCRM.
///
/// Что умеет сервер (проверено GET на NBS 22.10): справочник api/construction/cashflow-categories/ (id, title, type
/// expense/income/both, archived, is_default; slug/is_system пока НЕТ) — стандартные статьи «Аренда», «Налоги», «Зарплата»,
/// «Коммунальные», «Закупка товара», «Оплата поставщику», «Реклама», «Прочие», «Внесение»… Движение денег (cashflows) хранит
/// category и отдаёт category_title; веб NurCRM в кассе выбирает статью тем же списком («Без категории» + справочник). Отчёт
/// analytics/pnl делит opex только на salaries / rent / taxes / utilities / other (ТЗ ч. 24.3 — по slug), поэтому остальные
/// статьи окно «Прибыль и деньги» группирует само — по category_title, а для старых записей по названию «Изъятие: Аренда — …».
///
/// Поэтому изъятие уходит с category (id статьи из справочника) И со статьёй в названии: сервер, не принявший category вместе
/// со сменой, всё равно покажет статью. Справочник кэшируется: в памяти на 30 минут и в файле (плитки статей, заведённых
/// владельцем, видны и без связи).</summary>
public static class CashArticles
{
    public const string OtherKey = "other";
    public const string CollectionKey = "collection";
    /// <summary>Приставка ключа статьи, взятой из справочника сервера («srv:&lt;id&gt;»).</summary>
    private const string ServerKeyPrefix = "srv:";

    public static readonly IReadOnlyList<CashArticle> Withdrawals = new[]
    {
        W("rent", "Аренда", new[] { "Аренда", "Ижара", "Rent", "Kira", "Ijara" }, "Аренда"),
        W("tax", "Налог", new[] { "Налог", "Салык", "Tax", "Vergi", "Soliq" }, "Налоги", "Налог"),
        W("salary", "Зарплата", new[] { "Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi" }, "Зарплата", "Зарплаты", "Заработная плата"),
        W("supplier", "Закупка товара / оплата поставщику",
            new[] { "Закупка товара / оплата поставщику", "Товар сатып алуу / жеткирүүчүгө төлөм", "Stock purchase / supplier payment",
                "Mal alımı / tedarikçi ödemesi", "Tovar xaridi / yetkazib beruvchiga to'lov" },
            "Оплата поставщику", "Закупка товара", "Закупки"),
        W("utilities", "Коммунальные услуги", new[] { "Коммунальные услуги", "Коммуналдык кызматтар", "Utilities", "Faturalar", "Kommunal xizmatlar" },
            "Коммунальные", "Коммунальные услуги", "Коммуналка"),
        W("transport", "Транспорт / доставка", new[] { "Транспорт / доставка", "Транспорт / жеткирүү", "Transport / delivery", "Ulaşım / teslimat", "Transport / yetkazib berish" },
            create: true, "Транспорт / доставка", "Транспорт", "Доставка"),
        W("household", "Хозяйственные нужды", new[] { "Хозяйственные нужды", "Чарба муктаждыктары", "Household needs", "Genel ihtiyaçlar", "Xo'jalik ehtiyojlari" },
            create: true, "Хозяйственные нужды", "Хозяйственные", "Хознужды"),
        W(CollectionKey, "Инкассация",
            new[] { "Инкассация (сдача денег владельцу)", "Инкассация (акчаны ээсине тапшыруу)", "Cash collection (handed to the owner)",
                "Para teslimi (sahibine)", "Inkassatsiya (pulni egasiga topshirish)" },
            create: true, "Инкассация"),
        W(OtherKey, "Прочее", new[] { "Прочее", "Башка", "Other", "Diğer", "Boshqa" }, "Прочие", "Прочее"),
    };

    public static readonly IReadOnlyList<CashArticle> Deposits = new[]
    {
        D("change", "Размен", new[] { "Размен", "Майда акча", "Change float", "Bozuk para", "Mayda pul" }),
        D("collection_return", "Возврат из инкассации",
            new[] { "Возврат из инкассации", "Инкассациядан кайтаруу", "Return from collection", "Teslimden iade", "Inkassatsiyadan qaytarish" }),
        D(OtherKey, "Прочее", new[] { "Прочее", "Башка", "Other", "Diğer", "Boshqa" }),
    };

    private static CashArticle W(string key, string ru, string[] names, params string[] serverTitles) =>
        new() { Key = key, IsWithdrawal = true, Ru = ru, Names = names, ServerTitles = serverTitles };

    private static CashArticle W(string key, string ru, string[] names, bool create, params string[] serverTitles) =>
        new() { Key = key, IsWithdrawal = true, Ru = ru, Names = names, ServerTitles = serverTitles, CreateOnServer = create };

    // Внесение на сервере — стандартная статья прихода «Внесение» (источник — в названии движения).
    private static CashArticle D(string key, string ru, string[] names) =>
        new() { Key = key, IsWithdrawal = false, Ru = ru, Names = names, ServerTitles = new[] { "Внесение" } };

    /// <summary>Плитки окна внесения/изъятия: стандартные статьи + статьи расходов, которые владелец сам завёл на сайте
    /// (из кэша справочника), «Прочее» — последней; последняя использованная на этом ПК — первой (не выбрана: кассир
    /// выбирает осознанно).</summary>
    public static IReadOnlyList<CashArticle> ForDialog(bool withdrawal)
    {
        var list = (withdrawal ? Withdrawals : Deposits).ToList();
        if (withdrawal)
        {
            var extra = CachedCategories()
                .Where(c => !c.Archived && c.Type is "expense" or "both" && !c.Title.Equals("Без категории", StringComparison.OrdinalIgnoreCase))
                .Where(c => Withdrawals.All(a => !Matches(a, c.Title)))
                .Select(c => new CashArticle
                {
                    Key = ServerKeyPrefix + c.Id, IsWithdrawal = true, Ru = c.Title, Names = Array.Empty<string>(),
                    ServerTitles = new[] { c.Title }, ServerCategoryId = c.Id,
                })
                .ToList();
            list.InsertRange(list.Count - 1, extra);
        }

        var last = LastUsedKey(withdrawal);
        var lastIndex = list.FindIndex(a => a.Key == last);
        if (lastIndex > 0)
        {
            var item = list[lastIndex];
            list.RemoveAt(lastIndex);
            list.Insert(0, item);
        }
        return list;
    }

    public static CashArticle? Find(bool withdrawal, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (key.StartsWith(ServerKeyPrefix, StringComparison.Ordinal))
            return ForDialog(withdrawal).FirstOrDefault(a => a.Key == key);
        return (withdrawal ? Withdrawals : Deposits).FirstOrDefault(a => a.Key == key);
    }

    /// <summary>Подпись статьи на текущем языке по ключу из журнала кассы; статья с сайта — её название (Ru из журнала).</summary>
    public static string TitleFor(bool withdrawal, string? key, string? storedRu) =>
        Find(withdrawal, key) is { Names.Length: >= 5 } a ? a.Title : storedRu ?? "";

    /// <summary>Совпадает ли название статьи сервера с нашей статьёй (без регистра, по канону и вариантам).</summary>
    public static bool Matches(CashArticle article, string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0)
            return false;
        return t.Equals(article.Ru, StringComparison.OrdinalIgnoreCase)
               || article.ServerTitles.Any(s => t.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Название движения на сервере: «Изъятие: Аренда — за октябрь». Без статьи — как раньше.</summary>
    public static string FlowName(string type, string? articleRu, string? comment)
    {
        var c = (comment ?? "").Trim();
        string name;
        if (!string.IsNullOrWhiteSpace(articleRu))
            name = c.Length > 0 ? $"{type}: {articleRu.Trim()} — {c}" : $"{type}: {articleRu.Trim()}";
        else
            name = c.Length > 0 ? $"{type}: {c}" : type;
        return name.Length > 255 ? name[..255] : name;
    }

    /// <summary>Статья движения для отчётов владельца: category_title с сервера; нет — из названия «Изъятие: &lt;статья&gt; — …»
    /// (только если это одна из наших статей). null — статьи нет (старые изъятия со свободной причиной).</summary>
    public static CashArticle? FromFlow(string? name, string? categoryTitle)
    {
        if (!string.IsNullOrWhiteSpace(categoryTitle))
        {
            return Withdrawals.FirstOrDefault(a => Matches(a, categoryTitle))
                   ?? new CashArticle { Key = ServerKeyPrefix + categoryTitle.Trim(), IsWithdrawal = true, Ru = categoryTitle.Trim() };
        }

        var n = (name ?? "").Trim();
        var colon = n.IndexOf(':');
        if (colon < 0)
            return null;
        var rest = n[(colon + 1)..].Trim();
        var dash = rest.IndexOf(" — ", StringComparison.Ordinal);
        var head = dash >= 0 ? rest[..dash].Trim() : rest;
        return Withdrawals.FirstOrDefault(a => Matches(a, head));
    }

    // ── Справочник статей NurCRM ───────────────────────────────────────────────────

    public sealed record ServerCategory(string Id, string Title, string Type, bool Archived);

    private static readonly object CacheLock = new();
    private static List<ServerCategory>? _memory;
    private static string? _memoryCompany;
    private static DateTime _memoryUntilUtc;
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    private static string CompanyKey => CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : "default";

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "cash_articles.json");

    /// <summary>Справочник из кэша (память → файл) без запроса к серверу.</summary>
    public static IReadOnlyList<ServerCategory> CachedCategories()
    {
        lock (CacheLock)
        {
            if (_memory is not null && _memoryCompany == CompanyKey)
                return _memory;
        }
        var file = LoadFile();
        return file.Categories.TryGetValue(CompanyKey, out var list) ? list : Array.Empty<ServerCategory>();
    }

    /// <summary>Справочник статей с сервера (кэш 30 минут). null — сервер не ответил (тогда — то, что в кэше).</summary>
    public static async Task<IReadOnlyList<ServerCategory>?> LoadCategoriesAsync(bool force, CancellationToken ct)
    {
        var company = CompanyKey;
        lock (CacheLock)
        {
            if (!force && _memory is not null && _memoryCompany == company && DateTime.UtcNow < _memoryUntilUtc)
                return _memory;
        }
        if (NurCrmReportsApi.ClientResolver?.Invoke() is not { AccessToken.Length: > 0 } api)
            return null;

        await LoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = new List<ServerCategory>();
            for (var page = 1; page <= 5; page++)
            {
                var data = await api.RequestAsync(HttpMethod.Get, "api/construction/cashflow-categories/", null,
                    new Dictionary<string, string> { ["page_size"] = "500", ["page"] = page.ToString(CultureInfo.InvariantCulture) },
                    ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                foreach (var x in NurMarketApiClient.UnwrapList(data))
                {
                    var id = Str(x, "id") ?? Str(x, "uuid");
                    var title = Str(x, "title") ?? Str(x, "name");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(title))
                        continue;
                    var archived = x.TryGetProperty("archived", out var a) && a.ValueKind == JsonValueKind.True;
                    rows.Add(new ServerCategory(id, title.Trim(), (Str(x, "type") ?? "both").Trim().ToLowerInvariant(), archived));
                }
                if (!(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                    break;
            }
            Remember(company, rows);
            return rows;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Статьи изъятия: справочник статей NurCRM не прочитан ({ex.Message}) — плитки из кэша.", "WARNING");
            return null;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    /// <summary>id статьи в справочнике сервера для операции. Нет такой — для «Транспорт», «Хозяйственные нужды»,
    /// «Инкассация» создаётся один раз (title, как веб NurCRM). null — справочника нет / не ответил / создать не дали:
    /// движение уходит без category (статья всё равно в названии).</summary>
    public static async Task<string?> ServerCategoryIdAsync(bool withdrawal, string? key, string? articleRu, CancellationToken ct)
    {
        var article = Find(withdrawal, key);
        if (article?.ServerCategoryId is { Length: > 0 } direct)
            return direct;
        if (article is null && key?.StartsWith(ServerKeyPrefix, StringComparison.Ordinal) == true)
            return key[ServerKeyPrefix.Length..];
        if (article is null)
            return null;

        var categories = await LoadCategoriesAsync(force: false, ct).ConfigureAwait(false) ?? CachedCategories();
        // По порядку вариантов статьи: оплата поставщику из кассы — «Оплата поставщику», а не системная «Закупка товара»
        // (её ставит приход товара), хотя на сервере та идёт в списке раньше.
        string? FindId()
        {
            var usable = categories.Where(c => !c.Archived && (withdrawal ? c.Type is "expense" or "both" : c.Type is "income" or "both")).ToList();
            foreach (var title in article.ServerTitles.Prepend(article.Ru))
                if (usable.FirstOrDefault(c => c.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    return hit.Id;
            return null;
        }
        if (FindId() is { } found)
            return found;
        if (!withdrawal || !article.CreateOnServer || NurCrmReportsApi.ClientResolver?.Invoke() is not { AccessToken.Length: > 0 } api)
            return null;

        // Свежий справочник перед созданием — статью мог только что завести другой ПК.
        categories = await LoadCategoriesAsync(force: true, ct).ConfigureAwait(false) ?? categories;
        if (FindId() is { } again)
            return again;
        var title = article.ServerTitles.Length > 0 ? article.ServerTitles[0] : article.Ru;
        try
        {
            var created = await api.RequestAsync(HttpMethod.Post, "api/construction/cashflow-categories/",
                new Dictionary<string, string> { ["title"] = title }, null, ct, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            var id = Str(created, "id") ?? Str(created, "uuid");
            PosLogger.Log($"Статьи изъятия: в справочнике NurCRM создана статья «{title}» ({id ?? "сервер не вернул id"}).", "SHIFT");
            if (!string.IsNullOrEmpty(id))
            {
                lock (CacheLock)
                    _memoryUntilUtc = DateTime.MinValue; // перечитать при следующем изъятии
            }
            return id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Статьи изъятия: статью «{title}» создать в NurCRM не удалось ({ex.Message}) — изъятие без category, статья в названии.", "WARNING");
            return null;
        }
    }

    // ── Отчёт владельца: изъятия из кассы по статьям ──────────────────────────────

    /// <summary>Сумма изъятий одной статьи за период. IsCollection — инкассация (не расход магазина).</summary>
    public sealed record ArticleTotal(string Title, double Amount, int Count, bool IsCollection);

    /// <summary>Изъятия из ящика смен за период по статьям (GET api/construction/cashflows/?source_kind=shift_drawer_outflow):
    /// статья — category_title, для старых записей — из названия «Изъятие: Аренда — …», иначе «Без статьи». Дата и вид
    /// проверяются ещё раз здесь (сервер может проигнорировать фильтр). Отклонённые (status rejected) не считаются.
    /// null — сервер не ответил.</summary>
    public static async Task<IReadOnlyList<ArticleTotal>?> WithdrawalsByArticleAsync(
        NurMarketApiClient api, DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            var groups = new Dictionary<string, (string Title, double Amount, int Count, bool IsCollection)>(StringComparer.OrdinalIgnoreCase);
            var noArticle = Tr.T("Без статьи (до 22.10.2026 — свободная причина)", "Беренесиз (22.10.2026 чейин — эркин себеп)",
                "No category (before 22.10.2026 — free-text reason)", "Kalemsiz (22.10.2026 öncesi — serbest neden)",
                "Moddasiz (22.10.2026 gacha — erkin sabab)");
            for (var page = 1; page <= 20; page++)
            {
                var data = await api.RequestAsync(HttpMethod.Get, "api/construction/cashflows/", null,
                    new Dictionary<string, string>
                    {
                        ["source_kind"] = "shift_drawer_outflow",
                        ["type"] = "expense",
                        ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["page_size"] = "500",
                        ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    }, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                foreach (var row in NurMarketApiClient.UnwrapList(data))
                {
                    if (!string.Equals(Str(row, "source_kind"), "shift_drawer_outflow", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Str(row, "status"), "rejected", StringComparison.OrdinalIgnoreCase)
                        || !DateTimeOffset.TryParse(Str(row, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        || at.LocalDateTime.Date < from.Date || at.LocalDateTime.Date > to.Date)
                        continue;
                    var amountText = row.TryGetProperty("amount", out var a)
                        ? a.ValueKind == JsonValueKind.Number ? a.GetRawText() : a.ValueKind == JsonValueKind.String ? a.GetString() : null
                        : null;
                    if (!double.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                        continue;
                    var article = FromFlow(Str(row, "name"), Str(row, "category_title"));
                    var key = article?.Ru ?? "";
                    var title = article is null ? noArticle : article.Names.Length >= 5 ? article.Title : article.Ru;
                    var old = groups.TryGetValue(key, out var g) ? g : (title, 0d, 0, article?.IsCollection == true);
                    groups[key] = (old.Item1, old.Item2 + amount, old.Item3 + 1, old.Item4);
                }
                if (!(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                    break;
            }
            return groups.Values
                .Select(g => new ArticleTotal(g.Title, Math.Round(g.Amount, 2), g.Count, g.IsCollection))
                .OrderBy(g => g.IsCollection)
                .ThenByDescending(g => g.Amount)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Изъятия по статьям не получены с сервера: {ex.Message}", "WARNING");
            return null;
        }
    }

    // ── Расходы по названию статьи (зарплата, аренда, налоги, коммунальные) ────────

    /// <summary>2026-10-22 (зарплата: источник, статьи): к какой строке отчёта относится название статьи расхода. «Зарплата» /
    /// «Выплата зарплаты» / «Заработная плата» / «ЗП», «Аренда», «Налог(и)» / «Салык», «Коммунальные»; нет — null («Прочие»).
    /// Ключи те же, что у analytics/pnl → opex и analytics/cashflow → outflow: salaries, rent, taxes, utilities.</summary>
    public static string? NamedExpenseKey(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0)
            return null;
        if (t.Contains("зарплат") || t.Contains("заработн") || t is "зп" || t.StartsWith("зп ") || t.Contains("эмгек акы") || t.Contains("salary"))
            return "salaries";
        if (t.Contains("аренд") || t.Contains("ижара") || t.Contains("rent"))
            return "rent";
        if (t.Contains("налог") || t.Contains("салык") || t.Contains("tax"))
            return "taxes";
        if (t.Contains("коммунал"))
            return "utilities";
        return null;
    }

    /// <summary>Расходы за период по названию статьи: salaries / rent / taxes / utilities → сумма (GET api/construction/cashflows/
    /// ?type=expense). Статья — category_title; нет — по названию («Зарплата: ФИО (…)», «Изъятие: Аренда — …»). Не одобренные
    /// (pending/rejected) не считаются. Инкассация сюда не попадает. null — сервер не ответил.</summary>
    public static async Task<Dictionary<string, double>?> ExpensesByNamedCategoryAsync(
        NurMarketApiClient api, DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (var page = 1; page <= 20; page++)
            {
                var data = await api.RequestAsync(HttpMethod.Get, "api/construction/cashflows/", null,
                    new Dictionary<string, string>
                    {
                        ["type"] = "expense",
                        ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["page_size"] = "500",
                        ["page"] = page.ToString(CultureInfo.InvariantCulture),
                    }, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                foreach (var row in NurMarketApiClient.UnwrapList(data))
                {
                    var status = Str(row, "status");
                    if (!string.Equals(Str(row, "type"), "expense", StringComparison.OrdinalIgnoreCase)
                        || (status is { Length: > 0 } && !string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
                        || !DateTimeOffset.TryParse(Str(row, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                        || at.LocalDateTime.Date < from.Date || at.LocalDateTime.Date > to.Date)
                        continue;
                    var amountText = row.TryGetProperty("amount", out var a)
                        ? a.ValueKind == JsonValueKind.Number ? a.GetRawText() : a.ValueKind == JsonValueKind.String ? a.GetString() : null
                        : null;
                    if (!double.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                        continue;
                    var name = Str(row, "name") ?? "";
                    string? key;
                    if (!string.IsNullOrWhiteSpace(Str(row, "category_title")))
                        key = NamedExpenseKey(Str(row, "category_title"));
                    else if (name.StartsWith("Зарплата:", StringComparison.OrdinalIgnoreCase))
                        key = "salaries";
                    else
                        key = FromFlow(name, null) is { IsCollection: false } art ? NamedExpenseKey(art.Ru) : null;
                    if (key is null)
                        continue;
                    sums[key] = (sums.TryGetValue(key, out var old) ? old : 0) + amount;
                }
                if (!(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String))
                    break;
            }
            return sums.ToDictionary(k => k.Key, k => Math.Round(k.Value, 2), StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Расходы по статьям (зарплата/аренда/налоги) не получены с сервера: {ex.Message}", "WARNING");
            return null;
        }
    }

    /// <summary>Пояснение к инкассации в отчётах владельца (окно «Прибыль и деньги», Word/Excel).</summary>
    public static string CollectionNote => Tr.T(
        "Инкассация — деньги сданы владельцу, это не расход магазина. Если NurCRM учёл её в «Прочих расходах», чистая прибыль занижена на эту сумму (сервер пока не отделяет инкассацию).",
        "Инкассация — акча ээсине тапшырылды, бул дүкөндүн чыгымы эмес. Эгер NurCRM аны «Башка чыгымдарга» кошкон болсо, таза пайда ушул суммага азыраак көрсөтүлдү (сервер азырынча инкассацияны бөлбөйт).",
        "Cash collection is money handed to the owner, not a shop expense. If NurCRM counted it in “Other expenses”, net profit is understated by this amount (the server does not separate collections yet).",
        "Para teslimi sahibine verilen paradır, mağaza gideri değildir. NurCRM bunu «Diğer giderler»e kattıysa net kâr bu tutar kadar düşük görünür (sunucu henüz ayırmıyor).",
        "Inkassatsiya — egasiga topshirilgan pul, do'kon xarajati emas. Agar NurCRM uni «Boshqa xarajatlar»ga qo'shgan bo'lsa, sof foyda shu summaga kam ko'rsatilgan (server hali ajratmaydi).");

    // ── Последняя статья и файл кэша ───────────────────────────────────────────────

    public static void RememberLastUsed(bool withdrawal, string key)
    {
        try
        {
            lock (CacheLock)
            {
                var file = LoadFile();
                if (withdrawal) file.LastWithdrawal = key; else file.LastDeposit = key;
                SaveFile(file);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Статьи изъятия: последняя статья не запомнена: {ex.Message}", "WARNING");
        }
    }

    private static string? LastUsedKey(bool withdrawal)
    {
        var file = LoadFile();
        return withdrawal ? file.LastWithdrawal : file.LastDeposit;
    }

    private static void Remember(string company, List<ServerCategory> rows)
    {
        lock (CacheLock)
        {
            _memory = rows;
            _memoryCompany = company;
            _memoryUntilUtc = DateTime.UtcNow.AddMinutes(30);
            try
            {
                var file = LoadFile();
                if (file.Categories.TryGetValue(company, out var old) && old.SequenceEqual(rows))
                    return;
                file.Categories[company] = rows;
                SaveFile(file);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Статьи изъятия: кэш справочника не записан: {ex.Message}", "WARNING");
            }
        }
    }

    private sealed class CacheFile
    {
        public string? LastWithdrawal { get; set; }
        public string? LastDeposit { get; set; }
        public Dictionary<string, List<ServerCategory>> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private static CacheFile LoadFile()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath), JsonOpts) ?? new CacheFile();
                file.Categories = new Dictionary<string, List<ServerCategory>>(file.Categories ?? new(), StringComparer.OrdinalIgnoreCase);
                return file;
            }
        }
        catch (Exception)
        {
            // Повреждённый кэш — как пустой: плитки стандартные, справочник перечитается.
        }
        return new CacheFile();
    }

    private static void SaveFile(CacheFile file)
    {
        var path = CacheFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOpts));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
