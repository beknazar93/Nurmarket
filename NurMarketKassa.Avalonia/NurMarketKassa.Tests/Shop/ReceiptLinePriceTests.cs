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

/// <summary>2026-11-07 (живой тест перед 1.17.81, Б-П1): в накладной ПР у существующего товара стояли цена и наценка по СТАРОЙ карточке (закупка 40 × 1,2 = 48 / 20 %, хотя введено 60 / 50 %):
/// касса слала в строку прихода только закупку, а введённая цена уходила в товар отдельным PATCH после прихода. Теперь строка прихода несёт sale_price и markup_percent, а окно накладной
/// при сервере, который строку пересчитал по карточке (Н-04), показывает введённое владельцем из журнала кассы. Поддельный сервер — WireMock; журнал и база — во временной папке test-safe.ps1.</summary>
[Collection("BackgroundReceivingQueue")] // меняет общий PosApp.CatalogApi
public sealed class ReceiptLinePriceTests
{
    /// <summary>Stores — сервер хранит в строке прихода то, что прислали; RecalcsByCard — Н-04: игнорирует поля строки и считает по карточке товара (закупка × (1 + наценка карточки)).</summary>
    public enum Mode { Stores, RecalcsByCard }

    private sealed class Prod
    {
        public string Id = "";
        public double Price, Purchase, Markup = 20, Qty;
    }

    private sealed class Server : IDisposable
    {
        public readonly FakeNurCrmServer Fake = new();
        public readonly ConcurrentDictionary<string, Prod> Products = new();
        public JsonNode? LastBody;
        public JsonNode? StoredReceipt;
        public int Version = 1;
        private readonly Mode _mode;

        public Server(Mode mode, Prod product)
        {
            _mode = mode;
            Products[product.Id] = product;
            var s = Fake.Server;
            s.ResetMappings();
            Fake.StubLogin();
            Fake.StubProfile();
            Fake.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
            s.Given(Request.Create().WithPath("/api/main/products/*").UsingMethod("GET")).AtPriority(9).RespondWith(Response.Create().WithCallback(req => Get(Last(req.Path))));
            s.Given(Request.Create().WithPath("/api/main/products/*").UsingMethod("PATCH")).AtPriority(9).RespondWith(Response.Create().WithCallback(req => Patch(Last(req.Path), req.Body)));
            s.Given(Request.Create().WithPath("/api/main/suppliers/*/receipt/").UsingMethod("POST")).RespondWith(Response.Create().WithCallback(req => Receipt(req.Body)));
            s.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r-1/").UsingMethod("GET")).AtPriority(1).RespondWith(Response.Create().WithCallback(_ => Reply(200, ReceiptJson())));
        }

        private static string Last(string path) => path.TrimEnd('/').Split('/')[^1];

        private static WireMock.ResponseMessage Reply(int status, string json) => new()
        {
            StatusCode = status,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = json },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };

