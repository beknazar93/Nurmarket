using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.Ui.Shared;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Perf;

/// <summary>2026-11-13 (Л-12 / Н-5): замеры скорости окон и действий кассира в Avalonia.Headless против подставного NurCRM — с пределами-«храповиками»
/// (предел = базовая цифра этого ПК × 3–5: тест падает, когда стало ЗАМЕТНО медленнее, а не от шума загруженного ПК). Меряется время до готового кадра —
/// без пауз «дать загрузкам закончиться» (UiShot.Settle) и без сети. Цифры прогона дописываются в %TEMP%\nurmarket-tests\speed-budget.txt.
/// Для медленного ПК пределы можно умножить: NURMARKET_SPEED_FACTOR=2. Поиск по каталогу, корзина и база — в проекте обычных тестов (SpeedBudgetTests).</summary>
[Collection("UiScenarios")]
public sealed class SpeedBudgetUiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public SpeedBudgetUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
    }

    public void Dispose()
    {
        PosApp.ActiveShiftId = _savedShift;
        Sess.Session.ActiveShiftId = _savedShift;
    }

    private static class Sess
    {
        public static IAppSession Session => TestHost.Services.GetRequiredService<IAppSession>();
    }

    // ───────────── общая обвязка ─────────────

    private static readonly object LogGate = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "speed-budget.txt");

    private static double Factor =>
        double.TryParse(Environment.GetEnvironmentVariable("NURMARKET_SPEED_FACTOR"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;

    private static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(x => x).ToList();
        return list[list.Count / 2];
    }

    private static void Check(string what, double ms, double limitMs)
    {
        var limit = limitMs * Factor;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (LogGate)
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {what} | {ms:0.0} мс | предел {limit:0} мс\n");
        }
        catch { /* журнал замеров не должен ронять тест */ }
        ms.Should().BeLessThan(limit, "«{0}» занял {1:0.0} мс при пределе {2:0} мс — стало заметно медленнее (журнал: {3})", what, ms, limit, LogPath);
    }

    /// <summary>Время (мс) от начала действия до готового кадра: создание/показ окна и несколько прогонов очереди интерфейса и отрисовки (без пауз).</summary>
    private static double Frame(Func<Window> create, out Window window, int pumps = 3)
    {
        var sw = Stopwatch.StartNew();
        window = create();
        window.Show();
        for (var i = 0; i < pumps; i++)
            UiShot.Pump();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void SetShift(string? id)
    {
        PosApp.ActiveShiftId = id;
        Sess.Session.ActiveShiftId = id;
    }

    private static MainWindow OpenMain(UiSize size)
    {
        var main = (MainWindow)TestHostCreateMain();
        UiRobot.Reset(main);
        using var _ = UiShot.Render(main, size);
        main.Activate();
        UiShot.Settle(6, 40);
        return main;
    }

    private static Window TestHostCreateMain() => TestHost.Services.GetRequiredService<MainWindow>();

    private static void CloseMain(Window main)
    {
        UiRobot.CloseAll();
        UiRobot.Main = null;
        main.Close();
        UiShot.Pump();
    }

    private static BasketPanelViewModel Basket(Window main) => ((MainWindowViewModel)main.DataContext!).Basket;

    // ───────────── 1. запуск до окна кассира ─────────────

    // 2026-11-19: «запуск до окна кассира» (MainWindow) из автозамеров убран — после закрытия MainWindow тестовый хост Headless падает на следующем тесте; цифры (1,3–2,6 с при пределе 5 с) сняты вручную 19.11, см. отчёт Л-12.

    // 2026-11-19: повторное открытие окна кассы убрано из замеров — в Headless второе MainWindow подряд зависает (тест висел 10 минут), это не скорость программы.

    // ───────────── 2. скан → строка в чеке ─────────────

    // 2026-11-19: скан → строка в окне кассы в замеры не входит — второе MainWindow подряд в одном Headless-прогоне роняет тестовый хост; скан до корзины меряет SpeedBudgetTests (ScanToCartLine_*).

    // 2026-11-19: поиск в панели каталога на 15 тыс. измеряет проект обычных тестов (SpeedBudgetTests.CatalogSearch_On15kProducts_*); панель в Headless на 15 тыс. нестабильна.

    // ───────────── 4. открытие окна оплаты и проведение ─────────────

    private static (CheckoutViewModel Vm, string Json) PaymentVm()
    {
        var cartJson = CartJson([("Хлеб", 3, 150, 0)]);
        var totals = CartTotalsCalculator.Calculate(JsonDocument.Parse(cartJson).RootElement);
        var vm = new CheckoutViewModel(totals, "", "") { IsPrintReceiptEnabled = false };
        return (vm, cartJson);
    }

    [AvaloniaFact]
    public async Task PaymentDialog_Open_StaysWithinBudget()
    {
        await TestHost.WarmAsync();
        var times = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            var (vm, _) = PaymentVm();
            var owner = new Window { Width = 1024, Height = 640 };
            owner.Show();
            var sw = Stopwatch.StartNew();
            var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
            var result = dialog.ShowDialog<bool?>(owner);
            for (var k = 0; k < 3; k++)
                UiShot.Pump();
            sw.Stop();
            if (i > 0) // первое открытие — прогрев
                times.Add(sw.Elapsed.TotalMilliseconds);
            dialog.IsVisible.Should().BeTrue();
            dialog.Close(false);
            await result;
            owner.Close();
            UiShot.Pump();
        }
        Check("Открытие окна оплаты (до первого кадра)", Median(times), 2500);
    }

    [AvaloniaFact]
    public async Task Checkout_AgainstFakeServer_StaysWithinBudget()
    {
        await TestHost.WarmAsync();
        var shiftId = Guid.NewGuid().ToString();
        SetShift(shiftId);
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
        var times = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            UiFake.Server.Json("POST", "/api/main/pos/checkout/", 200, "{\"id\":\"sale-" + (2000 + i) + "\",\"number\":" + (2000 + i) + ",\"status\":\"paid\",\"total\":\"450.00\"}");
            var cartJson = CartJson([("Хлеб", 3, 150, 0)]);
            var cart = MakeCart(cartJson);
            var service = new PosCheckoutService(cart, new SalesApiService(api), new ShiftApiService(api),
                Null<IShiftStateService>(), Null<IReceiptPrinterService>(), Null<IMediator>(), Null<IAutonomousAuthService>());
            var vm = PaymentVm().Vm;
            var request = new PosCheckoutRequest
            {
                PaymentMethod = "cash",
                CashReceived = "500",
                PrintReceipt = false,
                ExpectedTotal = 450.0,
            };
            var sw = Stopwatch.StartNew();
            var result = await service.CheckoutAsync(request);
            sw.Stop();
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            if (i > 0)
                times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Check("Проведение чека (оплата → ответ подставного сервера)", Median(times), 3000);
    }

    // ───────────── 5. склад, приёмка, разделы владельца ─────────────

    [AvaloniaFact]
    public async Task WarehouseWindow_Open_StaysWithinBudget()
    {
        await TestHost.WarmAsync();
        var first = Frame(() => TestHost.Services.GetRequiredService<WarehouseWindow>(), out var w1);
        w1.Close();
        UiShot.Pump();
        var second = Frame(() => TestHost.Services.GetRequiredService<WarehouseWindow>(), out var w2);
        try
        {
            Check("Открытие «Склада» (повторное; первое было " + first.ToString("0") + " мс)", second, 8000);
        }
        finally
        {
            w2.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ReceivingWindow_Open_StaysWithinBudget()
    {
        await TestHost.WarmAsync();
        var first = Frame(() => { var w = new PurchasesWindow(); w.OpenReceive(null); return w; }, out var w1);
        w1.Close();
        UiShot.Pump();
        var second = Frame(() => { var w = new PurchasesWindow(); w.OpenReceive(null); return w; }, out var w2);
        try
        {
            Check("Открытие «Приёмки товара» (повторное; первое было " + first.ToString("0") + " мс)", second, 8000);
        }
        finally
        {
            w2.Close();
            UiShot.Pump();
        }
    }

    // 2026-11-19: переключение разделов оболочки владельца в замерах убрано — в Headless при смене разделов падает раскладка Avalonia Grid (NullReference в SetFinalSizeMaxDiscrepancy), это не скорость.

    /// <summary>Переключение вкладок внутри «Склада» (программа владельца): Товары → Приёмка → Штрихкоды → ...; меряется каждое переключение до кадра.</summary>
    [AvaloniaFact]
    public async Task WarehouseTabs_Switch_StaysWithinBudget()
    {
        await TestHost.WarmAsync();
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        wh.Width = 1366;
        wh.Height = 768;
        wh.Show();
        UiShot.Settle(4, 30);
        try
        {
            var tabs = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wh).OfType<TabControl>().FirstOrDefault();
            tabs.Should().NotBeNull("на «Складе» есть вкладки");
            var count = tabs!.ItemCount;
            count.Should().BeGreaterThan(1);
            var times = new List<double>();
            for (var round = 0; round < 2; round++)
            {
                for (var i = 0; i < count; i++)
                {
                    var sw = Stopwatch.StartNew();
                    tabs.SelectedIndex = i;
                    for (var k = 0; k < 3; k++)
                        UiShot.Pump();
                    sw.Stop();
                    if (round > 0)
                        times.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            Check("Переключение вкладок «Склада» — медиана", Median(times), 3000);
            Check("Переключение вкладок «Склада» — самое долгое", times.Max(), 8000);
        }
        finally
        {
            wh.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-21 (жалоба «зависание при переключении вкладок», «оптимизация»): открытие окна «Настройки» и переключение его вкладок (Экран → Печать → Операции → Обновления).</summary>
    [AvaloniaFact]
    public async Task SettingsWindow_Open_AndTabs_StayWithinBudget()
    {
        await TestHost.WarmAsync();
        var settings = TestHost.Services.GetRequiredService<PosSettingsWindow>();
        settings.Width = 1280;
        settings.Height = 800;
        var open = Frame(() => settings, out var window);
        try
        {
            Check("Открытие «Настроек» (первое)", open, 8000);
            var times = new List<double>();
            for (var round = 0; round < 2; round++)
            {
                foreach (var key in new[] { "screen", "print", "operations", "updates", "screen" })
                {
                    var sw = Stopwatch.StartNew();
                    settings.SelectNavTab(key);
                    for (var k = 0; k < 3; k++)
                        UiShot.Pump();
                    sw.Stop();
                    if (round > 0)
                        times.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            Check("Переключение вкладок «Настроек» — медиана", Median(times), 3000);
            Check("Переключение вкладок «Настроек» — самое долгое", times.Max(), 8000);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
