// Поддельная касса (хост) для проверки ретранслятора и консоли оператора (2026-09-29).
// Говорит по протоколу NurRemoteSupport/PROTOCOL.md: hello → код+PIN → согласие → поток JPEG-кусков.
// Ввод оператора НЕ выполняется (никаких нажатий на этом ПК) — только рисуется на картинке и пишется в консоль.
//
// Примеры:
//   FakeHost --url ws://127.0.0.1:5080/ws/host
//   FakeHost --url wss://1-2-3-4.sslip.io/ws/host --company "Тест" --fps 15 --size 1600x900
//   FakeHost --screen            (показывать настоящий экран этого ПК, только просмотр)
//   FakeHost --accept ask|auto|decline|none   --frames 100   --seconds 30
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NurRemoteSupport.FakeHost;

internal sealed class Options
{
    public string Url = "ws://127.0.0.1:5080/ws/host";
    public string Company = "Тестовая касса";
    public string Machine = Environment.MachineName;
    public string Version = "0.0.0-fakehost";
    public int Width = 1280, Height = 720;
    public int Fps = 15;
    public int Quality = 60;
    public bool Screen;
    public string Accept = "auto";
    public int MaxFrames;    // 0 — без предела
    public int MaxSeconds;   // 0 — без предела
    public int TileSize = 128;
    // 2026-10-16, v1.1: кассир и проблема в hello (очередь программы оператора).
    public string User = "Тест Кассир";
    public string Note = "";
    // 2026-10-26: приоритет кассира — расписание "мс:true,мс:false" от начала сеанса: FakeHost шлёт {"type":"client-active"} и в это время
    // сам не исполняет ввод оператора (как настоящая касса).
    public string ClientSchedule = "";
    // 2026-10-27, v1.3: возможности кассы в hello. --caps none — «старая» касса 1.17.61 без поля caps (чат, сведения, файлы молчат);
    // --caps a,b,c — свой список. По умолчанию FakeHost умеет всё, что у него реализовано.
    public string Caps = "client-active,view-only,screen-paused,chat,info,diag,logs,files,clipboard";
    // 2026-10-27: --screen-pause "мс:длительность" — имитация UAC/блокировки: через мс от начала сеанса шлёт screen-paused и перестаёт слать кадры
    // на длительность мс, потом кадры идут снова (сеанс не рвётся).
    public string ScreenPause = "";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Нет значения для " + args[i]);
            switch (args[i])
            {
                case "--url": o.Url = Next(); break;
                case "--company": o.Company = Next(); break;
                case "--machine": o.Machine = Next(); break;
                case "--version": o.Version = Next(); break;
                case "--size":
                {
                    var p = Next().Split('x', 'X');
                    o.Width = int.Parse(p[0]);
                    o.Height = int.Parse(p[1]);
                    break;
                }
                case "--fps": o.Fps = Math.Clamp(int.Parse(Next()), 1, 60); break;
                case "--quality": o.Quality = Math.Clamp(int.Parse(Next()), 10, 90); break;
                case "--screen": o.Screen = true; break;
                case "--accept": o.Accept = Next(); break;
                case "--frames": o.MaxFrames = int.Parse(Next()); break;
                case "--seconds": o.MaxSeconds = int.Parse(Next()); break;
                case "--tile": o.TileSize = Math.Clamp(int.Parse(Next()), 32, 1024); break;
                case "--user": o.User = Next(); break;
                case "--note": o.Note = Next(); break;
                case "--client-active": o.ClientSchedule = Next(); break;
                case "--caps": o.Caps = Next(); break;
                case "--screen-pause": o.ScreenPause = Next(); break;
                case "-h":
                case "--help":
                    Console.WriteLine("FakeHost [--url ws://…/ws/host] [--company X] [--size 1280x720] [--fps 15] [--quality 60] [--screen] [--accept auto|ask|decline|none] [--frames N] [--seconds N]");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException("Неизвестный параметр " + args[i]);
            }
        }
        return o;
    }
}

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options o;
        try { o = Options.Parse(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        if (o.Screen)
        {
            SetProcessDPIAware();
            var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            o.Width = b.Width;
            o.Height = b.Height;
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var host = new Host(o);
        try
        {
            return await host.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Остановлено.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
    }
}

internal sealed class Host(Options o)
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _drawGate = new();
    private readonly List<Point> _clicks = new();
    private readonly StringBuilder _typed = new();
    private volatile bool _started, _ended;
    private volatile int _quality = o.Quality;
    private volatile bool _viewOnly;
    private volatile bool _clientActive; // 2026-10-26: кассир «работает» — ввод оператора пропускается
    private volatile bool _forceFull = true;
    private uint _frameNo;
    private long _lastAck;
    private Point _mouse = new(-1, -1);
    private int _moves, _keys, _texts, _wheels, _acks;
    private long _bytesSent;
    private string _operator = "";

    public async Task<int> RunAsync(CancellationToken ct)
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        Console.WriteLine($"Подключаюсь к {o.Url} …");
        await _ws.ConnectAsync(new Uri(o.Url), ct);
        await SendJsonAsync(new
        {
            type = "hello", role = "host", protocol = 1, app = "FakeHost", version = o.Version,
            company = o.Company, machine = o.Machine, screen = new { w = o.Width, h = o.Height },
            user = o.User, note = o.Note,
            caps = o.Caps == "none" ? null : o.Caps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        }, ct);

        Task? stream = null;
        using var statsTimer = new System.Threading.Timer(_ => PrintStats(), null, 5000, 5000);
        var buf = new byte[64 * 1024];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType != WebSocketMessageType.Text) continue; // двоичное хосту не положено
            JsonElement m;
            try { m = JsonDocument.Parse(ms.ToArray()).RootElement; }
            catch (JsonException) { continue; }
            var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ping":
                    await SendJsonAsync(new { type = "pong" }, ct);
                    break;
                // 2026-10-16, v1.1: простые ответы на инструменты программы оператора (настоящая касса отвечает подробно).
                case "chat" when HasCap("chat"):
                    Console.WriteLine($"чат от оператора: {m.GetProperty("text").GetString()}");
                    await SendJsonAsync(new { type = "chat", text = "FakeHost получил: " + m.GetProperty("text").GetString() }, ct);
                    break;
                case "info-request" when HasCap("info"):
                    await SendJsonAsync(new { type = "info", text = $"FakeHost {o.Version}\nКомпания: {o.Company}\nКомпьютер: {o.Machine}\nЭкран: {o.Width}×{o.Height}" }, ct);
                    break;
                case "diag-request" when HasCap("diag"):
                    await SendJsonAsync(new { type = "diag", items = new object[] { new { name = "Связь с NurCRM", ok = (bool?)true, detail = "FakeHost: проверка-заглушка" }, new { name = "Принтер чеков", ok = (bool?)false, detail = "FakeHost: принтера нет" }, new { name = "Версия программы", ok = (bool?)null, detail = o.Version } } }, ct);
                    break;
                case "logs-request" when HasCap("logs"):
                    await SendJsonAsync(new { type = "logs", text = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"{DateTime.Now:HH:mm:ss} [INFO] FakeHost строка {i}")) }, ct);
                    break;
                // 2026-10-27, v1.3: буфер обмена и файлы (только если в caps есть clipboard/files; со старой кассой — молчание)
                case "clipboard-set" when HasCap("clipboard"):
                    _clipboard = m.GetProperty("text").GetString() ?? "";
                    Console.WriteLine($"  буфер кассы ← {_clipboard.Length} символов");
                    break;
                case "clipboard-get" when HasCap("clipboard"):
                    await SendJsonAsync(new { type = "clipboard", text = _clipboard }, ct);
                    break;
                case "file-begin" when HasCap("files"):
                    _files[m.GetProperty("id").GetString()!] = (m.GetProperty("name").GetString() ?? "file", 0L);
                    break;
                case "file-chunk" when HasCap("files"):
                {
                    var id = m.GetProperty("id").GetString()!;
                    if (_files.TryGetValue(id, out var f))
                        _files[id] = (f.name, f.bytes + Convert.FromBase64String(m.GetProperty("data").GetString() ?? "").Length);
                    break;
                }
                case "file-end" when HasCap("files"):
                {
                    var id = m.GetProperty("id").GetString()!;
                    if (_files.Remove(id, out var f))
                    {
                        Console.WriteLine($"  файл «{f.name}»: {f.bytes} байт принято (не сохраняется)");
                        await SendJsonAsync(new { type = "file-saved", id, name = f.name }, ct);
                    }
                    break;
                }
                case "session":
                {
                    var code = m.GetProperty("code").GetString()!;
                    var pin = m.GetProperty("pin").GetString()!;
                    Console.WriteLine();
                    Console.WriteLine($"  Код:  {code[..3]} {code[3..6]} {code[6..]}");
                    Console.WriteLine($"  PIN:  {pin}");
                    Console.WriteLine($"  (действует {m.GetProperty("expiresIn").GetInt32() / 60} мин)");
                    Console.WriteLine($"CODE={code} PIN={pin}");
                    break;
                }
                case "join-request":
                {
                    var op = m.GetProperty("operator").GetString();
                    // 2026-10-27: сервер больше не передаёт кассе IP оператора (поле ip убрано)
                    Console.WriteLine($"Запрос подключения: оператор «{op}»");
                    _ = AnswerConsentAsync(op ?? "", ct);
                    break;
                }
                case "started":
                    _operator = m.GetProperty("operator").GetString() ?? "";
                    Console.WriteLine($"СЕАНС НАЧАТ: {m.GetProperty("sessionId").GetString()}, оператор «{_operator}»");
                    _started = true;
                    if (o.ClientSchedule.Length > 0) _ = Task.Run(() => RunClientScheduleAsync(ct), ct);
                    if (o.ScreenPause.Length > 0) _ = Task.Run(() => RunScreenPauseAsync(ct), ct);
                    stream = Task.Run(() => StreamAsync(ct), ct);
                    break;
                case "expired":
                    Console.WriteLine("Код истёк (никто не подключился).");
                    _ended = true;
                    break;
                case "locked":
                    Console.WriteLine("Заблокировано: 5 неверных PIN.");
                    _ended = true;
                    break;
                case "error":
                    Console.WriteLine("Ошибка сервера: " + m.GetProperty("code").GetString());
                    _ended = true;
                    break;
                case "end":
                    Console.WriteLine("Сеанс завершён: " + m.GetProperty("reason").GetString());
                    _ended = true;
                    break;
                case "ack":
                {
                    var f = m.GetProperty("frame").GetInt64();
                    Interlocked.Increment(ref _acks);
                    if (f > Interlocked.Read(ref _lastAck)) Interlocked.Exchange(ref _lastAck, f);
                    _ackSignal.Release();
                    break;
                }
                case "mouse" or "key" or "text" when _clientActive:
                    Console.WriteLine($"  ввод пропущен: кассир работает ({type})");
                    break;
                case "mouse":
                    OnMouse(m);
                    break;
                case "key":
                {
                    Interlocked.Increment(ref _keys);
                    var action = m.GetProperty("action").GetString();
                    var code = m.GetProperty("code").GetString();
                    var key = m.TryGetProperty("key", out var k) ? k.GetString() : "";
                    Console.WriteLine($"  клавиша {action} {code} «{key}»");
                    if (action == "down" && !_viewOnly)
                    {
                        lock (_drawGate)
                        {
                            if (code == "Backspace" && _typed.Length > 0) _typed.Length--;
                            else if (code == "Enter") _typed.Append(" ⏎ ");
                            else if (key is { Length: 1 }) _typed.Append(key);
                            TrimTyped();
                        }
                    }
                    break;
                }
                case "text":
                {
                    Interlocked.Increment(ref _texts);
                    var text = m.GetProperty("text").GetString() ?? "";
                    Console.WriteLine($"  текст «{(text.Length > 60 ? text[..60] + "…" : text)}» ({text.Length} симв.)");
                    if (!_viewOnly) lock (_drawGate) { _typed.Append(text.Replace('\n', ' ')); TrimTyped(); }
                    break;
                }
                case "quality":
                    _quality = Math.Clamp(m.GetProperty("value").GetInt32(), 10, 90);
                    _forceFull = true;
                    Console.WriteLine($"  качество → {_quality}");
                    break;
                case "view-only":
                    _viewOnly = m.GetProperty("value").GetBoolean();
                    Console.WriteLine($"  только просмотр → {_viewOnly}");
                    break;
                case "monitor":
                    Console.WriteLine($"  монитор → {m.GetProperty("index").GetInt32()} (у FakeHost один)");
                    break;
                default:
                    Console.WriteLine($"  неизвестное сообщение: {type}");
                    break;
            }
            if (_ended) break;
        }
        _ended = true;
        if (stream is not null) { try { await stream; } catch { } }
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        PrintStats();
        return 0;
    }

    private bool HasCap(string cap) => o.Caps != "none" && o.Caps.Split(',').Contains(cap);
    private string _clipboard = "";
    private readonly Dictionary<string, (string name, long bytes)> _files = new();
    private volatile bool _screenPaused;

    /// <summary>2026-10-27: имитация UAC/блокировки — screen-paused, кадры не идут, затем снова идут.</summary>
    private async Task RunScreenPauseAsync(CancellationToken ct)
    {
        var p = o.ScreenPause.Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], out var at) || !int.TryParse(p[1], out var len)) return;
        await Task.Delay(at, ct);
        if (_ended) return;
        _screenPaused = true;
        Console.WriteLine("  экран защищён Windows (имитация) → screen-paused");
        await SendJsonAsync(new { type = "screen-paused", reason = "secure-desktop" }, ct);
        await Task.Delay(len, ct);
        _screenPaused = false;
        _forceFull = true; // после паузы — полный кадр, как у кассы
        Console.WriteLine("  экран снова доступен");
    }

    /// <summary>2026-10-26: расписание «кассир работает» из --client-active "1500:true,3500:false" (мс от начала сеанса).</summary>
    private async Task RunClientScheduleAsync(CancellationToken ct)
    {
        var start = Stopwatch.StartNew();
        foreach (var step in o.ClientSchedule.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = step.Split(':');
            if (p.Length != 2 || !int.TryParse(p[0], out var at) || !bool.TryParse(p[1], out var active)) continue;
            var wait = at - (int)start.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct);
            if (_ended) return;
            _clientActive = active;
            Console.WriteLine($"  кассир {(active ? "работает" : "закончил")} → client-active {(active ? "true" : "false")}");
            await SendJsonAsync(new { type = "client-active", active }, ct);
        }
    }

    private void TrimTyped()
    {
        if (_typed.Length > 80) _typed.Remove(0, _typed.Length - 80);
    }

    private void OnMouse(JsonElement m)
    {
        var action = m.GetProperty("action").GetString();
        var x = m.TryGetProperty("x", out var xe) ? xe.GetInt32() : 0;
        var y = m.TryGetProperty("y", out var ye) ? ye.GetInt32() : 0;
        if (x < 0 || y < 0 || x >= o.Width || y >= o.Height)
        {
            Console.WriteLine($"  мышь: координаты вне экрана ({x},{y}) — отброшено");
            return;
        }
        switch (action)
        {
            case "move":
                Interlocked.Increment(ref _moves);
                break;
            case "wheel":
                Interlocked.Increment(ref _wheels);
                Console.WriteLine($"  колесо {m.GetProperty("delta").GetInt32()} в ({x},{y})");
                break;
            default:
                Console.WriteLine($"  мышь {action} кнопка {(m.TryGetProperty("button", out var b) ? b.GetInt32() : 0)} в ({x},{y})");
                break;
        }
        if (_viewOnly) return;
        lock (_drawGate)
        {
            _mouse = new Point(x, y);
            if (action == "down")
            {
                _clicks.Add(_mouse);
                if (_clicks.Count > 8) _clicks.RemoveAt(0);
            }
        }
    }

    private async Task AnswerConsentAsync(string op, CancellationToken ct)
    {
        switch (o.Accept)
        {
            case "none":
                Console.WriteLine("  (--accept none: не отвечаю, ждём тайм-аута)");
                return;
            case "decline":
                await Task.Delay(300, ct);
                Console.WriteLine("  → отказ");
                await SendJsonAsync(new { type = "consent", accept = false }, ct);
                return;
            case "view":
                // 2026-10-27: кассир выбрал «Только просмотр» (consent view_only:true) — как настоящая касса
                await Task.Delay(300, ct);
                Console.WriteLine("  → разрешено только просмотр (--accept view)");
                _viewOnly = true;
                await SendJsonAsync(new { type = "consent", accept = true, view_only = true }, ct);
                return;
            case "ask":
            {
                Console.Write($"Разрешить оператору «{op}» подключиться? [y/N] (60 с): ");
                var read = Task.Run(Console.ReadLine, ct);
                var done = await Task.WhenAny(read, Task.Delay(60_000, ct));
                var yes = done == read && (read.Result ?? "").Trim().ToLowerInvariant() is "y" or "д" or "yes" or "да";
                await SendJsonAsync(new { type = "consent", accept = yes }, ct);
                return;
            }
            default:
                await Task.Delay(300, ct);
                Console.WriteLine("  → разрешено (--accept auto)");
                await SendJsonAsync(new { type = "consent", accept = true }, ct);
                return;
        }
    }

    // ---------- поток кадров ----------

    private async Task StreamAsync(CancellationToken ct)
    {
        int w = o.Width, h = o.Height, tile = o.TileSize;
        await SendJsonAsync(new { type = "screen", w, h, monitor = 0, monitors = 1 }, ct);
        using var cur = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var background = o.Screen ? null : MakeBackground(w, h);
        var stride = 0;
        byte[]? curBytes = null, prevBytes = null;
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var interval = TimeSpan.FromSeconds(1.0 / o.Fps);
        var began = Stopwatch.StartNew();
        var lastCursor = new Point(-1, -1);
        var lastStats = Stopwatch.StartNew();
        long bytesAtStats = 0;
        uint framesAtStats = 0;

        while (!_ended && !ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            var tick = Stopwatch.StartNew();
            if (o.MaxSeconds > 0 && began.Elapsed.TotalSeconds >= o.MaxSeconds) break;
            if (o.MaxFrames > 0 && _frameNo >= o.MaxFrames) break;

            if (_screenPaused) { await Task.Delay(100, ct); continue; } // 2026-10-27: экран защищён — кадров нет
            // Не больше 2 неподтверждённых кадров (PROTOCOL.md)
            while (_frameNo - Interlocked.Read(ref _lastAck) >= 2 && !_ended)
                await _ackSignal.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
            if (_ended) break;

            // Рисуем кадр
            using (var g = Graphics.FromImage(cur))
            {
                if (o.Screen) g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                else DrawScene(g, background!, w, h);
            }
            var data = cur.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                stride = data.Stride;
                curBytes ??= new byte[stride * h];
                prevBytes ??= new byte[stride * h];
                Marshal.Copy(data.Scan0, curBytes, 0, stride * h);
            }
            finally { cur.UnlockBits(data); }

            // Изменившиеся куски
            var full = _forceFull;
            _forceFull = false;
            var changed = new List<Rectangle>();
            for (var ty = 0; ty < h; ty += tile)
            for (var tx = 0; tx < w; tx += tile)
            {
                var rect = new Rectangle(tx, ty, Math.Min(tile, w - tx), Math.Min(tile, h - ty));
                if (full || TileChanged(curBytes, prevBytes, stride, rect)) changed.Add(rect);
            }
            (prevBytes, curBytes) = (curBytes, prevBytes);

            if (changed.Count > 0)
            {
                var frame = ++_frameNo;
                using var q = new EncoderParameters(1);
                q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_quality);
                foreach (var rect in changed)
                {
                    using var part = cur.Clone(rect, PixelFormat.Format24bppRgb);
                    using var ms = new MemoryStream();
                    part.Save(ms, jpeg, q);
                    await SendBinaryAsync(Packet(1, frame, rect, ms.GetBuffer().AsSpan(0, (int)ms.Length)), ct);
                }
                await SendBinaryAsync(Packet(2, frame, Rectangle.Empty, ReadOnlySpan<byte>.Empty), ct);
            }

            // Курсор: в синтетике — последняя точка оператора, на настоящем экране — настоящий курсор
            var c = o.Screen ? System.Windows.Forms.Cursor.Position : _mouse;
            if (c != lastCursor && c.X >= 0)
            {
                lastCursor = c;
                await SendJsonAsync(new { type = "cursor", x = c.X, y = c.Y }, ct);
            }

            if (lastStats.Elapsed.TotalSeconds >= 1)
            {
                var secs = lastStats.Elapsed.TotalSeconds;
                var sent = Interlocked.Read(ref _bytesSent);
                await SendJsonAsync(new { type = "stats", fps = Math.Round((_frameNo - framesAtStats) / secs, 1), kbps = (long)((sent - bytesAtStats) * 8 / 1000 / secs) }, ct);
                framesAtStats = _frameNo;
                bytesAtStats = sent;
                lastStats.Restart();
            }

            var left = interval - tick.Elapsed;
            if (left > TimeSpan.Zero) await Task.Delay(left, ct);
        }

        if (!_ended && _ws.State == WebSocketState.Open)
        {
            Console.WriteLine("Поток окончен (--frames/--seconds) — завершаю сеанс.");
            await SendJsonAsync(new { type = "end", reason = "host" }, ct);
        }
    }

    private static bool TileChanged(byte[] cur, byte[] prev, int stride, Rectangle r)
    {
        var len = r.Width * 3;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var off = y * stride + r.Left * 3;
            if (!cur.AsSpan(off, len).SequenceEqual(prev.AsSpan(off, len))) return true;
        }
        return false;
    }

    private static byte[] Packet(byte kind, uint frame, Rectangle r, ReadOnlySpan<byte> jpeg)
    {
        var p = new byte[13 + jpeg.Length];
        p[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), frame);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), (ushort)r.X);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7), (ushort)r.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), (ushort)r.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), (ushort)r.Height);
        jpeg.CopyTo(p.AsSpan(13));
        return p;
    }

    private Bitmap MakeBackground(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(24, 40, 72), Color.FromArgb(18, 92, 110), 35f))
            g.FillRectangle(br, 0, 0, w, h);
        using var grid = new Pen(Color.FromArgb(40, 255, 255, 255));
        for (var x = 0; x < w; x += 80) g.DrawLine(grid, x, 0, x, h);
        for (var y = 0; y < h; y += 80) g.DrawLine(grid, 0, y, w, y);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var title = new Font("Segoe UI", 28, FontStyle.Bold);
        using var small = new Font("Segoe UI", 14);
        g.DrawString("NurMarket FakeHost — тестовая касса", title, Brushes.White, 40, 30);
        g.DrawString($"{o.Company} · {o.Machine} · {w}×{h}. Ввод оператора только рисуется, ничего не нажимается.", small, Brushes.Gainsboro, 44, 84);
        // Метки координат — удобно проверять, что клик попал туда
        using var tiny = new Font("Consolas", 10);
        for (var x = 0; x < w; x += 160)
        for (var y = 160; y < h; y += 160)
            g.DrawString($"{x},{y}", tiny, Brushes.LightSteelBlue, x + 2, y + 2);
        return bmp;
    }

    private void DrawScene(Graphics g, Bitmap background, int w, int h)
    {
        g.DrawImageUnscaled(background, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        var t = _clock.Elapsed.TotalSeconds;
        // Прыгающий шар
        var bx = (int)((Math.Sin(t * 0.9) * 0.5 + 0.5) * (w - 120)) + 10;
        var by = (int)((Math.Abs(Math.Sin(t * 2.1))) * (h - 300)) + 150;
        using (var ball = new SolidBrush(Color.FromArgb(255, 196, 61)))
            g.FillEllipse(ball, bx, by, 100, 100);
        // Часы и номер кадра
        using var big = new Font("Consolas", 22, FontStyle.Bold);
        using var mid = new Font("Segoe UI", 16);
        g.FillRectangle(Brushes.Black, w - 330, h - 70, 310, 50);
        g.DrawString($"{DateTime.Now:HH:mm:ss.f}  #{_frameNo}", big, Brushes.Lime, w - 320, h - 64);
        lock (_drawGate)
        {
            // Набранный текст
            using (var shade = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillRectangle(shade, 30, h - 70, w - 380, 50);
            g.DrawString("Ввод: " + _typed, mid, Brushes.White, 40, h - 60);
            // Клики
            using var clickPen = new Pen(Color.OrangeRed, 3);
            foreach (var p in _clicks) g.DrawEllipse(clickPen, p.X - 14, p.Y - 14, 28, 28);
            // Перекрестие мыши оператора
            if (_mouse.X >= 0)
            {
                using var pen = new Pen(Color.Cyan, 2);
                g.DrawLine(pen, _mouse.X - 20, _mouse.Y, _mouse.X + 20, _mouse.Y);
                g.DrawLine(pen, _mouse.X, _mouse.Y - 20, _mouse.X, _mouse.Y + 20);
                g.DrawString($"{_mouse.X},{_mouse.Y}", mid, Brushes.Cyan, _mouse.X + 8, _mouse.Y + 6);
            }
        }
        if (_viewOnly) g.DrawString("ТОЛЬКО ПРОСМОТР", mid, Brushes.Orange, w - 250, 30);
        g.DrawString($"качество {_quality}", mid, Brushes.Gainsboro, w - 250, 60);
    }

    // ---------- отправка ----------

    private async Task SendJsonAsync(object o, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(o);
        await SendAsync(bytes, WebSocketMessageType.Text, ct);
    }

    private Task SendBinaryAsync(byte[] data, CancellationToken ct) => SendAsync(data, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            await _ws.SendAsync(data, type, true, ct);
            Interlocked.Add(ref _bytesSent, data.Length);
        }
        finally { _sendLock.Release(); }
    }

    private void PrintStats()
    {
        if (!_started) return;
        Console.WriteLine($"[статистика] кадров {_frameNo}, подтверждено {_acks}, отправлено {Interlocked.Read(ref _bytesSent) / 1024} КБ; мышь: движений {_moves}, колесо {_wheels}; клавиш {_keys}, текстов {_texts}");
    }
}
