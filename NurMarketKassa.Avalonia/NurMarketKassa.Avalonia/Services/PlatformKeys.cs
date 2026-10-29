using Avalonia.Input;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-29, macOS: «главная» клавиша сочетаний там Cmd (KeyModifiers.Meta), а не Ctrl. Сочетания кассы
/// записаны как «Ctrl+…» — на Mac Cmd считается за Ctrl (Ctrl тоже работает), в остальных системах ничего не меняется.</summary>
public static class PlatformKeys
{
    /// <summary>Есть ли среди нажатых модификаторов «главный» (Ctrl; на Mac — ещё и Cmd).</summary>
    public static bool HasCommand(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control) || (OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Meta));

    /// <summary>Нажат только «главный» модификатор (без Shift/Alt) — вместо `mods == KeyModifiers.Control`.</summary>
    public static bool IsCommandOnly(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.Control || (OperatingSystem.IsMacOS() && modifiers == KeyModifiers.Meta);

    /// <summary>Cmd → Ctrl для сопоставления сохранённых сочетаний (только macOS).</summary>
    public static KeyModifiers NormalizeCommand(KeyModifiers modifiers)
    {
        if (OperatingSystem.IsMacOS() && modifiers.HasFlag(KeyModifiers.Meta))
            return (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control;
        return modifiers;
    }

    /// <summary>Название главной клавиши для подписей: «Ctrl» или «Cmd».</summary>
    public static string CommandName => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
}
