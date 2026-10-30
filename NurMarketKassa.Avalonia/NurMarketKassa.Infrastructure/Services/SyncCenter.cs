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

    // ---------------------------------------------------------------- 2026-10-29: адаптивный опрос и подсказки (отчёт «Скорость синхронизации»)

    /// <summary>Лента каталога при открытом чеке и 10 с после действия/подсказки (было всегда 8 с).</summary>
    public static TimeSpan FeedActiveInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Лента при свёрнутом окне или простое больше <see cref="IdleAfter"/>.</summary>
    public static TimeSpan FeedIdleInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Лента при простое больше <see cref="DeepIdleAfter"/>.</summary>
    public static TimeSpan FeedDeepIdleInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Пульс продаж программы владельца, пока окно активно (владелец смотрит «Сводку»).</summary>
    public static TimeSpan OwnerPulseActiveInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Нет ввода (мышь/клавиатура/сканер) дольше — «простой».</summary>
    public static TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(10);

    public static TimeSpan DeepIdleAfter { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Окно свёрнуто или скрыто (ставит SyncActivityWatcher главного окна / окна владельца).</summary>
    public static volatile bool WindowMinimized;

    /// <summary>2026-10-30 (оптимизация кассы, Б1): Android в фоне (MainActivity.OnStop) — окно формально «видимо», но экрана нет.
    /// Ставит MainActivity; SyncActivityWatcher его не сбрасывает. Опросы тогда идут как у свёрнутого окна, а ещё реже (60 с).</summary>
    public static volatile bool AppInBackground;

    /// <summary>Окно программы сейчас активно.</summary>
    public static volatile bool WindowActive = true;

    /// <summary>У кассира есть строки в открытом чеке (ставит SyncActivityWatcher).</summary>
    public static volatile bool OpenCheck;

    /// <summary>Сигнал «пора опросить сейчас»: будит цикл вместо ожидания такта. Несколько сигналов подряд сливаются в один.</summary>
    public sealed class KickSignal
    {
        private readonly SemaphoreSlim _s = new(0, 1);

        public void Kick()
        {
            try { _s.Release(); }
            catch (SemaphoreFullException) { /* уже есть непрочитанный сигнал */ }
        }

        public Task<bool> WaitAsync(TimeSpan timeout) => _s.WaitAsync(timeout);
    }

    /// <summary>Разбудить цикл ленты каталога (CatalogFeedSync).</summary>
    public static readonly KickSignal FeedKick = new();

    /// <summary>Разбудить пульс продаж (RemoteSalesPulse).</summary>
    public static readonly KickSignal PulseKick = new();

    private static long _boostUntilTicks;

    private static bool Boosted => DateTime.UtcNow.Ticks < Interlocked.Read(ref _boostUntilTicks);

    /// <summary>Быстрый опрос на <paramref name="span"/> (по умолчанию 10 с): после своей записи или подсказки соседа.</summary>
    public static void BoostFeed(TimeSpan? span = null)
    {
        var until = DateTime.UtcNow.Ticks + (span ?? TimeSpan.FromSeconds(10)).Ticks;
        if (until > Interlocked.Read(ref _boostUntilTicks))
            Interlocked.Exchange(ref _boostUntilTicks, until);
    }

    private static long _lastInputTicks = Environment.TickCount64;

    /// <summary>2026-10-30 (Б1): на Android/Linux/macOS нет GetLastInputInfo — окно и активность сами отмечают здесь касание/клавишу/клик
    /// (MainActivity.DispatchTouchEvent/DispatchKeyEvent, SyncActivityWatcher). На Windows не используется.</summary>
    public static void NoteInput() => Interlocked.Exchange(ref _lastInputTicks, Environment.TickCount64);

    /// <summary>Сколько нет ввода. Windows — GetLastInputInfo (весь сеанс); другие системы — время последнего касания/клавиши в программе
    /// (было всегда ноль, поэтому «простоя» не наступало никогда). При сбое Windows — ноль («кто-то работает»).</summary>
    public static TimeSpan InputIdle()
    {
        if (!OperatingSystem.IsWindows())
            return TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastInputTicks)));
        try
        {
            var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
                return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.MarshalDirectiveException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>2026-10-30 (Б3): «тихий режим» — окно свёрнуто, программа в фоне (Android) или нет ввода дольше <see cref="IdleAfter"/>.</summary>
    public static bool IsQuiet() => WindowMinimized || AppInBackground || InputIdle() >= IdleAfter;

    /// <summary>2026-10-30 (Б3): интервал фонового опроса «сторожа смены» и общих настроек компании: обычный (30 с) при работе,
    /// ×4 (120 с) в тихом режиме — как у ленты каталога (8 → 30 с). Кассир вернулся — следующий такт снова обычный.</summary>
    public static TimeSpan QuietInterval(TimeSpan normal) => IsQuiet() ? TimeSpan.FromTicks(normal.Ticks * 4) : normal;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Период ленты каталога сейчас: 3 с при открытом чеке и после действия/подсказки, обычный (<paramref name="normal"/>, 8 с),
    /// 30 с при свёрнутом окне или простое больше 10 мин, 60 с при простое больше часа.</summary>
    public static TimeSpan FeedInterval(TimeSpan normal)
    {
        if (Boosted)
            return FeedActiveInterval;
        if (AppInBackground)
            return FeedDeepIdleInterval; // 2026-10-30 (Б1): Android в фоне — 60 с вместо 8 (подсказка соседа FeedKick будит сразу)
        if (WindowMinimized)
            return FeedIdleInterval;
        var idle = InputIdle();
        if (idle >= DeepIdleAfter)
            return FeedDeepIdleInterval;
        if (idle >= IdleAfter)
            return FeedIdleInterval;
        return OpenCheck ? FeedActiveInterval : normal;
    }

    /// <summary>Период пульса продаж сейчас. Владелец: 5 с при активном окне, 20 с при неактивном, 30 с в простое, 60 с свёрнутое.
    /// Касса: обычный <see cref="SalesPulseInterval"/>, 60 с в простое или при свёрнутом окне. После подсказки — 3 с.</summary>
    public static TimeSpan PulseInterval()
    {
        if (Boosted)
            return TimeSpan.FromSeconds(3);
        if (WindowMinimized || AppInBackground)
            return TimeSpan.FromSeconds(60);
        var idle = InputIdle() >= IdleAfter;
        if (AppMode.IsOwner)
            return idle ? TimeSpan.FromSeconds(30) : WindowActive ? OwnerPulseActiveInterval : TimeSpan.FromSeconds(20);
        return idle ? TimeSpan.FromSeconds(60) : SalesPulseInterval;
    }

    /// <summary>Ждать следующего такта: не дольше текущего <paramref name="interval"/> (±15 %, пересчитывается каждые 2 с — кассир вернулся
    /// из простоя, и такт сокращается), но раньше — по сигналу <paramref name="kick"/>. Между тактами не меньше <paramref name="minGap"/>.</summary>
    public static async Task WaitAdaptiveAsync(Func<TimeSpan> interval, KickSignal kick, TimeSpan minGap)
    {
        double k;
        lock (Rng)
            k = 1 + ((Rng.NextDouble() * 2) - 1) * 0.15;
        var started = Environment.TickCount64;
        var slice = TimeSpan.FromSeconds(2);
        while (true)
        {
            var target = TimeSpan.FromMilliseconds(Math.Max(1000, interval().TotalMilliseconds * k));
            var left = target - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (left <= TimeSpan.Zero)
                return;
            if (await kick.WaitAsync(left < slice ? left : slice).ConfigureAwait(false))
            {
                var gap = minGap - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (gap > TimeSpan.Zero)
                    await Task.Delay(gap).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Подсказки, которые принимаем от соседей по локальной сети и от телефонов: только тип, без данных.</summary>
    public static bool IsKnownHint(string? kind) => kind is "sale" or "product" or "stock" or "price";

    /// <summary>Эта программа что-то записала (продажа, правка товара/цены/прихода): быстрый опрос и подсказка соседям — другим кассам, программе
    /// владельца (локальная сеть) и подключённым телефонам. Ничего секретного не передаётся: тип события.</summary>
    public static void NoteLocalChange(string kind)
    {
        if (!IsKnownHint(kind))
            return;
        // 2026-10-30 (скорость массовой загрузки): пока идёт массовая запись товаров, подсказка на каждый запрос не нужна (ускоренный опрос ленты,
        // рассылка соседям и телефонам на каждый из тысяч запросов) — одна общая подсказка уйдёт в конце (QuietScope.Dispose).
        if (Volatile.Read(ref _quietWrites) > 0)
            return;
        BoostFeed();
        try { Lan.LanSyncService.Instance.NotifyPeers(kind); }
        catch (Exception ex) { PosLogger.Log($"SYNC: подсказка соседям не ушла: {ex.Message}", "WARNING"); }
        try { PhoneLink.PhoneLinkEvents.Publish(kind); }
        catch (Exception ex) { PosLogger.Log($"SYNC: событие для телефонов не записано: {ex.Message}", "WARNING"); }
    }

    private static int _quietWrites;

    /// <summary>2026-10-30: массовая запись товаров (мастер загрузки, приход пачкой): на время работы подсказки <see cref="NoteLocalChange"/> молчат,
    /// при завершении последнего такого участка уходит одна общая подсказка «product».</summary>
    public static IDisposable QuietLocalChanges()
    {
        Interlocked.Increment(ref _quietWrites);
        return new QuietScope();
    }

    private sealed class QuietScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            if (Interlocked.Decrement(ref _quietWrites) == 0)
                NoteLocalChange("product");
        }
    }

    /// <summary>Пришла подсказка от соседа или телефона: сразу читаем ленту (а при продаже — и чеки компании). Данных в подсказке нет —
    /// всё берётся с NurCRM, права и правильность не страдают.</summary>
    public static void OnPeerHint(string kind)
    {
        if (!IsKnownHint(kind))
            return;
        BoostFeed();
        FeedKick.Kick();
        if (kind != "sale")
            return;
        PulseKick.Kick();
        // Сервер мог ещё дописывать продажу — один повтор через 2 с (дёшево: пустой такт ленты ~200 байт).
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
        {
            FeedKick.Kick();
            PulseKick.Kick();
        }, TaskScheduler.Default);
    }

    /// <summary>Подсказка от телефона: как от соседа, плюс передаём её дальше (другие кассы, программа владельца, остальные телефоны).</summary>
    public static void OnPhoneHint(string kind)
    {
        if (!IsKnownHint(kind))
            return;
        OnPeerHint(kind);
        NoteLocalChange(kind);
    }

    // 2026-10-30 (оптимизация кассы, С7): сколько запросов к серверу NurCRM ушло — раз в 5 минут строка в журнал (CatalogFeedSync.LogStatsIfDue),
    // чтобы вместо оценок по коду были цифры «запросов в минуту». Считает TransportTuningHandler (все запросы API).
    private static long _requestCount;

    public static void CountRequest() => Interlocked.Increment(ref _requestCount);

    /// <summary>Забрать и обнулить счётчик запросов.</summary>
    public static long TakeRequestCount() => Interlocked.Exchange(ref _requestCount, 0);

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
