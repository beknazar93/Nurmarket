using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05: накладная на возврат поставщику в Word (.docx) и Excel (.xlsx) — теми же построителями, что документы перемещения
/// (WordReportBuilder, ExcelWorkbookBuilder). Данные — те же, что на PDF (<see cref="SupplierReturnDocData"/>): номер сервера, дата, подписи
/// на языке документа.</summary>
public static class SupplierReturnDocumentOffice
{
    private const string Blank = "________________";

    private static string Or(string? v, string fallback = "—") => string.IsNullOrWhiteSpace(v) ? fallback : v!;

    private static string TinLabel(TransferDocumentRenderer.L l) => l.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ");

    private static string TelLabel(TransferDocumentRenderer.L l) => l.T("тел. ", "тел. ", "tel. ", "tel. ", "tel. ");

    // ------------------------------------------------------------------ Word

    public static void SaveWord(SupplierReturnDocData d, string path)
    {
        var l = new TransferDocumentRenderer.L(d.Lang);
        var title = SupplierReturnDocuments.Title(d.Lang) + " № " + d.Number;
        var meta = ReportMeta.Create(title, string.IsNullOrWhiteSpace(d.OrgName) ? null : d.OrgName, d.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        using var doc = new WordReportBuilder(path, meta);

        var details = new List<(string, string)>
        {
            (l.T("Отправитель", "Жөнөтүүчү", "Sender", "Gönderen", "Jo'natuvchi"), Or(string.Join(", ", new[] { d.Warehouse, d.OrgName }.Where(x => !string.IsNullOrWhiteSpace(x))), Blank)),
            (l.T("Получатель (поставщик)", "Алуучу (жеткирүүчү)", "Recipient (supplier)", "Alıcı (tedarikçi)", "Qabul qiluvchi (yetkazib beruvchi)"),
                Or(SupplierReturnDocuments.SupplierLine(d, TinLabel(l), TelLabel(l)), Blank)),
            (l.T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), SupplierReturnDocuments.BasisText(d)),
            (l.T("Причина возврата", "Кайтаруу себеби", "Reason", "İade nedeni", "Qaytarish sababi"), SupplierReturnDocuments.ReasonName(d.Reason, d.Lang)),
            (l.T("Расчёты", "Эсептешүү", "Settlement", "Hesaplaşma", "Hisob-kitob"), SupplierReturnDocuments.SettlementText(d)),
        };
        if (!string.IsNullOrWhiteSpace(d.OrgInn))
            details.Insert(0, (l.T("ИНН", "ИНН", "TIN", "VKN", "STIR"), d.OrgInn));
        if (!string.IsNullOrWhiteSpace(d.Comment))
            details.Add((l.T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), d.Comment));
        doc.TitleBlock(details);

