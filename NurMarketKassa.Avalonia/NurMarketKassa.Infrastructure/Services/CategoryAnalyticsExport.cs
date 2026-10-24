using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>2026-10-22 (аналитика по категориям): выгрузка вкладки «По категориям» в Excel и Word теми же построителями, что
/// <see cref="AnalyticsExportService"/> (раздел «по категориям»). Цифры — те, что на экране (<see cref="CategoryAnalyticsData"/>):
/// отчёт не пересчитывается заново, поэтому файл и экран не разойдутся.</summary>
[SupportedOSPlatform("windows")]
public static class CategoryAnalyticsExport
{
    public static string FileTitle => Tr.T("Аналитика по категориям", "Категориялар боюнча аналитика", "Category analytics", "Kategori analizi", "Kategoriyalar tahlili");

    private static string Cur => ReportLabels.Currency;
    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Period(CategoryAnalyticsData d) => $"{ReportFormat.Date(d.From)} — {ReportFormat.Date(d.To)}";

    private static string Subject(CategoryAnalyticsData d) =>
        d.SelectedIds.Count == 0 ? L("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar") : d.SelectionTitle;

    private static string Delta(double cur, double prev) =>
        prev <= 0 ? "" : $"{(cur >= prev ? "▲" : "▼")} {Math.Abs((cur - prev) / prev * 100):0.#} %";

    private static string Hint => L(
        "Себестоимость = проданное количество × закупочная цена из карточки товара на сегодня. Расходы по категориям сервер не отдаёт: общие расходы магазина показаны отдельно, а их доля — только пропорционально выручке (оценка). Приход, возвраты, списания и перемещения по категории сервер пока не отдаёт.",
        "Өздүк баа = сатылган саны × товар карточкасындагы бүгүнкү сатып алуу баасы. Чыгымдарды категория боюнча сервер бербейт: дүкөндүн жалпы чыгымдары өзүнчө, алардын үлүшү — түшүмгө пропорционалдуу гана (баалоо). Киреше, кайтаруулар, эсептен чыгаруулар жана жылдырууларды сервер азырынча бербейт.",
        "Cost = sold quantity × the product card's purchase price today. The server does not give expenses by category: shop-wide expenses are shown separately and their share is only proportional to revenue (an estimate). Receipts, returns, write-offs and transfers by category are not provided by the server yet.",
        "Maliyet = satılan miktar × ürün kartındaki bugünkü alış fiyatı. Sunucu giderleri kategoriye göre vermiyor: mağaza genel giderleri ayrı gösterilir, payı yalnızca ciroya orantılıdır (tahmin). Kategoriye göre giriş, iade, zayiat ve transferler henüz sunucudan gelmiyor.",
        "Tannarx = sotilgan miqdor × mahsulot kartochkasidagi bugungi xarid narxi. Server xarajatlarni kategoriya bo'yicha bermaydi: do'konning umumiy xarajatlari alohida, ulushi esa faqat tushumga mos (baho). Kategoriya bo'yicha kirim, qaytarish, hisobdan chiqarish va ko'chirishni server hozircha bermaydi.");

