using System.Globalization;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-21, «доведи Нур Советника до 10» (О2, идея 2): один источник цифр с программой. Долги — ТОТ ЖЕ точный расчёт,
/// что «Сводка», «Клиенты» и «Долги клиентов» (DebtsView: общий кэш LastExact / RefreshSharedExactAsync, исправлено 20.10 — быстрый
/// список не вычитал оплаты через сделку: 245 856 вместо 225 856). Склад — локальный каталог программы, продажи 30 дней — тот же
/// расчёт, что «ABC-анализ». Используют: сводка советника, Telegram-бот (владельцу), утренний отчёт, напоминания должникам.</summary>
public static class OwnerShopFacts
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Должник: id клиента, имя, телефон (может быть пусто), остаток долга, чеков в долг, самый старый долг, сумма старше 30 дней.</summary>
    public sealed record DebtorFact(string ClientId, string Name, string Phone, double Amount, int Checks, DateTime Oldest, double Overdue30);

    /// <summary>Итоги долгов. Exact — точный расчёт (как «Долги клиентов»); false — быстрый список (без вычета оплат по сделкам), с «≈».</summary>
    public sealed record DebtFacts(bool Exact, DateTime AtLocal, IReadOnlyList<DebtorFact> Debtors)
    {
        public double Total => Debtors.Sum(d => d.Amount);
        public int Checks => Debtors.Sum(d => d.Checks);
        public double Overdue30 => Debtors.Sum(d => d.Overdue30);
        public int OverdueClients => Debtors.Count(d => d.Overdue30 > 0.005);
    }

    /// <summary>Долги клиентов: точный общий кэш (≤ 10 мин, долги не менялись); нет — ждём фоновый точный расчёт до
    /// <paramref name="waitExact"/>; не успел — быстрый список с пометкой Exact = false.</summary>
    public static async Task<DebtFacts> GetDebtsAsync(CancellationToken ct, TimeSpan? waitExact = null)
    {
        var exact = DebtsView.KnownExact();
        if (exact is null && CachedExact() is { } older)
        {
            // 2026-10-21 (О3): точный расчёт старше 10 минут, но долги в программе с тех пор не менялись — отвечаем по нему сразу
            // (с временем расчёта), а свежий считается в фоне: не ждать 3–12 с на каждый первый вопрос.
            _ = DebtsView.RefreshSharedExactAsync();
            exact = older;
        }
        if (exact is null)
        {
            var refresh = DebtsView.RefreshSharedExactAsync();
            await Task.WhenAny(refresh, Task.Delay(waitExact ?? TimeSpan.FromSeconds(8), ct)).ConfigureAwait(false);
            exact = DebtsView.KnownExact();
        }
        if (exact is not null)
            return new DebtFacts(true, DebtsView.LastExact?.AtUtc.ToLocalTime() ?? DateTime.Now, exact.Select(ToFact).Where(d => d.Amount > 0.005).OrderByDescending(d => d.Amount).ToList());
        PosLogger.Log("Нур Советник: точный расчёт долгов не готов — быстрый список с «≈».", "WARNING");
        var fast = await DebtsView.LoadFastDebtorsAsync(ct).ConfigureAwait(false);
        return new DebtFacts(false, DateTime.Now, fast.Select(ToFact).Where(d => d.Amount > 0.005).OrderByDescending(d => d.Amount).ToList());
    }

    /// <summary>Точный список «Долгов клиентов» не старше часа, если долги в программе с тех пор не менялись (иначе null).</summary>
    internal static List<DebtsView.Debtor>? CachedExact() =>
        DebtsView.KnownExact() ?? (DebtsView.LastExact is { } c && c.Version == PosDataEvents.DebtVersion && DateTime.UtcNow - c.AtUtc <= TimeSpan.FromMinutes(60) ? c.Debtors : null);

    private static DebtorFact ToFact(DebtsView.Debtor d)
    {
        var today = DateTime.Today;
        var overdue = d.Sales.Where(s => (today - s.At.Date).TotalDays > 30).Sum(s => s.Left);
        return new DebtorFact(d.ClientId, d.Name, d.Phone ?? "", d.Total, d.Sales.Count, d.Sales.Count > 0 ? d.Oldest : today, overdue);
    }

    /// <summary>Строка склада для анализа: остаток, продажи 30 дней (как «ABC-анализ»), в день, цена, закупка, наценка, замороженные деньги.</summary>
    public sealed record StockRow(CatalogProductTileVm Tile, string Name, double Stock, double Sold30, double PerDay, double Recent, double Before,
        double Price, double Cost, double? Markup, double Frozen)
    {
        public double DaysLeft => PerDay > 0 ? Math.Max(0, Stock) / PerDay : double.PositiveInfinity;

        /// <summary>Сколько заказать на 14 дней продаж (с учётом остатка).</summary>
        public double OrderFor14 => Math.Max(0, Math.Ceiling(PerDay * 14 - Math.Max(0, Stock)));
    }

    private static readonly SemaphoreSlim StockGate = new(1, 1);
    private static IReadOnlyList<StockRow>? _stock;
    private static DateTime _stockAtUtc = DateTime.MinValue;

    /// <summary>Склад с продажами за 30 дней (кэш 10 минут). Продажи — AnalyticsReportData (последние 15 и предыдущие 15 дней).</summary>
    public static async Task<IReadOnlyList<StockRow>> StockRowsAsync(CancellationToken ct)
    {
        if (_stock is not null && DateTime.UtcNow - _stockAtUtc < TimeSpan.FromMinutes(10))
            return _stock;
        await StockGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stock is not null && DateTime.UtcNow - _stockAtUtc < TimeSpan.FromMinutes(10))
                return _stock;
            var today = DateTime.Today;
            // 2026-10-21 (О3): два отчёта — одновременно, а не по очереди.
            var recentTask = AnalyticsReportData.BuildAsync(App.SalesApi, today.AddDays(-14), today, includeSeasonality: false, ct: ct);
            var beforeTask = AnalyticsReportData.BuildAsync(App.SalesApi, today.AddDays(-29), today.AddDays(-15), includeSeasonality: false, ct: ct);
            var recent = await recentTask.ConfigureAwait(false);
            var before = await beforeTask.ConfigureAwait(false);
            static Dictionary<string, double> SoldQty(AnalyticsReportData d)
            {
                var rows = d.Slice(AnalyticsReportData.KeyRevenue)?.Rows ?? d.Abc;
                return rows.GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity), StringComparer.OrdinalIgnoreCase);
            }
            var q2 = SoldQty(recent);
            var q1 = SoldQty(before);
            var rows = Tiles().Where(t => !t.IsService).Select(t =>
            {
                var name = t.Title.Trim();
                var r2 = q2.TryGetValue(name, out var b) ? b : 0;
                var r1 = q1.TryGetValue(name, out var a) ? a : 0;
                var price = LocalCartService.ParsePrice(t.PriceLine);
                double? markup = t.PurchasePrice > 0 ? (price - t.PurchasePrice) / t.PurchasePrice * 100 : null;
                return new StockRow(t, name, t.Quantity, r1 + r2, (r1 + r2) / 30.0, r2, r1, price, t.PurchasePrice, markup,
                    Math.Max(0, t.Quantity) * (t.PurchasePrice > 0 ? t.PurchasePrice : price));
            }).ToList();
            _stock = rows;
            _stockAtUtc = DateTime.UtcNow;
            return rows;
        }
        finally
        {
            StockGate.Release();
        }
    }

    /// <summary>Хорошо продавались 30 дней, но остаток 0.</summary>
    public static List<StockRow> SoldOut(IEnumerable<StockRow> rows, int take = 15) =>
        rows.Where(r => r.Stock <= 0 && r.Sold30 > 0).OrderByDescending(r => r.Sold30).Take(take).ToList();

    /// <summary>Популярные, запаса меньше чем на 7 дней продаж.</summary>
    public static List<StockRow> RunningLow(IEnumerable<StockRow> rows, int take = 15) =>
        rows.Where(r => r.Stock > 0 && r.PerDay > 0 && r.DaysLeft < 7).OrderBy(r => r.DaysLeft).ThenByDescending(r => r.Sold30).Take(take).ToList();

    /// <summary>Не продавались 30 дней при остатке — по замороженным деньгам.</summary>
    public static List<StockRow> Dead(IEnumerable<StockRow> rows, int take = 15) =>
        rows.Where(r => r.Stock > 0 && r.Sold30 <= 0).OrderByDescending(r => r.Frozen).Take(take).ToList();

    /// <summary>Каталог программы (локальная копия, иначе кэш каталога).</summary>
    public static IReadOnlyList<CatalogProductTileVm> Tiles()
    {
        IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
        if (tiles.Count == 0)
            tiles = CatalogCacheService.Products.ToList();
        return tiles;
    }

    public static string Money(double v) => Math.Round(v).ToString("N0", Ru) + " сом";
}
