using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-22 (закупки как в вебе): «Подтверждение оплаты» сайта NurCRM — «Наличные (полная оплата)» / «Предоплата» / «В долг поставщику» и
// галочка «Я заплатил из кассы (ящика) смены». Приход — тот же, что у «Приёмки» склада (PurchaseReceivingService.PostAsync), долг — тем же
// SupplierDebtRecorder (сделка-долг, как сайт, с защитой от двойного долга). Что делает каждый способ — см. PurchasePayKind.

/// <summary>Как оплачен приход.</summary>
public enum PurchasePayKind
{
    /// <summary>Оплачено полностью из кассы расходов: приход payment_type=cash, cashbox_role=expense_variable — расход пишет сервер (как раньше).</summary>
    Cash,
    /// <summary>Оплачено полностью из ящика открытой смены (касса): приход cash + cashbox_role=pos_main, cashbox и shift — расход пишет сервер;
    /// после — проверка (только чтение), попал ли расход в смену.</summary>
    Drawer,
    /// <summary>Часть сразу, остальное в долг: приход payment_type=debt (сервер денег не пишет), сделка-долг на остаток, оплаченная часть —
    /// изъятием из ящика смены (касса) или расходом из выбранной кассы компании (source_id = номер прихода, без дублей).</summary>
    Partial,
    /// <summary>В долг поставщику: приход payment_type=debt, сделка-долг на всю сумму со сроком.</summary>
    Debt,
}

public sealed record PurchasePayment(PurchasePayKind Kind, double PaidNow, int DueDays, bool PartialFromDrawer, string? CashboxId, string? CashboxName);

public sealed record PurchaseOutcome(PurchaseReceivingService.PostResult Result, string Summary, bool Warning, PurchaseReceiptNote? Note);

