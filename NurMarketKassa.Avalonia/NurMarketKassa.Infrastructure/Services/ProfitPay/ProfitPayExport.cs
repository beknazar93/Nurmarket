using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи» — расчётная ведомость с новыми схемами:
/// Excel (книга из двух листов: «Ведомость» и «Как посчитано»), CSV (для бухгалтерии, «;», UTF-8 с BOM) и PDF. Цифры берутся из
/// <see cref="ProfitPayResult"/> один в один — выгрузка ничего не пересчитывает.</summary>
public static class ProfitPayExport
{
    /// <summary>Строка ведомости: что насчитал сервер по схеме сотрудника + надбавка от прибыли.</summary>
    public sealed record Row(string UserId, string Name, string SchemeText, decimal Server, decimal Profit, string Note, IReadOnlyList<string> Steps)
    {
        public decimal Total => Server + Profit;
    }

    private static string Cur => Tr.T("сом", "сом", "som", "som", "so'm");
    private static string Title => Tr.T("Зарплата от прибыли — ведомость", "Пайдадан эмгек акы — ведомость", "Profit-based pay — statement", "Kârdan maaş — bordro", "Foydadan ish haqi — qaydnoma");

    public static string SuggestFileName(DateTime from, DateTime to, string? shop, string ext) =>
        ExportFileName.Build(Tr.T("Зарплата от прибыли", "Пайдадан эмгек акы", "Profit pay", "Kârdan maaş", "Foydadan ish haqi"), ExportFileName.Period(from, to), shop, ext);