        private static string N(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private static double? D(JsonNode? n) => n is null ? null : double.TryParse(n.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        private string Card(Prod p) => "{\"id\":\"" + p.Id + "\",\"barcode\":\"4600000000100\",\"name\":\"Товар\",\"quantity\":\"" + N(p.Qty) + "\",\"price\":\"" + N(p.Price)
                                       + "\",\"purchase_price\":\"" + N(p.Purchase) + "\",\"markup_percent\":\"" + N(p.Markup) + "\",\"price_changed\":false}";

        private WireMock.ResponseMessage Get(string id) => Products.TryGetValue(id, out var p) ? Reply(200, Card(p)) : Reply(404, "{}");

        private WireMock.ResponseMessage Patch(string id, string? body)
        {
            if (!Products.TryGetValue(id, out var p))
                return Reply(404, "{}");
            var j = JsonNode.Parse(body ?? "{}")!;
            if (D(j["price"]) is { } pr) p.Price = pr;
            if (D(j["markup_percent"]) is { } mk) p.Markup = mk;
            if (D(j["purchase_price"]) is { } pp) p.Purchase = pp;
            return Reply(200, Card(p));
        }

        private WireMock.ResponseMessage Receipt(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            LastBody = j;
            var items = new JsonArray();
            foreach (var item in j["items"]!.AsArray())
            {
                var p = Products[item!["product"]!.ToString()];
                var purchase = D(item["purchase_price"]) ?? p.Purchase;
                var previous = p.Price;
                double sale, markup;
                if (_mode == Mode.Stores && D(item["sale_price"]) is { } sent)
                {
                    sale = sent;
                    markup = D(item["markup_percent"]) ?? 0;
                }
                else
                {
                    // Н-04: сервер игнорирует поля строки и пересчитывает от закупки по наценке карточки товара
                    markup = p.Markup;
                    sale = Math.Ceiling(purchase * (1 + markup / 100));
                }

                p.Qty += D(item["qty"]) ?? 0;
                p.Purchase = purchase;
                items.Add(new JsonObject
                {
                    ["id"] = "li-" + p.Id, ["product_id"] = p.Id, ["product_name"] = "Товар", ["qty"] = D(item["qty"]) ?? 0, ["purchase_price"] = purchase,
                    ["sale_price"] = sale, ["markup_percent"] = markup, ["previous_sale_price"] = previous,
                });
            }

            StoredReceipt = new JsonObject { ["id"] = "r-1", ["number_display"] = "ПР-1", ["supplier_id"] = "s1", ["supplier_name"] = "Поставщик", ["total_amount"] = "120", ["version"] = Version, ["items"] = items };
            return Reply(201, StoredReceipt.ToJsonString());
        }

        public string ReceiptJson()
        {
            var copy = JsonNode.Parse(StoredReceipt!.ToJsonString())!;
            copy["version"] = Version;
            return copy.ToJsonString();
        }

        public void Dispose() => Fake.Dispose();
    }

    private static readonly PurchaseReceivingService.Supplier Supplier = new("s1", "Поставщик", null, null);

    private static async Task<(PurchaseReceivingService.PostResult Result, Server Srv, SupplierReceipt Receipt)> RunAsync(Mode mode)
    {
        ServerOutageMonitor.ResetForTests();
        ServerPriceContract.ResetForTests();
        // Товар: закупка 30, наценка в карточке 20 %, цена 36; в приёмке введено закупка 40, продажа 60 (наценка 50 %).
        var srv = new Server(mode, new Prod { Id = "pe", Price = 36, Purchase = 30, Markup = 20, Qty = 10 });
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = srv.Fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            var line = new ReceivingLineVm { OriginalSalePrice = 36, OriginalPurchasePrice = 30, ProductName = "Товар pe", ProductId = "pe", Source = ReceivingSource.Warehouse, Quantity = 3, PurchasePrice = 40, SalePrice = 60 };
            var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, paidNow: true, "тест");
            result.ReceiptId.Should().Be("r-1");
            var receipt = await new SupplierApiService(api).GetReceiptAsync("r-1");
            return (result, srv, receipt);
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }

    [Fact]
    public async Task Receipt_line_carries_the_entered_sale_price_and_markup()
    {
        var (_, srv, receipt) = await RunAsync(Mode.Stores);
        using var _s = srv;

        var item = srv.LastBody!["items"]![0]!;
        ((double)item["sale_price"]!).Should().Be(60, "в строке прихода — цена, введённая владельцем");
        ((double)item["markup_percent"]!).Should().Be(50, "и наценка от закупки по накладной: (60 / 40 − 1) × 100");
        ((double)item["purchase_price"]!).Should().Be(40);

        // Накладная на сервере (строка прихода) хранит то же, что введено, — окно показывает серверное значение без поправок.
        var line = receipt.Lines.Single();
        line.SalePrice.Should().Be(60);
        line.MarkupPercent.Should().Be(50);
        ReceiptMarkupLog.Pick(line, receipt.Version, ReceiptMarkupLog.Line(receipt.Id, "pe"))!.Sale.Should().Be(60);
        srv.Products["pe"].Price.Should().Be(60, "цена товара после прихода — введённая");
    }

