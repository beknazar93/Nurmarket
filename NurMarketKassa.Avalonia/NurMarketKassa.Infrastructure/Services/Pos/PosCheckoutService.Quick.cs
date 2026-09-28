using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28, BE-11: быстрый путь оплаты — продажа одним запросом POST api/main/pos/checkout/
/// с заголовком Idempotency-Key. Раньше одна продажа = sales/start → очистка корзины → позиции
/// по одной → скидка → checkout (4–5 запросов, 1,5–2 с на хорошем интернете, по журналу кассы
/// 28.09: «PAY API checkout» → «Checkout API OK» 1,5–1,9 с на 1–2 позиции).
///
/// Правила (проверены на тестовом аккаунте 28.09, продажи 1116–1118):
/// • повтор с тем же ключом → 200 и та же продажа ("replayed": true), дубля нет;
/// • тело при повторе сервер НЕ сравнивает, поэтому ключ живёт ровно столько, сколько не
///   меняется состав запроса (<see cref="_quickKeyFingerprint"/>): тот же чек повторно — тот же
///   ключ, изменили чек/оплату — новый ключ;
/// • 4xx — сервер отказал по существу, ничего не создано → идём старым путём, который умеет
///   больше (раскладка скидки по строкам, продажа при нехватке остатка, если её пропускает
///   старый адрес) и даёт кассиру привычные сообщения;
/// • 500 дважды подряд с тем же ключом → тоже старый путь: продажа, прошедшая в первый раз,
///   вернулась бы на втором запросе как повтор (200), значит её нет;
/// • таймаут / 502–504 / 429 → повтор с ТЕМ ЖЕ ключом вместо сверки корзины; не помогло или
///   нет сети → чек в офлайн-очередь с id записи = ключ, досылка идёт тем же ключом
///   (SyncService.TryQuickReplayAsync) — двойного чека не будет, даже если первый запрос дошёл.
/// </summary>
public sealed partial class PosCheckoutService
{
    /// <summary>Ключ идемпотентности текущего чека и «отпечаток» тела, для которого он выдан.</summary>
    private string? _quickKey;
    private string? _quickKeyFingerprint;

    private void ForgetQuickKey()
    {
        _quickKey = null;
        _quickKeyFingerprint = null;
    }

