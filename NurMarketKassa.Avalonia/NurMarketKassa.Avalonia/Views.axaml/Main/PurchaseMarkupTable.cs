using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, просьба владельца «в накладную — сколько % наценки добавили»: состав прихода таблицей «Товар | Кол-во | Закупка | Наценка % | Цена продажи | Сумма»
/// и итог «средняя наценка / ожидаемая выручка / прибыль». Наценку берёт из журнала кассы (ReceiptMarkupLog) или полей сервера; нет данных —
/// расчётная «≈ N %» от текущей цены продажи товара с подсказкой. Строки с низкой маржой (&lt; 10 %) и ниже закупки подсвечены, как в таблице приёмки.
/// Показывается только ролям с правом на закупочные цены (WarehouseWindow.RevisionShowPurchase).</summary>
internal static class PurchaseMarkupTable
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Строка таблицы: показатели наценки и подписанные тексты.</summary>
    internal sealed record Row(SupplierReceiptLine Line, MarkupView View);

    /// <summary>Показатели по строкам прихода: сервер → журнал кассы → расчёт от текущей цены продажи товара.</summary>
    internal static List<Row> Rows(SupplierReceipt r)
    {
        var doc = ReceiptMarkupLog.Get(r.Id);
        var result = new List<Row>();
        foreach (var l in r.Lines)
        {
            ReceiptMarkupLine? saved = l.SalePrice is > 0
                ? new ReceiptMarkupLine { ProductId = l.ProductId, Purchase = l.PurchasePrice, Qty = l.Qty, MarkupPercent = l.MarkupPercent, Sale = l.SalePrice.Value, PreviousSale = l.PreviousSalePrice ?? 0 }
                : doc?.Lines.FirstOrDefault(x => string.Equals(x.ProductId, l.ProductId, StringComparison.OrdinalIgnoreCase));
            double? current = null;
            if (saved is null)
            {
                try
                {
                    current = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, l.ProductId, StringComparison.OrdinalIgnoreCase)) is { } tile && tile.PriceValue > 0 ? tile.PriceValue : null;
                }
                catch (InvalidOperationException)
                {
                    // каталог обновлялся в этот момент — оценку покажем при следующем открытии
                }
            }
            result.Add(new Row(l, ReceiptMarkupLog.View(l.PurchasePrice, saved, current)));
        }
        return result;
    }

    public static string EstimateHint => T("≈ — наценка расчётная: при этом приходе касса её не запоминала (приход с сайта, старый или с другой кассы), посчитана от текущей цены продажи товара.",
        "≈ — баа эсептелген: бул кирүүдө касса кошумчаны эстеген эмес (сайттан, эски же башка кассадан), товардын азыркы сатуу баасынан эсептелди.",
        "≈ — estimated: the till did not remember the markup for this delivery (from the website, old, or from another till); it is calculated from the product's current sale price.",
        "≈ — hesaplanmış: bu girişte kasa kârı kaydetmedi (siteden, eski veya başka kasadan); ürünün güncel satış fiyatından hesaplandı.",
        "≈ — hisoblangan: bu kirimda kassa ustamani eslab qolmagan (saytdan, eski yoki boshqa kassadan); mahsulotning joriy sotuv narxidan hisoblandi.");

    public static Border Build(Control host, IReadOnlyList<Row> rows, ISet<int>? highlight)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };
        var r = 0;
        void Add(string text, int col, bool header, string? brush = null, bool mark = false, string? tip = null)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = header ? 12 : 13, TextWrapping = col == 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontWeight = mark ? FontWeight.Bold : FontWeight.Normal,
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(col == 0 ? 0 : 14, header ? 0 : 5, 0, header ? 4 : 5),
            };
            if (!header && col > 0)
                t.Classes.Add("num");
            t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(brush ?? (header ? "BrushTextSoft" : "BrushText")));
            if (tip is not null)
                ToolTip.SetTip(t, tip);
            Grid.SetRow(t, r);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), 0, true);
        Add(T("Кол-во", "Саны", "Qty", "Adet", "Soni"), 1, true);
        Add(T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"), 2, true);
        Add(T("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"), 3, true);
        Add(T("Цена реализации", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotish narxi"), 4, true); // 2026-10-30: в накладной — «Цена реализации»
        Add(T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), 5, true);
        var index = 0;
        foreach (var row in rows)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = row.Line;
            var mark = highlight?.Contains(index) == true;
            if (mark)
            {
                var back = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(-6, 0) };
                back.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushAccentSoft"));
                Grid.SetRow(back, r);
                Grid.SetColumnSpan(back, 6);
                grid.Children.Add(back);
            }
            var v = row.View;
            // ниже закупки — красным, маржа < 10 % — жёлтым (как строки таблицы приёмки)
            var flag = v.Below ? "BrushDanger" : v.Low ? "BrushWarning" : null;
            var flagTip = v.Below ? T("Цена продажи ниже закупки", "Сатуу баасы сатып алуудан төмөн", "Sale price is below purchase", "Satış fiyatı alıştan düşük", "Sotuv narxi xariddan past")
                : v.Low ? T("Низкая маржа (меньше 10 %)", "Маржа төмөн (10 %дан аз)", "Low margin (under 10 %)", "Düşük marj (%10'dan az)", "Marja past (10 % dan kam)") : null;
            Add(l.ProductName + (l.ReturnedQty > 0 ? T($" (возвр. {PurchasesWindow.Q(l.ReturnedQty)})", $" (кайт. {PurchasesWindow.Q(l.ReturnedQty)})", $" (ret. {PurchasesWindow.Q(l.ReturnedQty)})", $" (iade {PurchasesWindow.Q(l.ReturnedQty)})", $" (qayt. {PurchasesWindow.Q(l.ReturnedQty)})") : ""),
                0, false, null, mark);
            Add(PurchasesWindow.Q(l.Qty) + " " + (l.Unit.Length > 0 ? l.Unit : "шт"), 1, false, null, mark);
            Add(MoneyText.Som(l.PurchasePrice), 2, false, null, mark);
            Add(ReceiptMarkupLog.PercentText(v.Percent, v.Estimated), 3, false, flag, mark, v.Estimated ? EstimateHint : flagTip);
            Add(v.Sale > 0 ? MoneyText.Som(v.Sale) : "—", 4, false, flag, mark, flagTip);
            Add(MoneyText.Som(l.Qty * l.PurchasePrice), 5, false, null, mark);
            index++;
        }
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(grid);

        var totals = ReceiptMarkupLog.Totals(rows.Select(x => (x.Line.Qty, x.Line.PurchasePrice, x.View)));
        if (totals.AvgPercent is { } avg)
        {
            var approx = totals.AnyEstimated ? "≈ " : "";
            var sum = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                Text = T($"Средняя наценка: {approx}{avg.ToString("0.##", Ru)} % · Ожидаемая выручка: {approx}{MoneyText.Som(totals.Revenue)} · Ожидаемая прибыль: {approx}{MoneyText.Som(totals.Profit)}",
                    $"Орточо кошумча: {approx}{avg.ToString("0.##", Ru)} % · Күтүлгөн түшүм: {approx}{MoneyText.Som(totals.Revenue)} · Күтүлгөн пайда: {approx}{MoneyText.Som(totals.Profit)}",
                    $"Average markup: {approx}{avg.ToString("0.##", Ru)} % · Expected revenue: {approx}{MoneyText.Som(totals.Revenue)} · Expected profit: {approx}{MoneyText.Som(totals.Profit)}",
                    $"Ortalama kâr: {approx}{avg.ToString("0.##", Ru)} % · Beklenen ciro: {approx}{MoneyText.Som(totals.Revenue)} · Beklenen kâr: {approx}{MoneyText.Som(totals.Profit)}",
                    $"O'rtacha ustama: {approx}{avg.ToString("0.##", Ru)} % · Kutilgan tushum: {approx}{MoneyText.Som(totals.Revenue)} · Kutilgan foyda: {approx}{MoneyText.Som(totals.Profit)}"),
            };
            sum.Margin = new Thickness(0, 6, 0, 0);
            sum.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushText"));
            stack.Children.Add(sum);
            if (totals.BelowCount + totals.LowCount > 0)
            {
                var warn = new TextBlock
                {
                    FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                    Text = T($"Ниже закупки: {totals.BelowCount} · маржа ниже 10 %: {totals.LowCount}", $"Сатып алуудан төмөн: {totals.BelowCount} · маржа 10 %дан аз: {totals.LowCount}",
                        $"Below purchase: {totals.BelowCount} · margin under 10 %: {totals.LowCount}", $"Alışın altında: {totals.BelowCount} · marj %10'un altı: {totals.LowCount}",
                        $"Xariddan past: {totals.BelowCount} · marja 10 % dan kam: {totals.LowCount}"),
                };
                warn.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(totals.BelowCount > 0 ? "BrushDanger" : "BrushWarning"));
                stack.Children.Add(warn);
            }
            if (totals.AnyEstimated)
            {
                var hint = new TextBlock { Text = EstimateHint, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                hint.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
                stack.Children.Add(hint);
            }
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Margin = new Thickness(0, 4, 0, 0), Child = stack };
        box.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushPanelSoft"));
        return box;
    }
}
