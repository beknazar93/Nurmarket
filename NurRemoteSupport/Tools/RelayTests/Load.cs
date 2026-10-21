using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;

namespace NurRemoteSupport.RelayTests;

/// <summary>
/// Нагрузка: N одновременных сеансов, у каждого касса шлёт кадры (1–4 куска по 5–40 КБ) с окном 2 неподтверждённых,
/// оператор подтверждает каждый кадр. Меряем процессор/память сервера и пропускную способность (2026-09-29).
/// </summary>
public sealed class Load(string dll, int sessions, int seconds, int fps)
{
    public async Task<int> RunAsync()
    {
        Console.WriteLine($"=== Нагрузка: {sessions} сеансов × {seconds} с, цель {fps} к/с на сеанс ===");
        ThreadPool.SetMinThreads(Math.Max(64, sessions * 2), 64); // клиентов много — не ждём разгона пула потоков
        await using var s = await ServerProcess.StartAsync(dll, new()
        {
            ["JoinPerMinute"] = "100000",
            ["HostConnectsPerMinutePerIp"] = "100000",
            ["MaxPendingPerIp"] = "100000",
            ["LoginPerMinutePerIp"] = "1000",
        }, quiet: true);
        await s.CliAsync(dll, "add-operator loadop \"Нагрузка\"", "Load-Test-Pass1");
        var token = await s.LoginAsync("loadop", "Load-Test-Pass1");

        s.Process.Refresh();
        var idleMem = s.Process.WorkingSet64;
        var idlePriv = s.Process.PrivateMemorySize64;

        // 1. Открываем сеансы
        var sw = Stopwatch.StartNew();
        var pairs = new List<(TestWs host, TestWs op)>();
        var setupTasks = Enumerable.Range(0, sessions).Select(async i =>
        {
            var h = await TestWs.ConnectAsync(s.WsUrl + "/ws/host", 15000);
            await h.SendJsonAsync(new { type = "hello", role = "host", protocol = 1, app = "Load", version = "1", company = "Нагрузка " + i, machine = "LOAD-" + i, screen = new { w = 1920, h = 1080 } });
            var sess = await h.ExpectAsync("session", 15000);
            var o = await TestWs.ConnectAsync(s.WsUrl + "/ws/operator?token=" + Uri.EscapeDataString(token), 15000);
            await o.SendJsonAsync(new { type = "join", code = sess.GetProperty("code").GetString(), pin = sess.GetProperty("pin").GetString() });
            await o.ExpectAsync("waiting-consent", 15000);
            await h.ExpectAsync("join-request", 15000);
            await h.SendJsonAsync(new { type = "consent", accept = true });
            await h.ExpectAsync("started", 15000);
            await o.ExpectAsync("started", 15000);
            lock (pairs) pairs.Add((h, o));
        }).ToList();
        await Task.WhenAll(setupTasks);
        Console.WriteLine($"Сеансов открыто: {pairs.Count} за {sw.ElapsedMilliseconds} мс");

        s.Process.Refresh();
        var connectedMem = s.Process.WorkingSet64;

        // 2. Поток
        var rttMs = new ConcurrentBag<double>();
        long framesSent = 0, framesAcked = 0, bytesToOperators = 0, errors = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var pool = new byte[64 * 1024];
        Random.Shared.NextBytes(pool);

        var cpuSamples = new List<double>();
        var memSamples = new List<long>();
        var privSamples = new List<long>();
        var threadSamples = new List<int>();
        var monitor = Task.Run(async () =>
        {
            var lastCpu = s.Process.TotalProcessorTime;
            var lastT = Stopwatch.GetTimestamp();
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cts.Token); } catch { break; }
                s.Process.Refresh();
                var cpu = s.Process.TotalProcessorTime;
                var now = Stopwatch.GetTimestamp();
                var wall = Stopwatch.GetElapsedTime(lastT, now).TotalMilliseconds;
                cpuSamples.Add((cpu - lastCpu).TotalMilliseconds / wall * 100.0); // % одного ядра
                memSamples.Add(s.Process.WorkingSet64);
                privSamples.Add(s.Process.PrivateMemorySize64);
                threadSamples.Add(s.Process.Threads.Count);
                lastCpu = cpu;
                lastT = now;
            }
        });

        var began = Stopwatch.StartNew();
        var streams = pairs.Select(pair => Task.Run(async () =>
        {
            var (h, o) = pair;
            var sent = new ConcurrentDictionary<uint, long>();
            long lastAck = 0;
            var ackSignal = new SemaphoreSlim(0, int.MaxValue);
            var rnd = new Random();
            // приём ack на стороне кассы
            var hostRecv = Task.Run(async () =>
            {
                while (true)
                {
                    var m = await h.NextAsync(seconds * 1000 + 10000);
                    if (m is null) break;
                    if (m.JsonType != "ack") continue;
                    var f = (uint)m.Json.GetProperty("frame").GetInt64();
                    if (sent.TryRemove(f, out var t0)) rttMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    Interlocked.Exchange(ref lastAck, f);
                    Interlocked.Increment(ref framesAcked);
                    ackSignal.Release();
                }
            });
            // приём кадров на стороне оператора
            var opRecv = Task.Run(async () =>
            {
                while (true)
                {
                    var m = await o.NextAsync(seconds * 1000 + 10000);
                    if (m is null) break;
                    if (m.Type != WebSocketMessageType.Binary) continue;
                    Interlocked.Add(ref bytesToOperators, m.Data.Length);
                    if (m.Data[0] == 2)
                        await o.SendJsonAsync(new { type = "ack", frame = BinaryPrimitives.ReadUInt32LittleEndian(m.Data.AsSpan(1)) });
                }
            });
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            // Расписание по абсолютному времени: погрешность таймера Windows (15,6 мс) не накапливается
            var schedule = Stopwatch.StartNew();
            var nextAt = TimeSpan.Zero;
            uint frame = 0;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    while (frame - Interlocked.Read(ref lastAck) >= 2 && !cts.IsCancellationRequested)
                        await ackSignal.WaitAsync(200);
                    if (cts.IsCancellationRequested) break;
                    frame++;
                    var tiles = 1 + rnd.Next(4);
                    for (var t = 0; t < tiles; t++)
                    {
                        var len = rnd.Next(5 * 1024, 40 * 1024);
                        var p = new byte[13 + len];
                        p[0] = 1;
                        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), frame);
                        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), 128);
                        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), 128);
                        Buffer.BlockCopy(pool, rnd.Next(0, pool.Length - len), p, 13, len);
                        await h.SendAsync(p, WebSocketMessageType.Binary);
                    }
                    var end = new byte[13];
                    end[0] = 2;
                    BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(1), frame);
                    sent[frame] = Stopwatch.GetTimestamp();
                    await h.SendAsync(end, WebSocketMessageType.Binary);
                    Interlocked.Increment(ref framesSent);
                    nextAt += interval;
                    var left = nextAt - schedule.Elapsed;
                    if (left > TimeSpan.Zero) await Task.Delay(left);
                    else if (left < -interval * 3) nextAt = schedule.Elapsed; // сильно отстали — не догоняем пачкой
                }
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
            await Task.Delay(500);
            await o.SendJsonAsync(new { type = "end", reason = "operator" });
            await Task.WhenAll(hostRecv, opRecv).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        })).ToList();

        await Task.WhenAll(streams);
        var elapsed = began.Elapsed.TotalSeconds;
        cts.Cancel();
        await monitor;

        // 3. Итог
        await Task.Delay(1000);
        s.Process.Refresh();
        var afterMem = s.Process.WorkingSet64;
        var journalLines = File.Exists(Path.Combine(s.DataDir, "journal.jsonl")) ? File.ReadAllLines(Path.Combine(s.DataDir, "journal.jsonl")).Length : 0;
        foreach (var (h, o) in pairs) { await h.DisposeAsync(); await o.DisposeAsync(); }

        var rtts = rttMs.OrderBy(x => x).ToArray();
        double P(double q) => rtts.Length == 0 ? double.NaN : rtts[Math.Min(rtts.Length - 1, (int)(q * rtts.Length))];
        var cores = Environment.ProcessorCount;
        var mb = 1024.0 * 1024.0;
        var steady = cpuSamples.Skip(1).DefaultIfEmpty(0).ToList();

        Console.WriteLine();
        Console.WriteLine("----- РЕЗУЛЬТАТ -----");
        Console.WriteLine($"Сеансов: {pairs.Count}, длительность потока: {elapsed:F1} с, ядер на ПК: {cores}");
        Console.WriteLine($"Кадров отправлено: {framesSent}, подтверждено: {framesAcked} ({framesAcked / elapsed:F0} к/с всего, {framesAcked / elapsed / Math.Max(1, pairs.Count):F1} к/с на сеанс при цели {fps})");
        Console.WriteLine($"Передано операторам через сервер: {bytesToOperators / mb:F0} МБ, {bytesToOperators / mb / elapsed:F1} МБ/с ({bytesToOperators * 8 / 1e6 / elapsed:F0} Мбит/с)");
        Console.WriteLine($"Кадр → ack (туда и обратно через сервер): p50 {P(0.5):F1} мс, p95 {P(0.95):F1} мс, p99 {P(0.99):F1} мс, max {(rtts.Length > 0 ? rtts[^1] : 0):F1} мс");
        Console.WriteLine($"Процессор сервера: в среднем {steady.Average():F0}% одного ядра ({steady.Average() / cores:F1}% всех {cores}), пик {steady.Max():F0}%");
        Console.WriteLine($"Память сервера (рабочий набор): без нагрузки {idleMem / mb:F0} МБ, после подключения {connectedMem / mb:F0} МБ, пик под нагрузкой {memSamples.DefaultIfEmpty(0).Max() / mb:F0} МБ, после {afterMem / mb:F0} МБ");
        Console.WriteLine($"Память сервера (private): без нагрузки {idlePriv / mb:F0} МБ, пик {privSamples.DefaultIfEmpty(0).Max() / mb:F0} МБ; потоков пик {threadSamples.DefaultIfEmpty(0).Max()}");
        Console.WriteLine($"Ошибок в потоках: {errors}; строк журнала: {journalLines} (ожидалось {pairs.Count})");
        string log;
        lock (s.Log) log = s.Log.ToString();
        var warnLines = log.Split('\n').Count(l => l.Contains("fail") || l.Contains("ERR ") || l.Contains("crit"));
        Console.WriteLine($"Ошибок в логе сервера: {warnLines}");
        if (warnLines > 0) foreach (var l in log.Split('\n').Where(l => l.Contains("fail") || l.Contains("ERR ")).Take(10)) Console.WriteLine("  | " + l);
        return errors == 0 && journalLines == pairs.Count ? 0 : 1;
    }
}
