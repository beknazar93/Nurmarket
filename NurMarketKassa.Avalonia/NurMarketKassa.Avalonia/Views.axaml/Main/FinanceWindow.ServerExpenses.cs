using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-10-20, живой аудит владельца (КРИТИЧНО): «Аналитика» за октябрь показывала чистую прибыль +588 428
    /// (= валовой), а «Прибыль и деньги» — −466 643: расходы здесь брались из изъятий денежного ящика ЭТОГО компьютера
    /// (у программы владельца их нет — 0), а не из расходов NurCRM (зарплата 50 000 + прочие 1 005 071). Теперь расходы
    /// и чистая прибыль — из того же отчёта сервера, что «Прибыль и деньги» (GET api/main/analytics/pnl/: opex.total,
    /// operating_profit). Сервер не ответил — как раньше, изъятия из кассы, с подписью, что это только изъятия.</summary>
    public partial class FinanceWindow
    {
        /// <summary>Расходы периода с сервера: всего, зарплата, прочее (остальные статьи), чистая прибыль сервера.</summary>
        private sealed record ServerExpenses(decimal Total, decimal Salaries, decimal Other, decimal? OperatingProfit, decimal? GrossProfit);

        private ServerExpenses _serverExpenses;
        /// <summary>Отчёт расходов запрошен, ответа ещё нет — в плитках «…».</summary>
        private bool _expensesPending;
        /// <summary>Последняя показанная валовая прибыль — чистая пересчитывается, когда придут расходы.</summary>
        private decimal _shownGross;

        private async Task ApplyServerExpensesAsync(Task<ServerExpenses> task, CancellationToken ct)
        {
            ServerExpenses result = null;
            try
            {
                result = await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Аналитика: расходы не применены: {ex.Message}", "WARNING");
            }
            if (ct.IsCancellationRequested)
                return;
            _serverExpenses = result;
            _expensesPending = false;
            ShowExpenses(_shownGross);
        }

        private static async Task<ServerExpenses> FetchServerExpensesAsync(DateTime from, DateTime to, CancellationToken ct)
        {
            try
            {
                var query = new Dictionary<string, string>
                {
                    ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                };
                var api = App.GetRequiredService<NurMarketApiClient>();
                var data = await api.RequestAsync(HttpMethod.Get, "api/main/analytics/pnl/", null, query, ct, TimeSpan.FromSeconds(60))
                    .ConfigureAwait(true);
                if (data is not { ValueKind: JsonValueKind.Object } d
                    || !d.TryGetProperty("opex", out var opex) || opex.ValueKind != JsonValueKind.Object
                    || PnlNum(opex, "total") is not { } total)
                    return null;
                var salaries = PnlNum(opex, "salaries") ?? 0m;
                return new ServerExpenses(total, salaries, total - salaries, PnlNum(d, "operating_profit"), PnlNum(d, "gross_profit"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Аналитика: расходы с сервера (pnl) не получены: {ex.Message}", "WARNING");
                return null;
            }
        }

        /// <summary>2026-10-20, аудит владельца («Сезонность» по неполной истории кассы с 16.09): выручка последних 12 месяцев
        /// с сервера — те же карточки, что «Сводка» за месяц (analytics/market/summary/ одним запросом; не знает адреса —
        /// отчёт на каждый месяц, по 3 одновременно). Индекс — номер месяца (0 = январь); null — за месяц ответа нет;
        /// весь результат null — сервер не ответил совсем (остаются столбики по истории кассы).</summary>
        private static async Task<decimal?[]> FetchServerMonthTotalsAsync(DateTime today, CancellationToken ct)
        {
            var ranges = new List<(DateTime From, DateTime To)>();
            var monthStart = new DateTime(today.Year, today.Month, 1);
            for (var back = 11; back >= 0; back--)
            {
                var start = monthStart.AddMonths(-back);
                ranges.Add((start, back == 0 ? today.Date : start.AddMonths(1).AddDays(-1)));
            }

            var result = new decimal?[12];
            try
            {
                if (await App.SalesApi.MarketSummaryCardsAsync(ranges, ct).ConfigureAwait(true) is { } cards && cards.Count == ranges.Count)
                {
                    for (var i = 0; i < ranges.Count; i++)
                        result[ranges[i].From.Month - 1] = PnlNum(cards[i], "revenue");
                    return result;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Сезонность: итоги месяцев одним запросом не получены ({ex.Message}) — по отчёту на месяц.", "WARNING");
            }

            using var gate = new SemaphoreSlim(3);
            var any = false;
            var tasks = ranges.Select(async r =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(true);
                try
                {
                    var report = await App.SalesApi.MarketSalesReportAsync(r.From, r.To, ct).ConfigureAwait(true);
                    if (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("cards", out var c) && PnlNum(c, "revenue") is { } rev)
                    {
                        result[r.From.Month - 1] = rev;
                        any = true;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Сезонность: выручка за {r.From:MM.yyyy} не получена: {ex.Message}", "WARNING");
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(true);
            return any ? result : null;
        }

        private static decimal? PnlNum(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
                return null;
            var text = v.ValueKind switch
            {
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.String => v.GetString(),
                _ => null,
            };
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : null;
        }

        /// <summary>Плитки «Расходы» и «Чистая прибыль». grossProfit — уже показанная «Валовая прибыль» окна.</summary>
        private void ShowExpenses(decimal grossProfit)
        {
            _shownGross = grossProfit;
            if (_expensesPending)
            {
                if (ExpensesText != null)
                    ExpensesText.Text = "…";
                if (ExpensesBreakdownText != null)
                    ExpensesBreakdownText.Text = Tr.T("загружаю расходы NurCRM…", "NurCRM чыгымдары жүктөлүүдө…", "loading NurCRM expenses…",
                        "NurCRM giderleri yükleniyor…", "NurCRM xarajatlari yuklanmoqda…");
                if (ProfitAfterExpensesText != null)
                    ProfitAfterExpensesText.Text = "…";
                return;
            }

            decimal expenses;
            string breakdown;
            if (_serverExpenses is { } srv)
            {
                expenses = srv.Total;
                breakdown = srv.Total <= 0m
                    ? Tr.T("расходов в NurCRM нет", "NurCRMде чыгым жок", "no expenses in NurCRM", "NurCRM'de gider yok", "NurCRMda xarajat yo'q")
                    : Tr.T($"зарплата {srv.Salaries:N2} · прочие {srv.Other:N2} сом (NurCRM)",
                        $"эмгек акы {srv.Salaries:N2} · башка {srv.Other:N2} сом (NurCRM)",
                        $"salaries {srv.Salaries:N2} · other {srv.Other:N2} som (NurCRM)",
                        $"maaşlar {srv.Salaries:N2} · diğer {srv.Other:N2} som (NurCRM)",
                        $"ish haqi {srv.Salaries:N2} · boshqa {srv.Other:N2} so'm (NurCRM)");
            }
            else
            {
                // Сервер не ответил — как раньше: деньги, вынутые из кассы этого компьютера (честно подписано).
                var (deposits, withdrawals) = CashOperationsForPeriod(_displayFrom, _displayTo);
                expenses = withdrawals;
                breakdown = Tr.T("расходы NurCRM не получены — показаны только изъятия из кассы этого компьютера",
                        "NurCRM чыгымдары алынган жок — ушул компьютердин кассасынан алынганы гана көрсөтүлдү",
                        "NurCRM expenses not received — only cash taken out of this computer's till is shown",
                        "NurCRM giderleri alınamadı — yalnızca bu bilgisayarın kasasından alınan para gösteriliyor",
                        "NurCRM xarajatlari olinmadi — faqat shu kompyuter kassasidan olingan pul ko'rsatildi")
                    + (deposits > 0m
                        ? "; " + Tr.T($"внесено: {deposits:N2} сом", $"салынды: {deposits:N2} сом", $"cash in: {deposits:N2} som", $"para girişi: {deposits:N2} som", $"kiritildi: {deposits:N2} so'm")
                        : "");
            }

            if (ExpensesText != null)
                ExpensesText.Text = $"{expenses:N2} {Som}";
            if (ExpensesBreakdownText != null)
                ExpensesBreakdownText.Text = breakdown;
            if (ProfitAfterExpensesText != null)
            {
                var net = grossProfit - expenses;
                ProfitAfterExpensesText.Text = $"{net:N2} {Som}";
                MarkLoss(ProfitAfterExpensesText, net < 0);
            }
        }
    }
}
