using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-10 (жалоба владельца, фото клиента: возврат долгового чека → «Есть позиции не из этого чека или несуществующие sale_item_id.»):
/// строки возврата сверяются со СВЕЖЕЙ карточкой чека с сервера (GET pos/sales/{id}) прямо перед отправкой. Сервер принимает в
/// <c>items[].sale_item_id</c> только id строк ЭТОГО чека; окно возврата могло держать устаревшие строки (чек открыт давно, после частичного
/// возврата/обмена/правки чека на сайте строки пересоздаются, или id строки взят из другого поля). Здесь:
/// <list type="bullet">
/// <item>id строки для возврата — <see cref="SaleItemId"/>: явное <c>sale_item_id</c>, затем <c>id</c> строки чека (контракт Умара 11.11, как мобилка), затем прежние поля;</item>
/// <item><see cref="Verify"/> — каких id нет в свежем чеке и можно ли однозначно сопоставить их по товару (тот же товар одной строкой);</item>
/// <item><see cref="IsUnknownLineRefusal"/> — отказ сервера «не из этого чека / несуществующие sale_item_id».</item>
/// </list></summary>
public static class ReturnLineCheck
{
    /// <summary>id строки чека, который сервер ждёт в <c>sale_item_id</c>. null — у строки нет id (чек из кэша/старого ответа).
    /// 2026-11-11 (ответ Умара по контракту возврата): <c>sale_item_id</c> — это id СТРОКИ ЧЕКА из детальной карточки (GET pos/sales/{id}/ → items[].id), как в мобилке.
    /// Порядок: явное поле <c>sale_item_id</c> строки → сам <c>id</c> строки (если это не id товара) → прежний разбор кассы (<see cref="CartDisplayHelper.TryRefundLineId"/>:
    /// cart_item_id / sale_line_id / line_id / item_id… — раньше он стоял ПЕРВЫМ, и при поле item_id/line_id в строке уходил чужой id → «Есть позиции не из этого чека»).
    /// <paramref name="preferRowId"/> оставлен для совместимости вызовов (id строки и так первый после явного поля).</summary>
    public static string? SaleItemId(JsonElement line, bool preferRowId = false)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return null;
        if (line.TryGetProperty("sale_item_id", out var explicitId) && Scalar(explicitId) is { Length: > 0 } sid)
            return sid;
        if (RowId(line) is { } rowId)
            return rowId;
        return CartDisplayHelper.TryRefundLineId(line);
    }

    /// <summary>Сам <c>id</c> строки (если это не id товара).</summary>
    public static string? RowId(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return null;
        var productId = CartDisplayHelper.TryProductId(line);
        return line.TryGetProperty("id", out var idEl) && Scalar(idEl) is { Length: > 0 } id
               && (string.IsNullOrEmpty(productId) || !string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
            ? id
            : null;
    }

    /// <summary>У строк чека прежний разбор кассы (TryRefundLineId) дал бы другой id, чем id строки — для журнала: «окно до 11.11 слало бы другое поле».</summary>
    public static bool HasDifferentRowIds(JsonElement sale) =>
        CartDisplayHelper.EnumerateSaleLineItems(sale).Any(l => RowId(l) is { } r && CartDisplayHelper.TryRefundLineId(l) is { } legacy
                                                                && !string.Equals(r, legacy, StringComparison.OrdinalIgnoreCase));

    /// <summary>Строка возврата, как её выбрал кассир.</summary>
    public readonly record struct Wanted(string LineId, string? ProductId, string Title, double Quantity);

    /// <summary>Итог сверки: <see cref="Remap"/> — старый id → id строки свежего чека (тот же товар, одна строка); <see cref="Missing"/> — строки,
    /// которых в свежем чеке нет и сопоставить нельзя; <see cref="FreshHasNoIds"/> — в свежем чеке у строк вообще нет id.</summary>
    public sealed record Result(IReadOnlyDictionary<string, string> Remap, IReadOnlyList<Wanted> Missing, bool FreshHasNoIds)
    {
        public bool Ok => Missing.Count == 0 && !FreshHasNoIds;
        public bool Changed => Remap.Count > 0;
    }

    public static Result Verify(JsonElement freshSale, IReadOnlyList<Wanted> wanted, bool preferRowId = false)
    {
        var lines = CartDisplayHelper.EnumerateSaleLineItems(freshSale).ToList();
        var ids = lines.Select(l => SaleItemId(l, preferRowId)).ToList();
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<Wanted>();
        if (lines.Count == 0 || ids.All(string.IsNullOrEmpty))
            return new Result(remap, wanted.ToList(), FreshHasNoIds: true);
        var present = new HashSet<string>(ids.Where(i => !string.IsNullOrEmpty(i))!, StringComparer.OrdinalIgnoreCase);
        foreach (var w in wanted)
        {
            if (present.Contains(w.LineId))
                continue;
            // Сопоставление по товару — только однозначное: ровно одна строка свежего чека с этим товаром (или с этим названием, если товара нет).
            var candidates = lines.Select((l, i) => (Line: l, Id: ids[i]))
                .Where(x => !string.IsNullOrEmpty(x.Id)
                            && (!string.IsNullOrWhiteSpace(w.ProductId)
                                ? string.Equals(CartDisplayHelper.TryProductId(x.Line), w.ProductId, StringComparison.OrdinalIgnoreCase)
                                : string.Equals(CartDisplayHelper.ItemName(x.Line)?.Trim(), w.Title.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (candidates.Count == 1 && !remap.ContainsValue(candidates[0].Id!))
                remap[w.LineId] = candidates[0].Id!;
            else
                missing.Add(w);
        }
        return new Result(remap, missing, FreshHasNoIds: false);
    }

    /// <summary>Отказ сервера «Есть позиции не из этого чека или несуществующие sale_item_id» (400/404/422). Текст сервера сохраняется в журнале.</summary>
    public static bool IsUnknownLineRefusal(ApiException ex)
    {
        if (ex.StatusCode is not (400 or 404 or 422))
            return false;
        string text;
        try
        {
            text = ((ex.Payload is { } p ? p.GetRawText() : "") + " " + ex.Message).ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            text = ex.Message.ToLowerInvariant();
        }
        return text.Contains("не из этого чека") || text.Contains("несуществующие sale_item")
               || (text.Contains("sale_item_id") && (text.Contains("не найден") || text.Contains("not found") || text.Contains("invalid") || text.Contains("не существ")));
    }

    /// <summary>Строки запроса для журнала (без секретов): «id × кол-во (название)».</summary>
    public static string Describe(IEnumerable<Wanted> lines) =>
        string.Join("; ", lines.Select(l => $"{l.LineId} × {l.Quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ({l.Title})"));

    /// <summary>id строк чека с сервера для журнала.</summary>
    public static string DescribeSale(JsonElement sale) =>
        string.Join("; ", CartDisplayHelper.EnumerateSaleLineItems(sale).Select(l => $"{SaleItemId(l) ?? "—"}/id {RowId(l) ?? "—"} ({CartDisplayHelper.ItemName(l)})"));

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };
}
