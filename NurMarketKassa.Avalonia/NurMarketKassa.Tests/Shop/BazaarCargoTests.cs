using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 («Базар», опт, модуль invoice_cargo, ТЗ Контейнер-базар 3.6): «Накладная A4 + карго». Данные карго лежат в самом документе клиента (Extra документа и строк), печатная форма — колонки «Мест» и «Брутто, кг»
/// и блок «Карго». Формат документа и совместимость с телефоном не меняются (незнакомые поля сохраняются как есть).</summary>
public sealed class BazaarCargoTests
{
    private static ClientDoc Waybill(params (string Name, double Qty, double Price)[] lines)
    {
        var doc = new ClientDoc { Number = "НК-0007", Client = "Ош, Азамат", Phone = "+996700111222", Address = "г. Ош, рынок Жайма" };
        doc.Kind = ClientDocKind.Waybill;
        doc.Date = new DateTime(2026, 11, 21);
        foreach (var (name, qty, price) in lines)
            doc.Lines.Add(new ClientDocLine { Name = name, Unit = "ряд", Qty = qty, Price = price });
        return doc;
    }

    private static ClientDocProfile Seller() => new() { Name = "ИП Базар-Опт", Inn = "12345678901234", Address = "г. Бишкек, Дордой", Phone = "+996555000111", Director = "Иванов И." };

    private static ClientDocRenderer.Input Input(ClientDoc doc, string template = ClientDocRenderer.Classic, string? lang = null)
    {
        doc.Lang = lang ?? doc.Lang ?? "ru";
        return new ClientDocRenderer.Input(doc, Seller(), template, "#1F3A93", null);
    }

    [Fact]
    public void Cargo_Roundtrip_InDocument_AndOffByDefault()
    {
        var doc = Waybill(("Кроссовки Nike", 10, 6000));
        BazaarCargo.Read(doc).Should().Be(CargoInfo.Off);
        BazaarCargo.IsOn(doc).Should().BeFalse("карго не включено — накладная как раньше");

        BazaarCargo.Write(doc, new CargoInfo(true, 12, " Грузовик-экспресс ", "Ош", "GX-12345"));
        var back = BazaarCargo.Read(doc);
        back.Enabled.Should().BeTrue();
        back.Bags.Should().Be(12);
        back.Carrier.Should().Be("Грузовик-экспресс");
        back.City.Should().Be("Ош");
        back.Track.Should().Be("GX-12345");
        BazaarCargo.IsOn(doc).Should().BeTrue();

        var json = JsonSerializer.Serialize(doc, ClientDocsStore.Json);
        var reread = JsonSerializer.Deserialize<ClientDoc>(json, ClientDocsStore.Json)!;
        BazaarCargo.Read(reread).Should().Be(back, "карго переживает запись и чтение документа");

        BazaarCargo.Write(doc, CargoInfo.Off);
        doc.Extra?.ContainsKey(BazaarCargo.DocKey).Should().NotBe(true, "выключено и пусто — поле убирается");
    }

    [Fact]
    public void Cargo_OnlyWaybills_GetCargo()
    {
        var invoice = Waybill(("Товар", 1, 100));
        invoice.Kind = ClientDocKind.Invoice;
        BazaarCargo.Write(invoice, new CargoInfo(true, 0, "", "", ""));
        BazaarCargo.IsOn(invoice).Should().BeFalse("у счёта и КП колонок карго нет");
    }

    [Fact]
    public void Cargo_LinePlacesAndGross_AreStoredPerLine_AndSummed()
    {
        var doc = Waybill(("Кроссовки", 10, 6000), ("Сумки", 4, 2500), ("Носки", 100, 80));
        BazaarCargo.SetLine(doc.Lines[0], 5, 62.5);
        BazaarCargo.SetLine(doc.Lines[1], 2, 14);
        BazaarCargo.Places(doc.Lines[0]).Should().Be(5);
        BazaarCargo.Gross(doc.Lines[0]).Should().Be(62.5);
        BazaarCargo.Places(doc.Lines[2]).Should().Be(0);
        BazaarCargo.TotalPlaces(doc).Should().Be(7);
        BazaarCargo.TotalGross(doc).Should().Be(76.5);

        BazaarCargo.SetLine(doc.Lines[1], 0, 0);
        doc.Lines[1].Extra.Should().BeNull("пустые поля строки не оставляют следа");
        BazaarCargo.TotalPlaces(doc).Should().Be(5);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("12", 12)]
    [InlineData(" 7 ", 7)]
    public void Cargo_ParsePlaces_Accepts(string text, int expected) => BazaarCargo.ParsePlaces(text).Should().Be(expected);

