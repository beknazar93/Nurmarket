using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: шапка накладной из «свободных» строк таблицы (Excel/CSV): «Номер документа: 104», «Дата составления: 27 октября 2026 г.»,
// «Поставщик: ООО «Х»», «ИНН/КПП: 7725896321 / 772501001», «Покупатель: ИП …», «ИНН: …». ИНН относится к тому участнику (поставщик /
// покупатель), который назван последним, — так покупатель не подменяет поставщика. Ничего не угадывает дальше подписей-ключей.

public sealed class InvoiceHeaderReader
{
    private enum Party { None, Supplier, Buyer }

    private Party _party = Party.None;
    private bool _supplierSet, _buyerSet;

    public InvoiceHeader Header { get; } = new();

    /// <summary>Строка «Итого…» таблицы разбирается самой таблицей (по столбцам) — здесь «Итого: 84» (количество) итогом не считается.</summary>
    public bool IgnoreTotals { get; set; }

    /// <summary>Текст подвала накладной (прописью итог, «руб.») — для определения валюты.</summary>
    public string CurrencyHint { get; private set; } = "";

    private static readonly string[] SupplierKeys =
    [
        "поставщик", "продавец", "грузоотправитель", "отправитель", "отпустил", "supplier", "seller", "vendor", "sender", "from", "shipper",
        "жеткирүүчү", "жеткирувчу", "сатуучу", "yetkazibberuvchi", "yetkazuvchi", "sotuvchi", "tedarikçi", "tedarikci", "satıcı", "satici",
    ];

    private static readonly string[] BuyerKeys =
    [
        "покупатель", "получатель", "грузополучатель", "плательщик", "заказчик", "buyer", "customer", "consignee", "billto", "shipto", "client",
        "сатыпалуучу", "алуучу", "кабылалуучу", "xaridor", "oluvchi", "buyurtmachi", "alıcı", "alici", "müşteri", "musteri",
    ];

    private static readonly Regex InnInline = new(@"(?:ИНН|INN|ПИН|PIN|СТИР|STIR|TIN)\s*(?:/\s*КПП)?\s*[:№#-]?\s*(?<inn>\d{9,14})(?:\s*/\s*(?<kpp>\d{9}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Title = new(
        @"(?:накладная|накладной|накладную|счет[\s-]*фактура|счёт[\s-]*фактура|упд\b|invoice|жүк\s*кат|жук\s*кат|yuk\s*xati|nakladnoy|delivery\s+note|irsaliye|акт\s+приема)[^\d№#]{0,40}(?:№|N°|N\s|No\.?\s*|#)\s*(?<n>[\p{L}\p{N}][\p{L}\p{N}\-/_.]*)(?:\s*(?:от|dated|from|күнү|sanasi|tarihli|даты?)\s*(?<d>.{4,40}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Передать очередную строку таблицы (ячейки как есть).</summary>
    public void Feed(IReadOnlyList<string> cells)
    {
        var filled = cells.Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).ToList();
        if (filled.Count == 0)
            return;

        for (var i = 0; i < filled.Count; i++)
        {
            var text = filled[i];
            var colon = text.IndexOf(':');
            if (colon > 0 && colon <= 45)
            {
                var key = text[..colon].Trim();
                var value = text[(colon + 1)..].Trim();
                if (value.Length == 0 && i + 1 < filled.Count)
                {
                    value = filled[i + 1];
                    i++;
                }
                if (value.Length > 0 && Apply(key, value))
                    continue;
            }
            else if (i + 1 < filled.Count && IsLabel(text))
            {
                if (Apply(text, filled[i + 1]))
                {
                    i++;
                    continue;
                }
            }

            if (Title.Match(text) is { Success: true } m)
            {
                if (Header.Number.Length == 0)
                    Header.Number = m.Groups["n"].Value.Trim().TrimEnd('.', ',');
                if (Header.Date is null)
                    Header.Date = m.Groups["d"].Success ? InvoiceText.ParseDate(m.Groups["d"].Value) : InvoiceText.ParseDate(text[(m.Index + m.Length)..]);
            }
            else if (InvoiceText.DetectCurrency(text) is { Length: > 0 } cur && CurrencyHint.Length == 0 && (text.Contains("сумму", StringComparison.OrdinalIgnoreCase) || text.Contains("итого", StringComparison.OrdinalIgnoreCase) || text.Contains("всего", StringComparison.OrdinalIgnoreCase) || text.Contains("total", StringComparison.OrdinalIgnoreCase)))
                CurrencyHint = cur;
        }
    }

