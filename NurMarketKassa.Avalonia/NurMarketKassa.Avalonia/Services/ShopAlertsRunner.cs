using System.Text.Json;
using Avalonia.Threading;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-05 (волна 2-А, функции 3 и 5): фоновый «дежурный» программы владельца. Раз в 5 минут смотрит, что пора сообщить в Telegram:
// утренняя проверка «пора заказать» (с кнопкой «Сформировать заказы»), просрочка, крупные возвраты и долги за сегодня, выручка к заданному часу ниже обычной,
// закончившийся товар A-группы, вечерний итог и недельный отчёт. Что и когда — решает ShopAlerts/ShopAutomationSettings; тихие часы — как у напоминаний должникам.
// Запускается из OwnerMorningReport.Start() (программа владельца); без Telegram-бота и с выключенными функциями ничего не делает и сеть не трогает.

public static class ShopAlertsRunner
{
    private static DispatcherTimer? _timer;
    private static int _busy;
    private static bool _salesSeeded;

    /// <summary>Запустить (повторный вызов ничего не делает). Первая проверка — через 2 минуты после старта.</summary>
    public static void Start()
    {
        if (_timer is not null)
            return;
        try
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _timer.Tick += (_, _) => _ = TickAsync();
            DispatcherTimer.RunOnce(() =>
            {
                _ = TickAsync();
                _timer.Start();
            }, TimeSpan.FromMinutes(2));
            FraudMonitor.StartDigest(); // 2026-11-05 (волна 2-Б, функция 2): досылка подозрительных операций, накопленных за тихие часы
            DispatcherTimer.RunOnce(() => _ = ExpiryHintOnceAsync(), TimeSpan.FromSeconds(45));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оповещения магазина: дежурный не запущен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-11-05 (ревью волн 0–3, С6, решение владельца): режим «предупреждать» о просрочке включён по умолчанию — при первом запуске программы владельца
    /// один раз объясняем, что изменилось, и предлагаем настроить. Флажок лежит в общем файле настроек: кассе подсказка не нужна, повтора не будет.</summary>
    private static async Task ExpiryHintOnceAsync()
    {
        try
        {
            var data = ShopAutomationSettings.Current;
            if (data.ExpiryHintShown)
                return;
            ShopAutomationSettings.Update(d => d.ExpiryHintShown = true);
            if (data.ExpiryMode != ExpiryModes.Warn)
                return; // владелец уже сам выбрал режим
            var owner = NurMarketKassa.AvaloniaHost.Views.Dialogs.PosDialogHost.ResolveOwner(null);
            var settings = await PosDialogs.ConfirmYesNoModalAsync(owner,
                Tr.T("Теперь касса предупреждает, если продаётся просроченный товар (по сроку годности из карточки): «Продать всё равно?». Если срок в карточках не заполнен или устарел, окно может мешать. Можно выключить или запретить продажу без вашего PIN.",
                    "Эми касса мөөнөтү өткөн товар сатылганда эскертет (карточкадагы жарактуулук мөөнөтү боюнча): «Баары бир сатасызбы?». Карточкаларда мөөнөт толтурулбаса же эскирсе, терезе тоскоол болушу мүмкүн. Өчүрсө же сиздин PIN кодусуз сатууну тыюуга болот.",
                    "The till now warns when an expired product is sold (by the expiry date in the product card): “Sell anyway?”. If dates in cards are empty or outdated, the window may get in the way. You can turn it off or forbid selling without your PIN.",
                    "Kasa artık süresi geçmiş ürün satılırken uyarıyor (kartındaki son kullanma tarihine göre): «Yine de satılsın mı?». Kartlarda tarih boş veya eskiyse pencere engel olabilir. Kapatabilir veya PIN'iniz olmadan satışı yasaklayabilirsiniz.",
                    "Kassa endi muddati o'tgan mahsulot sotilganda ogohlantiradi (kartochkadagi yaroqlilik muddati bo'yicha): «Baribir sotilsinmi?». Kartochkalarda muddat bo'sh yoki eskirgan bo'lsa, oyna xalaqit berishi mumkin. Uni o'chirish yoki PIN kodingizsiz sotishni taqiqlash mumkin."),
                Tr.T("Просрочка при продаже", "Сатууда мөөнөтү өткөн", "Expired products at sale", "Satışta süresi geçmiş ürün", "Sotuvda muddati o'tgan mahsulot"),
                confirmText: Tr.T("Настроить", "Жөндөө", "Settings", "Ayarla", "Sozlash"),
                cancelText: Tr.T("Оставить как есть", "Ушундай калтыруу", "Keep as is", "Olduğu gibi bırak", "Shunday qoldirish")).ConfigureAwait(true);
            if (settings)
                await NurMarketKassa.AvaloniaHost.Views.ShopAutomationWindow.ShowAsync(owner).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Просрочка: подсказка не показана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Закупочная цена товара из каталога (для суммы просроченного); нет товара — 0.</summary>
    public static double PurchasePriceOf(string productId)
    {
        try
        {
            return CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase))?.PurchasePrice ?? 0;
        }
        catch (InvalidOperationException)
        {
            return 0; // каталог обновлялся в этот момент
        }
    }

    /// <summary>Утренняя проверка «пора заказать»: берёт свежий результат (не старше 6 часов), иначе считает по поставщикам. Ошибки наружу не прячет (их ловит вызывающий).</summary>
    public static async Task<AutoOrderResult?> OrderCheckAsync(CancellationToken ct)
    {
        if (AutoOrderCheck.Latest is { } last && DateTime.Now - AutoOrderCheck.LatestAt < TimeSpan.FromHours(6) && AutoOrderCheck.LatestAt.Date == DateTime.Today)
            return last;
        var data = PurchasesStore.Load();
        var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        return await AutoOrderCheck.ComputeAsync(svc, new PlannerSettings(data.PeriodDays, data.CoverDays, data.SafetyDays), ct).ConfigureAwait(true);
    }

    private static async Task TickAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            var data = ShopAutomationSettings.Current;
            var rules = data.Alerts;
            if ((!rules.Enabled && !data.MorningOrderCheck) || !TelegramBotService.IsConfigured)
                return;
            var now = DateTime.Now;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ct = cts.Token;
            var quiet = ShopAlerts.InQuietHours(rules, now);

            if (rules.Enabled)
                await ShopAlerts.FlushPendingAsync(now).ConfigureAwait(true);

            if (data.MorningOrderCheck && now.Hour >= 9 && !quiet && !ShopAlerts.WasSentToday("order-check", now))
                await StepAsync("пора заказать", () => OrderCheckStepAsync(now, ct)).ConfigureAwait(true);

            if (!rules.Enabled)
                return;

            if (rules.Expired && now.Hour >= 9 && !ShopAlerts.WasSentToday("expired", now))
                await StepAsync("просрочка", () => ExpiredStepAsync(now, ct)).ConfigureAwait(true);

            if ((rules.BigReturn || rules.NewDebt) && now.Hour >= 8)
                await StepAsync("возвраты и долги", () => SalesStepAsync(rules, now, ct)).ConfigureAwait(true);

            if (rules.RevenueBehindPlan && now.Hour >= rules.RevenueCheckHour && now.Hour < Math.Min(24, rules.RevenueCheckHour + 3) && !ShopAlerts.WasSentToday("revenue", now))
                await StepAsync("выручка к часу", () => RevenueStepAsync(rules, now, ct)).ConfigureAwait(true);

            if (rules.ClassAOut && now.Hour >= 10 && !ShopAlerts.WasSentToday("class-a", now))
                await StepAsync("A-группа", () => ClassAStepAsync(now, ct)).ConfigureAwait(true);

            if (rules.EveningSummary && now.Hour >= rules.EveningHour && !ShopAlerts.WasSentToday("evening", now))
                await StepAsync("вечерний итог", () => ReportStepAsync("evening", "today", now, ct)).ConfigureAwait(true);

            if (rules.WeeklyReport && now.DayOfWeek == DayOfWeek.Monday && now.Hour >= 9 && !quiet && !ShopAlerts.WasSentToday("weekly", now))
                await StepAsync("недельный отчёт", () => ReportStepAsync("weekly", "week", now, ct)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оповещения магазина: ошибка дежурного ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static async Task StepAsync(string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Оповещения магазина: «{what}» пропущено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    // ───────── шаги ─────────

    private static async Task OrderCheckStepAsync(DateTime now, CancellationToken ct)
    {
        var result = await OrderCheckAsync(ct).ConfigureAwait(true);
        if (result is null)
            return;
        ShopAlerts.TryMarkSentToday("order-check", now);
        var chatId = UserPreferences.Instance.TelegramChatId;
        if (result.Count == 0 || string.IsNullOrWhiteSpace(chatId))
            return;
        var lines = result.Suppliers.Take(8).Select(s => $"• {s.Supplier.Name} — {MoneyText.Som(s.Total)} ({s.Rows.Count})");
        var text = "<b>" + Escape(AutoOrderCheck.SummaryText(result)) + "</b>\n\n" + Escape(string.Join("\n", lines))
                   + (result.Waiting > 0 ? "\n\n" + Escape(Tr.T($"Ещё {result.Waiting} ждут минимальной суммы заказа.", $"Дагы {result.Waiting} буйрутманын минималдуу суммасын күтүүдө.",
                       $"{result.Waiting} more are waiting for the minimum order sum.", $"{result.Waiting} tanesi daha asgari sipariş tutarını bekliyor.", $"Yana {result.Waiting} ta minimal buyurtma summasini kutmoqda.")) : "");
        var error = await TelegramBotService.SendWithButtonsAsync(chatId!, text,
            new[] { (Tr.T("Сформировать заказы", "Буйрутмаларды түзүү", "Make the orders", "Siparişleri oluştur", "Buyurtmalarni yaratish"), "po:make") }, ct).ConfigureAwait(true);
        PosLogger.Log(error is null ? $"Автозаказ: утреннее сообщение отправлено ({result.Count} поставщиков)." : $"Автозаказ: сообщение не ушло ({error}).", error is null ? "TELEGRAM" : "WARNING");
    }

    private static async Task ExpiredStepAsync(DateTime now, CancellationToken ct)
    {
        await ProductExpiryIndex.RefreshAsync(false, ct).ConfigureAwait(true);
        if (!ProductExpiryIndex.IsLoaded)
            return;
        ShopAlerts.TryMarkSentToday("expired", now);
        var summary = ExpiryRules.SummarizeCurrent(now);
        var text = ExpiryRules.SummaryText(summary);
        if (text.Length > 0)
            await ShopAlerts.NotifyAsync(Tr.T("Просроченные товары", "Мөөнөтү өткөн товарлар", "Expired products", "Süresi geçmiş ürünler", "Muddati o'tgan mahsulotlar"),
                text + "\n" + string.Join("\n", summary.Marks.Take(8).Select(m => $"• {m.Name} ({m.Date:dd.MM})")), now).ConfigureAwait(true);
    }

    private static async Task SalesStepAsync(ShopAlertRules rules, DateTime now, CancellationToken ct)
    {
        var today = now.Date;
        var found = new List<(string Key, string Title, string Text)>();
        for (var page = 1; page <= 10; page++)
        {
            var items = await App.SalesApi.PosSalesListAsync(page, 80, null, ct, dateFrom: today, dateToExclusive: today.AddDays(1)).ConfigureAwait(true);
            if (items.Count == 0)
                break;
            foreach (var row in items)
            {
                var id = Str(row, "id");
                if (id.Length == 0)
                    continue;
                var status = Str(row, "status").ToLowerInvariant();
                var total = RowTotal(row);
                if (rules.BigReturn && total >= rules.BigReturnAmount && ReceiptHistoryService.IsReturnedOrCanceled(new ReceiptHistoryEntry { Id = id, Status = status }))
                    found.Add(("ret|" + id, Tr.T("Крупный возврат", "Чоң кайтаруу", "Large return", "Büyük iade", "Katta qaytarish"),
                        Tr.T($"Возврат или отмена чека на {MoneyText.Som(total)} (порог {MoneyText.Som(rules.BigReturnAmount)}).", $"Чек боюнча {MoneyText.Som(total)} кайтаруу же жокко чыгаруу (чек {MoneyText.Som(rules.BigReturnAmount)}).",
                            $"Return or cancellation of a receipt for {MoneyText.Som(total)} (threshold {MoneyText.Som(rules.BigReturnAmount)}).", $"{MoneyText.Som(total)} tutarında fiş iadesi veya iptali (eşik {MoneyText.Som(rules.BigReturnAmount)}).",
                            $"{MoneyText.Som(total)} lik chek qaytarildi yoki bekor qilindi (chegara {MoneyText.Som(rules.BigReturnAmount)}).")));
                else if (rules.NewDebt && status == "debt" && total >= rules.NewDebtAmount)
                    found.Add(("debt|" + id, Tr.T("Новый долг", "Жаңы карыз", "New debt", "Yeni borç", "Yangi qarz"),
                        Tr.T($"Продажа в долг на {MoneyText.Som(total)} (порог {MoneyText.Som(rules.NewDebtAmount)}).", $"{MoneyText.Som(total)} карызга сатуу (чек {MoneyText.Som(rules.NewDebtAmount)}).",
                            $"A sale on credit for {MoneyText.Som(total)} (threshold {MoneyText.Som(rules.NewDebtAmount)}).", $"{MoneyText.Som(total)} tutarında veresiye satış (eşik {MoneyText.Som(rules.NewDebtAmount)}).",
                            $"{MoneyText.Som(total)} lik qarzga sotuv (chegara {MoneyText.Som(rules.NewDebtAmount)}).")));
            }
            if (items.Count < 80)
                break;
        }
        // Уже сообщённые отбрасываем; первая проверка после запуска, если событий много, — одним сообщением (а не россыпью).
        var fresh = found.Where(f => ShopAlerts.TryMarkEvent(f.Key, now)).ToList();
        var seeding = !_salesSeeded;
        _salesSeeded = true;
        if (fresh.Count == 0)
            return;
        if (seeding && fresh.Count > 3)
        {
            await ShopAlerts.NotifyAsync(Tr.T($"Крупные возвраты и долги за сегодня: {fresh.Count}", $"Бүгүнкү чоң кайтаруулар жана карыздар: {fresh.Count}", $"Large returns and debts today: {fresh.Count}",
                $"Bugünkü büyük iadeler ve borçlar: {fresh.Count}", $"Bugungi katta qaytarishlar va qarzlar: {fresh.Count}"), string.Join("\n", fresh.Select(f => "• " + f.Text)), now).ConfigureAwait(true);
            return;
        }
        foreach (var f in fresh.Take(5))
            await ShopAlerts.NotifyAsync(f.Title, f.Text, now).ConfigureAwait(true);
    }

    private static async Task RevenueStepAsync(ShopAlertRules rules, DateTime now, CancellationToken ct)
    {
        if (TelegramReportBuilder.ServerRevenueProvider is not { } provider)
            return;
        var data = await AnalyticsReportData.BuildAsync(App.SalesApi, now.Date.AddDays(-28), now.Date.AddDays(-1), includeSeasonality: false, ct: ct).ConfigureAwait(true);
        if (data.ServerFailed)
            return;
        var typical = ShopAlerts.TypicalDayRevenue(data.ByDay, now);
        var today = await provider("today", ct).ConfigureAwait(true);
        if (today is null)
            return;
        ShopAlerts.TryMarkSentToday("revenue", now);
        if (ShopAlerts.RevenueBehind(today.Revenue, typical, rules))
            await ShopAlerts.NotifyAsync(Tr.T("Выручка ниже обычной", "Түшүм адаттагыдан төмөн", "Revenue below usual", "Ciro her zamankinden düşük", "Tushum odatdagidan past"),
                Tr.T($"К {rules.RevenueCheckHour}:00 выручка {MoneyText.Som(today.Revenue)}. Обычный такой день — {MoneyText.Som(typical)} за весь день, к этому часу ждём около {rules.RevenueShareByCheck} %.",
                    $"{rules.RevenueCheckHour}:00 чейин түшүм {MoneyText.Som(today.Revenue)}. Адаттагы мындай күн — бир күндө {MoneyText.Som(typical)}, ушул саатка чейин болжол менен {rules.RevenueShareByCheck} % күтөбүз.",
                    $"By {rules.RevenueCheckHour}:00 revenue is {MoneyText.Som(today.Revenue)}. A usual such day brings {MoneyText.Som(typical)}; by now we expect about {rules.RevenueShareByCheck} %.",
                    $"Saat {rules.RevenueCheckHour}:00'a kadar ciro {MoneyText.Som(today.Revenue)}. Böyle bir gün genelde {MoneyText.Som(typical)}; bu saate kadar yaklaşık %{rules.RevenueShareByCheck} beklenir.",
                    $"{rules.RevenueCheckHour}:00 gacha tushum {MoneyText.Som(today.Revenue)}. Odatdagi shunday kun {MoneyText.Som(typical)}; shu soatgacha taxminan {rules.RevenueShareByCheck} % kutiladi."), now).ConfigureAwait(true);
    }

    private static async Task ClassAStepAsync(DateTime now, CancellationToken ct)
    {
        var abc = await AnalyticsReportData.BuildAsync(App.SalesApi, now.Date.AddDays(-29), now.Date, includeSeasonality: false, ct: ct).ConfigureAwait(true);
        if (abc.ServerFailed || abc.Slice(AnalyticsReportData.KeyRevenue) is not { } slice)
            return;
        var classA = slice.Rows.Where(r => r.Group == "A").Select(r => r.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var stock = await OwnerShopFacts.StockRowsAsync(ct).ConfigureAwait(true);
        ShopAlerts.TryMarkSentToday("class-a", now);
        var gone = OwnerShopFacts.SoldOut(stock, 1000).Where(r => classA.Contains(r.Name)).Select(r => r.Name).Distinct().ToList();
        if (gone.Count == 0)
            return;
        // Каждый товар — не чаще раза в сутки (пока не появится снова, повтор завтра не нужен): ключ с названием.
        var fresh = gone.Where(n => ShopAlerts.TryMarkEvent("class-a|" + n, now)).ToList();
        if (fresh.Count > 0)
            await ShopAlerts.NotifyAsync(Tr.T("Закончился товар A-группы", "A тобундагы товар түгөндү", "A-group product ran out", "A grubu ürün tükendi", "A guruhidagi mahsulot tugadi"),
                string.Join("\n", fresh.Take(10).Select(n => "• " + n)) + (fresh.Count > 10 ? "\n…" : ""), now).ConfigureAwait(true);
    }

    private static async Task ReportStepAsync(string key, string period, DateTime now, CancellationToken ct)
    {
        var text = await TelegramReportBuilder.BuildRevenueAsync(period, ct).ConfigureAwait(true);
        ShopAlerts.TryMarkSentToday(key, now);
        var error = await TelegramBotService.SendAsync(text, ct).ConfigureAwait(true);
        PosLogger.Log(error is null ? $"Оповещения магазина: отчёт «{key}» отправлен." : $"Оповещения магазина: отчёт «{key}» не ушёл ({error}).", error is null ? "TELEGRAM" : "WARNING");
    }

    // ───────── мелочи ─────────

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    private static double RowTotal(JsonElement row)
    {
        foreach (var key in new[] { "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total" })
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d))
                return d;
        return 0;
    }
}
