using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27 (аудит редакторов, В-8): барьер перед применением плохо читаемой своей темы. Раньше «Применить к кассе» (редактор тем) и
/// «Применить тему» (Маркетплейс) включали тему с нечитаемыми цветами без единого слова. Теперь касса показывает, какие пары цветов ниже нормы
/// (WCAG AA, 4,5:1), и применяет тему только по явному «Всё равно применить». Расчёт — AccentThemeService.CheckContrast.</summary>
public static class ThemeReadability
{
    /// <summary>Строки для плашки и вопроса: вариант, пара цветов, достигнутый и нужный контраст (не больше <paramref name="maxLines"/>, остальное — «и ещё N»).</summary>
    public static string Describe(IReadOnlyList<AccentThemeService.ContrastIssue> issues, int maxLines = 6)
    {
        var lines = issues.Take(maxLines).Select(i =>
        {
            var variant = i.Dark
                ? Tr.T("тёмный", "караңгы", "dark", "koyu", "qorong'i")
                : Tr.T("светлый", "жарык", "light", "açık", "yorug'");
            return $"• {i.What} ({variant}): {i.Ratio:0.0}:1, " + Tr.T($"нужно от {i.Min:0.#}:1", $"{i.Min:0.#}:1 же андан жогору керек", $"needs at least {i.Min:0.#}:1", $"en az {i.Min:0.#}:1 gerekir", $"kamida {i.Min:0.#}:1 kerak");
        }).ToList();
        if (issues.Count > maxLines)
            lines.Add(Tr.T($"…и ещё {issues.Count - maxLines}", $"…дагы {issues.Count - maxLines}", $"…and {issues.Count - maxLines} more", $"…ve {issues.Count - maxLines} tane daha", $"…yana {issues.Count - maxLines} ta"));
        return string.Join("\n", lines);
    }

    /// <summary>true — тема читается нормально или владелец подтвердил «Всё равно применить».</summary>
    public static bool ConfirmApply(Window? owner, CustomThemeDefinition def)
    {
        var issues = AccentThemeService.CheckContrastBoth(def);
        if (issues.Count == 0)
            return true;
        return PosConfirmDialog.Show(owner,
            Tr.T("Тема плохо читается", "Тема начар окулат", "The theme is hard to read", "Tema zor okunuyor", "Mavzu yomon o'qiladi"),
            Tr.T($"В теме «{def.Name}» некоторые надписи плохо видны:\n{Describe(issues)}\n\nКассир может не разобрать текст. Применить всё равно?",
                $"«{def.Name}» темасында кээ бир жазуулар начар көрүнөт:\n{Describe(issues)}\n\nКассир текстти окуй албай калышы мүмкүн. Баары бир колдоносузбу?",
                $"Some captions in the theme “{def.Name}” are hard to see:\n{Describe(issues)}\n\nThe cashier may not be able to read the text. Apply anyway?",
                $"«{def.Name}» temasında bazı yazılar zor görünüyor:\n{Describe(issues)}\n\nKasiyer metni okuyamayabilir. Yine de uygulansın mı?",
                $"«{def.Name}» mavzusida ba'zi yozuvlar yomon ko'rinadi:\n{Describe(issues)}\n\nKassir matnni o'qiy olmasligi mumkin. Baribir qo'llansinmi?"),
            Tr.T("Всё равно применить", "Баары бир колдонуу", "Apply anyway", "Yine de uygula", "Baribir qo'llash"),
            Tr.T("Вернуться к правке", "Оңдоого кайтуу", "Back to editing", "Düzenlemeye dön", "Tahrirga qaytish"),
            PosConfirmAccent.Danger);
    }
}
