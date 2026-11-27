using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-26 (владелец: «0 не убирается автоматически при создании товара в количестве!»): один общий механизм для числовых полей.
/// 1) Поле, в котором стоит ровно «0» / «0,00» / «0.0» (значение по умолчанию), при получении фокуса (клик, Tab, программно) выделяется целиком —
///    первая введённая цифра заменяет ноль («5», а не «05»), «0,5» набирается как обычно. Правило автоматическое для ВСЕХ полей приложения.
/// 2) Класс «numField» у поля — выделять всё содержимое при фокусе всегда (суммы, цены, количества: набор заменяет число).
/// 3) Пустое поле при уходе фокуса снова «0», если при входе в него стоял ноль (а также всегда — при классе «numZero»): не пустота и не ошибка.
/// Нажатие мышью ставит курсор уже ПОСЛЕ получения фокуса и снимает выделение — поэтому выделение повторяется после обработки нажатия.
/// Своя экранная клавиатура не затрагивается (osk.exe), Ctrl+Z работает как у обычного TextBox.</summary>
public static class NumericFocus
{
    public const string FieldClass = "numField";
    public const string ZeroClass = "numZero";

    private static readonly Regex ZeroText = new(@"^\s*0+([.,]0*)?\s*$", RegexOptions.Compiled);
    private static bool _registered;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, object> EnteredWithZero = new();

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        InputElement.GotFocusEvent.AddClassHandler<TextBox>(OnGotFocus, RoutingStrategies.Direct | RoutingStrategies.Bubble);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>(OnLostFocus, RoutingStrategies.Direct | RoutingStrategies.Bubble);
    }

    /// <summary>Значение «ноль по умолчанию»: «0», «0,00», «0.0», «00» (но не «0,5» и не пусто).</summary>
    public static bool IsZeroText(string? text) => !string.IsNullOrEmpty(text) && ZeroText.IsMatch(text);

    /// <summary>Числовое поле: класс numField/numZero, класс «num» (поля цифр, собранные в коде) или подсказка-цифра («0», «0,00»).</summary>
    private static bool IsNumericField(TextBox box) =>
        box.Classes.Contains(FieldClass) || box.Classes.Contains(ZeroClass) || box.Classes.Contains("num")
        || IsZeroText(box.Watermark);

    private static bool Applies(TextBox box) =>
        !box.IsReadOnly && box.PasswordChar == default && !box.AcceptsReturn;

    private static void OnGotFocus(TextBox box, GotFocusEventArgs e)
    {
        if (!ReferenceEquals(e.Source, box) || !Applies(box))
            return;
        if (!IsNumericField(box) && !IsZeroText(box.Text))
            return;
        if (string.IsNullOrEmpty(box.Text))
            return;
        if (IsZeroText(box.Text))
            EnteredWithZero.AddOrUpdate(box, box);
        box.SelectAll();
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsFocused && !string.IsNullOrEmpty(box.Text))
                box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private static void OnLostFocus(TextBox box, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, box) || !Applies(box))
            return;
        var wasZero = EnteredWithZero.Remove(box);
        if ((wasZero || box.Classes.Contains(ZeroClass)) && string.IsNullOrWhiteSpace(box.Text))
            box.Text = "0";
    }
}
