using System.Text;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.Tests.Invoice;

/// <summary>2026-11-22 (жалоба владельца «не понятно как загружать с накладной»; файл «Штрих Товар NURCRM.xlsx»: штрихкод, наименование, цена, закупочная — без количества): автоопределение столбцов накладной
/// по заголовкам (ru / ky / en / uz), «Закупочная» — цена закупки, а обычная «Цена» рядом — цена продажи; файл без количества — явная отметка «Количество = 1 для всех» (разбор, память «запомнить»,
/// совместимость старых записей); строка заголовков ищется там, где узнаны название/штрихкод и цена, а не просто в самой широкой строке.</summary>
[Collection("InvoiceColumnMemory")]
public sealed class InvoiceColumnDetectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "invoice-columns-" + Guid.NewGuid().ToString("N")[..8]);

    public InvoiceColumnDetectionTests() => InvoiceImportRegistry.DataDirOverride = _dir;

    public void Dispose()
    {
        InvoiceImportRegistry.DataDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static ProductFileTable Csv(params string[] lines)
    {
        var read = ProductFileReader.ReadCsv(Encoding.UTF8.GetBytes(string.Join("\n", lines)), "test.csv");
        read.Ok.Should().BeTrue(read.Error);
        return read.Table!;
    }

    [Fact]
    public void CatalogFile_Headers_BarcodeNamePurchaseAndRetail_AreDetected()
    {
        var m = InvoiceColumnMapping.AutoDetect(["Штрих Код", "Наименование", "Цена", "Закупочная"]);
        m.ColumnOf(InvoiceField.Barcode).Should().Be(0);
        m.ColumnOf(InvoiceField.Name).Should().Be(1);
        m.ColumnOf(InvoiceField.Price).Should().Be(3, "«Закупочная» — цена закупки накладной");
        m.ColumnOf(InvoiceField.Retail).Should().Be(2, "обычная «Цена» рядом с «Закупочной» — цена продажи");
        m.ColumnOf(InvoiceField.Quantity).Should().Be(-1);
        m.IsComplete.Should().BeFalse("количества в файле нет — нужна явная отметка");
        m.QuantityOne = true;
        m.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PurchaseBeforePlainPrice_StillPurchaseIsPriceAndPlainIsRetail()
    {
        var m = InvoiceColumnMapping.AutoDetect(["Наименование", "Закупочная цена", "Цена"]);
        m.ColumnOf(InvoiceField.Price).Should().Be(1);
        m.ColumnOf(InvoiceField.Retail).Should().Be(2);
    }

    [Fact]
    public void ClassicInvoice_SinglePrice_StaysPurchasePrice()
    {
        var m = InvoiceColumnMapping.AutoDetect(["№", "Наименование", "Ед. изм.", "Количество", "Цена", "Сумма"]);
        m.ColumnOf(InvoiceField.Name).Should().Be(1);
        m.ColumnOf(InvoiceField.Unit).Should().Be(2);
        m.ColumnOf(InvoiceField.Quantity).Should().Be(3);
        m.ColumnOf(InvoiceField.Price).Should().Be(4, "единственная «Цена» — цена закупки, как раньше");
        m.ColumnOf(InvoiceField.Sum).Should().Be(5);
        m.ColumnOf(InvoiceField.Retail).Should().Be(-1);
        m.IsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData("Штрих-код", "Аталышы", "Саны", "Баасы")]                    // ky
    [InlineData("EAN", "Product name", "Qty", "Price")]                        // en
    [InlineData("Shtrix-kod", "Nomi", "Miqdor", "Narx")]                        // uz
    [InlineData("ШК", "Товар", "Кол-во", "Цена закупки")]                      // ru, сокращения
    [InlineData("Barcode", "Название", "Количество", "Закупка")]
    public void Synonyms_RuKyEnUz_AreDetected(string barcode, string name, string qty, string price)
    {
        var m = InvoiceColumnMapping.AutoDetect([barcode, name, qty, price]);
        m.ColumnOf(InvoiceField.Barcode).Should().Be(0);
        m.ColumnOf(InvoiceField.Name).Should().Be(1);
        m.ColumnOf(InvoiceField.Quantity).Should().Be(2);
        m.ColumnOf(InvoiceField.Price).Should().Be(3);
        m.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Detect_FileWithoutQuantity_FindsHeaderRowAndAsksForMapping()
    {
        var table = Csv("Список товаров магазина;;;", ";;;", "Штрих Код;Наименование;Цена;Закупочная", ";АЛЬЯНС;0;", "4084500490468;Old spice сухой;177;153.79", "4084500569478;Тайд капсула;480;436.75");
        var d = InvoiceTableParser.Detect(table);
        d.Recognized.Should().BeFalse("без столбца количества заголовок не «узнан» полностью");
        table.Rows[d.HeaderIndex].Cells[1].Should().Be("Наименование", "строка заголовков — та, где узнаны название, штрихкод и цена");
        d.Mapping.ColumnOf(InvoiceField.Barcode).Should().Be(0);
        d.Mapping.ColumnOf(InvoiceField.Name).Should().Be(1);
        d.Mapping.ColumnOf(InvoiceField.Price).Should().Be(3);
        d.Mapping.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Parse_QuantityOne_ReadsItemsWithQuantityOneAndPurchaseAndRetail()
    {
        var table = Csv("Штрих Код;Наименование;Цена;Закупочная", ";Группа без чисел;;", "4084500490468;Old spice сухой;177;153.79", "4084500569478;Тайд капсула;480;436.75");
        var m = InvoiceColumnMapping.AutoDetect(table.Rows[0].Cells);
        m.QuantityOne = true;
        var doc = InvoiceTableParser.Parse(table, 0, m, "test.csv");
        doc.Items.Should().HaveCount(2, "строка-заголовок группы без чисел товаром не становится");
        doc.Items.Should().OnlyContain(i => i.Quantity == 1);
        doc.Items[0].Barcode.Should().Be("4084500490468");
        doc.Items[0].Name.Should().Be("Old spice сухой");
        doc.Items[0].Price.Should().Be(153.79);
        doc.Items[0].RetailPrice.Should().Be(177);
        doc.Items[1].UnitPrice(false).Should().Be(436.75);
    }

    [Fact]
    public void Parse_WithoutQuantityOneFlag_NoQuantityColumn_IsNotComplete()
    {
        var m = InvoiceColumnMapping.AutoDetect(["Штрих Код", "Наименование", "Закупочная"]);
        m.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Memory_QuantityOne_IsSavedAndLoaded_AndOldEntriesStayFalse()
    {
        string[] headers = ["Штрих Код", "Наименование", "Цена", "Закупочная"];
        var m = InvoiceColumnMapping.AutoDetect(headers);
        InvoiceImportRegistry.SaveMapping(headers, m);
        var plain = InvoiceImportRegistry.TryLoadMapping(headers);
        plain.Should().NotBeNull();
        plain!.QuantityOne.Should().BeFalse("запись без отметки (как у старых записей) — QuantityOne выключено");
        plain.IsComplete.Should().BeFalse();

        m.QuantityOne = true;
        InvoiceImportRegistry.SaveMapping(headers, m);
        var loaded = InvoiceImportRegistry.TryLoadMapping(headers);
        loaded!.QuantityOne.Should().BeTrue();
        loaded.IsComplete.Should().BeTrue();
        loaded.ColumnOf(InvoiceField.Price).Should().Be(3);
        loaded.ColumnOf(InvoiceField.Retail).Should().Be(2);
    }

    [Fact]
    public void Memory_QuantityOne_IsNotSavedWhenQuantityColumnChosen()
    {
        string[] headers = ["Штрих Код", "Наименование", "Количество", "Цена"];
        var m = InvoiceColumnMapping.AutoDetect(headers);
        m.QuantityOne = true; // остаток от прежнего выбора, столбец количества потом указали
        InvoiceImportRegistry.SaveMapping(headers, m);
        InvoiceImportRegistry.TryLoadMapping(headers)!.QuantityOne.Should().BeFalse();
    }

    [Fact]
    public void Detect_RemembersQuantityOneForSameHeaders()
    {
        var table = Csv("Штрих Код;Наименование;Цена;Закупочная", "4084500490468;Old spice сухой;177;153.79");
        var first = InvoiceTableParser.Detect(table);
        first.Mapping.IsComplete.Should().BeFalse();
        first.Mapping.QuantityOne = true;
        InvoiceImportRegistry.SaveMapping(table.Rows[first.HeaderIndex].Cells, first.Mapping);

        var again = InvoiceTableParser.Detect(table);
        again.Recognized.Should().BeTrue("те же заголовки уже сопоставляли — берётся запомненное");
        again.Mapping.IsComplete.Should().BeTrue();
        again.Mapping.QuantityOne.Should().BeTrue();
    }
}
