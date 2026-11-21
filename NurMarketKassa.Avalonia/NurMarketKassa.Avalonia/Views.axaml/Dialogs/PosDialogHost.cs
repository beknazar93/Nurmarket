using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.ExceptionServices;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21: окно, которое отличается от других окон того же типа ключом (номер чека, id документа): повтор открытия считается дублем
/// только при том же типе И том же ключе (см. <see cref="PosDialogHost.IsDuplicateOpen(Window, Window?)"/>). Без ключа — по одному типу.</summary>
public interface IDialogOpenKey
{
    string? DialogOpenKey { get; }
}

public static class PosDialogHost
{
    public static Window ResolveOwner(Window? owner)
    {
        // 2026-09-30, живой сбой в программе владельца («Пополнение и сроки» → «Подтянуть историю»):
        // раздел открыт внутри окна владельца, а его собственное окно невидимо — сообщение «Готово»
        // и затем сообщение об ошибке падали с «Cannot show window with non-visible owner».
        // Невидимого владельца заменяем видимым: его родителем или главным окном программы.
        if (owner is { IsVisible: false })
        {
            if (owner.Owner is Window { IsVisible: true } parent)
                return parent;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } visibleMain })
                return visibleMain;
        }

        // 2026-10-30, срочный баг владельца («модалка уходит на 2 план»): диалог без владельца раньше брал главное окно, даже когда
        // поверх него раздел или уже открытая модалка — новый диалог оказывался ПОД ними. Теперь владелец — верхняя модалка / активное окно;
        // и явно заданный владелец под открытой модалкой заменяется ею (модалки всегда друг над другом).
#if !NURANDROID
        if (ModalWindowManager.PreferredOwner() is { IsVisible: true } preferred
            && (owner is null || ModalWindowManager.IsOpenModal(preferred)))
            return preferred;
#endif
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
#if NURANDROID
        // 2026-11-11: на Android слой окна уже появляется плавно (WindowLayerHost.AnimateIn) — второе затухание самого окна давало
        // «тёмную модалку, потом светлее» вдвое дольше.
        return;
#else
        // 2026-11-11 (жалоба «затемнение дольше 2 с»): без анимаций при «слабом устройстве» / выключенных анимациях Windows.
        if (ModalMotion.ReduceMotion)
            return;
        dialog.Opacity = 0;
        dialog.Opened += OnDialogOpenedFadeIn;
