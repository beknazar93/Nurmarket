using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace NurRemoteSupport.RelayTests;

/// <summary>
/// 2026-10-27 (пакет С1): проверки безопасности ретранслятора — «Только просмотр» на сервере, отзыв входа оператора,
/// память без входа (ping, двоичное, мусор), повтор ключа type, IPv6 /64, Origin, пределы простоя и длительности сеанса.
/// Каждая проверка соответствует дефекту из аудита 27.10.2026 (В-1, В-2, В-5, В-6, С-1, С-2, С-6, В-7).
/// </summary>
public sealed partial class E2E
{
    private const string FiredPassword = "Fired-Passw0rd!!";

    private static long ServerMemory(ServerProcess s)
    {
        using var p = Process.GetProcessById(s.Process.Id);
        return p.WorkingSet64;
    }

    private static async Task<(TestWs host, TestWs op, JsonElement started)> StartedWith(ServerProcess s, string token, bool viewOnly, string company = "Тест Компания")
    {
        var (h, code, pin) = await NewHost(s, company);
        var op = await NewOperator(s, token);
        await op.SendJsonAsync(new { type = "join", code, pin });
        await op.ExpectAsync("waiting-consent");
        await h.ExpectAsync("join-request");
        await h.SendJsonAsync(new { type = "consent", accept = true, view_only = viewOnly });
        await h.ExpectAsync("started");
        var os = await op.ExpectAsync("started");
        return (h, op, os);
    }

    // ---------- сервер C: защита от обхода и перегрузки ----------

