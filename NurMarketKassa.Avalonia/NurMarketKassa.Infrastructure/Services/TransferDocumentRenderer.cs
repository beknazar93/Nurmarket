using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-31: бланки документов перемещения на A4 (QuestPDF — тот же построитель, что у накладных «Клиентов» и закупок,
/// ClientDocRenderer). Стиль единый с накладной КР: строгий «classic» — Arial, реквизиты организации сверху, заголовок по центру
/// «№ … от …», таблица с чёрными линиями, итоги и сумма прописью, подписи «Отпустил / Принял / Перевозчик» с местом под печать.
/// Все подписи — на языке документа (ru/ky/en/tr/uz, <see cref="TransferDocData.Lang"/>), суммы — с разделителем тысяч, подвал — с поясом UTC.</summary>
public static class TransferDocumentRenderer
{
    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };
    private const string Ink = "#000000";
    private const string Soft = "#444444";
    private const string Line = "#000000";
    private const string HeadFill = "#E9E9E9";
    private const string TotalFill = "#F4F4F4";

    static TransferDocumentRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void SavePdf(TransferDocData data, string path) => Build(data).GeneratePdf(path);

    public static byte[] PdfBytes(TransferDocData data) => Build(data).GeneratePdf();

    /// <summary>Страницы картинками (PNG) — печать и снимки для проверки.</summary>
    public static List<byte[]> PageImages(TransferDocData data, int dpi = 96) =>
        Build(data).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi }).ToList();

    // ───────────────────────────── подписи и форматы ─────────────────────────────

    internal sealed class L
    {
        private readonly string _lang;
        public L(string lang) => _lang = lang;
        public string Lang => _lang;
        public string T(string ru, string ky, string en, string tr, string uz) => TransferDocuments.Pick(_lang, ru, ky, en, tr, uz);

        /// <summary>Числа: «1 234,56» (ru, ky, uz), «1,234.56» (en), «1.234,56» (tr) — разделитель тысяч всегда есть.</summary>
        public CultureInfo Culture => _lang switch
        {
            "en" => CultureInfo.GetCultureInfo("en-US"),
            "tr" => CultureInfo.GetCultureInfo("tr-TR"),
            _ => CultureInfo.GetCultureInfo("ru-RU"),
        };

        public string Money(double v) => v.ToString("#,##0.00", Culture);
        public string Qty(double v) => v.ToString("#,##0.###", Culture);
        public string Currency => T("сом", "сом", "KGS", "som", "so'm");
        public string Dash => "—";
    }

    private static string NumberLine(TransferDocData d, L l)
    {
        var date = d.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return l.Lang switch
        {
            "ky" => $"№ {d.Number}, {date}-ж.",
            "en" => $"No. {d.Number} dated {date}",
            "tr" => $"No: {d.Number}, tarih: {date}",
            "uz" => $"№ {d.Number}, {date}",
            _ => $"№ {d.Number} от {date}",
        };
    }

    /// <summary>Сумма прописью: по-русски и по-кыргызски — словами (MoneyWords), на en/tr/uz — цифрами с подписью.</summary>
    private static string AmountInWords(double total, L l) => l.Lang switch
    {
        "ru" => MoneyWords.Amount(total, "ru"),
        "ky" => MoneyWords.Amount(total, "ky"),
        _ => l.T("", "", "Amount in figures: ", "Rakamla tutar: ", "Raqamlarda summa: ") + l.Money(total) + " " + l.Currency,
    };

    private static string OrgLine(TransferDocData d, L l) => string.Join(", ", new[]
    {
        string.IsNullOrWhiteSpace(d.OrgInn) ? null : l.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + d.OrgInn,
        d.OrgAddress,
        string.IsNullOrWhiteSpace(d.OrgPhone) ? null : l.T("тел. ", "тел. ", "tel. ", "tel. ", "tel. ") + d.OrgPhone,
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private const string Blank = "________________________________";

    // ───────────────────────────── страница ─────────────────────────────

    private static IDocument Build(TransferDocData d)
    {
        var l = new L(d.Lang);
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(16, Unit.Millimetre);
                page.MarginVertical(14, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(10).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    Header(col, d, l);
                    switch (d.Kind)
                    {
                        case TransferDocKind.Act:
                            ActBody(col, d, l);
                            break;
                        case TransferDocKind.Transport:
                            TransportBody(col, d, l);
                            break;
                        default:
                            WaybillBody(col, d, l);
                            break;
                    }
                });

                page.Footer().PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Text($"{TransferDocuments.Title(d.Kind, d.Lang)} {d.Number} · {TransferDocuments.Stamp(d.CreatedAt)}").FontSize(8).FontColor(Soft);
                    row.AutoItem().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(8).FontColor(Soft));
                        t.Span(l.T("стр. ", "бет ", "page ", "sayfa ", "bet "));
                        t.CurrentPageNumber();
                        t.Span(l.T(" из ", " / ", " of ", " / ", " / "));
                        t.TotalPages();
                    });
                });
            });
        });
    }

    // ───────────────────────────── шапка ─────────────────────────────

    private static void Header(ColumnDescriptor col, TransferDocData d, L l)
    {
        col.Item().Column(c =>
        {
            c.Item().Text(string.IsNullOrWhiteSpace(d.OrgName) ? Blank : d.OrgName).Bold().FontSize(12);
            var line = OrgLine(d, l);
            if (line.Length > 0)
                c.Item().Text(line).FontSize(9);
        });
        col.Item().PaddingTop(6).AlignCenter().Text(TransferDocuments.Title(d.Kind, d.Lang) + " " + NumberLine(d, l)).Bold().FontSize(14);
        col.Item().LineHorizontal(1.2f).LineColor(Ink);
    }

    /// <summary>Строки «подпись: значение» — как у накладной КР (Отправитель / Получатель / Основание).</summary>
    private static void Parties(ColumnDescriptor col, IEnumerable<(string Label, string Value)> rows)
    {
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(40, Unit.Millimetre);
                c.RelativeColumn();
            });
            foreach (var (label, value) in rows)
            {
                t.Cell().PaddingVertical(2).Text(label).SemiBold();
                t.Cell().PaddingVertical(2).Text(string.IsNullOrWhiteSpace(value) ? Blank : value);
            }
        });
    }

    private static string Place(string org, string place) =>
        string.Join(", ", new[] { place, org }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Basis(TransferDocData d, L l) =>
        l.T("Перемещение товара № ", "Товардын жылышуусу № ", "Stock transfer No. ", "Stok transferi No: ", "Tovar ko'chirish № ") + d.TransferNumber
        + (string.IsNullOrWhiteSpace(d.Note) ? "" : " (" + d.Note + ")");

    private static string CarrierText(TransferDocData d)
    {
        var parts = new[] { d.Carrier, d.Tracking }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return string.Join(" · ", parts);
    }

    // ───────────────────────────── накладная ─────────────────────────────

    private static void WaybillBody(ColumnDescriptor col, TransferDocData d, L l)
    {
        var rows = new List<(string, string)>
        {
            (l.T("Отправитель:", "Жөнөтүүчү:", "Sender:", "Gönderen:", "Jo'natuvchi:"), Place(d.OrgName, d.From)),
            (l.T("Получатель:", "Алуучу:", "Recipient:", "Alıcı:", "Qabul qiluvchi:"), Place(d.OrgName, d.To)),
            (l.T("Ответственный:", "Жооптуу:", "Responsible:", "Sorumlu:", "Mas'ul:"), d.Responsible),
        };
        if (CarrierText(d).Length > 0)
            rows.Add((l.T("Перевозчик:", "Ташуучу:", "Carrier:", "Taşıyıcı:", "Tashuvchi:"), CarrierText(d)));
        if (d.Weight > 0)
            rows.Add((l.T("Вес партии:", "Партиянын салмагы:", "Batch weight:", "Parti ağırlığı:", "Partiya og'irligi:"), l.Qty(d.Weight) + " " + l.T("кг", "кг", "kg", "kg", "kg")));
        rows.Add((l.T("Основание:", "Негиз:", "Basis:", "Dayanak:", "Asos:"), Basis(d, l)));
        Parties(col, rows);

        // Таблица: № / Наименование / Штрихкод / Ед. изм. / Кол-во [/ Цена / Сумма]
        col.Item().PaddingTop(4).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(9, Unit.Millimetre);
                c.RelativeColumn(6);
                c.ConstantColumn(31, Unit.Millimetre);
                c.ConstantColumn(14, Unit.Millimetre);
                c.ConstantColumn(19, Unit.Millimetre);
                if (d.ShowPrices)
                {
                    c.ConstantColumn(22, Unit.Millimetre);
                    c.ConstantColumn(26, Unit.Millimetre);
                }
            });
            t.Header(h =>
            {
                h.Cell().Element(Head).AlignCenter().Text("№");
                h.Cell().Element(Head).Text(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"));
                h.Cell().Element(Head).Text(l.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"));
                h.Cell().Element(Head).AlignCenter().Text(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"));
                if (d.ShowPrices)
                {
                    h.Cell().Element(Head).AlignRight().Text(l.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"));
                    h.Cell().Element(Head).AlignRight().Text(l.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"));
                }
            });

            var i = 0;
            foreach (var line in d.Lines)
            {
                i++;
                t.Cell().Element(Cell).AlignCenter().Text(i.ToString(CultureInfo.InvariantCulture));
                t.Cell().Element(Cell).Text(line.Name.Length == 0 ? "—" : line.Name);
                t.Cell().Element(Cell).Text(line.Barcode.Length == 0 ? line.Article : line.Barcode);
                t.Cell().Element(Cell).AlignCenter().Text(line.Unit);
                t.Cell().Element(Cell).AlignRight().Text(l.Qty(line.Qty));
                if (d.ShowPrices)
                {
                    t.Cell().Element(Cell).AlignRight().Text(line.Price > 0 ? l.Money(line.Price) : "—");
                    t.Cell().Element(Cell).AlignRight().Text(line.Price > 0 ? l.Money(line.Sum) : "—");
                }
            }
            if (d.Lines.Count == 0)
                t.Cell().ColumnSpan((uint)(d.ShowPrices ? 7 : 5)).Element(Cell).AlignCenter()
                    .Text(l.T("Товары не добавлены", "Товарлар кошула элек", "No products added", "Ürün eklenmedi", "Mahsulot qo'shilmagan")).FontColor(Soft);
            else
            {
                // Итоговая строка: число позиций и общее количество (и сумма — при закупочных ценах).
                t.Cell().ColumnSpan(4).Element(Total).AlignRight().Text(l.T("Итого:", "Жыйынтык:", "Total:", "Toplam:", "Jami:"));
                t.Cell().Element(Total).AlignRight().Text(l.Qty(d.TotalQty));
                if (d.ShowPrices)
                {
                    t.Cell().Element(Total).Text("");
                    t.Cell().Element(Total).AlignRight().Text(l.Money(d.TotalSum));
                }
            }
        });

        col.Item().ShowEntire().Column(end =>
        {
            end.Spacing(6);
            end.Item().Text(d.ShowPrices
                ? l.T($"Всего наименований {d.Lines.Count}, общее количество {l.Qty(d.TotalQty)}, на сумму {l.Money(d.TotalSum)} {l.Currency}.",
                    $"Бардыгы {d.Lines.Count} аталыш, жалпы саны {l.Qty(d.TotalQty)}, суммасы {l.Money(d.TotalSum)} {l.Currency}.",
                    $"Total items: {d.Lines.Count}, total quantity: {l.Qty(d.TotalQty)}, amount: {l.Money(d.TotalSum)} {l.Currency}.",
                    $"Toplam kalem: {d.Lines.Count}, toplam miktar: {l.Qty(d.TotalQty)}, tutar: {l.Money(d.TotalSum)} {l.Currency}.",
                    $"Jami pozitsiyalar: {d.Lines.Count}, umumiy miqdor: {l.Qty(d.TotalQty)}, summa: {l.Money(d.TotalSum)} {l.Currency}.")
                : l.T($"Всего наименований {d.Lines.Count}, общее количество {l.Qty(d.TotalQty)}.",
                    $"Бардыгы {d.Lines.Count} аталыш, жалпы саны {l.Qty(d.TotalQty)}.",
                    $"Total items: {d.Lines.Count}, total quantity: {l.Qty(d.TotalQty)}.",
                    $"Toplam kalem: {d.Lines.Count}, toplam miktar: {l.Qty(d.TotalQty)}.",
                    $"Jami pozitsiyalar: {d.Lines.Count}, umumiy miqdor: {l.Qty(d.TotalQty)}.")).FontSize(9);
            if (d.ShowPrices)
                end.Item().Text(AmountInWords(d.TotalSum, l)).Bold();
            end.Item().LineHorizontal(1f).LineColor(Ink);
            Signatures(end, l,
                (l.T("Отпустил", "Берди", "Released by", "Teslim eden", "Topshirdi"), d.Responsible, true),
                (l.T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), d.Carrier, false),
                (l.T("Принял", "Алды", "Received by", "Teslim alan", "Qabul qildi"), "", true));
        });
    }

    // ───────────────────────────── акт приёма-передачи ─────────────────────────────

    private static void ActBody(ColumnDescriptor col, TransferDocData d, L l)
    {
        col.Item().Text(l.T(
            $"Настоящий акт составлен о том, что «{Fill(d.From)}» передал, а «{Fill(d.To)}» принял товар по перемещению № {d.TransferNumber} в количестве {d.Lines.Count} наименований, всего {l.Qty(d.TotalQty)} ед.",
            $"Бул акт «{Fill(d.From)}» берген, «{Fill(d.To)}» болсо {d.TransferNumber}-жылдыруу боюнча товарды ({d.Lines.Count} аталыш, бардыгы {l.Qty(d.TotalQty)} бирдик) кабыл алгандыгы жөнүндө түзүлдү.",
            $"This certificate confirms that “{Fill(d.From)}” handed over and “{Fill(d.To)}” received the goods under stock transfer No. {d.TransferNumber}: {d.Lines.Count} items, {l.Qty(d.TotalQty)} units in total.",
            $"Bu tutanak, «{Fill(d.From)}» tarafından {d.TransferNumber} numaralı transfer kapsamındaki malların ({d.Lines.Count} kalem, toplam {l.Qty(d.TotalQty)} adet) «{Fill(d.To)}» tarafından teslim alındığını belgelemek için düzenlenmiştir.",
            $"Ushbu dalolatnoma «{Fill(d.From)}» {d.TransferNumber}-sonli ko'chirish bo'yicha tovarni ({d.Lines.Count} pozitsiya, jami {l.Qty(d.TotalQty)} birlik) topshirgani, «{Fill(d.To)}» esa qabul qilgani haqida tuzildi."));

        var rows = new List<(string, string)>
        {
            (l.T("Передал:", "Берди:", "Handed over by:", "Teslim eden:", "Topshirdi:"), Place(d.OrgName, d.From)),
            (l.T("Принял:", "Алды:", "Received by:", "Teslim alan:", "Qabul qildi:"), Place(d.OrgName, d.To)),
            (l.T("Ответственный:", "Жооптуу:", "Responsible:", "Sorumlu:", "Mas'ul:"), d.Responsible),
        };
        if (CarrierText(d).Length > 0)
            rows.Add((l.T("Перевозчик:", "Ташуучу:", "Carrier:", "Taşıyıcı:", "Tashuvchi:"), CarrierText(d)));
        Parties(col, rows);

        col.Item().PaddingTop(2).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(9, Unit.Millimetre);
                c.RelativeColumn(5);
                c.ConstantColumn(27, Unit.Millimetre);
                c.ConstantColumn(11, Unit.Millimetre);
                c.ConstantColumn(19, Unit.Millimetre);
                c.ConstantColumn(25, Unit.Millimetre);
                c.ConstantColumn(27, Unit.Millimetre);
            });
            t.Header(h =>
            {
                h.Cell().Element(Head).AlignCenter().Text("№");
                h.Cell().Element(Head).Text(l.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"));
                h.Cell().Element(Head).Text(l.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"));
                h.Cell().Element(Head).AlignCenter().Text(l.T("Ед.", "Бирд.", "Unit", "Birim", "Birlik"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Передано", "Берилди", "Handed over", "Teslim edilen", "Topshirildi"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Принято фактически", "Иш жүзүндө алынды", "Actually received", "Fiilen alınan", "Haqiqatda qabul qilindi"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Расхождение", "Айырма", "Difference", "Fark", "Farq"));
            });
            var i = 0;
            foreach (var line in d.Lines)
            {
                i++;
                t.Cell().Element(Cell).AlignCenter().Text(i.ToString(CultureInfo.InvariantCulture));
                t.Cell().Element(Cell).Text(line.Name.Length == 0 ? "—" : line.Name);
                t.Cell().Element(Cell).Text(line.Barcode.Length == 0 ? line.Article : line.Barcode);
                t.Cell().Element(Cell).AlignCenter().Text(line.Unit);
                t.Cell().Element(Cell).AlignRight().Text(l.Qty(line.Qty));
                // «Принято фактически» и «Расхождение» заполняют от руки на приёмке.
                t.Cell().Element(Cell).Text("");
                t.Cell().Element(Cell).Text("");
            }
            if (d.Lines.Count == 0)
                t.Cell().ColumnSpan(7).Element(Cell).AlignCenter()
                    .Text(l.T("Товары не добавлены", "Товарлар кошула элек", "No products added", "Ürün eklenmedi", "Mahsulot qo'shilmagan")).FontColor(Soft);
            else
            {
                t.Cell().ColumnSpan(4).Element(Total).AlignRight().Text(l.T("Итого:", "Жыйынтык:", "Total:", "Toplam:", "Jami:"));
                t.Cell().Element(Total).AlignRight().Text(l.Qty(d.TotalQty));
                t.Cell().Element(Total).Text("");
                t.Cell().Element(Total).Text("");
            }
        });

        col.Item().ShowEntire().Column(end =>
        {
            end.Spacing(7);
            end.Item().Text(t =>
            {
                t.Span(l.T("Состояние груза: ", "Жүктүн абалы: ", "Condition of the cargo: ", "Yükün durumu: ", "Yukning holati: ")).SemiBold();
                t.Span(l.T("[   ] без замечаний     [   ] есть повреждения     [   ] есть расхождения по количеству",
                    "[   ] эскертүүсүз     [   ] бузулуулар бар     [   ] саны боюнча айырма бар",
                    "[   ] no remarks     [   ] damaged     [   ] quantity differences",
                    "[   ] sorunsuz     [   ] hasarlı     [   ] miktar farkı var",
                    "[   ] e'tirozsiz     [   ] shikastlangan     [   ] miqdorda farq bor"));
            });
            end.Item().Text(l.T("Замечания:", "Эскертүүлөр:", "Remarks:", "Notlar:", "Izohlar:")).SemiBold();
            for (var i = 0; i < 3; i++)
                end.Item().PaddingTop(10).LineHorizontal(0.6f).LineColor(Ink);
            end.Item().LineHorizontal(1f).LineColor(Ink);
            Signatures(end, l,
                (l.T("Передал", "Берди", "Handed over", "Teslim eden", "Topshirdi"), d.Responsible, true),
                (l.T("Принял", "Алды", "Received", "Teslim alan", "Qabul qildi"), "", true));
        });
    }

    // ───────────────────────────── транспортный документ ─────────────────────────────

    private static void TransportBody(ColumnDescriptor col, TransferDocData d, L l)
    {
        string kg = l.T("кг", "кг", "kg", "kg", "kg");
        var fields = new List<(string Label, string Value)>
        {
            (l.T("Грузоотправитель", "Жүк жөнөтүүчү", "Consignor", "Gönderici", "Yuk jo'natuvchi"), Place(d.OrgName, d.From)),
            (l.T("Грузополучатель", "Жүк алуучу", "Consignee", "Alıcı", "Yuk qabul qiluvchi"), Place(d.OrgName, d.To)),
            (l.T("Пункт отправления", "Жөнөтүү пункту", "Point of departure", "Çıkış noktası", "Jo'natish punkti"), d.From),
            (l.T("Пункт назначения", "Жеткирүү пункту", "Destination", "Varış noktası", "Manzil punkti"), d.To),
            (l.T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), d.Carrier),
            (l.T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), d.Tracking),
            (l.T("Водитель (Ф.И.О.)", "Айдоочу (аты-жөнү)", "Driver (full name)", "Sürücü (ad soyad)", "Haydovchi (F.I.Sh.)"), ""),
            (l.T("Автомобиль, гос. номер", "Автоунаа, мамлекеттик номери", "Vehicle, plate number", "Araç, plaka", "Avtomobil, davlat raqami"), ""),
            (l.T("Количество мест", "Орундардын саны", "Number of packages", "Koli sayısı", "Joylar soni"), ""),
            (l.T("Вес груза, кг", "Жүктүн салмагы, кг", "Cargo weight, kg", "Yük ağırlığı, kg", "Yuk og'irligi, kg"), d.Weight > 0 ? l.Qty(d.Weight) + " " + kg : ""),
            (l.T("Дата и время погрузки", "Жүктөө күнү жана убактысы", "Loading date and time", "Yükleme tarihi ve saati", "Yuklash sanasi va vaqti"), ""),
            (l.T("Дата и время выгрузки", "Түшүрүү күнү жана убактысы", "Unloading date and time", "Boşaltma tarihi ve saati", "Tushirish sanasi va vaqti"), ""),
            (l.T("Объявленная стоимость", "Жарыяланган баасы", "Declared value", "Beyan edilen değer", "E'lon qilingan qiymat"), d.ShowPrices && d.TotalSum > 0 ? l.Money(d.TotalSum) + " " + l.Currency : ""),
            (l.T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), Basis(d, l)),
        };
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(1);
                c.RelativeColumn(1.5f);
                c.RelativeColumn(1);
                c.RelativeColumn(1.5f);
            });
            IContainer Lab(IContainer x) => x.Border(0.7f).BorderColor(Line).Background(TotalFill).Padding(4).DefaultTextStyle(s => s.FontSize(8.5f).SemiBold());
            IContainer Val(IContainer x) => x.Border(0.7f).BorderColor(Line).Padding(4).MinHeight(20);
            for (var i = 0; i < fields.Count; i += 2)
            {
                t.Cell().Element(Lab).Text(fields[i].Label);
                if (i == fields.Count - 1 || (fields.Count - i == 1))
                {
                    t.Cell().ColumnSpan(3).Element(Val).Text(fields[i].Value);
                    continue;
                }
                t.Cell().Element(Val).Text(fields[i].Value);
                t.Cell().Element(Lab).Text(fields[i + 1].Label);
                t.Cell().Element(Val).Text(fields[i + 1].Value);
            }
        });

        col.Item().PaddingTop(4).Text(l.T("Сведения о грузе", "Жүк жөнүндө маалымат", "Cargo details", "Yük bilgileri", "Yuk haqida ma'lumot")).SemiBold();
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(9, Unit.Millimetre);
                c.RelativeColumn(6);
                c.ConstantColumn(14, Unit.Millimetre);
                c.ConstantColumn(20, Unit.Millimetre);
                c.RelativeColumn(2.5f);
            });
            t.Header(h =>
            {
                h.Cell().Element(Head).AlignCenter().Text("№");
                h.Cell().Element(Head).Text(l.T("Наименование груза", "Жүктүн аталышы", "Cargo name", "Yük adı", "Yuk nomi"));
                h.Cell().Element(Head).AlignCenter().Text(l.T("Ед. изм.", "Бирд.", "Unit", "Birim", "Birlik"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"));
                h.Cell().Element(Head).Text(l.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"));
            });
            var i = 0;
            foreach (var line in d.Lines)
            {
                i++;
                t.Cell().Element(Cell).AlignCenter().Text(i.ToString(CultureInfo.InvariantCulture));
                t.Cell().Element(Cell).Text(line.Name.Length == 0 ? "—" : line.Name);
                t.Cell().Element(Cell).AlignCenter().Text(line.Unit);
                t.Cell().Element(Cell).AlignRight().Text(l.Qty(line.Qty));
                t.Cell().Element(Cell).Text("");
            }
            if (d.Lines.Count == 0)
                t.Cell().ColumnSpan(5).Element(Cell).AlignCenter()
                    .Text(l.T("Товары не добавлены", "Товарлар кошула элек", "No products added", "Ürün eklenmedi", "Mahsulot qo'shilmagan")).FontColor(Soft);
            else
            {
                t.Cell().ColumnSpan(3).Element(Total).AlignRight().Text(l.T("Итого:", "Жыйынтык:", "Total:", "Toplam:", "Jami:"));
                t.Cell().Element(Total).AlignRight().Text(l.Qty(d.TotalQty));
                t.Cell().Element(Total).Text("");
            }
        });

        col.Item().ShowEntire().Column(end =>
        {
            end.Spacing(6);
            end.Item().LineHorizontal(1f).LineColor(Ink);
            Signatures(end, l,
                (l.T("Грузоотправитель (отпустил)", "Жүк жөнөтүүчү (берди)", "Consignor (released)", "Gönderici (teslim etti)", "Yuk jo'natuvchi (topshirdi)"), d.Responsible, true),
                (l.T("Водитель (принял к перевозке)", "Айдоочу (ташууга алды)", "Driver (accepted for transport)", "Sürücü (taşımaya aldı)", "Haydovchi (tashishga oldi)"), "", false),
                (l.T("Грузополучатель (принял)", "Жүк алуучу (алды)", "Consignee (received)", "Alıcı (teslim aldı)", "Yuk qabul qiluvchi (qabul qildi)"), "", true));
        });
    }

    // ───────────────────────────── общие элементы ─────────────────────────────

    private static string Fill(string value) => string.IsNullOrWhiteSpace(value) ? "________________" : value;

    private static IContainer Head(IContainer x) =>
        x.Border(0.7f).BorderColor(Line).Background(HeadFill).PaddingVertical(4).PaddingHorizontal(3)
            .DefaultTextStyle(s => s.SemiBold().FontSize(8.5f));

    private static IContainer Cell(IContainer x) =>
        x.Border(0.7f).BorderColor(Line).PaddingVertical(3).PaddingHorizontal(3).MinHeight(18);

    private static IContainer Total(IContainer x) =>
        x.Border(0.7f).BorderColor(Line).Background(TotalFill).PaddingVertical(3).PaddingHorizontal(3).DefaultTextStyle(s => s.SemiBold());

    /// <summary>Подписи: «Отпустил: ________» с подписью-пояснением «(подпись, Ф.И.О.)» и квадратом «М.П.» (место под печать).</summary>
    private static void Signatures(ColumnDescriptor col, L l, params (string Role, string Name, bool Stamp)[] roles)
    {
        col.Item().PaddingTop(10).Row(row =>
        {
            for (var i = 0; i < roles.Length; i++)
            {
                if (i > 0)
                    row.ConstantItem(8, Unit.Millimetre);
                var (role, name, stamp) = roles[i];
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(role + ":").SemiBold();
                    c.Item().PaddingTop(14).BorderBottom(0.6f).BorderColor(Ink).Height(2);
                    c.Item().Text(string.IsNullOrWhiteSpace(name) ? l.T("(подпись, Ф.И.О.)", "(колу, аты-жөнү)", "(signature, full name)", "(imza, ad soyad)", "(imzo, F.I.Sh.)")
                        : "/" + name.Trim() + "/").FontSize(8).FontColor(Soft);
                    c.Item().PaddingTop(4).Text(l.T("Дата: ", "Күнү: ", "Date: ", "Tarih: ", "Sana: ") + "____.____.________").FontSize(8).FontColor(Soft);
                    if (stamp)
                        c.Item().PaddingTop(8).Width(26, Unit.Millimetre).Height(26, Unit.Millimetre).Border(0.6f).BorderColor(Soft)
                            .AlignMiddle().AlignCenter().Text(l.T("М.П.", "М.О.", "Seal", "Mühür", "M.O.")).FontSize(9).FontColor(Soft);
                });
            }
        });
    }
}
