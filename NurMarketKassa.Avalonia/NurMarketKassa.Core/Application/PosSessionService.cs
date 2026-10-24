using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application;

public sealed class PosSessionService : IPosSessionService
{
    private readonly IShiftStateService _shiftState;
    private readonly IUserPrompts _userPrompts;
    private readonly IShiftOpenCoordinator _shiftOpenCoordinator;

    public PosSessionService(
        IShiftStateService shiftState,
        IUserPrompts userPrompts,
        IShiftOpenCoordinator shiftOpenCoordinator)
    {
        _shiftState = shiftState;
        _userPrompts = userPrompts;
        _shiftOpenCoordinator = shiftOpenCoordinator;
    }

    public async Task<bool> EnsureOperationalAsync(CancellationToken cancellationToken = default, bool silent = false)
    {
        await _shiftState.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (_shiftState.IsShiftOpen)
            return true;

        if (!silent)
        {
            var shouldTryOpen = await _userPrompts.ConfirmAsync(
                // 2026-10-22 (аудит новой сборки, П1): сервер закрыл смену сам — говорим об этом прямо.
                (ServerShiftClosedState.Current is { } closedByServer
                    ? ServerShiftClosedState.BlockText(closedByServer)
                    : "Смена не открыта на этой кассе.") + "\n\nОткрыть смену сейчас?",
                // 2026-10-22 (модалки, пакет 1): глагол вместо «Продолжить». В Core нет Tr (переводов) — по-русски,
                // как и сам вопрос.
                "Открыть смену").ConfigureAwait(false);

            if (shouldTryOpen)
            {
                if (await _shiftOpenCoordinator.TryOpenShiftAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _shiftState.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    if (_shiftState.IsShiftOpen)
                        return true;
                }
            }

            _userPrompts.ShowError(
                "Смена не открыта на этой кассе.\n\n" +
                "Нажмите «Открыть смену» в шапке окна (при необходимости укажите остаток в кассе).\n\n" +
                "Без открытой смены эта операция недоступна.");
        }

        return false;
    }
}
