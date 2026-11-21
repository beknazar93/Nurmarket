using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь», как в мобилке — invoice_bind.dart / barcode_bind.dart): штрихкоды в накладных — проверка кода
/// (формат 4–14 цифр, дубль в накладной, владелец в каталоге), куда писать код у товара склада (основной / дополнительный / уже есть), приход пачкой (скан упаковки = N штук),
/// запись на сервер без сети (подмены), XML с несколькими штрихкодами, поиск товара по доп. коду строки. Сервер и каталог не трогаются.</summary>
[Collection("InvoiceBarcodeBinder")]
public sealed class InvoiceBarcodeTests : IDisposable
{
    public InvoiceBarcodeTests()
    {
        InvoiceBarcodeBinder.OwnerOverride = null;
        InvoiceBarcodeBinder.ReadCardOverride = null;
        InvoiceBarcodeBinder.SaveOverride = null;
        InvoiceBarcodeBinder.AddOverride = null;
        InvoiceBarcodeBinder.RemoveOverride = null;
    }

    public void Dispose()
    {
        InvoiceBarcodeBinder.OwnerOverride = null;
        InvoiceBarcodeBinder.ReadCardOverride = null;
        InvoiceBarcodeBinder.SaveOverride = null;
        InvoiceBarcodeBinder.AddOverride = null;
        InvoiceBarcodeBinder.RemoveOverride = null;
    }

    private static CatalogProductTileVm Tile(string id, string title, string barcode, params (string Code, string? Name, double Qty)[] alts) =>
        new(id, title, "10.00 сом", false)
        {
            Unit = "шт",
            Barcode = barcode,
            AlternateBarcodeVariants = alts.Select(a => new AlternateBarcodeVariant { Barcode = a.Code, Name = a.Name, Quantity = a.Qty }).ToList(),
        };

    private static InvoiceCodeRow Row(object key, string name, string? productId, string barcode, params string[] extras) => new(key, name, productId, barcode, extras);

    // ------------------------------------------------------------------ проверка кода

    [Theory]
    [InlineData("123", false)]          // короче 4
    [InlineData("12345678901234", true)] // 14 цифр
    [InlineData("123456789012345", false)] // 15
    [InlineData("48702002A1261", false)] // буква
    [InlineData("4870200221261", true)]
    public void IsValidNew_DigitsFourToFourteen(string code, bool valid) => InvoiceBarcodeRules.IsValidNew(code).Should().Be(valid);

    [Fact]
    public void Check_EmptyAndBadFormat_StopBeforeCatalog()
    {
        InvoiceBarcodeRules.Check("  ", null, "r", [], null).Verdict.Should().Be(InvoiceCodeVerdict.Empty);
        InvoiceBarcodeRules.Check("12", null, "r", [], null).Verdict.Should().Be(InvoiceCodeVerdict.BadFormat);
        InvoiceBarcodeRules.Check("48702002abc", null, "r", [], null).Verdict.Should().Be(InvoiceCodeVerdict.BadFormat);
    }

    [Fact]
    public void Check_DuplicateInsideInvoice_ByMainCodeAndByExtraCode_NamesTheOtherRow()
    {
        var me = new object();
        var other = new object();
        var rows = new[]
        {
            Row(me, "Вода", null, ""),
            Row(other, "Сок яблочный", null, "4870200221261", "4870200221278"),
        };
        var byMain = InvoiceBarcodeRules.Check("4870200221261", null, me, rows, null);
        byMain.Verdict.Should().Be(InvoiceCodeVerdict.DuplicateInInvoice);
        byMain.RowName.Should().Be("Сок яблочный");
        InvoiceBarcodeRules.Check("4870200221278", null, me, rows, null).Verdict.Should().Be(InvoiceCodeVerdict.DuplicateInInvoice);
        // своя же строка с этим кодом — не дубль
        InvoiceBarcodeRules.Check("4870200221261", null, other, rows, null).Verdict.Should().Be(InvoiceCodeVerdict.Free);
    }

    [Fact]
    public void Check_DraftDuplicateComesBeforeCatalog()
    {
        var me = new object();
        var rows = new[] { Row(me, "Вода", null, ""), Row(new object(), "Сок", null, "4870200221261") };
        var owner = Tile("p1", "Чужой товар", "4870200221261");
        InvoiceBarcodeRules.Check("4870200221261", null, me, rows, owner).Verdict.Should().Be(InvoiceCodeVerdict.DuplicateInInvoice);
    }

