using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-20, этап П3: общий вызов «записи долга» поставщику для приёмки склада и закупки по накладной (SupplierDebtRecorder)
/// и право на кнопку «Записать долг» в «Поставщиках».</summary>
internal static class SupplierDebtUi
{
    /// <summary>После прихода «в долг»: записать долг. Исключений не бросает (приход уже проведён) — непредвиденная ошибка = «не записан».</summary>
    public static async Task<SupplierDebtResult?> RecordAfterReceiptAsync(PurchaseReceivingService.Supplier supplier, PurchaseReceivingService.PostResult result,
        int dueDays, string source)
    {
        try
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            return await SupplierDebtRecorder.RecordAfterReceiptAsync(api, supplier, result, dueDays, source, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долг поставщику {supplier.Id} после прихода {result.ReceiptId}: запись упала ({ex.GetType().Name}: {ex.Message}).", "ERROR");
            return new SupplierDebtResult(SupplierDebtResultKind.Failed, null, result.ReceiptTotal ?? result.TotalAmount, ex.Message);
        }
    }

    /// <summary>«Записать долг» (повтор очереди и старые закупки) — только владелец или администратор (роль из профиля NurCRM).</summary>
    public static bool CanRecordDebts =>
        App.AuthApi?.UserPayload is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
        && r.GetString() is { } role
        && (string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase) || string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));
}
