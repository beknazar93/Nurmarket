using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// 2026-10-16: сквозная проверка ретранслятора NurRemoteSupport без dotnet test (в репозитории он запрещён).
// Сам запускает собранный сервер на 127.0.0.1:5088 с короткими таймаутами, играет кассу (/ws/host) и оператора
// (/ws/operator) по протоколу v1 и в конце гасит сервер. Запуск (из корня репозитория):
//   dotnet build NurRemoteSupport/Server -c Release
//   dotnet run --project NurRemoteSupport/SmokeTest -- [--server-dll путь\NurRemoteSupport.Server.dll] [--port 5088] [--show-log]
Console.OutputEncoding = Encoding.UTF8;
var port = 5088;
var showLog = false;
string? serverDll = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--server-dll" && i + 1 < args.Length)
        serverDll = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length)
        port = int.Parse(args[++i]);
    else if (args[i] == "--show-log")
        showLog = true;
}
serverDll = Path.GetFullPath(serverDll ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Server", "bin", "Release", "net8.0", "NurRemoteSupport.Server.dll"));
if (!File.Exists(serverDll))
{
    Console.WriteLine($"Нет сервера: {serverDll}. Сначала: dotnet build NurRemoteSupport/Server -c Release");
    return 2;
}

const string Key = "smoke-key-not-secret"; // тестовый ключ, только для этого прогона
const int Ttl = 8, ConsentTimeout = 4, PinLength = 6;
var ws = $"ws://127.0.0.1:{port}";
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
var pass = 0;
var fail = 0;
var pins = new List<string>();
var serverLog = new StringBuilder();

var psi = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(serverDll)!,
};
psi.ArgumentList.Add(serverDll);
foreach (var (k, v) in new Dictionary<string, string>
{
    ["BIND"] = "127.0.0.1", ["PORT"] = port.ToString(), ["OPERATOR_KEY"] = Key, ["CODE_TTL_SECONDS"] = Ttl.ToString(),
    ["CONSENT_TIMEOUT_SECONDS"] = ConsentTimeout.ToString(), ["PING_INTERVAL_SECONDS"] = "2", ["PIN_LENGTH"] = PinLength.ToString(),
    ["TRUSTED_PROXIES"] = "", ["ASPNETCORE_ENVIRONMENT"] = "Production",
})
    psi.Environment[k] = v;
using var server = Process.Start(psi)!;
server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverLog) serverLog.AppendLine(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverLog) serverLog.AppendLine(e.Data); };
server.BeginOutputReadLine();
server.BeginErrorReadLine();
try
{
    var up = false;
    for (var i = 0; i < 60 && !up && !server.HasExited; i++)
    {
        try
        {
            up = (await http.GetAsync("health")).IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            await Task.Delay(250);
        }
    }
    if (!up)
    {
        Console.WriteLine("Сервер не поднялся:\n" + serverLog);
        return 2;
    }
    Console.WriteLine($"Сервер запущен: {ws} (PID {server.Id})");
    await RunAllAsync();
}
finally
{
    try
    {
        server.Kill(entireProcessTree: true);
        server.WaitForExit(5000);
    }
    catch
    {
        // уже завершён
    }
}
if (showLog || fail > 0)
    Console.WriteLine("\n---- журнал сервера ----\n" + serverLog);
Console.WriteLine($"\nИТОГ: пройдено {pass}, не пройдено {fail}. Сервер остановлен.");
return fail == 0 ? 0 : 1;

// ---------------- сценарии ----------------

