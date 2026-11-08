using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>
/// 2026-11-08, решение владельца («рабочее время сотрудника»): кассир с включённым ограничением (Настройки → Сотрудники → карточка → «Рабочее время»)
/// входит и открывает смену только в своё время; за 10 минут до конца — предупреждение; в конце — мягкий вопрос; при бездействии (по умолчанию 60 мин,
/// настраивается, 0 — выключено) — выход из аккаунта. У всех остальных (владелец, администратор, кассир без ограничения — а это все по умолчанию)
/// ничего не меняется: правило null → монитор спит. Решения — в чистом WorkTimeMonitor (тесты), здесь только исполнение.
///
/// Что НЕ трогаем (решение): (1) оплату, печать и открытое окно — ни предупреждение-вопрос, ни выход не срабатывают, пока они идут (повтор на следующем такте);
/// (2) чек с товарами — при конце времени кассиру предлагают «Отложить чек и выйти» (тот же путь, что при истечении подписки: DeferCartAsync), без согласия
/// чек не прерывается; жёсткий выход только когда чека нет; автовыход при бездействии откладывает чек сам; (3) открытая смена никогда не закрывается сама
/// (только по «Закрыть смену» кассира) и остаётся открытой при выходе; (4) деньги, остатки и данные не затрагиваются, всё пишется в журнал (STAFF).
/// Часы ПК — местные; перевод часов не ломает работу (проверка каждый раз смотрит на текущее время; простой — из счётчика системы).
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _workTimeTimer;
    private WorkTimeMonitor? _workTime;
    private int _workTimeRunning;
    private bool _workRuleKnownAtEntry;

    /// <summary>Вход вне рабочего времени ограниченного кассира: понятное сообщение и экран входа (сохранённый вход остаётся — завтра утром касса откроется сама;
    /// чек и смена не теряются). true — можно работать.</summary>
    private async Task<bool> EnforceWorkTimeOnEntryAsync()
    {
        var rule = WorkSchedule.CurrentRule();
        _workRuleKnownAtEntry = rule is not null;
        var status = WorkSchedule.Evaluate(rule, DateTime.Now);
        if (status.Verdict != WorkTimeVerdict.Outside)
            return true;
        await RefuseOutsideWorkTimeAsync(rule!, status, "вход").ConfigureAwait(true);
        return false;
    }

    private async Task RefuseOutsideWorkTimeAsync(WorkRule rule, WorkTimeStatus status, string what)
    {
        PosLogger.Log($"Рабочее время: {what} отказан — вне рабочего времени ({WorkSchedule.Describe(rule)}).", "STAFF");
        _prompts.ShowWarning(WorkSchedule.OutsideRefusalText(rule, status.NextStart));
        await NavigateToLoginAsync(keepSavedLogin: true).ConfigureAwait(true);
    }

    /// <summary>Открытие смены вне рабочего времени ограниченного кассира — отказ с сообщением. true — можно.</summary>
    private bool AuthorizeWorkTimeForShift()
    {
        var rule = WorkSchedule.CurrentRule();
        var status = WorkSchedule.Evaluate(rule, DateTime.Now);
        if (status.Verdict != WorkTimeVerdict.Outside)
            return true;
        PosLogger.Log($"Рабочее время: открытие смены отказано — вне рабочего времени ({WorkSchedule.Describe(rule!)}).", "STAFF");
        _prompts.ShowWarning(WorkSchedule.OutsideRefusalText(rule!, status.NextStart));
        return false;
    }

    private void StartWorkTimeWatch()
    {
        if (_workTimeTimer is not null)
            return;
        _workTime = new WorkTimeMonitor(() => DateTime.Now, SyncCenter.InputIdle, WorkSchedule.CurrentRule, WorkSchedule.IdleLogoutMinutes);
        _workTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _workTimeTimer.Tick += (_, _) => _ = WorkTimeTickAsync();
        _workTimeTimer.Start();
        SettingsCloudSync.Applied += OnWorkTimeSettingsApplied;
    }

    private void StopWorkTimeWatch()
    {
        _workTimeTimer?.Stop();
        _workTimeTimer = null;
        SettingsCloudSync.Applied -= OnWorkTimeSettingsApplied;
    }

    /// <summary>Общие настройки компании пришли (первый вход на этом ПК: список сотрудников мог быть ещё неизвестен при входе). Правило появилось, а мы вне времени,
    /// и сейчас ни оплаты, ни окон, ни чека — тот же отказ, что при входе. Иначе (чек, окно) решает обычный монитор — мягко.</summary>
    private void OnWorkTimeSettingsApplied() => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            if (_workRuleKnownAtEntry || !IsVisible || _windowCts.IsCancellationRequested)
                return;
            var rule = WorkSchedule.CurrentRule();
            if (rule is null)
                return;
            _workRuleKnownAtEntry = true;
            var status = WorkSchedule.Evaluate(rule, DateTime.Now);
            if (status.Verdict == WorkTimeVerdict.Outside && !WorkTimeBusy() && !_viewModel.Basket.HasItems)
                await RefuseOutsideWorkTimeAsync(rule, status, "вход (правило пришло после входа)").ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Рабочее время: проверка после загрузки настроек не удалась: {ex.GetType().Name}", "STAFF");
        }
    });

    /// <summary>Нельзя прерывать: идёт оплата/печать (корзина занята), поверх кассы открыто окно (диалог, раздел) или окно не видно.</summary>
    private bool WorkTimeBusy() =>
        !IsVisible
        || _viewModel.Basket.IsBusy
        || OwnedWindows.Any(w => w.IsVisible && w.ShowActivated)
        || SessionExpiredPrompt.TopWindow(this) is { } top && !ReferenceEquals(top, this);

    private async Task WorkTimeTickAsync()
    {
        if (_workTime is null || _windowCts.IsCancellationRequested || Interlocked.Exchange(ref _workTimeRunning, 1) != 0)
            return;
        try
        {
            var action = _workTime.Tick(WorkTimeBusy(), _viewModel.Basket.HasItems, _session.IsShiftOpen);
            switch (action)
            {
                case WorkTimeAction.EndWarning when _workTime.LastRule is { } rule:
                    PosLogger.Log($"Рабочее время: предупреждение, до конца {_workTime.LastMinutesLeft} мин ({WorkSchedule.Describe(rule)}).", "STAFF");
                    _prompts.ShowToast(WorkSchedule.EndWarningText(_workTime.LastMinutesLeft, rule), true);
                    break;
                case WorkTimeAction.IdleWarning:
                    PosLogger.Log("Автовыход: предупреждение за минуту.", "STAFF");
                    _prompts.ShowToast(WorkSchedule.IdleWarningText(), true);
                    break;
                case WorkTimeAction.EndReached:
                    await AskEndOfWorkAsync().ConfigureAwait(true);
                    break;
                case WorkTimeAction.EndHardLogout:
                    PosLogger.Log($"Рабочее время закончилось: выход из аккаунта (чека нет{(_session.IsShiftOpen ? ", смена остаётся открытой" : "")}).", "STAFF");
                    await LogoutForWorkTimeAsync().ConfigureAwait(true);
                    break;
                case WorkTimeAction.IdleLogout:
                    PosLogger.Log($"Автовыход: нет действий {WorkSchedule.IdleLogoutMinutes()} мин — выход из аккаунта (чек откладывается, смена не закрывается).", "STAFF");
                    await LogoutForWorkTimeAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Рабочее время: проверка не выполнена: {ex.GetType().Name}: {ex.Message}", "STAFF");
        }
        finally
        {
            Volatile.Write(ref _workTimeRunning, 0);
        }
    }

    /// <summary>Рабочее время закончилось, а есть чек или открытая смена: мягкий вопрос. Отказ («Продолжить работу» / «Позже») — ничего не происходит, вопрос повторится через 5 минут.</summary>
    private async Task AskEndOfWorkAsync()
    {
        var rule = WorkSchedule.CurrentRule();
        if (rule is null)
            return;
        var title = Tr.T("Рабочее время закончилось", "Иш убактысы бүттү", "Working time is over", "Çalışma süresi bitti", "Ish vaqti tugadi");
        var hours = WorkSchedule.YourHoursText(rule);
        if (_viewModel.Basket.HasItems)
        {
            var defer = await PosConfirmDialog.ShowModalAsync(this, title,
                hours + "\n\n" + Tr.T("В чеке есть товары. Сохранить чек как отложенный и выйти из аккаунта? Смена останется открытой, чек можно будет продолжить.",
                    "Чекте товарлар бар. Чекти калтырылган катары сактап, аккаунттан чыгаарбызбы? Смена ачык калат, чекти улантууга болот.",
                    "The receipt has items. Save it as held and sign out? The shift stays open and the receipt can be continued.",
                    "Fişte ürünler var. Fiş bekletilen olarak kaydedilip hesaptan çıkılsın mı? Vardiya açık kalır, fiş devam ettirilebilir.",
                    "Chekda mahsulotlar bor. Chekni kechiktirilgan sifatida saqlab, akkauntdan chiqaylikmi? Smena ochiq qoladi, chekni davom ettirish mumkin."),
                Tr.T("Отложить чек и выйти", "Чекти калтырып чыгуу", "Hold receipt and sign out", "Fişi beklet ve çık", "Chekni kechiktirib chiqish"),
                Tr.T("Продолжить работу", "Иштөөнү улантуу", "Keep working", "Çalışmaya devam", "Ishni davom ettirish")).ConfigureAwait(true);
            PosLogger.Log(defer ? "Рабочее время закончилось: кассир откладывает чек и выходит." : "Рабочее время закончилось: кассир продолжает работу (чек не тронут).", "STAFF");
            if (defer)
                await LogoutForWorkTimeAsync().ConfigureAwait(true);
            return;
        }

        if (!_session.IsShiftOpen)
            return;
        var close = await PosConfirmDialog.ShowModalAsync(this, title,
            hours + "\n\n" + Tr.T("Смена ещё открыта. Закрыть смену и выйти? Если выйти без закрытия, смена останется открытой.",
                "Смена дагы эле ачык. Сменаны жаап, чыгабызбы? Жаппай чыксаңыз, смена ачык калат.",
                "The shift is still open. Close it and sign out? If you sign out without closing, the shift stays open.",
                "Vardiya hâlâ açık. Vardiya kapatılıp çıkılsın mı? Kapatmadan çıkarsanız vardiya açık kalır.",
                "Smena hali ochiq. Smenani yopib chiqaylikmi? Yopmasdan chiqsangiz, smena ochiq qoladi."),
            Tr.T("Закрыть смену", "Сменаны жабуу", "Close shift", "Vardiyayı kapat", "Smenani yopish"),
            Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq")).ConfigureAwait(true);
        PosLogger.Log(close ? "Рабочее время закончилось: кассир закрывает смену." : "Рабочее время закончилось: кассир ответил «Позже» (смена открыта).", "STAFF");
        if (!close)
            return;
        await CloseShiftAsync(confirmUnfinishedReceipt: false).ConfigureAwait(true);
        // Смена закрыта (кассир не отменил окно закрытия) — выходим; осталась открытой — не трогаем, вопрос повторится.
        if (!_session.IsShiftOpen)
            await LogoutForWorkTimeAsync().ConfigureAwait(true);
    }

    /// <summary>Выход по рабочему времени/бездействию: открытый чек — в «Отложенные» (как при истечении подписки), смену НЕ закрываем, затем экран входа
    /// (сессия снимается — это настоящий выход). Если отложить не вышло, чек всё равно сохраняется в состоянии кассы (NavigateToLoginAsync → SaveApplicationState).</summary>
    private async Task LogoutForWorkTimeAsync()
    {
        if (WorkTimeBusy())
            return; // между решением и выходом кто-то начал оплату/открыл окно — ничего не прерываем, монитор вернётся на следующем такте
        if (_viewModel.Basket.HasItems)
        {
            try
            {
                await _viewModel.Basket.DeferCartAsync().ConfigureAwait(true);
                PosLogger.Log("Выход по рабочему времени/бездействию: открытый чек отложен.", "STAFF");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Выход по рабочему времени/бездействию: чек не отложен ({ex.GetType().Name}), он сохранится в состоянии кассы.", "STAFF");
            }
        }
        await NavigateToLoginAsync().ConfigureAwait(true);
    }
}
