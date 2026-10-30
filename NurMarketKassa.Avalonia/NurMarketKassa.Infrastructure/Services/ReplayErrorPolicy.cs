namespace NurMarketKassa.Services;

/// <summary>Что делать с чеком очереди после ответа сервера.</summary>
internal enum ReplayVerdict
{
    /// <summary>Временная причина (связь, шлюз, чужой ответ): чек остаётся в очереди и уйдёт сам.</summary>
    Retry,

    /// <summary>Сервер отказал по тарифу/подписке/правам (402/403 от NurCRM): чек ждёт в очереди, владельцу — оповещение.</summary>
    RetryNotifyOwner,

    /// <summary>Отказ по существу (400/404/422…): чек в «Ошибка отправки», дальше — ограниченные автоповторы и кнопка.</summary>
    Rejected,
}

/// <summary>2026-10-29 (жалоба «связь отваливается, чеки сами не уходят», тариф «Старт»): решение «повторить или отклонить» вынесено в
/// чистую функцию (без сети и базы) — её же прогоняет мини-проверка без UI. Раньше любой 4xx, кроме 401/408/409/423/425/429, навсегда
/// уводил чек в failed, даже если ответ дал не NurCRM, а прокси, страница провайдера или защита шлюза (HTML без JSON), и даже 402/403
/// (тариф, подписка, права), которые владелец может устранить.</summary>
internal static class ReplayErrorPolicy
{
    /// <summary>Паузы автоповторов «Ошибки отправки»: 3 мин, 15 мин, 1 час — всего три круга, потом решает человек.</summary>
    internal static readonly TimeSpan[] AutoRetryDelays = [TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <param name="status">Код ответа (null — ответа не было).</param>
    /// <param name="hasJsonPayload">Тело ответа — JSON-объект/массив (так отвечает NurCRM). HTML, пустое тело, текст — ответ не от NurCRM.</param>
    internal static ReplayVerdict Classify(int? status, bool hasJsonPayload)
    {
        // Нет ответа, 5xx, таймаут шлюза, «слишком много запросов», «ключ ещё выполняется» (409/423/425) и 401 (сессия — не вина чека).
        if (status is null or >= 500 or 401 or 408 or 409 or 423 or 425 or 429)
            return ReplayVerdict.Retry;

        if (status is >= 400 and < 500)
        {
            // Прокси, защита шлюза, страница провайдера («войдите в Wi-Fi»): чек до NurCRM не дошёл, отказа по существу нет.
            if (!hasJsonPayload)
                return ReplayVerdict.Retry;
            if (status is 402 or 403)
                return ReplayVerdict.RetryNotifyOwner;
            return ReplayVerdict.Rejected;
        }

        // 1xx/3xx — не окончательный отказ сервера.
        return ReplayVerdict.Retry;
    }

    /// <summary>2026-10-29 (приёмка 1.17.65, Б-1): автоповтор только для свежих чеков — не старше суток и не из закрытой смены. Старый
    /// отклонённый чек молча не повторяем: за это время причина могла уйти (товар пришёл, цена изменилась), и на сервере появилась бы
    /// продажа недельной давности (а если кассир уже пробил её руками — дубль). Такой чек остаётся «Ошибкой отправки» с кнопкой.</summary>
    internal static readonly TimeSpan AutoRetryMaxAge = TimeSpan.FromHours(24);

    /// <summary>Лимит времени на пересборку серверной корзины одного чека при досылке старым путём: большой чек (сотни позиций, по
    /// запросу на позицию) не держит очередь дольше этого срока — уходит в конец и пробуется на следующем проходе.</summary>
    internal static readonly TimeSpan ReplayRebuildBudget = TimeSpan.FromSeconds(60);

    /// <summary>Не повторять сразу после запуска кассы: при старте не гоняем долгие повторы одним проходом.</summary>
    internal static readonly TimeSpan AutoRetryStartupGrace = TimeSpan.FromMinutes(2);

    /// <summary>Не больше одного чека за проход очереди — иначе пачка «Ошибок отправки» разом занимает очередь.</summary>
    internal const int AutoRetryMaxPerPass = 1;

    /// <summary>Свежий ли чек для автоповтора. shiftClosed — смена чека известна и она уже не текущая.</summary>
    internal static bool IsFreshForAutoRetry(DateTimeOffset createdAt, DateTimeOffset? checkoutSubmittedAt, DateTimeOffset now, bool shiftClosed)
    {
        if (shiftClosed || now - createdAt > AutoRetryMaxAge)
            return false;
        // Старым путём (корзина + отметка отправки) не повторяем чек, чей checkout ушёл больше суток назад.
        return checkoutSubmittedAt is not { } submitted || now - submitted <= AutoRetryMaxAge;
    }

    /// <summary>Пора ли вернуть «Ошибку отправки» в очередь автоматически. «Требует проверки» (неясно, прошла ли оплата) и
    /// уже проведённые чеки — никогда: это решает человек. Старые чеки (см. <see cref="IsFreshForAutoRetry"/>) — тоже никогда.</summary>
    internal static bool ShouldAutoRequeue(int autoRetries, DateTimeOffset lastAttempt, DateTimeOffset now, bool checkoutCompleted,
        int replayDeferrals, int needsReviewDeferrals, DateTimeOffset createdAt, DateTimeOffset? checkoutSubmittedAt, bool shiftClosed)
    {
        if (checkoutCompleted || replayDeferrals >= needsReviewDeferrals || autoRetries >= AutoRetryDelays.Length || autoRetries < 0)
            return false;
        if (!IsFreshForAutoRetry(createdAt, checkoutSubmittedAt, now, shiftClosed))
            return false;
        return now - lastAttempt >= AutoRetryDelays[autoRetries];
    }
}