async Task RunAllAsync()
{
    Client? expiring = null;

    await Step("HTTP: /health, страница оператора, /api/info", async () =>
    {
        var health = JsonDocument.Parse(await http.GetStringAsync("health")).RootElement;
        Check(S(health, "status") == "ok", "/health -> status ok");
        var page = await http.GetAsync("");
        var html = await page.Content.ReadAsStringAsync();
        Check(page.IsSuccessStatusCode && html.Contains("<canvas") && html.Contains("operator.js"), "/ -> страница оператора с canvas");
        Check(page.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("script-src 'self'"), "CSP: только свои скрипты");
        Check((await http.GetAsync("operator.js")).IsSuccessStatusCode && (await http.GetAsync("operator.css")).IsSuccessStatusCode, "operator.js и operator.css отдаются");
        var info = JsonDocument.Parse(await http.GetStringAsync("api/info")).RootElement;
        Check(info.GetProperty("keyRequired").GetBoolean() && info.GetProperty("pinLength").GetInt32() == PinLength, "/api/info: нужен ключ оператора, длина PIN");
        Check((int)(await http.GetAsync("ws/operator")).StatusCode == 400, "/ws/operator без WebSocket -> 400");
    });

    await Step("Касса: hello -> код и PIN", async () =>
    {
        var a = await NewHostAsync();
        var b = await NewHostAsync();
        Check(Regex.IsMatch(a.Code, @"^[1-9]\d{8}$"), "код - 9 цифр (как ждёт окно кассы)");
        Check(Regex.IsMatch(a.Pin, $@"^\d{{{PinLength}}}$"), $"PIN - {PinLength} цифр (PIN_LENGTH)");
        Check(a.ExpiresIn == Ttl, "expiresIn = CODE_TTL_SECONDS");
        Check(a.Code != b.Code, "у двух касс разные коды");
        await b.Host.DisposeAsync();
        expiring = a.Host; // эта касса ждёт до конца прогона — проверка истечения кода

        var bad = await Client.ConnectAsync(ws + "/ws/host");
        await bad.SendAsync(new { type = "hello", role = "host", protocol = 2 });
        var e = await bad.NextJsonAsync();
        Check(S(e, "type") == "error" && S(e, "code") == "protocol" && await bad.ClosedAsync(), "hello с protocol 2 -> error protocol и закрытие");
        var junk = await Client.ConnectAsync(ws + "/ws/host");
        await junk.SendTextAsync("не json");
        e = await junk.NextJsonAsync();
        Check(S(e, "code") == "bad-hello" && await junk.ClosedAsync(), "мусор вместо hello -> error bad-hello");
    });

    await Step("Полный доступ: запрос -> согласие -> экран и ввод -> конец", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        await host.SendBinaryAsync(Packet(1, 9, 0, 0, 16, 16, new byte[50])); // кадр до сеанса — оператору не положен
        var (op, reply) = await JoinAsync(code, pin);
        Check(S(reply, "type") == "waiting" && S(reply, "company") == "Магазин Смоук", "оператору: waiting с названием магазина");
        var jr = await host.ExpectAsync("join-request");
        Check(S(jr, "operator") == "Тест Оператор", "кассе: join-request с именем оператора");
        var second = await JoinAsync(code, pin, "Другой");
        Check(S(second.Reply, "code") == "busy", "второй оператор, пока кассира спрашивают -> busy");
        await second.Op.DisposeAsync();

        await host.SendAsync(new { type = "consent", accept = true });
        var hs = await host.ExpectAsync("started");
        Check(S(hs, "operator") == "Тест Оператор", "кассе: started с тем же именем (касса сверяет с одобренным)");
        var os = await op.ExpectAsync("started");
        Check(!os.GetProperty("viewOnly").GetBoolean() && os.GetProperty("screen").GetProperty("w").GetInt32() == 1280,
            "оператору: started, полный доступ, экран 1280x720 из hello (кадр до сеанса не дошёл)");

        await host.SendAsync(new { type = "screen", w = 1280, h = 720, monitor = 0, monitors = 1 });
        Check((await op.ExpectAsync("screen")).GetProperty("h").GetInt32() == 720, "screen кассы -> оператору");
        var tile = Packet(1, 1, 128, 256, 128, 128, RandomBytes(3000));
        var frameEnd = Packet(2, 1, 0, 0, 0, 0, []);
        await host.SendBinaryAsync(tile);
        await host.SendBinaryAsync(frameEnd);
        Check((await op.ExpectBinaryAsync()).AsSpan().SequenceEqual(tile), "кусок кадра (JPEG) -> оператору байт в байт");
        Check((await op.ExpectBinaryAsync()).AsSpan().SequenceEqual(frameEnd), "конец кадра -> оператору");
        await host.SendAsync(new { type = "cursor", x = 10, y = 20 });
        Check((await op.ExpectAsync("cursor")).GetProperty("y").GetInt32() == 20, "курсор кассы -> оператору");
        await op.SendAsync(new { type = "ack", frame = 1 });
        Check((await host.ExpectAsync("ack")).GetProperty("frame").GetInt64() == 1, "ack оператора -> кассе (поток кадров идёт дальше)");

        await op.SendAsync(new { type = "mouse", action = "move", x = 100, y = 200 });
        var m = await host.ExpectAsync("mouse");
        Check(S(m, "action") == "move" && m.GetProperty("x").GetInt32() == 100 && m.GetProperty("y").GetInt32() == 200, "мышь move -> кассе");
        await op.SendAsync(new { type = "mouse", action = "down", x = 5, y = 6, button = 2 });
        m = await host.ExpectAsync("mouse");
        Check(S(m, "action") == "down" && m.GetProperty("button").GetInt32() == 2, "правая кнопка down -> кассе");
        await op.SendAsync(new { type = "mouse", action = "wheel", x = 5, y = 6, delta = -5000 });
        Check((await host.ExpectAsync("mouse")).GetProperty("delta").GetInt32() == -1200, "колесо -> кассе, delta ограничена 1200");
        await op.SendAsync(new { type = "key", action = "down", code = "KeyA", key = "a" });
        var k = await host.ExpectAsync("key");
        Check(S(k, "code") == "KeyA" && S(k, "key") == "a" && S(k, "action") == "down", "клавиша -> кассе");
        await op.SendAsync(new { type = "started", @operator = "Злоумышленник" });
        await op.SendAsync(new { type = "consent", accept = true });
        await op.SendAsync(new { type = "key", action = "down", code = "Key<A>" });
        await op.SendTextAsync("не json");
        await op.SendAsync(new { type = "text", text = "secret-text-42" });
        Check(S(await host.ExpectAsync("text"), "text") == "secret-text-42", "текст -> кассе; started/consent/кривая клавиша/не-JSON от оператора отброшены");
        await op.SendAsync(new { type = "quality", value = 5 });
        Check((await host.ExpectAsync("quality")).GetProperty("value").GetInt32() == 10, "quality -> кассе (ограничено 10..90)");

        await op.SendAsync(new { type = "view-only", value = true });
        Check((await host.ExpectAsync("view-only")).GetProperty("value").GetBoolean(), "оператор включил просмотр -> кассе view-only true");
        var ov = await op.ExpectAsync("view-only");
        Check(ov.GetProperty("value").GetBoolean() && !ov.GetProperty("cashier").GetBoolean(), "оператору: view-only подтверждён");
        await op.SendAsync(new { type = "mouse", action = "move", x = 1, y = 1 });
        Check(await host.NothingAsync(), "в режиме просмотра мышь на кассу не уходит");
        await op.SendAsync(new { type = "view-only", value = false });
        Check(!(await host.ExpectAsync("view-only")).GetProperty("value").GetBoolean(), "оператор снял свой просмотр -> кассе view-only false");
        await op.ExpectAsync("view-only");
        await op.SendAsync(new { type = "mouse", action = "move", x = 2, y = 2 });
        Check((await host.ExpectAsync("mouse")).GetProperty("x").GetInt32() == 2, "после снятия просмотра мышь снова идёт");

        await op.SendAsync(new { type = "end" });
        Check(S(await host.ExpectAsync("end"), "reason") == "operator", "оператор завершил -> кассе end operator");
        Check(S(await op.ExpectAsync("end"), "reason") == "operator", "оператору: end operator");
        Check(await host.ClosedAsync() && await op.ClosedAsync(), "оба соединения закрыты сервером");
        var again = await JoinAsync(code, pin);
        Check(S(again.Reply, "code") == "not-found", "после сеанса код больше не действует");
        await again.Op.DisposeAsync();
    });

    await Step("Только просмотр (выбор кассира): сервер отбрасывает ввод", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var (op, _) = await JoinAsync(code, pin, "Оператор Два");
        await host.ExpectAsync("join-request");
        await host.SendAsync(new { type = "consent", accept = true, view_only = true });
        await host.ExpectAsync("started");
        Check((await op.ExpectAsync("started")).GetProperty("viewOnly").GetBoolean(), "consent view_only:true -> оператору started viewOnly true");
        await op.SendAsync(new { type = "mouse", action = "down", x = 5, y = 5, button = 0 });
        await op.SendAsync(new { type = "key", action = "down", code = "Enter" });
        await op.SendAsync(new { type = "text", text = "abc" });
        Check(await host.NothingAsync(), "мышь, клавиши и текст до кассы не дошли");
        await op.SendAsync(new { type = "ack", frame = 3 });
        Check((await host.ExpectAsync("ack")).GetProperty("frame").GetInt64() == 3, "ack в просмотре проходит (экран идёт)");
        await op.SendAsync(new { type = "view-only", value = false });
        Check((await host.ExpectAsync("view-only")).GetProperty("value").GetBoolean(), "снять выбор кассира оператор не может (кассе view-only true)");
        var ov = await op.ExpectAsync("view-only");
        Check(ov.GetProperty("value").GetBoolean() && ov.GetProperty("cashier").GetBoolean(), "оператору: просмотр остаётся, его выбрал кассир");
        await op.SendAsync(new { type = "key", action = "down", code = "Enter" });
        Check(await host.NothingAsync(), "клавиша после попытки снять просмотр - не дошла");
        await host.SendAsync(new { type = "end", reason = "host" });
        Check(S(await op.ExpectAsync("end"), "reason") == "host", "кассир завершил -> оператору end host");
        Check(await op.ClosedAsync(), "оператор отключён");
        await host.DisposeAsync();
    });

    await Step("Отказ кассира, уход оператора до ответа, повторный запрос", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var (op1, _) = await JoinAsync(code, pin, "Первый");
        await host.ExpectAsync("join-request");
        await host.SendAsync(new { type = "consent", accept = false });
        Check(S(await op1.ExpectAsync("end"), "reason") == "declined", "отказ -> оператору end declined");
        Check(await host.NothingAsync(400), "касса после отказа ждёт дальше (код действует)");
        var (op2, _) = await JoinAsync(code, pin, "Второй");
        Check(S(await host.ExpectAsync("join-request"), "operator") == "Второй", "тем же кодом можно попросить снова");
        op2.Abort();
        await Task.Delay(300);
        await host.SendAsync(new { type = "consent", accept = true });
        Check(await host.NothingAsync(), "оператор ушёл до ответа - согласие сеанс не начинает");
        var (op3, r3) = await JoinAsync(code, pin, "Третий");
        Check(S(r3, "code") == "busy", "пока касса не сняла согласие - busy (касса сама снимает его через 30 с)");
        await op3.DisposeAsync();
        await host.SendAsync(new { type = "consent", accept = false }); // так касса снимает согласие по таймеру
        await Task.Delay(200);
        var (op4, r4) = await JoinAsync(code, pin, "Четвёртый");
        Check(S(r4, "type") == "waiting", "после consent accept:false касса снова свободна");
        await host.ExpectAsync("join-request");
        await host.SendAsync(new { type = "consent", accept = true });
        await host.ExpectAsync("started");
        await op4.ExpectAsync("started");
        op4.Abort();
        Check(S(await host.ExpectAsync("end"), "reason") == "disconnect", "оператор пропал в сеансе -> кассе end disconnect");
        Check(await host.ClosedAsync(), "касса отключена");
        await op1.DisposeAsync();
    });

    await Step($"Кассир не отвечает (CONSENT_TIMEOUT_SECONDS={ConsentTimeout})", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var (op, _) = await JoinAsync(code, pin);
        await host.ExpectAsync("join-request");
        Check(S(await op.ExpectAsync("end", (ConsentTimeout + 3) * 1000), "reason") == "consent-timeout", "оператору: end consent-timeout");
        Check(S(await host.ExpectAsync("end"), "reason") == "consent-timeout", "кассе: end consent-timeout");
        Check(await host.ClosedAsync(), "касса отключена");
    });

    await Step("Слишком большое сообщение оператора (MAX_TEXT_BYTES)", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var (op, _) = await JoinAsync(code, pin);
        await host.ExpectAsync("join-request");
        await host.SendAsync(new { type = "consent", accept = true });
        await host.ExpectAsync("started");
        await op.ExpectAsync("started");
        try
        {
            await op.SendTextAsync("{\"type\":\"text\",\"text\":\"" + new string('x', 100_000) + "\"}");
        }
        catch (WebSocketException)
        {
            // сервер уже закрыл соединение
        }
        Check(await op.ClosedAsync() && op.CloseStatus == WebSocketCloseStatus.MessageTooBig, "оператор отключён с кодом 1009");
        Check(S(await host.ExpectAsync("end"), "reason") == "disconnect", "кассе: end disconnect");
    });

    await Step("Неверный PIN 5 раз -> код заблокирован", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var wrong = pin[..^1] + (char)('0' + (pin[^1] - '0' + 1) % 10);
        for (var i = 1; i <= 4; i++)
        {
            var (op, r) = await JoinAsync(code, wrong);
            Check(S(r, "code") == "wrong-pin" && r.GetProperty("attemptsLeft").GetInt32() == 5 - i, $"неверный PIN #{i} -> wrong-pin, осталось {5 - i}");
            await op.DisposeAsync();
        }
        var (op5, r5) = await JoinAsync(code, long.Parse(wrong)); // PIN числом — тоже принимается
        Check(S(r5, "code") == "locked", "5-й неверный PIN (числом) -> locked");
        await host.ExpectAsync("locked");
        Check(await host.ClosedAsync(), "кассе: locked, соединение закрыто");
        var (op6, r6) = await JoinAsync(code, pin);
        Check(S(r6, "code") == "not-found", "верный PIN после блокировки уже не помогает");
        await op5.DisposeAsync();
        await op6.DisposeAsync();
    });

    await Step("Ключ оператора (OPERATOR_KEY) и чужой сайт (Origin)", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        var (op, r) = await JoinAsync(code, pin, key: "wrong-key");
        Check(S(r, "code") == "bad-key", "неверный ключ -> bad-key");
        Check(await host.NothingAsync(), "касса запроса не получила");
        await op.DisposeAsync();
        await host.DisposeAsync();
        try
        {
            var evil = await Client.ConnectAsync(ws + "/ws/operator", "http://evil.example");
            Check(false, "Origin чужого сайта должен получить отказ");
            await evil.DisposeAsync();
        }
        catch (WebSocketException)
        {
            Check(true, "Origin чужого сайта -> отказ при подключении (403)");
        }
        var own = await Client.ConnectAsync(ws + "/ws/operator", $"http://127.0.0.1:{port}");
        Check(true, "свой Origin -> подключение принято");
        await own.DisposeAsync();
    });

    await Step("Перебор кодов -> rate-limit по IP (JOIN_FAIL_LIMIT=10)", async () =>
    {
        var (host, code, pin, _) = await NewHostAsync();
        string? last = null;
        var tries = 0;
        while (tries < 6 && last != "rate-limit")
        {
            tries++;
            var (op, r) = await JoinAsync("999999999", "000000");
            last = S(r, "code");
            if (last == "rate-limit")
                Check(r.GetProperty("retryAfter").GetInt32() > 0, "rate-limit говорит, сколько ждать (retryAfter)");
            await op.DisposeAsync();
        }
        Check(last == "rate-limit", $"после 10 неудач с IP - rate-limit (на {tries}-й попытке перебора)");
        var (op2, r2) = await JoinAsync(code, pin);
        Check(S(r2, "code") == "rate-limit", "даже верные код и PIN с этого IP - отказ до конца окна");
        Check(await host.NothingAsync(), "касса запроса не получила");
        await op2.DisposeAsync();
        await host.DisposeAsync();
    });

    await Step($"Код истекает (CODE_TTL_SECONDS={Ttl}), ping/pong держит связь", async () =>
    {
        var h = expiring ?? throw new Exception("касса для проверки не создана");
        await h.ExpectAsync("expired", (Ttl + 5) * 1000);
        Check(true, "кассе: expired");
        Check(h.Pings >= 2, $"касса получала ping и отвечала pong ({h.Pings} раз)");
        Check(await h.ClosedAsync(), "касса отключена после истечения");
    });

    await Step("Журнал сервера: без PIN и содержимого", async () =>
    {
        await Task.Delay(300);
        string log;
        lock (serverLog)
            log = serverLog.ToString();
        Check(Regex.Matches(log, @"\d{9}").Count > 5, "в журнале есть события по кодам");
        var leaked = pins.Count(p => Regex.IsMatch(log, $@"(?<!\d){p}(?!\d)"));
        Check(leaked == 0, $"ни один из {pins.Count} PIN не попал в журнал");
        Check(!log.Contains("secret-text-42"), "текст оператора не попал в журнал");
    });
}

