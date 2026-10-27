using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Горизонтальная столбчатая диаграмма без сторонней библиотеки — та же ручная техника
/// (Border с шириной, пропорциональной значению), что уже используется в графиках Финансов
/// (UpdateDailyRevenueChart/UpdateHourlyRevenueChart), только вынесенная в общий метод, чтобы не
/// дублировать её в каждом окне. Пропорции строятся через звёздочные колонки Grid, поэтому
/// корректно тянутся при изменении размера окна.</summary>
public static class BarChartRenderer
{
    // 2026-10-26, владелец: «в аналитике, сводке используй РАЗНЫЕ ЦВЕТА в диаграммах». Единая палитра — ChartPalette (Core, та же,
    // что в мобилке); цвет категории — по хэшу названия, а не по порядку (не «прыгает» при сортировке и обновлении).
    private const string AccentYellow = "#FFB208";
    private const string SeriesBlue = "#5470E1";

    /// <param name="barBrush">Один цвет всех полос (для ВРЕМЕННОГО ряда есть RenderTimeSeriesBars); null — у каждой полосы свой цвет
    /// из палитры по названию (2026-10-26: рейтинги товаров и категорий снова разноцветные по просьбе владельца; с 2026-10-19 были одним цветом).</param>
    public static void Render(StackPanel container, IReadOnlyList<(string Label, double Value, string ValueText)> items, IBrush? barBrush = null)
    {
        container.Children.Clear();
        if (items.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных", "Маалымат жок", "No data", "Veri yok", "Ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        var max = items.Max(i => i.Value);
        if (max <= 0) max = 1;

        var colors = ChartPalette.Assign(items.Select(i => (string?)i.Label).ToList());
        for (var i = 0; i < items.Count; i++)
        {
            var (label, value, valueText) = items[i];
            var fraction = Math.Clamp(value / max, 0.03, 1.0);
            var color = barBrush ?? Brush.Parse(colors[i]);

            // 2026-10-19, этап О10: подпись 150 → 220 — названия товаров («Сыр «Российский» весовой») целиком.
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,Auto") };

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(labelText, 0);

            var track = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(
                    $"{fraction.ToString("0.###", CultureInfo.InvariantCulture)}*,{(1 - fraction).ToString("0.###", CultureInfo.InvariantCulture)}*"),
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var fill = new Border { Background = color, CornerRadius = new CornerRadius(5) };
            Grid.SetColumn(fill, 0);
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);

            var valueLabel = new TextBlock
            {
                Text = valueText,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(valueLabel, 2);

            row.Children.Add(labelText);
            row.Children.Add(track);
            row.Children.Add(valueLabel);
            container.Children.Add(row);
        }
    }

    /// <summary>2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах». ВРЕМЕННОЙ ряд горизонтальными полосами (выручка по месяцам): полосы
    /// одного оттенка (цвет текста темы), лучший период — акцентом (жёлтый), самый слабый — приглушённо, текущий — контуром;
    /// под диаграммой легенда. Выделения по правилам <see cref="ChartPalette.SeriesMarks"/>.</summary>
    public static void RenderTimeSeriesBars(StackPanel container, IReadOnlyList<(string Label, double Value, string ValueText)> items, int currentIndex = -1, string? currentWord = null)
    {
        container.Children.Clear();
        if (items.Count == 0)
        {
            container.Children.Add(new TextBlock { Text = Tr.T("Нет данных", "Маалымат жок", "No data", "Veri yok", "Ma'lumot yo'q"), FontSize = 12, Foreground = Brushes.Gray });
            return;
        }

        var max = items.Max(i => i.Value);
        if (max <= 0) max = 1;
        var (best, worst) = ChartPalette.SeriesMarks(items.Select(i => i.Value).ToList());
        var accent = Brush.Parse(AccentYellow);

        for (var i = 0; i < items.Count; i++)
        {
            var (label, value, valueText) = items[i];
            var fraction = Math.Clamp(value / max, 0.03, 1.0);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,Auto") };
            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = i == currentIndex ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(labelText, 0);
            var track = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(
                    $"{fraction.ToString("0.###", CultureInfo.InvariantCulture)}*,{(1 - fraction).ToString("0.###", CultureInfo.InvariantCulture)}*"),
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var fill = new Border { CornerRadius = new CornerRadius(5) };
            if (best.Contains(i))
            {
                fill.Background = accent;
                fill.BorderBrush = Brush.Parse("#8A6200"); // тёмно-жёлтый контур: жёлтый на белом — 1,8:1
                fill.BorderThickness = new Thickness(1);
            }
            else
            {
                fill.Bind(Border.BackgroundProperty, container.GetResourceObservable("BrushText"));
                if (worst.Contains(i)) fill.Opacity = 0.4;
            }
            if (i == currentIndex)
            {
                // текущий период — контур цвета текста темы
                fill.BorderThickness = new Thickness(2);
                fill.Bind(Border.BorderBrushProperty, container.GetResourceObservable("BrushText"));
            }
            Grid.SetColumn(fill, 0);
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            var valueLabel = new TextBlock { Text = valueText, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(valueLabel, 2);
            row.Children.Add(labelText);
            row.Children.Add(track);
            row.Children.Add(valueLabel);
            container.Children.Add(row);
        }

        var legend = SeriesLegend(container, best.Count > 0, worst.Count > 0, currentIndex >= 0,
            Tr.T("Лучший период", "Эң мыкты мезгил", "Best period", "En iyi dönem", "Eng yaxshi davr"),
            Tr.T("Самый слабый", "Эң начар", "Weakest", "En zayıf", "Eng sust"),
            currentWord ?? Tr.T("Текущий", "Азыркы", "Current", "Güncel", "Joriy"));
        if (legend != null) container.Children.Add(legend);
    }

    /// <summary>2026-10-26: оформление столбца ВРЕМЕННОГО ряда (выручка по дням/часам): обычный — цвет текста темы, лучший — акцент
    /// (жёлтый с тёмно-жёлтым контуром: жёлтый на белом 1,8:1), самый слабый — приглушённо, текущий (сегодня) — кольцо цвета текста
    /// вокруг столбца (контур на тёмном столбце не виден). Возвращает то, что нужно положить в панель (столбец или его кольцо).</summary>
    public static Control StyleSeriesBar(Border bar, Control themeSource, bool best, bool worst, bool current)
    {
        if (best)
        {
            bar.Background = Brush.Parse(AccentYellow);
            bar.BorderBrush = Brush.Parse("#8A6200");
            bar.BorderThickness = new Thickness(1);
        }
        else
        {
            bar.Bind(Border.BackgroundProperty, themeSource.GetResourceObservable("BrushText"));
            if (worst) bar.Opacity = 0.4;
        }
        if (!current) return bar;
        var ring = new Border
        {
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(bar.CornerRadius.TopLeft + 2),
            HorizontalAlignment = bar.HorizontalAlignment,
            Child = bar,
        };
        ring.Bind(Border.BorderBrushProperty, themeSource.GetResourceObservable("BrushText"));
        return ring;
    }

    /// <summary>Легенда временного ряда (лучший / самый слабый / текущий); null — выделений нет и легенда не нужна.</summary>
    public static WrapPanel? SeriesLegend(Control themeSource, bool hasBest, bool hasWorst, bool hasCurrent, string bestWord, string worstWord, string currentWord)
    {
        var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Chip(IBrush fill, string text, IBrush? border = null, double opacity = 1)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), Spacing = 6 };
            chip.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = fill, Opacity = opacity,
                BorderBrush = border, BorderThickness = border == null ? new Thickness(0) : new Thickness(1.5), VerticalAlignment = VerticalAlignment.Center,
            });
            var t = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            t.Bind(TextBlock.ForegroundProperty, themeSource.GetResourceObservable("BrushTextSoft"));
            chip.Children.Add(t);
            legend.Children.Add(chip);
        }
        if (hasBest) Chip(Brush.Parse(AccentYellow), bestWord, Brush.Parse("#8A6200"));
        if (hasWorst) Chip(Brush.Parse("#64748B"), worstWord, null, 0.5);
        if (hasCurrent) Chip(Brushes.Transparent, currentWord, Brushes.Gray);
        return legend.Children.Count > 0 ? legend : null;
    }

    /// <summary>Круговая диаграмма (пироги/donut без выреза) — доли строятся через
    /// Path+ArcSegment, легенда — отдельным списком справа с процентами.</summary>
    public static void RenderPie(StackPanel container, IReadOnlyList<(string Label, double Value, string ValueText)> items, double diameter = 170,
        IReadOnlyDictionary<string, string>? colorKeys = null)
    {
        container.Children.Clear();
        var positive = items.Where(i => i.Value > 0).ToList();
        if (positive.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных", "Маалымат жок", "No data", "Veri yok", "Ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        var total = positive.Sum(i => i.Value);
        var canvas = new Canvas { Width = diameter, Height = diameter };
        var center = new Point(diameter / 2, diameter / 2);
        var radius = diameter / 2 - 3;
        var legend = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        var startAngle = -90.0;
        // colorKeys: подпись → ключ цвета (способы оплаты: цвет не зависит от языка подписи и тот же, что в «Сводке»).
        var colors = ChartPalette.Assign(positive.Select(i => colorKeys != null && colorKeys.TryGetValue(i.Label, out var k) ? k : i.Label).ToList<string?>());
        for (var i = 0; i < positive.Count; i++)
        {
            var (label, value, valueText) = positive[i];
            var sweep = value / total * 360.0;
            var color = Brush.Parse(colors[i]);

            // Единственная доля (100%) — полный круг вместо вырожденной дуги.
            if (positive.Count == 1)
            {
                canvas.Children.Add(new Ellipse
                {
                    Width = diameter - 6,
                    Height = diameter - 6,
                    Fill = color,
                    [Canvas.LeftProperty] = 3.0,
                    [Canvas.TopProperty] = 3.0,
                });
            }
            else
            {
                canvas.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = BuildPieSliceGeometry(center, radius, startAngle, Math.Max(sweep, 0.75)),
                    Fill = color,
                });
            }
            startAngle += sweep;

            var pct = value / total * 100;
            var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            legendRow.Children.Add(new Border
            {
                Width = 12, Height = 12, Background = color, CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            legendRow.Children.Add(new TextBlock
            {
                Text = $"{label} — {pct.ToString("0.#", CultureInfo.InvariantCulture)}% ({valueText})",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
            });
            legend.Children.Add(legendRow);
        }

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,20,*") };
        Grid.SetColumn(canvas, 0);
        Grid.SetColumn(legend, 2);
        layout.Children.Add(canvas);
        layout.Children.Add(legend);
        container.Children.Add(layout);
    }

    private static Geometry BuildPieSliceGeometry(Point center, double radius, double startAngleDeg, double sweepAngleDeg)
    {
        var startRad = startAngleDeg * Math.PI / 180.0;
        var endRad = (startAngleDeg + sweepAngleDeg) * Math.PI / 180.0;
        var startPoint = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
        var endPoint = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
        var isLargeArc = sweepAngleDeg > 180.0;

        var figure = new PathFigure { StartPoint = center, IsClosed = true };
        figure.Segments = new PathSegments
        {
            new LineSegment { Point = startPoint },
            new ArcSegment
            {
                Point = endPoint,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc,
            },
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    /// <summary>Линейная диаграмма (точки, соединённые линией, без заливки) — для трендов
    /// по времени (например, выручка по дням).</summary>
    public static void RenderLine(StackPanel container, IReadOnlyList<(string Label, double Value)> points) =>
        RenderLineOrArea(container, points, filled: false);

    /// <summary>График с областями — та же линия, но с заливкой под кривой (полупрозрачной) —
    /// удобен для показа накопительной/суммарной величины.</summary>
    public static void RenderArea(StackPanel container, IReadOnlyList<(string Label, double Value)> points) =>
        RenderLineOrArea(container, points, filled: true);

    private static void RenderLineOrArea(StackPanel container, IReadOnlyList<(string Label, double Value)> points, bool filled)
    {
        container.Children.Clear();
        if (points.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных за выбранный период", "Тандалган мезгилде маалымат жок", "No data for the selected period", "Seçilen dönem için veri yok", "Tanlangan davr uchun ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        const double height = 160;
        const double topPad = 10, bottomPad = 26, leftPad = 10, rightPad = 10;
        const double stepWidth = 56;
        var plotHeight = height - topPad - bottomPad;
        var canvasWidth = Math.Max(260, leftPad + rightPad + stepWidth * Math.Max(1, points.Count - 1));

        var max = points.Max(p => p.Value);
        var min = Math.Min(0, points.Min(p => p.Value));
        if (max <= min) max = min + 1;

        var canvas = new Canvas { Width = canvasWidth, Height = height };

        Point PointAt(int i)
        {
            var x = leftPad + (points.Count > 1 ? stepWidth * i : (canvasWidth - leftPad - rightPad) / 2);
            var normalized = (points[i].Value - min) / (max - min);
            var y = topPad + (1 - normalized) * plotHeight;
            return new Point(x, y);
        }

        var linePoints = new Avalonia.Points();
        for (var i = 0; i < points.Count; i++)
            linePoints.Add(PointAt(i));

        if (filled)
        {
            var areaPoints = new Avalonia.Points { new Point(linePoints[0].X, topPad + plotHeight) };
            foreach (var p in linePoints)
                areaPoints.Add(p);
            areaPoints.Add(new Point(linePoints[^1].X, topPad + plotHeight));

            canvas.Children.Add(new Polygon
            {
                Points = areaPoints,
                Fill = new SolidColorBrush(Color.Parse(SeriesBlue), 0.22),
            });
        }

        canvas.Children.Add(new Polyline
        {
            Points = linePoints,
            Stroke = Brush.Parse(SeriesBlue),
            StrokeThickness = 2.5,
            StrokeJoin = PenLineJoin.Round,
        });

        // 2026-10-26: лучший период — акцентом (жёлтая точка покрупнее), самый слабый — пустой точкой; остальные — как были.
        var (bestPoints, worstPoints) = ChartPalette.SeriesMarks(points.Select(pt => pt.Value).ToList());
        for (var i = 0; i < points.Count; i++)
        {
            var p = PointAt(i);
            var isBest = bestPoints.Contains(i);
            var size = isBest ? 12 : 8;
            var dot = new Ellipse { Width = size, Height = size, Fill = Brush.Parse(isBest ? AccentYellow : SeriesBlue) };
            if (isBest)
            {
                dot.Stroke = Brush.Parse("#191919");
                dot.StrokeThickness = 1.5;
                ToolTip.SetTip(dot, Tr.T("Лучший день", "Эң мыкты күн", "Best day", "En iyi gün", "Eng yaxshi kun"));
            }
            else if (worstPoints.Contains(i))
            {
                dot.Fill = Brushes.Transparent;
                dot.Stroke = Brush.Parse(SeriesBlue);
                dot.StrokeThickness = 2;
                ToolTip.SetTip(dot, Tr.T("Самый слабый день", "Эң начар күн", "Weakest day", "En zayıf gün", "Eng sust kun"));
            }
            Canvas.SetLeft(dot, p.X - size / 2.0);
            Canvas.SetTop(dot, p.Y - size / 2.0);
            canvas.Children.Add(dot);

            var label = new TextBlock
            {
                Text = points[i].Label,
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = stepWidth,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(label, p.X - stepWidth / 2);
            Canvas.SetTop(label, height - bottomPad + 6);
            canvas.Children.Add(label);
        }

        var scroller = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = height + 4,
        };
        container.Children.Clear();
        container.Children.Add(scroller);
    }

    /// <summary>Цвета групп ABC. Держим их в одном месте: ими красятся и столбцы диаграммы,
    /// и строки таблицы, и легенда — расхождение сбивало бы с толку сильнее, чем отсутствие
    /// цвета вовсе.</summary>
    /// <summary>Прозрачная полоса, по которой открывается разбор товара. Прозрачный фон
    /// обязателен: без заданного Background панель в Avalonia не ловит нажатия вовсе.</summary>
    private static Border ClickZone(string label, double width, double height, Action<string> onClick)
    {
        var zone = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        zone.PointerPressed += (_, _) => onClick(label);
        return zone;
    }

    public static string AbcColor(string group) => group switch
    {
        "A" => "#16A34A",
        "B" => "#F59E0B",
        _ => "#3B82F6",
    };

    /// <summary>Диаграмма Парето для ABC-анализа: столбцы — доля товара в выручке, ломаная —
    /// та же доля нарастающим итогом по правой шкале 0–100 %.
    ///
    /// Две шкалы здесь не прихоть оформления, а суть метода: по столбцам видно, насколько один
    /// товар весомее соседнего, а по ломаной — где проходят границы 80 % и 95 %, то есть где
    /// заканчивается группа A и начинается C. Обе границы нарисованы пунктиром, иначе деление
    /// на группы выглядело бы взятым с потолка.
    ///
    /// Столбцы окрашены по группе, а не по порядку: цвет здесь несёт смысл, а не различает
    /// соседей.
    ///
    /// <paramref name="onItemClick"/> — по нажатию на столбец открывается разбор этого товара.
    /// Без него столбцы на нажатие не реагируют.</summary>
    public static void RenderPareto(
        StackPanel container,
        IReadOnlyList<(string Label, double Share, double Cumulative, string Group, string ValueText)> items,
        Action<string>? onItemClick = null)
    {
        container.Children.Clear();
        if (items.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет продаж за выбранный период", "Тандалган мезгилде сатуу жок", "No sales in the selected period", "Seçilen dönemde satış yok", "Tanlangan davrda sotuv yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        // 2026-10-18, доводка О4: подписи под столбцами длиннее (170 px вместо 62, «Aos Extra…» не обрезается) — поле снизу выше,
        // справа — место под повёрнутую подпись последнего столбца.
        const double labelWidth = 170;
        // 2026-10-21 (пакет 2, п. 86): поле под подписями 134 → 150 (высота 360 → 376, сам график той же высоты): повёрнутая
        // на 48° подпись в 170 px шрифтом 11 занимает ~142 px — длинные названия больше не срезаются снизу.
        const double height = 376;
        const double topPad = 14, bottomPad = 150, leftPad = 42, rightPad = 46;
        const double barWidth = 30, gap = 12;
        const double step = barWidth + gap;
        var plotHeight = height - topPad - bottomPad;
        var plotWidth = step * items.Count;
        var canvasWidth = leftPad + plotWidth + Math.Max(rightPad, labelWidth * 0.67 - barWidth / 2 - gap / 2 + 8);

        var maxShare = items.Max(i => i.Share);
        if (maxShare <= 0) maxShare = 1;

        var canvas = new Canvas { Width = canvasWidth, Height = height };
        var gridBrush = new SolidColorBrush(Color.Parse("#94A3B8"), 0.35);

        // Сетка и правая шкала: подписи процентов принадлежат НАКОПИТЕЛЬНОЙ ломаной.
        for (var percent = 0; percent <= 100; percent += 25)
        {
            var y = topPad + (1 - percent / 100.0) * plotHeight;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(leftPad, y),
                EndPoint = new Point(leftPad + plotWidth, y),
                Stroke = gridBrush,
                StrokeThickness = 1,
            });

            var right = new TextBlock
            {
                Text = percent.ToString(CultureInfo.InvariantCulture) + " %",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = rightPad - 6,
            };
            Canvas.SetLeft(right, leftPad + plotWidth + 6);
            Canvas.SetTop(right, y - 7);
            canvas.Children.Add(right);
        }

        // Границы групп: 80 % — конец A, 95 % — конец B.
        foreach (var boundary in new[] { (Value: 80.0, Group: "A"), (Value: 95.0, Group: "B") })
        {
            var y = topPad + (1 - boundary.Value / 100.0) * plotHeight;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(leftPad, y),
                EndPoint = new Point(leftPad + plotWidth, y),
                Stroke = Brush.Parse(AbcColor(boundary.Group)),
                StrokeThickness = 1.5,
                StrokeDashArray = new AvaloniaList<double> { 5, 4 },
            });

            var tag = new TextBlock
            {
                Text = boundary.Value.ToString("0", CultureInfo.InvariantCulture) + " %",
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse(AbcColor(boundary.Group)),
            };
            Canvas.SetLeft(tag, 2);
            Canvas.SetTop(tag, y - 7);
            canvas.Children.Add(tag);
        }

        // Столбцы: высота — доля товара в выручке по своей, левой шкале.
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var barHeight = Math.Max(2, item.Share / maxShare * plotHeight);
            var x = leftPad + i * step + gap / 2;

            var bar = new Border
            {
                Width = barWidth,
                Height = barHeight,
                Background = Brush.Parse(AbcColor(item.Group)),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
            };
            ToolTip.SetTip(bar, item.Label + "\n" + item.ValueText
                + "\n" + Tr.T("Доля", "Үлүшү", "Share", "Pay", "Ulush") + ": "
                + item.Share.ToString("0.##", CultureInfo.CurrentCulture) + " %"
                + "\n" + Tr.T("Накопительно", "Топтолгон", "Cumulative", "Kümülatif", "To'plangan") + ": "
                + item.Cumulative.ToString("0.##", CultureInfo.CurrentCulture) + " %");
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, topPad + plotHeight - barHeight);
            canvas.Children.Add(bar);

            // Название под столбцом — повёрнутое: при двух десятках товаров горизонтальные
            // подписи наезжают друг на друга и не читается ни одна.
            var label = CaptionLabel(container, item.Label, labelWidth);
            Canvas.SetLeft(label, x + barWidth / 2);
            Canvas.SetTop(label, topPad + plotHeight + 6);
            canvas.Children.Add(label);
        }

        // Полоса клика на каждый товар — во всю высоту поля и поверх столбцов, сетки и
        // ломаной. Добавляется последней, чтобы ничто её не перекрывало.
        if (onItemClick is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var zone = ClickZone(items[i].Label, step, plotHeight + 6, onItemClick);
                Canvas.SetLeft(zone, leftPad + i * step);
                Canvas.SetTop(zone, topPad);
                canvas.Children.Add(zone);
            }
        }

        // Накопительная ломаная по правой шкале.
        var linePoints = new Avalonia.Points();
        for (var i = 0; i < items.Count; i++)
        {
            var x = leftPad + i * step + gap / 2 + barWidth / 2;
            var y = topPad + (1 - Math.Clamp(items[i].Cumulative, 0, 100) / 100.0) * plotHeight;
            linePoints.Add(new Point(x, y));
        }

        // 2026-10-18, доводка О4: ломаная — цветом текста темы (тёмно-синяя #0F172A на тёмном фоне не была видна).
        var polyline = new Polyline
        {
            Points = linePoints,
            Stroke = Brush.Parse("#0F172A"),
            StrokeThickness = 2,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        polyline.Bind(Shape.StrokeProperty, container.GetResourceObservable("BrushText"));
        canvas.Children.Add(polyline);

        foreach (var point in linePoints)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = Brush.Parse("#0F172A"),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            dot.Bind(Shape.StrokeProperty, container.GetResourceObservable("BrushText"));
            Canvas.SetLeft(dot, point.X - 3.5);
            Canvas.SetTop(dot, point.Y - 3.5);
            canvas.Children.Add(dot);
        }

        container.Children.Add(new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = height + 6,
        });
    }

    /// <summary>2026-10-21 (пакет 2, п. 86, аудит 18.10: подписи столбцов ABC «обрезаны», серые 10 px плохо читаются): подпись
    /// под столбцом — шрифт 11 цветом второстепенного текста темы (был постоянный серый 10), полное название — во всплывающей
    /// подсказке, если не влезло.</summary>
    private static TextBlock CaptionLabel(Control themeSource, string text, double width)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Brushes.Gray,
            Width = width,
            TextTrimming = TextTrimming.CharacterEllipsis,
            RenderTransform = new RotateTransform(48),
            RenderTransformOrigin = RelativePoint.TopLeft,
        };
        label.Bind(TextBlock.ForegroundProperty, themeSource.GetResourceObservable("BrushTextSoft"));
        ToolTip.SetTip(label, text);
        return label;
    }

    /// <summary>Та же Парето, но столбцами — как её строят в Excel: на каждый товар три
    /// столбца рядом (значение по левой шкале, накопленная доля и постоянный порог 80 % по
    /// правой).
    ///
    /// Стоит рядом с обычной Парето, а не вместо неё: по ломаной удобнее ловить перелом, а по
    /// трём столбцам — сравнивать накопленную долю с порогом, потому что обе величины меряются
    /// одной высотой. Владелец просил обе.
    ///
    /// Цвета здесь по назначению столбца, а не по группе ABC: три разноцветных ряда рядом
    /// читаются только при постоянных цветах, а группа видна на соседней диаграмме.</summary>
    public static void RenderParetoColumns(
        StackPanel container,
        IReadOnlyList<(string Label, double Value, double Cumulative, string ValueText)> items,
        string valueTitle,
        Action<string>? onItemClick = null)
    {
        container.Children.Clear();
        if (items.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет продаж за выбранный период", "Тандалган мезгилде сатуу жок", "No sales in the selected period", "Seçilen dönemde satış yok", "Tanlangan davrda sotuv yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        const string ColorValue = "#4E86C7";
        const string ColorCumulative = "#ED7D31";
        const string ColorThreshold = "#A5A5A5";
        const double Threshold = 80.0;

        // 2026-10-18, доводка О4: подписи под группами столбцов не обрезаются (см. RenderPareto).
        const double labelWidth = 170;
        // 2026-10-21 (пакет 2, п. 86): поле под подписями 130 → 150, как в RenderPareto.
        const double height = 380;
        const double topPad = 14, bottomPad = 150, leftPad = 74, rightPad = 52;
        const double barWidth = 13, innerGap = 2, groupGap = 18;
        var groupWidth = barWidth * 3 + innerGap * 2;
        var step = groupWidth + groupGap;
        var plotHeight = height - topPad - bottomPad;
        var plotWidth = step * items.Count;

        var maxValue = items.Max(i => i.Value);
        if (maxValue <= 0) maxValue = 1;

        var canvas = new Canvas { Width = leftPad + plotWidth + Math.Max(rightPad, labelWidth * 0.67 - groupWidth / 2 - groupGap / 2 + 8), Height = height };
        var gridBrush = new SolidColorBrush(Color.Parse("#94A3B8"), 0.35);

        for (var percent = 0; percent <= 100; percent += 20)
        {
            var y = topPad + (1 - percent / 100.0) * plotHeight;
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(leftPad, y),
                EndPoint = new Point(leftPad + plotWidth, y),
                Stroke = gridBrush,
                StrokeThickness = 1,
            });

            var left = new TextBlock
            {
                Text = (maxValue * percent / 100.0).ToString("N0", CultureInfo.CurrentCulture),
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = leftPad - 8,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(left, 0);
            Canvas.SetTop(left, y - 7);
            canvas.Children.Add(left);

            var right = new TextBlock
            {
                Text = percent.ToString(CultureInfo.InvariantCulture) + " %",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = rightPad - 6,
            };
            Canvas.SetLeft(right, leftPad + plotWidth + 6);
            Canvas.SetTop(right, y - 7);
            canvas.Children.Add(right);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var groupX = leftPad + i * step + groupGap / 2;
            var tip = item.Label + "\n" + valueTitle + ": " + item.ValueText + "\n"
                + Tr.T("Накопительно", "Топтолгон", "Cumulative", "Kümülatif", "To'plangan") + ": "
                + item.Cumulative.ToString("0.##", CultureInfo.CurrentCulture) + " %";

            void AddBar(int index, double fraction, string color)
            {
                var barHeight = Math.Max(2, Math.Clamp(fraction, 0, 1) * plotHeight);
                var bar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = Brush.Parse(color),
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                };
                ToolTip.SetTip(bar, tip);

                Canvas.SetLeft(bar, groupX + index * (barWidth + innerGap));
                Canvas.SetTop(bar, topPad + plotHeight - barHeight);
                canvas.Children.Add(bar);
            }

            AddBar(0, item.Value / maxValue, ColorValue);
            AddBar(1, Math.Clamp(item.Cumulative, 0, 100) / 100.0, ColorCumulative);
            AddBar(2, Threshold / 100.0, ColorThreshold);

            var caption = CaptionLabel(container, item.Label, labelWidth);
            Canvas.SetLeft(caption, groupX + groupWidth / 2);
            Canvas.SetTop(caption, topPad + plotHeight + 6);
            canvas.Children.Add(caption);
        }

        if (onItemClick is not null)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var zone = ClickZone(items[i].Label, step, plotHeight + 6, onItemClick);
                Canvas.SetLeft(zone, leftPad + i * step);
                Canvas.SetTop(zone, topPad);
                canvas.Children.Add(zone);
            }
        }

        container.Children.Add(new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = height + 6,
        });

        var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void AddLegend(string color, string text)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 16, 0),
                Spacing = 6,
            };
            row.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                Background = Brush.Parse(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.Gray });
            legend.Children.Add(row);
        }

        AddLegend(ColorValue, valueTitle);
        AddLegend(ColorCumulative, Tr.T("Накопленная доля", "Топтолгон үлүш", "Cumulative share", "Kümülatif pay", "To'plangan ulush"));
        AddLegend(ColorThreshold, Tr.T("Порог 80 %", "Босого 80 %", "80% threshold", "Eşik %80", "80 % chegarasi"));
        container.Children.Add(legend);
    }

    /// <summary>Классическая картинка ABC «две колонки»: слева доля ПОЗИЦИЙ, справа доля
    /// ВЫРУЧКИ, между ними буквы сегментов и линии, показывающие, во что превращается каждая
    /// группа.
    ///
    /// Смысл именно в паре колонок: по одной колонке не видно перекоса, а он и есть весь
    /// вывод анализа — малая часть ассортимента даёт почти всю выручку. Проценты слева и
    /// справа считаются от разных итогов (штуки и деньги), поэтому колонки подписаны.</summary>
    public static void RenderAbcPyramid(
        StackPanel container,
        IReadOnlyList<(string Group, int Count, double Sum, double Share)> summary,
        string leftTitle,
        string rightTitle)
    {
        container.Children.Clear();
        if (summary.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных", "Маалымат жок", "No data", "Veri yok", "Ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        const double width = 640, height = 330;
        const double headerHeight = 34, bandsTop = headerHeight + 6;
        const double columnWidth = 200, columnGap = 130;
        const double leftX = 24;
        var rightX = leftX + columnWidth + columnGap;
        var bandsHeight = height - bandsTop - 8;

        var totalCount = summary.Sum(g => g.Count);
        if (totalCount <= 0) totalCount = 1;

        var canvas = new Canvas { Width = width, Height = height };
        var outline = new SolidColorBrush(Color.Parse("#94A3B8"), 0.5);

        void Header(double x, double w, string text)
        {
            canvas.Children.Add(new Border
            {
                Width = w,
                Height = headerHeight,
                BorderBrush = outline,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            Canvas.SetLeft(canvas.Children[^1], x);
            Canvas.SetTop(canvas.Children[^1], 0);
        }

        // 2026-10-18, этап О4 (аудит «Продажи»): подписи обычным регистром, без ЗАГЛАВНЫХ; посередине — группы A/B/C.
        Header(leftX, columnWidth, leftTitle);
        Header(leftX + columnWidth, columnGap, Tr.T("Группы", "Топтор", "Groups", "Gruplar", "Guruhlar"));
        Header(rightX, columnWidth, rightTitle);

        // Полоса группы: высота пропорциональна доле, подпись — процент внутри полосы.
        double DrawBand(double x, double y, double share, string group)
        {
            var bandHeight = Math.Max(16, share / 100.0 * bandsHeight);
            var band = new Border
            {
                Width = columnWidth,
                Height = bandHeight,
                Background = Brush.Parse(AbcColor(group)),
                Child = new TextBlock
                {
                    Text = share.ToString("0.#", CultureInfo.CurrentCulture) + " %", // 2026-10-18, доводка О4: запятая по культуре
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    // 2026-10-26: чёрный или белый — что контрастнее на цвете группы (белый на жёлтом «B» был 2:1)
                    Foreground = ChartPalette.OnColorIsDark(AbcColor(group)) ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(band, x);
            Canvas.SetTop(band, y);
            canvas.Children.Add(band);
            return bandHeight;
        }

        var leftY = bandsTop;
        var rightY = bandsTop;
        var leftCenters = new List<double>();
        var rightCenters = new List<double>();

        foreach (var group in summary)
        {
            var countShare = group.Count * 100.0 / totalCount;
            var h = DrawBand(leftX, leftY, countShare, group.Group);
            leftCenters.Add(leftY + h / 2);
            leftY += h;

            var vh = DrawBand(rightX, rightY, group.Share, group.Group);
            rightCenters.Add(rightY + vh / 2);
            rightY += vh;
        }

        // Буквы сегментов ставим посередине между «своими» полосами — так видно, что 20 %
        // позиций слева и 80 % выручки справа это одна и та же группа.
        for (var i = 0; i < summary.Count; i++)
        {
            var midY = (leftCenters[i] + rightCenters[i]) / 2;
            var letter = new TextBlock
            {
                Text = summary[i].Group,
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(AbcColor(summary[i].Group)),
                Width = columnGap,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(letter, leftX + columnWidth);
            Canvas.SetTop(letter, midY - 14);
            canvas.Children.Add(letter);

            var stroke = new SolidColorBrush(Color.Parse(AbcColor(summary[i].Group)), 0.65);
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(leftX + columnWidth, leftCenters[i]),
                EndPoint = new Point(leftX + columnWidth + columnGap * 0.34, midY),
                Stroke = stroke,
                StrokeThickness = 1.4,
            });
            canvas.Children.Add(new Line
            {
                StartPoint = new Point(leftX + columnWidth + columnGap * 0.66, midY),
                EndPoint = new Point(rightX, rightCenters[i]),
                Stroke = stroke,
                StrokeThickness = 1.4,
            });
        }

        container.Children.Add(new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = height + 6,
        });
    }

    /// <summary>Три столбца A/B/C с долей каждой группы в выручке. Отдельно от круговой
    /// диаграммы намеренно: у групп всегда ровно три значения и жёстко заданные цвета, а общая
    /// RenderPie раскрашивает сектора по своей палитре — цвета разъезжались бы с легендой и с
    /// диаграммой Парето на этой же вкладке.</summary>
    public static void RenderAbcGroups(
        StackPanel container,
        IReadOnlyList<(string Group, int Count, double Sum, double Share)> summary,
        string? itemsWord = null)
    {
        container.Children.Clear();
        if (summary.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных", "Маалымат жок", "No data", "Veri yok", "Ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        const double height = 190;
        const double plotHeight = 140;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 26,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var maxShare = summary.Max(g => g.Share);
        if (maxShare <= 0) maxShare = 1;

        foreach (var group in summary)
        {
            var column = new StackPanel
            {
                Width = 96,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            column.Children.Add(new TextBlock
            {
                Text = group.Share.ToString("0.#", CultureInfo.CurrentCulture) + " %",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            });

            column.Children.Add(new Border
            {
                Width = 66,
                Height = Math.Max(6, group.Share / maxShare * plotHeight),
                Background = Brush.Parse(AbcColor(group.Group)),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            column.Children.Add(new TextBlock
            {
                Text = group.Group,
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });

            column.Children.Add(new TextBlock
            {
                // 2026-10-18, доводка О4: «товаров: 4» вместо «4 поз.».
                Text = (itemsWord ?? Tr.T("позиций", "позиция", "items", "kalem", "pozitsiya")) + ": " + group.Count.ToString(CultureInfo.CurrentCulture),
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextAlignment = TextAlignment.Center,
            });

            row.Children.Add(column);
        }

        container.Children.Add(row);
    }

    /// <summary>Легенда групп ABC: квадратик цвета, буква и что за ней стоит.</summary>
    public static void RenderAbcLegend(
        StackPanel container,
        IReadOnlyList<(string Group, int Count, double Sum, double Share)> summary,
        string unit = "сом",
        string? itemsWord = null)
    {
        container.Children.Clear();
        if (summary.Count == 0)
            return;

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var group in summary)
        {
            var chip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 18, 6),
            };
            chip.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Background = Brush.Parse(AbcColor(group.Group)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            chip.Children.Add(new TextBlock
            {
                // 2026-10-18, доводка О4: «A — товаров: 4 · 6 476 сом (87,3 %)» вместо «A: 4 поз. — … (87.3 %)».
                Text = group.Group + " — " + (itemsWord ?? Tr.T("позиций", "позиция", "items", "kalem", "pozitsiya")) + ": "
                    + group.Count.ToString(CultureInfo.CurrentCulture)
                    + " · " + group.Sum.ToString(unit == "шт." || unit == "даана" ? "0.###" : "N0", CultureInfo.CurrentCulture)
                    + " " + unit + " (" + group.Share.ToString("0.#", CultureInfo.CurrentCulture) + " %)",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(chip);
        }

        container.Children.Add(row);
    }

    /// <summary>Комбинированная диаграмма: столбцы одной величины (например, выручка) + линия
    /// другой (например, кол-во продаж), у линии своя, независимая шкала.</summary>
    public static void RenderCombined(
        StackPanel container,
        IReadOnlyList<(string Label, double BarValue, double LineValue, string BarValueText)> items,
        string barLegend,
        string lineLegend)
    {
        container.Children.Clear();
        if (items.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = Tr.T("Нет данных за выбранный период", "Тандалган мезгилде маалымат жок", "No data for the selected period", "Seçilen dönem için veri yok", "Tanlangan davr uchun ma'lumot yo'q"),
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 8) };
        legendRow.Children.Add(BuildLegendChip(ChartPalette.Hex[0], barLegend));
        legendRow.Children.Add(BuildLegendChip(SeriesBlue, lineLegend));
        container.Children.Add(legendRow);

        const double height = 170;
        const double topPad = 10, bottomPad = 26, leftPad = 10, rightPad = 10;
        const double stepWidth = 56, barWidth = 26;
        var plotHeight = height - topPad - bottomPad;
        var canvasWidth = Math.Max(260, leftPad + rightPad + stepWidth * Math.Max(1, items.Count - 1) + stepWidth);

        var maxBar = items.Max(i => i.BarValue);
        if (maxBar <= 0) maxBar = 1;
        var maxLine = items.Max(i => i.LineValue);
        var minLine = Math.Min(0, items.Min(i => i.LineValue));
        if (maxLine <= minLine) maxLine = minLine + 1;

        var canvas = new Canvas { Width = canvasWidth, Height = height };

        double XAt(int i) => leftPad + stepWidth * i + stepWidth / 2;

        for (var i = 0; i < items.Count; i++)
        {
            var barHeight = Math.Max(2, items[i].BarValue / maxBar * plotHeight);
            var bar = new Border
            {
                Width = barWidth,
                Height = barHeight,
                Background = Brush.Parse(ChartPalette.Hex[0]),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
            };
            Canvas.SetLeft(bar, XAt(i) - barWidth / 2);
            Canvas.SetTop(bar, topPad + plotHeight - barHeight);
            canvas.Children.Add(bar);

            var label = new TextBlock
            {
                Text = items[i].Label,
                FontSize = 10,
                Foreground = Brushes.Gray,
                Width = stepWidth,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(label, XAt(i) - stepWidth / 2);
            Canvas.SetTop(label, height - bottomPad + 6);
            canvas.Children.Add(label);
        }

        var linePoints = new Avalonia.Points();
        for (var i = 0; i < items.Count; i++)
        {
            var normalized = (items[i].LineValue - minLine) / (maxLine - minLine);
            linePoints.Add(new Point(XAt(i), topPad + (1 - normalized) * plotHeight));
        }

        canvas.Children.Add(new Polyline
        {
            Points = linePoints,
            Stroke = Brush.Parse(SeriesBlue),
            StrokeThickness = 2.5,
            StrokeJoin = PenLineJoin.Round,
        });

        foreach (var p in linePoints)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Brush.Parse(SeriesBlue) };
            Canvas.SetLeft(dot, p.X - 4);
            Canvas.SetTop(dot, p.Y - 4);
            canvas.Children.Add(dot);
        }

        var scroller = new ScrollViewer
        {
            Content = canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = height + 4,
        };
        container.Children.Add(scroller);
    }

    private static Border BuildLegendChip(string colorHex, string text)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chip.Children.Add(new Border
        {
            Width = 10, Height = 10, Background = Brush.Parse(colorHex),
            CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
        });
        chip.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = chip };
    }
}
