using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.ServerCeba67c;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-19 (ответ Умара 19.11, «закупка, признаки, очередь 7 суток»): подтверждение отправки старых офлайн-чеков PIN-ом владельца/админа
/// (offline_accept_late, users/auth/pin/verify/ → X-Confirm-Token, accept_late в теле), разбор ошибок (offline_too_old, pin_invalid, pin_not_set, 429),
/// токен не попадает в журнал, предупреждения принятого из очереди чека (below_cost / negative_stock / stock_negative), 400 по payment.received,
/// возврат закупочной цены после удаления прихода (deltas.purchase_price). Сервер — WireMock.</summary>
[Collection("ServerFeatures")]
public sealed class LateAcceptConfirm1119Tests : IDisposable
{
    private const string Secret = "tok-SECRET-1119";
    private readonly List<string> _log = new();
    private readonly Action<string, string>? _oldMirror = PosLogger.Mirror;
    private readonly List<string> _entries = new();

    public LateAcceptConfirm1119Tests()
    {
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.OfflineAcceptLate);
        PosLogger.Mirror = (cat, msg) => { lock (_log) _log.Add(cat + " " + msg); };
        OutboxSentNotes.Clear();
        SyncService.ResetLatePinLockForTests();
    }

    public void Dispose()
    {
        SyncService.ResetLatePinLockForTests(); // пауза после 429 общая на программу — не оставляем её другим тестам
        PosLogger.Mirror = _oldMirror;
        ServerCapabilities.SetFeaturesForTests();
        PurchasePriceDelta.Notifier = null;
        ProductActions.RequestCatalogRefresh = null;
        OutboxSentNotes.Clear();
        foreach (var id in _entries)
            OfflinePendingSalesStore.RemoveSynced(id);
    }

    private static NurMarketApiClient Client(WireMockServer server) => new(new AppSettings { ApiBaseUrl = server.Url! });

    private static async Task<(WireMockServer Server, SalesApiService Sales)> StartAsync()
    {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        return (server, new SalesApiService(api));
    }

    private static void Json(WireMockServer server, string method, string path, int status, string body, int priority = 5) =>
        server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    // product_id по названию — чтобы заглушка сервера могла отличить чеки по телу запроса (название товара в запрос не уходит)
    private static string Pid(string name) => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToString();

    private static string Cart(string name) => new JsonObject
    {
        ["items"] = new JsonArray(new JsonObject { ["product_id"] = Pid(name), ["product_name"] = name, ["quantity"] = 2, ["unit_price"] = 30.0 }),
    }.ToJsonString();

    /// <summary>Чек из очереди, который сервер уже отклонил как «старше 7 суток» (offline_too_old): отметка TooOldForServer, «Требует проверки».</summary>
    private OfflineSaleEntry OldEntry(string name, int daysOld = 10)
    {
        var e = new OfflineSaleEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            PaymentMethod = "cash",
            CashReceived = "100",
            CartJson = Cart(name),
            CreatedAt = DateTimeOffset.Now.AddDays(-daysOld),
            Status = OfflineSaleEntry.Failed,
            ReviewNeeded = true,
            TooOldForServer = true,
            LastError = "Старше 7 суток",
        };
        OfflinePendingSalesStore.Append(e);
        _entries.Add(e.Id);
        return e;
    }

    private static JsonNode? BodyOf(WireMock.Logging.ILogEntry e) => JsonNode.Parse(e.RequestMessage.Body ?? "{}");

    private static SyncService Sync(SalesApiService sales) => new(sales, null!, null!, null!, null!, null!);

    // ───────────── PIN: токен, ошибки ─────────────

    [Fact]
    public async Task PinVerify_Ok_ReadsConfirmTokenAndExpiresIn()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/users/auth/pin/verify/", 200, $"{{\"token\":\"{Secret}\",\"expires_in\":300}}");
        var r = await sales.VerifyOwnerPinAsync("1234");
        r.Accepted.Should().BeTrue();
        r.Token.Should().Be(Secret);
        r.ExpiresIn.Should().Be(300);

        var sync = Sync(sales);
        var late = await sync.VerifyOwnerPinForLateAsync("1234");
        late.Status.Should().Be(LatePinStatus.Ok);
        late.ExpiresInSeconds.Should().Be(300);
        lock (_log) _log.Should().NotContain(l => l.Contains(Secret) || l.Contains("1234"), "ни токен, ни PIN в журнал не пишутся");
    }

    [Fact]
    public async Task PinVerify_PinNotSet_IsAnErrorWithHint_NotAWrongPin()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/users/auth/pin/verify/", 400, "{\"code\":\"pin_not_set\"}");
        (await sales.VerifyOwnerPinAsync("1234")).NotSet.Should().BeTrue();

        var late = await Sync(sales).VerifyOwnerPinForLateAsync("1234");
        late.Status.Should().Be(LatePinStatus.Error, "PIN не задан — отказ без обхода и без траты попытки");
        late.Message.Should().Contain("PIN не задан").And.Contain("Настройки → Сотрудники");
    }

    [Fact]
    public async Task PinVerify_PinInvalid_ShowsServerAttemptsLeft_AndBlock429IsLockedFor15Minutes()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*1111*"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"pin_invalid\",\"attempts_left\":3}"));
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*2222*"))
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Content-Type", "application/json").WithHeader("Retry-After", "900").WithBody("{\"code\":\"pin_locked\",\"retry_after\":900}"));
        var sync = Sync(sales);

        var wrong = await sync.VerifyOwnerPinForLateAsync("1111");
        wrong.Status.Should().Be(LatePinStatus.Wrong);
        wrong.Message.Should().Contain("Осталось попыток: 3");

        var locked = await sync.VerifyOwnerPinForLateAsync("2222");
        locked.Status.Should().Be(LatePinStatus.Locked);
        locked.Message.Should().Contain("15 мин");
        var again = await sync.VerifyOwnerPinForLateAsync("2222");
        again.Status.Should().Be(LatePinStatus.Locked);
        server.LogEntries.Count(e => (e.RequestMessage.Body ?? "").Contains("2222")).Should().Be(1, "до конца блокировки новых запросов не шлём");
    }

    // ───────────── сериализация запроса: accept_late + X-Confirm-Token ─────────────

    [Fact]
    public async Task LateAccept_SendsAcceptLateBodyAndConfirmTokenHeader_SameIdempotencyKey_AndKeepsSecretOutOfTheLog()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 201, "{\"id\":\"sale-1\",\"warnings\":[{\"code\":\"below_cost\",\"index\":0,\"price\":5,\"purchase_price\":9}],\"stock_negative\":[{\"name\":\"Молоко\"}]}");
        var entry = OldEntry("Хлеб");
        var sync = Sync(sales);

        // чек старше 7 суток, сервер сообщил признак: можно подтвердить
        SyncService.LateAcceptEnabled.Should().BeTrue();
        SyncService.GetLateAcceptState(entry).Should().Be(LateAcceptState.Allowed);

        var result = await sync.SendLateAcceptedAsync(entry.Id, Secret, "Владелец");

        result.Ok.Should().BeTrue(result.Message);
        result.SentWithWarnings.Should().BeTrue();
        var req = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/main/pos/checkout/");
        req.RequestMessage.Headers!["X-Confirm-Token"].Single().Should().Be(Secret);
        req.RequestMessage.Headers["Idempotency-Key"].Single().Should().Be(entry.Id, "тот же ключ, что у автоотправки: повтор не задвоит продажу");
        var body = BodyOf(req)!;
        body["accept_late"]!.GetValue<bool>().Should().BeTrue();
        body["offline"]!.GetValue<bool>().Should().BeTrue();
        body["sold_at"].Should().NotBeNull("настоящая дата продажи");

        // «Отправлено, но…» с названиями товаров; в журнале пометка есть, токена и PIN нет
        var note = OutboxSentNotes.Recent.Should().ContainSingle(n => n.EntryId == entry.Id).Subject.Text;
        note.Should().Contain("отправлен, но").And.Contain("ниже закупки").And.Contain("Хлеб").And.Contain("в минус по остатку").And.Contain("Молоко");
        lock (_log)
        {
            _log.Should().Contain(l => l.Contains("отправлен, но"));
            _log.Should().NotContain(l => l.Contains(Secret), "токен подтверждения нигде не пишется");
        }
        OfflinePendingSalesStore.TryGetById(entry.Id).Should().BeNull("принятый чек ушёл из очереди");
    }

    [Fact]
    public async Task LateAccept_WithoutToken_ForSignedInOwner_StillSendsAcceptLate_NoHeader()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 201, "{\"id\":\"sale-2\"}");
        var entry = OldEntry("Сахар");

        var result = await Sync(sales).SendLateAcceptedAsync(entry.Id, null, "Владелец");

        result.Ok.Should().BeTrue(result.Message);
        var req = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/main/pos/checkout/");
        req.RequestMessage.Headers!.ContainsKey("X-Confirm-Token").Should().BeFalse("владелец/админ в системе — сервер токен не требует");
        BodyOf(req)!["accept_late"]!.GetValue<bool>().Should().BeTrue();
        result.SentWithWarnings.Should().BeFalse();
    }

    [Fact]
    public async Task LateAccept_TokenRejected_KeepsReceiptInQueue_AndAsksForPinAgain()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 400, "{\"code\":\"offline_too_old\",\"max_days\":7,\"late_max_days\":90}");
        var entry = OldEntry("Чай");

        var result = await Sync(sales).SendLateAcceptedAsync(entry.Id, "expired-token", "Кассир");

        result.Ok.Should().BeFalse();
        result.TokenRejected.Should().BeTrue("чек моложе 90 суток, а сервер снова offline_too_old — подтверждение не принято");
        result.Message.Should().Contain("Старше 7 суток — нужно подтверждение");
        var left = OfflinePendingSalesStore.TryGetById(entry.Id)!;
        left.ReviewNeeded.Should().BeTrue();
        left.LastError.Should().Contain("Старше 7 суток — нужно подтверждение");
    }

    [Fact]
    public async Task LateAccept_Older_Than90Days_IsNotSent_AndSaysContactSupport()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        var entry = OldEntry("Соль", daysOld: 100);

        SyncService.GetLateAcceptState(entry).Should().Be(LateAcceptState.TooOld);
        var result = await Sync(sales).SendLateAcceptedAsync(entry.Id, "tok", null);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("Старше 90 суток — отправить нельзя, обратитесь в поддержку");
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/pos/checkout/").Should().Be(0, "старше 90 суток запрос вообще не уходит");

        var item = new SalesQueueOutboxSource().Snapshot().Single(i => i.Id == entry.Id);
        item.CanConfirm.Should().BeFalse();
        item.Reason.Should().Contain("Старше 90 суток");
    }

    // ───────────── пакет: один токен на несколько чеков ─────────────

    [Fact]
    public async Task Batch_OneToken_SendsAllReceiptsOldestFirst_AndStopsWhenServerRejectsTheToken()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 201, "{\"id\":\"sale-ok\"}");
        // чек «Second» сервер не принимает (токен отклонён) — пакет останавливается, «Третий» не тронут
        server.Given(Request.Create().WithPath("/api/main/pos/checkout/").UsingMethod("POST").WithBody("*" + Pid("Second") + "*")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"forbidden\"}"));
        var first = OldEntry("First", 12);
        var second = OldEntry("Second", 11);
        var third = OldEntry("Third", 10);
        var sync = Sync(sales);

        var result = await sync.SendLateAcceptedBatchAsync(new[] { first.Id, second.Id, third.Id }, Secret, 300, "Владелец");

        result.Sent.Should().Be(1);
        result.Failed.Should().Be(1);
        result.Remaining.Should().Be(1, "третий чек не тронут: нужно подтверждение заново");
        result.TokenExpired.Should().BeTrue();
        result.Message.Should().Contain("Отправлено: 1").And.Contain("введите PIN ещё раз");
        var checkouts = server.LogEntries.Where(e => e.RequestMessage.Path == "/api/main/pos/checkout/").ToList();
        checkouts.Should().HaveCount(2);
        checkouts.Should().OnlyContain(e => e.RequestMessage.Headers!["X-Confirm-Token"].Single() == Secret, "одно подтверждение на весь пакет");
        OfflinePendingSalesStore.TryGetById(third.Id).Should().NotBeNull();
        lock (_log) _log.Should().NotContain(l => l.Contains(Secret));
    }

    [Fact]
    public async Task Batch_Success_SendsAllWithTheSameToken()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 201, "{\"id\":\"sale-ok\"}");
        var ids = new[] { OldEntry("А", 20).Id, OldEntry("Б", 15).Id, OldEntry("В", 9).Id };

        var result = await Sync(sales).SendLateAcceptedBatchAsync(ids, Secret, 300, "Владелец");

        result.Sent.Should().Be(3);
        result.Failed.Should().Be(0);
        result.Remaining.Should().Be(0);
        result.Message.Should().Be("Отправлено: 3.");
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/pos/checkout/").Should().Be(3);
    }

    // ───────────── «Неотправленное»: статус и кнопка ─────────────

    [Fact]
    public void OutboxItem_OldReceipt_IsMarkedNeedsConfirmation_OnlyWhenServerReportedTheFeature()
    {
        var entry = OldEntry("Масло");
        var item = new SalesQueueOutboxSource().Snapshot().Single(i => i.Id == entry.Id);
        item.CanConfirm.Should().BeTrue();
        item.Status.Should().Be(OutboxStatus.Error);
        item.Reason.Should().Contain("Старше 7 суток — нужно подтверждение");

        ServerCapabilities.SetFeaturesForTests(); // сервер признака не сообщил — прежнее поведение
        var before = new SalesQueueOutboxSource().Snapshot().Single(i => i.Id == entry.Id);
        before.CanConfirm.Should().BeFalse();
        before.Reason.Should().NotContain("нужно подтверждение");
        before.Hint.Should().Contain("Некорректные чеки");
    }

    // ───────────── разбор ошибок ─────────────

    [Theory]
    [InlineData("{\"features\":{\"offline_accept_late\":true,\"other\":false}}", true)]
    [InlineData("{\"capabilities\":{\"features\":{\"offline_accept_late\":true}}}", true)]
    [InlineData("{\"capabilities\":{\"features\":[\"offline_accept_late\"]}}", true)]
    [InlineData("{\"features\":{\"offline_accept_late\":false}}", false)]
    public void Capabilities_Parse_FindsOfflineAcceptLate(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        ServerCapabilities.Parse(doc.RootElement).Contains(ServerCapabilities.OfflineAcceptLate).Should().Be(expected);
    }

    [Fact]
    public void OfflineTooOld_And_PinErrors_HaveReadableTexts()
    {
        var tooOld = new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"offline_too_old\",\"max_days\":7,\"late_max_days\":90}").RootElement.Clone());
        SyncService.IsOfflineTooOld(tooOld, new OfflineSaleEntry { CreatedAt = DateTimeOffset.Now.AddDays(-9) }).Should().BeTrue();
        ServerErrorCodes.Friendly(new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"pin_not_set\"}").RootElement.Clone())).Should().Contain("Настройки → Сотрудники");
        ServerErrorCodes.Friendly(new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"pin_invalid\",\"attempts_left\":4}").RootElement.Clone())).Should().Contain("Осталось попыток: 4");
        ServerErrorCodes.Friendly(new ApiException("x", 429, JsonDocument.Parse("{\"code\":\"pin_locked\",\"retry_after\":900}").RootElement.Clone())).Should().Contain("15 мин");
    }

    [Fact]
    public void PaymentReceived_ValidationError_IsFriendly_OtherValidationErrorsAreNot()
    {
        var pay = new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"validation_error\",\"fields\":{\"payment.received\":[\"Не более 12 цифр\"]}}").RootElement.Clone());
        ServerErrorCodes.IsPaymentReceivedError(pay).Should().BeTrue();
        ServerErrorCodes.Friendly(pay).Should().Contain("Проверьте сумму оплаты").And.Contain("12 цифр");
        var other = new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"validation_error\",\"fields\":{\"client\":[\"нужен\"]}}").RootElement.Clone());
        ServerErrorCodes.IsPaymentReceivedError(other).Should().BeFalse();
        ServerErrorCodes.Friendly(other).Should().BeNull();
    }

    [Fact]
    public async Task QueuedReceipt_PaymentReceivedTooLong_StaysInQueueMarkedCheckPaymentAmount()
    {
        var (server, sales) = await StartAsync();
        using var _ = server;
        Json(server, "POST", "/api/main/pos/checkout/", 400, "{\"code\":\"validation_error\",\"fields\":{\"payment.received\":[\"Не более 12 цифр\"]}}");
        var id = Guid.NewGuid().ToString("N");
        OfflinePendingSalesStore.Append(new OfflineSaleEntry
        {
            Id = id, PaymentMethod = "cash", CashReceived = "1000000000000000", CartJson = Cart("Хлеб"), QuickCheckoutAttempted = true, CreatedAt = DateTimeOffset.Now.AddMinutes(-3),
        });
        _entries.Add(id);
        var sync = Sync(sales);
        var entry = OfflinePendingSalesStore.TryGetById(id)!;
        var call = typeof(SyncService).GetMethod("TryQuickReplayAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Exception? deferred = null;
        try { await (Task)call.Invoke(sync, new object[] { entry, CancellationToken.None })!; }
        catch (Exception ex) { deferred = ex; }
        sync.Dispose();
        deferred.Should().NotBeNull().And.BeOfType<ReplayDeferredException>();
        deferred!.Message.Should().StartWith("Проверьте сумму оплаты");
        ((ReplayDeferredException)deferred).NeedsReview.Should().BeTrue("чек ждёт решения человека, а не бесконечно повторяется");

        var left = OfflinePendingSalesStore.TryGetById(id)!;
        left.ReviewNeeded.Should().BeTrue();
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/pos/checkout/").Should().Be(1, "старым путём не идём — то же значение дало бы ту же ошибку");
    }

    // ───────────── предупреждения принятого чека ─────────────

    [Fact]
    public void SentWarnings_BuildText_NamesProductsFromTheCart_AndIgnoresCleanResponses()
    {
        var cart = new JsonObject { ["items"] = new JsonArray(
            new JsonObject { ["product_id"] = "p1", ["product_name"] = "Хлеб", ["quantity"] = 1, ["unit_price"] = 30.0 },
            new JsonObject { ["product_id"] = "p2", ["product_name"] = "Сыр", ["quantity"] = 1, ["unit_price"] = 90.0 }) }.ToJsonString();
        using var doc = JsonDocument.Parse("{\"warnings\":[{\"code\":\"below_cost\",\"index\":1},{\"code\":\"negative_stock\",\"index\":0}],\"stock_negative\":true}");
        var text = OutboxSentNotes.BuildText("abcdef123456", doc.RootElement.Clone(), cart)!;
        text.Should().Contain("продано ниже закупки — Сыр").And.Contain("ушло в минус по остатку — Хлеб");

        using var clean = JsonDocument.Parse("{\"id\":\"s\",\"warnings\":[{\"code\":\"lines_merged\",\"index\":1,\"into\":0}]}");
        OutboxSentNotes.BuildText("abcdef123456", clean.RootElement.Clone(), cart).Should().BeNull();
    }

    [Fact]
    public void SentWarnings_ServerForm_NegativeStockWithoutIndex_UsesProductId_AndOldCodeIsAccepted()
    {
        // 2026-11-19 (ответ Умара 19.11, п. 8): warnings[{code:"negative_stock", index:null, product_id, variant_id, balance}] + stock_negative[{product_id, variant_id, balance}]; номера строки нет
        using var doc = JsonDocument.Parse("{\"warnings\":[{\"code\":\"negative_stock\",\"index\":null,\"product_id\":\"p-unknown-1\",\"variant_id\":null,\"balance\":\"-2.000\"}],\"stock_negative\":[{\"product_id\":\"p-unknown-1\",\"variant_id\":null,\"balance\":\"-2.000\"}]}");
        var text = OutboxSentNotes.BuildText("abcdef123456", doc.RootElement.Clone(), null)!;
        text.Should().Contain("ушло в минус по остатку — p-unknown-1", "строки нет — по product_id; один товар не дублируется из двух полей").And.NotContain("p-unknown-1, p-unknown-1");
        CheckoutWarnings.Parse(doc.RootElement.Clone()).Single().Should().Match<CheckoutWarning>(w => w.Code == "negative_stock" && w.Index == null && w.ProductId == "p-unknown-1");

        using var old = JsonDocument.Parse("{\"warnings\":[{\"code\":\"stock_negative\",\"product_id\":\"p2\"}]}");
        CheckoutWarnings.Has(CheckoutWarnings.Parse(old.RootElement.Clone()), CheckoutWarnings.NegativeStock).Should().BeTrue("старое имя кода принимаем наравне с negative_stock");
    }

    // ───────────── удаление прихода: закупочная цена вернулась ─────────────

    [Fact]
    public void PurchasePriceDelta_ArrayFromServer_ThreeLinesAndPlusN()
    {
        using var doc = JsonDocument.Parse("{\"deltas\":{\"purchase_price\":[" +
            "{\"product_id\":\"1\",\"name\":\"А\",\"before\":1,\"after\":35,\"proposed\":35,\"applied\":true,\"latest\":35}," +
            "{\"product_id\":\"2\",\"name\":\"Б\",\"before\":2,\"after\":20,\"proposed\":20,\"applied\":true,\"latest\":20}," +
            "{\"product_id\":\"3\",\"name\":\"В\",\"before\":3,\"after\":30,\"proposed\":30,\"applied\":true,\"latest\":30}," +
            "{\"product_id\":\"4\",\"name\":\"Г\",\"before\":4,\"after\":40,\"proposed\":40,\"applied\":true,\"latest\":40}]}}");
        var msg = PurchasePriceDelta.Message(PurchasePriceDelta.Parse(doc.RootElement.Clone()))!;
        msg.Should().Contain("«А»").And.Contain("«Б»").And.Contain("«В»").And.NotContain("«Г»").And.EndWith("+1");
        msg.Split('\n').Length.Should().Be(5, "заголовок + 3 строки + «+1»");
    }

    [Fact]
    public void PurchasePriceDelta_Message_ForObjectAndArray_SkipsUnchanged()
    {
        using var one = JsonDocument.Parse("{\"deltas\":{\"purchase_price\":{\"before\":1,\"after\":35,\"applied\":true}}}");
        var msg = PurchasePriceDelta.Message(PurchasePriceDelta.Parse(one.RootElement))!;
        msg.Should().Contain("Закупочная цена возвращена").And.Contain("было 1").And.Contain("стало 35");

        using var many = JsonDocument.Parse("{\"deltas\":{\"purchase_price\":[{\"name\":\"Хлеб\",\"before\":10,\"after\":12,\"applied\":true},{\"name\":\"Сыр\",\"before\":5,\"after\":5,\"applied\":false}]}}");
        var text = PurchasePriceDelta.Message(PurchasePriceDelta.Parse(many.RootElement))!;
        text.Should().Contain("«Хлеб»").And.NotContain("Сыр");

        using var none = JsonDocument.Parse("{\"deltas\":{\"purchase_price\":{\"before\":7,\"after\":7,\"applied\":false}}}");
        PurchasePriceDelta.Message(PurchasePriceDelta.Parse(none.RootElement)).Should().BeNull();
        using var empty = JsonDocument.Parse("{}");
        PurchasePriceDelta.Message(PurchasePriceDelta.Parse(empty.RootElement)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteReceipt_ShowsRestoredPurchasePrice_AndRefreshesTheCatalog()
    {
        using var server = WireMockServer.Start();
        Json(server, "POST", "/api/users/auth/login/", 200, "{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}");
        Json(server, "GET", "/api/main/suppliers/receipts/r7/", 200, "{\"id\":\"r7\",\"version\":2}");
        Json(server, "DELETE", "/api/main/suppliers/receipts/r7/", 200, "{\"deltas\":{\"purchase_price\":{\"before\":1,\"after\":35,\"applied\":true}}}");
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        string? shown = null;
        var refreshed = 0;
        PurchasePriceDelta.Notifier = m => shown = m;
        ProductActions.RequestCatalogRefresh = () => refreshed++;

        await new SupplierApiService(api).DeleteReceiptAsync("r7", "причина теста", "key-7");

        shown.Should().NotBeNull().And.Contain("Закупочная цена возвращена: было").And.Contain("стало");
        refreshed.Should().Be(1, "карточка и кэш цены перечитываются");
    }
}
