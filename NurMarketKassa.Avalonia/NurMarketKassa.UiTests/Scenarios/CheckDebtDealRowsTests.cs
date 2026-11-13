using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-11 (сводка Умара): GET clients/&lt;id&gt;/deals/ отдаёт в конце ВИРТУАЛЬНЫЕ строки старых долговых чеков (id «sale:&lt;uuid&gt;», is_check_debt).
/// Общий загрузчик долгов кассы и программы владельца (PayDebtDialog.LoadNoSaleDebtRowsAsync — окно «Оплата долга», «Долги клиентов», «Принять оплату»):
/// обычная сделка без чека — «Долг без продажи», как раньше; виртуальная строка чека, который уже в списке, — второй раз не считается; чека нет в списке —
/// строка «Долг по чеку №…» (Id = uuid чека, без сделки), «Оплатить» по ней — оплата долга по чеку (pos/sales/&lt;uuid&gt;/pay-debt/), «sale:» не уходит в адреса сделок.
/// Сервер — поддельный NurCRM (WireMock).</summary>
[Collection("UiScenarios")]
public sealed class CheckDebtDealRowsTests
{
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string KnownSale = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string OldSale = "bbbbbbbb-0000-4000-8000-000000000002";

    private static async Task<List<DebtSaleRow>> LoadAsync(IReadOnlyList<DebtSaleRow> saleRows)
    {
        var method = typeof(PayDebtDialog).GetMethod("LoadNoSaleDebtRowsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<List<DebtSaleRow>>)method.Invoke(null, [ClientId, saleRows, CancellationToken.None, null])!;
        return await task;
    }

    [AvaloniaFact]
    public async Task Deals_with_regular_and_virtual_rows_no_double_count_and_check_row_pays_by_receipt()
    {
        TestHost.Ensure();
        var fake = UiFake.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
        fake.Json("GET", "/api/main/clients/" + ClientId + "/deals/", 200, "{\"count\":3,\"results\":["
            + "{\"id\":\"501\",\"kind\":\"debt\",\"title\":\"Долг\",\"note\":\"занял\",\"amount\":\"300.00\",\"remaining_debt\":\"300.00\",\"created_at\":\"2026-11-01T09:00:00Z\"},"
            + "{\"id\":\"sale:" + KnownSale + "\",\"is_check_debt\":true,\"title\":\"Долг по чеку №1001\",\"amount\":\"200.00\",\"remaining_debt\":\"200.00\"},"
            + "{\"id\":\"sale:" + OldSale + "\",\"is_check_debt\":true,\"title\":\"Долг по чеку №17\",\"amount\":\"500.00\",\"remaining_debt\":\"150.00\",\"created_at\":\"2025-01-10T09:00:00Z\"}]}");
        fake.Json("POST", "/api/main/pos/sales/" + OldSale + "/pay-debt/", 200, "{\"paid\":\"150.00\",\"left\":\"0.00\"}");

        // чек KnownSale уже пришёл из pos/sales (?status=debt) — с точным остатком
        var saleRows = new List<DebtSaleRow> { new() { Id = KnownSale, Number = "1001", Amount = 200, SaleRemaining = 200, DealId = "" } };
        var rows = await LoadAsync(saleRows);

        rows.Should().HaveCount(2, "обычная сделка + старый чек; чек 1001 уже в списке — не задваивается");
        var noSale = rows.Single(r => r.IsNoSale);
        noSale.Id.Should().Be("deal:501");
        noSale.Amount.Should().Be(300);
        var check = rows.Single(r => !r.IsNoSale);
        check.Id.Should().Be(OldSale, "строка чека — по uuid чека, не «sale:…»");
        check.DealId.Should().BeEmpty("у виртуальной строки нет сделки");
        check.Number.Should().Be("17");
        check.FirstItemName.Should().Be("Долг по чеку №17");
        check.Amount.Should().Be(150);
        check.CanPayByReceipt.Should().BeTrue("«Оплатить» — оплатой долга по чеку");
        (saleRows.Sum(r => r.Amount) + rows.Sum(r => r.Amount)).Should().Be(650, "200 (чек 1001) + 300 (долг без продажи) + 150 (старый чек) — без дубля");

        // «Погасить» строку чека — тем же механизмом, что «Оплатить» у чека в окне: pos/sales/<uuid>/pay-debt/
        using var debts = new ClientDebtsApiService(api, TestHost.Services.GetRequiredService<NurMarketKassa.Configuration.AppSettings>());
        await debts.PaySaleDebtAsync(check.Id, check.Amount, "cash", null, "key-old-check");
        fake.Server.LogEntries.Should().Contain(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/pos/sales/" + OldSale + "/pay-debt/");
        fake.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/deals/sale", StringComparison.OrdinalIgnoreCase)
            || e.RequestMessage.Path.Contains("sale%3A", StringComparison.OrdinalIgnoreCase), "«sale:…» не уходит в адреса сделок");
        fake.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.Contains("/deals/") && e.RequestMessage.Path.Contains("/pay/"), "оплата не через сделку");
    }
}
