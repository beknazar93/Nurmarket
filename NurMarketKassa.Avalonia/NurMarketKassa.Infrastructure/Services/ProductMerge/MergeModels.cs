using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.ProductMerge;

// 2026-11-21 (просьба владельца «исправь в нашем складе уже загруженных товаров»): «Найти дубли и варианты» — вкусы/цвета одного товара, загруженные отдельными карточками
// («Асу», «Асу клубника», «Асу лимон»), сводятся в один товар с доп. штрихкодами. Здесь — чистые данные без окон и без сети (тесты — Tests/Stock/ProductMerge*Tests).

/// <summary>Доп. штрихкод карточки: код, название варианта (сочетается с названием товара при сканировании) и количество в упаковке (только для показа).</summary>
public sealed record MergeAltCode(string Barcode, string? Name, double Quantity);

/// <summary>Снимок карточки товара для поиска групп и предпросмотра (из локального каталога кассы).</summary>
public sealed record MergeProduct(
    string Id,
    string Title,
    string? Brand,
    string? Category,
    double Price,
    string? Barcode,
    IReadOnlyList<MergeAltCode> AltCodes,
    string? Unit,
    bool IsWeight,
    string? Kind,
    double Quantity,
    double PurchasePrice,
    bool HasExpiry,
    string? ImageUrl)
{
    public bool IsBundle => string.Equals(Kind, "bundle", StringComparison.OrdinalIgnoreCase);

    public bool IsService => string.Equals(Kind, "service", StringComparison.OrdinalIgnoreCase);

    /// <summary>Все коды карточки: основной и дополнительные.</summary>
    public IEnumerable<string> AllCodes()
    {
        if (!string.IsNullOrWhiteSpace(Barcode))
            yield return Barcode.Trim();
        foreach (var a in AltCodes)
            if (!string.IsNullOrWhiteSpace(a.Barcode))
                yield return a.Barcode.Trim();
    }

    public static MergeProduct From(CatalogProductTileVm t)
    {
        var alt = new List<MergeAltCode>();
        if (t.AlternateBarcodeVariants is { Count: > 0 } variants)
            alt.AddRange(variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)).Select(v => new MergeAltCode(v.Barcode.Trim(), v.Name, v.Quantity)));
        else if (!string.IsNullOrWhiteSpace(t.AlternateBarcodesRaw))
            alt.AddRange(t.AlternateBarcodesRaw.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => new MergeAltCode(c, null, 0)));
        return new MergeProduct(t.Id, t.Title ?? "", t.Brand, t.Category, t.PriceValue, t.Barcode?.Trim(), alt, t.Unit,
            t.IsWeighted || t.MustWeigh || t.ServerIsWeight == true, t.Kind, t.Quantity, t.PurchasePrice, t.HasExpiryBadge, t.ImageUrl);
    }
}

/// <summary>Причина, по которой карточку нельзя объединять (красная): весовой, комплект, услуга, другая единица, нет штрихкода.</summary>
public enum MergeBlocker
{
    Weighed,
    Bundle,
    Service,
    OtherUnit,
    NoBarcode,
}

/// <summary>Предупреждение к группе (жёлтое): решение за владельцем.</summary>
public enum MergeWarning
{
    /// <summary>Цены продажи разные: после объединения варианты продаются по цене главного.</summary>
    PricesDiffer,
    /// <summary>У части товаров есть срок годности/партии: остатки сложатся в один.</summary>
    ExpiryBatches,
    /// <summary>У главного и источника есть остаток и закупка разная — закупка станет средневзвешенной.</summary>
    PurchaseDiffers,
    /// <summary>Бренд указан не у всех товаров группы.</summary>
    BrandMissing,
    /// <summary>Категории не совпадают или указаны не у всех.</summary>
    CategoryDiffers,
}

public enum MergeConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>Товар группы-кандидата с названием варианта, выведенным из разницы названий.</summary>
public sealed class MergeMember
{
    public MergeMember(MergeProduct product, string variantName, IReadOnlyList<MergeBlocker> blockers)
    {
        Product = product;
        VariantName = variantName;
        Blockers = blockers;
    }

    public MergeProduct Product { get; }

    /// <summary>Вкус/цвет из названия («Клубника»); пусто — у товара нет отличительного слова (основной «Асу»).</summary>
    public string VariantName { get; }

    public IReadOnlyList<MergeBlocker> Blockers { get; set; }
}

/// <summary>Группа-кандидат: товары с общей основой названия, которые можно свести в один.</summary>
public sealed class MergeGroup
{
    public MergeGroup(string id, string baseTitle, IReadOnlyList<MergeMember> members, int confidence, string? brand, string? category, bool byDictionary,
        IReadOnlyList<MergeWarning> warnings)
    {
        Id = id;
        BaseTitle = baseTitle;
        Members = members;
        Score = confidence;
        Brand = brand;
        Category = category;
        ByDictionary = byDictionary;
        Warnings = warnings;
    }

    /// <summary>Стабильный ключ группы (хэш id товаров) — по нему запоминаются отказы «объединять не надо».</summary>
    public string Id { get; }

    public string BaseTitle { get; }

    public IReadOnlyList<MergeMember> Members { get; }

    /// <summary>Оценка уверенности 0–100.</summary>
    public int Score { get; }

    public MergeConfidence Confidence => Score >= 75 ? MergeConfidence.High : Score >= 50 ? MergeConfidence.Medium : MergeConfidence.Low;

    public string? Brand { get; }

    public string? Category { get; }

    /// <summary>true — группа найдена по словарю вкусов/цветов; false — только по общему началу названия.</summary>
    public bool ByDictionary { get; }

    public IReadOnlyList<MergeWarning> Warnings { get; }

    public bool PricesDiffer => Warnings.Contains(MergeWarning.PricesDiffer);

    /// <summary>Сколько товаров группы вообще можно объединять (без блокирующих причин).</summary>
    public int MergeableCount => Members.Count(m => m.Blockers.Count == 0);
}

/// <summary>Правки владельца к группе в окне: главный товар, исключённые, названия вариантов, новое название главного.</summary>
public sealed class MergeDraft
{
    public string MainId { get; set; } = "";

    public HashSet<string> Excluded { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> VariantNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Новое название главного товара; пусто — не менять.</summary>
    public string? NewMainTitle { get; set; }
}
