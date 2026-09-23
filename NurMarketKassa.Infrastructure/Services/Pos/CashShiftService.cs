using System.Globalization;
using System.Net.Http;
using System.Text;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

public sealed class CashShiftService : ICashShiftService
{
    private readonly IShiftApiService _shiftApi;
    private readonly IShiftStateService _shiftStateService;
    private readonly MySqlAuditService _auditDb;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IOfflinePosStateStore _offlinePosStateStore;

    public CashShiftService(
        IShiftApiService shiftApi,
        IShiftStateService shiftStateService,
        MySqlAuditService auditDb,
        IReceiptPrinterService receiptPrinter,
        IOfflinePosStateStore offlinePosStateStore)
    {
        _shiftApi = shiftApi;
        _shiftStateService = shiftStateService;
        _auditDb = auditDb;
        _receiptPrinter = receiptPrinter;
        _offlinePosStateStore = offlinePosStateStore;
    }

    public async Task<CashShiftOperationResult> OpenShiftAsync(decimal openingCash, CancellationToken cancellationToken = default)
    {
        if (OfflineModeHelper.UseLocalOperations)
        {
            PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            return CashShiftOperationResult.Success(openingCash, isOffline: true, infoMessage: "Смена открыта офлайн.");
        }

        var cashboxId = await EnsurePosCashboxIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cashboxId))
            return CashShiftOperationResult.Failed("Не удалось определить кассу.");

        var opening = openingCash.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            var response = await _shiftApi
                .ConstructionShiftOpenAsync(cashboxId, opening, cancellationToken)
                .ConfigureAwait(false);

            var shiftId = CartDisplayHelper.TryShiftIdFromOpenResponse(response);
            if (!string.IsNullOrWhiteSpace(shiftId))
                PosApp.ActiveShiftId = shiftId;
            else
                await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);

            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            _auditDb.LogShift("open", openingCash, PosApp.ActiveShiftId);
            return CashShiftOperationResult.Success(openingCash);
        }
        catch (HttpRequestException ex)
        {
            PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            return CashShiftOperationResult.Success(
                openingCash,
                isOffline: true,
                infoMessage: $"Смена открыта офлайн ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return CashShiftOperationResult.Failed("Ошибка открытия смены: " + ex.Message);
        }
    }

    public async Task<CashShiftOperationResult> CloseShiftAsync(decimal? closingCash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return CashShiftOperationResult.Failed("Смена уже закрыта.");

        var shiftId = PosApp.ActiveShiftId;
        var closing = closingCash?.ToString("0.00", CultureInfo.InvariantCulture);

        if (OfflineModeHelper.UseLocalOperations)
        {
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close_offline", closingCash, shiftId);
            return CashShiftOperationResult.Success(closingCash, isOffline: true);
        }

        try
        {
            await _shiftApi
                .ConstructionShiftCloseAsync(shiftId!, closing, cancellationToken)
                .ConfigureAwait(false);

            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            // A successful close is authoritative. An immediate list refresh may
            // still return the closed shift or restore it from the offline cache.
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close", closingCash, shiftId);
            return CashShiftOperationResult.Success(closingCash);
        }
        catch (Exception ex)
        {
            return CashShiftOperationResult.Failed("Ошибка закрытия смены: " + ex.Message);
        }
    }

    public async Task<CashShiftReportResult> GenerateXReportAsync(decimal? currentBalance, CancellationToken cancellationToken = default)
    {
        var report = BuildReportText("X", PosApp.ActiveShiftId, currentBalance);
        return new CashShiftReportResult(report, await PrintReportAsync(report, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CashShiftReportResult> GenerateZReportAsync(decimal? closingCash, CancellationToken cancellationToken = default)
    {
        var report = BuildReportText("Z", PosApp.ActiveShiftId, closingCash);
        return new CashShiftReportResult(report, await PrintReportAsync(report, cancellationToken).ConfigureAwait(false));
    }

    public Task<bool> PrintReportAsync(string reportText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportText))
            return Task.FromResult(false);

        return _receiptPrinter.PrintReceiptAsync(
            new CartSnapshot
            {
                CartJson = "{}",
                ReceiptText = reportText,
                OfflineNote = "SHIFT REPORT",
                PaymentMethodKey = null,
                CashReceived = null,
            },
            cancellationToken);
    }

    private async Task<string?> EnsurePosCashboxIdAsync(CancellationToken cancellationToken)
    {
        var current = PosApp.PosCashboxId;
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);
        if (!CartDisplayHelper.TryFirstCashbox(rawList, out var id, out var displayName))
            return null;

        PosApp.PosCashboxId = id;
        PosApp.PosCashboxDisplayName = displayName;
        return id;
    }

    private static string BuildReportText(string reportType, string? shiftId, decimal? cash)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"*** {reportType}-ОТЧЕТ ***");
        sb.AppendLine($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Кассир: {PosApp.CurrentUserId ?? "—"}");
        sb.AppendLine($"Смена: {shiftId ?? "—"}");
        sb.AppendLine($"Остаток: {(cash ?? 0m):0.00} сом");
        sb.AppendLine("------------------------------");
        sb.AppendLine("NurMarket Kassa");
        return sb.ToString();
    }
}
