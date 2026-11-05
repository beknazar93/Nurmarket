using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
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
    /// <summary>2026-11-05 (аудит №4, Л4-11): раньше «грязно» было одним флагом, который снимала только кнопка со словом «сохранить» —
    /// Советник после отправки вопроса, «Принять оплату», «Провести» оставались «несохранёнными», и спрятанные разделы (~37 МБ каждый) не закрывались.
    /// Теперь помним, какие поля владелец менял и что в них было ДО правки; «несохранённое» — только поле, значение которого сейчас отличается
    /// от исходного (отправил вопрос и поле опустело — чисто). Кнопка «сохранить/провести/отправить…» очищает всё.</summary>
    private sealed class State
    {
        // 2026-11-05 (ревью волн 0–3, В6): ссылка СИЛЬНАЯ. Раньше была WeakReference: поле ввода ячейки таблицы создаётся на время правки и после неё
        // отцепляется, после сборки мусора ссылка пропадала, раздел считался «чистым» и закрывался по простою вместе с несохранённой правкой.
        // Утечки нет: состояние лежит в ConditionalWeakTable по окну раздела и уходит вместе с ним.
        public readonly List<(Control Control, object? Initial)> Edited = new();

        public void Remember(Control control, object? initial)
        {
            if (Edited.Any(e => ReferenceEquals(e.Control, control)))
                return;
            // Отцепленные от окна поля, значение которых вернулось к исходному, больше не нужны (не копим мусор).
            Edited.RemoveAll(e => !e.Control.IsAttachedToVisualTree() && !Changed(e.Control, e.Initial));
            if (Edited.Count >= 500)
                Edited.RemoveAt(0);
            Edited.Add((control, initial));
        }

        public bool IsDirty() => Edited.Any(e => Changed(e.Control, e.Initial));
    }

    private static object? Current(Control c) => c switch
    {
        TextBox t => t.Text ?? "",
        ComboBox cb => cb.SelectedItem,
        ToggleButton tb => tb.IsChecked ?? false,
        _ => null,
    };

    private static bool Changed(Control c, object? initial) => !Equals(Current(c), initial);

    private static readonly ConditionalWeakTable<Window, State> States = new();

    /// <summary>2026-11-05 (аудит №3, В4-02): признак «ввод внутри этого элемента не считается несохранённой правкой» (фильтры, вид, период — не форма).
    /// Ставится на контейнер фильтров: <c>UnsavedInputTracker.SetIgnore(panel, true)</c>; действует на всё внутри.</summary>
    public static readonly AttachedProperty<bool> IgnoreProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Ignore", typeof(UnsavedInputTracker));

    public static void SetIgnore(Control element, bool value) => element.SetValue(IgnoreProperty, value);

    public static bool GetIgnore(Control element) => element.GetValue(IgnoreProperty);

    private static bool IsIgnored(object? source)
    {
        for (var visual = source as Avalonia.Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control c && c.GetValue(IgnoreProperty))
                return true;
        return false;
    }

    private static readonly string[] SaveWords =
    {
        "сохран", "сакта", "save", "kaydet", "saqla",
        "опубликов", "жарыяла", "publish", "yayınla", "e'lon",
        "примен", "колдон", "apply", "uygula", "qo'lla",
        // 2026-11-05 (аудит №4, Л4-11): ещё завершающие действия — после них ввод ушёл на сервер, «несохранённого» нет.
        // 2026-11-05 (ревью волн 0–3, С1): «Создать резервную копию», «Отправить в WhatsApp», «Записать» стирали признак несохранённого у всех полей окна —
        // слова "созда/отправ/запис/send/create…" убраны. Завершающую кнопку с другим словом можно отметить явно: UnsavedInputTracker.SetCommits(button, true).
        "провест", "приня", "оприход", "submit",
    };

    /// <summary>Явная отметка: эта кнопка завершает ввод (после неё «несохранённого» нет), каким бы ни был её текст.</summary>
    public static readonly AttachedProperty<bool> CommitsProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Commits", typeof(UnsavedInputTracker));

    public static void SetCommits(Control element, bool value) => element.SetValue(CommitsProperty, value);

    public static bool GetCommits(Control element) => element.GetValue(CommitsProperty);

    private static readonly string[] SearchWords = { "поиск", "найти", "издөө", "search", "find", "ara", "qidir" };

    public static bool IsDirty(Window window) => States.TryGetValue(window, out var s) && s.IsDirty();

    public static void Attach(Window window)
    {
        if (States.TryGetValue(window, out _))
            return;
        var state = States.GetOrCreateValue(window);
        window.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (FindEditable(e.Source) is { } box && !IsIgnored(e.Source))
                state.Remember(box, box.Text ?? ""); // TextInput идёт до изменения текста — тут ещё исходное значение
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Back or Key.Delete && FindEditable(e.Source) is { } box && !IsIgnored(e.Source))
                state.Remember(box, box.Text ?? "");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(Button.ClickEvent, (_, e) =>
        {
            switch (e.Source)
            {
                case RadioButton { GroupName: "QuickDate" }:
                    break; // период отчёта — не ввод
                // 2026-11-05 (аудит №3, В4-02): «Весовые / Штучные», «Список / Плитки», период, «Документы / Поток» — это фильтр и вид, а не правка формы,
                // но при выходе владельцу задавали ложный вопрос «несохранённые правки: Склад…». Переключатели вариантов (RadioButton) и вкладки-сегменты
                // не считаем; флажок (CheckBox) и тумблер (ToggleSwitch) — по-прежнему правка формы.
                case RadioButton:
                    break;
                case ToggleButton tb when tb.Classes.Contains("sec-seg") || tb.Classes.Contains("seg") || tb.Classes.Contains("filter") || IsIgnored(tb):
                    break;
                case ToggleButton tb when tb is CheckBox || tb is ToggleSwitch:
                    state.Remember(tb, !(tb.IsChecked ?? false)); // флажок, переключатель: Click приходит уже после смены — исходное значение обратное
                    break;
                case ToggleButton:
                    break; // прочие кнопки-переключатели — фильтры и вид
                case Button b when IsSaveButton(b):
                    state.Edited.Clear();
                    break;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
        {
            // Только выбор пользователя (список открыт или в фокусе), а не заполнение при загрузке.
            if (e.Source is ComboBox cb && (cb.IsDropDownOpen || cb.IsKeyboardFocusWithin) && !IsIgnored(cb))
                state.Remember(cb, e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static TextBox? FindEditable(object? source)
    {
        var box = source as TextBox ?? (source as Avalonia.Visual)?.FindAncestorOfType<TextBox>();
        if (box is null || box.IsReadOnly)
            return null;
        if (box.FindAncestorOfType<CalendarDatePicker>() is not null || box.FindAncestorOfType<DatePicker>() is not null)
            return null;
        var hint = ((box.Watermark ?? "") + " " + (box.Name ?? "") + " " + (Avalonia.Automation.AutomationProperties.GetName(box) ?? "")).ToLowerInvariant();
        return !SearchWords.Any(hint.Contains) && !box.Classes.Contains("search") ? box : null;
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
        if (b.GetValue(CommitsProperty))
            return true;
        text = (text ?? Avalonia.Automation.AutomationProperties.GetName(b) ?? "").ToLowerInvariant();
        return text.Length > 0 && SaveWords.Any(text.Contains);
    }
}
