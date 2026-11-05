# ТЗ — ИИ-звонок, видео и голосовой ввод на Android (03.11.2026)

**Для кого:** исполнитель (Sonnet, high). Автор — архитектор (Opus), 03.11.2026.
**Просьбы владельца:** «добавь ИИ-звонок и голосовой ввод в Android»; раньше: «на планшетах, если есть камера, то и ИИ-вызов видео тоже должна быть».
**Когда делать:** после выпуска 1.17.73 (идёт подготовка выпуска — до команды владельца исходники не трогать). Отдельной веткой работ, отдельным коммитом по просьбе владельца.

Правила (CLAUDE.md): `dotnet test` нельзя; секреты (ключи Gemini, `token.txt`, пароль ключа подписи) не печатать и не логировать; все новые надписи — `Tr.T(ru, ky, en, tr, uz)`; комментарии по-русски с датой и причиной (`// 2026-11-xx: …`); минимальный дифф в стиле кода; новых NuGet-зависимостей нет (см. раздел 9); после правок — `graphify update NurMarketKassa.Avalonia` из корня репозитория.

---

## 0. Коротко: главные решения

1. **Звук — через тонкий платформенный слой** `PlatformAudio` (Infrastructure, без Android-типов). На Windows фабрики пустые — `GeminiLiveVoice`, `VoiceChatRecorder`, `VoiceChatPlayer` идут **по старым строкам NAudio без изменений**. Новая ветка срабатывает только при `!OperatingSystem.IsWindows()` и установленной фабрике (её ставит только Android). Linux/Portable — фабрик нет → «недоступно», как сейчас.
2. **Android-звук — свой код на `AudioRecord`/`AudioTrack`** из Mono.Android (без зависимостей), по образцу уже работающего `NurMarketMobile/android/.../VoiceCallAudio.kt`: источник `VoiceCommunication` + `AcousticEchoCanceler`/`NoiseSuppressor` для звонка, `VoiceRecognition` для голосового вопроса, режим связи `MODE_IN_COMMUNICATION`, аудиофокус, громкая связь/гарнитура.
3. **Эхозащита звонка остаётся как на Windows** (микрофон молчит, пока советник говорит, +0,5 с; перебивание — кнопкой). Перебивание голосом — не в этой работе.
4. **Видео — тот же сеанс Gemini Live**: JPEG ≈640×480, ~1 кадр/с (3 с, пока ИИ говорит), сообщение `realtimeInput.video {data, mimeType:"image/jpeg"}`, `generationConfig.mediaResolution = MEDIA_RESOLUTION_LOW`, порядок моделей для видео — `gemini-2.5-flash-native-audio-latest` первой (как в мобилке, лимит токенов/мин). Модель отвергла видео — звонок продолжается голосом.
5. **Камера звонка — Camera1 без экрана Android** (offscreen `SurfaceTexture`, свой `HandlerThread`, `PreviewCallbackWithBuffer` — копируем только нужные кадры). Предпросмотр рисуется в окне советника (Avalonia `Image`), кадр на диск не пишется. Камера отпускается в `OnPause` активности — сканер штрихкодов (`ScannerActivity`) и системная камера получают её без конфликта.
6. **Права**: `RECORD_AUDIO` + `MODIFY_AUDIO_SETTINGS` в манифест; запрос при первом нажатии (по образцу CAMERA в `AndroidCameraScanner`), общий помощник `AndroidPermissions`; отказ навсегда — карточка с «Открыть настройки».
7. **Платный доступ не меняется**: `TelegramAiChat.IsConfigured` (ключ + `TariffGate.CanUseAi`), тумблеры `voice.ask`/`voice.live`, права `CurrentAccess()`, «Действия ИИ» только через карточку «Выполнить». Видео — дополнительный тумблер `voice.video` (по умолчанию включён; решение Р-1).
8. **Android-ограничения ради батареи/трафика**: звонок — мягкий лимит 20 мин (предупреждение за минуту, «Продлить на 10 минут»), видео — 10 мин (потом камера гаснет, голос продолжается); свёрнутая программа — микрофон молчит, через 60 с звонок завершается; потеря аудиофокуса (входящий звонок) — конец звонка. На Windows ничего из этого не включается.

---

## 1. Что есть сейчас (прочитано 03.11.2026)

| Что | Где | Суть |
|---|---|---|
| Живой звонок | `NurMarketKassa.Infrastructure/Services/Hardware/GeminiLiveVoice.cs` | WebSocket Gemini Live; NAudio: `WaveInEvent` (стр. 46, 494–497), `WaveOutEvent` + `BufferedWaveProvider` (47–48, 475–488); `OperatingSystem.IsWindows()` в `StartSpeaker` (477); эхозащита по `_speaker.BufferedBytes` (107, 506); `ClearBuffer` (111, 431); `AddSamples` (457); `StopDevices` (609–633); `IsSupported => VoiceChatRecorder.IsSupported && TelegramAiChat.IsConfigured` (95). Микрофон включается в фоновом потоке на `setupComplete` (399). Исключение в `StartMicrophone` уходит в `RunAsync` → `Ended(reason)`. |
| Голосовой вопрос/озвучка | `…/Hardware/VoiceChatAudio.cs` | `VoiceChatRecorder` (WAV 16 кГц через `WaveFileWriter`, `IsSupported` только Windows, стр. 23–38), `VoiceChatPlayer` (статический, очередь по `Generation`, `Enqueue` выходит на не-Windows, стр. 132). |
| Окно советника | `NurMarketKassa.Avalonia/Views.axaml/Main/AiAdvisorWindow.cs` (5257 стр.) | Кнопки 🎤 и «Разговор» создаются только при `VoiceChatRecorder.IsSupported` (стр. 320–350); «Озвучить» под ответом — тоже (1634); `Closed` → `EndLive`, `_recorder.Dispose`, `VoiceChatPlayer.Stop` (469–477); панель звонка `BuildLiveBar` (3713–3758); `StartLiveAsync` (3761–3882); `TickLive` 60 мс, 3 мин тишины — конец (3885–3927); `EndLive` (4041–4060); `ToggleVoiceAsync` (4084–4157); `SpeakAsync` (4161–4190). Строка ввода — `Grid "*,Auto,Auto,Auto,Auto"`: поле, скрепка, 🎤, «Разговор», ➤ (274). Узкий экран — `NarrowLayout.Attach(this, 760, …)` (420, только Android). |
| Тумблеры | `…/AiAdvisorWindow.Capabilities.cs` (27–49), `Infrastructure/Services/AdvisorCapabilities.cs` (192–197: `voice.ask`, `voice.live`) | Видимость `_mic`/`_talk` по тумблерам. |
| Где открывается советник | `OwnerShellWindow.axaml.cs:680–681` | Только программа владельца, раздел; при переходе в другой раздел окно **прячется** (`Hide`, стр. 1012), а не закрывается; `TrimHiddenSections` может закрыть. Звонок на Windows при этом продолжается (ИИ сам открывает разделы инструментом `open_section`). |
| Другие места с микрофоном | Grep `VoiceChatRecorder|VoiceChatPlayer|GeminiLiveVoice|WaveIn|WaveOut` | **Только окно советника.** `TelegramVoice.TranscribeAsync` в `TelegramBotPollingService` — голосовые Telegram, микрофон не нужен. Vosk удалён 30.10. Значит п. (3) задачи = 🎤 и «Озвучить» в окне советника. |
| Android | `NurMarketKassa.Android/` | `AndroidBootstrap.BeforeAvalonia` — регистрация платформенных замен (стр. 98–117); `MainActivity.OnRequestPermissionsResult` → `AndroidCameraScanner.OnPermissionResult` (193–198); `OnStart/OnStop` (152–171); манифест `Properties/AndroidManifest.xml` — `CAMERA` есть, `RECORD_AUDIO` нет; `KeepAliveService` — `TypeSpecialUse` (не даёт права на микрофон/камеру в фоне). Сканер — отдельная `ScannerActivity` (Camera1, `FindBackCamera`, `DisplayOrientation`). Запасной выключатель через пустой файл в папке данных — образец `android-no-nested-loop` (AndroidBootstrap 122–133). |
| NAudio | `Infrastructure.csproj`: `NAudio 2.2.1` | Уже в APK. `WaveFormat`, `WaveFileWriter`, `BufferedWaveProvider` — из NAudio.Core (чистый .NET) — их можно использовать на Android (проверить запуском, см. С-03). `WaveInEvent/WaveOutEvent` (WinMM) — только Windows. |
| Образец мобилки | `NurMarketMobile/lib/core/ai/live_call.dart` (263–295 `LiveVideoWire`, 829–850 `sendFrame`), `android/.../VoiceCallAudio.kt`, `docs/ТЗ Мобилка — видеозвонок с ИИ (26.10.2026).md` | Протокол кадров, признаки «модель не принимает видео» (закрытие ≤5 с после первого кадра, подсказки `video/image/mime/unsupported/media/resolution/modality`, код 1007), ≤150 КБ, FF D8, оценка трафика 10 мин ≈ 40–60 МБ. Звук Kotlin — те же настройки, что ниже. |