    private static IEnumerable<(string Label, string Value)> ShopFacts(ProfitPayResult r)
    {
        yield return (Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), ProfitPayCalculator.M(r.Revenue));
        yield return (Tr.T("Закупочная цена проданного", "Сатылгандын сатып алуу баасы", "Purchase cost of goods sold", "Satılanların alış fiyatı", "Sotilganlarning xarid narxi"), ProfitPayCalculator.M(r.Cogs));
        yield return (Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"), ProfitPayCalculator.M(r.Gross));
        yield return (Tr.T("Возвраты (вычтены)", "Кайтаруулар (алынды)", "Refunds (subtracted)", "İadeler (düşüldü)", "Qaytarishlar (ayirildi)"), ProfitPayCalculator.M(r.Returns));
        yield return (Tr.T("Чистая маржа", "Таза маржа", "Net margin", "Net marj", "Sof marja"), ProfitPayCalculator.M(r.Margin));
        foreach (var (t, a) in r.ExpensesSubtracted)
            yield return ("− " + t, ProfitPayCalculator.M(a));
        foreach (var (t, a) in r.ExpensesSkipped)
            yield return (t + " (" + Tr.T("не вычитается", "алынбайт", "not subtracted", "düşülmez", "ayirilmaydi") + ")", ProfitPayCalculator.M(a));
        yield return (Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda"), ProfitPayCalculator.M(r.NetProfit));
    }

    private static string[] Header() =>
    [
        Tr.T("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim"),
        Tr.T("Схема сервера", "Сервер схемасы", "Server scheme", "Sunucu şeması", "Server sxemasi"),
        Tr.T("Начислено по схеме, сом", "Схема боюнча эсептелди, сом", "Accrued by scheme, som", "Şemaya göre tahakkuk, som", "Sxema bo'yicha hisoblangan, so'm"),
        Tr.T("От прибыли, сом", "Пайдадан, сом", "From profit, som", "Kârdan, som", "Foydadan, so'm"),
        Tr.T("Итого, сом", "Жыйынтыгы, сом", "Total, som", "Toplam, som", "Jami, so'm"),
        Tr.T("Пометка", "Белги", "Note", "Not", "Izoh"),
    ];

    // ───────────────────────────── CSV ─────────────────────────────

    public static void ExportCsv(string path, ProfitPayResult res, IReadOnlyList<Row> rows, DateTime from, DateTime to)
    {
        var sb = new StringBuilder();
        string Q(string s) => s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        sb.AppendLine(Q(Title) + ";" + from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + " - " + to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        sb.AppendLine(string.Join(";", Header().Select(Q)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(";", new[] { Q(r.Name), Q(r.SchemeText), N(r.Server), N(r.Profit), N(r.Total), Q(r.Note) }));
        sb.AppendLine(string.Join(";", new[] { Q(ReportLabels.Total), "", N(rows.Sum(x => x.Server)), N(rows.Sum(x => x.Profit)), N(rows.Sum(x => x.Total)), "" }));
        sb.AppendLine();
        foreach (var (l, v) in ShopFacts(res))
            sb.AppendLine(Q(l) + ";" + Q(v));
        sb.AppendLine();
        sb.AppendLine(Q(Tr.T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi")));
        foreach (var s in res.ShopSteps)
            sb.AppendLine(Q(s));
        foreach (var r in rows.Where(x => x.Steps.Count > 0))
        {
            sb.AppendLine(Q(r.Name));
            foreach (var s in r.Steps)
                sb.AppendLine(";" + Q(s));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ───────────────────────────── Excel ─────────────────────────────

    public static void ExportExcel(string path, ProfitPayResult res, IReadOnlyList<Row> rows, DateTime from, DateTime to, string? shop)
    {
        var meta = ReportMeta.Create(Title, shop, ExportFileName.Period(from, to));
        using var book = new ExcelWorkbookBuilder(path, meta);
        var sheet = book.AddSheet(Tr.T("Ведомость", "Ведомость", "Statement", "Bordro", "Qaydnoma"), Title, landscape: true);
        var h = Header();
        sheet.AddTable(
        [
            new XlColumn(h[0], MaxWidth: 36), new XlColumn(h[1], MaxWidth: 40),
            new XlColumn(h[2], XlKind.Money, XlTotal.Sum), new XlColumn(h[3], XlKind.Money, XlTotal.Sum), new XlColumn(h[4], XlKind.Money, XlTotal.Sum),
            new XlColumn(h[5], MaxWidth: 70),
        ],
        rows.Select(r => new object?[] { r.Name, r.SchemeText, (double)r.Server, (double)r.Profit, (double)r.Total, r.Note }));
        sheet.AddHeading(Tr.T("Результат магазина", "Дүкөндүн жыйынтыгы", "Shop result", "Mağaza sonucu", "Do'kon natijasi"));
        sheet.AddKeyValues(ShopFacts(res).Select(f => new XlKeyValue(f.Label, f.Value)).ToList());

        var how = book.AddSheet(Tr.T("Как посчитано", "Кантип эсептелди", "How calculated", "Nasıl hesaplandı", "Qanday hisoblandi"), Tr.T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi"));
        var lines = new List<object?[]>();
        foreach (var s in res.ShopSteps)
            lines.Add([Tr.T("Магазин", "Дүкөн", "Shop", "Mağaza", "Do'kon"), s]);
        foreach (var r in rows)
            foreach (var s in r.Steps)
                lines.Add([r.Name, s]);
        how.AddTable([new XlColumn(h[0], MaxWidth: 36), new XlColumn(Tr.T("Шаг расчёта", "Эсептөө кадамы", "Calculation step", "Hesap adımı", "Hisob qadami"), MaxWidth: 120)], lines, totals: false);
        book.Save();
    }

    // ───────────────────────────── PDF ─────────────────────────────

    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };

    public static void ExportPdf(string path, ProfitPayResult res, IReadOnlyList<Row> rows, DateTime from, DateTime to, string? shop)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var h = Header();
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(14, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(9).FontColor("#1B2330"));
            page.Content().Column(col =>
            {
                col.Spacing(6);
                col.Item().Text(Title).FontSize(15).Bold();
                col.Item().Text((string.IsNullOrWhiteSpace(shop) ? "" : shop + "   ·   ") + ReportLabels.Period + ": " + ExportFileName.Period(from, to)).FontColor("#6B7686");
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(3); d.RelativeColumn(2); d.RelativeColumn(2); d.RelativeColumn(2); d.RelativeColumn(4); });
                    foreach (var x in h)
                        t.Cell().Background("#1F4E78").Padding(3).Text(x).FontColor("#FFFFFF").Bold().FontSize(8);
                    void Cell(string s, bool right = false, bool bold = false)
                    {
                        var e = t.Cell().BorderBottom(0.5f).BorderColor("#C9D3E0").Padding(3);
                        var tx = (right ? e.AlignRight() : e).Text(s).FontSize(8.5f);
                        if (bold) tx.Bold();
                    }
                    foreach (var r in rows)
                    {
                        Cell(r.Name); Cell(r.SchemeText); Cell(ProfitPayCalculator.M(r.Server), true); Cell(ProfitPayCalculator.M(r.Profit), true); Cell(ProfitPayCalculator.M(r.Total), true, true); Cell(r.Note);
                    }
                    Cell(ReportLabels.Total, bold: true); Cell("");
                    Cell(ProfitPayCalculator.M(rows.Sum(x => x.Server)), true, true); Cell(ProfitPayCalculator.M(rows.Sum(x => x.Profit)), true, true); Cell(ProfitPayCalculator.M(rows.Sum(x => x.Total)), true, true); Cell("");
                });
                col.Item().PaddingTop(6).Text(Tr.T("Результат магазина", "Дүкөндүн жыйынтыгы", "Shop result", "Mağaza sonucu", "Do'kon natijasi")).Bold().FontSize(11);
                foreach (var (l, v) in ShopFacts(res))
                    col.Item().Row(r => { r.RelativeItem().Text(l); r.ConstantItem(120).AlignRight().Text(v); });
                col.Item().PaddingTop(6).Text(Tr.T("Как посчитано", "Кантип эсептелди", "How it was calculated", "Nasıl hesaplandı", "Qanday hisoblandi")).Bold().FontSize(11);
                foreach (var s in res.ShopSteps)
                    col.Item().Text(s).FontSize(8.5f);
                foreach (var r in rows.Where(x => x.Steps.Count > 0))
                {
                    col.Item().PaddingTop(3).Text(r.Name).Bold().FontSize(9);
                    foreach (var s in r.Steps)
                        col.Item().PaddingLeft(8).Text(s).FontSize(8.5f);
                }
            });
        })).GeneratePdf(path);
    }
}
