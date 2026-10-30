using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using ChartPalette = NurMarketKassa.Core.Application.ChartPalette;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Главное окно программы владельца «NurMarket Владелец» (2026-09-26, разделение программ,
/// см. <see cref="AppMode"/>). Разделы слева — те же окна, что раньше открывались из меню кассы
/// (склад, продажи, финансы, зарплата, ABC, клиенты, CRM…), с теми же проверками прав и тарифа;
/// раздел занимает правую часть этого окна (см. «разделы в окне» ниже).
/// Справа — сводка за день/неделю/месяц с сервера NurCRM: показатели со сравнением с прошлым
/// периодом, выручка по дням, способы оплаты, последние продажи и лучшие товары. Обновляется
/// раз в минуту (RefreshInterval) и сразу при возврате в окно: продажа, пробитая на кассе, появляется здесь
/// без отдельной синхронизации. // 2026-10-23 (аудит, круг 8, В-6): было написано «каждые 20 секунд» — давно не так.</summary>
public partial class OwnerShellWindow : Window, IMainShell
{
    // 2026-10-04, отчёт о производительности (п. 7): 60 с вместо 20 с и только пока «Сводка» на экране и
    // окно активно (RefreshWhenShownAsync). Было 12 запросов в минуту с каждой программы владельца (27 —
    // утром до первой продажи), даже свёрнутой или под открытым разделом.
    // 2026-10-29, «продажа появляется в Сводке через 60–100 с»: 30 с вместо 60 (SyncCenter.OverviewInterval) + обновление по сигналу SalesChanged
    // (своё действие или чужая продажа — RemoteSalesPulse, раз в ~20 с, один лёгкий запрос). Отчёты сервера кэшируются на 15 с (ReportCache).
    private static readonly TimeSpan RefreshInterval = SyncCenter.OverviewInterval;

    /// <summary>2026-10-04, п. 7: график «Выручка за 7 дней» — отдельный отчёт сервера; обновлять его чаще
    /// раза в 5 минут незачем (кнопка «Обновить» и смена периода — сразу).</summary>
    private static readonly TimeSpan ChartRefreshInterval = TimeSpan.FromMinutes(5);
    private JsonElement? _chartCache;
    private string? _chartCacheKey;
    private DateTime _chartCacheAtUtc = DateTime.MinValue;
    private DateTime _lastRefreshStartedUtc = DateTime.MinValue;
    // 2026-10-23 (аудит, круг 8, В-6): время последнего нажатия клавиши в окне. Раньше фоновое обновление
    // стояло, пока курсор в любом поле (даже «Найти раздел»); теперь ждём, только если печатают прямо сейчас.
    private DateTime _lastKeyInputUtc = DateTime.MinValue;
    private static readonly TimeSpan TypingQuietPeriod = TimeSpan.FromSeconds(15);

    private const int RecentRows = 8;

    // 2026-10-18, этап О1 (редизайн сводки): цвета способов оплаты (зелёный/синий/фиолетовый) убраны — у цвета одна роль,
    // способы различаются оттенком цвета текста темы (ApplyPayments).

    /// <summary>2026-10-18, этап О1: уже этого (ширина сводки) — показатели 2×2, ещё шире на 100 — колонки рядом.</summary>
    private const double KpiNarrowWidth = 900;
    private const double RowsNarrowWidth = 1000;

    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private string _period = "today";
    // 2026-09-30, «в сводке сделай спец. дату тоже»: свои даты (_period = "custom").
    private DateTime _customFrom = DateTime.Today;
    private DateTime _customTo = DateTime.Today;
    private bool _refreshing;
    private bool _loggingOut;
    private DateTime? _lastSuccess;

    // Прошлый период не меняется — берём его отчёт один раз на период (и на новый день).
    private string? _compareKey;
    private double _lastMargin;
    private JsonElement? _compareCards;

    // 2026-09-30, «проверь бота, он не работает»: команды бота слушала только касса, а бота
    // подключают здесь, в программе владельца (у неё свой файл настроек) — на /segodnya никто не
    // отвечал. Теперь команды слушает и программа владельца, если касса на этом компьютере их не
    // слушает (замок в TelegramBotPollingService), и раз в минуту подхватывает новые настройки бота.
    private readonly DispatcherTimer _telegramTimer;
    private TelegramBotPollingService? _telegramBot;

    private void StartTelegramBot()
    {
        try
        {
            // 2026-10-01: заказы, подтверждённые покупателем в боте, — в заказы витрины (решение владельца).
            TelegramAiChat.OrderCreator ??= (name, phone, items, comment, token) =>
                App.GetRequiredService<NurMarketKassa.Services.Api.ShowcaseApiService>().CreateBotOrderAsync(name, phone, items, comment, token);
            // 2026-10-02: бот видит размеры и цвета одежды (варианты товара с сервера).
            TelegramAssistant.VariantsLoader ??= (productId, token) => App.CatalogApi.GetProductVariantsAsync(productId, token);
            // 2026-10-02: бот отвечает и про прокат («кто не вернул», «залоги»).
            TelegramAssistant.RentalsLoader ??= (status, token) => App.GetRequiredService<NurMarketKassa.Services.Api.RentalsApi>().ListAsync(status, token);
            UserPreferences.AdoptTelegramBotFromOtherApp();
            // Касса на этом компьютере запущена — команды слушает она: отчёты бота считаются по
            // её продажам и каталогу. Программа владельца отвечает, только когда кассы нет.
            if (!TelegramBotService.IsConfigured || !UserPreferences.Instance.TelegramCommandsEnabled || IsKassaRunning())
            {
                _telegramBot?.Stop();
                return;
            }

            _telegramBot ??= new TelegramBotPollingService(
                App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.ClientDebtsApiService>());
            _telegramBot.Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: запустить не удалось ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Касса держит замок «одна копия» (Program.cs) — по нему и узнаём, что она запущена.</summary>
    private static bool IsKassaRunning()
    {
        if (!Mutex.TryOpenExisting(@"Global\NurMarketKassa-SingleInstance", out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    private bool _narrowKpi;

    /// <summary>2026-10-04, Android: четыре показателя «Сводки» в ряд не помещаются на узком экране —
    /// там они 2×2 (колонки 0, 2, 4, 6 → клетки 2×2), на широком — снова в ряд.</summary>
    private void ApplyNarrowKpiGrid()
    {
        var w = OverviewScroll.Bounds.Width;
        if (w <= 0)
            return;
        // 2026-10-18, этап О1: и в Windows — на 1024×768 с открытым меню (сводка ≈ 770) показатели 2×2.
        var narrow = _narrowKpi ? w < KpiNarrowWidth + 40 : w < KpiNarrowWidth;
        if (narrow == _narrowKpi)
            return;
        _narrowKpi = narrow;
        OverviewKpiGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,16,*" : "*,16,*,16,*,16,*");
        OverviewKpiGrid.RowDefinitions = narrow ? new RowDefinitions("Auto,16,Auto") : new RowDefinitions();
        var index = 0;
        foreach (var card in OverviewKpiGrid.Children.OfType<Control>())
        {
            Grid.SetColumn(card, narrow ? (index % 2) * 2 : index * 2);
            Grid.SetRow(card, narrow ? (index / 2) * 2 : 0);
            index++;
        }
    }

    public OwnerShellWindow()
    {
        InitializeComponent();
        // 2026-10-18, этап О2: цвета тёмной колонки групп — из темы, пересчёт при смене темы (OwnerShellWindow.Nav.cs).
        ApplyRailBrushes();
        ResourcesChanged += (_, _) => QueueRailBrushes();
        ActualThemeVariantChanged += (_, _) => QueueRailBrushes();
        // До 1366 точек колонка пунктов меню сворачивается сама, пока открыт раздел.
        SizeChanged += (_, _) => OnShellSizeChangedForNav();
        // 2026-10-04, владелец: «у владельца тоже адаптацию под экраны сделай» (Android, вертикальный телефон).
        // «Сводка»: на узком месте показатели — 2×2, «Выручка | Способы оплаты» и «Последние | Лучшие» — друг под другом.
        // 2026-10-18, этап О1 (редизайн сводки): то же и в Windows/Linux — на 1024×768 колонки сводки складываются
        // друг под друга (было только на Android, а окно владельца на 1024 обрезалось справа).
        NarrowStack.Attach(OverviewScroll, OverviewRow1, RowsNarrowWidth, "Auto,16,Auto");
        NarrowStack.Attach(OverviewScroll, OverviewRow2, RowsNarrowWidth, "Auto,16,Auto");
        OverviewScroll.SizeChanged += (_, _) => ApplyNarrowKpiGrid();
        HeaderGrid.SizeChanged += (_, _) => FitHeaderTools();
        // 2026-10-04, редизайн под Android-телефон: меню по «≡», без кнопок окна (OwnerShellWindow.Phone.cs).
        AttachPhoneLayout();
        // 2026-10-29 (скорость синхронизации): режим окна для пульса продаж (5 с при активной «Сводке», 60 с в свёрнутом).
        NurMarketKassa.AvaloniaHost.Services.SyncActivityWatcher.Attach(this);
        // 2026-10-18: вид меню на выбор — новое / бургер ☰ / старое (OwnerShellWindow.MenuStyle.cs).
        AttachMenuStyle();
        // 2026-10-05: на «Старте» подключили (или истёк тестовый доступ) пакет «Стандарта» — меню сразу по тарифу.
        // 2026-10-30 (стресс-тест 1.17.68): обработчики статических событий снимаются при закрытии окна — иначе каждое созданное окно программы
        // владельца оставалось в памяти навсегда (~6 МБ на окно; стенд: 30 открытий — +175 МБ, 35 из 35 окон живы).
        Action rebuildNavigation = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { BuildNavigation(); _ = RefreshDebtsCardAsync(force: true); });
        TariffGate.PacksChanged += rebuildNavigation;
        SectionVisibility.Changed += rebuildNavigation;
        Closed += (_, _) =>
        {
            TariffGate.PacksChanged -= rebuildNavigation;
            SectionVisibility.Changed -= rebuildNavigation;
            // 2026-10-31 (стресс-тест 1.17.68, п. 8): статические ссылки ProductActionPlan на лямбды окна держали закрытое главное окно (WireNavSearch).
            if (ProductActionPlan.OpenProduct?.Target == this) ProductActionPlan.OpenProduct = null;
            if (ProductActionPlan.OpenSection?.Target == this) ProductActionPlan.OpenSection = null;
            if (ProductActionPlan.NavigateByWords?.Target == this) ProductActionPlan.NavigateByWords = null;
            if (ProductActionPlan.OpenNavTarget?.Target == this) ProductActionPlan.OpenNavTarget = null;
            if (ProductActionPlan.ShowChangedProducts?.Target == this) ProductActionPlan.ShowChangedProducts = null;
        };
        // 2026-10-23 (аудит, круг 8, В-6): туннельный обработчик, handledEventsToo — видит и клавиши, которые поле уже обработало.
        AddHandler(KeyDownEvent, (_, _) => _lastKeyInputUtc = DateTime.UtcNow, RoutingStrategies.Tunnel, handledEventsToo: true);
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshWhenShownAsync().ConfigureAwait(true);
        // 2026-10-04, п. 7: окно снова активно, а «Сводка» давно не обновлялась (пока окно было свёрнуто
        // или владелец работал в другой программе) — обновить сразу, не дожидаясь таймера.
        Activated += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefreshStartedUtc >= RefreshInterval)
                await RefreshWhenShownAsync().ConfigureAwait(true);
        };
        _abcDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _abcDebounce.Tick += (_, _) =>
        {
            _abcDebounce.Stop();
            RefreshAbcWhenVisible();
        };
        _siteOrdersTimer = new DispatcherTimer { Interval = SiteOrdersPollInterval };
        _siteOrdersTimer.Tick += (_, _) => _ = PollSiteOrdersAsync();
        Opened += async (_, _) =>
        {
            // 2026-10-21 (сенсорный моноблок): окно оказалось на другом экране, чем основной, — минимум ещё раз по его рабочей области.
            FitMinSizeToScreen();
            // ABC — параллельно с загрузкой сводки: он считается локально и сервера не ждёт.
            _ = RefreshAbcAsync();
            await RefreshAsync().ConfigureAwait(true);
            _timer.Start();
            StartTelegramBot();
            _telegramTimer.Start();
            // 2026-10-01: один раз предложить перенести бота на сервер NurCRM (работает круглые сутки).
            ServerBotOffer.Schedule(this);
            // 2026-10-06, владелец: «новым клиентам при первом запуске спрашивать сферу маркета» — только на новой установке.
            _ = Dialogs.MarketSphereChoiceWindow.MaybeAskAsync(this);
            // 2026-10-02: сроки проката — значок у «Проката», карточка в меню и напоминание в Телеграм.
            RentalDueNotifier.Changed += OnRentalDueChanged;
            RentalDueNotifier.Start((status, token) => App.GetRequiredService<NurMarketKassa.Services.Api.RentalsApi>().ListAsync(status, token));
            // 2026-10-21, И3 «Утренний отчёт» Нур Советника: планировщик (в 9:00 или при первом запуске после 9:00 — карточка и Telegram владельцу).
            OwnerMorningReport.Start();
            // Заказы с сайта — после сводки, чтобы первые запросы не шли пачкой.
            // 2026-09-30: опрос «Закупок» и значок — только когда список заказов включён.
            if (ShowcaseApiService.OrdersListEnabled)
            {
                _ = PollSiteOrdersAsync();
                _siteOrdersTimer.Start();
            }
        };
        _telegramTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _telegramTimer.Tick += (_, _) => StartTelegramBot();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _telegramTimer.Stop();
            _telegramBot?.Stop();
            _siteOrdersTimer.Stop();
            ShowcaseApiService.NewOrdersCountChanged -= OnSiteOrdersCountChanged;
            RentalDueNotifier.Changed -= OnRentalDueChanged;
            _abcDebounce.Stop();
            _abcCts?.Cancel();
            _cts.Cancel();
            Tr.LanguageChanged -= OnLanguageChanged;
            LanSyncService.Instance.PeerDataChanged -= OnLanPeerData;
            PosDataEvents.SalesChanged -= OnSalesChangedForAbc;
        };
        // 2026-10-30: сессия недействительна — одно окно «Сессия истекла. Войдите снова» (права и данные не сбрасываются).
        var sessionPrompt = NurMarketKassa.AvaloniaHost.Services.SessionExpiredPrompt.Attach(this, SignOutAsync);
        Closed += (_, _) => sessionPrompt.Dispose();
        Tr.LanguageChanged += OnLanguageChanged;
        LanSyncService.Instance.PeerDataChanged += OnLanPeerData;
        PosDataEvents.SalesChanged += OnSalesChangedForAbc;
        ShowcaseApiService.NewOrdersCountChanged += OnSiteOrdersCountChanged;
        UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");

        // 2026-10-25, ТЗ «оприходование в фоне» (Q5): значок «в фоне: N» в шапке (скрыт, пока приходов в фоне нет); нажатие — окно «Приходы в фоне».
        if (NurMarketKassa.AvaloniaHost.Views.Main.Controls.BackgroundReceiptsBadge.Create() is { } bgReceiptsBadge)
        {
            bgReceiptsBadge.HorizontalAlignment = HorizontalAlignment.Right;
            bgReceiptsBadge.Margin = new Thickness(0, 6, 12, 0);
            Grid.SetColumn(bgReceiptsBadge, 0);
            HeaderGrid.Children.Add(bgReceiptsBadge);
        }
        Closing += (_, _) => CloseAllSections();
        PositionChanged += (_, _) => SyncSectionBounds();
        // Не сразу, а после разметки: размер области раздела меняется раньше, чем её родитель
        // встаёт на новое место (Avalonia ставит Bounds родителю после детей), и при сворачивании
        // меню раздел ложился по старому краю — поверх меню (скриншот владельца 2026-09-26).
        SectionHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
        };

        ApplyTexts();
    }

    /// <summary>То же, что кассе при входе: компания, раздельные данные аккаунтов и проверка
    /// подписки. Без смены, каталога продаж и оборудования — это касса.</summary>
    public async Task<bool> InitializeApplicationAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(Tr.T("Загрузка профиля…", "Профиль жүктөлүүдө…", "Loading profile…", "Profil yükleniyor…", "Profil yuklanmoqda…"));
        try
        {
            var subscription = await CompanyInfoService.RefreshAsync(App.AuthApi, cancellationToken).ConfigureAwait(true);
            AccountDataIsolation.SwitchTo(CompanyInfoService.LastCompany?.Id);
            await AccountDataIsolation.EnsureAccountDataLoadedAsync(progress, cancellationToken).ConfigureAwait(true); // 2026-10-29: «Загружаем данные аккаунта…» до открытия окна
            // 2026-10-25, ТЗ «оприходование в фоне» (Q5): очередь приходов в фоне догружает незавершённое.
            BackgroundReceiptsHost.Start();
            if (subscription is { IsExpired: true })
            {
                try
                {
                    PosAlertDialog.Show(null,
                        Tr.T("Подписка не оплачена", "Жазылуу төлөнгөн эмес", "Subscription not paid", "Abonelik ödenmedi", "Obuna to'lanmagan"),
                        Tr.T($"Срок действия компании истёк ({subscription.EndDate:dd.MM.yyyy}). Пожалуйста, оплатите!",
                            $"Компаниянын мөөнөтү бүттү ({subscription.EndDate:dd.MM.yyyy}). Сураныч, төлөңүз!",
                            $"Your company's subscription has expired ({subscription.EndDate:dd.MM.yyyy}). Please make a payment.",
                            $"Şirketin abonelik süresi doldu ({subscription.EndDate:dd.MM.yyyy}). Lütfen ödeme yapın!",
                            $"Kompaniya obunasi muddati tugadi ({subscription.EndDate:dd.MM.yyyy}). Iltimos, to'lovni amalga oshiring!"),
                        PosAlertKind.Error,
                        Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"));
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: subscription alert failed: {ex.Message}", "WARNING");
                }

                OpenUrl("https://www.nurcrm.kg");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: company profile unavailable: {ex.Message}", "WARNING");
        }

        ApplyTexts();
        return true;
    }

    public void PlaceOnPrimaryScreen()
    {
        // 2026-10-21 (сенсорный моноблок): минимум окна из XAML (1000×560) больше рабочей области маленьких экранов
        // (1366×768 при 150 % — 911×512 точек, 10" моноблок 1024×600 — 1024×552): развёрнутое окно всё равно оставалось
        // 1000×560 и уходило за край экрана — «Выйти», поиск раздела, кнопки окна и «Все продажи» были недостижимы пальцем.
        // Минимум опускается до рабочей области (только вниз, на обычных мониторах не меняется) — до разворачивания.
        FitMinSizeToScreen();
        // 2026-10-27, жалоба владельца «программа владельца запускается в окне»: настройка «Полный экран» здесь не учитывалась —
        // окно всегда просто разворачивалось (с панелью задач). Теперь при включённой настройке — как у кассы (FullscreenHelper).
        WindowState = WindowState.Maximized;
        FullscreenHelper.ApplyToMainWindow(this);
        WindowFitGuard.Attach(this); // 2026-10-30: окно целиком в рабочей области экрана (см. WindowFitGuard)
    }

    /// <summary>2026-10-21 (сенсорный моноблок): MinWidth/MinHeight не больше рабочей области экрана окна (в точках).</summary>
    private void FitMinSizeToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
                return;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var width = Math.Floor(screen.WorkingArea.Width / scaling);
            var height = Math.Floor(screen.WorkingArea.Height / scaling);
            var changed = false;
            if (width > 0 && MinWidth > width) { MinWidth = width; changed = true; }
            if (height > 0 && MinHeight > height) { MinHeight = height; changed = true; }
            if (changed)
                PosLogger.Log($"Owner app: экран {width:0}×{height:0} меньше минимума окна — минимум опущен до экрана.", "UI");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: подгонка минимума окна под экран не удалась: {ex.Message}", "WARNING");
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTexts();
        RebuildSectionsForLanguage();
        _compareKey = null;
        _ = RefreshAsync();
        // Заголовки срезов и колонки ABC собраны кодом на прежнем языке — собираем заново.
        DropAbcView();
        _ = RefreshAbcAsync();
    });

    // ------------------------------------------------------------------ тексты и разделы

    private static CultureInfo UiCulture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(Tr.T("ru-RU", "ky-KG", "en-US", "tr-TR", "uz-Latn-UZ"));
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.GetCultureInfo("ru-RU");
            }
        }
    }

    private void ApplyTexts()
    {
        Title = Tr.T("NurCRM Владелец", "NurCRM Ээси", "NurCRM Owner", "NurCRM İşletme Sahibi", "NurCRM Egasi");
        // 2026-10-18, этап О2: подписи «NurMarket / Владелец» у логотипа в тёмной колонке нет — во всплывающей подсказке.
        ToolTip.SetTip(LogoImage, "NurCRM · " + Tr.T("Владелец", "Ээси", "Owner", "İşletme sahibi", "Egasi"));

        var company = CompanyInfoService.LastCompany;
        CompanyNameText.Text = string.IsNullOrWhiteSpace(company?.Name) ? "NurCRM" : company!.Name;
        var plan = TariffGate.CurrentPlanName;
        var end = CompanyInfoService.GetCachedSubscriptionStatus()?.EndDate;
        var tariff = string.IsNullOrWhiteSpace(plan) ? "" : Tr.T($"Тариф «{plan}»", $"Тариф «{plan}»", $"{plan} plan", $"«{plan}» tarifesi", $"«{plan}» tarifi");
        if (end is { } e && e.Year > 2000)
            tariff += (tariff.Length > 0 ? " · " : "") + Tr.T($"до {e:dd.MM.yyyy}", $"{e:dd.MM.yyyy} чейин", $"until {e:dd.MM.yyyy}", $"{e:dd.MM.yyyy} tarihine kadar", $"{e:dd.MM.yyyy} gacha");
        TariffText.Text = tariff;
        TariffText.IsVisible = tariff.Length > 0;
        // 2026-10-06, редизайн: в узком меню дата тарифа обрезалась («до 01.…») — полностью во всплывающей подсказке.
        // 2026-10-18, этап О2: тариф переносится строкой (колонка пунктов шире), подсказка оставлена.
        ToolTip.SetTip(CompanyCard, CompanyNameText.Text + (tariff.Length > 0 ? "\n" + tariff : ""));

        // 2026-10-18, этап О2 (макет OwnerNav): пользователь — кружок с буквами внизу тёмной колонки, имя — в подсказке.
        var name = PosApp.CurrentUserDisplayName ?? "";
        var userLine = (string.IsNullOrWhiteSpace(name) ? Tr.T("Пользователь", "Колдонуучу", "User", "Kullanıcı", "Foydalanuvchi") : name)
                       + "\n" + Tr.T("Вход через NurCRM", "NurCRM аркылуу кирүү", "Signed in via NurCRM", "NurCRM ile giriş", "NurCRM orqali kirish");
        AvatarText.Text = Initials(name);
        ToolTip.SetTip(AvatarBadge, userLine);
        Avalonia.Automation.AutomationProperties.SetName(AvatarBadge, userLine.Replace("\n", ", "));
        ThemeLabel.Text = Tr.T("Тема", "Тема", "Theme", "Tema", "Mavzu");
        ExitLabel.Text = Tr.T("Выйти", "Чыгуу", "Exit", "Çık", "Chiqish");
        var themeTip = Tr.T("Светлая / тёмная тема", "Жарык / караңгы тема", "Light / dark theme", "Açık / koyu tema", "Yorug' / qorong'i mavzu");
        var exitTip = Tr.T("Выйти на рабочий стол", "Иш столуна чыгуу", "Exit to desktop", "Masaüstüne çık", "Ish stoliga chiqish");
        ToolTip.SetTip(ThemeButton, themeTip);
        ToolTip.SetTip(ExitButton, exitTip);
        ToolTip.SetTip(RefreshButton, Tr.T("Обновить сейчас", "Азыр жаңыртуу", "Refresh now", "Şimdi yenile", "Hozir yangilash"));
        var collapseTip = Tr.T(
            "Свернуть колонку пунктов. На экране до 1366 точек она сворачивается сама, когда открыт раздел",
            "Пункттар тилкесин жыйноо. 1366 чекиттен кичине экранда бөлүм ачылганда өзү жыйылат",
            "Collapse the items column. On screens up to 1366 px it collapses by itself while a section is open",
            "Öğe sütununu daralt. 1366 piksele kadar ekranlarda bir bölüm açıkken kendiliğinden daralır",
            "Bandlar ustunini yig'ish. 1366 nuqtagacha ekranda bo'lim ochiq bo'lsa, o'zi yig'iladi");
        var expandTip = Tr.T("Развернуть колонку пунктов", "Пункттар тилкесин ачуу", "Expand the items column", "Öğe sütununu genişlet", "Bandlar ustunini yoyish");
        ToolTip.SetTip(CollapseButton, collapseTip);
        ToolTip.SetTip(ExpandNavButton, expandTip);
        // 2026-10-18, этап О2 (правило аудита 10): у кнопок-значков — имя для экранного диктора (было «Avalonia.Controls.Grid»).
        Avalonia.Automation.AutomationProperties.SetName(ThemeButton, themeTip);
        Avalonia.Automation.AutomationProperties.SetName(ExitButton, exitTip);
        Avalonia.Automation.AutomationProperties.SetName(CollapseButton, Tr.T("Свернуть колонку пунктов", "Пункттар тилкесин жыйноо", "Collapse the items column", "Öğe sütununu daralt", "Bandlar ustunini yig'ish"));
        Avalonia.Automation.AutomationProperties.SetName(ExpandNavButton, expandTip);
        Avalonia.Automation.AutomationProperties.SetName(RefreshButton, Tr.T("Обновить сейчас", "Азыр жаңыртуу", "Refresh now", "Şimdi yenile", "Hozir yangilash"));
        Avalonia.Automation.AutomationProperties.SetName(NavSearchText, Tr.T("Найти раздел (Ctrl+K)", "Бөлүмдү табуу (Ctrl+K)", "Find a section (Ctrl+K)", "Bölüm bul (Ctrl+K)", "Bo'limni topish (Ctrl+K)"));
        Avalonia.Automation.AutomationProperties.SetName(RentalAlertClose, Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish"));
        // 2026-10-27, аудит владельца С-12: кнопки окна читались как «Avalonia.Controls.Shapes.Path».
        Avalonia.Automation.AutomationProperties.SetName(MinimizeCaptionButton, Tr.T("Свернуть окно", "Терезени жыйыштыруу", "Minimize the window", "Pencereyi küçült", "Oynani yig'ish"));
        Avalonia.Automation.AutomationProperties.SetName(MaximizeCaptionButton, Tr.T("Развернуть окно", "Терезени чоңойтуу", "Maximize the window", "Pencereyi büyüt", "Oynani kengaytirish"));
        Avalonia.Automation.AutomationProperties.SetName(CloseCaptionButton, Tr.T("Закрыть программу", "Программаны жабуу", "Close the program", "Programı kapat", "Dasturni yopish"));
        AutomationNaming.Install();
        // 2026-10-18: кнопка «Вид» и подписи старого вида меню.
        ApplyMenuStyleTexts();
        UpdateThemeIcon();

        DashboardTitle.Text = Tr.T("Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish");
        SectionLoadingText.Text = Tr.T("Открываем раздел…", "Бөлүм ачылууда…", "Opening…", "Bölüm açılıyor…", "Bo'lim ochilmoqda…");
        var culture = UiCulture;
        var today = DateTime.Today.ToString("dddd, d MMMM yyyy", culture);
        DateText.Text = today.Length > 0 ? char.ToUpper(today[0], culture) + today[1..] : today;
        // 2026-10-18, этап О1: на узкой шапке (1024×768) — без дня недели (FitHeaderTools).
        _dateFull = DateText.Text;
        _dateShort = DateTime.Today.ToString("d MMMM yyyy", culture);
        FitHeaderTools();
        TodayButton.Content = Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun");
        WeekButton.Content = Tr.T("Неделя", "Жума", "Week", "Hafta", "Hafta");
        MonthButton.Content = Tr.T("Месяц", "Ай", "Month", "Ay", "Oy");
        CustomButton.Content = _period == "custom"
            ? (_customFrom == _customTo ? $"{_customFrom:dd.MM}" : $"{_customFrom:dd.MM}–{_customTo:dd.MM}")
            : Tr.T("Спец. дата", "Башка дата", "Custom dates", "Özel tarih", "Boshqa sana");

        // 2026-10-23: единое название с кассой (shiftDetails.revenueLabel), круг 6.
        RevenueLabel.Text = Tr.T("Выручка (без долга)", "Түшүм (карызсыз)", "Revenue (excl. debt)", "Ciro (borç hariç)", "Tushum (qarzsiz)");
        ChecksLabel.Text = Tr.T("Чеки", "Чектер", "Receipts", "Fişler", "Cheklar");
        AvgLabel.Text = Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek");
        // 2026-10-18, ревью О1: маржа последних карточек сохраняется и при смене языка.
        ProfitLabel.Text = Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda")
                           + (_lastMargin != 0 ? " · " + Percent(_lastMargin) : "");
        // 2026-10-19, этап О10: «как считается» — подсказкой у плитки (те же слова, что в Аналитике и «Прибыли и деньгах»).
        if (ProfitLabel.Parent?.Parent is Control profitCard)
            ToolTip.SetTip(profitCard, ProfitTerms.GrossHow);

        ChartTitle.Text = (_period == "month"
            ? Tr.T("Выручка по дням месяца", "Айдын күндөрү боюнча түшүм", "Revenue by day this month", "Ayın günlerine göre ciro", "Oy kunlari bo'yicha tushum")
            : _period == "custom"
                ? Tr.T("Выручка по дням периода", "Мезгилдин күндөрү боюнча түшүм", "Revenue by day for the period", "Dönemin günlerine göre ciro", "Davr kunlari bo'yicha tushum")
                : Tr.T("Выручка за 7 дней", "7 күндүк түшүм", "Revenue, last 7 days", "Son 7 günün cirosu", "7 kunlik tushum"))
            // 2026-10-18, этап О1: подписи над столбцами — в одной единице (тыс. сом), она названа в заголовке.
            + (ChartHasLabels ? ", " + ThousandSom() : "");
        AttentionTitle.Text = Tr.T("Требует внимания", "Көңүл буруу керек", "Needs attention", "Dikkat gerektirenler", "E'tibor talab qiladi");
        // 2026-10-18, ревью О1: «Загрузка…» / «Всё в порядке» — по тому, что уже загружено (RenderAttention).
        RenderAttention();
        AskAiText.Text = Tr.T("Спросить Нур Советника", "Нур Кеңешчиден суроо", "Ask Nur Advisor", "Nur Danışman'a sor", "Nur Maslahatchidan so'rash");
        ToolTip.SetTip(AskAiButton, AskAiText.Text);
        Avalonia.Automation.AutomationProperties.SetName(AskAiButton, AskAiText.Text);
        ChartEmptyText.Text = Tr.T("Продаж за эти дни нет", "Бул күндөрү сатуу жок", "No sales on these days", "Bu günlerde satış yok", "Bu kunlarda sotuv yo'q");
        PaymentsTitle.Text = Tr.T("Способы оплаты", "Төлөм ыкмалары", "Payment methods", "Ödeme yöntemleri", "To'lov usullari");
        PaymentsEmptyText.Text = Tr.T("Оплат пока нет", "Азырынча төлөм жок", "No payments yet", "Henüz ödeme yok", "Hozircha to'lov yo'q");
        ReturnsLabel.Text = Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar");
        RecentTitle.Text = Tr.T("Последние продажи", "Акыркы сатуулар", "Latest sales", "Son satışlar", "So'nggi sotuvlar");
        AllSalesText.Text = Tr.T("Все продажи", "Бардык сатуулар", "All sales", "Tüm satışlar", "Barcha sotuvlar");
        RecentEmptyText.Text = Tr.T("За этот период продаж нет", "Бул мезгилде сатуу жок", "No sales in this period", "Bu dönemde satış yok", "Bu davrda sotuv yo'q");
        TopTitle.Text = Tr.T("Лучшие товары", "Мыкты товарлар", "Top products", "En çok satanlar", "Eng yaxshi mahsulotlar");
        TopEmptyText.Text = Tr.T("Пока нечего показать", "Азырынча көрсөтө турган эч нерсе жок", "Nothing to show yet", "Henüz gösterilecek bir şey yok", "Hozircha ko'rsatadigan narsa yo'q");
        // 2026-10-18, этап О1: «Заканчивается на складе» — строками блока «Требует внимания» (OwnerShellWindow.Attention.cs).
        // 2026-10-21 (пакет 2, п. 73, 86): без «срезов» — что показывает блок, простыми словами.
        AbcTitle.Text = Tr.T("ABC-анализ: что приносит больше всего денег", "ABC-анализ: эмне көбүрөөк акча алып келет", "ABC analysis: what brings in the most money", "ABC analizi: en çok parayı ne getiriyor", "ABC tahlili: eng ko'p pulni nima keltiradi");
        AbcHint.Text = Tr.T(
            "За выбранный период: выручка, прибыль, количество, категории и бренды; склад по стоимости остатка — на сейчас. Нажмите на столбец, чтобы посмотреть разбор товара.",
            "Тандалган мезгил үчүн: түшүм, пайда, саны, категориялар жана бренддер; калдыктын наркы боюнча кампа — азыркы абалы. Товардын талдоосун көрүү үчүн мамыны басыңыз.",
            "For the selected period: revenue, profit, quantity, categories and brands; stock by inventory value is as of now. Click a bar to see the product breakdown.",
            "Seçilen dönem için: ciro, kâr, adet, kategoriler ve markalar; stok değerine göre depo ise şu anki durumu gösterir. Ürün ayrıntısını görmek için bir çubuğa tıklayın.",
            "Tanlangan davr uchun: tushum, foyda, miqdor, kategoriyalar va brendlar; qoldiq qiymati bo'yicha ombor — hozirgi holat. Mahsulot tahlilini ko'rish uchun ustunni bosing.");
        AbcOpenText.Text = Tr.T("Открыть ABC-анализ", "ABC-анализди ачуу", "Open ABC analysis", "ABC analizini aç", "ABC tahlilini ochish");
        AbcLoadingText.Text = Tr.T("Считаю ABC-анализ…", "ABC-анализ эсептелүүдө…", "Calculating the ABC analysis…", "ABC analizi hesaplanıyor…", "ABC tahlili hisoblanmoqda…");

        BuildNavigation();
        if (_activeSection != null)
            SectionTitleText.Text = TitleFor(_activeSection);
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length > 0 ? letters : "N";
    }

    /// <summary>Разделы по группам, с иконками. Проверки — те же, что у меню кассы
    /// (SideMenuViewModel / MainWindow.Navigate*): права роли и тариф «Старт» (на нём доступны
    /// склад и настройки, как на сайте). Недоступные разделы не показываются — как в кассе.</summary>
    private void BuildNavigation()
    {
        // 2026-10-18: поиск и пункты — в колонку выбранного вида меню (OwnerShellWindow.MenuStyle.cs).
        EnsureMenuStyleParts();
        _oldGroupHeaders.Clear();
        NavPanel.Children.Clear();
        RailPanel.Children.Clear();
        _navButtons.Clear();
        // 2026-10-06, редизайн меню: пункты и группы — для поиска раздела (OwnerShellWindow.NavSearch.cs).
        // 2026-10-18, этап О2: группы — кнопками тёмной колонки, пункты выбранной группы — в колонке пунктов (OwnerShellWindow.Nav.cs).
        _navEntries.Clear();
        _navGroups.Clear();
        _siteOrdersBadge = null;
        _rentalsBadge = null;
        var pendingGroup = (string?)null;
        var pendingGroupKey = (string?)null;
        var currentGroupKey = (string?)null;

        // 2026-10-04: на телефоне меню открывается на весь экран — всегда с колонкой пунктов (OwnerShellWindow.Phone.cs).
        ApplySidebarLayout();

        void Group(string groupKey, string title)
        {
            pendingGroup = title;
            pendingGroupKey = groupKey;
        }

        // iconKey — значок пункта прежнего меню; 2026-10-18, этап О2: у пунктов значков нет (значки — у групп, как в макете).
        void Add(string key, string iconKey, string text, bool visible, Action open)
        {
            _navTitles[key] = text;
            // 2026-10-05: раздел скрыт владельцем (Настройки → Экран → «Разделы меню»).
            if (!visible || SectionVisibility.IsHidden(key))
                return;

            if (pendingGroup != null)
            {
                // Группа появляется только с первым видимым пунктом — группа, где всё скрыто правами, тарифом или «Разделами
                // меню», в тёмной колонке не показывается.
                AddNavGroup(pendingGroupKey!, pendingGroup);
                currentGroupKey = pendingGroupKey;
                pendingGroup = null;
            }

            // 2026-09-29: у «Заказов с сайта» — число новых заказов; 2026-10-02: у «Проката» — сколько вернуть сегодня/завтра и просрочено.
            // 2026-10-18: в старом виде — пункт со значком (iconKey), как в выпуске 1.17.58.
            Border? badge;
            var button = OldMenu ? OldNavItemButton(key, iconKey, text, out badge) : NavItemButton(key, text, out badge);
            if (key == "siteorders")
                _siteOrdersBadge = badge;
            else if (key == "rentals")
                _rentalsBadge = badge;
            _navButtons[key] = button;
            _navEntries.Add(new NavEntry(key, text, currentGroupKey, button, open));
            button.Click += (_, _) =>
            {
                // 2026-10-04, телефон: выбор пункта закрывает меню на весь экран.
                SetPhoneMenu(false);
                // 2026-10-18, вид «Бургер ☰»: выехавшее меню закрывается; раздел не сменился (нет прав) — прежний возвращается.
                var fromBurger = CloseBurgerForNavigation();
                // 2026-10-18, этап О2: колонка пунктов была раскрыта нажатием на группу — после выбора снова свёрнута.
                if (_navPeek)
                {
                    _navPeek = false;
                    ApplySidebarLayout();
                }
                try
                {
                    open();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: section failed to open: {ex}", "ERROR");
                    PosMessageBox.Show(this, ex.Message, Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
                if (fromBurger)
                    RestoreSectionAfterBurger();
            };
            NavPanel.Children.Add(button);
        }

        // 2026-10-18, этап О2 (макет OwnerNav): «Главная» — своя группа в тёмной колонке.
        Group("home", Tr.T("Главная", "Башкы бет", "Home", "Ana sayfa", "Bosh sahifa"));
        Add("overview", "HomeIcon", Tr.T("Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish"), true,
            () => ShowSection(null));
        // 2026-10-05, владелец: «в десктопе открой чат с ИИ для владельца, чтобы владелец советовался с ним —
        // специальную вкладку». Видит выручку и лучшие товары — права как у «Аналитики».
        // 2026-10-05, владелец: «строго соблюдай разделение тарифов» — ИИ только на «Стандарте» (TariffGate.CanUseAi).
        Add("aiadvisor", "AiAdvisorIcon", Tr.T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), TariffGate.CanUseAi && AdvisorCapabilities.MasterOn, // 2026-10-30: главный тумблер «Нур Советник включён»
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("aiadvisor", () => new AiAdvisorWindow()); });

        Group("products", Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"));
        Add("warehouse", "WarehouseIcon", Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"), true,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("warehouse", () => App.GetRequiredService<WarehouseWindow>()); });
        // 2026-10-01: права разделов — как на сайте NurCRM (PosPermissions.ViewProducts/ViewAnalytics/…):
        // Склад — «Склад», Калькуляция/Финансы/Аналитика/ABC/бот — «Аналитика», Клиенты — «Клиенты»,
        // Заказы с сайта — «Заказы». Раньше Склад — по «Закупкам», остальное открывалось всем.
        Add("calculator", "CalculatorIcon", Tr.T("Калькуляция", "Калькуляция", "Pricing calculator", "Hesaplama", "Kalkulyatsiya"), true,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("calculator", () => new CalculatorWindow()); });
        Add("restock", "RestockIcon", Tr.T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar"), TariffGate.CanUseRestock,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("restock", () => App.GetRequiredService<RestockSuggestionsWindow>()); });
        // 2026-10-22, владелец: «Закупки как у веба» — история приходов, заказы поставщикам, рекомендуемый заказ, приём товара с оплатой
        // (PurchasesWindow). Тариф и право — как у «Поставщиков» (на «Старте» сайт закупки прячет), право — «Склад».
        Add("purchases", "PurchasesIcon", PurchasesWindow.TitleText, !TariffGate.IsStartTariff,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("purchases", () => new PurchasesWindow()); });
        // 2026-10-20, этап П1, владелец: «полностью возьми функционал (поставщиков) оттуда и нашим дизайном добавь нам» —
        // список поставщиков с долгом и карточка (SuppliersWindow); тариф и права — как у «Возвратов поставщикам».
        // 2026-10-20, живой аудит: свой значок (грузовик) — в «только значки» он совпадал с «Клиентами».
        Add("suppliers", "SuppliersIcon", SuppliersWindow.TitleText, !TariffGate.IsStartTariff,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("suppliers", () => new SuppliersWindow()); });
        // 2026-10-05, владелец: «возврат для поставщиков тоже добавь». Поставщики и закупки на сайте NurCRM на «Старте»
        // скрыты (платная услуга «Закупки») — здесь так же.
        Add("supplierreturns", "ReturnIcon", Tr.T("Возвраты поставщикам", "Жеткирүүчүлөргө кайтаруулар", "Returns to suppliers", "Tedarikçiye iadeler", "Yetkazib beruvchilarga qaytarishlar"),
            !TariffGate.IsStartTariff,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("supplierreturns", () => new SupplierReturnsWindow()); });
        // 2026-10-06, владелец: «на сайте есть филиалы — изучи и добавь в нашу админку тоже». Филиалы и перемещения товара
        // между складом и филиалами — те же, что на сайте NurCRM; права — как у «Склада». На «Старте» сайт раздел прячет — здесь так же.
        Add("branches", "BranchesIcon", Tr.T("Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar"), !TariffGate.IsStartTariff,
            () => { if (Authorize(PosPermissions.ViewProducts)) OpenSection("branches", () => new BranchesWindow()); });

        // 2026-10-06, редизайн меню: «Продажи и деньги» (9 пунктов) разделены — «Продажи» (чеки, долги, прокат)
        // и «Отчёты» (финансы, аналитика, ABC, прибыль, убыток, размеры).
        Group("sales", Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"));
        // 2026-10-18, решение владельца: «Продажи» и «Финансы» — один пункт «Продажи и финансы» (OwnerShellWindow.SalesFinance.cs).
        // Виден, если доступна хотя бы одна часть (право и «Разделы меню»); название — по доступным частям.
        var salesFinanceParts = SalesFinanceParts();
        Add(SalesFinanceKey, "SalesIcon", SalesFinanceNavTitle(salesFinanceParts),
            TariffGate.CanUseSalesAnalytics && (salesFinanceParts.Sales || salesFinanceParts.Finance),
            () => OpenSalesFinance());
        // 2026-10-05, владелец: «аналитика по долгам — при нажатии подробно показывать долги».
        Add("debts", "DebtsIcon", Tr.T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"), TariffGate.CanUseDebts,
            OpenDebts);
        // 2026-10-02, владелец: «и админку не забудь — при смене режима админка должна меняться». Прокат —
        // только в сферах «Одежда» и «Услуги»; в программе владельца — просмотр (выдача и возврат в кассе).
        // 2026-10-20, владелец: прокат во всех видах магазина (скрыть — «Показывать разделы»).
        Add("rentals", "RentalIcon", Tr.T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"),
            MarketSpheres.HasRentals,
            () => { if (Authorize(PosPermissions.ViewClients)) OpenSection("rentals", () => new RentalsWindow(null, null)); });

        Group("reports", Tr.T("Отчёты", "Отчёттор", "Reports", "Raporlar", "Hisobotlar"));
        // 2026-10-18: пункт «Финансы» убран — он теперь часть «Продаж и финансов» (группа «Продажи», выше).
        // Вся аналитика, кроме ABC (2026-09-27): выручка и оплаты, товары, сезонность, склад. Это
        // окно «Финансов» в режиме аналитики (FinanceWindow.AsAnalyticsSection); из самих
        // «Финансов», «Продаж» и «Склада» эти вкладки убраны. Права и тариф — как у «ABC-анализа».
        Add("analytics", "AnalyticsIcon", Tr.T("Аналитика", "Талдоо", "Analytics", "Analiz", "Analitika"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("analytics", () => App.GetRequiredService<FinanceWindow>().AsAnalyticsSection()); });
        Add("abc", "AbcIcon", Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC-tahlil"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("abc", () => App.GetRequiredService<AbcAnalysisWindow>()); });
        // 2026-10-01, ТЗ-BE-2026-04 (AN-11, AN-12 сделаны сервером): прибыль (P&L), движение денег и сверка отчётов.
        Add("profitcash", "ProfitIcon", Tr.T("Прибыль и деньги", "Пайда жана акча", "Profit & cash", "Kâr ve nakit", "Foyda va pul"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("profitcash", () => new ProfitCashReconcileWindow()); });
        // 2026-10-29, владелец: «Для аналитики как расход на начало бизнеса» + макет Investments: окупаемость вложений, инвесторы, выплаты. Деньги владельца — право как у «Зарплаты».
        Add("investments", "InvestIcon", Tr.T("Инвестиции и окупаемость", "Инвестициялар жана кайтарым", "Investments and payback", "Yatırımlar ve geri dönüş", "Investitsiyalar va o'zini oqlash"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("investments", () => new InvestmentsWindow()); });
        // 2026-10-03, владелец: «если в убыток продаёт со скидкой — фиксировать в админке».
        // 2026-10-05: аналитика — как «Аналитика» и «ABC», не на «Старте».
        Add("losssales", "LossIcon", Tr.T("Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("losssales", () => new LossSalesWindow()); });
        // 2026-10-28, запрос владельца «Общая аналитика по бонусам»: раздел «Бонусы» (BonusAnalyticsWindow) — права и тариф как у «Аналитики».
        Add("bonusanalytics", "ClientsIcon", Tr.T("Бонусы", "Бонустар", "Bonuses", "Bonuslar", "Bonuslar"), TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("bonusanalytics", () => new BonusAnalyticsWindow()); });
        // 2026-10-06, исследование «Кассы для одежды» (О-80): продажи по размерам и цветам — только в сфере «Одежда».
        Add("sizesreport", "SizesIcon", Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colours", "Bedenler ve renkler", "O'lchamlar va ranglar"),
            MarketSpheres.IsClothing && TariffGate.CanUseSalesAnalytics,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("sizesreport", () => new SizesReportWindow()); });

        // 2026-09-29, владелец: «заказы с сайта тоже должны падать в админку. Настройки сайта тоже».
        // Видны на любом тарифе: если витрина не подключена (на «Старте» это платная услуга NurCRM),
        // разделы сами говорят «Витрина не подключена» и как её подключить.
        Group("site", Tr.T("Сайт", "Сайт", "Website", "Web sitesi", "Veb-sayt"));
        Add("siteorders", "SiteOrdersIcon", Tr.T("Заказы с сайта", "Сайттан заказдар", "Website orders", "Web sitesi siparişleri", "Saytdan buyurtmalar"), true,
            () => { if (Authorize(PosPermissions.ViewOrders)) OpenSection("siteorders", () => new SiteOrdersWindow()); });
        // 2026-10-05, владелец (снимок витрины): «где редактор сайта??» — вид витрины (SiteEditorWindow).
        Add("siteeditor", "StoreIcon", Tr.T("Редактор сайта", "Сайттын редактору", "Website editor", "Web sitesi düzenleyici", "Sayt muharriri"), true,
            // 2026-10-27 (аудит редакторов, В-2): раньше без проверки прав — любой сотрудник мог менять и публиковать сайт; как у «Настроек сайта».
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("siteeditor", () => new SiteEditorWindow()); });
        Add("sitesettings", "SiteSettingsIcon", Tr.T("Настройки сайта", "Сайттын жөндөөлөрү", "Website settings", "Web sitesi ayarları", "Sayt sozlamalari"), true,
            OpenSiteSettings);
        // 2026-10-05, запрос NurCRM: «экран „Магазин в приложении“, чтобы владельцы подключались сами» (бесплатно на любом тарифе).
        Add("appshop", "AppShopIcon", Tr.T("Магазин в приложении", "Тиркемедеги дүкөн", "Shop in the app", "Uygulamadaki mağaza", "Ilovadagi do'kon"), true,
            () => OpenSection("appshop", () => new AppShopWindow()));

        // 2026-10-06, редизайн меню: «Люди» → «Клиенты» — всё о покупателях вместе (клиенты, бот, WhatsApp, воронка).
        Group("clients", Tr.T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"));
        Add("clients", "ClientsIcon", Tr.T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"), TariffGate.CanViewClients,
            () => { if (Authorize(PosPermissions.ViewClients)) OpenSection("clients", () => App.GetRequiredService<ClientsWindow>()); });
        // 2026-10-01, владелец: «в админке где аналитика по боту — обращения, клиенты, заказы?»
        // 2026-10-05: бот на «Старте» — только если куплен в Маркетплейсе (TariffGate.CanUseTelegramBot).
        Add("telegrambot", "TelegramBotIcon", Tr.T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot"), TariffGate.CanUseTelegramBot,
            () => { if (Authorize(PosPermissions.ViewAnalytics)) OpenSection("telegrambot", () => new TelegramBotAnalyticsWindow()); });
        // 2026-10-05, владелец: «к десктопу добавь воронку и WhatsApp Web». WhatsApp Web — встроенный браузер
        // (только Windows: WebView2); на Android откроется приложение WhatsApp.
        // 2026-10-05: воронка и WhatsApp — работа с клиентами, как раздел «Клиенты»: не на «Старте».
        Add("whatsapp", "WhatsAppIcon", "WhatsApp", TariffGate.CanViewClients, OpenWhatsApp);
        Add("funnel", "FunnelIcon", Tr.T("Воронка", "Воронка", "Sales funnel", "Satış hunisi", "Savdo voronkasi"), TariffGate.CanViewClients,
            () => { if (Authorize(PosPermissions.ViewClients)) OpenSection("funnel", () => new FunnelWindow()); });

        // 2026-10-06, редизайн меню: «Сервис» → «Управление» — зарплата, сайт NurCRM, покупка функций, настройки.
        Group("manage", Tr.T("Управление", "Башкаруу", "Management", "Yönetim", "Boshqaruv"));
        Add("salary", "SalaryIcon", Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi"), TariffGate.CanUseSalary,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("salary", () => new SalaryWindow()); });
        Add("crm", "CrmIcon", "NurCRM", TariffGate.CanUseService,
            () => OpenSection("crm", () => App.GetRequiredService<CrmWebViewWindow>()));
        Add("marketplace", "MarketplaceIcon", Tr.T("Маркетплейс", "Маркетплейс", "Marketplace", "Pazar yeri", "Marketpleys"), true,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("marketplace", () => new MarketplaceWindow().AsSection()); });
        Add("settings", "SettingsIcon", Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar"), true,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("settings", () => App.GetRequiredService<PosSettingsWindow>()); });

        Group("help", Tr.T("Помощь", "Жардам", "Help", "Yardım", "Yordam"));
        Add("kb", "KnowledgeBaseIcon", Tr.T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi"), TariffGate.CanUseService,
            () => OpenSection("kb", () => App.GetRequiredService<KnowledgeBaseWindow>()));
        Add("support", "RemoteSupportIcon", Tr.T("Тех. поддержка", "Тех колдоо", "Support", "Destek", "Texnik yordam"), TariffGate.CanUseService,
            () => OpenSection("support", () => App.GetRequiredService<RemoteSupportWindow>()));
        Add("logs", "ErrorLogIcon", Tr.T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali"), TariffGate.CanUseService,
            () => OpenSection("logs", () => App.GetRequiredService<LogsAndErrorsWindow>()));

        // Как в меню кассы: закрыть программу и выйти на рабочий стол (вход при этом сохраняется).
        // 2026-10-18, этап О2: группы «Система» в тёмной колонке нет — «Выйти» внизу неё; пункт остаётся для поиска Ctrl+K.
        Group("system", Tr.T("Система", "Система", "System", "Sistem", "Tizim"));
        Add("exit", "ExitIcon", Tr.T("Выйти на рабочий стол", "Иш столуна чыгуу", "Exit to desktop", "Masaüstüne çık", "Ish stoliga chiqish"), true,
            ExitToDesktop);

        UpdateNavHighlight();
        UpdateSiteOrdersBadge();
        UpdateRentalAlert();
        // 2026-10-18, этап О1: «Спросить Нур Советника» — только если пункт «Нур Советник» есть в меню (тариф, скрытые разделы).
        AskAiButton.IsVisible = _navButtons.ContainsKey("aiadvisor");
        ApplyNavFilter();
        UpdateRailDots();
        // 2026-10-18, ревью О2: плотность тёмной колонки по высоте окна (подсказки у групп — в режиме «только значки»).
        _railDensity = null;
        ApplyRailDensity();
    }

    // ------------------------------------------------------------------ свёрнутое меню
    // 2026-10-18, этап О2: «« / »» и ширина меню — OwnerShellWindow.Nav.cs (Collapse_Click, ExpandNav_Click, ApplySidebarLayout).

    // ------------------------------------------------------------------ разделы в окне

    // 2026-09-26, просьба владельца: разделы не отдельными окнами поверх, а «целое окно меняется»
    // при выборе пункта меню. Разделы — большие окна кассы (склад, финансы…) со своими диалогами,
    // сканером, Esc и загрузкой при открытии; переписывать их в панели — большой риск для кассы,
    // где они тоже работают. Поэтому окно раздела остаётся окном, но без рамки и принадлежит этому
    // окну: оно лежит ровно поверх правой части, двигается и меняет размер вместе с ним. Слева
    // остаётся меню, сверху — узкая полоса с названием раздела и кнопками окна. Раздел, открытый
    // раньше, при переходе прячется, а не закрывается: вернуться в него можно сразу, как было.
    // 2026-10-20: прячутся только несколько последних, остальные закрываются ради памяти (TrimHiddenSections).

    private sealed class Section
    {
        public required string Key { get; init; }
        public required Window Window { get; init; }

        /// <summary>Открыт до смены языка — при следующем переходе откроется заново.</summary>
        public bool Stale { get; set; }

        /// <summary>2026-10-20: номер последнего показа — кого из спрятанных закрыть первым (TrimHiddenSections).</summary>
        public long LastShown { get; set; }
    }

    /// <summary>2026-10-20, живой аудит (п. 9, память): к концу аудита ≈ 25 спрятанных разделов держали 933 МБ рабочего
    /// набора / 1 165 МБ private — рядом с кассой на ПК с 7,8 ГБ это много. Теперь спрятанными живут только несколько
    /// последних разделов, остальные закрываются штатно (Close — их Closed отписывает события и таймеры).</summary>
    private const int HiddenSectionsKept = 3;

    /// <summary>Тяжёлые разделы (предпросмотр сайта, графики аналитики) — закрываются вскоре после ухода из них.
    /// 2026-10-30 (оптимизация кассы, Б4): + «CRM» и «WhatsApp» (WebView2: 6–9 процессов Chromium, свёрнутый WhatsApp Web продолжал гонять JS и сокет).
    /// Вход в WhatsApp сохраняется в профиле WebView2; при возврате страница грузится 3–5 с.</summary>
    private static readonly HashSet<string> HeavySections = new() { "siteeditor", "analytics", "crm", "whatsapp" };

    /// <summary>Что вернуть разделу, открытому заново после закрытия: вкладку и кнопку периода.</summary>
    private sealed class SectionState
    {
        public int TabIndex { get; init; } = -1;
        public string? QuickDate { get; init; }
    }

    private readonly Dictionary<string, SectionState> _sectionStates = new();
    private long _sectionShowCounter;

    /// <summary>Разделы, которые сами переводятся на лету (калькулятор, база знаний), и сайт
    /// NurCRM — язык его страниц не наш. Остальные собирают часть надписей в коде один раз.</summary>
    private static readonly HashSet<string> LiveLanguageSections = new() { "calculator", "kb", "crm" };

    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, string> _navTitles = new();
    private Section? _activeSection;
    private bool _positioningSection;
    private bool _closingAllSections;

    private string TitleFor(Section section) =>
        _navTitles.TryGetValue(section.Key, out var title) ? title : section.Window.Title ?? section.Key;

    private void OpenSection(string key, Func<Window> create)
    {
        var existing = _sections.FirstOrDefault(s => s.Key == key);
        if (existing is { Stale: true })
        {
            // Сначала из списка — чтобы закрытие не вернуло окно к сводке и не нашлось снова.
            _sections.Remove(existing);
            existing.Window.Close();
            existing = null;
        }

        if (existing != null)
        {
            ShowSection(existing);
            return;
        }

        var window = create();
        PrepareEmbeddedWindow(window);
        UnsavedInputTracker.Attach(window); // 2026-10-20: «есть несохранённое» — см. SectionBusy
        var section = new Section { Key = key, Window = window };
        window.Closed += (_, _) => OnSectionClosed(section);
        _sections.Add(section);
        PosLogger.Log($"Owner app: раздел «{TitleFor(section)}» открыт.", "UI");
        ShowSection(section);
        // 2026-10-20: раздел закрывали ради памяти — возвращаем вкладку и период, как было бы у спрятанного окна.
        if (_sectionStates.Remove(SectionStateKey(section), out var state))
            RestoreSectionState(window, state);
    }

    /// <summary>Окно раздела без рамки, значка в панели задач и ограничений размера — его место
    /// и размер задаёт SyncSectionBounds. Свои кнопки «свернуть/развернуть» у разделов с
    /// нарисованной шапкой прячем: окно сворачивается и разворачивается целиком, кнопками сверху.</summary>
    private void PrepareEmbeddedWindow(Window window)
    {
        FullscreenHelper.MarkEmbedded(window); // 2026-10-27: встроенный раздел не разворачивается сам (FullscreenHelper.TryApplySectionWindowMode)
        window.SystemDecorations = SystemDecorations.None;
        window.ExtendClientAreaToDecorationsHint = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SizeToContent = SizeToContent.Manual;
        window.CanResize = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        window.WindowState = WindowState.Normal;

        // Раздел сам убирает повтор своего названия и свои кнопки окна (2026-09-27, «дублируется»).
        var ownerSection = window as IOwnerSection;
        ownerSection?.AsOwnerSection();
        // Esc закрывал раздел его кнопкой «Закрыть» (IsCancel), а она теперь скрыта — закрываем
        // напрямую. Разделы со своим обработчиком Esc (Склад, Продажи, Финансы…) срабатывают раньше.
        if (ownerSection != null)
            EscapeKey.Attach(window);
        // 2026-10-06: Ctrl+K из раздела — к поиску раздела в меню (OwnerShellWindow.NavSearch.cs).
        AttachNavHotkey(window);

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Поиск по классу — только для окон, которые не прячут свои кнопки сами: он задевал и
            // кнопки внутри окна (✕ карточки клиента тоже WindowControlButton).
            if (ownerSection == null)
            {
                foreach (var button in window.GetVisualDescendants().OfType<Button>())
                {
                    var classes = button.Classes;
                    if ((classes.Contains("WindowControlButton") && !classes.Contains("WindowCloseButton"))
                        || (classes.Contains("caption") && !classes.Contains("close")))
                        button.IsVisible = false;
                }
            }

            SyncSectionBounds();
        }, DispatcherPriority.Loaded);

        // Раздел сам себя не двигает и не разворачивает: перетаскивание за его шапку или двойной
        // щелчок возвращаем на место.
        window.PositionChanged += (_, _) =>
        {
            if (!_positioningSection && ReferenceEquals(_activeSection?.Window, window))
                Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Background);
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || _positioningSection || window.WindowState == WindowState.Normal)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Minimized && ReferenceEquals(_activeSection?.Window, window))
                {
                    window.WindowState = WindowState.Normal;
                    SyncSectionBounds();
                }
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>null — сводка. Иначе раздел занимает правую часть окна, шапка сжимается до
    /// полосы с названием раздела и кнопками окна.</summary>
    private void ShowSection(Section? section)
    {
        // 2026-10-18, вид «Бургер ☰»: переход в раздел закрывает выехавшее меню и убирает снимок раздела под ним.
        CloseBurgerQuietly();
        _activeSection = section;
        if (section != null)
            section.LastShown = ++_sectionShowCounter;
        // 2026-10-18, этап О2: в колонке пунктов — группа открытого раздела; раскрытая нажатием колонка сворачивается,
        // а до 1366 точек колонка пунктов сворачивается сама, пока открыт раздел (ширина SectionHost — ниже, UpdateLayout).
        if (GroupOfKey(section?.Key ?? "overview") is { } navGroup)
            _navGroup = navGroup;
        _navPeek = false;
        ApplySidebarLayout();
        PlaceRentalAlert();
        foreach (var other in _sections)
        {
            if (!ReferenceEquals(other, section) && other.Window.IsVisible)
                other.Window.Hide();
        }
        // 2026-10-20, память: лишние спрятанные разделы — закрыть.
        TrimHiddenSections();

        var overview = section == null;
        // 2026-10-19, вторая живая проверка: возврат на «Сводку» — карточка долгов из свежего кэша раздела (без запросов).
        if (overview)
            ApplyExactDebtsFromCache();
        OverviewScroll.IsVisible = overview;
        OverviewTools.IsVisible = overview;
        TitlePanel.IsVisible = overview;
        SectionTitleText.IsVisible = !overview;
        SectionHost.IsVisible = !overview;
        HeaderGrid.Margin = overview ? new Thickness(28, 12, 10, 4) : new Thickness(24, 4, 10, 4);
        AdjustHeaderForPhone(overview);
        // 2026-10-18, этап О2: шапка «Сводки» на узком окне — в две строки, в разделе — в одну (FitHeaderRows).
        FitHeaderTools();
        if (section != null)
            SectionTitleText.Text = TitleFor(section);
        UpdateNavHighlight();
        if (section == null)
        {
            if (_abcStale)
                _ = RefreshAbcAsync();
            return;
        }

        // Область раздела только что стала видимой — размеры у неё появятся после разметки.
        UpdateLayout();
        SyncSectionBounds();
        if (!section.Window.IsVisible)
            section.Window.Show(this);
        section.Window.Activate();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    private void UpdateNavHighlight()
    {
        var activeKey = _activeSection?.Key ?? "overview";
        foreach (var (key, button) in _navButtons)
            button.Classes.Set("active", key == activeKey);
        // 2026-10-06: открытый раздел виден и в свёрнутой группе.
        ApplyNavFilter();
    }

    /// <summary>Кладёт окно открытого раздела ровно на правую часть окна.</summary>
    private void SyncSectionBounds()
    {
        var section = _activeSection;
        if (section == null || !IsVisible || WindowState == WindowState.Minimized || !SectionHost.IsVisible)
            return;

        var size = SectionHost.Bounds.Size;
        if (size.Width < 50 || size.Height < 50)
            return;

        var topLeft = SectionHost.PointToScreen(new Point(0, 0));
        var window = section.Window;
        _positioningSection = true;
        try
        {
            if (window.WindowState != WindowState.Normal)
                window.WindowState = WindowState.Normal;
            if (window.Position != topLeft)
                window.Position = topLeft;
            if (Math.Abs(window.Width - size.Width) > 0.5)
                window.Width = size.Width;
            if (Math.Abs(window.Height - size.Height) > 0.5)
                window.Height = size.Height;
        }
        finally
        {
            _positioningSection = false;
        }
    }

    /// <summary>Раздел закрылся сам (своя кнопка «закрыть», Esc) — возвращаемся к сводке.</summary>
    private void OnSectionClosed(Section section)
    {
        if (!_sections.Remove(section) || _closingAllSections || !ReferenceEquals(_activeSection, section))
            return;
        ShowSection(null);
    }

    /// <summary>2026-10-20, память (живой аудит п. 9): спрятанными держим только HiddenSectionsKept последних разделов,
    /// тяжёлые (HeavySections) закрываем через несколько секунд после ухода. Не закрываем раздел с открытым окном поверх
    /// (диалог приёмки, карточка…) и раздел, который сам просит остаться (IOwnerSectionKeepAlive: звонок, несохранённая
    /// приёмка) — такие и в счёт не идут. Раздел после смены языка (Stale) всё равно открылся бы заново — закрываем сразу.</summary>
    private void TrimHiddenSections()
    {
        if (_closingAllSections)
            return;
        var kept = 0;
        foreach (var section in _sections.Where(s => !ReferenceEquals(s, _activeSection)).OrderByDescending(s => s.LastShown).ToList())
        {
            if (SectionBusy(section))
                continue;
            if (section.Stale)
                CloseHiddenSection(section, "после смены языка");
            else if (HeavySections.Contains(section.Key))
                ScheduleHeavySectionClose(section);
            else if (++kept > HiddenSectionsKept)
                CloseHiddenSection(section, "давно не открывали");
        }
    }

    private static bool SectionBusy(Section section) =>
        section.Window.OwnedWindows.Count > 0
        || section.Window is IOwnerSectionKeepAlive { KeepAliveWhenHidden: true }
        // 2026-10-20, доработка: несохранённый ввод (Настройки, Калькуляция, черновик редактора…) — раздел не закрываем.
        || ((section.Window as IOwnerSection)?.HasUnsavedChanges ?? UnsavedInputTracker.IsDirty(section.Window));

    /// <summary>Не сразу: «Редактор сайта» отправляет черновик через 0,8 с после правки, Нур Советник открывает редактор
    /// на одну голосовую команду и возвращается — пусть успеет. Вернулись в раздел за это время — не закрываем.</summary>
    private void ScheduleHeavySectionClose(Section section)
    {
        var shown = section.LastShown;
        DispatcherTimer.RunOnce(() =>
        {
            if (section.LastShown == shown && _sections.Contains(section) && !ReferenceEquals(_activeSection, section)
                && !section.Window.IsVisible && !SectionBusy(section))
                CloseHiddenSection(section, "тяжёлый раздел");
        }, TimeSpan.FromSeconds(5));
    }

    private void CloseHiddenSection(Section section, string why)
    {
        try
        {
            if (CaptureSectionState(section.Window) is { } state)
                _sectionStates[SectionStateKey(section)] = state;
            PosLogger.Log($"Owner app: спрятанный раздел «{TitleFor(section)}» закрыт ({why}) — освобождаем память.", "UI");
            // Из списка его убирает Closed → OnSectionClosed (раздел не открыт — к сводке не возвращает).
            section.Window.Close();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: hidden section close failed: {ex.Message}", "WARNING");
        }
    }

    /// <summary>У «Продаж и финансов» состав вкладок зависит от прав (Tag окна) — состояние храним для каждого состава.</summary>
    private static string SectionStateKey(Section section) => section.Key + "|" + (section.Window.Tag as string);

    /// <summary>Главная вкладка (первый TabControl окна) и кнопка периода «Сегодня/Вчера/Неделя/Месяц» (группа QuickDate).</summary>
    private static SectionState? CaptureSectionState(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        var quick = window.GetVisualDescendants().OfType<RadioButton>()
            .FirstOrDefault(r => r.GroupName == "QuickDate" && r.IsChecked == true)?.Tag as string;
        var tabIndex = tabs?.SelectedIndex ?? -1;
        return tabIndex <= 0 && (quick is null or "Today") ? null : new SectionState { TabIndex = tabIndex, QuickDate = quick };
    }

    /// <summary>Вкладка — сразу (до SelectSalesFinanceTab/ShowStockTab: явный переход главнее), период — нажатием кнопки
    /// после первой загрузки окна, как если бы его нажал владелец. «Спец. дата» не возвращаем — она открывает окно дат.</summary>
    private static void RestoreSectionState(Window window, SectionState state)
    {
        try
        {
            if (state.TabIndex > 0 && window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { } tabs
                && state.TabIndex < tabs.ItemCount && tabs.Items[state.TabIndex] is TabItem { IsVisible: true, IsEnabled: true })
                tabs.SelectedIndex = state.TabIndex;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: section tab restore failed: {ex.Message}", "WARNING");
        }

        if (state.QuickDate is null or "Today" or "Custom")
            return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!window.IsVisible)
                    return;
                var button = window.GetVisualDescendants().OfType<RadioButton>()
                    .FirstOrDefault(r => r.GroupName == "QuickDate" && Equals(r.Tag, state.QuickDate) && r.IsEffectivelyVisible && r.IsEffectivelyEnabled);
                if (button is null || button.IsChecked == true)
                    return;
                button.IsChecked = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section period restore failed: {ex.Message}", "WARNING");
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>2026-09-26, «баг с языком»: раздел, открытый до смены языка, оставался на старом
    /// (меню по-узбекски, «Эмгек акы» по-кыргызски). Спрятанные разделы закрываем — при переходе
    /// они откроются уже на новом языке; открытый сейчас (обычно «Настройки», где язык и меняют)
    /// не трогаем, а помечаем — он откроется заново при следующем переходе в него.</summary>
    private void RebuildSectionsForLanguage()
    {
        foreach (var section in _sections.ToList())
        {
            if (LiveLanguageSections.Contains(section.Key))
                continue;
            if (ReferenceEquals(section, _activeSection))
            {
                section.Stale = true;
                continue;
            }

            _sections.Remove(section);
            try
            {
                // 2026-10-20: вкладку и период — тоже вернуть при новом открытии (как у разделов, закрытых ради памяти).
                if (CaptureSectionState(section.Window) is { } state)
                    _sectionStates[SectionStateKey(section)] = state;
                section.Window.Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section close on language change failed: {ex.Message}", "WARNING");
            }
        }
    }

    private void CloseAllSections()
    {
        _closingAllSections = true;
        foreach (var section in _sections.ToList())
        {
            try
            {
                section.Window.Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section close failed: {ex.Message}", "WARNING");
            }
        }
    }

    private bool Authorize(string permission)
    {
        if (App.GetRequiredService<IPermissionService>().HasPermission(permission))
            return true;
        // 2026-10-30: сессия недействительна — права не подтвердить, это не «нет доступа»: вместо отказа — «Сессия истекла. Войдите снова».
        if (SessionExpiryMonitor.IsExpired)
        {
            PosLogger.Log($"Owner app: section {permission} — сессия недействительна, показываю «Войдите снова».", "AUTH");
            _ = NurMarketKassa.AvaloniaHost.Services.SessionExpiredPrompt.AskAsync(this, SignOutAsync);
            return false;
        }
        PosLogger.Log($"Owner app: permission denied: {permission}", "WARNING");
        // 2026-10-01: подсказка, какую галочку включить на сайте (названия — как на сайте NurCRM).
        var siteRight = SiteRightName(permission);
        var hint = siteRight is null ? "" : Environment.NewLine + Environment.NewLine + Tr.T(
            $"Доступ даёт владелец на сайте NurCRM: Сотрудники → сотрудник → доступ «{siteRight}». После изменения перезапустите программу.",
            $"Уруксатты ээси NurCRM сайтында берет: Кызматкерлер → кызматкер → «{siteRight}» уруксаты. Өзгөрткөндөн кийин программаны кайра ачыңыз.",
            $"The owner grants it on the NurCRM website: Employees → employee → “{siteRight}” access. Restart the program after the change.",
            $"Erişimi işletme sahibi NurCRM sitesinde verir: Çalışanlar → çalışan → «{siteRight}» erişimi. Değişiklikten sonra programı yeniden başlatın.",
            $"Ruxsatni ega NurCRM saytida beradi: Xodimlar → xodim → «{siteRight}» ruxsati. O'zgartirgandan keyin dasturni qayta ishga tushiring.");
        PosMessageBox.Show(this,
            Tr.T("Недостаточно прав для этого раздела.", "Бул бөлүм үчүн укук жетишсиз.", "You don't have permission to open this section.",
                "Bu bölüm için yetkiniz yetersiz.", "Bu bo'lim uchun huquq yetarli emas.") + hint,
            Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return false;
    }

    /// <summary>Название галочки доступа на сайте NurCRM (Сотрудники → доступы).</summary>
    private static string? SiteRightName(string permission) => permission switch
    {
        PosPermissions.ViewProducts => "Склад",
        PosPermissions.ViewAnalytics => "Аналитика",
        PosPermissions.ViewClients => "Клиенты",
        PosPermissions.ViewOrders => "Заказы",
        PosPermissions.ViewSettings => "Настройки",
        PosPermissions.ViewProcurement => "Закупки",
        PosPermissions.ViewSupplier => "Поставщики",
        PosPermissions.EmployeeReturn => "Возврат продаж сотрудником",
        _ => null,
    };

    // ------------------------------------------------------------------ сводка

    private (DateTime From, DateTime To) CurrentRange()
    {
        var today = DateTime.Today;
        return _period switch
        {
            // 2026-10-20: аудит владельца — «Неделя» была с понедельника (20 447 при 97 242 за 7 дней в других окнах).
            // Теперь общее правило PeriodRange: неделя = 7 дней включая сегодня, месяц — с 1-го числа.
            "week" => (PeriodRange.WeekFrom(today), today),
            "month" => (PeriodRange.MonthFrom(today), today),
            "custom" => (_customFrom.Date, _customTo.Date),
            _ => (today, today),
        };
    }

    /// <summary>С чем сравнивать: вчера; те же дни прошлой недели; те же числа прошлого месяца.</summary>
    private (DateTime From, DateTime To) PreviousRange(DateTime from, DateTime to)
    {
        var days = (to - from).Days;
        switch (_period)
        {
            case "week":
                return (from.AddDays(-7), from.AddDays(-7 + days));
            case "month":
            {
                var prevFrom = from.AddMonths(-1);
                var lastDay = DateTime.DaysInMonth(prevFrom.Year, prevFrom.Month) - 1;
                return (prevFrom, prevFrom.AddDays(Math.Min(days, lastDay)));
            }
            case "custom":
                // Свои даты — с таким же по длине периодом прямо перед ними.
                return (from.AddDays(-(days + 1)), from.AddDays(-1));
            default:
                return (from.AddDays(-1), to.AddDays(-1));
        }
    }

    /// <summary>Карточки прошлого периода для сравнения: итоги периода сервера (analytics/market/summary/, ТЗ ч.12, п. 3.7),
    /// а если сервер их не отдаёт — карточки полного отчёта, как раньше.</summary>
    private static async Task<JsonElement?> PreviousCardsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            if (await App.SalesApi.MarketSummaryCardsAsync(new[] { (from, to) }, ct).ConfigureAwait(true) is { Count: 1 } cards)
                return cards[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: итоги прошлого периода не получены ({ex.Message}) — полный отчёт.", "WARNING");
        }
        var report = await App.SalesApi.MarketSalesReportAsync(from, to, ct).ConfigureAwait(true);
        return report.ValueKind == JsonValueKind.Object && report.TryGetProperty("cards", out var pc) ? pc.Clone() : null;
    }

    /// <summary>График по дням самого периода (месяц, свои даты), иначе — последние 7 дней.</summary>
    private bool ChartIsPeriod => _period is "month" or "custom";

    private string CompareHint() => _period switch
    {
        "week" => Tr.T("к прошлой неделе", "өткөн жумага карата", "vs last week", "geçen haftaya göre", "o'tgan haftaga nisbatan"),
        "month" => Tr.T("к прошлому месяцу", "өткөн айга карата", "vs last month", "geçen aya göre", "o'tgan oyga nisbatan"),
        "custom" => Tr.T("к предыдущему периоду", "мурунку мезгилге карата", "vs previous period", "önceki döneme göre", "oldingi davrga nisbatan"),
        _ => Tr.T("к вчера", "кечээге карата", "vs yesterday", "düne göre", "kechaga nisbatan"),
    };

    /// <summary>Сервер недоступен: последние полученные данные (плюс продажи касс по локальной
    /// сети) с пометкой, на какое время они. 2026-09-29: вынесено из RefreshAsync; в аварии
    /// сервера (ServerOutageMonitor) — «Сервер не отвечает · данные на HH:mm» вместо «Нет связи».</summary>
    private void ShowOverviewStale()
    {
        UseBrush(LiveDot, Shape.FillProperty, "BrushWarning");
        _offline = true;
        if (!ApplyLanSales(online: false))
            UpdatedText.Text = NurMarketKassa.Services.ServerOutageMonitor.IsOutage
                ? NurMarketKassa.Services.ServerOutageMonitor.StaleDataNote(_lastSuccess)
                : _lastSuccess is { } at
                    ? Tr.T($"Нет связи · данные на {at:HH:mm}", $"Байланыш жок · маалымат {at:HH:mm} боюнча", $"Offline · data as of {at:HH:mm}",
                        $"Bağlantı yok · veriler {at:HH:mm} itibarıyla", $"Aloqa yo'q · ma'lumotlar {at:HH:mm} holatiga ko'ra")
                    : Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q");
        // 2026-10-18, ревью О1: каталог локальный — товары на исходе видны и без связи (иначе «Требует внимания» пустой).
        ApplyLowStock();
    }

    /// <summary>2026-10-04, п. 7: «Сводка» видна владельцу — окно активно, не свёрнуто и не закрыто
    /// открытым разделом (тогда сводка спрятана, см. OverviewScroll).</summary>
    private bool IsOverviewShown =>
        IsVisible && IsActive && WindowState != WindowState.Minimized && OverviewScroll.IsVisible;

    /// <summary>Обновление по таймеру и при возврате в окно — только когда «Сводку» видно (см. RefreshInterval).</summary>
    private Task RefreshWhenShownAsync()
    {
        if (IsOverviewShown)
            return RefreshAsync();
        // 2026-10-22 (аудит 1.17.59, П-З): окно владельца видно, но не активно (владелец работает в кассе рядом) — сводка
        // раньше стояла («Обновлено в 07:10» в 07:45, «Чеки 0» при прошедших возвратах). Теперь обновляется и так, но реже —
        // раз в 5 минут, одним запросом сводки, без пересборки окна; не во время ввода текста.
        // 2026-10-23 (аудит, круг 8, В-6): интервал — RefreshInterval (60 с; допуск 5 с на дрожание таймера, иначе
        // обновление пропускалось бы до следующего тика), а «не во время ввода» — только если печатали менее 15 с назад
        // (раньше любое поле с фокусом, даже пустое «Найти раздел», останавливало обновление насовсем).
        var visibleInactive = IsVisible && WindowState != WindowState.Minimized && OverviewScroll.IsVisible && !IsActive;
        if (visibleInactive && DateTime.UtcNow - _lastRefreshStartedUtc >= RefreshInterval - TimeSpan.FromSeconds(5)
            && !(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox
                 && DateTime.UtcNow - _lastKeyInputUtc < TypingQuietPeriod))
            return RefreshAsync();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync(bool forceChart = false)
    {
        if (_refreshing || _loggingOut || _cts.IsCancellationRequested)
            return;

        // 2026-09-29: сервер не отвечает — «Сводку» не перезапрашиваем каждые 20 с (таймауты и
        // лишняя нагрузка на лежащий сервер), показываем последние данные с пометкой времени.
        // Проверку связи ведёт ServerOutageMonitor; после восстановления таймер обновит сам.
        if (NurMarketKassa.Services.ServerOutageMonitor.IsOutage)
        {
            ShowOverviewStale();
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        _lastRefreshStartedUtc = DateTime.UtcNow;
        string? startedKey = null;
        try
        {
            var (from, to) = CurrentRange();
            startedKey = RangeKey(from, to);
            var ct = _cts.Token;
            var fetchStartedUtc = DateTime.UtcNow;

            // 2026-09-28, «аналитика грузится долго»: все запросы «Сводки» (период, прошлый период,
            // график, последние чеки, возвраты) уходят одновременно, а не один за другим — ожидание
            // равно самому долгому из них, а не их сумме.
            var (prevFrom, prevTo) = PreviousRange(from, to);
            var compareKey = $"{_period}:{prevFrom:yyyyMMdd}:{prevTo:yyyyMMdd}";
            // График: для «сегодня» и «недели» — последние 7 дней, для месяца — дни месяца (они уже
            // есть в отчёте за период).
            var (chartFrom, chartTo) = !ChartIsPeriod ? (DateTime.Today.AddDays(-6), DateTime.Today) : (from, to);

            var reportTask = App.SalesApi.MarketSalesReportAsync(from, to, ct);
            // 2026-10-06, ТЗ ч.12, п. 3.7: для сравнения нужны только карточки — итоги периода (summary), не весь отчёт.
            // 2026-10-18, ревью О1: прошлый период другой — старые карточки сравнения не показывать, пока не пришли новые.
            if (_compareKey != compareKey)
                _compareCards = null;
            var previousTask = _compareKey != compareKey ? PreviousCardsAsync(prevFrom, prevTo, ct) : null;
            // 2026-10-04, п. 7: график 7 дней — раз в 5 минут (и сразу по «Обновить» / смене дня).
            var chartKey = $"{chartFrom:yyyyMMdd}:{chartTo:yyyyMMdd}";
            var chartCached = !forceChart
                && _chartCache is not null
                && _chartCacheKey == chartKey
                && DateTime.UtcNow - _chartCacheAtUtc < ChartRefreshInterval;
            var chartTask = !ChartIsPeriod && !chartCached ? App.SalesApi.MarketSalesReportAsync(chartFrom, chartTo, ct) : null;
            var rowsTask = App.SalesApi.PosSalesListAsync(1, RecentRows, null, ct, dateFrom: from, dateToExclusive: to.AddDays(1));
            // 2026-09-28 (BE-09): возвраты периода — из списка возвратов сервера (null — не
            // ответил, тогда из «Документы → Возврат продажи» отчёта, как раньше).
            var returnsTask = NurMarketKassa.Services.Api.NurCrmReportsApi.ReturnsTotalsAsync(from, to, ct);
            // 2026-10-06: «Сегодня» сравнивается со вчера до того же часа (OwnerShellWindow.SameTime.cs).
            var yesterdayTask = EnsureYesterdaySalesAsync(ct);
            // 2026-10-06, лог владельца (17:12, сбой SSL): если отчёт падает раньше, остальные запросы никто не ждёт — их
            // ошибки уходили в «UnobservedTaskException». Ошибку помечаем просмотренной; await ниже бросает её как раньше.
            foreach (var started in new Task?[] { previousTask, chartTask, rowsTask, returnsTask, yesterdayTask })
                _ = started?.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            var report = await reportTask.ConfigureAwait(true);

            if (previousTask != null)
            {
                _compareCards = await previousTask.ConfigureAwait(true);
                _compareKey = compareKey;
            }

            var chartSource = chartTask != null
                ? await chartTask.ConfigureAwait(true)
                : !ChartIsPeriod && chartCached ? _chartCache!.Value : report;
            if (chartTask != null)
            {
                _chartCache = chartSource.Clone();
                _chartCacheKey = chartKey;
                _chartCacheAtUtc = DateTime.UtcNow;
            }

            var rows = await rowsTask.ConfigureAwait(true);

            _lastReturns = await returnsTask.ConfigureAwait(true);
            _lastReturnsKey = RangeKey(from, to);
            await yesterdayTask.ConfigureAwait(true);

            // Отчёт сервера запоминается как есть: к нему добавляются чеки касс, которые сервер
            // ещё не видит (касса без интернета), — и сейчас, и когда связь пропадёт.
            _lastReport = report.Clone();
            _lastChart = chartSource.Clone();
            _lastRows = rows.Select(r => r.Clone()).ToList();
            _lastReportKey = RangeKey(from, to);
            _lastFetchStartedUtc = fetchStartedUtc;
            _lastSuccess = DateTime.Now;
            _offline = false;
            ApplyLanSales(online: true);
            // 2026-10-05, ТЗ часть 7, п. 2.5: карточка «План продаж на месяц» (обновляется не чаще раза в 5 минут).
            _ = RefreshSalesPlanAsync();
            // 2026-10-05, владелец: «где в сводке долги??» — карточка «Долги клиентов» (не чаще раза в 2 минуты).
            _ = RefreshDebtsCardAsync();
            // 2026-10-05, запрос NurCRM: заметка «Ваш магазин в приложении NurCRM — бесплатно», пока магазин не подключён.
            _ = RefreshAppShopNoteAsync();
            if (DateTime.UtcNow - _abcRefreshedUtc > TimeSpan.FromMinutes(1))
                RefreshAbcWhenVisible();

            UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");
            ToolTip.SetTip(UpdatedText, Tr.T($"Обновляется само каждые {RefreshInterval.TotalSeconds:0} с",
                $"Ар {RefreshInterval.TotalSeconds:0} с сайын өзү жаңырат", $"Auto-refreshes every {RefreshInterval.TotalSeconds:0} s",
                $"Her {RefreshInterval.TotalSeconds:0} saniyede bir otomatik yenilenir", $"Har {RefreshInterval.TotalSeconds:0} soniyada avtomatik yangilanadi"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: overview refresh failed: {ex.Message}", "WARNING");
            ShowOverviewStale();
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            // 2026-10-30 (стресс-тест «Отчёты», гонка периодов): пока шла загрузка, владелец нажал другой период — его RefreshAsync вернулся
            // сразу (идёт предыдущая), а предыдущая закончилась для прежнего периода: на кнопке новый период, цифры — прежнего, до таймера
            // (до минуты). Теперь, если период сменился за время загрузки, обновляем ещё раз — уже по нему.
            if (startedKey != null && !_loggingOut && !_cts.IsCancellationRequested)
            {
                var (nowFrom, nowTo) = CurrentRange();
                if (RangeKey(nowFrom, nowTo) != startedKey)
                    _ = RefreshAsync();
            }
        }
    }

    // ── Без интернета: продажи касс по локальной сети ────────────────────────────────

    // Последний отчёт сервера за текущий период и когда его начали грузить (по своим часам) —
    // к нему добавляются продажи касс, которых в нём ещё нет.
    private JsonElement? _lastReport;
    private JsonElement? _lastChart;
    private List<JsonElement>? _lastRows;
    private string? _lastReportKey;
    // Возвраты периода по списку возвратов сервера (2026-09-28, BE-09) и для какого периода.
    private (int Count, decimal Sum)? _lastReturns;
    private string? _lastReturnsKey;
    private DateTime _lastFetchStartedUtc;
    private bool _offline;

    private string RangeKey(DateTime from, DateTime to) => $"{_period}:{from:yyyyMMdd}:{to:yyyyMMdd}";

    private (DateTime From, DateTime To) ChartRange(DateTime from, DateTime to) =>
        !ChartIsPeriod ? (DateTime.Today.AddDays(-6), DateTime.Today) : (from, to);

    private void OnLanPeerData() => Dispatcher.UIThread.Post(() =>
    {
        if (!_refreshing && !_loggingOut)
            ApplyLanSales(online: !_offline);
    });

    /// <summary>
    /// Отчёт сервера плюс продажи касс из локальной сети, которых в нём нет.
    /// online — отчёт только что получен: добавляются только чеки, которые кассы ещё не
    /// отправили на сервер (касса без интернета). Иначе сервер недоступен: продажа уже в
    /// последнем отчёте, если владелец узнал, что она на сервере, раньше, чем отчёт начали
    /// грузить; если отчёта за этот период не было — только продажи касс (прибыль и возвраты
    /// тогда неизвестны). false — показать нечего.
    /// </summary>
    private bool ApplyLanSales(bool online)
    {
        var (from, to) = CurrentRange();
        var (chartFrom, chartTo) = ChartRange(from, to);
        var hasBase = _lastReportKey == RangeKey(from, to) && _lastReport is not null;
        if (online && !hasBase)
            return false;

        List<LanPeerSale> peerSales;
        try
        {
            peerSales = LanJournal.ReadPeerSales(chartFrom.AddDays(-1).ToUniversalTime());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: продажи касс по локальной сети не прочитаны: {ex.Message}", "LAN");
            peerSales = new List<LanPeerSale>();
        }

        var fetchStarted = _lastFetchStartedUtc;
        var extra = online
            ? peerSales.Where(s => !s.IsOnServer).ToList()
            : peerSales.Where(s => !(hasBase && s.KnownOnServerSinceUtc is { } known && known <= fetchStarted)).ToList();
        var inPeriod = extra.Where(s => InDays(s, from, to)).ToList();
        var inChart = extra.Where(s => InDays(s, chartFrom, chartTo)).ToList();
        if (!hasBase && inPeriod.Count == 0 && inChart.Count == 0)
            return false;

        var report = MergeReport(hasBase ? _lastReport : null, inPeriod);
        var chartBase = hasBase ? (ChartIsPeriod ? _lastReport : _lastChart) : null;
        var chart = MergeDynamics(chartBase, inChart);
        var rows = new List<JsonElement>();
        if (hasBase && _lastRows != null)
            rows.AddRange(_lastRows);
        rows.AddRange(inPeriod.Select(LanSaleRow));
        rows = rows
            .OrderByDescending(r => DateTimeOffset.TryParse(Str(r, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue)
            .Take(RecentRows)
            .ToList();

        ApplyCards(report);
        if (!hasBase)
        {
            // Себестоимость кассы не передают — без отчёта сервера прибыль неизвестна, а не ноль.
            ProfitValue.Inlines = null;
            ProfitValue.Text = "—";
            // 2026-10-18, ревью О1: и сравнение прибыли неизвестно (было «▼ 100 % …» под «—»).
            ProfitDelta.Inlines = null;
            ProfitDelta.Text = "—";
        }
        ApplyChart(chart, chartFrom, chartTo);
        ApplyPayments(report);
        ApplyTopProducts(report);
        ApplyRecent(rows);

        var count = inPeriod.Count;
        if (online)
        {
            var now = _lastSuccess ?? DateTime.Now;
            UpdatedText.Text = Tr.T($"Обновлено в {now:HH:mm}", $"{now:HH:mm} жаңыртылды", $"Updated at {now:HH:mm}",
                $"Güncellendi: {now:HH:mm}", $"Yangilandi: {now:HH:mm}");
            if (count > 0)
                UpdatedText.Text += Tr.T($" · ещё {count} чек. касс без интернета", $" · интернетсиз кассалардан дагы {count} чек",
                    $" · plus {count} receipts from tills without internet", $" · internetsiz kasalardan {count} fiş daha",
                    $" · internetsiz kassalardan yana {count} ta chek");
            return true;
        }

        UpdatedText.Text = hasBase && _lastSuccess is { } at
            ? Tr.T($"Нет связи · данные на {at:HH:mm} + {count} чек. с касс по локальной сети",
                $"Байланыш жок · маалымат {at:HH:mm} боюнча + жергиликтүү тармактагы кассалардан {count} чек",
                $"Offline · data as of {at:HH:mm} + {count} receipts from tills on the local network",
                $"Bağlantı yok · veriler {at:HH:mm} itibarıyla + yerel ağdaki kasalardan {count} fiş",
                $"Aloqa yo'q · ma'lumotlar {at:HH:mm} holatiga ko'ra + mahalliy tarmoqdagi kassalardan {count} ta chek")
            : Tr.T($"Нет связи · по данным касс в локальной сети ({count} чек.)",
                $"Байланыш жок · жергиликтүү тармактагы кассалардын маалыматы боюнча ({count} чек)",
                $"Offline · from tills on the local network ({count} receipts)",
                $"Bağlantı yok · yerel ağdaki kasaların verilerine göre ({count} fiş)",
                $"Aloqa yo'q · mahalliy tarmoqdagi kassalar ma'lumotlari bo'yicha ({count} ta chek)");
        return true;
    }

    private static bool InDays(LanPeerSale s, DateTime from, DateTime to)
    {
        var day = s.Sale.CreatedAtUtc.ToLocalTime().Date;
        return day >= from.Date && day <= to.Date;
    }

    private static double NodeNum(JsonNode? node)
    {
        if (node is not JsonValue v)
            return 0;
        if (v.TryGetValue<double>(out var d))
            return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static JsonObject Obj(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o)
            return o;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray Arr(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray a)
            return a;
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    /// <summary>Карточки, способы оплаты и топ товаров отчёта + продажи касс.</summary>
    private static JsonElement MergeReport(JsonElement? baseReport, List<LanPeerSale> sales)
    {
        var root = baseReport is { ValueKind: JsonValueKind.Object } b
            ? JsonNode.Parse(b.GetRawText())!.AsObject()
            : new JsonObject();

        var cards = Obj(root, "cards");
        var revenue = NodeNum(cards["revenue"]) + sales.Sum(s => s.Sale.Total);
        var checks = NodeNum(cards["transactions"]) + sales.Count;
        cards["revenue"] = revenue;
        cards["transactions"] = checks;
        cards["avg_check"] = checks > 0 ? revenue / checks : 0;

        var methods = Arr(Obj(root, "charts"), "payment_methods");
        foreach (var group in sales.GroupBy(s => string.IsNullOrWhiteSpace(s.Sale.PaymentMethod) ? "cash" : s.Sale.PaymentMethod))
        {
            var row = methods.OfType<JsonObject>().FirstOrDefault(m =>
                string.Equals(m["method"]?.ToString(), group.Key, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                row = new JsonObject { ["method"] = group.Key, ["count"] = 0.0, ["total"] = 0.0 };
                methods.Add(row);
            }
            row["count"] = NodeNum(row["count"]) + group.Count();
            row["total"] = NodeNum(row["total"]) + group.Sum(s => s.Sale.Total);
        }

        var top = Arr(Obj(root, "tables"), "top_products");
        foreach (var group in sales.SelectMany(s => s.Sale.Items).Where(i => !string.IsNullOrWhiteSpace(i.Name)).GroupBy(i => i.Name))
        {
            var row = top.OfType<JsonObject>().FirstOrDefault(p => string.Equals(p["name"]?.ToString(), group.Key, StringComparison.Ordinal));
            if (row == null)
            {
                row = new JsonObject { ["name"] = group.Key, ["sold"] = 0.0, ["revenue"] = 0.0 };
                top.Add(row);
            }
            row["sold"] = NodeNum(row["sold"]) + group.Sum(i => i.Qty);
            row["revenue"] = NodeNum(row["revenue"]) + group.Sum(i => i.LineTotal);
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Выручка по дням для графика + продажи касс.</summary>
    private static JsonElement MergeDynamics(JsonElement? baseReport, List<LanPeerSale> sales)
    {
        var root = baseReport is { ValueKind: JsonValueKind.Object } b
            ? JsonNode.Parse(b.GetRawText())!.AsObject()
            : new JsonObject();
        var points = Arr(Obj(root, "charts"), "sales_dynamics");
        foreach (var group in sales.GroupBy(s => s.Sale.CreatedAtUtc.ToLocalTime().Date))
        {
            var date = group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var point = points.OfType<JsonObject>().FirstOrDefault(p => p["date"]?.ToString() == date);
            if (point == null)
            {
                point = new JsonObject { ["date"] = date, ["value"] = 0.0 };
                points.Add(point);
            }
            point["value"] = NodeNum(point["value"]) + group.Sum(s => s.Sale.Total);
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>Строка «последних продаж» в том же виде, что приходит с сервера.</summary>
    private static JsonElement LanSaleRow(LanPeerSale s)
    {
        var place = s.Sale.CashboxName is { Length: > 0 } name ? name : s.DeviceName;
        if (!s.IsOnServer)
            place += " · " + Tr.T("ещё не на сервере", "серверге али жете элек", "not on the server yet", "henüz sunucuda değil", "hali serverda emas");
        var row = new JsonObject
        {
            ["status"] = "paid",
            ["payment_method"] = s.Sale.PaymentMethod,
            ["total"] = s.Sale.Total,
            ["created_at"] = s.Sale.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            ["first_item_name"] = s.Sale.Items.FirstOrDefault()?.Name ?? "",
            // Для «+ ещё N» в «Последних продажах» (2026-09-28): состав чека кассы уже известен.
            ["items_count"] = s.Sale.Items.Count,
            ["user_display"] = s.Sale.CashierName ?? "",
            ["cashbox_name"] = place,
        };
        return JsonSerializer.SerializeToElement(row);
    }

    private void ApplyCards(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("cards", out var cards))
            return;

        var prev = _compareCards;
        var hint = CompareHint();
        double? prevRevenue = prev is { } p1 ? Num(p1, "revenue") : null;
        double? prevChecks = prev is { } p2 ? Num(p2, "transactions") : null;
        double? prevAvg = prev is { } p3 ? Num(p3, "avg_check") : null;
        double? prevProfit = prev is { } p4 ? Num(p4, "gross_profit") : null;
        // 2026-10-06: «Сегодня» — со вчера до того же часа, а не с целым вчерашним днём (OwnerShellWindow.SameTime.cs).
        var sameTime = false;
        if (prev != null && YesterdayShareNow() is { } share)
        {
            prevRevenue *= share.Revenue;
            prevChecks *= share.Checks;
            prevAvg = prevChecks is > 0 ? prevRevenue / prevChecks : null;
            prevProfit *= share.Revenue;
            hint = Tr.T("к вчера на это время", "кечээки ушул убакытка карата", "vs yesterday at this time", "dünün aynı saatine göre", "kechaning shu vaqtiga nisbatan");
            sameTime = true;
        }
        // 2026-10-18, этап О1, «—» во всех четырёх карточках: прошлый период получен, но чеков в нём (к этому часу) не было —
        // сравнивать не с чем (у NBS 18.10 в 12:02 все 22 вчерашних чека были после 16:00). Теперь так и написано, а не «—».
        var noneBefore = prev != null && prevChecks is not > 0 ? NoSalesBeforeText(sameTime) : null;
        // 2026-10-21 (пакет 2, п. 85, аудит 18.10: «вчера к этому времени продаж не было» 4 раза): сравнивать не с чем — одна
        // строка под плитками, а строки сравнения в плитках скрыты; то же, если прошлый период не получен (было «— к вчера» ×4).
        var sharedNote = noneBefore ?? (prev == null ? NoCompareText() : null);
        KpiCompareNote.Text = sharedNote;
        KpiCompareNote.IsVisible = sharedNote != null;
        RevenueDelta.IsVisible = ChecksDelta.IsVisible = AvgDelta.IsVisible = ProfitDelta.IsVisible = sharedNote == null;

        SetMoney(RevenueValue, Num(cards, "revenue"));
        SetDelta(RevenueDelta, Num(cards, "revenue"), prevRevenue, hint, noneBefore);

        ChecksValue.Inlines = null;
        ChecksValue.Text = Num(cards, "transactions").ToString("N0", UiCulture);
        SetDelta(ChecksDelta, Num(cards, "transactions"), prevChecks, hint, noneBefore);

        SetMoney(AvgValue, Num(cards, "avg_check"));
        SetDelta(AvgDelta, Num(cards, "avg_check"), prevAvg, hint, noneBefore);

        SetMoney(ProfitValue, Num(cards, "gross_profit"));
        var margin = Num(cards, "margin_percent");
        _lastMargin = margin;
        // 2026-10-18, этап О1: маржа — в названии («Валовая прибыль · 21,2 %», макет Owner), а строка под числом —
        // сравнение, как у остальных показателей (раньше при марже сравнения у прибыли не было).
        ProfitLabel.Text = Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda")
                           + (margin != 0 ? " · " + Percent(margin) : "");
        SetDelta(ProfitDelta, Num(cards, "gross_profit"), prevProfit, hint, noneBefore);

        // 2026-10-05: те же цифры — «ИИ-советнику» (OwnerOverviewSnapshot), точнее локальной истории продаж.
        OwnerOverviewSnapshot.Set("1-cards",
            $"Сводка сервера NurCRM за период «{PeriodNameForAi()}»: выручка {Num(cards, "revenue"):0.##} сом, чеков {Num(cards, "transactions"):0}, " +
            $"средний чек {Num(cards, "avg_check"):0.##} сом, валовая прибыль {Num(cards, "gross_profit"):0.##} сом" +
            (margin != 0 ? $" (маржа {margin:0.#}%)" : "") + ".");
    }

    /// <summary>Название выбранного периода «Сводки» для «ИИ-советника».</summary>
    private string PeriodNameForAi()
    {
        var (from, to) = CurrentRange();
        return from.Date == to.Date
            ? from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + (from.Date == DateTime.Today ? " (сегодня)" : "")
            : $"{from:dd.MM}–{to:dd.MM.yyyy}";
    }

    /// <summary>Сумма крупно, «сом» мелко и приглушённо. 2026-10-18, этап О1: без копеек (точная сумма — в «Продажах»).</summary>
    private void SetMoney(TextBlock target, double value)
    {
        var currency = new Run(" " + Som()) { FontSize = 17, FontWeight = FontWeight.Medium };
        UseBrush(currency, TextElement.ForegroundProperty, "BrushTextSoft");
        target.Text = null;
        target.Inlines = new InlineCollection { new Run(Whole(value)), currency };
    }

    /// <summary>2026-10-18, этап О1: строка сравнения «▲ 10 % к вчера на это время (3 900)» — стрелка и процент красным/зелёным,
    /// остальное серым; прошлое значение — в скобках. Нет прошлого периода — «—»; в нём не было продаж — noneBefore.</summary>
    private void SetDelta(TextBlock line, double current, double? previous, string hintText, string? noneBefore)
    {
        line.Inlines = null;
        if (previous is not { } prev || prev <= 0)
        {
            line.Text = noneBefore ?? "— " + hintText;
            return;
        }

        var change = (current - prev) / prev * 100;
        var abs = Math.Abs(change);
        var flat = abs < 0.5;
        var up = change > 0;
        var number = abs >= 100 ? abs.ToString("0", CultureInfo.InvariantCulture) : abs.ToString("0.#", UiCulture);
        var head = new Run((flat ? "" : up ? "▲ " : "▼ ") + Percent(flat ? 0 : abs, flat ? "0" : number)) { FontWeight = FontWeight.SemiBold };
        UseBrush(head, TextElement.ForegroundProperty, flat ? "BrushText" : up ? "BrushSuccess" : "BrushDanger");
        line.Text = null;
        line.Inlines = new InlineCollection { head, new Run($" {hintText} ({Whole(prev)})") };
    }

    /// <summary>«10 %» (в турецком «%10»), с неразрывным пробелом.</summary>
    private static string Percent(double value, string? formatted = null)
    {
        var n = formatted ?? value.ToString("0.#", UiCulture);
        return Tr.T($"{n} %", $"{n} %", $"{n}%", $"%{n}", $"{n} %");
    }

    /// <summary>Сумма без копеек: 4 300.</summary>
    private static string Whole(double value)
    {
        var r = Math.Round(value);
        if (r == 0)
            r = 0; // без «-0»
        return r.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// <summary>Подпись, когда в прошлом периоде (вчера к этому часу) продаж не было.
    /// 2026-10-21 (пакет 2, п. 85): одна строка под всеми плитками — с объяснением, почему нет «▲ / ▼».</summary>
    private string NoSalesBeforeText(bool sameTime) => _period switch
    {
        "week" => Tr.T("Сравнить не с чем: на прошлой неделе продаж не было", "Салыштырууга эч нерсе жок: өткөн жумада сатуу болгон эмес", "Nothing to compare with: no sales last week", "Karşılaştırılacak bir şey yok: geçen hafta satış yoktu", "Taqqoslash uchun hech narsa yo'q: o'tgan haftada sotuv bo'lmagan"),
        "month" => Tr.T("Сравнить не с чем: в прошлом месяце продаж не было", "Салыштырууга эч нерсе жок: өткөн айда сатуу болгон эмес", "Nothing to compare with: no sales last month", "Karşılaştırılacak bir şey yok: geçen ay satış yoktu", "Taqqoslash uchun hech narsa yo'q: o'tgan oyda sotuv bo'lmagan"),
        "custom" => Tr.T("Сравнить не с чем: в прошлом периоде продаж не было", "Салыштырууга эч нерсе жок: мурунку мезгилде сатуу болгон эмес", "Nothing to compare with: no sales in the previous period", "Karşılaştırılacak bir şey yok: önceki dönemde satış yoktu", "Taqqoslash uchun hech narsa yo'q: oldingi davrda sotuv bo'lmagan"),
        _ => sameTime
            ? Tr.T("Сравнить не с чем: вчера к этому времени продаж не было", "Салыштырууга эч нерсе жок: кечээ ушул убакытка чейин сатуу болгон эмес", "Nothing to compare with: no sales by this time yesterday", "Karşılaştırılacak bir şey yok: dün bu saate kadar satış yoktu", "Taqqoslash uchun hech narsa yo'q: kecha shu vaqtgacha sotuv bo'lmagan")
            : Tr.T("Сравнить не с чем: вчера продаж не было", "Салыштырууга эч нерсе жок: кечээ сатуу болгон эмес", "Nothing to compare with: no sales yesterday", "Karşılaştırılacak bir şey yok: dün satış yoktu", "Taqqoslash uchun hech narsa yo'q: kecha sotuv bo'lmagan"),
    };

    /// <summary>2026-10-21 (пакет 2, п. 85): прошлый период не получен (сервер не ответил) — одна строка вместо «— к вчера» в каждой плитке.</summary>
    private string NoCompareText() => _period switch
    {
        "week" => Tr.T("Сравнения с прошлой неделей пока нет — её данные не получены", "Өткөн жума менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with last week yet — its data was not received", "Geçen haftayla karşılaştırma henüz yok — verileri alınamadı", "O'tgan hafta bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        "month" => Tr.T("Сравнения с прошлым месяцем пока нет — его данные не получены", "Өткөн ай менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with last month yet — its data was not received", "Geçen ayla karşılaştırma henüz yok — verileri alınamadı", "O'tgan oy bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        "custom" => Tr.T("Сравнения с прошлым периодом пока нет — его данные не получены", "Мурунку мезгил менен салыштыруу азырынча жок — анын маалыматы алынган жок", "No comparison with the previous period yet — its data was not received", "Önceki dönemle karşılaştırma henüz yok — verileri alınamadı", "Oldingi davr bilan taqqoslash hozircha yo'q — uning ma'lumotlari olinmadi"),
        _ => Tr.T("Сравнения со вчера пока нет — данные за вчера не получены", "Кечээ менен салыштыруу азырынча жок — кечээки маалымат алынган жок", "No comparison with yesterday yet — yesterday's data was not received", "Dünle karşılaştırma henüz yok — dünün verileri alınamadı", "Kecha bilan taqqoslash hozircha yo'q — kechagi ma'lumotlar olinmadi"),
    };

    /// <summary>2026-10-18, этап О2: меньше этой суммы день выбросом на графике не считается.</summary>
    private const double ChartOutlierMinSom = 5000;

    private void ApplyChart(JsonElement report, DateTime from, DateTime to)
    {
        ChartBars.Children.Clear();
        ChartBars.ColumnDefinitions.Clear();
        ChartBars.RowDefinitions = new RowDefinitions("*,Auto");
        ChartLegend.Children.Clear();

        var byDay = new Dictionary<DateTime, double>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("sales_dynamics", out var dynamics)
            && dynamics.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in dynamics.EnumerateArray())
            {
                if (DateTime.TryParseExact(Str(point, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    byDay[day.Date] = Num(point, "value");
            }
        }

        var days = new List<DateTime>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            days.Add(d);

        var values = days.Select(d => byDay.TryGetValue(d, out var v) ? v : 0).ToList();
        var max = values.Count == 0 ? 0 : values.Max();
        var total = values.Sum();
        // 2026-10-05: выручка по дням — «ИИ-советнику» (OwnerOverviewSnapshot).
        OwnerOverviewSnapshot.Set("2-chart",
            $"Выручка по дням с {from:dd.MM} по {to:dd.MM} (сервер NurCRM): " +
            string.Join(", ", days.Select((d, i) => $"{d:dd.MM} — {values[i]:0.##}")) + $"; итого {total:0.##} сом.");
        ChartTotalText.Text = Tr.T($"Итого: {Whole(total)} {Som()}", $"Жыйынтык: {Whole(total)} {Som()}", $"Total: {Whole(total)} {Som()}",
            $"Toplam: {Whole(total)} {Som()}", $"Jami: {Whole(total)} {Som()}");
        ChartEmptyText.IsVisible = max <= 0;
        ChartBars.IsVisible = max > 0;
        if (max <= 0)
            return;

        var culture = UiCulture;
        var few = days.Count <= 10;
        // 2026-10-18, этап О1 (макет Owner): столбцы — цвет текста темы (тёмно-синий), «сегодня» — жёлтый с подписью «сегодня»;
        // подписи — в тыс. сом с одним знаком (6,1 / 51,4). Выброс (столбец больше 3 медиан остальных дней, у владельца 16.10 —
        // 51 тыс. при обычных 3–7) рисуется во всю высоту с отметкой разрыва, а остальные дни — в своей шкале, чтобы их было видно.
        const double barArea = 170;
        var outlier = new bool[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var others = values.Where((v, j) => j != i && v > 0).OrderBy(v => v).ToList();
            if (others.Count >= 2)
            {
                var median = others.Count % 2 == 1 ? others[others.Count / 2] : (others[others.Count / 2 - 1] + others[others.Count / 2]) / 2;
                // 2026-10-18, этап О2 (решение главной сессии): выброс — только день ≥ 3 медиан И ≥ 5 000 сом; при малых суммах
                // (870 сом при медиане ~110) обычный день рисовался с ложным разрывом.
                // 2026-10-22 (аудит 1.17.59, П-З): разрыв оси убран — 15,4 тыс. и 1,8 тыс. на нём выглядели почти одной высоты. Столбцы
                // пропорциональны (ось от нуля): высота = значение / максимум, маленький день виден минимумом 4 px.
                outlier[i] = false && values[i] >= 3 * median && values[i] >= ChartOutlierMinSom;
            }
        }
        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах»: выручка по дням — временной ряд: столбцы одного оттенка, лучший день — акцентом
        // (жёлтый), самый слабый — приглушённо, сегодня — кольцом (было: «сегодня» жёлтым), внизу легенда.
        var (bestDays, worstDays) = ChartPalette.SeriesMarks(values);
        var scaleMax = values.Where((_, i) => !outlier[i]).DefaultIfEmpty(0).Max();
        var capped = outlier.Any(o => o) && scaleMax > 0;
        if (!capped)
            scaleMax = max;
        for (var i = 0; i < days.Count; i++)
        {
            ChartBars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var day = days[i];
            var value = values[i];
            var isToday = day == DateTime.Today;
            var isOutlier = capped && outlier[i];

            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (few && value > 0)
            {
                var valueText = new TextBlock
                {
                    // 2026-10-18, ревью О1: меньше 100 сом — «<0,1», а не «0,0».
                    Text = value < 100 ? "<" + 0.1.ToString("N1", culture) : (value / 1000).ToString("N1", culture),
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 5),
                    FontWeight = FontWeight.SemiBold,
                    Classes = { "num" },
                };
                UseBrush(valueText, TextBlock.ForegroundProperty, "BrushText");
                column.Children.Add(valueText);
            }

            // Выбросов несколько (51,4 и 15,1) — они различаются хотя бы немного: верхние 12 % высоты — по их размеру.
            var height = value <= 0 ? 2
                : isOutlier ? barArea * (0.88 + 0.12 * value / max)
                : Math.Max(4, (capped ? barArea * 0.8 : barArea) * value / scaleMax);
            var bar = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(6, 6, 2, 2),
            };
            if (value <= 0)
                UseBrush(bar, Border.BackgroundProperty, "BrushBorder");
            ToolTip.SetTip(bar, $"{day.ToString("ddd, d MMMM", culture)} — {Amount(value)} {Som()}"
                + (bestDays.Contains(i) ? " · " + Tr.T("лучший день", "эң мыкты күн", "best day", "en iyi gün", "eng yaxshi kun")
                    : worstDays.Contains(i) ? " · " + Tr.T("самый слабый день", "эң начар күн", "weakest day", "en zayıf gün", "eng sust kun") : ""));
            Control barView = value > 0 ? BarChartRenderer.StyleSeriesBar(bar, this, bestDays.Contains(i), worstDays.Contains(i), isToday) : bar;
            if (isOutlier)
            {
                // Отметка разрыва: косая полоса цвета карточки поперёк столбца.
                var gap = new Border
                {
                    Height = 6,
                    Margin = new Thickness(-4, barArea * 0.28, -4, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    RenderTransform = new RotateTransform(-12),
                    IsHitTestVisible = false,
                };
                UseBrush(gap, Border.BackgroundProperty, "BrushPanel");
                barView = new Panel { Children = { bar, gap } };
            }
            barView.Margin = new Thickness(few ? 12 : 2, 0);
            column.Children.Add(barView);
            Grid.SetColumn(column, i);
            ChartBars.Children.Add(column);

            var label = new TextBlock
            {
                Text = few
                    ? (isToday ? Tr.T("сегодня", "бүгүн", "today", "bugün", "bugun") : day.ToString("ddd", culture)) + $"\n{day.Day}"
                    : day.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = few ? 12.5 : 11,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal,
            };
            UseBrush(label, TextBlock.ForegroundProperty, isToday ? "BrushText" : "BrushTextSoft");
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            ChartBars.Children.Add(label);
        }

        if (BarChartRenderer.SeriesLegend(this, bestDays.Count > 0, worstDays.Count > 0, days.Contains(DateTime.Today),
                Tr.T("Лучший день", "Эң мыкты күн", "Best day", "En iyi gün", "Eng yaxshi kun"),
                Tr.T("Самый слабый день", "Эң начар күн", "Weakest day", "En zayıf gün", "Eng sust kun"),
                Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")) is { } legend)
            ChartLegend.Children.Add(legend);
    }

    private void ApplyPayments(JsonElement report)
    {
        PaymentsBar.Children.Clear();
        PaymentsBar.ColumnDefinitions.Clear();
        PaymentsLegend.Children.Clear();

        var methods = new List<(string Method, double Count, double Total)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("payment_methods", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                var total = Num(m, "total");
                if (total > 0)
                    methods.Add((Str(m, "method"), Num(m, "count"), total));
            }
        }

        methods.Sort((a, b) => b.Total.CompareTo(a.Total));
        var sum = methods.Sum(m => m.Total);
        PaymentsCountText.Text = sum > 0 ? $"{Whole(sum)} {Som()}" : "";
        PaymentsEmptyText.IsVisible = methods.Count == 0;

        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — у каждого способа оплаты свой цвет из общей палитры (ChartPalette) по ключу способа,
        // тот же, что в «Финансах» и мобилке (с 2026-10-18, этап О1, способы различались оттенком цвета текста).
        var methodColors = ChartPalette.Assign(methods.Select(m => (string?)ChartPalette.PaymentKey(m.Method)).ToList());
        for (var i = 0; i < methods.Count; i++)
        {
            var (method, count, total) = methods[i];
            var share = sum > 0 ? total / sum : 0;

            PaymentsBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(share, 0.004), GridUnitType.Star));
            var segment = new Border
            {
                Background = Brush.Parse(methodColors[i]),
                Margin = new Thickness(0, 0, i < methods.Count - 1 ? 2 : 0, 0),
            };
            ToolTip.SetTip(segment, $"{PaymentLabel(method)}: {Whole(total)} {Som()}");
            Grid.SetColumn(segment, i);
            PaymentsBar.Children.Add(segment);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Brush.Parse(methodColors[i]), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(dot);

            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var nameRun = new Run(PaymentLabel(method)) { FontSize = 14 };
            UseBrush(nameRun, TextElement.ForegroundProperty, "BrushText");
            var countRun = new Run("  " + ChecksWord(count)) { FontSize = 13 };
            UseBrush(countRun, TextElement.ForegroundProperty, "BrushTextSoft");
            name.Inlines = new InlineCollection { nameRun, countRun };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var amount = new TextBlock { Text = $"{Whole(total)} {Som()}", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "num" } };
            UseBrush(amount, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);

            var percent = new TextBlock
            {
                Text = Percent(Math.Round(share * 100), (share * 100).ToString("0", CultureInfo.InvariantCulture)),
                FontSize = 13,
                MinWidth = 52,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "num" },
            };
            UseBrush(percent, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(percent, 3);
            row.Children.Add(percent);

            // 2026-10-05: «В долг» — по нажатию подробные долги клиентов.
            if (string.Equals(method?.Trim(), "debt", StringComparison.OrdinalIgnoreCase) && TariffGate.CanUseDebts)
            {
                row.Background = Brushes.Transparent;
                row.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(row, Tr.T("Подробно: кто и сколько должен", "Кененирээк: ким канча карыз", "Details: who owes how much", "Ayrıntılar: kim ne kadar borçlu", "Batafsil: kim qancha qarz"));
                nameRun.TextDecorations = TextDecorations.Underline;
                row.PointerPressed += (_, _) => OpenDebts();
            }

            PaymentsLegend.Children.Add(row);
        }

        // Возвраты — как на сайте: «Документы» → «Возврат продажи».
        double returnsCount = 0, returnsSum = 0;
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("documents", out var docs)
            && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                if (string.Equals(Str(doc, "name"), "Возврат продажи", StringComparison.OrdinalIgnoreCase))
                {
                    returnsCount = Num(doc, "count");
                    returnsSum = Num(doc, "sum");
                }
            }
        }

        // 2026-09-28 (BE-09): список возвратов сервера за этот же период — если он получен.
        var (rangeFrom, rangeTo) = CurrentRange();
        if (_lastReturns is { } listed && _lastReturnsKey == RangeKey(rangeFrom, rangeTo))
        {
            returnsCount = listed.Count;
            returnsSum = (double)listed.Sum;
        }

        // 2026-10-18, этап О1: «2 чека на 450 сом» вместо склейки «2 · 450 сом».
        ReturnsValue.Text = returnsCount > 0
            ? Tr.T($"{ChecksWord(returnsCount)} на {Whole(returnsSum)} {Som()}", $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}",
                $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}", $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}",
                $"{ChecksWord(returnsCount)}, {Whole(returnsSum)} {Som()}")
            : "0";
    }

    private void ApplyTopProducts(JsonElement report)
    {
        TopList.Children.Clear();
        var items = new List<(string Name, double Sold, double Revenue)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("top_products", out var top)
            && top.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in top.EnumerateArray())
                items.Add((Str(p, "name"), Num(p, "sold"), Num(p, "revenue")));
        }

        items = items.Where(i => i.Revenue > 0).OrderByDescending(i => i.Revenue).Take(5).ToList();
        // 2026-10-05: лучшие товары периода — «ИИ-советнику» (OwnerOverviewSnapshot).
        OwnerOverviewSnapshot.Set("3-top",
            $"Лучшие товары за период «{PeriodNameForAi()}» (сервер NurCRM): " +
            (items.Count == 0 ? "продаж нет" : string.Join("; ", items.Select(i => $"{i.Name} — продано {i.Sold:0.###}, выручка {i.Revenue:0.##} сом"))) + ".");
        TopEmptyText.IsVisible = items.Count == 0;
        var max = items.Count == 0 ? 0 : items.Max(i => i.Revenue);
        // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — у каждого товара свой цвет по названию (ChartPalette): плашка места и полоса доли
        // (раньше — нейтральные); цвет товара тот же на других экранах и в мобилке.
        var productColors = ChartPalette.Assign(items.Select(i => (string?)i.Name).ToList());

        for (var i = 0; i < items.Count; i++)
        {
            var (name, sold, revenue) = items[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            // 2026-10-18, этап О1: место — нейтральной плашкой у всех (жёлтый — только у «сегодня» и главной кнопки).
            var rank = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = Brush.Parse(productColors[i]) };
            var rankText = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rankText.Foreground = ChartPalette.OnColorIsDark(productColors[i]) ? Brushes.Black : Brushes.White; // контраст ≥ 4,5:1 на любом цвете палитры
            rank.Child = rankText;
            row.Children.Add(rank);

            var middle = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            UseBrush(title, TextBlock.ForegroundProperty, "BrushText");
            middle.Children.Add(title);

            var share = max > 0 ? revenue / max : 0;
            var track = new Border { Height = 5, CornerRadius = new CornerRadius(3), ClipToBounds = true };
            UseBrush(track, Border.BackgroundProperty, "BrushInputAlt");
            var fillGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(Math.Max(share, 0.01), GridUnitType.Star),
                    new ColumnDefinition(Math.Max(1 - share, 0.0001), GridUnitType.Star),
                },
            };
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = Brush.Parse(productColors[i]) };
            fillGrid.Children.Add(fill);
            track.Child = fillGrid;
            middle.Children.Add(track);
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var revenueText = new TextBlock { Text = $"{Whole(revenue)} {Som()}", FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Classes = { "num" } };
            UseBrush(revenueText, TextBlock.ForegroundProperty, "BrushText");
            var soldText = new TextBlock
            {
                Text = Tr.T($"продано {Qty(sold)}", $"{Qty(sold)} сатылды", $"sold {Qty(sold)}", $"satılan: {Qty(sold)}", $"sotildi {Qty(sold)}"),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            UseBrush(soldText, TextBlock.ForegroundProperty, "BrushTextSoft");
            right.Children.Add(revenueText);
            right.Children.Add(soldText);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            TopList.Children.Add(row);
        }

        ApplyLowStock();
    }

    /// <summary>2026-09-30: «Заканчивается на складе» — товары с самым малым остатком из каталога
    /// программы (без услуг: у них остатка нет). Пересчитывается вместе со сводкой; каталог уже в
    /// памяти, запросов к серверу нет. 2026-10-18, этап О1: показываются строками блока «Требует внимания»
    /// (OwnerShellWindow.Attention.cs) — три первых и «ещё N», действие «Пополнение ›».</summary>
    private void ApplyLowStock()
    {
        _attnStock.Clear();
        _attnStockMore = 0;
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> low;
        try
        {
            low = CatalogCacheService.Products
                // 2026-10-27, аудит владельца С-7: порог «мало» один на программу — тот же, что у «Склада» и аналитики склада
                // (меньше 10, нулевые — «Нет в наличии»). Было «5 и меньше»: на Сводке 81 товар, на Складе 158.
                .Where(p => !p.IsService && !string.IsNullOrWhiteSpace(p.Title) && p.Quantity < NurMarketKassa.Services.StockSyncService.LowStockThreshold)
                .OrderBy(p => p.Quantity)
                .ThenBy(p => p.Title)
                .ToList();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка: список «Заканчивается» не построен ({ex.Message}).", "WARNING");
            low = [];
        }

        foreach (var p in low.Take(AttentionStockRows))
            _attnStock.Add(new AttentionStock(p.Title, p.Quantity, p.Unit));
        _attnStockMore = Math.Max(0, low.Count - AttentionStockRows);
        _attnStockLoaded = true;

        ApplyLowSizes();
        RenderAttention();
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-74): в сфере «Одежда» под товарами — «Заканчиваются размеры»:
    /// размер/цвет, которого осталось 0–1 шт., когда другие размеры модели ещё есть. Остатки размеров — из общего справочника
    /// касс (VariantBarcodeIndex, обновляется в фоне), запросов к серверу здесь нет.</summary>
    private void ApplyLowSizes()
    {
        if (!MarketSpheres.IsClothing)
            return;
        List<VariantBarcodeIndex.LowSize> low;
        try
        {
            // 2026-10-18, ревью О1: весь список — «ещё N» считается по нему, а строк в блоке не больше AttentionStockRows всего.
            low = VariantBarcodeIndex.LowSizes(1000);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сводка: «Заканчиваются размеры» не построено ({ex.Message}).", "WARNING");
            return;
        }
        if (low.Count == 0)
            return;
        var titles = CatalogCacheService.Products.ToList().GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var room = Math.Max(0, AttentionStockRows - _attnStock.Count);
        foreach (var s in low.Take(room))
        {
            var label = string.Join(", ", new[] { s.Size, s.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var name = (titles.TryGetValue(s.ProductId, out var t) ? t : "—") + (label.Length > 0 ? " — " + label : "");
            _attnStock.Add(new AttentionStock(name, s.Quantity, null));
        }
        _attnStockMore += Math.Max(0, low.Count - room);
    }

    // 2026-09-28, сверка с сайтом: в «Последних продажах» был виден только первый товар чека
    // (first_item_name) — чек из трёх позиций выглядел как продажа одного товара. Списка позиций в
    // списке продаж сервер не отдаёт (у сайта там тоже только первый товар), поэтому число позиций
    // берём из деталей чека: SaleDetailCache качает каждый чек один раз за сеанс, при
    // 20-секундном обновлении запрашиваются только новые чеки.
    private readonly Dictionary<string, int> _saleItemCounts = new(StringComparer.OrdinalIgnoreCase);
    private List<JsonElement>? _recentShown;
    private bool _itemCountsLoading;

    private async Task LoadRecentItemCountsAsync()
    {
        if (_itemCountsLoading || _recentShown is not { } rows)
            return;

        var ids = rows
            .Where(r => Num(r, "items_count") <= 0)
            .Select(r => Str(r, "id"))
            .Where(id => id.Length > 0 && !_saleItemCounts.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return;

        _itemCountsLoading = true;
        var changed = false;
        try
        {
            foreach (var id in ids)
            {
                if (_cts.IsCancellationRequested || _loggingOut)
                    return;
                try
                {
                    var detail = await SaleDetailCache.GetAsync(id, _cts.Token).ConfigureAwait(true);
                    _saleItemCounts[id] = detail.ValueKind == JsonValueKind.Object
                        && detail.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                            ? items.GetArrayLength()
                            : 0;
                    changed = true;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Без состава чек просто покажется без «+ ещё N» — повторно не спрашиваем.
                    _saleItemCounts[id] = 0;
                    PosLogger.Log($"Owner app: состав чека {id} не получен: {ex.Message}", "DEBUG");
                }
            }
        }
        finally
        {
            _itemCountsLoading = false;
        }

        if (changed && _recentShown != null)
            ApplyRecent(_recentShown);
    }

    private void ApplyRecent(List<JsonElement> rows)
    {
        RecentList.Children.Clear();
        var shown = rows.Take(RecentRows).ToList();
        _recentShown = shown;
        RecentEmptyText.IsVisible = shown.Count == 0;

        for (var i = 0; i < shown.Count; i++)
        {
            var sale = shown[i];
            var canceled = string.Equals(Str(sale, "status"), "canceled", StringComparison.OrdinalIgnoreCase);
            var method = Str(sale, "payment_method");

            // 2026-10-18, этап О1: без линий между строками; способ оплаты — серой строкой под суммой, без цветной точки.
            var line = new Border { Padding = new Thickness(0, 8) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto") };

            var created = Str(sale, "created_at");
            var time = DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? dto.LocalDateTime.ToString(dto.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "dd.MM", CultureInfo.InvariantCulture)
                : "";
            var timeText = new TextBlock { Text = time, FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), Classes = { "num" } };
            UseBrush(timeText, TextBlock.ForegroundProperty, "BrushTextSoft");
            row.Children.Add(timeText);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
            var itemName = Str(sale, "first_item_name");
            var title = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var nameRun = new Run(string.IsNullOrWhiteSpace(itemName) ? Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv") : itemName);
            UseBrush(nameRun, TextElement.ForegroundProperty, "BrushText");
            var titleInlines = new InlineCollection { nameRun };
            var saleId = Str(sale, "id");
            var itemsCount = (int)Math.Round(Num(sale, "items_count"));
            if (itemsCount <= 0 && saleId.Length > 0 && _saleItemCounts.TryGetValue(saleId, out var known))
                itemsCount = known;
            if (itemsCount > 1 && !string.IsNullOrWhiteSpace(itemName))
            {
                var more = itemsCount - 1;
                var moreRun = new Run(Tr.T($"  + ещё {more}", $"  + дагы {more}", $"  + {more} more", $"  + {more} ürün daha", $"  + yana {more} ta"))
                {
                    FontWeight = FontWeight.Normal,
                    FontSize = 12.5,
                };
                UseBrush(moreRun, TextElement.ForegroundProperty, "BrushTextSoft");
                titleInlines.Add(moreRun);
            }
            title.Inlines = titleInlines;
            middle.Children.Add(title);
            var who = string.Join(" · ", new[] { Str(sale, "user_display"), Str(sale, "cashbox_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (who.Length > 0)
            {
                var sub = new TextBlock { Text = who, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
                UseBrush(sub, TextBlock.ForegroundProperty, "BrushTextSoft");
                middle.Children.Add(sub);
            }
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var right = new StackPanel { Spacing = 2, MinWidth = 110, VerticalAlignment = VerticalAlignment.Top };
            var amount = new TextBlock
            {
                Text = $"{Amount(Num(sale, "total"))} {Som()}",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextDecorations = canceled ? TextDecorations.Strikethrough : null,
                Classes = { "num" },
            };
            UseBrush(amount, TextBlock.ForegroundProperty, canceled ? "BrushTextSoft" : "BrushText");
            right.Children.Add(amount);
            var methodText = new TextBlock
            {
                Text = canceled ? Tr.T("Возврат", "Кайтаруу", "Returned", "İade", "Qaytarilgan") : PaymentLabel(method),
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            UseBrush(methodText, TextBlock.ForegroundProperty, "BrushTextSoft");
            right.Children.Add(methodText);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            line.Child = row;
            RecentList.Children.Add(line);
        }

        _ = LoadRecentItemCountsAsync();
    }

    /// <summary>2026-10-05: «Долги клиентов» — из меню и по нажатию на «В долг» в «Сводке».</summary>
    private void OpenDebts()
    {
        if (TariffGate.CanUseDebts && Authorize(PosPermissions.ViewSales))
            OpenSection("debts", () => new DebtsWindow());
    }

    private static string PaymentLabel(string method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        "transfer" or "card" => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
        "mbank" => "MBank",
        "mixed" or "split" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karışık", "Aralash"),
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        // 2026-10-06: зачёт при обмене и выдаче заказа (сервер: «Зачёт (предоплата/обмен)») — показывалось слово «offset».
        "offset" => Tr.T("Зачёт (обмен, предоплата)", "Эсепке алуу (алмаштыруу, алдын ала төлөм)", "Offset (exchange, prepayment)", "Mahsup (değişim, ön ödeme)", "Hisobga olish (almashtirish, oldindan to'lov)"),
        "" => "—",
        var other => other,
    };

    private static string ChecksWord(double count)
    {
        var n = (long)Math.Round(count);
        var ru = (n % 10 == 1 && n % 100 != 11) ? "чек"
            : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "чека"
            : "чеков";
        return Tr.T($"{n} {ru}", $"{n} чек", n == 1 ? "1 receipt" : $"{n} receipts", $"{n} fiş", $"{n} ta chek");
    }

    private static string Som() => Tr.T("сом", "сом", "som", "som", "so'm");

    /// <summary>Сумма без «,00» у целых: 414 634 и 414 634,07.</summary>
    private static string Amount(double value)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return Math.Abs(value - Math.Round(value)) < 0.005 ? value.ToString("N0", ru) : value.ToString("N2", ru);
    }

    private static string Qty(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0005
            ? value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))
            : value.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>2026-10-18, этап О1: единица подписей графика (было «415 тыс» / «4 288» вперемешку) — всё в тыс. сом.</summary>
    private static string ThousandSom() => Tr.T("тыс. сом", "миң сом", "thousand som", "bin som", "ming so'm");

    /// <summary>Подписи над столбцами есть, когда дней в графике не больше 10 (как в ApplyChart).</summary>
    private bool ChartHasLabels
    {
        get
        {
            var (from, to) = CurrentRange();
            var (chartFrom, chartTo) = ChartRange(from, to);
            return (chartTo.Date - chartFrom.Date).Days + 1 <= 10;
        }
    }

    private static double Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Цвет из темы с подпиской: при смене светлой/тёмной темы элементы, собранные в
    /// коде, перекрашиваются сами, как DynamicResource в разметке.</summary>
    private void UseBrush(AvaloniaObject target, AvaloniaProperty property, string resourceKey) =>
        target.Bind(property, this.GetResourceObservable(resourceKey));

    // ------------------------------------------------------------------ шапка окна

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            UpdateWindowChrome();
            Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
            // 2026-10-18, ревью О2: развернули/восстановили окно — заново решить, сворачивать ли колонку пунктов (порог 1366).
            Dispatcher.UIThread.Post(OnShellSizeChangedForNav, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Развёрнутое окно без системной рамки Windows сдвигает за край экрана на ширину
    /// рамки — отступаем на столько же. Значок кнопки: «развернуть» или «восстановить».</summary>
    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? OffScreenMargin : default;
        MaximizeIconPath.Data = Geometry.Parse(maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    // ------------------------------------------------------------------ кнопки

    private async void Period_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period } || (period == _period && period != "custom"))
            return;
        if (period == "custom")
        {
            // Спец. дата — то же окно выбора дат, что в «Финансах»; повторное нажатие — выбрать заново.
            var dlg = new FinanceDateRangeDialog();
            if (await dlg.ShowDialog<bool>(this) != true)
                return;
            _customFrom = dlg.FromDate.Date <= dlg.ToDate.Date ? dlg.FromDate.Date : dlg.ToDate.Date;
            _customTo = dlg.FromDate.Date <= dlg.ToDate.Date ? dlg.ToDate.Date : dlg.FromDate.Date;
        }
        _period = period;
        foreach (var b in new[] { TodayButton, WeekButton, MonthButton, CustomButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        ApplyTexts();
        _ = RefreshAsync();
        _ = RefreshAbcAsync();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-09-29: «Обновить» — всегда свежие цифры сервера, мимо короткого кэша отчётов.
        NurMarketKassa.Services.Api.SalesApiService.InvalidateReportCache();
        _ = RefreshAsync(forceChart: true);
        _ = RefreshAbcAsync();
        // 2026-10-29: заодно общие настройки компании, права, каталог и остатки — SyncCenter.ForceSyncAllAsync (то же делает «Обновить» кассы).
        _ = NurMarketKassa.Services.SyncCenter.ForceSyncAllAsync();
    }

    private void AbcOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (TariffGate.CanUseSalesAnalytics && Authorize(PosPermissions.ViewAnalytics))
            OpenSection("abc", () => App.GetRequiredService<AbcAnalysisWindow>());
    }

    /// <summary>Маркетплейс сразу на вкладке «Доп. функции» — для раздела, который предлагает
    /// подключить платную функцию (вкладка «Склад» в «Аналитике»). Права — как у пункта меню.</summary>
    public void OpenMarketplaceExtras()
    {
        if (!Authorize(PosPermissions.ViewSettings))
            return;
        OpenSection("marketplace", () => new MarketplaceWindow().AsSection());
        (_sections.FirstOrDefault(s => s.Key == "marketplace")?.Window as MarketplaceWindow)?.ShowExtrasTab();
    }

    // ------------------------------------------------------------------ сайт: заказы и настройки

    // 2026-09-29: число новых заказов у пункта «Заказы с сайта». Вебхука о заказах у NurCRM нет —
    // раз в минуту спрашиваем список (обычно один запрос), через общий темп массовых загрузок и не
    // во время паузы 429. Пока раздел открыт на экране, он обновляет список сам — тогда не спрашиваем.
    private static readonly TimeSpan SiteOrdersPollInterval = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _siteOrdersTimer;
    private Border? _siteOrdersBadge;
    private int _siteOrdersNewCount;
    private bool _siteOrdersPolling;
    private bool _siteOrdersUnavailable;

    private async Task PollSiteOrdersAsync()
    {
        if (_siteOrdersPolling || _siteOrdersUnavailable || _loggingOut || _cts.IsCancellationRequested)
            return;
        if (ApiThrottle.RemainingBlock > TimeSpan.Zero)
            return;
        if (_sections.Any(s => s.Key == "siteorders" && s.Window.IsVisible))
            return;
        try
        {
            // 2026-10-01: заказы с сайта — право «Заказы» сайта NurCRM.
            if (!App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewOrders))
                return;
        }
        catch (Exception)
        {
            return;
        }

        _siteOrdersPolling = true;
        try
        {
            var api = App.GetRequiredService<ShowcaseApiService>();
            await ApiThrottle.RunBulkAsync(() => api.ListOrdersAsync(_cts.Token), _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            // Нет права или адреса — до конца сеанса не спрашиваем; раздел сам покажет причину.
            _siteOrdersUnavailable = true;
            PosLogger.Log($"Owner app: заказы с сайта недоступны ({ex.StatusCode}) — опрос остановлен.", "SHOWCASE");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: заказы с сайта не опрошены: {ex.Message}", "DEBUG");
        }
        finally
        {
            _siteOrdersPolling = false;
        }
    }

    private void OnSiteOrdersCountChanged(int count) => Dispatcher.UIThread.Post(() =>
    {
        _siteOrdersNewCount = count;
        UpdateSiteOrdersBadge();
        // 2026-10-18, этап О1: новые заказы — и строкой в «Требует внимания».
        RenderAttention();
    });

    private void UpdateSiteOrdersBadge()
    {
        if (_siteOrdersBadge is not { Child: TextBlock text } badge)
            return;
        var count = _siteOrdersNewCount;
        badge.IsVisible = count > 0;
        text.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        UpdateRailDots();
        if (_navButtons.TryGetValue("siteorders", out var button) && _navTitles.TryGetValue("siteorders", out var title))
            ToolTip.SetTip(button, count > 0
                ? title + " · " + Tr.T($"новых: {count}", $"жаңы: {count}", $"new: {count}", $"yeni: {count}", $"yangi: {count}")
                : title);
    }

    // ------------------------------------------------------------------ прокат: сроки возврата

    // 2026-10-02, владелец: «в админке добавь уведомление об окончании и приближении срока аренды проката».
    // Проверяет RentalDueNotifier (раз в час); здесь — значок у пункта «Прокат» и карточка в меню.
    // Карточку можно закрыть — снова появится, когда изменится состав (новый срок или просрочка).
    private Border? _rentalsBadge;
    private string _rentalAlertDismissed = "";
    // 2026-10-02, владелец: «оповещение прям со звуком должно быть в админке». Звук — когда появилось
    // новое событие (прокат впервые стал «завтра», «сегодня» или «просрочен»), а не каждый час.
    private readonly HashSet<string> _rentalAlertSounded = new();

    private void OnRentalDueChanged(RentalDueNotifier.Summary summary) => Dispatcher.UIThread.Post(UpdateRentalAlert);

    private static string RentalAlertSignature(RentalDueNotifier.Summary s) =>
        string.Join("|", s.Overdue.Select(r => "o" + r.Id).Concat(s.Today.Select(r => "t" + r.Id)).Concat(s.Tomorrow.Select(r => "m" + r.Id)));

    /// <summary>Плашка показана владельцу (сроки есть, не скрыта крестиком) — от неё зависят звук и место плашки.</summary>
    private bool _rentalAlertActive;

    private void UpdateRentalAlert()
    {
        // 2026-10-18, этап О1: сроки проката — и строкой в «Требует внимания» сводки.
        // 2026-10-18, этап О2: плашка ушла из меню (правило аудита 9) в полосу над открытым разделом (PlaceRentalAlert), в «Сводке»
        // сроки — строками «Требует внимания». Звук — как раньше: новое событие (прокат впервые «завтра», «сегодня» или «просрочен»).
        // Прежде звук молчал при свёрнутом меню (карточки там не было) — теперь оповещение видно всегда, и звук тоже.
        RenderAttention();
        var s = RentalDueNotifier.Last;
        var count = s?.Count ?? 0;
        var visibleSphere = MarketSpheres.HasRentals; // 2026-10-20: прокат во всех сферах
        if (_rentalsBadge is { Child: TextBlock badgeText } badge)
        {
            badge.IsVisible = visibleSphere && count > 0;
            badgeText.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
            // Просрочка — красным, только сроки — серо-синим (UpdateRailDots — точка у группы «Продажи»).
            SetNavBadgeTone(badge, s is { Overdue.Count: > 0 });
        }

        if (s is null || count == 0 || !visibleSphere || RentalAlertSignature(s) == _rentalAlertDismissed)
        {
            _rentalAlertActive = false;
            PlaceRentalAlert();
            return;
        }

        RentalAlertTitle.Text = Tr.T("Прокат: сроки возврата", "Прокат: кайтаруу мөөнөтү", "Rentals: return dates", "Kiralama: iade tarihleri", "Prokat: qaytarish muddati");
        var parts = new List<string>();
        if (s.Overdue.Count > 0)
            parts.Add(Tr.T($"Просрочено: {s.Overdue.Count}", $"Мөөнөтү өттү: {s.Overdue.Count}", $"Overdue: {s.Overdue.Count}", $"Gecikmiş: {s.Overdue.Count}", $"Muddati o'tgan: {s.Overdue.Count}"));
        if (s.Today.Count > 0)
            parts.Add(Tr.T($"Вернуть сегодня: {s.Today.Count}", $"Бүгүн кайтаруу: {s.Today.Count}", $"Due today: {s.Today.Count}", $"Bugün iade: {s.Today.Count}", $"Bugun qaytarish: {s.Today.Count}"));
        if (s.Tomorrow.Count > 0)
            parts.Add(Tr.T($"Вернуть завтра: {s.Tomorrow.Count}", $"Эртең кайтаруу: {s.Tomorrow.Count}", $"Due tomorrow: {s.Tomorrow.Count}", $"Yarın iade: {s.Tomorrow.Count}", $"Ertaga qaytarish: {s.Tomorrow.Count}"));
        // В плашке — числа одной строкой; первые три проката с именем клиента — в подсказке (кому звонить).
        RentalAlertText.Text = string.Join(" · ", parts);
        var lines = new List<string>(parts);
        foreach (var r in s.Overdue.Concat(s.Today).Concat(s.Tomorrow).Take(3))
            lines.Add($"• №{r.Number} {r.ClientName} — {string.Join(", ", r.Items.Select(i => i.Label))}");
        ToolTip.SetTip(RentalAlertCard, string.Join("\n", lines));
        RentalAlertOpenText.Text = Tr.T("Открыть прокат", "Прокатты ачуу", "Open rentals", "Kiralamayı aç", "Prokatni ochish");
        ToolTip.SetTip(RentalAlertClose, Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish"));
        Avalonia.Automation.AutomationProperties.SetName(RentalAlertOpen, RentalAlertOpenText.Text);
        // 2026-10-18, ревью О2: пункта «Прокат» нет (права, «Разделы меню») — плашка только сообщает, без «Открыть прокат ›».
        RentalAlertOpen.IsVisible = _navButtons.ContainsKey("rentals");
        _rentalAlertActive = true;
        PlaceRentalAlert();

        var keys = RentalAlertSignature(s).Split('|');
        var fresh = keys.Count(k => _rentalAlertSounded.Add(k));
        if (fresh > 0)
        {
            AlertSound.PlayChime();
            PosLogger.Log($"Owner app: оповещение о сроке проката со звуком ({fresh}).", "RENTAL");
        }
    }

    /// <summary>2026-10-18, этап О2: плашка проката — в полосе над открытым разделом (окно раздела закрывает правую часть, а полоса
    /// видна всегда). В «Сводке» её нет: те же сроки — первыми строками «Требует внимания». На телефоне полоса узкая — только
    /// число у пункта «Прокат», точка у группы и строки «Требует внимания».</summary>
    private void PlaceRentalAlert()
    {
        if (RentalAlertCard is null)
            return;
        RentalAlertCard.IsVisible = _rentalAlertActive && _activeSection != null && !_phoneLayout;
    }

    private void RentalAlertClose_Click(object? sender, RoutedEventArgs e)
    {
        if (RentalDueNotifier.Last is { } s)
            _rentalAlertDismissed = RentalAlertSignature(s);
        _rentalAlertActive = false;
        PlaceRentalAlert();
    }

    private void RentalAlertOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (_navButtons.TryGetValue("rentals", out var button))
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Раздел «Настройки сайта» — пункт меню и кнопка «Настройки сайта» в «Заказах с сайта».
    /// Права — как у «Настроек» (владелец/админ).</summary>
    public void OpenSiteSettings()
    {
        if (Authorize(PosPermissions.ViewSettings))
            OpenSection("sitesettings", () => new SiteSettingsWindow());
    }

    /// <summary>Раздел «Аналитика» на вкладке «Склад» — кнопка «Открыть аналитику склада» в
    /// Маркетплейсе. false — раздела нет (тариф «Старт»): тогда аналитика склада открывается
    /// по-старому, вкладкой склада.</summary>
    public bool OpenAnalyticsStock()
    {
        if (!TariffGate.CanUseSalesAnalytics)
            return false;
        if (Authorize(PosPermissions.ViewAnalytics))
        {
            OpenSection("analytics", () => App.GetRequiredService<FinanceWindow>().AsAnalyticsSection());
            (_sections.FirstOrDefault(s => s.Key == "analytics")?.Window as FinanceWindow)?.ShowStockTab();
        }
        return true;
    }

    // ------------------------------------------------------------------ ABC в сводке

    // 2026-09-27, владелец: «ABC анализ должен быть в сводке и он должен быть по всем». Все шесть
    // срезов за период сводки. Считается локально (AnalyticsReportData) в фоне и только когда это
    // нужно: при открытии, смене периода, «Обновить», смене языка и после новых продаж (с
    // задержкой, пачкой). Двадцатисекундное обновление сводки его не трогает: оно ходит за цифрами
    // сервера, а локальные строки продаж меняются только вместе с сигналом SalesChanged.
    // 2026-09-28, сверка с сайтом: ABC считается по вкладке «Товары» сайта (локальная история
    // кассы — только без связи): по ней ABC расходился с сайтом (440 тыс. против 586 тыс.). Раз
    // цифры теперь серверные, продажи других касс меняют их без сигнала SalesChanged — поэтому
    // обновление сводки заодно освежает ABC, но не чаще раза в минуту (один запрос к серверу).
    private DateTime _abcRefreshedUtc = DateTime.MinValue;

    private readonly DispatcherTimer _abcDebounce;
    private AbcSectionView? _abcView;
    private CancellationTokenSource? _abcCts;
    private DateTime _abcFrom = DateTime.Today;
    private DateTime _abcTo = DateTime.Today;

    /// <summary>Продажи изменились, пока сводка была скрыта разделом, — пересчитать при возврате.</summary>
    private bool _abcStale;

    /// <summary>Как у раздела «ABC-анализ»: не на «Старте» и с правом «Аналитика» (2026-10-01, как на сайте).</summary>
    private static bool AbcAllowed
    {
        get
        {
            if (!TariffGate.CanUseSalesAnalytics)
                return false;
            try
            {
                return App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewAnalytics);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Сигнал приходит из фонового потока и за один чек — несколько раз: собираем пачку
    /// таймером в UI-потоке.</summary>
    private void OnSalesChangedForAbc() => Dispatcher.UIThread.Post(() =>
    {
        _abcDebounce.Stop();
        _abcDebounce.Start();
        // 2026-10-29: «Сводка» — по событию, а не только по таймеру (не чаще раза в 3 с; скрытую/неактивную по тем же правилам, что таймер).
        // 2026-10-29 (скорость синхронизации): было 8 с; события приходят быстро (пульс 5 с у владельца, подсказка кассы по локальной сети).
        if (DateTime.UtcNow - _lastRefreshStartedUtc >= TimeSpan.FromSeconds(3))
            _ = RefreshWhenShownAsync();
    });

    private void RefreshAbcWhenVisible()
    {
        if (_activeSection != null)
        {
            _abcStale = true;
            return;
        }

        _ = RefreshAbcAsync();
    }

    private void DropAbcView()
    {
        _abcCts?.Cancel();
        if (_abcView != null)
        {
            _abcView.ProductAnalyticsRequested -= ShowAbcProductAnalytics;
            AbcHost.Children.Remove(_abcView);
            _abcView = null;
        }
    }

    private async Task RefreshAbcAsync()
    {
        var allowed = AbcAllowed;
        AbcCard.IsVisible = allowed;
        if (!allowed || _loggingOut || _cts.IsCancellationRequested)
            return;

        _abcStale = false;
        _abcRefreshedUtc = DateTime.UtcNow;
        _abcCts?.Cancel();
        var cts = new CancellationTokenSource();
        _abcCts = cts;
        var (from, to) = CurrentRange();
        AbcLoadingText.IsVisible = _abcView == null;
        try
        {
            // 2026-09-28: ABC — по вкладке «Товары» сайта (как на сайте), без связи — по истории кассы.
            var data = await AnalyticsReportData.BuildAsync(App.SalesApi, from, to, includeSeasonality: false, full: false, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;

            ShowAbc(data, from, to);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: ABC-анализ сводки не построен: {ex.Message}", "WARNING");
            AbcLoadingText.Text = Tr.T("Не удалось построить ABC-анализ", "ABC-анализди түзүү мүмкүн болгон жок", "Could not build the ABC analysis", "ABC analizi oluşturulamadı", "ABC tahlilini tuzib bo'lmadi");
            AbcLoadingText.IsVisible = _abcView == null;
        }
        finally
        {
            if (ReferenceEquals(_abcCts, cts))
                _abcCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Показывает готовый отчёт; сам блок собирается при первом показе (и заново после
    /// смены языка — см. DropAbcView).</summary>
    private void ShowAbc(AnalyticsReportData data, DateTime from, DateTime to)
    {
        if (_abcView == null)
        {
            _abcView = new AbcSectionView
            {
                SliceKeys = AbcSectionView.AllSliceKeys,
                ShowSeasonality = false,
                TableMaxHeight = 360,
                // 2026-10-21 (пакет 2, п. 86): без прокрутки внутри прокрутки — блок по высоте содержимого, листает сама Сводка.
                ScrollsVertically = false,
            };
            _abcView.ProductAnalyticsRequested += ShowAbcProductAnalytics;
            AbcHost.Children.Add(_abcView);
        }

        _abcFrom = from;
        _abcTo = to;
        _abcView.Update(data);
        AbcLoadingText.IsVisible = false;
    }

    // 2026-10-21 (пакет 2, п. 86): FitAbcHeight (блок ABC высотой в экран со своей прокруткой) убран — блок по содержимому.

    private void ShowAbcProductAnalytics(string productName)
    {
        try
        {
            new ProductAnalyticsWindow(productName, _abcFrom, _abcTo).Show(this);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Разбор товара не открылся: {ex}", "WARNING");
        }
    }

    private void AllSales_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-18: «Все продажи» — раздел «Продажи и финансы», вкладка «Чеки» (OwnerShellWindow.SalesFinance.cs).
        if (TariffGate.CanUseSalesAnalytics && Authorize(PosPermissions.ViewSales))
            OpenSalesFinance("checks");
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var newIsDark = !prefs.DarkTheme;
        prefs.DarkTheme = newIsDark;
        App.ApplyTheme(newIsDark);
        prefs.SaveToDisk();
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        if (this.TryFindResource(UserPreferences.Instance.DarkTheme ? "SunIcon" : "MoonIcon", out var icon) && icon is Geometry geometry)
            ThemeIconPath.Data = OldThemeIconPath.Data = geometry;
    }

    /// <summary>Как в меню кассы: закрыть программу и выйти на рабочий стол (вход при этом сохраняется).</summary>
    private void ExitToDesktop()
    {
        App.ExitWithoutLoginRedirect = true;
        Close();
    }

    private void ExitToDesktop_Click(object? sender, RoutedEventArgs e) => ExitToDesktop();

    /// <summary>Выход из учётной записи — как в кассе (MainWindow.NavigateToLoginAsync): стираем
    /// сессию и сохранённый вход, показываем окно входа. 2026-09-29: кнопка — в Настройки → Аккаунт
    /// («Выйти», AccountView.LogoutButton_Click), а не внизу меню.</summary>
    public async Task SignOutAsync()
    {
        _loggingOut = true;
        _timer.Stop();
        _siteOrdersTimer.Stop();
        try
        {
            App.AuthApi.ClearSession();
            await App.GetRequiredService<IAuthSessionManager>().ClearSessionAsync().ConfigureAwait(true);
            OfflineAuthSessionStore.Clear();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: logout cleanup failed: {ex.Message}", "WARNING");
        }

        CloseAllSections();
        var login = App.GetRequiredService<LoginWindow>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = login;
        login.Show();
        Close();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: failed to open {url}: {ex.Message}", "WARNING");
        }
    }
}