---

## 2. Архитектура звука

### 2.1. Новый файл `NurMarketKassa.Infrastructure/Services/Hardware/PlatformAudio.cs`

Только интерфейсы и «крючки» (как `CameraScan.Scanner`, `PrinterPortService.PlatformTransport`). Никаких Android-типов — файл собирается во все проекты.

```csharp
namespace NurMarketKassa.Services.Hardware;

public enum MicMode { Dictation, Call }          // вопрос голосом / живой звонок (эхоподавление)
public enum SpeakerMode { Playback, Call }       // озвучка ответа (медиа) / звонок (голосовая связь)
public enum MicAccess { Granted, Denied, Blocked } // Blocked — «больше не спрашивать», только настройки

/// Микрофон: 16 кГц, 16 бит, моно, куски ~100 мс (3200 байт). События — из потока записи (как WaveInEvent).
public interface IPcmMicrophone : IDisposable
{
    event Action<byte[], int>? DataAvailable;    // буфер может переиспользоваться — потребитель копирует
    event Action<string>? Failed;                 // устройство пропало/занято во время записи (понятный текст)
    void Start();                                 // InvalidOperationException с понятным текстом: занят, нет права
    void Stop();
}

/// Динамик: PCM 16 бит моно заданной частоты; Write не блокирует; Clear — сразу тишина (перебивание).
public interface IPcmSpeaker : IDisposable
{
    void Start();
    void Write(byte[] pcm, int offset, int count);
    int BufferedBytes { get; }                    // ещё не прозвучало: очередь + буфер устройства
    void Clear();
    void Stop();
}

public static class PlatformAudio
{
    public static Func<MicMode, IPcmMicrophone>? CreateMicrophone { get; set; }
    public static Func<SpeakerMode, int, IPcmSpeaker>? CreateSpeaker { get; set; }
    public static Func<bool>? HasMicrophone { get; set; }                 // FEATURE_MICROPHONE
    public static Func<Task<MicAccess>>? RequestMicrophoneAsync { get; set; }
    public static Action? OpenAppSettings { get; set; }
    public static Action<bool>? KeepScreenOn { get; set; }
    public static Action<bool>? SetSpeakerphone { get; set; }            // null — переключателя нет
    public static Func<bool>? HasEarpiece { get; set; }                  // телефон с разговорным динамиком

    /// Есть платформенный микрофон (на Windows всегда false — там NAudio).
    public static bool IsMicrophoneSupported => !OperatingSystem.IsWindows() && CreateMicrophone is not null
                                                && CreateSpeaker is not null && (HasMicrophone?.Invoke() ?? false);

    /// На Windows/Linux крючка нет → Granted (поведение прежнее).
    public static async Task<MicAccess> EnsureMicrophoneAsync() =>
        RequestMicrophoneAsync is { } ask ? await ask().ConfigureAwait(true) : MicAccess.Granted;

    // Жизненный цикл (поднимает только Android: MainActivity): фон/экран, потеря аудиофокуса.
    public static event Action<bool>? AppBackgroundChanged;   // true — ушли в фон (OnStop), false — вернулись (OnStart)
    public static event Action<bool>? AppPausedChanged;       // OnPause/OnResume — для камеры
    public static event Action? AudioFocusLost;               // входящий звонок, другая программа со звуком
    public static void RaiseBackground(bool bg) => AppBackgroundChanged?.Invoke(bg);
    public static void RaisePaused(bool paused) => AppPausedChanged?.Invoke(paused);
    public static void RaiseFocusLost() => AudioFocusLost?.Invoke();
}
```

### 2.2. `GeminiLiveVoice` — минимальный дифф, Windows по старым строкам

Добавить поля `private IPcmMicrophone? _pmic; private IPcmSpeaker? _pspk;` и три помощника:

```csharp
// 2026-11-xx: Android — звук через PlatformAudio; на Windows _pspk == null и работает прежний BufferedWaveProvider.
private int SpeakerBuffered => _pspk?.BufferedBytes ?? _speaker?.BufferedBytes ?? 0;
private void SpeakerClear() { _pspk?.Clear(); _speaker?.ClearBuffer(); }
private void SpeakerAdd(byte[] pcm) { if (_pspk is not null) _pspk.Write(pcm, 0, pcm.Length); else _speaker?.AddSamples(pcm, 0, pcm.Length); }
```

Замены (только эти строки):
- `Interrupt` (107, 111): `(_speaker?.BufferedBytes ?? 0)` → `SpeakerBuffered`; `_speaker?.ClearBuffer()` → `SpeakerClear()`.
- `OnServerContent` (431) → `SpeakerClear()`; (457) → `SpeakerAdd(pcm)`.
- `OnMicData` (506) → `SpeakerBuffered > 0`. Добавить в начало `if (MicMuted) { MicLevel = 0; return; }` — новое свойство `public bool MicMuted { get; set; }` (Windows его не трогает).
- `StartSpeaker`: в начало, после `if (!UseSpeaker) return;` — ветка `if (!OperatingSystem.IsWindows() && PlatformAudio.CreateSpeaker is { } make) { _pspk = make(SpeakerMode.Call, 24000); _pspk.Start(); return; }`; дальше прежний `if (!OperatingSystem.IsWindows()) return;` и NAudio как было. (Сейчас условие одно: `if (!UseSpeaker || !OperatingSystem.IsWindows()) return;` — разбить на два, смысл для Windows тот же.)
- `StartMicrophone`: условие `_mic is not null` → `_mic is not null || _pmic is not null`; перед созданием `WaveInEvent` — ветка `if (!OperatingSystem.IsWindows() && PlatformAudio.CreateMicrophone is { } make) { var m = make(MicMode.Call); m.DataAvailable += OnMicData; m.Failed += OnMicFailed; m.Start(); _pmic = m; return; }`.
- `OnMicFailed(string why)`: `_micFailReason = why; _cts.Cancel();`; в `RunAsync` в `finally` причина `reason ?? _micFailReason` (иначе `Ended(null)` выглядел бы как «завершил владелец»).
- `StopDevices`: в начало — остановить и освободить `_pmic`, `_pspk` (каждое в своём `try/catch`), обнулить.

`IsSupported` не меняется: `VoiceChatRecorder.IsSupported && TelegramAiChat.IsConfigured` — на Android станет true через С-03.

### 2.3. `VoiceChatRecorder` / `VoiceChatPlayer`

- `VoiceChatRecorder.IsSupported`: в начало `if (!OperatingSystem.IsWindows()) return PlatformAudio.IsMicrophoneSupported;` — Windows-ветка с `WaveInEvent.DeviceCount` без изменений. **Право на микрофон здесь не проверять** — кнопка видна, право спрашивается по нажатию.
- `Start()`: на не-Windows — `PlatformAudio.CreateMicrophone!(MicMode.Dictation)`, тот же обработчик (запись в `_pcm`, `MaxLengthReached` один раз). Тело обработчика вынести в private-метод `Append(byte[] buffer, int count)` и вызывать из обоих вариантов (Windows-логика та же).
- `Stop()/Dispose()`: остановить/освободить и платформенный микрофон. WAV собирается как сейчас (`WaveFileWriter` из NAudio.Core). Если на Android NAudio.Core не загрузится (проверка С-03) — собрать WAV вручную: 44 байта заголовка RIFF/fmt/data (PCM, 1 канал, 16000, 16 бит) + данные.
- `VoiceChatPlayer.Enqueue`: заменить `if (!OperatingSystem.IsWindows() || pcm.Length == 0) return;` на `if (pcm.Length == 0) return; if (!OperatingSystem.IsWindows()) { PlatformEnqueue(pcm, rate, generation); return; }`.
  `PlatformEnqueue` под тем же `lock (Sync)`: проверка `generation`; `_platform` (поле `IPcmSpeaker?` + `_platformRate`) создаётся `CreateSpeaker(SpeakerMode.Playback, rate)` при первом куске или смене частоты; `Write`. Нет фабрики — выйти (Linux).
  `IsPlaying`: `_out is { PlaybackState: Playing } || (_platform?.BufferedBytes ?? 0) > 0`.
  `Stop()`: после `_generation++` — `_platform?.Clear(); _platform?.Dispose(); _platform = null;` (освобождать каждый раз — не держать `AudioTrack` после ответа, цена создания ~50 мс).

