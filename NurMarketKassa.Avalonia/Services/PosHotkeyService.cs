using Avalonia.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

public enum PosHotkeyAction
{
    Checkout,
    ClearCart,
    ToggleCustomerDisplay,
    ApplyDiscount,
    FocusProductSearch,
}

public sealed record PosHotkeyDefinition(
    PosHotkeyAction Action,
    string Title,
    string Description,
    string DefaultGesture);

public sealed class PosHotkeyService
{
    public static readonly IReadOnlyList<PosHotkeyDefinition> Definitions =
    [
        new(PosHotkeyAction.Checkout, "Оплата / провести чек", "Открывает окно оплаты текущего чека.", "F11"),
        new(PosHotkeyAction.ClearCart, "Очистить корзину", "Удаляет текущий чек после стандартного подтверждения.", "Ctrl+F1"),
        new(PosHotkeyAction.ToggleCustomerDisplay, "Экран покупателя", "Открывает или скрывает экран покупателя.", "F12"),
        new(PosHotkeyAction.ApplyDiscount, "Скидка на чек", "Открывает окно применения скидки.", "F8"),
        new(PosHotkeyAction.FocusProductSearch, "Быстрый поиск товара", "Переводит фокус в строку поиска каталога.", "F2"),
    ];

    public string GetGesture(PosHotkeyAction action)
    {
        var key = action.ToString();
        if (UserPreferences.Instance.PosHotkeys.TryGetValue(key, out var configured) &&
            TryParse(configured, out _, out _))
            return Normalize(configured);

        return Definitions.First(x => x.Action == action).DefaultGesture;
    }

    public bool TryMatch(KeyEventArgs e, out PosHotkeyAction action)
    {
        foreach (var definition in Definitions)
        {
            if (!TryParse(GetGesture(definition.Action), out var key, out var modifiers))
                continue;
            if (e.Key == key && NormalizeModifiers(e.KeyModifiers) == modifiers)
            {
                action = definition.Action;
                return true;
            }
        }

        action = default;
        return false;
    }

    public IReadOnlyDictionary<PosHotkeyAction, string> GetAll() =>
        Definitions.ToDictionary(x => x.Action, x => GetGesture(x.Action));

    public bool Save(IReadOnlyDictionary<PosHotkeyAction, string> gestures, out string? error)
    {
        var normalized = new Dictionary<PosHotkeyAction, string>();
        foreach (var definition in Definitions)
        {
            var raw = gestures.TryGetValue(definition.Action, out var value)
                ? value
                : definition.DefaultGesture;
            if (!TryParse(raw, out _, out _))
            {
                error = $"Некорректная комбинация для «{definition.Title}».";
                return false;
            }

            normalized[definition.Action] = Normalize(raw);
        }

        var duplicate = normalized
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            error = $"Комбинация {duplicate.Key} назначена нескольким действиям.";
            return false;
        }

        UserPreferences.Instance.PosHotkeys = normalized.ToDictionary(
            x => x.Key.ToString(),
            x => x.Value,
            StringComparer.OrdinalIgnoreCase);
        UserPreferences.Instance.SaveToDisk();
        error = null;
        return true;
    }

    public void ResetToDefaults()
    {
        UserPreferences.Instance.PosHotkeys = Definitions.ToDictionary(
            x => x.Action.ToString(),
            x => x.DefaultGesture,
            StringComparer.OrdinalIgnoreCase);
        UserPreferences.Instance.SaveToDisk();
    }

    public static string Format(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>();
        modifiers = NormalizeModifiers(modifiers);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string Normalize(string gesture) =>
        TryParse(gesture, out var key, out var modifiers)
            ? Format(key, modifiers)
            : gesture.Trim();

    private static bool TryParse(string? gesture, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        foreach (var rawPart in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Meta", StringComparison.OrdinalIgnoreCase))
                modifiers |= KeyModifiers.Meta;
            else if (!Enum.TryParse(part, true, out key) || key == Key.None || IsModifierKey(key))
                return false;
        }

        return key != Key.None;
    }

    private static KeyModifiers NormalizeModifiers(KeyModifiers modifiers) =>
        modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
}
