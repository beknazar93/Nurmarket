using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ3): «Счёт из этого чека» — суммы строк и итог документа равны чеку до тыйына,
/// скидка на чек становится скидкой документа, старые документы открываются.</summary>
public sealed class ClientDocFromReceiptTests
{
    private static ClientDoc NewInvoice() => new() { Kind = ClientDocKind.Invoice, Number = "T-1" };

    private static decimal D(double v) => (decimal)Math.Round(v, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public void Line_sums_and_total_equal_the_receipt_to_the_tyiyn()
    {
        var doc = NewInvoice();
        var lines = new[]
        {
            new ReceiptDocLine("a", "Тетрадь 12 л", "шт", 10, 8.50, 85.00),
            new ReceiptDocLine("b", "Ручка синяя", "шт", 3, 33.34, 100.00), // 3 × 33,33 ≠ 100,00 — цена остаётся точной
            new ReceiptDocLine("c", "Папка", "уп", 2, 120.00, 216.00),      // скидка строки 10 %
        };
        ClientDocFromReceipt.Fill(doc, lines, 401.00);

        doc.Lines.Select(l => D(l.Sum)).Should().Equal(85.00m, 100.00m, 216.00m);
        D(doc.Subtotal).Should().Be(401.00m);
        D(doc.Total).Should().Be(401.00m);
        doc.Discount.Should().Be(0);
        doc.Lines[2].ListPrice.Should().Be(120.00);
        doc.Lines[2].DiscountPercent.Should().Be(10.0);
        doc.Lines[0].ListPrice.Should().BeNull("без скидки строки цены до скидки нет");
    }

    [Fact]
    public void Receipt_discount_becomes_document_discount()
    {
        var doc = NewInvoice();
        ClientDocFromReceipt.Fill(doc, new[] { new ReceiptDocLine("a", "Бумага А4", "уп", 5, 300.00, 1500.00) }, 1350.00);

        D(doc.Subtotal).Should().Be(1500.00m);
        D(doc.Discount).Should().Be(150.00m);
        D(doc.Total).Should().Be(1350.00m);
    }

    [Fact]
    public void Random_receipts_always_match_the_total_to_the_tyiyn()
    {
        var rnd = new Random(20261112);
        for (var n = 0; n < 1000; n++)
        {
            var lines = new List<ReceiptDocLine>();
            var count = rnd.Next(1, 12);
            decimal sum = 0;
            for (var i = 0; i < count; i++)
            {
                var qty = rnd.Next(1, 40);
                var unit = rnd.Next(100, 90000) / 100m;
                var gross = unit * qty;
                var net = rnd.Next(0, 3) == 0 ? Math.Round(gross * (100 - rnd.Next(1, 30)) / 100m, 2) : gross;
                lines.Add(new ReceiptDocLine("p" + i, "Товар " + i, "шт", qty, (double)unit, (double)net));
                sum += net;
            }
            var orderDiscount = rnd.Next(0, 4) == 0 ? Math.Round(sum * rnd.Next(1, 20) / 100m, 2) : 0m;
            var total = sum - orderDiscount;
            var doc = NewInvoice();

            ClientDocFromReceipt.Fill(doc, lines, (double)total);

            D(doc.Total).Should().Be(total, $"чек №{n}");
            for (var i = 0; i < lines.Count; i++)
                D(doc.Lines[i].Sum).Should().Be((decimal)Math.Round(lines[i].LineTotal, 2), $"чек №{n}, строка {i}");
        }
    }

    [Fact]
    public void Zero_quantity_lines_are_skipped_and_unit_defaults_to_pieces()
    {
        var doc = NewInvoice();
        ClientDocFromReceipt.Fill(doc, new[] { new ReceiptDocLine("a", "A", "", 0, 10, 0), new ReceiptDocLine("b", "B", "", 2, 10, 20) }, 20);

        doc.Lines.Should().ContainSingle();
        doc.Lines[0].Unit.Should().Be("шт");
    }

    [Fact]
    public void Document_built_from_receipt_round_trips_and_old_documents_still_open()
    {
        var doc = NewInvoice();
        doc.Client = "ОсОО Школа № 1";
        doc.Inn = "01234567890123";
        ClientDocFromReceipt.Fill(doc, new[] { new ReceiptDocLine("a", "Тетрадь", "шт", 10, 8.5, 85) }, 85);
        var back = JsonSerializer.Deserialize<ClientDoc>(JsonSerializer.Serialize(doc, ClientDocsStore.Json), ClientDocsStore.Json)!;
        back.Kind.Should().Be(ClientDocKind.Invoice);
        back.Inn.Should().Be("01234567890123");
        D(back.Total).Should().Be(85.00m);

        // документ старого вида (без kind, clientId, vat и т. д.) открывается как раньше
        var old = JsonSerializer.Deserialize<ClientDoc>("{\"id\":\"1\",\"number\":\"7\",\"quote\":false,\"client\":\"Иванов\",\"lines\":[{\"p\":\"x\",\"n\":\"Ручка\",\"u\":\"шт\",\"q\":2,\"pr\":15}],\"discount\":0,\"status\":\"issued\"}", ClientDocsStore.Json)!;
        old.Kind.Should().Be(ClientDocKind.Invoice);
        D(old.Total).Should().Be(30.00m);
    }
}
