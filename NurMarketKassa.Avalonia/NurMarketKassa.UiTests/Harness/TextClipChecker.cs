using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): проверка «текст не обрезан». Обходит визуальное дерево окна: для каждого видимого TextBlock без многоточия (TextTrimming=None)
/// сравнивает ширину текста (одна строка) или высоту (с переносом) с реальными границами TextBlock после раскладки. Если текст шире/выше —
/// родитель (кнопка, ячейка, панель) обрезал его, а кассир видит огрызок. Эвристика: ширину текста считает FormattedText того же шрифта,
/// поэтому допуск 3 px; значки на шрифте Segoe MDL2 (класс mdl2) и пустые тексты пропускаются.</summary>
public static class TextClipChecker
{
    private const double WidthTolerance = 3;
    private const double HeightTolerance = 4;

    /// <summary>Нарушение: ключ для списка «известных» (экран|язык|текст|вид) и подробность для отчёта.</summary>
    public sealed record Violation(string Key, string Detail);

    public static IReadOnlyList<Violation> Find(Window window, string screen, string language)
    {
        var result = new List<Violation>();
        foreach (var tb in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!tb.IsEffectivelyVisible || tb.Opacity <= 0.01 || tb.Classes.Contains("mdl2"))
                continue;
            if (tb.TextTrimming != TextTrimming.None)
                continue; // многоточие — честное сокращение, не обрезка
            var text = TextOf(tb);
            if (string.IsNullOrWhiteSpace(text) || tb.Bounds.Width <= 0 || tb.Bounds.Height <= 0)
                continue;
            if (tb.FontFamily?.Name is { } fam && fam.Contains("MDL2", StringComparison.OrdinalIgnoreCase))
                continue;

            var typeface = new Typeface(tb.FontFamily ?? FontFamily.Default, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var wraps = tb.TextWrapping != TextWrapping.NoWrap;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, tb.FlowDirection, typeface, tb.FontSize, null)
            {
                MaxTextWidth = wraps ? Math.Max(1, tb.Bounds.Width) : double.PositiveInfinity,
            };
            var shown = text.Length > 60 ? text[..60] + "…" : text;
            if (!wraps)
            {
                // Ширина одной строки (самая длинная строка, если в тексте есть переводы строк) против границ TextBlock.
                var need = formatted.Width;
                if (need > tb.Bounds.Width + WidthTolerance)
                    result.Add(new($"{screen}|{language}|{shown.Replace('\n', ' ')}|ширина",
                        $"текст «{shown}» шире поля: нужно {need:0.#} px, есть {tb.Bounds.Width:0.#} px ({Path(tb)})"));
            }
            else
            {
                var need = formatted.Height;
                if (tb.MaxLines > 0 && formatted.Height > 0)
                    continue; // число строк задано намеренно
                if (need > tb.Bounds.Height + HeightTolerance)
                    result.Add(new($"{screen}|{language}|{shown.Replace('\n', ' ')}|высота",
                        $"текст «{shown}» выше поля: нужно {need:0.#} px, есть {tb.Bounds.Height:0.#} px ({Path(tb)})"));
            }
        }
        return result;
    }

    private static string TextOf(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text))
            return tb.Text;
        if (tb.Inlines is { Count: > 0 })
            return string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text));
        return "";
    }

    private static string Path(Visual v)
    {
        var parts = new List<string>();
        for (var cur = v; cur is not null && parts.Count < 4; cur = cur.GetVisualParent())
            parts.Add(cur is Control { Name: { Length: > 0 } n } ? $"{cur.GetType().Name}#{n}" : cur.GetType().Name);
        return string.Join(" < ", parts);
    }
}
