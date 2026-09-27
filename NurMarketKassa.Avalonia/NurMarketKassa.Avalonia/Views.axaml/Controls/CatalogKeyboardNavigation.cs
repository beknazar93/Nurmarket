using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>Часть экрана кассы, где лежат плитки каталога, — то, чем окно кассы управляет с
/// клавиатуры (стрелки входят в каталог, Num + добавляет товар под рамкой, Num − убавляет его,
/// Ctrl+F — поиск). Раньше окно знало только одну такую часть — CatalogPanelView; с раскладками
/// (2026-09-28) плитки есть и в других видах кассы, и окно спрашивает активную раскладку через
/// этот интерфейс. Раскладка без плиток (1С) просто не реализует его — клавиши тогда ведут себя
/// так же, как раньше при скрытом каталоге.</summary>
public interface ICatalogKeyboardSurface
{
    /// <summary>Первая стрелка, когда фокус ещё не в каталоге: ставит курсор на плитку.
    /// false — плиток нет или каталог не виден, стрелку окно не забирает.</summary>
    bool TryEnterCatalogNavigation();

    /// <summary>Товар под клавиатурной рамкой; null — рамки нет.</summary>
    CatalogProductTileVm? HighlightedProduct { get; }

    /// <summary>Добавляет в чек товар под рамкой (как клик мышью). false — рамки нет.</summary>
    bool TryAddHighlightedProduct();

    /// <summary>Поле поиска товара получает фокус (действие «Поиск товара» в Настройки → Клавиши).</summary>
    void FocusProductSearch();

    /// <summary>Фокус уже где-то внутри каталога — тогда стрелками управляет сам список, а не окно.</summary>
    bool ContainsFocus(Control? focused);
}

/// <summary>Клавиатурная навигация по плиткам каталога — общая для всех раскладок кассы.
///
/// 2026-09-28: вынесено из CatalogPanelView без изменения логики, чтобы у каждой раскладки
/// (Табличная, Карточки, Минимал, Профи) стрелки работали одинаково, а не копией кода.
/// Встроенная навигация ListBox + WrapPanel умеет только Влево/Вправо и только в пределах
/// страницы, поэтому считаем сами по индексу: Влево/Вправо — соседняя плитка (с переносом через
/// конец ряда), Вверх/Вниз — плитка того же столбца в соседнем ряду; число столбцов берётся из
/// фактической раскладки. Шаг за первую/последнюю плитку страницы листает на соседнюю страницу.
/// Стрелки с Ctrl/Shift/Alt не трогаем.</summary>
public static class CatalogKeyboardNavigation
{
    /// <summary>Подключает стрелки к списку плиток. Tunnel: разбираем их раньше встроенной
    /// обработки ListBox.</summary>
    public static void Attach(ListBox listBox, Func<CatalogPanelViewModel?> catalog) =>
        listBox.AddHandler(InputElement.KeyDownEvent,
            (object? sender, KeyEventArgs e) => OnArrowKeyTunnel(listBox, catalog(), e),
            RoutingStrategies.Tunnel);

    /// <summary>Товар под клавиатурной рамкой в одном из списков. Фокус, полученный мышью (клик по
    /// плитке или мимо кнопки в её край), курсором не считается — нужен :focus-visible.</summary>
    public static CatalogProductTileVm? GetHighlightedProduct(Visual anchor, params ListBox[] lists)
    {
        if (TopLevel.GetTopLevel(anchor)?.FocusManager?.GetFocusedElement() is not ListBoxItem item
            || !item.Classes.Contains(":focus-visible"))
            return null;

        foreach (var list in lists)
        {
            if (list.IndexFromContainer(item) >= 0)
                return item.DataContext as CatalogProductTileVm;
        }

        return null;
    }

    /// <summary>«Входит» в сетку товаров: ставит курсор на товар, где он стоял в прошлый раз, или
    /// на первую плитку и передаёт клавиатурный фокус самой плитке.</summary>
    public static bool TryEnter(CatalogPanelViewModel catalog, ListBox listBox)
    {
        if (catalog.Products.Count == 0)
            return false;

        var index = catalog.SelectedProduct is { } selected ? catalog.Products.IndexOf(selected) : -1;
        return FocusTile(listBox, index >= 0 ? index : 0);
    }