async Task<(Client Host, string Code, string Pin, int ExpiresIn)> NewHostAsync()
{
    var h = await Client.ConnectAsync(ws + "/ws/host");
    await h.SendAsync(new
    {
        type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version = "1.17.58", company = "Магазин Смоук",
        machine = "SMOKE-PC", screen = new { w = 1280, h = 720 },
    });
    var s = await h.ExpectAsync("session");
    var pin = S(s, "pin")!;
    lock (pins)
        pins.Add(pin);
    return (h, S(s, "code")!, pin, s.GetProperty("expiresIn").GetInt32());
}

async Task<(Client Op, JsonElement Reply)> JoinAsync(string code, object pin, string name = "Тест Оператор", string? key = Key)
{
    var op = await Client.ConnectAsync(ws + "/ws/operator");
    await op.SendAsync(new { type = "join", code, pin, name, key });
    return (op, await op.NextJsonAsync());
}

async Task Step(string name, Func<Task> body)
{
    Console.WriteLine($"\n== {name}");
    try
    {
        await body().WaitAsync(TimeSpan.FromSeconds(60));
    }
    catch (Exception ex)
    {
        fail++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void Check(bool ok, string what)
{
    if (ok)
        pass++;
    else
        fail++;
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}");
}

static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static byte[] RandomBytes(int n)
{
    var b = new byte[n];
    Random.Shared.NextBytes(b);
    return b;
}

static byte[] Packet(byte kind, uint frame, ushort x, ushort y, ushort w, ushort h, byte[] jpeg)
{
    var p = new byte[13 + jpeg.Length];
    p[0] = kind;
    BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), frame);
    BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), x);
    BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7), y);
    BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), w);
    BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), h);
    jpeg.CopyTo(p, 13);
    return p;
}

