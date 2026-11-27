using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 («создай то, чего у нас нет»: выгрузка в 1С): данные периода — номенклатура, остатки, продажи, возвраты, приходы, контрагенты — в виде,
/// который понимает 1С. Из чего выбирали: у «1С:Розница/Бухгалтерия» нет единого приёма файлов от касс — общий путь: «Загрузка данных из табличного документа»
/// (Excel/CSV, заголовки в первой строке) и обмен каталогом по CommerceML 2.x (import.xml + offers.xml; Розница 2.2 принимает его только внешней обработкой).
/// Поэтому: Excel (одна книга, лист на таблицу, заголовок в 1-й строке без шапки отчёта) и CSV (UTF-8 с BOM, «;», десятичная запятая, как ждёт Excel/1С в
/// русской и кыргызской локали) — по умолчанию; CommerceML — только каталог и остатки (import.xml/offers.xml), документы к нему идут CSV.
/// Деньги — decimal с округлением до копеек «половина вверх» (как сервер): сумма в файле = сумма отчёта. Данные собирает окно (OneCExportLoader), здесь — только формат.</summary>
public static class OneCExport
{
    public enum Part { Sales, Returns, Receipts, Stock, Counterparties, Products }

    public enum Format { Excel, Csv, CommerceMl }

    public sealed record ProductRow(string Id, string Code, string Barcode, string Name, string Category, string Unit, decimal Price, decimal Cost, decimal Quantity);

    public sealed record ReturnRow(string Id, string Number, DateTime? Date, string SaleNumber, decimal Amount, string Reason, string Items);

    public sealed record ReceiptLineRow(string Name, string Code, string Barcode, string Unit, decimal Quantity, decimal Price, decimal Sum);

    /// <param name="Esf">2026-11-26 (стресс-тест): № ЭСФ поставщика (DocumentNumberingCore) — бухгалтер сверяет приход в 1С с электронной счёт-фактурой.</param>
    public sealed record ReceiptRow(string Id, string Number, DateTime? Date, string Supplier, string SupplierDoc, decimal Total, IReadOnlyList<ReceiptLineRow> Lines, string Esf = "");

    /// <param name="IsSupplier">true — поставщик, false — покупатель (клиент).</param>
    public sealed record PartyRow(bool IsSupplier, string Id, string Name, string Phone, string Email, string Inn, string Okpo, string Account, string Bik, string Address);

    /// <param name="ServerRevenue">2026-11-26 (стресс-тест): выручка сводки сервера (плитка «Выручка») — для сверки с суммой чеков в файле; null — сводки нет.</param>
    public sealed record Data(DateTime From, DateTime To, string? Shop,
        IReadOnlyList<ProductRow> Products,
        IReadOnlyList<SalesExportService.Receipt> Sales,
        IReadOnlyList<ReturnRow> Returns,
        IReadOnlyList<ReceiptRow> Receipts,
        IReadOnlyList<PartyRow> Parties,
        decimal? ServerRevenue = null);

    /// <summary>Таблица выгрузки: лист Excel / отдельный CSV. Строки — значения по колонкам (decimal, DateTime, строки).</summary>
    public sealed record Table(string Sheet, string FileStem, IReadOnlyList<XlColumn> Columns, IReadOnlyList<object?[]> Rows);

    /// <summary>Что записано: имена файлов (внутри архива или сам файл) и число строк данных по всем таблицам.</summary>
    public sealed record Result(IReadOnlyList<string> Files, int Rows);

    // ------------------------------------------------------------------ деньги и количества