---

## 3. Android-реализация звука

Новый файл `NurMarketKassa.Android/Platform/AndroidAudio.cs` (в Android-проект он попадает глобом `Platform\**\*.cs`; в Windows/Portable — нет).

### 3.1. `AndroidAudio.Install()` (вызов из `AndroidBootstrap.BeforeAvalonia`, рядом со строкой 99)
Ставит `PlatformAudio.CreateMicrophone/CreateSpeaker/HasMicrophone` (`PackageManager.HasSystemFeature(PackageManager.FeatureMicrophone)`), `RequestMicrophoneAsync` (через `AndroidPermissions`, раздел 4), `OpenAppSettings` (`Settings.ActionApplicationDetailsSettings`, `package:` + имя пакета, `NewTask`), `KeepScreenOn` (`CurrentActivity.RunOnUiThread` → `Window.AddFlags/ClearFlags(KeepScreenOn)`), `SetSpeakerphone`, `HasEarpiece` (`FeatureTelephony`).

### 3.2. Сеанс связи `AndroidCallAudioSession` (общий для микрофона и динамика режима Call, счётчик ссылок)
Первый `Acquire`: запомнить `AudioManager.Mode`; аудиофокус (`AudioFocusRequest` с API 26, иначе устаревший `RequestAudioFocus(listener, Stream.VoiceCall, AudioFocus.Gain)`); `Mode = InCommunication`; маршрут звука (3.4). Потеря фокуса `Loss`/`LossTransient` → `PlatformAudio.RaiseFocusLost()` (окно завершит звонок). Последний `Release`: вернуть маршрут, `Mode` и отдать фокус. Всё в `try/catch` — аппарат может не уметь.

**Запасной выключатель:** пустой файл `android-live-media-audio` в папке данных (как `android-no-nested-loop`) — звонок без режима связи: микрофон `AudioSource.Mic`, динамик `Usage.Media`, `Mode` не трогается. Для аппаратов, где в режиме связи тихо или нет звука (бывает на дешёвых планшетах без разговорного динамика). Записать в журнал, что выключатель сработал.

### 3.3. `AndroidPcmMicrophone`
- Источник: `Call` → `AudioSource.VoiceCommunication`; `Dictation` → `AudioSource.VoiceRecognition` (не инициализировался — повтор с `AudioSource.Mic`).
- 16000 Гц, `ChannelIn.Mono`, `Encoding.Pcm16bit`, буфер `max(GetMinBufferSize, 6400)`.
- `Start()`: право `RECORD_AUDIO` (`CheckSelfPermission`, API 23+) — нет → `InvalidOperationException(текст «нет доступа»)`; `AudioManager.Mode` = `InCall` (идёт телефонный разговор) → исключение «занят»; `State != Initialized` → освободить, исключение «занят»; `StartRecording()`, затем `RecordingState != Recording` → исключение «занят». Для `Call`: `AcousticEchoCanceler.IsAvailable` → `Create(sessionId).Enabled = true`; то же `NoiseSuppressor`. Режим `Call` берёт `AndroidCallAudioSession.Acquire()`.
- Поток чтения — отдельный `Thread { IsBackground = true, Name = "nur-mic" }` (не пул): `Read(buf, 0, 3200)`; `n > 0` → `DataAvailable(buf, n)`; `n < 0` (`ErrorDeadObject`, `ErrorInvalidOperation`) → `Failed(текст)` и выход из цикла.
- `Stop()/Dispose()` идемпотентны: флаг, `Join(500)`, `Stop`, `Release`, освободить эффекты, `Release` сеанса.

### 3.4. `AndroidPcmSpeaker`
- `Call`: `AudioAttributes` `Usage.VoiceCommunication` + `ContentType.Speech`; `Playback`: `Usage.Media` + `ContentType.Speech`. Моно, 16 бит, частота из параметра (звонок — 24000). `AudioTrack.Builder` с API 23, ниже — устаревший конструктор (`Stream.VoiceCall` / `Stream.Music`). `TransferMode.Stream`, буфер `max(GetMinBufferSize, rate*2*0.2)`.
- Очередь: `ConcurrentQueue<byte[]>` + счётчик `_queuedBytes`; поток `nur-speaker` пишет в `AudioTrack.Write` (блокирующая запись — нормально в своём потоке).
- `BufferedBytes = _queuedBytes + max(0, _writtenFrames − (PlaybackHeadPosition − _baseHead)) * 2`. **Важно** для эхозащиты `GeminiLiveVoice`: пока звук ещё в буфере устройства, микрофон должен молчать.
- `Clear()`: очистить очередь, `Pause()`, `Flush()`, `_baseHead = PlaybackHeadPosition`, `_writtenFrames = 0`, `Play()`.
- Маршрут (режим `Call`): гарнитура подключена (`GetDevices(Outputs)` API 23+: `WiredHeadset`, `WiredHeadphones`, `UsbHeadset`, `BluetoothSco`) → не трогать; иначе громкая связь **включена по умолчанию** (на планшетах разговорного динамика нет — иначе тишина). API 31+: `SetCommunicationDevice` (Bluetooth SCO → проводная → `BuiltinSpeaker`), `ClearCommunicationDevice` при освобождении; ниже — `SpeakerphoneOn`. Bluetooth-гарнитура на API < 31 — звук через динамик (SCO не включаем; отметить в чек-листе).
- Громкость звонка регулируется кнопками громкости (они уже идут системе — `MainActivity.IsSystemKey`).

---

## 4. Права

### 4.1. Манифест `NurMarketKassa.Android/Properties/AndroidManifest.xml`
Добавить рядом с камерой (комментарий с датой):
```xml
<!-- 2026-11-xx: голосовой вопрос и живой разговор с Нур Советником (AndroidAudio); аппарат без микрофона программу ставит. -->
<uses-permission android:name="android.permission.RECORD_AUDIO" />
<uses-permission android:name="android.permission.MODIFY_AUDIO_SETTINGS" />
<uses-feature android:name="android.hardware.microphone" android:required="false" />
<uses-feature android:name="android.hardware.camera.front" android:required="false" />
```
csproj не меняется. Служба `KeepAliveService` не меняется (типы `microphone/camera` не добавляем — в фоне звонок не работает, раздел 6.4).

### 4.2. Новый `NurMarketKassa.Android/Platform/AndroidPermissions.cs`
`Task<MicAccess> RequestAsync(string permission, int code)` — по образцу `AndroidCameraScanner.ScanAsync` (стр. 45–60): API < 23 → `Granted`; `CheckSelfPermission == Granted` → `Granted`; иначе `RequestPermissions`, `TaskCompletionSource` по коду. Отказ: если раньше уже спрашивали (флаг `asked_<permission>` в `SharedPreferences "nur_permissions"`) и `ShouldShowRequestPermissionRationale == false` → `Blocked`, иначе `Denied` (так же делает мобилка, `VoiceCallAudio.kt:152–159`). Коды (исправлено 05.11.2026, аудит №4 А4-13): **4713 — микрофон, 4714 — камера звонка**; 4711 — камера-сканер, 4712 — Bluetooth-принтер (с 1.17.75), 4715 — уведомления. Запрос — через общий `Platform/AndroidPermissions.cs` (`RequestAsync(code, permissions)` — Task, завершается ответом пользователя; ответы приходят из `MainActivity.OnRequestPermissionsResult`), а не отдельным `TaskCompletionSource` в каждом классе.
`MainActivity.OnRequestPermissionsResult` — одной строкой добавить `AndroidPermissions.OnResult(requestCode, grantResults);` (строку сканера оставить).

