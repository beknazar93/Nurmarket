using System;
using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец (снимки с телефона): «исправь баги с адаптацией» — в окнах истории чеков,
/// возврата, оплаты долга, проката ряды кнопок и полей рассчитаны на ширину компьютера и на телефоне
/// уходили за край. Окно сообщает, узкое ли оно сейчас; само окно переставляет свои ряды (apply).
/// Только Android — на Windows окна не меняются.</summary>
public static class NarrowLayout
{
    /// <summary>Телефон: окно уже <paramref name="maxWidth"/> точек — apply(true), шире — apply(false).
    /// apply вызывается только при смене состояния.</summary>
    public static void Attach(Control control, double maxWidth, Action<bool> apply)
    {
        if (!OperatingSystem.IsAndroid())
            return;
        bool? last = null;
        control.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < maxWidth;
            if (last == narrow)
                return;
            last = narrow;
            apply(narrow);
        };
    }
}
