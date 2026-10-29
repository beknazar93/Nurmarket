using NurMarketKassa.Core.Application;
using System.Globalization;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Фильтр списка окна «Печать штрихкодов и ценников» по наличию штрихкода.</summary>
public enum BarcodeFilter { All, WithBarcode, WithoutBarcode }

/// <summary>Один штрихкод товара для выбора на карточке: основной или дополнительный (alternate_barcodes).</summary>
public sealed record BarcodeChoice(string Code, bool IsMain, string? VariantName);

/// <summary>Товар, поставленный в печать: сам товар, выбранный штрихкод и число копий.</summary>
public sealed record BarcodePrintItem(CatalogProductTileVm Product, string? Barcode, int Copies);

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
                list.Add(new BarcodeChoice(code!, false, string.IsNullOrWhiteSpace(v.Name) ? null : v.Name!.Trim()));
            }
        }
        return list;
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
            if (filter != BarcodeFilter.All)
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

    /// <summary>Число для листа A4: без «,00» у целых («85»), с копейками у дробных («85,50»).</summary>
    public static string SheetNumber(decimal value) =>
        (value == decimal.Truncate(value) ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture)).Replace('.', ',');

    /// <summary>Данные одного товара для листа A4. «Скидка (зачеркнуть старую цену)»: если у товара есть скидка (discount_percent
    /// карточки), старая цена — цена карточки, новая — цена минус скидка (копейки округляются как везде, от нуля). Цена карточки считается
    /// ценой ДО скидки. Скидки у товара нет — одна цена, как обычно.</summary>
    public static A4SheetItem ToSheetItem(BarcodePrintItem item, bool showDiscount, string? storeName = null)
    {
        var p = item.Product;
        var parsed = ParsePrice(p.PriceLine);
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
        var data = new PriceTagData(p.Title, item.Barcode, price, old, discountText, p.Article, p.Unit, p.Category ?? p.Brand, storeName);
        return new A4SheetItem(data, Math.Clamp(item.Copies, 1, 99), p.Plu?.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(p.Description) ? null : p.Description!.Trim());
    }
}
