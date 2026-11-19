using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (ТЗ Умара 18.11, eKassa): режим «сервер фискализирует» — готовый чек ГНС берётся с сервера
/// (GET main/pos/sales/{id}/receipt/?wait_ekassa=1&amp;receipt_text=1) и печатается как есть (32 символа, без изменений),
/// строка «QR&lt;TAB&gt;ссылка» заменяется QR-картинкой.</summary>
public static class FiscalServerReceipt
{
    /// <summary>Метка в начале текста: печать не меняет регистр и переносы (ReceiptPrintService.PrintReceipt).</summary>
    public const string Marker = "\u0001FISCAL\n";

    /// <summary>Фискальные возвраты продажи (ekassa_fiscal.returns): id → (статус, текст чека). Нет поля — пусто.</summary>
    public static List<(string Id, string Status, string? Text, long Fd)> ParseReturns(JsonElement sale)
    {
        var list = new List<(string, string, string?, long)>();
        if (sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            sale = d;
        if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("ekassa_fiscal", out var ef) || ef.ValueKind != JsonValueKind.Object
            || !ef.TryGetProperty("returns", out var rets) || rets.ValueKind != JsonValueKind.Object)
            return list;
        foreach (var r in rets.EnumerateObject())
        {
            if (r.Value.ValueKind != JsonValueKind.Object) continue;
            var st = r.Value.TryGetProperty("status", out var s1) && s1.ValueKind == JsonValueKind.String ? s1.GetString() ?? "" : "";
            var tx = r.Value.TryGetProperty("receipt_txt", out var t1) && t1.ValueKind == JsonValueKind.String ? t1.GetString() : null;
            long fd = 0;
            if (r.Value.TryGetProperty("fd_number", out var f1))
                long.TryParse(f1.ValueKind == JsonValueKind.Number ? f1.GetRawText() : f1.GetString(), out fd);
            list.Add((r.Name, st, tx, fd));
        }
        return list;
    }

    /// <summary>Уже напечатанные в этом запуске фискальные возвраты (id) — не печатать один и тот же дважды.</summary>
    public static readonly HashSet<string> PrintedReturns = new();

    /// <summary>Фискальный чек не получен (продажа в NurCRM записана): окно показывает предупреждение; true в аргументе — это возврат.</summary>
    public static event Action<bool>? NotSent;

    public static void RaiseNotSent(bool isReturn)
    {
        try { NotSent?.Invoke(isReturn); } catch { /* предупреждение не должно ломать печать */ }
    }

    public const string NotReceived = "Фискальный чек не получен";

    /// <summary>Фискальный текст чека из ответа сервера; null — чек не пробит (нет текста, «не получен», статус error/pending).</summary>
    public static string? ParseReady(JsonElement root, out string? status)
    {
        status = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            root = d;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("ekassa", out var ek) && ek.ValueKind == JsonValueKind.Object
            && ek.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
            status = st.GetString();
        if (!root.TryGetProperty("receipt_text", out var t) || t.ValueKind != JsonValueKind.String)
            return null;
        var text = t.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Contains(NotReceived, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            return null;
        return text;
    }
}
