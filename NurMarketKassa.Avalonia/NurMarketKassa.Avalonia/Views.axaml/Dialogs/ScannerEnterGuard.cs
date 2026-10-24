using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-22 (модалки, пакет 1): Enter сканера не нажимает главную кнопку окна. Сканер «печатает» код быстрее
/// 60 мс на символ и сразу жмёт Enter; окно, открытое посреди сканирования (подтверждение оплаты, упаковка, «нет на складе»),
/// раньше принимало этот Enter как «Далее»/«Подтвердить». Такой Enter (серия ≥ 4 быстрых символов прямо перед ним, или
/// готовый/только что отданный код в службе сканера) окно не получает; Enter кассира работает как раньше. Тот же признак,
/// что у CheckoutDialog.OnScannerGuardTunnel (там своя защита с подсказкой — не заменяется).</summary>
internal sealed class ScannerEnterGuard
{
    private const int ScanKeyGapMs = 60;
    private const int ScanEnterGapMs = 120;
    private const int ScanMinChars = 4;

    private readonly AvaloniaKeyboardWedgeBarcodeService? _scanner =
        App.AppHost?.Services.GetService<IBarcodeInputService>() as AvaloniaKeyboardWedgeBarcodeService;

    private readonly Action? _onBlocked;
    private int _burstChars;
    private long _lastBurstCharAt;

    private ScannerEnterGuard(Action? onBlocked) => _onBlocked = onBlocked;

    /// <summary>Подключить к окну (раньше всех: Tunnel). <paramref name="onBlocked"/> — что показать, когда Enter сканера отброшен.</summary>
    public static ScannerEnterGuard Attach(Window window, Action? onBlocked = null)
    {
        var guard = new ScannerEnterGuard(onBlocked);
        // handledEventsToo: общий разбор сканера (AvaloniaKeyboardWedgeBarcodeService) сам помечает быстрые символы обработанными —
        // без этого охранник их не увидел бы и не узнал серию.
        window.AddHandler(InputElement.KeyDownEvent, guard.OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        return guard;
    }

    private static long KeyTime() =>
        AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp() is { } ts ? (long)ts : Environment.TickCount64;

    private static bool IsScanChar(Key key) =>
        key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 or >= Key.A and <= Key.Z;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
            return;
        var now = KeyTime();
        if (IsScanChar(e.Key))
        {
            var fast = _burstChars > 0 && now - _lastBurstCharAt is >= 0 and <= ScanKeyGapMs;
            _burstChars = fast ? _burstChars + 1 : 1;
            _lastBurstCharAt = now;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            _burstChars = 0;
            return;
        }

        var burst = _burstChars >= ScanMinChars && now - _lastBurstCharAt is >= 0 and <= ScanEnterGapMs;
        _burstChars = 0;
        if (!burst && _scanner is not ({ HasFreshCompleteScan: true } or { ScanJustCompleted: true }))
            return;
        e.Handled = true;
        _scanner?.DiscardBuffer();
        PosLogger.Log("[UI] Enter сканера в модальном окне не принят (скан во время вопроса).", "UI");
        _onBlocked?.Invoke();
    }
}
