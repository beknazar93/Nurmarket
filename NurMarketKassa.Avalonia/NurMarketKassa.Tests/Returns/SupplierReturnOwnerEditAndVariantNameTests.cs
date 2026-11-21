using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-21 (ответ Умара 21.11.2026, п. 4–6): правка возврата поставщику владельцем после 24 ч (owner_override, editable_fields), добавление строк, причина и вид компенсации;
/// variant_name в чеке и приходе (отправка и показ); разбор for_receipt у sale_units. Окно и сервер — в UiTests (SupplierReturnEditStressUiTests).</summary>
[Collection("ServerFeatures")]
public sealed class SupplierReturnOwnerEditAndVariantNameTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-owneredit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _unitsFile = Path.Combine(Path.GetTempPath(), "nm-owneredit-units-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public SupplierReturnOwnerEditAndVariantNameTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.SupplierReturnEdit24h);
        ProductSaleUnits.FileOverride = _unitsFile;
        ProductSaleUnits.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ServerCapabilities.SetFeaturesForTests();
        ServerCapabilities.DirOverride = null;
        ProductSaleUnits.FileOverride = null;
        ProductSaleUnits.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
        try { File.Delete(_unitsFile); } catch { /* временный файл */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ReturnEditState State(string json) => ReturnEditState.Parse(J(json), DateTime.UtcNow);

    // ───────────── владелец после 24 ч ─────────────

    [Fact]
    public void ExpiredReturn_ManagerMayEdit_CashierMayNot()
    {
        var expired = State("""{"version":3,"can_edit":false,"can_void":false,"edit_block_reason":"edit_window_expired"}""");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, expired).Should().BeFalse("кассир — как раньше, только 24 ч");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, expired, isManager: false).Should().BeFalse();
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, expired, isManager: true).Should().BeTrue("владелец/админ правит и после 24 ч (PIN, журнал)");
        ReturnEditGate.IsOwnerOverride(ReturnDocKind.Supplier, expired, true).Should().BeTrue();
        ReturnEditGate.ShowEdit(ReturnDocKind.Customer, expired, isManager: true).Should().BeFalse("возврат клиента — прежние правила");
    }

    [Fact]
    public void OwnerOverrideFlag_FromCard_AllowsManager_ButOtherBlocksStay()
    {
        var withFlag = State("""{"version":2,"can_edit":false,"owner_override":true}""");
        withFlag.OwnerOverride.Should().BeTrue();
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, withFlag, isManager: true).Should().BeTrue();
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, withFlag, isManager: false).Should().BeFalse();

        var closedShift = State("""{"version":2,"can_edit":false,"edit_block_reason":"shift_closed"}""");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, closedShift, isManager: true).Should().BeFalse("другие запреты сервера не обходятся");
        var voided = State("""{"version":2,"can_edit":false,"status":"voided","edit_block_reason":"edit_window_expired"}""");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, voided, isManager: true).Should().BeFalse();
        var normal = State("""{"version":2,"can_edit":true,"can_void":true}""");
        ReturnEditGate.IsOwnerOverride(ReturnDocKind.Supplier, normal, true).Should().BeFalse("в срок это обычная правка");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, normal, isManager: false).Should().BeTrue();
    }

    [Fact]
    public void EditableFields_ArrayOrObject_OrUnknown()
    {
        var list = State("""{"version":1,"can_edit":true,"editable_fields":["items","reason","comment"]}""");
        list.CanEditField("items").Should().BeTrue();
        list.CanEditField("reason").Should().BeTrue();
        list.CanEditField("compensation").Should().BeFalse("сервер не перечислил — не показываем");
        var obj = State("""{"version":1,"can_edit":true,"editable_fields":{"lines":true,"compensation":true,"comment":false}}""");
        obj.CanEditField("items").Should().BeTrue("lines = items");
        obj.CanEditField("compensation").Should().BeTrue();
        obj.CanEditField("comment").Should().BeFalse();
        var none = State("""{"version":1,"can_edit":true}""");
        none.EditableFields.Should().BeNull();
        none.CanEditField("reason").Should().BeTrue("сервер не прислал — по контракту обычные поля можно");
    }

    // ───────────── новые строки, причина, компенсация ─────────────

    private static SupplierReturnEditLine Old(string id, double qty, double price) => new(id, "p" + id, "Товар " + id, "шт", qty, price, 0, "");

    [Fact]
    public void PatchBody_NewLine_HasProduct_NoId_PriceOnlyWhenEntered_AndReasonCompensation()
    {
        var original = new List<SupplierReturnEditLine> { Old("l1", 2, 100) };
        var lines = new List<SupplierReturnEditLine>
        {
            Old("l1", 2, 100),
            new("", "p9", "Новый", "шт", 3, 0, 0, "", IsNew: true, PriceSet: false),
            new("", "p8", "Новый с ценой", "шт", 1, 55.5, 0, "", IsNew: true, PriceSet: true),
            new("", "p7", "Ноль", "шт", 0, 0, 0, "", IsNew: true, PriceSet: false),
        };
        var body = SupplierReturnEdit.BuildPatchBody(lines, "добавили товар", "", original, "wrong_item", "debt_offset");
        var items = (List<Dictionary<string, object?>>)body["items"]!;
        items.Should().HaveCount(3, "строка с количеством 0 не уходит");
        items[0].Keys.Should().BeEquivalentTo(new[] { "id", "qty", "purchase_price" });
        items[1].Keys.Should().BeEquivalentTo(new[] { "product", "qty" }, "без цены — сервер возьмёт из прихода или закупочную товара");
        items[1]["product"].Should().Be("p9");
        items[1]["qty"].Should().Be("3");
        items[2].Keys.Should().BeEquivalentTo(new[] { "product", "qty", "purchase_price" });
        items[2]["purchase_price"].Should().Be("55.5");
        body["reason"].Should().Be("wrong_item");
        body["compensation"].Should().Be("debt_offset");
        body["edit_reason"].Should().Be("добавили товар");
        SupplierReturnEdit.BuildPatchBody(original, "правка возврата", "", original).Keys.Should().NotContain(new[] { "reason", "compensation" }, "не менялись — не отправляются");
    }

    [Fact]
    public void Validate_NewLine_NeedsNoId_ButNeedsProduct_AndHasChangesSeesIt()
    {
        var original = new List<SupplierReturnEditLine> { Old("l1", 2, 100) };
        var added = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("", "p9", "Новый", "шт", 1, 10, 0, "", IsNew: true) };
        SupplierReturnEdit.Validate(added, "добавили товар", original).Should().BeNull();
        SupplierReturnEdit.HasChanges(original, added, "", "").Should().BeTrue();
        var noProduct = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("", "", "Без товара", "шт", 1, 10, 0, "", IsNew: true) };
        SupplierReturnEdit.Validate(noProduct, "добавили товар", original).Should().Contain("нет товара");
        var zeroNew = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("", "p9", "Новый", "шт", 0, 10, 0, "", IsNew: true) };
        SupplierReturnEdit.HasChanges(original, zeroNew, "", "").Should().BeFalse("добавленная строка с 0 — как будто её нет");
        SupplierReturnEdit.HasChanges(original, original, "", "", "defect", "surplus", "cash", "cash").Should().BeTrue("причина возврата изменилась");
        SupplierReturnEdit.HasChanges(original, original, "", "", "defect", "defect", "cash", "debt_offset").Should().BeTrue("вид компенсации изменился");
        SupplierReturnEdit.HasChanges(original, original, "", "", "defect", "DEFECT", "cash", "cash").Should().BeFalse();
    }

    [Fact]
    public void Preview_CompensationChange_ShowsRollbackAndNew_AndNewLineLeavesStock()
    {
        var original = new List<SupplierReturnEditLine> { Old("l1", 2, 100) };
        var edited = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("", "p9", "Новый", "шт", 3, 0, 0, "", IsNew: true, PriceSet: false) };
        var lines = SupplierReturnEdit.Preview(original, edited, "cash", "debt_offset");
        lines.Should().Contain(l => l.Contains("Откатится прежнее") && l.Contains("из кассы уйдёт"), "касса откатывается по старой сумме");
        lines.Should().Contain(l => l.Contains("Проведётся новое") && l.Contains("долг поставщику уменьшится"));
        lines.Should().Contain(l => l.StartsWith("Склад:") && l.Contains("Новый") && l.Contains("уйдёт со склада"));
        lines.Should().Contain(l => l.Contains("сервер возьмёт из строки прихода"), "цена добавленной строки не введена");
        SupplierReturnEdit.Preview(original, original, "cash", "debt_offset").Should().Contain(l => l.Contains("Откатится прежнее"), "даже без смены строк вид компенсации — это изменение");
        SupplierReturnEdit.Preview(original, original, "cash").Should().BeEmpty();
    }

    [Fact]
    public void SameComposition_CountsAddedLines()
    {
        var sent = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("", "p9", "Новый", "шт", 3, 0, 0, "", IsNew: true, PriceSet: false) };
        var server = new List<SupplierReturnEditLine> { Old("l1", 2, 100), new("l77", "p9", "Новый", "шт", 3, 40, 0, "") };
        SupplierReturnEdit.SameComposition(server, sent).Should().BeTrue("у сервера добавленная строка получила id и цену");
        SupplierReturnEdit.SameComposition(new List<SupplierReturnEditLine> { Old("l1", 2, 100) }, sent).Should().BeFalse();
        SupplierReturnEdit.Signature("r1", 3, sent, "причина", "").Should().NotBe(SupplierReturnEdit.Signature("r1", 3, new List<SupplierReturnEditLine> { Old("l1", 2, 100) }, "причина", ""));
        SupplierReturnEdit.Signature("r1", 3, sent, "причина", "", "other", null).Should().NotBe(SupplierReturnEdit.Signature("r1", 3, sent, "причина", ""));
    }

    [Fact]
    public void FieldNotEditable_HasOwnText_AndIsNotFinal()
    {
        var api = new ApiException("x", 400, J("""{"code":"field_not_editable","message":"created_at"}"""));
        var e = ReturnEditErrors.Classify(api);
        e.Failure.Should().Be(ReturnEditFailure.FieldNotEditable);
        e.IsFinal.Should().BeFalse();
        ReturnEditErrors.Text(e).Should().Contain("менять это поле");
        ReturnEditErrors.Text(new ReturnEditException(ReturnEditFailure.NewLinesNotAllowed, "new_lines_not_allowed", ""))
            .Should().NotContain("нельзя", "устаревшая фраза «новые строки нельзя» убрана");
    }

    // ───────────── variant_name ─────────────

    [Theory]
    [InlineData("Асу Клубничный", "Асу", "Клубничный")]
    [InlineData("Рубашка (M, Синий)", "Рубашка", "M, Синий")]
    [InlineData("Клубничный", "Асу", "Клубничный")]
    [InlineData("Асу", "Асу", "Асу")]
    public void VariantPart_StripsProductTitle(string full, string title, string expected) =>
        ReceiptSnapshotCartEditor.VariantPart(full, title).Should().Be(expected);

    [Fact]
    public void VariantPart_Max128()
    {
        var part = ReceiptSnapshotCartEditor.VariantPart("Асу " + new string('я', 300), "Асу");
        part!.Length.Should().BeLessThanOrEqualTo(128);
    }

    private static string CartWith(string extra) =>
        "{\"items\":[{\"product_id\":\"11111111-1111-1111-1111-111111111111\",\"product_name\":\"Асу Клубничный\",\"quantity\":2,\"unit_price\":50.0" + extra + "}]}";

    [Fact]
    public void Checkout_VariantLine_SendsVariantName_PlainLineDoesNot()
    {
        var withVariant = QuickCheckoutBody.TryBuild(CartWith(",\"variant_name\":\"Асу Клубничный\",\"variant_part\":\"Клубничный\""), "cash", "100", null, null, Guid.NewGuid().ToString(), null, false, null, false, out var why);
        withVariant.Should().NotBeNull(why);
        var line = withVariant!["items"]!.AsArray()[0]!.AsObject();
        line["variant_name"]!.ToString().Should().Be("Клубничный", "название варианта без названия товара");
        line["qty"]!.ToString().Should().StartWith("2");
        var plain = QuickCheckoutBody.TryBuild(CartWith(""), "cash", "100", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
        plain!["items"]!.AsArray()[0]!.AsObject().ContainsKey("variant_name").Should().BeFalse("тело прежнее");
        var legacy = QuickCheckoutBody.TryBuild(CartWith(",\"variant_name\":\"Асу Клубничный\""), "cash", "100", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
        legacy!["items"]!.AsArray()[0]!.AsObject().ContainsKey("variant_name").Should().BeFalse("чек, начатый до обновления (нет variant_part), уходит как раньше");
    }

    [Fact]
    public void ItemTitle_NamePlusVariant_MergedInParentheses_NoDuplicate()
    {
        CartDisplayHelper.ItemTitle(J("""{"product_name":"Асу","variant_name":"Клубничный"}""")).Should().Be("Асу Клубничный");
        CartDisplayHelper.ItemTitle(J("""{"product_name":"Асу Клубничный","variant_name":"Клубничный"}""")).Should().Be("Асу Клубничный", "название уже содержит вариант");
        CartDisplayHelper.ItemTitle(J("""{"product_name":"Асу","variant_name":"Клубничный ×1, Лимонный ×2"}""")).Should().Be("Асу (Клубничный ×1, Лимонный ×2)", "слитая строка — как пришло");
        CartDisplayHelper.ItemTitle(J("""{"product_name":"Асу"}""")).Should().Be("Асу");
        CartDisplayHelper.ItemTitle(J("""{"product_name":"Асу","variant_name":""}""")).Should().Be("Асу");
        CartDisplayHelper.ItemName(J("""{"product_name":"Асу","variant_name":"Клубничный"}""")).Should().Be("Асу", "для сопоставления по названию — без варианта");
    }

    [Fact]
    public void ReceiptParse_ShowsNamePlusVariant_AndKeepsRawVariant()
    {
        var json = """{"id":"r1","supplier_id":"s","items":[{"id":"i1","product":"p1","product_name":"Асу","variant_name":"Клубничный","qty":"10","purchase_price":"40"},{"id":"i2","product":"p2","product_name":"Вода","qty":"1","purchase_price":"10"}]}""";
        var receipt = (SupplierReceipt)typeof(SupplierApiService).GetMethod("ParseReceipt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [J(json)])!;
        receipt.Lines[0].ProductName.Should().Be("Асу Клубничный");
        receipt.Lines[0].VariantName.Should().Be("Клубничный");
        receipt.Lines[1].ProductName.Should().Be("Вода");
        receipt.Lines[1].VariantName.Should().BeEmpty();
    }

    [Fact]
    public void ReceivingLine_VariantNote_BecomesVariantName()
    {
        var l = new ReceivingLineVm { ProductName = "Асу", VariantNote = "Асу: Клубничный 10, Лимонный 5" };
        PurchaseReceivingService.VariantNameOf(l).Should().Be("Клубничный ×10, Лимонный ×5");
        PurchaseReceivingService.VariantNameOf(new ReceivingLineVm { ProductName = "Асу" }).Should().BeNull("обычная строка — без поля");
        var many = new ReceivingLineVm { ProductName = "Асу", VariantNote = "Асу: " + string.Join(", ", Enumerable.Range(1, 40).Select(i => "Вкус номер " + i + " " + i)) };
        PurchaseReceivingService.VariantNameOf(many)!.Length.Should().BeLessThanOrEqualTo(128);
    }

    // ───────────── sale_units: for_receipt ─────────────

    [Fact]
    public void SaleUnits_ForReceipt_Parsed_WithoutChangingOtherFields()
    {
        var card = J("""{"id":"p1","sale_units":[{"id":"u1","name":"Мешок","unit_code":"bag","factor":"25","for_receipt":true},{"id":"u2","name":"Коробка","factor":"10","for_receipt":false},{"id":"u3","name":"Пачка","factor":"5"}]}""");
        ProductSaleUnits.Remember("p1", card);
        var units = ProductSaleUnits.For("p1");
        units.Should().HaveCount(3);
        units[0].ForReceipt.Should().BeTrue();
        units[1].ForReceipt.Should().BeFalse();
        units[2].ForReceipt.Should().BeNull("старый сервер флага не присылал");
        units[0].Factor.Should().Be(25m);
        ProductSaleUnits.ReceiptUnits("p1").Select(u => u.Id).Should().Equal("u1");
    }
}
