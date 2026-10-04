using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace NurMarketKassa;

/// <summary>2026-10-04, владелец (Android, телефон): «ты не адаптировал админку», «пройдись по всем модалкам и
/// посмотри за адаптацией». Окна кассы и разделы программы владельца сделаны для широкого экрана: ряды
/// «блок | блок | блок» (плашки показателей, панели, пары полей) на телефоне сжимались в нечитаемые столбики.
/// Здесь — общее правило для всех окон на узком экране: сетка, у которой только «резиновые» колонки и узкие
/// промежутки (до 40 точек), одна строка и каждый элемент в своей колонке, встаёт столбиком (колонка → строка);
/// на широком экране возвращается как была. Не трогаются: шаблоны элементов (кнопки, списки, полосы прокрутки),
/// сетки с колонками «по содержимому» (значок + текст, таблицы), ряды из одних кнопок (Сегодня/Неделя/Месяц).</summary>
internal static class AutoReflow
{
    /// <summary>Уже этого — «телефон», ряды столбиком.</summary>
    public const double NarrowWidth = 700;

    private sealed class GridState
    {
        public ColumnDefinitions Columns = null!;
        public RowDefinitions Rows = null!;
        public readonly List<(Control Child, int Column)> Children = new();
        public bool Stacked;
    }

    private static readonly ConditionalWeakTable<Grid, GridState> Grids = new();
    private static readonly ConditionalWeakTable<Grid, object> Rejected = new();
    private static readonly object Marker = new();

    public static void Apply(Visual root, bool narrow)
    {
        foreach (var visual in root.GetVisualDescendants().ToList())
        {
            if (visual is UniformGrid uniform)
            {
                ApplyUniform(uniform, narrow);
                continue;
            }
            if (visual is StackPanel stack)
            {
                ApplyStack(stack, narrow);
                continue;
            }
            if (visual is not Grid grid)
                continue;
            if (Grids.TryGetValue(grid, out var state))
            {
                SyncNewChildren(grid, state);
                if (state.Stacked != narrow)
                    Toggle(grid, state, narrow);
                continue;
            }
            if (!narrow || Rejected.TryGetValue(grid, out _))
                continue;
            if (!StructureQualifies(grid))
            {
                Rejected.AddOrUpdate(grid, Marker); // устройство сетки не подходит — больше не проверяем
                continue;
            }
            if (!ChildrenQualify(grid))
                continue; // элементы могут появиться позже (данные загрузятся) — проверим в следующий раз
            state = new GridState { Columns = grid.ColumnDefinitions, Rows = grid.RowDefinitions };
            foreach (var child in grid.Children.OfType<Control>())
                state.Children.Add((child, Grid.GetColumn(child)));
            Grids.AddOrUpdate(grid, state);
            Toggle(grid, state, true);
        }
    }

    // ---- ряды плашек (UniformGrid по 6–8 колонок) — по 2 ----
    private static readonly ConditionalWeakTable<UniformGrid, StrongBox<int>> Uniforms = new();

    private static void ApplyUniform(UniformGrid grid, bool narrow)
    {
        if (Uniforms.TryGetValue(grid, out var original))
        {
            var want = narrow ? Math.Min(original.Value, 2) : original.Value;
            if (grid.Columns != want)
                grid.Columns = want;
            return;
        }
        if (!narrow || grid.TemplatedParent is not null || grid.Columns < 3)
            return;
        var children = grid.Children.OfType<Control>().ToList();
        if (children.Count < 3 || children.All(c => c is Avalonia.Controls.Button or ToggleButton))
            return; // клавиатура, ряды кнопок — как есть
        Uniforms.AddOrUpdate(grid, new StrongBox<int>(grid.Columns));
        grid.Columns = 2;
    }

    // ---- строка, которая не помещается в ширину, — с переносом, как слова в тексте ----
    private static readonly ConditionalWeakTable<StackPanel, WrapPanel> WrappedStacks = new();

