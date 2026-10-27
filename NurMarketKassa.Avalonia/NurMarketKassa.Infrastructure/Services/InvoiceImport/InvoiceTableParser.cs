using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: накладная из таблицы Excel (.xlsx) или CSV. Таблицу читает ProductFileReader (тот же код, что у «Массового добавления»):
// все непустые строки подряд. Здесь: поиск строки заголовков по набору синонимов («Наименование» + «Кол-во» + «Цена/Сумма») где угодно в
// первых 60 строках, шапка документа из строк выше и ниже таблицы, строки товаров до «Итого» / подписей, пропуск строки нумерации колонок.

public sealed class InvoiceTableDetection
{
    /// <summary>Индекс строки заголовков в <see cref="ProductFileTable.Rows"/>.</summary>
    public int HeaderIndex { get; init; }

    public InvoiceColumnMapping Mapping { get; init; } = new();

    /// <summary>Строка заголовков найдена уверенно (иначе нужно ручное сопоставление).</summary>
    public bool Recognized { get; init; }
}

public static class InvoiceTableParser
{
    public const int HeaderScanRows = 60;

    private static readonly string[] TotalWords = ["итого", "всего", "total", "жыйынты", "жалпы", "jami", "umumiy", "toplam", "итогопо", "итогоотпущено"];

    private static readonly string[] SignatureWords =
    [
        "отпуск", "руководитель", "главныйбухгалтер", "бухгалтер", "подпись", "сдал", "принял", "грузпринял", "директор", "кладовщик", "мп",
        "signature", "доверенность", "расшифровка", "дополнительныесведения", "imza", "қолтамга",
    ];

    private static readonly Regex SpaceRun = new(@"\s+", RegexOptions.Compiled);

    // ------------------------------------------------------------------ поиск заголовков

    /// <summary>Строка заголовков и сопоставление столбцов. Сначала — по памяти (тот же поставщик уже загружался), иначе автоподбор.</summary>
    public static InvoiceTableDetection Detect(ProductFileTable table)
    {
        var bestScore = 0;
        var bestIndex = -1;
        InvoiceColumnMapping? best = null;
        var limit = Math.Min(table.Rows.Count, HeaderScanRows);
        for (var i = 0; i < limit; i++)
        {
            var score = InvoiceColumnMapping.Score(table.Rows[i].Cells, out var mapping);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
                best = mapping;
            }
        }

        if (best is null)
        {
            // Заголовок не узнан: берём строку, которую уже определил ProductFileReader (или первую), пусть кассир сопоставит вручную.
            // Заголовок не узнан: предполагаем первую «широкую» строку (больше всего непустых ячеек среди первых 20), а не строку «Название поставщика: …».
            var fallback = 0;
            var widest = 0;
            for (var i = 0; i < Math.Min(table.Rows.Count, 20); i++)
            {
                var filled = table.Rows[i].Cells.Count(c => !string.IsNullOrWhiteSpace(c));
                if (filled > widest)
                {
                    widest = filled;
                    fallback = i;
                }
            }
            if (widest < 3)
                fallback = Math.Clamp(table.HeaderIndex, 0, Math.Max(0, table.Rows.Count - 1));
            var cells = table.Rows.Count > 0 ? table.Rows[fallback].Cells : [];
            // Те же заголовки уже сопоставляли вручную — берём запомненное.
            if (cells.Length > 0 && InvoiceImportRegistry.TryLoadMapping(cells) is { IsComplete: true } saved)
                return new InvoiceTableDetection { HeaderIndex = fallback, Mapping = saved, Recognized = true };
            return new InvoiceTableDetection { HeaderIndex = fallback, Mapping = InvoiceColumnMapping.AutoDetect(cells), Recognized = false };
        }

