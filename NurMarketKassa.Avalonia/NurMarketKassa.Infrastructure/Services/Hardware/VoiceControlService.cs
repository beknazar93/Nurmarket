using System.Threading;
using NAudio.Wave;
using Vosk;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Офлайн-голосовое управление кассой (2026-09-04): кассир говорит ключевое слово
/// "касса" и команду ("касса картошка два кг", "касса убери последнюю позицию", "касса очисти
/// чек", "касса найди молоко", "касса повтори"), касса разбирает её в структурированную
/// VoiceCommand (см. IVoiceCommandParser) и разрешает товар по каталогу. Полностью офлайн — Vosk
/// (Apache 2.0) + маленькая русская модель (~90 МБ, VoiceModel\vosk-model-small-ru-0.22), без
/// облака и без API-ключей. Постоянное прослушивание вместо кнопки push-to-talk — по прямой
/// просьбе пользователя.
/// <para>Свободное распознавание (без ограничения словарём каталога) — первая версия строила
/// закрытую грамматику из точных слов "Title", но русский язык склоняется, и Vosk в режиме
/// закрытой грамматики требует буквального совпадения. Открытый словарь модели справляется со
/// словоформами сам; поиск товара по распознанному тексту — приблизительный, по общему началу
/// слова (VoiceCommandParser.FindProducts), с явной неоднозначностью вместо угадывания.</para>
/// <para>Опасные операции (удаление позиции, очистка чека) НЕ подтверждаются отдельным голосовым
/// "да"/"нет" — вместо этого команда прозрачно передаётся в уже существующие в кассе команды
/// (ClearCartCommand показывает свой Да/Нет-диалог, RemoveLineCommand требует пароль кассира),
/// это тот же самый защитный механизм, что и при ручном нажатии кнопки мышью.</para></summary>
public sealed class VoiceControlService : IVoiceControlService
{
    private const int SampleRate = 16000;

    private readonly IVoiceCommandParser _parser;
    private readonly IWeightScaleService? _weightScale;
    private readonly SpeakerVerificationService? _speakerVerification;
    private readonly object _lock = new();
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _waveIn;
    private bool _isListening;
    private string _status = "выключено";
    private VoiceCommandResult? _lastAddProductResult;

    // Голосовой замок (2026-09-05): накапливаем сырой звук ТЕКУЩЕЙ фразы параллельно с тем, как
    // он идёт в Vosk — на момент isFinal это именно аудио той фразы, что распозналась в text,
    // нужно для SpeakerVerificationService.Verify (Vosk отдаёт только текст, не исходный звук).
    private MemoryStream _utteranceAudio = new();

    public VoiceControlService(IWeightScaleService? weightScale = null, SpeakerVerificationService? speakerVerification = null)
    {
        _parser = new DefaultVoiceCommandParser();
        _weightScale = weightScale;
        _speakerVerification = speakerVerification;
    }

    public bool IsVoiceLockEnrolled => _speakerVerification?.IsEnrolled == true;

    public bool IsListening
    {
        get { lock (_lock) return _isListening; }
    }

    public string Status
    {
        get { lock (_lock) return _status; }
    }

    public event Action<VoiceCommandResult>? CommandRecognized;
    public event Action<string>? RawTextRecognized;