    [Fact]
    public async Task Server_that_recalcs_the_line_by_card_still_shows_the_entered_price_in_the_invoice()
    {
        var (_, srv, receipt) = await RunAsync(Mode.RecalcsByCard); // Н-04: строка прихода на сервере = 48 / 20 % (закупка 40 × 1,2)
        using var _s = srv;

        var line = receipt.Lines.Single();
        line.SalePrice.Should().Be(48, "эмуляция Н-04: сервер записал в строку цену по карточке");
        line.MarkupPercent.Should().Be(20);
        // но тело прихода всё равно несло введённое
        ((double)srv.LastBody!["items"]![0]!["sale_price"]!).Should().Be(60);

        // Окно накладной (PurchaseMarkupTable.Rows / реестр) берёт строку через Pick: журнал кассы помнит введённое — 60 и 50 %.
        var picked = ReceiptMarkupLog.Pick(line, receipt.Version, ReceiptMarkupLog.Line(receipt.Id, "pe"))!;
        picked.Sale.Should().Be(60, "накладная показывает введённую цену, а не 48 по карточке");
        picked.MarkupPercent.Should().Be(50);
        ReceiptMarkupLog.View(line.PurchasePrice, picked, null).Below.Should().BeFalse("и пометки «ниже закупки» нет");
    }

    [Fact]
    public void Edited_invoice_or_another_till_keeps_the_server_values()
    {
        var server = new SupplierReceiptLine("l1", "pe", "Товар", "", 3, 40, 0, "шт", 20, 48, 48);
        var journal = new ReceiptMarkupLine { ProductId = "pe", Purchase = 40, Sale = 60, MarkupPercent = 50 };

        ReceiptMarkupLog.Pick(server, 2, journal)!.Sale.Should().Be(48, "накладную правили (версия 2) — сервер источник истины, журнал мог устареть");
        ReceiptMarkupLog.Pick(server, 1, null)!.Sale.Should().Be(48, "журнала нет (приход с сайта / другой кассы) — серверное значение");
        ReceiptMarkupLog.Pick(server with { PurchasePrice = 41 }, 1, journal)!.Sale.Should().Be(48, "закупка в журнале другая — это не тот приход");
        ReceiptMarkupLog.Pick(server with { SalePrice = 60, MarkupPercent = 50 }, 1, journal)!.Sale.Should().Be(60, "значения совпали");
        ReceiptMarkupLog.Pick(server with { SalePrice = null, MarkupPercent = null }, 1, journal)!.Sale.Should().Be(60, "сервер цену не отдаёт — журнал, как раньше");
        ReceiptMarkupLog.Pick(server with { SalePrice = null }, 1, null).Should().BeNull();
    }

    [Fact]
    public async Task Line_without_a_sale_price_sends_no_price_fields()
    {
        ServerOutageMonitor.ResetForTests();
        var srv = new Server(Mode.Stores, new Prod { Id = "pe", Price = 36, Purchase = 30, Markup = 20, Qty = 10 });
        using var _s = srv;
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = srv.Fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            // «Оставить без цены»: цена продажи 0 — в строку прихода она не идёт, цена товара не трогается.
            var line = new ReceivingLineVm { OriginalSalePrice = 36, OriginalPurchasePrice = 30, ProductName = "Товар pe", ProductId = "pe", Source = ReceivingSource.Warehouse, Quantity = 1, PurchasePrice = 30, SalePrice = 0 };
            await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Supplier, paidNow: true, "тест");
            var item = srv.LastBody!["items"]![0]!;
            item["sale_price"].Should().BeNull();
            item["markup_percent"].Should().BeNull();
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }
}
