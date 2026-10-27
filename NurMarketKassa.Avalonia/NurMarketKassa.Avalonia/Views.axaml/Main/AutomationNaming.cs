using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-27, аудит владельца С-12 (доступность): экранный диктор читал кнопки как «Avalonia.Controls.Shapes.Path», «Avalonia.Controls.Grid»,
/// «…StackPanel», а переключатели («FullscreenCheck» и др.) были без имени вообще. Имя берётся из самого элемента, если оно не задано вручную:
/// первая надпись внутри кнопки → её подсказка → ближайшая надпись слева/выше переключателя. Включается при загрузке каждого элемента
/// (Control.Loaded), поэтому охватывает и окна разделов, и настройки; заданные вручную имена не трогаются.
/// 2026-10-27 (приёмка 1.17.63, Д-10): включается и в кассе (App); имя кнопки с надписью следует за надписью (кнопка поставщика после выбора
/// читалась «Без поставщика»); кнопки полос прокрутки, ячейки таблиц и поля ввода без имени тоже получают имя.</summary>
public static class AutomationNaming
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler<ToggleButton>((control, _) => Fill(control));
        Control.LoadedEvent.AddClassHandler<Button>((control, _) => Fill(control));
        Control.LoadedEvent.AddClassHandler<TextBox>((control, _) => FillField(control));
        Control.LoadedEvent.AddClassHandler<DataGridCell>((control, _) => FillCell(control));
    }

    /// <summary>2026-10-27 (Д-10): поле ввода без имени (цена в карточке товара и др.) — подсказка в поле, иначе надпись над ним/слева.</summary>
    private static void FillField(TextBox box)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)) || AutomationProperties.GetAccessibilityView(box) == AccessibilityView.Raw)
                return;
            var name = !string.IsNullOrWhiteSpace(box.Watermark) ? box.Watermark
                : ToolTip.GetTip(box) as string is { Length: > 0 } tip ? tip
                : LabelBefore(box);
            if (!string.IsNullOrWhiteSpace(name))
                AutomationProperties.SetName(box, name);
        }
        catch
        {
            // имя для диктора — необязательная мелочь
        }
    }

    /// <summary>2026-10-27 (Д-10): ячейка таблицы читалась «Avalonia.Controls.TextBlock» — имя следует за текстом ячейки (строки таблицы переиспользуются).</summary>
    private static void FillCell(DataGridCell cell)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(cell)))
                return;
            var text = cell.Content as TextBlock ?? cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            if (text is not null)
                cell.Bind(AutomationProperties.NameProperty, text.GetObservable(TextBlock.TextProperty));
        }
        catch
        {
            // необязательная мелочь
        }
    }

    /// <summary>2026-10-27 (Д-10): кнопки полос прокрутки читались «Avalonia.Controls.PathIcon».</summary>
    private static string? ScrollButtonName(Control control) => control.Name switch
    {
        "PART_LineUpButton" => Tr.T("Прокрутить вверх", "Жогору жылдыруу", "Scroll up", "Yukarı kaydır", "Yuqoriga aylantirish"),
        "PART_LineDownButton" => Tr.T("Прокрутить вниз", "Төмөн жылдыруу", "Scroll down", "Aşağı kaydır", "Pastga aylantirish"),
        "PART_LineLeftButton" => Tr.T("Прокрутить влево", "Солго жылдыруу", "Scroll left", "Sola kaydır", "Chapga aylantirish"),
        "PART_LineRightButton" => Tr.T("Прокрутить вправо", "Оңго жылдыруу", "Scroll right", "Sağa kaydır", "O'ngga aylantirish"),
        "PART_PageUpButton" => Tr.T("Страница вверх", "Бир барак жогору", "Page up", "Sayfa yukarı", "Sahifa tepaga"),
        "PART_PageDownButton" => Tr.T("Страница вниз", "Бир барак төмөн", "Page down", "Sayfa aşağı", "Sahifa pastga"),
        "PART_PageLeftButton" => Tr.T("Страница влево", "Бир барак солго", "Page left", "Sayfa sola", "Sahifa chapga"),
        "PART_PageRightButton" => Tr.T("Страница вправо", "Бир барак оңго", "Page right", "Sayfa sağa", "Sahifa o'ngga"),
        _ => null,
    };

    private static void Fill(Control control)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) || AutomationProperties.GetAccessibilityView(control) == AccessibilityView.Raw)
                return;
            if (ScrollButtonName(control) is { } scrollName)
            {
                AutomationProperties.SetName(control, scrollName);
                return;
            }

            // 2026-10-27 (Д-10): имя кнопки с надписью следует за надписью, а не запоминается один раз — кнопка выбора поставщика
            // после выбора читалась старым «Без поставщика».
            if (control is Button { Content: not string } && control is not ToggleButton && FirstTextBlock(control) is { } label)
            {
                control.Bind(AutomationProperties.NameProperty, label.GetObservable(TextBlock.TextProperty));
                return;
            }

            var name = NameFor(control);
            if (!string.IsNullOrWhiteSpace(name))
                AutomationProperties.SetName(control, name);
        }
        catch
        {
            // имя для диктора — необязательная мелочь, не должна мешать загрузке окна
        }
    }

    private static string? NameFor(Control control)
    {
        // Кнопка с обычной строкой — имя возьмёт сама Avalonia.
        if (control is ContentControl { Content: string })
            return null;
        // 1. Подсказка кнопки-значка (ToolTip.Tip) — для кнопок вида «рисунок без текста».
        var tip = ToolTip.GetTip(control) as string;
        // 2. Первая надпись внутри кнопки / переключателя.
        var text = FirstText(control);
        if (!string.IsNullOrWhiteSpace(text))
            return text;
        if (!string.IsNullOrWhiteSpace(tip))
            return tip;
        // 3. Переключатель без своей надписи: ближайшая надпись перед ним в той же строке или выше.
        if (control is ToggleSwitch or CheckBox)
            return LabelBefore(control);
        return null;
    }

    private static TextBlock? FirstTextBlock(Control control)
    {
        foreach (var d in control.GetVisualDescendants())
            if (d is TextBlock { Text: { } t } tb && !string.IsNullOrWhiteSpace(t))
                return tb;
        return null;
    }

    private static string? FirstText(Control control)
    {
        foreach (var d in control.GetVisualDescendants())
            if (d is TextBlock { Text: { } t } && !string.IsNullOrWhiteSpace(t))
                return t.Trim();
        return null;
    }

    private static string? LabelBefore(Control control)
    {
        Visual? node = control;
        for (var hops = 0; hops < 3 && node?.GetVisualParent() is { } parent; hops++)
        {
            var before = true;
            TextBlock? last = null;
            foreach (var sibling in parent.GetVisualChildren())
            {
                if (ReferenceEquals(sibling, node))
                {
                    before = false;
                    break;
                }

                if (sibling is TextBlock { Text: { } t } tb && !string.IsNullOrWhiteSpace(t))
                    last = tb;
                else if (sibling is Control c && FirstText(c) is { } inner)
                    last = new TextBlock { Text = inner };
            }

            if (!before && last is not null)
                return last.Text!.Trim();
            node = parent;
        }

        return null;
    }
}
