namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (функция 13): Excel-выгрузка сверки выписки — листы «Итог», «Выписка» (каждая строка банка и во что она сопоставлена), «Нет в банке» (смены без поступления).
/// Через общий ExcelWorkbookBuilder, как остальные отчёты.</summary>
public static class BankReconcileExport
{
    public static string KindTitle(BankLineKind kind) => kind switch
    {
        BankLineKind.SalesMatched => Tr.T("Безнал смены", "Смена безналы", "Shift cashless", "Vardiya kartlı", "Smena naqdsiz"),
        BankLineKind.SalesWithFee => Tr.T("Безнал смены (с комиссией)", "Смена безналы (комиссия менен)", "Shift cashless (with fee)", "Vardiya kartlı (komisyonlu)", "Smena naqdsiz (komissiya bilan)"),
        BankLineKind.SupplierPayment => Tr.T("Оплата поставщику", "Жеткирүүчүгө төлөм", "Supplier payment", "Tedarikçi ödemesi", "Yetkazib beruvchiga to'lov"),
        BankLineKind.SupplierByName => Tr.T("Поставщик (по названию)", "Жеткирүүчү (аты боюнча)", "Supplier (by name)", "Tedarikçi (adından)", "Yetkazib beruvchi (nomi bo'yicha)"),
        BankLineKind.Rule => Tr.T("По правилу", "Эреже боюнча", "By rule", "Kurala göre", "Qoida bo'yicha"),
        BankLineKind.UnknownExpense => Tr.T("Расход?", "Чыгым?", "Expense?", "Gider?", "Xarajat?"),
        _ => Tr.T("Поступление?", "Түшүү?", "Income?", "Gelir?", "Kirim?"),
    };

    public static void Save(string path, ReportMeta meta, BankReconcileResult result, BankMonthSummary summary, IReadOnlyList<ShiftCashless> shifts, DateTime from, DateTime to)
    {
        using var book = new ExcelWorkbookBuilder(path, meta);
        var period = $"{from:dd.MM.yyyy} — {to:dd.MM.yyyy}";

        var total = book.AddSheet(Tr.T("Итог", "Жыйынтык", "Summary", "Özet", "Yakun"), Tr.T("Сверка банковской выписки", "Банк көчүрмөсүн салыштыруу", "Bank statement reconciliation", "Banka ekstresi mutabakatı", "Bank ko'chirmasini solishtirish"), period);
        total.AddKeyValues(
        [
            new XlKeyValue(Tr.T("Безнал по кассе", "Касса боюнча безнал", "Cashless per register", "Kasaya göre kartlı", "Kassa bo'yicha naqdsiz"), summary.Kassa, XlKind.Money),
            new XlKeyValue(Tr.T("По банку", "Банк боюнча", "Per bank", "Bankaya göre", "Bank bo'yicha"), summary.Bank, XlKind.Money),
            new XlKeyValue(Tr.T("Разница (касса − банк)", "Айырма (касса − банк)", "Difference (register − bank)", "Fark (kasa − banka)", "Farq (kassa − bank)"), summary.Difference, XlKind.Money,
                Math.Abs(summary.Difference) > 0.5 ? ReportTone.Bad : ReportTone.Good),
            new XlKeyValue(Tr.T("Смен не найдено в банке", "Банктан табылбаган смендер", "Shifts not found in the bank", "Bankada bulunmayan vardiya", "Bankda topilmagan smenalar"), summary.ShiftsNotFound, XlKind.Integer),
            new XlKeyValue(Tr.T("Чеков в этих сменах", "Бул смендердеги чектер", "Receipts in those shifts", "Bu vardiyalardaki fiş", "Bu smenalardagi cheklar"), summary.ReceiptsNotFound, XlKind.Integer),
            new XlKeyValue(Tr.T("Строк в выписке", "Көчүрмөдөгү саптар", "Statement rows", "Ekstre satırı", "Ko'chirmadagi qatorlar"), result.Lines.Count, XlKind.Integer),
            new XlKeyValue(Tr.T("Требуют внимания (расход? / поступление?)", "Көңүл бурууну талап кылат", "Need attention (expense? / income?)", "Dikkat gerektirir", "E'tibor talab qiladi"),
                result.Count(BankLineKind.UnknownExpense) + result.Count(BankLineKind.UnknownIncome), XlKind.Integer),
        ]);

        var lines = book.AddSheet(Tr.T("Выписка", "Көчүрмө", "Statement", "Ekstre", "Ko'chirma"), Tr.T("Строки выписки", "Көчүрмө саптары", "Statement rows", "Ekstre satırları", "Ko'chirma qatorlari"), period, landscape: true);
        lines.AddTable(
        [
            new XlColumn(Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.Date),
            new XlColumn(Tr.T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"), XlKind.Money),
            new XlColumn(Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif"), XlKind.Text, MaxWidth: 70),
            new XlColumn(Tr.T("Сопоставлено", "Дал келди", "Matched as", "Eşleşme", "Moslik"), XlKind.Text),
            new XlColumn(Tr.T("Поставщик / категория", "Жеткирүүчү / категория", "Supplier / category", "Tedarikçi / kategori", "Yetkazib beruvchi / kategoriya"), XlKind.Text),
        ],
        result.Lines.Select(r => new object?[]
        {
            r.Line.Date, r.Line.Amount, r.Line.Description, KindTitle(r.Kind),
            r.Supplier ?? (r.Category is { Length: > 0 } c ? BankCategories.Title(c) : ""),
        }), totals: false);

        var missing = book.AddSheet(Tr.T("Нет в банке", "Банкта жок", "Not in bank", "Bankada yok", "Bankda yo'q"), Tr.T("Смены без поступления в банк", "Банкка түшпөгөн смендер", "Shifts with no bank credit", "Bankaya geçmeyen vardiyalar", "Bankka tushmagan smenalar"), period);
        var inPeriod = result.ShiftsNotFound.Where(s => s.Day.Date >= from.Date && s.Day.Date <= to.Date).ToList();
        missing.AddTable(
        [
            new XlColumn(Tr.T("День", "Күн", "Day", "Gün", "Kun"), XlKind.Date),
            new XlColumn(Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena"), XlKind.Text, MaxWidth: 50),
            new XlColumn(Tr.T("Безнал, сом", "Безнал, сом", "Cashless, som", "Kartlı, som", "Naqdsiz, so'm"), XlKind.Money, XlTotal.Sum),
            new XlColumn(Tr.T("Чеков", "Чектер", "Receipts", "Fiş", "Cheklar"), XlKind.Integer, XlTotal.Sum),
        ],
        inPeriod.Select(s => new object?[] { s.Day, s.Label, s.Amount, s.Receipts }));
        book.Save();
    }
}