    /// <summary>null — быстрый путь не применим или сервер отказал по существу: вызывающий идёт
    /// старым путём. Иначе — окончательный результат оплаты.</summary>
    private async Task<PosCheckoutResult?> TryQuickCheckoutAsync(
        PosCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (!UserPreferences.Instance.QuickCheckoutEnabled
            || OfflineModeHelper.UseLocalOperations
            || _salesApi is not IPosQuickCheckoutApi quickApi
            || !_cart.HasCart
            || _cart.LineCount == 0)
            return null;

        // Смена должна быть серверной: «offline-shift-…» сервер не знает, такой чек — старым путём
        // (он же решит, в очередь или нет).
        var shiftId = PosApp.ActiveShiftId;
        if (!Guid.TryParse(shiftId, out _))
            return null;

        var cartJson = _cart.GetRawText();
        var body = QuickCheckoutBody.TryBuild(
            cartJson,
            request.PaymentMethod,
            request.CashReceived,
            request.NonCashReceived,
            request.ClientId,
            shiftId,
            request.ConsultantId,
            request.ConsultantCommissionEnabled,
            request.ConsultantCommissionPercent,
            request.PrintReceipt,
            out var unsupported);
        if (body == null)
        {
            PosLogger.Log($"PAY quick: не подходит ({unsupported}) — старый путь.", "PAYMENT");
            return null;
        }

        var total = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;
        var fingerprint = body.ToJsonString();
        if (_quickKey == null || !string.Equals(_quickKeyFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _quickKey = Guid.NewGuid().ToString("N");
            _quickKeyFingerprint = fingerprint;
        }

        var key = _quickKey;
        var lines = CartDisplayHelper.EnumerateItems(_cart.Root).Count();
        // Первая попытка — по размеру чека; повтор короче: при «чёрной дыре» в сети кассир не
        // должен ждать дольше, чем ждал раньше подготовку корзины (25 с), до ухода в очередь.
        var timeout = TimeSpan.FromSeconds(Math.Min(60, 15 + 0.1 * lines));
        var sw = Stopwatch.StartNew();
        PosLogger.Log(
            $"PAY quick checkout: key={key}, lines={lines}, method={request.PaymentMethod}, total={total:0.00}",
            "PAYMENT");

        JsonElement response = default;
        var done = false;
        Exception? transient = null;
        var server500 = 0;
        for (var attempt = 1; attempt <= 3 && !done; attempt++)
        {
            try
            {
                var attemptTimeout = attempt == 1 ? timeout : TimeSpan.FromSeconds(Math.Min(15, timeout.TotalSeconds));
                response = await quickApi.PosQuickCheckoutAsync(body, key, attemptTimeout, cancellationToken).ConfigureAwait(false);
                done = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiException ex) when (ex.StatusCode == 500)
            {
                transient = ex;
                PosLogger.Log($"PAY quick: 500 (попытка {attempt}): {DescribeQuickError(ex)}", "WARNING");
                if (++server500 >= 2)
                {
                    ForgetQuickKey();
                    PosLogger.Log("PAY quick: сервер дважды ответил 500 — старый путь.", "PAYMENT");
                    return null;
                }
            }
            catch (ApiException ex) when (ex.StatusCode is 408 or 429 or 502 or 503 or 504)
            {
                transient = ex;
                PosLogger.Log($"PAY quick: {ex.StatusCode} (попытка {attempt}), повтор тем же ключом.", "WARNING");
            }
            catch (ApiException ex)
            {
                // Отказ по существу (нет товара, мало остатка, смена не открыта, сессия…). Ничего
                // не создано: при уже проведённом ключе сервер ответил бы 200-повтором.
                ForgetQuickKey();
                PosLogger.Log(
                    $"PAY quick: отказ {ex.StatusCode} за {sw.ElapsedMilliseconds} мс — старый путь. {DescribeQuickError(ex)}",
                    "PAYMENT");
                return null;
            }
            catch (HttpRequestException ex)
            {
                // Нет сети — повтор сейчас не поможет, очередь дошлёт тем же ключом.
                transient = ex;
                break;
            }
            catch (OperationCanceledException ex)
            {
                // Истёк наш таймаут запроса. Продажа могла пройти — повторяем тем же ключом.
                transient = ex;
                PosLogger.Log($"PAY quick: таймаут (попытка {attempt}).", "WARNING");
                if (attempt >= 2)
                    break;
            }

            if (!done && attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }

        if (!done)
        {
            PosLogger.Log(
                $"PAY quick: сервер не ответил ({transient?.GetType().Name}: {transient?.Message}) — в очередь с ключом {key}.",
                "PAYMENT");
            var reason = Tr.T("Таймаут оплаты или потеря сети.", "Төлөмдү күтүү убактысы бүттү же тармак үзүлдү.",
                "Payment timed out or the connection was lost.", "Ödeme zaman aşımına uğradı veya ağ bağlantısı koptu.",
                "To'lov vaqti tugadi yoki tarmoq uzildi.");
            var offline = await CompleteOfflineCheckoutAsync(request, cartJson, total, reason, entryId: key, quickAttempted: true)
                .ConfigureAwait(false);
            // Долг и смешанную очередь не принимает — ключ остаётся за чеком: повтор оплаты тем же
            // составом вернёт уже проведённую продажу, а не создаст вторую.
            if (offline.SavedOffline)
                ForgetQuickKey();
            return offline;
        }

        ForgetQuickKey();
        var replayed = response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("replayed", out var rp) && rp.ValueKind == JsonValueKind.True;
        var serverTotal = TryReadQuickDecimal(response, "total");
        PosLogger.Log(
            $"PAY quick OK за {sw.ElapsedMilliseconds} мс: number={TryReadQuickText(response, "number") ?? "—"}, " +
            $"total={serverTotal?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"} (касса {total:0.00}), replayed={replayed}",
            "PAYMENT");

        return await FinishOnlineCheckoutAsync(
                request,
                cartJson,
                serverTotal is { } st ? (double)st : total,
                response,
                null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DescribeQuickError(ApiException ex)
    {
        var text = ex.Payload is { } payload ? payload.GetRawText() : ex.Message;
        text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static decimal? TryReadQuickDecimal(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && TryReadDecimal(root, property, out var value) ? value : null;

    private static string? TryReadQuickText(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null,
            }
            : null;
}
