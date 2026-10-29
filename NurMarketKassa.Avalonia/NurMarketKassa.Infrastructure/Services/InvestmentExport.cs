using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>Строка ведомости выплат инвестору за месяц.</summary>
public sealed record StatementRow(string MonthKey, DateTime From, decimal Net, decimal Base, decimal Carried, string Stage, decimal Pay, decimal Tax,
    decimal ToHand, decimal Owner, decimal CutByCap, bool Paid, DateTime? PaidOn);

/// <summary>Общие подписи модуля «Инвестиции и окупаемость».</summary>
public static class InvestmentText
{
    public static string Som => ReportLabels.Currency;

    public static string Money(decimal v) => v.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " " + Som;

    public static string StageName(InvestorTerms t, int stage) => stage >= t.Stages.Count
        ? Tr.T("Выплаты завершены", "Төлөмдөр аяктады", "Payouts finished", "Ödemeler tamamlandı", "To'lovlar tugadi")
        : Tr.T($"Этап {stage + 1} · {t.Stages[stage].Pct:0.##}%", $"{stage + 1}-этап · {t.Stages[stage].Pct:0.##}%", $"Stage {stage + 1} · {t.Stages[stage].Pct:0.##}%",
            $"{stage + 1}. aşama · %{t.Stages[stage].Pct:0.##}", $"{stage + 1}-bosqich · {t.Stages[stage].Pct:0.##}%");

