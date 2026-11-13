using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// 2026-10-30: понятное окно «Сессия истекла. Войдите снова» вместо тупиковой «Ошибка закрытия смены: Сессия недействительна…».
/// Показывается ОДИН раз за потерю сессии (флаг в <see cref="SessionExpiryMonitor"/>). «Войти» — выход на экран входа тем же путём,
/// что и «Выйти» (данные кассы — очередь продаж, смена, корзины, pending_shift_closes.json — не трогаются: после входа тем же
/// аккаунтом они на месте и уходят на сервер сами; другая компания — защита UnsentDataDialog в AccountDataIsolation).
/// «Позже» — работа продолжается автономно, очередь ждёт; в шапке остаётся «🔑 Войдите снова».
/// 2026-11-06 (баг 3 журнала клиента): «Позже» больше не «навсегда» — пока сессия недействительна, вопрос повторяется раз в 10 минут
/// и сразу (не чаще раза в 2 минуты) при действии, упавшем из-за сессии (<see cref="SessionExpiryMonitor.Reminder"/>); таймер не лезет, пока кассир печатает.
/// </summary>
public static class SessionExpiredPrompt
{
    private static int _showing;

    /// <summary>2026-11-06 (Б-8): окно «Сессия истекла» сейчас открыто. Фоновые анонсы (подсказка про просрочку и т. п.) не открываются поверх него — две модалки сразу — а откладываются.</summary>
    public static bool IsShowing => Volatile.Read(ref _showing) != 0;

    /// <summary>Подписывает окно на событие; вернуть результат подписки нужно в Closed окна (Dispose).</summary>
    public static IDisposable Attach(Window window, Func<Task> signInAgain)
    {
        void OnExpired() => Dispatcher.UIThread.Post(() => _ = AskAsync(window, signInAgain), DispatcherPriority.Normal);
        void OnReminder(bool fromAction) => Dispatcher.UIThread.Post(() => _ = RemindAsync(window, signInAgain, fromAction), DispatcherPriority.Normal);
        SessionExpiryMonitor.Expired += OnExpired;
        SessionExpiryMonitor.Reminder += OnReminder;
        return new Unsubscribe(() =>
        {
            SessionExpiryMonitor.Expired -= OnExpired;
            SessionExpiryMonitor.Reminder -= OnReminder;
        });
    }

    private static async Task RemindAsync(Window window, Func<Task> signInAgain, bool fromAction)
    {
        // Повтор по таймеру не должен выскочить посреди набора (Enter кассира нажал бы «Войти» и закрыл окно с вводом): отложим на минуту.
        // После действия кассира (он только что нажал кнопку, а она упала из-за сессии) вопрос показываем сразу.
        try
        {
            if (!fromAction && IsUserBusy(window))
            {
                SessionExpiryMonitor.DeferReminder();
                return;
            }
            await AskAsync(window, signInAgain).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Напоминание «Сессия истекла» не показано: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Кассир сейчас вводит: поверх окна открыт другой диалог (форма) или фокус в поле с текстом.</summary>
    internal static bool IsUserBusy(Window window)
    {
        var top = TopWindow(window);
        if (top is null)
            return false;
        if (!ReferenceEquals(top, window))
            return true;
        return TopLevel.GetTopLevel(top)?.FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false, Text.Length: > 0 }
               || UnsavedInputTracker.IsDirty(top);
    }

    /// <summary>Окно, поверх которого показать вопрос: активное (может быть диалог), иначе главное.</summary>
    internal static Window? TopWindow(Window fallback) =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? fallback
            : fallback;

    public static async Task AskAsync(Window window, Func<Task> signInAgain)
    {
        // Окно уже открыто (или сессия за это время восстановлена) — второй раз не спрашиваем.
        if (!SessionExpiryMonitor.IsExpired)
            return;
        // 2026-11-06 (Б-8): открыт фоновый анонс («Просрочка при продаже» и т. п.) — две модалки сразу не показываем: вопрос откладывается на несколько секунд (не теряется) и появится сразу после закрытия анонса.
        if (ModalAnnouncements.BackgroundOpen)
        {
            DispatcherTimer.RunOnce(() => _ = AskAsync(window, signInAgain), ModalAnnouncements.PollInterval);
            return;
        }
        if (Interlocked.Exchange(ref _showing, 1) != 0)
            return;
        try
        {
            if (!window.IsVisible)
                return;
            using var important = ModalAnnouncements.BeginImportant(); // 2026-11-06 (Б-8): пока окно открыто, фоновые анонсы ждут
            SessionExpiryMonitor.NoteReminderShown(); // 2026-11-06: следующее напоминание — через 10 минут после ЭТОГО вопроса
            // 2026-11-06: честная причина — срок входа (7 суток от входа по паролю), а не «чужой вход».
            // 2026-11-13 (ответ Умара, п. 5): вход закрыт, потому что логин открыли на другом устройстве (401 session_revoked) — своё окно с именем устройства и временем. Кнопка «Войти снова» — экран входа.
            if (SessionExpiryMonitor.Revoked is { } revoked)
            {
                var again = await PosConfirmDialog.ShowModalAsync(TopWindow(window), NurMarketKassa.ViewModels.DeviceSlotPrompt.RevokedTitle,
                    NurMarketKassa.ViewModels.DeviceSlotPrompt.RevokedMessage(revoked.ByDeviceName, revoked.At),
                    confirmText: NurMarketKassa.ViewModels.DeviceSlotPrompt.RevokedSignIn,
                    cancelText: NurMarketKassa.ViewModels.DeviceSlotPrompt.RevokedLater).ConfigureAwait(true);
                SessionExpiryMonitor.NoteReminderShown();
                if (again)
                    await signInAgain().ConfigureAwait(true);
                return;
            }
            var signIn = await PosConfirmDialog.ShowModalAsync(
                TopWindow(window),
                Tr.T("Сессия истекла", "Сессиянын мөөнөтү бүттү", "Session expired", "Oturum süresi doldu", "Sessiya muddati tugadi"),
                Tr.T("Срок входа истёк. Войдите заново.\n\nПродажи, смена и корзины сохранены — после входа всё отправится на сервер само.",
                    "Кирүүнүн мөөнөтү бүттү. Кайра кириңиз.\n\nСатуулар, смена жана себеттер сакталды — кирген соң баары серверге өзү жөнөтүлөт.",
                    "Your sign-in has expired. Please sign in again.\n\nSales, the shift and carts are saved — after you sign in everything is sent to the server automatically.",
                    "Oturumun süresi doldu. Lütfen yeniden giriş yapın.\n\nSatışlar, vardiya ve sepetler kayıtlı — girişten sonra her şey sunucuya otomatik gönderilir.",
                    "Kirish muddati tugadi. Qaytadan kiring.\n\nSotuvlar, smena va savatlar saqlangan — kirgandan keyin hammasi serverga o'zi yuboriladi."),
                confirmText: Tr.T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish"),
                cancelText: Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq")).ConfigureAwait(true);
            SessionExpiryMonitor.NoteReminderShown(); // 2026-11-06: отсчёт от закрытия окна («Позже» = ещё 10 минут)
            if (signIn)
                await signInAgain().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Сессия истекла» не показано: {ex.Message}", "WARNING");
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
