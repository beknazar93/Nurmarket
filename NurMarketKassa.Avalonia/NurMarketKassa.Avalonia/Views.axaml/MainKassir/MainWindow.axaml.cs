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
using NurMarketKassa.AvaloniaHost.Views.Main.Controls;
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
        RegisterAccessibleButtonNames();
        _viewModel = viewModel;
        _session = session;
        _hostBridge = hostBridge;
        _cashShiftService = cashShiftService;
        _shiftStateService = shiftStateService;
        _prompts = prompts;
        _permissions = ResolveService<IPermissionService>();
        _customerDisplay = customerDisplay;
        _barcodeInputService = ResolveService<IBarcodeInputService>();
        _hostBridge.Window = this;
        WireDialogBridge();

        InitializeComponent();
        // 2026-10-23 (аудит Б-9): заголовок системной рамки — цветом темы кассы.
        NurMarketKassa.AvaloniaHost.Services.WindowChrome.Apply(this);
        DataContext = _viewModel;
        // 2026-10-23 (аудит, круг 5): узкая шапка — по ширине самой шапки (Bounds), а не по экрану.
        KassaHeaderBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateHeaderNarrow();
        };
        CatalogGridSplitter.PointerReleased += OnCatalogGridSplitterPointerReleased;
        CatalogColumn.PropertyChanged += OnCatalogColumnPropertyChanged;
        CartColumn.PropertyChanged += OnCartColumnPropertyChanged;
        // 2026-10-04, Android: экран поворачивается вместе с телефоном — вертикально каталог над чеком.
        if (OperatingSystem.IsAndroid())
            SizeChanged += (_, _) => ApplyPortraitLayout();
        RestoreApplicationState();
        ApplyMarketSphereToCatalog(fromSettings: false);
        MarketSpheres.Changed += OnMarketSphereChanged;
        _viewModel.Catalog.StateChanged += OnViewModelStateChanged;
        _viewModel.Basket.StateChanged += OnViewModelStateChanged;
        // 2026-10-04: оплата проведена — состояние на диск сразу, без задержки 0,4 с (иначе после сбоя сразу
        // за оплатой касса восстанавливала оплаченный чек). SaveDebounced с 0 отменяет и прежнюю отложенную запись.
        _viewModel.Basket.PaymentCommitted += (_, _) => _applicationStateService.SaveDebounced(CaptureApplicationState, 0);
        _viewModel.Basket.ShiftDesyncDetected += OnShiftDesyncDetected;
        _viewModel.Basket.EnsureShiftBeforePayment = EnsureShiftBeforePaymentAsync;
        // 2026-10-04: скан QR клиента NurCRM, а клиента нет в базе — окно «Новый клиент» с телефоном.
        _viewModel.Basket.OfferNewClientFromQr = OfferNewClientFromQrAsync;
        _viewModel.Basket.CheckoutSucceeded += OnCheckoutSucceeded;
        // 2026-10-20 (живой аудит кассы, К1–К3, К6): как заплатили — для суммы возврата и «Истории чеков» после возврата.
        _viewModel.Basket.CheckoutSucceeded += RememberSaleForReturns;
        // 2026-10-20 (К12): оплата долга (в этой кассе, в программе владельца или на соседней кассе) — «Касса» в шапке сразу.
        PosDataEvents.ClientDebtChanged += OnClientDebtChangedRefreshBalance;
        // 2026-10-18, редизайн (этап 6): «Продажа прошла» — плашка внизу вместо окна (MainWindow.SaleDoneBar.cs).
        InitSaleDoneBar();
        ServerOutageMonitor.Recovered += OnServerRecovered;
        ServerOutageMonitor.QueueFlushed += OnQueueFlushed;

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
        // 2026-10-20: Enter готового скана — сканеру, раньше «Оплаты» и кнопки с фокусом (см. OnScanEnterTunnel).
        AddHandler(KeyDownEvent, OnScanEnterTunnel, RoutingStrategies.Tunnel);
        InstallPopupScanHook();
        // 2026-09-16, тоже Tunnel и по той же причине: если фокус остался внутри списка
        // каталога (обычное дело после клика мышкой по плитке товара — фокус оседает на кнопке
        // плитки, потомке ListBox'а), у ListBox'а уже есть СВОЙ Bubble-обработчик Enter
        // (ProductsList_KeyDown — добавляет/повышает количество выделенного товара) и он
        // срабатывает раньше, чем событие добубнится до окна, помечая его Handled — до
        // OnMainWindowKeyDown оно уже не доходит. Из-за этого "Оплатить по Enter" не работал,
        // а вместо этого просто прибавлялось количество товара (репорт пользователя). Tunnel
        // гарантированно выполняется раньше любого Bubble-обработчика внутри окна.
        // 2026-09-27: здесь же — все действия на одиночных клавишах (Настройки → Клавиши):
        // оплата (Enter), добавить товар (Num +), убавить/убрать (Num −).
        AddHandler(KeyDownEvent, OnPlainHotkeyTunnel, RoutingStrategies.Tunnel);
        // NOTE: the cart's +/- quantity buttons keeping keyboard focus after a click (so the
        // next scan's Enter re-fires them instead of completing) is now fixed narrowly at the
        // button level (Focusable="False" in BasketPanelView.axaml) instead of here. Two prior
        // attempts at a window-wide fix (tunneling all KeyDown; restoring focus after every
        // Button.Click) both broke the scanner more broadly than the bug they fixed — do not
        // reintroduce either without confirming real hardware still scans correctly afterward.

        ApplyFullscreenPreference();
        // Масштаб — до показа окна: UiScaleHelper дождётся, пока окно окажется на экране, и
        // первый же кадр кассы будет подогнан под экран, а не в родных 1280×840.
        RefreshUiScale();
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

        // Разделы владельца в кассе — в автономном режиме и на тарифе «Старт» (см.
        // AppMode.ShowOwnerSectionsInKassa). С «Стандарта» они в программе «NurMarket Владелец»,
        // которая ставится вместе с кассой (ярлык создаёт OwnerShortcuts при установке и обновлении).
        NurMarketKassa.Services.AppMode.OwnerSectionsInKassa =
            App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous;
        // 2026-10-02: прокат (одежда и услуги). Залог приходуется в кассу смены — перед прокатом
        // открываем смену; стоимость проката — строкой «Прокат №…» в текущем чеке.
        _viewModel.SideMenu.NavigateRentalAction ??= () =>
        {
            var window = new NurMarketKassa.AvaloniaHost.Views.RentalsWindow(
                async () =>
                {
                    if (!_session.IsShiftOpen)
                        await OpenShiftAsync().ConfigureAwait(true);
                    return _session.IsShiftOpen;
                },
                (name, price) => _viewModel.Basket.AddCustomItem(name, price, 1, false));
            _ = window.ShowDialog(this);
        };
        NurMarketKassa.AvaloniaHost.Views.Main.Controls.BasketExtraActions.OpenRentals ??= _viewModel.SideMenu.NavigateRentalAction;
        // 2026-10-17, редизайн (этап 4): журнал «Удалено из чека» — владельцу и администратору (как «Настройки»).
        _viewModel.SideMenu.NavigateCartRemovalsAction ??= () =>
        {
            if (Authorize(PosPermissions.ViewSettings))
                _ = PosDialogHost.ShowAsync(new CartRemovalsWindow(), this);
        };
        // 2026-10-02: «Оформить как прокат» из окна оплаты.
        _viewModel.Basket.RentalFromCart ??= async (items, clientId, clientName) =>
        {
            // Цена за сутки по умолчанию — сумма строк чека (у товара-услуги «Аренда…» это и есть цена суток).
            var perDay = Convert.ToDouble(CartTotalsCalculator.Calculate(ResolveCartService().Root).TotalDue);
            var dlg = new NurMarketKassa.AvaloniaHost.Views.NewRentalWindow(items, clientId, clientName, perDay);
            var created = await dlg.ShowDialog<NurMarketKassa.Services.Api.RentalDto?>(this).ConfigureAwait(true);
            return created is null ? null : (created, dlg.RentTotal);
        };
        _viewModel.SideMenu.RefreshEntitlements();

        progress?.Report(Tr.T("Загрузка кассы...", "Касса жүктөлүүдө...", "Loading the till…", "Kasa yükleniyor...", "Kassa yuklanmoqda..."));

        if (AccountCatalogIsolation.RequireForcedCatalogSync)
            _viewModel.Catalog.StatusText = Tr.T("Требуется синхронизация каталога для нового пользователя.", "Жаңы колдонуучу үчүн каталогду синхрондоштуруу керек.", "Catalog sync is required for the new user.", "Yeni kullanıcı için katalog senkronizasyonu gerekiyor.", "Yangi foydalanuvchi uchun katalogni sinxronlash kerak.");

        progress?.Report(Tr.T("Загрузка профиля...", "Профиль жүктөлүүдө...", "Loading profile…", "Profil yükleniyor...", "Profil yuklanmoqda..."));
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
                // 2026-10-25, ТЗ «оприходование в фоне» (Q5): очередь приходов в фоне догружает незавершённое (после смены данных аккаунта).
                NurMarketKassa.AvaloniaHost.Views.BackgroundReceiptsHost.Start();
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

        progress?.Report(Tr.T("Обновление смены...", "Смена жаңыртылууда...", "Updating the shift…", "Vardiya güncelleniyor...", "Smena yangilanmoqda..."));
        await RefreshShiftStateAsync(cancellationToken).ConfigureAwait(true);
        StartPhoneSalePrint(); // 2026-10-27, К-П1: печать чеков телефона на этой кассе (по умолчанию выключено)

        progress?.Report(Tr.T("Подготовка рабочего места...", "Жумуш орду даярдалууда...", "Preparing the workstation…", "Çalışma alanı hazırlanıyor...", "Ish joyi tayyorlanmoqda..."));
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
                    $"Versiya {currentVersion}. Nimalar o'zgardi:\n\n{AppChangelog.LatestAsBulletedText()}"),
                Tr.T("Касса обновлена", "Касса жаңыртылды", "Till updated", "Kasa güncellendi", "Kassa yangilandi"),
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
                    $"Компаниянын мөөнөтү бүттү ({subscription.EndDate:dd.MM.yyyy}). Сураныч, төлөңүз!", $"Your company's subscription has expired ({subscription.EndDate:dd.MM.yyyy}). Please make a payment.", $"Şirketin abonelik süresi doldu ({subscription.EndDate:dd.MM.yyyy}). Lütfen ödeme yapın!", $"Kompaniya obunasi muddati tugadi ({subscription.EndDate:dd.MM.yyyy}). Iltimos, to'lovni amalga oshiring!"),
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
                    "Абоненттик төлөмдү мөөнөтүнөн мурда төлөңүз.", $"Your NurCRM subscription expires on {subscription.EndDate:dd.MM.yyyy} ({subscription.DaysRemaining} days left).\nPlease pay the subscription fee in advance.", $"NurCRM aboneliğiniz {subscription.EndDate:dd.MM.yyyy} tarihinde sona eriyor (kalan gün: {subscription.DaysRemaining}).\nLütfen abonelik ücretini önceden ödeyin.", $"NurCRM obunasi muddati tugaydi: {subscription.EndDate:dd.MM.yyyy} ({subscription.DaysRemaining} kun qoldi).\nIltimos, abonent to'lovini oldindan to'lang."),
                PosAlertKind.Warning,
                buttonText: Tr.T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish"));
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
                        "Абоненттик төлөмдү төлөңүз.", $"Your NurCRM subscription expires on {status.EndDate:dd.MM.yyyy} ({status.DaysRemaining} days left).\nPlease pay the subscription fee.", $"NurCRM aboneliğiniz {status.EndDate:dd.MM.yyyy} tarihinde sona eriyor (kalan gün: {status.DaysRemaining}).\nLütfen abonelik ücretini ödeyin.", $"NurCRM obunasi muddati tugaydi: {status.EndDate:dd.MM.yyyy} ({status.DaysRemaining} kun qoldi).\nIltimos, abonent to'lovini to'lang."),
                    PosAlertKind.Warning,
                    buttonText: Tr.T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish")).ConfigureAwait(true);
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
        WindowChrome.RepaintAll(); // 2026-10-23 (Б-9): заголовки окон — под новую тему (раньше в тёмной оставались белыми)
    }

    /// <summary>2026-09-15, по явной просьбе пользователя ("убери его, верни виртуальную
    /// клавиатуру Windows!") — свою FrmKeyboard пробовали как раз-таки замену системной osk.exe
    /// (см. историю правок этого метода), но пользователю она не подошла, нужна именно
    /// привычная клавиатура Windows. Возвращена системная osk.exe без собственной FrmKeyboard.</summary>
    internal void ToggleKeyboard()
    {
        // 2026-10-21 (пакет 6, п. 58): повторное нажатие «Клавиатура» закрывает открытую клавиатуру Windows (было — только
        // поднимало её наверх, закрыть можно было лишь крестиком osk).
        if (OperatingSystem.IsWindows() && WindowsOperatingSystemKeyboardService.TryCloseVisible())
            return;
        App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();

        // 2026-09-30, «экранная клавиатура не работает»: без поля ввода в фокусе набранное на
        // клавиатуре Windows уходило в буфер сканера штрихкодов (OnKeyDown → _barcodeInputService)
        // и пропадало. Если кассир не выбрал поле — ставим курсор в поиск товара.
        if (FocusManager?.GetFocusedElement() is not TextBox)
            ActiveCatalogSurface?.FocusProductSearch();
    }

    /// <summary>Этап 7 бэклога "Доработки" ("Упрощение"): при неудачной проверке экрана
    /// покупателя — короткое сообщение с двумя кнопками ("Открыть настройки"/"Понятно") вместо
    /// обычного предупреждения, сразу ведущее в Настройки → Монитор, где это чаще всего и
    /// настраивается/чинится.</summary>
    internal void CheckCustomerDisplay()
    {
        var result = _customerDisplay.TestSecondaryScreen(this);
        if (result.IsSuccess)
        {
            // 2026-10-21 (пакет 6, п. 56): «Экран покупателя открыт на …» — строкой под чеком, без окна, которое надо закрывать.
            _viewModel.Basket.CartMessage = result.Message;
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
        // Действия на одиночных клавишах разбирает OnPlainHotkeyTunnel (со своими правилами
        // фокуса и сканера), здесь — только комбинации с Ctrl/Alt: они работают из любого поля.
        if (_hotkeys.TryMatch(e, plain: false, out var action))
        {
            e.Handled = true;
            ExecuteHotkey(action);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && TryGetHotkeyGroup(e.Key, out var group))
        {
            e.Handled = true;
            OpenHotkeyGroup(group);
            return;
        }

        // Esc на кассе (2026-09-24): закрывает боковое меню, а из поля поиска или ввода кода
        // возвращает к сканеру (поиск при этом очищается). Чек Esc не трогает.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_viewModel.IsSideMenuOpen)
            {
                _viewModel.CloseSideMenu();
                return;
            }

            if (FocusManager?.GetFocusedElement() is TextBox { Name: "ProductSearchBox" } search)
                search.Text = "";
            RestoreScannerFocus();
            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (ReferenceEquals(e, _scanKeySeenInTunnel))
            return;

        _barcodeInputService.ProcessKeyDown(e);
    }

    /// <summary>Действия на одиночных клавишах без Ctrl/Alt (Настройки → Клавиши): «Оплата»
    /// (по умолчанию Enter — просьба пользователя 2026-09-16), «Добавить товар в чек» (Num +) и
    /// «Убавить / убрать товар» (Num −) — 2026-09-27. Вынесено в Tunnel, чтобы гарантированно
    /// сработать раньше локальных Bubble-обработчиков внутри окна (см. комментарий у регистрации).
    /// Не перехватывает, когда фокус в текстовом поле — там клавиша нужна для набора.</summary>
    private void OnPlainHotkeyTunnel(object? sender, KeyEventArgs e)
    {
        // 2026-10-20: открыто меню ☰ «Новое» — Enter/Num± разбирает меню (пункт, поиск, сканер), не «Оплата» и не чек.
        if (NurMarketKassa.AvaloniaHost.Views.Main.Controls.SideMenuView.CapturesKeyboard)
            return;
        if (!_hotkeys.TryMatch(e, plain: true, out var action))
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (action)
        {
            case PosHotkeyAction.Pay:
                TryPayFromKeyboard(e);
                return;
            case PosHotkeyAction.AddCatalogItem:
                // Num + / Num − сканер не печатает (он шлёт клавиши основной клавиатуры), но
                // забираем их раньше буфера сканера, иначе Num − ушёл бы туда как «-».
                e.Handled = true;
                AddFromCatalogOrIncrease();
                return;
            case PosHotkeyAction.DecreaseQuantity:
                e.Handled = true;
                DecreaseOrRemoveLine();
                return;
            default:
                if (_barcodeInputService.HasBufferedInput)
                    return;
                e.Handled = true;
                ExecuteHotkey(action);
                return;
        }
    }

    private void TryPayFromKeyboard(KeyEventArgs e)
    {
        // 2026-09-16, срочный живой баг ("при сканировании сразу идёт на оплатить"): сканер
        // штрихкода эмулирует быстрый набор с клавиатуры и завершает каждый скан символом
        // Enter — если в корзине уже есть хотя бы один товар (после первого скана), этот Enter
        // раньше перехватывался здесь и нажимал "Оплатить" вместо завершения добавления
        // следующего отсканированного товара. HasBufferedInput истинно, пока в буфере сканера
        // ещё копятся быстро введённые символы — значит текущий Enter завершает скан, а не
        // осознанное нажатие кассира, и его нужно пропустить сюда дальше, к самому сканеру.
        if (_barcodeInputService.HasBufferedInput)
            return;
        // 2026-10-20 (П1-0, живая проверка: окно оплаты большого чека открылось во время сканирования): Enter сразу
        // за сканом (второй Enter сканера, CR+LF) — часть скана, а не «Оплата»: его съедаем.
        if (_barcodeInputService is AvaloniaKeyboardWedgeBarcodeService { ScanJustCompleted: true })
        {
            e.Handled = true;
            return;
        }
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
        // 2026-10-20: при открытом меню ☰ «Новое» стрелки выбирают его пункты, а не товар в каталоге под затемнением.
        if (NurMarketKassa.AvaloniaHost.Views.Main.Controls.SideMenuView.CapturesKeyboard)
            return;

        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is TextBox || ActiveCatalogSurface?.ContainsFocus(focused) == true)
            return;

        if (ActiveCatalogSurface?.TryEnterCatalogNavigation() == true)
            e.Handled = true;
    }

    /// <summary>Каталог, которым сейчас управляет клавиатура (2026-09-28, раскладки кассы):
    /// в «Классике» — CatalogPanel, как было; в «Табличной», «Карточках», «Минимале», «Профи» —
    /// сама раскладка; в «1С» плиток нет — null, и клавиши ведут себя как раньше при скрытом каталоге.</summary>
    private ICatalogKeyboardSurface? ActiveCatalogSurface =>
        MainContentGrid.IsVisible ? CatalogPanel
        : AltLayoutHost.IsVisible ? _altLayout as ICatalogKeyboardSurface
        : null;

    /// <summary>F1-F12 без модификаторов — группы быстрых товаров (как в веб-версии NurCRM),
    /// не конфликтуют с действиями кассы: те переведены на Ctrl+... в PosHotkeyService.</summary>
    /// <summary>Товары горячей клавиши — отдельным окном, а не фильтром каталога.
    ///
    /// Фильтр менял список слева и держался, пока его не снимут тем же нажатием: продавец
    /// терял из виду остальной каталог и не всегда понимал, почему товаров стало мало. Окно
    /// живёт ровно один выбор: нажал товар — он в чеке, окно закрылось, каталог не тронут.</summary>
    private void OpenHotkeyGroup(string group)
    {
        // Цикл (2026-09-28): другая F-клавиша при открытом окне закрывает его и открывает
        // свою группу сразу — раньше окно не менялось, его надо было закрыть и нажать снова.
        string? next = group;
        for (var guard = 0; next != null && guard < 50; guard++)
        {
            var current = next;
            var products = LocalProductRepository.Instance.LoadAllTiles()
                // Именно HotkeyGroup: в модели есть ещё похожее HotkeyGroupName, но из базы
                // заполняется это поле — по второму список всегда выходил пустым.
                .Where(p => string.Equals(p.HotkeyGroup, current, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var chosen = HotkeyGroupDialog.Show(this, current, products, out next);
            if (chosen != null)
            {
                _viewModel.Catalog.SelectProductCommand.Execute(chosen);
                return;
            }
        }
    }

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
                ActiveCatalogSurface?.FocusProductSearch();
                break;
            case PosHotkeyAction.Pay:
                ExecuteCommand(_viewModel.Basket.PayCommand);
                break;
            case PosHotkeyAction.AddCatalogItem:
                AddFromCatalogOrIncrease();
                break;
            case PosHotkeyAction.DecreaseQuantity:
                DecreaseOrRemoveLine();
                break;
        }
    }

    /// <summary>«Добавить товар в чек»: товар под рамкой каталога (её двигают стрелками), а если
    /// рамки нет — ещё одна единица последней строки чека (строки идут новыми сверху).</summary>
    private void AddFromCatalogOrIncrease()
    {
        if (ActiveCatalogSurface?.TryAddHighlightedProduct() == true)
            return;

        var basket = _viewModel.Basket;
        var line = basket.Lines.FirstOrDefault();
        if (line is null)
        {
            basket.CartMessage = Tr.T("Выберите товар стрелками в каталоге или отсканируйте штрихкод.",
                "Каталогдон товарды жебелер менен тандаңыз же штрихкодду сканерлеңиз.",
                "Pick a product in the catalog with the arrow keys or scan a barcode.",
                "Katalogda ok tuşlarıyla bir ürün seçin veya barkodu okutun.",
                "Katalogdan mahsulotni strelkalar bilan tanlang yoki shtrix-kodni skanerlang.");
            return;
        }

        if (basket.IncreaseQuantityCommand.CanExecute(line))
            basket.IncreaseQuantityCommand.Execute(line);
    }

    /// <summary>«Убавить / убрать товар»: строка товара под рамкой каталога (самая новая, если
    /// строк у товара несколько), а без рамки — последняя строка чека. Больше одной штуки —
    /// минус одна, одна — строка убирается той же командой, что корзина у строки: с проверкой
    /// права на удаление и кода сотрудника.</summary>
    private void DecreaseOrRemoveLine()
    {
        var basket = _viewModel.Basket;
        var product = ActiveCatalogSurface?.HighlightedProduct;
        var line = product is null
            ? basket.Lines.FirstOrDefault()
            : basket.Lines.FirstOrDefault(l => string.Equals(l.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            if (product is not null)
                basket.CartMessage = Tr.T($"«{product.Title}» нет в чеке.", $"«{product.Title}» чекте жок.",
                    $"“{product.Title}” is not in the receipt.", $"«{product.Title}» fişte yok.", $"«{product.Title}» chekda yo'q.");
            return;
        }

        if (basket.DecreaseQuantityCommand.CanExecute(line))
            basket.DecreaseQuantityCommand.Execute(line);
        else if (basket.RemoveLineCommand.CanExecute(line))
            basket.RemoveLineCommand.Execute(line);
    }

    private void ToggleCustomerDisplay()
    {
        if (_customerDisplay.IsOpen)
        {
            _ = _customerDisplay.CloseAsync(true);
            // 2026-10-21 (пакет 6, п. 56): без модального окна — строкой под чеком.
            _viewModel.Basket.CartMessage = Tr.T("Экран покупателя скрыт.", "Сатып алуучунун экраны жашырылды.", "Customer display hidden.", "Müşteri ekranı gizlendi.", "Xaridor ekrani yashirildi.");
            RestoreScannerFocus();
            return;
        }

        var result = _customerDisplay.TestSecondaryScreen(this);
        // 2026-10-21 (пакет 6, п. 56): успех — строкой под чеком, без модального окна.
        if (result.IsSuccess)
            _viewModel.Basket.CartMessage = result.Message;
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
        // 2026-10-20, живая проверка («после „Фильтр“ сканер ломается до конца сеанса»): окно было
        // нефокусируемым, Focus() молча не срабатывал, а ClearFocus() в Avalonia 11 возвращает фокус на
        // элемент, запомненный окном (нажатую кнопку), — и при каждой активации после модального окна
        // окно снова ставило фокус на «Фильтр». Теперь окно фокусируемое (Focusable в MainWindow.axaml),
        // Focus() запоминает само окно. Пока активно другое окно кассы (диалог), фокус у него не отнимаем:
        // FocusManager в Avalonia один на всё приложение.
        if (!IsActive && IsAnotherAppWindowActive())
            return;
        if (!Focus())
        {
            FocusManager?.ClearFocus();
            Focus();
        }
    }

    private bool IsAnotherAppWindowActive() =>
        Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
        && desktop.Windows.Any(w => !ReferenceEquals(w, this) && w.IsActive);

    /// <summary>2026-10-20, живая проверка: Enter, которым сканер заканчивает код, раньше доходил до
    /// элемента с фокусом (кнопка, плитка, вкладка) и нажимал его — товар не добавлялся. Если в буфере
    /// сканера уже готовый код, Enter забирает сканер раньше всех (Tunnel). Обычный Enter кассира
    /// (буфер пуст или набран медленно) идёт дальше как раньше — «Оплата» и т.д. В текстовом поле — не трогаем.</summary>
    private void OnScanEnterTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        var focused = FocusManager?.GetFocusedElement();
        // 2026-10-20 (живой аудит кассы, К4): курсор в поиске каталога — скан раньше уходил туда текстом, Enter ничего не
        // добавлял. Клавиши поиска передаём и сканеру (символы остаются в поле — ручной набор как раньше); код,
        // набранный со скоростью сканера и законченный Enter, уходит в чек, поиск очищается.
        if (focused is TextBox { Name: "ProductSearchBox" } productSearch)
        {
            FeedScannerFromProductSearch(productSearch, e);
            return;
        }
        if (focused is TextBox)
            return;

        // 2026-10-20, живая проверка («Табличная»: открыто меню «Ещё» → скан → открылись «Отложенные корзины»):
        // в открытом всплывающем меню цифры сканера забирало меню, а Enter нажимал пункт меню. Пока фокус во
        // всплывающем окне кассы (меню, выпадашка), клавиши сначала идут сканеру; готовый код закрывает меню и
        // уходит в чек, как обычный скан. Обычные стрелки/Enter/Esc кассира меню получает как раньше.
        // 2026-10-21: на Android свой TopLevel (Shell\TopLevel.cs) и меню без PopupRoot — блок не собирался (CS8121).
#if !NURANDROID
        if (focused is Visual visual && TopLevel.GetTopLevel(visual) is PopupRoot popupRoot && ReferenceEquals(popupRoot.ParentTopLevel, this))
        {
            OnPopupScanKey(popupRoot, e);
            return;
        }
#endif

        if (e.Key == Key.Enter && _barcodeInputService is AvaloniaKeyboardWedgeBarcodeService { HasCompleteScan: true })
            _barcodeInputService.ProcessKeyDown(e);
    }

    private int _searchFastRun;
    private long _lastSearchKeyTick;
    private ulong? _lastSearchKeyRaw;

    /// <summary>2026-10-20 (живой аудит кассы, К4): клавиши поля поиска каталога — и сканеру. Скан: ≥ 4 знака подряд быстрее
    /// 50 мс (по времени нажатия, как у сканера — AvaloniaKeyboardWedgeBarcodeService) и Enter сразу за ними (до 150 мс) —
    /// так человек не печатает (как в поиске меню ☰ «Новое»). Тогда код — в чек, поле поиска очищается, курсор — сканеру.
    /// Иначе Enter и символы — полю, как раньше (буфер сканера забывается).</summary>
    private void FeedScannerFromProductSearch(TextBox search, KeyEventArgs e)
    {
        if (_barcodeInputService is not AvaloniaKeyboardWedgeBarcodeService wedge)
            return;
        var now = Environment.TickCount64;
        if (e.Key == Key.Enter)
        {
            var fresh = now - _lastSearchKeyTick is >= 0 and < 150;
            if (wedge.ScanJustCompleted)
            {
                e.Handled = true; // второй Enter сканера (CR+LF)
                return;
            }
            if (_searchFastRun >= 3 && fresh && wedge.HasCompleteScan)
            {
                _searchFastRun = 0;
                _scanFromOwnPopup = true; // окно кассы активно, но фокус в поле — код всё равно наш (см. OnBarcodeScanned)
                try
                {
                    wedge.ProcessKeyDown(e); // готовый код → BarcodeScanned → очередь сканов чека
                }
                finally
                {
                    _scanFromOwnPopup = false;
                }
                e.Handled = true;
                search.Text = "";
                PosLogger.Log("Скан в поле поиска каталога: код ушёл в чек, поиск очищен.", "SCAN");
                RestoreScannerFocus();
                return;
            }
            wedge.DiscardBuffer();
            _searchFastRun = 0;
            return;
        }

        var raw = AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
        long delta = raw is { } r && _lastSearchKeyRaw is { } lr && r >= lr ? (long)Math.Min(r - lr, long.MaxValue) : now - _lastSearchKeyTick;
        _searchFastRun = delta is >= 0 and < 50 ? _searchFastRun + 1 : 0;
        _lastSearchKeyTick = now;
        _lastSearchKeyRaw = raw;
        var handled = e.Handled;
        wedge.ProcessKeyDown(e);
        e.Handled = handled; // символ остаётся в поле — набор кассира не страдает
    }

    // Клавиши во всплывающем меню (MenuFlyout «Ещё» раскладок) идут по его собственному дереву и до окна кассы
    // не доходят — ловим их на корне всплывающего окна (один раз на приложение).
    private static bool _popupScanHookInstalled;

    private static void InstallPopupScanHook()
    {
        if (_popupScanHookInstalled)
            return;
        _popupScanHookInstalled = true;
        // 2026-10-21: на Android MainWindow — слой, а не TopLevel Avalonia (CS8121); всплывающих окон PopupRoot там нет.
#if !NURANDROID
        InputElement.KeyDownEvent.AddClassHandler<PopupRoot>((root, e) =>
        {
            if (root.ParentTopLevel is MainWindow main)
                main.OnPopupScanKey(root, e);
        }, RoutingStrategies.Tunnel);
#endif
    }

    private void OnPopupScanKey(PopupRoot popupRoot, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || ReferenceEquals(e, _scanKeySeenInTunnel))
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        var completesScan = e.Key == Key.Enter && _barcodeInputService is AvaloniaKeyboardWedgeBarcodeService { HasCompleteScan: true };
        _scanKeySeenInTunnel = e;
        // Всплывающее окно могло забрать активность у окна кассы — готовый код всё равно наш (см. OnBarcodeScanned).
        _scanFromOwnPopup = completesScan;
        try
        {
            _barcodeInputService.ProcessKeyDown(e);
        }
        finally
        {
            _scanFromOwnPopup = false;
        }
        if (completesScan)
            ClosePopup(popupRoot);
    }

    /// <summary>Меню «Ещё» (MenuFlyout) закрывается своим Close(), остальное — через Popup.</summary>
    private static void ClosePopup(PopupRoot popupRoot)
    {
        if (popupRoot.Content is MenuBase menu)
            menu.Close();
        else
            (popupRoot.Parent as Popup)?.Close();
    }

    private bool _scanFromOwnPopup;

    /// <summary>Клавиша уже передана сканеру в OnScanEnterTunnel (всплывающее меню) — второй раз не передаём.</summary>
    private KeyEventArgs? _scanKeySeenInTunnel;

    private void OnBarcodeScanned(string barcode)
    {
        // IBarcodeInputService is a single shared instance: any window that reads a keystroke
        // (Warehouse, Return, etc.) raises this same event, and MainWindow stays subscribed for
        // its whole lifetime. Without this guard, scanning while another window has focus also
        // added the item to the cash register cart in the background.
        if (!IsActive && !_scanFromOwnPopup)
            return;

        // В очередь сканов, а не «положить в поле и нажать команду»: пока касса добавляла
        // предыдущий товар, команда была занята и быстрый второй скан терялся (2026-09-26).
        Dispatcher.UIThread.Post(() => _viewModel.Basket.EnqueueBarcode(barcode));
    }

    // 2026-10-27: обработчик голосовых команд (OnVoiceCommandRecognized) и обучение фразам убраны вместе с голосовым управлением — есть ИИ-ассистент.

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
    private DispatcherTimer? _balanceRefreshTimer;

    /// <summary>2026-10-23 (аудит, круг 7): после возврата «Касса: N сом» обновлялась через 20–30 с — запомненный список смен
    /// «после продажи» снят до возврата. Сбрасываем его (как после оплаты долга) и обновляем баланс сразу.</summary>
    private void OnReturnFinished()
    {
        try { App.ShiftApi.NoteSaleRecorded(); } catch { }
        OnCheckoutSucceeded(this, EventArgs.Empty);
    }

    /// <summary>2026-09-25, живой случай: за смену прошло 10 продаж на 112 255 сом, а в шапке и
    /// в меню «Касса: 0.00 сом» — остаток смены спрашивался у сервера только при запуске.
    /// Теперь после оплаты он подтягивается в фоне; несколько продаж подряд дают один запрос
    /// (таймер перезапускается), оплату это не задерживает.</summary>
    private void OnCheckoutSucceeded(object? sender, EventArgs e)
    {
        // Офлайн-чек мог встать в очередь — значок «В очереди» в шапке обновляем сразу.
        _viewModel.Toolbar.Status.RefreshQueuedCount();
        TrackSaleForCashCheck();

        // 2026-10-04, живой тест: после офлайн-продажи «Касса: N сом» обновлялась только по таймеру, хотя
        // сумма уже считается локально (остаток + внесения/изъятия + чеки очереди). Пересчитываем шапку
        // сразу, без запроса к серверу; остаток с сервера — как раньше, через 2 с, и только если продажа
        // ушла на сервер (в аварии и без связи его не спрашиваем).
        if (_session.IsShiftOpen)
            UpdateShiftBalanceUi();
        if (OfflineModeHelper.SellLocally)
            return;

        if (_balanceRefreshTimer == null)
        {
            _balanceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _balanceRefreshTimer.Tick += async (_, _) =>
            {
                _balanceRefreshTimer.Stop();
                await RefreshShiftBalanceQuietAsync(afterSale: true).ConfigureAwait(true);
            };
        }

        _balanceRefreshTimer.Stop();
        _balanceRefreshTimer.Start();
    }

    /// <summary>2026-10-20 (живой аудит кассы, К1–К3, К6): проведённая на сервере продажа — как её заплатили (итог, наличные
    /// и безнал, получено и сдача, сумма строк до скидки на чек) — в журнал возвратов (SaleReturnLedger): по нему окно
    /// возврата считает долю скидки на чек, потолок «не больше оплаченного» и что выдать, а «История чеков» после
    /// возврата показывает продажу как была. Запись в фоне — оплату не задерживает.</summary>
    private void RememberSaleForReturns(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel.Basket.LastSale is not { SavedOffline: false, SaleId: { Length: > 0 } saleId } sale)
                return;
            var total = (decimal)sale.Total;
            var method = (sale.PaymentMethod ?? "").Trim().ToLowerInvariant();
            decimal.TryParse(sale.CashReceivedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cashPart);
            decimal paidCash = 0m, paidNonCash = 0m;
            switch (method)
            {
                case "cash":
                    paidCash = total;
                    break;
                case "mixed":
                    paidCash = Math.Min(Math.Max(0m, cashPart), total);
                    paidNonCash = total - paidCash;
                    break;
                case "debt":
                    // 2026-10-21 (ревью денег, п. 1): предоплата долга безналом (cash_received несёт её же целиком) —
                    // в безнал, иначе возврат такого чека велел выдать её наличными из ящика.
                    var prepaid = Math.Min(Math.Max(0m, cashPart), total);
                    decimal.TryParse(sale.NonCashReceivedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nonCashPart);
                    paidNonCash = Math.Min(Math.Max(0m, nonCashPart), prepaid);
                    paidCash = prepaid - paidNonCash;
                    break;
                default:
                    paidNonCash = total;
                    break;
            }

            var linesTotal = 0m;
            DateTime? dueDate = null;
            var schedule = new List<SaleLedgerDuePayment>();
            string? clientName = null, clientPhone = null;
            if (!string.IsNullOrWhiteSpace(sale.CartJson))
            {
                using var doc = JsonDocument.Parse(sale.CartJson);
                var root = doc.RootElement;
                var totals = CartTotalsCalculator.Calculate(root);
                linesTotal = (decimal)Math.Max(0, totals.Subtotal - totals.LineDiscounts);
                // 2026-10-20, владелец (долг Д5): срок и график долга, клиент — из снимка чека (PosCheckoutService), для копии чека.
                static string? S(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S(root, "debt_due_date") is { } dd && DateTime.TryParseExact(dd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var due))
                    dueDate = due;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("debt_schedule", out var ds) && ds.ValueKind == JsonValueKind.Object
                    && ds.TryGetProperty("payments", out var pays) && pays.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pays.EnumerateArray())
                    {
                        if (S(p, "due_date") is { } pd && DateTime.TryParseExact(pd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var pdate)
                            && decimal.TryParse(S(p, "amount"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pam))
                            schedule.Add(new SaleLedgerDuePayment { Date = pdate, Amount = pam });
                    }
                }
                clientName = S(root, "debt_client_name");
                clientPhone = S(root, "debt_client_phone");
            }

            decimal? received = method == "cash" && sale.CashReceived is { } got ? (decimal)got : null;
            decimal? change = method == "cash" && sale.ChangeDue is { } back ? (decimal)back : null;
            var soldAt = new DateTimeOffset(sale.SoldAt);
            _ = Task.Run(() => SaleReturnLedger.RememberPayment(saleId, total, method, paidCash, paidNonCash, received, change, linesTotal, soldAt,
                dueDate, schedule, clientName, clientPhone));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Продажа не записана в журнал возвратов: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-20 (живой аудит кассы, К12): после оплаты долга «Касса» в шапке не менялась до следующей продажи.
    /// Долг изменился (оплата в этой кассе, в программе владельца, на соседней кассе) — остаток смены с сервера, как после продажи.</summary>
    private void OnClientDebtChangedRefreshBalance(string? clientId, string? operationKey) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_session.IsShiftOpen)
                return;
            // 2026-10-23 (живой аудит, Б-2): «Касса» не росла после оплаты долга — OnCheckoutSucceeded берёт запомненный
            // список смен «после продажи» (до 30 с), а он снят ДО оплаты долга. Помечаем, что остаток изменился: спросим сервер заново.
            try { App.ShiftApi.NoteSaleRecorded(); } catch { }
            OnCheckoutSucceeded(this, EventArgs.Empty);
        });

    /// <param name="afterSale">2026-10-04, отчёт о производительности (п. 8): после оплаты список открытых смен
    /// уже спросила проверка смены кассы (ShiftStateService) — остаток берём из её ответа, а не вторым
    /// таким же запросом.</param>
    private async Task RefreshShiftBalanceQuietAsync(bool afterSale = false)
    {
        // 2026-09-29: в аварии сервера не спрашиваем — остаток в шапке считается по кассе, а
        // внесения/изъятия уйдут по событию восстановления связи (OnServerRecovered).
        if (!_session.IsShiftOpen
            || App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous
            || ServerOutageMonitor.IsOutage)
        {
            // 2026-10-04: но шапку пересчитываем — к остатку прибавляются наличные чеков офлайн-очереди
            // (EffectiveShiftCashBalance); раньше «Касса: N сом» без сервера не менялось после продаж.
            if (_session.IsShiftOpen)
                UpdateShiftBalanceUi();
            return;
        }

        // Внесения/изъятия, ещё не записанные на сервер (не было сети), — до запроса остатка:
        // иначе остаток пришёл бы без них. Повтор идёт после каждой продажи.
        await ShiftCashFlowSync.PushPendingAsync(_session.ActiveShiftId).ConfigureAwait(true);

        try
        {
            // 2026-10-04: чеки очереди, которых ещё нет на сервере, — снимок ДО запроса остатка
            // (см. OfflineQueueCash: так отправленные чеки не считаются дважды).
            var queueAtBalance = OfflineQueueCash.Snapshot(_session.ActiveShiftId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var recent = afterSale
                ? await App.ShiftApi.OpenShiftsListAfterSaleAsync(TimeSpan.FromSeconds(30), cts.Token).ConfigureAwait(true)
                : null;
            var list = recent ?? await App.ShiftApi.ConstructionShiftsListAsync(openOnly: true, ct: cts.Token).ConfigureAwait(true);
            if (ShiftBalanceHelper.FindOpenShiftBalance(list, App.PosCashboxId) is not { } balance)
            {
                // 2026-10-04: остатка нет (офлайн-смена) — шапка всё равно учитывает новые чеки очереди.
                UpdateShiftBalanceUi();
                return;
            }

            // 2026-10-22 (аудит 1.17.59, П-А): сверка с реальным движением наличных (предоплата долга безналом).
            CheckSaleCashAgainstServer(_shiftCashBalance, balance);
            _shiftCashBalance = balance;
            _queueCashAtBalance = queueAtBalance;
            _shiftTotals = ShiftBalanceHelper.FindOpenShiftTotals(list, App.PosCashboxId) ?? _shiftTotals;
            UpdateShiftBalanceUi();
        }
        catch (Exception ex) when (!_windowCts.IsCancellationRequested)
        {
            PosLogger.Log($"Остаток смены после оплаты не обновлён: {ex.Message}", "DEBUG");
            // 2026-10-04: без связи — прежний остаток + чеки офлайн-очереди (см. EffectiveShiftCashBalance).
            UpdateShiftBalanceUi();
        }
    }

    // 2026-10-22 (аудит 1.17.59, П-А, Б1): сервер считает в expected_cash безналичную предоплату долга наличными, из-за чего
    // «Касса: …» в шапке росла без денег в ящике. Запоминаем, сколько наличных реально прошло через ящик за проведённые
    // продажи с прошлого запроса остатка, и сколько из них — безнал-предоплата долга (подозрительная сумма).
    private decimal _pendingSaleCashDelta;
    private decimal _pendingSaleSuspect;
    private string? _pendingSaleKey;
    private string? _lastTrackedSaleId;

    private void TrackSaleForCashCheck()
    {
        try
        {
            if (_viewModel.Basket.LastSale is not { SavedOffline: false, SaleId: { Length: > 0 } saleId } sale
                || string.Equals(saleId, _lastTrackedSaleId, StringComparison.OrdinalIgnoreCase))
                return;
            _lastTrackedSaleId = saleId;
            var total = (decimal)sale.Total;
            var method = (sale.PaymentMethod ?? "").Trim().ToLowerInvariant();
            decimal.TryParse(sale.CashReceivedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cashPart);
            decimal.TryParse(sale.NonCashReceivedText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nonCashPart);
            switch (method)
            {
                case "cash":
                    _pendingSaleCashDelta += total;
                    break;
                case "mixed":
                    _pendingSaleCashDelta += Math.Min(Math.Max(0m, cashPart), total);
                    break;
                case "debt":
                    var prepaid = Math.Min(Math.Max(0m, cashPart), total);
                    var nonCash = Math.Min(Math.Max(0m, nonCashPart), prepaid);
                    _pendingSaleCashDelta += prepaid - nonCash;
                    _pendingSaleSuspect += nonCash;
                    break;
            }
            _pendingSaleKey = (_pendingSaleKey is null ? "debtprepay:" : _pendingSaleKey + "+") + saleId;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка наличных продажи не подготовлена: {ex.Message}", "WARNING");
        }
    }

    private void CheckSaleCashAgainstServer(decimal? before, decimal after)
    {
        var key = _pendingSaleKey;
        var suspect = _pendingSaleSuspect;
        var cashDelta = _pendingSaleCashDelta;
        _pendingSaleKey = null;
        _pendingSaleSuspect = 0m;
        _pendingSaleCashDelta = 0m;
        if (key is null || suspect <= 0m || before is not { } prev)
            return;
        ExchangeCashCorrections.RecordIfKnownServerError(_session.ActiveShiftId, key, (after - prev) - cashDelta, suspect, "Предоплата долга безналом");
    }

    /// <summary>2026-10-04: наличные чеков офлайн-очереди, которых не было в остатке смены, когда
    /// его получили (см. OfflineQueueCash) — чтобы отправленные потом чеки не считались дважды.</summary>
    private OfflineQueueCashSnapshot _queueCashAtBalance = OfflineQueueCashSnapshot.Empty;

    /// <summary>2026-10-04, ТЗ P0-2: наличные в кассе для проверки изъятия (CashWithdrawalGuard) — как в шапке.</summary>
    internal decimal? CurrentDrawerCash => EffectiveShiftCashBalance;

    private decimal? EffectiveShiftCashBalance
    {
        get
        {
            if (!_session.IsShiftOpen)
                return _shiftCashBalance;

            var net = ShiftCashOperationsStore.NetForShift(_session.ActiveShiftId);
            // 2026-10-04, живой тест 04.10: без сервера шапка стояла на 10181.50, хотя прошла продажа
            // 42.50 наличными, — остаток смены знает только проведённые на сервере чеки. Прибавляем
            // наличные офлайн-очереди этой смены, которых в остатке ещё нет.
            var queued = OfflineQueueCash.NotInServerBalance(_queueCashAtBalance, _session.ActiveShiftId);
            if (_shiftCashBalance is null && net == 0m && queued == 0m)
                return null;

            return (_shiftCashBalance ?? 0m) + net + queued;
        }
    }

    /// <summary>2026-10-18, аудит доступов: открыть/закрыть смену — доступ «Смены».</summary>
    private bool AuthorizeShifts()
    {
        if (_permissions.HasPermission(PosPermissions.ViewShifts))
            return true;
        PosLogger.Log($"Permission denied: {PosPermissions.ViewShifts}", "WARNING");
        _prompts.ShowWarning(PermissionService.CashierWorkDeniedMessage(PosPermissions.ViewShifts));
        return false;
    }

    /// <summary>2026-10-18, ревью безопасности: выход с «Закрыть смену», а доступа «Смены» нет — явно сказать,
    /// что смена ОСТАЛАСЬ открытой (выход при этом продолжается, как при «Выйти без закрытия»). true — доступа нет, смену не закрываем.</summary>
    private bool WarnShiftLeftOpenIfDenied()
    {
        if (!_session.IsShiftOpen || _permissions.HasPermission(PosPermissions.ViewShifts))
            return false;
        _prompts.ShowWarning(Tr.T(
            "Смена осталась ОТКРЫТОЙ: у вас нет доступа «Смены». Закрыть её может владелец или сотрудник с этим доступом.",
            "Смена АЧЫК бойдон калды: сизде «Сменалар» укугу жок. Аны ээси же ушул укугу бар кызматкер жаба алат.",
            "The shift is still OPEN: you don't have the “Shifts” permission. The owner or an employee with this permission can close it.",
            "Vardiya AÇIK kaldı: «Vardiyalar» erişiminiz yok. Sahibi veya bu erişime sahip bir personel kapatabilir.",
            "Smena OCHIQ qoldi: sizda «Smenalar» huquqi yo'q. Uni egasi yoki shu huquqqa ega xodim yopishi mumkin."));
        return true;
    }

    internal async Task OpenShiftAsync()
    {
        if (!_session.IsShiftOpen && !AuthorizeShifts())
            return;

        if (_session.IsShiftOpen)
        {
            // Вторая смена поверх незакрытой осиротила бы продажи прежней смены.
            _prompts.ShowWarning(Tr.T("Смена уже открыта. Закройте текущую смену перед открытием новой.", "Смена мурунтан эле ачык. Жаңысын ачуудан мурун учурдагы сменаны жабыңыз.", "A shift is already open. Close the current shift before opening a new one.", "Vardiya zaten açık. Yeni bir vardiya açmadan önce mevcut vardiyayı kapatın.", "Smena allaqachon ochiq. Yangisini ochishdan oldin joriy smenani yoping."));
            return;
        }

        var dlg = App.GetRequiredService<OpenShiftDialog>();
        dlg.SuggestedBalance = EffectiveShiftCashBalance;
        // 2026-10-23 (аудит, круг 5): остатка нет в памяти — диалог покажет «загрузка…» и подтянет его в фоне (до 20 с, 2 попытки).
        dlg.LoadBalanceAsync = async () =>
        {
            // 2026-10-23 (аудит, круг 6): смена закрыта — открытых смен на сервере нет, остаток берём из closing_cash последней
            // закрытой смены кассы (один запрос, кэш в ShiftApiService); локальное значение — запасное без связи.
            if (!_session.IsShiftOpen && !ServerOutageMonitor.IsOutage
                && !App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    if (await App.ShiftApi.LastClosedShiftCashAsync(App.PosCashboxId, cts.Token).ConfigureAwait(true) is { } closed)
                        return closed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || cts.IsCancellationRequested)
                {
                    PosLogger.Log($"Остаток закрытой смены не получен: {ex.Message}", "SHIFT");
                }

                return EffectiveShiftCashBalance ?? OfflinePosStateStore.ReadShiftCashBalance();
            }

            await RefreshShiftStateAsync(CancellationToken.None, balanceTimeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            return EffectiveShiftCashBalance;
        };
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true)
            return;

        await ApplyShiftOpenedAsync(dlg.OpeningCash).ConfigureAwait(true);
    }

    internal Task CloseShiftAsync() => CloseShiftAsync(confirmUnfinishedReceipt: true);

    /// <param name="allowWithoutShiftsRight">2026-10-18, решение владельца: true — только из «Сменить кассира»: кассиру без
    /// доступа «Смены» разрешено передать кассу (закрыть смену при передаче). Обычное закрытие — с проверкой доступа.</param>
    private async Task CloseShiftAsync(bool confirmUnfinishedReceipt, bool allowWithoutShiftsRight = false)
    {
        if (!_session.IsShiftOpen)
            return;

        if (!allowWithoutShiftsRight && !AuthorizeShifts())
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

        await ApplyShiftClosedAsync(dlg.ClosingCash, allowWithoutShiftsRight).ConfigureAwait(true);
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
            // Смена закрывается по серверным итогам — незаписанное внесение/изъятие отправляем до них.
            await ShiftCashFlowSync.PushPendingAsync(_session.ActiveShiftId).ConfigureAwait(true);
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
    /// <summary>Экран покупателя включён, а задний экран у Windows в режиме «Дублировать» —
    /// предлагаем один раз за запуск переключить в «Расширить». После переключения Windows сообщит
    /// о новом экране, и OnCashierScreensChanged откроет экран покупателя сам.</summary>
    private async Task OfferExtendClonedDisplayAsync()
    {
        if (_extendDisplayOffered || !AvaloniaCustomerDisplayService.IsWindowsDisplayCloned())
            return;
        _extendDisplayOffered = true;

        var ok = await _prompts.ConfirmAsync(
            Tr.T("Задний экран сейчас повторяет экран кассы, поэтому покупатель не видит цену.\n\n" +
            "Переключить экраны Windows в режим «Расширить», чтобы на заднем экране показывался экран покупателя?", "Арткы экран азыр кассанын экранын кайталап жатат, ошондуктан сатып алуучу бааны көрбөйт.\n\nАрткы экранда сатып алуучунун экраны көрүнүшү үчүн Windows экрандарын «Расширить» режимине которосузбу?", "The rear screen is currently mirroring the till screen, so the customer can't see the price.\n\nSwitch Windows displays to “Extend” mode so the customer display appears on the rear screen?", "Arka ekran şu anda kasa ekranını yansıtıyor, bu yüzden müşteri fiyatı göremiyor.\n\nArka ekranda müşteri ekranının görünmesi için Windows ekranları «Genişlet» moduna geçirilsin mi?", "Orqa ekran hozir kassa ekranini takrorlayapti, shuning uchun xaridor narxni ko'rmaydi.\n\nOrqa ekranda xaridor ekrani ko'rinishi uchun Windows ekranlari «Расширить» rejimiga o'tkazilsinmi?"),
            // 2026-10-22 (модалки, пакет 1): глаголы вместо «Продолжить» / «Отмена».
            Tr.T("Расширить экраны", "Экрандарды кеңейтүү", "Extend displays", "Ekranları genişlet", "Ekranlarni kengaytirish"),
            Tr.T("Не сейчас", "Азыр эмес", "Not now", "Şimdi değil", "Hozir emas"))
            .ConfigureAwait(true);
        if (ok)
            AvaloniaCustomerDisplayService.TryExtendWindowsDisplays();
    }

    private bool _extendDisplayOffered;

    private async Task<bool> ConfirmDiscardUnfinishedReceiptAsync()
    {
        var cart = ResolveCartService();
        if (!cart.HasCart || cart.LineCount <= 0)
            return true;

        return await _prompts
            .ConfirmAsync(Tr.T("В текущем чеке есть незавершённые позиции. Закрыть смену и потерять чек?", "Учурдагы чекте аягына чыкпаган позициялар бар. Сменаны жаап, чекти жоготосузбу?", "The current receipt has unfinished items. Close the shift and lose the receipt?", "Mevcut fişte tamamlanmamış kalemler var. Vardiyayı kapatıp fişi kaybetmek istiyor musunuz?", "Joriy chekda yakunlanmagan pozitsiyalar bor. Smenani yopib, chekni yo'qotasizmi?"),
            // 2026-10-22 (модалки, пакет 1): чек пропадёт — красная «Закрыть смену», рядом «Отмена».
            Tr.T("Закрыть смену", "Сменаны жабуу", "Close shift", "Vardiyayı kapat", "Smenani yopish"),
            danger: true)
            .ConfigureAwait(true);
    }

    internal void NavigateWarehouse()
    {
        // 2026-10-01: «Склад» — флаг «Склад» сайта (can_view_products), а не «Закупки».
        if (OwnerSectionAvailable() && Authorize(PosPermissions.ViewProducts))
            ShowModuleWindow<WarehouseWindow>();
    }

    /// <summary>Раздел владельца из кассы (2026-09-26, разделение программ): при входе через NurCRM
    /// он в программе «NurMarket Владелец». Пункты меню скрыты, но в раздел ведут и другие пути
    /// (например, «Добавить товар» при пустом каталоге) — там кассир получает подсказку.</summary>
    private bool OwnerSectionAvailable()
    {
        if (NurMarketKassa.Services.AppMode.ShowOwnerSectionsInKassa)
            return true;

        _viewModel.CloseSideMenu();
        _prompts.ShowToast(Tr.T(
            "Этот раздел — в программе «NurMarket Владелец».",
            "Бул бөлүм «NurMarket Ээси» программасында.",
            "This section is in the “NurMarket Owner” program.",
            "Bu bölüm «NurMarket İşletme Sahibi» programında yer alır.",
            "Bu bo'lim «NurMarket Egasi» dasturida."), isWarning: true);
        return false;
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

    internal void NavigateReturn() => NavigateReturn(false);

    // 2026-10-22 (аудит новой сборки, П1): перед возвратом — сверка смены с сервером; смену закрыл сервер — понятный текст,
    // окно возврата не открывается (чек в корзине остаётся).
    private void NavigateReturn(bool shiftChecked)
    {
        if (!Authorize(PosPermissions.EmployeeReturn))
            return;
        _viewModel.CloseSideMenu();
        if (!_session.IsShiftOpen && ServerShiftClosedState.Current is { } closedByServer)
        {
            _viewModel.Basket.CartMessage = ServerShiftTexts.Block(closedByServer);
            return;
        }
        if (_session.IsShiftOpen && !shiftChecked)
        {
            _ = ReturnAfterShiftCheckAsync();
            return;
        }
        // 2026-09-29: авария сервера — строкой под чеком, без окна ошибки (см. таблицу у CheckoutAsync).
        if (OfflineModeHelper.IsServerOutage)
        {
            _viewModel.Basket.CartMessage = OfflineModeHelper.ReturnUnavailableInOutage;
            PosLogger.Log("Возврат не открыт: сервер NurCRM не отвечает.", "OUTAGE");
            return;
        }
        var dlg = App.GetRequiredService<ReturnSaleDialog>();
        // 2026-10-21 (пакет 6, п. 35): касса не на весь экран — окно возврата на её месте и её размера.
        dlg.FitToOwnerWindow(this);
#if NURANDROID
        // 2026-10-04, Android-касса: без вложенного цикла — остаток смены обновляем после закрытия окна возврата.
        _ = PosDialogHost.ShowAsync(dlg, this).ContinueWith(
            _ => OnReturnFinished(),
            TaskScheduler.FromCurrentSynchronizationContext());
#else
        PosDialogHost.Show(dlg, this);
        // Возврат меняет остаток смены так же, как продажа — подтягиваем его с сервера (2026-09-25:
        // после возврата в шапке оставалась сумма до возврата).
        OnReturnFinished();
#endif
    }

    private async Task ReturnAfterShiftCheckAsync()
    {
        await CheckShiftOnServerAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        NavigateReturn(true);
    }

    /// <summary>«История чеков» (2026-09-27): чеки этой кассы и печать копии. Без проверки прав
    /// и тарифа — нужна любому кассиру; «Вернуть…» внутри окна проверяет право на возврат сам.</summary>
    internal async void NavigateReceiptHistory()
    {
        try
        {
            _viewModel.CloseSideMenu();
            var window = new ReceiptHistoryWindow();
            await PosDialogHost.ShowAsync(window, this).ConfigureAwait(true);

            // Возврат из истории меняет остаток смены так же, как из меню «Возврат».
            if (window.ReturnWasOpened)
                OnReturnFinished();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков не открылась: {ex}", "WARNING");
            _prompts.ShowWarning(Tr.T("Не удалось открыть историю чеков: ", "Чектердин тарыхын ачуу мүмкүн болгон жок: ", "Could not open the receipt history: ", "Fiş geçmişi açılamadı: ", "Cheklar tarixini ochib bo'lmadi: ") + ex.Message);
        }
    }

    internal void NavigateFinance()
    {
        // 2026-10-01: «Финансы» — флаг «Аналитика» сайта (раньше без проверки).
        // 2026-10-18, решение владельца: пункт «Продажи и финансы» (вместо «Финансов» и «Продаж»). Части — по правам и
        // «Разделам меню», как было у каждого пункта: «Продажи» — право «Продажи», «Финансы» — «Аналитика». Обе — окно
        // «Финансов» с вкладкой «Чеки» из «Продаж» (FinanceWindow.Merged.cs); одна — её прежнее окно.
        if (!OwnerSectionAvailable())
            return;
        var sales = _permissions.HasPermission(PosPermissions.ViewSales) && !SectionVisibility.IsHidden("kassa.sales");
        var finance = _permissions.HasPermission(PosPermissions.ViewAnalytics) && !SectionVisibility.IsHidden("kassa.finance");
        if (sales && finance)
        {
            _viewModel.CloseSideMenu();
            App.GetRequiredService<FinanceWindow>().AsSalesAndFinance().Show(this);
        }
        else if (sales)
            ShowModuleWindow<SalesWindow>();
        else if (Authorize(PosPermissions.ViewAnalytics))
            ShowModuleWindow<FinanceWindow>();
    }

    internal void NavigateSalary()
    {
        if (!OwnerSectionAvailable() || !Authorize(PosPermissions.ViewSettings))
            return;
        _viewModel.CloseSideMenu();
        SalaryWindow.Open(this);
    }

    internal void NavigateSales()
    {
        if (OwnerSectionAvailable() && Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<SalesWindow>();
    }

    internal void NavigateAbc()
    {
        if (OwnerSectionAvailable() && Authorize(PosPermissions.ViewAnalytics))
            ShowModuleWindow<AbcAnalysisWindow>();
    }

    internal void NavigateClients()
    {
        // Тариф «Старт» (2026-09-07): раздел «Клиенты» на сайте для магазина скрыт — см. TariffGate.
        // Пункт меню тоже спрятан (SideMenuViewModel.CanViewClients), это вторая линия защиты.
        if (!TariffGate.CanViewClients)
        {
            _prompts.ShowToast(TariffGate.ClientsLockedMessage, isWarning: true);
            return;
        }

        if (OwnerSectionAvailable() && Authorize(PosPermissions.ViewClients))
            ShowModuleWindow<ClientsWindow>();
    }

    internal void NavigatePayDebt()
    {
        // 2026-09-29: авария сервера — строкой под чеком, без окна ошибки (см. таблицу у CheckoutAsync).
        if (OfflineModeHelper.IsServerOutage)
        {
            _viewModel.CloseSideMenu();
            _viewModel.Basket.CartMessage = OfflineModeHelper.DebtPaymentUnavailableInOutage;
            PosLogger.Log("Оплата долга не открыта: сервер NurCRM не отвечает.", "OUTAGE");
            return;
        }

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

    internal void NavigateCrm()
    {
        if (OwnerSectionAvailable())
            ShowModuleWindow<CrmWebViewWindow>();
    }

    internal void NavigateDeferredReceipts() => ShowModuleWindow<IrregularReceiptsWindow>();

    internal void NavigateErrorLogs() => ShowModuleWindow<LogsAndErrorsWindow>();

    internal void NavigateRemoteSupport() => ShowModuleWindow<RemoteSupportWindow>();

    internal void NavigateRestock()
    {
        if (OwnerSectionAvailable())
            ShowModuleWindow<RestockSuggestionsWindow>();
    }

    internal void NavigateKnowledgeBase() => ShowModuleWindow<KnowledgeBaseWindow>();

    private bool Authorize(string permission)
    {
        if (_permissions.HasPermission(permission))
            return true;
        PosLogger.Log($"Permission denied: {permission}", "WARNING");
        _prompts.ShowWarning(Tr.T("Недостаточно прав для выполнения этой операции.", "Бул операцияны аткарууга укук жетишсиз.", "Insufficient permissions to perform this operation.", "Bu işlemi gerçekleştirmek için yetkiniz yok.", "Bu amalni bajarish uchun huquq yetarli emas."));
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
                    // 2026-10-18: без доступа «Смены» — смена остаётся открытой (сказано явно), чек не трогаем.
                    if (!WarnShiftLeftOpenIfDenied())
                    {
                        if (!await ConfirmDiscardUnfinishedReceiptAsync().ConfigureAwait(true))
                            return;

                        await CloseShiftAsync(confirmUnfinishedReceipt: false).ConfigureAwait(true);
                    }
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
        // Сразу после переноса на основной экран, а не в конце метода, как раньше: ниже есть
        // модальное «Касса обновлена» (первый запуск после установки) и ожидания — всё это
        // время касса стояла без подгонки, в родных 100% (на 1024×768 при 125% — в 1,6 раза
        // крупнее экрана).
        RefreshUiScale();
        if (!_appInitialized)
            await InitializeApplicationAsync(null, _windowCts.Token).ConfigureAwait(true);

        // 2026-10-18, аудит доступов: без доступа «Интерфейс кассира» в кассу не пускаем — понятное сообщение
        // и окно входа. Ревью безопасности 18.10 (решение владельца): сохранённый вход НЕ стираем — после
        // включения доступа на сайте касса снова войдёт им (без связи — когда сохранённый вход узнает о доступе).
        if (!_permissions.HasPermission(PosPermissions.ViewCashier))
        {
            PosLogger.Log("Вход в кассу: нет доступа «Интерфейс кассира» — окно входа, сохранённый вход оставлен.", "AUTH");
            _prompts.ShowWarning(PermissionService.CashierWorkDeniedMessage(PosPermissions.ViewCashier));
            await NavigateToLoginAsync(keepSavedLogin: true).ConfigureAwait(true);
            return;
        }

        // Здесь (а не в InitializeApplicationAsync) — окно уже гарантированно видимо и может
        // быть owner'ом диалога, независимо от того, был ли _appInitialized уже true к этому
        // моменту (LoginWindow может вызвать InitializeApplicationAsync до Show() этого окна).
        // Телеграм-бот владельца отвечает на команды, только пока касса включена: своего
        // сервера у программы нет, новые сообщения она спрашивает сама (getUpdates).
        StartTelegramBot();
        // 2026-09-30: бота могут подключить в программе владельца уже при открытой кассе, а команды
        // может держать программа владельца и потом закрыться — раз в минуту пробуем снова
        // (повторный вызов на работающем опросе ничего не делает).
        if (_telegramRetryTimer is null)
        {
            _telegramRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _telegramRetryTimer.Tick += (_, _) => StartTelegramBot();
            _telegramRetryTimer.Start();
        }
        // 2026-10-22 (аудит новой сборки, П1): раз в 90 с сверяем смену с сервером (он мог закрыть её сам в 23:00).
        if (_shiftWatchTimer is null)
        {
            _shiftWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            _shiftWatchTimer.Tick += (_, _) =>
            {
                _ = CheckShiftOnServerAsync();
                _ = RefreshShiftAgeAsync(); // 2026-10-22 (смена 23 ч): тот же таймер, без второго
            };
            _ = RefreshShiftAgeAsync();
            _shiftWatchTimer.Start();
        }
        // 2026-10-01: один раз предложить перенести бота на сервер NurCRM (работает круглые сутки).
        ServerBotOffer.Schedule(this);
        // 2026-10-06, владелец: «новым клиентам при первом запуске спрашивать сферу маркета» — только на новой установке.
        _ = MarketSphereChoiceWindow.MaybeAskAsync(this);

        // 2026-10-27 (приёмка 26.10, Д-5): экран покупателя открываем ДО модального окна «Касса обновлена» (оно держит этот метод,
        // пока его не закроют, и экран покупателя после обновления не появлялся); причина и итог — в журнал (CUSTOMER_DISPLAY).
        // Если окно кассы свёрнуто или не показано, повторяем при первой активации (Activated) — см. сторожа в сервисе.
        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var result = _customerDisplay.OpenForSession(this, "запуск кассы");
            if (result.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
            else
                _ = OfferExtendClonedDisplayAsync();
        }

        if (!_whatsNewShown)
        {
            _whatsNewShown = true;
            ShowWhatsNewIfUpdated();
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

        RefreshBackgroundWallpaper();
        RefreshLayoutMode();
    }

    /// <summary>Раскладка главного экрана кассы (Настройки → Экран → "Макет", см.
    /// UserPreferences.MainLayoutMode) — "standard" (обычная, каталог+корзина) или "onec"
    /// (альтернативная, в стиле 1С "Рабочее место кассира"). Шапка и боковое меню общие для
    /// обеих раскладок и не участвуют в переключении. Вызывается при загрузке окна и заново из
    /// ScreenSettingsView сразу при выборе макета — см. App.ApplyMainLayoutMode.</summary>
    internal void RefreshLayoutMode()
    {
        // 2026-09-28: раскладок стало шесть (см. KassaLayouts). «Классика» и «1С» объявлены в
        // разметке окна, остальные создаются здесь при выборе и живут, пока выбраны.
        var mode = KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode);
        var isOneC = mode == KassaLayouts.OneC;
        var isAlternative = KassaLayouts.IsAlternative(mode);

        if (!isAlternative)
        {
            _altLayout = null;
            _altLayoutId = "";
        }
        else if (_altLayout is null || _altLayoutId != mode)
        {
            _altLayout = KassaLayouts.CreateView(mode);
            _altLayoutId = mode;
            if (_altLayout is not null)
                _altLayout.DataContext = _viewModel;
        }

        if (!ReferenceEquals(AltLayoutHost.Content, _altLayout))
            AltLayoutHost.Content = _altLayout;

        MainContentGrid.IsVisible = !isOneC && !isAlternative;
        OneCLayout.IsVisible = isOneC;
        AltLayoutHost.IsVisible = isAlternative && _altLayout is not null;

        // Раскладку переключили на ходу — фокус мог остаться в спрятанной, возвращаем его сканеру.
        // При первом показе окна (и повторном выборе той же раскладки) фокус не трогаем — как раньше.
        // «1С» ставит фокус в своё поле сканера сама.
        var switched = _shownLayoutMode is not null && _shownLayoutMode != mode;
        _shownLayoutMode = mode;
        if (switched && !isOneC)
            Dispatcher.UIThread.Post(RestoreScannerFocus, DispatcherPriority.Background);

        // 2026-10-04: компактный вид и его масштаб есть только у «Классики» — при смене вида
        // кассы пересчитываем масштаб (см. RefreshUiScale).
        if (switched)
            RefreshUiScale();
    }

    private Control? _altLayout;
    private string _altLayoutId = "";
    private string? _shownLayoutMode;

    /// <summary>Масштаб интерфейса кассы (Настройки → Экран → "Масштаб", 50–200%, см.
    /// UserPreferences.UiScalePercent) — через LayoutTransformControl.LayoutTransform, а НЕ
    /// голый RenderTransform на самом Grid: LayoutTransformControl пересчитывает и раскладку,
    /// и hit-testing под новым масштабом, поэтому клики попадают туда, куда реально указывает
    /// курсор на увеличенном/уменьшённом интерфейсе. Вызывается при загрузке окна и заново из
    /// PosSettingsWindow/ScreenSettingsView сразу при перетаскивании ползунка и после
    /// "Сохранить" — см. MainWindowHostBridge.Window.</summary>
    internal void RefreshUiScale()
    {
        // 2026-10-04, редизайн под маленькие экраны и сенсорные моноблоки: у «Классики» есть
        // компактный вид (класс "compact" у окна — стили в MainWindow, CatalogPanelView и
        // BasketPanelView). Он включается сам, когда обычный вид не помещается на экран, и масштаб
        // тогда считается от компактного размера 1000×660 — на 1024×768 и 1366×768 касса остаётся
        // в 100% вместо 78–84%, кнопки не мельчают. Остальные виды кассы под компактный размер не
        // рассчитаны — у них всё по-прежнему (1280×840, без класса).
        if (KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode) == KassaLayouts.Standard)
        {
            UiScaleHelper.ApplyAdaptive(UiScaleTransform, 1280, 840,
                new UiScaleHelper.CompactLayoutPolicy(CompactDesignWidth, CompactDesignHeight, SetCompactLayout));
        }
        else
        {
            UiScaleHelper.Apply(UiScaleTransform, 1280, 840);
            SetCompactLayout(false);
        }
    }

    /// <summary>2026-10-04: «родной» размер компактного вида «Классики» — помещается на 1024×768
    /// (рабочая область 1024×728 минус запас UiScaleHelper) без уменьшения.</summary>
    private const double CompactDesignWidth = 1000;
    private const double CompactDesignHeight = 660;

    /// <summary>2026-10-23 (аудит, круг 5): в окне 1024 при большом экране "compact" не включался (он — по рабочей области), и
    /// «Касса: N сом» обрезалась. Класс окна "headernarrow" — по ширине шапки: включается при &lt; 1250, выключается при &gt;= 1270 (на стенде полная шапка со статусом целиком помещается с ~1255)
    /// (зазор — чтобы не мигало на границе). Прячет подписи кнопок, «Экран/Тема/Горячие» (они в «⋯») и режим сети.</summary>
    private void UpdateHeaderNarrow()
    {
        var width = KassaHeaderBar.Bounds.Width;
        if (width <= 0)
            return;
        var narrow = Classes.Contains("headernarrow") ? width < 1270 : width < 1250;
        if (Classes.Contains("headernarrow") != narrow)
            Classes.Set("headernarrow", narrow);
    }

    /// <summary>2026-10-04: включает/выключает компактный вид (класс окна "compact").</summary>
    private void SetCompactLayout(bool compact)
    {
        if (Classes.Contains("compact") == compact)
            return;
        Classes.Set("compact", compact);
        PosLogger.Log(compact ? "Компактный вид кассы включён (маленький экран)." : "Обычный вид кассы.", "UI");
    }

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
                    // 2026-10-18: без доступа «Смены» — смена остаётся открытой (сказано явно), чек не трогаем.
                    if (!WarnShiftLeftOpenIfDenied())
                    {
                        if (!await ConfirmDiscardUnfinishedReceiptAsync().ConfigureAwait(true))
                            return;

                        await CloseShiftAsync(confirmUnfinishedReceipt: false).ConfigureAwait(true);
                    }
                }
            }

            // 2026-09-28, владелец: «открыть кассу, закрыть — потом не откроешь просто так».
            // Крестик окна раньше выходил из учётной записи и стирал сохранённый вход — кассира
            // встречал экран входа с паролем. Теперь крестик = «Выйти на рабочий стол»: программа
            // закрывается, вход сохраняется (если была отмечена «Запомнить меня»). Выйти из
            // учётной записи можно отдельно — «Сменить кассира».
            ExitApplication();
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Window close flow canceled.", "DEBUG");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Window close flow failed: {ex}", "ERROR");
            _prompts.ShowError(Tr.T("Не удалось корректно завершить текущую операцию.", "Учурдагы операцияны туура аяктоо мүмкүн болгон жок.", "Could not complete the current operation properly.", "Mevcut işlem düzgün şekilde tamamlanamadı.", "Joriy amalni to'g'ri yakunlab bo'lmadi."));
        }
        finally
        {
            _closeFlowActive = false;
        }
    }

    private TelegramBotPollingService? _telegramBot;
    private DispatcherTimer? _telegramRetryTimer;

    /// <summary>Поднимает опрос команд бота. Вызывается при открытии окна и повторно после
    /// сохранения настроек бота — повторный вызов на уже запущенном опросе безвреден.</summary>
    internal void StartTelegramBot()
    {
        try
        {
            // 2026-09-30, «бот не работает»: бота подключили в программе владельца, а у неё свой файл
            // настроек — касса его не видела и команды никто не слушал. Берём оттуда, если у кассы пусто.
            // 2026-10-01: заказы, подтверждённые покупателем в боте, — в заказы витрины (решение владельца).
            TelegramAiChat.OrderCreator ??= (name, phone, items, comment, token) =>
                App.GetRequiredService<NurMarketKassa.Services.Api.ShowcaseApiService>().CreateBotOrderAsync(name, phone, items, comment, token);
            // 2026-10-02: бот видит размеры и цвета одежды (варианты товара с сервера).
            TelegramAssistant.VariantsLoader ??= (productId, token) => App.CatalogApi.GetProductVariantsAsync(productId, token);
            // 2026-10-02: бот отвечает и про прокат («кто не вернул», «залоги»).
            TelegramAssistant.RentalsLoader ??= (status, token) => App.GetRequiredService<NurMarketKassa.Services.Api.RentalsApi>().ListAsync(status, token);
            // 2026-10-02, владелец: «уведомление о приближении и окончании срока проката — и в боте тоже».
            // Касса шлёт напоминание в Телеграм, даже когда программа владельца закрыта (повтора нет — общий файл).
            RentalDueNotifier.Start(TelegramAssistant.RentalsLoader);
            UserPreferences.AdoptTelegramBotFromOtherApp();
            if (!TelegramBotService.IsConfigured || !UserPreferences.Instance.TelegramCommandsEnabled)
            {
                _telegramBot?.Stop();
                return;
            }

            // 2026-09-28: + новые адреса NurCRM (должники, chat_id клиента, остатки) — BE-03/04/05.
            _telegramBot ??= new TelegramBotPollingService(
                App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.ClientDebtsApiService>());
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
        // 2026-10-18, ревью этапа 6: таймер плашки «Продажа прошла» (40 мс) не должен пережить окно.
        SaleDoneBar.Dismiss();
        ServerOutageMonitor.Recovered -= OnServerRecovered;
        _shiftWatchTimer?.Stop(); // 2026-10-22 (П1)
        _balanceRefreshTimer?.Stop(); // 2026-10-27: разовый таймер обновления остатка не должен пережить окно
        ServerOutageMonitor.QueueFlushed -= OnQueueFlushed;
        MarketSpheres.Changed -= OnMarketSphereChanged;
        Screens.Changed -= OnCashierScreensChanged;
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        PosDataEvents.ClientDebtChanged -= OnClientDebtChangedRefreshBalance;
        _telegramRetryTimer?.Stop();
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

            _customerDisplay.OpenForSession(this, "смена экранов Windows"); // 2026-10-27: итог пишет сервис
            Activate();
        }, DispatcherPriority.Background);
    }

    private void OnSideMenuBackdropPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel.CloseSideMenu();

    /// <summary>2026-09-29: щелчок по уведомлению об аварии сервера закрывает его.</summary>
    private void OnOutageToastPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel.Toolbar.Status.DismissOutageToast();

    /// <summary>2026-09-29: сервер NurCRM снова отвечает — внесения/изъятия, записанные в аварии,
    /// уходят на сервер, остаток смены в шапке обновляется (продажи досылает SyncService).</summary>
    private void OnServerRecovered() =>
        Dispatcher.UIThread.Post(() =>
        {
            // 2026-10-22 (аудит новой сборки, П1): в аварии сервер мог закрыть смену — сверяемся сразу, до запроса остатка.
            _ = CheckShiftOnServerAsync(force: true).ContinueWith(_ => RefreshShiftBalanceQuietAsync(),
                TaskScheduler.FromCurrentSynchronizationContext());
        });

    // 2026-10-22 (аудит новой сборки, П1): сверка смены с сервером. Сервер сам закрыл смену владельца в 23:00:00, а касса
    // показывала «Касса: 2 250,19» и «Закрыть смену». Вызывается: таймер 90 с, перед оплатой, при открытии «Внесения/изъятия»,
    // после возврата связи. Сеть недоступна/ошибка — ничего не меняем. Возвращает true, если смена закрыта сервером.
    private DispatcherTimer? _shiftWatchTimer;
    private DateTime _lastShiftWatchUtc = DateTime.MinValue;
    private int _shiftWatchBusy;

    internal async Task<bool> CheckShiftOnServerAsync(TimeSpan? timeout = null, bool force = false)
    {
        if (!_session.IsShiftOpen || _windowCts.IsCancellationRequested)
            return false;
        if (Interlocked.Exchange(ref _shiftWatchBusy, 1) == 1)
            return false;
        try
        {
            if (!force && DateTime.UtcNow - _lastShiftWatchUtc < TimeSpan.FromSeconds(15))
                return false;
            _lastShiftWatchUtc = DateTime.UtcNow;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
            var info = await _shiftStateService.CheckServerClosedAsync(cts.Token).ConfigureAwait(true);
            if (info is null || !_session.IsShiftOpen
                || !string.Equals(_session.ActiveShiftId, info.ShiftId, StringComparison.OrdinalIgnoreCase))
                return false;
            ApplyShiftClosedByServer(info);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка смены с сервером: {ex.GetType().Name}: {ex.Message}", "SHIFT");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _shiftWatchBusy, 0);
        }
    }

    // 2026-10-22 (смена 23 ч): предупреждение заранее. Сервер закрывает смену старше 24 ч сам (ближайший целый час после
    // opened_at + 24 ч). Время открытия берём один раз на смену (ShiftStateService.GetActiveShiftOpenedAtAsync), дальше
    // уровень считается из памяти. Деньги и смену не трогает.
    private string? _shiftAgeToastShiftId;
    private string? _foreignShiftToastShiftId;

    // 2026-10-23 (аудит, круг 8, В-1): открытая на кассе смена принадлежит другому кассиру — продажи пойдут на неё. Один раз на смену
    // предупреждаем (info, без «Отложить ошибку») и пишем в журнал SHIFT. Деньги и смену не трогает.
    private void WarnIfShiftOfOtherCashier(System.Text.Json.JsonElement shiftsList)
    {
        try
        {
            var (cashierId, cashierName) = ShiftBalanceHelper.FindOpenShiftCashier(shiftsList, App.PosCashboxId);
            var me = NurMarketKassa.PosApp.CurrentUserId;
            if (string.IsNullOrWhiteSpace(cashierId) || string.Equals(cashierId.Trim(), me?.Trim(), StringComparison.OrdinalIgnoreCase))
                return;
            var key = !string.IsNullOrWhiteSpace(_session.ActiveShiftId) ? _session.ActiveShiftId : cashierId;
            if (string.Equals(_foreignShiftToastShiftId, key, StringComparison.OrdinalIgnoreCase))
                return;
            _foreignShiftToastShiftId = key;
            var who = string.IsNullOrWhiteSpace(cashierName) ? cashierId : cashierName!;
            PosLogger.Log($"Открытая смена {key} принадлежит другому кассиру ({who}, id {cashierId}); за кассой id {me}.", "SHIFT");
            _ = PosAlertDialog.ShowAsync(this, Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"),
                Tr.T($"Смена открыта кассиром «{who}». Продажи запишутся на его смену. Если за кассой вы — закройте эту смену (Z) и откройте свою.",
                    $"Смена «{who}» кассири тарабынан ачылган. Сатуулар анын сменасына жазылат. Эгер кассада сиз болсоңуз — бул сменаны (Z) жаап, өзүңүздүкүн ачыңыз.",
                    $"The shift was opened by cashier \"{who}\". Sales will be recorded on their shift. If you are at the till, close this shift (Z) and open your own.",
                    $"Vardiya \"{who}\" kasiyeri tarafından açıldı. Satışlar onun vardiyasına kaydedilecek. Kasada siz iseniz, bu vardiyayı (Z) kapatıp kendi vardiyanızı açın.",
                    $"Smena \"{who}\" kassiri tomonidan ochilgan. Sotuvlar uning smenasiga yoziladi. Kassada siz bo'lsangiz, bu smenani (Z) yoping va o'zingiznikini oching."),
                PosAlertKind.Warning, null, info: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка кассира открытой смены не удалась: {ex.Message}", "SHIFT");
        }
    }
    private ShiftAgeLevel _shiftAgeLevel;
    private string _shiftAgeLine = "";

    private async Task RefreshShiftAgeAsync()
    {
        try
        {
            if (_session.IsShiftOpen && !_windowCts.IsCancellationRequested)
                await _shiftStateService.GetActiveShiftOpenedAtAsync(_windowCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возраст смены: {ex.GetType().Name}: {ex.Message}", "SHIFT");
        }
        ApplyShiftAgeUi();
    }

    /// <summary>Без сети: плашка в шапке, одно уведомление на смену (23 ч), строка под чеком — по запомненному opened_at.</summary>
    private void ApplyShiftAgeUi()
    {
        var level = ShiftAgeLevel.None;
        var line = "";
        var id = _session.ActiveShiftId;
        if (_session.IsShiftOpen && ShiftAgeWarning.Get(id) is { } opened)
        {
            var now = ShiftAgeWarning.Now();
            level = ShiftAgeWarning.LevelFor(opened, now);
            if (level != ShiftAgeLevel.None)
                line = ServerShiftTexts.AgeWarning(opened, now);
        }
        _shiftAgeLevel = level;
        _shiftAgeLine = line;
        var status = _viewModel.Toolbar.Status;
        status.SetShiftAge(line, level == ShiftAgeLevel.Red);
        if (level >= ShiftAgeLevel.Late
            && !string.Equals(_shiftAgeToastShiftId, id, StringComparison.OrdinalIgnoreCase))
        {
            _shiftAgeToastShiftId = id;
            // 2026-10-23: info:true — без кнопки «Отложить ошибку» (это напоминание, не ошибка).
            _ = PosAlertDialog.ShowAsync(this, Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"), line, PosAlertKind.Warning, null, info: true);
        }
    }

    /// <summary>Сервер закрыл смену сам: шапка и кнопки сразу «Смена закрыта сервером в ЧЧ:ММ», «Открыть смену» активна,
    /// «Закрыть смену» скрыта (всё это от IsShiftOpen), корзина НЕ трогается, локальные данные смены (внесения/изъятия
    /// ShiftCashOperationsStore, запасной остаток) остаются. Одно уведомление с итогом — без модального окна.</summary>
    private void ApplyShiftClosedByServer(ShiftClosedByServer info)
    {
        PosLogger.Log($"Сервер закрыл смену {info.ShiftId} в {ServerShiftClosedState.TimeText(info)} (ожидалось {info.ExpectedCash}, закрыто на {info.ClosingCash}, продаж {info.SalesCount}) — касса переведена в «смена не открыта».", "SHIFT");
        ServerShiftClosedState.Current = info;
        NurMarketKassa.PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        NurMarketKassa.App.SyncToSession(_session);
        // Остаток смены на диске остаётся (запасной), меняется только признак открытой смены.
        OfflinePosStateStore.SaveFromApp(OfflinePosStateStore.ReadShiftCashBalance());
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.Catalog.StatusText = ServerShiftTexts.Block(info);
        _prompts.ShowToast(ServerShiftTexts.Summary(info), isWarning: true);
    }

    /// <summary>2026-10-04: очередь дослана после аварии — остаток смены с сервера уже включает эти
    /// чеки; новый запрос остатка заменяет их учёт в шапке (см. OfflineQueueCash).</summary>
    private void OnQueueFlushed(int sent, int left)
    {
        if (sent > 0)
            Dispatcher.UIThread.Post(() => _ = RefreshShiftBalanceQuietAsync());
    }
    private void OnViewModelStateChanged(object? sender, EventArgs e) =>
        ScheduleApplicationStateSave();

    private void OnMarketSphereChanged() =>
        Dispatcher.UIThread.Post(() => ApplyMarketSphereToCatalog(fromSettings: true));

    /// <summary>Сфера «Услуги» (Настройки → Операции): каталог открывается на вкладке «Услуги» —
    /// при запуске и сразу после переключения. Уход из этой сферы возвращает на «Все товары».</summary>
    private void ApplyMarketSphereToCatalog(bool fromSettings)
    {
        var catalog = _viewModel.Catalog;
        if (MarketSpheres.IsServices)
            catalog.SelectedTabIndex = CatalogPanelViewModel.ServicesTabIndex;
        else if (fromSettings && catalog.SelectedTabIndex == CatalogPanelViewModel.ServicesTabIndex)
            catalog.SelectedTabIndex = 0;
    }

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

    private bool _portraitLayout;

    /// <summary>2026-10-04, владелец (Android): «сделай как у других Android-программ», «адаптацию под все
    /// экраны». Экран больше не закреплён горизонтально; на вертикальном (телефон) каталог и чек — друг под
    /// другом на всю ширину, разделитель тянется вверх-вниз. Колонки сетки не трогаются (их ширины — сохранённая
    /// пропорция для горизонтального экрана), каталог и чек только растягиваются на все три колонки.</summary>
    private void ApplyPortraitLayout()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;
        var portrait = h > w * 1.05;
        // 2026-10-04, редизайн: вертикально и невысоко — телефон (и планшет 7"): два экрана «Товары» / «Чек».
        // 2026-10-04, владелец: «раздели каталог и корзину для всех мобильных устройств» — на телефоне и планшете
        // (DeviceForm) два экрана в любом положении. Кассовый терминал — рядом или каталог над чеком, как было.
        var phone = DeviceForm.IsHandheld || (portrait && h < PhoneModeMaxHeight);
        if (portrait == _portraitLayout && phone == _phoneMode)
            return;
        _portraitLayout = portrait;
        _phoneMode = phone;
        Classes.Set("portrait", portrait);
        Classes.Set("phone", phone);
        PhoneModeBar.IsVisible = phone;
        CatalogGridSplitter.IsVisible = !phone;

        if (phone)
        {
            MainContentGrid.RowDefinitions = new RowDefinitions("Auto,*");
            CatalogColumn.MinWidth = 0;
            CartColumn.MinWidth = 0;
            Grid.SetRow(PhoneModeBar, 0);
            Grid.SetColumnSpan(PhoneModeBar, 3);
            Grid.SetRow(CatalogPanel, 1);
            Grid.SetColumn(CatalogPanel, 0);
            Grid.SetColumnSpan(CatalogPanel, 3);
            Grid.SetRow(CartPanelView, 1);
            Grid.SetColumn(CartPanelView, 0);
            Grid.SetColumnSpan(CartPanelView, 3);
            ApplyPhoneScreen();
            PosLogger.Log($"Экран {w:0}×{h:0}: телефон — экраны «Товары» и «Чек».", "UI");
            return;
        }
        ApplyPhoneScreen();

        if (portrait)
        {
            MainContentGrid.RowDefinitions = new RowDefinitions("1.1*,8,*");
            CatalogColumn.MinWidth = 0;
            CartColumn.MinWidth = 0;
        }
        else
        {
            MainContentGrid.RowDefinitions = new RowDefinitions();
            CatalogColumn.MinWidth = this.FindResource("CatalogMinimumWidth") is double catalogMin ? catalogMin : 470;
            CartColumn.MinWidth = this.FindResource("CartMinimumWidth") is double cartMin ? cartMin : 490;
        }

        Grid.SetRow(CatalogPanel, 0);
        Grid.SetColumn(CatalogPanel, 0);
        Grid.SetColumnSpan(CatalogPanel, portrait ? 3 : 1);

        Grid.SetRow(CatalogGridSplitter, portrait ? 1 : 0);
        Grid.SetColumn(CatalogGridSplitter, portrait ? 0 : 1);
        Grid.SetColumnSpan(CatalogGridSplitter, portrait ? 3 : 1);
        CatalogGridSplitter.ResizeDirection = portrait ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        CatalogGridSplitter.Width = portrait ? double.NaN : 8;
        CatalogGridSplitter.MinWidth = portrait ? 0 : 8;
        CatalogGridSplitter.MaxWidth = portrait ? double.PositiveInfinity : 8;
        CatalogGridSplitter.Height = portrait ? 8 : double.NaN;

        Grid.SetRow(CartPanelView, portrait ? 2 : 0);
        Grid.SetColumn(CartPanelView, portrait ? 0 : 2);
        Grid.SetColumnSpan(CartPanelView, portrait ? 3 : 1);
        PosLogger.Log($"Экран {w:0}×{h:0}: {(portrait ? "вертикальная раскладка (каталог над чеком)" : "горизонтальная раскладка")}.", "UI");
    }

    /// <summary>Ниже этой высоты (точки вида) вертикальный экран — телефонный: каталог и чек по очереди.</summary>
    private const double PhoneModeMaxHeight = 1000;

    private bool _phoneMode;
    private bool _phoneShowsCart;
    private bool _phoneBasketWatched;

    /// <summary>2026-10-04, телефон: какой из двух экранов виден. Вне телефонного режима видны оба.</summary>
    private void ApplyPhoneScreen()
    {
        if (_phoneMode && !_phoneBasketWatched)
        {
            // Чек оплачен или очищен — обратно к товарам (как в мобильных кассах).
            _phoneBasketWatched = true;
            _viewModel.Basket.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BasketPanelViewModel.IsEmpty))
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_phoneMode && _phoneShowsCart && _viewModel.Basket.IsEmpty)
                            ShowPhoneScreen(cart: false);
                    });
            };
        }
        var showCart = _phoneMode && _phoneShowsCart;
        // 2026-10-05, «приятный переход»: экран, который появляется, проявляется за 0,15 с.
        if (_phoneMode && showCart && !CartPanelView.IsVisible)
            FadeIn(CartPanelView);
        else if (_phoneMode && !showCart && !CatalogPanel.IsVisible)
            FadeIn(CatalogPanel);
        CatalogPanel.IsVisible = !showCart;
        CartPanelView.IsVisible = !_phoneMode || showCart;
        PhoneCatalogTab.IsChecked = !showCart;
        PhoneCartTab.IsChecked = showCart;
        PhonePayButton.IsVisible = !showCart;
    }

    private static void FadeIn(Control control)
    {
        control.Transitions ??= new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            },
        };
        control.Opacity = 0;
        Dispatcher.UIThread.Post(() => control.Opacity = 1, DispatcherPriority.Background);
    }

    private void ShowPhoneScreen(bool cart)
    {
        _phoneShowsCart = cart;
        ApplyPhoneScreen();
    }

    private void PhoneCatalogTab_Click(object? sender, RoutedEventArgs e) => ShowPhoneScreen(cart: false);

    private void PhoneCartTab_Click(object? sender, RoutedEventArgs e) => ShowPhoneScreen(cart: true);

    private ApplicationState CaptureApplicationState() =>
        new()
        {
            // 2026-10-04: в вертикальной раскладке колонки не видны — сохраняем прежнюю пропорцию, а не ширины.
            CatalogWidth = _portraitLayout ? CatalogColumn.Width.Value : MainContentGrid.ColumnDefinitions[0].ActualWidth,
            CartWidth = _portraitLayout ? CartColumn.Width.Value : MainContentGrid.ColumnDefinitions[2].ActualWidth,
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
        // 2026-10-21 (пакет 6, п. 68): немодальное «Некорректные чеки» закрывалось только мышью — Esc закрывает его, как
        // остальные окна (у тех Esc уже свой: IsCancel или обработчик клавиш).
        if (window is IrregularReceiptsWindow)
            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && !e.Handled)
                {
                    e.Handled = true;
                    window.Close();
                }
            };
        window.Show(this);
    }

    private static bool _accessibleNamesRegistered;

    /// <summary>2026-10-21 (пакет 6, п. 68): у кнопок-значков (☰, вид каталога, обновить, ⋮, шапка…) экранный диктор
    /// читал «Avalonia.Controls.PathIcon» — имени не было. Кнопке без своего имени при показе даётся имя из подсказки
    /// (ToolTip) или из подписи внутри (TextBlock, кроме значков шрифта Segoe MDL2). Заданные имена не трогаются.</summary>
    private static void RegisterAccessibleButtonNames()
    {
        if (_accessibleNamesRegistered)
            return;
        _accessibleNamesRegistered = true;
        Control.LoadedEvent.AddClassHandler<Button>((button, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)) || button.Content is string)
                    return;
                var name = ToolTip.GetTip(button) as string;
                if (string.IsNullOrWhiteSpace(name))
                    name = string.Join(" ", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(button)
                        .OfType<TextBlock>()
                        .Select(t => t.Text?.Trim() ?? "")
                        .Where(t => t.Length > 0 && !t.All(ch => ch is >= '\uE000' and <= '\uF8FF')));
                if (!string.IsNullOrWhiteSpace(name))
                    Avalonia.Automation.AutomationProperties.SetName(button, name.Length > 120 ? name[..120] : name);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Имя кнопки для диктора не задано: {ex.Message}", "UI");
            }
        });
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
                    _session.PosCashboxDisplayName = Tr.T("Локальная касса", "Жергиликтүү касса", "Local till", "Yerel kasa", "Mahalliy kassa");
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
                // 2026-10-04, стенд «сбои сервера»: сервер не отвечает (ServerOutageMonitor) — касса
                // остаётся на своей кассе без запроса списка. Раньше этот запрос (до 6 с) и остаток смены
                // ниже (до 6 с) шли и в аварии: запуск кассы и «Оплатить» без открытой смены ждали сеть
                // до 12 с на каждый раз.
                if (!ServerOutageMonitor.IsOutage)
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
            // Обновление смены могло перевести кассу на кассу найденной смены кассира (см.
            // ShiftStateService.RefreshAsync) — итоги ниже должны смотреть туда же.
            App.PosCashboxId = NurMarketKassa.App.PosCashboxId;

            // 2026-09-12: тот же баг, что уже поправлен в ShiftStateService.RefreshAsync (см. её
            // комментарий) — здесь была ВТОРАЯ, отдельная копия того же неверного условия,
            // которая тут же затирала уже корректно посчитанный ShiftStateService баланс своим
            // собственным, застревающим на 0.00 для обычного (не автономного) аккаунта, который
            // просто открыл смену без сети в моменте.
            if (_session.IsShiftOpen)
            {
                // 2026-10-04: чеки офлайн-очереди, которых нет в остатке, — снимок ДО запроса остатка
                // (см. OfflineQueueCash и EffectiveShiftCashBalance).
                _queueCashAtBalance = OfflineQueueCash.Snapshot(_session.ActiveShiftId);
                if (App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous)
                {
                    _shiftCashBalance = OfflinePosStateStore.ReadShiftCashBalance();
                    // Автономный режим не ходит на сервер — разбивки продаж (как в вебе) взять
                    // неоткуда, диалог закрытия смены покажет только "Остаток по системе".
                    _shiftTotals = null;
                }
                else if (ServerOutageMonitor.IsOutage)
                {
                    // 2026-10-04: сервер не отвечает — остаток по кассе, без ожидания (см. выше);
                    // разбивка остаётся последней полученной.
                    _shiftCashBalance = OfflinePosStateStore.ReadShiftCashBalance();
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
                        WarnIfShiftOfOtherCashier(shiftsList);
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
                _viewModel.Catalog.StatusText = Tr.T("Смена открыта", "Смена ачык", "Shift open", "Vardiya açık", "Smena ochiq");

            // Остались внесения/изъятия, не дошедшие до сервера в прошлый раз (касса была без
            // сети или её закрыли раньше) — дошлём в фоне.
            if (_session.IsShiftOpen && ShiftCashOperationsStore.PendingForShift(_session.ActiveShiftId).Count > 0)
                _ = RefreshShiftBalanceQuietAsync();
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
        // 2026-10-18: и из окна «Операции с кассой» — тоже только с доступом «Смены».
        if (!AuthorizeShifts())
            return;
        NurMarketKassa.App.SyncFromSession(_session);
        var result = await _cashShiftService.OpenShiftAsync(openingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? Tr.T("Не удалось открыть смену.", "Сменаны ачуу мүмкүн болгон жок.", "Could not open the shift.", "Vardiya açılamadı.", "Smenani ochib bo'lmadi."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.InfoMessage))
            _prompts.ShowToast(result.InfoMessage);

        NurMarketKassa.App.SyncToSession(_session);
        App.PosCashboxId = NurMarketKassa.App.PosCashboxId;

        _shiftCashBalance = result.Balance ?? openingCash;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        // 2026-10-17: текст меню уже поставил UpdateShiftBalanceUi (он же скрывает сумму при слепом пересчёте).
        // 2026-10-20 (живой аудит кассы, К18): «Остаток: 0,00» под поиском не менялся после продаж и путал с «Касса» в шапке —
        // теперь это явно начальная сумма и время открытия (текущая сумма — в шапке).
        var openedText = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var openingText = MoneyText.Som(openingCash);
        _viewModel.Catalog.StatusText = Tr.T($"Смена открыта в {openedText}, начальная сумма {openingText}.", $"Смена {openedText} ачылды, баштапкы сумма {openingText}.",
            $"Shift opened at {openedText}, opening cash {openingText}.", $"Vardiya {openedText} açıldı, başlangıç tutarı {openingText}.",
            $"Smena {openedText} da ochildi, boshlang'ich summa {openingText}.");
        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var displayResult = _customerDisplay.OpenForSession(this, "открытие смены");
            if (displayResult.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
        }
    }

    private async Task ApplyShiftClosedAsync(decimal? closingCash, bool allowWithoutShiftsRight = false)
    {
        if (!_session.IsShiftOpen || (!allowWithoutShiftsRight && !AuthorizeShifts()))
            return;

        NurMarketKassa.App.SyncFromSession(_session);
        // Захватываем ДО закрытия — CloseShiftAsync при успехе обнуляет ActiveShiftId, а
        // _shiftTotals понадобится ниже для отчёта (см. ShowShiftClosedReport).
        var shiftIdBeforeClose = NurMarketKassa.PosApp.ActiveShiftId;
        var totalsBeforeClose = _shiftTotals;
        var result = await _cashShiftService.CloseShiftAsync(closingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? Tr.T("Не удалось закрыть смену.", "Сменаны жабуу мүмкүн болгон жок.", "Could not close the shift.", "Vardiya kapatılamadı.", "Smenani yopib bo'lmadi."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.InfoMessage))
            _prompts.ShowToast(result.InfoMessage);

        _viewModel.Basket.ClearAfterShiftClose(shiftIdBeforeClose); // 2026-10-17, этап 4: смена — для журнала удалений
        NurMarketKassa.App.SyncToSession(_session);

        _shiftCashBalance = result.Balance ?? closingCash ?? 0m;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = Tr.T("Смена не открыта", "Смена ачылган эмес", "Shift not open", "Vardiya açık değil", "Smena ochilmagan");
        _viewModel.Catalog.StatusText = Tr.T("Смена закрыта.", "Смена жабылды.", "Shift closed.", "Vardiya kapatıldı.", "Smena yopildi.");
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
                // Раньше не заполнялось — в отчёте закрытия стояло «Открыта: —» (стресс-тест 2026-09-26).
                OpenedAt = before?.OpenedAt,
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
            // Отчёт собран из ответа самого закрытия — сверять его с сервером незачем.
            var dialog = new ShiftDetailsDialog(shift) { RefreshFromServer = false };
            if (fresh?.PendingDebtSales is { } pendingDebt)
            {
                // 2026-09-27: отчёт больше не ждёт расчёта долга — открывается сразу, долг
                // подставляется, когда посчитается (итог тот же: посчитанный, иначе из снимка
                // «до закрытия»). Сводка в Telegram уходит после этого — с долгом, как раньше.
                var debtApplied = dialog.ApplyDebtWhenResolvedAsync(pendingDebt, before?.DebtSales);
                _ = SendShiftSummaryWhenDebtKnownAsync(debtApplied, shift, shiftId);
            }
            else
            {
                SendShiftSummaryToTelegram(shift, shiftId);
            }
            PosDialogHost.Show(dialog, this);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShowShiftClosedReport failed: {ex.Message}", "SHIFT");
        }
    }

    private bool _xReportOpening;

    /// <summary>2026-10-20 (живой аудит кассы, К16): «X-отчёт» из меню ☰ — текущие цифры открытой смены без её закрытия (то же
    /// окно «Просмотр смены», что после закрытия: продажи, наличные/безнал, возвраты, оплаты долга, внесения/изъятия).</summary>
    internal async void ShowXReport()
    {
        if (_xReportOpening)
            return;
        _viewModel.CloseSideMenu();
        var shiftId = _session.ActiveShiftId;
        if (!_session.IsShiftOpen || string.IsNullOrWhiteSpace(shiftId))
        {
            _prompts.ShowToast(Tr.T("Смена не открыта — X-отчёта нет.", "Смена ачылган эмес — X-отчёт жок.", "The shift is not open — no X-report.",
                "Vardiya açık değil — X raporu yok.", "Smena ochilmagan — X-hisobot yo'q."), isWarning: true);
            return;
        }

        _xReportOpening = true;
        try
        {
            ShiftModel? shift = null;
            if (!ServerOutageMonitor.IsOutage && !shiftId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    if (await ShiftHistoryService.LoadOneAsync(shiftId, cts.Token).ConfigureAwait(true) is { } entry)
                        shift = ShiftModel.FromEntry(entry);
                }
                catch (Exception ex) when (!_windowCts.IsCancellationRequested)
                {
                    PosLogger.Log($"X-отчёт: смена с сервера не получена, показываю цифры кассы: {ex.Message}", "SHIFT");
                }
            }

            var totals = _shiftTotals;
            shift ??= new ShiftModel
            {
                Id = shiftId,
                ShiftNumber = shiftId,
                OpenedAt = totals?.OpenedAt,
                Cashier = NurMarketKassa.PosApp.CurrentUserDisplayName ?? "—",
                Status = "Открыта",
                Revenue = totals?.TotalSales ?? 0m,
                OpeningCash = totals?.OpeningCash,
                CashSales = totals?.CashSales,
                NonCashSales = totals?.NonCashSales,
                DebtSales = totals?.DebtSales,
                SalesCount = totals?.SalesCount,
                ExpenseTotal = totals?.ExpenseTotal,
                IncomeTotal = totals?.IncomeTotal,
            };
            var dialog = new ShiftDetailsDialog(shift)
            {
                Title = Tr.T("X-отчёт (смена не закрывается)", "X-отчёт (смена жабылбайт)", "X-report (the shift stays open)",
                    "X raporu (vardiya kapanmaz)", "X-hisobot (smena yopilmaydi)"),
            };
            PosLogger.Log($"X-отчёт открыт: смена {shiftId}.", "SHIFT");
            await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"X-отчёт не открылся: {ex}", "WARNING");
        }
        finally
        {
            _xReportOpening = false;
            RestoreScannerFocus();
        }
    }

    private static async Task SendShiftSummaryWhenDebtKnownAsync(Task debtApplied, ShiftModel shift, string? shiftId)
    {
        try
        {
            await debtApplied.ConfigureAwait(true);
            SendShiftSummaryToTelegram(shift, shiftId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка смены в Telegram не отправлена: {ex.Message}", "SHIFT");
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

        _ = Task.Run(async () =>
        {
            // 2026-09-28 (BE-10): цифры сводки — из отчёта смены сервера (смешанная раздельно в
            // наличных/безнале, внесения и изъятия с любого компьютера). Нет сервера — как раньше.
            var summaryShift = shift;
            var (summaryDeposits, summaryWithdrawals) = (deposits, withdrawals);
            try
            {
                if (await NurMarketKassa.Services.Api.NurCrmReportsApi.GetShiftReportAsync(shiftId).ConfigureAwait(false) is { } report)
                {
                    summaryShift = report.ApplyTo(shift);
                    (summaryDeposits, summaryWithdrawals) = (report.Deposits, report.Withdrawals);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Сводка смены: отчёт сервера не получен: {ex.Message}", "TELEGRAM");
            }

            // 2026-10-01, ТЗ часть 5: бот на сервере NurCRM сам шлёт сводку по закрытию смены —
            // касса не дублирует (иначе владелец получал бы две).
            if (NurMarketKassa.Services.Api.ServerTelegramBotApi.LastKnownServerMode == true)
            {
                PosLogger.Log("Сводка смены: её отправляет бот на сервере NurCRM.", "TELEGRAM");
                return;
            }

            var text = TelegramBotService.BuildShiftSummary(summaryShift, summaryDeposits, summaryWithdrawals, prefs.StoreName);
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
        // Событие приходит из потока оплаты, а ниже — окно и тулбар.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnShiftDesyncDetected(sender, e));
            return;
        }

        if (!_session.IsShiftOpen)
            return;

        PosLogger.Log("Shift desync detected after failed payment — resetting local shift state.", "PAYMENT");

        var rejectedShiftId = NurMarketKassa.PosApp.ActiveShiftId;
        NurMarketKassa.PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        NurMarketKassa.App.SyncToSession(_session);
        // 2026-09-26: и на диске — иначе при медленном ответе сервера ShiftStateService.RefreshAsync
        // откатывается на сохранённое состояние и возвращает ту же закрытую смену.
        OfflinePosStateStore.SaveFromApp(OfflinePosStateStore.ReadShiftCashBalance());

        _shiftCashBalance = 0m;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = Tr.T("Смена не открыта", "Смена ачылган эмес", "Shift not open", "Vardiya açık değil", "Smena ochilmagan");
        _viewModel.Catalog.StatusText = Tr.T("Смена закрыта на сервере — откройте смену заново.", "Смена серверде жабылган — сменаны кайра ачыңыз.", "The shift was closed on the server — open the shift again.", "Vardiya sunucuda kapatıldı — vardiyayı yeniden açın.", "Smena serverda yopilgan — smenani qaytadan oching.");
        _customerDisplay.CloseForSession();

        _ = ResyncShiftAfterDesyncAsync(rejectedShiftId);
    }

    /// <summary>Сразу после отказа сервера сверяемся с ним: касса могла держать устаревший номер,
    /// а своя смена открыта — тогда следующее «Оплатить» пройдёт. Если своей смены нет, её
    /// предложит открыть EnsureShiftBeforePaymentAsync.</summary>
    private async Task ResyncShiftAfterDesyncAsync(string? rejectedShiftId)
    {
        try
        {
            await RefreshShiftStateAsync(_windowCts.Token).ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                PosLogger.Log("Сверка после отказа: своей открытой смены на сервере нет — при оплате касса предложит открыть смену.", "SHIFT");
            else if (string.Equals(_session.ActiveShiftId, rejectedShiftId, StringComparison.OrdinalIgnoreCase))
                PosLogger.Log($"Сверка после отказа: сервер отказал в продаже по смене {rejectedShiftId}, но в списке смен она открыта (касса {App.PosCashboxId}).", "SHIFT");
            else
                PosLogger.Log($"Сверка после отказа: найдена своя открытая смена {_session.ActiveShiftId} — касса перешла на неё.", "SHIFT");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка смены после отказа сервера не удалась: {ex.Message}", "SHIFT");
        }
    }

    /// <summary>См. BasketPanelViewModel.EnsureShiftBeforePayment: без смены сначала сверка с
    /// сервером (своя смена могла открыться на сайте или остаться с прошлого запуска), а если
    /// своей смены нет — окно «Открыть смену», как при добавлении первого товара.</summary>
    private async Task<bool> EnsureShiftBeforePaymentAsync()
    {
        // 2026-10-22 (аудит новой сборки, П1): смена «открыта», но сервер мог закрыть её сам — быстрая сверка (до 4 с,
        // не чаще раза в 15 с) перед оплатой; чек в корзине при этом не трогаем.
        if (_session.IsShiftOpen)
            await CheckShiftOnServerAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (_session.IsShiftOpen)
        {
            // 2026-10-22 (смена 23 ч): 23 ч и больше — строка под чеком при каждой оплате (оплату не блокирует).
            ApplyShiftAgeUi();
            if (_shiftAgeLevel >= ShiftAgeLevel.Late && _shiftAgeLine.Length > 0)
                _viewModel.Basket.CartMessage = _shiftAgeLine;
            return true;
        }
        if (ServerShiftClosedState.Current is { } closedByServer)
            _viewModel.Basket.CartMessage = ServerShiftTexts.Block(closedByServer);

        PosLogger.Log("PAY: смена не открыта — сверка с сервером перед оплатой.", "SHIFT");
        try
        {
            await RefreshShiftStateAsync(_windowCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (_session.IsShiftOpen)
            return true;

        await OpenShiftAsync().ConfigureAwait(true);
        return _session.IsShiftOpen;
    }

    private void UpdateShiftBalanceUi()
    {
        // 2026-10-22 (аудит новой сборки, П1): открыта смена — «закрыта сервером» больше не актуально.
        if (_session.IsShiftOpen)
            ServerShiftClosedState.Clear();
        var balance = EffectiveShiftCashBalance;
        var balanceText = _session.IsShiftOpen
            ? Tr.T($"Касса: {ShiftBalanceHelper.FormatBalance(balance)}", $"Касса: {ShiftBalanceHelper.FormatBalance(balance)}", $"Till: {ShiftBalanceHelper.FormatBalance(balance)}", $"Kasa: {ShiftBalanceHelper.FormatBalance(balance)}", $"Kassa: {ShiftBalanceHelper.FormatBalance(balance)}")
            // 2026-10-20 (живой аудит кассы, К18): «0,00 сом», как после открытия смены.
            : Tr.T("Касса: ", "Касса: ", "Till: ", "Kasa: ", "Kassa: ") + MoneyText.Som(0m);

        // 2026-10-22 (П1): смену закрыл сервер — вместо «Касса: 0,00» понятная подпись со временем.
        if (!_session.IsShiftOpen && ServerShiftClosedState.Current is { } closedByServer)
            balanceText = ServerShiftTexts.Short(closedByServer);

        _viewModel.Toolbar.Status.SetShiftBalance(balance ?? 0m);
        _viewModel.SideMenu.ShiftBalanceText = _session.IsShiftOpen
            ? ShiftBalanceHelper.FormatBalance(balance)
            : ServerShiftClosedState.Current is { } closedByServerMenu ? ServerShiftTexts.Short(closedByServerMenu)
            : Tr.T("Смена не открыта", "Смена ачылган эмес", "Shift not open", "Vardiya açık değil", "Smena ochilmagan");

        // 2026-10-17, ревью этапа 1: при слепом пересчёте (Настройки → Операции) сумма в ящике не показывается
        // в шапке и меню — иначе кассир видит ожидаемое и «слепой» пересчёт теряет смысл.
        if (_session.IsShiftOpen && UserPreferences.Instance.BlindCashCount)
        {
            balanceText = Tr.T("Касса: скрыто", "Касса: жашырылган", "Till: hidden", "Kasa: gizli", "Kassa: yashirilgan");
            _viewModel.SideMenu.ShiftBalanceText = Tr.T("скрыто (слепой пересчёт)", "жашырылган (сокур эсептөө)", "hidden (blind count)", "gizli (kör sayım)", "yashirilgan (ko'r sanash)");
        }
        _viewModel.SideMenu.RefreshSessionInfo();

        if (_viewModel.Toolbar.Status.ShiftBalanceText != balanceText)
            _viewModel.Toolbar.Status.ShiftBalanceText = balanceText;

        // 2026-10-22 (смена 23 ч): смена закрыта/другая — плашку убрать; открыта и время известно — показать по возрасту.
        ApplyShiftAgeUi();
        ShiftAgeCloseButton.Content = ServerShiftTexts.CloseShiftButton();
    }

    /// <param name="keepSavedLogin">2026-10-18, ревью безопасности: true — отказ по доступу «Интерфейс кассира»:
    /// сохранённый вход (auth.dat) НЕ стирается, чтобы после включения доступа на сайте войти снова (и без связи).</param>
    private async Task NavigateToLoginAsync(bool keepSavedLogin = false)
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

        // 2026-10-18, ревью безопасности (П4): выход — облачные настройки следующего входа получаются заново, отложенная
        // отправка этого пользователя не уйдёт под чужим входом.
        SettingsCloudSync.OnUserChanged(null);
        App.AuthApi.ClearSession();
        // Logout is explicit: remove the DPAPI session as well as in-memory tokens.
        if (!keepSavedLogin)
        {
            await App.GetRequiredService<NurMarketKassa.Core.Contracts.IAuthSessionManager>()
                .ClearSessionAsync()
                .ConfigureAwait(true);
            OfflineAuthSessionStore.Clear();
        }
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