### 4.3. Где спрашивать
- `ToggleVoiceAsync` (4093, ветка «начать запись») и `StartLiveAsync` (после проверок 3763–3769, **до** `++_liveGen`): `var access = await PlatformAudio.EnsureMicrophoneAsync();` → не `Granted` — карточка в ленте (текст Т-06; при `Blocked` — кнопка «Открыть настройки» → `PlatformAudio.OpenAppSettings`) и выход. На Windows крючка нет → `Granted` мгновенно, поведение прежнее.
- Занятый микрофон: исключение из `Start()` уже ловится — голосовой вопрос (4104–4109) покажет «Микрофон не включился: » + текст Т-07; звонок — `Ended(reason)` → «Разговор прервался: …» (3856–3866). Отдельно ничего не нужно, только понятный текст исключения.

---

## 5. Голосовой ввод на Android (п. 3 задачи)
После С-01…С-05 кнопки 🎤, «Разговор» и ссылка «Озвучить» появляются сами (условие `VoiceChatRecorder.IsSupported`, стр. 320 и 1634). Проверить: запись ≤ 60 с (`MaxLength`), «Не расслышал» на тишине < 0,4 с, распознавание `TelegramVoice.TranscribeAsync(wav, "audio/wav")` — без изменений, ответ озвучивается через `VoiceChatPlayer` (динамик, режим медиа). Тумблеры `voice.ask`/`voice.live` прячут кнопки как на Windows (`Capabilities.cs:30–33`).

---

## 6. Видео (п. 4 задачи)

### 6.1. Новый `NurMarketKassa.Infrastructure/Services/Hardware/PlatformCamera.cs`
```csharp
public enum LiveCameraFacing { Back, Front }

/// Камера звонка. События — из потока камеры. Кадры только в памяти.
public interface ILiveCamera : IDisposable
{
    bool HasBack { get; }
    bool HasFront { get; }
    LiveCameraFacing Facing { get; }
    bool IsRunning { get; }
    /// Предпросмотр для окна: JPEG как с датчика + поворот (0/90/180/270) и зеркало (фронтальная) — поворачивает окно.
    event Action<byte[], int, bool>? PreviewFrame;
    event Action<string>? Failed;                      // занята другой программой / пропала
    Task<MicAccess> StartAsync(LiveCameraFacing facing); // спрашивает CAMERA (код 4714, `AndroidPermissions.CallCamera`); MicAccess здесь — общий «результат права»
                                                         // (можно переименовать в PermissionResult для обоих — на выбор исполнителя)
    void Stop();
    Task SwitchAsync();
    /// Кадр для ИИ: уже повёрнутый «как видит человек», JPEG ≤ 150 КБ, длинная сторона ≤ 640. null — кадра ещё нет.
    byte[]? GrabFrameForAi();
}

public static class PlatformCamera
{
    public static Func<bool>? IsAvailable { get; set; }   // Android: FeatureCameraAny
    public static Func<ILiveCamera>? Create { get; set; }
    public static bool Supported => Create is not null && (IsAvailable?.Invoke() ?? false);
}
```

### 6.2. Новый `NurMarketKassa.Android/Platform/AndroidLiveCamera.cs` (Camera1, `#pragma warning disable CS0618, CA1422` как в сканере)
- Свой `HandlerThread "nur-live-camera"`; `Camera.Open(id)` и все вызовы камеры — в нём (иначе обратные вызовы кадров придут в главный поток Android, где работает интерфейс Avalonia, — подёргивания).
- Без экрана: `SetPreviewTexture(new SurfaceTexture(10))` (с API 26 можно `new SurfaceTexture(false)`); если `StartPreview` падает на аппарате — записать в журнал и `Failed`.
- Размер: поддерживаемый 4:3, ближайший к 640×480 (не больше 1280×720); диапазон кадров — минимальный из `SupportedPreviewFpsRange`; фокус `ContinuousVideo` → `ContinuousPicture` → `Auto` (как в сканере).
- `SetPreviewCallbackWithBuffer` + два заранее выделенных буфера `w*h*3/2`. Буфер возвращается камере (`AddCallbackBuffer`) только когда нужен следующий кадр: ~8 кадров/с при видимом предпросмотре, 1 кадр/с при скрытом. Так из Java в .NET копируется 1–8 кадров в секунду, а не 30.
- Предпросмотр: NV21 → `YuvImage.CompressToJpeg(rect, 50, stream)` → `PreviewFrame(jpeg, rotation, mirror)`. Поворот: как `ScannerActivity.DisplayOrientation` (сенсор + поворот экрана `CurrentActivity.WindowManager.DefaultDisplay.Rotation`, читать на каждом кадре — экран поворачивается без пересоздания активности).
- `GrabFrameForAi()`: последний NV21 (копия) → `YuvImage` JPEG 85 → `BitmapFactory.DecodeByteArray` → `Bitmap.CreateBitmap(…, Matrix.PostRotate(rotation))` (без зеркала) → при длинной стороне > 640 — `CreateScaledBitmap` → `Compress(Jpeg, 60)`; > 150 КБ → повтор с 40; всё ещё больше → `null` (пропуск). Все `Bitmap` — `Recycle()` в `finally`. Вызывается не чаще 1 раза в секунду.
- `SwitchAsync`: освободить текущую, открыть другую (`CameraInfo.Facing`). По умолчанию — задняя; нет задней — фронтальная.
- **Пауза активности**: подписка на `PlatformAudio.AppPausedChanged`: `true` → освободить камеру **синхронно** (пост в поток камеры + ожидание до 1 с), запомнить «была включена»; `false` → открыть снова. `OnPause` текущей активности завершается до `OnResume` следующей — поэтому `ScannerActivity`, системная камера («📷 Добавить фото») и окно разрешений получают камеру без «Camera in use». Сканер не меняем; если на живом аппарате конфликт всё же будет — добавить в `ScannerActivity.StartCamera` явный вызов `AndroidLiveCamera.ReleaseForOtherUse()` (отметить в отчёте).
- Ошибки `Camera.Open` («Fail to connect to camera service»), `ErrorCallback` (`CameraErrorServerDied`, `Evicted`) → `Failed(Т-09)`.
- Ничего не писать на диск, в журнал — только «камера N, кадр W×H, фокус …» и итоги.
- `AndroidLiveCamera.Install()` — из `AndroidBootstrap.BeforeAvalonia` рядом с `AndroidCameraScanner.Install()`.
- В `MainActivity.OnPause/OnResume` (новые переопределения, `base` первым/последним как положено) — `PlatformAudio.RaisePaused(true/false)`; в `OnStart/OnStop` — добавить `PlatformAudio.RaiseBackground(false/true)`.

### 6.3. `GeminiLiveVoice` — видео (только при `Video = true`, Windows не затронут)
- `public bool Video { get; init; }`; `public event Action<string>? VideoRejected;`; счётчики `SentFrames`, `SkippedFrames`, `SentBytes`; `public bool VideoActive => Video && !_videoOff && IsOpen;`.
- Порядок моделей: при `Video` — `VideoModels = { "gemini-2.5-flash-native-audio-latest", "gemini-3.1-flash-live-preview" }` и своя запомненная `_workingVideoModel` (обычный звонок — прежние `Models`/`_workingModel`).
- `Setup()`: при `Video && !_videoOff` — `generationConfig["mediaResolution"] = "MEDIA_RESOLUTION_LOW"`.
- `public bool SendFrame(byte[] jpeg)`: `false`, если не `VideoActive` или `_cts` отменён; не JPEG (`FF D8`) или > 150 КБ → `SkippedFrames++`, `false`; иначе в `Channel.CreateBounded<byte[]>(1, DropOldest)` `_frames`. Отдельный `FramePumpAsync` (запускается в `RunAsync` рядом с `SendPumpAsync`, только при `Video`) шлёт `{"realtimeInput":{"video":{"data":<base64>,"mimeType":"image/jpeg"}}}` через `SendJsonAsync` (тот же `_sendLock`), запоминает `_firstFrameAtUtc`.
- Отказ видео: (а) сеанс не открылся с `mediaResolution` (не лимит и не ключ) → `_videoOff = true; VideoRejected(reason); swap--; continue;` — по образцу `_toolsOff` (стр. 257–263), ставить **перед** проверкой инструментов; (б) Google закрыл соединение ≤ 5 с после первого кадра с причиной, где есть `video|image|mime|unsupported|media|resolution|modality`, или кодом 1007 → `_videoOff = true; VideoRejected(...)`, и в `RunAsync` переподключиться даже без ключа возобновления (условие стр. 175 расширить: `connected && (_resumeHandle is not null || _videoJustRejected) && reconnects++ < 6`).
- В конце разговора одна строка журнала: `Живой разговор ИИ: видео — кадров N, K КБ, пропущено S[, модель видео не приняла].` Кадры, base64 и ключ — никогда.

