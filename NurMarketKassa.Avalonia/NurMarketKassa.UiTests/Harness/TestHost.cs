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
    public static Task WarmAsync()
    {
        var services = Ensure();
        return _warm ??= Task.Run(async () =>
        {
            await services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678").ConfigureAwait(false);
            await CatalogCacheService.SyncCatalogFullAsync().ConfigureAwait(false);
        });
    }

    private static void SetStatic(string property, object value) =>
        typeof(AvaloniaApp).GetProperty(property, BindingFlags.Public | BindingFlags.Static)!
            .GetSetMethod(nonPublic: true)!.Invoke(null, [value]);
}
