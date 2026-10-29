namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-29, жалоба владельца «синхронизация кассы с админкой очень медленная»: единое место для интервалов фоновых обменов с сервером,
/// «Синхронизация: HH:mm:ss» в строке состояния и принудительной синхронизации.
///
/// API:
/// • <see cref="ForceSyncAllAsync"/> — одна функция «обновить всё сейчас» (каталог + остатки, общие настройки компании, смена, очередь чеков).
///   Её зовёт общая кнопка «Обновить» (⟳ / F5) во всех видах кассы и в программе владельца. Не бросает: каждый шаг сам глотает ошибки и
///   возвращается в <see cref="SyncAllResult"/>. Два одновременных вызова сливаются в один.
/// • <see cref="RecordSuccess"/> — любой цикл (лента, настройки, очередь, пульс продаж) отмечает здесь удачный обмен; время показывает подсказка
///   индикатора «Онлайн» (<see cref="StatusLine"/>) и событие <see cref="Changed"/>.
/// • крючки <see cref="CatalogRefresher"/>, <see cref="QueueFlusher"/>, <see cref="ShiftRefresher"/> регистрируют владельцы данных
///   (кэш каталога, SyncService, главное окно) — Infrastructure не знает про окна.
/// • <see cref="Jitter"/> и <see cref="ShouldPause"/> — общий «бюджет запросов»: циклы не стартуют в один и тот же миг (разброс ±15 %) и молчат,
///   пока сервер просит паузу (429, ApiThrottle), лежит (ServerOutageMonitor) или касса работает локально.
/// </summary>
public static class SyncCenter
{
    /// <summary>Опрос общих настроек компании и прав сотрудников (ETag / версия документа). Было: раз за вход, до перезапуска.</summary>
    public static TimeSpan CompanySettingsInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Пульс «появились ли чужие продажи» — один лёгкий запрос последних чеков (программа владельца и касса).</summary>
    public static TimeSpan SalesPulseInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Проход очереди офлайн-чеков и связи (SyncService). Было 45 / 90 с.</summary>
    public static TimeSpan QueueInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Сторож смены на кассе («Касса: N», смена закрыта на другом устройстве). Было 90 с.</summary>
    public static TimeSpan ShiftWatchInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>«Сводка» программы владельца, пока она на экране. Было 60 с.</summary>
    public static TimeSpan OverviewInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Не чаще: полная загрузка каталога по сигналу «чужая продажа» (остатки). Каталог ~350 товаров — 2 страницы.</summary>
    public static TimeSpan StockReloadMinSpacing { get; set; } = TimeSpan.FromSeconds(60);

    private static long _lastOkTicks;
    private static string _lastWhat = "";
    private static readonly Random Rng = new();

    /// <summary>Что-то успешно обменялось с сервером (время, что именно). Тихо, без журнала.</summary>
    public static event Action? Changed;

    public static DateTime? LastSyncLocal
    {
        get
        {
            var t = Interlocked.Read(ref _lastOkTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc).ToLocalTime();
        }
    }

    public static string LastSyncWhat => _lastWhat;