### 6.4. Расписание кадров — новый `NurMarketKassa.Infrastructure/Services/Hardware/LiveVideoPump.cs` (чистый .NET)
Вход: `Func<byte[]?> grab`, `GeminiLiveVoice live`, `Func<bool> allowed` (камера включена, окно видно, не фон, лимит не вышел). Цикл `Task` с `CancellationToken`:
| Состояние | Кадр |
|---|---|
| `Connecting`, `Ended`, `!allowed()` | не шлём |
| `Listening` | раз в 1 с; начало речи после тишины ≥ 2 с (`MicLevel` > 0,06) — свежий кадр сразу |
| `Speaking` | раз в 3 с |
| Тишина > 60 с (`LastActivityUtc`) | раз в 5 с |

Отдельный класс — чтобы проверять расписание имитацией (подставной `grab`, без камеры и без сети).

### 6.5. Приватность и согласие
- Перед **первым** видеозвонком на этом устройстве — карточка в ленте (не модальное окно: на Android модальные окна идут через `AndroidNestedLoop`): заголовок Т-05, текст Т-05а, кнопки «Включить камеру» / «Не сейчас». Согласие — новое поле `UserPreferences.AdvisorVideoConsent` (строка `"v1:<дата ISO>"`, только локально: поля нет в `SettingsCloudSync.CompanyKeys`, схема базы не меняется). Текст поменяется — `v2`, карточка покажется снова.
- Кадры — только в памяти (`byte[]`, сразу на отправку); не в журнал, не в историю чата, не в файл. В историю разговора — только реплики, как сейчас.
- Индикатор «● ИИ видит камеру» виден, пока кадры реально уходят; иначе «Камера на паузе». Плюс системный зелёный значок Android 12+.

---

## 7. Окно советника (п. 5 задачи)

Новый файл `NurMarketKassa.Avalonia/Views.axaml/Main/AiAdvisorWindow.Video.cs` (partial; без Android-типов — собирается и в Windows, и в Portable). В большом файле — только точечные вызовы.

### 7.1. Кнопки старта
- Видимость видео: `CanVideo => AdvisorCapabilities.MasterOn && Cap("voice.live") && Cap("voice.video") && PlatformCamera.Supported && TelegramAiChat.IsConfigured` (последнее — как у звонка). На Windows `PlatformCamera.Supported == false` → кнопок нет.
- **Широкое окно (≥ 760, планшет боком):** кнопка «Видео» (значок камеры + слово, как «Разговор») сразу после «Разговор». В `inputRow` добавить колонку: `"*,Auto,Auto,Auto,Auto,Auto"`, «Видео» — колонка 4, «Спросить» — 5 (стр. 274, 361). Создавать только при `PlatformCamera.Supported`, иначе колонка пустая (`Auto` = 0) и Windows выглядит как раньше.
- **Узкое окно (< 760, телефон, планшет стоя):** отдельной кнопки нет (не помещается). «Разговор» на аппарате с камерой открывает маленькое меню (`MenuFlyout`, пункты высотой ≥ 48): «Голосом» / «С камерой». Без камеры — как сейчас, сразу звонок. Переключение — в обработчике `NarrowLayout.Attach` (стр. 420–437): `_video.IsVisible = !narrow && CanVideo`.
- `ApplyCapabilities` (`Capabilities.cs:27–49`) — добавить `_video` и обновление меню.
- `StartLiveAsync()` → `StartLiveAsync(bool video = false)`; кнопка «Разговор» вызывает как раньше. Перед стартом видео: согласие (6.5) → право микрофона → `camera.StartAsync(Back)` (право CAMERA) → отказ — карточка Т-08 (+ «Открыть настройки» при `Blocked`) и выход без звонка. Затем `new GeminiLiveVoice(instruction) { Video = true }`.

### 7.2. Панель звонка (`BuildLiveBar`, стр. 3713–3758)
- Сетке ряда — класс `no-reflow` (чтобы `AutoReflow` не ставил её столбиком).
- Новые кнопки (видны только в видеозвонке): «Камера» вкл/выкл (значок камеры / перечёркнутой камеры), «Сменить камеру» (только при `HasBack && HasFront`). «Динамик» — только Android-телефон (`PlatformAudio.HasEarpiece() == true` и `SetSpeakerphone != null`), во всех звонках.
- Узко: два ряда — сверху круг + состояние, снизу кнопки (`WrapPanel`, кнопки ≥ 44×44, «Завершить» последней и красной). Широко — один ряд, как сейчас + новые кнопки перед «Перебить».

### 7.3. Предпросмотр
- `Border` со скруглением 14 и `ClipToBounds`, внутри `Image { Stretch = UniformToFill }` с `RotateTransform` (поворот из `PreviewFrame`) и `ScaleX = -1` для фронтальной. Кладётся в ряд 2 `_root` поверх `_scroll` (общая `Panel`).
- Широко: справа сверху 240×180 (боком) / 180×240 (стоя). Узко стоя: на всю ширину, высота `min(ширина·3/4, 40 % высоты)`; сообщения ленты получают такой же отступ сверху, чтобы не прятаться под предпросмотром. Телефон боком — 200×150 справа.
- Сверху слева на предпросмотре — плашка «● ИИ видит камеру» / «Камера на паузе» (точка + текст, не только цвет).
- Кадр предпросмотра декодируется в `Avalonia.Media.Imaging.Bitmap` **в фоновом потоке**; в интерфейс — `Dispatcher.UIThread.Post`, старый `Bitmap` — `Dispose()` после замены; пока предыдущий не показан — новый пропускается (без очереди).

### 7.4. Паузы, лимиты, жизненный цикл (в `TickLive` и подписках; всё — только при `PlatformAudio.IsMicrophoneSupported`, на Windows не срабатывает)
| Событие | Что делаем |
|---|---|
| Окно советника спрятано (`IsVisible` → false, переход в другой раздел) | камера `Stop`, кадры не шлём; голос продолжается (как на Windows). Вернулись — камера снова, если была включена. |
| `AppPausedChanged(true)` | камера освобождается (делает сама `AndroidLiveCamera`), плашка «на паузе». |
| `AppBackgroundChanged(true)` | `live.MicMuted = true`, в ленте Т-12; таймер 60 с. Вернулись раньше — `MicMuted = false`. Не вернулись — `EndLive(Т-12а)`. (Android 9+ сам не даёт микрофон и камеру свёрнутой программе без службы нужного типа — служба `TypeSpecialUse` этого не даёт, и не должна.) |
| `AudioFocusLost` | `EndLive(Т-12б)`. |
| Звонок 19 мин | карточка Т-11в с кнопкой «Продлить на 10 минут»; 20 мин — `EndLive(Т-11г)`. |
| Видео 9 мин | карточка Т-11 с «Продлить на 10 минут»; 10 мин — камера выкл., `EndLive` не вызывается, в ленте Т-11б. |
| `VideoRejected` | камера выкл., кнопки камеры скрыты, Т-10; звонок продолжается. |
| `camera.Failed` | камера выкл., Т-09; звонок продолжается. |
| 3 мин тишины | как сейчас — конец звонка (стр. 3924). |
| Начало звонка / конец | `PlatformAudio.KeepScreenOn?.Invoke(true/false)` (иначе экран погаснет → `OnStop` → пауза). |

### 7.5. Уборка (утечки)
`EndLive` (4041) — добавить в конце: отмена `LiveVideoPump`, `_camera?.Stop(); _camera?.Dispose(); _camera = null;`, скрыть предпросмотр и освободить `Bitmap`, `KeepScreenOn(false)`, остановить таймер фона. `Closed` (469) уже зовёт `EndLive` — отписаться там же от `PlatformAudio.*` событий и `IsVisible`. Все подписки на статические события — с отпиской (иначе спрятанное и закрытое окно живёт в памяти).

### 7.6. Доступность
Каждой новой кнопке — `AutomationProperties.SetName` и `ToolTip` (тексты ниже); предпросмотру — имя Т-13; пункты меню — свои имена; плашка состояния — текстом. Кнопки ≥ 44×44, шрифты ≥ 12, кисти — только ресурсы темы (`Use(…, "BrushText")` и т. п.), без жёстких цветов (кроме белого текста на `BrushDanger`, как у «Завершить»).

---

## 8. Тексты (5 языков, `Tr.T(ru, ky, en, tr, uz)`)

