using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-07, жалоба владельца «в накладной ставишь закупка 40, продажа 60, а после сохранения программа ставит 20 % и показывает 48 (при печати штрихкода — 60)».
/// Поддельный сервер с поведением Н-04 (старый сервер, ТЗ Умару 27.10): приход и PATCH закупки пересчитывают цену продажи как закупка × (1 + markup_percent / 100),
/// у товара наценка по умолчанию 20 %. Проверяем все сценарии приёмки (новый/существующий товар × с поставщиком/без × прямой вызов/очередь):
/// после проведения на сервере цена продажи = введённая (60), наценка = 50, закупка = 40, а если сервер пересчитал — касса сама вернула цену и сказала об этом.</summary>
[Collection("BackgroundReceivingQueue")] // 2026-11-07: меняет общие статические (PosApp.CatalogApi, признак price_changed) — без параллельных соседей
public sealed class ServerRecalcSalePriceTests
{
    // ------------------------------------------------------------------ поддельный сервер с Н-04

    private sealed class Prod
    {
        public string Id = "";
        public string Barcode = "";
        public double Price, Purchase, Markup = 20, Qty;
    }

    public enum Mode
    {
        /// <summary>Н-04 не исправлен: приход и PATCH закупки пересчитывают цену по наценке товара, price_changed в ответах нет.</summary>
        Old,
        /// <summary>Б-06 выложен: приход и PATCH закупки цену не меняют, в ответах price_changed = false.</summary>
        Fixed,
        /// <summary>Приход пересчитывает цену, а в ответе врёт «price_changed: false» (страховка: проверка после прихода не должна верить флагу вслепую).</summary>
        Lying,
        /// <summary>Создание товара игнорирует присланную цену и ставит закупка × (1 + 20 %) (наценка компании по умолчанию); приход цену не трогает, price_changed = false.</summary>
        CreateRecalcs,
        /// <summary>PATCH товара не слушается: цена из тела прихода/PATCH заменяется на закупка × (1 + markup / 100) всегда (упрямый сервер) — касса должна честно сказать «не удалось».</summary>
        Stubborn,
    }

    private sealed class PriceServer : IDisposable
    {
        public readonly FakeNurCrmServer Fake = new();
        public readonly ConcurrentDictionary<string, Prod> Products = new();
        private readonly Mode _mode;
        private int _seq;

