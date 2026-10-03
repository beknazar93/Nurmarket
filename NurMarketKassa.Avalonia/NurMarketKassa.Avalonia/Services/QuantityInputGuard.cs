using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-03, владелец: «если во время скана товар закончился — в количество вводится отсканированное
/// 12-значное число; сделай ограничение количества — 5». Поле количества принимает не больше 5 знаков. Шестой
/// знак означает, что в поле пишет сканер (штрихкод — 8–13 цифр подряд): поле очищается, а не остаётся с
/// первыми пятью цифрами кода (иначе Enter сканера провёл бы «76249 шт»).</summary>
public static class QuantityInputGuard
{
    public const int MaxLength = 5;

    public static void Attach(TextBox box, Action? onScanDetected = null)
    {
        box.MaxLength = MaxLength;
        box.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            var text = box.Text ?? "";
            var selected = Math.Abs(box.SelectionEnd - box.SelectionStart);
            if (text.Length - selected + (e.Text?.Length ?? 0) <= MaxLength)
                return;
            e.Handled = true;
            box.Text = "";
            onScanDetected?.Invoke();
        }, RoutingStrategies.Tunnel);
    }
}
