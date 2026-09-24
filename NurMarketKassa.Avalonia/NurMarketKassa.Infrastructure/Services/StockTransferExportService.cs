using System.Globalization;
using System.Runtime.Versioning;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

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
    /// <summary>Вес партии вписывают руками; ноль значит «не указан», и в накладной это должно
    /// читаться как прочерк, а не как груз без веса.</summary>
    private static string WeightText(double weight) =>
        weight > 0 ? weight.ToString("0.###", CultureInfo.InvariantCulture) + " кг" : "—";

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
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        ExcelWorkbookBuilder.AddStylesheet(workbookPart);

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;

        var headerRows = new List<string[]>
        {
            new[] { Label("Документ", "Документ", "Document", "Belge", "Hujjat"), transfer.Number },
            new[] { Label("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"), transfer.CreatedAt.ToString("dd.MM.yyyy HH:mm") },
            new[] { Label("Статус", "Абалы", "Status", "Durum", "Holat"), statusText },
            new[] { Label("Откуда", "Кайдан", "From", "Nereden", "Qayerdan"), transfer.FromPlaceName },
            new[] { Label("Куда", "Кайда", "To", "Nereye", "Qayerga"), transfer.ToPlaceName },
            new[] { Label("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul"), transfer.Responsible ?? "" },
            new[] { Label("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), transfer.Carrier ?? "" },
            new[] { Label("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek raqami"), transfer.TrackingNumber ?? "" },
            new[] { Label("Вес партии", "Партиянын салмагы", "Batch weight", "Parti ağırlığı", "Partiya og'irligi"),
                WeightText(transfer.TotalWeight) },
        };
        if (!string.IsNullOrWhiteSpace(shopName))
            headerRows.Insert(0, new[] { Label("Магазин", "Дүкөн", "Shop", "Mağaza", "Do'kon"), shopName });

        ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId,
            Label("Перемещение", "Жылышуу", "Transfer", "Transfer", "Ko'chirish"),
            [
                new ExcelWorkbookBuilder.Column(Label("Поле", "Талаа", "Field", "Alan", "Maydon"), 28),
                new ExcelWorkbookBuilder.Column(Label("Значение", "Мааниси", "Value", "Değer", "Qiymati"), 46),
            ],
            headerRows);

        ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId,
            Label("Состав", "Курамы", "Contents", "İçerik", "Tarkibi"),
            [
                new ExcelWorkbookBuilder.Column(Label("Название", "Аты", "Name", "Ad", "Nomi"), 44),
                new ExcelWorkbookBuilder.Column(Label("Код", "Коду", "Code", "Kod", "Kod"), 14),
                new ExcelWorkbookBuilder.Column(Label("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 20),
                new ExcelWorkbookBuilder.Column(Label("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), 14, IsNumber: true),
                new ExcelWorkbookBuilder.Column(Label("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik"), 12),
            ],
            items.Select(i => new[]
            {
                i.ProductName,
                i.Article ?? "",
                i.Barcode ?? "",
                i.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                i.Unit ?? "",
            }).ToList());

        ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId,
            Label("История", "Тарых", "History", "Geçmiş", "Tarix"),
            [
                new ExcelWorkbookBuilder.Column(Label("Когда", "Качан", "When", "Ne zaman", "Qachon"), 22),
                new ExcelWorkbookBuilder.Column(Label("Статус", "Абалы", "Status", "Durum", "Holat"), 22),
                new ExcelWorkbookBuilder.Column(Label("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim"), 30),
                new ExcelWorkbookBuilder.Column(Label("Примечание", "Эскертүү", "Note", "Not", "Izoh"), 40),
            ],
            log.Select(l => new[]
            {
                l.At.ToString("dd.MM.yyyy HH:mm"),
                describeStatus(l.Status),
                l.Employee ?? "",
                l.Note ?? "",
            }).ToList());

        workbookPart.Workbook.Save();
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
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document();
        var body = mainPart.Document.AppendChild(new W.Body());

        body.AppendChild(Heading(Label("Перемещение товара", "Товардын жылышуусу", "Stock transfer",
            "Stok transferi", "Tovar ko'chirish") + "  №" + transfer.Number));

        if (!string.IsNullOrWhiteSpace(shopName))
            body.AppendChild(TextLine(shopName));

        body.AppendChild(TextLine(
            Label("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi") + ": "
            + transfer.CreatedAt.ToString("dd.MM.yyyy HH:mm")
            + "    ·    " + Label("Статус", "Абалы", "Status", "Durum", "Holat") + ": " + statusText));

        body.AppendChild(TextLine(
            Label("Откуда", "Кайдан", "From", "Nereden", "Qayerdan") + ": " + Dash(transfer.FromPlaceName)
            + "    →    " + Label("Куда", "Кайда", "To", "Nereye", "Qayerga") + ": " + Dash(transfer.ToPlaceName)));

        body.AppendChild(TextLine(
            Label("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul") + ": " + Dash(transfer.Responsible)
            + "    ·    " + Label("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi") + ": " + Dash(transfer.Carrier)
            + "    ·    " + Label("Трек-номер", "Трек-номер", "Tracking", "Takip", "Trek") + ": " + Dash(transfer.TrackingNumber)));

        body.AppendChild(TextLine(""));

        var rows = items.Select((i, index) => new[]
        {
            (index + 1).ToString(),
            i.ProductName,
            i.Article ?? "",
            i.Barcode ?? "",
            i.Quantity.ToString("0.###", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(i.Unit) ? "" : " " + i.Unit),
        }).ToList();

        body.AppendChild(BuildTable(
            [
                "№",
                Label("Название", "Аты", "Name", "Ad", "Nomi"),
                Label("Код", "Коду", "Code", "Kod", "Kod"),
                Label("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"),
                Label("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
            ],
            rows));

        body.AppendChild(TextLine(""));
        body.AppendChild(TextLine(
            Label("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiya") + ": " + items.Count
            + "    ·    " + Label("Всего единиц", "Жалпы бирдик", "Total units", "Toplam adet", "Jami birlik")
            + ": " + items.Sum(i => i.Quantity).ToString("0.###", CultureInfo.InvariantCulture)
            + "    ·    " + Label("Вес", "Салмагы", "Weight", "Ağırlık", "Og'irlik")
            + ": " + WeightText(transfer.TotalWeight)));

        body.AppendChild(TextLine(""));
        body.AppendChild(TextLine(""));
        body.AppendChild(TextLine(
            Label("Отгрузил", "Жөнөттү", "Shipped by", "Gönderen", "Jo'natdi")
            + ": _______________________          "
            + Label("Принял", "Кабыл алды", "Received by", "Teslim alan", "Qabul qildi")
            + ": _______________________"));

        mainPart.Document.Save();
    }

    /// <summary>Журнал перемещений целиком — для инвентаризации и разбора: какие документы были,
    /// куда шли и чем закончились.</summary>
    public static void ExportJournalToExcel(
        string path,
        IReadOnlyList<StockTransferService.Transfer> transfers,
        Func<string, string> describeStatus,
        string? shopName)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        ExcelWorkbookBuilder.AddStylesheet(workbookPart);

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;

        ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId,
            Label("Перемещения", "Жылышуулар", "Transfers", "Transferler", "Ko'chirishlar"),
            [
                new ExcelWorkbookBuilder.Column(Label("Номер", "Номери", "Number", "Numara", "Raqam"), 14),
                new ExcelWorkbookBuilder.Column(Label("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"), 20),
                new ExcelWorkbookBuilder.Column(Label("Статус", "Абалы", "Status", "Durum", "Holat"), 18),
                new ExcelWorkbookBuilder.Column(Label("Откуда", "Кайдан", "From", "Nereden", "Qayerdan"), 26),
                new ExcelWorkbookBuilder.Column(Label("Куда", "Кайда", "To", "Nereye", "Qayerga"), 26),
                new ExcelWorkbookBuilder.Column(Label("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), 12, IsNumber: true),
                new ExcelWorkbookBuilder.Column(Label("Единиц", "Бирдик", "Units", "Adet", "Birlik"), 12, IsNumber: true),
                new ExcelWorkbookBuilder.Column(Label("Вес", "Салмагы", "Weight", "Ağırlık", "Og'irlik"), 12, IsNumber: true),
                new ExcelWorkbookBuilder.Column(Label("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul"), 26),
                new ExcelWorkbookBuilder.Column(Label("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), 22),
                new ExcelWorkbookBuilder.Column(Label("Трек-номер", "Трек-номер", "Tracking", "Takip", "Trek"), 22),
            ],
            transfers.Select(t => new[]
            {
                t.Number,
                t.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                describeStatus(t.Status),
                t.FromPlaceName,
                t.ToPlaceName,
                t.ItemCount.ToString(CultureInfo.InvariantCulture),
                t.TotalQuantity.ToString("0.###", CultureInfo.InvariantCulture),
                t.TotalWeight.ToString("0.###", CultureInfo.InvariantCulture),
                t.Responsible ?? "",
                t.Carrier ?? "",
                t.TrackingNumber ?? "",
            }).ToList());

        _ = shopName;
        workbookPart.Workbook.Save();
    }

    public static void ExportJournalToWord(
        string path,
        IReadOnlyList<StockTransferService.Transfer> transfers,
        Func<string, string> describeStatus,
        string? shopName)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document();
        var body = mainPart.Document.AppendChild(new W.Body());

        body.AppendChild(Heading(Label("Журнал перемещений", "Жылышуулар журналы", "Transfer journal",
            "Transfer günlüğü", "Ko'chirishlar jurnali")));
        if (!string.IsNullOrWhiteSpace(shopName))
            body.AppendChild(TextLine(shopName));
        body.AppendChild(TextLine(DateTime.Now.ToString("dd.MM.yyyy HH:mm")));
        body.AppendChild(TextLine(""));

        body.AppendChild(BuildTable(
            [
                Label("Номер", "Номери", "Number", "Numara", "Raqam"),
                Label("Создан", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"),
                Label("Статус", "Абалы", "Status", "Durum", "Holat"),
                Label("Маршрут", "Багыты", "Route", "Güzergah", "Yo'nalish"),
                Label("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"),
                Label("Ответственный", "Жооптуу", "Responsible", "Sorumlu", "Mas'ul"),
            ],
            transfers.Select(t => new[]
            {
                t.Number,
                t.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                describeStatus(t.Status),
                Dash(t.FromPlaceName) + " → " + Dash(t.ToPlaceName),
                t.ItemCount.ToString(CultureInfo.InvariantCulture),
                Dash(t.Responsible),
            }).ToList()));

        mainPart.Document.Save();
    }

    // ------------------------------------------------------------------ мелочи оформления

    private static string Label(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static W.Paragraph Heading(string text) =>
        new(new W.Run(
            new W.RunProperties(new W.Bold(), new W.FontSize { Val = "32" }),
            new W.Text(text)));

    private static W.Paragraph TextLine(string text) =>
        new(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Table BuildTable(string[] header, List<string[]> rows)
    {
        var table = new W.Table(new W.TableProperties(
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));

        table.AppendChild(BuildRow(header, bold: true));
        foreach (var row in rows)
            table.AppendChild(BuildRow(row, bold: false));

        return table;
    }

    private static W.TableRow BuildRow(string[] values, bool bold)
    {
        var row = new W.TableRow();
        foreach (var value in values)
        {
            var run = bold
                ? new W.Run(new W.RunProperties(new W.Bold()), new W.Text(value ?? ""))
                : new W.Run(new W.Text(value ?? ""));
            row.AppendChild(new W.TableCell(new W.Paragraph(run)));
        }

        return row;
    }
}
