using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Статусная строка: сеть, смена, баланс кассы.</summary>
public sealed class MainStatusViewModel : ViewModelBase, IDisposable
{
    // 20 с вместо 5 (2026-09-07): индикатор "онлайн" делал GET к серверу каждые 5 секунд — 720 запросов
    // в час с каждой кассы; SyncService и так проверяет связь каждые 45 с.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    /// <summary>Отдельный, гораздо более частый таймер только для отсчёта подписки — чистая
    /// дата-математика без сети, поэтому безопасно тикать раз в секунду. Раньше отсчёт
    /// обновлялся тем же 5-секундным PollInterval, что и сеть/очередь, и секунды в бейдже
    /// заметно "зависали" между обновлениями вместо плавного тиканья.</summary>
    private static readonly TimeSpan SubscriptionCountdownPollInterval = TimeSpan.FromSeconds(1);

    private readonly IConnectivityService _connectivity;
    private readonly IAppSession _session;
    private readonly IDispatcher _dispatcher;
    private readonly IAutonomousAuthService _autonomous;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    private bool _isOnline = true;
    private string _networkModeText = "";
    private string _shiftBalanceText = "Касса: 0.00 сом";
    private string _statusLabel = Tr.T("Онлайн", "Онлайн", "Online", "Çevrimiçi", "Onlayn");
    private int _queuedCount;
    private string _queueText = "";
    private bool _hasQueuedItems;
    private string _subscriptionCountdownText = "";
    private bool _hasSubscriptionCountdown;

    /// <summary>Порог, с которого предупреждение "N дней осталось" сменяется живым отсчётом
    /// до закрытия кассы — раздельные тарифы реагирования на приближение конца подписки:
    /// периодические модалки (MainWindow.MonitorSubscriptionAsync) на дальней дистанции,
    /// постоянно видимый таймер здесь — на ближней.</summary>
    private static readonly TimeSpan SubscriptionCountdownThreshold = TimeSpan.FromHours(3);

    public MainStatusViewModel(
        IConnectivityService connectivity,
        IAppSession session,
        IDispatcher dispatcher,
        IAutonomousAuthService autonomous)
    {
        _connectivity = connectivity;
        _session = session;
        _dispatcher = dispatcher;
        _autonomous = autonomous;
        RefreshFromSession();
        QueuedCount = OfflinePendingSalesStore.PendingCount;
        _ = MonitorConnectivityAsync(_lifetimeCts.Token);
        _ = MonitorSubscriptionCountdownAsync(_lifetimeCts.Token);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (!SetProperty(ref _isOnline, value))
                return;
            StatusLabel = value ? Tr.T("Онлайн", "Онлайн", "Online", "Çevrimiçi", "Onlayn") : Tr.T("Офлайн", "Оффлайн", "Offline", "Çevrimdışı", "Oflayn");

            // Этап 3 бэклога "Доработки": история обрывов связи должна попадать в "Логи и
            // ошибки" (пример из ТЗ: "в 12:37 не было связи с сервером"). Логируется только
            // на РЕАЛЬНОМ переходе состояния (SetProperty выше вернул true), а не на каждом
            // 5-секундном опросе — иначе лог мгновенно забился бы повторами.
            if (value)
                PosLogger.Log("Связь с сервером восстановлена.", "INFORMATION");
            else
                PosLogger.Log("Нет связи с сервером.", "WARNING");
        }
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public string NetworkModeText
    {
        get => _networkModeText;
        set => SetProperty(ref _networkModeText, value ?? "");
    }

    public string ShiftBalanceText
    {
        get => _shiftBalanceText;
        set => SetProperty(ref _shiftBalanceText, value ?? "");
    }

    /// <summary>Сколько чеков ждут отправки на сервер (см. OfflinePendingSalesStore) —
    /// растёт при сбоях связи с сервером, чтобы кассир видел масштаб проблемы.</summary>
    public int QueuedCount
    {
        get => _queuedCount;
        private set
        {
            if (!SetProperty(ref _queuedCount, value))
                return;
            HasQueuedItems = value > 0;
            QueueText = value > 0
                ? Tr.T($"В очереди: {value}", $"Кезекте: {value}",
                    $"Queued: {value}", $"Sırada: {value}", $"Navbatda: {value}")
                : "";
        }
    }

    public string QueueText
    {
        get => _queueText;
        private set => SetProperty(ref _queueText, value ?? "");
    }

    public bool HasQueuedItems
    {
        get => _hasQueuedItems;
        private set => SetProperty(ref _hasQueuedItems, value);
    }

