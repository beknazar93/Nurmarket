using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.ViewModels.Settings;

namespace NurMarketKassa.AvaloniaHost;

public partial class App : Application
{
    public static IHost? AppHost { get; private set; }

    // Отменяется в desktop.Exit ДО того, как хост будет уничтожен — без этого автовход
    // (медленная/зависшая сеть) продолжает выполняться после DisposeApplicationHost() и
    // падает с ObjectDisposedException на уже освобождённых сервисах/DI-контейнере.
    private static readonly CancellationTokenSource ShutdownCts = new();

    // Bridge for ported services/views (same pattern as WPF App.*).
    public static ICatalogApiService CatalogApi { get; private set; } = null!;
    public static ISalesApiService SalesApi { get; private set; } = null!;
    public static IShiftApiService ShiftApi { get; private set; } = null!;
    public static IAuthApiService AuthApi { get; private set; } = null!;
    public static MySqlAuditService AuditDb { get; private set; } = null!;
    public static string? PosCashboxId { get; set; }
    public static bool ExitWithoutLoginRedirect { get; set; }
    public static bool IsOfflineBootstrap { get; set; }
    public static string? OfflineBootstrapMessage { get; set; }

    public static string? CurrentUserId
    {
        get => TryGetSession()?.CurrentUserId;
        set
        {
            var session = TryGetSession();
            if (session != null) session.CurrentUserId = value;
        }
    }

    public static string? CurrentUserDisplayName
    {
        get => TryGetSession()?.CurrentUserDisplayName;
        set
        {
            var session = TryGetSession();
            if (session != null) session.CurrentUserDisplayName = value;
        }
    }

    public static T GetRequiredService<T>() where T : notnull =>
        AppHost!.Services.GetRequiredService<T>();

    public static void ApplyTheme(bool dark)
    {
        if (Current is not App app)
            return;

        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentThemeService.Apply(UserPreferences.Instance.AccentTheme, dark);

        // Экран покупателя следует за темой кассы, когда в Настройки → Монитор выбрана
        // "Системная" (2026-09-07) — тот же ApplySettings-путь, что уже используется для
        // синхронизации акцентного цвета (см. SyncCustomerDisplayAccent) ниже.
        GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettings(UserPreferences.Instance.CustomerDisplay);
    }

    /// <summary>Меняет только акцентный цвет (кнопки/вкладки/цены/фокус во всей программе),
    /// не трогая светлый/тёмный режим. См. AccentThemeService.</summary>
    public static void ApplyAccentTheme(string themeId)
    {
        UserPreferences.Instance.AccentTheme = themeId;
        SyncCustomerDisplayAccent(themeId);
        UserPreferences.Instance.SaveToDisk();
        AccentThemeService.Apply(themeId, UserPreferences.Instance.DarkTheme);
        // Цвета «как тема кассы» у экрана покупателя (2026-09-28): фон, панели и текст — не только
        // акцент, поэтому перекрашиваем его при любой смене темы, а не только при смене акцента.
        GetRequiredService<AvaloniaCustomerDisplayService>().RefreshStyle();

        // "Жидкое стекло" включает имитацию живого блюра на кассе автоматически (без ручной
        // настройки обоев в Кастомизации) — остальные темы её выключают, если фото не выбрано.
        GetRequiredService<MainWindowHostBridge>().Window?.RefreshBackgroundWallpaper();
    }

    /// <summary>Переключает раскладку главного экрана кассы (2026-09-06) — "standard" (текущая,
    /// каталог+корзина рядом) или "onec" (альтернативная, в стиле 1С "Рабочее место кассира":
    /// крупные плитки, чек фиксированной колонкой справа, numpad для количества). Применяется
    /// мгновенно на уже открытой кассе — тот же трёхшаговый идиом, что и ApplyAccentTheme.</summary>
    public static void ApplyMainLayoutMode(string mode)
    {
        UserPreferences.Instance.MainLayoutMode = mode;
        UserPreferences.Instance.SaveToDisk();
        GetRequiredService<MainWindowHostBridge>().Window?.RefreshLayoutMode();
        // 2026-09-28, «при смене вида кассы меняй и 2 экран покупателя тоже»: открытый экран
        // покупателя с видом «как у кассы» перестраивается сразу, без перезапуска.
        GetRequiredService<AvaloniaCustomerDisplayService>().RefreshStyle();
    }

