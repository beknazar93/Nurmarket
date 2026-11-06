using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06 (живой тест Этапа 1): Б-3 — номер прихода после отправки везде серверный (number_display), а не номер кассы; Б-5 — без интернета приём берёт сохранённый
/// список поставщиков (память кассы, по компании); В-3 — подпись «принят … · отправлен …» у досланного прихода. Сервер — WireMock; очередь/нумерация — общая коллекция.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ServerNumberAndSuppliersTests : IDisposable
{
    private readonly string? _savedAccountKey = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _cachePath = Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "suppliers-cache.json");
    private readonly byte[]? _savedCache;

    public ServerNumberAndSuppliersTests()
    {
        _savedCache = File.Exists(_cachePath) ? File.ReadAllBytes(_cachePath) : null;
    }

    public void Dispose()
    {
        UserPreferences.Instance.LastDataAccountKey = _savedAccountKey;
        try
        {
            if (_savedCache is not null)
                File.WriteAllBytes(_cachePath, _savedCache);
            else if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch { }
    }

    private static ReceivingLineVm Line(string id)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = 15, OriginalPurchasePrice = 10, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 2;
        l.PurchasePrice = 10;
        l.SalePrice = 15;
        return l;
    }

    private static async Task<IDisposable> UseApiAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return new Restorer(() => PosApp.CatalogApi = previous);
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    // ------------------------------------------------------------------ Б-3: серверный номер

    [Fact]
    public async Task After_sending_the_server_number_is_used_not_the_till_number()
    {
        using var server = new FakeNurCrmServer();
        var serverNumber = $"ПР-{DateTime.Now.Year}-007781";
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-srv-7781\",\"total_amount\":\"20.00\",\"number_display\":\"" + serverNumber + "\"}");
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
        server.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\"}");
        using var restore = await UseApiAsync(server);
        var st = new ReceivingPostState { ReceivedAt = DateTime.Now };
        st.KeyOf = vm => vm.ProductId ?? "";

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), false, "тест", default, null, st);

        result.ReceiptId.Should().Be("r-srv-7781");
        result.ReceiptNumber.Should().Be(serverNumber, "в итог прихода идёт номер из ответа сервера");
        st.ServerNumber.Should().Be(serverNumber);
        st.DocNumber.Should().Be(serverNumber, "номер кассы, показанный до отправки, заменён серверным");
        if (DocumentNumbering.Store.IsComplete(DocSeries.Receipt))
            DocumentNumbering.ReceiptText("r-srv-7781").Should().Be(serverNumber, "список, окно документа, печать и PDF показывают один номер");
    }

    [Fact]
    public async Task Direct_posting_without_queue_state_also_returns_the_server_number()
    {
        using var server = new FakeNurCrmServer();
        var serverNumber = $"ПР-{DateTime.Now.Year}-007782";
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-srv-7782\",\"total_amount\":\"20.00\",\"number_display\":\"" + serverNumber + "\"}");
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
        server.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\"}");
        using var restore = await UseApiAsync(server);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), false, "тест");

        result.ReceiptNumber.Should().Be(serverNumber);
    }

    [Fact]
    public void Server_number_display_wins_over_the_number_the_till_wrote_into_the_comment()
    {
        var year = DateTime.Now.Year;
        var json = JsonDocument.Parse("{\"count\":1,\"results\":[{\"id\":\"r-1\",\"created_at\":\"" + year + "-03-05T10:00:00Z\",\"number_display\":\"ПР-" + year + "-000078\","
                                      + "\"comment\":\"№ ПР-" + year + "-000077; накладная поставщика: 45\"}]}").RootElement;

        var page = SupplierApiService.ParseDocPage(json, DocSeries.Receipt, new DocNumberingSettings().Normalized());

        page.Docs.Should().ContainSingle().Which.Claim.Should().Be(78, "номер кассы в комментарии (77) расходился с серверным (78) — для показа берётся серверный");
    }

    // ------------------------------------------------------------------ В-3: подпись принят/отправлен

    [Fact]
    public void A_receipt_sent_after_more_than_half_a_minute_shows_both_times_even_under_two_minutes()
    {
        var now = DateTime.Now;
        var job = new ReceivingJob { Status = ReceivingJobStatus.Done, CreatedAt = now.AddSeconds(-90), SentAt = now, FinishedAt = now };

        var text = job.StatusText;

        text.Should().Contain(job.ReceivedAt.ToString("HH:mm")).And.Contain(now.ToString("HH:mm"), "видно и время приёма, и время отправки");
        job.ReceivedAt.ToString("HH:mm").Should().NotBe(now.ToString("HH:mm"));
    }

    [Fact]
    public void A_receipt_sent_at_once_keeps_the_short_signature()
    {
        var now = DateTime.Now;
        var job = new ReceivingJob { Status = ReceivingJobStatus.Done, CreatedAt = now.AddSeconds(-3), SentAt = now, FinishedAt = now };

        job.StatusText.Should().NotContain("·", "обычная быстрая отправка — одно время");
    }

    // ------------------------------------------------------------------ Б-5: поставщики из памяти кассы

    private static void StubSuppliers(FakeNurCrmServer s, int status, string body)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/clients/", status, body);
    }

    private const string TwoSuppliers = "{\"results\":[{\"id\":\"sa\",\"full_name\":\"Альфа\",\"phone\":\"+996700000001\"},{\"id\":\"sb\",\"full_name\":\"Бета\",\"phone\":\"+996700000002\"}]}";

    [Fact]
    public async Task Suppliers_come_from_memory_on_any_refusal_and_the_cache_holds_the_real_company_id()
    {
        using var server = new FakeNurCrmServer();
        StubSuppliers(server, 200, TwoSuppliers);
        using var restore = await UseApiAsync(server);
        UserPreferences.Instance.LastDataAccountKey = "c:mine-co";

        var fresh = await PurchaseReceivingService.Instance.LoadSuppliersAsync();
        fresh.Select(x => x.Name).Should().Equal("Альфа", "Бета");
        var saved = JsonDocument.Parse(File.ReadAllText(_cachePath)).RootElement;
        saved.GetProperty("CompanyId").GetString().Should().Be(CompanyInfoService.LastCompany?.Id ?? "mine-co", "в память пишется настоящая компания, а не пустая строка");

        // Любой отказ (раньше 4xx пробрасывался, даже когда память есть) — берём сохранённый список.
        StubSuppliers(server, 403, "{\"detail\":\"forbidden\"}");
        var offline = await PurchaseReceivingService.Instance.LoadSuppliersAsync();
        offline.Select(x => x.Id).Should().Equal("sa", "sb");
        PurchaseReceivingService.CachedSuppliers().Should().NotBeNull().And.HaveCount(2, "окно «Закупки» берёт тот же список напрямую, когда его собственный запрос не прошёл");
    }

    [Fact]
    public async Task Suppliers_of_another_company_are_never_read_but_an_old_record_without_company_is()
    {
        using var server = new FakeNurCrmServer();
        StubSuppliers(server, 403, "{\"detail\":\"forbidden\"}");
        using var restore = await UseApiAsync(server);
        UserPreferences.Instance.LastDataAccountKey = "c:mine-co";
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);

        File.WriteAllText(_cachePath, JsonSerializer.Serialize(new { CompanyId = "foreign-co", Items = new[] { new { Id = "x1", Name = "Чужой", Inn = (string?)null, Phone = (string?)null } } }));
        PurchaseReceivingService.CachedSuppliers().Should().BeNull("список чужой компании не показываем");
        var act = async () => await PurchaseReceivingService.Instance.LoadSuppliersAsync();
        await act.Should().ThrowAsync<ApiException>("памяти этой компании нет — честный отказ, а не чужие поставщики");

        File.WriteAllText(_cachePath, JsonSerializer.Serialize(new { CompanyId = "", Items = new[] { new { Id = "o1", Name = "Старый", Inn = (string?)null, Phone = (string?)null } } }));
        PurchaseReceivingService.CachedSuppliers().Should().ContainSingle().Which.Name.Should().Be("Старый", "старая запись без компании читается для текущей компании");
    }

    [Fact]
    public void Remembering_the_list_loaded_by_the_purchases_window_makes_it_available_offline()
    {
        UserPreferences.Instance.LastDataAccountKey = "c:mine-co";
        PurchaseReceivingService.RememberSuppliers(new[]
        {
            new SupplierInfo("s9", "Девятый", "+996700000009", "", "", "123456789", "", "", "", "", null),
        });

        var cached = PurchaseReceivingService.CachedSuppliers();

        cached.Should().ContainSingle().Which.Should().Match<PurchaseReceivingService.Supplier>(s => s.Id == "s9" && s.Name == "Девятый" && s.Phone == "+996700000009" && s.Inn == "123456789");
    }
}
