using NurMarketKassa.Models.Pos;

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

    /// <summary>Собирает отчёт из локальных данных кассы. Всё считается на месте, без сети:
    /// выгрузку часто просят тогда, когда интернет уже недоступен, а цифры нужны те же, что
    /// на экране «Отчёты».</summary>
    /// <param name="includeSeasonality">Считать ли сезонность. Она читает ВСЮ историю продаж,
    /// а не выбранный период, поэтому нужна только там, где её показывают. Телеграм-бот и
    /// выгрузка ABC запрашивают отчёт часто и без неё — лишний полный проход по базе на каждый
    /// запрос там ни к чему.</param>
    public static AnalyticsReportData Build(
        DateTime fromLocal, DateTime toLocal, bool includeSeasonality = true)
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

        var top = lines
            .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Name: g.First().ProductName,
                Quantity: g.Sum(x => x.Quantity),
                Sum: g.Sum(x => x.Quantity * x.UnitPrice)))
            .OrderByDescending(x => x.Sum)
            .Take(20)
            .ToList();

        var slices = BuildAbcSlices(lines);
        var abc = slices.Count > 0 ? slices[0].Rows : (IReadOnlyList<AbcRow>)[];

        return new AnalyticsReportData
        {
            FromLocal = fromLocal.Date,
            ToLocal = toLocal.Date,
            Revenue = Math.Max(0, gross - adjustments.Discounts),
            Discounts = adjustments.Discounts,
            PointsRedeemed = adjustments.PointsRedeemed,
            Returns = Event(ShiftEventsStore.KindReturn),
            WriteOffs = Event(ShiftEventsStore.KindWriteOff),
            Expenses = Event(ShiftEventsStore.KindExpense),
            DebtPayments = Event(ShiftEventsStore.KindDebtPayment),
            // Чек — это одна отметка времени: все строки одной продажи пишутся одним моментом.
            ReceiptCount = lines.Select(l => l.SoldAt).Distinct().Count(),
            ByDay = byDay,
            TopProducts = top,
            Abc = abc,
            AbcSummary = slices.Count > 0 ? slices[0].Summary : [],
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
            + Tr.T("продажи есть в", "сатуу бар", "sales in", "satış var", "sotuv bor")
            + $" {coveredMonths} " + Tr.T("мес.", "ай", "mo.", "ay", "oy") + ". ";
        var note = reliable
            ? history + Tr.T("Доли считаются только по этим месяцам: месяцы без истории — это «неизвестно», а не ноль.", "Үлүштөр ушул айлар боюнча гана эсептелет: тарыхы жок айлар — «белгисиз», нөл эмес.", "Shares are calculated only over these months: a month without history means «unknown», not zero.", "Paylar yalnızca bu aylara göre hesaplanır: geçmişi olmayan ay «bilinmiyor» demektir, sıfır değil.", "Ulushlar faqat shu oylar bo'yicha hisoblanadi: tarixi yo'q oy — «noma'lum», nol emas.")
            : history + Tr.T("Для вывода о сезонности нужна история минимум за два сезона — иначе любой товар выглядит сезонным просто потому, что в другие месяцы касса ещё не работала. Ниже — распределение по месяцам как есть, без выводов.", "Мезгилдүүлүк боюнча тыянак чыгаруу үчүн кеминде эки сезондук тарых керек — болбосо ар кандай товар башка айларда касса иштебегендиктен эле мезгилдүү болуп көрүнөт. Төмөндө — айлар боюнча бөлүштүрүү кандай болсо ошондой, тыянаксыз.", "A seasonality conclusion needs at least two seasons of history - otherwise any product looks seasonal simply because the till was not running in the other months. Below is the month-by-month distribution as it is, without conclusions.", "Mevsimsellik sonucu için en az iki sezonluk geçmiş gerekir - aksi halde her ürün, diğer aylarda kasa çalışmadığı için mevsimlik görünür. Aşağıda aylara göre dağılım olduğu gibi, sonuçsuz.", "Mavsumiylik xulosasi uchun kamida ikki mavsumlik tarix kerak - aks holda har qanday mahsulot boshqa oylarda kassa ishlamagani uchun mavsumiy ko'rinadi. Quyida - oylar bo'yicha taqsimot qanday bo'lsa shundayligicha, xulosasiz.");

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
        if (lines.Count == 0)
            return [];

        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in CatalogCacheService.Products)
        {
            if (!string.IsNullOrEmpty(product.Id))
                catalog[product.Id] = product;
        }

        CatalogProductTileVm? Card(string productId) =>
            catalog.TryGetValue(productId, out var card) ? card : null;

        return
        [
            BuildSlice(
                "revenue",
                Tr.T("Товары по выручке", "Товарлар түшкөн акча боюнча", "Products by revenue", "Ciroya göre ürünler", "Tushum bo'yicha mahsulotlar"),
                Tr.T("сом", "сом", "KGS", "KGS", "KGS"), isMoney: true,
                Tr.T("Классический ABC: где сосредоточены деньги магазина.", "Классикалык ABC: дүкөндүн акчасы кайда топтолгон.", "Classic ABC: where the shop's money is concentrated.", "Klasik ABC: mağazanın parası nerede toplanıyor.", "Klassik ABC: do'kon puli qayerda to'plangan."),
                lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (
                        Name: g.First().ProductName,
                        Quantity: g.Sum(x => x.Quantity),
                        Value: g.Sum(x => x.Quantity * x.UnitPrice)))),

            BuildSlice(
                "profit",
                Tr.T("Товары по прибыли", "Товарлар пайда боюнча", "Products by profit", "Kâra göre ürünler", "Foyda bo'yicha mahsulotlar"),
                Tr.T("сом", "сом", "KGS", "KGS", "KGS"), isMoney: true,
                Tr.T("Выручка минус закупочная цена. Товар из группы A по выручке легко оказывается в C по прибыли.", "Түшкөн акча минус сатып алуу баасы. Түшкөн акча боюнча A тобундагы товар пайда боюнча C тобуна оңой түшөт.", "Revenue minus the purchase price. A product in group A by revenue easily lands in C by profit.", "Ciro eksi alış fiyatı. Ciroda A grubundaki bir ürün kârda kolayca C grubuna düşer.", "Tushum minus xarid narxi. Tushum bo'yicha A guruhidagi mahsulot foyda bo'yicha oson C ga tushadi."),
                lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                    .Where(g => Card(g.Key) is { PurchasePrice: > 0 })
                    .Select(g =>
                    {
                        var purchase = Card(g.Key)!.PurchasePrice;
                        return (
                            Name: g.First().ProductName,
                            Quantity: g.Sum(x => x.Quantity),
                            Value: g.Sum(x => x.Quantity * (x.UnitPrice - purchase)));
                    })),

            BuildSlice(
                "quantity",
                Tr.T("Товары по количеству", "Товарлар саны боюнча", "Products by quantity", "Adede göre ürünler", "Soni bo'yicha mahsulotlar"),
                Tr.T("шт.", "даана", "pcs", "adet", "dona"), isMoney: false,
                Tr.T("ABC по штукам, а не по деньгам: показывает товары, которые держат поток покупателей.", "Акча эмес, даана боюнча ABC: сатып алуучулардын агымын кармаган товарларды көрсөтөт.", "ABC by units rather than money: shows the products that keep customers coming.", "Para yerine adede göre ABC: müşteri akışını tutan ürünleri gösterir.", "Pul emas, dona bo'yicha ABC: xaridorlar oqimini ushlab turadigan mahsulotlarni ko'rsatadi."),
                lines.GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (
                        Name: g.First().ProductName,
                        Quantity: g.Sum(x => x.Quantity),
                        Value: g.Sum(x => x.Quantity)))),

            BuildSlice(
                "category",
                Tr.T("Категории", "Категориялар", "Categories", "Kategoriler", "Kategoriyalar"),
                Tr.T("сом", "сом", "KGS", "KGS", "KGS"), isMoney: true,
                Tr.T("Те же 80/15/5, но по категориям каталога — что нельзя допускать до пустых полок.", "Ошол эле 80/15/5, бирок каталогдун категориялары боюнча — кайсынысын бош текчеге чейин жеткирүүгө болбойт.", "The same 80/15/5, but by catalogue category - what must never run out on the shelf.", "Aynı 80/15/5, ama katalog kategorilerine göre - rafta hangisinin bitmemesi gerektiği.", "O'sha 80/15/5, lekin katalog kategoriyalari bo'yicha - qaysi biri javonda tugamasligi kerak."),
                lines.GroupBy(
                        l => Card(l.ProductId)?.Category is { Length: > 0 } c ? c : "Без категории",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(g => (
                        Name: g.Key,
                        Quantity: g.Sum(x => x.Quantity),
                        Value: g.Sum(x => x.Quantity * x.UnitPrice)))),

            BuildSlice(
                "brand",
                Tr.T("Бренды", "Бренддер", "Brands", "Markalar", "Brendlar"),
                Tr.T("сом", "сом", "KGS", "KGS", "KGS"), isMoney: true,
                Tr.T("Кто из поставщиков-брендов реально делает выручку.", "Кайсы бренд чын эле түшкөн акча алып келет.", "Which brands actually bring in the revenue.", "Hangi markalar gerçekten ciro getiriyor.", "Qaysi brendlar haqiqatan tushum keltiradi."),
                lines.GroupBy(
                        l => Card(l.ProductId)?.Brand is { Length: > 0 } b ? b : "Без бренда",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(g => (
                        Name: g.Key,
                        Quantity: g.Sum(x => x.Quantity),
                        Value: g.Sum(x => x.Quantity * x.UnitPrice)))),
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
            running += share;
            var group = running <= 80.0 ? "A" : running <= 95.0 ? "B" : "C";
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
