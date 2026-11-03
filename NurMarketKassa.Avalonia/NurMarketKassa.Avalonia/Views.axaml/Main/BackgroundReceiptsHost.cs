using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels.Main;
using Microsoft.Extensions.DependencyInjection;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q5): запуск очереди приходов после входа (<see cref="Start"/>), общее уведомление о
/// завершении задачи (лёгкая плашка, не модальное окно) и предупреждение при выходе из программы (<see cref="OnShutdownRequested"/>).
/// Вызывается одной строкой из MainWindow / OwnerShellWindow (после смены данных аккаунта) и из App.axaml.cs (ShutdownRequested).</summary>
public static class BackgroundReceiptsHost
{
    private static bool _started;
    private static bool _exitConfirmed;
    private static bool _exitAsking;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>После входа и загрузки компании (касса и программа владельца): очередь читает файл и догружает незавершённые приходы.
    /// Финишер Закупок (<see cref="BackgroundReceivingQueue.RegisterFinisher"/>) регистрируют ДО этого вызова. Повторный вызов
    /// (смена кассира, повторный вход) только заново вызывает Resume.</summary>
    public static void Start()
    {
        try
        {
            // 2026-10-26 (Q4): финишер Закупок — до Resume (он уже регистрируется при загрузке сборки; вызов безвреден и страхует порядок).
            PurchasesWindow.RegisterBackgroundFinisher();
            var queue = BackgroundReceivingQueue.Instance;
            BackgroundReceivingQueue.ApiResolver ??= () => App.AppHost?.Services.GetService<NurMarketApiClient>();
            if (!_started)
            {
                _started = true;
                queue.JobFinished += job => Dispatcher.UIThread.Post(() => NotifyFinished(job));
            }
            var api = App.AppHost?.Services.GetService<NurMarketApiClient>();
            if (api is not null)
            {
                queue.Resume(api);
                BulkReceivingFeeder.Instance.Resume(); // 2026-10-26 (ТЗ «Склад — массовое добавление», М1): догрузить незаконченные пачки
            }
        }
        catch (Exception ex)
        {
            // Очередь не должна мешать входу: продажи важнее, ошибка — в журнал.
            PosLogger.Log($"Приходы в фоне: запуск очереди не удался: {ex}", "WARNING");
        }
    }

    // ------------------------------------------------------------------ уведомление о завершении

    private static void NotifyFinished(ReceivingJob job)
    {
        try
        {
            // 2026-10-26 (М1): готовая часть массовой пачки (сотня строк) — не повод для плашки: итог всей пачки показывает окно по BatchFinished.
            if (job.Source == ReceivingJobSource.Bulk && job.Status == ReceivingJobStatus.Done)
                return;
            // Окно-источник само ждёт задачу 0,5 с и показывает итог — быстрый конец не дублируем.
            if (job.FinishedAt is { } finished && finished - job.CreatedAt <= TimeSpan.FromMilliseconds(700))
                return;
            var ok = job.Status == ReceivingJobStatus.Done;
            var text = FinishedText(job);
            // Успех — лёгкая плашка под шапкой кассы; нет главного окна кассы (программа владельца) или ошибка —
            // своя всплывающая плашка (по нажатию открывает список «Приходы в фоне»).
            if (ok && MainStatusViewModel.TryShowInfoToast(text))
                return;
            ShowToast(text, error: !ok);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: уведомление не показано: {ex.Message}", "WARNING");
        }
    }

