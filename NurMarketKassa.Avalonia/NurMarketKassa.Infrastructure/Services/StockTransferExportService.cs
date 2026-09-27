using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

/// <summary>Выгрузка перемещений в Excel (.xlsx) и Word (.docx).
///
/// Файлы пишутся напрямую через DocumentFormat.OpenXml — так же, как отчёты аналитики: на кассе
/// офисных программ обычно нет, а накладную распечатать надо. Excel нужен для проверки и
/// пересчёта, Word — чтобы отдать бумагу водителю и получить подпись на приёмке.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StockTransferExportService
{
    // ------------------------------------------------------------------ подписи

    private static string LDocument => Label("Документ", "Документ", "Document", "Belge", "Hujjat");
    private static string LCreated => Label("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi");
    private static string LStatus => Label("Статус", "Абалы", "Status", "Durum", "Holat");
    private static string LFrom => Label("Откуда", "Кайдан", "From", "Nereden", "Qayerdan");
    private static string LTo => Label("Куда", "Кайда", "To", "Nereye", "Qayerga");
    private static string LResponsible => Label("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul");
    private static string LCarrier => Label("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi");
    private static string LTracking => Label("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami");
    private static string LWeight => Label("Вес партии", "Партиянын салмагы", "Batch weight", "Parti ağırlığı", "Partiya og'irligi");
    private static string LNote => Label("Примечание", "Эскертүү", "Note", "Not", "Izoh");
    private static string LName => Label("Название", "Аты", "Name", "Ad", "Nomi");
    private static string LCode => Label("Код", "Коду", "Code", "Kod", "Kod");
    private static string LBarcode => Label("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod");
    private static string LQuantity => Label("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
    private static string LUnit => Label("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik");
    private static string LWhen => Label("Когда", "Качан", "When", "Ne zaman", "Qachon");
    private static string LEmployee => Label("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim");
    private static string LNumber => Label("Номер", "Номери", "Number", "Numara", "Raqam");
    private static string LItems => Label("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar");
    private static string LUnits => Label("Всего единиц", "Жалпы бирдик", "Total units", "Toplam adet", "Jami birlik");
    private static string LUnitsShort => Label("Единиц", "Бирдик", "Units", "Adet", "Birlik");
    private static string LRoute => Label("Маршрут", "Багыты", "Route", "Güzergah", "Yo'nalish");

    private static string WaybillTitle(string number) =>
        Label("Накладная на перемещение", "Жылдыруу боюнча накладной", "Stock transfer waybill", "Stok transfer irsaliyesi", "Ko'chirish yuk xati") + " № " + number;

    private static string TransferTitle(string number) =>
        Label("Перемещение товара", "Товардын жылышуусу", "Stock transfer", "Stok transferi", "Tovar ko'chirish") + " № " + number;

    private static string JournalTitle =>
        Label("Журнал перемещений", "Жылышуулар журналы", "Transfer journal", "Transfer günlüğü", "Ko'chirishlar jurnali");

    /// <summary>Имя файла одного перемещения: «Перемещение № 12 28.09.2026 Магазин.xlsx».</summary>
    public static string SuggestTransferFileName(StockTransferService.Transfer transfer, string? shopName, string extension) =>
        ExportFileName.Build(
            Label("Перемещение", "Жылышуу", "Transfer", "Transfer", "Ko'chirish") + " № " + transfer.Number,
            ReportFormat.Date(transfer.CreatedAt), shopName, extension);

    /// <summary>Имя файла журнала: «Журнал перемещений 28.09.2026 Магазин.xlsx».</summary>
    public static string SuggestJournalFileName(string? shopName, string extension) =>
        ExportFileName.Build(JournalTitle, ReportFormat.Date(DateTime.Now), shopName, extension);

    /// <summary>Цвет статуса — тот же смысл, что и у плашек на экране склада.</summary>
    private static ReportTone StatusTone(string status) => status switch
    {
        StockTransferService.StatusDelivered => ReportTone.Good,
        StockTransferService.StatusInTransit => ReportTone.Warn,
        StockTransferService.StatusCancelled => ReportTone.Muted,
        _ => ReportTone.Info,
    };

    /// <summary>Вес партии вписывают руками; ноль значит «не указан», и в накладной это должно
    /// читаться как прочерк, а не как груз без веса.</summary>
    private static string WeightText(double weight) =>
        weight > 0 ? ReportFormat.Quantity(weight) + " " + ReportLabels.Kg : "—";

    // ------------------------------------------------------------------ один документ

    /// <summary>Один документ перемещения: шапка с маршрутом, состав партии и хронология.</summary>
    public static void ExportTransferToExcel(
        string path,
        StockTransferService.Transfer transfer,
        IReadOnlyList<StockTransferService.TransferItem> items,
        IReadOnlyList<StockTransferService.LogEntry> log,
        string statusText,
        Func<string, string> describeStatus,
        string? shopName)
    {
        // Период у документа не нужен: дата создания стоит в самой карточке.
        var meta = ReportMeta.Create(TransferTitle(transfer.Number), shopName, null);
        using var book = new ExcelWorkbookBuilder(path, meta);

        var header = book.AddSheet(Label("Документ", "Документ", "Document", "Belge", "Hujjat"), meta.Title);
        var fields = new List<XlKeyValue>
        {
            new(LDocument, transfer.Number),
            new(LCreated, transfer.CreatedAt, XlKind.DateTime),
            new(LStatus, statusText, Tone: StatusTone(transfer.Status)),
            new(LFrom, Dash(transfer.FromPlaceName)),
            new(LTo, Dash(transfer.ToPlaceName)),
            new(LResponsible, Dash(transfer.Responsible)),
            new(LCarrier, Dash(transfer.Carrier)),
            new(LTracking, Dash(transfer.TrackingNumber), XlKind.Code),
            transfer.TotalWeight > 0
                ? new XlKeyValue(ReportLabels.WithUnit(LWeight, ReportLabels.Kg), transfer.TotalWeight, XlKind.Quantity)
                : new XlKeyValue(ReportLabels.WithUnit(LWeight, ReportLabels.Kg), "—"),
            new(LItems, items.Count, XlKind.Integer),
            new(LUnits, items.Sum(i => i.Quantity), XlKind.Quantity),
        };
        if (!string.IsNullOrWhiteSpace(transfer.Note))
            fields.Add(new XlKeyValue(LNote, transfer.Note));
        header.AddKeyValues(fields);

        var hasItemWeight = items.Any(i => i.Weight > 0);
        var contents = book.AddSheet(Label("Состав", "Курамы", "Contents", "İçerik", "Tarkibi"), meta.Title);
        var columns = new List<XlColumn>
        {
            new(ReportLabels.Number, XlKind.Index),
            new(LName, MaxWidth: 50),
            new(LCode, XlKind.Code),
            new(LBarcode, XlKind.Code),
            new(LQuantity, XlKind.Quantity, XlTotal.Sum),
            new(LUnit),
        };
        if (hasItemWeight)
            columns.Add(new XlColumn(ReportLabels.WithUnit(Label("Вес", "Салмагы", "Weight", "Ağırlık", "Og'irlik"), ReportLabels.Kg), XlKind.Quantity, XlTotal.Sum));
        contents.AddTable(columns, items.Select((i, index) => hasItemWeight
            ? new object?[] { index + 1, i.ProductName, i.Article ?? "", i.Barcode ?? "", i.Quantity, i.Unit ?? "", i.Weight > 0 ? i.Weight : null }
            : new object?[] { index + 1, i.ProductName, i.Article ?? "", i.Barcode ?? "", i.Quantity, i.Unit ?? "" }));

        // «History» Excel резервирует как имя листа, поэтому по-английски лист называется иначе.
        var history = book.AddSheet(Label("История", "Тарых", "Status history", "Geçmiş", "Tarix"), meta.Title);
        history.AddTable(
        [
            new XlColumn(LWhen, XlKind.DateTime),
            new XlColumn(LStatus, MaxWidth: 28),
            new XlColumn(LEmployee, MaxWidth: 36),
            new XlColumn(LNote, MaxWidth: 60),
        ],
        log.Select(l => new object?[]
        {
            l.At,
            new XlCell(describeStatus(l.Status), StatusTone(l.Status)),
            l.Employee ?? "",
            l.Note ?? "",
        }), totals: false);

        book.Save();
    }

    /// <summary>Тот же документ, но бумагой: шапка, маршрут, таблица состава и место для подписей
    /// отправителя и получателя — без них накладная бесполезна.</summary>
    public static void ExportTransferToWord(
        string path,
        StockTransferService.Transfer transfer,
        IReadOnlyList<StockTransferService.TransferItem> items,
        string statusText,
        string? shopName)
    {
        var meta = ReportMeta.Create(WaybillTitle(transfer.Number), shopName, null);
        using var doc = new WordReportBuilder(path, meta);

        doc.TitleBlock(
        [
            (LCreated, ReportFormat.DateAndTime(transfer.CreatedAt)),
            (LStatus, statusText),
            (LFrom, Dash(transfer.FromPlaceName)),
            (LTo, Dash(transfer.ToPlaceName)),
            (LResponsible, Dash(transfer.Responsible)),
            (LCarrier, Dash(transfer.Carrier)),
            (LTracking, Dash(transfer.TrackingNumber)),
            (LWeight, WeightText(transfer.TotalWeight)),
        ]);

        if (!string.IsNullOrWhiteSpace(transfer.Note))
            doc.Note(LNote + ": " + transfer.Note);

        doc.Table(
            [
                new WColumn(ReportLabels.Number, 0.5, Center: true),
                new WColumn(LName, 4.6),
                new WColumn(LCode, 1.3),
                new WColumn(LBarcode, 1.9),
                new WColumn(LQuantity, 1.3, Numeric: true),
                new WColumn(LUnit, 0.9, Center: true),
            ],
            items.Select((i, index) => new[]
            {
                (index + 1).ToString(ReportFormat.Culture),
                i.ProductName,
                i.Article ?? "",
                i.Barcode ?? "",
                ReportFormat.Quantity(i.Quantity),
                i.Unit ?? "",
            }).ToList(),
            ["", ReportLabels.Total + ": " + items.Count + " " + Label("поз.", "поз.", "items", "kalem", "poz."), "", "",
                ReportFormat.Quantity(items.Sum(i => i.Quantity)), ""]);

        doc.Paragraph(
            LItems + ": " + items.Count
            + "    ·    " + LUnits + ": " + ReportFormat.Quantity(items.Sum(i => i.Quantity))
            + "    ·    " + LWeight + ": " + WeightText(transfer.TotalWeight));

        doc.Signatures(
        [
            Label("Отгрузил", "Жөнөттү", "Shipped by", "Gönderen", "Jo'natdi"),
            Label("Перевёз", "Ташыды", "Carried by", "Taşıyan", "Tashidi"),
            Label("Принял", "Кабыл алды", "Received by", "Teslim alan", "Qabul qildi"),
        ]);

        doc.Save();
    }

    // ------------------------------------------------------------------ журнал

    /// <summary>Журнал перемещений целиком — для инвентаризации и разбора: какие документы были,
    /// куда шли и чем закончились.</summary>
    public static void ExportJournalToExcel(
        string path,
        IReadOnlyList<StockTransferService.Transfer> transfers,
        Func<string, string> describeStatus,
        string? shopName)
    {
        var meta = ReportMeta.Create(JournalTitle, shopName, JournalPeriod(transfers));
        using var book = new ExcelWorkbookBuilder(path, meta);

        var sheet = book.AddSheet(Label("Перемещения", "Жылышуулар", "Transfers", "Transferler", "Ko'chirishlar"), JournalTitle, landscape: true);
        sheet.AddTable(
        [
            new XlColumn(LNumber, XlKind.Code),
            new XlColumn(LCreated, XlKind.DateTime),
            new XlColumn(LStatus, MaxWidth: 24),
            new XlColumn(LFrom, MaxWidth: 32),
            new XlColumn(LTo, MaxWidth: 32),
            new XlColumn(LItems, XlKind.Integer, XlTotal.Sum),
            new XlColumn(LUnitsShort, XlKind.Quantity, XlTotal.Sum),
            new XlColumn(ReportLabels.WithUnit(Label("Вес", "Салмагы", "Weight", "Ağırlık", "Og'irlik"), ReportLabels.Kg), XlKind.Quantity, XlTotal.Sum),
            new XlColumn(LResponsible, MaxWidth: 30),
            new XlColumn(LCarrier, MaxWidth: 26),
            new XlColumn(LTracking, XlKind.Code, MaxWidth: 26),
        ],
        transfers.Select(t => new object?[]
        {
            t.Number,
            t.CreatedAt,
            new XlCell(describeStatus(t.Status), StatusTone(t.Status)),
            t.FromPlaceName,
            t.ToPlaceName,
            t.ItemCount,
            t.TotalQuantity,
            t.TotalWeight > 0 ? t.TotalWeight : null,
            t.Responsible ?? "",
            t.Carrier ?? "",
            t.TrackingNumber ?? "",
        }));

        book.Save();
    }

    public static void ExportJournalToWord(
        string path,
        IReadOnlyList<StockTransferService.Transfer> transfers,
        Func<string, string> describeStatus,
        string? shopName)
    {
        var meta = ReportMeta.Create(JournalTitle, shopName, JournalPeriod(transfers));
        // Колонок много — журнал печатается альбомом, иначе маршрут и ответственный ломаются
        // по буквам.
        using var doc = new WordReportBuilder(path, meta, landscape: true);

        doc.TitleBlock();
        doc.Table(
            [
                new WColumn(LNumber, 0.9),
                new WColumn(LCreated, 1.35, Center: true),
                new WColumn(LStatus, 1.2, Center: true),
                new WColumn(LRoute, 3.4),
                new WColumn(LItems, 0.9, Numeric: true),
                new WColumn(LUnitsShort, 1, Numeric: true),
                new WColumn(LResponsible, 1.8),
                new WColumn(LCarrier, 1.5),
            ],
            transfers.Select(t => new[]
            {
                t.Number,
                ReportFormat.DateAndTime(t.CreatedAt),
                describeStatus(t.Status),
                Dash(t.FromPlaceName) + " → " + Dash(t.ToPlaceName),
                ReportFormat.Integer(t.ItemCount),
                ReportFormat.Quantity(t.TotalQuantity),
                Dash(t.Responsible),
                Dash(t.Carrier),
            }).ToList(),
            [ReportLabels.Total, "", "", Label("Документов", "Документтер", "Documents", "Belge", "Hujjatlar") + ": " + transfers.Count,
                ReportFormat.Integer(transfers.Sum(t => t.ItemCount)), ReportFormat.Quantity(transfers.Sum(t => t.TotalQuantity)), "", ""],
            (row, column) => column == 2 ? StatusTone(transfers[row].Status) : ReportTone.None);

        doc.Save();
    }

    // ------------------------------------------------------------------ мелочи

    /// <summary>Период журнала — от самого раннего до самого позднего документа в выгрузке.</summary>
    private static string? JournalPeriod(IReadOnlyList<StockTransferService.Transfer> transfers)
    {
        if (transfers.Count == 0)
            return null;

        var from = transfers.Min(t => t.CreatedAt);
        var to = transfers.Max(t => t.CreatedAt);
        return from.Date == to.Date ? ReportFormat.Date(from) : $"{ReportFormat.Date(from)} — {ReportFormat.Date(to)}";
    }

    private static string Label(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
