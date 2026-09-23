using System.Globalization;
using MediatR;
using NurMarketKassa.Core.Application.Notifications;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Application;

public sealed class PosBarcodeScannerService : IPosBarcodeScanner
{
    private static readonly TimeSpan ScaleReadTimeout = TimeSpan.FromSeconds(2);

    private readonly IPosCartGateway _cartGateway;
    private readonly IProductCatalogLookup _catalog;
    private readonly IScaleWeightProvider _scale;
    private readonly IWeightInputPrompt _weightPrompt;
    private readonly IMediator _mediator;

    public PosBarcodeScannerService(
        IPosCartGateway cartGateway,
        IProductCatalogLookup catalog,
        IScaleWeightProvider scale,
        IWeightInputPrompt weightPrompt,
        IMediator mediator)
    {
        _cartGateway = cartGateway;
        _catalog = catalog;
        _scale = scale;
        _weightPrompt = weightPrompt;
        _mediator = mediator;
    }

    public async Task<bool> ScanAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var code = (barcode ?? "").Trim();
        if (code.Length == 0)
            return false;

        await _mediator.Publish(new ScanBusyNotification(true), cancellationToken).ConfigureAwait(false);
        try
        {
            await _mediator.Publish(
                new ScanStatusNotification("", ScanStatusLevel.Info),
                cancellationToken).ConfigureAwait(false);

            if (!await _cartGateway.EnsureSaleSessionAsync(cancellationToken).ConfigureAwait(false))
                return false;

            if (WeightBarcodeParser.TryParse(code, out var weighted))
                return await ScanEmbeddedWeightAsync(weighted, cancellationToken).ConfigureAwait(false);

            var catalogProduct = _catalog.FindByBarcode(code);
            if (catalogProduct?.MustWeigh == true)
                return await ScanMustWeighProductAsync(catalogProduct, cancellationToken).ConfigureAwait(false);

            return await ScanPieceAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (CartOperationException ex)
        {
            await PublishWarningAsync(ex.Message, cancellationToken, showToast: true).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            try
            {
                await _mediator.Publish(new ScanBusyNotification(false), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Сброс индикатора сканирования не должен маскировать исходную ошибку.
            }
        }
    }

    private async Task<bool> ScanEmbeddedWeightAsync(
        Domain.WeightBarcodeParseResult weighted,
        CancellationToken cancellationToken)
    {
        var product = _catalog.FindByEmbeddedCode(weighted.ProductCode);
        if (product == null)
        {
            await PublishWarningAsync(
                $"Товар с кодом {weighted.ProductCode} не найден в каталоге.",
                cancellationToken,
                showToast: true).ConfigureAwait(false);
            return false;
        }

        var qty = FormatQuantity(weighted.WeightKg);
        if (!await _cartGateway.AddProductAsync(product.Id, qty, cancellationToken).ConfigureAwait(false))
        {
            await PublishWarningAsync(
                "Не удалось добавить весовой товар после повтора.",
                cancellationToken,
                showToast: true).ConfigureAwait(false);
            return false;
        }

        await PublishSuccessAsync(
            $"Добавлено {qty} кг",
            cancellationToken,
            showToast: true).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ScanMustWeighProductAsync(
        Domain.CatalogProductInfo product,
        CancellationToken cancellationToken)
    {
        var weight = await _scale.TryReadWeightAsync(ScaleReadTimeout, cancellationToken).ConfigureAwait(false);
        if (weight is null or <= 0)
            weight = await _weightPrompt.PromptWeightKgAsync(product.Title, cancellationToken).ConfigureAwait(false);

        if (weight is null or <= 0)
        {
            await PublishWarningAsync("Вес не указан.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var qty = FormatQuantity(weight.Value);
        if (!await _cartGateway.AddProductAsync(product.Id, qty, cancellationToken).ConfigureAwait(false))
        {
            await PublishWarningAsync(
                "Не удалось добавить весовой товар после повтора.",
                cancellationToken,
                showToast: true).ConfigureAwait(false);
            return false;
        }

        await PublishSuccessAsync(
            $"Добавлено {qty} кг",
            cancellationToken,
            showToast: true).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ScanPieceAsync(string code, CancellationToken cancellationToken)
    {
        if (!await _cartGateway.ScanBarcodeAsync(code, null, cancellationToken).ConfigureAwait(false))
        {
            await PublishWarningAsync(
                "Не удалось отсканировать после повтора.",
                cancellationToken,
                showToast: true).ConfigureAwait(false);
            return false;
        }

        await PublishSuccessAsync("Товар добавлен.", cancellationToken, clearBarcode: true).ConfigureAwait(false);
        return true;
    }

    private async Task PublishSuccessAsync(
        string message,
        CancellationToken cancellationToken,
        bool clearBarcode = false,
        bool showToast = false)
    {
        await _mediator.Publish(new CartUpdatedNotification(), cancellationToken).ConfigureAwait(false);
        await _mediator.Publish(
            new ScanStatusNotification(message, ScanStatusLevel.Success, clearBarcode, showToast),
            cancellationToken).ConfigureAwait(false);
    }

    private Task PublishWarningAsync(
        string message,
        CancellationToken cancellationToken,
        bool showToast = false) =>
        _mediator.Publish(
            new ScanStatusNotification(message, ScanStatusLevel.Warning, ShowToast: showToast, ToastWarning: true),
            cancellationToken);

    private static string FormatQuantity(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
