using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-24, ТЗ «Склад», пакет Р3 (владелец: «наименование товара, количество на складе, фактическое количество и цена»).
/// Выгрузка ревизии в Excel (.xlsx), CSV и PDF. Работает на простом входном списке <see cref="RevisionExportRow"/> — окно ревизии
/// (пакет Р2) подставляет туда свои данные (черновик, итог перед «Применить» или сохранённый акт), к модели движка ревизии
/// сервис не привязан. Без новых зависимостей: Excel — <see cref="ExcelWorkbookBuilder"/>, PDF — QuestPDF (как ClientDocRenderer),
/// CSV пишется здесь (UTF-8 с BOM, разделитель «;», числа в культуре <see cref="ReportFormat.Culture"/>).
///
/// Правила: «Расхождение» = факт − учёт (округляется до 0,001, чтобы 0,711 − 1 не давало хвост); «Сумма расхождения» = расхождение ×
/// цена продажи; «Сумма по закупке» = расхождение × цена закупки (только с флажком <see cref="RevisionExportOptions.IncludePurchasePrices"/>).
/// Непосчитанная строка: факт, расхождение и суммы пусты. Сводка в шапке считается по ВСЕМ переданным строкам, таблица — по выбранному
/// объёму (весь склад / отсканированные / с расхождением).
///
/// Скорость: вызывать из <see cref="Task.Run(Action)"/> (<see cref="ExportAsync"/> так и делает) — окно не замирает. Файл пишется во
/// временный «.part» и подменяет целевой только после успеха: сорвавшаяся выгрузка не портит прежний файл.</summary>
public static class RevisionExportService
{
    static RevisionExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };

    /// <summary>Расхождение меньше этого — «ноль» (весовой товар: 0,711 − 0,711 не должно давать 1E-16).</summary>
    private const double Epsilon = 0.0005;

    // ------------------------------------------------------------------ вход/выход

    /// <summary>Объём выгрузки одной сводкой: считается по ВСЕМ строкам, а не по выбранному объёму.</summary>
    public sealed record Summary(
        int Total, int Counted, int WithDifference,
        double ShortageQuantity, double ShortageAmount, double ShortageCostAmount,
        double SurplusQuantity, double SurplusAmount, double SurplusCostAmount);

    /// <summary>Сводка по строкам (для окна итога тоже годится: «недостача −5 шт, излишек +3 шт»).</summary>
    public static Summary Summarize(IEnumerable<RevisionExportRow> rows)
    {
        int total = 0, counted = 0, diffs = 0;
        double shortQty = 0, shortSum = 0, shortCost = 0, surQty = 0, surSum = 0, surCost = 0;
        foreach (var r in rows)
        {
            total++;
            if (r.FactQuantity is not { } fact)
                continue;
            counted++;
            var diff = Math.Round(fact - r.BookQuantity, 3, MidpointRounding.AwayFromZero);
            if (Math.Abs(diff) < Epsilon)
                continue;
            diffs++;
            var sum = Math.Round(diff * r.SalePrice, 2, MidpointRounding.AwayFromZero);
            var cost = r.PurchasePrice is { } pp ? Math.Round(diff * pp, 2, MidpointRounding.AwayFromZero) : 0;
            if (diff < 0)
            {
                shortQty += -diff; shortSum += -sum; shortCost += -cost;
            }
            else
            {
                surQty += diff; surSum += sum; surCost += cost;
            }
        }

        return new Summary(total, counted, diffs, shortQty, shortSum, shortCost, surQty, surSum, surCost);
    }

    /// <summary>Строки выбранного объёма в исходном порядке (для окна — «сколько строк уйдёт в файл»).</summary>
    public static List<RevisionExportRow> Filter(IEnumerable<RevisionExportRow> rows, RevisionExportScope scope) =>
        scope switch
        {
            RevisionExportScope.Scanned => rows.Where(r => r.FactQuantity.HasValue).ToList(),
            RevisionExportScope.Discrepancy => rows.Where(HasDifference).ToList(),
            _ => rows as List<RevisionExportRow> ?? rows.ToList(),
        };

    private static bool HasDifference(RevisionExportRow r) =>
        r.FactQuantity is { } f && Math.Abs(Math.Round(f - r.BookQuantity, 3, MidpointRounding.AwayFromZero)) >= Epsilon;

    public static string Extension(RevisionExportFormat format) => format switch
    {
        RevisionExportFormat.Csv => "csv",
        RevisionExportFormat.Pdf => "pdf",
        _ => "xlsx",
    };

    /// <summary>«Акт ревизии 24.10.2026 Магазин.xlsx»; для объёма не «весь склад» — в названии пометка.</summary>
    public static string SuggestFileName(RevisionExportMeta meta, RevisionExportFormat format, RevisionExportScope scope = RevisionExportScope.All)
    {
        var report = TitleOf(meta);
        if (scope != RevisionExportScope.All)
            report += " (" + ScopeText(scope).ToLowerInvariant() + ")";
        return ExportFileName.Build(report, ReportFormat.Date(meta.FinishedAt ?? meta.StartedAt ?? DateTime.Now), meta.ShopName, Extension(format));
    }

    /// <summary>Выгрузка на фоновом потоке. <paramref name="progress"/> — короткие сообщения для строки окна (создавать на потоке окна).</summary>
    public static Task ExportAsync(string path, RevisionExportFormat format, IReadOnlyList<RevisionExportRow> rows, RevisionExportMeta meta,
        RevisionExportOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Export(path, format, rows, meta, options, progress, ct), ct);

    /// <summary>Та же выгрузка, но на вызывающем потоке (стенд, тесты без окна).</summary>
    public static void Export(string path, RevisionExportFormat format, IReadOnlyList<RevisionExportRow> rows, RevisionExportMeta meta,
        RevisionExportOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new RevisionExportOptions();
        progress?.Report(Preparing);
        var summary = Summarize(rows);
        var lines = BuildLines(rows, options.Scope, options.IncludePurchasePrices, ct);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".part";
        try
        {
            progress?.Report(Writing(lines.Count));
            switch (format)
            {
                case RevisionExportFormat.Csv:
                    WriteCsv(temp, lines, options, ct);
                    break;
                case RevisionExportFormat.Pdf:
                    WritePdf(temp, lines, summary, meta, options, ct);
                    break;
                default:
                    WriteExcel(temp, lines, summary, meta, options, ct);
                    break;
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* временный файл не критичен */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ строки

    /// <summary>Строка, уже посчитанная для вывода (один раз на все форматы).</summary>
    private sealed record Line(
        int Number, string Name, string Barcode, double Book, double? Fact, double? Diff,
        double Price, double? Purchase, double? DiffAmount, double? DiffCost, DateTime? CountedAt, double? Recorded);

    private static List<Line> BuildLines(IReadOnlyList<RevisionExportRow> rows, RevisionExportScope scope, bool purchase, CancellationToken ct)
    {
        var result = new List<Line>(rows.Count);
        var n = 0;
        foreach (var r in rows)
        {
            if ((++n & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            double? diff = r.FactQuantity is { } f ? Math.Round(f - r.BookQuantity, 3, MidpointRounding.AwayFromZero) : null;
            if (scope == RevisionExportScope.Scanned && diff is null)
                continue;
            if (scope == RevisionExportScope.Discrepancy && (diff is null || Math.Abs(diff.Value) < Epsilon))
                continue;
            double? amount = diff is { } d ? Math.Round(d * r.SalePrice, 2, MidpointRounding.AwayFromZero) : null;
            double? cost = purchase && diff is { } d2 && r.PurchasePrice is { } pp ? Math.Round(d2 * pp, 2, MidpointRounding.AwayFromZero) : null;
            result.Add(new Line(result.Count + 1, r.Name ?? "", r.Barcode ?? "", r.BookQuantity, r.FactQuantity, diff,
                r.SalePrice, purchase ? r.PurchasePrice : null, amount, cost, r.CountedAt, r.RecordedQuantity));
        }

        return result;
    }

    // ------------------------------------------------------------------ подписи (5 языков)

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Заголовки колонок — из таблицы ТЗ 3.2. Денежные колонки получают «, сом» отдельно (ReportLabels.WithUnit).
    public static string ColNumber => ReportLabels.Number;
    public static string ColName => L("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi");
    public static string ColBarcode => L("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod");
    public static string ColBook => L("Количество на складе (учёт)", "Кампадагы саны (эсеп)", "Book quantity", "Sistem miktarı", "Ombordagi miqdor (hisob)");
    public static string ColFact => L("Фактическое количество", "Иш жүзүндөгү саны", "Counted quantity", "Sayılan miktar", "Haqiqiy miqdor");
    public static string ColDiff => L("Расхождение (факт − учёт)", "Айырма (факт − эсеп)", "Difference (counted − book)", "Fark (sayılan − sistem)", "Farq (haqiqiy − hisob)");
    public static string ColPrice => L("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi");
    public static string ColPurchase => L("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi");
    public static string ColDiffAmount => L("Сумма расхождения", "Айырманын суммасы", "Difference amount", "Fark tutarı", "Farq summasi");
    public static string ColDiffCost => L("Сумма по закупке", "Сатып алуу баасы боюнча сумма", "Amount at cost", "Alış tutarı", "Xarid narxida summa");
    public static string ColRecorded => L("Записано в акт", "Актыга жазылды", "Recorded in the act", "Esas alınan miktar", "Aktga yozildi");
    public static string ColTime => L("Время пересчёта", "Саналган убакыт", "Counted at", "Sayım zamanı", "Sanalgan vaqt");

    private static string Money(string title) => ReportLabels.WithUnit(title, ReportLabels.Currency);

    private static string TitleOf(RevisionExportMeta meta) => meta.IsDraft
        ? L("Ревизия (черновик)", "Ревизия (черновик)", "Stocktake (draft)", "Sayım (taslak)", "Reviziya (qoralama)")
        : L("Акт ревизии", "Ревизия актысы", "Stocktake report", "Sayım tutanağı", "Reviziya dalolatnomasi");

    private static string ScopeText(RevisionExportScope scope) => scope switch
    {
        RevisionExportScope.Scanned => L("Только отсканированные", "Сканерленгендер гана", "Counted items only", "Yalnızca sayılanlar", "Faqat skanerlanganlar"),
        RevisionExportScope.Discrepancy => L("Только с расхождением", "Айырмасы барлары гана", "Only with a difference", "Yalnızca farklı olanlar", "Faqat farqi borlar"),
        _ => L("Весь склад", "Бүт кампа", "Whole warehouse", "Tüm depo", "Butun ombor"),
    };

    private static string Preparing => L("Готовлю выгрузку ревизии…", "Ревизияны чыгарууга даярдоо…", "Preparing the stocktake export…", "Sayım dışa aktarımı hazırlanıyor…", "Reviziya eksporti tayyorlanmoqda…");

    private static string Writing(int count) => L($"Записываю файл… строк: {count}", $"Файл жазылууда… саптар: {count}",
        $"Writing the file… rows: {count}", $"Dosya yazılıyor… satır: {count}", $"Fayl yozilmoqda… qatorlar: {count}");

    private static string PeriodText(RevisionExportMeta meta)
    {
        var from = meta.StartedAt;
        var to = meta.FinishedAt;
        if (from is null && to is null)
            return ReportFormat.Date(DateTime.Now);
        if (from is null)
            return ReportFormat.DateAndTime(to!.Value);
        return to is null || to.Value == from.Value
            ? ReportFormat.DateAndTime(from.Value)
            : ReportFormat.DateAndTime(from.Value) + " – " + ReportFormat.DateAndTime(to.Value);
    }

    private static string ActsText(RevisionExportMeta meta) =>
        meta.ActNumbers is { Count: > 0 } acts ? string.Join(", ", acts) : "—";

    private static string ConductedBy(RevisionExportMeta meta) =>
        !string.IsNullOrWhiteSpace(meta.ConductedBy) ? meta.ConductedBy!.Trim()
        : !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName) ? PosApp.CurrentUserDisplayName!.Trim() : "—";

    private static bool HasRecorded(IReadOnlyList<Line> lines)
    {
        foreach (var l in lines)
            if (l.Recorded.HasValue)
                return true;
        return false;
    }

    // ------------------------------------------------------------------ Excel

    private static void WriteExcel(string path, IReadOnlyList<Line> lines, Summary summary, RevisionExportMeta meta, RevisionExportOptions options, CancellationToken ct)
    {
        var title = TitleOf(meta);
        var reportMeta = ReportMeta.Create(title, meta.ShopName, PeriodText(meta));
        using var book = new ExcelWorkbookBuilder(path, reportMeta);

        var purchase = options.IncludePurchasePrices;
        var recorded = HasRecorded(lines);
        var columns = new List<XlColumn>
        {
            new(ColNumber, XlKind.Index),
            new(ColName, MaxWidth: 50),
            new(ColBarcode, XlKind.Code),
            new(ColBook, XlKind.Quantity, MaxWidth: 22),
            new(ColFact, XlKind.Quantity, MaxWidth: 22),
            new(ColDiff, XlKind.Quantity, XlTotal.Sum, MaxWidth: 24),
            new(Money(ColPrice), XlKind.Money),
        };
        if (purchase)
            columns.Add(new XlColumn(Money(ColPurchase), XlKind.Money));
        columns.Add(new XlColumn(Money(ColDiffAmount), XlKind.Money, XlTotal.Sum));
        if (purchase)
            columns.Add(new XlColumn(Money(ColDiffCost), XlKind.Money, XlTotal.Sum));
        if (recorded)
            columns.Add(new XlColumn(ColRecorded, XlKind.Quantity));
        columns.Add(new XlColumn(ColTime, XlKind.DateTime));

        var note = ScopeText(options.Scope)
            + (meta.StockAsOf is { } asOf ? "   ·   " + L("Остатки на", "Калдыктар", "Stock as of", "Stok tarihi", "Qoldiqlar sanasi") + " " + ReportFormat.DateAndTime(asOf) : "")
            + "   ·   " + L("Провёл", "Өткөрдү", "Conducted by", "Yapan", "O'tkazdi") + ": " + ConductedBy(meta);
        var sheet = book.AddSheet(L("Ревизия", "Ревизия", "Stocktake", "Sayım", "Reviziya"), title, note, landscape: true);

        IEnumerable<object?[]> Rows()
        {
            var i = 0;
            foreach (var l in lines)
            {
                if ((++i & 0x3FFF) == 0)
                    ct.ThrowIfCancellationRequested();
                var row = new List<object?>(columns.Count)
                {
                    l.Number, l.Name, l.Barcode, l.Book, l.Fact,
                    new XlCell(l.Diff, Tone(l.Diff)),
                    l.Price,
                };
                if (purchase)
                    row.Add(l.Purchase);
                row.Add(new XlCell(l.DiffAmount, Tone(l.DiffAmount)));
                if (purchase)
                    row.Add(new XlCell(l.DiffCost, Tone(l.DiffCost)));
                if (recorded)
                    row.Add(l.Recorded);
                row.Add(l.CountedAt);
                yield return row.ToArray();
            }
        }

        sheet.AddTable(columns, Rows());
        ct.ThrowIfCancellationRequested();

        // Второй лист — шапка акта и сводка (первый лист — сама таблица, чтобы файл открывался сразу на ней).
        var summarySheet = book.AddSheet(L("Итоги", "Жыйынтык", "Summary", "Özet", "Yakun"), title);
        var items = new List<XlKeyValue>
        {
            new(L("Документ", "Документ", "Document", "Belge", "Hujjat"), title),
            new(ReportLabels.Shop, string.IsNullOrWhiteSpace(meta.ShopName) ? "—" : meta.ShopName!.Trim()),
        };
        if (meta.StartedAt is { } started)
            items.Add(new XlKeyValue(L("Начата", "Башталды", "Started", "Başlangıç", "Boshlandi"), started, XlKind.DateTime));
        if (meta.FinishedAt is { } finished)
            items.Add(new XlKeyValue(L("Завершена", "Аяктады", "Finished", "Bitiş", "Tugadi"), finished, XlKind.DateTime));
        if (meta.StockAsOf is { } stockAsOf)
            items.Add(new XlKeyValue(L("Остатки на", "Калдыктар", "Stock as of", "Stok tarihi", "Qoldiqlar sanasi"), stockAsOf, XlKind.DateTime));
        items.Add(new XlKeyValue(L("Провёл", "Өткөрдү", "Conducted by", "Yapan", "O'tkazdi"), ConductedBy(meta)));
        items.Add(new XlKeyValue(L("Акт(ы) на сервере", "Сервердеги акт(тар)", "Act(s) on the server", "Sunucudaki tutanak(lar)", "Serverdagi akt(lar)"), ActsText(meta)));
        items.Add(new XlKeyValue(L("Объём выгрузки", "Чыгаруу көлөмү", "Export scope", "Dışa aktarım kapsamı", "Eksport hajmi"), ScopeText(options.Scope)));
        items.Add(new XlKeyValue(L("Строк в таблице", "Таблицадагы саптар", "Rows in the table", "Tablodaki satır", "Jadvaldagi qatorlar"), lines.Count, XlKind.Integer));
        items.Add(new XlKeyValue(L("Позиций всего", "Бардыгы позициялар", "Items in total", "Toplam kalem", "Jami pozitsiyalar"), summary.Total, XlKind.Integer));
        items.Add(new XlKeyValue(L("Посчитано", "Саналды", "Counted", "Sayılan", "Sanaldi"), summary.Counted, XlKind.Integer));
        items.Add(new XlKeyValue(L("С расхождением", "Айырмасы барлар", "With a difference", "Farklı olan", "Farqi borlar"), summary.WithDifference, XlKind.Integer, summary.WithDifference > 0 ? ReportTone.Warn : ReportTone.None));
        items.Add(new XlKeyValue(L("Недостача, шт", "Жетишпөө, даана", "Shortage, pcs", "Eksik, adet", "Kam, dona"), summary.ShortageQuantity, XlKind.Quantity, summary.ShortageQuantity > 0 ? ReportTone.Bad : ReportTone.None));
        items.Add(new XlKeyValue(ReportLabels.WithUnit(L("Недостача по цене продажи", "Жетишпөө сатуу баасы боюнча", "Shortage at selling price", "Eksik (satış fiyatı)", "Kam (sotuv narxida)"), ReportLabels.Currency), summary.ShortageAmount, XlKind.Money, summary.ShortageAmount > 0 ? ReportTone.Bad : ReportTone.None));
        if (purchase)
            items.Add(new XlKeyValue(ReportLabels.WithUnit(L("Недостача по закупке", "Жетишпөө сатып алуу баасы боюнча", "Shortage at cost", "Eksik (alış fiyatı)", "Kam (xarid narxida)"), ReportLabels.Currency), summary.ShortageCostAmount, XlKind.Money, summary.ShortageCostAmount > 0 ? ReportTone.Bad : ReportTone.None));
        items.Add(new XlKeyValue(L("Излишек, шт", "Ашыкча, даана", "Surplus, pcs", "Fazla, adet", "Ortiqcha, dona"), summary.SurplusQuantity, XlKind.Quantity, summary.SurplusQuantity > 0 ? ReportTone.Good : ReportTone.None));
        items.Add(new XlKeyValue(ReportLabels.WithUnit(L("Излишек по цене продажи", "Ашыкча сатуу баасы боюнча", "Surplus at selling price", "Fazla (satış fiyatı)", "Ortiqcha (sotuv narxida)"), ReportLabels.Currency), summary.SurplusAmount, XlKind.Money, summary.SurplusAmount > 0 ? ReportTone.Good : ReportTone.None));
        if (purchase)
            items.Add(new XlKeyValue(ReportLabels.WithUnit(L("Излишек по закупке", "Ашыкча сатып алуу баасы боюнча", "Surplus at cost", "Fazla (alış fiyatı)", "Ortiqcha (xarid narxida)"), ReportLabels.Currency), summary.SurplusCostAmount, XlKind.Money, summary.SurplusCostAmount > 0 ? ReportTone.Good : ReportTone.None));
        summarySheet.AddKeyValues(items);

        book.Save();
    }

    /// <summary>Недостача — красным, излишек — зелёным, ноль и «не считали» — без подсветки.</summary>
    private static ReportTone Tone(double? value) =>
        value is not { } v || Math.Abs(v) < Epsilon ? ReportTone.None : v < 0 ? ReportTone.Bad : ReportTone.Good;

    // ------------------------------------------------------------------ CSV

    private static void WriteCsv(string path, IReadOnlyList<Line> lines, RevisionExportOptions options, CancellationToken ct)
    {
        const char sep = ';';
        var culture = ReportFormat.Culture;
        var purchase = options.IncludePurchasePrices;
        var recorded = HasRecorded(lines);

        // Числа без разделителя тысяч: файл читают и Excel, и учётные программы.
        string Q(double? v) => v is { } x ? x.ToString("0.###", culture) : "";
        string M(double? v) => v is { } x ? x.ToString("0.00", culture) : "";

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16) { NewLine = "\r\n" };
        var sb = new StringBuilder(256);

        void Field(string? s)
        {
            if (sb.Length > 0)
                sb.Append(sep);
            sb.Append(Escape(s));
        }

        void Flush()
        {
            w.WriteLine(sb.ToString());
            sb.Clear();
        }

        Field(ColNumber); Field(ColName); Field(ColBarcode); Field(ColBook); Field(ColFact); Field(ColDiff);
        Field(Money(ColPrice));
        if (purchase) Field(Money(ColPurchase));
        Field(Money(ColDiffAmount));
        if (purchase) Field(Money(ColDiffCost));
        if (recorded) Field(ColRecorded);
        Field(ColTime);
        Flush();

        var i = 0;
        foreach (var l in lines)
        {
            if ((++i & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            Field(l.Number.ToString(CultureInfo.InvariantCulture)); Field(l.Name); Field(l.Barcode);
            Field(Q(l.Book)); Field(Q(l.Fact)); Field(Q(l.Diff));
            Field(M(l.Price));
            if (purchase) Field(M(l.Purchase));
            Field(M(l.DiffAmount));
            if (purchase) Field(M(l.DiffCost));
            if (recorded) Field(Q(l.Recorded));
            Field(l.CountedAt is { } at ? ReportFormat.DateAndTime(at) : "");
            Flush();
        }
    }

    /// <summary>Кавычки для «;», «"» и переводов строки. Название, начинающееся с «= + - @», получает апостроф, чтобы Excel не
    /// принял его за формулу (числа сюда приходят строкой с цифры или со знака минус и не затрагиваются: правило только для текста).</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = value;
        // Число (в том числе отрицательное «-5») формулой не считается — апостроф только перед текстом.
        if (text[0] is '=' or '+' or '@' or '\t' or '\r' || (text[0] == '-' && !(text.Length > 1 && (char.IsDigit(text[1]) || text[1] == ','|| text[1] == '.'))))
            text = "'" + text;
        return text.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    // ------------------------------------------------------------------ PDF

    private static void WritePdf(string path, IReadOnlyList<Line> lines, Summary summary, RevisionExportMeta meta, RevisionExportOptions options, CancellationToken ct)
    {
        var title = TitleOf(meta);
        var purchase = options.IncludePurchasePrices;
        var recorded = HasRecorded(lines);
        const string Ink = "#1B2330", Soft = "#6B7686", Rule = "#C9D3E0", HeadFill = "#1F4E78", Zebra = "#F3F6FA", TotalFill = "#DDE5F0";
        const string Bad = "#9C0006", Good = "#006100";

        string Q(double? v) => v is { } x ? ReportFormat.Quantity(x) : "—";
        string M(double? v) => v is { } x ? ReportFormat.Money(x) : "—";
        string Signed(double? v) => v is { } x && Math.Abs(x) >= Epsilon ? (x > 0 ? "+" : "") + ReportFormat.Quantity(x) : v.HasValue ? "0" : "—";
        string SignedMoney(double? v) => v is { } x && Math.Abs(x) >= Epsilon ? (x > 0 ? "+" : "") + ReportFormat.Money(x) : v.HasValue ? "0" : "—";
        string ToneColor(double? v) => v is { } x && Math.Abs(x) >= Epsilon ? (x < 0 ? Bad : Good) : Ink;

        double sumDiff = 0, sumAmount = 0, sumCost = 0;
        foreach (var l in lines)
        {
            sumDiff += l.Diff ?? 0;
            sumAmount += l.DiffAmount ?? 0;
            sumCost += l.DiffCost ?? 0;
        }

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(10, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(8.5f).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(3);
                    col.Item().Text(title).FontSize(15).Bold();

                    var info = new List<string>();
                    if (!string.IsNullOrWhiteSpace(meta.ShopName))
                        info.Add(meta.ShopName!.Trim());
                    info.Add(PeriodText(meta));
                    info.Add(L("Провёл", "Өткөрдү", "Conducted by", "Yapan", "O'tkazdi") + ": " + ConductedBy(meta));
                    col.Item().Text(string.Join("   ·   ", info)).FontColor(Soft);

                    var info2 = new List<string> { ScopeText(options.Scope) };
                    if (meta.StockAsOf is { } asOf)
                        info2.Add(L("Остатки на", "Калдыктар", "Stock as of", "Stok tarihi", "Qoldiqlar sanasi") + " " + ReportFormat.DateAndTime(asOf));
                    if (meta.ActNumbers is { Count: > 0 })
                        info2.Add(L("Акт(ы)", "Акт(тар)", "Act(s)", "Tutanak", "Akt(lar)") + ": " + ActsText(meta));
                    col.Item().Text(string.Join("   ·   ", info2)).FontColor(Soft);

                    col.Item().Text(t =>
                    {
                        t.Span(L("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar") + ": " + summary.Total
                            + "  ·  " + L("посчитано", "саналды", "counted", "sayıldı", "sanaldi") + ": " + summary.Counted
                            + "  ·  " + L("с расхождением", "айырмасы бар", "with a difference", "farklı", "farqi bor") + ": " + summary.WithDifference + "  ·  ").SemiBold();
                        t.Span(L("недостача", "жетишпөө", "shortage", "eksik", "kam") + " −" + ReportFormat.Quantity(summary.ShortageQuantity) + " ("
                            + ReportFormat.Money(summary.ShortageAmount) + " " + ReportLabels.Currency + ")").SemiBold().FontColor(Bad);
                        t.Span("  ·  ").SemiBold();
                        t.Span(L("излишек", "ашыкча", "surplus", "fazla", "ortiqcha") + " +" + ReportFormat.Quantity(summary.SurplusQuantity) + " ("
                            + ReportFormat.Money(summary.SurplusAmount) + " " + ReportLabels.Currency + ")").SemiBold().FontColor(Good);
                    });

                    col.Item().PaddingTop(4).Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(11, Unit.Millimetre);
                            c.RelativeColumn(5);
                            c.ConstantColumn(30, Unit.Millimetre);
                            c.ConstantColumn(21, Unit.Millimetre);
                            c.ConstantColumn(21, Unit.Millimetre);
                            c.ConstantColumn(23, Unit.Millimetre);
                            c.ConstantColumn(19, Unit.Millimetre);
                            if (purchase) c.ConstantColumn(19, Unit.Millimetre);
                            c.ConstantColumn(23, Unit.Millimetre);
                            if (purchase) c.ConstantColumn(23, Unit.Millimetre);
                            if (recorded) c.ConstantColumn(19, Unit.Millimetre);
                        });

                        IContainer Head(IContainer x) => x.Background(HeadFill).PaddingVertical(3).PaddingHorizontal(3)
                            .DefaultTextStyle(s => s.SemiBold().FontSize(7.5f).FontColor("#FFFFFF"));

                        t.Header(h =>
                        {
                            h.Cell().Element(Head).AlignCenter().Text(ColNumber);
                            h.Cell().Element(Head).Text(ColName);
                            h.Cell().Element(Head).Text(ColBarcode);
                            h.Cell().Element(Head).AlignRight().Text(ColBook);
                            h.Cell().Element(Head).AlignRight().Text(ColFact);
                            h.Cell().Element(Head).AlignRight().Text(ColDiff);
                            h.Cell().Element(Head).AlignRight().Text(Money(ColPrice));
                            if (purchase) h.Cell().Element(Head).AlignRight().Text(Money(ColPurchase));
                            h.Cell().Element(Head).AlignRight().Text(Money(ColDiffAmount));
                            if (purchase) h.Cell().Element(Head).AlignRight().Text(Money(ColDiffCost));
                            if (recorded) h.Cell().Element(Head).AlignRight().Text(ColRecorded);
                        });

                        var i = 0;
                        foreach (var l in lines)
                        {
                            if ((++i & 0x3FF) == 0)
                                ct.ThrowIfCancellationRequested();
                            var bg = i % 2 == 0 ? Zebra : "#FFFFFF";
                            IContainer Cell(IContainer x) => x.Background(bg).BorderBottom(0.4f).BorderColor(Rule).PaddingVertical(2).PaddingHorizontal(3);
                            var color = ToneColor(l.Diff);
                            t.Cell().Element(Cell).AlignCenter().Text(l.Number.ToString(CultureInfo.InvariantCulture));
                            t.Cell().Element(Cell).Text(l.Name.Length == 0 ? "—" : l.Name);
                            t.Cell().Element(Cell).Text(l.Barcode);
                            t.Cell().Element(Cell).AlignRight().Text(Q(l.Book));
                            t.Cell().Element(Cell).AlignRight().Text(Q(l.Fact));
                            t.Cell().Element(Cell).AlignRight().Text(Signed(l.Diff)).FontColor(color);
                            t.Cell().Element(Cell).AlignRight().Text(M(l.Price));
                            if (purchase) t.Cell().Element(Cell).AlignRight().Text(M(l.Purchase));
                            t.Cell().Element(Cell).AlignRight().Text(SignedMoney(l.DiffAmount)).FontColor(color);
                            if (purchase) t.Cell().Element(Cell).AlignRight().Text(SignedMoney(l.DiffCost)).FontColor(ToneColor(l.DiffCost));
                            if (recorded) t.Cell().Element(Cell).AlignRight().Text(Q(l.Recorded));
                        }

                        // Строка «Итого» — под таблицей на последней странице.
                        IContainer Total(IContainer x) => x.Background(TotalFill).PaddingVertical(3).PaddingHorizontal(3).DefaultTextStyle(s => s.SemiBold());
                        t.Cell().Element(Total).Text("");
                        t.Cell().Element(Total).Text(ReportLabels.Total);
                        t.Cell().Element(Total).Text("");
                        t.Cell().Element(Total).Text("");
                        t.Cell().Element(Total).Text("");
                        t.Cell().Element(Total).AlignRight().Text(Signed(sumDiff)).FontColor(ToneColor(sumDiff));
                        t.Cell().Element(Total).Text("");
                        if (purchase) t.Cell().Element(Total).Text("");
                        t.Cell().Element(Total).AlignRight().Text(SignedMoney(sumAmount)).FontColor(ToneColor(sumAmount));
                        if (purchase) t.Cell().Element(Total).AlignRight().Text(SignedMoney(sumCost)).FontColor(ToneColor(sumCost));
                        if (recorded) t.Cell().Element(Total).Text("");
                    });

                    col.Item().ShowEntire().PaddingTop(22).Row(r =>
                    {
                        r.RelativeItem().Text(L("Провёл", "Өткөрдү", "Conducted by", "Yapan", "O'tkazdi") + " ________________________");
                        r.RelativeItem().Text(L("Проверил", "Текшерди", "Checked by", "Kontrol eden", "Tekshirdi") + " ________________________");
                        r.RelativeItem().Text(L("Дата", "Датасы", "Date", "Tarih", "Sana") + " ________________");
                    });
                });

                page.Footer().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Text(title).FontSize(7.5f).FontColor(Soft);
                    row.AutoItem().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Soft));
                        t.Span(L("стр. ", "бет ", "page ", "sayfa ", "bet "));
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        }).GeneratePdf(path);
    }
}

