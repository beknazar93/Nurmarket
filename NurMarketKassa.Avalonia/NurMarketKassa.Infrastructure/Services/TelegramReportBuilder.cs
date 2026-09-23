using System.Globalization;
using System.Linq;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>
/// Тексты отчётов, которые бот присылает владельцу: выручка, топ товаров, что заканчивается,
/// что пора заказать.
///
/// Считаются ЛОКАЛЬНО, по данным самой кассы (<see cref="SoldLineItemsStore"/>,
/// <see cref="CatalogCacheService"/>, <see cref="ClientLoyaltyStore"/>), а не запросом к серверу.
/// Причин две: отчёт приходит мгновенно и не зависит от связи, и владелец получает ровно те
/// цифры, которые видит в кассе на экране «Отчёты», — расхождений между двумя источниками не
/// возникает. Долги — единственное исключение: они живут только на сервере
/// (см. TelegramBotPollingService).
/// </summary>
public static class TelegramReportBuilder
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string Money(double value) => value.ToString("N2", Ru) + " сом";

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>Выручка за период. Считается по проданным строкам, из суммы вычитаются скидки
    /// (включая оплату бонусами) — то есть это деньги, реально полученные от покупателей, а не
    /// прайсовая стоимость проданного.</summary>
    public static string BuildRevenue(int days, string title)
    {
        // Сутки считаем по МЕСТНОМУ времени, а не по UTC. Бишкек — UTC+6, и «сегодня» по UTC
        // начиналось бы в 6 утра по-местному: утренние продажи попадали бы во «вчера».
        var sinceUtc = DateTime.Today.AddDays(-(days - 1)).ToUniversalTime();
        var lines = SoldLineItemsStore.LoadWithPriceSince(sinceUtc);

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Escape(title)}</b>");

        if (lines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Продаж за этот период нет.");
            return sb.ToString();
        }

        // Цена берётся ИЗ САМОЙ ПРОДАЖИ, а не из каталога. Поштучная продажа из пачки уходит по
        // своей цене (390 шт по 12 сом = 4 680), а в каталоге у того же товара стоит цена пачки:
        // по каталогу та же строка дала бы больше 68 000, и выручка за день завышалась в разы.
        var gross = lines.Sum(l => l.Quantity * l.UnitPrice);

        var adjustments = ClientLoyaltyStore.AdjustmentsBetween(sinceUtc, DateTime.UtcNow.AddDays(1));
        var net = Math.Max(0, gross - adjustments.Discounts);

        sb.AppendLine();
        sb.AppendLine($"<b>Выручка: {Money(net)}</b>");
        sb.AppendLine($"Чеков с товарами: {lines.Select(l => l.SoldAt).Distinct().Count()}");
        sb.AppendLine($"Позиций продано: {lines.Sum(l => l.Quantity):0.##}");
        if (adjustments.Discounts > 0.005)
        {
            sb.AppendLine($"Скидки: {Money(adjustments.Discounts)}");
            if (adjustments.PointsRedeemed > 0.005)
                sb.AppendLine($"Оплачено бонусами: {Money(adjustments.PointsRedeemed)}");
        }

        return sb.ToString();
    }

    /// <summary>Топ проданных товаров за период — по количеству.</summary>
    public static string BuildTopProducts(int days, int take = 10)
    {
        // Границы суток — по местному времени, как и в BuildRevenue.
        var sinceUtc = DateTime.Today.AddDays(-(days - 1)).ToUniversalTime();
        var lines = SoldLineItemsStore.LoadSince(sinceUtc);

        var sb = new StringBuilder();
        sb.AppendLine($"<b>Топ товаров за {days} дн.</b>");

        if (lines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Продаж за этот период нет.");
            return sb.ToString();
        }

        var top = lines
            .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.First().ProductName, Quantity = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.Quantity)
            .Take(take)
            .ToList();

        sb.AppendLine();
        var position = 1;
        foreach (var item in top)
            sb.AppendLine($"{position++}. {Escape(item.Name)} — {item.Quantity:0.##}");

        return sb.ToString();
    }

    /// <summary>Что пора заказать: на сколько дней хватит остатка при нынешней скорости продаж.
    /// Та же арифметика, что в окне «Рекомендации по закупке» (RestockSuggestionsWindow) —
    /// владелец в Telegram и кассир на экране видят одни и те же числа.</summary>
    public static string BuildRestockSuggestions(int lookbackDays = 30, int take = 12)
    {
        var sinceUtc = DateTime.UtcNow.AddDays(-lookbackDays);
        var lines = SoldLineItemsStore.LoadSince(sinceUtc);

        var sb = new StringBuilder();
        sb.AppendLine("<b>Пора заказать</b>");

        if (lines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine($"За последние {lookbackDays} дн. продаж не было — рекомендовать нечего.");
            return sb.ToString();
        }

        // Скорость продаж считаем не за весь запрошенный период, а с ПЕРВОЙ известной продажи:
        // если касса работает три дня, делить на 30 нельзя — расход получится втрое заниженным.
        var earliest = lines.Min(l => l.SoldAt);
        var spanDays = Math.Max(1.0, (DateTime.UtcNow - earliest).TotalDays);

        var sold = lines
            .GroupBy(l => l.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);

        var rows = new List<(string Name, double Stock, double DailyRate, double DaysLeft)>();
        foreach (var product in CatalogCacheService.Products)
        {
            if (!sold.TryGetValue(product.Id, out var quantity) || quantity <= 0)
                continue;

            var dailyRate = quantity / spanDays;
            if (dailyRate <= 0)
                continue;

            rows.Add((product.Title, product.Quantity, dailyRate, product.Quantity / dailyRate));
        }

        var urgent = rows.OrderBy(r => r.DaysLeft).Take(take).ToList();
        if (urgent.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Ничего срочного: остатков хватает.");
            return sb.ToString();
        }

        sb.AppendLine();
        foreach (var row in urgent)
        {
            var mark = row.DaysLeft <= 2 ? "\U0001F534" : row.DaysLeft <= 7 ? "\U0001F7E1" : "\U0001F7E2";
            sb.AppendLine(
                $"{mark} {Escape(row.Name)} — остаток {row.Stock:0.##}, хватит на {row.DaysLeft:0.#} дн. " +
                $"(в день {row.DailyRate:0.##})");
        }

        sb.AppendLine();
        sb.AppendLine("<i>Расход посчитан по продажам этой кассы.</i>");
        return sb.ToString();
    }

    /// <summary>Товары с нулевым или почти нулевым остатком — без привязки к скорости продаж.</summary>
    public static string BuildLowStock(double threshold = 3, int take = 20)
    {
        var low = CatalogCacheService.Products
            .Where(p => p.Quantity <= threshold)
            .OrderBy(p => p.Quantity)
            .Take(take)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<b>Заканчивается на складе</b>");
        sb.AppendLine();

        if (low.Count == 0)
        {
            sb.AppendLine($"Товаров с остатком ниже {threshold:0.##} нет.");
            return sb.ToString();
        }

        foreach (var product in low)
            sb.AppendLine($"• {Escape(product.Title)} — {product.Quantity:0.##}");

        return sb.ToString();
    }

    /// <summary>Предел сообщения в Telegram — 4096 символов. Отчёты строятся по живым данным
    /// магазина, и на большом ассортименте список легко уходит за предел: сообщение тогда не
    /// отправляется вовсе. Поэтому режем сами и честно говорим, что показано не всё.</summary>
    private const int TelegramLimit = 3900;

    private static string Trim(StringBuilder builder)
    {
        var text = builder.ToString();
        if (text.Length <= TelegramLimit)
            return text;

        var cut = text.LastIndexOf('\n', TelegramLimit - 1);
        if (cut < 0)
            cut = TelegramLimit - 1;

        return text[..cut] + "\n\n<i>…список обрезан, целиком — в кассе, раздел «ABC-анализ».</i>";
    }

    /// <summary>ABC-анализ за период. Присылается сводка по группам во всех срезах сразу —
    /// именно сравнение срезов и полезно: товар из группы A по выручке часто оказывается в C
    /// по прибыли, и по одной выручке такой товар не разглядеть.</summary>
    public static string BuildAbc(int days = 30)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));
        var data = AnalyticsReportData.Build(from, to, includeSeasonality: false);

        if (data.AbcSlices.Count == 0 || data.AbcSlices[0].Rows.Count == 0)
            return $"<b>ABC-анализ за {days} дн.</b>\n\nПродаж за период нет — считать не на чем.";

        var text = new StringBuilder();
        text.AppendLine($"<b>ABC-анализ за {days} дн.</b>");
        text.AppendLine("<i>A — первые 80 % результата, B — следующие 15 %, C — остальные 5 %.</i>");

        foreach (var slice in data.AbcSlices)
        {
            if (slice.Rows.Count == 0)
                continue;

            text.AppendLine();
            text.AppendLine($"<b>{Escape(slice.Title)}</b>");
            foreach (var group in slice.Summary)
            {
                var amount = slice.Unit == "шт."
                    ? group.Sum.ToString("N0", Ru) + " шт."
                    : Money(group.Sum);
                text.AppendLine($"{group.Group}: {group.Count} поз. — {amount} ({group.Share:0.#} %)");
            }
        }

        // Группа A по выручке — то, что нельзя допускать до пустых полок.
        var topSlice = data.AbcSlices[0];
        var groupA = topSlice.Rows.Where(r => r.Group == "A").Take(10).ToList();
        if (groupA.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("<b>Группа A по выручке</b>");
            foreach (var row in groupA)
                text.AppendLine($"• {Escape(row.Name)} — {Money(row.Sum)} ({row.Share:0.#} %)");
        }

        return Trim(text);
    }

    /// <summary>Сезонность: какие товары продаются в одни месяцы и не продаются в другие.
    /// Считается по ВСЕЙ истории кассы, а не за период: за один месяц сезонность не видна.</summary>
    public static string BuildSeasonality()
    {
        var report = AnalyticsReportData.BuildSeasonality();
        var text = new StringBuilder();
        text.AppendLine("<b>Сезонность товаров</b>");

        if (report.Rows.Count == 0)
            return text.AppendLine().Append("Продаж в истории кассы пока нет.").ToString();

        if (!report.Reliable)
        {
            // Без этой оговорки отчёт вреден: на короткой истории сезонным выглядит весь
            // ассортимент просто потому, что раньше касса не работала.
            text.AppendLine();
            text.AppendLine($"История: {report.FirstSale:dd.MM.yyyy} — {report.LastSale:dd.MM.yyyy}, "
                + $"продажи есть в {report.CoveredMonths} мес.");
            text.AppendLine();
            text.AppendLine("Выводов о сезонности пока нет: нужна история хотя бы за два сезона. "
                + "Иначе любой товар выглядит сезонным потому, что в другие месяцы касса ещё не работала. "
                + "Отчёт заполнится сам, как накопится история.");
            return text.ToString();
        }

        text.AppendLine($"<i>История: {report.FirstSale:dd.MM.yyyy} — {report.LastSale:dd.MM.yyyy}, "
            + $"{report.CoveredMonths} мес. с продажами.</i>");

        var seasonal = report.Rows.Where(r => r.Kind == "Сезонный").ToList();
        if (seasonal.Count == 0)
        {
            text.AppendLine();
            text.AppendLine("Сезонных товаров не нашлось — продажи распределены по месяцам ровно.");
            return text.ToString();
        }

        foreach (var season in new[] { "лето", "осень", "зима", "весна" })
        {
            var items = seasonal.Where(r => r.PeakSeason == season).Take(8).ToList();
            if (items.Count == 0)
                continue;

            text.AppendLine();
            text.AppendLine($"<b>{char.ToUpper(season[0], Ru)}{season[1..]}</b> — {items.Count} товар(ов)");
            foreach (var item in items)
                text.AppendLine($"• {Escape(item.Name)} — пик: {Escape(item.PeakMonths)}");
        }

        return Trim(text);
    }

    /// <summary>Рекомендации: то, ради чего владелец вообще смотрит аналитику.
    ///
    /// Собирается из пересечений, которых не видно ни на одном отдельном экране: товар группы A,
    /// который вот-вот кончится; товар, который делает выручку и не делает прибыли; товар,
    /// который лежит на складе и не продаётся вовсе.</summary>
    public static string BuildRecommendations(int days = 30)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));
        var data = AnalyticsReportData.Build(from, to, includeSeasonality: false);

        var text = new StringBuilder();
        text.AppendLine($"<b>Рекомендации за {days} дн.</b>");

        if (data.AbcSlices.Count == 0 || data.AbcSlices[0].Rows.Count == 0)
            return text.AppendLine().Append("Продаж за период нет — советовать нечего.").ToString();

        var byRevenue = data.AbcSlices[0];
        var groupA = byRevenue.Rows.Where(r => r.Group == "A")
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Главное: товар группы A, которого осталось меньше чем на неделю. Пустая полка
        // по такому товару бьёт по выручке сильнее всего остального вместе взятого.
        var urgent = data.Restock
            .Where(r => groupA.Contains(r.Name) && r.DaysLeft <= 7)
            .OrderBy(r => r.DaysLeft)
            .Take(10)
            .ToList();
        if (urgent.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("<b>Срочно заказать — группа A на исходе</b>");
            foreach (var row in urgent)
                text.AppendLine($"• {Escape(row.Name)} — осталось на {row.DaysLeft:0.#} дн. ({row.Stock:0.##})");
        }

        // 2. Делает выручку, но не делает прибыли: в A по деньгам и в C по прибыли.
        // По ключу, а не по названию: названия переводятся, и на кыргызском поиск по слову
        // «прибыли» ничего бы не нашёл.
        var profitSlice = data.AbcSlices.FirstOrDefault(x => x.Key == "profit");
        if (profitSlice is { Rows.Count: > 0 })
        {
            var weakProfit = profitSlice.Rows
                .Where(r => r.Group == "C" && groupA.Contains(r.Name))
                .Take(8)
                .ToList();
            if (weakProfit.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("<b>Оборот есть, прибыли нет</b>");
                text.AppendLine("<i>В группе A по выручке и в C по прибыли — проверьте наценку.</i>");
                foreach (var row in weakProfit)
                    text.AppendLine($"• {Escape(row.Name)} — прибыль {Money(row.Sum)}");
            }
        }

        // 3. Лежит на складе и не продаётся: деньги, замороженные в товаре.
        var sold = byRevenue.Rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dead = CatalogCacheService.Products
            .Where(x => x.Quantity > 0 && !sold.Contains(x.Title))
            .Select(x => (x.Title, Value: x.Quantity * LocalCartService.ParsePrice(x.PriceLine)))
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Take(8)
            .ToList();
        if (dead.Count > 0)
        {
            var frozen = dead.Sum(x => x.Value);
            text.AppendLine();
            text.AppendLine($"<b>Не продавалось за период</b> — заморожено {Money(frozen)}");
            foreach (var item in dead)
                text.AppendLine($"• {Escape(item.Title)} — {Money(item.Value)}");
        }

        // 4. Длинный хвост: сколько позиций держат всего 5 % выручки.
        var tail = byRevenue.Rows.Count(r => r.Group == "C");
        if (tail > 0)
        {
            var share = tail * 100.0 / byRevenue.Rows.Count;
            text.AppendLine();
            text.AppendLine($"<b>Длинный хвост</b>: {tail} позиц. из {byRevenue.Rows.Count} "
                + $"({share:0.#} % ассортимента) дают последние 5 % выручки. "
                + "По ним запас можно сокращать смелее.");
        }

        if (text.Length < 60)
            text.AppendLine().Append("Явных проблем не видно — запасы и наценка в порядке.");

        return Trim(text);
    }

    public static string BuildHelp() =>
        """
        <b>Что умеет бот</b>

        /segodnya — выручка за сегодня
        /nedelya — выручка за 7 дней
        /top — топ продаваемых товаров
        /abc — ABC-анализ: где деньги магазина
        /sezon — сезонность: что продаётся не круглый год
        /soveti — рекомендации: что заказать, где нет наценки, что лежит мёртвым грузом
        /zakaz — что пора заказать
        /ostatki — что заканчивается
        /dolgi — должники и ссылки для напоминания
        /help — эта справка

        <i>Бот отвечает, пока касса включена: она сама спрашивает Telegram о новых сообщениях,
        своего сервера у неё нет.</i>
        """;
}
