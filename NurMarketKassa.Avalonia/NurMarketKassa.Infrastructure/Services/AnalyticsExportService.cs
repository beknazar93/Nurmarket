using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>
/// Выгрузка аналитики продаж и склада в Excel (.xlsx) и Word (.docx) с графиками.
///
/// Формат пишется напрямую через DocumentFormat.OpenXml (лицензия MIT, библиотека Microsoft) —
/// без Excel и Word на компьютере: на кассе офиса обычно нет, а файл нужен.
///
/// Excel — рабочая книга: на каждом листе шапка отчёта, таблица с закреплённой шапкой,
/// автофильтром и строкой «Итого» (формулами), рядом — настоящие диаграммы Excel по этим же
/// ячейкам. Word — документ для чтения и печати: титульный блок, оглавление, разделы с
/// графиками-картинками (см. AnalyticsChartRenderer) и таблицами.
///
/// Подписи — на языке интерфейса (как и названия ABC-срезов, которые уже переводятся в
/// AnalyticsReportData): отчёт на смеси русского и кыргызского выглядел бы небрежно.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AnalyticsExportService
{
    /// <summary>Сколько строк среза ABC попадает в Word: дальше таблица перестаёт читаться на
    /// бумаге, а полный список всегда есть в Excel-выгрузке.</summary>
    private const int WordAbcRows = 40;

    private static string ReportTitle => Tr.T("Аналитика продаж и склада", "Сатуу жана кампа аналитикасы", "Sales and stock analytics", "Satış ve stok analizi", "Savdo va ombor tahlili");

    /// <summary>Название отчёта для имени файла.</summary>
    public static string FileTitle => Tr.T("Аналитика продаж", "Сатуу аналитикасы", "Sales analytics", "Satış analizi", "Savdo tahlili");

    /// <summary>Готовое имя файла: «Аналитика продаж 01.09.2026-28.09.2026 Магазин.xlsx».</summary>
    public static string SuggestFileName(string report, DateTime from, DateTime to, string? shopName, string extension) =>
        ExportFileName.Build(report, ExportFileName.Period(from, to), shopName, extension);

    private static string Period(AnalyticsReportData d) =>
        d.FromLocal.Date == d.ToLocal.Date
            ? ReportFormat.Date(d.FromLocal)
            : $"{ReportFormat.Date(d.FromLocal)} — {ReportFormat.Date(d.ToLocal)}";

    // ------------------------------------------------------------------ подписи

    private static string Currency => ReportLabels.Currency;
    private static string LRevenue => Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum");
    private static string LReceipts => Tr.T("Чеков", "Чектер", "Receipts", "Fiş sayısı", "Cheklar");
    private static string LAverage => Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek");
    private static string LDiscounts => Tr.T("Скидки", "Арзандатуулар", "Discounts", "İndirimler", "Chegirmalar");
    private static string LBonuses => Tr.T("из них бонусами", "анын ичинен бонустар менен", "of which paid with bonuses", "bunun bonusla ödenen kısmı", "shundan bonuslar bilan");
    private static string LReturns => Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar");
    private static string LWriteOffs => Tr.T("Списания", "Эсептен чыгаруулар", "Write-offs", "Zayiatlar", "Hisobdan chiqarishlar");
    private static string LExpenses => Tr.T("Расход", "Чыгым", "Expenses", "Giderler", "Xarajatlar");
    private static string LDebtPayments => Tr.T("Оплата долгов", "Карыздарды төлөө", "Debt payments", "Borç ödemeleri", "Qarz to'lovlari");
    private static string LCatalogItems => Tr.T("Позиций в каталоге", "Каталогдогу позициялар", "Catalog items", "Katalogdaki ürünler", "Katalogdagi pozitsiyalar");
    private static string LStockValue => Tr.T("Склад по ценам продажи", "Кампа сатуу баасы менен", "Stock at selling prices", "Satış fiyatlarıyla stok", "Ombor sotish narxlarida");
    private static string LIndicator => Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich");
    private static string LValue => Tr.T("Значение", "Мааниси", "Value", "Değer", "Qiymati");
    private static string LDate => Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana");
    private static string LProduct => Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");
    private static string LQuantity => Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
    private static string LAmount => Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa");
    private static string LGroup => Tr.T("Группа", "Топ", "Group", "Grup", "Guruh");
    private static string LName => Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi");
    private static string LShare => Tr.T("Доля", "Үлүшү", "Share", "Pay", "Ulush");
    private static string LCumulative => Tr.T("Накопительно", "Топтолгон үлүш", "Cumulative", "Kümülatif", "Jamlangan");
    private static string LItems => Tr.T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar");
    private static string LSlice => Tr.T("Срез", "Кесим", "Slice", "Kesit", "Kesim");
    private static string LUnit => Tr.T("Ед.", "Бирдик", "Unit", "Birim", "Birlik");
    private static string LStock => Tr.T("Остаток", "Калдык", "In stock", "Stok", "Qoldiq");
    private static string LPerDay => Tr.T("Продаж в день", "Күнүнө сатуу", "Sales per day", "Günlük satış", "Kunlik sotuv");
    private static string LDaysLeft => Tr.T("Хватит на, дн.", "Канча күнгө жетет", "Days left", "Kalan gün", "Necha kunga yetadi");
    private static string LItemsShort => Tr.T("поз.", "поз.", "items", "kalem", "poz.");

    private static string SheetSummary => Tr.T("Сводка", "Жыйынтык", "Summary", "Özet", "Xulosa");
    private static string SheetByDay => Tr.T("По дням", "Күндөр боюнча", "By day", "Günlere göre", "Kunlar bo'yicha");
    private static string SheetTop => Tr.T("Топ товаров", "Топ товарлар", "Top products", "En çok satanlar", "Top mahsulotlar");
    private static string SheetAbcSummary => Tr.T("ABC-сводка", "ABC жыйынтыгы", "ABC summary", "ABC özeti", "ABC xulosasi");
    private static string SheetStock => Tr.T("Склад", "Кампа", "Stock", "Stok", "Ombor");

    private static string TitleByDay => Tr.T("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum");
    private static string TitleTop => Tr.T("Топ товаров по сумме", "Сумма боюнча топ товарлар", "Top products by amount", "Tutara göre en çok satanlar", "Summa bo'yicha top mahsulotlar");
    private static string TitleStructure => Tr.T("Структура за период", "Мезгилдин түзүмү", "Period structure", "Dönem yapısı", "Davr tuzilmasi");
    private static string TitleAbcShare => Tr.T("Доля групп в выручке", "Түшүмдөгү топтордун үлүшү", "Group share of revenue", "Grupların ciro payı", "Guruhlarning tushumdagi ulushi");
    private static string TitleAbcSummary => Tr.T("ABC-анализ: сводка по группам", "ABC-анализ: топтор боюнча жыйынтык", "ABC analysis: group summary", "ABC analizi: grup özeti", "ABC tahlili: guruhlar bo'yicha xulosa");
    private static string TitleRestock => Tr.T("Что пора заказать", "Эмнени буйрутма кылуу керек", "What to reorder", "Sipariş edilmesi gerekenler", "Nimani buyurtma qilish kerak");
    private static string TitlePareto => Tr.T("Диаграмма Парето", "Парето диаграммасы", "Pareto chart", "Pareto grafiği", "Pareto diagrammasi");
    private static string TitleParetoColumns => Tr.T("Парето столбцами", "Парето мамычалар менен", "Pareto columns", "Pareto sütunları", "Pareto ustunlari");
    private static string TitleGroupShare => Tr.T("доля групп", "топтордун үлүшү", "group shares", "grup payları", "guruh ulushlari");

    private static string AbcExplanation => Tr.T(
        "Группа A даёт первые 80 % результата, B — следующие 15 %, C — оставшиеся 5 %. Срезы считаются независимо: товар из группы A по выручке легко оказывается в C по прибыли.",
        "A тобу натыйжанын алгачкы 80 %ын берет, B — кийинки 15 %ын, C — калган 5 %ын. Кесимдер өз-өзүнчө эсептелет: түшүм боюнча A тобундагы товар пайда боюнча оңой эле C тобуна түшүшү мүмкүн.",
        "Group A delivers the first 80 % of the result, B the next 15 %, C the remaining 5 %. Slices are calculated independently: a product in group A by revenue can easily be in group C by profit.",
        "A grubu sonucun ilk %80'ini, B sonraki %15'ini, C ise kalan %5'ini sağlar. Kesitler bağımsız hesaplanır: ciroda A grubundaki bir ürün kârda kolayca C grubunda olabilir.",
        "A guruhi natijaning dastlabki 80 % ini, B — keyingi 15 % ini, C — qolgan 5 % ini beradi. Kesimlar mustaqil hisoblanadi: tushum bo'yicha A guruhidagi mahsulot foyda bo'yicha osongina C guruhida bo'lishi mumkin.");

    private static string RestockNote => Tr.T(
        "Скорость продаж — с первой известной продажи. Красным — запаса меньше чем на 3 дня, жёлтым — меньше чем на неделю.",
        "Сатуу ылдамдыгы — биринчи белгилүү сатуудан баштап. Кызыл — запас 3 күнгө жетпейт, сары — бир жумага жетпейт.",
        "Sales rate is counted from the first known sale. Red — less than 3 days of stock left, yellow — less than a week.",
        "Satış hızı ilk bilinen satıştan itibaren hesaplanır. Kırmızı — 3 günden az stok, sarı — bir haftadan az.",
        "Sotuv tezligi birinchi ma'lum sotuvdan hisoblanadi. Qizil — zaxira 3 kundan kam, sariq — bir haftadan kam.");

    private static string TruncatedNote(int shown, int total) => Tr.T(
        $"Показаны первые {shown} позиций из {total}; полный список — в выгрузке Excel.",
        $"{total} позициянын алгачкы {shown}и көрсөтүлдү; толук тизме — Excel файлында.",
        $"Showing the first {shown} of {total} items; the full list is in the Excel export.",
        $"{total} kalemden ilk {shown} tanesi gösteriliyor; tam liste Excel dosyasında.",
        $"{total} ta pozitsiyadan dastlabki {shown} tasi ko'rsatildi; to'liq ro'yxat — Excel faylida.");

    /// <summary>Мера среза для заголовка колонки: «Выручка», «Прибыль», «Продано».</summary>
    private static string SliceMeasure(AnalyticsReportData.AbcSlice slice) => slice.Key switch
    {
        "profit" => Tr.T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda"),
        "quantity" => Tr.T("Продано", "Сатылды", "Sold", "Satılan", "Sotildi"),
        _ => LRevenue,
    };

    /// <summary>Что лежит в строке среза: товар, категория или бренд.</summary>
    private static string SliceSubject(AnalyticsReportData.AbcSlice slice) => slice.Key switch
    {
        "category" => Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"),
        "brand" => Tr.T("Бренд", "Бренд", "Brand", "Marka", "Brend"),
        _ => LProduct,
    };

    private static string MeasureHeader(AnalyticsReportData.AbcSlice slice) =>
        ReportLabels.WithUnit(SliceMeasure(slice), slice.IsMoney ? Currency : slice.Unit);

    /// <summary>Подсветка остатка: меньше трёх дней — красным, меньше недели — жёлтым.</summary>
    private static ReportTone DaysTone(double daysLeft) =>
        daysLeft < 3 ? ReportTone.Bad : daysLeft < 7 ? ReportTone.Warn : ReportTone.None;

    private static ReportTone GroupTone(string group) => group switch
    {
        "A" => ReportTone.Good,
        "B" => ReportTone.Warn,
        _ => ReportTone.Info,
    };

    private static readonly string[] GroupColors =
    [
        ReportPalette.GroupChartColor("A"),
        ReportPalette.GroupChartColor("B"),
        ReportPalette.GroupChartColor("C"),
    ];

    // ------------------------------------------------------------------ Excel

    public static void ExportToExcel(string path, AnalyticsReportData data, string? shopName)
    {
        var meta = ReportMeta.Create(ReportTitle, shopName, Period(data));
        using var book = new ExcelWorkbookBuilder(path, meta);

        var summary = book.AddSheet(SheetSummary, ReportTitle);
        summary.AddKeyValues(
        [
            new XlKeyValue(ReportLabels.WithUnit(LRevenue, Currency), data.Revenue, XlKind.Money),
            new XlKeyValue(LReceipts, data.ReceiptCount, XlKind.Integer),
            new XlKeyValue(ReportLabels.WithUnit(LAverage, Currency), data.AverageReceipt, XlKind.Money),
            new XlKeyValue(ReportLabels.WithUnit(LDiscounts, Currency), data.Discounts, XlKind.Money),
            new XlKeyValue("      " + LBonuses, data.PointsRedeemed, XlKind.Money),
            new XlKeyValue(ReportLabels.WithUnit(LReturns, Currency), data.Returns, XlKind.Money),
            new XlKeyValue(ReportLabels.WithUnit(LWriteOffs, Currency), data.WriteOffs, XlKind.Money),
            new XlKeyValue(ReportLabels.WithUnit(LExpenses, Currency), data.Expenses, XlKind.Money),
            new XlKeyValue(ReportLabels.WithUnit(LDebtPayments, Currency), data.DebtPayments, XlKind.Money),
            new XlKeyValue(LCatalogItems, data.StockPositions, XlKind.Integer),
            new XlKeyValue(ReportLabels.WithUnit(LStockValue, Currency), data.StockValue, XlKind.Money),
        ], LIndicator, LValue);

        var linked = new List<(string, XlSheet)>();

        // ---- По дням
        var byDaySheet = book.AddSheet(SheetByDay, TitleByDay);
        var periodTotal = data.ByDay.Sum(x => x.Revenue);
        var byDay = byDaySheet.AddTable(
        [
            new XlColumn(LDate, XlKind.Date),
            new XlColumn(ReportLabels.WithUnit(LRevenue, Currency), XlKind.Money, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(LShare, "%"), XlKind.Percent, XlTotal.Sum),
        ],
        data.ByDay.Select(d => new object?[] { d.Day, d.Revenue, Share(d.Revenue, periodTotal) }));
        byDaySheet.AddBarChart(TitleByDay, byDay, categoryColumn: 0, valueColumn: 1, maxPoints: 62, categoryFormat: "dd.mm");
        linked.Add((TitleByDay, byDaySheet));

        // ---- Топ товаров
        var topSheet = book.AddSheet(SheetTop, TitleTop, landscape: true);
        var top = topSheet.AddTable(
        [
            new XlColumn(ReportLabels.Number, XlKind.Index),
            new XlColumn(LProduct, MaxWidth: 50),
            new XlColumn(LQuantity, XlKind.Quantity, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(LAmount, Currency), XlKind.Money, XlTotal.Sum),
        ],
        data.TopProducts.Select((p, i) => new object?[] { i + 1, p.Name, p.Quantity, p.Sum }));
        topSheet.AddBarChart(TitleTop, top, categoryColumn: 1, valueColumn: 3, maxPoints: 10, horizontal: true);
        linked.Add((TitleTop, topSheet));

        // ---- ABC: сводка по всем срезам (одна таблица — её можно фильтровать по срезу)
        var abcSlices = data.AbcSlices.Where(s => s.Rows.Count > 0).ToList();
        if (abcSlices.Count > 0)
        {
            var abcSheet = book.AddSheet(SheetAbcSummary, TitleAbcSummary, AbcExplanation, landscape: true);
            var summaryRows = abcSlices
                .SelectMany(slice => slice.Summary.Select(g => new object?[]
                {
                    slice.Title,
                    g.Group,
                    g.Count,
                    new XlCell(g.Sum, Kind: slice.IsMoney ? XlKind.Money : XlKind.Quantity),
                    slice.IsMoney ? Currency : slice.Unit,
                    g.Share,
                }))
                .ToList();
            var abcSummary = abcSheet.AddTable(
            [
                new XlColumn(LSlice, MaxWidth: 36),
                new XlColumn(LGroup, XlKind.Group),
                new XlColumn(LItems, XlKind.Integer),
                new XlColumn(LValue, XlKind.Money),
                new XlColumn(LUnit),
                new XlColumn(ReportLabels.WithUnit(LShare, "%"), XlKind.Percent),
            ], summaryRows, totals: false);

            // Круговая — по первому срезу (выручка): его строки идут в таблице первыми.
            abcSheet.AddPieChart(TitleAbcShare, abcSummary, categoryColumn: 1, valueColumn: 3,
                points: abcSlices[0].Summary.Count, GroupColors);
            linked.Add((TitleAbcSummary, abcSheet));

            // ---- По листу на срез
            foreach (var slice in abcSlices)
            {
                var sheet = book.AddSheet("ABC " + slice.Title, "ABC: " + slice.Title, slice.Hint, landscape: true);
                var columns = new List<XlColumn>
                {
                    new(ReportLabels.Number, XlKind.Index),
                    new(LGroup, XlKind.Group),
                    new(SliceSubject(slice), MaxWidth: 50),
                };
                if (slice.IsMoney)
                    columns.Add(new XlColumn(LQuantity, XlKind.Quantity, XlTotal.Sum));
                columns.Add(new XlColumn(MeasureHeader(slice), slice.IsMoney ? XlKind.Money : XlKind.Quantity, XlTotal.Sum));
                columns.Add(new XlColumn(ReportLabels.WithUnit(LShare, "%"), XlKind.Percent, XlTotal.Sum));
                columns.Add(new XlColumn(ReportLabels.WithUnit(LCumulative, "%"), XlKind.Percent));

                var table = sheet.AddTable(columns, slice.Rows.Select((r, i) => slice.IsMoney
                    ? new object?[] { i + 1, r.Group, r.Name, r.Quantity, r.Sum, r.Share, r.Cumulative }
                    : new object?[] { i + 1, r.Group, r.Name, r.Sum, r.Share, r.Cumulative }));

                var shareColumn = columns.Count - 2;
                sheet.AddParetoChart(TitlePareto + ": " + slice.Title, table, categoryColumn: 2, shareColumn: shareColumn,
                    cumulativeColumn: shareColumn + 1, groupColumn: 1, maxPoints: 20);
                linked.Add(("ABC: " + slice.Title, sheet));
            }
        }

        // ---- Склад
        var stockSheet = book.AddSheet(SheetStock, TitleRestock, RestockNote);
        stockSheet.AddTable(
        [
            new XlColumn(ReportLabels.Number, XlKind.Index),
            new XlColumn(LProduct, MaxWidth: 50),
            new XlColumn(LStock, XlKind.Quantity),
            new XlColumn(LPerDay, XlKind.Decimal2),
            new XlColumn(LDaysLeft, XlKind.Days),
        ],
        data.Restock.Select((r, i) => new object?[]
        {
            i + 1, r.Name, r.Stock, r.DailyRate, new XlCell(r.DaysLeft, DaysTone(r.DaysLeft)),
        }), totals: false);
        linked.Add((TitleRestock, stockSheet));

        summary.AddHeading(ReportLabels.Contents);
        summary.AddLinks(linked);

        book.Save();
    }

    private static double Share(double value, double total) => total > 0 ? value / total * 100 : 0;

    // ------------------------------------------------------------------- Word

    public static void ExportToWord(string path, AnalyticsReportData data, string? shopName)
    {
        var meta = ReportMeta.Create(ReportTitle, shopName, Period(data));
        using var doc = new WordReportBuilder(path, meta);

        doc.TitleBlock();
        doc.ContentsPlaceholder();
        doc.PageBreak();

        // 1. Итоги
        doc.Heading1("1. " + Tr.T("Итоги периода", "Мезгилдин жыйынтыгы", "Period summary", "Dönem özeti", "Davr yakunlari"));
        doc.Table(
            [new WColumn(LIndicator, 3), new WColumn(LValue, 1.4, Numeric: true)],
            [
                [ReportLabels.WithUnit(LRevenue, Currency), ReportFormat.Money(data.Revenue)],
                [LReceipts, ReportFormat.Integer(data.ReceiptCount)],
                [ReportLabels.WithUnit(LAverage, Currency), ReportFormat.Money(data.AverageReceipt)],
                [ReportLabels.WithUnit(LDiscounts, Currency), ReportFormat.Money(data.Discounts)],
                ["      " + LBonuses, ReportFormat.Money(data.PointsRedeemed)],
                [ReportLabels.WithUnit(LReturns, Currency), ReportFormat.Money(data.Returns)],
                [ReportLabels.WithUnit(LWriteOffs, Currency), ReportFormat.Money(data.WriteOffs)],
                [ReportLabels.WithUnit(LExpenses, Currency), ReportFormat.Money(data.Expenses)],
                [ReportLabels.WithUnit(LDebtPayments, Currency), ReportFormat.Money(data.DebtPayments)],
                [LCatalogItems, ReportFormat.Integer(data.StockPositions)],
                [ReportLabels.WithUnit(LStockValue, Currency), ReportFormat.Money(data.StockValue)],
            ]);

        // Круговая строится только по тому, что реально было: нули в легенде только мешают.
        var structure = new List<(string, double)>
        {
            (LRevenue, data.Revenue),
            (LDiscounts, data.Discounts),
            (LReturns, data.Returns),
            (LWriteOffs, data.WriteOffs),
            (LExpenses, data.Expenses),
        }.Where(x => x.Item2 > 0.005).ToList();
        doc.Heading2(TitleStructure);
        // Пустой период — не пустая картинка на полстраницы, а одна строка «нет данных».
        if (structure.Count > 0)
            doc.Image(AnalyticsChartRenderer.RenderPie(TitleStructure, structure), TitleStructure);
        else
            doc.Note(ReportLabels.NoData);

        // 2. Динамика
        doc.Heading1("2. " + TitleByDay);
        if (data.ByDay.Count > 0)
        {
            doc.Image(AnalyticsChartRenderer.RenderBars(TitleByDay,
                data.ByDay.Select(d => (d.Day.ToString("dd.MM"), d.Revenue)).ToList()), TitleByDay);
        }
        else
        {
            doc.Note(ReportLabels.NoData);
        }

        // 3. Топ товаров
        doc.Heading1("3. " + SheetTop);
        if (data.TopProducts.Count > 0)
        {
            doc.Image(AnalyticsChartRenderer.RenderBars(TitleTop,
                data.TopProducts.Take(10).Select(p => (p.Name, p.Sum)).ToList()), TitleTop);
        }
        doc.Table(
            [
                new WColumn(ReportLabels.Number, 0.5, Center: true),
                new WColumn(LProduct, 5.5),
                new WColumn(LQuantity, 1.4, Numeric: true),
                new WColumn(ReportLabels.WithUnit(LAmount, Currency), 1.8, Numeric: true),
            ],
            data.TopProducts.Select((p, i) => new[]
            {
                (i + 1).ToString(ReportFormat.Culture), p.Name, ReportFormat.Quantity(p.Quantity), ReportFormat.Money(p.Sum),
            }).ToList(),
            ["", ReportLabels.Total, ReportFormat.Quantity(data.TopProducts.Sum(p => p.Quantity)),
                ReportFormat.Money(data.TopProducts.Sum(p => p.Sum))]);

        // 4. ABC
        doc.Heading1("4. " + Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"));
        doc.Paragraph(AbcExplanation);

        foreach (var slice in data.AbcSlices)
        {
            if (slice.Rows.Count == 0)
                continue;

            string Value(double v) => slice.IsMoney ? ReportFormat.Money(v) : ReportFormat.Quantity(v);

            doc.Heading2(slice.Title);
            doc.Note(slice.Hint);
            doc.Table(
                [
                    new WColumn(LGroup, 1, Center: true),
                    new WColumn(LItems, 1.2, Numeric: true),
                    new WColumn(MeasureHeader(slice), 2, Numeric: true),
                    new WColumn(ReportLabels.WithUnit(LShare, "%"), 1.2, Numeric: true),
                ],
                slice.Summary.Select(g => new[]
                {
                    g.Group, ReportFormat.Integer(g.Count), Value(g.Sum), ReportFormat.Percent(g.Share),
                }).ToList(),
                [ReportLabels.Total, ReportFormat.Integer(slice.Summary.Sum(g => g.Count)), Value(slice.Summary.Sum(g => g.Sum)), ReportFormat.Percent(100)],
                (row, column) => column == 0 ? GroupTone(slice.Summary[row].Group) : ReportTone.None);

            var top = slice.Rows.Take(20).ToList();
            doc.Image(AnalyticsChartRenderer.RenderPie(
                $"ABC: {slice.Title} — {TitleGroupShare}",
                slice.Summary.Select(g => ($"{LGroup} {g.Group} ({g.Count} {LItemsShort})", g.Sum)).ToList()), slice.Title);
            doc.Image(AnalyticsChartRenderer.RenderParetoClassic(
                $"{TitlePareto}: {slice.Title}",
                top.Select(r => (r.Name, r.Share, r.Cumulative, r.Group)).ToList()), TitlePareto + ": " + slice.Title);
            doc.Image(AnalyticsChartRenderer.RenderPareto(
                $"{TitleParetoColumns}: {slice.Title}",
                top.Select(r => (r.Name, r.Sum, r.Cumulative)).ToList(),
                SliceMeasure(slice)), TitleParetoColumns + ": " + slice.Title);

            var rows = slice.Rows.Take(WordAbcRows).ToList();
            doc.Table(
                [
                    new WColumn(ReportLabels.Number, 0.5, Center: true),
                    new WColumn(LGroup, 0.8, Center: true),
                    new WColumn(SliceSubject(slice), 4.5),
                    new WColumn(MeasureHeader(slice), 1.7, Numeric: true),
                    new WColumn(ReportLabels.WithUnit(LShare, "%"), 1.1, Numeric: true),
                    new WColumn(ReportLabels.WithUnit(LCumulative, "%"), 1.4, Numeric: true),
                ],
                rows.Select((r, i) => new[]
                {
                    (i + 1).ToString(ReportFormat.Culture), r.Group, r.Name, Value(r.Sum),
                    ReportFormat.Percent(r.Share), ReportFormat.Percent(r.Cumulative),
                }).ToList(),
                tone: (row, column) => column == 1 ? GroupTone(rows[row].Group) : ReportTone.None);
            if (slice.Rows.Count > rows.Count)
                doc.Note(TruncatedNote(rows.Count, slice.Rows.Count));
        }

        // 5. Склад
        doc.Heading1("5. " + TitleRestock);
        doc.Note(RestockNote);
        doc.Table(
            [
                new WColumn(ReportLabels.Number, 0.5, Center: true),
                new WColumn(LProduct, 5),
                new WColumn(LStock, 1.3, Numeric: true),
                new WColumn(LPerDay, 1.4, Numeric: true),
                new WColumn(LDaysLeft, 1.4, Numeric: true),
            ],
            data.Restock.Select((r, i) => new[]
            {
                (i + 1).ToString(ReportFormat.Culture), r.Name, ReportFormat.Quantity(r.Stock),
                r.DailyRate.ToString("#,##0.00", ReportFormat.Culture), ReportFormat.Days(r.DaysLeft),
            }).ToList(),
            tone: (row, column) => column == 4 ? DaysTone(data.Restock[row].DaysLeft) : ReportTone.None);

        doc.Save();
    }
}
