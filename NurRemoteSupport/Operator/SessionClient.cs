using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using SkiaSharp;

namespace NurRemoteSupport.Operator;

/// <summary>Сеанс оператора с одной кассой (PROTOCOL.md v1.1): вход по коду или из очереди + PIN → ждём «Разрешить» кассира →
/// экран (куски JPEG собираются в один кадр <see cref="Frame"/>), ввод, чат, сведения, диагностика, журналы, файлы, буфер.
/// Сетевой цикл — в фоне; события приходят из фонового потока (окно само переходит в поток интерфейса).</summary>
internal sealed class SessionClient : IAsyncDisposable
{
    private readonly Uri _wsUri;
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly string? _token;

    public SessionClient(Uri wsUri, string? token = null)
    {
        _wsUri = wsUri;
        _token = token;
    }

    // ---------- события ----------
    public event Action<string, int?>? Error;                 // код ошибки, сколько попыток PIN осталось
    public event Action? WaitingConsent;
    public event Action<JsonElement>? Started;                // started (с host: компания, компьютер, версия, экран)
    public event Action<string>? Ended;                       // причина
    public event Action<int, int>? ScreenSize;
    public event Action<int, int, int, int, uint>? FrameReady; // грязная область x, y, w, h и номер кадра
    public event Action<int, int>? Cursor;
    public event Action<string>? Chat;
    public event Action<string>? Info;
    public event Action<string>? Logs;
    public event Action<List<(string Name, bool? Ok, string Detail)>>? Diag;
    public event Action<string>? Clipboard;
    public event Action<string, byte[]>? FileFromHost;         // имя, содержимое (журналы zip)
    public event Action<string, string, string>? FileStatus;   // id, "saved"/"error", имя или код ошибки
    public event Action<double, double>? Stats;                // кадров/с, кбит/с

    // ---------- кадр ----------
    public readonly object FrameLock = new();
    public byte[] Frame { get; private set; } = Array.Empty<byte>(); // BGRA, W×H×4
    public int W { get; private set; }
    public int H { get; private set; }

    private int _dirtyL = int.MaxValue, _dirtyT = int.MaxValue, _dirtyR, _dirtyB;
    private long _bytes;
    private int _frames;
    private readonly Stopwatch _statWatch = Stopwatch.StartNew();
    private readonly Dictionary<string, (string Name, long Size, MemoryStream Data)> _incoming = new();

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync()
    {
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(_token))
            _ws.Options.SetRequestHeader("Authorization", "Bearer " + _token); // 2026-10-19: токен заголовком, не в адресе
        await _ws.ConnectAsync(_wsUri, _cts.Token);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public Task JoinByCodeAsync(string code, string pin) => SendAsync(new { type = "join", code, pin });

    public Task JoinBySessionAsync(string sessionId, string pin) => SendAsync(new { type = "join", session = sessionId, pin });