    /// <summary>Экран покупателя (2-й экран) подхватывает акцентный цвет выбранной темы кассы —
    /// при каждой смене темы, не только при первом запуске. Если окно экрана покупателя сейчас
    /// открыто, оно обновляется вживую через AvaloniaCustomerDisplayService.ApplySettings, а не
    /// только при следующем открытии.</summary>
    private static void SyncCustomerDisplayAccent(string themeId)
    {
        var swatchHex = AccentThemeService.GetAccentHex(themeId);
        if (swatchHex is null)
            return;

        var display = UserPreferences.Instance.CustomerDisplay;
        if (string.Equals(display.AccentColor, swatchHex, StringComparison.OrdinalIgnoreCase))
            return;

        display.AccentColor = swatchHex;
        GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettings(display);
    }

    private static ResourceDictionary LoadThemeDictionary(string source) =>
        AvaloniaXamlLoader.Load(new Uri(source, UriKind.Absolute)) as ResourceDictionary
        ?? throw new InvalidOperationException($"Failed to load theme: {source}");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>2026-10-04, Android-касса: заменить время жизни приложения (AndroidBootstrap.InstallLifetime).
    /// Только здесь — до base.RegisterServices: после неё Avalonia запрещает менять ApplicationLifetime
    /// (живое падение на телефоне). В Windows и Linux не задано.</summary>
    public static Action<Application>? BeforeRegisterServices { get; set; }

