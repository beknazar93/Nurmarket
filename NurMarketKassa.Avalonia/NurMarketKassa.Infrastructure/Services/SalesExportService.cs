namespace NurMarketKassa.Services;

/// <summary>2026-10-19, владелец: «при нажатии на Excel просто выведи все продажи, а не аналитику».
/// Кнопка «Excel» в «Продажах и финансах» и в «Продажах» выгружает все чеки периода: лист «Чеки» (чек — строка, как во
/// вкладке «Чеки») и лист «Товары» (каждая строка каждого чека). Аналитика в Excel остаётся в «Отчётах» (ABC), Word —
/// прежний (AnalyticsExportService). Данные собирает окно (SalesExportLoader в приложении), здесь — только книга.</summary>
public static class SalesExportService
{
    /// <summary>Строка чека: название, количество, единица, цена и сумма строки.</summary>
    public sealed record Line(string Name, double Quantity, string? Unit, double Price, double Sum);

    /// <summary>Чек списка. <paramref name="Lines"/> null — состав чека не получен (в «Товары» он не попадает).</summary>
    public sealed record Receipt(
        string Id,
        DateTime CreatedAt,
        string Number,
        double Total,
        double Discount,
        string PaymentMethod,
        string Status,
        string? Cashier,
        string? Client,
        string? FirstItem,
        IReadOnlyList<Line>? Lines);

    /// <param name="LocalOnly">Связи с сервером нет — чеки по данным этой кассы (проданные строки, как экран без связи).</param>
    /// <param name="Truncated">Список упёрся в предел страниц (50 000 чеков), как у экрана.</param>
    /// <param name="LinesSkipped">Состав скольких чеков не запрашивался (слишком много чеков с других касс).</param>
    /// <param name="LinesFailed">Состав скольких чеков сервер не отдал.</param>
    /// <param name="ServerRevenue">Выручка сводки сервера — та, что на плитке «Выручка»; null — сводки нет.</param>
    public sealed record Data(
        DateTime From,
        DateTime To,
        IReadOnlyList<Receipt> Receipts,
        bool LocalOnly,
        bool Truncated = false,
        int LinesSkipped = 0,
        int LinesFailed = 0,
        double? ServerRevenue = null);

    // ------------------------------------------------------------------ подписи

    /// <summary>Название книги и файла: «Продажи 01.10.2026-19.10.2026 Магазин.xlsx».</summary>
    public static string FileTitle => Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar");

    public static string SuggestFileName(DateTime from, DateTime to, string? shopName) =>
        ExportFileName.Build(FileTitle, ExportFileName.Period(from, to), shopName, "xlsx");

