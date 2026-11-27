using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-26 (ТОП-15 п. 4 отчёта о конкурентах, «Номер ЭСФ в приходе»): номер электронной счёт-фактуры поставщика уходит с приходом (комментарий «ЭСФ №: …» и поле esf_number),
/// читается обратно из поля или из комментария, а поиск находит приход по номеру целиком или по части. В ГНС ничего не отправляется.</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class EsfNumberTests
{
    [Theory]
    [InlineData("№ ПР-2026-000123; ЭСФ №: 0123456789", "0123456789")]
    [InlineData("ЭСФ №: 0123456789; накладная поставщика: 45", "0123456789")]
    [InlineData("накладная поставщика: 45 от 29.10.2026; ЭСФ № 77-ABC-1", "77-ABC-1")]
    [InlineData("ЭСФ: 555", "555")]
    [InlineData("эсф №:  12  34 ", "12 34")]
    public void Number_is_read_from_the_receipt_comment(string comment, string expected) =>
        DocumentNumberingCore.EsfFromText(comment).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("№ ПР-2026-000123; накладная поставщика: 45")]
    [InlineData("нет ЭСФ; накладная 5")]
    [InlineData("ЭСФ №: ;")]
    public void No_number_in_the_comment_gives_empty(string? comment) =>
        DocumentNumberingCore.EsfFromText(comment).Should().BeEmpty();

    [Fact]
    public void Typed_number_is_cleaned_so_it_cannot_break_the_comment()
    {
        DocumentNumberingCore.NormalizeEsf("  12;34   56 ").Should().Be("12,34 56", "«;» разделяет части служебного комментария — заменяется");
        DocumentNumberingCore.NormalizeEsf(new string('9', 80)).Length.Should().Be(40);
        DocumentNumberingCore.NormalizeEsf(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("0123456789", "0123456789", true)]
    [InlineData("0123456789", "456789", true)]
    [InlineData("0123456789", "0123 4567", true)]
    [InlineData("77-ABC-1", "77abc", true)]
    [InlineData("77-ABC-1", "№ 77-abc-1", true)]
    [InlineData("0123456789", "99", false)]
    [InlineData("0123456789", "12", false)]            // короче трёх знаков — не ищем (иначе находится всё подряд)
    [InlineData("", "0123", false)]
    [InlineData("0123456789", "", false)]
    [InlineData("0123456789", "987", false)]
    public void Search_finds_a_receipt_by_the_whole_number_or_a_part(string esf, string query, bool expected) =>
        DocumentNumberingCore.EsfMatches(esf, query).Should().Be(expected);

    // ------------------------------------------------------------------ чтение прихода с сервера

    private static async Task<SupplierApiService> ApiAsync(FakeNurCrmServer server)
    {
        server.StubLogin();
        server.StubProfile();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new SupplierApiService(api);
    }

    [Fact]
    public async Task Receipt_carries_the_esf_number_from_the_server_field()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/suppliers/receipts/r1/", 200,
            "{\"id\":\"r1\",\"supplier_id\":\"s1\",\"esf_number\":\" 0123456789 \",\"comment\":\"ЭСФ №: другой\",\"items\":[{\"id\":\"i1\",\"product_id\":\"p\",\"product_name\":\"Т\",\"qty\":1,\"purchase_price\":10}]}");
        var receipt = await (await ApiAsync(server)).GetReceiptAsync("r1");
        receipt.EsfNumber.Should().Be("0123456789", "поле сервера главнее комментария");
    }

    [Fact]
    public async Task Receipt_falls_back_to_the_comment_and_is_empty_without_it()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/suppliers/receipts/r2/", 200,
            "{\"id\":\"r2\",\"supplier_id\":\"s1\",\"comment\":\"№ ПР-2026-000002; накладная поставщика: 45; ЭСФ №: 0987654321\",\"items\":[]}");
        server.Json("GET", "/api/main/suppliers/receipts/r3/", 200, "{\"id\":\"r3\",\"supplier_id\":\"s1\",\"comment\":\"№ ПР-2026-000003\",\"items\":[]}");
        var api = await ApiAsync(server);
        var r2 = await api.GetReceiptAsync("r2");
        r2.EsfNumber.Should().Be("0987654321");
        r2.SupplierDocNumber.Should().Be("45", "накладная поставщика читается рядом и не мешает");
        (await api.GetReceiptAsync("r3")).EsfNumber.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ отправка вместе с приходом

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static async Task<IDisposable> UseApiAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return new Restorer(() => PosApp.CatalogApi = previous);
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

    private static async Task<JsonElement> PostWithAsync(Action<ReceivingPostState> prepare)
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-esf-1\",\"total_amount\":\"20.00\",\"number_display\":\"ПР-2026-000001\"}");
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
        server.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\"}");
        using var restore = await UseApiAsync(server);
        var st = new ReceivingPostState();
        st.KeyOf = vm => vm.ProductId ?? vm.Barcode;
        prepare(st);
        await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), false, "тест", default, null, st);
        var post = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/suppliers/s1/receipt/");
        return JsonDocument.Parse(post.RequestMessage.Body!).RootElement.Clone();
    }

    [Fact]
    public async Task Esf_number_goes_with_the_receipt_in_the_comment_and_in_its_own_field()
    {
        var body = await PostWithAsync(st =>
        {
            st.SupplierDocNumber = "45";
            st.SupplierDocDate = new DateTime(2026, 11, 20);
            st.EsfNumber = "0123456789";
        });
        var comment = body.GetProperty("comment").GetString()!;
        comment.Should().Contain("ЭСФ №: 0123456789").And.Contain("накладная поставщика: 45 от 20.11.2026");
        body.GetProperty("esf_number").GetString().Should().Be("0123456789");
        DocumentNumberingCore.EsfFromText(comment).Should().Be("0123456789", "что ушло в комментарий, то и читается обратно");
        DocumentNumberingCore.SupplierDocFromText(comment).Number.Should().Be("45", "накладная поставщика по-прежнему читается");
    }

    [Fact]
    public async Task Without_an_esf_number_nothing_extra_is_sent()
    {
        var body = await PostWithAsync(st => st.SupplierDocNumber = "45");
        body.TryGetProperty("esf_number", out _).Should().BeFalse();
        body.GetProperty("comment").GetString().Should().NotContain("ЭСФ");
    }

    // ------------------------------------------------------------------ 2026-11-26: стресс-тест ЭСФ

    [Theory]
    [InlineData("№ ПР-2026-000123; ЭСФ выпишут завтра")]                    // слово «ЭСФ» в обычной фразе — не номер
    [InlineData("накладная поставщика: ЭСФ 12 от 20.11.2026")]             // «ЭСФ» внутри номера накладной — не номер ЭСФ
    [InlineData("№ ПР-2026-000123; без ЭСФ: поставщик на патенте")]         // не в начале части комментария
    public void Esf_word_inside_other_text_is_not_taken_for_a_number(string comment) =>
        DocumentNumberingCore.EsfFromText(comment).Should().BeEmpty();

    [Theory]
    [InlineData("  АБ-0000123456789  ", "АБ-0000123456789")]             // кириллица и пробелы по краям
    [InlineData("0123 4567 8901", "0123 4567 8901")]
    [InlineData("12\t\t34\n56", "12 34 56")]                               // табуляция и перевод строки — один пробел
    public void Typed_numbers_of_any_form_survive_the_round_trip(string typed, string expected)
    {
        var esf = DocumentNumberingCore.NormalizeEsf(typed);
        esf.Should().Be(expected);
        var comment = "№ ПР-2026-000007; накладная поставщика: 45; ЭСФ №: " + esf + "; принято офлайн 20.11.2026 14:05";
        DocumentNumberingCore.EsfFromText(comment).Should().Be(expected, "что записано в комментарий, то и читается, даже если за ним идут другие части");
        DocumentNumberingCore.SupplierDocFromText(comment).Number.Should().Be("45");
    }

    [Fact]
    public void Long_number_is_cut_at_40_and_still_found_by_its_part()
    {
        var esf = DocumentNumberingCore.NormalizeEsf(new string('7', 30) + "-ABCDEFGHIJKLMN");
        esf.Length.Should().Be(40);
        DocumentNumberingCore.EsfFromText("ЭСФ №: " + esf).Should().Be(esf);
        DocumentNumberingCore.EsfMatches(esf, "777-abc").Should().BeTrue();
    }

    [Fact]
    public void Search_over_two_thousand_receipts_finds_every_receipt_with_the_same_number_quickly()
    {
        // одинаковый № ЭСФ у разных поставщиков (бывает: у каждого своя нумерация) — поиск показывает все такие приходы, а не первый
        var receipts = Enumerable.Range(0, 2000).Select(i => (Supplier: "Поставщик " + (i % 37), Esf: i % 500 == 0 ? "0001234" : $"ESF-{i}-X")).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = receipts.Where(r => DocumentNumberingCore.EsfMatches(r.Esf, "0001234")).ToList();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        found.Select(r => r.Supplier).Distinct().Should().HaveCountGreaterThan(1, "номер повторяется у разных поставщиков");
        found.Should().HaveCount(4, "приходы 0, 500, 1000, 1500 с номером 0001234");
        receipts.Count(r => DocumentNumberingCore.EsfMatches(r.Esf, "esf-1999")).Should().Be(1, "поиск по части с буквами и дефисом");
    }

    [Fact]
    public void Esf_and_supplier_invoice_are_added_without_document_numbering()
    {
        // Раньше при сбое нумерации прихода (AddDocumentFieldsAsync → catch) № накладной и № ЭСФ не попадали в приход вовсе.
        var body = new Dictionary<string, object?>();
        var parts = new List<string>();
        var st = new ReceivingPostState { SupplierDocNumber = "45", SupplierDocDate = new DateTime(2026, 11, 20), EsfNumber = "0123456789" };
        PurchaseReceivingService.AddSupplierDocParts(body, st, parts);
        body["esf_number"].Should().Be("0123456789");
        body["document_number"].Should().Be("45");
        var comment = string.Join("; ", parts);
        DocumentNumberingCore.EsfFromText(comment).Should().Be("0123456789");
        DocumentNumberingCore.SupplierDocFromText(comment).Number.Should().Be("45");
    }

    [Fact]
    public void Draft_keeps_the_esf_number()
    {
        var file = new ReceivingDraftFile { EsfNumber = "АБ-0123 456" };
        var back = ReceivingDraftStore.TryParse(ReceivingDraftStore.Serialize(file), out var problem);
        problem.Should().BeNull();
        back!.EsfNumber.Should().Be("АБ-0123 456", "черновик приёмки восстанавливает номер ЭСФ после перезапуска");
    }
}