/// <summary>2026-10-16: тестовый WebSocket-клиент: сам отвечает «pong» на «ping», остальное — в очередь.</summary>
sealed class Client : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<(bool Binary, byte[] Data)> _in = Channel.CreateUnbounded<(bool, byte[])>();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pings;
    private volatile bool _closed;

    public int Pings => Volatile.Read(ref _pings);
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public static async Task<Client> ConnectAsync(string url, string? origin = null)
    {
        var c = new Client();
        if (origin is not null)
            c._ws.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(5000);
        await c._ws.ConnectAsync(new Uri(url), cts.Token);
        _ = c.PumpAsync();
        return c;
    }

    private async Task PumpAsync()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        CloseStatus = r.CloseStatus;
                        await _send.WaitAsync();
                        try
                        {
                            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        }
                        catch
                        {
                            // уже закрыто
                        }
                        finally
                        {
                            _send.Release();
                        }
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var data = ms.ToArray();
                if (r.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(data) == "{\"type\":\"ping\"}")
                {
                    Interlocked.Increment(ref _pings);
                    await SendTextAsync("{\"type\":\"pong\"}");
                    continue;
                }
                _in.Writer.TryWrite((r.MessageType == WebSocketMessageType.Binary, data));
            }
        }
        catch
        {
            // соединение оборвано
        }
        finally
        {
            _closed = true;
            _in.Writer.TryComplete();
            _done.TrySetResult();
        }
    }

    public Task SendAsync(object message) => SendTextAsync(JsonSerializer.Serialize(message));

    public Task SendTextAsync(string text) => SendRawAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

    public Task SendBinaryAsync(byte[] data) => SendRawAsync(data, WebSocketMessageType.Binary);

    private async Task SendRawAsync(byte[] data, WebSocketMessageType type)
    {
        await _send.WaitAsync();
        try
        {
            await _ws.SendAsync(data, type, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    public async Task<(bool Binary, byte[] Data)?> NextAsync(int ms = 3000)
    {
        using var cts = new CancellationTokenSource(ms);
        try
        {
            return await _in.Reader.ReadAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    public async Task<JsonElement> NextJsonAsync(int ms = 3000)
    {
        var m = await NextAsync(ms) ?? throw new Exception("сервер ничего не прислал");
        if (m.Binary)
            throw new Exception("пришёл двоичный кадр вместо JSON");
        return JsonDocument.Parse(m.Data).RootElement;
    }

    public async Task<JsonElement> ExpectAsync(string type, int ms = 3000)
    {
        var m = await NextAsync(ms) ?? throw new Exception($"ждали «{type}», ничего не пришло");
        if (m.Binary)
            throw new Exception($"ждали «{type}», пришёл двоичный кадр");
        var e = JsonDocument.Parse(m.Data).RootElement;
        if (!e.TryGetProperty("type", out var t) || t.GetString() != type)
            throw new Exception($"ждали «{type}», пришло: {e.GetRawText()}");
        return e;
    }

    public async Task<byte[]> ExpectBinaryAsync(int ms = 3000)
    {
        var m = await NextAsync(ms) ?? throw new Exception("ждали двоичный кадр, ничего не пришло");
        if (!m.Binary)
            throw new Exception($"ждали двоичный кадр, пришло: {Encoding.UTF8.GetString(m.Data)}");
        return m.Data;
    }

    /// <summary>За ms ничего не пришло и соединение живо.</summary>
    public async Task<bool> NothingAsync(int ms = 500)
    {
        var m = await NextAsync(ms);
        if (m is { } got)
            Console.WriteLine($"     (лишнее сообщение: {(got.Binary ? "двоичное" : Encoding.UTF8.GetString(got.Data))})");
        return m is null && !_closed;
    }

    /// <summary>Сервер закрыл соединение (оставшиеся сообщения пропускаются).</summary>
    public async Task<bool> ClosedAsync(int ms = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (await NextAsync((int)Math.Max(1, ms - sw.ElapsedMilliseconds)) is null)
                return _closed;
        }
        return _closed;
    }

    public void Abort() => _ws.Abort();

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            await _send.WaitAsync();
            try
            {
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch
            {
                // уже закрыто
            }
            finally
            {
                _send.Release();
            }
        }
        await Task.WhenAny(_done.Task, Task.Delay(2000));
        _ws.Dispose();
    }
}
