using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using MediatR;
using NurMarketKassa.Core.Application.Notifications;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>
/// Общая реализация оплаты POS: онлайн checkout, офлайн-очередь, печать и новый чек.
/// </summary>
public sealed class PosCheckoutService : IPosCheckoutService
{
    private readonly ICartService _cart;
    private readonly ISalesApiService _salesApi;
    private readonly IShiftApiService _shiftApi;
    private readonly IShiftStateService _shiftStateService;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IMediator _mediator;
    private readonly IAutonomousAuthService _autonomous;

    public PosCheckoutService(
        ICartService cart,
        ISalesApiService salesApi,
        IShiftApiService shiftApi,
        IShiftStateService shiftStateService,
        IReceiptPrinterService receiptPrinter,
        IMediator mediator,
        IAutonomousAuthService autonomous)
    {
        _cart = cart;
        _salesApi = salesApi;
        _shiftApi = shiftApi;
        _shiftStateService = shiftStateService;
        _receiptPrinter = receiptPrinter;
        _mediator = mediator;
        _autonomous = autonomous;
    }

    public async Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default) =>
        await PrepareCartForCheckoutAsync(forceMaterialize: false, cancellationToken).ConfigureAwait(false);

    /// <param name="forceMaterialize">Перенести снимок заново, даже если корзина уже на
    /// сервере. Нужно, когда в снимок только что добавили скидку оплаты: иначе она осталась бы
    /// только в кассе, а сервер посчитал бы чек без неё.</param>
    public async Task PrepareCartForCheckoutAsync(bool forceMaterialize, CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            throw new ApiException("Добавьте товары в корзину.", 400);

        if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline)
            return;

        if (forceMaterialize || _cart.IsStaging || ! _cart.CanRefresh)
        {
            PosLogger.Log(
                $"PAY prepare: cart needs materialization (staging={_cart.IsStaging}, canRefresh={_cart.CanRefresh})",
                "PAYMENT");
            await StagingCartService.MaterializeSnapshotOnServerAsync(
                _salesApi,
                _cart,
                PosApp.PosCashboxId,
                cancellationToken,
                force: forceMaterialize).ConfigureAwait(false);
        }
    }

    /// <summary>Гасит то из двух полей скидки, которое мы сейчас НЕ выставляем, если оно
    /// сейчас непустое. Иначе сервер получит корзину, где заданы оба, и упадёт с 500.</summary>
    private async Task ClearOppositeDiscountFieldAsync(
        Dictionary<string, string> discountBody, CancellationToken cancellationToken)
    {
        string opposite;
        if (discountBody.ContainsKey("order_discount_total") && !discountBody.ContainsKey("order_discount_percent"))
            opposite = "order_discount_percent";
        else if (discountBody.ContainsKey("order_discount_percent") && !discountBody.ContainsKey("order_discount_total"))
            opposite = "order_discount_total";
        else
            return;

        // Если противоположное поле и так нулевое — лишний запрос не нужен. Когда прочитать
        // корзину не удалось, гасим на всякий случай: лишний PATCH нулём безвреден, а
        // непогашенное поле роняет оплату целиком.
        if (TryReadDecimal(_cart.Root, opposite, out var current) && Math.Abs(current) < 0.005m)
            return;

        try
        {
            await _salesApi
                .PosCartPatchAsync(_cart.CartId!, new Dictionary<string, string> { [opposite] = "0" }, cancellationToken)
                .ConfigureAwait(false);
            PosLogger.Log($"Скидка на чек: поле {opposite} обнулено перед выставлением второго.", "PAYMENT");
        }
        catch (Exception ex)
        {
            // Не смогли обнулить — пусть основной запрос попробует сам и, если не выйдет,
            // сообщит кассиру. Глушить оплату здесь не за что.
            PosLogger.Log($"Не удалось обнулить {opposite}: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Скидку только что проставила материализация этой же корзины. Это надёжнее
    /// разбора ответа сервера: не зависит от того, под каким именем и в каком виде сервер
    /// вернёт поле в GET-корзине.</summary>
    private bool DiscountJustAppliedOnMaterialize(Dictionary<string, string> discountBody)
    {
        if (StagingCartService.LastAppliedOrderDiscount is not { } applied)
            return false;
        if (!string.Equals(applied.CartId, _cart.CartId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (applied.Body.Count != discountBody.Count)
            return false;

        foreach (var (key, requested) in discountBody)
        {
            if (!applied.Body.TryGetValue(key, out var already))
                return false;
            if (!decimal.TryParse(OrderDiscountHelper.NormalizeDecimal(requested),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var wanted)
                || !decimal.TryParse(OrderDiscountHelper.NormalizeDecimal(already),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var done)
                || Math.Abs(wanted - done) > 0.005m)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Совпадает ли запрошенная скидка с той, что уже лежит в серверной корзине.
    /// Сравниваем ЧИСЛА, а не строки: сервер возвращает «10.00», а касса могла отправить «10».
    /// Если в запросе есть поле, которого в корзине нет (например, сумма после списания
    /// бонусов), считаем, что применять надо — и отправляем.</summary>
    private bool DiscountAlreadyApplied(Dictionary<string, string> discountBody)
    {
        try
        {
            var root = _cart.Root;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var (key, requested) in discountBody)
            {
                if (!TryReadDecimal(root, key, out var applied))
                    return false;
                if (!decimal.TryParse(OrderDiscountHelper.NormalizeDecimal(requested),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var wanted))
                    return false;
                if (Math.Abs(applied - wanted) > 0.005m)
                    return false;
            }

            return discountBody.Count > 0;
        }
        catch (Exception)
        {
            // Не смогли разобрать корзину — ведём себя как раньше и отправляем PATCH.
            return false;
        }
    }

    private static bool TryReadDecimal(JsonElement root, string property, out decimal value)
    {
        value = 0m;
        if (!root.TryGetProperty(property, out var element))
            return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                value = element.GetDecimal();
                return true;
            case JsonValueKind.String:
                return decimal.TryParse(OrderDiscountHelper.NormalizeDecimal(element.GetString() ?? ""),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    public async Task<bool> ApplyOrderDiscountAsync(
        Dictionary<string, string> discountBody,
        CancellationToken cancellationToken = default)
    {
        if (discountBody.Count == 0)
            return true;

        if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline || string.IsNullOrWhiteSpace(_cart.CartId))
        {
            var percent = discountBody.TryGetValue("order_discount_percent", out var pct) ? pct : null;
            var total = discountBody.TryGetValue("order_discount_total", out var sum) ? sum : null;
            ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, percent, total);
            return true;
        }

        // Скидка на чек к этому моменту УЖЕ могла быть проставлена на сервере: материализация
        // корзины (StagingCartService.ApplyOrderDiscountFromSnapshotAsync) переносит её из
        // снимка сразу после выгрузки строк. Повторный PATCH с тем же значением сервер не
        // переживает — отвечает 500, и касса показывает «Проверьте параметры скидки» на
        // совершенно корректной скидке (живой лог владельца 2026-09-22: материализация
        // «discount applied», следом checkout с тем же order_discount_total=10.00 → 500).
        // Поэтому: то, что уже стоит в корзине, повторно не отправляем.
        if (DiscountJustAppliedOnMaterialize(discountBody) || DiscountAlreadyApplied(discountBody))
        {
            PosLogger.Log("Скидка на чек уже проставлена при материализации — повторный PATCH пропущен.", "PAYMENT");
            return true;
        }

        try
        {
            // Сервер хранит скидку на чек В ДВУХ полях и принимает ровно ОДНО из них за раз.
            // Если в корзине уже стоит процент, а мы выставляем сумму (или наоборот), он не
            // отвечает внятной ошибкой — он ПАДАЕТ с 500, и кассир видит «Проверьте параметры
            // скидки» на совершенно нормальной скидке.
            //
            // Живой случай владельца 2026-09-22: кассир поставил 10% на чек, затем списал
            // 5 бонусов. Бонусы серверу неизвестны, поэтому они уходят суммой
            // (order_discount_total=5.00) — а в корзине к этому моменту уже лежит
            // order_discount_percent=10.00. Прочитано прямо с сервера: subtotal 160.00,
            // order_discount_percent 10.00, order_discount_total 0.00.
            //
            // Поэтому противоположное поле сначала гасим ОТДЕЛЬНЫМ запросом: два ключа в одном
            // теле сервер тоже не принимает («Выберите либо фиксированную скидку, либо скидку
            // в процентах») — даже когда второй ключ нулевой.
            await ClearOppositeDiscountFieldAsync(discountBody, cancellationToken).ConfigureAwait(false);

            await _salesApi
                .PosCartPatchAsync(_cart.CartId!, discountBody, cancellationToken)
                .ConfigureAwait(false);
            _cart.SetCart(await _salesApi.PosCartGetAsync(_cart.CartId!, cancellationToken).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex)
        {
            // 2026-09-21: раньше здесь не было видно, ЧТО именно отправили серверу — только
            // текст ответа. Живой случай ("Внутренняя ошибка сервера (500)") без тела запроса в
            // логе невозможно было понять, сервер ли сломался сам по себе или касса прислала
            // некорректное значение (например, скидку больше суммы чека).
            var bodyDump = string.Join(", ", discountBody.Select(kv => $"{kv.Key}={kv.Value}"));
            // 2026-09-21: живой случай с ПРАВИЛЬНЫМ (одно поле) телом всё равно получил 500 —
            // сервер отвечает своим собственным телом ответа (Django обычно кладёт traceback/
            // detail даже в 500), но ApiException.ToString() его не печатает — только Message.
            // Логируем Payload отдельно, иначе при повторе бага снова нечем будет объяснить,
            // ломается сервер сам по себе или ему что-то конкретное не нравится в значении.
            var payloadDump = ex is ApiException { Payload: { } payload } ? payload.GetRawText() : "(нет)";
            PosLogger.Log($"Checkout discount failed: cartId={_cart.CartId}, body=[{bodyDump}], serverPayload={payloadDump}: {ex}", "PAYMENT");
            return false;
        }
    }

    public async Task<PosCheckoutResult> CheckoutAsync(
        PosCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            return PosCheckoutResult.Failed("Добавьте товары в корзину.");

        // Materialization replaces a staging cart before its network work is
        // complete. Keep a recoverable copy for an offline fallback.
        var fallbackCartJson = _cart.GetRawText();
        var fallbackTotal = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;
        // 2026-09-13: захвачено здесь же, до PrepareCartForCheckoutAsync/материализации, по той
        // же причине, что и fallbackCartJson выше — нужен настоящий ID продажи, на который
        // реально был отправлен checkout, а не то, что окажется в _cart ПОСЛЕ возможной
        // перестройки корзины. См. WasCheckoutAlreadyAppliedAsync ниже.
        var fallbackCartId = _cart.CartId;

        try
        {
            // 2026-09-15, по просьбе пользователя ("при зависании или отказе сервера программа
            // должна работать автономно и в фоне делать отправку") — живой случай: сервер не
            // падал с ошибкой, а просто ОЧЕНЬ долго отвечал на каждый шаг подготовки корзины
            // (sales/start/ensure-empty/пуш товаров) — 14-40+ секунд вместо обычных сотен
            // миллисекунд, оставаясь в пределах общего 55с таймаута HttpClient. Раньше касса
            // в этом случае просто молча ждала до конца вместо того, чтобы раньше уйти в уже
            // существующий безопасный офлайн-режим (см. catch (TaskCanceledException) ниже —
            // он уже умеет проверять WasCheckoutAlreadyAppliedAsync и не задваивать продажу).
            // Свой, более короткий бюджет времени именно на ЭТАП ПОДГОТОВКИ — чтобы при заметно
            // замедлившемся сервере кассир не ждал минуту+ за один чек, а получал управление
            // (локально/офлайн для наличных и карты — долг офлайн всё равно недоступен, честно
            // сообщит об этом дальше по коду) заметно раньше.
            // Скидку, выбранную в окне оплаты (в том числе списанные бонусы), кладём В СНИМОК
            // ЧЕКА ДО переноса корзины на сервер. Тогда она уезжает тем же путём, что и обычная
            // скидка, — внутри материализации (StagingCartService.ApplyOrderDiscountFromSnapshotAsync),
            // который годами работает.
            //
            // Раньше она отправлялась ОТДЕЛЬНЫМ запросом уже ПОСЛЕ переноса, и именно этот
            // запрос сервер стабильно заваливал с 500 («Оплата не прошла. Проверьте параметры
            // скидки»). Проверено 2026-09-22: то же самое поле тем же значением на ту же
            // корзину через API проходит с 200 — то есть запрос кассы корректен, а падает
            // сервер. Почему падает именно вызов из оплаты — со стороны кассы не видно, но
            // обходить его целиком надёжнее, чем угадывать причину чужой ошибки.
            //
            // Обычная скидка (не тронутая в окне оплаты) сюда не попадает: у неё
            // OrderDiscountBody пуст, потому что она уже лежит в снимке.
            //
            // ВАЖНО: в снимок скидка пишется ВСЕГДА, в том числе офлайн. Раньше здесь стояла
            // проверка «только если работаем с сервером», и офлайн-чек уходил в очередь БЕЗ
            // скидки, хотя сумма наличных в нём была уже уменьшена на неё. При выгрузке сервер
            // складывал позиции по полной цене и отвечал «Сумма, полученная наличными, меньше
            // суммы продажи» — чек навсегда оседал в «Некорректных чеках». Живой случай
            // 2026-09-22: три чека подряд, все с оплатой бонусами.
            var discountForSnapshot = request.OrderDiscountBody;
            var discountWentIntoSnapshot = false;
            if (discountForSnapshot is { Count: > 0 })
            {
                var snapshotPercent = discountForSnapshot.TryGetValue("order_discount_percent", out var pct) ? pct : null;
                var snapshotTotal = discountForSnapshot.TryGetValue("order_discount_total", out var sum) ? sum : null;
                ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, snapshotPercent, snapshotTotal);

                // Флаг влияет только на серверный путь: он говорит «отдельный запрос скидки
                // больше не нужен». Офлайн туда не идёт вовсе, поэтому его не поднимаем.
                discountWentIntoSnapshot = !OfflineModeHelper.UseLocalOperations && !_cart.IsLocalOffline;
            }

            using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            prepareCts.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                // Корзина уже на сервере (например, это повтор после неудачной попытки) —
                // переносим заново, иначе добавленная в снимок скидка туда не доедет.
                await PrepareCartForCheckoutAsync(discountWentIntoSnapshot, prepareCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Подготовка чека заняла слишком долго — сервер отвечает медленно.");
            }

            if (!discountWentIntoSnapshot
                && request.OrderDiscountBody != null
                && !await ApplyOrderDiscountAsync(request.OrderDiscountBody, cancellationToken).ConfigureAwait(false))
            {
                return PosCheckoutResult.Failed(PaymentErrorMessages.DiscountFailure);
            }

            // Capture the authoritative receipt only after server refresh and
            // after the final discount selected in the payment dialog.
            var cartJsonSnapshot = _cart.GetRawText();
            var total = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;

            if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline)
            {
                if (string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
                    return PosCheckoutResult.Failed(
                        "Продажа «в долг» недоступна офлайн — нужна связь с сервером.");

                if (string.Equals(request.PaymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
                    return PosCheckoutResult.Failed(
                        "Смешанная оплата недоступна офлайн — нужна связь с сервером.");

                return await CompleteOfflineCheckoutAsync(request, cartJsonSnapshot, total)
                    .ConfigureAwait(false);
            }

            return await CompleteOnlineCheckoutAsync(request, cartJsonSnapshot, total, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            PaymentErrorMessages.Log("Checkout API error", ex);
            RestoreCartAfterFailedCheckout(fallbackCartJson);

            // 2026-09-14, живой баг: "cashbox_id: Касса не найдена или не принадлежит этому
            // филиалу" повторялся даже после того, как PosApp.PosCashboxId стал сверяться со
            // списком касс компании при входе (MainWindow.RefreshShiftStateAsync) — этот список
            // (ConstructionCashboxesListAsync) не фильтруется по филиалу, поэтому касса из
            // ДРУГОГО филиала той же компании проходила проверку "есть в списке" и оставалась
            // выбранной навсегда. Сервер — единственный, кто реально знает правильную привязку
            // к филиалу, поэтому при ЭТОЙ конкретной ошибке сбрасываем и заново подбираем ID
            // кассы прямо здесь, а не полагаемся на клиентскую сверку списком.
            if (IsCashboxRejectedError(ex.Message))
            {
                var reassigned = await TryReassignCashboxAsync(cancellationToken).ConfigureAwait(false);
                return PosCheckoutResult.Failed(reassigned
                    ? "Касса была переназначена (старая не подходит для вашего филиала). Нажмите «Оплатить» ещё раз."
                    : "Ни одна касса компании не подходит для вашего филиала. Обратитесь к администратору NurCRM — " +
                      "проверьте привязку кассы к филиалу в веб-версии.");
            }

            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
        catch (HttpRequestException ex)
        {
            PosLogger.Log($"Checkout network error, saving offline: {ex}", "PAYMENT");
            if (await WasCheckoutAlreadyAppliedAsync(fallbackCartId).ConfigureAwait(false))
                return CompleteAlreadyAppliedCheckout(fallbackCartJson, fallbackTotal);
            var fallback = BuildOfflineFallback(fallbackCartJson, fallbackTotal, request.OrderDiscountBody);
            return await CompleteOfflineCheckoutAsync(request, fallback.CartJson, fallback.Total, ex.Message)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PosLogger.Log("Checkout canceled by caller.", "DEBUG");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            PosLogger.Log($"Checkout HTTP timeout: {ex.GetType().Name}", "WARNING");
            // 2026-09-13, живой баг: таймаут ответа НЕ означает, что checkout не выполнился —
            // запрос мог реально дойти и провестись на сервере, просто ответ не успел вернуться.
            // Раньше касса в любом случае считала это провалом: списывала остаток локально ЕЩЁ
            // РАЗ (сервер его уже списал) и ставила чек в офлайн-очередь — при восстановлении
            // связи очередь реплеится ЧЕРЕЗ /pos/sales/start/, создавая СОВЕРШЕННО НОВУЮ продажу
            // (не тот же ID) поверх уже прошедшей — задвоенный чек и задвоенное списание остатка.
            // Перед тем как считать checkout неудавшимся, спрашиваем у сервера настоящий статус
            // ЭТОЙ ЖЕ продажи по её ID (WasCheckoutAlreadyAppliedAsync) — если он уже не "new",
            // checkout прошёл, и втоое списание/реплей делать нельзя.
            if (await WasCheckoutAlreadyAppliedAsync(fallbackCartId).ConfigureAwait(false))
                return CompleteAlreadyAppliedCheckout(fallbackCartJson, fallbackTotal);
            var fallback = BuildOfflineFallback(fallbackCartJson, fallbackTotal, request.OrderDiscountBody);
            return await CompleteOfflineCheckoutAsync(
                    request, fallback.CartJson, fallback.Total, "Таймаут оплаты или потеря сети.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PaymentErrorMessages.Log("Checkout unexpected error", ex);
            RestoreCartAfterFailedCheckout(fallbackCartJson);
            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
    }

    /// <summary>
    /// MaterializeSnapshotOnServerAsync clears the cart before its network work
    /// completes; a non-network failure must not leave the cashier staring at an
    /// empty receipt after they already rang up a full basket.
    /// </summary>
    private void RestoreCartAfterFailedCheckout(string fallbackCartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(fallbackCartJson) ? "{}" : fallbackCartJson);
            _cart.SetCart(doc.RootElement);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout cart restore after failure skipped: {ex}", "PAYMENT");
        }
    }

    public async Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(PosApp.ActiveShiftId))
            {
                _cart.ResetForNewReceipt();
                return "Новый чек не открыт: смена не открыта. Откройте смену и нажмите «Новый чек».";
            }

            if (OfflineModeHelper.UseLocalOperations)
            {
                LocalCartService.StartNewLocalCart(_cart);
                return null;
            }

            var serverCart = await _salesApi
                .PosSalesStartAsync(PosApp.PosCashboxId, cancellationToken)
                .ConfigureAwait(false);
            _cart.SetCart(serverCart);

            // Defensive: a freshly started sale must never inherit the previous
            // receipt's order-level discount. Some server responses for
            // "start sale" have been observed to echo it back from cashbox state.
            ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, null, null);
            return null;
        }
        catch (ApiException ex) when (OfflineModeHelper.CanOperateWithoutServer)
        {
            PosLogger.Log($"Restart sale offline after API error: {ex}", "PAYMENT");
            LocalCartService.StartNewLocalCart(_cart);
            return null;
        }
        catch (HttpRequestException ex) when (OfflineModeHelper.CanOperateWithoutServer)
        {
            LocalCartService.StartNewLocalCart(_cart);
            PosLogger.Log($"Restart sale offline after network error: {ex}", "PAYMENT");
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Restart sale failed: {ex}", "PAYMENT");
            return ex.Message;
        }
    }

    /// <summary>Спрашивает у сервера, не провёлся ли ЭТОТ ЖЕ checkout, несмотря на то что клиент
    /// не дождался ответа (см. катч-блоки CheckoutAsync выше). GET /pos/sales/{id}/ (тот же
    /// эндпоинт, что уже использует диалог возврата) возвращает status: один из
    /// new/paid/debt/canceled/partially_returned — "new" держится только пока продажа не
    /// оплачена, поэтому любой другой статус однозначно значит "checkout уже применился".
    /// Короткий собственный таймаут и любая ошибка здесь — false (обычное поведение, offline-
    /// очередь как раньше): связь и так плохая, вешать кассира на вторую долгую попытку не
    /// стоит — риск "не узнали, что успело пройти" гораздо безопаснее риска задвоить продажу.</summary>
    private async Task<bool> WasCheckoutAlreadyAppliedAsync(string? cartId)
    {
        if (string.IsNullOrWhiteSpace(cartId))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sale = await _salesApi.PosSaleGetAsync(cartId, cts.Token).ConfigureAwait(false);
            if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("status", out var statusEl)
                || statusEl.ValueKind != JsonValueKind.String)
                return false;

            var alreadyApplied = !string.Equals(statusEl.GetString(), "new", StringComparison.OrdinalIgnoreCase);
            PosLogger.Log($"Checkout reconciliation for {cartId}: status={statusEl.GetString()}, alreadyApplied={alreadyApplied}", "PAYMENT");
            return alreadyApplied;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout reconciliation check failed for {cartId}: {ex}", "PAYMENT");
            return false;
        }
    }

    /// <summary>Sale уже проведена на сервере (см. WasCheckoutAlreadyAppliedAsync) — НЕ трогаем
    /// остаток (сервер его уже списал) и НЕ ставим в офлайн-очередь (реплей создал бы новую,
    /// дублирующую продажу): просто открываем кассиру новый чек локально, т.к. в этот момент
    /// связь всё ещё под вопросом (мы здесь именно потому, что предыдущий запрос не дождались) —
    /// обычный серверный RestartSaleSessionAsync рискует так же зависнуть.</summary>
    private PosCheckoutResult CompleteAlreadyAppliedCheckout(string cartJsonSnapshot, double total)
    {
        LocalCartService.StartNewLocalCart(_cart);
        return PosCheckoutResult.Succeeded(
            total,
            cartJsonSnapshot,
            info: "Оплата уже прошла на сервере (ответ не успел дойти вовремя) — повторно не проводим. " +
                  "Чек можно распечатать ещё раз из раздела «Продажи».");
    }

    /// <summary>Узнаёт именно ту ошибку сервера ("cashbox_id: Касса не найдена или не
    /// принадлежит этому филиалу"), а не любую ApiException — иначе, например, обычное "нет
    /// связи" тоже пыталось бы переподобрать кассу без нужды.</summary>
    private static bool IsCashboxRejectedError(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.Contains("cashbox", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не принадлежит", StringComparison.OrdinalIgnoreCase));

    /// <summary>Заново запрашивает список касс и выбирает первую доступную, отличную от той,
    /// что сервер только что отверг — сам список не фильтруется по филиалу (см. комментарий
    /// в catch(ApiException) выше), поэтому единственный надёжный сигнал "эта касса не
    /// подходит" — реальный отказ сервера, а не клиентская сверка списком.
    ///
    /// Возвращает false, когда кандидатов не осталось (все кассы компании уже отвергнуты за
    /// эту сессию) — 2026-09-14, живой баг: "каждый раз при продаже такое выходит!" — раньше
    /// в этом случае PosCashboxId просто оставался как есть (последней ОТВЕРГНУТОЙ кассой),
    /// следующая продажа отправляла ЕЁ ЖЕ, сервер отвергал её снова, и кассир видел одно и то
    /// же "переназначено, нажмите ещё раз" бесконечно. Возврат false даёт вызывающему коду
    /// показать другое, честное сообщение вместо повторения того же самого.</summary>
    private async Task<bool> TryReassignCashboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rejectedId = PosApp.PosCashboxId;
            if (!string.IsNullOrWhiteSpace(rejectedId))
                PosApp.RejectedCashboxIds.Add(rejectedId);

            var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);
            var candidates = CartDisplayHelper.ListCashboxes(rawList)
                .Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id))
                .ToList();

            var next = candidates.FirstOrDefault(c => c.IsActive);
            if (next.Id == null)
                next = candidates.FirstOrDefault();

            if (next.Id == null)
            {
                PosLogger.Log("Cashbox reassignment: no alternative (unrejected) cashbox found in list.", "PAYMENT");
                return false;
            }

            PosApp.PosCashboxId = next.Id;
            PosApp.PosCashboxDisplayName = next.DisplayName;
            PosLogger.Log($"Cashbox reassigned after server rejection: {rejectedId} -> {next.Id}", "PAYMENT");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cashbox reassignment failed: {ex}", "PAYMENT");
            return false;
        }
    }

    private async Task<PosCheckoutResult> CompleteOfflineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        string? reason = null)
    {
        // Проверка стоит здесь, в единственной точке постановки чека в очередь, а не только на
        // ветке «мы заранее знаем, что офлайн». В очередь чек попадает ещё двумя путями — из
        // catch(HttpRequestException) и catch(TaskCanceledException), — и там этой проверки не
        // было: долг и смешанная оплата проскакивали в очередь, а повтор не умеет ни привязать
        // клиента к долгу, ни передать безналичную часть (её просто негде хранить), поэтому
        // смешанный чек 3000 нал + 7000 карта выгрузился бы как 3000.
        if (string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            return PosCheckoutResult.Failed(
                "Продажа «в долг» недоступна без связи с сервером. Повторите оплату, когда появится интернет.");

        if (string.Equals(request.PaymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
            return PosCheckoutResult.Failed(
                "Смешанная оплата недоступна без связи с сервером. Повторите оплату, когда появится интернет.");

        var isAutonomous = _autonomous.IsCurrentSessionAutonomous;
        var entry = new OfflineSaleEntry
        {
            PaymentMethod = request.PaymentMethod ?? "",
            CashReceived = request.CashReceived,
            CartJson = cartJsonSnapshot,
            CartId = _cart.CartId,
            ShiftId = PosApp.ActiveShiftId,
            BranchId = PosApp.AuthApi.ActiveBranchId,
            CashboxId = PosApp.PosCashboxId,
            IsAutonomous = isAutonomous,
        };

        OfflinePendingSalesStore.Append(entry);
        ApplyOfflineStockDecrement(cartJsonSnapshot);

        // Ящик открывается и в офлайне тоже: наличные кассир берёт независимо от того, дошла ли
        // продажа до сервера. Не завязано на PrintReceipt — ящик нужен и когда чек не печатают.
        ReceiptPrintService.TryOpenCashDrawerAfterSale(request.PaymentMethod, request.CashReceived);

        var printed = request.PrintReceipt && await TryPrintReceiptAsync(
            cartJsonSnapshot,
            request.PaymentMethod,
            request.CashReceived,
            offlineNote: isAutonomous ? "АВТОНОМНЫЙ РЕЖИМ" : "ОФФЛАЙН (ожидает выгрузку)").ConfigureAwait(false);

        // The completed receipt must disappear before the cashier can start
        // another operation; a delayed background reset could erase new items.
        LocalCartService.StartNewLocalCart(_cart);

        // 2026-09-10: автономная продажа никуда не "выгружается" (нет сервера/аккаунта, на
        // который выгружать) — "В очереди: N" тут вводит в заблуждение, как будто чек чего-то
        // ждёт. Обычный офлайн-режим (временная потеря связи с NurCRM) ниже не тронут.
        var info = isAutonomous
            ? "Оплата сохранена (автономный режим)."
            : reason != null
                ? $"Оплата сохранена локально ({reason}). В очереди: {OfflinePendingSalesStore.PendingCount}."
                : $"Оплата сохранена локально. В очереди: {OfflinePendingSalesStore.PendingCount}.";

        if (request.PrintReceipt && !printed)
            info += " Продажа сохранена, но чек не напечатан; используйте повторную печать.";

        return PosCheckoutResult.OfflineSaved(
            total,
            cartJsonSnapshot,
            info,
            request.PrintReceipt,
            printed);
    }

    private async Task<PosCheckoutResult> CompleteOnlineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        CancellationToken cancellationToken)
    {
        var cartId = _cart.CartId;
        if (string.IsNullOrWhiteSpace(cartId))
            return PosCheckoutResult.Failed("Корзина не привязана к серверу. Начните продажу заново.");

        var body = BuildCheckoutRequestBody(
            request.PaymentMethod, request.CashReceived, request.PrintReceipt, request.ClientId, request.NonCashReceived);
        var checkoutIds = CartDisplayHelper.CollectCheckoutTargetIds(_cart.Root, cartId);

        PosLogger.Log(
            $"Checkout API: ids=[{string.Join(", ", checkoutIds)}], method={body.GetValueOrDefault("payment_method")}, " +
            $"cash={body.GetValueOrDefault("cash_received")}, transfer={body.GetValueOrDefault("transfer_received")}, " +
            $"shift={body.GetValueOrDefault("shift_id")}, " +
            $"cashbox={body.GetValueOrDefault("cashbox_id")}, client={body.GetValueOrDefault("client_id")}",
            "PAYMENT");

        JsonElement checkoutResponse;
        try
        {
            checkoutResponse = await _salesApi
                .PosCheckoutAsync(checkoutIds, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex) when (IsRecoverableCheckoutCartError(ex))
        {
            PosLogger.Log(
                $"Checkout cart is stale or empty; rebuilding server cart once. " +
                $"Status={ex.StatusCode}, old ids=[{string.Join(", ", checkoutIds)}]",
                "PAYMENT");

            await StagingCartService.MaterializeSnapshotOnServerAsync(
                    _salesApi,
                    _cart,
                    PosApp.PosCashboxId,
                    cancellationToken,
                    force: true)
                .ConfigureAwait(false);

            var recoveredCartId = _cart.CartId;
            if (string.IsNullOrWhiteSpace(recoveredCartId))
                throw;

            var recoveredIds = CartDisplayHelper.CollectCheckoutTargetIds(_cart.Root, recoveredCartId);
            PosLogger.Log(
                $"Checkout retry after cart rebuild: ids=[{string.Join(", ", recoveredIds)}]",
                "PAYMENT");
            checkoutResponse = await _salesApi
                .PosCheckoutAsync(recoveredIds, body, cancellationToken)
                .ConfigureAwait(false);
        }

        CheckoutResponseHelper.FormatSuccess(checkoutResponse);

        var saleId = CheckoutResponseHelper.TrySaleId(checkoutResponse) ?? cartId;
        PosLogger.Log($"Checkout API OK: saleId={saleId}", "PAYMENT");

        // 2026-09-15, живой баг ("Максимум: 5.33" — совершенно одинаковое число при трёх разных
        // клиентах/сменах/суммах подряд): сразу после создания продажи «в долг» сумма остатка по
        // новой сделке клиента на сервере ЕЩЁ НЕ ПОСЧИТАНА (похоже на асинхронный пересчёт на
        // стороне сервера) — единственная повторная попытка через 1.5с (первая версия этого фикса)
        // тоже упала с той же самой "5.33" оба раза подряд, то есть 1.5с server'у не хватает.
        // Ждать неизвестно сколько секунд, блокируя кассира на экране оплаты — плохой UX; поэтому
        // сама попытка довнесения полностью уходит в фон (кассир получает "оплата прошла" сразу
        // же), а кассиру просто честно объясняем, что частичная оплата досчитывается сервером.
        var debtPartialAmountRequested =
            string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(request.CashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var debtPartialAmount)
            && debtPartialAmount > 0.005;
        if (debtPartialAmountRequested)
        {
            var saleIdForDebtPayment = saleId;
            var cashReceivedForDebtPayment = request.CashReceived;
            _ = Task.Run(() => TryApplyInitialDebtPaymentAsync(
                saleIdForDebtPayment, cashReceivedForDebtPayment, CancellationToken.None));
        }

        // Сервер возвращает номер продажи только в отдельном поле ответа checkout, а не внутри
        // самой корзины. Кладём его в снимок корзины, чтобы печать чека и повторный предпросмотр
        // после оплаты могли найти "Чек №" через тот же TryReceiptNumber, что уже используется
        // в Продажах/Финансах для поиска receipt_number/sale_id/order_id.
        if (!string.IsNullOrWhiteSpace(saleId))
        {
            var enrichedCart = CartJsonHelper.ParseObjectOrEmpty(cartJsonSnapshot);
            enrichedCart["sale_id"] = saleId;
            cartJsonSnapshot = enrichedCart.ToJsonString();
        }

        var cartSnapshot = _cart.Root.Clone();
        try
        {
            await PublishSaleFinalizedAsync(saleId, cartSnapshot).ConfigureAwait(false);
            PosLogger.Log("Checkout stock commit published", "PAYMENT");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Checkout already succeeded on the server. A local projection failure
            // must never invite the cashier to charge the same sale again.
            PosLogger.Log($"Checkout stock commit skipped after successful sale: {ex}", "STOCK");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StockSyncService.RefreshSoldItemsStockAsync(cartSnapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Background stock refresh failed: {ex}", "STOCK");
            }
        });

        try
        {
            PosApp.AuditDb.LogSale(saleId, total, request.PaymentMethod ?? "", PosApp.CurrentUserId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout audit skipped after successful sale: {ex}", "AUDIT");
        }

        ReceiptPrintService.TryOpenCashDrawerAfterSale(request.PaymentMethod, request.CashReceived);

        var printed = request.PrintReceipt && await TryPrintReceiptAsync(
            cartJsonSnapshot,
            request.PaymentMethod,
            request.CashReceived,
            checkoutResponse: checkoutResponse).ConfigureAwait(false);

        // Чек уже оплачен и напечатан — кассир должен увидеть "готово" СЕЙЧАС, а не ждать ещё
        // два сетевых похода подряд (полный список смен + sales/start) просто чтобы завести
        // пустую корзину заранее (2026-09-05, по просьбе пользователя: "оплату делай
        // мгновенно, а на сервер если есть сеть отправляй фоном" — раньше это было самой
        // тяжёлой частью оплаты, api/construction/shifts/ отдаёт ВЕСЬ список смен магазина).
        // ResetForNewReceipt() — тот же самый локальный, без обращения к серверу, приём,
        // которым по всему проекту уже открывают новый чек (кнопка "Новый чек" и т.п.);
        // корзина полностью рабочая сразу и сама зарегистрируется на сервере при следующей
        // реальной оплате (см. PrepareCartForCheckoutAsync → StagingCartService.
        // MaterializeSnapshotOnServerAsync — тот же механизм годами работает для отложенных
        // чеков), так что явный вызов sales/start здесь ничего не даёт, кроме задержки.
        _cart.ResetForNewReceipt();
        PosLogger.Log("Checkout: next receipt started locally, server registration deferred.", "PAYMENT");
        _ = Task.Run(() => RefreshShiftStateInBackgroundAsync());

        var info = debtPartialAmountRequested
            ? "Продажа оформлена в долг. Частичная оплата досчитывается сервером в фоне — если через пару минут долг клиента не уменьшится, введите оплату вручную через «Оплата долга»."
            : request.PrintReceipt && !printed
                ? "Оплата выполнена, но чек не напечатан; используйте повторную печать."
                : null;

        return PosCheckoutResult.Succeeded(
            total,
            cartJsonSnapshot,
            checkoutResponse,
            info,
            request.PrintReceipt,
            printed);
    }

    /// <summary>Проверка "не закрылась ли смена удалённо" после оплаты — раньше блокировала
    /// завершение оплаты, пока не придёт ответ на api/construction/shifts/ (весь список смен
    /// магазина). Теперь чисто фоновая и ничего не возвращает кассиру: если смена правда
    /// закрылась, это всё равно всплывёт на следующей реальной оплате (серверу нечего будет
    /// подставить в shift_id) — небольшая задержка обнаружения ради мгновенной оплаты во всех
    /// остальных случаях.</summary>
    private async Task RefreshShiftStateInBackgroundAsync()
    {
        try
        {
            await _shiftStateService.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrEmpty(PosApp.ActiveShiftId))
                PosLogger.Log("Post-checkout background check: смена закрыта.", "PAYMENT");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Post-checkout background shift refresh failed: {ex}", "PAYMENT");
        }
    }

    /// <summary>Продажа с payment_method=debt на сервере всегда уходит в долг ЦЕЛИКОМ —
    /// поле cash_received в теле checkout сервер для этого случая игнорирует (кассир вводит
    /// "получено сейчас" 100 из 200, но в долг всё равно уходит 200). Реальный, рабочий способ
    /// сразу частично погасить только что созданный долг — тот же эндпоинт, что использует
    /// "Оплата долга" (clientdeals/{deal_id}/pay/, найден через DevTools в браузере): сразу
    /// после успешного создания продажи довносим эту сумму отдельным вызовом (теперь фоновым —
    /// см. вызов в CheckoutAsync — и с длинной серией повторов, см. комментарий ниже). Если все
    /// попытки не удались — продажа всё равно уже проведена, просто откатывать/спорить с кассиром
    /// не из-за чего, поэтому только логируем.
    /// Возвращает null, если частичная оплата не запрашивалась (получено сейчас = 0 — обычная
    /// продажа в долг целиком); true/false — была ли она реально применена.</summary>
    private async Task<bool?> TryApplyInitialDebtPaymentAsync(
        string saleId, string? cashReceived, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return null;

        if (!double.TryParse(cashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0.005)
            return null;

        try
        {
            var sale = await _salesApi.PosSaleGetAsync(saleId, cancellationToken).ConfigureAwait(false);
            var dealId = sale.ValueKind == JsonValueKind.Object
                && sale.TryGetProperty("deal_id", out var dealIdEl)
                && dealIdEl.ValueKind == JsonValueKind.String
                    ? dealIdEl.GetString()
                    : null;
            var clientId = sale.ValueKind == JsonValueKind.Object
                && sale.TryGetProperty("client", out var clientIdEl)
                && clientIdEl.ValueKind == JsonValueKind.String
                    ? clientIdEl.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(dealId) || string.IsNullOrWhiteSpace(clientId))
            {
                PosLogger.Log(
                    $"Initial debt payment skipped: missing deal_id/client on sale {saleId}.", "PAYMENT");
                return false;
            }

            // 2026-09-15, живой баг: изначально здесь звали PosPayDebtAsync по СТАРОМУ (неверному)
            // плоскому адресу api/main/clientdeals/{dealId}/pay/, потом — по верному вложенному
            // (api/main/clients/{clientId}/deals/{dealId}/pay/), но БЕЗ installment_id — оба раза
            // сервер отвечал 200, но отклонял сумму с "Максимум: <копейки>", ни разу не совпадавшим
            // с реальным остатком (5.33 / 5.00 / 1.41 подряд на разных клиентах/суммах). Причина
            // нашлась только после точного захвата DevTools реальной успешной оплаты: долг в
            // NurCRM хранится как график ПЛАТЕЖЕЙ-ВЗНОСОВ (installments), и оплата обязательно
            // идёт по конкретному installment_id — берём его из сделки (ClientDealGetAsync) заново
            // на каждой попытке (не один раз до цикла), т.к. график может ещё не быть готов сразу
            // после checkout.
            var delays = new[] { 3, 8, 15, 30 };
            for (var attempt = 1; attempt <= delays.Length + 1; attempt++)
            {
                try
                {
                    var deal = await _salesApi.ClientDealGetAsync(clientId, dealId, cancellationToken).ConfigureAwait(false);
                    var installmentId = TryFindPayableInstallmentId(deal);
                    await _salesApi.PosPayDebtAsync(clientId, dealId, installmentId, amount, cancellationToken).ConfigureAwait(false);
                    PosLogger.Log(
                        $"Initial debt payment applied: sale={saleId}, deal={dealId}, installment={installmentId ?? "(none)"}, amount={amount:0.00}, attempt={attempt}",
                        "PAYMENT");
                    return true;
                }
                catch (Exception ex) when (attempt <= delays.Length)
                {
                    var delaySeconds = delays[attempt - 1];
                    PosLogger.Log(
                        $"Initial debt payment attempt {attempt} failed for sale {saleId}, retrying in {delaySeconds}s: {ex.Message}",
                        "PAYMENT");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            // Продажа уже проведена на сервере — только логируем, не проваливаем checkout.
            PosLogger.Log($"Initial debt payment failed for sale {saleId}: {ex}", "PAYMENT");
            return false;
        }
    }

    /// <summary>2026-09-15: подтверждено живым захватом DevTools реального успешного платежа
    /// (200 OK) в веб-CRM — долг в NurCRM хранится как график ПЛАТЕЖЕЙ-ВЗНОСОВ (installments)
    /// внутри сделки, и оплата всегда идёт по конкретному installment_id, а не "по сделке в
    /// целом". Возвращает id первого ещё не погашенного взноса (paid_on пуст) — продажи из кассы
    /// создаются без явного графика, поэтому у сделки обычно один взнос на всю сумму.</summary>
    private static string? TryFindPayableInstallmentId(JsonElement deal)
    {
        if (deal.ValueKind != JsonValueKind.Object
            || !deal.TryGetProperty("installments", out var installments)
            || installments.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var installment in installments.EnumerateArray())
        {
            if (installment.ValueKind != JsonValueKind.Object)
                continue;

            if (installment.TryGetProperty("paid_on", out var paidOn)
                && paidOn.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(paidOn.GetString()))
                continue;

            if (installment.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(idEl.GetString()))
                return idEl.GetString();
        }

        return null;
    }

    private static bool IsRecoverableCheckoutCartError(ApiException exception)
    {
        if (exception.StatusCode == 404)
            return true;

        if (exception.StatusCode != 400 || string.IsNullOrWhiteSpace(exception.Message))
            return false;

        return exception.Message.Contains("пуст", StringComparison.OrdinalIgnoreCase)
               && (exception.Message.Contains("корзин", StringComparison.OrdinalIgnoreCase)
                   || exception.Message.Contains("cart", StringComparison.OrdinalIgnoreCase));
    }

    private async Task PublishSaleFinalizedAsync(string saleId, JsonElement cartSnapshot)
    {
        var saleLines = CartDisplayHelper.EnumerateItems(cartSnapshot)
            .Select(it =>
            {
                var productId = CartDisplayHelper.TryProductId(it);
                var qty = CartDisplayHelper.LineQuantity(it);
                return string.IsNullOrEmpty(productId) ? null : new CartLineDto(productId, qty);
            })
            .Where(line => line != null)
            .Cast<CartLineDto>()
            .ToList();

        if (saleLines.Count > 0)
        {
            await _mediator.Publish(new SaleFinalizedNotification(saleId, saleLines), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> BuildCheckoutRequestBody(
        string paymentMethod,
        string cashReceived,
        bool printReceipt,
        string? clientId = null,
        string? nonCashReceived = null)
    {
        var body = new Dictionary<string, string>
        {
            ["payment_method"] = paymentMethod ?? "",
            ["print_receipt"] = printReceipt ? "true" : "false",
            ["cash_received"] = cashReceived ?? "",
        };

        if (!string.IsNullOrWhiteSpace(clientId))
            body["client_id"] = clientId.Trim();

        if (!string.IsNullOrWhiteSpace(nonCashReceived))
            body["transfer_received"] = nonCashReceived.Trim();

        if (!string.IsNullOrWhiteSpace(PosApp.PosCashboxId))
            body["cashbox_id"] = PosApp.PosCashboxId.Trim();

        var shiftId = PosApp.ActiveShiftId;
        if (!string.IsNullOrWhiteSpace(shiftId)
            && !shiftId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase))
            body["shift_id"] = shiftId.Trim();

        return body;
    }

    private static void ApplyOfflineStockDecrement(string cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            foreach (var item in CartDisplayHelper.EnumerateItems(doc.RootElement))
            {
                var productId = CartDisplayHelper.TryProductId(item);
                if (string.IsNullOrEmpty(productId))
                    continue;

                var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                    string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                if (tile == null)
                    continue;

                var soldQty = CartDisplayHelper.LineQuantityInStockUnits(item, tile);
                if (soldQty <= 0)
                    continue;

                var next = Math.Max(0, tile.Quantity - soldQty);
                LocalProductRepository.Instance.UpdateStock(productId, next, tile.MustWeigh);
                // 2026-09-23: OnUi, а не прямой вызов. Этот код выполняется в продолжении
                // после ConfigureAwait(false), то есть в потоке пула, а плитка уже
                // привязана к каталогу на экране: смена Quantity поднимает PropertyChanged,
                // который Avalonia принимает только с UI-потока. Соседний DecrementLocalStock
                // делает правильно — расхождение было непреднамеренным.
                StockSyncService.ApplyQuantityToTileOnUi(tile, next, tile.MustWeigh);

                // 2026-09-13, живой баг: продажа комплекта офлайн не трогала остатки товаров,
                // из которых он состоит — только (нередко фиктивный, локальный) SKU самого
                // комплекта. Онлайн это делает сервер по составу BundleItems, но офлайн-продажа
                // ставится в очередь и сервер её пока не видит — единственный источник состава
                // здесь тот же BundleItems (для локально созданных комплектов вообще ничего,
                // кроме него, серверу и не сообщить). Возможный риск повторного списания
                // компонентов у СЕРВЕРНОГО комплекта, когда очередь потом реплеится — самоисправится
                // ближайшей полной синхронизацией каталога, как и любое другое оптимистичное
                // офлайн-значение остатка.
                if (tile.IsBundle && tile.BundleItems is { Count: > 0 } bundleItems)
                {
                    foreach (var component in bundleItems)
                    {
                        if (string.IsNullOrEmpty(component.ProductId))
                            continue;

                        var componentTile = CatalogCacheService.Products.FirstOrDefault(p =>
                            string.Equals(p.Id, component.ProductId, StringComparison.OrdinalIgnoreCase));
                        if (componentTile == null)
                            continue;

                        var componentNext = Math.Max(0, componentTile.Quantity - component.Quantity * soldQty);
                        LocalProductRepository.Instance.UpdateStock(component.ProductId, componentNext, componentTile.MustWeigh);
                        StockSyncService.ApplyQuantityToTileOnUi(componentTile, componentNext, componentTile.MustWeigh);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Offline stock decrement skipped: {ex}", "STOCK");
        }
    }

    private async Task<bool> TryPrintReceiptAsync(
        string cartJson,
        string? paymentMethod,
        string? cashReceived,
        string? offlineNote = null,
        JsonElement? checkoutResponse = null)
    {
        try
        {
            var printed = await _receiptPrinter.PrintReceiptAsync(new CartSnapshot
            {
                CartJson = cartJson,
                OfflineNote = offlineNote,
                PaymentMethodKey = paymentMethod,
                CashReceived = cashReceived,
                ReceiptText = checkoutResponse.HasValue
                    ? CheckoutResponseHelper.TryReceiptTextFromCheckout(checkoutResponse.Value)
                    : null,
            }, CancellationToken.None).ConfigureAwait(false);
            PosLogger.Log(printed ? "Receipt printed." : "Receipt printer returned failure.",
                printed ? "PRINTER" : "WARNING");
            return printed;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Print failed: {ex}", "PRINTER");
            return false;
        }
    }

    private static (string CartJson, double Total) BuildOfflineFallback(
        string cartJson,
        double originalTotal,
        Dictionary<string, string>? discountBody)
    {
        if (discountBody is null || discountBody.Count == 0)
            return (cartJson, originalTotal);

        using var fallbackCart = new CartService();
        fallbackCart.SetLocalOfflineCart(cartJson);
        var percent = discountBody.TryGetValue("order_discount_percent", out var pct) ? pct : null;
        var total = discountBody.TryGetValue("order_discount_total", out var sum) ? sum : null;
        ReceiptSnapshotCartEditor.PatchOrderDiscount(fallbackCart, percent, total);
        return (fallbackCart.GetRawText(), CartTotalsCalculator.Calculate(fallbackCart.Root).TotalDue);
    }
}
