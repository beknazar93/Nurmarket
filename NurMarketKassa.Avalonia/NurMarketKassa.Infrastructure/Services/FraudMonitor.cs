using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-Б, функция 2): живая часть «Подозрительных операций». Окна кассы сообщают сюда действия (возврат, скидка, удаление строки, убыток,
// закрытие смены с недостачей, PIN старшего); монитор по правилам решает, подозрительно ли это, пишет событие в журнал (FraudEventStore) и шлёт владельцу в Telegram.
// Всё — в фоне и без исключений наружу: подозрительность не должна мешать продаже. Выключено (FraudRules.Enabled = false) — монитор молчит, журнал не пухнет.
// Тихие часы: сообщения в это время не уходят сразу, а копятся в журнале и одним письмом уходят утром (FlushUnsentAsync — запускает дежурный программы владельца).

public static class FraudMonitor
{
    private static readonly object NotifyGate = new();
    private static readonly SemaphoreSlim FlushLock = new(1, 1);
    private static Timer? _digestTimer;

    private static string NotifiedFile => Path.Combine(AntiFraudSettingsStore.Folder, "fraud-notified.json");

    // 2026-11-06: В3 Л5-22 — замок на время «проверил, что не отправляли → отправил → отметил» общий для кассы и программы владельца на этом ПК (раньше lock был только внутри процесса:
    // обе программы могли одновременно прочитать «не отправляли» и отправить одно сообщение дважды).
    private static string SendLockPath => Path.Combine(AntiFraudSettingsStore.Folder, "fraud-send.lock");
    private static readonly TimeSpan SendLockWait = TimeSpan.FromSeconds(20);

    // ───────── что сообщают окна кассы ─────────

    /// <summary>Возврат подтверждён кассиром и уходит на сервер.</summary>
    public static void OnReturn(string? saleId, string? receiptNumber, double amount, bool hasReceipt = true)
    {
        try // 2026-11-05 (ревью волн 0–3, п. 14): как остальные методы — сбой монитора не должен мешать возврату
        {
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled || !FraudAnalyzer.IsReturnSuspicious(amount, hasReceipt, r))
                return;
            Report(NewEvent(!hasReceipt && r.ReturnNoReceipt ? FraudKinds.ReturnNoReceipt : FraudKinds.Return, amount, subject: ReceiptSubject(receiptNumber), saleId: saleId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Антифрод: возврат не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Скидка применена (percent — процент, amount — сумма скидки в сомах, если известна).</summary>
    public static void OnDiscount(double percent, double amount, string? subject)
    {
        try // 2026-11-05 (ревью волн 0–3, п. 14): сбой монитора не должен мешать скидке
        {
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled || !FraudAnalyzer.IsDiscountSuspicious(percent, r))
                return;
            var e = NewEvent(FraudKinds.Discount, amount, subject: subject);
            e.Value = Math.Round(percent, 1);
            Report(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Антифрод: скидка не записана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Строки убраны из чека (после записи в журнал удалений): крупные — сразу, «много за смену» — когда счётчик смены перешёл порог.</summary>
    public static void OnRemovals(IReadOnlyList<CartRemovalRecord> records)
    {
        var r = AntiFraudSettingsStore.Current.Fraud;
        if (!r.Enabled || records.Count == 0)
            return;
        var batch = records.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                var events = FraudAnalyzer.FromRemovals(batch, r).Where(e => e.Kind == FraudKinds.Removal).ToList();
                if (r.RemovalMany && batch[0].ShiftId is { Length: > 0 } shiftId)
                {
                    // Журнал удалений пишется в очереди: дождаться, чтобы счётчик смены включал и эти записи.
                    await CartRemovalLogStore.FlushAsync().ConfigureAwait(false);
                    var inShift = CartRemovalLogStore.ReadAll().Count(x => x.ShiftId == shiftId && x.Kind != CartRemovalLogStore.KindShiftClose);
                    var before = inShift - batch.Count(x => x.Kind != CartRemovalLogStore.KindShiftClose);
                    // Сообщаем один раз — в момент, когда счётчик смены перешёл порог.
                    if (before <= r.RemovalPerShift && inShift > r.RemovalPerShift)
                        events.Add(new FraudEvent
                        {
                            Id = "many|" + shiftId, At = DateTimeOffset.Now, Kind = FraudKinds.RemovalMany, Cashier = batch[0].Cashier,
                            CompanyId = batch[0].CompanyId, ShiftId = shiftId, Value = inShift,
                        });
                }
                foreach (var e in events)
                    Report(e);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Антифрод: удаление из чека не проверено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        });
    }

    /// <summary>Оплаченный чек с ценой ниже закупки (запись «Продажи в убыток»).</summary>
    public static void OnLossSale(LossSaleRecord record)
    {
        try
        {
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled)
                return;
            foreach (var e in FraudAnalyzer.FromLoss(new[] { record }, r))
                Report(e);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: продажа в убыток не проверена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Смена закрыта: недостача от порога — событие (формула разницы та же, что у оповещений и сводки смены).</summary>
    public static void OnShiftClosed(ShiftModel shift, decimal deposits, decimal withdrawals)
    {
        try
        {
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled || ShopAlerts.ShiftDifference(shift, deposits, withdrawals) is not { } diff || diff >= 0)
                return;
            var shortage = (double)(-diff);
            if (!FraudAnalyzer.IsShortageSuspicious(shortage, r))
                return;
            var e = NewEvent(FraudKinds.Shortage, shortage);
            e.Id = "short|" + shift.Id;
            e.ShiftId = shift.Id;
            if (!string.IsNullOrWhiteSpace(shift.Cashier))
                e.Cashier = shift.Cashier!;
            Report(e);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: недостача смены не проверена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Старший ввёл PIN (ok) или не ввёл/ошибся (denied). Пишется в журнал всегда, когда включены права по действиям, — «каждое подтверждение».</summary>
    public static void OnPin(GuardAction action, double amount, bool granted, string? subject = null)
    {
        try
        {
            var e = NewEvent(granted ? FraudKinds.PinOk : FraudKinds.PinDenied, action == GuardAction.Discount ? 0 : amount, subject: action.ToString());
            if (action == GuardAction.Discount)
                e.Value = Math.Round(amount, 1); // у скидки значение — процент, а не сомы
            e.Note = subject;
            PosLogger.Log($"ФРОД: {FraudText.Describe(e)}", "SECURITY");
            FraudEventStore.Append(e);
            // В Telegram — только если включён мониторинг и отказ (подтверждение старшего — тихая запись в журнал).
            if (!granted && AntiFraudSettingsStore.Current.Fraud is { Enabled: true, Telegram: true })
                _ = NotifyAsync(new[] { e });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: PIN не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Значения <see cref="FraudEvent.Subject"/> события «защита выключена».</summary>
    public const string ProtectionGuard = "guard";
    public const string ProtectionFraud = "fraud";

    /// <summary>2026-11-06: В3 (хвост, ФМ) — защиту (PIN старшего по действиям, журнал подозрительных операций) выключили: в журнал антифрода пишется, КТО и что выключил
    /// (раньше выключатель менял файл настроек молча). В Telegram уходит, если мониторинг ещё включён (иначе — только запись в журнал).</summary>
    public static void OnProtectionOff(string protection)
    {
        try
        {
            var e = NewEvent(FraudKinds.ProtectionOff, 0, subject: protection);
            Report(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Антифрод: выключение защиты не записано ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-11-05 (ревью волн 0–3, С13): смена с недостачей закрыта без PIN старшего, потому что нет связи с сервером — запись для владельца.</summary>
    public static void OnShortagePinSkipped(double shortage)
    {
        var e = NewEvent(FraudKinds.Shortage, shortage);
        e.Note = "PIN старшего не проверен — нет связи; подтвердить позже";
        Report(e);
    }

    // ───────── журнал и Telegram ─────────

    internal static void Report(FraudEvent e)
    {
        try
        {
            PosLogger.Log($"ФРОД: {FraudText.Describe(e)}", "SECURITY");
            FraudEventStore.Append(e);
            if (AntiFraudSettingsStore.Current.Fraud.Telegram)
                _ = NotifyAsync(new[] { e });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: событие не записано ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static FraudEvent NewEvent(string kind, double amount, string? subject = null, string? saleId = null) => new()
    {
        Kind = kind,
        Amount = amount,
        Subject = subject,
        SaleId = saleId,
        ShiftId = PosApp.ActiveShiftId,
        CompanyId = CompanyInfoService.LastCompany?.Id,
        Cashier = !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName) ? PosApp.CurrentUserDisplayName! : PosApp.CurrentUserId ?? "",
    };

    private static string? ReceiptSubject(string? number) => string.IsNullOrWhiteSpace(number) ? null : "№" + number.Trim().TrimStart('№');

    /// <summary>Тихие часы по настройке антифрода (через полночь тоже).</summary>
    public static bool InQuietHours(FraudRules r, DateTime now) =>
        ShopAlerts.InQuietHours(new ShopAlertRules { QuietFromHour = r.QuietFromHour, QuietToHour = r.QuietToHour }, now);

    /// <summary>Сообщение владельцу о событиях. Тихие часы, выключенный бот или Telegram — событие остаётся «не отправленным» и уйдёт позже (FlushUnsentAsync).</summary>
    private static async Task NotifyAsync(IReadOnlyList<FraudEvent> events)
    {
        try
        {
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled || !r.Telegram || !TelegramBotService.IsConfigured || !TariffGate.CanUseTelegramBot || InQuietHours(r, DateTime.Now))
                return;
            var fresh = events.Where(e => FraudKinds.Weight(e.Kind) > 0 && !WasNotified(e.Id)).ToList();
            if (fresh.Count == 0)
                return;
            using var crossLock = await CrossProcessLock.TryAcquireAsync(SendLockPath, SendLockWait).ConfigureAwait(false);
            if (crossLock is null)
                return; // другая программа сейчас отправляет — событие осталось «не сообщённым» и уйдёт позже (FlushUnsentAsync)
            fresh = fresh.Where(e => !WasNotified(e.Id)).ToList(); // пока ждали замок, другая программа могла уже отправить
            if (fresh.Count == 0)
                return;
            var error = await TelegramBotService.SendAsync(BuildMessage(fresh)).ConfigureAwait(false);
            if (error is null)
                MarkNotified(fresh.Select(e => e.Id));
            else
                PosLogger.Log($"Антифрод: сообщение в Telegram не ушло ({error}).", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: сообщение в Telegram не отправлено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Текст сообщения (HTML для Telegram): заголовок и до 10 строк.</summary>
    public static string BuildMessage(IReadOnlyList<FraudEvent> events)
    {
        var title = events.Count == 1
            ? FraudText.Title(events[0].Kind)
            : Tr.T($"Подозрительные операции: {events.Count}", $"Шектүү операциялар: {events.Count}", $"Suspicious operations: {events.Count}", $"Şüpheli işlemler: {events.Count}", $"Shubhali amallar: {events.Count}");
        var lines = events.Take(10).Select(e => (events.Count == 1 ? "" : "• ") + FraudText.Describe(e));
        var tail = events.Count > 10 ? "\n…" : "";
        return TelegramBotService.BuildAlert(title, string.Join("\n", lines) + tail);
    }

    /// <summary>Отправить накопленные (за тихие часы или при обрыве связи) события за последние сутки одним сообщением. Вызывает дежурный программы владельца.</summary>
    public static async Task FlushUnsentAsync(DateTime? now = null)
    {
        if (!await FlushLock.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            var at = now ?? DateTime.Now;
            var r = AntiFraudSettingsStore.Current.Fraud;
            if (!r.Enabled || !r.Telegram || !TelegramBotService.IsConfigured || !TariffGate.CanUseTelegramBot || InQuietHours(r, at))
                return;
            var since = DateTimeOffset.Now.AddHours(-24);
            var list = FraudEventStore.ReadAll().Where(e => e.At >= since && FraudKinds.Weight(e.Kind) > 0 && !WasNotified(e.Id)).OrderBy(e => e.At).ToList();
            if (list.Count == 0)
                return;
            using var crossLock = await CrossProcessLock.TryAcquireAsync(SendLockPath, SendLockWait).ConfigureAwait(false); // 2026-11-06: В3 Л5-22
            if (crossLock is null)
                return;
            list = list.Where(e => !WasNotified(e.Id)).ToList();
            if (list.Count == 0)
                return;
            var error = await TelegramBotService.SendAsync(BuildMessage(list)).ConfigureAwait(false);
            if (error is null)
                MarkNotified(list.Select(e => e.Id));
            else
                PosLogger.Log($"Антифрод: накопленные события не ушли в Telegram ({error}).", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: накопленные события не отправлены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            FlushLock.Release();
        }
    }

    /// <summary>Раз в 10 минут досылать накопленное (запускается из дежурного программы владельца; повторный вызов ничего не делает).</summary>
    public static void StartDigest()
    {
        lock (NotifyGate)
            _digestTimer ??= new Timer(_ => _ = FlushUnsentAsync(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
    }

    // ───────── «уже сообщали» (общий файл кассы и владельца) ─────────

    private static bool WasNotified(string id)
    {
        lock (NotifyGate)
            return ReadNotified().Contains(id);
    }

    private static void MarkNotified(IEnumerable<string> ids)
    {
        lock (NotifyGate)
        {
            // 2026-11-05 (Н7): порядок HashSet не гарантирован — хранится список в порядке добавления, обрезается с начала (самые старые).
            var set = ReadNotified();
            var keep = ReadNotifiedList();
            foreach (var id in ids)
                if (set.Add(id))
                    keep.Add(id);
            // Хранится не больше 2000 последних: старые события всё равно старше суток и во «Flush» не попадают.
            if (keep.Count > 2000)
                keep.RemoveRange(0, keep.Count - 2000);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(NotifiedFile)!);
                AtomicFile.WriteAllText(NotifiedFile, JsonSerializer.Serialize(keep));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Антифрод: отметки «уже сообщили» не записаны ({ex.GetType().Name}).", "WARNING");
            }
        }
    }

    private static HashSet<string> ReadNotified() => new(ReadNotifiedList(), StringComparer.Ordinal);

    private static List<string> ReadNotifiedList()
    {
        try
        {
            if (File.Exists(NotifiedFile) && JsonSerializer.Deserialize<List<string>>(AtomicFile.ReadAllText(NotifiedFile) ?? "[]") is { } list)
                return list;
        }
        catch (Exception)
        {
            // испорченный файл — начинаем с пустого: худшее, что будет, — одно повторное сообщение
        }
        return new List<string>();
    }
}
