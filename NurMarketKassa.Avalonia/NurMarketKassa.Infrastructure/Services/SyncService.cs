using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Фоновая синхронизация офлайн-продаж и каталога (интервал 45 с).</summary>
public sealed partial class SyncService : IDisposable
{
    // 2026-10-29, «синхронизация очень медленная»: проход очереди и связи раз в 15 с (было 45; 30 вместо 90 на слабом ПК), чек, попавший в очередь,
    // уходит сразу (OnQueueAppended). Запрос связи общий (SharedConnectivityCheck: только если минуту не было ответов сервера), очередь — локальная база.
    private static TimeSpan SyncInterval => SyncCenter.QueueInterval;

    // Полная синхронизация каталога (2026-09-07): раз в 2 минуты (3 — в режиме слабого ПК), отдельно
    // от 45-секундного цикла проверки связи и отправки офлайн-продаж. Серверного признака версии
    // каталога нет (catalog-meta/version отвечают 404, ETag не отдаётся), а каждый sync — полная
    // загрузка каталога + diff в SQLite, поэтому чаще нет смысла. Результат sync'а теперь доходит
    // до экрана кассира (CatalogCacheService.CatalogChanged), а после своей продажи каталог
    // обновляется из локальной базы без сети (CatalogPanelViewModel.RepublishFromLocalAsync).
    private static TimeSpan CatalogSyncInterval =>
        UserPreferences.Instance.LowPerformanceMode ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(2);

    private DateTime _lastCatalogSyncUtc = DateTime.MinValue;

    // На слабых устройствах (см. UserPreferences.LowPerformanceMode) реже гоняем
    // полную синхронизацию каталога, чтобы не конкурировать с UI-потоком за CPU/диск.
    private static TimeSpan CurrentSyncInterval =>
        UserPreferences.Instance.LowPerformanceMode ? SyncInterval * 2 : SyncInterval;

    private readonly ISalesApiService _sales;
    private readonly IAuthApiService _auth;
    private readonly ISyncConflictResolver _syncConflictResolver;
    private readonly ICatalogCacheService _catalogCache;
    private readonly NurMarketKassa.Core.Contracts.IAutonomousAuthService _autonomous;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    /// <summary>Догрузка истории продаж с сервера: первый проход сразу, дальше раз в полчаса.
    /// Чаще незачем — это не продажи текущей кассы, а чужие чеки для аналитики.</summary>
    private static readonly TimeSpan HistoryBackfillInterval = TimeSpan.FromMinutes(30);
    private DateTime _lastHistoryBackfillUtc = DateTime.MinValue;
    private bool _disposed;

    public SyncService(
        ISalesApiService sales,
        IAuthApiService auth,
        ISyncConflictResolver syncConflictResolver,
        ICatalogCacheService catalogCache,
        IShiftApiService shiftApi,
        NurMarketKassa.Core.Contracts.IAutonomousAuthService autonomous)
    {
        _autonomous = autonomous;
        _sales = sales;
        _auth = auth;
        _syncConflictResolver = syncConflictResolver;
        _catalogCache = catalogCache;
        _shiftApi = shiftApi;
    }

    private readonly IShiftApiService _shiftApi;

    private static readonly HashSet<string> WarnedForeignShifts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-29 (Б-1): момент создания службы — отсчёт паузы автоповтора после запуска кассы.</summary>
    private readonly DateTime _createdUtc = DateTime.UtcNow;

