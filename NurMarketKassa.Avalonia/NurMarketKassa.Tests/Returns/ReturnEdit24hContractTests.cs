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

/// <summary>2026-11-11 (сервер Умара, боевой app.nurcrm.kg): клиентская часть правки/аннулирования возвратов сверена с РЕАЛЬНЫМ контрактом. Заглушки WireMock:
/// PIN → PATCH → повтор с тем же ключом (replayed:true); каждый код ошибки (428 confirm_required / version_required, 403 confirm_invalid, 412 version_conflict,
/// 400 validation_error / idempotency_key_required / new_lines_not_allowed, 409 negative_stock / exceeds_returnable / debt_already_paid / period_closed, PIN-ошибки);
/// can_edit:false по каждой причине edit_block_reason (кнопок нет, пояснение есть); возврат от клиента: тело {sale_item_id, quantity}, максимум по чеку;
/// ?status=all в списках и итоги без аннулированных; PIN и токен не в журнале.</summary>
[Collection("ServerFeatures")]
public sealed class ReturnEdit24hContractTests : IDisposable
{
    private const string Pin = "582917";
    private const string Token = "ct-secret-55aa";
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly Func<NurMarketApiClient?>? _oldResolver = NurCrmReportsApi.ClientResolver;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-ret24c-" + Guid.NewGuid().ToString("N")[..8]);

