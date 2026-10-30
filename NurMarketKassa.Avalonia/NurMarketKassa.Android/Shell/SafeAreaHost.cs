using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Media;
using AvTopLevel = Avalonia.Controls.TopLevel;

namespace NurMarketKassa;

/// <summary>2026-10-29, владелец: «на Android нет отступа справа, трудно нажать три точки». Вид кассы (ScreenFitHost)
/// шёл вплотную к краю экрана: справа его перекрывали системная панель/жесты, вырез камеры и скругление угла.
/// Теперь вокруг вида — безопасные поля из InsetsManager Avalonia (системные панели, вырез) и не меньше
/// <see cref="MinRight"/> точек справа (<see cref="MinLeft"/> слева): кнопки у правого края достаются пальцем.
/// Поля красятся цветом окна кассы (фон TopLevel уже привязан в WindowLayerHost).</summary>
public sealed class SafeAreaHost : Border
{
    /// <summary>Наименьший отступ справа (dp): жесты «назад» Android и скруглённые углы.</summary>
    public const double MinRight = 20;
    public const double MinLeft = 8;

    private IInsetsManager? _insets;

    public SafeAreaHost(Control child)
    {
        Child = child;
        Padding = new Thickness(MinLeft, 0, MinRight, 0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            _insets = AvTopLevel.GetTopLevel(this)?.InsetsManager;
            if (_insets is not null)
                _insets.SafeAreaChanged += OnSafeAreaChanged;
            Apply(_insets?.SafeAreaPadding ?? default);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: безопасные поля не получены — {ex.Message}", "WARNING");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets is not null)
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
        _insets = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => Apply(e.SafeAreaPadding);

    private void Apply(Thickness safe)
    {
        // Avalonia сам сдвигает содержимое TopLevel на безопасную область (AutoSafeAreaPadding); здесь добавляем только
        // недостающее до минимума, чтобы поля не удваивались.
        Padding = new Thickness(
            Math.Max(0, MinLeft - safe.Left),
            0,
            Math.Max(0, MinRight - safe.Right),
            0);
    }
}
