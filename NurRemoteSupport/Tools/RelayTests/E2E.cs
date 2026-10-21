using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurRemoteSupport.RelayTests;

/// <summary>Сквозная проверка ретранслятора по PROTOCOL.md (2026-09-29).</summary>
public sealed class E2E(string dll)
{
    private const string Login = "aibek";
    private const string Name = "Айбек Тестов";
    private const string Password = "Test-Passw0rd!";
    private int _pass, _fail;
    private readonly List<string> _failures = new();

    public async Task<int> RunAsync()
    {
        Console.WriteLine("=== Сервер A (обычные таймеры) ===");
        await using (var a = await ServerProcess.StartAsync(dll, new()
        {
            ["JoinPerMinute"] = "1000",
            ["LoginPerMinutePerIp"] = "100",
            ["HostConnectsPerMinutePerIp"] = "1000",
            ["MaxPendingPerIp"] = "1000",
        }))
        {
            await SuiteA(a);
            if (_fail > 0) DumpLog(a);
        }

        Console.WriteLine("=== Сервер B (короткие таймеры и пределы) ===");
        await using (var b = await ServerProcess.StartAsync(dll, new()
        {
            ["SessionTtlSeconds"] = "3",
            ["ConsentTimeoutSeconds"] = "2",
            ["PingIntervalSeconds"] = "1",
            ["IdleTimeoutSeconds"] = "3",
            ["SendStallSeconds"] = "3",
            ["SendQueueBytes"] = (4 * 1024 * 1024).ToString(),
            ["JoinPerMinute"] = "6",
            ["LoginPerMinutePerIp"] = "3",
            ["HostConnectsPerMinutePerIp"] = "8",
            ["MaxPendingPerIp"] = "100",
        }))
        {
            var before = _fail;
            await SuiteB(b);
            if (_fail > before) DumpLog(b);
        }

        Console.WriteLine();
        Console.WriteLine($"ИТОГ: пройдено {_pass}, провалено {_fail}");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        return _fail == 0 ? 0 : 1;
    }

    private static void DumpLog(ServerProcess s)
    {
        Console.WriteLine("--- хвост лога сервера ---");
        string log;
        lock (s.Log) log = s.Log.ToString();
        foreach (var l in log.Split('\n').TakeLast(40)) Console.WriteLine("  | " + l.TrimEnd());
    }

    private async Task Step(string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await body().WaitAsync(TimeSpan.FromSeconds(60));
            _pass++;
            Console.WriteLine($"  OK    {name} ({sw.ElapsedMilliseconds} мс)");
        }
        catch (Exception ex)
        {
            _fail++;
            _failures.Add(name + ": " + ex.Message);
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    // ---------- помощники ----------

    private static async Task<(TestWs host, string code, string pin)> NewHost(ServerProcess s, string company = "Тест Компания", object? hello = null)
    {
        var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
        await h.SendJsonAsync(hello ?? new
        {
            type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version = "9.9.9",
            company, machine = "TEST-PC", screen = new { w = 1920, h = 1080 },
        });
        var m = await h.ExpectAsync("session");
        return (h, m.GetProperty("code").GetString()!, m.GetProperty("pin").GetString()!);
    }

    private static Task<TestWs> NewOperator(ServerProcess s, string token) =>
        TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(token));

    private static async Task<(TestWs host, TestWs op, string sessionId, string code)> Started(ServerProcess s, string token, string company = "Тест Компания")
    {
        var (h, code, pin) = await NewHost(s, company);
        var op = await NewOperator(s, token);
        await op.SendJsonAsync(new { type = "join", code, pin });
        await op.ExpectAsync("waiting-consent");
        await h.ExpectAsync("join-request");
        await h.SendJsonAsync(new { type = "consent", accept = true });
        var hs = await h.ExpectAsync("started");
        await op.ExpectAsync("started");
        return (h, op, hs.GetProperty("sessionId").GetString()!, code);
    }

    private static string WrongPin(string pin) => ((int.Parse(pin) + 1) % 10000).ToString("D4");

