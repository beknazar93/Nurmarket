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
        if (_autonomous.IsCurrentSessionAutonomous)
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
            var openId = ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId);
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
}
