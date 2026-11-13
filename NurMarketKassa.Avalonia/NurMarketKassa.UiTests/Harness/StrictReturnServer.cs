using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-12 (аудит Fable, В-8, С-37): поддельный «сервер Умара» с СТРОГИМ контрактом правки и аннулирования возвратов — вместо мока, который принимал любой PATCH.
/// PIN руководителя проверяется настоящим запросом (users/auth/pin/verify/: pin, scope, object_type, object_id, version) и выдаёт одноразовый токен, привязанный к действию, документу и версии;
/// PATCH/void без If-Match → 428 version_required, без X-Confirm-Token → 428 confirm_required, без Idempotency-Key → 400 idempotency_key_required, чужой/использованный токен → 403 confirm_invalid,
/// старая версия → 412 version_conflict (с current_version); повтор с тем же ключом отвечает тем же результатом (replayed:true) и второй раз не применяется; в ответе — полный документ с новой версией.
/// Состояние документов живёт на «сервере» (<see cref="Doc"/>): тест видит, сколько раз правка РЕАЛЬНО применена. Один экземпляр на тест (WireMock на свободном порту).</summary>
public sealed class StrictReturnServer : IAsyncDisposable
{
    public const string ValidPin = "1234";

    public sealed class Line
    {
        public string Id = "";
        public string ProductId = "";
        public string Name = "";
        public string Unit = "шт";
        public double Qty;
        public double Price;
        /// <summary>Только у возврата от клиента: сколько продано в чеке и сколько уже вернули ДРУГИМИ возвратами (returnable_qty чека = Sold − OtherReturned − Qty).</summary>
        public double Sold;
        public double OtherReturned;
    }

    public sealed class Doc
    {
        public string Id = "";
        public bool Customer;
        public int Version = 3;
        public bool Voided;
        public bool CanEdit = true;
        public string? BlockReason;
        public bool Expired;
        public string SaleId = "s1";
        public string Number = "";
        public List<Line> Lines = new();
        /// <summary>Сколько раз правка/аннулирование РЕАЛЬНО применены сервером (повтор по ключу не считается).</summary>
        public int AppliedEdits;
        public int AppliedVoids;
    }

    private sealed record Token(string Value, string Scope, string ObjectId, int Version, string ObjectType)
    {
        public bool Used { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Token> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Status, string Body)> _byKey = new(StringComparer.Ordinal);
    private int _tokenSeq;

    public WireMockServer Server { get; } = WireMockServer.Start();
    public NurMarketApiClient Api { get; private set; } = null!;
    public ReturnEditApi Edit { get; private set; } = null!;
    public Dictionary<string, Doc> Docs { get; } = new(StringComparer.Ordinal);

    /// <summary>Токены, выданные сервером (по порядку) — тест сверяет заголовок PATCH с токеном СЕРВЕРА, а не со своим значением.</summary>
    public List<string> IssuedTokens { get; } = new();

    /// <summary>Подмена ответа на правку/аннулирование ПОСЛЕ строгих проверок (409 negative_stock, 409 exceeds_returnable, 403…); null — применить как обычно. Вызывается с (документ, это void?).</summary>
    public Func<Doc, bool, ResponseMessage?>? Override { get; set; }

    /// <summary>Правка применяется, а ответ теряется настоящим обрывом сокета (FaultType.EMPTY_RESPONSE): следующая мутация. Повтор с тем же ключом получит прежний результат.</summary>
    public bool DropNextResponseAfterApply { get; set; }

    /// <summary>Пока включено — любой запрос PIN отвечает 403 permission_denied (нет права руководителя).</summary>
    public bool PinForbidden { get; set; }

    /// <summary>Задержка ответа на GET карточки (мс) — чтобы первое нажатие «Сохранить» ещё шло, когда придёт второе.</summary>
    public int GetDelayMs { get; set; }

