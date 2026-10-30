using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-10-20, этап П3 переноса «Поставщиков» (docs/«Поставщики и долги поставщикам в NurCRM — исследование 2026-10-20.md», 3.2 и 5.2):
// приход «в долг» у кассы и программы владельца не создавал долг поставщику — сделку-долг на сайте создаёт фронтенд отдельным
// запросом (SupplierReceiptPage: POST clients/{id}/deals/ «Долги {имя}», kind=debt). Здесь — то же самое после успешного прихода,
// с защитой от двойного долга и локальной очередью «долг не записан».

/// <summary>Что записать: поставщик, метка прихода (receipt:&lt;id&gt; — по ней «своя» сделка находится при повторе), сумма, время прихода,
/// дата оплаты (first_due_date) и срок в днях; Source — receipt (приёмка), invoice (накладная), manual (старая закупка, «Записать долг»).</summary>
public sealed record SupplierDebtRequest(
    string SupplierId, string SupplierName, string Marker, string ReceiptId, double Amount, DateTime ReceiptAt, DateTime DueDate, int DueDays, string Source);

public enum SupplierDebtResultKind
{
    /// <summary>Касса создала сделку-долг.</summary>
    Created,
    /// <summary>Сервер уже создал сделку-долг на этот приход сам — ничего не создавали.</summary>
    ServerCreated,
    /// <summary>Долг уже был записан раньше (журнал «записан» или сделка с этой меткой найдена).</summary>
    AlreadyRecorded,
    /// <summary>Не записан (ошибка сервера, нет связи, ответ не дошёл) — запись в очереди «долг не записан».</summary>
    Failed,
    /// <summary>Журнал записи долгов повреждён — запись закрыта (ничего не отправлялось).</summary>
    Closed,
}

public sealed record SupplierDebtResult(SupplierDebtResultKind Kind, string? DealId, double Amount, string? Error)
{
    public bool Recorded => Kind is SupplierDebtResultKind.Created or SupplierDebtResultKind.ServerCreated or SupplierDebtResultKind.AlreadyRecorded;
}

