using MediatR;
using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Application.Notifications;

public sealed record SaleFinalizedNotification(string SaleId, IReadOnlyList<CartLineDto> Lines) : INotification;
