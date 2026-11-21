namespace NurMarketKassa.Services.ProductMerge;

/// <summary>Что произойдёт с одним товаром-источником: какие коды уйдут главному и под каким названием варианта.</summary>
public sealed record MergeVariantLine(
    MergeProduct Source,
    string Name,
    IReadOnlyList<MergeAltCode> CodesToAdd,
    double Stock,
    bool PriceDiffers);

/// <summary>Почему «Объединить» для группы недоступно.</summary>
public enum MergeProblem
{
    None,
    NoMain,
    MainBlocked,
    NothingToMerge,
}

/// <summary>Предпросмотр слияния группы: итог без записи на сервер (окно показывает его владельцу до нажатия «Объединить»).</summary>
public sealed class MergePlan
{
    public required MergeGroup Group { get; init; }

    public MergeProduct? Main { get; init; }

    /// <summary>Новое название главного товара; null — название не меняется.</summary>
    public string? NewMainTitle { get; init; }

    public IReadOnlyList<MergeVariantLine> Variants { get; init; } = [];

    /// <summary>Доп. коды главного после слияния: прежние плюс принесённые (без повторов).</summary>
    public IReadOnlyList<MergeAltCode> FinalAltCodes { get; init; } = [];

    public double MainStockBefore { get; init; }

    public double TotalStock { get; init; }

    public double? OldPurchase { get; init; }

    /// <summary>Новая закупочная цена (средневзвешенная по остаткам); null — не меняется (остатков нет или у кого-то с остатком нет закупки).</summary>
    public double? NewPurchase { get; init; }

    public bool PurchaseUnknown { get; init; }

    public IReadOnlyList<MergeWarning> Warnings { get; init; } = [];

    public MergeProblem Problem { get; init; }

    /// <summary>Источники, исключённые автоматически (блокирующие причины) — с причинами.</summary>
    public IReadOnlyList<(MergeProduct Product, IReadOnlyList<MergeBlocker> Blockers)> Blocked { get; init; } = [];

    public bool CanRun => Problem == MergeProblem.None;

    public IEnumerable<string> SourceIds => Variants.Select(v => v.Source.Id);
}

public static class MergePlanner
{
    private static string UnitKey(string? unit)
    {
        var u = (unit ?? "").Trim().ToLowerInvariant().TrimEnd('.');
        return u is "" or "шт" or "pcs" or "дона" or "dona" or "adet" ? "шт" : u;
    }

    /// <summary>Блокирующие причины товара с учётом выбранного главного (единица измерения сверяется с главным).</summary>
    public static IReadOnlyList<MergeBlocker> EffectiveBlockers(MergeMember member, MergeProduct main)
    {
        var list = member.Blockers.Where(b => b != MergeBlocker.OtherUnit).ToList();
        if (UnitKey(member.Product.Unit) != UnitKey(main.Unit))
            list.Add(MergeBlocker.OtherUnit);
        return list;
    }

