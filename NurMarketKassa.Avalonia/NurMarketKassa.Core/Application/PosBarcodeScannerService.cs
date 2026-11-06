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

            // 2026-09-12: прямое совпадение по каталогу проверяем ПЕРВЫМ (как в
            // BasketPanelViewModel.AddByBarcodeAsync, см. её комментарий) — у любого настоящего
            // EAN-13 штрих-кода, начинающегося с "2", контрольная сумма технически валидна (это
            // свойство всех корректно сгенерированных штрих-кодов, не только весовых). Если
            // сначала пробовать распознать код как весовой, штучный товар с barcode,
            // начинающимся на "2", никогда не находился бы своим настоящим MustWeigh=false из
            // Склада — "успешно" срабатывала бы весовая расшифровка раньше прямого поиска
            // (реальный баг: такой товар всегда добавлялся в кг). Весовые же коды с весов
            // никогда не совпадают ни с одним товаром напрямую (каждое взвешивание уникально),
            // так что они всё равно корректно попадут в ветку ниже.
            var catalogProduct = _catalog.FindByBarcode(code);
            if (catalogProduct != null)
            {
                return catalogProduct.MustWeigh == true
                    ? await ScanMustWeighProductAsync(catalogProduct, cancellationToken).ConfigureAwait(false)
                    : await ScanPieceAsync(code, cancellationToken).ConfigureAwait(false);
            }

            if (WeightBarcodeParser.TryParse(code, out var weighted))
                return await ScanEmbeddedWeightAsync(weighted, cancellationToken).ConfigureAwait(false);

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

    /// <summary>2026-11-06: выбор текста по языку интерфейса (ru, ky, en, tr, uz). Core не ссылается на Tr из Infrastructure; хост может подставить Tr.T. По умолчанию — русский.</summary>
    public static Func<string, string, string, string, string, string> Localizer { get; set; } = static (ru, _, _, _, _) => ru;

    private async Task<bool> ScanEmbeddedWeightAsync(
        Domain.WeightBarcodeParseResult weighted,
        CancellationToken cancellationToken)
    {
        var product = _catalog.FindByEmbeddedCode(weighted.ProductCode);
        if (product == null)
        {
            // 2026-11-06 (весы РС-200): текст весового PLU на 5 языках; Core не знает Tr — язык подставляет Localizer (по умолчанию русский).
            await PublishWarningAsync(
                Localizer(
                    $"Весовой товар с PLU {weighted.ProductCode} не найден. Проверьте PLU товара в NurCRM.",
                    $"PLU {weighted.ProductCode} болгон салмактуу товар табылган жок. NurCRM'де товардын PLU кодун текшериңиз.",
                    $"Weighed product with PLU {weighted.ProductCode} was not found. Check the product's PLU in NurCRM.",
                    $"PLU {weighted.ProductCode} olan tartılı ürün bulunamadı. Ürünün PLU kodunu NurCRM'de kontrol edin.",
                    $"PLU {weighted.ProductCode} bo'lgan tarozi mahsuloti topilmadi. Mahsulotning PLU kodini NurCRM'da tekshiring."),
                cancellationToken,
                showToast: true).ConfigureAwait(false);
            return false;
        }

        var qty = FormatQuantity(weighted.ResolveWeightKg(0));
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
