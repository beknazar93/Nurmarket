using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// 2026-11-06: мягкое окно «Через N ч вход истечёт. Войдите заново сейчас, чтобы кассу не остановило посреди смены» (сервер NurCRM: refresh-токен
/// живёт 7 суток от входа по паролю, ответ Умара). Показывается ОДИН раз за запуск при остатке ≤ 3 ч (решает RefreshExpiryWatcher), а также
/// при закрытии смены и выходе из кассы, если остаток ≤ 24 ч (<see cref="OfferAsync"/>). Не показывается, пока открыт чек с товарами или идёт оплата
/// (isBusy окна; watcher повторит через 5 минут), пока кассир вводит данные в форме или сессия уже потеряна (для этого есть SessionExpiredPrompt).
/// «Войти заново» — тот же путь, что «Войти» в SessionExpiredPrompt (выход на экран входа, данные кассы не трогаются); «Позже» — работаем дальше.
/// Общий код для кассы, программы владельца и Android (проект приложения подключается глобом).
/// </summary>
public static class RefreshExpiryPrompt
{
    private static int _showing;

    /// <summary>Подписывает окно на событие watcher'а; результат — в Closed окна (Dispose).</summary>
    /// <param name="isBusy">Сейчас нельзя прерывать (чек с товарами, оплата); вызывается в UI-потоке.</param>
    public static IDisposable Attach(Window window, Func<Task> signInAgain, Func<bool>? isBusy = null)
    {
        var watcher = RefreshExpiryWatcher.Current;
        bool Busy()
        {
            // Вызывается из потока таймера watcher'а — состояние окна читаем в UI-потоке.
            bool Probe() => !window.IsVisible
                            || SessionExpiryMonitor.IsExpired
                            || Volatile.Read(ref _showing) != 0
                            || ModalAnnouncements.BackgroundOpen // 2026-11-06 (Б-8): открыт фоновый анонс — вопрос придёт на следующем такте
                            || isBusy?.Invoke() == true
                            || SessionExpiredPrompt.IsUserBusy(window);
            return Dispatcher.UIThread.CheckAccess() ? Probe() : Dispatcher.UIThread.Invoke(Probe);
        }

        void OnPromptDue() => Dispatcher.UIThread.Post(() => _ = AskAsync(window, signInAgain), DispatcherPriority.Normal);
        Func<bool> busyFunc = Busy;
        watcher.IsBusy = busyFunc;
        watcher.PromptDue += OnPromptDue;
        return new Unsubscribe(() =>
        {
            watcher.PromptDue -= OnPromptDue;
            if (ReferenceEquals(watcher.IsBusy, busyFunc))
                watcher.IsBusy = null;
        });
    }

    private static async Task AskAsync(Window window, Func<Task> signInAgain)
    {
        try
        {
            await ShowAsync(window, signInAgain).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Окно «Вход скоро истечёт» не показано: {ex.GetType().Name}", "WARNING");
        }
    }

    /// <summary>Закрытие смены или выход из кассы: остаток ≤ 24 ч — предложить войти заново. true — кассир согласился и вход выполнен (вызывающий дальше не идёт).</summary>
    public static async Task<bool> OfferAsync(Window window, Func<Task> signInAgain)
    {
        try
        {
            if (!RefreshExpiryWatcher.Current.IsWarning)
                return false;
            return await ShowAsync(window, signInAgain).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Предложение «войти заново» не показано: {ex.GetType().Name}", "WARNING");
            return false;
        }
    }

    private static async Task<bool> ShowAsync(Window window, Func<Task> signInAgain)
    {
        var hours = RefreshExpiryWatcher.Current.HoursLeft;
        // Срок неизвестен или уже вышел, сессия потеряна (окно «Сессия истекла») либо вопрос уже на экране — не спрашиваем.
        if (hours <= 0 || SessionExpiryMonitor.IsExpired || Interlocked.Exchange(ref _showing, 1) != 0)
            return false;
        try
        {
            if (!window.IsVisible)
                return false;
            using var important = ModalAnnouncements.BeginImportant(); // 2026-11-06 (Б-8): пока окно открыто, фоновые анонсы ждут
            var signIn = await PosConfirmDialog.ShowModalAsync(
                SessionExpiredPrompt.TopWindow(window),
                RefreshExpiryWatcher.PromptTitle(),
                RefreshExpiryWatcher.PromptMessage(hours),
                confirmText: RefreshExpiryWatcher.PromptSignInText(),
                cancelText: Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq")).ConfigureAwait(true);
            if (!signIn)
                return false;
            await signInAgain().ConfigureAwait(true);
            return true;
        }
        finally
        {
            Volatile.Write(ref _showing, 0);
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
