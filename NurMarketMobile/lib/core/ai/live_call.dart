import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import 'live_tools.dart';

/// 2026-10-26 (В4, замер расхода): `usageMetadata` сообщения сервера Live API — только поля, которые есть в документации
/// (UsageMetadata): promptTokenCount, responseTokenCount, totalTokenCount, promptTokensDetails / responseTokensDetails
/// (список {modality, tokenCount}; modality — TEXT, AUDIO, IMAGE, VIDEO). Чего нет в сообщении — 0.
class LiveUsage {
  final int total;
  final int prompt;
  final int response;
  final Map<String, int> promptDetails;
  final Map<String, int> responseDetails;
  const LiveUsage({this.total = 0, this.prompt = 0, this.response = 0, this.promptDetails = const {}, this.responseDetails = const {}});

  static Map<String, int> _details(Object? v) {
    final out = <String, int>{};
    if (v is List) {
      for (final d in v) {
        if (d is! Map) continue;
        final m = '${d['modality'] ?? ''}'.toUpperCase();
        final n = d['tokenCount'];
        if (m.isNotEmpty && n is num) out[m] = (out[m] ?? 0) + n.toInt();
      }
    }
    return out;
  }

  static int _n(Object? v) => v is num ? v.toInt() : 0;

  static LiveUsage? fromJson(Object? j) {
    if (j is! Map) return null;
    return LiveUsage(
      total: _n(j['totalTokenCount']),
      prompt: _n(j['promptTokenCount']),
      response: _n(j['responseTokenCount']),
      promptDetails: _details(j['promptTokensDetails']),
      responseDetails: _details(j['responseTokensDetails']),
    );
  }
}

/// Накопитель расхода за звонок. Сервер присылает usageMetadata по ходу разговора; [total] — сумма totalTokenCount по сообщениям
/// (если сервер шлёт нарастающие значения, число завышено — смотрите [peak]), [start] — первое сообщение (описания инструментов
/// и данные магазина), [peak] — наибольшее одно сообщение (то, что видно как пик в AI Studio).
class LiveUsageTotals {
  int messages = 0;
  int total = 0;
  int start = 0;
  int peak = 0;
  int video = 0;
  int audio = 0;
  int text = 0;

  void add(LiveUsage u) {
    if (messages == 0) start = u.total;
    messages++;
    total += u.total;
    if (u.total > peak) peak = u.total;
    for (final d in [u.promptDetails, u.responseDetails]) {
      video += (d['VIDEO'] ?? 0) + (d['IMAGE'] ?? 0);
      audio += d['AUDIO'] ?? 0;
      text += d['TEXT'] ?? 0;
    }
  }

  /// Одна строка итога (без ключей и без текста речи).
  String get line => 'токены: всего $total, на старте $start, пик $peak, видео $video, звук $audio, текст $text (сообщений $messages)';
}

/// 2026-10-22 (звонок ИИ мобилки): одно сообщение сервера Gemini Live (BidiGenerateContent). Разбор отдельно от звонка —
/// его проверяют тесты. Поля — как в документации Live API и как их читает GeminiLiveVoice на ПК.
class LiveMessage {
  final bool setupComplete;
  final List<Uint8List> audio;
  final String? inputText;
  final String? outputText;
  final bool interrupted;
  final bool turnComplete;
  final bool goAway;
  final String? resumeHandle;

  /// 2026-10-26 (голос-управление, В0): вызовы инструментов (toolCall.functionCalls) и отмена (toolCallCancellation.ids).
  final List<LiveToolCall> toolCalls;
  final List<String> toolCancelIds;

  /// 2026-10-26 (В4): расход токенов (usageMetadata), если пришёл.
  final LiveUsage? usage;
  const LiveMessage({
    this.setupComplete = false,
    this.audio = const [],
    this.inputText,
    this.outputText,
    this.interrupted = false,
    this.turnComplete = false,
    this.goAway = false,
    this.resumeHandle,
    this.toolCalls = const [],
    this.toolCancelIds = const [],
    this.usage,
  });

