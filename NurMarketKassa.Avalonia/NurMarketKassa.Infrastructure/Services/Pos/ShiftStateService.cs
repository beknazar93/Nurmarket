using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

public sealed class ShiftStateService : IShiftStateService
{
    private readonly IShiftApiService _shiftApi;
    private readonly IAutonomousAuthService _autonomous;

    public ShiftStateService(IShiftApiService shiftApi, IAutonomousAuthService autonomous)
    {
        _shiftApi = shiftApi;
        _autonomous = autonomous;
    }

    public event Action<ShiftStateSnapshot>? StateRefreshed;

    public string? ActiveShiftId => PosApp.ActiveShiftId;

    public bool IsShiftOpen => !string.IsNullOrEmpty(PosApp.ActiveShiftId);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        decimal? balance = null;

        // 2026-09-12: реальный баг с живого теста — "Остаток по системе" при закрытии смены
        // показывал 0.00, хотя на сервере (и в вебе) уже накопился настоящий итог смены в сотни
        // тысяч сом. Причина: OfflineModeHelper.UseLocalOperations — это флаг "сессия НАЧАЛАСЬ
        // офлайн", он не сбрасывается обратно сам, даже когда интернет потом восстанавливается —
        // так что эта функция ни разу больше не спрашивала у сервера баланс, застревая на
        // стартовом остатке смены. Для настоящего автономного аккаунта (сервера нет в принципе)
        // так и должно быть — но для обычного NurCRM-аккаунта, который просто открыл смену без
        // сети в моменте, сеть может вернуться в любую секунду, и тогда нужно доверять серверу
        // (который знает про ВСЕ продажи смены, а не только те, что успели пройти локально),
        // а не застрявшему локальному числу.
        // 2026-09-29: сервер не отвечает (ServerOutageMonitor) — не ждём таймаута списка смен при
        // каждой оплате и новом чеке, берём сохранённое состояние, как при отказе запроса ниже.
        if (_autonomous.IsCurrentSessionAutonomous || OfflineModeHelper.IsServerOutage)
        {
            OfflinePosStateStore.RestoreToApp();
            balance = OfflinePosStateStore.ReadShiftCashBalance();
            StateRefreshed?.Invoke(new ShiftStateSnapshot
            {
                ActiveShiftId = PosApp.ActiveShiftId,
                CashBalance = balance,
            });
            return;
        }