    private static string Currency => ReportLabels.Currency;
    private static string SheetReceipts => Tr.T("Чеки", "Чектер", "Receipts", "Fişler", "Cheklar");
    private static string SheetLines => Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar");
    private static string TitleReceipts => Tr.T("Продажи: все чеки за период", "Сатуулар: мезгилдеги бардык чектер", "Sales: all receipts for the period", "Satışlar: dönemin tüm fişleri", "Sotuvlar: davrdagi barcha cheklar");
    private static string TitleLines => Tr.T("Продажи: товары в чеках", "Сатуулар: чектердеги товарлар", "Sales: products in receipts", "Satışlar: fişlerdeki ürünler", "Sotuvlar: cheklardagi mahsulotlar");
    private static string LDate => Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana");
    private static string LTime => Tr.T("Время", "Убактысы", "Time", "Saat", "Vaqt");
    private static string LReceiptNo => Tr.T("Номер чека", "Чектин номери", "Receipt no.", "Fiş no.", "Chek raqami");
    private static string LItems => Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar");
    private static string LProduct => Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");
    private static string LAmount => Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa");
    private static string LDiscount => Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma");
    private static string LPayment => Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov");
    private static string LStatus => Tr.T("Статус", "Абалы", "Status", "Durum", "Holat");
    private static string LClient => Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz");
    private static string LQuantity => Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
    private static string LUnit => Tr.T("Ед.", "Бирдик", "Unit", "Birim", "Birlik");
    private static string LPrice => Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx");
    private static string LSummary => Tr.T("Итоги за период", "Мезгилдин жыйынтыгы", "Period totals", "Dönem toplamları", "Davr yakunlari");
    private static string LIndicator => Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich");
    private static string LValue => Tr.T("Значение", "Мааниси", "Value", "Değer", "Qiymati");
    private static string LReceiptsInList => Tr.T("Чеков в списке", "Тизмедеги чектер", "Receipts in the list", "Listedeki fişler", "Ro'yxatdagi cheklar");
    private static string LRevenue => Tr.T("Выручка (как плитка «Выручка»)", "Түшүм («Түшүм» плиткасындай)", "Revenue (as on the «Revenue» tile)", "Ciro («Ciro» kutucuğundaki gibi)", "Tushum («Tushum» plitkasidagidek)");
    private static string LRevenueReceipts => Tr.T("Чеков в выручке", "Түшүмдөгү чектер", "Receipts in revenue", "Ciroya giren fişler", "Tushumdagi cheklar");
    private static string LNotRevenue => Tr.T("Не входят в выручку (в долг, не оплачены)", "Түшүмгө кирбейт (карызга, төлөнгөн эмес)", "Not in revenue (on credit, unpaid)", "Ciroya dahil değil (veresiye, ödenmemiş)", "Tushumga kirmaydi (qarzga, to'lanmagan)");
    private static string LNotRevenueReceipts => Tr.T("Таких чеков", "Мындай чектер", "Such receipts", "Bu tür fişler", "Bunday cheklar");
    private static string LDiscounts => Tr.T("Скидки в чеках выручки", "Түшүмдөгү чектердин арзандатуулары", "Discounts in revenue receipts", "Ciro fişlerindeki indirimler", "Tushum cheklaridagi chegirmalar");
    private static string LServerRevenue => Tr.T("Выручка по сводке сервера", "Сервердин жыйынтыгы боюнча түшүм", "Revenue per server summary", "Sunucu özetine göre ciro", "Server xulosasi bo'yicha tushum");

    private static string NoteServer => Tr.T(
        "Все чеки периода, как во вкладке «Чеки». «Итого» в таблице — сумма всех чеков списка; выручка — под таблицей. Полностью возвращённые чеки сервер отменяет — их здесь нет (они во вкладке «Возвраты»).",
        "Мезгилдеги бардык чектер, «Чектер» өтмөгүндөгүдөй. Таблицадагы «Жыйынтыгы» — тизмедеги бардык чектердин суммасы; түшүм — таблицанын астында. Толук кайтарылган чектерди сервер жокко чыгарат — алар бул жерде жок («Кайтаруулар» өтмөгүндө).",
        "All receipts for the period, as on the «Receipts» tab. «Total» in the table is the sum of all listed receipts; revenue is below the table. Fully returned receipts are canceled by the server and are not listed here (see the «Returns» tab).",
        "Dönemin tüm fişleri, «Fişler» sekmesindeki gibi. Tablodaki «Toplam» listedeki tüm fişlerin tutarıdır; ciro tablonun altındadır. Tamamen iade edilen fişleri sunucu iptal eder — burada yoktur («İadeler» sekmesinde).",
        "Davrdagi barcha cheklar, «Cheklar» bo'limidagidek. Jadvaldagi «Jami» — ro'yxatdagi barcha cheklar summasi; tushum — jadval ostida. To'liq qaytarilgan cheklarni server bekor qiladi — ular bu yerda yo'q («Qaytarishlar» bo'limida).");

    private static string NoteDebt => Tr.T(
        " Продажи в долг до оплаты в выручку не входят — как на плитке «Выручка».",
        " Карызга сатуулар төлөнгөнгө чейин түшүмгө кирбейт — «Түшүм» плиткасындагыдай.",
        " Sales on credit are not in revenue until paid — as on the «Revenue» tile.",
        " Veresiye satışlar ödenene kadar ciroya dahil edilmez — «Ciro» kutucuğundaki gibi.",
        " Qarzga sotuvlar to'languncha tushumga kirmaydi — «Tushum» plitkasidagidek.");