    private static void ApplyStack(StackPanel panel, bool narrow)
    {
        if (WrappedStacks.TryGetValue(panel, out var wrap))
        {
            if (!narrow)
            {
                // Широкий экран — элементы обратно в строку.
                var items = wrap.Children.ToList();
                wrap.Children.Clear();
                panel.Children.Remove(wrap);
                foreach (var item in items)
                    panel.Children.Add(item);
                panel.Orientation = Orientation.Horizontal;
                WrappedStacks.Remove(panel);
            }
            return;
        }
        if (!narrow || panel.Orientation != Orientation.Horizontal || panel.TemplatedParent is not null)
            return;
        if (panel.Children.Count < 2 || panel.Bounds.Width <= 0)
            return;
        // Помещается — как есть; не помещается (обрезается справа) — с переносом.
        if (panel.DesiredSize.Width <= panel.Bounds.Width + 2)
            return;
        if (panel.FindAncestorOfType<Avalonia.Controls.Button>() is not null)
            return; // значок + текст внутри кнопки
        var children = panel.Children.ToList();
        panel.Children.Clear();
        wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            // Промежуток StackPanel (Spacing) у WrapPanel задаётся отступом элементов.
            if (panel.Spacing > 0 && child is Layoutable l && l.Margin == default)
                l.Margin = new Thickness(0, 0, panel.Spacing, panel.Spacing);
            wrap.Children.Add(child);
        }
        panel.Orientation = Orientation.Vertical;
        panel.Children.Add(wrap);
        WrappedStacks.AddOrUpdate(panel, wrap);
    }

    private static bool StructureQualifies(Grid grid)
    {
        if (grid.TemplatedParent is not null)
            return false; // часть шаблона кнопки, списка, полосы прокрутки
        var columns = grid.ColumnDefinitions;
        if (columns.Count < 2 || grid.RowDefinitions.Count > 1)
            return false;
        int stars = 0, autos = 0;
        foreach (var c in columns)
        {
            if (c.Width.IsStar)
                stars++;
            else if (c.Width.IsAuto)
                autos++;
            else if (!(c.Width.IsAbsolute && c.Width.Value <= 40))
                return false;
        }
        // «блок | блок»: только резиновые колонки и промежутки; «поле | поле | поле | кнопка»: ≥3 резиновые и одна по содержимому.
        return autos == 0 ? stars >= 2 : stars >= 3 && autos == 1;
    }

    private static bool ChildrenQualify(Grid grid)
    {
        var children = grid.Children.OfType<Control>().ToList();
        if (children.Count < 2)
            return false;
        if (children.Any(c => Grid.GetColumnSpan(c) > 1 || Grid.GetRow(c) > 0))
            return false;
        // Ряд из одних кнопок помещается и в узкий экран — столбиком он только займёт место.
        return !children.All(c => c is Avalonia.Controls.Button or ToggleButton);
    }

    private static void SyncNewChildren(Grid grid, GridState state)
    {
        foreach (var child in grid.Children.OfType<Control>())
        {
            if (state.Children.Any(x => ReferenceEquals(x.Child, child)))
                continue;
            var column = Grid.GetColumn(child);
            state.Children.Add((child, column));
            if (state.Stacked)
            {
                Grid.SetColumn(child, 0);
                Grid.SetRow(child, Math.Min(column, Math.Max(0, grid.RowDefinitions.Count - 1)));
            }
        }
    }

    private static void Toggle(Grid grid, GridState state, bool stacked)
    {
        if (stacked)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("*");
            var rows = new RowDefinitions();
            foreach (var c in state.Columns)
                rows.Add(new RowDefinition(c.Width.IsAbsolute ? new GridLength(Math.Min(c.Width.Value, 12)) : GridLength.Auto));
            grid.RowDefinitions = rows;
            foreach (var (child, column) in state.Children)
            {
                Grid.SetColumn(child, 0);
                Grid.SetRow(child, Math.Min(column, rows.Count - 1));
            }
        }
        else
        {
            grid.ColumnDefinitions = state.Columns;
            grid.RowDefinitions = state.Rows;
            foreach (var (child, column) in state.Children)
            {
                Grid.SetRow(child, 0);
                Grid.SetColumn(child, column);
            }
        }
        state.Stacked = stacked;
    }
}
