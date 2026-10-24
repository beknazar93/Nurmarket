import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

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
  const LiveMessage({
    this.setupComplete = false,
    this.audio = const [],
    this.inputText,
    this.outputText,
    this.interrupted = false,
    this.turnComplete = false,
    this.goAway = false,
    this.resumeHandle,
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

  final String apiKey;
  final String instruction;
  final LiveConnect connect;
  final LiveAudio audio;
  final Duration maxDuration;
  final Duration warnBefore;

  final state = ValueNotifier<CallState>(CallState.connecting);
  final level = ValueNotifier<double>(0);
  bool muted = false;
  bool speaker;
  String? model;
  void Function(String text)? onUserText;
  void Function(String text)? onAiText;
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
  DateTime _playEnd = DateTime(2000);
  DateTime _lastVoice = DateTime(2000);
  final started = DateTime.now();

  /// Порог громкости (0…1): выше — голос; пока ИИ говорит, перебить можно только голосом громче [bargeIn].
  static const voice = 0.05;
  static const bargeIn = 0.16;

  bool get ended => _ended;

  static Map<String, dynamic> setupJson(String model, String instruction, String? resume) => {
    'setup': {
      'model': 'models/$model',
      'generationConfig': {
        'responseModalities': ['AUDIO'],
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
    final list = [?_workingModel, ...models.where((m) => m != _workingModel)];
    String? last;
    for (final m in list) {
      if (_ended) return false;
      final LiveSocket ws;
      try {
        ws = await connect(Uri.parse(endpoint), {'x-goog-api-key': apiKey});
      } catch (_) {
        last = 'нет связи с Google';
        break; // нет интернета — другие модели не помогут
      }
      if (_ended) {
        await ws.close();
        return false;
      }
      final opening = Completer<bool>();
      _opening = opening;
      _ws = ws;
      _wsSub = ws.frames.listen((f) => _onFrame(ws, f), onDone: () => _onSocketDone(ws), onError: (_) {}, cancelOnError: false);
      ws.send(jsonEncode(setupJson(m, instruction, _resume)));
      final ok = await opening.future.timeout(const Duration(seconds: 15), onTimeout: () => false);
      if (ok) {
        model = m;
        _workingModel = m;
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
      if (last.contains('API key') || last.contains('API_KEY')) break; // ключ не подходит — модели не помогут
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

  void _onFrame(LiveSocket ws, Object? frame) {
    if (ws != _ws || _ended) return;
    final m = LiveMessage.parse(frame);
    if (m == null) return;
    if (m.setupComplete) {
      final opening = _opening;
      if (opening != null && !opening.isCompleted) opening.complete(true);
    }
    if (m.resumeHandle != null) _resume = m.resumeHandle;
    if (m.interrupted) {
      unawaited(audio.flush());
      _modelTurn = false;
      _drop = false;
      _playEnd = DateTime.now();
      onTurnDone?.call(true);
    }
    if (m.inputText != null) {
      _heard = true;
      onUserText?.call(m.inputText!);
    }
    if (!_drop) {
      for (final pcm in m.audio) {
        _modelTurn = true;
        _heard = false;
        final now = DateTime.now();
        final from = _playEnd.isAfter(now) ? _playEnd : now;
        _playEnd = from.add(Duration(microseconds: pcm.length * 1000000 ~/ 48000)); // 24 кГц × 2 байта
        unawaited(audio.play(pcm));
      }
      if (m.outputText != null) onAiText?.call(m.outputText!);
    }
    if (m.turnComplete) {
      final dropped = _drop;
      _modelTurn = false;
      _drop = false;
      _heard = false;
      if (!dropped) onTurnDone?.call(false);
    }
    _updateState();
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
      onTurnDone?.call(true);
    }
    if (rms > voice) _lastVoice = now;
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
    state.value = CallState.ended;
    level.value = 0;
    onEnded?.call(why, detail);
  }

  @visibleForTesting
  static void resetForTest() => _workingModel = null;
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
