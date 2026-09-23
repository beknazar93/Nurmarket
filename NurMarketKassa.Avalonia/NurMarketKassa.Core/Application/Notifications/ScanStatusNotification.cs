using MediatR;

namespace NurMarketKassa.Core.Application.Notifications;

public sealed record ScanStatusNotification(
    string Message,
    ScanStatusLevel Level,
    bool ClearBarcode = false,
    bool ShowToast = false,
    bool ToastWarning = false) : INotification;
