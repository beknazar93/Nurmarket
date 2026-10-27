namespace NurMarketKassa.Services;

/// <summary>2026-10-16, ревью: ключ идемпотентности денежной операции, который живёт до ответа сервера. Повтор ТОЙ ЖЕ
/// операции (та же подпись: чек, строки, суммы) после ответа, который не дошёл (таймаут, обрыв, 5xx, 408, 429), уходит
/// с тем же ключом — сервер вернёт прежний итог, а не проведёт операцию второй раз. Новый ключ — после успеха, после
/// отказа сервера по существу (остальные 4xx), если подпись изменилась или ключ старше <c>lifetime</c>: старый ключ не
/// должен подменить итогом прошлой операции новую, настоящую операцию с тем же составом.
/// Раньше эта логика была скопирована в ReturnSaleDialog, ExchangeWindow, PurchaseReceivingService, PosCheckoutService.</summary>
public sealed class PendingIdempotencyKey
{
    private readonly TimeSpan? _lifetime;
    private string? _key;
    private string? _signature;
    private DateTime _createdUtc;

    public PendingIdempotencyKey(TimeSpan? lifetime = null) => _lifetime = lifetime;

    private bool IsAlive => _key != null && (_lifetime is not { } life || DateTime.UtcNow - _createdUtc <= life);

    /// <summary>Ключ для операции с этой подписью: прежний, если прошлая попытка той же операции осталась без ответа.</summary>
    public string Get(string signature)
    {
        if (!IsAlive || !string.Equals(_signature, signature, StringComparison.Ordinal))
        {
            _key = Guid.NewGuid().ToString();
            _signature = signature;
            _createdUtc = DateTime.UtcNow;
        }
        return _key!;
    }

    /// <summary>2026-10-27 (аудит кассы, С-3): ключ прошлой попытки, пережившей перезапуск кассы (PendingDebtPrepaymentStore): та же подпись
    /// — тот же ключ, и сервер вернёт прежний итог вместо второй оплаты.</summary>
    public void Restore(string key, string signature)
    {
        _key = key;
        _signature = signature;
        _createdUtc = DateTime.UtcNow;
    }

    /// <summary>Сервер провёл операцию (или касса убедилась, что она уже проведена) — следующая будет новой.</summary>
    public void OnSuccess() => _key = null;

    /// <summary>true — исход операции неизвестен, ключ оставлен для повтора; false — сервер отказал по существу (ключ
    /// забыт) или операция уже завершилась (OnSuccess), а ошибка случилась после неё.</summary>
    public bool OnError(Exception ex)
    {
        if (_key == null)
            return false;
        if (IsOutcomeUnknown(ex))
            return true;
        _key = null;
        return false;
    }

    /// <summary>Ответ не дошёл или сервер не ответил по существу: таймаут/обрыв, 5xx, 408, 429 — операция могла пройти.</summary>
    public static bool IsOutcomeUnknown(Exception ex) =>
        ex is not ApiException { StatusCode: >= 400 and < 500 and not 408 and not 429 };
}