  /// Кадр WebSocket (текст или байты JSON) → сообщение; не JSON — null.
  static LiveMessage? parse(Object? frame) {
    final String text;
    if (frame is String) {
      text = frame;
    } else if (frame is List<int>) {
      text = utf8.decode(frame, allowMalformed: true);
    } else {
      return null;
    }
    final Object? root;
    try {
      root = jsonDecode(text);
    } catch (_) {
      return null;
    }
    if (root is! Map) return null;
    final content = root['serverContent'] is Map ? root['serverContent'] as Map : const {};
    final audio = <Uint8List>[];
    final turn = content['modelTurn'];
    final parts = turn is Map ? turn['parts'] : null;
    if (parts is List) {
      for (final p in parts) {
        final inline = p is Map ? p['inlineData'] : null;
        final data = inline is Map ? inline['data'] : null;
        final mime = inline is Map ? '${inline['mimeType'] ?? ''}' : '';
        if (data is String && data.isNotEmpty && (mime.isEmpty || mime.startsWith('audio/'))) {
          try {
            audio.add(base64Decode(data));
          } catch (_) {
            // испорченный кусок звука — пропускаем
          }
        }
      }
    }
    String? say(Object? t) {
      final v = t is Map ? t['text'] : null;
      return v is String && v.isNotEmpty ? v : null;
    }

    final resume = root['sessionResumptionUpdate'];
    final toolCalls = <LiveToolCall>[];
    final tc = root['toolCall'];
    final fcs = tc is Map ? tc['functionCalls'] : null;
    if (fcs is List) {
      for (final f in fcs) {
        final c = LiveToolCall.fromJson(f);
        if (c != null) toolCalls.add(c);
      }
    }
    final cancelIds = <String>[];
    final tcc = root['toolCallCancellation'];
    final ids = tcc is Map ? tcc['ids'] : null;
    if (ids is List) {
      for (final i in ids) {
        if (i is String) cancelIds.add(i);
      }
    }
    return LiveMessage(
      setupComplete: root.containsKey('setupComplete'),
      audio: audio,
      inputText: say(content['inputTranscription']),
      outputText: say(content['outputTranscription']),
      interrupted: content['interrupted'] == true,
      turnComplete: content['turnComplete'] == true,
      goAway: root.containsKey('goAway'),
      resumeHandle: resume is Map && resume['resumable'] == true && resume['newHandle'] is String && (resume['newHandle'] as String).isNotEmpty
          ? resume['newHandle'] as String
          : null,
      toolCalls: toolCalls,
      toolCancelIds: cancelIds,
      usage: LiveUsage.fromJson(root['usageMetadata']),
    );
  }
}

/// Соединение WebSocket (настоящее — dart:io; в тестах — фейк).
abstract class LiveSocket {
  Stream<Object?> get frames;
  void send(String text);
  Future<void> close();
  int? get closeCode;
  String? get closeReason;
}

typedef LiveConnect = Future<LiveSocket> Function(Uri uri, Map<String, String> headers);

class _IoSocket implements LiveSocket {
  final WebSocket ws;
  _IoSocket(this.ws);
  @override
  Stream<Object?> get frames => ws;
  @override
  void send(String text) {
    if (ws.readyState == WebSocket.open) ws.add(text);
  }

  @override
  Future<void> close() => ws.close();
  @override
  int? get closeCode => ws.closeCode;
  @override
  String? get closeReason => ws.closeReason;
}

/// Настоящее подключение: ключ — заголовком x-goog-api-key, не в адресе (как GeminiLiveVoice на ПК и официальный SDK).
Future<LiveSocket> ioLiveConnect(Uri uri, Map<String, String> headers) async {
  final ws = await WebSocket.connect(uri.toString(), headers: headers).timeout(const Duration(seconds: 15));
  ws.pingInterval = const Duration(seconds: 20);
  return _IoSocket(ws);
}

/// Звук звонка: микрофон (куски PCM 16 кГц) и динамик (PCM 24 кГц). Настоящий — VoiceCallAudio.kt; в тестах — фейк.
abstract class LiveAudio {
  /// Uint8List — кусок звука с микрофона; Map {'event': 'focusLoss'} — входящий звонок или другое приложение со звуком.
  Stream<Object?> get events;
  Future<bool> start({required bool speaker});
  Future<void> play(Uint8List pcm);
  Future<void> flush();
  Future<void> setMuted(bool muted);
  Future<void> setSpeaker(bool on);
  Future<void> stop();
}

class PlatformLiveAudio implements LiveAudio {
  static const _ch = MethodChannel('kg.nurcrm.nurmarket/voice');
  static const _ev = EventChannel('kg.nurcrm.nurmarket/voice/events');

  /// granted / denied / blocked (отказано навсегда — только «Открыть настройки»).
  static Future<String> micPermission() async => await _ch.invokeMethod<String>('micPermission') ?? 'denied';
  static Future<String> requestMic() async => await _ch.invokeMethod<String>('requestMic') ?? 'denied';
  static Future<void> openSettings() => _ch.invokeMethod('openSettings');

  @override
  Stream<Object?> get events => _ev.receiveBroadcastStream();
  @override
  Future<bool> start({required bool speaker}) async => await _ch.invokeMethod<bool>('start', {'speaker': speaker}) ?? false;
  @override
  Future<void> play(Uint8List pcm) => _ch.invokeMethod('play', pcm);
  @override
  Future<void> flush() => _ch.invokeMethod('flush');
  @override
  Future<void> setMuted(bool muted) => _ch.invokeMethod('mute', {'muted': muted});
  @override
  Future<void> setSpeaker(bool on) => _ch.invokeMethod('speaker', {'on': on});
  @override
  Future<void> stop() => _ch.invokeMethod('stop');
}

enum CallState { connecting, listening, thinking, speaking, ended }

/// 2026-10-26 (видеозвонок, В1): проводные константы видео Gemini Live — в одном месте, чтобы поправить после проверки на
/// устройстве (раздел 12 ТЗ). Имя поля кадра, mime, mediaResolution и признаки «модель не принимает видео» сверены только
/// с документацией Live API, живого запроса не было.
class LiveVideoWire {
  /// realtimeInput.[videoField] = {data, mimeType}. Старое mediaChunks не используется.
  static const videoField = 'video';
  static const mime = 'image/jpeg';