    [Fact]
    public void Check_CatalogOwner_OtherProductConflicts_OwnProductIsFine()
    {
        var me = new object();
        var owner = Tile("p1", "Вода «Асу»", "4870200221261");
        var conflict = InvoiceBarcodeRules.Check("4870200221261", null, me, [Row(me, "Новая вода", null, "")], owner);
        conflict.Verdict.Should().Be(InvoiceCodeVerdict.OwnedByOther);
        conflict.Owner.Should().BeSameAs(owner);
        InvoiceBarcodeRules.VerdictText(conflict, "4870200221261").Should().Contain("Вода «Асу»");
        // у строки уже этот товар — записывать нечего
        InvoiceBarcodeRules.Check("4870200221261", "p1", me, [Row(me, "Вода", "p1", "")], owner).Verdict.Should().Be(InvoiceCodeVerdict.AlreadyOwn);
        InvoiceBarcodeRules.Check("4870200221261", null, me, [Row(me, "Вода", null, "")], null).Verdict.Should().Be(InvoiceCodeVerdict.Free);
    }

    [Fact]
    public void Same_Upca12AndEan13WithLeadingZero_AreOneCode()
    {
        InvoiceBarcodeRules.Same("762497741537", "0762497741537").Should().BeTrue();
        InvoiceBarcodeRules.Same("762497741537", "0762497741538").Should().BeFalse();
        InvoiceBarcodeRules.OwnerIn([Tile("p1", "Товар", "0762497741537")], "762497741537")!.Id.Should().Be("p1");
    }

    // ------------------------------------------------------------------ куда писать код

    [Fact]
    public void PlanFor_NoCode_Main_HasCode_Alt_AlreadyThere_Already()
    {
        InvoiceBarcodeRules.PlanFor(Tile("p1", "Товар", ""), "4870200221261").Should().Be(InvoiceBindPlan.Main);
        InvoiceBarcodeRules.PlanFor(Tile("p1", "Товар", "4870200221000"), "4870200221261").Should().Be(InvoiceBindPlan.Alt);
        InvoiceBarcodeRules.PlanFor(Tile("p1", "Товар", "4870200221000"), "4870200221000").Should().Be(InvoiceBindPlan.Already);
        InvoiceBarcodeRules.PlanFor(Tile("p1", "Товар", "4870200221000", ("4870200221261", null, 0)), "4870200221261").Should().Be(InvoiceBindPlan.Already);
    }

    [Fact]
    public void MergeVariants_SameCodeReplaced_NewAppended()
    {
        var merged = InvoiceBarcodeRules.MergeVariants([new AlternateBarcodeVariant { Barcode = "111111", Quantity = 6 }], new AlternateBarcodeVariant { Barcode = "111111", Name = "Коробка", Quantity = 12 });
        merged.Should().ContainSingle().Which.Quantity.Should().Be(12);
        InvoiceBarcodeRules.MergeVariants(merged, new AlternateBarcodeVariant { Barcode = "222222" }).Should().HaveCount(2);
    }

    // ------------------------------------------------------------------ приход пачкой

    [Fact]
    public void PackFor_PackCode_GivesQuantityAndNote()
    {
        var tile = Tile("p1", "Саморез", "4600000000011", ("4600000000200", "Коробка", 200), ("4600000000012", "Клубничный", 1));
        var hit = InvoiceBarcodeRules.PackFor(tile, "4600000000200");
        hit.Should().NotBeNull();
        hit!.Quantity.Should().Be(200);
        hit.Note.Should().Contain("200");
        // основной код — одна штука; вариант с N = 1 — не упаковка; неизвестный код — не упаковка
        InvoiceBarcodeRules.PackFor(tile, "4600000000011").Should().BeNull();
        InvoiceBarcodeRules.PackFor(tile, "4600000000012").Should().BeNull();
        InvoiceBarcodeRules.PackFor(tile, "4600000000999").Should().BeNull();
    }

    [Fact]
    public void PackFor_WeighedProductAndAbsurdQuantity_AreNotPacks()
    {
        var weighed = new CatalogProductTileVm("p2", "Мука", "50 сом", true) { Barcode = "4600000000100", AlternateBarcodeVariants = [new AlternateBarcodeVariant { Barcode = "4600000000555", Quantity = 25 }] };
        InvoiceBarcodeRules.PackFor(weighed, "4600000000555").Should().BeNull();
        var huge = Tile("p3", "Гвозди", "4600000000300", ("4600000000301", null, 5_000_000));
        InvoiceBarcodeRules.PackFor(huge, "4600000000301").Should().BeNull();
    }

    [Fact]
    public void PackNote_UsesVariantNameOrDefault()
    {
        InvoiceBarcodeRules.PackNote(12, null).Should().Contain("= 12");
        InvoiceBarcodeRules.PackNote(12, "Блок").Should().StartWith("Блок = 12");
    }

