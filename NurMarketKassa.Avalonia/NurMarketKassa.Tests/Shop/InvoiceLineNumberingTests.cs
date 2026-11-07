using System.Collections.ObjectModel;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.InvoiceImport;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-07 (ТЗ «NurCRM — Порядковая нумерация товаров в накладной»): колонка «№» — порядок ДОБАВЛЕНИЯ товаров (ReceivingLineVm.LineNo): присваивается при добавлении,
/// удаление уплотняет номера без пропусков, правка количества/цены номер не меняет, повтор товара — своя строка со своим номером. Только показ: в тело прихода номер не уходит,
/// а строки уходят в порядке добавления, поэтому открытая позже накладная показывает те же номера. Печатные формы (Word) начинаются с колонки «№».</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class InvoiceLineNumberingTests
{
    private static ReceivingLineVm Line(string id, double qty = 1, double purchase = 10, double sale = 15)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = sale, OriginalPurchasePrice = purchase, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = qty;
        l.PurchasePrice = purchase;
        l.SalePrice = sale;
        return l;
    }

    /// <summary>Список приёмки склада: CollectionChanged → номера (как в WarehouseViewModel); новая строка — сверху.</summary>
    private static ObservableCollection<ReceivingLineVm> WarehouseList()
    {
        var list = new ObservableCollection<ReceivingLineVm>();
        list.CollectionChanged += (_, e) => ReceivingListOrder.OnListChanged(list, e);
        return list;
    }

    private static string[] Numbers(IEnumerable<ReceivingLineVm> lines) => lines.Select(l => $"{l.ProductId}={l.LineNo}").ToArray();

    [Fact]
    public void Lines_are_numbered_in_the_order_they_were_added()
    {
        var lines = new ObservableCollection<ReceivingLineVm>();
        foreach (var id in new[] { "A", "B", "C" })
            ReceivingListOrder.Place(lines, Line(id)); // новая строка встаёт наверх

        lines.Select(l => l.ProductId).Should().Equal(new[] { "C", "B", "A" }, "порядок на экране прежний: новая сверху");
        Numbers(lines).Should().Equal("C=3", "B=2", "A=1");
    }

    [Fact]
    public void Removing_from_the_middle_renumbers_without_gaps()
    {
        var lines = WarehouseList();
        foreach (var id in new[] { "A", "B", "C", "D" })
            lines.Insert(0, Line(id));
        Numbers(lines).Should().Equal("D=4", "C=3", "B=2", "A=1");

        lines.Remove(lines.First(l => l.ProductId == "B"));

        Numbers(lines).Should().Equal("D=3", "C=2", "A=1");

        lines.Remove(lines.First(l => l.ProductId == "D"));
        Numbers(lines).Should().Equal("C=2", "A=1");
        lines.Insert(0, Line("E"));
        Numbers(lines).Should().Equal("E=3", "C=2", "A=1");
    }

    [Fact]
    public void The_same_product_in_two_lines_gets_two_numbers()
    {
        var lines = WarehouseList();
        lines.Insert(0, Line("A", purchase: 10));
        lines.Insert(0, Line("A", purchase: 12)); // та же позиция другой закупкой — отдельная строка

        lines.Select(l => l.LineNo).Should().Equal(2, 1);
    }

    [Fact]
    public void Editing_quantity_and_prices_does_not_change_the_number()
    {
        var lines = WarehouseList();
        foreach (var id in new[] { "A", "B", "C" })
            lines.Insert(0, Line(id));
        var before = Numbers(lines);

        lines[1].Quantity = 25;
        lines[1].SetPurchaseText("99,5");
        lines[2].SetSaleText("120");
        lines[0].QuantityText = "3,5";
        lines[0].MarkupText = "40";

        Numbers(lines).Should().Equal(before);
    }

    [Fact]
    public void Moving_a_line_to_the_top_keeps_its_number_when_the_list_handler_is_skipped()
    {
        // WarehouseViewModel.MoveReceivingLineToTop (повторный скан): Remove + Insert одной строки под флагом IsReorderingReceiving — обработчик номеров не вызывается.
        var lines = new ObservableCollection<ReceivingLineVm>();
        var reordering = false;
        lines.CollectionChanged += (_, e) =>
        {
            if (!reordering)
                ReceivingListOrder.OnListChanged(lines, e);
        };
        foreach (var id in new[] { "A", "B", "C" })
            lines.Insert(0, Line(id));
        var a = lines.First(l => l.ProductId == "A");

        reordering = true;
        lines.RemoveAt(lines.IndexOf(a));
        lines.Insert(0, a);
        reordering = false;

        lines.Select(l => l.ProductId).Should().Equal("A", "C", "B");
        Numbers(lines).Should().Equal(new[] { "A=1", "C=3", "B=2" }, "номер строки не зависит от места на экране");
    }

    [Fact]
    public void Lines_added_at_the_end_get_the_next_numbers_and_new_ones_continue_after_the_last()
    {
        var lines = new ObservableCollection<ReceivingLineVm> { Line("A"), Line("B") };
        ReceivingListOrder.Renumber(lines); // «по заказу»: строки добавлены в конец, перерисовка присваивает 1, 2
        Numbers(lines).Should().Equal("A=1", "B=2");

        ReceivingListOrder.Place(lines, Line("C")); // потом скан: новая строка сверху, но номер — следующий после последнего
        Numbers(lines).Should().Equal("C=3", "A=1", "B=2");
    }

    [Fact]
    public void Renumber_is_idempotent_and_reports_changes()
    {
        var lines = new List<ReceivingLineVm> { Line("A"), Line("B") };
        ReceivingListOrder.Renumber(lines).Should().BeTrue();
        ReceivingListOrder.Renumber(lines).Should().BeFalse("номера уже без пропусков");
    }

    [Fact]
    public void Lines_go_to_the_receipt_in_added_order()
    {
        var lines = new ObservableCollection<ReceivingLineVm>();
        foreach (var id in new[] { "A", "B", "C" })
            ReceivingListOrder.Place(lines, Line(id));

        ReceivingListOrder.InAddedOrder(lines).Select(l => l.ProductId).Should().Equal("A", "B", "C");
        ReceivingListOrder.InAddedOrder(new[] { Line("X"), Line("Y") }).Select(l => l.ProductId).Should().Equal(new[] { "X", "Y" }, "без номеров порядок как есть");
    }

    [Fact]
    public void The_number_is_display_only_and_is_not_part_of_the_product_data()
    {
        var l = Line("A");
        l.LineNo = 7;
        l.Barcode = "4600000000001";

        l.Barcode.Should().Be("4600000000001", "номер строки не смешивается со штрихкодом");
        l.ProductName.Should().Be("Товар A");
        JobLine.From(l).ToVm().LineNo.Should().Be(0, "в очередь и на диск номер не пишется");
    }

    // ------------------------------------------------------------------ сохранение и повторное открытие

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

    [Fact]
    public async Task Saved_receipt_keeps_the_added_order_and_reopens_with_the_same_numbers()
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-num-1\",\"total_amount\":\"60.00\",\"number_display\":\"ПР-2026-000001\"}");
        foreach (var id in new[] { "pa", "pb", "pc" })
        {
            server.Json("GET", $"/api/main/products/{id}/", 200, "{\"id\":\"" + id + "\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
            server.Json("PATCH", $"/api/main/products/{id}/", 200, "{\"id\":\"" + id + "\"}");
        }
        using var restore = await UseApiAsync(server);

        // На экране новая строка сверху: pc, pb, pa; в приход строки уходят в порядке добавления.
        var shown = new ObservableCollection<ReceivingLineVm>();
        foreach (var id in new[] { "pa", "pb", "pc" })
            ReceivingListOrder.Place(shown, Line(id, qty: 2));
        shown.Select(l => l.ProductId).Should().Equal("pc", "pb", "pa");
        var toPost = ReceivingListOrder.InAddedOrder(shown);

        await PurchaseReceivingService.Instance.PostAsync(toPost, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), false, "тест");

        var post = server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/suppliers/s1/receipt/");
        using var body = JsonDocument.Parse(post.RequestMessage.Body!);
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("product").GetString()).Should().Equal(new[] { "pa", "pb", "pc" }, "порядок строк в теле прихода = порядок добавления («№» 1, 2, 3)");
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.EnumerateObject().Select(p => p.Name).ToArray()).Should()
            .OnlyContain(names => !names.Contains("line_no") && !names.Contains("LineNo"), "номер — только показ, на сервер не уходит");

        // Повторное открытие: сервер вернул строки в том же порядке — номера те же (порядок «как пришёл»).
        server.Json("GET", "/api/main/suppliers/receipts/r-num-1/", 200,
            "{\"id\":\"r-num-1\",\"supplier_id\":\"s1\",\"items\":[" +
            "{\"id\":\"i1\",\"product_id\":\"pa\",\"product_name\":\"Товар pa\",\"qty\":2,\"purchase_price\":10}," +
            "{\"id\":\"i2\",\"product_id\":\"pb\",\"product_name\":\"Товар pb\",\"qty\":2,\"purchase_price\":10}," +
            "{\"id\":\"i3\",\"product_id\":\"pc\",\"product_name\":\"Товар pc\",\"qty\":2,\"purchase_price\":10}]}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var receipt = await new SupplierApiService(api).GetReceiptAsync("r-num-1");

        receipt.Lines.Select(l => l.ProductId).Should().Equal("pa", "pb", "pc");
    }

    [Fact]
    public void A_server_position_field_is_used_when_every_line_has_it_otherwise_the_response_order_stays()
    {
        static List<JsonElement> Items(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        static string[] Ids(List<JsonElement> items) => items.Select(i => i.GetProperty("id").GetString()!).ToArray();

        var withPosition = Items("[{\"id\":\"c\",\"position\":3},{\"id\":\"a\",\"position\":1},{\"id\":\"b\",\"position\":2}]");
        Ids(SupplierApiService.InLineOrder(withPosition)).Should().Equal("a", "b", "c");

        var withLineNo = Items("[{\"id\":\"c\",\"line_no\":2},{\"id\":\"a\",\"line_no\":1}]");
        Ids(SupplierApiService.InLineOrder(withLineNo)).Should().Equal("a", "c");

        var partial = Items("[{\"id\":\"c\",\"position\":3},{\"id\":\"a\"},{\"id\":\"b\",\"position\":2}]");
        Ids(SupplierApiService.InLineOrder(partial)).Should().Equal(new[] { "c", "a", "b" }, "поле порядка есть не у всех — порядок как пришёл");

        var none = Items("[{\"id\":\"z\"},{\"id\":\"y\"}]");
        Ids(SupplierApiService.InLineOrder(none)).Should().Equal("z", "y");
    }

    // ------------------------------------------------------------------ печатная форма (Word): первая колонка «№»

    [Fact]
    public void Invoice_word_document_starts_with_the_number_column_in_added_order()
    {
        var doc = new ClientDoc { Kind = ClientDocKind.Waybill, Number = "ПР-1", Client = "Магазин", Lang = "ru", Template = ClientDocRenderer.Classic };
        doc.Date = new DateTime(2026, 11, 7);
        foreach (var name in new[] { "Хлеб", "Молоко", "Хлеб" }) // повтор товара — отдельная строка со своим номером
        {
            var line = new ClientDocLine { Name = name, Unit = "шт", Qty = 2 };
            line.SetPrice(10, 0);
            doc.Lines.Add(line);
        }
        var input = new ClientDocRenderer.Input(doc, new ClientDocProfile { Name = "Поставщик" }, ClientDocRenderer.Classic, "#F5B800", null);
        var path = Path.Combine(Path.GetTempPath(), "nm-invoice-no-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            ClientDocWord.Save(input, path);

            using var word = WordprocessingDocument.Open(path, false);
            var table = word.MainDocumentPart!.Document.Body!.Descendants<Table>()
                .First(t => t.Elements<TableRow>().First().Elements<TableCell>().First().InnerText == "№");
            var rows = table.Elements<TableRow>().ToList();
            rows[0].Elements<TableCell>().First().InnerText.Should().Be("№", "первый столбец шапки — «№»");
            rows.Skip(1).Take(3).Select(r => r.Elements<TableCell>().First().InnerText).Should().Equal("1", "2", "3");
            rows.Skip(1).Take(3).Select(r => r.Elements<TableCell>().ElementAt(1).InnerText).Should().Equal("Хлеб", "Молоко", "Хлеб");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
        ClientDocRenderer.PdfBytes(input).Should().NotBeEmpty("печатная форма (PDF) строится тем же построителем с первым столбцом «№»");
    }

    // ------------------------------------------------------------------ предпросмотр импортируемой накладной

    [Fact]
    public void Invoice_import_preview_rows_are_numbered_from_one_without_gaps()
    {
        var doc = new InvoiceDocument
        {
            Items =
            [
                new InvoiceItem { SourceRow = 4, Name = "Хлеб белый", Unit = "шт", Quantity = 2, Price = 10 },
                new InvoiceItem { SourceRow = 5, Name = "Молоко", Unit = "шт", Quantity = 1, Price = 50 },
                new InvoiceItem { SourceRow = 6, Name = "Сыр", Unit = "кг", Quantity = 3, Price = 400 },
            ],
        };
        var plan = InvoicePlan.Build(doc, catalog: [], directoryLookup: _ => null);

        plan.Rows.Select(r => r.No).Should().Equal(1, 2, 3);

        plan.Rows.Remove(plan.Rows[1]);

        plan.Rows.Select(r => r.No).Should().Equal(1, 2);
        plan.Rows.Select(r => r.Item.Name).Should().Equal("Хлеб белый", "Сыр");
    }
}