  /// setup.generationConfig.mediaResolution: LOW — меньше токенов на кадр (порядка 66 вместо 258 — проверить).
  static const mediaResolutionKey = 'mediaResolution';
  static const mediaResolution = 'MEDIA_RESOLUTION_LOW';

  /// Кадр тяжелее — пропускается (skippedFrames).
  static const maxFrameBytes = 150 * 1024;

  /// Соединение закрыто не позже чем через это время после первого кадра с такой причиной — модель видео не принимает.
  static const rejectWindow = Duration(seconds: 5);
  static const rejectHints = ['video', 'image', 'mime', 'unsupported', 'media', 'resolution', 'modality'];

  /// 1007 (invalid argument) в окне [rejectWindow] после первого кадра — тоже отказ, даже без причины.
  static const rejectCodes = {1007};
  static const quotaHints = ['resource_exhausted', 'quota', 'exhausted', 'rate limit', 'rate_limit'];

  static bool isVideoReject(String reason) {
    final r = reason.toLowerCase();
    return rejectHints.any(r.contains);
  }

  static bool isQuota(String reason) {
    final r = reason.toLowerCase();
    return quotaHints.any(r.contains);
  }

  /// Кадр — настоящий JPEG (FF D8).
  static bool isJpeg(Uint8List b) => b.length > 3 && b[0] == 0xFF && b[1] == 0xD8;
}

/// Почему звонок закончился: user — нажали «Завершить»; limit — вышло время; focus — входящий звонок, сворачивание;
/// error — [detail] (нет связи, ключ, модель).
enum CallEnd { user, limit, focus, error }

/// 2026-10-22 (звонок ИИ мобилки): голосовой звонок с Нур Советником через Gemini Live, как GeminiLiveVoice на ПК:
/// WebSocket BidiGenerateContent, setup (модель, системная инструкция с данными магазина, расшифровка обеих сторон, сжатие
/// контекста, возобновление сеанса), микрофон — realtimeInput audio/pcm;rate=16000, ответ — PCM 24 кГц.
/// Отличие от ПК: перебивание голосом. Пока ИИ говорит, тихий звук с микрофона (эхо динамика) в Google не уходит, а громкий
/// голос — уходит, и ответ сразу замолкает (Google присылает interrupted). На телефоне ещё работает подавление эха.
class LiveCall {
  static const models = ['gemini-3.8-live', 'gemini-3.1-flash-live-preview', 'gemini-2.5-flash-native-audio-latest'];
  static const endpoint = 'wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent';
  static String? _workingModel;
  static String? _workingVideoModel;

  /// 2026-10-26 (В4, лимиты бесплатного ключа): видеозвонок начинает с gemini-2.5 Flash Native Audio (запросов без ограничений,
  /// 1 млн токенов в минуту), а не с Gemini 3 Flash Live (65 тыс. токенов в минуту — одного подключения с описаниями инструментов
  /// и данными магазина хватает на половину). Не принимает видео — прежняя логика откатывается на голос и следующие модели.
  static const videoFirst = 'gemini-2.5-flash-native-audio-latest';

  /// Порядок моделей для звонка: для видео — [videoFirst] первой; запомненная рабочая модель (отдельная для видео) — впереди всех.
  static List<String> orderFor({required bool video, String? working}) {
    final base = video ? [videoFirst, ...models.where((m) => m != videoFirst)] : models;
    final w = working ?? (video ? _workingVideoModel : _workingModel);
    return [?w, ...base.where((m) => m != w)];
  }

  // 2026-10-27 (дополнительные ключи Gemini): ключ можно сменить на следующий, если Google отказал ему ДО начала сеанса.
  String apiKey;
  final String instruction;
  final LiveConnect connect;
  final LiveAudio audio;
  final Duration maxDuration;
  final Duration warnBefore;

  /// 2026-10-26 (голос-управление, В0): объявления инструментов (functionDeclarations) и обработчик вызовов. Без [tools] setup
  /// и поведение звонка такие же, как раньше. Инструмент обязан ответить за [toolTimeout], иначе модели уйдёт `pending`
  /// (она молчит, пока ждёт ответа), а итог придёт позже текстом `[Программа] Готово: …`.
  final List<Map<String, dynamic>>? tools;
  final ToolHandler? onToolCall;
  final Duration toolTimeout;

  /// 2026-10-26 (видеозвонок, В1): звонок умеет принимать кадры камеры (setup с mediaResolution, [sendFrame]). Без video
  /// всё как раньше. [videoActive] гаснет, если модель видео не приняла: звонок продолжается голосом.
  final bool video;
  late final videoActive = ValueNotifier<bool>(video);

  /// Модель отвергла видео: причина закрытия (без ключа и без кадров).
  void Function(String reason)? onVideoRejected;

  /// Одна строка итогов видео при конце звонка (кадры, байты, пропуски) — для журнала; без кадров и без ключа.
  void Function(String line)? onVideoStats;
  int sentFrames = 0;
  int sentBytes = 0;
  int skippedFrames = 0;

