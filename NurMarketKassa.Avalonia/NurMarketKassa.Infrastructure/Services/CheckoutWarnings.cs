using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Одно предупреждение сервера из ответа pos/checkout: <c>warnings: [{code, index, ...}]</c>.</summary>
public sealed record CheckoutWarning(string Code, int? Index, string? ProductId, decimal? Price, decimal? PurchasePrice, int? Into);

/// <summary>2026-11-05 (сервер ceba67c): ответ pos/checkout может нести <c>warnings</c> — продажа при этом проведена (201/200).
/// lines_merged {index, into} — одинаковые строки сервер сложил в одну; below_cost {index, product_id, price, purchase_price} — позиция из очереди продана ниже закупки;
/// negative_stock — остатка не хватало (досылка из очереди, как и раньше проводится). Разбор терпим к любой форме: нет поля / не массив — пусто.</summary>
public static class CheckoutWarnings
{
    public const string LinesMerged = "lines_merged";
    public const string BelowCost = "below_cost";
    public const string NegativeStock = "negative_stock";

    public static IReadOnlyList<CheckoutWarning> Parse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("warnings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<CheckoutWarning>();
        var list = new List<CheckoutWarning>();
        foreach (var w in arr.EnumerateArray())
        {
            string? code = null;
            if (w.ValueKind == JsonValueKind.String)
                code = w.GetString();
            else if (w.ValueKind == JsonValueKind.Object && w.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                code = c.GetString();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            list.Add(w.ValueKind == JsonValueKind.Object
                ? new CheckoutWarning(code.Trim().ToLowerInvariant(), Int(w, "index"), Str(w, "product_id"), ServerErrorCodes.Number(w, "price"), ServerErrorCodes.Number(w, "purchase_price"), Int(w, "into"))
                : new CheckoutWarning(code.Trim().ToLowerInvariant(), null, null, null, null, null));
        }
        return list;
    }

    private static int? Int(JsonElement o, string name) =>
        ServerErrorCodes.Number(o, name) is { } n ? (int)n : null;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null) : null;

    public static bool Has(IReadOnlyList<CheckoutWarning> warnings, string code) => warnings.Any(w => w.Code == code);

    /// <summary>Короткий текст для журнала: «lines_merged(1→0), below_cost(2)».</summary>
    public static string Describe(IReadOnlyList<CheckoutWarning> warnings) =>
        string.Join(", ", warnings.Select(w => w.Code + (w.Index is { } i ? "(" + i + (w.Into is { } into ? "→" + into : "") + ")" : "")));

    private static string Num(decimal? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";

    /// <summary>Текст записи «Некорректные чеки»: чек из очереди проведён с предупреждением «ниже закупки».</summary>
    public static string BelowCostNote(IReadOnlyList<CheckoutWarning> warnings, string? saleId)
    {
        var rows = warnings.Where(w => w.Code == BelowCost).ToList();
        var details = string.Join("; ", rows.Take(5).Select(w =>
        {
            var n = (w.Index ?? 0) + 1;
            return Tr.T(
                $"позиция {n}: цена {Num(w.Price)}, закупка {Num(w.PurchasePrice)}",
                $"позиция {n}: баасы {Num(w.Price)}, сатып алуу {Num(w.PurchasePrice)}",
                $"line {n}: price {Num(w.Price)}, cost {Num(w.PurchasePrice)}",
                $"satır {n}: fiyat {Num(w.Price)}, maliyet {Num(w.PurchasePrice)}",
                $"qator {n}: narx {Num(w.Price)}, xarid {Num(w.PurchasePrice)}");
        }));
        var sale = string.IsNullOrWhiteSpace(saleId) ? "" : " " + saleId;
        return Tr.T(
            $"Чек из очереди{sale} проведён с предупреждением сервера: продано ниже закупочной цены ({details}). Продажа записана — проверьте цену.",
            $"Кезектеги чек{sale} сервердин эскертүүсү менен өткөрүлдү: сатып алуу баасынан төмөн сатылды ({details}). Сатуу жазылды — баасын текшериңиз.",
            $"The queued receipt{sale} was posted with a server warning: sold below the purchase price ({details}). The sale is recorded — check the price.",
            $"Kuyruktaki fiş{sale} sunucu uyarısıyla işlendi: alış fiyatının altında satıldı ({details}). Satış kaydedildi — fiyatı kontrol edin.",
            $"Navbatdagi chek{sale} server ogohlantirishi bilan o'tkazildi: xarid narxidan past sotildi ({details}). Sotuv yozildi — narxni tekshiring.");
    }

    /// <summary>Досылка из очереди: ответ сервера с предупреждениями — в журнал; «ниже закупки» — ещё и в «Некорректные чеки» (владелец видит). Сбой записи досылку не ломает.</summary>
    public static void HandleReplayResponse(string entryId, string? saleId, JsonElement response)
    {
        var warnings = Parse(response);
        if (warnings.Count == 0)
            return;
        PosLogger.Log($"OFFLINE replay: чек {entryId}, продажа {saleId ?? "—"} — предупреждения сервера: {Describe(warnings)}.", "OFFLINE");
        if (!Has(warnings, BelowCost))
            return;
        try
        {
            IrregularReceiptsStore.Append(new IrregularReceiptEntry
            {
                Tag = IrregularReceiptEntry.SoldBelowCost,
                CashierName = PosApp.CurrentUserDisplayName,
                Note = BelowCostNote(warnings, saleId),
                CartJson = "{}",
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE replay: предупреждение «ниже закупки» не записано в «Некорректные чеки»: {ex.Message}", "WARNING");
        }
    }
}