    /// <summary>Главный по умолчанию: без отличительного слова («Асу»), потом с наибольшим остатком, потом самое короткое название.</summary>
    public static MergeDraft DefaultDraft(MergeGroup group)
    {
        var draft = new MergeDraft();
        var candidates = group.Members.Where(m => m.Blockers.Count == 0).ToList();
        var main = candidates.OrderByDescending(m => m.VariantName.Length == 0).ThenByDescending(m => m.Product.Quantity)
            .ThenBy(m => m.Product.Title.Length).ThenBy(m => m.Product.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? group.Members.OrderByDescending(m => m.Product.Quantity).First();
        draft.MainId = main.Product.Id;
        foreach (var m in group.Members)
        {
            draft.VariantNames[m.Product.Id] = m.VariantName;
            if (m.Blockers.Count > 0)
                draft.Excluded.Add(m.Product.Id);
        }
        draft.NewMainTitle = NeedsRename(group, main) ? group.BaseTitle : null;
        return draft;
    }

    /// <summary>Название главного надо поправить, если в нём остался вкус/цвет («Асу клубника»): иначе карточка с вариантами называлась бы одним из вкусов.</summary>
    public static bool NeedsRename(MergeGroup group, MergeMember main) =>
        main.VariantName.Length > 0 && group.BaseTitle.Length > 0
        && !string.Equals(main.Product.Title.Trim(), group.BaseTitle.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public static void SetMain(MergeGroup group, MergeDraft draft, string mainId)
    {
        draft.MainId = mainId;
        var main = group.Members.FirstOrDefault(m => m.Product.Id == mainId);
        draft.Excluded.Remove(mainId);
        draft.NewMainTitle = main is not null && NeedsRename(group, main) ? group.BaseTitle : null;
    }

    public static MergePlan Build(MergeGroup group, MergeDraft draft)
    {
        var mainMember = group.Members.FirstOrDefault(m => m.Product.Id == draft.MainId);
        if (mainMember is null)
            return new MergePlan { Group = group, Problem = MergeProblem.NoMain, Warnings = group.Warnings };
        var main = mainMember.Product;
        var mainBlockers = mainMember.Blockers.Where(b => b != MergeBlocker.OtherUnit).ToList();
        var blocked = new List<(MergeProduct, IReadOnlyList<MergeBlocker>)>();
        var variants = new List<MergeVariantLine>();
        var knownCodes = new HashSet<string>(main.AllCodes(), StringComparer.OrdinalIgnoreCase);
        var finalAlt = new List<MergeAltCode>(main.AltCodes);

        foreach (var m in group.Members.Where(x => x.Product.Id != main.Id))
        {
            var blockers = EffectiveBlockers(m, main);
            if (blockers.Count > 0)
            {
                blocked.Add((m.Product, blockers));
                continue;
            }
            if (draft.Excluded.Contains(m.Product.Id))
                continue;
            var name = draft.VariantNames.TryGetValue(m.Product.Id, out var n) ? n.Trim() : m.VariantName;
            var add = new List<MergeAltCode>();
            // основной код источника получает название варианта; его собственные доп. коды сохраняют свои названия (пустое — название варианта)
            if (!string.IsNullOrWhiteSpace(m.Product.Barcode) && knownCodes.Add(m.Product.Barcode.Trim()))
                add.Add(new MergeAltCode(m.Product.Barcode.Trim(), name, 0));
            foreach (var a in m.Product.AltCodes)
                if (!string.IsNullOrWhiteSpace(a.Barcode) && knownCodes.Add(a.Barcode.Trim()))
                    add.Add(new MergeAltCode(a.Barcode.Trim(), string.IsNullOrWhiteSpace(a.Name) ? name : a.Name, a.Quantity));
            variants.Add(new MergeVariantLine(m.Product, name, add, m.Product.Quantity, !MergeCandidateFinder.SamePrice(m.Product.Price, main.Price)));
            finalAlt.AddRange(add);
        }

        var stockMembers = new List<MergeProduct> { main };
        stockMembers.AddRange(variants.Select(v => v.Source));
        var total = Math.Round(stockMembers.Sum(p => Math.Max(0, p.Quantity)), 3);
        var withStock = stockMembers.Where(p => p.Quantity > 0.0005).ToList();
        var unknown = withStock.Any(p => p.PurchasePrice <= 0);
        double? newPurchase = null;
        if (!unknown && withStock.Count > 1)
            newPurchase = Math.Round(withStock.Sum(p => p.Quantity * p.PurchasePrice) / withStock.Sum(p => p.Quantity), 4);

        var problem = mainBlockers.Count > 0 ? MergeProblem.MainBlocked : variants.Count == 0 ? MergeProblem.NothingToMerge : MergeProblem.None;
        var warnings = new List<MergeWarning>(group.Warnings.Where(w => w != MergeWarning.PricesDiffer));
        if (variants.Any(v => v.PriceDiffers))
            warnings.Add(MergeWarning.PricesDiffer);
        var title = string.IsNullOrWhiteSpace(draft.NewMainTitle) || string.Equals(draft.NewMainTitle.Trim(), main.Title.Trim(), StringComparison.Ordinal) ? null : draft.NewMainTitle.Trim();
        return new MergePlan
        {
            Group = group,
            Main = main,
            NewMainTitle = title,
            Variants = variants,
            FinalAltCodes = finalAlt,
            MainStockBefore = main.Quantity,
            TotalStock = total,
            OldPurchase = main.PurchasePrice > 0 ? main.PurchasePrice : null,
            NewPurchase = newPurchase,
            PurchaseUnknown = unknown && withStock.Count > 1,
            Warnings = warnings,
            Problem = problem,
            Blocked = blocked,
        };
    }
}
