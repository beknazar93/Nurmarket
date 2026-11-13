using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-11 (сводка Умара, в бою): GET clients/&lt;id&gt;/deals/ добавляет в конец ВИРТУАЛЬНЫЕ строки старых долговых чеков (id «sale:&lt;uuid&gt;»,
/// is_check_debt: true, без рассрочек и платежей). Касса не шлёт «sale:…» в адреса сделок (detail / pay / PATCH), не считает их сделками поставщика,
/// гасит такой долг по чеку (pos/sales/&lt;uuid&gt;/pay-debt/). Всё на заглушке WireMock.</summary>
public sealed class ClientCheckDebtDealsTests : IDisposable
{
    private const string ClientId = "c-1";
    private const string SaleUuid = "8f0c2a1e-1111-4222-8333-944455556666";
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string RegularDeal = "{\"id\":\"77\",\"kind\":\"debt\",\"title\":\"Долг\",\"amount\":\"300.00\",\"remaining_debt\":\"300.00\",\"installments\":[{\"id\":\"i1\",\"amount\":\"300.00\"}],\"payments\":[]}";
    private const string VirtualRow = "{\"id\":\"sale:" + SaleUuid + "\",\"is_check_debt\":true,\"title\":\"Долг по чеку №1042\",\"amount\":\"450.00\",\"remaining_debt\":\"120.00\",\"created_at\":\"2025-03-01T10:00:00Z\"}";

    [Fact]
    public void Virtual_row_is_recognised_and_parsed_regular_deal_is_not()
    {
        var v = J(VirtualRow);
        ClientCheckDebtDeals.IsVirtual(v).Should().BeTrue();
        ClientCheckDebtDeals.SaleId(v).Should().Be(SaleUuid);
        ClientCheckDebtDeals.Number(v).Should().Be("1042", "номер из заголовка «Долг по чеку №1042»");
        ClientCheckDebtDeals.Remaining(v).Should().Be(120);
        ClientCheckDebtDeals.Original(v).Should().Be(450);
        ClientCheckDebtDeals.Title("1042").Should().Be("Долг по чеку №1042");

        var r = J(RegularDeal);
        ClientCheckDebtDeals.IsVirtual(r).Should().BeFalse();
        ClientCheckDebtDeals.RealDeals(new[] { r, v }).Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be("77");
        ClientCheckDebtDeals.IsVirtual(J("{\"id\":\"sale:abc\"}")).Should().BeTrue("и без флага — по префиксу id");
    }

    [Fact]
    public async Task Sale_prefixed_id_never_goes_to_deal_urls()
    {
        _fake.Json("POST", "/api/main/clients/*/deals/*/pay/", 200, "{\"ok\":true}");
        _fake.Json("GET", "/api/main/clients/*/deals/*", 200, "{}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var suppliers = new SupplierApiService(api);

        await FluentActions.Awaiting(() => sales.PosPayDebtAsync(ClientId, "sale:" + SaleUuid, null, 120)).Should().ThrowAsync<ApiException>();
        await FluentActions.Awaiting(() => sales.ClientDealGetAsync(ClientId, "sale:" + SaleUuid)).Should().ThrowAsync<ApiException>();
        await FluentActions.Awaiting(() => suppliers.PayInstallmentAsync(ClientId, "sale:" + SaleUuid, "i1", "k-1", "")).Should().ThrowAsync<ApiException>();

        _fake.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/deals/sale", StringComparison.OrdinalIgnoreCase)
            || e.RequestMessage.Path.Contains("sale%3A", StringComparison.OrdinalIgnoreCase), "«sale:…» не уходит в адреса сделок");

        // настоящая сделка — как раньше
        await sales.PosPayDebtAsync(ClientId, "77", "i1", null);
        Requests(_fake, "POST", "/deals/77/pay/").Should().ContainSingle();
    }

    [Fact]
    public async Task Supplier_deals_list_skips_virtual_check_rows()
    {
        _fake.Json("GET", "/api/main/clients/" + ClientId + "/deals/", 200, "{\"count\":2,\"results\":[" + RegularDeal + "," + VirtualRow + "]}");
        var api = await LoggedInAsync(_fake);
        var deals = await new SupplierApiService(api).GetDealsAsync(ClientId);
        deals.Should().ContainSingle().Which.Id.Should().Be("77", "долг по чеку — не сделка с поставщиком и не «наш долг»");
    }

    [Fact]
    public async Task Paying_a_check_debt_goes_by_receipt_without_prefix()
    {
        _fake.Json("POST", "/api/main/pos/sales/" + SaleUuid + "/pay-debt/", 200, "{\"paid\":\"120.00\",\"left\":\"0.00\"}");
        var api = await LoggedInAsync(_fake);
        using var debts = new ClientDebtsApiService(api, new AppSettings { ApiBaseUrl = _fake.Url });
        var result = await debts.PaySaleDebtAsync("sale:" + SaleUuid, 120, "cash", null, "key-check-1");
        result.Should().NotBeNull();
        Requests(_fake, "POST", "/pay-debt/").Should().ContainSingle().Which.RequestMessage.Path.Should().Be("/api/main/pos/sales/" + SaleUuid + "/pay-debt/");
        _fake.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/deals/"));
    }

    [Fact]
    public async Task Due_date_sync_patches_the_real_deal_not_the_virtual_row()
    {
        // виртуальная строка того же чека стоит первой — PATCH уходит только настоящей сделке
        _fake.Json("GET", "/api/main/clients/" + ClientId + "/deals/", 200,
            "[{\"id\":\"sale:" + SaleUuid + "\",\"is_check_debt\":true,\"sale\":\"" + SaleUuid + "\",\"remaining_debt\":\"120\"},{\"id\":\"91\",\"sale\":\"" + SaleUuid + "\",\"remaining_debt\":\"120\"}]");
        _fake.Json("PATCH", "/api/main/clients/" + ClientId + "/deals/*", 200, "{}");
        var api = await LoggedInAsync(_fake);
        var old = DebtDueDateSync.ApiProvider;
        DebtDueDateSync.ApiProvider = () => api;
        try
        {
            await DebtDueDateSync.ApplyAsync(ClientId, SaleUuid, new DebtSchedulePlan("day", 1, 1, DateTime.Today.AddDays(14), Array.Empty<DebtSchedulePayment>()));
        }
        finally
        {
            DebtDueDateSync.ApiProvider = old;
        }
        var patches = _fake.Server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH").ToList();
        patches.Should().ContainSingle().Which.RequestMessage.Path.Should().Be("/api/main/clients/" + ClientId + "/deals/91/");
    }
}