        try
        {
            // 2026-09-12: живой баг сразу после включения этого запроса — на машинах с протухшей
            // (но ещё не разлогиненной) NurCRM-сессией сервер не отвечает ошибкой мгновенно, а
            // висит до дефолтного таймаута HttpClient (десятки секунд) — именно это превращало
            // обычный вход в кассу в "очень долгий вход", хотя сама проверка авторизации тут ни
            // при чём. Явный короткий таймаут: либо получаем реальный баланс быстро, либо честно
            // и быстро откатываемся на локальный (тот же catch ниже, что и раньше).
            // 2026-10-30 (приёмка 1.17.65, «Важно» 2): 429 («Сервер занят») не обрыв — состояние смены не сбрасываем. Запрос критичный (идёт первым после паузы
            // сервера), при 429 — до трёх попыток с паузой Retry-After; каждой попытке 6 с + оставшаяся пауза. Не вышло — ниже последнее известное состояние.
            using var critical = ApiThrottle.BeginCritical();
            var shiftAtStart = PosApp.ActiveShiftId; // 2026-11-01 (К2-02): смена на момент начала сверки — после await присваиваем, только если она не менялась
            var list = await ApiThrottle.RunWithRetryAsync(async () =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(6) + ApiThrottle.RemainingBlock);
                return await _shiftApi.ConstructionShiftsListAsync(openOnly: true, ct: timeoutCts.Token).ConfigureAwait(false);
            }, 3, null, cancellationToken).ConfigureAwait(false);
            var openId = ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId, PosApp.CurrentUserId);
            if (string.IsNullOrEmpty(openId)
                && ShiftHelper.FindCashierShiftOnOtherCashbox(list, PosApp.CurrentUserId) is { } own
                && CanFollowCashbox(own.CashboxId))
            {
                // Своя смена этого кассира, но под другой кассой — переходим на ту кассу целиком,
                // а не только на смену: иначе продажа уйдёт в одну кассу, а шапка, «Продажи» и
                // итоги смены будут смотреть в другую (см. ShiftHelper.PickOpenShiftId).
                PosLogger.Log(
                    $"Открытая смена кассира найдена на другой кассе: {own.CashboxName ?? own.CashboxId} "
                    + $"(была {PosApp.PosCashboxDisplayName ?? PosApp.PosCashboxId}) — касса переключена на неё.",
                    "SHIFT");
                // 2026-11-02 (К2-15, аудит №2): раньше — только строка в журнале. Две кассы одного кассира молча садились в одну смену; теперь кассир видит, что касса переключена,
                // и что делать, если это другой компьютер.
                var wasCashbox = PosApp.PosCashboxDisplayName ?? PosApp.PosCashboxId ?? "—";
                var nowCashbox = own.CashboxName ?? own.CashboxId;
                PosApp.PosCashboxId = own.CashboxId;
                if (!string.IsNullOrWhiteSpace(own.CashboxName))
                    PosApp.PosCashboxDisplayName = own.CashboxName;
                openId = own.ShiftId;
                RaiseShiftNotice(Tr.T($"Ваша смена открыта на кассе «{nowCashbox}» (здесь была «{wasCashbox}»), поэтому касса переключена на неё. Если это другой компьютер, закрепите нужную кассу в Настройках, иначе продажи с двух компьютеров пойдут в одну смену.",
                    $"Сиздин смена «{nowCashbox}» кассасында ачык (бул жерде «{wasCashbox}» болчу), ошондуктан касса ага которулду. Бул башка компьютер болсо, керектүү кассаны Жөндөөлөрдө бекитиңиз, болбосо эки компьютердин сатуулары бир сменага түшөт.",
                    $"Your shift is open on till “{nowCashbox}” (this one was “{wasCashbox}”), so the till has switched to it. If this is another computer, pin the right till in Settings, otherwise sales from both computers go into one shift.",
                    $"Vardiyanız «{nowCashbox}» kasasında açık (burası «{wasCashbox}» idi), bu yüzden kasa ona geçti. Bu başka bir bilgisayarsa Ayarlar'da doğru kasayı sabitleyin, aksi halde iki bilgisayarın satışları tek vardiyaya düşer.",
                    $"Smenangiz «{nowCashbox}» kassasida ochiq (bu yerda «{wasCashbox}» edi), shuning uchun kassa unga o'tdi. Bu boshqa kompyuter bo'lsa, Sozlamalarda kerakli kassani mahkamlang, aks holda ikkala kompyuterning sotuvlari bir smenaga tushadi."), true);
            }

            // 2026-11-01 (К-02, аудит «10 из 10»): смена, открытая без связи («offline-shift-…»), раньше обнулялась здесь же, как только сервер не находил
            // открытой смены, — Z-отчёт и сумма открытия терялись, а чеки и внесения уходили в следующую смену (возможно, другого кассира).
            // Теперь: сервер вернул открытую смену кассира — офлайн-смена заменяется ею (внесения перепривязывает ShiftCashFlowSync.PushPendingAsync);
            // не вернул — офлайн-смена остаётся, касса «догоняет»: открывает серверную смену с той же начальной суммой и переходит на неё.
            // 2026-11-01 (К2-01, контрольный аудит): сервер вернул открытую смену, которая стоит в очереди закрытия (её закрыли на кассе без связи, а на сервере она ещё открыта).
            // Раньше она подхватывалась: утренние продажи шли во вчерашнюю смену, вчерашний пересчёт и сумма открытия терялись. Теперь такую смену НЕ принимаем: пробуем закрыть её
            // на сервере (после досылки её чеков), смену кассы оставляем как есть (офлайн-смена — остаётся, иначе — «откройте смену»). Уже активную смену правило не трогает.
            if (!string.IsNullOrEmpty(openId)
                && !string.Equals(openId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase)
                && PendingShiftCloseStore.TryGet(openId) is { } queuedClose)
            {
                // 2026-11-02 (К3-06, аудит №2): попытка закрытия идёт В ФОНЕ и не чаще раза в минуту — «Новый чек»/опрос шапки не ждут до 6 с на каждый вызов.
                StartQueuedShiftCloseInBackground(openId, queuedClose.ClosingCash);
                openId = null;
            }

            var currentShift = PosApp.ActiveShiftId;
            string? handedOverFrom = null; // 2026-11-01: исходный офлайн-id, если ниже офлайн-смена заменяется серверной
            // 2026-11-01 (К-02, приёмка): офлайн-смена другого кассира (в сохранённом состоянии записан иной кассир, чем вошедший) не наша — прежнее поведение:
            // сбрасываем и пишем в журнал; серверную смену на чужую начальную сумму не открываем.
            var foreignOffline = IsOfflineShift(currentShift) && IsOfflineShiftOfOtherCashier(currentShift!);
            if (foreignOffline)
                PosLogger.Log($"Смена: офлайн-смена {currentShift} принадлежит другому кассиру — сбрасываю (серверную смену для неё не открываю).", "SHIFT");
            if (IsOfflineShift(currentShift) && !foreignOffline)
            {
                if (string.IsNullOrEmpty(openId))
                    openId = await TryOpenServerShiftForOfflineAsync(currentShift!, cancellationToken).ConfigureAwait(false);
                else
                    PosLogger.Log($"Смена: офлайн-смена {currentShift} заменена открытой сменой сервера {openId} (чеки офлайн-смены уйдут в неё).", "SHIFT");

                if (!string.IsNullOrEmpty(openId))
                    handedOverFrom = currentShift;
                if (string.IsNullOrEmpty(openId))
                {
                    PosLogger.Log($"Смена: у сервера нет открытой смены кассира — офлайн-смена {currentShift} остаётся (Z-отчёт и начальная сумма сохранены).", "SHIFT");
                    balance = OfflinePosStateStore.ReadShiftCashBalance();
                    StateRefreshed?.Invoke(new ShiftStateSnapshot
                    {
                        ActiveShiftId = PosApp.ActiveShiftId,
                        CashBalance = balance,
                    });
                    return;
                }
            }

            // 2026-11-01 (К2-02): пока шла сверка/открытие на сервере, смену могли закрыть или сменить (кассир нажал «Закрыть») — тогда результат не применяем,
            // иначе закрытая смена снова «откроется». Серверную смену, успевшую открыться в это время, ставит в очередь закрытия TryOpenServerShiftForOfflineAsync.
            if (!string.Equals(PosApp.ActiveShiftId, shiftAtStart, StringComparison.OrdinalIgnoreCase))
            {
                PosLogger.Log($"Смена: пока шла сверка с сервером, смена на кассе изменилась ({shiftAtStart ?? "нет"} → {PosApp.ActiveShiftId ?? "нет"}) — результат сверки не применяю.", "SHIFT");
                StateRefreshed?.Invoke(new ShiftStateSnapshot
                {
                    ActiveShiftId = PosApp.ActiveShiftId,
                    CashBalance = OfflinePosStateStore.ReadShiftCashBalance(),
                });
                return;
            }

            PosApp.ActiveShiftId = string.IsNullOrEmpty(openId) ? null : openId;
            balance = ShiftBalanceHelper.FindOpenShiftBalance(list, PosApp.PosCashboxId);
            if (balance is { } apiBalance)
            {
                OfflinePosStateStore.SaveFromApp(apiBalance);
            }
            else
            {
                // Баланс не распознан в ответе API — сохраняем состояние смены/кассы,
                // но не затираем ранее сохранённый (возможно ненулевой) остаток нулём.
                PosLogger.Log("Не удалось определить остаток кассы из ответа API — сохранённый остаток оставлен без изменений.", "SHIFT");
                OfflinePosStateStore.SaveFromApp(OfflinePosStateStore.ReadShiftCashBalance());
            }
            // 2026-11-01 (code-review): чеки/внесения офлайн-смены проверяются при закрытии и после подмены id.
            if (handedOverFrom is not null)
                OfflinePosStateStore.RememberOriginOfflineShift(handedOverFrom);
        }
        catch (Exception ex)
        {
            // 2026-09-12: раньше здесь не было лога — при живой отладке "разбивка не
            // отображается" не было видно, срабатывает ли вообще этот откат и почему.
            PosLogger.Log(ApiThrottle.IsThrottled(ex)
                ? "Shift balance refresh: сервер занят (429) — оставляю последнее известное состояние смены и кассы."
                : $"Shift balance refresh failed/timed out: {ex.GetType().Name}: {ex.Message}", "SHIFT");
            OfflinePosStateStore.RestoreToApp();
            balance = OfflinePosStateStore.ReadShiftCashBalance();
        }

        StateRefreshed?.Invoke(new ShiftStateSnapshot
        {
            ActiveShiftId = PosApp.ActiveShiftId,
            CashBalance = balance,
        });
    }

    private static readonly Dictionary<string, DateTime> QueuedCloseAttempts = new(StringComparer.OrdinalIgnoreCase);
    private static int _queuedCloseRunning;

    /// <summary>2026-11-02 (К3-06): сообщение кассиру от сверки смены (текст, предупреждение ли). Подписчик — главное окно (тост / окно). Приходит с потока пула.</summary>
    public static event Action<string, bool>? ShiftNotice;

    internal static void RaiseShiftNotice(string text, bool warning) // 2026-11-06: internal — предупреждение о пересчёте долга сервером после возврата без связи (OfflineOperationsReplayer)
    {
        try { ShiftNotice?.Invoke(text, warning); }
        catch (Exception ex) { PosLogger.Log($"Смена: сообщение кассиру не показано: {ex.Message}", "SHIFT"); }
    }

    /// <summary>2026-11-01 (К2-01) / 2026-11-02 (К3-06): смена из очереди закрытия ещё открыта на сервере — пробуем закрыть её в фоне (не чаще раза в минуту на смену, по одной попытке за раз),
    /// если чеки этой смены уже ушли; иначе ждёт фоновую очередь (SyncService). Вызывающий не ждёт: кассе смена не принимается в любом случае.</summary>
    private void StartQueuedShiftCloseInBackground(string shiftId, string? closingCash)
    {
        lock (QueuedCloseAttempts)
        {
            if (QueuedCloseAttempts.TryGetValue(shiftId, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(60))
                return;
            QueuedCloseAttempts[shiftId] = DateTime.UtcNow;
        }

        if (Interlocked.Exchange(ref _queuedCloseRunning, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await TryCloseQueuedShiftAsync(shiftId, closingCash).ConfigureAwait(false))
                    RaiseShiftNotice(Tr.T("Вчерашняя смена, закрытая без связи, теперь закрыта и на сервере.",
                        "Байланышсыз жабылган кечээки смена эми серверде да жабылды.",
                        "The previous shift, closed without a connection, is now closed on the server too.",
                        "Bağlantısız kapatılan önceki vardiya artık sunucuda da kapatıldı.",
                        "Aloqasiz yopilgan oldingi smena endi serverda ham yopildi."), false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена: фоновое закрытие смены {shiftId} из очереди: {ex.GetType().Name}: {ex.Message}", "SHIFT");
            }
            finally
            {
                Interlocked.Exchange(ref _queuedCloseRunning, 0);
            }
        });
    }

    private async Task<bool> TryCloseQueuedShiftAsync(string shiftId, string? closingCash)
    {
        var queuedSales = OfflinePendingSalesStore.LoadPendingForSync().Count(e => string.Equals(e.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase));
        if (queuedSales > 0)
        {
            PosLogger.Log($"Смена: открытая на сервере смена {shiftId} стоит в очереди закрытия и не принимается; её чеков в очереди {queuedSales} — закроется после досылки.", "SHIFT");
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var (outcome, error) = await ShiftCloseRetry.CloseAsync(_shiftApi, shiftId, closingCash, cts.Token).ConfigureAwait(false);
            if (outcome != ShiftCloseOutcome.Failed)
                PendingShiftCloseStore.Remove(shiftId);
            PosLogger.Log($"Смена: смена {shiftId} из очереди закрытия не принята кассой как текущая; закрытие на сервере: {outcome}{(error is null ? "" : " (" + error + ")")}.", "SHIFT");
            return outcome != ShiftCloseOutcome.Failed;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смена: закрыть на сервере смену {shiftId} из очереди не вышло ({ex.GetType().Name}: {ex.Message}) — повторит фоновая очередь.", "SHIFT");
            return false;
        }
    }

    private static bool IsOfflineShift(string? shiftId) =>
        !string.IsNullOrEmpty(shiftId) && shiftId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase);

    private static DateTime _lastCatchUpUtc = DateTime.MinValue;

    /// <summary>2026-11-01 (К-02, code-review): офлайн-смена передана серверу (открыта серверная смена) — текст для кассира; подписчик — главное окно (плашка-тост).</summary>
    public static event Action<string>? OfflineShiftHandedToServer;

    /// <summary>2026-11-01 (К-02, приёмка): RefreshAsync идёт параллельно (Новый чек, опрос шапки, закрытие смены) — открытие серверной смены только одним потоком.</summary>
    private static readonly SemaphoreSlim CatchUpGate = new(1, 1);

    /// <summary>В сохранённом состоянии записан ДРУГОЙ кассир, чем вошедший. Владелец неизвестен (старый файл) или не вошли — не считаем чужой.</summary>
    private static bool IsOfflineShiftOfOtherCashier(string offlineShiftId)
    {
        var saved = OfflinePosStateStore.TryLoad();
        var me = PosApp.CurrentUserId;
        return saved is not null
               && string.Equals(saved.ActiveShiftId, offlineShiftId, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(saved.CashierId) && !string.IsNullOrEmpty(me)
               && !string.Equals(saved.CashierId, me, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>2026-11-01 (К-02): связь есть, а у сервера нет открытой смены кассира, хотя на кассе идёт офлайн-смена: открываем серверную смену с той же
    /// начальной суммой (она сохранена в offline_pos_state.json вместе с id офлайн-смены) и возвращаем её id. Не чаще раза в минуту; любой сбой —
    /// null (офлайн-смена остаётся, ничего не теряется). «Открыть смену» у сервера идемпотентна: уже открытую смену кассира он вернул бы в списке выше.</summary>
    private async Task<string?> TryOpenServerShiftForOfflineAsync(string offlineShiftId, CancellationToken cancellationToken)
    {
        if (!CatchUpGate.Wait(0))
            return null; // другой поток уже открывает
        try
        {
        if (DateTime.UtcNow - _lastCatchUpUtc < TimeSpan.FromSeconds(60))
            return null;
        _lastCatchUpUtc = DateTime.UtcNow;

        var cashboxId = PosApp.PosCashboxId;
        var saved = OfflinePosStateStore.TryLoad();
        // 2026-11-01 (приёмка): открываем только кассиру, чья это смена (CashierId записан и равен вошедшему); неизвестный владелец — не открываем.
        // 2026-11-01 (code-review): сумма — именно ОТКРЫТИЯ офлайн-смены (OfflineOpeningCash), а не «текущий остаток» (ShiftCashBalance): его перезаписывают ответы
        // сервера, и серверная смена открылась бы завышенной суммой, а офлайн-наличные продажи при досылке посчитались бы дважды. Нет поля (старый файл) — не открываем.
        if (string.IsNullOrWhiteSpace(cashboxId)
            || saved is null
            || !string.Equals(saved.ActiveShiftId, offlineShiftId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(saved.CashierId)
            || !string.Equals(saved.CashierId, PosApp.CurrentUserId, StringComparison.OrdinalIgnoreCase)
            || saved.OfflineOpeningCash is null)
        {
            PosLogger.Log($"Смена: офлайн-смена {offlineShiftId} — сумма открытия или кассир-владелец в сохранённом состоянии не найдены/не совпадают, серверную смену автоматически не открываю.", "SHIFT");
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6) + ApiThrottle.RemainingBlock);
            var opening = saved.OfflineOpeningCash.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var response = await _shiftApi.ConstructionShiftOpenAsync(cashboxId, opening, timeoutCts.Token).ConfigureAwait(false);
            var newId = CartDisplayHelper.TryShiftIdFromOpenResponse(response);
            var openedCashbox = response.ValueKind == JsonValueKind.Object ? ShiftHelper.ReadCashboxId(response) : null;
            if (string.IsNullOrWhiteSpace(newId) || !Guid.TryParse(newId, out _)
                || (!string.IsNullOrWhiteSpace(openedCashbox) && !string.Equals(openedCashbox, cashboxId, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"Смена: для офлайн-смены {offlineShiftId} сервер вернул неподходящую смену (id={newId ?? "—"}, касса={openedCashbox ?? "—"}) — офлайн-смена остаётся.", "SHIFT");
                return null;
            }

            // 2026-11-01 (К2-01): «открыть смену» у сервера идемпотентна — если вчерашняя смена из очереди закрытия ещё открыта, он вернул её. Принимать нельзя.
            if (PendingShiftCloseStore.TryGet(newId) is not null)
            {
                PosLogger.Log($"Смена: сервер вернул для офлайн-смены {offlineShiftId} вчерашнюю смену {newId}, стоящую в очереди закрытия, — не принимаю (офлайн-смена остаётся, очередь закроет вчерашнюю).", "SHIFT");
                return null;
            }

            // 2026-11-01 (К2-02): пока шёл запрос, кассир мог закрыть офлайн-смену. Серверная смена уже открыта — не оставляем её висеть: закрытие в очередь (SyncService закроет её после досылки чеков).
            if (!string.Equals(PosApp.ActiveShiftId, offlineShiftId, StringComparison.OrdinalIgnoreCase))
            {
                PendingShiftCloseStore.Enqueue(newId, null, "офлайн-смена закрыта кассиром, пока передавалась серверу");
                PosLogger.Log($"Смена: офлайн-смена {offlineShiftId} закрыта, пока передавалась серверу, — открытая смена {newId} поставлена в очередь закрытия.", "SHIFT");
                return null;
            }

            PosLogger.Log($"Смена: офлайн-смена {offlineShiftId} передана серверу — открыта смена {newId} с начальной суммой {opening}; чеки и внесения офлайн-смены уйдут в неё.", "SHIFT");
            // 2026-11-01 (code-review): не молчаливый фон — кассир видит плашку (главное окно показывает её тостом).
            try
            {
                OfflineShiftHandedToServer?.Invoke(Tr.T(
                    $"Смена, открытая без связи, передана серверу (начальная сумма {opening}). Чеки и внесения за это время уйдут в неё.",
                    $"Байланышсыз ачылган смена серверге өткөрүлдү (баштапкы сумма {opening}). Ушул убакыттагы чектер жана киргизүүлөр ага түшөт.",
                    $"The shift opened without a connection was handed to the server (opening amount {opening}). Receipts and cash operations made meanwhile go into it.",
                    $"Bağlantısız açılan vardiya sunucuya aktarıldı (başlangıç tutarı {opening}). Bu sürede yapılan fişler ve para hareketleri ona işlenecek.",
                    $"Aloqasiz ochilgan smena serverga o'tkazildi (boshlang'ich summa {opening}). Shu vaqtdagi cheklar va kiritish/olishlar unga tushadi."));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена: уведомление о передаче офлайн-смены серверу не показано: {ex.Message}", "SHIFT");
            }

            return newId;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            PosLogger.Log($"Смена: открыть серверную смену для офлайн-смены {offlineShiftId} не вышло ({ex.GetType().Name}: {ex.Message}) — офлайн-смена остаётся, повтор через минуту.", "SHIFT");
            return null;
        }
        }
        finally
        {
            CatchUpGate.Release();
        }
    }

    // 2026-10-22 (аудит новой сборки, П1): сервер сам закрыл смену владельца в 23:00:00 (closed_at 23:00:00.254), а касса
    // продолжала показывать «Касса: 2 250,19» и «Закрыть смену» — продажи пошли бы в никуда. Раньше смену сверяли только
    // при запуске, после отказа в оплате и перед оплатой без смены. Здесь — само определение «сервер закрыл»; окно
    // (MainWindow) вызывает его раз в 1–2 минуты, при открытии оплаты/возврата/внесения и после возврата связи.
    // Ложное срабатывание хуже пропуска (кассир потеряет смену), поэтому: пустой список дважды подряд и карточка смены
    // с признаком закрытия. Любая ошибка сети — «не знаем», состояние не меняется.
    public async Task<ShiftClosedByServer?> CheckServerClosedAsync(CancellationToken cancellationToken = default)
    {
        var activeId = PosApp.ActiveShiftId;
        if (string.IsNullOrEmpty(activeId)
            || activeId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
            || _autonomous.IsCurrentSessionAutonomous
            || OfflineModeHelper.IsServerOutage)
            return null;

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var list = await _shiftApi.ConstructionShiftsListAsync(openOnly: true, ct: cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId, PosApp.CurrentUserId))
                    || ShiftHelper.FindCashierShiftOnOtherCashbox(list, PosApp.CurrentUserId) is not null)
                    return null; // смена на месте (или своя на другой кассе — её подхватит обычная сверка)
                if (!string.Equals(PosApp.ActiveShiftId, activeId, StringComparison.OrdinalIgnoreCase))
                    return null; // пока спрашивали, смену уже сменили (закрыл сам кассир)
            }

            using var detailCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            detailCts.CancelAfter(TimeSpan.FromSeconds(6));
            var row = await _shiftApi.ConstructionShiftGetAsync(activeId, detailCts.Token).ConfigureAwait(false);
            if (row.ValueKind != JsonValueKind.Object)
                return null;
            var status = row.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            DateTime? closedAt = null;
            if (row.TryGetProperty("closed_at", out var ca) && ca.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ca.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dto))
                closedAt = dto.LocalDateTime;
            var looksOpen = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase)
                            || (status is null && closedAt is null);
            if (looksOpen)
                return null;

            var totals = ShiftBalanceHelper.ReadShiftTotals(row);
            decimal? closing = null;
            if (row.TryGetProperty("closing_cash", out var cc))
            {
                if (cc.ValueKind == JsonValueKind.Number && cc.TryGetDecimal(out var d1)) closing = d1;
                else if (cc.ValueKind == JsonValueKind.String
                         && decimal.TryParse(cc.GetString(), System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var d2)) closing = d2;
            }

            return new ShiftClosedByServer
            {
                ShiftId = activeId,
                ClosedAt = closedAt,
                ExpectedCash = totals.ExpectedCash,
                ClosingCash = closing,
                TotalSales = totals.TotalSales,
                SalesCount = totals.SalesCount,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка смены с сервером не удалась (состояние не меняем): {ex.GetType().Name}: {ex.Message}", "SHIFT");
            return null;
        }
    }

    // 2026-10-22 (смена 23 ч): время открытия смены для предупреждения «сервер закроет сам». Один запрос карточки на смену,
    // дальше из памяти (ShiftAgeWarning). Офлайн-смена, автономный режим, сбой связи — null (предупреждения нет).
    public async Task<DateTime?> GetActiveShiftOpenedAtAsync(CancellationToken cancellationToken = default)
    {
        var activeId = PosApp.ActiveShiftId;
        if (string.IsNullOrEmpty(activeId) || activeId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
            || _autonomous.IsCurrentSessionAutonomous)
            return null;
        if (ShiftAgeWarning.Get(activeId) is { } known)
            return known;
        if (OfflineModeHelper.IsServerOutage)
            return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var row = await _shiftApi.ConstructionShiftGetAsync(activeId, cts.Token).ConfigureAwait(false);
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("opened_at", out var oa) && oa.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(oa.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dto))
            {
                ShiftAgeWarning.Remember(activeId, dto.LocalDateTime);
                return dto.LocalDateTime;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Время открытия смены не получено: {ex.GetType().Name}: {ex.Message}", "SHIFT");
        }
        return null;
    }

    /// <summary>2026-10-21 (пакет 5, п. 36): запасной остаток смены (offline_pos_state.json) — из КАЖДОГО ответа
    /// списка открытых смен, а не только из RefreshAsync. Остаток после возврата, внесения/изъятия и оплаты долга шапка
    /// кассы берёт отдельным запросом (MainWindow.RefreshShiftBalanceQuietAsync), и файл оставался со старым числом:
    /// пропала связь сразу после возврата — касса без сервера показывала остаток ДО возврата (живая проверка П3:
    /// −40 167,04 в файле при −40 187,04 на экране). Вызывается из ShiftApiService после запроса ?status=open.
    /// Без открытой смены и без остатка в ответе файл не трогается; одинаковое значение не переписывается.</summary>
    public static void RememberFallbackBalance(JsonElement openShifts)
    {
        try
        {
            if (string.IsNullOrEmpty(PosApp.ActiveShiftId)
                || ShiftBalanceHelper.FindOpenShiftBalance(openShifts, PosApp.PosCashboxId) is not { } balance)
                return;

            if (OfflinePosStateStore.TryLoad() is { } saved
                && saved.ShiftCashBalance == balance
                && string.Equals(saved.ActiveShiftId, PosApp.ActiveShiftId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(saved.PosCashboxId, PosApp.PosCashboxId, StringComparison.OrdinalIgnoreCase))
                return;

            OfflinePosStateStore.SaveFromApp(balance);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Запасной остаток смены не сохранён: {ex.GetType().Name}: {ex.Message}", "SHIFT");
        }
    }

    /// <summary>Кассу, которую кассир сам закрепил в настройках, смена с другой кассы не
    /// перебивает: он прямо сказал, какая это касса.</summary>
    private static bool CanFollowCashbox(string cashboxId)
    {
        var pinned = UserPreferences.Instance.PreferredCashboxId;
        return string.IsNullOrWhiteSpace(pinned)
            || string.Equals(pinned, cashboxId, StringComparison.OrdinalIgnoreCase);
    }
}