    public static string MonthTitle(string monthKey)
    {
        var d = DateTime.ParseExact(monthKey + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return d.ToString("MM.yyyy", CultureInfo.InvariantCulture);
    }

    public static string BaseTitle(string kind) => kind switch
    {
        "revenue" => Tr.T("выручка", "түшүм", "revenue", "ciro", "tushum"),
        "gross" => Tr.T("валовая прибыль", "дүң пайда", "gross profit", "brüt kâr", "yalpi foyda"),
        _ => Tr.T("чистая прибыль", "таза пайда", "net profit", "net kâr", "sof foyda"),
    };

    public static string ExpenseTitle(string key) => key switch
    {
        "salaries" => Tr.T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi"),
        "rent" => Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara"),
        "taxes" => Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar"),
        "utilities" => Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal"),
        _ => Tr.T("Прочие расходы", "Башка чыгымдар", "Other expenses", "Diğer giderler", "Boshqa xarajatlar"),
    };

    /// <summary>Ведомость инвестора: месяцы с даты его вложения.</summary>
    public static List<StatementRow> Statement(InvestorResult r, IReadOnlyList<PayMark> payments) =>
        r.Rows.Where(x => x.To.Date >= r.Investor.Date.Date).Select(x =>
        {
            var mark = payments.FirstOrDefault(p => p.Id == PayMark.KeyOf(r.Investor.Id, x.MonthKey) && !p.Deleted);
            return new StatementRow(x.MonthKey, x.From, x.Net, x.Base, x.Carried, StageName(r.Investor.Terms, x.StageBefore), x.Pay, x.Tax, x.ToHand,
                x.Net - x.Pay, x.CutByCap, mark != null, mark?.PaidOn);
        }).ToList();
}

/// <summary>2026-10-29, владелец: «ведомость выплат инвесторам; Excel/CSV/PDF» — раздел «Инвестиции и окупаемость». Только записывает файлы.
/// Цифры — те, что на экране (<see cref="InvestmentAnalysis"/>): чистая прибыль — отчёт сервера, выплаты не больше чистой прибыли месяца.</summary>
public static class InvestmentExport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };
    private const string Ink = "#1B2330", Soft = "#6B7686", RuleColor = "#D5DAE1", Head = "#EEF1F5", Red = "#B42318";

    static InvestmentExport()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string Title => Tr.T("Ведомость выплат инвесторам", "Инвесторлорго төлөмдөрдүн ведомосту", "Investor payout statement", "Yatırımcı ödeme çizelgesi", "Investorlarga to'lovlar qaydnomasi");

    public static string SuggestFileName(string? shop, string extension) =>
        ExportFileName.Build(Title, DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), shop, extension);

    private static string StatusText(StatementRow r) => r.Paid
        ? Tr.T("Выплачено", "Төлөндү", "Paid", "Ödendi", "To'landi")
        : Tr.T("Начислено", "Эсептелди", "Accrued", "Tahakkuk", "Hisoblandi");

    private static string[] Headers() => new[]
    {
        Tr.T("Месяц", "Ай", "Month", "Ay", "Oy"), Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda"),
        Tr.T("База расчёта", "Эсептөө базасы", "Base", "Hesap tabanı", "Hisob bazasi"), Tr.T("Перенос убытка", "Зыянды ташуу", "Loss carried", "Taşınan zarar", "Zarar ko'chirildi"),
        Tr.T("Этап", "Этап", "Stage", "Aşama", "Bosqich"), Tr.T("Начислено инвестору", "Инвесторго эсептелди", "Accrued to investor", "Yatırımcıya tahakkuk", "Investorga hisoblandi"),
        Tr.T("Налог", "Салык", "Tax", "Vergi", "Soliq"), Tr.T("К выплате", "Төлөнүүчү", "To pay", "Ödenecek", "To'lanadi"),
        Tr.T("Владельцу", "Ээсине", "To owner", "Sahibe", "Egasiga"), Tr.T("Статус", "Абалы", "Status", "Durum", "Holat"),
    };

    private static string SheetName(InvestorItem i, int n)
    {
        var nm = string.Concat(i.Name.Where(c => !"[]:*?/\\".Contains(c))).Trim();
        if (nm.Length == 0)
            nm = "Investor";
        return (nm.Length > 24 ? nm[..24] : nm) + " " + n;
    }

    // ───────────────────────────── Excel ─────────────────────────────

    public static void ToExcel(string path, InvestmentResult result, IReadOnlyList<PayMark> payments, string? shopName)
    {
        using var book = new ExcelWorkbookBuilder(path, ReportMeta.Create(Title, shopName, null));
        var cur = ReportLabels.Currency;
        var n = 0;
        foreach (var r in result.Investors)
        {
            n++;
            var sheet = book.AddSheet(SheetName(r.Investor, n), Title + " · " + r.Investor.Name,
                Tr.T($"Вложение {InvestmentText.Money(r.Invested)} от {r.Investor.Date:dd.MM.yyyy}. Условия: версия {r.Investor.Terms.Version}, база — {InvestmentText.BaseTitle(r.Investor.Terms.Base)}.",
                    $"Салым {InvestmentText.Money(r.Invested)}, {r.Investor.Date:dd.MM.yyyy}. Шарттар: {r.Investor.Terms.Version}-версия, база — {InvestmentText.BaseTitle(r.Investor.Terms.Base)}.",
                    $"Investment {InvestmentText.Money(r.Invested)} on {r.Investor.Date:dd.MM.yyyy}. Terms: version {r.Investor.Terms.Version}, base — {InvestmentText.BaseTitle(r.Investor.Terms.Base)}.",
                    $"Yatırım {InvestmentText.Money(r.Invested)}, {r.Investor.Date:dd.MM.yyyy}. Koşullar: sürüm {r.Investor.Terms.Version}, taban — {InvestmentText.BaseTitle(r.Investor.Terms.Base)}.",
                    $"Sarmoya {InvestmentText.Money(r.Invested)}, {r.Investor.Date:dd.MM.yyyy}. Shartlar: {r.Investor.Terms.Version}-versiya, baza — {InvestmentText.BaseTitle(r.Investor.Terms.Base)}."));
            var h = Headers();
            var cols = new List<XlColumn>
            {
                new(h[0]), new(h[1] + ", " + cur, XlKind.Money, XlTotal.Sum), new(h[2] + ", " + cur, XlKind.Money, XlTotal.Sum), new(h[3] + ", " + cur, XlKind.Money),
                new(h[4]), new(h[5] + ", " + cur, XlKind.Money, XlTotal.Sum), new(h[6] + ", " + cur, XlKind.Money, XlTotal.Sum), new(h[7] + ", " + cur, XlKind.Money, XlTotal.Sum),
                new(h[8] + ", " + cur, XlKind.Money, XlTotal.Sum), new(h[9]),
            };
            sheet.AddTable(cols, InvestmentText.Statement(r, payments).Select(x => new object?[]
            {
                InvestmentText.MonthTitle(x.MonthKey), x.Net, x.Base, x.Carried, x.Stage, x.Pay, x.Tax, x.ToHand, x.Owner, StatusText(x),
            }).ToList());
        }
        if (n == 0)
            book.AddSheet(Title, Title, Tr.T("Инвесторов нет.", "Инвесторлор жок.", "No investors.", "Yatırımcı yok.", "Investorlar yo'q."));
        book.Save();
    }

    // ───────────────────────────── CSV ─────────────────────────────

    public static void ToCsv(string path, InvestmentResult result, IReadOnlyList<PayMark> payments)
    {
        const string sep = ";";
        var sb = new StringBuilder();
        sb.AppendLine(Cell(Title));
        foreach (var r in result.Investors)
        {
            sb.AppendLine();
            sb.AppendLine(Cell(r.Investor.Name) + sep + r.Invested.ToString("0.00", Ru) + sep + r.Investor.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Join(sep, Headers()));
            foreach (var x in InvestmentText.Statement(r, payments))
                sb.AppendLine(string.Join(sep, InvestmentText.MonthTitle(x.MonthKey), x.Net.ToString("0.00", Ru), x.Base.ToString("0.00", Ru), x.Carried.ToString("0.00", Ru),
                    Cell(x.Stage), x.Pay.ToString("0.00", Ru), x.Tax.ToString("0.00", Ru), x.ToHand.ToString("0.00", Ru), x.Owner.ToString("0.00", Ru), StatusText(x)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return value.Contains(';', StringComparison.Ordinal) || value.IndexOfAny(['"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ───────────────────────────── PDF ─────────────────────────────

    public static void ToPdf(string path, InvestmentResult result, IReadOnlyList<PayMark> payments, string? shopName)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var generated = ReportLabels.Generated + ": " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(12, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(8.5f).FontColor(Ink));
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span(generated + "   ").FontSize(7.5f).FontColor(Soft);
                    t.CurrentPageNumber().FontSize(7.5f).FontColor(Soft);
                });
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    col.Item().Text(Title).FontSize(15).Bold();
                    if (!string.IsNullOrWhiteSpace(shopName))
                        col.Item().Text(shopName!.Trim()).FontSize(9.5f).FontColor(Soft);
                    if (result.Investors.Count == 0)
                        col.Item().Text(Tr.T("Инвесторов нет.", "Инвесторлор жок.", "No investors.", "Yatırımcı yok.", "Investorlar yo'q.")).FontColor(Soft);
                    foreach (var r in result.Investors)
                    {
                        col.Item().PaddingTop(8).Text(r.Investor.Name + " · " + InvestmentText.Money(r.Invested) + " · " + r.Investor.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).FontSize(11).Bold();
                        var rows = InvestmentText.Statement(r, payments);
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(46);
                                for (var k = 0; k < 4; k++) c.RelativeColumn();
                                c.RelativeColumn(1.2f);
                                for (var k = 0; k < 4; k++) c.RelativeColumn();
                            });
                            var heads = Headers();
                            table.Header(h =>
                            {
                                for (var k = 0; k < heads.Length; k++)
                                {
                                    var c = h.Cell().Background(Head).PaddingVertical(3).PaddingHorizontal(3);
                                    (k is 0 or 4 or 9 ? c : c.AlignRight()).Text(heads[k]).Bold().FontSize(7.5f);
                                }
                            });
                            foreach (var x in rows)
                            {
                                var vals = new[]
                                {
                                    InvestmentText.MonthTitle(x.MonthKey), x.Net.ToString("N2", Ru), x.Base.ToString("N2", Ru), x.Carried > 0 ? x.Carried.ToString("N2", Ru) : "—", x.Stage,
                                    x.Pay.ToString("N2", Ru), x.Tax.ToString("N2", Ru), x.ToHand.ToString("N2", Ru), x.Owner.ToString("N2", Ru), StatusText(x),
                                };
                                for (var k = 0; k < vals.Length; k++)
                                {
                                    var c = table.Cell().BorderBottom(0.5f).BorderColor(RuleColor).Padding(3);
                                    var t = (k is 0 or 4 or 9 ? c : c.AlignRight()).Text(vals[k]).FontSize(8);
                                    if (k == 1 && x.Net < 0m)
                                        t.FontColor(Red);
                                }
                            }
                            var tot = new[]
                            {
                                Tr.T("Итого", "Жалпы", "Total", "Toplam", "Jami"), rows.Sum(x => x.Net).ToString("N2", Ru), rows.Sum(x => x.Base).ToString("N2", Ru), "", "",
                                rows.Sum(x => x.Pay).ToString("N2", Ru), rows.Sum(x => x.Tax).ToString("N2", Ru), rows.Sum(x => x.ToHand).ToString("N2", Ru), rows.Sum(x => x.Owner).ToString("N2", Ru), "",
                            };
                            for (var k = 0; k < tot.Length; k++)
                            {
                                var c = table.Cell().Background(Head).Padding(3);
                                (k is 0 or 4 or 9 ? c : c.AlignRight()).Text(tot[k]).Bold().FontSize(8);
                            }
                        });
                    }
                    col.Item().PaddingTop(6).Text(Tr.T("Чистая прибыль — по отчёту «Прибыль и деньги». Выплата за месяц не больше чистой прибыли месяца. Стартовые расходы в прибыль не входят.",
                        "Таза пайда — «Пайда жана акча» отчету боюнча. Айлык төлөм айдын таза пайдасынан ашпайт. Баштапкы чыгымдар пайдага кирбейт.",
                        "Net profit is from the “Profit & cash” report. A month's payout never exceeds the month's net profit. Startup costs are not part of profit.",
                        "Net kâr «Kâr ve nakit» raporundandır. Aylık ödeme o ayın net kârını aşmaz. Başlangıç giderleri kâra dahil değildir.",
                        "Sof foyda «Foyda va pul» hisobotidan. Oylik to'lov oyning sof foydasidan oshmaydi. Boshlang'ich xarajatlar foydaga kirmaydi.")).FontSize(7.5f).FontColor(Soft);
                });
            });
        }).GeneratePdf(path);
    }
}
