using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-08, владелец: «Android (NurMarket владелец): сенсор откликается очень плохо». Прокрутка пальцем в Avalonia 11.2
/// (ScrollGestureRecognizer в шаблонах ScrollViewer и DataGrid) начинается, когда палец сдвинулся всего на 5 точек (половина TapSize),
/// и сразу забирает касание себе: кнопка, строка таблицы, вкладка внутри прокручиваемой области своё нажатие теряют. Палец на телефоне
/// при обычном нажатии «плывёт» на 3–8 точек — поэтому часть нажатий в списках и таблицах программы владельца просто пропадала.
/// У Android порог 8 dp (ViewConfiguration touch slop), у Flutter 18. Здесь — как у Android, 8 точек: короткое касание остаётся
/// нажатием, прокрутка начинается без рывка (Avalonia учитывает порог при старте). Порог нарочно меньше 10 у TouchGuard (прокрутка
/// за кнопки) — порядок прежний: сначала жест Avalonia (с инерцией), потом запасной путь TouchGuard.
/// Включается только на Android (<see cref="TouchGuard.Register"/>); Windows-касса не затрагивается.</summary>
public static class TouchScrollSlop
{
    /// <summary>Порог начала прокрутки пальцем, точек (dp).</summary>
    public const int Distance = 8;

    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        Control.LoadedEvent.AddClassHandler<ScrollContentPresenter>((p, _) => Apply(p), RoutingStrategies.Direct, handledEventsToo: true);
        Control.LoadedEvent.AddClassHandler<DataGridRowsPresenter>((p, _) => Apply(p), RoutingStrategies.Direct, handledEventsToo: true);
    }

    /// <summary>Поднимает порог у распознавателей прокрутки элемента (меньший порог не уменьшает). Возвращает число изменённых.</summary>
    public static int Apply(InputElement presenter, int distance = Distance)
    {
        var changed = 0;
        try
        {
            foreach (var recognizer in presenter.GestureRecognizers)
            {
                if (recognizer is ScrollGestureRecognizer scroll && scroll.ScrollStartDistance < distance)
                {
                    scroll.ScrollStartDistance = distance;
                    changed++;
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сенсор: порог прокрутки не изменён: {ex.Message}", "WARNING");
        }
        return changed;
    }
}
