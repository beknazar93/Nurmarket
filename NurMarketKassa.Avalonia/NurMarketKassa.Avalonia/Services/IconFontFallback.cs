using Avalonia;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, владелец: «иконки у тебя исчезли» (Android-касса на телефоне — вместо значков квадраты).
/// Значки кассы — шрифт Windows «Segoe MDL2 Assets»: на Android и Linux его нет, а класть его в программу
/// нельзя (лицензия Microsoft). Поэтому там, где его нет, подставляется свой шрифт NurIcons
/// (Assets/Fonts/NurIcons.ttf: 53 значка Fluent UI System Icons, MIT, под теми же кодами MDL2 — см.
/// NurIcons-LICENSE.txt). Подставляется только для символов, которых нет в шрифте, — в Windows ничего не меняется.
/// Новый значок MDL2 в коде — добавить его и в NurIcons (scratchpad build_nuricons.py, таблица MAP).</summary>
public static class IconFontFallback
{
    public const string FamilyUri = "avares://NurMarketKassa.Avalonia/Assets/Fonts#NurIcons";

    /// <summary>Для AppBuilder.With(...) на Android и Linux.</summary>
    public static FontManagerOptions Options => new()
    {
        FontFallbacks = new[]
        {
            new FontFallback { FontFamily = new FontFamily(FamilyUri) },
        },
    };
}
