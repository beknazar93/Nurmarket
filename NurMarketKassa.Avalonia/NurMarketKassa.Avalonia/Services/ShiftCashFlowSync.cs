using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;

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
/// Не ушло (нет сети, сервер недоступен) — операция остаётся в очереди и уходит при следующей
/// операции или при обновлении остатка смены. Только в открытую смену: в закрытую смену сервер
/// уже посчитал итоги.</summary>
public static class ShiftCashFlowSync
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task PushPendingAsync(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId) || !Guid.TryParse(shiftId, out _))
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
            // движениями смены по source_id (= ID операции на кассе), чтобы не записать дважды.
            var existing = await ExistingFlowsAsync(shiftId, cts.Token).ConfigureAwait(false);

            foreach (var op in pending)
            {
                if (existing.TryGetValue(op.Id, out var knownId))
                {
                    ShiftCashOperationsStore.MarkSynced(op.Id, knownId);
                    continue;
                }

                var withdrawal = CashOperationModel.ResolveKind(op.Type) == CashOperationKind.Withdrawal;
                var body = new Dictionary<string, string>
                {
                    ["cashbox"] = cashboxId,
                    ["shift"] = shiftId,
                    ["type"] = withdrawal ? "expense" : "income",
                    ["name"] = FlowName(op.Type, op.Comment),
                    ["amount"] = op.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                    ["source_kind"] = withdrawal ? "shift_drawer_outflow" : "manual",
                    ["source_id"] = op.Id,
                    ["payment_method"] = "cash",
                };

                var created = await PosApp.ShiftApi.ConstructionCashFlowCreateAsync(body, cts.Token).ConfigureAwait(false);
                var flowId = Str(created, "id");
                ShiftCashOperationsStore.MarkSynced(op.Id, string.IsNullOrEmpty(flowId) ? "?" : flowId);
                var status = Str(created, "status");
                PosLogger.Log(
                    $"{op.Type} {op.Amount:0.00} записано на сервер (смена {shiftId[..8]}, движение {flowId}, статус {status}).",
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

    private static async Task<Dictionary<string, string>> ExistingFlowsAsync(string shiftId, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    map[sourceId] = id;
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