| № | ru | ky | en | tr | uz |
|---|---|---|---|---|---|
| Т-01 кнопка | Видео | Видео | Video | Görüntülü | Video |
| Т-01а имя/подсказка | Видеозвонок с советником: он слышит вас и видит камеру — покажите товар, полку или накладную | Кеңешчи менен видео чалуу: ал сизди угат жана камераны көрөт — товарды, текчени же накладнойду көрсөтүңүз | Video call with the advisor: it hears you and sees the camera — show a product, a shelf or an invoice | Danışmanla görüntülü görüşme: sizi duyar ve kamerayı görür — ürünü, rafı veya faturayı gösterin | Maslahatchi bilan videoqo'ng'iroq: u sizni eshitadi va kamerani ko'radi — mahsulot, javon yoki yuk xatini ko'rsating |
| Т-02 меню | Голосом / С камерой | Үн менен / Камера менен | By voice / With the camera | Sesli / Kamerayla | Ovoz bilan / Kamera bilan |
| Т-03 камера | Включить камеру / Выключить камеру | Камераны күйгүзүү / Камераны өчүрүү | Turn the camera on / Turn the camera off | Kamerayı aç / Kamerayı kapat | Kamerani yoqish / Kamerani o'chirish |
| Т-03а | Сменить камеру | Камераны алмаштыруу | Switch camera | Kamerayı değiştir | Kamerani almashtirish |
| Т-03б | Динамик | Динамик | Speaker | Hoparlör | Karnay |
| Т-04 плашка | ● ИИ видит камеру | ● ИИ камераны көрүп жатат | ● The AI sees the camera | ● Yapay zekâ kamerayı görüyor | ● SI kamerani ko'ryapti |
| Т-04а | Камера на паузе | Камера тыныгууда | Camera paused | Kamera duraklatıldı | Kamera pauzada |
| Т-05 согласие | Показать камеру советнику? | Камераны кеңешчиге көрсөтөлүбү? | Show the camera to the advisor? | Kamera danışmana gösterilsin mi? | Kamerani maslahatchiga ko'rsatamizmi? |
| Т-05а | Во время звонка примерно раз в секунду кадр с камеры уходит в Google Gemini — так советник видит товар, полку или накладную. Кадры не сохраняются на устройстве и не пишутся в журнал. Не показывайте банковские карты, пароли и лица посторонних. Видео расходует лимит ключа ИИ и интернет быстрее голоса (≈ 4–6 МБ в минуту). | Чалуу учурунда болжол менен секундасына бир жолу камерадан кадр Google Gemini'ге кетет — ошентип кеңешчи товарды, текчени же накладнойду көрөт. Кадрлар түзмөктө сакталбайт жана журналга жазылбайт. Банк карталарын, сырсөздөрдү жана бөтөн адамдардын жүздөрүн көрсөтпөңүз. Видео ИИ ачкычынын лимитин жана интернетти үнгө караганда тезирээк коротот (мүнөтүнө ≈ 4–6 МБ). | During the call, about once a second a camera frame is sent to Google Gemini so the advisor can see a product, a shelf or an invoice. Frames are not saved on the device or written to the log. Don't show bank cards, passwords or other people's faces. Video uses the AI key's limit and mobile data faster than voice (≈ 4–6 MB per minute). | Görüşme sırasında yaklaşık saniyede bir kamera karesi Google Gemini'ye gönderilir; böylece danışman ürünü, rafı veya faturayı görür. Kareler cihazda saklanmaz ve günlüğe yazılmaz. Banka kartlarını, şifreleri ve başkalarının yüzlerini göstermeyin. Görüntü, yapay zekâ anahtarının limitini ve interneti sesten daha hızlı harcar (dakikada ≈ 4–6 MB). | Qo'ng'iroq paytida taxminan soniyasiga bir marta kameradan kadr Google Gemini'ga yuboriladi — shunda maslahatchi mahsulot, javon yoki yuk xatini ko'radi. Kadrlar qurilmada saqlanmaydi va jurnalga yozilmaydi. Bank kartalari, parollar va begonalarning yuzlarini ko'rsatmang. Video SI kaliti limitini va internetni ovozdan tezroq sarflaydi (daqiqasiga ≈ 4–6 MB). |
| Т-05б кнопки | Включить камеру / Не сейчас | Камераны күйгүзүү / Азыр эмес | Turn on the camera / Not now | Kamerayı aç / Şimdi değil | Kamerani yoqish / Hozir emas |
| Т-06 нет права микрофона | Нет доступа к микрофону. Разрешите его: Настройки Android → Приложения → NurCRM → Разрешения → Микрофон. | Микрофонго уруксат жок. Android жөндөөлөрү → Колдонмолор → NurCRM → Уруксаттар → Микрофон. | No microphone access. Allow it in Android Settings → Apps → NurCRM → Permissions → Microphone. | Mikrofon izni yok. Android Ayarlar → Uygulamalar → NurCRM → İzinler → Mikrofon. | Mikrofonga ruxsat yo'q. Android sozlamalari → Ilovalar → NurCRM → Ruxsatlar → Mikrofon. |
| Т-06а кнопка | Открыть настройки | Жөндөөлөрдү ачуу | Open settings | Ayarları aç | Sozlamalarni ochish |
| Т-07 занят | Микрофон занят: идёт телефонный звонок или его использует другая программа. Завершите их и попробуйте снова. | Микрофон бош эмес: телефон чалуу жүрүп жатат же аны башка программа колдонуп жатат. Аларды бүтүрүп, кайра аракет кылыңыз. | The microphone is busy: a phone call is in progress or another app is using it. Finish it and try again. | Mikrofon meşgul: bir telefon görüşmesi sürüyor veya başka bir uygulama kullanıyor. Kapatıp tekrar deneyin. | Mikrofon band: telefon qo'ng'irog'i ketyapti yoki uni boshqa dastur ishlatyapti. Ularni tugatib, qayta urinib ko'ring. |
| Т-08 нет права камеры | Нет доступа к камере. Разрешите его: Настройки Android → Приложения → NurCRM → Разрешения → Камера. | Камерага уруксат жок. Android жөндөөлөрү → Колдонмолор → NurCRM → Уруксаттар → Камера. | No camera access. Allow it in Android Settings → Apps → NurCRM → Permissions → Camera. | Kamera izni yok. Android Ayarlar → Uygulamalar → NurCRM → İzinler → Kamera. | Kameraga ruxsat yo'q. Android sozlamalari → Ilovalar → NurCRM → Ruxsatlar → Kamera. |
| Т-09 камера занята | Камера занята другой программой. Разговор продолжается голосом. | Камераны башка программа колдонуп жатат. Маек үн менен уланууда. | The camera is in use by another app. The conversation continues by voice. | Kamera başka bir uygulama tarafından kullanılıyor. Sohbet sesli devam ediyor. | Kamerani boshqa dastur ishlatyapti. Suhbat ovoz bilan davom etmoqda. |
| Т-10 модель | Эта модель Google не принимает видео — разговор продолжается голосом. | Бул Google модели видео кабыл албайт — маек үн менен уланууда. | This Google model doesn't accept video — the conversation continues by voice. | Bu Google modeli görüntü kabul etmiyor — sohbet sesli devam ediyor. | Bu Google modeli videoni qabul qilmaydi — suhbat ovoz bilan davom etmoqda. |
| Т-11 | Через минуту видео выключится, чтобы беречь батарею и интернет. | Бир мүнөттөн кийин батареяны жана интернетти үнөмдөө үчүн видео өчөт. | Video will turn off in a minute to save battery and data. | Pil ve internetten tasarruf için görüntü bir dakika içinde kapanacak. | Batareya va internetni tejash uchun video bir daqiqadan keyin o'chadi. |
| Т-11а кнопка | Продлить на 10 минут | 10 мүнөткө узартуу | Extend by 10 minutes | 10 dakika uzat | 10 daqiqaga uzaytirish |
| Т-11б | Видео выключено: прошло 10 минут. Разговор продолжается голосом. | Видео өчүрүлдү: 10 мүнөт өттү. Маек үн менен уланууда. | Video turned off: 10 minutes have passed. The conversation continues by voice. | Görüntü kapatıldı: 10 dakika geçti. Sohbet sesli devam ediyor. | Video o'chirildi: 10 daqiqa o'tdi. Suhbat ovoz bilan davom etmoqda. |
| Т-11в | Через минуту разговор завершится. | Бир мүнөттөн кийин маек бүтөт. | The conversation will end in a minute. | Sohbet bir dakika içinde bitecek. | Suhbat bir daqiqadan keyin tugaydi. |
| Т-11г | Разговор завершён: прошло 20 минут. Начните новый, если нужно. | Маек бүттү: 20 мүнөт өттү. Керек болсо жаңысын баштаңыз. | Conversation ended: 20 minutes have passed. Start a new one if needed. | Sohbet bitti: 20 dakika geçti. Gerekirse yenisini başlatın. | Suhbat tugadi: 20 daqiqa o'tdi. Kerak bo'lsa, yangisini boshlang. |
| Т-12 фон | Разговор на паузе — программа свёрнута. Вернитесь в течение минуты, чтобы продолжить. | Маек тыныгууда — программа кичирейтилди. Улантуу үчүн бир мүнөттүн ичинде кайтыңыз. | The conversation is paused — the app is minimised. Come back within a minute to continue. | Sohbet duraklatıldı — uygulama simge durumunda. Devam etmek için bir dakika içinde dönün. | Suhbat pauzada — dastur yig'ildi. Davom ettirish uchun bir daqiqa ichida qayting. |
| Т-12а | Разговор завершён: программа была свёрнута. | Маек бүттү: программа кичирейтилген. | Conversation ended: the app was minimised. | Sohbet bitti: uygulama simge durumundaydı. | Suhbat tugadi: dastur yig'ilgan edi. |
| Т-12б | Разговор завершён: входящий звонок или другая программа заняла звук. | Маек бүттү: кирүүчү чалуу же башка программа үндү ээледи. | Conversation ended: an incoming call or another app took over the audio. | Sohbet bitti: gelen arama veya başka bir uygulama sesi devraldı. | Suhbat tugadi: kiruvchi qo'ng'iroq yoki boshqa dastur ovozni egalladi. |
| Т-13 имя предпросмотра | Изображение с камеры, его видит советник | Камеранын сүрөтү, аны кеңешчи көрөт | Camera image the advisor sees | Danışmanın gördüğü kamera görüntüsü | Kamera tasviri, uni maslahatchi ko'radi |
| Т-14 тумблер (название) | Видеозвонок с камерой (Android) | Камера менен видео чалуу (Android) | Video call with the camera (Android) | Kamerayla görüntülü görüşme (Android) | Kamera bilan videoqo'ng'iroq (Android) |
| Т-14а тумблер (пояснение) | Выключено: кнопок «Видео» и «Камера» нет, кадры с камеры в Google Gemini не уходят. | Өчүк: «Видео» жана «Камера» баскычтары жок, камеранын кадрлары Google Gemini'ге кетпейт. | Off: no “Video” or “Camera” buttons; no camera frames go to Google Gemini. | Kapalı: «Görüntülü» ve «Kamera» düğmeleri yok; kamera kareleri Google Gemini'ye gitmez. | O'chiq: «Video» va «Kamera» tugmalari yo'q, kamera kadrlari Google Gemini'ga ketmaydi. |