    [Theory]
    [InlineData("-1")]
    [InlineData("1,5")]
    [InlineData("abc")]
    [InlineData("100000")]
    public void Cargo_ParsePlaces_Rejects(string text) => BazaarCargo.ParsePlaces(text).Should().BeNull();

    [Theory]
    [InlineData("", 0)]
    [InlineData("62,5", 62.5)]
    [InlineData("62.5", 62.5)]
    [InlineData("0", 0)]
    public void Cargo_ParseGross_Accepts(string text, double expected) => BazaarCargo.ParseGross(text).Should().Be(expected);

    [Theory]
    [InlineData("-3")]
    [InlineData("kg")]
    public void Cargo_ParseGross_Rejects(string text) => BazaarCargo.ParseGross(text).Should().BeNull();

    [Theory]
    [InlineData(ClientDocRenderer.Classic)]
    [InlineData(ClientDocRenderer.Modern)]
    [InlineData(ClientDocRenderer.Minimal)]
    [InlineData(ClientDocRenderer.Brand)]
    public void Render_WaybillWithCargo_BuildsOneA4Page_AndDiffersFromThePlainOne(string template)
    {
        var plain = Waybill(("Кроссовки Nike", 10, 6000), ("Сумки", 4, 2500), ("Носки", 100, 80));
        var cargo = plain.Clone();
        BazaarCargo.Write(cargo, new CargoInfo(true, 12, "Грузовик-экспресс", "Ош, рынок Жайма", "GX-12345"));
        BazaarCargo.SetLine(cargo.Lines[0], 5, 62.5);
        BazaarCargo.SetLine(cargo.Lines[1], 2, 14);

        var plainPdf = ClientDocRenderer.PdfBytes(Input(plain, template));
        var cargoPdf = ClientDocRenderer.PdfBytes(Input(cargo, template));
        plainPdf.Length.Should().BeGreaterThan(1000);
        cargoPdf.Length.Should().BeGreaterThan(1000);
        cargoPdf.Should().NotEqual(plainPdf, "колонки и блок «Карго» меняют бланк");

        var pages = ClientDocRenderer.PageImages(Input(cargo, template), dpi: 72);
        pages.Should().HaveCount(1, "три строки и блок «Карго» умещаются на одной странице A4");
        ClientDocRenderer.PageImages(Input(plain, template), dpi: 72).Should().HaveCount(1);

        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"bazaar-cargo-waybill-{template}.png"), ClientDocRenderer.PageImages(Input(cargo, template), dpi: 110)[0]);
    }

    [Fact]
    public void Render_Kyrgyz_AndManyLines_StillWork_AndTableHeaderRepeats()
    {
        var doc = Waybill(Enumerable.Range(1, 60).Select(i => ($"Товар для карго номер {i} с длинным названием, чтобы строка переносилась на вторую строку таблицы", 1.0 + i, 100.0 + i)).ToArray());
        BazaarCargo.Write(doc, new CargoInfo(true, 3, "Маршрутка", "Каракол", ""));
        foreach (var (line, i) in doc.Lines.Select((l, i) => (l, i)))
            BazaarCargo.SetLine(line, i % 4 + 1, 10 + i);
        var pages = ClientDocRenderer.PageImages(Input(doc, ClientDocRenderer.Classic, "ky"), dpi: 60);
        pages.Count.Should().BeGreaterThan(1, "длинная накладная идёт на несколько страниц, шапка таблицы повторяется (колонки карго не ломают разбиение)");
    }

    [Fact]
    public void Render_CargoWithEmptyDocument_ShowsEmptyRowAcrossAllColumns()
    {
        var doc = Waybill();
        BazaarCargo.Write(doc, new CargoInfo(true, 0, "", "", ""));
        ClientDocRenderer.PdfBytes(Input(doc)).Length.Should().BeGreaterThan(1000);
    }
}
