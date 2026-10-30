using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этап О9б (редизайн «Возвратов поставщикам» и «Филиалов»): общие кусочки оформления окон, собранных в коде, —
/// по правилам О3–О9 (одна жёлтая кнопка, остальное обводкой, пустые состояния с подсказкой, ModalShell для диалогов).
/// Только вид: данные, запросы к серверу и обработчики остаются в самих окнах.</summary>
internal static class SectionCards
{
    /// <summary>Окно-диалог в оболочке ModalShell (как NewClientDialog): без рамки ОС, фон и тень темы, высота по содержимому.</summary>
    public static void AsModal(Window w, double width)
    {
        w.Width = width;
        w.SizeToContent = SizeToContent.Height;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.SystemDecorations = SystemDecorations.None;
        w.CanResize = false;
        w.Bind(TopLevel.BackgroundProperty, w.GetResourceObservable("DialogWindowBackground"));
        w.Bind(TopLevel.TransparencyLevelHintProperty, w.GetResourceObservable("DialogTransparencyHint"));
    }

    public static ModalShell Shell(string title, string? subtitle, Control content, Control footer, object? hint = null) =>
        new() { Margin = new Thickness(16), Title = title, Subtitle = subtitle, Content = content, Footer = footer, Hint = hint };

    /// <summary>«Отмена» / «Нет» внизу модалки: обводка, Esc и ✕ нажимают её.</summary>
    public static Button Cancel(string text)
    {
        var b = new Button { Content = text, IsCancel = true };
        b.Classes.Add("modal-cancel");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Главная кнопка модалки (жёлтая у «NurMarket»). 2026-10-22 (модалки, пакет 0): <paramref name="isDefault"/> —
    /// Enter нажимает её (по умолчанию нет, как раньше). Одна такая кнопка на окно.</summary>
    public static Button Primary(string text, bool isDefault = false)
    {
        var b = new Button { Content = text, IsDefault = isDefault };
        b.Classes.Add("modal-primary");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): второстепенное действие подвала («Назад», «Сбросить», «Печать») —
    /// обводка, как «Отмена», но без Esc. Жёлтой остаётся только одна главная.</summary>
    public static Button Secondary(string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("modal-secondary");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): опасная главная кнопка («Удалить», «Закрыть смену», «Отменить перемещение») —
    /// красная вместо жёлтой; рядом всегда «Отмена». <paramref name="isDefault"/> — Enter нажимает её (по умолчанию нет:
    /// опасное не должно срабатывать от случайного Enter/сканера).</summary>
    public static Button Danger(string text, bool isDefault = false)
    {
        var b = new Button { Content = text, IsDefault = isDefault };
        b.Classes.Add("modal-primary");
        b.Classes.Add("danger");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): третья кнопка — без рамки, цветом текста («Подробнее», «Сбросить» в Hint).</summary>
    public static Button Ghost(string text)
    {
        var b = new Button { Content = text };
        b.Classes.Add("modal-ghost");
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): текст ошибки ПОД полем (стандарт: ошибки — у поля, не всплывающим окном):
    /// 13 px, цвет ошибки темы, переносится; скрыт, пока пустой. Окно пишет в него <see cref="ShowError"/>.</summary>
    public static TextBlock ErrorText(Control host)
    {
        var t = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushDangerOnSoft"));
        return t;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): показать (текст) или спрятать (null/пусто) ошибку под полем; с полем —
    /// фокус в него и выделение значения.</summary>
    public static void ShowError(TextBlock error, string? text, Control? field = null)
    {
        error.Text = text ?? "";
        error.IsVisible = !string.IsNullOrWhiteSpace(text);
        if (error.IsVisible && field is not null)
        {
            field.Focus();
            if (field is TextBox box)
                box.SelectAll();
        }
    }

    /// <summary>2026-10-22 (модалки, пакет 0): поле ввода модалки — касание ≥ 44 px (класс modal-field).</summary>
    public static T Field<T>(T field) where T : Control
    {
        field.Classes.Add("modal-field");
        return field;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
            row.Children.Add(b);
        return row;
    }

    /// <summary>Ошибка в мягкой красной плашке; видна, когда видим сам текст (окна по-прежнему переключают его IsVisible).</summary>
    public static Border ErrorBox(Control host, TextBlock error)
    {
        error.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushDangerOnSoft"));
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = error };
        box.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushDangerSoft"));
        box.Bind(Visual.IsVisibleProperty, error.GetObservable(Visual.IsVisibleProperty));
        return box;
    }

    /// <summary>Пустой список: подсказка по центру — что здесь будет и что нажать.</summary>
    public static TextBlock Empty(Control host, string text)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 48, 24, 0),
        };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    /// <summary>«Показать товары ▾» в карточке — цветом текста (жёлтый текст на светлом не читается).</summary>
    public static TextBlock Toggle(Control host, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushText"));
        return t;
    }

    /// <summary>Плашка состояния/причины: мягкий фон и текст темы.</summary>
    public static Border Pill(Control host, string text, string background = "BrushPanelSoft", string foreground = "BrushTextSoft")
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(foreground));
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3), Child = label, VerticalAlignment = VerticalAlignment.Center };
        pill.Bind(Border.BackgroundProperty, host.GetResourceObservable(background));
        return pill;
    }

    /// <summary>Состав документа таблицей: товар | количество | цена | сумма (числа справа, ровными столбцами).</summary>
    public static Border LinesTable(Control host, IReadOnlyList<(string Name, string Qty, string Price, string Sum)> rows, string nameHeader, string qtyHeader, string priceHeader, string sumHeader,
        IReadOnlySet<int>? highlight = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        var r = 0;
        void Add(string text, int col, bool header, bool right, bool mark = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = header ? 12 : 13, TextWrapping = col == 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontWeight = mark ? FontWeight.Bold : FontWeight.Normal,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(col == 0 ? 0 : 16, header ? 0 : 5, 0, header ? 4 : 5),
            };
            if (!header && col > 0)
                t.Classes.Add("num");
            t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable(header ? "BrushTextSoft" : "BrushText"));
            Grid.SetRow(t, r);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(nameHeader, 0, true, false);
        Add(qtyHeader, 1, true, true);
        Add(priceHeader, 2, true, true);
        Add(sumHeader, 3, true, true);
        var index = 0;
        foreach (var row in rows)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            // 2026-10-30: строка выделенного товара («Поступления» в карточке товара) — мягкая заливка акцента темы и жирный шрифт.
            var mark = highlight?.Contains(index) == true;
            if (mark)
            {
                var back = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(-6, 0) };
                back.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushAccentSoft"));
                Grid.SetRow(back, r);
                Grid.SetColumnSpan(back, 4);
                grid.Children.Add(back);
            }
            Add(row.Name, 0, false, false, mark);
            Add(row.Qty, 1, false, true, mark);
            Add(row.Price, 2, false, true, mark);
            Add(row.Sum, 3, false, true, mark);
            index++;
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Margin = new Thickness(0, 4, 0, 0), Child = grid };
        box.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushPanelSoft"));
        return box;
    }

    /// <summary>Рамка-группа внутри диалога (строки товаров, варианты).</summary>
    public static Border Group(Control host, Control child)
    {
        var box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10), Child = child };
        box.Bind(Border.BorderBrushProperty, host.GetResourceObservable("BrushBorder"));
        box.Bind(Border.BackgroundProperty, host.GetResourceObservable("BrushPanel"));
        return box;
    }
}
