using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.Wave;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-10-05, владелец: «постоянное голосовое общение как ChatGPT можем?» → «Делать? да».
/// Живой разговор с ИИ-советником через Gemini Live (тот же бесплатный ключ ИИ): микрофон всё время слушает, Google сам
/// понимает, где владелец закончил фразу, и отвечает голосом сразу, потоком (замер 05.10 на ключе владельца: первый звук
/// через 0,6 с у gemini-3.1-flash-live-preview, 3,6 с у gemini-2.5-flash-native-audio-latest). Текст обеих сторон
/// приходит расшифровкой — советник пишет его в чат и в историю.
///
/// Эха нет: пока советник говорит (и ещё полсекунды после), звук с микрофона в Google не уходит — иначе ИИ услышал бы
/// сам себя из динамиков и перебивал сам себя. Перебить — кнопкой (<see cref="Interrupt"/>).
/// Google закрывает соединение примерно раз в 10 минут — разговор продолжается по ключу возобновления (sessionResumption).
/// NAudio — только Windows (как и обычный голосовой вопрос, VoiceChatRecorder).
/// </summary>
public sealed class GeminiLiveVoice
{
    public enum LiveState { Connecting, Listening, Speaking, Ended }

    private static readonly string[] Models = { "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest" };
    private static string? _workingModel;

    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=";

    private static readonly WaveFormat MicFormat = new(16000, 16, 1);
    private static readonly WaveFormat SpeakerFormat = new(24000, 16, 1);

    /// <summary>Сколько микрофон ещё молчит после конца ответа: хвост звука в динамиках и в буферах звуковой карты.</summary>
    private static readonly long EchoGuardTicks = TimeSpan.FromMilliseconds(500).Ticks;

    private readonly string _instruction;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private ClientWebSocket? _ws;
    private WaveInEvent? _mic;
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _speaker;
    private volatile bool _modelTurn;
    private volatile bool _dropAnswer;
    private long _micOpenAtTicks;
    private long _speakerBytes;
    private string? _resumeHandle;
    private Task? _run;
    private long _lastVoiceTicks;

    /// <summary>Кусок распознанной речи владельца (из фонового потока).</summary>
    public event Action<string>? UserText;

    /// <summary>Кусок текста ответа советника — то, что он сейчас говорит (из фонового потока).</summary>
    public event Action<string>? AiText;

    /// <summary>Советник договорил ответ (из фонового потока).</summary>
    public event Action? TurnDone;

    /// <summary>Разговор закончен: null — завершил владелец, иначе — понятная причина (из фонового потока).</summary>
    public event Action<string?>? Ended;

    public string? Model { get; private set; }

    /// <summary>Громкость микрофона 0…1 — для анимации круга «слушаю».</summary>
    public double MicLevel { get; private set; }

    public LiveState State { get; private set; } = LiveState.Connecting;

    /// <summary>Когда последний раз кто-то говорил — советник завершает разговор после долгой тишины.</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>false — без микрофона: звук подаётся через <see cref="FeedAudioAsync"/> (проверка без человека).</summary>
    public bool UseMicrophone { get; init; } = true;

    /// <summary>false — ответ не играет в динамики, только считается (проверка, чтобы не мешать владельцу).</summary>
    public bool UseSpeaker { get; init; } = true;

    /// <summary>Секунд звука ответа получено за разговор.</summary>
    public double ReceivedAudioSeconds => Interlocked.Read(ref _speakerBytes) / (double)SpeakerFormat.AverageBytesPerSecond;

    public static bool IsSupported => VoiceChatRecorder.IsSupported && TelegramAiChat.IsConfigured;

    public GeminiLiveVoice(string instruction)
    {
        _instruction = instruction;
    }

    public void Start() => _run = Task.Run(RunAsync);

