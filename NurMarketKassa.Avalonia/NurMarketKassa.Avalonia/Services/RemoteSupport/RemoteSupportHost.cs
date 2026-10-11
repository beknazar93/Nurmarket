#if !NURANDROID
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services.RemoteSupport;

/// <summary>2026-10-07, владелец: «начни разработку аналога AnyDesk для тех поддержки» (решение 29.09 — «полностью своё, свой VPS»;
/// 07.10 — сервер на нашем VPS, оператор — отдельная программа). Касса-хост по протоколу NurRemoteSupport/PROTOCOL.md v1:
/// hello → код и PIN → запрос оператора → согласие кассира → поток экрана (изменившиеся куски JPEG, не больше 2 неподтверждённых
/// кадров) и ввод оператора (мышь, клавиши, текст) через SendInput. Логика потока — из проверенного FakeHost
/// (NurRemoteSupport/Tools/FakeHost). Только основной монитор. Никакого доступа без кнопки кассира «Разрешить».</summary>
public sealed class RemoteSupportHost : IAsyncDisposable
{
    public enum State { Connecting, WaitingOperator, AskingConsent, InSession, Ended }

    /// <summary>Адрес ретранслятора (wss://…/ws/host). Пока сервер не поставлен на VPS — из файла
    /// %LOCALAPPDATA%\NurMarketKassa\remote-relay.txt или переменной NURREMOTE_RELAY; пусто — своя поддержка выключена.</summary>
    public static string RelayUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NURREMOTE_RELAY");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            try
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "remote-relay.txt");
                if (File.Exists(file))
                    return File.ReadAllText(file).Trim();
            }
            catch
            {
                // нет доступа к файлу — считаем, что не настроено
            }
            return DefaultRelayUrl;
        }
    }

    /// <summary>Боевой адрес ретранслятора — впишется, когда сервер встанет на VPS.</summary>
    public const string DefaultRelayUrl = "";

    public event Action<State>? StateChanged;
    public event Action<string, string, int>? CodeReceived;          // код, PIN, секунд до истечения
    public event Func<string, Task<bool>>? ConsentRequested;          // имя оператора → разрешить?
    public event Action<string>? SessionStarted;                      // имя оператора
    public event Action<string>? Ended;                               // причина словами

    public State Current { get; private set; } = State.Connecting;
    public string Operator { get; private set; } = "";
    public bool ViewOnly => _viewOnly;

    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ushort> _keysDown = new();
    private readonly HashSet<int> _buttonsDown = new();
    private volatile bool _viewOnly;
    private volatile bool _forceFull = true;
    private volatile int _quality = 60;
    private uint _frameNo;
    private long _lastAck;
    private Thread? _streamThread;
    private int _screenW, _screenH;

    private void SetState(State s)
    {
        Current = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>Подключиться к ретранслятору и получить код. Завершается, когда сеанс (или ожидание) закончился.</summary>
    public async Task RunAsync(string company, string version)
    {
        var ct = _cts.Token;
        _screenW = GetSystemMetrics(SM_CXSCREEN);
        _screenH = GetSystemMetrics(SM_CYSCREEN);
        var endReason = "";
        try
        {
            SetState(State.Connecting);
            await _ws.ConnectAsync(new Uri(RelayUrl), ct).ConfigureAwait(false);
            await SendJsonAsync(new
            {
                type = "hello", role = "host", protocol = 1, app = "NurMarketKassa", version, company,
                machine = Environment.MachineName, screen = new { w = _screenW, h = _screenH },
            }, ct).ConfigureAwait(false);
            var buf = new byte[64 * 1024];
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                if (r.MessageType != WebSocketMessageType.Text)
                    continue; // двоичное хосту в v1 не положено
                JsonElement m;
                try
                {
                    m = JsonDocument.Parse(ms.ToArray()).RootElement;
                }
                catch (JsonException)
                {
                    continue;
                }
                var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "ping":
                        await SendJsonAsync(new { type = "pong" }, ct).ConfigureAwait(false);
                        break;
                    case "session":
                        SetState(State.WaitingOperator);
                        CodeReceived?.Invoke(m.GetProperty("code").GetString() ?? "", m.GetProperty("pin").GetString() ?? "",
                            m.TryGetProperty("expiresIn", out var exp) ? exp.GetInt32() : 900);
                        break;
                    case "join-request":
                        _ = AskConsentAsync(m.TryGetProperty("operator", out var op) ? op.GetString() ?? "" : "", ct);
                        break;
                    case "started":
                        Operator = m.TryGetProperty("operator", out var name) ? name.GetString() ?? "" : "";
                        PosLogger.Log($"Удалённая помощь: сеанс начат, оператор «{Operator}».", "INFO");
                        SetState(State.InSession);
                        SessionStarted?.Invoke(Operator);
                        _streamThread = new Thread(() => StreamLoop(ct)) { IsBackground = true, Name = "RemoteSupportStream" };
                        _streamThread.Start();
                        break;
                    case "expired":
                        endReason = Tr.T("Код истёк — никто не подключился за 15 минут.", "Код мөөнөтү бүттү — 15 мүнөттө эч ким туташкан жок.",
                            "The code expired — nobody connected within 15 minutes.", "Kodun süresi doldu — 15 dakikada kimse bağlanmadı.",
                            "Kod muddati tugadi — 15 daqiqada hech kim ulanmadi.");
                        break;
                    case "locked":
                        endReason = Tr.T("Заблокировано: 5 раз ввели неверный PIN. Получите новый код.", "Бөгөттөлдү: PIN 5 жолу туура эмес киргизилди. Жаңы код алыңыз.",
                            "Locked: a wrong PIN was entered 5 times. Get a new code.", "Kilitlendi: PIN 5 kez yanlış girildi. Yeni kod alın.",
                            "Bloklandi: PIN 5 marta noto'g'ri kiritildi. Yangi kod oling.");
                        break;
                    case "error":
                        endReason = Tr.T("Сервер поддержки ответил ошибкой: ", "Колдоо сервери ката менен жооп берди: ", "The support server returned an error: ",
                            "Destek sunucusu hata döndürdü: ", "Qo'llab-quvvatlash serveri xato qaytardi: ") + (m.TryGetProperty("code", out var c) ? c.GetString() : "?");
                        break;
                    case "end":
                        endReason = EndText(m.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "");
                        break;
                    case "ack":
                        var f = m.GetProperty("frame").GetInt64();
                        if (f > Interlocked.Read(ref _lastAck))
                            Interlocked.Exchange(ref _lastAck, f);
                        _ackSignal.Release();
                        break;
                    case "mouse":
                        if (Current == State.InSession && !_viewOnly)
                            OnMouse(m);
                        break;
                    case "key":
                        if (Current == State.InSession && !_viewOnly)
                            OnKey(m);
                        break;
                    case "text":
                        if (Current == State.InSession && !_viewOnly && m.TryGetProperty("text", out var tx))
                            TypeText(tx.GetString() ?? "");
                        break;
                    case "quality":
                        _quality = Math.Clamp(m.GetProperty("value").GetInt32(), 10, 90);
                        _forceFull = true;
                        break;
                    case "view-only":
                        _viewOnly = m.GetProperty("value").GetBoolean();
                        if (_viewOnly)
                            ReleaseAll();
                        break;
                }
                if (endReason.Length > 0)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            endReason = Tr.T("Сеанс завершён на кассе.", "Сеанс кассада аяктады.", "The session was ended on this till.", "Oturum bu kasada sonlandırıldı.",
                "Seans shu kassada tugatildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: соединение не удалось ({ex.Message}).", "WARNING");
            endReason = Tr.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ",
                "Destek sunucusuna bağlantı yok: ", "Qo'llab-quvvatlash serveri bilan aloqa yo'q: ") + ex.Message;
        }
        finally
        {
            ReleaseAll();
            var wasSession = Current == State.InSession;
            SetState(State.Ended);
            _ackSignal.Release();
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // соединение уже оборвано
                }
            }
            if (endReason.Length == 0)
                endReason = wasSession
                    ? Tr.T("Сеанс поддержки завершён.", "Колдоо сеансы аяктады.", "The support session has ended.", "Destek oturumu sona erdi.", "Qo'llab-quvvatlash seansi tugadi.")
                    : Tr.T("Соединение с сервером поддержки закрыто.", "Колдоо сервери менен байланыш жабылды.", "The connection to the support server was closed.",
                        "Destek sunucusuyla bağlantı kapandı.", "Qo'llab-quvvatlash serveri bilan aloqa yopildi.");
            PosLogger.Log($"Удалённая помощь: конец — {endReason}", "INFO");
            Ended?.Invoke(endReason);
        }
    }

    private static string EndText(string reason) => reason switch
    {
        "declined" => Tr.T("Вы отказали оператору в доступе.", "Сиз операторго уруксат берген жоксуз.", "You declined the operator's access.", "Operatörün erişimini reddettiniz.", "Siz operatorga ruxsat bermadingiz."),
        "consent-timeout" => Tr.T("Нет ответа на запрос оператора — доступ не дан.", "Оператордун суроосуна жооп болгон жок — уруксат берилген жок.", "No answer to the operator's request — access was not granted.", "Operatör isteğine yanıt verilmedi — erişim verilmedi.", "Operator so'roviga javob bo'lmadi — ruxsat berilmadi."),
        "operator" => Tr.T("Оператор завершил сеанс.", "Оператор сеансты аяктады.", "The operator ended the session.", "Operatör oturumu sonlandırdı.", "Operator seansni tugatdi."),
        "host" => Tr.T("Сеанс завершён на кассе.", "Сеанс кассада аяктады.", "The session was ended on this till.", "Oturum bu kasada sonlandırıldı.", "Seans shu kassada tugatildi."),
        "timeout" or "disconnect" => Tr.T("Связь с оператором прервалась.", "Оператор менен байланыш үзүлдү.", "The connection to the operator was lost.", "Operatörle bağlantı koptu.", "Operator bilan aloqa uzildi."),
        _ => Tr.T("Сеанс поддержки завершён.", "Колдоо сеансы аяктады.", "The support session has ended.", "Destek oturumu sona erdi.", "Qo'llab-quvvatlash seansi tugadi."),
    };

    private async Task AskConsentAsync(string op, CancellationToken ct)
    {
        SetState(State.AskingConsent);
        var accept = false;
        try
        {
            if (ConsentRequested is { } ask)
            {
                // Кассир думает не дольше 60 с (сервер сам отказывает через 90 с).
                var answer = ask(op);
                accept = await Task.WhenAny(answer, Task.Delay(60_000, ct)).ConfigureAwait(false) == answer && answer.Result;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        PosLogger.Log($"Удалённая помощь: оператор «{op}» — {(accept ? "разрешено" : "отказано")} кассиром.", "INFO");
        if (!accept)
            SetState(State.WaitingOperator);
        await SendJsonAsync(new { type = "consent", accept }, ct).ConfigureAwait(false);
    }

    /// <summary>Завершить сеанс или ожидание кнопкой кассира.</summary>
    public async Task StopAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await SendJsonAsync(new { type = "end", reason = "host" }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // уже закрыто
        }
        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ws.Dispose();
    }

    // ---------- поток экрана (отдельный поток: GDI и SendAsync синхронно) ----------

    private void StreamLoop(CancellationToken ct)
    {
        const int tile = 128;
        const int fps = 10;
        try
        {
            int w = _screenW, h = _screenH;
            SendJsonAsync(new { type = "screen", w, h, monitor = 0, monitors = 1 }, ct).GetAwaiter().GetResult();
            using var cur = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            byte[]? curBytes = null, prevBytes = null;
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            var lastCursor = new POINT { X = -1, Y = -1 };
            while (Current == State.InSession && !ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var tick = Stopwatch.StartNew();
                while (_frameNo - Interlocked.Read(ref _lastAck) >= 2 && Current == State.InSession && !ct.IsCancellationRequested)
                    _ackSignal.Wait(500, ct);
                if (Current != State.InSession)
                    break;
                using (var g = Graphics.FromImage(cur))
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                var data = cur.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride;
                try
                {
                    stride = data.Stride;
                    curBytes ??= new byte[stride * h];
                    prevBytes ??= new byte[stride * h];
                    Marshal.Copy(data.Scan0, curBytes, 0, stride * h);
                }
                finally
                {
                    cur.UnlockBits(data);
                }
                var full = _forceFull;
                _forceFull = false;
                var changed = new List<Rectangle>();
                for (var ty = 0; ty < h; ty += tile)
                for (var tx = 0; tx < w; tx += tile)
                {
                    var rect = new Rectangle(tx, ty, Math.Min(tile, w - tx), Math.Min(tile, h - ty));
                    if (full || TileChanged(curBytes, prevBytes, stride, rect))
                        changed.Add(rect);
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
                        SendAsync(Packet(1, frame, rect, ms.GetBuffer().AsSpan(0, (int)ms.Length)), WebSocketMessageType.Binary, ct).GetAwaiter().GetResult();
                    }
                    SendAsync(Packet(2, frame, Rectangle.Empty, ReadOnlySpan<byte>.Empty), WebSocketMessageType.Binary, ct).GetAwaiter().GetResult();
                }
                if (GetCursorPos(out var c) && (c.X != lastCursor.X || c.Y != lastCursor.Y))
                {
                    lastCursor = c;
                    SendJsonAsync(new { type = "cursor", x = c.X, y = c.Y }, ct).GetAwaiter().GetResult();
                }
                var left = interval - tick.Elapsed;
                if (left > TimeSpan.Zero)
                    Thread.Sleep(left);
            }
        }
        catch (OperationCanceledException)
        {
            // сеанс завершён
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: поток экрана остановлен ({ex.Message}).", "WARNING");
            _ = StopAsync();
        }
    }

    private static bool TileChanged(byte[] cur, byte[] prev, int stride, Rectangle r)
    {
        var len = r.Width * 3;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var off = y * stride + r.Left * 3;
            if (!cur.AsSpan(off, len).SequenceEqual(prev.AsSpan(off, len)))
                return true;
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

    private Task SendJsonAsync(object o, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text, ct);

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(data, type, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---------- ввод оператора ----------

    private void OnMouse(JsonElement m)
    {
        var action = m.TryGetProperty("action", out var a) ? a.GetString() : "";
        var x = m.TryGetProperty("x", out var xe) && xe.TryGetInt32(out var xv) ? xv : -1;
        var y = m.TryGetProperty("y", out var ye) && ye.TryGetInt32(out var yv) ? yv : -1;
        if (x < 0 || y < 0 || x >= _screenW || y >= _screenH)
            return;
        var nx = x * 65535 / Math.Max(1, _screenW - 1);
        var ny = y * 65535 / Math.Max(1, _screenH - 1);
        var button = m.TryGetProperty("button", out var b) && b.TryGetInt32(out var bv) ? bv : 0;
        uint flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        var data = 0;
        switch (action)
        {
            case "down":
                flags |= button switch { 2 => MOUSEEVENTF_RIGHTDOWN, 1 => MOUSEEVENTF_MIDDLEDOWN, _ => MOUSEEVENTF_LEFTDOWN };
                _buttonsDown.Add(button);
                break;
            case "up":
                flags |= button switch { 2 => MOUSEEVENTF_RIGHTUP, 1 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP };
                _buttonsDown.Remove(button);
                break;
            case "wheel":
                flags |= MOUSEEVENTF_WHEEL;
                data = m.TryGetProperty("delta", out var d) && d.TryGetInt32(out var dv) ? Math.Clamp(dv, -1200, 1200) : 0;
                break;
            case "move":
                break;
            default:
                return;
        }
        SendMouse(nx, ny, flags, data);
    }

    private static void SendMouse(int nx, int ny, uint flags, int data)
    {
        var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void OnKey(JsonElement m)
    {
        var action = m.TryGetProperty("action", out var a) ? a.GetString() : "";
        var code = m.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        if (Vk(code) is not { } vk)
        {
            // Неизвестная клавиша с печатным символом — как текст.
            if (action == "down" && m.TryGetProperty("key", out var k) && k.GetString() is { Length: 1 } ch)
                TypeText(ch);
            return;
        }
        var up = action == "up";
        if (up)
            _keysDown.Remove(vk.Code);
        else
            _keysDown.Add(vk.Code);
        SendKey(vk.Code, vk.Extended, up);
    }

    private static void SendKey(ushort vk, bool extended, bool up)
    {
        var flags = (up ? KEYEVENTF_KEYUP : 0u) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void TypeText(string text)
    {
        if (text.Length > 10_000)
            text = text[..10_000];
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                SendKey(0x0D, false, false);
                SendKey(0x0D, false, true);
                continue;
            }
            var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } };
            var upIn = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } };
            SendInput(2, new[] { down, upIn }, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>Конец сеанса или «только просмотр» — отпустить всё, что оператор держал нажатым.</summary>
    private void ReleaseAll()
    {
        try
        {
            foreach (var vk in _keysDown.ToList())
                SendKey(vk, false, true);
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToList())
                SendMouse(0, 0, b switch { 2 => MOUSEEVENTF_RIGHTUP, 1 => MOUSEEVENTF_MIDDLEUP, _ => MOUSEEVENTF_LEFTUP }, 0);
            _buttonsDown.Clear();
        }
        catch
        {
            // ввод недоступен — ничего не держим
        }
    }

    /// <summary>KeyboardEvent.code → виртуальная клавиша Windows.</summary>
    private static (ushort Code, bool Extended)? Vk(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key") && char.IsAsciiLetterUpper(code[3]))
            return ((ushort)code[3], false);
        if (code.Length == 6 && code.StartsWith("Digit") && char.IsAsciiDigit(code[5]))
            return ((ushort)code[5], false);
        if (code.StartsWith("Numpad") && code.Length == 7 && char.IsAsciiDigit(code[6]))
            return ((ushort)(0x60 + code[6] - '0'), false);
        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code[1..], out var fn) && fn is >= 1 and <= 12)
            return ((ushort)(0x6F + fn), false);
        return code switch
        {
            "Enter" => (0x0D, false), "NumpadEnter" => (0x0D, true), "Escape" => (0x1B, false), "Backspace" => (0x08, false), "Tab" => (0x09, false),
            "Space" => (0x20, false), "ArrowLeft" => (0x25, true), "ArrowUp" => (0x26, true), "ArrowRight" => (0x27, true), "ArrowDown" => (0x28, true),
            "Home" => (0x24, true), "End" => (0x23, true), "PageUp" => (0x21, true), "PageDown" => (0x22, true), "Insert" => (0x2D, true), "Delete" => (0x2E, true),
            "ShiftLeft" => (0xA0, false), "ShiftRight" => (0xA1, false), "ControlLeft" => (0xA2, false), "ControlRight" => (0xA3, true),
            "AltLeft" => (0xA4, false), "AltRight" => (0xA5, true), "MetaLeft" => (0x5B, true), "MetaRight" => (0x5C, true), "CapsLock" => (0x14, false),
            "Minus" => (0xBD, false), "Equal" => (0xBB, false), "BracketLeft" => (0xDB, false), "BracketRight" => (0xDD, false), "Backslash" => (0xDC, false),
            "Semicolon" => (0xBA, false), "Quote" => (0xDE, false), "Backquote" => (0xC0, false), "Comma" => (0xBC, false), "Period" => (0xBE, false),
            "Slash" => (0xBF, false), "NumpadAdd" => (0x6B, false), "NumpadSubtract" => (0x6D, false), "NumpadMultiply" => (0x6A, false),
            "NumpadDivide" => (0x6F, true), "NumpadDecimal" => (0x6E, false), "PrintScreen" => (0x2C, true), "ContextMenu" => (0x5D, true),
            _ => null,
        };
    }

    // ---------- Win32 ----------

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
#endif
