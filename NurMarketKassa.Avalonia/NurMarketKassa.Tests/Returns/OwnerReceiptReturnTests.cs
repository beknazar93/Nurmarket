using System.Globalization;
using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-12: тесты, меняющие AppMode.IsOwner (общая статика), — по одному, не параллельно с остальными.</summary>
[CollectionDefinition("OwnerModeSerial", DisableParallelization = true)]
public sealed class OwnerModeSerialCollection
{
}

/// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): в программе владельца (--owner, без кассовой смены и ящика)
/// чек возврата и обмена на принтер чеков не печатается (печать по ESC/POS открывала бы и ящик); в кассе — как раньше. Тело возврата дробного
/// количества и один ключ повтора на повторную отправку того же возврата — те же, что в кассе (окно возврата общее).</summary>
[Collection("OwnerModeSerial")]
public sealed class OwnerReceiptReturnTests : IDisposable
{
    private static readonly PropertyInfo IsOwner = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _was = AppMode.IsOwner;
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose()
    {
        IsOwner.SetValue(null, _was);
        _fake.Dispose();
    }

    private static readonly (string Name, double Quantity, decimal UnitPrice, decimal Sum)[] Lines = [("Сахар", 1.25, 100m, 125m)];

    [Fact]
    public void OwnerMode_ReturnAndExchangeReceipts_AreNotPrinted()
    {
        IsOwner.SetValue(null, true);
        OperationReceiptPrinter.OwnerSkipsPrint("возврата").Should().BeTrue("в программе владельца нет принтера чеков кассы и ящика");
        OperationReceiptPrinter.PrintReturn("№003701", Lines, 125m, "Не подошёл товар", isWholeSale: false, "Владелец")
            .Should().BeNull("печать пропущена молча — это не ошибка печати");
        OperationReceiptPrinter.PrintExchange("№003701", DateTime.Today, "№003702", Lines, Lines, 125m, 125m, 0m, "cash", null, "Владелец")
            .Should().BeNull();
    }

    [Fact]
    public void KassaMode_PrintRuleUnchanged()
    {
        IsOwner.SetValue(null, false);
        OperationReceiptPrinter.OwnerSkipsPrint("возврата").Should().BeFalse("касса печатает чек возврата по своим настройкам, как раньше");
    }

    [Fact]
    public async Task FractionalReturn_Body_And_RepeatOfSameReturn_UsesSameKey()
    {
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var pending = new PendingIdempotencyKey(TimeSpan.FromMinutes(10));
        var line = new PosRefundLineRequest { LineId = "si-71", Title = "Сахар", Quantity = 1.25, OriginalQuantity = 2.5 };
        var signature = "lines|s-701|si-71:1.25";

        var first = pending.Get(signature);
        var again = pending.Get(signature); // ответ ещё не пришёл — тот же возврат уходит с тем же ключом
        again.Should().Be(first);
        await sales.PosReturnSaleAsync("s-701", [line], default, first);
        pending.OnSuccess();
        pending.Get(signature).Should().NotBe(first, "после успеха следующий возврат — новая операция");

        var post = Requests(_fake, "POST", "/api/main/pos/sales/s-701/return/").Should().ContainSingle().Subject;
        (Header(post, "Idempotency-Key") ?? BodyOf(post)["idempotency_key"]?.GetValue<string>()).Should().Be(first, "ключ повтора возврата — в теле запроса (idempotency_key)");
        var item = BodyOf(post)["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["sale_item_id"]!.GetValue<string>().Should().Be("si-71");
        double.Parse(item["quantity"]!.ToString(), CultureInfo.InvariantCulture).Should().Be(1.25, "весовой товар возвращается дробным количеством");
    }
}
