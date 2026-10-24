// 2026-10-22 (звонок ИИ мобилки): голосовой звонок с Нур Советником на фейках (без сети и без микрофона):
// разбор сообщений Gemini Live, ключ — заголовком (не в адресе), запасная модель, звук, перебивание, завершение по таймеру.
import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/live_call.dart';

class _FakeSocket implements LiveSocket {
  final ctrl = StreamController<Object?>();
  final sent = <Map<String, dynamic>>[];
  @override
  int? closeCode;
  @override
  String? closeReason;
  bool closed = false;
  @override
  Stream<Object?> get frames => ctrl.stream;
  @override
  void send(String text) => sent.add(jsonDecode(text) as Map<String, dynamic>);
  @override
  Future<void> close() async {
    closed = true;
    if (!ctrl.isClosed) await ctrl.close();
  }

  void push(Map<String, dynamic> m) => ctrl.add(jsonEncode(m));
  void pushBytes(Map<String, dynamic> m) => ctrl.add(utf8.encode(jsonEncode(m)));
}

class _FakeAudio implements LiveAudio {
  final ctrl = StreamController<Object?>.broadcast();
  final played = <Uint8List>[];
  int flushes = 0;
  bool started = false;
  bool stopped = false;
  @override
  Stream<Object?> get events => ctrl.stream;
  @override
  Future<bool> start({required bool speaker}) async => started = true;
  @override
  Future<void> play(Uint8List pcm) async => played.add(pcm);
  @override
  Future<void> flush() async => flushes++;
  @override
  Future<void> setMuted(bool muted) async {}
  @override
  Future<void> setSpeaker(bool on) async {}
  @override
  Future<void> stop() async => stopped = true;
}

/// Кусок «голоса» 100 мс: громкость [amp] 0…1.
Uint8List _pcm(double amp) {
  final b = ByteData(3200);
  for (var i = 0; i < 1600; i++) {
    b.setInt16(i * 2, ((i.isEven ? 1 : -1) * amp * 32767 / 6).round(), Endian.little);
  }
  return b.buffer.asUint8List();
}

Map<String, dynamic> _audioMsg(List<int> pcm) => {
  'serverContent': {
    'modelTurn': {
      'parts': [
        {
          'inlineData': {'mimeType': 'audio/pcm;rate=24000', 'data': base64Encode(pcm)},
        },
      ],
    },
  },
};

Future<void> _settle() => Future<void>.delayed(const Duration(milliseconds: 20));