    [Fact]
    public void SplitCodes_DedupesAndCleans()
    {
        InvoiceBarcodeRules.SplitCodes("4870200221261; 4870 2002 21278,\n4870200221261").Should().Equal("4870200221261", "487020022127" + "8");
    }

    // ------------------------------------------------------------------ запись на сервер (без сети)

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Bind_ProductWithoutCode_WritesMainBarcode()
    {
        IReadOnlyDictionary<string, object?>? saved = null;
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("""{"id":"p1","barcode":"","alternate_barcodes":[]}"""));
        InvoiceBarcodeBinder.SaveOverride = (_, fields, _) => { saved = fields; return Task.FromResult<string?>(null); };
        var r = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261");
        r.Ok.Should().BeTrue();
        r.Plan.Should().Be(InvoiceBindPlan.Main);
        saved.Should().NotBeNull();
        saved!["barcode"].Should().Be("4870200221261");
        saved.Should().NotContainKey("alternate_barcodes");
    }

    [Fact]
    public async Task Bind_ProductWithCode_AddsAlternateKeepingServerOnes()
    {
        // 2026-11-21: доп. код уходит ОДНИМ запросом (POST products/<id>/barcodes/), серверные коды не трогаются и в запросе не повторяются
        AlternateBarcodeVariant? added = null;
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("""{"id":"p1","barcode":"4870200221000","alternate_barcodes":[{"barcode":"4870200221017","name":"Пачка","quantity":10}]}"""));
        InvoiceBarcodeBinder.AddOverride = (_, v, _, _) => { added = v; return Task.FromResult(new BarcodeWriteOutcome(true)); };
        var r = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261", new AlternateBarcodeVariant { Barcode = "4870200221261", Name = "Коробка", Quantity = 24 });
        r.Ok.Should().BeTrue();
        r.Plan.Should().Be(InvoiceBindPlan.Alt);
        added!.Barcode.Should().Be("4870200221261");
        added.Name.Should().Be("Коробка");
        r.Variants.Should().HaveCount(2);
        r.Variants!.Single(d => d.Barcode == "4870200221017").Quantity.Should().Be(10d);   // серверный вариант не потерян
    }

    [Fact]
    public async Task Bind_CodeAlreadyOnProduct_WritesNothing()
    {
        var saves = 0;
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("""{"barcode":"4870200221261"}"""));
        InvoiceBarcodeBinder.SaveOverride = (_, _, _) => { saves++; return Task.FromResult<string?>(null); };
        var r = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261");
        r.Ok.Should().BeTrue();
        r.Plan.Should().Be(InvoiceBindPlan.Already);
        saves.Should().Be(0);
    }

    [Fact]
    public async Task Bind_CardNotRead_OrServerRefuses_IsNotOk_AndBadFormatNeverReachesServer()
    {
        var saves = 0;
        InvoiceBarcodeBinder.SaveOverride = (_, _, _) => { saves++; return Task.FromResult<string?>("штрихкод уже используется"); };
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(null);
        (await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261")).Ok.Should().BeFalse();   // карточка не прочиталась — писать нельзя (можно затереть чужое)
        saves.Should().Be(0);
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(Card("""{"barcode":""}"""));
        var refused = await InvoiceBarcodeBinder.BindAsync("p1", "4870200221261");
        refused.Ok.Should().BeFalse();
        refused.Message.Should().Contain("уже используется");
        (await InvoiceBarcodeBinder.BindAsync("p1", "12")).Ok.Should().BeFalse();
        saves.Should().Be(1);
    }

    [Fact]
    public async Task SaveAlternates_WritesWholeList()
    {
        IReadOnlyDictionary<string, object?>? saved = null;
        InvoiceBarcodeBinder.SaveOverride = (_, fields, _) => { saved = fields; return Task.FromResult<string?>(null); };
        var r = await InvoiceBarcodeBinder.SaveAlternatesAsync("p1", [new AlternateBarcodeVariant { Barcode = "111111", Name = "Пачка", Quantity = 5 }]);
        r.Ok.Should().BeTrue();
        ((object[])saved!["alternate_barcodes"]!).Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ XML и поиск по доп. коду строки

    private static InvoiceDocument ParseXml(string xml)
    {
        var result = InvoiceXmlParser.Parse(Encoding.UTF8.GetBytes(xml), "nakladnaya.xml");
        result.Error.Should().BeNull();
        return result.Document!;
    }

    [Fact]
    public void Xml_SeveralBarcodeTags_FirstIsMain_RestAreExtra()
    {
        var doc = ParseXml("""
            <Накладная>
              <Товар><Наименование>Вода Асу 0,5</Наименование><Штрихкод>4870200221261</Штрихкод><EAN>4870200221278</EAN><GTIN>4870200221285</GTIN><Количество>10</Количество><Цена>25</Цена></Товар>
              <Товар><Наименование>Сок яблочный</Наименование><Штрихкод>4870200221292</Штрихкод><Количество>5</Количество><Цена>60</Цена></Товар>
            </Накладная>
            """);
        doc.Items.Should().HaveCount(2);
        var water = doc.Items.Single(i => i.Name.StartsWith("Вода"));
        water.Barcode.Should().Be("4870200221261");
        water.ExtraBarcodes.Should().Equal("4870200221278", "4870200221285");
        doc.Items.Single(i => i.Name.StartsWith("Сок")).ExtraBarcodes.Should().BeEmpty();
    }

    [Fact]
    public void Xml_NoMainCodeButAnother_PromotesIt_NoDuplicatesOfMain()
    {
        var doc = ParseXml("""
            <Накладная>
              <Товар><Наименование>Хлеб</Наименование><Штрихкод></Штрихкод><GTIN>4870200221300</GTIN><Количество>3</Количество><Цена>20</Цена></Товар>
              <Товар><Наименование>Батон</Наименование><Штрихкод>4870200221317</Штрихкод><EAN>4870200221317</EAN><Количество>3</Количество><Цена>22</Цена></Товар>
            </Накладная>
            """);
        doc.Items.Single(i => i.Name == "Хлеб").Barcode.Should().Be("4870200221300");
        doc.Items.Single(i => i.Name == "Батон").ExtraBarcodes.Should().BeEmpty();
    }

    [Fact]
    public void Matcher_FindsProductByExtraBarcodeOfTheInvoiceLine()
    {
        var tile = Tile("p9", "Вода Асу 0,5", "4870200221278");
        var index = new InvoiceCatalogIndex([tile]);
        var item = new InvoiceItem { Barcode = "4870200999990", Name = "Вода без совпадения", Quantity = 1 };
        item.ExtraBarcodes.Add("4870200221278");
        var match = index.Match(item);
        match.Kind.Should().Be(InvoiceMatchKind.Barcode);
        match.Tile!.Id.Should().Be("p9");
    }

    // ------------------------------------------------------------------ строка приёмки: доп. коды живут в черновике и в очереди

    [Fact]
    public void ReceivingLine_ExtraBarcodesSurviveDraftAndJobCopy()
    {
        var line = new ReceivingLineVm { Barcode = "4870200221261", ProductName = "Вода", Source = ReceivingSource.Unknown, Quantity = 2 };
        line.ExtraBarcodes = [new AlternateBarcodeVariant { Barcode = "4870200221278", Name = "Пачка", Quantity = 12 }];
        line.ScanPackNote = "пачка = 12 шт.";
        var fromDraft = ReceivingLineVm.FromDraft(line.ToDraft());
        fromDraft.ExtraBarcodes.Should().ContainSingle().Which.Quantity.Should().Be(12);
        fromDraft.ScanPackNote.Should().Be("пачка = 12 шт.");
        var fromJob = JobLine.From(line).ToVm();
        fromJob.ExtraBarcodes.Should().ContainSingle().Which.Barcode.Should().Be("4870200221278");
        // JSON очереди: без доп. кодов поле в файл не пишется (старые задачи не меняются)
        JsonSerializer.Serialize(JobLine.From(new ReceivingLineVm { Barcode = "4870200221261", ProductName = "Вода", Source = ReceivingSource.Unknown })).Should().NotContain("ExtraBarcodes");
    }

    [Fact]
    public void PrintedInvoice_BarcodeColumnOnlyWhenAsked()
    {
        var doc = new ClientDoc { Kind = ClientDocKind.Waybill, Number = "ПР-1", Date = new DateTime(2026, 11, 20) };
        doc.Lines.Add(new ClientDocLine { Name = "Вода", Unit = "шт", Qty = 2, Price = 10, Barcode = "4870200221261" });
        doc.Extra = new Dictionary<string, JsonElement>();
        ClientDocRenderer.ShowBarcodes(doc).Should().BeFalse();                       // по умолчанию форма прежняя
        doc.Extra["barcodes"] = JsonSerializer.SerializeToElement(true);
        ClientDocRenderer.ShowBarcodes(doc).Should().BeTrue();
        doc.Lines[0].Barcode = null;
        ClientDocRenderer.ShowBarcodes(doc).Should().BeFalse();                       // ни у одной строки нет кода — колонки нет
        JsonSerializer.Serialize(new ClientDocLine { Name = "Х" }).Should().NotContain("\"bc\"");  // у документов клиентов поле в файл не пишется
        new DocNumberingSettings().PrintBarcode.Should().BeFalse();
        new DocNumberingSettings { PrintBarcode = true }.Normalized().PrintBarcode.Should().BeTrue();
    }
}
