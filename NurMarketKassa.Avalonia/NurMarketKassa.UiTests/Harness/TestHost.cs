using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NurMarketKassa.AvaloniaHost;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using AvaloniaApp = NurMarketKassa.AvaloniaHost.App;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): хост служб кассы для окон, которые берут сервисы из DI (App.AppHost): тот же <c>App.ConfigureServices</c>, что и в программе
/// (через отражение — метод закрытый, приложение не менялось), тот же порядок инициализации, что в StartHostAndAuthenticationAsync.
/// Сервер — поддельный NurCRM (WireMock, UiFake). Данные — во временной папке (TestDataRoot).
/// Хост один на весь прогон (как App), адрес сервера выбирается при первом создании.</summary>
public static class TestHost
{
    private static readonly object Gate = new();
    private static IHost? _host;

    public static IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Хост не создан: вызовите TestHost.Ensure().");

    /// <summary>Создаёт хост, если ещё нет. Сервер — общий поддельный NurCRM (<see cref="UiFake"/>), адрес берётся при первом создании.</summary>
    public static IServiceProvider Ensure()
    {
        lock (Gate)
        {
            if (_host is not null)
                return _host.Services;

            Environment.SetEnvironmentVariable("DESKTOP_MARKET_API_URL", UiFake.Url);

            var configure = typeof(AvaloniaApp).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("App.ConfigureServices не найден — приложение изменилось, обновите TestHost.");
            var host = Host.CreateDefaultBuilder()
                .ConfigureServices(services => configure.Invoke(null, [services]))
                .Build();
            host.StartAsync().GetAwaiter().GetResult();

            SetStatic(nameof(AvaloniaApp.AppHost), host);
            UiDispatcherHolder.Current = host.Services.GetRequiredService<IDispatcher>();
            // Класс регистрации служб закрытый (internal) — вызываем через отражение, как и ConfigureServices.
            var registration = typeof(AvaloniaApp).Assembly.GetTypes().Single(t => t.Name == "AvaloniaHostServiceRegistration");
            registration.GetMethod("InitializeAuthInfrastructure", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [host.Services]);
            registration.GetMethod("InitializePosInfrastructure", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [host.Services]);

            var settings = host.Services.GetRequiredService<AppSettings>();
            var session = host.Services.GetRequiredService<IAppSession>();
            var catalog = host.Services.GetRequiredService<ICatalogApiService>();
            var sales = host.Services.GetRequiredService<ISalesApiService>();
            var shift = host.Services.GetRequiredService<IShiftApiService>();
            var auth = host.Services.GetRequiredService<IAuthApiService>();
            var audit = host.Services.GetRequiredService<MySqlAuditService>();
            SetStatic(nameof(AvaloniaApp.CatalogApi), catalog);
            SetStatic(nameof(AvaloniaApp.SalesApi), sales);
            SetStatic(nameof(AvaloniaApp.ShiftApi), shift);
            SetStatic(nameof(AvaloniaApp.AuthApi), auth);
            SetStatic(nameof(AvaloniaApp.AuditDb), audit);
            NurMarketKassa.App.InitializeFromHost(settings, auth, catalog, sales, shift, audit, session);
            NurMarketKassa.App.SyncFromSession(session);

            _host = host;
            return host.Services;
        }
    }

    private static Task? _warm;

