namespace NurMarketKassa.Services;

/// <summary>2026-11-27: выгрузка «Аналитики склада» в Excel — те же цифры, что на экране (<see cref="StockAnalytics"/>): сводка, категории, «Заканчивается», «Нет в наличии»,
/// самые дорогие остатки. Шапка, стили и «Итого» — общие (<see cref="ExcelWorkbookBuilder"/>), как у остальных выгрузок аналитики. Закупка — только если она есть в отчёте (право роли).</summary>
public static class StockAnalyticsExport
{
    public static string FileTitle => Tr.T("Аналитика склада", "Кампанын аналитикасы", "Stock analytics", "Depo analizi", "Ombor tahlili");

    public static string SuggestFileName(string? shopName) =>
        ExportFileName.Build(FileTitle, DateTime.Today.ToString("dd.MM.yyyy"), shopName, "xlsx");

    private static string Currency => ReportLabels.Currency;

    public static void ExportToExcel(string path, StockAnalyticsReport data, string? shopName, string? filterNote = null)
    {
        var meta = ReportMeta.Create(FileTitle, shopName, DateTime.Today.ToString("dd.MM.yyyy"));
        using var book = new ExcelWorkbookBuilder(path, meta);
        var linked = new List<(string, XlSheet)>();

        var lProduct = Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");
        var lCategory = Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya");
        var lStock = Tr.T("Остаток", "Калдык", "In stock", "Stok", "Qoldiq");
        var lMin = Tr.T("Минимум", "Минимум", "Minimum", "Minimum", "Minimum");
        var lUnit = Tr.T("Ед.", "Бирдик", "Unit", "Birim", "Birlik");

        // ---- Сводка
        var summary = book.AddSheet(Tr.T("Сводка", "Жыйынтык", "Summary", "Özet", "Xulosa"), FileTitle, filterNote);
        var items = new List<XlKeyValue>
        {
            new(Tr.T("Всего товаров (позиций)", "Бардык товарлар (позициялар)", "Products (items) in total", "Toplam ürün (kalem)", "Jami mahsulotlar (pozitsiyalar)"), data.Positions, XlKind.Integer),
            new(Tr.T("Всего единиц на складе", "Кампадагы бирдиктердин саны", "Units in stock", "Stoktaki toplam adet", "Ombordagi birliklar"), data.Units, XlKind.Quantity),
        };
        if (data.PurchaseValue is { } buy)
            items.Add(new XlKeyValue(ReportLabels.WithUnit(Tr.T("Стоимость по закупке", "Сатып алуу баасы боюнча наркы", "Value at purchase price", "Alış fiyatıyla değer", "Xarid narxidagi qiymat"), Currency), buy, XlKind.Money));
        items.Add(new XlKeyValue(ReportLabels.WithUnit(Tr.T("Стоимость по продаже", "Сатуу баасы боюнча наркы", "Value at sale price", "Satış fiyatıyla değer", "Sotuv narxidagi qiymat"), Currency), data.SaleValue, XlKind.Money));
        if (data.Markup is { } markup)
            items.Add(new XlKeyValue(ReportLabels.WithUnit(Tr.T("Потенциальная наценка", "Мүмкүн болгон кошумча баа", "Potential markup", "Potansiyel kâr marjı", "Potensial ustama"), Currency), markup, XlKind.Money));
        items.Add(new XlKeyValue(Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), data.OutOfStock, XlKind.Integer, data.OutOfStock > 0 ? ReportTone.Bad : ReportTone.None));
        items.Add(new XlKeyValue(Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), data.Low, XlKind.Integer, data.Low > 0 ? ReportTone.Warn : ReportTone.None));
        items.Add(new XlKeyValue(Tr.T("В минусе", "Минуста", "Negative stock", "Eksi stok", "Minusda"), data.Negative, XlKind.Integer, data.Negative > 0 ? ReportTone.Bad : ReportTone.None));
        items.Add(new XlKeyValue(Tr.T("Без цены", "Баасыз", "No price", "Fiyatsız", "Narxsiz"), data.NoPrice, XlKind.Integer));
        items.Add(new XlKeyValue(Tr.T("Без штрихкода", "Штрихкодсуз", "No barcode", "Barkodsuz", "Shtrixkodsiz"), data.NoBarcode, XlKind.Integer));
        items.Add(new XlKeyValue(Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz"), data.NoCategory, XlKind.Integer));
        summary.AddKeyValues(items,
            Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"),
            Tr.T("Значение", "Мааниси", "Value", "Değer", "Qiymati"));

        // ---- Категории
        var catSheet = book.AddSheet(Tr.T("Категории", "Категориялар", "Categories", "Kategoriler", "Kategoriyalar"),
            Tr.T("Склад по категориям", "Категориялар боюнча кампа", "Stock by category", "Kategorilere göre depo", "Kategoriyalar bo'yicha ombor"), landscape: true);
        var catColumns = new List<XlColumn>
        {
            new(ReportLabels.Number, XlKind.Index),
            new(lCategory, MaxWidth: 40),
            new(Tr.T("Товаров", "Товарлар", "Products", "Ürün", "Mahsulotlar"), XlKind.Integer, XlTotal.Sum),
            new(Tr.T("Единиц", "Бирдик", "Units", "Adet", "Birliklar"), XlKind.Quantity, XlTotal.Sum),
        };
        if (data.ShowPurchase)
            catColumns.Add(new XlColumn(ReportLabels.WithUnit(Tr.T("По закупке", "Сатып алуу боюнча", "At purchase", "Alış", "Xarid bo'yicha"), Currency), XlKind.Money, XlTotal.Sum));
        catColumns.Add(new XlColumn(ReportLabels.WithUnit(Tr.T("По продаже", "Сатуу боюнча", "At sale", "Satış", "Sotuv bo'yicha"), Currency), XlKind.Money, XlTotal.Sum));
        catColumns.Add(new XlColumn(Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), XlKind.Integer, XlTotal.Sum));
        catColumns.Add(new XlColumn(Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), XlKind.Integer, XlTotal.Sum));
        catColumns.Add(new XlColumn(ReportLabels.WithUnit(Tr.T("Доля", "Үлүшү", "Share", "Pay", "Ulush"), "%"), XlKind.Percent));
        var catTable = catSheet.AddTable(catColumns, data.Categories.Select((c, i) =>
        {
            var row = new List<object?> { i + 1, c.Name, c.Positions, c.Units };
            if (data.ShowPurchase)
                row.Add(c.PurchaseValue ?? 0);
            row.Add(c.SaleValue);
            row.Add(c.Low);
            row.Add(c.OutOfStock);
            row.Add(c.Share);
            return row.ToArray();
        }));
        var valueColumn = data.ShowPurchase ? 5 : 4; // «По продаже» — диаграмма долей по ней
        if (data.Categories.Count > 0) // пустой склад — диаграмму не строим
        catSheet.AddPieChart(Tr.T("Доли категорий в стоимости склада", "Кампанын наркындагы категориялардын үлүшү", "Category shares of stock value", "Depo değerinde kategori payları", "Ombor qiymatidagi kategoriyalar ulushi"),
            catTable, categoryColumn: 1, valueColumn: valueColumn, points: Math.Min(8, data.Categories.Count),
            [.. NurMarketKassa.Core.Application.ChartPalette.Hex.Take(8)]);
        linked.Add((Tr.T("Склад по категориям", "Категориялар боюнча кампа", "Stock by category", "Kategorilere göre depo", "Kategoriyalar bo'yicha ombor"), catSheet));

        // ---- Заканчивается и нет в наличии
        AddProductSheet(book, linked,
            Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), data.LowItems, withMin: true, lProduct, lCategory, lStock, lMin, lUnit);
        AddProductSheet(book, linked,
            Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), data.OutItems, withMin: false, lProduct, lCategory, lStock, lMin, lUnit);

        // ---- Самые дорогие остатки
        var topName = Tr.T("Топ по стоимости", "Наркы боюнча топ", "Top by value", "Değere göre en çok", "Qiymat bo'yicha top");
        var topSheet = book.AddSheet(topName, Tr.T($"Топ-{StockAnalytics.TopCount} по стоимости остатка", $"Калдыктын наркы боюнча топ-{StockAnalytics.TopCount}", $"Top {StockAnalytics.TopCount} by stock value", $"Stok değerine göre ilk {StockAnalytics.TopCount}", $"Qoldiq qiymati bo'yicha top-{StockAnalytics.TopCount}"), landscape: true);
        var topColumns = new List<XlColumn>
        {
            new(ReportLabels.Number, XlKind.Index),
            new(lProduct, MaxWidth: 50),
            new(lStock, XlKind.Quantity),
            new(ReportLabels.WithUnit(Tr.T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"), Currency), XlKind.Money),
            new(ReportLabels.WithUnit(Tr.T("Стоимость", "Наркы", "Value", "Değer", "Qiymat"), Currency), XlKind.Money, XlTotal.Sum),
        };
        var topTable = topSheet.AddTable(topColumns, data.TopByValue.Select((r, i) => new object?[] { i + 1, r.Title, r.Quantity, r.SalePrice, r.SaleValue }));
        if (data.TopByValue.Count > 0)
        topSheet.AddBarChart(topName, topTable, categoryColumn: 1, valueColumn: 4, maxPoints: 10, horizontal: true);
        linked.Add((topName, topSheet));

        summary.AddHeading(ReportLabels.Contents);
        summary.AddLinks(linked);
        book.Save();
    }

    private static void AddProductSheet(ExcelWorkbookBuilder book, List<(string, XlSheet)> linked, string name, IReadOnlyList<StockProductRow> rows, bool withMin,
        string lProduct, string lCategory, string lStock, string lMin, string lUnit)
    {
        var sheet = book.AddSheet(name, name, landscape: true);
        var columns = new List<XlColumn>
        {
            new(ReportLabels.Number, XlKind.Index),
            new(lProduct, MaxWidth: 50),
            new(lCategory, MaxWidth: 30),
            new(lStock, XlKind.Quantity),
        };
        if (withMin)
            columns.Add(new XlColumn(lMin, XlKind.Quantity));
        columns.Add(new XlColumn(lUnit));
        sheet.AddTable(columns, rows.Select((r, i) =>
        {
            var line = new List<object?> { i + 1, r.Title, r.Category.Length == 0 ? StockAnalytics.NoCategoryLabel : r.Category, new XlCell(r.Quantity, r.Quantity <= 0 ? ReportTone.Bad : ReportTone.Warn) };
            if (withMin)
                line.Add(r.Threshold);
            line.Add(r.Unit);
            return line.ToArray();
        }), totals: false);
        linked.Add((name, sheet));
    }
}
