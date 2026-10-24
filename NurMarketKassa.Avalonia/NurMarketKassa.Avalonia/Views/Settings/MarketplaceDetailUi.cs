using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-3): общие мелочи страницы «подробно» — значки интерфейса, привязка к
// ресурсам темы, кликабельная плашка (круглые кнопки ленты, строки «Похожие», пауза GIF) и коробка с пропорцией 16:9.

/// <summary>Коробка с пропорцией 16:9: ширина — вся доступная (не больше MaxWidth), высота — 9/16 от неё.</summary>
internal class MarketplaceAspectBox : Decorator
{
    public double Ratio { get; set; } = 9.0 / 16.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 720 : availableSize.Width;
        var height = width * Ratio;
        Child?.Measure(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

internal static class MarketplaceDetailUi
{
    // Запасные значки интерфейса (Material Design Icons, 24×24, Apache 2.0): пока набор MarketplaceIcons (П-2) пуст.
    private static Geometry? Fallback(string key) => key switch
    {
        "play" => Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z"),
        "pause" => Geometry.Parse("M14,19H18V5H14M6,19H10V5H6V19Z"),
        "play-circle-outline" => Geometry.Parse("M10,16.5V7.5L16,12M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20Z"),
        "lock-outline" => Geometry.Parse("M12,17C10.89,17 10,16.1 10,15C10,13.89 10.89,13 12,13A2,2 0 0,1 14,15A2,2 0 0,1 12,17M18,20V10H6V20H18M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6C4.89,22 4,21.1 4,20V10C4,8.89 4.89,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z"),
        "information-outline" => Geometry.Parse("M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z"),
        "chevron-left" => Resource("IconChevronLeft"),
        "chevron-right" => Resource("IconChevronRight"),
        "close" => Resource("IconClose"),
        "check" => Resource("IconCheck"),
        _ => null,
    };

    private static Geometry? Resource(string key) =>
        Application.Current?.TryFindResource(key, out var g) == true ? g as Geometry : null;

    /// <summary>Геометрия значка: сначала набор Маркетплейса, потом запасной; неизвестный ключ — information-outline.</summary>
    public static Geometry? Icon(string key) =>
        (string.IsNullOrEmpty(key) ? null : MarketplaceIcons.Get(key)) ?? Fallback(key) ?? Fallback("information-outline");

    public static PathIcon PathIconOf(string key, double size, string? brushKey = null)
    {
        var icon = new PathIcon { Width = size, Height = size, Data = Icon(key), IsHitTestVisible = false };
        if (brushKey != null)
            Bind(icon, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, brushKey);
        return icon;
    }

    /// <summary>Привязать свойство к ресурсу темы (следует за светлой/тёмной без пересборки).</summary>
    public static void Bind(Control control, AvaloniaProperty property, string resourceKey) =>
        control.Bind(property, control.GetResourceObservable(resourceKey));

    public static TextBlock Text(string text, double size, string brushKey, FontWeight? weight = null, bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Bind(block, TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>Сделать плашку кликабельной: мышь/касание, Enter и пробел, кольцо фокуса при Tab, подсветка при наведении.</summary>
    public static void MakeClickable(Border border, string automationName, Action click, string? hoverBrushKey = null)
    {
        border.Focusable = true;
        border.IsTabStop = true;
        border.Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(border, automationName);
        AutomationProperties.SetControlTypeOverride(border, AutomationControlType.Button);
        ToolTip.SetTip(border, automationName);
        var baseBorderThickness = border.BorderThickness;
        var pressed = false;
        border.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            {
                pressed = true;
                e.Handled = true; // не даём ленте/странице начать свою прокрутку
            }
        };
        border.PointerExited += (_, _) => pressed = false;
        border.PointerReleased += (_, e) =>
        {
            if (pressed && e.InitialPressMouseButton == MouseButton.Left && new Rect(border.Bounds.Size).Contains(e.GetPosition(border)))
            {
                e.Handled = true;
                click();
            }
            pressed = false;
        };
        border.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                click();
            }
        };
        border.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            {
                border.BorderThickness = new Thickness(2);
                Bind(border, Border.BorderBrushProperty, "BrushFocusRing");
            }
        };
        border.LostFocus += (_, _) =>
        {
            border.ClearValue(Border.BorderBrushProperty);
            border.BorderThickness = baseBorderThickness;
        };
        if (hoverBrushKey != null)
        {
            border.PointerEntered += (_, _) => Bind(border, Border.BackgroundProperty, hoverBrushKey);
            border.PointerExited += (_, _) => border.ClearValue(Border.BackgroundProperty);
        }
    }

    /// <summary>Круглая кнопка со стрелкой/крестом (лента слайдов, просмотр, пауза): фон — ресурс темы либо цвет.</summary>
    public static Border RoundButton(string iconKey, double size, double iconSize, string automationName, Action click, bool onDark)
    {
        var icon = PathIconOf(iconKey, iconSize);
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        if (onDark)
        {
            border.Background = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            icon.Foreground = Brushes.White;
        }
        else
        {
            Bind(border, Border.BackgroundProperty, "BrushPanelElevated");
            Bind(icon, Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "BrushText");
            border.BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 8, Color = Color.FromArgb(0x40, 0, 0, 0) });
        }
        MakeClickable(border, automationName, click);
        return border;
    }

    /// <summary>Подпись состояния (5 языков) — общие слова страницы.</summary>
    public static string Word(string key) => key switch
    {
        "prev" => Tr.T("Предыдущий слайд", "Мурунку слайд", "Previous slide", "Önceki slayt", "Oldingi slayd"),
        "next" => Tr.T("Следующий слайд", "Кийинки слайд", "Next slide", "Sonraki slayt", "Keyingi slayd"),
        "close" => Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
        "pause" => Tr.T("Пауза", "Тындыруу", "Pause", "Duraklat", "To'xtatish"),
        "play" => Tr.T("Воспроизвести", "Ойнотуу", "Play", "Oynat", "Ijro etish"),
        _ => key,
    };

    /// <summary>Выполнить позже, после отрисовки (освобождение картинок, которые могла ещё держать отрисовка).</summary>
    public static void PostBackground(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}
