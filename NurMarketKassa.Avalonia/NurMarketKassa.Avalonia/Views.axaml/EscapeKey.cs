using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Esc в окне, где поверх основного содержимого открывается что-то своё: всплывающий
/// чек, карточка клиента, меню, подраздел (2026-09-24, просьба владельца «Esc кнопка сделай
/// функциональным»). Сначала закрывается то, что открыто сверху, и только следующим нажатием —
/// само окно. В простых окнах и диалогах Esc нажимает их собственную кнопку «Закрыть/Отмена»
/// (IsCancel="True" в разметке), этот помощник для них не нужен.
///
/// Обработчик — Bubble и только на необработанное нажатие: раскрытый выпадающий список и ячейка
/// таблицы в режиме правки забирают Esc себе раньше, окно при этом не закрывается.</summary>
public static class EscapeKey
{
    /// <param name="closeInner">Закрывает то, что открыто поверх, и возвращает true; false —
    /// поверх ничего нет.</param>
    /// <param name="closeWindow">Как закрыть само окно; по умолчанию <see cref="Window.Close()"/>.</param>
    public static void Attach(Window window, Func<bool>? closeInner = null, Action? closeWindow = null)
    {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
                return;

            e.Handled = true;
            if (closeInner?.Invoke() == true)
                return;

            if (closeWindow != null)
                closeWindow();
            else
                window.Close();
        }, RoutingStrategies.Bubble);
    }
}