    /// <summary>Бейдж срока подписки в шапке кассы — виден постоянно и тикает (с точностью
    /// до секунд), пока до конца ≤3 дней (см. SubscriptionStatus.IsNearExpiry); иконка
    /// меняется с "⚠" на "⏳" в последние 3 часа (см. SubscriptionCountdownThreshold);
    /// пусто в остальное время.</summary>
    public string SubscriptionCountdownText
    {
        get => _subscriptionCountdownText;
        private set => SetProperty(ref _subscriptionCountdownText, value ?? "");
    }

    public bool HasSubscriptionCountdown
    {
        get => _hasSubscriptionCountdown;
        private set => SetProperty(ref _hasSubscriptionCountdown, value);
    }

    public void RefreshFromSession()
    {
        // Название кассы здесь НЕ показываем: оно уже стоит слева, рядом с логотипом, и в
        // шапке получалось два одинаковых «Основная касса компании» подряд. В центре остаётся
        // только то, чего больше нигде нет, — причина, по которой касса работает без сервера.
        NetworkModeText = _session.IsOfflineBootstrap ? "Офлайн-режим" : "";

        // 2026-09-12: в автономном режиме уже виден отдельный жёлтый баннер "Работа в
        // автономном режиме" прямо в каталоге — этот же текст ещё раз в шапке (рядом с именем
        // кассы) кассир попросил убрать как дублирующий. Для обычного (временно недоступного
        // NurCRM) офлайна текст остаётся — там он несёт полезную причину простоя.
        if (!_autonomous.IsCurrentSessionAutonomous
            && _session.IsOfflineBootstrap && !string.IsNullOrWhiteSpace(_session.OfflineBootstrapMessage))
            NetworkModeText = _session.OfflineBootstrapMessage;
    }

    public void SetShiftBalance(decimal balance) =>
        ShiftBalanceText = $"Касса: {balance:0.00} сом";

    private void ApplySubscriptionCountdown(SubscriptionStatus? status)
    {
        if (status is null || status.IsExpired)
        {
            HasSubscriptionCountdown = false;
            SubscriptionCountdownText = "";
            return;
        }

        var remaining = status.EndDate - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero || !status.IsNearExpiry)
        {
            HasSubscriptionCountdown = false;
            SubscriptionCountdownText = "";
            return;
        }

        // Настоящий тикающий отсчёт (с секундами) всё время, пока подписка "скоро истекает" —
        // не просто текст "N дней", обновляющийся раз в час. Формат меняется только когда
        // счёт на дни уже не нужен (последний день), иконка — когда счёт идёт на часы.
        var clamped = new TimeSpan(Math.Max(remaining.Ticks, 0));
        var urgent = clamped <= SubscriptionCountdownThreshold;
        var icon = urgent ? "⏳" : "⚠";
        var timePart = clamped.Days > 0
            ? $"{clamped.Days}д {clamped:hh\\:mm\\:ss}"
            : $"{clamped:hh\\:mm\\:ss}";
        SubscriptionCountdownText = Tr.T(
            $"{icon} Касса закроется через {timePart}",
            $"{icon} Касса {timePart} ичинде жабылат");

        HasSubscriptionCountdown = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private async Task MonitorConnectivityAsync(CancellationToken ct)
    {
        // 2026-09-10: автономный (офлайн, без NurCRM) режим принципиально не обращается в
        // интернет — сетевой опрос "онлайн ли мы" здесь был бы (а) бесполезным сетевым вызовом
        // каждые 20с в режиме, который обязан работать вообще без интернета, и (б) визуально
        // противоречил бы соседнему бейджу "Автономный режим — работа без интернета" (индикатор
        // показывал бы "Онлайн" зелёным, если на ПК физически есть интернет — а он может быть,
        // автономный режим просто им не пользуется). QueuedCount не нуждается в повторном опросе
        // по той же причине: автономные продажи навсегда исключены из PendingCount (см.
        // OfflinePendingSalesStore.IsPendingLike), значение из конструктора уже верно (0).
        if (_autonomous.IsCurrentSessionAutonomous)
        {
            await _dispatcher.InvokeAsync(() => IsOnline = false).ConfigureAwait(false);
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var online = await _connectivity.IsOnlineAsync(ct).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() => IsOnline = online).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await _dispatcher.InvokeAsync(() => IsOnline = false).ConfigureAwait(false);
            }

            try
            {
                var queued = OfflinePendingSalesStore.PendingCount;
                await _dispatcher.InvokeAsync(() => QueuedCount = queued).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Не критично — просто пропускаем это обновление счётчика.
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MonitorSubscriptionCountdownAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SubscriptionCountdownPollInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = CompanyInfoService.GetCachedSubscriptionStatus();
                await _dispatcher.InvokeAsync(() => ApplySubscriptionCountdown(status)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Не критично — просто пропускаем это обновление отсчёта.
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
