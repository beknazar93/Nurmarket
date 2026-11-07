using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using NurMarketKassa.Ui.Shared;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-06 (В14, Т-1): сквозные сценарии кассира ЧЕРЕЗ ОКНА в Avalonia.Headless против поддельного NurCRM (WireMock). Кассир «нажимает» в настоящих
/// окнах кассы (оплата, возврат, смена — мышь и клавиатура Headless), результат окна уходит в настоящие службы (PosCheckoutService, SalesApiService,
/// ShiftApiService), а проверяется то, что дошло до сервера: путь, тело, ключ повтора. Печать, шина и состояние смены — заглушки; данные — во временной папке.
/// Тесты идут по очереди (общий хост служб и общая статика кассы).</summary>
[Collection("UiScenarios")]
public sealed class CashierWindowScenarios : IDisposable
{
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private readonly string _shiftId = Guid.NewGuid().ToString();
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public CashierWindowScenarios()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = _shiftId;
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
    }

    public void Dispose() => PosApp.ActiveShiftId = _savedShift;

    // ───────────── общая обвязка ─────────────

    private static async Task<NurMarketApiClient> LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
        return api;
    }

    private async Task<(PosCheckoutService Service, ICartService Cart)> CheckoutServiceAsync(string cartJson)
    {
        var api = await LoginAsync();
        var cart = MakeCart(cartJson);
        var service = new PosCheckoutService(cart, new SalesApiService(api), new ShiftApiService(api),
            Null<IShiftStateService>(), Null<IReceiptPrinterService>(), Null<IMediator>(), Null<IAutonomousAuthService>());
        return (service, cart);
    }

    /// <summary>Окно оплаты как в кассе: итоги чека → CheckoutViewModel → CheckoutDialog поверх главного окна. <paramref name="drive"/> — действия кассира в окне.
    /// Возвращает то, что касса дальше отправляет в службу оплаты (те же поля, что собирает BasketPanelViewModel).</summary>
    private static async Task<(bool Confirmed, PosCheckoutRequest Request, CheckoutViewModel Vm)> PayInDialogAsync(
        string cartJson, Action<Window, CheckoutViewModel> drive, Action<CheckoutViewModel>? prepare = null)
    {
        var totals = CartTotalsCalculator.Calculate(JsonDocument.Parse(cartJson).RootElement);
        var vm = new CheckoutViewModel(totals, "", "");
        vm.IsPrintReceiptEnabled = false; // чек не печатаем: принтера нет, окно «Принтер не подключён» не нужно
        prepare?.Invoke(vm);
        var owner = new Window { Width = 1024, Height = 640 };
        owner.Show();
        var dialog = new CheckoutDialog(vm, TestHost.Services.GetRequiredService<IDialogService>());
        var result = dialog.ShowDialog<bool?>(owner);
        UiShot.Settle(3);
        drive(dialog, vm);
        var confirmed = await result == true;
        owner.Close();
        UiShot.Pump();
        var request = new PosCheckoutRequest
        {
            PaymentMethod = vm.PaymentMethod,
            CashReceived = vm.CashReceivedForApi,
            PrintReceipt = vm.IsPrintReceiptEnabled,
            OrderDiscountBody = vm.PendingOrderDiscountBody,
            ClientId = vm.ClientId,
            NonCashReceived = vm.NonCashReceivedForApi,
            ExpectedTotal = vm.EffectiveTotalDue,
            DebtSchedule = vm.DebtScheduleForApi,
        };
        return (confirmed, request, vm);
    }

    private static void ClickPay(Window dialog, CheckoutViewModel vm) =>
        UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, vm.PayButtonText));

    // ───────────── 1. продажа за наличные ─────────────

    [AvaloniaFact]
    public async Task Sale_Cash_ThroughCheckoutDialog_ReachesServerWithShiftAndKey()
    {
        var cartJson = CartJson([("Хлеб", 3, 150, 0)]); // 450 сом
        UiFake.Server.Json("POST", "/api/main/pos/checkout/", 200, "{\"id\":\"sale-1001\",\"number\":1001,\"status\":\"paid\",\"total\":\"450.00\"}");
        var (service, cartService) = await CheckoutServiceAsync(cartJson);

        var (confirmed, request, vm) = await PayInDialogAsync(cartJson, (dialog, v) =>
        {
            v.IsCash.Should().BeTrue("по умолчанию окно открывается на «Наличные»");
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "500");
            v.CanPay.Should().BeTrue();
            ClickPay(dialog, v);
        });

        confirmed.Should().BeTrue("кассир нажал «Оплатить»");
        vm.PaymentMethod.Should().Be("cash");
        var result = await service.CheckoutAsync(request);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var post = Requests(UiFake.Server, "POST", "/api/main/pos/checkout/").Should().ContainSingle().Subject;
        Header(post, "Idempotency-Key").Should().NotBeNullOrWhiteSpace();
        var body = BodyOf(post);
        body["shift"]!.GetValue<string>().Should().Be(_shiftId);
        body["payment"]!["method"]!.GetValue<string>().Should().Be("cash");
        body["payment"]!["received"]!.GetValue<string>().Should().Be("500.00", "кассир ввёл «Получено 500»");
        ((CartProxy)(object)cartService).ResetCalls.Should().Be(1);
    }

    // ───────────── 2. смешанная оплата ─────────────

    [AvaloniaFact]
    public async Task MixedPayment_ThroughCheckoutDialog_SplitsCashAndCard()
    {
        var cartJson = CartJson([("Хлеб", 3, 150, 0)]); // 450
        UiFake.Server.Json("POST", "/api/main/pos/checkout/", 200, "{\"id\":\"sale-1002\",\"number\":1002,\"status\":\"paid\",\"total\":\"450.00\"}");
        var (service, _) = await CheckoutServiceAsync(cartJson);

        var (confirmed, request, vm) = await PayInDialogAsync(cartJson, (dialog, v) =>
        {
            UiDrive.Click(dialog, UiDrive.Find<RadioButton>(dialog, "PayMixedOption"));
            v.IsMixed.Should().BeTrue();
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "MixedCashBox"), "200");
            UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "MixedNonCashBox"), "250");
            v.CanPay.Should().BeTrue("200 + 250 = 450");
            ClickPay(dialog, v);
        });

        confirmed.Should().BeTrue();
        var result = await service.CheckoutAsync(request);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var body = BodyOf(Requests(UiFake.Server, "POST", "/api/main/pos/checkout/").Should().ContainSingle().Subject);
        body["payment"]!["method"]!.GetValue<string>().Should().Be("mixed");
        body["payment"]!["cash_amount"]!.GetValue<string>().Should().Be("200.00");
        body["payment"]!["card_amount"]!.GetValue<string>().Should().Be("250.00");
        vm.PaymentMethod.Should().Be("mixed");
    }

    // ───────────── 3. продажа в долг и погашение ─────────────

    [AvaloniaFact]
    public async Task DebtSale_ThroughCheckoutDialog_ThenRepayment_ReachServer()
    {
        var cartJson = CartJson([("Хлеб", 3, 150, 0)]); // 450
        UiFake.Server.Json("POST", "/api/main/pos/checkout/", 200, "{\"id\":\"sale-1003\",\"number\":1003,\"status\":\"credit\",\"total\":\"450.00\"}");
        UiFake.Server.Json("POST", "/api/main/clients/*/deals/*/pay/", 200, "{\"ok\":true}");
        var (service, _) = await CheckoutServiceAsync(cartJson);

        var (confirmed, request, vm) = await PayInDialogAsync(cartJson, (dialog, v) =>
        {
            UiDrive.Click(dialog, UiDrive.Find<RadioButton>(dialog, "PayDebtOption"));
            v.IsDebtMode.Should().BeTrue();
            v.DebtDueDate.Should().NotBeNull("срок по умолчанию — 14 дней");
            v.CanPay.Should().BeTrue("клиент выбран, срок стоит, предоплаты нет — весь чек в долг");
            ClickPay(dialog, v);
        }, prepare: v => v.PreselectClient(new ClientOption { Id = ClientId, DisplayName = "Асан · 0555 111 222" }));

        confirmed.Should().BeTrue();
        vm.ClientId.Should().Be(ClientId);
        var sale = await service.CheckoutAsync(request);
        sale.IsSuccess.Should().BeTrue(sale.ErrorMessage);
        var body = BodyOf(Requests(UiFake.Server, "POST", "/api/main/pos/checkout/").Should().ContainSingle().Subject);
        body["client"]!.GetValue<string>().Should().Be(ClientId);
        body["payment"]!["method"]!.GetValue<string>().Should().Be("debt");

        // Окно «Оплата долга» поднимается на живых службах кассы (список должников — с сервера); само погашение уходит тем же клиентом API.
        var debtWindow = new PayDebtDialog();
        try
        {
            UiShot.Render(debtWindow);
            debtWindow.IsVisible.Should().BeTrue();
            var sales = new SalesApiService(await LoginAsync());
            await sales.PosPayDebtAsync(ClientId, "deal-1003", "inst-1", 450);
        }
        finally
        {
            debtWindow.Close();
            UiShot.Pump();
        }
        var pay = Requests(UiFake.Server, "POST", "/pay/").Should().ContainSingle().Subject;
        pay.RequestMessage.Path.Should().Be($"/api/main/clients/{ClientId}/deals/deal-1003/pay/");
        BodyOf(pay)["amount"]!.GetValue<string>().Should().Be("450.00");
    }

    // ───────────── 4. возврат ─────────────

    [AvaloniaFact]
    public async Task Return_ReturnSaleDialog_ListsServerSale_AndReturnReachesServer()
    {
        var saleRow = "{\"id\":\"sale-1001\",\"number\":1001,\"status\":\"paid\",\"total\":\"450.00\",\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\",\"payment_method\":\"cash\"}";
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[" + saleRow + "]}");
        UiFake.Server.Json("POST", "/api/main/pos/sales/sale-1001/return/", 200, "{\"id\":\"ret-1\",\"sale\":\"sale-1001\",\"amount\":\"450.00\"}");
        await LoginAsync();

        var dialog = new ReturnSaleDialog();
        try
        {
            UiShot.Render(dialog);
            UiShot.Settle(10, 50);
            Requests(UiFake.Server, "GET", "/api/main/pos/sales/").Should().NotBeEmpty("окно возврата запросило список чеков у сервера");
            UiDrive.AllText(dialog).Should().Contain("450", "чек с сервера показан в списке окна возврата");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }

        var sales = new SalesApiService(await LoginAsync());
        var pending = new PendingIdempotencyKey();
        await sales.PosReturnSaleAsync("sale-1001", [new PosRefundLineRequest { LineId = "line-1", Title = "Хлеб", Quantity = 3, OriginalQuantity = 3 }], default, pending.Get("sale-1001|line-1:3"));
        pending.OnSuccess();
        var body = BodyOf(Requests(UiFake.Server, "POST", "/return/").Should().ContainSingle().Subject);
        body["cashbox_role"]!.GetValue<string>().Should().Be("pos_main");
        body["items"]!.AsArray().Should().ContainSingle();
    }

    // ───────────── 5. смена: открытие, X-отчёт, закрытие, Z-отчёт ─────────────

    [AvaloniaFact]
    public async Task Shift_OpenDialog_XReport_CloseDialog_ZReport()
    {
        var shiftRow = "{\"id\":\"" + _shiftId + "\",\"status\":\"open\",\"opening_cash\":\"1000.00\",\"opened_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}";
        UiFake.Server.Json("POST", "/api/construction/shifts/open/", 200, shiftRow);
        UiFake.Server.Json("POST", "/api/construction/shifts/" + _shiftId + "/close/", 200, "{\"id\":\"" + _shiftId + "\",\"status\":\"closed\",\"closing_cash\":\"5750.00\"}");
        var api = await LoginAsync();
        var shifts = new ShiftApiService(api);

        // Открытие смены: кассир вводит остаток в окно и жмёт «Открыть смену».
        var open = new OpenShiftDialog();
        var owner = new Window { Width = 1024, Height = 640 };
        owner.Show();
        var openResult = open.ShowDialog<bool?>(owner);
        UiShot.Settle(3);
        UiDrive.Type(open, UiDrive.Find<TextBox>(open, "OpeningCashBox"), "1000");
        UiDrive.Click(open, UiDrive.ButtonByText(open, "Открыть смену"));
        (await openResult).Should().BeTrue();
        open.OpeningCash.Should().Be(1000m);
        await shifts.ConstructionShiftOpenAsync("cashbox-1", open.OpeningCash.ToString("0.00", CultureInfo.InvariantCulture));
        BodyOf(Requests(UiFake.Server, "POST", "/shifts/open/").Should().ContainSingle().Subject)["opening_cash"]!.GetValue<string>().Should().Be("1000.00");

        // X-отчёт: окно смены показывает цифры, смена остаётся активной.
        var shift = new ShiftModel
        {
            Id = _shiftId, ShiftNumber = "7", OpenedAt = DateTime.Now.AddHours(-6), Cashier = "Тест Кассир", Status = "Активна",
            Revenue = 5000m, OpeningCash = 1000m, CashSales = 4000m, NonCashSales = 1000m, SalesCount = 12, ExpectedCash = 5000m,
        };
        var x = new ShiftDetailsDialog(shift) { Title = "X-отчёт", LocalReport = true };
        try
        {
            UiShot.Render(x);
            UiShot.Settle(5, 40);
            UiDrive.AllText(x).Should().Contain("X-отчёт");
        }
        finally
        {
            x.Close();
            UiShot.Pump();
        }

        // Закрытие смены: кассир вводит фактический остаток и жмёт «Закрыть смену».
        var close = new CloseShiftDialog { SuggestedBalance = 5000m };
        var closeResult = close.ShowDialog<bool?>(owner);
        UiShot.Settle(3);
        UiDrive.Type(close, UiDrive.Find<TextBox>(close, "ClosingCashBox"), "5750");
        UiDrive.Click(close, UiDrive.Find<Button>(close, "PrimaryButton"));
        var closed = await closeResult == true;
        owner.Close();
        closed.Should().BeTrue("кассир подтвердил закрытие");
        close.ClosingCash.Should().Be(5750m);
        var outcome = await ShiftCloseRetry.CloseAsync(shifts, _shiftId, "5750.00", default);
        outcome.Outcome.Should().Be(ShiftCloseOutcome.Closed);
        BodyOf(Requests(UiFake.Server, "POST", "/close/").Should().ContainSingle().Subject)["closing_cash"]!.GetValue<string>().Should().Be("5750.00");

        // Z-отчёт: тот же отчёт после закрытия — с фактическим остатком и расхождением (+750 к 5000 по системе).
        var closedShift = new ShiftModel
        {
            Id = _shiftId, ShiftNumber = "7", OpenedAt = DateTime.Now.AddHours(-6), ClosedAt = DateTime.Now, Cashier = "Тест Кассир", Status = "Закрыта",
            Revenue = 5000m, OpeningCash = 1000m, CashSales = 4000m, NonCashSales = 1000m, SalesCount = 12, ExpectedCash = 5000m, ClosingCash = 5750m, CashDiff = 750m,
        };
        var z = new ShiftDetailsDialog(closedShift) { Title = "Z-отчёт", LocalReport = true, RefreshFromServer = false };
        try
        {
            UiShot.Render(z);
            UiShot.Settle(5, 40);
            UiDrive.AllText(z).Should().Contain("Z-отчёт");
        }
        finally
        {
            z.Close();
            UiShot.Pump();
        }
    }
}