    private static string NoteLocal => Tr.T(
        "По данным этой кассы: связи с сервером нет. Способа оплаты, скидок, кассира и возвратов здесь нет — они хранятся на сервере.",
        "Ушул кассанын маалыматтары боюнча: сервер менен байланыш жок. Төлөм түрү, арзандатуулар, кассир жана кайтаруулар бул жерде жок — алар серверде сакталат.",
        "Based on this till's data: no connection to the server. Payment method, discounts, cashier and returns are not here — they are stored on the server.",
        "Bu kasanın verilerine göre: sunucuyla bağlantı yok. Ödeme yöntemi, indirimler, kasiyer ve iadeler burada yok — sunucuda saklanır.",
        "Shu kassaning ma'lumotlari bo'yicha: server bilan aloqa yo'q. To'lov turi, chegirmalar, kassir va qaytarishlar bu yerda yo'q — ular serverda saqlanadi.");

    private static string NoteTruncated => Tr.T(
        " Период очень большой: в списке — последние 50 000 чеков.",
        " Мезгил өтө чоң: тизмеде — акыркы 50 000 чек.",
        " The period is very large: the list has the latest 50,000 receipts.",
        " Dönem çok büyük: listede son 50.000 fiş var.",
        " Davr juda katta: ro'yxatda — oxirgi 50 000 ta chek.");

    private static string NoteLines => Tr.T(
        "Каждая строка каждого чека. Сумма строки — как в чеке; скидка на весь чек — на листе «Чеки», поэтому итог здесь может немного отличаться от выручки.",
        "Ар бир чектин ар бир сабы. Саптын суммасы — чектегидей; бүт чекке арзандатуу — «Чектер» барагында, ошондуктан бул жердеги жыйынтык түшүмдөн бир аз айырмаланышы мүмкүн.",
        "Every line of every receipt. The line amount is as on the receipt; a discount on the whole receipt is on the «Receipts» sheet, so the total here may differ slightly from revenue.",
        "Her fişin her satırı. Satır tutarı fişteki gibidir; tüm fişe uygulanan indirim «Fişler» sayfasındadır, bu yüzden buradaki toplam cirodan biraz farklı olabilir.",
        "Har bir chekning har bir qatori. Qator summasi — chekdagidek; butun chekka chegirma — «Cheklar» varag'ida, shuning uchun bu yerdagi jami tushumdan biroz farq qilishi mumkin.");

    private static string NoteLinesSkipped(int count) => Tr.T(
        $" Состав {count} чеков не выгружен: за период слишком много чеков с других касс — выберите период короче.",
        $" {count} чектин курамы чыгарылган жок: мезгилде башка кассалардан чектер өтө көп — кыскараак мезгилди тандаңыз.",
        $" The contents of {count} receipts were not exported: too many receipts from other tills for the period — choose a shorter period.",
        $" {count} fişin içeriği aktarılmadı: dönemde diğer kasalardan çok fazla fiş var — daha kısa bir dönem seçin.",
        $" {count} ta chek tarkibi chiqarilmadi: davrda boshqa kassalardan cheklar juda ko'p — qisqaroq davrni tanlang.");

    private static string NoteLinesFailed(int count) => Tr.T(
        $" Состав {count} чеков сервер не отдал.",
        $" {count} чектин курамын сервер берген жок.",
        $" The server did not return the contents of {count} receipts.",
        $" Sunucu {count} fişin içeriğini vermedi.",
        $" Server {count} ta chek tarkibini bermadi.");

    /// <summary>Способ оплаты словами — как в колонке «Оплата» таблицы чеков (SalesWindow.NormalizePaymentMethodLabel).</summary>
    public static string PaymentLabel(string? method)
    {
        var key = (method ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "" => Tr.T("Не указано", "Көрсөтүлгөн эмес", "Not specified", "Belirtilmemiş", "Ko'rsatilmagan"),
            "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
            // 2026-10-27, аудит владельца Н-1: «Безналичные», как в «Истории» (FinanceWindow) и в окне оплаты; было «Перевод» — один и тот же
            // чек 001899 назывался по-разному на вкладках «Чеки» и «История».
            "transfer" or "card" => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
            "mbank" => "MBank",
            "mixed" => Tr.T("Смешанный", "Аралаш", "Mixed", "Karışık", "Aralash"),
            "debt" => Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"),
            "offset" => Tr.T("Зачёт (обмен, предоплата)", "Эсепке алуу (алмаштыруу, алдын ала төлөм)", "Offset (exchange, prepayment)", "Mahsup (değişim, ön ödeme)", "Hisobga olish (almashtirish, oldindan to'lov)"),
            _ when key.Contains("нал") => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
            _ => char.ToUpperInvariant(method!.Trim()[0]) + method.Trim()[1..],
        };
    }

