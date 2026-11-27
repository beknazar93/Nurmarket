using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (флаки полного прогона 1.17.91): проверка самого стенда. Окно кассы, открытое прежним тестом, после закрытия ещё довольно долго догружает каталог в фоне
/// (запуск окна → тёплый старт → сверка с сервером → TryLoadFromDatabase) и переписывает общий статический каталог CatalogCacheService.Products. Следующий тест,
/// положивший туда свои товары, терял их (каталог теста «пустел» или заменялся двумя товарами заглушки; строка «Синхронизация каталога…» в кадре main-kassa).
/// На быстрой машине хвост успевает закончиться до следующего теста, в полном прогоне (нагрузка, медленный отклик) — нет; здесь медленный сервер задан явно, поэтому пара
/// «виновник → жертва» воспроизводится всегда: без ожидания в TestHost.WarmAsync (переключатель NURMARKET_UI_SKIP_QUIESCE=1) тест красный, с ожиданием — зелёный.</summary>
[Collection("UiScenarios")]
public sealed class HarnessIsolationUiTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();

    public void Dispose()
    {
        UiFake.Reset(); // убирает медленную заглушку списка товаров
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    [AvaloniaFact]
    public async Task ClosedMainWindow_BackgroundCatalogLoad_DoesNotReplaceCatalogOfNextTest()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Reset();
        // Сервер отвечает на список товаров за 2 с: загрузка каталога из запуска окна ещё идёт, когда окно уже закрыто.
        UiFake.Server.Slow("GET", "/api/main/products/list/", TimeSpan.FromSeconds(2),
            "{\"count\":2,\"next\":null,\"previous\":null,\"results\":[{\"id\":\"p1\",\"name\":\"Хлеб\",\"barcode\":\"4600000000001\",\"price\":\"45.00\",\"quantity\":\"10\",\"is_weight\":false},"
            + "{\"id\":\"p2\",\"name\":\"Яблоки\",\"barcode\":\"4600000000002\",\"price\":\"120.00\",\"quantity\":\"5.5\",\"is_weight\":true}]}");

        // «Виновник»: окно кассы открыто и закрыто, как в быстрых тестах (Accessibility, Tour, ...), пока его запуск не закончился.
        var window = (MainWindow)ScreenCatalog.Get("main-kassa").Create();
        window.Show();
        UiShot.Settle(2, 20);
        window.CaptureRenderedFrame()?.Dispose();
        // Загрузку каталога окно запускает само при показе (OnLoaded → запуск → тёплый старт); чтобы тест не зависел от того, когда придёт Loaded, запускаем её тем же путём явно.
        // «Свежая» загрузка прошлого теста (меньше 15 с) пропускает новую — сбрасываем, как при смене аккаунта, чтобы загрузка действительно пошла к медленному серверу.
        (TestHost.Services.GetRequiredService<NurMarketKassa.Core.Contracts.ICatalogCacheService>() as NurMarketKassa.AvaloniaHost.Services.AvaloniaCatalogCacheService)?.ResetForAccountChange();
        var catalog = ((MainWindowViewModel)window.DataContext!).Catalog;
        _ = (Task?)typeof(CatalogPanelViewModel).GetMethod("RefreshCatalogAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(catalog, null);
        UiRobot.WaitFor(() => MainWindow.StartupsInProgress > 0 || CatalogPanelViewModel.ActiveLoads > 0, 5000).Should().BeTrue("загрузка каталога из окна кассы началась (иначе тест ничего не проверяет); обрыв связи=" + ServerOutageMonitor.IsOutage + ", офлайн=" + PosApp.IsOfflineBootstrap);
        window.Close();
        UiShot.Pump();

        // «Жертва»: начало следующего теста и его собственные товары.
        await TestHost.WarmAsync();
        var seeded = Enumerable.Range(0, 12).Select(i => new CatalogProductTileVm("iso" + i, "Изоляция " + i, "10.00 сом", false) { Unit = "шт", Quantity = 5 }).ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(seeded);

        // Фоновые хвосты закрытого окна, если они ещё есть, закончатся за это время.
        UiRobot.WaitFor(() => CatalogCacheService.Products.Count != seeded.Count, 5000).Should().BeFalse(
            "каталог теста не должен переписываться фоновой загрузкой окна из прошлого теста; товаров сейчас " + CatalogCacheService.Products.Count);
        CatalogCacheService.Products.Should().HaveCount(12);
    }

    /// <summary>2026-11-27 (полный прогон 27.11: TransferAndAiTab ×6, WarehouseKpiTiles ×5, CoachTour ×2 красные только в общем прогоне): «виновник» входит кассиром
    /// и оставляет товар в общей корзине; «жертва» — следующий тест — после TestHost.WarmAsync обязана видеть вход владельца (карточка «Перенос данных», плитка
    /// «Склад по закупке») и пустой чек (учебный режим обучения). Пара воспроизводится здесь явно.</summary>
    [AvaloniaFact]
    public async Task CashierLoginAndLeftoverCart_FromPreviousTest_AreResetByWarm()
    {
        await TestHost.WarmAsync();
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        var auth = TestHost.Services.GetRequiredService<NurMarketKassa.Services.Api.IAuthApiService>();
        var cart = TestHost.Services.GetRequiredService<NurMarketKassa.Interfaces.ICartService>();
        // «виновник»: вход кассиром (как ScalesSaveAndUnsavedUiTests.LoginAsync("cashier")) и товар в чеке
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"cashier\"}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"cashier\",\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await api.LoginAsync("cashier@test.kg", "12345678");
        PermissionService.IsOwnerOrAdmin(auth.UserPayload).Should().BeFalse("виновник вошёл кассиром");
        cart.ResetForNewReceipt();
        cart.AddItem(CatalogCacheService.Products.First(), 1);
        cart.LineCount.Should().BeGreaterThan(0, "виновник оставил товар в чеке");

        // «жертва»: начало следующего теста
        await TestHost.WarmAsync();
        PermissionService.IsOwnerOrAdmin(auth.UserPayload).Should().BeTrue("следующий тест начинается со входа по умолчанию (роль owner)");
        cart.LineCount.Should().Be(0, "следующий тест начинается с пустого чека");
    }
}
