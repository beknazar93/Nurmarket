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
/// </summary>
public static class SessionExpiredPrompt
{
    private static int _showing;

    /// <summary>Подписывает окно на событие; вернуть результат подписки нужно в Closed окна (Dispose).</summary>
    public static IDisposable Attach(Window window, Func<Task> signInAgain)
    {
        void OnExpired() => Dispatcher.UIThread.Post(() => _ = AskAsync(window, signInAgain), DispatcherPriority.Normal);
        SessionExpiryMonitor.Expired += OnExpired;
        return new Unsubscribe(() => SessionExpiryMonitor.Expired -= OnExpired);
    }

    /// <summary>Окно, поверх которого показать вопрос: активное (может быть диалог), иначе главное.</summary>
    private static Window? TopWindow(Window fallback) =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? fallback
            : fallback;

    public static async Task AskAsync(Window window, Func<Task> signInAgain)
    {
        // Окно уже открыто (или сессия за это время восстановлена) — второй раз не спрашиваем.
        if (!SessionExpiryMonitor.IsExpired || Interlocked.Exchange(ref _showing, 1) != 0)
            return;
        try
        {
            if (!window.IsVisible)
                return;
            var signIn = await PosConfirmDialog.ShowModalAsync(
                TopWindow(window),
                Tr.T("Сессия истекла", "Сессиянын мөөнөтү бүттү", "Session expired", "Oturum süresi doldu", "Sessiya muddati tugadi"),
                Tr.T("Сессия истекла. Войдите снова.\n\nПродажи, смена и корзины сохранены — после входа всё отправится на сервер само.",
                    "Сессиянын мөөнөтү бүттү. Кайра кириңиз.\n\nСатуулар, смена жана себеттер сакталды — кирген соң баары серверге өзү жөнөтүлөт.",
                    "Your session has expired. Please sign in again.\n\nSales, the shift and carts are saved — after you sign in everything is sent to the server automatically.",
                    "Oturum süresi doldu. Lütfen yeniden giriş yapın.\n\nSatışlar, vardiya ve sepetler kayıtlı — girişten sonra her şey sunucuya otomatik gönderilir.",
                    "Sessiya muddati tugadi. Qaytadan kiring.\n\nSotuvlar, smena va savatlar saqlangan — kirgandan keyin hammasi serverga o'zi yuboriladi."),
                confirmText: Tr.T("Войти", "Кирүү", "Sign in", "Giriş yap", "Kirish"),
                cancelText: Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq")).ConfigureAwait(true);
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
