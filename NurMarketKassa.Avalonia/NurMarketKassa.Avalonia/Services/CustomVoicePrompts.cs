using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Озвучка кассы своим голосом (2026-09-25, просьба владельца: «загрузить образец голоса,
/// чтобы программа говорила этим голосом, с его интонацией и тоном»). Каждую фразу подсказки
/// можно записать с микрофона или загрузить файлом (WAV, MP3, M4A…). Запись ложится в папку
/// переопределений VoicePromptPlayer под именем встроенной фразы, и касса играет её вместо
/// синтезированной. Голос и интонация — ровно те, что записаны: нейросеть не участвует,
/// поэтому работает и для кыргызского, и без интернета.
///
/// Перед сохранением тишина по краям обрезается, а тихая запись поднимается по громкости,
/// чтобы фраза звучала сразу и не тише встроенных.</summary>
public static class CustomVoicePrompts
{
    public sealed record Prompt(string Key, string RuText, string KyText)
    {
        public string Text(string lang) => lang == "ky" ? KyText : RuText;
    }

    /// <summary>Все фразы, которые касса произносит (см. VoicePromptPlayer).</summary>
    public static readonly IReadOnlyList<Prompt> All =
    [
        new("piece_or_pack", "Поштучно или целая пачка?", "Даанадан же бүтүн пачкадан?"),
        new("not_found", "Товар не найден.", "Товар табылган жок."),
        new("clarify", "Уточните товар.", "Товарды тактаңыз."),
        new("cart_empty", "Чек пуст.", "Чек бош."),
        new("voice_mismatch", "Голос не совпадает.", "Үн дал келбейт."),
    ];

    private const int SampleRate = 44100;
    private static readonly WaveFormat OutputFormat = new(SampleRate, 16, 1);
    private static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(15);

    public static string FileName(string key, string lang) => $"{key}_{lang}.wav";

    private static string PathFor(string key, string lang) =>
        Path.Combine(VoicePromptPlayer.OverrideDirectory, FileName(key, lang));

    public static bool HasCustom(string key, string lang) => File.Exists(PathFor(key, lang));

    /// <summary>Вернуть встроенную фразу.</summary>
    public static void Reset(string key, string lang)
    {
        var path = PathFor(key, lang);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Файл пользователя → WAV кассы (44,1 кГц, моно, 16 бит — такой SoundPlayer играет всегда).</summary>
    public static Task ImportAsync(string sourcePath, string key, string lang) => Task.Run(() =>
    {
        using var reader = new MediaFoundationReader(sourcePath);
        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var maxFloats = (int)(provider.WaveFormat.SampleRate * channels * MaxLength.TotalSeconds);

        var interleaved = new List<float>();
        var buffer = new float[provider.WaveFormat.SampleRate * channels];
        int read;
        while (interleaved.Count < maxFloats && (read = provider.Read(buffer, 0, buffer.Length)) > 0)
            interleaved.AddRange(buffer.Take(read));

        // Каналы сводятся в моно средним — стереофайл звучит так же, как был.
        var mono = new float[interleaved.Count / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }

        var resampled = Resample(mono, provider.WaveFormat.SampleRate);
        Save(Process(resampled), key, lang);
    });

    private static float[] Resample(float[] mono, int sourceRate)
    {
        if (sourceRate == SampleRate)
            return mono;

        var source = new FloatArraySampleProvider(mono, WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1));
        var resampler = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, SampleRate);
        var result = new List<float>(mono.Length * SampleRate / sourceRate + SampleRate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            result.AddRange(buffer.Take(read));
        return result.ToArray();
    }

    /// <summary>Обрезка тишины по краям и выравнивание громкости. Слишком тихая или пустая
    /// запись — ошибка с понятным текстом, а не молчащая подсказка на кассе.</summary>
    private static float[] Process(float[] samples)
    {
        if (samples.Length == 0)
            throw new InvalidDataException(Tr.T("Запись пустая.", "Жазуу бош.", "The recording is empty.", "Kayıt boş.", "Yozuv bo'sh."));

        var peak = samples.Max(Math.Abs);
        if (peak < 0.02f)
            throw new InvalidDataException(Tr.T(
                "Слишком тихо — голос почти не слышен. Говорите ближе к микрофону.",
                "Өтө акырын — үн дээрлик угулбайт. Микрофонго жакыныраак сүйлөңүз.",
                "Too quiet - the voice is barely audible. Speak closer to the microphone.",
                "Çok sessiz - ses neredeyse duyulmuyor. Mikrofona daha yakın konuşun.",
                "Juda past - ovoz deyarli eshitilmaydi. Mikrofonga yaqinroq gapiring."));

        var threshold = Math.Max(0.02f, peak * 0.06f);
        var first = Array.FindIndex(samples, s => Math.Abs(s) > threshold);
        var last = Array.FindLastIndex(samples, s => Math.Abs(s) > threshold);
        var start = Math.Max(0, first - SampleRate * 8 / 100);
        var end = Math.Min(samples.Length - 1, last + SampleRate * 20 / 100);
        var trimmed = samples[start..(end + 1)];

        if (trimmed.Length < SampleRate / 5)
            throw new InvalidDataException(Tr.T(
                "Слишком короткая запись — произнесите фразу целиком.",
                "Жазуу өтө кыска — сөз айкашын толук айтыңыз.",
                "The recording is too short - say the whole phrase.",
                "Kayıt çok kısa - cümlenin tamamını söyleyin.",
                "Yozuv juda qisqa - iborani to'liq ayting."));

        var gain = Math.Min(0.9f / peak, 4f);
        if (gain > 1.05f)
        {
            for (var i = 0; i < trimmed.Length; i++)
                trimmed[i] *= gain;
        }

        return trimmed;
    }

    private static void Save(float[] samples, string key, string lang)
    {
        Directory.CreateDirectory(VoicePromptPlayer.OverrideDirectory);
        var target = PathFor(key, lang);
        var temp = target + ".tmp";
        using (var writer = new WaveFileWriter(temp, OutputFormat))
            writer.WriteSamples(samples, 0, samples.Length);
        File.Move(temp, target, overwrite: true);
        PosLogger.Log($"Своя озвучка сохранена: {FileName(key, lang)} ({samples.Length / (double)SampleRate:0.0} с).", "VOICE_PROMPT");
    }

    /// <summary>Запись одной фразы с микрофона: Start — Stop (или сама остановится через 15 с).</summary>
    public sealed class Recorder : IDisposable
    {
        private readonly WaveInEvent _waveIn;
        private readonly MemoryStream _pcm = new();
        private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _error;

        public Recorder()
        {
            _waveIn = new WaveInEvent { WaveFormat = OutputFormat, BufferMilliseconds = 50 };
            _waveIn.DataAvailable += (_, e) =>
            {
                lock (_pcm)
                {
                    if (_pcm.Length < OutputFormat.AverageBytesPerSecond * MaxLength.TotalSeconds)
                        _pcm.Write(e.Buffer, 0, e.BytesRecorded);
                    else
                        _waveIn.StopRecording();
                }
            };
            _waveIn.RecordingStopped += (_, e) =>
            {
                _error = e.Exception;
                _stopped.TrySetResult(true);
            };
        }

        public void Start() => _waveIn.StartRecording();

        /// <summary>Останавливает запись и сохраняет фразу.</summary>
        public async Task StopAndSaveAsync(string key, string lang)
        {
            _waveIn.StopRecording();
            await _stopped.Task.ConfigureAwait(false);
            if (_error != null)
                throw _error;

            byte[] bytes;
            lock (_pcm)
                bytes = _pcm.ToArray();

            await Task.Run(() =>
            {
                var samples = new float[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                Save(Process(samples), key, lang);
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { _waveIn.StopRecording(); } catch { /* уже остановлена */ }
            _waveIn.Dispose();
            _pcm.Dispose();
        }
    }

    /// <summary>Готовый массив как источник звука — для пересчёта частоты.</summary>
    private sealed class FloatArraySampleProvider(float[] samples, WaveFormat format) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = format;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            if (n <= 0)
                return 0;
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
