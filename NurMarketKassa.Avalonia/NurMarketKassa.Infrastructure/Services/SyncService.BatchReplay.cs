using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-06, ТЗ ч.12, п. 3.6 (сервер выложил 05.10): пакетная досылка очереди — POST api/main/pos/checkout/batch/,
/// до 50 продаж одним запросом, у каждой свой ключ (id записи очереди) и свой результат. После долгой работы без связи
/// сотня чеков уходит за 2–3 запроса, а не за сотню.
///
/// Пакет — только ускоритель перед обычной досылкой по одному (SyncBatchAsync): что пакет не провёл ясно (отказ 4xx по
/// существу, ошибка 500 этой продажи), идёт обычным путём. Чек, отправленный пакетом без ясного ответа (обрыв, таймаут,
/// 5xx), повторяется только пакетом и тем же ключом (OfflineSaleEntry.BatchCheckoutAttempted): ключи пакета и одиночной
/// продажи сервер может хранить раздельно — одиночная досылка такого чека могла бы провести его второй раз.
/// В пакет идут только чеки, которые одиночной быстрой досылкой ещё не отправлялись.
/// </summary>
public sealed partial class SyncService
{
    private const int BatchLimit = 50;
    private static bool _batchMissing;

    /// <summary>Stop — связь пропала или сервер лёг: досылку на этом проходе прекратить.
    /// Skip — чеки, которые обычной досылке на этом проходе брать нельзя (проведены пакетом или ждут повтора пакетом).</summary>
    private async Task<(bool Stop, HashSet<string> Skip)> TryBatchReplayAsync(IReadOnlyList<OfflineSaleEntry> pending, CancellationToken ct)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (_batchMissing || _sales is not IPosQuickCheckoutApi api)
            return (false, skip);

        var candidates = new List<(OfflineSaleEntry Entry, JsonObject Body)>();
        foreach (var entry in pending)
        {
            if (entry.CheckoutCompleted || entry.CheckoutSubmittedAt is not null || !string.IsNullOrWhiteSpace(entry.SyncCartId))
                continue;
            // Уже уходил одиночной быстрой досылкой — только ей же (тем же ключом), см. TryQuickReplayAsync.
            if (entry.QuickCheckoutAttempted && !entry.BatchCheckoutAttempted)
                continue;
            if (!entry.BatchCheckoutAttempted && !UserPreferences.Instance.QuickCheckoutEnabled)
                continue;
            var body = BuildQuickReplayBody(entry);
            if (body == null)
                continue;
            candidates.Add((entry, body));
        }

        // Один новый чек — обычной досылкой, пакет ему ничего не даёт; чек, начатый пакетом, — только пакетом.
        if (candidates.Count < 2 && !candidates.Any(c => c.Entry.BatchCheckoutAttempted))
            return (false, skip);

