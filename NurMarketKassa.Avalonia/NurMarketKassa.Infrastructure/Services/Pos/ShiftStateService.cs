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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            var list = await _shiftApi.ConstructionShiftsListAsync(openOnly: true, ct: timeoutCts.Token).ConfigureAwait(false);
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
                PosApp.PosCashboxId = own.CashboxId;
                if (!string.IsNullOrWhiteSpace(own.CashboxName))
                    PosApp.PosCashboxDisplayName = own.CashboxName;
                openId = own.ShiftId;
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
        }
        catch (Exception ex)
        {
            // 2026-09-12: раньше здесь не было лога — при живой отладке "разбивка не
            // отображается" не было видно, срабатывает ли вообще этот откат и почему.
            PosLogger.Log($"Shift balance refresh failed/timed out: {ex.GetType().Name}: {ex.Message}", "SHIFT");
            OfflinePosStateStore.RestoreToApp();
            balance = OfflinePosStateStore.ReadShiftCashBalance();
        }

        StateRefreshed?.Invoke(new ShiftStateSnapshot
        {
            ActiveShiftId = PosApp.ActiveShiftId,
            CashBalance = balance,
        });
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
