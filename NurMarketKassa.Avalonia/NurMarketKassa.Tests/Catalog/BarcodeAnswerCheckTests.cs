using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-11 (владелец «да сделай», разбор «Кыргызстанские штрихкоды (470)», п. 1а–е): сверка штрихкода в ответах сервера во всех местах, кэш общей базы,
/// поиск доп. кода в 3–5-м результате, 13 → 14 цифр, мягкая подсказка при неверной контрольной цифре. Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class BarcodeAnswerCheckTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly List<CatalogProductTileVm> _savedCatalog = CatalogCacheService.Products.ToList();

    public BarcodeAnswerCheckTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
    }

    public void Dispose()
    {
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_savedCatalog);
    }

    private static string Ean13(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + ((10 - sum % 10) % 10);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(FakeNurCrmServer Server, CatalogApiService Api, ICatalogApiService? Previous)> StartAsync()
    {
        var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        var client = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await client.LoginAsync("cashier@test.kg", "12345678");
        var api = new CatalogApiService(client);
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = api;
        return (server, api, previous);
    }

    // ---------------------------------------------------------------- 1а/1б: общий помощник сверки

    [Fact]
    public void OnlyIfHasCode_keeps_exact_main_or_alternate_and_drops_foreign_answers()
    {
        var code = Ean13("470000471007");
        var neighbour = Ean13("470000471008");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"id\":\"a\",\"barcode\":\"" + code + "\"}"), code, requireField: true).Should().NotBeNull();
        PurchaseReceivingService.OnlyIfHasCode(J("{\"id\":\"a\",\"barcode\":\"4600000000011\",\"alternate_barcodes\":[{\"barcode\":\"" + code + "\"}]}"), code, true)
            .Should().NotBeNull("доп. штрихкод товара — тот же товар");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"id\":\"w\",\"name\":\"Вафли Енсо вес\",\"barcode\":\"" + neighbour + "\"}"), code, true)
            .Should().BeNull("чужой товар («сосед» в одну цифру) не подставляется и не считается найденным");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"id\":\"w\",\"name\":\"Весовой без штрихкода\"}"), code, true)
            .Should().BeNull("склад: у ответа нет штрихкода вовсе — товар не наш");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"name\":\"Огурцы 900гр х6\"}"), code, requireField: false)
            .Should().NotBeNull("общая база отвечает на точный запрос и может не присылать штрихкод");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"name\":\"Рола Макарон\",\"barcode\":\"" + neighbour + "\"}"), code, requireField: false)
            .Should().BeNull("общая база с другим штрихкодом — отбросить");
        PurchaseReceivingService.OnlyIfHasCode(null, code, true).Should().BeNull();
    }

    [Fact]
    public void OnlyIfHasCode_accepts_the_same_code_written_with_a_leading_zero()
    {
        var code = Ean13("470001206094");
        PurchaseReceivingService.OnlyIfHasCode(J("{\"id\":\"g\",\"barcode\":\"0" + code + "\"}"), code, true)
            .Should().NotBeNull("GTIN-14 с нулём впереди — та же запись кода (13 → 14)");
    }

    // ---------------------------------------------------------------- 1б: массовая загрузка приёмки (BulkReceivingFeeder)

    [Fact]
    public async Task Bulk_receiving_feeder_does_not_turn_a_new_line_into_a_receipt_of_a_foreign_product()
    {
        var foreignScan = Ean13("470000471097");
        var neighbour = Ean13("470000471096");
        var exactScan = Ean13("470000471007");
        var (server, _, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", $"/api/main/products/warehouse-barcode/{foreignScan}/", 200,
                "{\"product\":{\"id\":\"w1\",\"name\":\"Вафли Енсо вес\",\"barcode\":\"" + neighbour + "\",\"unit\":\"кг\",\"quantity\":3,\"price\":\"420\"}}");
            server.Json("GET", $"/api/main/products/warehouse-barcode/{exactScan}/", 200,
                "{\"product\":{\"id\":\"w2\",\"name\":\"Огурцы 900гр х6\",\"barcode\":\"" + exactScan + "\",\"unit\":\"шт\",\"quantity\":3,\"price\":\"250\"}}");
            try
            {
                var job = new ReceivingJob();
                job.Lines.Add(new JobLine { Barcode = foreignScan, ProductName = "Огурцы 3л", Source = ReceivingSource.Unknown, Quantity = 1 });
                job.Lines.Add(new JobLine { Barcode = exactScan, ProductName = "Огурцы", Source = ReceivingSource.Unknown, Quantity = 1 });
                await BulkReceivingFeeder.PrepareJobAsync(job, CancellationToken.None);

                job.Lines[0].ProductId.Should().BeNull("ответ сервера про другой штрихкод — строка остаётся новым товаром");
                job.Lines[0].ProductName.Should().Be("Огурцы 3л");
                job.Lines[1].ProductId.Should().Be("w2", "точный штрихкод — приход к найденному товару");
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    // ---------------------------------------------------------------- 1в: кэш общей базы

    [Fact]
    public async Task Global_base_answer_about_another_code_is_not_cached_under_the_scanned_code()
    {
        var scanned = Ean13("470999123450");
        var neighbour = Ean13("470999123460");
        var exact = Ean13("470999123470");
        var (server, api, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", $"/api/main/products/global-barcode/{scanned}/", 200, "{\"name\":\"Рола Макарон ассорти\",\"barcode\":\"" + neighbour + "\"}");
            server.Json("GET", $"/api/main/products/global-barcode/{exact}/", 200, "{\"name\":\"Огурцы Беловодское 3л\",\"barcode\":\"" + exact + "\"}");
            try
            {
                await api.FindGlobalProductByBarcodeAsync(scanned);
                GlobalBarcodeCache.TryGet(scanned).Should().BeNull("ответ про другой код в кэш под отсканированным кодом не кладётся");

                (await api.FindGlobalProductByBarcodeAsync(exact)).Should().NotBeNull();
                GlobalBarcodeCache.TryGet(exact).Should().NotBeNull("ответ именно про этот код кэшируется, как раньше");
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    // ---------------------------------------------------------------- 1г: доп. штрихкод в 3–5-м результате поиска

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Server_search_finds_the_product_by_alternate_barcode_in_positions_3_to_5(int position)
    {
        var code = Ean13("470000471007");
        var (server, api, previous) = await StartAsync();
        using (server)
        {
            var results = new List<ProductDto>();
            for (var i = 1; i <= 5; i++)
            {
                var id = "s-" + i;
                var own = Ean13("46000000000" + i);
                var alts = i == position ? "[{\"barcode\":\"" + code + "\",\"name\":\"упаковка\"}]" : "[]";
                server.Json("GET", $"/api/main/products/{id}/", 200,
                    "{\"id\":\"" + id + "\",\"name\":\"Товар " + i + "\",\"price\":\"100\",\"barcode\":\"" + own + "\",\"alternate_barcodes\":" + alts + ",\"unit\":\"шт\",\"kind\":\"product\"}");
                results.Add(new ProductDto { Id = id, Title = "Товар " + i, Price = 100m, Barcode = own });
            }
            try
            {
                var resolved = await ProductCodeLookup.ResolveServerResultsAsync(api, results, ProductCodeLookup.Candidates(code), ApiBase);
                resolved.Tile.Should().NotBeNull($"доп. штрихкод у {position}-го результата поиска должен находиться (раньше проверялись только первые два)");
                resolved.Tile!.Id.Should().Be("s-" + position);
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    [Fact]
    public async Task Server_search_with_no_matching_alternate_barcode_still_finds_nothing()
    {
        var code = Ean13("470000471007");
        var (server, api, previous) = await StartAsync();
        using (server)
        {
            var results = new List<ProductDto>();
            for (var i = 1; i <= 5; i++)
            {
                var id = "n-" + i;
                var own = Ean13("46000000001" + i);
                server.Json("GET", $"/api/main/products/{id}/", 200,
                    "{\"id\":\"" + id + "\",\"name\":\"Товар " + i + "\",\"price\":\"100\",\"barcode\":\"" + own + "\",\"alternate_barcodes\":[{\"barcode\":\"" + Ean13("470000471008") + "\"}],\"unit\":\"шт\"}");
                results.Add(new ProductDto { Id = id, Title = "Товар " + i, Price = 100m, Barcode = own });
            }
            try
            {
                var resolved = await ProductCodeLookup.ResolveServerResultsAsync(api, results, ProductCodeLookup.Candidates(code), ApiBase);
                resolved.Tile.Should().BeNull("у всех пяти — соседний доп. код, искомого нет");
                _repo.TryGetTileByBarcode(code).Should().BeNull();
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    // ---------------------------------------------------------------- 1д: 13 → 14 цифр

    [Fact]
    public void Ean13_scan_finds_the_product_stored_as_gtin14_with_a_leading_zero_only_exactly()
    {
        var code = Ean13("470001206094"); // 4700012060943
        var tile = CatalogTestHelper.CreateTile("g14", "Товар GTIN-14", "0" + code, 50);
        _repo.SyncReplaceAllWithDiff([tile]);
        CatalogCacheService.Products.Add(tile);

        ProductCodeLookup.Candidates(code).Should().Equal(code, "0" + code);
        ProductCodeLookup.FindLocal(code, allowWeightLabel: false)!.Product.Id.Should().Be("g14");
        ProductCodeLookup.FindLocal("00" + code, allowWeightLabel: false).Should().BeNull("два нуля — уже не та запись");
        ProductCodeLookup.FindLocal(code[..12] + (char)('0' + (code[12] - '0' + 1) % 10), allowWeightLabel: false).Should().BeNull("сосед по контрольной цифре не находит");
    }

    // ---------------------------------------------------------------- 1е: мягкая подсказка о контрольной цифре

    [Theory]
    [InlineData("4700004710078", false)] // верный EAN-13 (огурцы)
    [InlineData("4700004710079", true)]  // неверная контрольная цифра
    [InlineData("47009530", false)]      // верный EAN-8? — проверяется ниже вычислением
    [InlineData("2700123012508", false)] // код на «2» (весы/внутренний) — не трогаем
    [InlineData("470000471007", false)]  // 12 цифр — не EAN-13/EAN-8
    [InlineData("АБВ123", false)]
    public void LooksMisread_only_for_regular_ean13_and_ean8_with_a_wrong_check_digit(string code, bool expected)
    {
        if (code == "47009530")
        {
            // EAN-8: веса 3,1,3,1…; верная цифра — по вычислению, неверная — любая другая
            var sum = 0;
            for (var i = 0; i < 7; i++)
                sum += (code[i] - '0') * (i % 2 == 0 ? 3 : 1);
            var good = code[..7] + ((10 - sum % 10) % 10);
            ScanMessages.LooksMisread(good).Should().BeFalse(good);
            ScanMessages.LooksMisread(code[..7] + (char)('0' + (good[7] - '0' + 1) % 10)).Should().BeTrue();
            return;
        }
        ScanMessages.LooksMisread(code).Should().Be(expected, code);
    }

    [Fact]
    public async Task ConfirmUnknownCode_asks_to_rescan_only_for_a_wrong_check_digit_and_add_anyway_continues()
    {
        var prompts = new FakePrompts { Answer = true };
        (await ScanMessages.ConfirmUnknownCodeAsync(prompts, "4700004710078")).Should().BeTrue();
        prompts.Questions.Should().BeEmpty("верная контрольная цифра — без вопроса, сразу «Добавить товар»");

        (await ScanMessages.ConfirmUnknownCodeAsync(prompts, "4700004710079")).Should().BeTrue("«Всё равно добавить»");
        prompts.Questions.Should().ContainSingle().Which.Should().Be(ScanMessages.CheckDigitHint);

        prompts.Answer = false;
        (await ScanMessages.ConfirmUnknownCodeAsync(prompts, "4700004710079")).Should().BeFalse("«Отсканирую ещё раз» — товар не добавляется");
    }

    [Fact]
    public async Task Kassa_scan_of_an_unknown_code_with_a_wrong_check_digit_shows_the_hint_before_add_product()
    {
        var offers = new List<string>();
        var (server, _, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", "/api/main/products/list/", 200, "{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}");
            try
            {
                // «Отсканирую ещё раз» — предложения «Добавить товар» нет
                using (var h = new BasketVmHarness(c => { offers.Add(c); return Task.CompletedTask; }))
                {
                    h.Prompts.Answer = false;
                    h.Vm.EnqueueBarcode("4700004710079");
                    (await WaitAsync(() => h.Prompts.Questions.Count > 0)).Should().BeTrue("подсказка о контрольной цифре показана");
                    h.Prompts.Questions[0].Should().Be(ScanMessages.CheckDigitHint);
                    await Task.Delay(200);
                    offers.Should().BeEmpty();
                }

                // «Всё равно добавить» — обычное «Добавить товар» с этим кодом
                using (var h = new BasketVmHarness(c => { offers.Add(c); return Task.CompletedTask; }))
                {
                    h.Prompts.Answer = true;
                    h.Vm.EnqueueBarcode("4700004710079");
                    (await WaitAsync(() => offers.Count > 0)).Should().BeTrue("после «Всё равно добавить» — предложение добавить товар");
                    offers.Should().ContainSingle().Which.Should().Be("4700004710079");
                }

                // верный, но незнакомый код — без подсказки, сразу «Добавить товар»
                offers.Clear();
                using (var h = new BasketVmHarness(c => { offers.Add(c); return Task.CompletedTask; }))
                {
                    h.Vm.EnqueueBarcode("4700004710078");
                    (await WaitAsync(() => offers.Count > 0)).Should().BeTrue();
                    h.Prompts.Questions.Should().BeEmpty();
                }
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(30);
        }
        return condition();
    }
}
