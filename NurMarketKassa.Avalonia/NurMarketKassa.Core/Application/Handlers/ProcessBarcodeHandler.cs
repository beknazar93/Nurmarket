using MediatR;
using NurMarketKassa.Core.Application.Commands;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application.Handlers;

public sealed class ProcessBarcodeHandler : IRequestHandler<ProcessBarcodeCommand, bool>
{
    private readonly IPosSessionService _posSessionService;
    private readonly IPosBarcodeScanner _barcodeScanner;
    private readonly IUserPrompts _userPrompts;

    public ProcessBarcodeHandler(
        IPosSessionService posSessionService,
        IPosBarcodeScanner barcodeScanner,
        IUserPrompts userPrompts)
    {
        _posSessionService = posSessionService;
        _barcodeScanner = barcodeScanner;
        _userPrompts = userPrompts;
    }

    public async Task<bool> Handle(ProcessBarcodeCommand request, CancellationToken cancellationToken)
    {
        var code = (request.Barcode ?? "").Trim();
        if (code.Length == 0)
            return false;

        try
        {
            if (!await _posSessionService.EnsureOperationalAsync(cancellationToken).ConfigureAwait(false))
                return false;

            return await _barcodeScanner.ScanAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _userPrompts.ShowWarning(
                    "Превышено время ожидания ответа от оборудования или сервера. Попробуйте ещё раз.");
            }

            return false;
        }
        catch (Exception ex)
        {
            _userPrompts.ShowError($"Ошибка при обработке штрих-кода: {ex.Message}");
            return false;
        }
    }
}
