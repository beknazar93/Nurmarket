using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: поля накладной и автоподбор столбцов таблицы по заголовкам (ru / ky / en / uz / tr). Отдельно от ProductImportMapping:
// у накладной свои поля (сумма, НДС, цена с НДС) и нет «наценки/категории». Ключ заголовка — ProductImportMapping.Key
// (только буквы и цифры, нижний регистр, ё = е).

public enum InvoiceField
{
    Barcode,
    Article,
    Name,
    Unit,
    Quantity,
    Price,
    PriceWithVat,
    Retail,
    Sum,
    SumWithVat,
    VatRate,
    VatSum,
}

/// <summary>Какой столбец таблицы какому полю накладной соответствует. Один столбец — одно поле.</summary>
public sealed class InvoiceColumnMapping
{
    private readonly Dictionary<InvoiceField, int> _columns = new();

    public bool FromMemory { get; internal set; }

    public IReadOnlyDictionary<InvoiceField, int> Columns => _columns;

    public int ColumnOf(InvoiceField field) => _columns.TryGetValue(field, out var c) ? c : -1;

    /// <summary>Назначить поле столбцу (-1 — «нет»). Столбец, занятый другим полем, у того отнимается.</summary>
    public void Set(InvoiceField field, int column)
    {
        if (column < 0)
        {
            _columns.Remove(field);
            return;
        }

        foreach (var other in _columns.Where(kv => kv.Value == column && kv.Key != field).Select(kv => kv.Key).ToList())
            _columns.Remove(other);
        _columns[field] = column;
    }

    /// <summary>Хватает для разбора: название (или штрихкод), количество и хоть какая-то цена/сумма.</summary>
    public bool IsComplete =>
        (_columns.ContainsKey(InvoiceField.Name) || _columns.ContainsKey(InvoiceField.Barcode))
        && _columns.ContainsKey(InvoiceField.Quantity)
        && (_columns.ContainsKey(InvoiceField.Price) || _columns.ContainsKey(InvoiceField.PriceWithVat) || _columns.ContainsKey(InvoiceField.Sum) || _columns.ContainsKey(InvoiceField.SumWithVat));

    public static string FieldLabel(InvoiceField field) => field switch
    {
        InvoiceField.Barcode => Tr.T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"),
        InvoiceField.Article => Tr.T("Артикул / код", "Артикул / код", "Article / code", "Ürün kodu", "Artikul / kod"),
        InvoiceField.Name => Tr.T("Наименование", "Аталышы", "Name", "Ürün adı", "Nomi"),
        InvoiceField.Unit => Tr.T("Ед. изм.", "Өлчөм бирдиги", "Unit", "Birim", "Birlik"),
        InvoiceField.Quantity => Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
        InvoiceField.Price => Tr.T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
        InvoiceField.PriceWithVat => Tr.T("Цена с НДС", "НДС менен баасы", "Price incl. VAT", "KDV'li fiyat", "QQS bilan narx"),
        InvoiceField.Retail => Tr.T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"),
        InvoiceField.Sum => Tr.T("Сумма (без НДС)", "Сумма (НДСсиз)", "Amount (excl. VAT)", "Tutar (KDV hariç)", "Summa (QQSsiz)"),
        InvoiceField.SumWithVat => Tr.T("Сумма с НДС", "НДС менен сумма", "Amount incl. VAT", "KDV'li tutar", "QQS bilan summa"),
        InvoiceField.VatRate => Tr.T("Ставка НДС", "НДС ставкасы", "VAT rate", "KDV oranı", "QQS stavkasi"),
        _ => Tr.T("Сумма НДС", "НДС суммасы", "VAT amount", "KDV tutarı", "QQS summasi"),
    };

    // ------------------------------------------------------------------ автоподбор