void main() {
  setUp(LiveCall.resetForTest);

  group('разбор сообщений Live API', () {
    test('setupComplete, звук, расшифровка, turnComplete, interrupted, возобновление', () {
      expect(LiveMessage.parse('{"setupComplete":{}}')!.setupComplete, isTrue);
      final a = LiveMessage.parse(jsonEncode(_audioMsg([1, 2, 3, 4])))!;
      expect(a.audio.single, [1, 2, 3, 4]);
      expect(a.setupComplete, isFalse);
      final t = LiveMessage.parse(
        jsonEncode({
          'serverContent': {
            'inputTranscription': {'text': 'Сколько продали вчера?'},
            'outputTranscription': {'text': 'Вчера — 12 тысяч сом.'},
            'turnComplete': true,
          },
        }),
      )!;
      expect(t.inputText, 'Сколько продали вчера?');
      expect(t.outputText, 'Вчера — 12 тысяч сом.');
      expect(t.turnComplete, isTrue);
      expect(LiveMessage.parse('{"serverContent":{"interrupted":true}}')!.interrupted, isTrue);
      final r = LiveMessage.parse('{"sessionResumptionUpdate":{"newHandle":"h1","resumable":true},"goAway":{"timeLeft":"10s"}}')!;
      expect(r.resumeHandle, 'h1');
      expect(r.goAway, isTrue);
      // Сервер шлёт JSON и байтами — разбирается так же; не JSON — пропуск.
      expect(LiveMessage.parse(utf8.encode('{"setupComplete":{}}'))!.setupComplete, isTrue);
      expect(LiveMessage.parse('<html>'), isNull);
    });
  });

  group('звонок', () {
    late List<_FakeSocket> sockets;
    late List<(Uri, Map<String, String>)> connects;
    late _FakeAudio audio;

    LiveCall make({Duration max = const Duration(minutes: 10)}) {
      sockets = [];
      connects = [];
      audio = _FakeAudio();
      return LiveCall(
        apiKey: 'test-key',
        instruction: 'ДАННЫЕ МАГАЗИНА: выручка 12 500 сом',
        audio: audio,
        maxDuration: max,
        warnBefore: Duration.zero,
        connect: (uri, headers) async {
          connects.add((uri, headers));
          final s = _FakeSocket();
          sockets.add(s);
          return s;
        },
      );
    }

    test('ключ — заголовком, не в адресе; setup как на ПК; звук ходит в обе стороны; перебивание', () async {
      final call = make();
      final users = <String>[];
      final ai = <String>[];
      final turns = <bool>[];
      call.onUserText = users.add;
      call.onAiText = ai.add;
      call.onTurnDone = turns.add;
      final started = call.start();
      await _settle();
      expect(connects.single.$1.toString(), LiveCall.endpoint);
      expect(connects.single.$1.queryParameters, isEmpty);
      expect(connects.single.$1.toString(), isNot(contains('test-key')));
      expect(connects.single.$2['x-goog-api-key'], 'test-key');
      final setup = sockets.single.sent.single['setup'] as Map;
      expect(setup['model'], 'models/${LiveCall.models.first}');
      expect((setup['generationConfig'] as Map)['responseModalities'], ['AUDIO']);
      expect(setup.containsKey('inputAudioTranscription') && setup.containsKey('outputAudioTranscription'), isTrue);
      expect(((setup['systemInstruction'] as Map)['parts'] as List).single['text'], contains('12 500'));

      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
      expect(audio.started, isTrue);
      expect(call.state.value, CallState.listening);

      // Голос с микрофона → realtimeInput audio/pcm;rate=16000.
      audio.ctrl.add(_pcm(0.5));
      await _settle();
      final input = sockets.single.sent.last['realtimeInput'] as Map;
      expect((input['audio'] as Map)['mimeType'], 'audio/pcm;rate=16000');

      // Ответ: звук → динамик, субтитры, конец реплики.
      sockets.single.pushBytes({
        'serverContent': {
          'inputTranscription': {'text': 'Сколько продали?'},
        },
      });
      sockets.single.push(_audioMsg(List.filled(48000, 0)));
      sockets.single.push({
        'serverContent': {
          'outputTranscription': {'text': 'Сегодня 12 500 сом.'},
        },
      });
      await _settle();
      expect(audio.played.length, 1);
      expect(call.state.value, CallState.speaking);
      expect(users, ['Сколько продали?']);
      expect(ai, ['Сегодня 12 500 сом.']);

      // Тихий звук (эхо динамика), пока ИИ говорит, — в Google не уходит; громкий голос — перебивает сразу.
      final before = sockets.single.sent.length;
      audio.ctrl.add(_pcm(0.05));
      await _settle();
      expect(sockets.single.sent.length, before);
      audio.ctrl.add(_pcm(0.9));
      await _settle();
      expect(audio.flushes, 1);
      expect(sockets.single.sent.length, before + 1);
      expect(turns, [true]);
      // Google подтверждает перебивание — динамик ещё раз очищается.
      sockets.single.push({
        'serverContent': {'interrupted': true},
      });
      await _settle();
      expect(audio.flushes, 2);

      call.end();
      await _settle();
      expect(call.state.value, CallState.ended);
      expect(audio.stopped, isTrue);
      expect(sockets.single.closed, isTrue);
    });

    test('модель недоступна (Google закрыл до setupComplete) — следующая модель', () async {
      final call = make();
      final started = call.start();
      await _settle();
      sockets.first.closeCode = 1008;
      sockets.first.closeReason = 'models/gemini-3.8-live is not found for API version v1beta';
      await sockets.first.ctrl.close();
      await _settle();
      expect(sockets.length, 2);
      expect((sockets[1].sent.single['setup'] as Map)['model'], 'models/${LiveCall.models[1]}');
      sockets[1].push({'setupComplete': <String, dynamic>{}});
      await started;
      expect(call.model, LiveCall.models[1]);
      call.end();
    });

    test('ключ не подходит — без перебора моделей, понятная причина', () async {
      final call = make();
      CallEnd? why;
      String? detail;
      call.onEnded = (w, d) {
        why = w;
        detail = d;
      };
      final started = call.start();
      await _settle();
      sockets.first.closeCode = 1007;
      sockets.first.closeReason = 'API key not valid. Please pass a valid API key.';
      await sockets.first.ctrl.close();
      await started;
      expect(sockets.length, 1);
      expect(why, CallEnd.error);
      expect(detail, contains('API key'));
    });

    test('ограничение длительности — звонок завершается сам', () async {
      final call = make(max: const Duration(milliseconds: 80));
      final ended = Completer<CallEnd>();
      call.onEnded = (w, _) => ended.complete(w);
      final started = call.start();
      await _settle();
      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
      expect(await ended.future.timeout(const Duration(seconds: 2)), CallEnd.limit);
      expect(audio.stopped, isTrue);
    });

    test('входящий звонок (фокус звука потерян) — звонок завершается', () async {
      final call = make();
      CallEnd? why;
      call.onEnded = (w, _) => why = w;
      final started = call.start();
      await _settle();
      sockets.single.push({'setupComplete': <String, dynamic>{}});
      await started;
      audio.ctrl.add({'event': 'focusLoss'});
      await _settle();
      expect(why, CallEnd.focus);
    });
  });

  test('вопрос с цифрами — таблица в чат', () {
    expect(looksLikeDataQuestion('Сколько продали вчера?'), isTrue);
    expect(looksLikeDataQuestion('Кечээ канча саттык?'), isTrue);
    expect(looksLikeDataQuestion('Привет, как дела?'), isFalse);
  });
}