internal static class PurchasePaymentService
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Ящик смены доступен: это касса (не программа владельца) и смена открыта.</summary>
    public static bool DrawerAvailable => !AppMode.IsOwner && !string.IsNullOrWhiteSpace(PosApp.ActiveShiftId) && !string.IsNullOrWhiteSpace(PosApp.PosCashboxId);

    /// <summary>Провести приход и оплату. Исключений по деньгам не бросает: приход прошёл — итог с предупреждениями.</summary>
    public static async Task<PurchaseOutcome> PostAsync(IReadOnlyList<ReceivingLineVm> lines, PurchaseReceivingService.Supplier supplier, PurchasePayment pay,
        string? orderId)
    {
        var employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId;
        var paidNow = pay.Kind is PurchasePayKind.Cash or PurchasePayKind.Drawer;
        Dictionary<string, string>? fields = null;
        if (pay.Kind == PurchasePayKind.Drawer)
            fields = new Dictionary<string, string>
            {
                ["cashbox_role"] = "pos_main",
                ["cashbox"] = PosApp.PosCashboxId ?? "",
                ["shift"] = PosApp.ActiveShiftId ?? "",
            };
        PosLogger.Log($"Закупки: приход от «{supplier.Name}» ({supplier.Id}), строк {lines.Count}, оплата {pay.Kind}" +
                      (pay.Kind == PurchasePayKind.Partial ? $" — сразу {pay.PaidNow:0.00}{(pay.PartialFromDrawer ? " из ящика смены" : $" из кассы «{pay.CashboxName}»")}" : "") +
                      (pay.Kind is PurchasePayKind.Debt or PurchasePayKind.Partial ? $", срок {pay.DueDays} дн." : "") + ".", "PAYMENT");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await PurchaseReceivingService.Instance.PostAsync(lines, supplier, paidNow, employee, cts.Token, fields).ConfigureAwait(true);
        return await FinishAsync(result, supplier, pay, orderId, null).ConfigureAwait(true);
    }

    // 2026-10-25, ТЗ «оприходование в фоне» (Q4): всё после прихода вынесено из PostAsync без изменения логики — тот же код вызывает
    // и PostAsync (приход сразу, как раньше), и финишер очереди фоновых приходов (BackgroundFinisherAsync). bg == null — прежнее поведение.
    public static async Task<PurchaseOutcome> FinishAsync(PurchaseReceivingService.PostResult result, PurchaseReceivingService.Supplier supplier, PurchasePayment pay,
        string? orderId, PurchaseFinishContext? bg)
    {
        var summary = PurchaseReceivingService.SummaryText(result);
        var warning = result.Errors.Count > 0;
        if (result.PostedLines.Count == 0)
            return new PurchaseOutcome(result, summary, true, null);

        var total = Math.Round(result.ReceiptTotal is > 0 and var t ? t : result.TotalAmount, 2);
        var note = new PurchaseReceiptNote
        {
            ReceiptId = result.ReceiptId ?? "", SupplierId = supplier.Id, SupplierName = supplier.Name, Total = total, OrderId = orderId,
            PayKind = pay.Kind switch { PurchasePayKind.Cash => "cash", PurchasePayKind.Drawer => "drawer", PurchasePayKind.Partial => "partial", _ => "debt" },
            CreatedAt = result.ReceiptAt ?? DateTime.Now,
        };

        switch (pay.Kind)
        {
            case PurchasePayKind.Debt:
            {
                var debt = await SupplierDebtUi.RecordAfterReceiptAsync(supplier, result, pay.DueDays, "purchase").ConfigureAwait(true);
                note.Debt = total;
                note.DueDate = (result.ReceiptAt ?? DateTime.Now).Date.AddDays(pay.DueDays);
                if (debt is not null)
                {
                    summary += "\n" + SupplierDebtRecorder.ResultText(debt);
                    warning |= !debt.Recorded;
                    note.MoneyProblem = !debt.Recorded;
                    if (!debt.Recorded)
                        bg?.Problems.Add(SupplierDebtRecorder.ResultText(debt));
                }
                break;
            }
            case PurchasePayKind.Partial:
            {
                var paid = Math.Round(Math.Min(Math.Max(0, pay.PaidNow), total), 2);
                var rest = Math.Round(total - paid, 2);
                note.Paid = paid;
                note.Debt = rest;
                var at = result.ReceiptAt ?? DateTime.Now;
                note.DueDate = at.Date.AddDays(pay.DueDays);
                if (rest > 0.004)
                {
                    var debt = await RecordRestAsync(supplier, result, rest, at, pay.DueDays).ConfigureAwait(true);
                    summary += "\n" + SupplierDebtRecorder.ResultText(debt);
                    warning |= !debt.Recorded;
                    note.MoneyProblem |= !debt.Recorded;
                    if (!debt.Recorded)
                        bg?.Problems.Add(SupplierDebtRecorder.ResultText(debt));
                }
                if (paid > 0.004)
                {
                    // 2026-10-25 (Q4): в фоне шаг «оплачено сразу» уже мог быть выполнен (повтор финишера после обрыва) — не повторяем, берём прежний итог.
                    (bool Ok, string Text) money;
                    if (bg is not null && bg.TryGetMark("money", out var prev))
                        money = (prev.Ok, prev.Text);
                    else
                    {
                        money = pay.PartialFromDrawer
                            ? DrawerWithdrawal(supplier, result.ReceiptId, paid, bg)
                            : await CompanyExpenseAsync(supplier, result.ReceiptId, paid, pay.CashboxId, pay.CashboxName).ConfigureAwait(true);
                        bg?.SetMark("money", money.Ok, money.Text);
                    }
                    var (ok, text) = money;
                    summary += "\n" + text;
                    warning |= !ok;
                    note.MoneyProblem |= !ok;
                    note.MoneyNote = text;
                    if (!ok)
                        bg?.Problems.Add(text);
                }
                break;
            }
            default:
            {
                note.Paid = total;
                var (ok, text) = await CheckServerExpenseAsync(result.ReceiptId, pay.Kind == PurchasePayKind.Drawer, bg?.ShiftId).ConfigureAwait(true);
                if (text.Length > 0)
                {
                    summary += "\n" + text;
                    note.MoneyNote = text;
                }
                warning |= !ok;
                note.MoneyProblem = !ok;
                if (!ok && text.Length > 0)
                    bg?.Problems.Add(text);
                break;
            }
        }

        var received = result.PostedLines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId))
            .GroupBy(l => l.ProductId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);
        // 2026-10-25 (Q4): запись в «Закупки» — один раз (отметка «note»; и по самой записи с этим номером прихода): иначе принятое по заказу задвоится.
        if (bg is null || !(bg.TryGetMark("note", out _) || (note.ReceiptId.Length > 0 && PurchasesStore.ReceiptNotes().ContainsKey(note.ReceiptId))))
        {
            PurchasesStore.AddReceipt(note, received);
            bg?.SetMark("note", true, "");
        }
        return new PurchaseOutcome(result, summary, warning, note);
    }

    /// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q4): финишер очереди фоновых приходов (имя «purchases») — всё после прихода: долг, расход или
    /// изъятие, запись в «Закупки». Деньги считает тот же FinishAsync, что и раньше. Повтор (обрыв, перезапуск) не задвоит ни расход, ни изъятие, ни запись:
    /// отметки шагов в задаче (PurchaseFinishMarks) + прежние защиты (поиск расхода по номеру прихода, номер изъятия = номер прихода, журнал долгов).
    /// Всегда Done: денежные замечания (смена сменилась, расход не записан) идут в Errors — кассир видит их в итоге, ничего не повторяется вслепую.</summary>
    public static async Task<FinisherResult> BackgroundFinisherAsync(ReceivingJob job, PurchaseReceivingService.PostResult result, CancellationToken ct)
    {
        PurchaseFinishPayload? p = null;
        try
        {
            p = string.IsNullOrWhiteSpace(job.FinisherPayload) ? null : JsonSerializer.Deserialize<PurchaseFinishPayload>(job.FinisherPayload);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки в фоне: данные оплаты задачи {job.Id} не прочитаны ({ex.Message}).", "WARNING");
        }
        if (p is null || job.Supplier is null || !Enum.TryParse<PurchasePayKind>(p.Kind, out var kind))
            return new FinisherResult(true, null, new[]
            {
                T("Приход проведён, но данные оплаты закупки не прочитаны — оформите оплату или долг вручную.", "Кирүү өткөрүлдү, бирок сатып алуунун төлөм маалыматы окулган жок — төлөмдү же карызды кол менен жасаңыз.",
                    "The delivery is posted, but the purchase payment data couldn't be read — record the payment or debt manually.", "Mal kabulü işlendi ancak alım ödeme verisi okunamadı — ödemeyi veya borcu elle yapın.",
                    "Kirim o'tkazildi, lekin xarid to'lovi ma'lumoti o'qilmadi — to'lov yoki qarzni qo'lda rasmiylashtiring."),
            });
        var supplier = job.Supplier.ToSupplier();
        var pay = new PurchasePayment(kind, p.PaidNow, p.DueDays, p.PartialFromDrawer, p.CashboxId, p.CashboxName);
        var ctx = new PurchaseFinishContext(job.Id, p.ShiftId);
        ct.ThrowIfCancellationRequested();
        PosLogger.Log($"Закупки в фоне: задача {job.Id}, оплата {kind}, смена при постановке {p.ShiftId ?? "—"}, сейчас {PosApp.ActiveShiftId ?? "—"}.", "PAYMENT");
        var outcome = await FinishAsync(result, supplier, pay, p.OrderId, ctx).ConfigureAwait(true);
        var head = PurchaseReceivingService.SummaryText(result);
        var extra = outcome.Summary.StartsWith(head, StringComparison.Ordinal) ? outcome.Summary[head.Length..].Trim() : outcome.Summary;
        return new FinisherResult(true, extra.Length > 0 ? extra : null, ctx.Problems.Count > 0 ? ctx.Problems : null);
    }

    /// <summary>Остаток «Предоплаты» — в долг: та же запись сделки-долга, что у прихода «в долг», но на остаток (метка — номер прихода).</summary>
    private static async Task<SupplierDebtResult> RecordRestAsync(PurchaseReceivingService.Supplier supplier, PurchaseReceivingService.PostResult result,
        double rest, DateTime at, int dueDays)
    {
        var receiptId = result.ReceiptId?.Trim() ?? "";
        var marker = receiptId.Length > 0 ? SupplierDebtRecorder.MarkerForReceipt(receiptId)
            : result.ReceiptKey is { Length: > 0 } key ? SupplierDebtRecorder.MarkerForKey(key) : "";
        if (marker.Length == 0)
            return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, rest, T("сервер не вернул номер прихода", "сервер кириштин номерин кайтарган жок",
                "the server didn't return the delivery number", "sunucu giriş numarasını döndürmedi", "server kirim raqamini qaytarmadi"));
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var days = Math.Clamp(dueDays, 1, 3650);
            return await SupplierDebtRecorder.RecordAsync(api, new SupplierDebtRequest(supplier.Id, supplier.Name, marker, receiptId, rest, at,
                at.Date.AddDays(days), days, "purchase"), cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: остаток {rest:0.00} в долг «{supplier.Name}» не записан ({ex.GetType().Name}: {ex.Message}).", "ERROR");
            return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, rest, ex.Message);
        }
    }

    /// <summary>Оплаченная часть — из ящика смены: обычное «Изъятие» смены (тот же путь, что кнопка «Изъятие»: смена, Z-отчёт, сервер
    /// shift_drawer_outflow), номер операции = номер прихода — повторная отправка не задвоит.</summary>
    private static (bool Ok, string Text) DrawerWithdrawal(PurchaseReceivingService.Supplier supplier, string? receiptId, double paid, PurchaseFinishContext? bg = null)
    {
        try
        {
            // 2026-10-25 (Q4, фон): изъятие пишется в ту смену, что была при постановке прихода. Смена сменилась (закрыта/открыта другая) — НЕ пишем
            // автоматически (сумма ушла бы в чужой Z-отчёт): «проверьте вручную». Повтор после обрыва: операция с этим номером уже есть — второй раз не пишем.
            var opId = !string.IsNullOrWhiteSpace(receiptId) ? receiptId.Trim() : bg is not null ? "pp-" + bg.JobId : Guid.NewGuid().ToString("N");
            string DoneText() => T($"Из ящика смены выдано поставщику: {MoneyText.Som(paid)} (изъятие в смене).", $"Сменанын кассасынан жеткирүүчүгө берилди: {MoneyText.Som(paid)} (сменада акча алуу).",
                $"Paid to the supplier from the shift drawer: {MoneyText.Som(paid)} (cash out in the shift).", $"Vardiya kasasından tedarikçiye ödendi: {MoneyText.Som(paid)} (vardiyada para çıkışı).",
                $"Smena kassasidan yetkazib beruvchiga berildi: {MoneyText.Som(paid)} (smenada chiqim).");
            if (bg is not null && ShiftCashOperationsStore.LoadAll().Any(o => string.Equals(o.Id, opId, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"Закупки: изъятие {opId} уже записано — второй раз не пишем.", "PAYMENT");
                return (true, DoneText());
            }
            if (bg is not null && !string.Equals(bg.ShiftId ?? "", PosApp.ActiveShiftId ?? "", StringComparison.OrdinalIgnoreCase))
            {
                PosLogger.Log($"Закупки: изъятие {paid:0.00} для «{supplier.Name}» не записано — смена изменилась (была {bg.ShiftId}, сейчас {PosApp.ActiveShiftId}).", "WARNING");
                return (false, T("Смена сменилась, пока приход проводился в фоне — изъятие НЕ записано. ", "Приход фондо өткөрүлүп жатканда смена өзгөрдү — акча алуу жазылган жок. ",
                    "The shift changed while the delivery was being posted in the background — the cash-out was NOT recorded. ", "Mal kabulü arka planda işlenirken vardiya değişti — para çıkışı KAYDEDİLMEDİ. ",
                    "Kirim fonda o'tkazilayotganda smena almashdi — chiqim YOZILMADI. ")
                    + T($"Оформите «Изъятие» {MoneyText.Som(paid)} в операциях смены вручную.", $"Сменанын операцияларында {MoneyText.Som(paid)} «Акча алууну» кол менен жасаңыз.",
                    $"Make a “Cash out” of {MoneyText.Som(paid)} in the shift operations manually.", $"Vardiya işlemlerinde {MoneyText.Som(paid)} «Para çıkışı»nı elle yapın.",
                    $"Smena amallarida {MoneyText.Som(paid)} «Chiqim»ni qo'lda bajaring."));
            }
            var comment = T($"Оплата поставщику «{supplier.Name}» при приёмке", $"«{supplier.Name}» жеткирүүчүгө кабыл алууда төлөм",
                $"Payment to supplier “{supplier.Name}” on receiving", $"Mal kabulde «{supplier.Name}» tedarikçisine ödeme",
                $"Qabulda «{supplier.Name}» yetkazib beruvchiga to'lov");
            var op = new CashOperationModel
            {
                Id = opId,
                CreatedAt = DateTime.Now,
                Type = "Изъятие",
                Kind = CashOperationKind.Withdrawal,
                Amount = (decimal)paid,
                Cashier = App.CurrentUserId ?? "—",
                Reason = T("Оплата поставщику", "Жеткирүүчүгө төлөм", "Payment to supplier", "Tedarikçiye ödeme", "Yetkazib beruvchiga to'lov"),
                Comment = comment,
                // Статья изъятия (CashArticles, 22.10) — «Закупка товара / оплата поставщику»: в отчёте смены и на сервере (category).
                ArticleKey = "supplier",
                ArticleRu = "Закупка товара / оплата поставщику",
                ArticleComment = supplier.Name,
            };
            ShiftCashOperationsStore.Append(op);
            ShiftEventsStore.Record(ShiftEventsStore.KindExpense, PosApp.ActiveShiftId, op.Id, paid, comment);
            _ = ShiftCashFlowSync.PushPendingAsync(PosApp.ActiveShiftId);
            PosLogger.Log($"Закупки: оплачено поставщику «{supplier.Name}» из ящика смены {paid:0.00} (изъятие {op.Id}).", "PAYMENT");
            return (true, DoneText());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: изъятие {paid:0.00} для «{supplier.Name}» не записано ({ex.Message}).", "ERROR");
            return (false, T($"Оплата {MoneyText.Som(paid)} из ящика не записана — оформите «Изъятие» в операциях смены вручную.",
                $"Кассадан {MoneyText.Som(paid)} төлөм жазылган жок — сменанын операцияларында «Акча алууну» кол менен жасаңыз.",
                $"The {MoneyText.Som(paid)} payment from the drawer wasn't recorded — make a “Cash out” in the shift operations manually.",
                $"Kasadan {MoneyText.Som(paid)} ödeme kaydedilmedi — vardiya işlemlerinde «Para çıkışı»nı elle yapın.",
                $"Kassadan {MoneyText.Som(paid)} to'lov yozilmadi — smena amallarida «Chiqim»ni qo'lda bajaring."));
        }
    }

    /// <summary>Оплаченная часть — расход из кассы компании (программа владельца): POST construction/cashflows/ с source_id = номер прихода.
    /// Перед отправкой и после обрыва — поиск расхода с этим source_id (GET), второй не создаётся.</summary>
    private static async Task<(bool Ok, string Text)> CompanyExpenseAsync(PurchaseReceivingService.Supplier supplier, string? receiptId, double paid,
        string? cashboxId, string? cashboxName)
    {
        string Failed(string why) => T($"Оплата {MoneyText.Som(paid)} поставщику не записана в кассу ({why}) — запишите расход на сайте NurCRM вручную.",
            $"Жеткирүүчүгө {MoneyText.Som(paid)} төлөм кассага жазылган жок ({why}) — чыгымды NurCRM сайтында кол менен жазыңыз.",
            $"The {MoneyText.Som(paid)} payment to the supplier wasn't recorded in the till ({why}) — record the expense on the NurCRM website manually.",
            $"Tedarikçiye {MoneyText.Som(paid)} ödeme kasaya kaydedilmedi ({why}) — gideri NurCRM sitesinde elle kaydedin.",
            $"Yetkazib beruvchiga {MoneyText.Som(paid)} to'lov kassaga yozilmadi ({why}) — xarajatni NurCRM saytida qo'lda yozing.");
        if (string.IsNullOrWhiteSpace(receiptId))
            return (false, Failed(T("нет номера прихода", "кириштин номери жок", "no delivery number", "giriş numarası yok", "kirim raqami yo'q")));
        if (string.IsNullOrWhiteSpace(cashboxId))
            return (false, Failed(T("касса не выбрана", "касса тандалган жок", "no till chosen", "kasa seçilmedi", "kassa tanlanmagan")));
        var done = T($"Оплачено поставщику {MoneyText.Som(paid)} из кассы «{cashboxName}».", $"Жеткирүүчүгө {MoneyText.Som(paid)} «{cashboxName}» кассасынан төлөндү.",
            $"Paid {MoneyText.Som(paid)} to the supplier from the “{cashboxName}” till.", $"Tedarikçiye «{cashboxName}» kasasından {MoneyText.Som(paid)} ödendi.",
            $"Yetkazib beruvchiga «{cashboxName}» kassasidan {MoneyText.Som(paid)} to'landi.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (await NurCrmReportsApi.CashFlowsBySourceIdAsync(receiptId, cts.Token).ConfigureAwait(true) is { Count: > 0 } known)
            {
                PosLogger.Log($"Закупки: расход по приходу {receiptId} уже есть ({known[0].Id}) — второй не создаём.", "PAYMENT");
                return (true, done);
            }
            var body = new Dictionary<string, string>
            {
                ["cashbox"] = cashboxId.Trim(),
                ["type"] = "expense",
                ["name"] = $"Закупка: {supplier.Name} (оплата при приёмке)",
                ["amount"] = paid.ToString("0.00", CultureInfo.InvariantCulture),
                ["status"] = SalaryPayoutService.ApprovedRightAway ? "approved" : "pending",
                ["source_id"] = receiptId.Trim(),
                ["idempotency_key"] = "purchase-pay-" + receiptId.Trim(),
            };
            try
            {
                var created = await PosApp.ShiftApi.ConstructionCashFlowCreateAsync(body, cts.Token).ConfigureAwait(true);
                PosLogger.Log($"Закупки: расход {paid:0.00} поставщику «{supplier.Name}» из кассы «{cashboxName}» записан ({SupplierApiServiceStr(created, "id")}, статус {SupplierApiServiceStr(created, "status")}).", "PAYMENT");
                return (true, done);
            }
            catch (Exception ex) when (PendingIdempotencyKey.IsOutcomeUnknown(ex))
            {
                // Ответ не дошёл — расход мог записаться: не повторяем вслепую, ищем его.
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(true);
                using var again = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (await NurCrmReportsApi.CashFlowsBySourceIdAsync(receiptId, again.Token).ConfigureAwait(true) is { Count: > 0 })
                    return (true, done);
                PosLogger.Log($"Закупки: исход расхода {paid:0.00} по приходу {receiptId} неизвестен ({ex.Message}).", "WARNING");
                return (false, Failed(T("сервер не ответил — проверьте «Деньги» на сайте, не повторяйте", "сервер жооп берген жок — сайттан текшериңиз, кайталабаңыз",
                    "the server didn't respond — check the website, don't repeat", "sunucu yanıt vermedi — siteden kontrol edin, tekrarlamayın",
                    "server javob bermadi — saytdan tekshiring, takrorlamang")));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: расход {paid:0.00} по приходу {receiptId} не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return (false, Failed(ServerTelegramBotApi.DescribeFields(ex)));
        }
    }

    /// <summary>Оплачено сразу: какой расход записал сервер на приход (GET construction/cashflows/?source_id=номер прихода, только чтение) —
    /// касса и смена для подсказки. Из ящика смены, а расход без этой смены — предупреждение (ящик и Z-отчёт его не видят).</summary>
    private static async Task<(bool Ok, string Text)> CheckServerExpenseAsync(string? receiptId, bool fromDrawer, string? expectedShift = null)
    {
        if (string.IsNullOrWhiteSpace(receiptId))
            return (true, "");
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var data = await api.RequestAsync(HttpMethod.Get, "api/construction/cashflows/", null,
                new Dictionary<string, string> { ["source_id"] = receiptId.Trim() }, cts.Token, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var rows = NurMarketApiClient.UnwrapList(data)
                .Where(r => string.Equals(SupplierApiServiceStr(r, "source_id"), receiptId.Trim(), StringComparison.OrdinalIgnoreCase)
                            && SupplierApiServiceStr(r, "type") == "expense").ToList();
            if (rows.Count == 0)
            {
                PosLogger.Log($"Закупки: расход по приходу {receiptId} на сервере не найден (оплата {(fromDrawer ? "из ящика" : "из кассы расходов")}).", "WARNING");
                return fromDrawer
                    ? (false, T("Сервер не показал расход на этот приход — в ящике смены он не учтён. Сообщите владельцу; повторно не проводите.",
                        "Сервер бул кирүүгө чыгымды көрсөткөн жок — сменанын кассасында эсепке алынган жок. Ээсине айтыңыз; кайра өткөрбөңүз.",
                        "The server showed no expense for this delivery — the shift drawer doesn't account for it. Tell the owner; don't post it again.",
                        "Sunucu bu giriş için gider göstermedi — vardiya kasasında hesaba katılmadı. Sahibine söyleyin; tekrar işlemeyin.",
                        "Server bu kirim uchun xarajatni ko'rsatmadi — smena kassasida hisobga olinmadi. Egasiga ayting; qayta o'tkazmang."))
                    : (true, "");
            }
            var row = rows[0];
            var cashbox = SupplierApiServiceStr(row, "cashbox_name");
            var shift = SupplierApiServiceStr(row, "shift");
            var inShift = shift.Length > 0 && string.Equals(shift, expectedShift ?? PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase);
            PosLogger.Log($"Закупки: расход по приходу {receiptId}: {SupplierApiServiceStr(row, "amount")} из «{cashbox}», смена {(shift.Length > 0 ? shift : "—")}.", "PAYMENT");
            if (fromDrawer && !inShift)
                return (false, T($"Сервер записал расход в кассу «{cashbox}» без этой смены — в ящике смены его нет. При закрытии смены будет излишек на эту сумму; сообщите владельцу.",
                    $"Сервер чыгымды «{cashbox}» кассасына бул сменасыз жазды — сменанын кассасында жок. Сменаны жапканда ушул суммага ашыкча болот; ээсине айтыңыз.",
                    $"The server recorded the expense in the “{cashbox}” till without this shift — the shift drawer doesn't have it. Closing the shift will show a surplus of this amount; tell the owner.",
                    $"Sunucu gideri bu vardiya olmadan «{cashbox}» kasasına kaydetti — vardiya kasasında yok. Vardiya kapanışında bu tutarda fazla çıkar; sahibine söyleyin.",
                    $"Server xarajatni bu smenasiz «{cashbox}» kassasiga yozdi — smena kassasida yo'q. Smena yopilganda shu summaga ortiqcha chiqadi; egasiga ayting."));
            return (true, inShift
                ? T("Списано из ящика смены.", "Сменанын кассасынан чыгарылды.", "Taken from the shift drawer.", "Vardiya kasasından düşüldü.", "Smena kassasidan chiqarildi.")
                : T($"Списано из кассы «{cashbox}».", $"«{cashbox}» кассасынан чыгарылды.", $"Taken from the “{cashbox}” till.", $"«{cashbox}» kasasından düşüldü.", $"«{cashbox}» kassasidan chiqarildi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: расход по приходу {receiptId} не проверен ({ex.Message}).", "WARNING");
            return (true, "");
        }
    }

    private static string SupplierApiServiceStr(JsonElement e, string name) => NurMarketKassa.Services.Api.SupplierApiService.Str(e, name);
}

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q4): данные оплаты закупки, которые окно кладёт в задачу очереди (FinisherPayload, JSON).
/// ShiftId — смена на момент постановки: изъятие из ящика пишется только в неё.</summary>
internal sealed record PurchaseFinishPayload(string Kind, double PaidNow, int DueDays, bool PartialFromDrawer, string? CashboxId, string? CashboxName,
    string? OrderId, string? ShiftId)
{
    public static string From(PurchasePayment pay, string? orderId, string? shiftId) =>
        JsonSerializer.Serialize(new PurchaseFinishPayload(pay.Kind.ToString(), pay.PaidNow, pay.DueDays, pay.PartialFromDrawer, pay.CashboxId, pay.CashboxName, orderId, shiftId));
}

/// <summary>Контекст финишера: номер задачи (ключ отметок), смена постановки, замечания по деньгам, отметки выполненных шагов.</summary>
internal sealed class PurchaseFinishContext
{
    public PurchaseFinishContext(string jobId, string? shiftId)
    {
        JobId = jobId;
        ShiftId = shiftId;
    }

    public string JobId { get; }
    public string? ShiftId { get; }
    public List<string> Problems { get; } = new();

    public bool TryGetMark(string step, out PurchaseStepMark mark) => PurchaseFinishMarks.TryGet(JobId, step, out mark);

    public void SetMark(string step, bool ok, string text) => PurchaseFinishMarks.Set(JobId, step, new PurchaseStepMark(ok, text));
}

internal sealed record PurchaseStepMark(bool Ok, string Text);

/// <summary>Отметки выполненных шагов финишера «purchases» по номеру задачи (расход/изъятие, запись в «Закупки»): повтор шага после обрыва их не задвоит.
/// Файл рядом с очередью (background-receipts\purchases-finisher.json), запись атомарная; записи старше 30 дней убираются.</summary>
internal static class PurchaseFinishMarks
{
    private sealed class Entry
    {
        public DateTime At { get; set; }
        public Dictionary<string, PurchaseStepMark> Steps { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static string Dir => BackgroundReceivingQueue.DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "background-receipts");
    private static string FilePath => Path.Combine(Dir, "purchases-finisher.json");

    private static Dictionary<string, Entry> Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, Entry>();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new Dictionary<string, Entry>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки в фоне: файл отметок шагов не прочитан ({ex.Message}) — начат заново (расход/изъятие защищены поиском по номеру прихода).", "WARNING");
            try
            {
                File.Move(FilePath, Path.Combine(Dir, $"purchases-finisher.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
            }
            catch
            {
            }
            return new Dictionary<string, Entry>();
        }
    }

    public static bool TryGet(string jobId, string step, out PurchaseStepMark mark)
    {
        lock (Sync)
        {
            if (Read().TryGetValue(jobId, out var e) && e.Steps.TryGetValue(step, out var m) && m is not null)
            {
                mark = m;
                return true;
            }
        }
        mark = new PurchaseStepMark(false, "");
        return false;
    }

    public static void Set(string jobId, string step, PurchaseStepMark mark)
    {
        lock (Sync)
        {
            try
            {
                var all = Read();
                foreach (var old in all.Where(kv => (DateTime.Now - kv.Value.At).TotalDays > 30).Select(kv => kv.Key).ToList())
                    all.Remove(old);
                if (!all.TryGetValue(jobId, out var e))
                    all[jobId] = e = new Entry { At = DateTime.Now };
                e.Steps[step] = mark;
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Закупки в фоне: отметка шага «{step}» задачи {jobId} не записана ({ex.Message}).", "WARNING");
            }
        }
    }
}

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q4): регистрирует финишер «purchases» очереди фоновых приходов при загрузке сборки (как NurCrmReportsApiSetup —
/// без правки общего запуска): очередь на старте программы может встретить недопроведённую закупку раньше, чем кассир откроет окно «Закупок».</summary>
internal static class PurchaseFinisherSetup
{
    internal static void Register() =>
        BackgroundReceivingQueue.Instance.RegisterFinisher("purchases", (job, result, ct) => PurchasePaymentService.BackgroundFinisherAsync(job, result, ct));

#pragma warning disable CA2255 // инициализатор модуля здесь намеренный: только регистрирует делегат
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => Register();
#pragma warning restore CA2255
}
