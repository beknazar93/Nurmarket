using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-11-03: помощь чтению накладной по фото (в том числе рукописной) в Нур Советнике.
//  (а) Словарь: до ~200 названий товаров склада + недавние соответствия «как у поставщика → как у нас» (InvoiceMatchMemory) уходят в подсказку модели —
//      читать неразборчивое с опорой на них, но НЕ выдумывать (чётко видимое слово не «исправляется» под словарь).
//  (б) Каждая служебная строка ТОВАР несёт n (№ строки), line_total (сумма строки, как напечатана), confidence 0..1 и unclear — слабые строки подсвечиваются в окне закупки.
//  (в) Второй проход: строки, где количество × цена ≠ сумма строки в документе, перечитываются отдельным запросом с указанием расхождения.
// Сверку итога накладной (НДС, скидка, число позиций — 1.17.70, ProductActionPlan.InvoiceTotalsHint / InvoiceReconcile) это не меняет.

public static class InvoiceReadingAid
{
    /// <summary>Уверенность ниже — строка считается «нечётко прочитанной».</summary>
    public const double LowConfidence = 0.7;

    public const int DictionaryMax = 200;
    public const int MemoryHintMax = 40;
    public const int SecondPassMaxRows = 12;

    /// <summary>Допуск расхождения «количество × цена» и «сумма строки», сом: округление цены до копеек даёт до ~0,5 на строке.</summary>
    public static bool LineTotalMismatch(double qty, double purchase, double lineTotal) =>
        qty > 0 && purchase > 0 && lineTotal > 0 && Math.Abs(qty * purchase - lineTotal) > Math.Max(1.0, lineTotal * 0.005);

    // ------------------------------------------------------------------ (а) словарь для подсказки

    private static string OneLine(string? text, int max)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim().Replace("\"", "'");
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Подсказка для чтения фото: словарь названий склада и память поставщиков. Пустая строка — каталог пуст. Не содержит цен, остатков и персональных данных.</summary>
    public static string DictionaryHint(IReadOnlyList<CatalogProductTileVm>? catalog = null)
    {
        try
        {
            catalog ??= InvoicePlan.SnapshotCatalog();
            if (catalog.Count == 0)
                return "";
            // Свежие и с остатком — вероятнее встретятся в новой накладной.
            var names = catalog.Where(t => !string.IsNullOrWhiteSpace(t.Title))
                .OrderByDescending(t => t.Quantity > 0).ThenByDescending(t => t.CreatedAt)
                .Select(t => OneLine(t.Title, 60)).Distinct(StringComparer.OrdinalIgnoreCase).Take(DictionaryMax).ToList();
            var sb = new StringBuilder();
            sb.Append(" СЛОВАРЬ ТОВАРОВ МАГАЗИНА (подсказка для разбора неразборчивого почерка, не обязательный список): ").Append(string.Join("; ", names)).Append('.');
            var pairs = MemoryPairs(catalog);
            if (pairs.Count > 0)
                sb.Append(" ТАК НАЗЫВАЛИ У ПОСТАВЩИКОВ РАНЬШЕ (в накладной → у нас): ").Append(string.Join("; ", pairs)).Append('.');
            sb.Append(" Как пользоваться словарём: слово или число, которое на фото читается ЧЁТКО, пиши как на фото, даже если его нет в словаре; сверяйся со словарём только когда почерк неразборчив и вариант из словаря явно подходит по буквам. ")
              .Append("Не выдумывай и не «улучшай» названия: сомневаешься — пиши, что видишь, и ставь \"unclear\": true.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: словарь для чтения фото не собран: {ex.GetType().Name}.", "WARNING");
            return "";
        }
    }

