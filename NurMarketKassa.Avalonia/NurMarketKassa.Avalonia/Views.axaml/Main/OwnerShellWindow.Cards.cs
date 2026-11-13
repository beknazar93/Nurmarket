using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15 — часть класса OwnerShellWindow (разбор огромного файла на partial-файлы), логика не менялась; остальное — OwnerShellWindow.axaml.cs
public partial class OwnerShellWindow
{
    /// <summary>2026-10-18, этап О2: меньше этой суммы день выбросом на графике не считается.</summary>
    private const double ChartOutlierMinSom = 5000;

    private void ApplyChart(JsonElement report, DateTime from, DateTime to)
    {
        ChartBars.Children.Clear();
        ChartBars.ColumnDefinitions.Clear();
        ChartBars.RowDefinitions = new RowDefinitions("*,Auto");
        ChartLegend.Children.Clear();

        var byDay = new Dictionary<DateTime, double>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("sales_dynamics", out var dynamics)
            && dynamics.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in dynamics.EnumerateArray())
            {
                if (DateTime.TryParseExact(Str(point, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    byDay[day.Date] = Num(point, "value");
            }
        }

        var days = new List<DateTime>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            days.Add(d);

        var values = days.Select(d => byDay.TryGetValue(d, out var v) ? v : 0).ToList();
        var max = values.Count == 0 ? 0 : values.Max();
        var total = values.Sum();
        // 2026-10-05: выручка по дням — «ИИ-советнику» (OwnerOverviewSnapshot).
        OwnerOverviewSnapshot.Set("2-chart",
            $"Выручка по дням с {from:dd.MM} по {to:dd.MM} (сервер NurCRM): " +
            string.Join(", ", days.Select((d, i) => $"{d:dd.MM} — {values[i]:0.##}")) + $"; итого {total:0.##} сом.");
        ChartTotalText.Text = Tr.T($"Итого: {Whole(total)} {Som()}", $"Жыйынтык: {Whole(total)} {Som()}", $"Total: {Whole(total)} {Som()}",
            $"Toplam: {Whole(total)} {Som()}", $"Jami: {Whole(total)} {Som()}");
        ChartEmptyText.IsVisible = max <= 0;
        ChartBars.IsVisible = max > 0;
        if (max <= 0)
            return;

        var culture = UiCulture;
        var few = days.Count <= 10;
        // 2026-10-18, этап О1 (макет Owner): столбцы — цвет текста темы (тёмно-синий), «сегодня» — жёлтый с подписью «сегодня»;
        // подписи — в тыс. сом с одним знаком (6,1 / 51,4). Выброс (столбец больше 3 медиан остальных дней, у владельца 16.10 —
        // 51 тыс. при обычных 3–7) рисуется во всю высоту с отметкой разрыва, а остальные дни — в своей шкале, чтобы их было видно.
        const double barArea = 170;
        var outlier = new bool[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var others = values.Where((v, j) => j != i && v > 0).OrderBy(v => v).ToList();
            if (others.Count >= 2)
            {
                var median = others.Count % 2 == 1 ? others[others.Count / 2] : (others[others.Count / 2 - 1] + others[others.Count / 2]) / 2;
                // 2026-10-18, этап О2 (решение главной сессии): выброс — только день ≥ 3 медиан И ≥ 5 000 сом; при малых суммах
                // (870 сом при медиане ~110) обычный день рисовался с ложным разрывом.
                // 2026-10-22 (аудит 1.17.59, П-З): разрыв оси убран — 15,4 тыс. и 1,8 тыс. на нём выглядели почти одной высоты. Столбцы
                // пропорциональны (ось от нуля): высота = значение / максимум, маленький день виден минимумом 4 px.
                outlier[i] = false && values[i] >= 3 * median && values[i] >= ChartOutlierMinSom;
            }
        }
        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах»: выручка по дням — временной ряд: столбцы одного оттенка, лучший день — акцентом
        // (жёлтый), самый слабый — приглушённо, сегодня — кольцом (было: «сегодня» жёлтым), внизу легенда.
        var (bestDays, worstDays) = ChartPalette.SeriesMarks(values);
        var scaleMax = values.Where((_, i) => !outlier[i]).DefaultIfEmpty(0).Max();
        var capped = outlier.Any(o => o) && scaleMax > 0;
        if (!capped)
            scaleMax = max;
        for (var i = 0; i < days.Count; i++)
        {
            ChartBars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var day = days[i];
            var value = values[i];
            var isToday = day == ServerClock.Today;
            var isOutlier = capped && outlier[i];

            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (few && value > 0)
            {
                var valueText = new TextBlock
                {
                    // 2026-10-18, ревью О1: меньше 100 сом — «<0,1», а не «0,0».
                    Text = value < 100 ? "<" + 0.1.ToString("N1", culture) : (value / 1000).ToString("N1", culture),
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 5),
                    FontWeight = FontWeight.SemiBold,
                    Classes = { "num" },
                };
                UseBrush(valueText, TextBlock.ForegroundProperty, "BrushText");
                column.Children.Add(valueText);
            }

            // Выбросов несколько (51,4 и 15,1) — они различаются хотя бы немного: верхние 12 % высоты — по их размеру.
            var height = value <= 0 ? 2
                : isOutlier ? barArea * (0.88 + 0.12 * value / max)
                : ChartBarHeight.For(value, scaleMax, capped ? barArea * 0.8 : barArea); // 2026-11-06 (Д4-41): столбец дня с продажами не уже 8 px (был минимум 4)
            var bar = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(6, 6, 2, 2),
            };
            if (value <= 0)
                UseBrush(bar, Border.BackgroundProperty, "BrushBorder");
            ToolTip.SetTip(bar, $"{day.ToString("ddd, d MMMM", culture)} — {Amount(value)} {Som()}"
                + (bestDays.Contains(i) ? " · " + Tr.T("лучший день", "эң мыкты күн", "best day", "en iyi gün", "eng yaxshi kun")
                    : worstDays.Contains(i) ? " · " + Tr.T("самый слабый день", "эң начар күн", "weakest day", "en zayıf gün", "eng sust kun") : ""));
            Control barView = value > 0 ? BarChartRenderer.StyleSeriesBar(bar, this, bestDays.Contains(i), worstDays.Contains(i), isToday) : bar;
            if (isOutlier)
            {
                // Отметка разрыва: косая полоса цвета карточки поперёк столбца.
                var gap = new Border
                {
                    Height = 6,
                    Margin = new Thickness(-4, barArea * 0.28, -4, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    RenderTransform = new RotateTransform(-12),
                    IsHitTestVisible = false,
                };
                UseBrush(gap, Border.BackgroundProperty, "BrushPanel");
                barView = new Panel { Children = { bar, gap } };
            }
            barView.Margin = new Thickness(few ? 12 : 2, 0);
            column.Children.Add(barView);
            Grid.SetColumn(column, i);
            ChartBars.Children.Add(column);

            var label = new TextBlock
            {
                Text = few
                    ? (isToday ? Tr.T("сегодня", "бүгүн", "today", "bugün", "bugun") : day.ToString("ddd", culture)) + $"\n{day.Day}"
                    : day.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = few ? 12.5 : 11,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal,
            };
            UseBrush(label, TextBlock.ForegroundProperty, isToday ? "BrushText" : "BrushTextSoft");
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            ChartBars.Children.Add(label);
        }

        if (BarChartRenderer.SeriesLegend(this, bestDays.Count > 0, worstDays.Count > 0, days.Contains(ServerClock.Today),
                Tr.T("Лучший день", "Эң мыкты күн", "Best day", "En iyi gün", "Eng yaxshi kun"),
                Tr.T("Самый слабый день", "Эң начар күн", "Weakest day", "En zayıf gün", "Eng sust kun"),
                Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")) is { } legend)
            ChartLegend.Children.Add(legend);
    }

    private void ApplyPayments(JsonElement report)
    {
        PaymentsBar.Children.Clear();
        PaymentsBar.ColumnDefinitions.Clear();
        PaymentsLegend.Children.Clear();

        var methods = new List<(string Method, double Count, double Total)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("payment_methods", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                var total = Num(m, "total");
                if (total > 0)
                    methods.Add((Str(m, "method"), Num(m, "count"), total));
            }
        }

        methods.Sort((a, b) => b.Total.CompareTo(a.Total));
        var sum = methods.Sum(m => m.Total);
        PaymentsCountText.Text = sum > 0 ? $"{Whole(sum)} {Som()}" : "";
        PaymentsEmptyText.IsVisible = methods.Count == 0;

        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — у каждого способа оплаты свой цвет из общей палитры (ChartPalette) по ключу способа,
        // тот же, что в «Финансах» и мобилке (с 2026-10-18, этап О1, способы различались оттенком цвета текста).
        var methodColors = ChartPalette.Assign(methods.Select(m => (string?)ChartPalette.PaymentKey(m.Method)).ToList());
        for (var i = 0; i < methods.Count; i++)
        {
            var (method, count, total) = methods[i];
            var share = sum > 0 ? total / sum : 0;

            PaymentsBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(share, 0.004), GridUnitType.Star));
            var segment = new Border
            {
                Background = Brush.Parse(methodColors[i]),
                Margin = new Thickness(0, 0, i < methods.Count - 1 ? 2 : 0, 0),
            };
            ToolTip.SetTip(segment, $"{PaymentLabel(method)}: {Whole(total)} {Som()}");
            Grid.SetColumn(segment, i);
            PaymentsBar.Children.Add(segment);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Brush.Parse(methodColors[i]), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);

            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var nameRun = new Run(PaymentLabel(method)) { FontSize = 14 };
            UseBrush(nameRun, TextElement.ForegroundProperty, "BrushText", name);
            var countRun = new Run("  " + ChecksWord(count)) { FontSize = 13 };
            UseBrush(countRun, TextElement.ForegroundProperty, "BrushTextSoft", name);
            name.Inlines = new InlineCollection { nameRun, countRun };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var amount = new TextBlock { Text = $"{Whole(total)} {Som()}", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "num" } };
            UseBrush(amount, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);

            var percent = new TextBlock
            {
                Text = Percent(Math.Round(share * 100), (share * 100).ToString("0", CultureInfo.InvariantCulture)),
                FontSize = 13,
                MinWidth = 52,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "num" },
            };
            UseBrush(percent, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(percent, 3);
            row.Children.Add(percent);

            // 2026-10-05: «В долг» — по нажатию подробные долги клиентов.
            if (string.Equals(method?.Trim(), "debt", StringComparison.OrdinalIgnoreCase) && TariffGate.CanUseDebts)
            {
                row.Background = Brushes.Transparent;
                row.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(row, Tr.T("Подробно: кто и сколько должен", "Кененирээк: ким канча карыз", "Details: who owes how much", "Ayrıntılar: kim ne kadar borçlu", "Batafsil: kim qancha qarz"));
                nameRun.TextDecorations = TextDecorations.Underline;
                row.PointerPressed += (_, _) => OpenDebts();
            }

            PaymentsLegend.Children.Add(row);
        }

        // Возвраты — как на сайте: «Документы» → «Возврат продажи».
        double returnsCount = 0, returnsSum = 0;
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("documents", out var docs)
            && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                if (string.Equals(Str(doc, "name"), "Возврат продажи", StringComparison.OrdinalIgnoreCase))
                {
                    returnsCount = Num(doc, "count");
                    returnsSum = Num(doc, "sum");
                }
            }
        }

        // 2026-09-28 (BE-09): список возвратов сервера за этот же период — если он получен.
        var (rangeFrom, rangeTo) = CurrentRange();
        if (_lastReturns is { } listed && _lastReturnsKey == RangeKey(rangeFrom, rangeTo))
        {
            returnsCount = listed.Count;
            returnsSum = (double)listed.Sum;
        }

        // 2026-10-18, этап О1: «2 чека на 450 сом» вместо склейки «2 · 450 сом».
        ReturnsValue.Text = returnsCount > 0
            ? Tr.T($"{ChecksWord(returnsCount)} на {Whole(returnsSum)} {Som()}", $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}",
                $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}", $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}",
                $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}")
            : "0";
    }

    private void ApplyTopProducts(JsonElement report)
    {
        TopList.Children.Clear();
        var items = new List<(string Name, double Sold, double Revenue)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("top_products", out var top)
            && top.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in top.EnumerateArray())
                items.Add((Str(p, "name"), Num(p, "sold"), Num(p, "revenue")));
        }

        items = items.Where(i => i.Revenue > 0).OrderByDescending(i => i.Revenue).Take(5).ToList();
        // 2026-10-05: лучшие товары периода — «ИИ-советнику» (OwnerOverviewSnapshot).
        OwnerOverviewSnapshot.Set("3-top",
            $"Лучшие товары за период «{PeriodNameForAi()}» (сервер NurCRM): " +
            (items.Count == 0 ? "продаж нет" : string.Join("; ", items.Select(i => $"{i.Name} — продано {i.Sold:0.###}, выручка {i.Revenue:0.##} сом"))) + ".");
        TopEmptyText.IsVisible = items.Count == 0;
        var max = items.Count == 0 ? 0 : items.Max(i => i.Revenue);
        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — у каждого товара свой цвет по названию (ChartPalette): плашка места и полоса доли
        // (раньше — нейтральные); цвет товара тот же на других экранах и в мобилке.
        var productColors = ChartPalette.Assign(items.Select(i => (string?)i.Name).ToList());

        for (var i = 0; i < items.Count; i++)
        {
            var (name, sold, revenue) = items[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            // 2026-10-18, этап О1: место — нейтральной плашкой у всех (жёлтый — только у «сегодня» и главной кнопки).
            var rank = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = Brush.Parse(productColors[i]) };
            var rankText = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rankText.Foreground = ChartPalette.OnColorIsDark(productColors[i]) ? Brushes.Black : Brushes.White; // контраст ≥ 4,5:1 на любом цвете палитры
            rank.Child = rankText;
            row.Children.Add(rank);

            var middle = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            UseBrush(title, TextBlock.ForegroundProperty, "BrushText");
            middle.Children.Add(title);

            var share = max > 0 ? revenue / max : 0;
            var track = new Border { Height = 5, CornerRadius = new CornerRadius(3), ClipToBounds = true };
            UseBrush(track, Border.BackgroundProperty, "BrushInputAlt");
            var fillGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(Math.Max(share, 0.01), GridUnitType.Star),
                    new ColumnDefinition(Math.Max(1 - share, 0.0001), GridUnitType.Star),
                },
            };
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = Brush.Parse(productColors[i]) };
            fillGrid.Children.Add(fill);
            track.Child = fillGrid;
            middle.Children.Add(track);
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var revenueText = new TextBlock { Text = $"{Whole(revenue)} {Som()}", FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Classes = { "num" } };
            UseBrush(revenueText, TextBlock.ForegroundProperty, "BrushText");
            var soldText = new TextBlock
            {
                Text = Tr.T($"продано {Qty(sold)}", $"{Qty(sold)} сатылды", $"sold {Qty(sold)}", $"satılan: {Qty(sold)}", $"sotildi {Qty(sold)}"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            UseBrush(soldText, TextBlock.ForegroundProperty, "BrushTextSoft");
            right.Children.Add(revenueText);
            right.Children.Add(soldText);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            TopList.Children.Add(row);
        }

        ApplyLowStock();
    }

    /// <summary>2026-09-30: «Заканчивается на складе» — товары с самым малым остатком из каталога
    /// программы (без услуг: у них остатка нет). Пересчитывается вместе со сводкой; каталог уже в
    /// памяти, запросов к серверу нет. 2026-10-18, этап О1: показываются строками блока «Требует внимания»
    /// (OwnerShellWindow.Attention.cs) — три первых и «ещё N», действие «Пополнение ›».</summary>
    private void ApplyLowStock()
    {
        _attnStock.Clear();
        _attnStockMore = 0;
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> low;
        try
        {
            low = CatalogCacheService.Products
                // 2026-10-27, аудит владельца С-7: порог «мало» один на программу — тот же, что у «Склада» и аналитики склада
                // (меньше 10, нулевые — «Нет в наличии»). Было «5 и меньше»: на Сводке 81 товар, на Складе 158.
                .Where(p => !p.IsService && !string.IsNullOrWhiteSpace(p.Title) && p.Quantity < NurMarketKassa.Services.StockSyncService.LowStockThreshold)
                .OrderBy(p => p.Quantity)
                .ThenBy(p => p.Title)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка: список «Заканчивается» не построен ({ex.Message}).", "WARNING");
            low = [];
        }

        foreach (var p in low.Take(AttentionStockRows))
            _attnStock.Add(new AttentionStock(p.Title, p.Quantity, p.Unit));
        _attnStockMore = Math.Max(0, low.Count - AttentionStockRows);
        _attnStockLoaded = true;

        ApplyLowSizes();
        RenderAttention();
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-74): в сфере «Одежда» под товарами — «Заканчиваются размеры»:
    /// размер/цвет, которого осталось 0–1 шт., когда другие размеры модели ещё есть. Остатки размеров — из общего справочника
    /// касс (VariantBarcodeIndex, обновляется в фоне), запросов к серверу здесь нет.</summary>
    private void ApplyLowSizes()
    {
        if (!MarketSpheres.IsClothing)
            return;
        List<VariantBarcodeIndex.LowSize> low;
        try
        {
            // 2026-10-18, ревью О1: весь список — «ещё N» считается по нему, а строк в блоке не больше AttentionStockRows всего.
            low = VariantBarcodeIndex.LowSizes(1000);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка: «Заканчиваются размеры» не построено ({ex.Message}).", "WARNING");
            return;
        }
        if (low.Count == 0)
            return;
        var titles = CatalogCacheService.Products.ToList().GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var room = Math.Max(0, AttentionStockRows - _attnStock.Count);
        foreach (var s in low.Take(room))
        {
            var label = string.Join(", ", new[] { s.Size, s.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
            // 2026-11-06: удалённый товар — название из памяти об удалённых, а не «—»
            var name = (titles.TryGetValue(s.ProductId, out var t) ? t : RemovedProductMemory.Find(s.ProductId)?.Title ?? "—") + (label.Length > 0 ? " — " + label : "");
            _attnStock.Add(new AttentionStock(name, s.Quantity, null));
        }
        _attnStockMore += Math.Max(0, low.Count - room);
    }

    // 2026-09-28, сверка с сайтом: в «Последних продажах» был виден только первый товар чека
    // (first_item_name) — чек из трёх позиций выглядел как продажа одного товара. Списка позиций в
    // списке продаж сервер не отдаёт (у сайта там тоже только первый товар), поэтому число позиций
    // берём из деталей чека: SaleDetailCache качает каждый чек один раз за сеанс, при
    // 20-секундном обновлении запрашиваются только новые чеки.
    private readonly Dictionary<string, int> _saleItemCounts = new(StringComparer.OrdinalIgnoreCase);
    private List<JsonElement>? _recentShown;
    private bool _itemCountsLoading;

    private async Task LoadRecentItemCountsAsync()
    {
        if (_itemCountsLoading || _recentShown is not { } rows)
            return;

        var ids = rows
            .Where(r => Num(r, "items_count") <= 0)
            .Select(r => Str(r, "id"))
            .Where(id => id.Length > 0 && !_saleItemCounts.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return;

        _itemCountsLoading = true;
        var changed = false;
        try
        {
            foreach (var id in ids)
            {
                if (_cts.IsCancellationRequested || _loggingOut)
                    return;
                try
                {
                    var detail = await SaleDetailCache.GetAsync(id, _cts.Token).ConfigureAwait(true);
                    _saleItemCounts[id] = detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                            ? items.GetArrayLength()
                            : 0;
                    changed = true;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Без состава чек просто покажется без «+ ещё N» — повторно не спрашиваем.
                    _saleItemCounts[id] = 0;
                    PosLogger.Log($"Owner app: состав чека {id} не получен: {ex.Message}", "DEBUG");
                }
            }
        }
        finally
        {
            _itemCountsLoading = false;
        }

        if (changed && _recentShown != null)
            ApplyRecent(_recentShown);
    }

    private void ApplyRecent(List<JsonElement> rows)
    {
        RecentList.Children.Clear();
        var shown = rows.Take(RecentRows).ToList();
        _recentShown = shown;
        RecentEmptyText.IsVisible = shown.Count == 0;

        for (var i = 0; i < shown.Count; i++)
        {
            var sale = shown[i];
            var canceled = string.Equals(Str(sale, "status"), "canceled", StringComparison.OrdinalIgnoreCase);
            var method = Str(sale, "payment_method");

            // 2026-10-18, этап О1: без линий между строками; способ оплаты — серой строкой под суммой, без цветной точки.
            var line = new Border { Padding = new Thickness(0, 8) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto") };

            var created = Str(sale, "created_at");
            var time = DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? dto.LocalDateTime.ToString(dto.LocalDateTime.Date == ServerClock.Today ? "HH:mm" : "dd.MM", CultureInfo.InvariantCulture)
                : "";
            var timeText = new TextBlock { Text = time, FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Classes = { "num" } };
            UseBrush(timeText, TextBlock.ForegroundProperty, "BrushTextSoft");
            row.Children.Add(timeText);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
            var itemName = Str(sale, "first_item_name");
            var title = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var nameRun = new Run(string.IsNullOrWhiteSpace(itemName) ? Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv") : itemName);
            UseBrush(nameRun, TextElement.ForegroundProperty, "BrushText", title);
            var titleInlines = new InlineCollection { nameRun };
            var saleId = Str(sale, "id");
            var itemsCount = (int)Math.Round(Num(sale, "items_count"));
            if (itemsCount <= 0 && saleId.Length > 0 && _saleItemCounts.TryGetValue(saleId, out var known))
                itemsCount = known;
            if (itemsCount > 1 && !string.IsNullOrWhiteSpace(itemName))
            {
                var more = itemsCount - 1;
                var moreRun = new Run(Tr.T($"  + ещё {more}", $"  + дагы {more}", $"  + {more} more", $"  + {more} ürün daha", $"  + yana {more} ta"))
                {
                    FontWeight = FontWeight.Normal,
                    FontSize = 12.5,
                };
                UseBrush(moreRun, TextElement.ForegroundProperty, "BrushTextSoft", title);
                titleInlines.Add(moreRun);
            }
            title.Inlines = titleInlines;
            middle.Children.Add(title);
            var who = string.Join(" · ", new[] { Str(sale, "user_display"), Str(sale, "cashbox_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (who.Length > 0)
            {
                var sub = new TextBlock { Text = who, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
                UseBrush(sub, TextBlock.ForegroundProperty, "BrushTextSoft");
                middle.Children.Add(sub);
            }
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var right = new StackPanel { Spacing = 2, MinWidth = 110, VerticalAlignment = VerticalAlignment.Top };
            var amount = new TextBlock
            {
                Text = $"{Amount(Num(sale, "total"))} {Som()}",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextDecorations = canceled ? TextDecorations.Strikethrough : null,
                Classes = { "num" },
            };
            UseBrush(amount, TextBlock.ForegroundProperty, canceled ? "BrushTextSoft" : "BrushText");
            right.Children.Add(amount);
            var methodText = new TextBlock
            {
                Text = canceled ? Tr.T("Возврат", "Кайтаруу", "Returned", "İade", "Qaytarilgan") : PaymentLabel(method),
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            UseBrush(methodText, TextBlock.ForegroundProperty, "BrushTextSoft");
            right.Children.Add(methodText);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            line.Child = row;
            AttachRecentReturnMenu(line, sale); // 2026-11-12: меню строки — «Возврат» / «Возвраты по чеку»
            RecentList.Children.Add(line);
        }

        _ = LoadRecentItemCountsAsync();
    }

    /// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): у строки «Последних продаж» — меню (правая кнопка мыши, долгое касание):
    /// «Возврат» (то же окно возврата, что в кассе, сразу с этим чеком; права и проверки — ReturnSaleLauncher) и «Возвраты по чеку» (правка/удаление 24 ч).
    /// После возврата «Сводка» и разделы перечитываются (сигнал «продажи изменились»).</summary>
    private void AttachRecentReturnMenu(Border line, JsonElement sale)
    {
        var saleId = Str(sale, "id");
        if (!ReceiptReturnActions.Enabled || saleId.Length == 0)
            return;
        var receipt = SalesWindow.TryReceiptNumber(sale) ?? "";
        var menu = new ContextMenu();
        if (ReturnSaleLauncher.CanReturn())
        {
            var full = ReturnSaleLauncher.IsFullyReturned(Str(sale, "status"), Str(sale, "return_status"));
            var ret = new MenuItem { Header = full ? ReturnSaleLauncher.FullyReturnedHint : ReturnSaleLauncher.ButtonText + (receipt.Length > 0 ? " — " + receipt : ""), IsEnabled = !full };
            ret.Click += async (_, _) =>
            {
                try
                {
                    if (await ReceiptReturnActions.OpenReturnAsync(this, saleId, receipt).ConfigureAwait(true))
                        PosDataEvents.RaiseSalesChanged();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Owner app: возврат из «Сводки» (чек {saleId}) не открылся: {ex.Message}", "WARNING");
                }
            };
            menu.Items.Add(ret);
        }
        var list = new MenuItem { Header = ReceiptReturnActions.ReturnsListText };
        list.Click += async (_, _) =>
        {
            try
            {
                if (await ReceiptReturnActions.OpenReturnsListAsync(this, saleId, receipt).ConfigureAwait(true))
                    PosDataEvents.RaiseSalesChanged();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Owner app: возвраты чека {saleId} из «Сводки» не открылись: {ex.Message}", "WARNING");
            }
        };
        menu.Items.Add(list);
        line.ContextMenu = menu;
        line.Background = Brushes.Transparent; // меню открывается и по пустому месту строки
        // 2026-11-12 (аудит 12.11, С-19): на сенсорном экране правую кнопку и подсказку не найти — видимая кнопка «⋯» в конце строки открывает то же меню
        if (line.Child is Grid row)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var more = new Button
            {
                Content = "⋯", Name = "RecentSaleMenuButton", Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
                FontSize = 18, VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            more.Classes.Add("sec-outline");
            var tip = Tr.T("Возврат по этому чеку", "Бул чек боюнча кайтаруу", "Return on this receipt", "Bu fişte iade", "Shu chek bo'yicha qaytarish") + (receipt.Length > 0 ? " — " + receipt : "");
            ToolTip.SetTip(more, tip);
            Avalonia.Automation.AutomationProperties.SetName(more, tip);
            more.Click += (_, e) =>
            {
                e.Handled = true;
                menu.Open(more);
            };
            Grid.SetColumn(more, row.ColumnDefinitions.Count - 1);
            row.Children.Add(more);
        }
    }

    /// <summary>2026-10-05: «Долги клиентов» — из меню и по нажатию на «В долг» в «Сводке».</summary>
    private void OpenDebts()
    {
        if (TariffGate.CanUseDebts && Authorize(PosPermissions.ViewSales))
            OpenSection("debts", () => new DebtsWindow());
    }

    private static string PaymentLabel(string method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        "transfer" or "card" => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
        "mbank" => "MBank",
        "mixed" or "split" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karışık", "Aralash"),
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        // 2026-10-06: зачёт при обмене и выдаче заказа (сервер: «Зачёт (предоплата/обмен)») — показывалось слово «offset».
        "offset" => Tr.T("Зачёт (обмен, предоплата)", "Эсепке алуу (алмаштыруу, алдын ала төлөм)", "Offset (exchange, prepayment)", "Mahsup (değişim, ön ödeme)", "Hisobga olish (almashtirish, oldindan to'lov)"),
        "" => "—",
        var other => other,
    };

    private static string ChecksWord(double count)
    {
        var n = (long)Math.Round(count);
        var ru = (n % 10 == 1 && n % 100 != 11) ? "чек"
            : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "чека"
            : "чеков";
        return Tr.T($"{n} {ru}", $"{n} чек", n == 1 ? "1 receipt" : $"{n} receipts", $"{n} fiş", $"{n} ta chek");
    }

    private static string Som() => Tr.T("сом", "сом", "som", "som", "so'm");

    /// <summary>Сумма без «,00» у целых: 414 634 и 414 634,07.</summary>
    private static string Amount(double value)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return Math.Abs(value - Math.Round(value)) < 0.005 ? value.ToString("N0", ru) : value.ToString("N2", ru);
    }

    private static string Qty(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0005
            ? value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))
            : value.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>2026-10-18, этап О1: единица подписей графика (было «415 тыс» / «4 288» вперемешку) — всё в тыс. сом.</summary>
    private static string ThousandSom() => Tr.T("тыс. сом", "миң сом", "thousand som", "bin som", "ming so'm");

    /// <summary>Подписи над столбцами есть, когда дней в графике не больше 10 (как в ApplyChart).</summary>
    private bool ChartHasLabels
    {
        get
        {
            var (from, to) = CurrentRange();
            var (chartFrom, chartTo) = ChartRange(from, to);
            return (chartTo.Date - chartFrom.Date).Days + 1 <= 10;
        }
    }

    private static double Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Цвет из темы с подпиской: при смене светлой/тёмной темы элементы, собранные в
    /// коде, перекрашиваются сами, как DynamicResource в разметке.</summary>
    /// <remarks>2026-11-01 (Н-01, аудит «10 из 10»): подписка на ресурсы раньше шла от самого долгоживущего окна
    /// (<c>this</c>) — окно держало ссылку на каждый пересобранный элемент «Сводки» (столбики, строки, списки), и за
    /// рабочий день память росла до 3,5 ГБ. Теперь подписка — от самого элемента (для <c>Run</c> — от его
    /// <paramref name="owner"/>-текста): умирает вместе с ним, и сборщик мусора забирает старую разметку.</remarks>
    private IDisposable UseBrush(AvaloniaObject target, AvaloniaProperty property, string resourceKey, StyledElement? owner = null)
    {
        StyledElement host = target is Control own ? own : owner ?? this;
        return target.Bind(property, host.GetResourceObservable(resourceKey));
    }

    /// <summary>2026-11-01 (В2-01в): <c>Run</c> в постоянном TextBlock («Сводка»: деньги, дельты) пересоздаётся при каждом обновлении, а его подписка
    /// на ресурсы живёт на TextBlock, который не умирает. Прошлая подписка этого TextBlock снимается перед новой — не копятся +7 за обновление.</summary>
    private readonly Dictionary<StyledElement, IDisposable> _runBrushSubs = new();

    private void UseRunBrush(Run run, string resourceKey, TextBlock owner)
    {
        if (_runBrushSubs.Remove(owner, out var old))
            old.Dispose();
        _runBrushSubs[owner] = UseBrush(run, TextElement.ForegroundProperty, resourceKey, owner);
    }

    // ------------------------------------------------------------------ шапка окна

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // 2026-11-11 (жалоба «частое сворачивание — программа наполовину»): окно на весь экран сворачивается как с панели задач — Avalonia при
    // WindowState = Minimized сначала выходила из полного экрана (окно на миг с рамкой и прежним размером, после разворачивания — обычное окно).
    private void Minimize_Click(object? sender, RoutedEventArgs e) => NurMarketKassa.AvaloniaHost.Services.WindowRestoreGuard.Minimize(this);

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            UpdateWindowChrome();
            Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
            // 2026-10-18, ревью О2: развернули/восстановили окно — заново решить, сворачивать ли колонку пунктов (порог 1366).
            Dispatcher.UIThread.Post(OnShellSizeChangedForNav, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Развёрнутое окно без системной рамки Windows сдвигает за край экрана на ширину
    /// рамки — отступаем на столько же. Значок кнопки: «развернуть» или «восстановить».</summary>
    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        // 2026-11-01: отступ — на обёртке масштаба, а не на RootGrid: рамка Windows не должна уменьшаться/расти вместе с масштабом.
        UiScaleTransform.Margin = maximized ? OffScreenMargin : default;
        MaximizeIconPath.Data = Geometry.Parse(maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    // ------------------------------------------------------------------ кнопки

    private async void Period_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period } || (period == _period && period != "custom"))
            return;
        if (period == "custom")
        {
            // Спец. дата — то же окно выбора дат, что в «Финансах»; повторное нажатие — выбрать заново.
            var dlg = new FinanceDateRangeDialog();
            if (await dlg.ShowDialog<bool>(this) != true)
                return;
            _customFrom = dlg.FromDate.Date <= dlg.ToDate.Date ? dlg.FromDate.Date : dlg.ToDate.Date;
            _customTo = dlg.FromDate.Date <= dlg.ToDate.Date ? dlg.ToDate.Date : dlg.FromDate.Date;
        }
        _period = period;
        foreach (var b in new[] { TodayButton, WeekButton, MonthButton, CustomButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        ApplyTexts();
        _ = RefreshAsync(supersede: true); // 2026-11-03 (С-13): смена периода отменяет идущую загрузку
        _ = RefreshAbcAsync();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-09-29: «Обновить» — всегда свежие цифры сервера, мимо короткого кэша отчётов.
        NurMarketKassa.Services.Api.SalesApiService.InvalidateReportCache();
        _ = RefreshAsync(forceChart: true, supersede: true); // 2026-11-03 (С-13): «Обновить» во время загрузки начинает заново
        _ = RefreshAbcAsync();
        // 2026-10-29: заодно общие настройки компании, права, каталог и остатки — SyncCenter.ForceSyncAllAsync (то же делает «Обновить» кассы).
        _ = NurMarketKassa.Services.SyncCenter.ForceSyncAllAsync();
    }

    private void AbcOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (TariffGate.CanUseSalesAnalytics && Authorize(PosPermissions.ViewAnalytics))
            OpenSection("abc", () => App.GetRequiredService<AbcAnalysisWindow>());
    }

    /// <summary>Маркетплейс сразу на вкладке «Доп. функции» — для раздела, который предлагает
    /// подключить платную функцию (вкладка «Склад» в «Аналитике»). Права — как у пункта меню.</summary>
    public void OpenMarketplaceExtras()
    {
        if (!Authorize(PosPermissions.ViewSettings))
            return;
        OpenSection("marketplace", () => new MarketplaceWindow().AsSection());
        (_sections.FirstOrDefault(s => s.Key == "marketplace")?.Window as MarketplaceWindow)?.ShowExtrasTab();
    }

    // ------------------------------------------------------------------ сайт: заказы и настройки

    // 2026-09-29: число новых заказов у пункта «Заказы с сайта». Вебхука о заказах у NurCRM нет —
    // раз в минуту спрашиваем список (обычно один запрос), через общий темп массовых загрузок и не
    // во время паузы 429. Пока раздел открыт на экране, он обновляет список сам — тогда не спрашиваем.
    private static readonly TimeSpan SiteOrdersPollInterval = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _siteOrdersTimer;
    private DateTime _siteOrdersPolledUtc = DateTime.MinValue;
    private Border? _siteOrdersBadge;
    private int _siteOrdersNewCount;
    private bool _siteOrdersPolling;
    private bool _siteOrdersUnavailable;

    private async Task PollSiteOrdersAsync()
    {
        if (_siteOrdersPolling || _siteOrdersUnavailable || _loggingOut || _cts.IsCancellationRequested)
            return;
        if (ApiThrottle.RemainingBlock > TimeSpan.Zero)
            return;
        if (_sections.Any(s => s.Key == "siteorders" && s.Window.IsVisible))
            return;
        try
        {
            // 2026-10-01: заказы с сайта — право «Заказы» сайта NurCRM.
            if (!App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewOrders))
                return;
        }
        catch (Exception)
        {
            return;
        }

        _siteOrdersPolling = true;
        try
        {
            var api = App.GetRequiredService<ShowcaseApiService>();
            await ApiThrottle.RunBulkAsync(() => api.ListOrdersAsync(_cts.Token), _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            // Нет права или адреса — до конца сеанса не спрашиваем; раздел сам покажет причину.
            _siteOrdersUnavailable = true;
            PosLogger.Log($"Owner app: заказы с сайта недоступны ({ex.StatusCode}) — опрос остановлен.", "SHOWCASE");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: заказы с сайта не опрошены: {ex.Message}", "DEBUG");
        }
        finally
        {
            _siteOrdersPolling = false;
        }
    }

    private void OnSiteOrdersCountChanged(int count) => Dispatcher.UIThread.Post(() =>
    {
        _siteOrdersNewCount = count;
        UpdateSiteOrdersBadge();
        // 2026-10-18, этап О1: новые заказы — и строкой в «Требует внимания».
        RenderAttention();
    });

    private void UpdateSiteOrdersBadge()
    {
        if (_siteOrdersBadge is not { Child: TextBlock text } badge)
            return;
        var count = _siteOrdersNewCount;
        badge.IsVisible = count > 0;
        text.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        UpdateRailDots();
        if (_navButtons.TryGetValue("siteorders", out var button) && _navTitles.TryGetValue("siteorders", out var title))
            ToolTip.SetTip(button, count > 0
                ? title + " · " + Tr.T($"новых: {count}", $"жаңы: {count}", $"new: {count}", $"yeni: {count}", $"yangi: {count}")
                : title);
    }

    // ------------------------------------------------------------------ прокат: сроки возврата

    // 2026-10-02, владелец: «в админке добавь уведомление об окончании и приближении срока аренды проката».
    // Проверяет RentalDueNotifier (раз в час); здесь — значок у пункта «Прокат» и карточка в меню.
    // Карточку можно закрыть — снова появится, когда изменится состав (новый срок или просрочка).
    private Border? _rentalsBadge;
    private string _rentalAlertDismissed = "";
    // 2026-10-02, владелец: «оповещение прям со звуком должно быть в админке». Звук — когда появилось
    // новое событие (прокат впервые стал «завтра», «сегодня» или «просрочен»), а не каждый час.
    private readonly HashSet<string> _rentalAlertSounded = new();

    private void OnRentalDueChanged(RentalDueNotifier.Summary summary) => Dispatcher.UIThread.Post(UpdateRentalAlert);

    private static string RentalAlertSignature(RentalDueNotifier.Summary s) =>
        string.Join("|", s.Overdue.Select(r => "o" + r.Id).Concat(s.Today.Select(r => "t" + r.Id)).Concat(s.Tomorrow.Select(r => "m" + r.Id)));

    /// <summary>Плашка показана владельцу (сроки есть, не скрыта крестиком) — от неё зависят звук и место плашки.</summary>
    private bool _rentalAlertActive;

    private void UpdateRentalAlert()
    {
        // 2026-10-18, этап О1: сроки проката — и строкой в «Требует внимания» сводки.
        // 2026-10-18, этап О2: плашка ушла из меню (правило аудита 9) в полосу над открытым разделом (PlaceRentalAlert), в «Сводке»
        // сроки — строками «Требует внимания». Звук — как раньше: новое событие (прокат впервые «завтра», «сегодня» или «просрочен»).
        // Прежде звук молчал при свёрнутом меню (карточки там не было) — теперь оповещение видно всегда, и звук тоже.
        RenderAttention();
        var s = RentalDueNotifier.Last;
        var count = s?.Count ?? 0;
        var visibleSphere = MarketSpheres.HasRentals; // 2026-10-20: прокат во всех сферах
        if (_rentalsBadge is { Child: TextBlock badgeText } badge)
        {
            badge.IsVisible = visibleSphere && count > 0;
            badgeText.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
            // Просрочка — красным, только сроки — серо-синим (UpdateRailDots — точка у группы «Продажи»).
            SetNavBadgeTone(badge, s is { Overdue.Count: > 0 });
        }

        if (s is null || count == 0 || !visibleSphere || RentalAlertSignature(s) == _rentalAlertDismissed)
        {
            _rentalAlertActive = false;
            PlaceRentalAlert();
            return;
        }

        RentalAlertTitle.Text = Tr.T("Прокат: сроки возврата", "Прокат: кайтаруу мөөнөтү", "Rentals: return dates", "Kiralama: iade tarihleri", "Prokat: qaytarish muddati");
        var parts = new List<string>();
        if (s.Overdue.Count > 0)
            parts.Add(Tr.T($"Просрочено: {s.Overdue.Count}", $"Мөөнөтү өттү: {s.Overdue.Count}", $"Overdue: {s.Overdue.Count}", $"Gecikmiş: {s.Overdue.Count}", $"Muddati o'tgan: {s.Overdue.Count}"));
        if (s.Today.Count > 0)
            parts.Add(Tr.T($"Вернуть сегодня: {s.Today.Count}", $"Бүгүн кайтаруу: {s.Today.Count}", $"Due today: {s.Today.Count}", $"Bugün iade: {s.Today.Count}", $"Bugun qaytarish: {s.Today.Count}"));
        if (s.Tomorrow.Count > 0)
            parts.Add(Tr.T($"Вернуть завтра: {s.Tomorrow.Count}", $"Эртең кайтаруу: {s.Tomorrow.Count}", $"Due tomorrow: {s.Tomorrow.Count}", $"Yarın iade: {s.Tomorrow.Count}", $"Ertaga qaytarish: {s.Tomorrow.Count}"));
        // В плашке — числа одной строкой; первые три проката с именем клиента — в подсказке (кому звонить).
        RentalAlertText.Text = string.Join(" · ", parts);
        var lines = new List<string>(parts);
        foreach (var r in s.Overdue.Concat(s.Today).Concat(s.Tomorrow).Take(3))
            lines.Add($"• №{r.Number} {r.ClientName} — {string.Join(", ", r.Items.Select(i => i.Label))}");
        ToolTip.SetTip(RentalAlertCard, string.Join("\n", lines));
        RentalAlertOpenText.Text = Tr.T("Открыть прокат", "Прокатты ачуу", "Open rentals", "Kiralamayı aç", "Prokatni ochish");
        ToolTip.SetTip(RentalAlertClose, Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish"));
        Avalonia.Automation.AutomationProperties.SetName(RentalAlertOpen, RentalAlertOpenText.Text);
        // 2026-10-18, ревью О2: пункта «Прокат» нет (права, «Разделы меню») — плашка только сообщает, без «Открыть прокат ›».
        RentalAlertOpen.IsVisible = _navButtons.ContainsKey("rentals");
        _rentalAlertActive = true;
        PlaceRentalAlert();

        var keys = RentalAlertSignature(s).Split('|');
        var fresh = keys.Count(k => _rentalAlertSounded.Add(k));
        if (fresh > 0)
        {
            AlertSound.PlayChime();
            PosLogger.Log($"Owner app: оповещение о сроке проката со звуком ({fresh}).", "RENTAL");
        }
    }

    /// <summary>2026-10-18, этап О2: плашка проката — в полосе над открытым разделом (окно раздела закрывает правую часть, а полоса
    /// видна всегда). В «Сводке» её нет: те же сроки — первыми строками «Требует внимания». На телефоне полоса узкая — только
    /// число у пункта «Прокат», точка у группы и строки «Требует внимания».</summary>
    private void PlaceRentalAlert()
    {
        if (RentalAlertCard is null)
            return;
        RentalAlertCard.IsVisible = _rentalAlertActive && _activeSection != null && !_phoneLayout;
    }

    private void RentalAlertClose_Click(object? sender, RoutedEventArgs e)
    {
        if (RentalDueNotifier.Last is { } s)
            _rentalAlertDismissed = RentalAlertSignature(s);
        _rentalAlertActive = false;
        PlaceRentalAlert();
    }

    private void RentalAlertOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_navButtons.TryGetValue("rentals", out var button))
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Раздел «Настройки сайта» — пункт меню и кнопка «Настройки сайта» в «Заказах с сайта».
    /// Права — как у «Настроек» (владелец/админ).</summary>
    public void OpenSiteSettings()
    {
        if (Authorize(PosPermissions.ViewSettings))
            OpenSection("sitesettings", () => new SiteSettingsWindow());
    }

    /// <summary>Раздел «Аналитика» на вкладке «Склад» — кнопка «Открыть аналитику склада» в
    /// Маркетплейсе. false — раздела нет (тариф «Старт»): тогда аналитика склада открывается
    /// по-старому, вкладкой склада.</summary>
    public bool OpenAnalyticsStock()
    {
        if (!TariffGate.CanUseSalesAnalytics)
            return false;
        if (Authorize(PosPermissions.ViewAnalytics))
        {
            OpenSection("analytics", () => App.GetRequiredService<FinanceWindow>().AsAnalyticsSection());
            (_sections.FirstOrDefault(s => s.Key == "analytics")?.Window as FinanceWindow)?.ShowStockTab();
        }
        return true;
    }

    // ------------------------------------------------------------------ ABC в сводке

    // 2026-09-27, владелец: «ABC анализ должен быть в сводке и он должен быть по всем». Все шесть
    // срезов за период сводки. Считается локально (AnalyticsReportData) в фоне и только когда это
    // нужно: при открытии, смене периода, «Обновить», смене языка и после новых продаж (с
    // задержкой, пачкой). Двадцатисекундное обновление сводки его не трогает: оно ходит за цифрами
    // сервера, а локальные строки продаж меняются только вместе с сигналом SalesChanged.
    // 2026-09-28, сверка с сайтом: ABC считается по вкладке «Товары» сайта (локальная история
    // кассы — только без связи): по ней ABC расходился с сайтом (440 тыс. против 586 тыс.). Раз
    // цифры теперь серверные, продажи других касс меняют их без сигнала SalesChanged — поэтому
    // обновление сводки заодно освежает ABC, но не чаще раза в минуту (один запрос к серверу).
    private DateTime _abcRefreshedUtc = DateTime.MinValue;

    private readonly DispatcherTimer _abcDebounce;
    private AbcSectionView? _abcView;
    private CancellationTokenSource? _abcCts;
    private DateTime _abcFrom = ServerClock.Today;
    private DateTime _abcTo = ServerClock.Today;

    /// <summary>Продажи изменились, пока сводка была скрыта разделом, — пересчитать при возврате.</summary>
    private bool _abcStale;

    /// <summary>Как у раздела «ABC-анализ»: не на «Старте» и с правом «Аналитика» (2026-10-01, как на сайте).</summary>
    private static bool AbcAllowed
    {
        get
        {
            if (!TariffGate.CanUseSalesAnalytics)
                return false;
            try
            {
                return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewAnalytics);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Сигнал приходит из фонового потока и за один чек — несколько раз: собираем пачку
    /// таймером в UI-потоке.</summary>
    private void OnSalesChangedForAbc() => Dispatcher.UIThread.Post(() =>
    {
        _abcDebounce.Stop();
        _abcDebounce.Start();
        // 2026-10-29: «Сводка» — по событию, а не только по таймеру (не чаще раза в 3 с; скрытую/неактивную по тем же правилам, что таймер).
        // 2026-10-29 (скорость синхронизации): было 8 с; события приходят быстро (пульс 5 с у владельца, подсказка кассы по локальной сети).
        if (DateTime.UtcNow - _lastRefreshStartedUtc >= TimeSpan.FromSeconds(3))
            _ = RefreshWhenShownAsync();
    });

    private void RefreshAbcWhenVisible()
    {
        if (_activeSection != null)
        {
            _abcStale = true;
            return;
        }

        _ = RefreshAbcAsync();
    }

    private void DropAbcView()
    {
        _abcCts?.Cancel();
        if (_abcView != null)
        {
            _abcView.ProductAnalyticsRequested -= ShowAbcProductAnalytics;
            AbcHost.Children.Remove(_abcView);
            _abcView = null;
        }
    }

    private async Task RefreshAbcAsync()
    {
        var allowed = AbcAllowed;
        AbcCard.IsVisible = allowed;
        if (!allowed || _loggingOut || _cts.IsCancellationRequested)
            return;

        _abcStale = false;
        _abcRefreshedUtc = DateTime.UtcNow;
        _abcCts?.Cancel();
        var cts = new CancellationTokenSource();
        _abcCts = cts;
        var (from, to) = CurrentRange();
        AbcLoadingText.IsVisible = _abcView == null;
        try
        {
            // 2026-09-28: ABC — по вкладке «Товары» сайта (как на сайте), без связи — по истории кассы.
            var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, includeSeasonality: false, full: false, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            ShowAbc(data, from, to);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: ABC-анализ сводки не построен: {ex.Message}", "WARNING");
            AbcLoadingText.Text = Tr.T("Не удалось построить ABC-анализ", "ABC-анализди түзүү мүмкүн болгон жок", "Could not build the ABC analysis", "ABC analizi oluşturulamadı", "ABC tahlilini tuzib bo'lmadi");
            AbcLoadingText.IsVisible = _abcView == null;
        }
        finally
        {
            if (ReferenceEquals(_abcCts, cts))
                _abcCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Показывает готовый отчёт; сам блок собирается при первом показе (и заново после
    /// смены языка — см. DropAbcView).</summary>
    private void ShowAbc(AnalyticsReportData data, DateTime from, DateTime to)
    {
        if (_abcView == null)
        {
            _abcView = new AbcSectionView
            {
                SliceKeys = AbcSectionView.AllSliceKeys,
                ShowSeasonality = false,
                TableMaxHeight = 360,
                // 2026-10-21 (пакет 2, п. 86): без прокрутки внутри прокрутки — блок по высоте содержимого, листает сама Сводка.
                ScrollsVertically = false,
            };
            _abcView.ProductAnalyticsRequested += ShowAbcProductAnalytics;
            AbcHost.Children.Add(_abcView);
        }

        _abcFrom = from;
        _abcTo = to;
        _abcView.Update(data);
        AbcLoadingText.IsVisible = false;
    }

    // 2026-10-21 (пакет 2, п. 86): FitAbcHeight (блок ABC высотой в экран со своей прокруткой) убран — блок по содержимому.

    private void ShowAbcProductAnalytics(string productName)
    {
        try
        {
            new ProductAnalyticsWindow(productName, _abcFrom, _abcTo).Show(this);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Разбор товара не открылся: {ex}", "WARNING");
        }
    }

    private void AllSales_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-18: «Все продажи» — раздел «Продажи и финансы», вкладка «Чеки» (OwnerShellWindow.SalesFinance.cs).
        if (TariffGate.CanUseSalesAnalytics && Authorize(PosPermissions.ViewSales))
            OpenSalesFinance("checks");
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var newIsDark = !prefs.DarkTheme;
        prefs.DarkTheme = newIsDark;
        App.ApplyTheme(newIsDark);
        prefs.SaveToDisk();
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        if (this.TryFindResource(UserPreferences.Instance.DarkTheme ? "SunIcon" : "MoonIcon", out var icon) && icon is Geometry geometry)
            ThemeIconPath.Data = OldThemeIconPath.Data = geometry;
    }

    /// <summary>Как в меню кассы: закрыть программу и выйти на рабочий стол (вход при этом сохраняется).</summary>
    private void ExitToDesktop()
    {
        App.ExitWithoutLoginRedirect = true;
        Close();
    }

    private void ExitToDesktop_Click(object? sender, RoutedEventArgs e) => ExitToDesktop();

    /// <summary>Выход из учётной записи — как в кассе (MainWindow.NavigateToLoginAsync): стираем
    /// сессию и сохранённый вход, показываем окно входа. 2026-09-29: кнопка — в Настройки → Аккаунт
    /// («Выйти», AccountView.LogoutButton_Click), а не внизу меню.</summary>
    public async Task SignOutAsync()
    {
        // 2026-11-02 (В3-01): выход из учётной записи тоже не теряет несохранённое молча.
        if (!_exitConfirmed && !await ConfirmLeaveAsync().ConfigureAwait(true))
            return;
        _loggingOut = true;
        _timer.Stop();
        _siteOrdersTimer.Stop();
        try
        {
            App.AuthApi.ClearSession();
            RefreshExpiryWatcher.Current.Clear(); // 2026-11-06: вышли — срока входа нет
            await App.GetRequiredService<IAuthSessionManager>().ClearSessionAsync().ConfigureAwait(true);
            OfflineAuthSessionStore.Clear();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: logout cleanup failed: {ex.Message}", "WARNING");
        }

        CloseAllSections();
        var login = App.GetRequiredService<LoginWindow>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = login;
        login.Show();
        Close();
    }

    private static void OpenUrl(string url)
    {
        // 2026-11-06: В4 А5-05 — общий запуск ссылки (на Android — Intent).
        if (!PlatformLauncher.OpenUrl(url))
            PosLogger.Log($"Owner app: failed to open {url}", "WARNING");
    }
}
