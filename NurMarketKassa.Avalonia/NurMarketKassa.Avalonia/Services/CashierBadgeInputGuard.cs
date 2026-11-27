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
using Avalonia.Threading;
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
/// просто отбрасывается. Секрет бейджа в поле, на экран и в журнал не попадает.
/// 2026-11-27 (живая проверка 1.17.97, D3): настоящий сканер шлёт на каждый символ WM_KEYDOWN + WM_CHAR + WM_KEYUP, а Avalonia/Win32 НЕ шлёт
/// WM_CHAR (TextInput) после обработанного KeyDown. Поэтому после префикса символы берутся из самих KeyDown (KeySymbol, иначе код клавиши),
/// а TextInput, если всё же пришёл, лишь гасится. Каждая клавиша продлевает «живость» строки (раньше считалась пауза от последнего TextInput —
/// медленный ввод обрывал перехват и цифры секрета утекали в штрихкод). Конец строки — Enter или Tab; если конца нет, а строка уже целый
/// бейдж и клавиш нет полсекунды — она уходит сама.</summary>
public static class CashierBadgeInputGuard
{
    /// <summary>Пауза между символами, после которой накопленное считается ручным вводом и сбрасывается.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(1500);
    /// <summary>Целый бейдж и тишина столько — сканер без Enter в конце закончил, строка уходит окну.</summary>
    private static readonly TimeSpan IdleDeliver = TimeSpan.FromMilliseconds(500);
    /// <summary>После выдачи по тишине запоздавший Enter сканера (не ставший частью строки) глотается столько времени.</summary>
    private static readonly TimeSpan LateEnterSwallow = TimeSpan.FromMilliseconds(1500);
    /// <summary>Сканер с концом CR+LF шлёт два Enter: второй не должен «Отменить» только что открывшийся вопрос.</summary>
    private static readonly TimeSpan SecondEnterSwallow = TimeSpan.FromMilliseconds(400);

    private sealed class State
    {
        public readonly StringBuilder Buffer = new();
        /// <summary>Для каждого символа буфера — поле с фокусом и его текст/курсор ДО ввода этого символа.</summary>
        public readonly List<(TextBox? Box, string? Text, int Caret)> Before = new();
        public bool Capturing;
        public readonly StringBuilder Captured = new();
        public DateTime LastUtc;
        /// <summary>Сколько символов взято из KeyDown, а соответствующий TextInput ещё не пришёл (на Windows он не придёт вовсе).</summary>
        public int PendingKeyChars;
        public Avalonia.Controls.TopLevel? Top;
        /// <summary>Поколение «тихого» таймера: каждое новое нажатие и сброс увеличивают его — устаревший отсчёт ничего не делает.</summary>
        public int IdleGeneration;

        public void Reset()
        {
            Buffer.Clear();
            Before.Clear();
            Capturing = false;
            Captured.Clear();
            PendingKeyChars = 0;
            IdleGeneration++;
        }
    }

    private static readonly ConditionalWeakTable<Avalonia.Controls.TopLevel, State> States = new();
    private static bool _registered;
    private static DateTime _lateEnterUntilUtc;

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
        s.Top = top;
        var now = DateTime.UtcNow;
        if (now - s.LastUtc > Gap)
            s.Reset();
        s.LastUtc = now;

        if (s.Capturing)
        {
            // 2026-11-27: символ этой клавиши уже взят из KeyDown — повторно не добавляем (на Linux/Android TextInput приходит и после обработанного KeyDown).
            if (s.PendingKeyChars > 0)
                s.PendingKeyChars = Math.Max(0, s.PendingKeyChars - e.Text.Length);
            else if (s.Captured.Length < 256)
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
        s.PendingKeyChars = 0;
        s.Buffer.Clear();
        s.Before.Clear();
        e.Handled = true;
        ArmIdleDelivery(s);
    }

    private static void OnKeyDown(Avalonia.Controls.TopLevel top, KeyEventArgs e)
    {
        var now = DateTime.UtcNow;
        // 2026-11-27: второй Enter сканера (CR+LF) или Enter без Enter-конца после выдачи по тишине — не «Оплатить», не «Отмена» открывшегося вопроса и не вход с пустыми полями (во всех окнах).
        var capturing = States.TryGetValue(top, out var s) && s.Capturing; // идущий скан важнее «запоздавшего» Enter прошлого
        if (!capturing && e.Key == Key.Return && now < _lateEnterUntilUtc)
        {
            _lateEnterUntilUtc = DateTime.MinValue;
            e.Handled = true;
            return;
        }
        if (s is null)
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
        if (now - s.LastUtc > Gap)
        {
            s.Reset();
            return;
        }
        s.LastUtc = now; // 2026-11-27: каждая клавиша строки продлевает её (раньше — только TextInput, которого после обработанного KeyDown нет)
        // Пока идёт строка бейджа, клавиши не достаются ни полю, ни горячим клавишам окна (Enter не нажимает «Оплатить»/«Сменить»).
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            s.Reset();
            return;
        }
        if (e.Key is Key.Return or Key.Tab)
        {
            _lateEnterUntilUtc = now + SecondEnterSwallow;
            Deliver(top, s);
            return;
        }
        var ch = KeyChar(e);
        if (ch is null)
            return;
        if (s.Captured.Length < 256)
        {
            s.Captured.Append(ch);
            s.PendingKeyChars += ch.Length;
        }
        ArmIdleDelivery(s);
    }

    /// <summary>Символ клавиши из самого KeyDown: KeySymbol (Windows берёт его у раскладки), иначе код клавиши — буквы и цифры по латинским
    /// позициям (не зависят от раскладки), «:» — для Shift+«;» и Shift+цифры (русская раскладка). Остальное — null (разделители не нужны:
    /// <see cref="CashierBadgeCode.TryParse"/> читает бейдж и без них).</summary>
    internal static string? KeyChar(KeyEventArgs e)
    {
        var sym = e.KeySymbol;
        if (!string.IsNullOrEmpty(sym) && !char.IsControl(sym[0]) && !char.IsWhiteSpace(sym[0]))
            return sym.Length > 4 ? sym[..4] : sym;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var k = e.Key;
        if (k is >= Key.A and <= Key.Z)
            return ((char)('a' + (k - Key.A))).ToString();
        if (k is >= Key.D0 and <= Key.D9)
            return shift ? ":" : ((char)('0' + (k - Key.D0))).ToString();
        if (k is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k == Key.OemSemicolon)
            return ":";
        return null;
    }

    /// <summary>Строка уже целый бейдж, а клавиш нет полсекунды — сканер без Enter в конце закончил: отдаём её окну.</summary>
    private static void ArmIdleDelivery(State s)
    {
        var generation = ++s.IdleGeneration;
        if (!s.Capturing || s.Top is null || !CashierBadgeCode.TryParse(s.Captured.ToString(), out _, out _))
            return;
        // Не DispatcherTimer: отсчёт на пуле потоков и возврат в интерфейс через Post (в Headless-тестах таймеры диспетчера не тикают, а Post — да).
        _ = System.Threading.Tasks.Task.Delay(IdleDeliver).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (generation != s.IdleGeneration || !s.Capturing || s.Top is not { } top)
                return;
            _lateEnterUntilUtc = DateTime.UtcNow + LateEnterSwallow;
            Deliver(top, s);
        }), System.Threading.Tasks.TaskScheduler.Default);
    }

    private static void Deliver(Avalonia.Controls.TopLevel top, State s)
    {
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