/// <summary>Строка журнала записи долгов. Status: pending — запись начата или исход запроса неизвестен; done — долг записан
/// (Outcome: created — создала касса, server — сервер сам, found — найдена своя сделка по метке); failed — сервер отказал / не прочитать сделки.</summary>
public sealed class SupplierDebtEntry
{
    public string Key { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string SupplierId { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string ReceiptId { get; set; } = "";
    public double Amount { get; set; }
    public DateTime ReceiptAt { get; set; }
    public DateTime DueDate { get; set; }
    public int DueDays { get; set; }
    public string Source { get; set; } = "";
    public string Program { get; set; } = "";
    public string Status { get; set; } = "";
    public string? DealId { get; set; }
    public string? Outcome { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Когда запрос создания сделки ушёл впервые и в последний раз (окно поиска «своей» сделки без метки).</summary>
    public DateTime? FirstSentAt { get; set; }
    public DateTime? LastSentAt { get; set; }

    public bool IsOpen => Status is "pending" or "failed";

    public SupplierDebtRequest ToRequest() => new(SupplierId, SupplierName, Key, ReceiptId, Amount, ReceiptAt, DueDate, DueDays, Source);
}

/// <summary>Очередь «долг не записан» для показа: Closed — журнал повреждён (кнопки записи закрыты).</summary>
public sealed record SupplierDebtQueue(bool Closed, string? ClosedFile, IReadOnlyList<SupplierDebtEntry> Open);

/// <summary>2026-10-20, этап П3: «запись долга» поставщику после прихода «в долг».
///
/// Порядок (под межпроцессным замком — касса и программа владельца на одном ПК пишут один журнал):
///  1. журнал: метка уже «записан» — ничего не делаем; иначе строка pending (ДО любого запроса создания);
///  2. GET clients/{id}/deals/ — сделка с нашей меткой есть → записан; сделка-долг без чужой метки на ту же сумму, созданная от −2 до +10 мин
///     от прихода (или со ссылкой на id прихода) → сервер записал сам, не создаём; повтор после неизвестного исхода — ещё и «наша» сделка
///     (то же название и сумма) в окне отправок;
///  3. POST clients/{id}/deals/ — тем же телом, что сайт (title «Долги {имя}», kind debt, amount, note, client, debt_days 30 — как «1 месяц» сайта,
///     first_due_date = день прихода + срок, auto_schedule), в note — метка receipt:&lt;id прихода&gt;. Ключ повтора сайт не шлёт, сервер его
///     не обещает (исследование 6.9) — не шлём; защита — метка и перечитывание.
///  4. ответ не дошёл (обрыв, таймаут, 5xx) — строка остаётся pending, сразу перечитываем сделки по метке; отказ 4xx — failed.
/// Повреждённый журнал переименовывается в supplier-debts.bad-&lt;время&gt;.json, запись закрыта до разбора поддержкой.</summary>
public static class SupplierDebtRecorder
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Только для стенда проверки: другая папка журнала (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Только для стенда проверки: компания без входа в NurCRM.</summary>
    public static string? CompanyIdOverride { get; set; }

    /// <summary>Пауза перед перечитыванием сделок после неизвестного исхода (стенд ставит 0).</summary>
    public static TimeSpan UnknownRecheckDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Один журнал на компьютер для кассы и программы владельца (как debt-payments): владелец видит долги, не записанные
    /// кассой на этом ПК. Не в папке data — её AccountDataIsolation уносит при смене компании (строки и так с компанией).</summary>
    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "supplier-debts");

    private static string FilePath => Path.Combine(DataDir, "supplier-debts.json");
    private static string LockPath => Path.Combine(DataDir, "supplier-debts.lock");

    private const int LockWaitSeconds = 45;
    private const int KeepDone = 500;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object FileSync = new();
    private static string? _brokenPath;

    /// <summary>Журнал изменился (окна перечитывают очередь). Может прийти не с UI-потока.</summary>
    public static event Action? Changed;

    private static string CurrentCompany => CompanyIdOverride ?? CompanyInfoService.LastCompany?.Id ?? "";
    private static string CurrentProgram => AppMode.IsOwner ? "owner" : "kassa";

    // ------------------------------------------------------------------ метки

    public static string MarkerForReceipt(string receiptId) => "receipt:" + receiptId.Trim();
    public static string MarkerForKey(string key) => "receipt-key:" + key.Trim();
    /// <summary>Старая закупка без найденного прихода — метка по времени закупки.</summary>
    public static string MarkerForPurchase(DateTime at) => "purchase:" + at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>Название сделки — как у сайта («Долги {имя}»): так её узнаёт и сайт, и сверка двойных записей.</summary>
    public static string DealTitle(string supplierName) => "Долги " + supplierName.Trim();

    private static readonly Regex AnyMarker = new(@"(?<![\w:-])(receipt|receipt-key|purchase):[\w-]+", RegexOptions.CultureInvariant);

    private static string DealText(SupplierDeal d) => d.Note + " " + d.Title;

    /// <summary>В примечании или названии сделки есть ровно эта метка (не часть другой).</summary>
    public static bool HasMarker(SupplierDeal d, string marker) =>
        AnyMarker.Matches(DealText(d)).Any(m => string.Equals(m.Value, marker, StringComparison.Ordinal));

    private static bool HasAnyMarker(SupplierDeal d) => AnyMarker.IsMatch(DealText(d));

    private static readonly Regex KeyMarker = new(@"(?<![\w:-])receipt-key:[\w-]+", RegexOptions.CultureInvariant);
    private static readonly Regex ReceiptIdMarker = new(@"(?<![\w:-])receipt:[\w-]+", RegexOptions.CultureInvariant);
    private static readonly Regex NoteReceiptAt = new(@"приход (\d{2}\.\d{2}\.\d{4} \d{2}:\d{2})", RegexOptions.CultureInvariant);

    /// <summary>2026-10-21 (ревью денег, мелочь): сделка-долг с меткой «receipt-key:» (сервер не вернул номер прихода) — на приход
    /// в момент <paramref name="receiptAt"/> (время прихода из примечания сделки, окно −2…+10 мин) на ту же сумму (null — сумму не
    /// сверяем). По метке «receipt:id» её не найти, и «Записать долг» в карточке создавал второй долг.</summary>
    /// <param name="receiptMarker">true — наоборот: сделка с меткой «receipt:id» (для строки журнала с «receipt-key:»).</param>
    public static bool IsReceiptKeyDealFor(SupplierDeal d, DateTime receiptAt, double? amount = null, bool receiptMarker = false)
    {
        if (!d.IsDebt || !(receiptMarker ? ReceiptIdMarker : KeyMarker).IsMatch(DealText(d)))
            return false;
        if (amount is { } sum && Math.Abs(d.Amount - sum) >= 0.01)
            return false;
        var m = NoteReceiptAt.Match(d.Note ?? "");
        if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return false;
        // В примечании — минуты: начало окна на минуту шире.
        return at >= receiptAt.AddMinutes(-3) && at <= receiptAt.AddMinutes(10);
    }

    // ------------------------------------------------------------------ после прихода

    /// <summary>После успешного прихода «в долг»: метка по id прихода (нет id — по ключу прихода), сумма и время — по ответу сервера,
    /// а если их нет — по GET suppliers/receipts/{id}/ (только чтение), иначе по строкам приёмки и часам ПК. null — проведённых строк нет.</summary>
    public static async Task<SupplierDebtResult?> RecordAfterReceiptAsync(NurMarketApiClient api, PurchaseReceivingService.Supplier supplier,
        PurchaseReceivingService.PostResult result, int dueDays, string source, CancellationToken ct = default)
    {
        if (result.PostedLines.Count == 0)
            return null;
        var receiptId = result.ReceiptId?.Trim() ?? "";
        var marker = receiptId.Length > 0 ? MarkerForReceipt(receiptId) : result.ReceiptKey is { Length: > 0 } key ? MarkerForKey(key) : "";
        var computed = Math.Round(result.TotalAmount, 2);
        var amount = result.ReceiptTotal is > 0 and var t ? Math.Round(t, 2) : computed;
        var at = result.ReceiptAt;
        if (receiptId.Length > 0 && (at is null || result.ReceiptTotal is null))
        {
            try
            {
                var r = await new SupplierApiService(api).GetReceiptAsync(receiptId, ct).ConfigureAwait(false);
                at ??= r.CreatedAt;
                if (result.ReceiptTotal is null && r.Total > 0)
                    amount = Math.Round(r.Total, 2);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Долг поставщику: приход {receiptId} не перечитан ({ex.Message}) — сумма и время по приёмке.", "WARNING");
            }
        }
        if (Math.Abs(amount - computed) > 0.01)
            PosLogger.Log($"Долг поставщику: сумма прихода {receiptId} по серверу {amount:0.00}, по строкам приёмки {computed:0.00} — берём серверную.", "WARNING");
        var receiptAt = at ?? DateTime.Now;
        var days = Math.Clamp(dueDays, 1, 3650);
        if (marker.Length == 0)
        {
            PosLogger.Log($"Долг поставщику {supplier.Id}: сервер не вернул номер прихода и ключа нет — долг {amount:0.00} не записан.", "ERROR");
            return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, amount, T("сервер не вернул номер прихода", "сервер кириштин номерин кайтарган жок",
                "the server didn't return the delivery number", "sunucu giriş numarasını döndürmedi", "server kirim raqamini qaytarmadi"));
        }
        return await RecordAsync(api, new SupplierDebtRequest(supplier.Id, supplier.Name, marker, receiptId, amount, receiptAt,
            receiptAt.Date.AddDays(days), days, source), ct).ConfigureAwait(false);
    }

    /// <summary>Повтор из очереди «долг не записан» (кнопка «Записать долг») — та же защита, что при приёмке.</summary>
    public static Task<SupplierDebtResult> RetryAsync(NurMarketApiClient api, SupplierDebtEntry entry, CancellationToken ct = default) =>
        RecordAsync(api, entry.ToRequest(), ct);

    // ------------------------------------------------------------------ запись

    public static async Task<SupplierDebtResult> RecordAsync(NurMarketApiClient api, SupplierDebtRequest req, CancellationToken ct = default)
    {
        if (!(req.Amount > 0.004) || double.IsInfinity(req.Amount) || string.IsNullOrWhiteSpace(req.SupplierId) || string.IsNullOrWhiteSpace(req.Marker))
            return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, req.Amount, T("нет суммы или поставщика", "сумма же жеткирүүчү жок",
                "no amount or supplier", "tutar veya tedarikçi yok", "summa yoki yetkazib beruvchi yo'q"));

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fileLock = await AcquireFileLockAsync().ConfigureAwait(false);
            if (fileLock is null)
                return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, req.Amount, T("журнал долгов занят другой программой — повторите",
                    "карыздар журналы башка программада бош эмес — кайталаңыз", "the debt log is busy in another program — try again",
                    "borç günlüğü başka programda meşgul — tekrar deneyin", "qarzlar jurnali boshqa dasturda band — qayta urining"));