    /// <summary>Перебить: ответ замолкает сразу, остаток этого ответа не играет, микрофон снова слушает.</summary>
    public void Interrupt()
    {
        if (!_modelTurn && (_speaker?.BufferedBytes ?? 0) == 0)
            return;
        _dropAnswer = _modelTurn;
        _modelTurn = false;
        _speaker?.ClearBuffer();
        Interlocked.Exchange(ref _micOpenAtTicks, DateTime.UtcNow.Ticks + EchoGuardTicks / 2);
        State = LiveState.Listening;
        PosLogger.Log("Живой разговор ИИ: владелец перебил ответ.", "INFO");
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested)
            return;
        _cts.Cancel();
        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", close.Token).ConfigureAwait(false);
            }
            catch
            {
                // соединение уже закрыто
            }
        }
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // не дождались — устройства всё равно освобождены в RunAsync
            }
        }
    }

    /// <summary>Подать звук (16 кГц, 16 бит, моно) вместо микрофона — кусками по 100 мс в реальном времени.</summary>
    public async Task FeedAudioAsync(byte[] pcm, CancellationToken ct)
    {
        const int chunk = 3200;
        for (var i = 0; i < pcm.Length; i += chunk)
        {
            var part = new byte[Math.Min(chunk, pcm.Length - i)];
            Buffer.BlockCopy(pcm, i, part, 0, part.Length);
            OnMicData(part, part.Length);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        string? reason = null;
        var sender = Task.Run(() => SendPumpAsync(ct));
        try
        {
            StartSpeaker();
            var reconnects = 0;
            while (!ct.IsCancellationRequested)
            {
                var (connected, closeReason) = await SessionAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    break;
                if (connected && _resumeHandle is not null && reconnects++ < 6)
                {
                    PosLogger.Log($"Живой разговор ИИ: Google закрыл соединение ({closeReason ?? "плановое"}) — продолжаю тот же разговор.", "INFO");
                    continue;
                }
                reason = closeReason ?? "связь с Google прервалась";
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            reason = Clean(ex.Message);
        }
        finally
        {
            _cts.Cancel();
            StopDevices();
            State = LiveState.Ended;
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch
            {
                // отправка остановлена вместе с разговором
            }
            PosLogger.Log($"Живой разговор ИИ: завершён{(reason is null ? " владельцем" : " — " + reason)}.", reason is null ? "INFO" : "WARNING");
            Ended?.Invoke(reason);
        }
    }

    /// <summary>Одно соединение. Connected — сеанс был открыт (тогда после обрыва можно продолжить).</summary>
    private async Task<(bool Connected, string? Reason)> SessionAsync(CancellationToken ct)
    {
        var key = TelegramAiChat.AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (false, "ключ ИИ не задан");
        var models = _workingModel is null ? Models : new[] { _workingModel }.Concat(Models.Where(m => m != _workingModel)).ToArray();
        string? lastReason = null;
        foreach (var model in models)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(Endpoint + key), connect.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return (false, "нет связи с Google (" + Clean(ex.Message) + ")");
            }
            _ws = ws;
            await SendJsonAsync(Setup(model), ct).ConfigureAwait(false);
            var (opened, reason) = await ReceiveAsync(ws, model, watch, ct).ConfigureAwait(false);
            _ws = null;
            if (opened || ct.IsCancellationRequested)
                return (opened, reason);
            // Модель недоступна этому ключу (или у неё кончился лимит) — пробуем следующую.
            PosLogger.Log($"Живой разговор ИИ: {model} не открылась ({reason}).", "WARNING");
            lastReason = reason;
            if (_resumeHandle is not null)
            {
                // Ключ возобновления привязан к модели — с другой моделью разговор начинается заново.
                _resumeHandle = null;
            }
        }
        return (false, lastReason);
    }

    private JsonObject Setup(string model)
    {
        var setup = new JsonObject
        {
            ["model"] = "models/" + model,
            ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("AUDIO") },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = _instruction }) },
            // Расшифровка речи обеих сторон — для чата и истории разговоров.
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
            // Без сжатия контекста звуковой разговор ограничен ~15 минутами.
            ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
            ["sessionResumption"] = _resumeHandle is null ? new JsonObject() : new JsonObject { ["handle"] = _resumeHandle },
        };
        return new JsonObject { ["setup"] = setup };
    }

    private async Task<(bool Opened, string? Reason)> ReceiveAsync(ClientWebSocket ws, string model, System.Diagnostics.Stopwatch watch, CancellationToken ct)
    {
        var opened = false;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        return (opened, Describe(ws.CloseStatus, ws.CloseStatusDescription));
                    message.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("setupComplete", out _))
                {
                    opened = true;
                    Model = model;
                    _workingModel = model;
                    PosLogger.Log($"Живой разговор ИИ: {model} открыт за {watch.ElapsedMilliseconds} мс (инструкция {_instruction.Length} симв.).", "INFO");
                    StartMicrophone();
                    State = LiveState.Listening;
                    LastActivityUtc = DateTime.UtcNow;
                    continue;
                }
                if (root.TryGetProperty("serverContent", out var content))
                    OnServerContent(content);
                if (root.TryGetProperty("sessionResumptionUpdate", out var resume)
                    && resume.TryGetProperty("resumable", out var resumable) && resumable.ValueKind == JsonValueKind.True
                    && resume.TryGetProperty("newHandle", out var handle) && handle.GetString() is { Length: > 0 } newHandle)
                    _resumeHandle = newHandle;
                if (root.TryGetProperty("goAway", out _))
                    PosLogger.Log("Живой разговор ИИ: Google скоро закроет соединение (goAway) — разговор продолжится.", "INFO");
            }
        }
        catch (OperationCanceledException)
        {
            return (opened, null);
        }
        catch (WebSocketException ex)
        {
            return (opened, Describe(ws.CloseStatus, ws.CloseStatusDescription ?? ex.Message));
        }
        return (opened, Describe(ws.CloseStatus, ws.CloseStatusDescription));
    }

    private void OnServerContent(JsonElement content)
    {
        if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
        {
            _speaker?.ClearBuffer();
            _modelTurn = false;
            _dropAnswer = false;
        }
        if (content.TryGetProperty("inputTranscription", out var input) && input.TryGetProperty("text", out var said)
            && said.GetString() is { Length: > 0 } saidText)
        {
            LastActivityUtc = DateTime.UtcNow;
            UserText?.Invoke(saidText);
        }
        if (!_dropAnswer)
        {
            if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inline) || inline.GetProperty("data").GetString() is not { Length: > 0 } data)
                        continue;
                    var pcm = Convert.FromBase64String(data);
                    var sinceVoice = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastVoiceTicks));
                    if (!_modelTurn && sinceVoice < TimeSpan.FromSeconds(30))
                        PosLogger.Log($"Живой разговор ИИ: первый звук ответа через {sinceVoice.TotalMilliseconds:0} мс после конца фразы.", "INFO");
                    _modelTurn = true;
                    State = LiveState.Speaking;
                    LastActivityUtc = DateTime.UtcNow;
                    Interlocked.Add(ref _speakerBytes, pcm.Length);
                    _speaker?.AddSamples(pcm, 0, pcm.Length);
                }
            }
            if (content.TryGetProperty("outputTranscription", out var output) && output.TryGetProperty("text", out var spoken)
                && spoken.GetString() is { Length: > 0 } spokenText)
                AiText?.Invoke(spokenText);
        }
        if (content.TryGetProperty("turnComplete", out var complete) && complete.ValueKind == JsonValueKind.True)
        {
            var dropped = _dropAnswer;
            _modelTurn = false;
            _dropAnswer = false;
            LastActivityUtc = DateTime.UtcNow;
            if (!dropped)
                TurnDone?.Invoke();
        }
    }

    private void StartSpeaker()
    {
        if (!UseSpeaker || !OperatingSystem.IsWindows())
            return;
        _speaker = new BufferedWaveProvider(SpeakerFormat)
        {
            BufferDuration = TimeSpan.FromMinutes(5),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _out = new WaveOutEvent { DesiredLatency = 200 };
        _out.Init(_speaker);
        _out.Play();
    }

    private void StartMicrophone()
    {
        if (!UseMicrophone || _mic is not null)
            return;
        var mic = new WaveInEvent { WaveFormat = MicFormat, BufferMilliseconds = 100 };
        mic.DataAvailable += (_, e) => OnMicData(e.Buffer, e.BytesRecorded);
        mic.StartRecording();
        _mic = mic;
    }

    /// <summary>Кусок звука с микрофона: пока советник говорит (и полсекунды после) — не отправляется.</summary>
    private void OnMicData(byte[] buffer, int count)
    {
        if (_cts.IsCancellationRequested || count <= 0)
            return;
        var now = DateTime.UtcNow.Ticks;
        var speaking = _modelTurn || (_speaker?.BufferedBytes ?? 0) > 0;
        if (speaking)
        {
            Interlocked.Exchange(ref _micOpenAtTicks, now + EchoGuardTicks);
            MicLevel = 0;
            State = LiveState.Speaking;
            return;
        }
        if (State == LiveState.Speaking)
            State = LiveState.Listening;
        if (now < Interlocked.Read(ref _micOpenAtTicks))
        {
            MicLevel = 0;
            return;
        }
        double sum = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var s = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768.0;
            sum += s * s;
        }
        MicLevel = Math.Min(1, Math.Sqrt(sum / Math.Max(1, count / 2)) * 6);
        if (MicLevel > 0.06)
            Interlocked.Exchange(ref _lastVoiceTicks, now);
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        _outgoing.Writer.TryWrite(copy);
    }

    private async Task SendPumpAsync(CancellationToken ct)
    {
        await foreach (var chunk in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (_ws is not { State: WebSocketState.Open } || Model is null)
                continue;
            var msg = new JsonObject
            {
                ["realtimeInput"] = new JsonObject
                {
                    ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(chunk), ["mimeType"] = "audio/pcm;rate=16000" },
                },
            };
            try
            {
                await SendJsonAsync(msg, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // соединение переоткрывается — этот кусок пропадает
            }
        }
    }

    /// <summary>2026-10-06, владелец: «дай возможность голосом менять информацию о товарах». Программа сообщает советнику
    /// в звонке текстом (что подготовлено или выполнено) — он коротко озвучивает это владельцу.</summary>
    public async Task SendTextAsync(string text)
    {
        if (_ws is not { State: WebSocketState.Open } || Model is null || string.IsNullOrWhiteSpace(text))
            return;
        var msg = new JsonObject
        {
            ["clientContent"] = new JsonObject
            {
                ["turns"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
                }),
                ["turnComplete"] = true,
            },
        };
        try
        {
            await SendJsonAsync(msg, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Не дошло — владелец всё равно видит карточку на экране.
        }
    }

    private async Task SendJsonAsync(JsonObject msg, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StopDevices()
    {
        try
        {
            _mic?.StopRecording();
        }
        catch
        {
            // микрофон мог пропасть
        }
        _mic?.Dispose();
        _mic = null;
        try
        {
            _out?.Stop();
        }
        catch
        {
            // уже остановлено
        }
        _out?.Dispose();
        _out = null;
        _speaker = null;
        MicLevel = 0;
    }

    private static string Describe(WebSocketCloseStatus? status, string? text)
    {
        var t = Clean(text ?? "");
        if (t.Contains("quota", StringComparison.OrdinalIgnoreCase) || t.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
            || t.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "закончился бесплатный лимит живого голоса Google — попробуйте позже";
        if (t.Contains("API key", StringComparison.OrdinalIgnoreCase) || t.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase))
            return "Google не принял ключ ИИ";
        if (t.Length == 0)
            return status is null or WebSocketCloseStatus.NormalClosure ? "Google закрыл соединение" : $"Google закрыл соединение ({status})";
        return "Google: " + (t.Length > 160 ? t[..160] : t);
    }

    /// <summary>Ключ ИИ стоит в адресе соединения — в сообщениях об ошибках его быть не должно.</summary>
    private static string Clean(string text)
    {
        var key = UserPreferences.Instance.TelegramAiKey;
        return string.IsNullOrEmpty(key) ? text : text.Replace(key, "***");
    }
}
