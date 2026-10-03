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
/// • 2026-09-29: всё это — в пределах окна ожидания оплаты (ServerAnswerBudget, 1,8 с): кассир
///   не ждёт дольше, остальное делает досылка тем же ключом.
/// </summary>
public sealed partial class PosCheckoutService
{
    /// <summary>Ключ идемпотентности текущего чека и «отпечаток» тела, для которого он выдан.</summary>
    private string? _quickKey;
    private string? _quickKeyFingerprint;

    /// <summary>Ключ, на который сервер дважды ответил 500 в этой оплате (см. TryQuickCheckoutAsync).</summary>
    private string? _quickKeyAbandonedOn500;

    private void ForgetQuickKey()
    {
        _quickKey = null;
        _quickKeyFingerprint = null;
    }

    /// <summary>null — быстрый путь не применим или сервер отказал по существу: вызывающий идёт
    /// старым путём. Иначе — окончательный результат оплаты.</summary>
    /// <param name="cancellationToken">Отмена кассиром/окном.</param>
    /// <param name="serverWait">2026-09-29: то же плюс окно ожидания ответа сервера
    /// (ServerAnswerBudget): истекло — чек в очередь с этим же ключом.</param>
    private async Task<PosCheckoutResult?> TryQuickCheckoutAsync(
        PosCheckoutRequest request,
        CancellationToken cancellationToken,
        CancellationToken serverWait)
    {
        if (!UserPreferences.Instance.QuickCheckoutEnabled
            || OfflineModeHelper.SellLocally
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

        // 2026-09-28, денежный баг продажи №1136: окно оплаты показало 50,00, кассир взял 50, а
        // продажа ушла на 42,50 (акцию товара сервер применил сам, касса её не видела). Теперь
        // итог, который получится у сервера из ЭТОГО тела, сверяется с итогом окна оплаты ДО
        // отправки. Не сошлось больше чем на 0,01 — быстрым путём не отправляем: старый путь
        // перенесёт чек в серверную корзину и сверит её итог с окном оплаты ещё до проведения
        // продажи (CheckoutAsync), а при расхождении остановит оплату с понятным кассиру текстом.
        var bodyTotal = QuickCheckoutBody.ComputeTotal(body);
        var expectedTotal = request.ExpectedTotal ?? total;
        if (Math.Abs(bodyTotal - expectedTotal) > TotalTolerance)
        {
            PosLogger.Log(
                $"PAY quick: итог запроса {bodyTotal:0.00} ≠ итог окна оплаты {expectedTotal:0.00} (снимок чека {total:0.00}) — " +
                $"быстрым путём не отправлено, сверка через серверную корзину. Тело: {body.ToJsonString()}",
                "WARNING");
            return null;
        }

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
        var budgetExpired = false;
        for (var attempt = 1; attempt <= 3 && !done; attempt++)
        {
            try
            {
                var attemptTimeout = attempt == 1 ? timeout : TimeSpan.FromSeconds(Math.Min(15, timeout.TotalSeconds));
                response = await quickApi.PosQuickCheckoutAsync(body, key, attemptTimeout, serverWait).ConfigureAwait(false);
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
                    // 2026-09-29: ключ запоминается — если и старый путь упрётся в сбой сервера,
                    // чек встанет в очередь с ЭТИМ ключом: провелась ли продажа на самом деле,
                    // сервер скажет ответом «replayed» при досылке (см. SaveOfflineAfterServerFailureAsync).
                    _quickKeyAbandonedOn500 = key;
                    ForgetQuickKey();
                    PosLogger.Log("PAY quick: сервер дважды ответил 500 — старый путь.", "PAYMENT");
                    return null;
                }
            }
            catch (ApiException ex) when (IsQuickTransientStatus(ex.StatusCode))
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
                // Истёк наш таймаут запроса или окно ожидания оплаты. Продажа могла пройти —
                // повторяем тем же ключом (если есть время) или ставим в очередь с ним же.
                transient = ex;
                if (serverWait.IsCancellationRequested)
                {
                    budgetExpired = true;
                    PosLogger.Log($"PAY quick: сервер не ответил за {ServerAnswerBudget.TotalSeconds:0.0} с (попытка {attempt}).", "WARNING");
                    break;
                }

                PosLogger.Log($"PAY quick: таймаут (попытка {attempt}).", "WARNING");
                if (attempt >= 2)
                    break;
            }

            if (!done && attempt < 3)
            {
                // 2026-09-29: паузы между повторами — 0,25 и 0,5 с вместо 1 и 2 с: при сбое сервера
                // все три попытки укладываются в окно ожидания оплаты (владелец: не больше 2 с).
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), serverWait).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    budgetExpired = true;
                    break;
                }
            }
        }

        if (!done)
        {
            PosLogger.Log(
                $"PAY quick: сервер не ответил ({transient?.GetType().Name}: {transient?.Message}) — в очередь с ключом {key}.",
                "PAYMENT");
            // 2026-09-29: оплата исчерпала повторы или окно ожидания — сервер объявляется
            // недоступным сразу (ServerOutageMonitor): следующие чеки уйдут в очередь без ожидания.
            ServerOutageMonitor.ReportFailure(Tr.T("оплата", "төлөм", "payment", "ödeme", "to'lov"),
                budgetExpired
                    ? Tr.T($"сервер не ответил за {ServerAnswerBudget.TotalSeconds:0.0} с",
                        $"сервер {ServerAnswerBudget.TotalSeconds:0.0} с ичинде жооп берген жок",
                        $"the server did not answer within {ServerAnswerBudget.TotalSeconds:0.0} s",
                        $"sunucu {ServerAnswerBudget.TotalSeconds:0.0} sn içinde yanıt vermedi",
                        $"server {ServerAnswerBudget.TotalSeconds:0.0} s ichida javob bermadi")
                    : ServerOutageMonitor.Describe(transient ?? new TimeoutException()),
                hard: true);
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

        // Сервер провёл продажу — окно ожидания больше не нужно (см. CheckoutAsync).
        DisarmServerWait();
        ForgetQuickKey();
        var replayed = response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("replayed", out var rp) && rp.ValueKind == JsonValueKind.True;
        var serverTotal = TryReadQuickDecimal(response, "total");
        PosLogger.Log(
            $"PAY quick OK за {sw.ElapsedMilliseconds} мс: number={TryReadQuickText(response, "number") ?? "—"}, " +
            $"total={serverTotal?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—"} (касса {total:0.00}), replayed={replayed}",
            "PAYMENT");

        // 2026-09-28, продажа №1136: продажа уже проведена. Если сервер всё-таки насчитал другую
        // сумму (например, акцию или цену товара поменяли на сайте за минуты до оплаты и касса ещё
        // не подтянула каталог), молчать нельзя: кассир взял деньги по окну оплаты.
        string? mismatchWarning = null;
        if (serverTotal is { } serverSum && Math.Abs((double)serverSum - expectedTotal) > TotalTolerance)
        {
            var server = (double)serverSum;
            var diff = Math.Abs(server - expectedTotal);
            var number = TryReadQuickText(response, "number") ?? "—";
            PosLogger.Log(
                $"PAY quick: СУММА НЕ СОШЛАСЬ после продажи №{number}: сервер {server:0.00}, окно оплаты {expectedTotal:0.00}, " +
                $"тело {bodyTotal:0.00}. Тело: {body.ToJsonString()}. Ответ: {response.GetRawText()}",
                "WARNING");
            mismatchWarning = server < expectedTotal
                ? Tr.T(
                    $"Внимание: сервер провёл чек №{number} на {server:0.00} сом, а в окне оплаты было {expectedTotal:0.00} сом. Верните покупателю {diff:0.00} сом. Скорее всего, на сайте изменили акцию или цену товара.",
                    $"Көңүл буруңуз: сервер №{number} чекти {server:0.00} сомго өткөрдү, ал эми төлөм терезесинде {expectedTotal:0.00} сом болчу. Сатып алуучуга {diff:0.00} сом кайтарыңыз. Сайтта товардын акциясы же баасы өзгөртүлгөн болушу мүмкүн.",
                    $"Attention: the server recorded receipt No. {number} for {server:0.00} som, but the payment window showed {expectedTotal:0.00} som. Give the customer back {diff:0.00} som. Most likely the product's promotion or price was changed on the website.",
                    $"Dikkat: sunucu {number} numaralı fişi {server:0.00} som olarak kaydetti, ödeme penceresinde ise {expectedTotal:0.00} som vardı. Müşteriye {diff:0.00} som iade edin. Büyük olasılıkla sitede ürünün kampanyası veya fiyatı değiştirildi.",
                    $"Diqqat: server №{number} chekni {server:0.00} so'mga o'tkazdi, to'lov oynasida esa {expectedTotal:0.00} so'm edi. Xaridorga {diff:0.00} so'm qaytaring. Ehtimol, saytda mahsulot aksiyasi yoki narxi o'zgartirilgan.")
                : Tr.T(
                    $"Внимание: сервер провёл чек №{number} на {server:0.00} сом, а в окне оплаты было {expectedTotal:0.00} сом. Не хватает {diff:0.00} сом — доберите у покупателя. Скорее всего, на сайте изменили акцию или цену товара.",
                    $"Көңүл буруңуз: сервер №{number} чекти {server:0.00} сомго өткөрдү, ал эми төлөм терезесинде {expectedTotal:0.00} сом болчу. {diff:0.00} сом жетишпейт — сатып алуучудан алыңыз. Сайтта товардын акциясы же баасы өзгөртүлгөн болушу мүмкүн.",
                    $"Attention: the server recorded receipt No. {number} for {server:0.00} som, but the payment window showed {expectedTotal:0.00} som. {diff:0.00} som is missing — collect it from the customer. Most likely the product's promotion or price was changed on the website.",
                    $"Dikkat: sunucu {number} numaralı fişi {server:0.00} som olarak kaydetti, ödeme penceresinde ise {expectedTotal:0.00} som vardı. {diff:0.00} som eksik — müşteriden tahsil edin. Büyük olasılıkla sitede ürünün kampanyası veya fiyatı değiştirildi.",
                    $"Diqqat: server №{number} chekni {server:0.00} so'mga o'tkazdi, to'lov oynasida esa {expectedTotal:0.00} so'm edi. {diff:0.00} so'm yetishmaydi — xaridordan oling. Ehtimol, saytda mahsulot aksiyasi yoki narxi o'zgartirilgan.");
        }

        var finished = await FinishOnlineCheckoutAsync(
                request,
                cartJson,
                serverTotal is { } st ? (double)st : total,
                response,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        finished.TotalMismatchWarning = mismatchWarning;
        return finished;
    }

    /// <summary>Допуск сверки итога кассы с сервером — копейка (плюс запас на двоичную погрешность).</summary>
    private const double TotalTolerance = 0.01 + 1e-6;

    /// <summary>2026-10-04, стенд «сбои сервера»: ответы, после которых продажа с этим ключом МОГЛА
    /// пройти или ещё выполняется, — повтор только тем же ключом (или очередь с ним же), но не старый
    /// путь. Раньше сюда попадали лишь 408/429/502/503/504, а 520–524 (Cloudflare: запрос мог дойти до
    /// сервера и провестись) и 409/423/425 («запрос с этим ключом ещё выполняется») считались отказом
    /// по существу — касса уходила старым путём и создавала вторую продажу: на стенде 520 после
    /// проведённой продажи давал 2 продажи на один чек.</summary>
    internal static bool IsQuickTransientStatus(int? statusCode) =>
        statusCode is 408 or 409 or 423 or 425 or 429 || statusCode is >= 501 and <= 599;

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
