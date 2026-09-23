using System.Globalization;
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

    public static string BuildHelp() =>
        """
        <b>Что умеет бот</b>

        /segodnya — выручка за сегодня
        /nedelya — выручка за 7 дней
        /top — топ продаваемых товаров
        /zakaz — что пора заказать
        /ostatki — что заканчивается
        /dolgi — должники и ссылки для напоминания
        /help — эта справка

        <i>Бот отвечает, пока касса включена: она сама спрашивает Telegram о новых сообщениях,
        своего сервера у неё нет.</i>
        """;
}
