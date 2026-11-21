using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-21 (сфера «Базар», модули <c>volume_price</c> и <c>haggle</c>): правки снимка чека, которые нужны только «Базару». Отдельный файл — чтобы основной редактор чека не пух.
/// </summary>
public static partial class ReceiptSnapshotCartEditor
{
    /// <summary>Метка строки: цена поставлена ступенью «от N шт» (значение — N). Только для кассы: на сервер не уходит.</summary>
    public const string VolumeMinField = "volume_min_qty";

    /// <summary>Метка строки: кассир сам снял оптовую цену — ступень к этой строке больше не применяется.</summary>
    public const string VolumeSkipField = "volume_skip";

    /// <summary>Строка со ступенью цены сравнивается с розничной ценой товара (прежняя цена хранится в <c>retail_price</c>), иначе повторный скан того же товара создавал бы вторую строку.</summary>
    private static bool PriceMatchesOrVolume(JsonObject line, double unitPrice) =>
        PriceMatches(line, unitPrice)
        || (line[VolumeMinField] is not null && line["retail_price"] is { } retail && Math.Abs(JsonNumericReader.ToDouble(retail) - unitPrice) < 0.005);

    private sealed record VolumeChange(string ItemId, bool Revert, bool MarkSkip, VolumeTier? Tier);

    /// <summary>
    /// Цена по количеству: каждой обычной строке (товар без упаковки, варианта и серверного уровня цен) ставится цена ступени, подходящей к количеству; ушло ниже ступени — вернулась розничная.
    /// Ручной выбор главнее: строка, где кассир сам включил «Опт» (без метки ступени), не трогается; снял «Опт» со строки со ступенью — ступень к ней больше не применяется.
    /// Ступень, не дешевле цены строки, не применяется. Цена уходит в чек явной ценой (<c>is_wholesale</c> — сервер не считает её устаревшей, снятие опта вернёт <c>retail_price</c>).
    /// Возвращает число изменённых строк; повторный вызов без изменений чека ничего не меняет.
    /// </summary>
    public static int ApplyVolumePrices(ICartService cart, Func<string, IReadOnlyList<VolumeTier>?> tiersOf)
    {
        if (!cart.HasCart || cart.Root.ValueKind != JsonValueKind.Object)
            return 0;
        var changes = new List<VolumeChange>();
        foreach (var it in CartDisplayHelper.EnumerateItems(cart.Root))
        {
            if (!IsPriceLevelEligible(it) || CartDisplayHelper.TryItemId(it) is not { Length: > 0 } itemId || CartDisplayHelper.TryProductId(it) is not { Length: > 0 } productId)
                continue;
            if (it.TryGetProperty(PriceLevelField, out var level) && level.ValueKind == JsonValueKind.String)
                continue; // цена серверного уровня клиента главнее ступени
            if (it.TryGetProperty(VolumeSkipField, out var skip) && skip.ValueKind == JsonValueKind.True)
                continue;
            var isWholesale = it.TryGetProperty("is_wholesale", out var w) && w.ValueKind == JsonValueKind.True;
            var hasMark = it.TryGetProperty(VolumeMinField, out var mark) && mark.ValueKind == JsonValueKind.Number;
            if (isWholesale && !hasMark)
                continue; // кассир включил «Опт» сам
            if (hasMark && !isWholesale)
            {
                changes.Add(new VolumeChange(itemId, Revert: false, MarkSkip: true, null)); // кассир снял «Опт» со строки со ступенью
                continue;
            }
            var unit = CartDisplayHelper.UnitPrice(it);
            var basePrice = hasMark && it.TryGetProperty("retail_price", out var rp) && JsonNumericReader.TryToDouble(rp, out var retail) ? retail : unit;
            var qty = (decimal)CartDisplayHelper.LineQuantity(it);
            var tier = BazaarVolumePrices.Pick(tiersOf(productId), qty);
            if (tier is not null && (double)tier.Price >= basePrice - 0.005)
                tier = null; // ступень не дешевле розницы — не применяем
            if (tier is null)
            {
                if (hasMark)
                    changes.Add(new VolumeChange(itemId, Revert: true, MarkSkip: false, null));
                continue;
            }
            var markMin = hasMark && JsonNumericReader.TryToDouble(mark, out var m) ? m : double.NaN;
            if (!hasMark || Math.Abs(unit - (double)tier.Price) > 0.005 || Math.Abs(markMin - (double)tier.MinQty) > 1e-9)
                changes.Add(new VolumeChange(itemId, Revert: false, MarkSkip: false, tier));
        }
        if (changes.Count == 0)
            return 0;

        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray;
        var applied = 0;
        foreach (var c in changes)
        {
            var line = FindLineByItemId(items, c.ItemId);
            if (line is null)
                continue;
            if (c.MarkSkip)
            {
                line.Remove(VolumeMinField);
                line[VolumeSkipField] = true;
            }
            else if (c.Revert)
            {
                if (line["retail_price"] is { } retail)
                    line["unit_price"] = JsonNumericReader.ToDouble(retail);
                line.Remove("retail_price");
                line.Remove("is_wholesale");
                line.Remove(VolumeMinField);
            }
            else if (c.Tier is { } tier)
            {
                if (line[VolumeMinField] is null)
                    line["retail_price"] = JsonNumericReader.ToDouble(line["unit_price"]);
                line["unit_price"] = (double)tier.Price;
                line["is_wholesale"] = true;
                line[VolumeMinField] = (double)tier.MinQty;
            }
            ClampLineDiscountToGross(line);
            RecalcLine(line);
            applied++;
        }
        if (applied == 0)
            return 0;
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
        return applied;
    }