    public ReturnEdit24hContractTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ServerCapabilities.DirOverride = _dir;
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.PosReturnEdit24h, ServerCapabilities.SupplierReturnEdit24h);
        ReturnEditApi.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        NurCrmReportsApi.ClientResolver = _oldResolver;
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

    // ───────────── PIN → PATCH → повтор (replayed) ─────────────

    [Fact]
    public async Task CustomerReturn_Pin_Patch_RetrySameKey_Replayed_SameBodyAndHeaders()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 200,
            "{\"confirm_token\":\"" + Token + "\",\"expires_in\":120,\"approved_by_name\":\"Айбек\",\"editable_until\":\"2026-11-12T08:00:00Z\",\"server_now\":\"2026-11-11T08:00:00Z\"}");
        // первый PATCH — обычный ответ, повтор с тем же ключом — replayed:true
        server.Given(Request.Create().WithPath("/api/main/pos/returns/pr1/").UsingMethod("PATCH")).InScenario("patch").WillSetStateTo("done").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"pr1\",\"version\":4,\"amount\":\"100.00\"}"));
        server.Given(Request.Create().WithPath("/api/main/pos/returns/pr1/").UsingMethod("PATCH")).InScenario("patch").WhenStateIs("done").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"pr1\",\"version\":4,\"amount\":\"100.00\",\"replayed\":true}"));

        var svc = new ReturnEditApi(api);
        var confirm = await svc.ConfirmAsync(ReturnDocKind.Customer, "pr1", ReturnEditAction.Edit, 3, Pin);
        confirm.Ok.Should().BeTrue();
        var pinBody = J(server.LogEntries.Single(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/").RequestMessage.Body!);
        pinBody.GetProperty("scope").GetString().Should().Be("return_edit");
        pinBody.GetProperty("object_type").GetString().Should().Be("pos"); // 2026-11-11: как в сводке Умара 11.11
        pinBody.GetProperty("object_id").GetString().Should().Be("pr1");
        pinBody.GetProperty("version").GetInt32().Should().Be(3);
        server.LogEntries.Single(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/").RequestMessage.Headers!.ContainsKey("Idempotency-Key")
            .Should().BeFalse("создание токена — без Idempotency-Key");

        var lines = new List<CustomerReturnEditLine> { new("si-1", "Молоко", "шт", 1, 2, 100), new("si-2", "Хлеб", "шт", 0, 1, 30) };
        var key = "pos-edit-key-1";
        var d1 = await svc.PatchAsync(ReturnDocKind.Customer, "pr1", 3, CustomerReturnEdit.BuildPatchBody(lines, " ошиблись количеством "), confirm.Token!, key);
        var d2 = await svc.PatchAsync(ReturnDocKind.Customer, "pr1", 3, CustomerReturnEdit.BuildPatchBody(lines, " ошиблись количеством "), confirm.Token!, key);
        d1.GetProperty("version").GetInt32().Should().Be(4);
        d2.GetProperty("replayed").GetBoolean().Should().BeTrue("повтор с тем же ключом — тот же результат, не второе проведение");

        var patches = server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH").Select(e => e.RequestMessage).ToList();
        patches.Should().HaveCount(2);
        foreach (var req in patches)
        {
            req.Headers!["Idempotency-Key"].Single().Should().Be(key);
            req.Headers!["If-Match"].Single().Should().Be("\"3\"");
            req.Headers!["X-Confirm-Token"].Single().Should().Be(Token);
            var body = J(req.Body!);
            body.GetProperty("version").GetInt32().Should().Be(3);
            body.GetProperty("edit_reason").GetString().Should().Be("ошиблись количеством");
            var items = body.GetProperty("items");
            items.GetArrayLength().Should().Be(1, "строка с 0 не отправляется — полный новый состав");
            items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[] { "sale_item_id", "quantity" }, "ни цены, ни скидки, ни примечания");
            items[0].GetProperty("sale_item_id").GetString().Should().Be("si-1");
            items[0].GetProperty("quantity").GetString().Should().Be("1");
        }
    }

    // ───────────── коды ошибок ─────────────

    public static IEnumerable<object[]> ErrorCases() => new[]
    {
        new object[] { 428, "{\"code\":\"confirm_required\"}", ReturnEditFailure.ConfirmRequired, false, "PIN" },
        new object[] { 403, "{\"code\":\"confirm_invalid\"}", ReturnEditFailure.ConfirmInvalid, false, "PIN" },
        new object[] { 428, "{\"code\":\"version_required\"}", ReturnEditFailure.VersionRequired, true, "версию" },
        new object[] { 412, "{\"code\":\"version_conflict\",\"current_version\":7}", ReturnEditFailure.VersionConflict, true, "другим сотрудником" },
        new object[] { 400, "{\"code\":\"validation_error\",\"detail\":\"quantity\"}", ReturnEditFailure.Validation, false, "не принял" },
        new object[] { 400, "{\"code\":\"idempotency_key_required\"}", ReturnEditFailure.IdempotencyKeyRequired, false, "ключ" },
        new object[] { 400, "{\"code\":\"new_lines_not_allowed\"}", ReturnEditFailure.NewLinesNotAllowed, false, "новые строки" },
        new object[] { 409, "{\"code\":\"negative_stock\"}", ReturnEditFailure.NegativeStock, false, "остатка" },
        new object[] { 409, "{\"code\":\"exceeds_returnable\"}", ReturnEditFailure.ExceedsReturnable, false, "больше" },
        new object[] { 409, "{\"code\":\"debt_already_paid\"}", ReturnEditFailure.DebtAlreadyPaid, true, "погашен" },
        new object[] { 409, "{\"code\":\"period_closed\"}", ReturnEditFailure.PeriodClosed, true, "Период закрыт" },
        new object[] { 409, "{\"code\":\"edit_window_expired\"}", ReturnEditFailure.WindowExpired, true, "24" },
        new object[] { 409, "{\"code\":\"already_voided\"}", ReturnEditFailure.AlreadyVoided, true, "аннулирован" },
        new object[] { 409, "{\"code\":\"has_later_documents\"}", ReturnEditFailure.Blocked, true, "другой возврат по чеку" },
        new object[] { 409, "{\"code\":\"shift_closed\"}", ReturnEditFailure.Blocked, true, "Смена закрыта" },
        new object[] { 403, "{\"code\":\"permission_denied\"}", ReturnEditFailure.PermissionDenied, true, "прав" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Patch_ErrorCodes_ClassifiedWithFriendlyText(int status, string body, ReturnEditFailure expected, bool final, string textPart)
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/r1/", "PATCH", status, body);
        var act = async () => await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Supplier, "r1", 3, new Dictionary<string, object?>(), "tok", "k-1");
        var ex = (await act.Should().ThrowAsync<ReturnEditException>()).Which;
        ex.Failure.Should().Be(expected);
        ex.IsFinal.Should().Be(final);
        ReturnEditErrors.Text(ex).Should().Contain(textPart);
        if (expected == ReturnEditFailure.VersionConflict)
            ex.CurrentVersion.Should().Be(7);
        server.LogEntries.Count(e => e.RequestMessage.Method == "PATCH").Should().Be(1, "отказ сервера молча не повторяется");
    }

    [Theory]
    [InlineData(400, "{\"code\":\"pin_invalid\",\"attempts_left\":3}", ReturnEditFailure.PinInvalid, false, "3")]
    [InlineData(400, "{\"code\":\"pin_not_set\"}", ReturnEditFailure.PinNotSet, true, "не задан PIN")]
    [InlineData(429, "{\"code\":\"pin_locked\",\"retry_after\":600}", ReturnEditFailure.PinLocked, false, "10 мин")]
    [InlineData(409, "{\"code\":\"edit_window_expired\"}", ReturnEditFailure.WindowExpired, true, "24")]
    [InlineData(409, "{\"code\":\"already_voided\"}", ReturnEditFailure.AlreadyVoided, true, "аннулирован")]
    [InlineData(412, "{\"code\":\"version_conflict\",\"current_version\":5}", ReturnEditFailure.VersionConflict, true, "другим сотрудником")]
    public async Task Confirm_PinErrors_BeforeToken(int status, string body, ReturnEditFailure expected, bool final, string textPart)
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", status, body);
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Customer, "pr1", ReturnEditAction.Void, 2, Pin);
        r.Ok.Should().BeFalse();
        r.Error!.Failure.Should().Be(expected);
        r.Error.IsFinal.Should().Be(final);
        ReturnEditErrors.Text(r.Error).Should().Contain(textPart);
    }

    // ───────────── can_edit:false — кнопок нет, причина понятна ─────────────

    [Theory]
    [InlineData("legacy_return_not_supported", "до включения правки")]
    [InlineData("debt_sale_not_supported", "Чек в долг")]
    [InlineData("agent_sale_not_supported", "агента")]
    [InlineData("has_later_documents", "другой возврат по чеку")]
    [InlineData("shift_closed", "Смена закрыта")]
    [InlineData("edit_window_expired", "24 часа")]
    [InlineData("permission_denied", "Нет прав")]
    public void CanEditFalse_EachReason_NoButtons_HintExplains(string reason, string textPart)
    {
        var s = ReturnEditState.Parse(J("{\"id\":\"pr1\",\"version\":2,\"status\":\"posted\",\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"" + reason + "\"}"));
        ReturnEditGate.ShowEdit(ReturnDocKind.Customer, s).Should().BeFalse();
        ReturnEditGate.ShowVoid(ReturnDocKind.Customer, s).Should().BeFalse();
        ReturnEditGate.BlockHint(ReturnDocKind.Customer, s).Should().Contain(textPart);
        ReturnEditErrors.NotEditableText(s).Should().Contain(textPart);
        // на всех 5 языках текст есть и не пустой
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            ReturnEditErrors.BlockReasonText(reason).Should().NotBeNullOrWhiteSpace();
        }
        UserPreferences.Instance.Language = AppLanguage.Russian;
    }

    [Fact]
    public void BlockHint_NotShown_WhenAllowed_Voided_OrFeatureOff()
    {
        ReturnEditGate.BlockHint(ReturnDocKind.Customer, ReturnEditState.Parse(J("{\"version\":2,\"can_edit\":true,\"can_void\":true}"))).Should().BeNull();
        ReturnEditGate.BlockHint(ReturnDocKind.Customer, ReturnEditState.Parse(J("{\"version\":2,\"status\":\"voided\",\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"already_voided\"}")))
            .Should().BeNull("у аннулированного — своя плашка «АННУЛИРОВАН»");
        ServerCapabilities.SetFeaturesForTests();
        ReturnEditGate.BlockHint(ReturnDocKind.Customer, ReturnEditState.Parse(J("{\"version\":2,\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"debt_sale_not_supported\"}")))
            .Should().BeNull("без признака сервера — как раньше, ничего не показываем");
    }

    // ───────────── возврат от клиента: строки, максимум, проверки ─────────────

    private const string PosReturnCard = "{\"id\":\"pr1\",\"sale\":\"s1\",\"version\":3,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,\"amount\":\"250.00\","
        + "\"items\":[{\"sale_item_id\":\"si-1\",\"name\":\"Молоко\",\"quantity\":\"2\",\"amount\":\"200.00\"},{\"sale_item\":{\"id\":\"si-2\"},\"quantity\":\"0.5\",\"price\":\"100\"}]}";

    private const string SaleCard = "{\"id\":\"s1\",\"items\":[{\"id\":\"si-1\",\"product_id\":\"p1\",\"name\":\"Молоко\",\"quantity\":\"5\",\"returned_qty\":\"2\",\"returnable_qty\":\"3\",\"unit_price\":\"100\"},"
        + "{\"id\":\"si-2\",\"product_id\":\"p2\",\"name\":\"Сыр\",\"unit\":\"кг\",\"quantity\":\"1\",\"returnable_qty\":\"0.5\",\"unit_price\":\"100\"}]}";

    [Fact]
    public void CustomerEdit_FromCards_MaxIsReturnablePlusThisReturn_ValidateAndBody()
    {
        var lines = CustomerReturnEdit.FromCards(J(PosReturnCard), J(SaleCard));
        lines.Should().HaveCount(2);
        lines[0].SaleItemId.Should().Be("si-1");
        lines[0].Qty.Should().Be(2);
        lines[0].Max.Should().Be(5, "можно вернуть ещё 3 + 2 уже в этом возврате");
        lines[0].UnitPrice.Should().Be(100, "сумма строки возврата / количество");
        lines[1].SaleItemId.Should().Be("si-2");
        lines[1].Name.Should().Be("Сыр", "имя — из строки чека, если у строки возврата его нет");
        lines[1].Unit.Should().Be("кг");
        lines[1].Max.Should().Be(1);
        CustomerReturnEdit.Total(lines).Should().Be(250);

        CustomerReturnEdit.Validate(new[] { lines[0] with { Qty = 6 }, lines[1] }, "причина правки").Should().Contain("не больше 5");
        CustomerReturnEdit.Validate(new[] { lines[0] with { Qty = 1.5 }, lines[1] }, "причина правки").Should().Contain("целое");
        CustomerReturnEdit.Validate(new[] { lines[0] with { Qty = 0 }, lines[1] with { Qty = 0 } }, "причина правки").Should().Contain("Удалить возврат");
        CustomerReturnEdit.Validate(lines, "абв").Should().Contain("причину", "причина обязательна, от 5 знаков");
        CustomerReturnEdit.Validate(new[] { lines[0] with { Qty = 3 }, lines[1] with { Qty = 0.25 } }, "ошиблись количеством").Should().BeNull();
        CustomerReturnEdit.HasChanges(lines, lines).Should().BeFalse();
        CustomerReturnEdit.HasChanges(lines, new[] { lines[0] with { Qty = 1 }, lines[1] }).Should().BeTrue();

        // чек не получен — увеличивать нельзя (максимум = текущее количество)
        var noSale = CustomerReturnEdit.FromCards(J(PosReturnCard), default);
        noSale[0].Max.Should().Be(2);
    }

    // ───────────── ?status=all и итоги без аннулированных ─────────────

    [Fact]
    public async Task PosReturns_StatusAll_ShowsVoided_TotalsExcludeVoided()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        NurCrmReportsApi.ClientResolver = () => api;
        // со status=all — два возврата (один аннулирован, сумма 0); без — только действующий
        server.Given(Request.Create().WithPath("/api/main/pos/returns/").WithParam("status", "all").UsingMethod("GET")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                "[{\"id\":\"pr1\",\"sale\":\"s1\",\"amount\":\"150.00\",\"version\":2,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true},"
                + "{\"id\":\"pr2\",\"sale\":\"s1\",\"amount\":\"0.00\",\"version\":3,\"status\":\"voided\",\"can_edit\":false,\"can_void\":false,\"edit_block_reason\":\"already_voided\"}]"));
        Reply(server, "/api/main/pos/returns/", "GET", 200, "[{\"id\":\"pr1\",\"sale\":\"s1\",\"amount\":\"150.00\",\"version\":2,\"status\":\"posted\"}]");

        var all = await NurCrmReportsApi.ListReturnsAsync(null, null, saleId: "s1", includeVoided: true);
        all.Should().HaveCount(2);
        all!.Count(NurCrmReportsApi.IsVoided).Should().Be(1);
        server.LogEntries.Last().RequestMessage.Query!["status"].Single().Should().Be("all");

        var plain = await NurCrmReportsApi.ListReturnsAsync(null, null, saleId: "s1");
        plain.Should().HaveCount(1, "без status=all сервер аннулированные не отдаёт — старые итоги не меняются");
        server.LogEntries.Last().RequestMessage.Query!.ContainsKey("status").Should().BeFalse();

        // итоги: даже если в список попал аннулированный (сервер отдал его без status=all), он не считается — ни штукой, ни суммой
        server.Reset();
        api = await LoggedIn(server);
        Reply(server, "/api/main/pos/returns/", "GET", 200,
            "[{\"id\":\"pr1\",\"sale\":\"s1\",\"amount\":\"150.00\",\"status\":\"posted\"},{\"id\":\"pr2\",\"sale\":\"s1\",\"amount\":\"40.00\",\"status\":\"voided\"}]");
        var totals = await NurCrmReportsApi.ReturnsTotalsAsync(DateTime.Today, DateTime.Today);
        totals.Should().Be((1, 150m));
    }

    [Fact]
    public async Task SupplierReturns_StatusAll_OnlyWhenAsked()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/suppliers/returns/", "GET", 200,
            "{\"results\":[{\"id\":\"r1\",\"total_amount\":\"300\",\"status\":\"posted\",\"version\":1},{\"id\":\"r2\",\"total_amount\":\"0\",\"status\":\"voided\",\"version\":2}],\"meta\":{\"total_amount\":\"300\"}}");
        var svc = new SupplierApiService(api);
        var all = await svc.GetReturnsAsync(null, default, includeVoided: true);
        server.LogEntries.Last().RequestMessage.Query!["status"].Single().Should().Be("all");
        all.Where(r => !r.EditState.IsVoided).Sum(r => r.Total).Should().Be(300, "итог — без аннулированных, не задваивается");
        all.Count(r => r.EditState.IsVoided).Should().Be(1);
        await svc.GetReturnsAsync(null);
        server.LogEntries.Last().RequestMessage.Query!.ContainsKey("status").Should().BeFalse();
    }

    // ───────────── 2026-11-11: object_type — сводка 11.11 ("supplier"/"pos") или прежнее ("supplier_return"/"pos_return") ─────────────

    private static string ObjectTypeOf(string? body)
    {
        try
        {
            return JsonDocument.Parse(body ?? "{}").RootElement.TryGetProperty("object_type", out var t) ? t.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Заглушка pin/verify: принимает только <paramref name="accepted"/>, на другой object_type — 400 validation_error с ошибкой поля object_type.</summary>
    private static void PinServerAccepting(WireMockServer server, string accepted)
    {
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody((string? b) => ObjectTypeOf(b) == accepted)).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"confirm_token\":\"" + Token + "\",\"expires_in\":120}"));
        Reply(server, "/api/users/auth/pin/verify/", "POST", 400,
            "{\"code\":\"validation_error\",\"detail\":\"Неверный тип объекта.\",\"errors\":{\"object_type\":[\"Значение недопустимо.\"]}}", priority: 5);
    }

    private static List<string> SentObjectTypes(WireMockServer server) =>
        server.LogEntries.Where(e => e.RequestMessage.Path == "/api/users/auth/pin/verify/").Select(e => ObjectTypeOf(e.RequestMessage.Body)).ToList();

    [Theory]
    [InlineData(ReturnDocKind.Customer, "pos")]
    [InlineData(ReturnDocKind.Supplier, "supplier")]
    public async Task ObjectType_ServerAcceptsSummaryValue_OneRequest(ReturnDocKind kind, string expected)
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        PinServerAccepting(server, expected);
        var r = await new ReturnEditApi(api).ConfirmAsync(kind, "d1", ReturnEditAction.Edit, 2, Pin);
        r.Ok.Should().BeTrue();
        SentObjectTypes(server).Should().Equal(expected);
    }

    [Theory]
    [InlineData(ReturnDocKind.Customer, "pos", "pos_return")]
    [InlineData(ReturnDocKind.Supplier, "supplier", "supplier_return")]
    public async Task ObjectType_ServerWantsLegacy_RetriesOnce_RemembersInProcess(ReturnDocKind kind, string first, string legacy)
    {
        var log = new List<string>();
        var oldMirror = PosLogger.Mirror;
        PosLogger.Mirror = (cat, msg) => { lock (log) log.Add(cat + " " + msg); };
        try
        {
            using var server = WireMockServer.Start();
            var api = await LoggedIn(server);
            PinServerAccepting(server, legacy);
            var svc = new ReturnEditApi(api);
            var r = await svc.ConfirmAsync(kind, "d1", ReturnEditAction.Void, 2, Pin);
            r.Ok.Should().BeTrue("после 400 про object_type касса один раз повторила с прежним значением");
            r.Token.Should().Be(Token);
            SentObjectTypes(server).Should().Equal(first, legacy);

            // сработавший вариант запомнен — следующий PIN сразу с ним, без лишнего отказа
            var again = await svc.ConfirmAsync(kind, "d2", ReturnEditAction.Edit, 3, Pin);
            again.Ok.Should().BeTrue();
            SentObjectTypes(server).Should().Equal(first, legacy, legacy);
        }
        finally
        {
            PosLogger.Mirror = oldMirror;
        }
        log.Should().Contain(l => l.Contains("object_type") && l.Contains(first) && l.Contains(legacy), "в журнале — факт повтора");
        log.Should().NotContain(l => l.Contains(Pin) || l.Contains(Token), "PIN и токен в журнал не пишутся");
    }

    [Fact]
    public async Task ObjectType_LegacyRemembered_ServerSwitchesBack_RetriesToSummaryValue()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        ReturnDocKinds.RememberObjectType(ReturnDocKind.Customer, legacy: true);
        PinServerAccepting(server, "pos");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Customer, "d1", ReturnEditAction.Edit, 2, Pin);
        r.Ok.Should().BeTrue();
        SentObjectTypes(server).Should().Equal("pos_return", "pos");
        ReturnDocKinds.ObjectType(ReturnDocKind.Customer).Should().Be("pos");
    }

    [Fact]
    public async Task ObjectType_BothRejected_OnlyTwoRequests_ValidationText()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        PinServerAccepting(server, "nothing");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Supplier, "d1", ReturnEditAction.Edit, 2, Pin);
        r.Ok.Should().BeFalse();
        r.Error!.Failure.Should().Be(ReturnEditFailure.Validation);
        SentObjectTypes(server).Should().Equal("supplier", "supplier_return");
        ReturnDocKinds.ObjectType(ReturnDocKind.Supplier).Should().Be("supplier", "ничего не сработало — запоминать нечего");
    }

    [Fact]
    public async Task ObjectType_PinInvalid400_NoObjectTypeRetry()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/users/auth/pin/verify/", "POST", 400, "{\"code\":\"pin_invalid\",\"attempts_left\":2,\"object_type\":\"pos\"}");
        var r = await new ReturnEditApi(api).ConfirmAsync(ReturnDocKind.Customer, "d1", ReturnEditAction.Edit, 2, Pin);
        r.Error!.Failure.Should().Be(ReturnEditFailure.PinInvalid);
        SentObjectTypes(server).Should().Equal("pos");
    }

    // ───────────── 2026-11-11: 409 правки возврата от клиента — понятные причины; ответ PATCH — новая version и edit_count ─────────────

    [Theory]
    [InlineData("legacy_return_not_supported", "до включения правки")]
    [InlineData("debt_sale_not_supported", "Чек в долг")]
    [InlineData("agent_sale_not_supported", "агента")]
    [InlineData("has_later_documents", "другой возврат по чеку")]
    [InlineData("shift_closed", "Смена закрыта")]
    [InlineData("already_voided", "аннулирован")]
    [InlineData("period_closed", "Период закрыт")]
    public async Task CustomerPatch_409Codes_FriendlyReason_Final(string code, string textPart)
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/pos/returns/pr1/", "PATCH", 409, "{\"code\":\"" + code + "\",\"detail\":\"server words\"}");
        var act = async () => await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Customer, "pr1", 3,
            CustomerReturnEdit.BuildPatchBody(new[] { new CustomerReturnEditLine("si-1", "Молоко", "шт", 1, 2, 100) }, "ошиблись количеством"), "tok", "k-409");
        var ex = (await act.Should().ThrowAsync<ReturnEditException>()).Which;
        ex.IsFinal.Should().BeTrue("причина по существу — повтор PIN бессмыслен");
        ReturnEditErrors.Text(ex).Should().Contain(textPart).And.NotContain("server words", "не сырые слова сервера, а понятная причина");
    }

    [Fact]
    public async Task CustomerPatch_Response_GivesNewVersionAndEditCount()
    {
        using var server = WireMockServer.Start();
        var api = await LoggedIn(server);
        Reply(server, "/api/main/pos/returns/pr1/", "PATCH", 200,
            "{\"id\":\"pr1\",\"version\":4,\"edit_count\":1,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,\"amount\":\"100.00\"}");
        var d = await new ReturnEditApi(api).PatchAsync(ReturnDocKind.Customer, "pr1", 3,
            CustomerReturnEdit.BuildPatchBody(new[] { new CustomerReturnEditLine("si-1", "Молоко", "шт", 1, 2, 100) }, "ошиблись количеством"), "tok", "k-v4");
        var s = ReturnEditState.Parse(d);
        s.Version.Should().Be(4);
        s.EditCount.Should().Be(1);
        s.CanEdit.Should().BeTrue();
    }

    // ───────────── PIN и токен не в журнале ─────────────

    [Fact]
    public async Task CustomerFlow_PinAndToken_NeverInLog()
    {
        var log = new List<string>();
        var oldMirror = PosLogger.Mirror;
        PosLogger.Mirror = (cat, msg) => { lock (log) log.Add(cat + " " + msg); };
        try
        {
            using var server = WireMockServer.Start();
            var api = await LoggedIn(server);
            Reply(server, "/api/users/auth/pin/verify/", "POST", 200, "{\"confirm_token\":\"" + Token + "\",\"expires_in\":120}");
            Reply(server, "/api/main/pos/returns/pr1/void/", "POST", 403, "{\"code\":\"confirm_invalid\"}");
            Reply(server, "/api/main/pos/returns/pr1/", "PATCH", 412, "{\"code\":\"version_conflict\",\"current_version\":9}");
            var svc = new ReturnEditApi(api);
            var ok = await svc.ConfirmAsync(ReturnDocKind.Customer, "pr1", ReturnEditAction.Void, 2, Pin);
            ok.Ok.Should().BeTrue();
            await FluentActions.Awaiting(() => svc.VoidAsync(ReturnDocKind.Customer, "pr1", 2, "ошибочный возврат", ok.Token!, "kv")).Should().ThrowAsync<ReturnEditException>();
            await FluentActions.Awaiting(() => svc.PatchAsync(ReturnDocKind.Customer, "pr1", 2, CustomerReturnEdit.BuildPatchBody(new[] { new CustomerReturnEditLine("si-1", "Молоко", "шт", 1, 2, 100) }, "ошиблись"), ok.Token!, "kp"))
                .Should().ThrowAsync<ReturnEditException>();
        }
        finally
        {
            PosLogger.Mirror = oldMirror;
        }
        log.Should().NotBeEmpty();
        log.Should().NotContain(l => l.Contains(Pin), "PIN не пишется в журнал");
        log.Should().NotContain(l => l.Contains(Token), "токен подтверждения не пишется в журнал");
    }
}
