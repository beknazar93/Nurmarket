using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): клиентская часть за признаком сервера. Сервер — заглушки WireMock по НАШЕМУ
/// предложенному контракту (docs/Отчеты/«ТЗ Умару — правка и аннулирование возвратных накладных 24 ч.md»; сервером не подтверждён): can_edit true/false,
/// истёк срок, pin_invalid / pin_locked, version_conflict, void, history; кнопки скрыты без признака; PIN не попадает в журнал и файлы; метка «АННУЛИРОВАН»
/// в Excel/Word; расчёт и тело правки возврата поставщику.</summary>
[Collection("ServerFeatures")]
public sealed class ReturnEdit24hTests : IDisposable
{
    private const string Pin = "739184"; // уникальные цифры — ищем их в журнале и файлах
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-ret24-" + Guid.NewGuid().ToString("N")[..8]);

    public ReturnEdit24hTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests();
        ReturnEditApi.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ServerCapabilities.SetFeaturesForTests();
        ServerCapabilities.DirOverride = null;
        ReturnEditApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<NurMarketApiClient> LoggedIn(WireMockServer server)
    {
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        return api;
    }

    private static void Reply(WireMockServer server, string path, string method, int status, string body, int priority = 5) =>
        server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private const string EditableDoc = "{\"id\":\"r1\",\"version\":3,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,\"closed_at\":\"2026-11-10T08:00:00Z\","
        + "\"editable_until\":\"2026-11-11T08:00:00Z\",\"server_now\":\"2026-11-11T03:00:00Z\",\"total_amount\":\"300.00\","
        + "\"items\":[{\"id\":\"l1\",\"product_id\":\"p1\",\"product_name\":\"Молоко\",\"qty\":\"2\",\"unit\":\"шт\",\"purchase_price\":\"150.00\",\"discount\":\"0\",\"note\":\"\"}]}";

    // ───────────── модель редактируемости ─────────────

