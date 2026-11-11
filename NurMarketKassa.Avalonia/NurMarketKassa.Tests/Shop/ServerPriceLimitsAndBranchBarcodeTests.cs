using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11, ответ Умара (NurCRM): пределы цен сервера (продажа/опт ≤ 9 999 999,999; закупка ≤ 9 999 999 999,9999; строка чека ≤ 9 999 999 999,99) —
/// ровно предел принимается, предел + 0,001 — запрет с понятным текстом; «подозрительно большая» (&gt; 1 000 000) — только предупреждение.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ServerPriceLimitsTests
{
    [Fact]
    public void Limits_are_inclusive_exactly_the_limit_passes_and_one_step_more_fails()
    {
        ServerPriceLimits.SaleExceeds(9_999_999.999).Should().BeFalse("ровно предел — можно");
        ServerPriceLimits.SaleExceeds(10_000_000.0).Should().BeTrue("предел + 0,001 — нельзя");
        ServerPriceLimits.PurchaseExceeds(9_999_999_999.9999).Should().BeFalse();
        ServerPriceLimits.PurchaseExceeds(10_000_000_000.0).Should().BeTrue();
        ServerPriceLimits.LineUnitPriceExceeds(9_999_999_999.99).Should().BeFalse();
        ServerPriceLimits.LineUnitPriceExceeds(10_000_000_000.0).Should().BeTrue();
        ServerPriceLimits.SaleExceeds(double.NaN).Should().BeTrue("не число — не принимаем");
        ServerPriceLimits.SaleExceeds(double.PositiveInfinity).Should().BeTrue();
        ServerPriceLimits.SaleExceeds(0).Should().BeFalse();
    }

    [Fact]
    public void Texts_name_the_limit_in_five_languages()
    {
        ServerPriceLimits.SaleMaxText.Should().Be("9 999 999,999");
        ServerPriceLimits.PurchaseMaxText.Should().Be("9 999 999 999,9999");
        ServerPriceLimits.SaleTooBigText.Should().Contain("9 999 999,999");
        ServerPriceLimits.PurchaseTooBigText.Should().Contain("9 999 999 999,9999");
        ServerPriceLimits.ProductPriceProblem(9_999_999.999, 9_999_999.999, 9_999_999_999.9999).Should().BeNull();
        ServerPriceLimits.ProductPriceProblem(10_000_000, null, null).Should().Be(ServerPriceLimits.SaleTooBigText);
        ServerPriceLimits.ProductPriceProblem(null, 10_000_000, null).Should().Be(ServerPriceLimits.SaleTooBigText, "оптовая — тот же предел");
        ServerPriceLimits.ProductPriceProblem(null, null, 10_000_000_000).Should().Be(ServerPriceLimits.PurchaseTooBigText);
    }

    [Fact]
    public void Receiving_line_blocks_only_above_server_limits_and_warns_above_one_million()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 25, SalePrice = 30 };

        l.SalePrice = 9_999_999.999;
        PurchaseReceivingService.Validate(new[] { l }).Should().BeEmpty("ровно предел цены продажи — проводится");
        l.IsPriceOverLimit.Should().BeFalse();
        l.IsPriceTooBig.Should().BeTrue("больше 1 000 000 — красное предупреждение");

        l.SalePrice = 10_000_000;
        l.IsPriceOverLimit.Should().BeTrue();
        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains(ServerPriceLimits.SaleMaxText), "предел + 0,001 — не проводится");

        l.SalePrice = 30;
        l.PurchasePrice = 9_999_999_999.9999;
        PurchaseReceivingService.Validate(new[] { l }).Should().NotContain(p => p.Contains(ServerPriceLimits.PurchaseMaxText), "ровно предел закупки — можно");
        l.PurchasePrice = 10_000_000_000;
        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains(ServerPriceLimits.PurchaseMaxText));

        var warnOnly = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 25, SalePrice = 1_500_000 };
        warnOnly.IsPriceTooBig.Should().BeTrue();
        PurchaseReceivingService.Validate(new[] { warnOnly }).Should().BeEmpty("выше 1 000 000, но в пределах сервера — не блокируем");
    }

    [Fact]
    public void Typed_price_above_limit_is_not_accepted_and_the_old_price_stays()
    {
        var l = new ReceivingLineVm { ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 25, SalePrice = 30 };

        l.SetSaleText("9999999,99");
        l.SalePrice.Should().Be(9_999_999.99, "в пределах 9 999 999,999 — принято");
        l.CommitText();
        l.SetSaleText("10000000,00");
        l.SalePrice.Should().Be(9_999_999.99, "выше предела — прежнее значение остаётся");
        l.PriceProblemText.Should().Be(ServerPriceLimits.SaleTooBigText);
        l.CommitText();

        l.SetPurchaseText("10 000 000 000,01");
        l.PurchasePrice.Should().Be(25);
        l.PriceProblemText.Should().Be(ServerPriceLimits.PurchaseTooBigText);
        l.CommitText();
        l.SetPurchaseText("9 999 999 999,9999");
        l.PurchasePrice.Should().Be(9_999_999_999.9999);
    }
}