        foreach (var chunk in candidates.Chunk(BatchLimit))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsOnline || ServerOutageMonitor.IsOutage)
                return (true, skip);

            // Отметка ДО запроса и переживает перезапуск: без ясного ответа чек повторяется только пакетом.
            foreach (var (entry, _) in chunk)
            {
                OfflinePendingSalesStore.Update(entry.Id, e => e.BatchCheckoutAttempted = true);
                skip.Add(entry.Id);
            }

            var items = new JsonArray();
            foreach (var (entry, body) in chunk)
                items.Add(new JsonObject { ["idempotency_key"] = entry.Id, ["body"] = body.DeepClone() });
            var started = DateTime.UtcNow;
            JsonElement response;
            try
            {
                response = await api.PosCheckoutBatchAsync(new JsonObject { ["items"] = items }, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Ответ не дошёл: продажи могли провестись. Чеки ждут следующего прохода — тем же пакетом и ключами.
                IsOnline = false;
                ServerOutageMonitor.ReportFailure(ReplayContext, ex, hard: true);
                PosLogger.Log($"OFFLINE batch: {chunk.Length} чек(ов) — нет ответа ({ex.Message}), повторим пакетом.", "OFFLINE");
                return (true, skip);
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405 || (ex.StatusCode is >= 400 and < 500 && !PosCheckoutService.IsQuickTransientStatus(ex.StatusCode) && ex.StatusCode != 408))
            {
                // Сервер отверг запрос целиком (адреса нет или пакет неверный) — ни одна продажа не проведена:
                // чеки идут обычной досылкой.
                if (ex.StatusCode is 404 or 405)
                    _batchMissing = true;
                foreach (var (entry, _) in chunk)
                {
                    OfflinePendingSalesStore.Update(entry.Id, e => e.BatchCheckoutAttempted = false);
                    skip.Remove(entry.Id);
                }
                PosLogger.Log($"OFFLINE batch: отказ {ex.StatusCode} ({ex.Message}) — чеки идут по одному.", "OFFLINE");
                continue;
            }
            catch (ApiException ex)
            {
                // 5xx, 408, 409/429: часть продаж могла провестись (у каждой своя транзакция). Повтор — пакетом.
                if (ex.StatusCode is 408 or (>= 502 and <= 599))
                    ServerOutageMonitor.ReportFailure(ReplayContext, ex, hard: true);
                PosLogger.Log($"OFFLINE batch: {chunk.Length} чек(ов) — ошибка {ex.StatusCode} ({ex.Message}), повторим пакетом.", "OFFLINE");
                return (true, skip);
            }

            var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("results", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (row.TryGetProperty("idempotency_key", out var k) && k.GetString() is { Length: > 0 } key)
                        results[key] = row.Clone();

            int done = 0, single = 0, later = 0;
            foreach (var (entry, _) in chunk)
            {
                if (!results.TryGetValue(entry.Id, out var result) || !result.TryGetProperty("status", out var st) || !st.TryGetInt32(out var status))
                {
                    later++;
                    continue;
                }

                if (status is 200 or 201)
                {
                    var saleId = CheckoutResponseHelper.TrySaleId(result);
                    OfflinePendingSalesStore.Update(entry.Id, e =>
                    {
                        e.CheckoutCompleted = true;
                        if (!string.IsNullOrWhiteSpace(saleId))
                            e.SyncedSaleId = saleId;
                    });
                    await ResyncStockAfterReplayAsync(entry, ct).ConfigureAwait(false);
                    PosLogger.Log($"OFFLINE replay batch: чек {entry.Id} проведён, продажа {saleId ?? "—"}{(status == 200 ? ", повтор по ключу" : "")}.", "OFFLINE");
                    OfflinePendingSalesStore.MarkSynced(entry.Id, saleId);
                    OfflinePendingSalesStore.RemoveSynced(entry.Id);
                    Interlocked.Increment(ref _replayedCount);
                    Lan.LanJournal.PublishUploaded(entry.Id, saleId);
                    done++;
                }
                else if (status == 500 || (status is >= 400 and < 500 && !PosCheckoutService.IsQuickTransientStatus(status) && status != 408))
                {
                    // Ясный отказ этой продажи (её транзакция не прошла) — продажи с ключом нет, дальше обычной досылкой.
                    OfflinePendingSalesStore.Update(entry.Id, e => e.BatchCheckoutAttempted = false);
                    skip.Remove(entry.Id);
                    PosLogger.Log($"OFFLINE replay batch: чек {entry.Id} — отказ {status}: {Detail(result)} — по одному.", "OFFLINE");
                    single++;
                }
                else
                {
                    later++;
                }
            }

            PosLogger.Log(
                $"OFFLINE batch: {chunk.Length} чек(ов) за {(DateTime.UtcNow - started).TotalMilliseconds:0} мс — проведено {done}, по одному {single}, повтор пакетом {later}.",
                "OFFLINE");
            UpdateStatusText();
            RaiseStateChanged();
        }

        return (false, skip);
    }

    private static string Detail(JsonElement result)
    {
        if (!result.TryGetProperty("detail", out var d))
            return "—";
        var text = d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : d.GetRawText();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
