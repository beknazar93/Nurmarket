namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 6): XYZ к ABC. Метод «важность × предсказуемость»: ABC — по выручке (A — первые 80 %, B — следующие 15 %, C — остальное,
// граница — по доле ДО позиции, как в AnalyticsReportData); XYZ — по коэффициенту вариации недельных продаж (X — спрос ровный, Y — колеблется, Z — случайный).
// Идея из открытых методик (importance × forecastability), код свой. Матрица 3×3: AX… CZ.

public sealed record AbcXyzItem(string Key, string Name, double Revenue, double Quantity, string Abc, string? Xyz, double? Cv, int Weeks)
{
    /// <summary>«AX», «BZ»… или «A?» — мало истории (меньше 4 недель).</summary>
    public string Cell => Abc + (Xyz ?? "?");
}

public sealed record AbcXyzResult(IReadOnlyList<AbcXyzItem> Items, IReadOnlyDictionary<string, int> CellCounts, IReadOnlyDictionary<string, double> CellRevenue, int WeeksUsed)
{
    public int Count(string cell) => CellCounts.GetValueOrDefault(cell);
    public double Revenue(string cell) => CellRevenue.GetValueOrDefault(cell);
}

public static class AbcXyz
{
    /// <summary>X: коэффициент вариации не больше 25 %; Y: не больше 50 %; Z: выше (из отчёта: «X — до 10–25 %, Y — до 50 %»).</summary>
    public const double XLimit = 0.25;
    public const double YLimit = 0.50;
    /// <summary>Меньше недель истории — класс XYZ не определяем (показываем «?»).</summary>
    public const int MinWeeks = 4;

    public static string XyzClass(double cv) => cv <= XLimit ? "X" : cv <= YLimit ? "Y" : "Z";

    /// <summary>Строки продаж (ключ, название, штук, сумма, когда по местному времени) за последние <paramref name="weeks"/> недель (по умолчанию 12).
    /// Недели считаются назад от <paramref name="todayLocal"/>: неделя 0 — последние 7 дней. У товара, который начали продавать позже, считаются только
    /// недели с его первой продажи (иначе новинка получила бы «Z» из-за нулей до появления).</summary>
    public static AbcXyzResult Compute(IEnumerable<(string Key, string Name, double Qty, double Sum, DateTime SoldAtLocal)> lines, DateTime todayLocal, int weeks = 12)
    {
        weeks = Math.Clamp(weeks, MinWeeks, 52);
        var today = todayLocal.Date;
        var from = today.AddDays(-(weeks * 7 - 1));
        var perKey = new Dictionary<string, (string Name, double Qty, double Sum, double[] Weekly, DateTime First)>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            var day = l.SoldAtLocal.Date;
            if (day < from || day > today || string.IsNullOrWhiteSpace(l.Key))
                continue;
            var w = Math.Min(weeks - 1, (today - day).Days / 7);
            if (!perKey.TryGetValue(l.Key, out var e))
                e = (l.Name, 0, 0, new double[weeks], day);
            e.Weekly[w] += l.Qty;
            e = (e.Name.Length > 0 ? e.Name : l.Name, e.Qty + l.Qty, e.Sum + l.Sum, e.Weekly, day < e.First ? day : e.First);
            perKey[l.Key] = e;
        }

        var ranked = perKey.Where(p => p.Value.Sum > 0).OrderByDescending(p => p.Value.Sum).ToList();
        var total = ranked.Sum(p => p.Value.Sum);
        var items = new List<AbcXyzItem>(ranked.Count);
        var running = 0.0;
        foreach (var (key, e) in ranked)
        {
            var before = running;
            running += e.Sum / total * 100;
            var abc = before < 80.0 ? "A" : before < 95.0 ? "B" : "C";
            // Недели товара: от недели первой продажи до сегодняшней (включительно).
            var active = Math.Min(weeks, (today - e.First).Days / 7 + 1);
            string? xyz = null;
            double? cv = null;
            if (active >= MinWeeks)
            {
                var series = e.Weekly.Take(active).ToArray();
                var mean = series.Average();
                if (mean > 0)
                {
                    var variance = series.Sum(x => (x - mean) * (x - mean)) / series.Length;
                    cv = Math.Sqrt(variance) / mean;
                    xyz = XyzClass(cv.Value);
                }
            }
            items.Add(new AbcXyzItem(key, e.Name, e.Sum, e.Qty, abc, xyz, cv, active));
        }

        var counts = new Dictionary<string, int>();
        var revenue = new Dictionary<string, double>();
        foreach (var it in items)
        {
            counts[it.Cell] = counts.GetValueOrDefault(it.Cell) + 1;
            revenue[it.Cell] = revenue.GetValueOrDefault(it.Cell) + it.Revenue;
        }
        return new AbcXyzResult(items, counts, revenue, weeks);
    }

    /// <summary>Во сколько раз менять страховой запас автозаказа. Важные и ровные (AX, BX) — запас выше, «хвост» CZ — без запаса (под заказ).</summary>
    public static double SafetyMultiplier(string? cell) => cell switch
    {
        "AX" => 1.5,
        "AY" => 1.25,
        "AZ" => 1.0,
        "BX" => 1.25,
        "BY" => 1.0,
        "BZ" => 0.75,
        "CX" => 0.75,
        "CY" => 0.5,
        "CZ" => 0,
        _ => 1.0,
    };

    /// <summary>Подсказка к клетке матрицы — простыми словами.</summary>
    public static string Hint(string cell) => cell switch
    {
        "AX" => Tr.T("Главные и ровные: всегда в наличии, запас с запасом.", "Негизги жана туруктуу: дайыма болсун, запасы чоң.", "Key and steady: always in stock, with extra safety.", "Ana ve düzenli: hep stokta, ek güvenlik stoğuyla.", "Asosiy va barqaror: doim omborda, zaxira bilan."),
        "AY" => Tr.T("Главные, спрос колеблется: держать с запасом, следить за сезоном.", "Негизги, суроо термелет: запас менен кармаңыз, сезонду карап туруңуз.", "Key, demand varies: keep a buffer, watch the season.", "Ana, talep dalgalı: tampon tutun, sezona bakın.", "Asosiy, talab tebranadi: zaxira tuting, mavsumga qarang."),
        "AZ" => Tr.T("Главные, но спрос случайный: заказывать чаще и понемногу.", "Негизги, бирок суроо кокус: көп жана аз-аздан буйрутма бериңиз.", "Key but random demand: order often, in small amounts.", "Ana ama talep rastgele: sık ve az sipariş verin.", "Asosiy, lekin talab tasodifiy: tez-tez va oz-ozdan buyurtma bering."),
        "BX" => Tr.T("Средние и ровные: заказ по расчёту, запас небольшой.", "Орто жана туруктуу: эсеп боюнча буйрутма, запас аз.", "Mid and steady: order by calculation, small buffer.", "Orta ve düzenli: hesaba göre sipariş, küçük tampon.", "O'rta va barqaror: hisob bo'yicha buyurtma, zaxira oz."),
        "BY" => Tr.T("Средние: обычный автозаказ.", "Орто: кадимки автобуйрутма.", "Mid: regular auto-order.", "Orta: normal otomatik sipariş.", "O'rta: oddiy avtobuyurtma."),
        "BZ" => Tr.T("Средние, спрос случайный: запас меньше, смотреть вручную.", "Орто, суроо кокус: запас аз, колдон караңыз.", "Mid, random demand: smaller buffer, check by hand.", "Orta, talep rastgele: daha az tampon, elle bakın.", "O'rta, talab tasodifiy: zaxira kam, qo'lda tekshiring."),
        "CX" => Tr.T("Мелкие, но ровные: заказывать редко, запас небольшой.", "Майда, бирок туруктуу: сейрек буйрутма, запас аз.", "Small but steady: order rarely, small buffer.", "Küçük ama düzenli: seyrek sipariş, az tampon.", "Mayda, lekin barqaror: kam buyurtma, zaxira oz."),
        "CY" => Tr.T("Мелкие: минимальный запас.", "Майда: минималдуу запас.", "Small: minimal buffer.", "Küçük: asgari tampon.", "Mayda: minimal zaxira."),
        "CZ" => Tr.T("Мелкие и случайные: только под заказ или вывести из ассортимента.", "Майда жана кокус: буйрутма боюнча гана же ассортименттен алып салыңыз.", "Small and random: on demand only, or drop from the range.", "Küçük ve rastgele: yalnızca siparişle veya çıkarın.", "Mayda va tasodifiy: faqat buyurtma bilan yoki assortimentdan chiqaring."),
        _ => Tr.T("Мало истории (меньше 4 недель) — класс спроса пока не определён.", "Тарых аз (4 жумадан кем) — суроо классы али аныкталган жок.", "Too little history (under 4 weeks) — demand class not set yet.", "Geçmiş az (4 haftadan az) — talep sınıfı henüz belirlenmedi.", "Tarix kam (4 haftadan kam) — talab sinfi hali aniqlanmadi."),
    };
}