    /// <summary>Статус чека словами (как в «Истории чеков», ReceiptHistoryService.StatusLabel).</summary>
    public static string StatusLabel(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        "partially_returned" => Tr.T("Частичный возврат", "Жарым-жартылай кайтарылды", "Partially returned", "Kısmi iade", "Qisman qaytarilgan"),
        "" or "paid" or "completed" or "closed" or "checked_out" => Tr.T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'langan"),
        // Незнакомый статус — как есть, а не «оплачен».
        _ => status!.Trim(),
    };

    /// <summary>Входит ли чек в выручку — как плитка «Выручка» и сайт (SaleItem.CountsAsRevenue окон): оплаченные и
    /// частично возвращённые; продажа в долг до оплаты — нет.</summary>
    public static bool CountsAsRevenue(string? status)
    {
        var s = (status ?? "").Trim();
        return s.Length == 0
               || string.Equals(s, "paid", StringComparison.OrdinalIgnoreCase)
               || string.Equals(s, "partially_returned", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Товары чека одной строкой, как в таблице «Чеки»: «Хлеб, Молоко ×2».</summary>
    public static string ItemsSummary(Receipt r)
    {
        if (r.Lines is { Count: > 0 } lines)
        {
            return string.Join(", ", lines.Select(l => Math.Abs(l.Quantity - 1) < 0.0000001
                ? l.Name
                : $"{l.Name} ×{l.Quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
        }

        return r.FirstItem ?? "";
    }

    private static string Period(DateTime from, DateTime to) =>
        from.Date == to.Date ? ReportFormat.Date(from) : $"{ReportFormat.Date(from)} — {ReportFormat.Date(to)}";

    private static string Time(DateTime at) => at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ Excel

    public static void ExportToExcel(string path, Data data, string? shopName)
    {
        var receipts = data.Receipts.OrderBy(r => r.CreatedAt).ToList();
        var meta = ReportMeta.Create(FileTitle, shopName, Period(data.From, data.To));
        using var book = new ExcelWorkbookBuilder(path, meta);

        // Колонки, которых в данных нет (без связи — скидки, кассир, оплата), не выводим пустыми.
        var hasDiscount = !data.LocalOnly;
        var hasPayment = !data.LocalOnly;
        var hasCashier = receipts.Any(r => !string.IsNullOrWhiteSpace(r.Cashier));
        var hasClient = receipts.Any(r => !string.IsNullOrWhiteSpace(r.Client));
        var notRevenue = receipts.Where(r => !CountsAsRevenue(r.Status)).ToList();

        var note = data.LocalOnly ? NoteLocal : NoteServer + (notRevenue.Count > 0 ? NoteDebt : "");
        if (data.Truncated)
            note += NoteTruncated;

        // ---- Чеки
        var columns = new List<(XlColumn Column, Func<Receipt, int, object?> Value)>
        {
            (new XlColumn(ReportLabels.Number, XlKind.Index), (_, i) => i + 1),
            (new XlColumn(LDate, XlKind.Date), (r, _) => r.CreatedAt),
            (new XlColumn(LTime), (r, _) => Time(r.CreatedAt)),
            (new XlColumn(LReceiptNo, XlKind.Code), (r, _) => r.Number),
            (new XlColumn(LItems, MaxWidth: 60), (r, _) => ItemsSummary(r)),
            (new XlColumn(ReportLabels.WithUnit(LAmount, Currency), XlKind.Money, XlTotal.Sum), (r, _) => r.Total),
        };
        if (hasDiscount)
            columns.Add((new XlColumn(ReportLabels.WithUnit(LDiscount, Currency), XlKind.Money, XlTotal.Sum), (r, _) => r.Discount));
        if (hasPayment)
            columns.Add((new XlColumn(LPayment), (r, _) => PaymentLabel(r.PaymentMethod)));
        if (!data.LocalOnly)
        {
            columns.Add((new XlColumn(LStatus), (r, _) => CountsAsRevenue(r.Status)
                ? (object?)StatusLabel(r.Status)
                : new XlCell(StatusLabel(r.Status), ReportTone.Warn)));
        }
        if (hasCashier)
            columns.Add((new XlColumn(ReportLabels.Cashier), (r, _) => r.Cashier));
        if (hasClient)
            columns.Add((new XlColumn(LClient), (r, _) => r.Client));

        var sheet = book.AddSheet(SheetReceipts, TitleReceipts, note, landscape: true);
        sheet.AddTable(columns.Select(c => c.Column).ToList(),
            receipts.Select((r, i) => columns.Select(c => c.Value(r, i)).ToArray()));

        // Итоги под таблицей: выручка — ровно как плитка «Выручка» (чеки, входящие в выручку).
        var revenueReceipts = receipts.Where(r => CountsAsRevenue(r.Status)).ToList();
        var revenue = revenueReceipts.Sum(r => r.Total);
        var totals = new List<XlKeyValue>
        {
            new(LReceiptsInList, receipts.Count, XlKind.Integer),
            new(ReportLabels.WithUnit(LRevenue, Currency), revenue, XlKind.Money),
            new(LRevenueReceipts, revenueReceipts.Count, XlKind.Integer),
        };
        if (notRevenue.Count > 0)
        {
            totals.Add(new XlKeyValue(ReportLabels.WithUnit(LNotRevenue, Currency), notRevenue.Sum(r => r.Total), XlKind.Money, ReportTone.Warn));
            totals.Add(new XlKeyValue(LNotRevenueReceipts, notRevenue.Count, XlKind.Integer));
        }
        if (hasDiscount)
            totals.Add(new XlKeyValue(ReportLabels.WithUnit(LDiscounts, Currency), revenueReceipts.Sum(r => r.Discount), XlKind.Money));
        // Сводка сервера разошлась со списком (например, список обрезан) — показываем и её, а не молча одну из двух.
        if (data.ServerRevenue is { } server && Math.Abs(server - revenue) >= 0.01)
            totals.Add(new XlKeyValue(ReportLabels.WithUnit(LServerRevenue, Currency), server, XlKind.Money, ReportTone.Info));
        sheet.AddHeading(LSummary);
        sheet.AddKeyValues(totals, LIndicator, LValue);

        // ---- Товары
        var lineNote = NoteLines;
        if (data.LinesSkipped > 0)
            lineNote += NoteLinesSkipped(data.LinesSkipped);
        if (data.LinesFailed > 0)
            lineNote += NoteLinesFailed(data.LinesFailed);
        if (data.LocalOnly)
            lineNote = NoteLocal + " " + lineNote;

        var lineColumns = new List<XlColumn>
        {
            new(LDate, XlKind.Date),
            new(LTime),
            new(LReceiptNo, XlKind.Code),
            new(LProduct, MaxWidth: 50),
            new(LQuantity, XlKind.Quantity),
            new(LUnit),
            new(ReportLabels.WithUnit(LPrice, Currency), XlKind.Money),
            new(ReportLabels.WithUnit(LAmount, Currency), XlKind.Money, XlTotal.Sum),
        };
        if (hasCashier)
            lineColumns.Add(new XlColumn(ReportLabels.Cashier));

        var lineRows = receipts
            .Where(r => r.Lines is { Count: > 0 })
            .SelectMany(r => r.Lines!.Select(l =>
            {
                var row = new List<object?> { r.CreatedAt, Time(r.CreatedAt), r.Number, l.Name, l.Quantity, l.Unit, l.Price, l.Sum };
                if (hasCashier)
                    row.Add(r.Cashier);
                return row.ToArray();
            }));

        var linesSheet = book.AddSheet(SheetLines, TitleLines, lineNote, landscape: true);
        linesSheet.AddTable(lineColumns, lineRows);

        book.Save();
    }
}
