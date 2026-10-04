using Avalonia;
using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, владелец: «адаптацию под все экраны» (Android, вертикальный телефон). Сетка из
/// двух колонок «A | промежуток | B» на узком окне перестраивается в столбик: A, промежуток, B — друг под
/// другом; на широком возвращается как была. Колонка ребёнка становится его строкой (0, 1, 2…).
/// Подключается только там, где это нужно (Android), — в Windows раскладка не меняется.</summary>
public static class NarrowStack
{
    /// <param name="widthSource">Чья ширина решает (обычно само окно).</param>
    /// <param name="grid">Сетка с колонками.</param>
    /// <param name="breakpoint">Уже этого — столбиком.</param>
    /// <param name="stackedRows">Строки в столбике, например «Auto,16,*».</param>
    public static void Attach(Control widthSource, Grid grid, double breakpoint, string stackedRows)
    {
        ColumnDefinitions? originalColumns = null;
        RowDefinitions? originalRows = null;
        var originalColumn = new Dictionary<Control, int>();
        var stacked = false;

        void Apply()
        {
            var w = widthSource.Bounds.Width;
            if (w <= 0)
                return;
            // Запас 40 точек, чтобы раскладка не прыгала туда-обратно на границе.
            var want = stacked ? w < breakpoint + 40 : w < breakpoint;
            if (want == stacked)
                return;
            stacked = want;

            if (want)
            {
                originalColumns = grid.ColumnDefinitions;
                originalRows = grid.RowDefinitions;
                originalColumn.Clear();
                foreach (var child in grid.Children.OfType<Control>())
                    originalColumn[child] = Grid.GetColumn(child);
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions(stackedRows);
                foreach (var (child, column) in originalColumn)
                {
                    Grid.SetColumn(child, 0);
                    Grid.SetRow(child, column);
                }
            }
            else
            {
                if (originalColumns is not null)
                    grid.ColumnDefinitions = originalColumns;
                grid.RowDefinitions = originalRows ?? new RowDefinitions();
                foreach (var (child, column) in originalColumn)
                {
                    Grid.SetRow(child, 0);
                    Grid.SetColumn(child, column);
                }
            }
        }

        widthSource.SizeChanged += (_, _) => Apply();
        Apply();
    }
}