    public override void RegisterServices()
    {
        BeforeRegisterServices?.Invoke(this);
        base.RegisterServices();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppHost = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        PosLogger.Configure(AppHost.Services.GetRequiredService<ILoggerFactory>());
        // 2026-10-05: бонусы покупателей — на сервере NurCRM (ServerLoyalty).
        ServerLoyalty.ApiProvider = () => AppHost?.Services.GetService<NurMarketApiClient>();
        // 2026-10-05: настройки аккаунта — на сервере, подтягиваются на любом устройстве (SettingsCloudSync).
        SettingsCloudSync.ApiProvider = () => AppHost?.Services.GetService<NurMarketApiClient>();
        // 2026-10-06: срок долга, названный клиентом, — на сервер после продажи «В долг».
        DebtDueDateSync.ApiProvider = () => AppHost?.Services.GetService<NurMarketApiClient>();
        // Смена компании = смена набора локальных данных. Подписка здесь, один раз на запуск:
        // так разделение срабатывает на любом пути входа, включая смену кассира.
        CompanyInfoService.CompanyChanged += id => AccountDataIsolation.SwitchTo(id);
        // 2026-10-04: общий кеш размеров/цветов одежды (окно выбора размера, прокат, бот) — см. ProductVariantCache.
        ProductVariantCache.Loader = (productId, token) => CatalogApi.GetProductVariantsAsync(productId, token);
        CompanyInfoService.CompanyChanged += _ => ProductVariantCache.Clear();
        // 2026-10-06 (О-01, магазин одежды): справочник штрихкодов размеров — скан этикетки размера сразу добавляет
        // этот размер. Сервер такой штрихкод не находит (ТЗ бэкенда, часть 14, п. 14.4), поэтому касса в сфере «Одежда»
        // сама тихо обходит каталог (см. VariantBarcodeIndex). 2026-10-06 (О-74): и программа владельца — ей нужны остатки
        // размеров для «Заканчиваются размеры»; справочник общий, что проверила касса, программа не перепроверяет.
        CompanyInfoService.CompanyChanged += _ => VariantBarcodeIndex.Clear();
        _ = VariantBarcodeIndex.RunBackgroundAsync(
                () => MarketSpheres.IsClothing,
                () =>
                {
                    // Каталог обновляется в другом потоке — список мог поменяться во время перебора; тогда в следующий раз.
                    try
                    {
                        return CatalogCacheService.Products.ToList()
                            .Where(p => !p.MustWeigh && !p.IsService && !p.IsBundle && !string.IsNullOrWhiteSpace(p.Id))
                            .Select(p => p.Id)
                            .ToList();
                    }
                    catch (InvalidOperationException)
                    {
                        return Array.Empty<string>();
                    }
                },
                CancellationToken.None);
        // 2026-10-06, владелец: «очень долгая загрузка» отчёта «Размеры и цвета» — программа владельца в «Одежде» заранее
        // и медленно запоминает состав чеков за 30 дней (SaleSizesCache); отчёт потом читает с сервера только новые чеки.
        CompanyInfoService.CompanyChanged += _ => SaleSizesCache.Clear();
        if (NurMarketKassa.Services.AppMode.IsOwner)
            _ = SaleSizesCache.RunBackgroundAsync(
                () => MarketSpheres.IsClothing && TariffGate.CanUseSalesAnalytics && CompanyInfoService.LastCompany is not null,
                CancellationToken.None);
        // 2026-10-06, владелец: «к ИИ и боту дай полный доступ к товарам» — общие действия с товаром (ProductActions):
        // кнопка «±» склада, ИИ-советник и бот меняют остаток тем же документом ревизии, что и «Списание».
        ProductActions.InventoryApi = AppHost.Services.GetService<NurMarketKassa.Services.Api.IInventoryApiService>();
        // 2026-10-06, владелец: «дай доступ ИИ к табелю сотрудников» — те же смены и тот же расчёт, что окно «Табель».
        // 2026-10-06, владелец «почему долго??»: список смен сервер отдаёт 3–7 с — для ИИ он держится 10 минут, загрузка одна
        // на все запросы (чат, звонок, заранее при открытии советника).
        var shiftsLock = new object();
        Task<IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry>>? shiftsLoad = null;
        var shiftsAt = DateTime.MinValue;
        async Task<IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry>> LoadShiftsCachedAsync(CancellationToken token)
        {
            Task<IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry>> load;
            lock (shiftsLock)
            {
                // 3 минуты: только что закрытая смена должна быстро попасть в архив для ИИ.
                if (shiftsLoad is null || shiftsLoad.IsFaulted || (shiftsLoad.IsCompleted && DateTime.UtcNow - shiftsAt > TimeSpan.FromMinutes(3)))
                {
                    shiftsAt = DateTime.UtcNow;
                    shiftsLoad = ShiftHistoryService.LoadAsync(CancellationToken.None);
                }
                load = shiftsLoad;
            }
            var list = await load.WaitAsync(token).ConfigureAwait(false);
            if (list.Count == 0)
                lock (shiftsLock)
                    if (shiftsLoad == load)
                        shiftsLoad = null; // пусто — скорее всего, сервер не ответил: в следующий раз загрузить заново
            return list;
        }
        // 2026-10-06, владелец (снимок бота: «закрытых архивных Z-отчётов по прошлым сменам зафиксировано не было»):
        // «архив смен тоже показывай в Телеграм-боте». Последние закрытые смены — для ИИ в боте и в программе владельца.
        TelegramAiChat.ShiftArchiveProvider = async token =>
        {
            var shifts = await LoadShiftsCachedAsync(token).ConfigureAwait(false);
            var closed = shifts.Where(s => s.ClosedAt is not null).OrderByDescending(s => s.ClosedAt).Take(10).ToList();
            var sb = new System.Text.StringBuilder("АРХИВ СМЕН (Z-отчёты, последние закрытые смены с сервера NurCRM):\n");
            foreach (var s in closed)
                sb.Append($"• Смена {s.ShiftNumber}, кассир {s.Cashier}: открыта {s.OpenedAt:dd.MM.yyyy HH:mm}, закрыта {s.ClosedAt:dd.MM.yyyy HH:mm}; выручка {s.Revenue:0.##} сом"
                          + (s.CashSales is { } cash ? $" (наличные {cash:0.##}" + (s.NonCashSales is { } nc ? $", безнал {nc:0.##})" : ")") : "")
                          + (s.SalesCount is { } n ? $", чеков {n}" : "")
                          + (s.OpeningCash is { } op ? $", в кассе на начало {op:0.##}" : "")
                          + (s.ClosingCash is { } cl ? $", при закрытии {cl:0.##}" : "") + " сом\n");
            if (closed.Count == 0)
                sb.Append("Закрытых смен на сервере нет.\n");
            foreach (var open in shifts.Where(s => s.ClosedAt is null && s.OpenedAt is not null))
                sb.Append($"Открыта сейчас: смена {open.ShiftNumber}, кассир {open.Cashier}, с {open.OpenedAt:dd.MM.yyyy HH:mm}, выручка {open.Revenue:0.##} сом.\n");
            return sb.ToString();
        };
        TelegramAiChat.TimesheetProvider = async (from, to, token) =>
        {
            var shifts = await LoadShiftsCachedAsync(token).ConfigureAwait(false);
            var rows = StaffTimesheetService.Aggregate(shifts, from, to);
            static string H(TimeSpan t) => $"{(int)t.TotalHours} ч {t.Minutes:00} мин";
            var sb = new System.Text.StringBuilder($"ТАБЕЛЬ (смены кассы {from:dd.MM}–{to:dd.MM}; часы — по закрытым сменам):\n");
            foreach (var r in rows)
                sb.Append($"• {r.Cashier}: смен {r.ShiftCount}, дней {r.DaysWorked}, отработано {H(r.TotalWorked)} (в среднем {H(r.AverageShift)} за смену), "
                          + $"выручка смен {r.TotalRevenue:0.##} сом, первая смена {r.FirstShift:dd.MM HH:mm}, последняя {r.LastShift:dd.MM HH:mm}\n");
            if (rows.Count == 0)
                sb.Append("За этот период закрытых смен нет.\n");
            foreach (var open in shifts.Where(s => s.ClosedAt is null && s.OpenedAt is not null))
                sb.Append($"Сейчас на смене: {open.Cashier} с {open.OpenedAt:dd.MM HH:mm}.\n");
            return sb.ToString();
        };
        ProductActions.RequestCatalogRefresh = () => AppHost?.Services.GetService<SyncService>()?.RequestCatalogSyncNow();
        // Действие ИИ «photo» — тот же поиск фото, что у кнопки «Найди фото» советника (Open Food Facts, затем интернет).
        ProductActionPlan.PhotoFinder = async (product, token) =>
        {
            var (found, _) = await ProductPhotoFinder.SearchAsync(new[] { product }, null, token, webLimit: 1).ConfigureAwait(false);
            if (found.FirstOrDefault() is not { } candidate)
                return new ProductActions.Result(false, Tr.T($"«{product.Title}»: фото не нашлось.", $"«{product.Title}»: сүрөт табылган жок.", $"“{product.Title}”: no photo found.",
                    $"«{product.Title}»: fotoğraf bulunamadı.", $"«{product.Title}»: surat topilmadi."));
            var ok = await ProductPhotoFinder.ApplyAsync(candidate, token).ConfigureAwait(false);
            return new ProductActions.Result(ok, ok
                ? Tr.T($"«{product.Title}»: фото поставлено ({candidate.Source}).", $"«{product.Title}»: сүрөт коюлду ({candidate.Source}).", $"“{product.Title}”: photo set ({candidate.Source}).",
                    $"«{product.Title}»: fotoğraf eklendi ({candidate.Source}).", $"«{product.Title}»: surat qo'yildi ({candidate.Source}).")
                : Tr.T($"«{product.Title}»: фото не загрузилось.", $"«{product.Title}»: сүрөт жүктөлгөн жок.", $"“{product.Title}”: the photo didn't upload.",
                    $"«{product.Title}»: fotoğraf yüklenmedi.", $"«{product.Title}»: surat yuklanmadi."));
        };
        // 2026-10-05, ТЗ часть 7: события допродажи уходят на сервер (UpsellServerSync).
        UpsellServerSync.ApiProvider = () => AppHost?.Services.GetService<NurMarketKassa.Services.Api.RecommendationsApi>();
        // 2026-10-05, ТЗ часть 7, раздел 3: ошибки и падения — отчётами на сервер поддержки (ErrorReportService).
        var reportsApi = AppHost.Services.GetRequiredService<NurMarketApiClient>();
        ErrorReportService.Sender = body => reportsApi.RequestAsync(System.Net.Http.HttpMethod.Post, "api/support/error-reports/", body, null,
            CancellationToken.None, TimeSpan.FromSeconds(20));
        ErrorReportService.Start();
        // Догрузка истории продаж с сервера живёт в приложении, а вызывает её фоновая
        // синхронизация из Infrastructure — связываем их здесь.
        SalesHistoryBackfillHook.Register(SalesHistoryBackfill.RunAsync);
        RegisterGlobalExceptionHandlers();
        // 2026-09-30: все окна-модалки — в пределах экрана кассы (маленькие/квадратные экраны).
        NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit.RegisterForAllWindows();
        // 2026-10-04: два пальца на сенсоре — прокрутка, а не два нажатия сразу (см. TouchGuard).
        TouchGuard.Register();
        // 2026-10-06, моноблоки клиентов: касание поля ввода пальцем — клавиатура Windows (TouchKeyboardAuto).
        TouchKeyboardAuto.Register();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Never construct LoginWindow speculatively. The credential-free
            // splash remains the only visual until auto-auth reaches a final state.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            // 2026-09-23. Чекпоинт WAL висел ТОЛЬКО на desktop.Exit. Когда кассир
            // выключает компьютер через «Пуск», Windows шлёт запрос на завершение сеанса и
            // затем убивает процесс — Exit не срабатывает, и несведённый WAL остаётся на
            // диске. Это самый частый способ выключения кассы и наиболее вероятный
            // оставшийся источник «database disk image is malformed».
            desktop.ShutdownRequested += (_, _) =>
            {
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch (Exception ex) { NurMarketKassa.Services.PosLogger.Log($"WAL checkpoint (shutdown) failed: {ex.Message}", "WARNING"); }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch { /* процесс уже завершается — логировать некуда */ }
            };

            desktop.Exit += (_, _) =>
            {
                ShutdownCts.Cancel();
                try { NurMarketKassa.Services.Lan.LanSyncService.Instance.Stop(); }
                catch { /* соседи сами перестанут ждать это место через 45 секунд */ }
                // Сливает WAL в основной файл БД при штатном закрытии — без этого несколько раз
                // за 2026-09-04 недописанный WAL для редко используемых таблиц приводил к
                // "database disk image is malformed" при следующем запуске (см.
                // DatabaseService.CheckpointWal). Не спасает от taskkill /F (тот вообще не даёт
                // коду выполниться), но перекрывает обычное закрытие окна/Stop-Process.
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch { /* завершение процесса не должно зависеть от этого */ }
                DisposeApplicationHost();
            };
            // Ежедневная резервная копия локальной базы — в фоне, чтобы не задерживать показ
            // экрана кассира. В базе лежит очередь непроведённых офлайн-продаж, которой нет
            // больше нигде: сервер про неё по определению не знает.
            _ = Task.Run(() =>
            {
                try { NurMarketKassa.Services.DatabaseService.EnsureDailyBackup(); }
                catch { /* страховка не должна мешать запуску кассы */ }
            });

            _ = StartHostAndAuthenticationAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartHostAndAuthenticationAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        try
        {
            await AppHost!.StartAsync().ConfigureAwait(true);
            UiDispatcherHolder.Current = AppHost.Services.GetRequiredService<IDispatcher>();
            AvaloniaHostServiceRegistration.InitializeAuthInfrastructure(AppHost.Services);
            AvaloniaHostServiceRegistration.InitializePosInfrastructure(AppHost.Services);

            var settings = AppHost.Services.GetRequiredService<AppSettings>();
            var session = AppHost.Services.GetRequiredService<IAppSession>();
            CatalogApi = AppHost.Services.GetRequiredService<ICatalogApiService>();
            SalesApi = AppHost.Services.GetRequiredService<ISalesApiService>();
            ShiftApi = AppHost.Services.GetRequiredService<IShiftApiService>();
            AuthApi = AppHost.Services.GetRequiredService<IAuthApiService>();
            AuditDb = AppHost.Services.GetRequiredService<MySqlAuditService>();

            NurMarketKassa.App.InitializeFromHost(
                settings, AuthApi, CatalogApi, SalesApi, ShiftApi, AuditDb, session);
            NurMarketKassa.App.SyncFromSession(session);
            // X/Z-отчёт должен учитывать локальные внесения/изъятия текущей смены.
            CashShiftService.ShiftCashOperationsNetProvider = ShiftCashOperationsStore.NetForShift;
            PosCashboxId = NurMarketKassa.App.PosCashboxId;
            CurrentUserId = NurMarketKassa.App.CurrentUserId;
            ApplyTheme(UserPreferences.Instance.DarkTheme);
            LocalizationManager.Apply(LanguagePackGate.EnforceOnStartup());
            // 2026-09-07: если 15-минутный мастер-доступ истёк, пока касса была закрыта —
            // сбрасываем сразу при старте, а не ждём таймера (который тоже переставится ниже).
            LicenseKeys.ExpireIfDue();

            // Пока CrashReportService.UploadEndpoint не задан (появится в будущем обновлении) —
            // не делает ничего, отчёты просто продолжают копиться локально.
            _ = CrashReportService.TryUploadPendingReportsAsync();
            // 2026-10-01: отчёты о сбоях — в бот поддержки (если он подключён, см. SupportLogService).
            _ = SupportLogService.TrySendPendingCrashReportsAsync();
            // 2026-10-01: создать клиент бота на сервере заранее — опрос Telegram спрашивает у него режим.
            _ = AppHost!.Services.GetRequiredService<NurMarketKassa.Services.Api.ServerTelegramBotApi>();

            await CompleteAuthenticationStartupAsync(desktop, splash, session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Application startup canceled.", "DEBUG");
            if (!ShutdownCts.IsCancellationRequested)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application host startup failed: {ex}", "CRITICAL");
            // Хост уже мог быть уничтожен через desktop.Exit (ShutdownCts) — тогда
            // AppHost.Services недоступен, и попытка показать LoginWindow только
            // добавит вторую, ничего не объясняющую ошибку поверх первой.
            if (!ShutdownCts.IsCancellationRequested)
                ShowLoginWindow(desktop, splash);
        }
    }

    /// <summary>Сообщение, которое видит кассир при неожиданной ошибке — без технических деталей.</summary>
    private static string UserFacingErrorMessage => Tr.T(
        "Произошла ошибка в программе. Программисты уже знают о таких случаях и работают над " +
        "исправлением. Попробуйте повторить действие ещё раз — если ошибка повторяется, " +
        "сообщите администратору.", "Программада ката кетти. Программисттер мындай учурлардан кабардар жана аларды оңдоп жатышат. Аракетти дагы бир жолу кайталап көрүңүз — ката кайталанса, администраторго кабарлаңыз.", "An error occurred in the app. The developers are aware of this kind of issue and are working on a fix. Please try again — if the error persists, contact your administrator.", "Programda bir hata oluştu. Geliştiriciler bu tür durumlardan haberdar ve düzeltme üzerinde çalışıyor. İşlemi tekrar deneyin — hata devam ederse yöneticinize bildirin.", "Dasturda xatolik yuz berdi. Dasturchilar bunday holatlardan xabardor va tuzatish ustida ishlamoqda. Amalni yana bir bor takrorlab ko'ring — agar xato takrorlansa, administratorga xabar bering.");

    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            PosLogger.Log(
                $"Unhandled application exception. Terminating={args.IsTerminating}. {exception}",
                "CRITICAL");
            if (exception != null)
                CrashReportService.WriteReport(exception, $"AppDomain (Terminating={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            var onlyCancellation = args.Exception.Flatten().InnerExceptions.All(
                exception => exception is OperationCanceledException);
            PosLogger.Log(
                onlyCancellation
                    ? "Unobserved task completed by cancellation."
                    : $"Unobserved task exception: {args.Exception}",
                onlyCancellation ? "DEBUG" : "ERROR");
            if (!onlyCancellation)
                CrashReportService.WriteReport(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            PosLogger.Log($"Unhandled Avalonia UI exception: {args.Exception}", "CRITICAL");
            CrashReportService.WriteReport(args.Exception, "Avalonia UI thread");

            // 2026-10-04, владелец: «если 2 пальца касаются — появляется какая-то ошибка». Сбой во время касания
            // двумя пальцами записан выше (журнал и отчёт); окно «Произошла ошибка» кассиру не показываем.
            if (TouchGuard.MultiTouchRecently)
            {
                PosLogger.Log("Сбой во время касания двумя пальцами — окно ошибки не показано (подробности выше).", "WARNING");
                args.Handled = true;
                return;
            }

            // Пробуем удержать кассу живой вместо аварийного закрытия — ошибка уже
            // произошла и залогирована, но кассиру лучше увидеть понятное сообщение
            // и продолжить работу, чем потерять открытую смену/корзину из-за краша.
            try
            {
                var lifetime = Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
                PosDialogs.Error(owner, UserFacingErrorMessage, Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"));
                args.Handled = true;
            }
            catch (Exception dialogEx)
            {
                // Если даже диалог показать не удалось — не рискуем, даём приложению упасть штатно.
                PosLogger.Log($"Failed to show friendly error dialog: {dialogEx.GetType().Name}", "WARNING");
                args.Handled = false;
            }
        };
    }

    private static void DisposeApplicationHost()
    {
        if (AppHost is null)
            return;
        try
        {
            AppHost.Dispose();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application host disposal failed: {ex}", "ERROR");
        }
    }

    private static async Task CompleteAuthenticationStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        IAppSession appSession)
    {
        try
        {
            // 2026-09-10: если последний успешный вход на этом ПК был автономным (локальный
            // логин/пароль, без NurCRM) — тихо продолжаем его здесь же, ДО обычного
            // AutoLoginAsync ниже. Без этой ветки касса на каждом перезапуске сначала
            // пыталась бы восстановить старую (часто уже протухшую) NurCRM-сессию — экран
            // "Работать автономно" даже не успевал бы показаться, MainWindow открывался бы
            // сразу в обычном (сетевом) режиме. Уровень доверия тот же, что и у обычного
            // автовхода ниже — пароль повторно не спрашивается.
            if (await TryAutoResumeAutonomousSessionAsync(desktop, splash, appSession).ConfigureAwait(true))
                return;

            var authentication = AppHost!.Services
                .GetRequiredService<IOnlineOfflineAuthenticationService>();
            var result = await authentication.AutoLoginAsync(ShutdownCts.Token);

            if (!result.IsSuccess || result.Session is null)
            {
                ShowLoginWindow(desktop, splash);
                return;
            }

            ApplyAuthenticatedSession(appSession, result);
            NurMarketKassa.App.SyncFromSession(appSession);
            PosCashboxId = appSession.ActiveTerminal;

            // Разделение данных аккаунтов (AccountDataIsolation) здесь НЕ вызывается: на
            // автовходе компания ещё не загружена, а ключ по пользователю разрезал бы данные
            // одной компании между её кассирами. Оно произойдёт в MainWindow.InitializeApplicationAsync
            // сразу после CompanyInfoService.RefreshAsync — до любой продажи.
            AccountCatalogIsolation.PrepareForAuthenticatedUser("", appSession.CurrentUserId);
            AuditDb.LogEvent(
                "auth",
                "auto_login",
                new { mode = result.Mode.ToString(), userId = appSession.CurrentUserId },
                appSession.CurrentUserId);
            AppHost.Services.GetRequiredService<SyncService>().Start();
            NurMarketKassa.Services.Lan.LanSyncService.Instance.Start();

            var mainWindow = ResolveMainShell();
            var shell = (IMainShell)mainWindow;
            var canOpen = await shell.InitializeApplicationAsync(null, ShutdownCts.Token);
            if (!canOpen)
            {
                splash.Close();
                desktop.Shutdown();
                return;
            }

            desktop.MainWindow = mainWindow;
            shell.PlaceOnPrimaryScreen();
            mainWindow.Show();
            splash.Close();
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Automatic startup authentication canceled (app closing).", "AUTH");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Automatic startup authentication failed: {ex}", "AUTH");
            // Если приложение уже закрывается (ShutdownCts), AppHost/desktop уничтожены —
            // показывать окно входа здесь означает обращаться к disposed-объектам.
            if (!ShutdownCts.IsCancellationRequested)
                ShowLoginWindow(desktop, splash);
        }
    }

    /// <summary>2026-09-10: см. вызов в CompleteAuthenticationStartupAsync. Возвращает true, если
    /// автономная сессия была продолжена (тогда MainWindow уже показан или приложение уже
    /// закрывается — вызывающий код должен просто return) — false означает "на этом ПК автономный
    /// режим не активен/не был последним входом", и обычный AutoLoginAsync должен выполняться как
    /// раньше, без каких-либо изменений.</summary>
    private static async Task<bool> TryAutoResumeAutonomousSessionAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        IAppSession appSession)
    {
        // Программа владельца работает только с NurCRM: автономного (локального) входа у неё нет.
        if (NurMarketKassa.Services.AppMode.IsOwner)
            return false;

        var autonomous = AppHost!.Services.GetRequiredService<IAutonomousAuthService>();
        var (resumed, email, displayName) = autonomous.TryAutoResume();
        if (!resumed)
            return false;

        appSession.CurrentUserId = email;
        appSession.CurrentUserDisplayName = displayName;
        appSession.PosCashboxDisplayName = displayName;
        appSession.ActiveTerminal = null;
        appSession.IsOfflineBootstrap = true;
        appSession.OfflineBootstrapMessage = Tr.T("Автономный режим — работа без интернета.", "Автономдук режим — интернетсиз иштөө.", "Offline mode — working without internet.", "Çevrimdışı mod — internetsiz çalışma.", "Oflayn rejim — internetsiz ishlash.");

        NurMarketKassa.App.SyncFromSession(appSession);
        PosCashboxId = appSession.ActiveTerminal;
        IsOfflineBootstrap = true;
        OfflineBootstrapMessage = appSession.OfflineBootstrapMessage;

        // Ни CompanyInfoService.RefreshAsync, ни AccountCatalogIsolation.PrepareForAuthenticatedUser,
        // ни SyncService.Start() — все три про NurCRM, которого у автономного аккаунта нет (см. тот
        // же комментарий в LoginWindow.axaml.cs.OnLoginSuccess).
        var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
        var canOpen = await mainWindow.InitializeApplicationAsync(null, ShutdownCts.Token).ConfigureAwait(true);
        if (!canOpen)
        {
            splash.Close();
            desktop.Shutdown();
            return true;
        }

        desktop.MainWindow = mainWindow;
        mainWindow.PlaceOnPrimaryScreen();
        mainWindow.Show();
        splash.Close();
        return true;
    }

    private static void ApplyAuthenticatedSession(
        IAppSession appSession,
        AuthenticationResult result)
    {
        var authenticated = result.Session!;
        var offline = result.Mode == AuthenticationMode.Offline;

        appSession.CurrentUserId = authenticated.UserId;
        appSession.CurrentUserDisplayName = authenticated.DisplayName;
        appSession.ActiveTerminal = authenticated.BranchId;
        appSession.PosCashboxDisplayName = authenticated.DisplayName;
        appSession.IsOfflineBootstrap = offline;
        appSession.OfflineBootstrapMessage = offline
            ? Tr.T("Нет связи с сервером. Используются локальные данные.", "Сервер менен байланыш жок. Жергиликтүү маалыматтар колдонулууда.", "No connection to the server. Using local data.", "Sunucuyla bağlantı yok. Yerel veriler kullanılıyor.", "Server bilan aloqa yo'q. Mahalliy ma'lumotlar ishlatilmoqda.")
            : null;

        IsOfflineBootstrap = offline;
        OfflineBootstrapMessage = appSession.OfflineBootstrapMessage;
        CurrentUserId = authenticated.UserId;
    }

    /// <summary>Главное окно после входа: касса или программа владельца (см. AppMode). Три места
    /// входа — автовход, автономный вход и окно входа — берут окно только отсюда.</summary>
    internal static Window ResolveMainShell() =>
        NurMarketKassa.Services.AppMode.IsOwner
            ? GetRequiredService<OwnerShellWindow>()
            : GetRequiredService<MainWindow>();

    private static void ShowLoginWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        // This is the only place in startup where LoginWindow is resolved.
        var loginWindow = AppHost!.Services.GetRequiredService<LoginWindow>();
        desktop.MainWindow = loginWindow;
        loginWindow.Show();
        splash.Close();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            NurMarketKassa.Services.AppMode.DataFolderName,
            "Logs");