    public static async Task<StrictReturnServer> StartAsync(bool supplierFeature = true, bool customerFeature = true)
    {
        var s = new StrictReturnServer();
        s.Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        s.Api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Server.Url! });
        await s.Api.LoginAsync("a@b.kg", "12345678");
        var caps = new List<string>();
        if (supplierFeature)
            caps.Add("\"supplier_return_edit_24h\"");
        if (customerFeature)
            caps.Add("\"pos_return_edit_24h\"");
        s.Server.Given(Request.Create().WithPath("/api/main/capabilities/").UsingMethod("GET")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[" + string.Join(",", caps) + "]"));
        await ServerCapabilities.LoadAsync(s.Api, force: true);
        s.Edit = new ReturnEditApi(s.Api);
        s.Server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST")).AtPriority(1).RespondWith(Response.Create().WithCallback(s.PinVerify));
        return s;
    }

    // ───────────── документы ─────────────

    public Doc AddSupplier(string id, params Line[] lines)
    {
        var d = new Doc { Id = id, Customer = false, Number = "ВП-2026-000123", Lines = lines.ToList() };
        Register(d);
        return d;
    }

    public Doc AddCustomer(string id, string saleId, params Line[] lines)
    {
        var d = new Doc { Id = id, Customer = true, SaleId = saleId, Number = "№1115", Lines = lines.ToList() };
        Register(d);
        Server.Given(Request.Create().WithPath("/api/main/pos/sales/" + saleId + "/").UsingMethod("GET")).AtPriority(1).RespondWith(Response.Create().WithCallback(_ => Reply(200, SaleJsonOf(d))));
        return d;
    }

    private string Base(Doc d) => (d.Customer ? "/api/main/pos/returns/" : "/api/main/suppliers/returns/") + d.Id + "/";

    private void Register(Doc d)
    {
        Docs[d.Id] = d;
        Server.Given(Request.Create().WithPath(Base(d)).UsingMethod("GET")).AtPriority(1).RespondWith(Response.Create().WithCallback(_ =>
        {
            if (GetDelayMs > 0)
                Thread.Sleep(GetDelayMs);
            lock (_gate)
                return Reply(200, DocJson(d, replayed: false));
        }));
        Server.Given(Request.Create().WithPath(Base(d)).UsingMethod("PATCH")).AtPriority(1).RespondWith(Response.Create().WithCallback(req => Mutate(d, req, isVoid: false)));
        Server.Given(Request.Create().WithPath(Base(d) + "void/").UsingMethod("POST")).AtPriority(1).RespondWith(Response.Create().WithCallback(req => Mutate(d, req, isVoid: true)));
    }

    // ───────────── ответы ─────────────

    private static ResponseMessage Reply(int status, string json) => new()
    {
        StatusCode = status,
        BodyData = new BodyData { DetectedBodyType = BodyType.String, BodyAsString = json },
        Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") },
    };

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public string DocJson(Doc d, bool replayed)
    {
        var now = DateTimeOffset.UtcNow;
        var until = d.Expired ? now.AddHours(-1) : now.AddHours(20);
        var canEdit = d.CanEdit && !d.Expired && !d.Voided;
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(d.Id).Append("\",\"version\":").Append(d.Version).Append(",\"status\":\"").Append(d.Voided ? "voided" : "posted").Append("\",");
        sb.Append("\"can_edit\":").Append(canEdit ? "true" : "false").Append(",\"can_void\":").Append(canEdit ? "true" : "false").Append(',');
        sb.Append("\"editable_until\":\"").Append(until.ToString("O")).Append("\",\"server_now\":\"").Append(now.ToString("O")).Append("\",");
        var block = d.BlockReason ?? (d.Expired ? "edit_window_expired" : null);
        if (block is not null && !canEdit)
            sb.Append("\"edit_block_reason\":\"").Append(block).Append("\",");
        if (replayed)
            sb.Append("\"replayed\":true,");
        var total = d.Lines.Sum(l => l.Qty * l.Price);
        if (d.Customer)
        {
            sb.Append("\"sale\":\"").Append(d.SaleId).Append("\",\"amount\":\"").Append(N(total)).Append("\",\"created_at\":\"2026-11-12T03:00:00Z\",\"items\":[");
            sb.Append(string.Join(",", d.Lines.Select(l => "{\"sale_item_id\":\"" + l.Id + "\",\"name\":\"" + l.Name + "\",\"unit\":\"" + l.Unit + "\",\"quantity\":\"" + N(l.Qty) + "\",\"price\":\"" + N(l.Price) + "\"}")));
        }
        else
        {
            sb.Append("\"number_display\":\"").Append(d.Number).Append("\",\"supplier_id\":\"s1\",\"supplier_name\":\"ОсОО «Поставщик»\",\"reason\":\"defect\",\"compensation\":\"debt_offset\",\"created_at\":\"2026-11-12T03:00:00Z\",\"comment\":\"\",");
            sb.Append("\"total_amount\":\"").Append(N(total)).Append("\",\"items\":[");
            sb.Append(string.Join(",", d.Lines.Select(l => "{\"id\":\"" + l.Id + "\",\"product_id\":\"" + l.ProductId + "\",\"product_name\":\"" + l.Name + "\",\"qty\":\"" + N(l.Qty) + "\",\"unit\":\"" + l.Unit + "\",\"purchase_price\":\"" + N(l.Price) + "\"}")));
        }
        sb.Append("]}");
        return sb.ToString();
    }

