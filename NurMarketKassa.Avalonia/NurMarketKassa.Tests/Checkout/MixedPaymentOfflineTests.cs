using System.Globalization;
using FluentAssertions;
using MediatR;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Checkout;

/// <summary>2026-11-27, жалоба владельца (фото кассы 11:22): «Оплата не прошла. Смешанная оплата недоступна, пока сервер NurCRM не отвечает» при живом сервере.
/// Причина: касса объявила «аварию» (журнал 27.11 06:19 — пачка фоновых сбоев за 0,2 с: таймаут профиля + 500 на company/addons и company/modules),
/// а смешанная в аварии была запрещена. Теперь: смешанная без связи встаёт в очередь с разбивкой нал/безнал и досылается только быстрым путём с ключом;
/// быстрый 500 на фоновое чтение аварию не объявляет.</summary>
[Collection("ServerFeatures")]
public sealed class MixedPaymentOfflineTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();
    private readonly string _shiftId = Guid.NewGuid().ToString();
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private readonly IAuthApiService _savedAuth = PosApp.AuthApi;

    public MixedPaymentOfflineTests()
    {
        PosApp.ActiveShiftId = _shiftId;
        PosApp.AuthApi ??= Null<IAuthApiService>(); // запись очереди читает филиал (ActiveBranchId)
        ServerOutageMonitor.ResetForTests();
    }

    private readonly List<string> _names = new();

    public void Dispose()
    {
        // записи очереди этого класса убираем — иначе соседние тесты досылки увидят чужие чеки
        try
        {
            OfflineDatabase.RemoveIds(OfflinePendingSalesStore.LoadAll()
                .Where(e => _names.Any(n => e.CartJson.Contains(n, StringComparison.Ordinal))).Select(e => e.Id).ToList());
        }
        catch (Exception) { }
        ServerOutageMonitor.ResetForTests();
        PosApp.ActiveShiftId = _savedShift;
        PosApp.AuthApi = _savedAuth;
        _fake.Dispose();
    }

    private async Task<PosCheckoutService> ServiceAsync(string cartJson)
    {
        var api = await LoggedInAsync(_fake);
        return new PosCheckoutService(MakeCart(cartJson), new SalesApiService(api), new ShiftApiService(api),
            Null<IShiftStateService>(), Null<IReceiptPrinterService>(), Null<IMediator>(), Null<IAutonomousAuthService>());
    }

    private static PosCheckoutRequest Mixed(string cash, string card, double total) =>
        new() { PaymentMethod = "mixed", CashReceived = cash, NonCashReceived = card, PrintReceipt = false, ExpectedTotal = total };

    private static OfflineSaleEntry QueuedWith(string productName) =>
        OfflinePendingSalesStore.LoadAll().Single(e => e.CartJson.Contains(productName, StringComparison.Ordinal));

    [Fact]
    public async Task Mixed_InServerOutage_IsQueuedWithCardPart_AndReplaysOnlyByQuickPathWithSplit()
    {
        var name = "cheese-" + Guid.NewGuid().ToString("N")[..8];
        _names.Add(name);
        var service = await ServiceAsync(CartJson([(name, 3, 10, 0)]));
        ServerOutageMonitor.ReportFailure("тест", "нет связи", hard: true);

        var result = await service.CheckoutAsync(Mixed("10", "20", 30));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.SavedOffline.Should().BeTrue("смешанная без связи — в очередь, как наличные (раньше: «недоступна, пока сервер не отвечает»)");
        Requests(_fake, "POST", "/api/main/pos/checkout/").Should().BeEmpty("в аварии касса на сервер не ходит");

        var entry = QueuedWith(name);
        entry.PaymentMethod.Should().Be("mixed");
        entry.CashReceived.Should().Be("10");
        entry.NonCashReceived.Should().Be("20", "безналичная часть хранится в записи очереди");
        entry.QuickCheckoutAttempted.Should().BeTrue("досылка только быстрым путём с ключом — старый путь безнал не передаёт");

        var body = SyncService.BuildQuickReplayBody(entry)!;
        body.Should().NotBeNull();
        body["payment"]!["method"]!.GetValue<string>().Should().Be("mixed");
        body["payment"]!["cash_amount"]!.GetValue<string>().Should().Be("10.00");
        body["payment"]!["card_amount"]!.GetValue<string>().Should().Be("20.00");
        body["offline"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Mixed_ServerFails5xx_GoesToQueueWithTheSameIdempotencyKey_NoSecondSale()
    {
        _fake.Json("POST", "/api/main/pos/checkout/", 503, "{\"detail\":\"unavailable\"}");
        var name = "tea-" + Guid.NewGuid().ToString("N")[..8];
        _names.Add(name);
        var service = await ServiceAsync(CartJson([(name, 1, 40.5, 0)]));

        var result = await service.CheckoutAsync(Mixed("20", "20.50", 40.5));

        result.SavedOffline.Should().BeTrue(result.ErrorMessage);
        var posts = Requests(_fake, "POST", "/api/main/pos/checkout/");
        posts.Should().NotBeEmpty();
        var keys = posts.Select(p => Header(p, "Idempotency-Key")).Distinct().ToList();
        keys.Should().ContainSingle("все попытки — одним ключом");
        var entry = QueuedWith(name);
        entry.Id.Should().Be(keys[0], "запись очереди досылается тем же ключом: если продажа прошла, сервер вернёт её (replayed), а не создаст вторую");
        entry.NonCashReceived.Should().Be("20.50");
        var payment = SyncService.BuildQuickReplayBody(entry)!["payment"]!;
        (decimal.Parse(payment["cash_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)
         + decimal.Parse(payment["card_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture)).Should().Be(40.50m, "разбивка сходится с итогом до копейки");
    }

    [Fact]
    public void MixedOfflineProblem_PartsMustMatchTotal_AndCardPartIsRequired()
    {
        var cart = CartJson([("Хлеб", 3, 10, 0)]);
        PosCheckoutService.MixedOfflineProblem(Mixed("10", "20", 30), cart, 30).Should().BeNull();
        PosCheckoutService.MixedOfflineProblem(Mixed("10", "19.98", 30), cart, 30).Should().NotBeNull("разбивка не сходится с итогом — в очередь не ставим");
        PosCheckoutService.MixedOfflineProblem(Mixed("30", "0", 30), cart, 30).Should().NotBeNull("без безналичной части это не смешанная");
        PosCheckoutService.MixedOfflineProblem(Mixed("10.005", "19.995", 30), cart, 30).Should().BeNull("дробные части в пределах копейки");
    }

    [Fact]
    public void QueuedMixed_CountsCashPartInDrawer_AndRestAsNonCash()
    {
        var cart = CartJson([("Хлеб", 3, 10, 0)]);
        var entry = new OfflineSaleEntry { PaymentMethod = "mixed", CashReceived = "10", NonCashReceived = "20", CartJson = cart, ShiftId = "s-mixed" };

        var totals = LocalShiftTotals.Aggregate("s-mixed", [entry], [])!;
        totals.TotalSales.Should().Be(30m);
        totals.CashSales.Should().Be(10m);
        totals.NonCashSales.Should().Be(20m);

        var snap = OfflineRefundPlanner.FromQueueEntry(entry)!;
        snap.PaidCash.Should().Be(10m);
        snap.PaidNonCash.Should().Be(20m);
    }

    [Fact]
    public async Task FastHttp500OnBackgroundRead_DoesNotDeclareOutage_GatewayErrorsStillDo()
    {
        _fake.Json("GET", "/api/users/company/modules/", 500, "{\"detail\":\"boom\"}");
        _fake.Json("GET", "/api/users/company/addons/", 502, "{\"detail\":\"bad gateway\"}");
        var api = await LoggedInAsync(_fake);

        for (var i = 0; i < 4; i++)
            try { await api.GetAsync("api/users/company/modules/"); } catch (Exception) { }

        ServerOutageMonitor.IsOutage.Should().BeFalse("сервер жив и ответил 500 за миг — это ошибка одного адреса, а не «сервер лёг»");

        for (var i = 0; i < 4; i++)
            try { await api.GetAsync("api/users/company/addons/"); } catch (Exception) { }

        ServerOutageMonitor.State.Should().NotBe(ServerLinkState.Online, "шлюзовые 502 считаются сбоем, как раньше");
    }
}