  /// Конец звонка из-за лимита ключа ИИ (RESOURCE_EXHAUSTED / quota) — экран покажет совет про «Экономию».
  bool quotaExceeded = false;

  /// Растёт, когда человек начинает новую реплику (голос после паузы > 1,2 с) — по нему политика кадров шлёт свежий кадр.
  final voiceStart = ValueNotifier<int>(0);
  bool _videoRejected = false;
  DateTime? _firstFrameAt;

  /// Речь пользователя по репликам (расшифровка микрофона, метка эха) — по ней ворота намерения решают, была ли просьба.
  final speech = UserSpeechLog();

  /// 2026-10-26 (В4): расход токенов за звонок; итоговая строка — в [onUsageLine] и в журнал отладки при конце звонка.
  final usage = LiveUsageTotals();
  void Function(String line)? onUsageLine;

  /// false — модель отвергла `tools`, звонок переподключился без них (запасной путь, раздел 3.7 ТЗ).
  bool toolsSupported = true;

  /// Модель отвергла инструменты: причина закрытия (без ключа) — для журнала одной строкой.
  void Function(String reason)? onToolsRejected;

  /// Реплика пользователя целиком вместе с меткой эха (кроме [onUserText], который отдаёт куски как раньше).
  void Function(String text, bool echoSuspect)? onUserSpeech;

  final state = ValueNotifier<CallState>(CallState.connecting);
  final level = ValueNotifier<double>(0);
  bool muted = false;
  bool speaker;
  String? model;
  void Function(String text)? onUserText;
  void Function(String text)? onAiText;
  /// 2026-10-27: ключ отклонён при открытии соединения (лимит или «API key not valid») → следующий ключ из списка или null.
  /// Вызывается только до начала сеанса, поэтому та же модель пробуется заново с новым ключом без потери разговора.
  String? Function(String failedKey, String reason)? onKeyRejected;
  void Function(bool interrupted)? onTurnDone;
  void Function()? onWarning;
  void Function(CallEnd why, String? detail)? onEnded;

  LiveCall({
    required this.apiKey,
    required this.instruction,
    required this.audio,
    LiveConnect? connect,
    this.maxDuration = const Duration(minutes: 10),
    this.warnBefore = const Duration(minutes: 1),
    this.speaker = true,
    this.tools,
    this.onToolCall,
    this.toolTimeout = const Duration(seconds: 8),
    this.video = false,
  }) : connect = connect ?? ioLiveConnect;

  LiveSocket? _ws;
  StreamSubscription<Object?>? _wsSub;
  StreamSubscription<Object?>? _audioSub;
  Completer<bool>? _opening;
  Timer? _limit;
  Timer? _warn;
  Timer? _tick;
  String? _resume;
  bool _ended = false;
  bool _audioStarted = false;
  bool _modelTurn = false;
  bool _drop = false;
  bool _heard = false;
  int _reconnects = 0;
  final _toolCache = ToolResponseCache();
  final _toolCalls = <String, LiveToolCall>{};
  Future<void> _toolQueue = Future<void>.value();
  // Эхо: сколько «голосовых» кусков микрофона ушло в Google пока ИИ говорил и пока молчал (в текущей реплике).
  int _voicedWhileSpeaking = 0;
  int _voicedWhileIdle = 0;
  DateTime _playEnd = DateTime(2000);
  DateTime _lastVoice = DateTime(2000);
  final started = DateTime.now();

  /// Порог громкости (0…1): выше — голос; пока ИИ говорит, перебить можно только голосом громче [bargeIn].
  static const voice = 0.05;
  static const bargeIn = 0.16;

  bool get ended => _ended;

  /// Когда в последний раз был голос пользователя и до какого момента играет ответ ИИ (для политики кадров).
  DateTime get lastVoiceAt => _lastVoice;
  DateTime get aiSoundUntil => _playEnd;

  /// [tools] — объявления функций (ToolDecl.toJson); без них в setup ничего нового (старое поведение).
  /// [video] — добавить в generationConfig mediaResolution LOW (2026-10-26, видеозвонок); без него setup прежний.
  static Map<String, dynamic> setupJson(String model, String instruction, String? resume, {List<Map<String, dynamic>>? tools, bool video = false}) => {
    'setup': {
      'model': 'models/$model',
      'generationConfig': {
        'responseModalities': ['AUDIO'],
        if (video) LiveVideoWire.mediaResolutionKey: LiveVideoWire.mediaResolution,
      },
      'systemInstruction': {
        'parts': [
          {'text': instruction},
        ],
      },
      // Расшифровка речи обеих сторон — субтитры и текст в чате после звонка.
      'inputAudioTranscription': <String, dynamic>{},
      'outputAudioTranscription': <String, dynamic>{},
      // Без сжатия контекста звуковой разговор ограничен ~15 минутами.
      'contextWindowCompression': {'slidingWindow': <String, dynamic>{}},
      'sessionResumption': resume == null ? <String, dynamic>{} : {'handle': resume},
      if (tools != null && tools.isNotEmpty) 'tools': [{'functionDeclarations': tools}],
    },
  };

