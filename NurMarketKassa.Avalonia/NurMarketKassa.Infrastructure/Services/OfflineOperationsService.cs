using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: оформление возврата и оплаты долга без связи (владелец разрешил). Здесь — вся логика без окон: найти чек, посчитать, поставить
/// операцию в очередь (OfflineOperationsStore), записать её в журнал возвратов и события смены, увеличить остаток. Отправка на сервер — SyncService
/// (OfflineOperationsReplayer) после чеков очереди, тем же ключом идемпотентности.
///
/// Порядок записи важен для денег: СНАЧАЛА операция в файл очереди (не записалась — исключение, ничего больше не тронуто), потом журнал/смена/остаток
/// (их сбой — только в журнал: операция уже в очереди и не потеряется).</summary>
public static class OfflineOperationsService
{
    public sealed record FoundSale(OfflineSaleSnapshot? Snapshot, string? Error);

    public static string RejectedSaleText => Tr.T(
        "Этот чек не принят сервером («Требует проверки»). Сначала разберите его в «Некорректных чеках», потом оформляйте возврат.",
        "Бул чек сервер тарабынан кабыл алынган жок («Текшерүү керек»). Алгач аны «Туура эмес чектерде» иретке келтириңиз, анан кайтарууну жасаңыз.",
        "This receipt was not accepted by the server (“Needs review”). Sort it out in “Irregular receipts” first, then make the return.",
        "Bu fiş sunucu tarafından kabul edilmedi («Kontrol gerekli»). Önce «Hatalı fişler» bölümünde çözün, sonra iade yapın.",
        "Bu chek server tomonidan qabul qilinmadi («Tekshirish kerak»). Avval uni «Noto'g'ri cheklar»da hal qiling, so'ng qaytarishni rasmiylashtiring.");

    public static string NotKnownLocallyText => Tr.T(
        "Этого чека нет в памяти кассы (он старый или пробит на другой кассе). Найдите чек на сервере, когда появится связь.",
        "Бул чек кассанын эсинде жок (эски же башка кассада өткөрүлгөн). Байланыш пайда болгондо чекти серверден табыңыз.",
        "The till has no record of this receipt (it is old or was rung up on another till). Find the receipt on the server once the connection is back.",
        "Bu fiş kasanın hafızasında yok (eski veya başka kasada kesilmiş). Bağlantı gelince fişi sunucuda bulun.",
        "Bu chek kassa xotirasida yo'q (eski yoki boshqa kassada urilgan). Aloqa tiklanganda chekni serverdan toping.");

    public static string NoShiftText => Tr.T(
        "Откройте смену — оплату долга без связи принимает только касса с открытой своей сменой.",
        "Сменаны ачыңыз — байланышсыз карыз төлөмүн өз сменасы ачык касса гана кабыл алат.",
        "Open a shift — a debt payment without a connection is taken only by a till with its own open shift.",
        "Vardiyayı açın — bağlantısız borç ödemesini yalnızca kendi vardiyası açık olan kasa alır.",
        "Smenani oching — aloqasiz qarz to'lovini faqat o'z smenasi ochiq kassa qabul qiladi.");

    public static string NoShiftReturnText => Tr.T(
        "Откройте смену — возврат с выдачей наличных без связи оформляется только при открытой смене (иначе деньги не попадут в итоги смены).",
        "Сменаны ачыңыз — накталай берүү менен байланышсыз кайтаруу сменасы ачык болгондо гана жасалат (антпесе акча смена жыйынтыгына кирбейт).",
        "Open a shift — a return with cash paid out without a connection is made only with an open shift (otherwise the money will not reach the shift totals).",
        "Vardiyayı açın — nakit ödemeli bağlantısız iade yalnızca açık vardiyada yapılır (aksi halde para vardiya toplamlarına girmez).",
        "Smenani oching — naqd berish bilan aloqasiz qaytarish faqat ochiq smenada rasmiylashtiriladi (aks holda pul smena jamisiga kirmaydi).");

