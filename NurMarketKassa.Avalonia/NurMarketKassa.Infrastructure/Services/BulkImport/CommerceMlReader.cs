using System.Xml.Linq;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (Л-10, перенос товаров из 1С): выгрузка CommerceML — import.xml (каталог: Ид, Артикул, Наименование, Штрихкод, единица)
/// и offers.xml (предложения: цена, остаток). Строки собираются в обычную таблицу с заголовками, дальше всё как у Excel/CSV.
/// Если в файле только каталог — цена и остаток пустые (их дозаполнит владелец или второй файл offers.xml).</summary>
internal static class CommerceMlReader
{
    private static readonly string[] Headers = ["Наименование", "Артикул", "Штрихкод", "Единица измерения", "Категория", "Цена продажи", "Остаток"];

    internal static bool LooksLike(byte[] bytes)
    {
        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n')) i++;
        if (i >= bytes.Length || bytes[i] != (byte)'<')
            return false;
        var head = System.Text.Encoding.UTF8.GetString(bytes, i, Math.Min(bytes.Length - i, 4000));
        return head.Contains("КоммерческаяИнформация", StringComparison.Ordinal) || head.Contains("CommerceInformation", StringComparison.OrdinalIgnoreCase);
    }

    internal static ProductFileReadResult Read(byte[] bytes, string fileName)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var doc = XDocument.Load(ms);
            static string V(XElement? e) => (e?.Value ?? "").Trim();
            static XElement? C(XElement e, string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n);

            var rows = new List<ProductFileRow> { new(1, Headers, 0UL) };
            var groups = doc.Descendants().Where(x => x.Name.LocalName == "Группа").ToDictionary(g => V(C(g, "Ид")), g => V(C(g, "Наименование")), StringComparer.OrdinalIgnoreCase);
            var items = doc.Descendants().Where(x => x.Name.LocalName == "Товар").ToList();
            foreach (var t in items)
            {
                var name = V(C(t, "Наименование"));
                if (name.Length == 0) continue;
                var unit = V(C(t, "БазоваяЕдиница"));
                var cat = "";
                if (C(t, "Группы")?.Elements().FirstOrDefault() is { } gid && groups.TryGetValue(V(gid), out var gname)) cat = gname;
                rows.Add(new ProductFileRow(rows.Count + 1, [name, V(C(t, "Артикул")), V(C(t, "Штрихкод")), unit, cat, "", ""], 0UL));
            }
            // offers.xml: Предложение → Наименование, Артикул, Штрихкод, Цены/Цена/ЦенаЗаЕдиницу, Количество
            foreach (var o in doc.Descendants().Where(x => x.Name.LocalName == "Предложение"))
            {
                var name = V(C(o, "Наименование"));
                if (name.Length == 0) continue;
                var price = V(C(o, "Цены")?.Descendants().FirstOrDefault(x => x.Name.LocalName == "ЦенаЗаЕдиницу"));
                rows.Add(new ProductFileRow(rows.Count + 1, [name, V(C(o, "Артикул")), V(C(o, "Штрихкод")), V(C(o, "БазоваяЕдиница")), "", price, V(C(o, "Количество"))], 0UL));
            }
            if (rows.Count <= 1)
                return ProductFileReadResult.Fail(Tr.T("В файле 1С не найдено товаров (ни «Товар», ни «Предложение»).", "1С файлынан товарлар табылган жок.", "No products found in the 1C file.", "1C dosyasında ürün bulunamadı.", "1C faylida tovarlar topilmadi."));
            if (rows.Count > ProductFileReader.MaxRows + 1)
                return ProductFileReadResult.Fail(Tr.T("В файле больше 50 000 строк. Разделите его на части.", "Файлда 50 000 саптан ашык. Бөлүп жөнөтүңүз.", "The file has more than 50,000 rows. Split it.", "Dosyada 50.000'den fazla satır var. Bölün.", "Faylda 50 000 dan ortiq qator. Uni bo'ling."));
            var table = new ProductFileTable { Kind = ProductFileKind.Xml, FileName = fileName, Rows = rows, ColumnCount = Headers.Length };
            table.DetectHeaderRow();
            return new ProductFileReadResult { Table = table };
        }
        catch (Exception ex)
        {
            return ProductFileReadResult.Fail(Tr.T("Файл 1С (XML) не прочитан: " + ex.Message, "1С (XML) файлы окулган жок: " + ex.Message, "The 1C (XML) file could not be read: " + ex.Message, "1C (XML) dosyası okunamadı: " + ex.Message, "1C (XML) fayli o'qilmadi: " + ex.Message));
        }
    }
}