    /// <summary>Одинаковое исходное состояние для всех тестов: кассир вошёл, каталог (2 товара поддельного сервера) загружен — иначе вид окон зависел бы от того,
    /// успел ли ранний тест войти и подгрузить товары. Выполняется один раз; ждать нужно через await (поток интерфейса не блокируется: загрузка каталога
    /// сама обращается к нему).</summary>
    public static async Task WarmAsync()
    {
        var services = Ensure();
        await (_warm ??= Task.Run(async () =>
        {
            await services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678").ConfigureAwait(false);
            await CatalogCacheService.SyncCatalogFullAsync().ConfigureAwait(false);
            _seedProducts = CatalogCacheService.Products.ToList();
        }));
        // 2026-11-20: тесты со «сохранить список → очистить → вернуть» (Штрихкоды, Приёмка…) запоминают список В КОНСТРУКТОРЕ, до первого WarmAsync; если такой тест оказывался
        // первым, его Dispose «возвращал» пустой список, и все следующие сценарии (плитка «Хлеб», пустой каталог в снимках) видели каталог без товаров. Пустой каталог на
        // входе теста — всегда следствие такой утечки (тесты, которым нужен пустой каталог, очищают его сами внутри), поэтому возвращаем исходные товары поддельного сервера.
        // 2026-11-21 (прогон 1.17.91): окно кассы читает state.json (вкладка каталога, ширины колонок) — файл общий на весь прогон; сценарий со сферой «Услуги» оставлял вкладку «Услуги»,
        // и следующие окна кассы (тур, «первый час», снимок main-kassa) открывались на пустой вкладке. Каждый тест начинает без чужого сохранённого состояния окна.
        NurMarketKassa.ViewModels.Main.ApplicationStateService.CancelPendingSaves();
        // 2026-11-21: то же для barcode-print-settings.json — окно «Штрихкоды» восстанавливает последний фильтр («Со штрихкодом» после соседнего сценария), и кнопка «Сгенерировать штрихкоды (N)» не появлялась.
        foreach (var persisted in new[] { "state.json", "barcode-print-settings.json" })
        {
            try { File.Delete(Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, persisted)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (_seedProducts is { Count: > 0 } seed && CatalogCacheService.Products.Count == 0)
            CatalogCacheService.Products.AddRange(seed);
        // 2026-11-27 (полный прогон 27.11: TransferAndAiTab ×6, WarehouseKpiTiles ×5 красные только в общем прогоне): сценарии с ролью «cashier»/«salesperson»
        // (ScalesSaveAndUnsaved, Barcodes… — LoginAsync(role)) оставляли в общем клиенте профиль кассира. Без роли owner/admin пропадали карточка «Перенос данных»
        // (право «Склад: редактирование») и плитка «Склад по закупке» (RevisionShowPurchase) — тесты владельца видели кассу кассира. Каждый тест начинает
        // со входа по умолчанию (роль owner, как в UiFake.Reset); тест, которому нужна другая роль, входит под ней сам — после WarmAsync.
        await RestoreDefaultLoginAsync(services);
        // 2026-11-27: корзина (ICartService) — одна на весь прогон; товары, оставленные прошлым сценарием, делали CanEnterTraining = false
        // (CoachTour: «Сначала закончите или уберите открытые чеки» → «Нет активного окна для диалога»). Каждый тест начинает с пустого чека.
        if (services.GetService<NurMarketKassa.Interfaces.ICartService>() is { LineCount: > 0 } cart)
            cart.ResetForNewReceipt();
        // 2026-11-21 (флаки полного прогона 1.17.91): тест, которому нужен свой каталог, кладёт товары в общий CatalogCacheService.Products уже после WarmAsync — а закрытое окно кассы
        // из прошлого теста ещё грузит каталог в фоне и переписывает этот список. Перед тестом дожидаемся, пока фоновые загрузки прежних окон закончатся.
        await QuiesceAsync();
    }

    /// <summary>2026-11-21: ждёт (до 20 с), пока закрытые окна кассы из прежних тестов доделают запуск и загрузку каталога; пока они идут, общий список товаров и корзина «чужие».
    /// Переключатель NURMARKET_UI_SKIP_QUIESCE=1 отключает ожидание — только для проверки, что HarnessIsolationUiTests действительно ловит это (без ожидания он красный).</summary>
    public static async Task QuiesceAsync()
    {
        if (Environment.GetEnvironmentVariable("NURMARKET_UI_SKIP_QUIESCE") == "1")
            return;
        var until = Environment.TickCount64 + 20000;
        while ((NurMarketKassa.AvaloniaHost.Views.MainKassir.MainWindow.StartupsInProgress > 0 || NurMarketKassa.ViewModels.Main.CatalogPanelViewModel.ActiveLoads > 0)
               && Environment.TickCount64 < until)
            await Task.Delay(25);
    }

    /// <summary>2026-11-27: профиль после чужого сценария не owner/admin (вход под «cashier», выход) — вернуть заглушки входа/профиля по умолчанию и войти заново.</summary>
    private static async Task RestoreDefaultLoginAsync(IServiceProvider services)
    {
        var auth = services.GetRequiredService<IAuthApiService>();
        if (services.GetService<IAutonomousAuthService>() is { IsCurrentSessionAutonomous: true } || PermissionService.IsOwnerOrAdmin(auth.UserPayload))
            return;
        UiFake.Server.StubLogin();
        UiFake.Server.StubProfile();
        await Task.Run(() => services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678"));
    }

    private static List<NurMarketKassa.Models.Pos.CatalogProductTileVm>? _seedProducts;

    private static void SetStatic(string property, object value) =>
        typeof(AvaloniaApp).GetProperty(property, BindingFlags.Public | BindingFlags.Static)!
            .GetSetMethod(nonPublic: true)!.Invoke(null, [value]);
}