    /// <summary>Ступень, по которой сейчас стоит цена строки (метка «от N»), или null — строка по обычной цене.</summary>
    public static decimal? LineVolumeMin(JsonElement it) =>
        it.TryGetProperty(VolumeMinField, out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetDecimal(out var d) ? d : null;

    // ───────────── валюта ─────────────

    /// <summary>Положить в снимок чека принятую валюту (<see cref="BazaarCurrencies.CartField"/>) перед оплатой; null — убрать (оплата в сомах). Поле только для кассы и тела продажи.</summary>
    public static void SetFxPayment(ICartService cart, FxPayment? fx)
    {
        if (!cart.HasCart)
            return;
        var has = cart.Root.ValueKind == JsonValueKind.Object && cart.Root.TryGetProperty(BazaarCurrencies.CartField, out _);
        if (fx is null && !has)
            return;
        var root = ParseRoot(cart);
        if (fx is null)
            root.Remove(BazaarCurrencies.CartField);
        else
            root[BazaarCurrencies.CartField] = BazaarCurrencies.ToCartJson(fx);
        ApplyRoot(cart, root);
    }

    // ───────────── торг ─────────────

    /// <summary>Пометить скидку чека (<paramref name="itemId"/> = null) или строки причиной «торг» (<see cref="BazaarHaggle.ReasonField"/>). Только снимок кассы: на сервер поле не уходит.</summary>
    public static bool MarkDiscountReason(ICartService cart, string? itemId, string reason)
    {
        if (!cart.HasCart)
            return false;
        var root = ParseRoot(cart);
        var target = root;
        if (itemId is not null)
        {
            target = FindLineByItemId(root["items"] as JsonArray, itemId);
            if (target is null)
                return false;
        }
        target[BazaarHaggle.ReasonField] = reason;
        ApplyRoot(cart, root);
        return true;
    }

    /// <summary>Скидка чека сейчас поставлена торгом.</summary>
    public static bool OrderDiscountIsHaggle(JsonElement cartRoot) =>
        cartRoot.ValueKind == JsonValueKind.Object && cartRoot.TryGetProperty(BazaarHaggle.ReasonField, out var r)
        && r.ValueKind == JsonValueKind.String && r.GetString() == BazaarHaggle.ReasonValue;

    /// <summary>Сколько сомов сейчас можно уступить на чеке: сумма строк после их скидок (то, от чего считается скидка чека).</summary>
    public static decimal HaggleBase(JsonElement cartRoot)
    {
        var totals = CartTotalsCalculator.Calculate(cartRoot);
        return Math.Max(0m, (decimal)(totals.Subtotal - totals.LineDiscounts));
    }
}
