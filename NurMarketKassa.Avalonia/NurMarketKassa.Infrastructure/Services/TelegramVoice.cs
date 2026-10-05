using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-01, владелец: «у нас есть ИИ-обработка голосовых команд — ответ голосом тоже реализуй в боте».
///
/// Голосовое сообщение боту (OGG/Opus из Telegram) распознаётся Google Gemini — он принимает аудио
/// как есть и понимает русский и кыргызский, поэтому ни декодера, ни офлайн-модели не нужно. Ответ
/// озвучивается Gemini TTS (PCM 24 кГц) и перекодируется в OGG/Opus библиотекой Concentus (чистый
/// C#, без ffmpeg) — Telegram показывает его как обычное голосовое. Нужен тот же бесплатный ключ ИИ,
/// что и для разговора; без ключа голос не работает, бот отвечает текстом.
/// </summary>
public static class TelegramVoice
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    // 30.09: проверено на ключе владельца — обе модели TTS отвечают за 2,5–3,5 с.
    // 2026-10-05, владелец: «голос очень сильно тормозит». Замер на ключе владельца: короткая фраза — 2,6–4,3 с у всех
    // моделей, 176 знаков — 6,5–10 с (время растёт с длиной текста; потоковой отдачи звука нет). У бесплатного ключа малый
    // суточный лимит озвучки на КАЖДУЮ модель — при 429 теперь пробуем следующую, а не сдаёмся.
    private static readonly string[] TtsModels = { "gemini-2.5-flash-preview-tts", "gemini-3.1-flash-tts-preview", "gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts" };

    /// <summary>Модели, у которых кончился лимит (429): до этого времени их не спрашиваем — не тратим 0,2–0,5 с на отказ.</summary>
    private static readonly Dictionary<string, DateTime> TtsQuotaUntil = new();
    private static readonly string[] SttModels = { "gemini-3.5-flash-lite", "gemini-flash-lite-latest", "gemini-3.1-flash-lite", "gemini-flash-latest" };

    /// <summary>Сколько символов ответа озвучивать: длинные отчёты голосом слушать неудобно.</summary>
    private const int MaxSpokenChars = 450;

    public static bool IsAvailable => TelegramAiChat.IsConfigured;

    /// <summary>Текст голосового сообщения. null — не распознано (причина в журнале).</summary>
    public static Task<string?> TranscribeAsync(byte[] ogg, CancellationToken ct) => TranscribeAsync(ogg, "audio/ogg", ct);

    /// <summary>2026-10-05: то же для записи с микрофона компьютера (голосовой чат ИИ-советника — «audio/wav»).</summary>
    public static async Task<string?> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct)
    {
        var key = TelegramAiChat.AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = Convert.ToBase64String(audio) } },
                    new JsonObject { ["text"] = "Запиши дословно, что сказано в этом аудио (обычно по-русски или по-кыргызски). "
                                                + "Ответь только текстом речи, без пояснений и кавычек. Если речи нет — ответь пустой строкой." }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 400 },
        }.ToJsonString();

        foreach (var model in SttModels)
        {
            var (json, status) = await PostAsync(model, key!, body, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                var text = ReadText(json)?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            PosLogger.Log($"Голос в боте: распознавание {model} → HTTP {status}.", "TELEGRAM");
            if (status is 400 or 401 or 403 or 429)
                return null;
        }

        return null;
    }

    /// <summary>Голосовой ответ (OGG/Opus) для текста ответа бота. null — не получилось.</summary>
    public static async Task<byte[]?> SynthesizeAsync(string htmlOrText, CancellationToken ct)
    {
        var (pcm, rate) = await SynthesizePcmAsync(htmlOrText, "Подробности — в сообщении.", ct).ConfigureAwait(false);
        if (pcm == null)
            return null;
        try
        {
            return EncodeOggOpus(pcm, rate);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голос в боте: не перекодировано в OGG ({ex.Message}).", "TELEGRAM");
            return null;
        }
    }

    /// <summary>2026-10-05: озвучка как есть (PCM 16 бит моно) — для голосового чата ИИ-советника, который играет её
    /// сам. moreHint — фраза в конце, если длинный текст обрезан («Подробности — на экране.»).</summary>
    public static async Task<(short[]? Pcm, int Rate)> SynthesizePcmAsync(string htmlOrText, string moreHint, CancellationToken ct)
    {
        var key = TelegramAiChat.AiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, 0);

        var spoken = ToSpeech(htmlOrText, moreHint);
        if (spoken.Length == 0)
            return (null, 0);

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                // «Спокойно» растягивало речь: 43 знака звучали 5–6 с и дольше готовились. Обычный темп — короче и быстрее.
                ["parts"] = new JsonArray(new JsonObject { ["text"] = "Прочитай естественно, в обычном темпе разговора: " + spoken }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = "Kore" } },
                },
            },
        }.ToJsonString();

        foreach (var model in TtsModels)
        {
            lock (TtsQuotaUntil)
            {
                if (TtsQuotaUntil.TryGetValue(model, out var until) && DateTime.UtcNow < until)
                    continue;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (json, status) = await PostAsync(model, key!, body, ct).ConfigureAwait(false);
            if (status is < 200 or >= 300)
            {
                PosLogger.Log($"Голос в боте: озвучка {model} → HTTP {status} за {watch.ElapsedMilliseconds} мс.", "TELEGRAM");
                if (status == 429)
                {
                    lock (TtsQuotaUntil)
                        TtsQuotaUntil[model] = DateTime.UtcNow.AddMinutes(30);
                    continue;
                }
                if (status is 400 or 401 or 403)
                    return (null, 0);
                continue;
            }

            var (pcm, rate) = ReadAudio(json);
            if (pcm == null)
            {
                // 2026-10-05, владелец: «голос очень сильно тормозит» — модель ответила 200 без звука, и программа
                // молча шла к следующей модели. Теперь видно, почему (finishReason) и сколько это стоило.
                PosLogger.Log($"Голос: {model} ответила без звука за {watch.ElapsedMilliseconds} мс ({NoAudioReason(json)}), {spoken.Length} симв.", "TELEGRAM");
                continue;
            }
            PosLogger.Log($"Голос: {model} озвучила {spoken.Length} симв. за {watch.ElapsedMilliseconds} мс ({pcm.Length / (double)rate:0.#} с звука).", "TELEGRAM");
            return (pcm, rate);
        }

        return (null, 0);
    }

    /// <summary>Почему в ответе озвучки нет звука: finishReason, blockReason или начало ответа (без ключа — его там нет).</summary>
    private static string NoAudioReason(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br))
                return "blockReason=" + br.GetString();
            if (root.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0)
            {
                var first = c[0];
                var reason = first.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "?";
                var hasContent = first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out _);
                return $"finishReason={reason}, content={(hasContent ? "есть, но без audio" : "нет")}";
            }
            return "нет candidates: " + (json.Length > 160 ? json[..160] : json);
        }
        catch
        {
            return "ответ не JSON";
        }
    }

    /// <summary>Текст для озвучки: без разметки, ссылок и значков, не длиннее MaxSpokenChars (по предложению).</summary>
    private static string ToSpeech(string text, string moreHint)
    {
        var s = System.Net.WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        s = Regex.Replace(s, @"https?://\S+", "");
        s = Regex.Replace(s, @"[•✅🛒🙂/]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= MaxSpokenChars)
            return s;
        var cut = s.LastIndexOfAny(new[] { '.', '!', '?' }, MaxSpokenChars);
        return (cut > 100 ? s[..(cut + 1)] : s[..MaxSpokenChars]) + " " + moreHint;
    }

    private static async Task<(string Json, int Status)> PostAsync(string model, string key, string body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
            request.Headers.Add("x-goog-api-key", key);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PosLogger.Log($"Голос в боте: нет связи с Google ({ex.GetType().Name}).", "TELEGRAM");
            return ("", 0);
        }
    }

    private static string? ReadText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parts = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
            var sb = new StringBuilder();
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                    continue;
                if (p.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PCM 16 бит моно из ответа TTS: «audio/L16;rate=24000» — как есть, «audio/wav» — без заголовка.</summary>
    private static (short[]? Pcm, int Rate) ReadAudio(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray())
            {
                if (!p.TryGetProperty("inlineData", out var data) && !p.TryGetProperty("inline_data", out data))
                    continue;
                var mime = (data.TryGetProperty("mimeType", out var m) ? m.GetString() : data.TryGetProperty("mime_type", out var m2) ? m2.GetString() : "") ?? "";
                var bytes = Convert.FromBase64String(data.GetProperty("data").GetString() ?? "");
                var rate = 24000;
                var offset = 0;
                if (mime.Contains("wav", StringComparison.OrdinalIgnoreCase) && bytes.Length > 44)
                {
                    rate = BitConverter.ToInt32(bytes, 24);
                    // Ищем чанк «data» — заголовок WAV бывает длиннее 44 байт.
                    for (var i = 12; i + 8 <= bytes.Length;)
                    {
                        var id = Encoding.ASCII.GetString(bytes, i, 4);
                        var size = BitConverter.ToInt32(bytes, i + 4);
                        if (id == "data")
                        {
                            offset = i + 8;
                            break;
                        }

                        i += 8 + size;
                    }
                }
                else if (Regex.Match(mime, @"rate=(\d+)") is { Success: true } r)
                {
                    rate = int.Parse(r.Groups[1].Value);
                }

                var samples = new short[(bytes.Length - offset) / 2];
                Buffer.BlockCopy(bytes, offset, samples, 0, samples.Length * 2);
                return (samples, rate);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голос в боте: ответ озвучки не разобран ({ex.Message}).", "TELEGRAM");
        }

        return (null, 0);
    }

    /// <summary>PCM → OGG/Opus (голосовое Telegram). Opus принимает 8/12/16/24/48 кГц; Gemini даёт 24 кГц.</summary>
    private static byte[] EncodeOggOpus(short[] pcm, int rate)
    {
        if (rate is not (8000 or 12000 or 16000 or 24000 or 48000))
            rate = 24000;
        // Дополняем тишиной до целого кадра 20 мс — без неполного последнего кадра.
        var frame = rate / 50;
        var padded = new short[(pcm.Length + frame - 1) / frame * frame];
        Array.Copy(pcm, padded, pcm.Length);

        var encoder = OpusCodecFactory.CreateEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000;
        using var ms = new MemoryStream();
        var ogg = new OpusOggWriteStream(encoder, ms);
        ogg.WriteSamples(padded, 0, padded.Length);
        ogg.Finish();
        return DropEmptyEndPage(ms.ToArray());
    }

    /// <summary>Concentus.Oggfile дописывает в конце пустую страницу с пустым пакетом — ffmpeg на неё
    /// ругается («Invalid data»), строгий разбор Telegram тоже может. Убираем её, а флаг конца потока
    /// ставим на предыдущую страницу с пересчётом CRC (проверено ffmpeg и распознаванием Gemini 01.10).</summary>
    private static byte[] DropEmptyEndPage(byte[] ogg)
    {
        var pages = new List<(int Offset, int Length, int Payload)>();
        for (var i = 0; i + 27 <= ogg.Length && ogg[i] == 'O' && ogg[i + 1] == 'g' && ogg[i + 2] == 'g' && ogg[i + 3] == 'S';)
        {
            int segments = ogg[i + 26], payload = 0;
            for (var s = 0; s < segments; s++)
                payload += ogg[i + 27 + s];
            var length = 27 + segments + payload;
            pages.Add((i, length, payload));
            i += length;
        }

        if (pages.Count < 3 || pages[^1].Payload != 0)
            return ogg;

        var prev = pages[^2];
        var result = new byte[pages[^1].Offset];
        Array.Copy(ogg, result, result.Length);
        result[prev.Offset + 5] |= 4;
        for (var k = 22; k < 26; k++)
            result[prev.Offset + k] = 0;
        var crc = OggCrc(result, prev.Offset, prev.Length);
        BitConverter.GetBytes(crc).CopyTo(result, prev.Offset + 22);
        return result;
    }

    private static uint OggCrc(byte[] data, int offset, int length)
    {
        uint crc = 0;
        for (var i = offset; i < offset + length; i++)
        {
            crc ^= (uint)data[i] << 24;
            for (var b = 0; b < 8; b++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return crc;
    }
}
