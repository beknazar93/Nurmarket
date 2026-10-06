using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «долг со сроком; срок возврата — тот, что указал клиент»; «рассрочка как в вебе».
/// Кассир выбирает в окне оплаты срок (одним платежом) или рассрочку по дням / месяцам. Сервер NurCRM при продаже одним
/// запросом (pos/checkout/) график не принимает и ставит 1 платёж через 30 дней (проверено 06.10: debt_schedule в теле —
/// и сверху, и внутри payment — игнорируется), поэтому после продажи касса меняет график сделки долга:
/// PATCH api/main/clients/{client}/deals/{deal}/
///   по дням:    {"debt_days": N, "interval_days": K, "debt_months": null, "first_due_date": "ГГГГ-ММ-ДД"}
///   по месяцам: {"debt_months": N, "interval_months": K, "debt_days": null, "first_due_date": "ГГГГ-ММ-ДД"}
/// Сервер пересобирает взносы сам (проверено 06.10 на тестовом аккаунте: 2 платежа раз в неделю — 13.10 и 20.10;
/// 3 платежа по месяцам — 06.11, 06.12, 06.01, суммы 0,66 + 0,66 + 0,68). Одним платежом — N = 1.
/// График печатается в чеке (CartReceiptTextBuilder, поля снимка debt_due_date / debt_schedule).</summary>
public static class DebtDueDateSync
{
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    private static NurMarketApiClient? Api => ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    /// <summary>Поставить график долга продажи. Ошибки — только в журнал (продажа уже проведена).</summary>
    public static async Task ApplyAsync(string clientId, string saleId, DebtSchedulePlan plan)
    {
        if (Api is not { } api || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(saleId))
            return;
        var body = new Dictionary<string, object?>
        {
            ["first_due_date"] = plan.FirstDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (plan.IsMonths)
        {
            body["debt_months"] = plan.Count;
            body["interval_months"] = plan.Interval;
            body["debt_days"] = null;
        }
        else
        {
            body["debt_days"] = plan.Count;
            body["interval_days"] = plan.Interval;
            body["debt_months"] = null;
        }
        var what = plan.Count == 1
            ? $"срок {plan.FirstDueDate:dd.MM.yyyy}"
            : $"рассрочка {plan.Count} плат. ({(plan.IsMonths ? "мес." : "дн.")} × {plan.Interval}) с {plan.FirstDueDate:dd.MM.yyyy}";
        try
        {
            // Сделка долга появляется вместе с продажей; на случай задержки — до 3 попыток.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var data = await api.RequestAsync(HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(clientId)}/deals/", null, null,
                    CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var rows = data.ValueKind == JsonValueKind.Array ? data : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r : default;
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var deal in rows.EnumerateArray())
                    {
                        if (Str(deal, "sale") != saleId && Str(deal, "sale_id") != saleId)
                            continue;
                        await api.RequestAsync(new HttpMethod("PATCH"),
                            $"api/main/clients/{Uri.EscapeDataString(clientId)}/deals/{Uri.EscapeDataString(Str(deal, "id"))}/",
                            body, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                        PosLogger.Log($"Долг продажи {saleId}: {what} — на сервере.", "PAYMENT");
                        return;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
            }
            PosLogger.Log($"Долг продажи {saleId}: сделка не найдена — {what} не записан.", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Долг продажи {saleId}: {what} не записан ({ex.Message}).", "WARNING");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
}
