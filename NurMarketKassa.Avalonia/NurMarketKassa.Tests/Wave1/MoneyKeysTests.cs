using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты ключей повтора денежных запросов: MoneyRequestKeys (ключ по умолчанию для денежных POST через RequestAsync)
/// и PendingIdempotencyKey (ключ оплаты/возврата/обмена, живёт до ответа сервера). MoneyRequestKeys держит ключи в статической памяти процесса,
/// поэтому каждый тест берёт уникальную подпись.</summary>
public sealed class MoneyKeysTests
{
    private static string Unique() => Guid.NewGuid().ToString("N");

    // ───────────── MoneyRequestKeys.Applies: какие запросы считаются денежными ─────────────

    [Theory]
    [InlineData("api/main/clients/1/deals/2/pay/")]
    [InlineData("api/construction/cashflows/")]
    [InlineData("api/main/suppliers/5/payments/")]
    [InlineData("api/main/pos/sales/9/return/")]
    [InlineData("api/main/rentals/")]
    [InlineData("api/main/bonus/transfer/")]
    [InlineData("api/main/purchases/")]
    [InlineData("api/main/salary/payout/")]
    [InlineData("api/main/inventory/sessions/")]
    [InlineData("  API/Main/Debt/Add/  ")] // регистр и пробелы не важны
    public void Applies_MoneyPost_IsTrue(string path) =>
        MoneyRequestKeys.Applies(HttpMethod.Post, path).Should().BeTrue();

    [Theory]
    [InlineData("api/main/pos/checkout/")]                 // оплата чека — свой ключ (PosCheckoutService)
    [InlineData("api/users/auth/login/")]
    [InlineData("api/users/auth/pin/verify/")]
    [InlineData("api/construction/cashflow-categories/")]  // справочник, не деньги
    [InlineData("api/main/products/")]
    [InlineData("")]
    [InlineData("   ")]
    public void Applies_NotMoney_IsFalse(string path) =>
        MoneyRequestKeys.Applies(HttpMethod.Post, path).Should().BeFalse();

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Applies_OnlyPost(string method) =>
        MoneyRequestKeys.Applies(new HttpMethod(method), "api/main/debt/pay/").Should().BeFalse();

    // ───────────── Signature ─────────────

    [Fact]
    public void Signature_BodyWithOwnIdempotencyKey_IsNull_KeyNotAdded() =>
        MoneyRequestKeys.Signature(HttpMethod.Post, "api/main/pos/sales/1/return/", "{\"idempotency_key\":\"abc\"}").Should().BeNull();

    [Fact]
    public void Signature_SamePathAndBody_Same_DifferentBody_Different()
    {
        var a = MoneyRequestKeys.Signature(HttpMethod.Post, "api/main/debt/pay/", "{\"amount\":10}");
        var b = MoneyRequestKeys.Signature(HttpMethod.Post, "API/main/debt/pay/", "{\"amount\":10}"); // путь без регистра
        var c = MoneyRequestKeys.Signature(HttpMethod.Post, "api/main/debt/pay", "{\"amount\":10}");  // слэш в конце не важен
        var d = MoneyRequestKeys.Signature(HttpMethod.Post, "api/main/debt/pay/", "{\"amount\":11}");
        a.Should().Be(b).And.Be(c);
        a.Should().NotBe(d);
    }

    // ───────────── жизненный цикл ключа ─────────────

    [Fact]
    public void TryBegin_FirstCall_CreatesKey_SecondWhileInFlight_IsRefused()
    {
        var sig = Unique();
        MoneyRequestKeys.TryBegin(sig, out var key1).Should().BeTrue();
        key1.Should().NotBeNullOrWhiteSpace();

        // тот же запрос ещё выполняется — повторно не слать (защита от повторного нажатия)
        MoneyRequestKeys.TryBegin(sig, out var key2).Should().BeFalse();
        key2.Should().Be(key1);

        MoneyRequestKeys.OnSuccess(sig);
    }

