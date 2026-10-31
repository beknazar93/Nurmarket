using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

// 2026-10-25, ТЗ «оприходование в фоне» (Q1), раздел 4.3: воркер очереди. Задачи строго по одной и по порядку создания (порядок операций
// над одним товаром сохраняется): пока старейшая активная задача ждёт повтор, следующие ждут её.

public sealed partial class BackgroundReceivingQueue
{
    private void EnsureWorker()
    {
        lock (_sync)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                // Связь вернулась — повторять сразу, не дожидаясь расписания ожидания связи.
                ServerOutageMonitor.StateChanged += (_, _) => Wake();
                ServerOutageMonitor.Recovered += Wake;
            }
            if (_worker is { IsCompleted: false } || _closed)
                return;
            _stop = new CancellationTokenSource();
            var stop = _stop.Token;
            _worker = Task.Run(() => WorkerLoopAsync(stop));
        }
    }

    private async Task WorkerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var wake = WakeTask();
                ReceivingJob? job;
                TimeSpan? wait = null;
                lock (_sync)
                    job = PickNextLocked(out wait);
                if (job is null)
                {
                    await Task.WhenAny(wake, Task.Delay(wait ?? TimeSpan.FromMinutes(1), stop)).ConfigureAwait(false);
                    continue;
                }

                if (!ServerReady())
                {
                    // Авария сервера / нет входа / офлайн-режим: попытки НЕ расходуются, ждём связь.
                    SetWaitingForServer(job, true);
                    await Task.WhenAny(wake, Task.Delay(ServerPoll, stop)).ConfigureAwait(false);
                    continue;
                }

                await RunAttemptAsync(job, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приход в фоне: сбой цикла очереди ({ex.GetType().Name}: {ex.Message}).", "ERROR");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Старейшая активная задача текущей компании; если она ждёт повтора — null и сколько ждать.</summary>
    private ReceivingJob? PickNextLocked(out TimeSpan? wait)
    {
        wait = null;
        if (_closed)
            return null;
        var company = CurrentCompany;
        var job = _jobs.Where(j => j.IsActive && SameCompany(j.CompanyId, company)).OrderBy(j => j.CreatedAt).FirstOrDefault();
        if (job is null)
            return null;
        if (job.Status == ReceivingJobStatus.RetryWait && job.NextAttemptAt is { } at && at > DateTime.UtcNow)
        {
            wait = at - DateTime.UtcNow;
            return null;
        }
        return job;
    }

    private bool ServerReady()
    {
        if (ServerOutageMonitor.IsOutage || OfflineModeHelper.SellLocally)
            return false;
        var api = Api;
        return api is null || !string.IsNullOrEmpty(api.AccessToken);
    }

    private void SetWaitingForServer(ReceivingJob job, bool waiting)
    {
        bool changed;
        lock (_sync)
        {
            changed = job.Status != ReceivingJobStatus.RetryWait || job.WaitingForServer != waiting;
            if (!changed)
                return;
            job.Status = ReceivingJobStatus.RetryWait;
            job.WaitingForServer = waiting;
            job.NextAttemptAt = null;
            Save();
        }
        RaiseChanged(job);
    }

    private void Update(ReceivingJob job, Action<ReceivingJob> change)
    {
        lock (_sync)
        {
            change(job);
            Save();
        }
        RaiseChanged(job);
    }

    // ------------------------------------------------------------------ одна попытка

    private async Task RunAttemptAsync(ReceivingJob job, CancellationToken stop)
    {
        using var fileLock = await AcquireFileLockAsync(stop).ConfigureAwait(false);
        if (fileLock is null)
        {
            // Замок держит другая копия программы на этом ПК — не мешаем ей; подождём.
            Update(job, j => { j.Status = ReceivingJobStatus.RetryWait; j.WaitingForServer = false; j.NextAttemptAt = DateTime.UtcNow.AddSeconds(30); });
            return;
        }

        SyncOutcomesFromState(job);
        bool needReceive;
        lock (_sync)
            needReceive = job.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown);
        if (needReceive)
            await ReceiveAsync(job, stop).ConfigureAwait(false);
        else
            await FinishAsync(job, stop).ConfigureAwait(false);
    }

    /// <summary>Строки, которые состояние задачи уже знает как принятые/отклонённые, а строка задачи ещё нет (перезапуск посреди попытки):
    /// заново их не отправляем — иначе без поставщика остаток прибавится дважды.</summary>
    private void SyncOutcomesFromState(ReceivingJob job)
    {
        var st = job.State;
        lock (_sync)
        {
            var changed = false;
            foreach (var l in job.Lines.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown))
            {
                string? o;
                lock (st.Gate)
                    o = st.LineOutcome.TryGetValue(l.Key, out var v) ? v : null;
                if (o is ReceivingLineOutcome.Posted)
                {
                    l.Outcome = ReceivingLineOutcome.Posted;
                    changed = true;
                    bool variantsLost;
                    lock (st.Gate)
                        variantsLost = l.VariantQuantities is { Count: > 0 } && !st.VariantsDone.Contains(l.Key);
                    if (variantsLost)
                        AddError(job, Tr.T($"{l.ProductName}: товар принят, но остатки размеров могли не обновиться — проверьте в «Размеры и цвета».",
                            $"{l.ProductName}: товар кабыл алынды, бирок өлчөмдөрдүн калдыгы жаңырбай калышы мүмкүн — «Өлчөмдөр жана түстөр» бөлүмүн текшериңиз.",
                            $"{l.ProductName}: received, but the size stock may not have been updated — check “Sizes and colors”.",
                            $"{l.ProductName}: kabul edildi ancak beden stokları güncellenmemiş olabilir — «Bedenler ve renkler»i kontrol edin.",
                            $"{l.ProductName}: qabul qilindi, lekin o'lchamlar qoldig'i yangilanmagan bo'lishi mumkin — «O'lchamlar va ranglar»ni tekshiring."));
                }
                else if (o is ReceivingLineOutcome.Check)
                {
                    l.Outcome = ReceivingLineOutcome.Check;
                    changed = true;
                }
            }
            if (changed)
                Save();
        }
    }

    private async Task ReceiveAsync(ReceivingJob job, CancellationToken stop)
    {
        Update(job, j =>
        {
            j.Status = ReceivingJobStatus.Sending;
            j.WaitingForServer = false;
            j.NextAttemptAt = null;
            j.LastError = null;
        });

        // 2026-10-26 (ТЗ «Склад — массовое добавление», М1): подготовка задачи (проверка штрихкода новых товаров на сервере) — до PostAsync.
        // Сбой подготовки — обычная неудачная попытка: те же повторы, 429/авария/нет входа попыток не тратят.
        if (!string.IsNullOrEmpty(job.Preparer))
        {
            Exception? prepFailure = null;
            job.State.Interrupted = false;
            job.State.LastStatusCode = null;
            try
            {
                await PrepareJobAsync(job, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return; // выход из программы: задача остаётся «отправляется», при запуске подготовка повторится (только чтение)
            }
            catch (Exception ex)
            {
                prepFailure = ex;
                job.State.NoteFailure(ex);
                PosLogger.Log($"Приход в фоне: задача {job.Id} — подготовка оборвалась ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
            if (prepFailure is not null)
            {
                await ApplyAttemptAsync(job, new Dictionary<ReceivingLineVm, JobLine>(ReferenceEqualityComparer.Instance),
                    new List<ReceivingLineVm>(), null, prepFailure).ConfigureAwait(false);
                return;
            }
        }

        var map = new Dictionary<ReceivingLineVm, JobLine>(ReferenceEqualityComparer.Instance);
        List<ReceivingLineVm> vms;
        lock (_sync)
        {
            vms = new List<ReceivingLineVm>();
            foreach (var jl in job.Lines.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown))
            {
                var vm = jl.ToVm();
                map[vm] = jl;
                vms.Add(vm);
            }
        }

        var st = job.State;
        st.KeyOf = vm => map[vm].Key;
        st.StopRequested = () => stop.IsCancellationRequested;
        st.ReceiptClaimed = id =>
        {
            lock (_sync)
                return _jobs.Any(j => !ReferenceEquals(j, job) && j.ReceiptId == id);
        };
        st.Interrupted = false;
        st.LastStatusCode = null;
        st.Parallelism = job.Source == ReceivingJobSource.Bulk ? Math.Max(1, BulkPostParallel) : 1; // 2026-10-30: части мастера загрузки идут по несколько строк сразу
        // 2026-10-30 (скорость): общая запись для параллельных строк. Изменение состояния получает номер; запись, начатая ПОСЛЕ изменения, уже содержит его —
        // другим потокам, ждавшим замок, свою запись делать не нужно (каждый поток по-прежнему возвращается только когда его изменение на диске).
        long stateSeq = 0, savedSeq = 0;
        void OnState()
        {
            var mine = Interlocked.Increment(ref stateSeq);
            lock (_sync)
            {
                if (Interlocked.Read(ref savedSeq) >= mine)
                    return;
                var upTo = Interlocked.Read(ref stateSeq);
                // ProductId созданного товара — в строку задачи до следующего запроса (после перезапуска товар не создаётся второй раз).
                foreach (var (vm, jl) in map)
                    if (!string.IsNullOrWhiteSpace(vm.ProductId) && jl.ProductId != vm.ProductId)
                        jl.ProductId = vm.ProductId;
                Save();
                Interlocked.Exchange(ref savedSeq, upTo);
            }
        }
        st.Changed += OnState;

        PurchaseReceivingService.PostResult? result = null;
        Exception? failure = null;
        // 2026-10-30: часть мастера загрузки — сотни записей товаров подряд; подсказка соседям/телефонам/опросу ленты уходит одна на часть, а не на каждый запрос.
        using var quiet = job.Source == ReceivingJobSource.Bulk ? SyncCenter.QuietLocalChanges() : null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            cts.CancelAfter(AttemptTimeout);
            var supplier = job.Supplier?.ToSupplier();
            result = await PurchaseReceivingService.Instance
                .PostAsync(vms, supplier, job.PaidNow, job.Employee, cts.Token, job.PaymentFields, st)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return; // выход из программы: задача остаётся «отправляется», при запуске сверится с сервером
        }
        catch (Exception ex)
        {
            failure = ex;
            PosLogger.Log($"Приход в фоне: задача {job.Id} — попытка оборвалась ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            st.Changed -= OnState;
        }

        // Страховка: остановка программы могла прийти внутри PostAsync (он выпускает отмену только по токену остановки).
        if (stop.IsCancellationRequested && result is null)
            return;

        await ApplyAttemptAsync(job, map, vms, result, failure).ConfigureAwait(false);
    }

    /// <summary>2026-10-26 (М1): крючок подготовки получает КОПИЮ задачи; изменённые строки (по Key) возвращаются в задачу под замком и пишутся
    /// на диск — даже если крючок упал на середине (уже подготовленные строки не готовим заново).</summary>
    private async Task PrepareJobAsync(ReceivingJob job, CancellationToken stop)
    {
        Func<ReceivingJob, CancellationToken, Task>? prepare;
        lock (_sync)
            _preparers.TryGetValue(job.Preparer ?? "", out prepare);
        if (prepare is null)
            throw new InvalidOperationException($"крючок подготовки «{job.Preparer}» не подключён");
        ReceivingJob copy;
        lock (_sync)
            copy = job.Clone();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            cts.CancelAfter(AttemptTimeout);
            await prepare(copy, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            var byKey = copy.Lines.ToDictionary(l => l.Key);
            lock (_sync)
            {
                var changed = false;
                for (var i = 0; i < job.Lines.Count; i++)
                {
                    if (!byKey.TryGetValue(job.Lines[i].Key, out var prepared) || job.Lines[i].Outcome != prepared.Outcome)
                        continue;
                    if (job.Lines[i].ProductId == prepared.ProductId && job.Lines[i].Source == prepared.Source)
                        continue;
                    job.Lines[i] = prepared;
                    changed = true;
                }
                if (changed)
                    Save();
            }
        }
    }

    private async Task ApplyAttemptAsync(ReceivingJob job, Dictionary<ReceivingLineVm, JobLine> map, List<ReceivingLineVm> vms,
        PurchaseReceivingService.PostResult? result, Exception? failure)
    {
        var st = job.State;
        lock (_sync)
        {
            foreach (var (vm, jl) in map)
                if (!string.IsNullOrWhiteSpace(vm.ProductId))
                    jl.ProductId = vm.ProductId;

            if (result is not null)
                foreach (var vm in result.PostedLines)
                    if (map.TryGetValue(vm, out var jl))
                        jl.Outcome = ReceivingLineOutcome.Posted;
            foreach (var jl in map.Values.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown))
            {
                string? o;
                lock (st.Gate)
                    o = st.LineOutcome.TryGetValue(jl.Key, out var v) ? v : null;
                if (o is not null && o != ReceivingLineOutcome.Posted)
                    jl.Outcome = o;
                else if (o == ReceivingLineOutcome.Posted)
                    jl.Outcome = ReceivingLineOutcome.Posted;
            }
            // С поставщиком отказ документа — отказ всем его строкам (ключ забыт, повтор — только по «Повторить»).
            if (job.Supplier is not null && st.ReceiptOutcome == ReceivingReceiptOutcome.Rejected)
                foreach (var jl in map.Values.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown && !string.IsNullOrWhiteSpace(l.ProductId)))
                    jl.Outcome = ReceivingLineOutcome.Rejected;
            if (job.Supplier is not null && st.ReceiptOutcome == ReceivingReceiptOutcome.Posted)
                foreach (var jl in map.Values.Where(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown && !string.IsNullOrWhiteSpace(l.ProductId)))
                    jl.Outcome = ReceivingLineOutcome.Posted;

            if (st.ReceiptOutcome == ReceivingReceiptOutcome.Posted)
            {
                job.ReceiptId = st.ReceiptId;
                job.ReceiptTotal = st.ReceiptTotal;
                job.ReceiptAt = st.ReceiptAt;
            }
            if (result is not null)
            {
                job.Created += result.Created;
                // Тексты отказов по строкам, которые ещё будут повторены, в итог не попадают (временные); замечания по принятым и итоговые отказы — да.
                var retryLeft = job.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown);
                foreach (var e in result.Errors)
                    if (KeepError(job, e, retryLeft))
                        AddError(job, e);
            }
            // Причина последнего отказа — человеку («Подробнее» и статус).
            if (failure is not null)
                job.LastError = failure is OperationCanceledException ? PurchaseReceivingService.NoAnswerText : ReceivingServerMessages.Humanize(failure.Message);
            else if (result is not null && result.Errors.Count > 0)
                job.LastError = result.Errors[^1];
            Save();
        }

        await ApplyPendingStockAsync(job).ConfigureAwait(false);
        // 2026-10-27 (цена склад-касса): после прихода каталог кассы читаем заново не только при новых товарах, но и когда у принятых
        // товаров менялась закупка/цена продажи (сервер мог пересчитать цену; версия каталога от этого не меняется — без этого
        // касса продавала бы по старой цене до полной загрузки раз в 15–30 мин).
        if (result is { Created: > 0 }
            || result is not null && result.PostedLines.Any(l => !l.IsNew
                && (l.Source == ReceivingSource.GlobalBase
                    || Math.Abs(l.SalePrice - l.OriginalSalePrice) > 0.004
                    || Math.Abs(l.PurchasePrice - l.OriginalPurchasePrice) > 0.004)))
            RequestCatalogRefresh();

        DecideNext(job, st, failure);
    }

    /// <summary>Что дальше после попытки: повтор / ошибка / шаги после прихода / итог.</summary>
    private void DecideNext(ReceivingJob job, ReceivingPostState st, Exception? failure)
    {
        bool retryable;
        lock (_sync)
            retryable = job.Lines.Any(l => l.Outcome is ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown);

        if (!retryable)
        {
            Update(job, j => j.Status = ReceivingJobStatus.Finishing);
            return;
        }

        // Повтор. 429 («слишком часто»), авария сервера и нет входа попыток не тратят: ждём, сколько просит сервер / пока вернётся связь.
        var code = st.LastStatusCode;
        if (code == 429)
        {
            var wait = ApiThrottleWait();
            Update(job, j =>
            {
                j.Status = ReceivingJobStatus.RetryWait;
                j.WaitingForServer = false;
                j.NextAttemptAt = DateTime.UtcNow + wait;
            });
            return;
        }
        if (ServerOutageMonitor.IsOutage || code == 401)
        {
            Update(job, j =>
            {
                j.Status = ReceivingJobStatus.RetryWait;
                j.WaitingForServer = true;
                // Авария: вернётся связь — воркер проснётся сам (StateChanged). Нет входа (401): проверим через ServerPoll.
                j.NextAttemptAt = ServerOutageMonitor.IsOutage ? null : DateTime.UtcNow + ServerPoll;
            });
            return;
        }

        bool exhausted;
        lock (_sync)
        {
            job.Attempts++;
            exhausted = job.Attempts > MaxRetries;
        }
        if (exhausted)
        {
            Update(job, j =>
            {
                j.LastError ??= PurchaseReceivingService.NoAnswerText;
                AddError(j, j.LastError);
                SetFinalStatus(j);
            });
            RaiseFinished(job);
            return;
        }
        var delay = RetryDelays[Math.Min(job.Attempts - 1, RetryDelays.Length - 1)];
        var block = ApiThrottle.RemainingBlock;
        if (block > delay)
            delay = block;
        Update(job, j =>
        {
            j.Status = ReceivingJobStatus.RetryWait;
            j.WaitingForServer = false;
            j.NextAttemptAt = DateTime.UtcNow + delay;
        });
    }

    private static TimeSpan ApiThrottleWait()
    {
        var block = ApiThrottle.RemainingBlock + TimeSpan.FromSeconds(1);
        return block > ThrottleMinWait ? block : ThrottleMinWait;
    }

    // ------------------------------------------------------------------ шаги после прихода

    /// <summary>Остатки плиток принятых строк — только после ответа сервера, от ТЕКУЩЕГО остатка плитки (продажи за это время не откатываются).
    /// Отметка StockApplied пишется сразу: второй раз не прибавляем.</summary>
    private async Task ApplyPendingStockAsync(ReceivingJob job)
    {
        List<JobLine> todo;
        lock (_sync)
            todo = job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted && !l.IsNew && !l.StockApplied && !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        foreach (var jl in todo)
        {
            await ApplyStockToTileAsync(jl).ConfigureAwait(false);
            lock (_sync)
            {
                jl.StockApplied = true;
                Save();
            }
        }
    }

    private async Task FinishAsync(ReceivingJob job, CancellationToken stop)
    {
        await ApplyPendingStockAsync(job).ConfigureAwait(false);
        Update(job, j => { j.Status = ReceivingJobStatus.Finishing; j.WaitingForServer = false; j.NextAttemptAt = null; });

        PurchaseReceivingService.PostResult built;
        PurchaseReceivingService.Supplier? supplier;
        lock (_sync)
        {
            var posted = job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).ToList();
            built = new PurchaseReceivingService.PostResult(posted.Select(l => l.ToVm()).ToList(), posted.Sum(l => l.LineTotal), job.Created,
                job.Errors.ToList(), job.ReceiptId, job.ReceiptTotal, job.ReceiptAt, job.State.ReceiptKey);
            supplier = job.Supplier?.ToSupplier();
        }

        var texts = new List<string>();
        Exception? failure = null;
        if (built.PostedLines.Count > 0)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                cts.CancelAfter(TimeSpan.FromSeconds(120));

                // Долг поставщику — для склада и накладной (Source закупок — у финишера окна).
                if (supplier is not null && !job.PaidNow && !job.DebtDone && job.Source is ReceivingJobSource.Warehouse or ReceivingJobSource.Invoice)
                {
                    var api = Api;
                    if (api is null)
                        throw new InvalidOperationException("нет клиента API");
                    var debt = await SupplierDebtRecorder.RecordAfterReceiptAsync(api, supplier, built, job.DebtDays,
                        job.Source == ReceivingJobSource.Invoice ? "invoice" : "receipt", cts.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        job.DebtDone = true;
                        if (debt is not null)
                            job.Summary = (job.Summary is { Length: > 0 } s ? s + "\n" : "") + SupplierDebtRecorder.ResultText(debt);
                        Save();
                    }
                }

                if (job.Source == ReceivingJobSource.Purchases && !job.FinisherDone)
                {
                    PurchaseFinisher? fin;
                    lock (_sync)
                        _finishers.TryGetValue(job.Finisher ?? "purchases", out fin);
                    if (fin is null)
                    {
                        lock (_sync)
                        {
                            AddError(job, Tr.T("Приход проведён, но обработчик оплаты закупки не подключён — оформите оплату вручную.",
                                "Кирүү өткөрүлдү, бирок сатып алуунун төлөм иштеткичи туташкан эмес — төлөмдү кол менен жасаңыз.",
                                "The delivery is posted, but the purchase payment handler is not connected — record the payment manually.",
                                "Mal kabulü işlendi ancak alım ödeme işleyicisi bağlı değil — ödemeyi elle yapın.",
                                "Kirim o'tkazildi, lekin xarid to'lovi ishlovchisi ulanmagan — to'lovni qo'lda rasmiylashtiring."));
                            job.FinisherDone = true;
                            Save();
                        }
                    }
                    else
                    {
                        var fr = await fin.Run(job.Clone(), built, cts.Token).ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (fr.Errors is { Count: > 0 })
                                foreach (var e in fr.Errors)
                                    AddError(job, e);
                            if (!string.IsNullOrWhiteSpace(fr.Text))
                                job.Summary = (job.Summary is { Length: > 0 } s ? s + "\n" : "") + fr.Text;
                            job.FinisherDone = fr.Done;
                            Save();
                        }
                        if (!fr.Done)
                            failure = new InvalidOperationException(fr.Text ?? "шаг после прихода не выполнен");
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
                PosLogger.Log($"Приход в фоне: задача {job.Id} — шаг после прихода не выполнен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }

        if (failure is not null)
        {
            // Приход проведён, не прошёл долг/оплата: повторяем только эти шаги (отметки DebtDone/FinisherDone), потом — внимание кассира.
            bool exhausted;
            lock (_sync)
            {
                job.LastError = failure is OperationCanceledException ? PurchaseReceivingService.NoAnswerText : ReceivingServerMessages.Humanize(failure.Message);
                job.Attempts++;
                exhausted = job.Attempts > MaxRetries;
            }
            if (!exhausted)
            {
                var delay = RetryDelays[Math.Min(job.Attempts - 1, RetryDelays.Length - 1)];
                Update(job, j => { j.Status = ReceivingJobStatus.RetryWait; j.WaitingForServer = false; j.NextAttemptAt = DateTime.UtcNow + delay; });
                return;
            }
            lock (_sync)
                AddError(job, Tr.T("Приход проведён, но долг или оплата не записаны: ", "Кирүү өткөрүлдү, бирок карыз же төлөм жазылган жок: ",
                    "The delivery is posted, but the debt or payment was not recorded: ", "Mal kabulü işlendi ancak borç veya ödeme kaydedilmedi: ",
                    "Kirim o'tkazildi, lekin qarz yoki to'lov yozilmadi: ") + job.LastError);
            Update(job, j => { SetFinalStatus(j, forcePartial: true); });
            RaiseFinished(job);
            return;
        }

        Update(job, j =>
        {
            if (built.PostedLines.Count > 0)
                j.Summary = PurchaseReceivingService.SummaryText(built) + (j.Summary is { Length: > 0 } s ? "\n" + s : "");
            SetFinalStatus(j);
        });
        RaiseFinished(job);
    }

    /// <summary>Итоговый статус по строкам. Под lock (_sync) вызывает Update.</summary>
    private static void SetFinalStatus(ReceivingJob j, bool forcePartial = false)
    {
        var posted = j.Lines.Count(l => l.Outcome == ReceivingLineOutcome.Posted);
        var anyCheck = j.Lines.Any(l => l.Outcome == ReceivingLineOutcome.Check);
        j.WaitingForServer = false;
        j.NextAttemptAt = null;
        j.FinishedAt = DateTime.Now;
        if (posted == j.Lines.Count && !forcePartial)
            j.Status = ReceivingJobStatus.Done;
        else if (posted > 0)
            j.Status = ReceivingJobStatus.Partial;
        else if (anyCheck)
            j.Status = ReceivingJobStatus.NeedsCheck;
        else
            j.Status = ReceivingJobStatus.Failed;
        if (j.Status != ReceivingJobStatus.Done)
            j.LastError ??= j.Errors.LastOrDefault();
        // Готовая задача не держит тяжёлое: размеры строк и состояние повторов нужны только пока приход не завершён.
        if (j.Status == ReceivingJobStatus.Done)
        {
            foreach (var l in j.Lines)
            {
                l.VariantQuantities = null;
                l.VariantLabels = null;
            }
            // 2026-10-26 (М1): готовая часть массовой пачки — совсем без строк и состояния повторов, остаются счётчики
            // (15 000 строк = 150 частей; иначе queue.json раздувается и переписывается целиком после каждого шага).
            if (j.Source == ReceivingJobSource.Bulk)
            {
                j.CompactedLineCount = j.Lines.Count;
                j.CompactedPostedCount = posted;
                j.CompactedTotal = j.Lines.Sum(l => l.LineTotal);
                j.Lines.Clear();
                j.Signature = ""; // подпись строк ~2 КБ на часть; защита от двойного нажатия нужна только первые 3 с
                j.State = new ReceivingPostState();
            }
        }
    }

    private static bool KeepError(ReceivingJob job, string error, bool retryLeft)
    {
        var line = job.Lines.FirstOrDefault(l => l.ProductName.Length > 0 && error.StartsWith(l.ProductName + ":", StringComparison.Ordinal));
        if (line is null)
            return !retryLeft;
        return line.Outcome is not (ReceivingLineOutcome.Pending or ReceivingLineOutcome.Unknown);
    }

    private static void AddError(ReceivingJob job, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || job.Errors.Contains(text) || job.Errors.Count >= 50)
            return;
        job.Errors.Add(text);
    }

    // ------------------------------------------------------------------ каталог кассы

    /// <summary>Остаток плитки = ТЕКУЩИЙ остаток + принятое (чтение и запись — в одном заходе UI-потока, чтобы подряд идущие
    /// приходы одного товара не потеряли друг друга).</summary>
    private static async Task ApplyStockToTileAsync(JobLine line)
    {
        double? next = null;
        var mustWeigh = false;
        try
        {
            await UiDispatcherHolder.InvokeAsync(() =>
            {
                var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
                if (tile is null)
                    return;
                mustWeigh = tile.MustWeigh;
                next = Math.Round(tile.Quantity + line.Quantity, 6); // 2026-10-31: только шум double, показ не округляется
                StockSyncService.ApplyQuantityToTile(tile, next.Value, mustWeigh);
            }).ConfigureAwait(false);
            if (next is { } value)
                CatalogCacheService.PersistProductStock(line.ProductId!, value, mustWeigh);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приход в фоне: остаток товара {line.ProductId} в каталоге не обновлён ({ex.Message}).", "WARNING");
        }
    }

    private int _refreshRunning;
    private bool _refreshPending;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    /// <summary>Новые товары созданы — каталог кассы обновляется в фоне (не ждём; пачка задач — не чаще раза в минуту).</summary>
    private void RequestCatalogRefresh()
    {
        lock (_sync)
            _refreshPending = true;
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var wait = _lastRefreshUtc + CatalogRefreshMinInterval - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait).ConfigureAwait(false);
                    while (ServerOutageMonitor.IsOutage)
                        await Task.Delay(ServerPoll).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (!_refreshPending)
                            break;
                        _refreshPending = false;
                    }
                    try
                    {
                        await (CatalogRefresher?.Invoke() ?? CatalogCacheService.RefreshFromApiAsync()).ConfigureAwait(false);
                        _lastRefreshUtc = DateTime.UtcNow;
                        CatalogRefreshed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _lastRefreshUtc = DateTime.UtcNow;
                        PosLogger.Log($"Приход в фоне: каталог после создания товаров не обновлён: {ex.Message}", "WARNING");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
                bool again;
                lock (_sync)
                    again = _refreshPending;
                if (again)
                    RequestCatalogRefresh();
            }
        });
    }
}
