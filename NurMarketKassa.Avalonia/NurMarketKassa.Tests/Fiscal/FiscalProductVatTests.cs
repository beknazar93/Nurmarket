using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Fiscal;

/// <summary>2026-11-14 (П0-1): ставка НДС товара (vat_rate: число или null = ставка компании) и правила разбора полей формы «Налоги и ККМ». Сервер — поддельный WireMock.
/// Запускать только tools/qa/test-safe.ps1.</summary>
[Collection(nameof(FiscalStateCollection))]
public sealed class FiscalProductVatTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    private static string LastPatchBody(FakeNurCrmServer server) =>
        server.Server.LogEntries.Last(e => e.RequestMessage.Method == "PATCH").RequestMessage.Body ?? "";

    [Fact]
    public async Task Vat_rate_is_sent_only_when_the_owner_changed_it()
    {
        using var server = new FakeNurCrmServer();
        server.Json("PATCH", "/api/main/products/p-9/", 200, """{"id":"p-9"}""");
        var catalog = await LoginAsync(server);

        // не менял — поля нет (старый сервер, режим off)
        await catalog.UpdateProductAsync("p-9", new ProductEditRequest { Name = "Хлеб", Unit = "шт", Price = 45, SendQuantity = false });
        LastPatchBody(server).Should().NotContain("vat_rate");

        // выбрал 12 % — строка «12.00»
        server.Server.ResetLogEntries();
        await catalog.UpdateProductAsync("p-9", new ProductEditRequest { Name = "Хлеб", Unit = "шт", Price = 45, SendQuantity = false, SendVatRate = true, VatRate = 12m });
        using (var body = JsonDocument.Parse(LastPatchBody(server)))
            body.RootElement.GetProperty("vat_rate").GetString().Should().Be("12.00");

        // вернул «Ставка компании» — null (убрать ставку товара)
        server.Server.ResetLogEntries();
        await catalog.UpdateProductAsync("p-9", new ProductEditRequest { Name = "Хлеб", Unit = "шт", Price = 45, SendQuantity = false, SendVatRate = true, VatRate = null });
        using (var body = JsonDocument.Parse(LastPatchBody(server)))
            body.RootElement.GetProperty("vat_rate").ValueKind.Should().Be(JsonValueKind.Null);

        // 0 % — освобождён от НДС, это не «ставка компании»
        server.Server.ResetLogEntries();
        await catalog.UpdateProductAsync("p-9", new ProductEditRequest { Name = "Хлеб", Unit = "шт", Price = 45, SendQuantity = false, SendVatRate = true, VatRate = 0m });
        using (var body = JsonDocument.Parse(LastPatchBody(server)))
            body.RootElement.GetProperty("vat_rate").GetString().Should().Be("0.00");
    }

    [Fact]
    public void Product_card_vat_rate_is_read_as_number_string_or_null()
    {
        FiscalFormRules.ReadProductVat(J("{\"vat_rate\":\"12.00\"}")).Should().Be(12m);
        FiscalFormRules.ReadProductVat(J("{\"vat_rate\":0}")).Should().Be(0m);
        FiscalFormRules.ReadProductVat(J("{\"vat_rate\":null}")).Should().BeNull("null — ставка компании");
        FiscalFormRules.ReadProductVat(J("{\"name\":\"x\"}")).Should().BeNull("старый сервер без поля");
        FiscalFormRules.ReadProductVat(J("[]")).Should().BeNull();
    }

    [Theory]
    [InlineData("0; 12", new[] { "0", "12" })]
    [InlineData("12", new[] { "12" })]
    [InlineData("12,5 ; 0", new[] { "12.5", "0" })]
    [InlineData("0 12 12", new[] { "0", "12" })]
    [InlineData("12%", new[] { "12" })]
    [InlineData("", new string[0])]
    public void Rates_text_is_parsed(string text, string[] expected)
    {
        var (rates, error) = FiscalFormRules.ParseRates(text);
        error.Should().BeNull();
        rates.Should().Equal(expected.Select(e => decimal.Parse(e, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("12; abc")]
    [InlineData("101")]
    [InlineData("-1")]
    public void Bad_rates_give_an_error_and_no_rates(string text)
    {
        var (rates, error) = FiscalFormRules.ParseRates(text);
        error.Should().NotBeNullOrWhiteSpace();
        rates.Should().BeEmpty();
    }

    [Fact]
    public void Optional_percent_and_formatting()
    {
        FiscalFormRules.ParseOptionalPercent("").Should().Be(((decimal?)null, (string?)null));
        FiscalFormRules.ParseOptionalPercent(" 2 ").Rate.Should().Be(2m);
        FiscalFormRules.ParseOptionalPercent("2,5").Rate.Should().Be(2.5m);
        FiscalFormRules.ParseOptionalPercent("abc").Error.Should().NotBeNull();
        FiscalFormRules.FormatPercent(12.00m).Should().Be("12");
        FiscalFormRules.FormatPercent(2.5m).Should().Be("2,5");
        FiscalFormRules.FormatRates(new[] { 0m, 12m }).Should().Be("0; 12");
        FiscalFormRules.ToApi(12m).Should().Be("12.00");
    }

    [Fact]
    public void Mode_text_covers_all_modes()
    {
        foreach (var mode in Enum.GetValues<FiscalMode>())
            FiscalFormRules.ModeText(mode).Should().NotBeNullOrWhiteSpace();
        FiscalFormRules.ModeText(FiscalMode.Server).Should().NotBe(FiscalFormRules.ModeText(FiscalMode.Kassa));
    }
}
