using System.Globalization;
using System.Text;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;


namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец: «дай доступ ко всему для ИИ — к долгам, к контактам, клиентам, к складу, к товарам,
/// поставщикам». Данные магазина для «Нур Советника».
/// 2026-10-21, «доведи Нур Советника до 10»: данные — частями по важности (AiSection, О1) с источником для подписи «откуда цифра»;
/// долги — точный общий расчёт, как «Сводка» и «Долги клиентов» (OwnerShopFacts, О2); имена и телефоны должников в ИИ не уходят
/// (коды [Д1], подстановка на этом ПК), сотрудники — коды [С1] (AiPseudonyms); без права «Клиенты» список должников не строится (О4).</summary>
public static class OwnerAiContext
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Итоги долгов клиентов (точно, как «Сводка») и поставщиков. Приоритет 0 — всегда в ИИ.</summary>
    public static async Task<AiSection> DebtTotalsSectionAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        string label = "Долги клиентов";
        try
        {
            var facts = await OwnerShopFacts.GetDebtsAsync(ct).ConfigureAwait(false);
            if (facts.Debtors.Count == 0)
                sb.AppendLine("ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ: непогашенных долгов нет.");
            else
            {
                var oldest = facts.Debtors.Min(d => d.Oldest);
                sb.AppendLine((facts.Exact
                                  ? $"ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (точный расчёт, как разделы «Долги клиентов» и «Сводка», на {facts.AtLocal:dd.MM HH:mm}): вам должны {Money2(facts.Total)}"
                                  : $"ДОЛГИ КЛИЕНТОВ ПЕРЕД МАГАЗИНОМ (ПРИБЛИЗИТЕЛЬНО: точный расчёт ещё не готов, оплаты по сделкам не вычтены — называй сумму со словом «примерно» и предложи раздел «Долги клиентов»): ≈ {Money2(facts.Total)}")
                              + $" — {facts.Debtors.Count} клиент(ов), {facts.Checks} чек(ов) в долг; просрочка больше 30 дней — {Money2(facts.Overdue30)} у {facts.OverdueClients} клиент(ов); "
                              + $"крупнейшие долги: {string.Join(", ", facts.Debtors.Take(5).Select(d => Money2(d.Amount)))}; самый старый долг с {oldest:dd.MM.yyyy}.");
                label = facts.Exact ? $"Долги клиентов (точно, на {facts.AtLocal:dd.MM HH:mm})" : "Долги клиентов (≈, точный расчёт не готов)";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: долги клиентов не получены ({ex.Message}).", "WARNING");
            sb.AppendLine("ДОЛГИ КЛИЕНТОВ: сервер сейчас не ответил — цифру не называй, подскажи раздел «Долги клиентов».");
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
                    ? $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; сумм долга перед поставщиками сервер не отдаёт — подскажи раздел «Поставщики»."
                    : $"ПОСТАВЩИКИ (сервер NurCRM): {rows.Count}; по данным сервера сумма по поставщикам {owed.Sum().ToString("N2", Ru)} сом " +
                      $"(у {owed.Count(v => Math.Abs(v) > 0.005)} есть ненулевая сумма).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: поставщики не получены ({ex.Message}).", "WARNING");
        }
        return new AiSection("debts", "Долги клиентов и поставщиков", 0, sb.ToString().Trim(), SourceSection: "debts", SourceLabel: label);
    }

    private static string Money2(double v) => v.ToString("N0", Ru) + " сом";

    private static readonly SemaphoreSlim AbcGate = new(1, 1);
    private static AiSection? _abcCached;
    private static DateTime _abcCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «дай доступ к ABC-анализу ИИ». Тот же расчёт, что раздел «ABC-анализ» (30 дней, все срезы).
    /// 2026-10-21 (О1): полный вариант (A — до 40, B и C — до 25 в каждом срезе) и короткий — итоги групп и top-10 группы A по
    /// выручке, прибыли и количеству. Пересчёт не чаще раза в 10 минут.</summary>
    public static async Task<AiSection> AbcSectionAsync(CancellationToken ct)
    {
        await AbcGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_abcCached is not null && DateTime.UtcNow - _abcCachedAtUtc < TimeSpan.FromMinutes(10))
                return _abcCached;
            var to = DateTime.Today;
            var from = to.AddDays(-29);
            var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, includeSeasonality: false, ct: ct).ConfigureAwait(false);
            var head = $"ABC-АНАЛИЗ за {from:dd.MM}–{to:dd.MM.yyyy} (как раздел «ABC-анализ»; A — первые 80 % меры, B — следующие 15 %, C — остальные 5 %):";
            var full = new StringBuilder(head).AppendLine();
            var brief = new StringBuilder(head).AppendLine();
            if (data.AbcSlices.Count == 0)
            {
                full.AppendLine("продаж за период нет — ABC не построен.");
                brief.AppendLine("продаж за период нет — ABC не построен.");
            }
            foreach (var slice in data.AbcSlices)
            {
                string Fmt(double v) => slice.IsMoney ? $"{v.ToString("N0", Ru)} сом" : $"{v.ToString("0.###", CultureInfo.InvariantCulture)} {slice.Unit}";
                var summary = $"Срез «{slice.Title}»: " + string.Join("; ", slice.Summary.Select(g => $"группа {g.Group} — {g.Count} поз., {Fmt(g.Sum)} ({g.Share:0.#} %)"));
                full.AppendLine(summary);
                brief.AppendLine(summary);
                foreach (var group in new[] { "A", "B", "C" })
                {
                    var rows = slice.Rows.Where(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (rows.Count == 0)
                        continue;
                    var take = group == "A" ? 40 : 25;
                    full.AppendLine($"  {group}: " + string.Join("; ", rows.Take(take).Select(r => $"{r.Name} — {Fmt(r.Sum)} ({r.Share:0.#} %)"))
                                    + (rows.Count > take ? $"; … и ещё {rows.Count - take}" : ""));
                    if (group == "A" && slice.Key is AnalyticsReportData.KeyRevenue or AnalyticsReportData.KeyProfit or AnalyticsReportData.KeyQuantity)
                        brief.AppendLine($"  A (top-10 из {rows.Count}): " + string.Join("; ", rows.Take(10).Select(r => $"{r.Name} — {Fmt(r.Sum)}")));
                }
            }
            brief.Append("(короткий вариант: по остальным срезам и группам B/C — НУЖНО: abc)");
            _abcCached = new AiSection("abc", "ABC-анализ (все срезы и группы)", 4, full.ToString().TrimEnd(), brief.ToString().TrimEnd(),
                SourceSection: "abc", SourceLabel: $"ABC-анализ, {from:dd.MM}–{to:dd.MM}");
            _abcCachedAtUtc = DateTime.UtcNow;
            return _abcCached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: ABC-анализ не построен ({ex.Message}).", "WARNING");
            return new AiSection("abc", "ABC-анализ", 4, "ABC-АНАЛИЗ: сейчас не построился — подскажи раздел «ABC-анализ».");
        }
        finally
        {
            AbcGate.Release();
        }
    }

    private static readonly SemaphoreSlim AnalysisGate = new(1, 1);
    private static List<AiSection>? _analysisCached;
    private static DateTime _analysisCachedAtUtc = DateTime.MinValue;

    /// <summary>2026-10-05, владелец: «в ИИ добавь анализ продаж, склада, клиентов, заказов — чтобы предлагать акции на проблемные
    /// товары». 2026-10-21 (О1): две части — «что заканчивается» (важнее, приоритет 5) и остальной анализ (мёртвый остаток,
    /// затоваривание, падение, низкая наценка, рост, клиенты, заказы бота; приоритет 8). Пересчёт не чаще раза в 10 минут.</summary>
    public static async Task<List<AiSection>> AnalysisSectionsAsync(CancellationToken ct)
    {
        await AnalysisGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_analysisCached is not null && DateTime.UtcNow - _analysisCachedAtUtc < TimeSpan.FromMinutes(10))
                return _analysisCached;
            var list = new List<AiSection>();
            try
            {
                var rows = await OwnerShopFacts.StockRowsAsync(ct).ConfigureAwait(false);
                string Money(double v) => v.ToString("N0", Ru) + " сом";
                var soldOutAll = OwnerShopFacts.SoldOut(rows, 1000);
                var lowAll = OwnerShopFacts.RunningLow(rows, 1000);
                string Low(int take) =>
                    "ЧТО ЗАКАНЧИВАЕТСЯ (продажи 30 дней как «ABC-анализ», остаток — склад программы; «заказать» — на 14 дней продаж):\n"
                    + $"• Хорошо продавались, но закончились (остаток 0) — {soldOutAll.Count} поз.: "
                    + (soldOutAll.Count == 0 ? "нет." : string.Join("; ", soldOutAll.Take(take).Select(r =>
                        $"{r.Name} — продано {r.Sold30:0.###} за 30 дн. (≈{r.PerDay:0.#} в день), теряется ≈{Money(r.PerDay * r.Price)} выручки в день, заказать ≈{r.OrderFor14:0}"))
                        + (soldOutAll.Count > take ? $"; показаны {take} из {soldOutAll.Count}" : ""))
                    + $"\n• Популярные, но осталось мало (запаса меньше чем на 7 дней) — {lowAll.Count} поз.: "
                    + (lowAll.Count == 0 ? "нет." : string.Join("; ", lowAll.Take(take).Select(r =>
                        $"{r.Name} — остаток {r.Stock:0.###}, хватит ≈{r.DaysLeft:0.#} дн., продаётся ≈{r.PerDay:0.#} в день, заказать ≈{r.OrderFor14:0}"))
                        + (lowAll.Count > take ? $"; показаны {take} из {lowAll.Count}" : ""));
                list.Add(new AiSection("low_stock", "Что заканчивается", 5, Low(40), Low(12) + "\n(полный список — НУЖНО: low_stock)",
                    SourceSection: "restock", SourceLabel: "Пополнение: продажи 30 дней и остатки склада"));

                var sb = new StringBuilder("АНАЛИЗ ТОВАРОВ И АКЦИЙ (30 дней; для акций на проблемные товары):\n");
                var deadAll = rows.Where(r => r.Stock > 0 && r.Sold30 <= 0).ToList();
                var dead = OwnerShopFacts.Dead(rows, 12);
                sb.AppendLine($"• Не продавались 30 дней, но лежат на складе — {deadAll.Count} поз., заморожено {Money(deadAll.Sum(r => r.Frozen))} (по закупке): " +
                              (dead.Count == 0 ? "нет." : string.Join("; ", dead.Select(r => $"{r.Name} — остаток {r.Stock:0.###}, {Money(r.Frozen)}")) + (deadAll.Count > dead.Count ? $"; показаны {dead.Count} из {deadAll.Count}" : "")));
                var overstock = rows.Where(r => r.PerDay > 0 && r.Stock / r.PerDay > 60).OrderByDescending(r => r.Stock / r.PerDay).Take(10).ToList();
                sb.AppendLine("• Затоварено (запаса больше чем на 60 дней продаж): " +
                              (overstock.Count == 0 ? "нет." : string.Join("; ", overstock.Select(r => $"{r.Name} — остаток {r.Stock:0.###}, продаётся {r.PerDay:0.##} в день, хватит на {r.Stock / r.PerDay:0} дн."))));
                var falling = rows.Where(r => r.Before >= 3 && r.Recent < r.Before * 0.6).OrderBy(r => r.Recent / Math.Max(1, r.Before)).Take(8).ToList();
                sb.AppendLine("• Продажи упали (последние 15 дней против предыдущих 15): " +
                              (falling.Count == 0 ? "нет." : string.Join("; ", falling.Select(r => $"{r.Name} — было {r.Before:0.###}, стало {r.Recent:0.###}"))));
                var lowMargin = rows.Where(r => r.Markup is { } m && m < 10).OrderBy(r => r.Markup).Take(10).ToList();
                sb.AppendLine("• Низкая наценка (меньше 10 %) или цена ниже закупки — скидку на них давать нельзя: " +
                              (lowMargin.Count == 0 ? "нет." : string.Join("; ", lowMargin.Select(r => $"{r.Name} — цена {r.Price:0.##}, закупка {r.Cost:0.##}, наценка {r.Markup:0.#} %"))));
                var growing = rows.Where(r => r.Recent >= 3 && r.Recent > r.Before * 1.3).OrderByDescending(r => r.Recent - r.Before).Take(6).ToList();
                sb.AppendLine("• Растут (хорошие «паровозы» для комплекта со слабым товаром): " +
                              (growing.Count == 0 ? "нет." : string.Join("; ", growing.Select(r => $"{r.Name} — было {r.Before:0.###}, стало {r.Recent:0.###}"))));
                sb.AppendLine("Правила акций: скидка не ниже закупки + 5 %; на мёртвый остаток — скидка 15–30 % или «2 по цене 1,5»; " +
                              "затоваренное — в комплект с растущим товаром; падающее — выкладка/напоминание в боте; товары с низкой наценкой — без скидки.");
                await AppendClientsAndOrdersAsync(sb, ct).ConfigureAwait(false);
                list.Add(new AiSection("analysis", "Анализ товаров и акций", 8, sb.ToString().TrimEnd(), SourceSection: "analytics", SourceLabel: "Анализ товаров за 30 дней"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"ИИ-советник: анализ товаров не построен ({ex.Message}).", "WARNING");
                list.Add(new AiSection("analysis", "Анализ товаров", 8, "АНАЛИЗ ТОВАРОВ: сейчас не построился — подскажи раздел «Пополнение и сроки»."));
            }
            _analysisCached = list;
            _analysisCachedAtUtc = DateTime.UtcNow;
            return list;
        }
        finally
        {
            AnalysisGate.Release();
        }
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

    /// <summary>2026-10-06, владелец: «дай ИИ доступ, чтобы по номеру находил должников и писал им вернуть долг». Должники последней
    /// сводки по кодам [Д1]… — имя, телефон, сумма (только на этом компьютере, для кнопок «Написать в WhatsApp»).</summary>
    public static IReadOnlyDictionary<int, (string Name, string? Phone, double Amount)> LastDebtors { get; private set; }
        = new Dictionary<int, (string Name, string? Phone, double Amount)>();

    /// <summary>2026-10-05, владелец: «дай список клиентов-должников». Имена и телефоны клиентов в Google не уходят: нейросеть видит
    /// должников под кодами [Д1], [Д2]… (сумма, число чеков, с какой даты), а настоящие имя и телефон подставляет программа
    /// (<see cref="RevealCodes"/>) — на этом компьютере. 2026-10-21: суммы — точные (OwnerShopFacts, как «Долги клиентов», О2);
    /// полный вариант — все должники, короткий — top-10 (О1).</summary>
    public static async Task<(AiSection? Section, IReadOnlyDictionary<int, string> Names)> DebtorsSectionAsync(CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        var debtors = new Dictionary<int, (string Name, string? Phone, double Amount)>();
        try
        {
            var facts = await OwnerShopFacts.GetDebtsAsync(ct).ConfigureAwait(false);
            if (facts.Debtors.Count == 0)
                return (new AiSection("debtors", "Должники", 7, "ДОЛЖНИКИ: непогашенных долгов нет."), names);

            // Телефоны — из карточек клиентов (только для показа владельцу на этом компьютере).
            var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in facts.Debtors.Where(d => d.Phone.Length > 0))
                phones[d.ClientId] = d.Phone.Trim();
            try
            {
                var api = App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>();
                for (var page = 1; page <= 20 && facts.Debtors.Any(d => d.ClientId.Length > 0 && !phones.ContainsKey(d.ClientId)); page++)
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

            var full = new StringBuilder();
            var brief = new StringBuilder();
            var head = $"ДОЛЖНИКИ ({(facts.Exact ? "точно, как «Долги клиентов»" : "≈ приблизительно")}; имена скрыты кодами — пиши код в квадратных скобках, программа сама покажет имя и телефон):";
            full.AppendLine(head);
            brief.AppendLine(head);
            var n = 0;
            foreach (var d in facts.Debtors.Take(60))
            {
                n++;
                var phone = phones.TryGetValue(d.ClientId, out var ph) ? ph : null;
                names[n] = d.Name + (phone is { Length: > 0 } ? $" (тел. {phone})" : "");
                debtors[n] = (d.Name, phone, d.Amount);
                var line = $"• [Д{n}] — {Money2(d.Amount)}, чеков в долг: {d.Checks}, долг с {d.Oldest:dd.MM.yyyy}" + (d.Overdue30 > 0.005 ? $", просрочено > 30 дн.: {Money2(d.Overdue30)}" : "");
                full.AppendLine(line);
                if (n <= 10)
                    brief.AppendLine(line);
            }
            if (facts.Debtors.Count > 60)
                full.AppendLine($"(показаны 60 из {facts.Debtors.Count} — остальные в разделе «Долги клиентов»)");
            if (n > 10)
                brief.AppendLine($"(показаны 10 из {facts.Debtors.Count} — весь список: НУЖНО: debtors)");
            LastDebtors = debtors;
            return (new AiSection("debtors", "Должники (коды)", 7, full.ToString().TrimEnd(), brief.ToString().TrimEnd(), SourceSection: "debts",
                SourceLabel: facts.Exact ? "Долги клиентов (точно)" : "Долги клиентов (≈)", Personal: true), names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"ИИ-советник: должники не получены ({ex.Message}).", "WARNING");
            return (new AiSection("debtors", "Должники", 7, "ДОЛЖНИКИ: сервер сейчас не ответил."), names);
        }
    }

    /// <summary>Коды [Д1]… в ответе нейросети → настоящие имя и телефон (только на экране владельца); [С1]… — имена сотрудников.
    /// 2026-10-21 (О9): только СО СКОБКАМИ — «витамин Д3» или товар «Д10» больше не превращаются в должника.</summary>
    public static string RevealCodes(string answer, IReadOnlyDictionary<int, string> names) =>
        AiPseudonyms.RevealStaff(names.Count == 0
            ? answer
            : System.Text.RegularExpressions.Regex.Replace(answer, @"\[Д(\d{1,3})\]",
                m => int.TryParse(m.Groups[1].Value, out var i) && names.TryGetValue(i, out var name) ? name : m.Value));

    /// <summary>Склад итогом (всегда в ИИ, О1): сколько товаров, в наличии, остаток по закупке и по цене продажи.</summary>
    public static AiSection WarehouseTotalsSection()
    {
        try
        {
            var tiles = OwnerShopFacts.Tiles().Where(t => !t.IsService).ToList();
            if (tiles.Count == 0)
                return new AiSection("warehouse_totals", "Склад итогом", 0, "СКЛАД: каталог в программе ещё не загружен — подскажи раздел «Склад».");
            var inStock = tiles.Where(t => t.Quantity > 0).ToList();
            var sale = inStock.Sum(t => t.Quantity * LocalCartService.ParsePrice(t.PriceLine));
            var cost = inStock.Sum(t => t.Quantity * t.PurchasePrice);
            var noCost = inStock.Count(t => t.PurchasePrice <= 0);
            return new AiSection("warehouse_totals", "Склад итогом", 0,
                $"СКЛАД ИТОГОМ (каталог программы, как раздел «Склад»): товаров {tiles.Count}, в наличии {inStock.Count}, с нулевым или минусовым остатком {tiles.Count - inStock.Count}; " +
                // 2026-10-30: тумблер приватности «Закупочные цены и прибыль» — стоимость по закупке в ИИ не уходит.
                (AdvisorCapabilities.HidePurchase ? $"остаток по цене продажи {Money2(sale)}"
                    : $"остаток по закупке {Money2(cost)}, по цене продажи {Money2(sale)}" + (noCost > 0 ? $" (у {noCost} товаров в наличии закупка не указана)" : "")) + ".",
                SourceSection: "warehouse", SourceLabel: "Склад (каталог программы)");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: итоги склада не собраны ({ex.Message}).", "WARNING");
            return new AiSection("warehouse_totals", "Склад итогом", 0, "СКЛАД: данные склада сейчас недоступны.");
        }
    }

    /// <summary>Весь склад построчно (О1): полный — все товары, короткий — 40 самых дорогих остатков; приоритет 10 (поднимается, если
    /// спрашивают список склада).</summary>
    public static AiSection WarehouseListSection()
    {
        try
        {
            var tiles = OwnerShopFacts.Tiles().Where(t => !t.IsService).ToList();
            string Line(CatalogProductTileVm t)
            {
                var unit = string.IsNullOrWhiteSpace(t.Unit) ? (t.MustWeigh ? "кг" : "шт.") : t.Unit;
                var cost = t.PurchasePrice > 0 ? t.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture) : "—";
                // 2026-10-30: тумблер приватности — колонка «закупка» убирается.
                return $"• {t.Title} | {t.Category ?? "—"} | {LocalCartService.ParsePrice(t.PriceLine):0.##} | {(AdvisorCapabilities.HidePurchase ? "" : cost + " | ")}{t.Quantity:0.###} {unit}" + (string.IsNullOrWhiteSpace(t.Barcode) ? "" : $" | {t.Barcode}");
            }
            var head = AdvisorCapabilities.HidePurchase ? "СКЛАД ПОСТРОЧНО — название | категория | цена продажи | остаток | штрихкод:"
                : "СКЛАД ПОСТРОЧНО — название | категория | цена продажи | закупка | остаток | штрихкод:";
            var full = head + "\n" + string.Join("\n", tiles.OrderBy(t => t.Category ?? "").ThenBy(t => t.Title).Select(Line));
            var top = tiles.OrderByDescending(t => Math.Max(0, t.Quantity) * Math.Max(t.PurchasePrice, 0)).Take(40).ToList();
            var brief = head + $" (показаны 40 из {tiles.Count} — самые дорогие остатки; весь склад: НУЖНО: warehouse)\n" + string.Join("\n", top.Select(Line));
            return new AiSection("warehouse", "Склад построчно (все товары)", 10, full, brief, SourceSection: "warehouse", SourceLabel: "Склад (каталог программы)");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: склад не собран ({ex.Message}).", "WARNING");
            return new AiSection("warehouse", "Склад построчно", 10, "СКЛАД: данные склада сейчас недоступны.");
        }
    }

    /// <summary>Товары, названные в вопросе (по словам названия или штрихкоду), — с ценой, закупкой, остатком и продажами 30 дней (О1, приоритет 1).</summary>
    public static async Task<AiSection?> QuestionProductsSectionAsync(string question, CancellationToken ct)
    {
        try
        {
            var found = ProductActionPlan.Mentioned(question, 8);
            var digits = System.Text.RegularExpressions.Regex.Matches(question ?? "", @"\d{6,14}").Select(m => m.Value).ToHashSet();
            if (digits.Count > 0)
                found = found.Concat(OwnerShopFacts.Tiles().Where(t => !string.IsNullOrWhiteSpace(t.Barcode) && digits.Contains(t.Barcode.Trim()))).DistinctBy(t => t.Id).ToList();
            if (found.Count == 0)
                return null;
            // Продажи этих товаров за период вопроса (не назван — 30 дней) — тот же расчёт, что «ABC-анализ». Только если о продажах
            // спрашивают (иначе — лишний отчёт сервера на каждый вопрос с названием товара, О3).
            var (pFrom, pToEx, pLabel) = OwnerSalesData.Period(question ?? "");
            var askSales = new[] { "продал", "продаж", "продан", "сатыл", "саттык", "сатуу", "sold", "sales", "satıl", "sotil" }.Any((question ?? "").ToLowerInvariant().Contains);
            if (pToEx - pFrom <= TimeSpan.FromDays(1) && !System.Text.RegularExpressions.Regex.IsMatch((question ?? "").ToLowerInvariant(), @"сегодн|вчера|позавчера|кечээ|бүгүн|today|yesterday|\d{1,2}[./]\d{1,2}"))
                (pFrom, pToEx, pLabel) = (DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1), "30 дней");
            Dictionary<string, (double Qty, double Sum)> sold = new(StringComparer.OrdinalIgnoreCase);
            if (!askSales)
                pLabel = "";
            else try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var report = await AnalyticsReportData.BuildAsync(App.SalesApi, pFrom, pToEx.AddDays(-1), includeSeasonality: false, ct: cts.Token).ConfigureAwait(false);
                foreach (var r in report.Slice(AnalyticsReportData.KeyRevenue)?.Rows ?? report.Abc)
                    sold[r.Name.Trim()] = sold.TryGetValue(r.Name.Trim(), out var e) ? (e.Qty + r.Quantity, e.Sum + r.Sum) : (r.Quantity, r.Sum);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                PosLogger.Log($"ИИ-советник: продажи товаров из вопроса не получены ({ex.GetType().Name}).", "WARNING");
                pLabel = "";
            }
            var sb = new StringBuilder("ТОВАРЫ ИЗ ВОПРОСА (найдены программой в каталоге по названию/штрихкоду — других похожих товаров на складе нет; если нужного нет в списке, скажи, что такого товара нет):\n");
            foreach (var t in found)
            {
                var price = LocalCartService.ParsePrice(t.PriceLine);
                var markup = t.PurchasePrice > 0 ? $", наценка {(price - t.PurchasePrice) / t.PurchasePrice * 100:0.#} %" : "";
                // 2026-10-30: тумблер приватности — закупка и наценка в ИИ не уходят.
                var costText = AdvisorCapabilities.HidePurchase ? "" : $", закупка {(t.PurchasePrice > 0 ? t.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture) + " сом" : "не указана")}{markup}";
                sb.AppendLine($"• {t.Title} — цена {price:0.##} сом{costText}, "
                              + $"остаток {t.Quantity:0.###} {(string.IsNullOrWhiteSpace(t.Unit) ? "шт." : t.Unit)}" + (string.IsNullOrWhiteSpace(t.Barcode) ? "" : $", штрихкод {t.Barcode}")
                              + (string.IsNullOrWhiteSpace(t.Category) ? "" : $", категория {t.Category}")
                              + (pLabel.Length == 0 ? "" : sold.TryGetValue(t.Title.Trim(), out var st) ? $"; продано за {pLabel} ({pFrom:dd.MM}–{pToEx.AddDays(-1):dd.MM}): {st.Qty:0.###} шт. на {st.Sum:N0} сом" : $"; за {pLabel} продаж не было"));
            }
            return new AiSection("question_products", "Товары из вопроса", 1, sb.ToString().TrimEnd(), SourceSection: "warehouse", SourceLabel: "Склад (каталог программы)");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: товары из вопроса не найдены ({ex.Message}).", "WARNING");
            return null;
        }
    }

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
