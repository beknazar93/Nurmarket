using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-06, владелец (фото моноблока клиента с сенсорным экраном): «экранная клавиатура не выходит — это баг у
/// клиентов на моноблоках». Настройка «Показывать экранную клавиатуру автоматически» (UserPreferences.AutoShowTouchKeyboard,
/// по умолчанию включена) нигде не применялась: клавиатура открывалась только кнопкой. Теперь касание пальцем (или пером)
/// любого поля ввода во всех окнах открывает клавиатуру Windows (osk.exe, как кнопка «Клавиатура» — владелец 15.09 просил
/// именно её). Мышь, физическая клавиатура и сканер не затрагиваются; уже открытая клавиатура только поднимается наверх.</summary>
internal static class TouchKeyboardAuto
{
    private static bool _registered;
    private static DateTime _lastShownUtc;

    public static void Register()
    {
        if (_registered || !OperatingSystem.IsWindows())
            return;
        _registered = true;
        InputElement.PointerReleasedEvent.AddClassHandler<TextBox>(OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnReleased(TextBox box, PointerReleasedEventArgs e)
    {
        try
        {
            if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen)
                || !UserPreferences.Instance.AutoShowTouchKeyboard
                || box.IsReadOnly || !box.IsEnabled || TouchGuard.MultiTouchRecently)
                return;
            // Касания подряд по полям — не дёргаем клавиатуру каждый раз.
            if (DateTime.UtcNow - _lastShownUtc < TimeSpan.FromMilliseconds(700))
                return;
            _lastShownUtc = DateTime.UtcNow;
            App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Экранная клавиатура по касанию не открылась: {ex.Message}", "WARNING");
        }
    }
}