#endif
    }

    private static void OnDialogOpenedFadeIn(object? sender, EventArgs e)
    {
        if (sender is not Window dialog)
            return;
        dialog.Opened -= OnDialogOpenedFadeIn;

        var animation = new Animation
        {
            // 2026-11-11: 160 → 110 мс — одна короткая смена непрозрачности поверх уже готового затемнения.
            Duration = TimeSpan.FromMilliseconds(ModalMotion.DialogFadeMs),
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
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Synchronous dialogs must be opened on the UI thread. Use ShowAsync from background work.");

        owner = ResolveOwner(owner);
        ApplyFadeIn(dialog);
        var task = ShowDialogDimmed<bool?>(dialog, owner);

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

#if NURANDROID
        // 2026-10-04, Android-касса: у Avalonia на Android нет вложенного цикла (MainLoop) —
        // ждём диалог вложенным циклом Android (NurMarketKassa.AndroidNestedLoop). Если он
        // выключен — диалог показан без ожидания: сообщение работает, подтверждение = «нет».
        if (!observer.IsCompleted)
        {
            if (!NurMarketKassa.AndroidNestedLoop.Enabled)
                return null;
            NurMarketKassa.AndroidNestedLoop.Run(cts.Token);
        }
#else
        if (!observer.IsCompleted)
            Dispatcher.UIThread.MainLoop(cts.Token);
#endif
        failure?.Throw();
        return result;
    }

    /// <summary>2026-10-04, Android-касса: модальный диалог из async-кода. В Windows — тот же
    /// синхронный <see cref="Show"/> (вложенный цикл, результат готов сразу, поведение прежнее);
    /// на Android — честное ожидание без вложенного цикла (<see cref="ShowAsync"/>).</summary>
    public static
#if NURANDROID
        Task<bool?>
#else
        async Task<bool?>
#endif
        ShowModalAsync(Window dialog, Window? owner)
    {
#if NURANDROID
        return ShowAsync(dialog, owner);
#else
        // 2026-11-02 (аудит №2, Д3-01): раньше здесь сразу запускался вложенный цикл (Show) — внутри обработчика Click. Нажатие через UIA
        // (экранный диктор, касание) — IInvokeProvider.Invoke, он ждёт возврата обработчика, а тот не возвращался, пока окно открыто:
        // диктор «замолкал», дерево окна было недоступно. Теперь вызывающий (его await) возвращает управление сразу, а окно открывается
        // следующим проходом цикла диспетчера (Task.Yield). Само окно и результат — прежние (тот же Show), порядок действий после
        // закрытия не менялся.
        // 2026-11-11 (жалоба «затемнение дольше 2 с»): затемнение владельца — сразу, и до разметки модалки даём отрисоваться одному кадру
        // (вместо Task.Yield — ждём кадр, не дольше 50 мс): тяжёлое окно размечается уже над затемнённым фоном, а не «ничего не происходит».
        ModalWindowManager.MarkShowRequested(dialog);
        try
        {
            var dimOwner = ResolveOwner(owner);
            ModalWindowManager.PreDim(dimOwner);
            await WaitOneFrameAsync(dimOwner).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            await Task.Yield(); // окна-владельца нет — Show ниже сообщит об этом, как раньше
        }
        return Show(dialog, owner);
#endif
    }

#if !NURANDROID
    /// <summary>2026-11-11: следующий кадр окна (затемнение уже отрисовано), но не дольше 50 мс — медленная отрисовка не задерживает модалку.</summary>
    private static Task WaitOneFrameAsync(Window window)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.RequestAnimationFrame(_ => tcs.TrySetResult());
        }
        catch
        {
            tcs.TrySetResult();
        }
        DispatcherTimer.RunOnce(() => tcs.TrySetResult(), TimeSpan.FromMilliseconds(50));
        return tcs.Task;
    }
