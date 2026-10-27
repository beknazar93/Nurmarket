using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (аудит кассы, В-7, Н-14): закрытие смены на сервере из очереди (PendingShiftCloseStore) — одно место для
/// SyncService (фоновая очередь) и CashShiftService (открытие смены, когда сервер вернул ещё не закрытую). Повтор с округлением
/// сумм («не более 2 цифр после запятой») раньше умел только живой путь закрытия; очередь шла тем же POST без округления и
/// получала тот же отказ при каждой попытке — смена не закрывалась на сервере никогда.</summary>
public enum ShiftCloseOutcome
{
    /// <summary>Сервер принял закрытие.</summary>
    Closed,
    /// <summary>Сервер уже считает смену закрытой (закрыли на сайте / автозакрытие) — очередь по ней не нужна.</summary>
    AlreadyClosed,
    /// <summary>Сервер не принял (сеть, 5xx, отказ) — попробуем позже.</summary>
    Failed,
}

public static class ShiftCloseRetry
{
    public static bool IsAlreadyClosed(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("закрыт", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не найден", StringComparison.OrdinalIgnoreCase));

    /// <summary>2026-10-27 (ревью, См-6): «не найден» в тексте (например, 400 «Касса не найдена») не значит, что смена закрыта: запись очереди
    /// снималась, а смена на сервере оставалась открытой до автозакрытия без пересчёта кассира. Слова «закрыт»/«already» — прямое «уже закрыта»;
    /// только «не найден» — сверяем по списку открытых смен: смена там есть (или список не получили) — закрытие не принято, Failed.</summary>
    private static async Task<ShiftCloseOutcome> ClassifyAlreadyClosedAsync(IShiftApiService api, string shiftId, string? message, CancellationToken ct)
    {
        if (message is not null
            && (message.Contains("закрыт", StringComparison.OrdinalIgnoreCase) || message.Contains("already", StringComparison.OrdinalIgnoreCase)))
            return ShiftCloseOutcome.AlreadyClosed;

        try
        {
            var open = await api.ConstructionShiftsListAsync(true, ct).ConfigureAwait(false);
            if (CashShiftService.FindShiftById(open, shiftId) is not null)
            {
                PosLogger.Log($"Закрытие смены {shiftId} из очереди: ответ «{message}», но смена в списке открытых — закрытие не принято, повторю позже.", "SHIFT");
                return ShiftCloseOutcome.Failed;
            }

            return ShiftCloseOutcome.AlreadyClosed;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            PosLogger.Log($"Закрытие смены {shiftId} из очереди: список открытых смен не получен ({ex.Message}) — запись остаётся.", "SHIFT");
            return ShiftCloseOutcome.Failed;
        }
    }

    public static async Task<(ShiftCloseOutcome Outcome, string? Error)> CloseAsync(
        IShiftApiService api, string shiftId, string? closingCash, CancellationToken ct)
    {
        try
        {
            await api.ConstructionShiftCloseAsync(shiftId, closingCash, null, ct).ConfigureAwait(false);
            return (ShiftCloseOutcome.Closed, null);
        }
        catch (ApiException ex) when (CashShiftService.LooksLikeDecimalPlacesError(ex.Message))
        {
            // Тот же повтор с округлением, что и при живом закрытии (CashShiftService.CloseShiftAsync).
            PosLogger.Log($"Закрытие смены {shiftId} из очереди отклонено из-за лишних знаков в суммах сервера — повтор с округлением.", "SHIFT");
            var rounded = await CashShiftService.BuildRoundedTotalsAsync(api, shiftId, ct).ConfigureAwait(false);
            if (rounded.Count == 0)
                return (ShiftCloseOutcome.Failed, ex.Message);
            try
            {
                await api.ConstructionShiftCloseAsync(shiftId, closingCash, rounded, ct).ConfigureAwait(false);
                return (ShiftCloseOutcome.Closed, null);
            }
            catch (ApiException retry) when (retry.StatusCode is 404 or 400 && IsAlreadyClosed(retry.Message))
            {
                return (await ClassifyAlreadyClosedAsync(api, shiftId, retry.Message, ct).ConfigureAwait(false), retry.Message);
            }
            catch (Exception retry) when (!(retry is OperationCanceledException && ct.IsCancellationRequested))
            {
                return (ShiftCloseOutcome.Failed, retry.Message);
            }
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 400 && IsAlreadyClosed(ex.Message))
        {
            // Смену закрыли где-то ещё (например, в веб-панели) — очередь об этом не знает.
            return (await ClassifyAlreadyClosedAsync(api, shiftId, ex.Message, ct).ConfigureAwait(false), ex.Message);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested)) // 2026-10-27: таймаут HttpClient без отмены вызывающего — обычный сбой
        {
            return (ShiftCloseOutcome.Failed, ex.Message);
        }
    }
}

/// <summary>2026-10-27 (аудит кассы, С-11): «открыть смену» у сервера идемпотентно — если смена кассира уже открыта, он отдаёт её, а
/// не новую, с прежними opening_cash и opened_at. Касса показывала в шапке введённую сумму, а у сервера была другая.</summary>
public static class ShiftOpenResponse
{
    /// <summary>Запас на разницу часов кассы и сервера: смена, открытая секунды назад, «продолженной» не считается.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(3);

    /// <summary>Resumed — сервер вернул смену, открытую раньше этого нажатия (по opened_at старше окна или по другой начальной
    /// сумме). ServerOpening/OpenedAt — как у сервера (местное время), если он их прислал.</summary>
    public static (bool Resumed, decimal? ServerOpening, DateTime? OpenedAtLocal) Classify(JsonElement response, decimal entered, DateTimeOffset now)
    {
        var body = response;
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("shift", out var inner) && inner.ValueKind == JsonValueKind.Object)
            body = inner;
        if (body.ValueKind != JsonValueKind.Object)
            return (false, null, null);

        decimal? serverOpening = null;
        if (body.TryGetProperty("opening_cash", out var oc))
        {
            var text = oc.ValueKind == JsonValueKind.String ? oc.GetString() : oc.ValueKind == JsonValueKind.Number ? oc.GetRawText() : null;
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                serverOpening = parsed;
        }

        DateTime? openedLocal = null;
        var old = false;
        if (body.TryGetProperty("opened_at", out var oa) && oa.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(oa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var opened))
        {
            openedLocal = opened.LocalDateTime;
            old = now - opened > FreshWindow;
        }

        var differentAmount = serverOpening is { } so && Math.Abs(so - entered) > 0.005m;
        return (old || differentAmount, serverOpening, openedLocal);
    }
}
