using NurMarketKassa.Core.Application;
using System.Globalization;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Фильтр списка окна «Печать штрихкодов и ценников» по наличию штрихкода.</summary>
public enum BarcodeFilter { All, WithBarcode, WithoutBarcode, WithProblems }

/// <summary>Один штрихкод товара для выбора на карточке: основной или дополнительный (alternate_barcodes).</summary>
public sealed record BarcodeChoice(string Code, bool IsMain, string? VariantName, double Quantity = 0, bool IsSize = false, double? Price = null);

/// <summary>Товар, поставленный в печать: сам товар, выбранный штрихкод и число копий.</summary>
public sealed record BarcodePrintItem(CatalogProductTileVm Product, string? Barcode, int Copies, string? VariantName = null, double VariantQuantity = 0, double? VariantPrice = null)
{
    /// <summary>2026-11-01 (С-2): подпись доп. штрихкода на этикетке — название упаковки/размера и «×N» при количестве больше 1. null — основной код.</summary>
    public string? VariantText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(VariantName) ? null : VariantName!.Trim();
            var qty = VariantQuantity > 1 ? "×" + VariantQuantity.ToString("0.##", CultureInfo.InvariantCulture) : null;
            return name is null ? qty : qty is null ? name : name + " " + qty;
        }
    }
}