    public void Start()
    {
        lock (_lock)
        {
            StopInternal();

            var prefs = UserPreferences.Instance;
            if (!prefs.VoiceControlEnabled)
            {
                _status = "выключено в настройках";
                PosLogger.Log("Голосовое управление: выключено в настройках кассы.", "VOICE");
                return;
            }

            var modelPath = ResolveVoskModelPath();
            if (modelPath is null)
            {
                _status = "модель распознавания не найдена";
                PosLogger.Log("Голосовое управление: ни одна модель распознавания не установлена.", "VOICE");
                return;
            }

            try
            {
                // Иначе Vosk/Kaldi пишет подробный debug-лог в stderr на каждый кадр звука.
                Vosk.Vosk.SetLogLevel(-1);

                _model = new Model(modelPath);
                _recognizer = new VoskRecognizer(_model, SampleRate);
                _recognizer.SetWords(false);

                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 300,
                };
                _waveIn.DataAvailable += OnAudioData;
                _waveIn.StartRecording();

                _isListening = true;
                _status = $"слушает (ключевое слово «{VoiceCommandParser.WakeWord}»)";
                PosLogger.Log($"Голосовое управление: запущено, устройство записи «{_waveIn.WaveFormat}».", "VOICE");
            }
            catch (Exception ex)
            {
                _status = "ошибка запуска: " + ex.Message;
                PosLogger.Log($"Голосовое управление: не удалось запустить: {ex}", "VOICE");
                StopInternal();
            }
        }
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        VoskRecognizer? recognizer;
        byte[] utteranceAudio;
        lock (_lock)
        {
            recognizer = _recognizer;
            if (recognizer is null || e.BytesRecorded <= 0)
                return;

            // Копится ВСЕГДА, параллельно с Vosk — на момент isFinal ниже это ровно звук той
            // фразы, что распозналась (голосовой замок сверяет его с зарегистрированным голосом).
            _utteranceAudio.Write(e.Buffer, 0, e.BytesRecorded);
            utteranceAudio = [];
        }

        bool isFinal;
        try
        {
            isFinal = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!isFinal)
            return;

        string resultJson;
        lock (_lock)
        {
            if (_recognizer is null)
                return;
            resultJson = _recognizer.Result();

            utteranceAudio = _utteranceAudio.ToArray();
            _utteranceAudio.SetLength(0);
        }

        var text = VoiceCommandParser.ExtractRecognizedText(resultJson);
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();

        // Логируется всегда (даже без ключевого слова) — иначе при "не работает" совершенно
        // не видно, слышит ли касса вообще что-то осмысленное или просто тишину/шум/чужую речь.
        PosLogger.Log($"Голосовое управление: услышано «{text}».", "VOICE");
        RawTextRecognized?.Invoke(text);