Исключения микрофона/камеры в Android-коде — тоже через `Tr.T` (он доступен в Android-проекте, см. `KeepAliveService`).

---

## 9. Платная функция, права, зависимости

- **Не менять**: `TariffGate.CanUseAi` (через `TelegramAiChat.IsConfigured`/`AiKey`), `LicenseKeys`, пункт меню советника (`OwnerShellWindow:680`), `CurrentAccess()` (сотрудники в звонок — только с правом на зарплату, стр. 3788–3790), `ProductActionPlan.ChangesAllowed`, карточки «Выполнить». Видео не добавляет инструментов и не меняет данных — ИИ только видит.
- `AdvisorCapabilities`: одна новая запись `new AdvisorCapability("voice.video", "voice", AdvisorRisk.SendsOut, true, false, null, L(Т-14), L(Т-14а))` после `voice.live` (стр. 195–197). По умолчанию включено — поведение «как было до тумблера» (решение Р-1).
- **Новых NuGet-зависимостей нет.** Используются Mono.Android (`AudioRecord`, `AudioTrack`, `AudioManager`, `audiofx`, `Camera`, `YuvImage`, `BitmapFactory`) и уже подключённый NAudio (только NAudio.Core на Android). APK практически не вырастет (≈ десятки КБ кода).
- Изменения вне кода, о которых владелец должен знать: манифест (+2 права, +2 `uses-feature`), новое локальное поле настроек `AdvisorVideoConsent`. csproj, схема базы, сборка — без изменений.

---

## 10. Риски и защита

| Риск | Защита |
|---|---|
| Регресс Windows | Ветки только при `!IsWindows()` и непустых фабриках; Windows-строки NAudio не трогаются; `MicMuted`/`Video` по умолчанию выключены. Проверка: дифф `GeminiLiveVoice`/`VoiceChatAudio` — Windows-пути байт-в-байт, плюс живой звонок и 🎤 на Windows (С-11). |
| Микрофон/камера остаются включены после закрытия окна или перехода в раздел | `EndLive` + `Closed` освобождают всё; камера — на `IsVisible=false`, `OnPause`; идемпотентные `Stop/Dispose`; журнал «камера освобождена», «микрофон освобождён». Проверить системным значком Android (зелёная точка гаснет). |
| Фон Android, выгрузка процесса | Микрофон молчит, 60 с — конец; камера отпущена в `OnPause`. Служба переднего плана не меняется (нет новых типов служб и прав FOREGROUND_SERVICE_MICROPHONE/CAMERA). |
| Конфликт со сканером штрихкодов и системной камерой | Освобождение в `OnPause` до `OnResume` следующей активности; запасной ход — явный вызов в `ScannerActivity.StartCamera`. |
| Эхо / ИИ слышит себя | `VoiceCommunication` + AEC/NS + прежняя эхозащита по `BufferedBytes` (точный подсчёт буфера `AudioTrack` — обязателен). |
| Тишина или тихо на планшете в режиме связи | Громкая связь по умолчанию; файл-выключатель `android-live-media-audio`. |
| Лимит ключа Gemini и трафик | 1 кадр/с (3 с при ответе, 5 с в тишине), `MEDIA_RESOLUTION_LOW`, ≤ 150 КБ, видео 10 мин, звонок 20 мин, 3 мин тишины; согласие называет расход. |
| Модель не принимает видео | Распознавание отказа (6.3), продолжение голосом, своя запомненная модель для видео. |
| Приватность | Кадры только в памяти; не в журнале, не в истории; согласие; плашка «ИИ видит камеру»; тумблер `voice.video`. |
| Нагрузка на слабые аппараты | Буферы камеры возвращаются по расписанию (1–8 копий/с), JPEG силами Android, предпросмотр без очереди, `Bitmap` освобождаются. |
| Поток интерфейса | Камера в своём `HandlerThread`, микрофон/динамик — свои потоки; всё в UI — `Dispatcher.UIThread.Post` (AI_HANDOFF, раздел 10 «Ловушки»). |
| Старые Android (API 21–25) | Ветки для `AudioTrack` без Builder, фокуса без `AudioFocusRequest`, `SpeakerphoneOn`, разрешения при установке. |
| Portable/Linux | Новые host-файлы без Android-типов; фабрики не ставятся → кнопок нет, как сейчас. |
| Подготовка выпуска | Работы — после 1.17.73, отдельным коммитом; версии не поднимать без команды «выгрузи». |

---

## 11. План работ

Каждый шаг — отдельная проверка сборкой. Сборки:
- Windows: `cd NurMarketKassa.Avalonia && dotnet build NurMarketKassa.Avalonia/NurMarketKassa.Avalonia.csproj -c Debug -v q -m:1 -nodeReuse:false` (при OutOfMemory в XAML — `dotnet build-server shutdown` и повтор; при параллельной сессии — через `build-locked.ps1`, см. память «Проверять другие сессии»).
- Portable: `dotnet build NurMarketKassa.Portable/NurMarketKassa.Portable.csproj -c Debug -v q -m:1 -nodeReuse:false`.
- Android: из папки с `global.json` SDK 8.0.422 полным путём к `NurMarketKassa.Android.csproj` (AI_HANDOFF, стр. 275 и 320; подпись — только ключом из `Documents\NurMarket-keys`, пароль из переменной, не печатать).