    private async Task SuiteC(ServerProcess s)
    {
        await s.CliAsync(dll, $"add-operator {Login} \"{Name}\"", Password);
        var token = await s.LoginAsync(Login, Password);

        await Step("С1/В-1/В-2: «Только просмотр» — started.viewOnly=true, ни мышь, ни клавиши, ни текст, ни буфер, ни файлы до кассы не доходят; чат, ack и качество идут", async () =>
        {
            var (h, op, st) = await StartedWith(s, token, viewOnly: true);
            await using var _h = h; await using var _o = op;
            Assert(st.TryGetProperty("viewOnly", out var vo) && vo.GetBoolean(), "нет viewOnly=true в started: " + st);
            await op.SendJsonAsync(new { type = "mouse", action = "down", x = 5, y = 5, button = 0 });
            await op.SendJsonAsync(new { type = "key", action = "down", code = "KeyA", key = "a" });
            await op.SendJsonAsync(new { type = "text", text = "secret" });
            await op.SendJsonAsync(new { type = "clipboard-set", text = "x" });
            await op.SendJsonAsync(new { type = "clipboard-get" });
            await op.SendJsonAsync(new { type = "file-begin", id = "f1", name = "a.txt", size = 3 });
            await op.SendJsonAsync(new { type = "file-chunk", id = "f1", index = 0, data = "AAAA" });
            await op.SendJsonAsync(new { type = "file-end", id = "f1" });
            await op.SendJsonAsync(new { type = "chat", text = "привет" });
            await op.SendJsonAsync(new { type = "quality", value = 50 });
            await op.SendJsonAsync(new { type = "ack", frame = 1 });
            // Первое, что получит касса, — чат (ввод отброшен), затем качество и ack.
            var chat = await h.ExpectAsync("chat", 3000);
            Assert(chat.GetProperty("text").GetString() == "привет", chat.ToString());
            await h.ExpectAsync("quality", 3000);
            await h.ExpectAsync("ack", 3000);
            Assert(await h.NextAsync(400) is null, "после этого касса получила что-то лишнее");
            var notice = await op.ExpectAsync("view-only", 3000, skip: true);
            Assert(notice.GetProperty("value").GetBoolean() && notice.GetProperty("cashier").GetBoolean(), "оператору не сказали про «только просмотр»: " + notice);
        });

        await Step("С1/В-1: обычное согласие — started.viewOnly=false и ввод идёт", async () =>
        {
            var (h, op, st) = await StartedWith(s, token, viewOnly: false);
            await using var _h = h; await using var _o = op;
            Assert(st.TryGetProperty("viewOnly", out var vo) && !vo.GetBoolean(), "viewOnly должен быть false: " + st);
            await op.SendJsonAsync(new { type = "text", text = "hello" });
            var t = await h.ExpectAsync("text", 3000);
            Assert(t.GetProperty("text").GetString() == "hello", t.ToString());
        });

        await Step("С1/В-3: возможности кассы (caps в hello) доходят оператору в started.host.caps и /api/sessions; без поля — пустой список", async () =>
        {
            var (h, code, pin) = await NewHost(s, "Caps", new
            {
                type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version = "9.9.9", company = "Caps", machine = "PC",
                screen = new { w = 10, h = 10 }, caps = new[] { "chat", "client-active", "chat", "bad cap!", new string('x', 50) },
            });
            await using var _h = h;
            var j = await Sessions(s, token);
            var mine = j.GetProperty("active").EnumerateArray().First(a => a.GetProperty("company").GetString() == "Caps");
            Assert(string.Join(",", mine.GetProperty("caps").EnumerateArray().Select(c => c.GetString())) == "chat,client-active,badcap," + new string('x', 32), mine.ToString());
            await using var op = await NewOperator(s, token);
            await op.SendJsonAsync(new { type = "join", code, pin });
            await op.ExpectAsync("waiting-consent");
            await h.ExpectAsync("join-request");
            await h.SendJsonAsync(new { type = "consent", accept = true });
            await h.ExpectAsync("started");
            var st = await op.ExpectAsync("started");
            Assert(st.GetProperty("host").GetProperty("caps").GetArrayLength() == 4, st.ToString());
            var (h2, op2, st2) = await StartedWith(s, token, viewOnly: false);
            await using var _h2 = h2; await using var _o2 = op2;
            Assert(st2.GetProperty("host").GetProperty("caps").GetArrayLength() == 0, "без caps должен быть пустой список: " + st2);
        });

        await Step("С1/С-1: сообщение с двумя ключами type отбрасывается (оператор → касса и касса → оператор)", async () =>
        {
            var (h, op, _) = await StartedWith(s, token, viewOnly: false);
            await using var _h = h; await using var _o = op;
            await op.SendRawTextAsync("{\"type\":\"mouse\",\"action\":\"move\",\"x\":1,\"y\":1,\"type\":\"end\",\"reason\":\"host\"}");
            var err = await op.ExpectAsync("error", 3000);
            Assert(err.GetProperty("code").GetString() == "bad-request", err.ToString());
            Assert(await h.NextAsync(500) is null, "касса получила сообщение с двойным type");
            await h.SendRawTextAsync("{\"type\":\"cursor\",\"x\":1,\"y\":1,\"type\":\"started\",\"operator\":\"x\"}");
            Assert(await op.NextAsync(500) is null, "оператор получил сообщение с двойным type");
            await h.SendJsonAsync(new { type = "cursor", x = 2, y = 3 });
            await op.ExpectAsync("cursor", 3000);
            await op.SendJsonAsync(new { type = "mouse", action = "move", x = 4, y = 4 });
            await h.ExpectAsync("mouse", 3000);
        });

        await Step("С1: причины error/timeout стороны не принимаются — end от кассы с reason=timeout записывается как host", async () =>
        {
            var (h, op, st) = await StartedWith(s, token, viewOnly: false);
            await using var _h = h; await using var _o = op;
            await h.SendJsonAsync(new { type = "end", reason = "timeout" });
            var e = await op.ExpectAsync("end", 3000, skip: true);
            Assert(e.GetProperty("reason").GetString() == "host", e.ToString());
            var j = await JournalFor(s, token, st.GetProperty("sessionId").GetString()!);
            Assert(j.GetProperty("reason").GetString() == "host", j.ToString());
        });

        await Step("С1/В-5: оператор удалён — join по уже открытому WS получает unauthorized, соединение закрыто, касса запроса не видит", async () =>
        {
            var (c0, o0) = await s.CliAsync(dll, "add-operator fired \"Уволен\"", FiredPassword);
            Assert(c0 == 0, o0);
            var tk = await s.LoginAsync("fired", FiredPassword);
            var (h, code, pin) = await NewHost(s);
            await using var _h = h;
            await using var op = await NewOperator(s, tk);
            await s.CliAsync(dll, "remove-operator fired", null);
            await op.SendJsonAsync(new { type = "join", code, pin });
            var e = await op.ExpectAsync("error", 5000);
            Assert(e.GetProperty("code").GetString() == "unauthorized", e.ToString());
            Assert(await op.WaitClosedAsync(7000), "WS удалённого оператора не закрыт");
            var next = await h.NextAsync(500);
            Assert(next is null || next.JsonType != "join-request", "касса получила join-request от удалённого оператора");
        });

        await Step("С1/В-5: пароль оператора сброшен, сеанс идёт — через ≤ 2 проверки WS закрыт, кассе end(revoked), в журнале revoked", async () =>
        {
            await s.CliAsync(dll, "add-operator tmpop \"Временный\"", FiredPassword);
            var tk = await s.LoginAsync("tmpop", FiredPassword);
            var (h, op, st) = await StartedWith(s, tk, viewOnly: false);
            await using var _h = h; await using var _o = op;
            await s.CliAsync(dll, "add-operator tmpop \"Временный\"", FiredPassword + "2");
            var e = await h.ExpectAsync("end", 8000, skip: true);
            Assert(e.GetProperty("reason").GetString() == "revoked", e.ToString());
            Assert(await op.WaitClosedAsync(8000), "WS оператора не закрыт");
            var admin = await s.LoginAsync(Login, Password);
            var j = await JournalFor(s, admin, st.GetProperty("sessionId").GetString()!);
            Assert(j.GetProperty("reason").GetString() == "revoked", j.ToString());
        });

        await Step("С1/В-6: ping-флуд без чтения ответов (касса и оператор) — соединение разорвано, память сервера почти не растёт", async () =>
        {
            GC.Collect();
            var before = ServerMemory(s);
            foreach (var role in new[] { "host", "operator" })
            {
                TestWs c;
                if (role == "host") c = (await NewHost(s)).host;
                else c = await NewOperator(s, token);
                c.AutoPong = false;
                c.Paused = true; // не читаем ответы, как поддельная касса
                var sent = 0;
                try
                {
                    for (; sent < 400_000; sent++)
                        await c.SendRawTextAsync("{\"type\":\"ping\"}");
                }
                catch { /* сервер оборвал — ожидаемо */ }
                c.Paused = false;
                Assert(await c.WaitClosedAsync(15000), $"{role}: соединение с флудом не закрыто (отправлено {sent})");
                await c.DisposeAsync();
            }
            await Task.Delay(500);
            var grow = ServerMemory(s) - before;
            Assert(grow < 10 * 1024 * 1024, $"память сервера выросла на {grow / 1024 / 1024} МБ");
            Console.WriteLine($"        (ping-флуд: рост памяти сервера {grow / 1024} КБ)");
        });

        await Step("С1/В-6: оператор шлёт мусор и не читает ошибки — служебная очередь > 1 МБ, соединение разорвано", async () =>
        {
            var before = ServerMemory(s);
            await using var op = await NewOperator(s, token);
            op.AutoPong = false;
            op.Paused = true;
            var sent = 0;
            try
            {
                for (; sent < 300_000; sent++)
                    await op.SendRawTextAsync("x");
            }
            catch { }
            op.Paused = false;
            Assert(await op.WaitClosedAsync(15000), $"мусор не привёл к разрыву (отправлено {sent})");
            var grow = ServerMemory(s) - before;
            Assert(grow < 20 * 1024 * 1024, $"память сервера выросла на {grow / 1024 / 1024} МБ");
        });

        await Step("С1/С-2: касса до сеанса шлёт двоичное 4 МБ — разрыв на первом (1008); текст > 4 КБ — разрыв (1009); код снят", async () =>
        {
            var (h, _, _) = await NewHost(s, "Двоичный флуд");
            var blob = new byte[4 * 1024 * 1024];
            try { for (var i = 0; i < 20; i++) await h.SendAsync(blob, WebSocketMessageType.Binary); }
            catch { }
            Assert(await h.WaitClosedAsync(8000), "соединение не закрыто");
            Assert(h.CloseStatus == WebSocketCloseStatus.PolicyViolation, "код закрытия " + h.CloseStatus);
            await h.DisposeAsync();

            var (h2, _, _) = await NewHost(s, "Длинный текст");
            await h2.SendRawTextAsync("{\"type\":\"noise\",\"x\":\"" + new string('a', 6000) + "\"}");
            Assert(await h2.WaitClosedAsync(8000), "соединение с длинным текстом не закрыто");
            Assert(h2.CloseStatus == WebSocketCloseStatus.MessageTooBig, "код закрытия " + h2.CloseStatus);
            await h2.DisposeAsync();
            await Task.Delay(300);
            var j = await Sessions(s, token);
            Assert(!j.GetProperty("active").EnumerateArray().Any(a => (a.GetProperty("company").GetString() ?? "").StartsWith("Двоичный") || (a.GetProperty("company").GetString() ?? "").StartsWith("Длинный")), "коды оборванных касс остались");
        });

        await Step("С1/В-7: лимит ожидающих кодов считается по сети IPv6 /64, а не по адресу", async () =>
        {
            static Task<TestWs> HostFrom(ServerProcess s, string ip) =>
                TestWs.ConnectAsync(s.WsUrl + "/ws/host", configure: o => o.SetRequestHeader("X-Forwarded-For", ip));
            static async Task<string> Hello(TestWs h)
            {
                await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, company = "v6", machine = "PC", screen = new { w = 1, h = 1 } });
                var m = await h.NextAsync() ?? throw new Exception("нет ответа");
                return m.JsonType == "error" ? "error:" + m.Json.GetProperty("code").GetString() : m.JsonType!;
            }
            var keep = new List<TestWs>();
            try
            {
                for (var i = 1; i <= 3; i++)
                {
                    var h = await HostFrom(s, $"2001:db8:aa:bb::{i}");
                    keep.Add(h);
                    var r = await Hello(h);
                    Assert(r == "session", $"код {i} из /64 не выдан: {r}");
                }
                var h4 = await HostFrom(s, "2001:db8:aa:bb:ffff::9");
                keep.Add(h4);
                var r4 = await Hello(h4);
                Assert(r4 == "error:rate-limited", "четвёртый адрес той же /64 получил: " + r4);
                var h5 = await HostFrom(s, "2001:db8:aa:cc::1");
                keep.Add(h5);
                var r5 = await Hello(h5);
                Assert(r5 == "session", "другая /64 получила: " + r5);
            }
            finally { foreach (var k in keep) await k.DisposeAsync(); }
        });

        await Step("С1: Origin чужого сайта на /ws/operator — 403; свой Origin и программа без Origin — подключаются", async () =>
        {
            var bad = false;
            try { await using var _ = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(token), configure: o => o.SetRequestHeader("Origin", "https://evil.example")); }
            catch (WebSocketException) { bad = true; }
            Assert(bad, "чужой Origin принят");
            await using var good = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(token), configure: o => o.SetRequestHeader("Origin", s.BaseUrl));
            await using var none = await NewOperator(s, token);
        });
    }

    // ---------- сервер D: пределы длительности сеанса ----------

    private async Task SuiteD(ServerProcess s)
    {
        await s.CliAsync(dll, $"add-operator {Login} \"{Name}\"", Password);
        var token = await s.LoginAsync(Login, Password);

        await Step("С1/С-6: оператор ничего не делает 3 с (предел простоя) — обеим сторонам end(idle), оператор отключён", async () =>
        {
            var (h, op, st) = await StartedWith(s, token, viewOnly: false);
            await using var _h = h; await using var _o = op;
            var sw = Stopwatch.StartNew();
            var eo = await op.ExpectAsync("end", 12000, skip: true);
            var eh = await h.ExpectAsync("end", 3000, skip: true);
            Assert(eo.GetProperty("reason").GetString() == "idle" && eh.GetProperty("reason").GetString() == "idle", eo + " / " + eh);
            Assert(sw.ElapsedMilliseconds is > 2000 and < 10000, $"простой сработал через {sw.ElapsedMilliseconds} мс");
            Assert(await op.WaitClosedAsync(5000), "оператор не отключён");
            var j = await JournalFor(s, token, st.GetProperty("sessionId").GetString()!);
            Assert(j.GetProperty("reason").GetString() == "idle", j.ToString());
        });

        await Step("С1/С-6: оператор работает, но сеанс дошёл до предела 6 с — обеим сторонам end(max)", async () =>
        {
            var (h, op, _) = await StartedWith(s, token, viewOnly: false);
            await using var _h = h; await using var _o = op;
            var sw = Stopwatch.StartNew();
            string? reason = null;
            while (sw.ElapsedMilliseconds < 14000 && reason is null)
            {
                await op.SendJsonAsync(new { type = "mouse", action = "move", x = 1, y = 1 });
                while (await op.NextAsync(700) is { } m)
                    if (m.JsonType == "end") { reason = m.Json.GetProperty("reason").GetString(); break; }
            }
            Assert(reason == "max", "причина " + reason + " через " + sw.ElapsedMilliseconds + " мс");
            Assert(sw.ElapsedMilliseconds > 4500, "слишком рано: " + sw.ElapsedMilliseconds);
        });
    }
}
