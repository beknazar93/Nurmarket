using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.ExceptionServices;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public static class PosDialogHost
{
    public static Window ResolveOwner(Window? owner)
    {
        if (owner != null)
            return owner;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main)
            return main;

        throw new InvalidOperationException("Нет активного окна для диалога.");
    }

    /// <summary>Плавное появление модалки (по просьбе пользователя, 2026-09-19: "открытие
    /// модальных окон не должно быть очень резким") — все диалоги здесь уже используют
    /// SystemDecorations="None" + TransparencyLevelHint (per-pixel alpha), поэтому анимация
    /// Opacity самого окна работает без побочных эффектов. Только вход: закрытие оставлено
    /// мгновенным — оборачивать все ~50 диалогов в отложенный Close() рискованно без
    /// пораздельной проверки каждого (можно случайно застопорить чек/оплату).</summary>
    private static void ApplyFadeIn(Window dialog)
    {
        dialog.Opacity = 0;
        dialog.Opened += OnDialogOpenedFadeIn;
    }

    private static void OnDialogOpenedFadeIn(object? sender, EventArgs e)
    {
        if (sender is not Window dialog)
            return;
        dialog.Opened -= OnDialogOpenedFadeIn;

        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
            },
        };
        _ = animation.RunAsync(dialog);
    }

    /// <summary>
    /// Shows a modal dialog. Safe to call from the UI thread (pumps the dispatcher
    /// instead of deadlocking on <c>GetResult()</c>).
    /// </summary>
    public static bool? Show(Window dialog, Window? owner)
    {
        owner = ResolveOwner(owner);
        ApplyFadeIn(dialog);
        var task = dialog.ShowDialog<bool?>(owner);

        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Synchronous dialogs must be opened on the UI thread. Use ShowAsync from background work.");

        // UI thread: pump until the dialog closes so Click → Close can complete.
        using var cts = new CancellationTokenSource();
        bool? result = null;
        ExceptionDispatchInfo? failure = null;

        async Task ObserveDialogAsync()
        {
            try
            {
                result = await task.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cts.Cancel();
            }
        }

        var observer = ObserveDialogAsync();

        if (!observer.IsCompleted)
            Dispatcher.UIThread.MainLoop(cts.Token);
        failure?.Throw();
        return result;
    }

    public static Task<bool?> ShowAsync(Window dialog, Window? owner)
    {
        owner = ResolveOwner(owner);
        ApplyFadeIn(dialog);
        return dialog.ShowDialog<bool?>(owner);
    }

    public static Task<T?> ShowDialogAsync<T>(Window owner, Window dialog) where T : class
    {
        ApplyFadeIn(dialog);
        return dialog.ShowDialog<T?>(owner);
    }
}
