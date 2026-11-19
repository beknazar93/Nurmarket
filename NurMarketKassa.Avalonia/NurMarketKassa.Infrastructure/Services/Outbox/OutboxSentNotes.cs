using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 (ответ Умара 19.11, п. 4): чек из офлайн-очереди проводится, даже если продан ниже закупки или остатка не хватало (warnings below_cost / negative_stock + stock_negative).
/// Ничего не блокируем: после отправки в «Неотправленном» остаётся пометка «Отправлено, но: продано ниже закупки / ушло в минус по остатку» с названиями товаров, в журнале — строка.
/// Хранится только в памяти (последние 20; после перезапуска остаётся запись в журнале и в «Некорректных чеках» для below_cost).</summary>
public sealed record SentWarningNote(string EntryId, string? SaleId, DateTimeOffset At, string Text);

public static class OutboxSentNotes
{
    private const int Max = 20;
    private static readonly object Gate = new();
    private static readonly List<SentWarningNote> Items = new();

    /// <summary>Список изменился (запись добавлена/убрана) — окно «Неотправленное» перерисовывает пометки.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<SentWarningNote> Recent
    {
        get
        {
            lock (Gate)
                return Items.ToList();
        }
    }

    public static bool HasNote(string entryId)
    {
        lock (Gate)
            return Items.Any(i => string.Equals(i.EntryId, entryId, StringComparison.Ordinal));
    }

    public static void Dismiss(string entryId)
    {
        bool changed;
        lock (Gate)
            changed = Items.RemoveAll(i => string.Equals(i.EntryId, entryId, StringComparison.Ordinal)) > 0;
        if (changed)
            Changed?.Invoke();
    }

    public static void Clear()
    {
        bool changed;
        lock (Gate)
        {
            changed = Items.Count > 0;
            Items.Clear();
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Разобрать ответ принятого чека; если сервер предупредил — запомнить пометку и записать в журнал. Не бросает.</summary>
    public static void Record(string entryId, string? saleId, JsonElement response)
    {
        try
        {
            string? cartJson = null;
            try { cartJson = OfflinePendingSalesStore.TryGetById(entryId)?.CartJson; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* названия — только для показа */ }
            var text = BuildText(entryId, response, cartJson);
            if (text is null)
                return;
            lock (Gate)
            {
                Items.RemoveAll(i => string.Equals(i.EntryId, entryId, StringComparison.Ordinal));
                Items.Add(new SentWarningNote(entryId, saleId, DateTimeOffset.Now, text));
                while (Items.Count > Max)
                    Items.RemoveAt(0);
            }
            PosLogger.Log($"OFFLINE replay: чек {entryId}, продажа {saleId ?? "—"} — отправлен, но: {text}", "OFFLINE");
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"OFFLINE replay: пометка «отправлено, но…» не записана ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Текст пометки по ответу сервера; null — предупреждений про цену/остаток нет. Чистая функция (для тестов).</summary>
    public static string? BuildText(string entryId, JsonElement response, string? cartJson)
    {
        var warnings = CheckoutWarnings.Parse(response);
        var below = new List<string>();
        var minus = new List<string>();
        foreach (var w in warnings)
        {
            if (w.Code == CheckoutWarnings.BelowCost)
                AddUnique(below, NameOf(cartJson, w.Index, w.ProductId));
            else if (w.Code == CheckoutWarnings.NegativeStock)
                AddUnique(minus, NameOf(cartJson, w.Index, w.ProductId));
        }
        // stock_negative — отдельное поле ответа: true, массив названий/id или массив объектов {product_id, name}.
        var hasStockNegative = false;
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("stock_negative", out var sn))
        {
            if (sn.ValueKind == JsonValueKind.True)
                hasStockNegative = true;
            else if (sn.ValueKind == JsonValueKind.Array)
                foreach (var x in sn.EnumerateArray())
                {
                    hasStockNegative = true;
                    var label = x.ValueKind switch
                    {
                        JsonValueKind.String => x.GetString(),
                        JsonValueKind.Number => x.GetRawText(),
                        JsonValueKind.Object => FirstString(x, "name", "product_name", "title") ?? NameOf(cartJson, Idx(x), FirstString(x, "product_id", "product", "id")),
                        _ => null,
                    };
                    AddUnique(minus, label);
                }
        }
        if (below.Count == 0 && minus.Count == 0 && !CheckoutWarnings.Has(warnings, CheckoutWarnings.BelowCost) && !CheckoutWarnings.Has(warnings, CheckoutWarnings.NegativeStock) && !hasStockNegative)
            return null;

        var parts = new List<string>();
        if (below.Count > 0 || CheckoutWarnings.Has(warnings, CheckoutWarnings.BelowCost))
            parts.Add(Tr.T("продано ниже закупки", "сатып алуу баасынан төмөн сатылды", "sold below the purchase price", "alış fiyatının altında satıldı", "xarid narxidan past sotildi") + Names(below));
        if (minus.Count > 0 || CheckoutWarnings.Has(warnings, CheckoutWarnings.NegativeStock) || hasStockNegative)
            parts.Add(Tr.T("ушло в минус по остатку", "калдык боюнча минуска кетти", "went negative on stock", "stokta eksiye düştü", "qoldiq bo'yicha minusga ketdi") + Names(minus));
        var reference = OfflineOperationsService.ShortRef(entryId);
        var list = string.Join("; ", parts);
        return Tr.T($"Чек {reference} отправлен, но: {list}.", $"Чек {reference} жөнөтүлдү, бирок: {list}.", $"Receipt {reference} was sent, but: {list}.", $"Fiş {reference} gönderildi, ancak: {list}.", $"Chek {reference} yuborildi, lekin: {list}.");
    }

    private static string Names(List<string> names) =>
        names.Count == 0 ? "" : " — " + string.Join(", ", names.Take(5)) + (names.Count > 5 ? "…" : "");

    private static void AddUnique(List<string> list, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name!, StringComparer.OrdinalIgnoreCase))
            list.Add(name!);
    }

    private static int? Idx(JsonElement o) => ServerErrorCodes.Number(o, "index") is { } n ? (int)n : null;

    private static string? FirstString(JsonElement o, params string[] names)
    {
        foreach (var n in names)
            if (o.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
                if (v.ValueKind == JsonValueKind.Number)
                    return v.GetRawText();
            }
        return null;
    }

    /// <summary>Название товара по номеру строки чека; нет корзины/номера — id товара (лучше, чем ничего).</summary>
    private static string? NameOf(string? cartJson, int? index, string? productId)
    {
        if (index is { } i && i >= 0 && !string.IsNullOrWhiteSpace(cartJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(cartJson);
                var found = CartDisplayHelper.EnumerateItems(doc.RootElement).Skip(i).Take(1).ToList();
                if (found.Count == 1 && CartDisplayHelper.ItemName(found[0]) is { Length: > 0 } name)
                    return name;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* корзина не разобрана — покажем id */ }
        }
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        // 2026-11-19 (ответ Умара 19.11, п. 8): у negative_stock номера строки нет (index: null), есть только product_id — название берём из своего каталога.
        try
        {
            if (CatalogCacheService.FindProductById(productId)?.Title is { Length: > 0 } title)
                return title;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* каталог не готов — покажем id */ }
        return productId;
    }
}
