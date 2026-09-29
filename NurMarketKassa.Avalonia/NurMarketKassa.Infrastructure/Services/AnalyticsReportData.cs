using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Данные аналитики за период — один расчёт на обе выгрузки (Excel и Word), чтобы
/// цифры в них не разошлись.</summary>
public sealed class AnalyticsReportData
{
    public DateTime FromLocal { get; init; }
    public DateTime ToLocal { get; init; }

    public double Revenue { get; init; }
    public double Discounts { get; init; }
    public double PointsRedeemed { get; init; }
    public double Returns { get; init; }
    public double WriteOffs { get; init; }
    public double Expenses { get; init; }
    public double DebtPayments { get; init; }
    public int ReceiptCount { get; init; }

    public double AverageReceipt => ReceiptCount > 0 ? Revenue / ReceiptCount : 0;

    /// <summary>Выручка по дням — для столбчатой диаграммы «динамика продаж».</summary>
    public IReadOnlyList<(DateTime Day, double Revenue)> ByDay { get; init; } = [];

    /// <summary>Топ товаров по сумме продаж.</summary>
    public IReadOnlyList<(string Name, double Quantity, double Sum)> TopProducts { get; init; } = [];

    /// <summary>ABC-анализ товаров по выручке. Группа A — товары, дающие первые 80 % выручки,
    /// B — следующие 15 %, C — оставшиеся 5 %. Это классическое правило Парето: обычно небольшая
    /// часть ассортимента приносит основную выручку, и именно её нельзя допускать до нуля на
    /// складе, тогда как по группе C можно спокойно сокращать запас.</summary>
    public IReadOnlyList<AbcRow> Abc { get; init; } = [];

    public sealed record AbcRow(
        string Name, double Quantity, double Sum, double Share, double Cumulative, string Group);

    /// <summary>Один срез ABC: по чему группировали и что считали мерой.
    ///
    /// Срезов несколько не для красоты: товар может быть в группе A по выручке и в группе C по
    /// прибыли (дорогой товар с нулевой наценкой), и наоборот — дешёвый товар с огромным
    /// оборотом держит кассу, хотя в денежном топе его не видно. Решения по закупке принимают
    /// по разным срезам, поэтому они лежат рядом, а не подменяют друг друга.</summary>
    /// <param name="Key">Устойчивый код среза («revenue», «profit», …). Название переводится
    /// на язык интерфейса, поэтому искать срез по названию нельзя: на кыргызском поиск по
    /// слову «прибыли» ничего не найдёт.</param>
    /// <param name="IsMoney">Мера в деньгах или в штуках. Тоже отдельным полем, а не сравнением
    /// Unit со строкой «шт.» — по той же причине.</param>
    public sealed record AbcSlice(
        string Key,
        string Title,
        string Unit,
        bool IsMoney,
        string Hint,
        IReadOnlyList<AbcRow> Rows,
        IReadOnlyList<(string Group, int Count, double Sum, double Share)> Summary);

    /// <summary>Все срезы ABC в порядке показа.</summary>
    public IReadOnlyList<AbcSlice> AbcSlices { get; init; } = [];

    /// <summary>Коды срезов (<see cref="AbcSlice.Key"/>). Первые пять считаются по продажам за
    /// период; «stock» — по складу на сейчас и от периода не зависит (2026-09-27).</summary>
    public const string KeyRevenue = "revenue";
    public const string KeyProfit = "profit";
    public const string KeyQuantity = "quantity";
    public const string KeyCategory = "category";
    public const string KeyBrand = "brand";
    public const string KeyStock = "stock";

    /// <summary>Срез по коду или null, если его в отчёте нет (например, продаж за период не было).</summary>
    public AbcSlice? Slice(string key) => AbcSlices.FirstOrDefault(s => s.Key == key);

    /// <summary>Сводка по группам — сколько позиций и сколько денег в каждой.</summary>
    public IReadOnlyList<(string Group, int Count, double Sum, double Share)> AbcSummary { get; init; } = [];

    /// <summary>Сезонность одного товара.
    ///
    /// Доли считаются ТОЛЬКО по тем месяцам, за которые вообще есть история продаж. Это не
    /// придирка: если касса работает с августа, то у любого товара «зимой продаж нет», и без
    /// этой оговорки весь ассортимент оказался бы летним. Месяцы без истории — не ноль, а
    /// «неизвестно», и они исключены из знаменателя.</summary>
    public sealed record SeasonRow(
        string Name,
        double TotalSum,
        IReadOnlyList<double> MonthShare,
        IReadOnlyList<double> SeasonShare,
        string PeakSeason,
        string PeakMonths,
        string QuietMonths,
        int MonthsWithSales,
        string Kind);

    /// <summary>Сезонность по всему магазину. Строится по ВСЕЙ локальной истории продаж, а не
    /// за выбранный период: за один месяц сезонность в принципе не видна.</summary>
    public sealed record SeasonalityReport(
        DateTime? FirstSale,
        DateTime? LastSale,
        int CoveredMonths,
        int CoveredSeasons,
        bool Reliable,
        string Note,
        IReadOnlyList<double> ShopMonthRevenue,
        IReadOnlyList<bool> MonthCovered,
        IReadOnlyList<SeasonRow> Rows);

    private static readonly SeasonalityReport EmptySeasonality =
        new(null, null, 0, 0, false, "", new double[12], new bool[12], []);

    /// <summary>Сезонность товаров за всю историю кассы.</summary>
    public SeasonalityReport Seasonality { get; init; } =
        new(null, null, 0, 0, false, "", new double[12], new bool[12], []);

    /// <summary>Остатки склада: что заканчивается (по дням запаса).</summary>
    public IReadOnlyList<(string Name, double Stock, double DailyRate, double DaysLeft)> Restock { get; init; } = [];

