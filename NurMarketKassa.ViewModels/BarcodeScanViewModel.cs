using MediatR;
using NurMarketKassa.Core.Application.Commands;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Scanning;

namespace NurMarketKassa.ViewModels;

/// <summary>Cross-platform barcode scan orchestration (parse, lookup, dispatch).</summary>
public sealed class BarcodeScanViewModel : ViewModelBase
{
    private readonly IMediator _mediator;
    private readonly IDispatcher _dispatcher;
    private readonly IAppSession _session;
    private readonly IProductCatalogLookup _catalogLookup;

    private string _barcodeBuffer = "";
    private bool _isProcessingScan;
    private string _scanErrorMessage = "";

    public BarcodeScanViewModel(
        IMediator mediator,
        IDispatcher dispatcher,
        IAppSession session,
        IProductCatalogLookup catalogLookup)
    {
        _mediator = mediator;
        _dispatcher = dispatcher;
        _session = session;
        _catalogLookup = catalogLookup;
    }

    public string BarcodeBuffer
    {
        get => _barcodeBuffer;
        private set => SetProperty(ref _barcodeBuffer, value ?? "");
    }

    public bool IsProcessingScan
    {
        get => _isProcessingScan;
        private set => SetProperty(ref _isProcessingScan, value);
    }

    public string ScanErrorMessage
    {
        get => _scanErrorMessage;
        private set => SetProperty(ref _scanErrorMessage, value ?? "");
    }

    /// <summary>Raised when a product is resolved and ready to be added to the cart.</summary>
    public event Action<ScannedProductFoundEventArgs>? ProductFound;

    public void ClearBarcodeBuffer() => BarcodeBuffer = "";

    public void AppendBarcodeCharacter(char character)
    {
        if (character == '\0')
            return;
        BarcodeBuffer += character;
    }

    public async Task<bool> ProcessBarcodeScanAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (IsProcessingScan)
            return false;

        var raw = (barcode ?? "").Trim();
        BarcodeBuffer = raw;
        if (raw.Length == 0)
        {
            ScanErrorMessage = "";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_session.CurrentUserId))
        {
            ScanErrorMessage = "Выполните вход в кассу.";
            return false;
        }

        IsProcessingScan = true;
        ScanErrorMessage = "";
        try
        {
            CatalogProductInfo? product;
            decimal? weightKg = null;
            var isWeighedBarcode = WeightBarcodeParseHelper.TryParse(raw, out var parsed);
            var lookupCode = isWeighedBarcode ? parsed.ProductCode : raw;

            if (isWeighedBarcode)
            {
                weightKg = parsed.WeightKg;
                product = _catalogLookup.FindByEmbeddedCode(lookupCode);
            }
            else
            {
                product = _catalogLookup.FindByBarcode(raw);
            }

            if (product == null)
            {
                ScanErrorMessage = isWeighedBarcode
                    ? $"Товар с кодом {lookupCode} не найден в каталоге."
                    : "У вас нет такого товара в базе.";
                return false;
            }

            if (product.MustWeigh && !isWeighedBarcode)
                weightKg = null;

            var args = new ScannedProductFoundEventArgs(
                product,
                raw,
                lookupCode,
                isWeighedBarcode,
                weightKg);

            await _dispatcher.InvokeAsync(() => ProductFound?.Invoke(args)).ConfigureAwait(false);
            BarcodeBuffer = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScanErrorMessage = string.IsNullOrWhiteSpace(ex.Message)
                ? "Ошибка при обработке штрих-кода."
                : ex.Message;
            return false;
        }
        finally
        {
            IsProcessingScan = false;
        }
    }

    /// <summary>
    /// Full POS pipeline: session checks, scale prompts, and cart mutation via <see cref="ProcessBarcodeCommand"/>.
    /// </summary>
    public async Task<bool> ProcessBarcodeViaMediatorAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        if (IsProcessingScan)
            return false;

        var raw = (barcode ?? "").Trim();
        BarcodeBuffer = raw;
        if (raw.Length == 0)
            return false;

        IsProcessingScan = true;
        ScanErrorMessage = "";
        try
        {
            var success = await _mediator
                .Send(new ProcessBarcodeCommand(raw), cancellationToken)
                .ConfigureAwait(false);

            if (!success && string.IsNullOrWhiteSpace(ScanErrorMessage))
                ScanErrorMessage = "Не удалось обработать штрих-код.";

            if (success)
                BarcodeBuffer = "";

            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScanErrorMessage = string.IsNullOrWhiteSpace(ex.Message)
                ? "Ошибка при обработке штрих-кода."
                : ex.Message;
            return false;
        }
        finally
        {
            IsProcessingScan = false;
        }
    }
}
