using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>
/// 2026-09-30, владелец: «пройдись по оптимизации экрана настроек к каждому экрану». Страницы настроек
/// собраны из строк в 2–3 колонки («Печать», «Монитор», «Весы», «Операции»…): на узком окне
/// (1024×768 при 125–150 %, 800×600, квадратные моноблоки) колонки сжимались так, что поля и подписи
/// обрезались. Одно правило для всех вкладок: когда страница уже <see cref="NarrowWidth"/>, строка из
/// нескольких «резиновых» колонок (между ними — только узкие отступы до 24) ставит свои блоки друг под
/// другом; стала шире — всё возвращается как было. Таблицы не трогаются: строка, где в колонках лежит
/// просто текст (TextBlock) или у сетки несколько строк, остаётся как есть.
/// </summary>
internal static class SettingsReflow
{
    public const double NarrowWidth = 760;

    private sealed class Saved
    {
        public required List<GridLength> Columns { get; init; }
        public required List<(Control Child, int Column, int Row, Thickness Margin)> Children { get; init; }
    }

    private static readonly ConditionalWeakTable<Grid, Saved> Stacked = new();

    public static void Apply(Control? root, double width)
    {
        if (root is null || width <= 0)
            return;
        var narrow = width < NarrowWidth;
        foreach (var grid in root.GetLogicalDescendants().OfType<Grid>().ToList())
        {
            if (narrow)
                Stack(grid);
            else
                Restore(grid);
        }
    }

    private static bool IsStar(ColumnDefinition c) => c.Width.IsStar;

    private static bool IsSpacer(ColumnDefinition c) => c.Width.IsAbsolute && c.Width.Value <= 24;

    private static void Stack(Grid grid)
    {
        if (Stacked.TryGetValue(grid, out _))
            return;
        if (grid.RowDefinitions.Count > 1 || grid.ColumnDefinitions.Count < 2)
            return;
        var stars = new List<int>();
        for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
        {
            var c = grid.ColumnDefinitions[i];
            if (IsStar(c))
                stars.Add(i);
            else if (!IsSpacer(c))
                return; // Auto/широкие колонки (подпись + поле + кнопка) — не наш случай
        }
        if (stars.Count < 2)
            return;
        var children = grid.Children.OfType<Control>().ToList();
        if (children.Count == 0)
            return;
        foreach (var child in children)
        {
            var column = Grid.GetColumn(child);
            if (Grid.GetColumnSpan(child) > 1 || !stars.Contains(column) || child is TextBlock)
                return; // таблица, растянутая ячейка или ребёнок в колонке-отступе
        }

        Stacked.Add(grid, new Saved
        {
            Columns = grid.ColumnDefinitions.Select(c => c.Width).ToList(),
            Children = children.Select(c => (c, Grid.GetColumn(c), Grid.GetRow(c), c.Margin)).ToList(),
        });
        grid.ColumnDefinitions = new ColumnDefinitions("*");
        grid.RowDefinitions = new RowDefinitions(string.Join(",", stars.Select(_ => "Auto")));
        foreach (var child in children)
        {
            var index = stars.IndexOf(Grid.GetColumn(child));
            Grid.SetColumn(child, 0);
            Grid.SetRow(child, index);
            if (index > 0)
                child.Margin = new Thickness(child.Margin.Left, child.Margin.Top + 10, child.Margin.Right, child.Margin.Bottom);
        }
    }

    private static void Restore(Grid grid)
    {
        if (!Stacked.TryGetValue(grid, out var saved))
            return;
        Stacked.Remove(grid);
        grid.RowDefinitions = new RowDefinitions();
        var columns = new ColumnDefinitions();
        foreach (var width in saved.Columns)
            columns.Add(new ColumnDefinition(width));
        grid.ColumnDefinitions = columns;
        foreach (var (child, column, row, margin) in saved.Children)
        {
            Grid.SetColumn(child, column);
            Grid.SetRow(child, row);
            child.Margin = margin;
        }
    }
}
