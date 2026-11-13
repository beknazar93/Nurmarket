using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11, владелец: «при выходе или сворачивании окна и возврате кнопки перестают работать! В закупках не нажимается удаление».
/// Что может «залипнуть», когда окно уходит в фон (сворачивание, Alt+Tab, другое приложение): захват указателя (Avalonia не обрабатывает
/// WM_CAPTURECHANGED — захват, потерянный системой, остаётся у прежнего элемента, и следующее нажатие уходит ему), палец сенсора, чьё
/// отпускание не пришло (TouchGuard до 5 с считает новое касание «вторым пальцем» и глушит его), ячейка таблицы в режиме правки, слой
/// затемнения модалки без модалки. Здесь — сброс этих состояний: при уходе приложения в фон, при сворачивании и после разворачивания;
/// при активации — только безопасное (палец сенсора, затемнение без модалки): захват при активации не трогаем — активирующее нажатие
/// уже могло его поставить.</summary>
public static class InputStateReset
{
    private static readonly ConditionalWeakTable<TopLevel, WeakReference<IPointer>> LastPointer = new();
    private static bool _registered;

    /// <summary>Сколько раз выполнялся полный сброс (для тестов и журнала).</summary>
    public static int ResetCount { get; private set; }

    /// <summary>Запомнить последний указатель каждого окна (чтобы снять его захват при уходе в фон). Один раз на запуск.</summary>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            try
            {
                var weak = LastPointer.GetValue(top, _ => new WeakReference<IPointer>(e.Pointer));
                weak.SetTarget(e.Pointer);
            }
            catch
            {
                // запоминание — вспомогательное
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Окно потеряло активность. Если следом не активировалось другое окно программы (ушли в другое приложение / свернули) —
    /// полный сброс во всех окнах программы. Переход в свою модалку (она активна) ничего не сбрасывает.</summary>
    public static void OnDeactivated(Window window)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 2026-11-11 (снимок владельца: подсказка «Товары» залипла поверх левой рейки после перехода в раздел): у окна, потерявшего активность,
                // открытые подсказки закрываем всегда — и когда активным стало другое окно программы (раздел владельца, модалка).
                CloseToolTips(window);
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    if (desktop.Windows.Any(w => w.IsActive))
                        return;
                    foreach (var w in desktop.Windows.ToList())
                        Reset(w, "программа в фоне");
                }
                else
                {
                    Reset(window, "программа в фоне");
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Сброс ввода при уходе в фон не удался: {ex.Message}", "WARNING");
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Окно активировано: только безопасный сброс (палец сенсора; затемнение без модалки снимает ModalWindowManager).</summary>
    public static void OnActivated(Window window)
    {
        TouchGuard.ResetFromPlatform();
    }

    /// <summary>2026-11-11: закрыть открытые всплывающие подсказки (ToolTip) окна — подсказка пункта меню не должна висеть после ухода курсора в другое окно.</summary>
    public static void CloseToolTips(Window window)
    {
        try
        {
            foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).ToList())
                ToolTip.SetIsOpen(control, false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно {window.GetType().Name}: закрыть подсказки не удалось: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Полный сброс «залипших» состояний ввода окна: захват указателя, палец сенсора, правка ячеек таблиц.</summary>
    public static void Reset(Window window, string reason)
    {
        ResetCount++;
        TouchGuard.ResetFromPlatform();
        CloseToolTips(window); // 2026-11-11
        try
        {
            if (LastPointer.TryGetValue(window, out var weak) && weak.TryGetTarget(out var pointer) && pointer.Captured is not null)
                pointer.Capture(null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно {window.GetType().Name}: снять захват указателя ({reason}) не удалось: {ex.Message}", "WARNING");
        }
        try
        {
            // Ячейка таблицы, оставшаяся в режиме правки, — завершить правку (как щелчок мимо); неверный ввод таблица сама оставит в правке.
            foreach (var grid in window.GetVisualDescendants().OfType<DataGrid>().ToList())
                grid.CommitEdit();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно {window.GetType().Name}: завершить правку ячейки ({reason}) не удалось: {ex.Message}", "WARNING");
        }
    }
}