    public static string UnknownOnlineReturnText => Tr.T(
        "По этому чеку онлайн-возврат оборвался без ответа сервера: он мог пройти. Пока связи нет, возврат по чеку без связи не оформляется — дождитесь связи и проверьте чек.",
        "Бул чек боюнча онлайн кайтаруу сервердин жообусуз үзүлдү: ал өтүп кетиши мүмкүн. Байланыш жок кезде бул чек боюнча кайтаруу жасалбайт — байланышты күтүп, чекти текшериңиз.",
        "An online return for this receipt was cut off without a server reply: it may have gone through. Without a connection no return is made for this receipt — wait for the connection and check the receipt.",
        "Bu fiş için çevrimiçi iade sunucu yanıtı olmadan kesildi: gerçekleşmiş olabilir. Bağlantı yokken bu fişe iade yapılmaz — bağlantıyı bekleyip fişi kontrol edin.",
        "Bu chek bo'yicha onlayn qaytarish server javobisiz uzildi: o'tib ketgan bo'lishi mumkin. Aloqa yo'q paytda bu chek bo'yicha qaytarish rasmiylashtirilmaydi — aloqani kutib, chekni tekshiring.");

    public static string QueueWriteFailedText => PaymentErrorMessages.LocalStorageFailure;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> UnknownOnlineReturns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-29 (ревью, В-5): онлайн-возврат по чеку оборвался без ответа (окно возврата, ключ живёт 10 минут) — офлайн-возврат по этому же чеку закрыт на это время.</summary>
    public static void NoteUnknownOnlineReturn(string? saleId)
    {
        if (!string.IsNullOrWhiteSpace(saleId))
            UnknownOnlineReturns[saleId.Trim()] = DateTime.UtcNow;
    }

    public static void ClearUnknownOnlineReturn(string? saleId)
    {
        if (!string.IsNullOrWhiteSpace(saleId))
            UnknownOnlineReturns.TryRemove(saleId.Trim(), out _);
    }

    private static bool HasUnknownOnlineReturn(string saleRef)
    {
        var ids = new List<string> { saleRef };
        if (OfflineOperationsStore.ServerIdOf(saleRef) is { Length: > 0 } mapped)
            ids.Add(mapped);
        return ids.Any(i => UnknownOnlineReturns.TryGetValue(i, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10));
    }

    // ── поиск чека ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Чек для возврата без связи: из очереди (ещё не отправлен) или из памяти кассы (проведён раньше).</summary>
    public static FoundSale FindSale(string saleRef)
    {
        if (string.IsNullOrWhiteSpace(saleRef))
            return new(null, NotKnownLocallyText);
        var id = saleRef.Trim();

        var queued = OfflinePendingSalesStore.TryGetById(id);
        if (queued != null)
        {
            if (queued.IsAutonomous)
                return new(null, NotKnownLocallyText);
            if (string.Equals(queued.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase))
                return new(null, RejectedSaleText);
            var snap = OfflineRefundPlanner.FromQueueEntry(queued);
            return snap is null ? new(null, NotKnownLocallyText) : new(snap, null);
        }

        // Чек уже ушёл на сервер, а возврат оформляют по локальному номеру — берём серверный id из карты.
        var mapped = OfflineOperationsStore.ServerIdOf(id);
        if (!string.IsNullOrWhiteSpace(mapped))
            id = mapped!;

        var ledger = SaleReturnLedger.Get(id);
        IReadOnlyList<(string ProductName, double Quantity, double UnitPrice)>? lines = null;
        try
        {
            if (SoldLineItemsStore.LinesBySale(new[] { id }).TryGetValue(id, out var found))
                lines = found;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: локальная история продаж не прочитана для возврата: {ex.Message}", "WARNING");
        }

        var history = OfflineRefundPlanner.FromHistory(id, ledger, lines);
        if (history is null)
            return new(null, NotKnownLocallyText);
        history.ClientId = ResolveClientId(ledger);
        return new(history, null);
    }

    /// <summary>Клиент долгового чека из памяти кассы: по телефону, иначе по имени — только если он один.</summary>
    private static string? ResolveClientId(SaleLedgerEntry? ledger)
    {
        if (ledger is null || (string.IsNullOrWhiteSpace(ledger.ClientPhone) && string.IsNullOrWhiteSpace(ledger.ClientName)))
            return null;
        try
        {
            var all = OfflineClientCache.All();
            static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
            var tail = Digits(ledger.ClientPhone ?? "");
            if (tail.Length >= 6)
            {
                if (tail.Length > 9)
                    tail = tail[^9..];
                var byPhone = all.Where(c => Digits(c.Phone).EndsWith(tail, StringComparison.Ordinal)).ToList();
                if (byPhone.Count == 1)
                    return byPhone[0].Id;
            }
            // 2026-10-29 (ревью, В-6): по имени клиента не подбираем — чужой клиент с тем же именем дал бы неверный долг. Только телефон.
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: клиент чека не определён по кэшу: {ex.Message}", "WARNING");
        }
        return null;
    }