public enum RevisionExportFormat
{
    Excel,
    Csv,
    Pdf,
}

/// <summary>Какие строки попадут в таблицу файла.</summary>
public enum RevisionExportScope
{
    /// <summary>Весь склад (непосчитанные — с пустым фактом).</summary>
    All,
    /// <summary>Только те, у кого есть факт.</summary>
    Scanned,
    /// <summary>Только с ненулевым расхождением.</summary>
    Discrepancy,
}

public sealed record RevisionExportOptions
{
    public RevisionExportScope Scope { get; init; } = RevisionExportScope.All;

    /// <summary>Колонки «Цена закупки» и «Сумма по закупке» (по флажку в окне: закупочные цены видны не всем).</summary>
    public bool IncludePurchasePrices { get; init; }
}

/// <summary>Одна строка ревизии. Окно подставляет сюда любые данные: черновик, итог, сохранённый акт.
/// <paramref name="FactQuantity"/> = null — товар не считали. <paramref name="RecordedQuantity"/> — «записано в акт» (с поправкой на продажи);
/// колонка появится, только если хоть у одной строки оно задано.</summary>
public sealed record RevisionExportRow(
    string Name,
    string? Barcode,
    double BookQuantity,
    double? FactQuantity,
    double SalePrice,
    double? PurchasePrice = null,
    DateTime? CountedAt = null,
    double? RecordedQuantity = null);

/// <summary>Шапка файла.</summary>
public sealed record RevisionExportMeta
{
    public string? ShopName { get; init; }

    /// <summary>true — «Ревизия (черновик)», false — «Акт ревизии».</summary>
    public bool IsDraft { get; init; }

    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }

    /// <summary>Кто проводил; пусто — текущий пользователь (<see cref="PosApp.CurrentUserDisplayName"/>).</summary>
    public string? ConductedBy { get; init; }

    /// <summary>Номера актов на сервере (после «Применить»).</summary>
    public IReadOnlyList<string>? ActNumbers { get; init; }

    /// <summary>«Остатки на …» — когда обновлялся каталог перед ревизией.</summary>
    public DateTime? StockAsOf { get; init; }
}
