using System.Diagnostics;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28, BE-11: досылка офлайн-чека одним запросом POST api/main/pos/checkout/ с
/// Idempotency-Key = id записи очереди. Старая досылка (sales/start → позиции → checkout)
/// защищалась от двойного чека сверкой корзины и при «не удалось проверить» откладывала чек;
/// с ключом повтор безопасен сам по себе: проведённую продажу сервер вернёт ещё раз (200,
/// "replayed": true), а не создаст вторую.
/// </summary>
public sealed partial class SyncService
{
    /// <summary>Handled=false — чек идёт старым путём (быстрый выключен, чек ему не подходит или
    /// сервер отказал по существу 4xx — значит, продажи с этим ключом нет). Ошибки связи и 5xx
    /// пробрасываются как есть: SyncBatchAsync оставит чек в очереди, следующий цикл пошлёт тот же ключ.</summary>
    private async Task<(bool Handled, string? SaleId)> TryQuickReplayAsync(OfflineSaleEntry entry, CancellationToken ct)
    {
        // Чек, который старый путь уже начал проводить (есть корзина/отметка отправки), доводит
        // старый путь — у него своя сверка по корзине.
        if (entry.CheckoutCompleted
            || entry.CheckoutSubmittedAt is not null
            || !string.IsNullOrWhiteSpace(entry.SyncCartId))
            return (false, null);

        // Если чек уже уходил этим адресом (ответ мог потеряться), дослать его можно только им же —
        // даже при выключенном в настройках быстром пути.
        if (!entry.QuickCheckoutAttempted && !UserPreferences.Instance.QuickCheckoutEnabled)
            return (false, null);

        if (_sales is not IPosQuickCheckoutApi quickApi)
            return (false, null);

        // 2026-10-06: сбор тела вынесен в BuildQuickReplayBody — им же пользуется пакетная досылка (SyncService.BatchReplay.cs).
        var body = BuildQuickReplayBody(entry);
        if (body == null)
            return (false, null);

        // Отметка ДО запроса и переживает перезапуск: следующий цикл пошлёт тот же ключ.
        OfflinePendingSalesStore.Update(entry.Id, e => e.QuickCheckoutAttempted = true);

        var sw = Stopwatch.StartNew();
        JsonElement response = default;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                response = await quickApi.PosQuickCheckoutAsync(body, entry.Id, TimeSpan.FromSeconds(45), ct)
                    .ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // SyncBatchAsync ловит именно TaskCanceledException: чек остаётся в очереди.
                throw new TaskCanceledException("Таймаут досылки чека.");
            }
            catch (ApiException ex) when (ex.StatusCode == 500 && attempt == 1)
            {
                // Один повтор тем же ключом: прошедшую продажу он вернёт как повтор (200).
                PosLogger.Log($"OFFLINE replay quick: чек {entry.Id} — 500, повтор тем же ключом.", "OFFLINE");
            }
            // 2026-10-04: 409/423/425 — «запрос с этим ключом ещё выполняется» (первая отправка ещё
            // обрабатывается сервером): это не отказ по существу, старым путём идти нельзя — он создал
            // бы вторую продажу. Такие ответы, как и 5xx, кроме 500, уходят в SyncBatchAsync — чек
            // остаётся в очереди и досылается тем же ключом.
            catch (ApiException ex) when (ex.StatusCode == 500
                                          || (ex.StatusCode is >= 400 and < 500
                                              && !PosCheckoutService.IsQuickTransientStatus(ex.StatusCode)))
            {
                // Продажи с этим ключом нет (иначе был бы 200-повтор) — пробуем старым путём.
                PosLogger.Log(
                    $"OFFLINE replay quick: чек {entry.Id} — отказ {ex.StatusCode}: {ex.Message} — старый путь.",
                    "OFFLINE");
                return (false, null);
            }
        }

        var saleId = CheckoutResponseHelper.TrySaleId(response);
        var replayed = response.ValueKind == JsonValueKind.Object
                       && response.TryGetProperty("replayed", out var rp) && rp.ValueKind == JsonValueKind.True;
        OfflinePendingSalesStore.Update(entry.Id, e =>
        {
            e.CheckoutCompleted = true;
            if (!string.IsNullOrWhiteSpace(saleId))
                e.SyncedSaleId = saleId;
        });
        PosLogger.Log(
            $"OFFLINE replay quick: чек {entry.Id} проведён за {sw.ElapsedMilliseconds} мс, продажа {saleId ?? "—"}, replayed={replayed}.",
            "OFFLINE");

        await ResyncStockAfterReplayAsync(entry, ct).ConfigureAwait(false);
        return (true, saleId);
    }

    /// <summary>Тело pos/checkout/ для чека очереди — для одиночной и пакетной досылки. null — чек этому пути не подходит.</summary>
    private static System.Text.Json.Nodes.JsonObject? BuildQuickReplayBody(OfflineSaleEntry entry)
    {
        // Смена, в которой чек пробит (см. комментарий в SubmitReplayCheckoutAsync): «offline-…»
        // сервер не знает — тогда без смены, сервер возьмёт открытую, как и старый путь.
        var shiftId = !string.IsNullOrWhiteSpace(entry.ShiftId)
                      && !entry.ShiftId!.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
            ? entry.ShiftId
            : null;

        var body = QuickCheckoutBody.TryBuild(
            entry.CartJson,
            entry.PaymentMethod,
            entry.CashReceived,
            nonCashReceived: null,
            clientId: null,
            shiftId,
            entry.ConsultantId,
            entry.ConsultantCommissionEnabled,
            entry.ConsultantCommissionPercent,
            printReceipt: false,
            out var unsupported);
        if (body == null)
        {
            PosLogger.Log($"OFFLINE replay quick: чек {entry.Id} не подходит ({unsupported}) — старый путь.", "OFFLINE");
            return null;
        }

        // 2026-10-05, ТЗ ч.12, п. 2.4 (сервер выложил 05.10): время продажи и касса. Сервер относит чек к смене этой кассы,
        // открытой в момент продажи, даже если она уже закрыта (раньше — 400 «Смена не открыта», деньги получены, а продажи
        // на сервере нет). Проверено 05.10: sold_at в закрытой смене → 201 в ту смену, повтор тем же ключом → 200 replayed.
        body["sold_at"] = entry.CreatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        if (Guid.TryParse(entry.CashboxId, out _))
            body["cashbox"] = entry.CashboxId!.Trim();
        return body;
    }
}
