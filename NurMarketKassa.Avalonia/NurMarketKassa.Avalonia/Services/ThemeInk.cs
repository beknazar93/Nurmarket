using Avalonia;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-01 (аудит «10 из 10», Д-05): цвет второстепенного текста из темы для кода без доступа к элементу
/// (подписи графиков). Раньше везде стоял <c>Brushes.Gray</c> — 3,9–4,0:1 на фоне обеих тем (норма 4,5:1);
/// <c>BrushTextSoft</c> проходит AA во всех темах. Читается при построении элемента: графики строятся заново при каждом показе.</summary>
public static class ThemeInk
{
    /// <summary>Кисть <c>BrushTextSoft</c> текущей темы; нет ресурса (ранний запуск) — серый запасной.</summary>
    public static IBrush Soft =>
        Application.Current is { } app && app.TryFindResource("BrushTextSoft", app.ActualThemeVariant, out var v) && v is IBrush b
            ? b
            : Brushes.Gray;
}