  Future<void> start() async {
    _audioSub = audio.events.listen(_onAudioEvent, onError: (_) {});
    if (!await _open()) return;
    if (_ended) return;
    final ok = await audio.start(speaker: speaker);
    if (!ok) {
      _end(CallEnd.error, 'микрофон недоступен');
      return;
    }
    _audioStarted = true;
    _limit = Timer(maxDuration, () => _end(CallEnd.limit, null));
    if (maxDuration > warnBefore) _warn = Timer(maxDuration - warnBefore, () => onWarning?.call());
    _tick = Timer.periodic(const Duration(milliseconds: 150), (_) => _updateState());
  }

  Future<bool> _open() async {
    final list = orderFor(video: video && !_videoRejected);
    String? last;
    for (final m in list) {
      if (_ended) return false;
      // 2026-10-26 (голос-управление, В0): сначала с инструментами; если модель закрыла соединение до setupComplete не из-за
      // ключа и не потому, что модели нет, — та же модель ещё раз без tools (toolsSupported=false, запасной путь).
      var withTools = (tools?.isNotEmpty ?? false) && toolsSupported;
      var withVideo = video && !_videoRejected;
      var firstReason = '';
      var videoReason = '';
      while (true) {
        final LiveSocket ws;
        try {
          ws = await connect(Uri.parse(endpoint), {'x-goog-api-key': apiKey});
        } catch (_) {
          _end(CallEnd.error, 'нет связи с Google');
          return false; // нет интернета — другие модели не помогут
        }
        if (_ended) {
          await ws.close();
          return false;
        }
        final opening = Completer<bool>();
        _opening = opening;
        _ws = ws;
        _wsSub = ws.frames.listen((f) => _onFrame(ws, f), onDone: () => _onSocketDone(ws), onError: (_) {}, cancelOnError: false);
        ws.send(jsonEncode(setupJson(m, instruction, _resume, tools: withTools ? tools : null, video: withVideo)));
        final ok = await opening.future.timeout(const Duration(seconds: 15), onTimeout: () => false);
        if (ok) {
          if (!withTools && (tools?.isNotEmpty ?? false) && toolsSupported) {
            toolsSupported = false;
            onToolsRejected?.call(firstReason);
          }
          // 2026-10-26 (видеозвонок): модель не приняла setup с видео — звонок продолжается голосом.
          if (!withVideo && video && !_videoRejected) _rejectVideo(videoReason);
          model = m;
          if (withVideo) {
            _workingVideoModel = m;
          } else {
            _workingModel = m;
          }
          _firstFrameAt = null;
          state.value = CallState.listening;
          return true;
        }
        last = describeClose(ws.closeCode, ws.closeReason) ?? 'Google не ответил';
        await _wsSub?.cancel();
        _wsSub = null;
        _ws = null;
        await ws.close();
        // Ключ возобновления привязан к модели — с другой моделью разговор начинается заново.
        _resume = null;
        if (last.contains('API key') || last.contains('API_KEY') || LiveVideoWire.isQuota(last)) {
          // 2026-10-27: есть другой ключ — та же модель ещё раз с ним (без траты попыток на tools/video)
          final next = onKeyRejected?.call(apiKey, last);
          if (next != null && next != apiKey && !_ended) {
            apiKey = next;
            continue;
          }
        }
        if (last.contains('API key') || last.contains('API_KEY')) {
          _end(CallEnd.error, last); // ключ не подходит — модели не помогут
          return false;
        }
        final notFound = last.toLowerCase().contains('not found');
        // 2026-10-26 (видеозвонок): причина про видео — сначала отказываемся от видео (инструменты целы), иначе по-старому
        // сначала инструменты, потом видео.
        if (withVideo && !_ended && !notFound && (!withTools || LiveVideoWire.isVideoReject(last))) {
          withVideo = false;
          videoReason = last;
          continue;
        }
        if (withTools && !_ended && !notFound) {
          withTools = false;
          firstReason = last;
          continue;
        }
        if (withVideo && !_ended && !notFound) {
          withVideo = false;
          videoReason = last;
          continue;
        }
        break;
      }
    }
    _end(CallEnd.error, last ?? 'нет подходящей модели');
    return false;
  }

  static String? describeClose(int? code, String? reason) {
    final r = reason?.trim() ?? '';
    if (r.isNotEmpty) return r;
    return code == null || code == 1000 ? null : 'Google закрыл соединение (код $code)';
  }