    /// <summary>Недавние соответствия из памяти поставщиков: «название в накладной → название товара у нас» (до <see cref="MemoryHintMax"/>).</summary>
    private static List<string> MemoryPairs(IReadOnlyList<CatalogProductTileVm> catalog)
    {
        var result = new List<string>();
        try
        {
            var byId = catalog.Where(t => !string.IsNullOrWhiteSpace(t.Id)).GroupBy(t => t.Id!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var e in InvoiceMatchMemory.Recent(MemoryHintMax * 2))
            {
                if (byId.TryGetValue(e.ProductId, out var tile) && result.Count < MemoryHintMax)
                    result.Add($"«{OneLine(e.InvoiceName, 50)}» → «{OneLine(tile.Title, 50)}»");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: память поставщиков для подсказки не прочитана: {ex.GetType().Name}.", "WARNING");
        }
        return result;
    }

    // ------------------------------------------------------------------ (в) второй проход

    private static readonly Regex ProductLine = new(@"(?im)^[ \t*`•\-]*ТОВАР\s*:\s*(\{[^\n]*\})[ \t*`]*$");

    /// <summary>Строка для перечитывания. LineTotal = 0 — сумма строки в документе не прочитана (строка выбрана за нечёткость при несошедшемся итоге).</summary>
    public sealed record Mismatch(int Number, string Name, double Qty, double Purchase, double LineTotal);

    private static double? Num(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return null;
        return ProductActionPlan.ParseMoney(v.ToString());
    }

    /// <summary>Строки ответа, где количество × закупка не сходится с суммой строки, прочитанной в документе (нужны n, qty, purchase и line_total). Не больше <see cref="SecondPassMaxRows"/>.</summary>
    public static List<Mismatch> FindMismatches(string? answer)
    {
        var list = new List<Mismatch>();
        foreach (Match m in ProductLine.Matches(answer ?? ""))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var root = doc.RootElement;
                var op = root.TryGetProperty("op", out var o) ? o.ToString().ToLowerInvariant() : "";
                if (op is not ("receive" or "create"))
                    continue;
                if (Num(root, "n") is not { } n || Num(root, "qty") is not { } qty || Num(root, "purchase") is not { } purchase || Num(root, "line_total") is not { } total)
                    continue;
                if (!LineTotalMismatch(qty, purchase, total))
                    continue;
                var name = root.TryGetProperty("name", out var nm) ? nm.ToString() : root.TryGetProperty("product", out var pr) ? pr.ToString() : "";
                list.Add(new Mismatch((int)n, OneLine(name, 80), qty, purchase, total));
            }
            catch (JsonException)
            {
            }
        }
        return list.GroupBy(x => x.Number).Select(g => g.First()).Take(SecondPassMaxRows).ToList();
    }

    /// <summary>Что перечитывать: (1) строки, где количество × цена ≠ сумма строки; (2) если итог накладной по документу не сошёлся с суммой строк (с учётом НДС и скидки,
    /// InvoiceReconcile) — ещё и строки, помеченные ИИ как нечёткие (unclear / confidence ниже порога). <paramref name="reference"/> — пояснение про итог для запроса (или null).</summary>
    public static List<Mismatch> FindTargets(string? answer, out string? reference)
    {
        reference = null;
        var targets = FindMismatches(answer);
        try
        {
            var hint = ProductActionPlan.InvoiceTotalsHint(answer);
            if (hint is not { HasDocumentTotal: true })
                return targets;
            var all = new List<(int N, string Name, double Qty, double Purchase, bool Weak)>();
            foreach (Match m in ProductLine.Matches(answer ?? ""))
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var root = doc.RootElement;
                var op = root.TryGetProperty("op", out var o) ? o.ToString().ToLowerInvariant() : "";
                if (op is not ("receive" or "create") || Num(root, "qty") is not { } qty || Num(root, "purchase") is not { } purchase)
                    continue;
                var weak = (root.TryGetProperty("unclear", out var u) && (u.ValueKind == JsonValueKind.True || u.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)))
                           || (Num(root, "confidence") is { } c && c < LowConfidence);
                var name = root.TryGetProperty("name", out var nm) ? nm.ToString() : root.TryGetProperty("product", out var pr) ? pr.ToString() : "";
                all.Add(((int?)Num(root, "n") ?? all.Count + 1, OneLine(name, 80), qty, purchase, weak));
            }
            if (all.Count == 0)
                return targets;
            var check = InvoiceReconcile.Check(all.Sum(x => x.Qty * x.Purchase), all.Count, hint);
            if (check.State != ReconcileState.Mismatch)
                return targets;
            reference = string.Create(CultureInfo.InvariantCulture, $"Итог накладной по документу {check.Reference:0.##}, а сумма прочитанных строк {check.LinesSum:0.##} (разница {check.Difference:0.##}) — часть чисел прочитана неверно.");
            foreach (var w in all.Where(x => x.Weak && targets.All(t => t.Number != x.N)))
            {
                if (targets.Count >= SecondPassMaxRows)
                    break;
                targets.Add(new Mismatch(w.N, w.Name, w.Qty, w.Purchase, 0));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // разбор служебных строк не удался — остаются построчные расхождения
        }
        return targets;
    }

    public const string SecondPassSystem =
        "Ты читаешь накладную по фото (возможно рукописную). Тебе указаны строки, где количество × цена не равно сумме строки. Перечитай на фото ТОЛЬКО эти строки. "
        + "Одно из трёх чисел (количество, цена, сумма) прочитано неверно: найди в документе, какое. Читай, что написано, не подгоняй числа под сумму; если на фото иначе, чем кажется, верь фото. "
        + "Ответ — только служебные строки, по одной на каждую указанную строку, без таблицы и без другого текста, ровно в том же виде: "
        + "ТОВАР: {\"op\": \"receive\" или \"create\", \"n\": №, \"name\": \"название\", \"qty\": N, \"purchase\": цена за единицу, \"line_total\": сумма строки как в документе, \"price\": розничная цена (как раньше), "
        + "\"confidence\": 0..1, \"unclear\": true/false}. Не уверен после перечитывания — \"unclear\": true и confidence ниже 0.7.";

    public static string SecondPassQuestion(IReadOnlyList<Mismatch> rows, string? reference)
    {
        var sb = new StringBuilder("Перечитай по фото только эти строки накладной:\n");
        foreach (var r in rows)
        {
            if (r.LineTotal > 0)
                sb.Append(CultureInfo.InvariantCulture, $"№{r.Number} «{r.Name}»: прочитано количество {r.Qty:0.###}, цена {r.Purchase:0.####}, сумма строки {r.LineTotal:0.##}; количество × цена = {r.Qty * r.Purchase:0.##}, расхождение {r.Qty * r.Purchase - r.LineTotal:0.##}.\n");
            else
                sb.Append(CultureInfo.InvariantCulture, $"№{r.Number} «{r.Name}»: прочитано количество {r.Qty:0.###}, цена {r.Purchase:0.####} (читалось нечётко); сумма строки в документе не указана.\n");
        }
        if (!string.IsNullOrWhiteSpace(reference))
            sb.Append(reference).Append('\n');
        sb.Append("Верни только строки ТОВАР для этих номеров.");
        return sb.ToString();
    }

    /// <summary>Заменить строки ТОВАР первого ответа (по полю n) строками второго прохода; в видимой таблице обновляются те же строки по № (количество, цена, сумма).</summary>
    public static string ApplyCorrections(string answer, string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return answer;
        var fixes = new Dictionary<int, (string Line, JsonElement Root)>();
        foreach (Match m in ProductLine.Matches(reply))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                if (Num(doc.RootElement, "n") is { } n)
                    fixes[(int)n] = (m.Groups[1].Value, doc.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }
        if (fixes.Count == 0)
            return answer;
        var text = ProductLine.Replace(answer, m =>
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                if (Num(doc.RootElement, "n") is { } n && fixes.TryGetValue((int)n, out var fix))
                    return "ТОВАР: " + fix.Line;
            }
            catch (JsonException)
            {
            }
            return m.Value;
        });
        // Видимая таблица: «| № | название | код | ед | кол-во | закупка | розница | сумма |» — обновляем кол-во, закупку и сумму у исправленных строк.
        var rows = text.Split('\n');
        for (var i = 0; i < rows.Length; i++)
        {
            var cells = rows[i].Split('|');
            if (cells.Length < 10 || !int.TryParse(cells[1].Trim(), out var no) || !fixes.TryGetValue(no, out var fix))
                continue;
            var root = fix.Root;
            string F(double? v, string fmt) => v is { } x ? x.ToString(fmt, CultureInfo.GetCultureInfo("ru-RU")) : "-";
            var qty = Num(root, "qty");
            var purchase = Num(root, "purchase");
            cells[5] = " " + F(qty, "0.###") + " ";
            cells[6] = " " + F(purchase, "N2") + " ";
            cells[8] = " " + F(Num(root, "line_total") ?? (qty.HasValue && purchase.HasValue ? qty * purchase : null), "N2") + " ";
            rows[i] = string.Join("|", cells);
        }
        return string.Join("\n", rows);
    }

    /// <summary>Второй проход целиком: найти расхождения → один запрос <paramref name="ask"/> (система, вопрос) → подставить исправления. Нет расхождений или запрос не удался — ответ без изменений.</summary>
    public static async Task<(string Answer, int Rows, bool Applied)> SecondPassAsync(string answer, string? reference, Func<string, string, CancellationToken, Task<string?>> ask, CancellationToken ct)
    {
        var rows = FindTargets(answer, out var totalNote);
        if (rows.Count == 0)
            return (answer, 0, false);
        try
        {
            var reply = await ask(SecondPassSystem, SecondPassQuestion(rows, reference ?? totalNote), ct).ConfigureAwait(false);
            var fixedAnswer = ApplyCorrections(answer, reply);
            var applied = !ReferenceEquals(fixedAnswer, answer) && fixedAnswer != answer;
            PosLogger.Log($"ИИ: второй проход по накладной — строк с расхождением {rows.Count}, исправления {(applied ? "применены" : "не получены")}.", "INFO");
            return (fixedAnswer, rows.Count, applied);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log("ИИ: второй проход по накладной не успел — оставлено первое чтение.", "WARNING");
            return (answer, rows.Count, false);
        }
    }
}
