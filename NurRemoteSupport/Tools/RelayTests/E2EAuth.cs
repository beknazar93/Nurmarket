using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NurRemoteSupport.RelayTests;

/// <summary>
/// 2026-10-27 (пакет С2): вход операторов, выход, смена пароля, одноразовый билет WS, блокировка по «логин + IP»,
/// доверенные прокси, ротация журнала и заметок, лимит заметок, заголовки. Запуск: dotnet run -c Release -- auth
/// </summary>
public sealed class E2EAuth(string dll)
{
    private const string Pw = "Test-Passw0rd!";
    private int _pass, _fail;
    private readonly List<string> _failures = new();

    public async Task<int> RunAsync()
    {
        Console.WriteLine("=== Сервер E (вход, пароль, билеты, прокси; доверие 127.0.0.0/8) ===");
        await using (var a = await ServerProcess.StartAsync(dll, new()
        {
            ["LoginPerMinutePerIp"] = "1000",
            ["LoginFailuresPer15MinPerLoginIp"] = "5",
            ["LoginGlobalFailureDelayAfter"] = "8",
            ["NotesPerMinute"] = "5",
            ["OperatorRecheckSeconds"] = "2",
            ["HostConnectsPerMinutePerIp"] = "100000",
            ["JoinPerMinute"] = "1000",
            ["TrustedProxies"] = "127.0.0.0/8",
        }))
        {
            var before = _fail;
            await SuiteAuth(a);
            if (_fail > before) Dump(a);
        }

        Console.WriteLine("=== Сервер F (доверенный прокси — другая сеть: подделка X-Forwarded-For не работает) ===");
        await using (var b = await ServerProcess.StartAsync(dll, new()
        {
            ["LoginPerMinutePerIp"] = "1000",
            ["HostConnectsPerMinutePerIp"] = "100000",
            ["TrustedProxies"] = "10.0.0.0/8",
        }))
        {
            var before = _fail;
            await SuiteUntrusted(b);
            if (_fail > before) Dump(b);
        }

        Console.WriteLine();
        Console.WriteLine($"ИТОГ (auth): пройдено {_pass}, провалено {_fail}");
        foreach (var f in _failures) Console.WriteLine("  - " + f);
        return _fail == 0 ? 0 : 1;
    }