    private static List<(string Label, double Value, XlKind Kind)> Indicators(CategoryAnalyticsData d)
    {
        var c = d.Current;
        var known = c.CostCoverage >= 0.5;
        var list = new List<(string, double, XlKind)>
        {
            (ReportLabels.WithUnit(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Cur), c.Revenue, XlKind.Money),
        };
        if (known)
        {
            list.Add((ReportLabels.WithUnit(L("Себестоимость", "Өздүк баа", "Cost of goods", "Maliyet", "Tannarx"), Cur), c.Cost, XlKind.Money));
            list.Add((ReportLabels.WithUnit(L("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"), Cur), c.Gross, XlKind.Money));
            list.Add((L("Маржа, %", "Маржа, %", "Margin, %", "Marj, %", "Marja, %"), c.MarginPct, XlKind.Decimal2));
            list.Add((L("Наценка, %", "Үстөк, %", "Markup, %", "Kâr (maliyete göre), %", "Ustama, %"), c.MarkupPct, XlKind.Decimal2));
        }
        list.Add((L("Продано, шт.", "Сатылды, даана", "Units sold", "Satılan adet", "Sotildi, dona"), c.Qty, XlKind.Quantity));
        list.Add((L("Чеков с этими товарами", "Бул товарлар менен чектер", "Receipts with these goods", "Bu ürünlerle fişler", "Bu mahsulotlar bilan cheklar"), c.Receipts, XlKind.Integer));
        list.Add((ReportLabels.WithUnit(L("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), Cur), c.AvgReceipt, XlKind.Money));
        list.Add((L("Доля в выручке компании, %", "Компаниянын түшүмүндөгү үлүшү, %", "Share of company revenue, %", "Şirket cirosundaki pay, %", "Kompaniya tushumidagi ulushi, %"), d.SharePct, XlKind.Decimal2));
        if (known && d.Company.CostCoverage >= 0.5)
            list.Add((L("Маржа всей компании (та же оценка), %", "Бүт компаниянын маржасы (ошол баа), %", "Whole company margin (same estimate), %", "Tüm şirket marjı (aynı tahmin), %", "Butun kompaniya marjasi (shu baho), %"), d.Company.MarginPct, XlKind.Decimal2));
        if (d.Opex is { } o)
        {
            list.Add((ReportLabels.WithUnit(L("Общие расходы магазина (не относятся к категории)", "Дүкөндүн жалпы чыгымдары (категорияга таандык эмес)", "Shop-wide expenses (not tied to the category)", "Mağaza genel giderleri (kategoriye ait değil)", "Do'konning umumiy xarajatlari (kategoriyaga tegishli emas)"), Cur), o, XlKind.Money));
            if (known && d.AllocatedOpex is { } a && d.NetAfterOpex is { } n)
            {
                list.Add((ReportLabels.WithUnit(L("Доля расходов пропорционально выручке (оценка)", "Чыгымдардын түшүмгө пропорционалдуу үлүшү (баалоо)", "Expense share proportional to revenue (estimate)", "Ciroya orantılı gider payı (tahmin)", "Tushumga mos xarajat ulushi (baho)"), Cur), a, XlKind.Money));
                list.Add((ReportLabels.WithUnit(L("Чистая прибыль категории после доли расходов (оценка)", "Үлүштөн кийинки категориянын таза пайдасы (баалоо)", "Category net profit after the share (estimate)", "Pay sonrası kategori net kârı (tahmin)", "Ulushdan keyingi kategoriya sof foydasi (baho)"), Cur), n, XlKind.Money));
            }
        }
        if (d.StockKnown)
        {
            list.Add((L("Остаток сейчас, шт.", "Азыр калдык, даана", "In stock now, units", "Şu an stok, adet", "Hozir qoldiq, dona"), d.StockQty, XlKind.Quantity));
            list.Add((ReportLabels.WithUnit(L("Остаток по закупочной цене", "Калдык сатып алуу баасы боюнча", "Stock at purchase price", "Alış fiyatıyla stok", "Qoldiq xarid narxida"), Cur), d.StockCost, XlKind.Money));
            if (d.Turnover is { } t)
                list.Add((L("Оборачиваемость, раз за период", "Айлануу, мезгилде жолу", "Turnover, times per period", "Devir hızı, dönemde kez", "Aylanish, davrda marta"), t, XlKind.Decimal2));
            if (d.DaysOfStock is { } ds)
                list.Add((L("Запаса хватит, дней", "Запас канча күнгө жетет", "Days of stock", "Stok kaç gün yeter", "Zaxira necha kunga yetadi"), ds, XlKind.Days));
        }
        return list;
    }

    public static void ExportToExcel(string path, CategoryAnalyticsData d, string? shopName)
    {
        var meta = ReportMeta.Create(FileTitle, shopName, Period(d));
        using var book = new ExcelWorkbookBuilder(path, meta);

        var sum = book.AddSheet(L("Сводка", "Жыйынтык", "Summary", "Özet", "Xulosa"), FileTitle + ": " + Subject(d), Hint);
        var p = d.Previous;
        sum.AddKeyValues(Indicators(d).Select(i => new XlKeyValue(i.Label, i.Value, i.Kind)).ToList(),
            L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), L("Значение", "Мааниси", "Value", "Değer", "Qiymati"));

        var cmpSheet = book.AddSheet(L("К прошлому периоду", "Мурунку мезгилге", "Vs previous period", "Önceki dönem", "Oldingi davr"),
            L("Сравнение с прошлым периодом", "Мурунку мезгил менен салыштыруу", "Comparison with the previous period", "Önceki dönemle karşılaştırma", "Oldingi davr bilan solishtirish"),
            $"{ReportFormat.Date(d.PrevFrom)} — {ReportFormat.Date(d.PrevTo)}");
        cmpSheet.AddTable(
        [
            new XlColumn(L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich")),
            new XlColumn(L("Сейчас", "Азыр", "Now", "Şimdi", "Hozir"), XlKind.Money),
            new XlColumn(L("Прошлый период", "Мурунку мезгил", "Previous", "Önceki", "Oldingi"), XlKind.Money),
            new XlColumn(L("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish")),
        ],
        new[]
        {
            new object?[] { L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), d.Current.Revenue, p.Revenue, Delta(d.Current.Revenue, p.Revenue) },
            new object?[] { L("Себестоимость", "Өздүк баа", "Cost of goods", "Maliyet", "Tannarx"), d.Current.Cost, p.Cost, Delta(d.Current.Cost, p.Cost) },
            new object?[] { L("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"), d.Current.Gross, p.Gross, Delta(d.Current.Gross, p.Gross) },
            new object?[] { L("Продано, шт.", "Сатылды, даана", "Units sold", "Satılan adet", "Sotildi, dona"), d.Current.Qty, p.Qty, Delta(d.Current.Qty, p.Qty) },
        }, totals: false);

        var topSheet = book.AddSheet(L("Топ товаров", "Топ товарлар", "Top products", "En çok satanlar", "Top mahsulotlar"),
            L("Топ-10 товаров категории по выручке", "Категориянын түшүм боюнча топ-10 товары", "Top 10 products of the category by revenue", "Kategorinin ciroya göre en iyi 10 ürünü", "Kategoriyaning tushum bo'yicha top-10 mahsuloti"), landscape: true);
        var top = topSheet.AddTable(
        [
            new XlColumn(ReportLabels.Number, XlKind.Index),
            new XlColumn(L("Товар", "Товар", "Product", "Ürün", "Mahsulot"), MaxWidth: 50),
            new XlColumn(L("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), XlKind.Quantity, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Cur), XlKind.Money, XlTotal.Sum),
            new XlColumn(L("Маржа, %", "Маржа, %", "Margin, %", "Marj, %", "Marja, %"), XlKind.Decimal2),
        ],
        d.Top.Select((t, i) => new object?[] { i + 1, t.Name, t.Qty, t.Revenue, t.PurchasePrice > 0 && t.Revenue > 0 ? (t.Revenue - t.Cost) / t.Revenue * 100 : null }));
        if (d.Top.Count > 0)
            topSheet.AddBarChart(L("Топ-10 товаров", "Топ-10 товар", "Top 10 products", "En iyi 10 ürün", "Top-10 mahsulot"), top, categoryColumn: 1, valueColumn: 3, maxPoints: 10, horizontal: true);

        if (d.StockKnown)
        {
            var outSheet = book.AddSheet(L("Не продавались", "Сатылган жок", "Not sold", "Satılmayanlar", "Sotilmaganlar"),
                L("Есть остаток, но не продавались за период", "Калдыгы бар, бирок мезгилде сатылган эмес", "In stock but not sold in the period", "Stokta var ama dönemde satılmadı", "Qoldig'i bor, lekin davrda sotilmagan") + $" ({d.NotSoldTotal})");
            outSheet.AddTable(
            [
                new XlColumn(L("Товар", "Товар", "Product", "Ürün", "Mahsulot"), MaxWidth: 50),
                new XlColumn(L("Остаток", "Калдык", "In stock", "Stok", "Qoldiq"), XlKind.Quantity),
            ],
            d.NotSold.Select(n => new object?[] { n.Name, n.Qty }), totals: false);
        }

        book.Save();
    }

    public static void ExportToWord(string path, CategoryAnalyticsData d, string? shopName)
    {
        var meta = ReportMeta.Create(FileTitle, shopName, Period(d));
        using var doc = new WordReportBuilder(path, meta);
        doc.TitleBlock([(L("Категория", "Категория", "Category", "Kategori", "Kategoriya"), Subject(d))]);
        doc.Note(Hint);

        doc.Heading1("1. " + L("Итоги по категории", "Категория боюнча жыйынтык", "Category summary", "Kategori özeti", "Kategoriya xulosasi"));
        doc.Table(
            [new WColumn(L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), 3), new WColumn(L("Значение", "Мааниси", "Value", "Değer", "Qiymati"), 1.4, Numeric: true)],
            Indicators(d).Select(i => new[]
            {
                i.Label,
                i.Kind switch
                {
                    XlKind.Money => ReportFormat.Money(i.Value),
                    XlKind.Integer => ReportFormat.Integer(i.Value),
                    XlKind.Quantity => ReportFormat.Quantity(i.Value),
                    XlKind.Days => ReportFormat.Days(i.Value),
                    _ => i.Value.ToString("0.##", ReportFormat.Culture),
                },
            }).ToList());

        doc.Heading1("2. " + L("К прошлому периоду", "Мурунку мезгилге карата", "Versus the previous period", "Önceki döneme göre", "Oldingi davrga nisbatan"));
        var p = d.Previous;
        doc.Table(
            [new WColumn(L("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), 2.4), new WColumn(L("Сейчас", "Азыр", "Now", "Şimdi", "Hozir"), 1.4, Numeric: true),
             new WColumn(L("Прошлый период", "Мурунку мезгил", "Previous", "Önceki", "Oldingi"), 1.4, Numeric: true), new WColumn(L("Изменение", "Өзгөрүү", "Change", "Değişim", "O'zgarish"), 1.2, Numeric: true)],
            [
                [L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), ReportFormat.Money(d.Current.Revenue), ReportFormat.Money(p.Revenue), Delta(d.Current.Revenue, p.Revenue)],
                [L("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"), ReportFormat.Money(d.Current.Gross), ReportFormat.Money(p.Gross), Delta(d.Current.Gross, p.Gross)],
                [L("Продано, шт.", "Сатылды, даана", "Units sold", "Satılan adet", "Sotildi, dona"), ReportFormat.Quantity(d.Current.Qty), ReportFormat.Quantity(p.Qty), Delta(d.Current.Qty, p.Qty)],
            ]);

        doc.Heading1("3. " + L("Топ-10 товаров по выручке", "Түшүм боюнча топ-10 товар", "Top 10 products by revenue", "Ciroya göre en iyi 10 ürün", "Tushum bo'yicha top-10 mahsulot"));
        if (d.Top.Count > 0)
        {
            doc.Image(AnalyticsChartRenderer.RenderBars(L("Топ-10 товаров", "Топ-10 товар", "Top 10 products", "En iyi 10 ürün", "Top-10 mahsulot"),
                d.Top.Select(t => (t.Name, t.Revenue)).ToList()), L("Топ-10 товаров", "Топ-10 товар", "Top 10 products", "En iyi 10 ürün", "Top-10 mahsulot"));
            doc.Table(
                [new WColumn(L("Товар", "Товар", "Product", "Ürün", "Mahsulot"), 3), new WColumn(L("Кол-во", "Саны", "Qty", "Adet", "Soni"), 1, Numeric: true),
                 new WColumn(ReportLabels.WithUnit(L("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Cur), 1.4, Numeric: true), new WColumn(L("Маржа", "Маржа", "Margin", "Marj", "Marja"), 1, Numeric: true)],
                d.Top.Select(t => new[]
                {
                    t.Name, ReportFormat.Quantity(t.Qty), ReportFormat.Money(t.Revenue),
                    t.PurchasePrice > 0 && t.Revenue > 0 ? ReportFormat.Percent((t.Revenue - t.Cost) / t.Revenue * 100) : "—",
                }).ToList());
        }
        else
            doc.Note(ReportLabels.NoData);

        if (d.StockKnown)
        {
            doc.Heading1("4. " + L("Есть остаток, но не продавались", "Калдыгы бар, бирок сатылган эмес", "In stock but not sold", "Stokta var ama satılmadı", "Qoldig'i bor, lekin sotilmagan") + $" ({d.NotSoldTotal})");
            if (d.NotSold.Count > 0)
                doc.Table(
                    [new WColumn(L("Товар", "Товар", "Product", "Ürün", "Mahsulot"), 3), new WColumn(L("Остаток", "Калдык", "In stock", "Stok", "Qoldiq"), 1, Numeric: true)],
                    d.NotSold.Select(n => new[] { n.Name, ReportFormat.Quantity(n.Qty) }).ToList());
            else
                doc.Note(ReportLabels.NoData);
        }

        doc.Save();
    }
}