#if DEBUG
        const LogLevel minimumLogLevel = LogLevel.Debug;
#else
        const LogLevel minimumLogLevel = LogLevel.Information;
#endif
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(minimumLogLevel);
            builder.AddProvider(new SafeFileLoggerProvider(
                Path.Combine(logDirectory, "nurmarket-kassa.log"),
                minimumLogLevel));
        });

        services.AddSingleton<IDispatcher, AvaloniaDispatcher>();
        services.AddSingleton<ProductThumbService>();
        services.AddSingleton<ISettingsImagePicker, AvaloniaSettingsImagePicker>();
        services.AddSingleton<IOperatingSystemKeyboardService, WindowsOperatingSystemKeyboardService>();
        services.AddSingleton<IAppSession, AvaloniaAppSession>();
        services.AddSingleton<IWindowService, AvaloniaWindowService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();

        AvaloniaHostServiceRegistration.AddAuthInfrastructure(services);
        AvaloniaHostServiceRegistration.AddPosInfrastructure(services);
        MainViewModelRegistration.AddMainWindowViewModels(services);

        // ViewModels (host-local + shared)
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<WarehouseViewModel>();

        // Windows / views
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainWindow>();
        services.AddTransient<OwnerShellWindow>();
        services.AddTransient<CheckoutDialog>();
        services.AddTransient<AdminSupportWindow>();
        services.AddTransient<WarehouseWindow>();
        services.AddTransient<ShiftsHistoryWindow>();
        services.AddTransient<ShiftHistoryView>();
        services.AddTransient<ShiftSummaryView>();
        services.AddTransient<ServicesWindow>();
        services.AddTransient<SalesWindow>();
        services.AddTransient<AbcAnalysisWindow>();
        services.AddTransient<FinanceWindow>();
        services.AddTransient<IrregularReceiptsWindow>();
        services.AddTransient<ScalesPluWindow>();
        services.AddTransient<LogsAndErrorsWindow>();
        services.AddTransient<RemoteSupportWindow>();
        services.AddTransient<KnowledgeBaseWindow>();
        services.AddTransient<RestockSuggestionsWindow>();
        services.AddTransient<ClientsWindow>();
        services.AddTransient<PayDebtDialog>();
        services.AddTransient<CrmWebViewWindow>();
        services.AddTransient<PosSettingsWindow>();
        services.AddTransient<FilterWindow>();
        services.AddTransient<OpenShiftDialog>();
        services.AddTransient<CloseShiftDialog>();
        services.AddTransient<CashOperationsDialog>();
        services.AddTransient<CashHistoryDialog>();
        services.AddTransient<ReturnSaleDialog>();
        services.AddTransient<ReturnLineReasonDialog>();
        services.AddTransient<ProductDetailDialog>();
        services.AddTransient<WeighedProductDialog>();
        services.AddTransient<OrderDiscountDialog>();
        services.AddTransient<DeferredCartsDialog>();
        services.AddTransient<ReceiptPreviewDialog>();
        services.AddTransient<FinanceDateRangeDialog>();
        services.AddTransient<FrmKeyboard>();
        services.AddTransient<NoStockDialog>();
        services.AddTransient<NewOperationDialog>();
        services.AddTransient<ShiftDetailsDialog>();
        services.AddTransient<ShiftActionsMenu>();
        services.AddTransient<PosAlertDialog>();
        services.AddTransient<PosConfirmDialog>();
        services.AddTransient<PaymentConfirmationDialog>();
        services.AddTransient<PrinterNotConnectedDialog>();
        services.AddTransient<SaleSuccessDialog>();
        services.AddTransient<PaymentStockBlockedDialog>();
        services.AddTransient<DeferredStockIssuesDialog>();
    }

    private static IAppSession? TryGetSession()
    {
        try { return AppHost?.Services.GetService<IAppSession>(); }
        catch (Exception ex)
        {
            PosLogger.Log($"Session resolution failed: {ex}", "WARNING");
            return null;
        }
    }
}