    public double StockValue { get; init; }
    public int StockPositions { get; init; }

    /// <summary>Цифры периода с сервера — те же, что показывает сайт (2026-09-28, сверка с вебом).
    ///
    /// Раньше выгрузка Excel/Word, ABC в «Сводке» программы владельца и раздел «ABC-анализ»
    /// считались только по локальной истории кассы (SoldLineItems), а экраны «Финансы» и «Сводка» —
    /// по серверу. За 28.09 выгрузка дала 153 052,97 / 40 чеков против 152 992,47 / 39 на экране:
    /// в локальную историю попадают продажи в долг (сервер их в выручку не берёт), строки,
    /// подтянутые с сервера, записывались по цене ДО скидки на строку (205 вместо 184,50), а
    /// «Скидки» брались только из локальной таблицы скидок на весь чек и показывали 0,00. ABC по
    /// локальной истории расходился с сайтом ещё сильнее (440 тыс. против 586 тыс.): история
    /// добиралась с сервера лишь по первой странице продаж.
    ///
    /// Теперь, когда сервер доступен, выручка, чеки, выручка по дням и возвраты берутся из вкладки
    /// «Продажи» сайта (tab=sales), товары, категории и бренды для топа и ABC — из вкладки «Товары»
    /// (tab=products), скидки — сумма discount_total оплаченных чеков периода. Без связи отчёт,
    /// как и раньше, считается по локальной истории.</summary>
    public sealed class ServerFigures
    {
        /// <param name="ProductId">Пусто у товаров, удалённых из каталога после продажи.</param>
        public sealed record Product(string? ProductId, string Name, double Quantity, double Revenue, double PurchasePrice);

        /// <summary>Выручка периода — cards.revenue вкладки «Продажи»; null — вкладку не запрашивали.</summary>
        public double? Revenue { get; init; }

        public int? ReceiptCount { get; init; }

        /// <summary>«Документы» → «Возврат продажи»: возвраты, сделанные где угодно — на сайте, на
        /// любой кассе. Локальный журнал знает только свои.</summary>
        public double? Returns { get; init; }

        public IReadOnlyList<(DateTime Day, double Revenue)>? ByDay { get; init; }

        /// <summary>Сумма discount_total оплаченных чеков периода (скидки на строку и на весь чек, с
        /// любой кассы); null — не считали.</summary>
        public double? Discounts { get; init; }

        /// <summary>Все проданные за период товары (top_by_revenue без ограничения числа строк).</summary>
        public IReadOnlyList<Product> Products { get; init; } = [];

        public IReadOnlyList<(string Name, double Quantity, double Revenue)> Categories { get; init; } = [];

        public IReadOnlyList<(string Name, double Quantity, double Revenue)> Brands { get; init; } = [];