            var (list, readError) = ReadForChange();
            if (readError is not null)
                return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, req.Amount, readError);
            if (list is null)
            {
                PosLogger.Log($"Долг поставщику {req.SupplierId} ({req.Marker}) на {req.Amount:0.00}: журнал повреждён — запись закрыта, запросов нет.", "ERROR");
                return new SupplierDebtResult(SupplierDebtResultKind.Closed, null, req.Amount, _brokenPath);
            }

            var company = CurrentCompany;
            var entry = list.FirstOrDefault(e => e.Key == req.Marker && SameCompany(e.CompanyId, company));
            if (entry is { Status: "done" })
            {
                PosLogger.Log($"Долг поставщику {req.SupplierId} ({req.Marker}): уже записан ({entry.Outcome}, сделка {entry.DealId}) — повторно не создаём.", "PAYMENT");
                return new SupplierDebtResult(SupplierDebtResultKind.AlreadyRecorded, entry.DealId, entry.Amount, null);
            }
            var now = DateTime.Now;
            if (entry is null)
            {
                entry = new SupplierDebtEntry
                {
                    Key = req.Marker, CompanyId = company, SupplierId = req.SupplierId, SupplierName = req.SupplierName, ReceiptId = req.ReceiptId,
                    Amount = Math.Round(req.Amount, 2), ReceiptAt = req.ReceiptAt, DueDate = req.DueDate.Date, DueDays = req.DueDays, Source = req.Source,
                    Program = CurrentProgram, CreatedAt = now,
                };
                list.Add(entry);
            }
            else if (req.DueDays > 0 && (entry.DueDate.Date != req.DueDate.Date || entry.DueDays != req.DueDays))
            {
                // 2026-10-21 (ревью денег, мелочь): повтор из окна «Записать долг» с новым сроком — берём его (было: старый срок
                // строки журнала, хотя в окне показан новый).
                PosLogger.Log($"Долг поставщику {req.SupplierId} ({req.Marker}): срок изменён при повторе — {entry.DueDate:dd.MM.yyyy} → {req.DueDate:dd.MM.yyyy}.", "PAYMENT");
                entry.DueDate = req.DueDate.Date;
                entry.DueDays = req.DueDays;
            }
            entry.Status = "pending";
            entry.Attempts++;
            entry.UpdatedAt = now;
            entry.Error = null;
            // Строка очереди — ДО любого запроса: не записалась — сделку не создаём.
            if (!Save(list))
                return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, entry.Amount, JournalWriteText);
            RaiseChanged();

            // 1. Перечитать сделки поставщика: долг уже есть (наш прошлый запрос или сервер сам)?
            List<SupplierDeal> deals;
            try
            {
                deals = await LoadDealsAsync(api, entry.SupplierId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail(list, entry, "failed", ex, "сделки поставщика не прочитаны");
            }
            if (Match(deals, entry, list) is { } found)
                return Done(list, entry, found.Deal.Id, found.Outcome);

            // 2. Создать сделку-долг тем же телом, что сайт.
            entry.FirstSentAt ??= DateTime.Now;
            entry.LastSentAt = DateTime.Now;
            if (!Save(list))
                return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, entry.Amount, JournalWriteText);
            var body = DealBody(entry);
            PosLogger.Log($"Долг поставщику {entry.SupplierId} ({entry.Key}): создаю сделку «{body["title"]}» на {body["amount"]}, оплата до {body["first_due_date"]} (попытка {entry.Attempts}).", "PAYMENT");
            JsonElement response;
            try
            {
                response = await api.RequestAsync(HttpMethod.Post, $"api/main/clients/{Uri.EscapeDataString(entry.SupplierId.Trim())}/deals/", body, null, ct, RequestTimeout)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (PendingIdempotencyKey.IsOutcomeUnknown(ex))
            {
                // Обрыв после отправки: сделка могла создаться. Не повторяем вслепую — сразу перечитываем по метке.
                entry.Error = Describe(ex);
                Save(list);
                PosLogger.Log($"Долг поставщику {entry.SupplierId} ({entry.Key}): ответ на создание сделки не получен ({ex.GetType().Name}: {ex.Message}) — перечитываю сделки.", "WARNING");
                try
                {
                    if (UnknownRecheckDelay > TimeSpan.Zero)
                        await Task.Delay(UnknownRecheckDelay, CancellationToken.None).ConfigureAwait(false);
                    var again = await LoadDealsAsync(api, entry.SupplierId, CancellationToken.None).ConfigureAwait(false);
                    if (Match(again, entry, list) is { } late)
                        return Done(list, entry, late.Deal.Id, late.Outcome == "server" ? "server" : "created", createdKind: true);
                }
                catch (Exception recheck)
                {
                    PosLogger.Log($"Долг поставщику {entry.SupplierId} ({entry.Key}): сделки после обрыва не перечитаны ({recheck.Message}).", "WARNING");
                }
                return Fail(list, entry, "pending", ex, "исход создания сделки неизвестен");
            }
            catch (Exception ex)
            {
                return Fail(list, entry, "failed", ex, "сервер отказал в создании сделки");
            }

            var dealId = SupplierApiService.Str(response, "id");
            if (dealId.Length == 0)
            {
                // Ответ без номера: ищем созданную сделку по метке, не создаём вторую.
                try
                {
                    var again = await LoadDealsAsync(api, entry.SupplierId, CancellationToken.None).ConfigureAwait(false);
                    if (Match(again, entry, list) is { } late)
                        return Done(list, entry, late.Deal.Id, "created", createdKind: true);
                }
                catch (Exception recheck)
                {
                    PosLogger.Log($"Долг поставщику {entry.SupplierId} ({entry.Key}): сделки после ответа без номера не перечитаны ({recheck.Message}).", "WARNING");
                }
                return Fail(list, entry, "pending", new InvalidOperationException("сервер ответил без номера сделки"), "сервер ответил без номера сделки");
            }
            if (response.ValueKind == JsonValueKind.Object && !SupplierApiService.Str(response, "note").Contains(entry.Key, StringComparison.Ordinal))
                PosLogger.Log($"Долг поставщику {entry.SupplierId}: сервер не сохранил метку {entry.Key} в примечании сделки {dealId} — повтор найдёт её по журналу.", "WARNING");
            return Done(list, entry, dealId, "created");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string JournalWriteText => T("журнал записи долгов не сохранён на диск — долг не отправлен", "карыз жазуу журналы дискке сакталган жок — карыз жөнөтүлгөн жок",
        "the debt log couldn't be saved to disk — the debt wasn't sent", "borç kayıt günlüğü diske kaydedilemedi — borç gönderilmedi",
        "qarz yozish jurnali diskka saqlanmadi — qarz yuborilmadi");

    /// <summary>Тело как у сайта (SupplierReceiptPage → deals/create, путь v2): срок — debt_months, дата первой оплаты — срок приёмки.
    /// Один взнос на дату оплаты (на NBS сделка сайта «Долги NUR»: debt_months 1, один взнос на first_due_date).
    /// 2026-10-20: было debt_days 30 — живая проверка на NBS: сервер разбил долг 1 сом на 30 ежедневных взносов по 0,03.
    /// Сервер понимает debt_days как число ежедневных взносов, debt_months — как число ежемесячных; срок до 45 дней — 1 месяц.</summary>
    public static Dictionary<string, object?> DealBody(SupplierDebtEntry e) => new()
    {
        ["title"] = DealTitle(e.SupplierName),
        ["kind"] = "debt",
        ["amount"] = Math.Round(e.Amount, 2).ToString("0.00", CultureInfo.InvariantCulture),
        ["note"] = $"{e.Key} · приход {e.ReceiptAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}, срок {e.DueDays} дн. · NurCRM",
        ["client"] = e.SupplierId,
        // 2026-10-21 (ревью денег, A7): всегда один взнос — сервер понимает debt_months как число ЕЖЕМЕСЯЧНЫХ взносов, и
        // Round(DueDays / 30) разбивал долг на 60/90 (и даже 45) дней на 2–3 взноса. Срок — только в first_due_date.
        ["debt_months"] = 1,
        ["first_due_date"] = e.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["auto_schedule"] = true,
    };

    /// <summary>Есть ли уже долг на этот приход: (1) сделка с нашей меткой; (2) сервер сам — сделка-долг без чужой метки со ссылкой на id
    /// прихода или на ту же сумму, созданная от −2 до +10 мин от прихода; (3) после прошлой отправки — «наша» сделка без метки (то же
    /// название и сумма) от −2 мин первой до +10 мин последней отправки. Сделки, уже отнесённые журналом к другим приходам, не берём.</summary>
    private static (SupplierDeal Deal, string Outcome)? Match(IReadOnlyList<SupplierDeal> deals, SupplierDebtEntry e, IReadOnlyList<SupplierDebtEntry> journal)
    {
        var claimed = journal.Where(x => x.Status == "done" && !string.IsNullOrEmpty(x.DealId) && x.Key != e.Key).Select(x => x.DealId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var debts = deals.Where(d => d.IsDebt && d.Id.Length > 0 && !claimed.Contains(d.Id)).ToList();
        if (debts.FirstOrDefault(d => HasMarker(d, e.Key)) is { } mine)
            return (mine, "found");
        // 2026-10-21 (ревью денег, мелочь): тот же приход уже записан с меткой «receipt-key:» (тогда сервер не вернул номер
        // прихода) — второй долг не создаём.
        // И наоборот: строка «receipt-key:» — приход уже записан по номеру («receipt:id», кнопкой в карточке).
        var keyEntry = e.Key.StartsWith("receipt-key:", StringComparison.Ordinal);
        if ((keyEntry || e.Key.StartsWith("receipt:", StringComparison.Ordinal))
            && debts.FirstOrDefault(d => IsReceiptKeyDealFor(d, e.ReceiptAt, e.Amount, receiptMarker: keyEntry)) is { } byKey)
            return (byKey, "found");
        var free = debts.Where(d => !HasAnyMarker(d)).ToList();
        if (e.ReceiptId.Length >= 8 && free.FirstOrDefault(d => DealText(d).Contains(e.ReceiptId, StringComparison.OrdinalIgnoreCase)) is { } linked)
            return (linked, "server");
        bool SameSum(SupplierDeal d) => Math.Abs(d.Amount - e.Amount) < 0.01;
        bool In(SupplierDeal d, DateTime from, DateTime to) => d.CreatedAt is { } at && at >= from && at <= to;
        if (free.Where(SameSum).FirstOrDefault(d => In(d, e.ReceiptAt.AddMinutes(-2), e.ReceiptAt.AddMinutes(10))) is { } server)
            return (server, "server");
        if (e.FirstSentAt is { } first && e.LastSentAt is { } last
            && free.Where(SameSum).FirstOrDefault(d => d.Title == DealTitle(e.SupplierName) && In(d, first.AddMinutes(-2), last.AddMinutes(10))) is { } ours)
            return (ours, "found");
        return null;
    }

    private static SupplierDebtResult Done(List<SupplierDebtEntry> list, SupplierDebtEntry e, string dealId, string outcome, bool createdKind = false)
    {
        e.Status = "done";
        e.DealId = dealId;
        e.Outcome = outcome;
        e.Error = null;
        e.UpdatedAt = DateTime.Now;
        var saved = Save(list);
        RaiseChanged();
        var kind = outcome switch
        {
            "server" => SupplierDebtResultKind.ServerCreated,
            "created" => SupplierDebtResultKind.Created,
            _ => createdKind ? SupplierDebtResultKind.Created : SupplierDebtResultKind.AlreadyRecorded,
        };
        PosLogger.Log(kind switch
        {
            SupplierDebtResultKind.ServerCreated => $"Долг поставщику {e.SupplierId} ({e.Key}) на {e.Amount:0.00}: сервер создал долг сам (сделка {dealId}) — не создаём.",
            SupplierDebtResultKind.Created => $"Долг поставщику {e.SupplierId} ({e.Key}) на {e.Amount:0.00}: записан, сделка {dealId}.",
            _ => $"Долг поставщику {e.SupplierId} ({e.Key}) на {e.Amount:0.00}: уже записан — найдена сделка {dealId}, вторая не создаётся.",
        } + (saved ? "" : " Журнал не сохранён — повтор найдёт сделку по метке."), "PAYMENT");
        return new SupplierDebtResult(kind, dealId, e.Amount, null);
    }

    private static SupplierDebtResult Fail(List<SupplierDebtEntry> list, SupplierDebtEntry e, string status, Exception ex, string what)
    {
        e.Status = status;
        e.Error = Describe(ex);
        e.UpdatedAt = DateTime.Now;
        Save(list);
        RaiseChanged();
        PosLogger.Log($"Долг поставщику {e.SupplierId} ({e.Key}) на {e.Amount:0.00} не записан: {what} ({ex.GetType().Name}: {ex.Message}) — в очереди «долг не записан» ({status}).", "WARNING");
        return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, e.Amount, e.Error);
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi"),
        HttpRequestException => T("нет связи с сервером", "сервер менен байланыш жок", "no connection to the server", "sunucuyla bağlantı yok", "server bilan aloqa yo'q"),
        _ => ex.Message.Length > 300 ? ex.Message[..300] : ex.Message,
    };

    /// <summary>Сделки-долги поставщика (kind=debt; сервер, не знающий фильтра, отдаёт все — берём только debt), все страницы.</summary>
    private static async Task<List<SupplierDeal>> LoadDealsAsync(NurMarketApiClient api, string supplierId, CancellationToken ct)
    {
        var result = new List<SupplierDeal>();
        for (var page = 1; page <= 20; page++)
        {
            var query = new Dictionary<string, string> { ["kind"] = "debt", ["page_size"] = "500" };
            if (page > 1)
                query["page"] = page.ToString(CultureInfo.InvariantCulture);
            var d = await api.RequestAsync(HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(supplierId.Trim())}/deals/", null, query, ct, RequestTimeout)
                .ConfigureAwait(false);
            var rows = d.ValueKind == JsonValueKind.Array ? d
                : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
            if (rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("сервер вернул сделки не списком");
            result.AddRange(rows.EnumerateArray().Select(SupplierApiService.ParseDeal));
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                return result;
        }
        throw new InvalidOperationException("у поставщика больше 20 страниц сделок — не прочитаны все");
    }

    // ------------------------------------------------------------------ очередь для окон

    /// <summary>Не записанные долги текущей компании (pending/failed), новые сверху; supplierId — только этого поставщика.</summary>
    public static SupplierDebtQueue LoadQueue(string? supplierId = null)
    {
        lock (FileSync)
        {
            List<SupplierDebtEntry>? list;
            try
            {
                list = ReadLocked();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги поставщикам: журнал не прочитан ({ex.Message}).", "WARNING");
                return new SupplierDebtQueue(false, null, Array.Empty<SupplierDebtEntry>());
            }
            if (list is null)
                return new SupplierDebtQueue(true, _brokenPath, Array.Empty<SupplierDebtEntry>());
            var company = CurrentCompany;
            return new SupplierDebtQueue(false, null, list.Where(e => e.IsOpen && SameCompany(e.CompanyId, company)
                    && (supplierId is null || string.Equals(e.SupplierId, supplierId, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.ReceiptAt).ToList());
        }
    }

    /// <summary>2026-10-27, аудит владельца С-2: сумма записанного журналом долга по метке прихода (плитка «В долг» в «Закупках» считала только
    /// местные заметки и показывала 0,00 при строке «В долг»).</summary>
    public static IReadOnlyDictionary<string, double> DoneAmounts()
    {
        lock (FileSync)
        {
            try
            {
                return (ReadLocked() ?? new()).Where(e => e.Status == "done" && SameCompany(e.CompanyId, CurrentCompany))
                    .GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Amount, StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, double>();
            }
        }
    }

    /// <summary>Записанный журналом долг по метке (для сверки «старых» закупок: уже записан — не предлагать снова).</summary>
    public static IReadOnlySet<string> DoneKeys()
    {
        lock (FileSync)
        {
            try
            {
                return (ReadLocked() ?? new()).Where(e => e.Status == "done" && SameCompany(e.CompanyId, CurrentCompany)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                return new HashSet<string>();
            }
        }
    }

    private static bool SameCompany(string entryCompany, string company) =>
        entryCompany.Length == 0 || company.Length == 0 || string.Equals(entryCompany, company, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ текст для кассира и владельца

    public static string ResultText(SupplierDebtResult r) => r.Kind switch
    {
        SupplierDebtResultKind.Created => T($"Долг поставщику записан: {MoneyText.Som(r.Amount)}.", $"Жеткирүүчүгө карыз жазылды: {MoneyText.Som(r.Amount)}.",
            $"Debt to the supplier recorded: {MoneyText.Som(r.Amount)}.", $"Tedarikçiye borç kaydedildi: {MoneyText.Som(r.Amount)}.", $"Yetkazib beruvchiga qarz yozildi: {MoneyText.Som(r.Amount)}."),
        SupplierDebtResultKind.ServerCreated => T($"Сервер записал долг сам: {MoneyText.Som(r.Amount)}.", $"Сервер карызды өзү жазды: {MoneyText.Som(r.Amount)}.",
            $"The server recorded the debt itself: {MoneyText.Som(r.Amount)}.", $"Sunucu borcu kendisi kaydetti: {MoneyText.Som(r.Amount)}.", $"Server qarzni o'zi yozdi: {MoneyText.Som(r.Amount)}."),
        SupplierDebtResultKind.AlreadyRecorded => T($"Долг поставщику уже записан: {MoneyText.Som(r.Amount)}.", $"Жеткирүүчүгө карыз мурунтан жазылган: {MoneyText.Som(r.Amount)}.",
            $"The debt to the supplier is already recorded: {MoneyText.Som(r.Amount)}.", $"Tedarikçiye borç zaten kayıtlı: {MoneyText.Som(r.Amount)}.", $"Yetkazib beruvchiga qarz allaqachon yozilgan: {MoneyText.Som(r.Amount)}."),
        SupplierDebtResultKind.Closed => T("Журнал записи долгов повреждён — долг не записан. Обратитесь в поддержку; долг можно записать на сайте NurCRM.",
            "Карыз жазуу журналы бузулган — карыз жазылган жок. Колдоо кызматына кайрылыңыз; карызды NurCRM сайтында жазса болот.",
            "The debt log is damaged — the debt wasn't recorded. Contact support; the debt can be recorded on the NurCRM website.",
            "Borç kayıt günlüğü bozuk — borç kaydedilmedi. Desteğe başvurun; borç NurCRM sitesinde kaydedilebilir.",
            "Qarz yozish jurnali buzilgan — qarz yozilmadi. Qo'llab-quvvatlashga murojaat qiling; qarzni NurCRM saytida yozish mumkin."),
        _ => T("Долг не записан — повторите в «Поставщиках» (кнопка «Записать долг»).", "Карыз жазылган жок — «Жеткирүүчүлөр» бөлүмүндө кайталаңыз («Карызды жазуу» баскычы).",
                "The debt wasn't recorded — retry in “Suppliers” (the “Record debt” button).", "Borç kaydedilmedi — «Tedarikçiler»de tekrarlayın («Borcu kaydet» düğmesi).",
                "Qarz yozilmadi — «Yetkazib beruvchilar»da qayta urining («Qarzni yozish» tugmasi).")
            + (string.IsNullOrWhiteSpace(r.Error) ? "" : " " + T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + r.Error),
    };

    // ------------------------------------------------------------------ журнал на диске

    private sealed record Holder(FileStream Stream) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    /// <summary>Межпроцессный замок (FileShare.None) на всё время записи долга — касса и программа владельца не создадут сделку вдвоём.</summary>
    private static async Task<IDisposable?> AcquireFileLockAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(LockWaitSeconds);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                return new Holder(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги поставщикам: журнал занят другой программой или недоступен ({ex.GetType().Name}: {ex.Message}) — запись не начата.", "WARNING");
                return null;
            }
        }
    }

    /// <summary>(null, null) — журнал повреждён; (null, текст) — файл не прочитать (занят, нет прав): это НЕ пустой журнал, запись не начинаем.</summary>
    private static (List<SupplierDebtEntry>? List, string? Error) ReadForChange()
    {
        lock (FileSync)
        {
            try
            {
                return (ReadLocked(), null);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги поставщикам: журнал не прочитан ({ex.Message}) — запись не начата.", "WARNING");
                return (null, T("журнал записи долгов не прочитан — повторите", "карыз жазуу журналы окулган жок — кайталаңыз",
                    "the debt log couldn't be read — try again", "borç kayıt günlüğü okunamadı — tekrar deneyin", "qarz yozish jurnali o'qilmadi — qayta urining"));
            }
        }
    }

    /// <summary>Чтение журнала. null — повреждён: переименован в supplier-debts.bad-&lt;время&gt;.json (или такой файл уже лежит в папке),
    /// запись закрыта. Ошибка ввода-вывода — исключение (не пустой журнал).</summary>
    private static List<SupplierDebtEntry>? ReadLocked()
    {
        if (_brokenPath is not null)
            return null;
        if (Directory.Exists(DataDir) && Directory.GetFiles(DataDir, "supplier-debts.bad-*.json").FirstOrDefault() is { } bad)
        {
            _brokenPath = bad;
            PosLogger.Log($"Долги поставщикам: в папке повреждённый журнал ({bad}) — запись долгов закрыта до разбора поддержкой.", "ERROR");
            return null;
        }
        if (!File.Exists(FilePath))
            return new();
        string text;
        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs))
            text = reader.ReadToEnd();
        string? problem = null;
        List<SupplierDebtEntry>? list = null;
        try
        {
            var raw = JsonSerializer.Deserialize<List<SupplierDebtEntry?>>(text);
            if (raw is null)
                problem = "документ null";
            else
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in raw)
                {
                    if (e is null)
                        problem = "пустая запись";
                    else if (string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.SupplierId))
                        problem = "запись без метки или поставщика";
                    else if (!(e.Amount > 0) || double.IsInfinity(e.Amount))
                        problem = $"сумма {e.Amount}";
                    else if (e.Status is not ("pending" or "done" or "failed"))
                        problem = $"статус «{e.Status}»";
                    else if (!keys.Add(e.CompanyId + "|" + e.Key))
                        problem = $"две записи {e.Key}";
                    if (problem is not null)
                        break;
                }
                if (problem is null)
                    list = raw.Select(e => e!).ToList();
            }
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
        }
        if (list is not null)
            return list;
        var badPath = Path.Combine(DataDir, $"supplier-debts.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Move(FilePath, badPath);
        }
        catch (Exception moveEx)
        {
            PosLogger.Log($"Долги поставщикам: повреждённый журнал не переименован ({moveEx.Message}).", "ERROR");
            badPath = FilePath;
        }
        _brokenPath = badPath;
        PosLogger.Log($"Долги поставщикам: журнал повреждён ({problem}) — сохранён как {badPath}; запись долгов закрыта.", "ERROR");
        RaiseChanged();
        return null;
    }

    /// <summary>Атомарная запись (временный файл + замена). Все незаписанные — всегда; записанные — последние KeepDone.</summary>
    private static bool Save(List<SupplierDebtEntry> list)
    {
        lock (FileSync)
        {
            if (_brokenPath is not null)
                return false;
            try
            {
                var keep = list.Where(e => e.Status != "done")
                    .Concat(list.Where(e => e.Status == "done").OrderByDescending(e => e.UpdatedAt).Take(KeepDone))
                    .OrderBy(e => e.CreatedAt).ToList();
                Directory.CreateDirectory(DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(keep));
                File.Move(tmp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Долги поставщикам: журнал не записан ({ex.Message}).", "ERROR");
                return false;
            }
        }
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долги поставщикам: обработчик изменения журнала упал ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Только для стенда проверки: снять признак «повреждён» (новый прогон в той же программе).</summary>
    public static void ResetForTests() => _brokenPath = null;
}
