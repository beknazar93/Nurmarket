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
using Avalonia.Platform.Storage;
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
    // 2026-11-06 (В11, Л4-06 хвост): «Сегодня» и периоды «Сводки» — по серверным часам (ServerClock.Today), а не по часам ПК.
    private DateTime _customFrom = ServerClock.Today;
    private DateTime _customTo = ServerClock.Today;
    private bool _refreshing;
    // 2026-11-03 (С-13, аудит «зависание при медленном сервере»): токен текущей загрузки «Сводки» и признак «начать заново» — смена периода и «Обновить» отменяют идущую
    // загрузку, а не ждут её до 55 с (раньше новый запрос просто не стартовал, цифры нового периода приходили после старой загрузки).
    private CancellationTokenSource? _refreshCts;
    private bool _refreshAgain;
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
        // 2026-11-06 (В11, шаг 22, Н-13): имя замка — Local\, как в Program.cs.
        if (!Mutex.TryOpenExisting(@"Local\NurMarketKassa-SingleInstance", out var mutex))
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
        // 2026-11-01, ThinkPad: «Масштаб интерфейса» действует и на программу владельца (OwnerShellWindow.Nav.cs → AttachUiScale).
        AttachUiScale();
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
        // 2026-11-06, А-8: меню пересобирается только если реально изменился состав пунктов (OwnerNavSignature), а не от каждого события
        // прав/тарифа/«Разделов меню» — иначе левая колонка «мигала» и значки прыгали, хотя набор пунктов тот же.
        Action rebuildNavigation = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { BuildNavigation(onlyIfChanged: true); _ = RefreshDebtsCardAsync(force: true); });
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
        _timer.Tick += async (_, _) => await RefreshWhenShownAsync(byTimer: true).ConfigureAwait(true);
        // 2026-10-04, п. 7: окно снова активно, а «Сводка» давно не обновлялась (пока окно было свёрнуто
        // или владелец работал в другой программе) — обновить сразу, не дожидаясь таймера.
        Activated += async (_, _) =>
        {
            RenderDebtsCardIfStale(); // 2026-11-01 (Н-01): карточка «Долги» не рисовалась, пока окно было свёрнуто
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
        _siteOrdersTimer.Tick += (_, _) =>
        {
            // 2026-11-02: опрос заказов (значок и «Требует внимания») — раз в минуту при работе, раз в 2 минуты, когда окно не в фокусе или свёрнуто,
            // раз в 5 минут в простое. Открытый раздел «Заказы» обновляется сам (SiteOrdersWindow).
            var gap = SyncCenter.IsQuiet() ? TimeSpan.FromMinutes(5) : (!IsActive || WindowState == WindowState.Minimized) ? TimeSpan.FromMinutes(2) : TimeSpan.Zero;
            if (DateTime.UtcNow - _siteOrdersPolledUtc < gap - TimeSpan.FromSeconds(5))
                return;
            _siteOrdersPolledUtc = DateTime.UtcNow;
            _ = PollSiteOrdersAsync();
        };
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
        // 2026-11-06: заранее — «Вход истекает через N ч» в шапке и мягкое окно за 3 ч (refresh-токен живёт 7 суток от входа по паролю).
        var refreshPrompt = NurMarketKassa.AvaloniaHost.Services.RefreshExpiryPrompt.Attach(this, SignOutAsync);
        Closed += (_, _) => refreshPrompt.Dispose();
        void ShowLoginExpiry() => Dispatcher.UIThread.Post(() =>
        {
            // 2026-11-06 (Б-8): сессия потеряна — в шапке «🔑 Войдите снова» (как в кассе), по нажатию — вопрос «Сессия истекла»; подпись «Сводки» тоже честная.
            string? text;
            if (SessionExpiryMonitor.IsExpired)
            {
                text = SessionExpiryMonitor.HeaderLabel;
                UpdatedText.Text = ExpiredHeaderText();
                UseBrush(LiveDot, Shape.FillProperty, "BrushWarning");
            }
            else
                text = RefreshExpiryWatcher.Current.WarningText;
            LoginExpiryText.Text = text ?? "";
            LoginExpiryText.IsVisible = text is not null;
        });
        LoginExpiryText.Cursor = new Cursor(StandardCursorType.Hand);
        LoginExpiryText.PointerPressed += (_, e) =>
        {
            if (!SessionExpiryMonitor.IsExpired)
                return;
            e.Handled = true;
            _ = NurMarketKassa.AvaloniaHost.Services.SessionExpiredPrompt.AskAsync(this, SignOutAsync);
        };
        RefreshExpiryWatcher.Current.Changed += ShowLoginExpiry;
        SessionExpiryMonitor.Changed += ShowLoginExpiry;
        Tr.LanguageChanged += ShowLoginExpiry;
        Closed += (_, _) =>
        {
            RefreshExpiryWatcher.Current.Changed -= ShowLoginExpiry;
            SessionExpiryMonitor.Changed -= ShowLoginExpiry;
            Tr.LanguageChanged -= ShowLoginExpiry;
        };
        ShowLoginExpiry();
        // 2026-11-05 (аудит №4, Л4-11): раз в минуту — закрыть спрятанные разделы, которые давно не открывали (память).
        var idleSectionsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        idleSectionsTimer.Tick += (_, _) => TrimIdleHiddenSections();
        Opened += (_, _) => idleSectionsTimer.Start();
        Closed += (_, _) => idleSectionsTimer.Stop();
        Tr.LanguageChanged += OnLanguageChanged;
        LanSyncService.Instance.PeerDataChanged += OnLanPeerData;
        PosDataEvents.SalesChanged += OnSalesChangedForAbc;
        ShowcaseApiService.NewOrdersCountChanged += OnSiteOrdersCountChanged;
        UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");

        // 2026-10-25, ТЗ «оприходование в фоне» (Q5): значок «в фоне: N» в шапке (скрыт, пока приходов в фоне нет); нажатие — окно «Приходы в фоне».
        // 2026-11-06 (Б-6): значок — компактный и внутри строки «дата · Обновлено…» (WrapPanel переносит его строкой ниже), а не поверх заголовка; полный текст — в подсказке.
        if (NurMarketKassa.AvaloniaHost.Views.Main.Controls.BackgroundReceiptsBadge.Create(compact: true) is { } bgReceiptsBadge)
        {
            bgReceiptsBadge.HorizontalAlignment = HorizontalAlignment.Left;
            bgReceiptsBadge.Margin = new Thickness(12, 0, 0, 0);
            TitleMetaPanel.Children.Add(bgReceiptsBadge);
        }
        // 2026-11-02 (аудит №2, В3-01): несохранённые правки разделов (редактор сайта, настройки, приёмка…) при выходе не теряются молча —
        // вопрос «Сохранить и выйти / Выйти без сохранения / Остаться».
        Closing += async (_, e) =>
        {
            // 2026-11-06: неотправленные приходы и новые товары — вопрос «Выйти / Подождать» ДО закрытия, пока окно-владелец живо
            // (раньше из ShutdownRequested после закрытия: «Cannot show a window with a closed owner»). Согласились — закрываем повторно.
            if (!_exitConfirmed && !_loggingOut && !_closingAllSections && !_unsentChecked && !QuietShutdown.ShouldSkipPrompts(e) && BackgroundReceiptsHost.HasUnsent())
            {
                e.Cancel = true;
                if (_exitAsking)
                    return;
                _exitAsking = true;
                try
                {
                    if (await BackgroundReceiptsHost.ConfirmUnsentBeforeExitAsync(this).ConfigureAwait(true))
                    {
                        _unsentChecked = true;
                        Close();
                    }
                }
                finally
                {
                    _exitAsking = false;
                }
                return;
            }
            if (_exitConfirmed || _loggingOut || _closingAllSections)
            {
                CloseAllSections();
                return;
            }
            if (DirtySections().Count == 0)
            {
                CloseAllSections();
                return;
            }
            // 2026-11-05 (аудит №3, Р3-02): выключение Windows / установщик («--close-quiet») — вопрос «Сохранить и выйти?» без срока держал выход,
            // через 15 с процесс убивали. Разделы, что умеют сохраняться сами (редактор сайта), сохраняем быстро (до 3 с), остальное — уходит, как было бы
            // без ответа; вопрос не показываем. Обычное закрытие кнопкой — как раньше.
            if (QuietShutdown.ShouldSkipPrompts(e))
            {
                var quietSave = DirtySections().Where(s => s.Window is IOwnerSectionExit).ToList();
                if (quietSave.Count == 0)
                {
                    CloseAllSections();
                    return;
                }
                e.Cancel = true;
                if (_exitAsking)
                    return;
                _exitAsking = true;
                try
                {
                    foreach (var section in quietSave)
                    {
                        try
                        {
                            var save = ((IOwnerSectionExit)section.Window).SaveForExitAsync();
                            await Task.WhenAny(save, Task.Delay(3000)).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Owner app: тихое сохранение раздела «{TitleFor(section)}» не удалось ({ex.Message}).", "WARNING");
                        }
                    }
                }
                finally
                {
                    _exitAsking = false;
                }
                _exitConfirmed = true;
                Close();
                return;
            }
            e.Cancel = true;
            if (_exitAsking)
                return;
            _exitAsking = true;
            try
            {
                if (await ConfirmLeaveAsync().ConfigureAwait(true))
                {
                    _exitConfirmed = true;
                    Close();
                }
            }
            finally
            {
                _exitAsking = false;
            }
        };
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
            // 2026-11-03 (С-03): startup — свежую компанию окна входа не грузим второй раз, а при медленном сервере (8 с) открываемся с кэшем подписки.
            var subscription = await CompanyInfoService.RefreshAsync(App.AuthApi, cancellationToken, startup: true).ConfigureAwait(true);
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
        // 2026-11-05 (аудит №3, В4-10): карточки, собранные кодом и обновляемые раз в 5 минут, после смены языка оставались на прежнем (турецкая Сводка с «План продаж на ноябрь»).
        _salesPlanEditing = false;
        _ = RefreshSalesPlanAsync(force: true);
        _ = RefreshDebtsCardAsync(force: true);
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
        var today = ServerClock.Today.ToString("dddd, d MMMM yyyy", culture);
        DateText.Text = today.Length > 0 ? char.ToUpper(today[0], culture) + today[1..] : today;
        // 2026-10-18, этап О1: на узкой шапке (1024×768) — без дня недели (FitHeaderTools).
        _dateFull = DateText.Text;
        _dateShort = ServerClock.Today.ToString("d MMMM yyyy", culture);
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
    /// <summary>2026-11-06, А-8: подпись состава меню после последней сборки (OwnerNavSignature); null — меню ещё не строилось.</summary>
    private string? _navSignature;

    /// <param name="onlyIfChanged">События прав/тарифа/«Разделов меню»: сначала «сухой» проход — собирается только подпись состава
    /// (какие пункты видны); меню пересоздаётся, лишь если подпись изменилась. Язык, вид меню, телефон — вызывают без параметра (всегда).</param>
    private void BuildNavigation(bool onlyIfChanged = false)
    {
        var dry = onlyIfChanged && _navSignature != null;
        var signature = new OwnerNavSignature();
        // 2026-11-06, А-8: при «сухом» проходе колонку не трогаем — только считаем подпись состава (см. Add ниже).
        var navScroll = dry ? null : SaveNavScroll();
        if (!dry)
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
        }
        var pendingGroup = (string?)null;
        var pendingGroupKey = (string?)null;
        var currentGroupKey = (string?)null;

        // 2026-10-04: на телефоне меню открывается на весь экран — всегда с колонкой пунктов (OwnerShellWindow.Phone.cs).
        if (!dry)
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
                signature.Group(pendingGroupKey!, pendingGroup);
                if (!dry)
                    AddNavGroup(pendingGroupKey!, pendingGroup);
                currentGroupKey = pendingGroupKey;
                pendingGroup = null;
            }
            signature.Item(key, text);
            if (dry)
                return;

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
                    // 2026-11-09: если права перепроверяются на сервере (Authorize), после подтверждения раздел откроется этим же действием.
                    _navOpenInProgress = open;
                    open();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: section failed to open: {ex}", "ERROR");
                    // 2026-11-02 (В3-02): без сырого текста исключения — подробности в журнале.
                    PosMessageBox.Show(this, Tr.T("Раздел не открылся. Подробности — в журнале ошибок.", "Бөлүм ачылган жок. Чоо-жайы — каталар журналында.",
                        "The section did not open. See the error log for details.", "Bölüm açılamadı. Ayrıntılar hata günlüğünde.", "Bo'lim ochilmadi. Tafsilotlar xatolar jurnalida."),
                        Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
                finally
                {
                    _navOpenInProgress = null;
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
        // 2026-11-10, владелец: «возвраты добавь в программу владельца тоже» — то же окно возврата, что в кассе («Возврат товаров по чеку»: фильтры, поиск по товару,
        // возврат долгового чека в счёт долга), через общий ReturnSaleLauncher. Виден только с правом «Возврат» (у владельца — всегда); скрывается в «Разделах меню».
        Add("salereturn", "ReturnIcon", Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), HasPermissionSafe(PosPermissions.EmployeeReturn),
            () => _ = ReturnSaleLauncher.OpenAsync(this));
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
        // 2026-11-08 (ответ Умара 08.11, п.39 Б-95 и п.37 Б-93): «Отзывы» (модерация) и «Коллекции» витрины — рядом с «Заказами с сайта»;
        // видны только если витрина подключена (владелец или админ с услугой «Онлайн витрина»), иначе скрыты.
        Add("sitereviews", "StoreIcon", Tr.T("Отзывы", "Пикирлер", "Reviews", "Yorumlar", "Sharhlar"), ShowcaseApiService.IsShowcaseConnected,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("sitereviews", () => new SiteReviewsWindow()); });
        Add("sitecollections", "StoreIcon", Tr.T("Коллекции", "Коллекциялар", "Collections", "Koleksiyonlar", "Kolleksiyalar"), ShowcaseApiService.IsShowcaseConnected,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("sitecollections", () => new SiteCollectionsWindow()); });
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
        // 2026-11-08, запрос владельца «вернуть табель»: «Табель» (отметки сотрудников по дням + итоги по сменам) — владельцу всегда, без покупки в Маркетплейсе (в кассе гейт прежний).
        // Права — как у «Зарплаты»; деньги и расчёт зарплаты окно не трогает.
        Add("timesheet", "TimesheetIcon", Tr.T("Табель", "Табель", "Timesheet", "Çizelge", "Tabel"), true,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("timesheet", () => new StaffTimesheetWindow()); });
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
        // 2026-11-06: «Неотправленное» — всё, что не ушло на сервер (приходы, новые товары, чеки и операции этого компьютера), с ручной отправкой.
        Add("outbox", "RefreshIcon", Tr.T("Неотправленное", "Жөнөтүлбөгөндөр", "Unsent", "Gönderilmeyenler", "Yuborilmaganlar"), true,
            () => OutboxWindow.ShowOrActivate(this));
        Add("logs", "ErrorLogIcon", Tr.T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali"), TariffGate.CanUseService,
            () => OpenSection("logs", () => App.GetRequiredService<LogsAndErrorsWindow>()));

        // Как в меню кассы: закрыть программу и выйти на рабочий стол (вход при этом сохраняется).
        // 2026-10-18, этап О2: группы «Система» в тёмной колонке нет — «Выйти» внизу неё; пункт остаётся для поиска Ctrl+K.
        Group("system", Tr.T("Система", "Система", "System", "Sistem", "Tizim"));
        Add("exit", "ExitIcon", Tr.T("Выйти на рабочий стол", "Иш столуна чыгуу", "Exit to desktop", "Masaüstüne çık", "Ish stoliga chiqish"), true,
            ExitToDesktop);

        if (dry)
        {
            // Состав меню не изменился — колонка остаётся как есть (прокрутка, наведение, фокус, плотность не сбрасываются).
            if (OwnerNavSignature.NeedsRebuild(_navSignature, signature.ToString()))
                BuildNavigation();
            return;
        }
        _navSignature = signature.ToString();
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
        RestoreNavScroll(navScroll);
    }

    /// <summary>2026-11-06, А-8: полная пересборка меню не должна сбрасывать прокрутку колонок (тёмной, пунктов, старого вида).</summary>
    private (ScrollViewer View, Vector Offset)[]? SaveNavScroll()
    {
        var railScroll = RailPanel.FindAncestorOfType<ScrollViewer>();
        var views = new[] { railScroll, NavItemsScroll, OldNavScroll };
        return views.Where(v => v != null && v.Offset.Y > 0).Select(v => (v!, v!.Offset)).ToArray();
    }

    private void RestoreNavScroll((ScrollViewer View, Vector Offset)[]? saved)
    {
        if (saved == null || saved.Length == 0)
            return;
        // Размеры содержимого известны только после разметки.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var (view, offset) in saved)
                view.Offset = offset;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
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
}
