using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-21, владелец: «На сенсорном экране ползунки не слушаются пальцев». Страницы настроек лежат в ScrollViewer, а у его
/// ScrollContentPresenter есть распознаватель прокрутки пальцем (ScrollGestureRecognizer): как только палец сдвинулся больше чем на 5 точек
/// по вертикали (а у пальца это всегда — он «плывёт» при перетаскивании ползунка вбок), распознаватель забирает касание себе, ползунок
/// получает «потерю захвата» и замирает; мышь этого не касается — потому на ПК всё работало. Теперь касание, начатое на ползунке
/// (<see cref="Slider"/>) или на самописной поверхности-«ползунке» (палитра цвета, рамка обрезки QR), не отдаётся распознавателям
/// прокрутки: PreventGestureRecognition. Прокрутка пальцем по остальному месту страницы работает как раньше.
/// Одним вызовом <see cref="Register"/> защищены все Slider программы кассы и владельца (Windows, Android, Linux); свои поверхности — через
/// <see cref="Protect"/>.</summary>
public static class TouchSliderGuard
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        // Bubble + handledEventsToo: Thumb и дорожка сами помечают нажатие обработанным, но запрет жеста нужен всё равно.
        InputElement.PointerPressedEvent.AddClassHandler<Slider>(OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Самописный «ползунок» (холст палитры, рамка обрезки): касание на нём не уходит в прокрутку страницы.</summary>
    public static void Protect(InputElement surface) =>
        surface.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
            e.PreventGestureRecognition();
    }
}
