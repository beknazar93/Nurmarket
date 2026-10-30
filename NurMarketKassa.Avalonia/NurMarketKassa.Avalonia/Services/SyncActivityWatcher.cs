using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-29 (скорость синхронизации, п. 6): сообщает SyncCenter, в каком режиме окно — свёрнуто, активно, есть ли товары в открытом чеке.
/// По этому адаптивный опрос ленты каталога и пульса продаж решает, как часто ходить на сервер (3 с / 8 с / 30–60 с). Только флаги,
/// ничего не читает с сервера и не трогает интерфейс; вызывается один раз из конструктора окна.</summary>
public static class SyncActivityWatcher
{
    /// <param name="basket">Корзина кассы (для «открытого чека»); у программы владельца null.</param>
    /// <param name="hasItems">Есть ли строки в открытом чеке; вызывается в UI-потоке.</param>
    public static void Attach(Window window, INotifyPropertyChanged? basket = null, Func<bool>? hasItems = null)
    {
        void Update()
        {
            // 2026-10-30: Android в фоне (SyncCenter.AppInBackground) остаётся «свёрнутым», даже если оболочка окна считает себя видимой.
            SyncCenter.WindowMinimized = window.WindowState == WindowState.Minimized || !window.IsVisible || SyncCenter.AppInBackground;
            SyncCenter.WindowActive = window.IsActive;
        }

#if !NURANDROID
        // 2026-10-30 (оптимизация кассы, Б1): на Linux/macOS нет GetLastInputInfo — «простой» считаем по последнему клику/клавише/колесу в окне
        // (сканер штрихкода — это клавиши). На Windows системный счётчик точнее и охватывает весь сеанс — там обработчики не нужны.
        // На Android то же делает MainActivity (касания и клавиши).
        if (!OperatingSystem.IsWindows())
        {
            window.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => SyncCenter.NoteInput(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            window.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, (_, _) => SyncCenter.NoteInput(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, _) => SyncCenter.NoteInput(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        }
#endif
        window.PropertyChanged += (_, e) =>
        {
#if NURANDROID
            // 2026-10-29: в оболочке Android у WindowBase.IsActive нет StyledProperty — активность ловят события Activated/Deactivated ниже.
            if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
#else
            if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty || e.Property == WindowBase.IsActiveProperty)
#endif
                Update();
        };
        window.Activated += (_, _) => Update();
        window.Deactivated += (_, _) => Update();
        window.Opened += (_, _) => Update();
        if (basket is not null && hasItems is not null)
            basket.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "HasItems" or "HasLines" or "IsEmpty")
                    SyncCenter.OpenCheck = hasItems();
            };
    }
}
