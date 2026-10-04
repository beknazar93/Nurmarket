using Android.OS;

namespace NurMarketKassa;

/// <summary>2026-10-04, Android-касса: вложенный цикл сообщений для синхронных диалогов.
///
/// В Windows-кассе ~170 мест показывают диалог и ЖДУТ ответа синхронно (PosDialogHost.Show →
/// Dispatcher.UIThread.MainLoop). У Avalonia на Android вложенного цикла нет (MainLoop бросает
/// исключение). Здесь он сделан средствами самого Android: Looper.Loop() на главном потоке
/// продолжает разбирать касания, отрисовку и задания Avalonia, пока диалог открыт; когда диалог
/// закрыт, в очередь кладётся сообщение, которое бросает особое исключение — оно разматывает
/// только этот вложенный Looper.Loop() и ловится здесь же. Это известный приём «модального
/// диалога» на Android; вложенные диалоги закрываются строго по порядку (стек кадров).
///
/// НЕ ПРОВЕРЕНО на устройстве (на ПК нет эмулятора). Если на каком-то аппарате приём не сработает,
/// <see cref="Enabled"/> = false переводит синхронные диалоги в режим «показать и не ждать»
/// (сообщения работают, подтверждения считаются отказом).</summary>
public static class AndroidNestedLoop
{
    internal const string ExitMarker = "NurMarketKassa.AndroidNestedLoop.Exit";

    public static bool Enabled { get; set; } = true;

    private sealed class Frame
    {
        public bool Done;
    }

    private static readonly Stack<Frame> Frames = new();
    private static Handler? _handler;

    /// <summary>Крутит вложенный цикл, пока не отменён <paramref name="token"/>.
    /// Вызывать только на главном потоке.</summary>
    public static void Run(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;

        if (!OnMainThread)
            throw new InvalidOperationException("Синхронный диалог на Android можно показать только с главного потока.");

        _handler ??= new Handler(Looper.MainLooper!);
        var frame = new Frame();
        Frames.Push(frame);
        using var registration = token.Register(() =>
        {
            frame.Done = true;
            PostExitCheck();
        });

        try
        {
            Looper.Loop();
        }
        catch (Java.Lang.RuntimeException ex) when (ex.Message == ExitMarker)
        {
            // штатный выход из вложенного цикла
        }
        catch (Exception ex) when (IsExitSignal(ex))
        {
            // на некоторых версиях исключение приходит обёрнутым
        }
        finally
        {
            if (Frames.Count > 0 && ReferenceEquals(Frames.Peek(), frame))
                Frames.Pop();
            // Внешний диалог мог закрыться, пока был открыт этот — выходим и из его цикла.
            if (Frames.Count > 0 && Frames.Peek().Done)
                PostExitCheck();
        }
    }

    /// <summary>2026-10-04, «запуск на других устройствах»: Looper.IsCurrentThread есть только с Android 6.0 — на 5.x
    /// касса падала бы на первом окне «Да/Нет». Сравнение с главным Looper работает на всех версиях.</summary>
    internal static bool OnMainThread => Looper.MyLooper() is { } current && current.Equals(Looper.MainLooper);

    /// <summary>2026-10-04, стресс-тест на телефоне: касса закрывалась при закрытии любого
    /// ожидающего диалога («Касса обновлена» → «Понятно»). Сигнал выхода проходит через обработчик
    /// необработанных ошибок Android, и экран ошибки (AndroidCrashReport) принимал его за падение.
    /// Сообщение у сигнала видно только через Java (у System.Exception — стандартный текст),
    /// поэтому проверяем и Java-сообщение, и полный текст с Java-стеком.</summary>
    public static bool IsExitSignal(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.Message == ExitMarker)
                return true;
            if (e is Java.Lang.Throwable jt && jt.Message == ExitMarker)
                return true;
        }
        try { return ex?.ToString().Contains(ExitMarker, StringComparison.Ordinal) == true; }
        catch { return false; }
    }

    private static void PostExitCheck() => _handler?.Post(ExitIfTopDone);

    private static void ExitIfTopDone()
    {
        if (Frames.Count > 0 && Frames.Peek().Done)
            throw new Java.Lang.RuntimeException(ExitMarker);
    }
}
