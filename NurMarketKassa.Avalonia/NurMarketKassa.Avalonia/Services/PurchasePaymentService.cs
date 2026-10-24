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
                }
                if (paid > 0.004)
                {
                    var (ok, text) = pay.PartialFromDrawer
                        ? DrawerWithdrawal(supplier, result.ReceiptId, paid)
                        : await CompanyExpenseAsync(supplier, result.ReceiptId, paid, pay.CashboxId, pay.CashboxName).ConfigureAwait(true);
                    summary += "\n" + text;
                    warning |= !ok;
                    note.MoneyProblem |= !ok;
                    note.MoneyNote = text;
                }
                break;
            }
            default:
            {
                note.Paid = total;
                var (ok, text) = await CheckServerExpenseAsync(result.ReceiptId, pay.Kind == PurchasePayKind.Drawer).ConfigureAwait(true);
                if (text.Length > 0)
                {
                    summary += "\n" + text;
                    note.MoneyNote = text;
                }
                warning |= !ok;
                note.MoneyProblem = !ok;
                break;
            }
        }

        var received = result.PostedLines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId))
            .GroupBy(l => l.ProductId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity), StringComparer.OrdinalIgnoreCase);
        PurchasesStore.AddReceipt(note, received);
        return new PurchaseOutcome(result, summary, warning, note);
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
    private static (bool Ok, string Text) DrawerWithdrawal(PurchaseReceivingService.Supplier supplier, string? receiptId, double paid)
    {
        try
        {
            var comment = T($"Оплата поставщику «{supplier.Name}» при приёмке", $"«{supplier.Name}» жеткирүүчүгө кабыл алууда төлөм",
                $"Payment to supplier “{supplier.Name}” on receiving", $"Mal kabulde «{supplier.Name}» tedarikçisine ödeme",
                $"Qabulda «{supplier.Name}» yetkazib beruvchiga to'lov");
            var op = new CashOperationModel
            {
                Id = string.IsNullOrWhiteSpace(receiptId) ? Guid.NewGuid().ToString("N") : receiptId.Trim(),
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
            return (true, T($"Из ящика смены выдано поставщику: {MoneyText.Som(paid)} (изъятие в смене).", $"Сменанын кассасынан жеткирүүчүгө берилди: {MoneyText.Som(paid)} (сменада акча алуу).",
                $"Paid to the supplier from the shift drawer: {MoneyText.Som(paid)} (cash out in the shift).", $"Vardiya kasasından tedarikçiye ödendi: {MoneyText.Som(paid)} (vardiyada para çıkışı).",
                $"Smena kassasidan yetkazib beruvchiga berildi: {MoneyText.Som(paid)} (smenada chiqim)."));
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
    private static async Task<(bool Ok, string Text)> CheckServerExpenseAsync(string? receiptId, bool fromDrawer)
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
            var inShift = shift.Length > 0 && string.Equals(shift, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase);
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
