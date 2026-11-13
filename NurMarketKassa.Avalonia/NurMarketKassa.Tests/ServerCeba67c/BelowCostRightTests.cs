using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.ServerCeba67c;

/// <summary>2026-11-13 (сервер bc1112): «продажа со скидкой ниже закупочной цены — только с правом can_sell_below_cost». Право разбирается из профиля (нет поля = нет права),
/// касса проверяет чек до отправки (с правом / без права / без закупочной цены / скидка на чек), отказ сервера показывается понятным текстом, а чек из очереди
/// «Неотправленное» не висит бесконечно, а становится «Требует проверки» с причиной. Сеть — только WireMock.</summary>
[Collection("Outbox")]
public sealed class BelowCostRightTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ───────────── 1. право в профиле ─────────────

    [Theory]
    [InlineData("""{"user":{"id":"u","role":"cashier","can_sell_below_cost":true}}""", true)]
    [InlineData("""{"can_sell_below_cost":true}""", true)]
    [InlineData("""{"permissions":["can_view_sales","can_sell_below_cost"]}""", true)]
    [InlineData("""{"user":{"id":"u","role":"owner","can_sell_below_cost":true}}""", true)]
    [InlineData("""{"user":{"role":"cashier","can_sell_below_cost":false}}""", false)]
    [InlineData("""{"user":{"role":"cashier"}}""", false)]
    [InlineData("""{"user":{"role":"cashier","can_sell_below_cost":null}}""", false)]
    [InlineData("""{"user":{"role":"cashier","can_sell_below_cost":"true"}}""", false)]
    [InlineData("""{"user":{"id":"u","role":"owner"}}""", true)]                                             // 2026-11-13: сервер владельцу флаг не шлёт (null) — можно по роли
    [InlineData("""{"user":{"id":"u","role":"owner","can_sell_below_cost":null}}""", true)]
    [InlineData("""{"user":{"id":"u","role":"admin","can_sell_below_cost":false}}""", true)]
    [InlineData("""{"role":"admin"}""", true)]
    [InlineData("""{"user":{"role":"manager"}}""", false)]
    [InlineData("""{}""", false)]
    public void Right_IsReadFromProfile_NoFieldMeansNoRight(string profile, bool expected) =>
        PermissionService.IsSellBelowCostAllowed(Json(profile)).Should().Be(expected);

    [Fact]
    public void Right_DefaultPayload_IsNoRight_FailClosed() =>
        PermissionService.IsSellBelowCostAllowed(default).Should().BeFalse();

    public class AuthStub : DispatchProxy
    {
        public static JsonElement Payload;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_UserPayload")
                return Payload;
            var rt = targetMethod!.ReturnType;
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    [Theory]
    [InlineData("""{"user":{"role":"cashier","can_sell_below_cost":true}}""", true)]
    [InlineData("""{"user":{"role":"cashier"}}""", false)]
    [InlineData("""{"user":{"id":"u","role":"owner"}}""", true)]
    [InlineData("""{"user":{"id":"u","role":"admin"}}""", true)]
    [InlineData("""{"user":{"role":"cashier","can_sell_below_cost":false}}""", false)]
    public void PermissionService_HasPermission_OwnerAdminByRole_OthersByFlag(string profile, bool expected)
    {
        lock (typeof(AuthStub))
        {
            AuthStub.Payload = Json(profile);
            var auth = DispatchProxy.Create<IAuthApiService, AuthStub>();
            var autonomous = DispatchProxy.Create<IAutonomousAuthService, AuthStub>();
            new PermissionService(auth, autonomous).HasPermission(PosPermissions.SellBelowCost).Should().Be(expected);
        }
    }

    // ───────────── 2. проверка чека до отправки ─────────────

    [Fact]
    public void Find_LineDiscountBelowCost_IsFound_AndBlocksWithoutRight()
    {
        // закупка 80 за штуку, 2 шт, цена 100 → строка 200; скидка 50 → 150 при закупке 160 → убыток 10
        var hits = BelowCostGuard.Find([new BelowCostLine("Хлеб", 80m, 2m, 150m, HasDiscount: true)], orderDiscount: 0m);
        hits.Should().ContainSingle().Which.Should().Be(new BelowCostHit("Хлеб", 10m));
        BelowCostGuard.Blocks(hits, canSellBelowCost: false).Should().BeTrue();
        BelowCostGuard.Blocks(hits, canSellBelowCost: true).Should().BeFalse("с правом — как раньше");
    }

    [Fact]
    public void Find_DiscountAboveCost_IsNotBelowCost()
    {
        // цена после скидки 170 при закупке 160 — прибыль, ограничения нет
        var hits = BelowCostGuard.Find([new BelowCostLine("Хлеб", 80m, 2m, 170m, true)], 0m);
        hits.Should().BeEmpty();
        BelowCostGuard.Blocks(hits, false).Should().BeFalse();
    }

    [Fact]
    public void Find_NoPurchasePrice_IsNotChecked()
    {
        BelowCostGuard.Find([new BelowCostLine("Услуга", 0m, 1m, 1m, true)], 50m).Should().BeEmpty("закупочной цены нет — не проверяем");
        BelowCostGuard.Find([new BelowCostLine("Без цены", -1m, 1m, 1m, true)], 0m).Should().BeEmpty();
    }

    [Fact]
    public void Find_PriceBelowCostWithoutAnyDiscount_IsNotTheDiscountRule()
    {
        // правило сервера — про СКИДКУ: строка без скидки (и без скидки на чек), даже если закупка выросла выше цены, не блокируется
        BelowCostGuard.Find([new BelowCostLine("Старая цена", 120m, 1m, 100m, HasDiscount: false)], 0m).Should().BeEmpty();
    }

    [Fact]
    public void Find_OrderDiscount_IsSpreadByLines()
    {
        // две строки по 100 (закупка 90 и 40), скидка на чек 40 → доля 20 %: нетто 80 и 80; первая ниже закупки на 10, вторая выше
        var hits = BelowCostGuard.Find(
            [new BelowCostLine("A", 90m, 1m, 100m, false), new BelowCostLine("B", 40m, 1m, 100m, false)],
            orderDiscount: 40m);
        hits.Should().ContainSingle().Which.Should().Be(new BelowCostHit("A", 10m));
    }

    [Fact]
    public void Find_OrderDiscountOverTotal_IsClampedToWholeReceipt()
    {
        var hits = BelowCostGuard.Find([new BelowCostLine("A", 50m, 1m, 100m, false)], orderDiscount: 500m);
        hits.Should().ContainSingle().Which.Loss.Should().Be(50m, "скидка больше суммы чека — строка уходит в ноль, убыток равен закупке");
    }

    [Fact]
    public void Find_KopeckRounding_DoesNotBlock()
    {
        // убыток меньше полкопейки — округление цен, а не продажа ниже закупки
        BelowCostGuard.Find([new BelowCostLine("A", 33.333m, 3m, 99.998m, true)], 0m).Should().BeEmpty();
    }

    [Fact]
    public void BlockedText_NamesTheRight_AndLists_AndIsLocalized()
    {
        var text = BelowCostGuard.BlockedText([new BelowCostHit("Хлеб", 10m)]);
        text.Should().Contain("ниже закупочной").And.Contain("Продажа ниже закупки").And.Contain("Хлеб");
        BelowCostGuard.ServerDeniedText().Should().Contain("Продажа ниже закупки").And.Contain("не проведена");
        BelowCostGuard.ReplayRejectedText().Should().Contain("Требует проверки").And.Contain("Повторить отправку").And.Contain("тем же ключом")
            .And.NotContain("проведите продажу на сайте", "совет «проведите на сайте» мог бы дать дубль: ключ уже уходил");
    }

    // ───────────── 3. отказ сервера: распознавание и понятный текст ─────────────

    private static ApiException Refusal(int status, string json) => new("серверный текст", status, Json(json));

    [Theory]
    [InlineData(403, """{"code":"can_sell_below_cost","detail":"Нет права"}""")]
    [InlineData(403, """{"detail":"permission denied: can_sell_below_cost"}""")]
    [InlineData(400, """{"code":"price_below_cost","min_price":"120.00","required_permission":"can_sell_below_cost","unit_price":"90 сом","index":1}""")]
    [InlineData(400, """{"errors":[{"index":0,"code":"price_below_cost","min_price":"5"}]}""")]
    [InlineData(400, """{"code":"below_cost"}""")]
    [InlineData(400, """{"error":"sell_below_cost_forbidden"}""")]
    [InlineData(422, """{"detail":"Цена со скидкой ниже закупочной, нужно право"}""")]
    public void ServerDenial_IsRecognized_AndFriendlyEverywhere(int status, string json)
    {
        var ex = Refusal(status, json);
        BelowCostGuard.IsServerDenial(ex).Should().BeTrue();
        var expected = BelowCostGuard.ServerDeniedText(ex, null);
        ServerErrorCodes.Friendly(ex).Should().Be(expected);
        PaymentErrorMessages.ForCashier(ex).Should().Be(expected);
        ServerOutageMonitor.FriendlyError(ex).Should().Be(expected);
    }

    [Theory]
    [InlineData(403, """{"code":"read_only"}""")]
    [InlineData(400, """{"code":"duplicate_line"}""")]
    [InlineData(500, """{"code":"can_sell_below_cost"}""")]   // 5xx — сбой сервера, не отказ по праву
    [InlineData(409, """{"code":"below_cost"}""")]
    public void OtherRefusals_AreNotBelowCostDenial(int status, string json) =>
        BelowCostGuard.IsServerDenial(Refusal(status, json)).Should().BeFalse();

    [Fact]
    public void Refusal_PriceBelowCost_GivesIndexAndMinPrice_AndLineByIndex()
    {
        var ex = Refusal(400, """{"code":"price_below_cost","index":1,"min_price":"1234.5","required_permission":"can_sell_below_cost","unit_price":"90 сом"}""");
        var refusal = BelowCostGuard.ParseRefusal(ex);
        refusal.Should().Be(new BelowCostGuard.BelowCostRefusal(1, 1234.5m));

        var cart = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["product_name"] = "Хлеб", ["quantity"] = 1, ["unit_price"] = 30.0 },
                new JsonObject { ["product_name"] = "Молоко", ["quantity"] = 2, ["unit_price"] = 60.0 }),
        }.ToJsonString();
        BelowCostGuard.TitleAtIndex(cart, 1).Should().Be("Молоко");
        BelowCostGuard.TitleAtIndex(cart, 5).Should().BeNull();
        BelowCostGuard.TitleAtIndex(cart, null).Should().BeNull();
        BelowCostGuard.TitleAtIndex("не json", 0).Should().BeNull();

        var text = BelowCostGuard.ServerDeniedText(ex, cart);
        text.Should().Contain("Молоко").And.Contain(MoneyText.Som(1234.5m), "минимальная цена — общим форматтером денег").And.Contain("Продажа ниже закупки");
        BelowCostGuard.ReplayRejectedText(ex, cart).Should().Contain("Молоко").And.Contain(MoneyText.Som(1234.5m));
        // без index/min_price (запасной признак) — текст без уточнения, но понятный
        BelowCostGuard.ServerDeniedText(Refusal(403, """{"code":"can_sell_below_cost"}"""), cart).Should().NotContain("Позиция");
    }

    [Fact]
    public void Refusal_IndexInBulkRow_IsFound()
    {
        var ex = Refusal(400, """{"results":[{"status":201},{"status":400,"index":2,"code":"price_below_cost","min_price":"7"}]}""");
        BelowCostGuard.ParseRefusal(ex).Should().Be(new BelowCostGuard.BelowCostRefusal(2, 7m));
    }

    [Fact]
    public void ServerDenial_NotAnApiException_IsFalse()
    {
        BelowCostGuard.IsServerDenial(null).Should().BeFalse();
        BelowCostGuard.IsServerDenial(new InvalidOperationException("can_sell_below_cost")).Should().BeFalse();
    }

    // ───────────── 4. WireMock: отказ сервера при оплате и при досылке из очереди ─────────────

    private static WireMockServer StartServerRefusing()
    {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/main/pos/checkout/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("{\"code\":\"price_below_cost\",\"index\":0,\"min_price\":\"25.00\",\"required_permission\":\"can_sell_below_cost\",\"unit_price\":\"20 сом\"}")); // 2026-11-13: точный ответ Умара
        return server;
    }

    private static JsonObject OneLineBody() => new()
    {
        ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 1, ["unit_price"] = 10.0 }),
    };

    [Fact]
    public async Task WireMock_Checkout403_GivesFriendlyText_NotRawAnswer()
    {
        using var server = StartServerRefusing();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        var sales = new SalesApiService(api);

        var act = async () => await sales.PosQuickCheckoutAsync(OneLineBody(), Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(10), strictPrice: false);
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;

        ex.StatusCode.Should().Be(400);
        BelowCostGuard.IsServerDenial(ex).Should().BeTrue();
        var shown = PaymentErrorMessages.ForCashier(ex);
        shown.Should().Be(BelowCostGuard.ServerDeniedText(ex, null));
        shown.Should().Contain(MoneyText.Som(25m), "минимальная цена из ответа сервера").And.NotContain("price_below_cost", "кассир не должен видеть сырой ответ сервера");
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/pos/checkout/").Should().Be(1);
    }

    public class AuthOnlineStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == "CanReachApiAsync")
                return Task.FromResult(true);
            var rt = targetMethod.ReturnType;
            if (rt == typeof(Task))
                return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    [Fact]
    public async Task WireMock_QueuedReceipt_Refused_BecomesNeedsReviewWithReason_NotHangingInQueue()
    {
        using var server = StartServerRefusing();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        var id = Guid.NewGuid().ToString("N");
        OfflinePendingSalesStore.Append(new OfflineSaleEntry
        {
            Id = id,
            PaymentMethod = "cash",
            CashReceived = "100",
            CartJson = new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Хлеб", ["quantity"] = 2, ["unit_price"] = 30.0 }),
            }.ToJsonString(),
            QuickCheckoutAttempted = true,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-3),
        });
        var auth = DispatchProxy.Create<IAuthApiService, AuthOnlineStub>();
        var sync = new SyncService(new SalesApiService(api), auth, null!, null!, null!, null!);
        try
        {
            try { await sync.ProbeNowAsync(); await sync.TriggerSyncNowAsync(); }
            catch (Exception) { /* у подставных служб нет каталога и т. п. — важен итог по чеку */ }

            var entry = OfflinePendingSalesStore.TryGetById(id);
            entry.Should().NotBeNull();
            entry!.Status.Should().Be(OfflineSaleEntry.Failed, "403 «ниже закупки» не «подождём прав»: чек не висит в очереди бесконечно");
            entry.ReviewNeeded.Should().BeTrue("автоповтора нет — решает человек (повтор после выдачи права или закрытие)");
            entry.LastError.Should().Contain("ниже закупочной").And.Contain("Продажа ниже закупки");
            entry.LastError.Should().Contain("Хлеб").And.Contain(MoneyText.Som(25m), "позиция по index и минимальная цена");
            entry.LastError.Should().NotContain("price_below_cost");
            server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/pos/checkout/").Should().Be(1, "старым путём без ключа чек не пересылается");
        }
        finally
        {
            sync.Dispose();
            OfflinePendingSalesStore.RemoveSynced(id);
        }
    }
}