        public PriceServer(Mode mode, params Prod[] existing)
        {
            _mode = mode;
            foreach (var p in existing)
                Products[p.Id] = p;
            var s = Fake.Server;
            s.ResetMappings();
            Fake.StubLogin();
            Fake.StubProfile();
            Fake.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
            s.Given(Request.Create().WithPath("/api/main/products/create-manual/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req => Create(req.Body)));
            s.Given(Request.Create().WithPath("/api/main/products/warehouse-barcode/*").UsingMethod("GET")).RespondWith(Response.Create().WithCallback(_ => Reply(404, "{\"detail\":\"not found\"}")));
            s.Given(Request.Create().WithPath("/api/main/products/*").UsingMethod("GET")).AtPriority(9).RespondWith(Response.Create().WithCallback(req => Get(Last(req.Path))));
            s.Given(Request.Create().WithPath("/api/main/products/*").UsingMethod("PATCH")).AtPriority(9).RespondWith(Response.Create().WithCallback(req => Patch(Last(req.Path), req.Body)));
            s.Given(Request.Create().WithPath("/api/main/suppliers/*/receipt/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req => Receipt(req.Body)));
        }

        private static string Last(string path) => path.TrimEnd('/').Split('/')[^1];

        private static WireMock.ResponseMessage Reply(int status, string json) => new()
        {
            StatusCode = status,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = json },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };

        private static string N(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private string Card(Prod p) => "{\"id\":\"" + p.Id + "\",\"barcode\":\"" + p.Barcode + "\",\"name\":\"Товар\",\"quantity\":\"" + N(p.Qty) + "\",\"price\":\"" + N(p.Price)
                                       + "\",\"purchase_price\":\"" + N(p.Purchase) + "\",\"markup_percent\":\"" + N(p.Markup) + "\"}";

        private WireMock.ResponseMessage Get(string id) => Products.TryGetValue(id, out var p) ? Reply(200, Card(p)) : Reply(404, "{\"detail\":\"not found\"}");

        private static double? D(JsonNode? n) => n is null ? null : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        private WireMock.ResponseMessage Create(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            var p = new Prod
            {
                Id = "new-" + Interlocked.Increment(ref _seq),
                Barcode = j["barcode"]?.ToString() ?? "",
                Price = D(j["price"]) ?? 0,
                Purchase = D(j["purchase_price"]) ?? 0,
                Qty = D(j["quantity"]) ?? 0,
                Markup = _mode == Mode.CreateRecalcs ? 20 : D(j["markup_percent"]) ?? 20,
            };
            if (_mode == Mode.CreateRecalcs)
                p.Price = Math.Ceiling(p.Purchase * 1.2); // создание ставит «закупка + 20 %» вместо введённой цены
            Products[p.Id] = p;
            return Reply(201, Card(p));
        }

        private WireMock.ResponseMessage Patch(string id, string? body)
        {
            if (!Products.TryGetValue(id, out var p))
                return Reply(404, "{}");
            var j = JsonNode.Parse(body ?? "{}")!;
            var priceBefore = p.Price;
            var hasPrice = j["price"] is not null;
            var markBefore = p.Markup;
            if (D(j["markup_percent"]) is { } mk && _mode != Mode.Stubborn)
                p.Markup = mk;
            if (D(j["price"]) is { } pr)
                p.Price = pr;
            if (D(j["quantity"]) is { } q)
                p.Qty = q;
            if (D(j["purchase_price"]) is { } pp)
            {
                var changed = Math.Abs(pp - p.Purchase) > 1e-9;
                p.Purchase = pp;
                // Н-04: смена закупки пересчитывает цену по наценке товара, если цену в этом же запросе не прислали.
                if (changed && _mode is not (Mode.Fixed or Mode.CreateRecalcs) && !hasPrice)
                    p.Price = Math.Ceiling(p.Purchase * (1 + p.Markup / 100));
            }
            if (_mode == Mode.Stubborn && hasPrice)
                p.Price = Math.Ceiling(p.Purchase * (1 + markBefore / 100)); // упрямый сервер: старая наценка важнее присланной цены
            return Reply(200, Card(p).TrimEnd('}') + (_mode == Mode.Old ? "" : ",\"price_changed\":" + (Math.Abs(p.Price - priceBefore) > 1e-9 ? "true" : "false")) + "}");
        }

        private WireMock.ResponseMessage Receipt(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            var anyChanged = false;
            foreach (var item in j["items"]!.AsArray())
            {
                if (!Products.TryGetValue(item!["product"]!.ToString(), out var p))
                    return Reply(400, "{\"detail\":\"product\"}");
                var priceBefore = p.Price;
                p.Qty += D(item["qty"]) ?? 0;
                var pp = D(item["purchase_price"]) ?? p.Purchase;
                var changed = Math.Abs(pp - p.Purchase) > 1e-9;
                p.Purchase = pp;
                if (changed && _mode is not (Mode.Fixed or Mode.CreateRecalcs))
                    p.Price = Math.Ceiling(p.Purchase * (1 + p.Markup / 100)); // Н-04: приход с новой закупкой пересчитывает цену продажи
                anyChanged |= Math.Abs(p.Price - priceBefore) > 1e-9;
            }
            var flag = _mode switch
            {
                Mode.Old => "",
                _ => ",\"price_changed\":false",
            };
            _ = anyChanged;
            return Reply(201, "{\"id\":\"r-" + Interlocked.Increment(ref _seq) + "\",\"number_display\":\"ПР-1\",\"total_amount\":\"" + N(j["items"]!.AsArray().Sum(i => (D(i!["qty"]) ?? 0) * (D(i["purchase_price"]) ?? 0))) + "\"" + flag + "}");
        }

        public int Patches(string id) => Fake.Server.LogEntries.Count(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path == $"/api/main/products/{id}/");

        public void Dispose() => Fake.Dispose();
    }

    private static async Task<IDisposable> UseApiAsync(PriceServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return new Restorer(() => PosApp.CatalogApi = previous);
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    // ------------------------------------------------------------------ строки приёмки

    /// <summary>Существующий товар: на сервере закупка/цена до прихода, в строке введено закупка 40, продажа 60.</summary>
    private static ReceivingLineVm ExistingLine(string id, double cardPurchase, double cardSale, double purchase = 40, double sale = 60)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = cardSale, OriginalPurchasePrice = cardPurchase, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 3;
        l.PurchasePrice = purchase;
        l.SalePrice = sale;
        return l;
    }

    private static ReceivingLineVm NewLine(string barcode, double purchase = 40, double sale = 60) =>
        new() { Barcode = barcode, ProductName = "Новый " + barcode, Source = ReceivingSource.Unknown, Quantity = 3, PurchasePrice = purchase, SalePrice = sale };

    private static readonly PurchaseReceivingService.Supplier Supplier = new("s1", "Поставщик", null, null);

    private static ReceivingPostState? State(bool queue)
    {
        if (!queue)
            return null;
        var st = new ReceivingPostState();
        st.KeyOf = vm => vm.ProductId ?? vm.Barcode;
        return st;
    }

    private static async Task<(PurchaseReceivingService.PostResult Result, PriceServer Server)> RunAsync(Mode mode, bool withSupplier, bool queue, Func<PriceServer, ReceivingLineVm> line, params Prod[] existing)
    {
        ServerOutageMonitor.ResetForTests(); // ответы 500/таймауты соседних тестов объявляют «аварию» сервера
        var server = new PriceServer(mode, existing);
        using var restore = await UseApiAsync(server);
        var vm = line(server);
        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { vm }, withSupplier ? Supplier : null, paidNow: true, "тест", default, null, State(queue));
        return (result, server);
    }

    private static void ExpectSixty(PriceServer server, string id)
    {
        var p = server.Products[id];
        p.Price.Should().Be(60, "цена продажи после прихода — введённая, а не «закупка × 20 %» (48)");
        p.Purchase.Should().Be(40);
        p.Markup.Should().BeApproximately(50, 0.01, "наценка товара согласована с ценами: (60 / 40 − 1) × 100");
    }

    // ------------------------------------------------------------------ сценарии: каждый должен дать 60 / 50 / 40

    private static readonly Mode[] HonestModes = { Mode.Old, Mode.Fixed, Mode.Lying, Mode.CreateRecalcs };

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var mode in HonestModes)
            foreach (var latched in new[] { false, true })
                foreach (var supplier in new[] { true, false })
                    foreach (var queue in new[] { false, true })
                        foreach (var scenario in new[] { "new", "existing-changed-sale", "existing-same-sale", "existing-same-sale-markup-stale" })
                            yield return new object[] { mode, latched, supplier, queue, scenario };
    }

