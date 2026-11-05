namespace NurMarketKassa.Droid;

/// <summary>2026-11-05 (аудит №4, А4-03): время нажатия клавиши для HID-сканера (клавиатурного). MainActivity кладёт
/// сюда <c>KeyEvent.EventTime</c> (мс от загрузки системы) на время обработки клавиши — как GetMessageTime в Windows.
/// Без этого пауза между цифрами меряется по времени ОБРАБОТКИ: пока касса добавляет прошлый товар, клавиши
/// следующего кода ждут в очереди, «пауза» выходит больше 220 мс и начало кода режется.</summary>
internal static class AndroidKeyClock
{
    [ThreadStatic] private static long _current;
    [ThreadStatic] private static bool _active;

    /// <summary>Время текущей обрабатываемой клавиши; null — клавиша сейчас не обрабатывается.</summary>
    public static ulong? Current => _active && _current > 0 ? (ulong)_current : null;

    public static void Install() =>
        NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestampProvider = () => Current;

    /// <summary>Обработать клавишу с её меткой времени.</summary>
    public static bool Dispatch(Android.Views.KeyEvent e, Func<Android.Views.KeyEvent, bool> action)
    {
        var previousActive = _active;
        var previous = _current;
        _current = e.EventTime;
        _active = true;
        try
        {
            return action(e);
        }
        finally
        {
            _active = previousActive;
            _current = previous;
        }
    }
}
