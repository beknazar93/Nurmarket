using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-10-30, просьба владельца «в накладную — сколько % наценки добавили к товару»: сервер NurCRM в строке прихода не хранит ни наценку, ни цену продажи,
// выставленную при приходе (ТЗ Умару, пункт 13: markup_percent, sale_price, previous_sale_price). Поэтому касса при проведении прихода запоминает по каждой
// строке закупку, наценку, цену продажи после наценки и прошлую цену продажи в своём журнале %APPDATA%\NurMarketKassa\purchases\receipt-markups.json
// (атомарно, рядом с журналом закупок). Для приходов без записи (сайт, старые, другая касса) показывается РАСЧЁТНАЯ наценка от текущей цены продажи.

/// <summary>Строка прихода: что запомнили при проведении.</summary>
public sealed class ReceiptMarkupLine
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public double Qty { get; set; }
    public double Purchase { get; set; }
    /// <summary>Наценка в процентах от закупки (null — цену продажи не задавали).</summary>
    public double? MarkupPercent { get; set; }
    /// <summary>Цена продажи после наценки (с округлением), как записана в товар.</summary>
    public double Sale { get; set; }
    /// <summary>Прошлая цена продажи (до этого прихода); 0 — не было (новый товар).</summary>
    public double PreviousSale { get; set; }
    /// <summary>auto — общая наценка, manual — цену или наценку ввели руками.</summary>
    public string Mode { get; set; } = "";
}

public sealed class ReceiptMarkupDoc
{
    public string ReceiptId { get; set; } = "";
    public DateTime At { get; set; }
    public List<ReceiptMarkupLine> Lines { get; set; } = new();
}

/// <summary>Показатели строки для таблицы: наценка (запомненная или расчётная), цена продажи, прибыль с единицы.</summary>
public readonly record struct MarkupView(double? Percent, double Sale, double ProfitPerUnit, bool Estimated, bool Low, bool Below);

/// <summary>Итог по документу: средняя наценка (взвешенная по сумме закупки), ожидаемая выручка и прибыль.</summary>
public readonly record struct MarkupTotals(double? AvgPercent, double Revenue, double Profit, bool AnyEstimated, int LowCount, int BelowCount);

public static class ReceiptMarkupLog
{
    private const int Keep = 1500;
    private static readonly object Sync = new();
    private static Dictionary<string, ReceiptMarkupDoc>? _cache;

    private static string FilePath => Path.Combine(PurchasesStore.StorageDir, "receipt-markups.json");

    // ------------------------------------------------------------------ расчёт (чистые функции)

    /// <summary>Строка приёмки → запись журнала. Наценка считается от итоговых цен: (продажа / закупка − 1) × 100 — так ручная цена и общая наценка записываются одинаково.</summary>
    public static ReceiptMarkupLine FromLine(ReceivingLineVm l) => new()
    {
        ProductId = l.ProductId ?? "", Name = l.ProductName, Qty = l.Quantity, Purchase = l.PurchasePrice,
        MarkupPercent = ReceivingPricing.MarkupPercent(l.PurchasePrice, l.SalePrice), Sale = l.SalePrice, PreviousSale = l.OriginalSalePrice,
        Mode = l.SaleMode == SalePriceMode.Manual ? "manual" : l.SaleMode == SalePriceMode.Auto ? "auto" : "",
    };

    /// <summary>Показатели строки. Есть запись журнала — берём её; нет — наценка расчётная от <paramref name="currentSale"/> (текущая цена продажи товара).</summary>
    public static MarkupView View(double purchase, ReceiptMarkupLine? saved, double? currentSale)
    {
        if (saved is { Sale: > 0 } s)
        {
            var pct = s.MarkupPercent ?? ReceivingPricing.MarkupPercent(purchase > 0 ? purchase : s.Purchase, s.Sale);
            var p = purchase > 0 ? purchase : s.Purchase;
            return new MarkupView(pct, s.Sale, Math.Round(s.Sale - p, 2), false, ReceivingPricing.IsLowMargin(p, s.Sale), s.Sale < p && p > 0);
        }
        if (currentSale is { } cs && cs > 0 && purchase > 0)
            return new MarkupView(ReceivingPricing.MarkupPercent(purchase, cs), cs, Math.Round(cs - purchase, 2), true, ReceivingPricing.IsLowMargin(purchase, cs), cs < purchase);
        return new MarkupView(null, 0, 0, true, false, false);
    }

