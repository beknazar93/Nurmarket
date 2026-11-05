using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.ServerCeba67c;

/// <summary>Тесты не параллельны другим: меняют общий кэш признаков сервера (ServerCapabilities) и правила округления.</summary>
[CollectionDefinition("ServerFeatures", DisableParallelization = true)]
public sealed class ServerFeaturesCollection { }

/// <summary>2026-11-05: подстройка кассы под сервер ceba67c (docs/Отчеты/Ответ Умара — ТЗ 10 из 10…). Признаки сервера подставляются прямо в кэш; сеть не используется (кроме WireMock).</summary>
[Collection("ServerFeatures")]
public sealed class ServerCeba67cTests : IDisposable
{
    public ServerCeba67cTests() => ServerCapabilities.SetFeaturesForTests();

    public void Dispose()
    {
        ServerCapabilities.SetFeaturesForTests();
        QuantityRules.WeightLineFloorEnabled = () => ServerCapabilities.Has(ServerCapabilities.WeightLineTotalFloor);
        QuantityRules.WeightStep = () => 0.01m;
        DeviceIdentity.DirOverride = null;
        DeviceIdentity.ResetForTests();
    }

    // ───────────── 1. debt_return_drawer_cash: поправки ящика ─────────────

    [Fact]
    public void DebtRefundCorrection_AppliedWithoutFeature_ArchivedAndIgnoredWithFeature()
    {
        var shift = "shift-" + Guid.NewGuid().ToString("N");
        // без признака — как раньше: поправка возврата долгового чека применяется
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => false;
        ExchangeCashCorrections.RecordIfKnownServerError(shift, "refund:old-1", overstated: 5m, suspect: 5m, "тест", debtRefund: true);
        ExchangeCashCorrections.ForShift(shift).Should().Be(-5m);

        // с признаком: новые поправки долгового возврата не пишутся, старые не применяются (сервер пересчитал смены сам) и уходят в архив
        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => true;
        ExchangeCashCorrections.RecordIfKnownServerError(shift, "refund:new-2", overstated: 7m, suspect: 7m, "тест", debtRefund: true);
        ExchangeCashCorrections.ForShift(shift).Should().Be(0m, "старая поправка −5 не вычитается второй раз");
        var archive = Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "exchange_cash_fix.archived.json");
        File.Exists(archive).Should().BeTrue();
        File.ReadAllText(archive).Should().Contain("refund:old-1").And.NotContain("refund:new-2");

        // поправки других ошибок сервера (предоплата безналом, обмен), записанные уже после, работают
        ExchangeCashCorrections.RecordIfKnownServerError(shift, "debtprepay:z", overstated: 3m, suspect: 3m, "тест");
        ExchangeCashCorrections.ForShift(shift).Should().Be(-3m);
        ExchangeCashCorrections.DebtRefundHandledByServer.Should().BeTrue();

