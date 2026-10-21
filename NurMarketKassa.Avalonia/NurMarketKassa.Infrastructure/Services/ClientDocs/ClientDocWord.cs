using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21: документ клиента в Word (.docx) — тем же построителем, что отчёты (WordReportBuilder): реквизиты, таблица с
/// повторяющейся шапкой, итог, сумма прописью, подписи. Для правки в Word у клиента; вид — деловой, без шаблонов PDF.</summary>
public static class ClientDocWord
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static void Save(ClientDocRenderer.Input input, string path)
    {
        var d = input.Doc;
        var s = input.Seller;
        var ky = string.Equals(d.Lang, "ky", StringComparison.OrdinalIgnoreCase);
        string T(string ru, string k) => ky ? k : ru;
        var title = d.Kind switch
        {
            ClientDocKind.Quote => T("Коммерческое предложение", "Коммерциялык сунуш"),
            ClientDocKind.Invoice => T("Счёт на оплату", "Төлөм үчүн эсеп"),
            _ => T("Расходная накладная", "Чыгым накладнойу"),
        } + " № " + d.Number;
        var meta = ReportMeta.Create(title, s.Name, d.Date.ToString("dd.MM.yyyy", Ru));
        using var doc = new WordReportBuilder(path, meta);
        doc.TitleBlock();
        var details = new List<(string, string)>
        {
            (d.Kind == ClientDocKind.Waybill ? T("Отправитель", "Жөнөтүүчү") : T("Поставщик", "Жеткирүүчү"),
                string.Join(", ", new[] { s.Name, Pre("ИНН ", s.Inn), s.Address, s.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)))),
            (d.Kind == ClientDocKind.Waybill ? T("Получатель", "Алуучу") : T("Покупатель", "Сатып алуучу"),
                string.Join(", ", new[] { d.Client, Pre("ИНН ", d.Inn), d.Address, d.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)))),
        };
        var bank = string.Join(", ", new[] { Pre(T("р/с ", "эсеп "), s.Account), s.Bank, Pre("БИК ", s.Bik) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (bank.Length > 0)
            details.Add((T("Банк", "Банк"), bank));
        if (d.Kind == ClientDocKind.Quote)
            details.Add((T("Действует до", "Жарактуу"), d.Until.ToString("dd.MM.yyyy", Ru)));
        else if (d.Kind == ClientDocKind.Invoice)
            details.Add((T("Оплатить до", "Төлөө мөөнөтү"), d.Until.ToString("dd.MM.yyyy", Ru)));
        if (!string.IsNullOrWhiteSpace(d.BasedOn))
            details.Add((T("Основание", "Негиз"), ClientDocRenderer.Basis(d, d.Lang)));
        doc.DetailsTable(details);

        var showDiscount = d.Lines.Any(l => l.DiscountPercent is > 0);
        var columns = new List<WColumn>
        {
            new("№", 0.5, Center: true),
            new(T("Наименование", "Аталышы"), 4.2),
            new(T("Ед.", "Бирд."), 0.7, Center: true),
            new(T("Кол-во", "Саны"), 0.9, Numeric: true),
            new(T("Цена", "Баасы"), 1.2, Numeric: true),
        };
        if (showDiscount)
            columns.Add(new(T("Скидка", "Арзанд."), 0.9, Numeric: true));
        columns.Add(new(T("Сумма", "Сумма"), 1.4, Numeric: true));
        var rows = d.Lines.Select((l, i) =>
        {
            var row = new List<string> { (i + 1).ToString(CultureInfo.InvariantCulture), l.Name, l.Unit, l.Qty.ToString("0.###", Ru), l.BasePrice.ToString("N2", Ru) };
            if (showDiscount)
                row.Add(l.DiscountPercent is > 0 and var dp ? dp.ToString("0.##", Ru) + "%" : "—");
            row.Add(l.Sum.ToString("N2", Ru));
            return row.ToArray();
        }).ToList();
        var totals = new string[columns.Count];
        totals[1] = T("Итого", "Жыйынтык");
        totals[^1] = d.Subtotal.ToString("N2", Ru);
        doc.Table(columns, rows, totals);

        if (d.Discount > 0.004)
            doc.Paragraph(T("Скидка: ", "Арзандатуу: ") + Math.Min(d.Discount, d.Subtotal).ToString("N2", Ru) + " сом");
        doc.Paragraph(T("Всего: ", "Бардыгы: ") + d.Total.ToString("N2", Ru) + " сом" +
                      (d.VatPercent is > 0 and var vat ? T($", в том числе НДС {vat:0.##}% — ", $", анын ичинде КНС {vat:0.##}% — ") + d.VatAmount.ToString("N2", Ru) : T(", без НДС", ", КНСсиз")));
        doc.Paragraph(MoneyWords.Amount(d.Total, d.Lang));
        if (!string.IsNullOrWhiteSpace(d.Note))
            doc.Note(d.Note!.Trim());
        doc.Signatures(d.Kind == ClientDocKind.Waybill
            ? new[] { T("Отпустил", "Берди"), T("Получил", "Алды") }
            : new[] { T("Руководитель", "Жетекчи"), T("Бухгалтер", "Бухгалтер") });
        doc.Save();
    }

    private static string? Pre(string prefix, string? v) => string.IsNullOrWhiteSpace(v) ? null : prefix + v.Trim();
}