    public Task SendAsync(object o) => SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(o));

    private async Task SendRawAsync(byte[] data)
    {
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(data, WebSocketMessageType.Text, true, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task EndAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await SendAsync(new { type = "end", reason = "operator" });
        }
        catch
        {
            // уже закрыто
        }
    }

    public async ValueTask DisposeAsync()
    {
        await EndAsync();
        _cts.Cancel();
        _ws.Dispose();
    }

    // ---------- файл на кассу ----------

    private const int FileChunk = 96 * 1024; // в base64 ≈ 128 КБ — меньше предела текста сервера (256 КБ)

    /// <summary>Файл кассиру (в «Загрузки\NurMarket поддержка»), по кускам; progress — доля 0..1.</summary>
    public async Task<string> SendFileAsync(string path, IProgress<double>? progress)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.LongLength > 100L * 1024 * 1024)
            throw new InvalidOperationException(L.T("Файл больше 100 МБ.", "Файл 100 МБдан чоң.", "The file is larger than 100 MB.", "Dosya 100 MB'tan büyük.", "Fayl 100 MB dan katta."));
        var id = Guid.NewGuid().ToString("N");
        await SendAsync(new { type = "file-begin", id, name = Path.GetFileName(path), size = bytes.LongLength });
        for (int off = 0, index = 0; off < bytes.Length; off += FileChunk, index++)
        {
            await SendAsync(new { type = "file-chunk", id, index, data = Convert.ToBase64String(bytes, off, Math.Min(FileChunk, bytes.Length - off)) });
            progress?.Report(Math.Min(1, (off + FileChunk) / (double)Math.Max(1, bytes.Length)));
        }
        await SendAsync(new { type = "file-end", id });
        return id;
    }

    // ---------- приём ----------

    private async Task ReceiveLoopAsync()
    {
        var reason = "disconnect";
        var buf = new byte[256 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                        break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                _bytes += ms.Length;
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    OnBinary(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                    continue;
                }
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
                if (type == "end")
                {
                    reason = m.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                    break;
                }
                OnText(type, m);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "operator";
        }
        catch (Exception ex)
        {
            reason = "error: " + ex.Message;
        }
        Ended?.Invoke(reason);
    }

    private void OnText(string? type, JsonElement m)
    {
        static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (type)
        {
            case "ping":
                _ = SendAsync(new { type = "pong" });
                break;
            case "error":
                Error?.Invoke(S(m, "code"), m.TryGetProperty("left", out var left) && left.TryGetInt32(out var l) ? l : null);
                break;
            case "waiting-consent":
                WaitingConsent?.Invoke();
                break;
            case "started":
                Started?.Invoke(m.Clone());
                break;
            case "screen":
                var w = m.GetProperty("w").GetInt32();
                var h = m.GetProperty("h").GetInt32();
                lock (FrameLock)
                {
                    W = w;
                    H = h;
                    Frame = new byte[w * h * 4];
                }
                ScreenSize?.Invoke(w, h);
                break;
            case "cursor":
                Cursor?.Invoke(m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32());
                break;
            case "chat":
                Chat?.Invoke(S(m, "text"));
                break;
            case "info":
                Info?.Invoke(S(m, "text"));
                break;
            case "logs":
                Logs?.Invoke(S(m, "text"));
                break;
            case "clipboard":
                Clipboard?.Invoke(S(m, "text"));
                break;
            case "diag":
                var items = new List<(string, bool?, string)>();
                if (m.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var i in arr.EnumerateArray())
                        items.Add((S(i, "name"), i.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False ? ok.GetBoolean() : null, S(i, "detail")));
                Diag?.Invoke(items);
                break;
            case "file-begin":
                var size = m.TryGetProperty("size", out var se) && se.TryGetInt64(out var sv) ? sv : 0;
                // 19.10 (проверка безопасности): не больше 4 приёмов сразу — касса не может открыть их без счёта.
                if (size is > 0 and <= 300L * 1024 * 1024 && _incoming.Count < 4)
                    _incoming[S(m, "id")] = (S(m, "name"), size, new MemoryStream());
                break;
            case "file-chunk":
                if (_incoming.TryGetValue(S(m, "id"), out var f))
                {
                    // 19.10: кусок сверх объявленного размера или не base64 — приём отменяется (память не растёт без предела).
                    byte[] chunk;
                    try
                    {
                        chunk = Convert.FromBase64String(S(m, "data"));
                    }
                    catch (FormatException)
                    {
                        _incoming.Remove(S(m, "id"));
                        f.Data.Dispose();
                        break;
                    }
                    if (f.Data.Length + chunk.Length > f.Size)
                    {
                        _incoming.Remove(S(m, "id"));
                        f.Data.Dispose();
                        break;
                    }
                    f.Data.Write(chunk);
                }
                break;
            case "file-end":
                if (_incoming.Remove(S(m, "id"), out var done))
                    FileFromHost?.Invoke(done.Name, done.Data.ToArray());
                break;
            case "file-saved":
                FileStatus?.Invoke(S(m, "id"), "saved", S(m, "name"));
                break;
            case "file-error":
                FileStatus?.Invoke(S(m, "id"), "error", S(m, "error"));
                break;
        }
    }

    /// <summary>Двоичный кусок экрана: вид 1 — JPEG в (x, y), вид 2 — конец кадра (окно рисует и отвечает ack).</summary>
    private void OnBinary(ReadOnlySpan<byte> p)
    {
        if (p.Length < 13)
            return;
        var kind = p[0];
        var frame = BinaryPrimitives.ReadUInt32LittleEndian(p[1..]);
        if (kind == 2)
        {
            _frames++;
            if (_statWatch.Elapsed.TotalSeconds >= 1)
            {
                Stats?.Invoke(_frames / _statWatch.Elapsed.TotalSeconds, _bytes * 8 / 1000.0 / _statWatch.Elapsed.TotalSeconds);
                _frames = 0;
                _bytes = 0;
                _statWatch.Restart();
            }
            int l, t, r, b;
            lock (FrameLock)
            {
                (l, t, r, b) = (_dirtyL, _dirtyT, _dirtyR, _dirtyB);
                (_dirtyL, _dirtyT, _dirtyR, _dirtyB) = (int.MaxValue, int.MaxValue, 0, 0);
            }
            if (r > l && b > t)
                FrameReady?.Invoke(l, t, r - l, b - t, frame);
            else
                _ = SendAsync(new { type = "ack", frame });
            return;
        }
        if (kind != 1)
            return;
        int x = BinaryPrimitives.ReadUInt16LittleEndian(p[5..]), y = BinaryPrimitives.ReadUInt16LittleEndian(p[7..]);
        using var tile = SKBitmap.Decode(p[13..]);
        if (tile is null)
            return;
        using var bgra = tile.ColorType == SKColorType.Bgra8888 ? null : tile.Copy(SKColorType.Bgra8888);
        var src = bgra ?? tile;
        var pixels = src.GetPixelSpan();
        lock (FrameLock)
        {
            if (W == 0 || H == 0 || x >= W || y >= H)
                return;
            var w = Math.Min(src.Width, W - x);
            var h = Math.Min(src.Height, H - y);
            for (var row = 0; row < h; row++)
                pixels.Slice(row * src.RowBytes, w * 4).CopyTo(Frame.AsSpan(((y + row) * W + x) * 4, w * 4));
            _dirtyL = Math.Min(_dirtyL, x);
            _dirtyT = Math.Min(_dirtyT, y);
            _dirtyR = Math.Max(_dirtyR, x + w);
            _dirtyB = Math.Max(_dirtyB, y + h);
        }
    }
}