        HandleRecognizedText(text, utteranceAudio);
    }

    private void HandleRecognizedText(string text, byte[] utteranceAudio)
    {
        if (!VoiceCommandParser.TryStripWakeWord(text, out var commandText))
            return;

        // Звуковой сигнал сразу по ключевому слову — кассиру не нужно смотреть на экран, чтобы
        // понять, что касса его услышала (и это же помогает диагностировать: если гудка нет,
        // значит ключевое слово не распозналось вообще, а не что-то дальше пошло не так).
        // Console.Beep — системный динамик, не требует звуковой карты/доп. пакетов (в отличие
        // от System.Media.SystemSounds, которому нужна отдельная сборка).
        try { Task.Run(() => Console.Beep(1000, 120)); }
        catch { /* нет системного динамика — не критично */ }

        if (commandText.Length == 0)
            return;

        var command = _parser.Parse(commandText);
        var products = CatalogCacheService.Products;

        VoiceCommandResult result;
        switch (command.Intent)
        {
            case VoiceIntent.AddProduct:
            {
                var candidates = VoiceCommandParser.FindProducts(command.ProductText, products);
                var quantity = command.Quantity;
                var resolved = candidates.Count == 1 ? candidates[0] : null;

                // Шаг "Scale Adapter" из сценария: для весового товара фактический вес с весов
                // приоритетнее произнесённого числа, если весы включены и на них что-то лежит.
                if (resolved is { MustWeigh: true } && _weightScale is { IsAvailable: true, LastWeight: > 0 })
                    quantity = _weightScale.LastWeight!.Value;

                result = new VoiceCommandResult
                {
                    Intent = VoiceIntent.AddProduct,
                    Product = resolved,
                    Candidates = candidates,
                    Quantity = quantity,
                    UnitKind = command.UnitKind,
                    ProductQuery = command.ProductText,
                    RawText = text,
                };
                if (resolved != null)
                    _lastAddProductResult = result;
                break;
            }

            case VoiceIntent.FindProduct:
            {
                var candidates = VoiceCommandParser.FindProducts(command.ProductText, products);
                result = new VoiceCommandResult
                {
                    Intent = VoiceIntent.FindProduct,
                    Candidates = candidates,
                    ProductQuery = command.ProductText,
                    RawText = text,
                };
                break;
            }

            case VoiceIntent.RepeatLast:
                result = _lastAddProductResult is { } last
                    ? last with { RawText = text }
                    : new VoiceCommandResult { Intent = VoiceIntent.Unknown, RawText = text };
                break;

            default:
                result = new VoiceCommandResult { Intent = command.Intent, RawText = text };
                break;
        }

        // Голосовой замок (2026-09-05) — применяется к ЛЮБОЙ распознанной команде, не только
        // AddProduct: "касса очисти чек" от постороннего голоса ничем не лучше "касса молоко".
        // Verify возвращает null, если замок выключен/голос не зарегистрирован — тогда
        // VoiceMatched остаётся null и подписчики CommandRecognized ничего не блокируют.
        if (result.Intent != VoiceIntent.Unknown && UserPreferences.Instance.VoiceLockEnabled)
        {
            var matched = _speakerVerification?.Verify(PcmBytesToFloat(utteranceAudio));
            result = result with { VoiceMatched = matched };
        }

        PosLogger.Log(
            $"Голосовое управление: «{text}» -> intent={result.Intent}"
            + (result.Product != null ? $", товар={result.Product.Title} x{result.Quantity}" : "")
            + (result.Product is null && result.Candidates.Count > 1 ? $", неоднозначно ({result.Candidates.Count} вариантов)" : "")
            + (result.Intent == VoiceIntent.AddProduct && result.Product is null && result.Candidates.Count == 0 ? ", товар не найден" : "")
            + (result.VoiceMatched == false ? ", ГОЛОС НЕ СОВПАЛ" : "")
            + ".",
            "VOICE");

        CommandRecognized?.Invoke(result);
    }

    public void Stop()
    {
        lock (_lock)
            StopInternal();
    }

    private void StopInternal()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnAudioData;
            try { _waveIn.StopRecording(); }
            catch { /* устройство уже могло исчезнуть — не критично при остановке */ }
            _waveIn.Dispose();
            _waveIn = null;
        }

        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;

        _isListening = false;
        _status = "выключено";
    }

    /// <summary>Предпочитаем модель под текущий язык интерфейса кассы — с откатом на вторую
    /// установленную модель, если предпочтительная не скачана (см.
    /// VoiceModelDownloadService.ResolvePreferredModelPath, 2026-09-04). Общий код для Start() и
    /// EnrollVoiceAsync — регистрации голоса нужен тот же движок распознавания речи не ради слов,
    /// а ради определения конца фразы (isFinal), т.е. та же модель, что и обычному прослушиванию.</summary>
    private static string? ResolveVoskModelPath()
    {
        var prefs = UserPreferences.Instance;
        var preferredLanguage = prefs.Language == AppLanguage.Kyrgyz ? VoiceModelLanguage.Kyrgyz : VoiceModelLanguage.Russian;
        return VoiceModelDownloadService.ResolvePreferredModelPath(preferredLanguage);
    }

    private static float[] PcmBytesToFloat(byte[] pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
        return samples;
    }

    public async Task<bool> EnrollVoiceAsync(int sampleCount, Action<int, int>? onSampleRecorded, CancellationToken ct = default)
    {
        PosLogger.Log(
            $"Голосовой замок: запрошена регистрация (speakerVerification={(_speakerVerification is null ? "null" : "ok")}, sampleCount={sampleCount}).",
            "VOICE_LOCK");

        if (_speakerVerification is null)
        {
            PosLogger.Log("Голосовой замок: SpeakerVerificationService не внедрён (DI) — регистрация невозможна.", "VOICE_LOCK");
            return false;
        }

        if (sampleCount <= 0)
            return false;

        if (!SpeakerVerificationModelService.IsInstalled())
        {
            PosLogger.Log("Голосовой замок: модель не установлена, регистрация невозможна.", "VOICE_LOCK");
            return false;
        }

        var modelPath = ResolveVoskModelPath();
        if (modelPath is null)
        {
            PosLogger.Log("Голосовой замок: нет установленной модели распознавания речи для записи фраз.", "VOICE_LOCK");
            return false;
        }

        bool wasListening;
        lock (_lock)
        {
            wasListening = _isListening;
            StopInternal();
        }

        // Отдельный WaveInEvent для записи фразы открывается сразу после остановки обычного
        // прослушивания — на некоторых звуковых драйверах Windows устройство записи освобождается
        // не мгновенно после Dispose(), и попытка тут же занять его снова падает с "устройство
        // уже занято" (2026-09-05, реальная жалоба пользователя: "регистрация не работает голоса",
        // без единой строки в логе — характерно именно для необработанного исключения из
        // WaveInEvent.StartRecording, которое раньше вообще ничем не перехватывалось внутри
        // RecordOneUtteranceAsync). Небольшая пауза — дешёвый и безопасный способ дать драйверу
        // время освободить устройство, а RecordOneUtteranceAsync теперь ещё и не падает молча,
        // если этого всё равно окажется недостаточно.
        await Task.Delay(250, ct).ConfigureAwait(false);

        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            using var model = new Model(modelPath);
            using var recognizer = new VoskRecognizer(model, SampleRate);
            recognizer.SetWords(false);

            var samples = new List<float[]>();
            for (var i = 0; i < sampleCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var audio = await RecordOneUtteranceAsync(recognizer, ct).ConfigureAwait(false);
                if (audio is not { Length: > 0 })
                {
                    PosLogger.Log($"Голосовой замок: запись {i + 1}/{sampleCount} не удалась (тишина/таймаут).", "VOICE_LOCK");
                    return false;
                }

                samples.Add(PcmBytesToFloat(audio));
                onSampleRecorded?.Invoke(i + 1, sampleCount);
            }

            if (!_speakerVerification.Enroll(samples))
                return false;

            UserPreferences.Instance.VoiceLockEnabled = true;
            UserPreferences.Instance.SaveToDisk();
            return true;
        }
        finally
        {
            if (wasListening)
                Start();
        }
    }

    public void ClearVoiceEnrollment()
    {
        _speakerVerification?.ClearEnrollment();
        UserPreferences.Instance.VoiceLockEnabled = false;
        UserPreferences.Instance.SaveToDisk();
    }

    /// <summary>Пишет один законченный кусок речи — тот же принцип, что и обычное прослушивание
    /// (VoskRecognizer сам решает, где пауза достаточно длинная, чтобы считать фразу законченной,
    /// AcceptWaveform → isFinal), но во ВРЕМЕННЫЙ WaveInEvent/буфер, не трогая поля класса — после
    /// регистрации обычное прослушивание (если было включено) поднимается заново с нуля через
    /// Start(). 10-секундный таймаут — на случай тишины (кассир не говорит) вместо зависания
    /// навсегда.</summary>
    private static Task<byte[]?> RecordOneUtteranceAsync(VoskRecognizer recognizer, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accumulated = new MemoryStream();
        var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 300 };
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        void OnData(object? sender, WaveInEventArgs e)
        {
            accumulated.Write(e.Buffer, 0, e.BytesRecorded);
            bool isFinal;
            try { isFinal = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded); }
            catch (ObjectDisposedException) { return; }

            if (isFinal)
            {
                try { recognizer.Result(); } catch { /* результат не нужен, только сброс состояния распознавателя */ }
                tcs.TrySetResult(accumulated.ToArray());
            }
        }

        timeoutCts.Token.Register(() => tcs.TrySetResult(null));

        waveIn.DataAvailable += OnData;
        try { Task.Run(() => Console.Beep(1200, 100)); }
        catch { /* нет системного динамика — не критично */ }

        try
        {
            waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            // Раньше исключение здесь ничем не перехватывалось и улетало наружу необработанным —
            // пользователь видел "регистрация не работает" без единой строки в логе (2026-09-05).
            // Самая вероятная причина — микрофон ещё не успел освободиться после остановки
            // обычного прослушивания (см. задержку перед вызовом в EnrollVoiceAsync).
            PosLogger.Log($"Голосовой замок: не удалось запустить запись микрофона: {ex}", "VOICE_LOCK");
            waveIn.DataAvailable -= OnData;
            waveIn.Dispose();
            timeoutCts.Dispose();
            return Task.FromResult<byte[]?>(null);
        }

        return tcs.Task.ContinueWith(t =>
        {
            waveIn.DataAvailable -= OnData;
            try { waveIn.StopRecording(); }
            catch { /* устройство уже могло исчезнуть */ }
            waveIn.Dispose();
            timeoutCts.Dispose();
            return t.Result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() => Stop();
}