        ExchangeCashCorrections.ServerSubtractsDebtRefund = () => ServerCapabilities.Has(ServerCapabilities.DebtReturnDrawerCash);
    }

    // ───────────── 2. checkout_branch: device_key / device_name / branch ─────────────

    [Fact]
    public void CheckoutBody_HasDeviceFields_OnlyWithFeature()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-device-" + Guid.NewGuid().ToString("N")[..8]);
        DeviceIdentity.DirOverride = dir;
        DeviceIdentity.ResetForTests();
        DeviceIdentity.NameProvider = () => "Касса 2";
        DeviceIdentity.BranchProvider = () => "11111111-1111-1111-1111-111111111111";

        var without = new JsonObject();
        DeviceIdentity.ApplyTo(without);
        without.ContainsKey("device_key").Should().BeFalse("старому серверу лишние поля не шлём");

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.CheckoutBranch);
        var body = new JsonObject();
        DeviceIdentity.ApplyTo(body);
        body["device_key"]!.GetValue<string>().Should().MatchRegex("^[A-Za-z0-9_-]{8,64}$");
        body["device_name"]!.GetValue<string>().Should().Be("Касса 2");
        body["branch"]!.GetValue<string>().Should().Be("11111111-1111-1111-1111-111111111111");

        var firstKey = body["device_key"]!.GetValue<string>();
        DeviceIdentity.ResetForTests(); // «перезапуск»: ключ читается из файла и не меняется
        var again = new JsonObject();
        DeviceIdentity.ApplyTo(again);
        again["device_key"]!.GetValue<string>().Should().Be(firstKey);

        DeviceIdentity.BranchProvider = () => null;
        var noBranch = new JsonObject();
        DeviceIdentity.ApplyTo(noBranch);
        noBranch.ContainsKey("branch").Should().BeFalse();
        try { Directory.Delete(dir, true); } catch { /* временная папка */ }
    }

    [Fact]
    public void QuickCheckoutBody_Includes_DeviceFields_WithFeature()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-device-" + Guid.NewGuid().ToString("N")[..8]);
        DeviceIdentity.DirOverride = dir;
        DeviceIdentity.ResetForTests();
        DeviceIdentity.NameProvider = () => "ПК-1";
        DeviceIdentity.BranchProvider = () => null;
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.CheckoutBranch);
        var cart = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = 1, ["unit_price"] = 10.0 }),
        };
        var body = QuickCheckoutBody.TryBuild(cart.ToJsonString(), "cash", "10", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
        body.Should().NotBeNull();
        body!["device_key"].Should().NotBeNull();
        body["device_name"]!.GetValue<string>().Should().Be("ПК-1");
        try { Directory.Delete(dir, true); } catch { }
    }

    // ───────────── 3. warnings и коды ошибок ─────────────

    [Fact]
    public void CheckoutWarnings_Parse_LinesMergedAndBelowCost()
    {
        using var doc = JsonDocument.Parse("""{"id":"s1","warnings":[{"code":"lines_merged","index":1,"into":0},{"code":"below_cost","index":2,"product_id":"p","price":"5.00","purchase_price":"7"},{"code":"negative_stock"}]}""");
        var warnings = CheckoutWarnings.Parse(doc.RootElement);
        warnings.Should().HaveCount(3);
        warnings[0].Should().Be(new CheckoutWarning("lines_merged", 1, null, null, null, 0));
        warnings[1].Price.Should().Be(5m);
        warnings[1].PurchasePrice.Should().Be(7m);
        CheckoutWarnings.Has(warnings, CheckoutWarnings.BelowCost).Should().BeTrue();
        CheckoutWarnings.BelowCostNote(warnings, "12345").Should().Contain("5").And.Contain("7");
        using var none = JsonDocument.Parse("""{"id":"s1"}""");
        CheckoutWarnings.Parse(none.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void ServerErrorCodes_FriendlyTexts_AreKnownCodesOnly()
    {
        static ApiException Err(int status, string json) => new("серверный текст", status, JsonDocument.Parse(json).RootElement.Clone());
        ServerErrorCodes.Friendly(Err(400, """{"code":"duplicate_line"}""")).Should().Be(ServerErrorCodes.DuplicateLineText());
        ServerErrorCodes.Friendly(Err(409, """{"code":"negative_stock"}""")).Should().Be(ServerErrorCodes.NegativeStockText());
        ServerErrorCodes.Friendly(Err(409, """{"code":"has_returns"}""")).Should().Be(ServerErrorCodes.HasReturnsText());
        ServerErrorCodes.Friendly(Err(409, """{"code":"debt_already_paid"}""")).Should().Be(ServerErrorCodes.DebtAlreadyPaidText());
        ServerErrorCodes.Friendly(Err(403, """{"code":"read_only"}""")).Should().Be(ServerErrorCodes.ReadOnlyText());
        ServerErrorCodes.Friendly(Err(400, """{"code":"что-то_другое"}""")).Should().BeNull();
        ServerErrorCodes.Friendly(Err(400, """{"code":"pin_invalid","attempts_left":3}""")).Should().Contain("3");
        ServerErrorCodes.Friendly(Err(429, """{"code":"pin_locked","retry_after":900}""")).Should().Contain("15");
        ServerErrorCodes.RetryAfterSeconds(Err(429, """{"code":"pin_locked","retry_after":900}""")).Should().Be(900);
        ServerOutageMonitor.FriendlyError(Err(400, """{"code":"duplicate_line"}"""), showServerText: true).Should().Be(ServerErrorCodes.DuplicateLineText());
    }

    // ───────────── 4. удаление прихода: версия, 428, 412 ─────────────

    private static NurMarketApiClient Client(WireMockServer server) => new(new AppSettings { ApiBaseUrl = server.Url! });

    [Fact]
    public async Task DeleteReceipt_SendsVersion_Retries428_And412WithNewKey()
    {
        using var server = WireMockServer.Start();
        // вход
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        // чтение прихода: версия 3
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r1/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r1\",\"version\":3,\"items\":[]}"));
        // DELETE с If-Match "3" → 412 (версия устарела)
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r1/").UsingMethod("DELETE").WithHeader("If-Match", "\"3\""))
            .RespondWith(Response.Create().WithStatusCode(412).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"version_conflict\"}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        var svc = new SupplierApiService(api);

        // известная версия 3 → 412 → 2026-11-06 (Л5-13): касса НЕ повторяет удаление молча (в приход могли добавить строки на другом устройстве) — сообщает расхождение
        var act = async () => await svc.DeleteReceiptAsync("r1", "причина теста", "key-1", default, knownVersion: 3);
        await act.Should().ThrowAsync<ReceiptChangedException>();
        var deletes = server.LogEntries.Where(e => e.RequestMessage.Method == "DELETE").ToList();
        deletes.Should().HaveCount(1);
        deletes[0].RequestMessage.Headers!["If-Match"].Single().Should().Be("\"3\"");
        deletes[0].RequestMessage.Headers!["Idempotency-Key"].Single().Should().Be("key-1");
        using var sent = JsonDocument.Parse(deletes[0].RequestMessage.Body!);
        sent.RootElement.GetProperty("version").GetInt32().Should().Be(3);
        sent.RootElement.GetProperty("reason").GetString().Should().Be("причина теста");
    }

    [Fact]
    public async Task DeleteReceipt_428_RereadsVersionFromAnswerAndRetries()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        // сервер приход не отдаёт с версией (0) → DELETE уходит без версии → 428 current_version 4 → повтор с If-Match "4"
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r3/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r3\"}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r3/").UsingMethod("DELETE").WithHeader("If-Match", "\"4\"")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r3/").UsingMethod("DELETE")).AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(428).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"version_required\",\"current_version\":4}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        await new SupplierApiService(api).DeleteReceiptAsync("r3", "причина теста", "key-3");
        var deletes = server.LogEntries.Where(e => e.RequestMessage.Method == "DELETE").ToList();
        deletes.Should().HaveCount(2);
        deletes[1].RequestMessage.Headers!["If-Match"].Single().Should().Be("\"4\"");
        deletes[1].RequestMessage.Headers!["Idempotency-Key"].Single().Should().Be("key-3", "428 — запрос отклонён до обработки, ключ тот же");
    }

    [Fact]
    public async Task DeleteReceipt_Without428Version_ReadsReceiptFirst()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r2/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r2\",\"version\":5}"));
        server.Given(Request.Create().WithPath("/api/main/suppliers/receipts/r2/").UsingMethod("DELETE"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        await new SupplierApiService(api).DeleteReceiptAsync("r2", "причина теста", "key-2");
        var delete = server.LogEntries.Single(e => e.RequestMessage.Method == "DELETE");
        delete.RequestMessage.Headers!["If-Match"].Single().Should().Be("\"5\"");
    }

    // ───────────── 5. прокат: Idempotency-Key и replayed ─────────────

    [Fact]
    public async Task RentalCreate_SendsIdempotencyKey_AndTreatsReplayedAsSuccess()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/rentals/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"rent-1\",\"number\":7,\"status\":\"active\",\"replayed\":true,\"items\":[]}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        var created = await new RentalsApi(api).CreateAsync("c1", [new RentalItem("p1", null, "Лыжи", "", "", 1)], DateTime.Today, DateTime.Today.AddDays(1), "day", "money", 100, "cash", null, null);
        created.Number.Should().Be(7);
        created.Replayed.Should().BeTrue();
        var post = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/rentals/");
        post.RequestMessage.Headers!["Idempotency-Key"].Single().Should().NotBeNullOrWhiteSpace();
    }

    // ───────────── 6. архив: фильтр is_archived ─────────────

    [Fact]
    public async Task ArchivedList_AsksServerForArchiveOnly()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/main/products/list/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\":1,\"next\":null,\"results\":[{\"id\":\"a1\",\"name\":\"Архивный\",\"is_archived\":true}]}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        var found = await new CatalogApiService(api).ListArchivedProductsAsync();
        found.Should().HaveCount(1);
        var query = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/main/products/list/").RequestMessage.Query!;
        query["is_archived"].Single().Should().Be("true");
    }

    // ───────────── 7. PIN ─────────────

    [Fact]
    public async Task OwnerPin_PinInvalid_GivesAttemptsLeft_AndPinLockedDoesNotThrottleWholeApp()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*1111*"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"pin_invalid\",\"attempts_left\":2}"));
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*2222*"))
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithHeader("Retry-After", "900").WithBody("{\"code\":\"pin_locked\",\"retry_after\":900}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        var sales = new SalesApiService(api);

        var wrong = await sales.VerifyOwnerPinAsync("1111");
        wrong.Rejected.Should().BeTrue();
        wrong.AttemptsLeft.Should().Be(2);

        var locked = async () => await sales.VerifyOwnerPinAsync("2222");
        var ex = (await locked.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be(429);
        ServerErrorCodes.RetryAfterSeconds(ex).Should().Be(900);
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/" && e.RequestMessage.Body!.Contains("2222")).Should().Be(1, "блокировку PIN не повторяем как «слишком частые запросы»");
        ApiThrottle.RemainingBlock.Should().BeLessThan(TimeSpan.FromSeconds(2), "блокировка PIN не ставит паузу на всю кассу");
    }

    // ───────────── 8. журнал идемпотентности ─────────────

    [Theory]
    [InlineData("done", 201, IdempotencyVerdict.Done)]
    [InlineData("in_progress", null, IdempotencyVerdict.InProgress)]
    [InlineData("failed", 500, IdempotencyVerdict.FailedRetry)]
    public void IdempotencyJournal_Decide_ByState(string state, int? status, IdempotencyVerdict expected) =>
        IdempotencyJournal.Decide(new IdempotencyRecord(true, state, status, null, "POST /main/branch-transfers/")).Should().Be(expected);

    [Fact]
    public void IdempotencyJournal_Decide_UnknownKeyAndOldServer()
    {
        IdempotencyJournal.Decide(new IdempotencyRecord(false, null, null, "idempotency_key_not_found", null)).Should().Be(IdempotencyVerdict.NotSent);
        IdempotencyJournal.Decide(null).Should().Be(IdempotencyVerdict.Unknown);
    }

    [Fact]
    public async Task IdempotencyJournal_Check_AsksServerOnlyWithFeature()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/main/idempotency/k1/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"found\":true,\"state\":\"in_progress\"}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");

        (await IdempotencyJournal.CheckAsync(api, "k1")).Should().Be(IdempotencyVerdict.Unknown, "без признака сервер не спрашиваем");
        server.LogEntries.Count(e => e.RequestMessage.Path.Contains("idempotency")).Should().Be(0);

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.IdempotencyLookupBonusTransfers);
        (await IdempotencyJournal.CheckAsync(api, "k1")).Should().Be(IdempotencyVerdict.InProgress);
    }

    // ───────────── 9. весовые строки: FLOOR на каждой строке ─────────────

    private static CartTotalsCalculator.CartTotals Calc(params (double Qty, double Price, bool Weighed, double? DiscountPercent)[] lines)
    {
        var items = new JsonArray();
        var i = 0;
        foreach (var l in lines)
        {
            var item = new JsonObject { ["product_id"] = "p" + i++, ["quantity"] = l.Qty, ["unit_price"] = l.Price, ["server_is_weight"] = l.Weighed };
            if (l.DiscountPercent is { } pct)
                item["discount_percent"] = pct;
            items.Add(item);
        }
        using var doc = JsonDocument.Parse(new JsonObject { ["items"] = items }.ToJsonString());
        return CartTotalsCalculator.Calculate(doc.RootElement);
    }

    [Fact]
    public void WeightedLine_FloorsEachLine_WhenFeatureOn_ElseSumFloor()
    {
        // два весовых товара по 10,005: по правилу «вниз от суммы» — 20,01; сервер с weight_line_total_floor — 10,00 + 10,00
        QuantityRules.WeightLineFloorEnabled = () => false;
        Calc((1.0, 10.005, true, null), (1.0, 10.005, true, null)).Subtotal.Should().Be(20.01);
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 0.01m;
        Calc((1.0, 10.005, true, null), (1.0, 10.005, true, null)).Subtotal.Should().Be(20.00);
        // пример сервера: 0,486 × 66 = 32,076 → 32,07; плюс обычный товар 3,00 → 35,07
        Calc((0.486, 66.0, true, null), (1.0, 3.0, false, null)).TotalDue.Should().Be(35.07);
    }

    [Fact]
    public void WeightedLine_WithPromo_MatchesServerExample()
    {
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 0.01m;
        // 66,00 × 0,343 = 22,638 → 22,63; скидка акции 15 % от точной суммы = 3,3957 → 3,40; итог 19,23
        var totals = Calc((0.343, 66.0, true, 15.0));
        totals.Subtotal.Should().Be(22.63);
        totals.LineDiscounts.Should().Be(3.40);
        totals.TotalDue.Should().Be(19.23);
    }

    [Fact]
    public void WeightedLine_StepSom_FloorsToWholeSom()
    {
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 1m;
        Calc((0.343, 66.0, true, null)).Subtotal.Should().Be(22.0);
    }

    [Fact]
    public void QuickBody_ComputeTotal_EqualsCartTotals_ForWeighedLines()
    {
        QuantityRules.WeightLineFloorEnabled = () => true;
        QuantityRules.WeightStep = () => 0.01m;
        Gen.Select(Gen.Int[1, 9000], Gen.Int[100, 20000], Gen.Int[1, 9000], Gen.Int[100, 20000], (q1, p1, q2, p2) => (q1, p1, q2, p2)).Sample(t =>
        {
            var cart = new JsonObject
            {
                ["items"] = new JsonArray(
                    new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = t.q1 / 1000.0, ["unit_price"] = t.p1 / 100.0, ["server_is_weight"] = true, ["is_weight"] = true },
                    new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["quantity"] = t.q2 / 1000.0, ["unit_price"] = t.p2 / 100.0, ["server_is_weight"] = true, ["is_weight"] = true }),
            };
            using var doc = JsonDocument.Parse(cart.ToJsonString());
            var totals = CartTotalsCalculator.Calculate(doc.RootElement);
            var body = QuickCheckoutBody.TryBuild(cart.ToJsonString(), "cash", "100000", null, null, Guid.NewGuid().ToString(), null, false, null, false, out _);
            body.Should().NotBeNull();
            QuickCheckoutBody.ComputeTotal(body!).Should().Be(totals.TotalDue);
            // независимая модель: каждая весовая строка вниз до копейки, сумма строк = подытог
            var expected = Math.Floor(t.q1 / 1000m * (t.p1 / 100m) * 100m) / 100m + Math.Floor(t.q2 / 1000m * (t.p2 / 100m) * 100m) / 100m;
            ((decimal)totals.Subtotal).Should().Be(expected);
        }, iter: 200);
    }

    // ───────────── 10. capabilities: только после входа ─────────────

    [Fact]
    public void ServerCapabilities_Has_FalseUntilLoaded_AndAfterReset()
    {
        ServerCapabilities.Reset();
        ServerCapabilities.Has(ServerCapabilities.DebtReturnDrawerCash).Should().BeFalse("список не получен — признаков «нет»");
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.DebtReturnDrawerCash);
        ServerCapabilities.Has(ServerCapabilities.DebtReturnDrawerCash).Should().BeTrue();
        ServerCapabilities.Reset(); // смена аккаунта
        ServerCapabilities.Has(ServerCapabilities.DebtReturnDrawerCash).Should().BeFalse();
    }
}
