using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): управление окном как кассир — настоящие нажатия мыши и ввод текста через Avalonia.Headless (никаких вызовов
/// обработчиков в обход окна). Поиск элементов — по x:Name или по тексту/имени для диктора.</summary>
public static class UiDrive
{
    public static T Find<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"Элемент {typeof(T).Name}#{name} не найден в окне {w.GetType().Name}");

    /// <summary>Кнопка по тексту (любой из переводов) — как её видит кассир.</summary>
    public static Button ButtonByText(Window w, params string[] anyOf) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && TextOf(b) is { } t && anyOf.Any(a => t.Contains(a, StringComparison.OrdinalIgnoreCase)))
        ?? throw new InvalidOperationException($"Кнопка «{string.Join("/", anyOf)}» не найдена в окне {w.GetType().Name}; есть: "
            + string.Join(", ", w.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(TextOf).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => $"«{t}»")));

    public static string? TextOf(Control c) =>
        Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } auto ? auto
        : c is ContentControl { Content: string s } ? s
        : c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>Все видимые тексты окна одной строкой (для проверки «на экране есть такие-то цифры»).</summary>
    public static string AllText(Window w) =>
        string.Join("\n", w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)
            .Concat(w.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text))
            .Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Нажатие мыши по центру элемента (вниз и вверх, как реальный клик).</summary>
    public static void Click(Window w, Control c)
    {
        UiShot.Pump();
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)
            ?? throw new InvalidOperationException("Элемент вне окна");
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        UiShot.Pump();
    }

    /// <summary>Фокус в поле, выделить всё и напечатать текст с клавиатуры.</summary>
    public static void Type(Window w, TextBox box, string text)
    {
        box.Focus();
        UiShot.Pump();
        box.SelectAll();
        w.KeyTextInput(text);
        UiShot.Pump();
    }
}
