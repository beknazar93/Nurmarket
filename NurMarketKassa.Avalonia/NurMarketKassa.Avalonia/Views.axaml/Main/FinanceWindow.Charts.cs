using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;
using NurMarketKassa.Models;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.AvaloniaHost.Views.Dialogs; using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;



using System.Windows.Data;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    // 2026-11-06: В15 — часть класса FinanceWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — FinanceWindow.axaml.cs
    public partial class FinanceWindow
    {
        /// <summary>2026-09-30, владелец: «баг частичного возврата — частичный возврат в аналитике
        /// не отображается». Вкладка «Возвраты» показывала журнал удалений строк из корзины ДО
        /// оплаты — это не возвраты; настоящие возвраты оплаченных чеков (и частичные, у которых
        /// чек остаётся «частичный возврат» с уменьшенной суммой) там не появлялись вовсе. С 28.09
        /// сервер отдаёт список возвратов (GET api/main/pos/returns/, те же цифры, что «Документы →
        /// Возврат продажи» сайта) — берём его: номер чека, полный/частичный, точная сумма, состав.
        /// Журнал удалений — только запасной путь, когда сервер список не дал.</summary>
        private async Task<List<RefundItem>> FetchRefundsAsync(DateTime from, DateTime to, CancellationToken token)
        {
            // 2026-11-11 (сервер Умара, pos_return_edit_24h): и аннулированные (status=all) — строка с меткой «АННУЛИРОВАН»; в итоги, ленту и разбор они не входят
            var withVoided = NurMarketKassa.Services.Api.ReturnEditGate.FeatureOn(NurMarketKassa.Services.Api.ReturnDocKind.Customer);
            var list = await NurMarketKassa.Services.Api.NurCrmReportsApi.ListReturnsAsync(from.Date, to.Date, ct: token, includeVoided: withVoided).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (list is null)
                return await FetchCartItemDeletionsAsync(from, to, token).ConfigureAwait(true);
            if (list.Count > 0)
                PosLogger.Log($"Возвраты с сервера: {list.Count}, первый: {list[0].CreatedAt:O}", "SALES");

            var items = list
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new RefundItem
                {
                    Id = r.Id,
                    SaleId = r.SaleId ?? "",
                    CreatedAt = r.CreatedAt?.LocalDateTime ?? default,
                    ReceiptNumber = r.SaleNumber is { } n ? $"№{n}" : "",
                    TotalAmount = r.Amount,
                    IsExact = true,
                    Kind = (r.IsFull
                        ? Tr.T("Полный возврат", "Толук кайтаруу", "Full return", "Tam iade", "To'liq qaytarish")
                        : Tr.T("Частичный возврат", "Жарым-жартылай кайтаруу", "Partial return", "Kısmi iade", "Qisman qaytarish"))
                        + (NurMarketKassa.Services.Api.NurCrmReportsApi.IsVoided(r) ? " · " + NurMarketKassa.Services.Api.ReturnEditErrors.VoidedMark() : ""),
                    // 2026-11-11: правка/аннулирование 24 ч — кнопка «Правка» в строке (окно возвратов чека)
                    IsVoided = NurMarketKassa.Services.Api.NurCrmReportsApi.IsVoided(r),
                    // 2026-11-12 (владелец: «корректировка возвратных накладных от клиентов и от поставщиков!»): в программе владельца кнопка «Править возврат» — у каждого
                    // возврата с чеком: окно возвратов чека само покажет «Редактировать / Удалить» или причину, почему нельзя (24 ч, чек в долг, нет признака сервера).
                    CanManage = !string.IsNullOrEmpty(r.SaleId) && (ReceiptReturnActions.Enabled || (withVoided && r.Edit is { HasServerFlags: true })),
                    ProductName = r.ItemsText ?? "—",
                    QuantityDisplay = r.ItemsQty > 0 ? r.ItemsQty.ToString("0.###", CultureInfo.InvariantCulture) : "",
                    Reason = r.Reason ?? "",
                    DeletedBy = string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!,
                })
                .ToList();
            return items;
        }

        /// <summary>Журнал удалений позиций из корзины (product/quantity/who/when) — единственный
        /// след возвратов по оплаченным чекам в реальном API (см. PosCartItemDeletionsListAsync).
        /// Сервер не отдаёт сумму возврата напрямую, поэтому TotalAmount — оценка: текущая цена
        /// товара из каталога × количество (не историческая цена на момент продажи, но ближе
        /// к правде, чем всегда показывать 0 в статистике/аналитике).</summary>
        private async Task<List<RefundItem>> FetchCartItemDeletionsAsync(DateTime from, DateTime to, CancellationToken token)
        {
            try
            {
                var raw = await App.SalesApi.PosCartItemDeletionsListAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                var result = new List<RefundItem>(raw.Count);
                foreach (var el in raw)
                {
                    var item = new RefundItem();
                    if (el.TryGetProperty("id", out var idProp))
                        item.Id = idProp.ToString() ?? "";
                    if (TryDeletionTimestamp(el) is { } dt)
                        item.CreatedAt = dt;
                    if (item.CreatedAt.Date < from.Date || item.CreatedAt.Date > to.Date)
                        continue;

                    if (el.TryGetProperty("product_name", out var nameProp))
                        item.ProductName = nameProp.GetString() ?? "";
                    decimal quantity = 0;
                    if (el.TryGetProperty("quantity", out var qtyProp))
                    {
                        quantity = ParseDecimal(qtyProp);
                        item.QuantityDisplay = quantity.ToString("0.###", CultureInfo.InvariantCulture);
                    }
                    if (el.TryGetProperty("deleted_by_display", out var byProp))
                        item.DeletedBy = byProp.GetString() ?? "";

                    if (quantity > 0 && el.TryGetProperty("product", out var productIdProp) &&
                        productIdProp.ValueKind == JsonValueKind.String)
                    {
                        var productId = productIdProp.GetString();
                        var product = CatalogCacheService.Products.FirstOrDefault(p =>
                            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                        if (product != null)
                            item.TotalAmount = (decimal)LocalCartService.ParsePrice(product.PriceLine) * quantity;
                    }

                    item.Reason = item.ProductName;
                    result.Add(item);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cart item deletions (возвраты) load failed: {ex}", "WARNING");
                return new List<RefundItem>();
            }
        }

        private void UpdateCollections(List<SaleItem> sales, List<RefundItem> refunds)
        {
            // 2026-09-29, стресс-тест: одной перерисовкой таблицы, а не на каждую строку — иначе
            // 12 000 чеков месяца подвешивали окно на полминуты (см. ListCollectionView.DeferRefresh).
            using (SalesView.DeferRefresh())
            {
                _sales.Clear();
                foreach (var s in sales) _sales.Add(s);
            }

            // 2026-11-06 (В11, шаг 7, п. 26.3): возвраты — тоже одной перерисовкой (каждое Add слал полный Reset представления, O(N²)).
            using (RefundsView.DeferRefresh())
            {
                _refunds.Clear();
                foreach (var r in refunds) _refunds.Add(r);
            }
        }

        /// <summary>Сводка периода с сервера (возвраты, прибыль — как на сайте); null — нет связи.</summary>
        private ServerSalesSummary? _serverSummary;

        private void UpdateStats(List<SaleItem> sales, List<RefundItem> refunds)
        {
            decimal totalSales = sales.Sum(s => s.TotalAmount);
            decimal totalRefunds = refunds.Where(r => !r.IsVoided).Sum(r => Math.Abs(r.TotalAmount)); // 2026-11-11: без аннулированных

            decimal cashSales = sales
                .Where(s =>
                    (s.PaymentMethod?.ToUpper() == "CASH") ||
                    (s.PaymentMethod?.ToLower().Contains("нал") ?? false))
                .Sum(s => s.TotalAmount);

            // 2026-09-23. Три расхождения, из-за которых цифры кассы не сходились ни с вебом,
            // ни между собственными экранами:
            //
            // 1. «Возвраты» показывали журнал удалений позиций ИЗ КОРЗИНЫ — то есть строки,
            //    убранные ДО оплаты. Они и так не входят в сумму чека, поэтому это были не
            //    возвраты вовсе. Реальные возвраты оплаченных чеков сервер в итогах не отдаёт,
            //    касса пишет их сама (ShiftEventsStore.KindReturn) — берём их.
            // 2. «Выручка» показывала сумму продаж без вычета возвратов, а «Средний чек» рядом
            //    считался уже с вычетом: две плитки в одной строке противоречили друг другу.
            // 3. «Чеков» считалось как продажи ПЛЮС записи журнала удалений, из-за чего средний
            //    чек делился на завышенное число.
            var realReturns = 0m;
            try
            {
                var events = ShiftEventsStore.TotalsBetween(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime());
                if (events.TryGetValue(ShiftEventsStore.KindReturn, out var returned))
                    realReturns = (decimal)returned;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Возвраты для показателей не прочитаны: {ex.Message}", "WARNING");
            }

            // 2026-09-25: продажи в долг — не безналичные. Раньше «Безнал» считался как «всё, что не
            // наличные», и долг клиента попадал туда же.
            decimal debtSales = sales
                .Where(s => string.Equals(s.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.TotalAmount);
            // 2026-09-28, сверка с сайтом: смешанная оплата — отдельно, как на сайте (вкладки
            // «Продажи» и «Кассы»: cash / transfer / mixed) и в «Сводке». Разложить её на нал и
            // безнал нельзя: сервер хранит у такого чека только сумму и способ «mixed»
            // (cash_received = 0, payments = [{mixed, сумма}]) — сколько было наличными, он не
            // знает, хотя касса передаёт это при оплате. Раньше «Безнал» включал смешанную целиком
            // (509 = 469 + 40), а «Сводка» показывала её отдельной строкой — экраны расходились.
            decimal mixedSales = sales.Where(s => IsMixedPayment(s.PaymentMethod)).Sum(s => s.TotalAmount);
            decimal nonCash = totalSales - cashSales - debtSales - mixedSales;
            int totalCount = sales.Count;
            // 2026-09-28: есть сводка сайта — выручка, чеки и раскладка по оплатам ровно её
            // (analytics/market/?tab=sales, как на сайте), а не сумма загруженного списка: список
            // мог быть неполным (обрыв, предел страниц), и плитки не ждут его загрузки.
            if (_serverSummary is { Revenue: { } serverRevenue } summary)
            {
                totalSales = serverRevenue;
                totalCount = summary.Transactions ?? totalCount;
                (cashSales, mixedSales, debtSales, nonCash) = summary.Split();
            }
            // Выручка — как у сайта: сумма входящих в выручку чеков без вычета частичных возвратов
            // (сайт берёт частично возвращённый чек полной суммой, сверено 2026-09-25 за день,
            // неделю и месяц). Полностью возвращённые чеки сервер отменяет — их здесь нет.
            decimal net = totalSales;
            decimal avg = totalCount > 0 ? net / totalCount : 0m;

            // ── Базовые показатели ──
            TotalSalesText.Text = $"{net:N2} {Som}";
            TotalRefundsText.Text = $"{_serverSummary?.Returns ?? realReturns:N2} {Som}";
            CashText.Text = $"{cashSales:N2} {Som}";
            NonCashText.Text = $"{nonCash:N2} {Som}";
            ShowMixedPayments(NonCashBreakdownText, mixedSales);
            AvgReceiptText.Text = $"{avg:N2} {Som}";
            ReceiptCountText.Text = totalCount.ToString();

            // ── Новые показатели ──
            // Чистая прибыль и маржа — за вычетом возвратов (иначе прибыль выглядела бы
            // завышенной на сумму возвратов). 2026-09-16, по репорту пользователя ("маржа всегда
            // показывает 40%"): себестоимость раньше считалась условно как 60% выручки для ЛЮБОГО
            // товара — теперь берётся реальная закупочная цена каждого проданного товара
            // (см. FetchSaleLineFactsAsync/_lastSaleLineFacts, покрывает тот же период sales, что
            // и эта сводка, так как обе выборки строятся из одного и того же списка sales).
            decimal costOfGoods = _lastSaleLineFacts.Sum(f => f.Cost);
            // 2026-09-25, сверка с сайтом: прибыль = выручка − себестоимость, как «Валовая
            // прибыль» сайта и как в окне «Продажи». Раньше здесь вычиталось ещё totalRefunds —
            // журнал удалений строк ИЗ КОРЗИНЫ до оплаты; это не возвраты (см. п.1 выше), и
            // прибыль занижалась на сумму убранных из чека товаров. Настоящие возвраты — это
            // отменённые чеки, в выручку они и так не входят.
            decimal netProfit = _serverSummary?.GrossProfit ?? totalSales - costOfGoods;
            if (NetProfitText != null)
            {
                NetProfitText.Text = $"{netProfit:N2} {Som}";
                MarkLoss(NetProfitText, netProfit < 0);
            }

            if (MarginPercentText != null)
            {
                decimal margin = _serverSummary?.MarginPercent
                    ?? (totalSales > 0 ? (netProfit / totalSales * 100) : 0);
                MarginPercentText.Text = $"{margin:F1}%";
            }

            // Расходы за период. 2026-10-20, живой аудит владельца (КРИТИЧНО): раньше — только изъятия из денежного ящика
            // этого компьютера (у программы владельца 0), и «Чистая прибыль» = валовой (+588 428 при −466 643 в «Прибыли и
            // деньгах»). Теперь — расходы NurCRM из того же отчёта сервера (pnl), см. FinanceWindow.ServerExpenses.cs.
            ShowExpenses(netProfit);
        }

        /// <summary>2026-10-18, ревью О3: прибыль в минусе (убыток) — красным (класс danger у sec-kpi-value), иначе цветом текста.</summary>
        private static void MarkLoss(TextBlock text, bool loss) => text?.Classes.Set("danger", loss);

        internal static bool IsMixedPayment(string method) =>
            string.Equals(method, "mixed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "split", StringComparison.OrdinalIgnoreCase);

        /// <summary>Строка под плиткой «Безнал»: смешанная оплата за период (см. UpdateStats).
        /// Наличные + безнал + смешанная = выручка.</summary>
        internal static void ShowMixedPayments(TextBlock target, decimal mixedSales)
        {
            if (target == null)
                return;

            target.IsVisible = mixedSales > 0;
            target.Text = mixedSales > 0
                ? Tr.T($"+ смешанная: {mixedSales:N2} сом", $"+ аралаш: {mixedSales:N2} сом", $"+ mixed: {mixedSales:N2} som",
                    $"+ karışık: {mixedSales:N2} som", $"+ aralash: {mixedSales:N2} so'm")
                : "";
            ToolTip.SetTip(target, Tr.T(
                "Смешанная оплата (часть наличными, часть безналом) показана отдельно, как на сайте: сервер не хранит, какая часть чека была наличными.",
                "Аралаш төлөм (бир бөлүгү накталай, бир бөлүгү накталай эмес) сайттагыдай өзүнчө көрсөтүлдү: сервер чектин кайсы бөлүгү накталай болгонун сактабайт.",
                "Mixed payments (part cash, part cashless) are shown separately, as on the website: the server does not store how much of the receipt was paid in cash.",
                "Karışık ödeme (bir kısmı nakit, bir kısmı nakitsiz) sitedeki gibi ayrı gösterilir: sunucu fişin ne kadarının nakit ödendiğini saklamaz.",
                "Aralash to'lov (bir qismi naqd, bir qismi naqdsiz) saytdagidek alohida ko'rsatildi: server chekning qancha qismi naqd to'langanini saqlamaydi."));
        }

        /// <summary>Внесения и изъятия из денежного ящика за период. Операции хранятся локально
        /// (сервер их не знает, см. ShiftCashOperationsStore), поэтому фильтруем по дате самой
        /// операции, а не по смене — период на странице может охватывать несколько смен.</summary>
        private static (decimal Deposits, decimal Withdrawals) CashOperationsForPeriod(DateTime from, DateTime to)
        {
            var deposits = 0m;
            var withdrawals = 0m;
            try
            {
                foreach (var op in ShiftCashOperationsStore.LoadAll())
                {
                    var day = op.CreatedAt.Date;
                    if (day < from.Date || day > to.Date)
                        continue;

                    switch (CashOperationModel.ResolveKind(op.Type))
                    {
                        case CashOperationKind.Deposit:
                            deposits += op.Amount;
                            break;
                        // 2026-10-22 (статьи изъятия): инкассация (деньги сданы владельцу) — не расход магазина, в расход не идёт.
                        case CashOperationKind.Withdrawal when op.ArticleKey != CashArticles.CollectionKey:
                            withdrawals += op.Amount;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Finance expenses aggregation failed: {ex.Message}", "WARNING");
            }

            return (deposits, withdrawals);

            // Максимальный, минимальный чек и долг временно скрыты —
            // их можно вернуть, добавив соответствующие TextBlock в XAML
        }

        private void SummaryPeriod_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tagStr } || !int.TryParse(tagStr, out var period))
                return;

            _summaryPeriod = period;
            SummaryPeriodDayButton.Classes.Set("active", period == 0);
            SummaryPeriodWeekButton.Classes.Set("active", period == 1);
            SummaryPeriodMonthButton.Classes.Set("active", period == 2);
            _ = RefreshSummaryPanelAsync();
        }

        /// <summary>Сводка простым языком за день/неделю/месяц (см. AI-фичи 2026-09-03, п.2 и
        /// п.11 из мозгового штурма — расширено с "только сегодня" до переключателя периодов).
        /// Никакого внешнего ИИ — просто оборачивает уже загруженные данные в текст. Работает
        /// НЕЗАВИСИМО от диапазона дат основной страницы (Sales/History/чарты) — использует
        /// <see cref="_lastAllSales"/> (см. FetchAllSalesAsync — вся история продаж, постранично,
        /// до ~4800 чеков) и сам вырезает нужное окно, чтобы клик по "Неделя"/"Месяц" в сводке
        /// не переключал фильтр всей остальной страницы.</summary>
        private async Task RefreshSummaryPanelAsync()
        {
            // В программе владельца этой карточки нет (см. AsOwnerSection).
            if (_isOwnerSection)
                return;

            var cts = new CancellationTokenSource();
            _summaryCts?.Cancel();
            _summaryCts = cts;
            var token = cts.Token;

            DailySummaryCard.IsVisible = true;

            var to = ServerClock.Today;
            var from = _summaryPeriod switch
            {
                // 2026-10-20, проверка фильтров: общие правила периода — «Месяц» с 1-го числа (было 30 дней).
                1 => PeriodRange.WeekFrom(to),
                2 => PeriodRange.MonthFrom(to),
                _ => to,
            };
            var periodLengthDays = (to - from).Days + 1;
            var prevTo = from.AddDays(-1);
            var prevFrom = prevTo.AddDays(-(periodLengthDays - 1));

            // Как и плитки — только оплаченные чеки (SaleItem.CountsAsRevenue), иначе сводка
            // расходилась с плитками и с сайтом на сумму продаж в долг.
            // 2026-09-28: список чеков теперь загружен только за период страницы (см. LoadDataAsync) —
            // из него берём лишь «самое активное время», и только если он покрывает период сводки.
            var periodSales = _lastFetchFrom <= from && _lastFetchTo >= to
                ? _lastAllSales.Where(s => s.CountsAsRevenue && s.CreatedAt.Date >= from && s.CreatedAt.Date <= to).ToList()
                : new List<SaleItem>();
            decimal? prevPeriodRevenue = _lastFetchFrom <= prevFrom && _lastFetchTo >= prevTo
                ? _lastAllSales
                    .Where(s => s.CountsAsRevenue && s.CreatedAt.Date >= prevFrom && s.CreatedAt.Date <= prevTo)
                    .Sum(s => (decimal?)s.TotalAmount)
                : null;

            // Выручка, чеки и сравнение — с сервера, как у сайта и плиток (два запроса одновременно).
            ServerSalesSummary current, previous;
            try
            {
                var currentTask = ServerSalesSummary.FetchAsync(from, to, token);
                var previousTask = ServerSalesSummary.FetchAsync(prevFrom, prevTo, token);
                current = await currentTask.ConfigureAwait(true);
                previous = await previousTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            decimal periodRevenue = periodSales.Sum(s => s.TotalAmount);
            int receiptCount = periodSales.Count;
            if (current?.Revenue is { } serverRevenue)
            {
                periodRevenue = serverRevenue;
                receiptCount = current.Transactions ?? receiptCount;
                prevPeriodRevenue = previous?.Revenue;
            }

            var periodLabel = _summaryPeriod switch
            {
                1 => Tr.T("За неделю", "Акыркы жума", "Past week", "Son hafta", "So'nggi hafta"),
                2 => Tr.T("За месяц", "Акыркы ай", "Past month", "Son ay", "So'nggi oy"),
                _ => Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"),
            };
            // Форма «с чем сравниваем» у каждого языка своя: «чем за вчера», «кечээкиге
            // караганда», «from yesterday», «düne göre», «kechagiga nisbatan».
            var compareLabel = _summaryPeriod switch
            {
                1 => Tr.T("прошлую неделю", "өткөн жумага", "the previous week", "geçen haftaya", "oldingi haftaga"),
                2 => Tr.T("прошлый месяц", "өткөн айга", "the previous month", "geçen aya", "oldingi oyga"),
                _ => Tr.T("вчера", "кечээкиге", "yesterday", "düne", "kechagi kunga"),
            };

            if (receiptCount == 0)
            {
                DailySummaryText.Text = _summaryPeriod == 0
                    ? Tr.T("Сегодня пока не было продаж.", "Бүгүн азырынча сатуу болгон жок.", "No sales yet today.", "Bugün henüz satış yok.", "Bugun hali sotuv bo'lmadi.")
                    : Tr.T("За этот период пока не было продаж.", "Бул мезгилде азырынча сатуу болгон жок.", "No sales in this period yet.", "Bu dönemde henüz satış yok.", "Bu davrda hali sotuv bo'lmadi.");
                return;
            }

            List<SaleLineFact> periodFacts;
            try
            {
                // 2026-09-28: товар-лидер — из вкладки «Товары» сайта одним запросом. Строки до 200
                // чеков (запрос на каждый чек) — только когда сервер эту вкладку не дал.
                periodFacts = current != null
                    ? await FetchServerProductFactsAsync(from, to, from, token).ConfigureAwait(true)
                    : null;
                periodFacts ??= await FetchSaleLineFactsAsync(periodSales.Take(200).ToList(), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            decimal avgCheck = receiptCount > 0 ? periodRevenue / receiptCount : 0m;

            var topProduct = periodFacts
                .GroupBy(f => f.ProductName)
                .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.Revenue), Qty = g.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Revenue)
                .FirstOrDefault();

            var busiestHour = periodSales
                .GroupBy(s => s.CreatedAt.Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            var sb = new System.Text.StringBuilder();
            // Выручка как на сайте и в плитках — без вычета удалений из корзины (это строки,
            // убранные до оплаты, а не возвраты); сравнение со вчера идёт по той же мерке.
            sb.Append(Tr.T($"{periodLabel}: выручка {periodRevenue:N0} сом",
                $"{periodLabel}: түшүм {periodRevenue:N0} сом",
                $"{periodLabel}: revenue {periodRevenue:N0} som",
                $"{periodLabel}: ciro {periodRevenue:N0} som",
                $"{periodLabel}: tushum {periodRevenue:N0} so'm"));

            if (prevPeriodRevenue is > 0)
            {
                decimal changePercent = (periodRevenue - prevPeriodRevenue.Value) / prevPeriodRevenue.Value * 100;
                var up = changePercent >= 0;
                var pct = Math.Abs(changePercent).ToString("F0");
                var prev = prevPeriodRevenue.Value.ToString("N0");
                sb.Append(Tr.T($", это на {pct}% {(up ? "больше" : "меньше")}, чем за {compareLabel} ({prev} сом)",
                    $", бул {compareLabel} ({prev} сом) караганда {pct}% {(up ? "көп" : "аз")}",
                    $", {(up ? "up" : "down")} {pct}% from {compareLabel} ({prev} som)",
                    $", {compareLabel} ({prev} som) göre %{pct} {(up ? "daha fazla" : "daha az")}",
                    $", bu {compareLabel} ({prev} so'm) nisbatan {pct}% {(up ? "ko'p" : "kam")}"));
            }
            sb.Append('.');

            if (topProduct is not null)
                sb.Append(Tr.T($" Лучше всего продавался «{topProduct.Name}» ({topProduct.Qty} шт).",
                    $" Эң көп сатылганы — «{topProduct.Name}» ({topProduct.Qty} даана).",
                    $" Best seller: “{topProduct.Name}” ({topProduct.Qty} pcs).",
                    $" En çok satan: «{topProduct.Name}» ({topProduct.Qty} adet).",
                    $" Eng ko'p sotilgani — «{topProduct.Name}» ({topProduct.Qty} dona)."));

            sb.Append(Tr.T($" Чеков — {receiptCount}, средний чек — {avgCheck:N0} сом.",
                $" Чектер — {receiptCount}, орточо чек — {avgCheck:N0} сом.",
                $" Receipts: {receiptCount}, average receipt: {avgCheck:N0} som.",
                $" Fiş sayısı: {receiptCount}, ortalama fiş: {avgCheck:N0} som.",
                $" Cheklar — {receiptCount}, o'rtacha chek — {avgCheck:N0} so'm."));

            if (busiestHour is not null)
                sb.Append(Tr.T($" Самое активное время — {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00.",
                    $" Эң активдүү убакыт — {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00.",
                    $" Busiest time: {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00.",
                    $" En yoğun saat: {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00.",
                    $" Eng faol vaqt — {busiestHour.Hour}:00–{busiestHour.Hour + 1}:00."));

            if (current == null && periodSales.Count > 200)
                sb.Append(Tr.T(" (Товар-лидер посчитан по первым 200 чекам периода.)",
                    " (Эң көп сатылган товар мезгилдин алгачкы 200 чеги боюнча эсептелди.)",
                    " (The top product is based on the first 200 receipts of the period.)",
                    " (En çok satan ürün, dönemin ilk 200 fişine göre hesaplandı.)",
                    " (Yetakchi mahsulot davrning dastlabki 200 ta cheki bo'yicha hisoblandi.)"));

            DailySummaryText.Text = sb.ToString();
        }

        /// <summary>2026-11-06 (В11, Д4-27): за период без продаж — одна плашка вверху («За сегодня продаж нет · Показать неделю»), пустые графики ниже сворачиваются
        /// до строки (см. Update*Chart). Пока список чеков ещё грузится, плашки нет — «нет продаж» пока не известно.</summary>
        private void UpdateEmptyPeriodBar(List<SaleItem> sales)
        {
            var show = sales.Count == 0 && !_listPending;
            EmptyPeriodBar.IsVisible = show;
            if (!show)
                return;
            var single = _historyFrom.Date == _historyTo.Date;
            EmptyPeriodText.Text = single && _historyFrom.Date == ServerClock.Today
                ? Tr.T("За сегодня продаж нет", "Бүгүн сатуу жок", "No sales today", "Bugün satış yok", "Bugun sotuv yo'q")
                : single
                    ? Tr.T("За выбранный день продаж нет", "Тандалган күнү сатуу жок", "No sales for the selected day", "Seçilen gün için satış yok", "Tanlangan kunda sotuv yo'q")
                    : Tr.T("За выбранный период продаж нет", "Тандалган мезгилде сатуу жок", "No sales for the selected period", "Seçilen dönemde satış yok", "Tanlangan davrda sotuv yo'q");
            EmptyPeriodWeekButton.Content = Tr.T("Показать неделю", "Жумасын көрсөтүү", "Show the week", "Haftayı göster", "Haftani ko'rsatish");
            EmptyPeriodWeekButton.IsVisible = single;
        }

        private void EmptyPeriodWeek_Click(object sender, RoutedEventArgs e)
        {
            var week = (CustomDatePill.Parent as Panel)?.Children.OfType<RadioButton>().FirstOrDefault(r => "Week".Equals(r.Tag));
            if (week is null)
                return;
            week.IsChecked = true;
            QuickDate_Click(week, new RoutedEventArgs());
        }

        private void UpdateCharts(List<SaleItem> sales)
        {
            UpdateEmptyPeriodBar(sales);
            // 2026-09-29, стресс-тест: список обрезан (см. _salesListTruncated) — оплаты и выручка по
            // дням берутся из сводки сервера (как плитки), иначе графики молча показывали бы только
            // последние 50 000 чеков. По часам и число продаж по дням сервер не отдаёт — они по
            // загруженным чекам, об этом говорит плашка.
            // 2026-10-20, живой аудит владельца: способы оплаты за месяц не сходились со «Сводкой» (после загрузки списка
            // чеков полоса строилась по списку: наличные 1 818 967 против 1 819 353, «долг 672» среди способов, сумма
            // 1 979 868 ≠ выручке 1 979 195). Теперь способы оплаты всегда из сводки сервера, как «Сводка» (charts.payment_methods
            // того же отчёта), продажи в долг — не способ оплаты: отдельной строкой «в долг, не входит в выручку».
            var payments = PaymentsForCharts(sales, out var debtOutside);
            // 2026-10-24 (аудит, круг 8, С-4): столбики по дням/часам и тренды — без продаж в долг (как плитка «Выручка»
            // и полоса способов оплаты); раньше «Сегодня» показывало 29 вместо 15 (долг входил в столбик).
            var revenueSales = sales.Where(s => !string.Equals(s.PaymentMethod?.Trim(), "debt", StringComparison.OrdinalIgnoreCase)).ToList();
            if ((_salesListTruncated || _listPending) && _serverSummary is { Revenue: not null } summary)
            {
                var byDay = summary.ByDay
                    .Select(d => new SaleItem { CreatedAt = d.Day, TotalAmount = d.Revenue })
                    .ToList();
                UpdatePaymentSplitChart(payments, debtOutside);
                UpdateDailyRevenueChart(byDay);
                UpdateHourlyRevenueChart(revenueSales);
                UpdatePaymentSplitPie(payments);
                UpdateRevenueTrendCharts(revenueSales);
                return;
            }

            UpdatePaymentSplitChart(payments, debtOutside);
            UpdateDailyRevenueChart(revenueSales);
            UpdateHourlyRevenueChart(revenueSales);
            UpdatePaymentSplitPie(payments);
            UpdateRevenueTrendCharts(revenueSales);
        }

        /// <summary>2026-10-20: суммы по способам оплаты для полосы и круговой — из сводки сервера (как «Сводка»), без продаж
        /// в долг; без сервера — по загруженным чекам, тоже без долга. debtOutside — продано в долг за период (по чекам).</summary>
        private List<SaleItem> PaymentsForCharts(List<SaleItem> sales, out decimal debtOutside)
        {
            static bool IsDebt(string method) => string.Equals(method?.Trim(), "debt", StringComparison.OrdinalIgnoreCase);
            debtOutside = sales.Where(s => IsDebt(s.PaymentMethod)).Sum(s => s.TotalAmount);
            if (_serverSummary is { Revenue: not null } summary && summary.PaymentTotals.Count > 0)
            {
                return summary.PaymentTotals
                    .Where(kv => !IsDebt(kv.Key))
                    .Select(kv => new SaleItem { PaymentMethod = kv.Key, TotalAmount = kv.Value })
                    .ToList();
            }

            return sales.Where(s => !IsDebt(s.PaymentMethod)).ToList();
        }

        private void UpdatePaymentSplitPie(List<SaleItem> sales)
        {
            var colorKeys = sales
                .GroupBy(s => NormalizePaymentMethodLabel(s.PaymentMethod))
                .ToDictionary(g => g.Key, g => ChartPalette.PaymentKey(g.First().PaymentMethod));
            var byMethod = sales
                .GroupBy(s => NormalizePaymentMethodLabel(s.PaymentMethod))
                .Select(g => (Label: g.Key, Value: (double)g.Sum(s => s.TotalAmount), ValueText: $"{AmtText(g.Sum(s => s.TotalAmount))} {Som}"))
                .Where(x => x.Value > 0)
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Label, StringComparer.Ordinal)
                .ToList();
            BarChartRenderer.RenderPie(PaymentSplitPie, byMethod, colorKeys: colorKeys);
        }

        /// <summary>Линейная (тренд), с областями (накопительно) и комбинированная (выручка +
        /// кол-во продаж) диаграммы по дням — на тех же дневных данных, что уже строит
        /// UpdateDailyRevenueChart, просто ещё двумя способами показа.</summary>
        private void UpdateRevenueTrendCharts(List<SaleItem> sales)
        {
            var byDay = sales
                .GroupBy(s => s.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(s => s.TotalAmount), Count = g.Count() })
                .OrderBy(x => x.Date)
                .ToList();

            // 2026-10-19, этап О10 (аудит: «за «Сегодня» графики-тренды из одной точки — пустые без подсказки»): тренд из одного
            // дня не построить — вместо пустых графиков подсказка выбрать неделю или месяц.
            if (byDay.Count < 2)
            {
                foreach (var chart in new[] { RevenueTrendChart, RevenueCumulativeChart, RevenueVsSalesChart })
                {
                    chart.Children.Clear();
                    var hint = new TextBlock
                    {
                        Text = Tr.T("Тренд виден за несколько дней — выберите сверху «Неделя» или «Месяц».",
                            "Тренд бир нече күндө көрүнөт — жогорудан «Апта» же «Ай» тандаңыз.",
                            "A trend needs several days — pick “Week” or “Month” at the top.",
                            "Eğilim birkaç günde görünür — üstten «Hafta» veya «Ay» seçin.",
                            "Tendensiya bir necha kunda ko'rinadi — yuqoridan «Hafta» yoki «Oy»ni tanlang."),
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 6),
                    };
                    hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
                    chart.Children.Add(hint);
                }
                return;
            }

            var linePoints = byDay
                .Select(d => (d.Date.ToString("dd.MM"), (double)d.Total))
                .ToList();
            BarChartRenderer.RenderLine(RevenueTrendChart, linePoints);

            var runningTotal = 0m;
            var cumulativePoints = byDay
                .Select(d =>
                {
                    runningTotal += d.Total;
                    return (d.Date.ToString("dd.MM"), (double)runningTotal);
                })
                .ToList();
            BarChartRenderer.RenderArea(RevenueCumulativeChart, cumulativePoints);

            var combined = byDay
                .Select(d => (d.Date.ToString("dd.MM"), (double)d.Total, (double)d.Count, $"{AmtText(d.Total)} {Som}"))
                .ToList();
            BarChartRenderer.RenderCombined(RevenueVsSalesChart, combined,
                Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Tr.T("Продажи, шт.", "Сатуулар, даана", "Sales, pcs.", "Satış, adet", "Sotuv, dona"));
        }

        /// <summary>Подписи валюты и единиц на экране — на языке интерфейса.</summary>
        /// <summary>2026-10-22 (аудит 1.17.59, П-З): подписи сумм на графиках — целые без копеек, дробные с копейками (было округление до целых: 756 против 755,50 в «Продажах»).</summary>
        private static string AmtText(decimal v) => v == Math.Round(v) ? v.ToString("N0") : v.ToString("N2");
        private static string AmtText(double v) => v == Math.Round(v) ? v.ToString("N0") : v.ToString("N2");

        private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");
        private static string Pcs => Tr.T("шт.", "даана", "pcs", "adet", "dona");
        private static string Kg => Tr.T("кг", "кг", "kg", "kg", "kg");

        private static string NormalizePaymentMethodLabel(string method)
        {
            var key = (method ?? "").Trim().ToLowerInvariant();
            return key switch
            {
                "" => Tr.T("Не указано", "Көрсөтүлгөн эмес", "Not specified", "Belirtilmemiş", "Ko'rsatilmagan"),
                "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
                // 2026-10-21: «Безналичные», как в Сводке и в окне оплаты (было «Перевод» — тот же способ, другое слово) (пакет 2, п. 87).
                "transfer" or "card" => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
                "mbank" => "MBank",
                "mixed" => Tr.T("Смешанный", "Аралаш", "Mixed", "Karışık", "Aralash"),
                "debt" => Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"),
                // 2026-10-06: обмен и предоплата — сервер пишет «offset»; было «Offset» по-английски.
                "offset" => Tr.T("Зачёт", "Эсепке алуу", "Offset", "Mahsup", "Hisobga olish"),
                _ when key.Contains("нал") => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
                _ => char.ToUpperInvariant(method![0]) + method[1..],
            };
        }

        private void UpdatePaymentSplitChart(List<SaleItem> sales, decimal debtOutside = 0m)
        {
            PaymentSplitBar.ColumnDefinitions.Clear();
            PaymentSplitBar.Children.Clear();
            PaymentLegend.Children.Clear();
            PaymentSplitBar.IsVisible = true;

            decimal total = sales.Sum(s => s.TotalAmount);
            var byMethod = sales
                .GroupBy(s => NormalizePaymentMethodLabel(s.PaymentMethod))
                .Select(g => new { Label = g.Key, Total = g.Sum(s => s.TotalAmount), Key = ChartPalette.PaymentKey(g.First().PaymentMethod) })
                .Where(x => x.Total > 0)
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Label, StringComparer.Ordinal)
                .ToList();

            if (total <= 0 || byMethod.Count == 0)
            {
                // 2026-11-06 (В11, Д4-27): серая полоса-заглушка убрана — остаётся одна строка «Нет данных».
                PaymentSplitBar.IsVisible = false;
                PaymentLegend.Children.Add(new TextBlock
                {
                    Text = Tr.T("Нет данных за выбранный период", "Тандалган мезгил боюнча маалымат жок", "No data for the selected period", "Seçilen dönem için veri yok", "Tanlangan davr uchun ma'lumot yo'q"),
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                });
                return;
            }

            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — цвет способа оплаты по ключу (ChartPalette), тот же в круговой, «Сводке» и мобилке.
            var methodColors = ChartPalette.Assign(byMethod.Select(x => (string?)x.Key).ToList());
            for (var i = 0; i < byMethod.Count; i++)
            {
                var entry = byMethod[i];
                var color = methodColors[i];
                var isFirst = i == 0;
                var isLast = i == byMethod.Count - 1;

                PaymentSplitBar.ColumnDefinitions.Add(new ColumnDefinition((double)entry.Total, GridUnitType.Star));
                var segment = new Border
                {
                    Background = Brush.Parse(color),
                    CornerRadius = new CornerRadius(isFirst ? 6 : 0, isLast ? 6 : 0, isLast ? 6 : 0, isFirst ? 6 : 0),
                    [ToolTip.TipProperty] = $"{entry.Label}: {entry.Total:N2} {Som} ({entry.Total / total * 100:N0}%)",
                };
                Grid.SetColumn(segment, i);
                PaymentSplitBar.Children.Add(segment);

                var pct = entry.Total / total * 100;
                var legendItem = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                legendItem.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Brush.Parse(color) });
                legendItem.Children.Add(new TextBlock
                {
                    Text = $"{entry.Label}: {AmtText(entry.Total)} {Som} ({pct:N1}%)",
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                });
                PaymentLegend.Children.Add(legendItem);
            }

            // 2026-10-20: аудит владельца — продажа в долг не оплачена и в выручку не входит; сумма способов выше = выручке.
            if (debtOutside > 0m)
            {
                PaymentLegend.Children.Add(new TextBlock
                {
                    Text = Tr.T($"Ещё в долг: {debtOutside:N0} сом — в выручку не входит, пока клиент не оплатит",
                        $"Дагы карызга: {debtOutside:N0} сом — кардар төлөмөйүнчө түшүмгө кирбейт",
                        $"Also on credit: {debtOutside:N0} som — not in revenue until the customer pays",
                        $"Ayrıca veresiye: {debtOutside:N0} som — müşteri ödeyene kadar ciroya dahil değil",
                        $"Yana qarzga: {debtOutside:N0} so'm — mijoz to'lamaguncha tushumga kirmaydi"),
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        private void UpdateHourlyRevenueChart(List<SaleItem> sales)
        {
            const double maxBarHeight = 80;
            const double barWidth = 22;

            HourlyRevenueBars.Children.Clear();
            HourlyRevenueLegend.Children.Clear();
            HourlyRevenueBars.Height = sales.Count == 0 ? double.NaN : 120; // 2026-11-06 (В11, Д4-27): пустой график — одна строка, а не рамка 120 px

            if (sales.Count == 0)
            {
                HourlyRevenueBars.Children.Add(new TextBlock
                {
                    Text = Tr.T("Нет данных за выбранный период", "Тандалган мезгил боюнча маалымат жок", "No data for the selected period", "Seçilen dönem için veri yok", "Tanlangan davr uchun ma'lumot yo'q"),
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 0, 8),
                });
                return;
            }

            var byHour = sales
                .GroupBy(s => s.CreatedAt.Hour)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.TotalAmount));
            var maxVal = byHour.Count > 0 ? byHour.Values.Max() : 0m;
            if (maxVal <= 0) maxVal = 1;

            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах»: временной ряд — столбцы одного оттенка, лучший час — акцентом, слабый — приглушённо.
            var (bestHours, worstHours) = ChartPalette.SeriesMarks(Enumerable.Range(0, 24).Select(h => byHour.TryGetValue(h, out var hv) ? (double)hv : 0.0).ToList());
            for (var hour = 0; hour < 24; hour++)
            {
                var value = byHour.TryGetValue(hour, out var v) ? v : 0m;
                var barHeight = value > 0 ? Math.Max(3, (double)(value / maxVal) * maxBarHeight) : 2;

                var column = new StackPanel
                {
                    Width = barWidth,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Spacing = 4,
                };
                var hourBar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    // 2026-10-19, этап О10: столбцы — цветом текста темы, как «Выручка за 7 дней» на Сводке (были синие и оранжевые).
                    Background = value > 0 ? (this.FindResource("BrushText") as IBrush ?? Brush.Parse("#3B82F6")) : Brushes.LightGray,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    [ToolTip.TipProperty] = $"{hour:00}:00 — {value:N2} {Som}",
                };
                column.Children.Add(value > 0 ? BarChartRenderer.StyleSeriesBar(hourBar, this, bestHours.Contains(hour), worstHours.Contains(hour), false) : hourBar);
                column.Children.Add(new TextBlock
                {
                    Text = $"{hour:00}",
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                HourlyRevenueBars.Children.Add(column);
            }

            if (BarChartRenderer.SeriesLegend(this, bestHours.Count > 0, worstHours.Count > 0, false,
                    Tr.T("Лучший час", "Эң мыкты саат", "Best hour", "En iyi saat", "Eng yaxshi soat"),
                    Tr.T("Самый слабый час", "Эң начар саат", "Weakest hour", "En zayıf saat", "Eng sust soat"), "") is { } hourLegend)
                HourlyRevenueLegend.Children.Add(hourLegend);
        }

        private void UpdateDailyRevenueChart(List<SaleItem> sales)
        {
            const double maxBarHeight = 100;
            const double barWidth = 28;

            DailyRevenueBars.Children.Clear();
            DailyRevenueLegend.Children.Clear();

            var byDay = sales
                .GroupBy(s => s.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Total = g.Sum(s => s.TotalAmount) })
                .OrderBy(x => x.Date)
                .ToList();

            DailyRevenueBars.Height = byDay.Count == 0 ? double.NaN : 150; // 2026-11-06 (В11, Д4-27): пустой график — одна строка, а не рамка 150 px
            if (byDay.Count == 0)
            {
                DailyRevenueBars.Children.Add(new TextBlock
                {
                    Text = Tr.T("Нет данных за выбранный период", "Тандалган мезгил боюнча маалымат жок", "No data for the selected period", "Seçilen dönem için veri yok", "Tanlangan davr uchun ma'lumot yo'q"),
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(4, 0, 0, 8),
                });
                return;
            }

            var maxVal = byDay.Max(x => x.Total);
            if (maxVal <= 0) maxVal = 1;

            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах»: выручка по дням — временной ряд: столбцы одного оттенка, лучший день — акцентом
            // (жёлтый), самый слабый — приглушённо, сегодня — кольцом; внизу легенда.
            var (bestDays, worstDays) = ChartPalette.SeriesMarks(byDay.Select(x => (double)x.Total).ToList());
            var hasToday = false;
            foreach (var day in byDay)
            {
                var dayIndex = byDay.IndexOf(day);
                var isToday = day.Date == ServerClock.Today;
                hasToday |= isToday;
                var barHeight = Math.Max(3, (double)(day.Total / maxVal) * maxBarHeight);

                var column = new StackPanel
                {
                    Width = barWidth + 8,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Spacing = 4,
                };
                column.Children.Add(new TextBlock
                {
                    Text = day.Total > 0 ? $"{day.Total:N0}" : "",
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                var dayBar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = this.FindResource("BrushText") as IBrush ?? Brush.Parse("#F59E0B"), // 2026-10-19, этап О10: как на Сводке
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    [ToolTip.TipProperty] = $"{day.Date:dd.MM.yyyy}: {day.Total:N2} {Som}",
                };
                column.Children.Add(BarChartRenderer.StyleSeriesBar(dayBar, this, bestDays.Contains(dayIndex), worstDays.Contains(dayIndex), isToday));
                column.Children.Add(new TextBlock
                {
                    Text = day.Date.ToString("dd.MM"),
                    FontSize = 12,
                    Foreground = ThemeInk.Soft,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                DailyRevenueBars.Children.Add(column);
            }

            if (BarChartRenderer.SeriesLegend(this, bestDays.Count > 0, worstDays.Count > 0, hasToday,
                    Tr.T("Лучший день", "Эң мыкты күн", "Best day", "En iyi gün", "Eng yaxshi kun"),
                    Tr.T("Самый слабый день", "Эң начар күн", "Weakest day", "En zayıf gün", "Eng sust kun"),
                    Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")) is { } dayLegend)
                DailyRevenueLegend.Children.Add(dayLegend);
        }

        private void UpdateHistory(List<SaleItem> sales, List<RefundItem> refunds)
        {
            // 2026-09-29: одна перерисовка на весь список (см. UpdateCollections); выход из
            // DeferRefresh сам обновляет представление, отдельный Refresh больше не нужен.
            using var batch = HistoryView.DeferRefresh();
            _history.Clear();
            // 2026-10-20, доработка аудита владельца: продажи и возвраты — одной лентой по времени (новые сверху). Раньше
            // возвраты добавлялись в конец списка, после всех продаж периода, и их не было видно рядом с их временем.
            var rows = new List<HistoryItem>();
            foreach (var s in sales)
            {
                rows.Add(new HistoryItem
                {
                    Id = s.Id,
                    CreatedAt = s.CreatedAt,
                    Type = Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"),
                    ReceiptNumber = s.ReceiptNumber,
                    TotalAmount = s.TotalAmount,
                    PaymentMethod = s.PaymentMethod ?? "—"
                });
            }
            foreach (var r in refunds.Where(r => !r.IsVoided)) // 2026-11-11: аннулированный возврат — не движение денег
            {
                rows.Add(new HistoryItem
                {
                    // 2026-09-30: возврат с сервера открывает свой чек (двойной щелчок в истории).
                    Id = string.IsNullOrEmpty(r.SaleId) ? r.Id : r.SaleId,
                    CreatedAt = r.CreatedAt,
                    Type = string.IsNullOrEmpty(r.Kind) ? Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish") : r.Kind,
                    ReceiptNumber = string.IsNullOrEmpty(r.ReceiptNumber) ? r.ProductName : $"{r.ReceiptNumber} · {r.ProductName}",
                    TotalAmount = -Math.Abs(r.TotalAmount),
                    PaymentMethod = string.IsNullOrWhiteSpace(r.DeletedBy) ? "—" : r.DeletedBy
                });
            }
            foreach (var row in rows.OrderByDescending(x => x.CreatedAt))
                _history.Add(row);
        }

        // Детали чека (popup)
        private string _openReceiptId;

        /// <summary>2026-10-24 (аудит, круг 8, С-15): чек открывается одним щелчком по строке (двойной на сенсорном экране
        /// срабатывает через раз — как уже сделано в «Продажах»). Щелчок по заголовку или пустому месту чек не открывает.</summary>
        private async void ReceiptRow_Tapped(object sender, TappedEventArgs e)
        {
            var row = Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<DataGridRow>(e.Source as Avalonia.Visual, includeSelf: true);
            if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(e.Source as Avalonia.Visual, includeSelf: true) is not null)
                return; // 2026-11-11: нажатие кнопки «Правка» в строке возврата — не открытие чека
            switch (row?.DataContext)
            {
                case SaleItem s:
                    await ShowReceiptDetailsByIdAsync(s.Id, s.ReceiptNumber);
                    break;
                case RefundItem { SaleId.Length: > 0 } r:
                    await ShowReceiptDetailsByIdAsync(r.SaleId, r.ReceiptNumber);
                    break;
                case HistoryItem h:
                    await ShowReceiptDetailsByIdAsync(h.Id, h.ReceiptNumber);
                    break;
            }
        }

        private async Task ShowReceiptDetailsByIdAsync(string receiptId, string receiptNumber)
        {
            if (string.IsNullOrEmpty(receiptId)) return;
            // 2026-10-24: одиночный щелчок уже открыл этот чек — двойной не грузит его второй раз.
            if (ReceiptDetailsPopup.IsOpen && string.Equals(_openReceiptId, receiptId, StringComparison.Ordinal)) return;
            _openReceiptId = receiptId;
            try
            {
                var json = await App.SalesApi.PosSaleGetAsync(receiptId, CancellationToken.None);
                var items = new List<string>();
                try
                {
                    foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(json))
                    {
                        items.Add($"• {CartDisplayHelper.ItemName(line)} — " +
                                  $"{CartDisplayHelper.LineQuantity(line)} × " +
                                  $"{CartDisplayHelper.UnitPrice(line):N2} = " +
                                  $"{CartDisplayHelper.LineTotal(line):N2}");
                    }
                }
                catch
                {
                    if (json.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in arr.EnumerateArray())
                        {
                            string name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                            decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                            decimal price = (decimal)CartDisplayHelper.UnitPrice(line);
                            decimal total = decimal.TryParse(
                                CartDisplayHelper.LineTotal(line), NumberStyles.Number, CultureInfo.InvariantCulture, out var lt)
                                ? lt
                                : 0;
                            items.Add($"• {name} — {qty} × {price:N2} = {total:N2}");
                        }
                    }
                }
                ShowReceiptDetailsPopup(receiptNumber ?? "—", items);
                ShowPopupReturnActions(receiptId, receiptNumber, json); // 2026-11-12: «Возврат» и «Правка возвратов» — только в программе владельца
            }
            catch (Exception ex)
            {
                ErrorMessage = Tr.T("Не удалось загрузить детали чека: ", "Чектин чоо-жайын жүктөө мүмкүн болгон жок: ", "Could not load the receipt details: ", "Fiş ayrıntıları yüklenemedi: ", "Chek tafsilotlarini yuklab bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex);
            }
        }

        private void ShowReceiptDetailsPopup(string receiptNumber, List<string> items)
        {
            if (ReceiptDetailsPopup == null || PopupTitle == null || PopupItemsControl == null)
            {
                // Элементы ещё не загружены — ничего не делаем
                return;
            }

            PopupTitle.Text = Tr.T("Чек ", "Чек ", "Receipt ", "Fiş ", "Chek ") + receiptNumber;
            PopupItemsControl.ItemsSource = items;
            ReceiptDetailsPopup.IsOpen = true;
        }

        private void CloseReceiptDetails_Click(object sender, RoutedEventArgs e) =>
            ReceiptDetailsPopup.IsOpen = false;

        private async void SalesGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (SalesGrid.SelectedItem is SaleItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        /// <summary>2026-09-30, владелец: «сделай так, чтобы на плитки нажималось, как в Z-отчётах,
        /// подробно». Плитка открывает то же окно, что плитки «Деталей смены», но за выбранный
        /// период: чеки с товарами (выручка, наличные, безнал, средний чек, чеки) или возвраты.</summary>
        private void Kpi_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border { Tag: string tile })
                return;
            // 2026-10-05, владелец (телефон): «зависла в продажах — нажатие на подробнее». Окно открывалось
            // синхронно прямо из нажатия пальца; на Android ожидание окна — вложенный цикл, и пока обработчик
            // нажатия не вернулся, следующие касания снова попадали в эту плитку: за минуту открылось 37 окон
            // друг на друге. Окно — после нажатия, и не второе, пока первое открыто.
            e.Handled = true;
            if (_drillDownOpen)
                return;
            Dispatcher.UIThread.Post(() => OpenKpiDrillDown(tile));
        }

        private bool _drillDownOpen;

        private void OpenKpiDrillDown(string tile)
        {
            if (_drillDownOpen)
                return;
            if (_listPending)
            {
                ErrorMessage = Tr.T("Список чеков ещё загружается — нажмите через несколько секунд.",
                    "Чектердин тизмеси дагы эле жүктөлүүдө — бир нече секунддан кийин басыңыз.",
                    "The receipt list is still loading — tap again in a few seconds.",
                    "Fiş listesi hâlâ yükleniyor — birkaç saniye sonra tekrar dokunun.",
                    "Cheklar ro'yxati hali yuklanmoqda — bir necha soniyadan keyin bosing.");
                return;
            }

            var kind = tile switch
            {
                "returns" => ShiftDrillDownDialog.KindReturns,
                "cash" => ShiftDrillDownDialog.KindCash,
                "card" => ShiftDrillDownDialog.KindCard,
                _ => ShiftDrillDownDialog.KindSales,
            };
            var sales = _sales
                .Where(s => s.CountsAsRevenue)
                .Select(s => new ShiftReportData.ShiftSale(
                    s.Id, s.CreatedAt, s.Status ?? "", (s.PaymentMethod ?? "").Trim().ToLowerInvariant(),
                    (double)s.TotalAmount, 0, 0, null, Array.Empty<ShiftReportData.SaleLine>(), s.ReceiptNumber))
                .ToList();
            var returns = _refunds
                .Where(r => !r.IsVoided) // 2026-11-11: без аннулированных
                .Select(r => (r.CreatedAt,
                    $"{r.CreatedAt:dd.MM.yyyy} {r.TimeDisplay} · {(string.IsNullOrEmpty(r.ReceiptNumber) ? "—" : r.ReceiptNumber)} · {r.Kind}",
                    (double)r.TotalAmount,
                    string.IsNullOrWhiteSpace(r.Reason) ? r.ProductName : $"{r.ProductName}\n{r.Reason}"))
                .ToList();
            var subtitle = _displayFrom.Date == _displayTo.Date
                ? _displayFrom.ToString("dd.MM.yyyy")
                : $"{_displayFrom:dd.MM.yyyy} — {_displayTo:dd.MM.yyyy}";
            _drillDownOpen = true;
            try
            {
                PosDialogHost.Show(new ShiftDrillDownDialog(kind, subtitle, sales, returns), this);
            }
            finally
            {
                _drillDownOpen = false;
            }
        }

        /// <summary>2026-11-11 (сервер Умара, pos_return_edit_24h): «Правка» в строке возврата — окно возвратов этого чека (Редактировать / Удалить / История,
        /// пояснение, если сервер не разрешает); после изменения список возвратов и итоги перечитываются.</summary>
        private async void RefundManage_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not RefundItem { SaleId.Length: > 0 } item)
                return;
            try
            {
                // 2026-11-12: у владельца без признака сервера — пояснение, почему правки нет (а не молча); с признаком — то же окно, что в кассе
                var changed = ReceiptReturnActions.Enabled
                    ? await ReceiptReturnActions.OpenReturnsListAsync(this, item.SaleId, item.ReceiptNumber)
                    : await ReturnEditFlows.ShowCustomerReturnsAsync(this, item.SaleId, item.ReceiptNumber);
                if (changed)
                    await LoadDataAsync(_historyFrom, _historyTo, forceRefresh: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Финансы: правка возвратов чека {item.SaleId} не открылась: {ex.Message}", "WARNING");
            }
        }

        private ReceiptReturnActions _popupReturn;

        /// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): карточка чека «Финансов» (из «Чеков», «Возвратов» и «Истории») —
        /// «Возврат» (то же окно возврата, что в кассе, сразу с этим чеком) и «Правка возвратов» (возвраты чека с правкой/удалением 24 ч); после — список и итоги
        /// перечитываются с сервера. Только программа владельца (ReceiptReturnActions.Enabled) — «Финансы» кассы прежние.</summary>
        private void ShowPopupReturnActions(string saleId, string receiptNumber, JsonElement json)
        {
            if (!ReceiptReturnActions.Enabled || PopupFooterPanel == null)
                return;
            if (_popupReturn == null)
            {
                _popupReturn = new ReceiptReturnActions
                {
                    OwnerWindow = () => this,
                    BeforeOpen = () => ReceiptDetailsPopup.IsOpen = false,
                    Changed = () => LoadDataAsync(_historyFrom, _historyTo, forceRefresh: true),
                };
                PopupFooterPanel.Children.Insert(0, _popupReturn.View);
            }
            // 2026-11-12 (аудит 12.11, Д-5): с кнопками возврата карточка не выше окна (отступ 24), список позиций прокручивается
            if (ReceiptDetailsPopup.Child is Border card && Bounds.Height > 200)
                card.MaxHeight = Bounds.Height - 24;
            var row = _sales.FirstOrDefault(s => string.Equals(s.Id, saleId, StringComparison.OrdinalIgnoreCase));
            string Text(string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            // полностью возвращённый чек «Финансы» в список не берут (canceled) — статус тогда из карточки чека
            _popupReturn.ShowFor(saleId, receiptNumber, row?.Status is { Length: > 0 } st ? st : Text("status"),
                row?.ReturnStatus is { Length: > 0 } rs ? rs : Text("return_status"));
        }

        private async void RefundsGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(e.Source as Avalonia.Visual, includeSelf: true) is not null)
                return; // 2026-11-11: двойное нажатие на кнопку «Правка» — не открытие чека
            // 2026-09-30: возврат с сервера открывает свой чек. У записей журнала удалений корзины
            // (запасной путь) id чека нет — они не открываются.
            if (RefundsGrid.SelectedItem is RefundItem { SaleId.Length: > 0 } item)
                await ShowReceiptDetailsByIdAsync(item.SaleId, item.ReceiptNumber);
        }

        private async void HistoryGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (HistoryGrid.SelectedItem is HistoryItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        // 2026-10-18, этап О3: выбранная кнопка периода — чтобы вернуть её, если окно «Спец. дата» закрыли без выбора.
        private RadioButton _quickDatePill;

        // Быстрые даты
        private async void QuickDate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            if (tag == "Custom")
            {
                // 2026-10-20, проверка фильтров: окно дат открывается на текущем периоде, а не на сегодня.
                var dlg = new FinanceDateRangeDialog(_historyFrom, _historyTo);
                // 2026-10-18, этап О3: выбранный период теперь виден (сегменты sec-seg) — после выбора дат «Спец. дата»
                // остаётся выбранной, при отмене выбор возвращается на прежнюю кнопку (раньше после окна дат не было
                // выбрано ничего). Даты и загрузка — как были.
                var previous = _quickDatePill ?? (CustomDatePill.Parent as Panel)?.Children.OfType<RadioButton>()
                    .FirstOrDefault(r => "Today".Equals(r.Tag));
                if (await dlg.ShowDialog<bool>(this) == true)
                {
                    // Отмечается сразу, до загрузки: пока грузится, могли нажать другой период — тогда его выбор не трогаем.
                    _quickDatePill = CustomDatePill;
                    CustomDatePill.IsChecked = true;
                    _historyFrom = dlg.FromDate;
                    _historyTo = dlg.ToDate;
                    SyncDatePickers(dlg.FromDate, dlg.ToDate);
                    await LoadDataAsync(_historyFrom, _historyTo);
                    if (ReferenceEquals(_quickDatePill, CustomDatePill))
                        CustomDatePill.IsChecked = true;
                }
                else if (previous != null && !ReferenceEquals(previous, CustomDatePill))
                    previous.IsChecked = true;
                else
                    CustomDatePill.IsChecked = ReferenceEquals(previous, CustomDatePill);
                return;
            }
            _quickDatePill = rb;

            DateTime from, to;
            DateTime today = ServerClock.Today;
            switch (tag)
            {
                case "Yesterday": from = to = today.AddDays(-1); break;
                // 2026-10-20, проверка фильтров: одно правило во всех окнах (PeriodRange) — «Неделя» = последние 7 дней
                // включая сегодня (было — с понедельника), «Месяц» — с 1-го числа.
                case "Week": from = PeriodRange.WeekFrom(today); to = today; break;
                case "Month": from = PeriodRange.MonthFrom(today); to = today; break;
                default: from = to = today; break;
            }

            _historyFrom = from;
            _historyTo = to;
            SyncDatePickers(from, to);
            await LoadDataAsync(from, to);
            // 2026-10-18, ревью О3: снимаем «Спец. дату», только если за время загрузки не выбрали другой период.
            if (ReferenceEquals(_quickDatePill, rb))
                CustomDatePill.IsChecked = false;
        }

        // 2026-10-20, проверка фильтров (владелец: «фильтры не работают»): поля «с / по» раньше были скрыты и не подписаны
        // на изменение. Теперь ввод даты — загрузка за этот период, выбранным становится «Спец. дата»; сегменты и окно дат
        // ставят даты в поля сами (без повторной загрузки — флаг _syncingPickers).
        private bool _syncingPickers;

        private void SyncDatePickers(DateTime from, DateTime to)
        {
            _syncingPickers = true;
            try
            {
                FromPicker.SelectedDate = from.Date;
                ToPicker.SelectedDate = to.Date;
            }
            finally
            {
                _syncingPickers = false;
            }
        }

        private async void DatePicker_DateChanged(object sender, Avalonia.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingPickers || FromPicker.SelectedDate is not { } fromPicked || ToPicker.SelectedDate is not { } toPicked)
                return;
            var from = fromPicked.Date;
            var to = toPicked.Date;
            if (from > to)
                (from, to) = (to, from);
            if (from == _historyFrom.Date && to == _historyTo.Date)
                return;
            _historyFrom = from;
            _historyTo = to;
            SyncDatePickers(from, to);
            _quickDatePill = CustomDatePill;
            CustomDatePill.IsChecked = true;
            await LoadDataAsync(from, to);
        }

        // Работа с кассовой историей
        private static List<CashSessionEntry> LoadCashHistoryFromDisk()
        {
            try
            {
                // 2026-10-29 (аварийный аудит, п. 7): битый файл — из копии .bak (AtomicFile), а не молча «пусто».
                var text = AtomicFile.ReadAllText(CashHistoryFilePath);
                if (text is not null)
                    return JsonSerializer.Deserialize<List<CashSessionEntry>>(text)
                           ?? new List<CashSessionEntry>();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cash history load failed: {ex.GetType().Name}", "WARNING");
            }
            return new List<CashSessionEntry>();
        }

        private static void SaveCashHistoryToDisk(IEnumerable<CashSessionEntry> entries)
        {
            try
            {
                var dir = Path.GetDirectoryName(CashHistoryFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(CashHistoryFilePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false })); // 2026-10-29: атомарно, с копией .bak
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Cash history save failed: {ex.GetType().Name}", "WARNING");
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // 2026-09-29: «Обновить» — всегда свежие цифры сервера, мимо короткого кэша отчётов.
            NurMarketKassa.Services.Api.SalesApiService.InvalidateReportCache();
            _seasonalityLoaded = false;
            await LoadDataAsync(_historyFrom, _historyTo, forceRefresh: true);
        }

        // INotifyPropertyChanged
        public new event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Вложенные классы
        public class SaleItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            // 2026-09-25: дата и время строкой, а не StringFormat=HH:mm в разметке — в «Истории»
            // все чеки показывали 00:00, хотя время с сервера приходило верное.
            public string DateText => CreatedAt.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture);
            public string TimeText => CreatedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
            /// <summary>Статус продажи на сервере: paid, debt (в долг, ещё не оплачена) и т.д.</summary>
            public string Status { get; set; } = "";
            /// <summary>Входит ли чек в выручку. Как у сайта (аналитика, «Документы → Продажа»):
            /// оплаченные и частично возвращённые (полной суммой). Продажа в долг до оплаты остаётся в списке, но не в плитках.</summary>
            public bool CountsAsRevenue => Status.Length == 0
                || string.Equals(Status, "paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Status, "partially_returned", StringComparison.OrdinalIgnoreCase);
            public bool IsRefund { get; set; }
            public string RefundReason { get; set; }
            public string CustomerId { get; set; }
            // 2026-10-18: для вкладки «Чеки» «Продаж и финансов» (см. FetchSalesPageAsync).
            public decimal DiscountTotal { get; set; }
            public string FirstItemName { get; set; } = "";
            // 2026-11-12 (возврат чеков в программе владельца): статус возврата (none/partial/full), кассир и клиент — для «Чеков», карточки чека и фильтров.
            public string ReturnStatus { get; set; }
            public string Cashier { get; set; }
            public string ClientName { get; set; }
        }

        public class RefundItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            /// <summary>2026-09-17: "—" вместо обманчивого "00:00", когда сервер не прислал
            /// реальное время суток (см. TryDeletionTimestamp) — ровно полночь для события
            /// удаления позиции из корзины на практике не бывает, так что это безопасный признак
            /// "время неизвестно", а не настоящая полночь.</summary>
            public string TimeDisplay => CreatedAt.TimeOfDay == TimeSpan.Zero ? "—" : CreatedAt.ToString("HH:mm");
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            /// <summary>2026-09-30: возврат с сервера — сумма точная, есть чек и вид (полный/частичный).
            /// Запись журнала удалений корзины — сумма оценочная («≈»).</summary>
            public bool IsExact { get; set; }
            public string SaleId { get; set; } = "";
            public string Kind { get; set; } = "";
            public string AmountDisplay => (IsExact ? "" : "≈") + TotalAmount.ToString("N2", CultureInfo.CurrentCulture) + " " + NurMarketKassa.Services.MoneyText.Currency; // 2026-11-01 (Д2-04): валюта на языке интерфейса
            public string Reason { get; set; } = "";
            public string ProductName { get; set; } = "";
            public string QuantityDisplay { get; set; } = "";
            public string DeletedBy { get; set; } = "";
            /// <summary>2026-11-11: аннулированный возврат (status=all) — с меткой, не в итогах.</summary>
            public bool IsVoided { get; set; }
            /// <summary>2026-11-11: показать кнопку «Правка» (признак pos_return_edit_24h и поля сервера у возврата).</summary>
            public bool CanManage { get; set; }
            public string ManageText => ReceiptReturnActions.Enabled // 2026-11-12: у владельца — «Править возврат» (как просил владелец), в кассе — прежнее «Правка»
                ? Tr.T("Править возврат", "Кайтарууну оңдоо", "Edit return", "İadeyi düzenle", "Qaytarishni tahrirlash")
                : Tr.T("Правка", "Оңдоо", "Edit", "Düzenle", "Tahrirlash");
        }

        public class HistoryItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            // 2026-09-25: дата и время строкой, а не StringFormat=HH:mm в разметке — в «Истории»
            // все чеки показывали 00:00, хотя время с сервера приходило верное.
            public string DateText => CreatedAt.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture);
            public string TimeText => CreatedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            public string Type { get; set; } = "";
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
        }

        public class CashSessionEntry
        {
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public decimal Amount { get; set; }
            public string UserId { get; set; }
            public string Type { get; set; }
            public string Comment { get; set; }
        }
    }
}
