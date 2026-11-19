using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Одна позиция «В чек»: что нужно положить, что в каталоге сейчас и что изменилось с момента сметы.</summary>
public sealed record EstimateCartItem(EstimateLine Line, decimal Qty, EstimateCatalogInfo? Info)
{
    public bool IsCustom => Line.IsCustom;
    /// <summary>Товара больше нет в каталоге.</summary>
    public bool Missing => !IsCustom && Info is null;
    /// <summary>Цена сейчас (по уровню сметы).</summary>
    public decimal CurrentPrice(EstimateLevel level) => Info is null ? Line.Price : EstimateRules.PriceFor(Info, level);
    public bool PriceChanged(EstimateLevel level) => Info is not null && CurrentPrice(level) != Line.Price;
    /// <summary>Остатка не хватает на нужное количество (услуги без остатка не считаются).</summary>
    public bool NoStock => Info is { IsService: false } info && info.Stock < Qty;
    public bool StockChanged => Info is { IsService: false } info && Line.StockAtTime is { } was && was != info.Stock;
}

/// <summary>
/// 2026-11-19 («Смета», ТЗ 5): «Добавить всё в чек» — подготовка. Чистая логика: сопоставляет позиции сметы с текущим каталогом, находит изменения цены и остатка и
/// собирает текст для вопроса кассиру. В чек идёт ТЕКУЩАЯ цена каталога (её ставит сама корзина), количество — с запасом; своя позиция — строкой «Доп. услуга» по сумме
/// строки; «нет остатка» решает существующее правило продажи без остатка (окно кассы при добавлении). Скидки сметы в чек не переносятся — об этом говорит текст.
/// </summary>
public static class EstimateCartPlanner
{
    public static List<EstimateCartItem> Plan(EstimateDoc doc, Func<string, EstimateCatalogInfo?> find) =>
        doc.Lines.Where(l => EstimateRules.EffectiveQty(l, doc) > 0m)
            .Select(l => new EstimateCartItem(l, EstimateRules.EffectiveQty(l, doc), l.IsCustom ? null : find(l.ProductId!)))
            .ToList();

    public static bool HasChanges(IEnumerable<EstimateCartItem> items, EstimateLevel level) =>
        items.Any(i => i.Missing || i.PriceChanged(level) || i.NoStock);

    /// <summary>Строки про изменения: «Краска: цена 1 250,00 → 1 300,00», «Плитка: остаток 3, нужно 5», «Клей: товара нет в каталоге» (только то, что изменилось или мешает).</summary>
    public static string ChangesText(IEnumerable<EstimateCartItem> items, EstimateLevel level)
    {
        var sb = new StringBuilder();
        foreach (var i in items)
        {
            var name = i.Line.Name.Trim();
            if (i.Missing)
                sb.AppendLine($"• {name}: " + Tr.T("товара больше нет в каталоге — позиция не будет добавлена", "товар каталогто жок — позиция кошулбайт", "the product is no longer in the catalog — the line will not be added", "ürün artık katalogda yok — satır eklenmeyecek", "mahsulot katalogda yo'q — qator qo'shilmaydi"));
            if (i.PriceChanged(level))
                sb.AppendLine($"• {name}: " + Tr.T("цена", "баасы", "price", "fiyat", "narx") + $" {MoneyText.Amount(i.Line.Price)} → {MoneyText.Amount(i.CurrentPrice(level))}");
            if (i.NoStock)
                sb.AppendLine($"• {name}: " + Tr.T($"остаток {MoneyText.Weight(i.Info!.Stock, "0.##")}, нужно {MoneyText.Weight(i.Qty, "0.##")}",
                    $"калдык {MoneyText.Weight(i.Info!.Stock, "0.##")}, керек {MoneyText.Weight(i.Qty, "0.##")}",
                    $"in stock {MoneyText.Weight(i.Info!.Stock, "0.##")}, needed {MoneyText.Weight(i.Qty, "0.##")}",
                    $"stokta {MoneyText.Weight(i.Info!.Stock, "0.##")}, gereken {MoneyText.Weight(i.Qty, "0.##")}",
                    $"qoldiq {MoneyText.Weight(i.Info!.Stock, "0.##")}, kerak {MoneyText.Weight(i.Qty, "0.##")}"));
            else if (i.StockChanged)
                sb.AppendLine($"• {name}: " + Tr.T($"остаток изменился: был {MoneyText.Weight(i.Line.StockAtTime!.Value, "0.##")}, сейчас {MoneyText.Weight(i.Info!.Stock, "0.##")}",
                    $"калдык өзгөрдү: {MoneyText.Weight(i.Line.StockAtTime!.Value, "0.##")} болчу, азыр {MoneyText.Weight(i.Info!.Stock, "0.##")}",
                    $"stock changed: was {MoneyText.Weight(i.Line.StockAtTime!.Value, "0.##")}, now {MoneyText.Weight(i.Info!.Stock, "0.##")}",
                    $"stok değişti: önceden {MoneyText.Weight(i.Line.StockAtTime!.Value, "0.##")}, şimdi {MoneyText.Weight(i.Info!.Stock, "0.##")}",
                    $"qoldiq o'zgardi: avval {MoneyText.Weight(i.Line.StockAtTime!.Value, "0.##")}, hozir {MoneyText.Weight(i.Info!.Stock, "0.##")}"));
        }
        return sb.ToString().TrimEnd();
    }
}
