using MediatR;

namespace NurMarketKassa.Core.Application.Notifications;

public sealed record ScanBusyNotification(bool IsBusy) : INotification;
