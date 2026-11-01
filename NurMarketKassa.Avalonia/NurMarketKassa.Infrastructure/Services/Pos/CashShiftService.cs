using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>2026-11-01: внесения/изъятия смены, которые не ушли на сервер к моменту закрытия: сколько, на какую сумму, строки «время вид сумма» через перевод строки.</summary>
public sealed record UnsentCashOps(int Count, decimal Sum, string Details);

public sealed class CashShiftService : ICashShiftService
{
    private readonly IShiftApiService _shiftApi;
    private readonly IShiftStateService _shiftStateService;
    private readonly MySqlAuditService _auditDb;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IOfflinePosStateStore _offlinePosStateStore;
    private readonly ISalesApiService _salesApi;
    private readonly SyncService? _syncService;

    public CashShiftService(
        IShiftApiService shiftApi,
        IShiftStateService shiftStateService,
        MySqlAuditService auditDb,
        IReceiptPrinterService receiptPrinter,
        IOfflinePosStateStore offlinePosStateStore,
        ISalesApiService salesApi,
        SyncService? syncService = null) // 2026-10-27 (ревью, См-1): досылка очереди чеков прошлой смены перед её закрытием; null (стенд) — без досылки
    {
        _shiftApi = shiftApi;
        _shiftStateService = shiftStateService;
        _auditDb = auditDb;
        _receiptPrinter = receiptPrinter;
        _offlinePosStateStore = offlinePosStateStore;
        _salesApi = salesApi;
        _syncService = syncService;
    }