    private static bool Has(string key, params string[] words)
    {
        foreach (var w in words)
        {
            if (key.Contains(w, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool Is(string key, params string[] words) => Array.IndexOf(words, key) >= 0;

    private static readonly string[] VatWords = ["ндс", "vat", "qqs", "kdv", "налог", "tax", "салык"];
    private static readonly string[] SumWords = ["сумм", "sum", "amount", "стоимост", "tutar", "жыйынтык", "итого", "jami", "total", "всего", "баасынынжыйынтыгы"];

    /// <summary>Какому полю соответствует заголовок. null — столбец не нужен («№ п/п», «Код по ОКЕИ», «Количество мест»…).</summary>
    public static InvoiceField? Classify(string? header)
    {
        var k = ProductImportMapping.Key(header);
        if (k.Length == 0)
            return null;
        // Коды по классификаторам, номер строки, количество мест, масса — не штрихкод и не количество товара.
        if (Has(k, "океи", "okei", "оквэд", "гтд", "страна") || Is(k, "пп", "ппп", "no", "n", "номер", "номерпп", "num", "nr") || (k.StartsWith("мест", StringComparison.Ordinal) || Has(k, "колмест", "колвомест", "количествомест", "коливмест", "местштук", "вместе")))
            return null;
        if (Has(k, "масса", "брутто", "нетто") && !Has(k, "колво", "количеств"))
            return null;
        // 2026-10-27 (ревью Ск-10): ТОРГ-12 — «Количество в одном месте» (штук в упаковке) — не количество товара; настоящее — «Количество (масса нетто)».
        if (Has(k, "водномместе", "водном", "вединомместе", "вупаковке", "вкоробке", "одномместе"))
            return null;
        // 2026-10-27 (ревью Ск-18): «Сумма скидки» — не сумма строки (иначе сумма строки = скидка).
        if (Has(k, "скидк", "discount", "chegirma", "жеңилдик", "жеңилдиги"))
            return null;

        var vat = Has(k, VatWords);
        var sumWord = Has(k, SumWords);
        var priceWord = Has(k, "цена", "price", "баасы", "баа", "narx", "fiyat", "прайс") && !Has(k, "ценность");
        // 2026-10-27 (ревью Ск-9): «Цена без НДС» — это цена (раньше из-за слова НДС шла в «Ставку НДС», цены выходили 0 или равными ставке).
        if (vat && priceWord && Has(k, "без", "excl", "ндссиз", "kdvhariç", "kdvharic", "qqssiz", "bezndc") && !Has(k, "сндс", "сучетомндс", "включаяндс", "вклндс", "withvat", "inclvat"))
            return Has(k, "розн", "продаж", "retail", "sale", "сатуу", "sotuv", "satış", "satis") ? InvoiceField.Retail : InvoiceField.Price;

        if (vat && sumWord && !Has(k, "сндс", "сучетомндс", "включаяндс", "вклндс", "withvat", "inclvat", "inclusive", "всегосндс", "sumsndc"))
        {
            // «Сумма НДС», «НДС сумма», «Сумма налога» — сумма самого налога; «Сумма без НДС» — обычная сумма.
            if (Has(k, "без", "excl", "net", "ндссиз", "kdvhariç", "kdvharic", "qqssiz", "бездс"))
                return InvoiceField.Sum;
            return InvoiceField.VatSum;
        }

        if (Has(k, "сндс", "сучетомндс", "включаяндс", "вклндс", "withvat", "inclvat", "всегосндс", "сналогом", "грос", "gross", "ндсменен", "kdvdahil", "qqsbilan", "qqsli"))
        {
            if (priceWord && !sumWord)
                return InvoiceField.PriceWithVat;
            return InvoiceField.SumWithVat;
        }

        if (vat)
        {
            if (Has(k, "ставк", "rate", "oran", "stavka", "процент", "percent", "%") || Is(k, "ндс", "vat", "qqs", "kdv", "ндспроцент", "налог", "tax"))
                return InvoiceField.VatRate;
            return InvoiceField.VatRate;
        }

        if (Has(k, "розн", "продаж", "retail", "sale", "сатуу", "sotuv", "satış", "satis") && priceWord)
            return InvoiceField.Retail;

        if (priceWord)
            return InvoiceField.Price;
        if (Is(k, "закупка", "закупочная", "входящаяцена", "cost", "costprice", "себестоимость", "sebestoimost"))
            return InvoiceField.Price;

        if (sumWord)
            return InvoiceField.Sum;

        if (Has(k, "штрихкод", "barcode", "barkod", "shtrixkod", "баркод", "gtin") || Is(k, "шк", "ean", "ean13", "ean8", "upc", "штрих", "штрихкоды"))
            return InvoiceField.Barcode;

        if (Is(k, "артикул", "art", "арт", "article", "sku", "код", "кодтовара", "кодтов", "kod", "артикултовара", "номенклатурныйкод", "кодномер", "кодномерклатуры", "коднач", "vendorcode", "артикулпоставщика", "productcode", "itemcode", "кодпоставщика")
            || (Has(k, "артикул", "article") && !Has(k, "наименован")))
            return InvoiceField.Article;

        if (Has(k, "колво", "количеств", "qty", "quantity", "miktar", "miqdor", "отпущено", "принято") || Is(k, "кол", "саны", "сан", "soni", "adet", "штук", "шт", "count", "объем"))
            return InvoiceField.Quantity;

        if (Has(k, "едизм", "единиц", "unit", "uom", "өлчөм", "бирдик", "birlik", "birim", "олчо") || Is(k, "ед", "едизмерения", "м", "meas", "measure"))
            return InvoiceField.Unit;

        if (Has(k, "наименован", "название", "номенклатура", "описание", "name", "product", "description", "аталышы", "nomi", "mahsulot", "ürün", "urun", "товар", "услуг", "ассортимент") || Is(k, "аты", "title", "ad"))
            return InvoiceField.Name;

        return null;
    }

    /// <summary>Насколько заголовок похож на «настоящее» количество товара: «…масса нетто» 3, «Количество» / «Кол-во» 2, прочее 1.</summary>
    private static int QtyRank(string? header)
    {
        var k = ProductImportMapping.Key(header);
        if (Has(k, "нетто"))
            return 3;
        return Is(k, "количество", "колво", "кол", "qty", "quantity", "miktar", "miqdor", "саны", "soni") ? 2 : 1;
    }

    /// <summary>Подобрать столбцы по строке заголовков.</summary>
    public static InvoiceColumnMapping AutoDetect(IReadOnlyList<string> headers)
    {
        var result = new InvoiceColumnMapping();
        var qtyRank = -1;
        for (var i = 0; i < headers.Count; i++)
        {
            if (Classify(headers[i]) is not { } field)
                continue;
            // 2026-10-27 (ревью Ск-10): из нескольких «количеств» ТОРГ-12 берём «Количество (масса нетто)» / «Количество», а не групповой заголовок или «мест».
            if (field == InvoiceField.Quantity && result._columns.ContainsKey(field))
            {
                var rank = QtyRank(headers[i]);
                if (rank > qtyRank)
                {
                    result._columns[field] = i;
                    qtyRank = rank;
                }
                continue;
            }
            if (!result._columns.ContainsKey(field))
            {
                result._columns[field] = i;
                if (field == InvoiceField.Quantity)
                    qtyRank = QtyRank(headers[i]);
            }
        }

        // Цена за единицу с НДС вместо обычной — тоже годится как цена закупки.
        return result;
    }

    /// <summary>Сколько разных полей узнано в строке; строка считается заголовком, если там есть название/штрихкод, количество и ещё одно поле.</summary>
    public static int Score(IReadOnlyList<string> cells, out InvoiceColumnMapping mapping)
    {
        mapping = AutoDetect(cells);
        var has = mapping.Columns;
        var nameOrCode = has.ContainsKey(InvoiceField.Name) || has.ContainsKey(InvoiceField.Barcode);
        if (!nameOrCode || !has.ContainsKey(InvoiceField.Quantity))
            return 0;
        var priceLike = has.ContainsKey(InvoiceField.Price) || has.ContainsKey(InvoiceField.PriceWithVat) || has.ContainsKey(InvoiceField.Sum) || has.ContainsKey(InvoiceField.SumWithVat);
        if (!priceLike)
            return 0;
        return has.Count;
    }
}