    private static void Dump(ServerProcess s)
    {
        Console.WriteLine("--- хвост лога сервера ---");
        string log;
        lock (s.Log) log = s.Log.ToString();
        foreach (var l in log.Split('\n').TakeLast(80)) Console.WriteLine("  | " + l.TrimEnd());
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

    private static Task<HttpResponseMessage> Send(ServerProcess s, HttpMethod m, string url, string? token = null, object? body = null, string? xff = null, string? proto = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (xff is not null) req.Headers.TryAddWithoutValidation("X-Forwarded-For", xff);
        if (proto is not null) req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", proto);
        if (body is not null) req.Content = JsonContent.Create(body);
        return s.Http.SendAsync(req);
    }

    private static async Task<string> Login(ServerProcess s, string login, string password, string? xff = null)
    {
        var r = await Send(s, HttpMethod.Post, "/api/login", body: new { login, password }, xff: xff);
        if (!r.IsSuccessStatusCode) throw new Exception($"вход {login}: {(int)r.StatusCode}");
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task AddOp(ServerProcess s, string login, string password = Pw)
    {
        var (c, o) = await s.CliAsync(dll, $"add-operator {login} \"Оп {login}\"", password);
        Assert(c == 0, "add-operator " + login + ": " + o);
    }

    private async Task SuiteAuth(ServerProcess s)
    {
        await Step("С2: новые хэши PBKDF2 — 600 тыс. итераций; логин с управляющими символами не портит строку лога", async () =>
        {
            await AddOp(s, "hasher");
            var json = await File.ReadAllTextAsync(Path.Combine(s.DataDir, "operators.json"));
            Assert(json.Contains("pbkdf2-sha256$600000$"), "итерации не 600000: " + json[..Math.Min(200, json.Length)]);
            var r = await Send(s, HttpMethod.Post, "/api/login", body: new { login = "bad\u0007log\nline", password = "whatever12" });
            Assert(r.StatusCode == HttpStatusCode.Unauthorized, ((int)r.StatusCode).ToString());
            await Task.Delay(300);
            string log;
            lock (s.Log) log = s.Log.ToString();
            Assert(log.Contains("Неудачный вход badlogline"), "в логе нет очищенного логина");
            Assert(!log.Contains('\u0007'), "в логе управляющий символ");
        });

        await Step("С2: ответы /api/* с Cache-Control: no-store; без HTTPS заголовка HSTS нет, с X-Forwarded-Proto=https от доверенного прокси — есть", async () =>
        {
            await AddOp(s, "hdr");
            var r = await Send(s, HttpMethod.Post, "/api/login", body: new { login = "hdr", password = Pw });
            Assert(r.Headers.CacheControl?.NoStore == true, "нет no-store на /api/login: " + r.Headers.CacheControl);
            var tk = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            var me = await Send(s, HttpMethod.Get, "/api/me", tk);
            Assert(me.Headers.CacheControl?.NoStore == true, "нет no-store на /api/me");
            Assert(!me.Headers.Contains("Strict-Transport-Security"), "HSTS без HTTPS");
            var me2 = await Send(s, HttpMethod.Get, "/api/me", tk, proto: "https");
            Assert(me2.Headers.Contains("Strict-Transport-Security"), "нет HSTS при X-Forwarded-Proto=https");
        });

        await Step("С2/С-5: смена пароля — неверный старый 403, короткий новый 400, тот же 400; верная смена даёт новый токен, старый токен и старый пароль не работают", async () =>
        {
            await AddOp(s, "chg");
            var old = await Login(s, "chg", Pw);
            var bad = await Send(s, HttpMethod.Post, "/api/password", old, new { old = "nope-nope-nope", @new = "Another-Passw0rd!" });
            Assert(bad.StatusCode == HttpStatusCode.Forbidden, "неверный старый: " + (int)bad.StatusCode);
            var weak = await Send(s, HttpMethod.Post, "/api/password", old, new { old = Pw, @new = "short" });
            Assert(weak.StatusCode == HttpStatusCode.BadRequest, "короткий: " + (int)weak.StatusCode);
            var same = await Send(s, HttpMethod.Post, "/api/password", old, new { old = Pw, @new = Pw });
            Assert(same.StatusCode == HttpStatusCode.BadRequest, "тот же: " + (int)same.StatusCode);
            var noAuth = await Send(s, HttpMethod.Post, "/api/password", null, new { old = Pw, @new = "Another-Passw0rd!" });
            Assert(noAuth.StatusCode == HttpStatusCode.Unauthorized, "без токена: " + (int)noAuth.StatusCode);
            var ok = await Send(s, HttpMethod.Post, "/api/password", old, new { old = Pw, @new = "Another-Passw0rd!" });
            Assert(ok.StatusCode == HttpStatusCode.OK, "смена: " + (int)ok.StatusCode);
            var fresh = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert((await Send(s, HttpMethod.Get, "/api/me", old)).StatusCode == HttpStatusCode.Unauthorized, "старый токен жив");
            Assert((await Send(s, HttpMethod.Get, "/api/me", fresh)).StatusCode == HttpStatusCode.OK, "новый токен не принят");
            Assert((await Send(s, HttpMethod.Post, "/api/login", body: new { login = "chg", password = Pw })).StatusCode == HttpStatusCode.Unauthorized, "старый пароль принят");
            await Login(s, "chg", "Another-Passw0rd!");
        });

        await Step("С2/С-5: неверные «старые» пароли считаются как ошибки входа — после 5 вход и смена заблокированы (429)", async () =>
        {
            await AddOp(s, "guess");
            var tk = await Login(s, "guess", Pw);
            for (var i = 0; i < 5; i++)
                Assert((await Send(s, HttpMethod.Post, "/api/password", tk, new { old = "wrong-" + i, @new = "Another-Passw0rd!" })).StatusCode == HttpStatusCode.Forbidden, "попытка " + i);
            var r = await Send(s, HttpMethod.Post, "/api/password", tk, new { old = Pw, @new = "Another-Passw0rd!" });
            Assert(r.StatusCode == HttpStatusCode.TooManyRequests, "после 5 ошибок: " + (int)r.StatusCode);
            var l = await Send(s, HttpMethod.Post, "/api/login", body: new { login = "guess", password = Pw });
            Assert(l.StatusCode == HttpStatusCode.TooManyRequests, "вход после 5 ошибок: " + (int)l.StatusCode);
        });

        await Step("С2/С-5: после смены пароля открытое соединение WS старого токена закрывается (≤ проверка раз в 2 с)", async () =>
        {
            await AddOp(s, "wsch");
            var tk = await Login(s, "wsch", Pw);
            await using var ws = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(tk));
            var ok = await Send(s, HttpMethod.Post, "/api/password", tk, new { old = Pw, @new = "Another-Passw0rd!" });
            Assert(ok.StatusCode == HttpStatusCode.OK, ((int)ok.StatusCode).ToString());
            Assert(await ws.WaitClosedAsync(8000), "WS со старым токеном не закрыт");
        });

        await Step("С2/С-4: выход — токен отзывается (/api/me 401), повторный выход 401, открытый WS закрывается сразу", async () =>
        {
            await AddOp(s, "bye");
            var tk = await Login(s, "bye", Pw);
            await using var ws = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(tk));
            Assert((await Send(s, HttpMethod.Get, "/api/me", tk)).StatusCode == HttpStatusCode.OK, "до выхода");
            Assert((await Send(s, HttpMethod.Post, "/api/logout", tk)).StatusCode == HttpStatusCode.OK, "logout");
            Assert((await Send(s, HttpMethod.Get, "/api/me", tk)).StatusCode == HttpStatusCode.Unauthorized, "токен жив после выхода");
            Assert((await Send(s, HttpMethod.Post, "/api/logout", tk)).StatusCode == HttpStatusCode.Unauthorized, "повторный выход");
            Assert(await ws.WaitClosedAsync(3000), "WS не закрыт после выхода");
            Assert((await Send(s, HttpMethod.Post, "/api/logout")).StatusCode == HttpStatusCode.Unauthorized, "выход без токена");
            var again = await Login(s, "bye", Pw); // новый вход после выхода работает
            Assert((await Send(s, HttpMethod.Get, "/api/me", again)).StatusCode == HttpStatusCode.OK, "новый токен");
        });

        await Step("С2/С-4: билет WS — одноразовый: первый раз подключает (токена в адресе нет), второй — 401; без токена билет не выдаётся", async () =>
        {
            await AddOp(s, "tkt");
            var tk = await Login(s, "tkt", Pw);
            Assert((await Send(s, HttpMethod.Post, "/api/ws-ticket")).StatusCode == HttpStatusCode.Unauthorized, "билет без токена");
            var r = await Send(s, HttpMethod.Post, "/api/ws-ticket", tk);
            Assert(r.StatusCode == HttpStatusCode.OK, ((int)r.StatusCode).ToString());
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            var ticket = j.GetProperty("ticket").GetString()!;
            Assert(j.GetProperty("expiresIn").GetInt32() == 30, "срок билета " + j);
            await using (var ws = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?ticket=" + ticket))
            {
                await ws.SendJsonAsync(new { type = "join", code = "123456789", pin = "0000" });
                var e = await ws.ExpectAsync("error", 3000);
                Assert(e.GetProperty("code").GetString() == "no-session", e.ToString());
            }
            var second = false;
            try { await using var _ = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?ticket=" + ticket); }
            catch (System.Net.WebSockets.WebSocketException) { second = true; }
            Assert(second, "повторный билет принят");
            var garbage = false;
            try { await using var _ = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?ticket=deadbeef"); }
            catch (System.Net.WebSockets.WebSocketException) { garbage = true; }
            Assert(garbage, "чужой билет принят");
        });

        await Step("С2/С-3: ошибки входа с адреса A не мешают входу с адреса B; с A верный пароль после 5 ошибок — 429", async () =>
        {
            await AddOp(s, "vic");
            for (var i = 0; i < 5; i++)
                Assert((await Send(s, HttpMethod.Post, "/api/login", body: new { login = "vic", password = "bad-" + i }, xff: "203.0.113.1")).StatusCode == HttpStatusCode.Unauthorized, "ошибка " + i);
            Assert((await Send(s, HttpMethod.Post, "/api/login", body: new { login = "vic", password = Pw }, xff: "203.0.113.1")).StatusCode == HttpStatusCode.TooManyRequests, "A не заблокирован");
            Assert((await Send(s, HttpMethod.Post, "/api/login", body: new { login = "vic", password = Pw }, xff: "203.0.113.2")).StatusCode == HttpStatusCode.OK, "B заблокирован чужими ошибками");
        });

        await Step("С2/С-3: подбор с многих адресов (больше 8 ошибок) не блокирует логин, но замедляет ответ", async () =>
        {
            await AddOp(s, "brute");
            foreach (var ip in new[] { "198.51.100.1", "198.51.100.2", "198.51.100.3" })
                for (var i = 0; i < 4; i++)
                    await Send(s, HttpMethod.Post, "/api/login", body: new { login = "brute", password = "bad-" + i }, xff: ip);
            var sw = Stopwatch.StartNew();
            var r = await Send(s, HttpMethod.Post, "/api/login", body: new { login = "brute", password = Pw }, xff: "198.51.100.77");
            Assert(r.StatusCode == HttpStatusCode.OK, "верный пароль заблокирован: " + (int)r.StatusCode);
            Assert(sw.ElapsedMilliseconds >= 400, $"ответ не замедлен ({sw.ElapsedMilliseconds} мс)");
        });

        await Step("С2/В-11: за доверенным прокси (127.0.0.0/8) hostIp в /api/sessions — адрес из X-Forwarded-For", async () =>
        {
            await AddOp(s, "ipchk");
            var tk = await Login(s, "ipchk", Pw);
            await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host", configure: o => o.SetRequestHeader("X-Forwarded-For", "203.0.113.9"));
            await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, company = "IP-проверка", machine = "PC", screen = new { w = 1, h = 1 } });
            await h.ExpectAsync("session");
            var j = (await (await Send(s, HttpMethod.Get, "/api/sessions", tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active");
            var mine = j.EnumerateArray().First(a => a.GetProperty("company").GetString() == "IP-проверка");
            Assert(mine.GetProperty("hostIp").GetString() == "203.0.113.9", mine.ToString());
        });

        await Step("С2/С-7: заметки — не больше 5 в минуту от оператора (6-я 429); чтение с конца: новые сверху, фильтр по компании", async () =>
        {
            await AddOp(s, "noter");
            var tk = await Login(s, "noter", Pw);
            for (var i = 1; i <= 5; i++)
            {
                var r = await Send(s, HttpMethod.Post, "/api/notes", tk, new { company = i % 2 == 0 ? "ЧётКо" : "НечётКо", machine = "PC", text = "заметка " + i });
                Assert(r.StatusCode == HttpStatusCode.OK, $"заметка {i}: {(int)r.StatusCode}");
            }
            Assert((await Send(s, HttpMethod.Post, "/api/notes", tk, new { company = "x", machine = "y", text = "лишняя" })).StatusCode == HttpStatusCode.TooManyRequests, "лимит заметок не сработал");
            var list = (await (await Send(s, HttpMethod.Get, "/api/notes?company=" + Uri.EscapeDataString("НечётКо"), tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("text").GetString()).ToList();
            Assert(list.SequenceEqual(new[] { "заметка 5", "заметка 3", "заметка 1" }), string.Join("|", list));
        });

        await Step("С2/С-7: ротация по месяцам — заметки и журнал прошлого месяца уходят в архив, но остаются видны в выдаче", async () =>
        {
            await AddOp(s, "rot");
            var tk = await Login(s, "rot", Pw);
            var old = DateTime.UtcNow.AddMonths(-2);
            var tag = $"{old.Year:D4}-{old.Month:D2}";
            // заметки: 3000 старых строк по чужой компании + 1 по нашей, дата файла — два месяца назад
            var notes = Path.Combine(s.DataDir, "notes.jsonl");
            var sb = new StringBuilder();
            for (var i = 0; i < 3000; i++)
                sb.AppendLine(JsonSerializer.Serialize(new { id = "o" + i, created = old, @operator = "x", operatorName = "X", company = i == 0 ? "АрхивКо" : "Чужая", machine = "PC", text = "старая " + i }));
            await File.WriteAllTextAsync(notes, sb.ToString());
            File.SetLastWriteTimeUtc(notes, old);
            // журнал: одна старая запись
            var journal = Path.Combine(s.DataDir, "journal.jsonl");
            await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new { sessionId = "oldsession", code = "111111111", @operator = "x", company = "Старая", machine = "PC", version = "1", hostIp = "1.1.1.1", start = old, end = old, durationSec = 1, reason = "host", created = old }) + "\n");
            File.SetLastWriteTimeUtc(journal, old);

            var add = await Send(s, HttpMethod.Post, "/api/notes", tk, new { company = "НоваяКо", machine = "PC", text = "новая" });
            Assert(add.StatusCode == HttpStatusCode.OK, ((int)add.StatusCode).ToString());
            Assert(File.Exists(Path.Combine(s.DataDir, $"notes-{tag}.jsonl")), "нет архива заметок notes-" + tag);
            Assert((await File.ReadAllLinesAsync(notes)).Length == 1, "в текущем файле заметок должна остаться одна строка");
            var all = (await (await Send(s, HttpMethod.Get, "/api/notes?limit=500", tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes").EnumerateArray().ToList();
            Assert(all.Count == 500 && all[0].GetProperty("text").GetString() == "новая", "выдача без архива/порядок: " + all.Count);
            var rare = (await (await Send(s, HttpMethod.Get, "/api/notes?company=" + Uri.EscapeDataString("АрхивКо"), tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notes");
            Assert(rare.GetArrayLength() == 1, "редкая запись из архива не найдена");

            // новый сеанс → запись журнала → ротация
            await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host");
            await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, company = "Новая", machine = "PC", screen = new { w = 1, h = 1 } });
            var sess = await h.ExpectAsync("session");
            await using var op = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(tk));
            await op.SendJsonAsync(new { type = "join", code = sess.GetProperty("code").GetString(), pin = sess.GetProperty("pin").GetString() });
            await op.ExpectAsync("waiting-consent");
            await h.ExpectAsync("join-request");
            await h.SendJsonAsync(new { type = "consent", accept = false });
            await op.ExpectAsync("error", 3000, skip: true);
            await Task.Delay(500);
            Assert(File.Exists(Path.Combine(s.DataDir, $"journal-{tag}.jsonl")), "нет архива журнала journal-" + tag);
            var recent = (await (await Send(s, HttpMethod.Get, "/api/sessions?limit=50", tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recent").EnumerateArray().Select(e => e.GetProperty("sessionId").GetString()).ToList();
            Assert(recent.Count == 2 && recent[1] == "oldsession", "журнал: " + string.Join(",", recent));
        });

        await Step("С2: DataDir без пути считается от папки программы, а не от текущей папки (add-operator из другой папки пишет туда же)", async () =>
        {
            var rel = "reldata-" + Guid.NewGuid().ToString("N")[..6];
            var dir = Path.Combine(Path.GetDirectoryName(dll)!, rel);
            try
            {
                var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" add-operator relop \"Отн\"")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                };
                psi.Environment["Relay__DataDir"] = rel;
                using var p = Process.Start(psi)!;
                await p.StandardInput.WriteLineAsync(Pw);
                p.StandardInput.Close();
                await p.WaitForExitAsync();
                Assert(File.Exists(Path.Combine(dir, "operators.json")), "operators.json не в папке программы: " + dir);
                Assert(!File.Exists(Path.Combine(Path.GetTempPath(), rel, "operators.json")), "создан в текущей папке");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }

    private async Task SuiteUntrusted(ServerProcess s)
    {
        await Step("С2/В-11: прямое подключение с поддельным X-Forwarded-For (адрес не из TrustedProxies) — hostIp остаётся настоящим", async () =>
        {
            await s.CliAsync(dll, "add-operator ipchk \"IP\"", Pw);
            var tk = await Login(s, "ipchk", Pw);
            await using var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host", configure: o => o.SetRequestHeader("X-Forwarded-For", "203.0.113.9"));
            await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, company = "Подделка", machine = "PC", screen = new { w = 1, h = 1 } });
            await h.ExpectAsync("session");
            var j = (await (await Send(s, HttpMethod.Get, "/api/sessions", tk)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active");
            var mine = j.EnumerateArray().First(a => a.GetProperty("company").GetString() == "Подделка");
            Assert(mine.GetProperty("hostIp").GetString() == "127.0.0.1", mine.ToString());
            var me = await Send(s, HttpMethod.Get, "/api/me", tk, proto: "https");
            Assert(!me.Headers.Contains("Strict-Transport-Security"), "поддельный X-Forwarded-Proto принят");
        });
    }
}