    private static bool IsLabel(string text)
    {
        var k = ProductImportMapping.Key(text);
        return k.Length is >= 3 and <= 30 && (StartsWithAny(k, SupplierKeys) || StartsWithAny(k, BuyerKeys) || k.StartsWith("инн", StringComparison.Ordinal)
            || k is "номердокумента" or "номер" or "дата" or "датасоставления" or "number" or "date" or "валюта" or "currency" or "nomer" or "sana");
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

    /// <summary>true — подпись узнана и значение использовано.</summary>
    private bool Apply(string rawKey, string value)
    {
        var k = ProductImportMapping.Key(rawKey);
        if (k.Length == 0)
            return false;

        // ИНН / КПП
        if (k.StartsWith("инн", StringComparison.Ordinal) || k.StartsWith("inn", StringComparison.Ordinal) || k is "пин" or "pin" or "tin" or "stir" or "идентификационныйномер" or "налоговыйномер" || k.StartsWith("кпп", StringComparison.Ordinal))
        {
            var parts = value.Split('/', StringSplitOptions.TrimEntries);
            var inn = InvoiceText.CleanInn(parts[0]);
            var kpp = parts.Length > 1 ? new string(parts[1].Where(char.IsDigit).ToArray()) : "";
            if (k.StartsWith("кпп", StringComparison.Ordinal))
                kpp = new string(parts[0].Where(char.IsDigit).ToArray());
            if (inn.Length == 0 && kpp.Length == 0)
                return false;
            if (_party == Party.Buyer)
            {
                if (inn.Length > 0)
                    Header.BuyerInn = inn;
            }
            else
            {
                if (inn.Length > 0 && Header.SupplierInn.Length == 0)
                    Header.SupplierInn = inn;
                if (kpp.Length == 9)
                    Header.SupplierKpp = kpp;
            }
            return true;
        }

        if (StartsWithAny(k, SupplierKeys) && !k.StartsWith("отпустил", StringComparison.Ordinal))
        {
            _party = Party.Supplier;
            var name = TakeName(value, out var inn, out var kpp);
            if (!_supplierSet && name.Length > 0)
            {
                Header.SupplierName = name;
                _supplierSet = true;
                if (inn.Length > 0)
                    Header.SupplierInn = inn;
                if (kpp.Length > 0)
                    Header.SupplierKpp = kpp;
            }
            return true;
        }

        if (StartsWithAny(k, BuyerKeys))
        {
            _party = Party.Buyer;
            var name = TakeName(value, out var inn, out _);
            if (!_buyerSet && name.Length > 0)
            {
                Header.BuyerName = name;
                _buyerSet = true;
                if (inn.Length > 0)
                    Header.BuyerInn = inn;
            }
            return true;
        }

        if (IsNumberKey(k))
        {
            var n = value.Trim().TrimStart('№', '#', ' ').Trim();
            var cut = n.IndexOfAny([' ', '\t', ',', ';']);
            // «104 от 27.10.2026» — берём число, остальное пробуем как дату
            if (cut > 0)
            {
                var rest = n[cut..];
                n = n[..cut];
                if (Header.Date is null)
                    Header.Date = InvoiceText.ParseDate(rest);
            }
            if (n.Length > 0 && n.Length <= 40 && Header.Number.Length == 0)
                Header.Number = n.TrimEnd('.', ',');
            return true;
        }

        if (IsDateKey(k))
        {
            if (InvoiceText.ParseDate(value) is { } d)
            {
                if (Header.Date is null)
                    Header.Date = d;
                return true;
            }
            return false;
        }

        if (k.StartsWith("валюта", StringComparison.Ordinal) || k.StartsWith("currency", StringComparison.Ordinal))
        {
            if (InvoiceText.DetectCurrency(value) is { Length: > 0 } c)
                Header.Currency = c;
            return true;
        }

        if (IgnoreTotals && (k.StartsWith("итого", StringComparison.Ordinal) || k.StartsWith("всего", StringComparison.Ordinal) || k.StartsWith("total", StringComparison.Ordinal) || k.StartsWith("жыйынты", StringComparison.Ordinal) || k.StartsWith("жалпы", StringComparison.Ordinal) || k.StartsWith("jami", StringComparison.Ordinal)))
        {
            if (InvoiceText.DetectCurrency(value + " " + rawKey) is { Length: > 0 } cur0 && CurrencyHint.Length == 0)
                CurrencyHint = cur0;
            return true;
        }

        if (k.StartsWith("итого", StringComparison.Ordinal) || k.StartsWith("всего", StringComparison.Ordinal) || k.StartsWith("total", StringComparison.Ordinal) || k.StartsWith("жыйынтык", StringComparison.Ordinal) || k.StartsWith("jami", StringComparison.Ordinal))
        {
            if (InvoiceText.TryParseNumber(value, out var total) && total > 0)
            {
                Header.TotalFromFile ??= total;
                return true;
            }
            if (InvoiceText.DetectCurrency(value + " " + rawKey) is { Length: > 0 } cur && CurrencyHint.Length == 0)
                CurrencyHint = cur;
            return false;
        }

        return false;
    }

    private static bool IsNumberKey(string k)
    {
        if (k.Contains("договор") || k.Contains("заказ") || k.Contains("order") || k.Contains("contract") || k.Contains("банк") || k.Contains("счетполуч") || k.Contains("доверенност") || k.Contains("путев"))
            return false;
        return k.StartsWith("номер", StringComparison.Ordinal) || k.StartsWith("number", StringComparison.Ordinal) || k.StartsWith("invoiceno", StringComparison.Ordinal)
               || k.StartsWith("nomer", StringComparison.Ordinal) || k.StartsWith("raqam", StringComparison.Ordinal) || k.StartsWith("hujjat", StringComparison.Ordinal) || k.Contains("raqami", StringComparison.Ordinal) || k.StartsWith("жүкхатномер", StringComparison.Ordinal)
               || k.StartsWith("документномер", StringComparison.Ordinal) || k.StartsWith("жүккат", StringComparison.Ordinal) || k.StartsWith("жүкхат", StringComparison.Ordinal) || k.StartsWith("yukxati", StringComparison.Ordinal) || k.StartsWith("nakladnoy", StringComparison.Ordinal) || k.StartsWith("faktura", StringComparison.Ordinal) || (k.StartsWith("накладная", StringComparison.Ordinal) && k.Length <= 12)
               || k is "no" or "invoice" or "счетфактура" or "документ";
    }

    private static bool IsDateKey(string k)
    {
        if (k.Contains("договор") || k.Contains("оплат") || k.Contains("срок") || k.Contains("платеж") || k.Contains("contract") || k.Contains("due") || k.Contains("доверенност") || k.Contains("rojden") || k.Contains("рожд"))
            return false;
        return k.StartsWith("дата", StringComparison.Ordinal) || k.StartsWith("date", StringComparison.Ordinal) || k.StartsWith("sana", StringComparison.Ordinal)
               || k.StartsWith("күнү", StringComparison.Ordinal) || k.StartsWith("күн", StringComparison.Ordinal) || k.StartsWith("tarih", StringComparison.Ordinal) || k == "от";
    }

    /// <summary>«ООО «Х», ИНН 123…, адрес…» → название «ООО «Х»» и ИНН/КПП из той же строки.</summary>
    private static string TakeName(string value, out string inn, out string kpp)
    {
        inn = "";
        kpp = "";
        var v = value.Trim();
        if (InnInline.Match(v) is { Success: true } m)
        {
            inn = m.Groups["inn"].Value;
            kpp = m.Groups["kpp"].Success ? m.Groups["kpp"].Value : "";
            v = v[..m.Index].Trim().TrimEnd(',', ';', '(', '-', '–').Trim();
        }
        // Адрес/реквизиты в той же ячейке — отрезаем по «, адрес»/«, р/с».
        foreach (var marker in new[] { ", адрес", "; адрес", ", address", ", р/с", ", тел", ", tel" })
        {
            var cut = v.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (cut > 0)
                v = v[..cut].Trim();
        }
        return v.Length > 200 ? v[..200] : v;
    }
}