    /// <summary>Итоги: средняя наценка = (Σ продажа·кол − Σ закупка·кол) / Σ закупка·кол × 100 по строкам, где цена продажи известна.</summary>
    public static MarkupTotals Totals(IEnumerable<(double Qty, double Purchase, MarkupView View)> rows)
    {
        double purchase = 0, revenue = 0;
        var any = false;
        int low = 0, below = 0;
        foreach (var (qty, buy, view) in rows)
        {
            if (view.Sale <= 0 || buy <= 0)
                continue;
            purchase += ReceivingPricing.LineTotal(qty, buy);
            revenue += ReceivingPricing.LineTotal(qty, view.Sale);
            any |= view.Estimated;
            if (view.Below) below++;
            else if (view.Low) low++;
        }
        double? avg = purchase > 0 ? Math.Round((revenue / purchase - 1) * 100, 2) : null;
        return new MarkupTotals(avg, Math.Round(revenue, 2), Math.Round(revenue - purchase, 2), any, low, below);
    }

    public static string PercentText(double? percent, bool estimated) =>
        percent is { } p ? (estimated ? "≈ " : "") + p.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + " %" : "—";

    // ------------------------------------------------------------------ журнал

    /// <summary>Запомнить строки только что проведённого прихода. Ошибки записи журнал не роняют (приход уже на сервере).</summary>
    public static void Record(string? receiptId, IEnumerable<ReceivingLineVm> lines)
    {
        if (string.IsNullOrEmpty(receiptId))
            return;
        try
        {
            var doc = new ReceiptMarkupDoc { ReceiptId = receiptId, At = DateTime.Now, Lines = lines.Where(l => l.Quantity > 0).Select(FromLine).ToList() };
            lock (Sync)
            {
                var all = Read();
                all[receiptId] = doc;
                if (all.Count > Keep)
                    foreach (var old in all.Values.OrderBy(d => d.At).Take(all.Count - Keep).Select(d => d.ReceiptId).ToList())
                        all.Remove(old);
                Write(all);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: наценка прихода {receiptId} не записана в журнал ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-10-30 (реестр «Покупки»): весь журнал одним чтением — по записи на документ без перечитывания файла на каждую строку.</summary>
    public static IReadOnlyDictionary<string, ReceiptMarkupDoc> Snapshot()
    {
        lock (Sync)
            return Read();
    }

    public static ReceiptMarkupDoc? Get(string? receiptId)
    {
        if (string.IsNullOrEmpty(receiptId))
            return null;
        lock (Sync)
            return Read().GetValueOrDefault(receiptId);
    }

    /// <summary>Запомненная строка прихода по товару (для карточки товара → «Поступления»); null — для этого прихода наценка не записана.</summary>
    public static ReceiptMarkupLine? Line(string? receiptId, string? productId)
    {
        if (string.IsNullOrEmpty(productId))
            return null;
        return Get(receiptId)?.Lines.FirstOrDefault(l => string.Equals(l.ProductId, productId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Средняя наценка прихода по запомненным строкам — для поиска/сортировки; null — записи нет.</summary>
    public static double? AveragePercent(string? receiptId)
    {
        if (Get(receiptId) is not { } doc)
            return null;
        return Totals(doc.Lines.Select(l => (l.Qty, l.Purchase, View(l.Purchase, l, null)))).AvgPercent;
    }

    private static Dictionary<string, ReceiptMarkupDoc> Read()
    {
        if (PurchasesStore.ReadOnly)
            return _cache ??= new Dictionary<string, ReceiptMarkupDoc>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (AtomicFile.ReadAllText(FilePath) is { } text)
                return new Dictionary<string, ReceiptMarkupDoc>(JsonSerializer.Deserialize<Dictionary<string, ReceiptMarkupDoc>>(text) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: журнал наценок не прочитан ({ex.Message}) — начат заново.", "WARNING");
        }
        return new Dictionary<string, ReceiptMarkupDoc>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Write(Dictionary<string, ReceiptMarkupDoc> all)
    {
        if (PurchasesStore.ReadOnly)
        {
            _cache = all;
            return;
        }
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(all));
    }
}