    /// <summary>Отметить удачный обмен (что: catalog, feed, settings, queue, sales, shift).</summary>
    public static void RecordSuccess(string what)
    {
        Interlocked.Exchange(ref _lastOkTicks, DateTime.UtcNow.Ticks);
        _lastWhat = what;
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"SYNC: обработчик события не отработал: {ex.Message}", "WARNING"); }
    }

    /// <summary>Строка для подсказки индикатора: «Синхронизация: 14:03:22 · в очереди: 2». Пусто, если обменов ещё не было.</summary>
    public static string StatusLine()
    {
        var at = LastSyncLocal;
        if (at is null)
            return "";
        var queued = 0;
        try { queued = OfflinePendingSalesStore.PendingCount; } catch { /* не критично */ }
        var line = Tr.T($"Синхронизация: {at:HH:mm:ss}", $"Шайкештирүү: {at:HH:mm:ss}", $"Synced: {at:HH:mm:ss}",
            $"Senkronizasyon: {at:HH:mm:ss}", $"Sinxronlash: {at:HH:mm:ss}");
        if (queued > 0)
            line += Tr.T($" · в очереди: {queued}", $" · кезекте: {queued}", $" · queued: {queued}", $" · sırada: {queued}", $" · navbatda: {queued}");
        return line;
    }

    /// <summary>Интервал с разбросом ±<paramref name="fraction"/>: десять касс одной компании не бьют в сервер в одну секунду.</summary>
    public static TimeSpan Jitter(TimeSpan interval, double fraction = 0.15)
    {
        double k;
        lock (Rng)
            k = 1 + ((Rng.NextDouble() * 2) - 1) * fraction;
        return TimeSpan.FromMilliseconds(Math.Max(1000, interval.TotalMilliseconds * k));
    }

    /// <summary>Сервер сейчас просит паузу / лежит / касса работает локально — фоновые обмены пропускают такт.</summary>
    public static bool ShouldPause()
    {
        try
        {
            return ApiThrottle429() || ServerOutageMonitor.IsOutage || OfflineModeHelper.SellLocally;
        }
        catch
        {
            return false;
        }
    }

    private static bool ApiThrottle429() => Api.ApiThrottle.RemainingBlock > TimeSpan.Zero;

    // ---------------------------------------------------------------- принудительная синхронизация

    /// <summary>Крючок: обновить каталог и остатки (лента + полная загрузка). Задаёт кэш каталога.</summary>
    public static Func<CancellationToken, Task>? CatalogRefresher { get; set; }

    /// <summary>Крючок: досыл очереди офлайн-чеков сейчас. Задаёт SyncService.</summary>
    public static Func<CancellationToken, Task>? QueueFlusher { get; set; }

    /// <summary>Крючок: обновить смену и «Касса: N». Задаёт главное окно кассы.</summary>
    public static Func<CancellationToken, Task>? ShiftRefresher { get; set; }

    public sealed record SyncAllResult(bool Catalog, bool Settings, bool Shift, bool Queue, bool Skipped);

    private static Task<SyncAllResult>? _forceInFlight;
    private static readonly object ForceGate = new();

    /// <summary>Принудительно синхронизировать всё: общие настройки компании (права, коды, реквизиты), очередь чеков, смену, каталог и остатки.
    /// Шаги идут параллельно; каждый — со своим таймаутом 25 с. Корзину и цены строк открытого чека не трогает.
    /// <paramref name="includeCatalog"/>/<paramref name="includeShift"/> = false — шаг пропустить (кнопка «Обновить» кассы уже сама
    /// перечитывает каталог и смену).</summary>
    public static Task<SyncAllResult> ForceSyncAllAsync(CancellationToken ct = default, bool includeCatalog = true, bool includeShift = true)
    {
        lock (ForceGate)
        {
            if (_forceInFlight is { IsCompleted: false } running)
                return running;
            return _forceInFlight = ForceCoreAsync(ct, includeCatalog, includeShift);
        }
    }

    private static async Task<SyncAllResult> ForceCoreAsync(CancellationToken ct, bool includeCatalog, bool includeShift)
    {
        if (OfflineModeHelper.SellLocally || ServerOutageMonitor.IsOutage)
        {
            PosLogger.Log("SYNC: принудительная синхронизация пропущена — нет связи с сервером.", "SYNC");
            return new SyncAllResult(false, false, false, false, true);
        }

        async Task<bool> Step(string name, Func<CancellationToken, Task>? hook)
        {
            if (hook is null)
                return false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                await hook(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"SYNC: шаг «{name}» не выполнен: {ex.Message}", "SYNC");
                return false;
            }
        }

        var settings = Step("settings", SettingsCloudSync.RefreshCompanyNowAsync);
        var queue = Step("queue", QueueFlusher);
        var shift = includeShift ? Step("shift", ShiftRefresher) : Task.FromResult(false);
        var catalog = includeCatalog ? Step("catalog", CatalogRefresher) : Task.FromResult(false);
        await Task.WhenAll(settings, queue, shift, catalog).ConfigureAwait(false);
        var result = new SyncAllResult(catalog.Result, settings.Result, shift.Result, queue.Result, false);
        PosLogger.Log($"SYNC: принудительная синхронизация — каталог {catalog.Result}, настройки {settings.Result}, смена {shift.Result}, очередь {queue.Result}.", "SYNC");
        if (catalog.Result || settings.Result || shift.Result || queue.Result)
            RecordSuccess("force");
        return result;
    }
}
