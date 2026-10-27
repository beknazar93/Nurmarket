using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NurRemoteSupport.RelayTests;

/// <summary>Сообщение, принятое тестовым клиентом.</summary>
public sealed record WsMsg(WebSocketMessageType Type, byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);
    public JsonElement Json => JsonDocument.Parse(Data).RootElement;
    public string? JsonType => Type == WebSocketMessageType.Text && Json.TryGetProperty("type", out var t) ? t.GetString() : null;
    public override string ToString() => Type == WebSocketMessageType.Text ? Text : $"<двоичное {Data.Length} байт>";
}

/// <summary>Тестовый WebSocket-клиент: фоновый приём, автоответ на ping, пауза чтения.</summary>
public sealed class TestWs : IAsyncDisposable
{
    public readonly ClientWebSocket Ws = new();
    private readonly Channel<WsMsg> _in = Channel.CreateUnbounded<WsMsg>();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public volatile bool AutoPong = true;
    public volatile bool Paused;
    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public long BytesReceived;

    public static async Task<TestWs> ConnectAsync(string url, int timeoutMs = 5000, Action<ClientWebSocketOptions>? configure = null)
    {
        var t = new TestWs();
        t.Ws.Options.KeepAliveInterval = TimeSpan.Zero;
        configure?.Invoke(t.Ws.Options); // 2026-10-27: свои заголовки (Origin, X-Forwarded-For) для проверок безопасности
        using var cts = new CancellationTokenSource(timeoutMs);
        await t.Ws.ConnectAsync(new Uri(url), cts.Token);
        _ = Task.Run(t.ReceiveLoop);
        return t;
    }

    public Task Closed => _closed.Task;

    private async Task ReceiveLoop()
    {
        var buf = new byte[256 * 1024];
        try
        {
            while (true)
            {
                while (Paused) await Task.Delay(20);
                using var ms = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await Ws.ReceiveAsync(buf.AsMemory(), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = Ws.CloseStatus;
                    try { await Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                    break;
                }
                var m = new WsMsg(r.MessageType, ms.ToArray());
                Interlocked.Add(ref BytesReceived, m.Data.Length);
                if (m.Type == WebSocketMessageType.Text && AutoPong && m.JsonType == "ping")
                {
                    _ = SendJsonAsync(new { type = "pong" });
                    continue;
                }
                _in.Writer.TryWrite(m);
            }
        }
        catch { /* оборвалось */ }
        finally
        {
            _in.Writer.TryComplete();
            _closed.TrySetResult();
        }
    }

    public async Task SendJsonAsync(object o) => await SendAsync(JsonSerializer.SerializeToUtf8Bytes(o), WebSocketMessageType.Text);

    public async Task SendRawTextAsync(string s) => await SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text);

    public async Task SendAsync(byte[] data, WebSocketMessageType type)
    {
        await _send.WaitAsync();
        try
        {
            if (Ws.State == WebSocketState.Open) await Ws.SendAsync(data, type, true, CancellationToken.None);
        }
        finally { _send.Release(); }
    }

    /// <summary>Следующее сообщение (или null, если соединение закрылось/время вышло).</summary>
    public async Task<WsMsg?> NextAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _in.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    /// <summary>Ждём сообщение нужного типа; skip — пропускать прочие текстовые (cursor, stats…).</summary>
    public async Task<JsonElement> ExpectAsync(string type, int timeoutMs = 5000, bool skip = false)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(until - Environment.TickCount64);
            if (left <= 0) throw new Exception($"не дождались «{type}»");
            var m = await NextAsync(left) ?? throw new Exception($"не дождались «{type}» (соединение закрыто: {CloseStatus})");
            if (m.Type == WebSocketMessageType.Text && m.JsonType == type) return m.Json;
            if (!skip) throw new Exception($"ждали «{type}», пришло {Short(m)}");
        }
    }

    public async Task<bool> WaitClosedAsync(int timeoutMs = 5000) =>
        await Task.WhenAny(_closed.Task, Task.Delay(timeoutMs)) == _closed.Task;

    public static string Short(WsMsg m) => m.ToString() is { Length: > 200 } s ? s[..200] + "…" : m.ToString();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(1000);
                await Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch { }
        Ws.Abort();
        Ws.Dispose();
    }
}

/// <summary>Локальный сервер: dotnet Server.dll на 127.0.0.1:свободный порт с временной папкой данных.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    public int Port { get; }
    public string DataDir { get; }
    public Process Process { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string WsUrl => $"ws://127.0.0.1:{Port}";
    public readonly StringBuilder Log = new();
    public readonly HttpClient Http;

    private ServerProcess(int port, string dataDir, Process p)
    {
        Port = port;
        DataDir = dataDir;
        Process = p;
        Http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

    public static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Server", "Server.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new Exception("Не нашёл папку NurRemoteSupport (с Server/Server.csproj).");
    }

    public static string ServerDll(string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath)) return overridePath;
        var p = Path.Combine(FindRoot(), "Server", "bin", "Release", "net8.0", "Server.dll");
        if (!File.Exists(p)) throw new Exception($"Нет {p}. Сначала: dotnet build -c Release (в папке Server).");
        return p;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<ServerProcess> StartAsync(string dll, Dictionary<string, string> relaySettings, bool quiet = false)
    {
        var port = FreePort();
        var data = Path.Combine(Path.GetTempPath(), "nrs-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["Relay__DataDir"] = data;
        psi.Environment["DOTNET_gcServer"] = "0";
        if (quiet) psi.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (k, v) in relaySettings) psi.Environment["Relay__" + k] = v;
        var p = Process.Start(psi)!;
        var sp = new ServerProcess(port, data, p);
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sp.Log) sp.Log.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sp.Log) sp.Log.AppendLine("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var r = await sp.Http.GetStringAsync("/health");
                if (r == "ok") return sp;
            }
            catch { }
            if (p.HasExited) throw new Exception("Сервер не запустился:\n" + sp.Log);
            await Task.Delay(100);
        }
        throw new Exception("Сервер не ответил на /health:\n" + sp.Log);
    }

    /// <summary>Команда обслуживания (add-operator и т.п.) с тем же окружением.</summary>
    public async Task<(int code, string output)> CliAsync(string dll, string args, string? stdin)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["Relay__DataDir"] = DataDir;
        var p = Process.Start(psi)!;
        if (stdin is not null) await p.StandardInput.WriteLineAsync(stdin);
        p.StandardInput.Close();
        var outp = await p.StandardOutput.ReadToEndAsync();
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, outp + err);
    }

    public async Task<string> LoginAsync(string login, string password)
    {
        var r = await Http.PostAsJsonAsync("/api/login", new { login, password });
        if (!r.IsSuccessStatusCode) throw new Exception($"вход {login}: {(int)r.StatusCode}");
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return j.GetProperty("token").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
        try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        Http.Dispose();
        try { Directory.Delete(DataDir, true); } catch { }
    }
}