  void _onSocketDone(LiveSocket ws) {
    if (ws != _ws) return;
    final opening = _opening;
    if (opening != null && !opening.isCompleted) {
      opening.complete(false);
      return;
    }
    if (_ended) return;
    // 2026-10-26 (видеозвонок): закрыли сразу после кадров — лимит ключа (звонок кончается) или модель не принимает видео
    // (переподключаемся без видео, число попыток не тратим).
    if (video && !_videoRejected && _firstFrameAt != null) {
      final reason = describeClose(ws.closeCode, ws.closeReason) ?? '';
      if (LiveVideoWire.isQuota(reason)) {
        _end(CallEnd.error, reason);
        return;
      }
      final fast = DateTime.now().difference(_firstFrameAt!) <= LiveVideoWire.rejectWindow;
      if (fast && (LiveVideoWire.isVideoReject(reason) || LiveVideoWire.rejectCodes.contains(ws.closeCode))) {
        _rejectVideo(reason);
        _ws = null;
        state.value = CallState.connecting;
        unawaited(_open());
        return;
      }
    }
    // Google закрывает соединение примерно раз в 10 минут (goAway) — продолжаем по ключу возобновления.
    if (_reconnects < 3) {
      _reconnects++;
      _ws = null;
      state.value = CallState.connecting;
      unawaited(_open());
    } else {
      _end(CallEnd.error, describeClose(ws.closeCode, ws.closeReason) ?? 'соединение с Google прервалось');
    }
  }

  void _rejectVideo(String reason) {
    _videoRejected = true;
    videoActive.value = false;
    onVideoRejected?.call(reason);
  }

  void _onFrame(LiveSocket ws, Object? frame) {
    if (ws != _ws || _ended) return;
    final m = LiveMessage.parse(frame);
    if (m == null) return;
    if (m.setupComplete) {
      final opening = _opening;
      if (opening != null && !opening.isCompleted) opening.complete(true);
    }
    if (m.resumeHandle != null) _resume = m.resumeHandle;
    if (m.usage != null) usage.add(m.usage!);
    if (m.interrupted) {
      unawaited(audio.flush());
      _modelTurn = false;
      _drop = false;
      _playEnd = DateTime.now();
      speech.closeTurn();
      onTurnDone?.call(true);
    }
    if (m.inputText != null) {
      _heard = true;
      onUserText?.call(m.inputText!);
      // 2026-10-26 (голос-управление, В0): реплика пользователя с меткой эха — для ворот намерения.
      final echo = _voicedWhileSpeaking > 0 && _voicedWhileIdle == 0;
      speech.add(m.inputText!, echoSuspect: echo);
      onUserSpeech?.call(m.inputText!, echo);
      final cur = speech.last?.text;
      if (cur != null && isSilenceRequest(cur)) interrupt(); // «замолчи / тише / стоп» — без ожидания модели
    }
    if (!_drop) {
      for (final pcm in m.audio) {
        _modelTurn = true;
        _heard = false;
        speech.closeTurn();
        final now = DateTime.now();
        final from = _playEnd.isAfter(now) ? _playEnd : now;
        _playEnd = from.add(Duration(microseconds: pcm.length * 1000000 ~/ 48000)); // 24 кГц × 2 байта
        unawaited(audio.play(pcm));
      }
      if (m.outputText != null) {
        speech.closeTurn();
        onAiText?.call(m.outputText!);
      }
    }
    if (m.turnComplete) {
      speech.closeTurn();
      final dropped = _drop;
      _modelTurn = false;
      _drop = false;
      _heard = false;
      if (!dropped) onTurnDone?.call(false);
    }
    for (final id in m.toolCancelIds) {
      _toolCalls[id]?.cancel.cancel();
    }
    if (m.toolCalls.isNotEmpty) _enqueueToolCalls(m.toolCalls);
    _updateState();
  }

  // ---- 2026-10-26 (голос-управление, В0): инструменты ------------------------------------------------------------------

  /// Вызовы выполняются по порядку (и между сообщениями), ответы одного toolCall уходят одним toolResponse.
  void _enqueueToolCalls(List<LiveToolCall> calls) {
    _toolQueue = _toolQueue.then((_) => _runToolCalls(calls)).catchError((Object _) {});
  }

  Future<void> _runToolCalls(List<LiveToolCall> calls) async {
    final out = <Map<String, dynamic>>[];
    for (final call in calls) {
      if (_ended) return;
      final known = _toolCache.get(call.id);
      if (call.id.isNotEmpty && known != null) {
        // Повтор того же id (после переподключения) — ответ из кэша, инструмент второй раз не выполняется.
        out.add({'id': call.id, 'name': call.name, 'response': known});
        continue;
      }
      if (call.id.isNotEmpty && _toolCache.isRunning(call.id)) continue; // этот вызов уже идёт — ответит он
      final handler = onToolCall;
      if (handler == null || tools == null || !toolsSupported) {
        out.add({
          'id': call.id,
          'name': call.name,
          'response': {'status': 'error', 'say': 'Tools are not available in this call'},
        });
        continue;
      }
      if (call.id.isNotEmpty) {
        _toolCache.markRunning(call.id);
        _toolCalls[call.id] = call;
      }
      final run = _callHandler(handler, call);
      // Срок ответа: не уложился — модели уходит pending, а итог придёт позже (таймер снимаем, чтобы не висел).
      final waiter = Completer<Map<String, dynamic>?>();
      final timer = Timer(toolTimeout, () {
        if (!waiter.isCompleted) waiter.complete(null);
      });
      unawaited(
        run.then((r) {
          if (!waiter.isCompleted) waiter.complete(r);
        }),
      );
      final first = await waiter.future;
      timer.cancel();
      final timedOut = first == null;
      final response = first ?? pendingResponse();
      if (call.cancel.isCancelled) {
        // Пользователь перебил: результат выбрасываем, ответа серверу нет.
        _toolCalls.remove(call.id);
        if (call.id.isNotEmpty) _toolCache.put(call.id, {'status': 'error', 'say': 'Cancelled'});
        continue;
      }
      if (call.id.isNotEmpty) _toolCache.put(call.id, response);
      out.add({'id': call.id, 'name': call.name, 'response': response});
      if (timedOut) {
        unawaited(
          run.then((late) {
            _toolCalls.remove(call.id);
            if (_ended || call.cancel.isCancelled) return;
            if (call.id.isNotEmpty) _toolCache.put(call.id, late);
            final say = '${late['say'] ?? ''}'.trim();
            final ok = late['status'] == 'ok';
            sendText('[Программа] ${ok ? 'Готово' : 'Не вышло'}: $say', respond: true);
          }),
        );
      } else {
        _toolCalls.remove(call.id);
      }
    }
    if (out.isNotEmpty) sendToolResponse(out);
  }

