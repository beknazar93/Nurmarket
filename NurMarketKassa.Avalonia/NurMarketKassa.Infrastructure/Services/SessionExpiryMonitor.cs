namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-30, живой баг («Ошибка закрытия смены: Сессия недействительна… Нажмите «Выйти»…»): сервер отозвал вход
/// (вошли с другого ПК/телефона, refresh-токен отклонён) — раньше кассир видел тупиковую ошибку в каждом окне, а журнал
/// засыпали повторы. Теперь об этом узнаём ОДИН раз: флаг, одна запись в журнале и событие <see cref="Expired"/> — окно
/// «Сессия истекла. Войдите снова» и надпись «🔑 Войдите снова» в шапке. Очередь продаж, смена и корзины не трогаются:
/// после входа тем же аккаунтом всё уходит на сервер само. Сбрасывается успешным входом (<see cref="Reset"/>).
/// </summary>
public static class SessionExpiryMonitor
{
    private static int _expired;

    /// <summary>Сессия недействительна: ждём повторного входа.</summary>
    public static bool IsExpired => Volatile.Read(ref _expired) != 0;

    /// <summary>Сессия объявлена недействительной (один раз до следующего входа). Вызывается в фоновом потоке.</summary>
    public static event Action? Expired;

    /// <summary>Состояние изменилось (объявлено или сброшено) — обновить надпись в шапке.</summary>
    public static event Action? Changed;

    /// <summary>Объявляет сессию недействительной. true — это первое объявление (повторные ничего не делают и не пишут в журнал).</summary>
    public static bool Report(string source)
    {
        if (Interlocked.Exchange(ref _expired, 1) != 0)
            return false;
        PosLogger.Log($"Сессия недействительна ({source}) — нужен повторный вход; очередь и данные сохранены.", "AUTH");
        Raise(Changed);
        Raise(Expired);
        return true;
    }

    /// <summary>Успешный вход (или восстановление сессии): снова можно объявлять.</summary>
    public static void Reset()
    {
        if (Interlocked.Exchange(ref _expired, 0) != 0)
        {
            PosLogger.Log("Сессия восстановлена — вход выполнен.", "AUTH");
            Raise(Changed);
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.65, «Важно»): сессию теряет только настоящий отказ входа: клиент уже объявил её недействительной (refresh отклонён,
    /// токена нет) либо ошибка — наш текст «Сессия недействительна». Голый 401, пришедший при живом refresh-токене (в том числе после удачного обновления
    /// токена: нет прав на конкретный запрос), сессию недействительной не объявляет — раньше CashShiftService объявлял её на любой 401.</summary>
    public static bool IsSessionLost(Exception? ex) =>
        IsAuthFailure(ex) && (IsExpired || (ex is ApiException api && api.Message.StartsWith("Сессия недействительна", StringComparison.Ordinal)));

    /// <summary>Ошибка — отказ по сессии (401 или наш текст «Сессия недействительна»).</summary>
    public static bool IsAuthFailure(Exception? ex) =>
        ex is ApiException { StatusCode: 401 }
        || (ex is ApiException api && api.Message.StartsWith("Сессия недействительна", StringComparison.Ordinal));

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Обработчик сессии упал: {ex.Message}", "WARNING"); }
    }
}
