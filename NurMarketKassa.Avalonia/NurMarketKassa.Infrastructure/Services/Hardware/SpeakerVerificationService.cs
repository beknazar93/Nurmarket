using System.Text.Json;
using SherpaOnnx;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// "Голосовой замок" (2026-09-05, по запросу пользователя: "добавь регистрацию голоса! и этот
/// голос только он может управлять") — проверка, что голосовую команду произнёс тот же человек,
/// что записал (enrollment) свой голос, а не просто распознавание СЛОВ (этим занимается Vosk в
/// VoiceControlService). Один голос на кассу (по решению пользователя) — не профиль на каждого
/// кассира. При несовпадении команда НЕ выполняется, но и ничего не блокируется навсегда — это
/// мягкое предупреждение, а не пароль (тоже по явному решению пользователя): простуда, шум или
/// другой микрофон не должны запирать легитимного кассира без возможности перезаписать голос.
///
/// Технически — sherpa-onnx (Apache 2.0, k2-fsa) + WeSpeaker ResNet34-LM (голос обучен на
/// VoxCeleb, ~26 МБ, см. SpeakerVerificationModelService), а не самодельная реализация MFCC/FFT —
/// вся обработка признаков уже реализована и протестирована внутри библиотеки под конкретно эту
/// архитектуру модели.
///
/// ЧЕСТНО О ТОЧНОСТИ: порог по умолчанию (0.6) — это значение из официального примера sherpa-onnx,
/// а не откалиброванное под реальных кассиров число (для этого нужны живые записи разных голосов,
/// которых на момент разработки не было). Итог заведомо не идеален — считать это разумным щитом
/// от случайного использования кассы посторонним, а не криптографической защитой.</summary>
public sealed class SpeakerVerificationService : IDisposable
{
    private const string SpeakerName = "cashier";
    private const float DefaultThreshold = 0.6f;
    private const int SampleRate = 16000;

    private static string VoiceLockDir => Path.Combine(AppContext.BaseDirectory, "VoiceLockModel");
    private static string VoiceprintPath => Path.Combine(VoiceLockDir, "voiceprint.json");

    private readonly object _lock = new();
    private SpeakerEmbeddingExtractor? _extractor;
    private SpeakerEmbeddingManager? _manager;

    public bool IsModelAvailable => SpeakerVerificationModelService.IsInstalled();

