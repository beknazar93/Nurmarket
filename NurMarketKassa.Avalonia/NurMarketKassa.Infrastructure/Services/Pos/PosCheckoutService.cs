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
public sealed partial class PosCheckoutService : IPosCheckoutService
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
            throw new ApiException(Tr.T("Добавьте товары в корзину.", "Себетке товар кошуңуз.", "Add products to the cart.", "Sepete ürün ekleyin.", "Savatga mahsulot qo'shing."), 400);

        // 2026-09-29: и в аварии сервера (ServerOutageMonitor) — чек уйдёт в очередь, переносить некуда.
        if (OfflineModeHelper.SellLocally)
            return;

        if (_cart.IsLocalOffline)
        {
            // 2026-09-28, стресс-тест: касса запустилась без своей смены (открыта смена другого
            // кассира), товар лёг в локальную корзину, потом кассир открыл смену — а чек так и
            // остался локальным: при живой связи ушёл в офлайн-очередь, и «в долг»/смешанная
            // оплата для него были бы запрещены. Если связь есть и смена серверная — переносим
            // локальный чек на сервер тем же путём, что отложенные чеки. Не вышло — возвращаем
            // чек как был, и оплата идёт офлайн, как раньше.
            if (!Guid.TryParse(PosApp.ActiveShiftId, out _))
                return;
            var localSnapshot = _cart.Root.GetRawText();
            try
            {
                PosLogger.Log("PAY prepare: local cart while online — moving it to the server", "PAYMENT");
                await StagingCartService.MaterializeSnapshotOnServerAsync(
                    _salesApi, _cart, PosApp.PosCashboxId, cancellationToken, force: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"PAY prepare: local cart stays offline ({ex.GetType().Name}: {ex.Message})", "WARNING");
                _cart.SetLocalOfflineCart(localSnapshot);
            }
            return;
        }

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

        if (OfflineModeHelper.SellLocally || _cart.IsLocalOffline || string.IsNullOrWhiteSpace(_cart.CartId))
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
            // 2026-09-29: названия строк (варианты по доп. штрихкоду) — прежние, как видел кассир
            // (см. StagingCartService.WithSnapshotLineNames).
            var namesSource = _cart.GetRawText();
            _cart.SetCart(StagingCartService.WithSnapshotLineNames(
                await _salesApi.PosCartGetAsync(_cart.CartId!, cancellationToken).ConfigureAwait(false), namesSource));
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

    /// <summary>
    /// Оплата чека.
    ///
    /// 2026-09-29, авария сервера NurCRM (ServerOutageMonitor, требование владельца «не выводи
    /// ошибку — предупреди и работай автономно до исправления бэка»). Что можно в аварии:
    /// <code>
    /// Операция              | В аварии                              | Почему так
    /// ----------------------|---------------------------------------|-------------------------------------------
    /// Продажа нал/безнал    | сразу в офлайн-очередь, чек печатается | досылка одним запросом с Idempotency-Key =
    ///                       | с пометкой «ОФФЛАЙН», остаток — местно | id записи: повтор не создаёт второй продажи
    /// Оплата, упавшая 5xx/  | сначала сверка «не прошла ли уже»;    | быстрый путь: запись очереди = тот же ключ;
    ///   таймаут/обрыв       | нет — в очередь, авария объявляется   | старый путь: запись с корзиной и отметкой
    ///                       |                                       | отправки — досылка сперва сверит корзину
    /// Сервер не ответил за  | в очередь без сверки, авария          | то же: ключ / отметка отправленной корзины
    ///   1,8 с (нал/безнал)  | объявляется (ServerAnswerBudget)      | (сверку делает досылка)
    /// Продажа «в долг»      | недоступна («пока сервер не отвечает») | клиент и сделка живут только на сервере
    /// Смешанная оплата      | недоступна                            | очередь не хранит безналичную часть
    /// Возврат               | недоступен                            | нужна продажа сервера; деньги без записи
    /// Оплата долга          | недоступна                            | список долгов только на сервере
    /// Внесение / изъятие    | записывается в кассе, на сервер — при | ShiftCashFlowSync сверяет source_id перед
    ///                       | восстановлении связи                  | записью — дубля нет
    /// Открытие смены        | офлайн-смена (как без интернета)      | сервер откроет смену при восстановлении
    /// Закрытие смены        | закрыта в кассе + очередь закрытия    | повторное закрытие сервер отклоняет
    ///                       | (после досылки продаж)                | «уже закрыта» — очередь это понимает
    /// Каталог, поиск, скан  | из локальной базы; остатки — местные  | синхронизации ждут восстановления
    ///                       | и соседних касс по сети               |
    /// </code>
    /// </summary>
    public async Task<PosCheckoutResult> CheckoutAsync(
        PosCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            return PosCheckoutResult.Failed(Tr.T("Добавьте товары в корзину.", "Себетке товар кошуңуз.", "Add products to the cart.", "Sepete ürün ekleyin.", "Savatga mahsulot qo'shing."));

        // Materialization replaces a staging cart before its network work is
        // complete. Keep a recoverable copy for an offline fallback.
        var fallbackCartJson = _cart.GetRawText();
        var fallbackTotal = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;
        // 2026-09-13: захвачено здесь же, до PrepareCartForCheckoutAsync/материализации, по той
        // же причине, что и fallbackCartJson выше — нужен настоящий ID продажи, на который
        // реально был отправлен checkout, а не то, что окажется в _cart ПОСЛЕ возможной
        // перестройки корзины. См. WasCheckoutAlreadyAppliedAsync ниже.
        var fallbackCartId = _cart.CartId;
        // 2026-09-29: корзина, на которую старый путь уже ОТПРАВИЛ checkout (без ключа
        // идемпотентности). Если ответ потерялся, запись очереди несёт эту корзину и отметку
        // отправки — досылка сперва спросит сервер о её статусе и второй продажи не создаст.
        _legacyCheckoutPostedCartId = null;
        _quickKeyAbandonedOn500 = null;

        // 2026-09-29, требование владельца: «если сеть есть (галочка "онлайн"), а продажа в базу не
        // уходит — анимация максимум 2 секунды, дальше в фон и обслуживать следующего». Наличные и
        // безнал ждут ответа сервера не дольше ServerAnswerBudget: не ответил — чек в офлайн-очередь
        // с тем же ключом идемпотентности (быстрый путь) или с отметкой отправленной корзины (старый
        // путь), касса объявляет аварию сервера, досылка идёт в фоне. Раньше ожидание доходило до
        // 31 с (молчащий сервер: два запроса по 15 с) и 3 с на ответах 5xx/429.
        //
        // Каждый ответ сервера в ЭТОЙ оплате (успех или отказ 4xx) отсчитывает окно заново: старый
        // путь переносит чек несколькими запросами, и большой чек при живом сервере не должен уходить
        // в очередь. «В долг» и смешанная в очередь не ставятся — для них ожидание прежнее.
        var queueable = IsQueueablePayment(request.PaymentMethod);
        using var serverWait = new CancellationTokenSource();
        if (queueable)
            serverWait.CancelAfter(ServerAnswerBudget);
        using var netCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, serverWait.Token);
        var net = netCts.Token;
        var watch = ServerOutageMonitor.BeginResponseWatch(queueable ? () => serverWait.CancelAfter(ServerAnswerBudget) : null);
        _serverWait = serverWait;
        _responseWatch = watch;
        var paymentStartedMs = Environment.TickCount64;

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

            // 2026-09-28, BE-11: сначала — продажа одним запросом (PosCheckoutService.Quick.cs):
            // снимок чека уже со скидкой уходит на сервер целиком, без переноса корзины. null —
            // этот чек новый адрес не берёт (или выключатель в настройках), дальше старый путь.
            var quickResult = await TryQuickCheckoutAsync(request, cancellationToken, net).ConfigureAwait(false);
            if (quickResult != null)
                return quickResult;

            using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(net);
            // Перенос позиций на сервер идёт по одной, ~0,2 с на позицию. 2026-09-26, стресс-тест:
            // чек на 144 позиции не успевал за прежние фиксированные 25 с и уходил в офлайн-очередь
            // при живой связи. Даём время по размеру чека: 25 с + 0,35 с на позицию, не больше 3 мин.
            var prepareLines = _cart.HasCart ? CartDisplayHelper.EnumerateItems(_cart.Root).Count() : 0;
            prepareCts.CancelAfter(TimeSpan.FromSeconds(Math.Min(180, 25 + 0.35 * prepareLines)));
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
                && !await ApplyOrderDiscountAsync(request.OrderDiscountBody, net).ConfigureAwait(false))
            {
                // 2026-09-29: скидка «не прошла», потому что сервер не ответил вовремя, — это не
                // ошибка скидки, а тот же уход в очередь, что и ниже (ApplyOrderDiscountAsync
                // глушит исключения и возвращает false).
                serverWait.Token.ThrowIfCancellationRequested();
                return PosCheckoutResult.Failed(PaymentErrorMessages.DiscountFailure);
            }

            // Capture the authoritative receipt only after server refresh and
            // after the final discount selected in the payment dialog.
            var cartJsonSnapshot = _cart.GetRawText();
            var total = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;

            // 2026-09-28, денежный баг продажи №1136 (окно оплаты 50,00 — продажа 42,50): итог
            // серверной корзины сверяется с итогом окна оплаты ДО проведения продажи в ОБЕ
            // стороны. Раньше ловилась только нехватка наличных, а если сервер насчитал МЕНЬШЕ
            // (акция товара, о которой касса не знала), продажа молча проходила на меньшую сумму,
            // а кассир уже взял деньги по окну оплаты. Сверяем с полем total самой корзины сервера —
            // это ровно та сумма, на которую он проведёт продажу.
            if (!OfflineModeHelper.SellLocally && !_cart.IsLocalOffline
                && request.ExpectedTotal is { } expectedTotal)
            {
                var serverCartTotal = TryReadDecimal(_cart.Root, "total", out var serverTotalValue)
                    ? (double)serverTotalValue
                    : total;
                if (Math.Abs(serverCartTotal - expectedTotal) > 0.01 + 1e-6)
                {
                    var diff = Math.Abs(serverCartTotal - expectedTotal);
                    PosLogger.Log(
                        $"PAY mismatch: окно оплаты {expectedTotal:0.00}, серверная корзина {serverCartTotal:0.00} " +
                        $"(касса по корзине {total:0.00}) — продажа не проведена. Корзина: {cartJsonSnapshot}",
                        "PAYMENT");
                    return PosCheckoutResult.Failed(Tr.T(
                        $"Сумма чека на сервере {serverCartTotal:0.00} сом, а в окне оплаты было {expectedTotal:0.00} сом (разница {diff:0.00}). "
                        + "Продажа НЕ проведена. Чек обновлён по данным сервера (например, акция товара) — проверьте сумму и нажмите «Оплатить» ещё раз.",
                        $"Сервердеги чектин суммасы {serverCartTotal:0.00} сом, ал эми төлөм терезесинде {expectedTotal:0.00} сом болчу (айырмасы {diff:0.00}). "
                        + "Сатуу ӨТКӨРҮЛГӨН ЖОК. Чек сервердин маалыматы боюнча жаңыртылды (мисалы, товардын акциясы) — сумманы текшерип, «Төлөө» баскычын кайра басыңыз.",
                        $"The receipt total on the server is {serverCartTotal:0.00} som, but the payment window showed {expectedTotal:0.00} som (difference {diff:0.00}). "
                        + "The sale was NOT recorded. The receipt has been updated from the server (for example, a product promotion) — check the total and click “Pay” again.",
                        $"Sunucudaki fiş tutarı {serverCartTotal:0.00} som, ödeme penceresinde ise {expectedTotal:0.00} som vardı (fark {diff:0.00}). "
                        + "Satış KAYDEDİLMEDİ. Fiş sunucu verilerine göre güncellendi (örneğin ürün kampanyası) — tutarı kontrol edip «Öde» düğmesine tekrar tıklayın.",
                        $"Serverdagi chek summasi {serverCartTotal:0.00} so'm, to'lov oynasida esa {expectedTotal:0.00} so'm edi (farq {diff:0.00}). "
                        + "Sotuv O'TKAZILMADI. Chek server ma'lumotlari bo'yicha yangilandi (masalan, mahsulot aksiyasi) — summani tekshirib, «To'lash» tugmasini yana bosing."));
                }
            }

            // Сверяем наличные с итогом ПОСЛЕ переноса чека на сервер.
            //
            // Кассир вводит деньги по сумме, которую показала касса ДО переноса. Если серверная
            // корзина после переноса стоит дороже — из-за скидки, не доехавшей до сервера, или
            // из-за копейки на весовом товаре, — сервер отвечает «Сумма, полученная наличными,
            // меньше суммы продажи», и кассир видит отказ без единой цифры: ни сколько не
            // хватило, ни почему. Ловим это здесь и называем обе суммы.
            if (string.Equals(request.PaymentMethod, "cash", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(request.CashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var cashGiven)
                && cashGiven + 0.005 < total)
            {
                var shortfall = total - cashGiven;
                PosLogger.Log(
                    $"PAY mismatch: наличные {cashGiven:0.00}, итог после переноса {total:0.00}, "
                    + $"не хватает {shortfall:0.00}. Корзина: {cartJsonSnapshot}",
                    "PAYMENT");

                return PosCheckoutResult.Failed(Tr.T(
                    $"Сумма чека изменилась при переносе на сервер: касса показала {cashGiven:0.00} сом, "
                    + $"сервер посчитал {total:0.00} сом (не хватает {shortfall:0.00}). "
                    + "Чаще всего это скидка, которую сервер не принял. Проверьте скидку и повторите оплату.",
                    $"Чектин суммасы серверге өткөрүүдө өзгөрдү: касса {cashGiven:0.00} сом көрсөттү, "
                    + $"сервер {total:0.00} сом эсептеди ({shortfall:0.00} жетишпейт). "
                    + "Көбүнчө бул сервер кабыл албаган арзандатуу. Арзандатууну текшерип, төлөмдү кайталаңыз.",
                    $"The receipt total changed when it was sent to the server: the till showed {cashGiven:0.00} som, "
                    + $"the server calculated {total:0.00} som ({shortfall:0.00} short). "
                    + "This is usually a discount the server didn't accept. Check the discount and retry the payment.",
                    $"Fiş tutarı sunucuya aktarılırken değişti: kasa {cashGiven:0.00} som gösterdi, "
                    + $"sunucu {total:0.00} som hesapladı ({shortfall:0.00} eksik). "
                    + "Bu genellikle sunucunun kabul etmediği bir indirimdir. İndirimi kontrol edip ödemeyi tekrarlayın.",
                    $"Chek summasi serverga o'tkazilganda o'zgardi: kassa {cashGiven:0.00} so'm ko'rsatdi, "
                    + $"server {total:0.00} so'm hisobladi ({shortfall:0.00} yetishmaydi). "
                    + "Odatda bu server qabul qilmagan chegirma. Chegirmani tekshirib, to'lovni takrorlang."));
            }

            if (OfflineModeHelper.SellLocally || _cart.IsLocalOffline)
            {
                // 2026-09-29: в аварии сервера — свой, не пугающий текст (чек остаётся в кассе).
                if (OfflineModeHelper.IsServerOutage && OutageUnavailablePaymentMessage(request.PaymentMethod) is { } outageMessage)
                    return PosCheckoutResult.Failed(outageMessage);

                if (string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
                    return PosCheckoutResult.Failed(Tr.T(
                        "Продажа «в долг» недоступна офлайн — нужна связь с сервером.",
                        "«Карызга» сатуу офлайн режимде жеткиликсиз — сервер менен байланыш керек.",
                        "Selling “on credit” is unavailable offline — a server connection is required.",
                        "«Veresiye» satış çevrimdışı modda kullanılamaz — sunucu bağlantısı gerekir.",
                        "«Qarzga» sotish oflayn rejimda mavjud emas — server bilan aloqa kerak."));

                if (string.Equals(request.PaymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
                    return PosCheckoutResult.Failed(Tr.T(
                        "Смешанная оплата недоступна офлайн — нужна связь с сервером.",
                        "Аралаш төлөм офлайн режимде жеткиликсиз — сервер менен байланыш керек.",
                        "Mixed payment is unavailable offline — a server connection is required.",
                        "Karışık ödeme çevrimdışı modda kullanılamaz — sunucu bağlantısı gerekir.",
                        "Aralash to'lov oflayn rejimda mavjud emas — server bilan aloqa kerak."));

                return await CompleteOfflineCheckoutAsync(request, cartJsonSnapshot, total)
                    .ConfigureAwait(false);
            }

            return await CompleteOnlineCheckoutAsync(request, cartJsonSnapshot, total, net, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            PaymentErrorMessages.Log("Checkout API error", ex);

            // 2026-09-28, стресс-тест (сервер ответил 502): шлюз мог отдать 5xx уже ПОСЛЕ того, как
            // продажа провелась. Как и при таймауте, сначала спрашиваем сервер о судьбе этой
            // продажи — иначе кассир повторит оплату и получит двойной чек.
            // Текущий ID корзины: после переноса отложенного чека на сервер он уже не тот, что был.
            if (ex.StatusCode is >= 500 and <= 599
                && await WasCheckoutAlreadyAppliedAsync(_legacyCheckoutPostedCartId
                                                        ?? (_cart.IsLocalOffline ? fallbackCartId : _cart.CartId ?? fallbackCartId),
                                                        serverWait.Token).ConfigureAwait(false))
                return CompleteAlreadyAppliedCheckout(fallbackCartJson, fallbackTotal);

            // 2026-09-29: сбой сервера (5xx, 408, 429, повреждённый ответ) — не ошибка для кассира:
            // сервер объявлен недоступным, чек уходит в офлайн-очередь, как при обрыве сети.
            if (ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode))
                return await SaveOfflineAfterServerFailureAsync(request, fallbackCartJson, fallbackTotal, ex).ConfigureAwait(false);

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
                    ? Tr.T("Касса была переназначена (старая не подходит для вашего филиала). Нажмите «Оплатить» ещё раз.",
                        "Касса алмаштырылды (мурункусу филиалыңызга туура келбейт). «Төлөө» баскычын дагы бир жолу басыңыз.",
                        "The till was reassigned (the old one doesn't match your branch). Click “Pay” again.",
                        "Kasa yeniden atandı (eskisi şubenize uymuyor). «Öde» düğmesine tekrar tıklayın.",
                        "Kassa qayta tayinlandi (eskisi filialingizga mos emas). «To'lash» tugmasini yana bir bor bosing.")
                    : Tr.T("Ни одна касса компании не подходит для вашего филиала. Обратитесь к администратору NurCRM — " +
                      "проверьте привязку кассы к филиалу в веб-версии.",
                        "Компаниянын бир да кассасы филиалыңызга туура келбейт. NurCRM администраторуна кайрылыңыз — " +
                        "веб-версияда кассанын филиалга байланышын текшериңиз.",
                        "None of the company's tills match your branch. Contact your NurCRM administrator — " +
                        "check the till's branch assignment in the web version.",
                        "Şirketin hiçbir kasası şubenize uymuyor. NurCRM yöneticinize başvurun — " +
                        "web sürümünde kasanın şubeye bağlantısını kontrol edin.",
                        "Kompaniyaning birorta kassasi filialingizga mos emas. NurCRM administratoriga murojaat qiling — " +
                        "veb-versiyada kassaning filialga bog'lanishini tekshiring."));
            }

            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
        catch (HttpRequestException ex)
        {
            PosLogger.Log($"Checkout network error, saving offline: {ex}", "PAYMENT");
            if (await WasCheckoutAlreadyAppliedAsync(_legacyCheckoutPostedCartId ?? fallbackCartId, serverWait.Token).ConfigureAwait(false))
                return CompleteAlreadyAppliedCheckout(fallbackCartJson, fallbackTotal);
            // 2026-09-29: общий путь с 5xx — авария объявляется, чек с отметкой отправленной корзины.
            return await SaveOfflineAfterServerFailureAsync(request, fallbackCartJson, fallbackTotal, ex).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PosLogger.Log("Checkout canceled by caller.", "DEBUG");
            throw;
        }
        catch (OperationCanceledException ex) when (serverWait.IsCancellationRequested)
        {
            // 2026-09-29: сервер не ответил за ServerAnswerBudget (см. начало метода). Сверку «не
            // прошла ли уже» здесь не делаем — на неё нет времени; её сделает досылка: быстрый путь
            // повторит тот же ключ, старый путь сперва спросит статус отправленной корзины.
            PosLogger.Log(
                $"PAY: сервер не ответил за {ServerAnswerBudget.TotalSeconds:0.0} с (ответов сервера в этой оплате: {watch.Answers}, " +
                $"прошло {Environment.TickCount64 - paymentStartedMs} мс) — чек в очередь, досылка в фоне" +
                (_legacyCheckoutPostedCartId != null ? $", checkout корзины {_legacyCheckoutPostedCartId} уже отправлен." : "."),
                "PAYMENT");
            return await SaveOfflineAfterServerFailureAsync(request, fallbackCartJson, fallbackTotal, ex).ConfigureAwait(false);
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
            if (await WasCheckoutAlreadyAppliedAsync(_legacyCheckoutPostedCartId ?? fallbackCartId, serverWait.Token).ConfigureAwait(false))
                return CompleteAlreadyAppliedCheckout(fallbackCartJson, fallbackTotal);
            // 2026-09-29: общий путь с 5xx — авария объявляется, чек с отметкой отправленной корзины.
            return await SaveOfflineAfterServerFailureAsync(request, fallbackCartJson, fallbackTotal, ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PaymentErrorMessages.Log("Checkout unexpected error", ex);
            RestoreCartAfterFailedCheckout(fallbackCartJson);
            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
        finally
        {
            watch.Stop();
            _serverWait = null;
            _responseWatch = null;
        }
    }

    /// <summary>2026-09-29: сколько касса ждёт ответа сервера при оплате наличными/безналом,
    /// прежде чем отдать чек в очередь (владелец: «максимум 2 секунды»). 1,8 с — чтобы вместе с
    /// записью в очередь и окном результата кассир ждал не больше двух секунд.</summary>
    internal static readonly TimeSpan ServerAnswerBudget = TimeSpan.FromSeconds(1.8);

    private CancellationTokenSource? _serverWait;
    private ServerOutageMonitor.ResponseWatch? _responseWatch;

    /// <summary>Наличные и безнал можно поставить в офлайн-очередь; «в долг» и смешанную — нет.</summary>
    private static bool IsQueueablePayment(string? paymentMethod) =>
        !string.Equals(paymentMethod, "debt", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(paymentMethod, "mixed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сервер провёл продажу — окно ожидания больше не нужно: дальнейшая работа (номер
    /// чека, печать) не должна увести уже проведённую продажу в очередь.</summary>
    private void DisarmServerWait()
    {
        _responseWatch?.Stop();
        try
        {
            _serverWait?.CancelAfter(Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
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

    /// <summary>См. CheckoutAsync: корзина, на которую старый путь уже отправил checkout.</summary>
    private string? _legacyCheckoutPostedCartId;

    /// <summary>2026-09-29: оплата упала из-за сервера (5xx/408/429, таймаут, обрыв, повреждённый
    /// ответ) и сверка «не прошла ли уже» ответила «нет или неизвестно». Раньше на 5xx кассир видел
    /// «Оплата не прошла … повторите через минуту», а сеть/таймаут уходили в очередь с причиной
    /// сбоя в тексте. Теперь одинаково: сервер объявляется недоступным (дальше касса работает
    /// автономно без ожидания таймаутов), чек — в офлайн-очередь. Если checkout старого пути уже
    /// ушёл на сервер, запись очереди несёт его корзину (см. CompleteOfflineCheckoutAsync).
    /// «В долг» и смешанную очередь не принимает — чек возвращается в кассу с понятным текстом.</summary>
    private async Task<PosCheckoutResult> SaveOfflineAfterServerFailureAsync(
        PosCheckoutRequest request,
        string fallbackCartJson,
        double fallbackTotal,
        Exception failure)
    {
        ServerOutageMonitor.ReportFailure(
            Tr.T("оплата", "төлөм", "payment", "ödeme", "to'lov"), failure, hard: true);

        if (OutageUnavailablePaymentMessage(request.PaymentMethod) is { } unavailable)
        {
            RestoreCartAfterFailedCheckout(fallbackCartJson);
            PosLogger.Log($"Checkout: сервер не отвечает, способ оплаты {request.PaymentMethod} в очередь не ставится — чек оставлен в кассе.", "PAYMENT");
            return PosCheckoutResult.Failed(unavailable);
        }

        var fallback = BuildOfflineFallback(fallbackCartJson, fallbackTotal, request.OrderDiscountBody);
        PosLogger.Log(
            $"Checkout: сервер не отвечает ({ServerOutageMonitor.Describe(failure)}) — чек в офлайн-очередь" +
            (_legacyCheckoutPostedCartId != null ? $", checkout корзины {_legacyCheckoutPostedCartId} уже отправлялся — досылка сверит её статус." : "."),
            "PAYMENT");
        // Быстрый путь в этой оплате уже отправлял чек и получил 500 дважды — запись очереди
        // получает тот же ключ: если продажа всё же прошла, досылка получит «replayed», а не дубль.
        var quickKey = _legacyCheckoutPostedCartId == null ? _quickKeyAbandonedOn500 : null;
        return await CompleteOfflineCheckoutAsync(
                request, fallback.CartJson, fallback.Total,
                Tr.T("сервер NurCRM не отвечает", "NurCRM сервери жооп бербей жатат", "the NurCRM server is not responding",
                    "NurCRM sunucusu yanıt vermiyor", "NurCRM serveri javob bermayapti"),
                entryId: quickKey,
                quickAttempted: quickKey != null,
                submittedCartId: _legacyCheckoutPostedCartId)
            .ConfigureAwait(false);
    }

    /// <summary>Способы оплаты, которые без сервера провести нельзя (см. таблицу у CheckoutAsync).
    /// null — способ проводится и в аварии.</summary>
    private static string? OutageUnavailablePaymentMessage(string? paymentMethod)
    {
        if (string.Equals(paymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            return Tr.T(
                "Продажа «в долг» недоступна, пока сервер NurCRM не отвечает. Чек сохранён в кассе — примите оплату наличными или картой либо повторите позже.",
                "NurCRM сервери жооп бербей турганда «карызга» сатуу жеткиликсиз. Чек кассада сакталды — накталай же карта менен төлөм алыңыз же кийинчерээк кайталаңыз.",
                "Selling “on credit” is unavailable while the NurCRM server is not responding. The receipt is kept in the till — take cash or card, or try again later.",
                "NurCRM sunucusu yanıt vermediği sürece «veresiye» satış yapılamaz. Fiş kasada tutuluyor — nakit veya kartla ödeme alın ya da daha sonra tekrar deneyin.",
                "NurCRM serveri javob bermayotgan paytda «qarzga» sotish mavjud emas. Chek kassada saqlandi — naqd yoki karta orqali to'lov oling yoki keyinroq qayta urinib ko'ring.");

        if (string.Equals(paymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
            return Tr.T(
                "Смешанная оплата недоступна, пока сервер NurCRM не отвечает. Чек сохранён в кассе — примите оплату только наличными или только картой либо повторите позже.",
                "NurCRM сервери жооп бербей турганда аралаш төлөм жеткиликсиз. Чек кассада сакталды — төлөмдү накталай гана же карта менен гана алыңыз же кийинчерээк кайталаңыз.",
                "Mixed payment is unavailable while the NurCRM server is not responding. The receipt is kept in the till — take cash only or card only, or try again later.",
                "NurCRM sunucusu yanıt vermediği sürece karışık ödeme yapılamaz. Fiş kasada tutuluyor — yalnızca nakit veya yalnızca kartla ödeme alın ya da daha sonra tekrar deneyin.",
                "NurCRM serveri javob bermayotgan paytda aralash to'lov mavjud emas. Chek kassada saqlandi — faqat naqd yoki faqat karta orqali to'lov oling yoki keyinroq qayta urinib ko'ring.");

        return null;
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

            if (OfflineModeHelper.SellLocally)
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
        catch (ApiException ex) when (OfflineModeHelper.CanOperateWithoutServer
                                      || ServerOutageMonitor.IsServerFailureStatus(ex.StatusCode))
        {
            // 2026-09-29: и при сбое сервера (5xx/429) — новый чек локально, без ошибки кассиру.
            PosLogger.Log($"Restart sale offline after API error: {ex}", "PAYMENT");
            LocalCartService.StartNewLocalCart(_cart);
            return null;
        }
        catch (HttpRequestException ex)
        {
            // 2026-09-29: обрыв связи — всегда локальный новый чек (раньше только в офлайн-входе,
            // иначе кассир видел текст сетевой ошибки вместо пустого чека).
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
    /// <param name="serverWait">2026-09-29: окно ожидания оплаты (см. CheckoutAsync). Истекло — не
    /// спрашиваем вовсе: чек уйдёт в очередь, и досылка сверит статус корзины сама.</param>
    private async Task<bool> WasCheckoutAlreadyAppliedAsync(string? cartId, CancellationToken serverWait = default)
    {
        if (string.IsNullOrWhiteSpace(cartId) || serverWait.IsCancellationRequested)
            return false;

        // Статус корзины, а не продажи — см. CartSaleSessionHelper.GetCheckoutStateAsync: прежний
        // запрос sales/{cartId} всегда давал 404, и оплата после тайм-аута считалась непрошедшей.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverWait);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var state = await CartSaleSessionHelper.GetCheckoutStateAsync(_salesApi, cartId, cts.Token).ConfigureAwait(false);
        return state == CartSaleSessionHelper.CartCheckoutState.Paid;
    }

    /// <summary>Sale уже проведена на сервере (см. WasCheckoutAlreadyAppliedAsync) — НЕ трогаем
    /// остаток (сервер его уже списал) и НЕ ставим в офлайн-очередь (реплей создал бы новую,
    /// дублирующую продажу): просто открываем кассиру новый чек локально, т.к. в этот момент
    /// связь всё ещё под вопросом (мы здесь именно потому, что предыдущий запрос не дождались) —
    /// обычный серверный RestartSaleSessionAsync рискует так же зависнуть.</summary>
    private PosCheckoutResult CompleteAlreadyAppliedCheckout(string cartJsonSnapshot, double total)
    {
        var appliedCartId = _cart.CartId;
        if (!string.IsNullOrWhiteSpace(appliedCartId))
            Lan.LanSalePublisher.Publish($"cart:{appliedCartId}", null, uploaded: true, cartJsonSnapshot, total, null);
        LocalCartService.StartNewLocalCart(_cart);
        return PosCheckoutResult.Succeeded(
            total,
            cartJsonSnapshot,
            info: Tr.T("Оплата уже прошла на сервере (ответ не успел дойти вовремя) — повторно не проводим. " +
                  "Чек можно распечатать ещё раз из раздела «Продажи».",
                "Төлөм серверде мурунтан эле өткөн (жооп убагында келген жок) — кайра өткөрбөйбүз. " +
                "Чекти «Сатуулар» бөлүмүнөн кайра басып чыгарса болот.",
                "The payment already went through on the server (the response didn't arrive in time) — not charging again. " +
                "You can reprint the receipt from the “Sales” section.",
                "Ödeme sunucuda zaten gerçekleşti (yanıt zamanında gelmedi) — tekrar işlenmeyecek. " +
                "Fişi «Satışlar» bölümünden yeniden yazdırabilirsiniz.",
                "To'lov serverda allaqachon o'tgan (javob o'z vaqtida kelmadi) — qayta o'tkazilmaydi. " +
                "Chekni «Sotuvlar» bo'limidan qayta chop etish mumkin."));
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
        string? reason = null,
        string? entryId = null,
        bool quickAttempted = false,
        string? submittedCartId = null)
    {
        // Проверка стоит здесь, в единственной точке постановки чека в очередь, а не только на
        // ветке «мы заранее знаем, что офлайн». В очередь чек попадает ещё двумя путями — из
        // catch(HttpRequestException) и catch(TaskCanceledException), — и там этой проверки не
        // было: долг и смешанная оплата проскакивали в очередь, а повтор не умеет ни привязать
        // клиента к долгу, ни передать безналичную часть (её просто негде хранить), поэтому
        // смешанный чек 3000 нал + 7000 карта выгрузился бы как 3000.
        if (OfflineModeHelper.IsServerOutage && OutageUnavailablePaymentMessage(request.PaymentMethod) is { } outageMessage)
            return PosCheckoutResult.Failed(outageMessage);

        if (string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            return PosCheckoutResult.Failed(Tr.T(
                "Продажа «в долг» недоступна без связи с сервером. Повторите оплату, когда появится интернет.",
                "«Карызга» сатуу сервер менен байланышсыз жеткиликсиз. Интернет пайда болгондо төлөмдү кайталаңыз.",
                "Selling “on credit” is unavailable without a server connection. Retry the payment when the internet is back.",
                "«Veresiye» satış sunucu bağlantısı olmadan kullanılamaz. İnternet bağlantısı sağlandığında ödemeyi tekrarlayın.",
                "«Qarzga» sotish server bilan aloqasiz mavjud emas. Internet paydo bo'lganda to'lovni takrorlang."));

        if (string.Equals(request.PaymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
            return PosCheckoutResult.Failed(Tr.T(
                "Смешанная оплата недоступна без связи с сервером. Повторите оплату, когда появится интернет.",
                "Аралаш төлөм сервер менен байланышсыз жеткиликсиз. Интернет пайда болгондо төлөмдү кайталаңыз.",
                "Mixed payment is unavailable without a server connection. Retry the payment when the internet is back.",
                "Karışık ödeme sunucu bağlantısı olmadan kullanılamaz. İnternet bağlantısı sağlandığında ödemeyi tekrarlayın.",
                "Aralash to'lov server bilan aloqasiz mavjud emas. Internet paydo bo'lganda to'lovni takrorlang."));

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
            ConsultantId = request.ConsultantId,
            ConsultantCommissionEnabled = request.ConsultantCommissionEnabled,
            ConsultantCommissionPercent = request.ConsultantCommissionPercent,
            QuickCheckoutAttempted = quickAttempted,
        };
        // 2026-09-28, BE-11: после неудачной быстрой оплаты id записи = её Idempotency-Key, и
        // досылка идёт тем же ключом: если первый запрос всё-таки дошёл, сервер вернёт ту же продажу.
        if (!string.IsNullOrWhiteSpace(entryId))
            entry.Id = entryId;

        // 2026-09-29: старый путь уже отправил checkout этой корзины и не дождался ответа (5xx,
        // таймаут, обрыв). Запись несёт корзину и отметку отправки: досылка (SyncService.
        // ReplayOfflineSaleAsync) сначала спросит сервер о статусе корзины — оплачена → повтора
        // нет. Без этого досылка шла бы одним запросом с новым ключом и создала бы вторую продажу.
        if (!string.IsNullOrWhiteSpace(submittedCartId))
        {
            entry.SyncCartId = submittedCartId;
            entry.CheckoutSubmittedAt = DateTimeOffset.Now;
        }

        OfflinePendingSalesStore.Append(entry);
        ApplyOfflineStockDecrement(cartJsonSnapshot);
        // Соседние кассы и программа владельца узнают о чеке по локальной сети, пока он в очереди.
        if (!isAutonomous)
            Lan.LanSalePublisher.Publish(entry.Id, null, uploaded: false, cartJsonSnapshot, total, request.PaymentMethod);

        // Ящик открывается и в офлайне тоже: наличные кассир берёт независимо от того, дошла ли
        // продажа до сервера. Не завязано на PrintReceipt — ящик нужен и когда чек не печатают.
        ReceiptPrintService.TryOpenCashDrawerAfterSale(request.PaymentMethod, request.CashReceived);

        // The completed receipt must disappear before the cashier can start
        // another operation; a delayed background reset could erase new items.
        LocalCartService.StartNewLocalCart(_cart);

        // 2026-10-04, отчёт о производительности (п. 9): как и при оплате онлайн — печать в фоне после
        // сброса чека; «чек не напечатан» кассир увидит по результату печати (ReceiptPrintTask).
        var printTask = request.PrintReceipt
            ? PrintReceiptInBackground(
                WithConsultantForReceipt(cartJsonSnapshot, request),
                ReceiptPaymentMethodKey(request),
                request.CashReceived,
                offlineNote: isAutonomous ? "АВТОНОМНЫЙ РЕЖИМ" : "ОФФЛАЙН (ожидает выгрузку)")
            : null;

        // 2026-09-10: автономная продажа никуда не "выгружается" (нет сервера/аккаунта, на
        // который выгружать) — "В очереди: N" тут вводит в заблуждение, как будто чек чего-то
        // ждёт. Обычный офлайн-режим (временная потеря связи с NurCRM) ниже не тронут.
        var pending = isAutonomous ? 0 : OfflinePendingSalesStore.PendingCount;
        var info = isAutonomous
            ? Tr.T("Оплата сохранена (автономный режим).", "Төлөм сакталды (автономдук режим).",
                "Payment saved (offline mode).", "Ödeme kaydedildi (çevrimdışı mod).", "To'lov saqlandi (oflayn rejim).")
            // 2026-09-29: авария сервера — кассиру не причина сбоя, а что будет с чеком.
            : OfflineModeHelper.IsServerOutage && !OfflineModeHelper.UseLocalOperations
                ? Tr.T($"Чек сохранён, отправится автоматически. В очереди: {pending}.",
                    $"Чек сакталды, автоматтык түрдө жөнөтүлөт. Кезекте: {pending}.",
                    $"Receipt saved, it will be sent automatically. Queued: {pending}.",
                    $"Fiş kaydedildi, otomatik olarak gönderilecek. Sırada: {pending}.",
                    $"Chek saqlandi, avtomatik ravishda yuboriladi. Navbatda: {pending}.")
            : reason != null
                ? Tr.T($"Оплата сохранена локально ({reason}). В очереди: {pending}.",
                    $"Төлөм ушул кассада сакталды ({reason}). Кезекте: {pending}.",
                    $"Payment saved locally ({reason}). Queued: {pending}.",
                    $"Ödeme yerel olarak kaydedildi ({reason}). Sırada: {pending}.",
                    $"To'lov shu kompyuterda saqlandi ({reason}). Navbatda: {pending}.")
                : Tr.T($"Оплата сохранена локально. В очереди: {pending}.",
                    $"Төлөм ушул кассада сакталды. Кезекте: {pending}.",
                    $"Payment saved locally. Queued: {pending}.",
                    $"Ödeme yerel olarak kaydedildi. Sırada: {pending}.",
                    $"To'lov shu kompyuterda saqlandi. Navbatda: {pending}.");

        // 2026-10-04: «чек не напечатан» добавляет к этому сообщению BasketPanelViewModel, когда фоновая
        // печать закончится неудачей (ReceiptPrintTask) — раньше здесь, после ожидания принтера.
        return PosCheckoutResult.OfflineSaved(
            total,
            cartJsonSnapshot,
            info,
            request.PrintReceipt,
            receiptPrinted: false,
            receiptPrintTask: printTask);
    }

    private async Task<PosCheckoutResult> CompleteOnlineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        CancellationToken cancellationToken,
        CancellationToken callerToken)
    {
        // cancellationToken — с окном ожидания ответа сервера (см. CheckoutAsync), callerToken —
        // без него: после ответа сервера продажа уже проведена, и её завершение (номер, печать)
        // не должно уводить её в очередь.
        var cartId = _cart.CartId;
        if (string.IsNullOrWhiteSpace(cartId))
            return PosCheckoutResult.Failed(Tr.T("Корзина не привязана к серверу. Начните продажу заново.",
                "Себет серверге байланган эмес. Сатууну кайрадан баштаңыз.",
                "The cart isn't linked to the server. Start the sale again.",
                "Sepet sunucuya bağlı değil. Satışı yeniden başlatın.",
                "Savat serverga bog'lanmagan. Sotuvni qaytadan boshlang."));

        var body = BuildCheckoutRequestBody(
            request.PaymentMethod, request.CashReceived, request.PrintReceipt, request.ClientId, request.NonCashReceived);
        AddConsultant(body, request.ConsultantId, request.ConsultantCommissionEnabled, request.ConsultantCommissionPercent);
        var checkoutIds = CartDisplayHelper.CollectCheckoutTargetIds(_cart.Root, cartId);

        PosLogger.Log(
            $"Checkout API: ids=[{string.Join(", ", checkoutIds)}], method={body.GetValueOrDefault("payment_method")}, " +
            $"cash={body.GetValueOrDefault("cash_received")}, transfer={body.GetValueOrDefault("transfer_received")}, " +
            $"shift={body.GetValueOrDefault("shift_id")}, " +
            $"cashbox={body.GetValueOrDefault("cashbox_id")}, client={body.GetValueOrDefault("client_id")}, " +
            $"consultant={body.GetValueOrDefault("consultant_id")} {body.GetValueOrDefault("consultant_commission_percent")}",
            "PAYMENT");

        JsonElement checkoutResponse;
        try
        {
            _legacyCheckoutPostedCartId = cartId;
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
            _legacyCheckoutPostedCartId = recoveredCartId;
            checkoutResponse = await _salesApi
                .PosCheckoutAsync(recoveredIds, body, cancellationToken)
                .ConfigureAwait(false);
        }

        DisarmServerWait();
        CheckoutResponseHelper.FormatSuccess(checkoutResponse);

        return await FinishOnlineCheckoutAsync(request, cartJsonSnapshot, total, checkoutResponse, cartId, callerToken)
            .ConfigureAwait(false);
    }

    /// <summary>Всё, что делается после того, как сервер провёл продажу. 2026-09-28: вынесено
    /// из CompleteOnlineCheckoutAsync без изменений, чтобы им же завершалась и продажа одним
    /// запросом (PosCheckoutService.Quick.cs). <paramref name="fallbackSaleId"/> — id корзины
    /// старого пути (null у быстрого: там id продажи всегда есть в ответе).</summary>
    private async Task<PosCheckoutResult> FinishOnlineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        JsonElement checkoutResponse,
        string? fallbackSaleId,
        CancellationToken cancellationToken)
    {
        var saleId = CheckoutResponseHelper.TrySaleId(checkoutResponse) ?? fallbackSaleId ?? "";
        PosLogger.Log($"Checkout API OK: saleId={saleId}", "PAYMENT");

        // 2026-10-04, клиент: «после продажи количество минусуется через некоторое время». Остаток
        // проданных товаров уменьшаем здесь, сразу после ответа сервера и ДО возврата к кассе: раньше
        // это делалось в фоне, а касса тем временем перечитывала каталог из базы (RepublishFromLocalAsync)
        // со старым остатком — и до следующей синхронизации (до 2 минут) на плитке было прежнее число.
        IReadOnlyDictionary<string, double>? soldStockExpected = null;
        try
        {
            soldStockExpected = StockSyncService.ApplySoldItemsDecrement(_cart.Root);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout: остаток проданных товаров сразу не уменьшен: {ex.Message}", "STOCK");
        }

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
        //
        // 2026-09-28, BE-08: постоянный номер продажи (sale.number) печатается на чеке сразу после
        // оплаты. Раньше чек при оплате выходил без «Чек №»: номер сервера в снимок корзины не
        // попадал, и номер был только у копии из «Истории чеков». Быстрый путь получает номер в
        // ответе POST pos/checkout/; старый checkout может его не прислать — тогда один короткий
        // GET продажи. Кладём под «receipt_number» — первое поле, которое ищет
        // CartReceiptTextBuilder.TryReceiptNumber (оттуда же предпросмотр чека после оплаты).
        var saleNumber = TryReadCheckoutSaleNumber(checkoutResponse)
            ?? await TryFetchSaleNumberAsync(saleId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(saleId) || saleNumber is not null)
        {
            var enrichedCart = CartJsonHelper.ParseObjectOrEmpty(cartJsonSnapshot);
            if (!string.IsNullOrWhiteSpace(saleId))
                enrichedCart["sale_id"] = saleId;
            if (saleNumber is { } number)
                enrichedCart["receipt_number"] = number.ToString(CultureInfo.InvariantCulture);
            cartJsonSnapshot = enrichedCart.ToJsonString();
        }
        cartJsonSnapshot = WithConsultantForReceipt(cartJsonSnapshot, request);

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

        // 2026-10-04, отчёт о производительности (п. 8): остаток проданных товаров уже уменьшен сразу
        // (ApplySoldItemsDecrement выше) — сверять его запросом products/{id} по каждому товару чека не нужно:
        // настоящий остаток сервера принесёт синхронизация каталога (не реже раза в 15 минут). Сверка с
        // сервером осталась только если сразу уменьшить не удалось (тогда она же и уменьшает).
        if (soldStockExpected is null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await StockSyncService.RefreshSoldItemsStockAsync(cartSnapshot, CancellationToken.None, soldStockExpected)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Background stock refresh failed: {ex}", "STOCK");
                }
            });
        }

        try
        {
            PosApp.AuditDb.LogSale(saleId, total, request.PaymentMethod ?? "", PosApp.CurrentUserId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout audit skipped after successful sale: {ex}", "AUDIT");
        }

        Lan.LanSalePublisher.Publish(saleId, saleId, uploaded: true, cartJsonSnapshot, total, request.PaymentMethod);

        ReceiptPrintService.TryOpenCashDrawerAfterSale(request.PaymentMethod, request.CashReceived);

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
        // 2026-10-04, отчёт о производительности (п. 9): печать чека — в фоне, уже после сброса чека. Раньше
        // касса ждала принтер до окна «Платёж принят» (на ПК с принтером в ошибке — 1,6 с на каждой продаже,
        // при зависшем принтере — до 8 с). Ящик открывается сразу (выше), чек печатается следом; не
        // напечатался — кассиру показывается то же «чек не напечатан» (BasketPanelViewModel, ReceiptPrintTask).
        var printTask = request.PrintReceipt
            ? PrintReceiptInBackground(cartJsonSnapshot, ReceiptPaymentMethodKey(request), request.CashReceived,
                checkoutResponse: checkoutResponse)
            : null;
        // 2026-10-04, п. 8: остаток смены после продажи шапка кассы возьмёт из ответа этой же проверки смены
        // (ShiftApiService.OpenShiftsListAfterSaleAsync), а не отдельным вторым запросом.
        _shiftApi.NoteSaleRecorded();
        _ = Task.Run(() => RefreshShiftStateInBackgroundAsync());

        var info = debtPartialAmountRequested
            ? Tr.T("Продажа оформлена в долг. Частичная оплата досчитывается сервером в фоне — если через пару минут долг клиента не уменьшится, введите оплату вручную через «Оплата долга».",
                "Сатуу карызга жазылды. Жарым-жартылай төлөмдү сервер фондо эсептейт — эгер бир-эки мүнөттөн кийин клиенттин карызы азайбаса, төлөмдү «Карыз төлөө» аркылуу кол менен киргизиңиз.",
                "The sale was recorded on credit. The server applies the partial payment in the background — if the client's debt doesn't go down in a couple of minutes, enter the payment manually via “Pay debt”.",
                "Satış veresiye olarak kaydedildi. Kısmi ödeme sunucu tarafından arka planda işleniyor — birkaç dakika içinde müşterinin borcu azalmazsa ödemeyi «Borç ödeme» üzerinden elle girin.",
                "Sotuv qarzga rasmiylashtirildi. Qisman to'lovni server fonda hisoblaydi — agar bir-ikki daqiqadan keyin mijozning qarzi kamaymasa, to'lovni «Qarzni to'lash» orqali qo'lda kiriting.")
            : null;

        return PosCheckoutResult.Succeeded(
            total,
            cartJsonSnapshot,
            checkoutResponse,
            info,
            request.PrintReceipt,
            receiptPrinted: false,
            receiptPrintTask: printTask);
    }

    /// <summary>2026-10-04, п. 9: фоновые печати чеков идут строго по одной и по порядку продаж — два чека
    /// подряд не должны перемешаться в одном принтере.</summary>
    private static readonly SemaphoreSlim BackgroundPrintGate = new(1, 1);

    private Task<bool> PrintReceiptInBackground(
        string cartJson,
        string? paymentMethod,
        string? cashReceived,
        string? offlineNote = null,
        JsonElement? checkoutResponse = null) =>
        Task.Run(async () =>
        {
            await BackgroundPrintGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await TryPrintReceiptAsync(cartJson, paymentMethod, cashReceived, offlineNote, checkoutResponse)
                    .ConfigureAwait(false);
            }
            finally
            {
                BackgroundPrintGate.Release();
            }
        });

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

                    // 2026-09-26, найдено при стресс-тесте: сервер сам записывает предоплату из
                    // checkout (cash_received) в сделку — «prepayment: 23, debt_amount: 128» при
                    // сумме 151 — и уже не считает её долгом. Второй платёж той же суммы зачёл бы
                    // клиенту предоплату дважды. До сих пор этого не случалось только потому, что
                    // сервер отклонял сумму больше одного взноса («Максимум: 4.26»); при предоплате
                    // меньше взноса деньги ушли бы. Сервер без поля prepayment — прежний путь.
                    if (deal.ValueKind == JsonValueKind.Object
                        && TryReadDecimal(deal, "prepayment", out var recorded)
                        && (double)recorded >= amount - 0.005)
                    {
                        PosLogger.Log(
                            $"Initial debt payment skipped: server already recorded prepayment {recorded:0.00} for sale {saleId}.",
                            "PAYMENT");
                        return true;
                    }

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

    /// <summary>2026-09-28, BE-08: постоянный номер продажи из ответа оплаты — «number» на верхнем
    /// уровне (быстрый путь, проверено: 1123–1136) или внутри «sale»/«data». null — не прислан.</summary>
    private static long? TryReadCheckoutSaleNumber(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return null;
        if (NurCrmReportsApi.TryReadSaleNumber(response) is { } number)
            return number;
        foreach (var key in new[] { "sale", "data" })
        {
            if (response.TryGetProperty(key, out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && NurCrmReportsApi.TryReadSaleNumber(nested) is { } nestedNumber)
                return nestedNumber;
        }

        return null;
    }

    /// <summary>2026-09-28, BE-08: номер продажи, если ответ checkout его не прислал (старый путь) —
    /// GET api/main/pos/sales/{id}/. Не дольше 3 с: продажа уже проведена, и чек без номера лучше,
    /// чем кассир, который ждёт сеть. Любая ошибка — null (чек печатается как раньше).</summary>
    private async Task<long?> TryFetchSaleNumberAsync(string saleId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(saleId, out _))
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var sale = await _salesApi.PosSaleGetAsync(saleId, timeout.Token).ConfigureAwait(false);
            return NurCrmReportsApi.TryReadSaleNumber(sale);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Номер продажи {saleId} для чека не получен: {ex.Message}", "PAYMENT");
            return null;
        }
    }

    /// <summary>Имя консультанта в снимок корзины — чек печатается из снимка
    /// (CartReceiptTextBuilder), а сайт печатает строку «Консультант» в чеке.</summary>
    private static string WithConsultantForReceipt(string cartJson, PosCheckoutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConsultantId) || string.IsNullOrWhiteSpace(request.ConsultantName))
            return cartJson;
        var cart = CartJsonHelper.ParseObjectOrEmpty(cartJson);
        cart["consultant_display"] = request.ConsultantName.Trim();
        return cart.ToJsonString();
    }

    /// <summary>Консультант продажи — те же поля, что отправляет сайт (CashierPage, marketSaleConsultant):
    /// consultant_id, consultant_commission_enabled, consultant_commission_percent («0.00» без процента).</summary>
    internal static void AddConsultant(Dictionary<string, string> body, string? consultantId, bool commissionEnabled, string? commissionPercent)
    {
        if (string.IsNullOrWhiteSpace(consultantId))
            return;
        body["consultant_id"] = consultantId.Trim();
        body["consultant_commission_enabled"] = commissionEnabled ? "true" : "false";
        body["consultant_commission_percent"] = commissionEnabled && !string.IsNullOrWhiteSpace(commissionPercent)
            ? commissionPercent.Trim()
            : "0.00";
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

        // 2026-10-03: у продажи «в долг» безналичная предоплата идёт только быстрым путём (cash_amount/card_amount);
        // старому пути transfer_received для долга не отправляем — сервер понял бы его как смешанную оплату.
        if (!string.IsNullOrWhiteSpace(nonCashReceived) && !string.Equals(paymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            body["transfer_received"] = nonCashReceived.Trim();

        // 2026-09-28, BE-07: разбивка смешанной оплаты полями, которые NurCRM теперь хранит в
        // продаже (cash_amount/card_amount). Раньше сервер записывал смешанную целиком без разбивки,
        // и Z-отчёт/сайт относили её к безналу.
        if (string.Equals(paymentMethod, "mixed", StringComparison.OrdinalIgnoreCase))
        {
            body["cash_amount"] = string.IsNullOrWhiteSpace(cashReceived) ? "0.00" : cashReceived.Trim();
            body["card_amount"] = string.IsNullOrWhiteSpace(nonCashReceived) ? "0.00" : nonCashReceived.Trim();
        }

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
            var stockLeft = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

                // 2026-09-29: у товара бывает несколько строк (основной штрихкод и варианты по доп.
                // штрихкодам, пачка и поштучно). Плитка обновляется в UI-потоке позже, и вторая строка
                // считала остаток от старого значения плитки, затирая списание первой, — поэтому
                // остаток уменьшаем от уже уменьшенного в этом чеке.
                var next = Math.Max(0, (stockLeft.TryGetValue(productId, out var left) ? left : tile.Quantity) - soldQty);
                stockLeft[productId] = next;
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

    /// <summary>2026-10-04, клиент: «предоплата в долг — наличкой или безнал — в чеке тоже должно отображаться».
    /// Для чека «в долг» с предоплатой: «debt-noncash» (безналом — NonCashReceived) или «debt-cash»;
    /// CartReceiptTextBuilder печатает «ВНЕСЕНО БЕЗНАЛОМ/НАЛИЧНЫМИ». Серверу уходит прежний «debt».</summary>
    private static string? ReceiptPaymentMethodKey(PosCheckoutRequest request)
    {
        if (!string.Equals(request.PaymentMethod, "debt", StringComparison.OrdinalIgnoreCase))
            return request.PaymentMethod;
        static double Amount(string? s) =>
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (Amount(request.NonCashReceived) > 0.005)
            return "debt-noncash";
        return Amount(request.CashReceived) > 0.005 ? "debt-cash" : request.PaymentMethod;
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