  /// Обработчик не должен ронять звонок: исключение → status error (без текста исключения: там бывают данные).
  Future<Map<String, dynamic>> _callHandler(ToolHandler handler, LiveToolCall call) async {
    try {
      return await handler(call);
    } catch (_) {
      return {'status': 'error', 'say': 'The tool failed'};
    }
  }

  /// Ответ(ы) инструментов: [{id, name, response}] → toolResponse.functionResponses.
  void sendToolResponse(List<Map<String, dynamic>> functionResponses) {
    final ws = _ws;
    if (ws == null || model == null || functionResponses.isEmpty) return;
    ws.send(jsonEncode({'toolResponse': {'functionResponses': functionResponses}}));
  }

  void _updateState() {
    if (_ended || state.value == CallState.connecting) return;
    final now = DateTime.now();
    final speaking = _modelTurn || now.isBefore(_playEnd);
    state.value = speaking
        ? CallState.speaking
        : (_heard && now.difference(_lastVoice) > const Duration(milliseconds: 700) ? CallState.thinking : CallState.listening);
  }

  void _onAudioEvent(Object? e) {
    if (_ended) return;
    if (e is Map) {
      if (e['event'] == 'focusLoss') _end(CallEnd.focus, null);
      return;
    }
    if (e is! Uint8List || muted) return;
    var sum = 0.0;
    final n = e.length ~/ 2;
    final data = ByteData.sublistView(e);
    for (var i = 0; i < n; i++) {
      final s = data.getInt16(i * 2, Endian.little) / 32768.0;
      sum += s * s;
    }
    final rms = n == 0 ? 0.0 : math.min(1.0, math.sqrt(sum / n) * 6);
    level.value = rms;
    final now = DateTime.now();
    final speaking = _modelTurn || now.isBefore(_playEnd.add(const Duration(milliseconds: 300)));
    if (speaking) {
      if (rms < bargeIn) return; // эхо динамика — в Google не уходит
      // Перебили голосом: ответ замолкает сразу, не дожидаясь Google.
      unawaited(audio.flush());
      _playEnd = now;
      _drop = true;
      _modelTurn = false;
      speech.closeTurn();
      onTurnDone?.call(true);
    }
    if (rms > voice) {
      // 2026-10-26 (голос-управление, В0): пауза > 1,2 с — новая реплика, счётчики эха заново. Реплика — эхо, если весь её
      // голос ушёл в Google, пока ИИ говорил (громкий кусок эха сквозь порог перебивания), и ни одного — пока он молчал.
      if (now.difference(_lastVoice) > const Duration(milliseconds: 1200)) {
        _voicedWhileSpeaking = 0;
        _voicedWhileIdle = 0;
        voiceStart.value++; // 2026-10-26 (видеозвонок): начало речи — свежий кадр
      }
      if (speaking) {
        _voicedWhileSpeaking++;
      } else {
        _voicedWhileIdle++;
      }
      _lastVoice = now;
    }
    _sendAudio(e);
  }

  void _sendAudio(Uint8List pcm) {
    final ws = _ws;
    if (ws == null || model == null || state.value == CallState.connecting) return;
    ws.send(
      jsonEncode({
        'realtimeInput': {
          'audio': {'data': base64Encode(pcm), 'mimeType': 'audio/pcm;rate=16000'},
        },
      }),
    );
  }

  /// 2026-10-26 (видеозвонок, В1): кадр камеры (JPEG) в тот же сокет: realtimeInput.video. Уходит только при готовом соединении
  /// (после setupComplete, не при переподключении и не после конца звонка), если видео не отвергнуто моделью. Не JPEG или
  /// тяжелее 150 КБ — пропуск (skippedFrames). Кадр нигде не сохраняется. true — отправлен.
  bool sendFrame(Uint8List jpeg) {
    final ws = _ws;
    if (_ended || ws == null || model == null || state.value == CallState.connecting) return false;
    if (!video || !videoActive.value) return false;
    if (!LiveVideoWire.isJpeg(jpeg) || jpeg.length > LiveVideoWire.maxFrameBytes) {
      skippedFrames++;
      return false;
    }
    ws.send(
      jsonEncode({
        'realtimeInput': {
          LiveVideoWire.videoField: {'data': base64Encode(jpeg), 'mimeType': LiveVideoWire.mime},
        },
      }),
    );
    _firstFrameAt ??= DateTime.now();
    sentFrames++;
    sentBytes += jpeg.length;
    return true;
  }

