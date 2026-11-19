using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-28, регресс 1.17.19: «Отчёт сохранён: …» в «Финансах», «Продажах» и
/// «ABC-анализе» выводился той же красной плашкой, что и ошибки, — владелец принимал успешную
/// выгрузку за сбой. Плашка одна на оба случая; этот помощник красит её зелёным (готово) или
/// красным (ошибка) цветами текущей темы.</summary>
public static class NoticeBanner
{
    public static void Apply(Border? box, TextBlock? text, bool success)
    {
        // Окно могло ещё не построить плашку (сообщение задано до InitializeComponent).
        if (box is null || text is null)
            return;

        var owner = TopLevel.GetTopLevel(box);
        var theme = owner?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant;
        box.Background = Brush(success ? "BrushSuccessSoft" : "BrushDangerSoft", theme, success ? Brushes.Honeydew : Brushes.MistyRose);
        // 2026-11-13 (Н-6): красный текст на мягком красном фоне — читаемый ключ BrushDangerOnSoft (в тёмной теме BrushDanger давал 3,5:1).
        text.Foreground = Brush(success ? "BrushUiStatusOk" : "BrushDangerOnSoft", theme, success ? Brushes.DarkGreen : Brushes.DarkRed);
    }

    private static IBrush Brush(string key, Avalonia.Styling.ThemeVariant? theme, IBrush fallback) =>
        Application.Current?.TryFindResource(key, theme, out var value) == true && value is IBrush brush
            ? brush
            : fallback;
}
