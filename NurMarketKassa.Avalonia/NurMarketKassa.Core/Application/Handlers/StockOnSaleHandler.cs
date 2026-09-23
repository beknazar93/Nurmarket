using MediatR;
using NurMarketKassa.Core.Application.Notifications;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application.Handlers;

public sealed class StockOnSaleHandler : INotificationHandler<SaleFinalizedNotification>
{
    private readonly IStockService _stockService;
    private readonly IStockAuditWriter _auditWriter;

    public StockOnSaleHandler(IStockService stockService, IStockAuditWriter auditWriter)
    {
        _stockService = stockService;
        _auditWriter = auditWriter;
    }

    public async Task Handle(SaleFinalizedNotification notification, CancellationToken cancellationToken)
    {
        foreach (var line in notification.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0)
                continue;

            await _stockService.CommitSaleAsync(
                notification.SaleId,
                line.ProductId,
                line.Quantity,
                cancellationToken).ConfigureAwait(false);

            _auditWriter.LogStock(line.ProductId, line.Quantity, "sale");
        }
    }
}
