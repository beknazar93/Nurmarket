using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-10-05, владелец: «добавь в ИИ голосовой чат тоже». Микрофон и динамики для голосового чата ИИ-советника:
/// запись вопроса (WAV 16 кГц моно — его распознаёт Gemini, TelegramVoice.TranscribeAsync) и проигрывание ответа
/// (PCM из Gemini TTS, TelegramVoice.SynthesizePcmAsync). NAudio работает только в Windows — на Android и Linux
/// кнопки микрофона нет (IsSupported = false).
/// </summary>
public sealed class VoiceChatRecorder : IDisposable
{
    private static readonly WaveFormat Format = new(16000, 16, 1);

    /// <summary>Не дольше минуты — потом запись останавливается сама.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(60);

    private readonly MemoryStream _pcm = new();
    private WaveInEvent? _in;

    public static bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                return WaveInEvent.DeviceCount > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Записано сейчас (по объёму звука).</summary>
    public TimeSpan Length
    {
        get
        {
            lock (_pcm)
                return TimeSpan.FromSeconds(_pcm.Length / (double)Format.AverageBytesPerSecond);
        }
    }

    /// <summary>Сработает один раз, когда запись дошла до MaxLength (из потока NAudio).</summary>
    public event Action? MaxLengthReached;

    public void Start()
    {
        _in = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 100 };
        var raised = false;
        _in.DataAvailable += (_, e) =>
        {
            lock (_pcm)
                _pcm.Write(e.Buffer, 0, e.BytesRecorded);
            if (!raised && Length >= MaxLength)
            {
                raised = true;
                MaxLengthReached?.Invoke();
            }
        };
        _in.StartRecording();
    }

    /// <summary>Остановить и отдать запись файлом WAV (null — тишина короче 0,4 с).</summary>
    public byte[]? Stop()
    {
        try
        {
            _in?.StopRecording();
        }
        catch
        {
            // устройство могло пропасть — отдаём то, что успели записать
        }
        _in?.Dispose();
        _in = null;
        byte[] pcm;
        lock (_pcm)
            pcm = _pcm.ToArray();
        if (pcm.Length < Format.AverageBytesPerSecond * 0.4)
            return null;
        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wav), Format))
            writer.Write(pcm, 0, pcm.Length);
        return wav.ToArray();
    }

    public void Dispose()
    {
        _in?.Dispose();
        _in = null;
    }
}

/// <summary>Проигрывание ответа ИИ-советника. Новый ответ или «стоп» обрывает прежний.
/// 2026-10-05, владелец: «голос очень сильно тормозит» — ответ озвучивается кусками: первый кусок играет, пока
/// готовятся следующие (Enqueue ставит их в очередь за текущим).</summary>
public static class VoiceChatPlayer
{
    private static readonly object Sync = new();
    private static readonly Queue<(short[] Pcm, int Rate)> Pending = new();
    private static WaveOutEvent? _out;
    private static int _generation;

    public static bool IsPlaying => _out is { PlaybackState: PlaybackState.Playing };

    /// <summary>Номер текущего ответа: Stop() его меняет, и опоздавшие куски старого ответа уже не играют.</summary>
    public static int Generation
    {
        get
        {
            lock (Sync)
                return _generation;
        }
    }

    public static void Play(short[] pcm, int rate)
    {
        Stop();
        Enqueue(pcm, rate, Generation);
    }

    /// <summary>Поставить кусок в очередь ответа generation (если тот не остановлен) — играет сразу или следом.</summary>
    public static void Enqueue(short[] pcm, int rate, int generation)
    {
        if (!OperatingSystem.IsWindows() || pcm.Length == 0)
            return;
        lock (Sync)
        {
            if (generation != _generation)
                return;
            if (_out is not null)
            {
                Pending.Enqueue((pcm, rate));
                return;
            }
            StartLocked(pcm, rate);
        }
    }

    private static void StartLocked(short[] pcm, int rate)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var source = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(rate, 16, 1));
        var output = new WaveOutEvent();
        output.Init(source);
        output.PlaybackStopped += (_, _) =>
        {
            source.Dispose();
            output.Dispose();
            lock (Sync)
            {
                if (!ReferenceEquals(_out, output))
                    return;
                _out = null;
                if (Pending.Count > 0)
                {
                    var (next, nextRate) = Pending.Dequeue();
                    StartLocked(next, nextRate);
                }
            }
        };
        _out = output;
        output.Play();
    }

    public static void Stop()
    {
        WaveOutEvent? output;
        lock (Sync)
        {
            _generation++;
            Pending.Clear();
            output = _out;
            _out = null;
        }
        try
        {
            output?.Stop();
        }
        catch
        {
            // уже остановлено
        }
    }
}