        var cols = new List<WColumn>
        {
            new("№", 0.5, Center: true),
            new(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"), 4.2),
            new(l.T("Артикул / штрихкод", "Артикул / штрихкод", "SKU / barcode", "Stok kodu / barkod", "Artikul / shtrix-kod"), 1.8),
            new(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik"), 0.8, Center: true),
            new(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"), 1, Numeric: true),
            new(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), 1.3, Numeric: true),
            new(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), 1.4, Numeric: true),
        };
        var rows = d.Lines.Select((x, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), x.Name, Code(x), x.Unit, l.Qty(x.Qty),
            x.Price > 0 ? l.Money(x.Price) : "—", x.Price > 0 ? l.Money(x.Sum) : "—",
        }).ToList();
        var totals = new string[cols.Count];
        totals[1] = l.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami");
        totals[4] = l.Qty(d.TotalQty);
        totals[^1] = l.Money(d.TotalSum);
        doc.Table(cols, rows, totals);
        doc.Paragraph(SupplierReturnDocumentRenderer.Summary(d, l));
        doc.Paragraph(d.Lang switch
        {
            "ru" => MoneyWords.Amount(d.TotalSum, "ru"),
            "ky" => MoneyWords.Amount(d.TotalSum, "ky"),
            _ => "",
        });
        doc.Signatures([
            l.T("Отпустил", "Берди", "Released by", "Teslim eden", "Topshirdi"),
            l.T("Принял (поставщик / перевозчик)", "Алды (жеткирүүчү / ташуучу)", "Received by (supplier / carrier)", "Teslim alan (tedarikçi / taşıyıcı)", "Qabul qildi (yetkazib beruvchi / tashuvchi)")]);
        doc.Save();
    }

    private static string Code(SupplierReturnDocLine x) =>
        x.Barcode.Length == 0 ? x.Article : x.Article.Length == 0 || x.Article == x.Barcode ? x.Barcode : x.Article + " / " + x.Barcode;

    // ------------------------------------------------------------------ Excel

    public static void SaveExcel(SupplierReturnDocData d, string path)
    {
        var l = new TransferDocumentRenderer.L(d.Lang);
        var title = SupplierReturnDocuments.Title(d.Lang) + " № " + d.Number;
        var meta = ReportMeta.Create(title, string.IsNullOrWhiteSpace(d.OrgName) ? null : d.OrgName, null);
        using var book = new ExcelWorkbookBuilder(path, meta);

        var header = book.AddSheet(l.T("Документ", "Документ", "Document", "Belge", "Hujjat"), meta.Title);
        var fields = new List<XlKeyValue>
        {
            new(l.T("Номер", "Номери", "Number", "Numara", "Raqam"), d.Number, XlKind.Code),
            new(l.T("Дата", "Күнү", "Date", "Tarih", "Sana"), d.Date, XlKind.Date),
            new(l.T("Отправитель", "Жөнөтүүчү", "Sender", "Gönderen", "Jo'natuvchi"), Or(string.Join(", ", new[] { d.Warehouse, d.OrgName }.Where(x => !string.IsNullOrWhiteSpace(x))))),
            new(l.T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), Or(d.SupplierName)),
            new(l.T("ИНН поставщика", "Жеткирүүчүнүн ИНН", "Supplier TIN", "Tedarikçi VKN", "Yetkazib beruvchi STIR"), Or(d.SupplierInn), XlKind.Code),
            new(l.T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), SupplierReturnDocuments.BasisText(d)),
            new(l.T("Причина возврата", "Кайтаруу себеби", "Reason", "İade nedeni", "Qaytarish sababi"), SupplierReturnDocuments.ReasonName(d.Reason, d.Lang)),
            new(l.T("Расчёты", "Эсептешүү", "Settlement", "Hesaplaşma", "Hisob-kitob"), SupplierReturnDocuments.SettlementText(d)),
            new(l.T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), Or(d.Comment)),
            new(l.T("Отпустил", "Берди", "Released by", "Teslim eden", "Topshirdi"), Or(d.IssuedBy)),
            new(l.T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar"), d.Lines.Count, XlKind.Integer),
            new(l.T("Всего единиц", "Жалпы бирдик", "Total units", "Toplam adet", "Jami birlik"), d.TotalQty, XlKind.Quantity),
            new(l.T("Сумма возврата", "Кайтаруунун суммасы", "Return amount", "İade tutarı", "Qaytarish summasi") + ", " + l.Currency, d.TotalSum, XlKind.Money),
        };
        if (!string.IsNullOrWhiteSpace(d.OrgInn))
            fields.Insert(2, new XlKeyValue(l.T("ИНН", "ИНН", "TIN", "VKN", "STIR"), d.OrgInn, XlKind.Code));
        header.AddKeyValues(fields);

        var contents = book.AddSheet(l.T("Состав", "Курамы", "Contents", "İçerik", "Tarkibi"), meta.Title);
        var columns = new List<XlColumn>
        {
            new("№", XlKind.Index),
            new(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"), MaxWidth: 50),
            new(l.T("Артикул", "Артикул", "SKU", "Stok kodu", "Artikul"), XlKind.Code),
            new(l.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), XlKind.Code),
            new(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik")),
            new(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"), XlKind.Quantity, XlTotal.Sum),
            new(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + ", " + l.Currency, XlKind.Money),
            new(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa") + ", " + l.Currency, XlKind.Money, XlTotal.Sum),
        };
        contents.AddTable(columns, d.Lines.Select((x, i) => new object?[]
        {
            i + 1, x.Name, x.Article, x.Barcode, x.Unit, x.Qty, x.Price > 0 ? x.Price : null, x.Price > 0 ? x.Sum : null,
        }));

        book.Save();
    }
}
