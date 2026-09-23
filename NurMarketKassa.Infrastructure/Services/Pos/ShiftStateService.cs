using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

public sealed class ShiftStateService : IShiftStateService
{
    private readonly IShiftApiService _shiftApi;

    public ShiftStateService(IShiftApiService shiftApi) => _shiftApi = shiftApi;

    public event Action<ShiftStateSnapshot>? StateRefreshed;

    public string? ActiveShiftId => PosApp.ActiveShiftId;

    public bool IsShiftOpen => !string.IsNullOrEmpty(PosApp.ActiveShiftId);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        decimal? balance = null;

        if (OfflineModeHelper.UseLocalOperations)
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
            var list = await _shiftApi.ConstructionShiftsListAsync(cancellationToken).ConfigureAwait(false);
            var openId = ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId);
            PosApp.ActiveShiftId = string.IsNullOrEmpty(openId) ? null : openId;
            balance = ShiftBalanceHelper.FindOpenShiftBalance(list, PosApp.PosCashboxId);
            OfflinePosStateStore.SaveFromApp(balance ?? 0m);
        }
        catch
        {
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