    /// <summary>Сумма в копейках без потери: double → decimal (15 значащих цифр) → 2 знака, половина вверх, как сервер. Не число — 0.</summary>
    public static decimal Money(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e15 ? 0m : Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Количество: до трёх знаков (весовое), половина вверх.</summary>
    public static decimal Qty(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e12 ? 0m : Math.Round((decimal)value, 3, MidpointRounding.AwayFromZero);

    /// <summary>Выручка периода в выгрузке: сумма чеков (как колонка «Сумма» таблицы «Чеки»), копейка в копейку.</summary>
    public static decimal SalesTotal(Data data) => data.Sales.Sum(r => Money(r.Total));

    /// <summary>2026-11-26 (стресс-тест: «сумма в выгрузке = сумма по отчёту»): выручка — чеки, которые входят в выручку (без «в долг» и неоплаченных),
    /// тем же правилом, что «Продажи → Excel» (<see cref="SalesExportService.CountsAsRevenue"/>). Раньше окно показывало только сумму всех чеков — с плиткой
    /// «Выручка» она не сходилась при продажах в долг, и было непонятно почему.</summary>
    public static decimal Revenue(Data data) => data.Sales.Where(r => SalesExportService.CountsAsRevenue(r.Status)).Sum(r => Money(r.Total));

    /// <summary>Итоги для окна после выгрузки: сумма чеков в файле, выручка (если отличается — с пояснением) и сверка со сводкой сервера.</summary>
    public static IReadOnlyList<string> TotalsLines(Data data)
    {
        var lines = new List<string>();
        var total = SalesTotal(data);
        var revenue = Revenue(data);
        var count = data.Sales.Count;
        lines.Add(L($"Сумма чеков в файле: {MoneyText.Som(total)} ({count} чеков).", $"Файлдагы чектердин суммасы: {MoneyText.Som(total)} ({count} чек).",
            $"Receipts total in the file: {MoneyText.Som(total)} ({count} receipts).", $"Dosyadaki fiş toplamı: {MoneyText.Som(total)} ({count} fiş).",
            $"Fayldagi cheklar summasi: {MoneyText.Som(total)} ({count} chek)."));
        if (revenue != total)
            lines.Add(L($"Из них выручка: {MoneyText.Som(revenue)} — чеки в долг и неоплаченные в выручку не входят (как плитка «Выручка»).",
                $"Анын ичинен түшүм: {MoneyText.Som(revenue)} — карызга жана төлөнбөгөн чектер түшүмгө кирбейт («Түшүм» плиткасындай).",
                $"Of which revenue: {MoneyText.Som(revenue)} — receipts on credit and unpaid ones are not revenue (as on the Revenue tile).",
                $"Bunun cirosu: {MoneyText.Som(revenue)} — veresiye ve ödenmemiş fişler ciroya dahil değil («Ciro» kutusu gibi).",
                $"Shundan tushum: {MoneyText.Som(revenue)} — qarzga va to'lanmagan cheklar tushumga kirmaydi («Tushum» plitkasi kabi)."));
        if (data.ServerRevenue is { } server && Math.Abs(Math.Round(server, 2, MidpointRounding.AwayFromZero) - revenue) >= 0.01m)
            lines.Add(L($"Внимание: выручка по сводке сервера {MoneyText.Som(server)} не совпадает с выручкой чеков в файле (разница {MoneyText.Som(server - revenue)}). Обычно это обрезанный список или чеки, изменённые во время выгрузки, — повторите выгрузку или выберите период короче.",
                $"Көңүл буруңуз: сервердин жыйынтыгы боюнча түшүм {MoneyText.Som(server)} файлдагы чектердин түшүмү менен дал келбейт (айырма {MoneyText.Som(server - revenue)}). Адатта бул кыскартылган тизме же чыгаруу учурунда өзгөргөн чектер — кайра чыгарыңыз же кыскараак мезгил тандаңыз.",
                $"Attention: revenue per the server summary {MoneyText.Som(server)} does not match the revenue of receipts in the file (difference {MoneyText.Som(server - revenue)}). Usually the list was cut or receipts changed during the export — export again or choose a shorter period.",
                $"Dikkat: sunucu özetine göre ciro {MoneyText.Som(server)} dosyadaki fişlerin cirosuyla eşleşmiyor (fark {MoneyText.Som(server - revenue)}). Genellikle liste kesilmiştir veya fişler aktarım sırasında değişmiştir — tekrar aktarın veya daha kısa dönem seçin.",
                $"Diqqat: server xulosasi bo'yicha tushum {MoneyText.Som(server)} fayldagi cheklar tushumiga mos kelmaydi (farq {MoneyText.Som(server - revenue)}). Odatda ro'yxat qisqargan yoki cheklar eksport paytida o'zgargan — qayta chiqaring yoki qisqaroq davr tanlang."));
        return lines;
    }

    // ------------------------------------------------------------------ период

    /// <summary>2026-11-26 (стресс-тест, сценарий «повторно за прошлый месяц»): быстрые периоды окна — этот месяц (с 1-го по сегодня) и прошлый месяц целиком.</summary>
    public static (DateTime From, DateTime To) ThisMonth(DateTime today) => (new DateTime(today.Year, today.Month, 1), today.Date);

    public static (DateTime From, DateTime To) LastMonth(DateTime today)
    {
        var first = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    // ------------------------------------------------------------------ подписи

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static string Cur => ReportLabels.Currency;

    public static string FileTitle => L("Выгрузка в 1С", "1С'ке чыгаруу", "Export for 1C", "1C'ye aktarım", "1C ga eksport");

    public static string PartTitle(Part part) => part switch
    {
        Part.Products => L("Номенклатура", "Номенклатура", "Nomenclature", "Ürün listesi", "Nomenklatura"),
        Part.Stock => L("Остатки", "Калдыктар", "Stock balances", "Stok bakiyeleri", "Qoldiqlar"),
        Part.Sales => L("Продажи (чеки)", "Сатуулар (чектер)", "Sales (receipts)", "Satışlar (fişler)", "Sotuvlar (cheklar)"),
        Part.Returns => L("Возвраты от клиентов", "Кардарлардан кайтаруулар", "Customer returns", "Müşteri iadeleri", "Mijozlardan qaytarishlar"),
        Part.Receipts => L("Приходы от поставщиков", "Жеткирүүчүлөрдөн кирешелер", "Supplier receipts", "Tedarikçi girişleri", "Yetkazib beruvchilardan kirimlar"),
        _ => L("Контрагенты", "Контрагенттер", "Counterparties", "Cariler", "Kontragentlar"),
    };

    public static string SuggestFileName(DateTime from, DateTime to, string? shop, Format format, int tableCount)
    {
        var extension = format == Format.Excel ? "xlsx" : format == Format.Csv && tableCount <= 1 ? "csv" : "zip";
        return ExportFileName.Build(FileTitle, ExportFileName.Period(from, to), shop, extension);
    }

    // ------------------------------------------------------------------ таблицы

    private static XlColumn Text(string title) => new(title);
    private static XlColumn Code(string title) => new(title, XlKind.Code);
    private static XlColumn Mny(string title) => new(title, XlKind.Money);

    public static IReadOnlyList<Table> BuildTables(Data data, IReadOnlyCollection<Part> parts)
    {
        var result = new List<Table>();
        var cur = " (" + Cur + ")";

        if (parts.Contains(Part.Products))
        {
            result.Add(new Table(PartTitle(Part.Products), "Nomenklatura",
                [Code(L("Артикул", "Артикул", "Article", "Ürün kodu", "Artikul")), Code(L("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod")),
                    Text(L("Наименование", "Аталышы", "Name", "Ad", "Nomi")), Text(L("Группа", "Топ", "Group", "Grup", "Guruh")),
                    Text(L("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik")), Mny(L("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi") + cur),
                    Mny(L("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + cur)],
                data.Products.Select(p => new object?[] { p.Code, p.Barcode, p.Name, p.Category, p.Unit, p.Price, p.Cost }).ToList()));
        }

        if (parts.Contains(Part.Stock))
        {
            result.Add(new Table(PartTitle(Part.Stock), "Ostatki",
                [Code(L("Артикул", "Артикул", "Article", "Ürün kodu", "Artikul")), Code(L("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod")),
                    Text(L("Наименование", "Аталышы", "Name", "Ad", "Nomi")), Text(L("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik")),
                    new XlColumn(L("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), XlKind.Quantity),
                    Mny(L("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + cur),
                    Mny(L("Сумма закупки", "Сатып алуу суммасы", "Purchase amount", "Alış tutarı", "Xarid summasi") + cur),
                    Mny(L("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi") + cur),
                    Mny(L("Сумма продажи", "Сатуу суммасы", "Sale amount", "Satış tutarı", "Sotuv summasi") + cur)],
                data.Products.Select(p => new object?[]
                {
                    p.Code, p.Barcode, p.Name, p.Unit, p.Quantity, p.Cost, Math.Round(p.Quantity * p.Cost, 2, MidpointRounding.AwayFromZero),
                    p.Price, Math.Round(p.Quantity * p.Price, 2, MidpointRounding.AwayFromZero),
                }).ToList()));
        }

        if (parts.Contains(Part.Sales))
        {
            result.Add(new Table(PartTitle(Part.Sales), "Prodazhi",
                [Text(L("Номер чека", "Чектин номери", "Receipt no.", "Fiş no.", "Chek raqami")), new XlColumn(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime),
                    Text(L("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz")), Text(L("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")),
                    Text(L("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov")), Text(L("Статус", "Абалы", "Status", "Durum", "Holat")),
                    Mny(L("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma") + cur), Mny(L("Сумма", "Сумма", "Amount", "Tutar", "Summa") + cur)],
                data.Sales.Select(r => new object?[]
                {
                    r.Number, r.CreatedAt, r.Client ?? "", r.Cashier ?? "", SalesExportService.PaymentLabel(r.PaymentMethod), SalesExportService.StatusLabel(r.Status),
                    Money(r.Discount), Money(r.Total),
                }).ToList()));

            result.Add(new Table(PartTitle(Part.Sales) + " — " + L("товары", "товарлар", "items", "ürünler", "mahsulotlar"), "Prodazhi-tovary",
                [Text(L("Номер чека", "Чектин номери", "Receipt no.", "Fiş no.", "Chek raqami")), new XlColumn(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime),
                    Text(L("Товар", "Товар", "Product", "Ürün", "Mahsulot")), Text(L("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik")),
                    new XlColumn(L("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), XlKind.Quantity),
                    Mny(L("Цена", "Баасы", "Price", "Fiyat", "Narx") + cur), Mny(L("Сумма", "Сумма", "Amount", "Tutar", "Summa") + cur)],
                data.Sales.Where(r => r.Lines is not null)
                    .SelectMany(r => r.Lines!.Select(l => new object?[] { r.Number, r.CreatedAt, l.Name, l.Unit ?? "", Qty(l.Quantity), Money(l.Price), Money(l.Sum) }))
                    .ToList()));
        }

        if (parts.Contains(Part.Returns))
        {
            result.Add(new Table(PartTitle(Part.Returns), "Vozvraty",
                [Text(L("Номер возврата", "Кайтаруу номери", "Return no.", "İade no.", "Qaytarish raqami")), new XlColumn(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime),
                    Text(L("Чек продажи", "Сатуу чеги", "Sale receipt", "Satış fişi", "Sotuv cheki")), Text(L("Состав", "Курамы", "Items", "İçerik", "Tarkibi")),
                    Text(L("Причина", "Себеби", "Reason", "Neden", "Sababi")), Mny(L("Сумма", "Сумма", "Amount", "Tutar", "Summa") + cur)],
                data.Returns.Select(r => new object?[] { r.Number, r.Date, r.SaleNumber, r.Items, r.Reason, r.Amount }).ToList()));
        }

        if (parts.Contains(Part.Receipts))
        {
            result.Add(new Table(PartTitle(Part.Receipts), "Prihody",
                [Text(L("Номер прихода", "Кирешенин номери", "Receipt no.", "Giriş no.", "Kirim raqami")), new XlColumn(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime),
                    Text(L("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")),
                    Text(L("Накладная поставщика", "Жеткирүүчүнүн накладнойу", "Supplier invoice", "Tedarikçi irsaliyesi", "Yetkazib beruvchi hujjati")),
                    Code(L("№ ЭСФ", "ЭСФ №", "ESF no.", "ESF no.", "ESF №")), // 2026-11-26 (стресс-тест): номер ЭСФ поставщика — для сверки в 1С
                    Mny(L("Сумма", "Сумма", "Amount", "Tutar", "Summa") + cur)],
                data.Receipts.Select(r => new object?[] { r.Number, r.Date, r.Supplier, r.SupplierDoc, r.Esf, r.Total }).ToList()));

            result.Add(new Table(PartTitle(Part.Receipts) + " — " + L("товары", "товарлар", "items", "ürünler", "mahsulotlar"), "Prihody-tovary",
                [Text(L("Номер прихода", "Кирешенин номери", "Receipt no.", "Giriş no.", "Kirim raqami")), new XlColumn(L("Дата", "Күнү", "Date", "Tarih", "Sana"), XlKind.DateTime),
                    Text(L("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")),
                    Code(L("Артикул", "Артикул", "Article", "Ürün kodu", "Artikul")), Code(L("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod")),
                    Text(L("Товар", "Товар", "Product", "Ürün", "Mahsulot")), Text(L("Ед. изм.", "Бирдик", "Unit", "Birim", "Birlik")),
                    new XlColumn(L("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), XlKind.Quantity),
                    Mny(L("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + cur), Mny(L("Сумма", "Сумма", "Amount", "Tutar", "Summa") + cur)],
                data.Receipts.SelectMany(r => r.Lines.Select(l => new object?[] { r.Number, r.Date, r.Supplier, l.Code, l.Barcode, l.Name, l.Unit, l.Quantity, l.Price, l.Sum })).ToList()));
        }

        if (parts.Contains(Part.Counterparties))
        {
            result.Add(new Table(PartTitle(Part.Counterparties), "Kontragenty",
                [Text(L("Вид", "Түрү", "Kind", "Tür", "Turi")), Text(L("Наименование", "Аталышы", "Name", "Ad", "Nomi")),
                    Text(L("Телефон", "Телефон", "Phone", "Telefon", "Telefon")), Text("Email"), Code(L("ИНН", "ИНН", "TIN", "Vergi no.", "STIR")),
                    Code(L("ОКПО", "ОКПО", "OKPO", "OKPO", "OKPO")), Code(L("Расчётный счёт", "Эсептешүү эсеби", "Bank account", "Banka hesabı", "Hisob raqami")),
                    Code(L("БИК", "БИК", "BIC", "BIC", "BIK")), Text(L("Адрес", "Дареги", "Address", "Adres", "Manzil"))],
                data.Parties.Select(p => new object?[]
                {
                    p.IsSupplier ? L("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi") : L("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"),
                    p.Name, p.Phone, p.Email, p.Inn, p.Okpo, p.Account, p.Bik, p.Address,
                }).ToList()));
        }

        return result;
    }

    // ------------------------------------------------------------------ запись

    /// <summary>Записать выгрузку. Excel — одна книга; CSV — один файл (одна таблица) или архив .zip с файлами; CommerceML — архив .zip: import.xml + offers.xml
    /// (каталог и остатки) и CSV остальных выбранных таблиц.</summary>
    /// <remarks>2026-11-26 (стресс-тест): файл пишется во временный рядом и только в конце заменяет выбранный — отмена, «нет места», сбой посередине
    /// не оставляют битый файл и не портят прежнюю выгрузку с тем же именем. CSV пишется потоком (100 000 строк — без строки на десятки МБ в памяти).
    /// Файл занят (открыт в Excel), нет места, нет доступа — понятная ошибка по-русски (<see cref="FriendlyIoError"/>), а не системный текст.</remarks>
    public static Result Export(string path, Format format, Data data, IReadOnlyCollection<Part> parts, CancellationToken ct = default)
    {
        var tables = BuildTables(data, parts);
        if (tables.Count == 0)
            throw new InvalidOperationException(NothingToExport);
        var rows = tables.Sum(t => t.Rows.Count);
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full) ?? ".", ".nurcrm-" + System.Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            var files = WriteTo(temp, Path.GetFileName(full), format, data, parts, tables, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
            return new Result(files, rows);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // временный файл удалим в другой раз — главное, что выбранный файл не тронут
            }
            if (ex is not OperationCanceledException && FriendlyIoError(ex) is { } message)
                throw new IOException(message, ex);
            throw;
        }
    }

    private static List<string> WriteTo(string temp, string shownName, Format format, Data data, IReadOnlyCollection<Part> parts, IReadOnlyList<Table> tables, CancellationToken ct)
    {
        switch (format)
        {
            case Format.Excel:
                LargeExcelWriter.WriteSheets(temp, tables.Select(t => (t.Sheet, t.Columns, (IEnumerable<object?[]>)t.Rows)).ToList(), ct);
                return [shownName];

            case Format.Csv when tables.Count == 1:
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    WriteCsv(file, tables[0], ct);
                return [shownName];

            default:
                var files = new List<string>();
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var csv = tables;
                    if (format == Format.CommerceMl && (parts.Contains(Part.Products) || parts.Contains(Part.Stock)))
                    {
                        var (import, offers) = BuildCommerceMl(data);
                        AddEntry(zip, "import.xml", s => SaveXml(import, s), files);
                        AddEntry(zip, "offers.xml", s => SaveXml(offers, s), files);
                        // каталог уже в XML — таблицы номенклатуры и остатков отдельно не нужны
                        csv = tables.Where(t => t.FileStem is not ("Nomenklatura" or "Ostatki")).ToList();
                    }
                    foreach (var table in csv)
                    {
                        ct.ThrowIfCancellationRequested();
                        AddEntry(zip, table.FileStem + ".csv", s => WriteCsv(s, table, ct), files);
                    }
                }
                return files;
        }
    }

    private static void AddEntry(ZipArchive zip, string name, Action<Stream> write, List<string> files)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var s = entry.Open())
            write(s);
        files.Add(name);
    }

    private static string NothingToExport => L("Нет данных для выгрузки: отметьте, что выгрузить.", "Чыгаруу үчүн маалымат жок: эмнени чыгарууну белгилеңиз.",
        "Nothing to export: tick what to export.", "Aktarılacak veri yok: neyi aktaracağınızı işaretleyin.", "Chiqarish uchun ma'lumot yo'q: nimani chiqarishni belgilang.");

    // ------------------------------------------------------------------ ошибки файла

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    /// <summary>2026-11-26 (стресс-тест): понятный текст для типичных сбоев записи файла; null — сбой не про файл (показывается как есть).</summary>
    public static string? FriendlyIoError(Exception ex) => ex switch
    {
        PathTooLongException => L("Слишком длинный путь к файлу. Сохраните в папку покороче (например, «Документы») или сократите имя файла.",
            "Файлга жол өтө узун. Кыскараак папкага (мисалы, «Документтер») сактаңыз же файлдын атын кыскартыңыз.",
            "The file path is too long. Save to a shorter folder (e.g. Documents) or shorten the file name.",
            "Dosya yolu çok uzun. Daha kısa bir klasöre (ör. Belgeler) kaydedin veya dosya adını kısaltın.",
            "Fayl yo'li juda uzun. Qisqaroq papkaga (masalan, «Hujjatlar») saqlang yoki fayl nomini qisqartiring."),
        DirectoryNotFoundException => L("Папка для сохранения не найдена (флешка вынута или папка удалена). Выберите другую папку.",
            "Сактоо папкасы табылган жок (флешка чыгарылган же папка өчүрүлгөн). Башка папка тандаңыз.",
            "The folder to save to was not found (USB drive removed or folder deleted). Choose another folder.",
            "Kaydetme klasörü bulunamadı (USB çıkarılmış veya klasör silinmiş). Başka bir klasör seçin.",
            "Saqlash papkasi topilmadi (fleshka chiqarilgan yoki papka o'chirilgan). Boshqa papka tanlang."),
        UnauthorizedAccessException => L("Нет доступа к файлу: он открыт в другой программе (например, в Excel), только для чтения или папка защищена. Закройте файл или выберите другую папку.",
            "Файлга кирүү жок: ал башка программада ачык (мисалы, Excel'де), окуу үчүн гана же папка корголгон. Файлды жабыңыз же башка папка тандаңыз.",
            "No access to the file: it is open in another program (e.g. Excel), read-only, or the folder is protected. Close the file or choose another folder.",
            "Dosyaya erişim yok: başka bir programda açık (ör. Excel), salt okunur veya klasör korumalı. Dosyayı kapatın veya başka bir klasör seçin.",
            "Faylga kirish yo'q: u boshqa dasturda ochiq (masalan, Excel'da), faqat o'qish uchun yoki papka himoyalangan. Faylni yoping yoki boshqa papka tanlang."),
        IOException io when io.HResult is SharingViolation or LockViolation => L("Файл открыт в другой программе (например, в Excel). Закройте его и нажмите «Выгрузить» ещё раз или сохраните под другим именем.",
            "Файл башка программада ачык (мисалы, Excel'де). Аны жаап, «Чыгаруу» дагы басыңыз же башка ат менен сактаңыз.",
            "The file is open in another program (e.g. Excel). Close it and press Export again, or save under another name.",
            "Dosya başka bir programda açık (ör. Excel). Kapatıp tekrar «Aktar»a basın veya başka bir adla kaydedin.",
            "Fayl boshqa dasturda ochiq (masalan, Excel'da). Uni yopib, «Chiqarish»ni yana bosing yoki boshqa nom bilan saqlang."),
        IOException io when io.HResult is DiskFull or HandleDiskFull => L("На диске не хватает места для файла. Освободите место или сохраните на другой диск.",
            "Дискте файл үчүн орун жетишсиз. Орун бошотуңуз же башка дискке сактаңыз.",
            "There is not enough disk space for the file. Free up space or save to another drive.",
            "Dosya için diskte yeterli yer yok. Yer açın veya başka bir sürücüye kaydedin.",
            "Diskda fayl uchun joy yetarli emas. Joy bo'shating yoki boshqa diskka saqlang."),
        _ => null,
    };

    /// <summary>2026-11-26 (стресс-тест): можно ли записать выбранный файл — проверка ДО загрузки данных (иначе минута загрузки, а в конце «файл открыт в Excel»).
    /// null — можно; иначе понятная причина.</summary>
    public static string? CheckWritable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { } dir && !Directory.Exists(dir))
                return FriendlyIoError(new DirectoryNotFoundException(dir));
            if (!File.Exists(full))
                return null;
            using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FriendlyIoError(ex) ?? ex.Message;
        }
    }

    // ------------------------------------------------------------------ последние настройки окна

    /// <summary>2026-11-26 (стресс-тест, «запоминание последних настроек»): что выгружали и в каком формате — окно открывается с теми же отметками.</summary>
    public sealed class Prefs
    {
        public List<string> Parts { get; set; } = new();
        public string Format { get; set; } = "";
    }

    public static string DefaultPrefsPath => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "onec-export.json");

    /// <summary>Сохранённые отметки и формат; файла нет или он испорчен — null (окно берёт значения по умолчанию).</summary>
    public static (HashSet<Part> Parts, Format Format)? LoadPrefs(string path)
    {
        try
        {
            if (!File.Exists(path) || System.Text.Json.JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path)) is not { } p)
                return null;
            var set = (p.Parts ?? new List<string>()).Select(s => Enum.TryParse<Part>(s, true, out var part) ? (Part?)part : null).Where(x => x is not null).Select(x => x!.Value).ToHashSet();
            if (set.Count == 0 || !Enum.TryParse<Format>(p.Format, true, out var format))
                return null;
            return (set, format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void SavePrefs(string path, IEnumerable<Part> parts, Format format)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Prefs { Parts = parts.Select(p => p.ToString()).ToList(), Format = format.ToString() }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не запомнили — в следующий раз будут значения по умолчанию
        }
    }

    // ------------------------------------------------------------------ CSV

    private static readonly char[] NeedQuote = [';', '"', '\r', '\n'];

    /// <summary>CSV как его открывает Excel и читает 1С: UTF-8 с BOM, разделитель «;», строки CRLF, десятичная запятая, даты «дд.мм.гггг».</summary>
    public static byte[] CsvBytes(Table table)
    {
        using var ms = new MemoryStream();
        WriteCsv(ms, table);
        return ms.ToArray();
    }

    /// <summary>2026-11-26 (стресс-тест): CSV потоком в файл или в запись архива — память не растёт с числом строк; отмена проверяется каждые 16 384 строки.</summary>
    public static void WriteCsv(Stream stream, Table table, CancellationToken ct = default)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(true), 1 << 16, leaveOpen: true);
        w.Write(string.Join(';', table.Columns.Select(c => Quote(c.Title))));
        w.Write("\r\n");
        var n = 0;
        foreach (var row in table.Rows)
        {
            if ((++n & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            for (var c = 0; c < table.Columns.Count; c++)
            {
                if (c > 0)
                    w.Write(';');
                w.Write(Quote(CsvCell(c < row.Length ? row[c] : null, table.Columns[c].Kind)));
            }
            w.Write("\r\n");
        }
    }

    internal static string CsvCell(object? value, XlKind kind)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            null => "",
            decimal m when kind == XlKind.Quantity => m.ToString("0.###", inv).Replace('.', ','),
            decimal m when kind is XlKind.Integer or XlKind.Index => m.ToString("0", inv),
            decimal m => m.ToString("0.00", inv).Replace('.', ','),
            double d when kind == XlKind.Quantity => Qty(d).ToString("0.###", inv).Replace('.', ','),
            double d => Money(d).ToString("0.00", inv).Replace('.', ','),
            int i => i.ToString(inv),
            long l => l.ToString(inv),
            DateTime t when kind == XlKind.Date => t.ToString("dd.MM.yyyy", inv),
            DateTime t => t.ToString("dd.MM.yyyy HH:mm:ss", inv),
            _ => Convert.ToString(value, inv) ?? "",
        };
    }

    private static string Quote(string text) =>
        text.IndexOfAny(NeedQuote) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    // ------------------------------------------------------------------ CommerceML 2.x (каталог и остатки)

    private const string SchemaVersion = "2.08";
    private const string CurrencyCode = "KGS";

    /// <summary>import.xml (классификатор групп + каталог товаров) и offers.xml (пакет предложений: цена и остаток) — структура обмена «1С ↔ сайт»:
    /// КоммерческаяИнформация / Классификатор / Каталог / Товары / Товар и ПакетПредложений / ТипыЦен / Предложения / Предложение. Ид товара — идентификатор товара кассы
    /// (не GUID — детерминированный GUID из него), ИдКаталога и ИдКлассификатора общие у обоих файлов.</summary>
    public static (XDocument Import, XDocument Offers) BuildCommerceMl(Data data, DateTime? generatedAt = null)
    {
        var stamp = (generatedAt ?? DateTime.Now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var catalogId = StableGuid("catalog:" + (data.Shop ?? ""));
        var classifierId = StableGuid("classifier:" + (data.Shop ?? ""));
        var shopName = X(string.IsNullOrWhiteSpace(data.Shop) ? "NurCRM" : data.Shop!);
        var priceTypeId = StableGuid("price-type:retail");

        var groups = data.Products.Select(p => p.Category.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        var import = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("КоммерческаяИнформация",
                new XAttribute("ВерсияСхемы", SchemaVersion), new XAttribute("ДатаФормирования", stamp),
                new XElement("Классификатор",
                    new XElement("Ид", classifierId), new XElement("Наименование", shopName),
                    new XElement("Группы", groups.Select(g => new XElement("Группа", new XElement("Ид", StableGuid("group:" + g.ToLowerInvariant())), new XElement("Наименование", X(g)))))),
                new XElement("Каталог",
                    new XElement("Ид", catalogId), new XElement("ИдКлассификатора", classifierId), new XElement("Наименование", shopName),
                    new XElement("Товары", data.Products.Select(p => ProductElement(p))))));

        var offers = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("КоммерческаяИнформация",
                new XAttribute("ВерсияСхемы", SchemaVersion), new XAttribute("ДатаФормирования", stamp),
                new XElement("ПакетПредложений",
                    new XAttribute("СодержитТолькоИзменения", "false"),
                    new XElement("Ид", StableGuid("offers:" + (data.Shop ?? ""))), new XElement("Наименование", L("Пакет предложений", "Сунуштар топтому", "Offers package", "Teklif paketi", "Takliflar to'plami")),
                    new XElement("ИдКаталога", catalogId), new XElement("ИдКлассификатора", classifierId),
                    new XElement("ТипыЦен", new XElement("ТипЦены",
                        new XElement("Ид", priceTypeId), new XElement("Наименование", L("Розничная", "Чекене", "Retail", "Perakende", "Chakana")), new XElement("Валюта", CurrencyCode))),
                    new XElement("Предложения", data.Products.Select(p =>
                    {
                        var unit = UnitElement(p.Unit);
                        return new XElement("Предложение",
                            new XElement("Ид", ProductGuid(p)),
                            string.IsNullOrWhiteSpace(p.Code) ? null : new XElement("Артикул", X(p.Code)),
                            string.IsNullOrWhiteSpace(p.Barcode) ? null : new XElement("Штрихкод", X(p.Barcode)),
                            new XElement("Наименование", X(p.Name)),
                            unit,
                            new XElement("Цены", new XElement("Цена",
                                new XElement("Представление", p.Price.ToString("0.00", CultureInfo.InvariantCulture) + " " + CurrencyCode + " " + L("за", "үчүн", "per", "başına", "uchun") + " " + X(p.Unit)),
                                new XElement("ИдТипаЦены", priceTypeId),
                                new XElement("ЦенаЗаЕдиницу", p.Price.ToString("0.00", CultureInfo.InvariantCulture)),
                                new XElement("Валюта", CurrencyCode),
                                new XElement("Единица", X(p.Unit)))),
                            new XElement("Количество", p.Quantity.ToString("0.###", CultureInfo.InvariantCulture)));
                    })))));

        return (import, offers);
    }

    private static XElement ProductElement(ProductRow p) =>
        new("Товар",
            new XElement("Ид", ProductGuid(p)),
            string.IsNullOrWhiteSpace(p.Code) ? null : new XElement("Артикул", X(p.Code)),
            new XElement("Наименование", X(p.Name)),
            UnitElement(p.Unit),
            string.IsNullOrWhiteSpace(p.Category) ? null : new XElement("Группы", new XElement("Ид", StableGuid("group:" + p.Category.Trim().ToLowerInvariant()))),
            string.IsNullOrWhiteSpace(p.Barcode) ? null : new XElement("Штрихкод", X(p.Barcode)));

    private static XElement UnitElement(string unit)
    {
        var (code, full) = UnitCode(unit);
        return new XElement("БазоваяЕдиница", new XAttribute("Код", code), new XAttribute("НаименованиеПолное", full), X(unit));
    }

    /// <summary>Коды ОКЕИ базовых единиц: шт 796, кг 166, г 163, л 112, м 006, м² 055, м³ 113, упак 778, пара 715; неизвестная — штука.</summary>
    public static (string Code, string Full) UnitCode(string? unit)
    {
        var u = (unit ?? "").Trim().TrimEnd('.').ToLowerInvariant().Replace("²", "2").Replace("³", "3");
        return u switch
        {
            "кг" or "kg" or "килограмм" => ("166", "Килограмм"),
            "г" or "g" or "гр" or "грамм" => ("163", "Грамм"),
            "л" or "l" or "литр" => ("112", "Литр"),
            "м" or "m" or "метр" => ("006", "Метр"),
            "м2" or "m2" or "кв.м" => ("055", "Квадратный метр"),
            "м3" or "m3" or "куб.м" => ("113", "Кубический метр"),
            "упак" or "упаковка" or "уп" => ("778", "Упаковка"),
            "пара" or "пар" => ("715", "Пара"),
            _ => ("796", "Штука"),
        };
    }

    private static string ProductGuid(ProductRow p) =>
        System.Guid.TryParse(p.Id, out var g) ? g.ToString() : StableGuid("product:" + p.Id + "|" + p.Barcode + "|" + p.Name);

    private static string StableGuid(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new System.Guid(hash).ToString();
    }

    /// <summary>Строка без символов, запрещённых в XML 1.0 (управляющие коды из чужих баз), и без краевых пробелов.</summary>
    private static string X(string? text) =>
        new string((text ?? "").Where(XmlConvert.IsXmlChar).ToArray()).Trim();

    private static void SaveXml(XDocument doc, Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false });
        doc.Save(writer);
    }
}