    /// <summary>Чеки, по которым можно оформить возврат без связи: очередь (не отклонённые) и недавние проведённые, новые первыми.</summary>
    public static List<OfflineSaleSnapshot> ListAvailableSales(int max = 40)
    {
        var result = new List<OfflineSaleSnapshot>();
        try
        {
            foreach (var q in OfflinePendingSalesStore.LoadPendingForSync().OrderByDescending(x => x.CreatedAt))
                if (OfflineRefundPlanner.FromQueueEntry(q) is { } snap)
                    result.Add(snap);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: чеки очереди для возврата не прочитаны: {ex.Message}", "WARNING");
        }

        try
        {
            var recent = SaleReturnLedger.Recent(TimeSpan.FromDays(14), max * 2);
            var ids = recent.Select(e => e.SaleId).ToList();
            var lines = ids.Count == 0 ? new() : SoldLineItemsStore.LinesBySale(ids);
            foreach (var e in recent)
            {
                if (!lines.TryGetValue(e.SaleId, out var ls))
                    continue;
                if (OfflineRefundPlanner.FromHistory(e.SaleId, e, ls) is { } snap)
                {
                    snap.ClientId = ResolveClientId(e);
                    result.Add(snap);
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: недавние чеки для возврата не прочитаны: {ex.Message}", "WARNING");
        }

        return result.OrderByDescending(s => s.CreatedAt).Take(max).ToList();
    }

    // ── возврат ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static OfflineRefundPlan PlanRefund(OfflineSaleSnapshot sale, IReadOnlyList<(string Key, double Quantity)> requested)
    {
        if (HasUnknownOnlineReturn(sale.SaleRef))
            return new OfflineRefundPlan { Ok = false, Error = UnknownOnlineReturnText };
        var ledger = SaleReturnLedger.Get(sale.SaleRef);
        var priors = OfflineRefundPlanner.PriorRefunds(sale.SaleRef, ledger);
        decimal? debt = null;
        if (sale.Debt > 0.005m && OfflineClientCache.Get(sale.ClientId) is { } rec)
            debt = OfflineClientCache.KnownDebt(rec, includeRejectedSales: false);
        return OfflineRefundPlanner.Plan(sale, priors, requested, debt);
    }

    /// <summary>Ставит возврат в очередь и делает всё, что кассе положено сразу: журнал возвратов, событие смены (наличные уходят из ящика), остаток.
    /// IOException — очередь не записалась: возврат НЕ оформлен, деньги не выдавать.</summary>
    public static OfflineOperationEntry CommitRefund(OfflineSaleSnapshot sale, OfflineRefundPlan plan, string? reason, string? cashierName)
    {
        if (!plan.Ok)
            throw new InvalidOperationException(plan.Error);

        // 2026-10-29 (ревью, В-9): возврат с выдачей наличных — только при открытой смене.
        if (plan.Cash > 0.005m && string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            throw new ArgumentException(NoShiftReturnText);

        // 2026-10-29 (ревью, мелочь 6): план пересчитывается под замком очереди ещё раз — два окна или долгое подтверждение не выдадут больше, чем осталось.
        var fresh = PlanRefund(sale, plan.Lines.Select(l => (l.Key, l.Quantity)).ToList());
        if (!fresh.Ok)
            throw new InvalidOperationException(fresh.Error);
        if (Math.Abs(fresh.Amount - plan.Amount) > 0.004m || fresh.Whole != plan.Whole)
            throw new InvalidOperationException(Tr.T("Данные чека изменились, пока вы подтверждали возврат. Откройте возврат заново.", "Ырастоо учурунда чектин маалыматтары өзгөрдү. Кайтарууну кайра ачыңыз.",
                "The receipt data changed while you were confirming the return. Open the return again.", "Siz iadeyi onaylarken fiş verileri değişti. İadeyi yeniden açın.", "Tasdiqlash paytida chek ma'lumotlari o'zgardi. Qaytarishni qayta oching."));
        plan = fresh;

        var op = new OfflineOperationEntry
        {
            Kind = OfflineOperationEntry.KindRefund,
            SaleRef = sale.SaleRef,
            SaleRefIsLocal = sale.IsLocal,
            ReceiptNumber = sale.ReceiptNumber,
            Lines = plan.Lines,
            Whole = plan.Whole,
            ReturnsAll = plan.Last,
            Reason = reason,
            Amount = plan.Amount,
            Cash = plan.Cash,
            NonCash = plan.NonCash,
            DebtReduced = plan.DebtReduced,
            ClientId = sale.ClientId,
            ClientName = sale.ClientName,
            ShiftId = PosApp.ActiveShiftId,
            CashierName = cashierName,
        };
        // Порядок: продажа (если ещё в очереди) → прошлые неотправленные возвраты по ней → этот возврат.
        if (sale.IsLocal)
            op.DependsOn.Add(sale.SaleRef);
        foreach (var prior in OfflineOperationsStore.RefundsForSale(sale.SaleRef).Where(p => p.Status != OfflineOperationEntry.Synced))
            op.DependsOn.Add(prior.Id);

        OfflineOperationsStore.Add(op); // исключение — наружу: возврат не оформлен

        try
        {
            // Журнал возвратов кассы: потолок «не больше оплаченного» и доля скидки для следующих возвратов (в том числе из окна возврата при связи).
            if (SaleReturnLedger.Get(sale.SaleRef) is not { Total: > 0m })
                SaleReturnLedger.RememberPayment(sale.SaleRef, sale.Total, sale.Method, sale.PaidCash, sale.PaidNonCash, null, null, sale.LinesTotal, sale.CreatedAt);
            SaleReturnLedger.AddRefund(sale.SaleRef, new SaleLedgerRefund
            {
                At = DateTimeOffset.Now,
                Amount = plan.Amount,
                Cash = plan.Cash,
                NonCash = plan.NonCash,
                DebtReduced = plan.DebtReduced,
                ServerDocId = OfflineRefundPlanner.OfflineLedgerPrefix + op.Id,
                Whole = plan.Whole,
                Lines = plan.Lines.Select(l => new SaleLedgerLine { LineId = l.Key, Name = l.Name, Quantity = l.Quantity, Total = l.Amount }).ToList(),
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: возврат {op.Id} не записан в журнал возвратов: {ex.Message}", "WARNING");
        }

        try
        {
            // Те же поля, что у возврата со связью: сумма — стоимость вернувшегося, примечание — разбивка (cash/noncash/debt) для Z-отчёта.
            var note = $"cash={SaleReturnLedger.Inv(plan.Cash)};noncash={SaleReturnLedger.Inv(plan.NonCash)};debt={SaleReturnLedger.Inv(plan.DebtReduced)};offline=1";
            ShiftEventsStore.Record(ShiftEventsStore.KindReturn, op.ShiftId, "offline:" + op.Id, (double)plan.Amount, note);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: возврат {op.Id} не записан в события смены: {ex.Message}", "WARNING");
        }

        try
        {
            var back = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in plan.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0)
                    continue;
                var per = sale.Lines.FirstOrDefault(l => l.Key == line.Key)?.StockPerQty ?? 1.0;
                back[line.ProductId!] = (back.TryGetValue(line.ProductId!, out var before) ? before : 0) + line.Quantity * (per > 0 ? per : 1.0);
            }
            if (back.Count > 0)
                StockSyncService.ApplyReturnedItemsIncrement(back);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: остаток после возврата {op.Id} не увеличен сразу: {ex.Message}", "STOCK");
        }

        PosLogger.Log($"OFFLINE: возврат без связи {op.Id} по чеку {sale.SaleRef} ({(sale.IsLocal ? "в очереди" : "на сервере")}): {plan.Amount:0.00} " +
                      $"(наличными {plan.Cash:0.00}, безнал {plan.NonCash:0.00}, долг −{plan.DebtReduced:0.00}), строк {plan.Lines.Count}.", "SALES");
        return op;
    }

    // ── оплата долга ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Что показать кассиру/что проверить до приёма оплаты долга без связи.</summary>
    public static OfflineDebtVerdict CheckDebtPayment(string clientId, decimal amount)
    {
        var rec = OfflineClientCache.Get(clientId);
        decimal? known = rec is null ? null : OfflineClientCache.KnownDebt(rec, includeRejectedSales: false);
        return OfflineDebtRules.CheckPayment(rec, amount, known, DateTimeOffset.Now);
    }

    /// <summary>Ставит оплату долга в очередь: касса должна быть со своей открытой сменой (иначе ArgumentException с текстом). Исключение
    /// IOException — очередь не записалась, деньги НЕ принимать.</summary>
    public static OfflineOperationEntry CommitDebtPayment(string clientId, decimal amount, string method, string? cashierName)
    {
        var shiftId = PosApp.ActiveShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            throw new ArgumentException(NoShiftText);
        var verdict = CheckDebtPayment(clientId, amount);
        if (verdict.Kind == OfflineDebtVerdictKind.Block)
            throw new ArgumentException(verdict.Message);

        var rec = OfflineClientCache.Get(clientId)!;
        var op = new OfflineOperationEntry
        {
            Kind = OfflineOperationEntry.KindDebtPayment,
            ClientId = rec.Id,
            ClientName = rec.Name,
            Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            Method = string.Equals(method, DebtPaymentSettings.MethodTransfer, StringComparison.OrdinalIgnoreCase) ? DebtPaymentSettings.MethodTransfer : DebtPaymentSettings.MethodCash,
            ShiftId = shiftId,
            CashierName = cashierName,
        };
        // Сначала чеки «в долг» этого клиента из очереди: оплата не должна уйти раньше долга, который она гасит.
        foreach (var sale in OfflinePendingSalesStore.LoadDebtSalesForClient(rec.Id, includeRejected: false))
            op.DependsOn.Add(sale.Id);

        OfflineOperationsStore.Add(op); // исключение — наружу

        try
        {
            ShiftEventsStore.Record(op.Method == DebtPaymentSettings.MethodTransfer ? ShiftEventsStore.KindDebtPaymentNonCash : ShiftEventsStore.KindDebtPayment,
                shiftId, "offline:" + op.Id, (double)op.Amount, rec.Name);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE: оплата долга {op.Id} не записана в события смены: {ex.Message}", "WARNING");
        }

        PosLogger.Log($"OFFLINE: оплата долга без связи {op.Id}: клиент {rec.Id}, {op.Amount:0.00} ({op.Method}), смена {shiftId}, " +
                      $"{OfflineDebtRules.DataStamp(rec.DebtAt ?? rec.UpdatedAt, DateTimeOffset.Now)}.", "PAYMENT");
        return op;
    }

    // ── подписи для окон ─────────────────────────────────────────────────────────────────────────────────────────────

    public static string StatusLabel(OfflineOperationEntry op) => op.Status switch
    {
        OfflineOperationEntry.Failed when op.LastError == OfflineOperationsStore.CanceledMarker => Tr.T("Закрыта вручную", "Колго менен жабылды", "Closed manually", "Elle kapatıldı", "Qo'lda yopildi"),
        OfflineOperationEntry.Failed when op.ReviewNeeded => Tr.T("Требует проверки", "Текшерүү керек", "Needs review", "Kontrol gerekli", "Tekshirish kerak"),
        OfflineOperationEntry.Failed => Tr.T("Ошибка отправки", "Жөнөтүү катасы", "Send error", "Gönderme hatası", "Yuborishda xato"),
        OfflineOperationEntry.Synced => Tr.T("Отправлено", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi"),
        _ => Tr.T("Ждёт отправки", "Жөнөтүүнү күтөт", "Waiting to be sent", "Gönderilmeyi bekliyor", "Yuborilishini kutmoqda"),
    };

    public static string Title(OfflineOperationEntry op)
    {
        var sum = MoneyText.Som(op.Amount);
        return op.Kind == OfflineOperationEntry.KindDebtPayment
            ? Tr.T($"Оплата долга: {op.ClientName} — {sum}", $"Карыз төлөмү: {op.ClientName} — {sum}", $"Debt payment: {op.ClientName} — {sum}",
                $"Borç ödemesi: {op.ClientName} — {sum}", $"Qarz to'lovi: {op.ClientName} — {sum}")
            : Tr.T($"Возврат по чеку {op.ReceiptNumber ?? ShortRef(op.SaleRef)} — {sum}", $"{op.ReceiptNumber ?? ShortRef(op.SaleRef)} чеги боюнча кайтаруу — {sum}",
                $"Return for receipt {op.ReceiptNumber ?? ShortRef(op.SaleRef)} — {sum}", $"{op.ReceiptNumber ?? ShortRef(op.SaleRef)} fişi iadesi — {sum}",
                $"{op.ReceiptNumber ?? ShortRef(op.SaleRef)} chek bo'yicha qaytarish — {sum}");
    }

    public static string ShortRef(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "—" : id.Length > 8 ? "№" + id[..6] : "№" + id;

    public static string Inv(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
