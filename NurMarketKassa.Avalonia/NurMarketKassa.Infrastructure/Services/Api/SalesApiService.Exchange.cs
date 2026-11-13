using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-10-06, исследование «Кассы для одежды» (О-30, О-33): обмен одним документом и возврат с причиной
/// и отметкой «брак». Отдельный интерфейс, как <see cref="IPosQuickCheckoutApi"/>: заглушки общего
/// <see cref="ISalesApiService"/> в тестах этих методов не знают.</summary>
public interface IPosExchangeApi
{
    /// <summary>POST api/main/pos/sales/{id}/exchange/ (проверено на тестовом аккаунте 06.10):
    /// {"return_items": [{"item": id строки, "qty"}], "new_items": [{"product", "variant"?, "qty"}], "payment": {"method"}} →
    /// {"exchange", "original_sale", "new_sale", "new_sale_number", "return", "returned_amount", "new_amount", "difference"}.
    /// Возвращённое засчитывается в новый чек оплатой «offset» (зачёт), деньгами проходит только разница:
    /// difference &gt; 0 — доплата покупателя способом payment.method (cash или transfer; «card» сервер не принимает),
    /// &lt; 0 — сдача наличными из кассы («Сдача по обмену»). Цены новых товаров считает сервер.</summary>
    Task<JsonElement> PosExchangeAsync(
        string saleId,
        IReadOnlyList<(string LineId, double Quantity)> returnItems,
        IReadOnlyList<(string ProductId, string? VariantId, double Quantity)> newItems,
        string paymentMethod,
        CancellationToken ct = default,
        string? idempotencyKey = null);

    /// <summary>POST api/main/pos/sales/{id}/return/ с причиной и отметкой «брак» (проверено 06.10: сервер хранит
    /// reason в документе возврата, is_defect=true — товар не возвращается на склад, restock=false).
    /// <paramref name="lines"/> пуст или null — возвращается весь чек.</summary>
    Task<JsonElement> PosReturnWithDetailsAsync(
        string saleId,
        IReadOnlyList<PosRefundLineRequest>? lines,
        string? reason,
        bool isDefect,
        CancellationToken ct = default,
        string? idempotencyKey = null);
}

public sealed partial class SalesApiService : IPosExchangeApi
{
    public async Task<JsonElement> PosExchangeAsync(
        string saleId,
        IReadOnlyList<(string LineId, double Quantity)> returnItems,
        IReadOnlyList<(string ProductId, string? VariantId, double Quantity)> newItems,
        string paymentMethod,
        CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);

        // 2026-10-15, ревью: ключ передаёт окно обмена — тот же при повторе того же обмена после таймаута.
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey;
        var body = new Dictionary<string, object>
        {
            ["return_items"] = returnItems
                .Select(r => new Dictionary<string, object> { ["item"] = r.LineId.Trim(), ["qty"] = FormatRefundQty(r.Quantity) })
                .ToList(),
            ["new_items"] = newItems
                .Select(n =>
                {
                    var row = new Dictionary<string, object> { ["product"] = n.ProductId.Trim(), ["qty"] = FormatRefundQty(n.Quantity) };
                    if (!string.IsNullOrWhiteSpace(n.VariantId))
                        row["variant"] = n.VariantId!.Trim();
                    return row;
                })
                .ToList(),
            ["payment"] = new Dictionary<string, object> { ["method"] = string.IsNullOrWhiteSpace(paymentMethod) ? "cash" : paymentMethod },
            // Сервер пока ключ не проверяет (06.10) — шлём на будущее, как в остальных денежных запросах.
            ["idempotency_key"] = key,
        };

        using var critical = ApiThrottle.BeginCritical(); // 2026-11-03 (Д3-02): обмен — деньги, идёт в быструю полосу очереди (как оплата), а не за каталогом
        return await _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{Uri.EscapeDataString(id)}/exchange/",
            body,
            null,
            ct,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public async Task<JsonElement> PosReturnWithDetailsAsync(
        string saleId,
        IReadOnlyList<PosRefundLineRequest>? lines,
        string? reason,
        bool isDefect,
        CancellationToken ct = default,
        string? idempotencyKey = null)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);

        var body = new Dictionary<string, object>
        {
            // 2026-10-15, ревью: при повторе того же возврата — тот же ключ (см. SalesApiService.PosReturnSaleAsync).
            ["idempotency_key"] = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey,
            ["cashbox_role"] = "pos_main",
        };
        if (!string.IsNullOrWhiteSpace(reason))
            body["reason"] = reason.Trim();
        if (isDefect)
            body["is_defect"] = true;
        if (lines is { Count: > 0 })
        {
            body["items"] = lines
                .Select(l => RefundItem(l))
                .ToList();
        }

        using var critical = ApiThrottle.BeginCritical(); // 2026-11-03 (Д3-02): возврат — деньги, быстрая полоса очереди
        return await _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{Uri.EscapeDataString(id)}/return/",
            body,
            null,
            ct).ConfigureAwait(false);
    }
}
