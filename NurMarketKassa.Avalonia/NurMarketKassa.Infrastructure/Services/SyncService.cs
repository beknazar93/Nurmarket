using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Фоновая синхронизация офлайн-продаж и каталога (интервал 45 с).</summary>
public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(45);

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
        UserPreferences.Instance.LowPerformanceMode ? TimeSpan.FromSeconds(90) : SyncInterval;

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

    /// <summary>Дожимает смены, закрытые на кассе, но не принятые сервером (см.
    /// PendingShiftCloseStore). Вызывается при каждом проходе синхронизации: как только сервер
    /// перестанет отклонять закрытие, смены закроются сами, без участия кассира.</summary>
    private async Task FlushPendingShiftClosesAsync(CancellationToken ct)
    {
        var pending = PendingShiftCloseStore.LoadAll();
        if (pending.Count == 0)
            return;

        foreach (var entry in pending)
        {
            if (ct.IsCancellationRequested)
                return;

            try
            {
                await _shiftApi
                    .ConstructionShiftCloseAsync(entry.ShiftId, entry.ClosingCash, null, ct)
                    .ConfigureAwait(false);
                PendingShiftCloseStore.Remove(entry.ShiftId);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 400 && IsAlreadyClosed(ex.Message))
            {
                // Смену закрыли где-то ещё (например, в веб-панели) — очередь об этом не знает.
                PendingShiftCloseStore.Remove(entry.ShiftId);
            }
            catch (Exception ex)
            {
                // Сервер всё ещё не принимает — пробуем в следующий раз, в журнал пишем
                // только смену попыток, чтобы не засорять его одной и той же строкой.
                PendingShiftCloseStore.RecordFailure(entry.ShiftId, ex.Message);
            }
        }
    }

    private static bool IsAlreadyClosed(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("закрыт", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не найден", StringComparison.OrdinalIgnoreCase));

    public event EventHandler? StateChanged;

    public bool IsOnline { get; private set; }

    public bool IsSyncInProgress { get; private set; }

    public string StatusText { get; private set; } = "Проверка связи…";

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

        _loopTask = RunLoopAsync(_cts.Token);
    }

    public async Task ProbeNowAsync(CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested)
            return;

        IsOnline = await _auth.CanReachApiAsync(ct).ConfigureAwait(false);
        LeaveOfflineModeIfBackOnline();
        UpdateStatusText();
        RaiseStateChanged();
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
                StatusText = "Синхронизация: " + ex.Message;
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

    private async Task SyncPendingAsync(CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested)
            return;

        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            IsSyncInProgress = true;
            UpdateStatusText();
            RaiseStateChanged();

            if (IsOnline)
                await FlushPendingShiftClosesAsync(ct).ConfigureAwait(false);

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
                    await _catalogCache.SyncCatalogFullAsync(ct).ConfigureAwait(false);
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

    private async Task SyncBatchAsync(IReadOnlyList<OfflineSaleEntry> pending, CancellationToken ct)
    {
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsOnline)
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
            }
            catch (HttpRequestException ex)
            {
                IsOnline = false;
                OfflinePendingSalesStore.MarkFailed(entry.Id, ex.Message, retryable: true);
                break;
            }
            catch (TaskCanceledException ex)
            {
                IsOnline = false;
                OfflinePendingSalesStore.MarkFailed(
                    entry.Id,
                    string.IsNullOrWhiteSpace(ex.Message) ? "Таймаут сети." : ex.Message,
                    retryable: true);
                break;
            }
            catch (ApiException ex)
            {
                // Раньше ЛЮБАЯ ошибка сервера означала "failed навсегда": ничто в приложении не
                // возвращает запись из failed обратно в очередь, то есть деньги взяты, а продажа
                // в NurCRM не попадает никогда. Временные сбои (5xx, таймаут шлюза, «слишком
                // много запросов») к этому не относятся — их надо повторить. Отказ по существу
                // (4xx: нет товара, неверные данные) повтором не лечится и остаётся failed.
                var retryable = ex.StatusCode is null or >= 500 or 408 or 429;
                OfflinePendingSalesStore.MarkFailed(entry.Id, ex.Message, retryable);
                PosLogger.Log(
                    $"OFFLINE replay: чек {entry.Id} — {(retryable ? "временная ошибка, повторим" : "отказ сервера")}: {ex.StatusCode} {ex.Message}",
                    retryable ? "OFFLINE" : "WARNING");
            }
            catch (JsonException ex)
            {
                OfflinePendingSalesStore.MarkFailed(entry.Id, ex.Message, retryable: false);
            }

            UpdateStatusText();
            RaiseStateChanged();
        }
    }

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
                throw new HttpRequestException("Не удалось проверить, прошла ли оплата — повтор отложен.");
            }

            if (state == CartSaleSessionHelper.CartCheckoutState.Open)
            {
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
        var removed = await CartSaleSessionHelper.EnsureServerCartEmptyAsync(_sales, cartId, ct).ConfigureAwait(false);
        if (removed > 0)
            PosLogger.Log($"OFFLINE replay: чек {entry.Id} — из серверной корзины убрано {removed} посторонних позиций.", "OFFLINE");

        // Общая с обычной оплатой выгрузка снимка (см. StagingCartService): она умеет строки
        // «Доп. услуга» и поштучную продажу из пачки. Прежняя local-копия этого цикла молча
        // пропускала строки без product_id и теряла sale_package_id.
        await StagingCartService.PushItemsFromSnapshotAsync(_sales, cartId, entry.CartJson, ct).ConfigureAwait(false);
        await StagingCartService.ApplyOrderDiscountFromSnapshotAsync(_sales, cartId, entry.CartJson, ct).ConfigureAwait(false);
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

        return CheckoutResponseHelper.TrySaleId(result);
    }

    private void UpdateStatusText()
    {
        var pending = OfflinePendingSalesStore.PendingCount;
        if (IsSyncInProgress)
        {
            StatusText = pending > 0
                ? $"Синхронизация очереди: {pending} чек(ов)."
                : "Синхронизация завершается…";
            return;
        }

        if (!IsOnline)
        {
            StatusText = pending > 0
                ? $"Оффлайн. В очереди {pending} чек(ов)."
                : "Оффлайн. Продажи будут сохранены локально.";
            return;
        }

        StatusText = pending > 0
            ? $"Онлайн. Ожидают синхронизации: {pending}."
            : "Онлайн. Очередь синхронизации пуста.";
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

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