    public bool IsEnrolled
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _manager?.Contains(SpeakerName) == true;
            }
        }
    }

    /// <summary>Перепроверяет IsModelAvailable КАЖДЫЙ раз, а не только один раз навсегда —
    /// раньше был флаг "_loadAttempted", выставлявшийся один раз и никогда не сбрасывавшийся:
    /// если модель ещё не скачана в момент первого обращения (например, RefreshVoiceLockUi при
    /// открытии окна ДО того, как кассир нажал "Скачать"), _extractor/_manager так и оставались
    /// null НАВСЕГДА, даже после успешного скачивания — регистрация голоса потом тихо проваливалась
    /// без единой строки в логе (2026-09-05, воспроизведено и подтверждено по логам). Теперь
    /// "уже загружено" проверяется по _extractor != null, а не по факту одной попытки.</summary>
    private void EnsureLoaded()
    {
        if (_extractor != null)
            return;

        if (!IsModelAvailable)
            return;

        try
        {
            var config = new SpeakerEmbeddingExtractorConfig
            {
                Model = SpeakerVerificationModelService.ModelPath,
                NumThreads = 1,
                Debug = 0,
                Provider = "cpu",
            };
            _extractor = new SpeakerEmbeddingExtractor(config);
            _manager = new SpeakerEmbeddingManager(_extractor.Dim);

            if (TryLoadVoiceprint(out var embeddings))
                _manager.Add(SpeakerName, embeddings);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовой замок: не удалось загрузить модель: {ex}", "VOICE_LOCK");
            _extractor?.Dispose();
            _extractor = null;
            _manager?.Dispose();
            _manager = null;
        }
    }

    private float[] ComputeEmbedding(float[] samples)
    {
        using var stream = _extractor!.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    /// <summary>Регистрация голоса — несколько коротких записей (обычно 3, см.
    /// IVoiceControlService.EnrollVoiceAsync) усредняются самой библиотекой при сравнении, здесь
    /// только считаем эмбеддинг каждой и сохраняем ВСЕ (не одно усреднённое значение) — так
    /// делает и официальный пример sherpa-onnx, эмбеддинги короткой речи слишком грубые, чтобы
    /// смешивать их в один вектор до сравнения.</summary>
    public bool Enroll(IReadOnlyList<float[]> sampleAudios)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_extractor == null || _manager == null || sampleAudios.Count == 0)
            {
                PosLogger.Log(
                    $"Голосовой замок: регистрация отменена — extractor={(_extractor == null ? "null" : "ok")}, " +
                    $"manager={(_manager == null ? "null" : "ok")}, samples={sampleAudios.Count}.",
                    "VOICE_LOCK");
                return false;
            }

            var embeddings = sampleAudios.Select(ComputeEmbedding).ToList();
            _manager.Remove(SpeakerName);
            if (!_manager.Add(SpeakerName, embeddings))
            {
                PosLogger.Log("Голосовой замок: SpeakerEmbeddingManager.Add вернул false.", "VOICE_LOCK");
                return false;
            }

            SaveVoiceprint(embeddings);
            PosLogger.Log($"Голосовой замок: голос зарегистрирован ({embeddings.Count} записей).", "VOICE_LOCK");
            return true;
        }
    }

    public void ClearEnrollment()
    {
        lock (_lock)
        {
            EnsureLoaded();
            _manager?.Remove(SpeakerName);
            try
            {
                if (File.Exists(VoiceprintPath))
                    File.Delete(VoiceprintPath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Голосовой замок: не удалось удалить voiceprint: {ex.GetType().Name}", "WARNING");
            }

            PosLogger.Log("Голосовой замок: регистрация голоса сброшена.", "VOICE_LOCK");
        }
    }

    /// <summary>null — проверка невозможна (модель не скачана или голос ещё не зарегистрирован) —
    /// вызывающая сторона в этом случае НЕ должна ничего блокировать/предупреждать, это не
    /// "чужой голос", а "замок ещё не настроен". true/false — реальный результат сравнения.</summary>
    public bool? Verify(float[] sampleAudio)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (_extractor == null || _manager == null || !_manager.Contains(SpeakerName))
                return null;

            try
            {
                var embedding = ComputeEmbedding(sampleAudio);
                return _manager.Verify(SpeakerName, embedding, DefaultThreshold);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Голосовой замок: ошибка при проверке голоса: {ex.GetType().Name}", "WARNING");
                return null;
            }
        }
    }

    private static bool TryLoadVoiceprint(out List<float[]> embeddings)
    {
        embeddings = [];
        try
        {
            if (!File.Exists(VoiceprintPath))
                return false;

            var json = File.ReadAllText(VoiceprintPath);
            var arr = JsonSerializer.Deserialize<float[][]>(json);
            if (arr is not { Length: > 0 })
                return false;

            embeddings.AddRange(arr);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовой замок: не удалось прочитать voiceprint: {ex.GetType().Name}", "WARNING");
            return false;
        }
    }

    private static void SaveVoiceprint(List<float[]> embeddings)
    {
        try
        {
            Directory.CreateDirectory(VoiceLockDir);
            var json = JsonSerializer.Serialize(embeddings);
            File.WriteAllText(VoiceprintPath, json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовой замок: не удалось сохранить voiceprint: {ex.GetType().Name}", "ERROR");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _manager?.Dispose();
            _manager = null;
            _extractor?.Dispose();
            _extractor = null;
        }
    }
}