    private static byte[] Tile(byte kind, uint frame, int x, int y, int w, int h, int payload, Random rnd)
    {
        var p = new byte[13 + payload];
        p[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), frame);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7), (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), (ushort)h);
        rnd.NextBytes(p.AsSpan(13));
        return p;
    }

    private static async Task<JsonElement> Sessions(ServerProcess s, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sessions?limit=500");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r = await s.Http.SendAsync(req);
        Assert(r.IsSuccessStatusCode, "/api/sessions: " + (int)r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> JournalFor(ServerProcess s, string token, string sessionIdOrCode)
    {
        for (var i = 0; i < 20; i++)
        {
            var j = await Sessions(s, token);
            foreach (var e in j.GetProperty("recent").EnumerateArray())
                if (e.GetProperty("sessionId").GetString() == sessionIdOrCode || e.GetProperty("code").GetString() == sessionIdOrCode)
                    return e;
            await Task.Delay(100);
        }
        throw new Exception("в журнале нет записи " + sessionIdOrCode);
    }

    // ---------- сервер A ----------

    private async Task SuiteA(ServerProcess s)
    {
        string token = "";

        await Step("/health = ok", async () =>
        {
            Assert(await s.Http.GetStringAsync("/health") == "ok", "не ok");
        });

        await Step("CLI add-operator (пароль со stdin), list-operators, короткий пароль отклонён", async () =>
        {
            var (c1, o1) = await s.CliAsync(dll, $"add-operator {Login} \"{Name}\"", Password);
            Assert(c1 == 0 && o1.Contains("добавлен"), "add-operator: " + o1);
            var (c2, o2) = await s.CliAsync(dll, "list-operators", null);
            Assert(c2 == 0 && o2.Contains(Login) && o2.Contains(Name), "list-operators: " + o2);
            var (c3, o3) = await s.CliAsync(dll, "add-operator shorty Коротко", "123");
            Assert(c3 == 2, "короткий пароль принят: " + o3);
            var json = await File.ReadAllTextAsync(Path.Combine(s.DataDir, "operators.json"));
            Assert(json.Contains("pbkdf2-sha256$") && !json.Contains(Password), "в файле не хэш");
        });

        await Step("вход: неверный пароль 401, неизвестный логин 401, верный → токен", async () =>
        {
            var bad = await s.Http.PostAsJsonAsync("/api/login", new { login = Login, password = "wrong-password" });
            Assert(bad.StatusCode == HttpStatusCode.Unauthorized, "неверный пароль: " + (int)bad.StatusCode);
            var ghost = await s.Http.PostAsJsonAsync("/api/login", new { login = "nobody", password = "whatever1" });
            Assert(ghost.StatusCode == HttpStatusCode.Unauthorized, "нет логина: " + (int)ghost.StatusCode);
            token = await s.LoginAsync(Login, Password);
            Assert(token.Contains('.'), "токен странный");
        });

        await Step("сброс пароля отзывает старый токен", async () =>
        {
            var (c, o) = await s.CliAsync(dll, $"add-operator {Login} \"{Name}\"", Password);
            Assert(c == 0 && o.Contains("сброшен"), "reset: " + o);
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var r = await s.Http.SendAsync(req);
            Assert(r.StatusCode == HttpStatusCode.Unauthorized, "старый токен жив: " + (int)r.StatusCode);
            token = await s.LoginAsync(Login, Password);
        });

        await Step("/api/me и /api/sessions: без токена 401, с поддельным 401, с верным 200", async () =>
        {
            Assert((await s.Http.GetAsync("/api/sessions")).StatusCode == HttpStatusCode.Unauthorized, "без токена");
            using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/sessions"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token[..^3] + "AAA");
                Assert((await s.Http.SendAsync(req)).StatusCode == HttpStatusCode.Unauthorized, "поддельный токен принят");
            }
            using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/me"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var r = await s.Http.SendAsync(req);
                var j = await r.Content.ReadFromJsonAsync<JsonElement>();
                Assert(j.GetProperty("name").GetString() == Name, "имя: " + j);
            }
            await Sessions(s, token);
        });

        await Step("10 неудачных входов на логин → 429", async () =>
        {
            var got429 = false;
            for (var i = 0; i < 12 && !got429; i++)
            {
                var r = await s.Http.PostAsJsonAsync("/api/login", new { login = "victim", password = "bad" + i });
                got429 = r.StatusCode == HttpStatusCode.TooManyRequests;
            }
            Assert(got429, "блокировки по логину нет");
        });

        await Step("WS оператора без токена/с плохим — отказ", async () =>
        {
            var failed = 0;
            foreach (var url in new[] { s.WsUrl + "/ws/operator", s.WsUrl + "/ws/operator?token=abc.def" })
            {
                try { await using var w = await TestWs.ConnectAsync(url); }
                catch (WebSocketException) { failed++; }
            }
            Assert(failed == 2, "подключился без токена");
        });

        await Step("hello с protocol=2 и мусор вместо hello → error protocol и закрытие", async () =>
        {
            await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
            await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 2 });
            var e = await h.ExpectAsync("error");
            Assert(e.GetProperty("code").GetString() == "protocol", "код: " + e);
            Assert(await h.WaitClosedAsync(), "не закрыто");
            await using var h2 = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
            await h2.SendRawTextAsync("not json");
            Assert((await h2.ExpectAsync("error")).GetProperty("code").GetString() == "protocol", "мусор");
        });

        TestWs? host = null, op = null;
        string code = "", pin = "";
        await Step("hello → код 9 цифр, PIN 4 цифры, expiresIn 900", async () =>
        {
            (host, code, pin) = await NewHost(s);
            Assert(code.Length == 9 && code.All(char.IsAsciiDigit) && code[0] != '0', "код " + code);
            Assert(pin.Length == 4 && pin.All(char.IsAsciiDigit), "PIN " + pin);
        });

        await Step("join: чужой код → no-session, короткий код → no-session", async () =>
        {
            op = await NewOperator(s, token);
            var other = code == "111111111" ? "222222222" : "111111111";
            await op.SendJsonAsync(new { type = "join", code = other, pin = "0000" });
            Assert((await op.ExpectAsync("error")).GetProperty("code").GetString() == "no-session", "чужой код");
            await op.SendJsonAsync(new { type = "join", code = "12", pin = "0000" });
            Assert((await op.ExpectAsync("error")).GetProperty("code").GetString() == "no-session", "короткий код");
        });

        await Step("join: неверный PIN → bad-pin, left=4", async () =>
        {
            await op!.SendJsonAsync(new { type = "join", code, pin = WrongPin(pin) });
            var e = await op.ExpectAsync("error");
            Assert(e.GetProperty("code").GetString() == "bad-pin" && e.GetProperty("left").GetInt32() == 4, e.ToString());
        });

        await Step("join: верный (код с пробелами) → waiting-consent; хосту join-request с именем оператора", async () =>
        {
            await op!.SendJsonAsync(new { type = "join", code = $"{code[..3]} {code[3..6]} {code[6..]}", pin });
            await op.ExpectAsync("waiting-consent");
            var jr = await host!.ExpectAsync("join-request");
            Assert(jr.GetProperty("operator").GetString() == Name && jr.GetProperty("login").GetString() == Login, jr.ToString());
            Assert(jr.GetProperty("ip").GetString() == "127.0.0.1", "ip " + jr);
        });

        await Step("второй оператор на тот же код → busy; повторный join на том же соединении → bad-request", async () =>
        {
            await using var op2 = await NewOperator(s, token);
            await op2.SendJsonAsync(new { type = "join", code, pin });
            Assert((await op2.ExpectAsync("error")).GetProperty("code").GetString() == "busy", "не busy");
            await op!.SendJsonAsync(new { type = "join", code, pin });
            Assert((await op.ExpectAsync("error")).GetProperty("code").GetString() == "bad-request", "не bad-request");
        });

        string sessionId = "";
        await Step("согласие → started обоим (оператору — данные кассы)", async () =>
        {
            await host!.SendJsonAsync(new { type = "consent", accept = true });
            var hs = await host.ExpectAsync("started");
            var os = await op!.ExpectAsync("started");
            sessionId = hs.GetProperty("sessionId").GetString()!;
            Assert(os.GetProperty("sessionId").GetString() == sessionId, "разные id");
            Assert(hs.GetProperty("operator").GetString() == Name, "имя у хоста");
            var hi = os.GetProperty("host");
            Assert(hi.GetProperty("company").GetString() == "Тест Компания" && hi.GetProperty("screen").GetProperty("w").GetInt32() == 1920, hi.ToString());
        });

        await Step("/api/sessions: активный сеанс виден, PIN и код не раскрываются", async () =>
        {
            var j = await Sessions(s, token);
            var raw = j.ToString();
            var a = j.GetProperty("active").EnumerateArray().FirstOrDefault(e => e.GetProperty("sessionId").GetString() == sessionId);
            Assert(a.ValueKind == JsonValueKind.Object && a.GetProperty("state").GetString() == "active", raw);
            Assert(!raw.Contains(pin + "\"") && !raw.Contains(code), "в API код или PIN");
        });

        await Step("150 кадров (куски до 200 КБ) + screen/cursor: порядок и байты совпадают, ack доходят хосту", async () =>
        {
            var rnd = new Random(42);
            var expected = new List<string>(); // sha256 каждого сообщения по порядку
            const int frames = 150;
            var opTask = Task.Run(async () =>
            {
                var idx = 0;
                var acked = 0;
                while (acked < frames)
                {
                    var m = await op!.NextAsync(10000) ?? throw new Exception($"оператор: поток оборвался на {idx}");
                    string hash = Convert.ToHexString(SHA256.HashData(m.Data));
                    string exp;
                    lock (expected) exp = idx < expected.Count ? expected[idx] : "";
                    if (hash != exp) throw new Exception($"сообщение {idx} не совпало ({TestWs.Short(m)})");
                    idx++;
                    if (m.Type == WebSocketMessageType.Binary && m.Data[0] == 2)
                    {
                        var f = BinaryPrimitives.ReadUInt32LittleEndian(m.Data.AsSpan(1));
                        await op.SendJsonAsync(new { type = "ack", frame = f });
                        acked++;
                    }
                }
                return idx;
            });
            long lastAck = 0;
            async Task Send(byte[] data, WebSocketMessageType t)
            {
                lock (expected) expected.Add(Convert.ToHexString(SHA256.HashData(data)));
                await host!.SendAsync(data, t);
            }
            await Send(Encoding.UTF8.GetBytes("{\"type\":\"screen\",\"w\":1920,\"h\":1080,\"monitor\":0,\"monitors\":2}"), WebSocketMessageType.Text);
            for (uint f = 1; f <= frames; f++)
            {
                while (f - lastAck > 2)
                {
                    var a = await host!.ExpectAsync("ack", 10000);
                    var n = a.GetProperty("frame").GetInt64();
                    Assert(n == lastAck + 1, $"ack не по порядку: {n} после {lastAck}");
                    lastAck = n;
                }
                var tiles = 1 + rnd.Next(3);
                for (var t = 0; t < tiles; t++)
                    await Send(Tile(1, f, t * 128, 0, 128, 128, rnd.Next(1, 200 * 1024), rnd), WebSocketMessageType.Binary);
                if (f % 10 == 0) await Send(Encoding.UTF8.GetBytes($"{{\"type\":\"cursor\",\"x\":{f},\"y\":{f * 2}}}"), WebSocketMessageType.Text);
                await Send(Tile(2, f, 0, 0, 0, 0, 0, rnd), WebSocketMessageType.Binary);
            }
            while (lastAck < frames)
            {
                var a = await host!.ExpectAsync("ack", 10000);
                lastAck = a.GetProperty("frame").GetInt64();
            }
            var count = await opTask;
            Assert(count == expected.Count, $"пришло {count} из {expected.Count}");
        });

        await Step("оператор → касса: mouse/key/text(кириллица 5000)/quality/view-only/monitor доходят байт в байт", async () =>
        {
            var msgs = new[]
            {
                "{\"type\":\"mouse\",\"action\":\"move\",\"x\":100,\"y\":200}",
                "{\"type\":\"mouse\",\"action\":\"down\",\"x\":100,\"y\":200,\"button\":0}",
                "{\"type\":\"mouse\",\"action\":\"up\",\"x\":100,\"y\":200,\"button\":0}",
                "{\"type\":\"mouse\",\"action\":\"down\",\"x\":5,\"y\":6,\"button\":2}",
                "{\"type\":\"mouse\",\"action\":\"wheel\",\"x\":5,\"y\":6,\"delta\":-120}",
                "{\"type\":\"key\",\"action\":\"down\",\"code\":\"KeyA\",\"key\":\"a\"}",
                "{\"type\":\"key\",\"action\":\"up\",\"code\":\"KeyA\",\"key\":\"a\"}",
                "{\"type\":\"text\",\"text\":\"" + string.Concat(Enumerable.Repeat("Привет, касса! ", 333)) + "\"}",
                "{\"type\":\"quality\",\"value\":30}",
                "{\"type\":\"view-only\",\"value\":true}",
                "{\"type\":\"monitor\",\"index\":1}",
            };
            foreach (var m in msgs) await op!.SendRawTextAsync(m);
            foreach (var m in msgs)
            {
                var got = await host!.NextAsync() ?? throw new Exception("хост не получил " + m[..30]);
                Assert(got.Text == m, $"искажено: {TestWs.Short(got)}");
            }
        });

        await Step("служебные типы не пересылаются (подделки оператора и кассы), двоичное от оператора отбрасывается", async () =>
        {
            foreach (var t in new[] { "join-request", "started", "session", "hello", "consent", "locked", "expired", "error", "waiting-consent" })
                await op!.SendJsonAsync(new { type = t, @operator = "Хакер" });
            await op!.SendAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 }, WebSocketMessageType.Binary);
            await op.SendRawTextAsync("not json at all");
            await op.SendRawTextAsync("{\"type\":\"key\",\"action\":\"down\",\"code\":\"KeyZ\",\"key\":\"z\"}");
            var got = await host!.NextAsync() ?? throw new Exception("хост ничего не получил");
            Assert(got.Text.Contains("KeyZ"), "до хоста дошло: " + TestWs.Short(got));
            // «not json» оператору вернул bad-request
            Assert((await op.ExpectAsync("error")).GetProperty("code").GetString() == "bad-request", "нет bad-request на мусор");

            foreach (var t in new[] { "join", "waiting-consent", "error", "session", "started" })
                await host.SendJsonAsync(new { type = t, code = "x" });
            await host.SendRawTextAsync("{\"type\":\"cursor\",\"x\":1,\"y\":2}");
            var g2 = await op.NextAsync() ?? throw new Exception("оператор ничего не получил");
            Assert(g2.Text.Contains("cursor"), "до оператора дошло: " + TestWs.Short(g2));
        });

        await Step("ping с t → pong с тем же t (оператор и касса)", async () =>
        {
            await op!.SendRawTextAsync("{\"type\":\"ping\",\"t\":12345.5}");
            var p = await op.ExpectAsync("pong");
            Assert(p.GetProperty("t").GetDouble() == 12345.5, p.ToString());
            await host!.SendRawTextAsync("{\"type\":\"ping\",\"t\":7}");
            Assert((await host.ExpectAsync("pong")).GetProperty("t").GetDouble() == 7, "host pong");
        });

        await Step("end от оператора → касса получает end(operator), оба закрыты, запись в журнале", async () =>
        {
            await op!.SendJsonAsync(new { type = "end", reason = "operator" });
            var e = await host!.ExpectAsync("end");
            Assert(e.GetProperty("reason").GetString() == "operator", e.ToString());
            Assert((await op.ExpectAsync("end")).GetProperty("reason").GetString() == "operator", "оператору end");
            Assert(await host.WaitClosedAsync() && await op.WaitClosedAsync(), "не закрыты");
            var j = await JournalFor(s, token, sessionId);
            Assert(j.GetProperty("reason").GetString() == "operator", j.ToString());
            Assert(j.GetProperty("operator").GetString() == Login && j.GetProperty("operatorName").GetString() == Name, j.ToString());
            Assert(j.GetProperty("company").GetString() == "Тест Компания" && j.GetProperty("machine").GetString() == "TEST-PC" && j.GetProperty("version").GetString() == "9.9.9", j.ToString());
            Assert(j.GetProperty("hostIp").GetString() == "127.0.0.1" && j.GetProperty("start").ValueKind == JsonValueKind.String, j.ToString());
            Assert(j.GetProperty("durationSec").GetInt64() >= 0 && j.GetProperty("bytesToOperator").GetInt64() > 10_000_000, j.ToString());
        });

        await Step("код одноразовый: после конца сеанса join → no-session", async () =>
        {
            await using var o2 = await NewOperator(s, token);
            await o2.SendJsonAsync(new { type = "join", code, pin });
            Assert((await o2.ExpectAsync("error")).GetProperty("code").GetString() == "no-session", "код жив");
        });

        await Step("end от кассы → оператору end(host)", async () =>
        {
            var (h, o, id, _) = await Started(s, token);
            await using (h) await using (o)
            {
                await h.SendJsonAsync(new { type = "end", reason = "host" });
                Assert((await o.ExpectAsync("end")).GetProperty("reason").GetString() == "host", "reason");
                Assert(await o.WaitClosedAsync(), "оператор не закрыт");
                Assert((await JournalFor(s, token, id)).GetProperty("reason").GetString() == "host", "журнал");
            }
        });

        await Step("касса оборвалась без end → оператору end(disconnect)", async () =>
        {
            var (h, o, id, _) = await Started(s, token);
            await using (o)
            {
                h.Ws.Abort();
                Assert((await o.ExpectAsync("end")).GetProperty("reason").GetString() == "disconnect", "reason");
                Assert((await JournalFor(s, token, id)).GetProperty("reason").GetString() == "disconnect", "журнал");
                await h.DisposeAsync();
            }
        });

        await Step("двоичное 5 МБ от кассы → сеанс завершён с error, касса закрыта кодом 1009", async () =>
        {
            var (h, o, id, _) = await Started(s, token);
            await using (h) await using (o)
            {
                await h.SendAsync(new byte[5 * 1024 * 1024], WebSocketMessageType.Binary);
                Assert((await o.ExpectAsync("end", 10000)).GetProperty("reason").GetString() == "error", "reason");
                Assert(await h.WaitClosedAsync(), "касса не закрыта");
                Assert(h.CloseStatus == WebSocketCloseStatus.MessageTooBig, "код закрытия " + h.CloseStatus);
                Assert((await JournalFor(s, token, id)).GetProperty("reason").GetString() == "error", "журнал");
            }
        });

        await Step("текст 300 КБ от оператора → error", async () =>
        {
            var (h, o, _, _) = await Started(s, token);
            await using (h) await using (o)
            {
                await o.SendRawTextAsync("{\"type\":\"text\",\"text\":\"" + new string('x', 300 * 1024) + "\"}");
                Assert((await h.ExpectAsync("end", 10000)).GetProperty("reason").GetString() == "error", "reason");
            }
        });

        await Step("5 неверных PIN → locked оператору и кассе, касса закрыта, дальше join → locked, журнал", async () =>
        {
            var (h, c, p) = await NewHost(s, "Блок");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                for (var i = 1; i <= 4; i++)
                {
                    await o.SendJsonAsync(new { type = "join", code = c, pin = WrongPin(p) });
                    var e = await o.ExpectAsync("error");
                    Assert(e.GetProperty("code").GetString() == "bad-pin" && e.GetProperty("left").GetInt32() == 5 - i, e.ToString());
                }
                await o.SendJsonAsync(new { type = "join", code = c, pin = WrongPin(p) });
                Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "locked", "5-й не locked");
                await h.ExpectAsync("locked");
                Assert(await h.WaitClosedAsync(), "касса не закрыта");
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "locked", "после блокировки верный PIN прошёл");
                var j = await JournalFor(s, token, c);
                Assert(j.GetProperty("reason").GetString() == "locked" && j.GetProperty("operator").GetString() == Login, j.ToString());
            }
        });

        await Step("кассир отказал → оператору declined, кассе end(declined) и закрытие; соединение оператора живо", async () =>
        {
            var (h, c, p) = await NewHost(s, "Отказ");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                await h.SendJsonAsync(new { type = "consent", accept = false });
                Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "declined", "не declined");
                Assert((await h.ExpectAsync("end")).GetProperty("reason").GetString() == "declined", "кассе не end declined");
                Assert(await h.WaitClosedAsync(), "касса не закрыта");
                await o.SendRawTextAsync("{\"type\":\"ping\",\"t\":1}");
                await o.ExpectAsync("pong");
                var j = await JournalFor(s, token, c);
                Assert(j.GetProperty("reason").GetString() == "declined" && j.GetProperty("start").ValueKind == JsonValueKind.Null, j.ToString());
            }
        });

        await Step("оператор отменил ожидание (end) → кассе end(operator); без сеанса end → not-joined", async () =>
        {
            var (h, c, p) = await NewHost(s, "Отмена");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                await o.SendJsonAsync(new { type = "end", reason = "operator" });
                Assert((await h.ExpectAsync("end")).GetProperty("reason").GetString() == "operator", "reason");
                Assert(await h.WaitClosedAsync(), "касса не закрыта");
                await o.SendJsonAsync(new { type = "end" });
                Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "not-joined", "not-joined");
            }
        });

        await Step("оператор закрыл вкладку во время ожидания → кассе end(disconnect)", async () =>
        {
            var (h, c, p) = await NewHost(s, "Вкладка");
            await using (h)
            {
                var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                o.Ws.Abort();
                Assert((await h.ExpectAsync("end")).GetProperty("reason").GetString() == "disconnect", "reason");
                await o.DisposeAsync();
            }
        });

        await Step("FakeHost (настоящие JPEG): код из консоли → join → 40 кадров, куски — JPEG в пределах экрана → end(host)", async () =>
        {
            var exe = Path.Combine(ServerProcess.FindRoot(), "Tools", "FakeHost", "bin", "Release", "net8.0-windows", "FakeHost.exe");
            Assert(File.Exists(exe), "нет FakeHost.exe — соберите Tools/FakeHost (Release)");
            var psi = new ProcessStartInfo(exe, $"--url {s.WsUrl}/ws/host --frames 40 --fps 20 --size 640x360 --accept auto --company \"Фейк\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = new StringBuilder();
            var codeTcs = new TaskCompletionSource<(string, string)>();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (output) output.AppendLine(e.Data);
                if (e.Data.StartsWith("CODE=")) codeTcs.TrySetResult((e.Data[5..14], e.Data[^4..]));
            };
            p.BeginOutputReadLine();
            try
            {
                var (fc, fp) = await codeTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = fc, pin = fp });
                await o.ExpectAsync("waiting-consent");
                var st = await o.ExpectAsync("started", 5000);
                Assert(st.GetProperty("host").GetProperty("company").GetString() == "Фейк", st.ToString());
                await o.ExpectAsync("screen", 5000, skip: true);
                int frames = 0, tiles = 0, firstFrameTiles = 0;
                while (true)
                {
                    var m = await o.NextAsync(10000) ?? throw new Exception("поток оборвался, кадров " + frames);
                    if (m.Type == WebSocketMessageType.Text)
                    {
                        if (m.JsonType == "end") { Assert(m.Json.GetProperty("reason").GetString() == "host", m.Text); break; }
                        continue; // cursor, stats
                    }
                    var d = m.Data;
                    if (d[0] == 1)
                    {
                        tiles++;
                        if (frames == 0) firstFrameTiles++;
                        int x = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5)), y = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(7));
                        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(9)), h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(11));
                        Assert(x + w <= 640 && y + h <= 360 && w > 0 && h > 0, $"кусок вне экрана {x},{y} {w}x{h}");
                        Assert(d[13] == 0xFF && d[14] == 0xD8 && d[^2] == 0xFF && d[^1] == 0xD9, "не JPEG");
                    }
                    else if (d[0] == 2)
                    {
                        frames++;
                        await o.SendJsonAsync(new { type = "ack", frame = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(1)) });
                        if (frames == 5) await o.SendJsonAsync(new { type = "mouse", action = "down", x = 320, y = 180, button = 0 });
                    }
                }
                Assert(frames == 40, "кадров " + frames);
                Assert(firstFrameTiles == 15, "первый кадр не полный: кусков " + firstFrameTiles);
                Assert(p.WaitForExit(10000) && p.ExitCode == 0, "FakeHost не завершился как надо");
                lock (output) Assert(output.ToString().Contains("мышь down кнопка 0 в (320,180)"), "FakeHost не получил клик:\n" + output);
                Console.WriteLine($"        (кусков JPEG: {tiles}, кадров: {frames})");
            }
            finally
            {
                if (!p.HasExited) p.Kill();
            }
        });

        await Step("журнал: каждая строка — JSON со всеми полями; API отдаёт новые сверху", async () =>
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(s.DataDir, "journal.jsonl"));
            Assert(lines.Length >= 8, "строк " + lines.Length);
            foreach (var l in lines)
            {
                var j = JsonDocument.Parse(l).RootElement;
                foreach (var f in new[] { "sessionId", "operator", "company", "machine", "version", "hostIp", "start", "end", "durationSec", "reason" })
                    Assert(j.TryGetProperty(f, out _), $"нет поля {f}: {l}");
            }
            var recent = (await Sessions(s, token)).GetProperty("recent").EnumerateArray().ToList();
            Assert(recent.Count == lines.Length, $"API {recent.Count} против файла {lines.Length}");
            var ends = recent.Select(e => e.GetProperty("end").GetDateTimeOffset()).ToList();
            Assert(ends.SequenceEqual(ends.OrderByDescending(x => x)), "не новые сверху");
            var reasons = string.Join(",", recent.Select(e => e.GetProperty("reason").GetString()).Distinct().OrderBy(x => x));
            Console.WriteLine($"        (строк журнала: {lines.Length}; итоги: {reasons})");
            var active = (await Sessions(s, token)).GetProperty("active").GetArrayLength();
            Assert(active == 0, "висят активные: " + active);
        });

        // ---------- 2026-10-16, v1.1: программа оператора ----------

        await Step("v1.1: очередь — кассир и проблема из hello; вход по номеру сеанса (без кода) → согласие → started", async () =>
        {
            await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
            await h.SendJsonAsync(new
            {
                type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version = "9.9.9", company = "Очередь", machine = "Q-PC",
                screen = new { w = 800, h = 600 }, user = "Айгерим", note = "Не печатает чек",
            });
            var sm = await h.ExpectAsync("session");
            var pin = sm.GetProperty("pin").GetString()!;
            var row = (await Sessions(s, token)).GetProperty("active").EnumerateArray().First(e => e.GetProperty("company").GetString() == "Очередь");
            Assert(row.GetProperty("user").GetString() == "Айгерим" && row.GetProperty("note").GetString() == "Не печатает чек", "нет кассира/проблемы: " + row);
            Assert(!row.TryGetProperty("code", out _) && !row.TryGetProperty("pin", out _), "очередь раскрывает код или PIN");
            var sid = row.GetProperty("sessionId").GetString()!;
            await using var o = await NewOperator(s, token);
            await o.SendJsonAsync(new { type = "join", session = sid, pin = WrongPin(pin) });
            var bad = await o.ExpectAsync("error");
            Assert(bad.GetProperty("code").GetString() == "bad-pin", "неверный PIN принят: " + bad);
            await o.SendJsonAsync(new { type = "join", session = "nope", pin });
            Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "no-session", "чужой номер сеанса");
            await o.SendJsonAsync(new { type = "join", session = sid, pin });
            await o.ExpectAsync("waiting-consent");
            await h.ExpectAsync("join-request");
            await h.SendJsonAsync(new { type = "consent", accept = true });
            await h.ExpectAsync("started");
            await o.ExpectAsync("started");

            // Новые сообщения сеанса идут насквозь в обе стороны (чат, сведения, файл частями).
            await o.SendJsonAsync(new { type = "chat", text = "Здравствуйте!" });
            Assert((await h.ExpectAsync("chat")).GetProperty("text").GetString() == "Здравствуйте!", "чат к кассе");
            await h.SendJsonAsync(new { type = "chat", text = "Принтер молчит" });
            Assert((await o.ExpectAsync("chat")).GetProperty("text").GetString() == "Принтер молчит", "чат к оператору");
            var chunk = Convert.ToBase64String(new byte[96 * 1024]);
            await o.SendJsonAsync(new { type = "file-chunk", id = "f1", index = 0, data = chunk });
            Assert((await h.ExpectAsync("file-chunk")).GetProperty("data").GetString()!.Length == chunk.Length, "кусок файла");
            await o.SendJsonAsync(new { type = "end", reason = "operator" });
            await h.ExpectAsync("end");
            var j = await JournalFor(s, token, sid);
            Assert(j.GetProperty("user").GetString() == "Айгерим" && j.GetProperty("note").GetString() == "Не печатает чек", "журнал без кассира/проблемы: " + j);
        });

        await Step("v1.1: заметки — без токена 401, пустой текст 400, запись и выборка по компании/компьютеру (новые сверху)", async () =>
        {
            Assert((await s.Http.GetAsync("/api/notes")).StatusCode == HttpStatusCode.Unauthorized, "без токена");
            async Task<HttpResponseMessage> Post(object body)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/api/notes") { Content = JsonContent.Create(body) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await s.Http.SendAsync(req);
            }
            Assert((await Post(new { company = "Очередь", machine = "Q-PC", text = "  " })).StatusCode == HttpStatusCode.BadRequest, "пустой текст принят");
            Assert((await Post(new { company = "Очередь", machine = "Q-PC", text = "Переустановили драйвер принтера" })).IsSuccessStatusCode, "заметка 1");
            Assert((await Post(new { company = "Очередь", machine = "Q-PC2", text = "Другая касса" })).IsSuccessStatusCode, "заметка 2");
            Assert((await Post(new { company = "Чужая", machine = "Q-PC", text = "Чужая компания" })).IsSuccessStatusCode, "заметка 3");
            using var get = new HttpRequestMessage(HttpMethod.Get, "/api/notes?company=" + Uri.EscapeDataString("очередь") + "&machine=Q-PC");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var list = (await (await s.Http.SendAsync(get)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes").EnumerateArray().ToList();
            Assert(list.Count == 1 && list[0].GetProperty("text").GetString() == "Переустановили драйвер принтера", "выборка: " + string.Join(" | ", list));
            Assert(list[0].GetProperty("operatorName").GetString() == Name, "оператор не записан");
            using var all = new HttpRequestMessage(HttpMethod.Get, "/api/notes?company=" + Uri.EscapeDataString("Очередь"));
            all.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var both = (await (await s.Http.SendAsync(all)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes").EnumerateArray().ToList();
            Assert(both.Count == 2 && both[0].GetProperty("text").GetString() == "Другая касса", "по компании: " + string.Join(" | ", both));
        });

        if (host is not null) await host.DisposeAsync();
        if (op is not null) await op.DisposeAsync();
    }

    // ---------- сервер B ----------

    private async Task SuiteB(ServerProcess s)
    {
        await s.CliAsync(dll, $"add-operator {Login} \"{Name}\"", Password);
        var token = await s.LoginAsync(Login, Password); // вход 1 из 3 в минуту

        await Step("код истекает (TTL 3 с): кассе expired и закрытие, оператору expired", async () =>
        {
            var (h, c, p) = await NewHost(s, "Истечение");
            await using (h)
            {
                await h.ExpectAsync("expired", 6000);
                Assert(await h.WaitClosedAsync(), "касса не закрыта");
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                Assert((await o.ExpectAsync("error")).GetProperty("code").GetString() == "expired", "не expired");
            }
        });

        await Step("кассир не ответил (сервер ждёт 2 с) → оператору declined, кассе end(consent-timeout)", async () =>
        {
            var (h, c, p) = await NewHost(s, "Молчун");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                var swc = Stopwatch.StartNew();
                Assert((await o.ExpectAsync("error", 6000)).GetProperty("code").GetString() == "declined", "не declined");
                Assert((await h.ExpectAsync("end", 3000)).GetProperty("reason").GetString() == "consent-timeout", "reason");
                Console.WriteLine($"        (отказ по тайм-ауту через {swc.ElapsedMilliseconds} мс после запроса)");
                var j = await JournalFor(s, token, c);
                Assert(j.GetProperty("reason").GetString() == "consent-timeout", j.ToString());
            }
        });

        await Step("оператор замолчал (нет pong 3 с) → соединение рвётся, кассе end(timeout)", async () =>
        {
            var (h, c, p) = await NewHost(s, "Тишина");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                await h.SendJsonAsync(new { type = "consent", accept = true });
                await o.ExpectAsync("started");
                await h.ExpectAsync("started");
                await Task.Delay(1500); // пусть оператор ответит хотя бы на один ping
                o.AutoPong = false;
                var sw = Stopwatch.StartNew();
                Assert((await h.ExpectAsync("end", 8000)).GetProperty("reason").GetString() == "timeout", "reason");
                Assert(await o.WaitClosedAsync(3000), "оператор не отключён");
                Console.WriteLine($"        (обрыв через {sw.ElapsedMilliseconds} мс после тишины)");
            }
        });

        await Step("давление назад: оператор не читает, касса шлёт без ack → сервер не копит (очередь 4 МБ), через 3 с застоя end(error)", async () =>
        {
            var (h, c, p) = await NewHost(s, "Поток");
            await using (h)
            {
                await using var o = await NewOperator(s, token);
                await o.SendJsonAsync(new { type = "join", code = c, pin = p });
                await o.ExpectAsync("waiting-consent");
                await h.ExpectAsync("join-request");
                await h.SendJsonAsync(new { type = "consent", accept = true });
                await o.ExpectAsync("started");
                await h.ExpectAsync("started");
                o.Paused = true; // перестаём читать
                using var keepAlive = new Timer(_ => _ = o.SendJsonAsync(new { type = "pong" }), null, 0, 400);
                s.Process.Refresh();
                var memBefore = s.Process.PrivateMemorySize64;
                long peak = memBefore;
                var sent = 0L;
                var chunk = new byte[1024 * 1024];
                chunk[0] = 1;
                var flood = Task.Run(async () =>
                {
                    try
                    {
                        while (h.Ws.State == WebSocketState.Open)
                        {
                            await h.SendAsync(chunk, WebSocketMessageType.Binary);
                            Interlocked.Add(ref sent, chunk.Length);
                        }
                    }
                    catch { }
                });
                var endTask = h.ExpectAsync("end", 30000);
                while (!endTask.IsCompleted)
                {
                    s.Process.Refresh();
                    peak = Math.Max(peak, s.Process.PrivateMemorySize64);
                    await Task.Delay(100);
                }
                var e = await endTask;
                Assert(e.GetProperty("reason").GetString() == "error", e.ToString());
                await flood.WaitAsync(TimeSpan.FromSeconds(10));
                var growMb = (peak - memBefore) / 1024.0 / 1024.0;
                Console.WriteLine($"        (касса успела отправить {Interlocked.Read(ref sent) / 1024 / 1024} МБ, рост памяти сервера {growMb:F0} МБ)");
                Assert(growMb < 150, $"сервер копит: +{growMb:F0} МБ");
            }
        });

        await Step("частота join: > предела в минуту → rate-limited", async () =>
        {
            await using var o = await NewOperator(s, token);
            var limited = false;
            for (var i = 0; i < 10 && !limited; i++)
            {
                await o.SendJsonAsync(new { type = "join", code = "999999999", pin = "0000" });
                limited = (await o.ExpectAsync("error")).GetProperty("code").GetString() == "rate-limited";
            }
            Assert(limited, "ограничения нет");
        });

        await Step("частота входа: > 3 в минуту с IP → 429", async () =>
        {
            var got429 = false;
            for (var i = 0; i < 6 && !got429; i++)
            {
                var r = await s.Http.PostAsJsonAsync("/api/login", new { login = Login, password = "bad" });
                got429 = r.StatusCode == HttpStatusCode.TooManyRequests;
            }
            Assert(got429, "ограничения нет");
        });

        await Step("частота подключений кассы: > предела в минуту с IP → error rate-limited", async () =>
        {
            var limited = false;
            for (var i = 0; i < 10 && !limited; i++)
            {
                await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
                await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, company = "x", machine = "y", screen = new { w = 1, h = 1 } });
                var m = await h.NextAsync() ?? throw new Exception("нет ответа");
                limited = m.JsonType == "error" && m.Json.GetProperty("code").GetString() == "rate-limited";
            }
            Assert(limited, "ограничения нет");
        });
    }
}