| Шаг | Что | Файлы | Приёмка |
|---|---|---|---|
| **С-01** | `PlatformAudio.cs` (интерфейсы, крючки, события) | Infrastructure/Services/Hardware/PlatformAudio.cs (новый) | Собираются Windows, Portable, Android; ничего не вызывает новый код. |
| **С-02** | `GeminiLiveVoice` на абстракции (2.2) + `MicMuted`, `OnMicFailed` | GeminiLiveVoice.cs | Дифф: NAudio-строки и логика Windows не изменены (кроме замен на помощники с тем же результатом). Сборка ×3. |
| **С-03** | `VoiceChatRecorder`/`VoiceChatPlayer` (2.3) | VoiceChatAudio.cs | Windows-ветки те же; на Android `WaveFileWriter` работает (проверка С-04 на эмуляторе/аппарате) или ручной WAV. |
| **С-04** | Android-звук (3) + регистрация | Android/Platform/AndroidAudio.cs (новый), AndroidBootstrap.cs | APK собирается; журнал: «Android: звук — микрофон есть, AEC да/нет, NS да/нет». |
| **С-05** | Права (4) | AndroidManifest.xml, Android/Platform/AndroidPermissions.cs (новый), MainActivity.cs (`OnRequestPermissionsResult`, `OnPause/OnResume`, `OnStart/OnStop` → события), AiAdvisorWindow.cs (`ToggleVoiceAsync`, `StartLiveAsync`) | Первый 🎤 — системный запрос; отказ — Т-06; «больше не спрашивать» — Т-06 + «Открыть настройки». Windows — без запроса, как раньше. |
| **С-06** | Звонок на Android: фон, фокус, экран, лимит 20 мин, «Динамик» (7.2, 7.4 без видео) | AiAdvisorWindow.Video.cs (новый), AiAdvisorWindow.cs (`BuildLiveBar`, `StartLiveAsync`, `TickLive`, `EndLive`, `Closed`) | Звонок идёт, ИИ не слышит себя; свернуть → Т-12, вернуться < 60 с → продолжается; > 60 с → Т-12а; входящий звонок → Т-12б; экран не гаснет. |
| **С-07** | Видео в `GeminiLiveVoice` (6.3) + `LiveVideoPump` (6.4) | GeminiLiveVoice.cs, LiveVideoPump.cs (новый) | При `Video=false` setup и сообщения те же (сравнить JSON в стенде); имитация: расписание кадров по таблице 6.4, пропуск не-JPEG и > 150 КБ, отказ → `VideoRejected`. |
| **С-08** | Камера (6.1, 6.2) | PlatformCamera.cs (новый), Android/Platform/AndroidLiveCamera.cs (новый), AndroidBootstrap.cs | Камера открывается/закрывается, поворот верный стоя и боком, задняя/фронтальная, кадр ИИ ≤ 150 КБ; сканер открывается во время видео и после; ничего не пишется на диск (проверить `cache`/`files` аппарата). |
| **С-09** | Окно: кнопка/меню «Видео», согласие, предпросмотр, плашка, паузы, лимит 10 мин (7.1–7.6) | AiAdvisorWindow.Video.cs, AiAdvisorWindow.cs (стр. 274, 320–361, 420–437), AiAdvisorWindow.Capabilities.cs, UserPreferences.cs (`AdvisorVideoConsent`) | Телефон и планшет, стоя и боком, светлая и тёмная тема; диктор TalkBack читает имена; переход в другой раздел — камера гаснет, голос идёт. |
| **С-10** | Тумблер `voice.video` (9) | AdvisorCapabilities.cs | В «Настройки → Операции → Нур Советник» новый тумблер; выкл. — кнопок видео нет, кадры не уходят. Остальные тумблеры как были. |
| **С-11** | Уборка, журнал, регресс Windows, граф | все выше; `graphify update NurMarketKassa.Avalonia` из корня | Windows: 🎤, «Озвучить», «Разговор», «Перебить», «Завершить» — как в 1.17.73; Portable собирается; Android-чек-лист (12) пройден владельцем; отчёт исполнителя в `docs/Отчеты/`. |

**Имитационная проверка без аппарата** (вместо `dotnet test`): маленький консольный стенд в `C:\Users\User\NurMarket-test\` (не в репозитории), ссылка на Infrastructure:
1. подставной `IPcmMicrophone` (читает WAV 16 кГц кусками по 100 мс) и `IPcmSpeaker` (считает байты, `BufferedBytes` уменьшается по времени) → `PlatformAudio`; `GeminiLiveVoice { UseSpeaker = true }`: эхозащита — пока «динамик» не пуст, кусков микрофона в отправке нет;
2. `LiveVideoPump` с подставным `grab` и подставным состоянием — счёт кадров по таблице 6.4;
3. проверка JSON `Setup()` с `Video = false` — совпадает с текущим (сравнить строку).
Живой запрос к Gemini — только с разрешения владельца (тратит лимит его ключа); ключ не печатать.

---

## 12. Что проверить на реальном аппарате (владелец, планшет и телефон)

Журнал: `adb logcat -s NurMarket` или файл журнала программы владельца (`NurMarketOwner\Logs`).

**Голос**
1. Первый 🎤 → запрос права «Микрофон» → «Разрешить» → говорите → ■ → вопрос распознан, ответ появился и прозвучал вслух.
2. Отказ в праве → Т-06; повторный отказ с «Больше не спрашивать» → Т-06 и «Открыть настройки» открывает настройки NurCRM.
3. «Разговор»: советник отвечает голосом, не перебивает сам себя (громкая связь на столе); «Перебить» — замолкает сразу.
4. Наушники проводные/Bluetooth — звук в наушниках (Bluetooth на Android 11 и старше — может идти в динамик, это ожидаемо).
5. Во время разговора позвонить на телефон → разговор с ИИ завершается (Т-12б), после звонка звук программы нормальный.
6. Свернуть программу на 20 с и вернуться → разговор продолжается; на 2 минуты → завершён (Т-12а).
7. Экран не гаснет во время разговора; громкость меняется кнопками.
8. Ссылка «Озвучить» под ответом — читает вслух; новый вопрос обрывает прежнюю озвучку.

**Видео** (только на аппарате с камерой)
9. Планшет боком — кнопка «Видео»; телефон — «Разговор» → меню «Голосом / С камерой». Аппарат без камеры — ни кнопки, ни пункта.
10. Первый раз — карточка согласия; «Не сейчас» — камера не включилась; «Включить камеру» → запрос права «Камера» → предпросмотр, плашка «● ИИ видит камеру».
11. Показать товар/ценник: советник называет, что видит. Сменить камеру — фронтальная зеркальная в предпросмотре.
12. Повернуть аппарат — предпросмотр не перевёрнут, советник «видит» правильно.
13. Выключить камеру кнопкой — плашка пропала, зелёная точка Android погасла, голос идёт.
14. Перейти в другой раздел программы владельца → камера гаснет (зелёная точка), разговор идёт; вернуться — камера снова.
15. Во время видео открыть сканер штрихкодов (кнопка камеры в разделе склада) → сканер работает; вернуться — видео снова.
16. Через 9 минут — Т-11 и «Продлить»; без продления на 10-й минуте камера гаснет (Т-11б), разговор идёт.
17. «Завершить» → зелёная точка и значок микрофона Android гаснут сразу.
18. Расход: в настройках Android «Передача данных» — примерно 4–6 МБ за минуту видео (сообщить фактическое число).
19. Тумблер «Видеозвонок с камерой (Android)» выключить — кнопок видео нет.

**Регресс Windows** (на этом ПК): 🎤, «Озвучить», «Разговор», «Перебить», «Завершить», 3 мин тишины — как раньше; кнопки «Видео» на Windows нет.

---

## 13. Решения владельца (по умолчанию — рекомендованное, работа не ждёт ответа)

- **Р-1.** Отдельный тумблер «Видеозвонок с камерой (Android)» — да (рекомендую; иначе видео выключается только вместе со звонком).
- **Р-2.** Мягкие лимиты на Android: звонок 20 мин, видео 10 мин, оба с «Продлить на 10 минут» — да. На Windows лимитов не добавляем.
- **Р-3.** Свёрнутая программа: пауза 60 с, потом конец (рекомендую) — или конец сразу, как в мобилке.
- **Р-4.** Перебивание голосом (как в мобилке, благодаря эхоподавлению Android) — не сейчас, отдельной задачей после живой проверки эха на планшете.
- **Р-5.** Видео на Windows с веб-камерой — не в этой работе (на Windows нет кода камеры; понадобилась бы новая зависимость или WinRT-обёртка — отдельное решение).
