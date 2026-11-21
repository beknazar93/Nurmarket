using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-21, жалоба владельца («Продажи и финансы»: «снизу все чеки открываются сразу при одном нажатии»): единое правило для списков
/// чеков программы владельца («Чеки», «Возвраты», «История»). Одно нажатие по строке только выделяет её (и не открывает чек — случайное касание
/// при прокрутке пальцем больше не открывает карточку). Чек открывается ЯВНЫМ действием: двойным щелчком/двойным касанием по строке
/// или кнопкой-стрелкой «Открыть чек» (44 px) в последней колонке. Состояние «открыт» у каждой строки своё (карточка одна и грузит ровно тот чек,
/// по строке которого нажали, а не «выбранный» — выбранный мог быть другим).</summary>
internal static class ReceiptRowOpen
{
    /// <summary>Название кнопки-стрелки (и подсказка) — на 5 языках.</summary>
    public static string OpenText => Tr.T("Открыть чек", "Чекти ачуу", "Open receipt", "Fişi aç", "Chekni ochish");

    /// <summary>Строка таблицы под нажатием: её элемент данных. null — заголовок, пустое место таблицы или кнопка внутри строки («Возврат», «Правка»):
    /// такое нажатие чек не открывает.</summary>
    public static object? RowItem(RoutedEventArgs e)
    {
        var source = e.Source as Visual;
        if (source is null || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return null;
        return source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext;
    }

    /// <summary>Элемент данных строки, в которой лежит кнопка «Открыть чек».</summary>
    public static object? ItemOfButton(object? sender) => (sender as Control)?.DataContext;

    /// <summary>Колонка с кнопкой-стрелкой «Открыть чек» (≥ 40 px по высоте и ширине, как все сенсорные кнопки). Нажатие обрабатывает
    /// <paramref name="open"/> с элементом строки; событие дальше (на таблицу) не идёт.</summary>
    public static DataGridTemplateColumn CreateColumn(Action<object> open)
    {
        return new DataGridTemplateColumn
        {
            Header = string.Empty,
            Width = new DataGridLength(56),
            MinWidth = 56,
            CanUserResize = false,
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var button = new Button
                {
                    Name = "OpenReceiptButton",
                    Content = "›",
                    FontSize = 24,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Width = 44,
                    Height = 40,
                    MinWidth = 44,
                    MinHeight = 40,
                    Padding = new Thickness(0, 0, 0, 3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                button.Classes.Add("sec-outline");
                ToolTip.SetTip(button, OpenText);
                Avalonia.Automation.AutomationProperties.SetName(button, OpenText);
                button.Click += (s, e) =>
                {
                    e.Handled = true;
                    if (ItemOfButton(s) is { } item)
                        open(item);
                };
                return button;
            }, supportsRecycling: false),
        };
    }
}
