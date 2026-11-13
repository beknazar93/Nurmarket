using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NurMarketKassa.Services;
using System;
using System.Linq;

// 2026-11-12 (аудит Fable 12.11, В-6, С-16): раскладка окна возврата на малых экранах (1024×600). Раньше открытые «Фильтры» занимали 4 ряда, и от списка чеков оставалась
// шапка (~40 px) — чек выбрать было нельзя; подсказка в 2 строки, три ряда кнопок и «Закрыть» отдельным рядом съедали высоту, справа виднелась одна строка товара.
// Теперь: подсказка — за значком «ⓘ»; «Все / Снять выбор» — в строке заголовка шага 2; «Закрыть» — в одном ряду с «Ручной ввод»; панель фильтров — в прокрутке, высота
// которой подгоняется так, чтобы у списка чеков оставалось место минимум на 3 чека.
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private ScrollViewer? _filterScroll;

    /// <summary>Высота одного ряда панели фильтров: поле 40 + отступ 8 (SaleFilterBar).</summary>
    internal const double FilterRowHeight = 48;

    /// <summary>Сколько чеков списка обязаны быть видны при открытых фильтрах (приёмка аудита: не меньше 3).</summary>
    internal const int MinVisibleReceipts = 3;

    private void ToggleInstructions_Click(object? sender, RoutedEventArgs e) => InstructionsText.IsVisible = !InstructionsText.IsVisible;

    /// <summary>Имя значка подсказки для экранного диктора (текст подсказки — в ToolTip и в InstructionsText из ресурса строк).</summary>
    private void NameLayoutControls()
    {
        Avalonia.Automation.AutomationProperties.SetName(InstructionsToggle,
            Tr.T("Подсказка", "Жардам", "Hint", "İpucu", "Yordam"));
    }

    /// <summary>Подгоняет высоту панели фильтров: остаётся не меньше места на <see cref="MinVisibleReceipts"/> чека (шапка таблицы + строки), панель целыми рядами,
    /// остальное — прокруткой внутри панели. Вызывается при изменении размера окна, при открытии «Фильтров» и при появлении счётчика «Найдено».</summary>
    internal void FitFilterHeight()
    {
        if (_filterScroll is null || MainAreaGrid.Children.Count == 0 || MainAreaGrid.Children[0] is not DockPanel block || block.Bounds.Height < 1)
            return;
        var used = 0d;
        for (var i = 0; i < block.Children.Count - 1; i++) // последний ребёнок — таблица чеков
        {
            if (block.Children[i] is Control c && c.IsVisible && !ReferenceEquals(c, _filterHost))
                used += c.Bounds.Height + c.Margin.Top + c.Margin.Bottom;
        }
        var firstRow = SalesDataGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault();
        double rowHeight = firstRow is { Bounds.Height: > 20 } ? firstRow.Bounds.Height : 56;
        var tableMin = 36 + MinVisibleReceipts * rowHeight + 4; // шапка колонок + строки + рамка
        var avail = block.Bounds.Height - used - tableMin - 2;
        var rows = Math.Max(1, (int)(avail / FilterRowHeight)); // целое вниз (при avail < 1 ряда — один ряд)
        var max = rows * FilterRowHeight;
        if (Math.Abs(_filterScroll.MaxHeight - max) > 0.5)
            _filterScroll.MaxHeight = max;
    }
}
