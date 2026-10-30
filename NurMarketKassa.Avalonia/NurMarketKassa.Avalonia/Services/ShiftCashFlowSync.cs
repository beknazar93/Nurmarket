using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Внесения и изъятия кассы — на сервер, в движения денег смены
/// (api/construction/cashflows/ с полем shift). 2026-09-25.
///
/// Раньше они жили только в cash_history.json этой кассы: сайт их не видел, а изъятие не
/// попадало в расход и ожидаемый остаток смены на сервере — при закрытии смены сайт и Z-отчёт
/// (он берёт расход с сервера) показывали недостачу ровно на вынутую сумму.
///
/// Как сервер их учитывает (проверено на живой смене тестового аккаунта): изъятие
/// (source_kind «shift_drawer_outflow», наличные) входит в expense_total и уменьшает
/// expected_cash; внесение (ручной приход) идёт в баланс кассы, но в expected_cash смены не
/// входит. Поэтому ShiftCashOperationsStore.NetForShift после записи на сервер перестаёт
/// вычитать изъятие сам, а внесение прибавляет по-прежнему.
///
/// 2026-09-28: NurCRM добавил вид «shift_drawer_inflow» (BE-06) — внесение теперь тоже входит в
/// expected_cash смены, и NetForShift перестаёт прибавлять его после записи на сервер.
///
/// Не ушло (нет сети, сервер недоступен) — операция остаётся в очереди и уходит при следующей
/// операции или при обновлении остатка смены. Основной путь — в открытую смену: в закрытую смену сервер
/// уже посчитал итоги. 2026-10-27 (В-5): хвосты ЗАКРЫТЫХ смен всё же досылаются (PushPendingForOtherShiftsAsync, до трёх
/// попыток за запуск), а перед закрытием смены касса сначала досылает хвосты и, если не вышло, спрашивает кассира (MainWindow.ApplyShiftClosedAsync).</summary>
public static class ShiftCashFlowSync
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>2026-10-22 (статьи изъятия): сервер отказал (400) в category вместе со сменой — до перезапуска не шлём.</summary>
    private static volatile bool _categoryRejected;

    /// <summary>2026-10-27 (аудит кассы, В-5): сколько раз подряд не удалось дослать хвосты закрытой смены (смена → попытки). Закрытую
    /// смену сервер мог не принять — после трёх неудач за запуск тихо ждём следующего запуска кассы, чтобы не молотить сервер.</summary>
    private static readonly Dictionary<string, int> ClosedShiftFailures = new(StringComparer.OrdinalIgnoreCase);
    private const int ClosedShiftMaxAttempts = 3;

    /// <summary>2026-10-27 (аудит кассы, В-5): внесения/изъятия, не дошедшие до сервера, раньше уходили только в ТЕКУЩУЮ смену. Если
    /// смену закрыли (кассир без связи, сервер сам в 01:00), хвосты оставались в cash_history.json навсегда, а Z на сайте показывал
    /// недостачу, которой нет. Теперь досылаем и для остальных смен с хвостами. Возвращает число смен, по которым ещё остались хвосты.</summary>
    public static async Task<int> PushPendingForOtherShiftsAsync(string? activeShiftId)
    {
        if (ServerOutageMonitor.IsOutage)
            return 0;
        var left = 0;
        foreach (var other in ShiftCashOperationsStore.PendingShiftIds())
        {
            if (string.Equals(other, activeShiftId, StringComparison.OrdinalIgnoreCase)
                || other.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(other, out _))
                continue;
            lock (ClosedShiftFailures)
            {
                if (ClosedShiftFailures.GetValueOrDefault(other) >= ClosedShiftMaxAttempts)
                    continue;
            }

            await PushPendingAsync(other).ConfigureAwait(false);
            if (ShiftCashOperationsStore.PendingForShift(other).Count == 0)
            {
                lock (ClosedShiftFailures) ClosedShiftFailures.Remove(other);
                PosLogger.Log($"Хвосты внесений/изъятий закрытой смены {other[..8]} досланы на сервер.", "SHIFT");
                continue;
            }

            left++;
            int attempts;
            lock (ClosedShiftFailures)
                attempts = ClosedShiftFailures[other] = ClosedShiftFailures.GetValueOrDefault(other) + 1;
            PosLogger.Log($"Хвосты внесений/изъятий закрытой смены {other[..8]} не ушли на сервер (попытка {attempts} из {ClosedShiftMaxAttempts}).", "WARNING");
        }

        return left;
    }

    public static async Task PushPendingAsync(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || !Guid.TryParse(shiftId, out _))
            return;
        // 2026-10-29 (аварийный аудит, п. 5): активна настоящая серверная смена — внесения/изъятия офлайн-смены («offline-shift-…», которую
        // сервер не знает и которые раньше не уходили никогда) привязываются к ней: туда же сервер кладёт чеки офлайн-смены при выгрузке.
        ShiftCashOperationsStore.RebindOfflineShifts(shiftId);
        // 2026-09-29: сервер не отвечает (ServerOutageMonitor) — операции ждут в кассе, уйдут
        // по восстановлении связи (MainWindow.OnServerRecovered), без ожидания таймаутов сейчас.
        if (ServerOutageMonitor.IsOutage)
            return;
        var cashboxId = PosApp.PosCashboxId;
        if (string.IsNullOrWhiteSpace(cashboxId) || ShiftCashOperationsStore.PendingForShift(shiftId).Count == 0)
            return;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pending = ShiftCashOperationsStore.PendingForShift(shiftId);
            if (pending.Count == 0)
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // Ответ на прошлую попытку мог потеряться уже после записи на сервере — сверяемся с
            // движениями по source_id (= ID операции на кассе), чтобы не записать дважды.
            // 2026-09-28: сервер научился фильтру ?source_id= (BE-06) — спрашиваем по каждой
            // операции одним коротким запросом вместо чтения всех движений смены (у большого
            // магазина это десятки страниц). Фильтр не ответил — как раньше, по всей смене.
            Dictionary<string, (string Id, string? Kind)>? existing = null;

            foreach (var op in pending)
            {
                var known = await NurCrmReportsApi.CashFlowsBySourceIdAsync(op.Id, cts.Token).ConfigureAwait(false);
                if (known is null)
                {
                    existing ??= await ExistingFlowsAsync(shiftId, cts.Token).ConfigureAwait(false);
                    if (existing.TryGetValue(op.Id, out var fromShift))
                        known = new[] { fromShift };
                }

                if (known is { Count: > 0 })
                {
                    ShiftCashOperationsStore.MarkSynced(op.Id, known[0].Id, known[0].SourceKind);
                    continue;
                }

                var withdrawal = CashOperationModel.ResolveKind(op.Type) == CashOperationKind.Withdrawal;
                // 2026-09-28, BE-06: внесение — парный к изъятию вид «shift_drawer_inflow». Раньше
                // уходило как «manual» и в ожидаемый остаток смены на сервере не входило: после
                // закрытия смены сайт показывал баланс меньше ровно на сумму внесений. Проверено на
                // тестовой смене: внесение 10 сом → expected_cash 905 → 915, изъятие 10 → 905.
                var sourceKind = withdrawal ? "shift_drawer_outflow" : ShiftCashOperationsStore.ShiftDrawerInflowKind;
                // 2026-10-22 (статьи изъятия): статья — в category (справочник NurCRM, как выбирает веб) и в названии
                // («Изъятие: Аренда — за октябрь»): сервер, не принявший category вместе со сменой, всё равно покажет статью.
                var hasArticle = !string.IsNullOrWhiteSpace(op.ArticleKey);
                var body = new Dictionary<string, string>
                {
                    ["cashbox"] = cashboxId,
                    ["shift"] = shiftId,
                    ["type"] = withdrawal ? "expense" : "income",
                    ["name"] = hasArticle ? CashArticles.FlowName(op.Type, op.ArticleRu, op.ArticleComment) : FlowName(op.Type, op.Comment),
                    ["amount"] = op.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                    ["source_kind"] = sourceKind,
                    ["source_id"] = op.Id,
                    ["payment_method"] = "cash",
                };
                if (hasArticle && !_categoryRejected)
                {
                    string? categoryId = null;
                    try
                    {
                        using var catCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        catCts.CancelAfter(TimeSpan.FromSeconds(5));
                        categoryId = await CashArticles.ServerCategoryIdAsync(withdrawal, op.ArticleKey, op.ArticleRu, catCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                        PosLogger.Log($"Статья «{op.ArticleRu}»: справочник NurCRM не ответил за 5 с — {op.Type} без category, статья в названии.", "WARNING");
                    }
                    if (!string.IsNullOrEmpty(categoryId))
                        body["category"] = categoryId;
                }

                JsonElement created;
                try
                {
                    created = await PosApp.ShiftApi.ConstructionCashFlowCreateAsync(body, cts.Token).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == 400 && body.ContainsKey("category")
                                              && (ex.Message.Contains("category", StringComparison.OrdinalIgnoreCase)
                                                  || ex.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("category", out _)))
                {
                    // Отказ 400 по category — движение не записано: повторяем без неё (статья остаётся в названии).
                    // Сервер не принимает category вместе со сменой — больше не шлём до перезапуска (ТЗ ч. 24.4).
                    _categoryRejected = true;
                    PosLogger.Log($"{op.Type}: сервер отверг category статьи «{op.ArticleRu}» ({ex.Message}) — без неё, статья в названии.", "WARNING");
                    body.Remove("category");
                    created = await PosApp.ShiftApi.ConstructionCashFlowCreateAsync(body, cts.Token).ConfigureAwait(false);
                }
                var flowId = Str(created, "id");
                // Вид берём из ответа: старый сервер без «shift_drawer_inflow» мог записать другой.
                var savedKind = Str(created, "source_kind");
                ShiftCashOperationsStore.MarkSynced(
                    op.Id,
                    string.IsNullOrEmpty(flowId) ? "?" : flowId,
                    string.IsNullOrEmpty(savedKind) ? sourceKind : savedKind);
                var status = Str(created, "status");
                var savedCategory = Str(created, "category_title") is { Length: > 0 } catTitle ? catTitle : body.GetValueOrDefault("category") ?? "—";
                PosLogger.Log(
                    $"{op.Type} {op.Amount:0.00} записано на сервер (смена {shiftId[..8]}, движение {flowId}, статус {status}"
                    + (hasArticle ? $", статья «{op.ArticleRu}», category {savedCategory}" : "")
                    + ").",
                    string.IsNullOrEmpty(status) || status == "approved" ? "SHIFT" : "WARNING");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Внесение/изъятие не записано на сервер, повторим позже: {ex.Message}", "WARNING");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Dictionary<string, (string Id, string? Kind)>> ExistingFlowsAsync(string shiftId, CancellationToken ct)
    {
        var map = new Dictionary<string, (string Id, string? Kind)>(StringComparer.OrdinalIgnoreCase);
        // Каждая продажа — тоже движение смены, у большого магазина их за смену тысячи.
        for (var page = 1; page <= 40; page++)
        {
            var root = await PosApp.ShiftApi.ConstructionCashFlowsForShiftAsync(shiftId, page, ct).ConfigureAwait(false);
            var rows = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var r) ? r : root;
            if (rows.ValueKind != JsonValueKind.Array)
                break;

            foreach (var row in rows.EnumerateArray())
            {
                var sourceId = Str(row, "source_id");
                var id = Str(row, "id");
                if (!string.IsNullOrEmpty(sourceId) && !string.IsNullOrEmpty(id))
                    map[sourceId] = (id, Str(row, "source_kind"));
            }

            var hasNext = root.ValueKind == JsonValueKind.Object
                          && root.TryGetProperty("next", out var next)
                          && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }
        return map;
    }

    /// <summary>Название движения на сайте: «Изъятие: инкассация». Без причины — просто тип.</summary>
    private static string FlowName(string type, string? comment)
    {
        var name = string.IsNullOrWhiteSpace(comment) ? type : $"{type}: {comment.Trim()}";
        return name.Length > 255 ? name[..255] : name;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