    /// <summary>Строка таблицы: введено закупка 40, продажа 60; сервер ведёт себя по режиму; «залипший» признак price_changed — как в рабочей программе после первого же ответа сервера.</summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Entered_sale_price_survives_posting(Mode mode, bool latched, bool withSupplier, bool queue, string scenario)
    {
        ServerPriceContract.ResetForTests();
        if (latched)
            ServerPriceContract.Observe(JsonDocument.Parse("{\"price_changed\":false}").RootElement);
        try
        {
            var existing = scenario == "new" ? Array.Empty<Prod>() : new[] { new Prod { Id = "pe", Barcode = "4600000000100", Price = scenario == "existing-changed-sale" ? 50 : 60, Purchase = 30, Markup = scenario == "existing-same-sale-markup-stale" ? 20 : 100, Qty = 10 } };
            var (result, server) = await RunAsync(mode, withSupplier, queue, _ => scenario switch
            {
                "new" => NewLine("4600000000200"),
                "existing-changed-sale" => ExistingLine("pe", cardPurchase: 30, cardSale: 50),
                _ => ExistingLine("pe", cardPurchase: 30, cardSale: 60), // цену не трогали, закупка выросла 30 → 40
            }, existing);
            using var _ = server;

            var id = scenario == "new" ? server.Products.Keys.Single(k => k.StartsWith("new-")) : "pe";
            ExpectSixty(server, id);
            result.PostedLines.Should().HaveCount(1);
            server.Products[id].Qty.Should().Be(scenario == "new" ? 3 : 13, "остаток прибавился ровно один раз");
        }
        finally
        {
            ServerPriceContract.ResetForTests();
        }
    }

    [Fact]
    public async Task Server_that_ignores_the_price_on_create_is_corrected_and_the_owner_is_told()
    {
        ServerPriceContract.ResetForTests();
        var (result, server) = await RunAsync(Mode.CreateRecalcs, withSupplier: true, queue: false, _ => NewLine("4600000000300"));
        using var _ = server;
        var id = server.Products.Keys.Single();
        ExpectSixty(server, id);
        result.Errors.Should().ContainSingle(e => e.Contains("сервер изменил цену продажи с 60 на 48") && e.Contains("исправлено"), "владелец видит: сервер поставил 48, программа вернула 60");
    }

    [Fact]
    public async Task Stubborn_server_gets_an_honest_warning_instead_of_silence()
    {
        ServerPriceContract.ResetForTests();
        var (result, server) = await RunAsync(Mode.Stubborn, withSupplier: false, queue: false, _ => ExistingLine("pe", 30, 50),
            new Prod { Id = "pe", Barcode = "4600000000100", Price = 50, Purchase = 30, Markup = 20, Qty = 10 });
        using var _ = server;
        server.Products["pe"].Price.Should().Be(48, "сервер упрямо пересчитал — это не скрывается");
        result.Errors.Should().Contain(e => e.Contains("сервер изменил цену продажи с 60 на 48") && e.Contains("не удалось"), "предупреждение: вернуть не удалось, поправьте в карточке");
        result.PostedLines.Should().HaveCount(1, "остаток при этом принят");
    }

    [Fact]
    public async Task Price_check_does_not_hit_the_server_when_everything_is_fine()
    {
        ServerPriceContract.ResetForTests();
        ServerPriceContract.Observe(JsonDocument.Parse("{\"price_changed\":false}").RootElement);
        var (_, server) = await RunAsync(Mode.Fixed, withSupplier: true, queue: false, _ => ExistingLine("pe", 30, 50, purchase: 30, sale: 50),
            new Prod { Id = "pe", Barcode = "4600000000100", Price = 50, Purchase = 30, Markup = 20, Qty = 10 });
        using var _s = server;
        server.Fake.Server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/api/main/products/")).Should().Be(0, "ничего не менялось — лишних запросов к товарам нет");
        ServerPriceContract.ResetForTests();
    }
}