#endif

    /// <summary>2026-11-11: ShowDialog с затемнением владельца заранее (до разметки модалки) и замером времени до первого кадра.
    /// Если окно не открылось — затемнение снимается.</summary>
    private static Task<T> ShowDialogDimmed<T>(Window dialog, Window owner)
    {
        // 2026-11-19 (список владельца, п. 7): двойное нажатие на кнопку открывало второй такой же диалог (ShowModalAsync ждёт кадр перед
        // показом) — слои затемнения накапливались. Повтор того же типа в течение 0,7 с = двойное нажатие: открытый диалог выводится вперёд,
        // а повтор завершается как «отмена» (результат по умолчанию).
        if (IsDuplicateOpen(dialog, owner))
        {
#if !NURANDROID
            ModalWindowManager.CancelPreDim(); // 2026-11-21: ShowModalAsync уже затемнил владельца заранее — повтор не откроется, затемнение снять
#endif
            return Task.FromResult(default(T)!);
        }
#if NURANDROID
        return dialog.ShowDialog<T>(owner);
#else
        ModalWindowManager.MarkShowRequested(dialog);
        ModalWindowManager.PreDim(owner);
        try
        {
            return dialog.ShowDialog<T>(owner);
        }
        catch
        {
            ModalWindowManager.CancelPreDim();
            ForgetOpen(dialog); // 2026-11-21: окно не открылось — запись «открывается» не должна залипнуть
            throw;
        }
#endif
    }

    /// <summary>2026-11-21 (жалоба владельца «чек в модалке открывается дважды»): запись об открываемом/открытом окне. Раньше повтор ловился только по
    /// времени (0,7 с от запроса) и только у того же владельца; но второе нажатие, обработанное во вложенном цикле первой модалки, получало ВЛАДЕЛЬЦЕМ
    /// эту модалку (ResolveOwner) — «другой владелец», и второе окно ложилось поверх первого. Теперь окно считается дублем, пока первое
    /// ещё не показано (но не дольше 10 с) или показано меньше 0,7 с назад, а владельцем второго может быть и само первое окно; запись снимается по Closed.</summary>
    private sealed class OpenEntry
    {
        public Type Type = null!;
        public string? Key;
        public WeakReference<Window> Window = null!;
        public WeakReference<Window>? Owner;
        public long At;
        public long OpenedAt;
        public bool Opened;
        public bool Closed;
    }

    private static readonly List<OpenEntry> Entries = new();
    private const double DuplicateWindowSeconds = 0.7;
    private const double PendingMaxSeconds = 10;

    /// <summary>true — диалог такого же типа (и с тем же ключом, см. <see cref="IDialogOpenKey"/>) уже открывается или только что открыт. Простые
    /// сообщения/вопросы не считаются (их несколько подряд — норма: ошибка за ошибкой).</summary>
    public static bool IsDuplicateOpen(Window dialog) => IsDuplicateOpen(dialog, null);

    /// <summary>2026-11-19: с владельцем — двойным нажатием считается только повтор у того же окна-владельца (если владелец известен у обоих запросов);
    /// диалог того же типа у другого владельца (другое окно, следующий сценарий подряд) — не дубль и не «отмена».
    /// 2026-11-21: владелец-само-первое-окно тоже считается повтором (см. <see cref="OpenEntry"/>).</summary>
    public static bool IsDuplicateOpen(Window dialog, Window? owner)
    {
        try
        {
            var type = dialog.GetType();
            if (type.Name is "PosAlertDialog" or "PosConfirmDialog" or "PasswordPromptDialog")
                return false;
            var key = (dialog as IDialogOpenKey)?.DialogOpenKey;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var freq = (double)System.Diagnostics.Stopwatch.Frequency;
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var e = Entries[i];
                if (e.Closed || !e.Window.TryGetTarget(out var open))
                {
                    Entries.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(open, dialog) || e.Type != type || !string.Equals(e.Key, key, StringComparison.Ordinal))
                    continue;
                var age = (now - e.At) / freq;
                var fresh = e.Opened
                    ? open.IsVisible && (age < DuplicateWindowSeconds || (now - e.OpenedAt) / freq < DuplicateWindowSeconds)
                    : age < PendingMaxSeconds;
                if (fresh && (SameOwner(e.Owner, owner) || ReferenceEquals(owner, open)))
                {
                    NurMarketKassa.Services.PosLogger.Log($"Повторное открытие {type.Name} (двойное нажатие) — второе окно не создаётся.", "UI");
                    try { if (open.IsVisible) open.Activate(); } catch { /* выведение вперёд — удобство */ }
                    return true;
                }
            }
            Register(dialog, type, key, owner, now);
        }
        catch
        {
            // защита — удобство; без неё диалог откроется как раньше
        }
        return false;
    }

    private static void Register(Window dialog, Type type, string? key, Window? owner, long now)
    {
        var entry = new OpenEntry
        {
            Type = type,
            Key = key,
            Window = new WeakReference<Window>(dialog),
            Owner = owner is null ? null : new WeakReference<Window>(owner),
            At = now,
        };
        Entries.Add(entry);
        void OnOpened(object? s, EventArgs a)
        {
            dialog.Opened -= OnOpened;
            entry.Opened = true;
            entry.OpenedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        void OnClosed(object? s, EventArgs a)
        {
            dialog.Closed -= OnClosed;
            dialog.Opened -= OnOpened;
            entry.Closed = true;
            Entries.Remove(entry);
        }
        if (dialog.IsVisible)
        {
            entry.Opened = true;
            entry.OpenedAt = now;
        }
        else
        {
            dialog.Opened += OnOpened;
        }
        dialog.Closed += OnClosed;
    }

    /// <summary>2026-11-21: окно не удалось показать — снять запись, чтобы следующий запрос не считался повтором.</summary>
    private static void ForgetOpen(Window dialog)
    {
        try
        {
            foreach (var e in Entries.Where(x => !x.Window.TryGetTarget(out var w) || ReferenceEquals(w, dialog)).ToList())
            {
                e.Closed = true;
                Entries.Remove(e);
            }
        }
        catch
        {
            // уборка записи — удобство
        }
    }

    private static bool SameOwner(WeakReference<Window>? prevOwner, Window? owner)
        => prevOwner is null || owner is null || (prevOwner.TryGetTarget(out var p) && ReferenceEquals(p, owner));

    /// <summary>Только для тестов (2026-11-19): забыть недавние открытия, чтобы диалог предыдущего сценария не считался «двойным нажатием».</summary>
    public static void ResetDuplicateGuardForTests()
    {
        Entries.Clear();
        lock (InFlight)
            InFlight.Clear();
    }

    // ---- Открытие «по ключу» (2026-11-21) ----

    private static readonly Dictionary<string, long> InFlight = new(StringComparer.Ordinal);
    private const double InFlightMaxSeconds = 600;

    /// <summary>2026-11-21: единый «замок открытия» для сценариев, где до показа окна идёт загрузка (чек с сервера, накладная): пока открытие
    /// с этим же ключом (например, <c>"receipt:" + id</c>) выполняется — от первого запроса до закрытия окна, — повторный запрос игнорируется
    /// (возвращает false). Замок снимается в finally: исключение, закрытие крестиком/Esc, закрытие владельца — ничего не залипает; «забытый» замок
    /// старше 10 минут не учитывается. Разные ключи (другой чек) не мешают друг другу. Ничего не ждёт — взаимной блокировки быть не может.</summary>
    public static async Task<bool> RunExclusiveAsync(string key, Func<Task> open)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (InFlight)
        {
            if (InFlight.TryGetValue(key, out var since)
                && (now - since) < InFlightMaxSeconds * System.Diagnostics.Stopwatch.Frequency)
            {
                NurMarketKassa.Services.PosLogger.Log($"Повторное открытие «{key}» (двойное нажатие) — игнорируется, первое ещё открыто.", "UI");
                return false;
            }
            InFlight[key] = now;
        }
        try
        {
            await open().ConfigureAwait(true);
            return true;
        }
        finally
        {
            lock (InFlight)
                InFlight.Remove(key);
        }
    }

    /// <summary>2026-11-21: тот же замок для синхронных мест (флаг без async): true — можно открывать, после закрытия вызвать <see cref="EndExclusive"/> (в finally).</summary>
    public static bool TryBeginExclusive(string key)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (InFlight)
        {
            if (InFlight.TryGetValue(key, out var since)
                && (now - since) < InFlightMaxSeconds * System.Diagnostics.Stopwatch.Frequency)
                return false;
            InFlight[key] = now;
            return true;
        }
    }

    public static void EndExclusive(string key)
    {
        lock (InFlight)
            InFlight.Remove(key);
    }

    public static Task<bool?> ShowAsync(Window dialog, Window? owner)
    {
        owner = ResolveOwner(owner);
        ApplyFadeIn(dialog);
        return ShowDialogDimmed<bool?>(dialog, owner);
    }

    /// <summary>2026-11-21: прямой <c>dialog.ShowDialog&lt;T&gt;(owner)</c> с защитой от двойного открытия (тот же тип и ключ у того же владельца — второе
    /// окно не показывается, результат по умолчанию = «отмена»). Для мест, где окно открывали в обход PosDialogHost (правка возвратов и т.п.).</summary>
    public static async Task<T?> ShowDialogOnce<T>(Window dialog, Window owner)
    {
        if (IsDuplicateOpen(dialog, owner))
            return default;
        try
        {
            return await dialog.ShowDialog<T>(owner).ConfigureAwait(true);
        }
        catch
        {
            ForgetOpen(dialog); // окно не открылось — запись «открывается» не должна залипнуть
            throw;
        }
    }

    /// <summary>То же без результата.</summary>
    public static async Task ShowDialogOnce(Window dialog, Window owner)
    {
        if (IsDuplicateOpen(dialog, owner))
            return;
        try
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        catch
        {
            ForgetOpen(dialog);
            throw;
        }
    }

    public static Task<T?> ShowDialogAsync<T>(Window owner, Window dialog) where T : class
    {
        ApplyFadeIn(dialog);
        return ShowDialogDimmed<T?>(dialog, owner);
    }
}
