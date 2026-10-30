using System.Globalization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

// 2026-10-30 (приёмка 1.17.67, «Важно» 4–5): поиск уже проведённого шага правки накладной — чистая логика без сети (её же прогоняет мини-проверка invoice-check).
// Раньше шаг искали «по составу и времени» (since = первая отправка − 5 мин): часы ПК, спешащие больше чем на 5 мин, теряли проведённый шаг (второй POST → двойной
// доприход), чужой приход с тем же составом через часы принимался за свой, а возврат v2 с тем же количеством — за v3. Теперь шаг узнаётся по МЕТКЕ в комментарии
// («[nm:код-прихода:vN:ключ-шага]») — она уникальна для шага и от часов не зависит; состав и время — только запасной путь для записей без метки, и при сомнении
// шаг не повторяется (Ambiguous), а остаётся «не проверено».

/// <summary>Итог поиска: ничего похожего / шаг найден / есть похожий документ без метки — неясно, свой ли он.</summary>
public enum PostedKind { None, Found, Ambiguous }

public readonly record struct PostedLookup(PostedKind Kind, string? Id = null, DateTime? At = null, string? Note = null)
{
    public static readonly PostedLookup NotFound = new(PostedKind.None);
}

/// <summary>Что ищем: метка шага, старый вид комментария (записи до метки), состав и окно времени (с запасом на часы ПК).</summary>
public sealed record StepMatch(string Token, string LegacyComment, IReadOnlyList<EditStepItem> Items, DateTime Since, DateTime Until, IReadOnlySet<string> Claimed);

/// <summary>Документ с сервера (возврат или приход) в виде, нужном для сопоставления. Parts — «товар:кол-во:цена» по строкам (для прихода), TotalQty — сумма количеств (для возврата).</summary>
public sealed record PostedCandidate(string Id, string Comment, DateTime? At, IReadOnlyList<string> Parts, double TotalQty);

public static class ReceiptEditMatcher
{
    private static readonly Regex Mark = new(@"\[nm:(?<r>[^:\]\s]{1,8}):v(?<v>\d+):(?<k>[^:\]\s]{1,8})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Запас на часы ПК, если поправка к серверному времени известна (<see cref="ReceiptEditPolicy.ClockSkew"/>), иначе — 15 минут.</summary>
    public static TimeSpan SkewMargin(TimeSpan skew) => TimeSpan.FromMinutes(15) + skew.Duration();

    /// <summary>Окно, в которое попадает документ шага: от первой отправки − 5 мин − запас до первой отправки + 15 мин + запас.</summary>
    public static (DateTime Since, DateTime Until) Window(DateTime firstSentAt, TimeSpan skew)
    {
        var margin = SkewMargin(skew);
        return (firstSentAt.AddMinutes(-5) - margin, firstSentAt.AddMinutes(15) + margin);
    }

    private static string Compact(string? id)
    {
        var s = (id ?? "").Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return s.Length > 8 ? s[..8] : s;
    }

    /// <summary>Метка шага: код прихода (первые 8 знаков id), версия правки и первые 8 знаков ключа шага. Короткая — комментарий на сервере может быть ограничен по длине.</summary>
    public static string Token(string receiptId, int version, string stepKey) => $"[nm:{Compact(receiptId)}:v{version}:{Compact(stepKey)}]";

    /// <summary>Наибольшая версия правки этого прихода по меткам в комментарии (0 — меток этого прихода нет).</summary>
    public static int VersionFromMark(string? comment, string receiptId)
    {
        if (string.IsNullOrEmpty(comment))
            return 0;
        var want = Compact(receiptId);
        var max = 0;
        foreach (Match m in Mark.Matches(comment))
            if (string.Equals(m.Groups["r"].Value, want, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(m.Groups["v"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                max = Math.Max(max, v);
        return max;
    }

    /// <summary>В комментарии есть метка правки (любого прихода/шага): документ точно чей-то шаг, и если не наш — к нашему отношения не имеет.</summary>
    public static bool HasAnyMark(string? comment) => !string.IsNullOrEmpty(comment) && Mark.IsMatch(comment);

    /// <summary>Сопоставление. Порядок: (1) метка этого шага; (2) запись без метки — тот же комментарий дословно + тот же состав + окно времени;
    /// (3) тот же состав в окне времени без метки и без чужого комментария правки — неясно (Ambiguous): повтор не отправляем.
    /// Документы, занятые другими шагами (Claimed), и документы с меткой другого шага пропускаются.</summary>
    public static PostedLookup Classify(IEnumerable<PostedCandidate> candidates, StepMatch match, bool isReturn, string docNumber)
    {
        var wantedParts = match.Items
            .Select(i => Part(i.ProductId, Math.Round(i.Qty, 3, MidpointRounding.AwayFromZero), Math.Round(i.Price, 3, MidpointRounding.AwayFromZero)))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var wantedQty = Math.Round(match.Items.Sum(i => i.Qty), 3);
        PostedLookup ambiguous = PostedLookup.NotFound;
        foreach (var c in candidates)
        {
            if (c.Id.Length > 0 && match.Claimed.Contains(c.Id))
                continue;
            var comment = c.Comment ?? "";
            if (match.Token.Length > 0 && comment.Contains(match.Token, StringComparison.OrdinalIgnoreCase))
                return new PostedLookup(PostedKind.Found, c.Id.Length > 0 ? c.Id : null, c.At);
            if (HasAnyMark(comment))
                continue; // метка другого шага (другая версия или другой приход) — это не наш документ
            var inWindow = c.At is { } at && at >= match.Since && at <= match.Until;
            if (!inWindow)
                continue;
            var sameComposition = isReturn
                ? Math.Abs(Math.Round(c.TotalQty, 3) - wantedQty) < 0.0005
                : c.Parts.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(wantedParts, StringComparer.Ordinal);
            if (!sameComposition)
                continue;
            if (match.LegacyComment.Length > 0 && string.Equals(comment.Trim(), match.LegacyComment.Trim(), StringComparison.CurrentCultureIgnoreCase))
                return new PostedLookup(PostedKind.Found, c.Id.Length > 0 ? c.Id : null, c.At);
            // Правка того же документа другой версии (старый вид комментария «…ПР-…, версия N») — не наш шаг.
            if (docNumber.Length > 0 && ReceiptEditService.EditVersionFromComment(comment, docNumber) > 0)
                continue;
            ambiguous = new PostedLookup(PostedKind.Ambiguous, c.Id.Length > 0 ? c.Id : null, c.At,
                $"{c.Id} ({c.At:dd.MM.yyyy HH:mm})");
        }
        return ambiguous;
    }

    public static string Part(string? product, double qty, double price) =>
        product + ":" + qty.ToString("0.###", CultureInfo.InvariantCulture) + ":" + price.ToString("0.##", CultureInfo.InvariantCulture);
}
