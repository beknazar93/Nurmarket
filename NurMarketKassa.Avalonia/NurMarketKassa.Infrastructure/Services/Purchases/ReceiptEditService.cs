using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-30, редактор накладных закупок («Б-лайт», ТЗ 3.4/Б2, пока у сервера нет PATCH прихода): правим только КОЛИЧЕСТВО.
//  • уменьшить — возврат поставщику по строке прихода (receipt_id + receipt_item_id), причина «Исправление документа», с ключом повтора;
//  • увеличить — доприход того же поставщика тем же способом оплаты (наличные из основной кассы / в долг) с пометкой «дополнение к ПР-…»;
//  • меньше проданного нельзя (остаток не уходит в минус), перед отправкой приход перечитывается: изменили с другого ПК — ничего не проводим.
// Каждый шаг пишется в журнал (ReceiptEditStore) ДО отправки с собственным ключом; обрыв — повтор тем же ключом (Resume).

/// <summary>Что нужно серверу от правки. В приложении — <see cref="ApiReceiptEditServer"/>, в мини-прогоне — подмена.</summary>
public interface IReceiptEditServer
{
    Task<SupplierReceipt> GetReceiptAsync(string receiptId, CancellationToken ct);

    /// <summary>POST suppliers/{id}/returns/ с Idempotency-Key. Возвращает id возврата (или null, если сервер не вернул).</summary>
    Task<string?> PostReturnAsync(string supplierId, IDictionary<string, object?> body, string key, CancellationToken ct);

    /// <summary>POST suppliers/{id}/receipt/ (body.idempotency_key). Возвращает id нового прихода и время.</summary>
    Task<(string? Id, DateTime? At)> PostReceiptAsync(string supplierId, IDictionary<string, object?> body, string key, CancellationToken ct);

    Task RecordDebtAsync(string supplierId, string supplierName, string receiptId, double amount, DateTime at, CancellationToken ct);

    // 2026-10-30 (приёмка 1.17.66, БЛОКЕР): перед повтором шага, ответ на который потерян, ищем уже проведённый документ (как PurchaseReceivingService.FindPostedReceiptAsync):
    // сервер может не смотреть idempotency_key в теле, и второй такой же POST провёл бы доприход/возврат дважды.

    /// <summary>GET main/idempotency/{key}/: true — сервер знает ключ (шаг проведён), false — нет, null — сервер так не отвечает или нет связи. Нашёл — повтор не шлём.</summary>
    Task<bool?> KeyKnownAsync(string key, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>2026-10-30 (приёмка 1.17.67, «Важно» 4): есть ли уже проведённый возврат поставщику по этому шагу. Узнаётся по метке шага в комментарии
    /// (<see cref="ReceiptEditMatcher"/>), запасной путь — тот же комментарий/состав в окне времени; похожий документ без метки — Ambiguous (повтор не шлём).
    /// Бросает при ошибке чтения — тогда повтор не отправляется.</summary>
    Task<PostedLookup> FindPostedReturnAsync(string supplierId, string sourceReceiptId, StepMatch match, string docNumber, CancellationToken ct) =>
        throw new NotSupportedException("поиск проведённого возврата не поддерживается");

    /// <summary>То же для доприхода того же поставщика (не занятого другими шагами).</summary>
    Task<PostedLookup> FindPostedAddendumAsync(string supplierId, StepMatch match, string docNumber, CancellationToken ct) =>
        throw new NotSupportedException("поиск проведённого доприхода не поддерживается");

    /// <summary>Наибольшая версия правки этого прихода, найденная в комментариях возвратов и приходов поставщика на сервере (метка «[nm:код-прихода:vN:…]», для возвратов —
    /// ещё по самому приходу-источнику). Ищется по серверному id прихода, а не по номеру документа (у каждого ПК своя нумерация). 0 — не найдено. Бросает, если просмотреть не удалось.</summary>
    Task<int> RemoteEditVersionAsync(string supplierId, string docNumber, string receiptId, DateTime? receiptAt, CancellationToken ct) => Task.FromResult(0);
}

public sealed class ApiReceiptEditServer : IReceiptEditServer
{
    private readonly NurMarketApiClient _api;
    private readonly SupplierApiService _suppliers;

    public ApiReceiptEditServer(NurMarketApiClient api)
    {
        _api = api;
        _suppliers = new SupplierApiService(api);
    }

    public Task<SupplierReceipt> GetReceiptAsync(string receiptId, CancellationToken ct) => _suppliers.GetReceiptAsync(receiptId, ct);

    public Task<string?> PostReturnAsync(string supplierId, IDictionary<string, object?> body, string key, CancellationToken ct) =>
        _suppliers.PostReturnAsync(supplierId, body, key, ct);

    public async Task<(string? Id, DateTime? At)> PostReceiptAsync(string supplierId, IDictionary<string, object?> body, string key, CancellationToken ct)
    {
        body["idempotency_key"] = key;
        var response = await PosApp.CatalogApi.CreateSupplierReceiptAsync(supplierId, body, ct).ConfigureAwait(false);
        return (SupplierApiService.Str(response, "id") is { Length: > 0 } id ? id : null, SupplierApiService.Date(SupplierApiService.Str(response, "created_at")));
    }

    public async Task<bool?> KeyKnownAsync(string key, CancellationToken ct)
    {
        var outcome = await new SalesApiService(_api).PosIdempotencyLookupAsync(key, ct).ConfigureAwait(false);
        return outcome switch { IdempotencyOutcome.Found => true, IdempotencyOutcome.NotFound => false, _ => null };
    }

    public async Task<PostedLookup> FindPostedReturnAsync(string supplierId, string sourceReceiptId, StepMatch match, string docNumber, CancellationToken ct)
    {
        var candidates = (await _suppliers.GetReturnsAsync(supplierId, ct).ConfigureAwait(false))
            .Where(r => r.ReceiptId.Length == 0 || string.Equals(r.ReceiptId, sourceReceiptId, StringComparison.OrdinalIgnoreCase))
            // без прихода-источника или строк состав сравнить нечем (NaN) — такой возврат узнаётся только по метке/комментарию
            .Select(r => new PostedCandidate(r.Id, r.Comment, r.CreatedAt, Array.Empty<string>(), r.ReceiptId.Length > 0 && r.Lines.Count > 0 ? r.Lines.Sum(l => l.Qty) : double.NaN))
            .ToList();
        return ReceiptEditMatcher.Classify(candidates, match, isReturn: true, docNumber);
    }

