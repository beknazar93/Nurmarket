using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-31: созданный документ перемещения в Word (.docx) и Excel (.xlsx) — теми же построителями, что и выгрузки перемещений
/// и отчёты (WordReportBuilder, ExcelWorkbookBuilder): Word — для правки и печати у перевозчика, Excel — для пересчёта. Данные — из снимка
/// документа (<see cref="TransferDocData"/>): подписи на языке документа, номер и дата те же, что на PDF.</summary>
public static class TransferDocumentOffice
{
    private const string Blank = "________________";

    private static (string Title, string Name) Names(TransferDocData d) =>
        (TransferDocuments.Title(d.Kind, d.Lang) + " № " + d.Number, d.OrgName);

    private static string? DashIfEmpty(string? v, bool blankLine = false) => string.IsNullOrWhiteSpace(v) ? (blankLine ? Blank : "—") : v;

    // ------------------------------------------------------------------ Word

    public static void SaveWord(TransferDocData d, string path)
    {
        var l = new TransferDocumentRenderer.L(d.Lang);
        var (title, org) = Names(d);
        var meta = ReportMeta.Create(title, string.IsNullOrWhiteSpace(org) ? null : org, d.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        using var doc = new WordReportBuilder(path, meta);

        var details = new List<(string, string)>
        {
            (l.T("Отправитель", "Жөнөтүүчү", "Sender", "Gönderen", "Jo'natuvchi"), Join(d.OrgName, d.From)),
            (l.T("Получатель", "Алуучу", "Recipient", "Alıcı", "Qabul qiluvchi"), Join(d.OrgName, d.To)),
            (l.T("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul"), DashIfEmpty(d.Responsible, true)!),
            (l.T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), DashIfEmpty(d.Carrier, true)!),
            (l.T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), DashIfEmpty(d.Tracking, true)!),
            (l.T("Вес партии, кг", "Партиянын салмагы, кг", "Batch weight, kg", "Parti ağırlığı, kg", "Partiya og'irligi, kg"), d.Weight > 0 ? l.Qty(d.Weight) : Blank),
            (l.T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), l.T("Перемещение № ", "Жылышуу № ", "Stock transfer No. ", "Stok transferi No: ", "Ko'chirish № ") + d.TransferNumber),
        };
        if (!string.IsNullOrWhiteSpace(d.OrgInn))
            details.Insert(0, (l.T("ИНН", "ИНН", "TIN", "VKN", "STIR"), d.OrgInn));
        if (d.Kind == TransferDocKind.Transport)
        {
            details.Add((l.T("Водитель", "Айдоочу", "Driver", "Sürücü", "Haydovchi"), Blank));
            details.Add((l.T("Автомобиль, гос. номер", "Автоунаа, мамлекеттик номери", "Vehicle, plate number", "Araç, plaka", "Avtomobil, davlat raqami"), Blank));
            details.Add((l.T("Количество мест", "Орундардын саны", "Number of packages", "Koli sayısı", "Joylar soni"), Blank));
            details.Add((l.T("Дата и время погрузки", "Жүктөө күнү жана убактысы", "Loading date and time", "Yükleme tarihi ve saati", "Yuklash sanasi va vaqti"), Blank));
            details.Add((l.T("Дата и время выгрузки", "Түшүрүү күнү жана убактысы", "Unloading date and time", "Boşaltma tarihi ve saati", "Tushirish sanasi va vaqti"), Blank));
        }
        doc.TitleBlock(details);

        var number = new Func<int, string>(i => (i + 1).ToString(CultureInfo.InvariantCulture));
        var nameCol = l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi");
        var unitCol = l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik");
        var qtyCol = l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor");
        var barcodeCol = l.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod");
        var totalLabel = l.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");

        switch (d.Kind)
        {
            case TransferDocKind.Act:
                doc.Table(
                    [
                        new WColumn("№", 0.5, Center: true),
                        new WColumn(nameCol, 3.6),
                        new WColumn(barcodeCol, 1.6),
                        new WColumn(unitCol, 0.8, Center: true),
                        new WColumn(l.T("Передано", "Берилди", "Handed over", "Teslim edilen", "Topshirildi"), 1.1, Numeric: true),
                        new WColumn(l.T("Принято фактически", "Иш жүзүндө алынды", "Actually received", "Fiilen alınan", "Haqiqatda qabul qilindi"), 1.4, Numeric: true),
                        new WColumn(l.T("Расхождение", "Айырма", "Difference", "Fark", "Farq"), 1.1, Numeric: true),
                    ],
                    d.Lines.Select((x, i) => new[] { number(i), x.Name, x.Barcode.Length == 0 ? x.Article : x.Barcode, x.Unit, l.Qty(x.Qty), "", "" }).ToList(),
                    ["", totalLabel, "", "", l.Qty(d.TotalQty), "", ""]);
                doc.Paragraph(l.T("Состояние груза: [   ] без замечаний   [   ] есть повреждения   [   ] есть расхождения по количеству",
                    "Жүктүн абалы: [   ] эскертүүсүз   [   ] бузулуулар бар   [   ] саны боюнча айырма бар",
                    "Condition of the cargo: [   ] no remarks   [   ] damaged   [   ] quantity differences",
                    "Yükün durumu: [   ] sorunsuz   [   ] hasarlı   [   ] miktar farkı var",
                    "Yukning holati: [   ] e'tirozsiz   [   ] shikastlangan   [   ] miqdorda farq bor"));
                doc.Paragraph(l.T("Замечания:", "Эскертүүлөр:", "Remarks:", "Notlar:", "Izohlar:") + " ______________________________________________");
                doc.Paragraph("_______________________________________________________________________");
                doc.Signatures([l.T("Передал", "Берди", "Handed over", "Teslim eden", "Topshirdi"), l.T("Принял", "Алды", "Received", "Teslim alan", "Qabul qildi")]);
                break;

            case TransferDocKind.Transport:
                doc.Table(
                    [
                        new WColumn("№", 0.5, Center: true),
                        new WColumn(l.T("Наименование груза", "Жүктүн аталышы", "Cargo name", "Yük adı", "Yuk nomi"), 4.6),
                        new WColumn(unitCol, 0.9, Center: true),
                        new WColumn(qtyCol, 1.2, Numeric: true),
                        new WColumn(l.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"), 2),
                    ],
                    d.Lines.Select((x, i) => new[] { number(i), x.Name, x.Unit, l.Qty(x.Qty), "" }).ToList(),
                    ["", totalLabel, "", l.Qty(d.TotalQty), ""]);
                doc.Signatures([
                    l.T("Грузоотправитель", "Жүк жөнөтүүчү", "Consignor", "Gönderici", "Yuk jo'natuvchi"),
                    l.T("Водитель", "Айдоочу", "Driver", "Sürücü", "Haydovchi"),
                    l.T("Грузополучатель", "Жүк алуучу", "Consignee", "Alıcı", "Yuk qabul qiluvchi")]);
                break;

            default:
                var cols = new List<WColumn>
                {
                    new("№", 0.5, Center: true),
                    new(nameCol, 4.2),
                    new(barcodeCol, 1.8),
                    new(unitCol, 0.8, Center: true),
                    new(qtyCol, 1, Numeric: true),
                };
                if (d.ShowPrices)
                {
                    cols.Add(new WColumn(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), 1.3, Numeric: true));
                    cols.Add(new WColumn(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), 1.4, Numeric: true));
                }
                var rows = d.Lines.Select((x, i) =>
                {
                    var r = new List<string> { number(i), x.Name, x.Barcode.Length == 0 ? x.Article : x.Barcode, x.Unit, l.Qty(x.Qty) };
                    if (d.ShowPrices)
                    {
                        r.Add(x.Price > 0 ? l.Money(x.Price) : "—");
                        r.Add(x.Price > 0 ? l.Money(x.Sum) : "—");
                    }
                    return r.ToArray();
                }).ToList();
                var totals = new string[cols.Count];
                totals[1] = totalLabel;
                totals[4] = l.Qty(d.TotalQty);
                if (d.ShowPrices)
                    totals[^1] = l.Money(d.TotalSum);
                doc.Table(cols, rows, totals);
                if (d.ShowPrices)
                {
                    doc.Paragraph(l.T($"Всего наименований {d.Lines.Count}, общее количество {l.Qty(d.TotalQty)}, на сумму {l.Money(d.TotalSum)} {l.Currency}.",
                        $"Бардыгы {d.Lines.Count} аталыш, жалпы саны {l.Qty(d.TotalQty)}, суммасы {l.Money(d.TotalSum)} {l.Currency}.",
                        $"Total items: {d.Lines.Count}, total quantity: {l.Qty(d.TotalQty)}, amount: {l.Money(d.TotalSum)} {l.Currency}.",
                        $"Toplam kalem: {d.Lines.Count}, toplam miktar: {l.Qty(d.TotalQty)}, tutar: {l.Money(d.TotalSum)} {l.Currency}.",
                        $"Jami pozitsiyalar: {d.Lines.Count}, umumiy miqdor: {l.Qty(d.TotalQty)}, summa: {l.Money(d.TotalSum)} {l.Currency}."));
                    doc.Paragraph(d.Lang switch
                    {
                        "ru" => MoneyWords.Amount(d.TotalSum, "ru"),
                        "ky" => MoneyWords.Amount(d.TotalSum, "ky"),
                        _ => "",
                    });
                }
                doc.Signatures([
                    l.T("Отпустил", "Берди", "Released by", "Teslim eden", "Topshirdi"),
                    l.T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"),
                    l.T("Принял", "Алды", "Received by", "Teslim alan", "Qabul qildi")]);
                break;
        }

        doc.Save();
    }

    // ------------------------------------------------------------------ Excel

    public static void SaveExcel(TransferDocData d, string path)
    {
        var l = new TransferDocumentRenderer.L(d.Lang);
        var (title, org) = Names(d);
        var meta = ReportMeta.Create(title, string.IsNullOrWhiteSpace(org) ? null : org, null);
        using var book = new ExcelWorkbookBuilder(path, meta);

        var header = book.AddSheet(l.T("Документ", "Документ", "Document", "Belge", "Hujjat"), meta.Title);
        var fields = new List<XlKeyValue>
        {
            new(l.T("Номер", "Номери", "Number", "Numara", "Raqam"), d.Number, XlKind.Code),
            new(l.T("Дата", "Күнү", "Date", "Tarih", "Sana"), d.Date, XlKind.Date),
            new(l.T("Перемещение №", "Жылышуу №", "Stock transfer No.", "Stok transferi No", "Ko'chirish №"), d.TransferNumber, XlKind.Code),
            new(l.T("Отправитель", "Жөнөтүүчү", "Sender", "Gönderen", "Jo'natuvchi"), Join(d.OrgName, d.From)),
            new(l.T("Получатель", "Алуучу", "Recipient", "Alıcı", "Qabul qiluvchi"), Join(d.OrgName, d.To)),
            new(l.T("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul"), DashIfEmpty(d.Responsible)),
            new(l.T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), DashIfEmpty(d.Carrier)),
            new(l.T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), DashIfEmpty(d.Tracking), XlKind.Code),
            d.Weight > 0
                ? new XlKeyValue(l.T("Вес партии, кг", "Партиянын салмагы, кг", "Batch weight, kg", "Parti ağırlığı, kg", "Partiya og'irligi, kg"), d.Weight, XlKind.Quantity)
                : new XlKeyValue(l.T("Вес партии, кг", "Партиянын салмагы, кг", "Batch weight, kg", "Parti ağırlığı, kg", "Partiya og'irligi, kg"), "—"),
            new(l.T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar"), d.Lines.Count, XlKind.Integer),
            new(l.T("Всего единиц", "Жалпы бирдик", "Total units", "Toplam adet", "Jami birlik"), d.TotalQty, XlKind.Quantity),
        };
        if (d.ShowPrices)
            fields.Add(new XlKeyValue(l.T("Сумма по закупке", "Сатып алуу боюнча сумма", "Purchase total", "Alış toplamı", "Xarid bo'yicha jami") + ", " + l.Currency, d.TotalSum, XlKind.Money));
        if (!string.IsNullOrWhiteSpace(d.OrgInn))
            fields.Insert(3, new XlKeyValue(l.T("ИНН", "ИНН", "TIN", "VKN", "STIR"), d.OrgInn, XlKind.Code));
        header.AddKeyValues(fields);

        var contents = book.AddSheet(l.T("Состав", "Курамы", "Contents", "İçerik", "Tarkibi"), meta.Title);
        var columns = new List<XlColumn>
        {
            new("№", XlKind.Index),
            new(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"), MaxWidth: 50),
            new(l.T("Код", "Коду", "Code", "Kod", "Kod"), XlKind.Code),
            new(l.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), XlKind.Code),
            new(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik")),
            new(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"), XlKind.Quantity, XlTotal.Sum),
        };
        if (d.Kind == TransferDocKind.Act)
        {
            columns.Add(new XlColumn(l.T("Принято фактически", "Иш жүзүндө алынды", "Actually received", "Fiilen alınan", "Haqiqatda qabul qilindi"), XlKind.Quantity));
            columns.Add(new XlColumn(l.T("Расхождение", "Айырма", "Difference", "Fark", "Farq"), XlKind.Quantity));
        }
        if (d.ShowPrices && d.Kind == TransferDocKind.Waybill)
        {
            columns.Add(new XlColumn(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + ", " + l.Currency, XlKind.Money));
            columns.Add(new XlColumn(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa") + ", " + l.Currency, XlKind.Money, XlTotal.Sum));
        }
        contents.AddTable(columns, d.Lines.Select((x, i) =>
        {
            var r = new List<object?> { i + 1, x.Name, x.Article, x.Barcode, x.Unit, x.Qty };
            if (d.Kind == TransferDocKind.Act)
            {
                r.Add(null);
                r.Add(null);
            }
            if (d.ShowPrices && d.Kind == TransferDocKind.Waybill)
            {
                r.Add(x.Price > 0 ? x.Price : null);
                r.Add(x.Price > 0 ? x.Sum : null);
            }
            return r.ToArray();
        }));

        book.Save();
    }

    private static string Join(string org, string place) =>
        string.Join(", ", new[] { place, org }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : Blank;
}
