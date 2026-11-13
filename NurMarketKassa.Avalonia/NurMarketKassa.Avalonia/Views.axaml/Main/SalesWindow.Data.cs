using NurMarketKassa.Core.Application;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
using Avalonia.Threading;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    // 2026-11-06: В15 — часть класса SalesWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — SalesWindow.axaml.cs
    public partial class SalesWindow
    {
        /// <summary>2026-09-28: топ-10 товаров периода — вкладка «Товары» сайта одним запросом
        /// (выручка строк после скидки, как на сайте). null — сервер её не дал.</summary>
        private static async Task<List<TopItem>> FetchServerTopItemsAsync(DateTime from, DateTime to, CancellationToken token)
        {
            try
            {
                var figures = await AnalyticsReportData.ServerFigures.FetchAsync(App.SalesApi, from, to, full: false, token)
                    .ConfigureAwait(true);
                return figures?.Products
                    .GroupBy(p => p.Name)
                    .Select(g => new TopItem
                    {
                        ProductName = g.Key,
                        Revenue = (decimal)g.Sum(p => p.Revenue),
                        Quantity = (int)Math.Round(g.Sum(p => p.Quantity), MidpointRounding.AwayFromZero),
                    })
                    .OrderByDescending(t => t.Revenue)
                    .Take(10)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Товары периода с сервера не получены: {ex.Message}", "WARNING");
                return null;
            }
        }

        private CancellationTokenSource _contentsCts;

        /// <summary>Не больше стольких чеков (самых новых) получают полный состав в строке списка.
        /// 2026-09-29, стресс-тест: было 500 — 500 запросов на каждое переключение периода; на живом
        /// сервере это за ~40 с упиралось в «слишком частые запросы» (429), и отказы получали уже
        /// запросы самих отчётов. 100 новых чеков — это первые экраны списка; у остальных — первый
        /// товар из списка продаж, как и раньше.</summary>
        private const int MaxReceiptContents = 100;

        /// <summary>Состав чека в строке списка (2026-09-26, «не показывает чеки внутри») — в фоне
        /// (2026-09-28). Цифры окна от него больше не зависят: раньше плитки прибыли и топ ждали, пока
        /// скачается КАЖДЫЙ чек периода. Полный состав — у 500 самых новых чеков (запрос на чек,
        /// с общим ограничением частоты); у остальных — первый товар из списка продаж.</summary>
        private async Task LoadReceiptContentsAsync(List<SaleItem> sales)
        {
            _contentsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _contentsCts = cts;
            var token = cts.Token;
            try
            {
                await Task.WhenAll(sales
                    .Where(s => !string.IsNullOrEmpty(s.Id))
                    .OrderByDescending(s => s.CreatedAt)
                    .Take(MaxReceiptContents)
                    .Select(async sale =>
                    {
                        try
                        {
                            var json = await SaleDetailCache.GetAsync(sale.Id, token).ConfigureAwait(false);
                            var summary = new List<string>();
                            if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var line in items.EnumerateArray())
                                {
                                    // 2026-11-12 (аудит 12.11, Д-14): название строки — общим разбором (product_name, name, product{…}…), а не «?»
                                    var name = CartDisplayHelper.ItemName(line);
                                    if (string.IsNullOrWhiteSpace(name))
                                        name = Tr.T("Товар без названия", "Аталышы жок товар", "Unnamed item", "Adsız ürün", "Nomsiz mahsulot");
                                    decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                                    summary.Add(qty == 1m ? name : $"{name} ×{qty.ToString("0.###", CultureInfo.InvariantCulture)}");
                                }
                            }

                            if (summary.Count > 0 && !token.IsCancellationRequested)
                            {
                                var text = string.Join(", ", summary);
                                Dispatcher.UIThread.Post(() => sale.ItemsSummary = text);
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                        }
                        catch (ApiException ex) when (ex.StatusCode == 429)
                        {
                            // 2026-09-29: сервер просит паузу — остальные составы не запрашиваем (это
                            // украшение списка), чтобы не отбирать лимит у отчётов и у кассы.
                            PosLogger.Log($"Состав чеков в «Продажах» остановлен: сервер ответил 429 ({sale.Id}).", "WARNING");
                            cts.Cancel();
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Sales receipt contents skipped ({sale.Id}): {SaleDetailCache.Describe(ex)}", "WARNING");
                        }
                    })).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void UpdateCollections(List<SaleItem> sales, List<RefundItem> refunds)
        {
            // 2026-09-29, стресс-тест: одной перерисовкой таблицы, а не на каждую строку (O(N²),
            // см. ListCollectionView.DeferRefresh и FinanceWindow.UpdateCollections).
            using (SalesView.DeferRefresh())
            {
                _sales.Clear();
                foreach (var s in sales) _sales.Add(s);
            }
            _refunds.Clear();
            foreach (var r in refunds) _refunds.Add(r);
        }

        /// <summary>Журнал удалений позиций из корзины (product/quantity/who/when) — единственный
        /// след возвратов по оплаченным чекам в реальном API (сервер не отдаёт "is_refund" вовсе).
        /// Сумма возврата — оценка: текущая цена товара из каталога × количество (не историческая
        /// цена на момент продажи, но ближе к правде, чем всегда показывать 0 в статистике).</summary>
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
                    if (el.TryGetProperty("created_at", out var dateProp) &&
                        DateTime.TryParse(dateProp.GetString(), out var dt))
                        item.CreatedAt = dt;
                    if (item.CreatedAt.Date < from.Date || item.CreatedAt.Date > to.Date)
                        continue;

                    string productName = "";
                    if (el.TryGetProperty("product_name", out var nameProp))
                        productName = nameProp.GetString() ?? "";

                    decimal quantity = 0;
                    if (el.TryGetProperty("quantity", out var qtyProp))
                        quantity = ParseDecimal(qtyProp);

                    if (quantity > 0 && el.TryGetProperty("product", out var productIdProp) &&
                        productIdProp.ValueKind == JsonValueKind.String)
                    {
                        var productId = productIdProp.GetString();
                        var product = CatalogCacheService.Products.FirstOrDefault(p =>
                            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                        if (product != null)
                            item.TotalAmount = (decimal)LocalCartService.ParsePrice(product.PriceLine) * quantity;
                    }

                    item.ReceiptNumber = productName;
                    item.Reason = quantity > 0
                        ? $"{productName} ×{quantity.ToString("0.###", CultureInfo.InvariantCulture)}"
                        : productName;
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

        /// <summary>Выгрузка аналитики за выбранный период в Excel и в Word.
        ///
        /// Период берётся ровно тот, что выбран на экране, — чтобы цифры в файле совпадали с
        /// теми, что кассир видит перед собой. Отчёт строится из локальных данных кассы и не
        /// требует интернета: выгрузку часто просят тогда, когда связи уже нет.</summary>
        // 2026-10-19, владелец: «при нажатии на Excel просто выведи все продажи, а не аналитику» — Excel: все чеки периода
        // (лист «Чеки» и «Товары», SalesExcelExport); Word — аналитика, как была.
        private async void ExportExcel_Click(object? sender, RoutedEventArgs e) =>
            await SalesExcelExport.RunAsync(this, _historyFrom, _historyTo, message => ErrorMessage = message,
                () => AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: true)).ConfigureAwait(true);

        private async void ExportWord_Click(object? sender, RoutedEventArgs e) =>
            await ExportAnalyticsAsync(toWord: true).ConfigureAwait(true);

        private async Task ExportAnalyticsAsync(bool toWord)
        {
            if (!TariffGate.CanUseAnalyticsExport)
            {
                ErrorMessage = TariffGate.AnalyticsExportLockedMessage;
                return;
            }

            var extension = toWord ? "docx" : "xlsx";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = toWord
                    ? Tr.T("Сохранить отчёт в Word", "Отчётту Word'го сактоо", "Save report to Word", "Raporu Word olarak kaydet", "Hisobotni Word'ga saqlash")
                    : Tr.T("Сохранить отчёт в Excel", "Отчётту Excel'ге сактоо", "Save report to Excel", "Raporu Excel olarak kaydet", "Hisobotni Excel'ga saqlash"),
                SuggestedFileName = AnalyticsExportService.SuggestFileName(AnalyticsExportService.FileTitle,
                    _historyFrom, _historyTo, ReportShopName.Current, extension),
                FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
            });
            if (file is null)
                return;

            var path = ExportFileBridge.PathFor(file); // 2026-11-05 (А3-05): на Android без локального пути — через временный файл

            try
            {
                ErrorMessage = Tr.T("Готовлю отчёт…", "Отчёт даярдалууда…", "Preparing the report…", "Rapor hazırlanıyor…", "Hisobot tayyorlanmoqda…");
                // 2026-09-28: цифры выгрузки — те же, что на экране и на сайте (см. AnalyticsReportData.ServerFigures).
                var data = await AnalyticsReportData.BuildAsync(App.SalesApi, _historyFrom, _historyTo, full: true).ConfigureAwait(true);
                var shop = ReportShopName.Current;

                await Task.Run(() =>
                {
                    if (toWord)
                        AnalyticsExportService.ExportToWord(path!, data, shop);
                    else
                        AnalyticsExportService.ExportToExcel(path!, data, shop);
                }).ConfigureAwait(true);
                path = await ExportFileBridge.CompleteAsync(file, path); // 2026-11-05 (А3-05)

                ErrorMessage = Tr.T($"Отчёт сохранён: {path}", $"Отчёт сакталды: {path}", $"Report saved: {path}", $"Rapor kaydedildi: {path}", $"Hisobot saqlandi: {path}");
                AvaloniaHost.Services.NoticeBanner.Apply(NoticeBox, NoticeText, success: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выгрузка аналитики не удалась: {ex}", "WARNING");
                ErrorMessage = Tr.T("Не удалось сохранить отчёт: ", "Отчётту сактоо мүмкүн болгон жок: ", "Could not save the report: ", "Rapor kaydedilemedi: ", "Hisobotni saqlab bo'lmadi: ") + ex.Message;
            }
        }

        /// <summary>Сводка периода с сервера (возвраты, прибыль — как на сайте); null — нет связи.</summary>
        private ServerSalesSummary? _serverSummary;

        /// <param name="listLoaded">false — список чеков ещё грузится, показаны только цифры сводки
        /// сайта (2026-09-28); скидки, которые считаются по списку, пока «…».</param>
        private void UpdateStats(List<SaleItem> allSales, List<RefundItem> refunds, bool listLoaded = true)
        {
            // 2026-09-25, сверка с сайтом: выручка — только оплаченные чеки (см. SaleItem.CountsAsRevenue).
            var sales = allSales.Where(s => s.CountsAsRevenue).ToList();
            decimal totalSales = sales.Sum(s => s.TotalAmount);
            decimal totalRefunds = refunds.Sum(r => Math.Abs(r.TotalAmount));
            decimal cashSales = sales.Where(s => (s.PaymentMethod?.ToUpper() == "CASH") || (s.PaymentMethod?.ToLower().Contains("нал") ?? false)).Sum(s => s.TotalAmount);
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
            // 2026-09-28: смешанная оплата — отдельно, как на сайте и в «Финансах» (см.
            // FinanceWindow.UpdateStats): сервер не хранит, какая часть такого чека была наличными.
            decimal mixedSales = sales.Where(s => FinanceWindow.IsMixedPayment(s.PaymentMethod)).Sum(s => s.TotalAmount);
            decimal nonCash = totalSales - cashSales - debtSales - mixedSales;
            int totalCount = sales.Count;
            // 2026-09-28: есть сводка сайта — выручка, чеки и оплаты ровно её (см. FinanceWindow.UpdateStats).
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

            TotalSalesText.Text = $"{net:N2} {Som}";
            TotalRefundsText.Text = $"{_serverSummary?.Returns ?? realReturns:N2} {Som}";
            CashText.Text = $"{cashSales:N2} {Som}";
            NonCashText.Text = $"{nonCash:N2} {Som}";
            FinanceWindow.ShowMixedPayments(NonCashBreakdownText, mixedSales);
            AvgReceiptText.Text = $"{avg:N2} {Som}";
            ReceiptCountText.Text = totalCount.ToString();
            // 2026-11-07 (Б-C1): неоплаченные долговые чеки в плитки не входят (как на сайте) — под «Чеками» отдельная строка «+ в долг: N на S сом».
            SalesTileRules.Show(ReceiptsDebtNote, listLoaded ? allSales.Select(s => (s.Status, s.TotalAmount)) : null);

            // Скидки и оплата бонусами (2026-09-22). Берутся из локальной таблицы кассы, а не с
            // сервера: сервер отдаёт скидку чека одной суммой, в которой бонусы неотличимы от
            // обычной скидки, а про баллы он не знает вовсе.
            try
            {
                var adjustments = ClientLoyaltyStore.AdjustmentsBetween(
                    _historyFrom.Date.ToUniversalTime(),
                    _historyTo.Date.AddDays(1).ToUniversalTime());
                // 2026-09-25: скидки — из discount_total самих чеков (там и скидки на строку, и на
                // весь чек, и чеки других касс), за вычетом оплаты бонусами: сервер пишет её в ту же
                // сумму. Локальная таблица знает только скидки на весь чек этой кассы, поэтому плитка
                // показывала 0,00, когда скидка была на строку.
                var serverDiscounts = sales.Sum(s => s.DiscountTotal);
                var discounts = Math.Max(0d, (double)serverDiscounts - adjustments.PointsRedeemed);
                DiscountsText.Text = !listLoaded ? "…" : _salesListTruncated ? "—" : $"{discounts:N2} {Som}";
                PointsRedeemedText.Text = $"{adjustments.PointsRedeemed:N2} {Som}";
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Shift adjustments read failed: {ex.Message}", "WARNING");
                DiscountsText.Text = "—";
                PointsRedeemedText.Text = "—";
            }

            // 2026-09-17: реальные себестоимость/маржа считаются в LoadTopItemsAsync (нужны
            // закупочные цены по каждой проданной позиции — раньше здесь было costOfGoods =
            // totalSales * 0.6m, поэтому маржа ВСЕГДА показывала ровно 40% независимо от
            // реальных продаж). Пока построчные данные не загрузились — плейсхолдер загрузки.
            NetProfitText.Text = totalSales > 0 ? "…" : "—";
            MarkLoss(NetProfitText, false);
            MarginPercentText.Text = totalSales > 0 ? "…" : "—";
            // 2026-09-28: прибыль и маржа сайта известны сразу — не ждём построчной загрузки чеков.
            if (_serverSummary is { GrossProfit: { } serverProfit, MarginPercent: { } serverMargin })
            {
                NetProfitText.Text = $"{serverProfit:N2} {Som}";
                MarkLoss(NetProfitText, serverProfit < 0);
                MarginPercentText.Text = $"{serverMargin:F1}%";
            }
        }

        /// <summary>2026-10-18, ревью О3: прибыль в минусе (убыток) — красным (класс danger у sec-kpi-value), иначе цветом текста.</summary>
        private static void MarkLoss(TextBlock text, bool loss) => text?.Classes.Set("danger", loss);

        /// <summary>2026-09-17: раньше здесь были ДВЕ проблемы разом:
        /// 1) N+1 — по одному последовательному запросу PosSaleGetAsync НА КАЖДЫЙ чек периода,
        ///    один за другим (при сотнях чеков — сотни round-trip подряд, отсюда "аналитика
        ///    очень медленно грузится"). Теперь запросы идут параллельно с ограничением
        ///    одновременных (SemaphoreSlim), а не строго по одному.
        /// 2) Маржа в UpdateStats считалась как totalSales * 0.6m (фиктивная себестоимость),
        ///    поэтому ВСЕГДА показывала ровно 40% вне зависимости от реальных продаж. Раз уж
        ///    здесь и так построчно загружаются проданные позиции — заодно считаем реальную
        ///    себестоимость по закупочной цене каждого товара (PurchasePrice из локального
        ///    каталога) и настоящую маржу.</summary>
        private async Task LoadTopItemsAsync(List<SaleItem> sales, CancellationToken token)
        {
            using var gate = new SemaphoreSlim(8);

            // Один снимок каталога на весь проход: раньше на КАЖДУЮ строку КАЖДОГО чека товар
            // искался перебором всего каталога (то же исправление, что уже сделано в «Финансах»).
            var catalogById = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in CatalogCacheService.Products.ToArray()) // 2026-11-06: копия — каталог обновляется в UI-потоке, а расчёт идёт в фоне
            {
                if (!string.IsNullOrEmpty(p.Id))
                    catalogById[p.Id] = p;
            }
            // 2026-11-06 (удаление товара): удалённый/архивный товар остался в старых чеках — без его закупки маржа за прошлые дни завышалась (себестоимость 0). Берём запомненную при удалении закупку.
            foreach (var gone in RemovedProductMemory.All())
            {
                if (!catalogById.ContainsKey(gone.Id))
                    catalogById[gone.Id] = RemovedProductMemory.ToTile(gone);
            }

            async Task<(decimal revenue, decimal cost, List<(string name, decimal total, int qty)> lines)> FetchOneAsync(SaleItem sale)
            {
                var summary = new List<string>();
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var json = await SaleDetailCache.GetWithRetryAsync(sale.Id, token);
                    var lines = new List<(string, decimal, int)>();
                    decimal revenue = 0m, cost = 0m;
                    if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var line in items.EnumerateArray())
                        {
                            string name = line.TryGetProperty("product_name", out var n) ? n.GetString() ?? "?" : "?";
                            decimal qty = line.TryGetProperty("quantity", out var q) ? ParseDecimal(q) : 0;
                            // "price"/"total" aren't real fields on a sale line — CartDisplayHelper
                            // (used successfully during checkout) reads the price from "unit_price".
                            // Mirror that instead of guessing field names again.
                            decimal total = line.TryGetProperty("line_total", out var lt) ? ParseDecimal(lt)
                                : line.TryGetProperty("amount", out var am) ? ParseDecimal(am)
                                : line.TryGetProperty("total", out var t) ? ParseDecimal(t)
                                : (decimal)CartDisplayHelper.UnitPrice(line) * qty;

                            revenue += total;
                            var productId = CartDisplayHelper.TryProductId(line);
                            NurMarketKassa.Models.Pos.CatalogProductTileVm? product = null;
                            if (!string.IsNullOrEmpty(productId))
                                catalogById.TryGetValue(productId, out product);
                            if (product != null && product.PurchasePrice > 0)
                                cost += (decimal)product.PurchasePrice * qty;

                            lines.Add((name, total, (int)qty));
                            summary.Add(qty == 1m ? name : $"{name} ×{qty.ToString("0.###", CultureInfo.InvariantCulture)}");
                        }
                    }

                    // 2026-09-26, отзыв «не показывает чеки внутри (Продажи)»: в колонке «Чек» был
                    // только номер, содержимое — лишь по двойному нажатию. Состав всё равно
                    // загружается здесь (для топа и прибыли) — показываем его прямо в строке.
                    var text = string.Join(", ", summary);
                    Dispatcher.UIThread.Post(() => sale.ItemsSummary = text);
                    return (revenue, cost, lines);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Загрузку отменили (окно закрыто, период сменён, пришла новая продажа) — это не
                    // ошибка; раньше каждый недогруженный чек попадал в «Журнал ошибок».
                    return (0m, 0m, new List<(string, decimal, int)>());
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Sales receipt aggregation skipped ({sale.Id}): {SaleDetailCache.Describe(ex)}", "WARNING");
                    return (0m, 0m, new List<(string, decimal, int)>());
                }
                finally
                {
                    gate.Release();
                }
            }

            // Состав грузим по всем чекам списка (он показывается в строке), а прибыль и топ
            // товаров считаем по тем же чекам, что и выручка (без продаж в долг), как у сайта.
            var results = await Task.WhenAll(sales.Select(FetchOneAsync));
            token.ThrowIfCancellationRequested();

            var dict = new Dictionary<string, (decimal revenue, int qty)>();
            decimal totalRevenueFromItems = 0m;
            decimal totalCostFromItems = 0m;
            foreach (var (sale, r) in sales.Zip(results))
            {
                if (!sale.CountsAsRevenue)
                    continue;
                totalRevenueFromItems += r.revenue;
                totalCostFromItems += r.cost;
                foreach (var (name, total, qty) in r.lines)
                {
                    dict[name] = dict.TryGetValue(name, out var existing)
                        ? (existing.revenue + total, existing.qty + qty)
                        : (total, qty);
                }
            }

            var top10 = dict.OrderByDescending(kv => kv.Value.revenue).Take(10);
            TopItems.Clear();
            foreach (var kv in top10)
                TopItems.Add(new TopItem { ProductName = kv.Key, Revenue = kv.Value.revenue, Quantity = kv.Value.qty });

            if (_serverSummary is { GrossProfit: { } serverProfit, MarginPercent: { } serverMargin })
            {
                // Как у сайта — «Валовая прибыль» и маржа сервера (см. ServerSalesSummary).
                NetProfitText.Text = $"{serverProfit:N2} {Som}";
                MarkLoss(NetProfitText, serverProfit < 0);
                MarginPercentText.Text = $"{serverMargin:F1}%";
            }
            else if (totalRevenueFromItems > 0)
            {
                var netProfit = totalRevenueFromItems - totalCostFromItems;
                NetProfitText.Text = $"{netProfit:N2} {Som}";
                MarkLoss(NetProfitText, netProfit < 0);
                MarginPercentText.Text = $"{netProfit / totalRevenueFromItems * 100:F1}%";
            }
            else
            {
                NetProfitText.Text = "—";
                MarkLoss(NetProfitText, false);
                MarginPercentText.Text = "—";
            }
        }

        // ── Фильтрация и поиск ──
        private void FilterSales(object sender, FilterEventArgs e)
        {
            // 2026-11-10: общая панель фильтров (оплата, статус, кассир, клиент, сумма — SaleListFilter, как в окне возврата).
            if (e.Item is SaleItem fs && !PassesSharedFilter(fs)) { e.Accepted = false; return; }
            if (string.IsNullOrWhiteSpace(_searchFilter)) { e.Accepted = true; return; }
            // Поиск и по номеру, и по товарам в чеке.
            if (e.Item is SaleItem s) e.Accepted = SearchMatch.Matches(_searchFilter.Trim().TrimStart('№', '#'), s.ReceiptNumber, s.ItemsSummary) // 2026-10-29: единый поиск («№1115», слова товаров в любом порядке)
                || ProductCodeMatches(s, _searchFilter.Trim()); // 2026-11-10: и по штрихкоду/артикулу товара (каталог кассы)
            else e.Accepted = false;
        }

        private async void SearchReceiptBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchFilter = SearchReceiptBox.Text;
            _searchCts?.Cancel();
            var currentCts = new CancellationTokenSource();
            _searchCts = currentCts;
            try
            {
                await Task.Delay(300, currentCts.Token);
                SalesView.Refresh();
                UpdateFilterCount(); // 2026-11-10
            }
            catch (TaskCanceledException)
            {
                PosLogger.Log("Sales search debounce superseded.", "DEBUG");
            }
            finally
            {
                if (ReferenceEquals(_searchCts, currentCts))
                    _searchCts = null;
                currentCts.Dispose();
            }
        }

        private async void SalesGrid_MouseDoubleClick(object sender, TappedEventArgs e)
        {
            if (SalesGrid.SelectedItem is SaleItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        /// <summary>Чек открывается и одним нажатием на строку (2026-09-26): двойное нажатие на
        /// сенсорном экране срабатывает через раз, и казалось, что чеки не открываются вовсе.
        /// Нажатие на заголовок или пустое место таблицы чек не открывает.</summary>
        private async void SalesGrid_Tapped(object sender, TappedEventArgs e)
        {
            // 2026-11-10: нажатие на кнопку «Возврат» в строке — не открывать чек.
            if (e.Source is Avalonia.Visual tappedVisual && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(tappedVisual, true) is not null)
                return;
            var row = Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<DataGridRow>(e.Source as Avalonia.Visual, includeSelf: true);
            if (row?.DataContext is SaleItem item)
                await ShowReceiptDetailsByIdAsync(item.Id, item.ReceiptNumber);
        }

        private SaleReceiptCardData? _receiptCard;
        private ReceiptActionBar? _popupBar;

        /// <summary>2026-10-27: панель действий под карточкой чека — создаётся один раз; «Печать» — прежняя повторная печать.</summary>
        private void EnsurePopupActionBar()
        {
            if (_popupBar != null)
                return;
            _popupBar = new ReceiptActionBar
            {
                GetData = () => _receiptCard,
                Print = () => { PrintReceiptAgain_Click(this, new RoutedEventArgs()); return Task.CompletedTask; },
                OwnerWindow = this,
                IsReady = true,
            };
            PopupActionBarHost.Content = _popupBar;
        }

        private async Task ShowReceiptDetailsByIdAsync(string receiptId, string receiptNumber)
        {
            if (string.IsNullOrEmpty(receiptId)) return;
            // Одно нажатие уже открыло этот чек — двойное не грузит его второй раз.
            if (ReceiptDetailsPopup.IsOpen && string.Equals(_currentReceiptNumber, receiptNumber, StringComparison.Ordinal)) return;
            try
            {
                JsonElement json;
                if (OfflineModeHelper.UseLocalOperations)
                {
                    var entry = OfflinePendingSalesStore.TryGetById(receiptId);
                    if (entry is null)
                    {
                        ErrorMessage = Tr.T("Локальный чек не найден.", "Жергиликтүү чек табылган жок.", "Local receipt not found.", "Yerel fiş bulunamadı.", "Mahalliy chek topilmadi.");
                        return;
                    }
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
                    json = doc.RootElement.Clone();
                }
                else
                {
                    json = await App.SalesApi.PosSaleGetAsync(receiptId, CancellationToken.None);
                }

                // 2026-09-19, временная диагностика по просьбе владельца ("печать чека с
                // мобильного через LAN-принтер") — нужно увидеть сырой ответ сервера и найти
                // поле, отличающее продажу с мобильного приложения от продажи в этой кассе
                // (source/channel/device и т.п. — сейчас в коде такое поле нигде не читается).
                // Снять после того, как найдём нужное поле и сузим до постоянной проверки.
                PosLogger.Log($"[DEBUG] Receipt {receiptNumber} raw JSON: {json.GetRawText()}", "SALE_DEBUG");

                var items = new List<string>();
                // Сервер называет поле цены "unit_price" (не "price") и использует разные имена
                // для суммы строки в разных ответах — прямое чтение "price"/"total" молча давало
                // 0.00 для обоих (кассир видел "1,000 × 0,00 = 0,00"). CartDisplayHelper уже умеет
                // это надёжно разбирать (тот же разбор, что и в диалоге возврата).
                // Предпросмотр строится тем же построителем, что и печать: раньше это были
                // два разных формата, и кассир видел на экране одно, а на бумаге другое.
                var receiptDiscount = ReadSaleDiscount(json);
                var receiptTotal = ReadSaleTotal(json);
                // 2026-10-23, владелец: «чек красивым как в мобилке» — карточка вместо моноширинного текста (печать — прежним текстом).
                // 2026-10-27: общий контрол чека «как в мобилке» + панель «Печать / QR / PDF / Отправить».
                var card = SaleReceiptCardData.Build(json, receiptNumber, receiptDiscount, receiptTotal);
                _receiptCard = card;
                PopupReceiptHost.Content = SaleReceiptCardView.Build(card);
                EnsurePopupActionBar();
                ShowPopupReturnActions(receiptId, receiptNumber, json); // 2026-11-12: «Возврат» и «Правка возвратов» — только в программе владельца

                PopupTitle.Text = Tr.T("Чек ", "Чек ", "Receipt ", "Fiş ", "Chek ") + receiptNumber;
                _currentReceiptJson = json;
                _currentReceiptNumber = receiptNumber;
                ReceiptDetailsPopup.IsOpen = true;
            }
            catch (Exception ex) { ErrorMessage = Tr.T("Не удалось загрузить детали чека: ", "Чектин чоо-жайын жүктөө мүмкүн болгон жок: ", "Could not load the receipt details: ", "Fiş ayrıntıları yüklenemedi: ", "Chek tafsilotlarini yuklab bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex); }
        }

        /// <summary>Скидка чека, как её записал сервер. Обычная скидка и списанные бонусы
        /// приходят ОДНОЙ суммой — разделить их здесь нельзя, для этого есть локальная таблица
        /// (ClientLoyaltyStore.AdjustmentsBetween).</summary>
        /// <summary>Скидка по проведённой продаже.
        ///
        /// Раньше читались ТОЛЬКО поля шапки чека (discount_total / order_discount_total), и
        /// это давало живой баг: на экране кассы скидка есть, а в чеке её нет. Причина в том,
        /// что скидку фиксированной суммой сервер у кассира отклоняет пятисоткой, и касса
        /// раскладывает её по строкам чека (StagingCartService.TrySpreadDiscountOverLinesAsync).
        /// После этого в шапке ноль, а вся скидка лежит на позициях — предпросмотр и повторная
        /// печать показывали чек так, будто скидки не было вовсе.
        ///
        /// Теперь складываем обе части: скидку на чек и сумму построчных скидок.</summary>
        internal static decimal ReadSaleDiscount(System.Text.Json.JsonElement json)
        {
            var header = 0m;
            foreach (var name in new[] { "discount_total", "order_discount_total" })
            {
                if (ReadDecimalProperty(json, name) is { } value && value > 0m)
                {
                    header = value;
                    break;
                }
            }

            // 2026-09-28, найдено проверкой копии чека: discount_total продажи — это уже ВСЯ
            // скидка чека (строки + на чек: subtotal − total). Раньше к ней прибавлялись ещё и
            // скидки строк — на копии «Сумма 225,50 / Скидка −41,00» вместо 205,00 / −20,50.
            if (header > 0m)
                return header;

            var lines = 0m;
            foreach (var item in CartDisplayHelper.EnumerateSaleLineItems(json))
            {
                foreach (var name in new[] { "discount_total", "line_discount", "discount" })
                {
                    if (ReadDecimalProperty(item, name) is { } value && value > 0m)
                    {
                        lines += value;
                        break;
                    }
                }
            }

            return header + lines;
        }

        /// <summary>Итог чека со стороны сервера — то, что покупатель реально заплатил.</summary>
        internal static decimal? ReadSaleTotal(System.Text.Json.JsonElement json) =>
            ReadDecimalProperty(json, "total");

        private static decimal? ReadDecimalProperty(System.Text.Json.JsonElement json, string name)
        {
            if (json.ValueKind != System.Text.Json.JsonValueKind.Object
                || !json.TryGetProperty(name, out var element))
            {
                return null;
            }

            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
                System.Text.Json.JsonValueKind.String when decimal.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => null,
            };
        }

        private async void PrintReceiptAgain_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Тот же построитель, что и в предпросмотре: раньше повторная печать
                // собирала собственный, третий по счёту формат чека.
                var reprintText = SaleReceiptTextBuilder.Build(
                    _currentReceiptJson,
                    _currentReceiptNumber,
                    ReadSaleDiscount(_currentReceiptJson),
                    ReadSaleTotal(_currentReceiptJson),
                    isReprint: true);

                // В фоне: медленный принтер не подвешивает окно.
                await Task.Run(() => ReceiptPrintService.PrintReceipt("{}", receiptText: reprintText)).ConfigureAwait(true);
                ErrorMessage = "";
            }
            catch (Exception ex)
            {
                ErrorMessage = Tr.T("Не удалось напечатать чек: ", "Чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt: ", "Fiş yazdırılamadı: ", "Chekni chop etib bo'lmadi: ") + ServerOutageMonitor.FriendlyError(ex);
            }
        }

        // 2026-10-03, владелец: «нужна кнопка для печати документов из чека» — товарный чек или накладная на A4.
        private void DocSalesReceipt_Click(object sender, RoutedEventArgs e) => OpenSaleDocument(SaleDocumentHtmlBuilder.Kind.SalesReceipt);

        private void DocWaybill_Click(object sender, RoutedEventArgs e) => OpenSaleDocument(SaleDocumentHtmlBuilder.Kind.Waybill);

        private void OpenSaleDocument(SaleDocumentHtmlBuilder.Kind kind)
        {
            try
            {
                SaleDocumentHtmlBuilder.OpenForPrint(_currentReceiptJson, _currentReceiptNumber, ReadSaleTotal(_currentReceiptJson), kind);
                ErrorMessage = "";
            }
            catch (Exception ex)
            {
                ErrorMessage = Tr.T("Документ не открылся: ", "Документ ачылган жок: ", "The document did not open: ", "Belge açılmadı: ", "Hujjat ochilmadi: ") + ServerOutageMonitor.FriendlyError(ex);
            }
        }

        private void Window_ManipulationBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e) =>
        e.Handled = true;

        private void CloseReceiptDetails_Click(object sender, RoutedEventArgs e) =>
            ReceiptDetailsPopup.IsOpen = false;

        // 2026-10-18, этап О3: выбранная кнопка периода — чтобы вернуть её, если окно «Спец. дата» закрыли без выбора.
        private RadioButton _quickDatePill;

        // Быстрые даты
        private async void QuickDate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            // 2026-09-17, по репорту пользователя ("в продаже не работает спец дата") — тут
            // была незаконченная заглушка ("диалог") без реализации, кнопка ничего не делала.
            // Переиспользуем тот же диалог выбора периода, что и в «Финансы».
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

        // INPC
        public new event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Вспомогательные классы
        public class TopItem
        {
            public string ProductName { get; set; }
            public decimal Revenue { get; set; }
            public int Quantity { get; set; }
        }

        public class SaleItem : INotifyPropertyChanged
        {
            private string _itemsSummary = "";

            /// <summary>Товары чека одной строкой — подгружаются после списка (см. LoadTopItemsAsync).</summary>
            public string ItemsSummary
            {
                get => _itemsSummary;
                set
                {
                    if (_itemsSummary == value)
                        return;
                    _itemsSummary = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemsSummary)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReceiptText)));
                }
            }

            /// <summary>Колонка «Чек»: номер и состав.</summary>
            public string ReceiptText => _itemsSummary.Length == 0 ? ReceiptNumber : $"{ReceiptNumber}   ·   {_itemsSummary}";

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            // 2026-09-25: дата и время строкой, а не StringFormat=HH:mm в разметке — в «Истории»
            // все чеки показывали 00:00, хотя время с сервера приходило верное.
            public string DateText => CreatedAt.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture);
            public string TimeText => CreatedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public decimal DiscountTotal { get; set; }
            public string PaymentMethod { get; set; } = "";
            public string PaymentMethodDisplay => NormalizePaymentMethodLabel(PaymentMethod);
            /// <summary>Статус продажи на сервере: paid, debt (в долг, ещё не оплачена) и т.д.</summary>
            public string Status { get; set; } = "";
            /// <summary>Входит ли чек в выручку. Как у сайта (аналитика, «Документы → Продажа»):
            /// оплаченные и частично возвращённые (полной суммой). Продажа в долг до оплаты в выручку не идёт — она видна в списке
            /// и в «Долгах», но плитки «Выручка», «Чеки», «Средний чек» и прибыль её не считают.</summary>
            public bool CountsAsRevenue => Status.Length == 0
                || string.Equals(Status, "paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Status, "partially_returned", StringComparison.OrdinalIgnoreCase);
            public bool IsRefund { get; set; }
            public string RefundReason { get; set; }

            // 2026-11-10 (возврат из «Продаж», фильтры): статус возврата, кассир и клиент строки списка.
            public string ReturnStatus { get; set; }
            public string Cashier { get; set; }
            public string ClientName { get; set; }
            public string ReturnMark => ReturnSaleLauncher.ReturnMark(Status, ReturnStatus);
            public bool HasReturnMark => ReturnMark.Length > 0;
            /// <summary>Чек можно вернуть: серверный (не локальный) и не возвращён целиком.</summary>
            public bool CanReturnRow => Id.Length > 0 && !Id.StartsWith("local-", StringComparison.Ordinal) && !ReturnSaleLauncher.IsFullyReturned(Status, ReturnStatus);
            public string ReturnTip => CanReturnRow ? null : ReturnSaleLauncher.IsFullyReturned(Status, ReturnStatus) ? ReturnSaleLauncher.FullyReturnedHint
                : Tr.T("Чек этой кассы без связи — возврат после отправки на сервер.", "Байланышсыз кассанын чеги — кайтаруу серверге жөнөтүлгөндөн кийин.",
                    "Offline receipt of this till — return after it is sent to the server.", "Bu kasanın çevrimdışı fişi — sunucuya gönderildikten sonra iade.",
                    "Aloqasiz kassa cheki — qaytarish serverga yuborilgandan keyin.");
            public string ReturnButtonName => ReturnSaleLauncher.ButtonText + " — " + ReceiptNumber;
        }

        /// <summary>Подпись валюты на экране.</summary>
        private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

        /// <summary>Читаемая подпись способа оплаты на языке интерфейса — сервер отдаёт технические ключи (cash/transfer/…).</summary>
        private static string NormalizePaymentMethodLabel(string method)
        {
            var key = (method ?? "").Trim().ToLowerInvariant();
            return key switch
            {
                "" => Tr.T("Не указано", "Көрсөтүлгөн эмес", "Not specified", "Belirtilmemiş", "Ko'rsatilmagan"),
                "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
                "transfer" or "card" => Tr.T("Перевод", "Которуу", "Transfer", "Nakitsiz", "Naqdsiz"),
                "mbank" => "MBank",
                "mixed" => Tr.T("Смешанный", "Аралаш", "Mixed", "Karışık", "Aralash"),
                "debt" => Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"),
                _ when key.Contains("нал") => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
                _ => char.ToUpperInvariant(method![0]) + method[1..],
            };
        }

        public class RefundItem
        {
            public string Id { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public decimal TotalAmount { get; set; }
            public string Reason { get; set; } = "";
        }
    }
}