    public async Task<PostedLookup> FindPostedAddendumAsync(string supplierId, StepMatch match, string docNumber, CancellationToken ct)
    {
        var candidates = new List<PostedCandidate>();
        var covered = false;
        for (var page = 1; page <= 8 && !covered; page++)
        {
            var data = await PosApp.CatalogApi.ListSupplierReceiptsAsync(page, 50, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts) || receipts.ValueKind != JsonValueKind.Array)
            {
                covered = true;
                break;
            }
            foreach (var receipt in receipts.EnumerateArray())
            {
                var at = SupplierApiService.Date(SupplierApiService.Str(receipt, "created_at"));
                if (at is { } seen && seen < match.Since)
                    covered = true; // дошли до приходов старше окна шага — дальше смотреть нечего
                var sid = SupplierApiService.Str(receipt, "supplier") is { Length: > 0 } s1 ? s1 : SupplierApiService.Str(receipt, "supplier_id");
                if (sid.Length > 0 && !string.Equals(sid, supplierId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = receipt.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array
                    ? rows.EnumerateArray().Select(i => ReceiptEditMatcher.Part(SupplierApiService.Str(i, "product_id") is { Length: > 0 } p ? p : SupplierApiService.Str(i, "product"),
                        SupplierApiService.Num(i, "qty"), SupplierApiService.Num(i, "purchase_price"))).ToList()
                    : new List<string>();
                candidates.Add(new PostedCandidate(SupplierApiService.Str(receipt, "id"), SupplierApiService.Str(receipt, "comment"), at, parts, 0));
            }
            if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                covered = true;
        }
        // Не дошли до окна шага (очень много приходов) — «не найдено» было бы догадкой: повтор не отправляем.
        if (!covered)
            throw new InvalidOperationException(Tr.T("не удалось просмотреть приходы за нужное время (их слишком много)", "керектүү убакыттагы кирүүлөрдү карап чыгуу мүмкүн болгон жок (өтө көп)",
                "could not scan the receipts for the needed period (too many)", "gerekli süredeki girişler taranamadı (çok fazla)", "kerakli davrdagi kirimlarni ko'rib chiqib bo'lmadi (juda ko'p)"));
        return ReceiptEditMatcher.Classify(candidates, match, isReturn: false, docNumber);
    }

    public async Task<int> RemoteEditVersionAsync(string supplierId, string docNumber, string receiptId, DateTime? receiptAt, CancellationToken ct)
    {
        var max = 0;
        foreach (var r in await _suppliers.GetReturnsAsync(supplierId, ct).ConfigureAwait(false))
        {
            max = Math.Max(max, ReceiptEditMatcher.VersionFromMark(r.Comment, receiptId));
            // возврат ЭТОГО прихода (по id, а не по номеру) со старой пометкой «…ПР-…, версия N» — тоже правка этого документа
            if (r.ReceiptId.Length > 0 && string.Equals(r.ReceiptId, receiptId, StringComparison.OrdinalIgnoreCase))
                max = Math.Max(max, ReceiptEditService.EditVersionFromComment(r.Comment, docNumber));
        }
        var covered = false;
        for (var page = 1; page <= 6 && !covered; page++)
        {
            var data = await PosApp.CatalogApi.ListSupplierReceiptsAsync(page, 50, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts) || receipts.ValueKind != JsonValueKind.Array)
            {
                covered = true;
                break;
            }
            foreach (var receipt in receipts.EnumerateArray())
            {
                max = Math.Max(max, ReceiptEditMatcher.VersionFromMark(SupplierApiService.Str(receipt, "comment"), receiptId));
                if (receiptAt is { } origin && SupplierApiService.Date(SupplierApiService.Str(receipt, "created_at")) is { } at && at < origin.AddDays(-1))
                    covered = true; // дальше — приходы старше самого документа: правок там быть не может
            }
            if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                covered = true;
        }
        if (!covered)
            throw new InvalidOperationException(Tr.T("просмотрены не все приходы поставщика", "жеткирүүчүнүн баардык кирүүлөрү каралган жок", "not all of the supplier's receipts were scanned", "tedarikçinin tüm girişleri taranmadı", "yetkazib beruvchining barcha kirimlari ko'rib chiqilmadi"));
        return max;
    }

    public async Task RecordDebtAsync(string supplierId, string supplierName, string receiptId, double amount, DateTime at, CancellationToken ct)
    {
        var req = new SupplierDebtRequest(supplierId, supplierName, SupplierDebtRecorder.MarkerForReceipt(receiptId), receiptId, Math.Round(amount, 2), at, at.Date.AddDays(30), 30, "receipt-edit");
        var result = await SupplierDebtRecorder.RecordAsync(_api, req, ct).ConfigureAwait(false);
        if (!result.Recorded)
            throw new InvalidOperationException(result.Error ?? "долг не записан");
    }
}

public sealed record EditRequest(
    SupplierReceipt Receipt, string DocNumber, ReceiptPayKind Pay, IReadOnlyList<EditLineInput> Inputs, IReadOnlyDictionary<string, double> NewQty,
    string Reason, string By, string Device, string Compensation, string? CashboxId,
    bool IsOwner, ReceiptEditWindow Window, string BaseFingerprint, int BaseVersion, int RevertedVersion = 0, bool InQueue = false, string SupplierName = "");

public sealed record EditResult(bool Ok, int Version, string? Error, bool Conflict, bool Partial, ReceiptEditRecord? Record, ReceiptEditPlan? Plan, bool NeedsManualCheck = false);

public sealed class ReceiptEditService
{
    private readonly IReceiptEditServer _server;
    private readonly ReceiptEditStore _store;
    private readonly Func<DateTime> _now;

    /// <summary>Правка проведена (полностью или частично): запись в журнал аудита и уведомление владельцу (Telegram). Мини-прогон отключает.</summary>
    public static Action<ReceiptEditRecord, string, string>? Notifier { get; set; } = NotifyOwner;

    public ReceiptEditService(IReceiptEditServer server, ReceiptEditStore? store = null, Func<DateTime>? now = null)
    {
        _server = server;
        _store = store ?? ReceiptEditStore.Default;
        _now = now ?? (() => DateTime.Now);
    }

    public static bool IsFullyReturned(SupplierReceipt r) => r.Lines.Count > 0 && r.Lines.All(l => l.ReturnedQty >= l.Qty - 0.0005);

