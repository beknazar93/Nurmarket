#if NURPORTABLE && !NURANDROID
using System;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: В4 А5-20 — на Linux у клавиши не было метки времени нажатия (KeyEventArgs её не несёт), и HID-сканер штрихкода при занятом
/// интерфейсе терял начало кода: пауза между цифрами считалась по времени ОБРАБОТКИ, а не нажатия (как на Windows/Android до А4-03).
/// Метка берётся у «сырого» события ввода Avalonia (RawKeyEventArgs.Timestamp, мс от системы) через InputManager.PreProcess: это закрытый API
/// Avalonia (в проекте Linux включён AvaloniaAccessUnstablePrivateApis, как в проекте Android). Событие обрабатывается синхронно вместе с
/// KeyDown, поэтому метка действительна только пока эта клавиша обрабатывается; устаревшая (&gt; 100 мс) не используется — тогда пауза
/// считается по часам, как раньше. Любая ошибка — молча прежнее поведение.</summary>
internal static class LinuxRawKeyClock
{
    private static bool _installed;
    private static ulong _lastTimestamp;
    private static long _lastTick;

    /// <summary>Метка текущей клавиши (мс), null — нет или устарела.</summary>
    public static ulong? TryGet()
    {
        EnsureInstalled();
        if (_lastTick == 0 || Environment.TickCount64 - _lastTick > 100 || _lastTimestamp == 0)
            return null;
        return _lastTimestamp;
    }

    public static void EnsureInstalled()
    {
        if (_installed)
            return;
        _installed = true;
        try
        {
            var manager = Avalonia.AvaloniaLocator.Current.GetService(typeof(IInputManager)) as IInputManager;
            if (manager is null)
            {
                _installed = false; // Avalonia ещё не поднялась — попробуем при следующем нажатии
                return;
            }
            manager.PreProcess.Subscribe(new KeyObserver());
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Linux: метка времени клавиши недоступна ({ex.Message}) — пауза сканера по часам.", "WARNING");
        }
    }

    private sealed class KeyObserver : IObserver<RawInputEventArgs>
    {
        public void OnNext(RawInputEventArgs value)
        {
            if (value is RawKeyEventArgs { Type: RawKeyEventType.KeyDown } key)
            {
                _lastTimestamp = key.Timestamp;
                _lastTick = Environment.TickCount64;
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
#endif