        var remembered = InvoiceImportRegistry.TryLoadMapping(table.Rows[bestIndex].Cells);
        return new InvoiceTableDetection { HeaderIndex = bestIndex, Mapping = remembered ?? best, Recognized = true };
    }

    // ------------------------------------------------------------------ разбор

    public static InvoiceDocument Parse(ProductFileTable table, int headerIndex, InvoiceColumnMapping mapping, string fileName = "")
    {
        var format = table.Kind == ProductFileKind.Xlsx ? "Excel" : "CSV";
        var reader = new InvoiceHeaderReader();
        var items = new List<InvoiceItem>();
        var warnings = new List<string>();
        var header = reader.Header;

        headerIndex = Math.Clamp(headerIndex, 0, Math.Max(0, table.Rows.Count - 1));
        var headerCells = table.Rows.Count > 0 ? table.Rows[headerIndex].Cells : [];

        // Шапка документа — строки выше заголовков таблицы.
        for (var i = 0; i < headerIndex; i++)
            reader.Feed(table.Rows[i].Cells);

        var cName = mapping.ColumnOf(InvoiceField.Name);
        var cBarcode = mapping.ColumnOf(InvoiceField.Barcode);
        var cArticle = mapping.ColumnOf(InvoiceField.Article);
        var cUnit = mapping.ColumnOf(InvoiceField.Unit);
        var cQty = mapping.ColumnOf(InvoiceField.Quantity);
        var cPrice = mapping.ColumnOf(InvoiceField.Price);
        var cPriceVat = mapping.ColumnOf(InvoiceField.PriceWithVat);
        var cRetail = mapping.ColumnOf(InvoiceField.Retail);
        var cSum = mapping.ColumnOf(InvoiceField.Sum);
        var cSumVat = mapping.ColumnOf(InvoiceField.SumWithVat);
        var cVatRate = mapping.ColumnOf(InvoiceField.VatRate);
        var cVatSum = mapping.ColumnOf(InvoiceField.VatSum);

        // Валюта — из заголовков столбцов цены/суммы («Цена за ед., руб.»).
        foreach (var c in new[] { cPrice, cPriceVat, cSum, cSumVat })
        {
            if (c >= 0 && c < headerCells.Length && InvoiceText.DetectCurrency(headerCells[c]) is { Length: > 0 } cur)
            {
                header.Currency = cur;
                break;
            }
        }

        var ended = false;
        var skippedNoise = 0;
        // 2026-10-27 (ревью Ск-1): многостраничная ТОРГ-12/УПД. «Итого по странице» раньше обрывало таблицу (ended = true) — все строки следующих страниц терялись, а итог брался
        // с первой страницы (??=), поэтому сверка «сумма строк = итог» сходилась и предупреждения не было. Теперь строка итога таблицу НЕ заканчивает: разбор идёт дальше
        // (повтор шапки и нумерацию колонок пропускаем), итогом файла считается последний «Всего/Итого» (по накладной), «по странице» копятся отдельно и идут запасным итогом.
        var sawTotal = false;
        var pageTotals = new PageTotals();
        for (var r = headerIndex + 1; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (ended)
            {
                reader.Feed(row.Cells);
                continue;
            }

            var first = FirstText(row.Cells);
            var firstKey = ProductImportMapping.Key(first);

            if (IsNumberingRow(row.Cells) || IsRepeatedHeader(row.Cells, headerCells, mapping))
                continue;

            if (firstKey.Length > 0 && StartsWithAny(firstKey, TotalWords) && !LooksLikeItem(row, cName, cQty, cPrice, cSum))
            {
                ReadTotals(row, header, cSum, cSumVat, cVatSum, IsPageTotal(row.Cells), pageTotals);
                reader.IgnoreTotals = true;
                reader.Feed(row.Cells);
                reader.IgnoreTotals = false;
                sawTotal = sawTotal || items.Count > 0;
                continue;
            }

            if (items.Count > 0 && firstKey.Length > 0 && IsSignature(firstKey) && !LooksLikeItem(row, cName, cQty, cPrice, cSum))
            {
                ended = true;
                reader.Feed(row.Cells);
                continue;
            }

            var name = Clean(Cell(row, cName));
            var qtyText = Cell(row, cQty).Trim();
            var barcodeRaw = Cell(row, cBarcode).Trim();
            var hasQty = InvoiceText.TryParseNumber(qtyText, out var qty);
            var price = InvoiceText.ParseNumberOrNull(Cell(row, cPrice));
            var priceVat = InvoiceText.ParseNumberOrNull(Cell(row, cPriceVat));
            var sum = InvoiceText.ParseNumberOrNull(Cell(row, cSum));
            var sumVat = InvoiceText.ParseNumberOrNull(Cell(row, cSumVat));

            // Строка без числа вообще — заголовок группы («Бытовая техника»), подпись, пустышка: не товар.
            var anyNumber = hasQty || price is not null || priceVat is not null || sum is not null || sumVat is not null;
            if (!anyNumber && barcodeRaw.Length == 0)
            {
                if (name.Length > 0)
                {
                    reader.Feed(row.Cells);
                    skippedNoise++;
                }
                continue;
            }
            if (name.Length == 0 && barcodeRaw.Length == 0 && Clean(Cell(row, cArticle)).Length == 0)
            {
                skippedNoise++;
                continue;
            }
            // 2026-10-27 (Ск-1): после строки итога (подвал страницы/накладной) товаром считается только строка с названием, количеством > 0 и ценой или суммой —
            // «в том числе НДС 50 250», «Масса груза» и подобные подписи с числом в столбце суммы товаром не становятся.
            if (sawTotal && !(hasQty && qty > 0 && name.Length > 0 && (price is not null || sum is not null || priceVat is not null || sumVat is not null)))
            {
                if (name.Length > 0)
                    reader.Feed(row.Cells);
                skippedNoise++;
                continue;
            }

            var item = new InvoiceItem
            {
                SourceRow = row.FileRow,
                Name = name,
                Unit = Clean(Cell(row, cUnit)),
                Quantity = hasQty ? qty : 0,
                Price = price,
                PriceWithVat = priceVat,
                RetailPrice = InvoiceText.ParseNumberOrNull(Cell(row, cRetail)),
                Sum = sum,
                SumWithVat = sumVat,
                VatSum = InvoiceText.ParseNumberOrNull(Cell(row, cVatSum)),
            };

            if (qtyText.Length > 0 && !hasQty)
                item.Warnings.Add(Tr.T($"количество не число: «{qtyText}»", $"саны сан эмес: «{qtyText}»", $"quantity is not a number: “{qtyText}”",
                    $"miktar sayı değil: «{qtyText}»", $"miqdor son emas: «{qtyText}»"));
            else if (hasQty && qty <= 0)
                item.Warnings.Add(Tr.T("количество не больше нуля", "саны нөлдөн чоң эмес", "quantity is not above zero", "miktar sıfırdan büyük değil", "miqdor noldan katta emas"));

            // Штрихкод: колонка штрихкода; иначе «артикул/код», если он на самом деле штрихкод (цифры + верная контрольная цифра).
            var barcode = InvoiceText.CleanBarcode(barcodeRaw, restoreLeadingZero: true);
            if (barcode.Length == 0 && barcodeRaw.Length > 0)
            {
                item.Warnings.Add(Tr.T($"«{barcodeRaw}» не похоже на штрихкод — не используется", $"«{barcodeRaw}» штрих-кодго окшобойт — колдонулбайт",
                    $"“{barcodeRaw}” does not look like a barcode — ignored", $"«{barcodeRaw}» barkoda benzemiyor — kullanılmadı", $"«{barcodeRaw}» shtrix-kodga o'xshamaydi — ishlatilmaydi"));
            }
            var article = Clean(Cell(row, cArticle));
            if (barcode.Length == 0 && InvoiceText.LooksLikeBarcode(article))
                barcode = InvoiceText.CleanBarcode(article);
            else if (barcode.Length > 0 && !InvoiceText.HasValidCheckDigit(barcode))
                item.Warnings.Add(Tr.T($"у штрихкода {barcode} не сходится контрольная цифра", $"{barcode} штрих-кодунун контролдук цифрасы дал келбейт",
                    $"barcode {barcode} has a wrong check digit", $"{barcode} barkodunun kontrol hanesi uyuşmuyor", $"{barcode} shtrix-kodining nazorat raqami mos kelmaydi"));
            item.Barcode = barcode;
            item.Article = article;

            // НДС: ставка числом («20», «20 %», 0,2 как доля) или текстом («Без НДС»).
            var rateText = Cell(row, cVatRate).Trim();
            item.VatText = rateText;
            if (rateText.Length > 0 && InvoiceText.TryParseNumber(rateText, out var rate))
                item.VatPercent = rate > 0 && rate < 1 ? Math.Round(rate * 100, 2) : rate;

            // Сверка внутри строки: цена × количество = сумма (если НДС не объясняет разницу).
            if (item.Price is > 0 && item.Sum is > 0 && item.Quantity > 0)
            {
                var expected = item.Price.Value * item.Quantity;
                if (Math.Abs(expected - item.Sum.Value) > Math.Max(0.5, item.Sum.Value * 0.01))
                    item.Warnings.Add(Tr.T($"сумма не сходится: {InvoiceText.Num(item.Quantity)} × {InvoiceText.Num(item.Price.Value)} = {InvoiceText.Num(expected)}, в файле {InvoiceText.Num(item.Sum.Value)}",
                        $"сумма дал келбейт: {InvoiceText.Num(item.Quantity)} × {InvoiceText.Num(item.Price.Value)} = {InvoiceText.Num(expected)}, файлда {InvoiceText.Num(item.Sum.Value)}",
                        $"amount mismatch: {InvoiceText.Num(item.Quantity)} × {InvoiceText.Num(item.Price.Value)} = {InvoiceText.Num(expected)}, file says {InvoiceText.Num(item.Sum.Value)}",
                        $"tutar uyuşmuyor: {InvoiceText.Num(item.Quantity)} × {InvoiceText.Num(item.Price.Value)} = {InvoiceText.Num(expected)}, dosyada {InvoiceText.Num(item.Sum.Value)}",
                        $"summa mos kelmaydi: {InvoiceText.Num(item.Quantity)} × {InvoiceText.Num(item.Price.Value)} = {InvoiceText.Num(expected)}, faylda {InvoiceText.Num(item.Sum.Value)}"));
            }
            if (item.UnitPrice(false) <= 0)
                item.Warnings.Add(Tr.T("нет цены", "баасы жок", "no price", "fiyat yok", "narx yo'q"));

            items.Add(item);
        }

        // 2026-10-27 (Ск-1): нет итога «по накладной» — запасной итог: сумма итогов «по странице» (многостраничный файл без последней строки «Всего»).
        if (header.TotalFromFile is null && header.NetTotalFromFile is null && pageTotals.Any)
        {
            if (pageTotals.Gross > 0)
                header.TotalFromFile = Math.Round(pageTotals.Gross, 2);
            if (pageTotals.Net > 0)
                header.NetTotalFromFile = Math.Round(pageTotals.Net, 2);
            if (pageTotals.Vat > 0)
                header.VatTotalFromFile ??= Math.Round(pageTotals.Vat, 2);
        }

        // Итоги, оставшиеся ниже таблицы без слова «Итого» в первой ячейке, не ищем — только то, что прочли.
        if (header.Currency.Length == 0 && reader.CurrencyHint.Length > 0)
            header.Currency = reader.CurrencyHint;
        if (header.TotalFromFile is null && header.NetTotalFromFile is not null)
            header.TotalFromFile = header.NetTotalFromFile;

        var doc = new InvoiceDocument { Header = header, Items = items, Format = format, FileName = fileName.Length > 0 ? fileName : table.FileName };
        if (items.Count == 0)
            doc.Warnings.Add(Tr.T("В таблице не найдено ни одной строки товара.", "Таблицадан бир да товар сабы табылган жок.", "No product rows found in the table.",
                "Tabloda ürün satırı bulunamadı.", "Jadvalda mahsulot qatori topilmadi."));
        if (skippedNoise > 0)
            doc.Warnings.Add(Tr.T($"Пропущено строк без чисел (заголовки групп, пояснения): {skippedNoise}.", $"Сандары жок саптар өткөрүлдү (топтун аталыштары, түшүндүрмөлөр): {skippedNoise}.",
                $"Skipped rows without numbers (group titles, notes): {skippedNoise}.", $"Sayısız satırlar atlandı (grup başlıkları, notlar): {skippedNoise}.", $"Raqamsiz qatorlar o'tkazib yuborildi (guruh sarlavhalari, izohlar): {skippedNoise}."));
        return doc;
    }

    // ------------------------------------------------------------------ мелочи

    private static string Cell(ProductFileRow row, int column) => column >= 0 ? row[column] : "";

    private static string Clean(string? s) => SpaceRun.Replace((s ?? "").Replace(' ', ' ').Trim(), " ");

    private static string FirstText(string[] cells)
    {
        foreach (var c in cells)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        }
        return "";
    }

    private static bool StartsWithAny(string key, string[] words)
    {
        foreach (var w in words)
        {
            if (key.StartsWith(w, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsSignature(string key)
    {
        foreach (var w in SignatureWords)
        {
            if (key.StartsWith(w, StringComparison.Ordinal) && (w.Length > 3 || key.Length <= 8))
                return true;
        }
        return false;
    }

    /// <summary>Строка с названием, количеством и ценой — это товар, даже если название начинается с «Всего…».</summary>
    private static bool LooksLikeItem(ProductFileRow row, int cName, int cQty, int cPrice, int cSum)
    {
        if (cName < 0 || cQty < 0 || string.IsNullOrWhiteSpace(row[cName]) || row[cName].TrimStart().Length < 3)
            return false;
        var nameKey = ProductImportMapping.Key(row[cName]);
        if (StartsWithAny(nameKey, TotalWords) && nameKey.Length < 40)
            return false;
        return InvoiceText.TryParseNumber(row[cQty], out _) && (InvoiceText.TryParseNumber(Cell(row, cPrice), out _) || InvoiceText.TryParseNumber(Cell(row, cSum), out _));
    }

    /// <summary>Строка нумерации колонок: «1 2 3 4 5 …» (ТОРГ-12, УПД) — все непустые ячейки целые подряд.</summary>
    private static bool IsNumberingRow(string[] cells)
    {
        var numbers = new List<int>();
        foreach (var c in cells)
        {
            var t = (c ?? "").Trim();
            if (t.Length == 0)
                continue;
            if (!int.TryParse(t, out var n) || n is < 1 or > 60)
                return false;
            numbers.Add(n);
        }
        if (numbers.Count < 4)
            return false;
        for (var i = 1; i < numbers.Count; i++)
        {
            if (numbers[i] <= numbers[i - 1])
                return false;
        }
        return numbers[0] <= 2;
    }

    /// <summary>2026-10-27 (Ск-1): сумма итогов «по странице».</summary>
    private sealed class PageTotals
    {
        public double Net, Gross, Vat;
        public bool Any;
    }

    /// <summary>Строка итога относится к странице («Итого по странице», «Итого на странице», «Page total»), а не ко всей накладной.</summary>
    private static bool IsPageTotal(string[] cells)
    {
        // «Итого» и «по странице» могут лежать в соседних ячейках — смотрим текст всей строки.
        var k = string.Concat(cells.Select(c => ProductImportMapping.Key(c)));
        return k.Contains("страниц", StringComparison.Ordinal) || k.Contains("постр", StringComparison.Ordinal)
               || k.Contains("pagetotal", StringComparison.Ordinal) || k.Contains("subtotal", StringComparison.Ordinal) || k.Contains("беттин", StringComparison.Ordinal);
    }

    /// <summary>Повторная строка заголовков таблицы на следующей странице (то же слово в тех же столбцах) — это не товар.</summary>
    private static bool IsRepeatedHeader(string[] cells, string[] headerCells, InvoiceColumnMapping mapping)
    {
        var same = 0;
        var total = 0;
        foreach (var column in mapping.Columns.Values)
        {
            if (column < 0 || column >= headerCells.Length)
                continue;
            var h = ProductImportMapping.Key(headerCells[column]);
            if (h.Length == 0)
                continue;
            total++;
            if (column < cells.Length && ProductImportMapping.Key(cells[column]) == h)
                same++;
        }
        return same >= 2 && same * 2 >= total;
    }

    private static void ReadTotals(ProductFileRow row, InvoiceHeader header, int cSum, int cSumVat, int cVatSum, bool isPage, PageTotals pages)
    {
        var net = InvoiceText.ParseNumberOrNull(Cell(row, cSum));
        var gross = InvoiceText.ParseNumberOrNull(Cell(row, cSumVat));
        var vat = InvoiceText.ParseNumberOrNull(Cell(row, cVatSum));
        if (isPage)
        {
            // 2026-10-27 (Ск-1): итог страницы не становится итогом файла — копится в запасной (если «по накладной» в файле нет).
            if (net is > 0) { pages.Net += net.Value; pages.Any = true; }
            if (gross is > 0) { pages.Gross += gross.Value; pages.Any = true; }
            if (vat is > 0) { pages.Vat += vat.Value; pages.Any = true; }
            return;
        }
        // 2026-10-27 (Ск-1): итогом файла считается ПОСЛЕДНИЙ «Всего/Итого» (раньше ??= — первый, т. е. итог первой страницы).
        if (net is > 0)
            header.NetTotalFromFile = net;
        if (gross is > 0)
            header.TotalFromFile = gross;
        if (vat is > 0)
            header.VatTotalFromFile = vat;

        // Итог прописью («Итого отпущено товаров на сумму: Триста одна тысяча пятьсот рублей 50 копеек») — как в мобилке; цифры «50» там копейки, не итог.
        if (header.TotalFromFile is null && header.NetTotalFromFile is null)
        {
            foreach (var cell in row.Cells)
            {
                if (InvoiceText.ParseAmountInWords(cell) is { } words and > 0)
                {
                    header.TotalFromFile = words;
                    return;
                }
            }
        }

        // Итого цифрой не в «своей» колонке («Итого: 301 500» в одной ячейке или в последней) — берём последнее число строки.
        if (header.TotalFromFile is null && header.NetTotalFromFile is null)
        {
            for (var i = row.Cells.Length - 1; i >= 0; i--)
            {
                var text = row.Cells[i];
                var colon = text.LastIndexOf(':');
                var candidate = colon >= 0 ? text[(colon + 1)..] : text;
                if (i == 0 && colon < 0)
                    break;
                if (InvoiceText.TryParseNumber(candidate, out var v) && v > 0)
                {
                    header.TotalFromFile = v;
                    break;
                }
            }
        }
    }
}
