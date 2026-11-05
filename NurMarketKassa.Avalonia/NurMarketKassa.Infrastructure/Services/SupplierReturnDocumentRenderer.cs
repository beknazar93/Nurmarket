using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05: бланк «Накладная на возврат товара поставщику» на A4 (QuestPDF). Страница, шрифт, шапка, таблица с чёрными линиями, итоги,
/// сумма прописью и подписи — те же, что у документов перемещения (<see cref="TransferDocumentRenderer"/>): строгий «classic» русской формы
/// накладной. Подписи «Отпустил» и «Принял (поставщик / перевозчик)» с местом под печать. Язык документа — <see cref="SupplierReturnDocData.Lang"/>.</summary>
public static class SupplierReturnDocumentRenderer
{
    static SupplierReturnDocumentRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void SavePdf(SupplierReturnDocData data, string path) => Build(data).GeneratePdf(path);

    public static byte[] PdfBytes(SupplierReturnDocData data) => Build(data).GeneratePdf();

    /// <summary>Страницы картинками (PNG) — печать и снимки для проверки.</summary>
    public static List<byte[]> PageImages(SupplierReturnDocData data, int dpi = 96) =>
        Build(data).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi }).ToList();

    private static IDocument Build(SupplierReturnDocData d)
    {
        var l = new TransferDocumentRenderer.L(d.Lang);
        var tin = l.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ");
        var tel = l.T("тел. ", "тел. ", "tel. ", "tel. ", "tel. ");
        return TransferDocumentRenderer.Compose(l, $"{SupplierReturnDocuments.PrintName(d)} · {TransferDocuments.Stamp(d.CreatedAt)}", col =>
        {
            TransferDocumentRenderer.HeaderBlock(col, d.OrgName, TransferDocumentRenderer.OrgLine(d.OrgInn, d.OrgAddress, d.OrgPhone, l),
                SupplierReturnDocuments.Title(d.Lang) + " " + TransferDocumentRenderer.NumberLine(d.Number, d.Date, l));

            var rows = new List<(string, string)>
            {
                (l.T("Отправитель:", "Жөнөтүүчү:", "Sender:", "Gönderen:", "Jo'natuvchi:"), string.Join(", ", new[] { d.Warehouse, d.OrgName }.Where(x => !string.IsNullOrWhiteSpace(x)))),
                (l.T("Получатель (поставщик):", "Алуучу (жеткирүүчү):", "Recipient (supplier):", "Alıcı (tedarikçi):", "Qabul qiluvchi (yetkazib beruvchi):"), SupplierReturnDocuments.SupplierLine(d, tin, tel)),
                (l.T("Основание:", "Негиз:", "Basis:", "Dayanak:", "Asos:"), SupplierReturnDocuments.BasisText(d)),
                (l.T("Причина возврата:", "Кайтаруу себеби:", "Reason:", "İade nedeni:", "Qaytarish sababi:"), SupplierReturnDocuments.ReasonName(d.Reason, d.Lang)),
                (l.T("Расчёты:", "Эсептешүү:", "Settlement:", "Hesaplaşma:", "Hisob-kitob:"), SupplierReturnDocuments.SettlementText(d)),
            };
            if (!string.IsNullOrWhiteSpace(d.Comment))
                rows.Add((l.T("Комментарий:", "Комментарий:", "Comment:", "Yorum:", "Izoh:"), d.Comment));
            TransferDocumentRenderer.Parties(col, rows);

            col.Item().PaddingTop(4).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(9, Unit.Millimetre);
                    c.RelativeColumn(6);
                    c.ConstantColumn(29, Unit.Millimetre);
                    c.ConstantColumn(13, Unit.Millimetre);
                    c.ConstantColumn(19, Unit.Millimetre);
                    c.ConstantColumn(25, Unit.Millimetre);
                    c.ConstantColumn(28, Unit.Millimetre);
                });
                t.Header(h =>
                {
                    h.Cell().Element(TransferDocumentRenderer.Head).AlignCenter().Text("№");
                    h.Cell().Element(TransferDocumentRenderer.Head).Text(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"));
                    h.Cell().Element(TransferDocumentRenderer.Head).Text(l.T("Артикул / штрихкод", "Артикул / штрихкод", "SKU / barcode", "Stok kodu / barkod", "Artikul / shtrix-kod"));
                    h.Cell().Element(TransferDocumentRenderer.Head).AlignCenter().Text(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik"));
                    h.Cell().Element(TransferDocumentRenderer.Head).AlignRight().Text(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"));
                    h.Cell().Element(TransferDocumentRenderer.Head).AlignRight().Text(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"));
                    h.Cell().Element(TransferDocumentRenderer.Head).AlignRight().Text(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"));
                });

                var i = 0;
                foreach (var line in d.Lines)
                {
                    i++;
                    var code = line.Barcode.Length == 0 ? line.Article : line.Article.Length == 0 || line.Article == line.Barcode ? line.Barcode : line.Article + " / " + line.Barcode;
                    t.Cell().Element(TransferDocumentRenderer.Cell).AlignCenter().Text(i.ToString(CultureInfo.InvariantCulture));
                    t.Cell().Element(TransferDocumentRenderer.Cell).Text(line.Name.Length == 0 ? "—" : line.Name);
                    t.Cell().Element(TransferDocumentRenderer.Cell).Text(code);
                    t.Cell().Element(TransferDocumentRenderer.Cell).AlignCenter().Text(line.Unit);
                    t.Cell().Element(TransferDocumentRenderer.Cell).AlignRight().Text(l.Qty(line.Qty));
                    t.Cell().Element(TransferDocumentRenderer.Cell).AlignRight().Text(line.Price > 0 ? l.Money(line.Price) : "—");
                    t.Cell().Element(TransferDocumentRenderer.Cell).AlignRight().Text(line.Price > 0 ? l.Money(line.Sum) : "—");
                }
                if (d.Lines.Count == 0)
                    t.Cell().ColumnSpan(7).Element(TransferDocumentRenderer.Cell).AlignCenter()
                        .Text(l.T("Состав возврата не получен", "Кайтаруунун курамы алынган жок", "The return contents were not received", "İade içeriği alınamadı", "Qaytarish tarkibi olinmadi"))
                        .FontColor(TransferDocumentRenderer.Soft);
                else
                {
                    t.Cell().ColumnSpan(4).Element(TransferDocumentRenderer.Total).AlignRight().Text(l.T("Итого:", "Жыйынтык:", "Total:", "Toplam:", "Jami:"));
                    t.Cell().Element(TransferDocumentRenderer.Total).AlignRight().Text(l.Qty(d.TotalQty));
                    t.Cell().Element(TransferDocumentRenderer.Total).Text("");
                    t.Cell().Element(TransferDocumentRenderer.Total).AlignRight().Text(l.Money(d.TotalSum));
                }
            });

            col.Item().ShowEntire().Column(end =>
            {
                end.Spacing(6);
                end.Item().Text(Summary(d, l)).FontSize(9);
                end.Item().Text(TransferDocumentRenderer.AmountInWords(d.TotalSum, l)).Bold();
                end.Item().LineHorizontal(1f).LineColor(TransferDocumentRenderer.Ink);
                TransferDocumentRenderer.Signatures(end, l,
                    (l.T("Отпустил", "Берди", "Released by", "Teslim eden", "Topshirdi"), d.IssuedBy, true),
                    (l.T("Принял (поставщик / перевозчик)", "Алды (жеткирүүчү / ташуучу)", "Received by (supplier / carrier)", "Teslim alan (tedarikçi / taşıyıcı)", "Qabul qildi (yetkazib beruvchi / tashuvchi)"), "", true));
            });
        });
    }

    /// <summary>«Всего наименований N, общее количество Q, на сумму S сом.» на языке документа.</summary>
    internal static string Summary(SupplierReturnDocData d, TransferDocumentRenderer.L l) => l.T(
        $"Всего наименований {d.Lines.Count}, общее количество {l.Qty(d.TotalQty)}, на сумму {l.Money(d.TotalSum)} {l.Currency}.",
        $"Бардыгы {d.Lines.Count} аталыш, жалпы саны {l.Qty(d.TotalQty)}, суммасы {l.Money(d.TotalSum)} {l.Currency}.",
        $"Total items: {d.Lines.Count}, total quantity: {l.Qty(d.TotalQty)}, amount: {l.Money(d.TotalSum)} {l.Currency}.",
        $"Toplam kalem: {d.Lines.Count}, toplam miktar: {l.Qty(d.TotalQty)}, tutar: {l.Money(d.TotalSum)} {l.Currency}.",
        $"Jami pozitsiyalar: {d.Lines.Count}, umumiy miqdor: {l.Qty(d.TotalQty)}, summa: {l.Money(d.TotalSum)} {l.Currency}.");
}
