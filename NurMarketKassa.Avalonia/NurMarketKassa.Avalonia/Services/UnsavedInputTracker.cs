using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-20, доработка п. 9 аудита владельца («не терять ввод»): оболочка закрывает спрятанные разделы ради памяти —
/// раздел, где владелец что-то ввёл и не сохранил (Настройки, Калькуляция, Настройки сайта, Магазин в приложении…), закрывать
/// нельзя. Общий признак без правки каждого окна: ввод с клавиатуры в поле, переключатель, выбор в списке — «есть несохранённое»;
/// нажатие «Сохранить» / «Опубликовать» / «Применить» — снова «всё сохранено». Поля поиска и дат периода не считаются.
/// Раздел, который знает сам (IOwnerSection.HasUnsavedChanges не null, например «Редактор сайта» по своему черновику), — по нему.</summary>
public static class UnsavedInputTracker
{
    private sealed class State
    {
        public bool Dirty;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    private static readonly string[] SaveWords =
    {
        "сохран", "сакта", "save", "kaydet", "saqla",
        "опубликов", "жарыяла", "publish", "yayınla", "e'lon",
        "примен", "колдон", "apply", "uygula", "qo'lla",
    };

    private static readonly string[] SearchWords = { "поиск", "найти", "издөө", "search", "find", "ara", "qidir" };

    public static bool IsDirty(Window window) => States.TryGetValue(window, out var s) && s.Dirty;

    public static void Attach(Window window)
    {
        if (States.TryGetValue(window, out _))
            return;
        var state = States.GetOrCreateValue(window);
        window.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (EditableText(e.Source))
                state.Dirty = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Back or Key.Delete && EditableText(e.Source))
                state.Dirty = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(Button.ClickEvent, (_, e) =>
        {
            switch (e.Source)
            {
                case RadioButton { GroupName: "QuickDate" }:
                    break; // период отчёта — не ввод
                case ToggleButton:
                    state.Dirty = true; // флажок, переключатель, выбор варианта
                    break;
                case Button b when IsSaveButton(b):
                    state.Dirty = false;
                    break;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
        {
            // Только выбор пользователя (список открыт или в фокусе), а не заполнение при загрузке.
            if (e.Source is ComboBox cb && (cb.IsDropDownOpen || cb.IsKeyboardFocusWithin))
                state.Dirty = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool EditableText(object? source)
    {
        var box = source as TextBox ?? (source as Avalonia.Visual)?.FindAncestorOfType<TextBox>();
        if (box is null || box.IsReadOnly)
            return false;
        if (box.FindAncestorOfType<CalendarDatePicker>() is not null || box.FindAncestorOfType<DatePicker>() is not null)
            return false;
        var hint = ((box.Watermark ?? "") + " " + (box.Name ?? "") + " " + (Avalonia.Automation.AutomationProperties.GetName(box) ?? "")).ToLowerInvariant();
        return !SearchWords.Any(hint.Contains) && !box.Classes.Contains("search");
    }

    private static bool IsSaveButton(Button b)
    {
        var text = b.Content switch
        {
            string s => s,
            TextBlock t => t.Text,
            Control c => c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text,
            _ => null,
        };
        text = (text ?? Avalonia.Automation.AutomationProperties.GetName(b) ?? "").ToLowerInvariant();
        return text.Length > 0 && SaveWords.Any(text.Contains);
    }
}