    /// <summary>Дожимает смены, закрытые на кассе, но не принятые сервером (см.
    /// PendingShiftCloseStore). Вызывается при каждом проходе синхронизации: как только сервер
    /// перестанет отклонять закрытие, смены закроются сами, без участия кассира.</summary>
    private async Task FlushPendingShiftClosesAsync(CancellationToken ct)
    {
        var pending = PendingShiftCloseStore.LoadAll();
        if (pending.Count == 0)
            return;

        // 2026-09-29: смены, чьи продажи ещё в очереди, не закрываем — ждём досылки чеков.
        var shiftsWithQueuedSales = OfflinePendingSalesStore.LoadPendingForSync()
            .Select(e => e.ShiftId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested)
                return;

            if (shiftsWithQueuedSales.Contains(entry.ShiftId))
                continue;

            // 2026-10-30 (приёмка 1.17.65, «Важно»): отложенное закрытие смены привязано к компании. Вошли в другую компанию — чужую смену фоном не закрываем
            // (запись остаётся и сработает после входа в ту компанию); кассиру/в журнал — предупреждение, один раз за запуск.
            var currentCompanyId = CompanyInfoService.LastCompany?.Id;
            if (!string.IsNullOrEmpty(entry.CompanyId) && !string.IsNullOrEmpty(currentCompanyId)
                && !string.Equals(entry.CompanyId, currentCompanyId, StringComparison.OrdinalIgnoreCase))
            {
                if (WarnedForeignShifts.Add(entry.ShiftId))
                {
                    PosLogger.Log($"Очередь закрытий: смена {entry.ShiftId} закрыта на кассе в другой компании ({entry.CompanyId}), сейчас вход в {currentCompanyId} — фоном не закрываю, дождётся входа в ту компанию.", "WARNING");
                    PendingShiftCloseStore.RecordFailure(entry.ShiftId, Tr.T(
                        "Смена другой компании — войдите в ту компанию, и она закроется на сервере сама.",
                        "Башка компаниянын сменасы — ошол компанияга кириңиз, ал серверде өзү жабылат.",
                        "This shift belongs to another company — sign in to that company and it will close on the server by itself.",
                        "Bu vardiya başka bir şirkete ait — o şirkete giriş yapın, sunucuda kendiliğinden kapanır.",
                        "Bu smena boshqa kompaniyaga tegishli — o'sha kompaniyaga kiring, u serverda o'zi yopiladi."));
                }
                continue;
            }

            // 2026-10-27 (аудит кассы, В-7): смена из очереди сейчас — ТЕКУЩАЯ (сервер отдал её при «открыть смену» или касса
            // подхватила открытую смену при входе). Закрыть её фоном значило закрыть смену кассира посреди работы
            // («Смена не открыта»). Запись остаётся: обычное закрытие смены снимет её (CashShiftService.CloseShiftAsync).
            if (string.Equals(entry.ShiftId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase))
            {
                PosLogger.Log($"Очередь закрытий: смена {entry.ShiftId} сейчас активна на кассе — фоном не закрываю.", "SHIFT");
                continue;
            }

            // 2026-10-27 (Н-14): тот же POST, что у живого закрытия, — с повтором и округлением сумм («не более 2 цифр после запятой»).
            // Раньше очередь повторяла голый POST и получала тот же отказ при каждой попытке.
            var (outcome, error) = await ShiftCloseRetry.CloseAsync(_shiftApi, entry.ShiftId, entry.ClosingCash, ct).ConfigureAwait(false);
            if (outcome is ShiftCloseOutcome.Closed or ShiftCloseOutcome.AlreadyClosed)
            {
                // AlreadyClosed: смену закрыли где-то ещё (например, в веб-панели) — очередь об этом не знает.
                PendingShiftCloseStore.Remove(entry.ShiftId);
            }
            else
            {
                // Сервер всё ещё не принимает — пробуем в следующий раз, в журнал пишем
                // только смену попыток, чтобы не засорять его одной и той же строкой.
                PendingShiftCloseStore.RecordFailure(entry.ShiftId, error);
            }
        }
    }

    public event EventHandler? StateChanged;

    public bool IsOnline { get; private set; }

    public bool IsSyncInProgress { get; private set; }

    public string StatusText { get; private set; } = Tr.T("Проверка связи…", "Байланыш текшерилүүдө…", "Checking connection…",
        "Bağlantı kontrol ediliyor…", "Aloqa tekshirilmoqda…");

    public void Start()
    {
        if (_loopTask != null)
            return;

        foreach (var entry in OfflinePendingSalesStore.LoadAll())
        {
            if (!string.Equals(entry.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase))
                continue;

            OfflinePendingSalesStore.MarkFailed(entry.Id, "Синхронизация была прервана перезапуском кассы.", retryable: true);
        }

        // 2026-09-29: сервер ожил после аварии — очередь досылается сразу, а не через 45 с.
        ServerOutageMonitor.Recovered += OnServerRecovered;
        SessionExpiryMonitor.Changed += OnSessionChanged; // 2026-10-30
        // 2026-10-29: чек попал в очередь — досылаем через несколько секунд, а не на следующем такте; «Обновить» (SyncCenter) тоже досылает сразу.
        OfflinePendingSalesStore.Appended += OnQueueAppended;
        OfflineOperationsStore.Appended += OnQueueAppended; // 2026-10-29: возврат/оплата долга без связи — тоже сразу, если связь есть
        SyncCenter.QueueFlusher = async ct =>
        {
            await ProbeNowAsync(ct).ConfigureAwait(false);
            if (IsOnline)
                await SyncPendingAsync(ct, waitForGate: true).ConfigureAwait(false);
        };
        _loopTask = RunLoopAsync(_cts.Token);
    }

    private void OnQueueAppended()
    {
        if (_disposed || _autonomous.IsCurrentSessionAutonomous || ServerOutageMonitor.IsOutage)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                // 5 с: исходная оплата, оборвавшаяся по таймауту, могла дойти до сервера — ReplayOfflineSaleAsync сверяет корзину по ключу.
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                if (IsOnline && !ServerOutageMonitor.IsOutage)
                    await SyncPendingAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Досылка очереди после продажи не удалась: {ex.Message}", "OFFLINE");
            }
        });
    }

    public async Task ProbeNowAsync(CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested)
            return;

        // 2026-09-29: в аварии сервера (ServerOutageMonitor) связь проверяет только монитор — с
        // нарастающей паузой. Этот цикл (раз в 45 с) сервер не дёргает и продажи не досылает:
        // очередь уйдёт сразу по событию Recovered.
        if (ServerOutageMonitor.IsOutage)
        {
            IsOnline = false;
        }
        else
        {
            // 2026-10-04, отчёт о производительности (п. 6): одна общая проверка с шапкой кассы
            // (SharedConnectivityCheck) — и только если за минуту не было ни одного удачного ответа
            // сервера. Раньше здесь был свой GET / каждые 45 с, независимо от шапки (каждые 20 с).
            IsOnline = await SharedConnectivityCheck.CheckAsync(_auth.CanReachApiAsync, "SyncService", ct).ConfigureAwait(false);
        }

        LeaveOfflineModeIfBackOnline();
        UpdateStatusText();
        RaiseStateChanged();
    }

    /// <summary>Сколько офлайн-чеков проведено на сервере за время работы (для «отправлено N»).</summary>
    private int _replayedCount;

    /// <summary>2026-09-29: сервер снова отвечает после аварии — досылаем очередь (продажи, затем
    /// закрытия смен, внесения/изъятия — их дожимает окно кассы) и сообщаем итог кассиру через
    /// ServerOutageMonitor.NotifyQueueFlushed. Фоном: событие приходит из сетевого потока.</summary>
    /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): вошли заново (флаг «сессия недействительна» снят) — очередь чеков, операций и закрытий смен досылается сразу,
    /// а не через такт. Пока сессия недействительна, проход SyncPendingAsync ничего не отправляет и ничего не помечает ошибкой (запросы без токена бессмысленны),
    /// а новые чеки и операции продолжают копиться в очереди.</summary>
    private void OnSessionChanged()
    {
        if (_disposed || SessionExpiryMonitor.IsExpired || _autonomous.IsCurrentSessionAutonomous)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeNowAsync(_cts.Token).ConfigureAwait(false);
                if (IsOnline)
                    await SyncPendingAsync(_cts.Token, waitForGate: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Досылка очереди после повторного входа не завершена: {ex.Message}", "WARNING");
            }
        });
    }

    private void OnServerRecovered()
    {
        if (_disposed || _autonomous.IsCurrentSessionAutonomous)
            return;

        _ = Task.Run(async () =>
        {
            var startCount = Volatile.Read(ref _replayedCount);
            try
            {
                await ProbeNowAsync(_cts.Token).ConfigureAwait(false);
                if (IsOnline)
                    await SyncPendingAsync(_cts.Token, waitForGate: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Досылка очереди после восстановления связи не завершена: {ex.Message}", "OUTAGE WARNING");
            }

            var sent = Volatile.Read(ref _replayedCount) - startCount;
            ServerOutageMonitor.NotifyQueueFlushed(Math.Max(0, sent), OfflinePendingSalesStore.PendingCount);
        });
    }

    /// <summary>Снимает офлайн-режим, когда связь с сервером подтвердилась.
    ///
    /// PosApp.IsOfflineBootstrap выставлялся ОДИН раз — на экране входа — и больше не
    /// сбрасывался никогда. Если кассир вошёл, пока интернета не было, касса до конца сессии
    /// продавала «в офлайн»: каждый чек ложился в очередь, хотя связь давно вернулась, а в
    /// шапке при этом горело «Онлайн» (её рисует IsOnline, который проверяется постоянно).
    /// Именно это владелец и видел: «постоянно в офлайне, но показывает, что подключено».
    ///
    /// Обратно в офлайн по неудачной проверке НЕ переводим: разовый сбой пробы ещё не значит,
    /// что сервер недоступен, а продажа и так уйдёт в очередь сама, если запрос не пройдёт.
    /// Автономный режим (работа вообще без NurCRM) не трогаем — там офлайн не временный.</summary>
    private void LeaveOfflineModeIfBackOnline()
    {
        if (!PosApp.IsOfflineBootstrap)
            return;

        if (_autonomous.IsCurrentSessionAutonomous)
            return;

        PosApp.IsOfflineBootstrap = false;
        PosApp.OfflineBootstrapMessage = null;
        PosLogger.Log("Связь с сервером восстановлена — касса вышла из офлайн-режима.", "OFFLINE");
    }

    /// <summary>Смена аккаунта: каталог нового нужен сразу, а не через интервал, отсчитанный от
    /// последней загрузки каталога прежнего аккаунта (в логе — 80 с пустого склада).</summary>
    public void RequestCatalogSyncNow()
    {
        _lastCatalogSyncUtc = DateTime.MinValue;
        _ = Task.Run(async () =>
        {
            try
            {
                await ProbeNowAsync().ConfigureAwait(false);
                if (IsOnline)
                    await SyncPendingAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Каталог после смены аккаунта не загружен сразу: {ex.Message}", "CATALOG");
            }
        });
    }

    public async Task TriggerSyncNowAsync(CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested)
            return;

        await SyncPendingAsync(ct).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CurrentSyncInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeNowAsync(ct).ConfigureAwait(false);
                if (IsOnline)
                    await SyncPendingAsync(ct).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusText = Tr.T("Синхронизация: ", "Шайкештирүү: ", "Sync: ", "Senkronizasyon: ", "Sinxronlash: ") + ex.Message;
                RaiseStateChanged();
                try
                {
                    await Task.Delay(CurrentSyncInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SyncPendingAsync(CancellationToken ct, bool waitForGate = false)
    {
        if (_disposed || ct.IsCancellationRequested)
            return;

        // 2026-10-30: сессия недействительна — очередь (продажи, закрытия смен, возвраты) ждёт повторного входа: запросы без токена
        // только засоряли бы журнал и могли бы пометить чеки отклонёнными. После входа флаг снимается, и всё уходит само.
        if (SessionExpiryMonitor.IsExpired)
            return;

        // waitForGate — досылка после аварии: дождаться идущего прохода, а не пропустить свой.
        if (waitForGate)
            await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            IsSyncInProgress = true;
            UpdateStatusText();
            RaiseStateChanged();

            // 2026-10-29: «Ошибка отправки» больше не тупик — до трёх автоповторов с паузами (см. OfflinePendingSalesStore.AutoRequeueFailed).
            // 2026-10-29 (приёмка 1.17.65, Б-1): первые 2 минуты после запуска автоповтор не включаем — старт не должен сразу садиться на долгий повтор.
            if (IsOnline && DateTime.UtcNow - _createdUtc >= ReplayErrorPolicy.AutoRetryStartupGrace)
            {
                try { OfflinePendingSalesStore.AutoRequeueFailed(MaxUnknownStateDeferrals); }
                catch (Exception ex) { PosLogger.Log($"OFFLINE: автоповтор отклонённых чеков не отработал: {ex.Message}", "WARNING"); }
            }

            var pending = OfflinePendingSalesStore.LoadPendingForSync();

            // Без этой записи застрявшую очередь невозможно разобрать по журналу: у чека,
            // который ни разу не пытались отправить, LastError пуст, и в окне «Некорректные
            // чеки» он выглядит так же, как только что поставленный в очередь. Пишем только
            // когда очередь не пуста — иначе журнал заполнится строками ни о чём.
            if (pending.Count > 0)
            {
                PosLogger.Log(
                    $"OFFLINE очередь: {pending.Count} чек(ов), связь {(IsOnline ? "есть" : "нет")}"
                    + (IsOnline ? ", отправляю" : ", жду связи"),
                    "OFFLINE");
            }

            if (IsOnline && pending.Count > 0)
                await SyncBatchAsync(pending, ct).ConfigureAwait(false);

            // 2026-10-29: возвраты и оплаты долга, оформленные без связи, — ПОСЛЕ чеков очереди (возврат по чеку из очереди ждёт его отправки).
            if (IsOnline && !ServerOutageMonitor.IsOutage)
                await ReplayOfflineOperationsAsync(ct).ConfigureAwait(false);
            if (IsOnline)
                SyncCenter.RecordSuccess("queue"); // 2026-10-29: «Синхронизация: HH:mm:ss» в подсказке индикатора связи

            // 2026-09-29: закрытия смен — ПОСЛЕ продаж (раньше шли первыми). Смена, закрытая в
            // аварии сервера, закрывается на сервере только когда её чеки уже там: закрой её
            // раньше — досылка чеков этой смены получила бы отказ «смена закрыта».
            if (IsOnline && !ServerOutageMonitor.IsOutage)
                await FlushPendingShiftClosesAsync(ct).ConfigureAwait(false);

            // 2026-10-29: клиенты и долги — в кэш кассы для «в долг» и оплаты долга без связи (раз в 30 / 5 минут, когда связь есть).
            if (IsOnline && !ServerOutageMonitor.IsOutage)
                await OfflineClientCache.RefreshIfStaleAsync(ct).ConfigureAwait(false);

            // 2026-10-27 (аудит кассы, С-3): предоплаты долга, не доехавшие до сервера (касса закрыта сразу после продажи) — довносим.
            if (IsOnline && !ServerOutageMonitor.IsOutage)
                await PosCheckoutService.ResumePendingDebtPrepaymentsAsync(_sales, ct).ConfigureAwait(false);

            // История продаж других касс этого же аккаунта. Раньше её добирал раздел ABC при
            // открытии — и на кассе с пустой историей окно висело минутами. Здесь это фоновая
            // работа: порциями, никто не ждёт.
            if (IsOnline && DateTime.UtcNow - _lastHistoryBackfillUtc >= HistoryBackfillInterval)
            {
                _lastHistoryBackfillUtc = DateTime.UtcNow;
                try
                {
                    var added = await SalesHistoryBackfillHook.RunAsync(ct).ConfigureAwait(false);
                    if (added > 0)
                        PosDataEvents.RaiseSalesChanged();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"История продаж не догружена: {ex.Message}", "WARNING");
                }
            }

            if (IsOnline && DateTime.UtcNow - _lastCatalogSyncUtc >= CatalogSyncInterval)
            {
                try
                {
                    // Must go through the same ICatalogCacheService instance the UI catalog
                    // reads from — the static CatalogCacheService.Products list used to be
                    // refreshed independently here, creating a second set of product-tile
                    // instances the UI never saw. Any stock decrement StockSyncService then
                    // applied (via CatalogCacheService.Products) landed on those orphaned
                    // tiles instead of the ones bound to the screen, so a sale's stock
                    // decrement silently never appeared until the next full app restart.
                    // 2026-10-04, отчёт о производительности (п. 5): раз в 2 мин — только проверка «версии»
                    // каталога (1 запрос); весь каталог — при её изменении и раз в 15 мин (остатки).
                    await _catalogCache.SyncCatalogIfChangedAsync(ct).ConfigureAwait(false);
                    _lastCatalogSyncUtc = DateTime.UtcNow;
                }
                catch
                {
                    /* каталог обновится при следующем цикле */
                }
            }
        }
        finally
        {
            IsSyncInProgress = false;
            UpdateStatusText();
            RaiseStateChanged();
            _syncGate.Release();
        }
    }

    /// <summary>2026-10-29 (аварийный аудит кассы, п. 4): сколько проходов подряд сервер может не ответить «прошла ли оплата»,
    /// прежде чем чек уходит в «Требует проверки» (проход раз в 45 с — около 15 минут).</summary>
    private const int MaxUnknownStateDeferrals = 20;

    private async Task SyncBatchAsync(IReadOnlyList<OfflineSaleEntry> pending, CancellationToken ct)
    {
        var stopBatch = false;
        var noCauseFailures = 0;
        // 2026-10-06, ТЗ ч.12, п. 3.6: сначала — пакетом (до 50 чеков одним запросом, SyncService.BatchReplay.cs);
        // что пакет не провёл ясно — по одному, как раньше.
        var (stopAll, batchSkip) = await TryBatchReplayAsync(pending, ct).ConfigureAwait(false);
        if (stopAll)
            return;
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            // Проведён пакетом или ждёт повтора пакетом (тем же ключом) — по одному его слать нельзя.
            if (batchSkip.Contains(entry.Id) || (entry.BatchCheckoutAttempted && !_batchMissing))
                continue;
            // 2026-09-29: сервер снова лёг посреди досылки — остальные чеки ждут восстановления,
            // а не бьются в него по очереди (раньше на 5xx цикл шёл дальше по всему списку).
            if (!IsOnline || ServerOutageMonitor.IsOutage)
                break;

            OfflinePendingSalesStore.MarkSyncing(entry.Id);
            UpdateStatusText();
            RaiseStateChanged();

            try
            {
                var saleId = await ReplayOfflineSaleAsync(entry, ct).ConfigureAwait(false);
                PosLogger.Log($"OFFLINE replay: чек {entry.Id} проведён на сервере, продажа {saleId ?? "—"}.", "OFFLINE");
                OfflinePendingSalesStore.MarkSynced(entry.Id, saleId);
                OfflinePendingSalesStore.RemoveSynced(entry.Id);
                Interlocked.Increment(ref _replayedCount);
                // Соседи перестают вычитать этот чек из остатка, как только увидят его на сервере.
                Lan.LanJournal.PublishUploaded(entry.Id, saleId);
            }
            catch (ReplayDeferredException ex)
            {
                // 2026-10-04: checkout этой корзины отправлен недавно — сервер мог его ещё не довести.
                // Чек ждёт в очереди, следующие досылаются (SyncService.LegacySettle.cs).
                // 2026-10-29 (аварийный аудит кассы, п. 4): слишком много отложений — «Требует проверки» (failed), очередь идёт дальше.
                OfflinePendingSalesStore.MarkFailed(entry.Id, ex.Message, retryable: !ex.NeedsReview);
                PosLogger.Log($"OFFLINE replay: чек {entry.Id} {(ex.NeedsReview ? "требует проверки" : "отложен")} — {ex.Message}",
                    ex.NeedsReview ? "WARNING" : "OFFLINE");
                if (ex.NeedsReview)
                    NotifyOwnerAboutRejected(entry, ex.Message);
            }
            catch (HttpRequestException ex) when (ex.InnerException is null && ++noCauseFailures < 3)
            {
                // 2026-10-29 (п. 4): ошибка HTTP без причины от сокета/TLS (не обрыв сети) — это не «Офлайн»: чек остаётся в очереди,
                // следующие досылаются дальше. Три таких подряд — похоже на сбой сервера, прекращаем проход.
                // 2026-10-29: в карточке чека — причина простыми словами («нет соединения», «таймаут»), а не английский текст .NET.
                OfflinePendingSalesStore.MarkFailed(entry.Id, ServerOutageMonitor.Describe(ex), retryable: true);
                PosLogger.Log($"OFFLINE replay: чек {entry.Id} — ошибка запроса без обрыва связи, пропускаю: {ex.Message}", "OFFLINE");
            }
            catch (HttpRequestException ex)
            {
                // 2026-10-29: «Офлайн» ставим только при настоящем обрыве (есть причина от сокета/TLS); иначе (3 сбоя подряд) просто стоп прохода.
                if (ex.InnerException is not null)
                    IsOnline = false;
                OfflinePendingSalesStore.MarkFailed(entry.Id, ServerOutageMonitor.Describe(ex), retryable: true);
                // 2026-10-04: досылка не дошла до сервера — это авария, а не «единичный сбой»: следующие
                // оплаты сразу идут в очередь, без ожидания сервера (раньше монитор оставался
                // «Онлайн/Degraded», и следующий чек снова ждал 1,8 с). Только настоящий обрыв (с
                // причиной от сокета/TLS): «не удалось сверить корзину» из ReplayOfflineSaleAsync —
                // не авария.
                if (ex.InnerException is not null)
                    ServerOutageMonitor.ReportFailure(ReplayContext, ex, hard: true);
                break;
            }
            catch (TaskCanceledException ex)
            {
                IsOnline = false;
                OfflinePendingSalesStore.MarkFailed(entry.Id, ServerOutageMonitor.Describe(ex), retryable: true);
                if (!ct.IsCancellationRequested)
                    ServerOutageMonitor.ReportFailure(ReplayContext, ex, hard: true);
                break;
            }
            catch (ApiException ex)
            {
                // Раньше ЛЮБАЯ ошибка сервера означала "failed навсегда": ничто в приложении не
                // возвращает запись из failed обратно в очередь, то есть деньги взяты, а продажа
                // в NurCRM не попадает никогда. Временные сбои (5xx, таймаут шлюза, «слишком
                // много запросов») к этому не относятся — их надо повторить. Отказ по существу
                // (4xx: нет товара, неверные данные) повтором не лечится и остаётся failed.
                // 2026-10-04: 409/423/425 («запрос с этим ключом ещё выполняется») — тоже временный:
                // раньше такой чек уходил в «Некорректные чеки», хотя продажа на сервере проводится.
                // 2026-10-29 (жалоба «чеки сами не уходят», тариф «Старт»): решение в ReplayErrorPolicy. 4xx без JSON-тела (прокси, защита
                // шлюза, страница провайдера) — не отказ NurCRM, чек повторится; 402/403 (тариф, подписка, права) — тоже ждёт в очереди, а не
                // «навсегда в ошибке». Отказ по существу (400/404/422 с JSON) остаётся «Ошибкой отправки»: три автоповтора и кнопка.
                var verdict = ReplayErrorPolicy.Classify(ex.StatusCode,
                    ex.Payload is { ValueKind: JsonValueKind.Object or JsonValueKind.Array });
                // Отказ про смену («смена закрыта») идёт обычным путём ниже (привязка к смене снимается), даже если код 403.
                if (verdict == ReplayVerdict.RetryNotifyOwner
                    && (ex.Message.Contains("смен", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("shift", StringComparison.OrdinalIgnoreCase)))
                    verdict = ReplayVerdict.Rejected;
                // 2026-10-29: «в долг» без связи — деловой отказ сервера (лимит долга, клиент удалён/заблокирован), в том числе 409, — не повод повторять:
                // чек уходит в «Требует проверки» с причиной, очередь идёт дальше. Обычный 409 «ключ ещё выполняется» остаётся временным.
                var debtBusinessReject = entry.OfflineDebt && OfflineRejection.IsBusinessConflict(ex);
                var retryable = !debtBusinessReject && (verdict != ReplayVerdict.Rejected
                                || PosCheckoutService.IsQuickTransientStatus(ex.StatusCode));
                // 2026-10-29 (аварийный аудит кассы, п. 8): 401 — токен не обновился (refresh отказал, сессия стёрта). Это не отказ
                // сервера по этому чеку: раньше вся очередь уходила в «отклонённые». Теперь чек ждёт входа, проход прекращается.
                if (ex.StatusCode == 401)
                {
                    retryable = true;
                    stopBatch = true;
                    PosLogger.Log($"OFFLINE replay: чек {entry.Id} — 401, сессия не обновлена; очередь ждёт повторного входа кассира.", "WARNING");
                }
                // 2026-10-04, стенд «сбои сервера»: сервер недоступен (502–504, 520–524, 408) или просит
                // паузу (429, 409 — ключ ещё выполняется) — остальные чеки ждут, а не идут по одному в
                // лежащий сервер (по 2–5 запросов на чек). Недоступность — ещё и авария: оплаты сразу в
                // очередь. 500 — ошибка конкретного чека: следующие чеки досылаются (чтобы один «плохой»
                // чек не держал всю очередь).
                if (ex.StatusCode is 408 or (>= 502 and <= 599))
                {
                    stopBatch = true;
                    ServerOutageMonitor.ReportFailure(ReplayContext, ex, hard: true);
                }
                else if (ex.StatusCode is 409 or 423 or 425 or 429 && !debtBusinessReject)
                {
                    stopBatch = true;
                }
                // 2026-09-29: чек офлайн-смены (открыта в аварии сервера или без интернета) уходит без
                // shift_id, и сервер берёт открытую смену кассира. Пока её нет, отказ 4xx («смена
                // не открыта») временный: чек ждёт в очереди, пока кассир откроет смену, а не
                // уходит в «Некорректные чеки».
                if (!retryable
                    && entry.ShiftId?.StartsWith("offline-", StringComparison.OrdinalIgnoreCase) == true
                    && (ex.Message.Contains("смен", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("shift", StringComparison.OrdinalIgnoreCase)))
                    retryable = true;
                var failText = ex.Message;
                if (verdict == ReplayVerdict.RetryNotifyOwner)
                {
                    // Тариф/подписка/права: остальные чеки получат тот же ответ — проход прекращаем, чек ждёт; владельцу — одно оповещение.
                    stopBatch = true;
                    failText = Tr.T($"Сервер NurCRM пока не разрешает провести чек (код {ex.StatusCode}: тариф, подписка или права). Чек сохранён и уйдёт сам, как только сервер разрешит. Ответ сервера: {ex.Message}",
                        $"NurCRM сервери чекти өткөрүүгө азырынча уруксат бербей жатат (код {ex.StatusCode}: тариф, жазылуу же укуктар). Чек сакталды, сервер уруксат бергенде өзү жөнөтүлөт. Сервердин жообу: {ex.Message}",
                        $"The NurCRM server does not allow this receipt yet (code {ex.StatusCode}: plan, subscription or permissions). The receipt is saved and will be sent automatically once the server allows it. Server reply: {ex.Message}",
                        $"NurCRM sunucusu fişe henüz izin vermiyor (kod {ex.StatusCode}: tarife, abonelik veya yetkiler). Fiş kayıtlı, sunucu izin verince kendiliğinden gönderilecek. Sunucu yanıtı: {ex.Message}",
                        $"NurCRM serveri chekka hozircha ruxsat bermayapti (kod {ex.StatusCode}: tarif, obuna yoki huquqlar). Chek saqlandi, server ruxsat berishi bilan o'zi yuboriladi. Server javobi: {ex.Message}");
                    NotifyOwnerAboutRejected(entry, failText);
                }
                else if (verdict == ReplayVerdict.Retry && ex.StatusCode is >= 400 and < 500 and not (401 or 408 or 409 or 423 or 425 or 429))
                {
                    // Ответ не от NurCRM (HTML/пустое тело): сеть или прокси «режет» запросы — не бьёмся по очереди, повторим на следующем проходе.
                    stopBatch = true;
                    failText = Tr.T($"Ответ не от сервера NurCRM (код {ex.StatusCode}) — похоже на сбой связи, прокси или страницу провайдера. Чек сохранён и повторится сам.",
                        $"NurCRM сервери жооп берген жок (код {ex.StatusCode}) — байланыш, прокси же провайдер бетинин бузулушуна окшойт. Чек сакталды, өзү кайталанат.",
                        $"The reply did not come from the NurCRM server (code {ex.StatusCode}) — looks like a connection, proxy or provider-page problem. The receipt is saved and will be retried automatically.",
                        $"Yanıt NurCRM sunucusundan gelmedi (kod {ex.StatusCode}) — bağlantı, proxy veya sağlayıcı sayfası sorunu gibi görünüyor. Fiş kayıtlı, kendiliğinden tekrar denenecek.",
                        $"Javob NurCRM serveridan kelmadi (kod {ex.StatusCode}) — aloqa, proksi yoki provayder sahifasi muammosiga o'xshaydi. Chek saqlandi, o'zi qayta uriniladi.");
                }
                // 2026-10-22 (аудит новой сборки, П1): сервер сам закрыл смену (автозакрытие в 23:00), а чек пробит в ней.
                // Отказ «смена закрыта» — не отказ по существу: чек не уходит в «Некорректные чеки» и не теряется. Привязку
                // к закрытой смене снимаем (как у офлайн-смен): следующая досылка идёт без shift_id (сервер возьмёт смену
                // по времени продажи sold_at или открытую на момент выгрузки), пока кассир не откроет новую смену.
                // 2026-10-30 (приёмка 1.17.65, «Важно»): чек «в долг» из офлайна НЕ переносим в чужую смену — без привязки сервер положил бы долг и
                // выручку в смену, открытую на момент выгрузки, а не в ту, где кассир взял деньги/выдал товар. Такой чек остаётся «Требует проверки».
                if (!retryable
                    && ex.StatusCode is >= 400 and < 500
                    && !string.IsNullOrWhiteSpace(entry.ShiftId)
                    && !entry.OfflineDebt
                    && !entry.ShiftId!.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
                    && (ex.Message.Contains("смен", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("shift", StringComparison.OrdinalIgnoreCase)))
                {
                    retryable = true;
                    var closedShiftId = entry.ShiftId!;
                    OfflinePendingSalesStore.Update(entry.Id, e => e.ShiftId = "offline-closed-" + closedShiftId);
                    failText = Tr.T("Смена закрыта сервером — чек дождётся новой смены и уйдёт сам.",
                        "Смена сервер тарабынан жабылды — чек жаңы сменаны күтөт да өзү жөнөтүлөт.",
                        "The shift was closed by the server — the receipt will wait for a new shift and be sent automatically.",
                        "Vardiya sunucu tarafından kapatıldı — fiş yeni vardiyayı bekleyip kendiliğinden gönderilecek.",
                        "Smena server tomonidan yopilgan — chek yangi smenani kutadi va o'zi yuboriladi.");
                }
                if (!retryable && entry.OfflineDebt)
                    failText = Tr.T($"Требует проверки: сервер не принял продажу «в долг», оформленную без связи (лимит долга, клиент удалён или заблокирован и т. п.). Чек и товар не потеряны — проверьте клиента и долг, затем верните чек в очередь или проведите вручную. Ответ сервера: {ex.Message}",
                        $"Текшерүү керек: сервер байланышсыз жасалган «карызга» сатууну кабыл алган жок (карыз лимити, кардар өчүрүлгөн же бөгөттөлгөн ж. б.). Чек жана товар жоголгон жок — кардарды жана карызды текшерип, чекти кезекке кайтарыңыз же кол менен өткөрүңүз. Сервердин жообу: {ex.Message}",
                        $"Needs review: the server did not accept the credit sale made without a connection (debt limit, client deleted or blocked, etc.). The receipt and goods are not lost — check the client and the debt, then return the receipt to the queue or process it manually. Server reply: {ex.Message}",
                        $"Kontrol gerekli: sunucu bağlantısız yapılan veresiye satışı kabul etmedi (borç limiti, müşteri silinmiş veya engellenmiş vb.). Fiş ve mal kaybolmadı — müşteriyi ve borcu kontrol edip fişi kuyruğa geri alın veya elle işleyin. Sunucu yanıtı: {ex.Message}",
                        $"Tekshirish kerak: server aloqasiz rasmiylashtirilgan «qarzga» sotuvni qabul qilmadi (qarz limiti, mijoz o'chirilgan yoki bloklangan va h. k.). Chek va tovar yo'qolmadi — mijoz va qarzni tekshirib, chekni navbatga qaytaring yoki qo'lda o'tkazing. Server javobi: {ex.Message}");
                OfflinePendingSalesStore.MarkFailed(entry.Id, failText, retryable);
                PosLogger.Log(
                    $"OFFLINE replay: чек {entry.Id} — {(retryable ? "временная ошибка, повторим" : "отказ сервера")}: {ex.StatusCode} {ex.Message}",
                    retryable ? "OFFLINE" : "WARNING");
                if (!retryable)
                    NotifyOwnerAboutRejected(entry, failText);
            }
            catch (JsonException ex)
            {
                OfflinePendingSalesStore.MarkFailed(entry.Id, ex.Message, retryable: false);
                NotifyOwnerAboutRejected(entry, ex.Message);
            }

            UpdateStatusText();
            RaiseStateChanged();
            if (stopBatch)
                break;
        }
    }

    /// <summary>2026-10-29 (аварийный аудит кассы, п. 8): чек отклонён сервером или требует проверки — одно сообщение владельцу в Telegram
    /// (если бот подключён) на чек; деньги покупателя взяты, а продажи на сервере нет, и без оповещения это лежало бы днями.
    /// В шапке кассы об этом же говорит красная плашка «Отклонено: M» (MainStatusViewModel).</summary>
    private static void NotifyOwnerAboutRejected(OfflineSaleEntry entry, string? reason)
    {
        try
        {
            if (entry.OwnerNotified)
                return;
            OfflinePendingSalesStore.Update(entry.Id, e => e.OwnerNotified = true);
            if (!TelegramBotService.IsConfigured)
                return;

            var total = "";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
                total = " (" + CartDisplayHelper.TotalDue(doc.RootElement).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";
            }
            catch
            {
                // сумма в сообщении необязательна
            }

            var why = string.IsNullOrWhiteSpace(reason) ? "—" : reason!.Trim();
            var when = entry.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
            var text = TelegramBotService.BuildAlert(
                Tr.T("Чек не отправился на сервер", "Чек серверге жөнөтүлгөн жок", "A receipt was not sent to the server",
                    "Fiş sunucuya gönderilemedi", "Chek serverga yuborilmadi"),
                Tr.T($"Касса: чек от {when}{total} не принят сервером NurCRM, а деньги покупатель уже отдал.\nПричина: {why}\nОткройте кассу, меню ☰, «Некорректные чеки»: проверьте чек и верните его в очередь либо проведите вручную.",
                    $"Касса: {when}{total} чек NurCRM серверине кабыл алынган жок, ал эми сатып алуучу акчаны берди.\nСебеби: {why}\nКассаны ачыңыз, меню ☰, «Туура эмес чектер»: чекти текшерип, кезекке кайтарыңыз же кол менен өткөрүңүз.",
                    $"Till: the receipt from {when}{total} was not accepted by the NurCRM server, but the customer has already paid.\nReason: {why}\nOpen the till, menu ☰, “Irregular receipts”: check the receipt and return it to the queue or process it manually.",
                    $"Kasa: {when}{total} tarihli fiş NurCRM sunucusu tarafından kabul edilmedi, müşteri ise parayı ödedi.\nNeden: {why}\nKasayı açın, menü ☰, «Hatalı fişler»: fişi kontrol edin ve kuyruğa geri alın ya da elle işleyin.",
                    $"Kassa: {when}{total} dagi chek NurCRM serveri tomonidan qabul qilinmadi, xaridor esa pulni to'ladi.\nSabab: {why}\nKassani oching, menyu ☰, «Noto'g'ri cheklar»: chekni tekshiring va navbatga qaytaring yoki qo'lda o'tkazing."));
            _ = Task.Run(async () =>
            {
                var error = await TelegramBotService.SendAsync(text).ConfigureAwait(false);
                if (error != null)
                    PosLogger.Log($"OFFLINE: оповещение владельцу об отклонённом чеке не ушло ({error}).", "WARNING");
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: оповещение об отклонённом чеке не отправлено: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Подпись сбоя досылки для журнала монитора аварии (2026-10-04).</summary>
    private static string ReplayContext => Tr.T("досылка очереди", "кезекти жөнөтүү", "sending the queue",
        "kuyruğu gönderme", "navbatni yuborish");

    private async Task<string?> ReplayOfflineSaleAsync(OfflineSaleEntry entry, CancellationToken ct)
    {
        string cartId;

        // Повтор ПОСЛЕ отправленного checkout'а. Раньше этой ветки не было: если ответ сервера не
        // дошёл (обрыв/таймаут), следующий цикл через 45 с просто проводил чек заново — деньги
        // списывались с покупателя один раз, а продажа появлялась в NurCRM дважды. Поля
        // SyncCartId/CheckoutSubmittedAt/CheckoutCompleted были объявлены под эту защиту, но
        // никогда не заполнялись — защита существовала только в комментарии.
        // 2026-09-23. Чек уже проведён на сервере, но запись вернулась в очередь. Так бывает,
        // когда касса закрылась (или её закрыли через диспетчер задач) между отметкой
        // CheckoutCompleted и удалением записи из очереди — а между ними идёт цикл сетевых
        // запросов по каждой позиции. При следующем запуске прерванная синхронизация
        // возвращается в pending_sync (см. MarkFailed(retryable: true) в начале файла).
        //
        // Проверка ниже раньше стояла с условием !entry.CheckoutCompleted, то есть выключалась
        // ровно в этом случае: код проваливался дальше и создавал вторую продажу с нуля.
        // Покупатель платил один раз, в NurCRM появлялось два чека и товар списывался дважды.
        if (entry.CheckoutCompleted)
        {
            PosLogger.Log(
                $"OFFLINE replay: чек {entry.Id} уже проведён ранее — повтор пропущен.",
                "OFFLINE");
            return entry.SyncedSaleId;
        }

        // 2026-09-28, BE-11: свежий чек очереди — одним запросом с Idempotency-Key = id записи
        // (SyncService.QuickReplay.cs). Повторная досылка тем же ключом дубля не создаёт.
        var quick = await TryQuickReplayAsync(entry, ct).ConfigureAwait(false);
        if (quick.Handled)
            return quick.SaleId;

        // 2026-10-29: «в долг» без связи старым путём (корзина → позиции → checkout) не отправляется: он не умеет передать клиента, предоплату и график долга,
        // и чек превратился бы в обычную продажу. Быстрый путь не взял чек — «Требует проверки», чек остаётся в кассе.
        if (entry.OfflineDebt)
            throw new ReplayDeferredException(Tr.T(
                "Продажа «в долг» не отправлена: быстрый способ отправки чека не подошёл. Чек сохранён — проведите его вручную на сайте или обратитесь к разработчику.",
                "«Карызга» сатуу жөнөтүлгөн жок: чекти жөнөтүүнүн тез ыкмасы туура келген жок. Чек сакталды — аны сайтта кол менен өткөрүңүз же өнүктүрүүчүгө кайрылыңыз.",
                "The credit sale was not sent: the fast sending method did not fit. The receipt is saved — process it by hand on the website or contact the developer.",
                "Veresiye satış gönderilmedi: hızlı gönderim yöntemi uymadı. Fiş kayıtlı — siteden elle işleyin veya geliştiriciye başvurun.",
                "«Qarzga» sotuv yuborilmadi: chekni yuborishning tez usuli to'g'ri kelmadi. Chek saqlandi — uni saytda qo'lda o'tkazing yoki dasturchiga murojaat qiling."))
            { NeedsReview = true };

        if (entry.CheckoutSubmittedAt is not null)
        {
            // Сверка по статусу КОРЗИНЫ (CartSaleSessionHelper.GetCheckoutStateAsync). Прежняя
            // спрашивала адрес продажи, всегда получала 404 и откладывала повтор навсегда.
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(5));
            var state = await CartSaleSessionHelper.GetCheckoutStateAsync(_sales, entry.SyncCartId, checkCts.Token)
                .ConfigureAwait(false);
            if (state == CartSaleSessionHelper.CartCheckoutState.Paid)
            {
                PosLogger.Log($"OFFLINE replay: чек {entry.Id} уже проведён на сервере — повтор пропущен.", "OFFLINE");
                OfflinePendingSalesStore.Update(entry.Id, e => e.CheckoutCompleted = true);
                return entry.SyncedSaleId;
            }

            if (state == CartSaleSessionHelper.CartCheckoutState.Unknown)
            {
                // Связь есть, но ответить "проводилась или нет" сервер не смог. Провести повторно
                // здесь — значит рискнуть дублем; ждём следующего цикла, чек остаётся в очереди.
                // 2026-10-29 (аварийный аудит кассы, п. 4): раньше здесь бросался HttpRequestException — SyncBatchAsync
                // принимал его за обрыв сети: ставил «Офлайн» при живой связи и прекращал досылку ВСЕХ следующих чеков.
                // Теперь чек откладывается (ReplayDeferredException: остальные идут дальше), а после MaxUnknownStateDeferrals
                // проходов уходит в «Требует проверки» (failed) — владелец решает вручную.
                var deferrals = entry.ReplayDeferrals + 1;
                OfflinePendingSalesStore.Update(entry.Id, e => e.ReplayDeferrals = deferrals);
                throw new ReplayDeferredException(deferrals >= MaxUnknownStateDeferrals
                    ? Tr.T("Требует проверки: сервер так и не подтвердил, прошла ли оплата этого чека. Сверьте его с продажами на сервере, затем верните в очередь или удалите.",
                        "Текшерүү керек: сервер бул чектин төлөмү өткөнүн же өтпөгөнүн ырастаган жок. Сервердеги сатуулар менен салыштырып, кийин кезекке кайтарыңыз же өчүрүңүз.",
                        "Needs review: the server never confirmed whether this receipt was paid. Compare it with the sales on the server, then return it to the queue or remove it.",
                        "Kontrol gerekli: sunucu bu fişin ödenip ödenmediğini hiç doğrulamadı. Sunucudaki satışlarla karşılaştırın, sonra kuyruğa geri alın veya silin.",
                        "Tekshirish kerak: server bu chek to'langan-to'lanmaganini hech tasdiqlamadi. Serverdagi sotuvlar bilan solishtiring, so'ng navbatga qaytaring yoki o'chiring.")
                    : "Не удалось проверить, прошла ли оплата — повтор отложен.")
                {
                    NeedsReview = deferrals >= MaxUnknownStateDeferrals,
                };
            }

            if (state == CartSaleSessionHelper.CartCheckoutState.Open)
            {
                // 2026-10-04: «open» сразу после отправки может значить «сервер ещё проводит» — ждём
                // (SyncService.LegacySettle.cs), иначе вторая продажа на тот же чек.
                DeferWhileLegacyCheckoutMayRun(entry);

                // Сервер подтвердил: корзина ещё не оплачена — значит, прошлая оплата отклонена или
                // не дошла. Её содержимое пересобираем заново (см. RebuildReplayCartAsync): раньше
                // здесь шли «сразу к оплате» с тем, что лежит в корзине, и если сервер отклонил чек
                // из-за лишних позиций, повтор отклонялся точно так же — бесконечно.
                cartId = entry.SyncCartId!;
                await RebuildReplayCartAsync(entry, cartId, ct).ConfigureAwait(false);
                return await SubmitReplayCheckoutAsync(entry, cartId, ct).ConfigureAwait(false);
            }

            // Корзины больше нет (сервер убирает незакрытые корзины при закрытии смены, живой
            // случай — автозакрытие в 01:00). Оплаченной она быть не могла: оплаченные остаются
            // со статусом checked_out. Начинаем с новой корзины.
            PosLogger.Log($"OFFLINE replay: чек {entry.Id} — прежней корзины на сервере нет, собираю заново.", "OFFLINE");
            OfflinePendingSalesStore.Update(entry.Id, e =>
            {
                e.SyncCartId = null;
                e.CheckoutSubmittedAt = null;
            });
        }

        var start = await _sales.PosSalesStartAsync(entry.CashboxId, ct).ConfigureAwait(false);
        cartId = CartDisplayHelper.TryCartId(start) ?? "";
        if (string.IsNullOrEmpty(cartId))
            throw new ApiException("Сервер не вернул cart_id для синхронизации офлайн-чека.", 500);

        await RebuildReplayCartAsync(entry, cartId, ct).ConfigureAwait(false);
        return await SubmitReplayCheckoutAsync(entry, cartId, ct).ConfigureAwait(false);
    }

    /// <summary>Корзина на сервере = ровно позиции офлайн-чека. 2026-09-26, стресс-тест: оплата
    /// чека на 144 позиции прервалась по тайм-ауту посреди переноса позиций и ушла в очередь, а
    /// часть позиций осталась в серверной корзине. Досылка брала ту же корзину (sales/start
    /// отдаёт основную) и добавляла все 144 поверх — сервер отказал «Сумма, полученная
    /// наличными, меньше суммы продажи», и чек, за который покупатель уже заплатил, навсегда
    /// оседал в «Некорректных чеках». Обычная оплата корзину перед переносом очищает
    /// (StagingCartService, «ensure-empty»), досылка — нет.</summary>
    private async Task RebuildReplayCartAsync(OfflineSaleEntry entry, string cartId, CancellationToken ct)
    {
        // 2026-10-29 (приёмка 1.17.65, Б-1): лимит времени на чек. Чек на сотни позиций (по запросу на позицию) раньше держал очередь
        // по 12 минут. Теперь по истечении лимита чек откладывается (до checkout ещё не дошли — дубля быть не может), очередь идёт дальше.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(ReplayErrorPolicy.ReplayRebuildBudget);
        try
        {
            var removed = await CartSaleSessionHelper.EnsureServerCartEmptyAsync(_sales, cartId, budgetCts.Token).ConfigureAwait(false);
            if (removed > 0)
                PosLogger.Log($"OFFLINE replay: чек {entry.Id} — из серверной корзины убрано {removed} посторонних позиций.", "OFFLINE");

            // Общая с обычной оплатой выгрузка снимка (см. StagingCartService): она умеет строки
            // «Доп. услуга» и поштучную продажу из пачки. Прежняя local-копия этого цикла молча
            // пропускала строки без product_id и теряла sale_package_id.
            await StagingCartService.PushItemsFromSnapshotAsync(_sales, cartId, entry.CartJson, budgetCts.Token).ConfigureAwait(false);
            await StagingCartService.ApplyOrderDiscountFromSnapshotAsync(_sales, cartId, entry.CartJson, budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            PosLogger.Log($"OFFLINE replay: чек {entry.Id} — сборка корзины не уложилась в {ReplayErrorPolicy.ReplayRebuildBudget.TotalSeconds:0} с, очередь идёт дальше.", "OFFLINE");
            throw new ReplayDeferredException(Tr.T(
                "Большой чек не успел собраться на сервере за отведённое время — очередь идёт дальше, чек будет отправлен на следующем проходе.",
                "Чоң чек сервердеги убакытка үлгүргөн жок — кезек улантылат, чек кийинки жолу жөнөтүлөт.",
                "The large receipt did not finish building on the server in time — the queue moves on and the receipt will be sent on the next pass.",
                "Büyük fiş sunucuda zamanında hazırlanamadı — kuyruk devam ediyor, fiş sonraki geçişte gönderilecek.",
                "Katta chek serverda vaqtida yig'ilmadi — navbat davom etadi, chek keyingi o'tishda yuboriladi."));
        }
    }

    private async Task<string?> SubmitReplayCheckoutAsync(OfflineSaleEntry entry, string cartId, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["payment_method"] = entry.PaymentMethod ?? "",
            ["print_receipt"] = "false",
            ["cash_received"] = entry.CashReceived ?? "",
        };
        PosCheckoutService.AddConsultant(body, entry.ConsultantId, entry.ConsultantCommissionEnabled, entry.ConsultantCommissionPercent);
        if (!string.IsNullOrWhiteSpace(entry.ClientId))
            body["client_id"] = entry.ClientId!.Trim(); // 2026-10-27 (Н-6): как у интерактивной оплаты (BuildCheckoutRequestBody)

        // Смена, в которую чек был пробит на самом деле. Без этого поля сервер относил продажу к
        // смене, открытой на момент ВЫГРУЗКИ: чек, пробитый вечером в смене А и выгруженный утром,
        // попадал в смену Б, и её кассир отвечал за деньги, которых не брал.
        // ВАЖНО: "offline-shift-<guid>" — это ЛОКАЛЬНЫЙ идентификатор смены, открытой без сети
        // (CashShiftService). Серверу он не известен и валидным UUID не является, поэтому такой
        // checkout отклоняется с 4xx, а 4xx здесь считается окончательным отказом (см. catch ниже)
        // — запись уходит в failed, откуда её НИЧТО не возвращает. Иначе говоря, отправив это
        // поле, мы теряли бы ровно те чеки, ради которых очередь и существует: всю офлайн-смену.
        // Тот же фильтр стоит в интерактивной оплате (PosCheckoutService.BuildCheckoutRequestBody);
        // здесь его сначала забыли продублировать. Без shift_id сервер отнесёт чек к смене на
        // момент выгрузки — это хуже, чем точная привязка, но несопоставимо лучше потери продажи.
        if (!string.IsNullOrWhiteSpace(entry.ShiftId)
            && !entry.ShiftId!.StartsWith("offline-", StringComparison.OrdinalIgnoreCase))
            body["shift_id"] = entry.ShiftId!;

        // Отметка ставится ДО запроса и переживает перезапуск процесса — именно по ней следующий
        // цикл поймёт, что ответ мог потеряться, и сначала сверится с сервером (см. выше).
        OfflinePendingSalesStore.Update(entry.Id, e =>
        {
            e.SyncCartId = cartId;
            e.CheckoutSubmittedAt = DateTimeOffset.Now;
        });

        var result = await _sales.PosCheckoutAsync(cartId, body, ct).ConfigureAwait(false);
        OfflinePendingSalesStore.Update(entry.Id, e => e.CheckoutCompleted = true);

        await ResyncStockAfterReplayAsync(entry, ct).ConfigureAwait(false);
        return CheckoutResponseHelper.TrySaleId(result);
    }

    /// <summary>2026-09-28: вынесено из SubmitReplayCheckoutAsync без изменений — тем же
    /// завершается и досылка одним запросом (SyncService.QuickReplay.cs).</summary>
    private async Task ResyncStockAfterReplayAsync(OfflineSaleEntry entry, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
        var root = doc.RootElement;

        foreach (var item in CartDisplayHelper.EnumerateItems(root))
        {
            var productId = CartDisplayHelper.TryProductId(item);
            if (string.IsNullOrEmpty(productId))
                continue;

            var soldQty = CartDisplayHelper.LineQuantity(item);
            if (soldQty <= 0)
                continue;

            try
            {
                // PosCheckoutAsync above already decremented stock server-side for this
                // sale; re-applying -soldQty via Accumulate would push serverStock+delta
                // back to the server and double-decrement it. Just resync the local
                // catalog cache to the now-authoritative server value instead.
                await _syncConflictResolver.ResolveAndSyncStockAsync(
                    productId,
                    -soldQty,
                    SyncStrategy.ServerWins,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                /* синхронизация остатков не должна отменять отправку чека */
            }
        }
    }

    private void UpdateStatusText()
    {
        var pending = OfflinePendingSalesStore.PendingCount;
        if (IsSyncInProgress)
        {
            StatusText = pending > 0
                ? Tr.T($"Синхронизация очереди: {pending} чек(ов).", $"Кезек шайкештирилүүдө: {pending} чек.",
                    $"Syncing queued receipts: {pending}.", $"Kuyruk senkronize ediliyor: {pending} fiş.",
                    $"Navbat sinxronlanmoqda: {pending} ta chek.")
                : Tr.T("Синхронизация завершается…", "Шайкештирүү аяктап жатат…", "Finishing sync…",
                    "Senkronizasyon tamamlanıyor…", "Sinxronlash yakunlanmoqda…");
            return;
        }

        if (!IsOnline)
        {
            StatusText = pending > 0
                ? Tr.T($"Оффлайн. В очереди {pending} чек(ов).", $"Офлайн. Кезекте {pending} чек.",
                    $"Offline. Receipts in queue: {pending}.", $"Çevrimdışı. Kuyrukta {pending} fiş.",
                    $"Oflayn. Navbatda {pending} ta chek.")
                : Tr.T("Оффлайн. Продажи будут сохранены локально.", "Офлайн. Сатуулар ушул кассада сакталат.",
                    "Offline. Sales will be saved locally.", "Çevrimdışı. Satışlar yerel olarak kaydedilecek.",
                    "Oflayn. Sotuvlar shu kompyuterda saqlanadi.");
            return;
        }

        StatusText = pending > 0
            ? Tr.T($"Онлайн. Ожидают синхронизации: {pending}.", $"Онлайн. Шайкештирүүнү күтүүдө: {pending}.",
                $"Online. Waiting to sync: {pending}.", $"Çevrimiçi. Senkronizasyon bekleyen: {pending}.",
                $"Onlayn. Sinxronlashni kutmoqda: {pending}.")
            : Tr.T("Онлайн. Очередь синхронизации пуста.", "Онлайн. Шайкештирүү кезеги бош.",
                "Online. The sync queue is empty.", "Çevrimiçi. Senkronizasyon kuyruğu boş.",
                "Onlayn. Sinxronlash navbati bo'sh.");
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ServerOutageMonitor.Recovered -= OnServerRecovered;
        SessionExpiryMonitor.Changed -= OnSessionChanged;
        OfflinePendingSalesStore.Appended -= OnQueueAppended;
        OfflineOperationsStore.Appended -= OnQueueAppended;

        try { _cts.Cancel(); } catch { /* ignore */ }

        try
        {
            _loopTask?.GetAwaiter().GetResult();
        }
        catch { /* ignore */ }

        _cts.Dispose();
        _syncGate.Dispose();
    }
}