        /// <summary>Скачивает цифры периода [from; to] (дни включительно).</summary>
        /// <param name="full">false — только товары (для ABC: один запрос, его можно делать
        /// после каждой продажи); true — ещё выручка, чеки, возвраты и скидки (для выгрузки).</param>
        public static async Task<ServerFigures?> FetchAsync(
            ISalesApiService api, DateTime fromLocal, DateTime toLocal, bool full, CancellationToken ct)
        {
            var from = fromLocal.Date;
            var to = toLocal.Date;

            var productsReport = await api.MarketProductsReportAsync(from, to, ct).ConfigureAwait(false);
            if (productsReport.ValueKind != JsonValueKind.Object
                || !productsReport.TryGetProperty("tables", out var tables)
                || tables.ValueKind != JsonValueKind.Object)
                return null;

            var products = new List<Product>();
            foreach (var row in Rows(tables, "top_by_revenue"))
            {
                var name = Text(row, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                products.Add(new Product(
                    Text(row, "product_id"),
                    name,
                    Num(row, "qty_sold"),
                    Num(row, "revenue"),
                    Num(row, "purchase_price")));
            }

            var categories = Rows(tables, "categories")
                .Select(r => (Name: Text(r, "category") ?? "", Quantity: Num(r, "qty_sold"), Revenue: Num(r, "revenue")))
                .Where(r => r.Name.Length > 0)
                .ToList();
            var brands = Rows(tables, "brands")
                .Select(r => (Name: Text(r, "brand") ?? "", Quantity: Num(r, "qty_sold"), Revenue: Num(r, "revenue")))
                .Where(r => r.Name.Length > 0)
                .ToList();

            if (!full)
                return new ServerFigures { Products = products, Categories = categories, Brands = brands };

            var salesReport = await api.MarketSalesReportAsync(from, to, ct).ConfigureAwait(false);
            if (salesReport.ValueKind != JsonValueKind.Object
                || !salesReport.TryGetProperty("cards", out var cards)
                || cards.ValueKind != JsonValueKind.Object)
                return null;

            var byDay = new List<(DateTime Day, double Revenue)>();
            if (salesReport.TryGetProperty("charts", out var charts) && charts.ValueKind == JsonValueKind.Object)
            {
                foreach (var point in Rows(charts, "sales_dynamics"))
                {
                    if (DateTime.TryParse(Text(point, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        byDay.Add((day.Date, Num(point, "value")));
                }
            }

            double? returns = null;
            if (salesReport.TryGetProperty("tables", out var salesTables) && salesTables.ValueKind == JsonValueKind.Object)
            {
                foreach (var doc in Rows(salesTables, "documents"))
                {
                    if (string.Equals(Text(doc, "name"), "Возврат продажи", StringComparison.OrdinalIgnoreCase))
                        returns = Num(doc, "sum");
                }
            }

            // 2026-09-28 (BE-09): возвраты периода — из списка возвратов сервера (сверено: те же
            // число и сумма, что «Документы → Возврат продажи»). Не ответил — цифра выше.
            if (await Api.NurCrmReportsApi.ReturnsTotalsAsync(from, to, ct).ConfigureAwait(false) is { } listedReturns)
                returns = (double)listedReturns.Sum;

            return new ServerFigures
            {
                Revenue = Num(cards, "revenue"),
                ReceiptCount = (int)Math.Round(Num(cards, "transactions")),
                Returns = returns,
                ByDay = byDay.OrderBy(d => d.Day).ToList(),
                Discounts = await SumDiscountsAsync(api, from, to, ct).ConfigureAwait(false),
                Products = products,
                Categories = categories,
                Brands = brands,
            };
        }

        /// <summary>Скидки периода: у сайта отдельной цифры нет, поэтому складываем discount_total
        /// чеков, которые сайт считает выручкой (paid и partially_returned), — как плитка «Скидки»
        /// в «Продажах». null — список продаж не дочитан (тогда в отчёте останется локальная цифра).</summary>
        private static async Task<double?> SumDiscountsAsync(ISalesApiService api, DateTime from, DateTime to, CancellationToken ct)
        {
            // 2026-09-28: по 500 строк (сервер отдаёт до 500) и до 100 страниц — раньше 60 по 80
            // (4 800 чеков): у больших магазинов скидки за месяц считались не по всем чекам.
            // 2026-09-29, стресс-тест: страницы — по 3 сразу, как в «Финансах» (100 страниц одна за
            // другой — это минуты, выгрузка упиралась в свой тайм-аут и молча уходила на локальную
            // историю кассы). Упёрлись в предел — сумма неполная: null (в отчёте — локальная цифра
            // и запись в журнале), а не заниженная «сумма за период».
            const int pageSize = 500;
            const int maxPages = 100;
            const int parallelPages = 3;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sum = 0.0;
            try
            {
                for (var first = 1; first <= maxPages; first += parallelPages)
                {
                    var batch = await Task.WhenAll(Enumerable
                            .Range(first, Math.Min(parallelPages, maxPages - first + 1))
                            .Select(page => api.PosSalesListAsync(page, pageSize, null, ct, dateFrom: from, dateToExclusive: to.AddDays(1))))
                        .ConfigureAwait(false);
                    foreach (var rows in batch)
                    {
                        var added = 0;
                        foreach (var row in rows)
                        {
                            var id = Text(row, "id") ?? "";
                            if (id.Length > 0 && !seen.Add(id))
                                continue;
                            added++;
                            var status = (Text(row, "status") ?? "").ToLowerInvariant();
                            if (status is "paid" or "partially_returned")
                                sum += Num(row, "discount_total");
                        }

                        if (added == 0 || rows.Count < pageSize)
                            return sum;
                    }
                }

                PosLogger.Log($"Аналитика: скидки {from:dd.MM.yyyy}–{to:dd.MM.yyyy} не посчитаны — больше {maxPages * pageSize} чеков.", "WARNING");
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Аналитика: скидки периода с сервера не получены: {ex.Message}", "WARNING");
                return null;
            }
        }

        private static IEnumerable<JsonElement> Rows(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                : [];

        private static string? Text(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static double Num(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
                return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => 0,
            };
        }
    }

    /// <summary>Отчёт с цифрами сервера (как на сайте), если он доступен; без связи — по локальной
    /// истории, как <see cref="Build"/>. См. <see cref="ServerFigures"/>.</summary>
    /// <param name="full">true — для выгрузки (выручка, чеки, скидки, возвраты с сервера); false —
    /// только товары для ABC.</param>
    public static async Task<AnalyticsReportData> BuildAsync(
        ISalesApiService api, DateTime fromLocal, DateTime toLocal,
        bool includeSeasonality = true, bool full = false, CancellationToken ct = default)
    {
        ServerFigures? server = null;
        // Касса работает без сервера — не ждём ответа, которого не будет.
        // 2026-09-29: и в аварии сервера (ServerOutageMonitor) — сразу по истории кассы.
        if (!OfflineModeHelper.SellLocally)
        {
            // Не дольше 20 с: ABC и выгрузка без сервера всё равно строятся — по истории кассы,
            // а общий тайм-аут запроса (55 с) заставил бы ждать почти минуту.
            // 2026-09-29, стресс-тест: выгрузке (full) — до 150 с. Она ещё листает список чеков ради
            // скидок (у большого магазина за квартал — сотня страниц), и за 20 с не успевала: отчёт
            // молча строился по локальной истории кассы — выручка, чеки и ABC расходились с экраном.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(full ? 150 : 20));
            try
            {
                server = await ServerFigures.FetchAsync(api, fromLocal, toLocal, full, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Аналитика: сервер недоступен ({ex.Message}) — считаю по истории кассы.", "WARNING");
            }
        }

        return await Task.Run(() => Build(fromLocal, toLocal, includeSeasonality, server), ct).ConfigureAwait(false);
    }

    /// <summary>Собирает отчёт из локальных данных кассы. Всё считается на месте, без сети:
    /// выгрузку часто просят тогда, когда интернет уже недоступен, а цифры нужны те же, что
    /// на экране «Отчёты».</summary>
    /// <param name="includeSeasonality">Считать ли сезонность. Она читает ВСЮ историю продаж,
    /// а не выбранный период, поэтому нужна только там, где её показывают. Телеграм-бот и
    /// выгрузка ABC запрашивают отчёт часто и без неё — лишний полный проход по базе на каждый
    /// запрос там ни к чему.</param>
    /// <param name="server">Цифры сервера за тот же период (см. <see cref="BuildAsync"/>): чем они
    /// есть, тем и заменяют локальный расчёт. Бонусы, списания, расходы, оплаты долгов, остатки,
    /// прогноз пополнения и сезонность сервер не отдаёт — они всегда локальные.</param>
    public static AnalyticsReportData Build(
        DateTime fromLocal, DateTime toLocal, bool includeSeasonality = true, ServerFigures? server = null)
    {
        var fromUtc = fromLocal.Date.ToUniversalTime();
        var toUtc = toLocal.Date.AddDays(1).ToUniversalTime();

        var lines = SoldLineItemsStore.LoadWithPriceSince(fromUtc, toUtc);
        var adjustments = ClientLoyaltyStore.AdjustmentsBetween(fromUtc, toUtc);
        var events = ShiftEventsStore.TotalsBetween(fromUtc, toUtc);
        double Event(string kind) => events.TryGetValue(kind, out var v) ? v : 0;

        var gross = lines.Sum(l => l.Quantity * l.UnitPrice);

        var byDay = lines
            .GroupBy(l => l.SoldAt.ToLocalTime().Date)
            .Select(g => (Day: g.Key, Revenue: g.Sum(x => x.Quantity * x.UnitPrice)))
            .OrderBy(x => x.Day)
            .ToList();

        var top = server != null
            ? server.Products
                .Select(p => (Name: p.Name, Quantity: p.Quantity, Sum: p.Revenue))
                .OrderByDescending(x => x.Sum)
                .Take(20)
                .ToList()
            : lines
                .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(g => (
                    Name: g.First().ProductName,
                    Quantity: g.Sum(x => x.Quantity),
                    Sum: g.Sum(x => x.Quantity * x.UnitPrice)))
                .OrderByDescending(x => x.Sum)
                .Take(20)
                .ToList();

        var slices = server != null ? BuildServerAbcSlices(server) : BuildAbcSlices(lines);
        // Срез склада считается и без продаж за период, поэтому первым в списке может оказаться
        // он — сводку «по выручке» берём по коду, а не по номеру.
        var revenueSlice = slices.FirstOrDefault(s => s.Key == KeyRevenue);
        var abc = revenueSlice?.Rows ?? (IReadOnlyList<AbcRow>)[];

        return new AnalyticsReportData
        {
            FromLocal = fromLocal.Date,
            ToLocal = toLocal.Date,
            // С сервером — выручка сайта: уже за вычетом всех скидок, без долгов и отмен.
            Revenue = server?.Revenue ?? Math.Max(0, gross - adjustments.Discounts),
            // Сервер пишет оплату бонусами в ту же скидку чека — вычитаем её, как плитка «Скидки»
            // в «Продажах»: бонусы в отчёте отдельной строкой.
            Discounts = server?.Discounts is { } serverDiscounts
                ? Math.Max(0, serverDiscounts - adjustments.PointsRedeemed)
                : adjustments.Discounts,
            PointsRedeemed = adjustments.PointsRedeemed,
            Returns = server?.Returns ?? Event(ShiftEventsStore.KindReturn),
            WriteOffs = Event(ShiftEventsStore.KindWriteOff),
            Expenses = Event(ShiftEventsStore.KindExpense),
            DebtPayments = Event(ShiftEventsStore.KindDebtPayment),
            // Чек — это одна отметка времени: все строки одной продажи пишутся одним моментом.
            ReceiptCount = server?.ReceiptCount ?? lines.Select(l => l.SoldAt).Distinct().Count(),
            ByDay = server?.ByDay ?? byDay,
            TopProducts = top,
            Abc = abc,
            AbcSummary = revenueSlice?.Summary ?? [],
            AbcSlices = slices,
            Restock = BuildRestock(lines),
            Seasonality = includeSeasonality ? BuildSeasonality() : EmptySeasonality,
            StockValue = CatalogCacheService.Products.Sum(p => p.Quantity * LocalCartService.ParsePrice(p.PriceLine)),
            StockPositions = CatalogCacheService.Products.Count,
        };
    }

    /// <summary>Месяцы попадают только в текст «пик»/«не продаётся», по ним ничего не ищется —
    /// поэтому их можно переводить прямо здесь. Кыргызские названия берём в привычной для
    /// магазинов форме (январь, февраль), а не в календарной (Үчтүн айы): кассир должен узнать
    /// месяц с первого взгляда.</summary>
    private static string[] MonthNames => Tr.T(
        "январь,февраль,март,апрель,май,июнь,июль,август,сентябрь,октябрь,ноябрь,декабрь",
        "январь,февраль,март,апрель,май,июнь,июль,август,сентябрь,октябрь,ноябрь,декабрь",
        "January,February,March,April,May,June,July,August,September,October,November,December",
        "ocak,şubat,mart,nisan,mayıs,haziran,temmuz,ağustos,eylül,ekim,kasım,aralık",
        "yanvar,fevral,mart,aprel,may,iyun,iyul,avgust,sentabr,oktabr,noyabr,dekabr").Split(',');

    /// <summary>Внутренний код сезона: по нему идёт отбор в разделе сезонности, поэтому он
    /// остаётся русским и переводится только на показ.</summary>
    private static readonly string[] SeasonNames = ["зима", "весна", "лето", "осень"];

    /// <summary>Сезон по номеру месяца: 0 — зима (12, 1, 2), 1 — весна, 2 — лето, 3 — осень.</summary>
    private static int SeasonOf(int month) => month switch
    {
        12 or 1 or 2 => 0,
        3 or 4 or 5 => 1,
        6 or 7 or 8 => 2,
        _ => 3,
    };

    /// <summary>Считает сезонность по всей локальной истории продаж.
    ///
    /// Главная ловушка этого отчёта — короткая история. Если касса работает три месяца, то
    /// формально каждый товар «продаётся только летом», и такой вывод хуже, чем его отсутствие:
    /// владелец по нему закупит не то. Поэтому здесь два уровня защиты. Первый: месяцы, за
    /// которые истории нет вообще, не считаются нулями и не входят в знаменатель. Второй: пока
    /// история не покрывает хотя бы два сезона, ни один товар не помечается сезонным — отчёт
    /// прямо говорит, сколько ещё копить.</summary>
    public static SeasonalityReport BuildSeasonality()
    {
        var earliest = SoldLineItemsStore.GetEarliestDate();
        if (earliest is null)
        {
            return new SeasonalityReport(
                null, null, 0, 0, false,
                "Продаж в истории кассы пока нет.",
                new double[12], new bool[12], []);
        }

        var lines = SoldLineItemsStore.LoadWithPriceSince(earliest.Value.AddDays(-1));
        if (lines.Count == 0)
        {
            return new SeasonalityReport(
                null, null, 0, 0, false,
                "Продаж в истории кассы пока нет.",
                new double[12], new bool[12], []);
        }

        var monthCovered = new bool[12];
        var shopMonth = new double[12];
        foreach (var line in lines)
        {
            var month = line.SoldAt.ToLocalTime().Month - 1;
            monthCovered[month] = true;
            shopMonth[month] += line.Quantity * line.UnitPrice;
        }

        var coveredMonths = monthCovered.Count(c => c);
        var coveredSeasons = Enumerable.Range(0, 12)
            .Where(m => monthCovered[m])
            .Select(m => SeasonOf(m + 1))
            .Distinct()
            .Count();

        var first = lines.Min(l => l.SoldAt).ToLocalTime();
        var last = lines.Max(l => l.SoldAt).ToLocalTime();
        var reliable = coveredSeasons >= 2;

        var history = Tr.T("История", "Тарых", "History", "Geçmiş", "Tarix")
            + $": {first:dd.MM.yyyy} — {last:dd.MM.yyyy}, "
            + Tr.T("продажи есть в", "сатуулар", "sales in", "satışlar", "sotuvlar")
            + $" {coveredMonths} " + Tr.T("мес.", "айда болгон", "months", "ayı kapsıyor", "oyda bo'lgan") + ". ";
        var note = reliable
            ? history + Tr.T("Доли считаются только по этим месяцам: месяцы без истории — это «неизвестно», а не ноль.", "Үлүштөр ушул айлар боюнча гана эсептелет: тарыхы жок айлар — «белгисиз», нөл эмес.", "Shares are calculated over these months only: a month without history means “unknown”, not zero.", "Paylar yalnızca bu aylara göre hesaplanır: geçmişi olmayan aylar sıfır değil, «bilinmiyor» sayılır.", "Ulushlar faqat shu oylar bo'yicha hisoblanadi: tarixi yo'q oy — «noma'lum», nol emas.")
            : history + Tr.T("Для вывода о сезонности нужна история минимум за два сезона — иначе любой товар выглядит сезонным просто потому, что в другие месяцы касса ещё не работала. Ниже — распределение по месяцам как есть, без выводов.", "Мезгилдүүлүк боюнча тыянак чыгаруу үчүн кеминде эки сезондук тарых керек — болбосо ар кандай товар башка айларда касса иштебегендиктен эле мезгилдүү болуп көрүнөт. Төмөндө — айлар боюнча бөлүштүрүү кандай болсо ошондой, тыянаксыз.", "Seasonality can only be judged with at least two seasons of history — otherwise any product looks seasonal simply because the till was not running in the other months. Below is the month-by-month distribution as is, with no conclusions.", "Mevsimsellik hakkında sonuç çıkarmak için en az iki sezonluk geçmiş gerekir — aksi halde her ürün, kasa diğer aylarda henüz çalışmadığı için mevsimlik görünür. Aşağıda aylara göre dağılım yorum yapılmadan, olduğu gibi verilmiştir.", "Mavsumiylik haqida xulosa chiqarish uchun kamida ikki mavsumlik tarix kerak — aks holda har qanday mahsulot mavsumiy bo'lib ko'rinadi, chunki boshqa oylarda kassa hali ishlamagan. Quyida — oylar bo'yicha taqsimot o'z holicha, xulosasiz.");

        var rows = new List<SeasonRow>();
        foreach (var group in lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase))
        {
            var byMonth = new double[12];
            foreach (var line in group)
                byMonth[line.SoldAt.ToLocalTime().Month - 1] += line.Quantity * line.UnitPrice;

            var total = byMonth.Sum();
            if (total <= 0)
                continue;

            var monthShare = byMonth.Select(v => v / total * 100).ToArray();

            var seasonSums = new double[4];
            for (var m = 0; m < 12; m++)
                seasonSums[SeasonOf(m + 1)] += byMonth[m];
            var seasonShare = seasonSums.Select(v => v / total * 100).ToArray();

            var peakSeason = Array.IndexOf(seasonShare, seasonShare.Max());
            var monthsWithSales = byMonth.Count(v => v > 0);

            // Пик — месяцы, которые дают заметно больше среднего по покрытым месяцам.
            var average = 100.0 / Math.Max(1, coveredMonths);
            var peakMonths = Enumerable.Range(0, 12)
                .Where(m => monthCovered[m] && monthShare[m] >= average * 1.5)
                .Select(m => MonthNames[m])
                .ToList();

            // Тихие — только те месяцы, когда касса работала, а этот товар не продавался.
            var quietMonths = Enumerable.Range(0, 12)
                .Where(m => monthCovered[m] && byMonth[m] <= 0)
                .Select(m => MonthNames[m])
                .ToList();

            string kind;
            if (!reliable)
                kind = "Мало истории";
            else if (seasonShare[peakSeason] >= 55 && quietMonths.Count > 0)
                kind = "Сезонный";
            else
                kind = "Круглогодичный";

            rows.Add(new SeasonRow(
                Name: group.First().ProductName,
                TotalSum: total,
                MonthShare: monthShare,
                SeasonShare: seasonShare,
                PeakSeason: SeasonNames[peakSeason],
                PeakMonths: peakMonths.Count > 0 ? string.Join(", ", peakMonths) : "—",
                QuietMonths: quietMonths.Count > 0 ? string.Join(", ", quietMonths) : "—",
                MonthsWithSales: monthsWithSales,
                Kind: kind));
        }

        // Сезонные — наверх, внутри — по выручке: владельцу важнее крупные.
        rows = rows
            .OrderByDescending(r => r.Kind == "Сезонный")
            .ThenByDescending(r => r.TotalSum)
            .ToList();

        return new SeasonalityReport(
            first, last, coveredMonths, coveredSeasons, reliable, note, shopMonth, monthCovered, rows);
    }

    /// <summary>Готовит все срезы ABC за период.
    ///
    /// Себестоимость и категория/бренд берутся из каталога кассы по коду товара. Если товар
    /// удалили из каталога после продажи, себестоимости у него уже нет — такая строка в срез
    /// «по прибыли» не попадает, но остаётся в срезах по выручке и количеству. Молча считать
    /// её прибыль равной выручке нельзя: это завысило бы прибыль на всю сумму закупки.</summary>
    private static List<AbcSlice> BuildAbcSlices(
        IReadOnlyList<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines)
    {
        var slices = BuildSalesAbcSlices(lines);
        // Склад — последним: срезы продаж идут первыми, как и до его появления.
        if (BuildStockSlice() is { } stock)
            slices.Add(stock);
        return slices;
    }

    /// <summary>Срез «Склад по стоимости остатка» (2026-09-27): в каких товарах сейчас лежат
    /// деньги магазина. Стоимость позиции — остаток × закупочная цена.
    ///
    /// Товары без закупочной цены в срез не входят — по той же причине, что и в срезе «по
    /// прибыли»: подставить вместо неё цену продажи значило бы завысить их стоимость на всю
    /// наценку, и они встали бы выше товаров, у которых закупка указана честно. Сколько позиций
    /// так выпало, написано в пояснении среза: владелец должен видеть, что картина неполная.
    /// Услуги и комплекты тоже не считаются: у услуги нет остатка, а остаток комплекта — это
    /// остатки его составляющих, которые уже посчитаны каждая сама по себе.
    ///
    /// От периода срез не зависит — это снимок склада на сейчас. null — считать не из чего
    /// (на складе нет ни одной позиции с остатком и закупочной ценой).</summary>
    public static AbcSlice? BuildStockSlice()
    {
        // Копия массивом, а не перебор: каталог обновляется в UI-потоке, а расчёт идёт в фоне, и
        // перебор живого списка падал бы на «коллекция изменена».
        var inStock = CatalogCacheService.Products.ToArray()
            .Where(p => p is { Quantity: > 0, IsService: false, IsBundle: false })
            .ToList();
        var priced = inStock.Where(p => p.PurchasePrice > 0).ToList();
        var skipped = inStock.Count - priced.Count;

        var hint = Tr.T(
            "Остаток × закупочная цена: в каких товарах сейчас заморожены деньги магазина. Группа A — товары, в которых лежит 80 % стоимости склада. Это снимок склада на сейчас — от выбранного периода он не зависит.",
            "Калдык × сатып алуу баасы: дүкөндүн акчасы азыр кайсы товарларда байланып турат. A тобу — кампанын наркынын 80 %ын түзгөн товарлар. Бул кампанын азыркы абалы — тандалган мезгилге көз каранды эмес.",
            "Stock × purchase price: which products the shop's money is tied up in right now. Group A holds 80% of the inventory value. This is a snapshot of the stock right now and does not depend on the selected period.",
            "Stok × alış fiyatı: mağazanın parası şu anda hangi ürünlere bağlı. A grubu, depo değerinin %80'ini oluşturan ürünlerdir. Bu, deponun şu anki durumudur; seçilen döneme bağlı değildir.",
            "Qoldiq × xarid narxi: do'kon pullari hozir qaysi mahsulotlarda turib qolgan. A guruhi — ombor qiymatining 80 % ini tashkil etuvchi mahsulotlar. Bu omborning hozirgi holati — tanlangan davrga bog'liq emas.");
        if (skipped > 0)
        {
            hint += " " + Tr.T(
                $"Без закупочной цены — {skipped} поз., в расчёт они не вошли.",
                $"Сатып алуу баасы жок {skipped} позиция эсепке кирген жок.",
                $"{skipped} items without a purchase price were left out.",
                $"Alış fiyatı girilmemiş {skipped} kalem hesaba katılmadı.",
                $"Xarid narxi ko'rsatilmagan {skipped} ta pozitsiya hisobga kiritilmadi.");
        }

        var slice = BuildSlice(
            KeyStock,
            Tr.T("Склад по стоимости остатка", "Калдыктын наркы боюнча кампа", "Stock by inventory value", "Stok değerine göre depo", "Qoldiq qiymati bo'yicha ombor"),
            Tr.T("сом", "сом", "som", "som", "so'm"), isMoney: true,
            hint,
            priced.Select(p => (Name: p.Title, Quantity: p.Quantity, Value: p.Quantity * p.PurchasePrice)));

        return slice.Rows.Count > 0 ? slice : null;
    }

    /// <summary>Пять срезов по продажам за период. Пусто, если продаж не было.</summary>
    private static List<AbcSlice> BuildSalesAbcSlices(
        IReadOnlyList<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines)
    {
        if (lines.Count == 0)
            return [];

        var catalog = CatalogById();
        CatalogProductTileVm? Card(string productId) =>
            catalog.TryGetValue(productId, out var card) ? card : null;

        return SalesSlices(
            lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(g => (
                    Name: g.First().ProductName,
                    Quantity: g.Sum(x => x.Quantity),
                    Value: g.Sum(x => x.Quantity * x.UnitPrice))),
            lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                .Where(g => Card(g.Key) is { PurchasePrice: > 0 })
                .Select(g =>
                {
                    var purchase = Card(g.Key)!.PurchasePrice;
                    return (
                        Name: g.First().ProductName,
                        Quantity: g.Sum(x => x.Quantity),
                        Value: g.Sum(x => x.Quantity * (x.UnitPrice - purchase)));
                }),
            lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(g => (
                    Name: g.First().ProductName,
                    Quantity: g.Sum(x => x.Quantity),
                    Value: g.Sum(x => x.Quantity))),
            lines.GroupBy(
                    l => Card(l.ProductId)?.Category is { Length: > 0 } c ? c : "Без категории",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => (
                    Name: g.Key,
                    Quantity: g.Sum(x => x.Quantity),
                    Value: g.Sum(x => x.Quantity * x.UnitPrice))),
            lines.GroupBy(
                    l => Card(l.ProductId)?.Brand is { Length: > 0 } b ? b : "Без бренда",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => (
                    Name: g.Key,
                    Quantity: g.Sum(x => x.Quantity),
                    Value: g.Sum(x => x.Quantity * x.UnitPrice))));
    }

    /// <summary>Те же пять срезов, но по цифрам сервера — вкладке «Товары» сайта (2026-09-28): ABC
    /// в программе владельца и на сайте теперь считаются по одному правилу — только оплаченные
    /// чеки, выручка строки после скидки, все кассы компании. Категории и бренды — таблицы сайта
    /// как есть (сайт не относит к ним товары, удалённые из каталога). Прибыль — выручка минус
    /// количество × закупочная цена товара (с сервера, иначе из каталога кассы); товар без
    /// закупочной цены в срез прибыли не входит — как и в локальном расчёте. Плюс срез склада.</summary>
    private static List<AbcSlice> BuildServerAbcSlices(ServerFigures server)
    {
        var slices = new List<AbcSlice>();
        if (server.Products.Count > 0)
        {
            var catalog = CatalogById();
            double Purchase(ServerFigures.Product p) =>
                p.PurchasePrice > 0 ? p.PurchasePrice
                : !string.IsNullOrEmpty(p.ProductId) && catalog.TryGetValue(p.ProductId, out var card) ? card.PurchasePrice
                : 0;

            slices = SalesSlices(
                server.Products.Select(p => (Name: p.Name, Quantity: p.Quantity, Value: p.Revenue)),
                server.Products
                    .Where(p => Purchase(p) > 0)
                    .Select(p => (Name: p.Name, Quantity: p.Quantity, Value: p.Revenue - p.Quantity * Purchase(p))),
                server.Products.Select(p => (Name: p.Name, Quantity: p.Quantity, Value: p.Quantity)),
                server.Categories.Select(c => (Name: c.Name, Quantity: c.Quantity, Value: c.Revenue)),
                server.Brands.Select(b => (Name: b.Name, Quantity: b.Quantity, Value: b.Revenue)));
        }

        if (BuildStockSlice() is { } stock)
            slices.Add(stock);
        return slices;
    }

    private static Dictionary<string, CatalogProductTileVm> CatalogById()
    {
        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        // Копия массивом: каталог обновляется в UI-потоке, а расчёт идёт в фоне.
        foreach (var product in CatalogCacheService.Products.ToArray())
        {
            if (!string.IsNullOrEmpty(product.Id))
                catalog[product.Id] = product;
        }

        return catalog;
    }

    /// <summary>Заголовки и пояснения пяти срезов продаж — общие для локального и серверного расчёта.</summary>
    private static List<AbcSlice> SalesSlices(
        IEnumerable<(string Name, double Quantity, double Value)> byRevenue,
        IEnumerable<(string Name, double Quantity, double Value)> byProfit,
        IEnumerable<(string Name, double Quantity, double Value)> byQuantity,
        IEnumerable<(string Name, double Quantity, double Value)> byCategory,
        IEnumerable<(string Name, double Quantity, double Value)> byBrand)
    {
        return
        [
            BuildSlice(
                KeyRevenue,
                Tr.T("Товары по выручке", "Түшүм боюнча товарлар", "Products by revenue", "Ciroya göre ürünler", "Tushum bo'yicha mahsulotlar"),
                Tr.T("сом", "сом", "som", "som", "so'm"), isMoney: true,
                Tr.T("Классический ABC: где сосредоточены деньги магазина.", "Классикалык ABC: дүкөндүн акчасы кайда топтолгон.", "Classic ABC: where the shop's money is concentrated.", "Klasik ABC: mağazanın parası nerede toplanıyor.", "Klassik ABC: do'kon pullari qayerda jamlangan."),
                byRevenue),

            BuildSlice(
                KeyProfit,
                Tr.T("Товары по прибыли", "Пайда боюнча товарлар", "Products by profit", "Kâra göre ürünler", "Foyda bo'yicha mahsulotlar"),
                Tr.T("сом", "сом", "som", "som", "so'm"), isMoney: true,
                Tr.T("Выручка минус закупочная цена. Товар из группы A по выручке легко оказывается в C по прибыли.", "Түшүм минус сатып алуу баасы. Түшүм боюнча A тобундагы товар пайда боюнча оңой эле C тобуна түшүп калышы мүмкүн.", "Revenue minus the purchase price. A product in group A by revenue can easily end up in group C by profit.", "Ciro eksi alış fiyatı. Ciroda A grubundaki bir ürün, kârda kolayca C grubuna düşebilir.", "Tushum minus xarid narxi. Tushum bo'yicha A guruhidagi mahsulot foyda bo'yicha osongina C guruhiga tushib qolishi mumkin."),
                byProfit),

            BuildSlice(
                KeyQuantity,
                Tr.T("Товары по количеству", "Саны боюнча товарлар", "Products by quantity", "Adede göre ürünler", "Miqdor bo'yicha mahsulotlar"),
                Tr.T("шт.", "даана", "pcs", "adet", "dona"), isMoney: false,
                Tr.T("ABC по штукам, а не по деньгам: показывает товары, которые держат поток покупателей.", "Акча эмес, даана боюнча ABC: сатып алуучулардын агымын кармаган товарларды көрсөтөт.", "ABC by units rather than money: shows the products that keep customers coming.", "Para yerine adede göre ABC: müşteri akışını sağlayan ürünleri gösterir.", "Pul emas, dona bo'yicha ABC: xaridorlar oqimini ushlab turadigan mahsulotlarni ko'rsatadi."),
                byQuantity),

            BuildSlice(
                KeyCategory,
                Tr.T("Категории", "Категориялар", "Categories", "Kategoriler", "Kategoriyalar"),
                Tr.T("сом", "сом", "som", "som", "so'm"), isMoney: true,
                Tr.T("Те же 80/15/5, но по категориям каталога — что нельзя допускать до пустых полок.", "Ошол эле 80/15/5, бирок каталогдун категориялары боюнча — кайсы категориялардын текчелери бош калбашы керек.", "The same 80/15/5, but by catalog category — what must never run out on the shelf.", "Aynı 80/15/5, ama katalog kategorilerine göre — hangi kategorilerin rafta tükenmemesi gerektiğini gösterir.", "Xuddi shu 80/15/5, lekin katalog kategoriyalari bo'yicha: qaysi kategoriyalarni javonda tugatib qo'ymaslik kerak."),
                byCategory),

            BuildSlice(
                KeyBrand,
                Tr.T("Бренды", "Бренддер", "Brands", "Markalar", "Brendlar"),
                Tr.T("сом", "сом", "som", "som", "so'm"), isMoney: true,
                Tr.T("Кто из поставщиков-брендов реально делает выручку.", "Кайсы бренддер чындыгында түшүм алып келет.", "Which brands actually bring in the revenue.", "Hangi markalar gerçekten ciro getiriyor.", "Qaysi brend-yetkazib beruvchilar haqiqatan tushum keltiradi."),
                byBrand),
        ];
    }

    /// <summary>Общий расчёт одного среза: сортировка по убыванию меры, нарастающий итог,
    /// границы 80 / 95 %. Границы общепринятые; менять их «на глаз» не стоит, иначе отчёты
    /// разных периодов станут несравнимы.
    ///
    /// Считается по ВСЕМ позициям среза, а не по первым двадцати: смысл анализа именно в том,
    /// чтобы увидеть длинный хвост группы C, который в топ просто не попадает.</summary>
    private static AbcSlice BuildSlice(
        string key, string title, string unit, bool isMoney, string hint,
        IEnumerable<(string Name, double Quantity, double Value)> source)
    {
        var items = source
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .ToList();

        var total = items.Sum(x => x.Value);
        if (total <= 0)
            return new AbcSlice(key, title, unit, isMoney, hint, [], []);

        var rows = new List<AbcRow>(items.Count);
        var running = 0.0;
        foreach (var item in items)
        {
            var share = item.Value / total * 100;
            // Группа — по доле ДО этой позиции (2026-09-25): позиция, которая сама пересекает
            // границу 80 %, остаётся в A. Раньше считалось по доле ПОСЛЕ неё, и товар с 27,7 %
            // выручки, стоявший вторым после товара с 69,3 %, попадал в C — «самые мелкие».
            var before = running;
            running += share;
            var group = before < 80.0 ? "A" : before < 95.0 ? "B" : "C";
            rows.Add(new AbcRow(item.Name, item.Quantity, item.Value, share, running, group));
        }

        return new AbcSlice(key, title, unit, isMoney, hint, rows, BuildAbcSummary(rows));
    }

    private static List<(string Group, int Count, double Sum, double Share)> BuildAbcSummary(List<AbcRow> abc)
    {
        var total = abc.Sum(r => r.Sum);
        if (total <= 0)
            return [];

        return new[] { "A", "B", "C" }
            .Select(group =>
            {
                var rows = abc.Where(r => r.Group == group).ToList();
                var sum = rows.Sum(r => r.Sum);
                return (Group: group, Count: rows.Count, Sum: sum, Share: sum / total * 100);
            })
            .Where(x => x.Count > 0)
            .ToList();
    }

    /// <summary>На сколько дней хватит остатка при нынешней скорости продаж. Скорость считаем
    /// с ПЕРВОЙ известной продажи, а не за весь запрошенный период: если касса работает три
    /// дня, делить на тридцать нельзя — расход вышел бы вдесятеро заниженным.</summary>
    private static List<(string Name, double Stock, double DailyRate, double DaysLeft)> BuildRestock(
        IReadOnlyList<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines)
    {
        if (lines.Count == 0)
            return [];

        var earliest = lines.Min(l => l.SoldAt);
        var spanDays = Math.Max(1.0, (DateTime.UtcNow - earliest).TotalDays);

        var sold = lines
            .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);

        var rows = new List<(string, double, double, double)>();
        foreach (var product in CatalogCacheService.Products)
        {
            if (!sold.TryGetValue(product.Id, out var quantity) || quantity <= 0)
                continue;

            var rate = quantity / spanDays;
            if (rate <= 0)
                continue;

            rows.Add((product.Title, product.Quantity, rate, product.Quantity / rate));
        }

        return rows.OrderBy(r => r.Item4).Take(25).ToList();
    }
}