    [Fact]
    public void OnSuccess_ForgetsKey_NextRequestGetsNewKey()
    {
        var sig = Unique();
        MoneyRequestKeys.TryBegin(sig, out var first).Should().BeTrue();
        MoneyRequestKeys.OnSuccess(sig);

        MoneyRequestKeys.TryBegin(sig, out var second).Should().BeTrue();
        second.Should().NotBe(first, "успех — следующая операция новая");
        MoneyRequestKeys.OnSuccess(sig);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(423)]
    [InlineData(425)]
    public void OnError_OutcomeUnknown_KeepsKeyForRetry(int status)
    {
        var sig = Unique();
        MoneyRequestKeys.TryBegin(sig, out var first).Should().BeTrue();

        MoneyRequestKeys.OnError(sig, new ApiException("x", status));

        MoneyRequestKeys.TryBegin(sig, out var retry).Should().BeTrue("повтор разрешён");
        retry.Should().Be(first, "ответ не дошёл — повтор уходит с тем же ключом");
        MoneyRequestKeys.OnSuccess(sig);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void OnError_BusinessRefusal_ForgetsKey(int status)
    {
        var sig = Unique();
        MoneyRequestKeys.TryBegin(sig, out var first).Should().BeTrue();

        MoneyRequestKeys.OnError(sig, new ApiException("x", status));

        MoneyRequestKeys.TryBegin(sig, out var next).Should().BeTrue();
        next.Should().NotBe(first, "отказ по существу — ключ забыт");
        MoneyRequestKeys.OnSuccess(sig);
    }

    [Fact]
    public void OnError_NetworkException_KeepsKey()
    {
        var sig = Unique();
        MoneyRequestKeys.TryBegin(sig, out var first).Should().BeTrue();
        MoneyRequestKeys.OnError(sig, new HttpRequestException("обрыв"));
        MoneyRequestKeys.TryBegin(sig, out var retry).Should().BeTrue();
        retry.Should().Be(first);
        MoneyRequestKeys.OnSuccess(sig);
    }

    [Fact]
    public void StillRunning_Is409_WithMessage()
    {
        var ex = MoneyRequestKeys.StillRunning();
        ex.StatusCode.Should().Be(409);
        ex.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────── PendingIdempotencyKey ─────────────

    [Fact]
    public void Pending_SameSignature_SameKey_UntilSuccess()
    {
        var p = new PendingIdempotencyKey();
        var k1 = p.Get("sale:1|100.00");
        p.Get("sale:1|100.00").Should().Be(k1);
        p.PeekKey.Should().Be(k1);
        p.Matches("sale:1|100.00").Should().BeTrue();
        p.Matches("sale:1|200.00").Should().BeFalse();

        p.OnSuccess();

        p.PeekKey.Should().BeNull();
        p.Get("sale:1|100.00").Should().NotBe(k1, "после успеха — новая операция");
    }

    [Fact]
    public void Pending_ChangedSignature_NewKey()
    {
        var p = new PendingIdempotencyKey();
        var k1 = p.Get("a");
        var k2 = p.Get("b");
        k2.Should().NotBe(k1, "ключ не должен подменить итогом прошлой операции новую с другим составом");
    }

    [Fact]
    public void Pending_OutcomeUnknown_KeepsKey_AndMarksUnanswered()
    {
        var p = new PendingIdempotencyKey();
        var k1 = p.Get("sig");
        p.HasUnansweredAttempt.Should().BeFalse();

        p.OnError(new ApiException("timeout", 504)).Should().BeTrue();

        p.HasUnansweredAttempt.Should().BeTrue();
        p.Get("sig").Should().Be(k1);
    }

    [Fact]
    public void Pending_BusinessRefusal_ForgetsKey()
    {
        var p = new PendingIdempotencyKey();
        var k1 = p.Get("sig");

        p.OnError(new ApiException("bad", 400)).Should().BeFalse();

        p.HasUnansweredAttempt.Should().BeFalse();
        p.Get("sig").Should().NotBe(k1);
    }

    [Fact]
    public void Pending_OnError_WithoutKey_IsFalse() =>
        new PendingIdempotencyKey().OnError(new ApiException("x", 500)).Should().BeFalse("ошибка после завершённой операции (OnSuccess) ключ не трогает");

    [Fact]
    public void Pending_Lifetime_ExpiredKeyIsReplaced()
    {
        var p = new PendingIdempotencyKey(TimeSpan.FromMilliseconds(30));
        var k1 = p.Get("sig");
        Thread.Sleep(80);
        p.Matches("sig").Should().BeFalse();
        p.Get("sig").Should().NotBe(k1, "ключ старше срока не подменяет новую операцию итогом прошлой");
    }

    [Fact]
    public void Pending_Restore_BringsBackKeyOfPreviousRun()
    {
        var p = new PendingIdempotencyKey();
        p.Restore("saved-key", "sig");
        p.Get("sig").Should().Be("saved-key");
        p.Get("other").Should().NotBe("saved-key");
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(423, true)]
    [InlineData(425, true)]
    [InlineData(409, true)]    // «запрос с этим ключом ещё выполняется» — исход неизвестен
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void IsOutcomeUnknown_ByStatus(int status, bool unknown) =>
        PendingIdempotencyKey.IsOutcomeUnknown(new ApiException("x", status)).Should().Be(unknown);

    [Fact]
    public void IsOutcomeUnknown_NonApiException_IsUnknown()
    {
        PendingIdempotencyKey.IsOutcomeUnknown(new HttpRequestException()).Should().BeTrue();
        PendingIdempotencyKey.IsOutcomeUnknown(new TaskCanceledException()).Should().BeTrue();
    }

    [Fact]
    public void IsOutcomeUnknown_ApiExceptionWithoutStatus_IsUnknown() =>
        PendingIdempotencyKey.IsOutcomeUnknown(new ApiException("нет связи")).Should().BeTrue();

    [Fact]
    public void IsOutcomeUnknown_409_PriceChanged_IsBusinessRefusal()
    {
        using var doc = JsonDocument.Parse("{\"code\":\"price_changed\"}");
        PendingIdempotencyKey.IsOutcomeUnknown(new ApiException("x", 409, doc.RootElement.Clone())).Should().BeFalse();
    }

    [Fact]
    public void IsOutcomeUnknown_409_RefundExceedsPaid_IsBusinessRefusal()
    {
        using var doc = JsonDocument.Parse("{\"code\":\"refund_exceeds_paid\"}");
        PendingIdempotencyKey.IsOutcomeUnknown(new ApiException("x", 409, doc.RootElement.Clone())).Should().BeFalse();
    }
}
