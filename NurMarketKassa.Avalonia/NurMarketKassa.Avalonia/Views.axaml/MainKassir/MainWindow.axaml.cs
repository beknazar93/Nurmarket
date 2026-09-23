using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IAppSession _session;
    private readonly MainWindowHostBridge _hostBridge;
    private readonly ICashShiftService _cashShiftService;
    private readonly IShiftStateService _shiftStateService;
    private readonly IUserPrompts _prompts;
    private readonly IPermissionService _permissions;
    private readonly AvaloniaCustomerDisplayService _customerDisplay;
    private readonly IVoiceControlService _voiceControl;
    private readonly PosHotkeyService _hotkeys = new();
    private readonly IBarcodeInputService _barcodeInputService;
    private readonly CancellationTokenSource _windowCts = new();
    private readonly ApplicationStateService _applicationStateService = new();

    private bool _appInitialized;
    private bool _whatsNewShown;
    private bool _allowMainWindowClose;
    private bool _closeFlowActive;
    private decimal? _shiftCashBalance;
    private ShiftBalanceHelper.ShiftTotals? _shiftTotals;
    private double _lastLoggedCatalogWidth = -1;
    private bool _subscriptionMonitorStarted;
    private bool _subscriptionExpiryHandled;
    private bool _offlineGraceMonitorStarted;
    private bool _offlineGraceExceededHandled;
    private static readonly TimeSpan OfflineGraceCheckInterval = TimeSpan.FromMinutes(15);
    private DateTimeOffset? _lastSubscriptionAlertAt;
    private DateTimeOffset? _lastSubscriptionServerSyncAt;
    private static readonly TimeSpan SubscriptionAlertInterval = TimeSpan.FromMinutes(15);
    /// <summary>Локальный пересчёт отсчёта (дата минус текущее время) — дешёвый, без сети,
    /// поэтому тикает часто. Обращение к серверу происходит отдельно и гораздо реже —
    /// см. SubscriptionServerSyncIntervalNormal/Urgent.</summary>
    private static readonly TimeSpan SubscriptionCheckInterval = TimeSpan.FromSeconds(15);
    /// <summary>Ниже этого порога до конца подписки периодические модалки уступают место
    /// постоянному отсчёту в статус-баре (MainStatusViewModel.SubscriptionCountdownText) —
    /// не дублируем предупреждение двумя разными UI одновременно.</summary>
    private static readonly TimeSpan SubscriptionModalCutoff = TimeSpan.FromHours(3);
    /// <summary>Ниже этого порога до конца подписки — "постоянная синхронизация" с сервером
    /// (SubscriptionServerSyncIntervalUrgent), чтобы факт оплаты (продление даты) подхватился
    /// быстро и предупреждение исчезло без ожидания следующего входа в кассу.</summary>
    private static readonly TimeSpan SubscriptionServerSyncUrgentThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan SubscriptionServerSyncIntervalNormal = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SubscriptionServerSyncIntervalUrgent = TimeSpan.FromSeconds(20);
    private double _lastLoggedCartWidth = -1;

    private ColumnDefinition CatalogColumn => MainContentGrid.ColumnDefinitions[0];
    private ColumnDefinition CartColumn => MainContentGrid.ColumnDefinitions[2];
    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public MainWindow() : this(
        ResolveService<MainWindowViewModel>(),
        ResolveService<IAppSession>(),
        ResolveService<MainWindowHostBridge>(),
        ResolveService<ICashShiftService>(),
        ResolveService<IShiftStateService>(),
        ResolveService<IUserPrompts>(),
        ResolveService<AvaloniaCustomerDisplayService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public MainWindow(
        MainWindowViewModel viewModel,
        IAppSession session,
        MainWindowHostBridge hostBridge,
        ICashShiftService cashShiftService,
        IShiftStateService shiftStateService,
        IUserPrompts prompts,
        AvaloniaCustomerDisplayService customerDisplay)
    {
        _viewModel = viewModel;
        _session = session;
        _hostBridge = hostBridge;
        _cashShiftService = cashShiftService;
        _shiftStateService = shiftStateService;
        _prompts = prompts;
        _permissions = ResolveService<IPermissionService>();
        _customerDisplay = customerDisplay;
        _barcodeInputService = ResolveService<IBarcodeInputService>();
        _voiceControl = ResolveService<IVoiceControlService>();
        _voiceControl.CommandRecognized += OnVoiceCommandRecognized;
        _hostBridge.Window = this;
        WireDialogBridge();

        InitializeComponent();
        DataContext = _viewModel;
        CatalogGridSplitter.PointerReleased += OnCatalogGridSplitterPointerReleased;
        CatalogColumn.PropertyChanged += OnCatalogColumnPropertyChanged;
        CartColumn.PropertyChanged += OnCartColumnPropertyChanged;
        RestoreApplicationState();
        _viewModel.Catalog.StateChanged += OnViewModelStateChanged;
        _viewModel.Basket.StateChanged += OnViewModelStateChanged;
        _viewModel.Basket.ShiftDesyncDetected += OnShiftDesyncDetected;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        Screens.Changed += OnCashierScreensChanged;
        _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnel, а не обычный (Bubble) KeyDown="OnMainWindowKeyDown": пока фокус стоит на
        // каком-нибудь тулбарном Button (обычное состояние покоя), встроенная в Avalonia
        // директивная навигация по стрелкам перехватывает Up/Down/Left/Right раньше, чем
        // событие успевает добубниться до окна — Tunnel гарантированно срабатывает первым.
        AddHandler(KeyDownEvent, OnArrowKeyTunnel, RoutingStrategies.Tunnel);
        // 2026-09-16, тоже Tunnel и по той же причине: если фокус остался внутри списка
        // каталога (обычное дело после клика мышкой по плитке товара — фокус оседает на кнопке
        // плитки, потомке ListBox'а), у ListBox'а уже есть СВОЙ Bubble-обработчик Enter
        // (ProductsList_KeyDown — добавляет/повышает количество выделенного товара) и он
        // срабатывает раньше, чем событие добубнится до окна, помечая его Handled — до
        // OnMainWindowKeyDown оно уже не доходит. Из-за этого "Оплатить по Enter" не работал,
        // а вместо этого просто прибавлялось количество товара (репорт пользователя). Tunnel
        // гарантированно выполняется раньше любого Bubble-обработчика внутри окна.
        AddHandler(KeyDownEvent, OnEnterKeyPayTunnel, RoutingStrategies.Tunnel);
        // NOTE: the cart's +/- quantity buttons keeping keyboard focus after a click (so the
        // next scan's Enter re-fires them instead of completing) is now fixed narrowly at the
        // button level (Focusable="False" in BasketPanelView.axaml) instead of here. Two prior
        // attempts at a window-wide fix (tunneling all KeyDown; restoring focus after every
        // Button.Click) both broke the scanner more broadly than the bug they fixed — do not
        // reintroduce either without confirming real hardware still scans correctly afterward.

        ApplyFullscreenPreference();
        _viewModel.Toolbar.UpdateThemeGlyph(UserPreferences.Instance.DarkTheme);
    }

    /// <summary>False означает, что подписка NurCRM просрочена — вызывающий код обязан
    /// показать компании сообщение об оплате и НЕ показывать главное окно кассы.</summary>
    public async Task<bool> InitializeApplicationAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_appInitialized)
            return true;

        progress?.Report("Загрузка кассы...");

        if (AccountCatalogIsolation.RequireForcedCatalogSync)
            _viewModel.Catalog.StatusText = "Требуется синхронизация каталога для нового пользователя.";

        progress?.Report("Загрузка профиля...");
        // 2026-09-10: автономный (офлайн) режим — компании на сервере не существует вообще,
        // этот вызов раньше всё равно уходил в сеть при КАЖДОМ запуске (падал в catch ниже, не
        // ронял кассу, но тратил время на заведомо обречённый запрос и логировал лишнее
        // предупреждение). Тарифных ограничений (Старт/Стандарт) в автономном режиме тоже нет —
        // RefreshEntitlements не нужен, доступ и так полный (см. PermissionService).
        // 2026-09-12: IsCurrentSessionAutonomous, не OfflineModeHelper.UseLocalOperations — тот
        // же класс бага, что и с остатком кассы (см. ShiftStateService.RefreshAsync): обычный
        // аккаунт, просто начавший сессию без сети, не должен НАВСЕГДА пропускать эту попытку.
        if (!App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
        {
            try
            {
                var subscription = await CompanyInfoService.RefreshAsync(App.AuthApi, cancellationToken).ConfigureAwait(true);
                // Единственное место на пути автовхода, где компания уже известна: до сюда
                // касса ещё ничего не продала, поэтому разделить данные аккаунтов можно здесь.
                AccountDataIsolation.SwitchTo(CompanyInfoService.LastCompany?.Id);
                // Тариф компании (Старт/Стандарт) становится известен только сейчас — без этого
                // пункт «Клиенты» в боковом меню остаётся видимым до первого его открытия
                // (2026-09-07, см. комментарий у SideMenuViewModel.CanViewClients).
                _viewModel.SideMenu.RefreshEntitlements();
                if (subscription != null && !ProceedPastSubscriptionCheck(subscription))
                    return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Company profile unavailable during offline bootstrap: {ex}", "WARNING");
            }
        }

        progress?.Report("Обновление смены...");
        await RefreshShiftStateAsync(cancellationToken).ConfigureAwait(true);

        progress?.Report("Подготовка рабочего места...");
        await _viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);

        // 2026-09-12: НАЙДЕН реальный источник дублирующего текста под жёлтым баннером каталога
        // — эта строка безусловно перетирала аккуратный StatusText, который только что выставил
        // CatalogPanelViewModel.RefreshCatalogAsync (вызванный чуть выше, из _viewModel.
        // InitializeAsync), сырым OfflineBootstrapMessage. Для автономного режима это давало
        // повторяющий жёлтый баннер текст "Автономный режим — работа без интернета." — кассир
        // специально просил его убрать. Для обычного (временно недоступного NurCRM) офлайна
        // сообщение остаётся — там оно несёт полезную причину простоя, которую сам каталог не
        // знает.
        if (!string.IsNullOrWhiteSpace(_session.OfflineBootstrapMessage)
            && !App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
            _viewModel.Catalog.StatusText = _session.OfflineBootstrapMessage!;

        if (AccountCatalogIsolation.RequireForcedCatalogSync)
            AccountCatalogIsolation.ClearForcedCatalogSyncFlag();

        _appInitialized = true;
        return true;
    }

    /// <summary>После обновления кассы (текущая версия отличается от той, что кассир уже
    /// видел) один раз показывает список изменений. На самой первой установке ничего не
    /// показывает — просто запоминает версию.
    ///
    /// ВАЖНО: вызывать только когда окно уже видимо (например из OnLoaded) — ShowDialog с
    /// owner, который ещё не показан, бросает "Cannot show window with non-visible owner" и
    /// раньше валил весь InitializeApplicationAsync (окно кассы вызывало этот метод до Show()),
    /// из-за чего _appInitialized оставался false и дальнейшие окна (Клиенты, Оплата долга)
    /// оставались нерабочими. Обёрнуто в try/catch на случай новых похожих ошибок в будущем —
    /// показ списка изменений не должен ронять запуск кассы.</summary>
    private void ShowWhatsNewIfUpdated()
    {
        try
        {
            var currentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "";
            if (string.IsNullOrEmpty(currentVersion))
                return;

            var prefs = UserPreferences.Instance;
            var alreadySeen = prefs.LastSeenAppVersion;
            prefs.LastSeenAppVersion = currentVersion;
            prefs.SaveToDisk();

            if (string.IsNullOrEmpty(alreadySeen) || alreadySeen == currentVersion)
                return;

            PosMessageBox.Show(
                this,
                Tr.T(
                    $"Версия {currentVersion}. Что изменилось:\n\n{AppChangelog.LatestAsBulletedText()}",
                    $"Версия {currentVersion}. Эмне өзгөрдү:\n\n{AppChangelog.LatestAsBulletedText()}",
                    $"Version {currentVersion}. What's new:\n\n{AppChangelog.LatestAsBulletedText()}",
                    $"Sürüm {currentVersion}. Neler değişti:\n\n{AppChangelog.LatestAsBulletedText()}",
                    $"Versiya {currentVersion}. Nima o'zgardi:\n\n{AppChangelog.LatestAsBulletedText()}"),
                Tr.T("Касса обновлена", "Касса жаңырды", "POS updated", "Kasa güncellendi", "Kassa yangilandi"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShowWhatsNewIfUpdated failed (non-fatal): {ex}", "WARNING");
        }
    }

    /// <summary>Показывает предупреждение (близко к окончанию оплаты) или блокирующее
    /// сообщение (подписка уже просрочена). Возвращает false, если кассу открывать нельзя.</summary>
    private bool ProceedPastSubscriptionCheck(SubscriptionStatus subscription)
    {
        if (subscription.IsExpired)
        {
            PosLogger.Log(
                $"Subscription expired: end_date={subscription.EndDate:yyyy-MM-dd}, daysRemaining={subscription.DaysRemaining}",
                "WARNING");
            ShowSubscriptionAlertSafe(
                Tr.T("Подписка не оплачена", "Жазылуу төлөнгөн эмес", "Subscription not paid", "Abonelik ödenmedi", "Obuna to'lanmagan"),
                Tr.T(
                    $"Срок действия компании истёк ({subscription.EndDate:dd.MM.yyyy}). Пожалуйста, оплатите!",
                    $"Компаниянын мөөнөтү бүттү ({subscription.EndDate:dd.MM.yyyy}). Сураныч, төлөңүз!"),
                PosAlertKind.Error,
                buttonText: Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"));
            OpenPaymentSite();
            // Блокировать доступ обязаны независимо от того, удалось ли показать диалог —
            // раньше исключение из PosAlertDialog.Show (owner ещё не виден на этом этапе
            // запуска) уходило в общий catch выше по стеку и проверка тихо пропускалась,
            // касса открывалась как ни в чём не бывало на просроченном аккаунте.
            return false;
        }

        if (subscription.IsNearExpiry)
        {
            PosLogger.Log(
                $"Subscription near expiry: end_date={subscription.EndDate:yyyy-MM-dd}, daysRemaining={subscription.DaysRemaining}",
                "WARNING");
            ShowSubscriptionAlertSafe(
                Tr.T("Скоро истекает подписка", "Жазылуунун мөөнөтү жакында бүтөт", "Subscription expiring soon", "Abonelik yakında sona eriyor", "Obuna tez orada tugaydi"),
                Tr.T(
                    $"Подписка NurCRM истекает {subscription.EndDate:dd.MM.yyyy} (осталось {subscription.DaysRemaining} дн.).\n" +
                    "Пожалуйста, оплатите абонентскую плату заранее.",
                    $"NurCRM жазылуусунун мөөнөтү {subscription.EndDate:dd.MM.yyyy} бүтөт ({subscription.DaysRemaining} күн калды).\n" +
                    "Абоненттик төлөмдү мөөнөтүнөн мурда төлөңүз."),
                PosAlertKind.Warning,
                buttonText: Tr.T("Пропустить", "Өткөрүү", "Skip", "Atla", "O'tkazish"));
        }

        return true;
    }

    /// <summary>owner=null (не this): на этапе InitializeApplicationAsync, вызванном ДО
    /// mainWindow.Show() (из LoginWindow или автологина при старте), это окно ещё не видимо —
    /// ShowDialog(this) бросает "Cannot show window with non-visible owner". PosDialogHost
    /// с owner=null сам находит текущее видимое окно (splash/логин/касса) через
    /// desktop.MainWindow. Обёрнуто в try/catch, чтобы сбой показа диалога никогда не мешал
    /// вызывающему коду (в первую очередь — блокировке просроченного аккаунта).</summary>
    private static void ShowSubscriptionAlertSafe(string title, string message, PosAlertKind kind, string buttonText)
    {
        try
        {
            PosAlertDialog.Show(null, title, message, kind, buttonText);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Subscription alert dialog failed: {ex}", "WARNING");
        }
    }

    private static void OpenPaymentSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.nurcrm.kg") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Failed to open payment site: {ex}", "ERROR");
        }
    }

    /// <summary>Фоновый мониторинг срока подписки компании во время активной работы кассы —
    /// работает и офлайн (см. CompanyInfoService.GetCachedSubscriptionStatus), пока не истечёт
    /// _windowCts (окно закрывается/выходим на логин). Отдельно от разового показа в
    /// ProceedPastSubscriptionCheck: тот срабатывает только при входе, этот — постоянно.</summary>
    private async Task MonitorSubscriptionAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SubscriptionCheckInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
                break;

            SubscriptionStatus? status;
            try
            {
                status = CompanyInfoService.GetCachedSubscriptionStatus();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Subscription monitor check failed: {ex}", "WARNING");
                continue;
            }

            if (status is null)
                continue;

            // Живой пересчёт (выше) не ходит на сервер — если подписку продлили (оплатили),
            // локальный кэш об этом не узнает сам. Пока подписка близка к концу, периодически
            // спрашиваем сервер заново; в последний час — гораздо чаще, чтобы факт оплаты
            // подхватился быстро, а не оставался "залипшим" до следующего входа.
            if (!status.IsExpired && status.IsNearExpiry)
            {
                var syncNow = DateTimeOffset.Now;
                var syncInterval = status.EndDate - syncNow <= SubscriptionServerSyncUrgentThreshold
                    ? SubscriptionServerSyncIntervalUrgent
                    : SubscriptionServerSyncIntervalNormal;
                var dueForSync = _lastSubscriptionServerSyncAt is not { } lastSync || syncNow - lastSync >= syncInterval;
                if (dueForSync)
                {
                    _lastSubscriptionServerSyncAt = syncNow;
                    try
                    {
                        var refreshed = await CompanyInfoService.RefreshAsync(App.AuthApi, ct).ConfigureAwait(true);
                        if (refreshed != null)
                            status = refreshed;
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"Subscription server sync failed: {ex}", "WARNING");
                    }
                }
            }

            if (status.IsExpired)
            {
                if (_subscriptionExpiryHandled)
                    continue;
                _subscriptionExpiryHandled = true;

                await HandleSubscriptionExpiredDuringSessionAsync().ConfigureAwait(true);
                return;
            }

            if (!status.IsNearExpiry)
                continue;

            // Последние 3 часа до истечения — здесь уже держит внимание постоянный отсчёт
            // в статус-баре (MainStatusViewModel.SubscriptionCountdownText), периодическая
            // модалка поверх него была бы лишней.
            if (status.EndDate - DateTimeOffset.Now <= SubscriptionModalCutoff)
                continue;

            var now = DateTimeOffset.Now;
            if (_lastSubscriptionAlertAt is { } lastAlert && now - lastAlert < SubscriptionAlertInterval)
                continue;

            _lastSubscriptionAlertAt = now;
            try
            {
                await PosAlertDialog.ShowAsync(
                    this,
                    Tr.T("Скоро истекает подписка", "Жазылуунун мөөнөтү жакында бүтөт", "Subscription expiring soon", "Abonelik yakında sona eriyor", "Obuna tez orada tugaydi"),
                    Tr.T(
                        $"Подписка NurCRM истекает {status.EndDate:dd.MM.yyyy} (осталось {status.DaysRemaining} дн.).\n" +
                        "Пожалуйста, оплатите абонентскую плату.",
                        $"NurCRM жазылуусунун мөөнөтү {status.EndDate:dd.MM.yyyy} бүтөт ({status.DaysRemaining} күн калды).\n" +
                        "Абоненттик төлөмдү төлөңүз."),
                    PosAlertKind.Warning,
                    buttonText: Tr.T("Пропустить", "Өткөрүү", "Skip", "Atla", "O'tkazish")).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Subscription reminder dialog failed: {ex}", "WARNING");
            }
        }
    }

    /// <summary>Подписка истекла прямо во время работы кассы (не только при входе) — по
    /// требованию: автоматически сохранить текущий чек (без подтверждений) и сразу выйти
    /// на экран авторизации, где ProceedPastSubscriptionCheck при следующей попытке входа
    /// покажет блокирующее сообщение с кнопкой «Оплатить».</summary>
    private async Task HandleSubscriptionExpiredDuringSessionAsync()
    {
        PosLogger.Log("Subscription expired during active session — auto-saving cart and forcing logout.", "WARNING");
        try
        {
            if (_viewModel.Basket.HasItems)
            {
                try
                {
                    await _viewModel.Basket.DeferCartAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Auto-defer cart on subscription expiry failed: {ex}", "ERROR");
                }
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Forced logout on subscription expiry failed: {ex}", "ERROR");
        }
    }

    /// <summary>2026-09-09: 60-часовой потолок офлайн-работы для обычного (не автономного,
    /// NurCRM) режима — та же схема, что MonitorSubscriptionAsync/HandleSubscriptionExpiredDuring
    /// SessionAsync выше: пока идёт офлайн-работа, OnlineContactTracker.LastSuccessUtc не
    /// обновляется (нет ни одного успешного ответа сервера — см. NurMarketApiClient.SendOnceAsync);
    /// если пауза превысила лимит — принудительный выход на экран входа, где следующая попытка
    /// офлайн-входа получит отказ от OnlineOfflineAuthenticationService.OfflineOrExpiredAsync.
    /// TODO: когда появится автономный (ключ-активированный) режим — этот монитор должен
    /// пропускаться, пока активна именно автономная сессия (там офлайн без лимита — в этом весь
    /// смысл автономного режима).</summary>
    private async Task MonitorOfflineGraceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(OfflineGraceCheckInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
                break;

            var lastSuccess = OnlineContactTracker.LastSuccessUtc;
            if (lastSuccess is null)
                continue;

            if (DateTime.UtcNow - lastSuccess.Value <= OfflineAuthSessionStore.MaxOfflineDuration)
                continue;

            if (_offlineGraceExceededHandled)
                continue;
            _offlineGraceExceededHandled = true;

            await HandleOfflineGraceExceededDuringSessionAsync().ConfigureAwait(true);
            return;
        }
    }

    private async Task HandleOfflineGraceExceededDuringSessionAsync()
    {
        PosLogger.Log("Offline grace period (60h) exceeded during active session — auto-saving cart and forcing logout.", "WARNING");
        try
        {
            if (_viewModel.Basket.HasItems)
            {
                try
                {
                    await _viewModel.Basket.DeferCartAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Auto-defer cart on offline-grace exceed failed: {ex}", "ERROR");
                }
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Forced logout on offline-grace exceed failed: {ex}", "ERROR");
        }
    }

    // ----------------------------------------------------------------
    //  Обработчики событий шапки и панели чека (MainWindow.axaml)
    // ----------------------------------------------------------------

    /// <summary>Переключение темы Light/Dark (иконка луны/солнца в шапке).</summary>
    private void ToggleTheme_Click(object? sender, RoutedEventArgs e) => ToggleTheme();

    /// <summary>Открытие смены (кнопка «🔓 Открыть смену» в шапке).</summary>
    private async void OpenShift_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OpenShiftAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Open shift canceled.", "DEBUG");
        }
    }

    /// <summary>Закрытие смены (кнопка «🔒 Закрыть смену» в шапке).</summary>
    private async void CloseShift_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CloseShiftAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Close shift canceled.", "DEBUG");
        }
    }

    internal void ToggleTheme()
    {
        var app = Application.Current;
        if (app is null)
            return;

        // Текущее состояние берём из сохранённой настройки, а НЕ из app.ActualThemeVariant.
        // 2026-09-22, живой баг («при смене тёмного режима каталог и корзина остаются
        // тёмными»): ActualThemeVariant у Application расходится с реально применённой темой —
        // второе нажатие подряд вычисляло то же самое значение, что и первое, поэтому
        // RequestedThemeVariant переставлялся, а UserPreferences.DarkTheme не менялся. Дальше
        // часть кистей бралась из словаря нового варианта, а всё, что зависит от
        // prefs.DarkTheme (обои, тонировка, экран покупателя), оставалось от старого — экран
        // получался наполовину светлым, наполовину тёмным. UserPreferences.DarkTheme — то же
        // значение, по которому тема восстанавливается при запуске (App.axaml.cs), поэтому
        // переключатель и старт теперь не могут разойтись.
        var prefs = UserPreferences.Instance;
        var newIsDark = !prefs.DarkTheme;

        // 2026-09-21, живой баг («при переходе на светлую тему интерфейс ломается»): здесь
        // менялся ТОЛЬКО RequestedThemeVariant. Но акцентная тема (AccentThemeService) хранит
        // свои 22 кисти прямо в Application.Resources, а они перекрывают словарь темы — и после
        // переключения там оставались цвета, посчитанные для ПРЕДЫДУЩЕГО варианта: панели и
        // плитки оставались тёмными, текст становился тёмным по тёмному, цены — синими из
        // тёмной палитры. На «золотой» теме баг не проявлялся, потому что она снимает все
        // перекрытия и портиться нечему. App.ApplyTheme делает это правильно — переиспользуем
        // его вместо урезанной копии (он же синхронизирует экран покупателя).
        // Настройка меняется ДО применения: App.ApplyTheme по пути обновляет экран покупателя,
        // а тот берёт режим из prefs.DarkTheme — при обратном порядке он получал старое значение.
        prefs.DarkTheme = newIsDark;
        App.ApplyTheme(newIsDark);
        prefs.SaveToDisk();
        _viewModel.Toolbar.UpdateThemeGlyph(newIsDark);
        RefreshBackgroundWallpaper();
    }

    /// <summary>2026-09-15, по явной просьбе пользователя ("убери его, верни виртуальную
    /// клавиатуру Windows!") — свою FrmKeyboard пробовали как раз-таки замену системной osk.exe
    /// (см. историю правок этого метода), но пользователю она не подошла, нужна именно
    /// привычная клавиатура Windows. Возвращена системная osk.exe без собственной FrmKeyboard.</summary>
    internal void ToggleKeyboard() =>
        App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();

    /// <summary>Этап 7 бэклога "Доработки" ("Упрощение"): при неудачной проверке экрана
    /// покупателя — короткое сообщение с двумя кнопками ("Открыть настройки"/"Понятно") вместо
    /// обычного предупреждения, сразу ведущее в Настройки → Монитор, где это чаще всего и
    /// настраивается/чинится.</summary>
    internal void CheckCustomerDisplay()
    {
        var result = _customerDisplay.TestSecondaryScreen(this);
        if (result.IsSuccess)
        {
            _prompts.ShowToast(result.Message);
        }
        else
        {
            var openSettings = PosConfirmDialog.Show(
                this,
                Tr.T("Экран покупателя", "Сатып алуучунун экраны", "Customer display", "Müşteri ekranı", "Xaridor ekrani"),
                result.Message,
                confirmText: Tr.T("Открыть настройки", "Жөндөөлөрдү ачуу", "Open settings", "Ayarları aç", "Sozlamalarni ochish"),
                cancelText: Tr.T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli"));

            if (openSettings && Authorize(PosPermissions.ViewSettings))
            {
                var settingsWindow = App.GetRequiredService<PosSettingsWindow>();
                settingsWindow.NavigateToMonitor();
                settingsWindow.Show(this);
            }
        }

        Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
    }

    private async void OpenHotkeySettings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new HotkeySettingsWindow(_hotkeys);
        await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        RestoreScannerFocus();
    }

    private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_hotkeys.TryMatch(e, out var action))
        {
            e.Handled = true;
            ExecuteHotkey(action);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && TryGetHotkeyGroup(e.Key, out var group))
        {
            e.Handled = true;
            _viewModel.Catalog.ToggleHotkeyGroupFilter(group);
            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        _barcodeInputService.ProcessKeyDown(e);
    }

    /// <summary>"Оплатить по Enter" (по просьбе пользователя, 2026-09-16) — вынесено в Tunnel,
    /// чтобы гарантированно сработать раньше локальных Bubble-обработчиков Enter внутри окна
    /// (например, у списка каталога — см. комментарий у регистрации хендлера). Не перехватывает,
    /// когда фокус в текстовом поле — там Enter может быть нужен для чего-то своего.</summary>
    private void OnEnterKeyPayTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        // 2026-09-16, срочный живой баг ("при сканировании сразу идёт на оплатить"): сканер
        // штрихкода эмулирует быстрый набор с клавиатуры и завершает каждый скан символом
        // Enter — если в корзине уже есть хотя бы один товар (после первого скана), этот Enter
        // раньше перехватывался здесь и нажимал "Оплатить" вместо завершения добавления
        // следующего отсканированного товара. HasBufferedInput истинно, пока в буфере сканера
        // ещё копятся быстро введённые символы — значит текущий Enter завершает скан, а не
        // осознанное нажатие кассира, и его нужно пропустить сюда дальше, к самому сканеру.
        if (_barcodeInputService.HasBufferedInput)
            return;
        if (!_viewModel.Basket.HasItems || _viewModel.Basket.IsBusy)
            return;
        if (!_viewModel.Basket.PayCommand.CanExecute(null))
            return;

        e.Handled = true;
        _viewModel.Basket.PayCommand.Execute(null);
    }

    /// <summary>Пока фокус нигде в каталоге не стоял (стоит на тулбарном Button/окне — обычное
    /// состояние покоя для сканера штрихкодов), первая стрелка вместо встроенной директивной
    /// Tab-навигации Avalonia "входит" в каталог: ставит курсор на первый товар и передаёт
    /// фокус ListBox'у, дальше стрелками уже управляет сам ListBox/WrapPanel. Если фокус уже
    /// где-то внутри каталога (после клика по плитке реальный фокус часто оседает на кнопке
    /// самой плитки, а не на ListBox) — не перехватываем, иначе вторая и все следующие стрелки
    /// не доходили бы до штатной навигации ListBox'а.</summary>
    private void OnArrowKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right))
            return;

        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is TextBox || IsWithinCatalogPanel(focused))
            return;

        if (CatalogPanel.TryEnterCatalogNavigation())
            e.Handled = true;
    }

    private bool IsWithinCatalogPanel(Control? control)
    {
        for (var c = control; c is not null; c = c.Parent as Control)
        {
            if (ReferenceEquals(c, CatalogPanel))
                return true;
        }
        return false;
    }

    /// <summary>F1-F12 без модификаторов — группы быстрых товаров (как в веб-версии NurCRM),
    /// не конфликтуют с действиями кассы: те переведены на Ctrl+... в PosHotkeyService.</summary>
    private static bool TryGetHotkeyGroup(Key key, out string group)
    {
        if (key is >= Key.F1 and <= Key.F12)
        {
            group = "F" + (key - Key.F1 + 1);
            return true;
        }

        group = "";
        return false;
    }

    private void ExecuteHotkey(PosHotkeyAction action)
    {
        switch (action)
        {
            case PosHotkeyAction.Checkout:
                ExecuteCommand(_viewModel.Basket.PayCommand);
                break;
            case PosHotkeyAction.ClearCart:
                ExecuteCommand(_viewModel.Basket.ClearCartCommand);
                break;
            case PosHotkeyAction.ToggleCustomerDisplay:
                ToggleCustomerDisplay();
                break;
            case PosHotkeyAction.ApplyDiscount:
                ExecuteCommand(_viewModel.Basket.ApplyOrderDiscountCommand);
                break;
            case PosHotkeyAction.FocusProductSearch:
                CatalogPanel.FocusProductSearch();
                break;
        }
    }

    private void ToggleCustomerDisplay()
    {
        if (_customerDisplay.IsOpen)
        {
            _ = _customerDisplay.CloseAsync(true);
            _prompts.ShowToast("Экран покупателя скрыт.");
            RestoreScannerFocus();
            return;
        }

        var result = _customerDisplay.TestSecondaryScreen(this);
        if (result.IsSuccess)
            _prompts.ShowToast(result.Message);
        else
            _prompts.ShowWarning(result.Message);
        RestoreScannerFocus();
    }

    private static void ExecuteCommand(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Never move focus while an interactive control is processing a press.
        // Doing it between PointerPressed and PointerReleased cancels Click on
        // touchscreens and on some mouse drivers.
        if (IsInteractivePointerTarget(e.Source))
            return;

        Dispatcher.UIThread.Post(RestoreScannerFocus, DispatcherPriority.Background);
    }

    private static bool IsInteractivePointerTarget(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is Button or TextBox or ComboBox or ListBox or DataGrid or
                Slider or ScrollBar or ScrollViewer or GridSplitter or NumericUpDown or
                DatePicker or Calendar or MenuItem or TabItem or ToggleSwitch or
                CheckBox or RadioButton)
                return true;
        }
        return false;
    }

    private void RestoreScannerFocus()
    {
        FocusManager?.ClearFocus();
        Focus();
    }

    private void OnBarcodeScanned(string barcode)
    {
        // IBarcodeInputService is a single shared instance: any window that reads a keystroke
        // (Warehouse, Return, etc.) raises this same event, and MainWindow stays subscribed for
        // its whole lifetime. Without this guard, scanning while another window has focus also
        // added the item to the cash register cart in the background.
        if (!IsActive)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _viewModel.Basket.BarcodeInput = barcode;
            ExecuteCommand(_viewModel.Basket.AddByBarcodeCommand);
        });
    }

    /// <summary>Срабатывает из фонового аудио-потока NAudio (не UI-поток) — тот же приём,
    /// что и OnBarcodeScanned: единственный общий на всё приложение слушатель, поэтому
    /// добавляем в корзину только если сейчас активно именно окно кассы.</summary>
    /// <summary>Диспетчер намерений голосовых команд (VoiceIntent) — опасные операции (удаление
    /// позиции, очистка чека) НЕ подтверждаются отдельным голосовым "да"/"нет": вместо этого
    /// команда просто дёргает те же ICommand, что и обычный клик мышью, а значит проходит через
    /// уже существующие в кассе защитные механизмы (ClearCartCommand — диалог Да/Нет,
    /// RemoveLineCommand — пароль кассира) без необходимости дублировать их голосом.</summary>
    private void OnVoiceCommandRecognized(VoiceCommandResult result)
    {
        if (!IsActive)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // Голосовой замок (2026-09-05) — команда произнесена не тем голосом, что был
            // зарегистрирован: ничего не выполняем, только предупреждаем. Это мягкая проверка
            // (по решению пользователя), а не пароль — простуда/шум могут ошибочно не совпасть,
            // поэтому нельзя просто молчать, кассир должен понимать, ПОЧЕМУ команда не сработала.
            if (result.VoiceMatched == false)
            {
                // Порядок важен: ShowToast/PosMessageBox — БЛОКИРУЮЩИЙ модальный диалог (см.
                // AvaloniaUserPrompts.ShowToast → PosMessageBox.Show, синхронный вызов, если мы
                // уже в UI-потоке — а OnVoiceCommandRecognized именно там и работает). Голос,
                // вызванный ПОСЛЕ ShowToast, реально звучал только после того, как кассир уже
                // закрыл диалог (2026-09-05, подтверждено пользователем) — подсказка должна
                // запускаться ДО открытия диалога, чтобы звучать, пока он на экране.
                VoicePromptPlayer.PlayVoiceMismatch();
                _prompts.ShowToast(
                    Tr.T("Голосовой замок: команда не выполнена — голос не совпадает с зарегистрированным.",
                        "Үндүк кулпу: буйрук аткарылган жок — үн катталган үн менен дал келбейт."),
                    isWarning: true);
                return;
            }

            var basket = _viewModel.Basket;
            switch (result.Intent)
            {
                case VoiceIntent.AddProduct:
                    if (result.Product != null)
                    {
                        // Раньше здесь всегда был basket.AddProductByVoice напрямую — товар с
                        // поштучной ценой (HasPieceOption) добавлялся по цене целой пачки, минуя
                        // PackageChoiceDialog и его звуковую подсказку. AddProductByVoiceAsync
                        // (MainWindow.Dialogs.cs) сама решает, нужен ли диалог выбора.
                        _ = AddProductByVoiceAsync(result.Product, result.Quantity, result.UnitKind);
                    }
                    else if (result.Candidates.Count > 1)
                    {
                        var names = string.Join(", ", result.Candidates.Take(5).Select(p => p.Title));
                        VoicePromptPlayer.PlayClarifyProduct();
                        _prompts.ShowToast(
                            Tr.T($"Голос: уточните товар — «{result.ProductQuery}» подходит нескольким: {names}.",
                                $"Үн: товарды тактаңыз — «{result.ProductQuery}» бир нече товарга дал келет: {names}."),
                            isWarning: true);
                    }
                    else if (string.IsNullOrWhiteSpace(result.ProductQuery) && result.UnitKind != VoiceUnitKind.None)
                    {
                        // "касса пачка"/"касса поштучно 3" — товар не назван вообще, значит это не
                        // "не найдено", а вероятный голосовой ответ на уже открытый
                        // PackageChoiceDialog (он сам подписан на CommandRecognized и уже обработал
                        // эту же команду, если диалог действительно открыт). Если диалога сейчас
                        // нет — команда просто без адресата, ругать "не найдено" тут неверно,
                        // товар для поиска и не назывался.
                    }
                    else
                    {
                        VoicePromptPlayer.PlayProductNotFound();
                        var phrase = string.IsNullOrWhiteSpace(result.ProductQuery) ? result.RawText : result.ProductQuery;
                        if (!OfferTeachVoicePhrase(phrase))
                        {
                            // Учить нечему (пустая фраза) — тогда хотя бы обычное предупреждение.
                            _prompts.ShowToast(
                                Tr.T($"Голос: товар не найден («{result.RawText}»).", $"Үн: товар табылган жок («{result.RawText}»).",
                                    $"Voice: product not found (“{result.RawText}”).", $"Sesli komut: ürün bulunamadı (“{result.RawText}”).",
                                    $"Ovoz: mahsulot topilmadi (“{result.RawText}”)."),
                                isWarning: true);
                        }
                    }
                    break;

                case VoiceIntent.FindProduct:
                    if (result.Candidates.Count == 0)
                    {
                        VoicePromptPlayer.PlayProductNotFound();
                        if (!OfferTeachVoicePhrase(result.ProductQuery))
                        {
                            _prompts.ShowToast(Tr.T($"Голос: ничего не найдено по «{result.ProductQuery}».", $"Үн: «{result.ProductQuery}» боюнча эч нерсе табылган жок.",
                                $"Voice: nothing found for “{result.ProductQuery}”.", $"Sesli komut: “{result.ProductQuery}” için bir şey bulunamadı.",
                                $"Ovoz: “{result.ProductQuery}” bo'yicha hech narsa topilmadi."), isWarning: true);
                        }
                    }
                    else
                        _prompts.ShowToast(Tr.T($"Найдено: {string.Join(", ", result.Candidates.Take(5).Select(p => p.Title))}.", $"Табылды: {string.Join(", ", result.Candidates.Take(5).Select(p => p.Title))}."));
                    break;

                case VoiceIntent.RemoveLastItem:
                {
                    var lastLine = basket.Lines.LastOrDefault();
                    if (lastLine is null)
                    {
                        VoicePromptPlayer.PlayCartEmpty();
                        _prompts.ShowToast(Tr.T("Голос: чек пуст, удалять нечего.", "Үн: чек бош, өчүрүүчү нерсе жок.", "Voice: the receipt is empty, nothing to remove.", "Sesli komut: fiş boş, silinecek bir şey yok.", "Ovoz: chek bo'sh, o'chirish uchun hech narsa yo'q."), isWarning: true);
                        break;
                    }
                    if (basket.RemoveLineCommand.CanExecute(lastLine))
                        basket.RemoveLineCommand.Execute(lastLine);
                    break;
                }

                case VoiceIntent.ClearCart:
                    ExecuteCommand(basket.ClearCartCommand);
                    break;

                case VoiceIntent.Pay:
                    if (!basket.HasItems)
                    {
                        VoicePromptPlayer.PlayCartEmpty();
                        _prompts.ShowToast(Tr.T("Голос: чек пуст, оплачивать нечего.", "Үн: чек бош, төлөөчү нерсе жок.", "Voice: the receipt is empty, nothing to pay.", "Sesli komut: fiş boş, ödenecek bir şey yok.", "Ovoz: chek bo'sh, to'lov uchun hech narsa yo'q."), isWarning: true);
                    }
                    else
                        ExecuteCommand(basket.PayCommand);
                    break;

                case VoiceIntent.Cancel:
                case VoiceIntent.Unknown:
                default:
                    // RepeatLast уже разрешён в VoiceControlService в готовый AddProduct-результат
                    // (или в Unknown, если повторять нечего) — здесь отдельной ветки не нужно.
                    break;
            }
        });
    }

    /// <summary>2026-09-09: "товар не найден" голосом — вместо того чтобы кассир шёл в Настройки
    /// → Регистрация голоса и печатал фразу заново, сразу предлагаем привязать её к товару здесь
    /// же (TeachVoicePhraseDialog → VoiceLexiconStore.AddProductAlias) — САМА этот диалог уже
    /// объясняет, что товар не найден, поэтому отдельное предупреждение перед ним не нужно (было
    /// два диалога подряд, кассир видел только первый и не понимал, куда делась кнопка привязки).
    /// Пустая фраза (например, сказали только "касса" без ничего) — предлагать нечего, диалог не
    /// открываем, вызывающий код должен сам показать обычное предупреждение. Возвращает true, если
    /// диалог открылся.</summary>
    private bool OfferTeachVoicePhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        PosDialogHost.Show(new TeachVoicePhraseDialog(phrase.Trim(), CatalogCacheService.Products), this);
        return true;
    }

    /// <summary>Ставит окно на основной монитор и применяет реальный режим полного экрана.
    /// РАНЬШЕ последняя строка жёстко ставила WindowState.Maximized — это откатывало
    /// WindowState.FullScreen, выставленный конструктором через ApplyFullscreenPreference
    /// (эта функция вызывается заново из OnLoaded, ПОСЛЕ конструктора), поэтому кассир видел
    /// обычное развёрнутое окно с рамкой и панелью задач Windows, даже когда включён
    /// "Настоящий полноэкранный режим". FullscreenHelper.Apply — единственный источник истины
    /// для перевода Fullscreen/TrueFullscreen в реальное состояние окна, здесь и в конструкторе
    /// он должен давать один и тот же результат.</summary>
    internal void PlaceOnPrimaryScreen()
    {
        var primary = Screens?.Primary;
        if (primary is null)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Position = primary.WorkingArea.TopLeft;
        FullscreenHelper.Apply(this);
    }

    /// <summary>
    /// Остаток кассы с учётом локальных внесений/изъятий текущей смены,
    /// которых нет в балансе с сервера/офлайн-состояния.
    /// </summary>
    private decimal? EffectiveShiftCashBalance
    {
        get
        {
            if (!_session.IsShiftOpen)
                return _shiftCashBalance;

            var net = ShiftCashOperationsStore.NetForShift(_session.ActiveShiftId);
            if (_shiftCashBalance is null && net == 0m)
                return null;

            return (_shiftCashBalance ?? 0m) + net;
        }
    }

    internal Task OpenShiftAsync()
    {
        if (_session.IsShiftOpen)
        {
            // Вторая смена поверх незакрытой осиротила бы продажи прежней смены.
            _prompts.ShowWarning("Смена уже открыта. Закройте текущую смену перед открытием новой.");
            return Task.CompletedTask;
        }

        var dlg = App.GetRequiredService<OpenShiftDialog>();
        dlg.SuggestedBalance = EffectiveShiftCashBalance;
        if (PosDialogHost.Show(dlg, this) != true)
            return Task.CompletedTask;

        return ApplyShiftOpenedAsync(dlg.OpeningCash);
    }

    internal Task CloseShiftAsync() => CloseShiftAsync(confirmUnfinishedReceipt: true);

    private async Task CloseShiftAsync(bool confirmUnfinishedReceipt)
    {
        if (!_session.IsShiftOpen)
            return;

        if (confirmUnfinishedReceipt && !await ConfirmDiscardUnfinishedReceiptAsync().ConfigureAwait(true))
            return;

        var dlg = App.GetRequiredService<CloseShiftDialog>();
        // 2026-09-12, реальный баг с живого теста: "Остаток по системе" в Z-отчёте показывал
        // 0.00, хотя за смену прошло множество продаж — _shiftCashBalance обновлялся только при
        // СТАРТЕ кассы, а не перед закрытием, так что продажи за время работы в него не попадали.
        // Первая попытка (await RefreshShiftStateAsync ПЕРЕД показом диалога) сама стала новой
        // жалобой — "очень долгая реакция на нажатие" — клик на кнопку блокировался сетевым
        // запросом. Правильно: показать диалог СРАЗУ с уже известным балансом, а свежий
        // подтянуть в фоне и обновить прямо в открытом диалоге, когда он придёт (см.
        // CloseShiftDialog.UpdateSystemBalance) — RefreshShiftStateAsync уже защищён
        // 6-секундным таймаутом, так что фон в худшем случае просто ничего не успеет.
        dlg.SuggestedBalance = EffectiveShiftCashBalance;
        dlg.Totals = _shiftTotals;
        _ = RefreshBalanceInBackgroundAsync(dlg);
        if (await PosDialogHost.ShowAsync(dlg, this).ConfigureAwait(true) != true)
            return;

        await ApplyShiftClosedAsync(dlg.ClosingCash).ConfigureAwait(true);
    }

    /// <summary>Фоновая часть исправления из CloseShiftAsync — см. её комментарий.</summary>
    private async Task RefreshBalanceInBackgroundAsync(CloseShiftDialog dlg)
    {
        try
        {
            // 2026-09-12: живой случай — этот запрос молча не укладывался в общий 6-секундный
            // таймаут (тот же "протухший" эффект, что чинили для входа), из-за чего разбивка в
            // закрытии смены никогда не успевала дойти. Здесь это уже фон — диалог показан и
            // кассир может печатать сумму, ничего не блокируется, поэтому можно подождать дольше.
            await RefreshShiftStateAsync(CancellationToken.None, balanceTimeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            dlg.UpdateSystemBalance(EffectiveShiftCashBalance ?? 0m);
            dlg.UpdateTotals(_shiftTotals);
        }
        catch (Exception ex)
        {
            // Диалог и так уже показан с прежним (возможно чуть устаревшим) балансом —
            // фоновое обновление просто не удалось, это не повод что-либо ломать кассиру.
            PosLogger.Log($"Background shift balance refresh (close dialog) failed: {ex.Message}", "SHIFT");
        }
    }

    /// <summary>
    /// Закрытие смены очищает корзину и вкладки чеков (ClearAfterShiftClose),
    /// поэтому набранный, но не проведённый чек нужно подтвердить к потере.
    /// </summary>
    private async Task<bool> ConfirmDiscardUnfinishedReceiptAsync()
    {
        var cart = ResolveCartService();
        if (!cart.HasCart || cart.LineCount <= 0)
            return true;

        return await _prompts
            .ConfirmAsync("В текущем чеке есть незавершённые позиции. Закрыть смену и потерять чек?")
            .ConfigureAwait(true);
    }

    internal void NavigateWarehouse()
    {
        if (Authorize(PosPermissions.ViewProcurement))
            ShowModuleWindow<WarehouseWindow>();
    }

    /// <summary>Открывает "Табель сотрудников" напрямую из главного меню (2026-09-05: раньше
    /// добраться можно было только через Маркетплейс → Доп. функции каждый раз заново, хотя
    /// доп. услуга уже куплена и активна) — пункт меню видим только когда
    /// UserPreferences.StaffTimesheetUnlocked (см. SideMenuViewModel.CanViewStaffTimesheet).</summary>
    internal void NavigateStaffTimesheet()
    {
        _viewModel.CloseSideMenu();
        StaffTimesheetWindow.Open(this);
    }

    /// <summary>Маркетплейс раньше был виден только внутри Настроек — сначала вынесен
    /// отдельным пунктом в главное меню (2026-09-05), а затем и вовсе убран из Настроек
    /// (в тот же день, по повторной просьбе пользователя) и стал отдельным окном
    /// (см. MarketplaceWindow) — этот пункт меню теперь единственный путь туда.</summary>
    internal void NavigateMarketplace()
    {
        if (!Authorize(PosPermissions.ViewSettings))
            return;
        _viewModel.CloseSideMenu();
        MarketplaceWindow.Open(this);
    }

    internal void NavigateReturn()
    {
        if (!Authorize(PosPermissions.EmployeeReturn))
            return;
        _viewModel.CloseSideMenu();
        var dlg = App.GetRequiredService<ReturnSaleDialog>();
        PosDialogHost.Show(dlg, this);
    }

    internal void NavigateFinance() => ShowModuleWindow<FinanceWindow>();

    internal void NavigateSales()
    {
        if (Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<SalesWindow>();
    }

    internal void NavigateAbc()
    {
        if (Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<AbcAnalysisWindow>();
    }

    internal void NavigateClients()
    {
        // Тариф «Старт» (2026-09-07): раздел «Клиенты» на сайте для магазина скрыт — см. TariffGate.
        // Пункт меню тоже спрятан (SideMenuViewModel.CanViewClients), это вторая линия защиты.
        if (TariffGate.IsStartTariff)
        {
            _prompts.ShowToast(TariffGate.ClientsLockedMessage, isWarning: true);
            return;
        }

        if (Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<ClientsWindow>();
    }

    internal void NavigatePayDebt()
    {
        if (Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<PayDebtDialog>();
    }

    internal void NavigateSettings()
    {
        if (Authorize(PosPermissions.ViewSettings))
            ShowModuleWindow<PosSettingsWindow>();
    }

    /// <summary>Клик по значку "доступно обновление" в шапке (2026-09-06) — открывает Настройки
    /// сразу на вкладке "Обновления", где уже есть кнопки проверки/скачивания через
    /// IAppUpdateService, вместо того чтобы открывать ссылку на сборку с GitHub прямо в
    /// системном браузере в обход этого экрана (см. MainToolbarViewModel.SetUpdateAvailable).</summary>
    internal void NavigateSettingsUpdates()
    {
        if (!Authorize(PosPermissions.ViewSettings))
            return;
        _viewModel.CloseSideMenu();
        var window = App.GetRequiredService<PosSettingsWindow>();
        window.SelectUpdatesTab();
        window.Show(this);
    }

    internal void NavigateCrm() => ShowModuleWindow<CrmWebViewWindow>();

    internal void NavigateDeferredReceipts() => ShowModuleWindow<IrregularReceiptsWindow>();

    internal void NavigateErrorLogs() => ShowModuleWindow<LogsAndErrorsWindow>();

    internal void NavigateRemoteSupport() => ShowModuleWindow<RemoteSupportWindow>();

    internal void NavigateRestock() => ShowModuleWindow<RestockSuggestionsWindow>();

    internal void NavigateKnowledgeBase() => ShowModuleWindow<KnowledgeBaseWindow>();

    private bool Authorize(string permission)
    {
        if (_permissions.HasPermission(permission))
            return true;
        PosLogger.Log($"Permission denied: {permission}", "WARNING");
        _prompts.ShowWarning("Недостаточно прав для выполнения этой операции.");
        return false;
    }

    /// <summary>Closes the app outright — no login-screen redirect. Reuses the same
    /// flag-based bypass the login window's own exit button relies on.</summary>
    internal void ExitApplication()
    {
        App.ExitWithoutLoginRedirect = true;
        Close();
    }

    internal async Task LogoutAsync()
    {
        try
        {
            if (_session.IsShiftOpen)
            {
                var shiftResult = ShiftNotClosedDialog.Prompt(this);
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.Cancel)
                    return;

                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.CloseShift)
                {
                    if (!await ConfirmDiscardUnfinishedReceiptAsync().ConfigureAwait(true))
                        return;

                    await CloseShiftAsync(confirmUnfinishedReceipt: false).ConfigureAwait(true);
                }
                // LogoutOnly: смена намеренно остаётся открытой — например, кассир передаёт
                // кассу другому сотруднику посреди смены, не проводя закрытие/пересчёт кассы.
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Logout canceled.", "DEBUG");
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PlaceOnPrimaryScreen();
        if (!_appInitialized)
            await InitializeApplicationAsync(null, _windowCts.Token).ConfigureAwait(true);

        // Здесь (а не в InitializeApplicationAsync) — окно уже гарантированно видимо и может
        // быть owner'ом диалога, независимо от того, был ли _appInitialized уже true к этому
        // моменту (LoginWindow может вызвать InitializeApplicationAsync до Show() этого окна).
        // Телеграм-бот владельца отвечает на команды, только пока касса включена: своего
        // сервера у программы нет, новые сообщения она спрашивает сама (getUpdates).
        StartTelegramBot();

        if (!_whatsNewShown)
        {
            _whatsNewShown = true;
            ShowWhatsNewIfUpdated();
        }

        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var result = _customerDisplay.OpenForSession(this);
            if (result.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
            else
                PosLogger.Log(result.Message, "CUSTOMER_DISPLAY");
        }

        if (!_subscriptionMonitorStarted)
        {
            _subscriptionMonitorStarted = true;
            // Стартовое предупреждение (если оно уже было показано в ProceedPastSubscriptionCheck
            // при входе) не должно сразу же повториться фоновым монитором — отсчёт 30 минут
            // начинается с этого момента.
            _lastSubscriptionAlertAt = DateTimeOffset.Now;
            _ = MonitorSubscriptionAsync(_windowCts.Token);
        }

        if (!_offlineGraceMonitorStarted)
        {
            _offlineGraceMonitorStarted = true;
            _ = MonitorOfflineGraceAsync(_windowCts.Token);
        }

        // Каталог к этому моменту уже загружен (InitializeApplicationAsync выше) — словарь
        // распознавания голоса строится из реальных названий товаров, а не пустого списка.
        // Start() сам ничего не делает, если UserPreferences.VoiceControlEnabled == false.
        _voiceControl.Start();

        RefreshBackgroundWallpaper();
        RefreshUiScale();
        RefreshLayoutMode();
    }

    /// <summary>Раскладка главного экрана кассы (Настройки → Экран → "Макет", см.
    /// UserPreferences.MainLayoutMode) — "standard" (обычная, каталог+корзина) или "onec"
    /// (альтернативная, в стиле 1С "Рабочее место кассира"). Шапка и боковое меню общие для
    /// обеих раскладок и не участвуют в переключении. Вызывается при загрузке окна и заново из
    /// ScreenSettingsView сразу при выборе макета — см. App.ApplyMainLayoutMode.</summary>
    internal void RefreshLayoutMode()
    {
        var isOneC = string.Equals(UserPreferences.Instance.MainLayoutMode, "onec", StringComparison.OrdinalIgnoreCase);
        MainContentGrid.IsVisible = !isOneC;
        OneCLayout.IsVisible = isOneC;
    }

    /// <summary>Масштаб интерфейса кассы (Настройки → Экран → "Масштаб", 50–200%, см.
    /// UserPreferences.UiScalePercent) — через LayoutTransformControl.LayoutTransform, а НЕ
    /// голый RenderTransform на самом Grid: LayoutTransformControl пересчитывает и раскладку,
    /// и hit-testing под новым масштабом, поэтому клики попадают туда, куда реально указывает
    /// курсор на увеличенном/уменьшённом интерфейсе. Вызывается при загрузке окна и заново из
    /// PosSettingsWindow/ScreenSettingsView сразу при перетаскивании ползунка и после
    /// "Сохранить" — см. MainWindowHostBridge.Window.</summary>
    internal void RefreshUiScale() => UiScaleHelper.Apply(UiScaleTransform, 1280, 840);

    /// <summary>Обои экрана кассира (Настройки → Кастомизация) — читает UserPreferences и
    /// применяет живьём: размытие через Avalonia BlurEffect на самом Image (размывается только
    /// статичная картинка обоев, а не живой снимок окна — тот подход был отвергнут раньше в
    /// этой сессии как хрупкий, здесь речь о принципиально другом случае). Вызывается при
    /// старте окна и заново из PosSettingsWindow/SettingsView после "Сохранить" — см.
    /// MainWindowHostBridge.Window.</summary>
    internal void RefreshBackgroundWallpaper()
    {
        var prefs = UserPreferences.Instance;
        var hasRealWallpaper = prefs.ApplyBackgroundToCashierScreen && !string.IsNullOrWhiteSpace(prefs.BackgroundImagePath);

        if (hasRealWallpaper)
        {
            var bitmap = AssetPathToBitmapConverter.Instance.Convert(
                prefs.BackgroundImagePath, typeof(Bitmap), null, System.Globalization.CultureInfo.InvariantCulture) as Bitmap;
            if (bitmap is not null)
            {
                GlassAmbientLayer.IsVisible = false;

                BackgroundWallpaperImage.Source = bitmap;
                BackgroundWallpaperImage.IsVisible = true;
                // x:Name на самом BlurEffect не порождает именованное поле (генератор именует
                // только Control'ы) — достаём его через Image.Effect. Radius 0 уже "без
                // размытия" сам по себе — умножитель переводит проценты (0–100) в пиксели
                // размытия (0–24px).
                // На слабых устройствах (LowPerformanceMode) BlurEffect рендерится программно
                // и заметно грузит CPU — картинка обоев остаётся, просто без размытия.
                if (BackgroundWallpaperImage.Effect is BlurEffect blurEffect)
                    blurEffect.Radius = prefs.LowPerformanceMode ? 0 : prefs.BackgroundBlurPercent / 100.0 * 24.0;

                var tint = prefs.DarkTheme ? Colors.Black : Colors.White;
                var alpha = (byte)Math.Clamp(Math.Round(prefs.BackgroundOpacity * 255), 0, 255);
                BackgroundTintOverlay.Background = new SolidColorBrush(new Color(alpha, tint.R, tint.G, tint.B));
                BackgroundTintOverlay.IsVisible = true;
                return;
            }
        }

        BackgroundWallpaperImage.IsVisible = false;
        BackgroundTintOverlay.IsVisible = false;

        // Своего фото нет (или отключено) — на теме "Жидкое стекло" включаем имитацию: мягкие
        // цветные пятна за настоящим блюром (тот же BlurEffect, что и у обоев выше), чтобы
        // тема не выглядела как просто плоский градиент карточек, а хоть немного напоминала
        // ожидаемый эффект живого стекла, без реального снимка экрана позади диалогов.
        // На LowPerformanceMode отключаем — 4 постоянных BlurEffect (radius 90-100) заметно
        // грузят слабые GPU/CPU, для которых этот режим и включается.
        GlassAmbientLayer.IsVisible = prefs.LiquidGlassEnabled && !prefs.LowPerformanceMode && string.Equals(
            prefs.AccentTheme, "glass", System.StringComparison.OrdinalIgnoreCase);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!App.ExitWithoutLoginRedirect && !_allowMainWindowClose)
        {
            e.Cancel = true;
            if (_closeFlowActive)
                return;
            _closeFlowActive = true;
            _ = HandleCloseRequestAsync();
            return;
        }

        SaveApplicationState();
        try
        {
            _windowCts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            PosLogger.Log($"Window cancellation source already disposed: {ex.GetType().Name}", "DEBUG");
        }
    }

    private async Task HandleCloseRequestAsync()
    {
        try
        {
            if (_session.IsShiftOpen)
            {
                var shiftResult = ShiftNotClosedDialog.Prompt(this);
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.Cancel)
                    return;
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.CloseShift)
                {
                    if (!await ConfirmDiscardUnfinishedReceiptAsync().ConfigureAwait(true))
                        return;

                    await CloseShiftAsync(confirmUnfinishedReceipt: false).ConfigureAwait(true);
                }
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Window close flow canceled.", "DEBUG");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Window close flow failed: {ex}", "ERROR");
            _prompts.ShowError("Не удалось корректно завершить текущую операцию.");
        }
        finally
        {
            _closeFlowActive = false;
        }
    }

    private TelegramBotPollingService? _telegramBot;

    /// <summary>Поднимает опрос команд бота. Вызывается при открытии окна и повторно после
    /// сохранения настроек бота — повторный вызов на уже запущенном опросе безвреден.</summary>
    internal void StartTelegramBot()
    {
        try
        {
            if (!TelegramBotService.IsConfigured || !UserPreferences.Instance.TelegramCommandsEnabled)
            {
                _telegramBot?.Stop();
                return;
            }

            _telegramBot ??= new TelegramBotPollingService(
                App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>());
            _telegramBot.Start();
        }
        catch (Exception ex)
        {
            // Бот — дополнительная функция: его сбой не должен мешать работе кассы.
            PosLogger.Log($"Телеграм-бот: запустить не удалось ({ex.Message}).", "WARNING");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _applicationStateService.CancelPendingSave();
        Screens.Changed -= OnCashierScreensChanged;
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        _voiceControl.CommandRecognized -= OnVoiceCommandRecognized;
        _voiceControl.Stop();
        _telegramBot?.Stop();
        _customerDisplay.CloseForSession();
        _viewModel.Dispose();
        _windowCts.Dispose();
        if (ReferenceEquals(_hostBridge.Window, this))
            _hostBridge.Window = null;
    }

    private void OnCashierScreensChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PlaceOnPrimaryScreen();
            if (!UserPreferences.Instance.CustomerDisplay.IsEnabled)
                return;

            var result = _customerDisplay.OpenForSession(this);
            PosLogger.Log(result.Message, "CUSTOMER_DISPLAY");
            Activate();
        }, DispatcherPriority.Background);
    }

    private void OnSideMenuBackdropPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel.CloseSideMenu();
    private void OnViewModelStateChanged(object? sender, EventArgs e) =>
        ScheduleApplicationStateSave();

    private void RestoreApplicationState()
    {
        var state = _applicationStateService.Load();
        _viewModel.Catalog.RestoreState(state.Catalog);
        _viewModel.Basket.RestoreState(state.Basket);

        if (state.CatalogWidth is > 0 && state.CartWidth is > 0)
        {
            // Restore the splitter ratio, not stale pixel widths. Star-sized columns
            // always consume the full window width after maximize/resize.
            CatalogColumn.Width = new GridLength(state.CatalogWidth.Value, GridUnitType.Star);
            CartColumn.Width = new GridLength(state.CartWidth.Value, GridUnitType.Star);
        }
    }

    private void ScheduleApplicationStateSave() =>
        _applicationStateService.SaveDebounced(CaptureApplicationState);

    private void SaveApplicationState() =>
        _applicationStateService.Save(CaptureApplicationState());

    private ApplicationState CaptureApplicationState() =>
        new()
        {
            CatalogWidth = MainContentGrid.ColumnDefinitions[0].ActualWidth,
            CartWidth = MainContentGrid.ColumnDefinitions[2].ActualWidth,
            Catalog = _viewModel.Catalog.CaptureState(),
            Basket = _viewModel.Basket.CaptureState(),
        };

    private void OnCatalogGridSplitterPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        LogGridSplitterColumnsIfChanged();

    private void OnCatalogColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColumnDefinition.WidthProperty)
            LogGridSplitterColumnsIfChanged();
    }

    private void OnCartColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColumnDefinition.WidthProperty)
            LogGridSplitterColumnsIfChanged();
    }

    private void LogGridSplitterColumnsIfChanged()
    {
        var catalogWidth = Math.Round(CatalogColumn.Width.Value);
        var cartWidth = Math.Round(CartColumn.Width.Value);
        if (Math.Abs(catalogWidth - _lastLoggedCatalogWidth) < 1 &&
            Math.Abs(cartWidth - _lastLoggedCartWidth) < 1)
            return;

        _lastLoggedCatalogWidth = catalogWidth;
        _lastLoggedCartWidth = cartWidth;
        ScheduleApplicationStateSave();
    }

    private void ShowModuleWindow<T>() where T : Window
    {
        _viewModel.CloseSideMenu();
        var window = App.GetRequiredService<T>();
        window.Show(this);
    }

    private async Task RefreshShiftStateAsync(CancellationToken cancellationToken, TimeSpan? balanceTimeout = null)
    {
        try
        {
            // 2026-09-10: автономный (офлайн) режим — реальных касс/смен на сервере не
            // существует, ConstructionCashboxesListAsync/ConstructionShiftsListAsync ниже — оба
            // реальный HTTP к NurCRM, которого в этом режиме нет вообще. Раньше уходили в сеть
            // при КАЖДОМ запуске (ловились внешним catch, не роняли кассу, но добавляли задержку
            // на заведомо обречённый запрос — то самое "почему долгий вход", только для
            // автономного режима). Кассе тут достаточно любого стабильного локального ID.
            // 2026-09-12: IsCurrentSessionAutonomous, не OfflineModeHelper.UseLocalOperations —
            // тот остаётся true до конца сессии, даже если интернет потом появился, и тогда
            // обычный (не автономный) аккаунт навсегда застревал бы с фиктивным ID кассы вместо
            // настоящего с сервера.
            if (App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
            {
                // Автономная сессия с уже выставленным локальным ID (продолжение предыдущего
                // запуска) — трогать нечего, и уж точно не ходить в сеть за списком касс (см.
                // комментарий 2026-09-10 ниже) — только реальный первый вход в этой сессии.
                if (string.IsNullOrWhiteSpace(App.PosCashboxId))
                {
                    const string localCashboxId = "offline-cashbox";
                    App.PosCashboxId = localCashboxId;
                    NurMarketKassa.App.PosCashboxId = localCashboxId;
                    _session.ActiveTerminal = localCashboxId;
                    _session.PosCashboxDisplayName = Tr.T("Локальная касса", "Жергиликтүү касса", "Local register", "Yerel kasa", "Mahalliy kassa");
                    NurMarketKassa.App.PosCashboxDisplayName = _session.PosCashboxDisplayName;
                }
            }
            else
            {
                // 2026-09-14, живой баг: "Оплата не прошла — cashbox_id: Касса не найдена или не
                // принадлежит этому филиалу". Раньше весь список касс запрашивался и сверялся
                // ТОЛЬКО когда App.PosCashboxId был пуст — если он уже был заполнен (восстановлен
                // из прошлой сессии через PosAppBridge.SyncFromSession/session.ActiveTerminal),
                // блок ниже целиком пропускался. Если админ на сервере переназначил кассира на
                // другой филиал или переместил/удалил кассу, старый ID так и оставался в
                // App.PosCashboxId навсегда — каждая попытка оплаты падала с этой ошибкой, и
                // кассир не мог понять, откуда она берётся (сама касса открывалась нормально).
                // Теперь уже выбранная касса тоже сверяется со свежим списком с сервера; если её
                // там больше нет — выбор происходит заново, тем же путём, что при первом входе
                // (PreferredCashboxId, иначе первая активная), вместо того чтобы зависать на
                // мёртвом ID до следующей переустановки/сброса кассы.
                //
                // 2026-09-12: короткий явный таймаут — на машине с протухшей NurCRM-сессией этот
                // запрос не отвечает ошибкой мгновенно, а висит до дефолтного таймаута HttpClient
                // (десятки секунд), из-за чего сам вход в кассу выглядел как "очень долгий".
                // Таймаут ловим ЛОКАЛЬНО (когда сработал именно он, а не настоящая внешняя
                // отмена cancellationToken) — иначе он попал бы под catch(OperationCanceledException)
                // {throw;} ниже, который предназначен для настоящей отмены (закрытие кассы), и
                // вместо мягкого "кассу не выбрали, попробуем в другой раз" уронил бы весь вход.
                JsonElement? rawListOrNull = null;
                try
                {
                    using var cashboxTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cashboxTimeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
                    rawListOrNull = await App.ShiftApi.ConstructionCashboxesListAsync(cashboxTimeoutCts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    PosLogger.Log("Список касс не получен за 6с (протухшая сессия/нет сети) — пробуем позже.", "SHIFT");
                }

                if (rawListOrNull is { } rawList)
                {
                var cashboxes = CartDisplayHelper.ListCashboxes(rawList);
                // 2026-09-14: сверяем именно PosApp.PosCashboxId, а не App.PosCashboxId — это
                // РАЗНЫЕ статические поля (см. комментарий в NavigateToLoginAsync ниже), и именно PosApp.
                // PosCashboxId реально уходит в тело запроса оплаты (PosCheckoutService.
                // BuildCheckoutRequestBody). App.PosCashboxId — UI-слойное зеркало, синхронизация
                // с PosApp одностороння (PosAppBridge.SyncToSession читает ИЗ PosApp, не пишет
                // в него), поэтому проверка и запись здесь должны идти именно через PosApp,
                // иначе кассир видит "касса выбрана" в интерфейсе, а сервер всё равно получает
                // старый мёртвый ID при оплате.
                // 2026-09-14: PosApp.RejectedCashboxIds — кассы, которые сервер уже реально
                // отверг ("не принадлежит этому филиалу") за эту сессию, см. PosCheckoutService/
                // CashShiftService.TryReassignCashboxAsync — список от сервера сам по себе не
                // фильтруется по филиалу, поэтому "есть в списке" одно не гарантирует, что она
                // подходит; известно отвергнутую точно не выбираем заново при входе.
                var currentStillValid = !string.IsNullOrWhiteSpace(PosApp.PosCashboxId)
                    && !PosApp.RejectedCashboxIds.Contains(PosApp.PosCashboxId)
                    && cashboxes.Any(c => c.Id == PosApp.PosCashboxId);

                if (!currentStillValid)
                {
                var preferredId = UserPreferences.Instance.PreferredCashboxId;
                // 2026-09-07: сохранённый PreferredCashboxId может принадлежать ДРУГОЙ компании
                // (кассир сменился на аккаунт другой компании через «Сменить кассира» или релогин) —
                // сервер отвечал "cashbox: Обязательное поле" при открытии смены с чужим ID кассы.
                // Поэтому доверяем ему только если он реально есть в списке касс ТЕКУЩЕЙ компании
                // и сервер её ещё не отвергал за эту сессию (см. PosApp.RejectedCashboxIds).
                var preferredMatch = string.IsNullOrWhiteSpace(preferredId) || PosApp.RejectedCashboxIds.Contains(preferredId)
                    ? default
                    : cashboxes.FirstOrDefault(c => c.Id == preferredId);

                if (preferredMatch.Id != null)
                {
                    // Кассир вручную выбрал кассу в настройках — она перекрывает автовыбор
                    // (тот берёт первую активную и может ошибиться филиалом).
                    App.PosCashboxId = preferredMatch.Id;
                    NurMarketKassa.App.PosCashboxId = preferredMatch.Id;
                    PosApp.PosCashboxId = preferredMatch.Id;
                    _session.ActiveTerminal = preferredMatch.Id;
                    _session.PosCashboxDisplayName = preferredMatch.DisplayName;
                    NurMarketKassa.App.PosCashboxDisplayName = preferredMatch.DisplayName;
                    PosApp.PosCashboxDisplayName = preferredMatch.DisplayName;
                }
                else
                {
                    // 2026-09-14: тот же список cashboxes, что и выше (не TryFirstCashbox с нуля) —
                    // тот не знает про PosApp.RejectedCashboxIds и мог бы вернуть уже отвергнутую
                    // сервером кассу заново.
                    // 2026-09-17: раньше здесь была своя копия "первая активная по порядку ответа
                    // сервера" без учёта имени кассы — из-за этого касса при перезапуске могла
                    // выбрать не "Основную"/"Главную", даже когда она есть в списке (см.
                    // CartDisplayHelper.PreferMainCashbox — та же логика, что и TryFirstCashbox).
                    var unrejected = cashboxes.Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id)).ToList();
                    var fallback = CartDisplayHelper.PreferMainCashbox(unrejected) ?? default;

                    if (fallback.Id != null)
                    {
                        App.PosCashboxId = fallback.Id;
                        NurMarketKassa.App.PosCashboxId = fallback.Id;
                        PosApp.PosCashboxId = fallback.Id;
                        _session.ActiveTerminal = fallback.Id;
                        _session.PosCashboxDisplayName = fallback.DisplayName;
                        NurMarketKassa.App.PosCashboxDisplayName = fallback.DisplayName;
                        PosApp.PosCashboxDisplayName = fallback.DisplayName;
                    }
                }
                }
                }
            }

            await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(true);
            NurMarketKassa.App.SyncToSession(_session);

            // 2026-09-12: тот же баг, что уже поправлен в ShiftStateService.RefreshAsync (см. её
            // комментарий) — здесь была ВТОРАЯ, отдельная копия того же неверного условия,
            // которая тут же затирала уже корректно посчитанный ShiftStateService баланс своим
            // собственным, застревающим на 0.00 для обычного (не автономного) аккаунта, который
            // просто открыл смену без сети в моменте.
            if (_session.IsShiftOpen)
            {
                if (App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
                {
                    _shiftCashBalance = OfflinePosStateStore.ReadShiftCashBalance();
                    // Автономный режим не ходит на сервер — разбивки продаж (как в вебе) взять
                    // неоткуда, диалог закрытия смены покажет только "Остаток по системе".
                    _shiftTotals = null;
                }
                else
                {
                    using var balanceTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var effectiveBalanceTimeout = balanceTimeout ?? TimeSpan.FromSeconds(6);
                    balanceTimeoutCts.CancelAfter(effectiveBalanceTimeout);
                    try
                    {
                        // 2026-09-12: один и тот же ответ используется и для баланса, и для
                        // разбивки (Начальная сумма/Продажи/Наличными/Безналичными как в вебе) —
                        // не делаем второй сетевой запрос ради второго набора чисел.
                        var shiftsList = await App.ShiftApi.ConstructionShiftsListAsync(openOnly: true, ct: balanceTimeoutCts.Token).ConfigureAwait(true);
                        _shiftCashBalance = ShiftBalanceHelper.FindOpenShiftBalance(shiftsList, App.PosCashboxId)
                            ?? OfflinePosStateStore.ReadShiftCashBalance();
                        _shiftTotals = ShiftBalanceHelper.FindOpenShiftTotals(shiftsList, App.PosCashboxId);
                        // 2026-09-12, временная диагностика по жалобе "не отображаешь подробно" —
                        // снять после подтверждения, что разбивка реально доходит до диалога.
                        PosLogger.Log(
                            $"totals fetch: cashboxId={App.PosCashboxId}, balance={_shiftCashBalance}, found={_shiftTotals is not null}, " +
                            $"opening={_shiftTotals?.OpeningCash?.ToString() ?? "null"}, " +
                            $"totalSales={_shiftTotals?.TotalSales?.ToString() ?? "null"}, " +
                            $"cash={_shiftTotals?.CashSales?.ToString() ?? "null"}, " +
                            $"nonCash={_shiftTotals?.NonCashSales?.ToString() ?? "null"}, " +
                            $"rawKind={shiftsList.ValueKind}",
                            "SHIFT");
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Сработал НАШ таймаут, а не настоящая отмена извне (закрытие
                        // кассы) — откатываемся на локальный остаток, а не прокидываем
                        // исключение дальше в catch(OperationCanceledException){throw;} ниже,
                        // который предназначен именно для настоящей внешней отмены.
                        // 2026-09-12: живой случай — этот конкретный сервер/сессия молча не
                        // укладывались в 6с (тот же "протухший" эффект, что чинили для входа),
                        // из-за чего разбивка в закрытии смены никогда не успевала дойти.
                        PosLogger.Log($"totals/balance fetch TIMED OUT after {effectiveBalanceTimeout.TotalSeconds:0}s — используем локальный кэш.", "SHIFT");
                        _shiftCashBalance = OfflinePosStateStore.ReadShiftCashBalance();
                        _shiftTotals = null;
                    }
                }
            }

            _viewModel.Toolbar.RefreshUserTitle();
            _viewModel.Toolbar.Status.RefreshFromSession();
            UpdateShiftBalanceUi();
            _viewModel.Toolbar.NotifyShiftStateChanged();

            if (_session.IsShiftOpen)
                _viewModel.Catalog.StatusText = "Смена открыта";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SHIFT refresh failed: {ex.Message}", "SHIFT");
            UpdateShiftBalanceUi();
        }
    }

    private async Task ApplyShiftOpenedAsync(decimal openingCash)
    {
        NurMarketKassa.App.SyncFromSession(_session);
        var result = await _cashShiftService.OpenShiftAsync(openingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? "Не удалось открыть смену.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.InfoMessage))
            _prompts.ShowToast(result.InfoMessage);

        NurMarketKassa.App.SyncToSession(_session);
        App.PosCashboxId = NurMarketKassa.App.PosCashboxId;

        _shiftCashBalance = result.Balance ?? openingCash;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = ShiftBalanceHelper.FormatBalance(EffectiveShiftCashBalance);
        _viewModel.Catalog.StatusText = $"Смена открыта. Остаток: {openingCash:0.00} сом";
        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var displayResult = _customerDisplay.OpenForSession(this);
            if (displayResult.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
            else
                PosLogger.Log(displayResult.Message, "CUSTOMER_DISPLAY");
        }
    }

    private async Task ApplyShiftClosedAsync(decimal? closingCash)
    {
        if (!_session.IsShiftOpen)
            return;

        NurMarketKassa.App.SyncFromSession(_session);
        // Захватываем ДО закрытия — CloseShiftAsync при успехе обнуляет ActiveShiftId, а
        // _shiftTotals понадобится ниже для отчёта (см. ShowShiftClosedReport).
        var shiftIdBeforeClose = NurMarketKassa.PosApp.ActiveShiftId;
        var totalsBeforeClose = _shiftTotals;
        var result = await _cashShiftService.CloseShiftAsync(closingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? "Не удалось закрыть смену.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.InfoMessage))
            _prompts.ShowToast(result.InfoMessage);

        _viewModel.Basket.ClearAfterShiftClose();
        NurMarketKassa.App.SyncToSession(_session);

        _shiftCashBalance = result.Balance ?? closingCash ?? 0m;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = "Смена не открыта";
        _viewModel.Catalog.StatusText = "Смена закрыта.";
        _customerDisplay.CloseForSession();

        ShowShiftClosedReport(shiftIdBeforeClose, totalsBeforeClose, result.Totals, _shiftCashBalance ?? 0m);
    }

    /// <summary>2026-09-14, по просьбе пользователя ("нужен подробный отчёт при закрытии
    /// смены, как на вебке") — раньше после закрытия смены кассир видел только короткий тост
    /// ("Смена закрыта."), без единой цифры по чекам/наличным/безналичным.
    ///
    /// 2026-09-15, живой баг ("система не считает наличную и безналичную"): раньше здесь
    /// использовался ТОЛЬКО totalsBeforeClose — снимок _shiftTotals, подтянутый в фоне ЕЩЁ ДО
    /// закрытия (пока был открыт CloseShiftDialog); если кассир закрывал смену вскоре после
    /// последней продажи, снимок мог не успеть её учесть, и разбивка наличные/безналичные
    /// путалась. fresh — гарантированно свежие данные из ОТВЕТА САМОГО закрытия
    /// (CashShiftService.CloseShiftAsync), берём поле оттуда, только если оно не пустое; иначе
    /// откатываемся на снимок "до закрытия" — так отчёт не покажет пустоту, если сервер в ответе
    /// на закрытие разбивку не прислал.</summary>
    private void ShowShiftClosedReport(
        string? shiftId, ShiftBalanceHelper.ShiftTotals? before, CashShiftClosingTotals? fresh, decimal actualClosingCash)
    {
        try
        {
            var shift = new ShiftModel
            {
                Id = shiftId ?? "",
                ShiftNumber = shiftId ?? "",
                ClosedAt = DateTime.Now,
                Cashier = NurMarketKassa.PosApp.CurrentUserDisplayName ?? "—",
                Status = "Закрыта",
                Revenue = fresh?.TotalSales ?? before?.TotalSales ?? 0m,
                ClosingCash = actualClosingCash,
                OpeningCash = fresh?.OpeningCash ?? before?.OpeningCash,
                CashSales = fresh?.CashSales ?? before?.CashSales,
                NonCashSales = fresh?.NonCashSales ?? before?.NonCashSales,
                DebtSales = fresh?.DebtSales ?? before?.DebtSales,
                SalesCount = fresh?.SalesCount ?? before?.SalesCount,
                // Расход и приход — с сервера. Без них отчёт считал изъятия по локальному
                // файлу и печатал меньше настоящего (живой случай: расход 1 940, в чеке 490).
                ExpenseTotal = before?.ExpenseTotal,
                IncomeTotal = before?.IncomeTotal,
                // Ожидаемый остаток и расхождение НЕ берём из снимка «до закрытия»: они
                // посчитаны до того, как кассир ввёл фактическую сумму. Здесь их считает
                // сам отчёт по опорным цифрам, которые уже серверные.
            };
            SendShiftSummaryToTelegram(shift, shiftId);
            PosDialogHost.Show(new ShiftDetailsDialog(shift), this);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShowShiftClosedReport failed: {ex.Message}", "SHIFT");
        }
    }

    /// <summary>Отправляет владельцу сводку по закрытой смене в Telegram — то, ради чего
    /// функция и делалась: владелец видит итог дня, не приезжая в магазин.
    ///
    /// Намеренно НЕ ждём ответа и не показываем ошибку кассиру: Telegram недоступен — это
    /// проблема владельца, а не кассира, который сейчас закрывает смену. Всё уходит в лог.</summary>
    private static void SendShiftSummaryToTelegram(ShiftModel shift, string? shiftId)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.TelegramShiftSummaryEnabled || !TelegramBotService.IsConfigured || !TariffGate.CanUseTelegramBot)
            return;

        // Внесения и изъятия берём по тому же ключу смены, что и печатный отчёт, — иначе
        // цифра в телефоне разошлась бы с бумажкой в руках кассира.
        var (deposits, withdrawals) = string.IsNullOrEmpty(shiftId)
            ? (0m, 0m)
            : ShiftCashOperationsStore.SumsForShift(shiftId);

        var text = TelegramBotService.BuildShiftSummary(shift, deposits, withdrawals, prefs.StoreName);

        _ = Task.Run(async () =>
        {
            var error = await TelegramBotService.SendAsync(text).ConfigureAwait(false);
            PosLogger.Log(
                error is null ? "Сводка по смене отправлена владельцу в Telegram." : $"Сводка в Telegram не ушла: {error}",
                error is null ? "TELEGRAM" : "WARNING");
        });
    }

    /// <summary>Оплата отклонена сервером как "смена не открыта", хотя локальный ID смены
    /// всё ещё был установлен (открыта офлайн и сервер о ней не знает, либо закрыта где-то
    /// ещё — например, в NurCRM). Ту же серверную операцию закрытия здесь не зовём: сервер
    /// уже и так считает её закрытой, нужно только привести локальное состояние/тулбар в
    /// соответствие, а корзину не трогать — набранный чек остаётся, оплатить его можно будет
    /// сразу после того, как кассир заново откроет смену.</summary>
    private void OnShiftDesyncDetected(object? sender, EventArgs e)
    {
        if (!_session.IsShiftOpen)
            return;

        PosLogger.Log("Shift desync detected after failed payment — resetting local shift state.", "PAYMENT");

        NurMarketKassa.PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        NurMarketKassa.App.SyncToSession(_session);

        _shiftCashBalance = 0m;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = "Смена не открыта";
        _viewModel.Catalog.StatusText = "Смена закрыта на сервере — откройте смену заново.";
        _customerDisplay.CloseForSession();
    }

    private void UpdateShiftBalanceUi()
    {
        var balance = EffectiveShiftCashBalance;
        var balanceText = _session.IsShiftOpen
            ? $"Касса: {ShiftBalanceHelper.FormatBalance(balance)}"
            : "Касса: 0.00 сом";

        _viewModel.Toolbar.Status.SetShiftBalance(balance ?? 0m);
        _viewModel.SideMenu.ShiftBalanceText = _session.IsShiftOpen
            ? ShiftBalanceHelper.FormatBalance(balance)
            : "Смена не открыта";

        if (_viewModel.Toolbar.Status.ShiftBalanceText != balanceText)
            _viewModel.Toolbar.Status.ShiftBalanceText = balanceText;
    }

    private async Task NavigateToLoginAsync()
    {
        FrmKeyboard.KillKeyboard();

        try
        {
            if (!_windowCts.IsCancellationRequested)
                SaveApplicationState();
            _windowCts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            PosLogger.Log($"Window already closing: {ex.GetType().Name}", "DEBUG");
        }

        App.AuthApi.ClearSession();
        // Logout is explicit: remove the DPAPI session as well as in-memory tokens.
        await App.GetRequiredService<NurMarketKassa.Core.Contracts.IAuthSessionManager>()
            .ClearSessionAsync()
            .ConfigureAwait(true);
        OfflineAuthSessionStore.Clear();
        App.PosCashboxId = null;
        // App.PosCashboxId (NurMarketKassa.AvaloniaHost.App) и PosApp.PosCashboxId
        // (Infrastructure) — два НЕЗАВИСИМЫХ статических поля, несмотря на похожие имена;
        // NurMarketKassa.App.PosCashboxId — тонкий мост поверх второго. Открытие смены
        // (CashShiftService.EnsurePosCashboxIdAsync) читает именно PosApp.PosCashboxId —
        // без этой строчки при выходе он оставался от ПРЕДЫДУЩЕГО аккаунта, и при входе
        // в другую компанию касса пыталась открыть смену с чужим/несуществующим ID кассы
        // (сервер отвечал "cashbox: Обязательное поле").
        PosApp.PosCashboxId = null;
        _session.ActiveShiftId = null;
        _session.ActiveTerminal = null;
        _session.PosCashboxDisplayName = null;
        _session.IsOfflineBootstrap = false;
        _session.OfflineBootstrapMessage = null;
        App.IsOfflineBootstrap = false;
        App.OfflineBootstrapMessage = null;

        Hide();

        var login = App.GetRequiredService<LoginWindow>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = login;

        login.Show();

        _allowMainWindowClose = true;
        try
        {
            Close();
        }
        finally
        {
            _allowMainWindowClose = false;
        }
    }

    private void ApplyFullscreenPreference()
    {
        if (!UserPreferences.Instance.Fullscreen)
            return;

        CanResize = false;
        FullscreenHelper.Apply(this);
    }
}
