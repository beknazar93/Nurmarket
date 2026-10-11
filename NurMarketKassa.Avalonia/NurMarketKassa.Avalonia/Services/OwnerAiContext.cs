using System.Globalization;
using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;


namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец: «дай доступ ко всему для ИИ — к долгам, к контактам, клиентам, к складу, к товарам,
/// поставщикам». Склад для «ИИ-советника» программы владельца: товары с категорией, ценой продажи, закупкой и
/// остатком (локальный каталог программы) и итоги склада. Персональные данные (имена и телефоны клиентов,
/// должники, контакты поставщиков) сюда не входят — их отправка в Google ждёт решения владельца.</summary>
public static class OwnerAiContext
{
    private static readonly SemaphoreSlim DebtGate = new(1, 1);
    private static string? _debtCached;
    private static DateTime _debtCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «я должен кому-то или мне должны?» — советник не знал. Обезличенные итоги
    /// долгов: сколько должны клиенты (сумма, сколько человек и чеков, крупнейшие суммы и давность — без имён и
    /// телефонов) и поставщики (число и сумма долга, если сервер её отдаёт). Сервер — не чаще раза в 5 минут.</summary>
    public static async Task<string> BuildDebtTotalsAsync(CancellationToken ct)
    {
        await DebtGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_debtCached is not null && DateTime.UtcNow - _debtCachedAtUtc < TimeSpan.FromMinutes(5))
                return _debtCached;
            var sb = new StringBuilder();
            try
            {
                var debts = await App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>()
                    .PosDebtSalesAsync(null, ct).ConfigureAwait(false);
                var amounts = new List<double>();
                var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime? oldest = null;
                foreach (var debt in debts)
                {
                    // debt_amount — остаток долга; нет его — сумма чека (как в отчёте бота «Должники»).
                    var amount = Num(debt, "debt_amount") ?? Num(debt, "total") ?? 0;
                    if (amount <= 0.005)
                        continue;
                    amounts.Add(amount);
                    if (Str(debt, "client") is { Length: > 0 } client)
                        clients.Add(client);
                    if (DateTime.TryParse(Str(debt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at)
                        && (oldest is null || at < oldest))
                        oldest = at;
                }
                sb.AppendLine(amounts.Count == 0
                    ? "ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (сервер NurCRM): непогашенных долгов нет."
                    : $"ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (сервер NurCRM): вам должны {amounts.Sum().ToString("N2", Ru)} сом — {clients.Count} клиент(ов), " +
                      $"{amounts.Count} чек(ов) в долг; крупнейшие долги по чекам: {string.Join(", ", amounts.OrderByDescending(a => a).Take(5).Select(a => a.ToString("N2", Ru)))} сом" +
                      (oldest is { } o ? $"; самый старый долг с {o.ToLocalTime():dd.MM.yyyy}" : "") +
                      ". Имена и телефоны должников — в разделе «Клиенты» (советнику они не передаются).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: долги клиентов не получены ({ex.Message}).", "WARNING");
                sb.AppendLine("ДОЛГИ КЛИЕНТОВ: сервер сейчас не ответил — подскажи раздел «Клиенты».");
            }

            try
            {
                var data = await PosApp.CatalogApi.ListSuppliersAsync(ct).ConfigureAwait(false);
                var rows = data.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? data.EnumerateArray().ToList()
                    : data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? r.EnumerateArray().ToList()
                        : new List<System.Text.Json.JsonElement>();
                var owed = rows.Select(row => Num(row, "debt") ?? Num(row, "total_debt") ?? Num(row, "balance")).Where(v => v is not null).Select(v => v!.Value).ToList();
                sb.AppendLine(rows.Count == 0
                    ? "ПОСТАВЩИКИ (сервер NurCRM): поставщиков в базе нет."
                    : owed.Count == 0
                        ? $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; сумм долга перед поставщиками сервер не отдаёт — подскажи раздел «Склад» → приходы."
                        : $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; по данным сервера сумма по поставщикам {owed.Sum().ToString("N2", Ru)} сом " +
                          $"(у {owed.Count(v => Math.Abs(v) > 0.005)} есть ненулевая сумма).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: поставщики не получены ({ex.Message}).", "WARNING");
            }

            _debtCached = sb.ToString().Trim();
            _debtCachedAtUtc = DateTime.UtcNow;
            return _debtCached;
        }
        finally
        {
            DebtGate.Release();
        }
    }

    private static readonly SemaphoreSlim AbcGate = new(1, 1);
    private static string? _abcCached;
    private static DateTime _abcCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «дай доступ к ABC-анализу ИИ». Тот же расчёт, что в разделе «ABC-анализ»
    /// (AnalyticsReportData — цифры сервера NurCRM, без связи — история продаж) за последние 30 дней, все срезы:
    /// выручка, прибыль, количество, категории, бренды, склад. По каждому срезу — итоги групп A/B/C и товары
    /// группы (A — все до 40, B и C — до 25). Пересчёт не чаще раза в 10 минут.</summary>
    public static async Task<string> BuildAbcAsync(CancellationToken ct)
    {
        await AbcGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_abcCached is not null && DateTime.UtcNow - _abcCachedAtUtc < TimeSpan.FromMinutes(10))
                return _abcCached;
            var to = DateTime.Today;
            var from = to.AddDays(-29);
            var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, includeSeasonality: false, ct: ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"ABC-АНАЛИЗ за {from:dd.MM}–{to:dd.MM.yyyy} (как в разделе «ABC-анализ»; A — первые 80 % меры, B — следующие 15 %, C — остальные 5 %):");
            if (data.AbcSlices.Count == 0)
                sb.AppendLine("продаж за период нет — ABC не построен.");
            foreach (var slice in data.AbcSlices)
            {
                string Fmt(double v) => slice.IsMoney ? $"{v.ToString("N2", Ru)} сом" : $"{v.ToString("0.###", CultureInfo.InvariantCulture)} {slice.Unit}";
                sb.AppendLine($"Срез «{slice.Title}»: " + string.Join("; ", slice.Summary.Select(g =>
                    $"группа {g.Group} — {g.Count} поз., {Fmt(g.Sum)} ({g.Share:0.#} %)")));
                foreach (var group in new[] { "A", "B", "C" })
                {
                    var rows = slice.Rows.Where(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (rows.Count == 0)
                        continue;
                    var take = group == "A" ? 40 : 25;
                    sb.AppendLine($"  {group}: " + string.Join("; ", rows.Take(take).Select(r => $"{r.Name} — {Fmt(r.Sum)} ({r.Share:0.#} %)"))
                                  + (rows.Count > take ? $"; … и ещё {rows.Count - take}" : ""));
                }
            }
            _abcCached = sb.ToString().TrimEnd();
            _abcCachedAtUtc = DateTime.UtcNow;
            return _abcCached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: ABC-анализ не построен ({ex.Message}).", "WARNING");
            return "ABC-АНАЛИЗ: сейчас не построился — подскажи раздел «ABC-анализ».";
        }
        finally
        {
            AbcGate.Release();
        }
    }

    private static readonly SemaphoreSlim AnalysisGate = new(1, 1);
    private static string? _analysisCached;
    private static DateTime _analysisCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «в ИИ добавь анализ продаж, анализ склада, анализ клиентов, анализ заказов,
    /// чтобы он смог потом предлагать назначить акции на проблемные товары». Готовые выводы для нейросети:
    /// • товары без продаж 30 дней при остатке (замороженные деньги);
    /// • затоваривание — запаса больше чем на 60 дней продаж;
    /// • падение продаж — последние 15 дней против предыдущих 15;
    /// • низкая наценка (меньше 10 %) и продажа ниже закупки;
    /// • растущие товары (кандидаты в «паровоз» для комплекта со слабыми);
    /// • клиенты — сколько всего и новых за 30 дней (без имён); заказы через Telegram-бота за 30 дней.
    /// Продажи — тот же расчёт, что «ABC-анализ» (AnalyticsReportData, цифры сервера); склад — каталог программы.
    /// Пересчёт не чаще раза в 10 минут.</summary>
    public static async Task<string> BuildAnalysisAsync(CancellationToken ct)
    {
        await AnalysisGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_analysisCached is not null && DateTime.UtcNow - _analysisCachedAtUtc < TimeSpan.FromMinutes(10))
                return _analysisCached;
            var sb = new StringBuilder();
            sb.AppendLine("АНАЛИЗ ТОВАРОВ И АКЦИЙ (30 дней; используй его для акций на проблемные товары и для заказа: что закончилось и что заканчивается):");
            try
            {
                await AppendProductAnalysisAsync(sb, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: анализ товаров не построен ({ex.Message}).", "WARNING");
                sb.AppendLine("Анализ товаров сейчас не построился.");
            }
            await AppendClientsAndOrdersAsync(sb, ct).ConfigureAwait(false);
            _analysisCached = sb.ToString().TrimEnd();
            _analysisCachedAtUtc = DateTime.UtcNow;
            return _analysisCached;
        }
        finally
        {
            AnalysisGate.Release();
        }
    }

    private static async Task AppendProductAnalysisAsync(StringBuilder sb, CancellationToken ct)
    {
        var today = DateTime.Today;
        var recent = await AnalyticsReportData.BuildAsync(App.SalesApi, today.AddDays(-14), today, includeSeasonality: false, ct: ct).ConfigureAwait(false);
        var before = await AnalyticsReportData.BuildAsync(App.SalesApi, today.AddDays(-29), today.AddDays(-15), includeSeasonality: false, ct: ct).ConfigureAwait(false);
        static Dictionary<string, double> SoldQty(AnalyticsReportData d)
        {
            var rows = d.Slice(AnalyticsReportData.KeyRevenue)?.Rows ?? d.Abc;
            return rows.GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity), StringComparer.OrdinalIgnoreCase);
        }
        var q2 = SoldQty(recent);
        var q1 = SoldQty(before);

        IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
        if (tiles.Count == 0)
            tiles = CatalogCacheService.Products.ToList();
        static double Price(CatalogProductTileVm t) => LocalCartService.ParsePrice(t.PriceLine);
        string Money(double v) => v.ToString("N0", Ru) + " сом";

        var rows = tiles
            .Where(t => !t.IsService)
            .Select(t =>
            {
                var name = t.Title.Trim();
                var sold30 = (q1.TryGetValue(name, out var a) ? a : 0) + (q2.TryGetValue(name, out var b) ? b : 0);
                var perDay = sold30 / 30.0;
                var price = Price(t);
                var markup = t.PurchasePrice > 0 ? (price - t.PurchasePrice) / t.PurchasePrice * 100 : (double?)null;
                return new
                {
                    Tile = t, Name = name, Stock = t.Quantity, Sold30 = sold30, PerDay = perDay,
                    Recent = q2.TryGetValue(name, out var r2) ? r2 : 0, Before = q1.TryGetValue(name, out var r1) ? r1 : 0,
                    Price = price, Cost = t.PurchasePrice, Markup = markup,
                    Frozen = Math.Max(0, t.Quantity) * (t.PurchasePrice > 0 ? t.PurchasePrice : price),
                };
            })
            .ToList();

        var dead = rows.Where(r => r.Stock > 0 && r.Sold30 <= 0).OrderByDescending(r => r.Frozen).Take(15).ToList();
        sb.AppendLine($"• Не продавались 30 дней, но лежат на складе — {rows.Count(r => r.Stock > 0 && r.Sold30 <= 0)} поз., " +
                      $"заморожено {Money(dead.Sum(r => r.Frozen))} (по закупке): " +
                      (dead.Count == 0 ? "нет." : string.Join("; ", dead.Select(r => $"{r.Name} — остаток {r.Stock:0.###}, {Money(r.Frozen)}"))));

        // 2026-10-06, владелец: «хорошо проданные товары, архив — что закончилось, и какие популярные остались в малом количестве — добавь в ИИ».
        var soldOut = rows.Where(r => r.Stock <= 0 && r.Sold30 > 0).OrderByDescending(r => r.Sold30).Take(15).ToList();
        sb.AppendLine("• ХОРОШО ПРОДАВАЛИСЬ, НО ЗАКОНЧИЛИСЬ (остаток 0, продажи за 30 дней): " +
                      (soldOut.Count == 0 ? "нет." : string.Join("; ", soldOut.Select(r =>
                          $"{r.Name} — продано {r.Sold30:0.###} за 30 дн. (≈{r.PerDay:0.#} в день), теряется ≈{Money(r.PerDay * r.Price)} выручки в день, на 14 дней заказать ≈{Math.Ceiling(r.PerDay * 14):0}"))));
        var lowPopular = rows.Where(r => r.Stock > 0 && r.PerDay > 0 && r.Stock / r.PerDay < 7).OrderByDescending(r => r.Sold30).Take(15).ToList();
        sb.AppendLine("• ПОПУЛЯРНЫЕ, НО ОСТАЛОСЬ МАЛО (запаса меньше чем на 7 дней продаж): " +
                      (lowPopular.Count == 0 ? "нет." : string.Join("; ", lowPopular.Select(r =>
                          $"{r.Name} — остаток {r.Stock:0.###}, хватит ≈{r.Stock / r.PerDay:0.#} дн., продаётся ≈{r.PerDay:0.#} в день, на 14 дней заказать ≈{Math.Max(0, Math.Ceiling(r.PerDay * 14 - r.Stock)):0}"))));

        var overstock = rows.Where(r => r.PerDay > 0 && r.Stock / r.PerDay > 60).OrderByDescending(r => r.Stock / r.PerDay).Take(12).ToList();
        sb.AppendLine("• Затоварено (запаса больше чем на 60 дней продаж): " +
                      (overstock.Count == 0 ? "нет." : string.Join("; ", overstock.Select(r =>
                          $"{r.Name} — остаток {r.Stock:0.###}, продаётся {r.PerDay:0.##} в день, хватит на {r.Stock / r.PerDay:0} дн."))));

        var falling = rows.Where(r => r.Before >= 3 && r.Recent < r.Before * 0.6).OrderBy(r => r.Recent / Math.Max(1, r.Before)).Take(10).ToList();
        sb.AppendLine("• Продажи упали (последние 15 дней против предыдущих 15): " +
                      (falling.Count == 0 ? "нет." : string.Join("; ", falling.Select(r => $"{r.Name} — было {r.Before:0.###}, стало {r.Recent:0.###}"))));

        var lowMargin = rows.Where(r => r.Markup is { } m && m < 10).OrderBy(r => r.Markup).Take(12).ToList();
        sb.AppendLine("• Низкая наценка (меньше 10 %) или цена ниже закупки — скидку на них давать нельзя: " +
                      (lowMargin.Count == 0 ? "нет." : string.Join("; ", lowMargin.Select(r =>
                          $"{r.Name} — цена {r.Price:0.##}, закупка {r.Cost:0.##}, наценка {r.Markup:0.#} %"))));

        var growing = rows.Where(r => r.Recent >= 3 && r.Recent > r.Before * 1.3).OrderByDescending(r => r.Recent - r.Before).Take(6).ToList();
        sb.AppendLine("• Растут (хорошие «паровозы» для комплекта со слабым товаром): " +
                      (growing.Count == 0 ? "нет." : string.Join("; ", growing.Select(r => $"{r.Name} — было {r.Before:0.###}, стало {r.Recent:0.###}"))));

        var withCost = rows.Where(r => r.Stock > 0 && r.Cost > 0).ToList();
        sb.AppendLine($"• Склад: {rows.Count(r => r.Stock > 0)} позиций в наличии, по закупке {Money(withCost.Sum(r => r.Stock * r.Cost))}; " +
                      $"нет в наличии {rows.Count(r => r.Stock <= 0)}.");
        sb.AppendLine("Правила акций: скидка не ниже закупки + 5 %; на мёртвый остаток — скидка 15–30 % или «2 по цене 1,5»; " +
                      "затоваренное — в комплект с растущим товаром; падающее — выкладка/напоминание в боте; товары с низкой наценкой — без скидки.");
    }

    private static async Task AppendClientsAndOrdersAsync(StringBuilder sb, CancellationToken ct)
    {
        try
        {
            var api = App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>();
            int total = 0, recent = 0;
            var since = DateTime.UtcNow.AddDays(-30);
            for (var page = 1; page <= 20; page++)
            {
                var (items, hasNext) = await api.GetClientsPageAsync(page, null, ct).ConfigureAwait(false);
                foreach (var c in items)
                {
                    total++;
                    if (DateTime.TryParse(Str(c, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at) && at >= since)
                        recent++;
                }
                if (!hasNext)
                    break;
            }
            sb.AppendLine($"• Клиенты (сервер NurCRM): всего {total}, новых за 30 дней {recent}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: клиенты для анализа не получены ({ex.Message}).", "WARNING");
        }
        try
        {
            var stats = await App.GetRequiredService<NurMarketKassa.Services.Api.ServerTelegramBotApi>()
                .GetStatsAsync(DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1), ct).ConfigureAwait(false);
            sb.AppendLine($"• Заказы через Telegram-бота за 30 дней: обращений {stats.Messages}, покупателей {stats.People}, " +
                          $"заказов {stats.Orders} на {stats.OrdersTotal.ToString("N0", Ru)} сом. Заказы с витрины сайта приходят в WhatsApp (на сервере не хранятся).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: статистика бота не получена ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-10-05, владелец: «дай список клиентов-должников». Имена и телефоны клиентов в Google не уходят:
    /// нейросеть видит должников под кодами [Д1], [Д2]… (сумма, число чеков, с какой даты), а настоящие имя и
    /// телефон подставляет в её ответ сама программа (<see cref="RevealDebtors"/>) — на этом компьютере.</summary>
    /// <summary>2026-10-06, владелец: «дай ИИ доступ, чтобы по номеру находил должников и писал им вернуть долг». Должники последней
    /// сводки по кодам [Д1]… — имя, телефон, сумма (только на этом компьютере, для кнопок «Написать в WhatsApp»).</summary>
    public static IReadOnlyDictionary<int, (string Name, string? Phone, double Amount)> LastDebtors { get; private set; }
        = new Dictionary<int, (string Name, string? Phone, double Amount)>();

    public static async Task<(string AiText, IReadOnlyDictionary<int, string> Names)> BuildDebtorsPseudonymousAsync(CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        var debtors = new Dictionary<int, (string Name, string? Phone, double Amount)>();
        try
        {
            var debts = await App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>()
                .PosDebtSalesAsync(null, ct).ConfigureAwait(false);
            var byClient = new Dictionary<string, (string Name, double Amount, int Count, DateTime? Oldest)>(StringComparer.OrdinalIgnoreCase);
            foreach (var debt in debts)
            {
                var clientId = Str(debt, "client");
                var amount = Num(debt, "debt_amount") ?? Num(debt, "total") ?? 0;
                if (string.IsNullOrWhiteSpace(clientId) || amount <= 0.005)
                    continue;
                DateTime? at = DateTime.TryParse(Str(debt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
                var name = Str(debt, "client_name") ?? "Без имени";
                byClient[clientId] = byClient.TryGetValue(clientId, out var e)
                    ? (e.Name, e.Amount + amount, e.Count + 1, e.Oldest is { } o && (at is null || o < at) ? o : at ?? e.Oldest)
                    : (name, amount, 1, at);
            }
            if (byClient.Count == 0)
                return ("ДОЛЖНИКИ: непогашенных долгов нет.", names);

            // Телефоны — из карточек клиентов (только для показа владельцу на этом компьютере).
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var api = App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>();
                for (var page = 1; page <= 20 && byClient.Keys.Any(k => !phones.ContainsKey(k)); page++)
                {
                    var (items, hasNext) = await api.GetClientsPageAsync(page, null, ct).ConfigureAwait(false);
                    foreach (var c in items)
                    {
                        if (Str(c, "id") is { Length: > 0 } id && Str(c, "phone") is { Length: > 0 } phone)
                            phones[id] = phone.Trim();
                    }
                    if (!hasNext)
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: телефоны должников не получены ({ex.Message}).", "WARNING");
            }

            var sb = new StringBuilder();
            sb.AppendLine("ДОЛЖНИКИ (сервер NurCRM; имена скрыты кодами, программа сама подставит имя и телефон вместо кода):");
            var n = 0;
            foreach (var (id, info) in byClient.OrderByDescending(x => x.Value.Amount).Take(60))
            {
                n++;
                names[n] = info.Name + (phones.TryGetValue(id, out var phone) ? $" (тел. {phone})" : "");
                debtors[n] = (info.Name, phones.TryGetValue(id, out var ph) ? ph : null, info.Amount);
                sb.AppendLine($"• [Д{n}] — {info.Amount.ToString("N2", Ru)} сом, чеков в долг: {info.Count}" +
                              (info.Oldest is { } o ? $", долг с {o.ToLocalTime():dd.MM.yyyy}" : ""));
            }
            LastDebtors = debtors;
            return (sb.ToString().TrimEnd(), names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: должники не получены ({ex.Message}).", "WARNING");
            return ("ДОЛЖНИКИ: сервер сейчас не ответил.", names);
        }
    }

    /// <summary>Коды [Д1]… в ответе нейросети → настоящие имя и телефон (только на экране владельца).</summary>
    public static string RevealDebtors(string answer, IReadOnlyDictionary<int, string> names) =>
        names.Count == 0
            ? answer
            : System.Text.RegularExpressions.Regex.Replace(answer, @"\[?Д(\d{1,3})\]?",
                m => int.TryParse(m.Groups[1].Value, out var i) && names.TryGetValue(i, out var name) ? name : m.Value);

    private static string? Str(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => v.GetString(),
                System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double? Num(System.Text.Json.JsonElement e, string name)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        return v.ValueKind == System.Text.Json.JsonValueKind.String
               && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const int MaxProducts = 600;

    /// <summary>2026-10-05, владелец: «добавь боту и ИИ отправлять фото товара, если есть». Товары каталога,
    /// названные в ответе советника (по полному названию, без учёта регистра), у которых есть фото, — в порядке
    /// упоминания, не больше <paramref name="max"/>.</summary>
    public static IReadOnlyList<CatalogProductTileVm> FindMentionedProducts(string text, int max = 4)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<CatalogProductTileVm>();
        try
        {
            IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
            if (tiles.Count == 0)
                tiles = CatalogCacheService.Products.ToList();
            return tiles
                .Where(t => t.Title.Trim().Length >= 3
                            && (!string.IsNullOrWhiteSpace(t.ImageUrl) || !string.IsNullOrWhiteSpace(t.ProductImagePath)))
                .Select(t => (Tile: t, At: text.IndexOf(t.Title.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.At >= 0)
                .OrderBy(x => x.At)
                .ThenByDescending(x => x.Tile.Title.Length)
                .Select(x => x.Tile)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .Take(max)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: товары для фото не найдены ({ex.Message}).", "WARNING");
            return Array.Empty<CatalogProductTileVm>();
        }
    }

    public static string BuildWarehouse()
    {
        try
        {
            IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
            if (tiles.Count == 0)
                tiles = CatalogCacheService.Products.ToList();
            if (tiles.Count == 0)
                return "СКЛАД: каталог в программе ещё не загружен — подскажи раздел «Склад».";

            static double Price(CatalogProductTileVm t) => LocalCartService.ParsePrice(t.PriceLine);
            var stockSale = tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * Price(t));
            var stockCost = tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * t.PurchasePrice);
            var outOfStock = tiles.Count(t => t.Quantity <= 0);
            var sb = new StringBuilder();
            sb.AppendLine($"СКЛАД (каталог программы): товаров {tiles.Count}, с нулевым остатком {outOfStock}; " +
                          $"остаток по цене продажи {stockSale.ToString("N2", Ru)} сом, по закупке {stockCost.ToString("N2", Ru)} сом.");
            sb.AppendLine("Товары — название | категория | цена продажи | закупка | остаток:");
            foreach (var t in tiles.OrderBy(t => t.Category ?? "").ThenBy(t => t.Title).Take(MaxProducts))
            {
                var unit = string.IsNullOrWhiteSpace(t.Unit) ? (t.MustWeigh ? "кг" : "шт.") : t.Unit;
                var cost = t.PurchasePrice > 0 ? t.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture) : "—";
                sb.AppendLine($"• {t.Title} | {t.Category ?? "—"} | {Price(t):0.##} | {cost} | {t.Quantity:0.###} {unit}");
            }
            if (tiles.Count > MaxProducts)
                sb.AppendLine($"… и ещё {tiles.Count - MaxProducts} товаров (полный список — раздел «Склад»).");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: склад не собран ({ex.Message}).", "WARNING");
            return "СКЛАД: данные склада сейчас недоступны.";
        }
    }

    /// <summary>Отчёт по товарам без локального фото. Строится целиком из локального каталога,
    /// чтобы запрос на список не зависел от сокращённого контекста, отправляемого нейросети.</summary>
    public static string BuildMissingPhotoReport()
    {
        try
        {
            IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
            if (tiles.Count == 0)
                tiles = CatalogCacheService.Products.ToList();
            if (tiles.Count == 0)
                return "Каталог товаров в программе ещё не загружен. Откройте раздел «Склад» и повторите запрос.";

            var products = tiles.Where(t => !t.IsService).ToList();
            var missing = products.Where(t => string.IsNullOrWhiteSpace(t.ImageUrl)
                                               && string.IsNullOrWhiteSpace(t.ProductImagePath))
                .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"АНАЛИЗ ФОТОГРАФИЙ СКЛАДА: товаров {products.Count}, без фото {missing.Count} " +
                          $"({(products.Count == 0 ? 0 : missing.Count * 100.0 / products.Count):0.#} %).");
            if (missing.Count == 0)
            {
                sb.AppendLine("У всех товаров есть фото.");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine();
            sb.AppendLine("| № | Наименование | Штрихкод / Артикул | Ед. изм. | Остаток |");
            sb.AppendLine("|---:|---|---|---|---:|");
            for (var i = 0; i < missing.Count; i++)
            {
                var t = missing[i];
                static string Cell(string? value) => string.IsNullOrWhiteSpace(value)
                    ? "-" : value.Trim().Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
                var code = !string.IsNullOrWhiteSpace(t.Barcode) ? t.Barcode : t.Article;
                var unit = string.IsNullOrWhiteSpace(t.Unit) ? (t.MustWeigh ? "кг" : "шт.") : t.Unit;
                sb.AppendLine($"| {i + 1} | {Cell(t.Title)} | {Cell(code)} | {Cell(unit)} | {t.Quantity:0.###} |");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: список товаров без фото не построен ({ex.Message}).", "WARNING");
            return "Не удалось прочитать локальный каталог товаров. Повторите запрос после загрузки склада.";
        }
    }
}
