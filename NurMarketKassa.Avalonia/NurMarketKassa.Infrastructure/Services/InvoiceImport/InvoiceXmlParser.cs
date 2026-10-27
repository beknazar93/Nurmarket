using System.Text;
using System.Xml;
using System.Xml.Linq;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: электронная накладная / счёт-фактура в XML произвольной структуры. Безопасность: DtdProcessing.Prohibit (DOCTYPE и внешние
// сущности — отказ), XmlResolver = null, лимит символов документа. Разбор обобщённый: ищем повторяющиеся элементы-строки (Товар, Позиция,
// item, line, СвТов, InvoiceLine…), в них поля по именам-синонимам (элементы и атрибуты). Шапка — номер, дата, поставщик/покупатель с ИНН
// (по предкам СвПрод/СвПокуп, Контрагент/Роль, Seller/Buyer, AccountingSupplierParty…). Отдельно узнаётся CommerceML (КоммерческаяИнформация).

public sealed class InvoiceXmlResult
{
    public InvoiceDocument? Document { get; init; }
    public string? Error { get; init; }
}

public static class InvoiceXmlParser
{
    public const int MaxChars = 40_000_000;
    public const int MaxDepth = 100;

    private static readonly string[] RowNames =
    [
        "товар", "позиция", "строка", "строкатаблицы", "строкадокумента", "item", "line", "lineitem", "invoiceline", "product", "row", "position", "good", "tovar",
        "свтов", "сведтов", "positions", "detail", "entry", "invoiceitem", "pozitsiya", "mahsulot", "satir",
    ];

    // Имена полей (ключ — только буквы и цифры, нижний регистр).
    private static readonly Dictionary<InvoiceField, string[]> Synonyms = new()
    {
        [InvoiceField.Barcode] = ["штрихкод", "шк", "barcode", "ean", "ean13", "gtin", "upc", "barkod", "штрихкодтовара", "баркод", "идентификаторштрихкода", "штрихкоды"],
        [InvoiceField.Article] = ["артикул", "article", "sku", "art", "vendorcode", "кодтовара", "кодтов", "код", "productcode", "itemcode", "номенклатурныйкод", "артикултовара", "kod", "sellersitemidentification"],
        [InvoiceField.Name] = ["наименование", "название", "наимтов", "наименованиетовара", "номенклатура", "name", "productname", "itemname", "description", "title", "товар", "nomi",
            "аталышы", "descriptionofgoods", "наименованиепродукции"],
        [InvoiceField.Unit] = ["единица", "едизм", "единицаизмерения", "базоваяединица", "наимедизм", "unit", "uom", "measure", "unitname", "ед", "единицыизмерения", "birlik", "өлчөм"],
        [InvoiceField.Quantity] = ["количество", "колво", "колтов", "qty", "quantity", "invoicedquantity", "count", "кол", "миқдор", "miqdor", "саны", "soni", "deliveredquantity", "количествоштук"],
        [InvoiceField.Price] = ["цена", "ценазаединицу", "ценатов", "price", "unitprice", "priceamount", "purchaseprice", "баасы", "narx", "ценаединицы", "ценабезндс"],
        [InvoiceField.PriceWithVat] = ["ценасндс", "ценасналогом", "priceinclvat", "pricewithvat", "grossprice"],
        [InvoiceField.Retail] = ["ценапродажи", "розничнаяцена", "retailprice", "saleprice"],
        [InvoiceField.Sum] = ["сумма", "стоимость", "сттовбезндс", "суммабезндс", "amount", "lineextensionamount", "linetotal", "netamount", "sum", "стоимостьбезндс", "summa", "суммасы", "turnoversize"],
        [InvoiceField.SumWithVat] = ["сттовучнал", "суммасндс", "стоимостьсндс", "grosstotal", "totalwithvat", "linetotalwithvat", "суммасналогом", "суммавсего", "всего", "суммаучнал"],
        [InvoiceField.VatRate] = ["ставка", "налст", "ставкандс", "vatrate", "vat", "taxrate", "percent", "ндс", "налог", "taxpercent"],
        [InvoiceField.VatSum] = ["суммандс", "сумнал", "vatamount", "taxamount", "налогсумма", "ндссумма", "sumnal", "налогсумма", "taxtotal"],
    };

    private static readonly Dictionary<string, InvoiceField> Lookup = BuildLookup();

    private static Dictionary<string, InvoiceField> BuildLookup()
    {
        var map = new Dictionary<string, InvoiceField>(StringComparer.Ordinal);
        foreach (var (field, words) in Synonyms)
        {
            foreach (var w in words)
                map.TryAdd(ProductImportMapping.Key(w), field);
        }
        return map;
    }

    private static readonly string[] SupplierParty = ["свпрод", "продавец", "поставщик", "грузоотправитель", "seller", "supplier", "accountingsupplierparty", "sellerparty", "vendor", "shipper", "sender", "жеткирүүчү", "yetkazibberuvchi"];
    private static readonly string[] BuyerParty = ["свпокуп", "покупатель", "получатель", "грузополучатель", "плательщик", "buyer", "customer", "accountingcustomerparty", "buyerparty", "consignee", "алуучу", "xaridor"];
    private static readonly string[] PartyName = ["наиморг", "наименование", "официальноенаименование", "полноенаименование", "name", "partyname", "registrationname", "fullname", "nameorg", "организация", "наимюл"];
    private static readonly string[] PartyInn = ["инн", "иннюл", "иннфл", "inn", "taxid", "companyid", "tin", "пин", "stir", "иннконтрагента"];

    // ------------------------------------------------------------------ вход

    public static InvoiceXmlResult Parse(byte[] bytes, string fileName)
    {
        XDocument xml;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxChars,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true,
            };
            // 2026-10-27 (ревью Ск-18): лимит вложенности элементов — сначала быстрый проход без построения дерева, как в мобилке (глубина ≤ 100).
            using (var scan = XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
            {
                while (scan.Read())
                {
                    if (scan.Depth > MaxDepth)
                        throw new XmlException($"слишком глубокая вложенность элементов (больше {MaxDepth})");
                }
            }
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            xml = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Prohibit", StringComparison.OrdinalIgnoreCase))
        {
            PosLogger.Log($"INVOICE_IMPORT: XML отклонён (DOCTYPE/внешние сущности запрещены): {fileName}.", "WARNING");
            return new InvoiceXmlResult
            {
                Error = Tr.T("XML-файл содержит DOCTYPE (внешние определения) — по соображениям безопасности такие файлы не открываются. Выгрузите накладную без DOCTYPE.",
                    "XML файлда DOCTYPE (тышкы аныктамалар) бар — коопсуздук үчүн мындай файлдар ачылбайт. Накладнойду DOCTYPEсиз чыгарыңыз.",
                    "The XML file contains a DOCTYPE (external definitions) — such files are not opened for security reasons. Export the invoice without DOCTYPE.",
                    "XML dosyası DOCTYPE (harici tanımlar) içeriyor — güvenlik nedeniyle bu tür dosyalar açılmaz. Faturayı DOCTYPE olmadan dışa aktarın.",
                    "XML faylda DOCTYPE (tashqi ta'riflar) bor — xavfsizlik uchun bunday fayllar ochilmaydi. Yuk xatini DOCTYPEsiz chiqaring."),
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            PosLogger.Log($"INVOICE_IMPORT: XML не прочитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return new InvoiceXmlResult
            {
                Error = Tr.T("XML-файл повреждён или это не XML (" + FirstLine(ex.Message) + "). Откройте файл в программе, из которой он выгружен, и сохраните заново.",
                    "XML файл бузулган же XML эмес (" + FirstLine(ex.Message) + "). Файлды чыгарган программада ачып, кайра сактаңыз.",
                    "The XML file is damaged or is not XML (" + FirstLine(ex.Message) + "). Re-export it from the program that produced it.",
                    "XML dosyası bozuk veya XML değil (" + FirstLine(ex.Message) + "). Dosyayı oluşturan programdan yeniden dışa aktarın.",
                    "XML fayl shikastlangan yoki XML emas (" + FirstLine(ex.Message) + "). Faylni yaratgan dasturdan qayta chiqaring."),
            };
        }

        var root = xml.Root;
        if (root is null)
            return new InvoiceXmlResult { Error = NotInvoice() };

        var isCommerceMl = Key(root).StartsWith("коммерческаяинформация", StringComparison.Ordinal) || Key(root) == "commercialinformation";
        var rows = FindRows(root, out var rowElement);
        var doc = new InvoiceDocument
        {
            Header = new InvoiceHeader(),
            Format = isCommerceMl ? "XML (CommerceML)" : "XML",
            FileName = fileName,
        };
        if (rows.Count == 0)
        {
            return new InvoiceXmlResult
            {
                Error = NotInvoice(),
            };
        }

        ReadHeader(root, rows, doc.Header, isCommerceMl);
        var index = 0;
        foreach (var el in rows)
        {
            index++;
            var item = ReadItem(el, index);
            if (item is not null)
                doc.Items.Add(item);
        }

        if (doc.Items.Count == 0)
            return new InvoiceXmlResult { Error = NotInvoice() };
        if (rowElement.Length > 0 && !RowNames.Contains(ProductImportMapping.Key(rowElement)))
            doc.Warnings.Add(Tr.T($"Строки товаров взяты из элементов «{rowElement}».", $"Товар саптары «{rowElement}» элементтеринен алынды.",
                $"Product lines were taken from “{rowElement}” elements.", $"Ürün satırları «{rowElement}» öğelerinden alındı.", $"Mahsulot qatorlari «{rowElement}» elementlaridan olindi."));
        return new InvoiceXmlResult { Document = doc };
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private static string NotInvoice() => Tr.T(
        "Не удалось найти в XML строки накладной (товар, количество, цена). Поддерживаются: CommerceML (1С), счёт-фактура / УПД (ФНС), UBL Invoice и XML с повторяющимися элементами «Товар / Позиция / item / line» с полями «наименование, количество, цена». Если у вас другой формат — пришлите образец или загрузите накладную Excel/CSV.",
        "XMLден накладнойдун саптары (товар, саны, баасы) табылган жок. Колдоого алынат: CommerceML (1С), эсеп-фактура / УПД (ФНС), UBL Invoice жана «Товар / Позиция / item / line» кайталануучу элементтери бар XML. Башка формат болсо — үлгүсүн жөнөтүңүз же Excel/CSV жүктөңүз.",
        "Could not find invoice lines (product, quantity, price) in the XML. Supported: CommerceML (1C), tax invoice / UPD, UBL Invoice, and XML with repeating “Product / Position / item / line” elements carrying name, quantity and price. For another format send a sample or load an Excel/CSV invoice.",
        "XML'de fatura satırları (ürün, miktar, fiyat) bulunamadı. Desteklenenler: CommerceML (1C), vergi faturası / UPD, UBL Invoice ve ad, miktar, fiyat içeren tekrarlayan «Product / Position / item / line» öğeli XML. Başka biçim için örnek gönderin ya da Excel/CSV yükleyin.",
        "XML ichida yuk xati qatorlari (mahsulot, miqdor, narx) topilmadi. Qo'llab-quvvatlanadi: CommerceML (1C), schyot-faktura / UPD, UBL Invoice va nomi, miqdori, narxi bor takrorlanuvchi «Product / Position / item / line» elementli XML. Boshqa format uchun namuna yuboring yoki Excel/CSV yuklang.");

    private static string Key(XElement e) => ProductImportMapping.Key(e.Name.LocalName);

    // ------------------------------------------------------------------ строки

    /// <summary>Пары «имя поля → значение» элемента-строки: атрибуты и листовые потомки (ближние — раньше).</summary>
    private static IEnumerable<(string Name, string Value)> Pairs(XElement row)
    {
        foreach (var a in row.Attributes())
        {
            if (!a.IsNamespaceDeclaration && a.Value.Length > 0)
                yield return (ProductImportMapping.Key(a.Name.LocalName), a.Value);
        }

        var queue = new Queue<XElement>();
        queue.Enqueue(row);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 400)
        {
            var el = queue.Dequeue();
            foreach (var child in el.Elements())
            {
                var childKey = Key(child);
                if (!child.HasElements)
                {
                    var text = child.Value.Trim();
                    if (text.Length > 0)
                    {
                        // Код единицы («796») вместо названия — берём «НаименованиеПолное» с этого же элемента (CommerceML: «Штука»).
                        if (text.All(char.IsDigit) && child.Attributes().FirstOrDefault(a => ProductImportMapping.Key(a.Name.LocalName) == "наименованиеполное") is { Value.Length: > 0 } fullName)
                            yield return (childKey, fullName.Value);
                        else
                        // «Сумма» внутри «Налог» — сумма налога, а не строки.
                        if (childKey is "сумма" or "amount" && child.Parent is { } taxParent && Key(taxParent) is "налог" or "tax" or "taxsubtotal" or "taxtotal")
                            yield return ("суммандс", text);
                        else
                            yield return (childKey, text);
                        // «ID» под StandardItemIdentification — штрихкод (UBL), под SellersItemIdentification — артикул.
                        if (childKey is "id" or "ид" or "value" && child.Parent is { } p && Key(p).Contains("identification", StringComparison.Ordinal))
                            yield return (Key(p), text);
                    }
                }
                else
                    queue.Enqueue(child);
                foreach (var a in child.Attributes())
                {
                    if (!a.IsNamespaceDeclaration && a.Value.Length > 0)
                        yield return (ProductImportMapping.Key(a.Name.LocalName), a.Value);
                }
            }
        }
    }

    private static Dictionary<InvoiceField, string> Fields(XElement row)
    {
        var map = new Dictionary<InvoiceField, string>();
        foreach (var (name, value) in Pairs(row))
        {
            if (Lookup.TryGetValue(name, out var field) && !map.ContainsKey(field))
            {
                // «Сумма» внутри «Налог» — сумма налога, а не строки: пропускаем пока не сложится явная; решается порядком обхода (ближние раньше).
                map[field] = value;
            }
            else if (name == "standarditemidentification" && !map.ContainsKey(InvoiceField.Barcode))
                map[InvoiceField.Barcode] = value;
        }
        return map;
    }

    private static List<XElement> FindRows(XElement root, out string chosenName)
    {
        chosenName = "";
        List<XElement>? best = null;
        double bestScore = 0;
        foreach (var parent in root.DescendantsAndSelf())
        {
            foreach (var group in parent.Elements().GroupBy(e => e.Name.LocalName))
            {
                var list = group.ToList();
                // Контейнер не может быть строкой, у которой потомки — тоже строки (Товары/Товар/СвойстваТовара).
                var f = Fields(list[0]);
                var hasName = f.ContainsKey(InvoiceField.Name) || f.ContainsKey(InvoiceField.Barcode);
                if (!hasName || !f.ContainsKey(InvoiceField.Quantity))
                    continue;
                var score = list.Count * (1 + f.Count * 0.5);
                if (RowNames.Contains(Key(list[0])))
                    score *= 1.5;
                if (f.ContainsKey(InvoiceField.Price) || f.ContainsKey(InvoiceField.Sum) || f.ContainsKey(InvoiceField.SumWithVat))
                    score *= 1.3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = list;
                    chosenName = group.Key;
                }
            }
        }

        return best ?? [];
    }

    private static InvoiceItem? ReadItem(XElement el, int index)
    {
        var f = Fields(el);
        string Get(InvoiceField field) => f.TryGetValue(field, out var v) ? v.Trim() : "";
        var name = System.Text.RegularExpressions.Regex.Replace(Get(InvoiceField.Name), @"\s+", " ");
        var barcode = InvoiceText.CleanBarcode(Get(InvoiceField.Barcode), restoreLeadingZero: true);
        var article = Get(InvoiceField.Article);
        if (barcode.Length == 0 && InvoiceText.LooksLikeBarcode(article))
            barcode = InvoiceText.CleanBarcode(article);
        if (name.Length == 0 && barcode.Length == 0)
            return null;

        var item = new InvoiceItem
        {
            SourceRow = index,
            Barcode = barcode,
            Article = article,
            Name = name,
            Unit = Get(InvoiceField.Unit),
            Price = InvoiceText.ParseNumberOrNull(Get(InvoiceField.Price)),
            PriceWithVat = InvoiceText.ParseNumberOrNull(Get(InvoiceField.PriceWithVat)),
            RetailPrice = InvoiceText.ParseNumberOrNull(Get(InvoiceField.Retail)),
            Sum = InvoiceText.ParseNumberOrNull(Get(InvoiceField.Sum)),
            SumWithVat = InvoiceText.ParseNumberOrNull(Get(InvoiceField.SumWithVat)),
            VatSum = InvoiceText.ParseNumberOrNull(Get(InvoiceField.VatSum)),
        };
        var qtyText = Get(InvoiceField.Quantity);
        if (InvoiceText.TryParseNumber(qtyText, out var qty))
            item.Quantity = qty;
        else if (qtyText.Length > 0)
            item.Warnings.Add(Tr.T($"количество не число: «{qtyText}»", $"саны сан эмес: «{qtyText}»", $"quantity is not a number: “{qtyText}”",
                $"miktar sayı değil: «{qtyText}»", $"miqdor son emas: «{qtyText}»"));
        var rate = Get(InvoiceField.VatRate);
        item.VatText = rate;
        if (rate.Length > 0 && InvoiceText.TryParseNumber(rate, out var pct))
            item.VatPercent = pct > 0 && pct < 1 ? Math.Round(pct * 100, 2) : pct;
        if (barcode.Length > 0 && !InvoiceText.HasValidCheckDigit(barcode))
            item.Warnings.Add(Tr.T($"у штрихкода {barcode} не сходится контрольная цифра", $"{barcode} штрих-кодунун контролдук цифрасы дал келбейт",
                $"barcode {barcode} has a wrong check digit", $"{barcode} barkodunun kontrol hanesi uyuşmuyor", $"{barcode} shtrix-kodining nazorat raqami mos kelmaydi"));
        // 2026-10-27 (ревью Ск-15): CommerceML — «УчтеноВСумме = true» у налога: цена и сумма строки УЖЕ с НДС. Раньше «Закупка с НДС» прибавляла налог второй раз.
        if (item.VatPercent is > 0 && Pairs(el).Any(p => p.Name == "учтеновсумме" && string.Equals(p.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase) || p.Name == "учтеновсумме" && p.Value.Trim() == "1"))
        {
            var r = 1 + item.VatPercent.Value / 100;
            if (item.Price is > 0 && item.PriceWithVat is not > 0)
            {
                item.PriceWithVat = item.Price;
                item.Price = Math.Round(item.Price.Value / r, 4);
            }
            if (item.Sum is > 0 && item.SumWithVat is not > 0)
            {
                item.SumWithVat = item.Sum;
                item.Sum = Math.Round(item.Sum.Value / r, 2);
            }
        }
        if (item.UnitPrice(false) <= 0)
            item.Warnings.Add(Tr.T("нет цены", "баасы жок", "no price", "fiyat yok", "narx yo'q"));
        return item;
    }

    // ------------------------------------------------------------------ шапка

    private static void ReadHeader(XElement root, List<XElement> rows, InvoiceHeader h, bool commerceMl)
    {
        var rowSet = new HashSet<XElement>(rows);
        bool InsideRow(XElement e) => e.AncestorsAndSelf().Any(rowSet.Contains);

        // 1. Участники: по предкам-ролям и по «Контрагент/Роль» CommerceML.
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!el.HasElements && !el.HasAttributes)
                continue;
            if (InsideRow(el))
                continue;
            var k = Key(el);
            string? role = null;
            if (SupplierParty.Contains(k))
                role = "s";
            else if (BuyerParty.Contains(k))
                role = "b";
            else if (k is "контрагент" or "party" or "организация")
            {
                var roleText = ProductImportMapping.Key(el.Elements().FirstOrDefault(c => Key(c) is "роль" or "role")?.Value);
                if (roleText.StartsWith("продав", StringComparison.Ordinal) || roleText.StartsWith("постав", StringComparison.Ordinal))
                    role = "s";
                else if (roleText.StartsWith("покуп", StringComparison.Ordinal) || roleText.StartsWith("получ", StringComparison.Ordinal))
                    role = "b";
            }
            if (role is null)
                continue;

            string name = "", inn = "", kpp = "";
            foreach (var (n, v) in Pairs(el))
            {
                if (name.Length == 0 && PartyName.Contains(n))
                    name = v;
                else if (inn.Length == 0 && PartyInn.Contains(n))
                    inn = InvoiceText.CleanInn(v);
                else if (kpp.Length == 0 && n is "кпп" or "kpp")
                    kpp = new string(v.Where(char.IsDigit).ToArray());
            }
            // Название может лежать атрибутом самого элемента-роли (<Продавец Наименование="…"/>).
            if (role == "s" && h.SupplierName.Length == 0 && name.Length > 0)
            {
                h.SupplierName = name;
                h.SupplierInn = inn;
                h.SupplierKpp = kpp;
            }
            else if (role == "b" && h.BuyerName.Length == 0 && name.Length > 0)
            {
                h.BuyerName = name;
                h.BuyerInn = inn;
            }
        }

        // 2. Номер, дата, итог, валюта: атрибуты и листья вне строк товаров и вне участников.
        foreach (var el in root.DescendantsAndSelf())
        {
            if (InsideRow(el))
                continue;
            if (el.AncestorsAndSelf().Any(a => SupplierParty.Contains(Key(a)) || BuyerParty.Contains(Key(a))))
                continue;
            foreach (var a in el.Attributes())
                Apply(h, ProductImportMapping.Key(a.Name.LocalName), a.Value, el);
            if (!el.HasElements && el.Value.Trim().Length > 0)
                Apply(h, Key(el), el.Value.Trim(), el);
        }

        if (h.TotalFromFile is null && h.NetTotalFromFile is not null)
            h.TotalFromFile = h.NetTotalFromFile;
    }

    private static void Apply(InvoiceHeader h, string k, string value, XElement owner)
    {
        if (value.Length == 0 || value.Length > 200)
            return;
        if (h.Number.Length == 0 && k is "номерсчф" or "номердок" or "номер" or "номердокумента" or "number" or "invoicenumber" or "id" or "nomer" or "invoiceno" or "номернакладной" or "документномери" or "номерсф")
        {
            // «Ид» корня CommerceML — служебный; берём только короткие читаемые номера.
            if (!(k == "id" && (value.Length > 30 || owner.Parent?.Parent is not null)))
                h.Number = value;
        }
        else if (h.Date is null && k is "датасчф" or "датадок" or "дата" or "датадокумента" or "date" or "issuedate" or "invoicedate" or "sana" or "датасоставления" or "датанакладной")
            h.Date = InvoiceText.ParseDate(value);
        else if (h.Currency.Length == 0 && k is "валюта" or "кодокв" or "currency" or "documentcurrencycode" or "currencycode")
            h.Currency = InvoiceText.CurrencyFromCode(value);
        else if (k is "всегоопл" or "стоимостьвсего" or "сттовучналвсего" or "сттоввсего" or "payableamount" or "totalamount" or "taxinclusiveamount" or "итого" or "total" or "суммавсего" or "сумма" or "суммадокумента")
        {
            // «Сумма» документа CommerceML: используем как итог с налогом, если иного нет
            if (InvoiceText.TryParseNumber(value, out var total) && total > 0 && h.TotalFromFile is null)
                h.TotalFromFile = total;
        }
        else if (k is "сттовбезндсвсего" or "суммабезндсвсего" or "taxexclusiveamount" or "lineextensionamount")
        {
            if (owner.Parent is not null && InvoiceText.TryParseNumber(value, out var net) && net > 0 && h.NetTotalFromFile is null)
                h.NetTotalFromFile = net;
        }
        else if (k is "суммаваловаяндс" or "суммандсвсего" or "сумналвсего" or "taxamount")
        {
            if (InvoiceText.TryParseNumber(value, out var vat) && vat > 0 && h.VatTotalFromFile is null)
                h.VatTotalFromFile = vat;
        }
    }
}