/// <summary>2026-11-11, ответ Умара: склад по штрихкоду без ?branch=<uuid> ищет по всей компании (может отдать товар чужого филиала) — касса шлёт branch, когда филиал известен;
/// «неоднозначный штрихкод» (один код у двух товаров) — понятное сообщение, товар не подставляется и строка не добавляется.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class WarehouseBarcodeBranchTests
{
    private const string Branch = "11111111-2222-3333-4444-555555555555";

    private static async Task<(FakeNurCrmServer Server, CatalogApiService Catalog)> StartAsync(string? branchId)
    {
        var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        var branchJson = branchId is null ? "" : $",\"primary_branch_id\":\"{branchId}\"";
        server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"cashier\"" + branchJson + "}}");
        server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест\",\"role\":\"cashier\"" + branchJson + ",\"company\":{\"id\":\"c1\",\"name\":\"Тест\"}}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return (server, new CatalogApiService(api));
    }

    private static string? BranchOfLastBarcodeRequest(FakeNurCrmServer server)
    {
        var req = server.Server.LogEntries.Last(e => e.RequestMessage.Path.Contains("warehouse-barcode")).RequestMessage;
        return req.Query is { } q && q.TryGetValue("branch", out var v) ? v.ToString() : null;
    }

    [Fact]
    public async Task Request_carries_the_branch_of_the_cashbox_when_it_is_known()
    {
        var (server, catalog) = await StartAsync(Branch);
        using (server)
        {
            server.Json("GET", "/api/main/products/warehouse-barcode/4600000000028/", 200, "{\"product\":{\"id\":\"p1\",\"name\":\"Вода\",\"barcode\":\"4600000000028\"}}");
            var found = await catalog.FindWarehouseProductByBarcodeAsync("4600000000028");
            found.Should().NotBeNull();
            BranchOfLastBarcodeRequest(server).Should().Be(Branch, "без branch сервер ищет по всей компании");
        }
    }

    [Fact]
    public async Task Request_has_no_branch_parameter_when_the_branch_is_unknown()
    {
        var (server, catalog) = await StartAsync(null);
        using (server)
        {
            server.Json("GET", "/api/main/products/warehouse-barcode/4600000000028/", 404, "{\"detail\":\"not found\"}");
            var found = await catalog.FindWarehouseProductByBarcodeAsync("4600000000028");
            found.Should().BeNull("404 — товара нет, это не ошибка");
            BranchOfLastBarcodeRequest(server).Should().BeNull("филиала нет — параметр не добавляем и не ломаемся");
        }
    }

    [Theory]
    [InlineData(400, "{\"detail\":\"Неоднозначный штрихкод: найдено несколько товаров\"}")]
    [InlineData(409, "{\"code\":\"ambiguous_barcode\",\"detail\":\"error\"}")]
    [InlineData(400, "{\"detail\":\"Multiple products match this barcode\"}")]
    public async Task Ambiguous_barcode_answer_becomes_a_friendly_error_and_no_product_is_substituted(int status, string body)
    {
        var (server, catalog) = await StartAsync(Branch);
        using (server)
        {
            server.Json("GET", "/api/main/products/warehouse-barcode/4600000000028/", status, body);
            server.Json("GET", "/api/main/products/global-barcode/4600000000028/", 200, "{\"name\":\"Из общей базы\",\"barcode\":\"4600000000028\"}");
            var previous = PosApp.CatalogApi;
            PosApp.CatalogApi = catalog;
            try
            {
                var act = async () => await catalog.FindWarehouseProductByBarcodeAsync("4600000000028");
                var thrown = await act.Should().ThrowAsync<AmbiguousBarcodeException>();
                thrown.Which.Message.Should().Be(AmbiguousBarcodeException.FriendlyText);

                // приёмка: исключение доходит до окна, строка не создаётся (ни «новый товар», ни товар из общей базы)
                var receive = async () => await PurchaseReceivingService.Instance.LookupAsync("4600000000028", 1);
                await receive.Should().ThrowAsync<AmbiguousBarcodeException>();
            }
            finally
            {
                PosApp.CatalogApi = previous;
            }
        }
    }

    [Fact]
    public async Task Other_bad_request_is_not_taken_for_an_ambiguous_barcode()
    {
        var (server, catalog) = await StartAsync(Branch);
        using (server)
        {
            server.Json("GET", "/api/main/products/warehouse-barcode/4600000000028/", 400, "{\"detail\":\"Некорректный запрос\"}");
            var act = async () => await catalog.FindWarehouseProductByBarcodeAsync("4600000000028");
            var thrown = await act.Should().ThrowAsync<ApiException>();
            thrown.Which.Should().NotBeOfType<AmbiguousBarcodeException>();
        }
    }

    [Fact]
    public async Task Product_card_save_refuses_a_price_above_the_server_limit_before_sending()
    {
        var (server, catalog) = await StartAsync(Branch);
        using (server)
        {
            server.Json("PATCH", "/api/main/products/p1/", 200, "{\"id\":\"p1\"}");
            var tooBig = async () => await catalog.UpdateProductAsync("p1", new ProductEditRequest { Name = "Вода", Price = 10_000_000 });
            (await tooBig.Should().ThrowAsync<ApiException>()).Which.Message.Should().Be(ServerPriceLimits.SaleTooBigText);
            var wholesale = async () => await catalog.UpdateProductAsync("p1", new ProductEditRequest { Name = "Вода", Price = 10, WholesalePrice = 10_000_000 });
            await wholesale.Should().ThrowAsync<ApiException>();
            var purchase = async () => await catalog.UpdateProductAsync("p1", new ProductEditRequest { Name = "Вода", Price = 10, PurchasePrice = 10_000_000_000 });
            (await purchase.Should().ThrowAsync<ApiException>()).Which.Message.Should().Be(ServerPriceLimits.PurchaseTooBigText);
            server.Server.LogEntries.Count(e => e.RequestMessage.Method == "PATCH").Should().Be(0, "цена выше предела на сервер не уходит");

            await catalog.UpdateProductAsync("p1", new ProductEditRequest { Name = "Вода", Price = 9_999_999.999, WholesalePrice = 9_999_999.999, PurchasePrice = 9_999_999_999.9999 });
            var sent = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "PATCH").RequestMessage.Body!;
            sent.Should().Contain("\"price\":\"9999999.999\"", "3 знака цены продажи не режутся молча");
            sent.Should().Contain("\"purchase_price\":\"9999999999.9999\"");

            var patch = async () => await catalog.PatchProductFieldsAsync("p1", new Dictionary<string, object?> { ["price"] = 10_000_000.0 });
            await patch.Should().ThrowAsync<ApiException>();
        }
    }
}