    internal static string FinishedText(ReceivingJob job)
    {
        var n = job.LineCount;
        var sum = MoneyText.Som(job.Total);
        switch (job.Status)
        {
            case ReceivingJobStatus.Done:
            {
                var supplier = job.Supplier is { Name.Length: > 0 } s ? s.Name : "";
                var text = T($"Приход оприходован: {n} поз. на {sum}", $"Кирүү кабыл алынды: {n} сап, {sum}", $"Receiving posted: {n} items, {sum}",
                    $"Mal kabul işlendi: {n} kalem, {sum}", $"Kirim qabul qilindi: {n} ta pozitsiya, {sum}");
                if (supplier.Length > 0)
                    text += T($" (поставщик «{supplier}»)", $" (жеткирүүчү «{supplier}»)", $" (supplier “{supplier}”)", $" (tedarikçi “{supplier}”)", $" (yetkazib beruvchi «{supplier}»)");
                // Текст про долг поставщику — последняя строка итога (SupplierDebtRecorder.ResultText).
                if (job.DebtDone && job.Summary is { Length: > 0 } summary)
                {
                    var last = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(last))
                        text += "\n" + last;
                }
                return text;
            }
            case ReceivingJobStatus.Partial:
                return T($"Приход оприходован частично: {job.PostedCount} из {n} — откройте «Приходы в фоне»",
                    $"Кирүү жарым-жартылай кабыл алынды: {job.PostedCount}/{n} — «Фондогу кирүүлөр» бөлүмүн ачыңыз",
                    $"Receiving posted partially: {job.PostedCount} of {n} — open “Background receivings”",
                    $"Mal kabul kısmen işlendi: {job.PostedCount}/{n} — “Arka plandaki mal kabulleri”ni açın",
                    $"Kirim qisman qabul qilindi: {n} tadan {job.PostedCount} tasi — «Fondagi kirimlar»ni oching");
            default:
                return T("Приход не оприходован — откройте «Приходы в фоне»",
                    "Кирүү кабыл алынган жок — «Фондогу кирүүлөр» бөлүмүн ачыңыз",
                    "The receiving was not posted — open “Background receivings”",
                    "Mal kabul işlenmedi — “Arka plandaki mal kabulleri”ni açın",
                    "Kirim qabul qilinmadi — «Fondagi kirimlar»ni oching");
        }
    }

    /// <summary>Лёгкая всплывающая плашка в правом нижнем углу рабочей области (не модальное окно, фокус не забирает).
    /// Исчезает сама через несколько секунд; нажатие открывает окно «Приходы в фоне».
    /// 2026-11-01 (Д-10): рисует общая плашка программы <see cref="SuccessToast"/> (раньше здесь была своя копия окна плашки).</summary>
    internal static void ShowToast(string text, bool error)
    {
        // 2026-11-02 (приёмка 1.17.73, п. 1): ошибка или частичный приход — плашка «залипающая» (не гаснет, пока не нажмут; нажатие открывает
        // «Приходы в фоне» — там подробности); успех гаснет через 7 с.
        SuccessToast.Show(text, error ? ToastKind.Error : ToastKind.Success, TimeSpan.FromSeconds(7), () =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            BackgroundReceiptsWindow.ShowOrActivate(owner is { IsVisible: true } ? owner : null);
        }, sticky: error);
    }

    // ------------------------------------------------------------------ выход из программы

    /// <summary>desktop.ShutdownRequested: пока в очереди есть незавершённые приходы (любой компании) — один раз спросить
    /// «Выйти» / «Подождать». Приходы не теряются при выходе: очередь на диске, они догрузятся при следующем запуске.
    /// «Подождать» остаётся в программе и открывает список «Приходы в фоне» (окно удерживает программу, пока приходы идут).
    /// Если вопрос задать не удалось — выход не задерживается.</summary>
    public static void OnShutdownRequested(IClassicDesktopStyleApplicationLifetime desktop, ShutdownRequestedEventArgs e)
    {
        try
        {
            var queue = BackgroundReceivingQueue.Instance;
            var left = queue.ActiveCountAll;
            if (left <= 0 || _exitConfirmed || _exitAsking)
            {
                queue.Shutdown();
                return;
            }
            // 2026-11-02 (живая проверка круга 3): при выходе владельца контейнер служб уже мог быть освобождён — GetService бросал
            // «Cannot access a disposed object» (WARNING в журнале). Берём службу только когда нужен вопрос, и освобождённый контейнер
            // — не ошибка: выходим, приходы на диске догрузятся при следующем запуске.
            IUserPrompts? prompts;
            try
            {
                prompts = App.AppHost?.Services.GetService<IUserPrompts>();
            }
            catch (ObjectDisposedException)
            {
                prompts = null;
            }
            if (prompts is null)
            {
                queue.Shutdown();
                return;
            }

            e.Cancel = true;
            _exitAsking = true;
            _ = AskExitAsync(desktop, prompts, left);
        }
        catch (ObjectDisposedException)
        {
            // Очередь/контейнер уже освобождены — программа и так выходит; это не ошибка.
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: проверка при выходе не удалась: {ex.Message}", "WARNING");
        }
    }

    private static async Task AskExitAsync(IClassicDesktopStyleApplicationLifetime desktop, IUserPrompts prompts, int left)
    {
        var exit = true;
        try
        {
            exit = await prompts.ConfirmAsync(
                T($"Есть приходы в фоне: {left} — они продолжатся при следующем запуске программы.",
                    $"Фондо кирүүлөр бар: {left} — программа кийинки жолу ачылганда уланат.",
                    $"There are background receivings: {left} — they will continue the next time the program starts.",
                    $"Arka planda mal kabul var: {left} — program bir sonraki açılışta devam edecek.",
                    $"Fonda kirimlar bor: {left} — dastur keyingi safar ochilganda davom etadi."),
                T("Выйти", "Чыгуу", "Exit", "Çık", "Chiqish"),
                T("Подождать", "Күтүү", "Wait", "Bekle", "Kutish")).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: вопрос при выходе не задан: {ex.Message}", "WARNING");
        }
        finally
        {
            _exitAsking = false;
        }

        if (exit)
        {
            _exitConfirmed = true;
            BackgroundReceivingQueue.Instance.Shutdown();
            desktop.Shutdown();
            return;
        }

        // «Подождать»: окно списка держит программу открытой и показывает ход приходов.
        // 2026-10-26 (приёмка, S-5): главное окно к этому моменту уже закрыто — в окне списка есть кнопка «Вернуться в программу».
        BackgroundReceiptsWindow.ShowOrActivate(null);
    }

    private static bool _returning;

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>Есть ли сейчас окно кассы / программы владельца / входа (его можно просто вывести на передний план).</summary>
    private static Window? FindShellWindow() =>
        Desktop?.Windows.FirstOrDefault(w => w.IsVisible && (w is NurMarketKassa.AvaloniaHost.Services.IMainShell || w is LoginWindow));

    /// <summary>2026-10-26 (приёмка, S-5): кнопка «Вернуться в программу» нужна, только когда окон программы нет (после «Подождать»
    /// главное окно уже закрыто) и вход выполнен — иначе открывать нечего или вернуться некуда.</summary>
    internal static bool CanReturnToProgram() =>
        !_returning && Desktop is not null && FindShellWindow() is null
        && !string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserId);

    /// <summary>Открыть главное окно заново тем же путём, что после входа (App.ResolveMainShell → InitializeApplicationAsync → Show).
    /// Если окно уже есть — только вывести его на передний план.</summary>
    internal static async Task ReturnToProgramAsync()
    {
        if (_returning)
            return;
        _returning = true;
        try
        {
            var desktop = Desktop;
            if (desktop is null)
                return;
            if (FindShellWindow() is { } existing)
            {
                existing.Activate();
                return;
            }
            if (string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.CurrentUserId))
                return;

            var window = App.ResolveMainShell();
            var shell = (NurMarketKassa.AvaloniaHost.Services.IMainShell)window;
            var canOpen = await shell.InitializeApplicationAsync(null, CancellationToken.None).ConfigureAwait(true);
            if (!canOpen)
            {
                window.Close();
                return;
            }

            desktop.MainWindow = window;
            shell.PlaceOnPrimaryScreen();
            window.Show();
            PosLogger.Log("Приходы в фоне: главное окно открыто заново кнопкой «Вернуться в программу».", "STOCK");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приходы в фоне: не удалось вернуться в программу: {ex}", "WARNING");
        }
        finally
        {
            _returning = false;
        }
    }
}
