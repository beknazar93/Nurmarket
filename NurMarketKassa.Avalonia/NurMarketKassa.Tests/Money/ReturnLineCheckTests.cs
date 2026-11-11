using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-10 (жалоба владельца, фото клиента: возврат долгового чека → «Есть позиции не из этого чека или несуществующие sale_item_id.»):
/// id строки для sale_item_id, сверка отмеченных строк со свежим чеком перед отправкой, распознавание отказа сервера. Сервер — заглушка (FakeNurCrmServer).</summary>
[Collection("ServerFeatures")]
public sealed class ReturnLineCheckTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string SaleWithIds = "{\"id\":\"sale-1\",\"status\":\"debt\",\"payment_method\":\"debt\",\"total\":\"3540.00\",\"items\":["
        + "{\"id\":\"si-1\",\"product_id\":\"p-1\",\"name\":\"Подшипник\",\"quantity\":\"1\",\"returnable_qty\":1},"
        + "{\"id\":\"si-2\",\"product_id\":\"p-2\",\"name\":\"Ступица подшипник передний Ласетти WNQ\",\"quantity\":\"1\",\"returnable_qty\":1},"
        + "{\"id\":\"si-3\",\"product_id\":\"p-3\",\"name\":\"S-OIL SEVEN ATF III\",\"quantity\":\"4\",\"returnable_qty\":4}]}";

    // ───────── id строки ─────────

    [Fact]
    public void SaleItemId_ExplicitField_ThenRowId_NotItemIdOrLineId()
    {
        // Контракт Умара (11.11): sale_item_id — id строки чека из GET pos/sales/{id}/ (items[].id).
        var explicitLine = Parse("{\"id\":\"row-9\",\"sale_item_id\":\"si-9\",\"cart_item_id\":\"ci-9\",\"product_id\":\"p\"}");
        ReturnLineCheck.SaleItemId(explicitLine).Should().Be("si-9", "явное поле sale_item_id — ровно то, что ждёт сервер");

        // До 11.11 касса брала cart_item_id / line_id / item_id РАНЬШЕ id строки — и слала чужой id («Есть позиции не из этого чека»).
        foreach (var other in new[] { "cart_item_id", "sale_line_id", "line_id", "item_id", "pos_line_id" })
        {
            var line = Parse("{\"id\":\"row-9\",\"" + other + "\":\"x-9\",\"product_id\":\"p\"}");
            ReturnLineCheck.SaleItemId(line).Should().Be("row-9", $"поле {other} не подменяет id строки");
            CartDisplayHelper.TryRefundLineId(line).Should().Be("x-9", "прежний разбор (для корзины) не менялся");
        }

        ReturnLineCheck.SaleItemId(Parse("{\"id\":\"row-1\",\"product_id\":\"p-1\"}")).Should().Be("row-1");
        ReturnLineCheck.SaleItemId(Parse("{\"id\":\"p-1\",\"product_id\":\"p-1\",\"line_id\":\"l-1\"}")).Should().Be("l-1", "id товара — не id строки; тогда прежние поля");
        ReturnLineCheck.SaleItemId(Parse("{\"product_id\":\"p-1\",\"name\":\"Хлеб\"}")).Should().BeNull("строка из кэша без id");
    }

    [Fact]
    public void HasDifferentRowIds_OnlyWhenRowIdDiffersFromSentId()
    {
        ReturnLineCheck.HasDifferentRowIds(Parse(SaleWithIds)).Should().BeFalse();
        ReturnLineCheck.HasDifferentRowIds(Parse("{\"items\":[{\"id\":\"row-1\",\"cart_item_id\":\"ci-1\",\"product_id\":\"p\"}]}")).Should().BeTrue();
    }

    // ───────── сверка со свежим чеком ─────────

    [Fact]
    public void Verify_SameIds_Ok()
    {
        var check = ReturnLineCheck.Verify(Parse(SaleWithIds), [new("si-1", "p-1", "Подшипник", 1)]);
        check.Ok.Should().BeTrue();
        check.Changed.Should().BeFalse();
    }

    [Fact]
    public void Verify_StaleId_SameProductOneLine_IsRemapped_NotSentByGuess()
    {
        // Чек после частичного возврата/правки на сайте: строки пересозданы, id другие.
        var check = ReturnLineCheck.Verify(Parse(SaleWithIds), [new("old-2", "p-2", "Ступица", 1)]);
        check.Missing.Should().BeEmpty();
        check.Remap.Should().ContainKey("old-2").WhoseValue.Should().Be("si-2");
        check.Changed.Should().BeTrue("окно перечитает чек и попросит повторить — по догадке деньги не отправляются");
    }

    [Fact]
    public void Verify_LineGoneFromReceipt_IsMissing()
    {
        var check = ReturnLineCheck.Verify(Parse(SaleWithIds), [new("old-7", "p-7", "Глушитель", 1)]);
        check.Ok.Should().BeFalse();
        check.Missing.Should().ContainSingle().Which.Title.Should().Be("Глушитель");
    }

    [Fact]
    public void Verify_SameProductTwoLines_NotGuessed()
    {
        var sale = Parse("{\"items\":[{\"id\":\"a\",\"product_id\":\"p\",\"name\":\"Хлеб\"},{\"id\":\"b\",\"product_id\":\"p\",\"name\":\"Хлеб\"}]}");
        var check = ReturnLineCheck.Verify(sale, [new("old", "p", "Хлеб", 1)]);
        check.Missing.Should().ContainSingle("один товар двумя строками — сопоставить однозначно нельзя");
        check.Remap.Should().BeEmpty();
    }

    [Fact]
    public void Verify_ReceiptWithoutLineIds_IsReported()
    {
        var sale = Parse("{\"items\":[{\"product_id\":\"p-1\",\"name\":\"Хлеб\",\"quantity\":\"1\"}]}");
        var check = ReturnLineCheck.Verify(sale, [new("si-1", "p-1", "Хлеб", 1)]);
        check.FreshHasNoIds.Should().BeTrue();
        check.Ok.Should().BeFalse();
    }

    // ───────── отказ сервера ─────────

    [Theory]
    [InlineData(400, "{\"detail\":\"Есть позиции не из этого чека или несуществующие sale_item_id.\"}", true)]
    [InlineData(400, "{\"items\":[\"Есть позиции не из этого чека или несуществующие sale_item_id.\"]}", true)]
    [InlineData(404, "{\"detail\":\"sale_item_id not found\"}", true)]
    [InlineData(400, "{\"method\":[\"Долг нельзя указывать в строках оплаты.\"]}", false)]
    [InlineData(409, "{\"code\":\"refund_exceeds_paid\"}", false)]
    [InlineData(500, "{\"detail\":\"Есть позиции не из этого чека\"}", false)]
    public async Task UnknownLineRefusal_IsRecognised_FromServerAnswer(int status, string body, bool expected)
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", status, body);
        var api = await LoggedInAsync(_fake);

        var act = async () => await new SalesApiService(api).PosReturnSaleAsync("sale-x",
            [new PosRefundLineRequest { LineId = "old-1", Title = "Подшипник", Quantity = 1, OriginalQuantity = 1 }], default, "k");

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ReturnLineCheck.IsUnknownLineRefusal(ex).Should().Be(expected);
    }

    [Fact]
    public async Task DebtReceipt_FreshCard_IdsMatch_ReturnGoesWithFreshIds()
    {
        // Долговой чек: окно перед отправкой берёт свежую карточку (GET pos/sales/{id}) — id строк совпали, в теле ушли именно они.
        _fake.Json("GET", "/api/main/pos/sales/sale-1/", 200, SaleWithIds);
        _fake.Json("POST", "/api/main/pos/sales/sale-1/return/", 200, "{\"id\":\"sale-1\",\"refund\":{\"amount\":\"560.00\",\"cash\":\"0.00\"}}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);

        var fresh = await sales.PosSaleGetAsync("sale-1");
        var wanted = new List<ReturnLineCheck.Wanted> { new("si-3", "p-3", "S-OIL SEVEN ATF III", 1) };
        ReturnLineCheck.Verify(fresh, wanted).Ok.Should().BeTrue();
        await sales.PosReturnSaleAsync("sale-1", [new PosRefundLineRequest { LineId = "si-3", Title = "S-OIL SEVEN ATF III", Quantity = 1, OriginalQuantity = 4 }], default, "key-d");

        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/sales/sale-1/return/").Single());
        body["items"]!.AsArray().Single()!["sale_item_id"]!.GetValue<string>().Should().Be("si-3");
        body.ContainsKey("payments").Should().BeFalse("способ «в счёт долга» сервер выбирает сам — строк оплаты касса не передаёт");
    }

    [Fact]
    public async Task LineWithItemIdField_ReturnSendsRowId_AsServerContractSays()
    {
        // Строка чека с полем item_id (раньше касса отправила бы его) — в sale_item_id уходит id строки.
        _fake.Json("GET", "/api/main/pos/sales/sale-2/", 200, "{\"id\":\"sale-2\",\"items\":[{\"id\":\"si-1\",\"item_id\":\"it-77\",\"product_id\":\"p-1\",\"name\":\"Подшипник\",\"quantity\":\"1\"}]}");
        _fake.Json("POST", "/api/main/pos/sales/sale-2/return/", 200, "{\"id\":\"sale-2\",\"refund\":{\"amount\":\"500.00\"},\"debt_adjustment\":null}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var fresh = await sales.PosSaleGetAsync("sale-2");
        var lineId = ReturnLineCheck.SaleItemId(CartDisplayHelper.EnumerateSaleLineItems(fresh).Single());
        lineId.Should().Be("si-1");
        ReturnLineCheck.HasDifferentRowIds(fresh).Should().BeTrue("прежний разбор дал бы it-77 — это и была ошибка «не из этого чека»");

        await sales.PosReturnSaleAsync("sale-2", [new PosRefundLineRequest { LineId = lineId!, Title = "Подшипник", Quantity = 1, OriginalQuantity = 1 }], default, "k1");
        var item = BodyOf(Requests(_fake, "POST", "/api/main/pos/sales/sale-2/return/").Single())["items"]!.AsArray().Single()!;
        item["sale_item_id"]!.GetValue<string>().Should().Be("si-1");
        double.Parse(item["quantity"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
        ReturnLineCheck.DescribeSale(fresh).Should().Contain("si-1").And.Contain("Подшипник");
    }

    [Fact]
    public async Task SameKeyRepeat_ServerReplayed_IsSuccessNotError()
    {
        // Повтор с тем же idempotency_key: сервер отвечает 200 с replayed:true (второй возврат не создаётся) — касса принимает как успех.
        _fake.Json("POST", "/api/main/pos/sales/sale-4/return/", 200, "{\"id\":\"sale-4\",\"replayed\":true,\"refund\":{\"amount\":\"560.00\"},\"debt_adjustment\":{\"reduced_by\":\"560.00\",\"remaining_debt\":\"2980.00\"}}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var line = new PosRefundLineRequest { LineId = "si-4", Title = "S-OIL", Quantity = 1, OriginalQuantity = 4 };

        var first = await sales.PosReturnSaleAsync("sale-4", [line], default, "same-key");
        var again = await sales.PosReturnSaleAsync("sale-4", [line], default, "same-key");

        again.GetProperty("replayed").GetBoolean().Should().BeTrue();
        ServerRefundFigures.TryRead(again).Should().NotBeNull("цифры возврата из ответа повтора читаются как обычно");
        Requests(_fake, "POST", "/api/main/pos/sales/sale-4/return/").Select(r => BodyOf(r)["idempotency_key"]!.GetValue<string>()).Should().Equal("same-key", "same-key");
    }

    [Fact]
    public async Task RepeatedReturn_AfterFullReturn_FreshCardShowsNothingLeft()
    {
        // Повторный возврат: свежая карточка — всё уже возвращено (returnable_qty 0, return_status full): строк к возврату нет.
        _fake.Json("GET", "/api/main/pos/sales/sale-3/", 200, "{\"id\":\"sale-3\",\"status\":\"canceled\",\"return_status\":\"full\",\"items\":[{\"id\":\"si-1\",\"product_id\":\"p-1\",\"name\":\"Подшипник\",\"quantity\":\"1\",\"returned_qty\":\"1.000\",\"returnable_qty\":0}]}");
        var api = await LoggedInAsync(_fake);
        var fresh = await new SalesApiService(api).PosSaleGetAsync("sale-3");

        CartDisplayHelper.IsSaleFullyReturned(fresh).Should().BeTrue();
        CartDisplayHelper.RefundableQuantity(CartDisplayHelper.EnumerateSaleLineItems(fresh).Single()).Should().Be(0);
        SaleListFilter.ReturnStateOf("canceled", "full").Should().Be(SaleListFilter.ReturnState.Full);
    }
}
