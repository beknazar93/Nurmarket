using System.Globalization;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А): разбор и вывод простых текстовых списков из окна «Автоматизация магазина» — по строке на запись.
// «Название; телефон» (запасные поставщики) и «Категория = процент» (целевая наценка). Мусорные строки пропускаются.

public static class ShopAutomationText
{
    public static List<ReserveSupplier> ParseReserve(string? text)
    {
        var list = new List<ReserveSupplier>();
        foreach (var raw in (text ?? "").Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var cut = line.IndexOf(';');
            var name = (cut >= 0 ? line[..cut] : line).Trim();
            var phone = cut >= 0 ? line[(cut + 1)..].Trim() : "";
            if (name.Length == 0 || list.Any(r => string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                continue;
            list.Add(new ReserveSupplier { Name = name, Phone = phone });
        }
        return list;
    }

    public static string FormatReserve(IEnumerable<ReserveSupplier> list) =>
        string.Join("\n", list.Select(r => r.Phone.Length > 0 ? $"{r.Name}; {r.Phone}" : r.Name));

    public static Dictionary<string, double> ParseMarkups(string? text)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n', '\r'))
        {
            var cut = raw.LastIndexOf('=');
            if (cut <= 0)
                continue;
            var name = raw[..cut].Trim();
            var number = raw[(cut + 1)..].Replace("%", "").Trim().Replace(',', '.');
            if (name.Length > 0 && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && percent > 0 && percent <= 1000)
                map[name] = percent;
        }
        return map;
    }

    public static string FormatMarkups(IReadOnlyDictionary<string, double> map) =>
        string.Join("\n", map.OrderBy(m => m.Key, StringComparer.CurrentCultureIgnoreCase).Select(m => $"{m.Key} = {m.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));

    /// <summary>Число из поля ввода: запятая или точка, пробелы; пусто или мусор — null.</summary>
    public static double? ParseNumber(string? text)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    public static string FormatNumber(double v) => v == 0 ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
}
