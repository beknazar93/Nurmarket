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
        foreach (var visual in Walk(root).ToList())
        {
            if (visual is UniformGrid uniform)
            {
                ApplyUniform(uniform, narrow);
                continue;
            }
            if (visual is StackPanel stack)
            {
                ApplyStack(stack, narrow, root);
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
                // 2026-10-05, снимки владельца (программа владельца на телефоне): ряд «заголовок | кнопки»
                // (колонки «*, Auto…» — «Продажи», «Склад», «Пополнение») уходил за правый край. Такой ряд —
                // тоже столбиком, но только когда он правда не помещается (проверяется на каждом проходе).
                if (!RowGridShape(grid))
                {
                    Rejected.AddOrUpdate(grid, Marker); // устройство сетки не подходит — больше не проверяем
                    continue;
                }
                if (!RowChildrenQualify(grid) || !GridOverflows(grid, root))
                    continue;
            }
            else if (!ChildrenQualify(grid))
                continue; // элементы могут появиться позже (данные загрузятся) — проверим в следующий раз
            state = new GridState { Columns = grid.ColumnDefinitions, Rows = grid.RowDefinitions };
            foreach (var child in grid.Children.OfType<Control>())
                state.Children.Add((child, Grid.GetColumn(child)));
            Grids.AddOrUpdate(grid, state);
            Toggle(grid, state, true);
        }
    }

    /// <summary>2026-10-05, владелец: «программа тротлит сама по себе». Перестройка обходила ВСЁ дерево окна раз в
    /// 0,4 с при любом пересчёте разметки (прокрутка — тоже пересчёт), включая каждую ячейку таблиц. Внутрь таблиц,
    /// строк списков, полей ввода и полос прокрутки не заходим: там нечего ставить столбиком, а элементов — тысячи.</summary>
    private static IEnumerable<Visual> Walk(Visual root)
    {
        var stack = new Stack<Visual>();
        foreach (var child in root.GetVisualChildren())
            stack.Push(child);
        while (stack.Count > 0)
        {
            var visual = stack.Pop();
            yield return visual;
            if (visual is TextBox or ScrollBar or ListBoxItem
                || visual.GetType().Name is "DataGrid" or "DataGridRow" or "DataGridCellsPresenter")
                continue;
            foreach (var child in visual.GetVisualChildren())
                stack.Push(child);
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

    private static void ApplyStack(StackPanel panel, bool narrow, Visual root)
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
        // 2026-10-05: раньше сравнивалось DesiredSize с Bounds — но Avalonia обрезает DesiredSize до доступной
        // ширины, и ряд «Поиск | Excel | Word | Обновить» не переносился никогда. Теперь — сумма естественных
        // ширин элементов против места до правого края окна. Ряды, которые сами листаются вбок (полосы вкладок
        // в ScrollViewer), не трогаются.
        if (InHorizontalScroller(panel, root))
            return;
        var visible = panel.Children.Where(c => c.IsVisible).ToList();
        var natural = visible.Sum(c => c.DesiredSize.Width) + panel.Spacing * Math.Max(0, visible.Count - 1);
        if (!Overflows(panel, natural, root))
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

    /// <summary>2026-10-05: сколько места до правого края окна (или до края самого элемента, если он уже).</summary>
    private static bool Overflows(Control control, double naturalWidth, Visual root)
    {
        var available = control.Bounds.Width > 0 ? control.Bounds.Width : double.MaxValue;
        if (root is Layoutable { Bounds.Width: > 0 } window && control.TranslatePoint(default, root) is { } origin)
            available = Math.Min(available, window.Bounds.Width - origin.X);
        return available > 0 && naturalWidth > available + 2;
    }

    private static bool InHorizontalScroller(Control control, Visual root)
    {
        for (var v = control.GetVisualParent(); v is not null && !ReferenceEquals(v, root); v = v.GetVisualParent())
        {
            if (v is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled })
                return true;
        }
        return false;
    }

    /// <summary>2026-10-05: ряд «заголовок | кнопки»: одна строка, колонки «*», «Auto» и постоянной ширины,
    /// хотя бы одна не резиновая (сетки из одних резиновых колонок — правило StructureQualifies).</summary>
    private static bool RowGridShape(Grid grid)
    {
        if (grid.TemplatedParent is not null || grid.Classes.Contains("no-reflow"))
            return false;
        var columns = grid.ColumnDefinitions;
        if (columns.Count < 2 || columns.Count > 8 || grid.RowDefinitions.Count > 1)
            return false;
        return columns.Any(c => c.Width.IsAuto || (c.Width.IsAbsolute && c.Width.Value > 40));
    }

    private static bool RowChildrenQualify(Grid grid)
    {
        // 2026-10-05: бывает виден один элемент — заголовок раздела скрыт в программе владельца, а ряд
        // «Поиск | Excel | Word | Обновить» в колонке «Auto» не помещается («Продажи», «ABC-анализ»): переносить
        // этот ряд можно, только когда колонка станет резиновой — поэтому достаточно одного видимого элемента.
        var children = grid.Children.OfType<Control>().Where(c => c.IsVisible).ToList();
        if (children.Count < 1)
            return false;
        return !children.Any(c => Grid.GetColumnSpan(c) > 1 || Grid.GetRow(c) > 0);
    }

    /// <summary>Естественная ширина ряда: колонки «Auto» — по самому широкому элементу, постоянные — как заданы,
    /// резиновые — не меньше 60 точек (заголовок или поле должны хоть как-то читаться).</summary>
    private static bool GridOverflows(Grid grid, Visual root)
    {
        if (grid.Bounds.Width <= 0 || InHorizontalScroller(grid, root))
            return false;
        double natural = 0;
        for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
        {
            var width = grid.ColumnDefinitions[i].Width;
            var inColumn = grid.Children.OfType<Control>().Where(c => c.IsVisible && Grid.GetColumn(c) == i).ToList();
            if (width.IsAbsolute)
                natural += width.Value;
            else if (width.IsAuto)
                natural += inColumn.Count == 0 ? 0 : inColumn.Max(c => c.DesiredSize.Width);
            else if (inColumn.Count > 0)
                natural += Math.Max(60, inColumn.Max(c => c.DesiredSize.Width));
        }
        return Overflows(grid, natural, root);
    }

    private static bool StructureQualifies(Grid grid)
    {
        if (grid.TemplatedParent is not null)
            return false; // часть шаблона кнопки, списка, полосы прокрутки
        // 2026-10-05: окно само перестраивает эту сетку на узком экране (NarrowLayout) — например, делит
        // высоту между списком и составом чека, а не «по содержимому», как здесь.
        if (grid.Classes.Contains("no-reflow"))
            return false;
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