  /// Итоги видео одной строкой (без кадров и без ключа).
  String get videoStatsLine =>
      'видеозвонок: кадров $sentFrames, ${sentBytes ~/ 1024} КБ, пропущено $skippedFrames${_videoRejected ? ', модель видео не приняла' : ''}';

  /// Текст от программы в разговор (таблица по вопросу и т. п.); respond=false — только запомнить, вслух не отвечать.
  void sendText(String text, {bool respond = false}) {
    final ws = _ws;
    if (ws == null || model == null || text.trim().isEmpty) return;
    ws.send(
      jsonEncode({
        'clientContent': {
          'turns': [
            {
              'role': 'user',
              'parts': [
                {'text': text},
              ],
            },
          ],
          'turnComplete': respond,
        },
      }),
    );
  }

  /// Перебить кнопкой: ответ замолкает, остаток ответа не играется.
  void interrupt() {
    if (!_modelTurn && !DateTime.now().isBefore(_playEnd)) return;
    unawaited(audio.flush());
    _playEnd = DateTime.now();
    _drop = _modelTurn;
    _modelTurn = false;
    onTurnDone?.call(true);
    _updateState();
  }

  Duration _extra = Duration.zero;

  /// Полный срок звонка с продлением.
  Duration get totalDuration => maxDuration + _extra;

  /// 2026-10-26 (голос-управление, В4): «Ещё 10 минут» (вопрос 6 ТЗ) — срок звонка растёт, предупреждение за минуту
  /// переставляется. Звонок ещё не начался или уже кончился — ничего не делает.
  void extend(Duration by) {
    if (_ended || !_audioStarted || by <= Duration.zero) return;
    _extra += by;
    _limit?.cancel();
    _warn?.cancel();
    final left = totalDuration - DateTime.now().difference(started);
    _limit = Timer(left.isNegative ? Duration.zero : left, () => _end(CallEnd.limit, null));
    if (left > warnBefore) _warn = Timer(left - warnBefore, () => onWarning?.call());
  }

  Future<void> setMuted(bool value) async {
    muted = value;
    if (value) level.value = 0;
    if (_audioStarted) await audio.setMuted(value);
  }

  Future<void> setSpeaker(bool value) async {
    speaker = value;
    if (_audioStarted) await audio.setSpeaker(value);
  }

  /// Завершить: [why] — user (кнопка) или focus (свернули приложение).
  void end([CallEnd why = CallEnd.user]) => _end(why, null);

  void _end(CallEnd why, String? detail) {
    if (_ended) return;
    _ended = true;
    for (final c in _toolCalls.values) {
      c.cancel.cancel(); // незаконченные вызовы инструментов: результат выбросить
    }
    _limit?.cancel();
    _warn?.cancel();
    _tick?.cancel();
    final opening = _opening;
    if (opening != null && !opening.isCompleted) opening.complete(false);
    unawaited(_wsSub?.cancel());
    final ws = _ws;
    _ws = null;
    if (ws != null) unawaited(ws.close().catchError((_) {}));
    unawaited(_audioSub?.cancel());
    if (_audioStarted) unawaited(audio.stop().catchError((_) {}));
    if (why == CallEnd.error && detail != null && LiveVideoWire.isQuota(detail)) quotaExceeded = true;
    state.value = CallState.ended;
    level.value = 0;
    if (usage.messages > 0) {
      if (kDebugMode) debugPrint('[assistant] ${usage.line}');
      onUsageLine?.call(usage.line);
    }
    if (video) {
      final line = videoStatsLine;
      if (kDebugMode) debugPrint(line);
      onVideoStats?.call(line);
    }
    onEnded?.call(why, detail);
  }

  @visibleForTesting
  static void resetForTest() {
    _workingModel = null;
    _workingVideoModel = null;
  }
}

/// 2026-10-22 (звонок ИИ мобилки): вопрос с цифрами (как LooksLikeDataQuestion на ПК) — ответ дублируется таблицей в чат.
bool looksLikeDataQuestion(String said) {
  final t = said.toLowerCase();
  if (RegExp(r'\d').hasMatch(t)) return true;
  const words = [
    'сколько', 'выручк', 'продал', 'продаж', 'прибыл', 'долг', 'должн', 'остат', 'заканчива', 'чек', 'сумм', 'итог', 'неделю', 'недел',
    'месяц', 'вчера', 'сегодня', 'табли', 'топ', 'лучш', 'заказ', 'канча', 'сатуу', 'карыз', 'киреше', 'how much', 'revenue', 'sales', 'debt',
    'ne kadar', 'satış', 'borç', 'qancha', 'sotuv', 'qarz',
  ];
  return words.any(t.contains);
}
