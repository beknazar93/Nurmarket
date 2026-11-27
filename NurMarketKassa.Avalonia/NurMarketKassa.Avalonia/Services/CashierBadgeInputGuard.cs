using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
#if !NURANDROID
using Avalonia.Controls.Primitives;
#endif
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-27 (вечер, баг с кассы клиента: в «Смене кассира» скан бейджа напечатался в поле Email — секрет был виден на экране):
/// окно, которое само обрабатывает скан QR-бейджа кассира (экран входа, «Смена кассира», основное окно кассы).</summary>
public interface ICashierBadgeScanTarget
{
    /// <param name="raw">Строка бейджа (секрет!) — не показывать и не писать в журнал.</param>
    void OnCashierBadgeScanned(string raw);
}

/// <summary>2026-11-27 (вечер): глобальный перехват строки QR-бейджа во ВСЕХ окнах программы — до текстового поля, даже если фокус
/// в логине/пароле/поиске. Обработчик класса на TopLevel (туннель — раньше полей и обработчиков окна) копит быстро набранные символы;
/// как только в них появился префикс NMBADGE (в любой раскладке), поле с фокусом возвращается к тексту до начала префикса, остальные
/// символы и клавиши до Enter глотаются, по Enter строка уходит окну-обработчику (<see cref="ICashierBadgeScanTarget"/>), а в прочих окнах
/// просто отбрасывается. Секрет бейджа в поле, на экран и в журнал не попадает.</summary>
public static class CashierBadgeInputGuard
{
    /// <summary>Пауза между символами, после которой накопленное считается ручным вводом и сбрасывается.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1500);

    private sealed class State
    {
        public readonly StringBuilder Buffer = new();
        /// <summary>Для каждого символа буфера — поле с фокусом и его текст/курсор ДО ввода этого символа.</summary>
        public readonly List<(TextBox? Box, string? Text, int Caret)> Before = new();
        public bool Capturing;
        public readonly StringBuilder Captured = new();
        public DateTime LastUtc;

        public void Reset()
        {
            Buffer.Clear();
            Before.Clear();
            Capturing = false;
            Captured.Clear();
        }
    }

    private static readonly ConditionalWeakTable<Avalonia.Controls.TopLevel, State> States = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;
        InputElement.TextInputEvent.AddClassHandler<Avalonia.Controls.TopLevel>(OnTextInput, RoutingStrategies.Tunnel);
        InputElement.KeyDownEvent.AddClassHandler<Avalonia.Controls.TopLevel>(OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnTextInput(Avalonia.Controls.TopLevel top, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;
        var s = States.GetOrCreateValue(top);
        var now = DateTime.UtcNow;
        if (now - s.LastUtc > Gap)
            s.Reset();
        s.LastUtc = now;

        if (s.Capturing)
        {
            if (s.Captured.Length < 256)
                s.Captured.Append(e.Text);
            e.Handled = true;
            return;
        }

        var box = top.FocusManager?.GetFocusedElement() as TextBox;
        var snapshot = (box, box?.Text, box?.CaretIndex ?? 0);
        foreach (var _ in e.Text)
            s.Before.Add(snapshot);
        s.Buffer.Append(e.Text);
        if (s.Buffer.Length > 256)
        {
            s.Buffer.Remove(0, s.Buffer.Length - 128);
            s.Before.RemoveRange(0, s.Before.Count - 128);
        }

        var p = CashierBadgeCode.PrefixIndex(s.Buffer.ToString());
        if (p < 0)
            return;

        // Префикс бейджа: поле возвращаем к состоянию до его первого символа, дальше всё глотаем до Enter.
        var (b, text, caret) = s.Before[p];
        if (b is not null)
        {
            b.Text = text ?? "";
            b.CaretIndex = Math.Min(caret, (text ?? "").Length);
        }
        s.Capturing = true;
        s.Captured.Clear();
        s.Captured.Append(s.Buffer.ToString(p, s.Buffer.Length - p));
        s.Buffer.Clear();
        s.Before.Clear();
        e.Handled = true;
    }

    private static void OnKeyDown(Avalonia.Controls.TopLevel top, KeyEventArgs e)
    {
        if (!States.TryGetValue(top, out var s))
            return;
        if (!s.Capturing)
        {
            if (e.Key is Key.Return or Key.Escape)
            {
                s.Buffer.Clear();
                s.Before.Clear();
            }
            return;
        }
        if (DateTime.UtcNow - s.LastUtc > Gap)
        {
            s.Reset();
            return;
        }
        // Пока идёт строка бейджа, клавиши не достаются ни полю, ни горячим клавишам окна (Enter не нажимает «Оплатить»/«Сменить»).
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            s.Reset();
            return;
        }
        if (e.Key != Key.Return)
            return;
        var raw = s.Captured.ToString().Trim();
        s.Reset();
#if NURANDROID
        object? window = top; // 2026-11-27: на Android свои окна-слои (Shell\Window.cs) — бейдж только глотается
#else
        object? window = top is PopupRoot popup ? popup.ParentTopLevel : top;
#endif
        if (window is ICashierBadgeScanTarget target)
            target.OnCashierBadgeScanned(raw);
        else
            PosLogger.Log($"Скан QR-бейджа в окне «{(window as Avalonia.Controls.Window)?.Title ?? window?.GetType().Name}» — отброшен (здесь вход по бейджу не делается).", "AUTH");
    }
}
