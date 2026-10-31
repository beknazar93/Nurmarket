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
///
/// 2026-10-21 (бот, К2 отчёта 20.10): ВЫРУЧКА — исключение из правила выше. Местная история — только продажи этой кассы, без
/// возвратов и других касс (живой случай 05.10: 1 353 572 сом за 7 дней против 886 049 на сервере). Теперь /segodnya, /nedelya,
/// /mesyac берут цифры у сервера тем же расчётом, что «Сводка» программы владельца (OwnerPeriodStats, подставляет приложение:
/// <see cref="ServerRevenueProvider"/>); местная история — только запасной вариант, с честной пометкой.
/// 2026-10-21 (бот, идея 10): тексты — на языке программы владельца (TelegramBotText.T).
/// </summary>
public static class TelegramReportBuilder
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string Money(double value) => value.ToString("N2", Ru) + " " + TelegramBotText.T("сом", "сом", "som", "som", "so'm");

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>Итоги периода с сервера NurCRM — как карточки «Сводки» программы владельца.</summary>
    public sealed record ServerRevenue(double Revenue, double Checks, double AvgCheck, double Profit, string? Note);

    /// <summary>Выручка периода с сервера: "today" | "week" (7 дней с сегодняшним, как «Сводка») | "month" (с 1-го числа).
    /// Подставляет приложение (OwnerPeriodStats — тот же расчёт, что «Сводка» и Нур Советник). null — нет или сервер не ответил.</summary>
    public static Func<string, CancellationToken, Task<ServerRevenue?>>? ServerRevenueProvider { get; set; }

    /// <summary>Сравнение периодов с сервера одним текстом для ИИ бота (OwnerPeriodStats.Snapshot.ToAiText). null — нет.</summary>
    public static Func<CancellationToken, Task<string?>>? ServerRevenueAiTextProvider { get; set; }

    /// <summary>Текст выручки с сервера для сводки ИИ бота; null — сервер не ответил (тогда в сводке местные цифры с пометкой).</summary>
    public static async Task<string?> ServerRevenueAiTextAsync(CancellationToken ct)
    {
        if (ServerRevenueAiTextProvider is not { } provider)
            return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await provider(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"ИИ бота: выручка с сервера не получена ({ex.GetType().Name}) — в сводке местные цифры.", "TELEGRAM");
            return null;
        }
    }

    private static string PeriodTitle(string period) => period switch
    {
        "today" => TelegramBotText.T($"Сегодня, {DateTime.Today:dd.MM}", $"Бүгүн, {DateTime.Today:dd.MM}", $"Today, {DateTime.Today:dd.MM}", $"Bugün, {DateTime.Today:dd.MM}", $"Bugun, {DateTime.Today:dd.MM}"),
        "month" => TelegramBotText.T($"С начала месяца ({DateTime.Today:01.MM}–{DateTime.Today:dd.MM})", $"Айдын башынан ({DateTime.Today:01.MM}–{DateTime.Today:dd.MM})",
            $"Month to date ({DateTime.Today:01.MM}–{DateTime.Today:dd.MM})", $"Ay başından beri ({DateTime.Today:01.MM}–{DateTime.Today:dd.MM})", $"Oy boshidan ({DateTime.Today:01.MM}–{DateTime.Today:dd.MM})"),
        _ => TelegramBotText.T($"За 7 дней ({DateTime.Today.AddDays(-6):dd.MM}–{DateTime.Today:dd.MM})", $"7 күндө ({DateTime.Today.AddDays(-6):dd.MM}–{DateTime.Today:dd.MM})",
            $"Last 7 days ({DateTime.Today.AddDays(-6):dd.MM}–{DateTime.Today:dd.MM})", $"Son 7 gün ({DateTime.Today.AddDays(-6):dd.MM}–{DateTime.Today:dd.MM})", $"7 kunda ({DateTime.Today.AddDays(-6):dd.MM}–{DateTime.Today:dd.MM})"),
    };

    /// <summary>Выручка периода для бота: сначала сервер (как «Сводка»), иначе местная история с пометкой.</summary>
    public static async Task<string> BuildRevenueAsync(string period, CancellationToken ct)
    {
        if (ServerRevenueProvider is { } provider)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (await provider(period, timeout.Token).ConfigureAwait(false) is { } r)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"<b>{Escape(PeriodTitle(period))}</b>");
                    sb.AppendLine();
                    sb.AppendLine("<b>" + TelegramBotText.T("Выручка: ", "Түшүм: ", "Revenue: ", "Ciro: ", "Tushum: ") + Money(r.Revenue) + "</b>");
                    sb.AppendLine(TelegramBotText.T("Чеков: ", "Чектер: ", "Receipts: ", "Fiş: ", "Cheklar: ") + r.Checks.ToString("0", Ru));
                    if (r.Checks > 0)
                        sb.AppendLine(TelegramBotText.T("Средний чек: ", "Орточо чек: ", "Average receipt: ", "Ortalama fiş: ", "O'rtacha chek: ") + Money(r.AvgCheck));
                    if (Math.Abs(r.Profit) > 0.005)
                        sb.AppendLine(TelegramBotText.T("Валовая прибыль: ", "Дүң пайда: ", "Gross profit: ", "Brüt kâr: ", "Yalpi foyda: ") + Money(r.Profit));
                    if (!string.IsNullOrWhiteSpace(r.Note))
                        sb.AppendLine(Escape(r.Note));
                    sb.AppendLine();
                    sb.AppendLine(TelegramBotText.T("<i>По данным сервера NurCRM — как в «Сводке» программы владельца.</i>",
                        "<i>NurCRM серверинин маалыматы боюнча — ээсинин программасындагы «Жыйынтык» сыяктуу.</i>",
                        "<i>From the NurCRM server — same as “Summary” in the owner app.</i>",
                        "<i>NurCRM sunucusu verileri — sahip programındaki «Özet» ile aynı.</i>",
                        "<i>NurCRM serveri ma'lumotlari — ega dasturidagi «Xulosa» bilan bir xil.</i>"));
                    return sb.ToString();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                PosLogger.Log($"Телеграм-бот: выручка с сервера не получена ({ex.GetType().Name}) — показываю местную историю кассы.", "TELEGRAM");
            }
        }

        var days = period switch { "today" => 1, "month" => DateTime.Today.Day, _ => 7 };
        return BuildRevenue(days, PeriodTitle(period)) + "\n" + TelegramBotText.T(
            "<i>⚠ Сервер NurCRM не ответил — это продажи только этой кассы, без возвратов и других касс. Точные цифры — «Сводка» в программе владельца.</i>",
            "<i>⚠ NurCRM сервери жооп берген жок — бул ушул кассанын гана сатуулары, кайтаруусуз жана башка кассаларсыз. Так сандар — ээсинин программасындагы «Жыйынтык».</i>",
            "<i>⚠ The NurCRM server did not respond — these are sales of this till only, without returns and other tills. Exact figures — “Summary” in the owner app.</i>",
            "<i>⚠ NurCRM sunucusu yanıt vermedi — bunlar yalnızca bu kasanın satışları, iadeler ve diğer kasalar hariç. Kesin rakamlar — sahip programında «Özet».</i>",
            "<i>⚠ NurCRM serveri javob bermadi — bu faqat shu kassaning sotuvlari, qaytarishlar va boshqa kassalarsiz. Aniq raqamlar — ega dasturidagi «Xulosa».</i>");
    }

    /// <summary>Выручка за период. Считается по проданным строкам, из суммы вычитаются скидки
    /// (включая оплату бонусами) — то есть это деньги, реально полученные от покупателей, а не
    /// прайсовая стоимость проданного.
    /// 2026-10-21: только запасной путь бота и сводка ИИ без связи (см. BuildRevenueAsync) — с пометкой «продажи этой кассы».</summary>
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
            sb.AppendLine(TelegramBotText.T("Продаж этой кассы за этот период нет.", "Бул кассанын бул мезгилде сатуулары жок.", "No sales on this till for this period.",
                "Bu kasada bu dönemde satış yok.", "Bu kassada bu davrda sotuv yo'q."));
            return sb.ToString();
        }

        // Цена берётся ИЗ САМОЙ ПРОДАЖИ, а не из каталога. Поштучная продажа из пачки уходит по
        // своей цене (390 шт по 12 сом = 4 680), а в каталоге у того же товара стоит цена пачки:
        // по каталогу та же строка дала бы больше 68 000, и выручка за день завышалась в разы.
        var gross = lines.Sum(l => l.Quantity * l.UnitPrice);

        var adjustments = ClientLoyaltyStore.AdjustmentsBetween(sinceUtc, DateTime.UtcNow.AddDays(1));
        var net = Math.Max(0, gross - adjustments.Discounts);

        sb.AppendLine();
        sb.AppendLine("<b>" + TelegramBotText.T("Выручка этой кассы: ", "Бул кассанын түшүмү: ", "This till's revenue: ", "Bu kasanın cirosu: ", "Shu kassa tushumi: ") + Money(net) + "</b>");
        sb.AppendLine(TelegramBotText.T("Чеков с товарами: ", "Товарлуу чектер: ", "Receipts with items: ", "Ürünlü fiş: ", "Mahsulotli cheklar: ") + lines.Select(l => l.SoldAt).Distinct().Count());
        sb.AppendLine(TelegramBotText.T("Позиций продано: ", "Сатылган позициялар: ", "Items sold: ", "Satılan kalem: ", "Sotilgan pozitsiyalar: ") + lines.Sum(l => l.Quantity).ToString("0.###", Ru));
        if (adjustments.Discounts > 0.005)
        {
            sb.AppendLine(TelegramBotText.T("Скидки: ", "Арзандатуулар: ", "Discounts: ", "İndirimler: ", "Chegirmalar: ") + Money(adjustments.Discounts));
            if (adjustments.PointsRedeemed > 0.005)
                sb.AppendLine(TelegramBotText.T("Оплачено бонусами: ", "Бонус менен төлөндү: ", "Paid with bonuses: ", "Bonusla ödenen: ", "Bonus bilan to'langan: ") + Money(adjustments.PointsRedeemed));
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
        sb.AppendLine(TelegramBotText.T($"<b>Топ товаров за {days} дн.</b>", $"<b>{days} күндөгү эң көп сатылгандар</b>", $"<b>Top products, {days} days</b>",
            $"<b>{days} günün en çok satanları</b>", $"<b>{days} kunda eng ko'p sotilganlar</b>"));

        if (lines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(TelegramBotText.T("Продаж за этот период нет.", "Бул мезгилде сатуу жок.", "No sales for this period.", "Bu dönemde satış yok.", "Bu davrda sotuv yo'q."));
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
            sb.AppendLine($"{position++}. {Escape(item.Name)} — {item.Quantity:0.###}");

        sb.AppendLine();
        sb.AppendLine(TelegramBotText.T("<i>По продажам этой кассы.</i>", "<i>Бул кассанын сатуулары боюнча.</i>", "<i>Based on this till's sales.</i>",
            "<i>Bu kasanın satışlarına göre.</i>", "<i>Shu kassa sotuvlari bo'yicha.</i>"));
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
        sb.AppendLine(TelegramBotText.T("<b>Пора заказать</b>", "<b>Заказ кылуу убактысы</b>", "<b>Time to reorder</b>", "<b>Sipariş zamanı</b>", "<b>Buyurtma vaqti</b>"));

        if (lines.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(TelegramBotText.T($"За последние {lookbackDays} дн. продаж не было — рекомендовать нечего.", $"Акыркы {lookbackDays} күндө сатуу болгон жок — сунуштай турган эч нерсе жок.",
                $"No sales in the last {lookbackDays} days — nothing to recommend.", $"Son {lookbackDays} günde satış yok — önerilecek bir şey yok.", $"Oxirgi {lookbackDays} kunda sotuv bo'lmadi — tavsiya qiladigan narsa yo'q."));
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
        // 2026-10-21 (бот, S14): копия каталога, а не живой список (гонка с обновлением → NullReferenceException).
        foreach (var product in TelegramCatalog.Products)
        {
            if (string.IsNullOrWhiteSpace(product.Id) || !sold.TryGetValue(product.Id, out var quantity) || quantity <= 0)
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
            sb.AppendLine(TelegramBotText.T("Ничего срочного: остатков хватает.", "Шашылыш эч нерсе жок: калдыктар жетиштүү.", "Nothing urgent: stock is sufficient.",
                "Acil bir şey yok: stok yeterli.", "Shoshilinch narsa yo'q: qoldiq yetarli."));
            return sb.ToString();
        }

        sb.AppendLine();
        foreach (var row in urgent)
        {
            var mark = row.DaysLeft <= 2 ? "\U0001F534" : row.DaysLeft <= 7 ? "\U0001F7E1" : "\U0001F7E2";
            sb.AppendLine($"{mark} {Escape(row.Name)} — " + TelegramBotText.T(
                $"остаток {row.Stock:0.###}, хватит на {row.DaysLeft:0.#} дн. (в день {row.DailyRate:0.##})",
                $"калдык {row.Stock:0.###}, {row.DaysLeft:0.#} күнгө жетет (күнүнө {row.DailyRate:0.##})",
                $"stock {row.Stock:0.###}, lasts {row.DaysLeft:0.#} days ({row.DailyRate:0.##} per day)",
                $"stok {row.Stock:0.###}, {row.DaysLeft:0.#} gün yeter (günde {row.DailyRate:0.##})",
                $"qoldiq {row.Stock:0.###}, {row.DaysLeft:0.#} kunga yetadi (kuniga {row.DailyRate:0.##})"));
        }

        sb.AppendLine();
        sb.AppendLine(TelegramBotText.T("<i>Расход посчитан по продажам этой кассы.</i>", "<i>Чыгым бул кассанын сатуулары боюнча эсептелди.</i>",
            "<i>Consumption is based on this till's sales.</i>", "<i>Tüketim bu kasanın satışlarına göre hesaplandı.</i>", "<i>Sarf shu kassa sotuvlari bo'yicha hisoblandi.</i>"));
        return sb.ToString();
    }

    internal static string LowStockTitle() => TelegramBotText.T("<b>Заканчивается на складе</b>", "<b>Кампада түгөнүп жатат</b>", "<b>Running low in stock</b>",
        "<b>Stokta azalanlar</b>", "<b>Omborda tugayapti</b>");

    internal static string NoLowStockText(double threshold) => TelegramBotText.T($"Товаров с остатком ниже {threshold:0.##} нет.", $"Калдыгы {threshold:0.##} төмөн товар жок.",
        $"No products with stock below {threshold:0.##}.", $"Stoğu {threshold:0.##} altında ürün yok.", $"Qoldig'i {threshold:0.##} dan kam mahsulot yo'q.");

    /// <summary>Товары с нулевым или почти нулевым остатком — без привязки к скорости продаж.</summary>
    public static string BuildLowStock(double threshold = NurMarketKassa.Models.Pos.CatalogProductTileVm.LowStockQuantityThreshold, int take = 20)
    {
        var low = TelegramCatalog.Products
            .Where(p => p.Quantity < threshold)
            .OrderBy(p => p.Quantity)
            .Take(take)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(LowStockTitle());
        sb.AppendLine();

        if (low.Count == 0)
        {
            sb.AppendLine(NoLowStockText(threshold));
            return sb.ToString();
        }

        foreach (var product in low)
            sb.AppendLine($"• {Escape(product.Title)} — {product.Quantity:0.###}");

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

        return text[..cut] + TelegramBotText.T("\n\n<i>…список обрезан, целиком — в кассе, раздел «ABC-анализ».</i>", "\n\n<i>…тизме кыскартылды, толугу — кассада, «ABC-анализ» бөлүмүндө.</i>",
            "\n\n<i>…the list is shortened; the full one is in the till, “ABC analysis”.</i>", "\n\n<i>…liste kısaltıldı; tamamı kasada, «ABC analizi» bölümünde.</i>",
            "\n\n<i>…ro'yxat qisqartirildi, to'liq — kassada, «ABC tahlil» bo'limida.</i>");
    }

    /// <summary>ABC-анализ за период. Присылается сводка по группам во всех срезах сразу —
    /// именно сравнение срезов и полезно: товар из группы A по выручке часто оказывается в C
    /// по прибыли, и по одной выручке такой товар не разглядеть.</summary>
    public static string BuildAbc(int days = 30)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));
        var data = AnalyticsReportData.Build(from, to, includeSeasonality: false);
        var title = TelegramBotText.T($"<b>ABC-анализ за {days} дн.</b>", $"<b>{days} күндүк ABC-анализ</b>", $"<b>ABC analysis, {days} days</b>",
            $"<b>{days} günlük ABC analizi</b>", $"<b>{days} kunlik ABC tahlil</b>");

        // Срез выручки — по коду, а не первым в списке: срез склада (2026-09-27) есть и без
        // продаж, и тогда первым оказался бы он.
        var topSlice = data.Slice(AnalyticsReportData.KeyRevenue);
        if (topSlice is not { Rows.Count: > 0 })
            return title + "\n\n" + TelegramBotText.T("Продаж за период нет — считать не на чем.", "Бул мезгилде сатуу жок — эсептей турган эч нерсе жок.",
                "No sales for the period — nothing to calculate.", "Dönemde satış yok — hesaplanacak bir şey yok.", "Davrda sotuv yo'q — hisoblaydigan narsa yo'q.");

        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(TelegramBotText.T("<i>A — первые 80 % результата, B — следующие 15 %, C — остальные 5 %.</i>", "<i>A — натыйжанын алгачкы 80 %, B — кийинки 15 %, C — калган 5 %.</i>",
            "<i>A — the first 80 % of the result, B — the next 15 %, C — the remaining 5 %.</i>", "<i>A — sonucun ilk %80'i, B — sonraki %15, C — kalan %5.</i>",
            "<i>A — natijaning birinchi 80 %, B — keyingi 15 %, C — qolgan 5 %.</i>"));

        foreach (var slice in data.AbcSlices)
        {
            if (slice.Rows.Count == 0)
                continue;

            text.AppendLine();
            text.AppendLine($"<b>{Escape(slice.Title)}</b>");
            foreach (var group in slice.Summary)
            {
                var amount = slice.Unit == "шт."
                    ? group.Sum.ToString("N0", Ru) + " " + TelegramBotText.T("шт.", "даана", "pcs", "adet", "dona")
                    : Money(group.Sum);
                text.AppendLine($"{group.Group}: {group.Count} " + TelegramBotText.T("поз.", "поз.", "items", "kalem", "poz.") + $" — {amount} ({group.Share:0.#} %)");
            }
        }

        // Группа A по выручке — то, что нельзя допускать до пустых полок.
        var groupA = topSlice.Rows.Where(r => r.Group == "A").Take(10).ToList();
        if (groupA.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(TelegramBotText.T("<b>Группа A по выручке</b>", "<b>Түшүм боюнча A тобу</b>", "<b>Group A by revenue</b>", "<b>Ciroya göre A grubu</b>", "<b>Tushum bo'yicha A guruhi</b>"));
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
        text.AppendLine(TelegramBotText.T("<b>Сезонность товаров</b>", "<b>Товарлардын мезгилдүүлүгү</b>", "<b>Product seasonality</b>", "<b>Ürün mevsimselliği</b>", "<b>Mahsulotlar mavsumiyligi</b>"));

        if (report.Rows.Count == 0)
            return text.AppendLine().Append(TelegramBotText.T("Продаж в истории кассы пока нет.", "Кассанын тарыхында азырынча сатуу жок.", "No sales in the till history yet.",
                "Kasa geçmişinde henüz satış yok.", "Kassa tarixida hali sotuv yo'q.")).ToString();

        if (!report.Reliable)
        {
            // Без этой оговорки отчёт вреден: на короткой истории сезонным выглядит весь
            // ассортимент просто потому, что раньше касса не работала.
            text.AppendLine();
            text.AppendLine(TelegramBotText.T("История: ", "Тарых: ", "History: ", "Geçmiş: ", "Tarix: ") + $"{report.FirstSale:dd.MM.yyyy} — {report.LastSale:dd.MM.yyyy}, "
                + TelegramBotText.T($"продажи есть в {report.CoveredMonths} мес.", $"сатуулар {report.CoveredMonths} айда бар.", $"sales in {report.CoveredMonths} months.",
                    $"{report.CoveredMonths} ayda satış var.", $"{report.CoveredMonths} oyda sotuv bor."));
            text.AppendLine();
            text.AppendLine(TelegramBotText.T(
                "Выводов о сезонности пока нет: нужна история хотя бы за два сезона. Иначе любой товар выглядит сезонным потому, что в другие месяцы касса ещё не работала. Отчёт заполнится сам, как накопится история.",
                "Мезгилдүүлүк боюнча азырынча корутунду жок: жок дегенде эки мезгилдик тарых керек. Болбосо ар бир товар мезгилдүү көрүнөт, анткени башка айларда касса иштеген эмес. Тарых топтолгондо отчёт өзү толот.",
                "No seasonality conclusions yet: at least two seasons of history are needed. Otherwise every product looks seasonal because the till did not work in other months. The report will fill in by itself as history accumulates.",
                "Henüz mevsimsellik sonucu yok: en az iki sezonluk geçmiş gerekli. Aksi halde her ürün mevsimsel görünür, çünkü kasa diğer aylarda çalışmıyordu. Geçmiş biriktikçe rapor kendiliğinden dolacak.",
                "Mavsumiylik bo'yicha hali xulosa yo'q: kamida ikki mavsumlik tarix kerak. Aks holda har qanday mahsulot mavsumiy ko'rinadi, chunki boshqa oylarda kassa ishlamagan. Tarix to'planganda hisobot o'zi to'ladi."));
            return text.ToString();
        }

        text.AppendLine("<i>" + TelegramBotText.T("История: ", "Тарых: ", "History: ", "Geçmiş: ", "Tarix: ") + $"{report.FirstSale:dd.MM.yyyy} — {report.LastSale:dd.MM.yyyy}, "
            + TelegramBotText.T($"{report.CoveredMonths} мес. с продажами.", $"{report.CoveredMonths} ай сатуу менен.", $"{report.CoveredMonths} months with sales.",
                $"{report.CoveredMonths} ay satışlı.", $"{report.CoveredMonths} oy sotuv bilan.") + "</i>");

        var seasonal = report.Rows.Where(r => r.Kind == "Сезонный").ToList();
        if (seasonal.Count == 0)
        {
            text.AppendLine();
            text.AppendLine(TelegramBotText.T("Сезонных товаров не нашлось — продажи распределены по месяцам ровно.", "Мезгилдүү товар табылган жок — сатуулар айлар боюнча бирдей.",
                "No seasonal products found — sales are spread evenly across months.", "Mevsimsel ürün bulunamadı — satışlar aylara eşit dağılmış.", "Mavsumiy mahsulot topilmadi — sotuvlar oylar bo'yicha teng."));
            return text.ToString();
        }

        foreach (var season in new[] { "лето", "осень", "зима", "весна" })
        {
            var items = seasonal.Where(r => r.PeakSeason == season).Take(8).ToList();
            if (items.Count == 0)
                continue;

            var seasonName = season switch
            {
                "лето" => TelegramBotText.T("Лето", "Жай", "Summer", "Yaz", "Yoz"),
                "осень" => TelegramBotText.T("Осень", "Күз", "Autumn", "Sonbahar", "Kuz"),
                "зима" => TelegramBotText.T("Зима", "Кыш", "Winter", "Kış", "Qish"),
                _ => TelegramBotText.T("Весна", "Жаз", "Spring", "İlkbahar", "Bahor"),
            };
            text.AppendLine();
            text.AppendLine($"<b>{seasonName}</b> — {items.Count} " + TelegramBotText.T("товар(ов)", "товар", "products", "ürün", "mahsulot"));
            foreach (var item in items)
                text.AppendLine($"• {Escape(item.Name)} — " + TelegramBotText.T("пик: ", "чокусу: ", "peak: ", "zirve: ", "cho'qqi: ") + Escape(item.PeakMonths));
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
        text.AppendLine(TelegramBotText.T($"<b>Рекомендации за {days} дн.</b>", $"<b>{days} күндүк сунуштар</b>", $"<b>Recommendations, {days} days</b>",
            $"<b>{days} günlük öneriler</b>", $"<b>{days} kunlik tavsiyalar</b>"));
        var emptyLength = text.Length;

        var byRevenue = data.Slice(AnalyticsReportData.KeyRevenue);
        if (byRevenue is not { Rows.Count: > 0 })
            return text.AppendLine().Append(TelegramBotText.T("Продаж за период нет — советовать нечего.", "Бул мезгилде сатуу жок — кеңеш бере турган эч нерсе жок.",
                "No sales for the period — nothing to advise.", "Dönemde satış yok — önerilecek bir şey yok.", "Davrda sotuv yo'q — maslahat beradigan narsa yo'q.")).ToString();

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
            text.AppendLine(TelegramBotText.T("<b>Срочно заказать — группа A на исходе</b>", "<b>Тез арада заказ кылыңыз — A тобу түгөнүп баратат</b>",
                "<b>Reorder urgently — group A is running out</b>", "<b>Acil sipariş — A grubu tükeniyor</b>", "<b>Zudlik bilan buyurtma — A guruhi tugayapti</b>"));
            foreach (var row in urgent)
                text.AppendLine($"• {Escape(row.Name)} — " + TelegramBotText.T($"осталось на {row.DaysLeft:0.#} дн. ({row.Stock:0.###})", $"{row.DaysLeft:0.#} күнгө калды ({row.Stock:0.###})",
                    $"left for {row.DaysLeft:0.#} days ({row.Stock:0.###})", $"{row.DaysLeft:0.#} günlük kaldı ({row.Stock:0.###})", $"{row.DaysLeft:0.#} kunga qoldi ({row.Stock:0.###})"));
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
                text.AppendLine(TelegramBotText.T("<b>Оборот есть, прибыли нет</b>", "<b>Жүгүртүү бар, пайда жок</b>", "<b>Turnover without profit</b>", "<b>Ciro var, kâr yok</b>", "<b>Aylanma bor, foyda yo'q</b>"));
                text.AppendLine(TelegramBotText.T("<i>В группе A по выручке и в C по прибыли — проверьте наценку.</i>", "<i>Түшүм боюнча A, пайда боюнча C тобунда — үстөк бааны текшериңиз.</i>",
                    "<i>Group A by revenue and C by profit — check the markup.</i>", "<i>Ciroda A, kârda C grubunda — kâr marjını kontrol edin.</i>", "<i>Tushumda A, foydada C guruhida — ustamani tekshiring.</i>"));
                foreach (var row in weakProfit)
                    text.AppendLine($"• {Escape(row.Name)} — " + TelegramBotText.T("прибыль ", "пайда ", "profit ", "kâr ", "foyda ") + Money(row.Sum));
            }
        }

        // 3. Лежит на складе и не продаётся: деньги, замороженные в товаре.
        var sold = byRevenue.Rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dead = TelegramCatalog.Products
            .Where(x => x.Quantity > 0 && !string.IsNullOrWhiteSpace(x.Title) && !sold.Contains(x.Title))
            .Select(x => (x.Title, Value: x.Quantity * LocalCartService.ParsePrice(x.PriceLine)))
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Take(8)
            .ToList();
        if (dead.Count > 0)
        {
            var frozen = dead.Sum(x => x.Value);
            text.AppendLine();
            text.AppendLine(TelegramBotText.T("<b>Не продавалось за период</b> — заморожено ", "<b>Бул мезгилде сатылган жок</b> — тоңдурулду ", "<b>Not sold in the period</b> — frozen ",
                "<b>Dönemde satılmadı</b> — bağlı para ", "<b>Davrda sotilmadi</b> — muzlatilgan ") + Money(frozen));
            foreach (var item in dead)
                text.AppendLine($"• {Escape(item.Title)} — {Money(item.Value)}");
        }

        // 4. Длинный хвост: сколько позиций держат всего 5 % выручки.
        var tail = byRevenue.Rows.Count(r => r.Group == "C");
        if (tail > 0)
        {
            var share = tail * 100.0 / byRevenue.Rows.Count;
            text.AppendLine();
            text.AppendLine(TelegramBotText.T(
                $"<b>Длинный хвост</b>: {tail} позиц. из {byRevenue.Rows.Count} ({share:0.#} % ассортимента) дают последние 5 % выручки. По ним запас можно сокращать смелее.",
                $"<b>Узун куйрук</b>: {byRevenue.Rows.Count} позициядан {tail} ({share:0.#} % ассортимент) түшүмдүн акыркы 5 % берет. Алар боюнча запасты батыраак кыскартса болот.",
                $"<b>Long tail</b>: {tail} of {byRevenue.Rows.Count} items ({share:0.#} % of the range) bring the last 5 % of revenue. You can cut their stock more boldly.",
                $"<b>Uzun kuyruk</b>: {byRevenue.Rows.Count} kalemden {tail} tanesi (çeşidin %{share:0.#}'i) cironun son %5'ini getiriyor. Bunların stoğunu daha cesurca azaltabilirsiniz.",
                $"<b>Uzun dum</b>: {byRevenue.Rows.Count} pozitsiyadan {tail} tasi (assortimentning {share:0.#} %) tushumning oxirgi 5 % ini beradi. Ular bo'yicha zaxirani dadilroq qisqartirish mumkin."));
        }

        if (text.Length <= emptyLength + 2)
            text.AppendLine().Append(TelegramBotText.T("Явных проблем не видно — запасы и наценка в порядке.", "Ачык көйгөй көрүнбөйт — запастар жана үстөк баа жайында.",
                "No obvious problems — stock and markup are fine.", "Belirgin bir sorun yok — stok ve kâr marjı yerinde.", "Aniq muammo ko'rinmaydi — zaxira va ustama joyida."));

        return Trim(text);
    }

    /// <summary>2026-10-21 (бот, идея 3 и 10): справка на языке владельца, с кнопками меню.</summary>
    public static string BuildHelp() => TelegramBotText.T(
        """
        <b>Что умеет бот</b>

        Кнопки внизу — отчёты одним нажатием. Или команды:
        /segodnya — выручка за сегодня (как «Сводка»)
        /nedelya — выручка за 7 дней
        /mesyac — выручка с начала месяца
        /top — топ продаваемых товаров
        /abc — ABC-анализ: где деньги магазина
        /sezon — сезонность: что продаётся не круглый год
        /soveti — рекомендации: что заказать, где нет наценки, что лежит мёртвым грузом
        /zakaz — что пора заказать
        /ostatki — что заканчивается
        /dolgi — должники и ссылки для напоминания
        /help — эта справка

        Можно писать и обычными словами — помощник поймёт:
        «сколько заработали сегодня», «кто должен», «что заканчивается», «цена кола»,
        «бүгүн канча түшүм», «ким карыз».

        <i>Бот отвечает, пока на компьютере магазина включена касса или программа владельца.</i>
        """,
        """
        <b>Бот эмне кыла алат</b>

        Төмөнкү баскычтар — отчёттор бир басуу менен. Же буйруктар:
        /segodnya — бүгүнкү түшүм («Жыйынтык» сыяктуу)
        /nedelya — 7 күндүк түшүм
        /mesyac — айдын башынан бери түшүм
        /top — эң көп сатылган товарлар
        /abc — ABC-анализ: дүкөндүн акчасы кайда
        /sezon — мезгилдүүлүк: эмне жыл бою сатылбайт
        /soveti — сунуштар: эмнени заказ кылуу, кайда үстөк баа жок, эмне жатып калды
        /zakaz — эмнени заказ кылуу керек
        /ostatki — эмне түгөнүп жатат
        /dolgi — карызкорлор жана эскертүү шилтемелери
        /help — ушул жардам

        Жөнөкөй сөз менен да жазса болот:
        «бүгүн канча түшүм», «ким карыз», «эмне түгөнүп жатат», «кола канча турат».

        <i>Бот дүкөндүн компьютеринде касса же ээсинин программасы күйүп турганда жооп берет.</i>
        """,
        """
        <b>What the bot can do</b>

        The buttons below are one-tap reports. Or commands:
        /segodnya — today's revenue (as in “Summary”)
        /nedelya — revenue for 7 days
        /mesyac — revenue month to date
        /top — top-selling products
        /abc — ABC analysis: where the shop's money is
        /sezon — seasonality: what doesn't sell all year round
        /soveti — advice: what to order, where there is no markup, what is dead stock
        /zakaz — what to reorder
        /ostatki — what is running low
        /dolgi — debtors and reminder links
        /help — this help

        You can also write in plain words: “how much did we earn today”, “who owes”, “what is running out”, “price of cola”.

        <i>The bot answers while the kassa or the owner app is running on the shop computer.</i>
        """,
        """
        <b>Bot neler yapabilir</b>

        Aşağıdaki düğmeler — tek dokunuşla raporlar. Ya da komutlar:
        /segodnya — bugünkü ciro («Özet» gibi)
        /nedelya — 7 günlük ciro
        /mesyac — ay başından beri ciro
        /top — en çok satan ürünler
        /abc — ABC analizi: mağazanın parası nerede
        /sezon — mevsimsellik: yıl boyu satmayanlar
        /soveti — öneriler: ne sipariş edilmeli, kâr marjı olmayanlar, ölü stok
        /zakaz — ne sipariş edilmeli
        /ostatki — azalanlar
        /dolgi — borçlular ve hatırlatma bağlantıları
        /help — bu yardım

        Normal kelimelerle de yazabilirsiniz: «bugün ne kadar kazandık», «kim borçlu», «ne bitiyor», «kola fiyatı».

        <i>Bot, mağaza bilgisayarında kasa veya sahip programı açıkken yanıt verir.</i>
        """,
        """
        <b>Bot nimalar qila oladi</b>

        Pastdagi tugmalar — bir bosishda hisobotlar. Yoki buyruqlar:
        /segodnya — bugungi tushum («Xulosa» kabi)
        /nedelya — 7 kunlik tushum
        /mesyac — oy boshidan beri tushum
        /top — eng ko'p sotilgan mahsulotlar
        /abc — ABC tahlil: do'kon puli qayerda
        /sezon — mavsumiylik: yil bo'yi sotilmaydiganlar
        /soveti — maslahatlar: nimani buyurtma qilish, ustama yo'q joylar, o'lik zaxira
        /zakaz — nimani buyurtma qilish kerak
        /ostatki — nima tugayapti
        /dolgi — qarzdorlar va eslatma havolalari
        /help — shu yordam

        Oddiy so'zlar bilan ham yozish mumkin: «bugun qancha topdik», «kim qarzdor», «nima tugayapti», «kola narxi».

        <i>Bot do'kon kompyuterida kassa yoki ega dasturi yoqilganda javob beradi.</i>
        """);
}
