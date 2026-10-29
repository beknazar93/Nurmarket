using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «для аналитики как расход на начало бизнеса» — выгрузка стартовых расходов и окупаемости в Excel (.xlsx,
/// тот же построитель, что выгрузки аналитики), CSV (UTF-8 с BOM, «;», десятичная запятая — открывается в Excel по колонкам) и PDF.
/// Окупаемость (<paramref name="payback"/>) может быть null — тогда выгружается только список статей.</summary>
public static class StartupCostsExport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };
    private const string Ink = "#1B2330", Soft = "#6B7686", Rule = "#D5DAE1", Head = "#EEF1F5", Red = "#B42318";

    static StartupCostsExport()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static string ReportTitle => Tr.T("Стартовые расходы и окупаемость", "Баштапкы чыгымдар жана кайтарым", "Startup costs and payback", "Başlangıç giderleri ve geri dönüş", "Boshlang'ich xarajatlar va o'zini oqlash");

    public static string SuggestFileName(string? shopName, string extension) =>
        ExportFileName.Build(ReportTitle, DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), shopName, extension);

    private static string Money(decimal v) => v.ToString("N2", Ru) + " " + ReportLabels.Currency;

    private static string OpenedText(StartupCostsMeta meta) =>
        meta.OpenedOn is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : Tr.T("не указана", "көрсөтүлгөн эмес", "not set", "belirtilmemiş", "ko'rsatilmagan");

    private static string MonthsText(decimal months) => months.ToString("0.#", Ru) + " " + Tr.T("мес.", "ай", "mo.", "ay", "oy");

    /// <summary>Строки итогов окупаемости: подпись → значение (текст) — общие для PDF и CSV.</summary>
    private static List<(string Label, string Value)> Summary(IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, PaybackResult? p)
    {
        var rows = new List<(string, string)>
        {
            (Tr.T("Дата открытия бизнеса", "Бизнес ачылган күн", "Business opening date", "İşletme açılış tarihi", "Biznes ochilgan sana"), OpenedText(meta)),
            (Tr.T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan"), Money(StartupCostsStore.Sum(items))),
        };
        if (p is { Status: PaybackStatus.InProgress or PaybackStatus.PaidBack })
        {
            rows.Add((Tr.T("Вернулось (накопленная чистая прибыль)", "Кайтты (топтолгон таза пайда)", "Returned (cumulative net profit)", "Geri dönen (birikmiş net kâr)", "Qaytdi (jamlangan sof foyda)"), Money(p.Returned)));
            rows.Add((Tr.T("Осталось окупить", "Кайтарууга калды", "Left to pay back", "Geri dönecek kalan", "Oqlash uchun qoldi"), Money(p.Remaining)));
            rows.Add((Tr.T("Окупаемость вложений", "Салымдардын кайтарымы", "Payback of the investment", "Yatırımın geri dönüşü", "Sarmoyaning oqlanishi"), p.PercentBack.ToString("0.0", Ru) + " %"));
            if (p.AvgMonthlyNet is { } avg)
                rows.Add((Tr.T("Среднемесячная чистая прибыль (последние месяцы)", "Орточо айлык таза пайда (акыркы айлар)", "Average monthly net profit (recent months)",
                    "Aylık ortalama net kâr (son aylar)", "O'rtacha oylik sof foyda (so'nggi oylar)"), Money(avg)));
            if (p.Status == PaybackStatus.PaidBack)
                rows.Add((Tr.T("Срок окупаемости", "Кайтарым мөөнөтү", "Payback period", "Geri dönüş süresi", "Oqlanish muddati"),
                    p.PaidBackMonth is { } pm ? Tr.T("окупилось в ", "кайтты ", "paid back in ", "geri döndü: ", "oqlandi: ") + pm.ToString("MM.yyyy", CultureInfo.InvariantCulture)
                        : Tr.T("окупилось", "кайтты", "paid back", "geri döndü", "oqlandi")));
            else if (p.MonthsToPayback is { } mtp && p.ForecastDate is { } fd)
                rows.Add((Tr.T("Прогноз срока окупаемости", "Кайтарым мөөнөтүнүн божомолу", "Payback forecast", "Geri dönüş tahmini", "Oqlanish prognozi"),
                    MonthsText(mtp) + " (" + fd.ToString("MM.yyyy", CultureInfo.InvariantCulture) + ")"));
            else
                rows.Add((Tr.T("Прогноз срока окупаемости", "Кайтарым мөөнөтүнүн божомолу", "Payback forecast", "Geri dönüş tahmini", "Oqlanish prognozi"),
                    Tr.T("нет — прибыль за последние месяцы не положительная или данных мало", "жок — акыркы айлардагы пайда оң эмес же маалымат аз",
                        "none — recent profit is not positive or there is too little data", "yok — son aylardaki kâr pozitif değil veya veri az", "yo'q — so'nggi oylardagi foyda musbat emas yoki ma'lumot kam")));
            if (meta.Amortize)
                rows.Add((Tr.T($"Амортизация: вложения списываются равномерно на {meta.AmortMonths} мес., списано к сегодня", $"Амортизация: салымдар {meta.AmortMonths} айга бирдей эсептен чыгат, бүгүнкүгө чейин",
                    $"Amortization: spread evenly over {meta.AmortMonths} months, written off to date", $"Amortisman: {meta.AmortMonths} aya eşit dağıtılır, bugüne kadar", $"Amortizatsiya: {meta.AmortMonths} oyga teng bo'linadi, bugungacha"), Money(p.AmortizedToDate)));
        }
        return rows;
    }

    // ───────────────────────────── Excel ─────────────────────────────

    public static void ToExcel(string path, IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, PaybackResult? payback, string? shopName)
    {
        var meta2 = ReportMeta.Create(ReportTitle, shopName, null);
        using var book = new ExcelWorkbookBuilder(path, meta2);
        var sheet = book.AddSheet(Tr.T("Стартовые расходы", "Баштапкы чыгымдар", "Startup costs", "Başlangıç giderleri", "Boshlang'ich xarajatlar"), ReportTitle);
        sheet.AddKeyValues(Summary(items, meta, payback).Select(r => new XlKeyValue(r.Label, r.Value)).ToList(),
            Tr.T("Показатель", "Көрсөткүч", "Indicator", "Gösterge", "Ko'rsatkich"), Tr.T("Значение", "Мааниси", "Value", "Değer", "Qiymati"));
        var cur = ReportLabels.Currency;
        var columns = new List<XlColumn>
        {
            new("№", XlKind.Index),
            new(Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.Date),
            new(Tr.T("Статья", "Берене", "Item", "Kalem", "Modda"), XlKind.Text),
            new(Tr.T("Категория", "Категория", "Category", "Kategori", "Toifa"), XlKind.Text),
            new(Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa") + ", " + cur, XlKind.Money, XlTotal.Sum),
            new(Tr.T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh"), XlKind.Text),
        };
        var n = 0;
        sheet.AddTable(columns, items.Select(i => new object?[] { ++n, i.Date, i.Title, StartupCostsStore.CategoryTitle(i.Category), i.Amount, i.Comment }).ToList());

        if (payback is { Rows.Count: > 0 })
        {
            var pay = book.AddSheet(Tr.T("Окупаемость", "Кайтарым", "Payback", "Geri dönüş", "Oqlanish"),
                Tr.T("Окупаемость вложений по месяцам", "Салымдардын кайтарымы айлар боюнча", "Payback by month", "Aylara göre geri dönüş", "Oylar bo'yicha oqlanish"),
                Tr.T("Чистая прибыль — по отчёту «Прибыль и деньги» (валовая прибыль − расходы NurCRM). Стартовые расходы в неё не входят.",
                    "Таза пайда — «Пайда жана акча» отчету боюнча (дүң пайда − NurCRM чыгымдары). Баштапкы чыгымдар анын ичине кирбейт.",
                    "Net profit is from the “Profit & cash” report (gross profit − NurCRM expenses). Startup costs are not part of it.",
                    "Net kâr «Kâr ve nakit» raporundandır (brüt kâr − NurCRM giderleri). Başlangıç giderleri buna dahil değildir.",
                    "Sof foyda «Foyda va pul» hisobotidan (yalpi foyda − NurCRM xarajatlari). Boshlang'ich xarajatlar unga kirmaydi."));
            var withAmort = meta.Amortize;
            var pcols = new List<XlColumn>
            {
                new(Tr.T("Месяц", "Ай", "Month", "Ay", "Oy"), XlKind.Text),
                new(Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda") + ", " + cur, XlKind.Money, XlTotal.Sum),
                new(Tr.T("Накоплено", "Топтолгон", "Cumulative", "Birikmiş", "Jamlangan") + ", " + cur, XlKind.Money),
                new(Tr.T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan") + ", " + cur, XlKind.Money),
            };
            if (withAmort)
            {
                pcols.Add(new(Tr.T("Амортизация", "Амортизация", "Amortization", "Amortisman", "Amortizatsiya") + ", " + cur, XlKind.Money, XlTotal.Sum));
                pcols.Add(new(Tr.T("Чистая с учётом амортизации", "Амортизацияны эске алганда таза", "Net after amortization", "Amortisman sonrası net", "Amortizatsiyadan keyingi sof") + ", " + cur, XlKind.Money, XlTotal.Sum));
            }
            var table = pay.AddTable(pcols, payback.Rows.Select(r => withAmort
                ? new object?[] { r.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture), r.Net, r.Cumulative, r.Invested, r.Amortization, r.NetAfterAmortization }
                : new object?[] { r.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture), r.Net, r.Cumulative, r.Invested }).ToList());
            if (payback.Rows.Count >= 2)
                pay.AddBarChart(Tr.T("Накопленная чистая прибыль по месяцам", "Топтолгон таза пайда айлар боюнча", "Cumulative net profit by month", "Aylara göre birikmiş net kâr", "Oylar bo'yicha jamlangan sof foyda"),
                    table, 0, 2, Math.Min(payback.Rows.Count, 36));
        }
        book.Save();
    }

    // ───────────────────────────── CSV ─────────────────────────────

    public static void ToCsv(string path, IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, PaybackResult? payback)
    {
        const string sep = ";";
        var text = new StringBuilder();
        text.AppendLine(Cell(ReportTitle));
        foreach (var (label, value) in Summary(items, meta, payback))
            text.AppendLine(Cell(label) + sep + Cell(value));
        text.AppendLine();
        text.AppendLine(string.Join(sep, Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), Tr.T("Статья", "Берене", "Item", "Kalem", "Modda"),
            Tr.T("Категория", "Категория", "Category", "Kategori", "Toifa"), Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), Tr.T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh")));
        foreach (var i in items)
            text.AppendLine(string.Join(sep, i.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), Cell(i.Title), Cell(StartupCostsStore.CategoryTitle(i.Category)),
                i.Amount.ToString("0.00", Ru), Cell(i.Comment ?? "")));
        if (payback is { Rows.Count: > 0 })
        {
            text.AppendLine();
            var header = new List<string>
            {
                Tr.T("Месяц", "Ай", "Month", "Ay", "Oy"), Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda"),
                Tr.T("Накоплено", "Топтолгон", "Cumulative", "Birikmiş", "Jamlangan"), Tr.T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan"),
            };
            if (meta.Amortize)
            {
                header.Add(Tr.T("Амортизация", "Амортизация", "Amortization", "Amortisman", "Amortizatsiya"));
                header.Add(Tr.T("Чистая с учётом амортизации", "Амортизацияны эске алганда таза", "Net after amortization", "Amortisman sonrası net", "Amortizatsiyadan keyingi sof"));
            }
            text.AppendLine(string.Join(sep, header));
            foreach (var r in payback.Rows)
            {
                var cells = new List<string> { r.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture), r.Net.ToString("0.00", Ru), r.Cumulative.ToString("0.00", Ru), r.Invested.ToString("0.00", Ru) };
                if (meta.Amortize)
                {
                    cells.Add(r.Amortization.ToString("0.00", Ru));
                    cells.Add(r.NetAfterAmortization.ToString("0.00", Ru));
                }
                text.AppendLine(string.Join(sep, cells));
            }
        }
        // UTF-8 с меткой порядка байтов: без неё Excel открывает кириллицу знаками вопроса.
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Ячейка CSV: разделитель, кавычки и переносы — в кавычках; текст, начинающийся с «=», «+», «-», «@», получает апостроф
    /// (иначе Excel выполнил бы его как формулу).</summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return value.Contains(';', StringComparison.Ordinal) || value.IndexOfAny(['"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    // ───────────────────────────── PDF ─────────────────────────────

    public static void ToPdf(string path, IReadOnlyList<StartupCostItem> items, StartupCostsMeta meta, PaybackResult? payback, string? shopName)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var summary = Summary(items, meta, payback);
        var generated = ReportLabels.Generated + ": " + DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(16, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(9.5f).FontColor(Ink));
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span(generated + "   ").FontSize(8).FontColor(Soft);
                    t.CurrentPageNumber().FontSize(8).FontColor(Soft);
                });
                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Text(ReportTitle).FontSize(16).Bold();
                    if (!string.IsNullOrWhiteSpace(shopName))
                        col.Item().Text(shopName!.Trim()).FontSize(10).FontColor(Soft);

                    col.Item().Column(sum =>
                    {
                        foreach (var (label, value) in summary)
                            sum.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(3).Row(r =>
                            {
                                r.RelativeItem().Text(label);
                                r.AutoItem().Text(value).Bold();
                            });
                    });

                    col.Item().PaddingTop(6).Text(Tr.T("Статьи вложений", "Салым беренелери", "Investment items", "Yatırım kalemleri", "Sarmoya moddalari")).FontSize(12).Bold();
                    if (items.Count == 0)
                        col.Item().Text(Tr.T("Статей нет.", "Беренелер жок.", "No items.", "Kalem yok.", "Moddalar yo'q.")).FontColor(Soft);
                    else
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(62);
                                c.RelativeColumn(3);
                                c.RelativeColumn(2.2f);
                                c.ConstantColumn(84);
                            });
                            table.Header(h =>
                            {
                                foreach (var (text, right) in new[]
                                         {
                                             (Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), false), (Tr.T("Статья", "Берене", "Item", "Kalem", "Modda"), false),
                                             (Tr.T("Категория", "Категория", "Category", "Kategori", "Toifa"), false), (Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), true),
                                         })
                                {
                                    var c = h.Cell().Background(Head).PaddingVertical(4).PaddingHorizontal(4);
                                    (right ? c.AlignRight() : c).Text(text).Bold().FontSize(9);
                                }
                            });
                            foreach (var i in items)
                            {
                                table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).Text(i.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
                                table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).Column(c =>
                                {
                                    c.Item().Text(i.Title);
                                    if (!string.IsNullOrWhiteSpace(i.Comment))
                                        c.Item().Text(i.Comment).FontSize(8).FontColor(Soft);
                                });
                                table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).Text(StartupCostsStore.CategoryTitle(i.Category));
                                table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4).AlignRight().Text(i.Amount.ToString("N2", Ru));
                            }
                        });

                    if (payback is { Rows.Count: > 0 })
                    {
                        col.Item().PaddingTop(8).Text(Tr.T("Окупаемость по месяцам", "Кайтарым айлар боюнча", "Payback by month", "Aylara göre geri dönüş", "Oylar bo'yicha oqlanish")).FontSize(12).Bold();
                        col.Item().Table(table =>
                        {
                            var cols = meta.Amortize ? 6 : 4;
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(54);
                                for (var k = 1; k < cols; k++)
                                    c.RelativeColumn();
                            });
                            var heads = new List<string>
                            {
                                Tr.T("Месяц", "Ай", "Month", "Ay", "Oy"), Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda"),
                                Tr.T("Накоплено", "Топтолгон", "Cumulative", "Birikmiş", "Jamlangan"), Tr.T("Вложено", "Салынды", "Invested", "Yatırılan", "Kiritilgan"),
                            };
                            if (meta.Amortize)
                            {
                                heads.Add(Tr.T("Амортизация", "Амортизация", "Amortization", "Amortisman", "Amortizatsiya"));
                                heads.Add(Tr.T("С амортизацией", "Амортизация менен", "After amortization", "Amortisman sonrası", "Amortizatsiya bilan"));
                            }
                            table.Header(h =>
                            {
                                for (var k = 0; k < heads.Count; k++)
                                {
                                    var c = h.Cell().Background(Head).PaddingVertical(4).PaddingHorizontal(4);
                                    (k == 0 ? c : c.AlignRight()).Text(heads[k]).Bold().FontSize(8.5f);
                                }
                            });
                            foreach (var r in payback.Rows)
                            {
                                var cells = new List<(string, bool)>
                                {
                                    (r.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture), false), (r.Net.ToString("N2", Ru), r.Net < 0m), (r.Cumulative.ToString("N2", Ru), r.Cumulative < 0m),
                                    (r.Invested.ToString("N2", Ru), false),
                                };
                                if (meta.Amortize)
                                {
                                    cells.Add((r.Amortization.ToString("N2", Ru), false));
                                    cells.Add((r.NetAfterAmortization.ToString("N2", Ru), r.NetAfterAmortization < 0m));
                                }
                                for (var k = 0; k < cells.Count; k++)
                                {
                                    var c = table.Cell().BorderBottom(0.5f).BorderColor(Rule).Padding(4);
                                    var t = (k == 0 ? c : c.AlignRight()).Text(cells[k].Item1).FontSize(8.5f);
                                    if (cells[k].Item2)
                                        t.FontColor(Red);
                                }
                            }
                        });
                        col.Item().Text(Tr.T("Чистая прибыль — по отчёту «Прибыль и деньги» (валовая прибыль − расходы NurCRM). Стартовые расходы в неё не входят.",
                            "Таза пайда — «Пайда жана акча» отчету боюнча (дүң пайда − NurCRM чыгымдары). Баштапкы чыгымдар анын ичине кирбейт.",
                            "Net profit is from the “Profit & cash” report (gross profit − NurCRM expenses). Startup costs are not part of it.",
                            "Net kâr «Kâr ve nakit» raporundandır (brüt kâr − NurCRM giderleri). Başlangıç giderleri buna dahil değildir.",
                            "Sof foyda «Foyda va pul» hisobotidan (yalpi foyda − NurCRM xarajatlari). Boshlang'ich xarajatlar unga kirmaydi.")).FontSize(8).FontColor(Soft);
                    }
                });
            });
        }).GeneratePdf(path);
    }
}
