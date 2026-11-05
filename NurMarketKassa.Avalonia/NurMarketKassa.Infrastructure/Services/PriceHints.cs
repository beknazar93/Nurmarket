using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 7): подсказки цены после прихода — закупка выросла, цена продажи осталась прежней: «Поднять цену с 85 до 95 (наценка 30 %)?».
// Идея — правила цен Odoo (цена = закупка + наценка, округление до …5/…0), код свой. Уже есть в приёмке: «Обновить цены продажи» (ReceivingPricing.PreviewRaise)
// до проведения; здесь — список ПОСЛЕ прихода по тому, что реально проведено, с «красивым» округлением до 5/10 сом и целевой наценкой по категориям.

/// <summary>Одна подсказка: товар, закупка и цена продажи было → предлагается.</summary>
public sealed record PriceHint(string ProductId, string Name, double OldPurchase, double NewPurchase, double CurrentSale, double SuggestedSale, double? SuggestedMarkup)
{
    public string Text => Tr.T($"Поднять цену с {Fmt(CurrentSale)} до {Fmt(SuggestedSale)}" + MarkupText(),
        $"Баасын {Fmt(CurrentSale)} дан {Fmt(SuggestedSale)} га көтөрөбүзбү" + MarkupText(),
        $"Raise the price from {Fmt(CurrentSale)} to {Fmt(SuggestedSale)}" + MarkupText(),
        $"Fiyatı {Fmt(CurrentSale)} → {Fmt(SuggestedSale)} yükselt" + MarkupText(),
        $"Narxni {Fmt(CurrentSale)} dan {Fmt(SuggestedSale)} ga ko'tarish" + MarkupText());

    private string MarkupText() => SuggestedMarkup is { } m ? $" ({Tr.T("наценка", "үстөк", "markup", "kâr payı", "ustama")} {m:0.#} %)" : "";

    private static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

public static class PriceHints
{
    /// <summary>«Красивая» цена: вверх до шага (1, 5 или 10 сом). 0 — не округлять (оставить как есть). Дешёвый товар (цена меньше двух шагов) округляется до целого,
    /// иначе «красивая» цена оказалась бы заметно дороже расчётной.</summary>
    public static double NiceUp(double price, int step)
    {
        if (price <= 0)
            return 0;
        if (step <= 0)
            return Math.Round(price, 2);
        var useStep = step >= 5 && price < step * 2 ? 1 : step;
        return Math.Ceiling(price / useStep - 1e-9) * useStep;
    }

    /// <summary>Целевая наценка, %: настройка категории → прежняя наценка товара (по прежним ценам) → общая категории/умолчание приёмки.</summary>
    public static double? TargetMarkup(string? category, double oldPurchase, double oldSale, ShopAutomationData data, ReceivingMarkupSettings receiving)
    {
        if (!string.IsNullOrWhiteSpace(category) && data.CategoryTargetMarkup.TryGetValue(category.Trim(), out var cat) && cat > 0)
            return cat;
        if (ReceivingPricing.MarkupPercent(oldPurchase, oldSale) is { } old && old > 0)
            return old;
        if (!string.IsNullOrWhiteSpace(category) && receiving.CategoryPercents.TryGetValue(category.Trim(), out var rc) && rc > 0)
            return rc;
        return receiving.DefaultPercent > 0 ? receiving.DefaultPercent : null;
    }

    /// <summary>Чистая часть расчёта одной подсказки. null — подсказывать нечего (закупка не выросла, цену уже поднимали руками, цена не вырастет).</summary>
    public static PriceHint? For(string productId, string name, string? category, double oldPurchase, double newPurchase, double oldSale, double currentSale,
        ShopAutomationData data, ReceivingMarkupSettings receiving)
    {
        if (oldPurchase <= 0 || newPurchase <= oldPurchase || oldSale <= 0 || currentSale <= 0)
            return null;
        if (Math.Abs(currentSale - oldSale) > 0.004)
            return null; // цену продажи уже поправили (в приёмке или руками) — не вмешиваемся
        if (TargetMarkup(category, oldPurchase, oldSale, data, receiving) is not { } markup)
            return null;
        var suggested = NiceUp(newPurchase * (1 + markup / 100.0), data.PriceNiceStep);
        if (suggested <= currentSale + 0.004)
            return null;
        return new PriceHint(productId, name, oldPurchase, newPurchase, currentSale, suggested, ReceivingPricing.MarkupPercent(newPurchase, suggested));
    }

    /// <summary>Подсказки по проведённым строкам прихода (товары склада; новые товары не трогаем — у них нет прежней цены).</summary>
    public static List<PriceHint> ForPosted(IEnumerable<ReceivingLineVm> lines)
    {
        var data = ShopAutomationSettings.Current;
        var receiving = ReceivingMarkupSettingsStore.Current;
        var hints = new List<PriceHint>();
        foreach (var l in lines)
        {
            if (l.IsNew || string.IsNullOrEmpty(l.ProductId))
                continue;
            // 2026-11-05 (ревью волн 0–3, Н5): при включённой доставке сравниваем себестоимость с доставкой, а не только цену по накладной.
            var newPurchase = l.LandedExtraPerUnit > 0 ? l.CostPrice : l.PurchasePrice;
            if (For(l.ProductId!, l.ProductName, ReceivingPricing.CategoryOf(l), l.OriginalPurchasePrice, newPurchase, l.OriginalSalePrice, l.SalePrice, data, receiving) is { } h)
                hints.Add(h);
        }
        return hints;
    }
}