    /// <summary>Фокус стоит внутри контрола (или на нём самом).</summary>
    public static bool IsWithin(Control? focused, Control container)
    {
        for (var c = focused; c is not null; c = c.Parent as Control)
        {
            if (ReferenceEquals(c, container))
                return true;
        }
        return false;
    }

    private static void OnArrowKeyTunnel(ListBox listBox, CatalogPanelViewModel? catalog, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        if (catalog is null || listBox.ItemCount == 0)
            return;

        // Даже когда двигаться некуда (первая плитка первой страницы), стрелку забираем: иначе её
        // подхватит встроенная навигация и курсор уйдёт туда, где его не ждут.
        e.Handled = true;
        MoveKeyboardCursor(listBox, catalog, e.Key, e.Source as Visual);
    }

    public static void MoveKeyboardCursor(ListBox listBox, CatalogPanelViewModel catalog, Key key, Visual? source)
    {
        var count = listBox.ItemCount;
        var current = IndexOfTileContaining(listBox, source);
        if (current < 0)
            current = listBox.SelectedIndex;
        if (current < 0 || current >= count)
        {
            FocusTile(listBox, 0);
            return;
        }

        var columns = CountColumns(listBox);
        var column = current % columns;
        var target = key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Up => current - columns,
            _ => current + columns,
        };

        if (target >= 0 && target < count)
        {
            FocusTile(listBox, target);
            return;
        }

        // Вниз, а под плиткой пусто, потому что последний ряд неполный, — на последнюю плитку,
        // листать страницу ещё рано.
        if (key == Key.Down && current / columns < (count - 1) / columns)
        {
            FocusTile(listBox, count - 1);
            return;
        }

        var forward = target >= count;
        var pageCommand = forward ? catalog.NextPageCommand : catalog.PreviousPageCommand;
        if (!pageCommand.CanExecute(null))
            return;

        pageCommand.Execute(null);
        // Плитки новой страницы должны получить размеры до того, как на них ставить фокус и
        // прокручивать к ним.
        listBox.UpdateLayout();
        var newCount = listBox.ItemCount;
        if (newCount == 0)
            return;

        var lastRowStart = (newCount - 1) / columns * columns;
        FocusTile(listBox, key switch
        {
            Key.Right => 0,
            Key.Left => newCount - 1,
            Key.Down => Math.Min(column, newCount - 1),
            _ => Math.Min(lastRowStart + column, newCount - 1),
        });
    }

    /// <summary>Ставит курсор на плитку: выделение (оно же SelectedProduct) и клавиатурный фокус
    /// на саму плитку, а не на кнопку внутри неё; прокручивает сетку так, чтобы плитку было видно.</summary>
    public static bool FocusTile(ListBox listBox, int index)
    {
        if (index < 0 || index >= listBox.ItemCount)
            return false;

        listBox.SelectedIndex = index;
        listBox.ScrollIntoView(index);
        if (listBox.ContainerFromIndex(index) is not { } tile || !tile.Focus(NavigationMethod.Directional))
            return false;

        tile.BringIntoView();
        return true;
    }

    private static int IndexOfTileContaining(ListBox listBox, Visual? source)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, listBox); visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem tile)
                return listBox.IndexFromContainer(tile);
        }
        return -1;
    }

    /// <summary>Сколько плиток в ряду сейчас: считаем плитки первого ряда по их координатам —
    /// ширина плитки зависит от темы, а ширина каталога от окна и разделителя. У списка строками
    /// (таблица) вторая строка всегда ниже первой — получается один столбец.</summary>
    private static int CountColumns(ListBox listBox)
    {
        if (listBox.ContainerFromIndex(0) is not { } first)
            return 1;

        var columns = 0;
        for (var i = 0; i < listBox.ItemCount; i++)
        {
            if (listBox.ContainerFromIndex(i) is not { } tile || Math.Abs(tile.Bounds.Y - first.Bounds.Y) > 1)
                break;
            columns++;
        }
        return Math.Max(1, columns);
    }
}