    [Fact]
    public void State_CanEdit_FromServer_TimeLeftOnlyByServerTimes()
    {
        var received = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var s = ReturnEditState.Parse(J(EditableDoc), received);
        s.Version.Should().Be(3);
        s.CanEdit.Should().BeTrue();
        s.CanVoid.Should().BeTrue();
        s.HasServerFlags.Should().BeTrue();
        s.IsVoided.Should().BeFalse();
        // editable_until − server_now = 5 ч; часы устройства (2026-01-01!) не участвуют, только время с момента ответа
        s.TimeLeft(received).Should().Be(TimeSpan.FromHours(5));
        s.TimeLeft(received.AddHours(1)).Should().Be(TimeSpan.FromHours(4));
        s.TimeLeft(received.AddHours(10)).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void State_NoServerFields_EverythingHidden()
    {
        var s = ReturnEditState.Parse(J("{\"id\":\"r1\",\"total_amount\":\"100\"}"));
        s.HasServerFlags.Should().BeFalse();
        s.CanEdit.Should().BeFalse();
        s.TimeLeft().Should().BeNull();
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.SupplierReturnEdit24h);
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeFalse("сервер не прислал can_edit — кнопок нет");
        ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, s).Should().BeFalse();
    }

    [Fact]
    public void State_CanEditFalse_Expired_HidesButtons()
    {
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.SupplierReturnEdit24h);
        var s = ReturnEditState.Parse(J("{\"id\":\"r1\",\"version\":2,\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"edit_window_expired\"}"));
        s.BlockReason.Should().Be("edit_window_expired");
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeFalse();
        ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, s).Should().BeFalse();
    }

    [Fact]
    public void Gate_WithoutCapability_NoButtons_EvenIfDocumentAllows()
    {
        var s = ReturnEditState.Parse(J(EditableDoc));
        ServerCapabilities.SetFeaturesForTests();
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeFalse("нет признака — поведение как сегодня");
        ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, s).Should().BeFalse();
        ReturnEditGate.ShowHistory(ReturnDocKind.Supplier, s, isManager: true).Should().BeFalse();

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.PosReturnEdit24h);
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeFalse("признак возврата от клиента не включает возврат поставщику");
        ReturnEditGate.ShowEdit(ReturnDocKind.Customer, s).Should().BeTrue();

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.SupplierReturnEdit24h);
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeTrue();
        ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, s).Should().BeTrue();
        ReturnEditGate.ShowHistory(ReturnDocKind.Supplier, s, isManager: false).Should().BeFalse("журнал аудита — только руководителю");
        ReturnEditGate.ShowHistory(ReturnDocKind.Supplier, s, isManager: true).Should().BeTrue();
    }

    [Fact]
    public void Voided_Document_NoEditNoVoid()
    {
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.SupplierReturnEdit24h);
        var s = ReturnEditState.Parse(J("{\"id\":\"r1\",\"version\":4,\"status\":\"voided\",\"can_edit\":true,\"can_void\":true,\"voided_at\":\"2026-11-10T09:00:00Z\",\"void_reason\":\"ошибка\"}"));
        s.IsVoided.Should().BeTrue();
        s.CanEdit.Should().BeFalse();
        ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, s).Should().BeFalse();
        ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, s).Should().BeFalse();
    }

    // ───────────── PIN руководителя ─────────────

    [Fact]
    public async Task Confirm_Ok_SendsScopeObjectAndVersion_ReturnsToken()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 200, "{\"confirm_token\":\"ct-1\",\"expires_in\":120,\"approved_by_name\":\"Айбек\"}");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin);
        r.Ok.Should().BeTrue();
        r.Token.Should().Be("ct-1");
        r.ApprovedBy.Should().Be("Айбек");
        var body = J(server.LogEntries.Last(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/").RequestMessage.Body!);
        body.GetProperty("scope").GetString().Should().Be("return_edit");
        body.GetProperty("object_type").GetString().Should().Be("supplier"); // 2026-11-11: как в сводке Умара 11.11 (прежнее "supplier_return" — запасной вариант)
        body.GetProperty("object_id").GetString().Should().Be("r1");
        body.GetProperty("version").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Confirm_SessionTokensInReply_NotAccepted()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 200, "{\"access\":\"x\",\"refresh\":\"y\",\"token\":\"z\"}");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Customer, "s1", ReturnEditAction.Void, 1, Pin);
        r.Ok.Should().BeFalse("токен сессии нельзя подставлять в X-Confirm-Token");
    }

    [Fact]
    public async Task Confirm_PinInvalid_AttemptsLeftFromServer()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 400, "{\"code\":\"pin_invalid\",\"attempts_left\":2}");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin);
        r.Ok.Should().BeFalse();
        r.Error!.Failure.Should().Be(ReturnEditFailure.PinInvalid);
        r.Error.AttemptsLeft.Should().Be(2);
        r.Error.IsFinal.Should().BeFalse("можно ввести PIN ещё раз");
        ReturnEditErrors.Text(r.Error).Should().Contain("2");
    }

    [Fact]
    public async Task Confirm_PinLocked_NoMoreRequestsUntilServerSays()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 429, "{\"code\":\"pin_locked\",\"retry_after\":900}");
        var svc = new ReturnEditApi(api);
        var r = await svc.ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin);
        r.Error!.Failure.Should().Be(ReturnEditFailure.PinLocked);
        r.Error.RetryAfterSeconds.Should().Be(900);
        ReturnEditApi.PinLockSecondsLeft.Should().BeGreaterThan(800);
        var again = await svc.ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin);
        again.Error!.Failure.Should().Be(ReturnEditFailure.PinLocked);
        server.LogEntries.Count(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/").Should().Be(1, "во время блокировки PIN на сервер не отправляется");
    }

    [Fact]
    public async Task Confirm_WindowExpired_IsFinal()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 409, "{\"code\":\"edit_window_expired\",\"editable_until\":\"2026-11-10T08:00:00Z\"}");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Void, 3, Pin);
        r.Error!.Failure.Should().Be(ReturnEditFailure.WindowExpired);
        r.Error.IsFinal.Should().BeTrue("срок вышел — повторять PIN бессмысленно");
        ReturnEditErrors.Text(r.Error).Should().Contain("24");
    }

    // ───────────── правка, аннулирование, история ─────────────

    [Fact]
    public async Task Patch_SendsIfMatch_ConfirmToken_IdempotencyKey_AndVersionInBody()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/", "PATCH", 200, "{\"id\":\"r1\",\"version\":4,\"total_amount\":\"150.00\"}");
        var lines = new List<SupplierReturnEditLine> { new("l1", "p1", "Молоко", "шт", 1, 150, 0, "") };
        var body = SupplierReturnEdit.BuildPatchBody(lines, "ошиблись количеством", "");
        var d = await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Supplier, "r1", 3, body, "ct-1", "key-1");
        d.GetProperty("version").GetInt32().Should().Be(4);
        var req = server.LogEntries.Single(e => e.RequestMessage.Method == "PATCH").RequestMessage;
        req.Headers!["If-Match"].Single().Should().Be("\"3\"");
        req.Headers!["X-Confirm-Token"].Single().Should().Be("ct-1");
        req.Headers!["Idempotency-Key"].Single().Should().Be("key-1");
        var sent = J(req.Body!);
        sent.GetProperty("version").GetInt32().Should().Be(3);
        sent.GetProperty("edit_reason").GetString().Should().Be("ошиблись количеством");
        sent.GetProperty("items")[0].GetProperty("id").GetString().Should().Be("l1");
        sent.GetProperty("items")[0].GetProperty("qty").GetString().Should().Be("1");
    }

    [Fact]
    public async Task Patch_412_VersionConflict_NotRetried()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/", "PATCH", 412, "{\"code\":\"version_conflict\",\"current_version\":5}");
        var act = async () => await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Supplier, "r1", 3, new Dictionary<string, object?>(), "ct-1", "key-1");
        var ex = (await act.Should().ThrowAsync<ReturnEditException>()).Which;
        ex.Failure.Should().Be(ReturnEditFailure.VersionConflict);
        ex.CurrentVersion.Should().Be(5);
        ex.IsFinal.Should().BeTrue();
        ReturnEditErrors.Text(ex).Should().Contain("другим сотрудником");
        server.LogEntries.Count(e => e.RequestMessage.Method == "PATCH").Should().Be(1, "чужую новую версию молча не перезаписываем");
    }

    [Fact]
    public async Task Patch_EditWindowExpired_OnSave_ServerRechecks()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/pos/returns/s9/", "PATCH", 409, "{\"code\":\"edit_window_expired\"}");
        var act = async () => await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Customer, "s9", 1, new Dictionary<string, object?>(), "ct", "k");
        (await act.Should().ThrowAsync<ReturnEditException>()).Which.Failure.Should().Be(ReturnEditFailure.WindowExpired);
        server.LogEntries.Single(e => e.RequestMessage.Method == "PATCH").RequestMessage.Path.Should().Be("/api/main/pos/returns/s9/", "возврат от клиента — свой адрес");
    }

    [Fact]
    public async Task Void_SendsReasonAndHeaders_AlreadyVoidedIsFinal()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/void/", "POST", 200, "{\"id\":\"r1\",\"version\":4,\"status\":\"voided\"}");
        var d = await new ReturnEditApi(api).VoidAsync(ReturnDocKind.Supplier, "r1", 3, "  ошибочный возврат  ", "ct-2", "key-v");
        ReturnEditState.Parse(d).IsVoided.Should().BeTrue();
        var req = server.LogEntries.Single(e => e.RequestMessage.Path == "/api/main/suppliers/returns/r1/void/").RequestMessage;
        req.Headers!["X-Confirm-Token"].Single().Should().Be("ct-2");
        req.Headers!["If-Match"].Single().Should().Be("\"3\"");
        req.Headers!["Idempotency-Key"].Single().Should().Be("key-v");
        J(req.Body!).GetProperty("reason").GetString().Should().Be("ошибочный возврат");

        server.Reset();
        api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/void/", "POST", 409, "{\"code\":\"already_voided\"}");
        var act = async () => await new ReturnEditApi(api).VoidAsync(ReturnDocKind.Supplier, "r1", 4, "ошибочный возврат", "ct-3", "key-v2");
        (await act.Should().ThrowAsync<ReturnEditException>()).Which.Failure.Should().Be(ReturnEditFailure.AlreadyVoided);
    }

    [Fact]
    public async Task Void_WithoutToken_NotSent()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        var act = async () => await new ReturnEditApi(api).VoidAsync(ReturnDocKind.Supplier, "r1", 3, "причина", "", "k");
        await act.Should().ThrowAsync<ArgumentException>();
        server.LogEntries.Should().NotContain(e => e.RequestMessage.Path.EndsWith("/void/"), "без подтверждения руководителя запрос не уходит");
    }

    [Fact]
    public async Task History_ThreeVersions_OriginalSnapshotReadable_403IsPermissionDenied()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/history/", "GET", 200,
            "[{\"version\":2,\"action\":\"edited\",\"at\":\"2026-11-10T10:00:00Z\",\"user_name\":\"Кассир\",\"approved_by_name\":\"Айбек\",\"reason\":\"ошиблись\",\"total\":\"150\","
            + "\"snapshot\":{\"items\":[{\"product_name\":\"Молоко\",\"qty\":\"1\",\"purchase_price\":\"150\"}]}},"
            + "{\"version\":1,\"action\":\"created\",\"at\":\"2026-11-10T08:00:00Z\",\"user_name\":\"Кассир\",\"total\":\"300\","
            + "\"snapshot\":{\"items\":[{\"product_name\":\"Молоко\",\"qty\":\"2\",\"purchase_price\":\"150\",\"discount\":\"10\"}]}},"
            + "{\"version\":3,\"action\":\"voided\",\"at\":\"2026-11-10T11:00:00Z\",\"user\":{\"name\":\"Админ\"},\"reason\":\"дубль\",\"total\":\"0\"}]");
        var list = await new ReturnEditApi(api).GetHistoryAsync(ReturnDocKind.Supplier, "r1");
        list.Select(v => v.Version).Should().Equal(1, 2, 3);
        list[0].IsCreated.Should().BeTrue();
        var original = list[0].SnapshotLines();
        original.Should().ContainSingle();
        original[0].Qty.Should().Be(2);
        original[0].Total.Should().Be(290, "2 × 150 − 10 скидки");
        list[1].ApprovedBy.Should().Be("Айбек");
        list[2].IsVoided.Should().BeTrue();
        list[2].User.Should().Be("Админ");

        server.Reset();
        api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/history/", "GET", 403, "{\"code\":\"permission_denied\"}");
        var act = async () => await new ReturnEditApi(api).GetHistoryAsync(ReturnDocKind.Supplier, "r1");
        (await act.Should().ThrowAsync<ReturnEditException>()).Which.Failure.Should().Be(ReturnEditFailure.PermissionDenied);
    }

    // ───────────── PIN не пишется в журнал и файлы ─────────────

    [Fact]
    public async Task Pin_NeverInLogOrFiles()
    {
        var log = new List<string>();
        var oldMirror = PosLogger.Mirror;
        PosLogger.Mirror = (cat, msg) => { lock (log) log.Add(cat + " " + msg); };
        try
        {
            using var server = WireMockServer.Start();
            var api = await LoggedIn(server);
            var svc = new ReturnEditApi(api);
            Reply(server, "/api/users/auth/pin/verify/", "POST", 400, "{\"code\":\"pin_invalid\",\"attempts_left\":4,\"pin\":[\"неверный\"]}");
            (await svc.ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin)).Ok.Should().BeFalse();
            server.Reset();
            api = await LoggedIn(server);
            svc = new ReturnEditApi(api);
            Reply(server, "/api/users/auth/pin/verify/", "POST", 200, "{\"confirm_token\":\"secret-token-777\"}");
            Reply(server, "/api/main/suppliers/returns/r1/", "PATCH", 500, "{\"detail\":\"boom\"}");
            var ok = await svc.ConfirmAsync(ReturnDocKind.Supplier, "r1", ReturnEditAction.Edit, 3, Pin);
            ok.Ok.Should().BeTrue();
            var act = async () => await svc.PatchAsync(ReturnDocKind.Supplier, "r1", 3, new Dictionary<string, object?>(), ok.Token!, "k");
            await act.Should().ThrowAsync<ReturnEditException>();
        }
        finally
        {
            PosLogger.Mirror = oldMirror;
        }
        log.Should().NotBeEmpty("журнал пишется");
        log.Should().NotContain(l => l.Contains(Pin), "PIN не пишется в журнал");
        log.Should().NotContain(l => l.Contains("secret-token-777"), "токен подтверждения не пишется в журнал");
        foreach (var root in new[] { AppPaths.KassaLocal, _dir }.Where(Directory.Exists).Distinct())
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length < 20_000_000))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                text.Should().NotContain(Pin, $"PIN не должен попасть в файл {file}");
            }
    }

    // ───────────── правка возврата поставщику: расчёт и проверки ─────────────

    [Fact]
    public void SupplierEdit_RecalculatesTotal_NoLineDiscount_BodyIsIdQtyPrice_AndDropsZeroLines()
    {
        // 2026-11-11 (реальный контракт Умара): скидку и примечание строки сервер не хранит — в сумму и в тело они не входят
        var lines = new List<SupplierReturnEditLine>
        {
            new("l1", "p1", "Молоко", "шт", 2, 150, 10, "мятая"),
            new("l2", "p2", "Сыр", "кг", 0.125, 1, 0, ""),
            new("l3", "p3", "Хлеб", "шт", 0, 30, 0, ""),
        };
        SupplierReturnEdit.Total(lines).Should().Be(300.13, "2×150 = 300 (скидка строки не хранится); 0,125×1 = 0,13 (до копейки от нуля); строка с 0 — 0");
        SupplierReturnEdit.Validate(lines, "ошиблись количеством").Should().BeNull();
        var body = SupplierReturnEdit.BuildPatchBody(lines, " ошиблись количеством ", " коммент ");
        var items = (List<Dictionary<string, object?>>)body["items"]!;
        items.Should().HaveCount(2, "строка с количеством 0 убирается из возврата (сервер удаляет отсутствующие)");
        items[0].Keys.Should().BeEquivalentTo(new[] { "id", "qty", "purchase_price" }, "ровно поля контракта: без discount, note, product_id");
        items[0]["id"].Should().Be("l1");
        items[0]["qty"].Should().Be("2");
        items[0]["purchase_price"].Should().Be("150");
        body["edit_reason"].Should().Be("ошиблись количеством");
        body["comment"].Should().Be("коммент");
    }

    [Fact]
    public void SupplierEdit_Validation()
    {
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("l1", "p1", "Молоко", "шт", 1.5, 150, 0, "") }, "причина правки").Should().Contain("целое");
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("l1", "p1", "Молоко", "шт", 1, 150, 200, "") }, "причина правки").Should().BeNull("скидку строки сервер не хранит — не проверяется");
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("", "p1", "Молоко", "шт", 1, 150, 0, "") }, "причина правки").Should().Contain("id строки", "без id строки сервер ответит new_lines_not_allowed");
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("l1", "p1", "Молоко", "шт", 0, 150, 0, "") }, "причина правки").Should().Contain("Удалить возврат");
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("l1", "p1", "Молоко", "шт", 1, 150, 0, "") }, "абв").Should().Contain("причину");
        SupplierReturnEdit.Validate(new[] { new SupplierReturnEditLine("", "", "Молоко", "шт", 1, 150, 0, "") }, "причина правки").Should().Contain("id строки");
        SupplierReturnEdit.ParseNumber("1,5").Should().Be(1.5);
        SupplierReturnEdit.ParseNumber("абв").Should().BeNull();
        var original = new List<SupplierReturnEditLine> { new("l1", "p1", "Молоко", "шт", 2, 150, 0, "") };
        SupplierReturnEdit.HasChanges(original, original, "", "").Should().BeFalse();
        SupplierReturnEdit.HasChanges(original, new List<SupplierReturnEditLine> { original[0] with { Qty = 1 } }, "", "").Should().BeTrue();
    }

    [Fact]
    public void ParseReturn_ReadsLineIds_AndEditState()
    {
        var info = SupplierApiService.ParseReturn(J(EditableDoc));
        info.Lines[0].Id.Should().Be("l1");
        info.Lines[0].ProductId.Should().Be("p1");
        info.EditState.Version.Should().Be(3);
        info.EditState.CanEdit.Should().BeTrue();
        SupplierReturnEdit.FromInfo(info)[0].Qty.Should().Be(2);
        // старый сервер без полей — None
        SupplierApiService.ParseReturn(J("{\"id\":\"x\",\"items\":[]}")).EditState.HasServerFlags.Should().BeFalse();
    }

    // ───────────── PDF/Excel/Word: метка «АННУЛИРОВАН» ─────────────

    private static string AllXml(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }));
    }

    [Fact]
    public void Documents_Voided_HaveMark_ActiveDoNot()
    {
        Directory.CreateDirectory(_dir);
        var voidedJson = "{\"id\":\"r7\",\"number_display\":\"ВП-2026-000007\",\"version\":4,\"status\":\"voided\",\"voided_at\":\"2026-11-10T09:00:00Z\",\"void_reason\":\"дубль возврата\","
            + "\"supplier_name\":\"ОсОО Поставщик\",\"total_amount\":\"300\",\"items\":[{\"product_name\":\"Молоко\",\"qty\":\"2\",\"purchase_price\":\"150\"}]}";
        var data = SupplierReturnDocuments.Build(SupplierApiService.ParseReturn(J(voidedJson)), null, "Тест");
        data.Voided.Should().BeTrue();
        SupplierReturnDocuments.HeaderTitle(data).Should().Contain("АННУЛИРОВАН");
        SupplierReturnDocuments.VoidedStatusText(data).Should().Contain("дубль возврата");
        var xlsx = Path.Combine(_dir, "v.xlsx");
        var docx = Path.Combine(_dir, "v.docx");
        SupplierReturnDocumentOffice.SaveExcel(data, xlsx);
        SupplierReturnDocumentOffice.SaveWord(data, docx);
        AllXml(xlsx).Should().Contain("АННУЛИРОВАН");
        AllXml(docx).Should().Contain("АННУЛИРОВАН");

        var active = SupplierReturnDocuments.Build(SupplierApiService.ParseReturn(J(EditableDoc)), null, "Тест");
        active.Voided.Should().BeFalse();
        SupplierReturnDocuments.HeaderTitle(active).Should().NotContain("АННУЛИРОВАН");
        var activeXlsx = Path.Combine(_dir, "a.xlsx");
        SupplierReturnDocumentOffice.SaveExcel(active, activeXlsx);
        AllXml(activeXlsx).Should().NotContain("АННУЛИРОВАН");
    }
}