    public static string SaleJsonOf(Doc d) =>
        "{\"id\":\"" + d.SaleId + "\",\"number\":1115,\"status\":\"paid\",\"payment_method\":\"cash\",\"total\":\"" + N(d.Lines.Sum(l => l.Sold * l.Price)) + "\",\"items\":["
        + string.Join(",", d.Lines.Select(l => "{\"id\":\"" + l.Id + "\",\"sale_item_id\":\"" + l.Id + "\",\"product_id\":\"" + l.ProductId + "\",\"name\":\"" + l.Name + "\",\"unit\":\"" + l.Unit + "\",\"quantity\":\"" + N(l.Sold)
            + "\",\"unit_price\":\"" + N(l.Price) + "\",\"total\":\"" + N(l.Sold * l.Price) + "\",\"returnable_qty\":" + N(Math.Max(0, l.Sold - l.OtherReturned - l.Qty)) + "}")) + "]}";

    private static string? Header(IRequestMessage req, string name) =>
        req.Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

    // ───────────── PIN ─────────────

    private ResponseMessage PinVerify(IRequestMessage req)
    {
        if (PinForbidden)
            return Reply(403, "{\"code\":\"permission_denied\",\"detail\":\"Нет права подтверждать\"}");
        JsonNode? body;
        try { body = JsonNode.Parse(req.Body ?? "{}"); } catch (JsonException) { body = null; }
        var pin = body?["pin"]?.ToString();
        var scope = body?["scope"]?.ToString() ?? "";
        var objectType = body?["object_type"]?.ToString() ?? "";
        var objectId = body?["object_id"]?.ToString() ?? "";
        var versionOk = int.TryParse(body?["version"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version);
        if (pin != ValidPin)
            return Reply(400, "{\"pin\":[\"Неверный PIN\"],\"code\":\"pin_invalid\",\"attempts_left\":4}");
        if (scope is not ("return_edit" or "return_void") || objectType is not ("supplier" or "pos") || objectId.Length == 0 || !versionOk)
            return Reply(400, "{\"code\":\"validation_error\",\"detail\":\"scope/object_type/object_id/version\"}");
        lock (_gate)
        {
            var value = "tok-" + (++_tokenSeq).ToString(CultureInfo.InvariantCulture);
            _tokens[value] = new Token(value, scope, objectId, version, objectType);
            IssuedTokens.Add(value);
            return Reply(200, "{\"confirm_token\":\"" + value + "\",\"expires_in\":120,\"approved_by_name\":\"Айбек\"}");
        }
    }

    // ───────────── правка / аннулирование ─────────────

    private ResponseMessage Mutate(Doc d, IRequestMessage req, bool isVoid)
    {
        var ifMatch = Header(req, "If-Match");
        var confirm = Header(req, "X-Confirm-Token");
        var key = Header(req, "Idempotency-Key");
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(key))
                return Reply(400, "{\"code\":\"idempotency_key_required\",\"detail\":\"Idempotency-Key обязателен\"}");
            if (_byKey.TryGetValue(key, out var stored))
                return Reply(stored.Status, stored.Body.Replace("\"replayed\":false", "\"replayed\":true", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(ifMatch))
                return Reply(428, "{\"code\":\"version_required\",\"detail\":\"If-Match обязателен\"}");
            if (string.IsNullOrWhiteSpace(confirm))
                return Reply(428, "{\"code\":\"confirm_required\",\"detail\":\"Нужен X-Confirm-Token\"}");
            var scope = isVoid ? "return_void" : "return_edit";
            if (!_tokens.TryGetValue(confirm.Trim(), out var token) || token.Used || token.Scope != scope || token.ObjectId != d.Id)
                return Reply(403, "{\"code\":\"confirm_invalid\",\"detail\":\"Токен подтверждения не подходит\"}");
            if (!int.TryParse(ifMatch.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                return Reply(400, "{\"code\":\"validation_error\",\"detail\":\"If-Match\"}");
            if (token.Version != version)
                return Reply(403, "{\"code\":\"confirm_invalid\",\"detail\":\"Токен выдан на другую версию\"}");
            if (version != d.Version)
                return Reply(412, "{\"code\":\"version_conflict\",\"current_version\":" + d.Version + ",\"detail\":\"Документ изменён\"}");
            if (d.Voided)
                return Reply(409, "{\"code\":\"already_voided\",\"detail\":\"Уже аннулирован\"}");
            if (d.Expired || !d.CanEdit)
                return Reply(403, "{\"code\":\"" + (d.BlockReason ?? "edit_window_expired") + "\",\"detail\":\"Правка запрещена\"}");
            if (Override?.Invoke(d, isVoid) is { } custom)
                return custom; // отказ по существу: токен остаётся неиспользованным, документ не изменён
            token.Used = true;
            if (isVoid)
            {
                d.Voided = true;
                d.AppliedVoids++;
            }
            else
            {
                ApplyItems(d, req.Body);
                d.AppliedEdits++;
            }
            d.Version++;
            var json = DocJson(d, replayed: false).Replace("\"status\"", "\"replayed\":false,\"status\"", StringComparison.Ordinal);
            _byKey[key] = (200, json);
            if (DropNextResponseAfterApply)
            {
                DropNextResponseAfterApply = false;
                return new ResponseMessage { FaultType = FaultType.EMPTY_RESPONSE };
            }
            return Reply(200, json);
        }
    }

    /// <summary>Полный новый состав из тела PATCH: строки не из тела исчезают (количество 0 клиент не присылает).</summary>
    private static void ApplyItems(Doc d, string? body)
    {
        if (JsonNode.Parse(body ?? "{}")?["items"] is not JsonArray items)
            return;
        var next = new List<Line>();
        foreach (var it in items)
        {
            var id = it?[d.Customer ? "sale_item_id" : "id"]?.ToString() ?? "";
            var line = d.Lines.FirstOrDefault(l => l.Id == id);
            if (line is null)
                continue;
            var qty = double.Parse(it![d.Customer ? "quantity" : "qty"]!.ToString(), CultureInfo.InvariantCulture);
            var price = d.Customer ? line.Price : double.Parse(it["purchase_price"]!.ToString(), CultureInfo.InvariantCulture);
            line.Qty = qty;
            line.Price = price;
            next.Add(line);
        }
        d.Lines = next;
    }

    // ───────────── журнал запросов ─────────────

    public List<ILogEntry> Calls(string method, string pathContains) =>
        Server.LogEntries.Where(e => e.RequestMessage.Method == method && e.RequestMessage.Path.Contains(pathContains, StringComparison.Ordinal)).ToList();

    public List<ILogEntry> Pins => Calls("POST", "/pin/verify/");
    public List<ILogEntry> Patches => Server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH").ToList();
    public List<ILogEntry> Voids => Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.EndsWith("/void/", StringComparison.Ordinal)).ToList();

    public static JsonNode BodyOf(ILogEntry e) => JsonNode.Parse(e.RequestMessage.Body ?? "{}")!;

    public static string? HeaderOf(ILogEntry e, string name) =>
        e.RequestMessage.Headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        Server.Reset();
        Server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        Server.Given(Request.Create().WithPath("/api/main/capabilities/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[]"));
        try
        {
            await ServerCapabilities.LoadAsync(Api, force: true);
        }
        finally
        {
            ServerCapabilities.Reset();
            Server.Stop();
        }
    }
}
