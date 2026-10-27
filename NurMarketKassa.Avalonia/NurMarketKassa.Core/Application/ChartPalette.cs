using System.Globalization;

namespace NurMarketKassa.Core.Application;

/// <summary>2026-10-26, владелец: «в аналитике, сводке используй РАЗНЫЕ ЦВЕТА в диаграммах». Единая палитра диаграмм кассы и
/// программы владельца (та же, что в мобилке: tokens.dart, T.chartColors). 12 цветов по кругу, порядок — от самых несхожих:
/// первые 5–6 различимы даже рядом. Светлота подобрана так, что каждый цвет держит контраст не ниже 3:1 (AA для графики)
/// и на белом, и на тёмном фоне (#191919 и панель #2A2A2A); чёрный или белый текст поверх цвета (<see cref="OnColorIsDark"/>)
/// — не ниже 4,5:1. Различимы при дальтонизме (наименьшая разница пар ΔE2000: 12 в обычном зрении, 6 при протанопии,
/// дейтеранопии и тританопии), красный и зелёный рядом не стоят. Золотой — тёмный брендовый жёлтый (чистый #FFB208 на
/// белом — 1,8:1; он оставлен для акцента «лучший день»).
///
/// Цвет категории (товар, способ оплаты, филиал, сотрудник…) берётся по хэшу ключа, а не по порядку в списке: при смене
/// сортировки и обновлении данных цвета не «прыгают», а тот же товар в кассе и в телефоне — того же цвета.</summary>
public static class ChartPalette
{
    public static readonly string[] Hex =
    {
        "#B8860B", // золотой
        "#5470E1", // синий
        "#EE3083", // розовый
        "#3E9157", // зелёный
        "#9F81DA", // фиолетовый
        "#D16B60", // коралловый (красный)
        "#C740B1", // пурпурный
        "#78859A", // серый
        "#6B8137", // оливковый
        "#C95523", // оранжевый
        "#0DA38A", // бирюзовый
        "#1F80B1", // голубой
    };

    /// <summary>FNV-1a 32 бит по кодам UTF-16 строки в нижнем регистре без пробелов по краям (в мобилке — тот же алгоритм).</summary>
    public static uint HashOf(string? key)
    {
        var h = 0x811C9DC5u;
        foreach (var unit in (key ?? "").Trim().ToLowerInvariant())
            h = unchecked((h ^ unit) * 0x01000193u);
        return h;
    }

    public static int SlotOf(string? key) => (int)(HashOf(key) % (uint)Hex.Length);

    public static string ColorFor(string? key) => Hex[SlotOf(key)];

    /// <summary>Цвета для набора ключей одной диаграммы: у каждого свой «любимый» цвет по хэшу; если двое попали в один, второй
    /// (по порядку хэша, не показа) берёт следующий свободный. Результат не зависит от порядка строк. Ключей больше цветов —
    /// палитра идёт по кругу.</summary>
    public static string[] Assign(IReadOnlyList<string?> keys)
    {
        var n = Hex.Length;
        var order = Enumerable.Range(0, keys.Count)
            .OrderBy(i => HashOf(keys[i]))
            .ThenBy(i => keys[i] ?? "", StringComparer.Ordinal)
            .ToList();
        var slotByKey = new Dictionary<string, int>();
        var used = new HashSet<int>();
        var result = new string[keys.Count];
        foreach (var i in order)
        {
            var norm = (keys[i] ?? "").Trim().ToLowerInvariant();
            if (!slotByKey.TryGetValue(norm, out var slot))
            {
                if (used.Count >= n) used.Clear();
                slot = SlotOf(keys[i]);
                while (used.Contains(slot)) slot = (slot + 1) % n;
                used.Add(slot);
                slotByKey[norm] = slot;
            }
            result[i] = Hex[slot];
        }
        return result;
    }

    /// <summary>Относительная яркость цвета (WCAG).</summary>
    public static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Lin(int from)
        {
            var v = int.Parse(h.Substring(from, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(0) + 0.7152 * Lin(2) + 0.0722 * Lin(4);
    }

    /// <summary>Контраст двух цветов по WCAG (1…21).</summary>
    public static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Чёрный текст (true) или белый (false) — что контрастнее на этом цвете (порог 0,179: у любого цвета палитры не ниже 4,5:1).</summary>
    public static bool OnColorIsDark(string hex) => Luminance(hex) > 0.179;

    /// <summary>Ключ способа оплаты для цвета: один способ — один цвет на всех экранах, на каком бы языке ни была подпись
    /// (карта и перевод в кассе показываются одной строкой «Безналичные»).</summary>
    public static string PaymentKey(string? method)
    {
        var key = (method ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "card" or "transfer" => "cashless",
            _ when key.Contains("нал") => "cash",
            _ => key,
        };
    }

    /// <summary>ВРЕМЕННОЙ ряд (выручка по дням/часам/месяцам): столбцы одного оттенка, лучший день — акцентом (жёлтый), самый слабый —
    /// приглушённо. Выделения только когда есть что сравнивать: лучший — от двух периодов с продажами (значения не все равны),
    /// слабый — от трёх. Нулевые периоды не считаются (они и так пустые). Как RevenueDayBars.marks в мобилке.</summary>
    public static (HashSet<int> Best, HashSet<int> Worst) SeriesMarks(IReadOnlyList<double> values)
    {
        var sold = Enumerable.Range(0, values.Count).Where(i => values[i] > 0).ToList();
        var none = (new HashSet<int>(), new HashSet<int>());
        if (sold.Count < 2) return none;
        var hi = sold.Max(i => values[i]);
        var lo = sold.Min(i => values[i]);
        if (hi == lo) return none;
        return (sold.Where(i => values[i] == hi).ToHashSet(),
            sold.Count >= 3 ? sold.Where(i => values[i] == lo).ToHashSet() : new HashSet<int>());
    }
}