/// <summary>2026-10-26, окно «Печать штрихкодов и ценников» (как «Печать штрих-кодов» на сайте NurCRM): поиск, фильтры, выбор
/// штрихкода и сборка данных для печати — без интерфейса, чтобы то же самое делали стенд проверки и обе печати (термопринтер, A4).</summary>
public static class BarcodePrintCatalog
{
    /// <summary>Категории каталога (без пустых), по алфавиту.</summary>
    public static List<string> Categories(IReadOnlyList<CatalogProductTileVm> source)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < source.Count; i++)
            if (!string.IsNullOrWhiteSpace(source[i].Category))
                set.Add(source[i].Category!.Trim());
        var list = set.ToList();
        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        return list;
    }

    /// <summary>Основной и дополнительные штрихкоды товара: основной первым, повторы не показываются. Пусто — штрихкода нет.</summary>
    public static List<BarcodeChoice> Choices(CatalogProductTileVm p)
    {
        var list = new List<BarcodeChoice>();
        var main = p.Barcode?.Trim();
        if (!string.IsNullOrWhiteSpace(main))
            list.Add(new BarcodeChoice(main!, true, null));
        if (p.AlternateBarcodeVariants is { Count: > 0 } alt)
        {
            foreach (var v in alt)
            {
                var code = v.Barcode?.Trim();
                if (string.IsNullOrWhiteSpace(code) || list.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new BarcodeChoice(code!, false, string.IsNullOrWhiteSpace(v.Name) ? null : v.Name!.Trim(), v.Quantity));
            }
        }
        // 2026-11-01 (П-3, из веба): штрихкоды размеров одежды («44, Красный») с ценой размера — как печатает «Размеры и цвета».
        foreach (var hit in VariantBarcodeIndex.ForProduct(p.Id))
        {
            var code = hit.Variant.Barcode?.Trim();
            if (string.IsNullOrWhiteSpace(code) || list.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)))
                continue;
            var text = string.Join(", ", new[] { hit.Variant.Size?.Trim(), hit.Variant.Color?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new BarcodeChoice(code!, false, text.Length == 0 ? null : text, 0, true, hit.Variant.Price is > 0 ? hit.Variant.Price : null));
        }
        return list;
    }

    /// <summary>Можно ли что-то напечатать: есть основной, дополнительный штрихкод или штрихкод размера.</summary>
    public static bool HasPrintableCode(CatalogProductTileVm p) => HasBarcode(p) || VariantBarcodeIndex.ForProduct(p.Id).Count > 0;

    /// <summary>2026-11-01 (П-4): «Копии = остаток» — целая часть остатка 1..99. Весовые, дробные и измеряемые (кг, л, м) товары — 1.
    /// <paramref name="capped"/> — остаток был больше 99.</summary>
    public static int StockCopies(CatalogProductTileVm p, out bool capped) => CopiesForQuantity(p.Quantity, p.Unit, p.MustWeigh, out capped);

    /// <summary>Число копий по количеству (остаток или принято): целое 1..99; весовые, дробные и измеряемые (кг, л, м) — 1; больше 99 — 99 и <paramref name="capped"/>.</summary>
    public static int CopiesForQuantity(double q, string? unit, bool mustWeigh, out bool capped)
    {
        capped = false;
        if (mustWeigh || double.IsNaN(q) || q < 1 || Math.Abs(q - Math.Round(q)) > 1e-9 || IsMeasuredUnit(unit))
            return 1;
        if (q > 99)
        {
            capped = true;
            return 99;
        }
        return (int)Math.Floor(q);
    }

    private static readonly string[] MeasuredUnits = ["кг", "kg", "г", "g", "гр", "л", "l", "lt", "мл", "ml", "м", "m", "см", "cm", "мм", "mm"];

    private static bool IsMeasuredUnit(string? unit)
    {
        var u = unit?.Trim().TrimEnd('.').ToLowerInvariant();
        return !string.IsNullOrEmpty(u) && MeasuredUnits.Contains(u);
    }

    /// <summary>Поиск по названию, штрихкоду (основному и дополнительным), артикулу и коду товара; несколько слов — все должны найтись.
    /// Скан штрихкода другой длины (12 ↔ 13 цифр) находит тот же товар, что нашла бы касса (ProductCodeLookup).</summary>
    public static List<CatalogProductTileVm> Search(IReadOnlyList<CatalogProductTileVm> source, string? query, BarcodeFilter filter, string? category)
    {
        // 2026-10-29: единый поиск (SearchMatch) — ё=е, слова в любом порядке; если как набрано ничего нет — не та раскладка/транслит («Asu»→«Асу»).
        var found = SearchCore(source, query, filter, category, false);
        return found.Count == 0 && !string.IsNullOrWhiteSpace(query) ? SearchCore(source, query, filter, category, true) : found;
    }

    private static List<CatalogProductTileVm> SearchCore(IReadOnlyList<CatalogProductTileVm> source, string? query, BarcodeFilter filter, string? category, bool fuzzy)
    {
        var sq = SearchMatch.Prepare(query);
        var tokens = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string? exactId = null;
        var q = (query ?? "").Trim();
        if (q.Length >= 8 && q.All(char.IsAsciiDigit))
        {
            try { exactId = ProductCodeLookup.FindLocal(q)?.Product.Id; }
            catch { exactId = null; }
        }
        var hasCategory = !string.IsNullOrWhiteSpace(category);
        var result = new List<CatalogProductTileVm>(Math.Min(source.Count, 4096));
        for (var i = 0; i < source.Count; i++)
        {
            var p = source[i];
            if (hasCategory && !string.Equals(p.Category?.Trim(), category!.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (filter is BarcodeFilter.WithBarcode or BarcodeFilter.WithoutBarcode)
            {
                var has = HasBarcode(p);
                if (has != (filter == BarcodeFilter.WithBarcode))
                    continue;
            }
            if (tokens.Length > 0 && !(exactId != null && string.Equals(p.Id, exactId, StringComparison.OrdinalIgnoreCase)) && !(fuzzy ? sq.MatchesFuzzy(p.SearchKey) : (MatchesAll(p, tokens) || sq.Matches(p.SearchKey))))
                continue;
            result.Add(p);
        }
        return result;
    }

    /// <summary>Есть ли у товара хоть один штрихкод (основной или дополнительный).</summary>
    public static bool HasBarcode(CatalogProductTileVm p)
    {
        if (!string.IsNullOrWhiteSpace(p.Barcode))
            return true;
        if (p.AlternateBarcodeVariants is { Count: > 0 } alt)
            foreach (var v in alt)
                if (!string.IsNullOrWhiteSpace(v.Barcode))
                    return true;
        return false;
    }

    private static bool MatchesAll(CatalogProductTileVm p, string[] tokens)
    {
        foreach (var t in tokens)
        {
            if (p.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (p.Barcode?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Article?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.ProductCode?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            var found = false;
            if (p.AlternateBarcodeVariants is { Count: > 0 } alt)
                foreach (var v in alt)
                    if (v.Barcode.Contains(t, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            if (!found)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ цена

    private static readonly Regex PriceRx = new(@"^\s*(-?\d[\d\s ]*(?:[.,]\d+)?)\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Разбор строки цены каталога («85.00 сом»): число и хвост («сом»). Нет числа — null.</summary>
    public static (decimal Value, string Suffix)? ParsePrice(string? priceLine)
    {
        var m = PriceRx.Match(priceLine ?? "");
        if (!m.Success)
            return null;
        var digits = m.Groups[1].Value.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? (v, m.Groups[2].Value.Trim()) : null;
    }

    /// <summary>Цена как на ценнике этикетки: с запятой (как в остальных окнах, аудит 20.10), «85,00 сом».</summary>
    public static string LabelPrice(CatalogProductTileVm p) =>
        Regex.Replace(p.PriceLine ?? "", @"(?<=\d)\.(?=\d)", ",");

    /// <summary>Цена на этикетке для позиции печати: у размера — цена размера (если задана), иначе цена товара. Копейки не округляются.</summary>
    public static string LabelPrice(BarcodePrintItem item)
    {
        if (item.VariantPrice is > 0 and var vp)
        {
            var suffix = ParsePrice(item.Product.PriceLine)?.Suffix;
            var text = ((decimal)vp).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            return string.IsNullOrEmpty(suffix) ? text + " сом" : text + " " + suffix;
        }
        return LabelPrice(item.Product);
    }

    /// <summary>Число для листа A4: без «,00» у целых («85»), с копейками у дробных («85,50»).</summary>
    public static string SheetNumber(decimal value) =>
        (value == decimal.Truncate(value) ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture)).Replace('.', ',');

    /// <summary>Данные одного товара для листа A4. «Скидка (зачеркнуть старую цену)»: если у товара есть скидка (discount_percent
    /// карточки), старая цена — цена карточки, новая — цена минус скидка (копейки округляются как везде, от нуля). Цена карточки считается
    /// ценой ДО скидки. Скидки у товара нет — одна цена, как обычно.</summary>
    public static A4SheetItem ToSheetItem(BarcodePrintItem item, bool showDiscount, string? storeName = null)
    {
        var p = item.Product;
        var parsed = ParsePrice(item.VariantPrice is > 0 ? LabelPrice(item) : p.PriceLine);
        string price;
        string? old = null;
        string? discountText = null;
        if (parsed is { } pr)
        {
            var suffix = pr.Suffix.Length > 0 ? " " + pr.Suffix : "";
            if (showDiscount && p.HasDiscount && p.DiscountPercent is > 0 and <= 100)
            {
                var net = Math.Round(pr.Value * (1m - (decimal)p.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
                old = SheetNumber(pr.Value) + suffix;
                price = SheetNumber(net) + suffix;
                discountText = "-" + p.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%";
            }
            else
                price = SheetNumber(pr.Value) + suffix;
        }
        else
            price = p.PriceLine ?? "";
        // 2026-11-01 (С-2): у доп. штрихкода (упаковка/размер) к названию добавляется его подпись.
        var title = item.VariantText is { } vt ? p.Title + " — " + vt : p.Title;
        var data = new PriceTagData(title, item.Barcode, price, old, discountText, p.Article, p.Unit, p.Category ?? p.Brand, storeName);
        return new A4SheetItem(data, Math.Clamp(item.Copies, 1, 99), p.Plu?.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(p.Description) ? null : p.Description!.Trim());
    }
}