    /// <summary>Исходные данные плана по строкам прихода: что на складе (stock по id товара) и доприходы из журнала.</summary>
    public static List<EditLineInput> BuildInputs(SupplierReceipt r, ReceiptHistory history, IReadOnlyDictionary<string, double> stockByProduct) =>
        r.Lines.Select(l => new EditLineInput(l.Id, l.ProductId, l.ProductName, l.Unit, l.Qty, l.ReturnedQty, l.PurchasePrice,
            stockByProduct.TryGetValue(l.ProductId, out var s) ? s : null,
            history.Addenda.Where(a => a.Remaining > 0.0005 && string.Equals(a.ProductId, l.ProductId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.At).Select(a => new ReturnSource(a.ReceiptId, a.LineId, a.Remaining, true)).ToList())).ToList();

    // ------------------------------------------------------------------ правка

    public async Task<EditResult> ApplyAsync(EditRequest req, CancellationToken ct = default)
    {
        var access = ReceiptEditPolicy.Evaluate(req.IsOwner, req.Window, req.Receipt.CreatedAt, _now(), req.InQueue, IsFullyReturned(req.Receipt));
        if (!access.Allowed)
            return Fail(ReceiptEditPolicy.DeniedText(access));
        if ((req.Reason ?? "").Trim().Length < 5)
            return Fail(Tr.T("Укажите причину правки (не короче 5 знаков).", "Оңдоонун себебин жазыңыз (5 белгиден кем эмес).", "Enter the reason for the edit (at least 5 characters).",
                "Düzenleme nedenini yazın (en az 5 karakter).", "Tahrirlash sababini yozing (kamida 5 belgi)."));
        var history = _store.Get(req.Receipt.Id);
        if (history.Edits.Any(e => e.Status is "applying" or "partial"))
            return Fail(Tr.T("Прошлая правка не доведена до конца — повторите её отправку.", "Мурунку оңдоо аягына чыккан жок — жөнөтүүнү кайталаңыз.", "The previous edit is not finished — retry sending it.",
                "Önceki düzenleme bitmedi — göndermeyi tekrarlayın.", "Oldingi tahrir tugallanmagan — yuborishni takrorlang."));
        if (req.RevertedVersion > 0 && req.RevertedVersion != history.Version)
            return Fail(Tr.T("Откатить можно только последнюю версию.", "Акыркы версияны гана кайтарса болот.", "Only the latest version can be rolled back.", "Yalnızca son sürüm geri alınabilir.", "Faqat oxirgi versiyani qaytarish mumkin."));
        // 2026-10-30 (приёмка 1.17.66, 3б): откат берёт только ПРОВЕДЁННУЮ версию (запись «done»), а не отклонённую попытку с тем же номером.
        var revertTarget = req.RevertedVersion > 0 ? history.Edits.LastOrDefault(e => e.Version == req.RevertedVersion && e.Status == "done") : null;
        if (req.RevertedVersion > 0 && revertTarget is null)
            return Fail(Tr.T("Откатить можно только проведённую версию.", "Өткөрүлгөн версияны гана кайтарса болот.", "Only a posted version can be rolled back.", "Yalnızca işlenmiş sürüm geri alınabilir.", "Faqat o'tkazilgan versiyani qaytarish mumkin."));
        // 2026-10-30 (3а): откат — симметричная обратная операция по тому, что реально провели: доприход оплачивается так же, как было возмещение (наличные / в долг).
        var pay = revertTarget is null ? req.Pay : RevertPay(revertTarget, req.Pay);

        // Конфликт: версия или состав на сервере изменились после открытия правки (другой ПК) — ничего не проводим.
        if (req.BaseVersion != history.Version)
            return new EditResult(false, history.Version, ConflictText, true, false, null, null);
        SupplierReceipt fresh;
        try
        {
            fresh = await _server.GetReceiptAsync(req.Receipt.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(Tr.T("Приход не перечитан с сервера: ", "Кирүү серверден кайра окулган жок: ", "The receipt was not re-read from the server: ", "Giriş sunucudan yeniden okunamadı: ", "Kirim serverdan qayta o'qilmadi: ") + ex.Message);
        }
        if (!string.Equals(InvoiceMath.Fingerprint(fresh), req.BaseFingerprint, StringComparison.Ordinal))
            return new EditResult(false, history.Version, ConflictText, true, false, null, null);

        // 2026-10-30 (3в): правку мог сделать другой ПК — его журнал здесь не виден, но пометка «версия N» осталась в комментариях возвратов/доприходов на сервере.
        try
        {
            // 2026-10-30 (приёмка 1.17.67, «Важно» 5): ищем по серверному id прихода (метка в комментарии), а не по номеру документа — у каждого ПК своя нумерация.
            var remote = await _server.RemoteEditVersionAsync(req.Receipt.SupplierId, req.DocNumber, req.Receipt.Id, req.Receipt.CreatedAt, ct).ConfigureAwait(false);
            if (remote > history.Version && !HasPostedVersion(history, remote))
                return new EditResult(false, history.Version, ForeignEditText(remote), true, false, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Правка накладной {req.DocNumber}: правки с других ПК не проверены ({ex.Message}).", "WARNING");
        }

        var plan = ReceiptEditPlanner.Build(req.Inputs, req.NewQty, pay);
        if (!plan.IsValid)
            return new EditResult(false, history.Version, string.Join("\n", plan.Errors.Take(5)), false, false, null, plan);
        if (!plan.HasChanges)
            return new EditResult(false, history.Version, Tr.T("Изменений нет.", "Өзгөртүү жок.", "No changes.", "Değişiklik yok.", "O'zgarish yo'q."), false, false, null, plan);
        if (revertTarget is not null && plan.Addenda.Count > 0 && RevertBlocked(revertTarget))
            return new EditResult(false, history.Version, RevertBlockedText, false, false, null, plan);

        var version = history.Version + 1;
        var record = new ReceiptEditRecord
        {
            ReceiptId = req.Receipt.Id, Version = version, BaseVersion = history.Version, Kind = req.RevertedVersion > 0 ? "revert" : "edit", RevertedVersion = req.RevertedVersion,
            At = _now(), By = req.By, Device = req.Device, Reason = req.Reason.Trim(), TotalBefore = plan.TotalBefore, TotalAfter = plan.TotalAfter,
            Compensation = req.Compensation, Fingerprint = req.BaseFingerprint, Status = "applying",
        };
        foreach (var l in plan.Lines.Where(l => l.Action != EditAction.None))
            record.Lines.Add(new EditLineChange { LineId = l.Input.LineId, ProductId = l.Input.ProductId, Name = l.Input.Name, Unit = l.Input.Unit, QtyBefore = l.Effective, QtyAfter = l.NewQty, Price = l.Input.Purchase });
        // возвраты — по одному на приход-источник; один доприход на все увеличенные строки
        foreach (var g in plan.Returns.GroupBy(r => r.ReceiptId))
            record.Steps.Add(new EditStep
            {
                Kind = "return", ReceiptId = g.Key,
                Items = g.Select(r => new EditStepItem { ProductId = r.ProductId, Name = r.Name, LineId = r.LineId, SourceReceiptId = r.ReceiptId, Qty = r.Qty, Price = r.Price }).ToList(),
            });
        if (plan.Addenda.Count > 0)
            record.Steps.Add(new EditStep { Kind = "addendum", Items = plan.Addenda.Select(a => new EditStepItem { ProductId = a.ProductId, Name = a.Name, Unit = a.Unit, Qty = a.Qty, Price = a.Price }).ToList() });

        // Ключи и шаги — на диск ДО первого запроса.
        _store.Mutate(req.Receipt.Id, h => h.Edits.Add(record));
        return await RunAsync(req.Receipt, req.DocNumber, pay, req.Compensation, req.CashboxId, req.SupplierName, record, plan, ct).ConfigureAwait(false);
    }

    /// <summary>2026-10-31 (приёмка 1.17.68, «Важно» 4а): версия «своя» только если попытка не отклонена целиком: done/partial/abandoned (есть проведённый шаг)
    /// или applying (шаг мог дойти — пометка на сервере тогда наша). Отклонённая (failed, ничего не проведено) попытка с тем же номером не своя:
    /// иначе правку другого ПК с тем же «v2» не заметят и получится два «v2».</summary>
    public static bool HasPostedVersion(ReceiptHistory history, int version) =>
        history.Edits.Any(e => e.Version == version && (e.Status is not "failed" || e.Steps.Any(s => s.Status == "done")));

    /// <summary>Чем оплачивать доприход при откате: так же, как было возмещение откатываемой правки (наличные → из кассы, зачёт в долг → в долг).</summary>
    public static ReceiptPayKind RevertPay(ReceiptEditRecord target, ReceiptPayKind original) => target.Compensation switch
    {
        "cash" => ReceiptPayKind.Cash,
        "debt_offset" => ReceiptPayKind.Debt,
        _ => original,
    };

    /// <summary>Откат невозможен: при той правке возврат шёл «Без возмещения» (деньги и долг не менялись), а откат оформляется доприходом, который их изменил бы.</summary>
    public static bool RevertBlocked(ReceiptEditRecord target) => target.Compensation == "none" && target.Steps.Any(s => s.Kind == "return" && s.Status == "done");

    public static string RevertBlockedText => Tr.T("Откат этой правки невозможен: при ней возврат шёл «Без возмещения» (деньги не возвращались), а откат оформляется приходом, который списал бы деньги или создал долг, которых не было. Оформите приход вручную.",
        "Бул оңдоону кайтаруу мүмкүн эмес: ал «Толуктоосуз» болгон (акча кайтарылган эмес), ал эми кайтаруу кирүү менен жасалат — ал жок акчаны чыгарат же карыз түзөт. Кирүүнү кол менен жасаңыз.",
        "This edit can't be rolled back: its return was “No compensation” (no money came back), while a rollback is a receipt that would take money or create a debt that never existed. Post a receipt manually.",
        "Bu düzenleme geri alınamaz: iade «Telafi yok» idi (para dönmedi); geri alma ise olmayan parayı düşecek veya borç yaratacak bir giriştir. Girişi elle yapın.",
        "Bu tahrirni qaytarib bo'lmaydi: unda qaytarish «Qoplamasiz» edi (pul qaytmagan), qaytarish esa yo'q pulni yechadigan yoki qarz yaratadigan kirim bilan bajariladi. Kirimni qo'lda rasmiylashtiring.");

    public static string ForeignEditText(int version) => Tr.T($"Эту накладную уже исправляли с другого ПК (версия {version}) — журнал правок здесь её не видит. Правьте на том ПК или обновите данные; ничего не проведено.",
        $"Бул накладнойду башка ПКдан оңдошкон ({version}-версия) — бул жерде оңдоо журналы аны көрбөйт. Ошол ПКда оңдоңуз же маалыматты жаңыртыңыз; эч нерсе өткөрүлгөн жок.",
        $"This invoice was already corrected from another PC (version {version}) — the edit journal here can't see it. Edit it on that PC or refresh; nothing was posted.",
        $"Bu fatura başka bir PC'den zaten düzeltildi (sürüm {version}) — buradaki düzenleme günlüğü görmüyor. O PC'de düzenleyin veya yenileyin; hiçbir şey işlenmedi.",
        $"Bu yuk xati boshqa kompyuterdan tuzatilgan (versiya {version}) — bu yerdagi tahrir jurnali uni ko'rmaydi. O'sha kompyuterda tahrirlang yoki yangilang; hech narsa o'tkazilmadi.");

    public static string RemoteUnknownText => Tr.T("Правка с другого ПК может быть не видна — не удалось проверить сервер. Обновите данные.", "Башка ПКдагы оңдоо көрүнбөй калышы мүмкүн — серверди текшерүү мүмкүн болгон жок. Маалыматты жаңыртыңыз.",
        "An edit from another PC may be invisible — the server could not be checked. Refresh.", "Başka bir PC'deki düzenleme görünmeyebilir — sunucu kontrol edilemedi. Yenileyin.", "Boshqa kompyuterdagi tahrir ko'rinmasligi mumkin — serverni tekshirib bo'lmadi. Yangilang.");

    private static readonly System.Text.RegularExpressions.Regex VersionMark = new(@"\G\s*,\s*(?:версия|version|sürüm|versiya)\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Версия правки из комментария возврата/доприхода («Исправление документа ПР-2026-000123, версия 3: причина»); 0 — комментарий не про этот документ.</summary>
    public static int EditVersionFromComment(string? comment, string docNumber)
    {
        if (string.IsNullOrEmpty(comment) || string.IsNullOrWhiteSpace(docNumber))
            return 0;
        var max = 0;
        var from = 0;
        while (from < comment.Length && comment.IndexOf(docNumber, from, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            var m = VersionMark.Match(comment, at + docNumber.Length);
            if (m.Success && m.Index == at + docNumber.Length && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                max = Math.Max(max, v);
            from = at + docNumber.Length;
        }
        return max;
    }

    /// <summary>Сколько каждого товара ждёт отправки в офлайн-очереди продаж этой кассы (сервер этих продаж ещё не знает — остаток на сервере завышен).</summary>
    public static Dictionary<string, double> PendingOfflineSales()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in OfflinePendingSalesStore.LoadPendingForSync())
            {
                try
                {
                    using var doc = JsonDocument.Parse(entry.CartJson);
                    foreach (var line in CartDisplayHelper.EnumerateItems(doc.RootElement))
                        if (CartDisplayHelper.TryProductId(line) is { Length: > 0 } id)
                            result[id] = result.GetValueOrDefault(id) + Math.Max(0, CartDisplayHelper.LineQuantity(line));
                }
                catch (JsonException)
                {
                    // чек с битым JSON: состав неизвестен — остальные учитываем
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Правка накладной: офлайн-очередь продаж не прочитана ({ex.Message}).", "WARNING");
        }
        return result;
    }

    /// <summary>Довести начатую правку: шаги pending/unknown/failed уходят ещё раз тем же ключом, проведённые не повторяются.</summary>
    public async Task<EditResult> ResumeAsync(SupplierReceipt receipt, string docNumber, ReceiptPayKind pay, string compensation, string? cashboxId, string supplierName, CancellationToken ct = default)
    {
        var record = _store.Get(receipt.Id).Edits.LastOrDefault(e => e.Status is "applying" or "partial");
        if (record is null)
            return Fail(Tr.T("Нет начатой правки.", "Башталган оңдоо жок.", "No unfinished edit.", "Yarım düzenleme yok.", "Tugallanmagan tahrir yo'q."));
        // откат довозим так же, как начинали: доприход оплачивается по возмещению откатываемой правки
        if (record.Kind == "revert" && _store.Get(receipt.Id).Edits.LastOrDefault(e => e.Version == record.RevertedVersion && e.Status == "done") is { } target)
            pay = RevertPay(target, pay);
        return await RunAsync(receipt, docNumber, pay, compensation, cashboxId, supplierName, record, null, ct).ConfigureAwait(false);
    }

    /// <summary>2026-10-30 (приёмка 1.17.67, «Важно» 5): отказаться от недоведённой правки (окно 24/48 ч прошло или довести нет возможности). Непроведённые шаги закрываются
    /// («abandoned»), проведённые остаются. Перед этим касса проверяет по серверу, что шаги не проведены: проведён — отказаться нельзя (нажмите «Довести», повтор их пропустит);
    /// проверить не удалось или документ похож, но без метки — отказ только с <paramref name="manuallyChecked"/> (владелец проверил вручную).</summary>
    public async Task<EditResult> AbandonAsync(SupplierReceipt receipt, string docNumber, bool manuallyChecked, CancellationToken ct = default)
    {
        var record = _store.Get(receipt.Id).Edits.LastOrDefault(e => e.Status is "applying" or "partial");
        if (record is null)
            return Fail(Tr.T("Нет начатой правки.", "Башталган оңдоо жок.", "No unfinished edit.", "Yarım düzenleme yok.", "Tugallanmagan tahrir yo'q."));
        var comment = EditComment(docNumber, record);
        // 2026-10-31 (приёмка 1.17.68, «Важно» 4в): у шага уже записан id документа сервера (доприход/возврат проведён, а до статуса done касса упала) —
        // отказаться нельзя, как бы ни ответил поиск (проведённый доприход он пропускает как «занятый»).
        if (record.Steps.Any(s => s.Status != "done" && s.ServerId is { Length: > 0 }))
            return Fail(AbandonPostedText);
        var doubt = new List<string>();
        foreach (var step in record.Steps.Where(s => s.Status != "done" && s.Attempts > 0 && s.Status != "failed"))
        {
            // шаг уходил, и ответ мог дойти: проверяем ключ и метку на сервере
            var token = ReceiptEditMatcher.Token(receipt.Id, record.Version, step.Key);
            try
            {
                if (await _server.KeyKnownAsync(step.Key, ct).ConfigureAwait(false) == true)
                    return Fail(AbandonPostedText);
                var found = step.Kind == "return"
                    ? await _server.FindPostedReturnAsync(receipt.SupplierId, step.ReceiptId.Length > 0 ? step.ReceiptId : receipt.Id, MatchFor(receipt, step, token, comment), docNumber, ct).ConfigureAwait(false)
                    : await _server.FindPostedAddendumAsync(receipt.SupplierId, MatchFor(receipt, step, token, AddendumPrefix(docNumber) + comment), docNumber, ct).ConfigureAwait(false);
                if (found.Kind == PostedKind.Found)
                    return Fail(AbandonPostedText);
                if (found.Kind == PostedKind.Ambiguous)
                    doubt.Add(AmbiguousText(found));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                doubt.Add(ex.Message);
            }
        }
        if (doubt.Count > 0 && !manuallyChecked)
            return new EditResult(false, record.Version, AbandonUnverifiedText + "\n" + string.Join("\n", doubt.Take(3)), false, false, record, null, NeedsManualCheck: true);

        var done = record.Steps.Count(s => s.Status == "done");
        foreach (var step in record.Steps.Where(s => s.Status != "done"))
            step.Status = "abandoned";
        // Ничего не проведено — правка просто «не удалась» (версия не менялась). Часть проведена — остаётся в истории как прекращённая.
        record.Status = done > 0 ? "abandoned" : "failed";
        if (done > 0)
            MarkUnposted(record); // 2026-10-31 (приёмка 1.17.68, 4б): в истории — то, что реально проведено, а не план
        _store.Mutate(receipt.Id, _ => { });
        PosLogger.Log($"AUDIT: владелец отказался от недоведённой правки накладной {docNumber} v{record.Version} ({done} из {record.Steps.Count} шагов проведено; сумма по плану {record.PlannedTotalAfter ?? record.TotalAfter:0.00}, проведено {record.TotalAfter:0.00}){(doubt.Count > 0 ? "; проведённость шагов не удалось проверить — проверено вручную" : "")}.", "WARNING");
        return new EditResult(true, record.Version, null, false, done > 0, record, null);
    }

    /// <summary>2026-10-31 (приёмка 1.17.68, «Важно» 4б): правка прекращена после частичного проведения — плановые строки и итог пересчитываются по проведённым шагам
    /// (возврат уменьшает, доприход увеличивает); плановое значение остаётся в <c>Planned…</c>, строка помечается <see cref="EditLineChange.NotPosted"/>.
    /// Две строки одного товара делят шаги — для них по количеству не пересчитываем, только помечаем.</summary>
    private static void MarkUnposted(ReceiptEditRecord record)
    {
        var posted = record.Steps.Where(s => s.Status == "done").ToList();
        double Net(string productId) => posted.Sum(s => s.Items.Where(i => string.Equals(i.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            .Sum(i => s.Kind == "return" ? -i.Qty : i.Qty));
        var delta = posted.Sum(s => s.Items.Sum(i => (s.Kind == "return" ? -1 : 1) * ReceivingPricing.LineTotal(i.Qty, i.Price)));
        record.PlannedTotalAfter = record.TotalAfter;
        record.TotalAfter = Math.Round(record.TotalBefore + delta, 2);
        foreach (var line in record.Lines)
        {
            var shared = record.Lines.Count(l => string.Equals(l.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) > 1;
            var actual = shared ? line.QtyAfter : Math.Round(line.QtyBefore + Net(line.ProductId), 3);
            if (shared ? posted.Count < record.Steps.Count : Math.Abs(actual - line.QtyAfter) > 0.0005)
            {
                line.PlannedQtyAfter = line.QtyAfter;
                line.QtyAfter = actual;
                line.NotPosted = true;
            }
        }
    }

    public static string AbandonPostedText => Tr.T("Этот шаг уже проведён на сервере — отказаться нельзя. Нажмите «Довести»: касса не отправит его повторно, а допишет остальное.",
        "Бул кадам серверде өткөрүлгөн — баш тартууга болбойт. «Аягына чыгаруу» басыңыз: касса аны кайра жөнөтпөйт, калганын жазат.",
        "This step is already posted on the server — it can't be abandoned. Press “Finish”: the till will not resend it and will complete the rest.",
        "Bu adım sunucuda zaten işlendi — vazgeçilemez. «Tamamla»ya basın: kasa tekrar göndermez, kalanını tamamlar.",
        "Bu qadam serverda allaqachon o'tkazilgan — voz kechib bo'lmaydi. «Tugatish»ni bosing: kassa uni qayta yubormaydi, qolganini tugatadi.");

    public static string AbandonUnverifiedText => Tr.T("Не удалось проверить, проведён ли шаг на сервере. Проверьте вручную («Закупки» / «Возвраты поставщику»): если шага там нет — можно отказаться.",
        "Кадам серверде өткөрүлгөнүн текшерүү мүмкүн болгон жок. Кол менен текшериңиз («Сатып алуу» / «Жеткирүүчүгө кайтаруу»): ал жерде жок болсо — баш тартса болот.",
        "Could not check whether the step was posted on the server. Check by hand (“Purchases” / “Supplier returns”): if it is not there, you can abandon it.",
        "Adımın sunucuda işlenip işlenmediği kontrol edilemedi. Elle kontrol edin («Alımlar» / «Tedarikçi iadeleri»): orada yoksa vazgeçebilirsiniz.",
        "Qadam serverda o'tkazilganini tekshirib bo'lmadi. Qo'lda tekshiring («Xaridlar» / «Yetkazib beruvchiga qaytarish»): u yerda bo'lmasa, voz kechsa bo'ladi.");

    /// <summary>Комментарий правки без метки шага: «Исправление документа ПР-…, версия N: причина».</summary>
    private static string EditComment(string docNumber, ReceiptEditRecord record) =>
        Tr.T($"Исправление документа {docNumber}, версия {record.Version}: {record.Reason}", $"Документти оңдоо {docNumber}, версия {record.Version}: {record.Reason}",
            $"Document correction {docNumber}, version {record.Version}: {record.Reason}", $"Belge düzeltme {docNumber}, sürüm {record.Version}: {record.Reason}", $"Hujjatni tuzatish {docNumber}, versiya {record.Version}: {record.Reason}");

    private async Task<EditResult> RunAsync(SupplierReceipt receipt, string docNumber, ReceiptPayKind pay, string compensation, string? cashboxId, string supplierName,
        ReceiptEditRecord record, ReceiptEditPlan? plan, CancellationToken ct)
    {
        string? stopError = null;
        var comment = EditComment(docNumber, record);
        // возвраты раньше доприходов: остаток сначала уменьшается, потом растёт
        foreach (var step in record.Steps.OrderBy(s => s.Kind == "return" ? 0 : 1).ToList())
        {
            if (step.Status == "done")
                continue;
            // 2026-10-30 (приёмка 1.17.66, БЛОКЕР): шаг уже уходил и ответ не дошёл (unknown) либо программа оборвалась посреди запроса (pending) —
            // перед повтором ищем уже проведённый документ, а не доверяем одному idempotency_key (сервер его может не смотреть).
            var mayBePosted = step.Attempts > 0 && step.Status != "failed";
            step.Attempts++;
            step.FirstSentAt ??= _now();
            step.Status = "pending";
            _store.Mutate(receipt.Id, _ => { });
            try
            {
                // 2026-10-30 (приёмка 1.17.67, «Важно» 4): у каждого шага своя метка в комментарии — по ней повтор находит именно ЭТОТ документ (не чужой и не другой версии).
                var token = ReceiptEditMatcher.Token(receipt.Id, record.Version, step.Key);
                if (step.Kind == "return")
                    await DoReturnAsync(receipt, step, compensation, cashboxId, token, comment, docNumber, mayBePosted, ct).ConfigureAwait(false);
                else
                    await DoAddendumAsync(receipt, step, pay, docNumber, token, comment, supplierName, record, mayBePosted, ct).ConfigureAwait(false);
                step.Status = "done";
                step.Error = null;
            }
            catch (UnverifiedException ex)
            {
                step.Status = "unknown";
                step.Error = ex.Message;
                stopError = Tr.T("Не удалось проверить, проведён ли шаг на сервере — повторно не отправляем, чтобы не задвоить. Проверьте связь и нажмите «Довести» ещё раз: ", "Кадам серверде өткөрүлгөнүн текшерүү мүмкүн болгон жок — эки эсе болбош үчүн кайра жөнөтпөйбүз. Байланышты текшерип, «Аягына чыгаруу» басыңыз: ",
                    "Could not check whether the step was already posted — not resending, to avoid a double. Check the connection and press “Finish” again: ", "Adımın sunucuda işlenip işlenmediği kontrol edilemedi — ikilememek için tekrar gönderilmiyor. Bağlantıyı kontrol edip «Tamamla»ya yeniden basın: ",
                    "Qadam serverda o'tkazilganini tekshirib bo'lmadi — ikkilanmasligi uchun qayta yuborilmaydi. Aloqani tekshirib, «Tugatish»ni yana bosing: ") + (ReceivingServerMessages.Humanize(ex.Message) ?? ex.Message);
            }
            catch (OperationCanceledException)
            {
                step.Status = "unknown";
                stopError = Tr.T("Ответ сервера не получен — повторите отправку.", "Сервердин жообу алынган жок — жөнөтүүнү кайталаңыз.", "No answer from the server — retry sending.", "Sunucudan yanıt alınamadı — tekrar gönderin.", "Serverdan javob olinmadi — yuborishni takrorlang.");
            }
            catch (Exception ex)
            {
                step.Error = ex.Message;
                step.Status = PendingIdempotencyKey.IsOutcomeUnknown(ex) ? "unknown" : "failed";
                stopError = (step.Status == "unknown"
                    ? Tr.T("Ответ сервера не дошёл: ", "Сервердин жообу жеткен жок: ", "No answer arrived: ", "Yanıt gelmedi: ", "Javob kelmadi: ")
                    : Tr.T("Сервер отказал: ", "Сервер баш тартты: ", "The server refused: ", "Sunucu reddetti: ", "Server rad etdi: ")) + (ReceivingServerMessages.Humanize(ex.Message) ?? ex.Message);
            }
            _store.Mutate(receipt.Id, _ => { });
            if (stopError is not null)
                break;
        }

        var done = record.Steps.Count(s => s.Status == "done");
        if (stopError is null && done == record.Steps.Count)
        {
            record.Status = "done";
            _store.Mutate(receipt.Id, h => h.Version = Math.Max(h.Version, record.Version));
            PosLogger.Log($"AUDIT: правка накладной {docNumber} v{record.Version} ({record.Kind}): {record.TotalBefore:0.00} → {record.TotalAfter:0.00}; причина: {record.Reason}.", "INFO");
            try { Notifier?.Invoke(record, docNumber, supplierName); } catch (Exception ex) { PosLogger.Log($"Правка накладной: уведомление не ушло ({ex.Message}).", "WARNING"); }
            return new EditResult(true, record.Version, null, false, false, record, plan);
        }
        // Ничего не проведено и сервер отказал по существу — правка «не удалась» и дальше не блокирует; ответ не дошёл — ждёт повтора тем же ключом.
        record.Status = done > 0 ? "partial" : record.Steps.Any(s => s.Status is "unknown" or "pending") ? "applying" : "failed";
        if (done > 0)
            _store.Mutate(receipt.Id, h => h.Version = Math.Max(h.Version, record.Version));
        else
            _store.Mutate(receipt.Id, _ => { });
        PosLogger.Log($"AUDIT: правка накладной {docNumber} v{record.Version} не доведена до конца ({done} из {record.Steps.Count} шагов): {stopError}", "WARNING");
        return new EditResult(false, record.Version, stopError, false, done > 0, record, plan);
    }

    /// <summary>Сервер не ответил на вопрос «проведён ли шаг» — повторно не шлём (лучше остановиться, чем задвоить деньги).</summary>
    private sealed class UnverifiedException : Exception
    {
        public UnverifiedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    private async Task DoReturnAsync(SupplierReceipt receipt, EditStep step, string compensation, string? cashboxId, string token, string comment, string docNumber, bool mayBePosted, CancellationToken ct)
    {
        if (mayBePosted && step.ServerId is { Length: > 0 })
            return; // ответ сервера с id уже был получен и записан — возврат проведён
        var sourceId = step.ReceiptId.Length > 0 ? step.ReceiptId : receipt.Id;
        // Строка-источник: у доприхода id строки не знаем — находим по товару в самом доприходе. 2026-10-30: источник читаем всегда — нужен «возвращено до отправки».
        var source = await _server.GetReceiptAsync(sourceId, ct).ConfigureAwait(false);
        string LineOf(EditStepItem it) => step.ReceiptId.Length > 0
            ? source.Lines.FirstOrDefault(l => string.Equals(l.ProductId, it.ProductId, StringComparison.OrdinalIgnoreCase))?.Id ?? it.LineId
            : source.Lines.FirstOrDefault(l => l.Id == it.LineId)?.Id ?? source.Lines.FirstOrDefault(l => string.Equals(l.ProductId, it.ProductId, StringComparison.OrdinalIgnoreCase))?.Id ?? it.LineId;
        var hadBaseline = step.BaseReturned.Count > 0;
        if (mayBePosted)
        {
            var (posted, postedId) = await WasReturnPostedAsync(receipt, step, source, sourceId, token, comment, docNumber, hadBaseline, LineOf, ct).ConfigureAwait(false);
            if (posted)
            {
                step.ServerId = postedId;
                PosLogger.Log($"Правка накладной: возврат уже проведён на сервере ({postedId ?? "без id"}) — второй раз не отправляем.", "STOCK");
                AfterReturnPosted(receipt, step);
                return;
            }
        }
        if (!hadBaseline)
        {
            // до отправки: сколько уже возвращено по строкам (на диск — вместе с шагом; по нему повтор узнает, что возврат прошёл)
            foreach (var it in step.Items)
                if (source.Lines.FirstOrDefault(l => l.Id == LineOf(it)) is { } sl)
                    step.BaseReturned[sl.Id] = sl.ReturnedQty;
            _store.Mutate(receipt.Id, _ => { });
        }
        var items = new List<Dictionary<string, object?>>();
        foreach (var it in step.Items)
        {
            var lineId = LineOf(it);
            var item = new Dictionary<string, object?>
            {
                ["product_id"] = it.ProductId,
                ["qty"] = it.Qty.ToString("0.###", CultureInfo.InvariantCulture),
                ["purchase_price"] = it.Price.ToString("0.###", CultureInfo.InvariantCulture),
            };
            if (lineId.Length > 0)
                item["receipt_item_id"] = lineId;
            items.Add(item);
        }
        var body = new Dictionary<string, object?>
        {
            ["reason"] = "other",
            ["comment"] = token + " " + comment,
            ["compensation"] = compensation,
            ["items"] = items,
            ["receipt_id"] = sourceId,
            ["idempotency_key"] = step.Key,
        };
        if (compensation == "cash" && !string.IsNullOrEmpty(cashboxId))
            body["cashbox_id"] = cashboxId;
        step.ServerId = await _server.PostReturnAsync(receipt.SupplierId, body, step.Key, ct).ConfigureAwait(false);
        AfterReturnPosted(receipt, step);
    }

    /// <summary>Возврат проведён (сейчас или раньше): вернули из доприхода — у него осталось меньше.</summary>
    private void AfterReturnPosted(SupplierReceipt receipt, EditStep step)
    {
        if (step.ReceiptId.Length > 0)
            _store.Mutate(receipt.Id, h =>
            {
                foreach (var it in step.Items)
                    foreach (var a in h.Addenda.Where(a => a.ReceiptId == step.ReceiptId && string.Equals(a.ProductId, it.ProductId, StringComparison.OrdinalIgnoreCase)))
                        a.Remaining = Math.Max(0, Math.Round(a.Remaining - it.Qty, 3));
            });
    }

    /// <summary>Проведён ли уже возврат этого шага: (1) сервер знает ключ; (2) «возвращено» по строкам выросло на количество шага; (3) в возвратах поставщика есть такой же
    /// (тот же приход, комментарий, время). Не смогли проверить — <see cref="UnverifiedException"/>: повтор не отправляется.</summary>
    private async Task<(bool Posted, string? Id)> WasReturnPostedAsync(SupplierReceipt receipt, EditStep step, SupplierReceipt source, string sourceId, string token, string comment, string docNumber, bool useBaseline, Func<EditStepItem, string> lineOf, CancellationToken ct)
    {
        try
        {
            if (await _server.KeyKnownAsync(step.Key, ct).ConfigureAwait(false) == true)
                return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Правка накладной: сверка по ключу не удалась ({ex.Message}) — ищем возврат в списке.", "INFO");
        }
        if (useBaseline && step.Items.Count > 0 && step.Items.All(it =>
                source.Lines.FirstOrDefault(l => l.Id == lineOf(it)) is { } sl && step.BaseReturned.TryGetValue(sl.Id, out var was) && sl.ReturnedQty - was >= it.Qty - 0.0005))
            return (true, null);
        try
        {
            var found = await _server.FindPostedReturnAsync(receipt.SupplierId, sourceId, MatchFor(receipt, step, token, comment), docNumber, ct).ConfigureAwait(false);
            if (found.Kind == PostedKind.Ambiguous)
                throw new UnverifiedException(AmbiguousText(found));
            return (found.Kind == PostedKind.Found, found.Id);
        }
        catch (UnverifiedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UnverifiedException(ex.Message, ex);
        }
    }

    private async Task DoAddendumAsync(SupplierReceipt receipt, EditStep step, ReceiptPayKind pay, string docNumber, string token, string comment, string supplierName, ReceiptEditRecord record, bool mayBePosted, CancellationToken ct)
    {
        if (!ReceiptEditPlanner.CanAddendum(pay))
            throw new InvalidOperationException(Tr.T("доприход для этого способа оплаты недоступен", "бул төлөм ыкмасы үчүн кошумча кирүү жеткиликсиз", "supplementary receipt isn't available for this payment type",
                "bu ödeme türü için ek giriş yok", "bu to'lov turi uchun qo'shimcha kirim mavjud emas"));
        var body = new Dictionary<string, object?>
        {
            ["items"] = step.Items.Select(i => new Dictionary<string, object?>
            {
                ["product"] = i.ProductId,
                ["qty"] = Math.Round(i.Qty, 3, MidpointRounding.AwayFromZero),
                ["purchase_price"] = Math.Round(i.Price, 3, MidpointRounding.AwayFromZero),
            }).ToList(),
            ["payment_type"] = pay == ReceiptPayKind.Cash ? "cash" : "debt",
            // 2026-10-30: пометка «дополнение к ПР-…» в комментарии прихода (сервер поле хранит — см. нумерацию документов закупок).
            ["comment"] = AddendumPrefix(docNumber) + token + " " + comment,
        };
        if (pay == ReceiptPayKind.Cash)
            body["cashbox_role"] = "expense_variable";
        string? id;
        DateTime? at;
        (string? Id, DateTime? At)? already = null;
        if (mayBePosted)
        {
            if (step.ServerId is { Length: > 0 } knownId)
                already = (knownId, _store.Get(receipt.Id).Addenda.FirstOrDefault(a => a.ReceiptId == knownId)?.At);
            else
                already = await FindPostedAddendumAsync(receipt, step, docNumber, token, comment, ct).ConfigureAwait(false);
        }
        if (already is { } found)
        {
            (id, at) = found;
            PosLogger.Log($"Правка накладной: доприход {id} уже есть на сервере — второй раз не отправляем.", "STOCK");
        }
        else
            (id, at) = await _server.PostReceiptAsync(receipt.SupplierId, body, step.Key, ct).ConfigureAwait(false);
        step.ServerId = id;
        if (id is { Length: > 0 })
            _store.Mutate(receipt.Id, h =>
            {
                foreach (var it in step.Items.Where(it => !h.Addenda.Any(a => a.ReceiptId == id && string.Equals(a.ProductId, it.ProductId, StringComparison.OrdinalIgnoreCase))))
                    h.Addenda.Add(new AddendumRef { ReceiptId = id, ProductId = it.ProductId, Qty = it.Qty, Remaining = it.Qty, FromVersion = record.Version, At = at ?? _now() });
            });
        // В долг: касса сама записывает долг поставщику (сервер сделку-долг по приходу не создаёт) — как при обычной приёмке.
        if (pay == ReceiptPayKind.Debt && id is { Length: > 0 })
        {
            try
            {
                await _server.RecordDebtAsync(receipt.SupplierId, supplierName.Length > 0 ? supplierName : receipt.SupplierName, id, step.Items.Sum(i => ReceivingPricing.LineTotal(i.Qty, i.Price)), at ?? _now(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                step.Warning = ex.Message;
                PosLogger.Log($"Правка накладной: долг по доприходу {id} не записан ({ex.Message}) — он в очереди «долг не записан».", "WARNING");
            }
        }
    }

    private static string AddendumPrefix(string docNumber) =>
        Tr.T($"Дополнение к {docNumber}. ", $"{docNumber} кошумчасы. ", $"Addition to {docNumber}. ", $"{docNumber} eki. ", $"{docNumber} qo'shimchasi. ");

    /// <summary>Окно времени, метка и занятые документы для поиска шага (см. <see cref="ReceiptEditMatcher"/>). Запас на часы ПК — поправка
    /// <see cref="ReceiptEditPolicy.ClockSkew"/>, если известна. <paramref name="legacyComment"/> — комментарий в виде до появления метки (записи прошлых сборок).</summary>
    private StepMatch MatchFor(SupplierReceipt receipt, EditStep step, string token, string legacyComment)
    {
        var (since, until) = ReceiptEditMatcher.Window(step.FirstSentAt ?? _now(), ReceiptEditPolicy.ClockSkew);
        return new StepMatch(token, legacyComment, step.Items, since, until, ClaimedAddenda(receipt.Id, step));
    }

    private static string AmbiguousText(PostedLookup found) => Tr.T(
        $"на сервере есть похожий документ без пометки правки ({found.Note}) — неясно, ваш ли он. Повторно не отправляем, чтобы не задвоить: проверьте его вручную (закупки / возвраты поставщику)",
        $"серверде оңдоо белгисиз окшош документ бар ({found.Note}) — өзүңүздүкүбү, белгисиз. Эки эсе болбош үчүн кайра жөнөтпөйбүз: аны кол менен текшериңиз (сатып алуу / жеткирүүчүгө кайтаруу)",
        $"the server has a similar document without an edit mark ({found.Note}) — unclear whether it is yours. Not resending, to avoid a double: check it by hand (purchases / supplier returns)",
        $"sunucuda düzenleme işareti olmayan benzer bir belge var ({found.Note}) — sizin olup olmadığı belirsiz. İkilememek için tekrar gönderilmiyor: elle kontrol edin (alımlar / tedarikçi iadeleri)",
        $"serverda tahrir belgisiz o'xshash hujjat bor ({found.Note}) — o'zingiznikimi, noma'lum. Ikkilanmasligi uchun qayta yuborilmaydi: uni qo'lda tekshiring (xaridlar / yetkazib beruvchiga qaytarish)");

    /// <summary>Есть ли уже на сервере доприход этого шага: сервер знает ключ или найден приход с меткой шага (запасной путь — тот же комментарий и состав в окне времени).
    /// Не смогли проверить или документ похож, но без метки — <see cref="UnverifiedException"/>.</summary>
    private async Task<(string? Id, DateTime? At)?> FindPostedAddendumAsync(SupplierReceipt receipt, EditStep step, string docNumber, string token, string comment, CancellationToken ct)
    {
        // Старый вид комментария доприхода (записи до метки): «Дополнение к ПР-…. » + комментарий правки.
        var match = MatchFor(receipt, step, token, AddendumPrefix(docNumber) + comment);
        try
        {
            if (await _server.KeyKnownAsync(step.Key, ct).ConfigureAwait(false) == true)
            {
                // сервер подтвердил ключ, но id не отдал — пробуем найти по метке, иначе считаем проведённым без id
                try
                {
                    var byKey = await _server.FindPostedAddendumAsync(receipt.SupplierId, match, docNumber, ct).ConfigureAwait(false);
                    if (byKey.Kind == PostedKind.Found)
                        return (byKey.Id, byKey.At);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Правка накладной: доприход по ключу найден, но id не получен ({ex.Message}).", "INFO");
                }
                return (null, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Правка накладной: сверка по ключу не удалась ({ex.Message}) — ищем доприход в списке.", "INFO");
        }
        try
        {
            var found = await _server.FindPostedAddendumAsync(receipt.SupplierId, match, docNumber, ct).ConfigureAwait(false);
            if (found.Kind == PostedKind.Ambiguous)
                throw new UnverifiedException(AmbiguousText(found));
            return found.Kind == PostedKind.Found ? (found.Id, found.At) : null;
        }
        catch (UnverifiedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UnverifiedException(ex.Message, ex);
        }
    }

    /// <summary>Документы, уже занятые другими шагами этого документа (чтобы один документ не засчитали двум шагам): доприходы из журнала и id проведённых шагов.</summary>
    private HashSet<string> ClaimedAddenda(string receiptId, EditStep current)
    {
        var h = _store.Get(receiptId);
        var set = new HashSet<string>(h.Addenda.Select(a => a.ReceiptId), StringComparer.OrdinalIgnoreCase);
        foreach (var s in h.Edits.SelectMany(e => e.Steps).Where(s => !ReferenceEquals(s, current) && s.ServerId is { Length: > 0 }))
            set.Add(s.ServerId!);
        return set;
    }

    // ------------------------------------------------------------------ вспомогательное

    public static string ConflictText => Tr.T("Приход изменили с другого ПК — откройте накладную заново и сравните. Ничего не проведено.", "Кирүүнү башка ПКдан өзгөрттү — накладнойду кайра ачып салыштырыңыз. Эч нерсе өткөрүлгөн жок.",
        "The receipt was changed from another PC — reopen the invoice and compare. Nothing was posted.", "Giriş başka bir PC'den değiştirildi — faturayı yeniden açıp karşılaştırın. Hiçbir şey işlenmedi.", "Kirim boshqa kompyuterdan o'zgartirilgan — yuk xatini qayta ochib solishtiring. Hech narsa o'tkazilmadi.");

    private static EditResult Fail(string error) => new(false, 0, error, false, false, null, null);

    /// <summary>Отметка «ИСПРАВЛЕНО · версия N от дата · причина» для печати и PDF; null — документ не правился.</summary>
    public static string? CorrectedStamp(ReceiptHistory h)
    {
        var last = h.Edits.LastOrDefault(e => e.Status is "done" or "partial" or "abandoned");
        if (last is null)
            return null;
        return Tr.T($"ИСПРАВЛЕНО · версия {last.Version} от {last.At:dd.MM.yyyy HH:mm} · причина: {last.Reason}", $"ОҢДОЛДУ · версия {last.Version}, {last.At:dd.MM.yyyy HH:mm} · себеби: {last.Reason}",
            $"CORRECTED · version {last.Version} of {last.At:dd.MM.yyyy HH:mm} · reason: {last.Reason}", $"DÜZELTİLDİ · sürüm {last.Version}, {last.At:dd.MM.yyyy HH:mm} · neden: {last.Reason}",
            $"TUZATILDI · versiya {last.Version}, {last.At:dd.MM.yyyy HH:mm} · sabab: {last.Reason}");
    }

    private static void NotifyOwner(ReceiptEditRecord r, string docNumber, string supplier)
    {
        if (!TelegramBotService.IsConfigured)
            return;
        var text = TelegramBotService.BuildAlert(
            Tr.T("Накладная закупки исправлена", "Сатып алуу накладнойу оңдолду", "A purchase invoice was corrected", "Alım faturası düzeltildi", "Xarid yuk xati tuzatildi"),
            Tr.T($"✎ {docNumber} («{supplier}») исправлена: {r.TotalBefore:0.00} → {r.TotalAfter:0.00} сом ({r.TotalAfter - r.TotalBefore:+0.00;-0.00}). Версия {r.Version}. Причина: {r.Reason}. Кто: {r.By}, ПК: {r.Device}.",
                $"✎ {docNumber} («{supplier}») оңдолду: {r.TotalBefore:0.00} → {r.TotalAfter:0.00} сом ({r.TotalAfter - r.TotalBefore:+0.00;-0.00}). Версия {r.Version}. Себеби: {r.Reason}. Ким: {r.By}, ПК: {r.Device}.",
                $"✎ {docNumber} (“{supplier}”) corrected: {r.TotalBefore:0.00} → {r.TotalAfter:0.00} som ({r.TotalAfter - r.TotalBefore:+0.00;-0.00}). Version {r.Version}. Reason: {r.Reason}. By: {r.By}, PC: {r.Device}.",
                $"✎ {docNumber} («{supplier}») düzeltildi: {r.TotalBefore:0.00} → {r.TotalAfter:0.00} som ({r.TotalAfter - r.TotalBefore:+0.00;-0.00}). Sürüm {r.Version}. Neden: {r.Reason}. Kim: {r.By}, PC: {r.Device}.",
                $"✎ {docNumber} («{supplier}») tuzatildi: {r.TotalBefore:0.00} → {r.TotalAfter:0.00} so'm ({r.TotalAfter - r.TotalBefore:+0.00;-0.00}). Versiya {r.Version}. Sabab: {r.Reason}. Kim: {r.By}, PK: {r.Device}."));
        _ = Task.Run(async () =>
        {
            var error = await TelegramBotService.SendAsync(text).ConfigureAwait(false);
            if (error != null)
                PosLogger.Log($"Правка накладной: уведомление владельцу не ушло ({error}).", "WARNING");
        });
    }
}