    /// <summary>2026-10-27 (ревью, См-1): сколько чеков смены ещё в офлайн-очереди (та же выборка, что у фоновой очереди закрытий
    /// SyncService и у MainWindow.FlushBeforeShiftCloseAsync). Подменяется только стендом (чтобы не трогать настоящую базу).</summary>
    internal static Func<string, int> QueuedSalesOfShift = shiftId =>
        OfflinePendingSalesStore.LoadPendingForSync().Count(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));

    /// <summary>2026-11-01 (К-03, приёмка): досылка внесений/изъятий смены перед её закрытием — перепривязывает хвосты офлайн-смены к серверной смене и отправляет их
    /// (ShiftCashFlowSync.FlushForCloseAsync, задаёт главное окно). Принимает id серверной смены, возвращает, что не ушло (число, сумма, строки «время вид сумма»). null (стенд) — без досылки.</summary>
    public static Func<string, CancellationToken, Task<UnsentCashOps>>? CashOperationsFlusher { get; set; }


    /// <summary>Стенд: вместо SyncService досылает чеки сам.</summary>
    internal static Func<CancellationToken, Task>? FlushQueuedSalesOverride;

    /// <summary>2026-10-27 (ревью, См-2): сколько раз «Открыть смену» отказывает, если сервер не закрыл прошлую смену из очереди, —
    /// дальше касса продолжает прошлую смену (кассира нельзя запирать насовсем), но с предупреждением о потерянном пересчёте.</summary>
    internal const int OpenRetriesBeforeResume = 3;

    /// <summary>2026-10-27 (ревью, См-1): ждём, пока уйдут чеки смены, до ~10 с (проба связи + проход очереди). Возвращает, сколько осталось.</summary>
    private async Task<int> FlushQueuedSalesAsync(string shiftId, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var attempt = 0; attempt < 4 && QueuedSalesOfShift(shiftId) > 0 && !cts.IsCancellationRequested; attempt++)
            {
                if (FlushQueuedSalesOverride is { } custom)
                    await custom(cts.Token).ConfigureAwait(false);
                else if (_syncService is not null)
                {
                    await _syncService.ProbeNowAsync(cts.Token).ConfigureAwait(false);
                    await _syncService.TriggerSyncNowAsync(cts.Token).ConfigureAwait(false);
                }
                else
                    break;

                if (QueuedSalesOfShift(shiftId) > 0)
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Открытие смены: досылка чеков прошлой смены не завершена: {ex.Message}", "SHIFT");
        }

        return QueuedSalesOfShift(shiftId);
    }

    public async Task<CashShiftOperationResult> OpenShiftAsync(decimal openingCash, CancellationToken cancellationToken = default)
    {
        // Вторая смена поверх незакрытой затирает ActiveShiftId прежней и осиротляет её продажи.
        if (!string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return CashShiftOperationResult.Failed(Tr.T("Смена уже открыта. Закройте текущую смену перед открытием новой.",
                "Смена мурунтан эле ачык. Жаңысын ачуудан мурун учурдагы сменаны жабыңыз.",
                "A shift is already open. Close the current shift before opening a new one.",
                "Vardiya zaten açık. Yeni bir vardiya açmadan önce mevcut vardiyayı kapatın.",
                "Smena allaqachon ochiq. Yangisini ochishdan oldin joriy smenani yoping."));

        // 2026-09-29: и в аварии сервера (ServerOutageMonitor) — офлайн-смена, как без интернета:
        // кассир продолжает работать, чеки смены досылаются, когда сервер оживёт.
        if (OfflineModeHelper.UseLocalOperations || OfflineModeHelper.IsServerOutage)
            return OpenOfflineShift(openingCash);

        using var critical = ApiThrottle.BeginCritical(); // 2026-10-30: открытие смены — критичный запрос, при 429 выходит первым после паузы
        var opening = openingCash.ToString("0.00", CultureInfo.InvariantCulture);
        // 2026-10-04, стенд «сбои сервера»: сервер, который молчит (обрыв без ответа, «чёрная дыра»,
        // ответ через 20–60 с), кассир ждёт не дольше OpenShiftServerAnswerBudget с последнего ответа
        // сервера, дальше — офлайн-смена (catch ниже), как при обрыве сети. Раньше — до 55 с на запрос
        // (таймаут HttpClient): на стенде открытие смены при «чёрной дыре» заняло 55 с.
        // 2026-10-30 (приёмка 1.17.67, «Важно» 1): уже идущая пауза 429 (лента, пульс) входит в срок — иначе он истекал внутри чужой паузы.
        using var serverWait = new CancellationTokenSource(OpenShiftServerAnswerBudget + ApiThrottle.RemainingBlock);
        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverWait.Token);
        // 2026-10-30 (приёмка 1.17.66, «Важно» 1): 429 — пауза Retry-After входит в срок (пауза + запас), а не 2 с: иначе срок выходил внутри паузы и смена открывалась офлайн при живом сервере.
        var shiftStartedUtc = DateTime.UtcNow;
        var watch = ServerOutageMonitor.BeginResponseWatch(() => serverWait.CancelAfter(OpenShiftServerAnswerBudget + ApiThrottle.RemainingBlock),
            wait => serverWait.CancelAfter(ApiThrottle.CriticalBudget(wait)));
        try
        {
            // 2026-09-10: раньше вызывался ДО этого try/catch — ApiException отсюда (например,
            // "Сессия недействительна" при протухшем токене) улетал необработанным и ронял кассу
            // с крашем прямо при добавлении первого товара в чек (оно само открывает смену).
            // Теперь ошибки этого вызова обрабатываются теми же catch, что и ниже.
            var cashboxId = await EnsurePosCashboxIdAsync(serverCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cashboxId))
                return CashShiftOperationResult.Failed(Tr.T("Не удалось определить кассу.", "Кассаны аныктоо мүмкүн болгон жок.",
                    "Could not determine the till.", "Kasa belirlenemedi.", "Kassani aniqlab bo'lmadi."));

            var response = await _shiftApi
                .ConstructionShiftOpenAsync(cashboxId, opening, serverCts.Token)
                .ConfigureAwait(false);
            watch.Stop();

            var shiftId = CartDisplayHelper.TryShiftIdFromOpenResponse(response);

            // 2026-10-27 (аудит кассы, В-7): смена закрыта на кассе «в очередь» (сервер не принял закрытие), а «открыть смену» у
            // сервера идемпотентна — он отдаёт ту же старую смену, и SyncService через ≤ 45 с закрывал её, то есть уже текущую
            // («Смена не открыта» посреди работы). Сначала пробуем закрыть старую на сервере и открыть новую; не вышло — касса
            // честно продолжает эту смену (запись очереди снимается, иначе фон её закроет).
            var queuedRevival = false;
            string? carryOverHint = null;
            if (!string.IsNullOrWhiteSpace(shiftId) && PendingShiftCloseStore.TryGet(shiftId) is { } queuedClose)
            {
                // 2026-10-27 (ревью, См-1): чеки старой смены ещё в офлайн-очереди — закрывать её на сервере рано: чеки уйдут в НОВУЮ смену (или
                // будут отклонены), Z вчерашней смены занижен, сегодняшней завышен. Фоновая очередь так же ждёт (SyncService, shiftsWithQueuedSales).
                // Сначала досылаем; не ушли — смену не закрываем и новую не открываем, запись очереди остаётся (фон закроет её сам, когда чеки уйдут).
                var queuedSales = QueuedSalesOfShift(shiftId);
                if (queuedSales > 0)
                {
                    PosLogger.Log($"Открытие смены: у смены {shiftId} из очереди закрытий в офлайн-очереди {queuedSales} чек(ов) — досылаю до её закрытия.", "SHIFT");
                    queuedSales = await FlushQueuedSalesAsync(shiftId, cancellationToken).ConfigureAwait(false);
                }

                if (queuedSales > 0)
                {
                    PosLogger.Log($"Открытие смены: чеки прошлой смены {shiftId} не ушли ({queuedSales}) — смену не закрываю, новую не открываю.", "SHIFT");
                    return CashShiftOperationResult.Failed(Tr.T(
                        $"Прошлая смена ещё не закрыта на сервере: не отправлено чеков — {queuedSales}. Дождитесь отправки (минута-две, нужна связь) и откройте смену ещё раз — иначе её чеки попадут в новую смену.",
                        $"Мурунку смена серверде дагы жабыла элек: жөнөтүлбөгөн чектер — {queuedSales}. Жөнөтүлгүчө күтүңүз (бир-эки мүнөт, байланыш керек) жана сменаны кайра ачыңыз — болбосо анын чектери жаңы сменага түшөт.",
                        $"The previous shift is not closed on the server yet: {queuedSales} receipt(s) not sent. Wait until they are sent (a minute or two, a connection is needed) and open the shift again — otherwise its receipts would land in the new shift.",
                        $"Önceki vardiya sunucuda henüz kapatılmadı: gönderilmemiş fiş sayısı — {queuedSales}. Gönderilmesini bekleyin (bir iki dakika, bağlantı gerekir) ve vardiyayı tekrar açın — aksi halde fişleri yeni vardiyaya düşer.",
                        $"Oldingi smena serverda hali yopilmagan: yuborilmagan cheklar — {queuedSales}. Yuborilishini kuting (bir-ikki daqiqa, aloqa kerak) va smenani qaytadan oching — aks holda uning cheklari yangi smenaga tushadi."));
                }

                PosLogger.Log($"Открытие смены: сервер вернул смену {shiftId}, закрытую на кассе и стоящую в очереди закрытий — закрываю её на сервере сначала.", "SHIFT");
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(TimeSpan.FromSeconds(15));
                (ShiftCloseOutcome Outcome, string? Error) closed;
                try
                {
                    closed = await ShiftCloseRetry.CloseAsync(_shiftApi, shiftId, queuedClose.ClosingCash, closeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    closed = (ShiftCloseOutcome.Failed, "таймаут");
                }

                if (closed.Outcome != ShiftCloseOutcome.Failed)
                {
                    PendingShiftCloseStore.Remove(shiftId);
                    try
                    {
                        response = await _shiftApi.ConstructionShiftOpenAsync(cashboxId, opening, closeCts.Token).ConfigureAwait(false);
                        shiftId = CartDisplayHelper.TryShiftIdFromOpenResponse(response);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return CashShiftOperationResult.Failed(Tr.T("Прошлая смена закрыта, но новая не открылась: сервер не ответил. Откройте смену ещё раз.",
                            "Мурунку смена жабылды, бирок жаңысы ачылган жок: сервер жооп берген жок. Сменаны кайра ачыңыз.",
                            "The previous shift is closed, but the new one did not open: the server did not answer. Open the shift again.",
                            "Önceki vardiya kapatıldı ama yenisi açılmadı: sunucu yanıt vermedi. Vardiyayı tekrar açın.",
                            "Oldingi smena yopildi, lekin yangisi ochilmadi: server javob bermadi. Smenani qaytadan oching."));
                    }
                }

                if (string.Equals(shiftId, queuedClose.ShiftId, StringComparison.OrdinalIgnoreCase))
                {
                    // 2026-10-27 (ревью, См-2): продолжить старую смену молча нельзя — вчерашний пересчёт кассира (closing cash) теряется, и
                    // сегодняшний Z покажет ложную недостачу (ожидание вчерашнее, в ящике только новое начало). Первые разы отказываем:
                    // запись очереди остаётся, кассир повторяет открытие. Не помогло (сервер упорно не принимает) — продолжаем, но кассир
                    // получает сумму, которую надо записать изъятием/внесением, чтобы ящик сошёлся.
                    var retries = PendingShiftCloseStore.RecordOpenRetry(shiftId!, closed.Error);
                    if (retries < OpenRetriesBeforeResume)
                    {
                        PosLogger.Log($"Открытие смены: сервер не закрыл прошлую смену {shiftId} ({closed.Error}) — новую не открываю (попытка {retries} из {OpenRetriesBeforeResume - 1}), запись очереди остаётся.", "SHIFT");
                        return CashShiftOperationResult.Failed(Tr.T(
                            "Прошлая смена ещё не закрыта на сервере, поэтому новая не открыта: иначе пересчёт ящика при её закрытии потеряется. Подождите минуту и откройте смену ещё раз.",
                            "Мурунку смена серверде дагы жабыла элек, ошондуктан жаңысы ачылган жок: болбосо аны жабууда кассаны кайра санаган сумма жоголот. Бир мүнөт күтүп, сменаны кайра ачыңыз.",
                            "The previous shift is not closed on the server yet, so the new one was not opened: otherwise the drawer count made at its closing would be lost. Wait a minute and open the shift again.",
                            "Önceki vardiya sunucuda henüz kapatılmadı, bu yüzden yenisi açılmadı: aksi halde kapanışta yapılan çekmece sayımı kaybolur. Bir dakika bekleyin ve vardiyayı tekrar açın.",
                            "Oldingi smena serverda hali yopilmagan, shuning uchun yangisi ochilmadi: aks holda uni yopishda qilingan kassa hisobi yo'qoladi. Bir daqiqa kuting va smenani qaytadan oching."));
                    }

                    PendingShiftCloseStore.Remove(shiftId!);
                    queuedRevival = true;
                    PosLogger.Log($"Открытие смены: сервер не закрыл прошлую смену {shiftId} ({closed.Error}) — касса продолжает её, запись очереди снята.", "SHIFT");
                    if (decimal.TryParse(queuedClose.ClosingCash, NumberStyles.Number, CultureInfo.InvariantCulture, out var countedBefore)
                        && Math.Abs(countedBefore - openingCash) >= 0.01m)
                    {
                        var diff = Math.Abs(countedBefore - openingCash).ToString("0.00", CultureInfo.InvariantCulture);
                        var counted = countedBefore.ToString("0.00", CultureInfo.InvariantCulture);
                        var entered = openingCash.ToString("0.00", CultureInfo.InvariantCulture);
                        carryOverHint = countedBefore > openingCash
                            ? Tr.T($"Внимание: при закрытии этой смены в ящике насчитали {counted}, а вы положили {entered}. Запишите изъятие {diff} (меню ☰), иначе в Z будет ложная недостача.",
                                $"Көңүл буруңуз: бул сменаны жабууда кассада {counted} саналган, сиз {entered} койдуңуз. {diff} алууну жазыңыз (☰ меню), болбосо Z-де жалган жетишпестик болот.",
                                $"Attention: when this shift was closed the drawer was counted at {counted}, and you put in {entered}. Record a withdrawal of {diff} (☰ menu), otherwise Z will show a false shortage.",
                                $"Dikkat: bu vardiya kapatılırken çekmece {counted} sayıldı, siz {entered} koydunuz. {diff} tutarında para çıkışı kaydedin (☰ menü), aksi halde Z'de sahte eksik görünür.",
                                $"Diqqat: bu smena yopilganda qutida {counted} sanalgan, siz {entered} qo'ydingiz. {diff} olishni yozing (☰ menyu), aks holda Z da soxta kamomad bo'ladi.")
                            : Tr.T($"Внимание: при закрытии этой смены в ящике насчитали {counted}, а вы положили {entered}. Запишите внесение {diff} (меню ☰), иначе в Z будет ложный излишек.",
                                $"Көңүл буруңуз: бул сменаны жабууда кассада {counted} саналган, сиз {entered} койдуңуз. {diff} киргизүүнү жазыңыз (☰ меню), болбосо Z-де жалган ашыкча болот.",
                                $"Attention: when this shift was closed the drawer was counted at {counted}, and you put in {entered}. Record a deposit of {diff} (☰ menu), otherwise Z will show a false surplus.",
                                $"Dikkat: bu vardiya kapatılırken çekmece {counted} sayıldı, siz {entered} koydunuz. {diff} tutarında para girişi kaydedin (☰ menü), aksi halde Z'de sahte fazla görünür.",
                                $"Diqqat: bu smena yopilganda qutida {counted} sanalgan, siz {entered} qo'ydingiz. {diff} kiritishni yozing (☰ menyu), aks holda Z da soxta ortiqcha bo'ladi.");
                    }
                }
            }

            // 2026-09-26: «открыть смену» у сервера — это «дай мою открытую смену»: если у кассира
            // смена уже открыта на ДРУГОЙ кассе, сервер новую не открывает, а возвращает ту
            // (проверено на тестовой компании). Принять её как смену этой кассы нельзя — продажа
            // уйдёт с этой кассой и сервер ответит «Смена не открыта». Переходим на ту кассу, как
            // ShiftStateService при входе, а если касса закреплена в настройках — объясняем.
            var openedCashboxId = response.ValueKind == JsonValueKind.Object ? ShiftHelper.ReadCashboxId(response) : null;
            if (!string.IsNullOrWhiteSpace(openedCashboxId)
                && !string.Equals(openedCashboxId, cashboxId, StringComparison.OrdinalIgnoreCase))
            {
                var openedCashboxName = response.TryGetProperty("cashbox_name", out var cn) && cn.ValueKind == JsonValueKind.String
                    ? cn.GetString()
                    : null;
                var pinned = UserPreferences.Instance.PreferredCashboxId;
                if (!string.IsNullOrWhiteSpace(pinned)
                    && !string.Equals(pinned, openedCashboxId, StringComparison.OrdinalIgnoreCase))
                {
                    PosLogger.Log($"Открытие смены: у кассира уже открыта смена на другой кассе ({openedCashboxName ?? openedCashboxId}), эта касса закреплена в настройках.", "SHIFT");
                    var otherName = openedCashboxName ?? openedCashboxId;
                    return CashShiftOperationResult.Failed(Tr.T(
                        $"У вас уже открыта смена на кассе «{otherName}». Вторую смену сервер не открывает: " +
                        $"закройте ту смену (на той кассе или на сайте) либо выберите в настройках кассу «{otherName}».",
                        $"Сизде «{otherName}» кассасында смена мурунтан эле ачык. Сервер экинчи сменаны ачпайт: " +
                        $"ал сменаны (ошол кассада же сайтта) жабыңыз же жөндөөлөрдөн «{otherName}» кассасын тандаңыз.",
                        $"You already have a shift open on till “{otherName}”. The server won't open a second one: " +
                        $"close that shift (on that till or on the website) or select till “{otherName}” in settings.",
                        $"«{otherName}» kasasında zaten açık bir vardiyanız var. Sunucu ikinci bir vardiya açmaz: " +
                        $"o vardiyayı kapatın (o kasada veya web sitesinde) ya da ayarlardan «{otherName}» kasasını seçin.",
                        $"Sizda «{otherName}» kassasida smena allaqachon ochiq. Server ikkinchi smenani ochmaydi: " +
                        $"o'sha smenani yoping (o'sha kassada yoki saytda) yoki sozlamalarda «{otherName}» kassasini tanlang."));
                }

                PosLogger.Log($"Открытие смены: сервер вернул уже открытую смену кассира на кассе {openedCashboxName ?? openedCashboxId} — касса переключена на неё.", "SHIFT");
                PosApp.PosCashboxId = openedCashboxId;
                if (!string.IsNullOrWhiteSpace(openedCashboxName))
                    PosApp.PosCashboxDisplayName = openedCashboxName;
            }

            if (!string.IsNullOrWhiteSpace(shiftId))
                PosApp.ActiveShiftId = shiftId;
            else
                await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);

            // Какую смену вернул сервер: новую или уже открытую (живой случай 2026-09-26 без этой
            // строки пришлось восстанавливать по косвенным признакам).
            PosLogger.Log(
                $"Смена открыта: id={PosApp.ActiveShiftId}, касса={PosApp.PosCashboxDisplayName ?? PosApp.PosCashboxId}, " +
                $"кассир={PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId}, открыта сервером в {(response.TryGetProperty("opened_at", out var oa) ? oa.ToString() : "?")}, " +
                $"введена начальная сумма {openingCash:0.00}, связь есть",
                "SHIFT");

            ShiftService.IsShiftOpen = true;
            // 2026-10-27 (аудит кассы, С-11): сервер мог вернуть уже открытую смену — тогда её начальная сумма и время открытия
            // не те, что ввёл кассир. Остаток в шапке и текст берём у сервера, кассир видит «смена продолжена».
            var (resumed, serverOpening, serverOpenedAt) = ShiftOpenResponse.Classify(response, openingCash, DateTimeOffset.Now);
            resumed |= queuedRevival;
            if (resumed)
            {
                var shownOpening = serverOpening ?? openingCash;
                var since = serverOpenedAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "—";
                var sum = shownOpening.ToString("0.00", CultureInfo.InvariantCulture);
                PosLogger.Log($"Открытие смены: продолжена открытая смена {PosApp.ActiveShiftId} (с {since}, начальная сумма у сервера {sum}, введено {openingCash:0.00}).", "SHIFT");
                _offlinePosStateStore.SaveFromApp(shownOpening);
                _auditDb.LogShift("open_resumed", shownOpening, PosApp.ActiveShiftId);
                return CashShiftOperationResult.Success(shownOpening,
                    infoMessage: Tr.T($"Смена продолжена с {since}: она уже была открыта, начальная сумма {sum}. Введённая сумма не применена.",
                        $"Смена {since} баштап улантылды: ал мурунтан эле ачык болчу, баштапкы сумма {sum}. Киргизилген сумма колдонулган жок.",
                        $"Shift continued since {since}: it was already open, opening amount {sum}. The amount you entered was not applied.",
                        $"Vardiya {since} itibarıyla devam ediyor: zaten açıktı, başlangıç tutarı {sum}. Girdiğiniz tutar uygulanmadı.",
                        $"Smena {since} dan davom etdi: u allaqachon ochiq edi, boshlang'ich summa {sum}. Kiritilgan summa qo'llanmadi.")
                        + (carryOverHint is null ? "" : "\n\n" + carryOverHint))
                    with { Resumed = true, ResumedOpenedAt = serverOpenedAt, InfoIsWarning = carryOverHint is not null, ResumedBy = ReadOpenedBy(response), ResumedHint = carryOverHint };
            }

            _offlinePosStateStore.SaveFromApp(openingCash);
            _auditDb.LogShift("open", openingCash, PosApp.ActiveShiftId);
            return CashShiftOperationResult.Success(openingCash);
        }
        catch (Exception ex) when ((ApiThrottle.IsThrottled(ex) || (ex is OperationCanceledException && ApiThrottle.ThrottledSince(shiftStartedUtc))) && !cancellationToken.IsCancellationRequested)
        {
            // 2026-10-30 (приёмка 1.17.66, «Важно» 1): срок мог выйти уже после паузы 429 (отмена) — это тоже «занят», не «нет связи».
            // 2026-10-30 (приёмка 1.17.65, «Важно» 2): 429 — сервер занят, а не «нет связи». Офлайн-смену не открываем (она подменила бы настоящую смену
            // сервера и отправила бы чеки без неё): кассир видит «Сервер занят», повторяет через пару секунд.
            PosLogger.Log($"Открытие смены: сервер занят (429) — смена не открыта, повтор через {ApiThrottle.RemainingBlock.TotalSeconds:0} с.", "SHIFT");
            return CashShiftOperationResult.Failed(ApiThrottle.BusyText());
        }
        catch (HttpRequestException ex)
        {
            // 2026-10-04: обрыв связи — авария сразу (следующие действия кассира не ждут сеть), как в
            // ветке сбоя сервера ниже.
            ServerOutageMonitor.ReportFailure(Tr.T("открытие смены", "сменаны ачуу", "opening the shift", "vardiya açma", "smenani ochish"), ex, hard: true);
            PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            OfflinePosStateStore.RememberOfflineOpening(openingCash); // 2026-11-01 (К-02, code-review): сумма открытия — отдельно от «текущего остатка»
            PosLogger.Log($"Смена открыта без связи (обрыв): id={PosApp.ActiveShiftId}, введена начальная сумма {openingCash:0.00}, будет передана серверу.", "SHIFT");
            return CashShiftOperationResult.Success(
                openingCash,
                isOffline: true,
                infoMessage: Tr.T($"Смена открыта без связи ({ex.Message}). Она будет передана на сервер, как только появится связь; повторно открывать не нужно.",
                    $"Смена байланышсыз ачылды ({ex.Message}). Байланыш пайда болгондо серверге өткөрүлөт; кайра ачуунун кереги жок.",
                    $"Shift opened without a connection ({ex.Message}). It will be handed to the server as soon as there is a connection; no need to open it again.",
                    $"Vardiya bağlantısız açıldı ({ex.Message}). Bağlantı gelir gelmez sunucuya aktarılacak; tekrar açmanıza gerek yok.",
                    $"Smena aloqasiz ochildi ({ex.Message}). Aloqa paydo bo'lishi bilan serverga o'tkaziladi; qayta ochish shart emas."));
        }
        catch (Exception ex) when (ServerOutageMonitor.IsServerFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            // 2026-09-29: сбой сервера (5xx, 429, таймаут) — не «Ошибка открытия смены», а
            // офлайн-смена, как при обрыве сети. Открыл ли сервер смену на самом деле — не важно:
            // чеки офлайн-смены уходят без shift_id и попадут в открытую смену кассира.
            PosLogger.Log($"Открытие смены: сервер не отвечает ({ServerOutageMonitor.Describe(ex)}) — смена открыта офлайн.", "SHIFT");
            ServerOutageMonitor.ReportFailure(Tr.T("открытие смены", "сменаны ачуу", "opening the shift", "vardiya açma", "smenani ochish"), ex, hard: true);
            return OpenOfflineShift(openingCash);
        }
        catch (Exception ex) when (IsCashboxRejectedError(ex.Message))
        {
            // 2026-09-14, тот же баг, что уже исправлен в PosCheckoutService (см. её
            // комментарий) — ConstructionCashboxesListAsync не фильтруется по филиалу, поэтому
            // EnsurePosCashboxIdAsync выше мог посчитать отвергнутую сервером кассу валидной.
            // Реальный отказ сервера при открытии смены — надёжный сигнал переподобрать кассу.
            var reassigned = await TryReassignCashboxAsync(cancellationToken).ConfigureAwait(false);
            return CashShiftOperationResult.Failed(reassigned
                ? Tr.T("Касса была переназначена (старая не подходит для вашего филиала). Откройте смену ещё раз.",
                    "Касса алмаштырылды (мурункусу филиалыңызга туура келбейт). Сменаны кайра ачыңыз.",
                    "The till was reassigned (the old one doesn't match your branch). Open the shift again.",
                    "Kasa yeniden atandı (eskisi şubenize uymuyor). Vardiyayı tekrar açın.",
                    "Kassa qayta tayinlandi (eskisi filialingizga mos emas). Smenani qaytadan oching.")
                : Tr.T("Ни одна касса компании не подходит для вашего филиала. Обратитесь к администратору NurCRM — " +
                  "проверьте привязку кассы к филиалу в веб-версии.",
                    "Компаниянын бир да кассасы филиалыңызга туура келбейт. NurCRM администраторуна кайрылыңыз — " +
                    "веб-версияда кассанын филиалга байланышын текшериңиз.",
                    "None of the company's tills match your branch. Contact your NurCRM administrator — " +
                    "check the till's branch assignment in the web version.",
                    "Şirketin hiçbir kasası şubenize uymuyor. NurCRM yöneticinize başvurun — " +
                    "web sürümünde kasanın şubeye bağlantısını kontrol edin.",
                    "Kompaniyaning birorta kassasi filialingizga mos emas. NurCRM administratoriga murojaat qiling — " +
                    "veb-versiyada kassaning filialga bog'lanishini tekshiring."));
        }
        catch (Exception ex)
        {
            return CashShiftOperationResult.Failed(Tr.T("Ошибка открытия смены: ", "Сменаны ачууда ката кетти: ",
                "Error opening the shift: ", "Vardiya açılırken hata oluştu: ", "Smenani ochishda xato: ") + ex.Message);
        }
        finally
        {
            watch.Stop();
        }
    }

    /// <summary>2026-10-04: сколько открытие смены ждёт ответа сервера (с последнего ответа), прежде чем
    /// открыть смену офлайн (владелец: «переход на офлайн должен быть мгновенным»). Живой сервер отвечает
    /// на список касс и открытие смены за доли секунды; каждый ответ отсчитывает окно заново.</summary>
    internal static readonly TimeSpan OpenShiftServerAnswerBudget = TimeSpan.FromSeconds(2);

    /// <summary>2026-10-04: то же для закрытия смены. Чуть больше, чем у открытия: закрытие сервер
    /// считает дольше (итоги смены), а закрытие «в кассе» вместо сервера — это Z-отчёт без итогов
    /// сервера и закрытие на сервере только из очереди (SyncService, после досылки чеков смены).</summary>
    internal static readonly TimeSpan CloseShiftServerAnswerBudget = TimeSpan.FromSeconds(3);

    /// <summary>Смена только в кассе (сервер недоступен). 2026-09-29: вынесено из OpenShiftAsync
    /// без изменений, чтобы ею же открывалась смена в аварии сервера.</summary>
    private CashShiftOperationResult OpenOfflineShift(decimal openingCash)
    {
        PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
        ShiftService.IsShiftOpen = true;
        _offlinePosStateStore.SaveFromApp(openingCash);
        OfflinePosStateStore.RememberOfflineOpening(openingCash); // 2026-11-01 (К-02, code-review): сумма открытия — отдельно от «текущего остатка»
        PosLogger.Log($"Смена открыта без связи: id={PosApp.ActiveShiftId}, касса={PosApp.PosCashboxDisplayName ?? PosApp.PosCashboxId}, введена начальная сумма {openingCash:0.00}, будет передана серверу.", "SHIFT");
        return CashShiftOperationResult.Success(openingCash, isOffline: true, infoMessage: Tr.T(
            "Смена открыта без связи. Она будет передана на сервер, как только появится связь; повторно открывать не нужно.",
            "Смена байланышсыз ачылды. Байланыш пайда болгондо серверге өткөрүлөт; кайра ачуунун кереги жок.",
            "Shift opened without a connection. It will be handed to the server as soon as there is a connection; no need to open it again.",
            "Vardiya bağlantısız açıldı. Bağlantı gelir gelmez sunucuya aktarılacak; tekrar açmanıza gerek yok.",
            "Smena aloqasiz ochildi. Aloqa paydo bo'lishi bilan serverga o'tkaziladi; qayta ochish shart emas."));
    }

    /// <summary>Узнаёт именно ошибку "cashbox_id: Касса не найдена или не принадлежит этому
    /// филиалу" — см. одноимённый метод в PosCheckoutService (та же проблема, разные вызовы).</summary>
    private static bool IsCashboxRejectedError(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.Contains("cashbox", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не принадлежит", StringComparison.OrdinalIgnoreCase));

    /// <summary>См. одноимённый метод в PosCheckoutService — заново запрашивает список касс и
    /// выбирает первую доступную, отличную от только что отвергнутой сервером. Возвращает
    /// false, когда кандидатов не осталось (см. её комментарий).</summary>
    private async Task<bool> TryReassignCashboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rejectedId = PosApp.PosCashboxId;
            if (!string.IsNullOrWhiteSpace(rejectedId))
                PosApp.RejectedCashboxIds.Add(rejectedId);

            var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);
            var candidates = CartDisplayHelper.ListCashboxes(rawList)
                .Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id))
                .ToList();

            var next = candidates.FirstOrDefault(c => c.IsActive);
            if (next.Id == null)
                next = candidates.FirstOrDefault();

            if (next.Id == null)
            {
                PosLogger.Log("Cashbox reassignment: no alternative (unrejected) cashbox found in list.", "SHIFT");
                return false;
            }

            PosApp.PosCashboxId = next.Id;
            PosApp.PosCashboxDisplayName = next.DisplayName;
            PosLogger.Log($"Cashbox reassigned after server rejection: {rejectedId} -> {next.Id}", "SHIFT");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cashbox reassignment failed: {ex}", "SHIFT");
            return false;
        }
    }

    /// <summary>2026-11-01: кассир подтвердил «Закрыть с пометкой»; запись о расхождении делается при УСПЕШНОМ закрытии (TakeUnsentMark).</summary>
    private UnsentCashOps? _unsentCashMark;

    private string? _unsentCashMarkShiftId;

    public async Task<CashShiftOperationResult> CloseShiftAsync(decimal? closingCash, CancellationToken cancellationToken = default, bool acceptUnsentCashOps = false)
    {
        _unsentCashMark = null;
        if (string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return CashShiftOperationResult.Failed(Tr.T("Смена уже закрыта.", "Смена мурунтан эле жабык.",
                "The shift is already closed.", "Vardiya zaten kapalı.", "Smena allaqachon yopilgan."));

        var shiftId = PosApp.ActiveShiftId;
        var closing = closingCash?.ToString("0.00", CultureInfo.InvariantCulture);
        using var critical = ApiThrottle.BeginCritical(); // 2026-10-30: закрытие смены — критичный запрос (при 429 первым после паузы)

        if (OfflineModeHelper.UseLocalOperations)
        {
            // 2026-11-01 (К-04, аудит «10 из 10»): вход был без связи, но смена НАСТОЯЩАЯ (восстановлена из offline_pos_state, id — GUID сервера).
            // Раньше она закрывалась только в кассе, без очереди: на сервере оставалась открытой, утром «Открыть смену» продолжала вчерашнюю
            // (два дня в одном Z, вчерашний пересчёт терялся). Теперь, как в аварии сервера: закрытие встаёт в очередь (PendingShiftCloseStore)
            // и уйдёт на сервер, когда появится связь и уйдут чеки смены. Офлайн-смена («offline-…») серверу неизвестна — только в кассе.
            if (!IsOfflineShiftId(shiftId) && Guid.TryParse(shiftId, out _))
                return CloseLocallyAndQueue(shiftId!, closing, closingCash, "вход без связи");

            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
            _auditDb.LogShift("close_offline", closingCash, shiftId);
            return CashShiftOperationResult.Success(closingCash, isOffline: true);
        }

        // 2026-09-29: сервер не отвечает (ServerOutageMonitor) — смена закрывается в кассе, а её
        // закрытие на сервере встаёт в очередь (SyncService дожмёт после досылки её чеков).
        // Офлайн-смены на сервере нет — её закрывать там нечего.
        if (OfflineModeHelper.IsServerOutage)
            return IsOfflineShiftId(shiftId)
                ? CloseOfflineShiftLocally(shiftId, closingCash)
                : CloseLocallyAndQueue(shiftId!, closing, closingCash, "сервер NurCRM не отвечает");

        // 2026-11-01 (К-03, аудит «10 из 10»): смена офлайн («offline-shift-…»), а сервер жив. Раньше сюда шёл обычный POST shifts/offline-shift-…/close/:
        // 404 → ветка «сервер не находит смену» закрывала ДРУГУЮ открытую смену кассира чужим пересчётом. Теперь сначала сверяем смену с сервером
        // (ShiftStateService берёт только открытую смену ЭТОГО кассира на ЭТОЙ кассе, при необходимости открывает её с той же начальной суммой — К-02):
        // нашлась серверная — закрываем именно её (после досылки чеков офлайн-смены); нет — смена закрывается только в кассе.
        // 2026-11-01 (code-review): после первого отказа (чеки/внесения ещё не ушли) RefreshAsync уже подменил ActiveShiftId на серверный GUID, и повторное «Закрыть»
        // пропускало эти проверки. Исходный офлайн-id хранится в состоянии (OriginOfflineShiftId): чеки очереди и хвосты внесений проверяются и после подмены.
        var originOffline = IsOfflineShiftId(shiftId) ? shiftId : OfflinePosStateStore.OriginOfflineShiftFor(shiftId);
        if (originOffline is not null)
        {
            var offlineId = originOffline;
            string? realId;
            if (IsOfflineShiftId(shiftId))
            {
                try
                {
                    await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Закрытие офлайн-смены {offlineId}: сверка смены с сервером не удалась: {ex.Message}", "SHIFT");
                }

                realId = PosApp.ActiveShiftId;
                if (string.IsNullOrWhiteSpace(realId) || IsOfflineShiftId(realId))
                {
                    PosApp.ActiveShiftId = offlineId;
                    return CloseOfflineShiftLocally(offlineId, closingCash);
                }
            }
            else
            {
                realId = shiftId!; // смена уже серверная (передана ранее), но чеки под офлайн-id могли ещё не уйти
            }

            var queuedLeft = QueuedSalesOfShift(offlineId);
            if (queuedLeft > 0)
            {
                PosLogger.Log($"Закрытие смены: офлайн-смена {offlineId} уже передана серверу как {realId}, но {queuedLeft} чек(ов) ещё в очереди — досылаю до закрытия.", "SHIFT");
                queuedLeft = await FlushQueuedSalesAsync(offlineId, cancellationToken).ConfigureAwait(false);
            }

            if (queuedLeft > 0)
                return CashShiftOperationResult.Failed(Tr.T(
                    $"Не все чеки смены отправлены на сервер: осталось {queuedLeft}. Дождитесь отправки (минута-две, нужна связь) и закройте смену ещё раз — иначе их выручка не попадёт в Z-отчёт этой смены.",
                    $"Сменанын бардык чектери серверге жөнөтүлгөн жок: {queuedLeft} калды. Жөнөтүлгүчө күтүңүз (бир-эки мүнөт, байланыш керек) жана сменаны кайра жабыңыз — болбосо алардын түшүмү бул сменанын Z-отчётуна кирбейт.",
                    $"Not all receipts of the shift have been sent to the server: {queuedLeft} left. Wait until they are sent (a minute or two, a connection is needed) and close the shift again — otherwise their revenue will not get into this shift's Z report.",
                    $"Vardiyanın tüm fişleri sunucuya gönderilmedi: {queuedLeft} kaldı. Gönderilmesini bekleyin (bir iki dakika, bağlantı gerekir) ve vardiyayı tekrar kapatın — aksi halde gelirleri bu vardiyanın Z raporuna girmez.",
                    $"Smenaning barcha cheklari serverga yuborilmadi: {queuedLeft} ta qoldi. Yuborilishini kuting (bir-ikki daqiqa, aloqa kerak) va smenani qaytadan yoping — aks holda ularning tushumi bu smenaning Z-hisobotiga kirmaydi."));

            // 2026-11-01 (приёмка, ВЫСОКАЯ-2): внесения и изъятия офлайн-смены тоже должны попасть в ЭТУ смену до её закрытия: иначе сервер закроет её без них
            // (недостача в закрытой), а PushPendingAsync позже привяжет их к следующей смене (излишек там). Перепривязываем к realId и отправляем.
            if (CashOperationsFlusher is { } flushCash)
            {
                UnsentCashOps cashLeft;
                try
                {
                    cashLeft = await flushCash(realId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 2026-11-01 (code-review): посчитать не вышло — «закрыть с пометкой» с выдуманными числами НЕ предлагаем: отказ «повторите», в журнал — тип исключения.
                    PosLogger.Log($"Закрытие смены: не удалось проверить неотправленные внесения/изъятия смены {realId} ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                    return CashShiftOperationResult.Failed(Tr.T(
                        "Не удалось проверить неотправленные внесения и изъятия. Повторите закрытие смены через несколько секунд.",
                        "Жөнөтүлбөгөн киргизүүлөрдү жана алууларды текшерүү мүмкүн болгон жок. Сменаны жабууну бир нече секунддан кийин кайталаңыз.",
                        "Could not check the unsent deposits and withdrawals. Try closing the shift again in a few seconds.",
                        "Gönderilmeyen para giriş ve çıkışları kontrol edilemedi. Vardiyayı birkaç saniye sonra tekrar kapatmayı deneyin.",
                        "Yuborilmagan kiritish va olishlarni tekshirib bo'lmadi. Bir necha soniyadan keyin smenani yopishni qaytaring."));
                }

                // 2026-11-01 (code-review, решение владельца «да» на окно): молча закрывать смену без внесений/изъятий нельзя (ложная недостача в Z). Окно кассы
                // спрашивает «Закрыть с пометкой?» (NeedsUnsentCashConfirm) и повторяет закрытие с acceptUnsentCashOps = true.
                if (cashLeft.Count > 0 && !acceptUnsentCashOps)
                    return UnsentCashRefusal(cashLeft);
                if (cashLeft.Count > 0)
                    MarkUnsentCash(realId, cashLeft);
            }

            PosLogger.Log($"Закрытие смены: офлайн-смена {offlineId} → закрываю серверную смену {realId}.", "SHIFT");
            shiftId = realId;
        }

        // 2026-10-04, стенд «сбои сервера»: молчащий сервер — не дольше CloseShiftServerAnswerBudget с
        // последнего ответа, дальше смена закрывается в кассе, закрытие на сервере — в очередь (catch
        // ниже). Раньше — до 55 с (таймаут HttpClient; на стенде при «чёрной дыре» — ровно 55 с).
        using var serverWait = new CancellationTokenSource(CloseShiftServerAnswerBudget + ApiThrottle.RemainingBlock); // 2026-10-30 (приёмка 1.17.67, «Важно» 1): + идущая пауза 429
        using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverWait.Token);
        // 2026-10-30 (приёмка 1.17.66, «Важно» 1): то же, что при открытии — на 429 срок = Retry-After + запас.
        var closeStartedUtc = DateTime.UtcNow;
        var watch = ServerOutageMonitor.BeginResponseWatch(() => serverWait.CancelAfter(CloseShiftServerAnswerBudget + ApiThrottle.RemainingBlock),
            wait => serverWait.CancelAfter(ApiThrottle.CriticalBudget(wait)));
        try
        {
            JsonElement response;
            try
            {
                response = await _shiftApi
                    .ConstructionShiftCloseAsync(shiftId!, closing, null, serverCts.Token)
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (LooksLikeDecimalPlacesError(ex.Message))
            {
                // Живой случай 2026-09-22: смена не закрывалась вообще — сервер отвечал
                // «income_total: Убедитесь, что вы ввели не более 2 цифр после запятой».
                // Касса это поле не отправляет: сервер сам посчитал и сохранил '150.00000'
                // (проверено запросом к api/construction/shifts/) и сам же отклонил его при
                // закрытии. Кассир при этом оказывался заперт — смену не закрыть никак.
                // Повторяем закрытие, передав те же суммы округлёнными до копеек.
                PosLogger.Log(
                    "Закрытие смены отклонено из-за лишних знаков в суммах сервера — повтор с округлением.",
                    "SHIFT");

                var rounded = await BuildRoundedTotalsAsync(_shiftApi, shiftId!, serverCts.Token).ConfigureAwait(false);
                if (rounded.Count == 0)
                    throw;

                response = await _shiftApi
                    .ConstructionShiftCloseAsync(shiftId!, closing, rounded, serverCts.Token)
                    .ConfigureAwait(false);
            }

            watch.Stop();
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            // 2026-10-27 (В-7): смена закрыта на сервере — запись очереди закрытий (если была) больше не нужна.
            PendingShiftCloseStore.Remove(shiftId!);
            RecordUnsentCashMark(closingCash);
            // A successful close is authoritative. An immediate list refresh may
            // still return the closed shift or restore it from the offline cache.
            _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
            _auditDb.LogShift("close", closingCash, shiftId);
            // 2026-09-15, живой баг: "система не считает наличную и безналичную" — отчёт по
            // смене раньше брал разбивку из _shiftTotals, снятого В ФОНЕ ЕЩЁ ДО закрытия (пока
            // был открыт CloseShiftDialog) — если кассир закрывал смену вскоре после последней
            // продажи, этот снимок мог не успеть учесть её. Ответ САМОГО закрытия — гарантированно
            // свежий, читаем разбивку из него.
            var totals = ReadClosingTotals(response, shiftId, cancellationToken);
            // 2026-11-01 (жалоба клиента): по журналу видно, что ввёл кассир и что получил сервер (только суммы и id смены).
            PosLogger.Log($"Смена закрыта: id={shiftId}, пересчитано кассиром {(closingCash is { } cc ? cc.ToString("0.00", CultureInfo.InvariantCulture) : "—")}, " +
                          $"у сервера: начальная {totals?.OpeningCash?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"}, выручка {totals?.TotalSales?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"}, " +
                          $"наличные {totals?.CashSales?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"}; связь есть.", "SHIFT");
            return CashShiftOperationResult.Success(closingCash, totals: totals);
        }
        catch (Exception ex) when ((ApiThrottle.IsThrottled(ex) || (ex is OperationCanceledException && ApiThrottle.ThrottledSince(closeStartedUtc)))
                                   && !cancellationToken.IsCancellationRequested)
        {
            // 2026-10-30 (приёмка 1.17.66, «Важно» 1): сервер занят (429) — смена НЕ закрывается офлайн и авария не объявляется: сервер жив, просил паузу.
            // Кассир повторяет закрытие через несколько секунд (смена остаётся открытой, ничего не потеряно).
            PosLogger.Log($"Закрытие смены: сервер занят (429) — смена не закрыта, повтор через {ApiThrottle.RemainingBlock.TotalSeconds:0} с.", "SHIFT");
            return CashShiftOperationResult.Failed(ApiThrottle.BusyText());
        }
        catch (ApiException ex) when (IsShiftNotOwned(ex))
        {
            // 2026-11-01 (К-03, аудит «10 из 10»): по ответу Умара 31.10 сервер на запрос с кассы в смену ДРУГОГО кассира отвечает 403 shift_not_owned (и владельцу тоже).
            // Раньше кассир видел сырой текст. Чужую смену не закрываем и не «чиним» (404-ветка закрыла бы другую смену этим пересчётом): сверяем свою смену
            // с сервером, чтобы следующее закрытие шло в смену этого кассира, и объясняем, что делать.
            PosLogger.Log($"Закрытие смены {shiftId}: сервер ответил shift_not_owned — смена принадлежит другому кассиру. {ex.Message}", "SHIFT");
            try
            {
                await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception refreshEx) when (refreshEx is not OperationCanceledException)
            {
                PosLogger.Log($"Закрытие смены: после shift_not_owned сверка смены не удалась: {refreshEx.Message}", "SHIFT");
            }

            return CashShiftOperationResult.Failed(Tr.T(
                "Эта смена на сервере принадлежит другому кассиру — закрыть её отсюда нельзя. Закройте её под тем кассиром, который её открыл (или на сайте), а здесь откройте свою смену. Касса сверила смену с сервером.",
                "Бул смена серверде башка кассирге таандык — бул жактан жаба албайсыз. Аны ачкан кассир менен (же сайттан) жабыңыз, ал эми бул жерде өз сменаңызды ачыңыз. Касса сменаны сервер менен салыштырды.",
                "This shift belongs to another cashier on the server — it cannot be closed from here. Close it as the cashier who opened it (or on the website) and open your own shift here. The till has re-checked the shift with the server.",
                "Bu vardiya sunucuda başka bir kasiyere ait — buradan kapatılamaz. Onu açan kasiyer olarak (veya siteden) kapatın, burada kendi vardiyanızı açın. Kasa vardiyayı sunucuyla yeniden kontrol etti.",
                "Bu smena serverda boshqa kassirga tegishli — bu yerdan yopib bo'lmaydi. Uni ochgan kassir sifatida (yoki saytda) yoping, bu yerda esa o'z smenangizni oching. Kassa smenani server bilan solishtirdi."));
        }
        catch (Exception ex) when (ex is not HttpRequestException
                                   && ServerOutageMonitor.IsServerFailure(ex)
                                   && !cancellationToken.IsCancellationRequested)
        {
            // 2026-09-29: сбой сервера (5xx, 429, таймаут, повреждённый ответ) — вместо «Ошибка
            // закрытия смены» смена закрывается в кассе, закрытие на сервере — в очереди.
            // Повтор безопасен: уже закрытую смену очередь снимает по ответу «уже закрыта».
            ServerOutageMonitor.ReportFailure(Tr.T("закрытие смены", "сменаны жабуу", "closing the shift", "vardiya kapatma", "smenani yopish"), ex, hard: true);
            return IsOfflineShiftId(shiftId)
                ? CloseOfflineShiftLocally(shiftId, closingCash)
                : CloseLocallyAndQueue(shiftId!, closing, closingCash, ServerOutageMonitor.Describe(ex));
        }
        catch (HttpRequestException ex)
        {
            // 2026-09-29: смена сервера — закрытие ещё и в очередь (раньше закрывалась только в
            // кассе, а на сервере оставалась открытой до автозакрытия в 01:00).
            if (!IsOfflineShiftId(shiftId))
            {
                ServerOutageMonitor.ReportFailure(Tr.T("закрытие смены", "сменаны жабуу", "closing the shift", "vardiya kapatma", "smenani yopish"), ex, hard: true);
                return CloseLocallyAndQueue(shiftId!, closing, closingCash, ServerOutageMonitor.Describe(ex));
            }

            // Симметрично OpenShiftAsync: сеть/DNS недоступны — закрываем смену локально,
            // а не оставляем кассира с зависшей открытой сменой без связи с сервером.
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
            _auditDb.LogShift("close_offline", closingCash, shiftId);
            return CashShiftOperationResult.Success(
                closingCash,
                isOffline: true,
                infoMessage: Tr.T($"Смена закрыта офлайн ({ex.Message}).", $"Смена офлайн режимде жабылды ({ex.Message}).",
                    $"Shift closed offline ({ex.Message}).", $"Vardiya çevrimdışı kapatıldı ({ex.Message}).",
                    $"Smena oflayn rejimda yopildi ({ex.Message})."));
        }
        catch (Exception ex) when (ex.Message.Contains("уже закрыта", StringComparison.OrdinalIgnoreCase))
        {
            // 2026-09-12, живой случай: смену закрыли где-то ещё (например, на сайте), пока
            // касса ещё считала её открытой — сервер отвечает {'status': ['Смена уже закрыта.']}
            // на повторное закрытие. Это не сбой — сервер и так уже согласен, что смена закрыта,
            // нужно только привести локальное состояние в соответствие (та же логика, что и в
            // MainWindow.OnShiftDesyncDetected для похожего рассинхрона после отклонённой
            // оплаты), а не пугать кассира сырым текстом ошибки.
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            PendingShiftCloseStore.Remove(shiftId!); // 2026-10-27 (В-7)
            RecordUnsentCashMark(closingCash);
            _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
            _auditDb.LogShift("close_already_closed", closingCash, shiftId);
            return CashShiftOperationResult.Success(
                closingCash,
                infoMessage: Tr.T("Смена уже была закрыта (например, на сайте) — состояние кассы синхронизировано.",
                    "Смена мурда эле жабылган (мисалы, сайтта) — кассанын абалы шайкештирилди.",
                    "The shift was already closed (for example, on the website) — the till's state has been synced.",
                    "Vardiya zaten kapatılmıştı (örneğin web sitesinde) — kasa durumu senkronize edildi.",
                    "Smena allaqachon yopilgan edi (masalan, saytda) — kassa holati sinxronlandi."));
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // 2026-09-15, живой баг: "Ошибка закрытия смены: Запрашиваемый ресурс или страница
            // не найдена (404)" на смене с реальным, накопленным за смену остатком — значит
            // PosApp.ActiveShiftId (тот ID, что касса запомнила при открытии смены) сервер уже
            // не узнаёт как текущую открытую смену для этой кассы (например, смена была открыта
            // при временной потере связи с локальным ID-заглушкой, который так и не был заменён
            // на настоящий, когда связь восстановилась). Вместо того чтобы сразу показать
            // кассиру сырую ошибку 404, один раз спрашиваем у сервера актуальный ID открытой
            // смены для этой кассы (ShiftHelper.PickOpenShiftId — тот же метод, что использует
            // ShiftStateService.RefreshAsync) и повторяем закрытие с ним.
            PosLogger.Log($"Shift close 404 for id={shiftId}, attempting recovery.", "SHIFT");
            var freshId = await TryRecoverActiveShiftIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(freshId) || string.Equals(freshId, shiftId, StringComparison.Ordinal))
                return CashShiftOperationResult.Failed(Tr.T(
                    "Не удалось закрыть смену: сервер не находит эту смену. Перезайдите в кассу и попробуйте снова.",
                    "Сменаны жабуу мүмкүн болгон жок: сервер бул сменаны таппай жатат. Кассага кайра кирип, дагы аракет кылыңыз.",
                    "Could not close the shift: the server can't find it. Sign in to the till again and retry.",
                    "Vardiya kapatılamadı: sunucu bu vardiyayı bulamıyor. Kasaya yeniden giriş yapıp tekrar deneyin.",
                    "Smenani yopib bo'lmadi: server bu smenani topa olmayapti. Kassaga qayta kiring va yana urinib ko'ring."));

            try
            {
                var retryResponse = await _shiftApi.ConstructionShiftCloseAsync(freshId, closing, null, cancellationToken).ConfigureAwait(false);
                PosApp.ActiveShiftId = null;
                ShiftService.IsShiftOpen = false;
                PendingShiftCloseStore.Remove(freshId); // 2026-10-27 (В-7)
                RecordUnsentCashMark(closingCash);
                _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
                _auditDb.LogShift("close", closingCash, freshId);
                return CashShiftOperationResult.Success(closingCash, totals: ReadClosingTotals(retryResponse, freshId, cancellationToken));
            }
            catch (Exception retryEx)
            {
                PosLogger.Log($"Shift close retry with recovered id={freshId} failed: {retryEx}", "SHIFT");
                if (LooksLikeDecimalPlacesError(retryEx.Message))
                    return CloseLocallyAndQueue(freshId, closing, closingCash, retryEx.Message);

                return CashShiftOperationResult.Failed(DescribeCloseFailure(retryEx));
            }
        }
        catch (Exception ex) when (SessionExpiryMonitor.IsSessionLost(ex) && !cancellationToken.IsCancellationRequested) // 2026-10-30: не любой 401
        {
            // 2026-10-30, живой баг («Ошибка закрытия смены: Сессия недействительна…» — тупик): сервер отозвал вход. Смена закрывается
            // в кассе, закрытие на сервере остаётся «ждёт» (PendingShiftCloseStore) и уйдёт само после повторного входа; окно «Войдите снова»
            // покажет SessionExpiredPrompt (один раз). Деньги, очередь продаж и корзины не трогаются.
            SessionExpiryMonitor.Report(Tr.T("закрытие смены", "сменаны жабуу", "closing the shift", "vardiya kapatma", "smenani yopish"));
            return IsOfflineShiftId(shiftId)
                ? CloseOfflineShiftLocally(shiftId, closingCash)
                : CloseLocallyAndQueue(shiftId!, closing, closingCash, "session expired");
        }
        catch (Exception ex)
        {
            // Ошибка на стороне сервера, которую из кассы не исправить (см. DescribeCloseFailure):
            // не запираем кассира. Смену закрываем ЛОКАЛЬНО и ставим в очередь — SyncService
            // дожмёт сервер, как только тот начнёт принимать закрытие.
            if (LooksLikeDecimalPlacesError(ex.Message))
                return CloseLocallyAndQueue(shiftId!, closing, closingCash, ex.Message);

            return CashShiftOperationResult.Failed(DescribeCloseFailure(ex));
        }
        finally
        {
            watch.Stop();
        }
    }

    private void MarkUnsentCash(string shiftId, UnsentCashOps ops)
    {
        _unsentCashMark = ops;
        _unsentCashMarkShiftId = shiftId;
    }

    private CashShiftOperationResult UnsentCashRefusal(UnsentCashOps ops) =>
        CashShiftOperationResult.Failed(Tr.T(
            $"Не все внесения и изъятия этой смены отправлены на сервер: осталось {ops.Count}. Дождитесь отправки (минута-две, нужна связь) и закройте смену ещё раз — иначе в Z-отчёте будет ложная недостача или излишек.",
            $"Бул сменанын бардык киргизүүлөрү жана алуулары серверге жөнөтүлгөн жок: {ops.Count} калды. Жөнөтүлгүчө күтүңүз (бир-эки мүнөт, байланыш керек) жана сменаны кайра жабыңыз — болбосо Z-отчётто жалган жетишпестик же ашыкча болот.",
            $"Not all deposits and withdrawals of this shift have been sent to the server: {ops.Count} left. Wait until they are sent (a minute or two, a connection is needed) and close the shift again — otherwise the Z report will show a false shortage or surplus.",
            $"Bu vardiyanın tüm para girişleri ve çıkışları sunucuya gönderilmedi: {ops.Count} kaldı. Gönderilmesini bekleyin (bir iki dakika, bağlantı gerekir) ve vardiyayı tekrar kapatın — aksi halde Z raporunda sahte eksik veya fazla görünür.",
            $"Bu smenaning barcha kiritish va olishlari serverga yuborilmadi: {ops.Count} ta qoldi. Yuborilishini kuting (bir-ikki daqiqa, aloqa kerak) va smenani qaytadan yoping — aks holda Z-hisobotda soxta kamomad yoki ortiqcha bo'ladi."))
        with { NeedsUnsentCashConfirm = true, UnsentCashOpsCount = ops.Count, UnsentCashOpsSum = ops.Sum, UnsentCashOpsDetails = ops.Details };

    /// <summary>2026-11-01: смена ЗАКРЫТА с пометкой (кассир подтвердил) — запись в журнал смены и оповещение владельцу. Неотправленные внесения/изъятия остаются в очереди,
    /// привязанные к ЭТОЙ (закрытой) смене: RebindOfflineShifts трогает только «offline-…», а хвосты закрытых смен досылает ShiftCashFlowSync.PushPendingForOtherShiftsAsync, —
    /// к следующей смене они не прилипают. Вызывается только при успешном закрытии; один раз.</summary>
    private void RecordUnsentCashMark(decimal? closingCash)
    {
        var ops = _unsentCashMark;
        var shiftId = _unsentCashMarkShiftId;
        _unsentCashMark = null;
        _unsentCashMarkShiftId = null;
        if (ops is null)
            return;

        var sum = ops.Sum.ToString("0.00", CultureInfo.InvariantCulture);
        var detail = string.IsNullOrWhiteSpace(ops.Details) ? "" : " " + ops.Details.Replace("\n", "; ");
        var note = Tr.T(
            $"Смена закрыта с пометкой: не отправлено операций (внесения/изъятия) — {ops.Count} на сумму {sum}.{detail} Они остаются в очереди и уйдут на сервер, привязанные к этой смене, когда появится связь.",
            $"Смена белги менен жабылды: жөнөтүлгөн эмес операциялар (киргизүү/алуу) — {ops.Count}, суммасы {sum}.{detail} Алар кезекте калат жана байланыш пайда болгондо ушул сменага байланышып серверге жөнөтүлөт.",
            $"Shift closed with a note: operations (deposits/withdrawals) not sent — {ops.Count}, total {sum}.{detail} They stay in the queue and go to the server bound to this shift once there is a connection.",
            $"Vardiya notla kapatıldı: gönderilmeyen işlemler (para giriş/çıkışı) — {ops.Count}, toplam {sum}.{detail} Kuyrukta kalır ve bağlantı gelince bu vardiyaya bağlı olarak sunucuya gönderilir.",
            $"Smena izoh bilan yopildi: yuborilmagan operatsiyalar (kiritish/olish) — {ops.Count} ta, jami {sum}.{detail} Ular navbatda qoladi va aloqa paydo bo'lganda shu smenaga bog'langan holda serverga yuboriladi.");
        PosLogger.Log($"Смена {shiftId}: {note}", "SHIFT");
        try { _auditDb.LogShift("close_marked_unsent_cash", closingCash, shiftId); }
        catch (Exception ex) { PosLogger.Log($"Журнал смены: запись «закрыта с пометкой» не сделана: {ex.Message}", "WARNING"); }
        try
        {
            IrregularReceiptsStore.Append(new IrregularReceiptEntry
            {
                Tag = IrregularReceiptEntry.ShiftClosedUnsentCash,
                CashierName = PosApp.CurrentUserDisplayName,
                Note = note,
                Total = (double)ops.Sum,
                CartJson = "{}",
            });
            if (TelegramBotService.IsConfigured)
            {
                var alert = TelegramBotService.BuildAlert(
                    Tr.T("Смена закрыта с пометкой", "Смена белги менен жабылды", "Shift closed with a note", "Vardiya notla kapatıldı", "Smena izoh bilan yopildi"),
                    note);
                _ = Task.Run(async () =>
                {
                    var error = await TelegramBotService.SendAsync(alert).ConfigureAwait(false);
                    if (error != null)
                        PosLogger.Log($"Оповещение владельцу о смене, закрытой с пометкой, не ушло ({error}).", "WARNING");
                });
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смена, закрытая с пометкой, не записана в «Нестандартные чеки»/оповещение: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-11-01 (К-03): ответ сервера «shift_not_owned» (код может быть в теле или в тексте).</summary>
    private static bool IsShiftNotOwned(ApiException ex)
    {
        try
        {
            return ((ex.Payload is { } p ? p.GetRawText() : "") + " " + ex.Message).Contains("shift_not_owned", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return ex.Message.Contains("shift_not_owned", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>2026-11-01: имя того, кто открыл уже открытую смену (если сервер его прислал в ответе «открыть смену»); без имени — null (в окне не показывается).</summary>
    private static string? ReadOpenedBy(JsonElement response)
    {
        var body = response;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("shift", out var inner) && inner.ValueKind == JsonValueKind.Object)
            body = inner;
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "cashier_name", "opened_by_name", "user_name", "cashier" })
            if (body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
        return null;
    }

    private static bool IsOfflineShiftId(string? shiftId) =>
        shiftId?.StartsWith("offline-", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>2026-09-29: офлайн-смена (сервер о ней не знает) — закрывается только в кассе,
    /// как в ветке UseLocalOperations выше.</summary>
    private CashShiftOperationResult CloseOfflineShiftLocally(string? shiftId, decimal? closingCash)
    {
        PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
        _auditDb.LogShift("close_offline", closingCash, shiftId);
        PosLogger.Log($"Офлайн-смена {shiftId} закрыта в кассе (сервер не отвечает).", "SHIFT");
        return CashShiftOperationResult.Success(closingCash, isOffline: true);
    }

    /// <summary>Закрывает смену на самой кассе и ставит её в очередь на закрытие сервера.
    /// Кассир продолжает работать: может открыть новую смену, пробивать чеки, сдавать кассу.
    /// Сервер дожимается фоном (SyncService.FlushPendingShiftClosesAsync).</summary>
    private CashShiftOperationResult CloseLocallyAndQueue(
        string shiftId, string? closingText, decimal? closingCash, string serverError)
    {
        PendingShiftCloseStore.Enqueue(shiftId, closingText, serverError);
        RecordUnsentCashMark(closingCash);

        PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        _offlinePosStateStore.SaveFromApp(closingCash ?? 0m); // 2026-11-01: запасной остаток после закрытия — пересчитанная сумма, а не 0
        _auditDb.LogShift("close_local_pending_server", closingCash, shiftId);

        PosLogger.Log(
            $"Смена {shiftId} закрыта локально: сервер отказал ({serverError}). Закрытие на сервере — фоном.",
            "SHIFT");

        return CashShiftOperationResult.Success(closingCash, isOffline: true);
    }

    /// <summary>Человеческое объяснение вместо сырого ответа Django.
    ///
    /// Разобрано 2026-09-22: сервер сам хранит сумму смены с ПЯТЬЮ знаками после запятой
    /// (income_total = '150.00000' — прочитано запросом к api/construction/shifts/) и сам же
    /// отклоняет её своей проверкой «не более 2 цифр после запятой» при закрытии. Касса это
    /// поле не отправляет вовсе, изменить его не может (PATCH на смену — 405 «Метод не
    /// разрешён»), а других эндпоинтов закрытия у сервера нет. То есть из программы это не
    /// лечится ничем — кассиру нужно сказать об этом прямо, а не показывать текст ошибки,
    /// по которому он ничего не поймёт и решит, что сломалась касса.</summary>
    private static string DescribeCloseFailure(Exception ex)
    {
        if (!LooksLikeDecimalPlacesError(ex.Message))
            return Tr.T("Ошибка закрытия смены: ", "Сменаны жабууда ката кетти: ",
                "Error closing the shift: ", "Vardiya kapatılırken hata oluştu: ", "Smenani yopishda xato: ") + ex.Message;

        return Tr.T("Смену не удаётся закрыть из-за ошибки на стороне сервера NurCRM: он хранит сумму "
                + "смены с лишними знаками после запятой и сам же её не принимает при закрытии. "
                + "Из кассы это не исправить — поле доступно только для чтения.",
                "NurCRM серверинин катасынан улам сменаны жабуу мүмкүн эмес: сервер сменанын суммасын "
                + "үтүрдөн кийин ашыкча цифралар менен сактайт жана жабууда аны өзү кабыл албайт. "
                + "Муну кассадан оңдоого болбойт — бул талаа окуу үчүн гана.",
                "The shift can't be closed because of an error on the NurCRM server side: it stores the shift "
                + "total with extra decimal places and then rejects it itself when closing. "
                + "This can't be fixed from the till — the field is read-only.",
                "Vardiya, NurCRM sunucusundaki bir hata nedeniyle kapatılamıyor: sunucu vardiya tutarını "
                + "virgülden sonra fazla basamakla saklıyor ve kapatırken bunu kendisi kabul etmiyor. "
                + "Bu sorun kasadan düzeltilemez — alan salt okunurdur.",
                "NurCRM serveridagi xato tufayli smenani yopib bo'lmayapti: server smena summasini "
                + "verguldan keyin ortiqcha raqamlar bilan saqlaydi va yopishda uni o'zi qabul qilmaydi. "
                + "Buni kassadan tuzatib bo'lmaydi — maydon faqat o'qish uchun.")
             + Environment.NewLine + Environment.NewLine
             + Tr.T("Что делать: попробуйте закрыть смену в веб-панели NurCRM. Если и там не выйдет — "
                + "обратитесь в поддержку NurCRM и передайте им это сообщение вместе с ответом сервера:",
                "Эмне кылуу керек: сменаны NurCRM веб-панелинде жабып көрүңүз. Ал жерде да болбосо — "
                + "NurCRM колдоо кызматына кайрылып, бул билдирүүнү сервердин жообу менен кошо жибериңиз:",
                "What to do: try closing the shift in the NurCRM web panel. If that doesn't work either, "
                + "contact NurCRM support and send them this message together with the server response:",
                "Ne yapmalı: vardiyayı NurCRM web panelinden kapatmayı deneyin. Orada da olmazsa "
                + "NurCRM desteğine başvurun ve bu mesajı sunucu yanıtıyla birlikte iletin:",
                "Nima qilish kerak: smenani NurCRM veb-panelida yopib ko'ring. U yerda ham bo'lmasa, "
                + "NurCRM yordam xizmatiga murojaat qiling va ushbu xabarni server javobi bilan birga yuboring:")
             + Environment.NewLine + ex.Message;
    }

    /// <summary>Отличает именно ошибку «слишком много знаков после запятой» от прочих отказов
    /// сервера: повторять с округлением имеет смысл только в этом случае.</summary>
    internal static bool LooksLikeDecimalPlacesError(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("после запятой", StringComparison.OrdinalIgnoreCase)
            || message.Contains("decimal places", StringComparison.OrdinalIgnoreCase));

    /// <summary>Читает суммы смены с сервера и возвращает их округлёнными до копеек.
    /// Пустой словарь — значит прочитать не удалось и повторять нечем.
    /// 2026-10-27 (Н-14): статический, чтобы тот же повтор с округлением был у очереди закрытий (ShiftCloseRetry).</summary>
    internal static async Task<Dictionary<string, string>> BuildRoundedTotalsAsync(
        IShiftApiService shiftApi, string shiftId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var list = await shiftApi.ConstructionShiftsListAsync(true, cancellationToken).ConfigureAwait(false);
            var shift = FindShiftById(list, shiftId);
            if (shift is not { } element)
                return result;

            foreach (var field in new[] { "income_total", "expense_total" })
            {
                if (!element.TryGetProperty(field, out var value))
                    continue;

                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    result[field] = Math.Round(number, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось прочитать суммы смены для повтора закрытия: {ex.Message}", "WARNING");
        }

        return result;
    }

    internal static JsonElement? FindShiftById(JsonElement list, string shiftId)
    {
        var items = list.ValueKind == JsonValueKind.Array
            ? list
            : list.ValueKind == JsonValueKind.Object && list.TryGetProperty("results", out var results)
                ? results
                : default;

        if (items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), shiftId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>См. комментарий у первого вызова Success(..., totals:) в CloseShiftAsync —
    /// переводит ShiftBalanceHelper.ShiftTotals (Infrastructure) в плоский Core-тип, который
    /// умеет пронести CashShiftOperationResult наружу без ссылки Core → Infrastructure.</summary>
    private CashShiftClosingTotals? ReadClosingTotals(
        System.Text.Json.JsonElement response, string? shiftId, CancellationToken cancellationToken)
    {
        if (response.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        var t = ShiftBalanceHelper.ReadShiftTotals(response);
        // Долг — тот же расчёт с теми же данными, но не ожидаемый здесь: Z-отчёт открывается
        // сразу после закрытия, а долг подставляется в него, когда посчитается (см. PendingDebtSales).
        return new CashShiftClosingTotals(
            t.OpeningCash, t.TotalSales, t.CashSales, t.NonCashSales, t.DebtSales, t.SalesCount)
        {
            PendingDebtSales = t.DebtSales is null
                ? TryComputeDebtTotalAsync(t.SalesCount, shiftId, cancellationToken)
                : null,
        };
    }

    /// <inheritdoc/>
    public Task<decimal?> ResolveShiftDebtTotalAsync(
        string shiftId, int? salesCountHint = null, CancellationToken cancellationToken = default) =>
        TryComputeDebtTotalAsync(salesCountHint, shiftId, cancellationToken);

    /// <summary>2026-09-15, живой баг ("Долг 480 сом за смену, где по факту только один долг на
    /// 160") — раньше этот метод суммировал ВСЕ продажи с payment_method=="debt" в списке продаж
    /// КАССЫ (не смены!), а список продаж одной кассы копится через много смен подряд — три
    /// тестовые продажи «в долг» по 160 сом в ТРЁХ РАЗНЫХ сменах одной кассы давали в сумме ровно
    /// 480 = 3×160 в отчёте закрытия КАЖДОЙ из них. Теперь дополнительно сверяем shift_id самой
    /// продажи с ID именно ЗАКРЫВАЕМОЙ смены. Поле подтверждено живым захватом (см. [DEBUG] Debt
    /// sale row sample в логе) — у строки продажи есть простое строковое поле "shift" (не
    /// вложенный объект), а сумма долга — отдельное числовое поле "debt_amount" (не "total": оно
    /// тоже 160 в наших тестах, но только потому что cash_received на сервере для payment_method=
    /// debt всегда "0.00" независимо от того, что кассир ввёл как "получено сейчас" — см.
    /// TryApplyInitialDebtPaymentAsync). Если НИ У ОДНОЙ строки в ответе нет "shift" (например,
    /// сервер сменит схему) — честно возвращаем null ("—" в отчёте), чем повторяем тот же баг
    /// с чужими сменами.
    ///
    /// 2026-09-15, второй живой баг ("оплата частичного долга вообще не попадает в отчёт") —
    /// salesCount ЗДЕСЬ приходит из "sales_count" самого ответа закрытия смены, а это поле, как
    /// уже выяснилось, НЕ считает долговые продажи вообще (смена только с одной продажей «в долг»
    /// и без единой наличной/безналичной показывала salesCount=0). Старая проверка "salesCount >
    /// 0" из-за этого пропускала весь расчёт долга ЦЕЛИКОМ для такой смены — отчёт показывал "—"
    /// вместо реального долга. Теперь salesCount используется только для размера страницы (когда
    /// его нет или он 0 — берём разумный запас по умолчанию), а не как условие "искать или нет".
    ///
    /// 2026-09-15, третий живой баг ("долг за смену вырос ПОСЛЕ успешной частичной оплаты") —
    /// раньше суммировалось поле "debt_amount"/"total" самой продажи, а это, как и cash_received,
    /// статичное поле исходной суммы продажи — сервер НЕ обновляет его после оплаты через сделку
    /// (тот же нюанс, что уже решён в PayDebtDialog.ResolveRealRemainingDebtAsync — тем же самым
    /// способом: реальный остаток — это remaining_debt сделки, а не total/debt_amount продажи).
    /// Теперь для каждой долговой продажи этой смены отдельно уточняем deal_id (PosSaleGetAsync)
    /// и текущий remaining_debt (ClientDealGetAsync), параллельно по всем сразу.</summary>
    private async Task<decimal?> TryComputeDebtTotalAsync(int? salesCount, string? shiftId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;

        // 2026-10-04, отчёт о производительности (п. 12): долг смены уже посчитан сервером — by_payment.debt
        // отчёта смены (остаток долга по продажам смены, сверено со сделками: смена 7e33b63b — 31,00 и там, и
        // там). Один запрос ~0,3 с вместо до 10 страниц списка продаж и двух запросов на каждую продажу в
        // долг. Нет отчёта или в нём нет поля debt — считаем по-старому.
        try
        {
            var report = await Api.NurCrmReportsApi.GetShiftReportAsync(shiftId, cancellationToken).ConfigureAwait(false);
            if (report is not null && report.ByPayment.ContainsKey("debt"))
                return report.Debt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долг смены: отчёт смены сервера не получен, считаю по продажам: {ex.Message}", "SHIFT");
        }

        try
        {
            var cashboxId = PosApp.PosCashboxId;

            // 2026-09-28, регресс 1.17.19: читалась только первая страница продаж кассы размером
            // «продаж смены + 20». Для закрытой смены, после которой прошли другие продажи (в
            // «Финансах» владельца, в «Истории смен»), чеки этой смены на первую страницу не
            // попадали — «Долг: —» вместо 40 сом. Теперь листаем (новые — первыми), пока не
            // пройдём чеки этой смены: страница без её чеков после того, как они уже встречались,
            // значит, смена позади. Не нашли вовсе — останавливаемся на MaxDebtPages.
            const int DebtPageSize = 80;
            const int MaxDebtPages = 10;
            var sales = new List<System.Text.Json.JsonElement>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var sawTargetShift = false;
            for (var page = 1; page <= MaxDebtPages; page++)
            {
                var rows = await _salesApi.PosSalesListAsync(page, DebtPageSize, cashboxId, cancellationToken).ConfigureAwait(false);
                var added = 0;
                var pageHasTarget = false;
                var lastRowIsTarget = false;
                foreach (var row in rows)
                {
                    if (row.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;
                    // Страницу за последней сервер отдаёт повтором последней — повторы не в счёт.
                    var rowId = row.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? idEl.GetString()
                        : null;
                    if (rowId is not null && !seenIds.Add(rowId))
                        continue;
                    added++;
                    sales.Add(row);
                    lastRowIsTarget = string.Equals(TryReadShiftId(row), shiftId, StringComparison.Ordinal);
                    pageHasTarget |= lastRowIsTarget;
                }

                // Чеки смены начались и уже кончились на этой странице (дальше — более старые
                // смены этой кассы) или кончились на прошлой — дальше листать незачем.
                if ((sawTargetShift && !pageHasTarget) || (pageHasTarget && !lastRowIsTarget && cashboxId is not null))
                    break;
                sawTargetShift |= pageHasTarget;
                if (rows.Count < DebtPageSize || added == 0)
                    break;
            }

            var matchingDebtSales = new List<(string SaleId, string ClientId, decimal FallbackAmount)>();
            // Поле смены есть у любой продажи, не только у долговой: если его нет ни у одной
            // строки, сервер сменил схему — честное «—» (см. комментарий у метода).
            var sawAnyShiftField = sales.Any(s => TryReadShiftId(s) is not null);
            var loggedSampleRow = false;
            foreach (var sale in sales)
            {
                if (sale.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                if (!sale.TryGetProperty("payment_method", out var pm) || pm.ValueKind != System.Text.Json.JsonValueKind.String
                    || !string.Equals(pm.GetString(), "debt", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!loggedSampleRow)
                {
                    // Диагностика для подтверждения реального имени поля — см. комментарий у метода.
                    PosLogger.Log($"[DEBUG] Debt sale row sample: {sale.GetRawText()}", "SHIFT");
                    loggedSampleRow = true;
                }

                var saleShiftId = TryReadShiftId(sale);
                if (saleShiftId is null || !string.Equals(saleShiftId, shiftId, StringComparison.Ordinal))
                    continue;

                if (!sale.TryGetProperty("id", out var saleIdEl) || saleIdEl.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(saleIdEl.GetString()))
                    continue;
                if (!sale.TryGetProperty("client", out var clientIdEl) || clientIdEl.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(clientIdEl.GetString()))
                    continue;

                var fallbackAmount = 0m;
                if (sale.TryGetProperty("debt_amount", out var totalEl) || sale.TryGetProperty("total", out totalEl))
                {
                    fallbackAmount = totalEl.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Number => totalEl.TryGetDecimal(out var d) ? d : 0m,
                        System.Text.Json.JsonValueKind.String =>
                            decimal.TryParse(totalEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : 0m,
                        _ => 0m,
                    };
                }

                matchingDebtSales.Add((saleIdEl.GetString()!, clientIdEl.GetString()!, fallbackAmount));
            }

            if (!sawAnyShiftField)
            {
                PosLogger.Log(
                    "Debt total: ни одна долговая продажа в ответе не содержит распознанного поля " +
                    "shift_id — посчитать долг именно этой смены нельзя, возвращаем «—».", "SHIFT");
                return null;
            }

            var remainingAmounts = await Task.WhenAll(
                matchingDebtSales.Select(s => ResolveRealRemainingDebtAsync(s.SaleId, s.ClientId, s.FallbackAmount, cancellationToken))
            ).ConfigureAwait(false);

            return remainingAmounts.Sum();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Debt total computation from sales list failed: {ex.Message}", "SHIFT");
            return null;
        }
    }

    /// <summary>Тот же приём, что уже проверен в PayDebtDialog.ResolveRealRemainingDebtAsync —
    /// remaining_debt сделки, а не статичный debt_amount/total продажи. При любой ошибке (сеть,
    /// сделка не найдена и т.п.) откатывается на fallbackAmount — лучше показать чуть неточную,
    /// но не нулевую цифру, чем уронить весь расчёт долга смены из-за одной продажи.</summary>
    private async Task<decimal> ResolveRealRemainingDebtAsync(
        string saleId, string clientId, decimal fallbackAmount, CancellationToken cancellationToken)
    {
        try
        {
            // По запросу на каждую долговую продажу смены — в общем темпе массовых загрузок,
            // чтобы не упереться в ограничение частоты запросов сервера.
            var saleDetail = await NurMarketKassa.Services.Api.ApiThrottle
                .RunBulkAsync(() => _salesApi.PosSaleGetAsync(saleId, cancellationToken), cancellationToken).ConfigureAwait(false);
            var dealId = saleDetail.ValueKind == System.Text.Json.JsonValueKind.Object
                && saleDetail.TryGetProperty("deal_id", out var dealIdEl)
                && dealIdEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? dealIdEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(dealId))
                return fallbackAmount;

            var deal = await _salesApi.ClientDealGetAsync(clientId, dealId, cancellationToken).ConfigureAwait(false);
            if (deal.ValueKind != System.Text.Json.JsonValueKind.Object
                || !deal.TryGetProperty("remaining_debt", out var remainingEl))
                return fallbackAmount;

            var remainingText = remainingEl.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => remainingEl.GetString(),
                System.Text.Json.JsonValueKind.Number => remainingEl.GetRawText(),
                _ => null,
            };
            return remainingText is not null
                && decimal.TryParse(remainingText, NumberStyles.Any, CultureInfo.InvariantCulture, out var remaining)
                    ? remaining
                    : fallbackAmount;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Debt total: remaining-debt resolve skipped for sale {saleId}: {ex.GetType().Name}", "SHIFT");
            return fallbackAmount;
        }
    }

    private static string? TryReadShiftId(System.Text.Json.JsonElement sale)
    {
        foreach (var key in new[] { "shift_id", "shift", "construction_shift_id", "construction_shift", "cashbox_shift_id" })
        {
            if (!sale.TryGetProperty(key, out var v))
                continue;

            if (v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();

            if (v.ValueKind == System.Text.Json.JsonValueKind.Object && v.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
                return idEl.GetString();
        }

        return null;
    }

    /// <summary>См. catch(ApiException 404) в CloseShiftAsync — спрашивает у сервера ID
    /// реально открытой сейчас смены для текущей кассы.</summary>
    private async Task<string?> TryRecoverActiveShiftIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _shiftApi.ConstructionShiftsListAsync(openOnly: true, ct: cancellationToken).ConfigureAwait(false);
            return ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId, PosApp.CurrentUserId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Active shift id recovery failed: {ex}", "SHIFT");
            return null;
        }
    }

    public Task<bool> PrintReportAsync(string reportText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportText))
            return Task.FromResult(false);

        return _receiptPrinter.PrintReceiptAsync(
            new CartSnapshot
            {
                CartJson = "{}",
                ReceiptText = reportText,
                OfflineNote = "SHIFT REPORT",
                PaymentMethodKey = null,
                CashReceived = null,
            },
            cancellationToken);
    }

    private async Task<string?> EnsurePosCashboxIdAsync(CancellationToken cancellationToken)
    {
        // 2026-09-14, живой баг: "Оплата не прошла — cashbox_id: Касса не найдена или не
        // принадлежит этому филиалу". PosApp.PosCashboxId — это ИМЕННО то значение, которое
        // PosCheckoutService.BuildCheckoutRequestBody кладёт в cashbox_id при оплате (см. её
        // комментарий). Раньше, если оно уже было чем-то заполнено (восстановлено из прошлой
        // сессии), этот метод возвращал его как есть, без единой проверки — тот же класс бага,
        // что уже был исправлен в MainWindow.RefreshShiftStateAsync для ДРУГОГО, чисто
        // UI-слойного холдера ID кассы (App.PosCashboxId/NurMarketKassa.App.PosCashboxId), но
        // тот фикс на PosApp.PosCashboxId не влиял вовсе — отдельная переменная, синхронизация
        // между ними односторонняя (PosAppBridge.SyncToSession читает ИЗ PosApp, не пишет В
        // него). Если админ на сервере переназначил кассира на другой филиал или переместил/
        // удалил кассу, старый ID так и оставался тут навсегда — реальный источник ошибки.
        // Теперь уже сохранённый ID тоже сверяется со свежим списком касс перед открытием
        // смены (смена открывается один раз в начале работы, лишний сетевой запрос здесь не
        // ощутим, в отличие от ежесекундных операций).
        var current = PosApp.PosCashboxId;
        var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);

        // 2026-09-14: "current" мог оказаться кассой, которую сервер УЖЕ отверг за эту сессию
        // (PosApp.RejectedCashboxIds) — например, если PosCheckoutService.TryReassignCashboxAsync
        // не нашёл замены и оставил её как есть (см. её комментарий). Раньше эта проверка
        // смотрела только "есть ли ID в списке компании" и снова отправляла на открытие смены
        // заведомо отвергнутую кассу — открытие сразу падало с той же ошибкой.
        if (!string.IsNullOrWhiteSpace(current) && !PosApp.RejectedCashboxIds.Contains(current)
            && CartDisplayHelper.ListCashboxes(rawList).Any(c => c.Id == current))
            return current;

        // Тот же порядок выбора, что в TryReassignCashboxAsync ниже — активная и ещё не
        // отвергнутая за сессию касса в приоритете, иначе первая ещё не отвергнутая.
        var candidates = CartDisplayHelper.ListCashboxes(rawList)
            .Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id))
            .ToList();
        var next = candidates.FirstOrDefault(c => c.IsActive);
        if (next.Id == null)
            next = candidates.FirstOrDefault();
        if (next.Id == null)
            return current;

        PosApp.PosCashboxId = next.Id;
        PosApp.PosCashboxDisplayName = next.DisplayName;
        return next.Id;
    }
}
