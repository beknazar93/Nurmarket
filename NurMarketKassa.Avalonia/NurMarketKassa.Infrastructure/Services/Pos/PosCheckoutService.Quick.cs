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

    /// <summary>2026-10-27: ключ, с которым запрос уже уходил на сервер (исход мог остаться неизвестным). 409 price_changed на такой ключ
    /// не доказывает «ничего не создано» (повтор мог прийти после проведённой продажи), поэтому ему не верим — см. TryQuickCheckoutAsync.</summary>
    private string? _quickKeyMaybeSent;

    /// <summary>2026-10-29: жетон чека, для которого выдан ключ. Другой чек с тем же составом (следующий покупатель «в долг») получает новый ключ,
    /// иначе сервер вернул бы ему продажу прошлого покупателя как повтор.</summary>
    private string? _quickKeyCheckToken;

    private void ForgetQuickKey()
    {
        _quickKey = null;
        _quickKeyFingerprint = null;
        _quickKeyMaybeSent = null;
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
        if (_quickKey == null || !string.Equals(_quickKeyFingerprint, fingerprint, StringComparison.Ordinal)
            || !string.Equals(_quickKeyCheckToken, _checkoutToken, StringComparison.Ordinal))
        {
            _quickKeyCheckToken = _checkoutToken;
            // 2026-10-29 (аварийный аудит, п. 2): тот же платёж «в долг»/смешанный с неясным исходом (в том числе до перезапуска кассы) —
            // повтор уходит прежним ключом, сервер вернёт прежнюю продажу (replayed). Только для ТОГО ЖЕ чека: у следующего покупателя
            // с таким же составом ключ должен быть новым, иначе его продажа «превратится» в повтор чужой.
            if (_unknownCheckout is { QuickKey: { Length: > 0 } restoredKey } pendingQuick
                && string.Equals(pendingQuick.QuickBody, fingerprint, StringComparison.Ordinal)
                && IsSameCheckAsUnknown(pendingQuick))
            {
                _quickKey = restoredKey;
                _quickKeyMaybeSent = restoredKey;
                PosLogger.Log($"PAY quick: повтор неясной оплаты ({pendingQuick.Method}) тем же ключом {restoredKey}.", "PAYMENT");
            }
            else
                _quickKey = Guid.NewGuid().ToString("N");
            _quickKeyFingerprint = fingerprint;
        }

        var key = _quickKey;
        // 2026-10-29 (приёмка 1.17.65, В-1): ключ и тело — на диск ДО отправки (атомарно, с fsync) для ВСЕХ способов оплаты. Раньше запись была только при
        // неясном исходе: сбой процесса через 150 мс после «Оплатить» не оставлял следов, и повторное «Оплатить» уходило с новым ключом — вторая продажа.
        // Запись снимается при определённом исходе (успех, отказ по существу, чек в очереди с этим ключом).
        MarkQuickInFlight(request, key, fingerprint);
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
        // 2026-10-27 (Б-03 сервера, PRICE_SYNC): строгая проверка цены — сервер не проведёт чек с устаревшей ценой строки (409 price_changed).
        // Сервер без Б-03 заголовок не знает и ведёт себя как раньше.
        var strictPrice = ServerPriceContract.StrictPriceEnabled;
        var keyMaybeSentBefore = string.Equals(_quickKeyMaybeSent, key, StringComparison.Ordinal);
        // 2026-10-30 (приёмка 1.17.66, «Важно» 1): 429 — сервер ЖИВ и просит паузу, а не «молчит». throttled429 — сколько раз подряд ответил 429; maybeProcessed — был ли
        // за эту оплату исход, после которого продажа МОГЛА пройти (таймаут, 5xx…): чистый 429 сервер отклоняет до обработки, продажи нет. deferNow — кассир уже выбрал «Отложить в очередь».
        var throttled429 = 0;
        var busyGiveUp = false;
        var maybeProcessed = keyMaybeSentBefore;
        var deferNow = request.DeferToQueue && IsQueueablePayment(request.PaymentMethod);
        if (deferNow)
            PosLogger.Log($"PAY quick: кассир отложил чек в очередь после «Сервер занят» — на сервер не идём, ключ {key}.", "PAYMENT");
        for (var attempt = 1; attempt <= 3 && !done && !deferNow; attempt++)
        {
            // 409 price_changed верим только на самую первую отправку этого ключа: на повтор после обрыва/таймаута продажа с ним уже могла пройти.
            var priceRefusalIsFinal = !keyMaybeSentBefore && attempt == 1;
            _quickKeyMaybeSent = key;
            try
            {
                // 2026-10-30 (приёмка 1.17.66, «Важно» 1): пауза 429 (щит ждёт её внутри запроса) входит в срок запроса, иначе таймаут срабатывает раньше паузы.
                var attemptTimeout = (attempt == 1 ? timeout : TimeSpan.FromSeconds(Math.Min(15, timeout.TotalSeconds))) + ApiThrottle.RemainingBlock;
                response = await quickApi.PosQuickCheckoutAsync(body, key, attemptTimeout, strictPrice, serverWait).ConfigureAwait(false);
                done = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiException ex) when (priceRefusalIsFinal && ServerPriceContract.IsPriceChanged(ex))
            {
                // 2026-10-27: 409 price_changed — продажа НЕ проведена (сервер ничего не создал). Это не «запрос с этим ключом ещё выполняется»
                // (409 ниже повторяет тем же ключом) и не отказ «в старый путь»: старый путь отправил бы те же устаревшие цены без строгой
                // проверки. Ключ забываем — после обновления цен тело изменится, и это уже другая продажа с новым ключом. Кассир возвращается
                // в окно оплаты (BasketPanelViewModel) с актуальными ценами.
                ForgetQuickKey();
                ClearInFlight(key); // 2026-10-29 (В-1): исход определён — продажи нет
                var changes = ServerPriceContract.ParseMismatch(ex.Payload);
                PosLogger.Log(
                    $"PRICE_SYNC: сервер отклонил чек (409 price_changed) за {sw.ElapsedMilliseconds} мс — продажа не проведена. "
                    + (changes.Count > 0 ? ServerPriceContract.Describe(changes) : "список price_mismatch не пришёл — цены будут запрошены."),
                    "PRICE_SYNC");
                return PosCheckoutResult.PriceChangedFailure(
                    Tr.T("Цена товара изменилась с момента добавления в чек. Продажа НЕ проведена — чек обновляется по актуальным ценам.",
                        "Товардын баасы чекке кошулгандан бери өзгөрдү. Сатуу ӨТКӨРҮЛГӨН ЖОК — чек азыркы баалар боюнча жаңыртылууда.",
                        "The product price has changed since it was added to the receipt. The sale was NOT recorded — the receipt is being updated with the current prices.",
                        "Ürün fiyatı fişe eklendiğinden beri değişti. Satış KAYDEDİLMEDİ — fiş güncel fiyatlarla yenileniyor.",
                        "Mahsulot narxi chekka qo'shilganidan beri o'zgardi. Sotuv O'TKAZILMADI — chek joriy narxlar bo'yicha yangilanmoqda."),
                    changes);
            }
            catch (ApiException ex) when (ex.StatusCode == 500)
            {
                transient = ex;
                maybeProcessed = true;
                PosLogger.Log($"PAY quick: 500 (попытка {attempt}): {DescribeQuickError(ex)}", "WARNING");
                if (++server500 >= 2)
                {
                    // 2026-10-29 (аварийный аудит, п. 3): раньше после двух 500 касса уходила старым путём БЕЗ ключа — если 500 пришёл уже после
                    // записи продажи, получался дубль. Теперь ключ остаётся за чеком: наличные/безнал встают в очередь с этим же ключом (досылка
                    // получит «replayed» или проведёт), «в долг»/смешанная — запись «неясная оплата» на диске (см. блок !done ниже).
                    PosLogger.Log("PAY quick: сервер дважды ответил 500 — не уходим на путь без ключа, чек в очередь/сверку с тем же ключом.", "PAYMENT");
                    break;
                }
            }
            catch (ApiException ex) when (IsQuickTransientStatus(ex.StatusCode))
            {
                transient = ex;
                if (ex.StatusCode == 429)
                {
                    // 2026-10-30 (приёмка 1.17.66, «Важно» 1): сервер отклонил запрос ДО обработки (ограничение частоты) — продажи нет. Срок ответа уже продлён на паузу
                    // (ServerOutageMonitor.NoteThrottled → CheckoutAsync), повтор щит выдержит сам. Второй 429 подряд — не крутим дальше: «Сервер занят», выбор кассира.
                    PosLogger.Log($"PAY quick: 429 «сервер занят» (попытка {attempt}), пауза {ApiThrottle.RemainingBlock.TotalSeconds:0} с, повтор тем же ключом.", "WARNING");
                    if (++throttled429 >= 2)
                    {
                        busyGiveUp = true;
                        break;
                    }
                }
                else
                {
                    maybeProcessed = true;
                    PosLogger.Log($"PAY quick: {ex.StatusCode} (попытка {attempt}), повтор тем же ключом.", "WARNING");
                }
            }
            catch (ApiException ex) when (keyMaybeSentBefore || attempt > 1)
            {
                // 2026-10-29 (аварийный аудит, п. 3): ключ с этим телом уже мог дойти до сервера (прошлый таймаут/обрыв). Отказ 4xx на повтор не
                // доказывает «ничего не создано»: сервер мог проверить остаток/смену раньше ключа, уже после проведённой продажи. Старый путь
                // без ключа создал бы вторую — поэтому как при таймауте: очередь с тем же ключом или запись «неясная оплата».
                transient = ex;
                maybeProcessed = true;
                PosLogger.Log($"PAY quick: отказ {ex.StatusCode} на повтор уже отправленного ключа — на путь без ключа не уходим. {DescribeQuickError(ex)}", "PAYMENT");
                break;
            }
            catch (ApiException ex)
            {
                // Отказ по существу (нет товара, мало остатка, смена не открыта, сессия…). Ничего
                // не создано: при уже проведённом ключе сервер ответил бы 200-повтором.
                ForgetQuickKey();
                ClearInFlight(key); // 2026-10-29 (В-1): исход определён — отказ по существу
                PosLogger.Log(
                    $"PAY quick: отказ {ex.StatusCode} за {sw.ElapsedMilliseconds} мс — старый путь. {DescribeQuickError(ex)}",
                    "PAYMENT");
                return null;
            }
            catch (HttpRequestException ex)
            {
                // Нет сети — повтор сейчас не поможет, очередь дошлёт тем же ключом.
                transient = ex;
                maybeProcessed = true;
                break;
            }
            catch (OperationCanceledException ex)
            {
                // Истёк наш таймаут запроса или окно ожидания оплаты. Продажа могла пройти —
                // повторяем тем же ключом (если есть время) или ставим в очередь с ним же.
                transient = ex;
                maybeProcessed = true;
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
            // 2026-10-29: отказ 4xx на повтор ключа — не авария сервера (сервер жив и ответил), аварию не объявляем.
            // 2026-10-30 (приёмка 1.17.66, «Важно» 1): 429 — тоже не авария: сервер жив, просил паузу. Срок мог выйти ПОСЛЕ паузы 429 (budgetExpired) — это «занят», а не «молчит».
            var throttledOnly = deferNow || busyGiveUp || ApiThrottle.IsThrottled(transient)
                                || (budgetExpired && ApiThrottle.ThrottledSince(_payStartedUtc));
            if (throttledOnly && !deferNow && (IsQueueablePayment(request.PaymentMethod) || !maybeProcessed))
            {
                // Чистый 429 (продажи нет): запись «в полёте» не нужна, ключ остаётся для повтора (кассир выбирает «Повторить» или «Отложить в очередь»).
                if (!maybeProcessed)
                {
                    ClearInFlight(key);
                    _quickKeyMaybeSent = null;
                }
                else if (_unknownCheckout is { InFlight: true, Warned: false, QuickKey: { Length: > 0 } busyKey } busyRecord && string.Equals(busyKey, key, StringComparison.Ordinal))
                {
                    // 2026-10-30 (приёмка 1.17.67, «Важно» 2): срок мог выйти в щите (запрос не уходил), и запись осталась «в полёте». Кассир только что получил вопрос
                    // «Сервер занят… деньги не принимайте» — он предупреждён: повтор/«Отложить» не должны снова показывать «Оплата могла пройти», сверка по ключу решит исход.
                    _unknownCheckout = busyRecord with { Warned = true };
                    PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
                }

                PosLogger.Log($"PAY quick: сервер занят (429) дольше срока оплаты ({sw.ElapsedMilliseconds} мс) — аварию не объявляем, выбор кассира: повторить / отложить в очередь. Ключ {key} сохранён.", "PAYMENT");
                return PosCheckoutResult.ServerBusyFailure(ServerBusyQuestion(IsQueueablePayment(request.PaymentMethod)), IsQueueablePayment(request.PaymentMethod));
            }

            if (!throttledOnly && (transient is not ApiException { StatusCode: >= 400 and < 500 } refused || IsQuickTransientStatus(refused.StatusCode)))
                ServerOutageMonitor.ReportFailure(Tr.T("оплата", "төлөм", "payment", "ödeme", "to'lov"),
                    budgetExpired
                        ? Tr.T($"сервер не ответил за {ServerAnswerBudget.TotalSeconds:0.0} с",
                            $"сервер {ServerAnswerBudget.TotalSeconds:0.0} с ичинде жооп берген жок",
                            $"the server did not answer within {ServerAnswerBudget.TotalSeconds:0.0} s",
                            $"sunucu {ServerAnswerBudget.TotalSeconds:0.0} sn içinde yanıt vermedi",
                            $"server {ServerAnswerBudget.TotalSeconds:0.0} s ichida javob bermadi")
                        : ServerOutageMonitor.Describe(transient ?? new TimeoutException()),
                    hard: true);
            // 2026-10-29 (аварийный аудит, п. 2): «в долг» и смешанную очередь не принимает, а продажа с этим ключом МОГЛА пройти. Раньше кассира
            // отправляли «взять наличные» (вторая продажа + ложный долг), а ключ жил только в памяти. Теперь ключ и тело — на диск, оплата
            // повторяется только тем же составом и тем же ключом (сервер вернёт прежнюю продажу).
            if (!IsQueueablePayment(request.PaymentMethod))
                return SaveUnknownQuickCheckout(request, key, fingerprint);
            var reason = throttledOnly
                ? Tr.T("Сервер был занят (429) — чек отложен в очередь по выбору кассира.", "Сервер бош эмес болчу (429) — чек кассирдин тандоосу боюнча кезекке коюлду.",
                    "The server was busy (429) — the receipt was deferred to the queue at the cashier's choice.", "Sunucu meşguldü (429) — fiş kasiyerin tercihiyle kuyruğa alındı.",
                    "Server band edi (429) — chek kassir tanloviga ko'ra navbatga qo'yildi.")
                : Tr.T("Таймаут оплаты или потеря сети.", "Төлөмдү күтүү убактысы бүттү же тармак үзүлдү.",
                "Payment timed out or the connection was lost.", "Ödeme zaman aşımına uğradı veya ağ bağlantısı koptu.",
                "To'lov vaqti tugadi yoki tarmoq uzildi.");
            var offline = await CompleteOfflineCheckoutAsync(request, cartJson, total, reason, entryId: key, quickAttempted: true)
                .ConfigureAwait(false);
            // Долг и смешанную очередь не принимает — ключ остаётся за чеком: повтор оплаты тем же
            // составом вернёт уже проведённую продажу, а не создаст вторую.
            if (offline.SavedOffline)
            {
                ForgetQuickKey();
                ClearInFlight(key); // 2026-10-29 (В-1): чек в очереди с этим же ключом — очередь сверит исход сама
            }

            return offline;
        }

        // Сервер провёл продажу — окно ожидания больше не нужно (см. CheckoutAsync).
        DisarmServerWait();
        ForgetQuickKey();
        // 2026-10-29: продажа с ключом из записи «неясная оплата» подтверждена сервером.
        // 2026-10-30 (приёмка 1.17.65, «Важно»: двойная продажа при сбое): запись НЕ стираем до сброса корзины — помечаем «оплата подтверждена» и сохраняем на диск.
        // Если процесс упадёт после ответа сервера, но до очистки чека, повторное «Оплатить» не создаст вторую продажу (QuickUnknownGuard покажет «уже прошла»).
        // Запись снимается, когда чек стал другим (жетон исчез/новый) — см. QuickUnknownGuard.
        MarkQuickPaid(request, key, fingerprint);
        // 2026-10-27 (Б-03): сервер мог провести чек и сообщить price_mismatch (цена строки отличалась от текущей). Нет поля — ничего не делаем.
        var priceMismatch = ServerPriceContract.ParseMismatch(response);
        if (priceMismatch.Count > 0)
            PosLogger.Log($"PRICE_SYNC: продажа проведена, но сервер сообщил price_mismatch: {ServerPriceContract.Describe(priceMismatch)}.", "PRICE_SYNC");
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
        if (priceMismatch.Count > 0)
            finished.PriceMismatchNote = ServerPriceContract.MismatchToast(priceMismatch);
        return finished;
    }

    /// <summary>2026-10-29: снимок чека и его итог на момент нажатия «Оплатить» (до скидки окна оплаты) — по ним запись «неясная оплата»
    /// узнаёт «тот же чек» (CheckoutAsync выставляет их в начале).</summary>
    private string _checkoutFallbackJson = "";
    private double _checkoutFallbackTotal;

    /// <summary>2026-10-29: тот же ли чек, что в записи «неясная оплата»: жетон (если есть у обоих), иначе состав и срок — как в
    /// ResolveUnknownCheckoutAsync.</summary>
    private bool IsSameCheckAsUnknown(UnknownCheckout unknown)
    {
        var age = DateTimeOffset.Now - unknown.At;
        if (!string.IsNullOrEmpty(unknown.CheckToken) && !string.IsNullOrEmpty(_checkoutToken))
            return string.Equals(unknown.CheckToken, _checkoutToken, StringComparison.Ordinal);
        return unknown.Signature.Length > 0
               && string.Equals(unknown.Signature, CartSignature(_checkoutFallbackJson, _checkoutFallbackTotal), StringComparison.Ordinal)
               && age <= UnknownCheckoutSameWindow;
    }

    /// <summary>2026-10-29: тело запроса быстрого пути для этой оплаты (как в TryQuickCheckoutAsync); null — быстрый путь чеку не подходит.</summary>
    private string? QuickBodyFingerprint(PosCheckoutRequest request)
    {
        var shiftId = PosApp.ActiveShiftId;
        if (!_cart.HasCart || !Guid.TryParse(shiftId, out _))
            return null;
        return QuickCheckoutBody.TryBuild(
                _cart.GetRawText(), request.PaymentMethod, request.CashReceived, request.NonCashReceived, request.ClientId, shiftId,
                request.ConsultantId, request.ConsultantCommissionEnabled, request.ConsultantCommissionPercent, request.PrintReceipt, out _)
            ?.ToJsonString();
    }

    /// <summary>2026-10-29 (аварийный аудит, п. 2): ответ на «в долг»/смешанную быстрым путём не дошёл. Ключ и тело — на диск; чек остаётся в кассе.</summary>
    private UnknownCheckout BuildQuickRecord(PosCheckoutRequest request, string key, string fingerprint, bool inFlight) =>
        new(
            "quick:" + key, CartSignature(_checkoutFallbackJson, _checkoutFallbackTotal), CartLineParts(_checkoutFallbackJson), _checkoutToken,
            DateTimeOffset.Now, request.PaymentMethod ?? "",
            double.TryParse(request.CashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var cashPart) && cashPart > 0.005,
            request.ClientId, request.CashReceived, request.NonCashReceived, request.ExpectedTotal,
            QuickKey: key, QuickBody: fingerprint, InFlight: inFlight);

    /// <summary>2026-10-29 (В-1): запись «в полёте» — ключ и тело на диск до отправки.</summary>
    private void MarkQuickInFlight(PosCheckoutRequest request, string key, string fingerprint)
    {
        _unknownCheckout = BuildQuickRecord(request, key, fingerprint, inFlight: true);
        PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
    }

    /// <summary>2026-10-30: сервер провёл продажу с этим ключом, а корзина ещё не сброшена — запись остаётся на диске как «оплата подтверждена».</summary>
    private void MarkQuickPaid(PosCheckoutRequest request, string key, string fingerprint)
    {
        if (string.IsNullOrEmpty(_checkoutToken))
        {
            // Без жетона «тот же чек» определяется только по составу и времени — подтверждённой записью можно заблокировать следующего покупателя. Как раньше.
            if (_unknownCheckout is { QuickKey: { Length: > 0 } doneKey } && string.Equals(doneKey, key, StringComparison.Ordinal))
                ClearUnknown();
            return;
        }

        var record = _unknownCheckout is { QuickKey: { Length: > 0 } k } existing && string.Equals(k, key, StringComparison.Ordinal)
            ? existing
            : BuildQuickRecord(request, key, fingerprint, inFlight: false);
        _unknownCheckout = record with { InFlight = false, PaidConfirmed = true };
        PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
    }

    /// <summary>2026-10-29 (В-1): исход определён — запись «в полёте» этого ключа больше не нужна.</summary>
    private void ClearInFlight(string key)
    {
        if (_unknownCheckout is { InFlight: true, QuickKey: { Length: > 0 } k } && string.Equals(k, key, StringComparison.Ordinal))
            ClearUnknown();
    }

    /// <summary>2026-10-29 (В-1): запись «в полёте» осталась с прошлого запуска кассы (или от прерванной оплаты) — сверяем исход по ключу
    /// (api/main/idempotency/{key}/). null — оплату можно продолжать (запись снята либо кассир уже предупреждён, а повтор пойдёт тем же ключом,
    /// поэтому второй продажи не будет). Иначе — отказ с предупреждением кассиру.</summary>
    private async Task<PosCheckoutResult?> ReconcileInFlightAsync(UnknownCheckout record, CancellationToken ct)
    {
        var same = IsSameCheckAsUnknown(record);
        var outcome = IdempotencyOutcome.Unknown;
        if (_salesApi is IPosQuickCheckoutApi lookupApi && !OfflineModeHelper.SellLocally)
        {
            try { outcome = await lookupApi.PosIdempotencyLookupAsync(record.QuickKey!, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { PosLogger.Log($"PAY: сверка прерванной оплаты по ключу {record.QuickKey} не удалась: {ex.Message}", "WARNING"); }
        }

        PosLogger.Log($"PAY: сверка прерванной оплаты, ключ {record.QuickKey}: {outcome}, тот же чек: {same}.", "PAYMENT");

        if (!same)
        {
            // Касса уже перешла к другому чеку: этот чек блокировать нечем, а если продажа прошла — она в истории.
            if (outcome == IdempotencyOutcome.Found)
                PosLogger.Log($"PAY: прерванная оплата (ключ {record.QuickKey}) на сервере проведена — смотрите историю чеков.", "WARNING");
            ClearUnknown();
            return null;
        }

        switch (InFlightReconcile.Decide(outcome, DateTimeOffset.Now - record.At, record.Warned, UnknownCheckoutSettle))
        {
            case InFlightAction.ReportPaid:
                _unknownCheckout = record with { InFlight = false, PaidConfirmed = true };
                PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
                return PosCheckoutResult.Failed(InFlightPaidMessage());
            case InFlightAction.Forget:
                // Сервер знает ключи и этого не знает, запрос давно не может «дорабатываться» — продажи нет; новая попытка пойдёт с новым ключом.
                ClearUnknown();
                return null;
            case InFlightAction.WarnOnce:
                // Исход неясен (старый сервер, нет связи) или запрос ещё мог дорабатываться: кассира предупреждаем один раз, повтор пойдёт
                // ТЕМ ЖЕ ключом и тем же составом (сервер вернёт прежнюю продажу, а не создаст вторую).
                _unknownCheckout = record with { Warned = true };
                PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
                return PosCheckoutResult.Failed(InFlightMaybeMessage());
            default:
                return null;
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.66, «Важно» 1): вопрос кассиру, когда сервер дольше срока отвечает 429.</summary>
    private static string ServerBusyQuestion(bool canQueue) => canQueue
        ? Tr.T("Сервер занят (слишком много запросов), оплата пока не прошла. Деньги пока не принимайте как «оплачено». Повторить оплату сейчас или отложить чек в очередь (он уйдёт на сервер сам, когда тот освободится; чек не потеряется и не задвоится)?",
            "Сервер бош эмес (өтүнмөлөр өтө көп), төлөм азырынча өткөн жок. Азыр кайра аракет кылыңызбы же чекти кезекке койосузбу (сервер бошогондо өзү жөнөтүлөт; чек жоголбойт жана эки жолу өтпөйт)?",
            "The server is busy (too many requests), the payment has not gone through yet. Retry the payment now, or defer the receipt to the queue (it will be sent by itself when the server is free; it will not be lost or duplicated)?",
            "Sunucu meşgul (çok fazla istek), ödeme henüz geçmedi. Ödemeyi şimdi tekrar deneyin mi, yoksa fişi kuyruğa mı alalım (sunucu boşalınca kendiliğinden gider; kaybolmaz ve çift olmaz)?",
            "Server band (so'rovlar juda ko'p), to'lov hali o'tmadi. To'lovni hozir qaytaramizmi yoki chekni navbatga qo'yamizmi (server bo'shaganda o'zi yuboriladi; yo'qolmaydi va ikki marta o'tmaydi)?")
        : Tr.T("Сервер занят (слишком много запросов), оплата пока не прошла. Чек остаётся в кассе. Повторить оплату сейчас?",
            "Сервер бош эмес (өтүнмөлөр өтө көп), төлөм азырынча өткөн жок. Чек кассада калат. Азыр кайра аракет кылыңызбы?",
            "The server is busy (too many requests), the payment has not gone through yet. The receipt stays in the till. Retry the payment now?",
            "Sunucu meşgul (çok fazla istek), ödeme henüz geçmedi. Fiş kasada kalıyor. Ödemeyi şimdi tekrar deneyin mi?",
            "Server band (so'rovlar juda ko'p), to'lov hali o'tmadi. Chek kassada qoladi. To'lovni hozir qaytaramizmi?");

    private static string InFlightPaidMessage() => Tr.T(
        "Оплата этого чека уже прошла на сервере (найдена по ключу) — повторно НЕ пробивайте. Проверьте продажу в «Истории чеков» и очистите корзину.",
        "Бул чектин төлөмү серверде өтүп кеткен (ачкыч боюнча табылды) — кайра ЧЫКПАҢЫЗ. «Чектер тарыхынан» сатууну текшерип, себетти тазалаңыз.",
        "Payment for this receipt has already gone through on the server (found by its key) — do NOT ring it up again. Check the sale in “Receipt history” and clear the cart.",
        "Bu fişin ödemesi sunucuda zaten gerçekleşti (anahtarla bulundu) — tekrar VURMAYIN. «Fiş geçmişi»nde satışı kontrol edin ve sepeti temizleyin.",
        "Bu chekning to'lovi serverda allaqachon o'tgan (kalit bo'yicha topildi) — qayta URMANG. «Cheklar tarixi»da sotuvni tekshiring va savatni tozalang.");

    private static string InFlightMaybeMessage() => Tr.T(
        "Оплата могла пройти — проверьте в «Истории чеков»: касса была закрыта или оплата прервана, пока шла отправка. Если продажи нет, нажмите «Оплатить» ещё раз с теми же данными: касса отправит её прежним ключом и второй продажи не создаст.",
        "Төлөм өтүп кетиши мүмкүн — «Чектер тарыхын» текшериңиз: жөнөтүү учурунда касса жабылды же төлөм үзгүлтүккө учурады. Сатуу жок болсо, «Төлөө» баскычын ошол эле маалыматтар менен кайра басыңыз: касса аны мурунку ачкыч менен жөнөтөт, экинчи сатуу түзүлбөйт.",
        "The payment may have gone through — check “Receipt history”: the till was closed or the payment interrupted while it was being sent. If there is no sale, click “Pay” again with the same details: the till will send it with the same key and will not create a second sale.",
        "Ödeme gerçekleşmiş olabilir — «Fiş geçmişi»ni kontrol edin: gönderim sırasında kasa kapandı veya ödeme kesildi. Satış yoksa «Öde»ye aynı bilgilerle tekrar tıklayın: kasa aynı anahtarla gönderir, ikinci satış oluşturmaz.",
        "To'lov o'tib ketgan bo'lishi mumkin — «Cheklar tarixi»ni tekshiring: yuborish paytida kassa yopildi yoki to'lov to'xtatildi. Sotuv bo'lmasa, «To'lash»ni xuddi shu ma'lumotlar bilan yana bosing: kassa uni oldingi kalit bilan yuboradi, ikkinchi sotuv yaratilmaydi.");

    private PosCheckoutResult SaveUnknownQuickCheckout(PosCheckoutRequest request, string key, string fingerprint)
    {
        _unknownCheckout = BuildQuickRecord(request, key, fingerprint, inFlight: false);
        PendingUnknownCheckoutStore.Save(UnknownToEntry(_unknownCheckout));
        PosLogger.Log($"PAY quick: исход оплаты ({request.PaymentMethod}) неизвестен, ключ {key} сохранён на диск — повтор только этим составом и ключом.", "PAYMENT");
        return PosCheckoutResult.Failed(QuickUnknownMessage());
    }

    /// <summary>2026-10-29: пока исход прошлой оплаты «в долг»/смешанной этого чека не подтверждён, другой способ оплаты и другой состав не
    /// проводим: сервер мог уже записать продажу или долг. <paramref name="quickDeclined"/>: быстрый путь чек не взял (null) — старый путь
    /// без ключа запрещён вовсе. null — оплату можно продолжать.</summary>
    private PosCheckoutResult? QuickUnknownGuard(PosCheckoutRequest request, bool quickDeclined)
    {
        if (_unknownCheckout is not { QuickKey: { Length: > 0 } } unknown || !IsSameCheckAsUnknown(unknown))
        {
            // 2026-10-30: чек стал другим (прошлый оплачен и сброшен) — запись «оплата подтверждена» больше не нужна.
            if (_unknownCheckout is { PaidConfirmed: true, InFlight: false })
                ClearUnknown();
            return null;
        }
        // 2026-10-29 (В-1): сервер подтвердил оплату этого чека по ключу — тот же чек повторно не пробиваем ни при каких данных.
        if (unknown.PaidConfirmed)
            return PosCheckoutResult.Failed(InFlightPaidMessage());
        if (!quickDeclined && string.Equals(unknown.QuickBody, QuickBodyFingerprint(request), StringComparison.Ordinal))
            return null;
        PosLogger.Log(
            $"PAY: чек с неясной оплатой «{unknown.Method}» (ключ {unknown.QuickKey}) — другой способ/состав не проводим, пока исход не подтверждён.", "WARNING");
        return PosCheckoutResult.Failed(unknown.InFlight ? InFlightMaybeMessage() : QuickUnknownMessage());
    }

    private static string QuickUnknownMessage() => Tr.T(
        "Ответ сервера не получен: продажа «в долг» или смешанная могла пройти. НЕ берите наличные и не меняйте чек. Нажмите «Оплатить» ещё раз с теми же данными — касса сверит продажу по ключу и не создаст вторую. Не получается — проверьте раздел «Продажи»: если продажи нет, откройте новый чек и пробейте заново.",
        "Сервердин жообу алынган жок: «карызга» же аралаш сатуу өтүп кетиши мүмкүн. Накталай акча АЛБАҢЫЗ, чекти өзгөртпөңүз. «Төлөө» баскычын ошол эле маалыматтар менен кайра басыңыз — касса сатууну ачкыч боюнча текшерет, экинчисин түзбөйт. Болбосо «Сатуулар» бөлүмүн текшериңиз: сатуу жок болсо, жаңы чек ачып, кайра кириңиз.",
        "No reply from the server: the “on credit” or mixed sale may have gone through. Do NOT take cash and do not change the receipt. Click “Pay” again with the same details — the till will check the sale by its key and will not create a second one. If that fails, check the “Sales” section: if there is no sale, open a new receipt and ring it up again.",
        "Sunucudan yanıt alınmadı: «veresiye» veya karışık satış gerçekleşmiş olabilir. Nakit ALMAYIN ve fişi değiştirmeyin. «Öde» düğmesine aynı bilgilerle tekrar tıklayın — kasa satışı anahtarıyla kontrol eder ve ikincisini oluşturmaz. Olmazsa «Satışlar» bölümüne bakın: satış yoksa yeni fiş açıp yeniden okutun.",
        "Serverdan javob olmadi: «qarzga» yoki aralash sotuv o'tib ketgan bo'lishi mumkin. Naqd pul OLMANG va chekni o'zgartirmang. «To'lash» tugmasini xuddi shu ma'lumotlar bilan yana bosing — kassa sotuvni kalit bo'yicha tekshiradi va ikkinchisini yaratmaydi. Bo'lmasa «Sotuvlar» bo'limini tekshiring: sotuv bo'lmasa, yangi chek ochib qaytadan kiriting.");

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
