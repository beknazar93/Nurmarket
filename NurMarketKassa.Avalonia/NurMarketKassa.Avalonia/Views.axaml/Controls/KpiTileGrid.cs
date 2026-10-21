using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-10-18, редизайн программы владельца (этап О3, правило аудита 3): ряд плиток-показателей раздела.
/// Раньше плитки стояли в UniformGrid с жёстким числом колонок (6 или 8), и на узком разделе суммы обрезались
/// («1 311,00 с», «20 943 555 …»). Здесь колонок столько, сколько помещается плиток шириной не меньше
/// <see cref="MinTileWidth"/>; остальные уходят вторым рядом, ряды выравниваются (8 плиток на 5 мест — 4 + 4).
/// Плитки ряда — одной ширины и одной высоты. Сумму внутри плитки уменьшает Viewbox (стиль sec-kpi-fit).</summary>
public sealed class KpiTileGrid : Panel
{
    public static readonly StyledProperty<double> MinTileWidthProperty =
        AvaloniaProperty.Register<KpiTileGrid, double>(nameof(MinTileWidth), 170);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<KpiTileGrid, double>(nameof(Spacing), 8);

    static KpiTileGrid()
    {
        AffectsMeasure<KpiTileGrid>(MinTileWidthProperty, SpacingProperty);
    }

    /// <summary>Наименьшая ширина плитки: уже — плитка уходит в следующий ряд.</summary>
    public double MinTileWidth
    {
        get => GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    /// <summary>Промежуток между плитками по горизонтали и вертикали.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    // Ширина ячейки, с которой плитки измерены в MeasureOverride (ревью О3: Arrange мог прийти с другой шириной —
    // тогда высота плиток считалась бы по чужой ширине, и перенесённая подпись вылезала бы за плитку).
    private double _measuredCellWidth = double.NaN;

    private List<Control> VisibleChildren()
    {
        var list = new List<Control>(Children.Count);
        foreach (var child in Children)
        {
            if (child.IsVisible)
                list.Add(child);
        }
        return list;
    }

    private int Columns(double width, int count)
    {
        var spacing = Math.Max(0, Spacing);
        var fit = Math.Max(1, (int)Math.Floor((width + spacing) / (Math.Max(1, MinTileWidth) + spacing)));
        var columns = Math.Min(fit, count);
        var rows = (int)Math.Ceiling(count / (double)columns);
        // Ряды поровну: 8 плиток в 5 колонок — это 4 + 4, а не 5 + 3.
        return (int)Math.Ceiling(count / (double)rows);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        if (children.Count == 0)
            return default;

        var spacing = Math.Max(0, Spacing);
        var width = double.IsInfinity(availableSize.Width)
            ? children.Count * (MinTileWidth + spacing) - spacing
            : availableSize.Width;
        var columns = Columns(width, children.Count);
        var cellWidth = Math.Max(0, (width - spacing * (columns - 1)) / columns);
        _measuredCellWidth = cellWidth;

        double height = 0;
        for (var start = 0; start < children.Count; start += columns)
        {
            double rowHeight = 0;
            for (var i = start; i < Math.Min(start + columns, children.Count); i++)
            {
                children[i].Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            }
            height += rowHeight + (start > 0 ? spacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        if (children.Count == 0)
            return finalSize;

        var spacing = Math.Max(0, Spacing);
        var columns = Columns(finalSize.Width, children.Count);
        var cellWidth = Math.Max(0, (finalSize.Width - spacing * (columns - 1)) / columns);
        // Ширина при раскладке другая, чем при измерении (иные колонки) — плитки перемеряются по настоящей ширине.
        if (double.IsNaN(_measuredCellWidth) || Math.Abs(cellWidth - _measuredCellWidth) > 0.5)
        {
            foreach (var child in children)
                child.Measure(new Size(cellWidth, double.PositiveInfinity));
            _measuredCellWidth = cellWidth;
        }
        double y = 0;
        for (var start = 0; start < children.Count; start += columns)
        {
            var end = Math.Min(start + columns, children.Count);
            double rowHeight = 0;
            for (var i = start; i < end; i++)
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                children[i].Arrange(new Rect((i - start) * (cellWidth + spacing), y, cellWidth, rowHeight));
            y += rowHeight + spacing;
        }
        return finalSize;
    }
}
